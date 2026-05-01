using System.IO.MemoryMappedFiles;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;
using static Yagu.Helpers.LineTruncator;

namespace Yagu.Services;

/// <summary>
/// Searches the contents of a single file for matches according to <see cref="SearchOptions"/>.
/// </summary>
public sealed class ContentSearcher
{
    public const long MemoryMapThresholdBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Limits the number of concurrent memory-mapped file views to prevent
    /// runaway virtual address space / private bytes growth. With 24+ parallel
    /// workers, uncapped MMF views were contributing to 13+ GB unmanaged memory.
    /// Raised from 4 to 16 since the threshold now only targets genuinely large
    /// files (≥ 8 MB), and the Rust side's mmap doesn't gate at all.
    /// </summary>
    private static readonly SemaphoreSlim s_mmfGate = new(16, 16);

    /// <summary>
    /// Limits concurrent native (Rust) scans. The native engine also memory-maps
    /// large files, so it must be gated like the managed MMF path. Without this,
    /// 24-way parallel scans of 50 MB-cap files drove process working set above
    /// 22 GB while the managed heap stayed under 3 GB (i.e. unmanaged blow-up).
    /// Higher than <see cref="s_mmfGate"/> because the native path streams
    /// matches incrementally without holding all decoded text, and its mmaps
    /// are promptly munmap'd. NVMe drives saturate at 16–64 outstanding reads,
    /// so the old cap of 8 was an I/O ceiling on fast storage.
    /// </summary>
    private static readonly SemaphoreSlim s_nativeGate = new(
        Math.Min(32, Environment.ProcessorCount * 2),
        Math.Min(32, Environment.ProcessorCount * 2));

    // Skip-reason codes (all negative so they are distinguishable from match counts).
    public const int SkipBinary     = -1;
    public const int SkipAccessDenied = -2;
    public const int SkipIOError     = -3;
    public const int SkipTooLarge    = -4;
    public const int SkipNotFound    = -5;
    public const int SkipEncoding    = -6;
    public const int SkipOther       = -7;
    public const int SkipByExtension = -8;

    /// <summary>
    /// Search a single file and write matches to <paramref name="writer"/>.
    /// Returns the number of matches written, or a negative SkipXxx code if skipped.
    /// </summary>
    public Task<int> SearchFileAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken)
        => SearchFileAsync(filePath, regex, literal, literalComparison, options, writer, cancellationToken, session: null);

    /// <summary>
    /// Search a single file using an optional pre-compiled native session for
    /// faster regex reuse across files. Falls back to the sessionless path
    /// when <paramref name="session"/> is null.
    /// </summary>
    internal async Task<int> SearchFileAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        Native.NativeSession? session)
        => (await SearchFileWithStatsAsync(filePath, regex, literal, literalComparison, options, writer, cancellationToken, session).ConfigureAwait(false)).MatchCount;

    internal async Task<FileSearchOutcome> SearchFileWithStatsAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        Native.NativeSession? session)
    {
        bool watched = FileWatchDiagnostics.IsWatched(filePath);
        var totalSw = watched ? System.Diagnostics.Stopwatch.StartNew() : null;
        if (watched) FileWatchDiagnostics.Checkpoint(filePath, "ENTER");

        FileInfo fi;
        long fileLength;
        FileMetadata metadata;
        if (FileMetadataCache.TryGet(filePath, out var cached))
        {
            fileLength = cached.Length;
            metadata = cached;
            fi = null!; // avoid stat; only used for Exists check below
            if (fileLength == 0 && !File.Exists(filePath)) return new FileSearchOutcome(SkipNotFound, 0);
        }
        else
        {
            try { fi = new FileInfo(filePath); }
            catch (Exception ex) { LogService.Instance.Verbose("ContentSearcher", $"Cannot stat file: {filePath}", ex); return new FileSearchOutcome(SkipOther, 0); }
            if (!fi.Exists) return new FileSearchOutcome(SkipNotFound, 0);

            fileLength = fi.Length;
            metadata = new FileMetadata(fileLength, fi.LastWriteTime);
        }

        if (options.MaxFileSizeBytes > 0 && fileLength > options.MaxFileSizeBytes) return new FileSearchOutcome(SkipTooLarge, 0);

        // Extension-based skip — no binary sniff, no content read.
        // When SearchInsideArchives is enabled, don't skip ZIP archives by extension.
        if (options.SkipExtensions.Count > 0)
        {
            var ext = Path.GetExtension(filePath);
            if (ext.Length > 1 && options.SkipExtensions.Contains(ext.AsSpan(1).ToString()))
            {
                // Allow zip-like extensions through when archive search is enabled
                if (!options.SearchInsideArchives || !IsZipLikeExtension(ext))
                    return new FileSearchOutcome(SkipByExtension, 0);
            }
        }

        // ZIP archive search: before the normal scan paths, peek the file header
        // and redirect to the archive searcher if it's a ZIP file.
        if (options.SearchInsideArchives)
        {
            try
            {
                using var peekFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> headerBuf = stackalloc byte[4];
                int headerRead = 0;
                while (headerRead < 4)
                {
                    int n = peekFs.Read(headerBuf[headerRead..]);
                    if (n <= 0) break;
                    headerRead += n;
                }
                if (headerRead >= 4 && BinaryDetector.IsZipMagic(headerBuf[..headerRead]))
                {
                    int archiveMatches = await ZipArchiveSearcher.SearchArchiveAsync(
                        filePath, regex, literal, literalComparison, options, writer, cancellationToken).ConfigureAwait(false);
                    if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-ZIP", totalSw!.ElapsedMilliseconds, $"produced={archiveMatches}");
                    return new FileSearchOutcome(archiveMatches, archiveMatches >= 0 ? fileLength : 0);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Instance.Verbose("ContentSearcher", $"ZIP header check failed for {filePath}, falling through to normal scan", ex);
            }
        }

        // Native (Rust) fast path. Falls back to the managed path on any failure
        // mode the native engine doesn't handle (encoding-aware decoding,
        // unusual streams, etc.).
        if (PreferNative && Native.NativeSearcher.IsAvailable)
        {
            int produced = await TryNativeAsync(filePath, options, writer, cancellationToken, session, metadata).ConfigureAwait(false);
            if (produced != NativeFellThrough)
            {
                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-NATIVE", totalSw!.ElapsedMilliseconds, $"produced={produced}");
                return new FileSearchOutcome(produced, produced >= 0 ? fileLength : 0);
            }
            if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-FELLTHROUGH", totalSw!.ElapsedMilliseconds);
        }

        try
        {
            int result;
            if (fileLength >= MemoryMapThresholdBytes)
            {
                // Large files: binary sniff still happens via the MMF view's first 8 KB.
                result = await SearchMappedAsync(filePath, fileLength, regex, literal, literalComparison, options, writer, cancellationToken, metadata).ConfigureAwait(false);
            }
            else
            {
                // Single-peek open: one FileStream, one 8 KB read for binary + encoding
                // detection, then reuse the same stream for scanning. This eliminates the
                // 2–3 CreateFile round-trips per file the old code path incurred.
                result = await SearchStreamSinglePeekAsync(filePath, regex, literal, literalComparison, options, writer, cancellationToken, metadata).ConfigureAwait(false);
            }
            if (watched) FileWatchDiagnostics.Checkpoint(filePath, "EXIT-MANAGED", totalSw!.ElapsedMilliseconds, $"produced={result} size={fileLength}");
            return new FileSearchOutcome(result, result >= 0 ? fileLength : 0);
        }
        catch (UnauthorizedAccessException ex) { LogService.Instance.Verbose("ContentSearcher", $"Access denied searching: {filePath}", ex); return new FileSearchOutcome(SkipAccessDenied, 0); }
        catch (IOException ex) { LogService.Instance.Verbose("ContentSearcher", $"IO error searching: {filePath}", ex); return new FileSearchOutcome(SkipIOError, 0); }
        catch (DecoderFallbackException ex) { LogService.Instance.Verbose("ContentSearcher", $"Encoding error searching: {filePath}", ex); return new FileSearchOutcome(SkipEncoding, 0); }
    }

    private static async Task<int> SearchStreamAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        FileMetadata metadata)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var encoding = EncodingDetector.DetectEncoding(fs);
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        return await SearchLinesAsync(filePath, reader, regex, literal, literalComparison, options, writer, cancellationToken, metadata).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-peek stream path: opens the file once, reads 8 KB into a buffer,
    /// runs binary detection AND encoding detection on that buffer, then seeks
    /// back and scans. Eliminates the 2–3 separate CreateFile + ReadFile calls
    /// the old code path made per file (~0.2 ms each on HDD).
    /// </summary>
    private static async Task<int> SearchStreamSinglePeekAsync(
        string filePath,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        FileMetadata metadata)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        // Single 8 KB peek: shared by binary detection + encoding detection.
        Span<byte> peek = stackalloc byte[BinaryDetector.SampleBytes];
        int peekRead = 0;
        while (peekRead < peek.Length)
        {
            int n = fs.Read(peek[peekRead..]);
            if (n <= 0) break;
            peekRead += n;
        }

        if (options.SkipBinary && peekRead > 0 && BinaryDetector.IsBinary(peek[..peekRead]))
            return SkipBinary;

        var encoding = EncodingDetector.DetectEncoding(peek[..Math.Min(peekRead, 4)]);

        // Seek back to the start so StreamReader reads the full file.
        fs.Position = 0;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        return await SearchLinesAsync(filePath, reader, regex, literal, literalComparison, options, writer, cancellationToken, metadata).ConfigureAwait(false);
    }

    private static async Task<int> SearchMappedAsync(
        string filePath,
        long length,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        FileMetadata metadata)
    {
        await s_mmfGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            using var stream = mmf.CreateViewStream(0, length, MemoryMappedFileAccess.Read);

            // Peek the first 8 KB of the view for binary + encoding detection
            // so we don't need a separate file open for sniffing.
            Span<byte> peek = stackalloc byte[BinaryDetector.SampleBytes];
            int peekRead = 0;
            while (peekRead < peek.Length)
            {
                int n = stream.Read(peek[peekRead..]);
                if (n <= 0) break;
                peekRead += n;
            }

            if (options.SkipBinary && peekRead > 0 && BinaryDetector.IsBinary(peek[..peekRead]))
                return SkipBinary;

            var encoding = EncodingDetector.DetectEncoding(peek[..Math.Min(peekRead, 4)]);
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return await SearchLinesAsync(filePath, reader, regex, literal, literalComparison, options, writer, cancellationToken, metadata).ConfigureAwait(false);
        }
        finally
        {
            s_mmfGate.Release();
        }
    }

    private static async Task<int> SearchLinesAsync(
        string filePath,
        StreamReader reader,
        Regex? regex,
        string? literal,
        StringComparison literalComparison,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        FileMetadata metadata)
    {
        int contextLines = Math.Max(0, options.ContextLines);
        int perFileCap = options.MaxMatchesPerFile > 0 ? options.MaxMatchesPerFile : int.MaxValue;
        var ring = new RingBuffer<string>(contextLines);
        // Each pending entry accumulates context-after lines in a mutable list,
        // building the final SearchResult only once when the context is complete.
        var pendingAfter = new Queue<(SearchResult Partial, List<string> AfterLines, int Remaining)>();
        int lineNumber = 0;
        int matchCount = 0;
        bool metadataCached = false;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            // Fill out queued context-after slots.
            if (pendingAfter.Count > 0)
            {
                int remaining = pendingAfter.Count;
                for (int i = 0; i < remaining; i++)
                {
                    var (partial, afterLines, left) = pendingAfter.Dequeue();
                    afterLines.Add(Truncate(line));
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

            // Find matches in this line.
            var matches = FindMatches(line, regex, literal, literalComparison);
            if (matches.Count > 0)
            {
                if (!metadataCached)
                {
                    FileMetadataCache.Set(filePath, metadata);
                    metadataCached = true;
                }
                matchCount += matches.Count;
                var before = ring.Snapshot();
                foreach (var (start, length) in matches)
                {
                    var displayLine = TruncateAroundMatch(line, start, length);
                    var partial = new SearchResult(
                        FilePath: filePath,
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

            ring.Add(Truncate(line));
        }

        // Flush any partials that didn't get a full after-context.
        while (pendingAfter.Count > 0)
        {
            var (partial, afterLines, _) = pendingAfter.Dequeue();
            await writer.WriteAsync(partial with { ContextAfter = afterLines }, cancellationToken).ConfigureAwait(false);
        }

        return matchCount;
    }

    [ThreadStatic] private static List<(int Start, int Length)>? t_hits;

    internal static List<(int Start, int Length)> FindMatches(string line, Regex? regex, string? literal, StringComparison cmp)
    {
        var hits = t_hits ??= new List<(int, int)>();
        hits.Clear();
        if (regex is not null)
        {
            foreach (Match m in regex.Matches(line))
            {
                if (m.Length == 0) continue; // avoid zero-width loop
                hits.Add((m.Index, m.Length));
            }
        }
        else if (!string.IsNullOrEmpty(literal))
        {
            int idx = 0;
            while (idx <= line.Length)
            {
                int found = line.IndexOf(literal, idx, cmp);
                if (found < 0) break;
                hits.Add((found, literal.Length));
                idx = found + literal.Length;
            }
        }
        return hits;
    }

    private sealed class RingBuffer<T>
    {
        private readonly T[] _data;
        private int _count;
        private int _head;
        // Cached snapshot: reused across consecutive matches until the ring mutates.
        // Without this, a file with N matches in a row allocated N copies of the same
        // before-context array — a major LOH/Gen2 contributor on dense-match files.
        private IReadOnlyList<T>? _cachedSnapshot;
        public RingBuffer(int capacity) { _data = new T[Math.Max(0, capacity)]; }
        public void Add(T item)
        {
            if (_data.Length == 0) return;
            _data[_head] = item;
            _head = (_head + 1) % _data.Length;
            if (_count < _data.Length) _count++;
            _cachedSnapshot = null; // invalidate
        }
        public IReadOnlyList<T> Snapshot()
        {
            if (_count == 0) return Array.Empty<T>();
            if (_cachedSnapshot is not null) return _cachedSnapshot;
            var result = new T[_count];
            int start = (_head - _count + _data.Length) % _data.Length;
            for (int i = 0; i < _count; i++) result[i] = _data[(start + i) % _data.Length];
            _cachedSnapshot = result;
            return result;
        }
    }

    /// <summary>When true, attempt the native (Rust) scan path before falling back to managed.</summary>
    public static bool PreferNative { get; set; } = true;

    /// <summary>Extensions that may be ZIP archives. Used to bypass extension-based skip when archive search is enabled.</summary>
    internal static readonly HashSet<string> ZipLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jar", ".war", ".ear", ".nupkg", ".vsix", ".apk",
        ".aab", ".aar", ".appx", ".msix", ".appxbundle", ".msixbundle",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
    };

    internal static bool IsZipLikeExtension(string ext) => ZipLikeExtensions.Contains(ext);

    /// <summary>Sentinel returned by <see cref="TryNativeAsync"/> when the native path cannot be used and the caller should run the managed path.</summary>
    private const int NativeFellThrough = int.MinValue;

    /// <summary>
    /// Per-thread cancel-flag pointer. Eliminates per-file AllocHGlobal/FreeHGlobal
    /// + CancellationToken.Register allocations — the same pinned int* is reused for
    /// every file scanned on a given thread.
    /// </summary>
    private static readonly ThreadLocal<IntPtr> t_cancelPtr = new(
        () => System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int)),
        trackAllValues: false);

    [ExcludeFromCodeCoverage]
    private static async Task<int> TryNativeAsync(
        string filePath,
        SearchOptions options,
        ChannelWriter<SearchResult> writer,
        CancellationToken cancellationToken,
        Native.NativeSession? session,
        FileMetadata metadata)
    {
        bool watched = FileWatchDiagnostics.IsWatched(filePath);
        var gateSw = watched ? System.Diagnostics.Stopwatch.StartNew() : null;
        // Gate native scans to bound concurrent mmap views. See s_nativeGate.
        await s_nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (watched)
        {
            FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-GATE-ACQUIRED", gateSw!.ElapsedMilliseconds,
                $"gateAvail={s_nativeGate.CurrentCount}");
        }
        try
        {
            return await Task.Run(() =>
            {
                // Reuse the thread-local cancel-int — reset to 0 before each file.
                IntPtr cancelPtr = t_cancelPtr.Value!;
                unsafe { *(int*)cancelPtr = 0; }
                using var ctr = cancellationToken.Register(static state =>
                {
                    unsafe { System.Threading.Interlocked.Exchange(ref *(int*)(IntPtr)state!, 1); }
                }, cancelPtr);

                var sink = new StreamingSink(filePath, writer, cancellationToken, options.ContextLines, metadata);
                var scanSw = watched ? System.Diagnostics.Stopwatch.StartNew() : null;
                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-SCAN-START");
                int status;
                try
                {
                    unsafe
                    {
                        if (session != null)
                        {
                            // Session path: uses pre-compiled regex — no per-file compilation.
                            status = Native.NativeSearcher.SearchFileStreamWithSession(
                                session, filePath, (int*)cancelPtr, sink);
                        }
                        else
                        {
                            status = Native.NativeSearcher.SearchFileStream(
                                filePath, options.Query, options, (int*)cancelPtr, sink);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("ContentSearcher", $"Native streaming scan threw for {filePath}; falling back to managed", ex);
                    if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-SCAN-THREW", scanSw!.ElapsedMilliseconds, ex.GetType().Name);
                    return NativeFellThrough;
                }

                if (watched) FileWatchDiagnostics.Checkpoint(filePath, "NATIVE-SCAN-DONE", scanSw!.ElapsedMilliseconds, $"status={status} emitted={sink.Emitted}");

                return status switch
                {
                    Native.NativeSearcher.StatusOk => sink.Emitted,
                    Native.NativeSearcher.StatusBinarySkipped => SkipBinary,
                    Native.NativeSearcher.StatusTooLarge => SkipTooLarge,
                    Native.NativeSearcher.StatusOpenFailed => SkipAccessDenied,
                    Native.NativeSearcher.StatusCancelled => Cancel(cancellationToken, sink.Emitted),
                    Native.NativeSearcher.StatusInvalidRegex => NativeFellThrough,
                    _ => NativeFellThrough,
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_nativeGate.Release();
        }
    }

    internal static int Cancel(CancellationToken ct, int emitted)
    {
        ct.ThrowIfCancellationRequested();
        return emitted;
    }

    /// <summary>
    /// Sink that receives per-match callbacks from the native engine, copies
    /// pointer-backed data into managed strings, and writes <see cref="SearchResult"/>
    /// items into the channel.
    /// </summary>
    [ExcludeFromCodeCoverage]
    private sealed class StreamingSink : Native.IStreamingSink
    {
        private readonly string _filePath;
        private readonly ChannelWriter<SearchResult> _writer;
        private readonly CancellationToken _ct;
        private readonly int _contextLines;
        private readonly FileMetadata _metadata;
        private bool _metadataCached;
        public int Emitted;
        public Exception? CapturedException { get; set; }
        public string? ErrorMessage { get; set; }

        public StreamingSink(string filePath, ChannelWriter<SearchResult> writer, CancellationToken ct, int contextLines, FileMetadata metadata)
        {
            _filePath = filePath;
            _writer = writer;
            _ct = ct;
            _contextLines = contextLines;
            _metadata = metadata;
        }

        public unsafe int OnMatch(Native.NativeSearcher.QgMatchView* m)
        {
            if (_ct.IsCancellationRequested) return 1;

            var view = *m;
            int lineBytes = view.LineLen > (nuint)int.MaxValue ? int.MaxValue : (int)view.LineLen;
            int matchStartBytes = view.MatchStart > int.MaxValue ? lineBytes : (int)view.MatchStart;
            int matchLenBytes = view.MatchLen > int.MaxValue ? 0 : (int)view.MatchLen;
            var matchLine = DecodeMatchLine(view.LinePtr, lineBytes, matchStartBytes, matchLenBytes);

            var before = UnpackLinesTruncated(view.CtxBeforePtr, view.CtxBeforeBytes, view.CtxBeforeCount);
            var after = UnpackLinesTruncated(view.CtxAfterPtr, view.CtxAfterBytes, view.CtxAfterCount);

            int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;

            var result = new SearchResult(
                FilePath: _filePath,
                LineNumber: lineNum,
                MatchLine: matchLine.Line,
                MatchStartColumn: matchLine.MatchStart,
                MatchLength: matchLine.MatchLength,
                ContextBefore: before,
                ContextAfter: after);

            // TryWrite is non-blocking; the channel is unbounded for streaming
            // (global MaxResults bounds total memory). If TryWrite ever fails
            // (closed channel), stop scanning.
            if (!_metadataCached)
            {
                FileMetadataCache.Set(_filePath, _metadata);
                _metadataCached = true;
            }

            if (!_writer.TryWrite(result)) return 1;
            Emitted++;
            return 0;
        }

        private static unsafe (string Line, int MatchStart, int MatchLength) DecodeMatchLine(byte* ptr, int len, int matchStartBytes, int matchLenBytes)
            => NativeMatchDecoder.DecodeMatchLine(ptr, len, matchStartBytes, matchLenBytes);

        private static unsafe IReadOnlyList<string> UnpackLinesTruncated(byte* ptr, nuint totalBytes, uint count)
            => NativeMatchDecoder.UnpackLinesTruncated(ptr, totalBytes, count);
    }

    /// <summary>
    /// Shared helpers for decoding native match data into managed SearchResult fields.
    /// Used by both the per-file <see cref="StreamingSink"/> and the batch
    /// <see cref="SearchService"/> parallel scan sink.
    /// </summary>
    internal static class NativeMatchDecoder
    {
        internal static unsafe (string Line, int MatchStart, int MatchLength) DecodeMatchLine(byte* ptr, int len, int matchStartBytes, int matchLenBytes)
        {
            if (ptr == null || len <= 0) return (string.Empty, 0, 0);

            int safeStartBytes = Math.Clamp(matchStartBytes, 0, len);
            int safeLengthBytes = Math.Clamp(matchLenBytes, 0, len - safeStartBytes);
            var line = Encoding.UTF8.GetString(ptr, len);
            int matchStart = Encoding.UTF8.GetCharCount(ptr, safeStartBytes);
            int matchLength = Encoding.UTF8.GetCharCount(ptr + safeStartBytes, safeLengthBytes);
            var displayLine = LineTruncator.TruncateAroundMatch(line, matchStart, matchLength);
            return (displayLine.Text, displayLine.MatchStart, matchLength);
        }

        internal static unsafe IReadOnlyList<string> UnpackLinesTruncated(byte* ptr, nuint totalBytes, uint count)
        {
            if (count == 0 || ptr == null || totalBytes == 0) return Array.Empty<string>();
            var list = new List<string>((int)count);
            int pos = 0;
            int total = (int)totalBytes;
            for (uint i = 0; i < count && pos + 4 <= total; i++)
            {
                uint len = (uint)(ptr[pos] | (ptr[pos + 1] << 8) | (ptr[pos + 2] << 16) | (ptr[pos + 3] << 24));
                pos += 4;
                if (len > int.MaxValue || pos + (int)len > total) break;
                list.Add(DecodeAndTruncate(ptr + pos, (int)len));
                pos += (int)len;
            }
            return list;
        }

        /// <summary>
        /// Decode a UTF-8 byte buffer to a managed string, hard-capped to keep huge
        /// lines off the LOH.
        /// </summary>
        internal static unsafe string DecodeAndTruncate(byte* ptr, int len)
        {
            if (ptr == null || len <= 0) return string.Empty;
            int maxBytes = (LineTruncator.MaxDisplayLength + 1) * 4;
            bool bytesTruncated = len > maxBytes;
            int decodeBytes = bytesTruncated ? maxBytes : len;
            if (bytesTruncated)
            {
                while (decodeBytes > 0 && (ptr[decodeBytes] & 0xC0) == 0x80) decodeBytes--;
            }
            var s = System.Text.Encoding.UTF8.GetString(ptr, decodeBytes);
            if (bytesTruncated && s.Length <= LineTruncator.MaxDisplayLength)
            {
                int keep = Math.Min(s.Length, LineTruncator.TruncatedLength);
                return string.Concat(s.AsSpan(0, keep), LineTruncator.Ellipsis);
            }
            return Truncate(s);
        }
    }
}
