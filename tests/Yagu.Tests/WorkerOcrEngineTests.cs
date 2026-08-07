using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Yagu.Services.Ocr;

namespace Yagu.Tests;

/// <summary>
/// Covers the pure-managed surface of <see cref="WorkerOcrEngine"/> and its subclasses:
/// environment configuration, the stdin request wire format, protocol parsing, cancellation,
/// disposed/not-running behavior, worker-path resolution, and process lifecycle behavior through
/// deterministic injected process and stream boundaries.
///
/// Unit tests never launch an arbitrary executable; the thin production process adapter is covered
/// by the production build while protocol behavior is exercised entirely in memory.
/// </summary>
[Collection("WorkerOcrEngineEnvironment")]
public sealed class WorkerOcrEngineTests
{
    private const string BogusWorker = @"C:\does-not-exist\Yagu.OcrWorker.exe";

    [Fact]
    public void Paddle_ConfigureWorkerEnvironment_SetsEngineAndModel()
    {
        var engine = new PaddleOcrEngine("EnglishV4");
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal(OcrEngineFactory.PaddleId, env[WorkerOcrEngine.EngineEnvVar]);
        Assert.Equal("EnglishV4", env[PaddleOcrEngine.ModelEnvVar]);
        // The worker is always pointed at a runtime + model directory (bundled payload or cache).
        Assert.False(string.IsNullOrEmpty(env[PaddleOcrEngine.RuntimeDirEnvVar]));
        Assert.False(string.IsNullOrEmpty(env[PaddleOcrEngine.ModelDirEnvVar]));
    }

    [Fact]
    public void Paddle_ConfigureWorkerEnvironment_OmitsModelWhenNotSpecified()
    {
        var engine = new PaddleOcrEngine();
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal(OcrEngineFactory.PaddleId, env[WorkerOcrEngine.EngineEnvVar]);
        Assert.False(env.ContainsKey(PaddleOcrEngine.ModelEnvVar));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Paddle_ConfigureWorkerEnvironment_TreatsBlankModelAsUnset(string model)
    {
        var engine = new PaddleOcrEngine(model);
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.False(env.ContainsKey(PaddleOcrEngine.ModelEnvVar));
    }

    [Fact]
    public void Paddle_ConfigureWorkerEnvironment_OmitsMaxSideWhenUnspecified()
    {
        // Default maxSide is -1 (unspecified) so the worker keeps its own default; the env var must
        // not be emitted in that case.
        var engine = new PaddleOcrEngine("EnglishV4");
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.False(env.ContainsKey(PaddleOcrEngine.MaxSideEnvVar));
    }

    [Theory]
    [InlineData(0, "0")]       // 0 = unlimited (native resolution)
    [InlineData(640, "640")]
    [InlineData(960, "960")]
    [InlineData(1536, "1536")]
    public void Paddle_ConfigureWorkerEnvironment_SetsMaxSideWhenSpecified(int maxSide, string expected)
    {
        var engine = new PaddleOcrEngine("EnglishV4", maxSide);
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal(expected, env[PaddleOcrEngine.MaxSideEnvVar]);
    }

    [Fact]
    public void Tesseract_ConfigureWorkerEnvironment_SetsEngine()
    {
        var engine = new TesseractOcrEngine();
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal(OcrEngineFactory.TesseractId, env[WorkerOcrEngine.EngineEnvVar]);
        Assert.False(env.ContainsKey(PaddleOcrEngine.ModelEnvVar));
        // The worker is always pointed at a tessdata directory (bundled payload or cache).
        Assert.False(string.IsNullOrEmpty(env[TesseractOcrEngine.TessdataDirEnvVar]));
        // ...and at an OpenCv native directory so the offline edition reuses the bundled
        // OpenCvSharpExtern.dll instead of downloading it.
        Assert.False(string.IsNullOrEmpty(env[TesseractOcrEngine.OpenCvDirEnvVar]));
    }

    [Fact]
    public void Paddle_DescribeAssetRequirement_ReportsPaddleEngine()
    {
        IOcrEngine engine = new PaddleOcrEngine("EnglishV4");

        OcrAssetRequirement requirement = engine.DescribeAssetRequirement();

        Assert.Equal("PaddleSharp", requirement.EngineDisplayName);
        // DownloadNeeded depends on what's installed on this machine; the invariant is that any
        // missing component implies a positive size, and a complete install implies zero.
        Assert.Equal(requirement.DownloadNeeded, requirement.MissingComponents.Count > 0);
        Assert.Equal(requirement.DownloadNeeded, requirement.ApproxBytes > 0);
    }

    [Fact]
    public void Paddle_OverrideConstructor_TrimsModelName()
    {
        // The internal (worker-override) constructor trims a supplied model name before it reaches
        // the worker environment, just like the public constructor.
        var engine = new PaddleOcrEngine(modelName: "  EnglishV4  ", workerPathOverride: BogusWorker);
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal("EnglishV4", env[PaddleOcrEngine.ModelEnvVar]);
    }

    [Fact]
    public void Tesseract_DescribeAssetRequirement_ReportsTesseractEngine()
    {
        var engine = new TesseractOcrEngine();

        OcrAssetRequirement requirement = engine.DescribeAssetRequirementForTest();

        Assert.Equal("Tesseract", requirement.EngineDisplayName);
        Assert.Equal(requirement.DownloadNeeded, requirement.MissingComponents.Count > 0);
        Assert.Equal(requirement.DownloadNeeded, requirement.ApproxBytes > 0);
    }

    [Fact]
    public void BuildRequestLine_ProducesCompactSingleLineJson()
    {
        string line = WorkerOcrEngine.BuildRequestLine(7, @"C:\images\photo.png");

        Assert.Equal("""{"Id":7,"Path":"C:\\images\\photo.png"}""", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public async Task EnsureReadyAsync_AfterDispose_ReportsDisposed()
    {
        var engine = new PaddleOcrEngine(modelName: null, workerPathOverride: BogusWorker);
        engine.Dispose();

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR engine has been disposed.", result.Error);
    }

    [Fact]
    public async Task RecognizeAsync_AfterDispose_ReportsDisposed()
    {
        var engine = new TesseractOcrEngine(workerPathOverride: BogusWorker);
        engine.Dispose();

        OcrResult result = await engine.RecognizeAsync("x.png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR engine has been disposed.", result.Error);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var engine = new PaddleOcrEngine(modelName: null, workerPathOverride: BogusWorker);

        engine.Dispose();
        var ex = Record.Exception(() => engine.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_WithoutStartedProcess_DoesNotThrow()
    {
        var engine = new PaddleOcrEngine(modelName: null, workerPathOverride: BogusWorker);

        var ex = await Record.ExceptionAsync(async () => await engine.DisposeAsync());

        Assert.Null(ex);
    }

    [Fact]
    public async Task EnsureReadyAsync_WorkerPathEnvVarPointingToMissingFile_ReportsUnavailable()
    {
        string? previous = Environment.GetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, BogusWorker);
            await using var engine = new PaddleOcrEngine();

            OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, previous);
        }
    }

    [Fact]
    public async Task EnsureReadyAsync_RepeatedMissingWorkerRemainsUnavailableWithoutRepeatedWarning()
    {
        await using var first = new PaddleOcrEngine(modelName: null, workerPathOverride: BogusWorker);
        await using var second = new PaddleOcrEngine(modelName: null, workerPathOverride: BogusWorker);

        OcrResult firstResult = await first.EnsureReadyAsync(CancellationToken.None);
        OcrResult secondResult = await second.EnsureReadyAsync(CancellationToken.None);

        Assert.False(firstResult.Success);
        Assert.False(secondResult.Success);
        Assert.Equal(firstResult.Error, secondResult.Error);
    }

}

[Collection("OcrDownloadGate")]
public sealed class WorkerOcrEngineProtocolTests
{
    [Fact]
    public void ResolveWorkerPath_CoversAuthoritativeOverridesAndBundledFallback()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "YaguApp");
        string bundled = WorkerOcrEngine.ResolveBundledWorkerPath(baseDirectory);

        Assert.Null(WorkerOcrEngine.ResolveWorkerPath(null, true, "ignored", baseDirectory, _ => true));
        Assert.Equal("forced.exe", WorkerOcrEngine.ResolveWorkerPath("forced.exe", true, "ignored", baseDirectory, _ => true));
        Assert.Null(WorkerOcrEngine.ResolveWorkerPath("forced.exe", true, "ignored", baseDirectory, _ => false));
        Assert.Equal("env.exe", WorkerOcrEngine.ResolveWorkerPath(null, false, "env.exe", baseDirectory, _ => true));
        Assert.Null(WorkerOcrEngine.ResolveWorkerPath(null, false, "env.exe", baseDirectory, _ => false));
        Assert.Equal(bundled, WorkerOcrEngine.ResolveWorkerPath(null, false, null, baseDirectory, path => path == bundled));
        Assert.Null(WorkerOcrEngine.ResolveWorkerPath(null, false, string.Empty, baseDirectory, _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBundledWorkerPath_RejectsBlankBaseDirectory(string? baseDirectory)
    {
        Assert.ThrowsAny<ArgumentException>(() => WorkerOcrEngine.ResolveBundledWorkerPath(baseDirectory!));
    }

    [Fact]
    public async Task EnsureReadyAsync_IsSingleFlightForConcurrentCallers()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = CreateEngine(process);

        Task<OcrResult> first = engine.EnsureReadyAsync(CancellationToken.None);
        Task<OcrResult> second = engine.EnsureReadyAsync(CancellationToken.None);
        await process.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        process.Output.WriteLine("""{"Type":"ready"}""");

        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(1, process.StartCount);
    }

    [Fact]
    public async Task EnsureReadyAsync_CallerCancellationDoesNotCancelSharedInitialization()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = CreateEngine(process);
        using var cancellation = new CancellationTokenSource();

        Task<OcrResult> canceledCall = engine.EnsureReadyAsync(cancellation.Token);
        await process.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        OcrResult canceled = await canceledCall.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(canceled.Success);
        Assert.Equal("OCR initialization canceled.", canceled.Error);

        process.Output.WriteLine("""{"Type":"ready"}""");
        Assert.True((await engine.EnsureReadyAsync(CancellationToken.None)).Success);
    }

    [Fact]
    public async Task EnsureReadyAsync_RejectsUntrustedWorkerBeforeProcessCreation()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = CreateEngine(process, hasWorkerPathOverride: false, trusted: false);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker failed signature verification.", result.Error);
        Assert.Equal(0, process.StartCount);
        Assert.Equal(1, engine.TrustCheckCount);
    }

    [Fact]
    public async Task EnsureReadyAsync_ProcessStartFalseFailsClosed()
    {
        var process = new FakeOcrWorkerProcess { StartResult = false };
        await using var engine = CreateEngine(process);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Failed to start OCR worker.", result.Error);
    }

    [Fact]
    public async Task EnsureReadyAsync_ProcessStartExceptionFailsClosed()
    {
        var process = new FakeOcrWorkerProcess { StartException = new InvalidOperationException("start failed") };
        await using var engine = CreateEngine(process);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker failed to start: start failed", result.Error);
    }

    [Fact]
    public async Task EnsureReadyAsync_FactoryExceptionFailsClosed()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = CreateEngine(
            process,
            processFactory: _ => throw new InvalidOperationException("factory failed"));

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker failed to start: factory failed", result.Error);
    }

    [Fact]
    public async Task EnsureReadyAsync_TimeoutFailsClosed()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = CreateEngine(process, readyTimeout: TimeSpan.Zero);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker did not become ready in time.", result.Error);
    }

    [Fact]
    public async Task EnsureReadyAsync_ConfiguresSecureJsonLineProcessStartInfo()
    {
        var process = new FakeOcrWorkerProcess();
        ProcessStartInfo? captured = null;
        using var gate = new DownloadGateScope(consentGranted: false);
        await using var engine = CreateEngine(process, processFactory: startInfo =>
        {
            captured = startInfo;
            return process;
        });
        process.Output.WriteLine("""{"Type":"ready"}""");

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("fake-worker.exe", captured.FileName);
        Assert.False(captured.UseShellExecute);
        Assert.True(captured.CreateNoWindow);
        Assert.True(captured.RedirectStandardInput);
        Assert.True(captured.RedirectStandardOutput);
        Assert.True(captured.RedirectStandardError);
        Assert.Empty(captured.StandardInputEncoding!.GetPreamble());
        Assert.Equal("test", captured.Environment[WorkerOcrEngine.EngineEnvVar]);
        Assert.Equal("0", captured.Environment[WorkerOcrEngine.AllowDownloadEnvVar]);
    }

    [Theory]
    [InlineData("""{"Type":"error","Message":"bad assets"}""", "bad assets")]
    [InlineData("""{"Type":"error","Message":null}""", "initialization error")]
    [InlineData("""{"Type":"error"}""", "initialization error")]
    public async Task EnsureReadyAsync_MapsWorkerInitializationErrors(string line, string expectedMessage)
    {
        var process = new FakeOcrWorkerProcess();
        process.Output.WriteLine(line);
        process.Output.Complete();
        await using var engine = CreateEngine(process);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker initialization failed: " + expectedMessage, result.Error);
    }

    [Fact]
    public async Task ReadLoop_SkipsBlankAndUnknownLinesAndSurvivesMalformedJson()
    {
        var process = new FakeOcrWorkerProcess();
        process.Output.WriteLine(string.Empty);
        process.Output.WriteLine("{}");
        process.Output.WriteLine("""{"Type":"unknown"}""");
        process.Output.WriteLine("not-json");
        process.Output.WriteLine("""{"Type":"ready"}""");
        await using var engine = CreateEngine(process);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReadLoop_StreamFailureFailsInitialization()
    {
        var process = new FakeOcrWorkerProcess();
        process.Output.Complete(new IOException("stdout failed"));
        await using var engine = CreateEngine(process);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker exited before signaling ready.", result.Error);
    }

    [Fact]
    public async Task StandardErrorPump_HandlesBlankDiagnosticAndStreamFailure()
    {
        var process = new FakeOcrWorkerProcess();
        process.Error.WriteLine(string.Empty);
        process.Error.WriteLine("worker diagnostic");
        process.Error.Complete(new IOException("stderr failed"));
        process.Output.WriteLine("""{"Type":"ready"}""");
        await using var engine = CreateEngine(process);

        Assert.True((await engine.EnsureReadyAsync(CancellationToken.None)).Success);
        await process.Error.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("""{"Type":"result","Id":1,"Ok":true,"Text":"recognized"}""", true, "recognized", null)]
    [InlineData("""{"Type":"result","Id":1,"Ok":true,"Text":null}""", true, "", null)]
    [InlineData("""{"Type":"result","Id":1,"Ok":true}""", true, "", null)]
    [InlineData("""{"Type":"result","Id":1,"Ok":false,"Error":"recognition failed"}""", false, "", "recognition failed")]
    [InlineData("""{"Type":"result","Id":1,"Ok":false,"Error":null}""", false, "", "OCR failed")]
    [InlineData("""{"Type":"result","Id":1}""", false, "", "OCR failed")]
    public async Task RecognizeAsync_MapsAllResultPayloadDefaults(
        string response,
        bool expectedSuccess,
        string expectedText,
        string? expectedError)
    {
        var process = new FakeOcrWorkerProcess();
        process.StandardInputWriter.OnLineAsync = (_, _) =>
        {
            process.Output.WriteLine(response);
            return Task.CompletedTask;
        };
        await using var engine = await CreateReadyEngineAsync(process);

        OcrResult result = await engine.RecognizeAsync("image.png", CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(expectedText, result.Text);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public async Task RecognizeAsync_IgnoresResultsWithoutPendingRequest()
    {
        var process = new FakeOcrWorkerProcess();
        process.Output.WriteLine("""{"Type":"ready"}""");
        process.Output.WriteLine("""{"Type":"result","Id":999,"Ok":true,"Text":"orphan"}""");
        process.Output.WriteLine("""{"Type":"result","Ok":false}""");
        process.StandardInputWriter.OnLineAsync = (_, _) =>
        {
            process.Output.WriteLine("""{"Type":"result","Id":1,"Ok":true,"Text":"matched"}""");
            return Task.CompletedTask;
        };
        await using var engine = CreateEngine(process);

        Assert.True((await engine.EnsureReadyAsync(CancellationToken.None)).Success);
        OcrResult result = await engine.RecognizeAsync("image.png", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("matched", result.Text);
    }

    [Fact]
    public async Task RecognizeAsync_ProcessAlreadyExitedFailsClosed()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = await CreateReadyEngineAsync(process);
        process.HasExitedValue = true;

        OcrResult result = await engine.RecognizeAsync("image.png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("OCR worker is not running.", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecognizeAsync_WriteOrFlushFailureReturnsSendError(bool failWrite)
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = await CreateReadyEngineAsync(process);
        process.StandardInputWriter.WriteException = failWrite ? new IOException("write failed") : null;
        process.StandardInputWriter.FlushException = failWrite ? null : new IOException("flush failed");

        OcrResult result = await engine.RecognizeAsync("image.png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("Failed to send OCR request: ", result.Error);
    }

    [Fact]
    public async Task RecognizeAsync_CancellationWhileAwaitingResponseReturnsCanceled()
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = await CreateReadyEngineAsync(process);
        using var cancellation = new CancellationTokenSource();

        Task<OcrResult> recognition = engine.RecognizeAsync("image.png", cancellation.Token);
        await process.StandardInputWriter.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        OcrResult result = await recognition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Equal("OCR canceled.", result.Error);
    }

    [Fact]
    public async Task RecognizeAsync_CancellationWhileAwaitingWriteLockDoesNotLeakPendingRequest()
    {
        var process = new FakeOcrWorkerProcess();
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.StandardInputWriter.OnLineAsync = async (line, cancellationToken) =>
        {
            using JsonDocument request = JsonDocument.Parse(line);
            int id = request.RootElement.GetProperty("Id").GetInt32();
            if (id == 1)
            {
                await releaseFirstWrite.Task.WaitAsync(cancellationToken);
                process.Output.WriteLine("""{"Type":"result","Id":1,"Ok":true,"Text":"first"}""");
            }
        };
        await using var engine = await CreateReadyEngineAsync(process);

        Task<OcrResult> first = engine.RecognizeAsync("first.png", CancellationToken.None);
        await process.StandardInputWriter.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var secondCancellation = new CancellationTokenSource();
        Task<OcrResult> second = engine.RecognizeAsync("second.png", secondCancellation.Token);
        secondCancellation.Cancel();

        OcrResult canceled = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(canceled.Success);
        Assert.Equal("OCR canceled.", canceled.Error);

        releaseFirstWrite.TrySetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingRecognition_FailsWhenWorkerExitsOrOutputCloses(bool raiseExit)
    {
        var process = new FakeOcrWorkerProcess();
        await using var engine = await CreateReadyEngineAsync(process);

        Task<OcrResult> recognition = engine.RecognizeAsync("image.png", CancellationToken.None);
        await process.StandardInputWriter.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (raiseExit)
        {
            process.HasExitedValue = true;
            process.RaiseExited();
        }
        else
        {
            process.Output.Complete();
        }

        OcrResult result = await recognition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Equal(raiseExit ? "OCR worker exited." : "OCR worker output stream closed.", result.Error);
    }

    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 0)]
    [InlineData(false, true, true, 1)]
    public async Task Dispose_CoversRunningExitedAndBestEffortFailures(
        bool hasExited,
        bool inputDisposeThrows,
        bool killThrows,
        int expectedKillCount)
    {
        var process = new FakeOcrWorkerProcess { HasExitedValue = hasExited, KillThrows = killThrows };
        var engine = await CreateReadyEngineAsync(process);
        process.StandardInputWriter.DisposeException = inputDisposeThrows ? new IOException("close failed") : null;

        Exception? error = Record.Exception(engine.Dispose);

        Assert.Null(error);
        Assert.Equal(expectedKillCount, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        engine.Dispose();
        await engine.DisposeAsync();
    }

    [Theory]
    [InlineData((int)AsyncDisposeMode.AlreadyExited, false, false, 0)]
    [InlineData((int)AsyncDisposeMode.WaitExits, false, false, 0)]
    [InlineData((int)AsyncDisposeMode.WaitThrows, true, false, 1)]
    [InlineData((int)AsyncDisposeMode.WaitTimesOut, true, true, 1)]
    public async Task DisposeAsync_CoversGracefulWaitTimeoutKillAndBestEffortFailures(
        int modeValue,
        bool inputDisposeThrows,
        bool killThrows,
        int expectedKillCount)
    {
        var mode = (AsyncDisposeMode)modeValue;
        var process = new FakeOcrWorkerProcess
        {
            HasExitedValue = mode == AsyncDisposeMode.AlreadyExited,
            KillThrows = killThrows,
        };
        process.WaitForExitHandler = mode switch
        {
            AsyncDisposeMode.WaitExits => _ =>
            {
                process.HasExitedValue = true;
                return Task.CompletedTask;
            },
            AsyncDisposeMode.WaitThrows => _ => Task.FromException(new IOException("wait failed")),
            AsyncDisposeMode.WaitTimesOut => cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            _ => _ => Task.CompletedTask,
        };
        var engine = await CreateReadyEngineAsync(
            process,
            shutdownTimeout: mode == AsyncDisposeMode.WaitTimesOut ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
        process.StandardInputWriter.DisposeException = inputDisposeThrows ? new IOException("close failed") : null;

        Exception? error = await Record.ExceptionAsync(async () => await engine.DisposeAsync());

        Assert.Null(error);
        Assert.Equal(expectedKillCount, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        await engine.DisposeAsync();
    }

    private static TestWorkerOcrEngine CreateEngine(
        FakeOcrWorkerProcess process,
        bool hasWorkerPathOverride = true,
        bool trusted = true,
        TimeSpan? readyTimeout = null,
        TimeSpan? shutdownTimeout = null,
        Func<ProcessStartInfo, IOcrWorkerProcess>? processFactory = null)
        => new(
            process,
            hasWorkerPathOverride,
            trusted,
            readyTimeout ?? TimeSpan.FromSeconds(5),
            shutdownTimeout ?? TimeSpan.FromSeconds(1),
            processFactory ?? (_ => process));

    private static async Task<TestWorkerOcrEngine> CreateReadyEngineAsync(
        FakeOcrWorkerProcess process,
        TimeSpan? shutdownTimeout = null)
    {
        process.Output.WriteLine("""{"Type":"ready"}""");
        TestWorkerOcrEngine engine = CreateEngine(process, shutdownTimeout: shutdownTimeout);
        OcrResult ready = await engine.EnsureReadyAsync(CancellationToken.None);
        Assert.True(ready.Success, ready.Error);
        return engine;
    }

    private enum AsyncDisposeMode
    {
        AlreadyExited,
        WaitExits,
        WaitThrows,
        WaitTimesOut,
    }

    private sealed class TestWorkerOcrEngine : WorkerOcrEngine
    {
        private readonly TrustVerifierProbe _trustVerifier;

        internal TestWorkerOcrEngine(
            FakeOcrWorkerProcess process,
            bool hasWorkerPathOverride,
            bool trusted,
            TimeSpan readyTimeout,
            TimeSpan shutdownTimeout,
            Func<ProcessStartInfo, IOcrWorkerProcess> processFactory)
            : this(
                process,
                hasWorkerPathOverride,
                new TrustVerifierProbe(trusted),
                readyTimeout,
                shutdownTimeout,
                processFactory)
        {
        }

        private TestWorkerOcrEngine(
            FakeOcrWorkerProcess process,
            bool hasWorkerPathOverride,
            TrustVerifierProbe trustVerifier,
            TimeSpan readyTimeout,
            TimeSpan shutdownTimeout,
            Func<ProcessStartInfo, IOcrWorkerProcess> processFactory)
            : base(
                hasWorkerPathOverride,
                processFactory,
                trustVerifier.Verify,
                readyTimeout,
                shutdownTimeout)
        {
            _trustVerifier = trustVerifier;
            Process = process;
        }

        internal FakeOcrWorkerProcess Process { get; }

        internal int TrustCheckCount => _trustVerifier.CheckCount;

        public override string Id => "test";

        public override string DisplayName => "Test";

        protected override string LogSource => "OcrTest";

        protected override void ConfigureWorkerEnvironment(IDictionary<string, string?> environment)
            => environment[EngineEnvVar] = "test";

        protected override OcrAssetRequirement DescribeAssetRequirement() => new()
        {
            EngineDisplayName = "Test",
            DownloadNeeded = false,
            ApproxBytes = 0,
            MissingComponents = Array.Empty<string>(),
        };

        protected override string? ResolveWorkerPath() => "fake-worker.exe";

    }

    private sealed class TrustVerifierProbe(bool trusted)
    {
        internal int CheckCount { get; private set; }

        internal bool Verify(string workerPath, out string failure)
        {
            CheckCount++;
            failure = trusted ? string.Empty : "test trust failure";
            return trusted;
        }
    }

    private sealed class FakeOcrWorkerProcess : IOcrWorkerProcess
    {
        private EventHandler? _exited;

        internal FakeOcrWorkerProcess()
        {
            StandardOutput = new StreamReader(Output, Encoding.UTF8, false, 1024, leaveOpen: true);
            StandardError = new StreamReader(Error, Encoding.UTF8, false, 1024, leaveOpen: true);
        }

        public event EventHandler? Exited
        {
            add => _exited += value;
            remove => _exited -= value;
        }

        internal ScriptedReadStream Output { get; } = new();

        internal ScriptedReadStream Error { get; } = new();

        internal RecordingTextWriter StandardInputWriter { get; } = new();

        internal TaskCompletionSource StartObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool StartResult { get; set; } = true;

        internal Exception? StartException { get; set; }

        internal bool HasExitedValue { get; set; }

        internal bool KillThrows { get; set; }

        internal int StartCount { get; private set; }

        internal int KillCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal Func<CancellationToken, Task> WaitForExitHandler { get; set; } = _ => Task.CompletedTask;

        public bool HasExited => HasExitedValue;

        public TextWriter StandardInput => StandardInputWriter;

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public bool Start()
        {
            StartCount++;
            StartObserved.TrySetResult();
            if (StartException is not null)
            {
                throw StartException;
            }

            return StartResult;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => WaitForExitHandler(cancellationToken);

        public void Kill()
        {
            KillCount++;
            if (KillThrows)
            {
                throw new InvalidOperationException("kill failed");
            }

            HasExitedValue = true;
        }

        internal void RaiseExited() => _exited?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            DisposeCount++;
            Output.Complete();
            Error.Complete();
        }
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        internal Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

        internal Func<string, CancellationToken, Task>? OnLineAsync { get; set; }

        internal Exception? WriteException { get; set; }

        internal Exception? FlushException { get; set; }

        internal Exception? DisposeException { get; set; }

        public override async Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (WriteException is not null)
            {
                throw WriteException;
            }

            string line = buffer.ToString();
            Lines.Writer.TryWrite(line);
            if (OnLineAsync is not null)
            {
                await OnLineAsync(line, cancellationToken);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => FlushException is null ? Task.CompletedTask : Task.FromException(FlushException);

        protected override void Dispose(bool disposing)
        {
            if (DisposeException is not null)
            {
                throw DisposeException;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ScriptedReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        internal TaskCompletionSource CompletionObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void WriteLine(string line)
            => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(line + Environment.NewLine));

        internal void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                try
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                    {
                        CompletionObserved.TrySetResult();
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    CompletionObserved.TrySetResult();
                    throw new IOException("Scripted stream failed.", ex);
                }

                if (_chunks.Reader.TryRead(out byte[]? chunk))
                {
                    _current = chunk;
                    _offset = 0;
                }
            }

            int copied = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Complete();
            base.Dispose(disposing);
        }
    }

    private sealed class DownloadGateScope : IDisposable
    {
        private readonly bool _consentGranted = OcrDownloadGate.ConsentGranted;

        internal DownloadGateScope(bool consentGranted)
        {
            OcrDownloadGate.ConsentGranted = consentGranted;
        }

        public void Dispose() => OcrDownloadGate.ConsentGranted = _consentGranted;
    }
}

/// <summary>
/// Init-time consent-gate behaviour for <see cref="WorkerOcrEngine"/>. These cases mutate the
/// process-global <see cref="OcrDownloadGate"/> statics, so they share the "OcrDownloadGate"
/// collection with <see cref="OcrDownloadGateTests"/> to serialize access and avoid cross-test races.
/// </summary>
[Collection("OcrDownloadGate")]
public sealed class WorkerOcrEngineDownloadGateTests
{
    [Fact]
    public async Task EnsureReadyAsync_DownloadNeededWithoutConsent_RefusesWithApproxMb()
    {
        var requirement = new OcrAssetRequirement
        {
            EngineDisplayName = "Fake",
            DownloadNeeded = true,
            ApproxBytes = 349L * 1024 * 1024,
            MissingComponents = new[] { "OCR engine runtime (~349 MB)" },
        };

        using var gate = new OcrGateReset();
        OcrDownloadGate.ConsentGranted = false;
        OcrDownloadGate.PromptAsync = null; // headless: no UI hook → refuse rather than download

        await using var engine = new FakeWorkerOcrEngine(requirement);
        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("349 MB", result.Error);
        Assert.Contains("not approved", result.Error);
        Assert.Contains("OCR engine runtime", result.Error);
    }

    [Fact]
    public async Task EnsureReadyAsync_DownloadNeededEmptyComponents_RefusesWithoutComponentList()
    {
        var requirement = new OcrAssetRequirement
        {
            EngineDisplayName = "Fake",
            DownloadNeeded = true,
            ApproxBytes = 1024 * 1024,
            MissingComponents = Array.Empty<string>(),
        };

        using var gate = new OcrGateReset();
        OcrDownloadGate.ConsentGranted = false;
        OcrDownloadGate.PromptAsync = null;

        await using var engine = new FakeWorkerOcrEngine(requirement);
        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not approved", result.Error);
        // With no named components, the message goes straight from the size to ", which..." with no
        // parenthetical component list (distinct from the components-present branch).
        Assert.Contains("1 MB, which was not approved", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureReadyAsync_NoDownloadNeeded_StartsWorker(bool consentGranted)
    {
        var requirement = new OcrAssetRequirement
        {
            EngineDisplayName = "Fake",
            DownloadNeeded = false,
            ApproxBytes = 0,
            MissingComponents = Array.Empty<string>(),
        };

        using var gate = new OcrGateReset();
        OcrDownloadGate.ConsentGranted = consentGranted;

        await using var engine = new FakeWorkerOcrEngine(requirement);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Equal(consentGranted ? "1" : "0", engine.LastAllowDownloadEnvValue);
    }

    [Fact]
    public async Task EnsureReadyAsync_DownloadNeededButConsentGranted_ProceedsToWorker()
    {
        // Download is needed, but consent was already granted this session → the gate returns true and
        // init falls through to start the worker (covers the "allowed" arm of the consent branch).
        var requirement = new OcrAssetRequirement
        {
            EngineDisplayName = "Fake",
            DownloadNeeded = true,
            ApproxBytes = 349L * 1024 * 1024,
            MissingComponents = new[] { "OCR engine runtime (~349 MB)" },
        };

        using var gate = new OcrGateReset();
        OcrDownloadGate.ConsentGranted = true; // pre-granted → EnsureAllowedAsync short-circuits to true
        OcrDownloadGate.PromptAsync = null;

        await using var engine = new FakeWorkerOcrEngine(requirement);

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Equal("1", engine.LastAllowDownloadEnvValue);
    }

    /// <summary>Minimal concrete <see cref="WorkerOcrEngine"/> for driving init branches with a forced requirement.</summary>
    private sealed class FakeWorkerOcrEngine : WorkerOcrEngine
    {
        private readonly OcrAssetRequirement _requirement;
        private readonly TempFile _worker;
        private readonly DownloadGateProcess _process;
        private IDictionary<string, string?>? _capturedEnv;

        public FakeWorkerOcrEngine(OcrAssetRequirement requirement)
            : this(requirement, new TempFile(), new DownloadGateProcess())
        {
        }

        private FakeWorkerOcrEngine(
            OcrAssetRequirement requirement,
            TempFile worker,
            DownloadGateProcess process)
            : base(
                hasWorkerPathOverride: true,
                _ => process,
                TrustWorker,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(50))
        {
            _requirement = requirement;
            _worker = worker;
            _process = process;
        }

        /// <summary>The value the base class wrote for the allow-download env var, read from the live process env dict.</summary>
        public string? LastAllowDownloadEnvValue =>
            _capturedEnv is not null && _capturedEnv.TryGetValue(WorkerOcrEngine.AllowDownloadEnvVar, out string? v) ? v : null;

        public int ProcessStartCount => _process.StartCount;

        public override string Id => "fake";

        public override string DisplayName => "Fake";

        protected override string LogSource => "OcrFake";

        protected override void ConfigureWorkerEnvironment(IDictionary<string, string?> environment)
        {
            // Stash the live dictionary; the base class sets AllowDownloadEnvVar on it right after this call.
            _capturedEnv = environment;
        }

        protected override OcrAssetRequirement DescribeAssetRequirement() => _requirement;

        protected override string? ResolveWorkerPath() => _worker.Path;

        private static bool TrustWorker(string workerPath, out string failure)
        {
            failure = string.Empty;
            return true;
        }

        public new async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _worker.Dispose();
        }
    }

    private sealed class DownloadGateProcess : IOcrWorkerProcess
    {
        private readonly StringWriter _stdin = new();

        public DownloadGateProcess()
        {
            StandardOutput = new StreamReader(new MemoryStream());
            StandardError = new StreamReader(new MemoryStream());
        }

        public event EventHandler? Exited;

        public bool HasExited => false;

        public TextWriter StandardInput => _stdin;

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public int StartCount { get; private set; }

        public bool Start()
        {
            StartCount++;
            return true;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Kill() => Exited?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            _stdin.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }

    /// <summary>Snapshots and restores the process-global <see cref="OcrDownloadGate"/> statics.</summary>
    private sealed class OcrGateReset : IDisposable
    {
        private readonly bool _consent = OcrDownloadGate.ConsentGranted;
        private readonly Func<OcrAssetRequirement, Task<bool>>? _prompt = OcrDownloadGate.PromptAsync;

        public void Dispose()
        {
            OcrDownloadGate.ConsentGranted = _consent;
            OcrDownloadGate.PromptAsync = _prompt;
        }
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yagu-ocr-worker-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(Path, string.Empty);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort */ }
        }
    }
}

[CollectionDefinition("WorkerOcrEngineEnvironment", DisableParallelization = true)]
public sealed class WorkerOcrEngineEnvironmentCollection
{
}

/// <summary>
/// Source-level pins for the out-of-process worker protocol. The request/response JSON exchange and
/// the worker-path probe order run only against a live, cooperating <c>Yagu.OcrWorker.exe</c>; they
/// cannot be line-covered by a unit test without spawning a protocol-speaking child process. These
/// pins lock the wire-protocol contract (property names, message types, and probe order) so it can
/// never silently drift out of sync with the worker.
/// </summary>
public sealed class WorkerOcrEngineProtocolSourceTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ocr", "WorkerOcrEngine.cs"));

    [Fact]
    public void WireProtocol_UsesPascalCasePropertyNames()
    {
        Assert.Contains("private const string PropType = \"Type\";", Source);
        Assert.Contains("private const string PropMessage = \"Message\";", Source);
        Assert.Contains("private const string PropId = \"Id\";", Source);
        Assert.Contains("private const string PropOk = \"Ok\";", Source);
        Assert.Contains("private const string PropText = \"Text\";", Source);
        Assert.Contains("private const string PropError = \"Error\";", Source);
        Assert.Contains("private const string PropPath = \"Path\";", Source);
    }

    [Fact]
    public void DispatchLine_HandlesReadyErrorAndResultMessages()
    {
        Assert.Contains("case \"ready\":", Source);
        Assert.Contains("case \"error\":", Source);
        Assert.Contains("case \"result\":", Source);
        // The request id must round-trip so concurrent recognitions are matched to their replies.
        Assert.Contains("_pending.TryRemove(id, out", Source);
        Assert.Contains("OcrResult.Ok(text)", Source);
        Assert.Contains("OcrResult.Fail(error)", Source);
    }

    [Fact]
    public void RequestLine_IsBuiltWithIdAndPath()
    {
        Assert.Contains("writer.WriteNumber(PropId, id);", Source);
        Assert.Contains("writer.WriteString(PropPath, path);", Source);
    }

    [Fact]
    public void StandardInput_UsesBomlessUtf8ToAvoidCorruptingFirstRequest()
    {
        Assert.Contains("Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)", Source);
        Assert.Contains("startInfo.StandardInputEncoding = Utf8NoBom;", Source);
    }

    [Fact]
    public void ResolveWorkerPath_ProbesOverrideThenBesideApp_AndNeverAUserWritablePath()
    {
        // SECURITY (binary planting): the worker must be loaded only from an explicit override or the
        // app's own install directory, NEVER from a per-user-writable path such as %LOCALAPPDATA%.
        // Auto-executing a planted exe from a user-writable location would let non-admin malware run
        // inside Yagu's process tree.
        Assert.Contains("if (hasWorkerPathOverride)", Source);
        Assert.Contains("if (!string.IsNullOrEmpty(environmentOverride))", Source);
        Assert.Contains("ResolveBundledWorkerPath(baseDirectory)", Source);
        Assert.Contains("\"ocr-worker\", \"Yagu.OcrWorker.exe\"", Source);
        Assert.DoesNotContain("\"ocr-runtime\", \"worker\"", Source);
        Assert.DoesNotContain("SpecialFolder.LocalApplicationData", Source);
    }

    [Fact]
    public void ResolveBundledWorkerPath_IndexWorkerUsesParentAppDirectory()
    {
        string app = Path.Combine(Path.GetTempPath(), "YaguApp");
        string nested = Path.Combine(app, "index-worker");
        Assert.Equal(
            Path.Combine(app, "ocr-worker", "Yagu.OcrWorker.exe"),
            WorkerOcrEngine.ResolveBundledWorkerPath(nested));
        Assert.Equal(
            Path.Combine(app, "ocr-worker", "Yagu.OcrWorker.exe"),
            WorkerOcrEngine.ResolveBundledWorkerPath(app));
    }

    private static void AssertContainsInOrder(string haystack, params string[] needles)
    {
        int index = 0;
        foreach (string needle in needles)
        {
            int found = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find \"{needle}\" after index {index}.");
            index = found + needle.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}

/// <summary>
/// Source-level pin for the worker's model resolver. <c>PaddleModelResolver</c> references
/// PaddleSharp's online-models package, which is deliberately NOT linked into the test assembly, so
/// its default/fallback lines cannot be line-covered by a unit test. This pins that the default
/// recognition model is <c>ChineseV5</c> (PP-OCRv5) and that both a blank/whitespace name and an
/// unknown name fall back to it — matching <c>AppSettings.DefaultImageOcrModel</c>.
/// </summary>
public sealed class PaddleModelResolverSourceTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu.OcrWorker", "PaddleModelResolver.cs"));

    [Fact]
    public void DefaultModelName_IsChineseV5()
    {
        Assert.Contains("public const string DefaultModelName = \"ChineseV5\";", Source);
    }

    [Fact]
    public void BlankAndUnknownNames_FallBackToChineseV5NotEnglishV4()
    {
        // Two return sites use the ChineseV5 default: the blank/whitespace guard at the top of Resolve
        // and the reflection-miss path at the bottom. Neither may regress to the former EnglishV4 default.
        Assert.Equal(2, CountOccurrences(Source, "return OnlineFullModels.ChineseV5;"));
        Assert.DoesNotContain("OnlineFullModels.EnglishV4", Source);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
