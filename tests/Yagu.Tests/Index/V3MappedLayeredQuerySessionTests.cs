using System.Reflection;
using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="V3MappedLayeredQuerySession"/> — the memory-mapped, tombstone-aware layered
/// classifier the worker's Stage-2 shadow mode uses over a base + segments (plan §5.6, §6 Stage 2 slice 3).
/// Its <see cref="V3MappedLayeredQuerySession.Classify"/> must be <b>byte-for-byte identical</b> to the
/// in-process oracle <see cref="LayeredContentIndexQuerySession.Classify"/> for every discovered path —
/// including a segment replacement (newest layer wins), a tombstoned path (shadows older layers), a new
/// segment doc, an absent path, and a dirty-since-layer-build path.
/// </summary>
public sealed class V3MappedLayeredQuerySessionTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";

    public V3MappedLayeredQuerySessionTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-v3-layered", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private static ContentIndexGeneration BuildBase(string root)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(root + "\\b.txt", Encoding.UTF8.GetBytes("beta filler content only"));
        builder.AddDocument(root + "\\c.txt", Encoding.UTF8.GetBytes("gamma content mentions nothing"));
        builder.AddDocument(root + "\\gone.txt", Encoding.UTF8.GetBytes("old removed planner content"));
        return builder.Build("scope", "vol", root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static ContentIndexDeltaSegment BuildSegment(string root)
    {
        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        seg.AddChangedDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("replaced planner content now here")); // replaces base a.txt
        seg.AddChangedDocument(root + "\\new.txt", Encoding.UTF8.GetBytes("a fresh trigram document appears")); // new
        seg.AddTombstone(root + "\\gone.txt"); // removes base gone.txt
        return seg.Build("scope", "vol", root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    private ContentIndexV3Reader WriteAndOpenBase(ContentIndexGeneration gen)
    {
        string dir = Path.Combine(_sandbox, "base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ContentIndexV3Format.Write(dir, gen);
        return ContentIndexV3Format.TryOpen(dir)!;
    }

    private ContentIndexV3Reader WriteAndOpenSegment(ContentIndexDeltaSegment segment)
    {
        string dir = Path.Combine(_sandbox, "seg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ContentIndexV3Format.Write(dir, segment.Added, segment.RemovedPaths);
        return ContentIndexV3Format.TryOpen(dir)!;
    }

    private static string Norm(string root, string file) => IndexScopeIdentity.NormalizePath(root + "\\" + file);

    [Theory]
    [InlineData("planner")]
    [InlineData("trigram")]
    [InlineData("content")]
    [InlineData("filler")]
    public void Classify_MatchesInProcessLayeredOracle_ForEveryDiscoveredPath(string term)
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        var segments = new[] { segment };
        var baseDirty = new DirtyContentSet();
        var segDirties = new[] { new DirtyContentSet() };
        TrigramExpression query = PlanQuery(term);

        LayeredContentIndexQuerySession oracle =
            LayeredContentIndexQuerySession.Begin(baseGen, segments, query, baseDirty, segDirties);

        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        Assert.True(V3MappedLayeredQuerySession.AllLayersHaveTombstoneIndex(baseReader, new[] { segReader }));
        V3MappedLayeredQuerySession mapped =
            V3MappedLayeredQuerySession.Begin(baseReader, new[] { segReader }, query, baseDirty, segDirties);

        Assert.Equal(oracle.CandidateCount, mapped.CandidateCount);
        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "gone.txt", "new.txt", "absent.txt" })
        {
            string norm = Norm(_root, file);
            Assert.Equal(oracle.Classify(norm), mapped.Classify(norm));
        }
    }

    [Fact]
    public void Classify_TombstonedPath_IsUnindexed_EvenThoughTheBaseIndexesIt()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);

        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"), new DirtyContentSet(), new[] { new DirtyContentSet() });

        // gone.txt is a "planner" member of the BASE, but the newer segment tombstones it → live-scan.
        IndexPathClassification verdict = mapped.Classify(Norm(_root, "gone.txt"));
        var unindexed = Assert.IsType<IndexPathClassification.Unindexed>(verdict);
        Assert.Contains("tombstoned", unindexed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_ReplacedPath_TheNewerSegmentLayerDecides()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);

        // Base a.txt contains "trigram"; the segment replaces it with text that does NOT → the newer layer
        // wins, so a.txt is a nonmember of "trigram" (not a member from the shadowed base).
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("trigram"), new DirtyContentSet(), new[] { new DirtyContentSet() });
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(mapped.Classify(Norm(_root, "a.txt")));
        // new.txt (segment-only) DOES contain "trigram" → member.
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(mapped.Classify(Norm(_root, "new.txt")));
    }

    [Fact]
    public void Begin_BuildsFlattenedNewestOwnerRoute_OncePerDistinctPathHash()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);

        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"),
            new DirtyContentSet(), new[] { new DirtyContentSet() });

        // Base hashes: a,b,c,gone. Segment hashes: replacement a, new, tombstone gone. The flattened
        // newest-owner table deduplicates replacement/tombstone hashes, so there are five O(1) routes —
        // not a per-classification walk across both layers.
        Assert.Equal(5, mapped.RoutedPathHashCount);

        // Routing still exact-verifies the selected layer: replacement/tombstone/newest semantics remain.
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(mapped.Classify(Norm(_root, "a.txt")));
        Assert.IsType<IndexPathClassification.Unindexed>(mapped.Classify(Norm(_root, "gone.txt")));
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(mapped.Classify(Norm(_root, "b.txt")));
        Assert.IsType<IndexPathClassification.Unindexed>(mapped.Classify(Norm(_root, "absent.txt")));
    }

    [Fact]
    public void Begin_ReportsLayerRouteAmplificationAndOpenPhaseTimings()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);

        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader,
            new[] { segReader },
            PlanQuery("planner"),
            new DirtyContentSet(),
            new[] { new DirtyContentSet() },
            parallelism: 2);

        Assert.Equal(2, mapped.LayerCount);
        Assert.Equal(6, mapped.PathRecordCount);       // base 4 + segment replacement/new 2
        Assert.Equal(1, mapped.TombstoneRecordCount);  // segment removes gone.txt
        Assert.Equal(7, mapped.RouteRecordCount);
        Assert.Equal(5, mapped.DistinctRouteHashCount);
        Assert.Equal(2, mapped.RouteRecordCount - mapped.DistinctRouteHashCount);
        Assert.True(mapped.CandidatesEvaluatedInWorker);
        Assert.True(mapped.CandidateEvaluationMs >= 0);
        Assert.True(mapped.RoutingIndexMs >= 0);
    }

    [Fact]
    public void Classify_DirtySegmentContent_IsDirtyByUsn_LikeTheOracle()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        // Mark the segment's a.txt content dirty at B0 (segment-local content id from its Added generation).
        Assert.True(segment.Added.TryGetAlias(Norm(_root, "a.txt"), out _, out long aContentId));
        var baseDirty = new DirtyContentSet();
        var segDirty = new DirtyContentSet();
        segDirty.MarkDirty(aContentId);

        LayeredContentIndexQuerySession oracle = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { segment }, PlanQuery("planner"), baseDirty, new[] { segDirty });
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"), baseDirty, new[] { segDirty });

        string norm = Norm(_root, "a.txt");
        Assert.IsType<IndexPathClassification.DirtyByUsn>(mapped.Classify(norm));
        Assert.Equal(oracle.Classify(norm), mapped.Classify(norm));
    }

    [Fact]
    public void ClassifyBatch_Parallel_PreservesOrderAndRecordsPrunesSequentially()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"),
            new DirtyContentSet(), new[] { new DirtyContentSet() }, parallelism: 4);

        string[] distinct = { "a.txt", "b.txt", "c.txt", "gone.txt", "new.txt", "absent.txt" };
        string[] paths = Enumerable.Range(0, 600)
            .Select(i => Norm(_root, distinct[i % distinct.Length]))
            .ToArray();
        IndexPathClassification[] expected = paths.Select(mapped.Classify).ToArray();

        IReadOnlyList<IndexPathClassification> actual = mapped.ClassifyBatch(
            paths, parallelism: 8, recordPruning: true);

        Assert.Equal(expected, actual);
        string[] expectedPruned = paths
            .Zip(expected)
            .Where(pair => pair.Second is IndexPathClassification.FreshIndexedNonmember)
            .Select(pair => pair.First)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPruned, mapped.ProvisionalPaths.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(expectedPruned, mapped.DrainAllProvisional());
    }

    [Fact]
    public void ClassifyBatch_ParallelFailure_DoesNotPartiallyRecordPrunes()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"),
            new DirtyContentSet(), new[] { new DirtyContentSet() }, parallelism: 4);

        string[] paths = { Norm(_root, "b.txt"), null!, Norm(_root, "c.txt") };
        Assert.ThrowsAny<ArgumentNullException>(() => mapped.ClassifyBatch(
            paths, parallelism: 4, recordPruning: true));
        Assert.Equal(0, mapped.ProvisionalCount);
    }

    [Fact]
    public void RunBounded_UsesMultipleLanes_WhenDegreeAllows()
    {
        int active = 0;
        int max = 0;
        V3MappedLayeredQuerySession.RunBounded(16, parallelism: 4, _ =>
        {
            int now = Interlocked.Increment(ref active);
            int observed;
            while (now > (observed = Volatile.Read(ref max))
                   && Interlocked.CompareExchange(ref max, now, observed) != observed)
            {
            }
            try
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref max) >= 2, TimeSpan.FromSeconds(1));
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        Assert.True(max >= 2, $"Expected multiple query lanes, observed {max}.");
    }

    [Fact]
    public void RunBounded_WithNoItems_DoesNotInvokeAction()
    {
        V3MappedLayeredQuerySession.RunBounded(0, parallelism: 4, _ => Assert.Fail("Action must not run."));
    }

    [Fact]
    public void AllLayersHaveTombstoneIndex_IsFalse_WhenASegmentLacksTheTombstoneSidecar()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);

        // Simulate an older 3-file segment v3 (no tombstone sidecar) → the mapped layered path must fall back.
        string dir = Path.Combine(_sandbox, "seg-legacy");
        Directory.CreateDirectory(dir);
        ContentIndexV3Format.Write(dir, segment.Added, segment.RemovedPaths);
        File.Delete(Path.Combine(dir, ContentIndexV3Format.TombstonesFile));
        using ContentIndexV3Reader legacySeg = ContentIndexV3Format.TryOpen(dir)!;

        Assert.False(legacySeg.HasTombstoneIndex);
        Assert.False(V3MappedLayeredQuerySession.AllLayersHaveTombstoneIndex(baseReader, new[] { legacySeg }));
    }

    [Fact]
    public void AllLayersHaveTombstoneIndex_IsFalse_WhenBaseLacksTheTombstoneSidecar()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        string dir = Path.Combine(_sandbox, "base-legacy");
        Directory.CreateDirectory(dir);
        ContentIndexV3Format.Write(dir, baseGen);
        File.Delete(Path.Combine(dir, ContentIndexV3Format.TombstonesFile));
        using ContentIndexV3Reader legacyBase = ContentIndexV3Format.TryOpen(dir)!;

        Assert.False(legacyBase.HasTombstoneIndex);
        Assert.False(V3MappedLayeredQuerySession.AllLayersHaveTombstoneIndex(
            legacyBase,
            Array.Empty<ContentIndexV3Reader>()));
    }

    [Fact]
    public void BeginWithCandidates_ClassifiesIdenticallyToBegin_AndRejectsMismatchedCounts()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        TrigramExpression query = PlanQuery("content");
        var baseDirty = new DirtyContentSet();
        var segDirties = new[] { new DirtyContentSet() };

        V3MappedLayeredQuerySession viaBegin = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, query, baseDirty, segDirties);
        V3MappedLayeredQuerySession viaCandidates = V3MappedLayeredQuerySession.BeginWithCandidates(
            baseReader, new[] { segReader }, baseReader.EvaluateSet(query),
            new[] { segReader.EvaluateSet(query) }, baseDirty, segDirties);

        foreach (string file in new[] { "a.txt", "b.txt", "gone.txt", "new.txt" })
        {
            string norm = Norm(_root, file);
            Assert.Equal(viaBegin.Classify(norm), viaCandidates.Classify(norm));
        }

        Assert.Throws<ArgumentException>(() => V3MappedLayeredQuerySession.BeginWithCandidates(
            baseReader, new[] { segReader }, baseReader.EvaluateSet(query),
            System.Array.Empty<IReadOnlySet<int>>(), baseDirty, segDirties));

        Assert.Throws<ArgumentException>(() => V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, query, baseDirty,
            Array.Empty<DirtyContentSet>()));
    }

    [Fact]
    public void Classify_LongAbsentPath_UsesPooledUtf8Buffer()
    {
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(BuildBase(_root));
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, Array.Empty<ContentIndexV3Reader>(), PlanQuery("planner"),
            new DirtyContentSet(), Array.Empty<DirtyContentSet>());

        Assert.IsType<IndexPathClassification.Unindexed>(
            mapped.Classify(_root + "\\" + new string('x', 600)));
    }

    [Fact]
    public void Classify_RoutingHashCollision_FailsSafeToUnindexed()
    {
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(BuildBase(_root));
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, Array.Empty<ContentIndexV3Reader>(), PlanQuery("planner"),
            new DirtyContentSet(), Array.Empty<DirtyContentSet>());
        string absent = Norm(_root, "collision.txt");
        var owners = (Dictionary<ulong, int>)typeof(V3MappedLayeredQuerySession)
            .GetField("_newestOwnerLayerByHash", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(mapped)!;
        owners[V3Fnv.Hash(Encoding.UTF8.GetBytes(absent))] = 0;

        var result = Assert.IsType<IndexPathClassification.Unindexed>(mapped.Classify(absent));
        Assert.Contains("collision", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_NonmemberWithoutCapturedIdentity_FailsSafeToDirty()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: _ => null);
        builder.AddDocument(_root + "\\no-id.txt", Encoding.UTF8.GetBytes("ordinary indexed text"));
        ContentIndexGeneration generation = builder.Build(
            "scope", "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(generation);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, Array.Empty<ContentIndexV3Reader>(), PlanQuery("planner"),
            new DirtyContentSet(), Array.Empty<DirtyContentSet>());

        var result = Assert.IsType<IndexPathClassification.DirtyByUsn>(
            mapped.Classify(Norm(_root, "no-id.txt")));
        Assert.Contains("no captured file identity", result.Reason, StringComparison.Ordinal);
    }

    // ── Stage-4 pruning primitive (RouteForPruning / ReconcileAtB1 / DrainAllProvisional) ──

    [Theory]
    [InlineData("planner")]
    [InlineData("trigram")]
    [InlineData("content")]
    [InlineData("filler")]
    public void RouteForPruning_PrunesExactlyTheFreshNonmembers_LikeTheOracle(string term)
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        var baseDirty = new DirtyContentSet();
        var segDirties = new[] { new DirtyContentSet() };
        TrigramExpression query = PlanQuery(term);

        LayeredContentIndexQuerySession oracle =
            LayeredContentIndexQuerySession.Begin(baseGen, new[] { segment }, query, baseDirty, segDirties);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped =
            V3MappedLayeredQuerySession.Begin(baseReader, new[] { segReader }, query, baseDirty, segDirties);

        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "gone.txt", "new.txt", "absent.txt" })
        {
            string norm = Norm(_root, file);
            bool pruned = mapped.RouteForPruning(norm, out IndexPathClassification classification);

            // RouteForPruning prunes iff the shared classification is a fresh posting nonmember …
            Assert.Equal(classification is IndexPathClassification.FreshIndexedNonmember, pruned);
            // … the reported classification equals the pure Classify …
            Assert.Equal(mapped.Classify(norm), classification);
            // … and that matches the in-process oracle's decision.
            Assert.Equal(oracle.Route(norm) is PathDecision.ProvisionalPrune, pruned);
        }

        // Every prunable path is now provisionally held (keyed by its own normalized path).
        Assert.Equal(mapped.ProvisionalCount, mapped.ProvisionalPaths.Count);
    }

    [Fact]
    public void ReconcileAtB1_RescuesOnlyProvisionalPathsWhoseLayerContentBecameDirty_LikeTheOracle()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        var baseDirtyB0 = new DirtyContentSet();
        var segDirtiesB0 = new[] { new DirtyContentSet() };
        TrigramExpression query = PlanQuery("planner");

        LayeredContentIndexQuerySession oracle =
            LayeredContentIndexQuerySession.Begin(baseGen, new[] { segment }, query, baseDirtyB0, segDirtiesB0);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped =
            V3MappedLayeredQuerySession.Begin(baseReader, new[] { segReader }, query, baseDirtyB0, segDirtiesB0);

        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "gone.txt", "new.txt", "absent.txt" })
        {
            string norm = Norm(_root, file);
            mapped.RouteForPruning(norm, out _);
            oracle.Route(norm);
        }

        // b.txt is a base-layer nonmember; dirty its BASE content id over [B0, B1). new.txt is a
        // segment-layer nonmember and must NOT be rescued when only the base content is dirty.
        Assert.True(baseGen.TryGetAlias(Norm(_root, "b.txt"), out _, out long bBaseContentId));
        var baseDirtyB1 = new DirtyContentSet();
        baseDirtyB1.MarkDirty(bBaseContentId);
        var segDirtiesB1 = new[] { new DirtyContentSet() };

        IReadOnlyList<string> mappedRescued = mapped.ReconcileAtB1(baseDirtyB1, segDirtiesB1);
        IReadOnlyList<long> oracleAliases = oracle.ReconcileAtB1(baseDirtyB1, segDirtiesB1);
        IReadOnlyList<string> oracleRescued = oracle.ResolveAliasPaths(oracleAliases);

        Assert.Equal(new[] { Norm(_root, "b.txt") }, mappedRescued);
        Assert.Equal(oracleRescued, mappedRescued); // both sorted ascending Ordinal
        // The rescued path leaves the provisional set; the other nonmembers stay pruned.
        Assert.DoesNotContain(Norm(_root, "b.txt"), mapped.ProvisionalPaths);
        Assert.Contains(Norm(_root, "c.txt"), mapped.ProvisionalPaths);
        Assert.Contains(Norm(_root, "new.txt"), mapped.ProvisionalPaths);
    }

    [Fact]
    public void ReconcileAtB1_RescuesASegmentLayerNonmemberWhenOnlyTheSegmentContentIsDirty()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"), new DirtyContentSet(), new[] { new DirtyContentSet() });

        // new.txt is a segment-only nonmember of "planner".
        Assert.True(mapped.RouteForPruning(Norm(_root, "new.txt"), out _));
        Assert.True(mapped.RouteForPruning(Norm(_root, "b.txt"), out _)); // base-layer nonmember

        // Dirty new.txt's SEGMENT content id → only new.txt rescues; the base b.txt stays pruned.
        Assert.True(segment.Added.TryGetAlias(Norm(_root, "new.txt"), out _, out long newSegContentId));
        var segDirtyB1 = new DirtyContentSet();
        segDirtyB1.MarkDirty(newSegContentId);

        IReadOnlyList<string> rescued = mapped.ReconcileAtB1(new DirtyContentSet(), new[] { segDirtyB1 });
        Assert.Equal(new[] { Norm(_root, "new.txt") }, rescued);
        Assert.Contains(Norm(_root, "b.txt"), mapped.ProvisionalPaths);
    }

    [Fact]
    public void ReconcileAtB1_Quiescent_RescuesNothing_AndDrainAllProvisionalReleasesTheRest()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"), new DirtyContentSet(), new[] { new DirtyContentSet() });

        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "new.txt" })
            mapped.RouteForPruning(Norm(_root, file), out _);
        int prunedBefore = mapped.ProvisionalCount;
        Assert.True(prunedBefore > 0);

        // A quiescent B1 (nothing dirtied) rescues nothing and leaves every prune in place.
        IReadOnlyList<string> rescued = mapped.ReconcileAtB1(new DirtyContentSet(), new[] { new DirtyContentSet() });
        Assert.Empty(rescued);
        Assert.Equal(prunedBefore, mapped.ProvisionalCount);

        // The fail-safe total rescue returns every remaining provisional path and empties the set.
        IReadOnlyList<string> all = mapped.DrainAllProvisional();
        Assert.Equal(prunedBefore, all.Count);
        Assert.Equal(0, mapped.ProvisionalCount);
        Assert.Empty(mapped.ProvisionalPaths);
    }

    [Fact]
    public void ReconcileAtB1_RejectsASegmentDirtyCountThatIsNotOneToOneWithTheSegmentLayers()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        ContentIndexDeltaSegment segment = BuildSegment(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        using ContentIndexV3Reader segReader = WriteAndOpenSegment(segment);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, new[] { segReader }, PlanQuery("planner"), new DirtyContentSet(), new[] { new DirtyContentSet() });

        // One segment layer → exactly one segment dirty set is required.
        Assert.Throws<ArgumentException>(() =>
            mapped.ReconcileAtB1(new DirtyContentSet(), System.Array.Empty<DirtyContentSet>()));
    }

    [Fact]
    public void RouteForPruning_AndReconcile_WorkOnABaseOnlyScope()
    {
        ContentIndexGeneration baseGen = BuildBase(_root);
        using ContentIndexV3Reader baseReader = WriteAndOpenBase(baseGen);
        V3MappedLayeredQuerySession mapped = V3MappedLayeredQuerySession.Begin(
            baseReader, System.Array.Empty<ContentIndexV3Reader>(), PlanQuery("planner"),
            new DirtyContentSet(), System.Array.Empty<DirtyContentSet>());

        // Base "planner": a.txt + gone.txt are members; b.txt + c.txt are nonmembers (no segment layer).
        Assert.False(mapped.RouteForPruning(Norm(_root, "a.txt"), out _));
        Assert.True(mapped.RouteForPruning(Norm(_root, "b.txt"), out _));
        Assert.True(mapped.RouteForPruning(Norm(_root, "c.txt"), out _));
        Assert.Equal(2, mapped.ProvisionalCount);

        Assert.True(baseGen.TryGetAlias(Norm(_root, "b.txt"), out _, out long bId));
        var b1 = new DirtyContentSet();
        b1.MarkDirty(bId);
        IReadOnlyList<string> rescued = mapped.ReconcileAtB1(b1, System.Array.Empty<DirtyContentSet>());
        Assert.Equal(new[] { Norm(_root, "b.txt") }, rescued);
        Assert.Equal(1, mapped.ProvisionalCount);
        Assert.Contains(Norm(_root, "c.txt"), mapped.ProvisionalPaths);
    }
}
