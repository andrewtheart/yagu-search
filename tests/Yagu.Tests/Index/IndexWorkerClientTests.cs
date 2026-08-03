using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="IndexWorkerClient"/> — the in-app proxy that launches and talks to the
/// out-of-process worker. The graceful-degradation contract (missing worker → failure results, never
/// throws) is unit-tested; the real protocol round-trips (<c>EnsureReadyAsync</c> / <c>ExtractAsync</c> /
/// <c>QueryContentBinAsync</c>) are integration-tested against the bundled worker and self-gate when it
/// hasn't been built (CI test-only runs).
/// </summary>
[Collection("IndexWorkerClientEnvironment")]
public sealed class IndexWorkerClientTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-query-worker", Guid.NewGuid().ToString("N"));

    public IndexWorkerClientTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }
    // ── Graceful degradation (no worker process) ──

    [Fact]
    public async Task EnsureReadyAsync_MissingWorker_ReturnsFalse()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_MissingWorker_ReturnsFailure()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        IndexWorkerExtractResult result = await client.ExtractAsync(@"C:\does\not\matter.txt", CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Empty(result.Trigrams);
    }

    [Fact]
    public async Task QueryContentBinAsync_MissingWorker_ReturnsFailure()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        IndexWorkerQueryResult result = await client.QueryContentBinAsync(@"C:\nope\content.bin", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ExtractAndQueryContentBin_WorkerRejectsWithoutError_UseDefaultErrors()
    {
        using var client = new IndexWorkerClient(FakeWorker("queryRejectNoError"));

        IndexWorkerExtractResult extract = await client.ExtractAsync("anything.txt", CancellationToken.None);
        IndexWorkerQueryResult query = await client.QueryContentBinAsync("content.bin", ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.False(extract.Success);
        Assert.Equal("extract failed", extract.Error);
        Assert.False(query.Success);
        Assert.Equal("query failed", query.Error);
    }

    [Fact]
    public async Task QueryContentBinAsync_HealthyWorker_AcceptsEmptyAndNonemptyRpn()
    {
        using var client = new IndexWorkerClient(FakeWorker("queryNormal"));

        IndexWorkerQueryResult empty = await client.QueryContentBinAsync(
            "content.bin", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        IndexWorkerQueryResult nonempty = await client.QueryContentBinAsync(
            "content.bin", new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.True(empty.Success, empty.Error);
        Assert.Empty(empty.Candidates);
        Assert.True(nonempty.Success, nonempty.Error);
        Assert.Empty(nonempty.Candidates);
    }

    [Fact]
    public void Dispose_WithoutStart_IsIdempotent()
    {
        var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        client.Dispose();
        client.Dispose(); // must not throw
    }

    [Fact]
    public async Task DefaultClient_AfterDispose_IsNotReady_AndHasNoWorkerMemory()
    {
        var client = new IndexWorkerClient();

        Assert.Equal(0, client.WorkerPeakWorkingSetBytes);
        client.Dispose();

        Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
    }

    // ── End-to-end with the REAL worker (self-gates when not built) ──

    [Fact]
    public async Task EnsureReadyAsync_RealWorker_ReturnsTrue_AndIsSingleFlight()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            return;
        }

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        // Single-flight: a second call returns the same (ready) result without relaunching.
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_RealWorker_MatchesManagedClassification()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            return;
        }

        string file = Path.Combine(Path.GetTempPath(), "yagu-index-client-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "the quick brown fox jumps over the lazy dog", new UTF8Encoding(false));
        try
        {
            using var client = new IndexWorkerClient(workerPathOverride: workerExe);
            IndexWorkerExtractResult result = await client.ExtractAsync(file, CancellationToken.None);

            Assert.True(result.Success, result.Error);

            byte[] bytes = await File.ReadAllBytesAsync(file);
            ContentRepresentationVerdict managedVerdict = ContentRepresentation.Classify(bytes, out var managedTrigrams);
            uint[] managed = managedTrigrams.Select(t => t.Value).ToArray();

            Assert.Equal((int)managedVerdict, result.Verdict);
            Assert.Equal(managed, result.Trigrams);
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ExtractAsync_RealWorker_MissingFile_ReturnsFailure()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            return;
        }

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexWorkerExtractResult result = await client.ExtractAsync(
            Path.Combine(Path.GetTempPath(), "yagu-no-such-" + Guid.NewGuid().ToString("N") + ".txt"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task QueryContentBinAsync_RealWorker_InvalidContentBin_ReturnsFailure()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            return;
        }

        // A file that exists but is not a valid checksummed content.bin → the native query rejects it and
        // the worker replies ok=false, which the client surfaces as a failure result (never throws).
        string bogus = Path.Combine(Path.GetTempPath(), "yagu-bogus-content-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(bogus, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        try
        {
            using var client = new IndexWorkerClient(workerPathOverride: workerExe);
            IndexWorkerQueryResult result = await client.QueryContentBinAsync(bogus, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            Assert.False(result.Success);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally
        {
            try { File.Delete(bogus); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("initError")]
    [InlineData("initErrorNoText")]
    public async Task EnsureReadyAsync_FailedHandshake_CanRestartOnNextAttempt(string firstScenario)
    {
        string worker = FakeWorker(firstScenario);
        using var client = new IndexWorkerClient(worker);
        Assert.False(await client.EnsureReadyAsync(CancellationToken.None));

        File.WriteAllText(worker + ".scenario", "queryNormal");
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        IndexWorkerExtractResult result = await client.ExtractAsync(Path.Combine(_sandbox, "anything.txt"), CancellationToken.None);
        Assert.True(result.Success, result.Error);
    }

    [Theory]
    [InlineData("queryMalformed")]
    [InlineData("queryUnknown")]
    [InlineData("nullMessage")]
    public async Task RuntimeProtocolFailure_FailsRequestAndRestartsCleanly(string scenario)
    {
        string worker = FakeWorker(scenario);
        using var client = new IndexWorkerClient(worker);
        IndexWorkerExtractResult failed = await client.ExtractAsync(Path.Combine(_sandbox, "anything.txt"), CancellationToken.None);
        Assert.False(failed.Success);

        File.WriteAllText(worker + ".scenario", "queryNormal");
        for (int i = 0; i < 100; i++)
        {
            if (await client.EnsureReadyAsync(CancellationToken.None))
            {
                IndexWorkerExtractResult recovered = await client.ExtractAsync(Path.Combine(_sandbox, "anything.txt"), CancellationToken.None);
                if (recovered.Success)
                    return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The query worker did not restart after a fatal protocol-channel failure.");
    }

    [Fact]
    public async Task EnsureReadyAsync_CanceledBeforeHandshake_ReturnsFalse()
    {
        using var client = new IndexWorkerClient(FakeWorker("hangBeforeReady"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Assert.False(await client.EnsureReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task ReadLoop_BlankLineIsSkippedBeforeProtocolFailure()
    {
        using var client = new IndexWorkerClient(FakeWorker("blankNormal"));

        IndexWorkerExtractResult result = await client.ExtractAsync("anything.txt", CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task StandardErrorDiagnostics_DoNotPreventReadyHandshake()
    {
        using var client = new IndexWorkerClient(FakeWorker("stderr"));

        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        await Task.Delay(100);
    }

    [Fact]
    public async Task WorkerPeakWorkingSetBytes_ReportsLiveWorker_AndFailsClosedAfterDispose()
    {
        var client = new IndexWorkerClient(FakeWorker("queryNormal"));
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        Assert.True(client.WorkerPeakWorkingSetBytes > 0);

        client.Dispose();

        Assert.Equal(0, client.WorkerPeakWorkingSetBytes);
    }

    [Fact]
    public async Task WorkerPeakWorkingSetBytes_ExitedWorker_ReturnsZero()
    {
        using var client = new IndexWorkerClient(FakeWorker("exitAfterReady"));
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));

        for (int attempt = 0; attempt < 100 && client.WorkerPeakWorkingSetBytes != 0; attempt++)
            await Task.Delay(10);

        Assert.Equal(0, client.WorkerPeakWorkingSetBytes);
    }

    // ── Mapped query-session ops (plan §5.2, Stage 2 shadow mode) ──

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
        var options = new SearchOptions { Directory = @"C:\qw", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
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

    private static string EncodeSet(System.Collections.Generic.IReadOnlySet<int> set)
        => IndexWorkerProtocol.EncodeCandidates(set.ToArray());

    private static string Norm(string root, string file) => IndexScopeIdentity.NormalizePath(root + "\\" + file);

    [Fact]
    public async Task OpenQueryScopeAsync_MissingWorker_ReturnsNull()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        IndexQueryOpenResult? result = await client.OpenQueryScopeAsync(
            new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None);
        Assert.Null(result); // worker unavailable → host live-scans
    }

    [Fact]
    public async Task OpenQueryScope_RealWorker_BaseOnly_ClassifiesLikeTheOracle()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = WriteV3("base", dir => ContentIndexV3Format.Write(dir, gen));
        TrigramExpression query = PlanQuery("planner");
        System.Collections.Generic.IReadOnlySet<int> candidates = gen.Postings.EvaluateSet(query);

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 7,
            BaseDir = baseDir,
            BaseCandidatesBase64 = EncodeSet(candidates),
        }, CancellationToken.None);

        Assert.NotNull(open);
        Assert.True(open!.Accelerable, open.BypassReason);
        Assert.Equal(candidates.Count, open.CandidateCount);

        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"), Norm(root, "d.txt"), Norm(root, "absent.txt") };
        byte[]? verdicts = await client.ClassifyPathsAsync(7, paths, CancellationToken.None);
        Assert.NotNull(verdicts);
        Assert.Equal(paths.Length, verdicts!.Length);

        var oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(paths[i])), verdicts[i]);

        await client.CloseQueryScopeAsync(7, CancellationToken.None);
    }

    [Fact]
    public async Task OpenQueryScope_RealWorker_RpnOnly_WorkerSelfEvaluatesCandidates_ClassifiesLikeTheOracle()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = WriteV3("rpnbase", dir => ContentIndexV3Format.Write(dir, gen));
        TrigramExpression query = PlanQuery("planner");
        System.Collections.Generic.IReadOnlySet<int> candidates = gen.Postings.EvaluateSet(query);

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        // No candidates passed — the worker decodes the RPN and evaluates candidates over its mapped v3 postings.
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 21,
            BaseDir = baseDir,
            QueryRpnBase64 = Convert.ToBase64String(TrigramQueryRpn.Encode(query)),
        }, CancellationToken.None);

        Assert.NotNull(open);
        Assert.True(open!.Accelerable, open.BypassReason);
        Assert.Equal(candidates.Count, open.CandidateCount);
        Assert.NotNull(open.Diagnostics);
        Assert.True(open.Diagnostics!.CandidatesEvaluatedInWorker);
        Assert.True(open.Diagnostics.CandidateEvaluationMs >= 0);

        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"), Norm(root, "d.txt"), Norm(root, "absent.txt") };
        byte[]? verdicts = await client.ClassifyPathsAsync(21, paths, CancellationToken.None);
        Assert.NotNull(verdicts);

        var oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(paths[i])), verdicts![i]);

        await client.CloseQueryScopeAsync(21, CancellationToken.None);
    }

    [Fact]
    public async Task OpenQueryScope_RealWorker_Layered_ClassifiesLikeTheOracle()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration baseGen = BuildBaseGeneration(root);
        ContentIndexDeltaSegment segment = BuildSegment(root);
        string baseDir = WriteV3("lbase", dir => ContentIndexV3Format.Write(dir, baseGen));
        string segDir = WriteV3("lseg", dir => ContentIndexV3Format.Write(dir, segment.Added, segment.RemovedPaths));
        TrigramExpression query = PlanQuery("planner");
        System.Collections.Generic.IReadOnlySet<int> baseCandidates = baseGen.Postings.EvaluateSet(query);
        System.Collections.Generic.IReadOnlySet<int> segCandidates = segment.Added.Postings.EvaluateSet(query);

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 9,
            BaseDir = baseDir,
            SegmentDirs = new[] { segDir },
            BaseCandidatesBase64 = EncodeSet(baseCandidates),
            SegmentCandidatesBase64 = new[] { EncodeSet(segCandidates) },
        }, CancellationToken.None);

        Assert.NotNull(open);
        Assert.True(open!.Accelerable, open.BypassReason);
        Assert.NotNull(open.Diagnostics);
        IndexQueryOpenDiagnostics diagnostics = open.Diagnostics!;
        Assert.Equal(2, diagnostics.LayerCount);
        Assert.Equal(6, diagnostics.PathRecordCount);
        Assert.Equal(1, diagnostics.TombstoneRecordCount);
        Assert.Equal(5, diagnostics.DistinctRouteHashCount);
        Assert.Equal(2, diagnostics.SupersededRouteRecordCount);
        Assert.Equal(1.4, diagnostics.RouteRecordAmplification, precision: 3);
        Assert.False(diagnostics.CandidatesEvaluatedInWorker);
        Assert.True(diagnostics.MapOpenMs >= 0);
        Assert.True(diagnostics.RoutingIndexMs >= 0);
        Assert.True(diagnostics.WorkerOpenMs >= diagnostics.MapOpenMs);
        Assert.True(diagnostics.HostRoundTripMs >= 0);

        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"), Norm(root, "d.txt"), Norm(root, "new.txt"), Norm(root, "absent.txt") };
        byte[]? verdicts = await client.ClassifyPathsAsync(9, paths, CancellationToken.None);
        Assert.NotNull(verdicts);

        var oracle = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { segment }, query, new DirtyContentSet(), new[] { new DirtyContentSet() });
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(paths[i])), verdicts![i]);

        await client.CloseQueryScopeAsync(9, CancellationToken.None);
    }

    [Fact]
    public async Task OpenQueryScope_RealWorker_UnUpgradedScope_ReportsNotAccelerable()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        // A directory with no format-v3 sidecars → the worker cannot map it → the host must live-scan.
        string emptyDir = WriteV3("nov3", _ => { });
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(
            new IndexQueryOpenRequest { SessionId = 11, BaseDir = emptyDir }, CancellationToken.None);

        Assert.NotNull(open);
        Assert.False(open!.Accelerable);
        Assert.False(string.IsNullOrEmpty(open.BypassReason));
    }

    // ── Stage-4 pruning + reconcileB1 (plan §5.5): the worker tracks a provisional prune set during
    //    classify and rescues the dirty ones at B1, byte-identically to the in-process layered oracle. ──

    [Fact]
    public async Task ReconcileB1_RealWorker_Layered_CertainThenNotCertain_RescuesLikeTheOracle()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration baseGen = BuildBaseGeneration(root);
        ContentIndexDeltaSegment segment = BuildSegment(root);
        string baseDir = WriteV3("pbase", dir => ContentIndexV3Format.Write(dir, baseGen));
        string segDir = WriteV3("pseg", dir => ContentIndexV3Format.Write(dir, segment.Added, segment.RemovedPaths));
        TrigramExpression query = PlanQuery("planner");
        System.Collections.Generic.IReadOnlySet<int> baseCandidates = baseGen.Postings.EvaluateSet(query);
        System.Collections.Generic.IReadOnlySet<int> segCandidates = segment.Added.Postings.EvaluateSet(query);

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 31,
            BaseDir = baseDir,
            SegmentDirs = new[] { segDir },
            BaseCandidatesBase64 = EncodeSet(baseCandidates),
            SegmentCandidatesBase64 = new[] { EncodeSet(segCandidates) },
            PruningEnabled = true,
        }, CancellationToken.None);
        Assert.NotNull(open);
        Assert.True(open!.Accelerable, open.BypassReason);

        // Classify every discovered path — pruning-mode records the fresh nonmembers (d.txt base, new.txt
        // segment) as provisionally pruned. The verdicts still match the oracle (RouteForPruning is Classify+track).
        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"), Norm(root, "d.txt"), Norm(root, "new.txt"), Norm(root, "absent.txt") };
        byte[]? verdicts = await client.ClassifyPathsAsync(31, paths, CancellationToken.None);
        Assert.NotNull(verdicts);
        var oracle = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { segment }, query, new DirtyContentSet(), new[] { new DirtyContentSet() });
        for (int i = 0; i < paths.Length; i++)
        {
            Assert.Equal(IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(paths[i])), verdicts![i]);
            oracle.Route(paths[i]);
        }

        // Certain B1: dirty d.txt's BASE content over [B0, B1) → only d.txt rescues; new.txt stays pruned.
        Assert.True(baseGen.TryGetAlias(Norm(root, "d.txt"), out _, out long dBaseContentId));
        var baseDirty = new System.Collections.Generic.HashSet<long> { dBaseContentId };
        var segDirties = new System.Collections.Generic.IReadOnlySet<long>[] { new System.Collections.Generic.HashSet<long>() };
        IndexWorkerReconcileResult certain = await client.ReconcileB1Async(31, baseDirty, segDirties, certain: true, CancellationToken.None);
        Assert.True(certain.Success);
        Assert.True(certain.PruningCertain);
        Assert.Equal(new[] { Norm(root, "d.txt") }, certain.RescuePaths);

        // Oracle parity: the in-process layered session rescues the same path set for the same dirty input.
        var oracleBaseDirty = new DirtyContentSet();
        oracleBaseDirty.MarkDirty(dBaseContentId);
        System.Collections.Generic.IReadOnlyList<long> oracleAliases = oracle.ReconcileAtB1(oracleBaseDirty, new[] { new DirtyContentSet() });
        Assert.Equal(oracle.ResolveAliasPaths(oracleAliases), certain.RescuePaths);

        // A subsequent NOT-certain reconcile drains the remaining prune (new.txt), flagged unaccelerated.
        IndexWorkerReconcileResult drain = await client.ReconcileB1Async(
            31, new System.Collections.Generic.HashSet<long>(), System.Array.Empty<System.Collections.Generic.IReadOnlySet<long>>(), certain: false, CancellationToken.None);
        Assert.True(drain.Success);
        Assert.False(drain.PruningCertain);
        Assert.Equal(new[] { Norm(root, "new.txt") }, drain.RescuePaths);

        await client.CloseQueryScopeAsync(31, CancellationToken.None);
    }

    [Fact]
    public async Task ReconcileB1_RealWorker_ShadowSession_HasNoProvisionalToRescue()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        // A session opened WITHOUT PruningEnabled classifies purely (Stage-2 shadow) → nothing is provisional →
        // even a not-certain reconcile rescues nothing.
        const string root = @"C:\qw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = WriteV3("shadowbase", dir => ContentIndexV3Format.Write(dir, gen));
        TrigramExpression query = PlanQuery("planner");

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 41,
            BaseDir = baseDir,
            BaseCandidatesBase64 = EncodeSet(gen.Postings.EvaluateSet(query)),
            // PruningEnabled defaults false.
        }, CancellationToken.None);
        Assert.NotNull(open);
        Assert.True(open!.Accelerable, open.BypassReason);

        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "d.txt") };
        byte[]? verdicts = await client.ClassifyPathsAsync(41, paths, CancellationToken.None);
        Assert.NotNull(verdicts);

        IndexWorkerReconcileResult drain = await client.ReconcileB1Async(
            41, new System.Collections.Generic.HashSet<long>(), System.Array.Empty<System.Collections.Generic.IReadOnlySet<long>>(), certain: false, CancellationToken.None);
        Assert.True(drain.Success);
        Assert.Empty(drain.RescuePaths); // shadow session tracked nothing

        await client.CloseQueryScopeAsync(41, CancellationToken.None);
    }

    // Stage-3 framed transport: batch sequence, deadline, cancellation, queuing.

    [Fact]
    public async Task ClassifyPathsAsync_PastDeadline_ReturnsNull_WithoutContactingWorker()
    {
        // A deadline already in the past → the client abandons the batch before ever launching/contacting the
        // worker (a hung/slow worker degrades to a live scan). NonexistentWorker proves no round-trip happens.
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        long pastDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000;
        byte[]? verdicts = await client.ClassifyPathsAsync(
            1, new[] { @"c:\qw\a.txt" }, CancellationToken.None, batchSeq: 1, deadlineUnixMs: pastDeadline);
        Assert.Null(verdicts);
    }

    [Fact]
    public async Task ClassifyPaths_RealWorker_NonZeroBatchSeq_IsAcceptedAndClassifies()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = WriteV3("seqbase", dir => ContentIndexV3Format.Write(dir, gen));
        TrigramExpression query = PlanQuery("planner");

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 30,
            BaseDir = baseDir,
            QueryRpnBase64 = Convert.ToBase64String(TrigramQueryRpn.Encode(query)),
        }, CancellationToken.None);
        Assert.True(open!.Accelerable, open.BypassReason);

        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt") };
        // A non-zero batch sequence + generous deadline must be echoed by the worker and accepted by the
        // reply gate (epoch + session + batch match) → verdicts returned, not dropped.
        long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;
        byte[]? verdicts = await client.ClassifyPathsAsync(30, paths, CancellationToken.None, batchSeq: 99, deadlineUnixMs: deadline);
        Assert.NotNull(verdicts);
        Assert.Equal(paths.Length, verdicts!.Length);

        var oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(paths[i])), verdicts[i]);

        await client.CloseQueryScopeAsync(30, CancellationToken.None);
    }

    [Fact]
    public async Task CancelSession_RealWorker_DropsSession_SoSubsequentClassifyFails()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = WriteV3("cancelbase", dir => ContentIndexV3Format.Write(dir, gen));

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(new IndexQueryOpenRequest
        {
            SessionId = 40,
            BaseDir = baseDir,
            QueryRpnBase64 = Convert.ToBase64String(TrigramQueryRpn.Encode(PlanQuery("planner"))),
        }, CancellationToken.None);
        Assert.True(open!.Accelerable, open.BypassReason);

        // Worker-acknowledged cancellation drops the session → a later classify against it fails (→ live-scan).
        await client.CancelSessionAsync(40, CancellationToken.None);
        byte[]? verdicts = await client.ClassifyPathsAsync(40, new[] { Norm(root, "a.txt") }, CancellationToken.None, batchSeq: 1);
        Assert.Null(verdicts);
    }

    [Fact]
    public void QueryScopeHost_LogsMappedSessionLifecycleToStderr()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu.IndexWorker", "IndexQueryScopeHost.cs"));

        Assert.Contains("query-session open id={spec.SessionId}", source);
        Assert.Contains("query-session cancel id={spec.SessionId} removed={removed} active={Sessions.Count}", source);
        Assert.Contains("query-session close id={spec.SessionId} removed={removed} active={Sessions.Count}", source);
        Assert.Contains("query-session close-all removed={removed} active={Sessions.Count}", source);
        Assert.Contains("Console.Error.WriteLine(\"[indexworker] \" + message)", source);
    }

    [Fact]
    public async Task ClassifyPaths_RealWorker_ConcurrentSessions_AllSucceed_NotFailFastBusy()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\qw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = WriteV3("concbase", dir => ContentIndexV3Format.Write(dir, gen));
        string rpn = Convert.ToBase64String(TrigramQueryRpn.Encode(PlanQuery("planner")));

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        foreach (int sid in new[] { 50, 51 })
        {
            IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(
                new IndexQueryOpenRequest { SessionId = sid, BaseDir = baseDir, QueryRpnBase64 = rpn }, CancellationToken.None);
            Assert.True(open!.Accelerable, open.BypassReason);
        }

        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt") };
        // Two concurrent classify batches on distinct sessions. Under the old fail-fast WorkLock one would have
        // been rejected as busy; the Stage-3 query queue serializes them so BOTH succeed.
        Task<byte[]?> t50 = client.ClassifyPathsAsync(50, paths, CancellationToken.None, batchSeq: 1);
        Task<byte[]?> t51 = client.ClassifyPathsAsync(51, paths, CancellationToken.None, batchSeq: 1);
        byte[]?[] results = await Task.WhenAll(t50, t51);

        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Equal(paths.Length, r!.Length));

        await client.CloseQueryScopeAsync(50, CancellationToken.None);
        await client.CloseQueryScopeAsync(51, CancellationToken.None);
    }

    [Fact]
    public async Task InjectedProcess_StartFalseFactoryFailureAndReadyTimeout_FailClosed()
    {
        var startFalse = new FakeIndexWorkerProcess { StartResult = false };
        using (IndexWorkerClient client = InjectedClient(startFalse))
        {
            Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
            Assert.Equal(1, startFalse.DisposeCount);
        }

        using (IndexWorkerClient client = InjectedClient(
            new FakeIndexWorkerProcess(),
            processFactory: _ => throw new InvalidOperationException("factory failed")))
        {
            Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
        }

        var timedOut = new FakeIndexWorkerProcess();
        using (IndexWorkerClient client = InjectedClient(timedOut, readyTimeout: TimeSpan.Zero))
        {
            Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task InjectedProcess_JobAssignmentAndIdFailures_AreBestEffort()
    {
        var process = ReadyFakeProcess();
        process.IdException = new InvalidOperationException("id failed");
        ProcessStartInfo? captured = null;
        using IndexWorkerClient client = InjectedClient(
            process,
            processFactory: startInfo =>
            {
                captured = startInfo;
                return process;
            },
            jobAssigner: (_, _) => throw new InvalidOperationException("assign failed"));

        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        Assert.NotNull(captured);
        Assert.False(captured.UseShellExecute);
        Assert.True(captured.CreateNoWindow);
        Assert.True(captured.RedirectStandardInput);
        Assert.True(captured.RedirectStandardOutput);
        Assert.True(captured.RedirectStandardError);
        Assert.Empty(captured.StandardInputEncoding!.GetPreamble());
    }

    [Fact]
    public async Task InjectedProcess_OutputAndErrorStreamFailures_FailClosed()
    {
        var outputFailure = new FakeIndexWorkerProcess();
        outputFailure.Output.Complete(new IOException("stdout failed"));
        using (IndexWorkerClient client = InjectedClient(outputFailure))
        {
            Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
            await outputFailure.Output.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var errorFailure = ReadyFakeProcess();
        errorFailure.Error.WriteLine(string.Empty);
        errorFailure.Error.WriteLine("diagnostic");
        errorFailure.Error.Complete(new IOException("stderr failed"));
        using (IndexWorkerClient client = InjectedClient(errorFailure))
        {
            Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
            await errorFailure.Error.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InjectedProcess_WriteOrFlushFailure_FailsRequest(bool failWrite)
    {
        var process = ReadyFakeProcess();
        using IndexWorkerClient client = InjectedClient(process);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        process.StandardInputWriter.AsyncWriteException = failWrite ? new IOException("write failed") : null;
        process.StandardInputWriter.AsyncFlushException = failWrite ? null : new IOException("flush failed");

        IndexWorkerExtractResult result = await client.ExtractAsync("anything.txt", CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task InjectedProcess_ExitedBeforeSend_FailsRequest()
    {
        var process = ReadyFakeProcess();
        using IndexWorkerClient client = InjectedClient(process);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        process.HasExitedValue = true;

        IndexWorkerExtractResult result = await client.ExtractAsync("anything.txt", CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task InjectedProcess_MissingProcessDuringReconcile_FailsClosed()
    {
        var process = ReadyFakeProcess();
        using IndexWorkerClient client = InjectedClient(process);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        SetPrivateField(client, "_process", null);

        IndexWorkerReconcileResult result = await client.ReconcileB1Async(
            1,
            new HashSet<long>(),
            Array.Empty<IReadOnlySet<long>>(),
            certain: true,
            CancellationToken.None);

        SetPrivateField(client, "_process", process);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task InjectedProcess_MissingInputDuringSend_FailsClosed()
    {
        var process = ReadyFakeProcess();
        using IndexWorkerClient client = InjectedClient(process);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        SetPrivateField(client, "_stdin", null);

        IndexWorkerExtractResult result = await client.ExtractAsync("anything.txt", CancellationToken.None);

        SetPrivateField(client, "_stdin", process.StandardInput);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExplicitEmptyWorkerOverride_IsUnavailable()
    {
        using var client = new IndexWorkerClient(workerPathOverride: null);

        Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkerPeakWorkingSetBytes_InjectedProcess_CoversLiveExitedAndFailureStates()
    {
        var process = ReadyFakeProcess();
        process.PeakWorkingSetBytesValue = 123;
        using IndexWorkerClient client = InjectedClient(process);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(123, client.WorkerPeakWorkingSetBytes);
        process.HasExitedValue = true;
        Assert.Equal(0, client.WorkerPeakWorkingSetBytes);
        process.HasExitedValue = false;
        process.RefreshException = new InvalidOperationException("refresh failed");
        Assert.Equal(0, client.WorkerPeakWorkingSetBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialization_RestartCleansPreviousProcess_EvenWhenKillThrows(bool killThrows)
    {
        var first = new FakeIndexWorkerProcess { KillException = killThrows ? new InvalidOperationException("kill failed") : null };
        first.Output.WriteLine("{\"type\":\"error\",\"error\":\"failed\"}");
        var second = ReadyFakeProcess();
        var processes = new Queue<IIndexWorkerProcess>(new IIndexWorkerProcess[] { first, second });
        using IndexWorkerClient client = InjectedClient(first, processFactory: _ => processes.Dequeue());

        Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(1, first.KillCount);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task Dispose_IgnoresShutdownKillAndProcessDisposeFailures()
    {
        var process = ReadyFakeProcess();
        var client = InjectedClient(process);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        process.StandardInputWriter.SyncWriteException = new IOException("shutdown write failed");
        process.KillException = new InvalidOperationException("kill failed");
        process.DisposeException = new InvalidOperationException("dispose failed");

        Exception? error = Record.Exception(client.Dispose);

        Assert.Null(error);
        client.Dispose();
    }

    [Fact]
    public async Task StaleExitAndProtocolFailureCallbacks_DoNotAffectCurrentWorker()
    {
        var current = ReadyFakeProcess();
        using IndexWorkerClient client = InjectedClient(current);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        var stale = new FakeIndexWorkerProcess();
        var ready = new TaskCompletionSource<IndexWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        InvokePrivate(client, "OnProcessExited", stale, 999, ready);
        InvokePrivate(client, "FailProtocolChannel", "stale failure", stale, 999, ready);

        Assert.False(ready.Task.IsCompleted);
        Assert.Equal(0, stale.KillCount);
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureReadyAsync_UnexpectedSharedInitializationFault_FailsClosed(bool replaceTaskBeforeFault)
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(client, "_initTask", source.Task);

        Task<bool> readiness = client.EnsureReadyAsync(CancellationToken.None);
        if (replaceTaskBeforeFault)
            SetPrivateField(client, "_initTask", Task.FromResult(true));
        source.SetException(new IOException("unexpected init fault"));

        Assert.False(await readiness);
    }

    [Fact]
    public async Task EnsureReadyAsync_CanceledStoredInitialization_Restarts()
    {
        var process = ReadyFakeProcess();
        using IndexWorkerClient client = InjectedClient(process);
        SetPrivateField(client, "_initTask", Task.FromCanceled<bool>(new CancellationToken(canceled: true)));

        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_WithoutReadySource_ThrowsInvariantFailure()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        Task<bool> initialize = (Task<bool>)typeof(IndexWorkerClient)
            .GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, null)!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => initialize);
    }

    [Fact]
    public async Task DefaultResolution_UsesEnvironmentThenLocalPath_AndEnforcesTrust()
    {
        string? previous = Environment.GetEnvironmentVariable(IndexWorkerClient.WorkerPathEnvVar);
        string environmentWorker = Path.Combine(_sandbox, "environment-worker.exe");
        File.WriteAllText(environmentWorker, "test");
        string localDirectory = Path.Combine(AppContext.BaseDirectory, "index-worker");
        string localWorker = Path.Combine(localDirectory, "Yagu.IndexWorker.exe");
        bool localWorkerExisted = File.Exists(localWorker);
        try
        {
            var environmentProcess = ReadyFakeProcess();
            ProcessStartInfo? captured = null;
            Environment.SetEnvironmentVariable(IndexWorkerClient.WorkerPathEnvVar, environmentWorker);
            using (var client = new IndexWorkerClient(
                workerPathOverride: null,
                hasWorkerPathOverride: false,
                processFactory: startInfo => { captured = startInfo; return environmentProcess; },
                trustVerifier: TrustWorker,
                readyTimeout: TimeSpan.FromSeconds(5),
                jobAssigner: static (_, _) => { }))
            {
                Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
                Assert.Equal(environmentWorker, captured!.FileName);
            }

            int factoryCalls = 0;
            using (var client = new IndexWorkerClient(
                workerPathOverride: null,
                hasWorkerPathOverride: false,
                processFactory: _ => { factoryCalls++; return ReadyFakeProcess(); },
                trustVerifier: RejectWorker,
                readyTimeout: TimeSpan.FromSeconds(5),
                jobAssigner: static (_, _) => { }))
            {
                Assert.False(await client.EnsureReadyAsync(CancellationToken.None));
                Assert.Equal(0, factoryCalls);
            }

            Environment.SetEnvironmentVariable(IndexWorkerClient.WorkerPathEnvVar, NonexistentWorker());
            if (!localWorkerExisted)
            {
                using var missingClient = new IndexWorkerClient(
                    workerPathOverride: null,
                    hasWorkerPathOverride: false,
                    processFactory: _ => throw new InvalidOperationException("must not start"),
                    trustVerifier: TrustWorker,
                    readyTimeout: TimeSpan.FromSeconds(5),
                    jobAssigner: static (_, _) => { });
                Assert.False(await missingClient.EnsureReadyAsync(CancellationToken.None));
            }

            Directory.CreateDirectory(localDirectory);
            if (!localWorkerExisted)
                File.WriteAllText(localWorker, "test");
            Environment.SetEnvironmentVariable(IndexWorkerClient.WorkerPathEnvVar, " ");
            var localProcess = ReadyFakeProcess();
            captured = null;
            using (var client = new IndexWorkerClient(
                workerPathOverride: null,
                hasWorkerPathOverride: false,
                processFactory: startInfo => { captured = startInfo; return localProcess; },
                trustVerifier: TrustWorker,
                readyTimeout: TimeSpan.FromSeconds(5),
                jobAssigner: static (_, _) => { }))
            {
                Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
                Assert.Equal(localWorker, captured!.FileName);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexWorkerClient.WorkerPathEnvVar, previous);
            if (!localWorkerExisted)
            {
                File.Delete(localWorker);
                if (Directory.Exists(localDirectory) && Directory.GetFileSystemEntries(localDirectory).Length == 0)
                    Directory.Delete(localDirectory);
            }
        }
    }

    private IndexWorkerClient InjectedClient(
        FakeIndexWorkerProcess process,
        Func<ProcessStartInfo, IIndexWorkerProcess>? processFactory = null,
        TimeSpan? readyTimeout = null,
        Action<WindowsJobObject, nint>? jobAssigner = null)
    {
        string workerPath = Path.Combine(_sandbox, "injected-worker.exe");
        File.WriteAllText(workerPath, "test");
        return new IndexWorkerClient(
            workerPath,
            hasWorkerPathOverride: true,
            processFactory ?? (_ => process),
            TrustWorker,
            readyTimeout ?? TimeSpan.FromSeconds(5),
            jobAssigner ?? ((_, _) => { }));
    }

    private static FakeIndexWorkerProcess ReadyFakeProcess()
    {
        var process = new FakeIndexWorkerProcess();
        process.Output.WriteLine("{\"type\":\"ready\",\"controlProtocolVersion\":2,\"epoch\":7}");
        return process;
    }

    private sealed class FakeIndexWorkerProcess : IIndexWorkerProcess
    {
        private EventHandler? _exited;

        internal FakeIndexWorkerProcess()
        {
            StandardOutput = new StreamReader(Output, Encoding.UTF8, false, 1024, leaveOpen: true);
            StandardError = new StreamReader(Error, Encoding.UTF8, false, 1024, leaveOpen: true);
        }

        public event EventHandler? Exited
        {
            add => _exited += value;
            remove => _exited -= value;
        }

        internal ScriptedReadStream Output { get; } = new();

        internal ScriptedReadStream Error { get; } = new();

        internal RecordingTextWriter StandardInputWriter { get; } = new();

        internal bool StartResult { get; set; } = true;

        internal Exception? StartException { get; set; }

        internal bool HasExitedValue { get; set; }

        internal Exception? IdException { get; set; }

        internal long PeakWorkingSetBytesValue { get; set; }

        internal Exception? RefreshException { get; set; }

        internal Exception? KillException { get; set; }

        internal Exception? DisposeException { get; set; }

        internal int KillCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public bool HasExited => HasExitedValue;

        public int Id => IdException is null ? 42 : throw IdException;

        public nint Handle => 1;

        public long PeakWorkingSetBytes => PeakWorkingSetBytesValue;

        public TextWriter StandardInput => StandardInputWriter;

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public bool Start()
        {
            if (StartException is not null)
                throw StartException;
            return StartResult;
        }

        public void Refresh()
        {
            if (RefreshException is not null)
                throw RefreshException;
        }

        public void Kill()
        {
            KillCount++;
            if (KillException is not null)
                throw KillException;
            HasExitedValue = true;
            _exited?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            DisposeCount++;
            Output.Complete();
            Error.Complete();
            if (DisposeException is not null)
                throw DisposeException;
        }
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        internal Exception? AsyncWriteException { get; set; }

        internal Exception? AsyncFlushException { get; set; }

        internal Exception? SyncWriteException { get; set; }

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
            => AsyncWriteException is null ? Task.CompletedTask : Task.FromException(AsyncWriteException);

        public override Task FlushAsync(CancellationToken cancellationToken)
            => AsyncFlushException is null ? Task.CompletedTask : Task.FromException(AsyncFlushException);

        public override void WriteLine(string? value)
        {
            if (SyncWriteException is not null)
                throw SyncWriteException;
        }
    }

    private sealed class ScriptedReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        internal TaskCompletionSource CompletionObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void WriteLine(string line)
            => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(line + Environment.NewLine));

        internal void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                try
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                    {
                        CompletionObserved.TrySetResult();
                        return 0;
                    }
                }
                catch (Exception exception)
                {
                    CompletionObserved.TrySetResult();
                    throw new IOException("Scripted stream failed.", exception);
                }

                if (_chunks.Reader.TryRead(out byte[]? chunk))
                {
                    _current = chunk;
                    _offset = 0;
                }
            }

            int copied = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Complete();
            base.Dispose(disposing);
        }
    }

    private static bool TrustWorker(string workerPath, out string failure)
    {
        failure = string.Empty;
        return true;
    }

    private static bool RejectWorker(string workerPath, out string failure)
    {
        failure = "test trust failure";
        return false;
    }

    private static void SetPrivateField(IndexWorkerClient client, string name, object? value)
        => typeof(IndexWorkerClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, value);

    private static void InvokePrivate(IndexWorkerClient client, string name, params object?[] arguments)
        => typeof(IndexWorkerClient).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, arguments);

    private static string NonexistentWorker()
        => Path.Combine(Path.GetTempPath(), "yagu-no-such-index-worker-" + Guid.NewGuid().ToString("N") + ".exe");

    private string FakeWorker(string scenario)
    {
        string sourceDirectory = FindFakeWorkerOutput();
        string executable = Path.Combine(_sandbox, $"query-{scenario}.exe");
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
    {
        string repo = FindRepoRoot();
        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string directory = Path.Combine(repo, "tests", "Yagu.FakeIndexWorker", "bin", configuration, "net10.0");
            if (File.Exists(Path.Combine(directory, "Yagu.FakeIndexWorker.exe")))
                return directory;
        }
        throw new FileNotFoundException("The fake index worker was not built.");
    }

    private static string? FindWorkerExe()
    {
        string repoRoot = FindRepoRoot();
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(repoRoot, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
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
