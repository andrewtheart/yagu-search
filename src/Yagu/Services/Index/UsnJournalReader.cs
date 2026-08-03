using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// A file identity from a USN record (plan §3.5/§3.6). V3 records carry a 128-bit <c>FILE_ID_128</c>;
/// V2 records carry a 64-bit file reference number, which is zero-extended into <see cref="High"/>. Two
/// hard links to one content object share this identity. Value-equatable for use as a dirty-set key.
/// </summary>
public readonly record struct UsnFileIdentity(ulong Low, ulong High)
{
    /// <summary>A V2 64-bit file reference number (zero-extended).</summary>
    public static UsnFileIdentity FromFileReferenceNumber(ulong frn) => new(frn, 0UL);

    /// <summary>A V3 128-bit FILE_ID_128 read little-endian from a 16-byte span.</summary>
    public static UsnFileIdentity FromFileId128(ReadOnlySpan<byte> id16)
    {
        if (id16.Length < 16)
            throw new ArgumentException("FILE_ID_128 requires 16 bytes.", nameof(id16));
        return new UsnFileIdentity(
            BinaryPrimitives.ReadUInt64LittleEndian(id16),
            BinaryPrimitives.ReadUInt64LittleEndian(id16[8..]));
    }
}

/// <summary>A single parsed USN change: the changed file identity and the OR of reason flags.</summary>
public readonly record struct UsnChange(UsnFileIdentity Identity, uint Reason);

/// <summary>The volume journal identity/cursors from <c>FSCTL_QUERY_USN_JOURNAL</c> (USN_JOURNAL_DATA_V0).</summary>
public readonly record struct UsnJournalInfo(ulong UsnJournalId, long FirstUsn, long NextUsn, long LowestValidUsn);

/// <summary>Outcome of reading the journal (plan §3.5). Any non-<see cref="Ok"/> status is untrusted and
/// forces the caller onto the live-scan path.</summary>
public enum UsnReadStatus
{
    /// <summary>Read succeeded; <see cref="UsnReadResult.Changes"/> and the advanced cursor are valid.</summary>
    Ok,

    /// <summary>The volume is not NTFS, the journal is inactive/disabled, or access was denied.</summary>
    Unavailable,

    /// <summary>The journal was deleted and recreated since the checkpoint (continuity lost).</summary>
    JournalIdChanged,

    /// <summary>The requested start USN was purged (wrap/gap) — records we needed are gone.</summary>
    GapDetected,

    /// <summary>The saved checkpoint is newer than the journal's current cursor. This can happen when
    /// ReFS recreates/resets a journal while retaining its journal id; continuity is impossible.</summary>
    CheckpointAhead,

    /// <summary>A record with an unsupported major version was seen; fail closed (plan §3.5).</summary>
    UnknownRecordVersion,

    /// <summary>An I/O or malformed-buffer error occurred.</summary>
    Error,

    /// <summary>A volume open/query/replay exceeded the configured synchronous-I/O deadline.</summary>
    IoTimeout,

    /// <summary>A legacy index layer persisted a FILE_ID_128 that cannot be matched to this volume's V2
    /// journal reference number. Pruning must fail closed until maintenance advances past the changes.</summary>
    IdentityMismatch,

    /// <summary>The indexed path now resolves to a different mounted volume.</summary>
    VolumeMismatch,

    /// <summary>The read stopped at the record/iteration catch-up limit before the cursor reached the
    /// target USN, so the change delta is <b>incomplete</b> — changes beyond the limit were never read.
    /// Fail closed (plan §3.5): the caller must live-scan, because a file dirtied beyond the limit would
    /// otherwise be classified clean and pruned, silently hiding a match.</summary>
    Incomplete,
}

/// <summary>The result of a journal read: a status, the advanced checkpoint to persist, and the changes.</summary>
public sealed record UsnReadResult(UsnReadStatus Status, UsnCheckpoint NextCheckpoint, IReadOnlyList<UsnChange> Changes)
{
    public bool IsTrusted => Status == UsnReadStatus.Ok;

    public static UsnReadResult Unavailable { get; } =
        new(UsnReadStatus.Unavailable, UsnCheckpoint.None, Array.Empty<UsnChange>());
}

/// <summary>Status of parsing a raw USN record buffer.</summary>
public enum UsnParseStatus
{
    Ok,
    UnknownVersion,
    Malformed,
}

/// <summary>
/// Pure parser for a raw <c>USN_RECORD</c> buffer (the bytes after the leading 8-byte next-USN that
/// <c>FSCTL_READ_*_USN_JOURNAL</c> returns). Walks packed V2/V3 records by <c>RecordLength</c>, extracts
/// the file identity + reason from each, and <b>fails closed</b> on an unknown major version or a
/// malformed length (plan §3.5). This is the unit-testable core of the reader; the P/Invoke plumbing in
/// <see cref="UsnJournalReader"/> is exercised by an integration test against the real journal.
/// </summary>
public static class UsnRecordParser
{
    // Field offsets within a USN_RECORD_V2 / _V3 (packed, little-endian).
    private const int RecordLengthOffset = 0;   // DWORD
    private const int MajorVersionOffset = 4;    // WORD
    private const int V2FileRefOffset = 8;       // DWORDLONG (8)
    private const int V2ReasonOffset = 40;       // DWORD
    private const int V2MinLength = 44;
    private const int V3FileIdOffset = 8;        // FILE_ID_128 (16)
    private const int V3ReasonOffset = 56;       // DWORD
    private const int V3MinLength = 60;

    /// <summary>
    /// Parses packed records from <paramref name="records"/> into <paramref name="sink"/>, stopping at a
    /// zero-length record (end marker), when <paramref name="maxRecords"/> is reached, or at the buffer
    /// end. Returns <see cref="UsnParseStatus.UnknownVersion"/> / <see cref="UsnParseStatus.Malformed"/>
    /// without adding a partial/ambiguous record.
    /// </summary>
    public static UsnParseStatus ParseRecords(ReadOnlySpan<byte> records, ICollection<UsnChange> sink, int maxRecords = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int offset = 0;
        int count = 0;
        while (offset + 8 <= records.Length)
        {
            uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(records[(offset + RecordLengthOffset)..]);
            if (recordLength == 0)
                break; // end marker
            if (recordLength < 8 || offset + (long)recordLength > records.Length)
                return UsnParseStatus.Malformed;

            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + MajorVersionOffset)..]);
            UsnFileIdentity identity;
            uint reason;
            switch (major)
            {
                case 2:
                    if (recordLength < V2MinLength)
                        return UsnParseStatus.Malformed;
                    identity = UsnFileIdentity.FromFileReferenceNumber(
                        BinaryPrimitives.ReadUInt64LittleEndian(records[(offset + V2FileRefOffset)..]));
                    reason = BinaryPrimitives.ReadUInt32LittleEndian(records[(offset + V2ReasonOffset)..]);
                    break;
                case 3:
                    if (recordLength < V3MinLength)
                        return UsnParseStatus.Malformed;
                    identity = UsnFileIdentity.FromFileId128(records.Slice(offset + V3FileIdOffset, 16));
                    reason = BinaryPrimitives.ReadUInt32LittleEndian(records[(offset + V3ReasonOffset)..]);
                    break;
                default:
                    return UsnParseStatus.UnknownVersion; // fail closed on V4+/unknown
            }

            sink.Add(new UsnChange(identity, reason));
            if (++count >= maxRecords)
                break;
            offset += (int)recordLength;
        }

        return UsnParseStatus.Ok;
    }
}

/// <summary>
/// Reads the NTFS USN change journal <b>without elevation</b> to prove freshness for a local volume
/// (plan §3.5, Phase 0 feasibility gate). It opens the volume's root directory with backup semantics,
/// issues <c>FSCTL_QUERY_USN_JOURNAL</c> for the journal identity/cursors, and replays a half-open
/// <c>[start, next)</c> interval with <c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c> (V2/V3 records). It
/// detects journal-id change and wrap/gap and, on any uncertainty, returns a non-Ok status so the caller
/// live-scans. It never elevates and never trusts <c>(length, mtime)</c>. Not yet wired into the search
/// hot path — this lands the reader + feasibility proof; consumption is a later step.
/// </summary>
public static class UsnJournalReader
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM, function, method, FILE_ANY_ACCESS)
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;              // fn 61, METHOD_BUFFERED
    private const uint FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903AB;  // fn 234, METHOD_NEITHER

    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    /// <summary>All reason flags — any change dirties the file (conservative: over-dirtying only costs a
    /// live scan, a false negative is impossible).</summary>
    public const uint AllReasons = 0xFFFFFFFF;

    private const int DefaultReadBufferBytes = 64 * 1024;

    /// <summary>
    /// Opens the volume root directory of <paramref name="path"/> (e.g. <c>C:\</c>) for journal FSCTLs.
    /// Returns null when the root cannot be resolved or opened. The handle uses <b>backup semantics</b>
    /// (required to open a directory) and shares read/write/delete so it never blocks other access.
    /// </summary>
    public static SafeFileHandle? TryOpenVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return null;

        try
        {
            var handle = CreateFileW(
                root,
                FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);
            return handle.IsInvalid ? null : handle;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Queries the journal identity/cursors, or null when unavailable (not NTFS, journal off, denied).</summary>
    public static UsnJournalInfo? QueryJournal(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        // USN_JOURNAL_DATA_V0 is 56 bytes; allocate extra so newer V1/V2 layouts still fit (the V0
        // prefix fields are stable across versions).
        var outBuffer = new byte[128];
        if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, null, 0, outBuffer, (uint)outBuffer.Length, out uint bytesReturned, IntPtr.Zero))
            return null;
        if (bytesReturned < 32)
            return null;

        ulong journalId = BinaryPrimitives.ReadUInt64LittleEndian(outBuffer);
        long firstUsn = BinaryPrimitives.ReadInt64LittleEndian(outBuffer.AsSpan(8));
        long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(outBuffer.AsSpan(16));
        long lowestValid = BinaryPrimitives.ReadInt64LittleEndian(outBuffer.AsSpan(24));
        return new UsnJournalInfo(journalId, firstUsn, nextUsn, lowestValid);
    }

    /// <summary>
    /// One-shot snapshot of the current journal identity/cursors for the volume containing
    /// <paramref name="rootPath"/> (opens the volume, queries, and releases the handle), or null when the
    /// journal is unavailable. Convenience for callers — e.g. the refresh scheduler's headroom check — that
    /// only need <see cref="UsnJournalInfo.FirstUsn"/>/<see cref="UsnJournalInfo.NextUsn"/> and do not
    /// replay records.
    /// </summary>
    public static UsnJournalInfo? TryQueryJournalInfo(string rootPath)
    {
        using SafeFileHandle? handle = TryOpenVolumeRoot(rootPath);
        return handle is null ? null : QueryJournal(handle);
    }

    /// <summary>
    /// Captures a checkpoint at the journal's current <c>NextUsn</c> for a build/query barrier (plan §3.5),

    /// or null when the journal is unavailable for that path.
    /// </summary>
    public static UsnCheckpoint? TryCaptureCheckpoint(string path)
    {
        using var handle = TryOpenVolumeRoot(path);
        if (handle is null)
            return null;
        var info = QueryJournal(handle);
        return info is null ? null : new UsnCheckpoint(info.Value.UsnJournalId, info.Value.NextUsn);
    }

    /// <summary>
    /// Collects the changes in <c>[since.NextUsn, currentNextUsn)</c> for the volume covering
    /// <paramref name="path"/>. Verifies journal-id continuity and wrap/gap first; either failure returns
    /// a non-Ok status with the current cursor so the caller rebuilds/live-scans. When
    /// <paramref name="since"/> is <see cref="UsnCheckpoint.None"/> there is nothing to replay, so it just
    /// reports the current cursor.
    /// </summary>
    public static UsnReadResult TryCollectChanges(string path, UsnCheckpoint since, uint reasonMask = AllReasons, int maxRecords = 500_000)
    {
        using var handle = TryOpenVolumeRoot(path);
        if (handle is null)
            return UsnReadResult.Unavailable;
        var info = QueryJournal(handle);
        if (info is null)
            return UsnReadResult.Unavailable;

        var journal = info.Value;
        var currentCheckpoint = new UsnCheckpoint(journal.UsnJournalId, journal.NextUsn);

        if (since.JournalId != 0 && since.JournalId != journal.UsnJournalId)
            return new UsnReadResult(UsnReadStatus.JournalIdChanged, currentCheckpoint, Array.Empty<UsnChange>());

        long start = since.NextUsn;
        if (start == 0)
            return new UsnReadResult(UsnReadStatus.Ok, currentCheckpoint, Array.Empty<UsnChange>());
        if (start < journal.FirstUsn)
            return new UsnReadResult(UsnReadStatus.GapDetected, currentCheckpoint, Array.Empty<UsnChange>());
        // ReFS can recreate/reset its journal while retaining the same journal id. A persisted checkpoint
        // from before that reset is then numerically AHEAD of the new journal. Treating [future, now) as an
        // empty successful interval would trust stale postings and could silently prune a changed file.
        if (start > journal.NextUsn)
            return new UsnReadResult(UsnReadStatus.CheckpointAhead, currentCheckpoint, Array.Empty<UsnChange>());

        return ReadInterval(handle, journal.UsnJournalId, start, journal.NextUsn, reasonMask, maxRecords);
    }

    /// <summary>Timeout-aware journal replay used by production search/index paths. Timeout never returns a
    /// partial trusted delta; it maps to <see cref="UsnReadStatus.IoTimeout"/> so callers live-scan or retain
    /// the previous checkpoint.</summary>
    public static UsnReadResult TryCollectChangesBounded(
        string path,
        UsnCheckpoint since,
        TimeSpan timeout,
        uint reasonMask = AllReasons,
        int maxRecords = 500_000,
        CancellationToken cancellationToken = default)
    {
        using var io = new BoundedSynchronousIo<UsnReadResult>(timeout);
        return io.TryExecute(
            _ => TryCollectChanges(path, since, reasonMask, maxRecords),
            cancellationToken,
            out UsnReadResult? result)
            ? result!
            : new UsnReadResult(UsnReadStatus.IoTimeout, since, Array.Empty<UsnChange>());
    }

    public static UsnJournalInfo? TryQueryJournalInfoBounded(
        string rootPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var io = new BoundedSynchronousIo<UsnJournalInfo?>(timeout);
        return io.TryExecute(
            _ => TryQueryJournalInfo(rootPath),
            cancellationToken,
            out UsnJournalInfo? result)
            ? result
            : null;
    }

    private static UsnReadResult ReadInterval(SafeFileHandle handle, ulong journalId, long start, long endUsn, uint reasonMask, int maxRecords)
    {
        var changes = new List<UsnChange>();
        var outBuffer = new byte[DefaultReadBufferBytes];
        long cursor = start;

        // Bound the loop defensively so a misbehaving cursor can never spin forever.
        int maxIterations = 1 + (maxRecords / 16) + 1024;
        int iteration = 0;
        for (; cursor < endUsn && changes.Count < maxRecords && iteration < maxIterations; iteration++)
        {
            byte[] input = BuildReadInput(cursor, reasonMask, journalId);
            if (!DeviceIoControl(handle, FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, input, (uint)input.Length, outBuffer, (uint)outBuffer.Length, out uint bytesReturned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                var checkpoint = new UsnCheckpoint(journalId, cursor);
                if (err == ERROR_JOURNAL_ENTRY_DELETED)
                {
                    YaguLog.For("ContentIndex").LogDebug("USN read hit a purged entry (gap) at usn {Cursor}; caller will rebuild/live-scan.", cursor);
                    return new UsnReadResult(UsnReadStatus.GapDetected, checkpoint, changes);
                }
                YaguLog.For("ContentIndex").LogWarning("USN journal read I/O error (Win32 {Win32Error}) at usn {Cursor}; caller will rebuild/live-scan.", err, cursor);
                return new UsnReadResult(UsnReadStatus.Error, checkpoint, changes);
            }

            if (bytesReturned <= 8)
                break; // only the next-USN header, no records left

            long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(outBuffer);
            var parseStatus = UsnRecordParser.ParseRecords(
                outBuffer.AsSpan(8, (int)bytesReturned - 8),
                changes,
                maxRecords - changes.Count);
            if (parseStatus == UsnParseStatus.UnknownVersion)
            {
                YaguLog.For("ContentIndex").LogWarning("USN journal returned an unsupported record version at usn {Cursor}; failing closed (caller live-scans).", cursor);
                return new UsnReadResult(UsnReadStatus.UnknownRecordVersion, new UsnCheckpoint(journalId, cursor), changes);
            }
            if (parseStatus == UsnParseStatus.Malformed)
            {
                YaguLog.For("ContentIndex").LogWarning("USN journal returned a malformed record buffer at usn {Cursor}; failing closed (caller live-scans).", cursor);
                return new UsnReadResult(UsnReadStatus.Error, new UsnCheckpoint(journalId, cursor), changes);
            }

            if (nextUsn <= cursor)
                break; // no forward progress
            cursor = nextUsn;
        }

        // If we stopped at the record or iteration cap before the cursor reached the journal's current
        // NextUsn, the [start, endUsn) delta is INCOMPLETE — changes beyond the cap were never read (the
        // per-buffer parser cap can also drop records in the final buffer without advancing the cursor to
        // them, so a record-cap hit is untrustworthy regardless of the cursor). Returning Ok with a partial
        // change set would let a dirtied file be classified clean and pruned — a silently missed match. Fail
        // closed so the caller treats freshness as discontinuous and live-scans (rescans all at B1).
        bool cappedByRecords = changes.Count >= maxRecords;
        bool cappedByIterations = iteration >= maxIterations && cursor < endUsn;
        if (cappedByRecords || cappedByIterations)
        {
            YaguLog.For("ContentIndex").LogWarning(
                "USN read stopped at the catch-up limit (records={RecordCount}, maxRecords={MaxRecords}) at usn {Cursor} before target {EndUsn}; delta incomplete -> caller live-scans.",
                changes.Count, maxRecords, cursor, endUsn);
            return new UsnReadResult(UsnReadStatus.Incomplete, new UsnCheckpoint(journalId, cursor), changes);
        }

        return new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(journalId, cursor), changes);
    }

    // READ_USN_JOURNAL_DATA_V1 (44 bytes): StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout,
    // BytesToWaitFor, UsnJournalID, MinMajorVersion(2), MaxMajorVersion(3). Built by hand to avoid
    // struct-packing pitfalls; the unprivileged read requires the V1 version range.
    private static byte[] BuildReadInput(long startUsn, uint reasonMask, ulong journalId)
    {
        var input = new byte[44];
        var span = input.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], reasonMask);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 0);   // ReturnOnlyOnClose = 0 (see first occurrence)
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 0);   // Timeout = 0 (non-blocking)
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], 0);   // BytesToWaitFor = 0
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], journalId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[40..], 2);   // MinMajorVersion
        BinaryPrimitives.WriteUInt16LittleEndian(span[42..], 3);   // MaxMajorVersion
        return input;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
