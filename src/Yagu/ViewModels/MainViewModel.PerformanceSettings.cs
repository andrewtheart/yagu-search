using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Performance and diagnostics settings: log levels, file-lister backend, parallelism and I/O
/// oversubscription, per-search limits (matches, results, timeouts, depth), autocomplete sizing,
/// and the memory / status-bar display toggles.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int FileLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] public partial int ParallelismIndex { get; set; } = 4; // 0 = safe cap, 1 = 1 thread, 2 = half cores, 3 = 2x cores, 4 = all cores

    /// <summary>Streaming-scanner I/O worker oversubscription: 0 = Auto (SSD 1×, HDD 2×), 1 = 1×, 2 = 2×, 3 = 3×.</summary>
    [ObservableProperty] public partial int IoOversubscriptionIndex { get; set; }

    /// <summary>
    /// One-shot per-search parallelism override for HDD roots, chosen from the HDD warning dialog.
    /// When set, the next search uses <see cref="ResolveParallelism"/> of this index for HDD roots
    /// instead of forcing them to 1 thread. Consumed (cleared) when the search starts, so it applies
    /// to that single search only and is never persisted.
    /// </summary>
    private int? _hddParallelismOverrideIndexForNextSearch;

    /// <summary>
    /// Overrides the HDD parallelism limit for the next search only (consumed on search start). The
    /// index uses the same scale as <see cref="ParallelismIndex"/>. Does not change any saved setting.
    /// </summary>
    public void SetHddParallelismOverrideForNextSearch(int index) => _hddParallelismOverrideIndexForNextSearch = index;

    [ObservableProperty] public partial int LineTruncationLength { get; set; } = 500;

    // Propagate the per-match truncation length to the shared LineTruncator the moment the user
    // changes it (e.g. via Settings), not only on save/reload, so a live preview refresh picks up
    // the new window width immediately.
    partial void OnLineTruncationLengthChanged(int value) => Helpers.LineTruncator.TruncatedLength = value;

    [ObservableProperty] public partial int MaxRecentItems { get; set; } = 20;
    [ObservableProperty] public partial int MaxSemanticRecentItems { get; set; } = 20;

    // How many autocomplete suggestions are visible at once (before scrolling) in the directory and
    // search-pattern dropdowns. Distinct from the "max ... to remember" history caps. Default 5.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutocompleteDropdownMaxHeight))]
    public partial int AutocompleteDropdownVisibleItems { get; set; } = 5;

    // Approximate rendered height (px) of one autocomplete suggestion row, used to convert the visible-item
    // count into the AutoSuggestBox MaxSuggestionListHeight.
    private const double AutocompleteItemHeightPx = 40;

    /// <summary>The suggestion-list max height (px) that shows <see cref="AutocompleteDropdownVisibleItems"/>
    /// rows before scrolling. Bound to both AutoSuggestBoxes' MaxSuggestionListHeight. Row count clamped
    /// 1..50 defensively so a hand-edited setting can't collapse or balloon the dropdown.</summary>
    public double AutocompleteDropdownMaxHeight => System.Math.Clamp(AutocompleteDropdownVisibleItems, 1, 50) * AutocompleteItemHeightPx;
    [ObservableProperty] public partial bool GlobalHotkeyEnabled { get; set; }
    [ObservableProperty] public partial int MemoryLimitMB { get; set; }
    [ObservableProperty] public partial int MemoryPressurePercent { get; set; } = 75;
    [ObservableProperty] public partial int LowDiskSpaceWarningPercent { get; set; } = AppSettings.DefaultLowDiskSpaceWarningPercent;
    [ObservableProperty] public partial bool ShowMemoryPressureWarningLabel { get; set; }
    [ObservableProperty] public partial bool ShowStatsForNerds { get; set; }
    [ObservableProperty] public partial bool ShowResourceUsageInStatusBar { get; set; }
    [ObservableProperty] public partial bool ShowBuildNumberInTitleBar { get; set; }
    [ObservableProperty] public partial bool ShowAutoScrollResultsCheckbox { get; set; }
    [ObservableProperty] public partial int SdkChannelBufferSize { get; set; } = 4096;
    [ObservableProperty] public partial int MaxMatchesPerFile { get; set; }
    [ObservableProperty] public partial int MaxMatchesPerLine { get; set; }
    [ObservableProperty] public partial int FileIoTimeoutSeconds { get; set; }
    [ObservableProperty] public partial int AbsoluteMaxResults { get; set; }
    [ObservableProperty] public partial double MaxSearchDepth { get; set; } = double.NaN;

    partial void OnMaxMatchesPerFileChanged(int value) => ApplyMaxMatchesPerFile(value);

    private static void ApplyMaxMatchesPerFile(int value)
    {
        Yagu.Models.FileGroup.MaxMatchesPerGroup = value > 0 ? value : int.MaxValue;
    }

    partial void OnContentSearchFileSizeMBChanged(int value) => ApplyLimitSettings();
    partial void OnMaxResultsCeilingChanged(int value) => ApplyLimitSettings();
    partial void OnMmfConcurrencyLimitChanged(int value) => ApplyLimitSettings();
    partial void OnNativeConcurrencyLimitChanged(int value) => ApplyLimitSettings();
    partial void OnArchiveMaxNestingDepthChanged(int value) => ApplyLimitSettings();
    partial void OnArchiveMaxEntryMBChanged(int value) => ApplyLimitSettings();

    private void ApplyLimitSettings()
    {
        SearchOptions.MaxResultsCeiling = MaxResultsCeiling > 0 ? MaxResultsCeiling : 50_000;
        FileLister.ContentSearchFileSizeCeiling = ContentSearchFileSizeMB > 0
            ? (long)ContentSearchFileSizeMB * 1024 * 1024
            : 0;
        ContentSearcher.ConfigureGates(MmfConcurrencyLimit, NativeConcurrencyLimit);
        ZipArchiveSearcher.Configure(ArchiveMaxNestingDepth, ArchiveMaxEntryMB);
    }
}
