using System.Diagnostics;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>Coordinates bounded cancellation and cleanup before the application exits.</summary>
public sealed partial class MainViewModel
{
    private bool _shutdownRequested;
    private bool _preserveLiveResourcesForProcessExit;

    /// <summary>True once application shutdown has started. New search, index, and warm-up work must not start.</summary>
    public bool IsShutdownRequested => _shutdownRequested;

    /// <summary>
    /// Cancels active search and index work, waits up to <paramref name="gracePeriod"/> for cooperative
    /// cleanup, then closes and deletes the disk-backed result store. Returns true when all tracked work
    /// stopped within the grace period; callers may still exit after a false result because index writes
    /// are atomic and worker job objects provide the final process-lifetime backstop.
    /// </summary>
    public async Task<bool> PrepareForShutdownAsync(TimeSpan gracePeriod)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await PrepareForShutdownAsync(gracePeriod).ConfigureAwait(true)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }))
            {
                return false;
            }
            return await completion.Task.ConfigureAwait(false);
        }

        if (_shutdownRequested)
            return !IsSearchActive && !IsTranslatingSemanticQuery
                && !IsIndexBuildActive && !IsIndexRebuildBlocking && !IsIndexWarmActive;

        _shutdownRequested = true;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan normalizedGrace = gracePeriod < TimeSpan.Zero ? TimeSpan.Zero : gracePeriod;

        CancelSearchPreparation();
        try { _semanticCts?.Cancel(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _metadataCts.Cancel(); } catch { }
        try { _indexStorageMeasurementCts?.Cancel(); } catch { }

        // A confirmed application exit is cancellation, not the user-visible Pause command: no work
        // should be re-kicked in this session, and the next launch starts with the normal persisted state.
        ResumeAutoIndexBuildAsync = null;
        RequestIdleIndexMaintenanceAsync = null;
        _pausedIndexBuildFolder = null;
        _resumeIndexWarmFolder = null;
        try { _indexBuildCancellation?.Cancel(); } catch { }
        try { _indexRebuildCancellation?.Cancel(); } catch { }
        Task? indexWarmTask = _indexWarmTask;
        try { _indexWarmCancellation?.Cancel(); } catch { }
        ++_indexWarmGeneration;

        bool searchStopped = await WaitForSearchCleanupAsync(Remaining(normalizedGrace, stopwatch)).ConfigureAwait(true);
        bool indexStopped = await WaitForIndexCleanupAsync(Remaining(normalizedGrace, stopwatch)).ConfigureAwait(true);
        bool warmStopped = await WaitForTaskAsync(indexWarmTask, Remaining(normalizedGrace, stopwatch)).ConfigureAwait(true);

        _activeIndexWarmFolder = null;
        IsIndexWarmActive = false;
        IsIndexWarmPausedForSearch = false;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));

        bool resultStoreStopped = true;
        if (searchStopped && _resultStore is { } oldStore)
        {
            _resultStore = null;
            resultStoreStopped = await WaitForTaskAsync(
                Task.Run(oldStore.Dispose),
                Remaining(normalizedGrace, stopwatch)).ConfigureAwait(true);
        }

        bool graceful = searchStopped && indexStopped && warmStopped && resultStoreStopped;
        _preserveLiveResourcesForProcessExit = !graceful;
        return graceful;
    }

    private async Task<bool> WaitForSearchCleanupAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await _searchLifecycleGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
            _searchLifecycleGate.Release();

            // Preparation and semantic translation run before the lifecycle semaphore is acquired.
            // Keep waiting for those flags after proving the scan pipeline's finally block completed.
            while (IsSearchActive || IsTranslatingSemanticQuery)
                await Task.Delay(50, cancellation.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> WaitForIndexCleanupAsync(TimeSpan timeout)
    {
        if (!IsIndexBuildActive && !IsIndexRebuildBlocking)
            return true;

        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(50).ConfigureAwait(true);
            if (!IsIndexBuildActive && !IsIndexRebuildBlocking)
                return true;
        }
        return !IsIndexBuildActive && !IsIndexRebuildBlocking;
    }

    private static async Task<bool> WaitForTaskAsync(Task? task, TimeSpan timeout)
    {
        if (task is null || task.IsCompleted)
        {
            if (task is not null)
            {
                try { await task.ConfigureAwait(true); } catch { }
            }
            return true;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(true);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static TimeSpan Remaining(TimeSpan gracePeriod, Stopwatch stopwatch)
    {
        TimeSpan remaining = gracePeriod - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}