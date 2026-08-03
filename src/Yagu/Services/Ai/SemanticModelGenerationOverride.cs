namespace Yagu.Services.Ai;

/// <summary>
/// Per-model override of the six Foundry Local text-generation (sampling) parameters that
/// <c>FoundryLocalSemanticQueryTranslator.ConfigureChatSettings</c> otherwise sets from built-in
/// defaults. Every field is nullable: a <c>null</c> field means "use the built-in default for this
/// model class (reasoning vs. instruct)"; a non-null field replaces that default when the selected
/// model matches the override's key (its catalog variant id first, then its alias — case-insensitive).
///
/// Persisted in <c>settings.json</c> under <c>SemanticModelParameterOverrides</c> (a map keyed by model
/// alias or variant id) and replayed to the out-of-process semantic worker over the JSON protocol, so a
/// power user can tune a specific model variant without changing any other model's behavior.
/// </summary>
/// <remarks>
/// This type is deliberately a small, flat, converter-free POCO so it is safe for BOTH source-generated
/// JSON contexts that serialize it — the AOT <c>AppSettingsJsonContext</c> (settings file) and the
/// <c>SemanticWorkerJsonContext</c> (worker wire protocol) — and so it can be SOURCE-LINKED into the
/// non-AOT <c>Yagu.SemanticWorker</c> host exactly like the translator it configures.
/// </remarks>
public sealed class SemanticModelGenerationOverride
{
    /// <summary>Sampling temperature (0 = greedy/deterministic). Null keeps the model-class default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Nucleus-sampling top-p (1 = disabled). Null keeps the model-class default.</summary>
    public float? TopP { get; set; }

    /// <summary>Hard cap on generated tokens for the reply. Null keeps the model-class default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Sampling seed (pins reproducible output run-to-run). Null keeps the model-class default.</summary>
    public int? RandomSeed { get; set; }

    /// <summary>Frequency penalty. Null keeps the model-class default.</summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>Presence penalty. Null keeps the model-class default.</summary>
    public float? PresencePenalty { get; set; }

    /// <summary>True when at least one field is set (an all-null override is meaningless and dropped).</summary>
    public bool HasAny =>
        Temperature is not null || TopP is not null || MaxTokens is not null ||
        RandomSeed is not null || FrequencyPenalty is not null || PresencePenalty is not null;
}
