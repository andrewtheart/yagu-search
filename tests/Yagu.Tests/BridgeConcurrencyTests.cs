using System.Diagnostics;
using Yagu.McpServer;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// How many bridged calls may run at once, per workload and across server processes.
/// </summary>
/// <remarks>
/// This matters more than it looks. A machine can now host several Yagu MCP servers at once — one per AI
/// client that registered Yagu, plus the sign-in helper — and each is a separate process with its own
/// in-process counters. Nothing but the cross-process gate stops three of them from loading a model whose
/// measured peak working set is ~2.65 GB.
/// </remarks>
public class BridgeConcurrencyTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>Isolated per test, so a live server on this machine cannot block the suite (or vice versa).</summary>
    private static string GateName() => "Yagu.Mcp.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task LightWork_RunsUpToTheGlobalLimitConcurrently()
    {
        using var concurrency = new BridgeConcurrency(3, GateName());

        IDisposable a = await concurrency.AcquireAsync(BridgeWorkload.Light, CancellationToken.None);
        IDisposable b = await concurrency.AcquireAsync(BridgeWorkload.Light, CancellationToken.None);
        IDisposable c = await concurrency.AcquireAsync(BridgeWorkload.Light, CancellationToken.None);

        Task<IDisposable> fourth = concurrency.AcquireAsync(BridgeWorkload.Light, CancellationToken.None);
        Assert.False(fourth.IsCompleted, "a fourth call ran despite a limit of three.");

        a.Dispose();
        (await fourth.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        b.Dispose();
        c.Dispose();
    }

    [Fact]
    public async Task IndexMutations_AreSerialized()
    {
        // Two builds of the same index fight over a writer; the engine would throw, but queueing is the
        // behaviour a caller can actually use.
        using var concurrency = new BridgeConcurrency(8, GateName());

        IDisposable first = await concurrency.AcquireAsync(BridgeWorkload.IndexMutation, CancellationToken.None);
        Task<IDisposable> second = concurrency.AcquireAsync(BridgeWorkload.IndexMutation, CancellationToken.None);

        await Task.Delay(Settle);
        Assert.False(second.IsCompleted, "a second index mutation started while one was already running.");

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ModelWork_IsSerializedWithinOneServer()
    {
        using var concurrency = new BridgeConcurrency(8, GateName());

        IDisposable first = await concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);
        Task<IDisposable> second = concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);

        await Task.Delay(Settle);
        Assert.False(second.IsCompleted, "two model loads ran at once inside one server.");

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ModelWork_IsSerializedAcrossServers()
    {
        // The point of the whole class. Two BridgeConcurrency instances stand in for two server
        // processes: they share only the named gate, exactly as separate processes would.
        string shared = GateName();
        using var serverOne = new BridgeConcurrency(8, shared);
        using var serverTwo = new BridgeConcurrency(8, shared);

        Assert.True(serverOne.HasMachineWideModelGate, "no cross-process gate was created.");

        IDisposable held = await serverOne.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);
        Task<IDisposable> other = serverTwo.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);

        await Task.Delay(Settle);
        Assert.False(other.IsCompleted, "a second server loaded a model while the first was still loading one.");

        held.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ModelWork_DoesNotBlockOrdinarySearches()
    {
        // A long model load must not make the server look hung for everything else.
        string shared = GateName();
        using var concurrency = new BridgeConcurrency(4, shared);

        IDisposable model = await concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);

        IDisposable search = await concurrency
            .AcquireAsync(BridgeWorkload.Search, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        search.Dispose();
        model.Dispose();
    }

    [Fact]
    public async Task ReleasingReturnsCapacity()
    {
        using var concurrency = new BridgeConcurrency(1, GateName());

        for (int i = 0; i < 5; i++)
        {
            IDisposable lease = await concurrency
                .AcquireAsync(BridgeWorkload.Search, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            lease.Dispose();
        }
    }

    [Fact]
    public void DisposingALeaseTwice_DoesNotReleaseTwice()
    {
        // A double release would hand out capacity that does not exist, which is how a limit silently
        // stops being a limit.
        using var concurrency = new BridgeConcurrency(1, GateName());

        IDisposable lease = concurrency.AcquireAsync(BridgeWorkload.Search, CancellationToken.None).GetAwaiter().GetResult();
        lease.Dispose();
        lease.Dispose();

        IDisposable next = concurrency.AcquireAsync(BridgeWorkload.Search, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        Task<IDisposable> blocked = concurrency.AcquireAsync(BridgeWorkload.Search, CancellationToken.None);
        Assert.False(blocked.IsCompleted, "the limit was exceeded, so a lease was released more than once.");

        next.Dispose();
        blocked.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Dispose();
    }

    [Fact]
    public async Task QueuedModelWork_DoesNotConsumeEveryGlobalPermit()
    {
        // Taking the global permit BEFORE the exclusive gate parks each queued model call on a global
        // slot while it waits for a gate only one of them can hold, so a couple of them starve every
        // ordinary search for the length of a model run.
        using var concurrency = new BridgeConcurrency(2, GateName());

        IDisposable model = await concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);
        Task<IDisposable> queuedModel = concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);

        await Task.Delay(Settle);
        Assert.False(queuedModel.IsCompleted, "the second model call did not queue, so this test proves nothing.");

        IDisposable search = await concurrency
            .AcquireAsync(BridgeWorkload.Search, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        search.Dispose();
        model.Dispose();
        (await queuedModel.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task AnAbandonedCrossProcessGate_DoesNotStrandModelWorkForever()
    {
        // A named semaphore has no owner-abandonment semantics: a server that dies holding the permit
        // strands it while any handle keeps the object alive. Waiting forever would mean model work on
        // this machine never runs again, so the wait is bounded and degrades to in-process gating.
        string shared = GateName();
        using var holder = new BridgeConcurrency(4, shared);
        using var waiter = new BridgeConcurrency(4, shared, TimeSpan.FromMilliseconds(400));

        Assert.True(holder.HasMachineWideModelGate, "no cross-process gate was created.");

        // Taken and deliberately never released, standing in for a server that died holding it.
        IDisposable stranded = await holder.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);

        IDisposable recovered = await waiter
            .AcquireAsync(BridgeWorkload.Model, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        recovered.Dispose();
        stranded.Dispose();
    }

    [Fact]
    public async Task CancellingAQueuedIndexMutation_DoesNotReleaseTheRunningOnesPermit()
    {
        // The global limit is deliberately high so the second caller queues on the INDEX semaphore
        // rather than the global one. That is the only arrangement where releasing "whatever is at
        // CurrentCount == 0" on cancellation steals the permit the RUNNING mutation is holding — and it
        // is the arrangement a real server is always in, because the global limit is never 1.
        using var concurrency = new BridgeConcurrency(8, GateName());

        IDisposable held = await concurrency.AcquireAsync(BridgeWorkload.IndexMutation, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<IDisposable> queued = concurrency.AcquireAsync(BridgeWorkload.IndexMutation, cts.Token);
        await Task.Delay(Settle);
        Assert.False(queued.IsCompleted, "the second mutation did not queue, so this test proves nothing.");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        Task<IDisposable> third = concurrency.AcquireAsync(BridgeWorkload.IndexMutation, CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(
            third.IsCompleted,
            "a second index mutation started while the first was still running: the cancelled waiter released a permit it never took.");

        held.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task CancellingAWaitDoesNotLeakCapacity()
    {
        using var concurrency = new BridgeConcurrency(1, GateName());

        IDisposable held = await concurrency.AcquireAsync(BridgeWorkload.Search, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<IDisposable> queued = concurrency.AcquireAsync(BridgeWorkload.Search, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        held.Dispose();

        // If the cancelled waiter had leaked a slot, this would never complete.
        IDisposable after = await concurrency
            .AcquireAsync(BridgeWorkload.Search, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        after.Dispose();
    }

    [Fact]
    public async Task WaitingForTheModelGateStaysCancellable()
    {
        string shared = GateName();
        using var serverOne = new BridgeConcurrency(4, shared);
        using var serverTwo = new BridgeConcurrency(4, shared);

        IDisposable held = await serverOne.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<IDisposable> queued = serverTwo.AcquireAsync(BridgeWorkload.Model, cts.Token);

        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "cancelling the model wait took too long.");
        held.Dispose();
    }

    [Fact]
    public void ProductionGateNameIsSharedByEveryServer()
        => Assert.Equal("Yagu.Mcp.ModelLoad", BridgeConcurrency.ModelGateName);

    [Fact]
    public void DefaultGateName_CreatesTheProductionMachineWideGate()
    {
        using var concurrency = new BridgeConcurrency(1);

        Assert.True(concurrency.HasMachineWideModelGate);
    }

    [Fact]
    public async Task CancellingWhileQueuedOnGlobal_GivesBackOnlyThePermitsThatWaiterTook()
    {
        using var concurrency = new BridgeConcurrency(1, GateName());

        // Occupies the single global permit without taking any per-workload gate.
        IDisposable light = await concurrency.AcquireAsync(BridgeWorkload.Light, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<IDisposable> queued = concurrency.AcquireAsync(BridgeWorkload.IndexMutation, cts.Token);
        await Task.Delay(Settle);

        // The waiter now holds the index-mutation gate and is parked on global. Cancelling must hand
        // that gate back; inferring ownership from CurrentCount instead would release the global permit
        // this waiter never took, and two index mutations could then run at once.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));

        light.Dispose();

        IDisposable after = await concurrency.AcquireAsync(BridgeWorkload.IndexMutation, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        after.Dispose();
    }

    [Fact]
    public async Task CancellingAQueuedModelCall_ReleasesTheCrossProcessGateForOtherServers()
    {
        string shared = GateName();
        var beforeGlobalWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var concurrency = new BridgeConcurrency(
            1,
            shared,
            onBeforeGlobalWait: workload =>
            {
                if (workload == BridgeWorkload.Model)
                    beforeGlobalWait.TrySetResult();
            });
        using var peer = new BridgeConcurrency(1, shared);

        Assert.True(concurrency.HasMachineWideModelGate);

        IDisposable light = await concurrency.AcquireAsync(BridgeWorkload.Light, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<IDisposable> queued = concurrency.AcquireAsync(BridgeWorkload.Model, cts.Token);
        await beforeGlobalWait.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        light.Dispose();

        // A named semaphore has no owner-abandonment semantics, so a permit stranded here would block
        // model work in every other server on the machine until one of them exits.
        IDisposable peerLease = await peer.AcquireAsync(BridgeWorkload.Model, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        peerLease.Dispose();
    }

    [Fact]
    public async Task FailureAfterAcquiringEveryGate_ReturnsAllCapacity()
    {
        string shared = GateName();
        int acquisitionCount = 0;
        using var concurrency = new BridgeConcurrency(
            1,
            shared,
            onAcquired: () =>
            {
                if (Interlocked.Increment(ref acquisitionCount) == 1)
                    throw new InvalidOperationException("injected setup failure");
            });
        using var peer = new BridgeConcurrency(1, shared);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None));
        Assert.Equal("injected setup failure", error.Message);

        IDisposable retry = await concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        retry.Dispose();

        IDisposable peerLease = await peer.AcquireAsync(BridgeWorkload.Model, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        peerLease.Dispose();
    }

    [Fact]
    public async Task AGateNameTheOsRejects_FallsBackToInProcessSerializationInsteadOfFailing()
    {
        // A backslash past the namespace prefix is not a legal kernel object name, so both the Global\
        // and Local\ attempts fail. Model work must still be serialized, just without cross-process reach.
        using var concurrency = new BridgeConcurrency(4, "yagu-test\\rejected\\" + Guid.NewGuid().ToString("N"));

        Assert.False(concurrency.HasMachineWideModelGate);

        IDisposable first = await concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);
        Task<IDisposable> second = concurrency.AcquireAsync(BridgeWorkload.Model, CancellationToken.None);
        await Task.Delay(Settle);

        Assert.False(second.IsCompleted, "a second model load ran while the first still held the gate.");

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
