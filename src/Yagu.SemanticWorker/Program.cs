using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Ai;
using Yagu.Services.Ai.Worker;

namespace Yagu.SemanticWorker;

/// <summary>
/// Out-of-process host for <see cref="FoundryLocalSemanticQueryTranslator"/>. Reads line-delimited-JSON
/// <see cref="WorkerRequest"/>s on stdin and writes <see cref="WorkerMessage"/>s on stdout
/// (<see cref="SemanticWorkerProtocol"/>); stderr is diagnostics only. Isolating the Foundry Local SDK
/// here means an SDK fail-fast (ObjectDisposedException on a ThreadPool progress callback, or the
/// onnxruntime-genai use-after-free access violation) kills THIS process, not Yagu — the in-app proxy
/// detects the exit and recovers.
/// </summary>
internal static class Program
{
    private const string LogSource = "SemanticWorker";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly object OutLock = new();
    private static readonly SemaphoreSlim WorkLock = new(1, 1);
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> InFlight = new();

    private static TextWriter _protocolOut = TextWriter.Null;
    private static FoundryLocalSemanticQueryTranslator _translator = null!;

    private static async Task<int> Main()
    {
        // Capture the REAL stdout for the protocol BEFORE redirecting Console.* to stderr, so any library
        // (or LogService) Console write can never corrupt the JSON channel. BOM-less UTF-8, explicit flush.
        _protocolOut = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = false };
        Console.SetOut(Console.Error);

        // Never write to the shared %APPDATA%\Yagu\yagu.log from here — the main app owns that file and two
        // processes appending would interleave/contend. Diagnostics go to stderr; the proxy forwards them.
        LogService.Instance.FileLevel = LogLevel.None;
        LogService.Instance.ConsoleLevel = LogLevel.Info;

        _translator = new FoundryLocalSemanticQueryTranslator(enabled: true);

        using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);

        Send(new WorkerMessage { Type = SemanticWorkerProtocol.MessageTypes.Ready });

        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            // Defense-in-depth against a stray BOM / zero-width prefix on the first line.
            line = line.Trim('\uFEFF', '\u200B').Trim();
            if (line.Length == 0)
                continue;

            WorkerRequest? req;
            try
            {
                req = JsonSerializer.Deserialize(line, SemanticWorkerJsonContext.Default.WorkerRequest);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning(LogSource, $"bad request json: {ex.Message}");
                continue;
            }

            if (req is null)
                continue;

            // Handled inline on the reader thread so they are ordered and never blocked by a running op:
            //  - shutdown ends the loop,
            //  - cancel must interrupt an in-flight op,
            //  - config setters must apply before the next translate.
            if (req.Op == SemanticWorkerProtocol.Ops.Shutdown)
                break;

            if (req.Op == SemanticWorkerProtocol.Ops.Cancel)
            {
                if (InFlight.TryGetValue(req.Id, out var cts))
                {
                    try { cts.Cancel(); } catch { /* already disposed / completed */ }
                }
                continue;
            }

            if (TryApplyConfig(req))
                continue;

            // Long-running ops run as a task so the reader loop keeps accepting cancel/config while they run;
            // WorkLock serializes the actual SDK work so two native ops never overlap.
            _ = HandleAsync(req);
        }

        try { await _translator.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        return 0;
    }

    /// <summary>Applies a config/setter op synchronously (in stdin order). Returns false when
    /// <paramref name="req"/> is not a setter.</summary>
    private static bool TryApplyConfig(WorkerRequest req)
    {
        switch (req.Op)
        {
            case SemanticWorkerProtocol.Ops.SetEnabled: _translator.SetEnabled(req.BoolValue); return true;
            case SemanticWorkerProtocol.Ops.SetDeviceOrder: _translator.SetDevicePreferenceOrder(req.StringValue); return true;
            case SemanticWorkerProtocol.Ops.SetModelOverride: _translator.SetModelOverride(req.StringValue); return true;
            case SemanticWorkerProtocol.Ops.SetAccelerators: _translator.SetAvailableAccelerators(req.BoolValue, req.BoolValue2); return true;
            case SemanticWorkerProtocol.Ops.SetGpuMemory: _translator.SetGpuMemoryBytes(req.LongValue); return true;
            case SemanticWorkerProtocol.Ops.SetUnloadAfterUse: _translator.SetUnloadAfterUse(req.BoolValue); return true;
            case SemanticWorkerProtocol.Ops.RefreshCatalog: _translator.RefreshCatalog(); return true;
            default: return false;
        }
    }

    private static async Task HandleAsync(WorkerRequest req)
    {
        var cts = new CancellationTokenSource();
        InFlight[req.Id] = cts;
        await WorkLock.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (req.Op)
            {
                case SemanticWorkerProtocol.Ops.Translate:
                    await HandleTranslateAsync(req, cts.Token).ConfigureAwait(false);
                    break;
                case SemanticWorkerProtocol.Ops.ListModels:
                    await HandleListModelsAsync(req, cts.Token).ConfigureAwait(false);
                    break;
                case SemanticWorkerProtocol.Ops.PrepareModel:
                    await _translator.PrepareModelAsync(req.Alias, new MessageProgress(req.Id), cts.Token).ConfigureAwait(false);
                    SendAck(req.Id, ok: true, error: null);
                    break;
                case SemanticWorkerProtocol.Ops.UnloadModel:
                    await _translator.UnloadCurrentModelAsync(cts.Token).ConfigureAwait(false);
                    SendAck(req.Id, ok: true, error: null);
                    break;
                default:
                    SendAck(req.Id, ok: false, error: $"unknown op '{req.Op}'");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SendAck(req.Id, ok: false, error: "cancelled");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource, $"op '{req.Op}' (#{req.Id}) failed: {ex.Message}");
            SendAck(req.Id, ok: false, error: ex.Message);
        }
        finally
        {
            WorkLock.Release();
            InFlight.TryRemove(req.Id, out _);
            cts.Dispose();
        }
    }

    private static async Task HandleTranslateAsync(WorkerRequest req, CancellationToken ct)
    {
        var context = new SemanticTranslationContext
        {
            Now = ParseNow(req.NowIso),
            DefaultDirectory = req.DefaultDirectory,
            OriginalQuery = req.OriginalQuery,
            // Same machine as the host, so the local filesystem probe is equivalent to the host's.
            DirectoryExists = Directory.Exists,
        };

        SemanticTranslationResult result = req.Streaming
            ? await _translator.TranslateStreamingAsync(req.Query ?? "", context, delta => SendToken(req.Id, delta), ct).ConfigureAwait(false)
            : await _translator.TranslateAsync(req.Query ?? "", context, new MessageProgress(req.Id), ct).ConfigureAwait(false);

        string? planJson = result.Plan is null
            ? null
            : JsonSerializer.Serialize(result.Plan, SemanticSearchPlanJsonContext.Default.SemanticSearchPlan);

        Send(new WorkerMessage
        {
            Type = SemanticWorkerProtocol.MessageTypes.Result,
            Id = req.Id,
            Ok = true,
            TranslationSuccess = result.Success,
            PlanJson = planJson,
            RawModelOutput = result.RawModelOutput,
            Error = result.Error,
            CurrentModelKey = _translator.CurrentModelKey,
        });
    }

    private static async Task HandleListModelsAsync(WorkerRequest req, CancellationToken ct)
    {
        IReadOnlyList<SemanticModelOption> options =
            await _translator.ListModelOptionsAsync(new MessageProgress(req.Id), ct).ConfigureAwait(false);

        string optionsJson = JsonSerializer.Serialize(
            new List<SemanticModelOption>(options), SemanticWorkerJsonContext.Default.ListSemanticModelOption);

        Send(new WorkerMessage
        {
            Type = SemanticWorkerProtocol.MessageTypes.Models,
            Id = req.Id,
            Ok = true,
            OptionsJson = optionsJson,
            CurrentModelKey = _translator.CurrentModelKey,
        });
    }

    private static DateTimeOffset ParseNow(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTimeOffset.Now;

    private static void SendToken(int id, string delta) =>
        Send(new WorkerMessage { Type = SemanticWorkerProtocol.MessageTypes.Token, Id = id, Delta = delta });

    private static void SendProgress(int id, SemanticTranslationProgress p) =>
        Send(new WorkerMessage
        {
            Type = SemanticWorkerProtocol.MessageTypes.Progress,
            Id = id,
            Stage = p.Stage.ToString(),
            Percent = p.Percent,
            Detail = p.Detail,
        });

    private static void SendAck(int id, bool ok, string? error) =>
        Send(new WorkerMessage
        {
            Type = SemanticWorkerProtocol.MessageTypes.Ack,
            Id = id,
            Ok = ok,
            Error = error,
            CurrentModelKey = _translator.CurrentModelKey,
        });

    private static void Send(WorkerMessage msg)
    {
        string json = JsonSerializer.Serialize(msg, SemanticWorkerJsonContext.Default.WorkerMessage);
        lock (OutLock)
        {
            _protocolOut.WriteLine(json);
            _protocolOut.Flush();
        }
    }

    /// <summary>Forwards the translator's <see cref="IProgress{T}"/> reports to the host as progress
    /// messages. The translator calls <c>Report</c> synchronously during an op, so progress is always
    /// emitted before that op's terminal message.</summary>
    private sealed class MessageProgress(int id) : IProgress<SemanticTranslationProgress>
    {
        public void Report(SemanticTranslationProgress value) => SendProgress(id, value);
    }
}
