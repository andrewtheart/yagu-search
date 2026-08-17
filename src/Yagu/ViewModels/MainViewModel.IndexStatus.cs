using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services.Index;

namespace Yagu.ViewModels;

/// <summary>
/// Content-index status surface: the status text/glyph/tooltip shown next to the search box, the
/// all-drive index health snapshot, index warm-up and build activity state, and the blocking
/// rebuild overlay.
/// </summary>
public sealed partial class MainViewModel
{
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

    /// <summary>Shows the green check beside the status glyph whenever the label reports unqualified
    /// success — every maintained index healthy, the running search fully accelerated, a finished search
    /// fully accelerated, or the index built/warmed and ready. Driven by the single allow-list in
    /// <see cref="ContentIndexUiStatus.IsFullSuccessLabel"/> so that adding a success state is one edit
    /// and a qualified variant (for example "Index: accelerating (1 of 4 needs attention)") can never
    /// pick up the check.</summary>
    public Microsoft.UI.Xaml.Visibility IndexHealthyCheckVisibility =>
        ContentIndexUiStatus.IsFullSuccessLabel(IndexStatusText)
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

    // Which phase of an incremental update is running (see IndexUpdateStages), or null when unphased.
    private string? _activeIndexBuildPhase;

    /// <summary>The canonical stage currently reported by the active build/update worker.</summary>
    public string? ActiveIndexBuildStage => _activeIndexBuildPhase;

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
            if (_shutdownRequested)
                return new CancellationToken(canceled: true);
            _indexBuildCancellation ??= new CancellationTokenSource();
            return _indexBuildCancellation.Token;
        }
    }

    partial void OnIsIndexingPausedChanged(bool value) => OnPropertyChanged(nameof(CanPauseIndexing));
}
