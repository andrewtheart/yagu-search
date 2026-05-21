using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Disk-backed store for evicted <see cref="Models.SearchResult"/> payloads.
/// Full match-line and context data are appended to a temp file; each write
/// returns a byte offset that can later be used to hydrate the result.
/// Thread-safe via locking on the underlying stream.
/// 
/// A single background drain task owns all eviction writes routed through
/// <see cref="EnqueueEvict"/>. This removes lock contention between bulk
/// eviction (<see cref="Models.SearchResultCollection.EvictAll"/>) and the
/// per-match degraded-mode eviction path that previously each spawned their
/// own Task.Run and fought over the same writer lock (observed multi-second
/// WriteBatch lock-hold spikes on large evictions).
/// </summary>
public sealed class ResultStore : IDisposable
{
    private const string TempFileSearchPattern = "yagu-results-*.tmp";
    private const int DrainBatchCap = 512;

    private readonly string _path;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;
    private int _evictedCount;

    private readonly Channel<SearchResult> _evictionChannel;
    private readonly Task _drainTask;
    private readonly CancellationTokenSource _drainCts = new();
    private readonly object _drainCompletionLock = new();
    private long _enqueuedCount;
    private long _processedCount;

    public int EvictedCount => Volatile.Read(ref _evictedCount);
    public string TempFilePath => _path;

    public ResultStore()
    {
        _path = Path.Combine(Path.GetTempPath(), $"yagu-results-{Guid.NewGuid():N}.tmp");
        _stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);

        _evictionChannel = Channel.CreateUnbounded<SearchResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _drainTask = Task.Run(DrainLoopAsync);
    }

    /// <summary>
    /// Enqueue a result for asynchronous eviction by the single drain task. Returns
    /// true if the item was queued. Already-evicted items are skipped. Callers do
    /// not block — use <see cref="Drain"/> to wait for completion.
    /// </summary>
    public bool EnqueueEvict(SearchResult result)
    {
        if (_disposed || result is null || result.IsEvicted)
            return false;
        Interlocked.Increment(ref _enqueuedCount);
        if (_evictionChannel.Writer.TryWrite(result))
            return true;
        // Failure path (channel closed): re-balance the counter so Drain() can complete.
        Interlocked.Decrement(ref _enqueuedCount);
        return false;
    }

    /// <summary>Enqueue many results for asynchronous eviction.</summary>
    public int EnqueueEvictMany(IReadOnlyList<SearchResult> results)
    {
        if (results is null || results.Count == 0) return 0;
        int queued = 0;
        for (int i = 0; i < results.Count; i++)
            if (EnqueueEvict(results[i])) queued++;
        return queued;
    }

    /// <summary>
    /// Synchronously wait until everything currently enqueued has been written
    /// to disk. Safe to call from any thread (but never from the drain task).
    /// </summary>
    public void Drain()
    {
        long target = Interlocked.Read(ref _enqueuedCount);
        lock (_drainCompletionLock)
        {
            while (!_disposed && Interlocked.Read(ref _processedCount) < target)
                Monitor.Wait(_drainCompletionLock, TimeSpan.FromMilliseconds(250));
        }
    }

    private async Task DrainLoopAsync()
    {
        var batch = new List<SearchResult>(DrainBatchCap);
        var reader = _evictionChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_drainCts.Token).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < DrainBatchCap && reader.TryRead(out var item))
                    batch.Add(item);
                if (batch.Count == 0) continue;

                try
                {
                    WriteBatch(writeOne =>
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            try { batch[i].EvictWith(writeOne); }
                            catch { /* skip and continue draining */ }
                        }
                    });
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("ResultStore", "Eviction drain batch failed", ex);
                }
                finally
                {
                    lock (_drainCompletionLock)
                    {
                        Interlocked.Add(ref _processedCount, batch.Count);
                        Monitor.PulseAll(_drainCompletionLock);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            LogService.Instance.Warning("ResultStore", "Eviction drain loop terminated unexpectedly", ex);
        }
        finally
        {
            // Wake any waiters so they don't hang.
            lock (_drainCompletionLock) { Monitor.PulseAll(_drainCompletionLock); }
        }
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
            WriteStringFast(_writer, matchLine ?? string.Empty);
            WriteStringList(_writer, contextBefore);
            WriteStringList(_writer, contextAfter);
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
                WriteStringFast(_writer, matchLine ?? string.Empty);
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Ensure buffered writes are committed before switching the shared stream to read mode.
            _writer.Flush();
            if (offset < 0 || offset >= _stream.Length)
                throw new InvalidOperationException($"ResultStore offset {offset} is out of range (stream length {_stream.Length}).");

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
        try { _evictionChannel.Writer.TryComplete(); } catch { }
        try { _drainCts.Cancel(); } catch { }
        try { _drainTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        // Wake any pending Drain() callers so they don't hang on a disposed store.
        lock (_drainCompletionLock) { Monitor.PulseAll(_drainCompletionLock); }
        _drainCts.Dispose();
        _writer.Dispose();
        _stream.Dispose();
        try { File.Delete(_path); } catch { /* best-effort cleanup */ }
    }

    private static void WriteStringList(BinaryWriter w, IReadOnlyList<string> list)
    {
        w.Write(list.Count);
        for (int i = 0; i < list.Count; i++)
            WriteStringFast(w, list[i] ?? string.Empty);
    }

    /// <summary>
    /// Write a string using BinaryWriter's length-prefixed UTF-8 format but with a
    /// rented buffer to avoid BinaryWriter's internal 128-byte chunked encoding for
    /// strings longer than ~42 chars.
    /// </summary>
    private static void WriteStringFast(BinaryWriter w, string value)
    {
        if (value.Length == 0)
        {
            w.Write(value); // 1-byte length prefix (0)
            return;
        }

        // For short strings, BinaryWriter.Write is fine (its 128-byte buffer suffices)
        if (value.Length <= 40)
        {
            w.Write(value);
            return;
        }

        // For longer strings: encode into a pooled buffer, write length + bytes directly
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int byteCount = Encoding.UTF8.GetBytes(value, rented);
            w.Write7BitEncodedInt(byteCount);
            w.BaseStream.Write(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
