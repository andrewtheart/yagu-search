using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage-3 exit gate at the pipeline level (plan §5.3 slice 4): the <see cref="ContentIndexShadowPipeline"/>
/// must consume every offered candidate and complete WITHOUT hanging or throwing under any worker fault, and
/// — because it never prunes — leave the search free to live-scan every path (result multiset == live scan).
/// A clean run records exactly the would-prune paths to the recovery spool and that spool replays them
/// verbatim (the Stage-4 backstop). Driven against the <c>Yagu.FakeIndexWorker</c> (self-gates when unbuilt).
/// </summary>
public sealed class ContentIndexShadowPipelineFaultTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-pipe-fault", Guid.NewGuid().ToString("N"));

    public ContentIndexShadowPipelineFaultTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static readonly string[] Offered =
    {
        IndexScopeIdentity.NormalizePath(@"C:\pf\a.txt"),
        IndexScopeIdentity.NormalizePath(@"C:\pf\b.txt"),
        IndexScopeIdentity.NormalizePath(@"C:\pf\c.txt"),
    };

    private ContentIndexClassifyBatcher SmallBatcher()
        => new(maxPaths: 2, maxEncodedBytes: 1_000_000, maxLatency: TimeSpan.FromMilliseconds(20));

    private async Task<ContentIndexShadowPipeline.ShadowPipelineMetrics> RunAsync(string scenario, ContentIndexRecoverySpool spool)
    {
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker(scenario));
        var pipeline = new ContentIndexShadowPipeline(client, spool, SmallBatcher(), 1, TimeSpan.FromMilliseconds(20), 64);

        bool opened = await pipeline.OpenAsync(new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None);
        Assert.True(opened); // the fake reports the scope accelerable; the fault (if any) is on classify

        foreach (string path in Offered)
            await pipeline.OfferAsync(path, CancellationToken.None);

        return await pipeline.CompleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Pipeline_HappyPath_SpoolsEveryWouldPrune_AndSpoolReplaysThemVerbatim()
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        // "queryHappy" is not a fault name → the fake classifies every path as a fresh Nonmember (would-prune).
        ContentIndexShadowPipeline.ShadowPipelineMetrics metrics = await RunAsync("queryHappy", spool);

        Assert.True(metrics.Accelerable);
        Assert.Equal(Offered.Length, metrics.Offered);
        Assert.Equal(Offered.Length, metrics.Classified);
        Assert.Equal(Offered.Length, metrics.WouldPrune);

        // Every would-prune path was appended and the recovery spool replays them verbatim, in order — the
        // Stage-4 backstop that guarantees nothing pruned is ever lost.
        Assert.Equal(Offered.Length, spool.Count);
        Assert.Equal(Offered, spool.ReplayAll().ToArray());
    }

    [Theory]
    [InlineData("classifyCrash")]
    [InlineData("classifyMalformed")]
    public async Task Pipeline_ClassifyFault_DrainsAllOffers_DegradesGracefully_SpoolsNothing(string scenario)
    {
        if (FindFakeWorkerOutputOrNull() is null)
            return;

        using var spool = ContentIndexRecoverySpool.Create(_sandbox);
        ContentIndexShadowPipeline.ShadowPipelineMetrics metrics = await RunAsync(scenario, spool);

        // The worker fault must not hang or throw: completion returns non-accelerable, but every offered path
        // was still drained from the channel (so discovery never deadlocked and the search live-scans them).
        Assert.False(metrics.Accelerable);
        Assert.Equal(Offered.Length, metrics.Offered);
        Assert.Equal(0, metrics.WouldPrune); // a failed classify prunes nothing
        Assert.Equal(0, spool.Count);
    }

    // ── Fake-worker harness (mirrors IndexWorkerClientTests) ──

    private string FakeWorker(string scenario)
    {
        string sourceDirectory = FindFakeWorkerOutput();
        string executable = Path.Combine(_sandbox, $"pipe-fault-{scenario}.exe");
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
