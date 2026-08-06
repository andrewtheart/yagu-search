using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Yagu.Models;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Ai;
using System.Diagnostics;

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
    private CancellationTokenSource? _resourceMonitorCts;
    private static readonly TimeSpan IndexStorageSizeRefreshInterval = TimeSpan.FromMinutes(1);
    private string _cachedIndexStorageRoot = string.Empty;
    private FileListerBackend _cachedIndexStorageBackend;
    private IndexStorageSizeMeasurement _cachedIndexStorageSize;
    private DateTimeOffset _nextIndexStorageSizeRefreshUtc = DateTimeOffset.MinValue;
    private bool _hasCachedIndexStorageSize;
    private CancellationTokenSource? _indexStorageMeasurementCts;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<string> _activeSearchRoots = Array.Empty<string>();
    private readonly SemaphoreSlim _searchLifecycleGate = new(1, 1);
    private int _searchRunId;
    private AppSettings _settings;
    private readonly SearchResultCollection _resultCollection = new();
    private ResultStore? _resultStore;
    private CancellationTokenSource _metadataCts = new();
    private bool _metadataSortFilterRefreshQueued;
    // Content-index pruning gates captured for the CURRENT search (one per accelerated root), used to
    // classify per-file result provenance (plan §6.2). Read-only queries only; cleared each new search.
    private readonly object _indexGatesLock = new();
    private readonly List<Yagu.Services.Index.ContentIndexSearchGate> _activeIndexGates = new();
    // Stage-5 worker pruning scans captured for the CURRENT search (one per root the worker serves), used to
    // badge index-member result files. Guarded by _indexGatesLock; cleared each new search.
    private readonly List<Yagu.Services.Index.IContentIndexPruningScan> _activePruningScans = new();
    // Long-lived out-of-process index-worker query source, created on first use when the user opts into
    // IndexUseNativeWorker (plan §3.3). Reused across searches; disposed with the VM.
    private Yagu.Services.Index.IndexWorkerClient? _indexWorkerClient;
    private Yagu.Services.Index.IIndexCandidateSource? _indexWorkerSource;
    private readonly object _indexWorkerLock = new();
    // Monotonic id for each mapped-query shadow session (plan §6 Stage 3). Unique per open so concurrent
    // per-root shadow sessions never collide in the worker's session table.
    private int _shadowQuerySessionId;
    // Startup/query index warm-up. Query-mode deserialization is cancellable so beginning a search can
    // stop the memory/IO-heavy warm rather than letting both compete and forcing the search into repeated GC.
    private CancellationTokenSource? _indexWarmCancellation;
    private int _indexWarmGeneration;
    private string? _activeIndexWarmFolder;
    private string? _resumeIndexWarmFolder;
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

    /// <summary>
    /// The live persisted settings object. Exposed so the Settings window's Indexing tab can read and
    /// write the many <c>Index*</c> ingestion/storage/scheduling fields directly through the shared
    /// <c>AppSettings.Normalize*</c> validators (plan §6.1). Everything else uses the observable
    /// view-model properties; direct mutation here is persisted by <see cref="PersistSettingsAsync"/>,
    /// which saves the whole object, and reverted by the Settings window's cancel/restore.
    /// </summary>
    internal AppSettings Settings => _settings;

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
        // Per-model text-generation overrides (Temperature/TopP/MaxTokens/RandomSeed/Frequency/Presence).
        // Empty by default; a power user can tune a specific model variant via settings.json.
        _semanticTranslator.SetModelGenerationOverrides(_settings.SemanticModelParameterOverrides);
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
        AutocompleteDropdownVisibleItems = _settings.AutocompleteDropdownVisibleItems;
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
        ShowResourceUsageInStatusBar = _settings.ShowResourceUsageInStatusBar;
        ShowBuildNumberInTitleBar = _settings.ShowBuildNumberInTitleBar;
        ShowAutoScrollResultsCheckbox = _settings.ShowAutoScrollResultsCheckbox;
        SdkChannelBufferSize = _settings.SdkChannelBufferSize;
        MaxMatchesPerFile = _settings.MaxMatchesPerFile;
        ApplyMaxMatchesPerFile(MaxMatchesPerFile);
        MaxMatchesPerLine = _settings.MaxMatchesPerLine;
        FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(_settings.FileIoTimeoutSeconds);
        AbsoluteMaxResults = _settings.AbsoluteMaxResults;
        SkipBinary = _settings.SkipBinary;
        SearchOnlineOnlyFiles = _settings.SearchOnlineOnlyFiles;
        SearchHiddenFiles = _settings.SearchHiddenFiles;
        SearchImageText = _settings.SearchImageText;
        SearchPdfText = _settings.SearchPdfText;
        // Session-only per-search content-index toggle (plan §5/§6.1). Seeded from the effective
        // default (master feature on AND used-by-default on); never persisted, so a per-search
        // opt-in/opt-out cannot change the saved default. Overrides apply on top of this.
        UseContentIndex = _settings.ContentIndexActiveByDefault;
        ImageOcrEngine = _settings.ImageOcrEngine;
        ImageOcrModel = _settings.ImageOcrModel;
        ImageOcrMaxSide = _settings.ImageOcrMaxSide;
        ImageOcrWorkerParallelism = _settings.ImageOcrWorkerParallelism;
        PinStartupDirectory = _settings.PinStartupDirectory;
        SearchInsideArchives = _settings.SearchInsideArchives;
        SettingsSkipExtensions = _settings.SkipExtensions;
        SettingsBinaryExtensions = _settings.BinaryExtensions;
        SettingsArchiveExtensions = _settings.ArchiveExtensions;
        SkipExtensions = SettingsSkipExtensions;
        BinaryExtensions = SettingsBinaryExtensions;
        ArchiveExtensions = SettingsArchiveExtensions;
        SuppressAdminWarning = _settings.SuppressAdminWarning;
        AdvancedOptionsTabOrder = _settings.AdvancedOptionsTabOrder ?? [];
        SuppressEverythingNotRunningPrompt = _settings.SuppressEverythingNotRunningPrompt;
        SuppressEverythingIndexCoverageWarning = _settings.SuppressEverythingIndexCoverageWarning;
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
        MaxSelectedFilesPerPreview = _settings.MaxSelectedFilesPerPreview;
        MaxSelectedResultsPerPreview = _settings.MaxSelectedResultsPerPreview;
        MaxRenderedMatchesPerSection = _settings.MaxRenderedMatchesPerSection;
        FullFilePreviewLimitMB = _settings.FullFilePreviewLimitMB;
        FullFilePreviewMaxRenderLines = _settings.FullFilePreviewMaxRenderLines;
        FullFilePreviewMaxRenderChars = _settings.FullFilePreviewMaxRenderChars;
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

        // Start the slow (10 s) status-bar resource monitor (disk temp + Yagu/worker RAM). Runs entirely off
        // the UI thread and self-cancels on Dispose.
        StartResourceUsageMonitor();
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
        _shutdownRequested = true;

        try { _searchPrepareCts?.Cancel(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _dirAutoCompleteCts?.Cancel(); } catch { }
        try { _metadataCts.Cancel(); } catch { }
        try { _semanticCts?.Cancel(); } catch { }
        try { _indexWarmCancellation?.Cancel(); } catch { }
        try { _indexBuildCancellation?.Cancel(); } catch { }
        try { _indexRebuildCancellation?.Cancel(); } catch { }
        StopSearchStatusHeartbeat();
        StopResourceUsageMonitor();
        if (_preserveLiveResourcesForProcessExit)
        {
            GC.SuppressFinalize(this);
            return;
        }
        _cts?.Dispose();
        _dirAutoCompleteCts?.Dispose();
        _metadataCts.Dispose();
        _semanticCts?.Dispose();
        _indexWarmCancellation?.Dispose();
        _indexBuildCancellation?.Dispose();
        if (_semanticTranslator is IAsyncDisposable semanticDisposable)
        {
            try { _ = semanticDisposable.DisposeAsync().AsTask(); } catch { }
        }
        // The graceful exit path already awaited search cleanup. A timed-out search returned above so
        // its live store remains valid until process teardown; ordinary disposal can delete this file now.
        _resultStore?.Dispose();
        try { _indexWorkerClient?.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Lazily creates (once) and returns the long-lived out-of-process index-worker candidate source used
    /// when <see cref="AppSettings.IndexUseNativeWorker"/> is on (plan §3.3). Reused across searches and
    /// disposed with the view model. Never throws — construction is cheap (the worker process starts lazily
    /// on the first query), and any later worker failure falls back to in-process evaluation.
    /// </summary>
    private Yagu.Services.Index.IIndexCandidateSource GetOrCreateIndexWorkerSource()
    {
        // Shares the single long-lived client with the Stage-3 shadow query pipeline (both created together).
        GetOrCreateIndexWorkerClient();
        return _indexWorkerSource!;
    }

    /// <summary>
    /// Lazily creates (once) and returns the long-lived out-of-process index-worker client, shared by the
    /// in-process-fallback candidate source (<see cref="GetOrCreateIndexWorkerSource"/>) and the Stage-3
    /// mapped-query shadow pipeline so both reuse one worker process. Disposed with the view model.
    /// </summary>
    private Yagu.Services.Index.IndexWorkerClient GetOrCreateIndexWorkerClient()
    {
        lock (_indexWorkerLock)
        {
            if (_indexWorkerClient is null)
            {
                _indexWorkerClient = new Yagu.Services.Index.IndexWorkerClient();
                _indexWorkerSource = new Yagu.Services.Index.IndexWorkerQuerySource(_indexWorkerClient);
            }
            return _indexWorkerClient;
        }
    }
}
