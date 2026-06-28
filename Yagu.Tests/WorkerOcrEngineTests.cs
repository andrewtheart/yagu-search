using Yagu.Services.Ocr;

namespace Yagu.Tests;

/// <summary>
/// Covers the pure-managed surface of <see cref="WorkerOcrEngine"/> and its subclasses:
/// environment configuration, the stdin request wire format, disposed/not-running behavior, worker
/// path resolution via the override environment variable, and the process lifecycle when a worker
/// executable is present but does not speak the protocol (it exits without signaling ready).
///
/// The happy-path protocol (a cooperating worker that emits <c>ready</c>/<c>result</c> JSON) is
/// exercised by the real <c>Yagu.OcrWorker.exe</c> at runtime; it is not reproduced here because it
/// requires a live, protocol-speaking child process.
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
    public void Tesseract_ConfigureWorkerEnvironment_SetsEngine()
    {
        var engine = new TesseractOcrEngine();
        var env = new Dictionary<string, string?>();

        engine.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal(OcrEngineFactory.TesseractId, env[WorkerOcrEngine.EngineEnvVar]);
        Assert.False(env.ContainsKey(PaddleOcrEngine.ModelEnvVar));
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
    public async Task EnsureReadyAsync_WorkerExitsWithoutSignalingReady_FailsGracefully()
    {
        // Point the worker path at a real, benign, short-lived system executable (whoami.exe). It is
        // a valid process the engine can start, but it writes a non-protocol line to stdout and then
        // exits without ever emitting the "ready" JSON. This drives the full in-process lifecycle:
        // ProcessStartInfo construction, ConfigureWorkerEnvironment, process start, the stdout/stderr
        // pump tasks, the unparseable-line dispatch path, OnProcessExited, and the read-loop teardown
        // — all of which must converge on a graceful "not ready" failure rather than hanging.
        string workerPath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        Assert.True(File.Exists(workerPath), "whoami.exe is expected on Windows.");

        string? previous = Environment.GetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, workerPath);
            await using var engine = new PaddleOcrEngine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            OcrResult ready = await engine.EnsureReadyAsync(cts.Token);
            Assert.False(ready.Success);

            // After a failed init, recognition short-circuits on the cached readiness failure.
            OcrResult recognized = await engine.RecognizeAsync("x.png", cts.Token);
            Assert.False(recognized.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, previous);
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
        Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ocr", "WorkerOcrEngine.cs"));

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
    public void ResolveWorkerPath_ProbesOverrideThenProvisionedThenBesideApp()
    {
        AssertContainsInOrder(Source,
            "if (_hasWorkerPathOverride)",
            "Environment.GetEnvironmentVariable(WorkerPathEnvVar)",
            "\"Yagu\", \"ocr-runtime\", \"worker\", \"Yagu.OcrWorker.exe\"",
            "\"ocr-worker\", \"Yagu.OcrWorker.exe\"");
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
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}
