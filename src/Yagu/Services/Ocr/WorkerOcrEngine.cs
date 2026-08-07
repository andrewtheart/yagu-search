using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Ocr;

internal delegate bool OcrWorkerTrustVerifier(string workerPath, out string failure);

/// <summary>
/// Base class for OCR engines hosted in a separate <c>Yagu.OcrWorker.exe</c> process.
/// <para>
/// This type is intentionally <b>pure managed</b> — it only manages a child process and exchanges
/// line-delimited JSON over stdin/stdout. It has zero native dependencies, so it is safe to compile
/// into the Native-AOT Yagu app and to link into the test project. All the native OCR work (which is
/// not AOT-compatible) lives in the out-of-process worker, which selects its backend from the
/// <c>YAGU_OCR_ENGINE</c> environment variable that subclasses set via
/// <see cref="ConfigureWorkerEnvironment"/>.
/// </para>
/// <para>
/// The worker lazily downloads its native runtime/models (and language data) on first use, so
/// <see cref="EnsureReadyAsync"/> can take a while the very first time. When the worker binary or
/// runtime is unavailable, the engine degrades gracefully by returning failure results rather than
/// throwing.
/// </para>
/// </summary>
public abstract class WorkerOcrEngine : IOcrEngine, IAsyncDisposable, IDisposable
{
    /// <summary>Environment variable that overrides the worker executable path (used by tests/dev).</summary>
    public const string WorkerPathEnvVar = "YAGU_OCR_WORKER";

    /// <summary>Environment variable that selects the worker's OCR backend (<c>paddle</c>/<c>tesseract</c>).</summary>
    public const string EngineEnvVar = "YAGU_OCR_ENGINE";

    /// <summary>Environment variable that authorizes the worker to download missing OCR assets
    /// (<c>"1"</c> = allowed). Absent / any other value forbids downloads: the worker fails fast
    /// instead of fetching anything. Set by the engine only after the consent gate approves.</summary>
    public const string AllowDownloadEnvVar = "YAGU_OCR_ALLOW_DOWNLOAD";

    // Wire-protocol property names (PascalCase). These MUST match the worker's serialized output
    // exactly because JsonElement.TryGetProperty is case-sensitive.
    private const string PropType = "Type";
    private const string PropMessage = "Message";
    private const string PropId = "Id";
    private const string PropOk = "Ok";
    private const string PropText = "Text";
    private const string PropError = "Error";
    private const string PropPath = "Path";

    // BOM-less UTF-8 for the worker's stdin. Encoding.UTF8 emits a 3-byte BOM preamble on the first
    // write, which would prepend 0xEF 0xBB 0xBF to the first JSON request line and make the worker's
    // deserializer reject it (the request then never gets a reply and RecognizeAsync hangs forever).
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Process-wide guard so a missing worker is reported exactly once (avoids spamming yagu.log on
    // every image / every search when OCR is not installed).
    private static int _missingWorkerLogged;

    private readonly string? _workerPathOverride;
    private readonly bool _hasWorkerPathOverride;
    private readonly Func<ProcessStartInfo, IOcrWorkerProcess> _processFactory;
    private readonly OcrWorkerTrustVerifier _trustVerifier;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<OcrResult>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<OcrResult> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<OcrResult>? _initTask;
    private IOcrWorkerProcess? _process;
    private volatile bool _ready;
    private volatile bool _disposed;
    private int _nextId;

    protected WorkerOcrEngine()
        : this(
            workerPathOverride: null,
            hasWorkerPathOverride: false,
            OcrWorkerProcessFactory.Create,
            AuthenticodeVerifier.IsWorkerTrustedForHost,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(3))
    {
    }

    /// <summary>
    /// Test/diagnostics hook: forces the worker path to a specific value (authoritative — if the
    /// file does not exist, the engine reports the worker as unavailable instead of probing the
    /// standard locations).
    /// </summary>
    protected WorkerOcrEngine(string? workerPathOverride)
        : this(
            workerPathOverride,
            hasWorkerPathOverride: true,
            OcrWorkerProcessFactory.Create,
            AuthenticodeVerifier.IsWorkerTrustedForHost,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(3))
    {
    }

    internal WorkerOcrEngine(
        bool hasWorkerPathOverride,
        Func<ProcessStartInfo, IOcrWorkerProcess> processFactory,
        OcrWorkerTrustVerifier trustVerifier,
        TimeSpan readyTimeout,
        TimeSpan shutdownTimeout)
        : this(null, hasWorkerPathOverride, processFactory, trustVerifier, readyTimeout, shutdownTimeout)
    {
    }

    private WorkerOcrEngine(
        string? workerPathOverride,
        bool hasWorkerPathOverride,
        Func<ProcessStartInfo, IOcrWorkerProcess> processFactory,
        OcrWorkerTrustVerifier trustVerifier,
        TimeSpan readyTimeout,
        TimeSpan shutdownTimeout)
    {
        _workerPathOverride = workerPathOverride;
        _hasWorkerPathOverride = hasWorkerPathOverride;
        _processFactory = processFactory;
        _trustVerifier = trustVerifier;
        _readyTimeout = readyTimeout;
        _shutdownTimeout = shutdownTimeout;
    }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>Log channel name used for this engine's diagnostic messages.</summary>
    protected abstract string LogSource { get; }

    /// <summary>
    /// Lets a subclass set engine-specific environment variables on the worker process (e.g. the
    /// backend selector and any model name). Called once just before the worker is started.
    /// </summary>
    protected abstract void ConfigureWorkerEnvironment(IDictionary<string, string?> environment);

    /// <summary>
    /// Reports which native runtime / model assets this engine still needs to download before it can
    /// run. Used by <see cref="InitializeAsync"/> to warn (and require consent) before any external
    /// download. Must agree with the directories set in <see cref="ConfigureWorkerEnvironment"/>.
    /// </summary>
    protected abstract OcrAssetRequirement DescribeAssetRequirement();

    /// <summary>Reports what this engine still needs to download, without starting the worker.</summary>
    OcrAssetRequirement IOcrEngine.DescribeAssetRequirement() => DescribeAssetRequirement();

    /// <summary>Test/diagnostics hook: exposes <see cref="DescribeAssetRequirement"/> without starting the worker.</summary>
    internal OcrAssetRequirement DescribeAssetRequirementForTest() => DescribeAssetRequirement();

    /// <summary>
    /// Test/diagnostics hook: invokes <see cref="ConfigureWorkerEnvironment"/> against the supplied
    /// dictionary so the engine-specific environment (backend selector and any model) can be
    /// inspected without starting the worker process.
    /// </summary>
    internal void ConfigureWorkerEnvironmentForTest(IDictionary<string, string?> environment)
        => ConfigureWorkerEnvironment(environment);

    public async Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return OcrResult.Fail("OCR engine has been disposed.");
        }

        Task<OcrResult> init;
        lock (_gate)
        {
            init = _initTask ??= InitializeAsync();
        }

        try
        {
            // Honor the caller's cancellation without cancelling the shared (single-flight) init.
            return await init.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Fail("OCR initialization canceled.");
        }
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        OcrResult ready = await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!ready.Success)
        {
            return ready;
        }

        IOcrWorkerProcess process = _process!;
        if (process.HasExited)
        {
            return OcrResult.Fail("OCR worker is not running.");
        }

        int id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<OcrResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        string requestLine = BuildRequestLine(id, imagePath);

        bool writeLockTaken = false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            writeLockTaken = true;
            await process.StandardInput.WriteLineAsync(requestLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            return OcrResult.Fail("OCR canceled.");
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return OcrResult.Fail("Failed to send OCR request: " + ex.Message);
        }
        finally
        {
            if (writeLockTaken)
            {
                _writeLock.Release();
            }
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<OcrResult>)state!).TrySetCanceled(),
            tcs);

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            return OcrResult.Fail("OCR canceled.");
        }
    }

    private async Task<OcrResult> InitializeAsync()
    {
        string? workerPath = ResolveWorkerPath();
        if (workerPath is null)
        {
            LogMissingWorkerOnce();
            return OcrResult.Fail("OCR worker (Yagu.OcrWorker.exe) is not installed.");
        }

        // SECURITY: in a signed, shipped build, refuse to launch a worker that is not signed by the
        // same publisher as Yagu itself. This blocks an attacker who plants or tampers a worker exe
        // (via the YAGU_OCR_WORKER path override or by writing to the install dir) from running code
        // inside the signed app's process tree. In unsigned local/dev builds the host is unsigned, so
        // the check is a no-op and the freshly-built (unsigned) worker launches normally. The
        // _hasWorkerPathOverride seam is the internal test/diagnostics constructor only (never set by
        // the production factory), so exempting it does not weaken the shipped app.
        if (!_hasWorkerPathOverride && !_trustVerifier(workerPath, out string trustFailure))
        {
            YaguLog.For(LogSource).LogWarning(
                "Refusing to launch OCR worker \"{WorkerPath}\": {TrustFailure}. Image-text search is unavailable.",
                workerPath, trustFailure);
            return OcrResult.Fail("OCR worker failed signature verification.");
        }

        // Warn (and require consent) before initiating any external download. When the assets are
        // already present — bundled by the OCR-bundled installer or downloaded on a previous run —
        // no prompt is shown and the worker runs offline.
        OcrAssetRequirement requirement = DescribeAssetRequirement();
        bool downloadAllowed = true;
        if (requirement.DownloadNeeded)
        {
            downloadAllowed = await OcrDownloadGate.EnsureAllowedAsync(requirement).ConfigureAwait(false);
            if (!downloadAllowed)
            {
                string components = requirement.MissingComponents.Count > 0
                    ? " (" + string.Join(", ", requirement.MissingComponents) + ")"
                    : string.Empty;
                YaguLog.For(LogSource).LogDebug("OCR download not approved; image-text search is unavailable until the assets are downloaded.");
                return OcrResult.Fail(
                    $"Image-text (OCR) search needs a one-time download of about {requirement.ApproxMb} MB{components}, which was not approved.");
            }
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.StandardInputEncoding = Utf8NoBom;
            ConfigureWorkerEnvironment(startInfo.Environment);
            // Authorize the worker to download only when the user has consented this session.
            // When we believe the assets are already present (no prompt was shown) this stays "0",
            // so the worker fails fast rather than silently downloading if our presence check was
            // wrong — "no external download without consent" is enforced at the actual download site.
            startInfo.Environment[AllowDownloadEnvVar] = OcrDownloadGate.ConsentGranted ? "1" : "0";

            IOcrWorkerProcess process = _processFactory(startInfo);
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                return OcrResult.Fail("Failed to start OCR worker.");
            }

            _process = process;

            _ = Task.Run(() => PumpStandardErrorAsync(process.StandardError));
            _ = Task.Run(() => ReadLoopAsync(process.StandardOutput));

            // First run downloads the native runtime + models, which can be slow; cap it generously.
            using CancellationTokenSource timeout = new(_readyTimeout);
            OcrResult ready;
            try
            {
                ready = await _readyTcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ready = OcrResult.Fail("OCR worker did not become ready in time.");
            }

            _ready = ready.Success;
            if (!ready.Success)
            {
                YaguLog.For(LogSource).LogDebug("OCR worker init failed: {Error}", ready.Error);
            }

            return ready;
        }
        catch (Exception ex)
        {
            return OcrResult.Fail("OCR worker failed to start: " + ex.Message);
        }
    }

    private async Task ReadLoopAsync(StreamReader stdout)
    {
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                DispatchLine(line);
            }
        }
        catch (Exception ex)
        {
            YaguLog.For(LogSource).LogDebug("OCR worker read loop ended: {Error}", ex.Message);
        }
        finally
        {
            _readyTcs.TrySetResult(OcrResult.Fail("OCR worker exited before signaling ready."));
            FailAllPending("OCR worker output stream closed.");
        }
    }

    private void DispatchLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty(PropType, out JsonElement typeElement) ? typeElement.GetString() : null;

            switch (type)
            {
                case "ready":
                    _ready = true;
                    _readyTcs.TrySetResult(OcrResult.Ok(string.Empty));
                    break;

                case "error":
                    string message = root.TryGetProperty(PropMessage, out JsonElement messageElement)
                        ? messageElement.GetString() ?? "initialization error"
                        : "initialization error";
                    _readyTcs.TrySetResult(OcrResult.Fail("OCR worker initialization failed: " + message));
                    break;

                case "result":
                    int id = root.TryGetProperty(PropId, out JsonElement idElement) ? idElement.GetInt32() : -1;
                    bool ok = root.TryGetProperty(PropOk, out JsonElement okElement) && okElement.GetBoolean();
                    if (_pending.TryRemove(id, out TaskCompletionSource<OcrResult>? tcs))
                    {
                        if (ok)
                        {
                            string text = root.TryGetProperty(PropText, out JsonElement textElement)
                                ? textElement.GetString() ?? string.Empty
                                : string.Empty;
                            tcs.TrySetResult(OcrResult.Ok(text));
                        }
                        else
                        {
                            string error = root.TryGetProperty(PropError, out JsonElement errorElement)
                                ? errorElement.GetString() ?? "OCR failed"
                                : "OCR failed";
                            tcs.TrySetResult(OcrResult.Fail(error));
                        }
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            YaguLog.For(LogSource).LogDebug("OCR worker emitted an unparseable line: {Error}", ex.Message);
        }
    }

    private async Task PumpStandardErrorAsync(StreamReader stderr)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length != 0)
                {
                    YaguLog.For(LogSource).LogDebug("{Line}", line);
                }
            }
        }
        catch
        {
            // Worker exited; nothing more to log.
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _ready = false;
        _readyTcs.TrySetResult(OcrResult.Fail("OCR worker exited."));
        FailAllPending("OCR worker exited.");
    }

    private void FailAllPending(string reason)
    {
        foreach (KeyValuePair<int, TaskCompletionSource<OcrResult>> pair in _pending)
        {
            _pending.TryRemove(pair.Key, out _);
            pair.Value.TrySetResult(OcrResult.Fail(reason));
        }
    }

    internal static string BuildRequestLine(int id, string path)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(PropId, id);
            writer.WriteString(PropPath, path);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    protected virtual string? ResolveWorkerPath()
        => ResolveWorkerPath(
            _workerPathOverride,
            _hasWorkerPathOverride,
            Environment.GetEnvironmentVariable(WorkerPathEnvVar),
            AppContext.BaseDirectory,
            File.Exists);

    internal static string? ResolveWorkerPath(
        string? workerPathOverride,
        bool hasWorkerPathOverride,
        string? environmentOverride,
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        // An explicit override (tests/diagnostics) is authoritative.
        if (hasWorkerPathOverride)
        {
            return !string.IsNullOrEmpty(workerPathOverride) && fileExists(workerPathOverride)
                ? workerPathOverride
                : null;
        }

        // An explicit environment override is also authoritative: a wrong path means "no worker"
        // rather than silently falling back to a different binary.
        if (!string.IsNullOrEmpty(environmentOverride))
        {
            return fileExists(environmentOverride) ? environmentOverride : null;
        }

        // SECURITY (binary planting): load the worker ONLY from the app's own install directory. We
        // deliberately do NOT probe a per-user-writable location such as %LOCALAPPDATA%. A signed app
        // that auto-executes an .exe from a predictable user-writable path lets any user-level process
        // (non-admin malware) plant a malicious "Yagu.OcrWorker.exe" there and have it run inside Yagu's
        // process tree on the next image search — a trust-laundering / persistence vector. The install
        // directory is protected by its own ACLs; an attacker able to write there could already replace
        // Yagu.exe itself, so it is no weaker than the app's own trust boundary.
        string besideApp = ResolveBundledWorkerPath(baseDirectory);
        if (fileExists(besideApp))
        {
            return besideApp;
        }

        return null;
    }

    /// <summary>
    /// Emits a single, actionable warning the first time the OCR worker can't be located. Without
    /// this, a missing <c>Yagu.OcrWorker.exe</c> silently yields zero image-text matches with no
    /// trace in <c>yagu.log</c>, which is hard to diagnose. Logged at most once per process.
    /// </summary>
    private void LogMissingWorkerOnce()
    {
        if (Interlocked.Exchange(ref _missingWorkerLogged, 1) != 0)
        {
            return;
        }

        string besideApp = ResolveBundledWorkerPath(AppContext.BaseDirectory);
        YaguLog.For(LogSource).LogWarning(
            "Image-text (OCR) search is unavailable: Yagu.OcrWorker.exe was not found, so image files " +
            "cannot be scanned and OCR searches will return no matches. Probed: {EnvVar} " +
            "environment variable and \"{BesideApp}\".",
            WorkerPathEnvVar, besideApp);
    }

    internal static string ResolveBundledWorkerPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string normalized = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string appDirectory = string.Equals(Path.GetFileName(normalized), "index-worker", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(normalized, ".."))
            : normalized;
        return Path.Combine(appDirectory, "ocr-worker", "Yagu.OcrWorker.exe");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        GC.SuppressFinalize(this);
        _disposed = true;
        IOcrWorkerProcess? process = _process;
        _process = null;
        if (process is null)
        {
            _writeLock.Dispose();
            return;
        }

        try
        {
            try
            {
                process.StandardInput.Dispose();
            }
            catch
            {
                // Ignore — we are tearing down anyway.
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore — process may have already exited.
                }
            }
        }
        finally
        {
            FailAllPending("OCR engine disposed.");
            process.Dispose();
            _writeLock.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        GC.SuppressFinalize(this);
        _disposed = true;
        IOcrWorkerProcess? process = _process;
        _process = null;
        if (process is null)
        {
            _writeLock.Dispose();
            return;
        }

        try
        {
            // Closing stdin signals the worker to drain and exit cleanly.
            try
            {
                process.StandardInput.Dispose();
            }
            catch
            {
                // Ignore.
            }

            if (!process.HasExited)
            {
                using CancellationTokenSource timeout = new(_shutdownTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Timed out or failed; fall through to Kill.
                }
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore — process may have already exited.
                }
            }
        }
        finally
        {
            FailAllPending("OCR engine disposed.");
            process.Dispose();
            _writeLock.Dispose();
        }
    }
}
