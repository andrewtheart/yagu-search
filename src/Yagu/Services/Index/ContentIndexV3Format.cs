using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The <b>format-v3 query structures</b> (plan §5.1, Stage 1) — the query-ready, memory-map-friendly on-disk
/// representation the out-of-process query worker will map (rather than deserialize) so the main process
/// holds no index bytes. It is <b>additive</b>: these sidecar files are produced beside the existing
/// generation artifacts and never change the current (document-oriented) format or query path. This managed
/// implementation is the <b>reference reader</b> the plan requires for managed↔Rust parity; the Rust engine
/// reads the identical bytes.
///
/// <para>Three structures, each in its own block-framed file (see <see cref="ContentIndexV3BlockFile"/>):</para>
/// <list type="bullet">
/// <item><b>Inverted postings</b> (<c>query-postings.v3</c>) — a directory of trigrams (sorted, binary
///   searchable) each pointing at a sorted content-id posting list. Directly evaluable; no reconstruction.</item>
/// <item><b>Exact path index</b> (<c>query-pathindex.v3</c>) — entries sorted by a full path hash, each
///   carrying <c>(aliasId, contentId)</c> plus the path bytes, so membership is a mapped binary search that
///   is <b>collision-verified</b> against the stored bytes (a hash match alone never resolves).</item>
/// <item><b>Content identities + reverse index</b> (<c>query-identities.v3</c>) — a fixed-width
///   contentId→<see cref="UsnFileIdentity"/> forward table and a <c>FILE_ID_128 → contentId</c> reverse
///   table (sorted, binary searchable) for USN replay.</item>
/// </list>
///
/// <para>Every file has 64-bit offsets and <b>block-level integrity</b>: the body is split into fixed
/// blocks each with its own hash, so the reader verifies only the blocks a lookup touches (a torn/corrupt
/// block is detected on access → the caller live-scans). Any read failure returns null/false so the search
/// always falls back safely.</para>
/// </summary>
public static class ContentIndexV3Format
{
    public const string PostingsFile = "query-postings.v3";
    public const string PathIndexFile = "query-pathindex.v3";
    public const string IdentitiesFile = "query-identities.v3";
    public const string TombstonesFile = "query-tombstones.v3";

    /// <summary>Bumped when any structure's byte layout changes (readers reject a mismatched version).</summary>
    public const ushort FormatVersion = 1;

    // Section kinds distinguish the four files so a mismatched/renamed file is rejected on open.
    internal const ushort SectionPostings = 1;
    internal const ushort SectionPathIndex = 2;
    internal const ushort SectionIdentities = 3;
    internal const ushort SectionTombstones = 4;

    private static readonly IReadOnlySet<string> NoTombstones = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Writes the format-v3 query structures for <paramref name="generation"/> into
    /// <paramref name="directory"/> (an empty tombstone index — a full generation/base has no tombstones).
    /// The generation must retain its documents (a build-time generation); a query-mode load has dropped
    /// them and cannot produce v3 structures.
    /// </summary>
    public static void Write(string directory, ContentIndexGeneration generation, CancellationToken cancellationToken = default)
        => Write(directory, generation, NoTombstones, cancellationToken);

    /// <summary>
    /// Writes the format-v3 query structures for a <b>segment</b>: <paramref name="generation"/> is the
    /// segment's added/replaced documents and <paramref name="removedPaths"/> is the segment's tombstone set
    /// (plan §5.1 "parallel tombstone index" — the newest layer that tombstones a path shadows older layers).
    /// </summary>
    public static void Write(string directory, ContentIndexGeneration generation, IReadOnlySet<string> removedPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(removedPaths);
        WriteCore(directory, generation.Documents, generation.Aliases, generation.ContentIdentities, removedPaths, cancellationToken);
    }

    /// <summary>
    /// Writes the format-v3 query structures for a persistence-only build batch (plan §5.5) — the same bytes
    /// as the <see cref="ContentIndexGeneration"/> overload; the batch simply never constructed a posting
    /// index, and v3 builds its own inverted index from the batch's per-document trigram sets. Writes an
    /// empty tombstone index.
    /// </summary>
    internal static void Write(string directory, ContentIndexBuildBatch batch, CancellationToken cancellationToken = default)
        => Write(directory, batch, NoTombstones, cancellationToken);

    /// <summary>Segment overload of the persistence-only batch writer: also writes the segment's tombstone
    /// index from <paramref name="removedPaths"/>.</summary>
    internal static void Write(string directory, ContentIndexBuildBatch batch, IReadOnlySet<string> removedPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(removedPaths);
        WriteCore(directory, batch.Documents, batch.Aliases, batch.ContentIdentities, removedPaths, cancellationToken);
    }

    private static void WriteCore(
        string directory,
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents,
        IReadOnlyDictionary<string, (long AliasId, long ContentId)> aliases,
        IReadOnlyList<UsnFileIdentity?> identities,
        IReadOnlySet<string> tombstones,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(tombstones);
        Directory.CreateDirectory(directory);

        cancellationToken.ThrowIfCancellationRequested();
        ContentIndexV3BlockFile.Write(Path.Combine(directory, PostingsFile), SectionPostings, FormatVersion,
            BuildPostingsBody(documents));

        cancellationToken.ThrowIfCancellationRequested();
        ContentIndexV3BlockFile.Write(Path.Combine(directory, PathIndexFile), SectionPathIndex, FormatVersion,
            BuildPathIndexBody(aliases));

        cancellationToken.ThrowIfCancellationRequested();
        ContentIndexV3BlockFile.Write(Path.Combine(directory, IdentitiesFile), SectionIdentities, FormatVersion,
            BuildIdentitiesBody(identities));

        cancellationToken.ThrowIfCancellationRequested();
        ContentIndexV3BlockFile.Write(Path.Combine(directory, TombstonesFile), SectionTombstones, FormatVersion,
            BuildTombstonesBody(tombstones));
    }

    /// <summary>
    /// Opens and header-validates the query structures in <paramref name="directory"/>. Returns null when any
    /// <b>required</b> file (postings/path index/identities) is missing, has a bad magic/version/section, or
    /// fails its header integrity check — the caller then treats the scope as not query-ready and live-scans
    /// (plan §5.1). The tombstone index is <b>optional</b>: it is present for v3 written by this build and for
    /// segments (carrying a tombstone set), absent for older 3-file v3 (then <see cref="ContentIndexV3Reader.HasTombstoneIndex"/>
    /// is false and <see cref="ContentIndexV3Reader.ContainsTombstone"/> always returns false). A tombstone
    /// file that is present but corrupt/rejected fails the whole open (safe: live-scan).
    /// </summary>
    public static ContentIndexV3Reader? TryOpen(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;
        ContentIndexV3BlockFile? postings = null;
        ContentIndexV3BlockFile? pathIndex = null;
        ContentIndexV3BlockFile? identities = null;
        ContentIndexV3BlockFile? tombstones = null;
        try
        {
            postings = ContentIndexV3BlockFile.Open(Path.Combine(directory, PostingsFile), SectionPostings, FormatVersion);
            pathIndex = ContentIndexV3BlockFile.Open(Path.Combine(directory, PathIndexFile), SectionPathIndex, FormatVersion);
            identities = ContentIndexV3BlockFile.Open(Path.Combine(directory, IdentitiesFile), SectionIdentities, FormatVersion);
            if (postings is null || pathIndex is null || identities is null)
            {
                // Any missing/rejected required structure → dispose the ones that did map and live-scan.
                postings?.Dispose();
                pathIndex?.Dispose();
                identities?.Dispose();
                return null;
            }

            // Optional tombstone index: absent → null (an older 3-file v3); present-but-rejected → fail open.
            string tombstonePath = Path.Combine(directory, TombstonesFile);
            if (File.Exists(tombstonePath))
            {
                tombstones = ContentIndexV3BlockFile.Open(tombstonePath, SectionTombstones, FormatVersion);
                if (tombstones is null)
                {
                    postings.Dispose();
                    pathIndex.Dispose();
                    identities.Dispose();
                    return null;
                }
            }

            return new ContentIndexV3Reader(postings, pathIndex, identities, tombstones);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            postings?.Dispose();
            pathIndex?.Dispose();
            identities?.Dispose();
            tombstones?.Dispose();
            YaguLog.For("ContentIndex").LogWarning(ex, "Opening format-v3 query structures in '{Directory}' failed → not query-ready (live-scan).", directory);
            return null;
        }
    }

    // ───────────────────────── Body builders ─────────────────────────

    // Postings body:
    //   u32 trigramCount T, u32 documentCount N, u32 reserved0, u32 reserved1
    //   directory[T]: { u32 trigramPacked (sorted asc), u32 postingCount, u64 postingByteOffset }  (16 B each)
    //   postings[]:   for each trigram in directory order, postingCount × u32 (sorted content ids)
    // postingByteOffset is relative to the START OF THE BODY.
    internal static byte[] BuildPostingsBody(IReadOnlyList<IReadOnlyCollection<Trigram>> docs)
    {
        // Invert the per-document trigram sets (build-time generations/batches retain them).
        var postings = new SortedDictionary<uint, List<int>>();
        for (int docId = 0; docId < docs.Count; docId++)
        {
            IReadOnlyCollection<Trigram> set = docs[docId];
            if (set is null)
                continue;
            foreach (Trigram t in set)
            {
                uint key = t.Value;
                if (!postings.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    postings[key] = list;
                }
                if (list.Count == 0 || list[^1] != docId) // docs visited ascending → stays sorted
                    list.Add(docId);
            }
        }

        int trigramCount = postings.Count;
        int documentCount = docs.Count;
        const int HeaderBytes = 16;
        const int DirEntryBytes = 16;
        long postingsRegionStart = HeaderBytes + (long)trigramCount * DirEntryBytes;

        long totalPostingInts = 0;
        foreach (List<int> list in postings.Values)
            totalPostingInts += list.Count;
        long bodyLength = postingsRegionStart + totalPostingInts * 4;
        var body = new byte[checked((int)bodyLength)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)trigramCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)documentCount);
        // reserved 8..16 stay zero.

        int dirOffset = HeaderBytes;
        long postingCursor = postingsRegionStart;
        foreach (KeyValuePair<uint, List<int>> kv in postings) // SortedDictionary → ascending trigram
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[dirOffset..], kv.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(dirOffset + 4)..], (uint)kv.Value.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(dirOffset + 8)..], (ulong)postingCursor);
            dirOffset += DirEntryBytes;

            int p = (int)postingCursor;
            foreach (int docId in kv.Value)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[p..], (uint)docId);
                p += 4;
            }
            postingCursor = p;
        }
        return body;
    }

    // Path-index body:
    //   u32 count M, u32 reserved0, u64 stringsByteOffset
    //   entries[M]: { u64 pathHash, i64 aliasId, i64 contentId, u32 pathOffset, u32 pathLength }  (32 B each)
    //   strings[]: concatenated UTF-8 path bytes
    // Entries are sorted by (pathHash, pathBytes) so a lookup binary-searches the hash then collision-verifies.
    internal static byte[] BuildPathIndexBody(IReadOnlyDictionary<string, (long AliasId, long ContentId)> aliases)
    {
        var entries = new List<(ulong Hash, long AliasId, long ContentId, byte[] Path)>(aliases.Count);
        foreach (KeyValuePair<string, (long AliasId, long ContentId)> pair in aliases)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(pair.Key);
            entries.Add((V3Fnv.Hash(pathBytes), pair.Value.AliasId, pair.Value.ContentId, pathBytes));
        }
        entries.Sort((a, b) =>
        {
            int c = a.Hash.CompareTo(b.Hash);
            return c != 0 ? c : a.Path.AsSpan().SequenceCompareTo(b.Path);
        });

        const int HeaderBytes = 16;
        const int EntryBytes = 32;
        long stringsStart = HeaderBytes + (long)entries.Count * EntryBytes;
        long totalStringBytes = 0;
        foreach (var e in entries)
            totalStringBytes += e.Path.Length;

        long bodyLength = stringsStart + totalStringBytes;
        var body = new byte[checked((int)bodyLength)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)stringsStart);

        int entryOffset = HeaderBytes;
        long stringCursor = stringsStart;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[entryOffset..], e.Hash);
            BinaryPrimitives.WriteInt64LittleEndian(span[(entryOffset + 8)..], e.AliasId);
            BinaryPrimitives.WriteInt64LittleEndian(span[(entryOffset + 16)..], e.ContentId);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entryOffset + 24)..], (uint)(stringCursor - stringsStart)); // offset RELATIVE to the strings region
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entryOffset + 28)..], (uint)e.Path.Length);
            entryOffset += EntryBytes;

            e.Path.CopyTo(span[(int)stringCursor..]);
            stringCursor += e.Path.Length;
        }
        return body;
    }

    // Identities body:
    //   u32 contentCount N, u32 reverseCount P, u64 reverseByteOffset
    //   forward[N]: { u64 low, u64 high, u8 present, 7×pad }  (24 B each) — index = contentId
    //   reverse[P]: { u64 low, u64 high, u32 contentId }  (20 B each) — sorted by (low, high)
    internal static byte[] BuildIdentitiesBody(IReadOnlyList<UsnFileIdentity?> identities)
    {
        int n = identities.Count;
        var reverse = new List<(ulong Low, ulong High, int ContentId)>(n);
        for (int contentId = 0; contentId < n; contentId++)
        {
            if (identities[contentId] is { } id)
                reverse.Add((id.Low, id.High, contentId));
        }
        reverse.Sort((a, b) =>
        {
            int c = a.Low.CompareTo(b.Low);
            return c != 0 ? c : a.High.CompareTo(b.High);
        });

        const int HeaderBytes = 16;
        const int ForwardBytes = 24;
        const int ReverseBytes = 20;
        long reverseStart = HeaderBytes + (long)n * ForwardBytes;
        long bodyLength = reverseStart + (long)reverse.Count * ReverseBytes;
        var body = new byte[checked((int)bodyLength)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)reverse.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)reverseStart);

        int fwd = HeaderBytes;
        for (int contentId = 0; contentId < n; contentId++)
        {
            if (identities[contentId] is { } id)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(span[fwd..], id.Low);
                BinaryPrimitives.WriteUInt64LittleEndian(span[(fwd + 8)..], id.High);
                span[fwd + 16] = 1;
            }
            fwd += ForwardBytes;
        }

        int rev = (int)reverseStart;
        foreach (var e in reverse)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[rev..], e.Low);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(rev + 8)..], e.High);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(rev + 16)..], (uint)e.ContentId);
            rev += ReverseBytes;
        }
        return body;
    }

    // Tombstones body:
    //   u32 count M, u32 reserved0, u64 stringsByteOffset
    //   entries[M]: { u64 pathHash, u32 pathOffset, u32 pathLength }  (16 B each)
    //   strings[]: concatenated UTF-8 path bytes
    // Entries are sorted by (pathHash, pathBytes) so a membership check binary-searches the hash then
    // collision-verifies against the stored bytes (a hash match alone never resolves). No alias/content ids —
    // a tombstoned path is authoritatively "removed by this layer" and is always live-scanned.
    internal static byte[] BuildTombstonesBody(IReadOnlySet<string> removed)
    {
        var entries = new List<(ulong Hash, byte[] Path)>(removed.Count);
        foreach (string path in removed)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            entries.Add((V3Fnv.Hash(pathBytes), pathBytes));
        }
        entries.Sort((a, b) =>
        {
            int c = a.Hash.CompareTo(b.Hash);
            return c != 0 ? c : a.Path.AsSpan().SequenceCompareTo(b.Path);
        });

        const int HeaderBytes = 16;
        const int EntryBytes = 16;
        long stringsStart = HeaderBytes + (long)entries.Count * EntryBytes;
        long totalStringBytes = 0;
        foreach (var e in entries)
            totalStringBytes += e.Path.Length;

        long bodyLength = stringsStart + totalStringBytes;
        var body = new byte[checked((int)bodyLength)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)stringsStart);

        int entryOffset = HeaderBytes;
        long stringCursor = stringsStart;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[entryOffset..], e.Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entryOffset + 8)..], (uint)(stringCursor - stringsStart)); // offset RELATIVE to the strings region
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entryOffset + 12)..], (uint)e.Path.Length);
            entryOffset += EntryBytes;

            e.Path.CopyTo(span[(int)stringCursor..]);
            stringCursor += e.Path.Length;
        }
        return body;
    }
}

/// <summary>FNV-1a (64-bit) — a fast, dependency-free checksum used for format-v3 block integrity and path
/// hashing. It is a corruption detector (torn/truncated blocks), not a tamper defense; the manifest
/// structural verdict and pointer-slot signature provide trust.</summary>
internal static class V3Fnv
{
    private const ulong Offset = 14695981039346656037UL; // FNV-1a-64 offset basis (0xcbf29ce484222325)
    private const ulong Prime = 1099511628211UL;         // FNV-1a-64 prime (0x100000001b3)

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong h = Offset;
        for (int i = 0; i < data.Length; i++)
        {
            h ^= data[i];
            h *= Prime;
        }
        return h;
    }
}

/// <summary>
/// A block-framed, integrity-checked file for the format-v3 query structures. Layout:
/// <c>[ u32 magic | u16 sectionKind | u16 formatVersion | u32 blockSize | u32 blockCount | u64 bodyLength |
/// blockCount × u64 blockHash | u64 headerHash ] [ body ]</c>. The header hash covers everything before it;
/// each block hash covers one <see cref="BlockSize"/>-byte slice of the body. The reader <b>memory-maps</b>
/// the file (it does not load it into a byte[]), verifies the header on open, and verifies each body block
/// lazily on first touch — so resident memory tracks only the pages a query actually reads, not the index
/// size (plan §2.4/§5.8). A corrupt/torn block is caught when it is read. (x86 windowed views for indexes
/// larger than the 32-bit address space are a later slice; this maps the whole file, fine on x64.)
/// </summary>
internal sealed unsafe class ContentIndexV3BlockFile : IDisposable
{
    public const int BlockSize = 64 * 1024;
    private const uint FileMagic = 0x33_56_51_59; // 'Y','Q','V','3' packed (LE-independent constant)
    private const int WriteChunkBytes = 4 * 1024 * 1024; // stream the body to disk in bounded chunks

    /// <summary>
    /// Test-only: force the x86 <b>windowed-view</b> path (bounded per-access mappings) even in a 64-bit
    /// process, so its parity with the whole-file zero-copy path can be validated on an x64 dev box.
    /// </summary>
    internal static bool ForceWindowedViewsForTests;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;     // null on the windowed path
    private SafeMemoryMappedViewHandle? _handle; // null on the windowed path
    private byte* _base;                 // whole-file mapping base (null on the windowed path); valid until Dispose
    private readonly bool _windowed;
    private readonly ulong[] _blockHashes;
    private readonly int _bodyStart;
    private readonly int _blockCount;
    private readonly bool[] _blockVerified;
    private readonly object _verificationLock = new();
    private bool _disposed;

    public ushort SectionKind { get; }
    public ushort FormatVersion { get; }
    public long BodyLength { get; }

    private ContentIndexV3BlockFile(
        MemoryMappedFile mmf, bool windowed, MemoryMappedViewAccessor? view, SafeMemoryMappedViewHandle? handle,
        byte* basePtr, ulong[] blockHashes, int bodyStart, long bodyLength, int blockCount, ushort sectionKind, ushort formatVersion)
    {
        _mmf = mmf;
        _windowed = windowed;
        _view = view;
        _handle = handle;
        _base = basePtr;
        _blockHashes = blockHashes;
        _bodyStart = bodyStart;
        BodyLength = bodyLength;
        _blockCount = blockCount;
        _blockVerified = new bool[blockCount];
        SectionKind = sectionKind;
        FormatVersion = formatVersion;
    }

    public static void Write(string path, ushort sectionKind, ushort formatVersion, byte[] body)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(body);

        int blockCount = body.Length == 0 ? 0 : (body.Length + BlockSize - 1) / BlockSize;
        int headerBytes = 4 + 2 + 2 + 4 + 4 + 8 + blockCount * 8 + 8;

        // Build ONLY the (small) header in memory — magic/section/version/blockSize/blockCount/bodyLength,
        // the per-block body hashes, then the header hash — then STREAM the header and the body straight to
        // disk. This avoids the previous second full [header|body] copy of a potentially large body (which
        // doubled write-time peak memory); the on-disk bytes are byte-for-byte identical either way.
        var header = new byte[headerBytes];
        Span<byte> span = header;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], FileMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], sectionKind);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], formatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)blockCount);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)body.Length);

        int hashTable = 24;
        for (int b = 0; b < blockCount; b++)
        {
            int start = b * BlockSize;
            int len = Math.Min(BlockSize, body.Length - start);
            ulong h = V3Fnv.Hash(body.AsSpan(start, len));
            BinaryPrimitives.WriteUInt64LittleEndian(span[(hashTable + b * 8)..], h);
        }

        int headerHashOffset = hashTable + blockCount * 8;
        ulong headerHash = V3Fnv.Hash(span[0..headerHashOffset]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[headerHashOffset..], headerHash);

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(header, 0, header.Length);
            IndexMutationFaults.Hit(IndexMutationFaults.V3HeaderWritten);
            // Stream the body in bounded chunks so a very large body is never re-buffered whole.
            int offset = 0;
            while (offset < body.Length)
            {
                int chunk = Math.Min(WriteChunkBytes, body.Length - offset);
                fs.Write(body, offset, chunk);
                offset += chunk;
            }
            IndexMutationFaults.Hit(IndexMutationFaults.V3BodyWritten);
            fs.Flush(flushToDisk: true);
        }
        IndexMutationFaults.Hit(IndexMutationFaults.V3FileClosed);
        File.Move(tmp, path, overwrite: true);
        IndexMutationFaults.Hit(IndexMutationFaults.V3Published);
    }

    public static ContentIndexV3BlockFile? Open(string path, ushort expectedSection, ushort expectedVersion)
    {
        if (!File.Exists(path))
            return null;

        // On a 32-bit process the whole-file view cannot fit a >2 GB index in the ~2 GB address space, so map
        // a bounded window per access instead. The test seam forces this path on x64 for parity validation.
        bool windowed = ForceWindowedViewsForTests || !Environment.Is64BitProcess;

        FileStream? fs = null;
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? persistentView = null;
        SafeMemoryMappedViewHandle? persistentHandle = null;
        bool acquired = false;
        byte* basePtr = null;
        try
        {
            // Share Read|Delete so a concurrent rebuild/retention can rename/delete the generation even while
            // it is mapped (Windows defers the actual removal until the mapping closes).
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long fileLength = fs.Length;
            if (fileLength < 32)
            {
                fs.Dispose();
                return Reject(path, "file shorter than the minimum header");
            }

            // capacity 0 → the map spans the whole file; mapping the FILE OBJECT reserves no address space
            // (only views do), so this is safe on x86 even for a file larger than the address space.
            mmf = MemoryMappedFile.CreateFromFile(fs, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            fs = null;

            // Read the fixed 24-byte prefix via a tiny bounded view (never maps the whole file).
            uint magic, blockSize;
            ushort section, version;
            long blockCountRaw, bodyLength;
            using (MemoryMappedViewAccessor prefix = mmf.CreateViewAccessor(0, 24, MemoryMappedFileAccess.Read))
            {
                SafeMemoryMappedViewHandle ph = prefix.SafeMemoryMappedViewHandle;
                byte* p = null;
                ph.AcquirePointer(ref p);
                try
                {
                    p += prefix.PointerOffset;
                    magic = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p, 4));
                    section = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(p + 4, 2));
                    version = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(p + 6, 2));
                    blockSize = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p + 8, 4));
                    blockCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p + 12, 4));
                    bodyLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(p + 16, 8));
                }
                finally { ph.ReleasePointer(); }
            }

            if (magic != FileMagic)
                return Reject(path, "bad magic", mmf);
            if (section != expectedSection)
                return Reject(path, $"section {section} != expected {expectedSection}", mmf);
            if (version != expectedVersion)
                return Reject(path, $"format version {version} != expected {expectedVersion}", mmf);
            if (blockSize != BlockSize)
                return Reject(path, $"block size {blockSize} != expected {BlockSize}", mmf);
            if (bodyLength < 0)
                return Reject(path, "negative body length", mmf);

            long expectedBlocks = bodyLength == 0 ? 0 : (bodyLength + BlockSize - 1) / BlockSize;
            if (blockCountRaw != expectedBlocks)
                return Reject(path, $"block count {blockCountRaw} inconsistent with body length {bodyLength}", mmf);
            int blockCount = (int)blockCountRaw;

            long headerHashOffset = 24 + (long)blockCount * 8;
            long bodyStart = headerHashOffset + 8;
            if (fileLength != bodyStart + bodyLength || bodyStart > int.MaxValue)
                return Reject(path, "file length inconsistent with header", mmf);

            // Verify the header hash and copy the per-block hash table out of a bounded header view, so Body()
            // never needs the header mapped again (on the windowed path there is no persistent mapping at all).
            var blockHashes = new ulong[blockCount];
            bool headerValid;
            using (MemoryMappedViewAccessor header = mmf.CreateViewAccessor(0, bodyStart, MemoryMappedFileAccess.Read))
            {
                SafeMemoryMappedViewHandle hh = header.SafeMemoryMappedViewHandle;
                byte* p = null;
                hh.AcquirePointer(ref p);
                try
                {
                    p += header.PointerOffset;
                    ulong storedHeaderHash = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(p + headerHashOffset, 8));
                    headerValid = V3Fnv.Hash(new ReadOnlySpan<byte>(p, (int)headerHashOffset)) == storedHeaderHash;
                    if (headerValid)
                        for (int b = 0; b < blockCount; b++)
                            blockHashes[b] = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(p + 24 + (long)b * 8, 8));
                }
                finally { hh.ReleasePointer(); }
            }

            if (!headerValid)
                return Reject(path, "header integrity check failed", mmf);

            if (windowed)
            {
                // No persistent whole-file view — Body() maps a bounded window per access (x86-safe).
                return new ContentIndexV3BlockFile(mmf, windowed: true, view: null, handle: null, basePtr: null,
                    blockHashes, (int)bodyStart, bodyLength, blockCount, section, version);
            }

            // 64-bit fast path: one whole-file view + a base pointer for zero-copy Body() reads.
            persistentView = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            persistentHandle = persistentView.SafeMemoryMappedViewHandle;
            persistentHandle.AcquirePointer(ref basePtr);
            acquired = true;
            basePtr += persistentView.PointerOffset;
            return new ContentIndexV3BlockFile(mmf, windowed: false, persistentView, persistentHandle, basePtr,
                blockHashes, (int)bodyStart, bodyLength, blockCount, section, version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (acquired) { try { persistentHandle!.ReleasePointer(); } catch { /* best effort */ } }
            persistentView?.Dispose();
            mmf?.Dispose();
            fs?.Dispose();
            return Reject(path, "mapping/open failed: " + ex.Message);
        }
    }

    private static ContentIndexV3BlockFile? Reject(string path, string reason, MemoryMappedFile mmf)
    {
        mmf.Dispose();
        return Reject(path, reason);
    }

    private static ContentIndexV3BlockFile? Reject(string path, string reason)
    {
        YaguLog.For("ContentIndex").LogWarning("Format-v3 file '{Path}' rejected: {Reason} (→ not query-ready, live-scan).", path, reason);
        return null;
    }

    /// <summary>
    /// Returns a verified read-only span over <c>[offset, offset+length)</c> of the body, checking the
    /// integrity of every block the range touches (once each). On the 64-bit whole-file mapping the span is
    /// directly over the mapped pages (no copy); on the x86 windowed path it maps a bounded window for the
    /// touched blocks and returns a managed copy (so no single mapping exceeds the address space). Throws
    /// <see cref="InvalidDataException"/> on a block integrity failure or an out-of-range request — the caller
    /// treats the structure as corrupt and live-scans. The span is valid until this file is disposed.
    /// </summary>
    public ReadOnlySpan<byte> Body(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || length < 0 || offset + length > BodyLength)
            throw new InvalidDataException($"Format-v3 body range [{offset},{offset + length}) out of bounds (bodyLength {BodyLength}).");
        if (length == 0)
            return ReadOnlySpan<byte>.Empty;

        int firstBlock = (int)(offset / BlockSize);
        int lastBlock = (int)((offset + length - 1) / BlockSize);

        if (!_windowed)
        {
            for (int b = firstBlock; b <= lastBlock; b++)
                VerifyBlock(b);
            return new ReadOnlySpan<byte>(_base + _bodyStart + offset, (int)length);
        }

        // Windowed (x86): map a bounded view over ONLY the block span this request touches, verify each
        // touched block, then copy the requested bytes into a managed array and unmap. The returned span is
        // backed by that array (kept alive by the span's byref), so it stays valid after the view is released.
        long spanStart = (long)firstBlock * BlockSize;
        long spanEnd = Math.Min((long)(lastBlock + 1) * BlockSize, BodyLength);
        using MemoryMappedViewAccessor window = _mmf!.CreateViewAccessor(_bodyStart + spanStart, spanEnd - spanStart, MemoryMappedFileAccess.Read);
        SafeMemoryMappedViewHandle wh = window.SafeMemoryMappedViewHandle;
        byte* p = null;
        wh.AcquirePointer(ref p);
        try
        {
            p += window.PointerOffset;
            lock (_verificationLock)
                for (int b = firstBlock; b <= lastBlock; b++)
                {
                    if (_blockVerified[b])
                        continue;
                    long blockStartInSpan = (long)b * BlockSize - spanStart;
                    int blockLen = (int)Math.Min(BlockSize, BodyLength - (long)b * BlockSize);
                    if (V3Fnv.Hash(new ReadOnlySpan<byte>(p + blockStartInSpan, blockLen)) != _blockHashes[b])
                        throw new InvalidDataException($"Format-v3 body block {b} failed its integrity check.");
                    _blockVerified[b] = true;
                }

            var result = new byte[length];
            new ReadOnlySpan<byte>(p + (offset - spanStart), (int)length).CopyTo(result);
            return result;
        }
        finally { wh.ReleasePointer(); }
    }

    private void VerifyBlock(int block)
    {
        if (_blockVerified[block])
            return;
        lock (_verificationLock)
        {
            if (_blockVerified[block])
                return;
            long start = (long)block * BlockSize;
            int len = (int)Math.Min(BlockSize, BodyLength - start);
            if (V3Fnv.Hash(new ReadOnlySpan<byte>(_base + _bodyStart + start, len)) != _blockHashes[block])
                throw new InvalidDataException($"Format-v3 body block {block} failed its integrity check.");
            _blockVerified[block] = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_base is not null && _handle is not null)
        {
            try { _handle.ReleasePointer(); } catch { /* best effort */ }
        }
        _base = null;
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _handle = null;
    }
}

/// <summary>
/// Reads the three format-v3 query structures (plan §5.1) — the managed reference the Rust worker mirrors.
/// It answers the exact questions the query path asks: evaluate a trigram query to a candidate content-id
/// set, resolve a normalized path to <c>(aliasId, contentId)</c>, and map a <see cref="UsnFileIdentity"/>
/// back to its content id (USN replay). The structures are <b>memory-mapped</b>, so resident memory tracks
/// only the pages a lookup touches, not the index size. Every lookup verifies only the blocks it touches; a
/// corrupt block throws and the caller live-scans. <b>Dispose</b> to release the mappings (and unlock the
/// files for a rebuild/retention delete).
/// </summary>
public sealed class ContentIndexV3Reader : IDisposable
{
    private readonly ContentIndexV3BlockFile _postings;
    private readonly ContentIndexV3BlockFile _pathIndex;
    private readonly ContentIndexV3BlockFile _identities;
    private readonly ContentIndexV3BlockFile? _tombstones;

    internal ContentIndexV3Reader(ContentIndexV3BlockFile postings, ContentIndexV3BlockFile pathIndex, ContentIndexV3BlockFile identities, ContentIndexV3BlockFile? tombstones = null)
    {
        _postings = postings;
        _pathIndex = pathIndex;
        _identities = identities;
        _tombstones = tombstones;
    }

    private const int PostingsHeaderBytes = 16;
    private const int PostingsDirEntryBytes = 16;
    private const int PathHeaderBytes = 16;
    private const int PathEntryBytes = 32;
    private const int IdHeaderBytes = 16;
    private const int IdForwardBytes = 24;
    private const int IdReverseBytes = 20;
    private const int TombstoneHeaderBytes = 16;
    private const int TombstoneEntryBytes = 16;

    /// <summary>Whether this v3 has a tombstone index (present for v3 written by the current build and for
    /// segments; absent for an older 3-file v3, in which case <see cref="ContainsTombstone"/> is always
    /// false). A layered mapped classifier requires every layer to have one before it can prune.</summary>
    public bool HasTombstoneIndex => _tombstones is not null;

    /// <summary>Number of documents (content ids) covered by the postings.</summary>
    public int DocumentCount => (int)BinaryPrimitives.ReadUInt32LittleEndian(_postings.Body(4, 4));

    /// <summary>Number of distinct trigrams with a posting list.</summary>
    public int TrigramCount => (int)BinaryPrimitives.ReadUInt32LittleEndian(_postings.Body(0, 4));

    /// <summary>Number of indexed path aliases.</summary>
    public int PathCount => (int)BinaryPrimitives.ReadUInt32LittleEndian(_pathIndex.Body(0, 4));

    /// <summary>
    /// Evaluates a monotone trigram query into a candidate content-id set — the mapped equivalent of
    /// <see cref="TrigramPostingIndex.EvaluateSet"/> (identical semantics, proven by parity tests).
    /// </summary>
    public IReadOnlySet<int> EvaluateSet(TrigramExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Evaluate(query);
    }

    private HashSet<int> Evaluate(TrigramExpression query)
    {
        switch (query.Kind)
        {
            case TrigramExpression.NodeKind.All:
            {
                int n = DocumentCount;
                var all = new HashSet<int>(n);
                for (int i = 0; i < n; i++)
                    all.Add(i);
                return all;
            }
            case TrigramExpression.NodeKind.None:
                return new HashSet<int>();
            case TrigramExpression.NodeKind.Trigram:
                return PostingSet(query.Trigram.Value);
            case TrigramExpression.NodeKind.And:
            {
                HashSet<int>? acc = null;
                // Intersect rarest-first for efficiency (matches the reference planner ordering intent).
                foreach (TrigramExpression child in OrderByFrequency(query.Children))
                {
                    HashSet<int> childSet = Evaluate(child);
                    if (acc is null) acc = childSet;
                    else acc.IntersectWith(childSet);
                    if (acc.Count == 0) break;
                }
                return acc ?? new HashSet<int>();
            }
            case TrigramExpression.NodeKind.Or:
            {
                var acc = new HashSet<int>();
                foreach (TrigramExpression child in query.Children)
                    acc.UnionWith(Evaluate(child));
                return acc;
            }
            default:
                return new HashSet<int>();
        }
    }

    private IEnumerable<TrigramExpression> OrderByFrequency(IReadOnlyList<TrigramExpression> children)
    {
        // Cheap heuristic: single-trigram children first, ordered by their (mapped) document frequency.
        return children
            .Select(c => (Child: c, Freq: c.Kind == TrigramExpression.NodeKind.Trigram ? PostingCount(c.Trigram.Value) : int.MaxValue))
            .OrderBy(x => x.Freq)
            .Select(x => x.Child);
    }

    private bool TryFindTrigram(uint packed, out long postingOffset, out int postingCount)
    {
        postingOffset = 0;
        postingCount = 0;
        int t = TrigramCount;
        if (t == 0)
            return false;
        int lo = 0, hi = t - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ReadOnlySpan<byte> entry = _postings.Body(PostingsHeaderBytes + (long)mid * PostingsDirEntryBytes, PostingsDirEntryBytes);
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (key == packed)
            {
                postingCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                postingOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                return true;
            }
            if (key < packed) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    private int PostingCount(uint packed) => TryFindTrigram(packed, out _, out int count) ? count : 0;

    private HashSet<int> PostingSet(uint packed)
    {
        if (!TryFindTrigram(packed, out long offset, out int count) || count == 0)
            return new HashSet<int>();
        ReadOnlySpan<byte> region = _postings.Body(offset, (long)count * 4);
        var set = new HashSet<int>(count);
        for (int i = 0; i < count; i++)
            set.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(region[(i * 4)..]));
        return set;
    }

    /// <summary>
    /// Resolves a normalized path to its <c>(aliasId, contentId)</c> via a mapped binary search on the full
    /// path hash, then a <b>collision verify</b> against the stored path bytes (a hash match alone never
    /// resolves — plan §5.1). Returns false when the path is not in the index.
    /// </summary>
    public bool TryLookupPath(string normalizedPath, out long aliasId, out long contentId)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        byte[] target = Encoding.UTF8.GetBytes(normalizedPath);
        return TryLookupPath(target, V3Fnv.Hash(target), out aliasId, out contentId);
    }

    /// <summary>
    /// Allocation-free lookup core for callers that classify the same path across layered structures. The
    /// caller supplies the UTF-8 bytes and hash once; exact stored-byte verification still makes a hash-only
    /// match insufficient (collisions can never produce a false index member/nonmember).
    /// </summary>
    internal bool TryLookupPath(ReadOnlySpan<byte> target, ulong wantHash, out long aliasId, out long contentId)
    {
        aliasId = 0;
        contentId = 0;
        int m = PathCount;
        if (m == 0)
            return false;

        long stringsBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(_pathIndex.Body(8, 8));

        // Binary search for the FIRST entry whose hash == wantHash, then linear-scan equal hashes.
        int lo = 0, hi = m - 1, first = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ulong h = EntryHash(mid);
            if (h < wantHash) lo = mid + 1;
            else if (h > wantHash) hi = mid - 1;
            else { first = mid; hi = mid - 1; }
        }
        if (first < 0)
            return false;

        for (int i = first; i < m && EntryHash(i) == wantHash; i++)
        {
            ReadOnlySpan<byte> entry = _pathIndex.Body(PathHeaderBytes + (long)i * PathEntryBytes, PathEntryBytes);
            uint pathOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
            uint pathLength = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
            ReadOnlySpan<byte> stored = _pathIndex.Body(stringsBase + pathOffset, pathLength);
            if (stored.SequenceEqual(target))
            {
                aliasId = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
                contentId = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns the stored path hash at <paramref name="index"/> for the layered session's compact
    /// newest-owner routing table. The hash is only a routing hint; every lookup still exact-verifies bytes.</summary>
    internal ulong PathHashAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PathCount);
        return EntryHash(index);
    }

    private ulong EntryHash(int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(_pathIndex.Body(PathHeaderBytes + (long)index * PathEntryBytes, 8));

    /// <summary>Returns the durable file identity captured for <paramref name="contentId"/>, or null when
    /// none was captured (that content must live-scan — USN can never dirty it).</summary>
    public UsnFileIdentity? TryGetIdentity(int contentId)
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_identities.Body(0, 4));
        if (contentId < 0 || contentId >= n)
            return null;
        ReadOnlySpan<byte> e = _identities.Body(IdHeaderBytes + (long)contentId * IdForwardBytes, IdForwardBytes);
        if (e[16] == 0)
            return null;
        return new UsnFileIdentity(BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt64LittleEndian(e[8..]));
    }

    /// <summary>Maps a durable file identity back to its content id for USN replay (plan §5.1 reverse index),
    /// or false when no indexed content has that identity.</summary>
    public bool TryReverseIdentity(UsnFileIdentity identity, out int contentId)
    {
        contentId = 0;
        int p = (int)BinaryPrimitives.ReadUInt32LittleEndian(_identities.Body(4, 4));
        if (p == 0)
            return false;
        long reverseBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(_identities.Body(8, 8));

        int lo = 0, hi = p - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ReadOnlySpan<byte> e = _identities.Body(reverseBase + (long)mid * IdReverseBytes, IdReverseBytes);
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(e);
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            int cmp = low.CompareTo(identity.Low);
            if (cmp == 0) cmp = high.CompareTo(identity.High);
            if (cmp == 0)
            {
                contentId = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
                return true;
            }
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="normalizedPath"/> is tombstoned by this layer (a segment recorded it as
    /// deleted/replaced). Resolved via a mapped binary search on the full path hash then a collision verify
    /// against the stored bytes (a hash match alone never resolves). Always false when this v3 has no
    /// tombstone index (<see cref="HasTombstoneIndex"/> is false).
    /// </summary>
    public bool ContainsTombstone(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        byte[] target = Encoding.UTF8.GetBytes(normalizedPath);
        return ContainsTombstone(target, V3Fnv.Hash(target));
    }

    /// <summary>Allocation-free tombstone lookup core paired with <see cref="TryLookupPath(ReadOnlySpan{byte}, ulong, out long, out long)"/>.</summary>
    internal bool ContainsTombstone(ReadOnlySpan<byte> target, ulong wantHash)
    {
        if (_tombstones is null)
            return false;

        int m = TombstoneCount;
        if (m == 0)
            return false;

        long stringsBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(_tombstones.Body(8, 8));

        int lo = 0, hi = m - 1, first = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ulong h = TombstoneEntryHash(mid);
            if (h < wantHash) lo = mid + 1;
            else if (h > wantHash) hi = mid - 1;
            else { first = mid; hi = mid - 1; }
        }
        if (first < 0)
            return false;

        for (int i = first; i < m && TombstoneEntryHash(i) == wantHash; i++)
        {
            ReadOnlySpan<byte> entry = _tombstones.Body(TombstoneHeaderBytes + (long)i * TombstoneEntryBytes, TombstoneEntryBytes);
            uint pathOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            uint pathLength = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            ReadOnlySpan<byte> stored = _tombstones.Body(stringsBase + pathOffset, pathLength);
            if (stored.SequenceEqual(target))
                return true;
        }
        return false;
    }

    /// <summary>Number of tombstoned path aliases in this layer.</summary>
    internal int TombstoneCount => _tombstones is null
        ? 0
        : (int)BinaryPrimitives.ReadUInt32LittleEndian(_tombstones.Body(0, 4));

    /// <summary>Returns a stored tombstone hash for newest-owner routing. Exact lookup verification remains
    /// mandatory, so collisions only degrade to a live scan.</summary>
    internal ulong TombstoneHashAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, TombstoneCount);
        return TombstoneEntryHash(index);
    }

    private ulong TombstoneEntryHash(int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(_tombstones!.Body(TombstoneHeaderBytes + (long)index * TombstoneEntryBytes, 8));

    /// <summary>Releases the memory mappings (unlocking the files for a rebuild/retention delete).</summary>
    public void Dispose()
    {
        _postings.Dispose();
        _pathIndex.Dispose();
        _identities.Dispose();
        _tombstones?.Dispose();
    }
}
