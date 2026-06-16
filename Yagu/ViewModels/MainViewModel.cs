using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Yagu.Models;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>A single extension entry in the skip-extensions dropdown.</summary>
public sealed partial class SkipExtensionItem : ObservableObject
{
    public string Extension { get; }
    public string Category { get; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public SkipExtensionItem(string extension, string category, bool isEnabled)
    {
        Extension = extension;
        Category = category;
        IsEnabled = isEnabled;
    }
}

public readonly record struct HydrationPayload(
    SearchResult Result,
    string MatchLine,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> ContextAfter);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SearchService _search;
    private readonly SettingsService _settingsService;
    private readonly EditorLauncher _editor;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _searchLifecycleGate = new(1, 1);
    private int _searchRunId;
    private AppSettings _settings;
    private readonly SearchResultCollection _resultCollection = new();
    private ResultStore? _resultStore;
    private CancellationTokenSource _metadataCts = new();
    private bool _metadataSortFilterRefreshQueued;
    private bool _clearedDefaultExcludeForRegexMode;
    private readonly List<SortCriterion> _sortCriteria = [new(1, 0)];
    private readonly HashSet<string> _selectedExtensionFilters = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingSortCriteria;
    private System.Diagnostics.Stopwatch? _searchTimer;
    private DateTime _searchStartedUtc;
    private TimeSpan _lastSearchElapsed;
    private long _lastSearchSortRefreshTicks;
    private bool _searchSortRefreshQueued;
    // Adaptive backoff multiplier (in seconds) for the in-search periodic sort/regroup
    // refresh. Starts at the base interval (2s) and doubles up to a 30s cap whenever a
    // refresh exceeds the slow-budget threshold, then halves on a fast pass.
    private double _searchSortRefreshIntervalSec = 2.0;
    private long _bytesScanned;
    private long _prevBytesScanned;
    private int _prevFilesScanned;
    private double _prevSampleTime;
    internal readonly List<(double filesPerSec, double mbPerSec)> ThroughputSamples = new();
    private readonly DirectoryAutoCompleteService _dirAutoComplete = new();
    private CancellationTokenSource? _dirAutoCompleteCts;
    private bool _disposed;
    private DiskSpaceSnapshot? _lowDiskSpaceCancellation;
    private static int s_postEvictionCompactingGcInFlight;
    private static long s_lastPostEvictionCompactingGcTicks;
    private static readonly TimeSpan PostEvictionCompactingGcCooldown = TimeSpan.FromSeconds(15);
    private const double SearchSortRefreshIntervalBaseSec = 2.0;
    private const double SearchSortRefreshIntervalMaxSec = 30.0;
    private const long SearchSortRefreshSlowBudgetMs = 500;

    public MainViewModel() : this(new SearchService(), new SettingsService(), new EditorLauncher(),
                                   DispatcherQueue.GetForCurrentThread())
    { }

    public MainViewModel(SearchService search, SettingsService settingsService, EditorLauncher editor, DispatcherQueue dispatcher)
    {
        _search = search;
        _settingsService = settingsService;
        _editor = editor;
        _dispatcher = dispatcher;
        _resultCollection.VisibleGroups.CollectionChanging += OnVisibleResultGroupsChanging;
        _resultCollection.VisibleGroups.CollectionChanged += OnVisibleResultGroupsChanged;

        _settings = _settingsService.Load();
        _editor.Command = _settings.EditorCommand;

        Directory = _settings.LastDirectory ?? string.Empty;
        CaseSensitive = _settings.CaseSensitive;
        UseRegex = _settings.UseRegex;
        ExactMatch = _settings.ExactMatch;
        ContextLines = _settings.ContextLines;
        PreviewContextLines = _settings.PreviewContextLines;
        ObeyGitignore = _settings.ObeyGitignore;
        GitignoreTakesPrecedence = _settings.GitignoreTakesPrecedence;
        IncludeGlobs = _settings.IncludeGlobs;
        ExcludeGlobs = _settings.ExcludeGlobs;
        IncludeFilterModeIndex = _settings.IncludeFilterModeIndex;
        ExcludeFilterModeIndex = _settings.ExcludeFilterModeIndex;
        DefaultMinFileSizeBytes = _settings.DefaultMinFileSizeBytes;
        DefaultMaxFileSizeBytes = _settings.DefaultMaxFileSizeBytes;
        MinFileSizeBytes = DefaultMinFileSizeBytes;
        MaxFileSizeBytes = DefaultMaxFileSizeBytes;
        DefaultCreatedAfterDate = _settings.DefaultCreatedAfterDate;
        DefaultCreatedBeforeDate = _settings.DefaultCreatedBeforeDate;
        DefaultModifiedAfterDate = _settings.DefaultModifiedAfterDate;
        DefaultModifiedBeforeDate = _settings.DefaultModifiedBeforeDate;
        CreatedAfterDate = DefaultCreatedAfterDate;
        CreatedBeforeDate = DefaultCreatedBeforeDate;
        ModifiedAfterDate = DefaultModifiedAfterDate;
        ModifiedBeforeDate = DefaultModifiedBeforeDate;
        MaxResults = _settings.MaxResults <= 0 ? 0 : Math.Min(_settings.MaxResults, SearchOptions.MaxResultsCeiling);
        EditorCommand = _settings.EditorCommand;
        PreviewModeIndex = _settings.PreviewModeIndex;
        ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(_settings.ThemeModeIndex);
        AppThemeService.CurrentThemeModeIndex = ThemeModeIndex;
        PreviewWrapModeIndex = NormalizePreviewWrapModeIndex(_settings.PreviewWordWrap, _settings.PreviewWrapModeIndex);
        PreviewWordWrap = PreviewWrapModeIndex == 0;
        PreviewAutoLoadMatches = _settings.PreviewAutoLoadMatches;
        SelectedPreviewContentBackgroundColor = ColorStringHelper.Normalize(
            _settings.SelectedPreviewContentBackgroundColor,
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        UnselectedPreviewContentBackgroundColor = ColorStringHelper.Normalize(
            _settings.UnselectedPreviewContentBackgroundColor,
            Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        PreviewGutterContextColor = ColorStringHelper.Normalize(
            _settings.PreviewGutterContextColor,
            Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        PreviewGutterMatchColor = ColorStringHelper.Normalize(
            _settings.PreviewGutterMatchColor,
            Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        PreviewEditorGutterColor = ColorStringHelper.Normalize(
            _settings.PreviewEditorGutterColor,
            Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6));
        // Empty string is the "Auto" sentinel (follow the app/system theme); only normalize explicit overrides.
        PreviewEditorTextColor = string.IsNullOrWhiteSpace(_settings.PreviewEditorTextColor)
            ? string.Empty
            : ColorStringHelper.Normalize(
                _settings.PreviewEditorTextColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        PreviewMatchTextColor = ColorStringHelper.Normalize(
            _settings.PreviewMatchTextColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        PreviewOverlayColor = ColorStringHelper.Normalize(
            _settings.PreviewOverlayColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x00));
        PreviewMatchLineColor = ColorStringHelper.Normalize(
            _settings.PreviewMatchLineColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        PreviewShowMoreEllipsisColor = ColorStringHelper.Normalize(
            _settings.PreviewShowMoreEllipsisColor,
            Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
        PreviewShowMoreEllipsisFontSize = Math.Clamp(
            _settings.PreviewShowMoreEllipsisFontSize <= 0 ? AppSettings.DefaultPreviewShowMoreEllipsisFontSize : _settings.PreviewShowMoreEllipsisFontSize,
            6,
            72);
        PreviewTextFontFamily = string.IsNullOrWhiteSpace(_settings.PreviewTextFontFamily)
            ? AppSettings.DefaultPreviewTextFontFamily
            : _settings.PreviewTextFontFamily;
        PreviewTextFontSize = Math.Clamp(
            _settings.PreviewTextFontSize <= 0 ? AppSettings.DefaultPreviewTextFontSize : _settings.PreviewTextFontSize,
            6,
            72);
        PreviewEditorFontFamily = string.IsNullOrWhiteSpace(_settings.PreviewEditorFontFamily)
            ? AppSettings.DefaultPreviewEditorFontFamily
            : _settings.PreviewEditorFontFamily;
        PreviewEditorFontSize = Math.Clamp(
            _settings.PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : _settings.PreviewEditorFontSize,
            6,
            72);
        ResultListMatchTextFontFamily = string.IsNullOrWhiteSpace(_settings.ResultListMatchTextFontFamily)
            ? AppSettings.DefaultResultListMatchTextFontFamily
            : _settings.ResultListMatchTextFontFamily;
        ResultListMatchTextFontSize = Math.Clamp(
            _settings.ResultListMatchTextFontSize <= 0 ? AppSettings.DefaultResultListMatchTextFontSize : _settings.ResultListMatchTextFontSize,
            6,
            72);
        ResultListMatchHighlightColor = ColorStringHelper.Normalize(
            _settings.ResultListMatchHighlightColor,
            Windows.UI.Color.FromArgb(0xFF, 0xB8, 0x86, 0x0B));
        FileLogLevelIndex = _settings.LogLevelIndex;
        ConsoleLogLevelIndex = _settings.ConsoleLogLevelIndex;
        FileListerBackendIndex = _settings.FileListerBackendIndex;
        ParallelismIndex = _settings.ParallelismIndex;
        LineTruncationLength = _settings.LineTruncationLength;
        MaxRecentItems = _settings.MaxRecentItems;
        GlobalHotkeyEnabled = _settings.GlobalHotkeyEnabled;
        GlobalHotkeyKey = HotkeyService.TryNormalizeLetter(_settings.GlobalHotkeyKey, out var hotkeyKey)
            ? hotkeyKey.ToString()
            : HotkeyService.DefaultStartKey.ToString();
        MemoryLimitMB = _settings.MemoryLimitMB;
        MemoryPressurePercent = _settings.MemoryPressurePercent;
        SearchResultTempDirectory = ResultStoreTempLocationService.NormalizeTempDirectory(_settings.SearchResultTempDirectory);
        HasChosenSearchResultTempDirectory = _settings.HasChosenSearchResultTempDirectory;
        LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(_settings.LowDiskSpaceWarningPercent);
        ShowMemoryPressureWarningLabel = _settings.ShowMemoryPressureWarningLabel;
        ShowStatsForNerds = _settings.ShowStatsForNerds;
        ShowBuildNumberInTitleBar = _settings.ShowBuildNumberInTitleBar;
        ShowAutoScrollResultsCheckbox = _settings.ShowAutoScrollResultsCheckbox;
        SdkChannelBufferSize = _settings.SdkChannelBufferSize;
        MaxMatchesPerFile = _settings.MaxMatchesPerFile;
        ApplyMaxMatchesPerFile(MaxMatchesPerFile);
        SkipBinary = _settings.SkipBinary;
        SearchInsideArchives = _settings.SearchInsideArchives;
        SettingsSkipExtensions = _settings.SkipExtensions;
        SettingsBinaryExtensions = _settings.BinaryExtensions;
        SettingsArchiveExtensions = _settings.ArchiveExtensions;
        SkipExtensions = SettingsSkipExtensions;
        BinaryExtensions = SettingsBinaryExtensions;
        ArchiveExtensions = SettingsArchiveExtensions;
        SuppressAdminWarning = _settings.SuppressAdminWarning;
        SuppressFontContrastWarnings = _settings.SuppressFontContrastWarnings;
        FontContrastReminderAfterUtc = _settings.FontContrastReminderAfterUtc;
        ExcludeAdminProtectedPaths = _settings.ExcludeAdminProtectedPaths;
        AdminProtectedPathSegments = string.IsNullOrWhiteSpace(_settings.AdminProtectedPathSegments)
            ? AppSettings.DefaultAdminProtectedPathSegments
            : _settings.AdminProtectedPathSegments;
        HasCompletedFirstRun = _settings.HasCompletedFirstRun;
        HasShownFileDrawerIntroTip = _settings.HasShownFileDrawerIntroTip;
        HasShownFileDrawerLineNumberIntroTip = _settings.HasShownFileDrawerLineNumberIntroTip;
        HasShownPreviewMatchIntroTip = _settings.HasShownPreviewMatchIntroTip;
        LimitParallelismOnHdd = _settings.LimitParallelismOnHdd;
        BackupBeforeSave = _settings.BackupBeforeSave;
        ShowEditorSavedOverlay = _settings.ShowEditorSavedOverlay;
        WindowFocusBehavior = _settings.WindowFocusBehavior;
        StartInLauncherMode = _settings.StartInLauncherMode;
        CloseToTray = _settings.CloseToTray;
        MaximizeOnStartup = _settings.MaximizeOnStartup;
        AdvancedOptionsCollapsedWidthModeIndex = NormalizeAdvancedOptionsCollapsedWidthModeIndex(_settings.AdvancedOptionsCollapsedWidthModeIndex);
        TerminalDefaultWorkingDirectory = _settings.TerminalDefaultWorkingDirectory ?? string.Empty;
        FileHeaderCheckAddsToPreview = _settings.FileHeaderCheckAddsToPreview;
        MatchLineCheckAddsToPreview = _settings.MatchLineCheckAddsToPreview;
        PreviewEditorMaxSizeMB = _settings.PreviewEditorMaxSizeMB;
        PreviewEditorMaxTextLength = _settings.PreviewEditorMaxTextLength;
        PreviewEditorMaxLineLength = _settings.PreviewEditorMaxLineLength;
        ContentSearchFileSizeMB = _settings.ContentSearchFileSizeMB;
        MaxResultsCeiling = _settings.MaxResultsCeiling > 0 ? _settings.MaxResultsCeiling : 50_000;
        MmfConcurrencyLimit = _settings.MmfConcurrencyLimit;
        NativeConcurrencyLimit = _settings.NativeConcurrencyLimit;

        MaxMatchesPerSection = _settings.MaxMatchesPerSection;
        PreviewSectionPageSize = _settings.PreviewSectionPageSize;
        FullFilePreviewLimitMB = _settings.FullFilePreviewLimitMB;
        ArchiveMaxNestingDepth = _settings.ArchiveMaxNestingDepth;
        ArchiveMaxEntryMB = _settings.ArchiveMaxEntryMB;

        ApplyLimitSettings();

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        foreach (var d in _settings.RecentDirectories) DirectorySuggestions.Add(d);
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);

        SyncSkipExtensionItems();
        SyncBinaryExtensionItems();
        SyncArchiveExtensionItems();
    }

    private static int NormalizePreviewWrapModeIndex(bool legacyPreviewWordWrap, int modeIndex)
    {
        if (legacyPreviewWordWrap)
            return (int)PreviewWrapMode.Wrap;

        return modeIndex == (int)PreviewWrapMode.Wrap
            ? (int)PreviewWrapMode.Wrap
            : (int)PreviewWrapMode.NoWrap;
    }

    private static int NormalizeAdvancedOptionsCollapsedWidthModeIndex(int modeIndex) =>
        modeIndex == 1 ? 1 : 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        try { _dirAutoCompleteCts?.Cancel(); } catch { }
        try { _metadataCts.Cancel(); } catch { }
        _cts?.Dispose();
        _dirAutoCompleteCts?.Dispose();
        _metadataCts.Dispose();
        _searchLifecycleGate.Dispose();
        _resultStore?.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty] public partial string Directory { get; set; } = string.Empty;
    [ObservableProperty] public partial string Query { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial bool ExactMatch { get; set; } = true;

    public Microsoft.UI.Xaml.Visibility HasQueryText =>
        string.IsNullOrEmpty(Query) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnQueryChanged(string value) => OnPropertyChanged(nameof(HasQueryText));

    [ObservableProperty] public partial int ContextLines { get; set; } = 3;
    [ObservableProperty] public partial int PreviewContextLines { get; set; } = 20;
    [ObservableProperty] public partial bool ObeyGitignore { get; set; }
    [ObservableProperty] public partial bool GitignoreTakesPrecedence { get; set; } = true;
    [ObservableProperty] public partial string IncludeGlobs { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExcludeGlobs { get; set; } = AppSettings.DefaultExcludeGlobs;
    [ObservableProperty] public partial int IncludeFilterModeIndex { get; set; }
    [ObservableProperty] public partial int ExcludeFilterModeIndex { get; set; }
    [ObservableProperty] public partial long MinFileSizeBytes { get; set; }
    [ObservableProperty] public partial long MaxFileSizeBytes { get; set; }
    [ObservableProperty] public partial long DefaultMinFileSizeBytes { get; set; }
    [ObservableProperty] public partial long DefaultMaxFileSizeBytes { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CreatedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CreatedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ModifiedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ModifiedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultCreatedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultCreatedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultModifiedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultModifiedBeforeDate { get; set; }
    [ObservableProperty] public partial int MaxResults { get; set; }
    [ObservableProperty] public partial string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    [ObservableProperty] public partial string FileNameFilter { get; set; } = string.Empty;
    [ObservableProperty] public partial int SearchModeIndex { get; set; }
    [ObservableProperty] public partial int SortModeIndex { get; set; }
    [ObservableProperty] public partial int SortDirectionIndex { get; set; }
    [ObservableProperty] public partial int GroupModeIndex { get; set; }
    [ObservableProperty] public partial int GroupSortDirectionIndex { get; set; }
    [ObservableProperty] public partial int DateRangeFilterIndex { get; set; }

    public GroupMode GroupMode => (GroupMode)GroupModeIndex;
    public FilterPatternMode IncludeFilterMode => IncludeFilterModeIndex == 1 ? FilterPatternMode.Regex : FilterPatternMode.GlobPath;
    public FilterPatternMode ExcludeFilterMode => ExcludeFilterModeIndex == 1 ? FilterPatternMode.Regex : FilterPatternMode.GlobPath;
    public string IncludeFilterPlaceholder => IncludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. \.(cs|xaml)$"
        : "e.g. ts,js,py or *.cs";
    public string ExcludeFilterPlaceholder => ExcludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. (^|/)node_modules/|\.min\.js$"
        : $"e.g. {AppSettings.DefaultExcludeGlobs}";
    public string GroupModeLabel => GroupMode switch
    {
        GroupMode.None => "None",
        GroupMode.Folder => "Folder",
        GroupMode.DateRangeModified => "Date range (Modified)",
        GroupMode.DateRangeCreated => "Date range (Created)",
        GroupMode.DateRangeModifiedCreated => "Date range (Modified + Created)",
        GroupMode.Extension => "Extension",
        GroupMode.FileSize => "File size",
        _ => "None",
    };
    public string GroupSortDirectionLabel => GroupMode switch
    {
        GroupMode.FileSize => GroupSortDirectionIndex == 0 ? "Small to large" : "Large to small",
        GroupMode.DateRangeModified or GroupMode.DateRangeCreated or GroupMode.DateRangeModifiedCreated =>
            GroupSortDirectionIndex == 0 ? "Recent first" : "Older first",
        _ => GroupSortDirectionIndex == 0 ? "A-Z" : "Z-A",
    };
    public DateRangeFilter DateRangeFilter => (DateRangeFilter)DateRangeFilterIndex;
    public string DateRangeFilterLabel => DateRangeFilter switch
    {
        DateRangeFilter.None => "Any date",
        DateRangeFilter.PastDay => "Last day",
        DateRangeFilter.PastWeek => "Last week",
        DateRangeFilter.PastTwoWeeks => "Last 2 weeks",
        DateRangeFilter.PastMonth => "Last month",
        DateRangeFilter.PastThreeMonths => "Last 3 months",
        DateRangeFilter.PastSixMonths => "Last 6 months",
        DateRangeFilter.PastNineMonths => "Last 9 months",
        DateRangeFilter.PastYear => "Last year",
        DateRangeFilter.PastTwoYears => "Last 2 years",
        DateRangeFilter.PastThreeYears => "Last 3 years",
        DateRangeFilter.PastFiveYears => "Last 5 years",
        _ => "Any date",
    };
    public bool HasExtensionFilter => _selectedExtensionFilters.Count > 0;
    public string ExtensionFilterLabel => _selectedExtensionFilters.Count switch
    {
        0 => "All extensions",
        1 => SearchResultCollection.FormatExtensionDisplayName(_selectedExtensionFilters.First()),
        _ => $"{_selectedExtensionFilters.Count:N0} extensions",
    };

    public IReadOnlyList<SortCriterion> SortCriteria => _sortCriteria;

    public int? GetSortDirectionIndex(int sortModeIndex)
    {
        int index = _sortCriteria.FindIndex(criterion => criterion.SortModeIndex == sortModeIndex);
        return index >= 0 ? _sortCriteria[index].SortDirectionIndex : null;
    }

    public void ApplySortSelection(int sortModeIndex, int sortDirectionIndex)
    {
        if (sortModeIndex <= 0)
        {
            SetSingleSortCriterion(0, sortDirectionIndex);
        }
        else
        {
            int direction = sortDirectionIndex == 1 ? 1 : 0;
            int index = _sortCriteria.FindIndex(criterion => criterion.SortModeIndex == sortModeIndex);
            var criterion = new SortCriterion(sortModeIndex, direction);
            if (index >= 0)
                _sortCriteria[index] = criterion;
            else
                _sortCriteria.Add(criterion);
        }

        SyncPrimarySortPropertiesFromCriteria();
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }

    public void RemoveSortSelection(int sortModeIndex)
    {
        int index = _sortCriteria.FindIndex(criterion => criterion.SortModeIndex == sortModeIndex);
        if (index < 0)
            return;

        _sortCriteria.RemoveAt(index);
        SyncPrimarySortPropertiesFromCriteria();
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }

    public IReadOnlyList<ExtensionFilterOption> GetExtensionFilterOptions() =>
        _resultCollection.GetExtensionFilterOptions();

    public void SetExtensionFilter(IEnumerable<string> extensions)
    {
        _selectedExtensionFilters.Clear();
        foreach (string extension in extensions)
        {
            string normalized = SearchResultCollection.NormalizeExtensionFilter(extension);
            if (!string.IsNullOrWhiteSpace(normalized))
                _selectedExtensionFilters.Add(normalized);
        }

        OnPropertyChanged(nameof(HasExtensionFilter));
        OnPropertyChanged(nameof(ExtensionFilterLabel));
        ApplySortAndFilter();
    }

    public void ClearExtensionFilter() => SetExtensionFilter([]);

    private void SetSingleSortCriterion(int sortModeIndex, int sortDirectionIndex)
    {
        _sortCriteria.Clear();
        if (sortModeIndex > 0)
            _sortCriteria.Add(new SortCriterion(sortModeIndex, sortDirectionIndex == 1 ? 1 : 0));
    }

    private void SyncPrimarySortPropertiesFromCriteria()
    {
        _updatingSortCriteria = true;
        try
        {
            if (_sortCriteria.Count > 0)
            {
                SortModeIndex = _sortCriteria[0].SortModeIndex;
                SortDirectionIndex = _sortCriteria[0].SortDirectionIndex;
            }
            else
            {
                SortModeIndex = 0;
                SortDirectionIndex = 0;
            }
        }
        finally
        {
            _updatingSortCriteria = false;
        }
    }
    [ObservableProperty] public partial int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    [ObservableProperty] public partial int ThemeModeIndex { get; set; } // 0 = Auto (system theme), 1 = Dark, 2 = Light
    [ObservableProperty] public partial bool PreviewWordWrap { get; set; }
    [ObservableProperty] public partial int PreviewWrapModeIndex { get; set; } = 2; // 0 = Wrap, 1 = legacy PartialWrap, 2 = NoWrap
    [ObservableProperty] public partial int PreviewAutoLoadMatches { get; set; } = 50;
    [ObservableProperty] public partial string SelectedPreviewContentBackgroundColor { get; set; } = AppSettings.DefaultSelectedPreviewContentBackgroundColor;
    [ObservableProperty] public partial string UnselectedPreviewContentBackgroundColor { get; set; } = AppSettings.DefaultUnselectedPreviewContentBackgroundColor;
    [ObservableProperty] public partial string PreviewGutterContextColor { get; set; } = AppSettings.DefaultPreviewGutterContextColor;
    [ObservableProperty] public partial string PreviewGutterMatchColor { get; set; } = AppSettings.DefaultPreviewGutterMatchColor;
    [ObservableProperty] public partial string PreviewEditorGutterColor { get; set; } = AppSettings.DefaultPreviewEditorGutterColor;
    // Empty string = "Auto" (follow the app/system theme); a non-empty ARGB hex is an explicit override.
    [ObservableProperty] public partial string PreviewEditorTextColor { get; set; } = AppSettings.DefaultPreviewEditorTextColor;
    [ObservableProperty] public partial string PreviewMatchTextColor { get; set; } = AppSettings.DefaultPreviewMatchTextColor;
    [ObservableProperty] public partial string PreviewOverlayColor { get; set; } = AppSettings.DefaultPreviewOverlayColor;
    [ObservableProperty] public partial string PreviewMatchLineColor { get; set; } = AppSettings.DefaultPreviewMatchLineColor;
    [ObservableProperty] public partial string PreviewShowMoreEllipsisColor { get; set; } = AppSettings.DefaultPreviewShowMoreEllipsisColor;
    [ObservableProperty] public partial int PreviewShowMoreEllipsisFontSize { get; set; } = AppSettings.DefaultPreviewShowMoreEllipsisFontSize;
    [ObservableProperty] public partial string PreviewTextFontFamily { get; set; } = AppSettings.DefaultPreviewTextFontFamily;
    [ObservableProperty] public partial int PreviewTextFontSize { get; set; } = AppSettings.DefaultPreviewTextFontSize;
    [ObservableProperty] public partial string PreviewEditorFontFamily { get; set; } = AppSettings.DefaultPreviewEditorFontFamily;
    [ObservableProperty] public partial int PreviewEditorFontSize { get; set; } = AppSettings.DefaultPreviewEditorFontSize;
    [ObservableProperty] public partial string ResultListMatchTextFontFamily { get; set; } = AppSettings.DefaultResultListMatchTextFontFamily;
    [ObservableProperty] public partial int ResultListMatchTextFontSize { get; set; } = AppSettings.DefaultResultListMatchTextFontSize;
    [ObservableProperty] public partial string ResultListMatchHighlightColor { get; set; } = AppSettings.DefaultResultListMatchHighlightColor;

    public IReadOnlyList<FontContrastCandidate> GetFontContrastCandidates()
    {
        var selectedPreviewBackground = FontContrastColor.Parse(
            SelectedPreviewContentBackgroundColor,
            FontContrastColor.FromArgb(0xFF, 0x00, 0x00, 0x00));
        var unselectedPreviewBackground = FontContrastColor.Parse(
            UnselectedPreviewContentBackgroundColor,
            FontContrastColor.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));

        return
        [
            new(nameof(PreviewGutterContextColor), "selected preview content", "Preview gutter text", PreviewGutterContextColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), selectedPreviewBackground),
            new(nameof(PreviewGutterContextColor), "unselected preview content", "Preview gutter text", PreviewGutterContextColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), unselectedPreviewBackground),
            new(nameof(PreviewGutterMatchColor), "selected preview content", "Matched preview gutter text", PreviewGutterMatchColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), selectedPreviewBackground),
            new(nameof(PreviewGutterMatchColor), "unselected preview content", "Matched preview gutter text", PreviewGutterMatchColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), unselectedPreviewBackground),
            new(nameof(PreviewMatchTextColor), "selected preview content", "Match highlight text", PreviewMatchTextColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xD7, 0x00), selectedPreviewBackground),
            new(nameof(PreviewMatchTextColor), "unselected preview content", "Match highlight text", PreviewMatchTextColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xD7, 0x00), unselectedPreviewBackground),
            new(nameof(PreviewMatchLineColor), "selected preview content", "Matched line text", PreviewMatchLineColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), selectedPreviewBackground),
            new(nameof(PreviewMatchLineColor), "unselected preview content", "Matched line text", PreviewMatchLineColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), unselectedPreviewBackground),
            new(nameof(PreviewEditorGutterColor), "built-in editor", "Editor gutter text", PreviewEditorGutterColor, FontContrastColor.FromArgb(0xFF, 0x3A, 0x8F, 0xD6)),
            new(nameof(ResultListMatchHighlightColor), "file list", "Highlighted match text", ResultListMatchHighlightColor, FontContrastColor.FromArgb(0xFF, 0xB8, 0x86, 0x0B)),
        ];
    }

    public void ApplyFontContrastColor(string key, string colorHex)
    {
        switch (key)
        {
            case nameof(PreviewGutterContextColor):
                PreviewGutterContextColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
                break;
            case nameof(PreviewGutterMatchColor):
                PreviewGutterMatchColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
                break;
            case nameof(PreviewMatchTextColor):
                PreviewMatchTextColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
                break;
            case nameof(PreviewMatchLineColor):
                PreviewMatchLineColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
                break;
            case nameof(PreviewEditorGutterColor):
                PreviewEditorGutterColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6));
                break;
            case nameof(ResultListMatchHighlightColor):
                ResultListMatchHighlightColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0xB8, 0x86, 0x0B));
                break;
        }
    }

    public void ResetFontContrastReminderState()
    {
        SuppressFontContrastWarnings = false;
        FontContrastReminderAfterUtc = null;
    }

    [ObservableProperty] public partial int FileLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] public partial int ParallelismIndex { get; set; } = 4; // 0 = safe cap, 1 = 1 thread, 2 = half cores, 3 = 2x cores, 4 = all cores

    /// <summary>
    /// Session-only parallelism override applied when the search target is detected on a rotational
    /// HDD. When non-null it takes precedence over <see cref="ParallelismIndex"/> for the actual
    /// search, but is never written back to <see cref="ParallelismIndex"/> nor persisted to settings.
    /// It is cleared when the user explicitly changes the parallelism setting, and lost on restart.
    /// </summary>
    private int? _sessionParallelismOverrideIndex;

    /// <summary>
    /// Applies a session-only parallelism override (e.g. limiting to 1 thread on an HDD). This does
    /// NOT modify the persisted <see cref="ParallelismIndex"/> setting; it only affects searches in
    /// the current session until the app restarts or the user changes parallelism in Settings.
    /// </summary>
    public void SetSessionParallelismOverride(int index) => _sessionParallelismOverrideIndex = index;

    // When the user (or settings load) explicitly changes the persisted parallelism index, drop any
    // session-only HDD override so the user's choice takes effect.
    partial void OnParallelismIndexChanged(int value) => _sessionParallelismOverrideIndex = null;

    [ObservableProperty] public partial int LineTruncationLength { get; set; } = 500;
    [ObservableProperty] public partial int MaxRecentItems { get; set; } = 20;
    [ObservableProperty] public partial bool GlobalHotkeyEnabled { get; set; }
    [ObservableProperty] public partial int MemoryLimitMB { get; set; }
    [ObservableProperty] public partial int MemoryPressurePercent { get; set; } = 75;
    [ObservableProperty] public partial int LowDiskSpaceWarningPercent { get; set; } = AppSettings.DefaultLowDiskSpaceWarningPercent;
    [ObservableProperty] public partial bool ShowMemoryPressureWarningLabel { get; set; }
    [ObservableProperty] public partial bool ShowStatsForNerds { get; set; }
    [ObservableProperty] public partial bool ShowBuildNumberInTitleBar { get; set; }
    [ObservableProperty] public partial bool ShowAutoScrollResultsCheckbox { get; set; }
    [ObservableProperty] public partial int SdkChannelBufferSize { get; set; } = 4096;
    [ObservableProperty] public partial int MaxMatchesPerFile { get; set; }
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

    [ObservableProperty] public partial bool SkipBinary { get; set; } = true;

    /// <summary>UI-facing inverse of <see cref="SkipBinary"/> for the "Search binary" toggle.</summary>
    public bool SearchBinary
    {
        get => !SkipBinary;
        set => SkipBinary = !value;
    }

    partial void OnSkipBinaryChanged(bool value)
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SearchBinary)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(BinaryExtensionsVisibility)));
    }

    [ObservableProperty] public partial string SkipExtensions { get; set; } = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] public partial string BinaryExtensions { get; set; } = AppSettings.DefaultBinaryExtensions;
    [ObservableProperty] public partial bool SearchInsideArchives { get; set; }
    [ObservableProperty] public partial string ArchiveExtensions { get; set; } = AppSettings.DefaultArchiveExtensions;
    [ObservableProperty] public partial string SettingsSkipExtensions { get; set; } = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] public partial string SettingsBinaryExtensions { get; set; } = AppSettings.DefaultBinaryExtensions;
    [ObservableProperty] public partial string SettingsArchiveExtensions { get; set; } = AppSettings.DefaultArchiveExtensions;
    [ObservableProperty] public partial int PreviewEditorMaxSizeMB { get; set; } = 32;
    [ObservableProperty] public partial int PreviewEditorMaxTextLength { get; set; } = 20_000_000;
    [ObservableProperty] public partial int PreviewEditorMaxLineLength { get; set; } = 1_000_000;
    [ObservableProperty] public partial int ContentSearchFileSizeMB { get; set; } = 100;
    [ObservableProperty] public partial int MaxResultsCeiling { get; set; } = 50_000;
    [ObservableProperty] public partial int MmfConcurrencyLimit { get; set; }
    [ObservableProperty] public partial int NativeConcurrencyLimit { get; set; }
    [ObservableProperty] public partial int MaxMatchesPerSection { get; set; }
    [ObservableProperty] public partial int PreviewSectionPageSize { get; set; }
    [ObservableProperty] public partial int FullFilePreviewLimitMB { get; set; }
    [ObservableProperty] public partial int ArchiveMaxNestingDepth { get; set; }
    [ObservableProperty] public partial int ArchiveMaxEntryMB { get; set; }

    public double MinFileSizeMB
    {
        get => MinFileSizeBytes == 0 ? double.NaN : MinFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (MinFileSizeBytes != bytes)
                MinFileSizeBytes = bytes;
        }
    }

    public double MaxFileSizeMB
    {
        get => MaxFileSizeBytes == 0 ? double.NaN : MaxFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (MaxFileSizeBytes != bytes)
                MaxFileSizeBytes = bytes;
        }
    }

    public double DefaultMinFileSizeMB
    {
        get => DefaultMinFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (DefaultMinFileSizeBytes != bytes)
                DefaultMinFileSizeBytes = bytes;
        }
    }

    public double DefaultMaxFileSizeMB
    {
        get => DefaultMaxFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (DefaultMaxFileSizeBytes != bytes)
                DefaultMaxFileSizeBytes = bytes;
        }
    }

    private static long MegabytesToBytes(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return 0;

        double bytes = value * 1024d * 1024d;
        if (bytes >= long.MaxValue)
            return long.MaxValue;

        return (long)Math.Round(bytes);
    }

    private static bool IsDateRangeInvalid(DateTimeOffset? after, DateTimeOffset? before)
        => after.HasValue && before.HasValue && after.Value.LocalDateTime.Date > before.Value.LocalDateTime.Date;

    private bool _suppressAdminWarning;
    public bool SuppressAdminWarning
    {
        get => _suppressAdminWarning;
        set => SetProperty(ref _suppressAdminWarning, value);
    }

    [ObservableProperty] public partial bool SuppressFontContrastWarnings { get; set; }
    [ObservableProperty] public partial DateTimeOffset? FontContrastReminderAfterUtc { get; set; }

    [ObservableProperty] public partial bool ExcludeAdminProtectedPaths { get; set; } = true;
    [ObservableProperty] public partial string AdminProtectedPathSegments { get; set; } = AppSettings.DefaultAdminProtectedPathSegments;

    [ObservableProperty] public partial bool HasCompletedFirstRun { get; set; }
    [ObservableProperty] public partial bool HasShownFileDrawerIntroTip { get; set; }
    [ObservableProperty] public partial bool HasShownFileDrawerLineNumberIntroTip { get; set; }
    [ObservableProperty] public partial bool HasShownPreviewMatchIntroTip { get; set; }

    public void ResetFirstTimeIntroductoryTooltips()
    {
        HasShownFileDrawerIntroTip = false;
        HasShownFileDrawerLineNumberIntroTip = false;
        HasShownPreviewMatchIntroTip = false;
    }

    public void RestoreFirstTimeIntroductoryTooltips(bool fileDrawer, bool fileDrawerLineNumber, bool previewMatch)
    {
        HasShownFileDrawerIntroTip = fileDrawer;
        HasShownFileDrawerLineNumberIntroTip = fileDrawerLineNumber;
        HasShownPreviewMatchIntroTip = previewMatch;
    }

    public Task MarkFileDrawerIntroTipShownAsync()
        => MarkIntroTipShownAsync(nameof(HasShownFileDrawerIntroTip));

    public Task MarkFileDrawerLineNumberIntroTipShownAsync()
        => MarkIntroTipShownAsync(nameof(HasShownFileDrawerLineNumberIntroTip));

    public Task MarkPreviewMatchIntroTipShownAsync()
        => MarkIntroTipShownAsync(nameof(HasShownPreviewMatchIntroTip));

    private async Task MarkIntroTipShownAsync(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(HasShownFileDrawerIntroTip):
                if (HasShownFileDrawerIntroTip) return;
                HasShownFileDrawerIntroTip = true;
                _settings.HasShownFileDrawerIntroTip = true;
                break;
            case nameof(HasShownFileDrawerLineNumberIntroTip):
                if (HasShownFileDrawerLineNumberIntroTip) return;
                HasShownFileDrawerLineNumberIntroTip = true;
                _settings.HasShownFileDrawerLineNumberIntroTip = true;
                break;
            case nameof(HasShownPreviewMatchIntroTip):
                if (HasShownPreviewMatchIntroTip) return;
                HasShownPreviewMatchIntroTip = true;
                _settings.HasShownPreviewMatchIntroTip = true;
                break;
            default:
                return;
        }

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }

    [ObservableProperty] public partial string SearchResultTempDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasChosenSearchResultTempDirectory { get; set; }
    [ObservableProperty] public partial bool LimitParallelismOnHdd { get; set; } = true;
    [ObservableProperty] public partial bool BackupBeforeSave { get; set; } = true;
    [ObservableProperty] public partial bool ShowEditorSavedOverlay { get; set; } = true;
    [ObservableProperty] public partial int WindowFocusBehavior { get; set; } = 1; // 0 = MinimizeToTray, 1 = StayOpen (default), 2 = AlwaysOnTop
    [ObservableProperty] public partial bool StartInLauncherMode { get; set; } = true;
    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;
    [ObservableProperty] public partial bool MaximizeOnStartup { get; set; }
    [ObservableProperty] public partial int AdvancedOptionsCollapsedWidthModeIndex { get; set; }
    [ObservableProperty] public partial string TerminalDefaultWorkingDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial bool FileHeaderCheckAddsToPreview { get; set; } = true;
    [ObservableProperty] public partial bool MatchLineCheckAddsToPreview { get; set; } = true;

    /// <summary>Observable collection of skip-extension items for the multi-select dropdown.</summary>
    public ObservableCollection<SkipExtensionItem> SkipExtensionItems { get; } = [];

    /// <summary>Summary label for the skip-extensions dropdown button.</summary>
    public string SkipExtensionsSummary
    {
        get
        {
            int enabled = SkipExtensionItems.Count(i => i.IsEnabled);
            int total = SkipExtensionItems.Count;
            return total == 0 ? "Skip Extensions: none" : $"Skip Extensions: {enabled}/{total}";
        }
    }

    private static readonly Dictionary<string, string> ExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Binaries / Build
        ["exe"] = "Binaries", ["dll"] = "Binaries", ["pdb"] = "Binaries", ["obj"] = "Binaries",
        ["lib"] = "Binaries", ["so"] = "Binaries", ["dylib"] = "Binaries",
        ["com"] = "Binaries", ["scr"] = "Binaries", ["sys"] = "Binaries", ["drv"] = "Binaries",
        ["ocx"] = "Binaries", ["cpl"] = "Binaries", ["mui"] = "Binaries", ["winmd"] = "Binaries",
        ["pri"] = "Binaries", ["cat"] = "Binaries", ["res"] = "Binaries", ["resources"] = "Binaries",
        ["o"] = "Binaries", ["a"] = "Binaries", ["lo"] = "Binaries", ["la"] = "Binaries",
        ["ilk"] = "Binaries", ["iobj"] = "Binaries", ["ipdb"] = "Binaries", ["exp"] = "Binaries",
        ["pyc"] = "Binaries", ["pyo"] = "Binaries", ["class"] = "Binaries", ["dex"] = "Binaries",
        ["wasm"] = "Binaries",
        // Data / Dumps
        ["bin"] = "Data", ["dat"] = "Data", ["db"] = "Data", ["db3"] = "Data",
        ["sqlite"] = "Data", ["sqlite3"] = "Data", ["edb"] = "Data", ["mdb"] = "Data",
        ["accdb"] = "Data", ["ldb"] = "Data", ["sdf"] = "Data", ["cache"] = "Data",
        ["tmp"] = "Data", ["bak"] = "Data", ["etl"] = "Data", ["evtx"] = "Data",
        ["dmp"] = "Data", ["mdmp"] = "Data", ["hdmp"] = "Data", ["hprof"] = "Data",
        ["vhd"] = "Data", ["vhdx"] = "Data", ["vmdk"] = "Data", ["pak"] = "Data",
        ["usm"] = "Data", ["bundle"] = "Data", ["assets"] = "Data",
        // Images
        ["png"] = "Images", ["jpg"] = "Images", ["jpeg"] = "Images", ["gif"] = "Images",
        ["bmp"] = "Images", ["ico"] = "Images", ["tif"] = "Images", ["tiff"] = "Images",
        ["webp"] = "Images", ["svg"] = "Images", ["heic"] = "Images", ["heif"] = "Images",
        ["avif"] = "Images",
        // Audio / Video
        ["mp3"] = "Media", ["mp4"] = "Media", ["avi"] = "Media", ["mov"] = "Media",
        ["wmv"] = "Media", ["flv"] = "Media", ["mkv"] = "Media", ["wav"] = "Media",
        ["ogg"] = "Media", ["flac"] = "Media", ["m4a"] = "Media", ["webm"] = "Media",
        // Fonts
        ["woff"] = "Fonts", ["woff2"] = "Fonts", ["ttf"] = "Fonts", ["eot"] = "Fonts", ["otf"] = "Fonts",
        // Documents
        ["pdf"] = "Documents", ["doc"] = "Documents",
        ["xls"] = "Documents", ["ppt"] = "Documents",
    };

    private static string CategorizeExtension(string ext) =>
        ExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";

    private bool _suppressSkipExtensionSync;
    private bool _updatingSkipExtensionsFromItems;

    partial void OnSkipExtensionsChanged(string value)
    {
        if (_suppressSkipExtensionSync || _updatingSkipExtensionsFromItems) return;
        SyncSkipExtensionItems();
    }

    partial void OnSettingsSkipExtensionsChanged(string value)
    {
        if (_suppressSkipExtensionSync || _updatingSkipExtensionsFromItems) return;
        SyncSkipExtensionItems();
    }

    /// <summary>Rebuild the <see cref="SkipExtensionItems"/> collection from the current <see cref="SkipExtensions"/> string.</summary>
    public void SyncSkipExtensionItems()
    {
        _suppressSkipExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(SkipExtensions);
            var available = ParseExtensionSet(SettingsSkipExtensions);
            foreach (var ext in enabled)
                available.Add(ext);

            SkipExtensionItems.Clear();

            var groups = available
                .GroupBy(CategorizeExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    SkipExtensionItems.Add(new SkipExtensionItem(ext, group.Key, enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(SkipExtensionsSummary));
        }
        finally
        {
            _suppressSkipExtensionSync = false;
        }
    }

    /// <summary>Called when a skip-extension item is toggled. Rebuilds the string and persists.</summary>
    public void OnSkipExtensionToggled()
    {
        if (_suppressSkipExtensionSync) return;
        var enabled = SkipExtensionItems.Where(i => i.IsEnabled).Select(i => i.Extension);
        _updatingSkipExtensionsFromItems = true;
        try
        {
            SkipExtensions = string.Join(';', enabled);
        }
        finally
        {
            _updatingSkipExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(SkipExtensionsSummary));
    }

    // ── Binary extensions dropdown ───────────────────────────────

    public ObservableCollection<SkipExtensionItem> BinaryExtensionItems { get; } = [];

    public string BinaryExtensionsSummary
    {
        get
        {
            int enabled = BinaryExtensionItems.Count(i => i.IsEnabled);
            int total = BinaryExtensionItems.Count;
            return total == 0 ? "Binary ext: none" : $"Binary ext: {enabled}/{total}";
        }
    }

    public Microsoft.UI.Xaml.Visibility BinaryExtensionsVisibility => SearchBinary
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    private bool _suppressBinaryExtensionSync;
    private bool _updatingBinaryExtensionsFromItems;

    partial void OnBinaryExtensionsChanged(string value)
    {
        if (_suppressBinaryExtensionSync || _updatingBinaryExtensionsFromItems) return;
        SyncBinaryExtensionItems();
    }

    partial void OnSettingsBinaryExtensionsChanged(string value)
    {
        if (_suppressBinaryExtensionSync || _updatingBinaryExtensionsFromItems) return;
        SyncBinaryExtensionItems();
    }

    public void SyncBinaryExtensionItems()
    {
        _suppressBinaryExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(BinaryExtensions);
            var available = ParseExtensionSet(SettingsBinaryExtensions);
            foreach (var ext in enabled)
                available.Add(ext);

            BinaryExtensionItems.Clear();

            var groups = available
                .GroupBy(CategorizeExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    BinaryExtensionItems.Add(new SkipExtensionItem(ext, group.Key, enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(BinaryExtensionsSummary));
        }
        finally
        {
            _suppressBinaryExtensionSync = false;
        }
    }

    public void OnBinaryExtensionToggled()
    {
        if (_suppressBinaryExtensionSync) return;

        _updatingBinaryExtensionsFromItems = true;
        try
        {
            BinaryExtensions = string.Join(';', BinaryExtensionItems.Where(i => i.IsEnabled).Select(i => i.Extension));
        }
        finally
        {
            _updatingBinaryExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(BinaryExtensionsSummary));
    }

    // ── Archive (ZIP-like) extensions dropdown ────────────────────

    /// <summary>Observable collection of archive-extension items for the multi-select dropdown.</summary>
    public ObservableCollection<SkipExtensionItem> ArchiveExtensionItems { get; } = [];

    /// <summary>Summary label for the archive-extensions dropdown button.</summary>
    public string ArchiveExtensionsSummary
    {
        get
        {
            int enabled = ArchiveExtensionItems.Count(i => i.IsEnabled);
            int total = ArchiveExtensionItems.Count;
            return total == 0 ? "Archive ext: none" : $"Archive ext: {enabled}/{total}";
        }
    }

    public Microsoft.UI.Xaml.Visibility ArchiveExtensionsVisibility => SearchInsideArchives
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static readonly Dictionary<string, string> ArchiveExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = "Archives", ["jar"] = "Java", ["war"] = "Java", ["ear"] = "Java",
        ["nupkg"] = ".NET", ["vsix"] = ".NET",
        ["apk"] = "Android", ["aab"] = "Android", ["aar"] = "Android",
        ["appx"] = "Windows", ["msix"] = "Windows", ["appxbundle"] = "Windows", ["msixbundle"] = "Windows",
        ["docx"] = "Office", ["xlsx"] = "Office", ["pptx"] = "Office",
        ["odt"] = "OpenDoc", ["ods"] = "OpenDoc", ["odp"] = "OpenDoc",
        ["epub"] = "eBooks",
        ["whl"] = "Python",
        ["gz"] = "Compressed", ["tar"] = "Compressed", ["7z"] = "Compressed",
        ["rar"] = "Compressed", ["bz2"] = "Compressed", ["xz"] = "Compressed",
        ["iso"] = "Disk Images", ["cab"] = "Installers", ["msi"] = "Installers",
        ["tgz"] = "Compressed", ["tbz2"] = "Compressed", ["txz"] = "Compressed",
        ["zst"] = "Compressed", ["zstd"] = "Compressed", ["br"] = "Compressed",
        ["lz4"] = "Compressed", ["lzma"] = "Compressed",
    };

    private static string CategorizeArchiveExtension(string ext) =>
        ArchiveExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";

    private bool _suppressArchiveExtensionSync;
    private bool _updatingArchiveExtensionsFromItems;

    partial void OnArchiveExtensionsChanged(string value)
    {
        if (_suppressArchiveExtensionSync || _updatingArchiveExtensionsFromItems) return;
        SyncArchiveExtensionItems();
    }

    partial void OnSettingsArchiveExtensionsChanged(string value)
    {
        if (_suppressArchiveExtensionSync || _updatingArchiveExtensionsFromItems) return;
        SyncArchiveExtensionItems();
    }

    /// <summary>Rebuild the <see cref="ArchiveExtensionItems"/> collection from the current <see cref="ArchiveExtensions"/> string.</summary>
    public void SyncArchiveExtensionItems()
    {
        _suppressArchiveExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(ArchiveExtensions);
            var available = ParseExtensionSet(SettingsArchiveExtensions);
            foreach (var ext in enabled)
                available.Add(ext);

            ArchiveExtensionItems.Clear();

            var groups = available
                .GroupBy(CategorizeArchiveExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    ArchiveExtensionItems.Add(new SkipExtensionItem(ext, group.Key, enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(ArchiveExtensionsSummary));
        }
        finally
        {
            _suppressArchiveExtensionSync = false;
        }
    }

    /// <summary>Called when an archive-extension item is toggled. Removes unchecked items and persists.</summary>
    public void OnArchiveExtensionToggled()
    {
        if (_suppressArchiveExtensionSync) return;

        _updatingArchiveExtensionsFromItems = true;
        try
        {
            ArchiveExtensions = string.Join(';', ArchiveExtensionItems.Where(i => i.IsEnabled).Select(i => i.Extension));
        }
        finally
        {
            _updatingArchiveExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(ArchiveExtensionsSummary));
    }

    private string _globalHotkeyKey = HotkeyService.DefaultStartKey.ToString();
    public string GlobalHotkeyKey
    {
        get => _globalHotkeyKey;
        set
        {
            var normalized = HotkeyService.TryNormalizeLetter(value, out var key)
                ? key.ToString()
                : HotkeyService.DefaultStartKey.ToString();
            SetProperty(ref _globalHotkeyKey, normalized);
        }
    }

    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? FallbackReason { get; set; }
    [ObservableProperty] public partial int FilesScanned { get; set; }
    [ObservableProperty] public partial int TotalFiles { get; set; }

    // .yagu-session save/load progress (0.0..1.0 while busy).
    [ObservableProperty] public partial bool IsSessionBusy { get; set; }
    [ObservableProperty] public partial double SessionProgressPercent { get; set; }
    [ObservableProperty] public partial string SessionProgressText { get; set; } = string.Empty;

    public bool IsSessionIdle => !IsSessionBusy;
    partial void OnIsSessionBusyChanged(bool value) => OnPropertyChanged(nameof(IsSessionIdle));

    public string ProgressTooltip
    {
        get
        {
            if (TotalFiles <= 0) return "Waiting for file list…";
            double pct = (double)FilesScanned / TotalFiles * 100;
            return $"{pct:F1}% complete ({FilesScanned:N0} files out of {TotalFiles:N0} total files)";
        }
    }

    partial void OnFilesScannedChanged(int value) => OnPropertyChanged(nameof(ProgressTooltip));
    partial void OnTotalFilesChanged(int value) => OnPropertyChanged(nameof(ProgressTooltip));
    [ObservableProperty] public partial int MatchesFound { get; set; }
    [ObservableProperty] public partial int FilesSkipped { get; set; }
    [ObservableProperty] public partial int AccessDeniedCount { get; set; }
    [ObservableProperty] public partial bool Truncated { get; set; }
    [ObservableProperty] public partial bool Degraded { get; set; }
    [ObservableProperty] public partial string DegradedNoticeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilesPerSecondText { get; set; } = string.Empty;

    /// <summary>UTC time when the last search started.</summary>
    public DateTime SearchStartedUtc => _searchStartedUtc;
    /// <summary>Duration of the last completed search.</summary>
    public TimeSpan LastSearchElapsed => _lastSearchElapsed;
    /// <summary>Total bytes scanned in the last/current search.</summary>
    public long BytesScanned => _bytesScanned;

    /// <summary>Disk-backed store for evicted results. Null before first search.</summary>
    public ResultStore? ActiveResultStore => _resultStore;

    public event EventHandler? ResultGroupsChanging;

    public ObservableCollection<FileGroup> ResultGroups => _resultCollection.VisibleGroups;
    public BatchObservableCollection<object> ResultRows { get; } = new();
    public ObservableCollection<string> RecentDirectories { get; } = [];
    public ObservableCollection<string> DirectorySuggestions { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];

    public bool HasResults => ResultGroups.Count > 0;
    public bool ShowEmptyState => !IsSearching && ResultGroups.Count == 0;
    public bool HasFallbackReason => !string.IsNullOrEmpty(FallbackReason);
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);
    public int OtherSkippedCount => Math.Max(0, FilesSkipped - AccessDeniedCount);
    public Microsoft.UI.Xaml.Visibility MemoryPressureWarningVisibility =>
        ShowMemoryPressureWarningLabel && !string.IsNullOrWhiteSpace(DegradedNoticeText)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility StatsForNerdsVisibility =>
        ShowStatsForNerds
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility AutoScrollResultsCheckboxVisibility =>
        ShowAutoScrollResultsCheckbox
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    private void OnVisibleResultGroupsChanging(object? sender, EventArgs e)
        => ResultGroupsChanging?.Invoke(this, EventArgs.Empty);

    private void OnVisibleResultGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (GroupMode != GroupMode.None)
        {
            RebuildResultRows();
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                ResultRows.AppendRange(e.NewItems.Cast<object>().ToList());
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var item in e.OldItems)
                    ResultRows.Remove(item);
                break;
            default:
                RebuildResultRows();
                break;
        }
    }

    public void ToggleResultGroupExpansion(ResultGroupHeaderRow header)
    {
        _expandedResultGroupKeys[header.Key] = !header.IsExpanded;
        RebuildResultRows();
    }

    private readonly Dictionary<string, bool> _expandedResultGroupKeys = new(StringComparer.Ordinal);

    private void RebuildResultRows()
    {
        var rows = ResultRowProjection.BuildRows(ResultGroups, GroupMode, _expandedResultGroupKeys);
        ResultRows.ReplaceAll(rows);
    }

    private SkipBreakdown? _lastSkipBreakdown;
    private const string ExtensionExclusionSkipNote = "Files excluded by extension during discovery are filtered before counting and are not included in skipped counts.";

    /// <summary>Formatted tooltip showing a per-category breakdown of skipped files.</summary>
    public string SkipTooltip
    {
        get
        {
            var b = _lastSkipBreakdown;
            if (b is null || FilesSkipped == 0)
                return $"No files skipped{Environment.NewLine}{Environment.NewLine}{ExtensionExclusionSkipNote}";

            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Skipped files breakdown:");
            lines.AppendLine(ExtensionExclusionSkipNote);
            lines.AppendLine();
            if (b.GlobExcluded > 0)   lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  🚫  Glob exclusions       {b.GlobExcluded,8:N0}");
            if (b.GitignoreExcluded > 0) lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  🙈  .gitignore excluded   {b.GitignoreExcluded,8:N0}");
            if (b.Binary > 0)         lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  🔒  Binary files          {b.Binary,8:N0}");
            if (b.ByExtension > 0)    lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  📄  Scanner extension skips {b.ByExtension,8:N0}");
            if (b.TooLarge > 0)       lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  📏  Too large             {b.TooLarge,8:N0}");
            if (b.AccessDenied > 0)   lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  🔐  Access denied         {b.AccessDenied,8:N0}");
            if (b.Directories > 0)    lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  📁  Inaccessible dirs     {b.Directories,8:N0}");
            if (b.IOError > 0)        lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  ⚠️  I/O errors            {b.IOError,8:N0}");
            if (b.NotFound > 0)       lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  ❓  Not found             {b.NotFound,8:N0}");
            if (b.Encoding > 0)       lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  🔤  Encoding errors       {b.Encoding,8:N0}");
            if (b.Other > 0)          lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  ❔  Other                 {b.Other,8:N0}");

            return lines.ToString().TrimEnd();
        }
    }

    private void UpdateSkipBreakdown(SkipBreakdown? breakdown)
    {
        _lastSkipBreakdown = breakdown;
        OnPropertyChanged(nameof(SkipTooltip));
    }

    partial void OnFallbackReasonChanged(string? value) => OnPropertyChanged(nameof(HasFallbackReason));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorText));
    partial void OnDegradedNoticeTextChanged(string value) => OnPropertyChanged(nameof(MemoryPressureWarningVisibility));
    partial void OnShowMemoryPressureWarningLabelChanged(bool value) => OnPropertyChanged(nameof(MemoryPressureWarningVisibility));
    partial void OnShowStatsForNerdsChanged(bool value) => OnPropertyChanged(nameof(StatsForNerdsVisibility));
    partial void OnShowAutoScrollResultsCheckboxChanged(bool value) => OnPropertyChanged(nameof(AutoScrollResultsCheckboxVisibility));
    partial void OnFilesSkippedChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); }
    partial void OnAccessDeniedCountChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); }
    partial void OnSortModeIndexChanged(int value)
    {
        if (_updatingSortCriteria) return;
        SetSingleSortCriterion(value, SortDirectionIndex);
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }

    partial void OnSortDirectionIndexChanged(int value)
    {
        if (_updatingSortCriteria) return;
        SetSingleSortCriterion(SortModeIndex, value);
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }
    partial void OnGroupModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GroupMode));
        OnPropertyChanged(nameof(GroupModeLabel));
        OnPropertyChanged(nameof(GroupSortDirectionLabel));
        ApplySortAndFilter();
    }
    partial void OnGroupSortDirectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GroupSortDirectionLabel));
        ApplySortAndFilter();
    }
    partial void OnDateRangeFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DateRangeFilter));
        OnPropertyChanged(nameof(DateRangeFilterLabel));
        ApplySortAndFilter();
    }
    partial void OnSearchInsideArchivesChanged(bool value) => OnPropertyChanged(nameof(ArchiveExtensionsVisibility));
    partial void OnIncludeGlobsChanged(string value) => ApplySortAndFilter();
    partial void OnExcludeGlobsChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _clearedDefaultExcludeForRegexMode = false;
        ApplySortAndFilter();
    }
    partial void OnIncludeFilterModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IncludeFilterMode));
        OnPropertyChanged(nameof(IncludeFilterPlaceholder));
        ApplySortAndFilter();
    }
    partial void OnExcludeFilterModeIndexChanged(int value)
    {
        if (ExcludeFilterMode == FilterPatternMode.Regex && IsDefaultExcludeGlobs(ExcludeGlobs))
        {
            _clearedDefaultExcludeForRegexMode = true;
            ExcludeGlobs = string.Empty;
        }
        else if (ExcludeFilterMode == FilterPatternMode.GlobPath
            && _clearedDefaultExcludeForRegexMode
            && string.IsNullOrWhiteSpace(ExcludeGlobs))
        {
            ExcludeGlobs = AppSettings.DefaultExcludeGlobs;
        }

        OnPropertyChanged(nameof(ExcludeFilterMode));
        OnPropertyChanged(nameof(ExcludeFilterPlaceholder));
        ApplySortAndFilter();
    }
    partial void OnMinFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MinFileSizeMB));
    }
    partial void OnMaxFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MaxFileSizeMB));
    }
    partial void OnDefaultMinFileSizeBytesChanged(long value) => OnPropertyChanged(nameof(DefaultMinFileSizeMB));
    partial void OnDefaultMaxFileSizeBytesChanged(long value) => OnPropertyChanged(nameof(DefaultMaxFileSizeMB));
    partial void OnFileLogLevelIndexChanged(int value)
    {
        LogService.Instance.FileLevel = (LogLevel)value;
        LogService.Instance.Info("Settings", $"File log level changed to {(LogLevel)value}");
    }
    partial void OnConsoleLogLevelIndexChanged(int value)
    {
        LogService.Instance.ConsoleLevel = (LogLevel)value;
        LogService.Instance.Info("Settings", $"Console log level changed to {(LogLevel)value}");
    }

    partial void OnFileListerBackendIndexChanged(int value)
    {
        var backend = (FileListerBackend)value;
        FileLister.Backend = backend;
        LogService.Instance.Info("Settings", $"FileLister backend set to {backend}");
    }

    [RelayCommand]
    public async Task StartSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            ErrorText = "Choose a directory to search.";
            return;
        }
        if (!System.IO.Directory.Exists(Directory))
        {
            ErrorText = $"Directory does not exist: {Directory}";
            return;
        }
        if (string.IsNullOrEmpty(Query))
        {
            ErrorText = "Enter a search query.";
            return;
        }

        // Validate: skip extensions must not contradict archive extensions when archive search is on.
        if (SearchInsideArchives)
        {
            var skipSet = BuildEffectiveSkipExtensionSet();
            var archiveSet = ParseExtensionSet(ArchiveExtensions);
            var conflicts = skipSet.Intersect(archiveSet, StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            if (conflicts.Count > 0)
            {
                ErrorText = $"Conflicting extensions found in both Skip and Archive lists: {string.Join(", ", conflicts.Select(e => $".{e}"))}. " +
                            "Remove them from the Skip list or the Archive list to proceed.";
                return;
            }
        }

        long effectiveMinFileSizeBytes = MinFileSizeBytes;
        long effectiveMaxFileSizeBytes = MaxFileSizeBytes;
        if (effectiveMinFileSizeBytes > 0 && effectiveMaxFileSizeBytes > 0 && effectiveMinFileSizeBytes > effectiveMaxFileSizeBytes)
        {
            ErrorText = "Minimum file size cannot be larger than maximum file size.";
            return;
        }

        if (IsDateRangeInvalid(CreatedAfterDate, CreatedBeforeDate))
        {
            ErrorText = "Created after date cannot be later than created before date.";
            return;
        }

        if (IsDateRangeInvalid(ModifiedAfterDate, ModifiedBeforeDate))
        {
            ErrorText = "Modified after date cannot be later than modified before date.";
            return;
        }

        int runId = System.Threading.Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);

        await _searchLifecycleGate.WaitAsync();

        CancellationTokenSource? cts = null;
        Task? lowDiskMonitorTask = null;
        try
        {
            if (runId != Volatile.Read(ref _searchRunId))
                return;

            ResetStateForNewSearch();

            SettingsService.PushRecent(_settings.RecentDirectories, Directory, MaxRecentItems);
            SettingsService.PushRecent(_settings.SearchHistory, Query, MaxRecentItems);
            SyncRecent();
            await PersistSettingsAsync();

            var effectiveSkipExtensions = BuildEffectiveSkipExtensionSet();

            var options = new SearchOptions
            {
                Directory = Directory,
                Query = Query,
                CaseSensitive = CaseSensitive,
                UseRegex = UseRegex,
                ExactMatch = ExactMatch,
                ContextLines = ContextLines,
                SearchMode = (SearchMode)SearchModeIndex,
                IncludeGlobs = SplitFilterPatterns(IncludeGlobs, IncludeFilterMode),
                ExcludeGlobs = SplitFilterPatterns(ExcludeGlobs, ExcludeFilterMode),
                IncludeFilterMode = IncludeFilterMode,
                ExcludeFilterMode = ExcludeFilterMode,
                MinFileSizeBytes = effectiveMinFileSizeBytes,
                MaxFileSizeBytes = effectiveMaxFileSizeBytes,
                CreatedAfterDate = CreatedAfterDate,
                CreatedBeforeDate = CreatedBeforeDate,
                ModifiedAfterDate = ModifiedAfterDate,
                ModifiedBeforeDate = ModifiedBeforeDate,
                MaxResults = MaxResults,
                SkipBinary = SkipBinary,
                ObeyGitignore = ObeyGitignore,
                GitignoreTakesPrecedence = GitignoreTakesPrecedence,
                SkipExtensions = effectiveSkipExtensions,
                SearchInsideArchives = SearchInsideArchives,
                ArchiveExtensions = ParseDottedExtensionSet(ArchiveExtensions),
                MaxDegreeOfParallelism = ResolveParallelism(_sessionParallelismOverrideIndex ?? ParallelismIndex),
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
                ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths,
                MaxSearchDepth = double.IsNaN(MaxSearchDepth) ? 0 : (int)MaxSearchDepth,
                DegradedResultStore = _resultStore,
            };

            cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;
            lowDiskMonitorTask = StartLowDiskSpaceMonitor(runId, cts, _resultStore);
            LogService.Instance.Warning("Search", $"Starting search #{runId}: query='{Query}', dir='{Directory}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}");

            // Yield to the UI message pump periodically so the app stays responsive
            // when the events channel is draining many buffered items synchronously.
            // Without this, the await foreach completes synchronously for thousands of
            // already-buffered items, starving the WinUI message pump and freezing the UI.
            long yieldTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long yieldIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 60; // ~16ms (one frame)

            // UI consumer diagnostics
            long uiEventsReceived = 0;
            long uiMatchesReceived = 0;
            long uiYieldCount = 0;
            long uiLastLogTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long uiLastStatusRefreshTicks = uiLastLogTicks;
            const long UiLogIntervalSec = 10;
            long uiStatusRefreshIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 4;
            var uiEventSw = new System.Diagnostics.Stopwatch();

            void RefreshStatusFromReceivedMatches(bool force = false)
            {
                long statusNow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!force && statusNow - uiLastStatusRefreshTicks < uiStatusRefreshIntervalTicks)
                    return;

                uiLastStatusRefreshTicks = statusNow;
                int receivedMatches = ClampMatchCount(uiMatchesReceived);
                if (receivedMatches > MatchesFound)
                    MatchesFound = receivedMatches;
                UpdateFilesPerSecond();
            }

            await foreach (var evt in _search.SearchAsync(options, token).ConfigureAwait(true))
            {
                uiEventsReceived++;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - yieldTimestamp >= yieldIntervalTicks)
                {
                    uiYieldCount++;
                    await Task.Delay(1, token).ConfigureAwait(true);
                    yieldTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (!IsCurrentSearch(runId, cts))
                {
                    LogService.Instance.Warning("Search", $"Ignoring stale search #{runId} event after a newer search started");
                    break;
                }

                // Periodic UI consumer throughput log
                now = System.Diagnostics.Stopwatch.GetTimestamp();
                if ((now - uiLastLogTicks) >= System.Diagnostics.Stopwatch.Frequency * UiLogIntervalSec)
                {
                    uiLastLogTicks = now;
                    LogService.Instance.Warning("UIConsumer",
                        $"Events received={uiEventsReceived:N0}, matchesReceived={uiMatchesReceived:N0}, " +
                        $"groups={_resultCollection.AllGroups.Count:N0}, yields={uiYieldCount:N0}, " +
                        $"degraded={Degraded}, diskEvicted={_resultStore?.EvictedCount ?? 0:N0}");
                }

                switch (evt)
                {
                    case SearchEvent.Fallback f:
                        FallbackReason = f.Reason;
                        break;
                    case SearchEvent.DiscoveryComplete d:
                        TotalFiles = d.TotalFiles;
                        StatusText = $"Searching {d.TotalFiles:N0} files…";
                        break;
                    case SearchEvent.Match m:
                        uiMatchesReceived++;
                        await AddMatchAsync(m.Result, token).ConfigureAwait(true);
                        RefreshStatusFromReceivedMatches();
                        break;
                    case SearchEvent.MatchBatch mb:
                        // Drain the whole batch under a single dispatcher tick. AddMatch is
                        // O(1) per result; doing them in a tight loop keeps allocations and
                        // PropertyChanged churn from each ResultGroups.Add to the absolute
                        // minimum. The list itself was produced by the discovery thread —
                        // we own it now and don't need a copy.
                        uiMatchesReceived += mb.Results.Count;
                        uiEventSw.Restart();
                        await AddMatchesAsync(mb.Results, token).ConfigureAwait(true);
                        uiEventSw.Stop();
                        RefreshStatusFromReceivedMatches();
                        if (uiEventSw.ElapsedMilliseconds > 200)
                        {
                            LogService.Instance.Warning("UIConsumer",
                                $"Slow AddMatches: {mb.Results.Count} results took {uiEventSw.ElapsedMilliseconds}ms " +
                                $"(groups={_resultCollection.AllGroups.Count:N0})");
                        }
                        break;
                    case SearchEvent.Progress p:
                        FilesScanned = p.Snapshot.FilesScanned;
                        TotalFiles = p.Snapshot.TotalFiles;
                        MatchesFound = Math.Max(p.Snapshot.MatchesFound, ClampMatchCount(uiMatchesReceived));
                        FilesSkipped = p.Snapshot.FilesSkipped;
                        AccessDeniedCount = p.Snapshot.AccessDenied;
                        _bytesScanned = p.Snapshot.BytesScanned;
                        UpdateSkipBreakdown(p.Snapshot.SkipReasons);
                        UpdateFilesPerSecond();
                        break;
                    case SearchEvent.SearchError e:
                        ErrorText = e.Message;
                        break;
                    case SearchEvent.MemoryPressure mp:
                        DegradedNoticeText = "Memory pressure — paging results to disk";
                        Degraded = true;
                        LogService.Instance.Warning("ViewModel", $"Memory pressure event received — starting async eviction ({_resultCollection.AllGroups.Count:N0} groups, {MatchesFound:N0} matches)");
                        // Fire-and-forget from the UI thread: the background task may wait
                        // for ResultStore queue space so existing payloads do not pile up
                        // in RAM while the disk writer catches up.
                        _ = Task.Run(() =>
                        {
                            var evictSw = System.Diagnostics.Stopwatch.StartNew();
                            int enqueued = EvictAllResults();
                            evictSw.Stop();
                            LogService.Instance.Warning("ViewModel", $"Eviction enqueued {enqueued:N0} results in {evictSw.ElapsedMilliseconds}ms (drain continues in background)");

                            // Acknowledge immediately so SearchService leaves eviction-in-flight
                            // state and can fire the next pressure cycle if memory is still high.
                            try { mp.AcknowledgeEviction(enqueued); }
                            catch (Exception ex) { LogService.Instance.Warning("ViewModel", "AcknowledgeEviction threw", ex); }

                            // Wait for the background drain to flush bytes to disk before
                            // triggering the compacting GC — otherwise we'd compact while
                            // the match-line/context strings are still rooted by the channel.
                            try { _resultStore?.Drain(); }
                            catch (Exception ex) { LogService.Instance.Warning("ViewModel", "ResultStore drain failed", ex); }

                            if (IsSearching)
                                SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(3));
                            else
                                CollectPostEvictionIfDue();
                        });
                        break;
                    case SearchEvent.MemoryPressureRelieved relieved:
                        Degraded = false;
                        DegradedNoticeText = string.Empty;
                        UpdateFilesPerSecond();
                        LogService.Instance.Warning("ViewModel", $"Memory pressure relieved — leaving memory-saving mode ({relieved.Diagnostics})");
                        break;
                    case SearchEvent.Completed c:
                        LogService.Instance.Warning("UIConsumer",
                            $"Search #{runId} completed: uiEvents={uiEventsReceived:N0}, uiMatches={uiMatchesReceived:N0}, " +
                            $"groups={_resultCollection.AllGroups.Count:N0}, yields={uiYieldCount:N0}, " +
                            $"diskEvicted={_resultStore?.EvictedCount ?? 0:N0}");
                        var completedElapsed = StopSearchTimer();
                        int actualTotalMatches = Math.Max(c.Summary.TotalMatches, ClampMatchCount(uiMatchesReceived));
                        FilesScanned = c.Summary.FilesScanned;
                        TotalFiles = c.Summary.TotalFiles;
                        MatchesFound = actualTotalMatches;
                        FilesSkipped = c.Summary.FilesSkipped;
                        AccessDeniedCount = c.Summary.SkipReasons?.AccessDenied ?? 0;
                        UpdateSkipBreakdown(c.Summary.SkipReasons);
                        Truncated = c.Summary.Truncated;
                        Degraded = c.Summary.Degraded;
                        // Use the actual file-group count so the status bar matches
                        // the clipboard export. Filename-only matches create UI
                        // groups but aren't tracked by the engine's filesWithMatches
                        // counter when content search is also active.
                        var actualFileCount = Math.Max(c.Summary.FilesWithMatches, _resultCollection.AllGroups.Count);
                        var displaySummary = c.Summary with { TotalMatches = actualTotalMatches, FilesWithMatches = actualFileCount };
                        StatusText = BuildCompletionStatus(displaySummary, completedElapsed);
                        ApplySortAndFilter();
                        ShowSearchCompleteToast(displaySummary, completedElapsed);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                var cancelledElapsed = StopSearchTimer();
                if (_lowDiskSpaceCancellation is { } lowDiskSpace)
                {
                    var message = LowDiskSpaceMonitor.BuildTerminationMessage(lowDiskSpace);
                    StatusText = message;
                    ErrorText = message;
                    LogService.Instance.Warning("Search", $"Search #{runId} terminated because temp-file drive {lowDiskSpace.DriveDisplayName} is {lowDiskSpace.UsedPercent:F1}% full");
                }
                else
                {
                    StatusText = BuildCancelledStatus(cancelledElapsed);
                    LogService.Instance.Info("Search", $"Search #{runId} cancelled");
                }
                DegradedNoticeText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                StopSearchTimer();
                ErrorText = $"Search failed: {ex.Message}";
                LogService.Instance.Critical("Search", $"Search #{runId} failed", ex);
            }
        }
        finally
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                IsSearching = false;
                FilesPerSecondText = string.Empty;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
                _cts = null;
            }

            try { cts?.Cancel(); } catch { }
            if (lowDiskMonitorTask is not null)
                await lowDiskMonitorTask.ConfigureAwait(true);

            cts?.Dispose();
            _searchLifecycleGate.Release();
        }
    }

    private void CancelPreviousSearchForNewRun(int runId)
    {
        var previous = _cts;
        if (previous is null) return;

        try
        {
            StatusText = "Cleaning up previous search…";
            previous.Cancel();
            LogService.Instance.Info("Search", $"Cancelling previous search before starting search #{runId}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Search", "Previous search cleanup cancellation failed", ex);
        }
    }

    private void ResetStateForNewSearch()
    {
        _cts = null;
        _lastSearchSortRefreshTicks = 0;
        _searchSortRefreshQueued = false;
        _searchSortRefreshIntervalSec = SearchSortRefreshIntervalBaseSec;

        // Cancel pending metadata tasks first so fire-and-forget closures
        // release their FileGroup references promptly.
        _metadataCts.Cancel();
        _metadataCts.Dispose();
        _metadataCts = new CancellationTokenSource();

        _expandedResultGroupKeys.Clear();
        _resultCollection.Clear();
        RebuildResultRows();
        FileMetadataCache.Clear();

        _resultStore?.Dispose();
        _resultStore = CreateResultStore();

        // Reclaim the previous search's result graph on the threadpool so the
        // UI thread isn't blocked by a full compacting GC.
        // Use blocking: false so search workers aren't suspended for seconds
        // when the heap is large (e.g. millions of evicted result shells).
        _ = Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            GC.WaitForPendingFinalizers();
        });

        ErrorText = null;
        FallbackReason = null;
        FilesScanned = 0;
        TotalFiles = 0;
        MatchesFound = 0;
        FilesSkipped = 0;
        AccessDeniedCount = 0;
        FilesPerSecondText = string.Empty;
        UpdateSkipBreakdown(null);
        Truncated = false;
        Degraded = false;
        DegradedNoticeText = string.Empty;
        _lowDiskSpaceCancellation = null;
        IsSearching = true;
        _bytesScanned = 0;
        _prevBytesScanned = 0;
        _prevFilesScanned = 0;
        _prevSampleTime = 0;
        _prevDisplayTime = 0;
        _prevDisplayFiles = 0;
        _prevDisplayBytes = 0;
        _instantFilesPerSec = 0;
        _instantMbPerSec = 0;
        ThroughputSamples.Clear();
        _searchStartedUtc = DateTime.UtcNow;
        _searchTimer = System.Diagnostics.Stopwatch.StartNew();
        StatusText = "Searching…";

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private bool IsCurrentSearch(int runId, CancellationTokenSource cts) =>
        runId == Volatile.Read(ref _searchRunId) && ReferenceEquals(_cts, cts);

    private Task StartLowDiskSpaceMonitor(int runId, CancellationTokenSource cts, ResultStore? resultStore)
    {
        var tempFilePath = resultStore?.TempFilePath;
        if (string.IsNullOrWhiteSpace(tempFilePath))
            return Task.CompletedTask;

        var fullThreshold = LowDiskSpaceMonitor.PercentToThreshold(LowDiskSpaceWarningPercent);

        return LowDiskSpaceMonitor.StartAsync(
            tempFilePath,
            fullThreshold,
            LowDiskSpaceMonitor.DefaultCheckInterval,
            lowDiskSpace =>
        {
            if (!IsCurrentSearch(runId, cts))
                return;

            _lowDiskSpaceCancellation = lowDiskSpace;
            try { cts.Cancel(); }
            catch (Exception ex) { LogService.Instance.Warning("Search", "Low disk-space cancellation failed", ex); }
        }, cts.Token);
    }

    private ResultStore CreateResultStore()
    {
        string? tempDir = ChooseResultStoreTempDir();
        try
        {
            return new ResultStore(tempDir);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(tempDir))
        {
            LogService.Instance.Warning("ResultStore", $"Could not create result store in '{tempDir}', falling back to Windows temp", ex);
            return new ResultStore();
        }
    }

    /// <summary>Pick the configured temp directory for disk-backed search results.</summary>
    private string? ChooseResultStoreTempDir()
    {
        // Override via environment variable (e.g. for profiling on the same fast SSD)
        string? envOverride = Environment.GetEnvironmentVariable("YAGU_RESULTSTORE_TEMP");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        if (!string.IsNullOrWhiteSpace(SearchResultTempDirectory))
            return SearchResultTempDirectory;

        return Path.GetTempPath();
    }

    private string BuildProgressStatus(SearchProgress progress)
    {
        var prefix = Degraded ? "Searching (memory-saving mode)" : "Searching";
        if (progress.TotalFiles > 0)
        {
            return $"{prefix} {progress.FilesScanned:N0}/{progress.TotalFiles:N0} files... {progress.MatchesFound:N0} matches";
        }

        return $"{prefix}... {progress.FilesScanned:N0} files scanned, {progress.MatchesFound:N0} matches";
    }

    private string BuildCurrentSearchStatus()
    {
        var prefix = Degraded ? "Searching (memory-saving mode)" : "Searching";
        if (TotalFiles > 0)
        {
            return $"{prefix} {FilesScanned:N0}/{TotalFiles:N0} files... {MatchesFound:N0} matches";
        }

        return $"{prefix}... {FilesScanned:N0} files scanned, {MatchesFound:N0} matches";
    }

    private static string BuildMemoryPressureStatus(SearchEvent.MemoryPressure memoryPressure)
    {
        return "Memory pressure high; paging Yagu results to disk and continuing in memory-saving mode...";
    }

    private TimeSpan StopSearchTimer()
    {
        var timer = _searchTimer;
        if (timer is null)
            return TimeSpan.Zero;

        timer.Stop();
        _searchTimer = null;
        _lastSearchElapsed = timer.Elapsed;
        return timer.Elapsed;
    }

    private string BuildCancelledStatus(TimeSpan elapsed)
    {
        var time = FormatElapsed(elapsed);
        var rate = FormatThroughput(FilesScanned, _bytesScanned, elapsed);
        return $"Cancelled — {MatchesFound:N0} matches, {FilesScanned:N0} files processed ({time}, {rate})";
    }

    [RelayCommand]
    public Task CancelAsync()
    {
        try { _cts?.Cancel(); } catch (Exception ex) { LogService.Instance.Warning("Search", "Cancel failed", ex); }
        return Task.CompletedTask;
    }

    [RelayCommand]
    public void OpenInEditor(SearchResult? result)
    {
        if (result is null) return;
        _editor.Command = EditorCommand;
        _editor.Open(result.FilePath, result.LineNumber);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void OpenContainingFolder(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenContainingFolder(result.FilePath);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void OpenTerminalHere(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenTerminalAt(result.FilePath);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void CopyFilePath(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.FilePath);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void CopyMatchLine(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.MatchLine);
    }

    private static void SetClipboard(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { LogService.Instance.Verbose("Clipboard", "Clipboard unavailable", ex); }
    }

    private async Task AddMatchAsync(SearchResult result, CancellationToken cancellationToken)
    {
        if (Degraded && _resultStore is not null && !result.IsEvicted)
            await EvictNewResultsBeforeUiAsync([result], cancellationToken).ConfigureAwait(true);

        bool resultAvailabilityChanged = AddMatchCore(result, evictedResultWriter: null);

        QueueSearchSortRefreshIfDue();

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();
    }

    private async Task AddMatchesAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken)
    {
        if (Degraded && _resultStore is not null && ContainsInMemoryPayload(results))
            await EvictNewResultsBeforeUiAsync(results, cancellationToken).ConfigureAwait(true);

        bool resultAvailabilityChanged = _resultCollection.AddRange(
            results,
            InitializeResultGroup,
            evictNewResults: false,
            resultStore: null);

        QueueSearchSortRefreshIfDue();

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();
    }

    private static bool ContainsInMemoryPayload(IReadOnlyList<SearchResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (!results[i].IsEvicted)
                return true;
        }

        return false;
    }

    private async Task EvictNewResultsBeforeUiAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken)
    {
        if (_resultStore is null || results.Count == 0)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int evicted = await Task.Run(() => _resultStore.EvictManyNow(results), cancellationToken).ConfigureAwait(true);
        sw.Stop();
        if (sw.ElapsedMilliseconds >= 500)
        {
            LogService.Instance.Warning("ViewModel",
                $"Pre-evicted {evicted:N0}/{results.Count:N0} new result payload(s) before UI insertion in {sw.ElapsedMilliseconds}ms");
        }
    }

    private bool AddMatchCore(
        SearchResult result,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictedResultWriter)
    {
        // FilePath comes from FileLister and is already a full path on Windows.
        // Avoiding Path.GetFullPath here removes a per-match string allocation +
        // PInvoke that was running on the UI dispatcher.
        var path = result.FilePath;
        bool watched = Yagu.Services.FileWatchDiagnostics.IsWatched(path);
        if (watched)
            Yagu.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-ENTER", -1, $"line={result.LineNumber} groups={_resultCollection.AllGroups.Count}");

        bool resultAvailabilityChanged = _resultCollection.Add(
            result,
            InitializeResultGroup,
            evictNewResult: Degraded && evictedResultWriter is not null,
            evictedResultWriter);

        if (watched)
            Yagu.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-EXIT", -1, $"groupCount={_resultCollection.AllGroups.Count} visibleGroups={ResultGroups.Count}");
        // MatchesFound is updated via throttled Progress / Completed events to avoid
        // pumping a PropertyChanged for every single result on huge searches.
        return resultAvailabilityChanged;
    }

    private void InitializeResultGroup(FileGroup group)
    {
        // Load metadata on a worker thread — the FileInfo syscall on the UI
        // dispatcher was a measurable stall on searches with thousands of
        // distinct files.
        group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()), OnResultGroupMetadataLoaded, _metadataCts.Token);
    }

    private void OnResultGroupMetadataLoaded(FileGroup group)
    {
        if (!IsMetadataSensitiveView)
            return;

        if (_metadataSortFilterRefreshQueued)
            return;

        _metadataSortFilterRefreshQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _metadataSortFilterRefreshQueued = false;
            ApplySortAndFilter();
        });
    }

    private bool IsMetadataSensitiveView =>
        DateRangeFilter != DateRangeFilter.None
        || GroupMode is GroupMode.DateRangeModified or GroupMode.DateRangeCreated or GroupMode.DateRangeModifiedCreated
        || GroupMode == GroupMode.FileSize
        || SortModeIndex is 2 or 3;

    private void QueueSearchSortRefreshIfDue()
    {
        int groupCount = _resultCollection.AllGroups.Count;
        // Note: intentionally allow refresh while Degraded (memory-pressure paging mode).
        // Adaptive backoff below handles cost on slow passes; skipping outright would
        // freeze the visible sort/group ordering for the remainder of large searches.
        if (!IsSearching || _searchSortRefreshQueued || groupCount < 2)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long intervalTicks = (long)(System.Diagnostics.Stopwatch.Frequency * _searchSortRefreshIntervalSec);
        if (_lastSearchSortRefreshTicks != 0 && now - _lastSearchSortRefreshTicks < intervalTicks)
            return;

        _searchSortRefreshQueued = true;
        _lastSearchSortRefreshTicks = now;

        if (!_dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            _searchSortRefreshQueued = false;
            int currentGroupCount = _resultCollection.AllGroups.Count;
            if (!IsSearching || currentGroupCount < 2)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ApplySortAndFilter();
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("ViewModel", $"Periodic in-search sort refresh threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            sw.Stop();
            LogService.Instance.Verbose("ViewModel",
                $"Periodic in-search sort refresh: {currentGroupCount:N0} group(s) in {sw.ElapsedMilliseconds}ms (degraded={Degraded}, nextInterval={_searchSortRefreshIntervalSec:F1}s)");

            // Adaptive backoff: if the pass was slow, double the interval (capped); if fast, halve it back toward base.
            if (sw.ElapsedMilliseconds >= SearchSortRefreshSlowBudgetMs)
            {
                _searchSortRefreshIntervalSec = Math.Min(SearchSortRefreshIntervalMaxSec, _searchSortRefreshIntervalSec * 2.0);
            }
            else if (sw.ElapsedMilliseconds < SearchSortRefreshSlowBudgetMs / 2 && _searchSortRefreshIntervalSec > SearchSortRefreshIntervalBaseSec)
            {
                _searchSortRefreshIntervalSec = Math.Max(SearchSortRefreshIntervalBaseSec, _searchSortRefreshIntervalSec / 2.0);
            }
        }))
        {
            _searchSortRefreshQueued = false;
        }
    }

    private void NotifyResultAvailabilityChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Evict all in-memory results to the disk-backed store to free memory.</summary>
    /// <returns>The number of results actually evicted.</returns>
    private int EvictAllResults()
    {
        int evicted = _resultCollection.EvictAll(_resultStore);
        LogService.Instance.Info("ViewModel", $"Evicted {evicted:N0} results to disk ({_resultStore?.EvictedCount ?? 0:N0} total on disk)");
        // GC is now triggered by the worker threads after the eviction signal,
        // keeping the UI thread responsive.
        return evicted;
    }

    private static void CollectPostEvictionIfDue()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastPostEvictionCompactingGcTicks);
        if (last != 0)
        {
            double secondsSinceLast = (double)(now - last) / System.Diagnostics.Stopwatch.Frequency;
            if (secondsSinceLast < PostEvictionCompactingGcCooldown.TotalSeconds)
                return;
        }

        if (Interlocked.CompareExchange(ref s_postEvictionCompactingGcInFlight, 1, 0) != 0)
            return;

        var gcStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("ViewModel", "Post-eviction compacting GC failed", ex);
        }
        finally
        {
            gcStopwatch.Stop();
            Volatile.Write(ref s_lastPostEvictionCompactingGcTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref s_postEvictionCompactingGcInFlight, 0);

            if (gcStopwatch.ElapsedMilliseconds >= 500)
                LogService.Instance.Warning("ViewModel", $"Post-eviction compacting GC took {gcStopwatch.ElapsedMilliseconds:N0}ms");
            else
                LogService.Instance.Info("ViewModel", $"Post-eviction compacting GC took {gcStopwatch.ElapsedMilliseconds:N0}ms");
        }
    }

    /// <summary>
    /// Clear all search results, dispose the disk-backed temp store,
    /// and perform a compacting GC.
    /// </summary>
    public async Task ClearResultsAsync()
    {
        if (IsSearching)
            await CancelAsync();

        _resultCollection.Clear();
        FileMetadataCache.Clear();

        var oldStore = _resultStore;
        _resultStore = null;

        MatchesFound = 0;
        FilesScanned = 0;
        TotalFiles = 0;
        FilesSkipped = 0;
        AccessDeniedCount = 0;
        ErrorText = null;
        FallbackReason = null;
        Truncated = false;
        Degraded = false;
        DegradedNoticeText = string.Empty;
        FilesPerSecondText = string.Empty;
        StatusText = string.Empty;
        ThroughputSamples.Clear();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));

        // Dispose the old store (deletes temp file) and GC on the threadpool
        // so the UI stays responsive.
        await Task.Run(() =>
        {
            oldStore?.Dispose();

            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }).ConfigureAwait(true);
    }

    /// <summary>Hydrate an evicted result from disk so its full data is available.</summary>
    public void HydrateResult(SearchResult result)
    {
        if (result.IsEvicted && _resultStore is not null)
        {
            try
            {
                result.Hydrate(_resultStore);
            }
            catch (Exception ex) when (ex is EndOfStreamException or FormatException or InvalidOperationException or ObjectDisposedException)
            {
                LogService.Instance.Warning("ViewModel", $"Could not hydrate result at offset {result.DiskOffset}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hydrate multiple evicted results in a single batched read, minimizing lock contention.
    /// </summary>
    public void HydrateResults(IReadOnlyList<SearchResult> results)
    {
        ApplyHydrationPayloads(ReadHydrationPayloads(results));
    }

    /// <summary>
    /// Read evicted result payloads from disk without mutating UI-bound SearchResult objects.
    /// Safe to call from a worker thread.
    /// </summary>
    public IReadOnlyList<HydrationPayload> ReadHydrationPayloads(IReadOnlyList<SearchResult> results)
    {
        if (_resultStore is null || results.Count == 0) return Array.Empty<HydrationPayload>();

        // Collect offsets for evicted items
        long[] offsets = new long[results.Count];
        int evictedCount = 0;
        int[] evictedIndices = new int[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].IsEvicted)
            {
                offsets[evictedCount] = results[i].DiskOffset;
                evictedIndices[evictedCount] = i;
                evictedCount++;
            }
        }
        if (evictedCount == 0) return Array.Empty<HydrationPayload>();

        try
        {
            var readResults = _resultStore.ReadBatch(offsets.AsSpan(0, evictedCount));
            var payloads = new List<HydrationPayload>(evictedCount);
            for (int i = 0; i < evictedCount; i++)
            {
                var data = readResults[i];
                if (data is null) continue;
                var (ml, cb, ca) = data.Value;
                payloads.Add(new HydrationPayload(results[evictedIndices[i]], ml, cb, ca));
            }
            return payloads;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            LogService.Instance.Warning("ViewModel", $"Batch hydration failed: {ex.Message}");
            return Array.Empty<HydrationPayload>();
        }
    }

    /// <summary>Apply hydrated payloads to SearchResult objects. Must run on the UI thread.</summary>
    public static void ApplyHydrationPayloads(IEnumerable<HydrationPayload> payloads)
    {
        foreach (var payload in payloads)
        {
            payload.Result.HydrateFrom(payload.MatchLine, payload.ContextBefore, payload.ContextAfter);
        }
    }

    /// <summary>
    /// Re-scan a file's content against the current query and update the result list.
    /// Removes matches that no longer exist and updates surviving match text/positions.
    /// </summary>
    /// <param name="filePath">The saved file path.</param>
    /// <param name="savedText">The text that was written to disk.</param>
    /// <returns>True if the file group still has matches; false if it was removed entirely.</returns>
    public bool RevalidateFileResults(string filePath, string savedText)
    {
        var group = _resultCollection.FindGroup(filePath);
        if (group is null) return false;

        // Build the same matcher the search engine uses.
        var query = Query;
        if (string.IsNullOrEmpty(query)) return group.Count > 0;

        System.Text.RegularExpressions.Regex? regex = null;
        string? literal = null;
        StringComparison literalComparison = StringComparison.OrdinalIgnoreCase;

        if (UseRegex)
        {
            var regexOptions = System.Text.RegularExpressions.RegexOptions.Multiline;
            if (!CaseSensitive) regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            try { regex = new System.Text.RegularExpressions.Regex(query, regexOptions, TimeSpan.FromSeconds(5)); }
            catch { return group.Count > 0; } // invalid regex — don't remove anything
        }
        else
        {
            literal = query;
            literalComparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        }

        // Split saved text into lines.
        var lines = savedText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
                lines[i] = lines[i][..^1];
        }

        int contextLineCount = ContextLines;

        // Build new results from the saved content.
        var newResults = new List<SearchResult>();
        for (int i = 0; i < lines.Length; i++)
        {
            var matches = ContentSearcher.FindMatches(lines[i], regex, literal, literalComparison);
            if (matches.Count == 0) continue;

            // Build context before/after.
            var before = new List<string>(contextLineCount);
            for (int b = Math.Max(0, i - contextLineCount); b < i; b++)
                before.Add(Helpers.LineTruncator.Truncate(lines[b]));
            var after = new List<string>(contextLineCount);
            for (int a = i + 1; a <= Math.Min(lines.Length - 1, i + contextLineCount); a++)
                after.Add(Helpers.LineTruncator.Truncate(lines[a]));

            foreach (var (start, length) in matches)
            {
                var displayLine = Helpers.LineTruncator.TruncateAroundMatch(lines[i], start, length);
                newResults.Add(new SearchResult(
                    FilePath: filePath,
                    LineNumber: i + 1,
                    MatchLine: displayLine.Text,
                    MatchStartColumn: displayLine.MatchStart,
                    MatchLength: length,
                    ContextBefore: before,
                    ContextAfter: after)
                { SourceMatchStartColumn = start });
            }
        }

        // Replace the group contents.
        int removedCount = group.Count;
        group.Clear();
        if (newResults.Count > 0)
        {
            foreach (var r in newResults)
                group.Add(r);
        }
        else
        {
            _resultCollection.RemoveGroup(group);
        }

        // Adjust MatchesFound to reflect the delta.
        int delta = newResults.Count - removedCount;
        MatchesFound = Math.Max(0, MatchesFound + delta);

        NotifyResultAvailabilityChanged();
        return newResults.Count > 0;
    }

    private static string BuildCompletionStatus(SearchSummary s, TimeSpan elapsed)
    {
        var time = FormatElapsed(elapsed);
        var rate = FormatThroughput(s.FilesScanned, s.BytesScanned, elapsed);
        if (s.Cancelled)
            return $"Cancelled — {s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
        if (s.Truncated)
            return $"Truncated at {s.TotalMatches:N0} matches ({time}, {rate})";
        if (s.Degraded)
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files — some results paged to disk ({time}, {rate})";
        return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2} elapsed";

    private static string FormatThroughput(int filesProcessed, long bytesScanned, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return $"{filesProcessed / seconds:N1} files/sec";
    }

    private static int ClampMatchCount(long matchCount) =>
        matchCount >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, matchCount);

    private double _instantFilesPerSec;
    private double _instantMbPerSec;
    private double _prevDisplayTime;
    private int _prevDisplayFiles;
    private long _prevDisplayBytes;

    private void UpdateFilesPerSecond()
    {
        if (_searchTimer is null)
        {
            return;
        }
        double seconds = Math.Max(_searchTimer.Elapsed.TotalSeconds, 0.001);
        int filesWithMatches = _resultCollection.AllGroups.Count;

        // Update instantaneous rate display (~2s window, like Task Manager)
        double displayDt = seconds - _prevDisplayTime;
        if (displayDt >= 2.0 && FilesScanned > 0)
        {
            int deltaFiles = FilesScanned - _prevDisplayFiles;
            long deltaBytes = _bytesScanned - _prevDisplayBytes;
            _instantFilesPerSec = deltaFiles / displayDt;
            _instantMbPerSec = deltaBytes / (1024.0 * 1024.0) / displayDt;
            _prevDisplayFiles = FilesScanned;
            _prevDisplayBytes = _bytesScanned;
            _prevDisplayTime = seconds;
        }

        StatusText = $"{MatchesFound:N0} matches in {filesWithMatches:N0} files ({FormatElapsed(_searchTimer.Elapsed)}, {_instantFilesPerSec:N1} files/sec)";

        // Collect incremental sample for sparkline (~0.15s window, rolling 30s)
        double dt = seconds - _prevSampleTime;
        if (dt >= 0.15 && FilesScanned > 0) // sample ~6-7x per second
        {
            int deltaFiles = FilesScanned - _prevFilesScanned;
            long deltaBytes = _bytesScanned - _prevBytesScanned;
            double sampleFps = deltaFiles / dt;
            double sampleMbps = deltaBytes / (1024.0 * 1024.0) / dt;
            ThroughputSamples.Add((sampleFps, sampleMbps));
            // Keep only last 30 seconds of samples (30s / 0.15s = 200)
            const int maxSamples = 200;
            if (ThroughputSamples.Count > maxSamples)
                ThroughputSamples.RemoveRange(0, ThroughputSamples.Count - maxSamples);
            _prevFilesScanned = FilesScanned;
            _prevBytesScanned = _bytesScanned;
            _prevSampleTime = seconds;
        }
    }

    partial void OnFileNameFilterChanged(string value) => ApplySortAndFilter();

    private void ApplySortAndFilter()
    {
        _resultCollection.FileNameFilter = FileNameFilter;
        _resultCollection.IncludeGlobs = IncludeGlobs;
        _resultCollection.ExcludeGlobs = ExcludeGlobs;
        _resultCollection.IncludeFilterMode = IncludeFilterMode;
        _resultCollection.ExcludeFilterMode = ExcludeFilterMode;
        _resultCollection.SortModeIndex = SortModeIndex;
        _resultCollection.SortDirectionIndex = SortDirectionIndex;
        _resultCollection.SetSortCriteria(_sortCriteria);
        _resultCollection.GroupMode = GroupMode;
        _resultCollection.GroupSortDirectionIndex = GroupSortDirectionIndex;
        _resultCollection.DateRangeFilter = DateRangeFilter;
        _resultCollection.SetExtensionFilters(_selectedExtensionFilters);
        _resultCollection.ApplySortAndFilter();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void SyncRecent()
    {
        RecentDirectories.Clear();
        foreach (var d in _settings.RecentDirectories) RecentDirectories.Add(d);
        SearchHistory.Clear();
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);
    }

    public async Task PersistSettingsAsync()
    {
        _settings.LastDirectory = Directory;
        _settings.CaseSensitive = CaseSensitive;
        _settings.UseRegex = UseRegex;
        _settings.ExactMatch = ExactMatch;
        _settings.ContextLines = ContextLines;
        _settings.PreviewContextLines = PreviewContextLines;
        _settings.ObeyGitignore = ObeyGitignore;
        _settings.GitignoreTakesPrecedence = GitignoreTakesPrecedence;
        _settings.IncludeGlobs = IncludeGlobs;
        _settings.ExcludeGlobs = ExcludeGlobs;
        _settings.IncludeFilterModeIndex = IncludeFilterModeIndex;
        _settings.ExcludeFilterModeIndex = ExcludeFilterModeIndex;
        _settings.MinFileSizeBytes = MinFileSizeBytes;
        _settings.MaxFileSizeBytes = MaxFileSizeBytes;
        _settings.CreatedAfterDate = CreatedAfterDate;
        _settings.CreatedBeforeDate = CreatedBeforeDate;
        _settings.ModifiedAfterDate = ModifiedAfterDate;
        _settings.ModifiedBeforeDate = ModifiedBeforeDate;
        _settings.DefaultMinFileSizeBytes = DefaultMinFileSizeBytes;
        _settings.DefaultMaxFileSizeBytes = DefaultMaxFileSizeBytes;
        _settings.DefaultCreatedAfterDate = DefaultCreatedAfterDate;
        _settings.DefaultCreatedBeforeDate = DefaultCreatedBeforeDate;
        _settings.DefaultModifiedAfterDate = DefaultModifiedAfterDate;
        _settings.DefaultModifiedBeforeDate = DefaultModifiedBeforeDate;
        _settings.MaxResults = MaxResults;
        _settings.EditorCommand = EditorCommand;
        _settings.PreviewModeIndex = PreviewModeIndex;
        _settings.ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(ThemeModeIndex);
        _settings.PreviewWordWrap = PreviewWordWrap;
        _settings.PreviewWrapModeIndex = PreviewWrapModeIndex;
        _settings.PreviewAutoLoadMatches = PreviewAutoLoadMatches;
        _settings.SelectedPreviewContentBackgroundColor = ColorStringHelper.Normalize(
            SelectedPreviewContentBackgroundColor,
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        _settings.UnselectedPreviewContentBackgroundColor = ColorStringHelper.Normalize(
            UnselectedPreviewContentBackgroundColor,
            Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        _settings.PreviewGutterContextColor = ColorStringHelper.Normalize(
            PreviewGutterContextColor,
            Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        _settings.PreviewGutterMatchColor = ColorStringHelper.Normalize(
            PreviewGutterMatchColor,
            Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        _settings.PreviewEditorGutterColor = ColorStringHelper.Normalize(
            PreviewEditorGutterColor,
            Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6));
        // Preserve the empty "Auto" sentinel; only normalize an explicit override to canonical ARGB hex.
        _settings.PreviewEditorTextColor = string.IsNullOrWhiteSpace(PreviewEditorTextColor)
            ? string.Empty
            : ColorStringHelper.Normalize(
                PreviewEditorTextColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        _settings.PreviewMatchTextColor = ColorStringHelper.Normalize(
            PreviewMatchTextColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        _settings.PreviewOverlayColor = ColorStringHelper.Normalize(
            PreviewOverlayColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x00));
        _settings.PreviewMatchLineColor = ColorStringHelper.Normalize(
            PreviewMatchLineColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        _settings.PreviewShowMoreEllipsisColor = ColorStringHelper.Normalize(
            PreviewShowMoreEllipsisColor,
            Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
        _settings.PreviewShowMoreEllipsisFontSize = Math.Clamp(
            PreviewShowMoreEllipsisFontSize <= 0 ? AppSettings.DefaultPreviewShowMoreEllipsisFontSize : PreviewShowMoreEllipsisFontSize,
            6,
            72);
        _settings.PreviewTextFontFamily = string.IsNullOrWhiteSpace(PreviewTextFontFamily)
            ? AppSettings.DefaultPreviewTextFontFamily
            : PreviewTextFontFamily.Trim();
        _settings.PreviewTextFontSize = Math.Clamp(
            PreviewTextFontSize <= 0 ? AppSettings.DefaultPreviewTextFontSize : PreviewTextFontSize,
            6,
            72);
        _settings.PreviewEditorFontFamily = string.IsNullOrWhiteSpace(PreviewEditorFontFamily)
            ? AppSettings.DefaultPreviewEditorFontFamily
            : PreviewEditorFontFamily.Trim();
        _settings.PreviewEditorFontSize = Math.Clamp(
            PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : PreviewEditorFontSize,
            6,
            72);
        _settings.ResultListMatchTextFontFamily = string.IsNullOrWhiteSpace(ResultListMatchTextFontFamily)
            ? AppSettings.DefaultResultListMatchTextFontFamily
            : ResultListMatchTextFontFamily.Trim();
        _settings.ResultListMatchTextFontSize = Math.Clamp(
            ResultListMatchTextFontSize <= 0 ? AppSettings.DefaultResultListMatchTextFontSize : ResultListMatchTextFontSize,
            6,
            72);
        _settings.ResultListMatchHighlightColor = ColorStringHelper.Normalize(
            ResultListMatchHighlightColor,
            Windows.UI.Color.FromArgb(0xFF, 0xB8, 0x86, 0x0B));
        _settings.LogLevelIndex = FileLogLevelIndex;
        _settings.ConsoleLogLevelIndex = ConsoleLogLevelIndex;
        _settings.FileListerBackendIndex = FileListerBackendIndex;
        _settings.ParallelismIndex = ParallelismIndex;
        _settings.LineTruncationLength = LineTruncationLength;
        _settings.MaxRecentItems = MaxRecentItems;
        _settings.GlobalHotkeyEnabled = GlobalHotkeyEnabled;
        _settings.GlobalHotkeyKey = HotkeyService.TryNormalizeLetter(GlobalHotkeyKey, out var hotkeyKey)
            ? hotkeyKey.ToString()
            : HotkeyService.DefaultStartKey.ToString();
        _settings.MemoryLimitMB = MemoryLimitMB;
        _settings.MemoryPressurePercent = MemoryPressurePercent;
        _settings.SearchResultTempDirectory = ResultStoreTempLocationService.NormalizeTempDirectory(SearchResultTempDirectory);
        _settings.HasChosenSearchResultTempDirectory = HasChosenSearchResultTempDirectory;
        _settings.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(LowDiskSpaceWarningPercent);
        _settings.ShowMemoryPressureWarningLabel = ShowMemoryPressureWarningLabel;
        _settings.ShowStatsForNerds = ShowStatsForNerds;
        _settings.ShowBuildNumberInTitleBar = ShowBuildNumberInTitleBar;
        _settings.ShowAutoScrollResultsCheckbox = ShowAutoScrollResultsCheckbox;
        _settings.SdkChannelBufferSize = SdkChannelBufferSize;
        _settings.MaxMatchesPerFile = MaxMatchesPerFile;
        _settings.SkipBinary = SkipBinary;
        _settings.SearchInsideArchives = SearchInsideArchives;
        _settings.ArchiveExtensions = SettingsArchiveExtensions;
        _settings.SkipExtensions = SettingsSkipExtensions;
        _settings.BinaryExtensions = SettingsBinaryExtensions;
        _settings.SuppressAdminWarning = SuppressAdminWarning;
        _settings.SuppressFontContrastWarnings = SuppressFontContrastWarnings;
        _settings.FontContrastReminderAfterUtc = FontContrastReminderAfterUtc;
        _settings.ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths;
        _settings.AdminProtectedPathSegments = AdminProtectedPathSegments;
        _settings.HasCompletedFirstRun = HasCompletedFirstRun;
        _settings.HasShownFileDrawerIntroTip = HasShownFileDrawerIntroTip;
        _settings.HasShownFileDrawerLineNumberIntroTip = HasShownFileDrawerLineNumberIntroTip;
        _settings.HasShownPreviewMatchIntroTip = HasShownPreviewMatchIntroTip;
        _settings.LimitParallelismOnHdd = LimitParallelismOnHdd;
        _settings.BackupBeforeSave = BackupBeforeSave;
        _settings.ShowEditorSavedOverlay = ShowEditorSavedOverlay;
        _settings.WindowFocusBehavior = WindowFocusBehavior;
        _settings.StartInLauncherMode = StartInLauncherMode;
        _settings.CloseToTray = CloseToTray;
        _settings.MaximizeOnStartup = MaximizeOnStartup;
        _settings.AdvancedOptionsCollapsedWidthModeIndex = NormalizeAdvancedOptionsCollapsedWidthModeIndex(AdvancedOptionsCollapsedWidthModeIndex);
        _settings.TerminalDefaultWorkingDirectory = string.IsNullOrWhiteSpace(TerminalDefaultWorkingDirectory)
            ? string.Empty
            : TerminalDefaultWorkingDirectory.Trim();
        _settings.FileHeaderCheckAddsToPreview = FileHeaderCheckAddsToPreview;
        _settings.MatchLineCheckAddsToPreview = MatchLineCheckAddsToPreview;
        _settings.PreviewEditorMaxSizeMB = PreviewEditorMaxSizeMB;
        _settings.PreviewEditorMaxTextLength = PreviewEditorMaxTextLength;
        _settings.PreviewEditorMaxLineLength = PreviewEditorMaxLineLength;
        _settings.ContentSearchFileSizeMB = ContentSearchFileSizeMB;
        _settings.MaxResultsCeiling = MaxResultsCeiling > 0 ? MaxResultsCeiling : 50_000;
        _settings.MmfConcurrencyLimit = MmfConcurrencyLimit;
        _settings.NativeConcurrencyLimit = NativeConcurrencyLimit;
        _settings.MaxMatchesPerSection = MaxMatchesPerSection;
        _settings.PreviewSectionPageSize = PreviewSectionPageSize;
        _settings.FullFilePreviewLimitMB = FullFilePreviewLimitMB;
        _settings.ArchiveMaxNestingDepth = ArchiveMaxNestingDepth;
        _settings.ArchiveMaxEntryMB = ArchiveMaxEntryMB;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        LogService.Instance.Info("Settings", "Settings persisted");
        LogService.Instance.Flush();
    }

    public List<SearchResult> GetAllSelectedResults()
    {
        return _resultCollection.GetAllSelectedResults();
    }

    // -----------------------------------------------------------------------
    // .yagu-session save/load — round-trips the visible result graph to disk
    // without re-running the search.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Save the current results plus search query / stats to a <c>.yagu-session</c>
    /// file. Evicted results are hydrated one group at a time and re-evicted after
    /// writing to avoid holding all payloads in memory simultaneously.
    /// </summary>
    public async Task<int> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        BeginSessionProgress($"Preparing to save {Path.GetFileName(path)}…");
        try
        {
            // Snapshot the group list so we can iterate without UI-thread mutation interference.
            var groupsSnapshot = _resultCollection.AllGroups.ToArray();
            int totalGroups = groupsSnapshot.Length;

            // Pre-count total results (materializing evicted stubs so Count is accurate)
            // without hydrating payloads — this is cheap (just expands compact stub pages).
            int totalResults = 0;
            for (int gi = 0; gi < totalGroups; gi++)
            {
                groupsSnapshot[gi].MaterializeEvictedStubs();
                totalResults += groupsSnapshot[gi].Count;
            }

            ReportSessionProgress(0.05, $"Writing {totalResults:N0} match(es) to {Path.GetFileName(path)} (streaming)…");

            var stats = new SessionFileService.SessionStats(
                _searchStartedUtc,
                _lastSearchElapsed,
                FilesScanned,
                _bytesScanned,
                MatchesFound);

            await using var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            var store = _resultStore;

            await SessionFileService.WriteStreamingAsync(
                fs,
                Query ?? string.Empty,
                Directory ?? string.Empty,
                stats,
                totalResults,
                totalGroups,
                prepareGroup: gi =>
                {
                    var g = groupsSnapshot[gi];
                    int count = g.Count;
                    // Hydrate evicted results for this group so WriteResult sees full payloads.
                    if (store is not null)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var r = g[i];
                            if (r.IsEvicted)
                                HydrateResult(r);
                        }
                    }
                    // Return a lightweight wrapper that indexes into the group directly.
                    return new FileGroupResultList(g);
                },
                releaseGroup: gi =>
                {
                    // Re-evict the group's results back to disk so memory is freed
                    // before we hydrate the next group.
                    if (store is null) return;
                    var g = groupsSnapshot[gi];
                    int count = g.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var r = g[i];
                        if (!r.IsEvicted)
                            r.Evict(store);
                    }
                },
                progress: new Progress<double>(p =>
                    ReportSessionProgress(0.05 + 0.95 * p,
                        $"Writing session: {p * 100:N0}% ({totalResults:N0} match(es))")),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var savedStatus = $"Saved session: {totalResults:N0} match(es) → {Path.GetFileName(path)}";
            if (!_dispatcher.TryEnqueue(() => StatusText = savedStatus))
                StatusText = savedStatus;
            return totalResults;
        }
        finally
        {
            EndSessionProgress();
        }
    }

    /// <summary>
    /// Lightweight <see cref="IReadOnlyList{SearchResult}"/> wrapper around a
    /// <see cref="FileGroup"/> so we don't allocate a copy of its items array
    /// just to pass it to the streaming writer.
    /// </summary>
    private sealed class FileGroupResultList(FileGroup group) : IReadOnlyList<SearchResult>
    {
        public SearchResult this[int index] => group[index];
        public int Count => group.Count;
        public IEnumerator<SearchResult> GetEnumerator()
        {
            for (int i = 0; i < group.Count; i++)
                yield return group[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Load a <c>.yagu-session</c> file into the result list. Cancels any
    /// in-progress search, clears existing state, then streams results into
    /// the collection in batches so very large sessions don't block the UI.
    /// </summary>
    public async Task<SessionFileService.SessionHeader> LoadSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsSearching)
            await CancelAsync().ConfigureAwait(true);

        BeginSessionProgress($"Opening {Path.GetFileName(path)}…");
        try
        {
            _resultCollection.Clear();
            FileMetadataCache.Clear();
            _resultStore?.Dispose();
            _resultStore = null;

            ErrorText = null;
            FallbackReason = null;
            FilesScanned = 0;
            TotalFiles = 0;
            MatchesFound = 0;
            FilesSkipped = 0;
            AccessDeniedCount = 0;
            Truncated = false;
            Degraded = false;
            DegradedNoticeText = string.Empty;
            FilesPerSecondText = string.Empty;
            ThroughputSamples.Clear();

            bool firstBatch = true;
            int loadedCount = 0;
            string fileName = Path.GetFileName(path);

            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);

            var readProgress = new Progress<double>(p =>
                ReportSessionProgress(p, $"Loading {fileName}: {p * 100:N0}%"));

            var header = await SessionFileService.ReadAsync(
                fs,
                h =>
                {
                    void apply()
                    {
                        Query = h.Query ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(h.SearchRoot))
                            Directory = h.SearchRoot;
                        _searchStartedUtc = h.Stats.StartedUtc;
                        _lastSearchElapsed = h.Stats.Elapsed;
                        FilesScanned = h.Stats.FilesScanned;
                        _bytesScanned = h.Stats.BytesScanned;
                    }
                    if (!_dispatcher.TryEnqueue(apply))
                        apply();
                },
                async batch =>
                {
                    // Hop to UI thread for the collection mutation.
                    var tcs = new TaskCompletionSource();
                    bool enqueued = _dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            bool resultAvailabilityChanged = _resultCollection.AddRange(
                                batch,
                                InitializeResultGroup,
                                evictNewResults: false,
                                resultStore: null);

                            loadedCount += batch.Count;
                            MatchesFound = loadedCount;

                            if (firstBatch || resultAvailabilityChanged)
                            {
                                firstBatch = false;
                                NotifyResultAvailabilityChanged();
                            }
                        }
                        finally
                        {
                            tcs.SetResult();
                        }
                    });

                    if (!enqueued)
                    {
                        // Dispatcher unavailable (e.g. tests without a UI thread) —
                        // fall back to a direct call.
                        _resultCollection.AddRange(batch, InitializeResultGroup, evictNewResults: false, resultStore: null);
                        loadedCount += batch.Count;
                        MatchesFound = loadedCount;
                        return;
                    }

                    await tcs.Task.ConfigureAwait(false);
                },
                readProgress,
                cancellationToken).ConfigureAwait(false);

            void finish()
            {
                int actualFileCount = _resultCollection.AllGroups.Count;
                var displaySummary = new SearchSummary(
                    TotalFiles: header.Stats.FilesScanned,
                    FilesScanned: header.Stats.FilesScanned,
                    FilesSkipped: 0,
                    FilesWithMatches: actualFileCount,
                    TotalMatches: loadedCount,
                    BytesScanned: header.Stats.BytesScanned,
                    Elapsed: header.Stats.Elapsed,
                    Cancelled: false,
                    Truncated: false,
                    Degraded: false,
                    FallbackReason: null);
                StatusText = BuildCompletionStatus(displaySummary, header.Stats.Elapsed);
                ApplySortAndFilter();
                NotifyResultAvailabilityChanged();
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
            if (!_dispatcher.TryEnqueue(finish))
                finish();

            return header;
        }
        finally
        {
            EndSessionProgress();
        }
    }

    private void BeginSessionProgress(string initialText)
    {
        void apply()
        {
            IsSessionBusy = true;
            SessionProgressPercent = 0;
            SessionProgressText = initialText;
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }

    private void ReportSessionProgress(double fraction, string text)
    {
        double pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        void apply()
        {
            SessionProgressPercent = pct;
            SessionProgressText = text;
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }

    private void EndSessionProgress()
    {
        void apply()
        {
            IsSessionBusy = false;
            SessionProgressPercent = 0;
            SessionProgressText = string.Empty;
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }

    public void SetDirectoryFromArgs(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!System.IO.Directory.Exists(dir))
        {
            ErrorText = $"--dir path does not exist or is not a directory: {dir}";
            return;
        }
        Directory = dir;
    }

    private static List<string> SplitCsv(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<string> SplitFilterPatterns(string s, FilterPatternMode mode) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : mode == FilterPatternMode.Regex
                ? [s.Trim()]
                : SplitCsv(s);

    private static bool IsDefaultExcludeGlobs(string value) =>
        string.Equals(value?.Trim(), AppSettings.DefaultExcludeGlobs, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ParseExtensionSet(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(e => e.TrimStart('.', '*')),
                StringComparer.OrdinalIgnoreCase);

    private HashSet<string> BuildEffectiveSkipExtensionSet()
    {
        var effective = ParseExtensionSet(SkipExtensions);
        foreach (var ext in ParseExtensionSet(BinaryExtensions))
            effective.Add(ext);
        return effective;
    }

    /// <summary>Parse a semicolon-separated extension string into a set WITH leading dots (e.g. ".zip", ".docx").</summary>
    private static HashSet<string> ParseDottedExtensionSet(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(e => e.TrimStart('.', '*'))
             .Select(e => "." + e),
            StringComparer.OrdinalIgnoreCase);
    }

    private static int ResolveParallelism(int index)
    {
        return SearchOptions.ResolveContentSearchParallelism(index, Environment.ProcessorCount);
    }

    private static void ShowSearchCompleteToast(SearchSummary s, TimeSpan elapsed)
    {
        try
        {
            var title = s.Cancelled ? "Search Cancelled" : "Search Complete";
            var body = $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files";
            if (s.FilesSkipped > 0)
                body += $" — {s.FilesSkipped:N0} skipped";
            body += $" ({elapsed.TotalSeconds:F1}s)";

            var xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{System.Security.SecurityElement.Escape(title)}</text>
                      <text>{System.Security.SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var notification = new Microsoft.Windows.AppNotifications.AppNotification(xml);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Toast failures should never break the app.
        }
    }

    /// <summary>
    /// Called when the directory text changes. Debounces and fetches subdirectory suggestions.
    /// </summary>
    internal async Task UpdateDirectorySuggestionsAsync(string text)
    {
        // Cancel any previous in-flight lookup.
        _dirAutoCompleteCts?.Cancel();
        _dirAutoCompleteCts = new CancellationTokenSource();
        var ct = _dirAutoCompleteCts.Token;

        try
        {
            // Debounce: wait 250ms before querying.
            await Task.Delay(250, ct).ConfigureAwait(false);

            var suggestions = await _dirAutoComplete.GetSuggestionsAsync(text, ct).ConfigureAwait(false);

            // If no subdirectory suggestions, show recent directories as fallback.
            if (suggestions.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    DirectorySuggestions.Clear();
                    foreach (var d in _settings.RecentDirectories)
                        DirectorySuggestions.Add(d);
                });
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                DirectorySuggestions.Clear();
                foreach (var s in suggestions)
                    DirectorySuggestions.Add(s);
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when user keeps typing.
        }
    }
}
