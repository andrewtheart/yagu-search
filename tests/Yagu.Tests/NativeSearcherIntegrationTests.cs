using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Yagu.Models;
using Yagu.Native;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class NativeSearcherIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "yagu-native-searcher-" + Guid.NewGuid().ToString("N"));

    public NativeSearcherIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void ProfilingNativeLibrary_IsAvailableWithSymbolsAndCurrentAbi()
    {
        string nativeDll = Path.Combine(AppContext.BaseDirectory, "yagu_core.dll");
        string nativePdb = Path.Combine(AppContext.BaseDirectory, "yagu_core.pdb");

        Assert.True(File.Exists(nativeDll), nativeDll);
        Assert.True(File.Exists(nativePdb), nativePdb);
        Assert.True(new FileInfo(nativeDll).Length > 0);
        Assert.True(new FileInfo(nativePdb).Length > 0);
        Assert.True(NativeSearcher.IsAvailable);
    }

    [Fact]
    public void CreateOptions_MapsEnabledValuesAndClampsTimeout()
    {
        var options = Options(
            "needle",
            caseSensitive: true,
            useRegex: true,
            skipBinary: true,
            contextLines: 2,
            maxResults: 100,
            maxMatchesPerFile: 40,
            maxFileSizeBytes: 1234,
            searchHiddenFiles: false,
            avoidSourceMemoryMap: true,
            fileIoTimeoutSeconds: 700,
            maxMatchesPerLine: 9);

        NativeSearcher.QgOptions actual = NativeSearcher.CreateOptions(options);

        Assert.Equal((byte)1, actual.CaseSensitive);
        Assert.Equal((byte)1, actual.UseRegex);
        Assert.Equal((byte)1, actual.SkipBinary);
        Assert.Equal(2u, actual.ContextBefore);
        Assert.Equal(2u, actual.ContextAfter);
        Assert.Equal(40ul, actual.MaxResults);
        Assert.Equal(1234ul, actual.MaxFileSize);
        Assert.Equal((byte)1, actual.SkipHidden);
        Assert.Equal((byte)0, actual.MultiLine);
        Assert.Equal((byte)1, actual.AvoidSourceMemoryMap);
        Assert.Equal((ushort)600, actual.FileIoTimeoutSeconds);
        Assert.Equal(9ul, actual.MaxMatchesPerLine);
    }

    [Theory]
    [InlineData(0, 0, 0ul)]
    [InlineData(7, 0, 7ul)]
    [InlineData(0, 11, 11ul)]
    [InlineData(7, 11, 7ul)]
    public void CreateOptions_MapsDisabledValuesAndEffectivePerFileCap(
        int perFile,
        int global,
        ulong expectedCap)
    {
        NativeSearcher.QgOptions actual = NativeSearcher.CreateOptions(Options(
            "needle",
            contextLines: -1,
            maxMatchesPerFile: perFile,
            maxResults: global,
            fileIoTimeoutSeconds: 0));

        Assert.Equal((byte)0, actual.CaseSensitive);
        Assert.Equal((byte)0, actual.UseRegex);
        Assert.Equal((byte)0, actual.SkipBinary);
        Assert.Equal(0u, actual.ContextBefore);
        Assert.Equal(expectedCap, actual.MaxResults);
        Assert.Equal(0ul, actual.MaxFileSize);
        Assert.Equal((byte)0, actual.SkipHidden);
        Assert.Equal((byte)0, actual.AvoidSourceMemoryMap);
        Assert.Equal((ushort)1, actual.FileIoTimeoutSeconds);
        Assert.Equal(0ul, actual.MaxMatchesPerLine);
    }

    [Theory]
    [InlineData(false, 0L, MultilineEngineKind.Regex)]
    [InlineData(true, 4096L, MultilineEngineKind.Grep)]
    public void CreateMultilineOptions_MapsDedicatedFields(
        bool dotAll,
        long maxBytes,
        MultilineEngineKind engine)
    {
        NativeSearcher.QgOptions actual = NativeSearcher.CreateMultilineOptions(Options(
            "foo.bar",
            useRegex: true,
            multiline: true,
            multilineDotAll: dotAll,
            maxMultilineBytes: maxBytes,
            multilineEngine: engine));

        Assert.Equal((byte)1, actual.MultiLine);
        Assert.Equal((byte)(dotAll ? 1 : 0), actual.MultiLineDotAll);
        Assert.Equal((byte)engine, actual.MultilineEngine);
        Assert.Equal(maxBytes > 0 ? (ulong)maxBytes : 0ul, actual.MaxFileSize);
        Assert.Equal(0ul, actual.MaxMatchesPerLine);
    }

    [Fact]
    public void CreateMultilineOptions_MapsEnabledCommonFlags()
    {
        NativeSearcher.QgOptions actual = NativeSearcher.CreateMultilineOptions(Options(
            "literal",
            caseSensitive: true,
            useRegex: false,
            skipBinary: true,
            contextLines: 0,
            maxResults: 0,
            searchHiddenFiles: false,
            multiline: true));

        Assert.Equal((byte)1, actual.CaseSensitive);
        Assert.Equal((byte)0, actual.UseRegex);
        Assert.Equal((byte)1, actual.SkipBinary);
        Assert.Equal(0u, actual.ContextBefore);
        Assert.Equal(0ul, actual.MaxResults);
        Assert.Equal((byte)1, actual.SkipHidden);
    }

    [Fact]
    public unsafe void CoreMethods_UnavailableReturnWithoutCallingNative()
    {
        var nativeApi = new FakeNativeApi();
        var sink = new RecordingStreamingSink();
        var multilineSink = new RecordingMultilineSink();
        var parallelSink = new RecordingParallelSink();
        SearchOptions options = Options("needle");
        int cancel = 0;

        Assert.Equal(NativeSearcher.StatusOpenFailed,
            NativeSearcher.SearchFileStreamCore("unused", "needle", options, &cancel, sink, false, nativeApi));
        Assert.Equal(NativeSearcher.StatusOpenFailed,
            NativeSearcher.SearchFileStreamMultilineCore("unused", "needle", options, &cancel, multilineSink, false, nativeApi));
        Assert.Equal(IntPtr.Zero, NativeSearcher.CreateSessionHandleCore("needle", options, false, nativeApi));
        Assert.Equal(IntPtr.Zero,
            NativeSearcher.CreateStreamingScannerCore(null!, 1, &cancel, parallelSink, out GCHandle handle, false, nativeApi));
        Assert.False(handle.IsAllocated);
        Assert.Equal(0, nativeApi.CallCount);
    }

    [Fact]
    public unsafe void CoreMethods_EmptyPathsAndPatternsReachNativeBoundary()
    {
        var nativeApi = new FakeNativeApi { SessionHandle = new IntPtr(123) };
        var sink = new RecordingStreamingSink();
        var multilineSink = new RecordingMultilineSink();
        SearchOptions options = Options(string.Empty);
        int cancel = 0;
        using NativeSession session = Assert.IsType<NativeSession>(
            NativeSearcher.CreateSession("needle", Options("needle")));

        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.SearchFileStreamCore(string.Empty, string.Empty, options, &cancel, sink, true, nativeApi));
        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.SearchFileStreamMultilineCore(
                string.Empty,
                string.Empty,
                options,
                &cancel,
                multilineSink,
                true,
                nativeApi));
        Assert.Equal(new IntPtr(123),
            NativeSearcher.CreateSessionHandleCore(string.Empty, options, true, nativeApi));
        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.SearchFileStreamWithSessionCore(
                session,
                string.Empty,
                &cancel,
                sink,
                nativeApi));
        Assert.Equal(4, nativeApi.CallCount);
    }

    [Fact]
    public unsafe void StreamingCores_NullPathsFailBeforeCallingNative()
    {
        var nativeApi = new FakeNativeApi();
        var sink = new RecordingStreamingSink();
        var multilineSink = new RecordingMultilineSink();
        SearchOptions options = Options("needle");
        int cancel = 0;
        using NativeSession session = Assert.IsType<NativeSession>(
            NativeSearcher.CreateSession("needle", options));

        Exception? directFailure = null;
        try { NativeSearcher.SearchFileStreamCore(null!, "needle", options, &cancel, sink, true, nativeApi); }
        catch (Exception ex) { directFailure = ex; }
        Assert.IsType<NullReferenceException>(directFailure);

        Exception? multilineFailure = null;
        try
        {
            NativeSearcher.SearchFileStreamMultilineCore(
                null!,
                "needle",
                options,
                &cancel,
                multilineSink,
                true,
                nativeApi);
        }
        catch (Exception ex) { multilineFailure = ex; }
        Assert.IsType<NullReferenceException>(multilineFailure);

        Exception? sessionFailure = null;
        try { NativeSearcher.SearchFileStreamWithSessionCore(session, null!, &cancel, sink, nativeApi); }
        catch (Exception ex) { sessionFailure = ex; }
        Assert.IsType<NullReferenceException>(sessionFailure);

        Assert.Equal(0, nativeApi.CallCount);
    }

    [Fact]
    public void ThrowIfCaptured_CoversEmptyAndCapturedStates()
    {
        var sink = new RecordingStreamingSink();
        NativeSearcher.ThrowIfCaptured(sink, "callback failed");

        sink.CapturedException = new ApplicationException("inner");
        var failure = Assert.Throws<InvalidOperationException>(
            () => NativeSearcher.ThrowIfCaptured(sink, "callback failed"));

        Assert.Equal("callback failed", failure.Message);
        Assert.Same(sink.CapturedException, failure.InnerException);
    }

    [Theory]
    [InlineData(NativeSearcher.StatusOk, true, 1, null)]
    [InlineData(NativeSearcher.StatusInvalidRegex, false, 1, null)]
    [InlineData(NativeSearcher.StatusInvalidRegex, true, 0, null)]
    [InlineData(NativeSearcher.StatusInvalidRegex, true, 1, "x")]
    public unsafe void SetInvalidRegexError_RequiresStatusPointerAndLength(
        int status,
        bool hasBuffer,
        int length,
        string? expected)
    {
        var sink = new RecordingStreamingSink();
        byte value = (byte)'x';
        byte* buffer = hasBuffer ? &value : null;

        NativeSearcher.SetInvalidRegexError(sink, status, buffer, (nuint)length);

        Assert.Equal(expected, sink.ErrorMessage);
    }

    [Fact]
    public unsafe void FreeBufferIfPresent_OnlyFreesNonNullBuffers()
    {
        var nativeApi = new FakeNativeApi();
        NativeSearcher.FreeBufferIfPresent(nativeApi, null, 0);
        Assert.Equal(0, nativeApi.FreedBuffers);

        byte* allocation = (byte*)Marshal.AllocHGlobal(1);
        NativeSearcher.FreeBufferIfPresent(nativeApi, allocation, 1);
        Assert.Equal(1, nativeApi.FreedBuffers);
    }

    [Theory]
    [InlineData(FakeErrorPayload.None, null, 0)]
    [InlineData(FakeErrorPayload.Empty, null, 3)]
    [InlineData(FakeErrorPayload.Text, "native error", 3)]
    public unsafe void SearchCores_HandleEveryErrorBufferShape(
        FakeErrorPayload payload,
        string? expectedMessage,
        int expectedFrees)
    {
        var nativeApi = new FakeNativeApi
        {
            Status = NativeSearcher.StatusInvalidRegex,
            ErrorPayload = payload,
        };
        SearchOptions options = Options("(", useRegex: true);
        string path = Write("core.txt", "needle\n");
        int cancel = 0;
        using NativeSession session = Assert.IsType<NativeSession>(NativeSearcher.CreateSession("needle", Options("needle")));
        var directSink = new RecordingStreamingSink();
        var multilineSink = new RecordingMultilineSink();
        var sessionSink = new RecordingStreamingSink();

        Assert.Equal(NativeSearcher.StatusInvalidRegex,
            NativeSearcher.SearchFileStreamCore(path, "(", options, &cancel, directSink, true, nativeApi));
        Assert.Equal(NativeSearcher.StatusInvalidRegex,
            NativeSearcher.SearchFileStreamMultilineCore(path, "(", options, &cancel, multilineSink, true, nativeApi));
        Assert.Equal(NativeSearcher.StatusInvalidRegex,
            NativeSearcher.SearchFileStreamWithSessionCore(session, path, &cancel, sessionSink, nativeApi));

        Assert.Equal(expectedMessage, directSink.ErrorMessage);
        Assert.Equal(expectedMessage, multilineSink.ErrorMessage);
        Assert.Equal(expectedMessage, sessionSink.ErrorMessage);
        Assert.Equal(expectedFrees, nativeApi.FreedBuffers);
    }

    [Fact]
    public unsafe void SearchCores_SurfacePreexistingCapturedExceptionsDeterministically()
    {
        var nativeApi = new FakeNativeApi();
        string path = Write("captured.txt", "needle\n");
        int cancel = 0;
        var directSink = new RecordingStreamingSink
        {
            CapturedException = new ApplicationException("direct"),
        };
        var multilineSink = new RecordingMultilineSink
        {
            CapturedException = new ApplicationException("multiline"),
        };
        using NativeSession session = Assert.IsType<NativeSession>(NativeSearcher.CreateSession("needle", Options("needle")));
        var sessionSink = new RecordingStreamingSink
        {
            CapturedException = new ApplicationException("session"),
        };

        Exception? directFailure = null;
        try { NativeSearcher.SearchFileStreamCore(path, "needle", Options("needle"), &cancel, directSink, true, nativeApi); }
        catch (Exception ex) { directFailure = ex; }
        Assert.Same(directSink.CapturedException, Assert.IsType<InvalidOperationException>(directFailure).InnerException);

        Exception? multilineFailure = null;
        try
        {
            NativeSearcher.SearchFileStreamMultilineCore(
                path,
                "needle",
                Options("needle", multiline: true),
                &cancel,
                multilineSink,
                true,
                nativeApi);
        }
        catch (Exception ex) { multilineFailure = ex; }
        Assert.Same(multilineSink.CapturedException, Assert.IsType<InvalidOperationException>(multilineFailure).InnerException);

        Exception? sessionFailure = null;
        try { NativeSearcher.SearchFileStreamWithSessionCore(session, path, &cancel, sessionSink, nativeApi); }
        catch (Exception ex) { sessionFailure = ex; }
        Assert.Same(sessionSink.CapturedException, Assert.IsType<InvalidOperationException>(sessionFailure).InnerException);
    }

    [Fact]
    public unsafe void CreateSessionHandleCore_CoversSuccessAndFailureOwnershipOutcomes()
    {
        var noMessageApi = new FakeNativeApi { SessionHandle = IntPtr.Zero };
        var messageApi = new FakeNativeApi
        {
            SessionHandle = IntPtr.Zero,
            ErrorPayload = FakeErrorPayload.Text,
        };
        var successApi = new FakeNativeApi { SessionHandle = new IntPtr(123) };

        Assert.Equal(IntPtr.Zero,
            NativeSearcher.CreateSessionHandleCore("(", Options("(", useRegex: true), true, noMessageApi));
        Assert.Equal(0, noMessageApi.FreedBuffers);
        Assert.Equal(IntPtr.Zero,
            NativeSearcher.CreateSessionHandleCore("(", Options("(", useRegex: true), true, messageApi));
        Assert.Equal(1, messageApi.FreedBuffers);
        Assert.Equal(new IntPtr(123),
            NativeSearcher.CreateSessionHandleCore("needle", Options("needle"), true, successApi));
    }

    [Fact]
    public unsafe void DirectAndSessionStreaming_UseNativeAndDecodeUtf16Columns()
    {
        string path = Write("utf16.txt", "a\U0001F4A9b needle\n");
        SearchOptions options = Options("needle", caseSensitive: true, contextLines: 0);
        int cancel = 0;
        var directSink = new RecordingStreamingSink();

        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.SearchFileStream(path, "needle", options, &cancel, directSink));
        Assert.Equal(5, Assert.Single(directSink.SourceColumns));

        NativeSession session = Assert.IsType<NativeSession>(NativeSearcher.CreateSession("needle", options));
        var sessionSink = new RecordingStreamingSink();
        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.SearchFileStreamWithSession(session, path, &cancel, sessionSink));
        Assert.Equal(5, Assert.Single(sessionSink.SourceColumns));

        session.Dispose();
        session.Dispose();
    }

    [Fact]
    public unsafe void MultilineStreaming_UsesNativeSpanAndUtf16Columns()
    {
        string path = Write("multiline.txt", "a\U0001F4A9 foo\nbar tail\n");
        SearchOptions options = Options(
            "foo.bar",
            useRegex: true,
            contextLines: 0,
            multiline: true,
            multilineDotAll: true);
        int cancel = 0;
        var sink = new RecordingMultilineSink();

        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.SearchFileStreamMultiline(path, "foo.bar", options, &cancel, sink));
        Assert.Equal(4, Assert.Single(sink.SourceColumns));
        Assert.Equal((2ul, 3u), Assert.Single(sink.Ends));
    }

    [Fact]
    public unsafe void RealNativeInvalidRegexes_SurfaceMessagesAndRejectSession()
    {
        string path = Write("invalid.txt", "alpha\nbeta\n");
        int cancel = 0;
        var directSink = new RecordingStreamingSink();
        var multilineSink = new RecordingMultilineSink();

        Assert.Equal(NativeSearcher.StatusInvalidRegex,
            NativeSearcher.SearchFileStream(path, "(", Options("(", useRegex: true), &cancel, directSink));
        Assert.False(string.IsNullOrWhiteSpace(directSink.ErrorMessage));

        Assert.Equal(NativeSearcher.StatusInvalidRegex,
            NativeSearcher.SearchFileStreamMultiline(
                path,
                "(?=alpha)",
                Options("(?=alpha)", useRegex: true, multiline: true),
                &cancel,
                multilineSink));
        Assert.False(string.IsNullOrWhiteSpace(multilineSink.ErrorMessage));
        Assert.Null(NativeSearcher.CreateSession("(", Options("(", useRegex: true)));
    }

    [Fact]
    public unsafe void RealNativeCallbacks_ConvertSinkExceptionsToManagedFailures()
    {
        string directPath = Write("throw.txt", "needle\n");
        SearchOptions directOptions = Options("needle", contextLines: 0);
        int cancel = 0;
        var directSink = new RecordingStreamingSink { ThrowOnMatch = true };
        Exception? directException = null;
        try { NativeSearcher.SearchFileStream(directPath, "needle", directOptions, &cancel, directSink); }
        catch (Exception ex) { directException = ex; }
        InvalidOperationException direct = Assert.IsType<InvalidOperationException>(directException);
        Assert.Same(directSink.CallbackException, direct.InnerException);

        using NativeSession session = Assert.IsType<NativeSession>(NativeSearcher.CreateSession("needle", directOptions));
        var sessionSink = new RecordingStreamingSink { ThrowOnMatch = true };
        Exception? sessionException = null;
        try { NativeSearcher.SearchFileStreamWithSession(session, directPath, &cancel, sessionSink); }
        catch (Exception ex) { sessionException = ex; }
        InvalidOperationException sessionFailure = Assert.IsType<InvalidOperationException>(sessionException);
        Assert.Same(sessionSink.CallbackException, sessionFailure.InnerException);

        string multilinePath = Write("throw-multiline.txt", "foo\nbar\n");
        var multilineSink = new RecordingMultilineSink { ThrowOnMatch = true };
        Exception? multilineException = null;
        try
        {
            NativeSearcher.SearchFileStreamMultiline(
                multilinePath,
                "foo.bar",
                Options("foo.bar", useRegex: true, multiline: true, multilineDotAll: true),
                &cancel,
                multilineSink);
        }
        catch (Exception ex) { multilineException = ex; }
        InvalidOperationException multiline = Assert.IsType<InvalidOperationException>(multilineException);
        Assert.Same(multilineSink.CallbackException, multiline.InnerException);
    }

    [Fact]
    public unsafe void StreamingScanner_PushesPathsAndCompletesCallbacks()
    {
        string first = Write("first.txt", "needle one\n");
        string second = Write("second.txt", "needle two\n");
        SearchOptions options = Options("needle", contextLines: 0);
        using NativeSession session = Assert.IsType<NativeSession>(NativeSearcher.CreateSession("needle", options));
        var sink = new RecordingParallelSink();
        int cancel = 0;
        IntPtr scanner = NativeSearcher.CreateStreamingScanner(session, 2, &cancel, sink, out GCHandle sinkHandle);
        Assert.NotEqual(IntPtr.Zero, scanner);
        Assert.True(sinkHandle.IsAllocated);

        try
        {
            Assert.Equal(NativeSearcher.StatusOk, NativeSearcher.PushPaths(scanner, [first, second], 7));
            Assert.Equal(NativeSearcher.StatusOk, NativeSearcher.FinishStreamingScanner(scanner));
            Assert.Equal(new uint[] { 7, 8 }, sink.FileIndexes.Order().ToArray());
            Assert.Equal(new uint[] { 7, 8 }, sink.MatchIndexes.Order().ToArray());
        }
        finally
        {
            NativeSearcher.DestroyStreamingScanner(scanner);
            sinkHandle.Free();
        }
    }

    [Fact]
    public unsafe void StreamingScannerCore_NullFactoryResultReleasesSinkHandle()
    {
        using NativeSession session = Assert.IsType<NativeSession>(NativeSearcher.CreateSession("needle", Options("needle")));
        var nativeApi = new FakeNativeApi { ScannerHandle = IntPtr.Zero };
        int cancel = 0;

        IntPtr scanner = NativeSearcher.CreateStreamingScannerCore(
            session,
            0,
            &cancel,
            new RecordingParallelSink(),
            out GCHandle sinkHandle,
            true,
            nativeApi);

        Assert.Equal(IntPtr.Zero, scanner);
        Assert.False(sinkHandle.IsAllocated);
        Assert.Equal(1, nativeApi.CallCount);
    }

    [Fact]
    public void ScannerNoOpInputs_ReturnStatusOkWithoutDereferencingHandles()
    {
        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.PushPaths(IntPtr.Zero, ["unused"], 0));
        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.PushPaths(new IntPtr(1), Array.Empty<string>(), 0));
        Assert.Equal(NativeSearcher.StatusOk,
            NativeSearcher.FinishStreamingScanner(IntPtr.Zero));
        NativeSearcher.DestroyStreamingScanner(IntPtr.Zero);
    }

    private SearchOptions Options(
        string query,
        bool caseSensitive = false,
        bool useRegex = false,
        bool skipBinary = false,
        int contextLines = 3,
        int maxResults = 50_000,
        int maxMatchesPerFile = 0,
        long maxFileSizeBytes = 0,
        bool searchHiddenFiles = true,
        bool avoidSourceMemoryMap = false,
        int fileIoTimeoutSeconds = 30,
        int maxMatchesPerLine = 0,
        bool multiline = false,
        bool multilineDotAll = false,
        long maxMultilineBytes = SearchOptions.DefaultMaxMultilineBytes,
        MultilineEngineKind multilineEngine = MultilineEngineKind.Regex) => new()
    {
        Directory = _root,
        Query = query,
        CaseSensitive = caseSensitive,
        UseRegex = useRegex,
        SkipBinary = skipBinary,
        ContextLines = contextLines,
        MaxResults = maxResults,
        MaxMatchesPerFile = maxMatchesPerFile,
        MaxFileSizeBytes = maxFileSizeBytes,
        SearchHiddenFiles = searchHiddenFiles,
        AvoidSourceMemoryMap = avoidSourceMemoryMap,
        FileIoTimeoutSeconds = fileIoTimeoutSeconds,
        MaxMatchesPerLine = maxMatchesPerLine,
        Multiline = multiline,
        MultilineDotAll = multilineDotAll,
        MaxMultilineBytes = maxMultilineBytes,
        MultilineEngine = multilineEngine,
    };

    private string Write(string fileName, string content)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}

public sealed class NativeSearcherCallbackTests
{
    [Fact]
    public unsafe void DispatchMatch_CoversSuccessMissingSinkAndExceptionCapture()
    {
        var successSink = new RecordingStreamingSink { CallbackResult = 4 };
        GCHandle success = GCHandle.Alloc(successSink);
        GCHandle missing = GCHandle.Alloc(new object());
        var throwingSink = new RecordingStreamingSink { ThrowOnMatch = true };
        GCHandle throwing = GCHandle.Alloc(throwingSink);
        var captureThrows = new RecordingStreamingSink { ThrowOnMatch = true, ThrowOnCapture = true };
        GCHandle badCapture = GCHandle.Alloc(captureThrows);
        try
        {
            Assert.Equal(4, NativeSearcher.DispatchMatch(success, null));
            Assert.Equal(1, NativeSearcher.DispatchMatch(missing, null));
            Assert.Equal(1, NativeSearcher.DispatchMatch(throwing, null));
            Assert.Same(throwingSink.CallbackException, throwingSink.CapturedException);
            Assert.Equal(1, NativeSearcher.DispatchMatch(badCapture, null));
        }
        finally
        {
            success.Free();
            missing.Free();
            throwing.Free();
            badCapture.Free();
        }
    }

    [Fact]
    public unsafe void DispatchMultilineMatch_CoversSuccessMissingSinkAndExceptionCapture()
    {
        var successSink = new RecordingMultilineSink { CallbackResult = 5 };
        GCHandle success = GCHandle.Alloc(successSink);
        GCHandle missing = GCHandle.Alloc(new object());
        var throwingSink = new RecordingMultilineSink { ThrowOnMatch = true };
        GCHandle throwing = GCHandle.Alloc(throwingSink);
        var captureThrows = new RecordingMultilineSink { ThrowOnMatch = true, ThrowOnCapture = true };
        GCHandle badCapture = GCHandle.Alloc(captureThrows);
        try
        {
            Assert.Equal(5, NativeSearcher.DispatchMultilineMatch(success, null));
            Assert.Equal(1, NativeSearcher.DispatchMultilineMatch(missing, null));
            Assert.Equal(1, NativeSearcher.DispatchMultilineMatch(throwing, null));
            Assert.Same(throwingSink.CallbackException, throwingSink.CapturedException);
            Assert.Equal(1, NativeSearcher.DispatchMultilineMatch(badCapture, null));
        }
        finally
        {
            success.Free();
            missing.Free();
            throwing.Free();
            badCapture.Free();
        }
    }

    [Fact]
    public unsafe void DispatchParallelCallbacks_CoverSuccessMissingSinkAndExceptionCapture()
    {
        var successSink = new RecordingParallelSink { CallbackResult = 6 };
        GCHandle success = GCHandle.Alloc(successSink);
        GCHandle missing = GCHandle.Alloc(new object());
        var throwingMatch = new RecordingParallelSink { ThrowOnMatch = true };
        GCHandle matchFailure = GCHandle.Alloc(throwingMatch);
        var throwingDone = new RecordingParallelSink { ThrowOnFileDone = true };
        GCHandle doneFailure = GCHandle.Alloc(throwingDone);
        var captureThrows = new RecordingParallelSink
        {
            ThrowOnMatch = true,
            ThrowOnFileDone = true,
            ThrowOnCapture = true,
        };
        GCHandle badCapture = GCHandle.Alloc(captureThrows);
        try
        {
            Assert.Equal(6, NativeSearcher.DispatchParallelMatch(success, 3, null));
            NativeSearcher.DispatchParallelFileDone(success, 3, NativeSearcher.StatusOk, 10, 20);
            Assert.Equal(1, NativeSearcher.DispatchParallelMatch(missing, 0, null));
            NativeSearcher.DispatchParallelFileDone(missing, 0, 0, 0, 0);
            Assert.Equal(1, NativeSearcher.DispatchParallelMatch(matchFailure, 0, null));
            Assert.Same(throwingMatch.CallbackException, throwingMatch.CapturedException);
            NativeSearcher.DispatchParallelFileDone(doneFailure, 0, 0, 0, 0);
            Assert.Same(throwingDone.CallbackException, throwingDone.CapturedException);
            Assert.Equal(1, NativeSearcher.DispatchParallelMatch(badCapture, 0, null));
            NativeSearcher.DispatchParallelFileDone(badCapture, 0, 0, 0, 0);
        }
        finally
        {
            success.Free();
            missing.Free();
            matchFailure.Free();
            doneFailure.Free();
            badCapture.Free();
        }
    }
}

public enum FakeErrorPayload
{
    None,
    Empty,
    Text,
}

internal sealed unsafe class FakeNativeApi : NativeSearcher.INativeApi
{
    public int Status { get; init; } = NativeSearcher.StatusOk;
    public FakeErrorPayload ErrorPayload { get; init; }
    public IntPtr SessionHandle { get; init; }
    public IntPtr ScannerHandle { get; init; }
    public int CallCount { get; private set; }
    public int FreedBuffers { get; private set; }

    public int SearchFileStream(
        char* pathUtf16,
        nuint pathLen,
        byte* patternUtf8,
        nuint patternLen,
        NativeSearcher.QgOptions* options,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, NativeSearcher.QgMatchView*, int> onMatch,
        void* onMatchCtx,
        int* outStatus,
        byte** outErrorMsg,
        nuint* outErrorMsgLen)
    {
        CallCount++;
        SetOutcome(outStatus, outErrorMsg, outErrorMsgLen);
        return 0;
    }

    public int SearchFileStreamMultiline(
        char* pathUtf16,
        nuint pathLen,
        byte* patternUtf8,
        nuint patternLen,
        NativeSearcher.QgOptions* options,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, NativeSearcher.QgMultilineMatchView*, int> onMatch,
        void* onMatchCtx,
        int* outStatus,
        byte** outErrorMsg,
        nuint* outErrorMsgLen)
    {
        CallCount++;
        SetOutcome(outStatus, outErrorMsg, outErrorMsgLen);
        return 0;
    }

    public IntPtr CreateSession(
        byte* patternUtf8,
        nuint patternLen,
        NativeSearcher.QgOptions* options,
        byte** outErrorMsg,
        nuint* outErrorMsgLen)
    {
        CallCount++;
        int ignoredStatus = Status;
        SetOutcome(&ignoredStatus, outErrorMsg, outErrorMsgLen);
        return SessionHandle;
    }

    public int SearchFileStreamWithSession(
        IntPtr session,
        char* pathUtf16,
        nuint pathLen,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, NativeSearcher.QgMatchView*, int> onMatch,
        void* onMatchCtx,
        int* outStatus,
        byte** outErrorMsg,
        nuint* outErrorMsgLen)
    {
        CallCount++;
        SetOutcome(outStatus, outErrorMsg, outErrorMsgLen);
        return 0;
    }

    public IntPtr CreateStreamingScanner(
        IntPtr session,
        uint threadCount,
        int* cancelFlag,
        delegate* unmanaged[Cdecl]<void*, uint, NativeSearcher.QgMatchView*, int> onMatch,
        delegate* unmanaged[Cdecl]<void*, uint, int, ulong, ulong, void> onFileDone,
        void* onMatchCtx)
    {
        CallCount++;
        return ScannerHandle;
    }

    public void FreeBuffer(byte* ptr, nuint len)
    {
        FreedBuffers++;
        Marshal.FreeHGlobal((IntPtr)ptr);
    }

    private void SetOutcome(int* outStatus, byte** outErrorMsg, nuint* outErrorMsgLen)
    {
        *outStatus = Status;
        switch (ErrorPayload)
        {
            case FakeErrorPayload.None:
                *outErrorMsg = null;
                *outErrorMsgLen = 0;
                break;
            case FakeErrorPayload.Empty:
                *outErrorMsg = (byte*)Marshal.AllocHGlobal(1);
                *outErrorMsgLen = 0;
                break;
            case FakeErrorPayload.Text:
                byte[] bytes = Encoding.UTF8.GetBytes("native error");
                IntPtr allocation = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, allocation, bytes.Length);
                *outErrorMsg = (byte*)allocation;
                *outErrorMsgLen = (nuint)bytes.Length;
                break;
        }
    }
}

internal class RecordingStreamingSink : IStreamingSink
{
    private Exception? _capturedException;

    public int CallbackResult { get; init; }
    public bool ThrowOnMatch { get; init; }
    public bool ThrowOnCapture { get; init; }
    public Exception CallbackException { get; } = new ApplicationException("streaming callback failed");
    public List<int> SourceColumns { get; } = [];

    public Exception? CapturedException
    {
        get => _capturedException;
        set
        {
            if (ThrowOnCapture) throw new InvalidOperationException("capture failed");
            _capturedException = value;
        }
    }

    public string? ErrorMessage { get; set; }

    public unsafe int OnMatch(NativeSearcher.QgMatchView* match)
    {
        if (ThrowOnMatch) throw CallbackException;
        if (match != null)
        {
            int? sourceStart = match->SourceMatchStart > int.MaxValue ? null : (int)match->SourceMatchStart;
            var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                match->LinePtr,
                checked((int)match->LineLen),
                checked((int)match->MatchStart),
                checked((int)match->MatchLen),
                sourceStart);
            SourceColumns.Add(decoded.SourceMatchStart);
        }
        return CallbackResult;
    }
}

internal sealed class RecordingMultilineSink : IMultilineStreamingSink
{
    private Exception? _capturedException;

    public int CallbackResult { get; init; }
    public bool ThrowOnMatch { get; init; }
    public bool ThrowOnCapture { get; init; }
    public Exception CallbackException { get; } = new ApplicationException("multiline callback failed");
    public List<int> SourceColumns { get; } = [];
    public List<(ulong EndLine, uint EndColumn)> Ends { get; } = [];

    public Exception? CapturedException
    {
        get => _capturedException;
        set
        {
            if (ThrowOnCapture) throw new InvalidOperationException("capture failed");
            _capturedException = value;
        }
    }

    public string? ErrorMessage { get; set; }

    public unsafe int OnMultilineMatch(NativeSearcher.QgMultilineMatchView* match)
    {
        if (ThrowOnMatch) throw CallbackException;
        if (match != null)
        {
            int? sourceStart = match->SourceMatchStart > int.MaxValue ? null : (int)match->SourceMatchStart;
            var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                match->LinePtr,
                checked((int)match->LineLen),
                checked((int)match->MatchStart),
                checked((int)match->MatchLen),
                sourceStart);
            SourceColumns.Add(decoded.SourceMatchStart);
            Ends.Add((match->EndLine, match->EndCol));
        }
        return CallbackResult;
    }
}

internal sealed class RecordingParallelSink : IStreamingSink, NativeSearcher.IParallelSink
{
    private readonly object _gate = new();
    private Exception? _capturedException;

    public int CallbackResult { get; init; }
    public bool ThrowOnMatch { get; init; }
    public bool ThrowOnFileDone { get; init; }
    public bool ThrowOnCapture { get; init; }
    public Exception CallbackException { get; } = new ApplicationException("parallel callback failed");
    public List<uint> MatchIndexes { get; } = [];
    public List<uint> FileIndexes { get; } = [];

    public Exception? CapturedException
    {
        get => _capturedException;
        set
        {
            if (ThrowOnCapture) throw new InvalidOperationException("capture failed");
            _capturedException = value;
        }
    }

    public string? ErrorMessage { get; set; }

    public unsafe int OnMatch(NativeSearcher.QgMatchView* match) => CallbackResult;

    public unsafe int OnMatchForFile(uint fileIndex, NativeSearcher.QgMatchView* match)
    {
        if (ThrowOnMatch) throw CallbackException;
        lock (_gate) MatchIndexes.Add(fileIndex);
        return CallbackResult;
    }

    public void OnFileDone(uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
    {
        if (ThrowOnFileDone) throw CallbackException;
        lock (_gate) FileIndexes.Add(fileIndex);
    }
}