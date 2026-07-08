using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;

namespace Yagu.Services.Ai.Worker;

/// <summary>Raised when the semantic worker reports that an op failed (<c>ok=false</c>).</summary>
public sealed class SemanticWorkerException(string message) : Exception(message);

/// <summary>
/// In-app proxy that implements <see cref="ISemanticQueryTranslator"/> by driving the out-of-process
/// <c>Yagu.SemanticWorker.exe</c> host over a line-delimited-JSON protocol (<see cref="SemanticWorkerProtocol"/>).
/// The Foundry Local SDK therefore runs in the WORKER process; when it fail-fasts (the ObjectDisposedException
/// on a ThreadPool progress callback, or the onnxruntime-genai use-after-free access violation) the worker
/// dies but Yagu stays alive — this proxy surfaces the failure as a normal <see cref="SemanticTranslationResult"/>
/// failure (or an empty model list) and respawns the worker on the next use, replaying the current config.
/// This type is AOT-safe (source-gen JSON only) so it can live in the Native-AOT main app.
/// </summary>
public sealed class WorkerSemanticQueryTranslator : ISemanticQueryTranslator, IAsyncDisposable
{
    private const string LogSource = "Semantic.WorkerProxy";
    private const string WorkerEnvVar = "YAGU_SEMANTIC_WORKER";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static int _missingWorkerLogged;

    private readonly SemaphoreSlim _spawnLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();

    private int _nextId;
    private Process? _proc;
    private TextWriter? _stdin;
    private bool _disposed;

    // Config snapshot — mirrored locally so it can be REPLAYED to a freshly (re)spawned worker.
    private bool _enabled;
    private string? _deviceOrder;
    private string? _modelOverride;
    private bool _hasGpu = true;
    private bool _hasNpu = true;
    private long _gpuMemoryBytes;
    private bool _unloadAfterUse;
    private volatile string? _currentModelKey;

    public WorkerSemanticQueryTranslator(bool enabled, string? modelOverrideAlias = null, string? devicePreferenceOrder = null)
    {
        _enabled = enabled;
        _modelOverride = string.IsNullOrWhiteSpace(modelOverrideAlias) ? null : modelOverrideAlias.Trim();
        _deviceOrder = devicePreferenceOrder;
    }

    /// <inheritdoc />
    public bool IsAvailable => _enabled;

    /// <inheritdoc />
    public string? CurrentModelKey => _currentModelKey;

    // ── ISemanticQueryTranslator: translation ────────────────────────────────────────────────────

    public async Task<SemanticTranslationResult> TranslateAsync(
        string naturalLanguageQuery, SemanticTranslationContext context,
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            WorkerMessage msg = await SendRequestAsync(
                BuildTranslateRequest(naturalLanguageQuery, context, streaming: false),
                progress, onToken: null, cancellationToken).ConfigureAwait(false);
            return ToResult(msg);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SemanticTranslationResult.Fail($"The local AI worker could not translate the query: {ex.Message}");
        }
    }

    public async Task<SemanticTranslationResult> TranslateStreamingAsync(
        string naturalLanguageQuery, SemanticTranslationContext context,
        Action<string>? onToken, CancellationToken cancellationToken)
    {
        try
        {
            WorkerMessage msg = await SendRequestAsync(
                BuildTranslateRequest(naturalLanguageQuery, context, streaming: onToken is not null),
                progress: null, onToken, cancellationToken).ConfigureAwait(false);
            return ToResult(msg);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SemanticTranslationResult.Fail($"The local AI worker could not translate the query: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SemanticModelOption>> ListModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_enabled) return Array.Empty<SemanticModelOption>();
        try
        {
            WorkerMessage msg = await SendRequestAsync(
                new WorkerRequest { Op = SemanticWorkerProtocol.Ops.ListModels },
                progress, onToken: null, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(msg.OptionsJson)) return Array.Empty<SemanticModelOption>();
            return JsonSerializer.Deserialize(msg.OptionsJson, SemanticWorkerJsonContext.Default.ListSemanticModelOption)
                ?? (IReadOnlyList<SemanticModelOption>)Array.Empty<SemanticModelOption>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource, $"listing model options failed: {ex.Message}");
            return Array.Empty<SemanticModelOption>();
        }
    }

    public async Task PrepareModelAsync(
        string? modelAlias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            new WorkerRequest { Op = SemanticWorkerProtocol.Ops.PrepareModel, Alias = modelAlias },
            progress, onToken: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnloadCurrentModelAsync(CancellationToken cancellationToken)
    {
        // Best-effort and never throws (interface contract). No point spawning a worker just to unload.
        if (_stdin is null || _proc is null || _proc.HasExited) return;
        try
        {
            await SendRequestAsync(
                new WorkerRequest { Op = SemanticWorkerProtocol.Ops.UnloadModel },
                progress: null, onToken: null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ignore — model eviction is opportunistic
        }
    }

    // ── ISemanticQueryTranslator: config (mirrored locally + pushed to a live worker) ─────────────

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        SendConfig(SemanticWorkerProtocol.Ops.SetEnabled, boolValue: enabled);
    }

    public void SetDevicePreferenceOrder(string? order)
    {
        _deviceOrder = order;
        SendConfig(SemanticWorkerProtocol.Ops.SetDeviceOrder, stringValue: order);
    }

    public void SetModelOverride(string? modelAlias)
    {
        _modelOverride = string.IsNullOrWhiteSpace(modelAlias) ? null : modelAlias.Trim();
        _currentModelKey = _modelOverride ?? _currentModelKey;
        SendConfig(SemanticWorkerProtocol.Ops.SetModelOverride, stringValue: _modelOverride);
    }

    public void SetAvailableAccelerators(bool hasGpu, bool hasNpu)
    {
        _hasGpu = hasGpu;
        _hasNpu = hasNpu;
        SendConfig(SemanticWorkerProtocol.Ops.SetAccelerators, boolValue: hasGpu, boolValue2: hasNpu);
    }

    public void SetGpuMemoryBytes(long dedicatedVideoMemoryBytes)
    {
        _gpuMemoryBytes = dedicatedVideoMemoryBytes;
        SendConfig(SemanticWorkerProtocol.Ops.SetGpuMemory, longValue: dedicatedVideoMemoryBytes);
    }

    public void SetUnloadAfterUse(bool unloadAfterUse)
    {
        _unloadAfterUse = unloadAfterUse;
        SendConfig(SemanticWorkerProtocol.Ops.SetUnloadAfterUse, boolValue: unloadAfterUse);
    }

    public void RefreshCatalog() => SendConfig(SemanticWorkerProtocol.Ops.RefreshCatalog);

    // ── Worker lifecycle ─────────────────────────────────────────────────────────────────────────

    private async Task<bool> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_enabled) return false;
        if (_stdin is not null && _proc is { HasExited: false }) return true;

        await _spawnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || !_enabled) return false;
            if (_stdin is not null && _proc is { HasExited: false }) return true;
            return await SpawnAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _spawnLock.Release();
        }
    }

    private async Task<bool> SpawnAsync(CancellationToken cancellationToken)
    {
        // A previous worker may have crashed with requests still pending; fail them before restarting.
        FaultAllPending(new SemanticWorkerException("semantic worker restarting"));

        string? exe = ResolveWorkerPath();
        if (exe is null)
        {
            LogMissingWorkerOnce();
            return false;
        }

        // SECURITY: in a signed, shipped build, refuse to launch a worker that is not signed by the
        // same publisher as Yagu itself. This blocks a planted or tampered worker (via the
        // YAGU_SEMANTIC_WORKER path override or a writable install dir) from running inside the signed
        // app's process tree. In unsigned local/dev builds the host is unsigned, so this is a no-op.
        if (!AuthenticodeVerifier.IsWorkerTrustedForHost(exe, out string trustFailure))
        {
            LogService.Instance.Warning(LogSource, $"refusing to launch semantic worker \"{exe}\": {trustFailure}.");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // BOM-less UTF-8 stdin so the worker's first JSON line isn't prefixed with EF BB BF.
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => OnWorkerExited(proc);

        try
        {
            if (!proc.Start())
            {
                LogService.Instance.Warning(LogSource, "semantic worker failed to start.");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource, $"semantic worker failed to start: {ex.Message}");
            return false;
        }

        _proc = proc;
        _stdin = proc.StandardInput;
        _ = Task.Run(() => ReadStdoutAsync(proc, ready), CancellationToken.None);
        _ = Task.Run(() => ReadStderrAsync(proc), CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ReadyTimeout);
        try
        {
            await ready.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            LogService.Instance.Warning(LogSource, "semantic worker did not signal ready in time.");
            TryKill(proc);
            return false;
        }

        await ReplayConfigAsync().ConfigureAwait(false);
        LogService.Instance.Info(LogSource, $"semantic worker ready (pid {SafeId(proc)}).");
        return true;
    }

    private async Task ReadStdoutAsync(Process proc, TaskCompletionSource<bool> ready)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                WorkerMessage? msg;
                try { msg = JsonSerializer.Deserialize(line, SemanticWorkerJsonContext.Default.WorkerMessage); }
                catch { continue; }
                if (msg is not null) RouteMessage(msg, ready);
            }
        }
        catch
        {
            // stdout closed (worker exiting) — OnWorkerExited handles pending requests.
        }
    }

    private static async Task ReadStderrAsync(Process proc)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                // Forward worker diagnostics into the main app's log (the worker never writes the file itself).
                LogService.Instance.Verbose("Semantic.Worker", line);
            }
        }
        catch
        {
            // stderr closed — nothing to do.
        }
    }

    private void RouteMessage(WorkerMessage msg, TaskCompletionSource<bool> ready)
    {
        switch (msg.Type)
        {
            case SemanticWorkerProtocol.MessageTypes.Ready:
                ready.TrySetResult(true);
                return;
            case SemanticWorkerProtocol.MessageTypes.Progress:
                if (_pending.TryGetValue(msg.Id, out var pp)) pp.Progress?.Report(ToProgress(msg));
                return;
            case SemanticWorkerProtocol.MessageTypes.Token:
                if (_pending.TryGetValue(msg.Id, out var pt)) pt.OnToken?.Invoke(msg.Delta ?? string.Empty);
                return;
            default: // result | models | ack — terminal
                if (!string.IsNullOrEmpty(msg.CurrentModelKey)) _currentModelKey = msg.CurrentModelKey;
                if (_pending.TryRemove(msg.Id, out var pr)) pr.Terminal.TrySetResult(msg);
                return;
        }
    }

    private void OnWorkerExited(Process proc)
    {
        if (ReferenceEquals(_proc, proc))
        {
            _stdin = null;
        }
        if (!_disposed)
        {
            LogService.Instance.Warning(LogSource,
                $"semantic worker exited (code {SafeExitCode(proc)}); in-flight requests fail and it respawns on next use.");
        }
        FaultAllPending(new SemanticWorkerException("the semantic worker process exited"));
    }

    private void FaultAllPending(Exception ex)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var p)) p.Terminal.TrySetException(ex);
        }
    }

    // ── Request/response ─────────────────────────────────────────────────────────────────────────

    private async Task<WorkerMessage> SendRequestAsync(
        WorkerRequest req, IProgress<SemanticTranslationProgress>? progress, Action<string>? onToken, CancellationToken ct)
    {
        if (!await EnsureWorkerAsync(ct).ConfigureAwait(false))
            throw new SemanticWorkerException("the semantic worker is unavailable");

        int id = Interlocked.Increment(ref _nextId);
        req.Id = id;
        var pending = new Pending(progress, onToken);
        _pending[id] = pending;
        await using var reg = ct.Register(static state => SendCancelStatic(state), (this, id)).ConfigureAwait(false);
        try
        {
            await SendLineAsync(req).ConfigureAwait(false);
            WorkerMessage terminal = await pending.Terminal.Task.WaitAsync(ct).ConfigureAwait(false);
            if (!terminal.Ok)
                throw new SemanticWorkerException(terminal.Error ?? "the semantic worker reported a failure");
            return terminal;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static void SendCancelStatic(object? state)
    {
        if (state is (WorkerSemanticQueryTranslator self, int id))
            _ = self.SendFireAndForgetAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.Cancel, Id = id });
    }

    private async Task SendLineAsync(WorkerRequest req)
    {
        string json = JsonSerializer.Serialize(req, SemanticWorkerJsonContext.Default.WorkerRequest);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            TextWriter stdin = _stdin ?? throw new SemanticWorkerException("semantic worker stdin is unavailable");
            await stdin.WriteLineAsync(json).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendFireAndForgetAsync(WorkerRequest req)
    {
        try { await SendLineAsync(req).ConfigureAwait(false); }
        catch { /* worker gone — nothing to do */ }
    }

    /// <summary>Applies a config setter to a LIVE worker. When no worker is running the change is only
    /// recorded locally and replayed by <see cref="ReplayConfigAsync"/> on the next spawn.</summary>
    private void SendConfig(string op, bool boolValue = false, bool boolValue2 = false, string? stringValue = null, long longValue = 0)
    {
        if (_disposed || _stdin is null || _proc is null || _proc.HasExited) return;
        _ = SendFireAndForgetAsync(new WorkerRequest
        {
            Op = op,
            BoolValue = boolValue,
            BoolValue2 = boolValue2,
            StringValue = stringValue,
            LongValue = longValue,
        });
    }

    private async Task ReplayConfigAsync()
    {
        await SendLineAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.SetEnabled, BoolValue = _enabled }).ConfigureAwait(false);
        await SendLineAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.SetAccelerators, BoolValue = _hasGpu, BoolValue2 = _hasNpu }).ConfigureAwait(false);
        await SendLineAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.SetGpuMemory, LongValue = _gpuMemoryBytes }).ConfigureAwait(false);
        await SendLineAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.SetUnloadAfterUse, BoolValue = _unloadAfterUse }).ConfigureAwait(false);
        if (_deviceOrder is not null)
            await SendLineAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.SetDeviceOrder, StringValue = _deviceOrder }).ConfigureAwait(false);
        await SendLineAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.SetModelOverride, StringValue = _modelOverride }).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static SemanticTranslationResult ToResult(WorkerMessage msg)
    {
        if (!msg.TranslationSuccess)
            return SemanticTranslationResult.Fail(msg.Error ?? "translation failed", msg.RawModelOutput);
        SemanticSearchPlan? plan = string.IsNullOrEmpty(msg.PlanJson)
            ? null
            : JsonSerializer.Deserialize(msg.PlanJson, SemanticSearchPlanJsonContext.Default.SemanticSearchPlan);
        return plan is null
            ? SemanticTranslationResult.Fail("the semantic worker returned no plan", msg.RawModelOutput)
            : SemanticTranslationResult.Ok(plan, msg.RawModelOutput);
    }

    private static SemanticTranslationProgress ToProgress(WorkerMessage msg)
    {
        SemanticTranslationStage stage = Enum.TryParse(msg.Stage, out SemanticTranslationStage s)
            ? s
            : SemanticTranslationStage.Initializing;
        return new SemanticTranslationProgress { Stage = stage, Percent = msg.Percent, Detail = msg.Detail };
    }

    private static WorkerRequest BuildTranslateRequest(string query, SemanticTranslationContext context, bool streaming) =>
        new()
        {
            Op = SemanticWorkerProtocol.Ops.Translate,
            Query = query,
            Streaming = streaming,
            DefaultDirectory = context.DefaultDirectory,
            OriginalQuery = context.OriginalQuery,
            NowIso = context.Now.ToString("o", CultureInfo.InvariantCulture),
        };

    private static string? ResolveWorkerPath()
    {
        string? env = Environment.GetEnvironmentVariable(WorkerEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        string local = Path.Combine(AppContext.BaseDirectory, "semantic-worker", "Yagu.SemanticWorker.exe");
        return File.Exists(local) ? local : null;
    }

    private static void LogMissingWorkerOnce()
    {
        if (Interlocked.Exchange(ref _missingWorkerLogged, 1) != 0) return;
        string local = Path.Combine(AppContext.BaseDirectory, "semantic-worker", "Yagu.SemanticWorker.exe");
        LogService.Instance.Warning(LogSource,
            $"Yagu.SemanticWorker.exe not found (probed {WorkerEnvVar} and '{local}'); AI (semantic) search is unavailable.");
    }

    private static int SafeId(Process p) { try { return p.Id; } catch { return -1; } }
    private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(CultureInfo.InvariantCulture); } catch { return "?"; } }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Process? proc = _proc;
        if (proc is { HasExited: false })
        {
            try { await SendFireAndForgetAsync(new WorkerRequest { Op = SemanticWorkerProtocol.Ops.Shutdown }).ConfigureAwait(false); }
            catch { /* ignore */ }
            try { await Task.WhenAny(WaitForExitAsync(proc), Task.Delay(1500)).ConfigureAwait(false); }
            catch { /* ignore */ }
            TryKill(proc);
        }
        FaultAllPending(new SemanticWorkerException("the semantic worker proxy was disposed"));
        _spawnLock.Dispose();
        _sendLock.Dispose();
    }

    private static async Task WaitForExitAsync(Process p)
    {
        try { await p.WaitForExitAsync().ConfigureAwait(false); } catch { /* ignore */ }
    }

    /// <summary>An outstanding request awaiting its terminal message, plus its streaming callbacks.</summary>
    private sealed class Pending(IProgress<SemanticTranslationProgress>? progress, Action<string>? onToken)
    {
        public TaskCompletionSource<WorkerMessage> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<SemanticTranslationProgress>? Progress { get; } = progress;
        public Action<string>? OnToken { get; } = onToken;
    }
}
