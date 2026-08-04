namespace Yagu.ViewModels;

/// <summary>
/// Semantic-search default protection: captures the user's saved search-filter defaults before a
/// semantic plan is applied and restores them afterwards, so an AI-resolved plan applies to that
/// one run only and never overwrites the persisted defaults.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Immutable snapshot of the user's current search-filter inputs. Captured before a semantic
    /// plan is applied so the same values can be restored afterward — a semantic search must never
    /// change the saved filter defaults shown in Settings/Advanced Options, and any input it does NOT
    /// set must reset to the user's default on the next search. NOTE: <c>Directory</c> is intentionally
    /// NOT captured/restored: when the model resolves a directory it should OVERRIDE and replace whatever
    /// was manually in the directory box (and persist), and when it resolves none the box value is left
    /// untouched anyway. <c>SearchModeIndex</c> IS captured (it is session-only, not persisted) so the
    /// Search-mode dropdown resets to the user's default — e.g. "File names + content" — each search
    /// rather than keeping a previous plan's mode.
    /// </summary>
    private sealed record SemanticSearchInputSnapshot(
        string IncludeGlobs,
        string ExcludeGlobs,
        int IncludeFilterModeIndex,
        int ExcludeFilterModeIndex,
        bool CaseSensitive,
        bool UseRegex,
        bool ExactMatch,
        bool ObeyGitignore,
        long MinFileSizeBytes,
        long MaxFileSizeBytes,
        DateTimeOffset? CreatedAfterDate,
        DateTimeOffset? CreatedBeforeDate,
        DateTimeOffset? ModifiedAfterDate,
        DateTimeOffset? ModifiedBeforeDate,
        bool SearchInsideArchives,
        string ArchiveExtensions,
        bool SkipBinary,
        string BinaryExtensions,
        string SkipExtensions,
        string SettingsSkipExtensions,
        string SettingsBinaryExtensions,
        string SettingsArchiveExtensions,
        bool SearchImageText,
        bool SearchPdfText,
        bool SearchHiddenFiles,
        int SearchModeIndex);

    /// <summary>Captures the current user search-filter defaults so a semantic plan can be reverted.</summary>
    private SemanticSearchInputSnapshot CaptureSearchDefaults() => new(
        IncludeGlobs,
        ExcludeGlobs,
        IncludeFilterModeIndex,
        ExcludeFilterModeIndex,
        CaseSensitive,
        UseRegex,
        ExactMatch,
        ObeyGitignore,
        MinFileSizeBytes,
        MaxFileSizeBytes,
        CreatedAfterDate,
        CreatedBeforeDate,
        ModifiedAfterDate,
        ModifiedBeforeDate,
        SearchInsideArchives,
        ArchiveExtensions,
        SkipBinary,
        BinaryExtensions,
        SkipExtensions,
        SettingsSkipExtensions,
        SettingsBinaryExtensions,
        SettingsArchiveExtensions,
        SearchImageText,
        SearchPdfText,
        SearchHiddenFiles,
        SearchModeIndex);

    /// <summary>Restores search-filter defaults captured by <see cref="CaptureSearchDefaults"/>,
    /// reverting any changes a semantic plan made so they apply only to the run that just consumed them.
    /// Directory is deliberately excluded — a resolved directory overrides the box and persists.</summary>
    private void RestoreSearchDefaults(SemanticSearchInputSnapshot s)
    {
        IncludeGlobs = s.IncludeGlobs;
        ExcludeGlobs = s.ExcludeGlobs;
        IncludeFilterModeIndex = s.IncludeFilterModeIndex;
        ExcludeFilterModeIndex = s.ExcludeFilterModeIndex;
        CaseSensitive = s.CaseSensitive;
        UseRegex = s.UseRegex;
        ExactMatch = s.ExactMatch;
        ObeyGitignore = s.ObeyGitignore;
        MinFileSizeBytes = s.MinFileSizeBytes;
        MaxFileSizeBytes = s.MaxFileSizeBytes;
        CreatedAfterDate = s.CreatedAfterDate;
        CreatedBeforeDate = s.CreatedBeforeDate;
        ModifiedAfterDate = s.ModifiedAfterDate;
        ModifiedBeforeDate = s.ModifiedBeforeDate;
        SearchInsideArchives = s.SearchInsideArchives;
        if (!string.Equals(ArchiveExtensions, s.ArchiveExtensions, StringComparison.Ordinal))
        {
            ArchiveExtensions = s.ArchiveExtensions;
            SyncArchiveExtensionItems();
        }
        SkipBinary = s.SkipBinary;
        if (!string.Equals(BinaryExtensions, s.BinaryExtensions, StringComparison.Ordinal))
        {
            BinaryExtensions = s.BinaryExtensions;
            SyncBinaryExtensionItems();
        }
        if (!string.Equals(SkipExtensions, s.SkipExtensions, StringComparison.Ordinal))
        {
            SkipExtensions = s.SkipExtensions;
            SyncSkipExtensionItems();
        }
        // The persisted "default" mirrors (Settings* lists) and the OCR toggle are part of the saved
        // filter surface too: a transient "Include & search" or a future resolution path must never
        // leave them changed once the run that consumed them is done.
        SettingsSkipExtensions = s.SettingsSkipExtensions;
        SettingsBinaryExtensions = s.SettingsBinaryExtensions;
        SettingsArchiveExtensions = s.SettingsArchiveExtensions;
        SearchImageText = s.SearchImageText;
        SearchPdfText = s.SearchPdfText;
        SearchHiddenFiles = s.SearchHiddenFiles;
        SearchModeIndex = s.SearchModeIndex;
    }

    /// <summary>
    /// Clears a completed semantic search's resolved settings from Advanced Options, resetting the
    /// filter view-model back to the saved defaults captured before that search. Called at the start of
    /// every new search so a previous resolution never leaks into the next run; a fresh semantic search
    /// then re-applies its own. No-op when nothing semantic is currently shown.
    /// </summary>
    private void ResetVisibleSemanticResolution()
    {
        if (!_semanticResolutionVisible)
            return;
        if (_semanticDefaultsSnapshot is { } snapshot)
            RestoreSearchDefaults(snapshot);
        _semanticDefaultsSnapshot = null;
        _semanticResolutionVisible = false;
    }
}
