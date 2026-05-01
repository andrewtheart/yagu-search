using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Searches inside ZIP archives for text content. Supports nested zips
/// (zip files within zip files) up to <see cref="MaxNestingDepth"/> levels.
/// </summary>
/// <remarks>
/// ZIP entries are extracted to temporary streams in memory (or to temp files
/// for very large entries). The archive path separator is '?' — e.g.
/// <c>C:\dir\archive.zip?/folder/readme.txt</c>.
/// For nested archives: <c>outer.zip?/inner.zip?/file.txt</c>.
/// </remarks>
public static class ZipArchiveSearcher
{
    /// <summary>Separator between the archive path and the internal entry path.</summary>
    public const char ArchiveSeparator = '?';

    /// <summary>Maximum nesting depth for nested zip archives.</summary>
    public const int MaxNestingDepth = 5;

    /// <summary>Maximum size of a single ZIP entry that will be searched (64 MB).</summary>
    public const long MaxEntrySize = 64L * 1024 * 1024;

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
    /// Returns the total number of matches found across all entries.
    /// </summary>
    public static async Task<int> SearchArchiveAsync(
        string archivePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        int nestingDepth = 0)
    {
        if (nestingDepth > MaxNestingDepth) return 0;

        int totalMatches = 0;

        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            totalMatches = await SearchArchiveStreamAsync(
                fs, archivePath, regex, literal, literalComparison, options, writer, cancellationToken, nestingDepth).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            LogService.Instance.Verbose("ZipArchiveSearcher", $"Invalid ZIP archive: {archivePath}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Verbose("ZipArchiveSearcher", $"Error searching archive: {archivePath}", ex);
        }

        return totalMatches;
    }

    /// <summary>
    /// Search entries from a ZIP archive stream. Used for both top-level and nested archives.
    /// </summary>
    private static async Task<int> SearchArchiveStreamAsync(
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

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return 0; // Not a valid zip
        }

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip directories
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Skip entries that are too large
                if (entry.Length > MaxEntrySize) continue;

                // Apply glob filtering on the entry name
                if (options.IncludeGlobs.Count > 0 || options.ExcludeGlobs.Count > 0)
                {
                    var globMatcher = new GlobMatcher(options.IncludeGlobs, options.ExcludeGlobs);
                    if (!globMatcher.Matches(entry.FullName))
                        continue;
                }

                // Build the virtual path: archivePath?/entry/path
                string virtualPath = $"{archiveDisplayPath}{ArchiveSeparator}/{entry.FullName}";

                using var entryStream = entry.Open();

                // Read the entry into a MemoryStream so we can peek and potentially re-read
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;

                // Check if this entry is itself a ZIP archive (nested)
                if (nestingDepth < MaxNestingDepth && ms.Length >= 4)
                {
                    long savedPos = ms.Position;
                    bool isNestedZip = IsZipByHeader(ms);
                    ms.Position = savedPos;

                    if (isNestedZip)
                    {
                        // Recurse into the nested archive
                        string nestedArchivePath = $"{archiveDisplayPath}{ArchiveSeparator}/{entry.FullName}";
                        int nestedMatches = await SearchArchiveStreamAsync(
                            ms, nestedArchivePath, regex, literal, literalComparison,
                            options, writer, cancellationToken, nestingDepth + 1).ConfigureAwait(false);
                        totalMatches += nestedMatches;
                        continue;
                    }
                }

                ms.Position = 0;

                // Binary detection — skip binary entries
                if (options.SkipBinary)
                {
                    long savedPos = ms.Position;
                    if (BinaryDetector.IsBinary(ms))
                    {
                        continue;
                    }
                    ms.Position = savedPos;
                }

                // Extension-based skip
                if (options.SkipExtensions.Count > 0)
                {
                    var ext = Path.GetExtension(entry.FullName);
                    if (ext.Length > 1 && options.SkipExtensions.Contains(ext.AsSpan(1).ToString()))
                        continue;
                }

                // Detect encoding and search
                ms.Position = 0;
                var encoding = EncodingDetector.DetectEncoding(ms);
                ms.Position = 0;
                using var reader = new StreamReader(ms, encoding, detectEncodingFromByteOrderMarks: true);

                int matches = await SearchEntryLinesAsync(
                    virtualPath, reader, regex, literal, literalComparison, options, writer, cancellationToken).ConfigureAwait(false);
                totalMatches += matches;
            }
        }

        return totalMatches;
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

        Stream currentStream = new FileStream(segments[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            // Walk through each nested archive level
            for (int i = 1; i < segments.Count; i++)
            {
                var archive = new ZipArchive(currentStream, ZipArchiveMode.Read, leaveOpen: false);
                var entry = archive.GetEntry(segments[i]);
                if (entry is null)
                {
                    archive.Dispose();
                    throw new FileNotFoundException($"Entry '{segments[i]}' not found in archive.");
                }

                var entryStream = entry.Open();
                var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                entryStream.Dispose();

                // If not the last segment, this is an intermediate archive — use the ms as currentStream.
                // If last segment, we write to a temp file.
                if (i < segments.Count - 1)
                {
                    archive.Dispose();
                    currentStream = ms;
                }
                else
                {
                    // Last segment: write to temp file
                    string tempDir = Path.Combine(Path.GetTempPath(), "Yagu", "ZipPreview");
                    Directory.CreateDirectory(tempDir);
                    string ext = Path.GetExtension(segments[i]);
                    string tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
                    await using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                    await ms.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                    archive.Dispose();
                    ms.Dispose();
                    return tempFile;
                }
            }
        }
        finally
        {
            // currentStream is disposed by the ZipArchive, but guard just in case
            if (currentStream is MemoryStream)
                currentStream.Dispose();
        }

        throw new InvalidOperationException("Failed to extract archive entry.");
    }

    /// <summary>
    /// Extract a specific entry from a (possibly nested) zip archive into a
    /// <see cref="MemoryStream"/>. Returns the stream positioned at 0.
    /// </summary>
    public static async Task<MemoryStream> ExtractToMemoryAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var segments = SplitAllSegments(archivePath);
        if (segments.Count < 2) throw new ArgumentException("Not an archive path", nameof(archivePath));

        Stream currentStream = new FileStream(segments[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            for (int i = 1; i < segments.Count; i++)
            {
                var archive = new ZipArchive(currentStream, ZipArchiveMode.Read, leaveOpen: false);
                var entry = archive.GetEntry(segments[i]);
                if (entry is null)
                {
                    archive.Dispose();
                    throw new FileNotFoundException($"Entry '{segments[i]}' not found in archive.");
                }

                var entryStream = entry.Open();
                var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                entryStream.Dispose();

                if (i < segments.Count - 1)
                {
                    archive.Dispose();
                    currentStream = ms;
                }
                else
                {
                    archive.Dispose();
                    return ms;
                }
            }
        }
        finally
        {
            if (currentStream is MemoryStream memStream && memStream.CanRead)
                memStream.Dispose();
        }

        throw new InvalidOperationException("Failed to extract archive entry.");
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
