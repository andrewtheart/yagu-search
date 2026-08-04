using Microsoft.UI.Dispatching;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Status-line text while a search runs: the progress/memory-pressure status builders, the search
/// timer, and the heartbeat that keeps the elapsed time ticking during long scans.
/// </summary>
public sealed partial class MainViewModel
{
    private string BuildProgressStatus(SearchProgress progress)
    {
        var prefix = Degraded ? "Searching (memory-saving mode)" : "Searching";
        string? phase = progress.SourceBacked?.BuildPhaseLabel(progress.FilesScanned, progress.TotalFiles);
        if (progress.TotalFiles > 0)
        {
            return phase is null
                ? $"{prefix} {progress.FilesScanned:N0}/{progress.TotalFiles:N0} files... {progress.MatchesFound:N0} matches"
                : $"{prefix} {phase}... {progress.MatchesFound:N0} matches";
        }

        return $"{prefix}... {progress.FilesScanned:N0} files scanned, {progress.MatchesFound:N0} matches";
    }

    private string BuildCurrentSearchStatus()
    {
        var prefix = Degraded ? "Searching (memory-saving mode)" : "Searching";
        if (TotalFiles > 0)
        {
            return $"{prefix} {FilesScanned:N0}/{TotalFiles:N0} files... {MatchesFound:N0} matches";
        }

        return $"{prefix}... {FilesScanned:N0} files scanned, {MatchesFound:N0} matches";
    }

    private static string BuildMemoryPressureStatus(SearchEvent.MemoryPressure memoryPressure)
    {
        return "Memory pressure high; paging Yagu results to disk and continuing in memory-saving mode...";
    }

    private TimeSpan StopSearchTimer()
    {
        var timer = _searchTimer;
        if (timer is null)
            return _lastSearchElapsed;

        timer.Stop();
        _searchTimer = null;
        StopSearchStatusHeartbeat();
        _lastSearchElapsed = timer.Elapsed;
        return timer.Elapsed;
    }

    private void StartSearchStatusHeartbeat()
    {
        StopSearchStatusHeartbeat();
        var cts = new CancellationTokenSource();
        _searchStatusHeartbeatCts = cts;
        _ = RunSearchStatusHeartbeatAsync(cts);
    }

    private void StopSearchStatusHeartbeat()
    {
        var cts = Interlocked.Exchange(ref _searchStatusHeartbeatCts, null);
        try { cts?.Cancel(); } catch { }
    }

    private async Task RunSearchStatusHeartbeatAsync(CancellationTokenSource cts)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.High, UpdateSearchStatusHeartbeat))
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.CompareExchange(ref _searchStatusHeartbeatCts, null, cts);
            cts.Dispose();
        }
    }

    private void UpdateSearchStatusHeartbeat()
    {
        if (_disposed || _searchTimer is null || !IsSearching)
        {
            StopSearchStatusHeartbeat();
            return;
        }

        UpdateFilesPerSecond();
    }

    private string BuildCancelledStatus(TimeSpan elapsed)
    {
        var time = FormatElapsed(elapsed);
        var rate = FormatThroughput(FilesScanned, _bytesScanned, elapsed);
        return $"Cancelled — {MatchesFound:N0} matches, {FilesScanned:N0} files processed ({time}, {rate})";
    }
}
