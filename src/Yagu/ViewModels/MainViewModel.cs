using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Yagu.Models;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Ai;
using Yagu.Services.Index;
using Yagu.Services.Ocr;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;
using YaguLogLevel = Yagu.Services.LogLevel;

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

        try { _cts?.Cancel(); } catch { }
        try { _dirAutoCompleteCts?.Cancel(); } catch { }
        try { _metadataCts.Cancel(); } catch { }
        try { _semanticCts?.Cancel(); } catch { }
        try { _indexWarmCancellation?.Cancel(); } catch { }
        try { _indexRebuildCancellation?.Cancel(); } catch { }
        StopSearchStatusHeartbeat();
        StopResourceUsageMonitor();
        _cts?.Dispose();
        _dirAutoCompleteCts?.Dispose();
        _metadataCts.Dispose();
        _semanticCts?.Dispose();
        _indexWarmCancellation?.Dispose();
        if (_semanticTranslator is IAsyncDisposable semanticDisposable)
        {
            try { _ = semanticDisposable.DisposeAsync().AsTask(); } catch { }
        }
        // Cancellation completes asynchronously; leave the lifecycle gate valid for the search finally.
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryIndexed))]
    [NotifyPropertyChangedFor(nameof(CurrentDirectoryIndexRoot))]
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

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQueryText));
        UpdateInlineCalculatorResult(value);
    }

    // ── Inline calculator / unit converter ──
    /// <summary>The formatted inline answer (e.g. <c>"5 km = 3.106856 miles"</c>) when the current
    /// query is a math expression or unit conversion; empty otherwise. Shown as a small banner below
    /// the search box so the user never has to leave Yagu for a quick calculation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineCalculatorResultVisibility))]
    public partial string InlineCalculatorResultText { get; set; } = string.Empty;

    /// <summary>Just the answer (no expression), for the banner's Copy button.</summary>
    public string InlineCalculatorCopyValue { get; private set; } = string.Empty;

    public Microsoft.UI.Xaml.Visibility InlineCalculatorResultVisibility =>
        string.IsNullOrEmpty(InlineCalculatorResultText)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    private void UpdateInlineCalculatorResult(string? query)
    {
        // Only Traditional mode has a literal query box; a natural-language request is never a sum.
        var result = IsSemanticQueryMode ? null : Yagu.Helpers.InlineCalculator.Evaluate(query);
        InlineCalculatorCopyValue = result?.Value ?? string.Empty;
        InlineCalculatorResultText = result?.Display ?? string.Empty;
    }

    /// <summary>Loads the canonical "find code annotations" search — a whole-word regex over
    /// TODO/FIXME/HACK/BUG/XXX/NOTE/OPTIMIZE/REVIEW — into the search box in Traditional regex mode.
    /// The caller submits the search afterwards.</summary>
    public void ApplyCodeAnnotationPreset()
    {
        IsSemanticQueryMode = false;
        UseRegex = true;
        CaseSensitive = true;
        ExactMatch = false;
        Query = Yagu.Helpers.CodeAnnotationQuery.Pattern;
    }

    /// <summary>Loads a curated developer "quick search" (see <see cref="Yagu.Helpers.QuickSearchPresets"/>)
    /// into the search box in Traditional regex mode. The caller submits the search afterwards.</summary>
    public void ApplyQuickSearchPreset(Yagu.Helpers.QuickSearchPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        IsSemanticQueryMode = false;
        UseRegex = true;
        ExactMatch = false;
        CaseSensitive = preset.CaseSensitive;
        Query = preset.Pattern;
    }

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

    partial void OnIsTranslatingSemanticQueryChanged(bool value)
    {
        // When the AI translation step ends, clear the "Canceling.." state — unless a real file scan is
        // still running (a normal semantic run transitions translation → scan; a cancelled one ends both).
        if (!value && !IsSearching) IsCancelling = false;
    }

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
        SemanticSearchAvailable && !IsSearching && !IsPreparingSearch && !IsTranslatingSemanticQuery
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The plain Search/Cancel button is shown when semantic search is unavailable (no mode
    /// chevron) or whenever a search is running — including the semantic translation phase — so it
    /// can morph into the red Cancel action the moment the user clicks Search.</summary>
    public Microsoft.UI.Xaml.Visibility SearchActionButtonVisibility =>
        !SemanticSearchAvailable || IsSearching || IsPreparingSearch || IsTranslatingSemanticQuery
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
        // The inline calculator only applies to the literal (Traditional) query box.
        UpdateInlineCalculatorResult(Query);
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
        catch (Exception ex)
        {
            YaguLog.For("Capability").LogWarning(ex, "Accelerated-hardware detection failed → assuming no acceleration.");
            return false;
        }
    }

    /// <summary>Runs a capability probe, swallowing any fault as "not present" so startup never breaks.</summary>
    private static bool SafeDetect(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            YaguLog.For("Capability").LogWarning(ex, "A capability probe failed → treating the capability as unavailable.");
            return false;
        }
    }

    /// <summary>Reads the machine's dedicated GPU VRAM (bytes) for the larger-model auto-upgrade
    /// decision, swallowing any fault as 0 (unknown) so startup never breaks.</summary>
    private long SafeDetectGpuMemoryBytes()
    {
        try { return _capabilityDetector.GetMaxDedicatedGpuMemoryBytes(); }
        catch (Exception ex)
        {
            YaguLog.For("Capability").LogWarning(ex, "GPU VRAM detection failed → treating available VRAM as unknown (0).");
            return 0;
        }
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

    // ── Group / Filter menu breadcrumbs ──
    // A short "you are here" path shown at the top of the Group and Filter menus when a selection is
    // active, e.g. "Folder \u203A A-Z" or "By date \u203A Last week", so the current choice is visible
    // without opening the submenus. Built on demand by the menu Opening handlers (no live binding needed).
    public bool HasGroupBreadcrumb => GroupMode != GroupMode.None;
    public string GroupBreadcrumb => HasGroupBreadcrumb
        ? $"{GroupModeLabel}  \u203A  {GroupSortDirectionLabel}"
        : string.Empty;

    public bool HasFilterBreadcrumb => DateRangeFilter != DateRangeFilter.None || HasExtensionFilter;
    public string FilterBreadcrumb
    {
        get
        {
            var parts = new List<string>(2);
            if (DateRangeFilter != DateRangeFilter.None)
                parts.Add($"By date  \u203A  {DateRangeFilterLabel}");
            if (HasExtensionFilter)
                parts.Add($"By extension  \u203A  {ExtensionFilterLabel}");
            return string.Join("      ", parts);
        }
    }


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

    /// <summary>When true, PDF files are converted to text (via the bundled Xpdf <c>pdftotext</c>) on a
    /// background queue and their extracted text is searched. Default false. Seeded from the persisted
    /// <c>SearchPdfText</c> setting and surfaced as the Advanced Options ▸ Filters toggle.</summary>
    [ObservableProperty] public partial bool SearchPdfText { get; set; }

    /// <summary>Session-only per-search opt-in to the persistent content index (plan §5/§6.1). Seeded
    /// from the effective default (<see cref="AppSettings.ContentIndexActiveByDefault"/>: master feature
    /// on AND used-by-default on) and surfaced as the Advanced Options ▸ Filters toggle. Never persisted;
    /// it only changes whether the ordinary-text candidate set is pruned and is orthogonal to the image/
    /// PDF/archive content-source toggles. When the master feature is off it has no effect.</summary>
    [ObservableProperty] public partial bool UseContentIndex { get; set; }

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

    /// <summary>Independent OCR worker processes used by image-text search. 0 = conservative automatic;
    /// explicit range 1–4. The effective count is resolved separately for each root so the existing
    /// HDD parallelism safeguard can force one process on rotational media.</summary>
    [ObservableProperty] public partial int ImageOcrWorkerParallelism { get; set; } = AppSettings.DefaultImageOcrWorkerParallelism;

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

    /// <summary>The registered root that covers the directory currently shown in the box, or null.</summary>
    public string? CurrentDirectoryIndexRoot => string.IsNullOrWhiteSpace(Directory)
        ? null
        : IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, Directory!);

    /// <summary>True when the current directory is either an explicit registered index root or lies below
    /// one. The indexing toggle therefore stays selected for <c>C:\src</c> when the single maintained root
    /// is <c>C:\</c>, instead of encouraging a redundant child index.</summary>
    public bool IsCurrentDirectoryIndexed => CurrentDirectoryIndexRoot is not null;


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
    // Configurable preview safety caps (0 = use the built-in default; see the Effective* properties in
    // MainWindow.PreviewCommands.cs / MainWindow.PreviewBuilder.cs). They bound how much of a very large
    // checked selection or a huge file the preview surface prepares/renders so the UI stays responsive.
    [ObservableProperty] public partial int MaxSelectedFilesPerPreview { get; set; }
    [ObservableProperty] public partial int MaxSelectedResultsPerPreview { get; set; }
    [ObservableProperty] public partial int MaxRenderedMatchesPerSection { get; set; }
    [ObservableProperty] public partial int FullFilePreviewLimitMB { get; set; }
    [ObservableProperty] public partial int FullFilePreviewMaxRenderLines { get; set; }
    [ObservableProperty] public partial int FullFilePreviewMaxRenderChars { get; set; }
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
    [ObservableProperty] public partial bool SuppressEverythingIndexCoverageWarning { get; set; }
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

    // ── Status-bar resource indicators (measured off the UI thread; see RunResourceUsageMonitorAsync).
    // TempUsage* reports this process's evicted-result files, IndexUsage* reports all content-index storage,
    // and RamUsage* reports Yagu + its worker children. The index total is cached for one minute and is never
    // refreshed while a search is running. ──
    [ObservableProperty] public partial string TempUsageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TempUsageTooltip { get; set; } = string.Empty;
    [ObservableProperty] public partial string IndexUsageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string IndexUsageTooltip { get; set; } = string.Empty;
    [ObservableProperty] public partial string RamUsageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string RamUsageTooltip { get; set; } = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    public partial bool IsSearching { get; set; }

    /// <summary>True from the instant a search is initiated from the UI until the file scan actually
    /// commits (<see cref="IsSearching"/> flips true) or the pre-search gate phase aborts. It lets the
    /// Search button morph to Cancel and the indeterminate progress bar appear immediately, instead of
    /// waiting out the multi-second pre-search gate work (e.g. the content-index journal-replay readiness
    /// check). Cleared in <see cref="ResetStateForNewSearch"/> when the scan commits, or by
    /// <see cref="EndSearchPreparation"/> when a gate aborts the run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    [NotifyPropertyChangedFor(nameof(SearchProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(SearchProgressRightLabel))]
    [NotifyPropertyChangedFor(nameof(SearchModeSplitButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchActionButtonVisibility))]
    public partial bool IsPreparingSearch { get; set; }

    /// <summary>True while a search is either being prepared (pre-scan gates) or actively scanning. Drives
    /// the progress-overlay visibility so the bar also shows during the gate phase.</summary>
    public bool IsSearchActive => IsSearching || IsPreparingSearch;

    /// <summary>True during the fast filename name-first pass (and its brief priority content scan), before
    /// the full-drive scan total is established. Set true when a scan commits and latched false once a
    /// progress snapshot reports the full phase, so the bar stays indeterminate across preparing + name-first
    /// and only becomes determinate for the single 0→100% content-scan climb.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(SearchProgressRightLabel))]
    public partial bool SearchInNameFirstPhase { get; set; }

    /// <summary>The search progress bar is indeterminate while preparing (pre-scan gates) or during the
    /// name-first pass, and determinate for the full content scan (a single 0→100% climb).</summary>
    public bool SearchProgressIndeterminate => IsPreparingSearch || SearchInNameFirstPhase;

    /// <summary>True from the instant the user clicks Cancel until the in-flight file scan or semantic
    /// translation actually stops. Cancellation isn't instantaneous — a large search keeps draining
    /// buffered results for a moment after <see cref="CancelAsync"/> fires — so this drives the morphing
    /// Cancel button into a disabled "Canceling.." state, giving immediate feedback and preventing a
    /// second Cancel click while the first is still in progress. Reset automatically when the search or
    /// translation ends (see <see cref="OnIsSearchingChanged"/> / <see cref="OnIsTranslatingSemanticQueryChanged"/>).</summary>
    [ObservableProperty]
    public partial bool IsCancelling { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? FallbackReason { get; set; }

    /// <summary>Main-window content-index availability indicator (plan §6.2). Reflects whether a usable
    /// index exists for the folder(s) the current search covers. It is presence-only and never implies
    /// acceleration — the tooltip states files are still read live in this build. Updated per search by
    /// <see cref="RefreshIndexStatusAsync"/>; hidden unless the feature and its status setting are on.</summary>
    private string _indexStatusText = string.Empty;
    public string IndexStatusText
    {
        get => _indexStatusText;
        // The status bar gives this label a fixed slot, so clamp centrally instead of trusting every
        // caller to keep its wording short enough to avoid a mid-word ellipsis.
        set
        {
            if (SetProperty(ref _indexStatusText, ContentIndexUiStatus.TrimStatusLabel(value)))
                OnPropertyChanged(nameof(IndexHealthyCheckVisibility));
        }
    }

    /// <summary>The status glyph. When it is the caution triangle the indicator paints an amber variant
    /// (<see cref="IndexStatusWarningVisibility"/>) instead of the muted default-coloured glyph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexStatusWarningVisibility))]
    [NotifyPropertyChangedFor(nameof(IndexStatusNormalGlyphVisibility))]
    public partial string IndexStatusGlyph { get; set; } = string.Empty;

    public Microsoft.UI.Xaml.Visibility IndexStatusWarningVisibility =>
        string.Equals(IndexStatusGlyph, ContentIndexUiStatus.StatusWarningGlyph, StringComparison.Ordinal)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility IndexStatusNormalGlyphVisibility =>
        string.Equals(IndexStatusGlyph, ContentIndexUiStatus.StatusWarningGlyph, StringComparison.Ordinal)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility IndexHealthyCheckVisibility =>
        string.Equals(IndexStatusText, "Indexes: all healthy", StringComparison.Ordinal)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexStatusAccessibleHelpText))]
    public partial string IndexStatusTooltip { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowIndexStatus { get; set; }

    /// <summary>One line per ready local drive and explicitly maintained index root. Unlike the
    /// current-search status, this launch-time snapshot is not discarded when a search changes roots.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllDriveIndexStatusVisibility))]
    [NotifyPropertyChangedFor(nameof(IndexStatusAccessibleHelpText))]
    public partial string AllDriveIndexStatusText { get; set; } = string.Empty;

    public string IndexStatusAccessibleHelpText => string.IsNullOrWhiteSpace(AllDriveIndexStatusText)
        ? IndexStatusTooltip
        : IndexStatusTooltip + Environment.NewLine + Environment.NewLine
            + "All-drive and indexed-folder health:" + Environment.NewLine + AllDriveIndexStatusText;

    /// <summary>True while the current search root's query index is being loaded into the process-wide
    /// immutable query cache. The status bar shows "Indexing: preparing..." until this becomes false.</summary>
    [ObservableProperty] public partial bool IsIndexWarmActive { get; set; }

    /// <summary>True after a user starts a search while warming and the warm has been cancelled until that
    /// search finishes. This is separate from pausing index builds.</summary>
    [ObservableProperty] public partial bool IsIndexWarmPausedForSearch { get; set; }

    /// <summary>The root currently warming, or the root queued to resume after the active search.</summary>
    public string? ActiveIndexWarmFolder => _activeIndexWarmFolder ?? _resumeIndexWarmFolder;

    /// <summary>Big, highly-visible percent for the custom "Indexing…" tooltip (e.g. "47%"); paired with
    /// <see cref="ShowIndexBuildPercent"/> and <see cref="IndexBuildPercentValue"/>. Distinct from the
    /// descriptive <see cref="IndexStatusTooltip"/> text — only populated while a build is actively running
    /// with a known estimate.</summary>
    [ObservableProperty] public partial string IndexBuildPercentText { get; set; } = string.Empty;

    /// <summary>The estimated percent-complete (0–99) driving the progress bar in the custom "Indexing…"
    /// tooltip.</summary>
    [ObservableProperty] public partial int IndexBuildPercentValue { get; set; }

    /// <summary>True while the custom "Indexing…" tooltip should show the big percent + progress bar (a
    /// build is running and its estimate is known). Drives <see cref="IndexBuildPercentVisibility"/>.</summary>
    [ObservableProperty] public partial bool ShowIndexBuildPercent { get; set; }

    /// <summary>True while an on-demand index rebuild triggered from the status-bar indicator's
    /// "Index date … (click to rebuild)" menu is running. Drives the full-window blocking overlay that
    /// prevents any other interaction until the rebuild finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelIndexRebuild))]
    public partial bool IsIndexRebuildBlocking { get; set; }

    private bool _indexBlockingOperationIsRebuild = true;

    /// <summary>True after the user requests cancellation until the worker-backed rebuild has observed
    /// its token and unwound. Keeps the overlay visible while disabling the button against repeat clicks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelIndexRebuild))]
    [NotifyPropertyChangedFor(nameof(IndexRebuildCancelButtonText))]
    public partial bool IsIndexRebuildCancelling { get; set; }

    public bool CanCancelIndexRebuild => IsIndexRebuildBlocking && !IsIndexRebuildCancelling;
    public string IndexRebuildOverlayTitle => _indexBlockingOperationIsRebuild
        ? "Rebuilding the content index"
        : "Building the content index";
    public string IndexRebuildCancelButtonText => IsIndexRebuildCancelling
        ? "Canceling…"
        : _indexBlockingOperationIsRebuild ? "Cancel rebuild" : "Cancel build";

    /// <summary>The 0–100 progress of the blocking index rebuild, driving the overlay's progress bar.</summary>
    [ObservableProperty] public partial double IndexRebuildProgressPercent { get; set; }

    /// <summary>The descriptive status line of the blocking index rebuild overlay (which root, how far).</summary>
    [ObservableProperty] public partial string IndexRebuildProgressText { get; set; } = string.Empty;

    /// <summary>Whole-number percent label for the blocking index rebuild overlay (e.g. "42%").</summary>
    public string IndexRebuildProgressPercentLabel => $"{IndexRebuildProgressPercent:F0}%";
    partial void OnIndexRebuildProgressPercentChanged(double value) => OnPropertyChanged(nameof(IndexRebuildProgressPercentLabel));

    /// <summary>Searched folders that have no content index yet and are not already registered in
    /// <see cref="AppSettings.IndexedRoots"/>. When non-empty the status indicator can offer Add folder.</summary>
    public IReadOnlyList<string> IndexStatusFoldersWithoutIndex { get; private set; } = Array.Empty<string>();

    /// <summary>Searched folders that are already registered in <see cref="AppSettings.IndexedRoots"/>
    /// but do not have a usable on-disk generation yet. These need Build now, not Add folder.</summary>
    public IReadOnlyList<string> IndexStatusRegisteredFoldersWithoutIndex { get; private set; } = Array.Empty<string>();

    /// <summary>True when clicking the main-window index indicator can offer to register a searched folder.</summary>
    public bool IndexStatusCanAddFolder => IndexStatusFoldersWithoutIndex.Count > 0;

    /// <summary>True when clicking the status should open Settings ▸ Indexing to build a registered root.</summary>
    public bool IndexStatusCanBuildRegisteredFolder => IndexStatusRegisteredFoldersWithoutIndex.Count > 0;

    // Background index-build activity (onboarding build, startup auto-build, Settings "Build now"): while
    // any is running the main-window index indicator shows "Indexing…" instead of availability/coverage.
    private int _activeIndexBuilds;
    private string? _activeIndexBuildFolder;
    private bool _activeIndexBuildIsIncremental;
    private IReadOnlyList<string> _lastIndexStatusRoots = Array.Empty<string>();
    private bool _lastIndexStatusUseThisSearch;
    // Searched roots that currently have a readable on-disk index, plus the oldest of their build times.
    // Captured per search by RefreshIndexStatusAsync and read (on the UI thread) when the status-bar
    // indicator's right-click menu builds its "Index date … (click to rebuild)" item.
    private IReadOnlyList<string> _currentIndexBuiltRoots = Array.Empty<string>();
    private DateTimeOffset? _currentIndexBuiltUtc;
    private readonly Dictionary<string, (DateTimeOffset? CreatedUtc, DateTimeOffset? BuiltUtc, DateTimeOffset? LastIncrementalUpdateUtc)> _currentIndexDatesByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContentIndexManager.ScopeFreshnessStatus> _currentIndexFreshnessByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    // Global launch-time health remains separate from current-search coverage. Search callbacks may
    // replace the context label/tooltip, but an unhealthy drive still keeps warning precedence and the
    // hover overlay always retains every drive/root row.
    private IReadOnlyList<IndexRootHealthEntry> _allDriveIndexHealth = Array.Empty<IndexRootHealthEntry>();
    public IReadOnlyList<IndexRootHealthEntry> AllDriveIndexHealth => _allDriveIndexHealth;
    private int _allDriveIndexHealthRefreshGeneration;
    // Session-only: set when the user turns the content index off (persistent) from the status-indicator
    // menu, so the indicator stays visible as "Index: off" this session — otherwise the menu that offers
    // "Enable indexing" would vanish with the indicator. Reset by re-enabling and never persisted.
    private bool _indexOffIndicatorSticky;
    // Immediate B0 index-routing status for the current search. Updated on the UI thread by the
    // per-root gate callbacks; prevents the slower availability refresh from overwriting a known bypass.
    private int _indexRuntimeStatusRunId;
    private readonly HashSet<string> _indexRuntimeAttemptedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _indexRuntimeAcceleratedRootPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _indexRuntimeBypassReasonsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private string? _indexRuntimeBypassReason;

    // Right-click "pause indexing": a VM-owned cancellation source that main-window-tracked builds observe,
    // plus the folder to re-kick when the user resumes.
    private CancellationTokenSource? _indexBuildCancellation;
    // Dedicated cancellation for the full-window on-demand rebuild. Linked to the shared build token so
    // global pause/disable still stops it, but canceling this overlay does not leave indexing paused.
    private CancellationTokenSource? _indexRebuildCancellation;
    private string? _pausedIndexBuildFolder;

    /// <summary>
    /// Hook the main window installs so <see cref="ResumeIndexing"/> can re-run the multi-root
    /// auto/startup/scheduled build pass (which the view owns) when the paused build had no single tracked
    /// folder. Without it, resuming such a pass could only clear the indicator to "index available".
    /// Returns a task that runs the pass over the registered folders; never throws.
    /// </summary>
    public Func<Task>? ResumeAutoIndexBuildAsync { get; set; }

    /// <summary>Session-only Developer Options override for the WhenIdle maintenance trigger. This
    /// changes only the idle verdict; the normal trigger, pause, search, battery, disk-space, and build
    /// eligibility gates still apply. It deliberately is not copied to <see cref="AppSettings"/>.</summary>
    [ObservableProperty] public partial bool SimulateSystemIdle { get; set; }

    /// <summary>Hook installed by the main window so Developer Options can evaluate the real WhenIdle
    /// scheduler immediately instead of waiting for its next 30-second timer tick.</summary>
    public Func<Task>? RequestIdleIndexMaintenanceAsync { get; set; }

    // Set when a build was stopped because the index drive hit the used-space limit; makes the indicator
    // show a disk-full warning (instead of the generic paused state) until the user resumes.
    private string? _indexDiskFullMessage;

    // Estimated percent-complete (0–99) of the active build, or -1 when unknown. Shown at the end of the
    // "Indexing…" tooltip. Reported periodically by the build via ReportIndexBuildProgress.
    private int _indexBuildPercent = -1;

    /// <summary>True while one or more background index builds are running (drives the "Indexing…" indicator).</summary>
    public bool IsIndexBuildActive => _activeIndexBuilds > 0;

    /// <summary>The folder whose index is actively building for a single-folder build (onboarding add /
    /// Settings "Build now"), or null for a multi-root pass with no single tracked folder. Lets the
    /// Settings folder list overlay a live "Indexing… N%" on the exact row being built.</summary>
    public string? ActiveIndexBuildFolder => _activeIndexBuildFolder;

    /// <summary>True when the active tracked index operation is an incremental journal update rather than
    /// a complete staged build. Used by exit warnings so interruption consequences are described accurately.</summary>
    public bool IsActiveIndexBuildIncremental => _activeIndexBuildIsIncremental;

    /// <summary>Session-only flag: the user paused indexing from the status-bar indicator's right-click menu.
    /// While set, tracked builds are cancelled and auto/startup/watcher builds are skipped until resumed.</summary>
    [ObservableProperty] public partial bool IsIndexingPaused { get; set; }

    /// <summary>True when the status-bar indicator can offer "Pause indexing" (a build is active and not
    /// already paused).</summary>
    public bool CanPauseIndexing => IsIndexBuildActive && !IsIndexingPaused;

    /// <summary>A cancellation token for main-window-tracked index builds; cancelled when the user pauses
    /// indexing. Callers pass it to <c>BuildScope</c> / the auto-builder so pause stops the work promptly.</summary>
    public CancellationToken IndexBuildCancellationToken
    {
        get
        {
            _indexBuildCancellation ??= new CancellationTokenSource();
            return _indexBuildCancellation.Token;
        }
    }

    partial void OnIsIndexingPausedChanged(bool value) => OnPropertyChanged(nameof(CanPauseIndexing));

    [ObservableProperty] public partial int FilesScanned { get; set; }
    [ObservableProperty] public partial int TotalFiles { get; set; }

    // .yagu-session save/load progress (0.0..1.0 while busy).
    [ObservableProperty] public partial bool IsSessionBusy { get; set; }
    [ObservableProperty] public partial double SessionProgressPercent { get; set; }
    [ObservableProperty] public partial string SessionProgressText { get; set; } = string.Empty;

    public bool IsSessionIdle => !IsSessionBusy;
    partial void OnIsSessionBusyChanged(bool value) => OnPropertyChanged(nameof(IsSessionIdle));

    // Whole-number percent label for the full-window session busy overlay (e.g. "42%").
    public string SessionProgressPercentLabel => $"{SessionProgressPercent:F0}%";
    partial void OnSessionProgressPercentChanged(double value) => OnPropertyChanged(nameof(SessionProgressPercentLabel));

    public string ProgressTooltip
    {
        get
        {
            if (TotalFiles > 0)
            {
                // FilesScanned can momentarily exceed a slightly stale TotalFiles between 100 ms
                // snapshots; clamp so the tooltip never reads over 100%.
                double pct = Math.Min(100.0, (double)FilesScanned / TotalFiles * 100);
                string baseText = $"{pct:F1}% complete ({FilesScanned:N0} files out of {TotalFiles:N0} total files)";
                string? phase = _sourceBackedSearchProgress?.BuildPhaseLabel(FilesScanned, TotalFiles);
                return phase is null ? baseText : baseText + Environment.NewLine + phase;
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

    private SourceBackedSearchProgress? _sourceBackedSearchProgress;

    /// <summary>Whole-number completion label shown at the far-right edge of the search progress bar.
    /// Empty while discovery has not produced a total; clamped because progress snapshots can briefly
    /// report more processed files than a slightly stale total.</summary>
    public string SearchProgressPercentLabel => TotalFiles > 0
        ? $"{Math.Min(100.0, (double)FilesScanned / TotalFiles * 100):F0}%"
        : string.Empty;

    private string _searchProgressPhaseLabel = string.Empty;

    /// <summary>Right-edge progress text: the normal rounded percent during discovery/native scanning,
    /// then an explicit OCR/PDF counter while slow extraction workers drain their remaining queue.</summary>
    public string SearchProgressRightLabel => SearchProgressIndeterminate
        ? string.Empty
        : string.IsNullOrEmpty(_searchProgressPhaseLabel)
            ? SearchProgressPercentLabel
            : _searchProgressPhaseLabel;

    partial void OnFilesScannedChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressTooltip));
        OnPropertyChanged(nameof(SearchProgressPercentLabel));
        OnPropertyChanged(nameof(SearchProgressRightLabel));
    }

    partial void OnTotalFilesChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressTooltip));
        OnPropertyChanged(nameof(SearchProgressPercentLabel));
        OnPropertyChanged(nameof(SearchProgressRightLabel));
    }

    private void UpdateSearchProgressPhaseLabel(SearchProgress progress)
    {
        _sourceBackedSearchProgress = progress.SourceBacked;
        string next = progress.SourceBacked?.BuildCombinedLabel(progress.FilesScanned, progress.TotalFiles)
            ?? string.Empty;
        if (string.Equals(next, _searchProgressPhaseLabel, StringComparison.Ordinal))
            return;
        _searchProgressPhaseLabel = next;
        OnPropertyChanged(nameof(SearchProgressRightLabel));
        OnPropertyChanged(nameof(ProgressTooltip));
    }
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

    // The "Filter files…" box only makes sense once a search has produced files. It keys off the
    // UNFILTERED result set (AllGroups), NOT HasResults (which reflects the filtered/visible groups) —
    // otherwise typing a filter that matches nothing would empty the visible groups and hide the very
    // box the user is typing in, trapping them. Its change notification piggybacks on HasResults via
    // the OnPropertyChanged override below (HasResults is raised at every point AllGroups can cross
    // empty/non-empty: first result streamed in, search completion, clear; and harmlessly on filter,
    // where AllGroups is unchanged so the box stays visible).
    public Microsoft.UI.Xaml.Visibility ResultFileFilterVisibility =>
        _resultCollection.AllGroups.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static readonly System.ComponentModel.PropertyChangedEventArgs s_resultFileFilterVisibilityChangedArgs =
        new(nameof(ResultFileFilterVisibility));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HasResults))
            base.OnPropertyChanged(s_resultFileFilterVisibilityChangedArgs);
    }

    public Microsoft.UI.Xaml.Visibility StatsForNerdsVisibility =>
        ShowStatsForNerds
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ResourceUsageStatusVisibility =>
        ShowResourceUsageInStatusBar
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility SkippedCountVisibility =>
        HasPerformedSearch
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility IndexStatusVisibility =>
        ShowIndexStatus
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility AllDriveIndexStatusVisibility =>
        string.IsNullOrWhiteSpace(AllDriveIndexStatusText)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility IndexBuildPercentVisibility =>
        ShowIndexBuildPercent
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
            if (b.IoTimeout > 0)      lines.AppendLine(CultureInfo.InvariantCulture, $"  ⏱️  I/O timeouts          {b.IoTimeout,8:N0}");
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
    partial void OnShowResourceUsageInStatusBarChanged(bool value) => OnPropertyChanged(nameof(ResourceUsageStatusVisibility));
    partial void OnShowAutoScrollResultsCheckboxChanged(bool value) => OnPropertyChanged(nameof(AutoScrollResultsCheckboxVisibility));
    partial void OnHasPerformedSearchChanged(bool value) => OnPropertyChanged(nameof(SkippedCountVisibility));
    partial void OnShowIndexStatusChanged(bool value) => OnPropertyChanged(nameof(IndexStatusVisibility));

    partial void OnShowIndexBuildPercentChanged(bool value) => OnPropertyChanged(nameof(IndexBuildPercentVisibility));
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
        LogService.Instance.FileLevel = (YaguLogLevel)value;
        YaguLog.For("Settings").LogInformation("File log level changed to {Level}", (YaguLogLevel)value);
    }
    partial void OnConsoleLogLevelIndexChanged(int value)
    {
        LogService.Instance.ConsoleLevel = (YaguLogLevel)value;
        YaguLog.For("Settings").LogInformation("Console log level changed to {Level}", (YaguLogLevel)value);
    }

    partial void OnFileListerBackendIndexChanged(int value)
    {
        var backend = (FileListerBackend)value;
        FileLister.Backend = backend;
        YaguLog.For("Settings").LogInformation("FileLister backend set to {Backend}", backend);
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
                // A single-token query already set an accurate passthrough status inside
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

            // The user clicked Cancel during the pre-search gate phase (before the scan committed) — abort.
            if (IsSearchPreparationCancellationRequested)
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
        if (IsTranslatingSemanticQuery) IsCancelling = true;
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

        // A single token cannot express a natural-language search request. Skip model startup entirely
        // and let the caller run a plain Traditional search for the typed text. Set an accurate status
        // so the caller's generic "AI couldn't interpret that" message is not shown.
        if (SemanticQuerySalvage.IsSingleTokenQuery(text))
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
            YaguLog.For("SemanticSearch").LogWarning(ex, "Translation failed: {Error}", ex.Message);
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

            YaguLog.For("SemanticSearch").LogInformation(
                "Foundry model update check: {CatalogCount} catalog model(s), {NewCount} new, baselineSeeded={BaselineSeeded}.",
                currentModels.Count, result.Changes.Count, result.BaselineSeeded);
            return result.Changes;
        }
        catch (OperationCanceledException)
        {
            return none;
        }
        catch (Exception ex)
        {
            YaguLog.For("SemanticSearch").LogWarning(ex, "Foundry model update check failed: {Error}", ex.Message);
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
        string normalizedDirectory = DriveEnumerator.NormalizeSearchRoot(Directory);
        if (normalizedDirectory.Length > 0)
            return [normalizedDirectory];

        return DriveEnumerator.GetSearchRoots(
            SearchAllDrivesIncludesNetwork,
            SearchAllDrivesIncludesRemovable,
            SearchAllDrivesIncludesCloud);
    }

    /// <summary>
    /// Updates the main-window content-index availability indicator (plan §6.2) for the folders a
    /// search covers. It reports only whether a usable index <em>exists</em> for each root — a fact
    /// knowable today from generation existence alone, with no USN journal, worker, or pruning — so it
    /// is safe and honest before the deferred hot-path integration lands. The read runs off the UI
    /// thread through the managed <see cref="ContentIndexManager"/> (crash-safe: it never memory-maps
    /// an index file and validates checksums), and a missing/corrupt scope counts as "no index" rather
    /// than throwing into the UI. The indicator never implies acceleration; its tooltip states files
    /// are still read live in this build.
    /// </summary>
    private async Task RefreshIndexStatusAsync(IReadOnlyList<string> roots, bool useThisSearch)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
        {
            // Keep a muted "Index: off" indicator visible after a menu-driven persistent disable (this
            // session only) so the status menu — and its "Enable indexing" command — stays reachable.
            if (_indexOffIndicatorSticky && !_settings.EnableContentIndex && _settings.ShowIndexStatusInMainWindow)
                ShowIndexDisabledIndicator();
            else
                ShowIndexStatus = false;
            IndexStatusFoldersWithoutIndex = Array.Empty<string>();
            IndexStatusRegisteredFoldersWithoutIndex = Array.Empty<string>();
            _currentIndexBuiltRoots = Array.Empty<string>();
            _currentIndexBuiltUtc = null;
            _currentIndexDatesByRoot.Clear();
            _currentIndexFreshnessByRoot.Clear();
            OnPropertyChanged(nameof(IndexStatusCanAddFolder));
            OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));
            return;
        }

        var rootsCopy = roots.ToArray();
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount);
        string storageDir = _settings.IndexStorageDirectory;
        bool masterEnabled = _settings.EnableContentIndex;
        int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(_settings.IndexMaxJournalCatchupRecords);

        // Remember the search context so a finishing background build can recompute the indicator for it.
        _lastIndexStatusRoots = rootsCopy;
        _lastIndexStatusUseThisSearch = useThisSearch;

        IndexAvailability availability;
        List<string> missingRoots;
        List<(string Root, DateTimeOffset? BuiltUtc, DateTimeOffset? CreatedUtc, DateTimeOffset? LastIncrementalUpdateUtc, ContentIndexManager.ScopeFreshnessStatus Freshness)> builtRoots;
        try
        {
            (availability, missingRoots, builtRoots) = await Task.Run(() =>
            {
                var provider = DefaultContentIndexPathProvider.Create(storageDir);
                var manager = new ContentIndexManager(provider, retained);
                int withIndex = 0;
                var missing = new List<string>();
                var built = new List<(string, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?, ContentIndexManager.ScopeFreshnessStatus)>();
                foreach (string root in rootsCopy)
                {
                    try
                    {
                        string indexRoot = manager.ResolveBestAvailableIndexRoot(root, _settings.IndexedRoots);
                        IndexMetadataStatus meta = manager.GetMetadataStatusForRoot(indexRoot);
                        if (meta.Exists && meta.MetadataReadable && meta.Health == IndexStorageHealth.Healthy)
                        {
                            ContentIndexManager.ScopeFreshnessStatus freshness = manager.GetScopeFreshnessStatus(
                                indexRoot,
                                ContentIndexFreshnessEvaluator.CreateReader(
                                    maxCatchupRecords,
                                    TimeSpan.FromSeconds(AppSettings.NormalizeFileIoTimeoutSeconds(_settings.FileIoTimeoutSeconds))));
                            if (!freshness.NeedsAttention)
                                withIndex++;
                            if (!built.Any(item => string.Equals(item.Item1, indexRoot, StringComparison.OrdinalIgnoreCase)))
                                built.Add((indexRoot, meta.BuiltUtc, meta.CreatedUtc, meta.LastIncrementalUpdateUtc, freshness));
                        }
                        else
                        {
                            missing.Add(root);
                        }
                    }
                    catch
                    {
                        // A missing/corrupt scope simply counts as "no index"; never throw into the UI.
                        missing.Add(root);
                    }
                }
                return (ContentIndexUiStatus.Availability(masterEnabled, useThisSearch, withIndex, rootsCopy.Length), missing, built);
            }).ConfigureAwait(true);
        }
        catch
        {
            ShowIndexStatus = false;
            IndexStatusFoldersWithoutIndex = Array.Empty<string>();
            IndexStatusRegisteredFoldersWithoutIndex = Array.Empty<string>();
            _currentIndexBuiltRoots = Array.Empty<string>();
            _currentIndexBuiltUtc = null;
            _currentIndexDatesByRoot.Clear();
            _currentIndexFreshnessByRoot.Clear();
            OnPropertyChanged(nameof(IndexStatusCanAddFolder));
            OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));
            ApplyAllDriveIndexHealthStatus(force: !IsSearchActive);
            return;
        }

        // Capture which searched roots currently have a readable index and the oldest of their build times,
        // so the status-bar right-click menu can show "Index date … (click to rebuild)" for them.
        _currentIndexBuiltRoots = builtRoots.Select(b => b.Root).ToArray();
        _currentIndexDatesByRoot.Clear();
        _currentIndexFreshnessByRoot.Clear();
        foreach (var built in builtRoots)
        {
            _currentIndexDatesByRoot[IndexScopeIdentity.NormalizePath(built.Root)] =
                (built.CreatedUtc ?? built.BuiltUtc, built.BuiltUtc, built.LastIncrementalUpdateUtc);
            _currentIndexFreshnessByRoot[IndexScopeIdentity.NormalizePath(built.Root)] = built.Freshness;
        }
        DateTimeOffset? oldestBuilt = null;
        foreach (var built in builtRoots)
        {
            DateTimeOffset? builtUtc = built.BuiltUtc;
            if (builtUtc is { } t && (oldestBuilt is null || t < oldestBuilt))
                oldestBuilt = t;
        }
        _currentIndexBuiltUtc = oldestBuilt;

        bool addable = availability is IndexAvailability.None or IndexAvailability.Partial;
        string[] registeredMissing = addable
            ? missingRoots.Where(root => IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is not null).ToArray()
            : Array.Empty<string>();
        string[] unregisteredMissing = addable
            ? missingRoots.Where(root => IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is null).ToArray()
            : Array.Empty<string>();
        // Only genuinely unregistered roots flow into Add folder. A registered-but-unbuilt root opens
        // Settings ▸ Indexing instead, where Build now can create its first on-disk generation.
        IndexStatusFoldersWithoutIndex = unregisteredMissing;
        IndexStatusRegisteredFoldersWithoutIndex = registeredMissing;
        OnPropertyChanged(nameof(IndexStatusCanAddFolder));
        OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));

        // Background build/warm activity owns the indicator until it finishes.
        if (_activeIndexBuilds > 0 || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return;
        // A B0 gate attempt has already produced a more precise status for this search (accelerating or
        // bypassed). Do not replace it with the coarser presence-only "Index: available" result.
        if (_indexRuntimeStatusRunId == Volatile.Read(ref _searchRunId)
            && _indexRuntimeAttemptedRoots.Count > 0)
            return;

        KeyValuePair<string, ContentIndexManager.ScopeFreshnessStatus>[] freshnessFailures = _currentIndexFreshnessByRoot
            .Where(static pair => pair.Value.NeedsAttention)
            .ToArray();
        if (freshnessFailures.Length > 0)
        {
            int rebuildCount = freshnessFailures.Count(static pair => pair.Value.RequiresRebuild);
            IndexStatusGlyph = ContentIndexUiStatus.StatusWarningGlyph;
            IndexStatusText = rebuildCount switch
            {
                1 => "Index: rebuild required",
                > 1 => $"Index: {rebuildCount} rebuilds required",
                _ => "Index: freshness unavailable",
            };
            IndexStatusTooltip = "One or more index files are structurally valid, but their drive change-journal freshness can no longer be proven. "
                + string.Join(" ", freshnessFailures.Select(static pair => $"{pair.Key}: {pair.Value.Problem}"))
                + BuildIndexRootStatusDetails()
                + BuildIndexDateDetails()
                + (rebuildCount > 0
                    ? " Hover to rebuild the repairable index, or open Settings \u25B8 Indexing for details."
                    : " Open Settings \u25B8 Indexing for details.");
            ShowIndexStatus = true;
            ApplyAllDriveIndexHealthStatus(force: !IsSearchActive);
            return;
        }

        bool onlyRegisteredUnbuilt = availability == IndexAvailability.None
            && registeredMissing.Length > 0
            && unregisteredMissing.Length == 0;
        IndexStatusGlyph = onlyRegisteredUnbuilt
            ? ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Bypassed)
            : ContentIndexUiStatus.AvailabilityGlyph(availability);
        IndexStatusText = onlyRegisteredUnbuilt
            ? (rootsCopy.Length == 1 ? "Index: not built for this folder" : "Index: registered but not built")
            : (rootsCopy.Length > 1 && availability == IndexAvailability.None
                ? "Index: none"
                : ContentIndexUiStatus.AvailabilityLabel(availability));
        string tooltip = ContentIndexUiStatus.AvailabilityTooltip(availability);
        if (registeredMissing.Length > 0)
            tooltip = (registeredMissing.Length == 1 && rootsCopy.Length == 1
                    ? "This folder is in your indexed-folders list, but it has no usable index yet. "
                    : registeredMissing.Length == 1
                        ? "One searched folder is in your indexed-folders list but has no usable index yet. "
                    : "Some searched folders are in your indexed-folders list but have no usable index yet. ")
                + "Click to open Settings \u25B8 Indexing and choose Build now.";
        if (unregisteredMissing.Length > 0)
            tooltip += " Click to add a folder to the index.";
        tooltip += BuildIndexRootStatusDetails();
        tooltip += BuildIndexDateDetails();
        // Not currently building: explain when indexing runs (manual / at startup / when idle).
        tooltip += BuildIndexSchedulingDetails();
        IndexStatusTooltip = tooltip;
        ShowIndexStatus = ContentIndexUiStatus.ShouldShowAvailability(availability);
        ApplyAllDriveIndexHealthStatus(force: !IsSearchActive);
    }

    /// <summary>Refreshes search-context index health for the directory currently shown in the search
    /// box. Called when the user commits a directory and around searches/builds; launch-time global
    /// visibility is handled separately by <see cref="RefreshAllDriveIndexStatus"/>.</summary>
    public void RefreshCurrentIndexStatus()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(RefreshCurrentIndexStatus);
            return;
        }
        if (_disposed)
            return;

        _ = RefreshIndexStatusAsync(
            ResolveTargetRoots(),
            UseContentIndex && _settings.EnableContentIndex);
    }

    /// <summary>Builds a launch-time health snapshot for every ready local fixed drive plus every
    /// explicitly maintained index root. The snapshot is deliberately independent of the current
    /// search directory, so changing/searching one folder cannot hide a bad index on another drive.</summary>
    public void RefreshAllDriveIndexStatus()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(RefreshAllDriveIndexStatus);
            return;
        }
        if (_disposed)
            return;

        int generation = Interlocked.Increment(ref _allDriveIndexHealthRefreshGeneration);
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
        {
            _allDriveIndexHealth = Array.Empty<IndexRootHealthEntry>();
            AllDriveIndexStatusText = string.Empty;
            return;
        }

        AllDriveIndexStatusText = "Checking local drive index health…";
        if (!IsIndexBuildActive && !IsIndexWarmActive && !IsIndexWarmPausedForSearch && !IsSearchActive)
        {
            IndexStatusGlyph = "\uE895"; // sync/checking
            IndexStatusText = "Index: checking all drives";
            IndexStatusTooltip = "Yagu is checking the content-index metadata and change-journal freshness for every ready local drive.";
            ShowIndexStatus = true;
        }

        string[] registeredRoots = IndexedRootsPolicy.Normalize(_settings.IndexedRoots).ToArray();
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount);
        string storageDir = _settings.IndexStorageDirectory;
        int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(_settings.IndexMaxJournalCatchupRecords);
        int fileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(_settings.FileIoTimeoutSeconds);
        _ = RefreshAllDriveIndexStatusAsync(
            generation,
            registeredRoots,
            retained,
            storageDir,
            maxCatchupRecords,
            fileIoTimeoutSeconds);
    }

    private async Task RefreshAllDriveIndexStatusAsync(
        int generation,
        string[] registeredRoots,
        int retained,
        string storageDir,
        int maxCatchupRecords,
        int fileIoTimeoutSeconds)
    {
        IReadOnlyList<IndexRootHealthEntry> health;
        try
        {
            health = await Task.Run(() =>
            {
                string[] roots = DriveEnumerator.GetSearchRoots(
                        includeNetwork: false,
                        includeRemovable: false,
                        includeCloud: false)
                    .Concat(registeredRoots)
                    .Where(static root => !string.IsNullOrWhiteSpace(root))
                    .Select(IndexScopeIdentity.NormalizePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var provider = DefaultContentIndexPathProvider.Create(storageDir);
                var manager = new ContentIndexManager(provider, retained);
                var rows = new List<IndexRootHealthEntry>(roots.Length);
                foreach (string root in roots)
                {
                    try
                    {
                        rows.Add(ReadAllDriveIndexHealth(
                            manager,
                            root,
                            registeredRoots,
                            maxCatchupRecords,
                            fileIoTimeoutSeconds));
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new IndexRootHealthEntry(
                            root,
                            IndexRootHealthKind.StorageProblem,
                            $"health check failed ({ex.GetType().Name}) — searches scan live"));
                    }
                }
                return (IReadOnlyList<IndexRootHealthEntry>)rows;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            health = new IndexRootHealthEntry[]
            {
                new IndexRootHealthEntry(
                    "Local drives",
                    IndexRootHealthKind.StorageProblem,
                    $"health check failed ({ex.GetType().Name}) — searches scan live"),
            };
        }

        if (_disposed || generation != Volatile.Read(ref _allDriveIndexHealthRefreshGeneration))
            return;

        _allDriveIndexHealth = health;
        AllDriveIndexStatusText = string.Join(
            Environment.NewLine,
            health.Select(static row => $"{row.Root} — {row.Status}"));
        ApplyAllDriveIndexHealthStatus(force: true);
    }

    private static IndexRootHealthEntry ReadAllDriveIndexHealth(
        ContentIndexManager manager,
        string root,
        IReadOnlyList<string> registeredRoots,
        int maxCatchupRecords,
        int fileIoTimeoutSeconds)
    {
        bool registered = IndexedRootsPolicy.FindBestCoveringRoot(registeredRoots, root) is not null;
        if (!registered)
        {
            // A ready drive remains in the all-drive overview after it is removed from IndexedRoots, but
            // any exact on-disk scope is now leftover/unmaintained data. Do not keep evaluating its journal
            // or let it raise a global freshness warning; Settings ▸ Indexing still surfaces it for add/delete.
            IndexMetadataStatus leftover = manager.GetMetadataStatusForRoot(root);
            return ContentIndexUiStatus.UnregisteredRootHealth(root, leftover.Exists);
        }

        string indexRoot = manager.ResolveBestAvailableIndexRoot(root, registeredRoots);
        IndexMetadataStatus metadata = manager.GetMetadataStatusForRoot(indexRoot);

        if (metadata.Exists && metadata.MetadataReadable && metadata.Health == IndexStorageHealth.Healthy)
        {
            ContentIndexManager.ScopeFreshnessStatus freshness = manager.GetScopeFreshnessStatus(
                indexRoot,
                ContentIndexFreshnessEvaluator.CreateReader(
                    maxCatchupRecords,
                    TimeSpan.FromSeconds(fileIoTimeoutSeconds)));
            string date = FormatAllDriveIndexDate(metadata);
            return freshness.State switch
            {
                ContentIndexManager.ScopeFreshnessState.Fresh => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.Healthy,
                    "healthy — up to date" + date),
                ContentIndexManager.ScopeFreshnessState.Dirty => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.ChangesPending,
                    "healthy — "
                        + (freshness.DirtyCount == 1
                            ? "1 recent filesystem change pending indexing"
                            : $"{freshness.DirtyCount:N0} recent filesystem changes pending indexing")
                        + "; affected files scan live until the next update"
                        + date),
                ContentIndexManager.ScopeFreshnessState.Uncertain when freshness.RequiresRebuild => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.RebuildRequired,
                    "rebuild required — " + (freshness.Problem ?? "freshness cannot be proven"),
                    indexRoot),
                _ => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.FreshnessUnavailable,
                    "freshness unavailable — live scan only — "
                        + (freshness.Problem ?? "freshness cannot be proven"),
                    IncrementalRoot: freshness.RawStatus == UsnReadStatus.Incomplete ? indexRoot : null),
            };
        }

        if (metadata.Exists)
        {
            bool canRebuild = metadata.Health != IndexStorageHealth.SourceMissing
                && System.IO.Directory.Exists(indexRoot);
            string problem = metadata.Problem ?? "The active index metadata is not usable.";
            return new IndexRootHealthEntry(
                root,
                canRebuild ? IndexRootHealthKind.RebuildRequired : IndexRootHealthKind.StorageProblem,
                ContentIndexUiStatus.StorageHealthLabel(metadata.Health) + " — " + problem,
                canRebuild ? indexRoot : null);
        }

        return new IndexRootHealthEntry(
            root,
            IndexRootHealthKind.BuildRequired,
            "registered, but the index is not built");
    }

    private static string FormatAllDriveIndexDate(IndexMetadataStatus metadata)
    {
        DateTimeOffset? timestamp = metadata.LastIncrementalUpdateUtc ?? metadata.CreatedUtc ?? metadata.BuiltUtc;
        if (timestamp is not { } value)
            return string.Empty;
        string label = metadata.LastIncrementalUpdateUtc is not null ? "last updated" : "created";
        return $" · {label} {value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}";
    }

    /// <summary>Applies global warning precedence without replacing the current-search explanation.
    /// A forced call owns the idle/startup indicator; ordinary search refreshes invoke the non-forced
    /// form, which preserves active acceleration in the label while also reporting how many other roots
    /// need attention.</summary>
    private bool ApplyAllDriveIndexHealthStatus(
        bool force = false,
        IndexSearchCoverage? activeSearchCoverage = null)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow
            || _allDriveIndexHealth.Count == 0
            || IsIndexBuildActive || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return false;

        bool needsAttention = _allDriveIndexHealth.Any(static root => root.NeedsAttention);
        if (!force && !needsAttention)
            return false;
        if (force && !needsAttention && IsSearchActive)
            return false; // healthy global state must not hide active search coverage

        // A drive-health refresh can finish after B0 has already reported acceleration. Recover the
        // current activity here as well as accepting the immediate caller's value, so that late refresh
        // never collapses "accelerating (x of y need attention)" back to only the warning count.
        if (activeSearchCoverage is null
            && IsSearchActive
            && _indexRuntimeStatusRunId == Volatile.Read(ref _searchRunId)
            && _indexRuntimeAcceleratedRootPaths.Count > 0)
        {
            int searchedRoots = _lastIndexStatusRoots.Count > 0
                ? _lastIndexStatusRoots.Count
                : _indexRuntimeAttemptedRoots.Count;
            activeSearchCoverage = _indexRuntimeAcceleratedRootPaths.Count == searchedRoots
                ? IndexSearchCoverage.Full
                : IndexSearchCoverage.Partial;
        }

        IndexStatusGlyph = ContentIndexUiStatus.AllDriveHealthGlyph(_allDriveIndexHealth);
        IndexStatusText = ContentIndexUiStatus.AllDriveHealthLabel(_allDriveIndexHealth, activeSearchCoverage);
        if (force)
        {
            IndexStatusTooltip = ContentIndexUiStatus.AllDriveHealthSummary(_allDriveIndexHealth)
                + " Hover for the status of each drive and indexed folder."
                + BuildIndexSchedulingDetails();
        }
        ShowIndexStatus = true;
        return true;
    }

    private void ResetRuntimeIndexStatus(int runId)
    {
        _indexRuntimeStatusRunId = runId;
        _indexRuntimeAttemptedRoots.Clear();
        _indexRuntimeAcceleratedRootPaths.Clear();
        _indexRuntimeBypassReasonsByRoot.Clear();
        _indexRuntimeBypassReason = null;
    }

    /// <summary>Receives the per-root gate decision at B0 (off the UI thread) and immediately replaces
    /// the availability-only indicator with the truthful state for the active search.</summary>
    private void ReportContentIndexAttempt(int runId, string root, bool accelerated, string reason)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ReportContentIndexAttempt(runId, root, accelerated, reason));
            return;
        }
        if (runId != Volatile.Read(ref _searchRunId))
            return; // stale callback from a superseded search
        if (_indexRuntimeStatusRunId != runId)
            ResetRuntimeIndexStatus(runId);
        string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
        _indexRuntimeAttemptedRoots.Add(normalizedRoot);
        bool registeredButUnbuilt = !accelerated
            && reason.Contains("no trusted index", StringComparison.OrdinalIgnoreCase)
            && IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, normalizedRoot) is not null;

        if (accelerated)
        {
            _indexRuntimeAcceleratedRootPaths.Add(normalizedRoot);
            _indexRuntimeBypassReasonsByRoot.Remove(normalizedRoot);
        }
        else
        {
            // A gate can begin accelerated and later fail safe at B1. Replace that root's optimistic B0
            // status instead of ignoring the repeated callback, so the indicator never claims that a
            // full live-scan fallback is still accelerating.
            _indexRuntimeAcceleratedRootPaths.Remove(normalizedRoot);
            _indexRuntimeBypassReasonsByRoot[normalizedRoot] = reason;
            _indexRuntimeBypassReason = reason;
        }

        if (registeredButUnbuilt)
        {
            IndexStatusFoldersWithoutIndex = IndexStatusFoldersWithoutIndex
                .Where(path => !string.Equals(IndexScopeIdentity.NormalizePath(path), normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            IndexStatusRegisteredFoldersWithoutIndex = IndexStatusRegisteredFoldersWithoutIndex
                .Append(normalizedRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            OnPropertyChanged(nameof(IndexStatusCanAddFolder));
            OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));
        }

        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow
            || IsIndexBuildActive || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return;

        int attempted = _indexRuntimeAttemptedRoots.Count;
        int acceleratedRoots = _indexRuntimeAcceleratedRootPaths.Count;
        int searchedRoots = _lastIndexStatusRoots.Count > 0 ? _lastIndexStatusRoots.Count : attempted;
        IndexSearchCoverage? activeSearchCoverage = null;
        if (acceleratedRoots > 0 && acceleratedRoots == searchedRoots)
        {
            activeSearchCoverage = IndexSearchCoverage.Full;
            IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Full);
            IndexStatusText = "Index: accelerating";
            IndexStatusTooltip = "The content index is actively pruning files for this search. Matching candidates are still verified live."
                + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                + BuildIndexDateDetails();
        }
        else if (acceleratedRoots > 0)
        {
            activeSearchCoverage = IndexSearchCoverage.Partial;
            IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Partial);
            IndexStatusText = "Index: partially accelerating";
            IndexStatusTooltip = "The content index is accelerating some searched roots; other roots are being scanned live. "
                + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                + BuildIndexDateDetails();
        }
        else
        {
            IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Bypassed);
            if (registeredButUnbuilt)
            {
                IndexStatusText = "Index: not built for this folder";
                IndexStatusTooltip = $"{normalizedRoot} is in your indexed-folders list, but it has no usable index yet. "
                    + "Click to open Settings \u25B8 Indexing and choose Build now."
                    + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                    + BuildIndexDateDetails();
            }
            else
            {
                bool catchupLimitFailure = IsIndexCatchupLimitReason(_indexRuntimeBypassReason);
                bool freshnessFailure = IsIndexFreshnessRepairReason(_indexRuntimeBypassReason);
                if (catchupLimitFailure)
                {
                    IndexStatusText = "Index: update needed";
                    IndexStatusTooltip = $"The index for {root} is beyond the configured change-journal catch-up limit. "
                        + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                        + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                        + BuildIndexDateDetails()
                        + " Open Settings \u25B8 Indexing to increase the catch-up limit, or rebuild explicitly.";
                }
                else if (freshnessFailure)
                {
                    IndexStatusText = "Index: rebuild required";
                    IndexStatusTooltip = $"The index for {root} cannot prove change-journal freshness. "
                        + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                        + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                        + BuildIndexDateDetails()
                        + " Hover to rebuild the affected index.";
                }
                else
                {
                    IndexStatusText = "Index: available \u00b7 not accelerated";
                    IndexStatusTooltip = $"An index is available for {root}, but it cannot accelerate this query. "
                        + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                        + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                        + BuildIndexDateDetails();
                }
            }
        }
        ShowIndexStatus = true;
        ApplyAllDriveIndexHealthStatus(activeSearchCoverage: activeSearchCoverage);
    }

    private static string DescribeIndexBypassReason(string? reason)
    {
        if (reason?.Contains("no required trigram", StringComparison.OrdinalIgnoreCase) == true)
            return "The query has no safe required trigram, so Yagu is scanning files live.";
        if (reason?.Contains("not selective", StringComparison.OrdinalIgnoreCase) == true)
            return "The query would leave too many candidates, so a live scan is faster.";
        if (reason?.Contains("Incomplete", StringComparison.OrdinalIgnoreCase) == true)
            return "The index checkpoint is more than the configured change-journal catch-up limit behind, so Yagu cannot prove the layer is fresh. Increase the catch-up limit and update the index, or rebuild it.";
        if (reason?.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase) == true)
            return "The saved index checkpoint is ahead of the drive's live change journal, usually because the journal was reset or recreated. Rebuild the affected index to establish a valid checkpoint.";
        if (reason?.Contains("GapDetected", StringComparison.OrdinalIgnoreCase) == true)
            return "The drive change journal no longer contains every change since this index layer was built. Rebuild the affected index to restore freshness.";
        if (reason?.Contains("JournalIdChanged", StringComparison.OrdinalIgnoreCase) == true)
            return "The drive change journal was reset after this index layer was built. Rebuild the affected index to establish a new freshness checkpoint.";
        if (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true)
            return "Yagu cannot prove that this index layer includes every recent file change. Rebuild the affected index to restore freshness.";
        return string.IsNullOrWhiteSpace(reason)
            ? "Yagu is scanning files live."
            : $"Yagu is scanning files live: {reason}.";
    }

    /// <summary>
    /// Builds a user-facing per-root breakdown for multi-root/all-drives index tooltips. Availability
    /// comes from the cheap manifest refresh; runtime callbacks add exact accelerated/bypass states. The
    /// aggregate completion summary only carries a count, so when the worker path did not callback per
    /// root, remaining accelerated slots are assigned to the built roots (the only roots that could have
    /// accelerated). A single-root search returns an empty suffix to keep its tooltip compact.
    /// </summary>
    private string BuildIndexRootStatusDetails(int acceleratedRootCount = 0, bool postSearch = false)
    {
        string[] roots = _lastIndexStatusRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length <= 1)
            return string.Empty;

        var builtRoots = _currentIndexBuiltRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registeredUnbuilt = IndexStatusRegisteredFoldersWithoutIndex
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceleratedRoots = new HashSet<string>(_indexRuntimeAcceleratedRootPaths, StringComparer.OrdinalIgnoreCase);

        // Worker pruning reports aggregate coverage even when it does not invoke the in-process per-root
        // attempt callback. Infer those roots only from manifest-backed roots, never from an unindexed root.
        int remainingAccelerated = Math.Max(0, acceleratedRootCount - acceleratedRoots.Count);
        if (remainingAccelerated > 0)
        {
            foreach (string root in roots)
            {
                if (remainingAccelerated == 0) break;
                if (builtRoots.Contains(root) && acceleratedRoots.Add(root))
                    remainingAccelerated--;
            }
        }

        var lines = new List<string>(roots.Length + 1) { "Drive/folder index status:" };
        foreach (string root in roots)
        {
            string state;
            if (acceleratedRoots.Contains(root))
                state = postSearch ? "accelerated this search" : "accelerating this search";
            else if (_indexRuntimeBypassReasonsByRoot.TryGetValue(root, out string? reason))
                state = "scanned live — " + FormatIndexRootBypassReason(reason);
            else if (TryGetCurrentIndexFreshnessForSearchRoot(root, out var freshness) && freshness.RequiresRebuild)
                state = "rebuild required — " + (freshness.Problem ?? "freshness cannot be proven");
            else if (TryGetCurrentIndexFreshnessForSearchRoot(root, out freshness) && freshness.NeedsAttention)
                state = "freshness unavailable — scanning live — " + (freshness.Problem ?? "freshness cannot be proven");
            else if (registeredUnbuilt.Contains(root))
                state = "registered, but the index is not built";
            else if (!builtRoots.Contains(root))
                state = "not indexed";
            else
                state = postSearch ? "index available, but scanned live" : "index available";
            lines.Add($"  {root} — {state}");
        }
        return Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string FormatIndexRootBypassReason(string? reason)
    {
        if (reason?.Contains("no required trigram", StringComparison.OrdinalIgnoreCase) == true)
            return "query has no safe required trigram";
        if (reason?.Contains("not selective", StringComparison.OrdinalIgnoreCase) == true)
            return "a live scan is faster for this query";
        if (reason?.Contains("no trusted index", StringComparison.OrdinalIgnoreCase) == true)
            return "no trusted index";
        if (reason?.Contains("Incomplete", StringComparison.OrdinalIgnoreCase) == true)
            return "change-journal catch-up limit reached";
        if (reason?.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase) == true)
            return "saved checkpoint is ahead of the live change journal";
        if (reason?.Contains("GapDetected", StringComparison.OrdinalIgnoreCase) == true)
            return "change journal no longer covers the index checkpoint";
        if (reason?.Contains("JournalIdChanged", StringComparison.OrdinalIgnoreCase) == true)
            return "change journal was reset after the index was built";
        if (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true)
            return "index freshness cannot be proven";
        return string.IsNullOrWhiteSpace(reason) ? "index was not used" : reason.Trim().TrimEnd('.');
    }

    private bool TryGetCurrentIndexFreshnessForSearchRoot(
        string searchRoot,
        out ContentIndexManager.ScopeFreshnessStatus freshness)
    {
        string normalized = IndexScopeIdentity.NormalizePath(searchRoot);
        if (_currentIndexFreshnessByRoot.TryGetValue(normalized, out freshness))
            return true;
        string? covering = IndexedRootsPolicy.FindBestCoveringRoot(
            _currentIndexFreshnessByRoot.Keys.ToArray(), normalized);
        return covering is not null && _currentIndexFreshnessByRoot.TryGetValue(covering, out freshness);
    }

    /// <summary>Builds the timestamp section shared by every index-status hover state. A single index
    /// gets compact Created/Active generation/Last updated lines; multi-root searches identify each indexed root.</summary>
    private string BuildIndexDateDetails()
    {
        if (_currentIndexDatesByRoot.Count == 0)
            return string.Empty;

        static string Format(DateTimeOffset value)
            => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

        if (_currentIndexDatesByRoot.Count == 1)
        {
            var dates = _currentIndexDatesByRoot.Values.First();
            var lines = new List<string>(2);
            if (dates.CreatedUtc is { } created)
                lines.Add($"Created: {Format(created)}");
            if (dates.BuiltUtc is { } built && built != dates.CreatedUtc)
                lines.Add($"Active generation built: {Format(built)}");
            if (dates.LastIncrementalUpdateUtc is { } updated)
                lines.Add($"Last incremental update: {Format(updated)}");
            return lines.Count == 0 ? string.Empty : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }

        var rootLines = new List<string>(_currentIndexDatesByRoot.Count + 1) { "Index dates:" };
        foreach (var pair in _currentIndexDatesByRoot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var parts = new List<string>(2);
            if (pair.Value.CreatedUtc is { } created)
                parts.Add($"created {Format(created)}");
            if (pair.Value.BuiltUtc is { } built && built != pair.Value.CreatedUtc)
                parts.Add($"active generation built {Format(built)}");
            if (pair.Value.LastIncrementalUpdateUtc is { } updated)
                parts.Add($"updated incrementally {Format(updated)}");
            if (parts.Count > 0)
                rootLines.Add($"  {pair.Key} — {string.Join(" · ", parts)}");
        }
        return rootLines.Count == 1 ? string.Empty : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, rootLines);
    }

    /// <summary>Places the automatic-indexing schedule in its own paragraph below the date section so
    /// it cannot run into the Created/Last updated line in the status hover surface.</summary>
    private string BuildIndexSchedulingDetails()
        => Environment.NewLine + Environment.NewLine
            + ContentIndexUiStatus.SchedulingHint(_settings.IndexBuildTrigger);

    /// <summary>
    /// Upgrades the main-window index indicator from pre-search <em>availability</em> to real post-search
    /// <em>coverage</em> (plan §6.2): once a search finishes, its <see cref="IndexAccelerationInfo"/> says
    /// how many searched roots the index actually accelerated, so the glyph honestly reflects Full/Partial/
    /// Bypassed. Leaves the availability indicator untouched when the feature/setting is off or the index
    /// did not participate (a null summary or no opted-in root).
    /// </summary>
    private void UpdateIndexCoverageStatus(IndexAccelerationInfo? acceleration)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
            return;
        if (acceleration is null || acceleration.RequestedRoots <= 0)
            return;
        // Background build/warm activity owns the indicator instead of coverage.
        if (_activeIndexBuilds > 0 || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return;

        int accelerated = acceleration.AcceleratedRoots;
        int liveScanned = Math.Max(0, acceleration.RequestedRoots - accelerated);
        IndexSearchCoverage coverage = ContentIndexUiStatus.Coverage(
            enabled: true, usedThisSearch: true, accelerated, liveScanned);

        IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(coverage);
        IndexStatusText = ContentIndexUiStatus.CoverageLabel(coverage);
        IndexStatusTooltip = ContentIndexUiStatus.CoverageTooltip(coverage, acceleration.FilesPruned)
            + BuildIndexRootStatusDetails(accelerated, postSearch: true)
            + BuildIndexDateDetails()
            + BuildIndexSchedulingDetails();
        ShowIndexStatus = ContentIndexUiStatus.ShouldShowStatus(true, _settings.ShowIndexStatusInMainWindow);
        ApplyAllDriveIndexHealthStatus();
    }

    /// <summary>
    /// Enables the content-index feature (if it is off), registers <paramref name="folder"/> as an indexed
    /// root, persists settings, and starts a background build of that folder. Backs the main-window
    /// "add this folder to the index" affordances (the clickable status indicator and the first-run
    /// onboarding prompt). Never throws — the build runs off the UI thread and a failure only logs; the
    /// caller is responsible for any large-folder confirmation before calling this.
    /// </summary>
    public async Task AddFolderToIndexAndBuildAsync(string folder)
    {
        string? effectiveRoot = await RegisterFolderForIndexAsync(folder).ConfigureAwait(true);
        if (effectiveRoot is null)
            return;

        YaguLog.For("ContentIndex").LogInformation(
            "Onboarding: registered effective root '{EffectiveRoot}' for requested folder '{RequestedRoot}' and starting a background index build.",
            effectiveRoot, folder.Trim());
        StartBackgroundIndexBuild(effectiveRoot);
    }

    /// <summary>
    /// Registers several folders as indexed roots at once (first-run onboarding lets the user pick more
    /// than one), optionally sets which automatic build trigger(s) maintain them and the update mode those
    /// passes use, persists settings a single time, then starts a background build for each distinct
    /// effective root. Folders already covered by a broader registered root are skipped. Never throws.
    /// </summary>
    public async Task AddFoldersToIndexAndBuildAsync(IReadOnlyList<string> folders, string? buildTrigger, string? updateMode = null)
    {
        if (folders is null || folders.Count == 0)
            return;

        _settings.EnableContentIndex = true;
        UseContentIndex = true;
        if (!string.IsNullOrWhiteSpace(buildTrigger))
            _settings.IndexBuildTrigger = AppSettings.NormalizeIndexBuildTrigger(buildTrigger);
        // Onboarding decides the update mode alongside the trigger, so an automatic trigger cannot be left
        // paired with ManualFullRebuild (which would only ever create missing indexes).
        if (!string.IsNullOrWhiteSpace(updateMode))
            _settings.IndexUpdateMode = AppSettings.NormalizeIndexUpdateMode(updateMode);

        var effectiveRoots = new List<string>(folders.Count);
        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            string root = folder.Trim();
            // Skip a folder already covered by an equal/broader root registered so far (including ones
            // added earlier in this same loop), so we never register or build a redundant child.
            if (IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is not null)
                continue;
            _settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, root);
            string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) ?? root;
            if (!effectiveRoots.Contains(effectiveRoot, StringComparer.OrdinalIgnoreCase))
                effectiveRoots.Add(effectiveRoot);
        }

        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));

        foreach (string effectiveRoot in effectiveRoots)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Onboarding: registered effective root '{EffectiveRoot}' and starting a background index build.",
                effectiveRoot);
            StartBackgroundIndexBuild(effectiveRoot);
        }
    }

    /// <summary>Registers <paramref name="folder"/> and awaits its initial build behind the same
    /// full-window blocking overlay used by an explicit rebuild. This is the pre-search readiness
    /// dialog path: the user chose "Add to index", so Yagu must stay blocked until that requested
    /// operation completes rather than silently starting the ordinary onboarding background build.</summary>
    public async Task AddFolderToIndexAndBuildBlockingAsync(string folder)
    {
        string? effectiveRoot = await RegisterFolderForIndexAsync(folder).ConfigureAwait(true);
        if (effectiveRoot is null)
            return;

        YaguLog.For("ContentIndex").LogInformation(
            "Pre-search readiness: registered effective root '{EffectiveRoot}' for requested folder '{RequestedRoot}' and starting a blocking index build.",
            effectiveRoot, folder.Trim());
        await RunCurrentIndexBlockingAsync(new[] { effectiveRoot }, rebuild: false).ConfigureAwait(true);
    }

    private async Task<string?> RegisterFolderForIndexAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        string root = folder.Trim();
        string? existingCover = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root);

        // Opt in: turn the master feature on, default the per-search toggle on, and register the root.
        _settings.EnableContentIndex = true;
        _settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, root);
        string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) ?? root;
        UseContentIndex = true;
        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));

        if (existingCover is not null)
        {
            StatusText = $"{root} is already covered by the content index root {existingCover}.";
            return null;
        }

        return effectiveRoot;
    }

    /// <summary>Enrolls an existing leftover index in the maintained-root list without rebuilding it.
    /// The next automatic maintenance pass evaluates its freshness and applies a safe incremental update
    /// when possible. This settings-only action is safe while another root is being indexed.</summary>
    public async Task MaintainExistingIndexAsync(string folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = MaintainExistingIndexAsync(folder));
            return;
        }
        if (_disposed || string.IsNullOrWhiteSpace(folder))
            return;

        string requestedRoot = IndexScopeIdentity.NormalizePath(folder);
        string? existingCover = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, requestedRoot);
        _settings.EnableContentIndex = true;
        _settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, requestedRoot);
        string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, requestedRoot)
            ?? requestedRoot;
        UseContentIndex = true;
        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));
        StatusText = existingCover is null
            ? $"Added {effectiveRoot} to maintained index folders. Its existing index will be checked by the next maintenance pass."
            : $"{requestedRoot} is already maintained by the covering index root {existingCover}.";
        RefreshCurrentIndexStatus();
        RefreshAllDriveIndexStatus();
    }

    /// <summary>Deletes the exact stored index for <paramref name="folder"/> without changing maintained
    /// roots. The caller supplies confirmation. A concurrent writer is rejected by the index lease.</summary>
    public async Task DeleteStoredIndexAsync(string folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = DeleteStoredIndexAsync(folder));
            return;
        }
        if (_disposed || string.IsNullOrWhiteSpace(folder))
            return;
        if (IsIndexBuildActive || IsIndexRebuildBlocking)
        {
            StatusText = "Wait for the current index operation to finish before deleting stored index data.";
            return;
        }

        string root = IndexScopeIdentity.NormalizePath(folder);
        var provider = DefaultContentIndexPathProvider.Create(_settings.IndexStorageDirectory);
        var manager = new ContentIndexManager(
            provider,
            AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount));
        try
        {
            bool existed = await Task.Run(() => manager.DeleteScope(ContentIndexManager.ScopeIdForRoot(root)))
                .ConfigureAwait(true);
            StatusText = existed
                ? $"Deleted the stored content index for {root}."
                : $"No stored content index existed for {root}.";
        }
        catch (IndexWriteBusyException)
        {
            StatusText = "Another index operation is running; delete the stored index after it finishes.";
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Deleting the stored index for '{Root}' failed.", root);
            StatusText = $"Deleting the stored index for {root} failed: {ex.Message}";
        }
        finally
        {
            RefreshCurrentIndexStatus();
            RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>
    /// Starts an immediate rebuild for an already-registered root from the status indicator's context
    /// menu. Does not modify registration or settings. The operation uses the same worker-backed,
    /// cancellable background path as onboarding and exposes normal progress/pause behavior.
    /// </summary>
    public void RebuildRegisteredIndexNow(string folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => RebuildRegisteredIndexNow(folder));
            return;
        }
        if (_disposed || IsIndexBuildActive || IsIndexingPaused || string.IsNullOrWhiteSpace(folder))
            return;

        string root = IndexScopeIdentity.NormalizePath(folder);
        if (!IndexedRootsPolicy.Contains(_settings.IndexedRoots, root))
            return; // context action is only valid for a registered root

        YaguLog.For("ContentIndex").LogInformation(
            "Status menu: rebuilding registered index root '{Root}'.", root);
        StartBackgroundIndexBuild(root, rebuild: true);
    }

    /// <summary>
    /// Describes the on-disk index for the currently searched roots for the status-bar indicator's
    /// "Index date … (click to rebuild)" menu item. Returns <c>true</c> (with a formatted
    /// <paramref name="dateLabel"/> and the <paramref name="roots"/> to rebuild) only when at least one
    /// searched root currently has a readable index; the date is the oldest of those roots' build times,
    /// rendered in local time (or "unknown" when a manifest carries no timestamp).
    /// </summary>
    public bool TryGetCurrentIndexRebuildTarget(out string dateLabel, out IReadOnlyList<string> roots)
    {
        roots = _currentIndexBuiltRoots;
        if (_currentIndexBuiltRoots.Count == 0)
        {
            dateLabel = string.Empty;
            return false;
        }

        string date = _currentIndexBuiltUtc is { } built
            ? built.ToLocalTime().ToString("MM/ddd/yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : "unknown";
        dateLabel = $"Index date: {date} (click to rebuild)";
        return true;
    }

    /// <summary>
    /// Returns indexed roots whose active-search bypass or all-drive health snapshot identifies as a
    /// repairable freshness/storage failure. Query-shape/selectivity bypasses and unsupported journals
    /// are intentionally excluded because rebuilding cannot help them.
    /// </summary>
    public bool TryGetCurrentIndexFreshnessRepairTarget(
        out string actionLabel,
        out IReadOnlyList<string> roots)
    {
        var builtRoots = _currentIndexBuiltRoots
            .Select(IndexScopeIdentity.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] repairRoots = _indexRuntimeBypassReasonsByRoot
            .Where(pair => IsIndexFreshnessRepairReason(pair.Value)
                && (!TryGetCurrentIndexFreshnessForSearchRoot(pair.Key, out var freshness)
                    || !freshness.NeedsAttention
                    || freshness.RequiresRebuild))
            .Select(pair =>
            {
                string searchedRoot = IndexScopeIdentity.NormalizePath(pair.Key);
                return builtRoots.Contains(searchedRoot)
                    ? searchedRoot
                    : IndexedRootsPolicy.FindBestCoveringRoot(builtRoots, searchedRoot);
            })
            .Where(root => root is not null)
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        repairRoots = repairRoots
            .Concat(_currentIndexFreshnessByRoot
                .Where(static pair => pair.Value.RequiresRebuild)
                .Select(static pair => pair.Key))
            .Concat(_allDriveIndexHealth
                .Where(static root => root.CanRepair)
                .Select(static root => root.RepairRoot!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        roots = repairRoots;
        actionLabel = repairRoots.Length switch
        {
            1 => $"Rebuild {repairRoots[0]} index",
            > 1 => $"Rebuild {repairRoots.Length} indexes",
            _ => string.Empty,
        };
        return repairRoots.Length > 0;
    }

    private static bool IsIndexFreshnessRepairReason(string? reason)
        => !IsIndexCatchupLimitReason(reason)
            && (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsIndexCatchupLimitReason(string? reason)
        => reason?.Contains("Incomplete", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Rebuilds the content index for <paramref name="roots"/> while a full-window blocking overlay
    /// prevents any other interaction, updating that overlay with live progress. Invoked from the
    /// status-bar indicator's "Index date … (click to rebuild)" menu item. The build uses the same
    /// worker-backed path as a background build, but here it is awaited and the rest of the UI is
    /// intentionally blocked until it finishes. Never throws.
    /// </summary>
    public async Task RebuildCurrentIndexBlockingAsync(IReadOnlyList<string> roots)
        => await RunCurrentIndexBlockingAsync(roots, rebuild: true).ConfigureAwait(true);

    private async Task RunCurrentIndexBlockingAsync(IReadOnlyList<string> roots, bool rebuild)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = RunCurrentIndexBlockingAsync(roots, rebuild));
            return;
        }
        if (_disposed || IsIndexRebuildBlocking || IsIndexBuildActive || IsIndexingPaused
            || roots is null || roots.Count == 0)
            return;

        var targets = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
        if (targets.Length == 0)
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(IndexBuildCancellationToken);
        _indexRebuildCancellation = cancellation;
        _indexBlockingOperationIsRebuild = rebuild;
        OnPropertyChanged(nameof(IndexRebuildOverlayTitle));
        OnPropertyChanged(nameof(IndexRebuildCancelButtonText));
        IsIndexRebuildCancelling = false;
        IsIndexRebuildBlocking = true;
        IndexRebuildProgressPercent = 0;
        string operation = rebuild ? "rebuild" : "build";
        IndexRebuildProgressText = targets.Length == 1
            ? $"Preparing to {operation} the index for {targets[0]}…"
            : $"Preparing to {operation} {targets.Length} indexes…";
        await Task.Yield(); // allow the full-window overlay to paint before worker startup

        try
        {
            for (int i = 0; i < targets.Length && !cancellation.IsCancellationRequested; i++)
                await BuildOneBlockingAsync(targets[i], i, targets.Length, rebuild, cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_indexRebuildCancellation, cancellation))
                _indexRebuildCancellation = null;
            IsIndexRebuildBlocking = false;
            IsIndexRebuildCancelling = false;
            IndexRebuildProgressPercent = 0;
            IndexRebuildProgressText = string.Empty;
            if (_lastIndexStatusRoots.Count > 0)
                await RefreshIndexStatusAsync(_lastIndexStatusRoots, _lastIndexStatusUseThisSearch).ConfigureAwait(true);
            RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>Runs an explicit incremental maintenance pass for one physical index root. This never
    /// falls back to a full rebuild: if journal continuity still cannot be proven, the existing index is
    /// retained unchanged and the user can search live or explicitly choose Rebuild. When
    /// <paramref name="increasedCatchupLimit"/> is supplied, that user-approved larger bounded journal
    /// replay limit is persisted before the pass.</summary>
    public async Task RefreshCurrentIndexIncrementallyAsync(string root, int? increasedCatchupLimit = null)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = RefreshCurrentIndexIncrementallyAsync(root, increasedCatchupLimit));
            return;
        }
        if (_disposed || IsIndexBuildActive || IsIndexRebuildBlocking || IsIndexingPaused
            || string.IsNullOrWhiteSpace(root))
            return;

        string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
        if (increasedCatchupLimit is { } requested)
        {
            int normalized = AppSettings.NormalizeIndexMaxJournalCatchupRecords(requested);
            if (normalized > _settings.IndexMaxJournalCatchupRecords)
            {
                _settings.IndexMaxJournalCatchupRecords = normalized;
                await PersistSettingsAsync().ConfigureAwait(true);
            }
        }

        BeginIndexBuildActivity(normalizedRoot, isIncremental: true);
        StatusText = $"Updating the {normalizedRoot} content index incrementally…";
        try
        {
            IndexMaintenanceOperation operation = IndexBuildOperationFactory.CreateMaintenance(
                _settings,
                new[] { normalizedRoot },
                IndexMaintenanceOperation.ModeIncremental,
                rebuildWhenDirty: false);
            operation.AllowFullRebuildFallback = false;
            operation.AllowCompatibilityRebuild = false;
            operation.ForceRefresh = true;
            var coordinator = new IndexBuildCoordinator();
            IndexMaintenanceSuccess result = await coordinator.RunMaintenancePreferWorkerAsync(
                operation,
                _settings.IndexUseNativeWorker,
                IndexBuildCancellationToken,
                (progressRoot, percent, stage) => ReportIndexBuildProgress(progressRoot, percent, stage)).ConfigureAwait(true);

            IndexMaintenanceRootResult? rootResult = result.Roots.FirstOrDefault();
            StatusText = rootResult?.Action switch
            {
                IndexMaintenanceActions.DeltaAppended => $"Updated the {normalizedRoot} index incrementally.",
                IndexMaintenanceActions.Compacted => $"Updated and compacted the {normalizedRoot} index.",
                IndexMaintenanceActions.Reanchored => $"The {normalizedRoot} index was already current; its checkpoint was refreshed.",
                IndexMaintenanceActions.Skipped => $"The {normalizedRoot} index is already up to date.",
                _ when rootResult?.Outcome == "needsFullRebuild" =>
                    $"The incremental update could not establish journal continuity for {normalizedRoot}; the existing index was kept unchanged. Search live or explicitly rebuild it.",
                _ => $"The incremental update for {normalizedRoot} did not complete; the existing index was kept unchanged.",
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Incremental update for {normalizedRoot} was cancelled; the existing index was kept unchanged.";
        }
        catch (IndexWriteBusyException)
        {
            StatusText = "Another index operation is already running.";
        }
        catch (IndexDiskFullException ex)
        {
            OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "On-demand incremental refresh failed for '{Root}'.", normalizedRoot);
            StatusText = $"Incremental update for {normalizedRoot} failed; the existing index was kept unchanged.";
        }
        finally
        {
            EndIndexBuildActivity();
            if (_lastIndexStatusRoots.Count > 0)
                await RefreshIndexStatusAsync(_lastIndexStatusRoots, _lastIndexStatusUseThisSearch).ConfigureAwait(true);
            RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>Requests cooperative cancellation of only the on-demand blocking rebuild. The previously
    /// published index remains available because the builder publishes staged generations atomically.</summary>
    public void CancelCurrentIndexRebuild()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(CancelCurrentIndexRebuild);
            return;
        }
        if (!IsIndexRebuildBlocking || IsIndexRebuildCancelling)
            return;

        IsIndexRebuildCancelling = true;
        IndexRebuildProgressText = _indexBlockingOperationIsRebuild
            ? "Canceling the rebuild… The existing index remains available."
            : "Canceling the build… No incomplete index will be published.";
        _indexRebuildCancellation?.Cancel();
        YaguLog.For("ContentIndex").LogInformation(
            "User cancelled the blocking index {Action}.", _indexBlockingOperationIsRebuild ? "rebuild" : "build");
    }

    private async Task BuildOneBlockingAsync(string root, int index, int total, bool rebuild, CancellationToken token)
    {
        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(_settings, root, rebuild);
        bool useWorker = _settings.IndexUseNativeWorker;
        long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);

        BeginIndexBuildActivity(root);
        try
        {
            var coordinator = new IndexBuildCoordinator();
            await coordinator.BuildFullScopePreferWorkerAsync(
                operation,
                useWorker,
                token,
                progress: p => ReportRebuildBlockingProgress(root, index, total,
                    IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes)),
                pdfProgress: p => ReportRebuildBlockingProgress(root, index, total,
                    p.Total <= 0 ? -1 : 90 + Math.Clamp(p.Processed * 5 / p.Total, 0, 5)),
                imageOcrProgress: p => ReportRebuildBlockingProgress(root, index, total,
                    p.Total <= 0 ? -1 : 95 + Math.Clamp(p.Processed * 4 / p.Total, 0, 4))).ConfigureAwait(true);
            YaguLog.For("ContentIndex").LogInformation("Blocking index {Action} complete for '{Root}'.", rebuild ? "rebuild" : "build", root);
        }
        catch (OperationCanceledException)
        {
            YaguLog.For("ContentIndex").LogInformation("Blocking index {Action} for '{Root}' was paused/cancelled.", rebuild ? "rebuild" : "build", root);
        }
        catch (IndexDiskFullException ex)
        {
            YaguLog.For("ContentIndex").LogWarning("Blocking index {Action} for '{Root}' stopped: {Error}", rebuild ? "rebuild" : "build", root, ex.Message);
            OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
        }
        catch (IndexWriteBusyException)
        {
            YaguLog.For("ContentIndex").LogInformation("Blocking index {Action} for '{Root}' skipped because another index operation is running.", rebuild ? "rebuild" : "build", root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Blocking index {Action} failed for '{Root}'.", rebuild ? "rebuild" : "build", root);
        }
        finally
        {
            EndIndexBuildActivity();
        }
    }

    /// <summary>Self-marshalling progress sink for <see cref="RebuildCurrentIndexBlockingAsync"/>: folds a
    /// per-root 0–99 estimate (or -1 unknown) into the overall 0–100 overlay progress across all roots and
    /// refreshes the overlay's status line.</summary>
    private void ReportRebuildBlockingProgress(string root, int index, int total, int percent)
    {
        void apply()
        {
            if (!IsIndexRebuildBlocking)
                return;
            if (percent >= 0)
            {
                double overall = (index * 100.0 + Math.Clamp(percent, 0, 100)) / Math.Max(1, total);
                IndexRebuildProgressPercent = Math.Clamp(overall, 0, 100);
            }
            string suffix = percent >= 0 ? $" {percent}%" : string.Empty;
            string verb = _indexBlockingOperationIsRebuild ? "Rebuilding" : "Building";
            IndexRebuildProgressText = total > 1
                ? $"{verb} {root} ({index + 1} of {total})…{suffix}"
                : $"{verb} {root}…{suffix}";
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }

    /// <summary>
    /// Starts loading the current root's immutable query index immediately. A cold open runs off the UI
    /// thread and is cooperatively cancellable; there is deliberately no fixed wait before it starts.
    /// If a search is already running, the root is queued and warming begins when that search finishes.
    /// </summary>
    public void StartContentIndexWarmup(string? folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => StartContentIndexWarmup(folder));
            return;
        }
        if (_disposed || !_settings.EnableContentIndex || !UseContentIndex || string.IsNullOrWhiteSpace(folder))
            return;

        // Stage-6 (plan §5.8): the worker PRUNING path needs no in-process warm. The worker memory-maps the
        // scope's format-v3 lazily and its open at barrier B0 is cheap (no ~8× in-process deserialize, no GC
        // storm), so a worker-served scope accelerates directly on the FIRST search. Warming here would
        // deserialize the whole index into the host — the exact footprint the worker path removes — so skip it
        // entirely when the flag is on.
        if (_settings.IndexUseWorkerQuerySessions)
            return;

        string requestedRoot = folder.Trim();
        string root = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, requestedRoot)
            ?? requestedRoot;
        if (IsSearching)
        {
            _resumeIndexWarmFolder = root;
            OnPropertyChanged(nameof(ActiveIndexWarmFolder));
            return;
        }
        if (IsIndexWarmActive
            && string.Equals(_activeIndexWarmFolder, root, StringComparison.OrdinalIgnoreCase))
            return;

        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount);
        string storageDir = _settings.IndexStorageDirectory;
        int maxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(_settings.IndexMaxInProcessSizeMB);
        var pathProvider = DefaultContentIndexPathProvider.Create(storageDir);
        try
        {
            var manager = new ContentIndexManager(pathProvider, retained);
            if (!manager.HasCurrentIndex(root))
                return;
            if (ContentIndexSearchGate.IsScopeWarm(pathProvider, root, retained))
            {
                ShowIndexWarmReadyStatus(root);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Could not prepare startup index warm-up for {Root}; searches will live-scan.", root);
            return;
        }

        CancellationTokenSource? previous = _indexWarmCancellation;
        _indexWarmCancellation = null;
        try { previous?.Cancel(); } catch { }

        int generation = ++_indexWarmGeneration;
        var cancellation = new CancellationTokenSource();
        _indexWarmCancellation = cancellation;
        _activeIndexWarmFolder = root;
        _resumeIndexWarmFolder = null;
        IsIndexWarmPausedForSearch = false;
        IsIndexWarmActive = true;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));
        ShowIndexWarmPreparingStatus(root);
        YaguLog.For("ContentIndex").LogInformation(
            "Index warm-up starting immediately for {Root} (no startup delay).", root);

        _ = RunContentIndexWarmupAsync(
            generation,
            root,
            pathProvider,
            retained,
            maxInProcessSizeMB,
            cancellation);
    }

    private async Task RunContentIndexWarmupAsync(
        int generation,
        string root,
        IContentIndexPathProvider pathProvider,
        int retained,
        int maxInProcessSizeMB,
        CancellationTokenSource cancellation)
    {
        bool ready = false;
        bool loadable = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            (ready, loadable) = await Task.Run(() =>
            {
                CancellationToken token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                if (!ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(
                        pathProvider,
                        root,
                        retained,
                        maxInProcessSizeMB))
                    return (false, false);

                string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                var store = new ContentIndexStore(pathProvider, scopeId, Math.Max(1, retained));
                if (store.IsCurrentLayeredIndexCached())
                    return (true, true);
                return (store.TryOpenLayered(retainDocuments: false, cancellationToken: token) is not null, true);
            }, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Index warm-up paused/cancelled for {Root} after {ElapsedSeconds:0.0}s.",
                root,
                stopwatch.Elapsed.TotalSeconds);
            return;
        }
        catch (OutOfMemoryException ex)
        {
            loadable = false;
            YaguLog.For("ContentIndex").LogCritical(ex,
                "Index warm-up ran out of memory for {Root}; searches will live-scan.", root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index warm-up failed for {Root}; searches will continue with live scanning.", root);
        }
        finally
        {
            cancellation.Dispose();
        }

        if (generation != _indexWarmGeneration || _disposed)
            return;

        _indexWarmCancellation = null;
        _activeIndexWarmFolder = null;
        IsIndexWarmActive = false;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));

        if (ready)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Index warm-up completed for {Root} in {ElapsedSeconds:0.0}s.",
                root,
                stopwatch.Elapsed.TotalSeconds);
            ShowIndexWarmReadyStatus(root);
        }
        else
        {
            if (!loadable)
                YaguLog.For("ContentIndex").LogInformation(
                    "Index warm-up skipped for {Root}: the index is outside the configured in-process size policy.",
                    root);
            _ = RefreshIndexStatusAsync([root], UseContentIndex && _settings.EnableContentIndex);
        }
    }

    /// <summary>Cancels an active warm before a search and remembers its root for automatic restart when
    /// the search ends. Returns false when no warm was active.</summary>
    public bool PauseContentIndexWarmupForSearch()
    {
        if (!_dispatcher.HasThreadAccess)
            return false;
        if (!IsIndexWarmActive || string.IsNullOrWhiteSpace(_activeIndexWarmFolder))
            return false;

        _resumeIndexWarmFolder = _activeIndexWarmFolder;
        _activeIndexWarmFolder = null;
        ++_indexWarmGeneration; // makes the cancelled run's completion stale
        CancellationTokenSource? cancellation = _indexWarmCancellation;
        _indexWarmCancellation = null;
        try { cancellation?.Cancel(); } catch { }
        IsIndexWarmActive = false;
        IsIndexWarmPausedForSearch = true;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));
        ShowIndexWarmPausedStatus();
        return true;
    }

    /// <summary>Restarts a warm that was paused (or queued) for a search. Safe to call after every search;
    /// it is a no-op when no root is waiting.</summary>
    public void ResumeContentIndexWarmupAfterSearch()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(ResumeContentIndexWarmupAfterSearch);
            return;
        }
        if (IsSearching || string.IsNullOrWhiteSpace(_resumeIndexWarmFolder))
            return;

        string root = _resumeIndexWarmFolder;
        _resumeIndexWarmFolder = null;
        IsIndexWarmPausedForSearch = false;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));
        StartContentIndexWarmup(root);
    }

    private void ShowIndexWarmPreparingStatus(string root)
    {
        if (!_settings.ShowIndexStatusInMainWindow || IsIndexBuildActive)
            return;
        IndexStatusGlyph = "\uE895";
        IndexStatusText = "Indexing: preparing...";
        IndexStatusTooltip = $"Loading the content index for {root} into the query cache. "
            + "A search can start now, but it will pause this warm-up and run without index acceleration."
            + BuildIndexDateDetails();
        ShowIndexBuildPercent = false;
        ShowIndexStatus = true;
    }

    private void ShowIndexWarmPausedStatus()
    {
        if (!_settings.ShowIndexStatusInMainWindow || IsIndexBuildActive)
            return;
        IndexStatusGlyph = "\uE769";
        IndexStatusText = "Indexing: warm-up paused";
        IndexStatusTooltip = "Index warm-up is paused while the search runs. It resumes automatically when the search finishes."
            + BuildIndexDateDetails();
        ShowIndexBuildPercent = false;
        ShowIndexStatus = true;
    }

    private void ShowIndexWarmReadyStatus(string root)
    {
        if (!_settings.ShowIndexStatusInMainWindow || IsIndexBuildActive)
            return;
        IndexStatusGlyph = ContentIndexUiStatus.AvailabilityGlyph(IndexAvailability.Available);
        IndexStatusText = "Index: ready";
        IndexStatusTooltip = $"The content index for {root} is warmed and ready for accelerated searches."
            + BuildIndexDateDetails();
        ShowIndexBuildPercent = false;
        ShowIndexStatus = true;
        ApplyAllDriveIndexHealthStatus(force: true);
    }

    /// <summary>
    /// Unregisters <paramref name="folder"/> from the content-index roots (the inverse of
    /// <see cref="AddFolderToIndexAndBuildAsync"/>) and persists settings. This only removes it from the
    /// auto-index list — it does NOT delete any already-built on-disk index data (that is managed from
    /// Settings ▸ Indexing), matching the "Remove selected folder" behavior there. Never throws.
    /// </summary>
    public async Task RemoveFolderFromIndexAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        string root = folder.Trim();

        if (!IndexedRootsPolicy.Contains(_settings.IndexedRoots, root))
            return;

        _settings.IndexedRoots = IndexedRootsPolicy.Remove(_settings.IndexedRoots, root);
        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));
        RefreshAllDriveIndexStatus();

        YaguLog.For("ContentIndex").LogInformation("Unregistered '{Root}' from the content-index roots.", root);
    }

    /// <summary>
    /// Starts a cancellable background build of <paramref name="folder"/> (using the shared
    /// <see cref="IndexBuildCancellationToken"/> so a right-click pause stops it), and brackets it with the
    /// "Indexing…" indicator activity. Never throws; a failure or pause only logs.
    /// </summary>
    private void StartBackgroundIndexBuild(string folder, bool rebuild = false)
    {
        string root = folder.Trim();
        if (root.Length == 0)
            return;

        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(_settings, root, rebuild);
        bool useWorker = _settings.IndexUseNativeWorker;
        CancellationToken token = IndexBuildCancellationToken;
        // Denominator for the "% complete" estimate: the used space of the drive this root lives on
        // (cheap, no pre-count). Captured once here on the UI thread; the build reports crawled bytes.
        long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);

        BeginIndexBuildActivity(root);
        _ = Task.Run(async () =>
        {
            try
            {
                var coordinator = new IndexBuildCoordinator();
                await coordinator.BuildFullScopePreferWorkerAsync(
                    operation,
                    useWorker,
                    token,
                    progress: p => ReportIndexBuildProgress(IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes)),
                    pdfProgress: p => ReportIndexBuildProgress(p.Total <= 0 ? -1 : 90 + Math.Clamp(p.Processed * 5 / p.Total, 0, 5)),
                    imageOcrProgress: p => ReportIndexBuildProgress(p.Total <= 0 ? -1 : 95 + Math.Clamp(p.Processed * 4 / p.Total, 0, 4)));
                YaguLog.For("ContentIndex").LogInformation(
                    "Background index {Action} complete for '{Root}'.", rebuild ? "rebuild" : "build", root);
            }
            catch (OperationCanceledException)
            {
                YaguLog.For("ContentIndex").LogInformation("Background index build for '{Root}' was paused/cancelled.", root);
            }
            catch (IndexDiskFullException ex)
            {
                YaguLog.For("ContentIndex").LogWarning("Background index build for '{Root}' stopped: {Error}", root, ex.Message);
                OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
            }
            catch (IndexWriteBusyException)
            {
                YaguLog.For("ContentIndex").LogInformation("Background index build for '{Root}' skipped because another index operation is running.", root);
            }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "Background index build failed for '{Root}'.", root);
            }
            finally
            {
                EndIndexBuildActivity();
            }
        });
    }

    /// <summary>
    /// Pauses indexing (from the status-bar indicator's right-click menu): cancels the running tracked
    /// build(s) and holds off auto/startup/watcher builds until <see cref="ResumeIndexing"/>. Safe from any
    /// thread. Session-only — a relaunch starts unpaused.
    /// </summary>
    public void PauseIndexing()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(PauseIndexing);
            return;
        }
        if (IsIndexingPaused)
            return;

        IsIndexingPaused = true;
        _pausedIndexBuildFolder = _activeIndexBuildFolder;
        _indexBuildCancellation?.Cancel();
        YaguLog.For("ContentIndex").LogInformation("User paused indexing.");
        ShowIndexBuildingStatus();
        OnPropertyChanged(nameof(CanPauseIndexing));
    }

    /// <summary>
    /// Resumes indexing after a pause: clears the pause, replaces the cancellation source, and re-starts the
    /// build for the folder that was building when paused (if any). Safe from any thread.
    /// </summary>
    public void ResumeIndexing()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(ResumeIndexing);
            return;
        }
        if (!IsIndexingPaused)
            return;

        IsIndexingPaused = false;
        _indexBuildCancellation?.Dispose();
        _indexBuildCancellation = null;
        string? folder = _pausedIndexBuildFolder;
        _pausedIndexBuildFolder = null;
        _indexDiskFullMessage = null;
        YaguLog.For("ContentIndex").LogInformation("User resumed indexing.");
        OnPropertyChanged(nameof(CanPauseIndexing));

        if (!string.IsNullOrWhiteSpace(folder))
        {
            StartBackgroundIndexBuild(folder!);
        }
        else if (IndexedRootsPolicy.Normalize(_settings.IndexedRoots).Count > 0 && ResumeAutoIndexBuildAsync is { } resumeAutoBuild)
        {
            // The paused build was a multi-root auto/startup/scheduled pass with no single tracked folder.
            // Reset the indicator baseline, then re-run that pass over the registered folders via the
            // view-installed hook so a resume actually resumes (it skips folders whose index is already
            // fresh, and re-shows "Indexing…" as soon as it starts) instead of just clearing the indicator.
            RevertIndexIndicatorAfterBuild();
            _ = resumeAutoBuild();
        }
        else
        {
            RevertIndexIndicatorAfterBuild();
        }
    }

    /// <summary>
    /// Stops using the content index for searches for the rest of this session WITHOUT changing the saved
    /// setting — the feature and any registered roots stay, and a relaunch uses the index again. Backs the
    /// status indicator's "Disable index ▸ Disable index (this run)" command. Safe from any thread.
    /// </summary>
    public void DisableContentIndexThisRun()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(DisableContentIndexThisRun);
            return;
        }
        if (_disposed)
            return;

        UseContentIndex = false;
        YaguLog.For("ContentIndex").LogInformation("Status menu: disabled content-index use for this session (not persisted).");
        StatusText = "Content index off for this session — it will be used again next launch.";
        _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, false);
    }

    /// <summary>
    /// Turns the content-index feature OFF and SAVES the setting so it stays off across launches. Cancels any
    /// running tracked build and hides the status indicator. Registered roots and the index files on disk are
    /// kept, so re-enabling in Settings ▸ Indexing restores them. Backs the status indicator's
    /// "Disable index ▸ Disable indexing (persistent)" command. Safe from any thread.
    /// </summary>
    public async Task DisableContentIndexPersistentlyAsync()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = DisableContentIndexPersistentlyAsync());
            return;
        }
        if (_disposed)
            return;

        // Stop any in-flight tracked build promptly, then clear the paused state (we are turning it off,
        // not pausing).
        _indexBuildCancellation?.Cancel();
        IsIndexingPaused = false;

        _settings.EnableContentIndex = false;
        UseContentIndex = false;
        await PersistSettingsAsync().ConfigureAwait(true);

        Interlocked.Increment(ref _allDriveIndexHealthRefreshGeneration);
        _allDriveIndexHealth = Array.Empty<IndexRootHealthEntry>();
        AllDriveIndexStatusText = string.Empty;

        // Keep a muted "Index: off" indicator this session so the status menu (which now offers "Enable
        // indexing") stays reachable — otherwise the user could only re-enable via Settings ▸ Indexing.
        _indexOffIndicatorSticky = true;
        ShowIndexDisabledIndicator();
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        YaguLog.For("ContentIndex").LogInformation("Status menu: disabled content indexing persistently.");
        StatusText = "Content indexing turned off. Right-click ▸ Enable indexing to turn it back on.";
    }

    /// <summary>
    /// Re-enables using the content index for searches this session after "Disable index (this run)". Sets
    /// the session <see cref="UseContentIndex"/> flag back on without touching saved settings. Backs the
    /// status indicator's "Use index (this run)" command. Safe from any thread.
    /// </summary>
    public void EnableContentIndexThisRun()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(EnableContentIndexThisRun);
            return;
        }
        if (_disposed)
            return;

        UseContentIndex = true;
        YaguLog.For("ContentIndex").LogInformation("Status menu: re-enabled content-index use for this session.");
        StatusText = "Content index on for this session.";
        _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, UseContentIndex && _settings.EnableContentIndex);
        RefreshAllDriveIndexStatus();
    }

    /// <summary>
    /// Turns the content-index feature back ON and SAVES it after "Disable indexing (persistent)". Clears
    /// the sticky "Index: off" indicator and refreshes the status. Registered folders and their on-disk
    /// indexes were kept, so they become usable again immediately. Backs the status indicator's
    /// "Enable indexing" command. Safe from any thread.
    /// </summary>
    public async Task EnableContentIndexFromStatusMenuAsync()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = EnableContentIndexFromStatusMenuAsync());
            return;
        }
        if (_disposed)
            return;

        _settings.EnableContentIndex = true;
        UseContentIndex = true;
        _indexOffIndicatorSticky = false;
        await PersistSettingsAsync().ConfigureAwait(true);

        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        YaguLog.For("ContentIndex").LogInformation("Status menu: re-enabled content indexing (persistent).");
        StatusText = "Content indexing turned on.";
        _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, UseContentIndex && _settings.EnableContentIndex);
        RefreshAllDriveIndexStatus();
    }

    /// <summary>
    /// Applies and immediately persists one of the simple automatic-indexing presets offered by the
    /// main-window status overlay. If automatic passes were still configured to build only missing
    /// indexes, upgrades them to incremental maintenance so existing indexes are kept current.
    /// </summary>
    public async Task SetAutomaticIndexingPresetAsync(string trigger)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = SetAutomaticIndexingPresetAsync(trigger));
            return;
        }
        if (_disposed)
            return;

        bool supported =
            string.Equals(trigger, ContentIndexBuildScheduler.TriggerContinuous, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, ContentIndexBuildScheduler.TriggerWhenIdle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, ContentIndexBuildScheduler.TriggerAtStartup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, ContentIndexBuildScheduler.TriggerOnSchedule, StringComparison.OrdinalIgnoreCase);
        if (!supported)
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown automatic-indexing preset.");

        string normalizedTrigger = AppSettings.NormalizeIndexBuildTrigger(trigger);
        _settings.IndexBuildTrigger = normalizedTrigger;
        if (string.Equals(
                AppSettings.NormalizeIndexUpdateMode(_settings.IndexUpdateMode),
                AppSettings.DefaultIndexUpdateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            _settings.IndexUpdateMode = AppSettings.IndexUpdateModeAutomaticIncremental;
        }

        await PersistSettingsAsync().ConfigureAwait(true);

        YaguLog.For("ContentIndex").LogInformation(
            "Status overlay: automatic indexing saved with trigger {Trigger} and update mode {UpdateMode}.",
            _settings.IndexBuildTrigger,
            _settings.IndexUpdateMode);
        StatusText = ContentIndexUiStatus.SchedulingHint(_settings.IndexBuildTrigger) + " Setting saved.";
        RefreshCurrentIndexStatus();
        RefreshAllDriveIndexStatus();

        if (AppSettings.IndexBuildTriggerHas(
                normalizedTrigger,
                ContentIndexBuildScheduler.TriggerContinuous)
            && RequestIdleIndexMaintenanceAsync is { } requestMaintenance)
        {
            _ = requestMaintenance();
        }
    }

    /// <summary>Shows the muted "Index: off" status indicator (used after a menu-driven persistent disable),
    /// unless the user has turned the indicator off entirely in settings.</summary>
    private void ShowIndexDisabledIndicator()
    {
        if (!_settings.ShowIndexStatusInMainWindow)
        {
            ShowIndexStatus = false;
            return;
        }
        IndexStatusGlyph = "\uEA39"; // Blocked
        IndexStatusText = "Index: off";
        IndexStatusTooltip = "Content indexing is off. Right-click \u25B8 Enable indexing to turn it back on."
            + BuildIndexDateDetails();
        ShowIndexStatus = true;
    }

    /// <summary>
    /// Called when an index build is stopped because the index drive reached its used-space limit
    /// (plan §11.2). Auto-pauses indexing (so auto/watcher builds don't immediately retry) and shows a
    /// disk-full warning in the status-bar indicator. The user frees space then right-clicks ▸ Resume.
    /// Safe from any thread.
    /// </summary>
    public void OnIndexBuildStoppedForDiskSpace(string driveDisplayName, double usedPercent, int thresholdPercent)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnIndexBuildStoppedForDiskSpace(driveDisplayName, usedPercent, thresholdPercent));
            return;
        }

        _indexDiskFullMessage =
            $"Indexing stopped: {driveDisplayName} is {usedPercent:F0}% full (limit {thresholdPercent}%). "
            + "Free disk space, then right-click ▸ Resume indexing — or raise the limit in Settings ▸ Indexing.";
        if (!IsIndexingPaused)
        {
            IsIndexingPaused = true;
            _pausedIndexBuildFolder = _activeIndexBuildFolder;
        }
        YaguLog.For("ContentIndex").LogWarning(
            "Indexing stopped for disk space: {Drive} {UsedPercent:F1}% full (limit {ThresholdPercent}%).",
            driveDisplayName, usedPercent, thresholdPercent);
        ShowIndexBuildingStatus();
        OnPropertyChanged(nameof(CanPauseIndexing));
    }

    /// <summary>
    /// Marks that a background index build has started and shows an "Indexing…" state in the main-window
    /// index indicator (overriding availability/coverage until every active build finishes). Safe to call
    /// from any thread. Each call MUST be paired with <see cref="EndIndexBuildActivity"/>.
    /// </summary>
    public void BeginIndexBuildActivity(string? folder = null, bool isIncremental = false)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => BeginIndexBuildActivity(folder, isIncremental));
            return;
        }

        _activeIndexBuilds++;
        if (!string.IsNullOrWhiteSpace(folder))
            _activeIndexBuildFolder = folder;
        _activeIndexBuildIsIncremental = isIncremental;
        _indexBuildPercent = -1; // fresh build starts at an unknown estimate
        ShowIndexBuildingStatus();
        OnPropertyChanged(nameof(IsIndexBuildActive));
        OnPropertyChanged(nameof(CanPauseIndexing));
    }

    /// <summary>
    /// Updates the estimated percent-complete (0–100, or -1 for unknown) shown at the end of the "Indexing…"
    /// tooltip. Called periodically from a running build (off the UI thread), so it self-marshals and only
    /// refreshes the tooltip when the value actually changed and a build is still active and unpaused.
    /// </summary>
    public void ReportIndexBuildProgress(int percent) => ReportIndexBuildProgress(null, percent, null);

    /// <summary>
    /// Reports which folder a multi-root pass is currently indexing (so the tooltip names the drive) together
    /// with its percent-complete. Passing a non-empty <paramref name="folder"/> updates the active folder
    /// without changing the active-build count; <paramref name="percent"/> is the 0–100 estimate (or -1 when
    /// unknown). Self-marshals; a late report after the build finished is ignored.
    /// </summary>
    public void ReportIndexBuildProgress(string? folder, int percent) => ReportIndexBuildProgress(folder, percent, null);

    /// <summary>Reports folder, progress, and worker stage. The incremental stage is retained so the status
    /// bar says the existing index is being updated rather than implying a full rebuild.</summary>
    public void ReportIndexBuildProgress(string? folder, int percent, string? stage)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ReportIndexBuildProgress(folder, percent, stage));
            return;
        }

        if (_activeIndexBuilds <= 0)
            return; // build finished (or none active) — ignore a late report

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(folder)
            && !string.Equals(folder, _activeIndexBuildFolder, StringComparison.OrdinalIgnoreCase))
        {
            _activeIndexBuildFolder = folder;
            changed = true;
        }
        if (percent != _indexBuildPercent)
        {
            _indexBuildPercent = percent;
            changed = true;
        }
        bool incremental = string.Equals(stage, "incremental", StringComparison.OrdinalIgnoreCase);
        if (stage is not null && incremental != _activeIndexBuildIsIncremental)
        {
            _activeIndexBuildIsIncremental = incremental;
            changed = true;
        }

        if (changed && !IsIndexingPaused)
            ShowIndexBuildingStatus();
    }

    /// <summary>
    /// Marks that a background index build has finished. When the last active build completes, the
    /// main-window index indicator reverts to the availability/coverage status for the last search
    /// context (or is hidden if none) — unless indexing is paused, in which case the paused state stays.
    /// Safe to call from any thread.
    /// </summary>
    public void EndIndexBuildActivity()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(EndIndexBuildActivity);
            return;
        }

        _activeIndexBuilds = Math.Max(0, _activeIndexBuilds - 1);
        OnPropertyChanged(nameof(IsIndexBuildActive));
        OnPropertyChanged(nameof(CanPauseIndexing));

        if (_activeIndexBuilds > 0)
        {
            ShowIndexBuildingStatus();
            return;
        }

        // While paused, keep the "Indexing paused" indicator until the user resumes.
        if (IsIndexingPaused)
        {
            ShowIndexBuildingStatus();
            return;
        }

        _activeIndexBuildFolder = null;
        _activeIndexBuildIsIncremental = false;
        RevertIndexIndicatorAfterBuild();
    }

    /// <summary>Reverts the main-window index indicator from a build state back to availability/coverage for
    /// the last search context (or hides it / shows a one-shot "Index: ready" when there is none).</summary>
    private void RevertIndexIndicatorAfterBuild()
    {
        ShowIndexBuildPercent = false;
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
        {
            ShowIndexStatus = false;
            return;
        }

        if (IsIndexWarmActive && !string.IsNullOrWhiteSpace(_activeIndexWarmFolder))
        {
            ShowIndexWarmPreparingStatus(_activeIndexWarmFolder);
            return;
        }
        if (IsIndexWarmPausedForSearch)
        {
            ShowIndexWarmPausedStatus();
            return;
        }

        if (_lastIndexStatusRoots.Count > 0)
        {
            _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, _lastIndexStatusUseThisSearch);
        }
        else
        {
            IndexStatusGlyph = ContentIndexUiStatus.AvailabilityGlyph(IndexAvailability.Available);
            IndexStatusText = "Index: ready";
            IndexStatusTooltip = "The content index finished building. Matching files are always read live from disk. "
                + BuildIndexDateDetails()
                + BuildIndexSchedulingDetails();
            ShowIndexStatus = true;
        }
        RefreshAllDriveIndexStatus();
    }

    /// <summary>Renders the "Indexing…" (or "Indexing paused") state on the main-window index indicator
    /// (no-op when the user has hidden index status).</summary>
    private void ShowIndexBuildingStatus()
    {
        if (!_settings.ShowIndexStatusInMainWindow)
            return;

        if (IsIndexingPaused)
        {
            ShowIndexBuildPercent = false;
            if (_indexDiskFullMessage is { } diskFull)
            {
                IndexStatusGlyph = ContentIndexUiStatus.StatusWarningGlyph;
                IndexStatusText = "Index: disk full";
                IndexStatusTooltip = diskFull + BuildIndexDateDetails();
                ShowIndexStatus = true;
                return;
            }

            IndexStatusGlyph = "\uE769"; // Pause
            IndexStatusText = "Indexing paused";
            IndexStatusTooltip = (string.IsNullOrWhiteSpace(_activeIndexBuildFolder)
                ? "Indexing is paused. Right-click to resume."
                : $"Indexing of {_activeIndexBuildFolder} is paused. Right-click to resume.")
                + BuildIndexDateDetails();
            ShowIndexStatus = true;
            return;
        }

        IndexStatusGlyph = "\uE895"; // Sync
        // Surface the estimate right in the status-bar text so the progress is visible at a glance, and
        // populate the custom tooltip's big percent + progress bar (below).
        string activity = _activeIndexBuildIsIncremental ? "Updating index" : "Indexing";
        IndexStatusText = _activeIndexBuildIsIncremental && _indexBuildPercent >= 100
            ? "Finalizing index update\u2026"
            : _indexBuildPercent >= 0 ? $"{activity}\u2026 {_indexBuildPercent}%" : $"{activity}\u2026";
        if (_activeIndexBuildIsIncremental)
        {
            IndexStatusTooltip = string.IsNullOrWhiteSpace(_activeIndexBuildFolder)
                ? "Updating the existing content index incrementally\u2026 This runs in the background; searches keep working and the current index remains available. Right-click to pause."
                : $"Updating the existing content index for {_activeIndexBuildFolder} incrementally\u2026 This runs in the background; searches keep working and the current index remains available. Right-click to pause.";
        }
        else
        {
            IndexStatusTooltip = string.IsNullOrWhiteSpace(_activeIndexBuildFolder)
                ? "Building the content index\u2026 This runs in the background; searches keep working and results never change. Right-click to pause."
                : $"Building a content index for {_activeIndexBuildFolder}\u2026 This runs in the background; searches keep working and results never change. Right-click to pause.";
        }
        IndexStatusTooltip += BuildIndexDateDetails();
        if (_indexBuildPercent >= 0)
        {
            IndexBuildPercentText = $"{_indexBuildPercent}%";
            IndexBuildPercentValue = _indexBuildPercent;
            ShowIndexBuildPercent = true;
        }
        else
        {
            ShowIndexBuildPercent = false;
        }
        ShowIndexStatus = true;
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
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        string normalizedDirectory = DriveEnumerator.NormalizeSearchRoot(Directory);
        bool directorySpecified = normalizedDirectory.Length > 0;
        if (directorySpecified && !string.Equals(Directory, normalizedDirectory, StringComparison.Ordinal))
            Directory = normalizedDirectory;
        if (directorySpecified && !System.IO.Directory.Exists(normalizedDirectory))
        {
            ErrorText = $"Directory does not exist: {normalizedDirectory}";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }
        // An empty directory means "search all drives" — resolve the eligible roots now.
        var targetRoots = ResolveTargetRoots();
        if (targetRoots.Count == 0)
        {
            ErrorText = "No drives are available to search.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }
        if (string.IsNullOrEmpty(Query))
        {
            ErrorText = "Enter a search query.";
            ResumeContentIndexWarmupAfterSearch();
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
                ResumeContentIndexWarmupAfterSearch();
                return;
            }
        }

        long effectiveMinFileSizeBytes = MinFileSizeBytes;
        long effectiveMaxFileSizeBytes = MaxFileSizeBytes;
        if (effectiveMinFileSizeBytes > 0 && effectiveMaxFileSizeBytes > 0 && effectiveMinFileSizeBytes > effectiveMaxFileSizeBytes)
        {
            ErrorText = "Minimum file size cannot be larger than maximum file size.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        if (IsDateRangeInvalid(CreatedAfterDate, CreatedBeforeDate))
        {
            ErrorText = "Created after date cannot be later than created before date.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        if (IsDateRangeInvalid(ModifiedAfterDate, ModifiedBeforeDate))
        {
            ErrorText = "Modified after date cannot be later than modified before date.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        int runId = Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);
        ResetRuntimeIndexStatus(runId);

        // Fire-and-forget: refresh the main-window content-index availability indicator for the roots
        // this search covers (plan §6.2). Presence-only, runs off the UI thread, and never blocks or
        // delays the search — filename-first results are unaffected.
        _ = RefreshIndexStatusAsync(targetRoots, UseContentIndex && _settings.EnableContentIndex);

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

            int baseParallelism = ResolveParallelism(ParallelismIndex);
            // One-shot HDD parallelism override chosen in the warning dialog; applies to this search
            // only. Consume it now so it never leaks into a later search.
            int? hddParallelismOverride = _hddParallelismOverrideIndexForNextSearch;
            _hddParallelismOverrideIndexForNextSearch = null;
            SearchOptions BuildOptionsForRoot(string dir, int parallelism, FileListerBackend? backendOverride, bool isHardDisk) => new SearchOptions
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
                FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(FileIoTimeoutSeconds),
                AbsoluteMaxResults = AbsoluteMaxResults,
                SkipBinary = SkipBinary,
                AvoidSourceMemoryMap = DriveEnumerator.ShouldAvoidSourceMemoryMap(
                    DriveEnumerator.GetDriveTypeForPath(dir)),
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
                ImageOcrWorkerParallelism = OcrWorkerParallelism.Resolve(
                    ImageOcrWorkerParallelism,
                    AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),
                    Environment.ProcessorCount,
                    LimitParallelismOnHdd,
                    isHardDisk),
                SearchPdfText = SearchPdfText,
                PdfTextExtensions = ParseExtensionSet(AppSettings.DefaultPdfTextExtensions),
                MaxDegreeOfParallelism = parallelism,
                FileListerBackendOverride = backendOverride,
                IoOversubscriptionIndex = IoOversubscriptionIndex,
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
                ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths,
                MaxSearchDepth = double.IsNaN(MaxSearchDepth) ? 0 : (int)MaxSearchDepth,
                DegradedResultStore = _resultStore,
                // Session-only content-index opt-in, gated by the master feature (plan §5/§6.1). Only
                // prunes the ordinary-text candidate set; orthogonal to the image/PDF/archive toggles.
                UseContentIndex = UseContentIndex && _settings.EnableContentIndex,
            };

            // Attaches the content-index pruning gate factory to a per-root options set (plan §5). The
            // factory is a closure invoked later, off the UI thread, at the start of that root's discovery,
            // so no index/journal I/O runs here. A null factory (feature off) leaves the live-scan path
            // untouched.
            void AttachContentIndexGateFactory(SearchOptions rootOptions, string root)
            {
                if (!rootOptions.UseContentIndex)
                    return;

                AppSettings settings = _settings;
                string storageDir = settings.IndexStorageDirectory;
                int retained = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount);
                // Opt-in: route the query through the isolated out-of-process worker (identical results, but a
                // native/read fault is contained in the worker). Falls back in-process on any worker failure.
                Yagu.Services.Index.IIndexCandidateSource? candidateSource =
                    settings.IndexUseNativeWorker ? GetOrCreateIndexWorkerSource() : null;
                int maxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(settings.IndexMaxInProcessSizeMB);
                int maxWorkerQuerySizeMB = AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(settings.IndexMaxWorkerQuerySizeMB);
                string ResolveIndexRoot(IContentIndexPathProvider pathProvider)
                    => new ContentIndexManager(pathProvider, retained)
                        .ResolveBestAvailableIndexRoot(root, settings.IndexedRoots);
                rootOptions.ContentIndexGateFactory = () =>
                {
                    // Stage-5 (plan §5.8): when the worker PRUNING path is enabled it supersedes the
                    // in-process gate — never open the index in-process (the worker path's whole purpose is a
                    // bounded host footprint, so a large scope is served by the worker or live-scanned).
                    if (settings.IndexUseWorkerQuerySessions)
                        return null;
                    var pathProvider = DefaultContentIndexPathProvider.Create(storageDir);
                    string indexRoot = ResolveIndexRoot(pathProvider);
                    // Size gate (plan §6.1): an index whose on-disk size exceeds the in-process limit is NEVER
                    // loaded into memory. Deserializing a multi-GB layered index leaves a multi-GB resident
                    // footprint that trips the search memory monitor into degraded mode, making the search
                    // SLOWER than a plain live scan — so such a scope always live-scans and is never warmed.
                    if (!ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(pathProvider, indexRoot, retained, maxInProcessSizeMB))
                    {
                        long activeBytes = new ContentIndexStore(
                            pathProvider,
                            ContentIndexManager.ScopeIdForRoot(indexRoot),
                            retained).GetCurrentLayeredIndexSizeBytes();
                        string reason = activeBytes <= 0
                            ? "no trusted index is available"
                            : $"active index size {ResourceUsageMonitor.FormatBytes(activeBytes)} exceeds the configured {ResourceUsageMonitor.FormatBytes((long)maxInProcessSizeMB * 1024 * 1024)} in-process limit; enable memory-mapped worker query sessions with format-v3 data to serve this large index";
                        ReportContentIndexAttempt(runId, root, false, reason);
                        return null;
                    }
                    // Don't block the first result on a COLD index open. A large layered index (a multi-GB
                    // base + delta segments) can take tens of seconds to deserialize — far slower than simply
                    // live-scanning — so if it isn't already warm (deserialized in the query-mode cache) for
                    // this scope, live-scan THIS search and warm the index in the background so the NEXT search
                    // is index-accelerated.
                    if (!ContentIndexSearchGate.IsScopeWarm(pathProvider, indexRoot, retained))
                    {
                        StartContentIndexWarmup(indexRoot);
                        return null;
                    }
                    var gate = ContentIndexSearchGate.TryCreate(
                        pathProvider,
                        indexRoot,
                        rootOptions,
                        settings,
                        retained,
                        journalReader: null,
                        candidateSource: candidateSource,
                        onAttempt: (active, reason) =>
                            ReportContentIndexAttempt(runId, root, active, reason));
                    // Capture the live gate so InitializeResultGroup can classify per-file provenance.
                    if (gate is not null)
                        lock (_indexGatesLock)
                            _activeIndexGates.Add(gate);
                    return gate;
                };

                // Stage-5 worker PRUNING path (plan §5.8): when the user-selectable mapped-worker setting
                // is on, prune this root via the isolated worker over its memory-mapped v3 WITHOUT loading the
                // index into the host — so a large scope over IndexMaxInProcessSizeMB is served with a bounded
                // host footprint (the in-process gate above returns null when this is on — mutually exclusive).
                // The factory takes the search's survivor sink (its pending-file writer); it forwards survivors
                // and prunes proven-nonmembers, rescuing the dirty subset at B1. Returns null → live-scan when
                // the worker cannot serve the scope (never a large in-process deserialize). Reuses the single
                // long-lived worker client.
                if (settings.IndexUseWorkerQuerySessions)
                {
                    Yagu.Services.Index.IndexWorkerClient pruningClient = GetOrCreateIndexWorkerClient();
                    int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(settings.IndexMaxJournalCatchupRecords);
                    int queryWorkerParallelism = Yagu.Services.Index.IndexWorkerParallelism.ResolveQueryDegree(
                        settings.IndexQueryWorkerParallelism,
                        Environment.ProcessorCount,
                        settings.LimitParallelismOnHdd,
                        Yagu.Helpers.DiskTypeDetector.IsHardDisk(root));
                    string spoolDir = System.IO.Path.Combine(storageDir, "query-spool");
                    rootOptions.ContentIndexPruningScanFactory = survivorSink =>
                    {
                        // Out-of-process size cap (IndexMaxWorkerQuerySizeMB, default 30 GB): the worker MAPS
                        // rather than deserializes the index, so it serves far larger scopes than the in-process
                        // cap — but is still bounded. An index over this size (or none) live-scans instead.
                        var workerPathProvider = DefaultContentIndexPathProvider.Create(storageDir);
                        string indexRoot = ResolveIndexRoot(workerPathProvider);
                        var store = new Yagu.Services.Index.ContentIndexStore(
                            workerPathProvider,
                            Yagu.Services.Index.ContentIndexManager.ScopeIdForRoot(indexRoot),
                            retained);
                        if (!ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(workerPathProvider, indexRoot, retained, maxWorkerQuerySizeMB))
                        {
                            long mappedBytes = store.GetCurrentLayeredMappedQuerySizeBytes();
                            string reason = mappedBytes <= 0
                                ? "no trusted format-v3 query index is available"
                                : $"mapped query index size {ResourceUsageMonitor.FormatBytes(mappedBytes)} exceeds the configured {ResourceUsageMonitor.FormatBytes((long)maxWorkerQuerySizeMB * 1024 * 1024)} worker limit";
                            ReportContentIndexAttempt(runId, root, false, reason);
                            return null;
                        }
                        var scan = Yagu.Services.Index.ContentIndexShadowScopeBuilder.TryCreatePruningScan(
                            pruningClient,
                            store,
                            rootOptions,
                            System.Threading.Interlocked.Increment(ref _shadowQuerySessionId),
                            Yagu.Services.Index.ContentIndexFreshnessEvaluator.CreateReader(
                                maxCatchupRecords,
                                TimeSpan.FromSeconds(AppSettings.NormalizeFileIoTimeoutSeconds(settings.FileIoTimeoutSeconds))),
                            spoolDir,
                            survivorSink,
                            workerParallelism: queryWorkerParallelism,
                            onAttempt: (active, reason) =>
                                ReportContentIndexAttempt(runId, root, active, reason));
                        // Capture the live scan so InitializeResultGroup can badge index-member result files.
                        if (scan is not null)
                            lock (_indexGatesLock)
                                _activePruningScans.Add(scan);
                        return scan;
                    };
                }

                // Extended-source (PDF-text) pruning (plan §7 Phase 4): skip PDFs whose extracted text cannot
                // contain a match. Off by default; only engages when a determinism-proven PDF namespace was
                // built for this root AND this search extracts PDF text. Fail-safe: null → extract every PDF.
                if ((settings.IndexBuildPdfTextExtendedSource && rootOptions.SearchPdfText)
                    || (settings.IndexBuildImageTextExtendedSource && rootOptions.SearchImageText))
                {
                    rootOptions.ExtendedSourceGateFactory = () =>
                    {
                        var extendedPathProvider = DefaultContentIndexPathProvider.Create(storageDir);
                        string indexRoot = ResolveIndexRoot(extendedPathProvider);
                        return Yagu.Services.Index.ExtendedSourceSearchGate.TryCreate(
                            extendedPathProvider,
                            indexRoot,
                            rootOptions,
                            settings);
                    };
                }
            }

            // One options set per target root. When searching all drives, each root gets its own
            // parallelism: HDD roots are forced to 1 (avoid thrashing) while other drives use the
            // configured value. Backend stays Auto so each root uses the fast Everything index when
            // it covers that drive (including drives the user added manually in Everything's settings)
            // and automatically falls back to the managed walker only for drives Everything does not
            // index — except when "force full scan" is enabled, which walks every drive directly.
            var perRootOptions = new List<SearchOptions>(targetRoots.Count);
            FileListerBackend? allDrivesBackendOverride =
                (!directorySpecified && SearchAllDrivesForceFullScan) ? FileListerBackend.Managed : null;
            // Drop any gates captured by a previous search before this one's factories start populating them.
            lock (_indexGatesLock)
            {
                _activeIndexGates.Clear();
                _activePruningScans.Clear();
            }
            foreach (var root in targetRoots)
            {
                int parallelism = baseParallelism;
                bool isHardDisk = Yagu.Helpers.DiskTypeDetector.IsHardDisk(root);
                if (LimitParallelismOnHdd && isHardDisk)
                    parallelism = hddParallelismOverride is int overrideIndex ? ResolveParallelism(overrideIndex) : 1;
                var rootOptions = BuildOptionsForRoot(root, parallelism, allDrivesBackendOverride, isHardDisk);
                AttachContentIndexGateFactory(rootOptions, root);
                perRootOptions.Add(rootOptions);
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
            _activeSearchRoots = targetRoots.ToArray();
            var token = cts.Token;
            lowDiskMonitorTask = StartLowDiskSpaceMonitor(runId, cts, _resultStore);
            YaguLog.For("Search").LogWarning("Starting search #{RunId}: query='{Query}', dir='{Dir}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}", runId, Query, directorySpecified ? Directory : "<all drives: " + targetRoots.Count + ">", UseRegex, CaseSensitive, SearchModeIndex);

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
                    YaguLog.For("Search").LogWarning("Ignoring stale search #{RunId} event after a newer search started", runId);
                    break;
                }

                // Periodic UI consumer throughput log
                now = Stopwatch.GetTimestamp();
                if ((now - uiLastLogTicks) >= Stopwatch.Frequency * UiLogIntervalSec)
                {
                    uiLastLogTicks = now;
                    YaguLog.For("UIConsumer").LogWarning(
                        "Events received={Events:N0}, matchesReceived={Matches:N0}, " +
                        "groups={Groups:N0}, yields={Yields:N0}, " +
                        "degraded={Degraded}, diskEvicted={DiskEvicted:N0}",
                        uiEventsReceived, uiMatchesReceived, _resultCollection.AllGroups.Count, uiYieldCount, Degraded, _resultStore?.EvictedCount ?? 0);
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
                        SearchInNameFirstPhase = false; // full total known — determinate bar from here
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
                            YaguLog.For("UIConsumer").LogWarning(
                                "Slow AddMatches: {Count} results took {ElapsedMs}ms " +
                                "(groups={Groups:N0})",
                                mb.Results.Count, uiEventSw.ElapsedMilliseconds, _resultCollection.AllGroups.Count);
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
                            YaguLog.For("UIConsumer").LogWarning(
                                "Slow AddSourceBackedMatches: {Count} results took {ElapsedMs}ms " +
                                "(groups={Groups:N0})",
                                sb.Results.Count, uiEventSw.ElapsedMilliseconds, _resultCollection.AllGroups.Count);
                        }
                        break;
                    case SearchEvent.Progress p:
                        FilesScanned = p.Snapshot.FilesScanned;
                        TotalFiles = p.Snapshot.TotalFiles;
                        // Latch out of the indeterminate name-first phase once the full-scan total is live.
                        if (SearchInNameFirstPhase && !p.Snapshot.NameFirstPhase)
                            SearchInNameFirstPhase = false;
                        UpdateSearchProgressPhaseLabel(p.Snapshot);
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
                        YaguLog.For("ViewModel").LogWarning("Memory pressure event received — starting async eviction ({Groups:N0} groups, {Matches:N0} matches)", _resultCollection.AllGroups.Count, MatchesFound);
                        // Fire-and-forget from the UI thread: the background task may wait
                        // for ResultStore queue space so existing payloads do not pile up
                        // in RAM while the disk writer catches up.
                        _ = Task.Run(() =>
                        {
                            var evictSw = Stopwatch.StartNew();
                            int enqueued = EvictAllResults();
                            evictSw.Stop();
                            YaguLog.For("ViewModel").LogWarning("Eviction enqueued {Enqueued:N0} results in {ElapsedMs}ms (drain continues in background)", enqueued, evictSw.ElapsedMilliseconds);

                            // Acknowledge immediately so SearchService leaves eviction-in-flight
                            // state and can fire the next pressure cycle if memory is still high.
                            try { mp.AcknowledgeEviction(enqueued); }
                            catch (Exception ex) { YaguLog.For("ViewModel").LogWarning(ex, "AcknowledgeEviction threw"); }

                            // Wait for the background drain to flush bytes to disk before
                            // triggering the compacting GC — otherwise we'd compact while
                            // the match-line/context strings are still rooted by the channel.
                            try { _resultStore?.Drain(); }
                            catch (Exception ex) { YaguLog.For("ViewModel").LogWarning(ex, "ResultStore drain failed"); }

                            // A zero-result eviction freed no managed payload. Forcing a full GC and
                            // trimming the working set in that case only causes page-fault churn while
                            // the native scanner continues reading files. The SearchService callback
                            // already applies the same productive-eviction guard; keep the post-drain GC
                            // here only when this pass actually queued payloads for eviction.
                            if (IsSearching && enqueued > 0)
                                SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(3));
                            else
                                CollectPostEvictionIfDue();
                        });
                        break;
                    case SearchEvent.MemoryPressureRelieved relieved:
                        Degraded = false;
                        DegradedNoticeText = string.Empty;
                        UpdateFilesPerSecond();
                        YaguLog.For("ViewModel").LogWarning("Memory pressure relieved — leaving memory-saving mode ({Diagnostics})", relieved.Diagnostics);
                        break;
                    case SearchEvent.ScanCompleted sc:
                        var scanElapsed = StopSearchTimer();
                        FilesScanned = sc.Summary.FilesScanned;
                        TotalFiles = sc.Summary.TotalFiles;
                        SearchInNameFirstPhase = false;
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
                        YaguLog.For("UIConsumer").LogWarning(
                            "Search #{RunId} completed: uiEvents={Events:N0}, uiMatches={Matches:N0}, " +
                            "groups={Groups:N0}, yields={Yields:N0}, " +
                            "diskEvicted={DiskEvicted:N0}",
                            runId, uiEventsReceived, uiMatchesReceived, _resultCollection.AllGroups.Count, uiYieldCount, _resultStore?.EvictedCount ?? 0);
                        var completedElapsed = StopSearchTimer();
                        int actualTotalMatches = Math.Max(c.Summary.TotalMatches, ClampMatchCount(uiMatchesReceived));
                        FilesScanned = c.Summary.FilesScanned;
                        TotalFiles = c.Summary.TotalFiles;
                        SearchInNameFirstPhase = false;
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
                        UpdateIndexCoverageStatus(c.Summary.IndexAcceleration);
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
                    YaguLog.For("Search").LogWarning("Search #{RunId} terminated because temp-file drive {Drive} is {UsedPercent:F1}% full", runId, lowDiskSpace.DriveDisplayName, lowDiskSpace.UsedPercent);
                    SearchTerminatedByLowDiskSpace?.Invoke(message);
                }
                else
                {
                    StatusText = BuildCancelledStatus(cancelledElapsed);
                    YaguLog.For("Search").LogInformation("Search #{RunId} cancelled", runId);
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
                YaguLog.For("Search").LogCritical(ex, "Search #{RunId} failed", runId);
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
                _activeSearchRoots = Array.Empty<string>();
            }

            try { cts?.Cancel(); } catch { }
            if (lowDiskMonitorTask is not null)
                await lowDiskMonitorTask.ConfigureAwait(true);

            cts?.Dispose();
            _searchLifecycleGate.Release();
            ResumeContentIndexWarmupAfterSearch();
        }
    }

    /// <summary>Cancels only active work whose captured root lies on a removed volume. This is a transient
    /// device-loss response, not the user-visible indexing pause state.</summary>
    public void CancelOperationsForRemovedVolumes(IReadOnlyList<string> removedVolumeRoots)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => CancelOperationsForRemovedVolumes(removedVolumeRoots));
            return;
        }
        if (removedVolumeRoots is null || removedVolumeRoots.Count == 0)
            return;

        if (_activeSearchRoots.Any(root => DeviceVolumeChange.IntersectsAnyRoot(root, removedVolumeRoots)))
        {
            try { _cts?.Cancel(); } catch { }
            StatusText = "Search cancelled because a source drive was removed.";
        }

        if (DeviceVolumeChange.IntersectsAnyRoot(_activeIndexBuildFolder, removedVolumeRoots)
            || DeviceVolumeChange.IntersectsAnyRoot(_activeIndexWarmFolder, removedVolumeRoots))
        {
            try { _indexBuildCancellation?.Cancel(); } catch { }
            try { _indexWarmCancellation?.Cancel(); } catch { }
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
            YaguLog.For("Search").LogCritical(ex, "Single-file-path display failed");
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
            YaguLog.For("Search").LogInformation("Cancelling previous search before starting search #{RunId}", runId);
        }
        catch (Exception ex)
        {
            YaguLog.For("Search").LogWarning(ex, "Previous search cleanup cancellation failed");
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
        _searchProgressPhaseLabel = string.Empty;
        _sourceBackedSearchProgress = null;
        OnPropertyChanged(nameof(SearchProgressRightLabel));
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
        IsPreparingSearch = false;   // the scan committed — hand feedback off to IsSearching
        // Stay indeterminate seamlessly from the preparing phase through the name-first pass; a progress
        // snapshot reporting the full phase (or discovery completion) latches this false for the content scan.
        SearchInNameFirstPhase = true;
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
            catch (Exception ex) { YaguLog.For("Search").LogWarning(ex, "Low disk-space cancellation failed"); }
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
            YaguLog.For("ResultStore").LogWarning(ex, "Could not create result store in '{TempDir}', falling back to Windows temp", tempDir);
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
        string? phase = progress.SourceBacked?.BuildPhaseLabel(progress.FilesScanned, progress.TotalFiles);
        if (progress.TotalFiles > 0)
        {
            return phase is null
                ? $"{prefix} {progress.FilesScanned:N0}/{progress.TotalFiles:N0} files... {progress.MatchesFound:N0} matches"
                : $"{prefix} {phase}... {progress.MatchesFound:N0} matches";
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

    // ── Status-bar resource-usage monitor ──
    // A slow (10 s) background loop that measures Yagu's disk-temp footprint, total content-index storage,
    // and RAM (plus its worker children), then publishes formatted labels to the status bar. All measurement
    // runs on the thread pool (the PeriodicTimer resumes off the UI thread). The potentially larger index
    // measurement is cached for one minute and never refreshed during a search. Only the final string
    // assignments are marshalled back to the UI thread.

    /// <summary>Starts the periodic resource-usage monitor (idempotent). Called once at construction.</summary>
    private void StartResourceUsageMonitor()
    {
        if (_disposed || _resourceMonitorCts is not null)
            return;
        var cts = new CancellationTokenSource();
        _resourceMonitorCts = cts;
        _ = RunResourceUsageMonitorAsync(cts);
    }

    private void StopResourceUsageMonitor()
    {
        var cts = Interlocked.Exchange(ref _resourceMonitorCts, null);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task RunResourceUsageMonitorAsync(CancellationTokenSource cts)
    {
        try
        {
            // Publish an immediate first sample so the indicators aren't blank for the first 10 s.
            await Task.Run(() => MeasureAndPublishResourceUsage(cts.Token), cts.Token).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (_disposed)
                    break;
                MeasureAndPublishResourceUsage(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            YaguLog.For("ViewModel").LogDebug(ex, "Resource-usage monitor stopped unexpectedly.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _resourceMonitorCts, null, cts);
            try { cts.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Measures (off the UI thread) disk temp, cached index storage, and RAM used by Yagu plus each worker
    /// child process, then marshals the formatted status-bar labels to the UI thread.
    /// </summary>
    private void MeasureAndPublishResourceUsage(CancellationToken cancellationToken)
    {
        try
        {
            // Temp footprint: sum this process's evicted-result temp files (metadata only).
            long tempBytes = ResourceUsageMonitor.SumProcessTempResultBytes(SearchResultTempDirectory, Environment.ProcessId);

            IndexStorageSizeMeasurement? indexMeasurement = MeasureIndexStorageUsage(cancellationToken, out string indexRoot);

            // RAM: this Yagu process + its own worker children (attributed by parent PID so a second Yagu
            // instance's workers are not double-counted).
            long totalRamBytes = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes();
            var breakdown = new List<(string Name, long Bytes)>();
            long selfBytes;
            try
            {
                using var self = Process.GetCurrentProcess();
                selfBytes = self.WorkingSet64;
            }
            catch { selfBytes = Environment.WorkingSet; }
            breakdown.Add(("Yagu", selfBytes));

            long usedBytes = selfBytes;
            int myPid = Environment.ProcessId;
            foreach (string workerName in OrphanedWorkerCleanup.WorkerProcessNames)
            {
                Process[] workers;
                try { workers = Process.GetProcessesByName(workerName); }
                catch { continue; }

                long workerBytes = 0;
                foreach (Process worker in workers)
                {
                    try
                    {
                        if (OrphanedWorkerCleanup.GetParentProcessId(worker.Id) == myPid)
                            workerBytes += worker.WorkingSet64;
                    }
                    catch { /* exited / access denied — skip */ }
                    finally { try { worker.Dispose(); } catch { } }
                }
                if (workerBytes > 0)
                {
                    breakdown.Add((workerName, workerBytes));
                    usedBytes += workerBytes;
                }
            }

            string tempText = ResourceUsageMonitor.FormatTempStatus(tempBytes);
            string tempTooltip = ResourceUsageMonitor.BuildTempTooltip(tempBytes, SearchResultTempDirectory);
            string? indexText = indexMeasurement is { } measuredIndex
                ? ResourceUsageMonitor.FormatIndexStatus(measuredIndex.Bytes)
                : null;
            string? indexTooltip = indexMeasurement is { } measuredTooltipIndex
                ? ResourceUsageMonitor.BuildIndexTooltip(measuredTooltipIndex, indexRoot)
                : null;
            string ramText = ResourceUsageMonitor.FormatRamStatus(usedBytes, totalRamBytes);
            string ramTooltip = ResourceUsageMonitor.BuildRamTooltip(breakdown, usedBytes, totalRamBytes);

            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed)
                    return;
                TempUsageText = tempText;
                TempUsageTooltip = tempTooltip;
                if (indexText is not null && indexTooltip is not null)
                {
                    IndexUsageText = indexText;
                    IndexUsageTooltip = indexTooltip;
                }
                RamUsageText = ramText;
                RamUsageTooltip = ramTooltip;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            YaguLog.For("ViewModel").LogDebug(ex, "Resource-usage sample failed.");
        }
    }

    private IndexStorageSizeMeasurement? MeasureIndexStorageUsage(
        CancellationToken cancellationToken,
        out string indexRoot)
    {
        indexRoot = DefaultContentIndexPathProvider.Create(_settings.IndexStorageDirectory).IndexRoot;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var backend = (FileListerBackend)FileListerBackendIndex;
        bool sameRoot = string.Equals(_cachedIndexStorageRoot, indexRoot, StringComparison.OrdinalIgnoreCase);
        bool sameBackend = _cachedIndexStorageBackend == backend;

        // The 10-second resource loop may continue during a search, but an index-size refresh never competes
        // with file discovery or scanning. Reuse the last value until the search ends; otherwise refresh no
        // more than once per minute. A storage-location or backend change invalidates the cache immediately.
        if (_hasCachedIndexStorageSize && sameRoot && sameBackend && now < _nextIndexStorageSizeRefreshUtc)
        {
            return _cachedIndexStorageSize;
        }

        // Never start a storage walk while a search is active. If there is no current-root cached value yet,
        // leave the existing label unchanged and retry on the next monitor tick after the search.
        if (IsSearching)
            return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;

        cancellationToken.ThrowIfCancellationRequested();
        using var measurementCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? priorCts = Interlocked.CompareExchange(
            ref _indexStorageMeasurementCts,
            measurementCts,
            comparand: null);
        if (priorCts is not null)
            return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;

        try
        {
            // Close the check/register race: a search that began just before registration could not cancel
            // this CTS, while any search beginning after registration will cancel it from the change hook.
            if (IsSearching)
            {
                measurementCts.Cancel();
                return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;
            }

            IndexStorageSizeMeasurement measurement = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                indexRoot,
                backend,
                measurementCts.Token);
            measurementCts.Token.ThrowIfCancellationRequested();
            _cachedIndexStorageSize = measurement;
            _cachedIndexStorageRoot = indexRoot;
            _cachedIndexStorageBackend = backend;
            _nextIndexStorageSizeRefreshUtc = now + IndexStorageSizeRefreshInterval;
            _hasCachedIndexStorageSize = true;
            return measurement;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && measurementCts.IsCancellationRequested)
        {
            return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _indexStorageMeasurementCts, null, measurementCts);
        }
    }

    private void CancelIndexStorageMeasurement()
    {
        try { Volatile.Read(ref _indexStorageMeasurementCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private string BuildCancelledStatus(TimeSpan elapsed)
    {
        var time = FormatElapsed(elapsed);
        var rate = FormatThroughput(FilesScanned, _bytesScanned, elapsed);
        return $"Cancelled — {MatchesFound:N0} matches, {FilesScanned:N0} files processed ({time}, {rate})";
    }

    private CancellationTokenSource? _searchPrepareCts;

    /// <summary>True once the user has requested cancellation of the in-progress pre-search preparation
    /// (Cancel clicked while the pre-scan gates are still running, before <see cref="IsSearching"/> flips).</summary>
    public bool IsSearchPreparationCancellationRequested => _searchPrepareCts?.IsCancellationRequested == true;

    /// <summary>Marks the start of the pre-search preparation phase (semantic offers + warning gates), so
    /// the Cancel button and an indeterminate progress bar appear immediately instead of after the
    /// multi-second gate work. Returns a token that <see cref="CancelSearchPreparation"/> cancels.</summary>
    public CancellationToken BeginSearchPreparation()
    {
        _searchPrepareCts?.Dispose();
        _searchPrepareCts = new CancellationTokenSource();
        IsPreparingSearch = true;
        return _searchPrepareCts.Token;
    }

    /// <summary>Ends the preparation phase (the scan committed, or a gate aborted the run). Clears the
    /// "Canceling.." state only when no file scan is actually running.</summary>
    public void EndSearchPreparation()
    {
        IsPreparingSearch = false;
        if (!IsSearching) IsCancelling = false;
        _searchPrepareCts?.Dispose();
        _searchPrepareCts = null;
    }

    /// <summary>Requests cancellation of the in-progress preparation (Cancel clicked before the scan
    /// starts). Shows the disabled "Canceling.." state; the gate phase aborts at its next checkpoint.</summary>
    public void CancelSearchPreparation()
    {
        if (!IsPreparingSearch) return;
        IsCancelling = true;
        try { _searchPrepareCts?.Cancel(); }
        catch (Exception ex) { YaguLog.For("Search").LogWarning(ex, "Cancel preparation failed"); }
    }

    [RelayCommand]
    public Task CancelAsync()
    {
        // Only flip into the "Canceling.." state when there's actually a run to cancel — CancelAsync is
        // also called on session load/close where nothing is in flight.
        if (IsSearching) IsCancelling = true;
        try { _cts?.Cancel(); } catch (Exception ex) { YaguLog.For("Search").LogWarning(ex, "Cancel failed"); }
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
        catch (Exception ex) { YaguLog.For("Clipboard").LogDebug(ex, "Clipboard unavailable"); }
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
            YaguLog.For("ViewModel").LogWarning(
                "Pre-evicted {Evicted:N0}/{Total:N0} new result payload(s) before UI insertion in {ElapsedMs}ms",
                evicted, results.Count, sw.ElapsedMilliseconds);
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
        // Tag the file's content-index candidacy provenance for the results-list badge (plan §6.2), if the
        // index participated in this search. Read-only + fast (a dict lookup per captured gate); safe on
        // the UI thread concurrently with the discovery loop.
        TrySetIndexProvenance(group);

        // Load metadata on a worker thread — the FileInfo syscall on the UI
        // dispatcher was a measurable stall on searches with thousands of
        // distinct files.
        group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()), OnResultGroupMetadataLoaded, _metadataCts.Token);
    }

    /// <summary>
    /// Sets <see cref="FileGroup.Provenance"/> from the captured per-root pruning gates (plan §6.2). Only
    /// runs when the master feature and the provenance setting are on and at least one gate accelerated
    /// this search; a file the index selected as a candidate is tagged index-accelerated, everything else
    /// live-scanned. Never throws — a classification failure just leaves the group unbadged.
    /// </summary>
    private void TrySetIndexProvenance(FileGroup group)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexProvenanceInResults)
            return;

        Yagu.Services.Index.ContentIndexSearchGate[] gates;
        Yagu.Services.Index.IContentIndexPruningScan[] pruningScans;
        lock (_indexGatesLock)
        {
            if (_activeIndexGates.Count == 0 && _activePruningScans.Count == 0)
                return;
            gates = _activeIndexGates.ToArray();
            pruningScans = _activePruningScans.ToArray();
        }

        try
        {
            string normalized = Yagu.Services.Index.IndexScopeIdentity.NormalizePath(group.FilePath);
            var provenance = Yagu.Services.Index.IndexProvenanceKind.LiveScanned;
            foreach (var gate in gates)
            {
                if (gate.ClassifyProvenance(normalized) == Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated)
                {
                    provenance = Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated;
                    break;
                }
            }
            // Stage-5 worker pruning path: a file the worker classified as an index member is badged too.
            if (provenance != Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated)
            {
                foreach (var scan in pruningScans)
                {
                    if (scan.WasIndexMember(normalized))
                    {
                        provenance = Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated;
                        break;
                    }
                }
            }
            group.Provenance = provenance;
        }
        catch
        {
            // Provenance is a cosmetic hint — never let a classification error affect results.
        }
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
                YaguLog.For("ViewModel").LogDebug(
                    "Deferring periodic in-search sort refresh for degraded large result set: {Groups:N0} group(s); final refresh will run on completion",
                    groupCount);
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
                YaguLog.For("ViewModel").LogWarning("Periodic in-search sort refresh threw: {ExceptionType}: {Error}", ex.GetType().Name, ex.Message);
                return;
            }
            sw.Stop();
            YaguLog.For("ViewModel").LogDebug(
                "Periodic in-search sort refresh: {Groups:N0} group(s) in {ElapsedMs}ms (degraded={Degraded}, nextInterval={NextIntervalSec:F1}s)",
                currentGroupCount, sw.ElapsedMilliseconds, Degraded, _searchSortRefreshIntervalSec);

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
        YaguLog.For("ViewModel").LogInformation("Evicted {Evicted:N0} results to disk ({TotalOnDisk:N0} total on disk)", evicted, _resultStore?.EvictedCount ?? 0);
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
            YaguLog.For("ViewModel").LogWarning(ex, "Post-eviction compacting GC failed");
        }
        finally
        {
            gcStopwatch.Stop();
            Volatile.Write(ref s_lastPostEvictionCompactingGcTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref s_postEvictionCompactingGcInFlight, 0);

            if (gcStopwatch.ElapsedMilliseconds >= 500)
                YaguLog.For("ViewModel").LogWarning("Post-eviction compacting GC took {ElapsedMs:N0}ms", gcStopwatch.ElapsedMilliseconds);
            else
                YaguLog.For("ViewModel").LogInformation("Post-eviction compacting GC took {ElapsedMs:N0}ms", gcStopwatch.ElapsedMilliseconds);
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
                YaguLog.For("ViewModel").LogWarning("Could not hydrate result at offset {Offset}: {Error}", result.DiskOffset, ex.Message);
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
            YaguLog.For("ViewModel").LogWarning("Batch hydration failed: {Error}", ex.Message);
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
            YaguLog.For("ViewModel").LogWarning("Source-backed hydration failed for '{File}': {Error}", result.FilePath, ex.Message);
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
            return $"{s.TotalMatches:N0} matches in {s.FilesWithMatches:N0} files ({time}, {rate})";
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

        string? sourcePhase = _sourceBackedSearchProgress?.BuildPhaseLabel(FilesScanned, TotalFiles);
        string phaseSuffix = sourcePhase is null ? string.Empty : $" — {sourcePhase}";
        StatusText = $"{MatchesFound:N0} matches in {filesWithMatches:N0} files ({FormatElapsed(_searchTimer.Elapsed)}, {_instantFilesPerSec:N1} files/sec){phaseSuffix}";

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
        _settings.AutocompleteDropdownVisibleItems = AutocompleteDropdownVisibleItems;
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
        _settings.ShowResourceUsageInStatusBar = ShowResourceUsageInStatusBar;
        _settings.ShowBuildNumberInTitleBar = ShowBuildNumberInTitleBar;
        _settings.ShowAutoScrollResultsCheckbox = ShowAutoScrollResultsCheckbox;
        _settings.SdkChannelBufferSize = SdkChannelBufferSize;
        _settings.MaxMatchesPerFile = MaxMatchesPerFile;
        _settings.MaxMatchesPerLine = MaxMatchesPerLine < 0 ? 0 : MaxMatchesPerLine;
        _settings.FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(FileIoTimeoutSeconds);
        _settings.AbsoluteMaxResults = AbsoluteMaxResults < 0 ? 0 : AbsoluteMaxResults;
        _settings.SkipBinary = d is null ? SkipBinary : d.SkipBinary;
        _settings.SearchOnlineOnlyFiles = SearchOnlineOnlyFiles;
        _settings.SearchHiddenFiles = d is null ? SearchHiddenFiles : d.SearchHiddenFiles;
        _settings.SearchImageText = d is null ? SearchImageText : d.SearchImageText;
        _settings.SearchPdfText = SearchPdfText;
        _settings.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine);
        _settings.ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel);
        _settings.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide);
        _settings.ImageOcrWorkerParallelism = AppSettings.NormalizeImageOcrWorkerParallelism(ImageOcrWorkerParallelism);
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
        _settings.SuppressEverythingIndexCoverageWarning = SuppressEverythingIndexCoverageWarning;
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
        _settings.MaxSelectedFilesPerPreview = MaxSelectedFilesPerPreview;
        _settings.MaxSelectedResultsPerPreview = MaxSelectedResultsPerPreview;
        _settings.MaxRenderedMatchesPerSection = MaxRenderedMatchesPerSection;
        _settings.FullFilePreviewLimitMB = FullFilePreviewLimitMB;
        _settings.FullFilePreviewMaxRenderLines = FullFilePreviewMaxRenderLines;
        _settings.FullFilePreviewMaxRenderChars = FullFilePreviewMaxRenderChars;
        _settings.ArchiveMaxNestingDepth = ArchiveMaxNestingDepth;
        _settings.ArchiveMaxEntryMB = ArchiveMaxEntryMB;

        Helpers.LineTruncator.TruncatedLength = LineTruncationLength;

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        YaguLog.For("Settings").LogInformation("Settings persisted");
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
