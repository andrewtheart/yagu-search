using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services;

// Concurrency gate for archive entry decompression — limits simultaneous
// MemoryStream allocations to prevent memory spikes on large archives.
// Mirrors ContentSearcher.s_nativeGate for regular file scanning.

/// <summary>
/// Searches inside archives for text content. Supports nested ZIP/7z archives
/// (zip files within zip files) up to <see cref="MaxNestingDepth"/> levels.
/// </summary>
/// <remarks>
/// Archive entries are extracted to temporary streams in memory (or to temp files
/// for very large entries). The archive path separator is '?' — e.g.
/// <c>C:\dir\archive.zip?/folder/readme.txt</c>.
/// For nested archives: <c>outer.zip?/inner.zip?/file.txt</c>.
/// </remarks>
public static class ZipArchiveSearcher
{
    /// <summary>Separator between the archive path and the internal entry path.</summary>
    public const char ArchiveSeparator = '?';

    /// <summary>Default maximum nesting depth for nested zip archives.</summary>
    private const int DefaultMaxNestingDepth = 5;

    /// <summary>Default maximum size of a single ZIP entry that will be searched (64 MB).</summary>
    private const long DefaultMaxEntrySize = 64L * 1024 * 1024;

    /// <summary>Maximum nesting depth for nested zip archives. Settable via settings (0 = use default 5).</summary>
    public static int MaxNestingDepth { get; set; } = DefaultMaxNestingDepth;

    /// <summary>Maximum size of a single ZIP entry that will be searched. Settable via settings (0 = use default 64 MB).</summary>
    public static long MaxEntrySize { get; set; } = DefaultMaxEntrySize;

    /// <summary>Apply settings values. Pass 0 for either to use defaults.</summary>
    public static void Configure(int maxNestingDepth, int maxEntryMB)
    {
        MaxNestingDepth = maxNestingDepth > 0 ? maxNestingDepth : DefaultMaxNestingDepth;
        MaxEntrySize = maxEntryMB > 0 ? (long)maxEntryMB * 1024 * 1024 : DefaultMaxEntrySize;
    }

    /// <summary>
    /// Concurrency gate for entry decompression — limits simultaneous MemoryStream
    /// allocations across all archive searches to prevent memory spikes.
    /// </summary>
    private static readonly SemaphoreSlim s_entryGate = new(
        Math.Min(32, Environment.ProcessorCount * 2),
        Math.Min(32, Environment.ProcessorCount * 2));

    /// <summary>
    /// Eagerly loads System.IO.Compression and JITs the hot search path
    /// on a background thread so the first archive hit doesn't pay the
    /// ~20 ms assembly-load + JIT cost.
    /// </summary>
    public static void WarmUp()
    {
        // Touch ZipArchive to force assembly load; the JIT will also
        // compile the static constructor and any referenced methods.
        RuntimeHelpers.RunClassConstructor(typeof(ZipArchive).TypeHandle);
    }

    /// <summary>
    /// Returns true if <paramref name="filePath"/> contains the archive separator,
    /// meaning it refers to a file inside a zip archive.
    /// </summary>
    public static bool IsArchivePath(string filePath) =>
        filePath.IndexOf(ArchiveSeparator) >= 0;

    /// <summary>
    /// Splits an archive path into (outerArchivePath, innerEntryPath).
    /// For nested archives, the first segment is the outermost archive.
    /// </summary>
    public static (string ArchivePath, string EntryPath) SplitArchivePath(string filePath)
    {
        int idx = filePath.IndexOf(ArchiveSeparator);
        if (idx < 0) return (filePath, string.Empty);
        return (filePath[..idx], filePath[(idx + 2)..]); // skip '?/'
    }

    /// <summary>
    /// Splits a potentially nested archive path into all segments.
    /// E.g. "a.zip?/b.zip?/c.txt" → ["a.zip", "b.zip", "c.txt"].
    /// The first element is always the outermost file on disk.
    /// </summary>
    public static List<string> SplitAllSegments(string filePath)
    {
        var segments = new List<string>();
        var remaining = filePath.AsSpan();
        while (true)
        {
            int idx = remaining.IndexOf(ArchiveSeparator);
            if (idx < 0)
            {
                // Last segment — trim leading /
                var last = remaining.TrimStart('/');
                if (last.Length > 0)
                    segments.Add(last.ToString());
                break;
            }
            segments.Add(remaining[..idx].TrimStart('/').ToString());
            remaining = remaining[(idx + 1)..]; // skip '?'
        }
        return segments;
    }

    /// <summary>
    /// Returns true if the file has an extension present in the configured
    /// archive extensions set. This is an I/O-free alternative to
    /// <see cref="IsZipByHeader(string)"/> for use in hot loops where
    /// opening every file is prohibitive.
    /// </summary>
    public static bool HasArchiveExtension(string filePath, IReadOnlySet<string> archiveExtensions)
    {
        var ext = Path.GetExtension(filePath.AsSpan());
        if (ext.Length <= 1) return false;
        var dotted = ext.ToString();
        var bare = dotted[1..];
        return archiveExtensions.Contains(bare) || archiveExtensions.Contains(dotted);
    }

    public static bool HasZipExtension(string filePath, IReadOnlySet<string> archiveExtensions) =>
        HasArchiveExtension(filePath, archiveExtensions);

    /// <summary>
    /// Detects whether a file is a ZIP archive by reading the first 4 bytes.
    /// </summary>
    public static bool IsZipByHeader(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return IsZipByHeader(fs);
        }
        catch { return false; }
    }

    /// <summary>
    /// Detects whether a stream starts with ZIP magic bytes (PK\x03\x04).
    /// The stream position is not reset.
    /// </summary>

    public static bool IsZipByHeader(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = stream.Read(magic[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return read >= 4
            && magic[0] == 0x50 && magic[1] == 0x4B
            && (magic[2] == 0x03 || magic[2] == 0x05 || magic[2] == 0x07);
    }

    /// <summary>
    /// Search all text entries inside a ZIP archive for matches. Writes results to the channel.
    /// Returns the total number of matches and entries scanned across all entries.
    /// </summary>
    public static async Task<(int MatchCount, int EntriesScanned)> SearchArchiveAsync(
        string archivePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        int nestingDepth = 0)
    {
        if (nestingDepth > MaxNestingDepth) return (0, 0);

        (int matchCount, int entriesScanned) = (0, 0);

        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            (matchCount, entriesScanned) = await SearchArchiveStreamAutoAsync(
                fs, archivePath, regex, literal, literalComparison, options, writer, cancellationToken, nestingDepth).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            LogService.Instance.Verbose("ZipArchiveSearcher", $"Invalid archive: {archivePath}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Verbose("ZipArchiveSearcher", $"Error searching archive: {archivePath}", ex);
        }

        return (matchCount, entriesScanned);
    }

    private enum ArchiveKind
    {
        Unknown,
        Zip,
    }

    private static ArchiveKind DetectArchiveKind(Stream archiveStream)
    {
        Span<byte> magic = stackalloc byte[4];
        int read = 0;
        long originalPosition = archiveStream.CanSeek ? archiveStream.Position : 0;

        while (read < magic.Length)
        {
            int n = archiveStream.Read(magic[read..]);
            if (n <= 0) break;
            read += n;
        }

        if (archiveStream.CanSeek)
            archiveStream.Position = originalPosition;

        var sample = magic[..read];
        if (BinaryDetector.IsZipMagic(sample)) return ArchiveKind.Zip;
        return ArchiveKind.Unknown;
    }

    internal static Task<(int MatchCount, int EntriesScanned)> SearchArchiveStreamAutoAsync(
        Stream archiveStream,
        string archiveDisplayPath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        int nestingDepth)
    {
        if (nestingDepth > MaxNestingDepth) return Task.FromResult((0, 0));

        var kind = DetectArchiveKind(archiveStream);
        if (archiveStream.CanSeek)
            archiveStream.Position = 0;

        return kind == ArchiveKind.Zip
            ? SearchArchiveStreamAsync(archiveStream, archiveDisplayPath, regex, literal, literalComparison, options, writer, cancellationToken, nestingDepth)
            : Task.FromResult((0, 0));
    }

    /// <summary>
    /// Search entries from a ZIP archive stream. Used for both top-level and nested archives.
    /// </summary>
    internal static async Task<(int MatchCount, int EntriesScanned)> SearchArchiveStreamAsync(
        Stream archiveStream,
        string archiveDisplayPath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        int nestingDepth)
    {
        int totalMatches = 0;
        int entriesScanned = 0;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return (0, 0); // Not a valid zip
        }

        // Hoist GlobMatcher outside the loop — one allocation per archive instead of per entry.
        GlobMatcher? globMatcher = (options.IncludeGlobs.Count > 0 || options.ExcludeGlobs.Count > 0)
            ? new GlobMatcher(options.IncludeGlobs, options.ExcludeGlobs, options.IncludeFilterMode, options.ExcludeFilterMode)
            : null;

        using (archive)
        {
            // Collect entries that pass zero-cost filters (no decompression needed).
            var candidates = new List<ZipArchiveEntry>();
            foreach (var entry in archive.Entries)
            {
                // Skip directories
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Skip entries that are too large (metadata-only check, no I/O)
                if (entry.Length > MaxEntrySize) continue;

                // Extension-based skip (string check, no I/O)
                if (options.SkipExtensions.Count > 0)
                {
                    var ext = Path.GetExtension(entry.FullName);
                    if (ext.Length > 1 && options.SkipExtensions.Contains(ext.AsSpan(1).ToString()))
                        continue;
                }

                // Glob filtering (string check, no I/O)
                if (globMatcher is not null && !globMatcher.Matches(entry.FullName))
                    continue;

                candidates.Add(entry);
            }

            // Process entries in parallel, bounded by the concurrency gate.
            // ZipArchive itself is not thread-safe for concurrent entry.Open()
            // calls, so we must extract entries sequentially but can search
            // the decompressed content in parallel.
            var tasks = new List<Task<(int MatchCount, int EntriesScanned)>>(candidates.Count);
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Build the virtual path: archivePath?/entry/path
                string virtualPath = $"{archiveDisplayPath}{ArchiveSeparator}/{entry.FullName}";

                // ── Peek phase: read only the first 8 KB to detect binary/zip ──
                using var entryStream = entry.Open();
                var peekBuf = new byte[BinaryDetector.SampleBytes];
                int peekRead = 0;
                while (peekRead < peekBuf.Length)
                {
                    int n = await entryStream.ReadAsync(peekBuf.AsMemory(peekRead), cancellationToken).ConfigureAwait(false);
                    if (n <= 0) break;
                    peekRead += n;
                }

                if (peekRead == 0) continue;

                bool isNestedZip = nestingDepth < MaxNestingDepth && peekRead >= 4
                    && BinaryDetector.IsZipMagic(peekBuf.AsSpan(0, Math.Min(peekRead, 4)));

                // Binary detection on the peek buffer — skip before full decompression.
                if (options.SkipBinary && BinaryDetector.IsBinary(peekBuf.AsSpan(0, peekRead)))
                {
                    // But still check for nested archives (binary archives should be recursed into).
                    if (isNestedZip)
                    {
                        // Need full content for recursive archive search
                    }
                    else
                    {
                        continue;
                    }
                }

                // ── Full load phase (only reached for text entries or nested zips) ──
                await s_entryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                MemoryStream ms;
                try
                {
                    // Pre-allocate from the entry's known uncompressed size when available.
                    ms = entry.Length > 0 ? new MemoryStream((int)Math.Min(entry.Length, MaxEntrySize)) : new MemoryStream();
                    ms.Write(peekBuf, 0, peekRead);
                    await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                    ms.Position = 0;
                }
                catch
                {
                    s_entryGate.Release();
                    throw;
                }

                // Release the decompression gate immediately — the MemoryStream is
                // fully populated and the gate's purpose (limiting concurrent
                // decompression I/O) is satisfied.  Holding it during search
                // was the main throughput bottleneck on large archives.
                s_entryGate.Release();

                entriesScanned++;

                if (isNestedZip)
                {
                    // Launch nested archive search as a task.
                    string nestedArchivePath = virtualPath;
                    tasks.Add(Task.Run(async () =>
                    {
                        using (ms)
                        {
                            return await SearchArchiveStreamAsync(
                                    ms, nestedArchivePath, regex, literal, literalComparison,
                                    options, writer, cancellationToken, nestingDepth + 1).ConfigureAwait(false);
                        }
                    }, cancellationToken));
                    continue;
                }

                // Detect encoding and search — launch as parallel task.
                tasks.Add(Task.Run(async () =>
                {
                    using (ms)
                    {
                        var encoding = EncodingDetector.DetectEncoding(ms);
                        ms.Position = 0;
                        using var reader = new StreamReader(ms, encoding, detectEncodingFromByteOrderMarks: true);

                        int matches = await SearchEntryLinesAsync(
                            virtualPath, reader, regex, literal, literalComparison, options, writer, cancellationToken).ConfigureAwait(false);
                        return (matches, 0); // entries already counted above
                    }
                }, cancellationToken));
            }

            // Await all parallel entry searches.
            if (tasks.Count > 0)
            {
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var (m, e) in results)
                {
                    totalMatches += m;
                    entriesScanned += e; // aggregate nested archive entries
                }
            }
        }

        return (totalMatches, entriesScanned);
    }

    private static string NormalizeArchiveEntryName(string entryName) =>
        entryName.Replace('\\', '/').TrimStart('/');

    private static bool ArchiveEntryNameEquals(string candidate, string expected)
    {
        string normalizedCandidate = NormalizeArchiveEntryName(candidate);
        string normalizedExpected = NormalizeArchiveEntryName(expected);
        return string.Equals(normalizedCandidate, normalizedExpected, StringComparison.Ordinal)
            || string.Equals(normalizedCandidate, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Line-by-line search of a single entry, matching the ContentSearcher pattern.
    /// </summary>
    private static async Task<int> SearchEntryLinesAsync(
        string virtualPath,
        StreamReader reader,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken)
    {
        int contextLines = Math.Max(0, options.ContextLines);
        int perFileCap = options.MaxMatchesPerFile > 0 ? options.MaxMatchesPerFile : int.MaxValue;
        var ring = new Queue<string>();
        var pendingAfter = new Queue<(SearchResult Partial, List<string> AfterLines, int Remaining)>();
        int lineNumber = 0;
        int matchCount = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            // Fill context-after for pending results
            if (pendingAfter.Count > 0)
            {
                int remaining = pendingAfter.Count;
                for (int i = 0; i < remaining; i++)
                {
                    var (partial, afterLines, left) = pendingAfter.Dequeue();
                    afterLines.Add(LineTruncator.Truncate(line));
                    int newLeft = left - 1;
                    if (newLeft <= 0)
                    {
                        await writer.WriteAsync(partial with { ContextAfter = afterLines }, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        pendingAfter.Enqueue((partial, afterLines, newLeft));
                    }
                }
            }

            // Find matches
            var matches = ContentSearcher.FindMatches(line, regex, literal, literalComparison);
            if (matches.Count > 0)
            {
                matchCount += matches.Count;
                var before = ring.Count > 0 ? ring.ToArray() : Array.Empty<string>();
                foreach (var (start, length) in matches)
                {
                    var displayLine = LineTruncator.TruncateAroundMatch(line, start, length);
                    var partial = new SearchResult(
                        FilePath: virtualPath,
                        LineNumber: lineNumber,
                        MatchLine: displayLine.Text,
                        MatchStartColumn: displayLine.MatchStart,
                        MatchLength: length,
                        ContextBefore: before,
                        ContextAfter: Array.Empty<string>());
                    if (contextLines == 0)
                    {
                        await writer.WriteAsync(partial, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        pendingAfter.Enqueue((partial, new List<string>(contextLines), contextLines));
                    }
                }
                if (matchCount >= perFileCap) break;
            }

            // Maintain context ring
            ring.Enqueue(LineTruncator.Truncate(line));
            while (ring.Count > contextLines) ring.Dequeue();
        }

        // Flush pending results without full context-after
        while (pendingAfter.Count > 0)
        {
            var (partial, afterLines, _) = pendingAfter.Dequeue();
            await writer.WriteAsync(partial with { ContextAfter = afterLines }, cancellationToken).ConfigureAwait(false);
        }

        return matchCount;
    }

    /// <summary>
    /// Extract a specific entry from a (possibly nested) zip archive to a temporary file.
    /// Returns the path to the temporary file. Caller is responsible for deleting it.
    /// </summary>

    public static async Task<string> ExtractToTempFileAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var segments = SplitAllSegments(archivePath);
        if (segments.Count < 2) throw new ArgumentException("Not an archive path", nameof(archivePath));

        using var ms = await ExtractToMemoryAsync(archivePath, cancellationToken).ConfigureAwait(false);

        string tempDir = Path.Combine(Path.GetTempPath(), "Yagu", "ZipPreview");
        Directory.CreateDirectory(tempDir);
        string ext = Path.GetExtension(segments[^1]);
        string tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
        await using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
        await ms.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        return tempFile;
    }

    /// <summary>
    /// Extract a specific entry from a (possibly nested) archive into a
    /// <see cref="MemoryStream"/>. Returns the stream positioned at 0.
    /// </summary>

    public static async Task<MemoryStream> ExtractToMemoryAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var segments = SplitAllSegments(archivePath);
        if (segments.Count < 2) throw new ArgumentException("Not an archive path", nameof(archivePath));

        Stream? currentStream = new FileStream(segments[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        try
        {
            for (int i = 1; i < segments.Count; i++)
            {
                var extracted = await ExtractEntryToMemoryAsync(currentStream, segments[i], cancellationToken).ConfigureAwait(false);

                if (i < segments.Count - 1)
                {
                    currentStream.Dispose();
                    currentStream = extracted;
                }
                else
                {
                    currentStream.Dispose();
                    currentStream = null;
                    return extracted;
                }
            }
        }
        finally
        {
            currentStream?.Dispose();
        }

        throw new InvalidOperationException("Failed to extract archive entry.");
    }

    private static async Task<MemoryStream> ExtractEntryToMemoryAsync(Stream archiveStream, string entryPath, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry(entryPath)
            ?? archive.Entries.FirstOrDefault(e => ArchiveEntryNameEquals(e.FullName, entryPath));
        if (entry is null)
            throw new FileNotFoundException($"Entry '{entryPath}' not found in archive.");

        await using var entryStream = entry.Open();
        var ms = entry.Length > 0 && entry.Length <= MaxEntrySize
            ? new MemoryStream((int)entry.Length)
            : new MemoryStream();
        await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Cleans up any temp files created by <see cref="ExtractToTempFileAsync"/>.
    /// Safe to call at app shutdown.
    /// </summary>

    public static void CleanupTempFiles()
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Yagu", "ZipPreview");
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("ZipArchiveSearcher", "Failed to clean temp files", ex);
        }
    }
}
