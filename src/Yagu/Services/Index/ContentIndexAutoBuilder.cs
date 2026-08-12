using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>Result of an automatic build pass: how many roots were built, skipped (already indexed), or failed.</summary>
public readonly record struct AutoBuildResult(int Built, int Skipped, int Failed)
{
    /// <summary>Total roots considered.</summary>
    public int Total => Built + Skipped + Failed;
}

/// <summary>
/// Decides which registered roots are due for an automatic build (plan §6.1 <c>IndexBuildTrigger</c>).
/// Pure and side-effect free so the decision is unit-tested; the actual building is done by
/// <see cref="ContentIndexAutoBuilder"/>. Auto-build is strictly opt-in: it returns nothing unless the
/// master feature is on <em>and</em> a non-manual trigger is selected, so the default configuration
/// never builds anything unexpectedly.
/// </summary>
public static class ContentIndexBuildScheduler
{
    public const string TriggerManual = "Manual";
    public const string TriggerWhenEnabled = "WhenEnabled";
    public const string TriggerAtStartup = "AtStartup";
    public const string TriggerWhenIdle = "WhenIdle";
    public const string TriggerContinuous = AppSettings.IndexBuildTriggerContinuous;
    public const string TriggerOnSchedule = "OnSchedule";

    /// <summary>True when <paramref name="trigger"/> is a non-manual (automatic) build trigger.</summary>
    public static bool IsAutomaticTrigger(string? trigger)
        => !string.IsNullOrWhiteSpace(trigger)
           && !string.Equals(trigger.Trim(), TriggerManual, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The registered roots to build at application startup — the normalized <c>IndexedRoots</c> when the
    /// master feature is on and the trigger is <c>AtStartup</c>; otherwise empty (nothing auto-builds).
    /// </summary>
    public static IReadOnlyList<string> RootsDueAtStartup(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableContentIndex)
            return Array.Empty<string>();
        if (!AppSettings.IndexBuildTriggerHas(settings.IndexBuildTrigger, TriggerAtStartup))
            return Array.Empty<string>();
        return IndexedRootsPolicy.Normalize(settings.IndexedRoots);
    }

    /// <summary>
    /// The registered roots eligible for a scheduled build pass — the normalized <c>IndexedRoots</c> when the
    /// master feature is on and the trigger is <c>OnSchedule</c>; otherwise empty. Whether a pass is actually
    /// <em>due</em> right now is decided by <see cref="ContentIndexScheduleEvaluator"/>.
    /// </summary>
    public static IReadOnlyList<string> RootsForScheduledBuild(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableContentIndex)
            return Array.Empty<string>();
        if (!AppSettings.IndexBuildTriggerHas(settings.IndexBuildTrigger, TriggerOnSchedule))
            return Array.Empty<string>();
        return IndexedRootsPolicy.Normalize(settings.IndexedRoots);
    }

    /// <summary>The registered roots eligible for an idle-style maintenance pass — the normalized
    /// <c>IndexedRoots</c> only when the master feature and either <c>WhenIdle</c> or
    /// <c>Continuous</c> trigger are enabled. Continuous uses its own repeat interval instead of the
    /// WhenIdle no-input delay; the normal update mode and battery/search/disk/pause safeguards remain
    /// authoritative.</summary>
    public static IReadOnlyList<string> RootsForIdleBuild(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableContentIndex)
            return Array.Empty<string>();
        if (!AppSettings.IndexBuildTriggerHas(settings.IndexBuildTrigger, TriggerWhenIdle)
            && !AppSettings.IndexBuildTriggerHas(settings.IndexBuildTrigger, TriggerContinuous))
            return Array.Empty<string>();
        return IndexedRootsPolicy.Normalize(settings.IndexedRoots);
    }

    /// <summary>The triggers that fire a build pass <em>repeatedly</em> over the life of an install. Unlike
    /// <c>WhenEnabled</c> (a one-shot on enabling the feature), these are the ones a user picks meaning
    /// "keep my indexes current", so they are the ones that need an automatic update mode to do anything
    /// beyond creating missing indexes.</summary>
    private static readonly string[] RecurringMaintenanceTriggers =
        { TriggerAtStartup, TriggerWhenIdle, TriggerContinuous, TriggerOnSchedule };

    /// <summary>True when <paramref name="buildTrigger"/> selects at least one recurring maintenance
    /// trigger (AtStartup / WhenIdle / Continuous / OnSchedule).</summary>
    public static bool HasRecurringMaintenanceTrigger(string? buildTrigger)
    {
        foreach (string flag in RecurringMaintenanceTriggers)
        {
            if (AppSettings.IndexBuildTriggerHas(buildTrigger, flag))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The update mode to preselect for <paramref name="buildTrigger"/>. A recurring maintenance trigger
    /// paired with the default <c>ManualFullRebuild</c> is a footgun: the pass wakes on schedule but only
    /// ever creates <em>missing</em> indexes, so existing ones go stale and searches silently fall back to a
    /// live scan (watcher hints are gated off entirely in that mode). Recommend <c>AutomaticIncremental</c>
    /// in that case. An update mode the user already moved off the default is always preserved, and a
    /// manual-only trigger never escalates the mode. Pure so it is unit-tested and shared by the GUI
    /// onboarding dialog and the CLI first-run prompt.
    /// </summary>
    public static string RecommendedUpdateMode(string? buildTrigger, string? currentUpdateMode)
    {
        string current = AppSettings.NormalizeIndexUpdateMode(currentUpdateMode);
        if (!string.Equals(current, AppSettings.DefaultIndexUpdateMode, StringComparison.Ordinal))
            return current; // the user already chose an automatic mode — never override it
        return HasRecurringMaintenanceTrigger(buildTrigger)
            ? AppSettings.IndexUpdateModeAutomaticIncremental
            : current;
    }

    /// <summary>
    /// True for the misconfiguration <see cref="RecommendedUpdateMode"/> exists to prevent: a recurring
    /// maintenance trigger is selected while the update mode is still <c>ManualFullRebuild</c>, so automatic
    /// passes never refresh an existing index. Surfaced as an inline warning in Settings ▸ Indexing.
    /// </summary>
    public static bool IsStaleAutomaticCombination(string? buildTrigger, string? updateMode)
        => HasRecurringMaintenanceTrigger(buildTrigger)
           && string.Equals(
               AppSettings.NormalizeIndexUpdateMode(updateMode),
               AppSettings.DefaultIndexUpdateMode,
               StringComparison.Ordinal);

    /// <summary>
    /// Whether an automatic build pass should pause right now given live power/foreground/disk state
    /// (plan §6.1): paused when the machine is on battery and <c>IndexPauseOnBattery</c> is set, while a
    /// foreground search is active and <c>IndexPauseDuringForegroundSearch</c> is set, or when the index
    /// drive has less than <c>IndexMinimumFreeSpaceMB</c> free (so an unattended build never fills the
    /// disk). Pure so the decision is unit-tested; the caller supplies the current
    /// <paramref name="onBattery"/>, <paramref name="foregroundSearchActive"/>, and
    /// <paramref name="indexDriveFreeSpaceMb"/> state (a negative free-space value means "unknown" and
    /// never pauses — fail open).
    /// </summary>
    public static bool ShouldPauseAutoBuild(
        AppSettings settings, bool onBattery, bool foregroundSearchActive, long indexDriveFreeSpaceMb)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.IndexPauseOnBattery && onBattery)
            return true;
        if (settings.IndexPauseDuringForegroundSearch && foregroundSearchActive)
            return true;
        if (settings.IndexMinimumFreeSpaceMB > 0
            && indexDriveFreeSpaceMb >= 0
            && indexDriveFreeSpaceMb < settings.IndexMinimumFreeSpaceMB)
            return true;
        return false;
    }
}

/// <summary>
/// Builds registered roots using the shared managed <see cref="ContentIndexManager"/> (plan §6.1/§6.2).
/// It is a background helper: it never blocks the UI, swallows per-root errors (a bad root can't abort the
/// pass), honors cancellation, and <b>skips roots that already have a fresh published generation</b> so a
/// startup pass never rebuilds everything every launch. In <c>AutomaticFullRebuildWhenDirty</c> mode it
/// additionally rebuilds a root whose change journal proves it changed since the index was built
/// (incremental delta updates remain a Phase 3 step). Publishing an index never changes any search
/// result — it only lets a later opted-in search prune candidates.
/// </summary>
public sealed class ContentIndexAutoBuilder
{
    private readonly IContentIndexPathProvider _paths;
    private readonly int _retainedGenerations;
    private readonly int _maxDiskUsagePercent;

    public ContentIndexAutoBuilder(IContentIndexPathProvider pathProvider, int retainedGenerations = 2, int maxDiskUsagePercent = 0)
    {
        _paths = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        _retainedGenerations = Math.Max(1, retainedGenerations);
        _maxDiskUsagePercent = Math.Max(0, maxDiskUsagePercent);
    }

    /// <summary>
    /// Builds each root in <paramref name="roots"/> that does not already have a current index. Missing
    /// directories and per-root failures are counted, not thrown; only cancellation propagates.
    /// </summary>
    public AutoBuildResult BuildMissing(IReadOnlyList<string> roots, IndexIngestionPolicy policy, CancellationToken cancellationToken = default)
        => BuildDue(roots, policy, rebuildWhenDirty: false, journalReader: null, cancellationToken);
    /// <summary>
    /// Builds each root that has no current index and, when <paramref name="rebuildWhenDirty"/> is true
    /// (<c>AutomaticFullRebuildWhenDirty</c>), rebuilds each indexed root the change journal proves is
    /// dirty (plan §6.1). A rebuilt root counts toward <see cref="AutoBuildResult.Built"/>; a fresh indexed
    /// root is skipped. Missing directories and per-root failures are counted, not thrown; only
    /// cancellation propagates. The journal reader is injectable for testing.
    /// </summary>
    public AutoBuildResult BuildDue(
        IReadOnlyList<string> roots,
        IndexIngestionPolicy policy,
        bool rebuildWhenDirty,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader = null,
        CancellationToken cancellationToken = default,
        Func<string, IndexIngestionPolicy>? policyForRoot = null,
        Action<string, int>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(policy);

        int built = 0, skipped = 0, failed = 0;
        var manager = new ContentIndexManager(_paths, _retainedGenerations);
        var driveUsedByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        YaguLog.For("ContentIndex").LogInformation("Auto-build pass starting over {RootCount} root(s) (rebuildWhenDirty={RebuildWhenDirty}).", roots.Count, rebuildWhenDirty);

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (manager.HasCurrentIndex(root)
                    && !(rebuildWhenDirty && manager.IsScopeStale(root, journalReader)))
                {
                    // Already indexed and either fresh or we're not rebuilding dirty roots this pass.
                    skipped++;
                    YaguLog.For("ContentIndex").LogDebug("Auto-build: '{Root}' already indexed and fresh — skipped.", root);
                    continue;
                }
                if (!Directory.Exists(root))
                {
                    failed++;
                    YaguLog.For("ContentIndex").LogWarning("Auto-build: root '{Root}' does not exist — counted as failed.", root);
                    continue;
                }
                YaguLog.For("ContentIndex").LogInformation("Auto-build: building '{Root}'.", root);
                onProgress?.Invoke(root, -1);
                manager.BuildScope(root, policyForRoot?.Invoke(root) ?? policy, cancellationToken, 0, _maxDiskUsagePercent,
                    progress: WrapBuildProgress(root, onProgress, driveUsedByRoot));
                built++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidDataException)
            {
                failed++;
                YaguLog.For("ContentIndex").LogWarning(ex, "Auto-build: root '{Root}' failed (counted, not fatal).", root);
            }
        }

        YaguLog.For("ContentIndex").LogInformation("Auto-build pass complete: {Built} built, {Skipped} skipped, {Failed} failed.", built, skipped, failed);
        return new AutoBuildResult(built, skipped, failed);
    }

    /// <summary>
    /// Wraps <see cref="ContentIndexManager.BuildScope"/>'s byte-crawl progress into the unified
    /// <c>(root, percent)</c> callback, caching each root's drive-used denominator so the drive is stat'd
    /// once per pass rather than on every progress tick.
    /// </summary>
    private static Action<IndexBuildProgress>? WrapBuildProgress(
        string root, Action<string, int>? onProgress, Dictionary<string, long> driveUsedByRoot)
    {
        if (onProgress is null)
            return null;
        return p =>
        {
            if (!driveUsedByRoot.TryGetValue(root, out long used))
                driveUsedByRoot[root] = used = IndexBuildProgressEstimate.DriveUsedBytes(root);
            onProgress(root, IndexBuildProgressEstimate.Percent(p.BytesCrawled, used));
        };
    }

    /// <summary>
    /// The <c>AutomaticIncremental</c> pass (plan §11.4): builds each root that has no index, skips fresh
    /// roots, and for a <b>stale</b> indexed root applies an incremental delta refresh
    /// (<see cref="ContentIndexIncrementalRefresher"/>) instead of a full rebuild — falling back to a full
    /// rebuild when the journal is discontinuous or the base is lost
    /// (<see cref="IncrementalUpdateOutcome.NeedsFullRebuild"/>). Every dependency is injected so the pass is
    /// unit-testable without a volume; production wires the USN journal, a <see cref="FileIdPathResolver"/>
    /// factory, <see cref="ContentIndexIncrementalUpdater.CreateFileReadClassifier"/> (the optimized content
    /// reader), and <see cref="FileIdentityReader.TryGetIdentity"/>. Per-root failures are counted, not
    /// thrown; only cancellation propagates.
    /// </summary>
    public AutoBuildResult RefreshIncremental(
        IReadOnlyList<string> roots,
        IndexIngestionPolicy policy,
        AppSettings settings,
        ContentIndexFreshnessEvaluator.JournalReader journalReader,
        Func<string, IFileIdPathResolver?> resolverFactory,
        Func<string, IncrementalFileRead?> readAndClassify,
        Func<string, FileIdentity?>? identityProvider = null,
        CancellationToken cancellationToken = default,
        Func<string, IndexIngestionPolicy>? policyForRoot = null,
        Action<string, int>? onProgress = null,
        Func<string, UsnJournalInfo?>? journalInfoProvider = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(journalReader);
        ArgumentNullException.ThrowIfNull(resolverFactory);
        ArgumentNullException.ThrowIfNull(readAndClassify);

        int built = 0, skipped = 0, failed = 0;
        var manager = new ContentIndexManager(_paths, _retainedGenerations);
        var driveUsedByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh pass starting over {RootCount} root(s).", roots.Count);

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool exists = manager.HasCurrentIndex(root);
                if (exists && !manager.IsScopeStale(root, journalReader))
                {
                    // A fresh (unchanging) root is normally skipped. But if it has accumulated too many
                    // delta segments, compact it into a single base now — otherwise every query keeps
                    // loading base + N segments into memory (the source of the multi-GB working set on a
                    // large drive). Compaction also re-enables the base-only worker query path.
                    if (manager.CompactScopeIfOverSegmented(root, policyForRoot?.Invoke(root) ?? policy, settings, DateTimeOffset.UtcNow))
                    {
                        built++;
                        YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh: '{Root}' fresh but over-segmented — compacted into a fresh base.", root);
                        continue;
                    }

                    // A fresh (unchanging) root's checkpoint still ages toward USN-journal wrap because
                    // FirstUsn advances with ALL volume activity, not just writes under the root. When it
                    // nears the wrap, proactively re-anchor the checkpoint (while the journal is still
                    // continuous) so a future search never bypasses the index (JournalDiscontinuity).
                    if (TryProactiveReanchor(manager, root, journalReader, journalInfoProvider))
                    {
                        built++;
                        YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh: '{Root}' checkpoint re-anchored ahead of USN-journal wrap.", root);
                        continue;
                    }

                    skipped++;
                    YaguLog.For("ContentIndex").LogDebug("Incremental auto-refresh: '{Root}' fresh — skipped.", root);
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    failed++;
                    YaguLog.For("ContentIndex").LogWarning("Incremental auto-refresh: root '{Root}' does not exist — counted as failed.", root);
                    continue;
                }

                if (!exists)
                {
                    // No index yet → full build.
                    onProgress?.Invoke(root, -1);
                    YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh: '{Root}' has no index — full build.", root);
                    manager.BuildScope(root, policyForRoot?.Invoke(root) ?? policy, cancellationToken, 0, _maxDiskUsagePercent,
                        progress: WrapBuildProgress(root, onProgress, driveUsedByRoot));
                    built++;
                    continue;
                }

                // Stale indexed root → incremental refresh, falling back to a full rebuild when unsafe.
                onProgress?.Invoke(root, -1); // name the folder in the indicator; the refresh reports percents
                string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
                var refresher = new ContentIndexIncrementalRefresher(
                    store, policyForRoot?.Invoke(root) ?? policy, _paths.IndexRoot,
                    journalReader, resolverFactory, readAndClassify, identityProvider);
                IncrementalUpdateOutcome outcome = refresher.Refresh(scopeId, settings, DateTimeOffset.UtcNow,
                    onProgress is null ? null : (Action<int, string>)((pct, _) => onProgress(root, pct)));
                YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh: '{Root}' stale → refresh outcome {Outcome}.", root, outcome);

                switch (outcome)
                {
                    case IncrementalUpdateOutcome.SegmentAppended:
                    case IncrementalUpdateOutcome.Compacted:
                        built++;
                        break;
                    case IncrementalUpdateOutcome.NoChanges:
                        skipped++;
                        break;
                    case IncrementalUpdateOutcome.SizeBudgetReached:
                        // A deliberate storage-budget halt, not a broken index — never rebuild from here.
                        skipped++;
                        break;
                    default: // NeedsFullRebuild
                        YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh: '{Root}' needs a full rebuild — rebuilding.", root);
                        manager.BuildScope(root, policyForRoot?.Invoke(root) ?? policy, cancellationToken, 0, _maxDiskUsagePercent,
                            progress: WrapBuildProgress(root, onProgress, driveUsedByRoot));
                        built++;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidDataException)
            {
                failed++;
                YaguLog.For("ContentIndex").LogWarning(ex, "Incremental auto-refresh: root '{Root}' failed (counted, not fatal).", root);
            }
        }

        YaguLog.For("ContentIndex").LogInformation("Incremental auto-refresh pass complete: {Built} built/refreshed, {Skipped} skipped, {Failed} failed.", built, skipped, failed);
        return new AutoBuildResult(built, skipped, failed);
    }

    /// <summary>
    /// For a FRESH (unchanging) root, checks whether its base checkpoint is nearing USN-journal wrap and, if
    /// so and the journal is still continuous, proactively re-anchors the checkpoint to the current journal
    /// position (a cheap manifest-only rewrite) so a future search never bypasses the index. A fresh root is
    /// normally skipped, but the volume's USN journal advances with ALL activity, so the checkpoint can be
    /// silently purged by wrap — after which every search over it bypasses (JournalDiscontinuity/GapDetected).
    /// Returns true when it re-anchored (the caller counts it as work done). When the checkpoint is already
    /// purged / the journal was recreated (too late to re-anchor cheaply) or the scope is segmented, it logs
    /// the near-wrap recommendation and returns false. A missing <paramref name="journalInfoProvider"/> (no
    /// live journal info) or a healthy checkpoint returns false silently. Reads only the base manifest
    /// checkpoint (cheap — no content.bin), mirroring <see cref="ContentIndexManager.IsScopeStale"/>. Never throws.
    /// </summary>
    private bool TryProactiveReanchor(
        ContentIndexManager manager,
        string root,
        ContentIndexFreshnessEvaluator.JournalReader journalReader,
        Func<string, UsnJournalInfo?>? journalInfoProvider)
    {
        if (journalInfoProvider is null)
            return false;
        try
        {
            string scopeId = ContentIndexManager.ScopeIdForRoot(root);
            var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
            if (store.TryReadCurrentFreshnessInputs() is not { } inputs)
                return false;
            if (journalInfoProvider(root) is not { } journal)
                return false;

            // The base manifest checkpoint is exactly what a search's B0 freshness barrier replays from, so
            // its headroom predicts whether the NEXT search over this root would bypass the index.
            UsnHeadroomVerdict verdict = UsnJournalHeadroom.Evaluate(journal, inputs.Manifest.FreshnessCheckpoint);
            if (!verdict.ShouldRefreshSoon)
                return false; // healthy — normal skip, no warning

            // Still continuous (not yet purged, same journal id) → advance the checkpoint now, cheaply.
            if (!verdict.CheckpointPurged && !verdict.JournalIdMismatch
                && manager.TryReanchorFreshScope(root, journalReader))
            {
                return true;
            }

            // Already purged / journal recreated / segmented scope → can't re-anchor cheaply; recommend it.
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental auto-refresh: '{Root}' index checkpoint is near USN-journal wrap "
                + "(headroom={Headroom:P0}, purged={Purged}, journalIdMismatch={Mismatch}); a future search may "
                + "bypass the index — recommend a proactive re-anchor.",
                root, verdict.SurvivalFraction, verdict.CheckpointPurged, verdict.JournalIdMismatch);
            return false;
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "Incremental auto-refresh: proactive re-anchor check failed for '{Root}' (non-fatal).", root);
            return false;
        }
    }
}
