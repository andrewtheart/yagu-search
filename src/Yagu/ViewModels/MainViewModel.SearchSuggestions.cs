using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Contextual search suggestions and warnings offered around a search: the CPU-only semantic
/// warning, the "this looks like a natural-language query" prompt, and the multiline-newline hint.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// True when the first-run "AI search will run on the CPU" warning should be shown: AI (Semantic)
    /// search is available, no GPU/NPU was detected (so the suggested model would fall back to CPU), and
    /// the warning has not been shown before. Shown at most once.
    /// </summary>
    public bool ShouldShowCpuSemanticWarning =>
        SemanticSearchAvailable && !SemanticHardwareAccelerated && !_settings.CpuSemanticWarningShown;

    /// <summary>
    /// Dismisses the first-run CPU-mode AI-search warning, recording that it has been shown so it never
    /// reappears. When <paramref name="useTraditionalDefault"/> is true (the user accepted the
    /// recommendation), Traditional becomes the persisted default search mode and the search bar switches
    /// to Traditional immediately. When false (the user chose to keep AI search anyway), Semantic becomes
    /// the selected mode and the persisted default, both in the search bar and in settings.
    /// </summary>
    public async Task DismissCpuSemanticWarningAsync(bool useTraditionalDefault)
    {
        _settings.CpuSemanticWarningShown = true;
        if (useTraditionalDefault)
        {
            // CPU-only machine + the user chose Traditional: turn AI (Semantic) search OFF entirely so the
            // "Enable AI (semantic) search" setting reflects their choice — not just the default mode.
            // OnSemanticSearchAvailableChanged persists SemanticSearchEnabled=false, disables the translator,
            // and forces Semantic mode off. (No-op on a GPU/NPU machine, which never sees this prompt.)
            SemanticSearchAvailable = false;
            DefaultToTraditionalSearchMode = true; // OnChanged persists + re-resolves launch mode when unpinned
            IsSemanticQueryMode = false;           // immediate switch to Traditional (idempotent if already off)
        }
        else
        {
            // User explicitly opted into AI (Semantic) search despite the CPU warning. Keep the feature
            // enabled, select it now and make it the persisted default. Setting IsSemanticQueryMode first
            // records the explicit choice (HasChosenQueryMode = true) so flipping the default below does
            // not re-resolve it away.
            SemanticSearchAvailable = true;        // ensure the AI-search feature stays enabled
            IsSemanticQueryMode = true;            // immediate switch to Semantic + persists the explicit choice
            DefaultToTraditionalSearchMode = false; // persisted default = AI/Semantic, reflected in settings
        }
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// True when an interactive Traditional-mode submit should first offer to switch to AI (Semantic)
    /// search because <paramref name="query"/> reads like a natural-language request. Gated on a
    /// downloaded model (so the switch is one click away), the user not having ticked "Don't remind me
    /// again", and the conservative heuristic. The AI-search toggle does NOT need to be on — if the user
    /// has it disabled, accepting the prompt turns it on. (<see cref="IsTraditionalQueryMode"/> is true
    /// whenever AI search is off, since Semantic mode is forced off in that state.)
    /// </summary>
    public bool ShouldOfferSemanticSuggestion(string? query) =>
        IsTraditionalQueryMode
        && IsSemanticModelDownloaded
        && !_settings.SemanticSuggestionDismissed
        && Yagu.Helpers.SemanticQueryHeuristicDetector.LooksLikeSemanticQuery(query);

    /// <summary>
    /// Records the outcome of the "this looks like an AI search" suggestion. When
    /// <paramref name="switchToSemantic"/> is true the search bar switches to Semantic mode for this run
    /// (enabling AI search first if the user had it turned off); when <paramref name="dontRemind"/> is
    /// true the suggestion is suppressed permanently. Either way the settings are persisted so the choice
    /// survives a restart.
    /// </summary>
    public async Task ApplySemanticSuggestionAsync(bool switchToSemantic, bool dontRemind)
    {
        if (dontRemind)
            _settings.SemanticSuggestionDismissed = true;
        if (switchToSemantic)
        {
            // The user opted into AI search. If it was turned off, enable it now (this flips the
            // translator on live and persists), then switch the search bar to Semantic for this run.
            if (!SemanticSearchAvailable)
                SemanticSearchAvailable = true;
            IsSemanticQueryMode = true;
        }
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// True when an interactive Traditional-mode submit should first offer to switch on Multiline search
    /// because <paramref name="query"/> contains a literal "\n" escape (the two characters backslash-n),
    /// which only matches a real line break once Multiline — and therefore Regex — is on. Gated on
    /// Multiline being off, the user not having ticked "Don't warn me again", and the query actually
    /// containing the escape. A no-op in Semantic mode, where the query is natural language.
    /// </summary>
    public bool ShouldOfferMultilineSuggestion(string? query) =>
        IsTraditionalQueryMode
        && !Multiline
        && !_settings.MultilineNewlineSuggestionDismissed
        && !string.IsNullOrEmpty(query)
        && query.Contains("\\n", StringComparison.Ordinal)
        && !Yagu.Helpers.SingleFilePathQueryDetector.LooksLikePath(query);

    /// <summary>
    /// Records the outcome of the "this looks like a multiline search" suggestion. When
    /// <paramref name="switchToMultiline"/> is true, Multiline is enabled for this run — which also turns
    /// on Regex and turns off Exact match via <see cref="OnMultilineChanged"/> — so the "\n" escape is
    /// interpreted as a line break; when <paramref name="dontRemind"/> is true the prompt is suppressed
    /// permanently. The settings are persisted so the choice survives a restart.
    /// </summary>
    public async Task ApplyMultilineSuggestionAsync(bool switchToMultiline, bool dontRemind)
    {
        if (dontRemind)
            _settings.MultilineNewlineSuggestionDismissed = true;
        if (switchToMultiline)
            Multiline = true;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Whether the literal-"\n" multiline prompt has been dismissed via "Don't warn me again".
    /// Exposed so the Developer Options reset button can reflect the current state.</summary>
    public bool MultilineNewlineSuggestionDismissed => _settings.MultilineNewlineSuggestionDismissed;

    /// <summary>True when the user opted out of the warning shown before a search pauses an active
    /// content-index warm-up. The behavior still pauses warming; only the warning is suppressed.</summary>
    public bool SuppressIndexWarmSearchWarning
    {
        get => _settings.SuppressIndexWarmSearchWarning;
        set
        {
            if (_settings.SuppressIndexWarmSearchWarning == value)
                return;
            _settings.SuppressIndexWarmSearchWarning = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Re-enables the index warm-up search warning from Developer Options.</summary>
    public async Task ResetIndexWarmSearchWarningAsync()
    {
        SuppressIndexWarmSearchWarning = false;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Restores pre-search warnings for unindexed locations without saving unrelated live
    /// Settings-window edits that have not been applied yet.</summary>
    public async Task RestoreContentIndexLiveScanWarningsAsync()
    {
        _settings.ContentIndexLiveScanWarningDismissedRoots.Clear();
        AppSettings persisted = await _settingsService.LoadAsync().ConfigureAwait(true);
        persisted.ContentIndexLiveScanWarningDismissedRoots.Clear();
        await _settingsService.SaveAsync(persisted).ConfigureAwait(true);
    }

    /// <summary>Re-enables the literal-"\n" multiline suggestion prompt after the user dismissed it
    /// (Developer Options → Reminders and Warnings reset). Persists so the reset survives a restart.</summary>
    public async Task ResetMultilineNewlineSuggestionAsync()
    {
        _settings.MultilineNewlineSuggestionDismissed = false;
        await PersistSettingsAsync().ConfigureAwait(true);
    }
}
