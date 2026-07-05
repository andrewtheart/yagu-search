using Yagu.Services.Ai;

namespace Yagu.ViewModels;

/// <summary>
/// Slow semantic-interpretation helpers: when the on-device AI model spends a long time turning a
/// natural-language query into search settings, Yagu offers a smaller/faster model. These members back
/// that prompt — identifying the running model, listing faster alternatives, and remembering when the
/// user opts out for a specific model variant.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A stable key for the model variant currently selected/loaded for semantic translation
    /// (variant id when known, otherwise the alias). Null when no model has been chosen yet. Used to
    /// suppress the slow-interpretation warning for one exact variant.</summary>
    public string? CurrentSemanticModelKey => _semanticTranslator?.CurrentModelKey;

    /// <summary>Friendly name of the model currently selected for semantic translation, for display in
    /// the slow-interpretation prompt. Falls back to the configured override or a generic label.</summary>
    public string CurrentSemanticModelName =>
        (_semanticTranslator as FoundryLocalSemanticQueryTranslator)?.SelectedModelAlias is { Length: > 0 } loaded
            ? loaded
            : !string.IsNullOrWhiteSpace(SemanticModelAlias) ? SemanticModelAlias
            : "The AI model";

    /// <summary>True when the user has permanently dismissed the slow-interpretation warning for the
    /// given model variant key (via "Don't show this warning again for this model").</summary>
    public bool IsSlowSemanticModelWarningSuppressed(string? modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return false;
        foreach (var suppressed in _settings.SuppressedSlowSemanticModelKeys)
        {
            if (string.Equals(suppressed, modelKey, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Permanently suppresses the slow-interpretation warning for the given model variant key
    /// and persists the choice. No-op for a null/empty key or one already suppressed.</summary>
    public async Task SuppressSlowSemanticModelWarningAsync(string? modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return;
        if (IsSlowSemanticModelWarningSuppressed(modelKey)) return;
        _settings.SuppressedSlowSemanticModelKeys.Add(modelKey.Trim());
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Lists the locally-runnable models that are smaller (and therefore typically faster) than
    /// the model currently running, smallest-first. Empty when none exist or the feature is unavailable —
    /// the caller then skips the slow-interpretation prompt.</summary>
    public async Task<IReadOnlyList<SemanticModelOption>> GetFasterSemanticModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return Array.Empty<SemanticModelOption>();

        var options = await _semanticTranslator.ListModelOptionsAsync(progress, cancellationToken).ConfigureAwait(true);
        return SlowSemanticModelAdvisor.SelectFasterOptions(
            options, _semanticTranslator.CurrentModelKey, SemanticModelAlias);
    }
}
