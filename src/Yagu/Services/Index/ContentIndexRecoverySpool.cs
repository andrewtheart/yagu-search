using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// A per-search, <b>disk-backed recovery spool</b> (plan §5.3) — the linchpin that guarantees a
/// worker-served prune can never silently lose a match. Before the search honors a prune decision for a
/// discovered path, that path is appended here; a normal end-of-search B1 reconciliation rescues only the
/// small dirty subset, but on <b>any</b> failure (worker exit / timeout / malformed or late reply / pointer
/// abort / cancellation / uncertain B1) the <b>entire</b> spool is replayed into the content-scan channel, so
/// every provisionally-pruned path is scanned after all — the fail-safe is total.
/// <para>
/// It is <b>disk-backed</b> specifically to keep the host's memory bounded: a whole-drive search can prune
/// ~99% of ~1.8M files, and holding that path set in memory would defeat the plan's bounded-footprint goal.
/// The spool streams paths to a temp file (bounded write buffer) and replays them lazily, so host memory
/// stays flat regardless of how many files are pruned. The host is alive across a worker failure, so it
/// replays its own spool; a host crash abandons the whole search (the user re-runs), and leftover spool
/// files are swept at startup (<see cref="SweepAbandoned"/>), like the result-store temp sweep.
/// </para>
/// </summary>
internal sealed class ContentIndexRecoverySpool : IDisposable
{
    /// <summary>Filename prefix for spool files (used by the startup sweep to find abandoned ones).</summary>
    public const string FilePrefix = "prune-spool-";

    /// <summary>Filename extension for spool files.</summary>
    public const string FileExtension = ".spool";

    private const string LogSource = "ContentIndex";

    // BOM-less UTF-8: a BOM would corrupt the first path line on read-back.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private const int FileBufferBytes = 1 << 20;
    private const int TextBufferChars = 1 << 16;

    private readonly string _filePath;
    private StreamWriter? _writer;
    private long _count;
    private bool _completed;
    private bool _disposed;

    internal Action<string> DeleteFile { get; set; } = File.Delete;

    private ContentIndexRecoverySpool(string filePath, StreamWriter writer)
    {
        _filePath = filePath;
        _writer = writer;
    }

    /// <summary>The full path of the backing spool file (for diagnostics / tests).</summary>
    public string FilePath => _filePath;

    /// <summary>The number of paths appended so far.</summary>
    public long Count => _count;

    /// <summary>
    /// Creates a fresh spool file in <paramref name="spoolDirectory"/> (created if absent). The write handle
    /// is opened with <see cref="FileShare.Read"/> so <see cref="ReplayAll"/> can read the file back while the
    /// spool is still open.
    /// </summary>
    public static ContentIndexRecoverySpool Create(string spoolDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(spoolDirectory);
        Directory.CreateDirectory(spoolDirectory);
        string filePath = Path.Combine(spoolDirectory, FilePrefix + Guid.NewGuid().ToString("N") + FileExtension);
        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read,
            FileBufferBytes, FileOptions.SequentialScan);
        var writer = new StreamWriter(stream, Utf8NoBom, TextBufferChars);
        return new ContentIndexRecoverySpool(filePath, writer);
    }

    /// <summary>
    /// Records a provisionally-pruned normalized path and returns its 0-based ordinal. Writes are buffered
    /// (durability against a host crash is not required — a crash abandons the whole search — but the buffer
    /// keeps the per-path cost negligible on the hot path). A normalized path never contains a newline, so
    /// the newline-delimited format is unambiguous.
    /// </summary>
    public long Append(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        ObjectDisposedException.ThrowIf(_disposed || _writer is null, this);
        _writer.Write(normalizedPath);
        _writer.Write('\n');
        return _count++;
    }

    /// <summary>
    /// Flushes and lazily streams every appended path back (the failure backstop: replay the whole spool into
    /// the content-scan channel so nothing pruned is lost). Safe to call once appends have stopped; the
    /// enumeration reads the file on its own handle. Returns nothing when the spool is empty.
    /// </summary>
    public IEnumerable<string> ReplayAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer?.Flush();
        return ReadLines(_filePath);
    }

    private static IEnumerable<string> ReadLines(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8NoBom);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length != 0)
                yield return line;
        }
    }

    /// <summary>
    /// Marks the search complete and deletes the spool file (the provisional prunes were reconciled — nothing
    /// left to replay). After this the spool must not be appended to.
    /// </summary>
    public void Complete()
    {
        if (_completed)
            return;
        _completed = true;
        CloseWriter();
        DeleteFileSafe();
    }

    /// <summary>Deletes the spool file if it was not <see cref="Complete"/>d (best effort).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseWriter();
        if (!_completed)
            DeleteFileSafe();
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); } catch { /* best effort */ }
        try { _writer?.Dispose(); } catch { /* best effort */ }
        _writer = null;
    }

    private void DeleteFileSafe()
    {
        try { if (File.Exists(_filePath)) DeleteFile(_filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            YaguLog.For(LogSource).LogDebug("could not delete recovery spool '{Path}': {Error}", _filePath, ex.Message);
        }
    }

    /// <summary>
    /// Deletes recovery-spool files in <paramref name="spoolDirectory"/> older than <paramref name="maxAge"/>
    /// (abandoned by a crashed host). Best effort — returns the number deleted; a file that is still open
    /// (an in-flight search) stays locked and is skipped. Call at startup, like the result-store temp sweep.
    /// </summary>
    public static int SweepAbandoned(string spoolDirectory, TimeSpan maxAge)
        => SweepAbandoned(
            spoolDirectory,
            maxAge,
            Directory.GetFiles,
            File.GetLastWriteTimeUtc,
            File.Delete);

    internal static int SweepAbandoned(
        string spoolDirectory,
        TimeSpan maxAge,
        Func<string, string, string[]> getFiles,
        Func<string, DateTime> getLastWriteTimeUtc,
        Action<string> deleteFile)
    {
        if (string.IsNullOrEmpty(spoolDirectory) || !Directory.Exists(spoolDirectory))
            return 0;

        DateTime cutoffUtc = DateTime.UtcNow - maxAge;
        int deleted = 0;
        string[] files;
        try { files = getFiles(spoolDirectory, FilePrefix + "*" + FileExtension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string file in files)
        {
            try
            {
                if (getLastWriteTimeUtc(file) >= cutoffUtc)
                    continue;
                deleteFile(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked (an active search) or already gone — leave it.
            }
        }

        if (deleted > 0)
            YaguLog.For(LogSource).LogInformation("swept {Count} abandoned recovery spool(s) from '{Dir}'.", deleted, spoolDirectory);
        return deleted;
    }
}
