using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Native;

/// <summary>
/// P/Invoke wrapper around the <c>yagu_core</c> Rust cdylib. The DLL is
/// optional — when it can't be loaded the rest of the app falls back to the
/// managed <see cref="Services.ContentSearcher"/> implementation.
/// </summary>

internal static partial class NativeSearcher
{
    private const string DllName = "yagu_core";

    [StructLayout(LayoutKind.Sequential)]
    internal struct QgOptions
    {
        public byte CaseSensitive;
        public byte UseRegex;
        public byte SkipBinary;
        public byte _Pad0;
        public uint ContextBefore;
        public uint ContextAfter;
        public ulong MaxResults;
        public ulong MaxFileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QgResult
    {
        public IntPtr Buffer;
        public nuint BufferLen;
        public uint MatchCount;
        public int Status;
        public IntPtr ErrorMsg;
        public nuint ErrorMsgLen;
    }

    internal const int StatusOk = 0;
    internal const int StatusOpenFailed = 1;
    internal const int StatusTooLarge = 2;
    internal const int StatusBinarySkipped = 3;
    internal const int StatusInvalidRegex = 4;
    internal const int StatusInvalidPath = 5;
    internal const int StatusCancelled = 6;

    [LibraryImport(DllName, EntryPoint = "qg_abi_version")]
    private static partial uint QgAbiVersion();

    [LibraryImport(DllName, EntryPoint = "qg_search_file")]
    private static unsafe partial int QgSearchFile(
        char* pathUtf16,
        nuint pathLen,
        byte* patternUtf8,
        nuint patternLen,
        QgOptions* options,
        int* cancelFlag,
        QgResult* outResult);

    [LibraryImport(DllName, EntryPoint = "qg_free_result")]
    private static unsafe partial void QgFreeResult(QgResult* result);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct QgMatchView
    {
        public ulong LineNumber;
        public uint MatchStart;
        public uint SourceMatchStart;
        public uint MatchLen;
        public byte* LinePtr;
        public nuint LineLen;
        public byte* CtxBeforePtr;
        public nuint CtxBeforeBytes;
        public uint CtxBeforeCount;
        public byte* CtxAfterPtr;
        public nuint CtxAfterBytes;
        public uint CtxAfterCount;
    }

    [LibraryImport(DllName, EntryPoint = "qg_search_file_stream")]
    private static unsafe partial int QgSearchFileStream(
        char* pathUtf16,
        nuint pathLen,
        byte* patternUtf8,
        nuint patternLen,
        QgOptions* options,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, QgMatchView*, int> onMatch,
        void* onMatchCtx,
        int* outStatus,
        byte** outErrorMsg,
        nuint* outErrorMsgLen);

    [LibraryImport(DllName, EntryPoint = "qg_free_buffer")]
    private static unsafe partial void QgFreeBuffer(byte* ptr, nuint len);

    // ── Session API (ABI v3): compile once, search many ──

    [LibraryImport(DllName, EntryPoint = "qg_create_session")]
    private static unsafe partial IntPtr QgCreateSession(
        byte* patternUtf8,
        nuint patternLen,
        QgOptions* options,
        byte** outErrorMsg,
        nuint* outErrorMsgLen);

    [LibraryImport(DllName, EntryPoint = "qg_free_session")]
    private static unsafe partial void QgFreeSession(IntPtr session);

    // Internal accessor for NativeSession.Dispose
    internal static unsafe void QgFreeSessionPublic(IntPtr session) => QgFreeSession(session);

    [LibraryImport(DllName, EntryPoint = "qg_session_search_file_stream")]
    private static unsafe partial int QgSessionSearchFileStream(
        IntPtr session,
        char* pathUtf16,
        nuint pathLen,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, QgMatchView*, int> onMatch,
        void* onMatchCtx,
        int* outStatus,
        byte** outErrorMsg,
        nuint* outErrorMsgLen);

    [LibraryImport(DllName, EntryPoint = "qg_session_scan_paths_parallel_ex")]
    private static unsafe partial int QgSessionScanPathsParallelEx(
        IntPtr session,
        char* pathsUtf16Concat,
        uint* pathLengths,
        nuint pathCount,
        uint threadCount,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, uint, QgMatchView*, int> onMatch,
        delegate* unmanaged[Cdecl]<void*, uint, int, ulong, ulong, void> onFileDone,
        void* onMatchCtx);

    // ── Streaming scanner FFI ──

    [LibraryImport(DllName, EntryPoint = "qg_create_streaming_scanner")]
    private static unsafe partial IntPtr QgCreateStreamingScanner(
        IntPtr session,
        uint threadCount,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, uint, QgMatchView*, int> onMatch,
        delegate* unmanaged[Cdecl]<void*, uint, int, ulong, ulong, void> onFileDone,
        void* onMatchCtx);

    [LibraryImport(DllName, EntryPoint = "qg_scanner_push_paths")]
    private static unsafe partial int QgScannerPushPaths(
        IntPtr scanner,
        char* pathsUtf16Concat,
        uint* pathLengths,
        nuint pathCount,
        uint fileIndexBase);

    [LibraryImport(DllName, EntryPoint = "qg_scanner_finish")]
    private static unsafe partial int QgScannerFinish(IntPtr scanner);

    [LibraryImport(DllName, EntryPoint = "qg_scanner_destroy")]
    private static unsafe partial void QgScannerDestroy(IntPtr scanner);

    private static readonly Lazy<bool> _available = new(TryLoad, LazyThreadSafetyMode.ExecutionAndPublication);
    public static bool IsAvailable => _available.Value;

    private static bool TryLoad()
    {
        // Resolve relative to this assembly's directory so unpackaged xcopy works.
        var dir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(dir))
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeSearcher).Assembly, (name, asm, _) =>
            {
                if (!string.Equals(name, DllName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                var candidate = Path.Combine(dir, "yagu_core.dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) return h;
                return NativeLibrary.TryLoad("yagu_core", asm, null, out var h2) ? h2 : IntPtr.Zero;
            });
        }

        try
        {
            return QgAbiVersion() == 4;
        }
        catch (DllNotFoundException) { LogService.Instance.Info("NativeSearcher", "yagu_core.dll not found"); return false; }
        catch (BadImageFormatException ex) { LogService.Instance.Warning("NativeSearcher", "yagu_core.dll bad image format", ex); return false; }
        catch (EntryPointNotFoundException ex) { LogService.Instance.Warning("NativeSearcher", "yagu_core.dll missing entry point", ex); return false; }
    }

    /// <summary>
    /// Computes the per-file match cap passed to the Rust engine. Uses the tighter
    /// of MaxMatchesPerFile and MaxResults so one huge file cannot exhaust the
    /// global budget.
    /// </summary>
    private static ulong EffectivePerFileCap(SearchOptions options)
    {
        int perFile = options.MaxMatchesPerFile > 0 ? options.MaxMatchesPerFile : 0;
        int global = options.MaxResults > 0 ? options.MaxResults : 0;
        if (perFile > 0 && global > 0) return (ulong)Math.Min(perFile, global);
        if (perFile > 0) return (ulong)perFile;
        if (global > 0) return (ulong)global;
        return 0;
    }

    /// <summary>
    /// Search a single file. Returns null when the native engine isn't loaded
    /// or the file should be skipped by the binary/size policy.
    /// </summary>
    public static unsafe NativeSearchOutcome SearchFile(
        string filePath,
        string pattern,
        SearchOptions options,
        int* cancelFlag)
    {
        if (!IsAvailable) return NativeSearchOutcome.Unavailable;

        var ffiOptions = new QgOptions
        {
            CaseSensitive = (byte)(options.CaseSensitive ? 1 : 0),
            UseRegex = (byte)(options.UseRegex ? 1 : 0),
            SkipBinary = (byte)(options.SkipBinary ? 1 : 0),
            ContextBefore = (uint)Math.Max(0, options.ContextLines),
            ContextAfter = (uint)Math.Max(0, options.ContextLines),
            MaxResults = EffectivePerFileCap(options),
            MaxFileSize = options.MaxFileSizeBytes > 0 ? (ulong)options.MaxFileSizeBytes : 0UL,
        };

        QgResult result = default;
        int status;
        var patternBytes = System.Text.Encoding.UTF8.GetBytes(pattern);
        fixed (char* pPath = filePath)
        fixed (byte* pPattern = patternBytes)
        {
            status = QgSearchFile(
                pPath, (nuint)filePath.Length,
                pPattern, (nuint)patternBytes.Length,
                &ffiOptions,
                cancelFlag,
                &result);
        }

        try
        {
            return status switch
            {
                StatusOk => NativeSearchOutcome.FromBuffer(filePath, result, options.ContextLines),
                StatusBinarySkipped => NativeSearchOutcome.Skipped("binary"),
                StatusTooLarge => NativeSearchOutcome.Skipped("too-large"),
                StatusOpenFailed => NativeSearchOutcome.Skipped("open-failed"),
                StatusCancelled => NativeSearchOutcome.Cancelled(),
                StatusInvalidRegex => NativeSearchOutcome.Error(ReadError(result) ?? "invalid regex"),
                _ => NativeSearchOutcome.Error($"native status {status}"),
            };
        }
        finally
        {
            QgFreeResult(&result);
        }
    }

    private static unsafe string? ReadError(QgResult result)
    {
        if (result.ErrorMsg == IntPtr.Zero || result.ErrorMsgLen == 0) return null;
        return System.Text.Encoding.UTF8.GetString((byte*)result.ErrorMsg, (int)result.ErrorMsgLen);
    }

    /// <summary>
    /// Streaming search: invokes <paramref name="sink"/> for every match. Returns
    /// the final native status code (StatusOk on success).
    /// </summary>
    public static unsafe int SearchFileStream(
        string filePath,
        string pattern,
        SearchOptions options,
        int* cancelFlag,
        IStreamingSink sink)
    {
        if (!IsAvailable) return StatusOpenFailed;

        var ffiOptions = new QgOptions
        {
            CaseSensitive = (byte)(options.CaseSensitive ? 1 : 0),
            UseRegex = (byte)(options.UseRegex ? 1 : 0),
            SkipBinary = (byte)(options.SkipBinary ? 1 : 0),
            ContextBefore = (uint)Math.Max(0, options.ContextLines),
            ContextAfter = (uint)Math.Max(0, options.ContextLines),
            MaxResults = EffectivePerFileCap(options),
            MaxFileSize = options.MaxFileSizeBytes > 0 ? (ulong)options.MaxFileSizeBytes : 0UL,
        };

        var patternBytes = System.Text.Encoding.UTF8.GetBytes(pattern);
        var handle = GCHandle.Alloc(sink, GCHandleType.Normal);
        int status = StatusOk;
        byte* errMsg = null;
        nuint errMsgLen = 0;
        try
        {
            fixed (char* pPath = filePath)
            fixed (byte* pPattern = patternBytes)
            {
                _ = QgSearchFileStream(
                    pPath, (nuint)filePath.Length,
                    pPattern, (nuint)patternBytes.Length,
                    &ffiOptions,
                    cancelFlag,
                    &OnMatchTrampoline,
                    (void*)GCHandle.ToIntPtr(handle),
                    &status,
                    &errMsg,
                    &errMsgLen);
            }

            // Surface any error message captured by the sink (callback exception).
            if (sink.CapturedException is { } ex)
            {
                throw new InvalidOperationException("Streaming sink threw inside native callback", ex);
            }

            if (status == StatusInvalidRegex && errMsg != null && errMsgLen > 0)
            {
                sink.ErrorMessage = System.Text.Encoding.UTF8.GetString(errMsg, (int)errMsgLen);
            }

            return status;
        }
        finally
        {
            if (errMsg != null) QgFreeBuffer(errMsg, errMsgLen);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int OnMatchTrampoline(void* ctx, QgMatchView* m)
    {
        // Must NOT throw — UnmanagedCallersOnly forbids managed exceptions across the FFI boundary.
        try
        {
            var handle = GCHandle.FromIntPtr((IntPtr)ctx);
            if (handle.Target is IStreamingSink sink)
            {
                return sink.OnMatch(m);
            }
            return 1; // stop
        }
        catch (Exception ex)
        {
            // Stash on the sink (best-effort) and signal stop.
            try
            {
                var handle = GCHandle.FromIntPtr((IntPtr)ctx);
                if (handle.Target is IStreamingSink sink)
                {
                    sink.CapturedException = ex;
                }
            }
            catch { /* swallow */ }
            return 1;
        }
    }

    /// <summary>
    /// Create a pre-compiled search session. Returns null if pattern is invalid.
    /// The session is thread-safe and should be reused across all files in a search.
    /// </summary>
    public static unsafe NativeSession? CreateSession(string pattern, SearchOptions options)
    {
        if (!IsAvailable) return null;

        var ffiOptions = new QgOptions
        {
            CaseSensitive = (byte)(options.CaseSensitive ? 1 : 0),
            UseRegex = (byte)(options.UseRegex ? 1 : 0),
            SkipBinary = (byte)(options.SkipBinary ? 1 : 0),
            ContextBefore = (uint)Math.Max(0, options.ContextLines),
            ContextAfter = (uint)Math.Max(0, options.ContextLines),
            MaxResults = EffectivePerFileCap(options),
            MaxFileSize = options.MaxFileSizeBytes > 0 ? (ulong)options.MaxFileSizeBytes : 0UL,
        };

        var patternBytes = System.Text.Encoding.UTF8.GetBytes(pattern);
        byte* errMsg = null;
        nuint errMsgLen = 0;
        IntPtr handle;
        fixed (byte* pPattern = patternBytes)
        {
            handle = QgCreateSession(pPattern, (nuint)patternBytes.Length, &ffiOptions, &errMsg, &errMsgLen);
        }
        if (errMsg != null) QgFreeBuffer(errMsg, errMsgLen);
        if (handle == IntPtr.Zero) return null;
        return new NativeSession(handle);
    }

    /// <summary>
    /// Streaming search using a pre-compiled session. Same semantics as
    /// <see cref="SearchFileStream"/> but skips pattern compilation per file.
    /// </summary>
    public static unsafe int SearchFileStreamWithSession(
        NativeSession session,
        string filePath,
        int* cancelFlag,
        IStreamingSink sink)
    {
        var gcHandle = GCHandle.Alloc(sink, GCHandleType.Normal);
        int status = StatusOk;
        byte* errMsg = null;
        nuint errMsgLen = 0;
        try
        {
            fixed (char* pPath = filePath)
            {
                _ = QgSessionSearchFileStream(
                    session.Handle,
                    pPath, (nuint)filePath.Length,
                    cancelFlag,
                    &OnMatchTrampoline,
                    (void*)GCHandle.ToIntPtr(gcHandle),
                    &status,
                    &errMsg,
                    &errMsgLen);
            }

            if (sink.CapturedException is { } ex)
                throw new InvalidOperationException("Streaming sink threw inside native callback", ex);

            if (status == StatusInvalidRegex && errMsg != null && errMsgLen > 0)
                sink.ErrorMessage = System.Text.Encoding.UTF8.GetString(errMsg, (int)errMsgLen);

            return status;
        }
        finally
        {
            if (errMsg != null) QgFreeBuffer(errMsg, errMsgLen);
            gcHandle.Free();
        }
    }

    /// <summary>
    /// Per-file completion callback for <see cref="ScanPathsParallel"/>.
    /// </summary>
    internal interface IParallelSink : IStreamingSink
    {
        unsafe int OnMatchForFile(uint fileIndex, QgMatchView* m);
        void OnFileDone(uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime);
    }

    /// <summary>
    /// Parallel batch search using a pre-compiled session and a worker pool
    /// inside the native library. Files are scanned concurrently so reads
    /// overlap; the match and per-file-done callbacks on <paramref name="sink"/>
    /// are serialised by the native layer so the sink can be non-thread-safe.
    /// </summary>
    public static unsafe int ScanPathsParallel(
        NativeSession session,
        IReadOnlyList<string> paths,
        int threadCount,
        int* cancelFlag,
        IParallelSink sink)
    {
        if (!IsAvailable) return StatusOpenFailed;
        if (paths.Count == 0) return StatusOk;

        // Concatenate UTF-16 paths and build a parallel length array. These
        // buffers are hot per-batch allocations, so rent them and reuse the
        // backing storage across searches.
        int totalChars = 0;
        for (int i = 0; i < paths.Count; i++)
            totalChars = checked(totalChars + paths[i].Length);

        var concat = ArrayPool<char>.Shared.Rent(totalChars);
        var lengths = ArrayPool<uint>.Shared.Rent(paths.Count);
        try
        {
            var concatSpan = concat.AsSpan(0, totalChars);
            var lengthsSpan = lengths.AsSpan(0, paths.Count);
            int cursor = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                var s = paths[i];
                s.AsSpan().CopyTo(concatSpan[cursor..]);
                lengthsSpan[i] = (uint)s.Length;
                cursor += s.Length;
            }

            var gcHandle = GCHandle.Alloc(sink, GCHandleType.Normal);
            try
            {
                fixed (char* pConcat = concatSpan)
                fixed (uint* pLengths = lengthsSpan)
                {
                    int ret = QgSessionScanPathsParallelEx(
                        session.Handle,
                        pConcat,
                        pLengths,
                        (nuint)paths.Count,
                        (uint)Math.Max(0, threadCount),
                        cancelFlag,
                        &OnParallelMatchTrampoline,
                        &OnParallelFileDoneTrampoline,
                        (void*)GCHandle.ToIntPtr(gcHandle));

                    if (sink.CapturedException is { } ex)
                        throw new InvalidOperationException("Parallel sink threw inside native callback", ex);

                    return ret;
                }
            }
            finally
            {
                gcHandle.Free();
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(lengths);
            ArrayPool<char>.Shared.Return(concat);
        }
    }

    // ── Streaming scanner high-level API ──

    /// <summary>
    /// Creates a streaming scanner with persistent worker threads that pull
    /// paths from an internal queue. Use <see cref="PushPaths"/> to feed work
    /// and <see cref="FinishStreamingScanner"/> to wait for completion.
    /// </summary>
    public static unsafe IntPtr CreateStreamingScanner(
        NativeSession session,
        int threadCount,
        int* cancelFlag,
        IParallelSink sink,
        out GCHandle sinkHandle)
    {
        if (!IsAvailable) { sinkHandle = default; return IntPtr.Zero; }

        sinkHandle = GCHandle.Alloc(sink, GCHandleType.Normal);
        var scanner = QgCreateStreamingScanner(
            session.Handle,
            (uint)Math.Max(0, threadCount),
            cancelFlag,
            &OnParallelMatchTrampoline,
            &OnParallelFileDoneTrampoline,
            (void*)GCHandle.ToIntPtr(sinkHandle));

        if (scanner == IntPtr.Zero)
        {
            sinkHandle.Free();
            sinkHandle = default;
        }
        return scanner;
    }

    /// <summary>
    /// Push a batch of paths into the streaming scanner's work queue.
    /// Workers pick them up immediately without waiting for a full batch.
    /// </summary>
    public static unsafe int PushPaths(IntPtr scanner, IReadOnlyList<string> paths, int fileIndexBase)
    {
        if (scanner == IntPtr.Zero || paths.Count == 0) return StatusOk;

        int totalChars = 0;
        for (int i = 0; i < paths.Count; i++)
            totalChars = checked(totalChars + paths[i].Length);

        var concat = ArrayPool<char>.Shared.Rent(totalChars);
        var lengths = ArrayPool<uint>.Shared.Rent(paths.Count);
        try
        {
            var concatSpan = concat.AsSpan(0, totalChars);
            var lengthsSpan = lengths.AsSpan(0, paths.Count);
            int cursor = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                var s = paths[i];
                s.AsSpan().CopyTo(concatSpan[cursor..]);
                lengthsSpan[i] = (uint)s.Length;
                cursor += s.Length;
            }

            fixed (char* pConcat = concatSpan)
            fixed (uint* pLengths = lengthsSpan)
            {
                return QgScannerPushPaths(
                    scanner,
                    pConcat,
                    pLengths,
                    (nuint)paths.Count,
                    (uint)fileIndexBase);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(lengths);
            ArrayPool<char>.Shared.Return(concat);
        }
    }

    /// <summary>
    /// Signal no more paths and wait for all workers to drain.
    /// </summary>
    public static unsafe int FinishStreamingScanner(IntPtr scanner)
    {
        if (scanner == IntPtr.Zero) return StatusOk;
        return QgScannerFinish(scanner);
    }

    /// <summary>
    /// Destroy a streaming scanner. The native side also joins workers as a
    /// last line of defense for cancellation cleanup paths.
    /// </summary>
    public static unsafe void DestroyStreamingScanner(IntPtr scanner)
    {
        if (scanner != IntPtr.Zero) QgScannerDestroy(scanner);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int OnParallelMatchTrampoline(void* ctx, uint fileIndex, QgMatchView* m)
    {
        try
        {
            var handle = GCHandle.FromIntPtr((IntPtr)ctx);
            if (handle.Target is IParallelSink sink) return sink.OnMatchForFile(fileIndex, m);
            return 1;
        }
        catch (Exception ex)
        {
            try
            {
                var handle = GCHandle.FromIntPtr((IntPtr)ctx);
                if (handle.Target is IStreamingSink s) s.CapturedException = ex;
            }
            catch { /* swallow */ }
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe void OnParallelFileDoneTrampoline(void* ctx, uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
    {
        try
        {
            var handle = GCHandle.FromIntPtr((IntPtr)ctx);
            if (handle.Target is IParallelSink sink) sink.OnFileDone(fileIndex, status, fileLength, lastModifiedFileTime);
        }
        catch (Exception ex)
        {
            try
            {
                var handle = GCHandle.FromIntPtr((IntPtr)ctx);
                if (handle.Target is IStreamingSink s) s.CapturedException = ex;
            }
            catch { /* swallow */ }
        }
    }
}

/// <summary>
/// Thread-safe handle to a pre-compiled native search session.
/// Dispose when the search is complete to free native memory.
/// </summary>
internal sealed class NativeSession : IDisposable
{
    internal IntPtr Handle;

    /// <summary>
    /// Estimated native memory held by the Rust compiled regex session.
    /// This is a conservative estimate — Rust regex automata typically
    /// allocate 1–10 MB of DFA/NFA tables. We use a fixed estimate so
    /// the .NET GC is aware of unmanaged memory pressure.
    /// </summary>
    private const long EstimatedNativeBytes = 2 * 1024 * 1024; // 2 MB

    internal NativeSession(IntPtr handle)
    {
        Handle = handle;
        GC.AddMemoryPressure(EstimatedNativeBytes);
    }

    public void Dispose()
    {
        var h = System.Threading.Interlocked.Exchange(ref Handle, IntPtr.Zero);
        if (h != IntPtr.Zero)
        {
            unsafe { NativeSearcher.QgFreeSessionPublic(h); }
            GC.RemoveMemoryPressure(EstimatedNativeBytes);
        }
    }
}

/// <summary>
/// Receives streaming matches from the native engine. Implementations must
/// copy any pointer-backed data inside <see cref="OnMatch"/> before returning.
/// Return 0 to continue scanning, non-zero to stop.
/// </summary>
internal interface IStreamingSink
{
    unsafe int OnMatch(NativeSearcher.QgMatchView* m);
    Exception? CapturedException { get; set; }
    string? ErrorMessage { get; set; }
}

internal sealed class NativeSearchOutcome
{
    public enum OutcomeKind { Unavailable, Matches, Skipped, Error, Cancelled }
    public OutcomeKind Kind { get; }
    public IReadOnlyList<SearchResult> Results { get; }
    public string? Reason { get; }

    private NativeSearchOutcome(OutcomeKind kind, IReadOnlyList<SearchResult>? results, string? reason)
    {
        Kind = kind;
        Results = results ?? Array.Empty<SearchResult>();
        Reason = reason;
    }

    public static readonly NativeSearchOutcome _Unavailable =
        new(OutcomeKind.Unavailable, null, null);
    public static NativeSearchOutcome Unavailable => _Unavailable;
    public static NativeSearchOutcome Skipped(string reason) => new(OutcomeKind.Skipped, null, reason);
    public static NativeSearchOutcome Error(string reason) => new(OutcomeKind.Error, null, reason);
    public static NativeSearchOutcome Cancelled() => new(OutcomeKind.Cancelled, null, null);

    public static unsafe NativeSearchOutcome FromBuffer(
        string filePath,
        NativeSearcher.QgResult result,
        int contextLines)
    {
        if (result.Buffer == IntPtr.Zero || result.BufferLen == 0)
            return new NativeSearchOutcome(OutcomeKind.Matches, Array.Empty<SearchResult>(), null);

        // Defensive: clamp buffer length to int range. A buffer larger than 2 GiB
        // would indicate a runaway native allocation — bail to managed scan.
        if (result.BufferLen > (nuint)int.MaxValue)
        {
            LogService.Instance.Warning("NativeSearcher", $"Native buffer too large ({result.BufferLen} bytes) for {filePath}");
            return new NativeSearchOutcome(OutcomeKind.Error, null, "buffer too large");
        }

        var span = new ReadOnlySpan<byte>((void*)result.Buffer, (int)result.BufferLen);
        var reader = new BufferReader(span);
        try
        {
            if (!reader.TryReadU32(out uint count))
                return new NativeSearchOutcome(OutcomeKind.Error, null, "truncated count");

            var list = new List<SearchResult>(Math.Min((int)count, 1024));
            for (uint i = 0; i < count; i++)
            {
                if (!reader.TryReadU64(out ulong lineNumber)
                    || !reader.TryReadU32(out uint matchStart)
                    || !reader.TryReadU32(out uint sourceMatchStart)
                    || !reader.TryReadU32(out uint matchLen)
                    || !reader.TryReadU32(out uint lineLen)
                    || !reader.TryReadUtf8String(lineLen, out string? line))
                {
                    LogService.Instance.Warning("NativeSearcher", $"Truncated record {i}/{count} in native buffer for {filePath}");
                    break;
                }

                if (!reader.TryReadU32(out uint beforeCount))
                    break;
                var before = new List<string>((int)Math.Min(beforeCount, 64));
                bool ctxOk = true;
                for (uint b = 0; b < beforeCount && ctxOk; b++)
                {
                    if (!reader.TryReadU32(out uint blen) || !reader.TryReadUtf8String(blen, out string? bs))
                    { ctxOk = false; break; }
                    before.Add(bs!);
                }
                if (!ctxOk) break;

                if (!reader.TryReadU32(out uint afterCount))
                    break;
                var after = new List<string>((int)Math.Min(afterCount, 64));
                for (uint a = 0; a < afterCount && ctxOk; a++)
                {
                    if (!reader.TryReadU32(out uint alen) || !reader.TryReadUtf8String(alen, out string? aS))
                    { ctxOk = false; break; }
                    after.Add(aS!);
                }
                if (!ctxOk) break;

                // Defensive numeric clamps: line numbers / columns from Rust are u64/u32
                // but UI/SearchResult are int. Negative values would crash callers.
                int lineNum = lineNumber > int.MaxValue ? int.MaxValue : (int)lineNumber;
                int col = matchStart > int.MaxValue ? 0 : (int)matchStart;
                int sourceCol = sourceMatchStart > int.MaxValue ? col : (int)sourceMatchStart;
                int mlen = matchLen > int.MaxValue ? 0 : (int)matchLen;

                list.Add(new SearchResult(
                    FilePath: filePath,
                    LineNumber: lineNum,
                    MatchLine: line!,
                    MatchStartColumn: col,
                    MatchLength: mlen,
                    ContextBefore: before,
                    ContextAfter: after)
                { SourceMatchStartColumn = sourceCol });
            }
            return new NativeSearchOutcome(OutcomeKind.Matches, list, null);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("NativeSearcher", $"Failed to parse native buffer for {filePath}", ex);
            return new NativeSearchOutcome(OutcomeKind.Error, null, $"buffer parse failed: {ex.Message}");
        }
    }


    internal ref struct BufferReader
    {
        private ReadOnlySpan<byte> _data;
        private int _pos;
        public BufferReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

        public bool TryReadU32(out uint value)
        {
            if (_pos < 0 || _pos + 4 > _data.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return true;
        }

        public bool TryReadU64(out ulong value)
        {
            if (_pos < 0 || _pos + 8 > _data.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return true;
        }

        public bool TryReadUtf8String(uint len, out string? value)
        {
            if (len == 0) { value = string.Empty; return true; }
            // Reject lengths that would overflow int or exceed the remaining buffer.
            if (len > int.MaxValue) { value = null; return false; }
            int ilen = (int)len;
            if (_pos < 0 || _pos + ilen > _data.Length || _pos + ilen < 0) { value = null; return false; }
            value = System.Text.Encoding.UTF8.GetString(_data.Slice(_pos, ilen));
            _pos += ilen;
            return true;
        }
    }
}
