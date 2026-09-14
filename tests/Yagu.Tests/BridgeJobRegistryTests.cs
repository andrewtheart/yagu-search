using Yagu.McpServer;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Admission and retention for long-running bridged operations.
/// </summary>
/// <remarks>
/// Retention alone bounds nothing. Pruning only removes FINISHED jobs, so a client that keeps starting
/// index builds or model-backed searches can queue unlimited work and pin unlimited result text — and
/// every one of those jobs is a child <c>Yagu.exe</c> process. The limits here are what make an
/// authenticated-but-hostile client a bounded problem rather than an out-of-memory one.
/// </remarks>
public class BridgeJobRegistryTests
{
    private static Task<string> Blocks(TaskCompletionSource gate, CancellationToken token)
        => gate.Task.WaitAsync(token).ContinueWith(_ => "done", TaskScheduler.Default);

    [Fact]
    public async Task FinishedJobs_NeverExceedTheRetentionLimit()
    {
        // Pruning runs before the incoming job is added, so returning at exactly the maximum leaves the
        // registry parked one job over its configured limit forever.
        const int retained = 4;
        using var registry = new BridgeJobRegistry(maxRetained: retained, maxRunning: 4);

        for (int i = 0; i < 20; i++)
        {
            BridgeJob? job = registry.Start("index-validate", $"job {i}", _ => Task.FromResult("ok"));
            Assert.NotNull(job);
            await WaitUntilFinished(job!);
        }

        Assert.True(
            registry.All().Count <= retained,
            $"retained {registry.All().Count} jobs against a limit of {retained}.");
    }

    [Fact]
    public async Task ACancelledJob_IsReportedAsCancelledRatherThanCompleted()
    {
        // The bridge turns cancellation into an ordinary return value, so a job that reported it as a
        // string would be recorded as Completed with an error body.
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 4);
        var started = new TaskCompletionSource();

        BridgeJob? job = registry.Start("nl-search", "cancel me", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });

        Assert.NotNull(job);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(registry.Cancel(job!.Id));
        await WaitUntilFinished(job);

        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public void RunningJobs_AreRefusedOnceTheLimitIsReached()
    {
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 2);
        var gate = new TaskCompletionSource();

        Assert.NotNull(registry.Start("index-build", "first", token => Blocks(gate, token)));
        Assert.NotNull(registry.Start("index-build", "second", token => Blocks(gate, token)));

        Assert.Null(registry.Start("index-build", "third", token => Blocks(gate, token)));

        gate.SetResult();
    }

    [Fact]
    public async Task ASlotIsReturnedWhenAJobFinishes()
    {
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 1);
        var gate = new TaskCompletionSource();

        BridgeJob? first = registry.Start("index-build", "first", token => Blocks(gate, token));
        Assert.NotNull(first);
        Assert.Null(registry.Start("index-build", "second", token => Blocks(gate, token)));

        gate.SetResult();
        await WaitUntilFinished(first!);

        Assert.NotNull(registry.Start("index-build", "third", _ => Task.FromResult("ok")));
    }

    [Fact]
    public async Task AnEnormousResult_IsBoundedBeforeItIsRetained()
    {
        // A completed job holds its whole result string until it is pruned. Without a cap, one search
        // over a large tree keeps hundreds of megabytes resident for the life of the server.
        const int cap = 8 * 1024;
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 4, maxResultChars: cap);

        BridgeJob? job = registry.Start("nl-search", "huge", _ => Task.FromResult(new string('x', 5_000_000)));
        Assert.NotNull(job);

        await WaitUntilFinished(job!);

        Assert.Equal(JobState.Completed, job!.State);
        Assert.NotNull(job.Result);
        Assert.True(
            job.Result!.Length < cap * 2,
            $"the retained result was {job.Result.Length:N0} characters against a {cap:N0}-character cap.");
        Assert.Contains("truncated", job.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultInsideTheCap_IsRetainedVerbatim()
    {
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 4, maxResultChars: 8 * 1024);

        BridgeJob? job = registry.Start("index-validate", "small", _ => Task.FromResult("all good"));
        Assert.NotNull(job);

        await WaitUntilFinished(job!);

        Assert.Equal("all good", job!.Result);
    }

    [Fact]
    public void RefusedJobs_AreNotRegisteredOrListed()
    {
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 1);
        var gate = new TaskCompletionSource();

        registry.Start("index-build", "first", token => Blocks(gate, token));
        registry.Start("index-build", "refused", token => Blocks(gate, token));

        Assert.Single(registry.All());

        gate.SetResult();
    }

    [Fact]
    public void MaxRunningJobs_ReportsTheConfiguredLimit()
    {
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: 3);

        // A refusal message quotes this, so it must agree with what admission actually enforces.
        Assert.Equal(3, registry.MaxRunningJobs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MaxRunningJobs_IsNeverBelowOne(int configured)
    {
        using var registry = new BridgeJobRegistry(maxRetained: 64, maxRunning: configured);

        // A zero limit would refuse every job forever rather than degrade.
        Assert.Equal(1, registry.MaxRunningJobs);
        Assert.NotNull(registry.Start("index-validate", "d", _ => Task.FromResult("ok")));
    }

    [Fact]
    public async Task AFailedJob_RecordsTheFailureRatherThanAResult()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? job = registry.Start("index-build", "boom",
            _ => Task.FromException<string>(new InvalidOperationException("disk is on fire")));
        Assert.NotNull(job);

        await WaitUntilFinished(job!);

        Assert.Equal(JobState.Failed, job!.State);
        Assert.Equal("disk is on fire", job.Failure);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Find_LocatesAStartedJobAndIgnoresAnUnknownId()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? job = registry.Start("index-validate", "d", _ => Task.FromResult("ok"));
        Assert.NotNull(job);
        await WaitUntilFinished(job!);

        Assert.Same(job, registry.Find(job!.Id));
        Assert.Null(registry.Find("no-such-job"));
    }

    [Fact]
    public async Task All_ReturnsTheMostRecentlyStartedJobFirst()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? first = registry.Start("index-validate", "first", _ => Task.FromResult("a"));
        await Task.Delay(15);
        BridgeJob? second = registry.Start("index-validate", "second", _ => Task.FromResult("b"));

        IReadOnlyList<BridgeJob> all = registry.All();

        Assert.Equal(2, all.Count);
        Assert.Same(second, all[0]);
        Assert.Same(first, all[1]);
    }

    [Fact]
    public void Cancel_UnknownJob_ReportsFailureRatherThanThrowing()
    {
        using var registry = new BridgeJobRegistry();

        Assert.False(registry.Cancel("no-such-job"));
    }

    [Fact]
    public async Task Cancel_AFinishedJob_IsRefused()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? job = registry.Start("index-validate", "d", _ => Task.FromResult("ok"));
        Assert.NotNull(job);
        await WaitUntilFinished(job!);

        // Cancelling something already finished would report success for work that was never stopped.
        Assert.False(registry.Cancel(job!.Id));
    }

    [Fact]
    public async Task Cancel_ARunningJob_StopsIt()
    {
        using var registry = new BridgeJobRegistry();
        var started = new TaskCompletionSource();

        // Blocks() resolves its continuation even when cancelled, so cancellation needs a body that
        // actually propagates it.
        BridgeJob? job = registry.Start("index-build", "long", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });

        Assert.NotNull(job);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(registry.Cancel(job!.Id));
        await WaitUntilFinished(job);

        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task Describe_ReportsEachTerminalStateDistinctly()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? completed = registry.Start("index-validate", "finished work", _ => Task.FromResult("the answer"));
        Assert.NotNull(completed);
        await WaitUntilFinished(completed!);

        string description = BridgeJobRegistry.Describe(completed!);
        Assert.Contains(completed!.Id, description);
        Assert.Contains("completed", description);
        Assert.Contains("the answer", description);
    }

    [Fact]
    public void Describe_ARunningJob_ReportsElapsedTimeAndTheDescription()
    {
        using var registry = new BridgeJobRegistry();
        var gate = new TaskCompletionSource();

        BridgeJob? job = registry.Start("index-build", "rebuilding C:", token => Blocks(gate, token));
        Assert.NotNull(job);

        string description = BridgeJobRegistry.Describe(job!);

        Assert.Contains("running", description);
        Assert.Contains("rebuilding C:", description);
        Assert.Contains("elapsed", description);

        gate.SetResult();
    }

    [Fact]
    public async Task Describe_ACancelledJob_SaysCancelledAndCarriesNoResult()
    {
        using var registry = new BridgeJobRegistry();
        var started = new TaskCompletionSource();

        BridgeJob? job = registry.Start("index-build", "long", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });

        Assert.NotNull(job);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Cancel(job!.Id);
        await WaitUntilFinished(job);

        string description = BridgeJobRegistry.Describe(job);

        Assert.Contains("cancelled", description);
        Assert.DoesNotContain("unreachable", description);
    }

    [Fact]
    public async Task Describe_AFailedJob_CarriesTheFailureText()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? job = registry.Start("index-build", "d",
            _ => Task.FromException<string>(new InvalidOperationException("disk is on fire")));
        Assert.NotNull(job);
        await WaitUntilFinished(job!);

        string description = BridgeJobRegistry.Describe(job!);

        Assert.Contains("failed", description);
        Assert.Contains("disk is on fire", description);
    }

    [Fact]
    public async Task Elapsed_StopsAdvancingOnceTheJobFinishes()
    {
        using var registry = new BridgeJobRegistry();

        BridgeJob? job = registry.Start("index-validate", "d", _ => Task.FromResult("ok"));
        Assert.NotNull(job);
        await WaitUntilFinished(job!);

        TimeSpan first = job!.Elapsed;
        await Task.Delay(30);

        Assert.Equal(first, job.Elapsed);
    }

    [Fact]
    public void Dispose_CancelsRunningWorkAndEmptiesTheRegistry()
    {
        var registry = new BridgeJobRegistry();
        var gate = new TaskCompletionSource();

        BridgeJob? job = registry.Start("index-build", "long", token => Blocks(gate, token));
        Assert.NotNull(job);

        registry.Dispose();

        // Jobs die with the server: a job whose owner has gone has nobody to report to.
        Assert.Empty(registry.All());
        Assert.Null(registry.Find(job!.Id));

        gate.TrySetResult();
    }

    [Fact]
    public void Dispose_IsSafeWhenNothingWasEverStarted()
    {
        var registry = new BridgeJobRegistry();

        registry.Dispose();
        registry.Dispose();
    }

    [Fact]
    public async Task DisposedCancellationSource_IsHandledByCancelAndShutdown()
    {
        var registry = new BridgeJobRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        BridgeJob? job = registry.Start("index-build", "disposing", async _ =>
        {
            started.TrySetResult();
            await finish.Task;
            return "done";
        });
        Assert.NotNull(job);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        job!.Cancellation.Dispose();

        Assert.False(registry.Cancel(job.Id));
        registry.Dispose();
        Assert.Empty(registry.All());

        finish.TrySetResult();
    }

    private static async Task WaitUntilFinished(BridgeJob job)
    {
        for (int i = 0; i < 200 && job.State == JobState.Running; i++)
            await Task.Delay(25);

        Assert.NotEqual(JobState.Running, job.State);
    }
}
