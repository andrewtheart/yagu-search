using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Advanced Options session behavior: tracking transient changes made for one search and resetting
/// back to the saved defaults.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Set while a search's Advanced Options were transiently changed (e.g. the excluded-extension
    /// "Include &amp; search" flow), so they are reset to the saved defaults once the search finishes.</summary>
    private bool _advancedOptionsTransientlyChanged;

    partial void OnIsSearchingChanged(bool value)
    {
        if (value)
        {
            CancelIndexStorageMeasurement();
            return;                                      // remaining work applies only when a search ENDS
        }
        SearchInNameFirstPhase = false;
        if (!IsTranslatingSemanticQuery) IsCancelling = false;   // cancel drained — restore the button
        if (!_advancedOptionsTransientlyChanged) return;
        _advancedOptionsTransientlyChanged = false;
        // A semantic search intentionally leaves its resolved plan visible in Advanced Options and reverts
        // it at the start of the next search; don't fight that here.
        if (_semanticResolutionVisible) return;
        ResetAdvancedOptionsToSavedDefaults();
    }

    /// <summary>
    /// Resets every Advanced Options control back to the user's saved settings. Invoked by the Advanced
    /// Options "Reset" button and automatically after a search that transiently changed the options, so a
    /// one-off "Include &amp; search" adjustment never lingers into the next search.
    /// </summary>
    public void ResetAdvancedOptionsToSavedDefaults()
    {
        AppSettings settings = _settingsService.Load();

        SearchModeIndex = 0;
        IncludeFilterModeIndex = settings.IncludeFilterModeIndex;
        ExcludeFilterModeIndex = settings.ExcludeFilterModeIndex;
        IncludeGlobs = settings.IncludeGlobs;
        // Mirror the constructor: when the exclude globs are the built-in default, leave the box EMPTY
        // so it shows the greyed "e.g. …" placeholder instead of the literal default as real text (which
        // would look — and behave — like a user-entered filter).
        ExcludeGlobs = IsDefaultExcludeGlobs(settings.ExcludeGlobs) ? string.Empty : settings.ExcludeGlobs;
        ObeyGitignore = settings.ObeyGitignore;

        SettingsSkipExtensions = settings.SkipExtensions;
        SkipExtensions = settings.SkipExtensions;
        SearchBinary = !settings.SkipBinary;
        SettingsBinaryExtensions = settings.BinaryExtensions;
        BinaryExtensions = settings.BinaryExtensions;
        SearchInsideArchives = settings.SearchInsideArchives;
        SettingsArchiveExtensions = settings.ArchiveExtensions;
        ArchiveExtensions = settings.ArchiveExtensions;

        DefaultMinFileSizeBytes = settings.DefaultMinFileSizeBytes;
        DefaultMaxFileSizeBytes = settings.DefaultMaxFileSizeBytes;
        MinFileSizeBytes = settings.DefaultMinFileSizeBytes;
        MaxFileSizeBytes = settings.DefaultMaxFileSizeBytes;
        DefaultCreatedAfterDate = settings.DefaultCreatedAfterDate;
        DefaultCreatedBeforeDate = settings.DefaultCreatedBeforeDate;
        DefaultModifiedAfterDate = settings.DefaultModifiedAfterDate;
        DefaultModifiedBeforeDate = settings.DefaultModifiedBeforeDate;
        CreatedAfterDate = settings.DefaultCreatedAfterDate;
        CreatedBeforeDate = settings.DefaultCreatedBeforeDate;
        ModifiedAfterDate = settings.DefaultModifiedAfterDate;
        ModifiedBeforeDate = settings.DefaultModifiedBeforeDate;
        MaxSearchDepth = double.NaN;

        SyncSkipExtensionItems();
        SyncBinaryExtensionItems();
        SyncArchiveExtensionItems();
    }
}
