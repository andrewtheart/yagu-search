using Yagu.Services;
using System.Globalization;

namespace Yagu.ViewModels;

/// <summary>
/// Advanced Options session behavior: tracking transient changes made for one search, resetting
/// back to the saved defaults, saving the current options as the new defaults, and describing what
/// those defaults are.
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

    /// <summary>
    /// Persists the Advanced Options exactly as they are shown right now as the saved defaults, writing
    /// them straight to the settings file. The inverse of <see cref="ResetAdvancedOptionsToSavedDefaults"/>:
    /// afterward, "Reset" and a fresh launch restore these values. Any transient ("Include &amp; search")
    /// or semantic-resolution markers are cleared, because the visible values ARE the defaults now.
    /// </summary>
    public async Task SaveAdvancedOptionsAsDefaultsAsync()
    {
        // The visible Advanced Options are becoming the real defaults, so drop the transient/semantic
        // guards that would otherwise make PersistSettingsAsync write a snapshot, or let a later Reset
        // undo the change.
        _semanticResolutionVisible = false;
        _semanticDefaultsSnapshot = null;
        _advancedOptionsTransientlyChanged = false;

        // Promote the active filter values into the persisted-default mirrors that Reset and a fresh
        // launch read from, so the saved default equals exactly what is shown now.
        SettingsSkipExtensions = SkipExtensions;
        // BinaryExtensions is the SKIP list and is EMPTY when "Search binary" is on (all types searched), so
        // it must never overwrite the universe of known binary types the dropdown is built from -- that would
        // drop every searched type. Preserve the full known set instead (active list is a subset of it).
        SettingsBinaryExtensions = string.Join(';', ParseExtensionSet(SettingsBinaryExtensions)
            .Union(ParseExtensionSet(BinaryExtensions))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
        SettingsArchiveExtensions = ArchiveExtensions;
        DefaultMinFileSizeBytes = MinFileSizeBytes;
        DefaultMaxFileSizeBytes = MaxFileSizeBytes;
        DefaultCreatedAfterDate = CreatedAfterDate;
        DefaultCreatedBeforeDate = CreatedBeforeDate;
        DefaultModifiedAfterDate = ModifiedAfterDate;
        DefaultModifiedBeforeDate = ModifiedBeforeDate;

        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Human-readable summary of the Advanced Options that <see cref="SaveAdvancedOptionsAsDefaultsAsync"/>
    /// would persist, shown in the confirmation dialog. Each entry is one "Label: value" line.
    /// </summary>
    internal IReadOnlyList<string> DescribeAdvancedOptionDefaults()
    {
        static string OnOff(bool value) => value ? "On" : "Off";

        var lines = new List<string>
        {
            $"Match case: {OnOff(CaseSensitive)}",
            $"Regular expression: {OnOff(UseRegex)}",
            $"Exact match: {OnOff(ExactMatch)}",
            $"Respect .gitignore: {OnOff(ObeyGitignore)}",
            $"Search hidden files: {OnOff(SearchHiddenFiles)}",
            $"Search binary files: {OnOff(SearchBinary)}",
            $"Search inside archives: {OnOff(SearchInsideArchives)}",
            $"Search image text (OCR): {(SearchImageText ? $"On ({AppSettings.NormalizeImageOcrEngine(ImageOcrEngine)})" : "Off")}",
            $"Search PDF text: {OnOff(SearchPdfText)}",
        };

        string include = (IncludeGlobs ?? string.Empty).Trim();
        lines.Add($"Include filter: {(include.Length == 0 ? "(none)" : include)}");
        string exclude = EffectiveExcludeGlobsText.Trim();
        lines.Add($"Exclude filter: {(exclude.Length == 0 ? "(none)" : exclude)}");

        string size = DescribeSizeRange(MinFileSizeBytes, MaxFileSizeBytes);
        if (size.Length > 0) lines.Add($"File size: {size}");

        string created = DescribeDateRange(CreatedAfterDate, CreatedBeforeDate);
        if (created.Length > 0) lines.Add($"Created date: {created}");
        string modified = DescribeDateRange(ModifiedAfterDate, ModifiedBeforeDate);
        if (modified.Length > 0) lines.Add($"Modified date: {modified}");

        return lines;
    }

    private static string DescribeSizeRange(long minBytes, long maxBytes)
    {
        bool hasMin = minBytes > 0;
        bool hasMax = maxBytes > 0;
        if (hasMin && hasMax) return $"between {FormatBytes(minBytes)} and {FormatBytes(maxBytes)}";
        if (hasMin) return $"at least {FormatBytes(minBytes)}";
        if (hasMax) return $"at most {FormatBytes(maxBytes)}";
        return string.Empty;
    }

    private static string DescribeDateRange(DateTimeOffset? after, DateTimeOffset? before)
    {
        static string D(DateTimeOffset d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (after.HasValue && before.HasValue) return $"between {D(after.Value)} and {D(before.Value)}";
        if (after.HasValue) return $"after {D(after.Value)}";
        if (before.HasValue) return $"before {D(before.Value)}";
        return string.Empty;
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / (double)gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:0.##} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:0.##} KB";
        return $"{bytes} bytes";
    }
}
