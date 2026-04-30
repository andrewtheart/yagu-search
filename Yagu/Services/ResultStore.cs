using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace Yagu.Services;

/// <summary>
/// Disk-backed store for evicted <see cref="Models.SearchResult"/> payloads.
/// Full match-line and context data are appended to a temp file; each write
/// returns a byte offset that can later be used to hydrate the result.
/// Thread-safe via locking on the underlying stream.
/// </summary>
public sealed class ResultStore : IDisposable
{
    private const string TempFileSearchPattern = "yagu-results-*.tmp";

    private readonly string _path;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;
    private int _evictedCount;

    public int EvictedCount => Volatile.Read(ref _evictedCount);
    public string TempFilePath => _path;

    public ResultStore()
    {
        _path = Path.Combine(Path.GetTempPath(), $"yagu-results-{Guid.NewGuid():N}.tmp");
        _stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public static Task CleanupOrphanedTempFilesAsync()
    {
        var scheduledAtUtc = DateTime.UtcNow;
        return Task.Run(() =>
        {
            int deleted = DeleteOrphanedTempFiles(Path.GetTempPath(), scheduledAtUtc);
            if (deleted > 0)
                LogService.Instance.Info("ResultStore", $"Deleted {deleted:N0} orphaned Yagu temp result file(s)");
        });
    }

    internal static int DeleteOrphanedTempFiles(string tempDirectory, DateTime deleteFilesLastWrittenAtOrBeforeUtc)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(tempDirectory)) return 0;

            foreach (var path in Directory.EnumerateFiles(tempDirectory, TempFileSearchPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || file.LastWriteTimeUtc > deleteFilesLastWrittenAtOrBeforeUtc)
                        continue;

                    file.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Verbose("ResultStore", $"Could not delete orphaned temp result file '{path}'", ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("ResultStore", $"Could not enumerate orphaned Yagu temp files in '{tempDirectory}'", ex);
        }

        return deleted;
    }

    /// <summary>
    /// Writes result payload to disk and returns the byte offset for later retrieval.
    /// </summary>
    public long Write(string matchLine, IReadOnlyList<string> contextBefore, IReadOnlyList<string> contextAfter)
    {
        lock (_lock)
        {
            long offset = _stream.Position;
            _writer.Write(matchLine ?? string.Empty);
            WriteStringList(_writer, contextBefore);
            WriteStringList(_writer, contextAfter);
            _writer.Flush();
            Interlocked.Increment(ref _evictedCount);
            return offset;
        }
    }

    /// <summary>
    /// Batched write: invokes <paramref name="action"/> with a per-item writer that
    /// returns the offset to record on each result. Holds the lock once and flushes
    /// once at the end. Use this during bulk eviction to avoid one Flush() syscall
    /// per result (was a major UI-thread stall on 50k+ result evictions).
    /// </summary>
    public void WriteBatch(Action<Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>> action)
    {
        lock (_lock)
        {
            long WriteOne(string matchLine, IReadOnlyList<string> before, IReadOnlyList<string> after)
            {
                long offset = _stream.Position;
                _writer.Write(matchLine ?? string.Empty);
                WriteStringList(_writer, before);
                WriteStringList(_writer, after);
                Interlocked.Increment(ref _evictedCount);
                return offset;
            }
            action(WriteOne);
            _writer.Flush();
        }
    }

    /// <summary>
    /// Reads result payload from disk at the given offset.
    /// </summary>
    public (string MatchLine, IReadOnlyList<string> ContextBefore, IReadOnlyList<string> ContextAfter) Read(long offset)
    {
        lock (_lock)
        {
            _stream.Position = offset;
            using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
            var matchLine = reader.ReadString();
            var before = ReadStringList(reader);
            var after = ReadStringList(reader);
            return (matchLine, before, after);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        _stream.Dispose();
        try { File.Delete(_path); } catch { /* best-effort cleanup */ }
    }

    private static void WriteStringList(BinaryWriter w, IReadOnlyList<string> list)
    {
        w.Write(list.Count);
        for (int i = 0; i < list.Count; i++)
            w.Write(list[i] ?? string.Empty);
    }

    private static string[] ReadStringList(BinaryReader r)
    {
        int count = r.ReadInt32();
        var arr = new string[count];
        for (int i = 0; i < count; i++)
            arr[i] = r.ReadString();
        return arr;
    }
}
