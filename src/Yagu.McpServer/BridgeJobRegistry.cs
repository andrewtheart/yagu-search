using System.Collections.Concurrent;
using System.Globalization;
using Yagu.Services.Cli.MachineOutput;

namespace Yagu.McpServer;

/// <summary>The lifecycle of a long-running bridged operation.</summary>
internal enum JobState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A long-running operation the client polls rather than blocks on.</summary>
internal sealed class BridgeJob
{
    private int _state = (int)JobState.Running;

    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>
    /// The job's state. Written by the worker thread and read by every status poll, so the access is
    /// volatile: on arm64 two plain stores may be reordered, which would let a poll observe
    /// <see cref="JobState.Completed"/> while <see cref="Result"/> is still null. Writers set the payload
    /// first and this last, making the release write publish it.
    /// </summary>
    public JobState State
    {
        get => (JobState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public string? Result { get; set; }
    public string? Failure { get; set; }
    public required CancellationTokenSource Cancellation { get; init; }

    public TimeSpan Elapsed => (FinishedUtc ?? DateTimeOffset.UtcNow) - StartedUtc;
}

/// <summary>
/// Tracks operations that are too slow to answer inside a single tool call.
/// </summary>
/// <remarks>
/// <para>
/// Two things need this. Index build/rebuild/compact/validate can run for minutes, and killing a
/// long-running maintenance request would strand recovery work or a busy writer whose outcome the
/// client could never observe. Natural-language search needs it for a different reason: measured
/// latency ranges from ~9 s to ~950 s for the same model, and no synchronous call survives that spread.
/// </para>
/// <para>
/// Jobs are held in memory only and die with the server, which is correct for a per-session stdio
/// server: a job whose owner has disconnected has nobody to report to.
/// </para>
/// </remarks>
internal sealed class BridgeJobRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, BridgeJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _admission = new();
    private readonly int _maxRetained;
    private readonly int _maxRunning;
    private readonly int _maxResultChars;

    public BridgeJobRegistry(int maxRetained = 64, int maxRunning = 8, int maxResultChars = 256 * 1024)
    {
        _maxRetained = Math.Max(4, maxRetained);
        _maxRunning = Math.Max(1, maxRunning);
        _maxResultChars = Math.Max(4096, maxResultChars);
    }

    /// <summary>How many jobs may be in flight at once.</summary>
    public int MaxRunningJobs => _maxRunning;

    /// <summary>
    /// Starts <paramref name="work"/> in the background and returns the job, or <c>null</c> when the
    /// in-flight limit is already reached.
    /// </summary>
    /// <remarks>
    /// Retention alone bounds nothing: pruning only removes FINISHED jobs, so a caller that keeps
    /// starting index builds or model-backed searches can queue unlimited work and hold unlimited
    /// results. Admission is checked and the slot claimed under one lock, so two concurrent callers
    /// cannot both pass a limit that only one of them fits into.
    /// </remarks>
    public BridgeJob? Start(string kind, string description, Func<CancellationToken, Task<string>> work)
    {
        var job = new BridgeJob
        {
            Id = $"{kind}-{Guid.NewGuid().ToString("n")[..8]}",
            Kind = kind,
            Description = description,
            Cancellation = new CancellationTokenSource(),
        };

        lock (_admission)
        {
            PruneFinished();

            if (_jobs.Values.Count(j => j.State == JobState.Running) >= _maxRunning)
            {
                job.Cancellation.Dispose();
                return null;
            }

            _jobs[job.Id] = job;
        }

        _ = Task.Run(async () =>
        {
            // In every branch the payload and finish time are written BEFORE State. State is the volatile
            // release write that publishes them, so a poller can never see a terminal state without them.
            try
            {
                string result = Bound(await work(job.Cancellation.Token).ConfigureAwait(false));
                job.Result = result;
                job.FinishedUtc = DateTimeOffset.UtcNow;
                job.State = JobState.Completed;
            }
            catch (OperationCanceledException)
            {
                job.Failure = "the job was cancelled.";
                job.FinishedUtc = DateTimeOffset.UtcNow;
                job.State = JobState.Cancelled;
            }
            catch (Exception ex)
            {
                job.Failure = ex.Message;
                job.FinishedUtc = DateTimeOffset.UtcNow;
                job.State = JobState.Failed;
            }
        }, CancellationToken.None);

        return job;
    }

    /// <summary>Caps one retained result, so finished jobs cannot pin an unbounded amount of memory.</summary>
    private string Bound(string result) =>
        string.IsNullOrEmpty(result) || result.Length <= _maxResultChars
            ? result
            : result[.._maxResultChars]
              + $"\n\n[truncated: the full result exceeded {_maxResultChars:N0} characters.]";

    public BridgeJob? Find(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public IReadOnlyList<BridgeJob> All() => [.. _jobs.Values.OrderByDescending(j => j.StartedUtc)];

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.State != JobState.Running)
            return false;

        try
        {
            job.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished and was pruned between the state check above and this call.
            return false;
        }

        return true;
    }

    /// <summary>Renders a job for a status response.</summary>
    public static string Describe(BridgeJob job)
    {
        string elapsed = job.Elapsed.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture);
        return job.State switch
        {
            JobState.Running => $"{job.Id}: running ({elapsed}s elapsed) — {job.Description}",
            JobState.Completed => $"{job.Id}: completed in {elapsed}s\n\n{job.Result}",
            JobState.Cancelled => $"{job.Id}: cancelled after {elapsed}s",
            _ => $"{job.Id}: failed after {elapsed}s — {job.Failure}",
        };
    }

    private void PruneFinished()
    {
        // Runs BEFORE the incoming job is added, so it has to make room for it. Returning at exactly
        // _maxRetained leaves the registry parked one job over its configured maximum forever.
        int excess = _jobs.Count - _maxRetained + 1;
        if (excess <= 0)
            return;

        foreach (BridgeJob finished in _jobs.Values
                     .Where(j => j.State != JobState.Running)
                     .OrderBy(j => j.FinishedUtc ?? DateTimeOffset.MaxValue)
                     .Take(excess))
        {
            if (_jobs.TryRemove(finished.Id, out var removed))
                removed.Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (BridgeJob job in _jobs.Values)
        {
            try
            {
                job.Cancellation.Cancel();
                job.Cancellation.Dispose();
            }
            catch (Exception)
            {
                // Best effort during shutdown.
            }
        }

        _jobs.Clear();
    }
}
