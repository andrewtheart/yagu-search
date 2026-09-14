namespace Yagu.McpServer;

/// <summary>What a bridged call costs, which decides how many may run at once.</summary>
internal enum BridgeWorkload
{
    /// <summary>Cheap and bounded: calc, read, index status, roots. Only the global limit applies.</summary>
    Light,

    /// <summary>A file scan. Several are fine; a lot are not.</summary>
    Search,

    /// <summary>Index maintenance. Serialized, because two builds of the same index fight over a writer.</summary>
    IndexMutation,

    /// <summary>Loads an AI model. Serialized <b>across every Yagu MCP server on the machine</b>.</summary>
    Model,
}

/// <summary>
/// Limits how many bridged <c>Yagu.exe</c> calls run at once, per workload and across processes.
/// </summary>
/// <remarks>
/// <para>
/// A single global limit is not enough once the same machine can host several servers at once — one per
/// AI client that registered Yagu, plus the sign-in helper. Each of those is a separate process with its
/// own in-process counters, so nothing stops three of them from starting a natural-language search
/// simultaneously and loading three copies of a model whose measured peak working set is ~2.65 GB.
/// </para>
/// <para>
/// So model work is gated by a <b>named semaphore</b>, which every Yagu MCP server on the machine shares.
/// A semaphore rather than a mutex on purpose: a mutex is thread-affine and must be released by the same
/// thread that took it, which an <c>async</c> continuation cannot promise.
/// </para>
/// </remarks>
internal sealed class BridgeConcurrency : IDisposable
{
    /// <summary>Shared by every Yagu MCP server for this user. Only one model load runs at a time.</summary>
    public const string ModelGateName = "Yagu.Mcp.ModelLoad";

    private readonly SemaphoreSlim _global;
    private readonly SemaphoreSlim _search;
    private readonly SemaphoreSlim _indexMutation = new(1, 1);
    private readonly SemaphoreSlim _model = new(1, 1);
    private readonly Semaphore? _machineWideModelGate;
    private readonly TimeSpan _modelGateTimeout;
    private readonly Action<BridgeWorkload>? _onBeforeGlobalWait;
    private readonly Action? _onAcquired;

    /// <summary>Guards the lease ledger below and signals when the last lease is returned.</summary>
    private readonly object _leaseGate = new();
    private int _outstandingLeases;
    private int _machineWideHeld;
    private bool _disposed;

    /// <summary>
    /// How long <see cref="Dispose"/> waits for in-flight calls to return their leases.
    /// </summary>
    /// <remarks>
    /// Long enough for a cancelled child process to be reaped, short enough not to hang shutdown. The
    /// cross-process permit is released explicitly when it expires, so overrunning it costs tidiness
    /// rather than correctness.
    /// </remarks>
    private static readonly TimeSpan LeaseDrainTimeout = TimeSpan.FromSeconds(5);

    /// <param name="gateName">Overridden only by tests, so they cannot block on a live server's gate.</param>
    /// <param name="modelGateTimeout">How long to wait for the cross-process gate before going it alone.</param>
    /// <param name="onBeforeGlobalWait">Test synchronization hook invoked after workload-specific gates are held.</param>
    /// <param name="onAcquired">Test fault hook invoked after every gate is held.</param>
    public BridgeConcurrency(
        int maxConcurrentCalls,
        string? gateName = null,
        TimeSpan? modelGateTimeout = null,
        Action<BridgeWorkload>? onBeforeGlobalWait = null,
        Action? onAcquired = null)
    {
        int limit = Math.Max(1, maxConcurrentCalls);
        _global = new SemaphoreSlim(limit);

        // Searches are the common case, so they get the global budget rather than a tighter one of
        // their own; the point of the per-workload gates is to stop expensive work from stacking.
        _search = new SemaphoreSlim(limit);
        _modelGateTimeout = modelGateTimeout ?? TimeSpan.FromMinutes(10);
        _machineWideModelGate = TryCreateMachineWideGate(gateName ?? ModelGateName);
        _onBeforeGlobalWait = onBeforeGlobalWait;
        _onAcquired = onAcquired;
    }

    /// <summary>True when model work is serialized across every Yagu MCP server on this machine.</summary>
    public bool HasMachineWideModelGate => _machineWideModelGate is not null;

    /// <summary>
    /// Waits for permission to run, and returns the handle that releases it.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(BridgeWorkload workload, CancellationToken cancellationToken)
    {
        SemaphoreSlim? specific = workload switch
        {
            BridgeWorkload.Search => _search,
            BridgeWorkload.IndexMutation => _indexMutation,
            BridgeWorkload.Model => _model,
            _ => null,
        };

        bool specificTaken = false;
        bool machineWideTaken = false;
        bool globalTaken = false;

        try
        {
            // The EXCLUSIVE gate is taken before the global one. Taking global first parks queued model
            // and index calls on global permits while they wait for a gate only one of them can ever
            // hold, so a few of them starve every ordinary search for the length of a model run.
            if (specific is not null)
            {
                await specific.WaitAsync(cancellationToken).ConfigureAwait(false);
                specificTaken = true;
            }

            if (workload == BridgeWorkload.Model && _machineWideModelGate is not null)
                machineWideTaken = await WaitMachineWideAsync(cancellationToken).ConfigureAwait(false);

            _onBeforeGlobalWait?.Invoke(workload);
            await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalTaken = true;
            _onAcquired?.Invoke();
        }
        catch
        {
            // Release only what THIS waiter actually took, in reverse order. Inferring ownership from
            // CurrentCount releases a permit another caller is holding whenever this one was cancelled
            // while queued, which lets two index mutations run at once.
            if (globalTaken)
                SafeRelease(_global);

            if (machineWideTaken)
                SafeRelease(_machineWideModelGate);

            if (specificTaken)
                SafeRelease(specific);

            throw;
        }

        lock (_leaseGate)
        {
            _outstandingLeases++;
            if (machineWideTaken)
                _machineWideHeld++;
        }

        return new Lease(this, specific, machineWideTaken);
    }

    /// <summary>
    /// Returns a lease's permits, unless shutdown already returned them on its behalf.
    /// </summary>
    private void ReleaseLease(SemaphoreSlim? specific, bool machineWideTaken)
    {
        lock (_leaseGate)
        {
            _outstandingLeases--;

            if (_disposed)
            {
                // Dispose gave up waiting for this lease and released its cross-process permit already.
                // Releasing again here would raise the machine-wide limit for every other server, and
                // the in-process semaphores are disposed, so there is nothing left to hand back.
                if (_outstandingLeases <= 0)
                    Monitor.PulseAll(_leaseGate);
                return;
            }

            if (machineWideTaken)
            {
                _machineWideHeld--;
                SafeRelease(_machineWideModelGate);
            }

            SafeRelease(_global);
            SafeRelease(specific);

            if (_outstandingLeases <= 0)
                Monitor.PulseAll(_leaseGate);
        }
    }

    private static void SafeRelease(SemaphoreSlim? semaphore)
    {
        try { semaphore?.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private static void SafeRelease(Semaphore? semaphore)
    {
        try { semaphore?.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Waits on the cross-process gate without blocking a thread pool thread for minutes.
    /// </summary>
    /// <remarks>
    /// Returns false when the gate could not be taken in time. A named semaphore has NO owner-abandonment
    /// semantics: a server that dies holding the permit strands it for every other server as long as any
    /// handle keeps the object alive. Giving up and serializing in-process risks two concurrent loads;
    /// waiting forever guarantees that model work on this machine never runs again.
    /// </remarks>
    private async Task<bool> WaitMachineWideAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _modelGateTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_machineWideModelGate!.WaitOne(TimeSpan.Zero))
                return true;

            if (DateTimeOffset.UtcNow >= deadline)
                return false;

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the shared gate, preferring the machine-wide namespace and falling back to a session-local
    /// one.
    /// </summary>
    /// <remarks>
    /// A named semaphore in <c>Global\</c> is creatable by an ordinary interactive user (unlike a section
    /// object, which needs SeCreateGlobalPrivilege), so this normally succeeds and the gate covers every
    /// session on the machine — including a second signed-in user. The <c>Local\</c> fallback exists for
    /// restricted environments that deny the global namespace; it still covers the case that matters
    /// most, several servers running for the same signed-in user.
    /// </remarks>
    private static Semaphore? TryCreateMachineWideGate(string gateName)
    {
        foreach (string name in new[] { $"Global\\{gateName}", $"Local\\{gateName}" })
        {
            try
            {
                return new Semaphore(1, 1, name, out _);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or PlatformNotSupportedException)
            {
                // Try the next, less privileged, scope.
            }
        }

        return null;
    }

    /// <summary>
    /// Drains in-flight leases, then releases the gates.
    /// </summary>
    /// <remarks>
    /// Shutdown cancels running jobs without awaiting them, so a call can still be unwinding here. A
    /// named semaphore has no owner-abandonment semantics: disposing this process's handle while a lease
    /// still holds the permit leaves the kernel object at zero for every OTHER Yagu MCP server on the
    /// machine, which then blocks for the full ten-minute gate timeout before it gives up. So wait for
    /// the leases, and hand the cross-process permit back explicitly for any that outlast the wait.
    /// </remarks>
    public void Dispose()
    {
        lock (_leaseGate)
        {
            if (_disposed)
                return;

            DateTimeOffset deadline = DateTimeOffset.UtcNow + LeaseDrainTimeout;
            while (_outstandingLeases > 0)
            {
                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_leaseGate, remaining))
                    break;
            }

            // Whatever is still outstanding will find _disposed set and skip its own release, so this is
            // the only hand-back those permits get.
            while (_machineWideHeld > 0)
            {
                SafeRelease(_machineWideModelGate);
                _machineWideHeld--;
            }

            _disposed = true;
        }

        _global.Dispose();
        _search.Dispose();
        _indexMutation.Dispose();
        _model.Dispose();
        _machineWideModelGate?.Dispose();
    }

    private sealed class Lease(BridgeConcurrency owner, SemaphoreSlim? specific, bool machineWideTaken) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Interlocked, not a bool: two threads disposing the same lease could both pass a plain check
            // and double-release, silently raising the concurrency limit for everyone.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            owner.ReleaseLease(specific, machineWideTaken);
        }
    }
}
