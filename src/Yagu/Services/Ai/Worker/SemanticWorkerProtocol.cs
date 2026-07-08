using System.Collections.Generic;
using System.Text.Json.Serialization;
using Yagu.Models;

namespace Yagu.Services.Ai.Worker;

/// <summary>
/// Line-delimited-JSON protocol shared by <see cref="WorkerSemanticQueryTranslator"/> (the in-app proxy)
/// and the out-of-process <c>Yagu.SemanticWorker.exe</c> host. One JSON object per line over the worker's
/// stdin (requests) and stdout (messages); stderr is diagnostics only.
///
/// This file is compiled into BOTH the main app (AOT) and the worker (non-AOT) via a shared
/// <c>&lt;Compile Include&gt;</c>, so both sides always agree on the wire shape. The two DTOs are kept
/// deliberately flat (only strings / numbers / bools) so the source-gen JSON context stays trivial and
/// AOT-safe; the richer domain payloads (<see cref="SemanticSearchPlan"/> and the model-option list) ride
/// as pre-serialized JSON strings (<see cref="WorkerMessage.PlanJson"/> / <see cref="WorkerMessage.OptionsJson"/>),
/// serialized on each side with the domain type's own converters/context.
/// </summary>
internal static class SemanticWorkerProtocol
{
    /// <summary>Request op names (host → worker), 1:1 with <see cref="ISemanticQueryTranslator"/> methods.</summary>
    internal static class Ops
    {
        public const string Translate = "translate";
        public const string ListModels = "listModels";
        public const string PrepareModel = "prepareModel";
        public const string UnloadModel = "unloadModel";
        public const string SetEnabled = "setEnabled";
        public const string SetDeviceOrder = "setDeviceOrder";
        public const string SetModelOverride = "setModelOverride";
        public const string SetAccelerators = "setAccelerators";
        public const string SetGpuMemory = "setGpuMemory";
        public const string SetUnloadAfterUse = "setUnloadAfterUse";
        public const string RefreshCatalog = "refreshCatalog";
        public const string Cancel = "cancel";
        public const string Shutdown = "shutdown";
    }

    /// <summary>Message type names (worker → host).</summary>
    internal static class MessageTypes
    {
        public const string Ready = "ready";
        public const string Token = "token";
        public const string Progress = "progress";
        public const string Result = "result";
        public const string Models = "models";
        public const string Ack = "ack";
    }
}

/// <summary>A single host → worker request line.</summary>
internal sealed class WorkerRequest
{
    /// <summary>One of <see cref="SemanticWorkerProtocol.Ops"/>.</summary>
    public string Op { get; set; } = "";

    /// <summary>Correlates a request with its terminal message and any intermediate token/progress
    /// messages. Config/setter ops that need no reply use 0.</summary>
    public int Id { get; set; }

    // ── translate ────────────────────────────────────────────────────────────────────────────────
    public string? Query { get; set; }
    public bool Streaming { get; set; }
    public string? DefaultDirectory { get; set; }
    public string? OriginalQuery { get; set; }

    /// <summary>Round-trip <see cref="System.DateTimeOffset"/> ("o") for the translation context's Now.</summary>
    public string? NowIso { get; set; }

    // ── prepareModel / setModelOverride ─────────────────────────────────────────────────────────
    public string? Alias { get; set; }

    // ── setters ─────────────────────────────────────────────────────────────────────────────────
    public bool BoolValue { get; set; }
    public bool BoolValue2 { get; set; }
    public string? StringValue { get; set; }
    public long LongValue { get; set; }
}

/// <summary>A single worker → host message line.</summary>
internal sealed class WorkerMessage
{
    /// <summary>One of <see cref="SemanticWorkerProtocol.MessageTypes"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>The request id this message belongs to (0 for <c>ready</c>).</summary>
    public int Id { get; set; }

    /// <summary>Terminal-message success flag (result/models/ack).</summary>
    public bool Ok { get; set; }

    /// <summary>Error text when <see cref="Ok"/> is false.</summary>
    public string? Error { get; set; }

    // ── token ───────────────────────────────────────────────────────────────────────────────────
    public string? Delta { get; set; }

    // ── progress ────────────────────────────────────────────────────────────────────────────────
    public string? Stage { get; set; }
    public double? Percent { get; set; }
    public string? Detail { get; set; }

    // ── result (translate) ──────────────────────────────────────────────────────────────────────
    /// <summary><see cref="SemanticTranslationResult.Success"/> for the translation itself (distinct from
    /// <see cref="Ok"/>, which is whether the WORKER handled the request without faulting).</summary>
    public bool TranslationSuccess { get; set; }

    /// <summary><see cref="SemanticSearchPlan"/> serialized via its own context, or null.</summary>
    public string? PlanJson { get; set; }

    public string? RawModelOutput { get; set; }

    // ── models (listModels) ─────────────────────────────────────────────────────────────────────
    /// <summary>A <c>List&lt;SemanticModelOption&gt;</c> serialized via <see cref="SemanticWorkerJsonContext"/>.</summary>
    public string? OptionsJson { get; set; }

    // ── piggybacked state ───────────────────────────────────────────────────────────────────────
    /// <summary>The worker's current model key, sent on terminal messages so the proxy can serve the
    /// synchronous <see cref="ISemanticQueryTranslator.CurrentModelKey"/> without a round-trip.</summary>
    public string? CurrentModelKey { get; set; }
}

/// <summary>Source-gen JSON context for the flat protocol DTOs and the model-option list (kept minimal
/// and converter-free so it is AOT-safe in the main app). The <see cref="SemanticSearchPlan"/> payload uses
/// its own <c>SemanticSearchPlanJsonContext</c> instead (it has custom converters).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(List<SemanticModelOption>))]
internal sealed partial class SemanticWorkerJsonContext : JsonSerializerContext
{
}
