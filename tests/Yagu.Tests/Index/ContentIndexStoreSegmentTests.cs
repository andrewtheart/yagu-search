using System;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests the Phase 3 store support (plan §11.4): publishing/opening delta segments over a base, the
/// backward-compatible pointer format, segment retention, the compaction triggers, and folding a layered
/// index into a fresh base via <see cref="ContentIndexCompactor"/>. Uses a per-test sandbox (§9.2).
/// </summary>
public sealed class ContentIndexStoreSegmentTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexStoreSegmentTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-store-seg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexStore NewStore(int retained = 2) => new(_paths, _scopeId, retained);

    private ContentIndexGeneration BuildBase(params (string Path, string Text)[] docs)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        foreach (var (p, t) in docs)
            builder.AddDocument(p, Encoding.UTF8.GetBytes(t));
        return builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private ContentIndexDeltaSegment BuildSegment(ulong journalId, long usn, Action<ContentIndexDeltaSegmentBuilder> add)
    {
        var b = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        add(b);
        return b.Build(_scopeId, "vol", _root, new UsnCheckpoint(journalId, usn), DateTimeOffset.UtcNow);
    }

    // ── Publish + open layered ──

    [Fact]
    public void PublishSegment_AppendsToBase_AndOpensLayered()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base")));
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"))));

        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.Single(handle!.Segments);
        Assert.Equal(1, handle.Base.AliasCount);
        Assert.Equal(1, store.ActiveSegmentCount());
        // The base-only open still works (returns the base generation).
        Assert.NotNull(store.TryOpenCurrent());
    }

    [Fact]
    public void TryReadCurrentFreshnessInputs_Layered_UsesNewestSegmentCheckpoint()
    {
        var store = new ContentIndexStore(_paths, _scopeId);
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        baseBuilder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha base"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));
        var segment1 = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        segment1.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"));
        store.PublishSegment(segment1.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow));
        var segment2 = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        segment2.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("gamma added"));
        store.PublishSegment(segment2.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 350), DateTimeOffset.UtcNow));

        var inputs = store.TryReadCurrentFreshnessInputs();
        Assert.NotNull(inputs);
        Assert.Equal(new UsnCheckpoint(1, 350), inputs.Value.Manifest.FreshnessCheckpoint);
        Assert.Equal(3, inputs.Value.FileIds.Count);
    }

    [Fact]
    public void TryReadCurrentIncrementalMetadata_DoesNotReadContent_AndAppliesNewestLayerPrecedence()
    {
        var store = new ContentIndexStore(_paths, _scopeId);
        string a = IndexScopeIdentity.NormalizePath(@"C:\r\a.txt");
        string b = IndexScopeIdentity.NormalizePath(@"C:\r\b.txt");
        var baseA = new UsnFileIdentity(101, 0);
        var segmentB = new UsnFileIdentity(202, 0);
        var replacementA = new UsnFileIdentity(303, 0);

        var baseBuilder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: path => new FileIdentity(5, baseA));
        baseBuilder.AddDocument(a, Encoding.UTF8.GetBytes("base alpha"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));

        var firstSegment = new ContentIndexDeltaSegmentBuilder(
            OpenPolicy,
            identityProvider: path => new FileIdentity(5, segmentB));
        firstSegment.AddChangedDocument(b, Encoding.UTF8.GetBytes("segment beta"));
        firstSegment.AddTombstone(a);
        store.PublishSegment(firstSegment.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow));

        var secondSegment = new ContentIndexDeltaSegmentBuilder(
            OpenPolicy,
            identityProvider: path => new FileIdentity(5, replacementA));
        secondSegment.AddChangedDocument(a, Encoding.UTF8.GetBytes("replacement alpha"));
        secondSegment.AddTombstone(b);
        store.PublishSegment(secondSegment.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 350), DateTimeOffset.UtcNow));

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        File.WriteAllBytes(Path.Combine(baseDir!, ContentIndexGenerationSerializer.ContentFile), new byte[] { 1, 2, 3 });
        foreach (string segmentDir in segmentDirs)
            File.WriteAllBytes(Path.Combine(segmentDir, ContentIndexGenerationSerializer.ContentFile), new byte[] { 4, 5, 6 });

        var metadata = store.TryReadCurrentIncrementalMetadata(
            new HashSet<UsnFileIdentity> { baseA, segmentB, replacementA });

        Assert.NotNull(metadata);
        Assert.Equal(new UsnCheckpoint(1, 350), metadata.Value.Manifest.FreshnessCheckpoint);
        Assert.False(metadata.Value.PathsByIdentity.ContainsKey(baseA));
        Assert.False(metadata.Value.PathsByIdentity.ContainsKey(segmentB));
        Assert.Equal(new[] { a }, metadata.Value.PathsByIdentity[replacementA]);
    }

    [Fact]
    public void TryReadCurrentIncrementalMetadata_NewerAliasReplacesPriorIdentityWithoutTombstone()
    {
        var store = new ContentIndexStore(_paths, _scopeId);
        string path = IndexScopeIdentity.NormalizePath(@"C:\r\same.txt");
        var oldIdentity = new UsnFileIdentity(401, 0);
        var newIdentity = new UsnFileIdentity(402, 0);

        var baseBuilder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(5, oldIdentity));
        baseBuilder.AddDocument(path, Encoding.UTF8.GetBytes("old content"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));

        var segment = new ContentIndexDeltaSegmentBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(5, newIdentity));
        segment.AddChangedDocument(path, Encoding.UTF8.GetBytes("new content"));
        store.PublishSegment(segment.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow));

        var metadata = store.TryReadCurrentIncrementalMetadata(
            new HashSet<UsnFileIdentity> { oldIdentity, newIdentity });

        Assert.NotNull(metadata);
        Assert.False(metadata.Value.PathsByIdentity.ContainsKey(oldIdentity));
        Assert.Equal(new[] { path }, metadata.Value.PathsByIdentity[newIdentity]);
    }

    [Fact]
    public void TryReadCurrentIncrementalMetadata_CorruptActiveMetadata_DoesNotMixPointerSlots()
    {
        var store = new ContentIndexStore(_paths, _scopeId);
        var identity = new UsnFileIdentity(501, 0);
        var baseBuilder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(5, identity));
        baseBuilder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("base content"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));

        var segment = new ContentIndexDeltaSegmentBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(5, new UsnFileIdentity(502, 0)));
        segment.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("segment content"));
        store.PublishSegment(segment.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow));
        Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);

        string segmentAliases = Path.Combine(
            store.ScopeDirectory,
            "segments",
            "seg-000001",
            ContentIndexGenerationSerializer.AliasesFile);
        File.WriteAllBytes(segmentAliases, new byte[] { 1, 2, 3 });

        Assert.Null(store.TryReadCurrentIncrementalMetadata(new HashSet<UsnFileIdentity> { identity }));
    }

    [Fact]
    public void MetadataReads_CorruptSegmentInputs_FallBackOrFailClosed()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        string firstSegment = Path.Combine(store.ScopeDirectory, "segments", "seg-000001");
        string fileIds = Path.Combine(firstSegment, ContentIndexGenerationSerializer.FileIdsFile);
        byte[] originalFileIds = File.ReadAllBytes(fileIds);

        File.WriteAllBytes(fileIds, new byte[] { 1, 2, 3 });
        Assert.Equal(new UsnCheckpoint(1, 100),
            store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);

        File.WriteAllBytes(fileIds, originalFileIds);
        store.PublishSegment(BuildSegment(1, 300,
            b => b.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("gamma"))));
        File.WriteAllBytes(fileIds, new byte[] { 1, 2, 3 });
        Assert.Null(store.TryReadCurrentFreshnessInputs());

        File.WriteAllBytes(fileIds, originalFileIds);
        string manifest = Path.Combine(firstSegment, ContentIndexGenerationSerializer.ManifestFile);
        File.WriteAllBytes(manifest, new byte[] { 1, 2, 3 });
        Assert.Null(store.TryReadCurrentIncrementalManifest());
    }

    [Fact]
    public void IncrementalMetadata_CancellationBaseFallbackAndCorruptTombstones_AreFailSafe()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\old.txt", "old")));
        store.Publish(BuildBase((@"C:\r\new.txt", "new")));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            store.TryReadCurrentIncrementalMetadata(new HashSet<UsnFileIdentity>(), cancelled.Token));

        string newestManifest = Path.Combine(
            store.ScopeDirectory,
            "generations",
            "gen-000002",
            ContentIndexGenerationSerializer.ManifestFile);
        File.WriteAllBytes(newestManifest, new byte[] { 1, 2, 3 });
        var fallback = store.TryReadCurrentIncrementalMetadata(new HashSet<UsnFileIdentity>());
        Assert.NotNull(fallback);
        Assert.Equal(new UsnCheckpoint(1, 100), fallback.Value.Manifest.FreshnessCheckpoint);

        store.DeleteScope();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddTombstone(@"C:\r\a.txt")));
        string tombstones = Path.Combine(
            store.ScopeDirectory,
            "segments",
            "seg-000001",
            ContentIndexDeltaSegmentSerializer.TombstonesFile);
        File.WriteAllBytes(tombstones, new byte[] { 1, 2, 3 });
        Assert.Null(store.TryReadCurrentIncrementalMetadata(new HashSet<UsnFileIdentity>()));

        store.DeleteScope();
        Assert.Null(store.TryReadCurrentIncrementalMetadata(new HashSet<UsnFileIdentity>()));
    }

    [Fact]
    public void MetadataReads_CorruptNewestBaseInputs_FallBackWithoutMixingSlots()
    {
        var store = NewStore();
        var firstBuilder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: IndexTestIdentities.Provider);
        firstBuilder.AddDocument(@"C:\r\first.txt", Encoding.UTF8.GetBytes("first"));
        store.Publish(firstBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));

        var secondBuilder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: IndexTestIdentities.Provider);
        secondBuilder.AddDocument(@"C:\r\second.txt", Encoding.UTF8.GetBytes("second"));
        secondBuilder.AddDocument(@"C:\r\third.txt", Encoding.UTF8.GetBytes("third"));
        store.Publish(secondBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow));

        string newest = Path.Combine(store.ScopeDirectory, "generations", "gen-000002");
        string fileIds = Path.Combine(newest, ContentIndexGenerationSerializer.FileIdsFile);
        byte[] originalFileIds = File.ReadAllBytes(fileIds);
        File.WriteAllBytes(fileIds, new byte[] { 1, 2, 3 });
        var freshness = store.TryReadCurrentFreshnessInputs();
        Assert.NotNull(freshness);
        Assert.Equal(new UsnCheckpoint(1, 100), freshness.Value.Manifest.FreshnessCheckpoint);
        Assert.Equal(1, freshness.Value.FileIds.Count);

        File.WriteAllBytes(fileIds, originalFileIds);
        File.WriteAllBytes(
            Path.Combine(newest, ContentIndexGenerationSerializer.ManifestFile),
            new byte[] { 1, 2, 3 });
        Assert.Equal(
            new UsnCheckpoint(1, 100),
            store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void IncrementalMetadata_CorruptAcceptedBaseMetadata_FailsClosed()
    {
        var store = NewStore();
        var identity = new UsnFileIdentity(901, 0);
        var builder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(7, identity));
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha"));
        store.Publish(builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));
        File.WriteAllBytes(
            Path.Combine(
                store.ScopeDirectory,
                "generations",
                "gen-000001",
                ContentIndexGenerationSerializer.AliasesFile),
            new byte[] { 1, 2, 3 });

        Assert.Null(store.TryReadCurrentIncrementalMetadata(
            new HashSet<UsnFileIdentity> { identity }));
    }

    [Fact]
    public void IncrementalMetadata_MissingNewestSegmentManifest_FallsBackToBaseSlot()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        File.Delete(Path.Combine(
            store.ScopeDirectory,
            "segments",
            "seg-000001",
            ContentIndexGenerationSerializer.ManifestFile));

        var metadata = store.TryReadCurrentIncrementalMetadata(new HashSet<UsnFileIdentity>());
        Assert.NotNull(metadata);
        Assert.Equal(new UsnCheckpoint(1, 100), metadata.Value.Manifest.FreshnessCheckpoint);
    }

    [Fact]
    public void PublishSegment_WithV3Enabled_WritesV3SidecarsInEverySegment_ThatRoundTrip()
    {
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        store.Publish(BuildBase((@"C:\r\a.txt", "the planner produces trigram queries")));
        store.PublishSegment(BuildSegment(2, 200, b =>
            b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"))));

        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.Single(handle!.SegmentDirs);

        // Base AND segment must each have query-postings.v3 that reproduces that layer's posting evaluation.
        TrigramExpression query = PlanQuery("planner");
        using (ContentIndexV3Reader baseReader = ContentIndexV3Format.TryOpen(handle.BaseDir)!)
        {
            Assert.NotNull(baseReader);
            Assert.True(handle.Base.Postings.EvaluateSet(query).SetEquals(baseReader.EvaluateSet(query)));
        }
        using (ContentIndexV3Reader segReader = ContentIndexV3Format.TryOpen(handle.SegmentDirs[0])!)
        {
            Assert.NotNull(segReader);
            Assert.True(handle.Segments[0].Added.Postings.EvaluateSet(query).SetEquals(segReader.EvaluateSet(query)));
        }
    }

    [Fact]
    public void PublishSegmentFast_WithV3Enabled_WritesSegmentV3()
    {
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        store.Publish(BuildBase((@"C:\r\a.txt", "the planner produces trigram queries")));
        store.PublishSegmentFast(BuildSegment(2, 200, b =>
            b.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"))));

        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.Single(handle!.SegmentDirs);
        Assert.True(File.Exists(Path.Combine(handle.SegmentDirs[0], ContentIndexV3Format.PostingsFile)));
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    [Fact]
    public void TryOpenLayered_CancelledWarm_ThrowsAndDoesNotPopulateQueryCache()
    {
        OpenedLayeredIndexCache.Clear();
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            store.TryOpenLayered(retainDocuments: false, cancellationToken: cts.Token));
        Assert.False(store.IsCurrentLayeredIndexCached());
    }

    [Fact]
    public void MetadataReads_AreManifestOnly_AndFallBackFromCorruptNewestSegment()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base")));
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"))));

        string segmentDir = Path.Combine(store.ScopeDirectory, "segments", "seg-000001");
        string contentFile = Path.Combine(segmentDir, ContentIndexGenerationSerializer.ContentFile);
        byte[] originalContent = File.ReadAllBytes(contentFile);
        File.WriteAllBytes(contentFile, new byte[] { 1, 2, 3 });
        Assert.Equal(1, store.ActiveSegmentCount()); // manifest-only: content corruption does not force a huge load

        File.WriteAllBytes(contentFile, originalContent);
        File.WriteAllBytes(Path.Combine(segmentDir, ContentIndexGenerationSerializer.ManifestFile), new byte[] { 1, 2, 3 });
        StoredIndexStat stat = store.ReadStorageStat();
        Assert.True(stat.Readable); // older redundant slot is the complete base-only index
        Assert.Equal(1, stat.DocumentCount);
        Assert.Equal(0, stat.SegmentCount);
        Assert.Equal(DirectorySize(Path.Combine(store.ScopeDirectory, "generations", "gen-000001")),
            store.GetCurrentLayeredIndexSizeBytes());
    }

    [Fact]
    public void MetadataReads_ReportCreationAndLatestIncrementalUpdate_AcrossCompaction()
    {
        var createdUtc = new DateTimeOffset(2026, 7, 27, 15, 14, 0, TimeSpan.Zero);
        var fullBuildCompletedUtc = createdUtc.AddHours(2);
        var updatedUtc = createdUtc.AddDays(1).AddHours(2);
        var compactedUtc = updatedUtc.AddHours(3);
        var store = NewStore();

        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        baseBuilder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha base"));
        store.Publish(baseBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), createdUtc));

        var fullBuildBatchBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        fullBuildBatchBuilder.AddDocument(@"C:\r\paged.txt", Encoding.UTF8.GetBytes("paged full-build batch"));
        ContentIndexGeneration fullBuildBatch = fullBuildBatchBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 100),
            fullBuildCompletedUtc,
            lastIncrementalUpdateUtc: null);
        store.PublishSegment(new ContentIndexDeltaSegment(fullBuildBatch, Array.Empty<string>()));

        StoredIndexStat pagedFullBuildStat = store.ReadStorageStat();
        Assert.Equal(createdUtc, pagedFullBuildStat.CreatedUtc);
        Assert.Equal(fullBuildCompletedUtc, pagedFullBuildStat.BuiltUtc);
        Assert.Null(pagedFullBuildStat.LastIncrementalUpdateUtc);

        ContentIndexStore.LayeredIndexHandle? pagedHandle = store.TryOpenLayered();
        Assert.NotNull(pagedHandle);
        ContentIndexGeneration pagedCompaction = ContentIndexCompactor.Compact(
            pagedHandle!, OpenPolicy, fullBuildCompletedUtc.AddMinutes(5));
        Assert.Null(pagedCompaction.Manifest.LastIncrementalUpdateUtc);

        var segmentBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        segmentBuilder.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"));
        store.PublishSegment(segmentBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(2, 200), updatedUtc));

        StoredIndexStat layeredStat = store.ReadStorageStat();
        Assert.Equal(createdUtc, layeredStat.CreatedUtc);
        Assert.Equal(fullBuildCompletedUtc, layeredStat.BuiltUtc);
        Assert.Equal(updatedUtc, layeredStat.LastIncrementalUpdateUtc);

        ContentIndexStore.LayeredIndexHandle? handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(handle!, OpenPolicy, compactedUtc);
        Assert.Equal(createdUtc, compacted.Manifest.CreatedUtc);
        Assert.Equal(updatedUtc, compacted.Manifest.LastIncrementalUpdateUtc);

        store.Compact(compacted);
        StoredIndexStat compactedStat = store.ReadStorageStat();
        Assert.Equal(createdUtc, compactedStat.CreatedUtc);
        Assert.Equal(compactedUtc, compactedStat.BuiltUtc);
        Assert.Equal(updatedUtc, compactedStat.LastIncrementalUpdateUtc);
    }

    [Fact]
    public void SmallSegmentCoalescing_PreservesFullBuildAndIncrementalProvenanceBoundaries()
    {
        var createdUtc = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);
        var store = NewStore();
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        baseBuilder.AddDocument(@"C:\r\base.txt", Encoding.UTF8.GetBytes("base"));
        store.Publish(baseBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), createdUtc));

        DateTimeOffset finalBatchUtc = createdUtc;
        for (int i = 0; i < EffectiveIndexSizePolicy.Default.CoalesceMinRun; i++)
        {
            finalBatchUtc = createdUtc.AddMinutes(i + 1);
            var batchBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
            batchBuilder.AddDocument($@"C:\r\batch-{i}.txt", Encoding.UTF8.GetBytes($"batch {i}"));
            ContentIndexGeneration batch = batchBuilder.Build(
                _scopeId,
                "vol",
                _root,
                new UsnCheckpoint(1, 100),
                finalBatchUtc,
                lastIncrementalUpdateUtc: null);
            store.PublishSegment(new ContentIndexDeltaSegment(batch, Array.Empty<string>()));
        }

        DateTimeOffset finalIncrementalUtc = finalBatchUtc;
        for (int i = 0; i < EffectiveIndexSizePolicy.Default.CoalesceMinRun; i++)
        {
            finalIncrementalUtc = finalBatchUtc.AddMinutes(i + 1);
            var incrementalBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
            incrementalBuilder.AddChangedDocument(
                $@"C:\r\incremental-{i}.txt",
                Encoding.UTF8.GetBytes($"incremental {i}"));
            store.PublishSegment(incrementalBuilder.Build(
                _scopeId,
                "vol",
                _root,
                new UsnCheckpoint(1, 200 + i),
                finalIncrementalUtc));
        }

        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        int removed = updater.CoalesceSmallSegmentsUnderLease(
            mutation, maxSegments: 1, CancellationToken.None);

        Assert.Equal((EffectiveIndexSizePolicy.Default.CoalesceMinRun - 1) * 2, removed);
        Assert.Equal(2, store.ActiveSegmentCount());
        StoredIndexStat stat = store.ReadStorageStat();
        Assert.Equal(finalBatchUtc, stat.BuiltUtc);
        Assert.Equal(finalIncrementalUtc, stat.LastIncrementalUpdateUtc);
    }

    [Fact]
    public void SmallSegmentRun_InvalidBoundsMissingBaseAndPhysicalBoundaries_AreRejectedOrSplit()
    {
        var store = NewStore();
        Assert.False(store.TryFindSmallSegmentRun(1, 2, 10, 20, out _));
        Assert.False(store.TryFindSmallSegmentRun(3, 2, 10, 20, out _));
        Assert.False(store.TryFindSmallSegmentRun(2, 2, 0, 20, out _));
        Assert.False(store.TryFindSmallSegmentRun(2, 2, 10, 9, out _));
        Assert.False(store.TryFindSmallSegmentRun(2, 2, 10, 20, out _));

        store.Publish(BuildBase((@"C:\r\base.txt", "base")));
        string baseManifest = Path.Combine(
            store.ScopeDirectory,
            "generations",
            "gen-000001",
            ContentIndexGenerationSerializer.ManifestFile);
        byte[] originalBaseManifest = File.ReadAllBytes(baseManifest);
        File.WriteAllBytes(baseManifest, new byte[] { 1, 2, 3 });
        Assert.False(store.TryFindSmallSegmentRun(2, 2, 10, 20, out _));
        File.WriteAllBytes(baseManifest, originalBaseManifest);

        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\one.txt", Encoding.UTF8.GetBytes("one"))));
        store.PublishSegment(BuildSegment(1, 300,
            b => b.AddChangedDocument(@"C:\r\two.txt", Encoding.UTF8.GetBytes("two"))));
        store.PublishSegment(BuildSegment(1, 400,
            b => b.AddChangedDocument(@"C:\r\three.txt", Encoding.UTF8.GetBytes("three"))));

        store.DirectorySizeReader = directory => Path.GetFileName(directory) == "seg-000001" ? 11 : 1;
        Assert.True(store.TryFindSmallSegmentRun(2, 3, 10, 30, out ContentIndexStore.SegmentCoalesceRun? afterLarge));
        Assert.Equal(1, afterLarge!.StartIndex);
        Assert.Equal(new[] { "seg-000002", "seg-000003" }, afterLarge.SegmentIds);

        store.DirectorySizeReader = _ => 6;
        Assert.False(store.TryFindSmallSegmentRun(2, 3, 6, 10, out _));

        string thirdManifest = Path.Combine(
            store.ScopeDirectory,
            "segments",
            "seg-000003",
            ContentIndexGenerationSerializer.ManifestFile);
        File.WriteAllBytes(thirdManifest, new byte[] { 1, 2, 3 });
        store.DirectorySizeReader = _ => 1;
        Assert.True(store.TryFindSmallSegmentRun(2, 3, 10, 30, out ContentIndexStore.SegmentCoalesceRun? beforeUnreadable));
        Assert.Equal(new[] { "seg-000001", "seg-000002" }, beforeUnreadable!.SegmentIds);
    }

    [Fact]
    public void SmallSegmentRun_ProvenanceBoundaryBeforeMinimum_ResetsTheCandidate()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\base.txt", "base")));

        var pageBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        pageBuilder.AddDocument(@"C:\r\page.txt", Encoding.UTF8.GetBytes("page"));
        ContentIndexGeneration page = pageBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow,
            lastIncrementalUpdateUtc: null);
        store.PublishSegment(new ContentIndexDeltaSegment(page, Array.Empty<string>()));
        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\incremental.txt", Encoding.UTF8.GetBytes("incremental"))));

        store.DirectorySizeReader = _ => 1;
        Assert.False(store.TryFindSmallSegmentRun(2, 2, 10, 20, out _));
    }

    [Fact]
    public void ReplaceSegmentRun_InvalidOrStaleDescriptors_DoNotMutateTheIndex()
    {
        var store = NewStore();
        var merged = BuildSegment(1, 400,
            b => b.AddChangedDocument(@"C:\r\merged.txt", Encoding.UTF8.GetBytes("merged")));
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var absent = new ContentIndexStore.SegmentCoalesceRun(0, ["seg-000001"], [], 1);
        Assert.False(store.TryReplaceSegmentRunUnderLease(mutation, absent, merged));

        store.PublishUnderLease(mutation, BuildBase((@"C:\r\base.txt", "base")));
        Assert.False(store.TryReplaceSegmentRunUnderLease(
            mutation,
            absent with { StartIndex = -1 },
            merged));
        Assert.False(store.TryReplaceSegmentRunUnderLease(mutation, absent, merged));

        store.PublishSegmentUnderLease(mutation, BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\one.txt", Encoding.UTF8.GetBytes("one"))));
        var stale = new ContentIndexStore.SegmentCoalesceRun(0, ["seg-stale"], [], 1);
        Assert.False(store.TryReplaceSegmentRunUnderLease(mutation, stale, merged));
        Assert.Equal(1, store.ActiveSegmentCount());
    }

    [Fact]
    public void ReplaceSegmentRun_WhenStagedReplacementFailsValidation_CleansTempAndKeepsRun()
    {
        var store = NewStore();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        store.PublishUnderLease(mutation, BuildBase((@"C:\r\base.txt", "base")));
        store.PublishSegmentUnderLease(mutation, BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\one.txt", Encoding.UTF8.GetBytes("one"))));
        store.PublishSegmentUnderLease(mutation, BuildSegment(1, 300,
            b => b.AddChangedDocument(@"C:\r\two.txt", Encoding.UTF8.GetBytes("two"))));
        var run = new ContentIndexStore.SegmentCoalesceRun(
            0,
            ["seg-000001", "seg-000002"],
            [],
            2);
        var merged = BuildSegment(1, 400,
            b => b.AddChangedDocument(@"C:\r\merged.txt", Encoding.UTF8.GetBytes("merged")));

        IndexMutationFaults.OnHit = point =>
        {
            if (point != IndexMutationFaults.CoalesceWritten)
                return;
            string tempDir = Directory.GetDirectories(
                Path.Combine(store.ScopeDirectory, "segments"),
                ".seg-*.tmp").Single();
            File.WriteAllBytes(
                Path.Combine(tempDir, ContentIndexGenerationSerializer.ContentFile),
                new byte[] { 1, 2, 3 });
        };
        try
        {
            Assert.False(store.TryReplaceSegmentRunUnderLease(mutation, run, merged));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }

        Assert.Equal(2, store.ActiveSegmentCount());
        Assert.DoesNotContain(
            Directory.GetDirectories(Path.Combine(store.ScopeDirectory, "segments")),
            directory => Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal));
    }

    [Fact]
    public void StorageMetadata_LegacyNullProvenanceWithAdvancedCheckpoint_IsNotFullBuildPaging()
    {
        var createdUtc = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);
        var legacyIncrementalUtc = createdUtc.AddHours(1);
        var store = NewStore();
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        baseBuilder.AddDocument(@"C:\r\base.txt", Encoding.UTF8.GetBytes("base"));
        store.Publish(baseBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), createdUtc));

        var legacyBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        legacyBuilder.AddDocument(@"C:\r\legacy.txt", Encoding.UTF8.GetBytes("legacy update"));
        ContentIndexGeneration legacySegment = legacyBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 200),
            legacyIncrementalUtc,
            lastIncrementalUpdateUtc: null);
        store.PublishSegment(new ContentIndexDeltaSegment(legacySegment, Array.Empty<string>()));

        StoredIndexStat stat = store.ReadStorageStat();
        Assert.Equal(createdUtc, stat.BuiltUtc);
        Assert.Null(stat.LastIncrementalUpdateUtc);
    }

    [Fact]
    public void ReadStorageStat_MissingWrongScopeAndIncompatibleActiveSegment_AreDiagnosed()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\base.txt", "base")));
        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\one.txt", Encoding.UTF8.GetBytes("one"))));
        store.PublishSegment(BuildSegment(1, 300,
            b => b.AddChangedDocument(@"C:\r\two.txt", Encoding.UTF8.GetBytes("two"))));

        string firstSegment = Path.Combine(store.ScopeDirectory, "segments", "seg-000001");
        string manifestPath = Path.Combine(firstSegment, ContentIndexGenerationSerializer.ManifestFile);
        byte[] original = File.ReadAllBytes(manifestPath);
        IndexManifest manifest = store.TryOpenLayered()!.Segments[0].Added.Manifest;

        File.Delete(manifestPath);
        StoredIndexStat missing = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, missing.Health);
        Assert.Equal(_root, missing.RootPath);

        File.WriteAllBytes(manifestPath, original);
        ChecksummedFile.Write(
            manifestPath,
            Encoding.UTF8.GetBytes((manifest with { ScopeId = "another-scope" }).Serialize()));
        StoredIndexStat wrongScope = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, wrongScope.Health);
        Assert.Equal(_root, wrongScope.RootPath);

        ChecksummedFile.Write(
            manifestPath,
            Encoding.UTF8.GetBytes((manifest with
            {
                IndexFormatVersion = IndexManifest.CurrentFormatVersion - 1,
            }).Serialize()));
        StoredIndexStat incompatible = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.IncompatibleFormat, incompatible.Health);
        Assert.Equal(_root, incompatible.RootPath);
        Assert.Contains("incompatible", incompatible.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadStorageStat_NullCreationAndExistingIncrementalTimes_UseSafeFallbacks()
    {
        var store = NewStore();
        DateTimeOffset baseBuilt = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset baseUpdated = baseBuilt.AddHours(2);
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        baseBuilder.AddDocument(@"C:\r\base.txt", Encoding.UTF8.GetBytes("base"));
        ContentIndexGeneration baseGeneration = baseBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 100),
            baseBuilt,
            createdUtc: baseBuilt,
            lastIncrementalUpdateUtc: baseUpdated);
        store.Publish(baseGeneration);
        string baseManifestPath = Path.Combine(
            store.ScopeDirectory,
            "generations",
            "gen-000001",
            ContentIndexGenerationSerializer.ManifestFile);
        ChecksummedFile.Write(
            baseManifestPath,
            Encoding.UTF8.GetBytes((baseGeneration.Manifest with { CreatedUtc = null }).Serialize()));

        var olderBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        olderBuilder.AddDocument(@"C:\r\older.txt", Encoding.UTF8.GetBytes("older"));
        ContentIndexGeneration olderGeneration = olderBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 200),
            baseBuilt.AddHours(1),
            lastIncrementalUpdateUtc: baseUpdated.AddMinutes(-1));
        store.PublishSegment(new ContentIndexDeltaSegment(olderGeneration, Array.Empty<string>()));

        var newerBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        newerBuilder.AddDocument(@"C:\r\newer.txt", Encoding.UTF8.GetBytes("newer"));
        ContentIndexGeneration newerGeneration = newerBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 300),
            baseBuilt.AddHours(3),
            lastIncrementalUpdateUtc: baseUpdated.AddMinutes(1));
        store.PublishSegment(new ContentIndexDeltaSegment(newerGeneration, Array.Empty<string>()));

        StoredIndexStat healthy = store.ReadStorageStat();
        Assert.Equal(baseBuilt, healthy.CreatedUtc);
        Assert.Equal(baseUpdated.AddMinutes(1), healthy.LastIncrementalUpdateUtc);

        string firstSegmentManifest = Path.Combine(
            store.ScopeDirectory,
            "segments",
            "seg-000001",
            ContentIndexGenerationSerializer.ManifestFile);
        byte[] originalSegmentManifest = File.ReadAllBytes(firstSegmentManifest);
        File.Delete(firstSegmentManifest);
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, store.ReadStorageStat().Health);

        IndexManifest incompatible = olderGeneration.Manifest with
        {
            IndexFormatVersion = IndexManifest.CurrentFormatVersion - 1,
        };
        ChecksummedFile.Write(firstSegmentManifest, Encoding.UTF8.GetBytes(incompatible.Serialize()));
        Assert.Equal(IndexStorageHealth.IncompatibleFormat, store.ReadStorageStat().Health);
        File.WriteAllBytes(firstSegmentManifest, originalSegmentManifest);
    }

    private static long DirectorySize(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

    // ── Proactive re-anchor (prevents USN-journal-wrap bypass on an unchanging root) ──

    [Fact]
    public void TryReanchorBaseCheckpoint_BaseOnly_AdvancesManifestCheckpoint_KeepingContent()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "the planner produces trigram queries")));

        // Base checkpoint starts at (1,100); advance it in place to (1,500) — a manifest-only rewrite.
        Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);
        Assert.True(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 500)));
        Assert.Equal(new UsnCheckpoint(1, 500), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);

        // Content / aliases / postings are untouched: the generation still reads + classifies its member.
        var gen = store.TryOpenCurrent();
        Assert.NotNull(gen);
        Assert.True(new ContentIndexQuerySessionAssertHelper(gen!).IsMember(@"C:\r\a.txt", "planner"));
    }

    [Fact]
    public void TryReanchorBaseCheckpoint_IsIdempotent_NeverRegresses()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        Assert.True(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 500)));
        // Re-anchoring to the same or an earlier position is a no-op (the checkpoint only moves forward).
        Assert.False(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 500)));
        Assert.False(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 400)));
        Assert.Equal(new UsnCheckpoint(1, 500), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);
    }

    [Fact]
    public void TryReanchorBaseCheckpoint_Segmented_ReturnsFalse_LeavesCheckpointUnchanged()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base")));
        store.PublishSegment(BuildSegment(1, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"))));

        // A segmented scope's base is the OLDEST layer → advancing only it would be inconsistent → refused.
        Assert.False(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 900)));
        // Effective freshness comes from the newest active segment, but the physical base remains unchanged.
        Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);
        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out _));
        Assert.Equal(new UsnCheckpoint(1, 100),
            ContentIndexGenerationSerializer.TryReadManifest(baseDir!)!.FreshnessCheckpoint);
    }

    [Fact]
    public void TryReanchorBaseCheckpoint_InvalidatesQueryModeCache()
    {
        OpenedLayeredIndexCache.Clear();
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "planner content here")));

        var first = store.TryOpenLayered(retainDocuments: false); // query-mode open → cached
        Assert.NotNull(first);
        Assert.Equal(new UsnCheckpoint(1, 100), first!.Base.Manifest.FreshnessCheckpoint);

        Assert.True(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 777)));

        // The gen id / segment ids (the cache key) are unchanged by an in-place rewrite, so re-anchoring MUST
        // evict the cache — otherwise queries keep replaying from the stale (soon-purged) checkpoint.
        var second = store.TryOpenLayered(retainDocuments: false);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(new UsnCheckpoint(1, 777), second!.Base.Manifest.FreshnessCheckpoint);
        OpenedLayeredIndexCache.Clear();
    }

    [Fact]
    public void TryReanchorBaseCheckpoint_NoIndexOrInjectedFailure_ReturnsFalse_AndOomEscapes()
    {
        var store = NewStore();
        Assert.False(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 200)));
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));

        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.ReanchorPointerPublished)
                throw new IOException("injected reanchor failure");
        };
        try
        {
            Assert.False(store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 200)));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }

        store.DeleteScope();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.ReanchorPointerPublished)
                throw new OutOfMemoryException("injected reanchor exhaustion");
        };
        try
        {
            Assert.Throws<OutOfMemoryException>(() =>
                store.TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 300)));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }
    }

    [Fact]
    public void TryOpenLayered_QueryMode_DropsDocumentsButKeepsPostings()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "the planner produces trigram queries")));
        store.PublishSegment(BuildSegment(2, 200, b =>
            b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta segment content here"))));

        // Default open retains each layer's per-document trigram sets (compaction/serialization need them).
        var retained = store.TryOpenLayered();
        Assert.NotNull(retained);
        Assert.NotEmpty(retained!.Base.Documents);
        Assert.NotEmpty(retained.Segments[0].Added.Documents);

        // Query-mode open drops the documents to halve the retained footprint (the accelerator uses this)...
        var queryMode = store.TryOpenLayered(retainDocuments: false);
        Assert.NotNull(queryMode);
        Assert.Empty(queryMode!.Base.Documents);
        Assert.Empty(queryMode.Segments[0].Added.Documents);

        // ...but the postings + alias table survive, so the real query path is unaffected: "planner" still
        // classifies a.txt as a fresh indexed member.
        Assert.True(new ContentIndexQuerySessionAssertHelper(queryMode.Base).IsMember(@"C:\r\a.txt", "planner"));
        Assert.Equal(retained.Base.AliasCount, queryMode.Base.AliasCount);
    }

    [Fact]
    public void TryOpenLayered_QueryMode_CachesAndInvalidatesOnChange()
    {
        OpenedLayeredIndexCache.Clear();
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base content")));

        // Repeated query-mode opens of the SAME unchanged index reuse the cached (immutable) handle instead
        // of re-deserializing base + segments — the fix for the per-search memory spike on repeated searches.
        var first = store.TryOpenLayered(retainDocuments: false);
        var second = store.TryOpenLayered(retainDocuments: false);
        Assert.NotNull(first);
        Assert.Same(first, second);

        // Appending a segment changes the pointer signature → the next open must NOT serve the stale handle.
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"))));
        var third = store.TryOpenLayered(retainDocuments: false);
        Assert.NotSame(first, third);
        Assert.Single(third!.Segments);

        // The retain path (compaction/serialization) is never served from the query cache.
        var retained = store.TryOpenLayered(retainDocuments: true);
        Assert.NotSame(third, retained);
        OpenedLayeredIndexCache.Clear();
    }

    [Fact]
    public void IsCurrentLayeredIndexCached_TracksQueryModeCacheForCurrentSignature()
    {
        OpenedLayeredIndexCache.Clear();
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base content")));

        // Cold: nothing deserialized in the query cache yet → a search should live-scan + warm.
        Assert.False(store.IsCurrentLayeredIndexCached());

        // A query-mode open populates the cache for the current pointer signature.
        Assert.NotNull(store.TryOpenLayered(retainDocuments: false));
        Assert.True(store.IsCurrentLayeredIndexCached());

        // Appending a segment changes the signature → the prior cache entry no longer matches.
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"))));
        Assert.False(store.IsCurrentLayeredIndexCached());

        // Re-opening the (new) layered index warms it again.
        Assert.NotNull(store.TryOpenLayered(retainDocuments: false));
        Assert.True(store.IsCurrentLayeredIndexCached());

        // The retain path (compaction/serialization) never populates the query cache.
        OpenedLayeredIndexCache.Clear();
        Assert.NotNull(store.TryOpenLayered(retainDocuments: true));
        Assert.False(store.IsCurrentLayeredIndexCached());
        OpenedLayeredIndexCache.Clear();
    }

    [Fact]
    public void CacheAndSizeProbes_FailOpenForIo_ButDoNotSwallowOom()
    {
        OpenedLayeredIndexCache.Clear();
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));

        store.BeforeCacheWarmthCheck = () => throw new IOException("cache probe");
        Assert.False(store.IsCurrentLayeredIndexCached());
        store.BeforeCacheWarmthCheck = () => throw new OutOfMemoryException("cache probe");
        Assert.Throws<OutOfMemoryException>(() => store.IsCurrentLayeredIndexCached());
        store.BeforeCacheWarmthCheck = null;

        store.DirectorySizeReader = _ => throw new IOException("size probe");
        Assert.Equal(0, store.GetCurrentLayeredIndexSizeBytes());
        store.DirectorySizeReader = _ => throw new OutOfMemoryException("size probe");
        Assert.Throws<OutOfMemoryException>(() => store.GetCurrentLayeredIndexSizeBytes());
        store.DirectorySizeReader = ContentIndexStore.DirectorySizeBytes;

        store.MappedQuerySizeReader = _ => throw new IOException("mapped probe");
        Assert.Equal(0, store.GetCurrentLayeredMappedQuerySizeBytes());
        store.MappedQuerySizeReader = _ => throw new OutOfMemoryException("mapped probe");
        Assert.Throws<OutOfMemoryException>(() => store.GetCurrentLayeredMappedQuerySizeBytes());
        OpenedLayeredIndexCache.Clear();
    }

    [Fact]
    public void QueryCache_ReplacesSameScopeEvictsOtherScopeAndHandlesMisses()
    {
        OpenedLayeredIndexCache.Clear();
        var store = NewStore();
        Assert.False(store.IsCurrentLayeredIndexCached());
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        ContentIndexStore.LayeredIndexHandle handle = store.TryOpenLayered()!;

        OpenedLayeredIndexCache.Store("scope-a", "one", handle);
        OpenedLayeredIndexCache.Store("scope-a", "two", handle);
        Assert.Same(handle, OpenedLayeredIndexCache.TryGet("scope-a", "two"));

        OpenedLayeredIndexCache.Store("scope-b", "three", handle);
        Assert.Null(OpenedLayeredIndexCache.TryGet("scope-a", "two"));
        Assert.Same(handle, OpenedLayeredIndexCache.TryGet("scope-b", "three"));
        OpenedLayeredIndexCache.Remove("missing-scope");
        OpenedLayeredIndexCache.Remove("SCOPE-B");
        Assert.Null(OpenedLayeredIndexCache.TryGet("scope-b", "three"));
        OpenedLayeredIndexCache.Clear();
    }

    [Fact]
    public void Retention_LeavesUnknownSegmentDirectoriesUntouched()
    {
        var store = NewStore();
        string unknown = Path.Combine(store.ScopeDirectory, "segments", "external-data");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.bin"), "keep");

        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));

        Assert.True(Directory.Exists(unknown));
        Assert.True(File.Exists(Path.Combine(unknown, "keep.bin")));
    }

    [Fact]
    public void GetCurrentLayeredIndexSizeBytes_CountsBasePlusSegments_AndIsZeroWhenEmpty()
    {
        var store = NewStore();

        // No trusted slot yet → 0 (the size gate then treats the scope as "no usable index" → live-scan).
        Assert.Equal(0, store.GetCurrentLayeredIndexSizeBytes());

        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base content")));
        long baseOnly = store.GetCurrentLayeredIndexSizeBytes();
        Assert.True(baseOnly > 0, "base generation size should be counted");

        // Appending a segment must grow the reported on-disk size (base + segment).
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta added"))));
        long withSegment = store.GetCurrentLayeredIndexSizeBytes();
        Assert.True(withSegment > baseOnly, "appending a segment should increase the counted size");
    }

    [Fact]
    public void IsScopeWithinInProcessSizeLimit_GatesOnSizeAndZeroMeansNever()
    {
        var store = NewStore();

        // No trusted index yet → not "within limit" (nothing to load) even under a generous cap → live-scan.
        Assert.False(ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(_paths, _root, 2, maxInProcessSizeMB: 4096));

        store.Publish(BuildBase((@"C:\r\a.txt", "alpha base content")));

        // A tiny index is well under a generous cap → allowed to load in-process.
        Assert.True(ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(_paths, _root, 2, maxInProcessSizeMB: 4096));

        // 0 = never load ANY index in-process (always live-scan), even a tiny one.
        Assert.False(ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(_paths, _root, 2, maxInProcessSizeMB: 0));
    }

    [Fact]
    public void WorkerMappedSizeLimit_CountsOnlyActiveV3Files_AndRequiresV3()
    {
        var legacyOnly = NewStore();
        legacyOnly.Publish(BuildBase((@"C:\r\legacy.txt", "legacy payload")));
        Assert.Equal(0, legacyOnly.GetCurrentLayeredMappedQuerySizeBytes());
        Assert.False(ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(
            _paths, _root, 2, maxMappedSizeMB: 4096));

        legacyOnly.DeleteScope();
        var mapped = NewStore();
        mapped.ProduceV3QueryStructures = true;
        mapped.Publish(BuildBase((@"C:\r\a.txt", "alpha mapped payload")));
        long baseMapped = mapped.GetCurrentLayeredMappedQuerySizeBytes();
        Assert.True(baseMapped > 0);
        Assert.True(baseMapped < mapped.GetCurrentLayeredIndexSizeBytes());
        Assert.True(ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(
            _paths, _root, 2, maxMappedSizeMB: 4096));
        Assert.False(ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(
            _paths, _root, 2, maxMappedSizeMB: 0));

        mapped.PublishSegment(BuildSegment(2, 200,
            b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta mapped payload"))));
        Assert.True(mapped.GetCurrentLayeredMappedQuerySizeBytes() > baseMapped);
    }

    [Fact]
    public void MappedSize_MissingSegmentSidecarOrOverflow_ReturnsZero()
    {
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(1, 200,
            b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        Assert.True(store.GetCurrentLayeredMappedQuerySizeBytes() > 0);

        string segmentDir = Path.Combine(store.ScopeDirectory, "segments", "seg-000001");
        File.Delete(Path.Combine(segmentDir, ContentIndexV3Format.PathIndexFile));
        Assert.Equal(0, store.GetCurrentLayeredMappedQuerySizeBytes());

        int calls = 0;
        store.MappedQuerySizeReader = _ => calls++ == 0 ? long.MaxValue : 1;
        Assert.Equal(0, store.GetCurrentLayeredMappedQuerySizeBytes());
    }

    [Fact]
    public void SizeAndActiveCount_NoIndexOrCorruptBases_FallBackThenReturnZero()
    {
        var store = NewStore();
        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Equal(0, store.TotalActiveSegmentBytes());
        Assert.Equal(0, store.TotalActiveIndexBytes());
        Assert.Equal(0, store.GetCurrentLayeredIndexSizeBytes());
        Assert.Equal(0, store.GetCurrentLayeredMappedQuerySizeBytes());

        store.Publish(BuildBase((@"C:\r\old.txt", "old")));
        store.Publish(BuildBase((@"C:\r\new.txt", "new")));
        string generations = Path.Combine(store.ScopeDirectory, "generations");
        File.WriteAllBytes(
            Path.Combine(generations, "gen-000002", ContentIndexGenerationSerializer.ManifestFile),
            new byte[] { 1, 2, 3 });
        store.DirectorySizeReader = directory => Path.GetFileName(directory) == "gen-000001" ? 17 : 99;
        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Equal(0, store.TotalActiveSegmentBytes());
        Assert.Equal(17, store.TotalActiveIndexBytes());
        Assert.Equal(17, store.GetCurrentLayeredIndexSizeBytes());

        File.WriteAllBytes(
            Path.Combine(generations, "gen-000001", ContentIndexGenerationSerializer.ManifestFile),
            new byte[] { 1, 2, 3 });
        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Equal(0, store.TotalActiveSegmentBytes());
        Assert.Equal(0, store.TotalActiveIndexBytes());
        Assert.Equal(0, store.GetCurrentLayeredIndexSizeBytes());
    }

    [Fact]
    public void PublishSegment_MultipleSegments_AreOrderedOldestToNewest()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        store.PublishSegment(BuildSegment(3, 300, b => b.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("gamma"))));

        var handle = store.TryOpenLayered();
        Assert.Equal(2, handle!.Segments.Count);
        Assert.Equal(new UsnCheckpoint(2, 200), handle.Segments[0].FreshnessCheckpoint);
        Assert.Equal(new UsnCheckpoint(3, 300), handle.Segments[1].FreshnessCheckpoint);
        Assert.Equal(2, store.ActiveSegmentCount());
    }

    [Fact]
    public void PublishSegment_WithoutBase_Throws()
    {
        var store = NewStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.PublishSegment(BuildSegment(2, 200, b => b.AddTombstone(@"C:\r\x.txt"))));
    }

    [Fact]
    public void PublishSegment_WhenStagedSegmentFailsValidation_CleansTempAndPreservesBase()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        IndexMutationFaults.OnHit = point =>
        {
            if (point != IndexMutationFaults.SegmentWritten)
                return;

            string segmentRoot = Path.Combine(store.ScopeDirectory, "segments");
            string tempDir = Directory.GetDirectories(segmentRoot, ".seg-*.tmp").Single();
            File.WriteAllBytes(
                Path.Combine(tempDir, ContentIndexGenerationSerializer.ContentFile),
                new byte[] { 1, 2, 3 });
        };
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                store.PublishSegment(BuildSegment(1, 200,
                    b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta")))));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }

        var current = store.TryOpenLayered();
        Assert.NotNull(current);
        Assert.Empty(current!.Segments);
        string segments = Path.Combine(store.ScopeDirectory, "segments");
        Assert.Empty(Directory.GetDirectories(segments));
    }

    // ── Cheap current-layer directory enumeration (large-scope worker query) ──

    [Fact]
    public void TryGetCurrentLayerDirectories_ReturnsBaseAndSegments_MatchingTryOpenLayered()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        store.PublishSegment(BuildSegment(3, 300, b => b.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("gamma"))));

        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        Assert.Equal(handle!.BaseDir, baseDir);
        Assert.Equal(handle.SegmentDirs, segmentDirs); // oldest → newest, same order as the layered open
        Assert.True(Directory.Exists(baseDir));
        Assert.All(segmentDirs, d => Assert.True(Directory.Exists(d)));
    }

    [Fact]
    public void TryGetCurrentLayerDirectories_BaseOnly_ReturnsEmptySegments()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        Assert.NotNull(baseDir);
        Assert.Empty(segmentDirs);
    }

    [Fact]
    public void TryGetCurrentLayerDirectories_NoIndex_ReturnsFalse()
    {
        var store = NewStore();

        Assert.False(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        Assert.Null(baseDir);
        Assert.Empty(segmentDirs);
    }

    [Fact]
    public void TryGetCurrentLayerDirectories_MissingSegmentDir_FallsBackToOlderBaseOnlyPointer()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha"))); // slot A = {base, []}
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta")))); // slot B = {base, [seg]}

        // Delete the segment directory the newest slot references → that slot is skipped, the older
        // base-only slot is used instead (the worker never maps a torn layer set).
        string segDir = Directory.GetDirectories(Path.Combine(_paths.GetScopeDirectory(_scopeId), "segments"), "seg-*").Single();
        Directory.Delete(segDir, recursive: true);

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        Assert.NotNull(baseDir);
        Assert.Empty(segmentDirs);
    }

    [Fact]
    public void TryGetCurrentLayerDirectories_MissingNewestBase_FallsBackToOlderBase()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\old.txt", "old")));
        store.Publish(BuildBase((@"C:\r\new.txt", "new")));
        Directory.Delete(Path.Combine(store.ScopeDirectory, "generations", "gen-000002"), recursive: true);

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        Assert.EndsWith("gen-000001", baseDir, StringComparison.Ordinal);
        Assert.Empty(segmentDirs);
    }

    [Fact]
    public void Publish_NewBase_ResetsSegmentList()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        Assert.Equal(1, store.ActiveSegmentCount());

        store.Publish(BuildBase((@"C:\r\a.txt", "alpha"), (@"C:\r\b.txt", "beta")));
        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Empty(store.TryOpenLayered()!.Segments);
    }

    [Fact]
    public void TryOpenLayered_CorruptNewestSegment_FallsBackToOlderBaseOnlyPointer()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha"))); // slot A = {base, []}
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta")))); // slot B = {base, [seg]}

        CorruptFirstSegmentContent();

        // The newest slot (with the corrupt segment) is skipped; the older base-only slot is used (safe).
        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.Empty(handle!.Segments);
        Assert.Equal(1, handle.Base.AliasCount);
    }

    [Fact]
    public void TryOpenLayered_AllPointersReferenceCorruptSegment_ReturnsNull()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        // Two segments so BOTH pointer slots reference seg-000001.
        store.PublishSegment(BuildSegment(2, 200, b => b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"))));
        store.PublishSegment(BuildSegment(3, 300, b => b.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("gamma"))));

        CorruptFirstSegmentContent(); // seg-000001 is referenced by both slots → no trusted layered view

        Assert.Null(store.TryOpenLayered());
    }

    private void CorruptFirstSegmentContent()
    {
        string segContent = Directory.GetDirectories(Path.Combine(_paths.GetScopeDirectory(_scopeId), "segments"), "seg-*")
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, ContentIndexGenerationSerializer.ContentFile)).First();
        byte[] bytes = File.ReadAllBytes(segContent);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(segContent, bytes);
    }

    // ── Legacy pointer compatibility ──

    [Fact]
    public void LegacyPointer_WithoutSegmentLine_StillOpens()
    {
        var store = NewStore();
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));

        // Rewrite the pointer slots in the legacy 3-line format (no segment line) with a valid digest.
        RewriteSlotsLegacy();

        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.Empty(handle!.Segments);
    }

    // ── Compaction triggers + fold ──

    [Fact]
    public void Compact_BaseOnlyLegacyManifest_UsesBaseCheckpointAndBuildTimeAsCreationTime()
    {
        DateTimeOffset builtUtc = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        ContentIndexGeneration current = BuildBase((@"C:\r\a.txt", "alpha"));
        var legacyBase = new ContentIndexGeneration(
            current.Manifest with { BuiltUtc = builtUtc, CreatedUtc = null },
            current.Postings,
            current.Aliases.ToDictionary(),
            current.Report,
            current.Documents,
            current.ContentIdentities);
        var handle = new ContentIndexStore.LayeredIndexHandle(legacyBase, string.Empty, [], []);

        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(
            handle, OpenPolicy, builtUtc.AddHours(1));

        Assert.Equal(legacyBase.Manifest.FreshnessCheckpoint, compacted.Manifest.FreshnessCheckpoint);
        Assert.Equal(builtUtc, compacted.Manifest.CreatedUtc);
    }

    [Fact]
    public void Compact_PreservesValidatedVolumeBinding()
    {
        VolumeBinding? captured = VolumeBindingReader.TryCapture(_root);
        Assert.True(captured.HasValue);
        VolumeBinding binding = captured.Value;
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        builder.SeedVolumeBinding(binding);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha"));
        ContentIndexGeneration generation = builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        var handle = new ContentIndexStore.LayeredIndexHandle(generation, string.Empty, [], []);

        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(
            handle, OpenPolicy, DateTimeOffset.UtcNow);

        Assert.Equal(binding.VolumeGuidPath, compacted.Manifest.VolumeGuidPath);
        Assert.Equal(binding.VolumeSerialNumber, compacted.Manifest.VolumeSerialNumber);
        Assert.Equal(binding.FileSystemName, compacted.Manifest.FileSystemName);
        Assert.Equal(binding.RootRelativePath, compacted.Manifest.VolumeRelativeRootPath);
    }

    [Fact]
    public void Compact_OlderSegmentUpdate_DoesNotReplaceNewerBaseUpdate()
    {
        DateTimeOffset latest = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        baseBuilder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha"));
        ContentIndexGeneration baseGeneration = baseBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), latest,
            lastIncrementalUpdateUtc: latest);

        var segmentBuilder = new ContentIndexGenerationBuilder(OpenPolicy);
        segmentBuilder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"));
        ContentIndexGeneration segmentGeneration = segmentBuilder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 200), latest.AddMinutes(-5),
            lastIncrementalUpdateUtc: latest.AddMinutes(-5));
        var segment = new ContentIndexDeltaSegment(segmentGeneration, []);
        var handle = new ContentIndexStore.LayeredIndexHandle(
            baseGeneration, string.Empty, [segment], [string.Empty]);

        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(
            handle, OpenPolicy, latest.AddHours(1));

        Assert.Equal(latest, compacted.Manifest.LastIncrementalUpdateUtc);
    }

    [Fact]
    public void ShouldCompact_TriggersOnSegmentCount()
    {
        var store = NewStore(retained: 4);
        store.Publish(BuildBase((@"C:\r\a.txt", "alpha")));
        for (int i = 0; i < 3; i++)
            store.PublishSegment(BuildSegment((ulong)(2 + i), 200 + i, b => b.AddChangedDocument($@"C:\r\s{i}.txt", Encoding.UTF8.GetBytes($"seg doc {i}"))));

        Assert.False(store.ShouldCompact(maxDeltaSegments: 8, compactionThresholdMB: 256));
        Assert.True(store.ShouldCompact(maxDeltaSegments: 2, compactionThresholdMB: 256)); // 3 > 2
    }

    [Fact]
    public void Compact_FoldsLayeredIntoBase_PreservingNewestFirstSemantics()
    {
        var store = NewStore(retained: 4);
        // base: a.txt (has "planner"), gone.txt (will be deleted).
        store.Publish(BuildBase(
            (@"C:\r\a.txt", "the planner produces trigram queries"),
            (@"C:\r\gone.txt", "delete me later")));
        // segment: replace a.txt with non-matching content, add b.txt, tombstone gone.txt.
        store.PublishSegment(BuildSegment(2, 200, b =>
        {
            b.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest"));
            b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("the planner is over here now"));
            b.AddTombstone(@"C:\r\gone.txt");
        }));

        var handle = store.TryOpenLayered();
        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(handle!, OpenPolicy, DateTimeOffset.UtcNow);

        // gone.txt removed; a.txt + b.txt survive; checkpoint is the newest layer's.
        Assert.Equal(2, compacted.AliasCount);
        Assert.False(compacted.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt"), out _, out _));
        Assert.True(compacted.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt"), out _, out _));
        Assert.Equal(new UsnCheckpoint(2, 200), compacted.Manifest.FreshnessCheckpoint);

        // Publishing the compacted base resets segments and drops the now-orphaned segment dir.
        store.Compact(compacted);
        Assert.Equal(0, store.ActiveSegmentCount());
        // "planner" now matches b.txt in the compacted base and NOT a.txt (segment shadow was folded in).
        var post = new ContentIndexQuerySessionAssertHelper(compacted);
        Assert.True(post.IsMember(@"C:\r\b.txt", "planner"));
        Assert.False(post.IsMember(@"C:\r\a.txt", "planner"));
    }

    [Fact]
    public void Compact_PreservesHardLinks_SharedContentAcrossPaths()
    {
        var store = NewStore(retained: 4);

        // A base whose two paths hard-link to one content id (a.txt + link.txt share content 0).
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        long contentId = builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("shared trigram content here"));
        builder.AddHardLink(@"C:\r\link.txt", contentId);
        store.Publish(builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));

        // A trivial segment so the store is layered and Compact walks the base via AddLayerDocuments.
        store.PublishSegment(BuildSegment(2, 200, b =>
            b.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("another doc entirely here"))));

        var handle = store.TryOpenLayered();
        ContentIndexGeneration compacted = ContentIndexCompactor.Compact(handle!, OpenPolicy, DateTimeOffset.UtcNow);

        // Both hard-linked paths survive and resolve to the SAME content id (the hard-link fold branch).
        Assert.True(compacted.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt"), out _, out long cidA));
        Assert.True(compacted.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\link.txt"), out _, out long cidLink));
        Assert.Equal(cidA, cidLink);
    }

    // Small helper to assert membership against a built generation without wiring the full accelerator.
    private sealed class ContentIndexQuerySessionAssertHelper
    {
        private readonly ContentIndexGeneration _gen;
        public ContentIndexQuerySessionAssertHelper(ContentIndexGeneration gen) => _gen = gen;

        public bool IsMember(string path, string term)
        {
            var options = new SearchOptions { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
            var query = ((TrigramPlan.Eligible)TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options))).Query;
            var session = ContentIndexQuerySession.Begin(_gen, query, new DirtyContentSet());
            return session.Classify(IndexScopeIdentity.NormalizePath(path)) is IndexPathClassification.FreshIndexedMember;
        }
    }

    private void RewriteSlotsLegacy()
    {
        // Read the current base generation id from the (new-format) slot, then rewrite both slots in the
        // legacy format so we exercise the backward-compatible read path.
        string scopeDir = _paths.GetScopeDirectory(_scopeId);
        foreach (string slot in new[] { "current.a", "current.b" })
        {
            string path = Path.Combine(scopeDir, slot);
            if (!File.Exists(path))
                continue;
            string[] lines = File.ReadAllText(path).Split('\n', StringSplitOptions.None);
            // new format lines: seq, genId, segCsv, digest
            string seq = lines[0];
            string genId = lines[1];
            string payload = seq + "\n" + genId;
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            File.WriteAllText(path, payload + "\n" + digest + "\n");
        }
    }
}
