using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests the Phase 3 USN-driven refresh (plan §3.5/§11.4): the pure <see cref="ContentIndexUsnChangeResolver"/>
/// (identity → created/modified/deleted paths, including renames) and the end-to-end
/// <see cref="ContentIndexIncrementalRefresher"/> that reads the journal, resolves changes, and appends a
/// segment. A fake <see cref="IFileIdPathResolver"/> + injected byte reader keep everything off the volume.
/// </summary>
public sealed class ContentIndexUsnRefreshTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexUsnRefreshTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-usn-refresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private static string Norm(string path) => IndexScopeIdentity.NormalizePath(path);

    // Reads-and-classifies a changed file the way the optimized content reader would (Stage 6): the resolver
    // now takes classified content + identity instead of raw bytes.
    private static IncrementalFileRead? Classified(string text)
        => new(IndexIngestionClassifier.ClassifyContent(Encoding.UTF8.GetBytes(text), OpenPolicy), null);

    // Builds a base with captured identities so USN changes can be reverse-mapped.
    private ContentIndexStore PublishBaseWithIdentities(
        out Dictionary<string, UsnFileIdentity> ids,
        VolumeBinding? volumeBinding = null)
    {
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        ulong next = 500;
        FileIdentity? Provider(string path)
        {
            string norm = Norm(path);
            if (!assigned.TryGetValue(norm, out var id)) { id = new UsnFileIdentity(next++, 0); assigned[norm] = id; }
            return new FileIdentity(0x9, id);
        }

        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        if (volumeBinding is { } binding)
            builder.SeedVolumeBinding(binding);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha original content"));
        builder.AddDocument(@"C:\r\gone.txt", Encoding.UTF8.GetBytes("to be deleted soon"));
        var gen = builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        var store = new ContentIndexStore(_paths, _scopeId, retainedGenerations: 4);
        store.Publish(gen);
        ids = assigned;
        return store;
    }

    private sealed class FakeResolver : IFileIdPathResolver
    {
        private readonly Dictionary<UsnFileIdentity, string?> _map;
        public FakeResolver(Dictionary<UsnFileIdentity, string?> map) => _map = map;
        public string? TryResolvePath(UsnFileIdentity identity) => _map.TryGetValue(identity, out string? p) ? p : null;
    }

    // ── Pure change resolver ──

    [Fact]
    public void Resolve_ModifiedFile_IsChanged()
    {
        var store = PublishBaseWithIdentities(out var ids);
        var baseGen = store.TryOpenCurrent()!;
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];

        var resolver = new FakeResolver(new() { [aId] = @"C:\r\a.txt" });
        var result = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(aId, 0) }, baseGen, resolver,
            _ => Classified("alpha NEW content"), _ => true);

        Assert.Single(result.Changed);
        Assert.Equal(@"C:\r\a.txt", result.Changed[0].Path);
        Assert.Empty(result.Deleted);
    }

    [Fact]
    public void Resolve_DeletedFile_TombstonesBasePath()
    {
        var store = PublishBaseWithIdentities(out var ids);
        var baseGen = store.TryOpenCurrent()!;
        UsnFileIdentity goneId = ids[Norm(@"C:\r\gone.txt")];

        // Resolver returns null → the identity no longer exists → deletion.
        var resolver = new FakeResolver(new() { [goneId] = null });
        var result = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(goneId, 0) }, baseGen, resolver,
            _ => null, _ => true);

        Assert.Empty(result.Changed);
        Assert.Contains(Norm(@"C:\r\gone.txt"), result.Deleted);
    }

    [Fact]
    public void Resolve_Rename_IndexesNewPath_AndTombstonesOld()
    {
        var store = PublishBaseWithIdentities(out var ids);
        var baseGen = store.TryOpenCurrent()!;
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];

        // a.txt (base) renamed to a2.txt (same identity resolves to the new path).
        var resolver = new FakeResolver(new() { [aId] = @"C:\r\a2.txt" });
        var result = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(aId, 0) }, baseGen, resolver,
            _ => Classified("alpha original content"), _ => true);

        Assert.Single(result.Changed);
        Assert.Equal(@"C:\r\a2.txt", result.Changed[0].Path);
        Assert.Contains(Norm(@"C:\r\a.txt"), result.Deleted); // old path tombstoned
    }

    [Fact]
    public void Resolve_UnreadableSamePath_TombstonesCurrentAndEveryPriorAlias()
    {
        _ = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        string hardLink = Norm(@"C:\r\a-link.txt");
        var prior = new Dictionary<UsnFileIdentity, IReadOnlyList<string>>
        {
            [aId] = new[] { Norm(@"C:\r\a.txt"), hardLink },
        };

        var result = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(aId, 0) }, prior,
            new FakeResolver(new() { [aId] = @"C:\r\a.txt" }),
            _ => null,
            _ => true);

        Assert.Empty(result.Changed);
        Assert.Equal(
            new[] { Norm(@"C:\r\a.txt"), hardLink }.OrderBy(static path => path),
            result.Deleted.OrderBy(static path => path));
    }

    [Fact]
    public void Resolve_ResolvedOutsideRoot_TombstonesBasePath()
    {
        var store = PublishBaseWithIdentities(out var ids);
        var baseGen = store.TryOpenCurrent()!;
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];

        // a.txt moved out of the indexed root → treated as a deletion from the scope.
        var resolver = new FakeResolver(new() { [aId] = @"C:\other\a.txt" });
        var result = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(aId, 0) }, baseGen, resolver,
            _ => Classified("x"), path => Norm(path).StartsWith(Norm(_root), StringComparison.Ordinal));

        Assert.Empty(result.Changed);
        Assert.Contains(Norm(@"C:\r\a.txt"), result.Deleted);
    }

    [Fact]
    public void Resolve_DuplicateIdentity_DecidedOnce()
    {
        var store = PublishBaseWithIdentities(out var ids);
        var baseGen = store.TryOpenCurrent()!;
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];

        var resolver = new FakeResolver(new() { [aId] = @"C:\r\a.txt" });
        var result = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(aId, 1), new UsnChange(aId, 2) }, baseGen, resolver,
            _ => Classified("alpha NEW"), _ => true);

        Assert.Single(result.Changed);
    }

    [Fact]
    public void Resolve_ReportsProgress_OverEveryRecord()
    {
        var store = PublishBaseWithIdentities(out var ids);
        var baseGen = store.TryOpenCurrent()!;
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];

        var resolver = new FakeResolver(new() { [aId] = @"C:\r\a.txt" });
        var reports = new List<(int Done, int Total)>();
        _ = ContentIndexUsnChangeResolver.Resolve(
            new[] { new UsnChange(aId, 1), new UsnChange(aId, 2), new UsnChange(aId, 3) }, baseGen, resolver,
            _ => Classified("alpha NEW"), _ => true,
            (done, total) => reports.Add((done, total)));

        // A small corpus gets only the end-of-loop tick; every tick carries the true total, and the last is
        // (total, total) — enough for a caller to compute a percent-complete.
        Assert.NotEmpty(reports);
        Assert.Equal((3, 3), reports[^1]);
        Assert.All(reports, r => Assert.Equal(3, r.Total));
    }

    [Fact]
    public void Resolve_ReportsProgress_AtThePeriodic512RecordBoundary()
    {
        _ = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        var changes = Enumerable.Range(0, 513).Select(i => new UsnChange(aId, (uint)i)).ToArray();
        var reports = new List<(int Done, int Total)>();

        _ = ContentIndexUsnChangeResolver.Resolve(
            changes,
            new Dictionary<UsnFileIdentity, IReadOnlyList<string>> { [aId] = new[] { Norm(@"C:\r\a.txt") } },
            new FakeResolver(new() { [aId] = @"C:\r\a.txt" }),
            _ => Classified("changed"),
            _ => true,
            (done, total) => reports.Add((done, total)));

        Assert.Equal(new[] { (512, 513), (513, 513) }, reports);
    }

    // ── End-to-end refresher ──

    private ContentIndexIncrementalRefresher NewRefresher(
        ContentIndexStore store,
        ContentIndexFreshnessEvaluator.JournalReader journal,
        IFileIdPathResolver resolver,
        Func<string, IncrementalFileRead?> readAndClassify)
        => new(store, OpenPolicy, _paths.IndexRoot, journal, _ => resolver, readAndClassify);

    [Fact]
    public void Refresh_ModifiedFile_AppendsSegment()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];

        ContentIndexFreshnessEvaluator.JournalReader journal = (root, since) =>
            new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), new[] { new UsnChange(aId, 0) });

        var refresher = NewRefresher(store,
            journal,
            new FakeResolver(new() { [aId] = @"C:\r\a.txt" }),
            _ => Classified("alpha CHANGED content"));

        var outcome = refresher.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
    }

    [Fact]
    public void PostBuildCatchUp_ExactThresholdSkipsResolutionAndPublication()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        bool resolverUsed = false;
        var refresher = new ContentIndexIncrementalRefresher(
            store,
            OpenPolicy,
            _paths.IndexRoot,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
                new[] { new UsnChange(aId, 0) }),
            _ =>
            {
                resolverUsed = true;
                return new FakeResolver(new() { [aId] = @"C:\r\a.txt" });
            },
            _ => Classified("changed"));
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 1,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NoChanges, result.Outcome);
        Assert.Equal(1, result.JournalChangeCount);
        Assert.True(result.ChangeCountComplete);
        Assert.False(result.ThresholdExceeded);
        Assert.False(resolverUsed);
        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void PostBuildCatchUp_AboveThresholdAppendsSegment()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        var refresher = NewRefresher(
            store,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
                new[] { new UsnChange(aId, 0) }),
            new FakeResolver(new() { [aId] = @"C:\r\a.txt" }),
            _ => Classified("changed after crawl"));
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, result.Outcome);
        Assert.Equal(1, result.JournalChangeCount);
        Assert.True(result.ChangeCountComplete);
        Assert.True(result.ThresholdExceeded);
        Assert.Equal(1, store.ActiveSegmentCount());
    }

    [Fact]
    public void PostBuildCatchUp_IncompleteJournalFailsClosedWithObservedCount()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        var refresher = NewRefresher(
            store,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Incomplete,
                since,
                new[] { new UsnChange(aId, 0) }),
            new FakeResolver(new()),
            _ => null);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.Equal(1, result.JournalChangeCount);
        Assert.False(result.ChangeCountComplete);
        Assert.True(result.ThresholdExceeded);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void PostBuildCatchUp_UnreadableFreshnessMetadataFailsClosedBeforeJournalAccess()
    {
        var store = PublishBaseWithIdentities(out _);
        Assert.NotNull(store.TryOpenCurrent(out string? generationDirectory));
        File.Delete(Path.Combine(generationDirectory!, ContentIndexGenerationSerializer.FileIdsFile));
        bool journalCalled = false;
        var refresher = NewRefresher(
            store,
            (_, since) =>
            {
                journalCalled = true;
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            },
            new FakeResolver(new()),
            _ => null);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.False(result.ChangeCountComplete);
        Assert.False(journalCalled);
    }

    [Fact]
    public void PostBuildCatchUp_VolumeMismatchFailsClosedBeforeJournalAccess()
    {
        var expectedVolume = new VolumeBinding(
            @"\\?\Volume{00000000-0000-0000-0000-000000000001}\",
            0x9,
            "NTFS",
            @"C:\",
            "r");
        var mountedElsewhere = expectedVolume with { VolumeSerialNumber = 0xA };
        var store = PublishBaseWithIdentities(out _, expectedVolume);
        bool journalCalled = false;
        var refresher = new ContentIndexIncrementalRefresher(
            store,
            OpenPolicy,
            _paths.IndexRoot,
            (_, since) =>
            {
                journalCalled = true;
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            },
            _ => new FakeResolver(new()),
            _ => null,
            volumeBindingReader: _ => mountedElsewhere);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.False(result.ChangeCountComplete);
        Assert.False(journalCalled);
    }

    [Fact]
    public void PostBuildCatchUp_JournalExceptionFailsClosed()
    {
        var store = PublishBaseWithIdentities(out _);
        var refresher = NewRefresher(
            store,
            (_, _) => throw new IOException("journal failed"),
            new FakeResolver(new()),
            _ => null);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.False(result.ChangeCountComplete);
    }

    [Fact]
    public void PostBuildCatchUp_UnreadableLayerMetadataRetainsObservedCount()
    {
        var store = PublishBaseWithIdentities(out var ids);
        Assert.NotNull(store.TryOpenCurrent(out string? generationDirectory));
        File.Delete(Path.Combine(generationDirectory!, ContentIndexGenerationSerializer.AliasesFile));
        UsnFileIdentity identity = ids[Norm(@"C:\r\a.txt")];
        var refresher = NewRefresher(
            store,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                since,
                new[] { new UsnChange(identity, 0) }),
            new FakeResolver(new()),
            _ => null);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.Equal(1, result.JournalChangeCount);
        Assert.True(result.ChangeCountComplete);
        Assert.True(result.ThresholdExceeded);
    }

    [Fact]
    public void PostBuildCatchUp_VolumeChangeBeforePublicationRetainsObservedCount()
    {
        var expectedVolume = new VolumeBinding(
            @"\\?\Volume{00000000-0000-0000-0000-000000000001}\",
            0x9,
            "NTFS",
            @"C:\",
            "r");
        var changedVolume = expectedVolume with { VolumeSerialNumber = 0xA };
        var store = PublishBaseWithIdentities(out var ids, expectedVolume);
        UsnFileIdentity identity = ids[Norm(@"C:\r\a.txt")];
        int volumeReads = 0;
        var refresher = new ContentIndexIncrementalRefresher(
            store,
            OpenPolicy,
            _paths.IndexRoot,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                since,
                new[] { new UsnChange(identity, 0) }),
            _ => new FakeResolver(new() { [identity] = @"C:\r\a.txt" }),
            _ => Classified("changed"),
            volumeBindingReader: _ => volumeReads++ == 0 ? expectedVolume : changedVolume);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: null,
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.Equal(1, result.JournalChangeCount);
        Assert.True(result.ChangeCountComplete);
        Assert.True(result.ThresholdExceeded);
        Assert.Equal(2, volumeReads);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void PostBuildCatchUp_ProgressFailureRetainsObservedCount()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity identity = ids[Norm(@"C:\r\a.txt")];
        var refresher = NewRefresher(
            store,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                since,
                new[] { new UsnChange(identity, 0) }),
            new FakeResolver(new() { [identity] = @"C:\r\a.txt" }),
            _ => Classified("changed"));
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IncrementalRefreshResult result = refresher.RefreshIfJournalChangeCountExceedsUnderLease(
            mutation,
            _scopeId,
            new IndexMaintenanceSettings(),
            DateTimeOffset.UtcNow,
            minimumJournalChanges: 0,
            progress: percent =>
            {
                if (percent == 100)
                    throw new IOException("progress failed");
            },
            CancellationToken.None);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, result.Outcome);
        Assert.Equal(1, result.JournalChangeCount);
        Assert.True(result.ChangeCountComplete);
        Assert.True(result.ThresholdExceeded);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Refresh_UnreadableKnownChange_WithAnotherSuccessfulChange_NeverTrustsStaleContent()
    {
        var store = PublishBaseWithIdentities(out var ids);
        string aPath = Norm(@"C:\r\a.txt");
        string bPath = Norm(@"C:\r\b.txt");
        UsnFileIdentity aId = ids[aPath];
        var bId = new UsnFileIdentity(9_500, 0);
        var resolver = new FakeResolver(new()
        {
            [aId] = @"C:\r\a.txt",
            [bId] = @"C:\r\b.txt",
        });
        var refresher = NewRefresher(
            store,
            (_, _) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(1, 250),
                new[] { new UsnChange(aId, 0), new UsnChange(bId, 0) }),
            resolver,
            path => path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase)
                ? null
                : new IncrementalFileRead(
                    IndexIngestionClassifier.ClassifyContent(Encoding.UTF8.GetBytes("successful beta change"), OpenPolicy),
                    new FileIdentity(0x9, bId)));

        IncrementalUpdateOutcome outcome = refresher.Refresh(
            _scopeId,
            new IndexMaintenanceSettings
            {
                MaxDeltaSegments = 64,
                CompactionThresholdMB = 8192,
            },
            DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        ContentIndexStore.LayeredIndexHandle layered = store.TryOpenLayered()!;
        Assert.True(layered.Segments[^1].IsRemoved(aPath));
        Assert.True(layered.Segments[^1].Added.TryGetAlias(bPath, out _, out _));
        Assert.Equal(new UsnCheckpoint(1, 250), layered.Segments[^1].FreshnessCheckpoint);

        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(layered, OpenPolicy, DateTimeOffset.UtcNow);
        store.Compact(compacted);
        ContentIndexGeneration reopened = store.TryOpenCurrent()!;
        Assert.False(reopened.TryGetAlias(aPath, out _, out _));
        Assert.True(reopened.TryGetAlias(bPath, out _, out _));
        Assert.Equal(new UsnCheckpoint(1, 250), reopened.Manifest.FreshnessCheckpoint);
    }

    [Fact]
    public void Refresh_ExcludesIndexStorage_AndReportsFinalizing()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        var storageId = new UsnFileIdentity(99_001, 0);
        string storageArtifact = Path.Combine(_paths.IndexRoot, "scopes", _scopeId, "active.ptr");
        var reads = new List<string>();
        var progress = new List<int>();
        var refresher = NewRefresher(
            store,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
                new[] { new UsnChange(aId, 0), new UsnChange(storageId, 0) }),
            new FakeResolver(new()
            {
                [aId] = @"C:\r\a.txt",
                [storageId] = storageArtifact,
            }),
            path =>
            {
                reads.Add(path);
                return Classified("changed content");
            });

        IncrementalUpdateOutcome outcome = refresher.Refresh(
            _scopeId, new AppSettings(), DateTimeOffset.UtcNow, progress.Add);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(new[] { @"C:\r\a.txt" }, reads);
        Assert.Equal(100, progress[^1]);
        Assert.DoesNotContain(store.TryOpenLayered()!.Segments[^1].Added.Aliases.Keys, alias =>
            IndexedRootsPolicy.Covers(_paths.IndexRoot, alias));
    }

    [Fact]
    public void Refresh_StorageOnlyChanges_DoNotAppendSegment()
    {
        var store = PublishBaseWithIdentities(out _);
        var storageId = new UsnFileIdentity(99_002, 0);
        string storageArtifact = Path.Combine(_paths.IndexRoot, "scope.ptr");
        bool read = false;
        var refresher = NewRefresher(
            store,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
                new[] { new UsnChange(storageId, 0) }),
            new FakeResolver(new() { [storageId] = storageArtifact }),
            _ =>
            {
                read = true;
                return Classified("must not be read");
            });

        IncrementalUpdateOutcome outcome = refresher.Refresh(
            _scopeId, new AppSettings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.NoChanges, outcome);
        Assert.False(read);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Refresh_DeletedFileIntroducedBySegment_TombstonesItsLayeredPriorPath()
    {
        var store = PublishBaseWithIdentities(out _);
        string segmentPath = Norm(@"C:\r\segment-only.txt");
        var segmentIdentity = new UsnFileIdentity(9001, 0);
        var segmentBuilder = new ContentIndexDeltaSegmentBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(0x9, segmentIdentity));
        segmentBuilder.AddChangedDocument(segmentPath, Encoding.UTF8.GetBytes("introduced after the base"));
        store.PublishSegment(segmentBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 150), DateTimeOffset.UtcNow));

        UsnCheckpoint journalSince = default;
        var refresher = NewRefresher(
            store,
            (_, since) =>
            {
                journalSince = since;
                return new UsnReadResult(
                    UsnReadStatus.Ok,
                    new UsnCheckpoint(1, 200),
                    new[] { new UsnChange(segmentIdentity, 0) });
            },
            new FakeResolver(new() { [segmentIdentity] = null }),
            _ => null);

        IncrementalUpdateOutcome outcome = refresher.Refresh(
            _scopeId, new AppSettings(), DateTimeOffset.UtcNow);

        Assert.Equal(new UsnCheckpoint(1, 150), journalSince);
        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(2, store.ActiveSegmentCount());
        Assert.True(store.TryOpenLayered()!.Segments[^1].IsRemoved(segmentPath));
    }

    [Fact]
    public void Refresh_NoBase_NeedsFullRebuild()
    {
        var store = new ContentIndexStore(_paths, _scopeId);
        var refresher = NewRefresher(store,
            (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>()),
            new FakeResolver(new()), _ => null);
        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, refresher.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Refresh_LegacyExtendedReFsIdentity_NeedsCompatibilityRebuildBeforeJournalAdvance()
    {
        FileIdentity? LegacyProvider(string _) => new(0x9, new UsnFileIdentity(0x67, 0x600));
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: LegacyProvider);
        builder.AddDocument(@"C:\r\legacy.txt", Encoding.UTF8.GetBytes("legacy ReFS identity"));
        var store = new ContentIndexStore(_paths, _scopeId, retainedGenerations: 4);
        store.Publish(builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));
        bool journalCalled = false;
        var refresher = NewRefresher(
            store,
            (_, since) =>
            {
                journalCalled = true;
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            },
            new FakeResolver(new()),
            _ => null);

        IncrementalUpdateOutcome outcome = refresher.Refresh(
            _scopeId, new AppSettings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.NeedsCompatibilityRebuild, outcome);
        Assert.False(journalCalled);
        Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Refresh_JournalGap_NeedsFullRebuild()
    {
        var store = PublishBaseWithIdentities(out _);
        var refresher = NewRefresher(store,
            (root, since) => new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>()),
            new FakeResolver(new()), _ => null);
        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, refresher.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Refresh_NoChanges_NoOp()
    {
        var store = PublishBaseWithIdentities(out _);
        var refresher = NewRefresher(store,
            (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>()),
            new FakeResolver(new()), _ => null);
        Assert.Equal(IncrementalUpdateOutcome.NoChanges, refresher.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Refresh_ResolverUnavailable_NeedsFullRebuild()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        // Resolver factory returns null (e.g. the root couldn't be opened) → full rebuild.
        var refresher = new ContentIndexIncrementalRefresher(store, OpenPolicy, _paths.IndexRoot,
            (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, new[] { new UsnChange(aId, 0) }),
            _ => null, _ => null);
        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, refresher.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Refresh_OutOfMemory_PropagatesFromJournalAndApplyPhases()
    {
        var store = PublishBaseWithIdentities(out var ids);
        UsnFileIdentity aId = ids[Norm(@"C:\r\a.txt")];
        var journalOom = NewRefresher(store,
            (_, _) => throw new OutOfMemoryException("journal oom"),
            new FakeResolver(new()), _ => null);
        Assert.Throws<OutOfMemoryException>(() => journalOom.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow));

        var applyOom = NewRefresher(store,
            (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, new[] { new UsnChange(aId, 0) }),
            new FakeResolver(new() { [aId] = @"C:\r\a.txt" }),
            _ => throw new OutOfMemoryException("read oom"));
        Assert.Throws<OutOfMemoryException>(() => applyOom.Refresh(_scopeId, new AppSettings(), DateTimeOffset.UtcNow));
    }
}
