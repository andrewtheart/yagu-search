using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Yagu.Models;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Ai;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

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
    IReadOnlyList<string> ContextAfter,
    int MatchStartColumn,
    int MatchLength,
    int SourceMatchStartColumn);

public sealed partial class MainViewModel : ObservableObject, IDisposable, ISemanticPlanTarget
{
    private readonly SearchService _search;
    private readonly SettingsService _settingsService;
    private readonly EditorLauncher _editor;
    private readonly DispatcherQueue _dispatcher;
    private readonly ISemanticQueryTranslator? _semanticTranslator;
    private readonly ISemanticCapabilityDetector _capabilityDetector;
    private readonly bool _semanticHasGpu;
    private readonly bool _semanticHasNpu;
    // Guards the telemetry/bug-report settings observable properties so seeding them from persisted
    // settings (in the constructor / consent flow) does not trigger a redundant persist.
    private bool _telemetryInitialized;
    private CancellationTokenSource? _semanticCts;
    // Natural-language query captured at submit time (before translation overwrites Query) so it can
    // be stored in the separate Semantic autocomplete history once the search actually starts.
    private string? _pendingSemanticHistoryEntry;
    // The user's saved search-filter defaults, captured before a semantic plan is applied so they can
    // be restored after the per-root options are built — ensuring a semantic search applies its
    // resolved settings to that ONE run only and never changes the persisted defaults. Null outside a
    // semantic run; consumed by StartSearchAsync, or restored by SubmitSearchAsync if the run is
    // cancelled before reaching that point.
    private SemanticSearchInputSnapshot? _semanticDefaultsSnapshot;
    // True while a completed semantic search's resolved settings are intentionally LEFT visible in
    // Advanced Options (so the user can see what the AI search applied). While set, PersistSettingsAsync
    // writes the saved defaults (from the snapshot) instead of the resolved values, and the next search
    // resets the view-model back to those defaults.
    private bool _semanticResolutionVisible;
    private bool _queryModeInitialized;
    private CancellationTokenSource? _searchStatusHeartbeatCts;

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
    private Stopwatch? _searchTimer;
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
    private const int SearchSortRefreshDegradedDeferGroupThreshold = 20_000;

    public MainViewModel() : this(new SearchService(), new SettingsService(), new EditorLauncher(),
                                   DispatcherQueue.GetForCurrentThread())
    { }

    public MainViewModel(SearchService search, SettingsService settingsService, EditorLauncher editor, DispatcherQueue dispatcher,
                         ISemanticQueryTranslator? semanticTranslator = null,
                         ISemanticCapabilityDetector? capabilityDetector = null)
    {
        _search = search;
        _settingsService = settingsService;
        _editor = editor;
        _dispatcher = dispatcher;
        _resultCollection.VisibleGroups.CollectionChanging += OnVisibleResultGroupsChanging;
        _resultCollection.VisibleGroups.CollectionChanged += OnVisibleResultGroupsChanged;

        _settings = _settingsService.Load();
        _editor.Command = _settings.EditorCommand;

        // Seed the OCR download consent gate from the persisted setting so a user who already approved
        // (or who installed an OCR-bundled edition) is never re-prompted.
        Yagu.Services.Ocr.OcrDownloadGate.ConsentGranted = _settings.OcrDownloadConsented;

        // Telemetry / bug-reporting (both opt-in and independent). Seed the gate and the Settings-panel
        // toggles from persisted consent, and start the senders when either is already enabled so a
        // returning user's choice keeps working without a restart.
        Yagu.Services.Telemetry.TelemetryGate.TelemetryEnabled = _settings.TelemetryEnabled;
        Yagu.Services.Telemetry.TelemetryGate.BugReportingEnabled = _settings.BugReportingEnabled;
        TelemetryEnabledSetting = _settings.TelemetryEnabled;
        BugReportingEnabledSetting = _settings.BugReportingEnabled;
        BugReportContactEmail = _settings.BugReportContactEmail;
        _telemetryInitialized = true;
        if (_settings.TelemetryEnabled || _settings.BugReportingEnabled)
        {
            string installId = EnsureTelemetryInstallId();
            Yagu.Services.Telemetry.TelemetryService.Instance.Initialize(installId);
            Yagu.Services.Telemetry.BugReportService.Instance.Initialize(installId);
        }

        // Semantic search (Foundry Local). The translator is cheap to construct; it only downloads
        // the execution provider/model lazily on first use. A caller may inject a fake for testing.
        // The GUI drives Foundry OUT-OF-PROCESS via WorkerSemanticQueryTranslator so an SDK fail-fast
        // (the ObjectDisposedException EP-registration race / the onnxruntime-genai use-after-free)
        // kills the worker, not Yagu; the proxy surfaces a clean failure and respawns on next use.
        _semanticTranslator = semanticTranslator
            ?? new Yagu.Services.Ai.Worker.WorkerSemanticQueryTranslator(_settings.SemanticSearchEnabled, _settings.SemanticModelAlias, _settings.SemanticDevicePreferenceOrder);
        _capabilityDetector = capabilityDetector ?? new GpuNpuCapabilityDetector();
        SemanticSearchAvailable = _settings.SemanticSearchEnabled;
        SemanticHardwareAccelerated = SafeDetectAcceleratedHardware();
        _semanticHasGpu = SafeDetect(() => _capabilityDetector.HasGpu());
        _semanticHasNpu = SafeDetect(() => _capabilityDetector.HasNpu());
        // Tell the translator which accelerators actually exist so it never selects a GPU/NPU model
        // build on a machine that lacks one (such a build can load via DirectML yet crash during
        // inference). A CPU-only machine deterministically gets the CPU model build.
        _semanticTranslator.SetAvailableAccelerators(_semanticHasGpu, _semanticHasNpu);
        // Tell the translator how much dedicated GPU VRAM exists so AUTO selection can upgrade to a
        // larger, more accurate model (e.g. phi-4 14B) on a strong GPU instead of always defaulting to
        // the small phi-4-mini. 0 (unknown / no GPU) leaves the small default in place.
        _semanticTranslator.SetGpuMemoryBytes(SafeDetectGpuMemoryBytes());
        // Whether to release the model from VRAM after each translation (frees GPU memory between AI
        // searches at the cost of a reload); mirrors the AI settings toggle.
        _semanticTranslator.SetUnloadAfterUse(_settings.SemanticUnloadModelAfterUse);
        DefaultToTraditionalSearchMode = _settings.DefaultToTraditionalSearchMode;
        SemanticModelAlias = _settings.SemanticModelAlias;
        SemanticDevicePreferenceOrder = _settings.SemanticDevicePreferenceOrder;
        FoundryModelUpdateAlertsEnabled = _settings.FoundryModelUpdateAlertsEnabled;
        SemanticUnloadModelAfterUse = _settings.SemanticUnloadModelAfterUse;
        // Launch mode: once the user has explicitly chosen, honor that; otherwise follow the
        // hardware-based default (Semantic on accelerated machines, Traditional elsewhere).
        IsSemanticQueryMode = ResolveLaunchQueryMode();
        _queryModeInitialized = true;

        Directory = ResolveStartupDirectory();
        CaseSensitive = _settings.CaseSensitive;
        UseRegex = _settings.UseRegex;
        ExactMatch = _settings.ExactMatch;
        Multiline = _settings.MultilineSearchDefault;
        ContextLines = _settings.ContextLines;
        PreviewContextLines = _settings.PreviewContextLines;
        ObeyGitignore = _settings.ObeyGitignore;
        GitignoreTakesPrecedence = _settings.GitignoreTakesPrecedence;
        GitignorePrecedencePreference = _settings.GitignorePrecedencePreference;
        if (_settings.GitignorePrecedencePreference is bool savedPrecedence)
            GitignoreTakesPrecedence = savedPrecedence;
        IncludeGlobs = _settings.IncludeGlobs;
        ExcludeGlobs = IsDefaultExcludeGlobs(_settings.ExcludeGlobs) ? string.Empty : _settings.ExcludeGlobs;
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
        PreviewLongLineWarningIndex = Math.Clamp(_settings.PreviewLongLineWarningIndex, 0, 2);
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

        // ── File list overlay ──
        FileListOverlayHeight = Math.Clamp(_settings.FileListOverlayHeight <= 0 ? AppSettings.DefaultFileListOverlayHeight : _settings.FileListOverlayHeight, 20, 100);
        FileListOverlayFontSize = Math.Clamp(_settings.FileListOverlayFontSize <= 0 ? AppSettings.DefaultFileListOverlayFontSize : _settings.FileListOverlayFontSize, 6, 72);
        FileListOverlayFontColor = string.IsNullOrWhiteSpace(_settings.FileListOverlayFontColor) ? AppSettings.DefaultFileListOverlayFontColor : _settings.FileListOverlayFontColor;
        FileListOverlayFontFamily = string.IsNullOrWhiteSpace(_settings.FileListOverlayFontFamily) ? AppSettings.DefaultFileListOverlayFontFamily : _settings.FileListOverlayFontFamily;

        // ── Preview sticky header ──
        PreviewStickyHeaderHeight = Math.Clamp(_settings.PreviewStickyHeaderHeight <= 0 ? AppSettings.DefaultPreviewStickyHeaderHeight : _settings.PreviewStickyHeaderHeight, 20, 100);
        PreviewStickyHeaderFileNameFontSize = Math.Clamp(_settings.PreviewStickyHeaderFileNameFontSize <= 0 ? AppSettings.DefaultPreviewStickyHeaderFileNameFontSize : _settings.PreviewStickyHeaderFileNameFontSize, 6, 72);
        PreviewStickyHeaderFileNameFontColor = string.IsNullOrWhiteSpace(_settings.PreviewStickyHeaderFileNameFontColor) ? AppSettings.DefaultPreviewStickyHeaderFileNameFontColor : _settings.PreviewStickyHeaderFileNameFontColor;
        PreviewStickyHeaderFileNameFontFamily = string.IsNullOrWhiteSpace(_settings.PreviewStickyHeaderFileNameFontFamily) ? AppSettings.DefaultPreviewStickyHeaderFileNameFontFamily : _settings.PreviewStickyHeaderFileNameFontFamily;
        PreviewStickyHeaderDetailFontSize = Math.Clamp(_settings.PreviewStickyHeaderDetailFontSize <= 0 ? AppSettings.DefaultPreviewStickyHeaderDetailFontSize : _settings.PreviewStickyHeaderDetailFontSize, 6, 72);
        PreviewStickyHeaderDetailFontColor = string.IsNullOrWhiteSpace(_settings.PreviewStickyHeaderDetailFontColor) ? AppSettings.DefaultPreviewStickyHeaderDetailFontColor : _settings.PreviewStickyHeaderDetailFontColor;
        PreviewStickyHeaderDetailFontFamily = string.IsNullOrWhiteSpace(_settings.PreviewStickyHeaderDetailFontFamily) ? AppSettings.DefaultPreviewStickyHeaderDetailFontFamily : _settings.PreviewStickyHeaderDetailFontFamily;

        // ── File list drawer labels ──
        DrawerFileNameFontSize = Math.Clamp(_settings.DrawerFileNameFontSize <= 0 ? AppSettings.DefaultDrawerFileNameFontSize : _settings.DrawerFileNameFontSize, 6, 72);
        DrawerFileNameFontColor = string.IsNullOrWhiteSpace(_settings.DrawerFileNameFontColor) ? AppSettings.DefaultDrawerFileNameFontColor : _settings.DrawerFileNameFontColor;
        DrawerFileNameFontFamily = string.IsNullOrWhiteSpace(_settings.DrawerFileNameFontFamily) ? AppSettings.DefaultDrawerFileNameFontFamily : _settings.DrawerFileNameFontFamily;
        DrawerDirectoryFontSize = Math.Clamp(_settings.DrawerDirectoryFontSize <= 0 ? AppSettings.DefaultDrawerDirectoryFontSize : _settings.DrawerDirectoryFontSize, 6, 72);
        DrawerDirectoryFontColor = string.IsNullOrWhiteSpace(_settings.DrawerDirectoryFontColor) ? AppSettings.DefaultDrawerDirectoryFontColor : _settings.DrawerDirectoryFontColor;
        DrawerDirectoryFontFamily = string.IsNullOrWhiteSpace(_settings.DrawerDirectoryFontFamily) ? AppSettings.DefaultDrawerDirectoryFontFamily : _settings.DrawerDirectoryFontFamily;
        DrawerMetadataFontSize = Math.Clamp(_settings.DrawerMetadataFontSize <= 0 ? AppSettings.DefaultDrawerMetadataFontSize : _settings.DrawerMetadataFontSize, 6, 72);
        DrawerMetadataFontColor = string.IsNullOrWhiteSpace(_settings.DrawerMetadataFontColor) ? AppSettings.DefaultDrawerMetadataFontColor : _settings.DrawerMetadataFontColor;
        DrawerMetadataFontFamily = string.IsNullOrWhiteSpace(_settings.DrawerMetadataFontFamily) ? AppSettings.DefaultDrawerMetadataFontFamily : _settings.DrawerMetadataFontFamily;

        FileLogLevelIndex = _settings.LogLevelIndex;
        ConsoleLogLevelIndex = _settings.ConsoleLogLevelIndex;
        FileListerBackendIndex = _settings.FileListerBackendIndex;
        ParallelismIndex = _settings.ParallelismIndex;
        IoOversubscriptionIndex = _settings.IoOversubscriptionIndex;
        LineTruncationLength = _settings.LineTruncationLength;
        MaxRecentItems = _settings.MaxRecentItems;
        MaxSemanticRecentItems = _settings.MaxSemanticRecentItems;
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
        MaxMatchesPerLine = _settings.MaxMatchesPerLine;
        AbsoluteMaxResults = _settings.AbsoluteMaxResults;
        SkipBinary = _settings.SkipBinary;
        SearchOnlineOnlyFiles = _settings.SearchOnlineOnlyFiles;
        SearchHiddenFiles = _settings.SearchHiddenFiles;
        SearchImageText = _settings.SearchImageText;
        ImageOcrEngine = _settings.ImageOcrEngine;
        ImageOcrModel = _settings.ImageOcrModel;
        ImageOcrMaxSide = _settings.ImageOcrMaxSide;
        PinStartupDirectory = _settings.PinStartupDirectory;
        SearchInsideArchives = _settings.SearchInsideArchives;
        SettingsSkipExtensions = _settings.SkipExtensions;
        SettingsBinaryExtensions = _settings.BinaryExtensions;
        SettingsArchiveExtensions = _settings.ArchiveExtensions;
        SkipExtensions = SettingsSkipExtensions;
        BinaryExtensions = SettingsBinaryExtensions;
        ArchiveExtensions = SettingsArchiveExtensions;
        SuppressAdminWarning = _settings.SuppressAdminWarning;
        SuppressEverythingNotRunningPrompt = _settings.SuppressEverythingNotRunningPrompt;
        SuppressExcludedExtensionWarnings = _settings.SuppressExcludedExtensionWarnings;
        IncludeExcludedExtensionByDefault = _settings.IncludeExcludedExtensionByDefault;
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
        SuppressHddParallelismWarnings = _settings.SuppressHddParallelismWarnings;
        SearchAllDrivesIncludesNetwork = _settings.SearchAllDrivesIncludesNetwork;
        SearchAllDrivesIncludesRemovable = _settings.SearchAllDrivesIncludesRemovable;
        SearchAllDrivesIncludesCloud = _settings.SearchAllDrivesIncludesCloud;
        SearchAllDrivesForceFullScan = _settings.SearchAllDrivesForceFullScan;
        BackupBeforeSave = _settings.BackupBeforeSave;
        ShowEditorSavedOverlay = _settings.ShowEditorSavedOverlay;
        EditorSyntaxHighlightingEnabled = _settings.EditorSyntaxHighlightingEnabled;
        WindowFocusBehavior = _settings.WindowFocusBehavior;
        StartInLauncherMode = _settings.StartInLauncherMode;
        CloseToTray = _settings.CloseToTray;
        HasShownCloseToTrayNotification = _settings.HasShownCloseToTrayNotification;
        MaximizeOnStartup = _settings.MaximizeOnStartup;
        LaunchWindowPosition = _settings.LaunchWindowPosition is >= 0 and <= 8 ? _settings.LaunchWindowPosition : 0;
        LauncherWindowPosition = _settings.LauncherWindowPosition is >= 0 and <= 8 ? _settings.LauncherWindowPosition : 2;
        AdvancedOptionsCollapsedWidthModeIndex = NormalizeAdvancedOptionsCollapsedWidthModeIndex(_settings.AdvancedOptionsCollapsedWidthModeIndex);
        TerminalDefaultWorkingDirectory = _settings.TerminalDefaultWorkingDirectory ?? string.Empty;
        TerminalShellKindIndex = TerminalShell.NormalizeSettingsIndex(_settings.TerminalShellKindIndex);
        FileHeaderCheckAddsToPreview = _settings.FileHeaderCheckAddsToPreview;
        MatchLineCheckAddsToPreview = _settings.MatchLineCheckAddsToPreview;
        PreviewEditorMaxSizeMB = _settings.PreviewEditorMaxSizeMB;
        PreviewEditorMaxTextLength = _settings.PreviewEditorMaxTextLength;
        PreviewEditorMaxLineLength = _settings.PreviewEditorMaxLineLength;
        PreviewEditorPopOutMaxSizeMB = _settings.PreviewEditorPopOutMaxSizeMB > 0 ? _settings.PreviewEditorPopOutMaxSizeMB : 100;
        PreviewEditorPopOutArrangementIndex = Math.Clamp(_settings.PreviewEditorPopOutArrangementIndex, 0, 4);
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
        foreach (var d in _settings.RecentDirectories) DirectorySuggestions.Add(new HistorySuggestion(d, LookupRecentDirectoryTimestamp(d)));
        foreach (var q in _settings.SearchHistory) SearchHistory.Add(q);
        foreach (var q in _settings.SemanticSearchHistory) SemanticSearchHistory.Add(q);

        SyncSkipExtensionItems();
        SyncBinaryExtensionItems();
        SyncArchiveExtensionItems();
        // From here on, toggling "Search binary" drives the dropdown selection (see OnSkipBinaryChanged).
        _binaryExtensionsInitialized = true;
    }

    private static int NormalizePreviewWrapModeIndex(bool legacyPreviewWordWrap, int modeIndex)
    {
        if (legacyPreviewWordWrap)
            return (int)PreviewWrapMode.Wrap;

        return modeIndex == (int)PreviewWrapMode.Wrap
            ? (int)PreviewWrapMode.Wrap
            : (int)PreviewWrapMode.NoWrap;
    }

    private static int NormalizeAdvancedOptionsCollapsedWidthModeIndex(int modeIndex) => 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        try { _dirAutoCompleteCts?.Cancel(); } catch { }
        try { _metadataCts.Cancel(); } catch { }
        try { _semanticCts?.Cancel(); } catch { }
        StopSearchStatusHeartbeat();
        _cts?.Dispose();
        _dirAutoCompleteCts?.Dispose();
        _metadataCts.Dispose();
        _semanticCts?.Dispose();
        if (_semanticTranslator is IAsyncDisposable semanticDisposable)
        {
            try { _ = semanticDisposable.DisposeAsync().AsTask(); } catch { }
        }
        _searchLifecycleGate.Dispose();
        _resultStore?.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))]
    public partial string Directory { get; set; } = string.Empty;
    [ObservableProperty] public partial string Query { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial bool ExactMatch { get; set; } = true;

    /// <summary>When true, the query regex runs over the whole file so a single match can span line
    /// breaks (ripgrep <c>-U</c>). Strictly opt-in; initialized from <see cref="SettingsService"/>.</summary>
    [ObservableProperty] public partial bool Multiline { get; set; }

    /// <summary>When true and <see cref="Multiline"/> is on, <c>.</c> also matches newlines (dot-all).</summary>
    [ObservableProperty] public partial bool MultilineDotAll { get; set; }

    /// <summary>
    /// The regex toggle follows the multiline toggle: cross-line matching is only meaningful in regex
    /// mode (a plain literal is split on whitespace — newlines included — so it can never span a line
    /// break). Turning Multiline ON enables Regex; turning Multiline OFF disables Regex. Exact-match
    /// (whole-word) is the inverse: it is a single-token concept that regex overrides anyway, so it is
    /// unchecked while Multiline is on and restored when Multiline turns off.
    /// </summary>
    partial void OnMultilineChanged(bool value)
    {
        UseRegex = value;
        ExactMatch = !value;
    }

    /// <summary>The pattern + flags the MOST RECENT search actually ran with, captured at search
    /// start. For a semantic search these are the model's RESOLVED literal pattern and flags — not
    /// the natural-language box text (which stays in <see cref="Query"/> for display) nor the user
    /// defaults that the semantic run restores afterward. Preview/editor match highlighting reads
    /// these so it boxes exactly the matches the engine found, independent of later Query/flag drift.</summary>
    public string LastSearchPattern { get; private set; } = string.Empty;
    public bool LastSearchCaseSensitive { get; private set; }
    public bool LastSearchUseRegex { get; private set; }
    public bool LastSearchExactMatch { get; private set; } = true;
    public bool LastSearchMultiline { get; private set; }
    public bool LastSearchMultilineDotAll { get; private set; }

    public Microsoft.UI.Xaml.Visibility HasQueryText =>
        string.IsNullOrEmpty(Query) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnQueryChanged(string value) => OnPropertyChanged(nameof(HasQueryText));

    // ── Semantic search (Foundry Local) ──
    /// <summary>True when the search bar is in natural-language (Semantic) mode rather than the
    /// traditional literal/regex mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTraditionalQueryMode))]
    [NotifyPropertyChangedFor(nameof(QueryPlaceholderText))]
    [NotifyPropertyChangedFor(nameof(InlineSearchTogglesVisibility))]
    [NotifyPropertyChangedFor(nameof(QueryModeLabel))]
    [NotifyPropertyChangedFor(nameof(QueryModeGlyph))]
    public partial bool IsSemanticQueryMode { get; set; }

    /// <summary>Inverse of <see cref="IsSemanticQueryMode"/> for binding the Traditional toggle.</summary>
    public bool IsTraditionalQueryMode => !IsSemanticQueryMode;

    /// <summary>Whether the Semantic toggle is offered at all (feature enabled in settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticDefaultOverrideEnabled))]
    public partial bool SemanticSearchAvailable { get; set; }

    /// <summary>True when the machine has a GPU/NPU accelerator capable of running a Semantic model.
    /// Drives the launch-mode default and gates the Settings override.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticDefaultOverrideEnabled))]
    public partial bool SemanticHardwareAccelerated { get; set; }

    /// <summary>User override: when true, default the search bar to Traditional even on accelerated
    /// machines. Bound to the Settings toggle; only editable when <see cref="SemanticDefaultOverrideEnabled"/>.</summary>
    [ObservableProperty]
    public partial bool DefaultToTraditionalSearchMode { get; set; }

    /// <summary>The model alias override the user has chosen (empty = automatic recommended pick).
    /// Mirrors <c>AppSettings.SemanticModelAlias</c>; updated when a model is chosen or reset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSemanticModelDisplay))]
    [NotifyPropertyChangedFor(nameof(HasSemanticModelOverride))]
    public partial string SemanticModelAlias { get; set; } = string.Empty;

    /// <summary>Friendly description of the model currently selected for semantic translation. Shows a
    /// pinned override by name, else the actually-loaded automatic model ("phi-4 (automatic)") when one
    /// is loaded, else a generic "Automatic" label until the first search (or a Refresh) resolves it.</summary>
    public string CurrentSemanticModelDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SemanticModelAlias))
                return SemanticModelAlias;
            string? loaded = (_semanticTranslator as FoundryLocalSemanticQueryTranslator)?.SelectedModelAlias;
            return string.IsNullOrWhiteSpace(loaded)
                ? "Automatic (recommended for your hardware)"
                : $"{loaded} (automatic)";
        }
    }

    /// <summary>Whether the user has pinned a specific model rather than using automatic selection.</summary>
    public bool HasSemanticModelOverride => !string.IsNullOrWhiteSpace(SemanticModelAlias);

    /// <summary>Preferred accelerator order (e.g. "GPU,NPU,CPU") for running the AI model. Applied
    /// live to the translator and persisted.</summary>
    [ObservableProperty]
    public partial string SemanticDevicePreferenceOrder { get; set; } = "GPU,NPU,CPU";

    /// <summary>When true (default), Yagu checks the Foundry Local catalog about once a day and alerts
    /// the user when a new/updated/variant on-device model becomes available. Bound to the AI settings
    /// tab toggle and the alert modal's "Don't alert me again" option.</summary>
    [ObservableProperty]
    public partial bool FoundryModelUpdateAlertsEnabled { get; set; } = true;

    /// <summary>When true (default), the on-device semantic model is unloaded from memory (freeing GPU
    /// VRAM) right after each AI-search translation finishes; the next query reloads it. Set false to keep
    /// the model resident for the fastest repeat queries. Bound to the AI settings tab toggle, applied live
    /// to the translator, and persisted.</summary>
    [ObservableProperty]
    public partial bool SemanticUnloadModelAfterUse { get; set; } = true;

    /// <summary>Settings-panel toggle for the silent, anonymized telemetry channel. Two-way bound;
    /// applied live to <see cref="Yagu.Services.Telemetry.TelemetryGate"/> and persisted.</summary>
    [ObservableProperty]
    public partial bool TelemetryEnabledSetting { get; set; }

    /// <summary>Settings-panel toggle for the (reviewed) bug-report flow. Two-way bound; applied live
    /// and persisted. Independent of <see cref="TelemetryEnabledSetting"/>.</summary>
    [ObservableProperty]
    public partial bool BugReportingEnabledSetting { get; set; }

    /// <summary>Optional contact email used to pre-fill the bug-report dialog. Two-way bound in the
    /// Settings panel and updated when the user types an email in a report.</summary>
    [ObservableProperty]
    public partial string BugReportContactEmail { get; set; } = string.Empty;

    /// <summary>True once the first-run telemetry/bug-report consent dialog has been shown, so the app
    /// never asks again.</summary>
    public bool TelemetryConsentPromptShown => _settings.TelemetryConsentPromptShown;

    /// <summary>True when a real GPU was detected (read-only info for the AI settings tab).</summary>
    public bool SemanticHasGpu => _semanticHasGpu;

    /// <summary>True when an NPU was detected (read-only info for the AI settings tab).</summary>
    public bool SemanticHasNpu => _semanticHasNpu;

    /// <summary>The Settings override is editable only when Semantic search is offered AND the machine
    /// has a supported accelerator; otherwise it is greyed out and unset (Traditional is forced anyway).</summary>
    public bool SemanticDefaultOverrideEnabled => SemanticSearchAvailable && SemanticHardwareAccelerated;

    /// <summary>True while a natural-language query is being translated by the local model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticStatusBarVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchModeSplitButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchActionButtonVisibility))]
    public partial bool IsTranslatingSemanticQuery { get; set; }

    /// <summary>Status/progress line shown next to the mode toggle during translation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticStatusBarVisibility))]
    public partial string SemanticStatusText { get; set; } = string.Empty;

    /// <summary>Whether a semantic model has already been downloaded (skip the first-run prompt).</summary>
    public bool IsSemanticModelDownloaded => _settings.SemanticModelDownloaded;

    /// <summary>Short label for the single query-mode dropdown button.</summary>
    public string QueryModeLabel => IsSemanticQueryMode ? "Semantic" : "Traditional";

    /// <summary>Segoe icon glyph for the single query-mode dropdown button.</summary>
    public string QueryModeGlyph => IsSemanticQueryMode ? "\uF4A5" : "\uE721";

    /// <summary>Visibility of the Traditional|Semantic mode bar (feature-gated).</summary>
    public Microsoft.UI.Xaml.Visibility SemanticModeBarVisibility =>
        SemanticSearchAvailable ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The search button is a SplitButton (with a chevron mode picker) only while semantic
    /// search is available AND fully idle. As soon as a search starts — including the semantic
    /// translation phase — it is replaced by the morphing Cancel button so the user can't fire a
    /// second concurrent run (which would corrupt the local model's in-flight inference).</summary>
    public Microsoft.UI.Xaml.Visibility SearchModeSplitButtonVisibility =>
        SemanticSearchAvailable && !IsSearching && !IsTranslatingSemanticQuery
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The plain Search/Cancel button is shown when semantic search is unavailable (no mode
    /// chevron) or whenever a search is running — including the semantic translation phase — so it
    /// can morph into the red Cancel action the moment the user clicks Search.</summary>
    public Microsoft.UI.Xaml.Visibility SearchActionButtonVisibility =>
        !SemanticSearchAvailable || IsSearching || IsTranslatingSemanticQuery
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Visibility of the translation status line — only while translating or when a result
    /// explanation is showing.</summary>
    public Microsoft.UI.Xaml.Visibility SemanticStatusBarVisibility =>
        SemanticSearchAvailable && (IsTranslatingSemanticQuery || !string.IsNullOrEmpty(SemanticStatusText))
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The Case/Regex/Exact inline toggles only apply in Traditional mode.</summary>
    public Microsoft.UI.Xaml.Visibility InlineSearchTogglesVisibility =>
        IsSemanticQueryMode ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Placeholder text that adapts to the current query mode.</summary>
    public string QueryPlaceholderText => IsSemanticQueryMode
        ? "Describe what to find — e.g. \"png files on C: modified in the past year, ignore mov files\""
        : "Search query (Enter to run)";

    partial void OnSemanticSearchAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(SemanticModeBarVisibility));
        OnPropertyChanged(nameof(SearchModeSplitButtonVisibility));
        OnPropertyChanged(nameof(SearchActionButtonVisibility));
        if (!_queryModeInitialized) return;
        // The AI-search toggle: persist, flip the translator live, and leave Semantic mode if turning off.
        _settings.SemanticSearchEnabled = value;
        _semanticTranslator?.SetEnabled(value);
        if (!value) IsSemanticQueryMode = false;
        _ = PersistSettingsAsync();
    }

    partial void OnSemanticDevicePreferenceOrderChanged(string value)
    {
        if (!_queryModeInitialized) return;
        _settings.SemanticDevicePreferenceOrder = value;
        _semanticTranslator?.SetDevicePreferenceOrder(value);
        _ = PersistSettingsAsync();
    }

    partial void OnFoundryModelUpdateAlertsEnabledChanged(bool value)
    {
        if (!_queryModeInitialized) return;
        _settings.FoundryModelUpdateAlertsEnabled = value;
        _ = PersistSettingsAsync();
    }

    partial void OnSemanticUnloadModelAfterUseChanged(bool value)
    {
        if (!_queryModeInitialized) return;
        _settings.SemanticUnloadModelAfterUse = value;
        _semanticTranslator?.SetUnloadAfterUse(value);
        _ = PersistSettingsAsync();
    }

    partial void OnTelemetryEnabledSettingChanged(bool value)
    {
        if (!_telemetryInitialized) return;
        Yagu.Services.Telemetry.TelemetryGate.TelemetryEnabled = value;
        _settings.TelemetryEnabled = value;
        if (value)
            Yagu.Services.Telemetry.TelemetryService.Instance.Initialize(EnsureTelemetryInstallId());
        _ = PersistSettingsAsync();
    }

    partial void OnBugReportingEnabledSettingChanged(bool value)
    {
        if (!_telemetryInitialized) return;
        Yagu.Services.Telemetry.TelemetryGate.BugReportingEnabled = value;
        _settings.BugReportingEnabled = value;
        if (value)
            Yagu.Services.Telemetry.BugReportService.Instance.Initialize(EnsureTelemetryInstallId());
        _ = PersistSettingsAsync();
    }

    partial void OnBugReportContactEmailChanged(string value)
    {
        if (!_telemetryInitialized) return;
        _settings.BugReportContactEmail = value ?? string.Empty;
        _ = PersistSettingsAsync();
    }

    partial void OnIsSemanticQueryModeChanged(bool value)
    {
        if (!value) SemanticStatusText = string.Empty;
        if (!_queryModeInitialized) return;
        _settings.LastQueryModeIsSemantic = value;
        _settings.HasChosenQueryMode = true;
        _ = PersistSettingsAsync();
    }

    partial void OnDefaultToTraditionalSearchModeChanged(bool value)
    {
        if (!_queryModeInitialized) return;
        _settings.DefaultToTraditionalSearchMode = value;
        // Re-evaluate the launch default only when the user hasn't already pinned a mode this
        // session; respecting an explicit choice avoids yanking the toggle out from under them.
        if (!_settings.HasChosenQueryMode)
            IsSemanticQueryMode = ResolveLaunchQueryMode();
        _ = PersistSettingsAsync();
    }

    /// <summary>Resolves the search bar's launch mode. An explicit prior choice wins; otherwise the
    /// hardware-based default applies (Semantic when accelerated and not overridden, else Traditional).</summary>
    private bool ResolveLaunchQueryMode()
    {
        if (!SemanticSearchAvailable) return false;
        if (_settings.HasChosenQueryMode)
            return _settings.LastQueryModeIsSemantic && SemanticHardwareAccelerated;
        return SemanticHardwareAccelerated && !_settings.DefaultToTraditionalSearchMode;
    }

    /// <summary>Detects accelerated hardware without ever letting a detector fault break startup.</summary>
    private bool SafeDetectAcceleratedHardware()
    {
        try { return _capabilityDetector.HasAcceleratedHardware(); }
        catch { return false; }
    }

    /// <summary>Runs a capability probe, swallowing any fault as "not present" so startup never breaks.</summary>
    private static bool SafeDetect(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }

    /// <summary>Reads the machine's dedicated GPU VRAM (bytes) for the larger-model auto-upgrade
    /// decision, swallowing any fault as 0 (unknown) so startup never breaks.</summary>
    private long SafeDetectGpuMemoryBytes()
    {
        try { return _capabilityDetector.GetMaxDedicatedGpuMemoryBytes(); }
        catch { return 0; }
    }

    [ObservableProperty] public partial int ContextLines { get; set; } = 3;
    [ObservableProperty] public partial int PreviewContextLines { get; set; } = 20;
    [ObservableProperty] public partial bool ObeyGitignore { get; set; }
    [ObservableProperty] public partial bool GitignoreTakesPrecedence { get; set; } = true;
    // null = unset (ask via dialog), true = .gitignore wins, false = Include filter wins.
    [ObservableProperty] public partial bool? GitignorePrecedencePreference { get; set; }
    [ObservableProperty] public partial string IncludeGlobs { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExcludeGlobs { get; set; } = string.Empty;
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
        ? @"e.g. \.(cs|xaml)$…"
        : "e.g. ts,js,py or *.cs…";
    public string ExcludeFilterPlaceholder => ExcludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. (^|/)node_modules/|\.min\.js$…"
        : $"e.g. {AppSettings.DefaultExcludeGlobs}…";

    // The exclude box shows greyed placeholder example text (e.g. "node_modules;bin;obj;.git")
    // when empty, but that text is ONLY an example — it is NOT applied. An empty box means
    // "no excludes": folders are excluded only when the user explicitly types them, matching the
    // include box. (Previously an empty box silently applied the example list as real excludes,
    // which hid files living in folders like bin/ that the user never chose to exclude.)
    private string EffectiveExcludeGlobsText => ExcludeGlobs ?? string.Empty;
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
    partial void OnThemeModeIndexChanged(int value)
        => AppThemeService.CurrentThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(value);
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
    // Long-line warning preference: 0 = Ask every time, 1 = Always open without word wrap, 2 = Always open with word wrap.
    [ObservableProperty] public partial int PreviewLongLineWarningIndex { get; set; }
    [ObservableProperty] public partial string ResultListMatchTextFontFamily { get; set; } = AppSettings.DefaultResultListMatchTextFontFamily;
    [ObservableProperty] public partial int ResultListMatchTextFontSize { get; set; } = AppSettings.DefaultResultListMatchTextFontSize;
    [ObservableProperty] public partial string ResultListMatchHighlightColor { get; set; } = AppSettings.DefaultResultListMatchHighlightColor;

    // ── File list overlay settings ──
    [ObservableProperty] public partial int FileListOverlayHeight { get; set; } = AppSettings.DefaultFileListOverlayHeight;
    [ObservableProperty] public partial int FileListOverlayFontSize { get; set; } = AppSettings.DefaultFileListOverlayFontSize;
    [ObservableProperty] public partial string FileListOverlayFontColor { get; set; } = AppSettings.DefaultFileListOverlayFontColor;
    [ObservableProperty] public partial string FileListOverlayFontFamily { get; set; } = AppSettings.DefaultFileListOverlayFontFamily;

    // ── Preview sticky file header overlay settings ──
    [ObservableProperty] public partial int PreviewStickyHeaderHeight { get; set; } = AppSettings.DefaultPreviewStickyHeaderHeight;
    [ObservableProperty] public partial int PreviewStickyHeaderFileNameFontSize { get; set; } = AppSettings.DefaultPreviewStickyHeaderFileNameFontSize;
    [ObservableProperty] public partial string PreviewStickyHeaderFileNameFontColor { get; set; } = AppSettings.DefaultPreviewStickyHeaderFileNameFontColor;
    [ObservableProperty] public partial string PreviewStickyHeaderFileNameFontFamily { get; set; } = AppSettings.DefaultPreviewStickyHeaderFileNameFontFamily;
    [ObservableProperty] public partial int PreviewStickyHeaderDetailFontSize { get; set; } = AppSettings.DefaultPreviewStickyHeaderDetailFontSize;
    [ObservableProperty] public partial string PreviewStickyHeaderDetailFontColor { get; set; } = AppSettings.DefaultPreviewStickyHeaderDetailFontColor;
    [ObservableProperty] public partial string PreviewStickyHeaderDetailFontFamily { get; set; } = AppSettings.DefaultPreviewStickyHeaderDetailFontFamily;

    // ── File list drawer label settings ──
    [ObservableProperty] public partial int DrawerFileNameFontSize { get; set; } = AppSettings.DefaultDrawerFileNameFontSize;
    [ObservableProperty] public partial string DrawerFileNameFontColor { get; set; } = AppSettings.DefaultDrawerFileNameFontColor;
    [ObservableProperty] public partial string DrawerFileNameFontFamily { get; set; } = AppSettings.DefaultDrawerFileNameFontFamily;
    [ObservableProperty] public partial int DrawerDirectoryFontSize { get; set; } = AppSettings.DefaultDrawerDirectoryFontSize;
    [ObservableProperty] public partial string DrawerDirectoryFontColor { get; set; } = AppSettings.DefaultDrawerDirectoryFontColor;
    [ObservableProperty] public partial string DrawerDirectoryFontFamily { get; set; } = AppSettings.DefaultDrawerDirectoryFontFamily;
    [ObservableProperty] public partial int DrawerMetadataFontSize { get; set; } = AppSettings.DefaultDrawerMetadataFontSize;
    [ObservableProperty] public partial string DrawerMetadataFontColor { get; set; } = AppSettings.DefaultDrawerMetadataFontColor;
    [ObservableProperty] public partial string DrawerMetadataFontFamily { get; set; } = AppSettings.DefaultDrawerMetadataFontFamily;

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

    // When the user picks a concrete precedence preference (dialog or Settings),
    // keep the effective runtime value in sync so the next search honors it immediately.
    partial void OnGitignorePrecedencePreferenceChanged(bool? value)
    {
        if (value is bool preference)
            GitignoreTakesPrecedence = preference;
    }

    public void ResetGitignorePrecedencePreference() => GitignorePrecedencePreference = null;

    [ObservableProperty] public partial int FileLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    [ObservableProperty] public partial int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    [ObservableProperty] public partial int ParallelismIndex { get; set; } = 4; // 0 = safe cap, 1 = 1 thread, 2 = half cores, 3 = 2x cores, 4 = all cores

    /// <summary>Streaming-scanner I/O worker oversubscription: 0 = Auto (SSD 1×, HDD 2×), 1 = 1×, 2 = 2×, 3 = 3×.</summary>
    [ObservableProperty] public partial int IoOversubscriptionIndex { get; set; }

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

    // When the user (or settings load) explicitly changes the persisted parallelism index, drop any
    // session-only HDD override so the user's choice takes effect.
    partial void OnParallelismIndexChanged(int value) => _sessionParallelismOverrideIndex = null;

    [ObservableProperty] public partial int LineTruncationLength { get; set; } = 500;
    [ObservableProperty] public partial int MaxRecentItems { get; set; } = 20;
    [ObservableProperty] public partial int MaxSemanticRecentItems { get; set; } = 20;
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
    [ObservableProperty] public partial int MaxMatchesPerLine { get; set; }
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

    [ObservableProperty] public partial bool SkipBinary { get; set; } = true;

    /// <summary>When true, search cloud-only (online-only) placeholder files by hydrating
    /// them on demand when a live provider is present; when false (default) they are
    /// skipped so the scan never blocks on hydration.</summary>
    [ObservableProperty] public partial bool SearchOnlineOnlyFiles { get; set; }

    /// <summary>When true (default), files and folders carrying the Windows Hidden
    /// attribute are included in the search; when false they are excluded. Seeded from
    /// the persisted <c>SearchHiddenFiles</c> setting and surfaced as the Advanced
    /// Options ▸ Content options toggle.</summary>
    [ObservableProperty] public partial bool SearchHiddenFiles { get; set; } = true;

    /// <summary>When true, raster image files (PNG/JPG/etc.) are OCR'd on a background queue and
    /// their recognized text is searched. Default false. Seeded from the persisted
    /// <c>SearchImageText</c> setting and surfaced as the Advanced Options ▸ Filters toggle.</summary>
    [ObservableProperty] public partial bool SearchImageText { get; set; }

    /// <summary>OCR engine used when <see cref="SearchImageText"/> is on: "paddle" (PaddleSharp) or
    /// "tesseract". Defaults to <see cref="AppSettings.EffectiveDefaultImageOcrEngine"/> (PaddleSharp on
    /// x64/Arm64; Tesseract on x86, where PaddleOCR's x64-only runtime cannot load). Settings-only.</summary>
    [ObservableProperty] public partial string ImageOcrEngine { get; set; } = AppSettings.EffectiveDefaultImageOcrEngine;

    /// <summary>PaddleSharp recognition model used for image OCR (e.g. "EnglishV4", "ChineseV5").
    /// Higher quality models trade speed for accuracy. Ignored by the Tesseract engine, which uses a
    /// fixed pipeline. Settings-only; configured on the OCR settings tab.</summary>
    [ObservableProperty] public partial string ImageOcrModel { get; set; } = AppSettings.DefaultImageOcrModel;

    /// <summary>Maximum detection resolution (longest image side, in pixels) for PaddleSharp OCR.
    /// Larger values find smaller text at the cost of speed; 0 means unlimited (use the image's
    /// native resolution). Settings-only; configured on the OCR settings tab.</summary>
    [ObservableProperty] public partial int ImageOcrMaxSide { get; set; } = AppSettings.DefaultImageOcrMaxSide;

    /// <summary>When true, the directory box is restored to <see cref="AppSettings.PinnedStartupDirectory"/>
    /// at launch; when false, the box starts empty (search all drives). Bound to the star toggle next to
    /// the Browse button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))]
    public partial bool PinStartupDirectory { get; set; }

    /// <summary>True only when the directory currently shown in the box IS the pinned startup directory.
    /// The star toggle's highlighted state binds to this (not to <see cref="PinStartupDirectory"/> alone),
    /// so switching the box to any other folder clears the highlight even though the pin remains saved;
    /// restoring the pinned folder lights it back up. Comparison is case-insensitive and ignores trailing
    /// path separators so <c>C:\foo</c> and <c>C:\foo\</c> are treated as the same folder.</summary>
    public bool IsCurrentDirectoryPinned =>
        PinStartupDirectory
        && !string.IsNullOrWhiteSpace(_settings.PinnedStartupDirectory)
        && string.Equals(
            (Directory ?? string.Empty).Trim().TrimEnd('\\', '/'),
            _settings.PinnedStartupDirectory!.Trim().TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);


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

        // Keep the binary-types dropdown consistent with the toggle. "Search binary" ON means "search all
        // binary types by default"; because BinaryExtensions is internally the SKIP list, that maps to an
        // EMPTY skip list (every type selected -> N/N shown). OFF restores the full skip list so content-only
        // mode still early-skips binary types (the dropdown is hidden in that state). Skipped during
        // construction, where the initial extension lists are seeded directly.
        if (!_binaryExtensionsInitialized) return;
        BinaryExtensions = value ? SettingsBinaryExtensions : string.Empty;
        SyncBinaryExtensionItems();
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
    [ObservableProperty] public partial int PreviewEditorPopOutMaxSizeMB { get; set; } = 100;
    [ObservableProperty] public partial int PreviewEditorPopOutArrangementIndex { get; set; }
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
    [ObservableProperty] public partial bool SuppressExcludedExtensionWarnings { get; set; }

    /// <summary>When the excluded-file-type warning is suppressed, whether to automatically INCLUDE the
    /// excluded type in the search (true) or search WITHOUT it (false). Set from the warning dialog's
    /// "Always do this" choice and from Settings. Only meaningful while <see cref="SuppressExcludedExtensionWarnings"/>
    /// is true.</summary>
    [ObservableProperty] public partial bool IncludeExcludedExtensionByDefault { get; set; }

    private bool _suppressEverythingNotRunningPrompt;
    public bool SuppressEverythingNotRunningPrompt
    {
        get => _suppressEverythingNotRunningPrompt;
        set => SetProperty(ref _suppressEverythingNotRunningPrompt, value);
    }
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

    /// <summary>Persists the embedded terminal's shell choice (0 = cmd, 1 = PowerShell) immediately,
    /// so switching shells via the terminal-pane dropdown survives a restart.</summary>
    public async Task SetTerminalShellKindIndexAsync(int index)
    {
        int normalized = TerminalShell.NormalizeSettingsIndex(index);
        TerminalShellKindIndex = normalized;
        _settings.TerminalShellKindIndex = normalized;
        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }

    [ObservableProperty] public partial string SearchResultTempDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasChosenSearchResultTempDirectory { get; set; }
    [ObservableProperty] public partial bool LimitParallelismOnHdd { get; set; } = true;
    [ObservableProperty] public partial bool SuppressHddParallelismWarnings { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesIncludesNetwork { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesIncludesRemovable { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesIncludesCloud { get; set; }
    [ObservableProperty] public partial bool SearchAllDrivesForceFullScan { get; set; }
    [ObservableProperty] public partial bool BackupBeforeSave { get; set; } = true;
    [ObservableProperty] public partial bool ShowEditorSavedOverlay { get; set; } = true;
    [ObservableProperty] public partial bool EditorSyntaxHighlightingEnabled { get; set; } = true;
    [ObservableProperty] public partial int WindowFocusBehavior { get; set; } = 1; // 0 = MinimizeToTray, 1 = StayOpen (default), 2 = AlwaysOnTop
    [ObservableProperty] public partial bool StartInLauncherMode { get; set; } = true;
    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;
    [ObservableProperty] public partial bool HasShownCloseToTrayNotification { get; set; }
    [ObservableProperty] public partial bool MaximizeOnStartup { get; set; }
    // 0 = Centered (default), 1 = Top Left, 2 = Top Middle, 3 = Top Right, 4 = Middle Left,
    // 5 = Middle Right, 6 = Bottom Left, 7 = Bottom Middle, 8 = Bottom Right.
    [ObservableProperty] public partial int LaunchWindowPosition { get; set; }
    // Compact launcher position; same anchors as LaunchWindowPosition but defaults to 2 = Top Middle.
    [ObservableProperty] public partial int LauncherWindowPosition { get; set; } = 2;
    [ObservableProperty] public partial int AdvancedOptionsCollapsedWidthModeIndex { get; set; }
    [ObservableProperty] public partial string TerminalDefaultWorkingDirectory { get; set; } = string.Empty;
    // 0 = Command Prompt (cmd.exe, default), 1 = PowerShell. Mirrors the terminal-pane shell dropdown.
    [ObservableProperty] public partial int TerminalShellKindIndex { get; set; }
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
    private bool _binaryExtensionsInitialized;

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
                    // "checked = search this binary type": an item is selected when it is NOT in the
                    // skip list. (Internally BinaryExtensions stays the skip list, so the search engine,
                    // CLI generator, and excluded-extension predictor are unaffected.)
                    BinaryExtensionItems.Add(new SkipExtensionItem(ext, group.Key, !enabled.Contains(ext)));
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
            // Selected items are the binary types to SEARCH; the skip list is the UNSELECTED ones.
            BinaryExtensions = string.Join(';', BinaryExtensionItems.Where(i => !i.IsEnabled).Select(i => i.Extension));
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

    /// <summary>
    /// When a semantic plan filters to archive-container extensions (e.g. .docx/.xlsx/.pptx, which are
    /// ZIP files), turn on "Search archives" and select those extensions in the archive-extensions
    /// list so their inner text is actually searched. Asking to search a container format implies
    /// searching inside it.
    /// </summary>
    private void EnableArchiveSearchForContainerGlobs(IReadOnlyList<string>? includeGlobs)
    {
        var toEnable = SemanticPlanApplier.GetArchiveExtensionsToEnable(
            includeGlobs,
            ArchiveExtensionCategories.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (toEnable.Count == 0)
            return;

        if (!SearchInsideArchives)
            SearchInsideArchives = true;

        var enabled = ParseExtensionSet(ArchiveExtensions);
        bool changed = false;
        foreach (var ext in toEnable)
            if (enabled.Add(ext))
                changed = true;

        if (changed)
            ArchiveExtensions = string.Join(';', enabled);

        SyncArchiveExtensionItems();
    }

    /// <summary>
    /// When a semantic plan explicitly filters to known-binary extensions (e.g. ".com"/".cpl"/".exe"),
    /// make those files findable AND show the intent: enable binary search and SELECT exactly the
    /// targeted binary types in the dropdown (so only they are searched; every other binary type stays
    /// skipped). Internally <see cref="BinaryExtensions"/> is the skip list, so the selection is the
    /// universe minus the targeted extensions. Changes are session-only — visible in Advanced Options
    /// until the next search resets them, and never written to the saved defaults.
    /// </summary>
    private void EnableBinarySearchForBinaryGlobs(IReadOnlyList<string>? includeGlobs)
    {
        var toEnable = SemanticPlanApplier.GetBinaryExtensionsToEnable(
            includeGlobs,
            ParseExtensionSet(AppSettings.DefaultBinaryExtensions));
        if (toEnable.Count == 0)
            return;

        if (!SearchBinary)
            SearchBinary = true;

        // Skip every binary type in the universe EXCEPT the targeted ones, so the dropdown shows only
        // the targeted extension(s) selected (= searched).
        var targeted = new HashSet<string>(toEnable, StringComparer.OrdinalIgnoreCase);
        var universe = ParseExtensionSet(SettingsBinaryExtensions);
        foreach (var ext in ParseExtensionSet(BinaryExtensions))
            universe.Add(ext);
        foreach (var ext in targeted)
            universe.Add(ext);

        var newSkip = string.Join(';', universe.Where(e => !targeted.Contains(e)));
        if (!string.Equals(BinaryExtensions, newSkip, StringComparison.OrdinalIgnoreCase))
        {
            BinaryExtensions = newSkip;
            SyncBinaryExtensionItems();
        }
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchModeSplitButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchActionButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(ProgressTooltip))]
    public partial bool IsSearching { get; set; }
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
            if (TotalFiles > 0)
            {
                // FilesScanned can momentarily exceed a slightly stale TotalFiles between 100 ms
                // snapshots; clamp so the tooltip never reads over 100%.
                double pct = Math.Min(100.0, (double)FilesScanned / TotalFiles * 100);
                return $"{pct:F1}% complete ({FilesScanned:N0} files out of {TotalFiles:N0} total files)";
            }
            // Total not yet known. A recursive enumeration of a large tree, or a search whose filters
            // exclude every file during discovery, can churn for minutes before a total is available —
            // show an active "discovering" state (with the running processed count when present) so a
            // long discovery never looks frozen on a static "Waiting for file list…".
            if (IsSearching)
            {
                int processed = Math.Max(FilesScanned, FilesSkipped);
                return processed > 0
                    ? $"Discovering files… ({processed:N0} found so far)"
                    : "Discovering files…";
            }
            return "Waiting for file list…";
        }
    }

    partial void OnFilesScannedChanged(int value) => OnPropertyChanged(nameof(ProgressTooltip));
    partial void OnTotalFilesChanged(int value) => OnPropertyChanged(nameof(ProgressTooltip));
    [ObservableProperty] public partial int MatchesFound { get; set; }
    [ObservableProperty] public partial int FilesSkipped { get; set; }
    [ObservableProperty] public partial bool HasPerformedSearch { get; set; }
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

    /// <summary>
    /// Raised when the active search is terminated because the result temp-file drive became too full.
    /// The argument is the user-facing termination message. The View surfaces this as a modal notice
    /// (with a link to the disk-space threshold setting) in addition to the inline status/error text.
    /// </summary>
    public event Action<string>? SearchTerminatedByLowDiskSpace;

    public ObservableCollection<FileGroup> ResultGroups => _resultCollection.VisibleGroups;
    public BatchObservableCollection<object> ResultRows { get; } = new();
    public ObservableCollection<string> RecentDirectories { get; } = [];
    public ObservableCollection<HistorySuggestion> DirectorySuggestions { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];
    /// <summary>Autocomplete history for the Semantic (natural-language) query mode, kept separate
    /// from <see cref="SearchHistory"/> so Traditional and Semantic suggestions never mix.</summary>
    public ObservableCollection<string> SemanticSearchHistory { get; } = [];

    private DateTimeOffset? LookupRecentDirectoryTimestamp(string value)
        => _settings.RecentDirectoryTimes.TryGetValue(value, out var t) ? t : null;

    /// <summary>
    /// Builds the query autocomplete dropdown items for the active mode (Semantic vs Traditional),
    /// filtered by <paramref name="filter"/> (substring, case-insensitive), annotated with each entry's
    /// last-used timestamp, and sorted newest-first. Entries without a timestamp (recorded before
    /// timestamps were tracked) sort to the end while preserving their existing relative order.
    /// </summary>
    public List<HistorySuggestion> BuildQuerySuggestionItems(string? filter)
    {
        var history = IsSemanticQueryMode ? SemanticSearchHistory : SearchHistory;
        var times = IsSemanticQueryMode ? _settings.SemanticSearchHistoryTimes : _settings.SearchHistoryTimes;

        string trimmed = filter?.Trim() ?? string.Empty;
        IEnumerable<string> values = trimmed.Length == 0
            ? history
            : history.Where(entry => entry.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        return values
            .Select((value, index) => (value, index, ts: times.TryGetValue(value, out var t) ? (DateTimeOffset?)t : null))
            .OrderByDescending(x => x.ts ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.index)
            .Select(x => new HistorySuggestion(x.value, x.ts))
            .ToList();
    }

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

    public Microsoft.UI.Xaml.Visibility SkippedCountVisibility =>
        HasPerformedSearch
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
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && (GroupMode == GroupMode.None || IsSearching))
        {
            ResultRows.AppendRange(e.NewItems.Cast<object>().ToList());
            return;
        }

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

            var lines = new StringBuilder();
            lines.AppendLine("Skipped files breakdown:");
            lines.AppendLine(ExtensionExclusionSkipNote);
            lines.AppendLine();
            if (b.GlobExcluded > 0)   lines.AppendLine(CultureInfo.InvariantCulture, $"  🚫  Glob exclusions       {b.GlobExcluded,8:N0}");
            if (b.GitignoreExcluded > 0) lines.AppendLine(CultureInfo.InvariantCulture, $"  🙈  .gitignore excluded   {b.GitignoreExcluded,8:N0}");
            if (b.CloudOnly > 0)      lines.AppendLine(CultureInfo.InvariantCulture, $"  ☁️  Cloud-only skipped    {b.CloudOnly,8:N0}");
            if (b.Binary > 0)         lines.AppendLine(CultureInfo.InvariantCulture, $"  🔒  Binary files          {b.Binary,8:N0}");
            if (b.ByExtension > 0)    lines.AppendLine(CultureInfo.InvariantCulture, $"  📄  Scanner extension skips {b.ByExtension,8:N0}");
            if (b.TooLarge > 0)       lines.AppendLine(CultureInfo.InvariantCulture, $"  📏  Too large             {b.TooLarge,8:N0}");
            if (b.AccessDenied > 0)   lines.AppendLine(CultureInfo.InvariantCulture, $"  🔐  Access denied         {b.AccessDenied,8:N0}");
            if (b.Directories > 0)    lines.AppendLine(CultureInfo.InvariantCulture, $"  📁  Inaccessible dirs     {b.Directories,8:N0}");
            if (b.IOError > 0)        lines.AppendLine(CultureInfo.InvariantCulture, $"  ⚠️  I/O errors            {b.IOError,8:N0}");
            if (b.NotFound > 0)       lines.AppendLine(CultureInfo.InvariantCulture, $"  ❓  Not found             {b.NotFound,8:N0}");
            if (b.Encoding > 0)       lines.AppendLine(CultureInfo.InvariantCulture, $"  🔤  Encoding errors       {b.Encoding,8:N0}");
            if (b.Other > 0)          lines.AppendLine(CultureInfo.InvariantCulture, $"  ❔  Other                 {b.Other,8:N0}");

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
    partial void OnHasPerformedSearchChanged(bool value) => OnPropertyChanged(nameof(SkippedCountVisibility));
    partial void OnFilesSkippedChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); OnPropertyChanged(nameof(ProgressTooltip)); }
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

    /// <summary>
    /// Entry point for an interactive search submission. In Semantic mode the natural-language
    /// query is first translated by the local model and applied to this view-model; in Traditional
    /// mode it goes straight to <see cref="StartSearchAsync"/>.
    /// </summary>
    public async Task SubmitSearchAsync(Func<Task<bool>>? postTranslationGate = null)
    {
        // Re-entrancy guard: a second submit (Enter in the query box, F5, a double-click on the
        // Search button) while a semantic translation is already in flight would start a concurrent
        // model inference on the same chat client and corrupt its output ("the model did not return
        // a JSON object"). Ignore additional submits until the translation finishes or is cancelled.
        if (IsTranslatingSemanticQuery) return;

        if (IsSemanticQueryMode && SemanticSearchAvailable)
        {
            // Clear any previous semantic search's resolved settings from Advanced Options back to the
            // saved defaults before this run; a new semantic search re-applies its own. This runs ONLY on
            // a Semantic submit — a Traditional search must NEVER read from or write to Advanced Options,
            // so whatever the user typed there (e.g. an include glob) is used verbatim and left untouched.
            ResetVisibleSemanticResolution();

            // Capture the typed NL text (translation overwrites Query) and snapshot the filter defaults.
            _pendingSemanticHistoryEntry = Query?.Trim();
            var defaultsSnapshot = CaptureSearchDefaults();
            var outcome = await TranslateSemanticQueryAsync().ConfigureAwait(true);
            if (outcome == SemanticTranslationOutcome.Aborted) return;
            if (outcome is SemanticTranslationOutcome.Applied or SemanticTranslationOutcome.Salvaged)
                _semanticDefaultsSnapshot = defaultsSnapshot; // armed: StartSearchAsync leaves the plan visible
            else
            {
                // No plan and nothing to salvage (e.g. a bare token like "#define") — fall back to a
                // plain Traditional search of the typed text. (A salvaged plan already set its own
                // "best guess" status inside TranslateSemanticQueryAsync.)
                ErrorText = string.Empty;
                // A trivially-literal query (e.g. "1") already set an accurate passthrough status inside
                // TranslateSemanticQueryAsync; only show the generic model-failure message when the
                // translator left the status blank.
                if (string.IsNullOrEmpty(SemanticStatusText))
                    SemanticStatusText = "AI couldn't interpret that — searching for the text directly.";
            }
        }

        try
        {
            // Run an optional pre-search gate AFTER any semantic translation, so it sees the resolved
            // search target (include globs / literal query) the model produced rather than the raw
            // natural-language text. Used for the excluded-extension warning.
            if (postTranslationGate is not null && !await postTranslationGate().ConfigureAwait(true))
                return;

            await StartSearchAsync().ConfigureAwait(true);
        }
        finally
        {
            // If the run didn't reach the commit point in StartSearchAsync (gate cancelled, or an early
            // validation error returned), revert the plan now — a cancelled semantic search should not
            // leave its resolution behind. A committed search sets _semanticResolutionVisible and is left
            // visible on purpose (reset at the start of the next search).
            if (_semanticDefaultsSnapshot is { } leftover && !_semanticResolutionVisible)
            {
                RestoreSearchDefaults(leftover);
                _semanticDefaultsSnapshot = null;
                await PersistSettingsAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>Cancels an in-flight semantic translation (the local-model inference that turns a
    /// natural-language query into search settings). Wired to the morphing Cancel button so a user
    /// can abort the AI step the same way they cancel a running file search.</summary>
    public void CancelSemanticTranslation()
    {
        try { _semanticCts?.Cancel(); } catch { }
        SemanticStatusText = string.Empty;
    }

    /// <summary>Outcome of <see cref="TranslateSemanticQueryAsync"/>.</summary>
    public enum SemanticTranslationOutcome
    {
        /// <summary>The model's plan was applied to this view-model; run the semantic search.</summary>
        Applied,
        /// <summary>The model produced no usable plan, but a deterministic best-guess salvage was applied
        /// from the raw query (file types, content term, OCR, hidden, folder). Run it like a normal plan;
        /// the status line tells the user it is a best guess.</summary>
        Salvaged,
        /// <summary>The model could not produce a usable plan; the caller may fall back to a literal search.</summary>
        Failed,
        /// <summary>Translation was cancelled or there was nothing to translate; do not search.</summary>
        Aborted,
    }

    /// <summary>
    /// Translates the current natural-language <see cref="Query"/> into concrete search settings via
    /// the local model and applies them to this view-model. Returns <see cref="SemanticTranslationOutcome.Applied"/>
    /// when settings were applied, <see cref="SemanticTranslationOutcome.Failed"/> when the model produced no
    /// usable plan (caller may fall back to a literal search), and <see cref="SemanticTranslationOutcome.Aborted"/>
    /// when the user cancelled or there was nothing to translate.
    /// </summary>
    public async Task<SemanticTranslationOutcome> TranslateSemanticQueryAsync()
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return SemanticTranslationOutcome.Failed;

        var text = Query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            ErrorText = "Describe what you want to find.";
            return SemanticTranslationOutcome.Aborted;
        }

        // A trivially-literal query — a single character, a bare number, or a short symbol token like
        // "1" — is not natural language. Small on-device models tend to hallucinate a plan (often
        // echoing a prompt example) for such input, so skip the model entirely and let the caller run a
        // plain traditional search of the typed text (what the user means). Set an accurate status so
        // the caller's generic "AI couldn't interpret that" message is not shown.
        if (SemanticQuerySalvage.IsTrivialLiteralQuery(text))
        {
            SemanticStatusText = $"\u201C{text}\u201D isn't a natural-language query \u2014 searching for it directly.";
            return SemanticTranslationOutcome.Failed;
        }

        try { _semanticCts?.Cancel(); } catch { }
        _semanticCts?.Dispose();
        _semanticCts = new CancellationTokenSource();
        var token = _semanticCts.Token;

        IsTranslatingSemanticQuery = true;
        SemanticStatusText = "Preparing the local AI model…";
        ErrorText = string.Empty;

        var progress = new Progress<SemanticTranslationProgress>(p =>
        {
            if (!token.IsCancellationRequested) SemanticStatusText = p.Message;
        });

        try
        {
            var context = new SemanticTranslationContext
            {
                Now = DateTimeOffset.Now,
                // Do NOT seed the model with the current box value: the directory must reflect ONLY what
                // the model interprets. A confidently-named path is applied below; anything else leaves
                // the directory box exactly as the user left it.
                DefaultDirectory = null,
                OriginalQuery = text,
                // A model-hallucinated directory that does not exist is treated as "no confident path"
                // (dropped to null), so the directory box is left unchanged rather than pointed at a
                // bogus location.
                DirectoryExists = static d => System.IO.Directory.Exists(d),
            };

            // Run the translation on a background thread. The translator's first-call initialization
            // (Foundry catalog/EP setup, model selection and load) runs SYNCHRONOUSLY up to its first
            // real await — the init SemaphoreSlim.WaitAsync completes inline when uncontended — so calling
            // it directly would block the UI thread on the first semantic search of each launch, delaying
            // the just-set query text from painting. Task.Run keeps that one-time cost off the UI thread;
            // progress still marshals back via the captured context, and ConfigureAwait(true) resumes here
            // on the UI thread to apply the plan.
            var result = await Task.Run(
                () => _semanticTranslator.TranslateAsync(text, context, progress, token), token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                SemanticStatusText = string.Empty;
                return SemanticTranslationOutcome.Aborted;
            }

            if (!result.Success || result.Plan is null)
            {
                // The model returned no usable plan (small on-device models often do this for bare
                // code tokens like "#define", and phi-mini has narrow quirks such as failing "jpg files
                // containing the word secret"). Before dropping to a bare literal search, try a
                // DETERMINISTIC best-guess salvage that rebuilds the obvious parts of the query — file
                // types, a content term, image OCR, hidden-file preference, a known folder — with the
                // same rules the model is taught. When it recovers something, apply it and tell the user
                // it is a best guess; otherwise fall through to the literal fallback.
                if (SemanticQuerySalvage.TryBuildPlan(text, out var salvagePlan))
                {
                    var salvaged = SemanticPlanApplier.ApplyToTarget(salvagePlan, context, this);
                    EnableArchiveSearchForContainerGlobs(salvaged.IncludeGlobs);
                    EnableBinarySearchForBinaryGlobs(salvaged.IncludeGlobs);
                    SemanticStatusText = "AI couldn't interpret that — using our best guess: "
                        + SemanticPlanApplier.BuildExplanation(salvaged, Directory);
                    return SemanticTranslationOutcome.Salvaged;
                }

                SemanticStatusText = string.Empty;
                return SemanticTranslationOutcome.Failed;
            }

            var resolved = SemanticPlanApplier.ApplyToTarget(result.Plan, context, this);
            // Adopt the directory ONLY when the model confidently named one (ApplyToTarget already set it
            // above in that case). When the query does not clearly contain a path, leave the directory box
            // exactly as the user left it instead of clearing it — clearing would silently widen the search
            // to all drives. The HDD check still runs against whatever location is in the box, via the
            // post-translation gate in SubmitSearchAsync.
            EnableArchiveSearchForContainerGlobs(resolved.IncludeGlobs);
            EnableBinarySearchForBinaryGlobs(resolved.IncludeGlobs);
            // Render the summary deterministically from the resolved plan rather than the model's
            // free-text explanation, which small on-device models often garble (e.g. "yagursd").
            // Pass the live directory box as the effective directory so an unscoped query (the model
            // resolves no directory) is described as the box's location — not the misleading "all
            // drives" — since the actual search honors whatever is in the box.
            string interpretation = SemanticPlanApplier.BuildExplanation(resolved, Directory);
            // Surface any warnings the plan raised (e.g. an unsupported content exclusion like "but not
            // X", or an exclusion that would have removed all matches) so the user knows part of the
            // request was not honored instead of silently dropping it. The CLI already prints these.
            if (resolved.Warnings.Count > 0)
                interpretation += "  \u26A0 " + string.Join("  \u26A0 ", resolved.Warnings);
            SemanticStatusText = interpretation;
            return SemanticTranslationOutcome.Applied;
        }
        catch (OperationCanceledException)
        {
            SemanticStatusText = string.Empty;
            return SemanticTranslationOutcome.Aborted;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("SemanticSearch", $"Translation failed: {ex.Message}", ex);
            SemanticStatusText = string.Empty;
            return SemanticTranslationOutcome.Failed;
        }
        finally
        {
            IsTranslatingSemanticQuery = false;
        }
    }

    /// <summary>Enumerates the locally-runnable model options for the first-run download prompt.</summary>
    public Task<IReadOnlyList<SemanticModelOption>> GetSemanticModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return Task.FromResult<IReadOnlyList<SemanticModelOption>>(Array.Empty<SemanticModelOption>());
        return _semanticTranslator.ListModelOptionsAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Resolves the human-readable name of the model that AI search will actually use right now, for
    /// display in Settings: a pinned override by name, else the loaded automatic model, else the
    /// recommended automatic model (resolved by querying the catalog). Falls back to a generic label on
    /// any failure. Does NOT change any state or reset the cache.
    /// </summary>
    public async Task<string> ResolveCurrentSemanticModelDisplayAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        // A pinned override, or an already-loaded automatic model, is authoritative and needs no query.
        if (!string.IsNullOrWhiteSpace(SemanticModelAlias))
            return SemanticModelAlias;
        string? loaded = (_semanticTranslator as FoundryLocalSemanticQueryTranslator)?.SelectedModelAlias;
        if (!string.IsNullOrWhiteSpace(loaded))
            return $"{loaded} (automatic)";

        // Automatic mode with nothing loaded yet: resolve the recommended model from the catalog.
        try
        {
            var options = await GetSemanticModelOptionsAsync(progress, cancellationToken).ConfigureAwait(true);
            var recommended = options.FirstOrDefault(o => o.IsRecommended);
            if (recommended is not null && !string.IsNullOrWhiteSpace(recommended.Alias))
                return $"{recommended.Alias} (automatic)";
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to the generic label */ }

        return "Automatic (recommended for your hardware)";
    }

    /// <summary>
    /// Clears the cached Foundry Local model catalog and loaded model (picking up models downloaded or
    /// updated out of band), then re-resolves and returns the current model's display name. Used by the
    /// "Refresh Foundry cache" button in Settings.
    /// </summary>
    public async Task<string> RefreshFoundryCacheAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        _semanticTranslator?.RefreshCatalog();
        OnPropertyChanged(nameof(CurrentSemanticModelDisplay));
        return await ResolveCurrentSemanticModelDisplayAsync(progress, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Downloads and selects the given semantic model, persisting it as the chosen model.</summary>
    public async Task PrepareSemanticModelAsync(
        string? modelAlias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            throw new InvalidOperationException("Semantic search is not available on this machine.");

        await _semanticTranslator.PrepareModelAsync(modelAlias, progress, cancellationToken).ConfigureAwait(true);

        _settings.SemanticModelAlias = modelAlias?.Trim() ?? string.Empty;
        SemanticModelAlias = _settings.SemanticModelAlias;
        _settings.SemanticModelDownloaded = true;
        OnPropertyChanged(nameof(IsSemanticModelDownloaded));
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Reverts to automatic (recommended) model selection. Applied live — the next semantic
    /// search re-selects the best model for the current hardware and device order — and persisted.</summary>
    public async Task ClearSemanticModelOverrideAsync()
    {
        _semanticTranslator?.SetModelOverride(null);
        _settings.SemanticModelAlias = string.Empty;
        SemanticModelAlias = string.Empty;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    // ── First-run AI-model qualification ──

    /// <summary>True when the one-time first-run AI-model qualification should be offered: AI (Semantic)
    /// search is available/enabled and the sweep has not been run yet.</summary>
    public bool ShouldOfferSemanticModelQualification =>
        SemanticModelQualificationCoordinator.ShouldOffer(_settings, SemanticSearchAvailable);

    /// <summary>
    /// Runs the first-run model-qualification sweep against this machine: enumerates the runnable models,
    /// probes each with a mix of simple and complex queries (<see cref="SemanticProbeSet.Default"/>), and
    /// returns the qualified model (if any), a best-effort fallback, and per-candidate reports. The user's
    /// chosen <paramref name="thresholds"/> decide how long to wait for a model to load and how slow a
    /// query may be before a candidate is abandoned. The sweep may download models and run inference, so
    /// it can take minutes — honor <paramref name="cancellationToken"/> so the user can cancel. Probing is
    /// in-process for now; a crashy model that faults with a managed exception is abandoned, but a hard
    /// native abort still ends the app until the out-of-process worker lands.
    /// </summary>
    public async Task<ModelQualificationResult> RunSemanticModelQualificationAsync(
        ModelQualificationThresholds thresholds,
        IProgress<SemanticQualificationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            throw new InvalidOperationException("Semantic search is not available on this machine.");

        // The runner prepares each candidate once and warms it up so every TIMED probe measures steady-
        // state inference latency. The "release model from memory after each search" setting (ON by
        // default) defeats that: it unloads the model after EVERY inference, so each timed probe reloads
        // the model from scratch inside its own timed window (~5-6s for a 14B model like phi-4), inflating
        // per-probe latency past the per-query limit and disqualifying otherwise-accurate large models as
        // "too slow". Keep each candidate's model resident across its probes for the sweep — the runner
        // already unloads the previous candidate before loading the next, so only one model is ever
        // resident — then restore the user's setting (and free VRAM) afterwards.
        bool restoreUnloadAfterUse = _settings.SemanticUnloadModelAfterUse;
        _semanticTranslator.SetUnloadAfterUse(false);
        try
        {
            var runner = new SemanticModelQualificationRunner(
                _semanticTranslator,
                defaultDirectory: null,
                directoryExists: System.IO.Directory.Exists,
                maxCandidates: SemanticModelQualificationRunner.DefaultMaxCandidates,
                failedProbeHoldMs: SemanticModelQualificationRunner.DefaultFailedProbeHoldMs);
            return await runner.RunAsync(SemanticProbeSet.Default, thresholds, progress, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _semanticTranslator.SetUnloadAfterUse(restoreUnloadAfterUse);
            if (restoreUnloadAfterUse)
            {
                // The user wants the model released when idle; the sweep left one resident. Free it.
                try { await _semanticTranslator.UnloadCurrentModelAsync(CancellationToken.None).ConfigureAwait(true); }
                catch { /* best-effort: freeing VRAM must never fail the sweep result */ }
            }
        }
    }

    /// <summary>
    /// Folds a finished qualification sweep into settings and, when the user accepts a model, selects it
    /// live and persists it. Pass the user's override as <paramref name="chosenAlias"/>; null accepts the
    /// sweep's recommendation. Marks the one-time check complete either way.
    /// </summary>
    public async Task ApplySemanticModelQualificationAsync(
        ModelQualificationResult result, bool accepted, string? chosenAlias = null)
    {
        SemanticModelQualificationCoordinator.ApplyResult(_settings, result, accepted, chosenAlias);

        // Reflect the (possibly new) effective model in the UI + translator.
        SemanticModelAlias = _settings.SemanticModelAlias;
        _semanticTranslator?.SetModelOverride(
            string.IsNullOrWhiteSpace(_settings.SemanticModelAlias) ? null : _settings.SemanticModelAlias);
        OnPropertyChanged(nameof(CurrentSemanticModelDisplay));
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Marks the first-run model check as declined (so it is not re-offered) without selecting a
    /// model. Use for an explicit "skip"; a plain "not now" should leave settings untouched so the offer
    /// returns next launch.</summary>
    public async Task DeclineSemanticModelQualificationAsync()
    {
        SemanticModelQualificationCoordinator.MarkDeclined(_settings);
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>The user refused the first-run model check. Because AI (Semantic) search needs a model
    /// that was validated on this PC to be reliable, turn the feature OFF and mark the one-time check
    /// complete so re-enabling it later (from Settings) does not re-offer the sweep. The user can opt back
    /// in and pick a model themselves — at their own risk — from the AI settings tab.</summary>
    public async Task DeclineAndDisableSemanticSearchAsync()
    {
        // Mark the check complete first so the persist triggered by the toggle below already carries it.
        SemanticModelQualificationCoordinator.MarkDeclined(_settings);
        // Turning the toggle off persists SemanticSearchEnabled=false and disables the translator live.
        SemanticSearchAvailable = false;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>True once the first-run model check has run (or was declined) or a model has been recorded,
    /// i.e. there is qualification state that <see cref="ResetSemanticModelQualificationAsync"/> would
    /// clear. Used to enable/disable the Developer Options "reset" button.</summary>
    public bool HasSemanticModelQualificationState =>
        _settings.SemanticModelQualificationCompleted
        || !string.IsNullOrEmpty(_settings.SemanticQualifiedModelAlias)
        || !string.IsNullOrEmpty(_settings.SemanticModelAlias);

    /// <summary>Developer action: clear the first-run AI-model qualification back to a fresh-install state
    /// and re-enable AI (Semantic) search, so the model check is offered again on the next startup. Forgets
    /// the recommended and selected model so the re-run starts from the automatic pick.</summary>
    public async Task ResetSemanticModelQualificationAsync()
    {
        SemanticModelQualificationCoordinator.Reset(_settings);
        // Re-enable AI search so ShouldOfferSemanticModelQualification returns true on the next launch.
        SemanticSearchAvailable = true;
        // Drop any live model override so the re-run sweep starts from the automatic pick.
        SemanticModelAlias = string.Empty;
        _semanticTranslator?.SetModelOverride(null);
        OnPropertyChanged(nameof(CurrentSemanticModelDisplay));
        OnPropertyChanged(nameof(HasSemanticModelQualificationState));
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Records that the user approved the one-time OCR component download. Sets the in-process
    /// gate (so concurrent OCR inits proceed) and persists the consent so the warning is shown at most
    /// once across sessions.</summary>
    public async Task MarkOcrDownloadConsentedAsync()
    {
        Yagu.Services.Ocr.OcrDownloadGate.ConsentGranted = true;
        _settings.OcrDownloadConsented = true;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Ensures a stable, non-PII install identifier exists (generating one on first need) and
    /// returns it. Used to tag telemetry and bug reports without identifying the user or machine.</summary>
    private string EnsureTelemetryInstallId()
    {
        if (string.IsNullOrEmpty(_settings.TelemetryInstallId))
            _settings.TelemetryInstallId = Guid.NewGuid().ToString("N");
        return _settings.TelemetryInstallId;
    }

    /// <summary>Records the user's first-run telemetry/bug-report choices (independently), applies them
    /// live to the gate and senders, reflects them in the Settings toggles, and persists. Marks the
    /// consent prompt as shown so it is never displayed again, regardless of the choices.</summary>
    public async Task MarkTelemetryConsentAsync(bool telemetryEnabled, bool bugReportingEnabled)
    {
        _settings.TelemetryConsentPromptShown = true;
        _settings.TelemetryEnabled = telemetryEnabled;
        _settings.BugReportingEnabled = bugReportingEnabled;
        OnPropertyChanged(nameof(TelemetryConsentPromptShown));

        string installId = EnsureTelemetryInstallId();
        Yagu.Services.Telemetry.TelemetryGate.TelemetryEnabled = telemetryEnabled;
        Yagu.Services.Telemetry.TelemetryGate.BugReportingEnabled = bugReportingEnabled;
        if (telemetryEnabled)
            Yagu.Services.Telemetry.TelemetryService.Instance.Initialize(installId);
        if (bugReportingEnabled)
            Yagu.Services.Telemetry.BugReportService.Instance.Initialize(installId);

        // Reflect into the Settings-panel toggles without re-triggering a persist per toggle.
        _telemetryInitialized = false;
        TelemetryEnabledSetting = telemetryEnabled;
        BugReportingEnabledSetting = bugReportingEnabled;
        _telemetryInitialized = true;

        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Persists the contact email the user supplied in a bug report so it pre-fills next time.</summary>
    public Task SetBugReportContactEmailAsync(string email)
    {
        BugReportContactEmail = (email ?? string.Empty).Trim();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks the Foundry Local catalog for newly-available, updated, or variant text-chat models and
    /// returns the ones the user has not seen, so the caller can show a one-time alert. Self-gating: it
    /// no-ops (returns empty) when alerts are disabled, semantic search is off/unavailable, the user has
    /// never used semantic search (so a catalog query would needlessly initialize Foundry), or it was
    /// already checked within <see cref="FoundryModelUpdateChecker.DefaultCheckInterval"/>. The very first
    /// successful check silently seeds the baseline and returns empty. Persists the refreshed baseline and
    /// check time. Failures (offline, etc.) are swallowed and leave the baseline unchanged.
    /// </summary>
    public async Task<IReadOnlyList<FoundryModelChange>> CheckForNewFoundryModelsAsync(CancellationToken cancellationToken)
    {
        var none = (IReadOnlyList<FoundryModelChange>)Array.Empty<FoundryModelChange>();

        if (!FoundryModelUpdateAlertsEnabled || !_settings.SemanticSearchEnabled || !_settings.SemanticModelDownloaded)
            return none;
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return none;
        if (!FoundryModelUpdateChecker.ShouldCheck(
                _settings.LastFoundryModelCheckUtc, DateTimeOffset.UtcNow, FoundryModelUpdateChecker.DefaultCheckInterval))
            return none;

        try
        {
            var options = await _semanticTranslator.ListModelOptionsAsync(null, cancellationToken).ConfigureAwait(true);
            var currentModels = options
                .Where(o => !string.IsNullOrEmpty(o.Id))
                .Select(o => new FoundryModelDescriptor(o.Id!, o.Alias, o.DeviceLabel, o.SizeBytes))
                .ToList();

            // An empty/failed catalog query must not clobber the baseline (it would mask real models
            // next time, or — on the very first run — seed an empty baseline).
            if (currentModels.Count == 0)
                return none;

            bool hasBaseline = _settings.LastFoundryModelCheckUtc is not null || _settings.KnownFoundryModelIds.Count > 0;
            var result = FoundryModelUpdateChecker.Detect(_settings.KnownFoundryModelIds, currentModels, hasBaseline);

            _settings.KnownFoundryModelIds = result.CurrentIds.ToList();
            _settings.LastFoundryModelCheckUtc = DateTimeOffset.UtcNow;
            if (result.Changes.Count > 0)
                _settings.LastFoundryModelAlertUtc = DateTimeOffset.UtcNow;
            await PersistSettingsAsync().ConfigureAwait(true);

            LogService.Instance.Info("SemanticSearch",
                $"Foundry model update check: {currentModels.Count} catalog model(s), {result.Changes.Count} new, baselineSeeded={result.BaselineSeeded}.");
            return result.Changes;
        }
        catch (OperationCanceledException)
        {
            return none;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("SemanticSearch", $"Foundry model update check failed: {ex.Message}", ex);
            return none;
        }
    }

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

    /// <summary>Re-enables the literal-"\n" multiline suggestion prompt after the user dismissed it
    /// (Developer Options → Reminders and Warnings reset). Persists so the reset survives a restart.</summary>
    public async Task ResetMultilineNewlineSuggestionAsync()
    {
        _settings.MultilineNewlineSuggestionDismissed = false;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Resolves the directory roots a search will target. When the <see cref="Directory"/> box has a
    /// value, that single directory is used; when it is empty the user is asking to "search all
    /// drives", so every eligible drive root is returned (fixed always; network/removable/cloud per
    /// the corresponding settings). An empty result means there is nothing to search.
    /// </summary>
    public IReadOnlyList<string> ResolveTargetRoots()
    {
        if (!string.IsNullOrWhiteSpace(Directory))
            return [Directory.Trim()];

        return DriveEnumerator.GetSearchRoots(
            SearchAllDrivesIncludesNetwork,
            SearchAllDrivesIncludesRemovable,
            SearchAllDrivesIncludesCloud);
    }

    [RelayCommand]
    public async Task StartSearchAsync()
    {
        // A complete file path typed into the Traditional search box (and nothing else) is a request
        // to show exactly that file, regardless of the Directory box. Detect and short-circuit here,
        // before any directory validation, so the Directory box never affects this lookup.
        if (!IsSemanticQueryMode && Yagu.Helpers.SingleFilePathQueryDetector.Resolve(Query) is { } singleFilePath)
        {
            await RunSingleFilePathDisplayAsync(singleFilePath).ConfigureAwait(true);
            return;
        }

        bool directorySpecified = !string.IsNullOrWhiteSpace(Directory);
        if (directorySpecified && !System.IO.Directory.Exists(Directory))
        {
            ErrorText = $"Directory does not exist: {Directory}";
            return;
        }
        // An empty directory means "search all drives" — resolve the eligible roots now.
        var targetRoots = ResolveTargetRoots();
        if (targetRoots.Count == 0)
        {
            ErrorText = "No drives are available to search.";
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

        int runId = Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);

        await _searchLifecycleGate.WaitAsync();

        CancellationTokenSource? cts = null;
        Task? lowDiskMonitorTask = null;
        try
        {
            if (runId != Volatile.Read(ref _searchRunId))
                return;

            ResetStateForNewSearch();

            if (directorySpecified)
                SettingsService.PushRecent(_settings.RecentDirectories, _settings.RecentDirectoryTimes, Directory, MaxRecentItems);
            // In Semantic mode the user-typed natural-language query (captured before translation)
            // goes to the separate Semantic history; Traditional searches use the literal Query.
            if (IsSemanticQueryMode)
            {
                if (!string.IsNullOrWhiteSpace(_pendingSemanticHistoryEntry))
                    SettingsService.PushRecent(_settings.SemanticSearchHistory, _settings.SemanticSearchHistoryTimes, _pendingSemanticHistoryEntry!, MaxSemanticRecentItems);
            }
            else
            {
                SettingsService.PushRecent(_settings.SearchHistory, _settings.SearchHistoryTimes, Query, MaxRecentItems);
            }
            _pendingSemanticHistoryEntry = null;
            SyncRecent();

            var effectiveSkipExtensions = BuildEffectiveSkipExtensionSet();

            int baseParallelism = ResolveParallelism(_sessionParallelismOverrideIndex ?? ParallelismIndex);
            // One-shot HDD parallelism override chosen in the warning dialog; applies to this search
            // only. Consume it now so it never leaks into a later search.
            int? hddParallelismOverride = _hddParallelismOverrideIndexForNextSearch;
            _hddParallelismOverrideIndexForNextSearch = null;
            SearchOptions BuildOptionsForRoot(string dir, int parallelism, FileListerBackend? backendOverride) => new SearchOptions
            {
                Directory = dir,
                Query = Query,
                CaseSensitive = CaseSensitive,
                UseRegex = UseRegex,
                ExactMatch = ExactMatch,
                Multiline = Multiline,
                MultilineDotAll = MultilineDotAll,
                MultilineEngine = (MultilineEngineKind)_settings.MultilineEngine,
                ContextLines = ContextLines,
                SearchMode = (SearchMode)SearchModeIndex,
                IncludeGlobs = SplitFilterPatterns(IncludeGlobs, IncludeFilterMode),
                ExcludeGlobs = SplitFilterPatterns(EffectiveExcludeGlobsText, ExcludeFilterMode),
                IncludeFilterMode = IncludeFilterMode,
                ExcludeFilterMode = ExcludeFilterMode,
                MinFileSizeBytes = effectiveMinFileSizeBytes,
                MaxFileSizeBytes = effectiveMaxFileSizeBytes,
                CreatedAfterDate = CreatedAfterDate,
                CreatedBeforeDate = CreatedBeforeDate,
                ModifiedAfterDate = ModifiedAfterDate,
                ModifiedBeforeDate = ModifiedBeforeDate,
                MaxResults = MaxResults,
                MaxMatchesPerLine = MaxMatchesPerLine,
                AbsoluteMaxResults = AbsoluteMaxResults,
                SkipBinary = SkipBinary,
                SearchOnlineOnlyFiles = SearchOnlineOnlyFiles,
                SearchHiddenFiles = SearchHiddenFiles,
                ObeyGitignore = ObeyGitignore,
                GitignoreTakesPrecedence = GitignoreTakesPrecedence,
                SkipExtensions = effectiveSkipExtensions,
                SearchInsideArchives = SearchInsideArchives,
                ArchiveExtensions = ParseDottedExtensionSet(ArchiveExtensions),
                SearchImageText = SearchImageText,
                ImageOcrExtensions = ParseExtensionSet(AppSettings.DefaultImageOcrExtensions),
                ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),
                ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel),
                ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide),
                MaxDegreeOfParallelism = parallelism,
                FileListerBackendOverride = backendOverride,
                IoOversubscriptionIndex = IoOversubscriptionIndex,
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
                ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths,
                MaxSearchDepth = double.IsNaN(MaxSearchDepth) ? 0 : (int)MaxSearchDepth,
                DegradedResultStore = _resultStore,
            };

            // One options set per target root. When searching all drives, each root gets its own
            // parallelism: HDD roots are forced to 1 (avoid thrashing) while other drives use the
            // configured value. Backend stays Auto so each root uses the fast Everything index when
            // it covers that drive (including drives the user added manually in Everything's settings)
            // and automatically falls back to the managed walker only for drives Everything does not
            // index — except when "force full scan" is enabled, which walks every drive directly.
            var perRootOptions = new List<SearchOptions>(targetRoots.Count);
            FileListerBackend? allDrivesBackendOverride =
                (!directorySpecified && SearchAllDrivesForceFullScan) ? FileListerBackend.Managed : null;
            foreach (var root in targetRoots)
            {
                int parallelism = baseParallelism;
                if (LimitParallelismOnHdd && Yagu.Helpers.DiskTypeDetector.IsHardDisk(root))
                    parallelism = hddParallelismOverride is int overrideIndex ? ResolveParallelism(overrideIndex) : 1;
                perRootOptions.Add(BuildOptionsForRoot(root, parallelism, allDrivesBackendOverride));
            }

            // Capture the parameters THIS search actually ran with, for preview/editor match
            // highlighting (the model's resolved literal pattern + flags).
            LastSearchPattern = Query;
            LastSearchCaseSensitive = CaseSensitive;
            LastSearchUseRegex = UseRegex;
            LastSearchExactMatch = ExactMatch;
            LastSearchMultiline = Multiline;
            LastSearchMultilineDotAll = MultilineDotAll;

            // A semantic plan's resolved settings stay applied to this view-model so they are VISIBLE in
            // Advanced Options (the user wanted to see what the AI search applied). They are NOT written
            // to the saved defaults: while the resolution is visible, PersistSettingsAsync persists the
            // pre-search defaults from the snapshot instead; the next search resets the view-model back
            // to those defaults. (Traditional searches have no snapshot and persist their own values.)
            if (_semanticDefaultsSnapshot is not null)
                _semanticResolutionVisible = true;
            await PersistSettingsAsync();

            cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;
            lowDiskMonitorTask = StartLowDiskSpaceMonitor(runId, cts, _resultStore);
            LogService.Instance.Warning("Search", $"Starting search #{runId}: query='{Query}', dir='{(directorySpecified ? Directory : $"<all drives: {targetRoots.Count}>")}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}");

            // Yield to the UI message pump periodically so the app stays responsive
            // when the events channel is draining many buffered items synchronously.
            // Without this, the await foreach completes synchronously for thousands of
            // already-buffered items, starving the WinUI message pump and freezing the UI.
            long yieldTimestamp = Stopwatch.GetTimestamp();
            // Yield about twice per frame (not once) so the UI thread gets frequent breathing room to
            // render smooth scrolling of the results list while heavy result batches stream in.
            long yieldIntervalTicks = Stopwatch.Frequency / 120; // ~8ms

            // UI consumer diagnostics
            long uiEventsReceived = 0;
            long uiMatchesReceived = 0;
            long uiYieldCount = 0;
            long uiLastLogTicks = Stopwatch.GetTimestamp();
            long uiLastStatusRefreshTicks = uiLastLogTicks;
            const long UiLogIntervalSec = 10;
            long uiStatusRefreshIntervalTicks = Stopwatch.Frequency / 4;
            var uiEventSw = new Stopwatch();

            void RefreshStatusFromReceivedMatches(bool force = false)
            {
                long statusNow = Stopwatch.GetTimestamp();
                if (!force && statusNow - uiLastStatusRefreshTicks < uiStatusRefreshIntervalTicks)
                    return;

                uiLastStatusRefreshTicks = statusNow;
                int receivedMatches = ClampMatchCount(uiMatchesReceived);
                if (receivedMatches > MatchesFound)
                    MatchesFound = receivedMatches;
                UpdateFilesPerSecond();
            }

            await foreach (var evt in _search.SearchManyAsync(perRootOptions, token).ConfigureAwait(true))
            {
                uiEventsReceived++;
                long now = Stopwatch.GetTimestamp();
                if (now - yieldTimestamp >= yieldIntervalTicks)
                {
                    uiYieldCount++;
                    // Yield to the dispatcher's higher-priority work (pending pointer/scroll input,
                    // layout, and rendering) instead of a fixed Task.Delay, so buffered result batches
                    // can never starve smooth scrolling. Resumes as soon as the pump is idle, so a
                    // non-interactive full-drive scan still drains at full speed.
                    await YieldToUiPumpAsync().ConfigureAwait(true);
                    yieldTimestamp = Stopwatch.GetTimestamp();
                }

                if (!IsCurrentSearch(runId, cts))
                {
                    LogService.Instance.Warning("Search", $"Ignoring stale search #{runId} event after a newer search started");
                    break;
                }

                // Periodic UI consumer throughput log
                now = Stopwatch.GetTimestamp();
                if ((now - uiLastLogTicks) >= Stopwatch.Frequency * UiLogIntervalSec)
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
                        // "Everything SDK returned no results" is an internal tiered-fallback
                        // diagnostic that is never useful on the main screen: when matches exist it
                        // looks like an error, and when none exist the status already shows 0 matches.
                        // Suppress it; any other fallback reason still surfaces.
                        if (f.Reason is null ||
                            !f.Reason.StartsWith("Everything SDK returned no results", StringComparison.Ordinal))
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
                    case SearchEvent.SourceBackedMatchBatch sb:
                        uiMatchesReceived += sb.Results.Count;
                        uiEventSw.Restart();
                        await AddSourceBackedMatchesAsync(sb.Results, token).ConfigureAwait(true);
                        uiEventSw.Stop();
                        RefreshStatusFromReceivedMatches();
                        if (uiEventSw.ElapsedMilliseconds > 200)
                        {
                            LogService.Instance.Warning("UIConsumer",
                                $"Slow AddSourceBackedMatches: {sb.Results.Count} results took {uiEventSw.ElapsedMilliseconds}ms " +
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
                            var evictSw = Stopwatch.StartNew();
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
                    case SearchEvent.ScanCompleted sc:
                        var scanElapsed = StopSearchTimer();
                        FilesScanned = sc.Summary.FilesScanned;
                        TotalFiles = sc.Summary.TotalFiles;
                        MatchesFound = Math.Max(sc.Summary.TotalMatches, ClampMatchCount(uiMatchesReceived));
                        FilesSkipped = sc.Summary.FilesSkipped;
                        AccessDeniedCount = sc.Summary.SkipReasons?.AccessDenied ?? 0;
                        _bytesScanned = sc.Summary.BytesScanned;
                        UpdateSkipBreakdown(sc.Summary.SkipReasons);
                        Truncated = sc.Summary.Truncated;
                        Degraded = sc.Summary.Degraded;
                        StatusText = $"Finalizing results... {MatchesFound:N0} matches in {_resultCollection.AllGroups.Count:N0} files ({FormatElapsed(scanElapsed)})";
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
                    SearchTerminatedByLowDiskSpace?.Invoke(message);
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

    /// <summary>
    /// Shows exactly one file as a file-name match, bypassing the search engine entirely. Used when the
    /// Traditional query is a complete file path: the file is displayed regardless of the Directory box.
    /// Reuses the normal search lifecycle (run id, gate, state reset, history, result collection) so the
    /// results list, status bar, and clipboard export behave just like any other completed search.
    /// </summary>
    private async Task RunSingleFilePathDisplayAsync(string filePath)
    {
        int runId = Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);

        await _searchLifecycleGate.WaitAsync();

        CancellationTokenSource? cts = null;
        try
        {
            if (runId != Volatile.Read(ref _searchRunId))
                return;

            ResetStateForNewSearch();
            cts = new CancellationTokenSource();
            _cts = cts;

            // The query was a complete path, not a content pattern: highlight nothing in the preview.
            LastSearchPattern = string.Empty;
            LastSearchCaseSensitive = CaseSensitive;
            LastSearchUseRegex = false;
            LastSearchExactMatch = false;
            LastSearchMultiline = false;
            LastSearchMultilineDotAll = false;

            var result = new SearchResult(
                FilePath: filePath,
                LineNumber: 0,
                MatchLine: string.Empty,
                MatchStartColumn: 0,
                MatchLength: 0,
                ContextBefore: Array.Empty<string>(),
                ContextAfter: Array.Empty<string>());
            await AddMatchAsync(result, cts.Token).ConfigureAwait(true);

            var elapsed = StopSearchTimer();
            FilesScanned = 1;
            TotalFiles = 1;
            MatchesFound = 1;
            Truncated = false;
            Degraded = false;
            StatusText = $"1 file matched the path \u2014 {Path.GetFileName(filePath)} ({FormatElapsed(elapsed)})";
            ApplySortAndFilter();

            // Record the typed path in Traditional search history (mirrors StartSearchAsync).
            SettingsService.PushRecent(_settings.SearchHistory, _settings.SearchHistoryTimes, Query, MaxRecentItems);
            _pendingSemanticHistoryEntry = null;
            SyncRecent();
            await PersistSettingsAsync();
        }
        catch (Exception ex)
        {
            StopSearchTimer();
            ErrorText = $"Search failed: {ex.Message}";
            LogService.Instance.Critical("Search", "Single-file-path display failed", ex);
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
        HasPerformedSearch = true;
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
        _searchTimer = Stopwatch.StartNew();
        StartSearchStatusHeartbeat();
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
            return _lastSearchElapsed;

        timer.Stop();
        _searchTimer = null;
        StopSearchStatusHeartbeat();
        _lastSearchElapsed = timer.Elapsed;
        return timer.Elapsed;
    }

    private void StartSearchStatusHeartbeat()
    {
        StopSearchStatusHeartbeat();
        var cts = new CancellationTokenSource();
        _searchStatusHeartbeatCts = cts;
        _ = RunSearchStatusHeartbeatAsync(cts);
    }

    private void StopSearchStatusHeartbeat()
    {
        var cts = Interlocked.Exchange(ref _searchStatusHeartbeatCts, null);
        try { cts?.Cancel(); } catch { }
    }

    private async Task RunSearchStatusHeartbeatAsync(CancellationTokenSource cts)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.High, UpdateSearchStatusHeartbeat))
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.CompareExchange(ref _searchStatusHeartbeatCts, null, cts);
            cts.Dispose();
        }
    }

    private void UpdateSearchStatusHeartbeat()
    {
        if (_disposed || _searchTimer is null || !IsSearching)
        {
            StopSearchStatusHeartbeat();
            return;
        }

        UpdateFilesPerSecond();
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
        // Test seam: a UI-automation harness (e.g. scripts\test-match-nav.ps1) can set
        // YAGU_EDITOR_COMMAND so that double-tapping a result while driving the real app
        // never launches the user's configured editor. Launching `code` under an elevated
        // VS Code pops a modal "Another instance of Code is already running as administrator"
        // dialog that steals focus and hangs the automation. When the variable is unset (the
        // normal case) the user's configured EditorCommand is used unchanged.
        var editorOverride = Environment.GetEnvironmentVariable("YAGU_EDITOR_COMMAND");
        _editor.Command = string.IsNullOrWhiteSpace(editorOverride) ? EditorCommand : editorOverride;
        _editor.Open(result.FilePath, result.LineNumber);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void OpenContainingFolder(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenContainingFolder(result.FilePath);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void OpenTerminalHere(SearchResult? result)
    {
        if (result is null) return;
        EditorLauncher.OpenTerminalAt(result.FilePath);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
    [RelayCommand]
    public void CopyFilePath(SearchResult? result)
    {
        if (result is null) return;
        SetClipboard(result.FilePath);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RelayCommand source generator expects instance command methods.")]
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

    /// <summary>
    /// Yields the UI thread to the dispatcher's higher-priority work — pending pointer/scroll input,
    /// layout, and rendering — before resuming, so a long run of buffered search-result batches cannot
    /// starve smooth scrolling of the results list. The Low-priority continuation resumes only after the
    /// pump has drained higher-priority work; when the UI is idle (e.g. a non-interactive full-drive
    /// scan) it resumes almost immediately, so result draining still runs at full speed.
    /// </summary>
    private Task YieldToUiPumpAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
            tcs.TrySetResult();
        return tcs.Task;
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

    private Task AddSourceBackedMatchesAsync(IReadOnlyList<SourceBackedMatch> results, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool resultAvailabilityChanged = _resultCollection.AddSourceBackedRange(
            results,
            InitializeResultGroup);

        QueueSearchSortRefreshIfDue();

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();

        return Task.CompletedTask;
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

        var sw = Stopwatch.StartNew();
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
        if (!IsSearching || _searchSortRefreshQueued || groupCount < 2)
            return;

        long now = Stopwatch.GetTimestamp();
        long intervalTicks = (long)(Stopwatch.Frequency * _searchSortRefreshIntervalSec);

        if (Degraded && groupCount >= SearchSortRefreshDegradedDeferGroupThreshold)
        {
            _searchSortRefreshIntervalSec = SearchSortRefreshIntervalMaxSec;
            if (_lastSearchSortRefreshTicks == 0 || now - _lastSearchSortRefreshTicks >= intervalTicks)
            {
                _lastSearchSortRefreshTicks = now;
                LogService.Instance.Verbose("ViewModel",
                    $"Deferring periodic in-search sort refresh for degraded large result set: {groupCount:N0} group(s); final refresh will run on completion");
            }

            return;
        }

        if (_lastSearchSortRefreshTicks != 0 && now - _lastSearchSortRefreshTicks < intervalTicks)
            return;

        // Don't reorder/rebuild the results list while the user has a file group
        // expanded. The periodic refresh goes through ApplySortAndFilter ->
        // VisibleGroups.ReplaceAll -> a Reset that tears down and re-creates every
        // ListView container, which makes the open drawer visibly collapse and
        // re-expand (flicker) and loses the user's scroll position. The final
        // ApplySortAndFilter on search completion still sorts everything.
        if (AnyResultGroupExpanded())
        {
            // Defer the next check by one interval so we don't rescan every batch.
            _lastSearchSortRefreshTicks = now;
            return;
        }

        _searchSortRefreshQueued = true;
        _lastSearchSortRefreshTicks = now;

        if (!_dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            _searchSortRefreshQueued = false;
            int currentGroupCount = _resultCollection.AllGroups.Count;
            if (!IsSearching || currentGroupCount < 2)
                return;

            // The user may have expanded a drawer between queueing and execution;
            // skip the rebuild so the open drawer doesn't flicker.
            if (AnyResultGroupExpanded())
                return;

            var sw = Stopwatch.StartNew();
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

    /// <summary>
    /// True if any visible file group is currently expanded. Used to suppress the
    /// periodic in-search sort refresh, whose ReplaceAll/Reset would otherwise tear
    /// down and re-create the open drawer's container (visible flicker).
    /// </summary>
    private bool AnyResultGroupExpanded()
    {
        var groups = _resultCollection.VisibleGroups;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].IsExpanded)
                return true;
        }

        return false;
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
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastPostEvictionCompactingGcTicks);
        if (last != 0)
        {
            double secondsSinceLast = (double)(now - last) / Stopwatch.Frequency;
            if (secondsSinceLast < PostEvictionCompactingGcCooldown.TotalSeconds)
                return;
        }

        if (Interlocked.CompareExchange(ref s_postEvictionCompactingGcInFlight, 1, 0) != 0)
            return;

        var gcStopwatch = Stopwatch.StartNew();
        try
        {
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
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
            Volatile.Write(ref s_lastPostEvictionCompactingGcTicks, Stopwatch.GetTimestamp());
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
        HasPerformedSearch = false;
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

            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }).ConfigureAwait(true);
    }

    /// <summary>Hydrate an evicted result from disk so its full data is available.</summary>
    public void HydrateResult(SearchResult result)
    {
        if (!result.IsEvicted) return;

        if (result.IsSourceBacked)
        {
            if (ReadSourceBackedHydrationPayload(result) is { } payload)
                ApplyHydrationPayloads([payload]);
            return;
        }

        if (_resultStore is not null)
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
        if (results.Count == 0) return Array.Empty<HydrationPayload>();

        List<HydrationPayload>? payloads = null;

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].IsSourceBacked && ReadSourceBackedHydrationPayload(results[i]) is { } payload)
                (payloads ??= new List<HydrationPayload>()).Add(payload);
        }

        if (_resultStore is null)
            return payloads ?? (IReadOnlyList<HydrationPayload>)Array.Empty<HydrationPayload>();

        // Collect offsets for evicted items
        long[] offsets = new long[results.Count];
        int evictedCount = 0;
        int[] evictedIndices = new int[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].DiskOffset >= 0)
            {
                offsets[evictedCount] = results[i].DiskOffset;
                evictedIndices[evictedCount] = i;
                evictedCount++;
            }
        }
        if (evictedCount == 0)
            return payloads ?? (IReadOnlyList<HydrationPayload>)Array.Empty<HydrationPayload>();

        try
        {
            var readResults = _resultStore.ReadBatch(offsets.AsSpan(0, evictedCount));
            payloads ??= new List<HydrationPayload>(evictedCount);
            for (int i = 0; i < evictedCount; i++)
            {
                var data = readResults[i];
                if (data is null) continue;
                var (ml, cb, ca) = data.Value;
                var result = results[evictedIndices[i]];
                payloads.Add(new HydrationPayload(
                    result,
                    ml,
                    cb,
                    ca,
                    result.MatchStartColumn,
                    result.MatchLength,
                    result.SourceMatchStartColumn));
            }
            return payloads;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            LogService.Instance.Warning("ViewModel", $"Batch hydration failed: {ex.Message}");
            return payloads ?? (IReadOnlyList<HydrationPayload>)Array.Empty<HydrationPayload>();
        }
    }

    private HydrationPayload? ReadSourceBackedHydrationPayload(SearchResult result)
    {
        if (result.LineNumber <= 0 || string.IsNullOrWhiteSpace(result.FilePath)) return null;

        try
        {
            int contextLineCount = Math.Max(0, ContextLines);
            var before = new Queue<string>(contextLineCount);
            var after = new List<string>(contextLineCount);
            string? matchLine = null;
            int currentLineNumber = 0;

            foreach (var line in File.ReadLines(result.FilePath))
            {
                currentLineNumber++;
                if (currentLineNumber < result.LineNumber)
                {
                    if (contextLineCount > 0)
                    {
                        if (before.Count == contextLineCount)
                            before.Dequeue();
                        before.Enqueue(LineTruncator.Truncate(line));
                    }
                    continue;
                }

                if (currentLineNumber == result.LineNumber)
                {
                    matchLine = line;
                    continue;
                }

                if (after.Count < contextLineCount)
                {
                    after.Add(LineTruncator.Truncate(line));
                    if (after.Count < contextLineCount)
                        continue;
                }
                break;
            }

            if (matchLine is null) return null;

            int sourceMatchStart = EstimateUtf16ColumnFromUtf8ByteOffset(matchLine, result.SourceMatchStartColumn);
            int matchLength = EstimateUtf16LengthFromUtf8ByteLength(matchLine, sourceMatchStart, result.MatchLength);
            matchLength = Math.Min(matchLength, Math.Max(0, matchLine.Length - sourceMatchStart));
            var displayLine = LineTruncator.TruncateAroundMatch(matchLine, sourceMatchStart, matchLength);

            return new HydrationPayload(
                result,
                displayLine.Text,
                before.ToArray(),
                after,
                displayLine.MatchStart,
                matchLength,
                sourceMatchStart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogService.Instance.Warning("ViewModel", $"Source-backed hydration failed for '{result.FilePath}': {ex.Message}");
            return null;
        }
    }

    private static int EstimateUtf16LengthFromUtf8ByteLength(string line, int sourceColumn, int utf8ByteLength)
    {
        if (utf8ByteLength <= 0 || sourceColumn >= line.Length) return 0;
        int consumedBytes = 0;
        int chars = 0;

        while (sourceColumn + chars < line.Length && consumedBytes < utf8ByteLength)
        {
            int charCount = 1;
            if (char.IsHighSurrogate(line[sourceColumn + chars])
                && sourceColumn + chars + 1 < line.Length
                && char.IsLowSurrogate(line[sourceColumn + chars + 1]))
            {
                charCount = 2;
            }

            int byteCount = Encoding.UTF8.GetByteCount(line.AsSpan(sourceColumn + chars, charCount));
            if (consumedBytes + byteCount > utf8ByteLength && chars > 0)
                break;

            consumedBytes += byteCount;
            chars += charCount;
        }

        return chars;
    }

    private static int EstimateUtf16ColumnFromUtf8ByteOffset(string line, int utf8ByteOffset)
    {
        if (utf8ByteOffset <= 0) return 0;

        int consumedBytes = 0;
        int column = 0;
        while (column < line.Length && consumedBytes < utf8ByteOffset)
        {
            int charCount = 1;
            if (char.IsHighSurrogate(line[column])
                && column + 1 < line.Length
                && char.IsLowSurrogate(line[column + 1]))
            {
                charCount = 2;
            }

            int byteCount = Encoding.UTF8.GetByteCount(line.AsSpan(column, charCount));
            if (consumedBytes + byteCount > utf8ByteOffset)
                break;

            consumedBytes += byteCount;
            column += charCount;
        }

        return column;
    }

    /// <summary>Apply hydrated payloads to SearchResult objects. Must run on the UI thread.</summary>
    public static void ApplyHydrationPayloads(IEnumerable<HydrationPayload> payloads)
    {
        foreach (var payload in payloads)
        {
            payload.Result.HydrateFrom(
                payload.MatchLine,
                payload.ContextBefore,
                payload.ContextAfter,
                payload.MatchStartColumn,
                payload.MatchLength,
                payload.SourceMatchStartColumn);
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

        Regex? regex = null;
        string? literal = null;
        StringComparison literalComparison = StringComparison.OrdinalIgnoreCase;

        if (UseRegex)
        {
            var regexOptions = RegexOptions.Multiline;
            if (!CaseSensitive) regexOptions |= RegexOptions.IgnoreCase;
            try { regex = new Regex(query, regexOptions, TimeSpan.FromSeconds(5)); }
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
        _resultCollection.ExcludeGlobs = EffectiveExcludeGlobsText;
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
        SemanticSearchHistory.Clear();
        foreach (var q in _settings.SemanticSearchHistory) SemanticSearchHistory.Add(q);
    }

    /// <summary>Resolves the directory the box should show at launch. Honors a pinned startup
    /// directory when the user has enabled the pin and a path was captured; otherwise starts empty so
    /// the search defaults to all drives. The legacy LastDirectory value is intentionally not restored
    /// here — it caused the box to spuriously preselect the last-used drive.</summary>
    private string ResolveStartupDirectory()
    {
        if (_settings.PinStartupDirectory && !string.IsNullOrWhiteSpace(_settings.PinnedStartupDirectory))
        {
            return _settings.PinnedStartupDirectory!;
        }

        return string.Empty;
    }

    /// <summary>Pins or unpins the current directory box for the next launch. Pinning snapshots the
    /// box value at the moment of the call (so later edits to the box do not change the pin) and
    /// persists immediately; unpinning clears the saved directory so the box starts empty next launch.
    /// This only affects what the box shows at startup and never overrides the box during a session.</summary>
    public async Task SetStartupDirectoryPinnedAsync(bool pinned)
    {
        PinStartupDirectory = pinned;
        _settings.PinStartupDirectory = pinned;
        _settings.PinnedStartupDirectory = pinned
            ? (string.IsNullOrWhiteSpace(Directory) ? null : Directory.Trim())
            : null;
        // The pinned-path snapshot lives on _settings (not an observable property), so re-pinning to a
        // DIFFERENT folder while PinStartupDirectory stays true wouldn't otherwise re-evaluate the star
        // highlight. Nudge the derived state explicitly so the toggle reflects the new snapshot now.
        OnPropertyChanged(nameof(IsCurrentDirectoryPinned));
        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }

    public async Task PersistSettingsAsync()
    {
        // While a completed semantic search's resolution is shown in Advanced Options, persist the saved
        // filter DEFAULTS (from the snapshot) instead of the resolved values, so a semantic search never
        // changes what a fresh Yagu instance opens with. (Directory is the one exception — a model-
        // resolved directory is meant to override and persist.) The snapshot captures the ENTIRE filter
        // surface — including the Skip/Binary/Archive extension lists (both the active and the persisted
        // Settings* mirror) and the OCR toggle — so a transient "Include & search" un-skip or any future
        // resolution path can never leak a resolved value to disk. Guard every filter field with `d`.
        var d = _semanticResolutionVisible ? _semanticDefaultsSnapshot : null;

        _settings.LastDirectory = Directory;
        _settings.CaseSensitive = d is null ? CaseSensitive : d.CaseSensitive;
        _settings.UseRegex = d is null ? UseRegex : d.UseRegex;
        _settings.ExactMatch = d is null ? ExactMatch : d.ExactMatch;
        _settings.MultilineSearchDefault = Multiline;
        _settings.ContextLines = ContextLines;
        _settings.PreviewContextLines = PreviewContextLines;
        _settings.ObeyGitignore = d is null ? ObeyGitignore : d.ObeyGitignore;
        _settings.GitignoreTakesPrecedence = GitignoreTakesPrecedence;
        _settings.GitignorePrecedencePreference = GitignorePrecedencePreference;
        _settings.DefaultToTraditionalSearchMode = DefaultToTraditionalSearchMode;
        _settings.SemanticSearchEnabled = SemanticSearchAvailable;
        _settings.SemanticModelAlias = SemanticModelAlias;
        _settings.SemanticDevicePreferenceOrder = SemanticDevicePreferenceOrder;
        _settings.SemanticUnloadModelAfterUse = SemanticUnloadModelAfterUse;
        _settings.IncludeGlobs = d is null ? IncludeGlobs : d.IncludeGlobs;
        _settings.ExcludeGlobs = d is null ? ExcludeGlobs : d.ExcludeGlobs;
        _settings.IncludeFilterModeIndex = d is null ? IncludeFilterModeIndex : d.IncludeFilterModeIndex;
        _settings.ExcludeFilterModeIndex = d is null ? ExcludeFilterModeIndex : d.ExcludeFilterModeIndex;
        _settings.MinFileSizeBytes = d is null ? MinFileSizeBytes : d.MinFileSizeBytes;
        _settings.MaxFileSizeBytes = d is null ? MaxFileSizeBytes : d.MaxFileSizeBytes;
        _settings.CreatedAfterDate = d is null ? CreatedAfterDate : d.CreatedAfterDate;
        _settings.CreatedBeforeDate = d is null ? CreatedBeforeDate : d.CreatedBeforeDate;
        _settings.ModifiedAfterDate = d is null ? ModifiedAfterDate : d.ModifiedAfterDate;
        _settings.ModifiedBeforeDate = d is null ? ModifiedBeforeDate : d.ModifiedBeforeDate;
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
        _settings.PreviewLongLineWarningIndex = PreviewLongLineWarningIndex;
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

        // ── File list overlay ──
        _settings.FileListOverlayHeight = Math.Clamp(FileListOverlayHeight <= 0 ? AppSettings.DefaultFileListOverlayHeight : FileListOverlayHeight, 20, 100);
        _settings.FileListOverlayFontSize = Math.Clamp(FileListOverlayFontSize <= 0 ? AppSettings.DefaultFileListOverlayFontSize : FileListOverlayFontSize, 6, 72);
        _settings.FileListOverlayFontColor = string.IsNullOrWhiteSpace(FileListOverlayFontColor) ? AppSettings.DefaultFileListOverlayFontColor : FileListOverlayFontColor.Trim();
        _settings.FileListOverlayFontFamily = string.IsNullOrWhiteSpace(FileListOverlayFontFamily) ? AppSettings.DefaultFileListOverlayFontFamily : FileListOverlayFontFamily.Trim();

        // ── Preview sticky header ──
        _settings.PreviewStickyHeaderHeight = Math.Clamp(PreviewStickyHeaderHeight <= 0 ? AppSettings.DefaultPreviewStickyHeaderHeight : PreviewStickyHeaderHeight, 20, 100);
        _settings.PreviewStickyHeaderFileNameFontSize = Math.Clamp(PreviewStickyHeaderFileNameFontSize <= 0 ? AppSettings.DefaultPreviewStickyHeaderFileNameFontSize : PreviewStickyHeaderFileNameFontSize, 6, 72);
        _settings.PreviewStickyHeaderFileNameFontColor = string.IsNullOrWhiteSpace(PreviewStickyHeaderFileNameFontColor) ? AppSettings.DefaultPreviewStickyHeaderFileNameFontColor : PreviewStickyHeaderFileNameFontColor.Trim();
        _settings.PreviewStickyHeaderFileNameFontFamily = string.IsNullOrWhiteSpace(PreviewStickyHeaderFileNameFontFamily) ? AppSettings.DefaultPreviewStickyHeaderFileNameFontFamily : PreviewStickyHeaderFileNameFontFamily.Trim();
        _settings.PreviewStickyHeaderDetailFontSize = Math.Clamp(PreviewStickyHeaderDetailFontSize <= 0 ? AppSettings.DefaultPreviewStickyHeaderDetailFontSize : PreviewStickyHeaderDetailFontSize, 6, 72);
        _settings.PreviewStickyHeaderDetailFontColor = string.IsNullOrWhiteSpace(PreviewStickyHeaderDetailFontColor) ? AppSettings.DefaultPreviewStickyHeaderDetailFontColor : PreviewStickyHeaderDetailFontColor.Trim();
        _settings.PreviewStickyHeaderDetailFontFamily = string.IsNullOrWhiteSpace(PreviewStickyHeaderDetailFontFamily) ? AppSettings.DefaultPreviewStickyHeaderDetailFontFamily : PreviewStickyHeaderDetailFontFamily.Trim();

        // ── File list drawer labels ──
        _settings.DrawerFileNameFontSize = Math.Clamp(DrawerFileNameFontSize <= 0 ? AppSettings.DefaultDrawerFileNameFontSize : DrawerFileNameFontSize, 6, 72);
        _settings.DrawerFileNameFontColor = string.IsNullOrWhiteSpace(DrawerFileNameFontColor) ? AppSettings.DefaultDrawerFileNameFontColor : DrawerFileNameFontColor.Trim();
        _settings.DrawerFileNameFontFamily = string.IsNullOrWhiteSpace(DrawerFileNameFontFamily) ? AppSettings.DefaultDrawerFileNameFontFamily : DrawerFileNameFontFamily.Trim();
        _settings.DrawerDirectoryFontSize = Math.Clamp(DrawerDirectoryFontSize <= 0 ? AppSettings.DefaultDrawerDirectoryFontSize : DrawerDirectoryFontSize, 6, 72);
        _settings.DrawerDirectoryFontColor = string.IsNullOrWhiteSpace(DrawerDirectoryFontColor) ? AppSettings.DefaultDrawerDirectoryFontColor : DrawerDirectoryFontColor.Trim();
        _settings.DrawerDirectoryFontFamily = string.IsNullOrWhiteSpace(DrawerDirectoryFontFamily) ? AppSettings.DefaultDrawerDirectoryFontFamily : DrawerDirectoryFontFamily.Trim();
        _settings.DrawerMetadataFontSize = Math.Clamp(DrawerMetadataFontSize <= 0 ? AppSettings.DefaultDrawerMetadataFontSize : DrawerMetadataFontSize, 6, 72);
        _settings.DrawerMetadataFontColor = string.IsNullOrWhiteSpace(DrawerMetadataFontColor) ? AppSettings.DefaultDrawerMetadataFontColor : DrawerMetadataFontColor.Trim();
        _settings.DrawerMetadataFontFamily = string.IsNullOrWhiteSpace(DrawerMetadataFontFamily) ? AppSettings.DefaultDrawerMetadataFontFamily : DrawerMetadataFontFamily.Trim();

        _settings.LogLevelIndex = FileLogLevelIndex;
        _settings.ConsoleLogLevelIndex = ConsoleLogLevelIndex;
        _settings.FileListerBackendIndex = FileListerBackendIndex;
        _settings.ParallelismIndex = ParallelismIndex;
        _settings.IoOversubscriptionIndex = IoOversubscriptionIndex;
        _settings.LineTruncationLength = LineTruncationLength;
        _settings.MaxRecentItems = MaxRecentItems;
        _settings.MaxSemanticRecentItems = MaxSemanticRecentItems;
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
        _settings.MaxMatchesPerLine = MaxMatchesPerLine < 0 ? 0 : MaxMatchesPerLine;
        _settings.AbsoluteMaxResults = AbsoluteMaxResults < 0 ? 0 : AbsoluteMaxResults;
        _settings.SkipBinary = d is null ? SkipBinary : d.SkipBinary;
        _settings.SearchOnlineOnlyFiles = SearchOnlineOnlyFiles;
        _settings.SearchHiddenFiles = d is null ? SearchHiddenFiles : d.SearchHiddenFiles;
        _settings.SearchImageText = d is null ? SearchImageText : d.SearchImageText;
        _settings.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine);
        _settings.ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel);
        _settings.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide);
        // The startup-directory pin flag mirrors the star toggle. The captured directory itself
        // (PinnedStartupDirectory) is a snapshot written by SetStartupDirectoryPinnedAsync at click
        // time, so it is intentionally NOT recaptured here and never drifts as the box changes.
        _settings.PinStartupDirectory = PinStartupDirectory;
        _settings.SearchInsideArchives = d is null ? SearchInsideArchives : d.SearchInsideArchives;
        _settings.ArchiveExtensions = d is null ? SettingsArchiveExtensions : d.SettingsArchiveExtensions;
        _settings.SkipExtensions = d is null ? SettingsSkipExtensions : d.SettingsSkipExtensions;
        _settings.BinaryExtensions = d is null ? SettingsBinaryExtensions : d.SettingsBinaryExtensions;
        _settings.SuppressAdminWarning = SuppressAdminWarning;
        _settings.SuppressEverythingNotRunningPrompt = SuppressEverythingNotRunningPrompt;
        _settings.SuppressExcludedExtensionWarnings = SuppressExcludedExtensionWarnings;
        _settings.IncludeExcludedExtensionByDefault = IncludeExcludedExtensionByDefault;
        _settings.SuppressFontContrastWarnings = SuppressFontContrastWarnings;
        _settings.FontContrastReminderAfterUtc = FontContrastReminderAfterUtc;
        _settings.ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths;
        _settings.AdminProtectedPathSegments = AdminProtectedPathSegments;
        _settings.HasCompletedFirstRun = HasCompletedFirstRun;
        _settings.HasShownFileDrawerIntroTip = HasShownFileDrawerIntroTip;
        _settings.HasShownFileDrawerLineNumberIntroTip = HasShownFileDrawerLineNumberIntroTip;
        _settings.HasShownPreviewMatchIntroTip = HasShownPreviewMatchIntroTip;
        _settings.LimitParallelismOnHdd = LimitParallelismOnHdd;
        _settings.SuppressHddParallelismWarnings = SuppressHddParallelismWarnings;
        _settings.SearchAllDrivesIncludesNetwork = SearchAllDrivesIncludesNetwork;
        _settings.SearchAllDrivesIncludesRemovable = SearchAllDrivesIncludesRemovable;
        _settings.SearchAllDrivesIncludesCloud = SearchAllDrivesIncludesCloud;
        _settings.SearchAllDrivesForceFullScan = SearchAllDrivesForceFullScan;
        _settings.BackupBeforeSave = BackupBeforeSave;
        _settings.ShowEditorSavedOverlay = ShowEditorSavedOverlay;
        _settings.EditorSyntaxHighlightingEnabled = EditorSyntaxHighlightingEnabled;
        _settings.WindowFocusBehavior = WindowFocusBehavior;
        _settings.StartInLauncherMode = StartInLauncherMode;
        _settings.CloseToTray = CloseToTray;
        _settings.HasShownCloseToTrayNotification = HasShownCloseToTrayNotification;
        _settings.MaximizeOnStartup = MaximizeOnStartup;
        _settings.LaunchWindowPosition = LaunchWindowPosition;
        _settings.LauncherWindowPosition = LauncherWindowPosition;
        _settings.AdvancedOptionsCollapsedWidthModeIndex = NormalizeAdvancedOptionsCollapsedWidthModeIndex(AdvancedOptionsCollapsedWidthModeIndex);
        _settings.TerminalDefaultWorkingDirectory = string.IsNullOrWhiteSpace(TerminalDefaultWorkingDirectory)
            ? string.Empty
            : TerminalDefaultWorkingDirectory.Trim();
        _settings.TerminalShellKindIndex = TerminalShell.NormalizeSettingsIndex(TerminalShellKindIndex);
        _settings.FileHeaderCheckAddsToPreview = FileHeaderCheckAddsToPreview;
        _settings.MatchLineCheckAddsToPreview = MatchLineCheckAddsToPreview;
        _settings.PreviewEditorMaxSizeMB = PreviewEditorMaxSizeMB;
        _settings.PreviewEditorMaxTextLength = PreviewEditorMaxTextLength;
        _settings.PreviewEditorMaxLineLength = PreviewEditorMaxLineLength;
        _settings.PreviewEditorPopOutMaxSizeMB = PreviewEditorPopOutMaxSizeMB;
        _settings.PreviewEditorPopOutArrangementIndex = PreviewEditorPopOutArrangementIndex;
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
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
            HasPerformedSearch = false;
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
        // Binary extensions only suppress CONTENT searching (handled by SkipBinary's header sniff in
        // ContentSearcher). They must NOT be early-skipped from file listing in name-matching modes, or a
        // search like "dnGrep.exe" finds nothing even though the file is right there in the index. Fold
        // them into the skip set only for Content-only mode, where file names are never matched anyway.
        if ((SearchMode)SearchModeIndex == SearchMode.Content)
            foreach (var ext in ParseExtensionSet(BinaryExtensions))
                effective.Add(ext);
        return effective;
    }

    /// <summary>
    /// Returns the predicted excluded-extension warning for the current query and advanced options, or
    /// null when there is nothing to warn about (the query does not name a file whose extension is
    /// currently excluded). Does NOT consider <see cref="SuppressExcludedExtensionWarnings"/> — the caller
    /// decides whether to SHOW the warning or silently apply the remembered default action
    /// (<see cref="IncludeExcludedExtensionByDefault"/>), both of which still need this warning's data.
    /// </summary>
    internal ExcludedExtensionWarning? TryGetExcludedExtensionWarning()
    {
        // Note: this runs AFTER semantic translation (via the SubmitSearchAsync gate), so in Semantic
        // mode Query/IncludeGlobs already reflect the model's resolved plan (e.g. include glob *.exe).

        // Archive universe = every known archive type (saved defaults + whatever is active). Contents are
        // only "searched" for the active archive types when Search-inside-archives is on.
        var archiveUniverse = ParseExtensionSet(SettingsArchiveExtensions);
        foreach (var ext in ParseExtensionSet(ArchiveExtensions))
            archiveUniverse.Add(ext);
        var archiveSearched = SearchInsideArchives
            ? ParseExtensionSet(ArchiveExtensions)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return ExcludedExtensionPredictor.Predict(
            Query,
            UseRegex,
            ExactMatch,
            (SearchMode)SearchModeIndex,
            ParseExtensionSet(SkipExtensions),
            ParseExtensionSet(BinaryExtensions),
            IncludeGlobs,
            IncludeFilterMode,
            EffectiveExcludeGlobsText,
            ExcludeFilterMode,
            archiveUniverse,
            archiveSearched);
    }

    /// <summary>
    /// Makes <paramref name="warning"/>'s extension findable for the CURRENT search only, by adjusting
    /// the offending Advanced Options list(s) transiently — nothing is written to the saved settings, and
    /// every control is reset back to the saved defaults once the search finishes (see
    /// <see cref="ResetAdvancedOptionsToSavedDefaults"/>). The rule per list:
    /// <list type="bullet">
    /// <item>Skip: keep skipping everything EXCEPT this extension (so only it is scanned).</item>
    /// <item>Binary: turn on binary search and select ONLY this binary type (skip every other binary type).</item>
    /// <item>Archive: turn on archive search and select ONLY this archive type.</item>
    /// <item>Include/Exclude filter: edit the session-only filter so the extension is no longer filtered out.</item>
    /// </list>
    /// </summary>
    internal Task IncludeExtensionForSearchAsync(ExcludedExtensionWarning warning)
    {
        string ext = warning.Extension;

        // Mark that this search transiently changed Advanced Options so they are reset to the saved
        // defaults once the search finishes (see OnIsSearchingChanged / ResetAdvancedOptionsToSavedDefaults).
        _advancedOptionsTransientlyChanged = true;

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.BinaryExtensions))
            EnableBinarySearchForExtension(ext);

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.SkipExtensions))
            UnskipExtensionForSearch(ext);

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.ArchiveExtensions))
            EnableArchiveSearchForExtension(ext);

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.ExcludeFilter))
        {
            // EffectiveExcludeGlobsText may be the built-in default; materialize it minus the extension
            // into the editable (session) ExcludeGlobs so the file is no longer excluded.
            ExcludeGlobs = ExcludedExtensionPredictor.RemoveExtensionToken(EffectiveExcludeGlobsText, ext);
        }

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.IncludeFilter))
        {
            // The restrictive Include filter omits this extension — add it so the file is included.
            IncludeGlobs = ExcludedExtensionPredictor.AppendExtensionToken(IncludeGlobs, ext);
        }

        // Deliberately NOT persisted: the change applies only to this search and is reverted afterward.
        return Task.CompletedTask;
    }

    /// <summary>Skip rule: stop skipping <paramref name="ext"/> (so it is scanned) while keeping every
    /// other skip-extension skipped. Session-only edit of the active Skip Extensions list.</summary>
    private void UnskipExtensionForSearch(string ext)
    {
        var universe = ParseExtensionSet(SettingsSkipExtensions);
        foreach (var e in ParseExtensionSet(SkipExtensions))
            universe.Add(e);
        universe.Remove(ext);

        var newSkip = string.Join(';', universe.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
        if (!string.Equals(SkipExtensions, newSkip, StringComparison.OrdinalIgnoreCase))
        {
            SkipExtensions = newSkip;
            SyncSkipExtensionItems();
        }
    }

    /// <summary>Binary rule: turn on binary search and SELECT ONLY <paramref name="ext"/> in the binary
    /// dropdown (every other binary type stays skipped). Session-only — internally BinaryExtensions is the
    /// skip list, so "select only ext" means "skip everything except ext".</summary>
    private void EnableBinarySearchForExtension(string ext)
    {
        if (!SearchBinary)
            SearchBinary = true;

        var universe = ParseExtensionSet(SettingsBinaryExtensions);
        foreach (var e in ParseExtensionSet(BinaryExtensions))
            universe.Add(e);
        universe.Add(ext);

        var newSkip = string.Join(';', universe.Where(e => !string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
        if (!string.Equals(BinaryExtensions, newSkip, StringComparison.OrdinalIgnoreCase))
        {
            BinaryExtensions = newSkip;
            SyncBinaryExtensionItems();
        }
    }

    /// <summary>Archive rule: turn on archive search and SELECT ONLY <paramref name="ext"/> in the archive
    /// dropdown (every other archive type disabled). Session-only edit of the active Archive list.</summary>
    private void EnableArchiveSearchForExtension(string ext)
    {
        if (!SearchInsideArchives)
            SearchInsideArchives = true;

        if (!string.Equals(ArchiveExtensions, ext, StringComparison.OrdinalIgnoreCase))
        {
            ArchiveExtensions = ext;
            SyncArchiveExtensionItems();
        }
    }

    /// <summary>Set while a search's Advanced Options were transiently changed (e.g. the excluded-extension
    /// "Include &amp; search" flow), so they are reset to the saved defaults once the search finishes.</summary>
    private bool _advancedOptionsTransientlyChanged;

    partial void OnIsSearchingChanged(bool value)
    {
        if (value) return;                                // act only when a search ENDS (finish or cancel)
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
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
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
                await ApplyDirectorySuggestionsAsync(_settings.RecentDirectories).ConfigureAwait(false);
                return;
            }

            await ApplyDirectorySuggestionsAsync(suggestions).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when user keeps typing.
        }
    }

    internal async Task<int> UpdateDirectorySuggestionsForSelectedDirectoryAsync(string directory)
    {
        _dirAutoCompleteCts?.Cancel();
        _dirAutoCompleteCts = new CancellationTokenSource();
        var ct = _dirAutoCompleteCts.Token;

        try
        {
            var suggestions = await _dirAutoComplete.GetChildDirectorySuggestionsAsync(directory, ct).ConfigureAwait(false);
            await ApplyDirectorySuggestionsAsync(suggestions).ConfigureAwait(false);
            return suggestions.Count;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private Task ApplyDirectorySuggestionsAsync(IEnumerable<string> suggestions)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            DirectorySuggestions.Clear();
            foreach (var suggestion in suggestions)
                DirectorySuggestions.Add(new HistorySuggestion(suggestion, LookupRecentDirectoryTimestamp(suggestion)));
            completion.SetResult();
        }))
        {
            completion.SetResult();
        }

        return completion.Task;
    }
}
