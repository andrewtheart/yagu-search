using System;
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
/// End-to-end tests for <see cref="ContentIndexShadowPipeline"/> — the Stage-3 async classification stage.
/// Driven against the real bundled worker (self-gated when it is not built), it must batch every offered
/// path through the worker, classify identically to the in-process oracle (zero mismatches), record exactly
/// the would-prune (fresh nonmember) paths to the recovery spool, and — being shadow — never affect the
/// result set. Its graceful-degradation contract (worker unavailable → not accelerable, offers are dropped)
/// is covered without a worker.
/// </summary>
public sealed class ContentIndexShadowPipelineTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-shadow-pipe", Guid.NewGuid().ToString("N"));

    public ContentIndexShadowPipelineTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration BuildGeneration(string root)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(root + "\\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        builder.AddDocument(root + "\\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        builder.AddDocument(root + "\\d.txt", Encoding.UTF8.GetBytes("unrelated filler content and words"));
        return builder.Build("scope", "vol", root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\sp", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    private static string Norm(string root, string file) => IndexScopeIdentity.NormalizePath(root + "\\" + file);

    private static string? FindWorkerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(dir.FullName, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private ContentIndexClassifyBatcher SmallBatcher()
        => new(maxPaths: 2, maxEncodedBytes: 1_000_000, maxLatency: TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Pipeline_MissingWorker_IsNotAccelerable_AndOffersAreDropped()
    {
        using var client = new IndexWorkerClient(workerPathOverride: Path.Combine(_sandbox, "no-worker.exe"));
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = new ContentIndexShadowPipeline(client, spool, SmallBatcher(), 1, TimeSpan.FromMilliseconds(20), 64);

        bool opened = await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None);
        Assert.False(opened);

        // Offers are dropped (no-op) when the pipeline never opened; complete returns non-accelerable.
        await pipeline.OfferAsync(@"c:\sp\a.txt", CancellationToken.None);
        ContentIndexShadowPipeline.ShadowPipelineMetrics metrics = await pipeline.CompleteAsync(CancellationToken.None);

        Assert.False(metrics.Accelerable);
        Assert.Equal(0, spool.Count);
    }

    [Fact]
    public async Task Pipeline_RealWorker_ClassifiesLikeOracle_AndSpoolsExactlyTheWouldPrunePaths()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\sp";
        ContentIndexGeneration gen = BuildGeneration(root);
        string baseDir = Path.Combine(_sandbox, "base");
        Directory.CreateDirectory(baseDir);
        ContentIndexV3Format.Write(baseDir, gen);

        TrigramExpression query = PlanQuery("planner");
        IReadOnlySet<int> candidates = gen.Postings.EvaluateSet(query);
        var oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());

        // Every discovered path (a..d present; z absent). "planner" members = a,c; nonmembers = b,d (would-prune).
        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"), Norm(root, "d.txt"), Norm(root, "z.txt") };
        var expectedWouldPrune = paths
            .Where(p => IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(p)) == IndexQueryWorkerProtocol.Verdicts.Nonmember)
            .ToArray();
        Assert.Equal(2, expectedWouldPrune.Length); // b.txt, d.txt

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        var pipeline = new ContentIndexShadowPipeline(
            client, spool, SmallBatcher(), sessionId: 5, TimeSpan.FromMilliseconds(20), channelCapacity: 4,
            oracleVerdict: p => IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(p)));

        var scope = new IndexQueryOpenRequest
        {
            SessionId = 5,
            BaseDir = baseDir,
            BaseCandidatesBase64 = IndexWorkerProtocol.EncodeCandidates(candidates.ToArray()),
        };
        Assert.True(await pipeline.OpenAsync(scope, CancellationToken.None));

        foreach (string p in paths)
            await pipeline.OfferAsync(p, CancellationToken.None);

        ContentIndexShadowPipeline.ShadowPipelineMetrics metrics = await pipeline.CompleteAsync(CancellationToken.None);

        Assert.True(metrics.Accelerable, metrics.BypassReason);
        Assert.Equal(0, metrics.Mismatches);            // worker == oracle for every path
        Assert.Equal(paths.Length, metrics.Classified); // every offered path classified
        Assert.Equal(paths.Length, metrics.Offered);
        Assert.Equal(expectedWouldPrune.Length, metrics.WouldPrune);
        Assert.True(metrics.Batches >= 2);              // batcher.maxPaths=2 over 5 paths → multiple batches

        // The recovery spool recorded exactly the fresh-nonmember paths (order-independent set compare).
        string[] spooled = spool.ReplayAll().ToArray();
        Assert.Equal(expectedWouldPrune.OrderBy(x => x, StringComparer.Ordinal),
                     spooled.OrderBy(x => x, StringComparer.Ordinal));
        spool.Complete();
    }
}
