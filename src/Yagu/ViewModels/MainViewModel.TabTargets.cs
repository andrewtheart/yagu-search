namespace Yagu.ViewModels;

/// <summary>Which search-input box a Tab-destination preference belongs to.</summary>
public enum SearchInputTabScope
{
    /// <summary>The directory box and the controls overlaid at its trailing edge.</summary>
    Directory,

    /// <summary>The search-pattern box and the toggles overlaid at its trailing edge.</summary>
    SearchPattern,
}

/// <summary>
/// Remembered Tab destinations for the directory and search-pattern boxes. Each box asks once, via a
/// non-blocking callout, whether Tab should move to the first control overlaid inside the box or skip
/// past them to the next major control; the answer is persisted and reused from then on.
/// </summary>
public sealed partial class MainViewModel
{
    public bool HasPromptedTabTarget(SearchInputTabScope scope) => scope switch
    {
        SearchInputTabScope.Directory => _settings.HasPromptedDirectoryTabTarget,
        _ => _settings.HasPromptedSearchPatternTabTarget,
    };

    public bool TabSkipsInlineControls(SearchInputTabScope scope) => scope switch
    {
        SearchInputTabScope.Directory => _settings.DirectoryTabSkipsInlineControls,
        _ => _settings.SearchPatternTabSkipsInlineControls,
    };

    /// <summary>Records the user's answer to the one-time prompt and persists it immediately, writing only
    /// the already-saved settings snapshot so live, unapplied Settings-window edits are not swept in.</summary>
    public async Task RecordTabTargetChoiceAsync(SearchInputTabScope scope, bool skipInlineControls)
    {
        if (scope == SearchInputTabScope.Directory)
        {
            _settings.HasPromptedDirectoryTabTarget = true;
            _settings.DirectoryTabSkipsInlineControls = skipInlineControls;
        }
        else
        {
            _settings.HasPromptedSearchPatternTabTarget = true;
            _settings.SearchPatternTabSkipsInlineControls = skipInlineControls;
        }

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }

    /// <summary>True when neither box has been asked yet (drives the Settings reset button's enabled state).</summary>
    public bool AreTabTargetPromptsReset
        => !_settings.HasPromptedDirectoryTabTarget && !_settings.HasPromptedSearchPatternTabTarget;

    /// <summary>Re-arms both prompts and restores the default (move to the inline controls) destination.</summary>
    public async Task ResetTabTargetPromptsAsync()
    {
        if (await PersistPromptResetAsync(settings =>
        {
            settings.HasPromptedDirectoryTabTarget = false;
            settings.DirectoryTabSkipsInlineControls = false;
            settings.HasPromptedSearchPatternTabTarget = false;
            settings.SearchPatternTabSkipsInlineControls = false;
        }).ConfigureAwait(true))
            OnPropertyChanged(nameof(AreTabTargetPromptsReset));
    }
}
