using System.Text;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the automatic-build scheduler + builder (plan §6.1/§6.2): the pure
/// <see cref="ContentIndexBuildScheduler"/> decision (opt-in, gated by master + trigger) and the
/// <see cref="ContentIndexAutoBuilder"/> that builds only the roots without a current index. Runs under a
/// per-test temp sandbox (§9.2).
/// </summary>
public sealed class ContentIndexAutoBuilderTests : IDisposable
{
    private readonly string _sandbox;

    public ContentIndexAutoBuilderTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-auto", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    // ── Scheduler (pure) ──

    [Fact]
    public void RootsDueAtStartup_MasterOff_ReturnsEmpty()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = false,
            IndexBuildTrigger = "AtStartup",
            IndexedRoots = new List<string> { @"C:\a" },
        };
        Assert.Empty(ContentIndexBuildScheduler.RootsDueAtStartup(settings));
    }

    [Fact]
    public void RootsDueAtStartup_ManualTrigger_ReturnsEmpty()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = true,
            IndexBuildTrigger = "Manual",
            IndexedRoots = new List<string> { @"C:\a" },
        };
        Assert.Empty(ContentIndexBuildScheduler.RootsDueAtStartup(settings));
    }

    [Fact]
    public void RootsDueAtStartup_AtStartupWithRoots_ReturnsNormalizedRoots()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = true,
            IndexBuildTrigger = "AtStartup",
            IndexedRoots = new List<string> { @"C:\a", @"c:\a\", @"D:\b" },
        };
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsDueAtStartup(settings));
    }

    [Fact]
    public void RootsForScheduledBuild_OnlyForOnScheduleTriggerWithMasterOn()
    {
        var roots = new List<string> { @"C:\a", @"c:\a\", @"D:\b" };

        // Master off → empty even when trigger is OnSchedule.
        Assert.Empty(ContentIndexBuildScheduler.RootsForScheduledBuild(
            new AppSettings { EnableContentIndex = false, IndexBuildTrigger = "OnSchedule", IndexedRoots = roots }));

        // A non-OnSchedule trigger → empty.
        Assert.Empty(ContentIndexBuildScheduler.RootsForScheduledBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "AtStartup", IndexedRoots = roots }));

        // OnSchedule + master on → the normalized (deduped) registered roots.
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsForScheduledBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "OnSchedule", IndexedRoots = roots }));
    }

    [Fact]
    public void RootsForIdleBuild_AcceptsWhenIdleOrContinuousWithMasterOn()
    {
        var roots = new List<string> { @"C:\a", @"D:\b", @"c:\A\" };
        Assert.Empty(ContentIndexBuildScheduler.RootsForIdleBuild(
            new AppSettings { EnableContentIndex = false, IndexBuildTrigger = "WhenIdle", IndexedRoots = roots }));
        Assert.Empty(ContentIndexBuildScheduler.RootsForIdleBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "AtStartup", IndexedRoots = roots }));
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsForIdleBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "WhenIdle", IndexedRoots = roots }));
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsForIdleBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "AtStartup, WhenIdle", IndexedRoots = roots }));
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsForIdleBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "Continuous", IndexedRoots = roots }));
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsForIdleBuild(
            new AppSettings { EnableContentIndex = true, IndexBuildTrigger = "WhenIdle, Continuous", IndexedRoots = roots }));
    }

    [Fact]
    public void CombinedTrigger_AtStartupAndOnSchedule_DrivesBothBuildPaths()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = true,
            IndexBuildTrigger = "AtStartup, OnSchedule",
            IndexedRoots = new List<string> { @"C:\a", @"D:\b" },
        };

        // Both the startup pass and the scheduled pass see the roots because both flags are active.
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsDueAtStartup(settings));
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, ContentIndexBuildScheduler.RootsForScheduledBuild(settings));
    }

    // ── Update-mode recommendation (a recurring trigger left on ManualFullRebuild never refreshes) ──

    [Theory]
    [InlineData("AtStartup", true)]
    [InlineData("WhenIdle", true)]
    [InlineData("Continuous", true)]
    [InlineData("OnSchedule", true)]
    [InlineData("WhenIdle, Continuous", true)]
    [InlineData("Manual", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // WhenEnabled is a one-shot on enabling the feature, not recurring maintenance.
    [InlineData("WhenEnabled", false)]
    public void HasRecurringMaintenanceTrigger_OnlyCountsRepeatingTriggers(string? trigger, bool expected)
        => Assert.Equal(expected, ContentIndexBuildScheduler.HasRecurringMaintenanceTrigger(trigger));

    [Theory]
    [InlineData("Continuous")]
    [InlineData("WhenIdle")]
    [InlineData("AtStartup")]
    [InlineData("OnSchedule")]
    public void RecommendedUpdateMode_UpgradesDefaultToIncrementalForARecurringTrigger(string trigger)
    {
        // The onboarding bug: picking "Continuously while Yagu is open" while the update mode stayed at
        // the default meant automatic passes only ever created MISSING indexes.
        Assert.Equal(
            AppSettings.IndexUpdateModeAutomaticIncremental,
            ContentIndexBuildScheduler.RecommendedUpdateMode(trigger, AppSettings.DefaultIndexUpdateMode));
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("")]
    [InlineData("WhenEnabled")]
    public void RecommendedUpdateMode_LeavesManualTriggersOnTheDefaultMode(string trigger)
        => Assert.Equal(
            AppSettings.DefaultIndexUpdateMode,
            ContentIndexBuildScheduler.RecommendedUpdateMode(trigger, AppSettings.DefaultIndexUpdateMode));

    [Theory]
    [InlineData(AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty)]
    [InlineData(AppSettings.IndexUpdateModeAutomaticIncremental)]
    public void RecommendedUpdateMode_NeverOverridesAModeTheUserAlreadyChose(string current)
        => Assert.Equal(current, ContentIndexBuildScheduler.RecommendedUpdateMode("Continuous", current));

    [Fact]
    public void RecommendedUpdateMode_NormalizesAnUnknownMode()
        => Assert.Equal(
            AppSettings.IndexUpdateModeAutomaticIncremental,
            ContentIndexBuildScheduler.RecommendedUpdateMode("Continuous", "not-a-mode"));

    [Theory]
    [InlineData("Continuous", AppSettings.DefaultIndexUpdateMode, true)]
    [InlineData("AtStartup, OnSchedule", AppSettings.DefaultIndexUpdateMode, true)]
    [InlineData("Continuous", AppSettings.IndexUpdateModeAutomaticIncremental, false)]
    [InlineData("Continuous", AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty, false)]
    [InlineData("Manual", AppSettings.DefaultIndexUpdateMode, false)]
    [InlineData("WhenEnabled", AppSettings.DefaultIndexUpdateMode, false)]
    public void IsStaleAutomaticCombination_FlagsARecurringTriggerLeftOnManualFullRebuild(
        string trigger, string mode, bool expected)
        => Assert.Equal(expected, ContentIndexBuildScheduler.IsStaleAutomaticCombination(trigger, mode));

    [Fact]
    public void CombinedTrigger_OnScheduleOnly_DoesNotBuildAtStartup()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = true,
            IndexBuildTrigger = "WhenIdle, OnSchedule",
            IndexedRoots = new List<string> { @"C:\a" },
        };

        Assert.Empty(ContentIndexBuildScheduler.RootsDueAtStartup(settings));
        Assert.Equal(new[] { @"C:\a" }, ContentIndexBuildScheduler.RootsForScheduledBuild(settings));
    }

    [Theory]
    [InlineData("Manual", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("AtStartup", true)]
    [InlineData("WhenIdle", true)]
    [InlineData("Continuous", true)]
    [InlineData("WhenEnabled", true)]
    public void IsAutomaticTrigger_ClassifiesTrigger(string? trigger, bool expected)
        => Assert.Equal(expected, ContentIndexBuildScheduler.IsAutomaticTrigger(trigger));

    [Theory]
    // (pauseOnBattery, pauseDuringSearch, minFreeMb, onBattery, searchActive, freeMb) → shouldPause
    [InlineData(true, true, 2048, false, false, 8000, false)]  // idle + AC + plenty of space → run
    [InlineData(true, true, 2048, true, false, 8000, true)]    // on battery + setting on → pause
    [InlineData(false, true, 2048, true, false, 8000, false)]  // on battery but setting off → run
    [InlineData(true, true, 2048, false, true, 8000, true)]    // search active + setting on → pause
    [InlineData(true, false, 2048, false, true, 8000, false)]  // search active but setting off → run
    [InlineData(false, false, 2048, true, true, 8000, false)]  // power/search settings off → those never pause
    [InlineData(true, true, 2048, false, false, 500, true)]    // low free space → pause
    [InlineData(true, true, 2048, false, false, -1, false)]    // unknown free space → fail open (run)
    [InlineData(true, true, 0, false, false, 10, false)]       // floor 0 → free-space check disabled
    public void ShouldPauseAutoBuild_HonorsPowerSearchAndDiskState(
        bool pauseOnBattery, bool pauseDuringSearch, int minFreeMb, bool onBattery, bool searchActive, long freeMb, bool expected)
    {
        var settings = new AppSettings
        {
            IndexPauseOnBattery = pauseOnBattery,
            IndexPauseDuringForegroundSearch = pauseDuringSearch,
            IndexMinimumFreeSpaceMB = minFreeMb,
        };
        Assert.Equal(expected, ContentIndexBuildScheduler.ShouldPauseAutoBuild(settings, onBattery, searchActive, freeMb));
    }

    // ── Auto-builder (integration) ──

    [Fact]
    public void BuildMissing_BuildsOnlyRootsWithoutAnIndex()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string rootA = Path.Combine(_sandbox, "a");
        string rootB = Path.Combine(_sandbox, "b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(rootA, "f.txt"), "alpha content", utf8);
        File.WriteAllText(Path.Combine(rootB, "f.txt"), "beta content", utf8);

        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);

        // Pre-build root A so the auto-builder must skip it.
        new ContentIndexManager(paths).BuildScope(rootA, policy);

        var builder = new ContentIndexAutoBuilder(paths);
        var result = builder.BuildMissing(new[] { rootA, rootB }, policy);

        Assert.Equal(1, result.Built);   // B
        Assert.Equal(1, result.Skipped); // A already indexed
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Total);

        // Both roots now have an index.
        Assert.True(new ContentIndexManager(paths).GetStatusForRoot(rootB).Exists);
    }

    [Fact]
    public void BuildMissing_MissingDirectory_CountsAsFailedNotThrows()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);

        var result = new ContentIndexAutoBuilder(paths)
            .BuildMissing(new[] { Path.Combine(_sandbox, "does-not-exist") }, policy);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void BuildMissing_Cancellation_Propagates()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new ContentIndexAutoBuilder(paths).BuildMissing(new[] { root }, policy, cts.Token));
    }

    // ── AutomaticFullRebuildWhenDirty (plan §6.1, V1) ──

    // Publishes a generation for `root` from `filePath` with a KNOWN synthetic identity + a fixed build
    // checkpoint (JournalId=1), so staleness is deterministic without depending on the machine's USN journal.
    private static UsnFileIdentity PublishFakeGeneration(
        IContentIndexPathProvider paths,
        string root,
        string filePath,
        ulong identityHigh = 0)
    {
        var id = new UsnFileIdentity(4242, identityHigh);
        FileIdentity? Provider(string _) => new FileIdentity(0x9, id);
        string scopeId = ContentIndexManager.ScopeIdForRoot(root);
        var builder = new ContentIndexGenerationBuilder(new IndexIngestionPolicy(0, null, null, true, false, 0), identityProvider: Provider);
        builder.AddDocument(filePath, Encoding.UTF8.GetBytes("indexed content here"));
        var gen = builder.Build(scopeId, "vol", root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(paths, scopeId).Publish(gen);
        return id;
    }

    private static ContentIndexFreshnessEvaluator.JournalReader FreshReader
        => (p, since) => new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), Array.Empty<UsnChange>());

    private static ContentIndexFreshnessEvaluator.JournalReader DirtyReaderFor(UsnFileIdentity id)
        => (p, since) => new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), new[] { new UsnChange(id, 0x1) });

    // Reads-and-classifies a changed file the way the optimized content reader would (Stage 6): the
    // incremental refresh path now takes classified content + identity instead of raw bytes.
    private static IncrementalFileRead? ClassifiedRead(string text, IndexIngestionPolicy policy)
        => new(IndexIngestionClassifier.ClassifyContent(Encoding.UTF8.GetBytes(text), policy), null);

    [Fact]
    public void IsScopeStale_NoIndex_IsFalse()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        // A root with no published index is "missing", not "stale".
        Assert.False(new ContentIndexManager(paths).IsScopeStale(Path.Combine(_sandbox, "nope"), FreshReader));
    }

    [Fact]
    public void IsScopeStale_ReflectsProvenJournalChanges()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);

        UsnFileIdentity id = PublishFakeGeneration(paths, root, file);
        var manager = new ContentIndexManager(paths);

        Assert.False(manager.IsScopeStale(root, FreshReader));       // no changes → fresh
        Assert.True(manager.IsScopeStale(root, DirtyReaderFor(id))); // journal proves a change → stale
    }

    [Fact]
    public void IsScopeStale_LegacyExtendedReFsIdentityWithV2Change_RequestsCompatibilityRebuild()
    {
        string indexRoot = Path.Combine(_sandbox, "legacy-refs-index");
        string root = Path.Combine(_sandbox, "legacy-refs-root");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        _ = PublishFakeGeneration(paths, root, file, identityHigh: 0x600);
        var manager = new ContentIndexManager(paths);
        ContentIndexFreshnessEvaluator.JournalReader v2Change = (_, since) => new UsnReadResult(
            UsnReadStatus.Ok,
            new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
            new[] { new UsnChange(new UsnFileIdentity(0x3000000000067600, 0), 0x1) });

        ContentIndexManager.ScopeFreshnessStatus status = manager.GetScopeFreshnessStatus(root, v2Change);

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Dirty, status.State);
        Assert.Equal(UsnReadStatus.IdentityMismatch, status.RawStatus);
        Assert.True(status.RequiresRebuild);
        Assert.True(status.NeedsUpdate);
        Assert.Equal(1, status.DirtyCount);
        Assert.True(manager.IsScopeStale(root, v2Change));
        Assert.Contains("older file-identity format", status.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetScopeFreshnessState_DistinguishesFreshDirtyAndIncomplete()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "tri-state");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        UsnFileIdentity id = PublishFakeGeneration(paths, root, file);
        var manager = new ContentIndexManager(paths);

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Fresh,
            manager.GetScopeFreshnessState(root, FreshReader));
        ContentIndexManager.ScopeFreshnessStatus dirty = manager.GetScopeFreshnessStatus(root, DirtyReaderFor(id));
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Dirty, dirty.State);
        Assert.Equal(1, dirty.DirtyCount);
        Assert.True(dirty.NeedsUpdate);
        Assert.False(dirty.NeedsAttention);
        ContentIndexManager.ScopeFreshnessStatus incomplete = manager.GetScopeFreshnessStatus(root, (p, since) =>
            new UsnReadResult(UsnReadStatus.Incomplete, since, Array.Empty<UsnChange>()));
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Uncertain, incomplete.State);
        Assert.Equal(UsnReadStatus.Incomplete, incomplete.RawStatus);
        Assert.True(incomplete.NeedsAttention);
        Assert.False(incomplete.RequiresRebuild);
        Assert.Contains("Increase the limit and update", incomplete.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Missing,
            manager.GetScopeFreshnessState(Path.Combine(_sandbox, "missing"), FreshReader));

        ContentIndexManager.ScopeFreshnessStatus future = manager.GetScopeFreshnessStatus(root, (p, since) =>
            new UsnReadResult(UsnReadStatus.CheckpointAhead, new UsnCheckpoint(since.JournalId, 10), Array.Empty<UsnChange>()));
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Uncertain, future.State);
        Assert.True(future.RequiresRebuild);
        Assert.Equal(UsnReadStatus.CheckpointAhead, future.RawStatus);
        Assert.Contains("ahead", future.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rebuild required", future.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetScopeFreshnessState_WholeDriveTreatsUnknownNewIdentityAsPendingMaintenance()
    {
        string indexRoot = Path.Combine(_sandbox, "whole-drive-index");
        string root = Path.GetPathRoot(_sandbox)!;
        string file = Path.Combine(_sandbox, "whole-drive-existing.txt");
        File.WriteAllText(file, "alpha", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        _ = PublishFakeGeneration(paths, root, file);
        var manager = new ContentIndexManager(paths);
        var newIdentity = new UsnFileIdentity(99_999, 0);

        ContentIndexManager.ScopeFreshnessStatus status = manager.GetScopeFreshnessStatus(
            root,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
                new[] { new UsnChange(newIdentity, 0x1) }));

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Dirty, status.State);
        Assert.Equal(1, status.DirtyCount);
        Assert.True(status.NeedsUpdate);
    }

    [Fact]
    public void GetScopeFreshnessState_SubfolderDoesNotClaimUnknownVolumeIdentityIsInScope()
    {
        string indexRoot = Path.Combine(_sandbox, "subfolder-index");
        string root = Path.Combine(_sandbox, "subfolder");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "existing.txt");
        File.WriteAllText(file, "alpha", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        _ = PublishFakeGeneration(paths, root, file);
        var manager = new ContentIndexManager(paths);

        ContentIndexManager.ScopeFreshnessStatus status = manager.GetScopeFreshnessStatus(
            root,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
                new[] { new UsnChange(new UsnFileIdentity(99_999, 0), 0x1) }));

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Fresh, status.State);
        Assert.Equal(0, status.DirtyCount);
    }

    [Theory]
    [InlineData("NTFS", true)]
    [InlineData("ntfs", true)]
    [InlineData("ReFS", true)]
    [InlineData("exFAT", false)]
    [InlineData("FAT32", false)]
    [InlineData("", false)]
    public void VolumeFormatSupportsChangeJournal_MatchesSupportedWindowsFilesystems(string format, bool expected)
    {
        Assert.Equal(expected, ContentIndexManager.VolumeFormatSupportsChangeJournal(format));
    }

    [Fact]
    public void BuildDue_RebuildWhenDirty_RebuildsProvenDirtyRoot_ButSkipsWhenFreshOrModeOff()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);

        UsnFileIdentity id = PublishFakeGeneration(paths, root, file);
        var builder = new ContentIndexAutoBuilder(paths);

        // Mode off → an existing index is skipped even though the (unused) journal would report changes.
        var modeOff = builder.BuildDue(new[] { root }, policy, rebuildWhenDirty: false, journalReader: DirtyReaderFor(id));
        Assert.Equal(0, modeOff.Built);
        Assert.Equal(1, modeOff.Skipped);

        // Mode on + fresh journal → not dirty → skip.
        var fresh = builder.BuildDue(new[] { root }, policy, rebuildWhenDirty: true, journalReader: FreshReader);
        Assert.Equal(0, fresh.Built);
        Assert.Equal(1, fresh.Skipped);

        // Mode on + proven dirty → full rebuild.
        var dirty = builder.BuildDue(new[] { root }, policy, rebuildWhenDirty: true, journalReader: DirtyReaderFor(id));
        Assert.Equal(1, dirty.Built);
        Assert.Equal(0, dirty.Skipped);
    }

    // ── AutomaticIncremental (plan §11.4) ──

    private sealed class MapResolver : IFileIdPathResolver
    {
        private readonly Dictionary<UsnFileIdentity, string?> _map;
        public MapResolver(Dictionary<UsnFileIdentity, string?> map) => _map = map;
        public string? TryResolvePath(UsnFileIdentity identity) => _map.TryGetValue(identity, out string? p) ? p : null;
    }

    [Fact]
    public void RefreshIncremental_FreshRoot_IsSkipped()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        PublishFakeGeneration(paths, root, file);

        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), FreshReader,
            _ => new MapResolver(new()), _ => null, FileIdentityReader.TryGetIdentity);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void RefreshIncremental_FreshBaseOnlyRoot_NearWrap_ReanchorsCheckpointInsteadOfSkipping()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        PublishFakeGeneration(paths, root, file); // base checkpoint (1,100), base-only

        // The base checkpoint (NextUsn=100) sits in the oldest sliver of the live journal window [90,1000) →
        // near wrap but still CONTINUOUS → a fresh root should re-anchor rather than be skipped-until-purged.
        static UsnJournalInfo? NearWrap(string _) => new UsnJournalInfo(1, 90, 1000, 0);

        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), FreshReader,
            _ => new MapResolver(new()), _ => null, FileIdentityReader.TryGetIdentity,
            journalInfoProvider: NearWrap);

        Assert.Equal(1, result.Built);   // re-anchored (counted as work), not skipped
        Assert.Equal(0, result.Skipped);

        // FreshReader advances the journal cursor to since.NextUsn+10 (=110), so the base checkpoint moved.
        var store = new ContentIndexStore(paths, ContentIndexManager.ScopeIdForRoot(root));
        Assert.Equal(new UsnCheckpoint(1, 110), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);
        Assert.Equal(0, store.ActiveSegmentCount()); // stayed a cheap base-only re-anchor (no segment added)
    }

    [Fact]
    public void RefreshIncremental_FreshRoot_HealthyJournal_SkipsWithoutReanchoring()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        PublishFakeGeneration(paths, root, file);

        // Checkpoint 100 has survived 50 of a 60-wide window (~83%) → healthy → nothing to do.
        static UsnJournalInfo? Healthy(string _) => new UsnJournalInfo(1, 50, 110, 0);

        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), FreshReader,
            _ => new MapResolver(new()), _ => null, FileIdentityReader.TryGetIdentity,
            journalInfoProvider: Healthy);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Skipped);
        var store = new ContentIndexStore(paths, ContentIndexManager.ScopeIdForRoot(root));
        Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);
    }

    [Fact]
    public void RefreshIncremental_FreshRoot_AlreadyPurged_SkipsWithoutReanchoring()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        PublishFakeGeneration(paths, root, file);

        // FirstUsn (200) has already climbed past the checkpoint (100) → purged → cannot cheaply re-anchor.
        static UsnJournalInfo? Purged(string _) => new UsnJournalInfo(1, 200, 1000, 0);

        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), FreshReader,
            _ => new MapResolver(new()), _ => null, FileIdentityReader.TryGetIdentity,
            journalInfoProvider: Purged);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Skipped);
        var store = new ContentIndexStore(paths, ContentIndexManager.ScopeIdForRoot(root));
        Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);
    }

    [Fact]
    public void RefreshIncremental_DirtyRoot_AppendsSegment()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        UsnFileIdentity id = PublishFakeGeneration(paths, root, file);

        var progressRoots = new List<string>();
        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), DirtyReaderFor(id),
            _ => new MapResolver(new() { [id] = file }),
            _ => ClassifiedRead("alpha CHANGED content", policy),
            FileIdentityReader.TryGetIdentity,
            onProgress: (r, _) => progressRoots.Add(r));

        Assert.Equal(1, result.Built);
        var store = new ContentIndexStore(paths, ContentIndexManager.ScopeIdForRoot(root));
        Assert.Equal(1, store.ActiveSegmentCount()); // incremental delta, not a full rebuild
        Assert.Contains(root, progressRoots);         // the pass names the folder it is indexing
    }

    [Fact]
    public void RefreshIncremental_MissingIndex_FullBuild()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "f.txt"), "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);

        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), FreshReader,
            _ => new MapResolver(new()), _ => null, FileIdentityReader.TryGetIdentity);

        Assert.Equal(1, result.Built); // no index yet → a normal full build
    }

    [Fact]
    public void RefreshIncremental_JournalGap_FallsBackToFullRebuild()
    {
        string indexRoot = Path.Combine(_sandbox, "index");
        string root = Path.Combine(_sandbox, "a");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "alpha content", new UTF8Encoding(false));
        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        UsnFileIdentity id = PublishFakeGeneration(paths, root, file);

        // A stale root whose journal is discontinuous → NeedsFullRebuild → full rebuild (no segments).
        ContentIndexFreshnessEvaluator.JournalReader gapWhenRefreshing = (p, since) =>
            new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>());
        // IsScopeStale must first report stale so the refresh path is taken.
        var staleThenGap = new StatefulReader(DirtyReaderFor(id), gapWhenRefreshing);

        var result = new ContentIndexAutoBuilder(paths).RefreshIncremental(
            new[] { root }, policy, new AppSettings(), staleThenGap.Read,
            _ => new MapResolver(new() { [id] = file }),
            _ => ClassifiedRead("x", policy), FileIdentityReader.TryGetIdentity);

        Assert.Equal(1, result.Built);
        var store = new ContentIndexStore(paths, ContentIndexManager.ScopeIdForRoot(root));
        Assert.Equal(0, store.ActiveSegmentCount()); // full rebuild resets segments
    }

    // Returns the first reader's result on the first call (the staleness probe), then the second reader's
    // result thereafter (the refresh read) — lets a test make a root look stale but then hit a journal gap.
    private sealed class StatefulReader
    {
        private readonly ContentIndexFreshnessEvaluator.JournalReader _first;
        private readonly ContentIndexFreshnessEvaluator.JournalReader _rest;
        private int _calls;
        public StatefulReader(ContentIndexFreshnessEvaluator.JournalReader first, ContentIndexFreshnessEvaluator.JournalReader rest)
        { _first = first; _rest = rest; }
        public UsnReadResult Read(string root, UsnCheckpoint since)
            => (_calls++ == 0 ? _first : _rest)(root, since);
    }
}
