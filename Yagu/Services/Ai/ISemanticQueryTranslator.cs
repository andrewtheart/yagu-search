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
}

/// <summary>A locally-runnable model the user can choose for semantic translation.</summary>
public sealed class SemanticModelOption
{
    /// <summary>Catalog alias used to select/download the model.</summary>
    public required string Alias { get; init; }

    /// <summary>Friendly name shown to the user (usually the alias).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Approximate on-disk/download size in bytes, when known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>The auto-selected best pick for this machine.</summary>
    public bool IsRecommended { get; init; }

    /// <summary>True when this model ranks below the recommended pick and may give less accurate
    /// results — the UI flags these with a warning.</summary>
    public bool IsBelowRecommended { get; init; }

    /// <summary>True when the model is already downloaded on this machine.</summary>
    public bool IsCached { get; init; }

    /// <summary>Optional hardware label (e.g. "GPU", "NPU", "CPU").</summary>
    public string? DeviceLabel { get; init; }
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
            Percent is { } p ? $"Downloading model — {p:F0}%" : "Downloading model…",
        SemanticTranslationStage.LoadingModel => "Loading the model…",
        SemanticTranslationStage.Interpreting => "Interpreting your request…",
        _ => "Working…",
    };
}
