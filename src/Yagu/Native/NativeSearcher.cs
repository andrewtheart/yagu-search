using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yagu.Models;
using Yagu.Services.Logging;
using Yagu.Services;
using System.Runtime.CompilerServices;
using System.Text;

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
        public byte OmitLineBytes;
        public uint ContextBefore;
        public uint ContextAfter;
        public ulong MaxResults;
        public ulong MaxFileSize;
        public byte SkipHidden;
        // Multiline (cross-line, ripgrep -U) fields — ABI v6. Default 0 keeps
        // the per-line path. Native multiline wiring lands in a later Phase 2 slice.
        public byte MultiLine;
        public byte MultiLineDotAll;
        public byte MultilineEngine;
        // ABI v8 — removable/optical roots use owned reads instead of source-file mmap.
        public byte AvoidSourceMemoryMap;
        // ABI v8 — cooperative per-file deadline in seconds; 0 disables.
        public ushort FileIoTimeoutSeconds;
        // ABI v8 — per-line match cap; 0 = unlimited. Bounds a match-everything
        // pattern (e.g. the regex ".") on a very long minified line.
        public ulong MaxMatchesPerLine;
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
    internal const int StatusHiddenSkipped = 7;
    internal const int StatusIoTimeout = 8;

    [LibraryImport(DllName, EntryPoint = "qg_abi_version")]
    private static partial uint QgAbiVersion();

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

    /// <summary>
    /// Span-carrying sibling of <see cref="QgMatchView"/> for the multiline
    /// (cross-line) engine. The leading fields are byte-identical to
    /// <see cref="QgMatchView"/> (so the single-line decode is reused for the
    /// START/display line); <see cref="EndLine"/>/<see cref="EndCol"/> carry the
    /// true span end. <see cref="EndCol"/> is a precomputed UTF-16 column on the
    /// end line (Rust computes it — the record ships only the start line's bytes).
    /// Layout is pinned by <c>qg_multiline_match_view_abi_layout_is_stable</c> in
    /// yagu-core (size 104, align 8).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct QgMultilineMatchView
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
        public ulong EndLine;
        public uint EndCol;
    }

    [LibraryImport(DllName, EntryPoint = "qg_search_file_stream_multiline")]
    private static unsafe partial int QgSearchFileStreamMultiline(
        char* pathUtf16,
        nuint pathLen,
        byte* patternUtf8,
        nuint patternLen,
        QgOptions* options,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, QgMultilineMatchView*, int> onMatch,
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

    internal unsafe interface INativeApi
    {
        int SearchFileStream(
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

        int SearchFileStreamMultiline(
            char* pathUtf16,
            nuint pathLen,
            byte* patternUtf8,
            nuint patternLen,
            QgOptions* options,
            int* cancelFlag,
            delegate* unmanaged[Cdecl]<void*, QgMultilineMatchView*, int> onMatch,
            void* onMatchCtx,
            int* outStatus,
            byte** outErrorMsg,
            nuint* outErrorMsgLen);

        IntPtr CreateSession(
            byte* patternUtf8,
            nuint patternLen,
            QgOptions* options,
            byte** outErrorMsg,
            nuint* outErrorMsgLen);

        int SearchFileStreamWithSession(
            IntPtr session,
            char* pathUtf16,
            nuint pathLen,
            int* cancelFlag,
            delegate* unmanaged[Cdecl]<void*, QgMatchView*, int> onMatch,
            void* onMatchCtx,
            int* outStatus,
            byte** outErrorMsg,
            nuint* outErrorMsgLen);

        IntPtr CreateStreamingScanner(
            IntPtr session,
            uint threadCount,
            int* cancelFlag,
            delegate* unmanaged[Cdecl]<void*, uint, QgMatchView*, int> onMatch,
            delegate* unmanaged[Cdecl]<void*, uint, int, ulong, ulong, void> onFileDone,
            void* onMatchCtx);

        void FreeBuffer(byte* ptr, nuint len);
    }

    private sealed unsafe class PInvokeNativeApi : INativeApi
    {
        internal static readonly PInvokeNativeApi Instance = new();

        private PInvokeNativeApi() { }

        public int SearchFileStream(
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
            nuint* outErrorMsgLen)
            => QgSearchFileStream(pathUtf16, pathLen, patternUtf8, patternLen, options, cancelFlag,
                onMatch, onMatchCtx, outStatus, outErrorMsg, outErrorMsgLen);

        public int SearchFileStreamMultiline(
            char* pathUtf16,
            nuint pathLen,
            byte* patternUtf8,
            nuint patternLen,
            QgOptions* options,
            int* cancelFlag,
            delegate* unmanaged[Cdecl]<void*, QgMultilineMatchView*, int> onMatch,
            void* onMatchCtx,
            int* outStatus,
            byte** outErrorMsg,
            nuint* outErrorMsgLen)
            => QgSearchFileStreamMultiline(pathUtf16, pathLen, patternUtf8, patternLen, options, cancelFlag,
                onMatch, onMatchCtx, outStatus, outErrorMsg, outErrorMsgLen);

        public IntPtr CreateSession(
            byte* patternUtf8,
            nuint patternLen,
            QgOptions* options,
            byte** outErrorMsg,
            nuint* outErrorMsgLen)
            => QgCreateSession(patternUtf8, patternLen, options, outErrorMsg, outErrorMsgLen);

        public int SearchFileStreamWithSession(
            IntPtr session,
            char* pathUtf16,
            nuint pathLen,
            int* cancelFlag,
            delegate* unmanaged[Cdecl]<void*, QgMatchView*, int> onMatch,
            void* onMatchCtx,
            int* outStatus,
            byte** outErrorMsg,
            nuint* outErrorMsgLen)
            => QgSessionSearchFileStream(session, pathUtf16, pathLen, cancelFlag,
                onMatch, onMatchCtx, outStatus, outErrorMsg, outErrorMsgLen);

        public IntPtr CreateStreamingScanner(
            IntPtr session,
            uint threadCount,
            int* cancelFlag,
            delegate* unmanaged[Cdecl]<void*, uint, QgMatchView*, int> onMatch,
            delegate* unmanaged[Cdecl]<void*, uint, int, ulong, ulong, void> onFileDone,
            void* onMatchCtx)
            => QgCreateStreamingScanner(session, threadCount, cancelFlag, onMatch, onFileDone, onMatchCtx);

        public void FreeBuffer(byte* ptr, nuint len) => QgFreeBuffer(ptr, len);
    }

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

        return TryReadAbiVersion(QgAbiVersion);
    }

    internal static bool TryReadAbiVersion(Func<uint> readAbiVersion)
    {
        try
        {
            return readAbiVersion() == 8;
        }
        catch (DllNotFoundException) { YaguLog.For("NativeSearcher").LogInformation("yagu_core.dll not found"); return false; }
        catch (BadImageFormatException ex) { YaguLog.For("NativeSearcher").LogWarning(ex, "yagu_core.dll bad image format"); return false; }
        catch (EntryPointNotFoundException ex) { YaguLog.For("NativeSearcher").LogWarning(ex, "yagu_core.dll missing entry point"); return false; }
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

    private static uint NativeContextLineCount(SearchOptions options)
        => (uint)Math.Max(0, options.ContextLines);

    internal static QgOptions CreateOptions(SearchOptions options)
    {
        uint contextLineCount = NativeContextLineCount(options);
        return new QgOptions
        {
            CaseSensitive = (byte)(options.CaseSensitive ? 1 : 0),
            UseRegex = (byte)(options.UseRegex ? 1 : 0),
            SkipBinary = (byte)(options.SkipBinary ? 1 : 0),
            OmitLineBytes = 0,
            ContextBefore = contextLineCount,
            ContextAfter = contextLineCount,
            MaxResults = EffectivePerFileCap(options),
            MaxFileSize = options.MaxFileSizeBytes > 0 ? (ulong)options.MaxFileSizeBytes : 0UL,
            SkipHidden = (byte)(options.SearchHiddenFiles ? 0 : 1),
            // Native multiline is gated off in Phase 2 slice 1 (the C# gate forces
            // managed for Multiline searches), so these stay 0 until the native
            // whole-buffer engine is FFI-wired. Keeping the fields marshalled here
            // ensures the QgOptions layout matches the Rust ABI v6 struct.
            MultiLine = 0,
            MultiLineDotAll = 0,
            MultilineEngine = 0,
            AvoidSourceMemoryMap = (byte)(options.AvoidSourceMemoryMap ? 1 : 0),
            FileIoTimeoutSeconds = (ushort)Math.Clamp(options.FileIoTimeoutSeconds, 1, 600),
            MaxMatchesPerLine = options.MaxMatchesPerLine > 0 ? (ulong)options.MaxMatchesPerLine : 0UL,
        };
    }

    /// <summary>
    /// Builds <see cref="QgOptions"/> for the native whole-buffer multiline
    /// engine (<see cref="SearchFileStreamMultiline"/>). Sets the multiline flags
    /// and — critically — carries the dedicated multiline size cap
    /// (<see cref="SearchOptions.MaxMultilineBytes"/>) as <c>MaxFileSize</c>, so
    /// the native path skips the EXACT same over-cap files the managed Phase-1
    /// path does (parity-critical, plan §9).
    /// </summary>
    internal static QgOptions CreateMultilineOptions(SearchOptions options)
    {
        uint contextLineCount = NativeContextLineCount(options);
        return new QgOptions
        {
            CaseSensitive = (byte)(options.CaseSensitive ? 1 : 0),
            UseRegex = (byte)(options.UseRegex ? 1 : 0),
            SkipBinary = (byte)(options.SkipBinary ? 1 : 0),
            OmitLineBytes = 0,
            ContextBefore = contextLineCount,
            ContextAfter = contextLineCount,
            MaxResults = EffectivePerFileCap(options),
            // The multiline cap is measured in raw file bytes, identical to the
            // managed path's MaxMultilineBytes check.
            MaxFileSize = options.MaxMultilineBytes > 0 ? (ulong)options.MaxMultilineBytes : 0UL,
            SkipHidden = (byte)(options.SearchHiddenFiles ? 0 : 1),
            MultiLine = 1,
            MultiLineDotAll = (byte)(options.MultilineDotAll ? 1 : 0),
            // 0 = hand-rolled regex::bytes (default), 1 = grep-searcher. Both engines are
            // compiled into the DLL (grep_crates feature); the selection is a runtime switch.
            MultilineEngine = (byte)options.MultilineEngine,
            // Per-line cap is meaningless for the whole-buffer multiline engine.
            MaxMatchesPerLine = 0,
        };
    }

    internal static void ThrowIfCaptured(INativeSinkState sink, string message)
    {
        Exception? exception = sink.CapturedException;
        if (exception != null) throw new InvalidOperationException(message, exception);
    }

    internal static unsafe void SetInvalidRegexError(
        INativeSinkState sink,
        int status,
        byte* errorMessage,
        nuint errorMessageLength)
    {
        if (status != StatusInvalidRegex) return;
        if (errorMessage == null) return;
        if (errorMessageLength == 0) return;
        sink.ErrorMessage = Encoding.UTF8.GetString(errorMessage, (int)errorMessageLength);
    }

    internal static unsafe void FreeBufferIfPresent(INativeApi nativeApi, byte* buffer, nuint length)
    {
        if (buffer != null) nativeApi.FreeBuffer(buffer, length);
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
        => SearchFileStreamCore(filePath, pattern, options, cancelFlag, sink, IsAvailable, PInvokeNativeApi.Instance);

    internal static unsafe int SearchFileStreamCore(
        string filePath,
        string pattern,
        SearchOptions options,
        int* cancelFlag,
        IStreamingSink sink,
        bool isAvailable,
        INativeApi nativeApi)
    {
        if (!isAvailable) return StatusOpenFailed;

        var ffiOptions = CreateOptions(options);

        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        var handle = GCHandle.Alloc(sink, GCHandleType.Normal);
        int status = StatusOk;
        byte* errMsg = null;
        nuint errMsgLen = 0;
        try
        {
            fixed (char* pPath = filePath)
            fixed (byte* pPattern = patternBytes)
            {
                _ = nativeApi.SearchFileStream(
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

            ThrowIfCaptured(sink, "Streaming sink threw inside native callback");
            SetInvalidRegexError(sink, status, errMsg, errMsgLen);

            return status;
        }
        finally
        {
            FreeBufferIfPresent(nativeApi, errMsg, errMsgLen);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnMatchTrampoline(void* ctx, QgMatchView* m)
        => DispatchMatch(GCHandle.FromIntPtr((IntPtr)ctx), m);

    internal static unsafe int DispatchMatch(GCHandle handle, QgMatchView* match)
    {
        if (handle.Target is not IStreamingSink sink) return 1;
        try
        {
            return sink.OnMatch(match);
        }
        catch (Exception ex)
        {
            CaptureException(sink, ex);
            return 1;
        }
    }

    private static void CaptureException(INativeSinkState sink, Exception exception)
    {
        try { sink.CapturedException = exception; }
        catch { }
    }

    /// <summary>
    /// Native whole-buffer multiline (cross-line) search of a single file — the
    /// Phase 2 engine. Mirrors <see cref="SearchFileStream"/> but drives the
    /// span-carrying <see cref="QgMultilineMatchView"/> callback. Returns the
    /// final native status (StatusOk / StatusInvalidRegex for lookaround /
    /// StatusTooLarge for over-cap / etc.). The pattern is the raw query; a
    /// literal is escaped into the regex engine on the Rust side.
    /// </summary>
    public static unsafe int SearchFileStreamMultiline(
        string filePath,
        string pattern,
        SearchOptions options,
        int* cancelFlag,
        IMultilineStreamingSink sink)
        => SearchFileStreamMultilineCore(filePath, pattern, options, cancelFlag, sink, IsAvailable, PInvokeNativeApi.Instance);

    internal static unsafe int SearchFileStreamMultilineCore(
        string filePath,
        string pattern,
        SearchOptions options,
        int* cancelFlag,
        IMultilineStreamingSink sink,
        bool isAvailable,
        INativeApi nativeApi)
    {
        if (!isAvailable) return StatusOpenFailed;

        var ffiOptions = CreateMultilineOptions(options);
        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        var handle = GCHandle.Alloc(sink, GCHandleType.Normal);
        int status = StatusOk;
        byte* errMsg = null;
        nuint errMsgLen = 0;
        try
        {
            fixed (char* pPath = filePath)
            fixed (byte* pPattern = patternBytes)
            {
                _ = nativeApi.SearchFileStreamMultiline(
                    pPath, (nuint)filePath.Length,
                    pPattern, (nuint)patternBytes.Length,
                    &ffiOptions,
                    cancelFlag,
                    &OnMultilineMatchTrampoline,
                    (void*)GCHandle.ToIntPtr(handle),
                    &status,
                    &errMsg,
                    &errMsgLen);
            }

            ThrowIfCaptured(sink, "Multiline streaming sink threw inside native callback");
            SetInvalidRegexError(sink, status, errMsg, errMsgLen);

            return status;
        }
        finally
        {
            FreeBufferIfPresent(nativeApi, errMsg, errMsgLen);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnMultilineMatchTrampoline(void* ctx, QgMultilineMatchView* m)
        => DispatchMultilineMatch(GCHandle.FromIntPtr((IntPtr)ctx), m);

    internal static unsafe int DispatchMultilineMatch(GCHandle handle, QgMultilineMatchView* match)
    {
        if (handle.Target is not IMultilineStreamingSink sink) return 1;
        try
        {
            return sink.OnMultilineMatch(match);
        }
        catch (Exception ex)
        {
            CaptureException(sink, ex);
            return 1;
        }
    }

    /// <summary>
    /// Create a pre-compiled search session. Returns null if pattern is invalid.
    /// The session is thread-safe and should be reused across all files in a search.
    /// </summary>
    public static unsafe NativeSession? CreateSession(string pattern, SearchOptions options)
    {
        IntPtr handle = CreateSessionHandleCore(pattern, options, IsAvailable, PInvokeNativeApi.Instance);
        if (handle == IntPtr.Zero) return null;
        return new NativeSession(handle);
    }

    internal static unsafe IntPtr CreateSessionHandleCore(
        string pattern,
        SearchOptions options,
        bool isAvailable,
        INativeApi nativeApi)
    {
        if (!isAvailable) return IntPtr.Zero;

        var ffiOptions = CreateOptions(options);

        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        byte* errMsg = null;
        nuint errMsgLen = 0;
        IntPtr handle;
        fixed (byte* pPattern = patternBytes)
        {
            handle = nativeApi.CreateSession(pPattern, (nuint)patternBytes.Length, &ffiOptions, &errMsg, &errMsgLen);
        }
        FreeBufferIfPresent(nativeApi, errMsg, errMsgLen);
        return handle;
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
        => SearchFileStreamWithSessionCore(session, filePath, cancelFlag, sink, PInvokeNativeApi.Instance);

    internal static unsafe int SearchFileStreamWithSessionCore(
        NativeSession session,
        string filePath,
        int* cancelFlag,
        IStreamingSink sink,
        INativeApi nativeApi)
    {
        var gcHandle = GCHandle.Alloc(sink, GCHandleType.Normal);
        int status = StatusOk;
        byte* errMsg = null;
        nuint errMsgLen = 0;
        try
        {
            fixed (char* pPath = filePath)
            {
                _ = nativeApi.SearchFileStreamWithSession(
                    session.Handle,
                    pPath, (nuint)filePath.Length,
                    cancelFlag,
                    &OnMatchTrampoline,
                    (void*)GCHandle.ToIntPtr(gcHandle),
                    &status,
                    &errMsg,
                    &errMsgLen);
            }

            ThrowIfCaptured(sink, "Streaming sink threw inside native callback");
            SetInvalidRegexError(sink, status, errMsg, errMsgLen);

            return status;
        }
        finally
        {
            FreeBufferIfPresent(nativeApi, errMsg, errMsgLen);
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
        => CreateStreamingScannerCore(session, threadCount, cancelFlag, sink, out sinkHandle,
            IsAvailable, PInvokeNativeApi.Instance);

    internal static unsafe IntPtr CreateStreamingScannerCore(
        NativeSession session,
        int threadCount,
        int* cancelFlag,
        IParallelSink sink,
        out GCHandle sinkHandle,
        bool isAvailable,
        INativeApi nativeApi)
    {
        if (!isAvailable) { sinkHandle = default; return IntPtr.Zero; }

        sinkHandle = GCHandle.Alloc(sink, GCHandleType.Normal);
        var scanner = nativeApi.CreateStreamingScanner(
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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnParallelMatchTrampoline(void* ctx, uint fileIndex, QgMatchView* m)
        => DispatchParallelMatch(GCHandle.FromIntPtr((IntPtr)ctx), fileIndex, m);

    internal static unsafe int DispatchParallelMatch(GCHandle handle, uint fileIndex, QgMatchView* match)
    {
        if (handle.Target is not IParallelSink sink) return 1;
        try
        {
            return sink.OnMatchForFile(fileIndex, match);
        }
        catch (Exception ex)
        {
            CaptureException(sink, ex);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnParallelFileDoneTrampoline(void* ctx, uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
        => DispatchParallelFileDone(GCHandle.FromIntPtr((IntPtr)ctx), fileIndex, status, fileLength, lastModifiedFileTime);

    internal static void DispatchParallelFileDone(
        GCHandle handle,
        uint fileIndex,
        int status,
        ulong fileLength,
        ulong lastModifiedFileTime)
    {
        if (handle.Target is not IParallelSink sink) return;
        try
        {
            sink.OnFileDone(fileIndex, status, fileLength, lastModifiedFileTime);
        }
        catch (Exception ex)
        {
            CaptureException(sink, ex);
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
        var h = Interlocked.Exchange(ref Handle, IntPtr.Zero);
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
internal interface IStreamingSink : INativeSinkState
{
    unsafe int OnMatch(NativeSearcher.QgMatchView* m);
}

/// <summary>
/// Receives streaming cross-line (multiline) matches from the native engine.
/// Like <see cref="IStreamingSink"/> but each view carries the true span end.
/// Implementations must copy pointer-backed data before returning.
/// </summary>
internal interface IMultilineStreamingSink : INativeSinkState
{
    unsafe int OnMultilineMatch(NativeSearcher.QgMultilineMatchView* m);
}

internal interface INativeSinkState
{
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
            YaguLog.For("NativeSearcher").LogWarning("Native buffer too large ({BufferLen} bytes) for {File}", result.BufferLen, filePath);
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
                    || !reader.TryReadUtf8String(lineLen, out string line))
                {
                    YaguLog.For("NativeSearcher").LogWarning("Truncated record {Index}/{Count} in native buffer for {File}", i, count, filePath);
                    break;
                }

                if (!reader.TryReadU32(out uint beforeCount)) break;
                if (!TryReadContext(ref reader, beforeCount, out List<string> before)) break;

                if (!reader.TryReadU32(out uint afterCount)) break;
                if (!TryReadContext(ref reader, afterCount, out List<string> after)) break;

                // Defensive numeric clamps: line numbers / columns from Rust are u64/u32
                // but UI/SearchResult are int. Negative values would crash callers.
                int lineNum = lineNumber > int.MaxValue ? int.MaxValue : (int)lineNumber;
                int col = matchStart > int.MaxValue ? 0 : (int)matchStart;
                int sourceCol = sourceMatchStart > int.MaxValue ? col : (int)sourceMatchStart;
                int mlen = matchLen > int.MaxValue ? 0 : (int)matchLen;

                list.Add(new SearchResult(
                    FilePath: filePath,
                    LineNumber: lineNum,
                    MatchLine: line,
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
            YaguLog.For("NativeSearcher").LogWarning(ex, "Failed to parse native buffer for {File}", filePath);
            return new NativeSearchOutcome(OutcomeKind.Error, null, $"buffer parse failed: {ex.Message}");
        }
    }

    internal static bool TryReadContext(
        ref BufferReader reader,
        uint count,
        out List<string> lines)
    {
        lines = new List<string>((int)Math.Min(count, 64));
        for (uint index = 0; index < count; index++)
        {
            if (!reader.TryReadU32(out uint length)) return false;
            if (!reader.TryReadUtf8String(length, out string line)) return false;
            lines.Add(line);
        }
        return true;
    }


    internal ref struct BufferReader
    {
        private ReadOnlySpan<byte> _data;
        private int _pos;
        public BufferReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

        public bool TryReadU32(out uint value)
        {
            if (_data.Length - _pos < 4) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return true;
        }

        public bool TryReadU64(out ulong value)
        {
            if (_data.Length - _pos < 8) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return true;
        }

        public bool TryReadUtf8String(uint len, out string value)
        {
            if (len == 0) { value = string.Empty; return true; }
            // Remaining bytes are always <= int.MaxValue, so this single comparison
            // rejects both oversized lengths and truncated buffers without overflow.
            if (len > (uint)(_data.Length - _pos)) { value = string.Empty; return false; }
            int ilen = (int)len;
            value = Encoding.UTF8.GetString(_data.Slice(_pos, ilen));
            _pos += ilen;
            return true;
        }
    }
}
