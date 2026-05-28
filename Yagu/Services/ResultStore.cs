using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Yagu.Helpers;
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
    private const int EvictionChannelCapacity = 4096;

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

        _evictionChannel = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(EvictionChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
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
        if (_disposed || result is null || !result.TryBeginEviction())
            return false;

        Interlocked.Increment(ref _enqueuedCount);
        if (_evictionChannel.Writer.TryWrite(result))
            return true;
        // Failure path (channel closed): re-balance the counter so Drain() can complete.
        result.CancelEvictionReservation();
        Interlocked.Decrement(ref _enqueuedCount);
        return false;
    }

    public async ValueTask<bool> EnqueueEvictAsync(SearchResult result, CancellationToken cancellationToken = default)
    {
        if (_disposed || result is null || !result.TryBeginEviction())
            return false;

        Interlocked.Increment(ref _enqueuedCount);
        try
        {
            await _evictionChannel.Writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            result.CancelEvictionReservation();
            Interlocked.Decrement(ref _enqueuedCount);
            return false;
        }
    }

    public bool EnqueueEvictBlocking(SearchResult result, CancellationToken cancellationToken = default)
    {
        if (_disposed || result is null || !result.TryBeginEviction())
            return false;

        Interlocked.Increment(ref _enqueuedCount);
        try
        {
            _evictionChannel.Writer.WriteAsync(result, cancellationToken).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            result.CancelEvictionReservation();
            Interlocked.Decrement(ref _enqueuedCount);
            return false;
        }
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

    public async ValueTask<int> EnqueueEvictManyAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default)
    {
        if (results is null || results.Count == 0) return 0;
        int queued = 0;
        for (int i = 0; i < results.Count && !cancellationToken.IsCancellationRequested; i++)
            if (await EnqueueEvictAsync(results[i], cancellationToken).ConfigureAwait(false)) queued++;
        return queued;
    }

    /// <summary>
    /// Evict a batch immediately on the caller's thread using one writer lock.
    /// Intended for worker-thread pre-eviction before results are attached to the UI graph.
    /// </summary>
    public int EvictManyNow(IReadOnlyList<SearchResult> results)
    {
        if (results is null || results.Count == 0) return 0;
        int evicted = 0;
        WriteBatch(writeOne =>
        {
            for (int i = 0; i < results.Count; i++)
            {
                try
                {
                    if (results[i].EvictWithLight(writeOne))
                        evicted++;
                }
                catch
                {
                    // Keep the rest of the batch moving; individual failures leave
                    // their result in memory and can be picked up by later EvictAll cycles.
                }
            }
        });
        return evicted;
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
                            try { batch[i].CompleteReservedEvictionWith(writeOne); }
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
    /// Write raw UTF-8 bytes directly to the store, bypassing String allocation entirely.
    /// Used in degraded mode to avoid the UTF-8 → String → UTF-8 round-trip per match.
    /// The format matches <see cref="Read"/>: BinaryWriter-style length-prefixed string
    /// for match line, then Int32-counted string lists for context.
    /// </summary>
    /// <param name="matchLineUtf8">Raw UTF-8 bytes of the match line (already truncated).</param>
    /// <param name="contextBeforePtr">Rust-packed context: repeated [4-byte LE len][bytes].</param>
    /// <param name="contextBeforeBytes">Total byte length of the before-context buffer.</param>
    /// <param name="contextBeforeCount">Number of lines in before-context.</param>
    /// <param name="contextAfterPtr">Rust-packed context: repeated [4-byte LE len][bytes].</param>
    /// <param name="contextAfterBytes">Total byte length of the after-context buffer.</param>
    /// <param name="contextAfterCount">Number of lines in after-context.</param>
    /// <returns>Byte offset that can be passed to <see cref="Read"/> later.</returns>
    public unsafe long WriteRawUtf8(
        ReadOnlySpan<byte> matchLineUtf8,
        byte* contextBeforePtr, int contextBeforeBytes, int contextBeforeCount,
        byte* contextAfterPtr, int contextAfterBytes, int contextAfterCount)
    {
        lock (_lock)
        {
            long offset = _stream.Position;

            // Match line: 7-bit-encoded length + raw UTF-8 bytes
            _writer.Write7BitEncodedInt(matchLineUtf8.Length);
            if (matchLineUtf8.Length > 0)
                _writer.BaseStream.Write(matchLineUtf8);

            // Context before
            WriteRawContextLines(_writer, contextBeforePtr, contextBeforeBytes, contextBeforeCount);

            // Context after
            WriteRawContextLines(_writer, contextAfterPtr, contextAfterBytes, contextAfterCount);

            Interlocked.Increment(ref _evictedCount);
            return offset;
        }
    }

    /// <summary>
    /// Writes Rust-packed context lines as BinaryWriter-format string list.
    /// Rust format: repeated [4-byte LE length][UTF-8 bytes].
    /// Output format: Int32(count) + repeated [7-bit-encoded length][UTF-8 bytes].
    /// Lines are truncated to <see cref="LineTruncator.MaxDisplayLength"/> bytes.
    /// </summary>
    private static unsafe void WriteRawContextLines(BinaryWriter w, byte* ptr, int totalBytes, int count)
    {
        w.Write(count);
        if (count == 0 || ptr == null || totalBytes == 0) return;

        int pos = 0;
        int maxBytes = (LineTruncator.MaxDisplayLength + 1) * 4; // same cap as DecodeAndTruncate
        for (int i = 0; i < count && pos + 4 <= totalBytes; i++)
        {
            uint len = (uint)(ptr[pos] | (ptr[pos + 1] << 8) | (ptr[pos + 2] << 16) | (ptr[pos + 3] << 24));
            pos += 4;
            if (len > int.MaxValue || pos + (int)len > totalBytes)
            {
                // Malformed — write empty string for remaining
                w.Write7BitEncodedInt(0);
                continue;
            }
            int writeLen = Math.Min((int)len, maxBytes);
            // Align to UTF-8 char boundary if truncated
            if (writeLen < (int)len)
            {
                while (writeLen > 0 && (ptr[pos + writeLen] & 0xC0) == 0x80)
                    writeLen--;
            }
            w.Write7BitEncodedInt(writeLen);
            if (writeLen > 0)
                w.BaseStream.Write(new ReadOnlySpan<byte>(ptr + pos, writeLen));
            pos += (int)len;
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
