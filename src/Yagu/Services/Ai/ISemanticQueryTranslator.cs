using Yagu.Models;

namespace Yagu.Services.Ai;

/// <summary>
/// Translates a natural-language search request (e.g. "find png files on C: modified in the
/// last year, ignore mov files") into a structured <see cref="SemanticSearchPlan"/> using a
/// locally-run language model. Implementations must never send the query off the machine.
/// </summary>
public interface ISemanticQueryTranslator
{
    /// <summary>
    /// Whether semantic translation is currently usable (feature enabled and the runtime is
    /// supported). This does not guarantee a model is already downloaded — the first
    /// <see cref="TranslateAsync"/> call may trigger a download.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Translates <paramref name="naturalLanguageQuery"/> into a <see cref="SemanticSearchPlan"/>.
    /// The first call may download execution providers and the model (reported via
    /// <paramref name="progress"/>). Honors <paramref name="cancellationToken"/> throughout.
    /// </summary>
    Task<SemanticTranslationResult> TranslateAsync(
        string naturalLanguageQuery,
        SemanticTranslationContext context,
        IProgress<SemanticTranslationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Translates like <see cref="TranslateAsync"/>, but streams the model's raw output to
    /// <paramref name="onToken"/> one delta at a time as it is generated. This is a SEPARATE inference
    /// path used only by the first-run model-qualification dialog to drive its live "chat" transcript;
    /// the normal search path uses <see cref="TranslateAsync"/> and is unaffected. When
    /// <paramref name="onToken"/> is null this behaves like a single-shot translation. Honors
    /// <paramref name="cancellationToken"/> throughout.
    /// </summary>
    Task<SemanticTranslationResult> TranslateStreamingAsync(
        string naturalLanguageQuery,
        SemanticTranslationContext context,
        Action<string>? onToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the locally-runnable chat models for the current hardware, ranked, with the
    /// recommended pick flagged. The first call may download execution providers and query the
    /// catalog (reported via <paramref name="progress"/>). Returns an empty list when the feature
    /// is disabled or no compatible model exists.
    /// </summary>
    Task<IReadOnlyList<SemanticModelOption>> ListModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the given model alias (or the recommended pick when <paramref name="modelAlias"/> is
    /// null/empty) is downloaded and selected for subsequent translations. Reports download progress
    /// via <paramref name="progress"/>. A no-op fast path when the model is already cached.
    /// </summary>
    Task PrepareModelAsync(
        string? modelAlias,
        IProgress<SemanticTranslationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Enables or disables the translator at runtime (mirrors the AI-search setting).
    /// Disabling drops any loaded model so a later re-enable re-selects cleanly.</summary>
    void SetEnabled(bool enabled);

    /// <summary>Sets the preferred execution-device order (e.g. "NPU,GPU,CPU") used to pick which
    /// accelerator build of the model runs. Applied to the next model selection; drops the currently
    /// loaded model so the change takes effect without an app restart.</summary>
    void SetDevicePreferenceOrder(string? order);

    /// <summary>Sets the model alias override (null/empty = automatic recommended pick). Applied to the
    /// next selection; drops the loaded model so the change takes effect without an app restart.</summary>
    void SetModelOverride(string? modelAlias);

    /// <summary>Tells the translator which hardware accelerators this machine actually has (per Yagu's
    /// capability detection). Builds for an absent accelerator are never selected, preventing a GPU/NPU
    /// model from loading (via DirectML) and then crashing during inference on a CPU-only machine.
    /// Applied to the next selection; drops the loaded model so the change takes effect immediately.</summary>
    void SetAvailableAccelerators(bool hasGpu, bool hasNpu);

    /// <summary>Tells the translator how much dedicated GPU memory (bytes) this machine has, so AUTO
    /// selection can upgrade to a larger, more-accurate model (e.g. phi-4 14B) when there is ample VRAM
    /// instead of always picking the small default. 0 = unknown / no GPU. Applied to the next selection;
    /// drops the loaded model so the change takes effect without a restart.</summary>
    void SetGpuMemoryBytes(long dedicatedVideoMemoryBytes);

    /// <summary>When enabled, the loaded model is unloaded from memory (freeing GPU VRAM) immediately
    /// after each translation finishes, and reloaded on the next translation. When disabled (default) the
    /// model stays resident for the fastest repeat queries. Applied live; does not itself drop or reload
    /// the currently loaded model — the unload happens after the next translation completes.</summary>
    void SetUnloadAfterUse(bool unloadAfterUse);

    /// <summary>Unloads the currently loaded model from memory (freeing GPU VRAM / system RAM) on demand
    /// and drops the cached references so the next translation reloads it. Unlike <see cref="SetModelOverride"/>
    /// — which only drops Yagu's references but leaves the model resident in Foundry Local — this actively
    /// evicts it via the SDK. Best-effort and never throws. Used by the first-run qualification sweep to
    /// keep only ONE candidate model resident at a time, so probing a ladder of models cannot pile them
    /// up in memory and OOM ("bad allocation").</summary>
    Task UnloadCurrentModelAsync(CancellationToken cancellationToken);

    /// <summary>Clears the cached Foundry model catalog and drops the loaded model so the next selection
    /// re-queries Foundry Local. Use to pick up models downloaded/updated out of band and to re-resolve
    /// the current model. Cheap and synchronous; the actual re-query happens on next use.</summary>
    void RefreshCatalog();

    /// <summary>A stable key identifying the model variant currently selected/loaded for translation —
    /// the catalog variant id when known (alias + accelerator build + quantization + version), otherwise
    /// the alias. Null when no model has been selected yet. Used to suppress the slow-interpretation
    /// warning per exact variant.</summary>
    string? CurrentModelKey { get; }
}

/// <summary>A locally-runnable model the user can choose for semantic translation.</summary>
public sealed class SemanticModelOption
{
    /// <summary>Catalog alias used to select/download the model.</summary>
    public required string Alias { get; init; }

    /// <summary>Unique catalog variant id (alias + accelerator build + quantization + version). Stable
    /// per variant and used to detect newly-available/updated models. Null when the catalog does not
    /// expose one.</summary>
    public string? Id { get; init; }

    /// <summary>Friendly name shown to the user (usually the alias).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Approximate on-disk/download size in bytes, when known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>The auto-selected best pick for this machine.</summary>
    public bool IsRecommended { get; init; }

    /// <summary>True when this model ranks below the recommended pick and may give less accurate
    /// results — the UI flags these with a warning.</summary>
    public bool IsBelowRecommended { get; init; }

    /// <summary>True when this model's family appears in the static preference list (a known-good family
    /// with a fixed rank); false for a "novel" family the preference list has never seen — e.g. a newly
    /// released model. The first-run qualification sweep reserves a probe slot for a novel family so a
    /// brand-new (potentially superior) model is never permanently pinned below the candidate cap by the
    /// static ranking.</summary>
    public bool IsPreferredFamily { get; init; }

    /// <summary>True when the model is already downloaded on this machine.</summary>
    public bool IsCached { get; init; }

    /// <summary>Optional hardware label (e.g. "GPU", "NPU", "CPU").</summary>
    public string? DeviceLabel { get; init; }

    /// <summary>True when the model's estimated memory footprint exceeds this machine's available RAM
    /// (computed for CPU-only machines; always false on accelerated hardware, where the model runs in
    /// device memory). The picker warns that such a model will fail to load, but still allows selecting
    /// it as a deliberate override.</summary>
    public bool ExceedsAvailableMemory { get; init; }
}

/// <summary>Ambient information the model needs to resolve relative dates and defaults.</summary>
public sealed class SemanticTranslationContext
{
    /// <summary>"Now" used to resolve relative ranges like "in the past year". Defaults to
    /// <see cref="DateTimeOffset.Now"/>.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>Directory the user currently has selected, used as a fallback when the request
    /// does not name a location. May be null/empty.</summary>
    public string? DefaultDirectory { get; init; }

    /// <summary>The user's raw natural-language query, before translation. Used to deterministically
    /// recover information the model loses when it emits the plan — chiefly programming-language
    /// names whose glyph can't appear in a glob (e.g. "c#"/"c++"/"f#"), which small models collapse
    /// to the wrong extension. May be null/empty when a caller does not supply it.</summary>
    public string? OriginalQuery { get; init; }

    /// <summary>Optional probe used to reject a hallucinated directory. A small model can echo a
    /// nonsense query into the plan's <c>directory</c> field; applying that would overwrite the
    /// search location with a path that does not exist and guarantee a "Directory does not exist"
    /// failure. When set, a model-supplied directory that does not satisfy this probe is dropped
    /// (falling back to <see cref="DefaultDirectory"/>) and a warning is recorded. Null disables the
    /// check, keeping resolution a pure function of its inputs for unit tests.</summary>
    public Func<string, bool>? DirectoryExists { get; init; }
}

/// <summary>Outcome of a translation attempt.</summary>
public sealed class SemanticTranslationResult
{
    public bool Success { get; init; }

    /// <summary>The parsed plan when <see cref="Success"/> is true; otherwise null.</summary>
    public SemanticSearchPlan? Plan { get; init; }

    /// <summary>User-facing error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>Raw model output, retained for diagnostics/logging.</summary>
    public string? RawModelOutput { get; init; }

    public static SemanticTranslationResult Ok(SemanticSearchPlan plan, string? raw = null) =>
        new() { Success = true, Plan = plan, RawModelOutput = raw };

    public static SemanticTranslationResult Fail(string error, string? raw = null) =>
        new() { Success = false, Error = error, RawModelOutput = raw };
}

/// <summary>Coarse phases reported while a translation runs.</summary>
public enum SemanticTranslationStage
{
    Initializing,
    DownloadingExecutionProviders,
    DownloadingModel,
    LoadingModel,
    Interpreting,
}

/// <summary>Progress update for long-running translation steps (first-run downloads, etc.).</summary>
public sealed class SemanticTranslationProgress
{
    public required SemanticTranslationStage Stage { get; init; }

    /// <summary>0–100 when a percentage is known; null for indeterminate steps.</summary>
    public double? Percent { get; init; }

    /// <summary>Optional detail, e.g. the execution-provider or model name being downloaded.</summary>
    public string? Detail { get; init; }

    /// <summary>Human-readable one-line status suitable for direct display.</summary>
    public string Message => Stage switch
    {
        SemanticTranslationStage.Initializing => "Preparing the local AI model…",
        SemanticTranslationStage.DownloadingExecutionProviders =>
            Percent is { } p ? $"Downloading AI runtime ({Detail}) — {p:F0}%" : "Downloading AI runtime…",
        SemanticTranslationStage.DownloadingModel =>
            Percent is { } p ? $"Downloading model — {p:F0}% (one-time per model)" : "Downloading model… (one-time per model)",
        SemanticTranslationStage.LoadingModel => "Loading the model…",
        SemanticTranslationStage.Interpreting => "Interpreting your request…",
        _ => "Working…",
    };
}
