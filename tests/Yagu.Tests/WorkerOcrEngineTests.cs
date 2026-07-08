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
        var engine = new PaddleOcrEngine("EnglishV4");

        OcrAssetRequirement requirement = engine.DescribeAssetRequirementForTest();

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
        // A real (but inert) file so ResolveWorkerPath returns non-null and init reaches the gate.
        using var worker = new TempFile();
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

        await using var engine = new FakeWorkerOcrEngine(worker.Path, requirement);
        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("349 MB", result.Error);
        Assert.Contains("not approved", result.Error);
        Assert.Contains("OCR engine runtime", result.Error);
    }

    [Fact]
    public async Task EnsureReadyAsync_DownloadNeededEmptyComponents_RefusesWithoutComponentList()
    {
        using var worker = new TempFile();
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

        await using var engine = new FakeWorkerOcrEngine(worker.Path, requirement);
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
        // No download needed → the gate is skipped and the worker is started. Use whoami.exe (a real
        // process that exits without speaking the protocol) to drive process start, ConfigureWorkerEnvironment,
        // the AllowDownload env ternary, and graceful not-ready teardown — independent of installed assets.
        string workerPath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        Assert.True(File.Exists(workerPath), "whoami.exe is expected on Windows.");

        var requirement = new OcrAssetRequirement
        {
            EngineDisplayName = "Fake",
            DownloadNeeded = false,
            ApproxBytes = 0,
            MissingComponents = Array.Empty<string>(),
        };

        using var gate = new OcrGateReset();
        OcrDownloadGate.ConsentGranted = consentGranted;

        await using var engine = new FakeWorkerOcrEngine(workerPath, requirement);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        OcrResult result = await engine.EnsureReadyAsync(cts.Token);

        Assert.False(result.Success); // whoami never emits "ready"
        Assert.Equal(consentGranted ? "1" : "0", engine.LastAllowDownloadEnvValue);
    }

    [Fact]
    public async Task EnsureReadyAsync_DownloadNeededButConsentGranted_ProceedsToWorker()
    {
        // Download is needed, but consent was already granted this session → the gate returns true and
        // init falls through to start the worker (covers the "allowed" arm of the consent branch).
        string workerPath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        Assert.True(File.Exists(workerPath), "whoami.exe is expected on Windows.");

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

        await using var engine = new FakeWorkerOcrEngine(workerPath, requirement);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        OcrResult result = await engine.EnsureReadyAsync(cts.Token);

        Assert.False(result.Success); // whoami never emits "ready", but the gate was passed
        // Worker was actually started, so the allow-download env var was authorized.
        Assert.Equal("1", engine.LastAllowDownloadEnvValue);
    }

    [Fact]
    public async Task Tesseract_EnsureReadyAsync_WithConsent_StartsWorkerAndFailsNotReady()
    {
        // Drive a real TesseractOcrEngine to process start (consent pre-granted bypasses the gate even
        // if tessdata is missing on this machine), exercising its ConfigureWorkerEnvironment and the
        // init-failure logging path. whoami exits without speaking the protocol → graceful not-ready.
        string workerPath = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        Assert.True(File.Exists(workerPath), "whoami.exe is expected on Windows.");

        using var gate = new OcrGateReset();
        OcrDownloadGate.ConsentGranted = true;
        OcrDownloadGate.PromptAsync = null;

        await using var engine = new TesseractOcrEngine(workerPathOverride: workerPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        OcrResult result = await engine.EnsureReadyAsync(cts.Token);

        Assert.False(result.Success);
    }

    /// <summary>Minimal concrete <see cref="WorkerOcrEngine"/> for driving init branches with a forced requirement.</summary>
    private sealed class FakeWorkerOcrEngine : WorkerOcrEngine
    {
        private readonly OcrAssetRequirement _requirement;
        private IDictionary<string, string?>? _capturedEnv;

        public FakeWorkerOcrEngine(string? workerPathOverride, OcrAssetRequirement requirement)
            : base(workerPathOverride)
            => _requirement = requirement;

        /// <summary>The value the base class wrote for the allow-download env var, read from the live process env dict.</summary>
        public string? LastAllowDownloadEnvValue =>
            _capturedEnv is not null && _capturedEnv.TryGetValue(WorkerOcrEngine.AllowDownloadEnvVar, out string? v) ? v : null;

        public override string Id => "fake";

        public override string DisplayName => "Fake";

        protected override string LogSource => "OcrFake";

        protected override void ConfigureWorkerEnvironment(IDictionary<string, string?> environment)
        {
            // Stash the live dictionary; the base class sets AllowDownloadEnvVar on it right after this call.
            _capturedEnv = environment;
        }

        protected override OcrAssetRequirement DescribeAssetRequirement() => _requirement;
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
        AssertContainsInOrder(Source,
            "if (_hasWorkerPathOverride)",
            "Environment.GetEnvironmentVariable(WorkerPathEnvVar)",
            "\"ocr-worker\", \"Yagu.OcrWorker.exe\"");
        Assert.DoesNotContain("\"ocr-runtime\", \"worker\"", Source);
        Assert.DoesNotContain("SpecialFolder.LocalApplicationData", Source);
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
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}
