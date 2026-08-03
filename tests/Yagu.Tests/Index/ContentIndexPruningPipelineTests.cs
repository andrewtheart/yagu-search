using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the Stage-4 <see cref="ContentIndexPruningPipeline"/> — the pipeline that actually skips the
/// files a required-superset trigram query cannot match. It must forward every non-pruned path to the survivor
/// sink, record every fresh nonmember to the recovery spool (and NOT scan it), rescue the dirty subset at B1,
/// and — under ANY worker fault — fail safe by forwarding/replaying so the result multiset equals a live scan.
/// The plumbing + fault behavior is driven against the <c>Yagu.FakeIndexWorker</c>; the real prune/rescue
/// correctness (member vs nonmember vs dirty routing) is proven against the real bundled worker vs the
/// in-process layered oracle. Both self-gate when their worker binary is not built.
/// </summary>
public sealed class ContentIndexPruningPipelineTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-prune-pipe", Guid.NewGuid().ToString("N"));

    public ContentIndexPruningPipelineTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static readonly IReadOnlySet<long> NoDirty = new HashSet<long>();
    private static readonly IReadOnlyList<IReadOnlySet<long>> NoSegmentDirties = Array.Empty<IReadOnlySet<long>>();

    private static readonly string[] Offered =
    {
        IndexScopeIdentity.NormalizePath(@"C:\pp\a.txt"),
        IndexScopeIdentity.NormalizePath(@"C:\pp\b.txt"),
        IndexScopeIdentity.NormalizePath(@"C:\pp\c.txt"),
    };

    private ContentIndexClassifyBatcher SmallBatcher()
        => new(maxPaths: 2, maxEncodedBytes: 1_000_000, maxLatency: TimeSpan.FromMilliseconds(20));

    private ContentIndexPruningPipeline NewPipeline(IndexWorkerClient client, ContentIndexRecoverySpool spool, ConcurrentQueue<string> survivors)
        => new(client, spool, SmallBatcher(),
            (path, _) => { survivors.Enqueue(path); return ValueTask.CompletedTask; },
            sessionId: 1, TimeSpan.FromMilliseconds(20), channelCapacity: 64);

    [Fact]
    public void ResultValues_ClampNetPrunedAndExposeNotAccelerated()
    {
        Assert.Equal(2, new PruningScanResult(true, Array.Empty<string>(), 5, 3).NetPruned);
        Assert.Equal(0, new PruningScanResult(true, Array.Empty<string>(), 3, 5).NetPruned);
        Assert.False(PruningScanResult.NotAccelerated.Accelerated);
        Assert.Empty(PruningScanResult.NotAccelerated.RescuePaths);
    }

    [Fact]
    public async Task ConstructorAndUnopenedOperations_ValidateAndRemainNoOps()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        ContentIndexClassifyBatcher batcher = SmallBatcher();
        static ValueTask Sink(string _, CancellationToken __) => ValueTask.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new ContentIndexPruningPipeline(
            null!, spool, batcher, Sink, 1, TimeSpan.FromMilliseconds(20), 1));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexPruningPipeline(
            client, null!, batcher, Sink, 1, TimeSpan.FromMilliseconds(20), 1));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexPruningPipeline(
            client, spool, null!, Sink, 1, TimeSpan.FromMilliseconds(20), 1));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexPruningPipeline(
            client, spool, batcher, null!, 1, TimeSpan.FromMilliseconds(20), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentIndexPruningPipeline(
            client, spool, batcher, Sink, 1, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentIndexPruningPipeline(
            client, spool, batcher, Sink, 1, TimeSpan.FromMilliseconds(20), 0));

        var pipeline = new ContentIndexPruningPipeline(
            client, spool, batcher, Sink, 1, TimeSpan.FromMilliseconds(20), 1);
        Assert.False(pipeline.WasIndexMember(null!));
        await pipeline.OfferAsync("scan", "classify", CancellationToken.None);
        await pipeline.CompleteOfferingAsync();
        await pipeline.CleanupAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.OpenAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            pipeline.ReconcileAtB1Async(null!, NoSegmentDirties, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            pipeline.ReconcileAtB1Async(NoDirty, null!, true, CancellationToken.None));

        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, true, CancellationToken.None);
        Assert.False(outcome.Accelerated);
        Assert.Equal("not opened", outcome.BypassReason);
    }

    // ── Fake worker: plumbing + fault behavior ──

    [Fact]
    public async Task AllNonmembers_ArePruned_NoneForwardedToScan_ThenReconcileRescuesNone()
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker("normal"));
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = NewPipeline(client, spool, survivors);

        Assert.True(await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None));
        Assert.NotNull(pipeline.OpenDiagnostics);
        Assert.Equal(3, pipeline.OpenDiagnostics!.LayerCount);
        Assert.Equal(125, pipeline.OpenDiagnostics.RouteRecordCount);
        Assert.Equal(25, pipeline.OpenDiagnostics.SupersededRouteRecordCount);
        Assert.Equal(1.25, pipeline.OpenDiagnostics.RouteRecordAmplification, precision: 3);
        Assert.True(pipeline.OpenDiagnostics.HostRoundTripMs >= 0);
        foreach (string path in Offered)
            await pipeline.OfferAsync(path, path, CancellationToken.None);
        await pipeline.CompleteOfferingAsync();
        await pipeline.CompleteOfferingAsync();

        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        ContentIndexPruningPipeline.PruningPipelineOutcome repeated =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);

        // The fake classifies every path as a fresh Nonmember → each is pruned (spooled), none is scanned; a
        // clean certain reconcile rescues nothing.
        Assert.True(outcome.Accelerated);
        Assert.Equal(Offered.Length, outcome.Offered);
        Assert.Equal(Offered.Length, outcome.GrossPruned);
        Assert.Equal(0, outcome.Rescued);
        Assert.Equal(Offered.Length, outcome.NetPruned);
        Assert.Equal(outcome, repeated);
        Assert.Empty(survivors);
        Assert.False(File.Exists(spool.FilePath)); // spool completed (deleted) after reconcile
        Assert.Same(pipeline.CleanupAsync(), pipeline.CleanupAsync());
    }

    [Fact]
    public async Task NonAccelerableOpen_PreservesWorkerBypassReason()
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker("queryOpenNotReady"));
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = NewPipeline(client, spool, survivors);

        Assert.False(await pipeline.OpenAsync(
            new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None));
        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, true, CancellationToken.None);
        Assert.Equal("fake not-ready", outcome.BypassReason);
    }

    [Theory]
    [InlineData("classifyCrash")]
    [InlineData("classifyMalformed")]
    public async Task ClassifyFault_ForwardsEveryOfferedPathToScan_PrunesNothing(string scenario)
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker(scenario));
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = NewPipeline(client, spool, survivors);

        Assert.True(await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None));
        foreach (string path in Offered)
            await pipeline.OfferAsync(path, path, CancellationToken.None);
        await pipeline.CompleteOfferingAsync();

        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);

        // A classify fault → the pipeline cannot prune → every offered path is forwarded to the survivor sink
        // (scanned), nothing is spooled or rescued → same result multiset as a live scan.
        Assert.False(outcome.Accelerated);
        Assert.Equal(0, outcome.GrossPruned);
        Assert.Empty(outcome.RescuePaths);
        Assert.Equal(Offered.OrderBy(p => p, StringComparer.Ordinal), survivors.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReconcileFault_ReplaysWholeSpool_SoEveryPrunedPathIsRescued()
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker("reconcileCrash"));
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = NewPipeline(client, spool, survivors);

        Assert.True(await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None));
        foreach (string path in Offered)
            await pipeline.OfferAsync(path, path, CancellationToken.None);
        await pipeline.CompleteOfferingAsync();

        // Classify succeeded (every path spooled as a would-prune), but reconcileB1 crashes → the whole spool
        // is replayed so every provisionally-pruned path is returned for rescue. Nothing is lost.
        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);

        Assert.Equal(Offered.Length, outcome.GrossPruned);
        Assert.Equal(Offered.Length, outcome.Rescued);
        Assert.Equal(0, outcome.NetPruned);
        Assert.False(outcome.PruningCertain);
        Assert.Equal(Offered.OrderBy(p => p, StringComparer.Ordinal), outcome.RescuePaths.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Empty(survivors); // none forwarded during classify (all pruned); all come back via rescue
        Assert.False(File.Exists(spool.FilePath));
    }

    [Fact]
    public async Task NotCertainReconcile_RescuesEveryPrune()
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker("normal"));
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = NewPipeline(client, spool, survivors);

        Assert.True(await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None));
        foreach (string path in Offered)
            await pipeline.OfferAsync(path, path, CancellationToken.None);
        await pipeline.CompleteOfferingAsync();

        // A not-certain B1 (discontinuous journal) → the pipeline replays its whole spool locally → every
        // prune is rescued (live-scanned), independent of the worker.
        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: false, CancellationToken.None);

        Assert.False(outcome.Accelerated);
        Assert.False(outcome.PruningCertain);
        Assert.Equal(Offered.Length, outcome.GrossPruned);
        Assert.Equal(Offered.Length, outcome.Rescued);
        Assert.Equal(0, outcome.NetPruned);
        Assert.Equal(Offered.OrderBy(p => p, StringComparer.Ordinal), outcome.RescuePaths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkerUnavailable_OpenReturnsFalse_PipelineIsANoOp()
    {
        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = NewPipeline(client, spool, survivors);

        Assert.False(await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None));
        // Offers are dropped; completion is a safe no-op the caller bypasses (it live-scans everything itself).
        foreach (string path in Offered)
            await pipeline.OfferAsync(path, path, CancellationToken.None);
        await pipeline.CompleteOfferingAsync();
        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        Assert.False(outcome.Accelerated);
        Assert.Equal(0, outcome.GrossPruned);
        Assert.Empty(survivors);
    }

    // ── Real bundled worker: prune / survive / rescue correctness vs the in-process layered oracle ──

    [Fact]
    public async Task RealWorker_Layered_ForwardsSurvivors_PrunesNonmembers_RescuesDirty_LikeTheOracle()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\pp";
        ContentIndexGeneration baseGen = BuildBaseGeneration(root);
        ContentIndexDeltaSegment segment = BuildSegment(root);
        string baseDir = WriteV3("rbase", dir => ContentIndexV3Format.Write(dir, baseGen));
        string segDir = WriteV3("rseg", dir => ContentIndexV3Format.Write(dir, segment.Added, segment.RemovedPaths));
        TrigramExpression query = PlanQuery("planner");
        IReadOnlySet<int> baseCandidates = baseGen.Postings.EvaluateSet(query);
        IReadOnlySet<int> segCandidates = segment.Added.Postings.EvaluateSet(query);

        string[] paths =
        {
            Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"),
            Norm(root, "d.txt"), Norm(root, "new.txt"), Norm(root, "absent.txt"),
        };

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = new ContentIndexPruningPipeline(client, spool, SmallBatcher(),
            (path, _) => { survivors.Enqueue(path); return ValueTask.CompletedTask; },
            sessionId: 7, TimeSpan.FromMilliseconds(20), channelCapacity: 64);

        Assert.True(await pipeline.OpenAsync(new IndexQueryOpenRequest
        {
            SessionId = 7,
            BaseDir = baseDir,
            SegmentDirs = new[] { segDir },
            BaseCandidatesBase64 = IndexWorkerProtocol.EncodeCandidates(baseCandidates.ToArray()),
            SegmentCandidatesBase64 = new[] { IndexWorkerProtocol.EncodeCandidates(segCandidates.ToArray()) },
        }, CancellationToken.None));

        foreach (string path in paths)
            await pipeline.OfferAsync(path, path, CancellationToken.None);
        await pipeline.CompleteOfferingAsync();

        // Dirty d.txt's BASE content over [B0, B1) → d.txt (a base-layer nonmember) rescues; new.txt stays pruned.
        Assert.True(baseGen.TryGetAlias(Norm(root, "d.txt"), out _, out long dBaseContentId));
        var baseDirty = new HashSet<long> { dBaseContentId };
        var segDirties = new IReadOnlySet<long>[] { new HashSet<long>() };
        ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
            await pipeline.ReconcileAtB1Async(baseDirty, segDirties, certain: true, CancellationToken.None);

        // The in-process layered oracle is the ground truth for prune / survive / rescue.
        var oracle = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { segment }, query, new DirtyContentSet(), new[] { new DirtyContentSet() });
        var expectedSurvivors = new List<string>();
        foreach (string path in paths)
        {
            if (oracle.Route(path) is not PathDecision.ProvisionalPrune)
                expectedSurvivors.Add(path);
        }
        var oracleB1 = new DirtyContentSet();
        oracleB1.MarkDirty(dBaseContentId);
        IReadOnlyList<string> expectedRescue = oracle.ResolveAliasPaths(oracle.ReconcileAtB1(oracleB1, new[] { new DirtyContentSet() }));

        // Survivors (a.txt member, c.txt member, b.txt tombstoned, absent.txt) were forwarded to be scanned.
        Assert.Equal(
            expectedSurvivors.OrderBy(p => p, StringComparer.Ordinal),
            survivors.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(2, outcome.GrossPruned);          // d.txt + new.txt
        Assert.True(outcome.PruningCertain);
        Assert.Equal(expectedRescue, outcome.RescuePaths); // only d.txt
        Assert.Equal(1, outcome.NetPruned);            // new.txt stays pruned

        // Provenance: the index MEMBERS (a.txt replaced-planner, c.txt planner) are recorded for the
        // results-list "indexed" badge; pruned nonmembers and absent paths are not members.
        Assert.True(pipeline.WasIndexMember(Norm(root, "a.txt")));
        Assert.True(pipeline.WasIndexMember(Norm(root, "c.txt")));
        Assert.False(pipeline.WasIndexMember(Norm(root, "d.txt")));      // pruned nonmember
        Assert.False(pipeline.WasIndexMember(Norm(root, "absent.txt"))); // never in the index

        // Reconcile always performs worker-acknowledged cancellation with a fresh cleanup token. The session
        // must already be gone when control returns, so a subsequent classify cannot retain/reuse mappings.
        byte[]? afterCleanup = await client.ClassifyPathsAsync(
            7, new[] { Norm(root, "a.txt") }, CancellationToken.None, batchSeq: 999);
        Assert.Null(afterCleanup);
    }

    [Fact]
    public async Task CanceledReconcile_StillDropsRealWorkerSessionWithFreshCleanupToken()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\pp";
        ContentIndexGeneration baseGen = BuildBaseGeneration(root);
        string baseDir = WriteV3("cancel-cleanup-base", dir => ContentIndexV3Format.Write(dir, baseGen));

        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = new ContentIndexPruningPipeline(client, spool, SmallBatcher(),
            (path, _) => { survivors.Enqueue(path); return ValueTask.CompletedTask; },
            sessionId: 8, TimeSpan.FromMilliseconds(20), channelCapacity: 64);

        Assert.True(await pipeline.OpenAsync(new IndexQueryOpenRequest
        {
            SessionId = 8,
            BaseDir = baseDir,
            QueryRpnBase64 = Convert.ToBase64String(TrigramQueryRpn.Encode(PlanQuery("planner"))),
        }, CancellationToken.None));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await pipeline.ReconcileAtB1Async(NoDirty, NoSegmentDirties, certain: true, canceled.Token);

        byte[]? afterCleanup = await client.ClassifyPathsAsync(
            8, new[] { Norm(root, "a.txt") }, CancellationToken.None, batchSeq: 1);
        Assert.Null(afterCleanup);
    }

    // ── Real-worker v3 corpus (mirrors IndexWorkerClientTests) ──

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration BuildBaseGeneration(string root)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(root + "\\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        builder.AddDocument(root + "\\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        builder.AddDocument(root + "\\d.txt", Encoding.UTF8.GetBytes("unrelated filler content and words"));
        return builder.Build("scope", "vol", root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private ContentIndexDeltaSegment BuildSegment(string root)
    {
        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        seg.AddChangedDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("replaced planner text right now"));
        seg.AddChangedDocument(root + "\\new.txt", Encoding.UTF8.GetBytes("a fresh trigram document here"));
        seg.AddTombstone(root + "\\b.txt");
        return seg.Build("scope", "vol", root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\pp", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    private string WriteV3(string subdir, Action<string> write)
    {
        string dir = Path.Combine(_sandbox, subdir);
        Directory.CreateDirectory(dir);
        write(dir);
        return dir;
    }

    private static string Norm(string root, string file) => IndexScopeIdentity.NormalizePath(root + "\\" + file);

    // ── Worker-binary discovery (self-gating) ──

    private static string NonexistentWorker()
        => Path.Combine(Path.GetTempPath(), "yagu-no-such-index-worker-" + Guid.NewGuid().ToString("N") + ".exe");

    private static string? FindWorkerExe()
    {
        string repoRoot = FindRepoRoot();
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(repoRoot, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private string FakeWorker(string scenario)
    {
        string sourceDirectory = FindFakeWorkerOutput();
        string executable = Path.Combine(_sandbox, $"prune-pipe-{scenario}.exe");
        foreach (string source in Directory.GetFiles(sourceDirectory))
        {
            string destination = Path.GetFileName(source).Equals("Yagu.FakeIndexWorker.exe", StringComparison.OrdinalIgnoreCase)
                ? executable
                : Path.Combine(_sandbox, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
        }
        File.WriteAllText(executable + ".scenario", scenario);
        return executable;
    }

    private static string FindFakeWorkerOutput()
        => FindFakeWorkerOutputOrNull() ?? throw new FileNotFoundException("The fake index worker was not built.");

    private static string? FindFakeWorkerOutputOrNull()
    {
        string repo = FindRepoRoot();
        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string directory = Path.Combine(repo, "tests", "Yagu.FakeIndexWorker", "bin", configuration, "net10.0");
            if (File.Exists(Path.Combine(directory, "Yagu.FakeIndexWorker.exe")))
                return directory;
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
