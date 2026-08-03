using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage-3 fault-injection gate (plan §5.2/§6 slice 4): every adversarial worker behavior on a mapped query
/// session — a crash mid-batch, a malformed reply, a busy rejection, a stale (wrong epoch / wrong batch)
/// reply, a duplicate reply, and a hung worker past the deadline — must make <see cref="IndexWorkerClient"/>
/// degrade to a null classify result (the caller live-scans that batch), never throw and never hang. This is
/// what lets the shadow pipeline keep the search's result set identical to a live scan under any fault. Driven
/// against the <c>Yagu.FakeIndexWorker</c> with a per-scenario fault (self-gates when it is not built).
/// </summary>
public sealed class IndexWorkerQueryFaultTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-query-fault", Guid.NewGuid().ToString("N"));

    public IndexWorkerQueryFaultTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static readonly string[] Paths = { @"c:\qf\a.txt", @"c:\qf\b.txt", @"c:\qf\c.txt" };

    private async Task<IndexWorkerClient> OpenAcceleratedSessionAsync(string scenario, int sessionId)
    {
        var client = new IndexWorkerClient(workerPathOverride: FakeWorker(scenario));
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(
            new IndexQueryOpenRequest { SessionId = sessionId, BaseDir = _sandbox }, CancellationToken.None);
        Assert.NotNull(open);
        Assert.True(open!.Accelerable, open.BypassReason);
        return client;
    }

    [Fact]
    public async Task Open_NotReadyScope_ReportsNotAccelerable()
    {
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker("queryOpenNotReady"));
        Assert.True(await client.EnsureReadyAsync(CancellationToken.None));
        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(
            new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None);
        Assert.NotNull(open);
        Assert.False(open!.Accelerable); // → the host live-scans this scope
    }

    [Theory]
    [InlineData("queryReject")]
    [InlineData("queryOpenMissingResult")]
    [InlineData("queryOpenNullResult")]
    public async Task Open_RejectedOrNullResult_ReturnsNull(string scenario)
    {
        using var client = new IndexWorkerClient(workerPathOverride: FakeWorker(scenario));

        IndexQueryOpenResult? open = await client.OpenQueryScopeAsync(
            new IndexQueryOpenRequest { SessionId = 1, BaseDir = _sandbox }, CancellationToken.None);

        Assert.Null(open);
    }

    [Fact]
    public async Task Classify_WorkerCrashMidBatch_ReturnsNull()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyCrash", 1);
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);
        Assert.Null(verdicts);
    }

    [Theory]
    [InlineData("classifyMalformed")]
    [InlineData("classifyMalformedResult")]
    public async Task Classify_MalformedReply_ReturnsNull(string scenario)
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync(scenario, 1);
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);
        Assert.Null(verdicts);
    }

    [Fact]
    public async Task Classify_BusyReply_ReturnsNull()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyBusy", 1);
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);
        Assert.Null(verdicts);
    }

    [Fact]
    public async Task Classify_NullResult_ReturnsNull()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyNullResult", 1);

        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);

        Assert.Null(verdicts);
    }

    [Fact]
    public async Task Classify_CallerCancellationWhileWaiting_ReturnsNull()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyHang", 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, cancellation.Token, batchSeq: 5);

        Assert.Null(verdicts);
    }

    [Fact]
    public async Task Classify_StaleSequenceReply_IsDropped_ReturnsNull()
    {
        // The worker echoes the WRONG batch sequence → the reply gate drops it (never applies it to batch 5).
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyWrongBatch", 1);
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);
        Assert.Null(verdicts);
    }

    [Fact]
    public async Task Classify_StaleEpochReply_IsDropped_ReturnsNull()
    {
        // The worker stamps a different epoch (as if a restarted worker) → the reply gate drops it.
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyWrongEpoch", 1);
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);
        Assert.Null(verdicts);
    }

    [Fact]
    public async Task Classify_DuplicateReply_IsAppliedExactlyOnce_ReturnsVerdicts()
    {
        // Two identical replies for the same request: the first is applied, the second is dropped by the
        // id-correlated transport (never double-applied / corrupting) → one well-formed verdict array.
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyDuplicate", 1);
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5);
        Assert.NotNull(verdicts);
        Assert.Equal(Paths.Length, verdicts!.Length);
    }

    [Fact]
    public async Task Classify_HungWorker_DeadlineAbandons_ReturnsNull()
    {
        // The worker never replies within the deadline → the host abandons the wait and live-scans the batch.
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("classifyHang", 1);
        long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500;
        byte[]? verdicts = await client.ClassifyPathsAsync(1, Paths, CancellationToken.None, batchSeq: 5, deadlineUnixMs: deadline);
        Assert.Null(verdicts);
    }

    // ── Stage-4 reconcileB1 fault injection (plan §5.5): every adversarial B1 reply must make
    //    ReconcileB1Async fail (Success=false) so the host replays its recovery spool, never hides a match. ──

    private static readonly IReadOnlySet<long> NoDirty = new HashSet<long>();
    private static readonly IReadOnlyList<IReadOnlySet<long>> NoSegmentDirties = Array.Empty<IReadOnlySet<long>>();

    [Theory]
    [InlineData("reconcileCrash")]
    [InlineData("reconcileReject")]
    public async Task ReconcileB1_WorkerFailure_ReturnsFailure(string scenario)
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync(scenario, 1);
        IndexWorkerReconcileResult result = await client.ReconcileB1Async(1, NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        Assert.False(result.Success); // → the host replays its recovery spool (live-scan every prune)
    }

    [Fact]
    public async Task ReconcileB1_MalformedReply_ReturnsFailure()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("reconcileMalformed", 1);
        IndexWorkerReconcileResult result = await client.ReconcileB1Async(1, NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReconcileB1_NullResult_ReturnsFailure()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("reconcileNullResult", 1);

        IndexWorkerReconcileResult result = await client.ReconcileB1Async(
            1, NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReconcileB1_StaleSessionReply_IsDropped_ReturnsFailure()
    {
        // The worker echoes the wrong session id → the reply gate drops it → the host replays its spool.
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("reconcileWrongSession", 1);
        IndexWorkerReconcileResult result = await client.ReconcileB1Async(1, NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReconcileB1_StaleEpochReply_IsDropped_ReturnsFailure()
    {
        // The worker stamps a different epoch (as if a restarted worker) → the reply gate drops it.
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("reconcileWrongEpoch", 1);
        IndexWorkerReconcileResult result = await client.ReconcileB1Async(1, NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReconcileB1_HealthyWorker_Succeeds_AndEchoesCertainty()
    {
        using IndexWorkerClient client = await OpenAcceleratedSessionAsync("normal", 1);

        IndexWorkerReconcileResult certain = await client.ReconcileB1Async(1, NoDirty, NoSegmentDirties, certain: true, CancellationToken.None);
        Assert.True(certain.Success);
        Assert.True(certain.PruningCertain);
        Assert.Empty(certain.RescuePaths);

        IndexWorkerReconcileResult notCertain = await client.ReconcileB1Async(1, NoDirty, NoSegmentDirties, certain: false, CancellationToken.None);
        Assert.True(notCertain.Success);
        Assert.False(notCertain.PruningCertain); // a not-certain reconcile flags the scope unaccelerated
    }

    // ── Fake-worker harness (mirrors IndexWorkerClientTests) ──

    private string FakeWorker(string scenario)
    {
        string sourceDirectory = FindFakeWorkerOutput();
        string executable = Path.Combine(_sandbox, $"query-fault-{scenario}.exe");
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
