using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Yagu.Services.Ocr;

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
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<OcrResult>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<OcrResult> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<OcrResult>? _initTask;
    private Process? _process;
    private TextWriter? _stdin;
    private volatile bool _ready;
    private volatile bool _disposed;
    private int _nextId;

    protected WorkerOcrEngine()
    {
    }

    /// <summary>
    /// Test/diagnostics hook: forces the worker path to a specific value (authoritative — if the
    /// file does not exist, the engine reports the worker as unavailable instead of probing the
    /// standard locations).
    /// </summary>
    protected WorkerOcrEngine(string? workerPathOverride)
    {
        _workerPathOverride = workerPathOverride;
        _hasWorkerPathOverride = true;
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

        Process? process = _process;
        TextWriter? stdin = _stdin;
        if (process is null || stdin is null || process.HasExited)
        {
            return OcrResult.Fail("OCR worker is not running.");
        }

        int id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<OcrResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        string requestLine = BuildRequestLine(id, imagePath);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stdin.WriteLineAsync(requestLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return OcrResult.Fail("Failed to send OCR request: " + ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }

        await using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            (ConcurrentDictionary<int, TaskCompletionSource<OcrResult>> pending, int requestId, CancellationToken token) =
                ((ConcurrentDictionary<int, TaskCompletionSource<OcrResult>>, int, CancellationToken))state!;
            if (pending.TryRemove(requestId, out TaskCompletionSource<OcrResult>? pendingTcs))
            {
                pendingTcs.TrySetCanceled(token);
            }
        }, (_pending, id, cancellationToken));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                return OcrResult.Fail("Failed to start OCR worker.");
            }

            _process = process;
            _stdin = process.StandardInput;

            _ = Task.Run(() => PumpStandardErrorAsync(process.StandardError));
            _ = Task.Run(() => ReadLoopAsync(process.StandardOutput));

            // First run downloads the native runtime + models, which can be slow; cap it generously.
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(30));
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
                LogService.Instance.Verbose(LogSource, "OCR worker init failed: " + ready.Error);
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
            LogService.Instance.Verbose(LogSource, "OCR worker read loop ended: " + ex.Message);
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
            LogService.Instance.Verbose(LogSource, "OCR worker emitted an unparseable line: " + ex.Message);
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
                    LogService.Instance.Verbose(LogSource, line);
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
        foreach (int key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out TaskCompletionSource<OcrResult>? tcs))
            {
                tcs.TrySetResult(OcrResult.Fail(reason));
            }
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

    private string? ResolveWorkerPath()
    {
        // An explicit override (tests/diagnostics) is authoritative.
        if (_hasWorkerPathOverride)
        {
            return !string.IsNullOrEmpty(_workerPathOverride) && File.Exists(_workerPathOverride)
                ? _workerPathOverride
                : null;
        }

        // An explicit environment override is also authoritative: a wrong path means "no worker"
        // rather than silently falling back to a different binary.
        string? overridePath = Environment.GetEnvironmentVariable(WorkerPathEnvVar);
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string provisioned = Path.Combine(localAppData, "Yagu", "ocr-runtime", "worker", "Yagu.OcrWorker.exe");
        if (File.Exists(provisioned))
        {
            return provisioned;
        }

        string besideApp = Path.Combine(AppContext.BaseDirectory, "ocr-worker", "Yagu.OcrWorker.exe");
        if (File.Exists(besideApp))
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

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string provisioned = Path.Combine(localAppData, "Yagu", "ocr-runtime", "worker", "Yagu.OcrWorker.exe");
        string besideApp = Path.Combine(AppContext.BaseDirectory, "ocr-worker", "Yagu.OcrWorker.exe");
        LogService.Instance.Warning(
            LogSource,
            "Image-text (OCR) search is unavailable: Yagu.OcrWorker.exe was not found, so image files " +
            "cannot be scanned and OCR searches will return no matches. Probed: " + WorkerPathEnvVar +
            " environment variable, \"" + provisioned + "\", \"" + besideApp + "\".");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        GC.SuppressFinalize(this);
        _disposed = true;
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            try
            {
                _stdin?.Dispose();
            }
            catch
            {
                // Ignore — we are tearing down anyway.
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
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
        Process? process = _process;
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
                _stdin?.Dispose();
            }
            catch
            {
                // Ignore.
            }

            if (!process.HasExited)
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
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
                    process.Kill(entireProcessTree: true);
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
