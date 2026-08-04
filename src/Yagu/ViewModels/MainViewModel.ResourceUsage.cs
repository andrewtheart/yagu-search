using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services;
using Yagu.Services.Index;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Status-bar resource usage: the temp/index/RAM usage labels plus the background monitor that
/// samples process memory and measures content-index storage on a throttled interval.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial string SearchResultTempDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasChosenSearchResultTempDirectory { get; set; }

    // ── Status-bar resource indicators (measured off the UI thread; see RunResourceUsageMonitorAsync).
    // TempUsage* reports this process's evicted-result files, IndexUsage* reports all content-index storage,
    // and RamUsage* reports Yagu + its worker children. The index total is cached for one minute and is never
    // refreshed while a search is running. ──
    [ObservableProperty] public partial string TempUsageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TempUsageTooltip { get; set; } = string.Empty;
    [ObservableProperty] public partial string IndexUsageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string IndexUsageTooltip { get; set; } = string.Empty;
    [ObservableProperty] public partial string RamUsageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string RamUsageTooltip { get; set; } = string.Empty;

    // ── Status-bar resource-usage monitor ──
    // A slow (10 s) background loop that measures Yagu's disk-temp footprint, total content-index storage,
    // and RAM (plus its worker children), then publishes formatted labels to the status bar. All measurement
    // runs on the thread pool (the PeriodicTimer resumes off the UI thread). The potentially larger index
    // measurement is cached for one minute and never refreshed during a search. Only the final string
    // assignments are marshalled back to the UI thread.

    /// <summary>Starts the periodic resource-usage monitor (idempotent). Called once at construction.</summary>
    private void StartResourceUsageMonitor()
    {
        if (_disposed || _resourceMonitorCts is not null)
            return;
        var cts = new CancellationTokenSource();
        _resourceMonitorCts = cts;
        _ = RunResourceUsageMonitorAsync(cts);
    }

    private void StopResourceUsageMonitor()
    {
        var cts = Interlocked.Exchange(ref _resourceMonitorCts, null);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task RunResourceUsageMonitorAsync(CancellationTokenSource cts)
    {
        try
        {
            // Publish an immediate first sample so the indicators aren't blank for the first 10 s.
            await Task.Run(() => MeasureAndPublishResourceUsage(cts.Token), cts.Token).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (_disposed)
                    break;
                MeasureAndPublishResourceUsage(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            YaguLog.For("ViewModel").LogDebug(ex, "Resource-usage monitor stopped unexpectedly.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _resourceMonitorCts, null, cts);
            try { cts.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Measures (off the UI thread) disk temp, cached index storage, and RAM used by Yagu plus each worker
    /// child process, then marshals the formatted status-bar labels to the UI thread.
    /// </summary>
    private void MeasureAndPublishResourceUsage(CancellationToken cancellationToken)
    {
        try
        {
            // Temp footprint: sum this process's evicted-result temp files (metadata only).
            long tempBytes = ResourceUsageMonitor.SumProcessTempResultBytes(SearchResultTempDirectory, Environment.ProcessId);

            IndexStorageSizeMeasurement? indexMeasurement = MeasureIndexStorageUsage(cancellationToken, out string indexRoot);

            // RAM: this Yagu process + its own worker children (attributed by parent PID so a second Yagu
            // instance's workers are not double-counted).
            long totalRamBytes = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes();
            var breakdown = new List<(string Name, long Bytes)>();
            long selfBytes;
            try
            {
                using var self = Process.GetCurrentProcess();
                selfBytes = self.WorkingSet64;
            }
            catch { selfBytes = Environment.WorkingSet; }
            breakdown.Add(("Yagu", selfBytes));

            long usedBytes = selfBytes;
            int myPid = Environment.ProcessId;
            foreach (string workerName in OrphanedWorkerCleanup.WorkerProcessNames)
            {
                Process[] workers;
                try { workers = Process.GetProcessesByName(workerName); }
                catch { continue; }

                long workerBytes = 0;
                foreach (Process worker in workers)
                {
                    try
                    {
                        if (OrphanedWorkerCleanup.GetParentProcessId(worker.Id) == myPid)
                            workerBytes += worker.WorkingSet64;
                    }
                    catch { /* exited / access denied — skip */ }
                    finally { try { worker.Dispose(); } catch { } }
                }
                if (workerBytes > 0)
                {
                    breakdown.Add((workerName, workerBytes));
                    usedBytes += workerBytes;
                }
            }

            string tempText = ResourceUsageMonitor.FormatTempStatus(tempBytes);
            string tempTooltip = ResourceUsageMonitor.BuildTempTooltip(tempBytes, SearchResultTempDirectory);
            string? indexText = indexMeasurement is { } measuredIndex
                ? ResourceUsageMonitor.FormatIndexStatus(measuredIndex.Bytes)
                : null;
            string? indexTooltip = indexMeasurement is { } measuredTooltipIndex
                ? ResourceUsageMonitor.BuildIndexTooltip(measuredTooltipIndex, indexRoot)
                : null;
            string ramText = ResourceUsageMonitor.FormatRamStatus(usedBytes, totalRamBytes);
            string ramTooltip = ResourceUsageMonitor.BuildRamTooltip(breakdown, usedBytes, totalRamBytes);

            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed)
                    return;
                TempUsageText = tempText;
                TempUsageTooltip = tempTooltip;
                if (indexText is not null && indexTooltip is not null)
                {
                    IndexUsageText = indexText;
                    IndexUsageTooltip = indexTooltip;
                }
                RamUsageText = ramText;
                RamUsageTooltip = ramTooltip;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            YaguLog.For("ViewModel").LogDebug(ex, "Resource-usage sample failed.");
        }
    }

    private IndexStorageSizeMeasurement? MeasureIndexStorageUsage(
        CancellationToken cancellationToken,
        out string indexRoot)
    {
        indexRoot = DefaultContentIndexPathProvider.Create(_settings.IndexStorageDirectory).IndexRoot;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var backend = (FileListerBackend)FileListerBackendIndex;
        bool sameRoot = string.Equals(_cachedIndexStorageRoot, indexRoot, StringComparison.OrdinalIgnoreCase);
        bool sameBackend = _cachedIndexStorageBackend == backend;

        // The 10-second resource loop may continue during a search, but an index-size refresh never competes
        // with file discovery or scanning. Reuse the last value until the search ends; otherwise refresh no
        // more than once per minute. A storage-location or backend change invalidates the cache immediately.
        if (_hasCachedIndexStorageSize && sameRoot && sameBackend && now < _nextIndexStorageSizeRefreshUtc)
        {
            return _cachedIndexStorageSize;
        }

        // Never start a storage walk while a search is active. If there is no current-root cached value yet,
        // leave the existing label unchanged and retry on the next monitor tick after the search.
        if (IsSearching)
            return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;

        cancellationToken.ThrowIfCancellationRequested();
        using var measurementCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? priorCts = Interlocked.CompareExchange(
            ref _indexStorageMeasurementCts,
            measurementCts,
            comparand: null);
        if (priorCts is not null)
            return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;

        try
        {
            // Close the check/register race: a search that began just before registration could not cancel
            // this CTS, while any search beginning after registration will cancel it from the change hook.
            if (IsSearching)
            {
                measurementCts.Cancel();
                return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;
            }

            IndexStorageSizeMeasurement measurement = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                indexRoot,
                backend,
                measurementCts.Token);
            measurementCts.Token.ThrowIfCancellationRequested();
            _cachedIndexStorageSize = measurement;
            _cachedIndexStorageRoot = indexRoot;
            _cachedIndexStorageBackend = backend;
            _nextIndexStorageSizeRefreshUtc = now + IndexStorageSizeRefreshInterval;
            _hasCachedIndexStorageSize = true;
            return measurement;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && measurementCts.IsCancellationRequested)
        {
            return sameRoot && sameBackend && _hasCachedIndexStorageSize ? _cachedIndexStorageSize : null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _indexStorageMeasurementCts, null, measurementCts);
        }
    }

    private void CancelIndexStorageMeasurement()
    {
        try { Volatile.Read(ref _indexStorageMeasurementCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
