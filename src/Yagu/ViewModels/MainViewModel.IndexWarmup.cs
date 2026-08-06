using Yagu.Services;
using Yagu.Services.Index;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Content-index warm-up: pre-opening the index for the current directory so the first search is
/// fast, pausing it while a search runs and resuming afterwards, plus its status messages.
/// </summary>
public sealed partial class MainViewModel
{
    private Task? _indexWarmTask;

    /// <summary>
    /// Starts loading the current root's immutable query index immediately. A cold open runs off the UI
    /// thread and is cooperatively cancellable; there is deliberately no fixed wait before it starts.
    /// If a search is already running, the root is queued and warming begins when that search finishes.
    /// </summary>
    public void StartContentIndexWarmup(string? folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => StartContentIndexWarmup(folder));
            return;
        }
        if (_disposed || _shutdownRequested || !_settings.EnableContentIndex || !UseContentIndex || string.IsNullOrWhiteSpace(folder))
            return;

        // Stage-6 (plan §5.8): the worker PRUNING path needs no in-process warm. The worker memory-maps the
        // scope's format-v3 lazily and its open at barrier B0 is cheap (no ~8× in-process deserialize, no GC
        // storm), so a worker-served scope accelerates directly on the FIRST search. Warming here would
        // deserialize the whole index into the host — the exact footprint the worker path removes — so skip it
        // entirely when the flag is on.
        if (_settings.IndexUseWorkerQuerySessions)
            return;

        string requestedRoot = folder.Trim();
        string root = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, requestedRoot)
            ?? requestedRoot;
        if (IsSearching)
        {
            _resumeIndexWarmFolder = root;
            OnPropertyChanged(nameof(ActiveIndexWarmFolder));
            return;
        }
        if (IsIndexWarmActive
            && string.Equals(_activeIndexWarmFolder, root, StringComparison.OrdinalIgnoreCase))
            return;

        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount);
        string storageDir = _settings.IndexStorageDirectory;
        int maxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(_settings.IndexMaxInProcessSizeMB);
        var pathProvider = DefaultContentIndexPathProvider.Create(storageDir);
        try
        {
            var manager = new ContentIndexManager(pathProvider, retained);
            if (!manager.HasCurrentIndex(root))
                return;
            if (ContentIndexSearchGate.IsScopeWarm(pathProvider, root, retained))
            {
                ShowIndexWarmReadyStatus(root);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Could not prepare startup index warm-up for {Root}; searches will live-scan.", root);
            return;
        }

        CancellationTokenSource? previous = _indexWarmCancellation;
        _indexWarmCancellation = null;
        try { previous?.Cancel(); } catch { }

        int generation = ++_indexWarmGeneration;
        var cancellation = new CancellationTokenSource();
        _indexWarmCancellation = cancellation;
        _activeIndexWarmFolder = root;
        _resumeIndexWarmFolder = null;
        IsIndexWarmPausedForSearch = false;
        IsIndexWarmActive = true;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));
        ShowIndexWarmPreparingStatus(root);
        YaguLog.For("ContentIndex").LogInformation(
            "Index warm-up starting immediately for {Root} (no startup delay).", root);

        _indexWarmTask = RunContentIndexWarmupAsync(
            generation,
            root,
            pathProvider,
            retained,
            maxInProcessSizeMB,
            cancellation);
    }

    private async Task RunContentIndexWarmupAsync(
        int generation,
        string root,
        IContentIndexPathProvider pathProvider,
        int retained,
        int maxInProcessSizeMB,
        CancellationTokenSource cancellation)
    {
        bool ready = false;
        bool loadable = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            (ready, loadable) = await Task.Run(() =>
            {
                CancellationToken token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                if (!ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(
                        pathProvider,
                        root,
                        retained,
                        maxInProcessSizeMB))
                    return (false, false);

                string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                var store = new ContentIndexStore(pathProvider, scopeId, Math.Max(1, retained));
                if (store.IsCurrentLayeredIndexCached())
                    return (true, true);
                return (store.TryOpenLayered(retainDocuments: false, cancellationToken: token) is not null, true);
            }, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Index warm-up paused/cancelled for {Root} after {ElapsedSeconds:0.0}s.",
                root,
                stopwatch.Elapsed.TotalSeconds);
            return;
        }
        catch (OutOfMemoryException ex)
        {
            loadable = false;
            YaguLog.For("ContentIndex").LogCritical(ex,
                "Index warm-up ran out of memory for {Root}; searches will live-scan.", root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index warm-up failed for {Root}; searches will continue with live scanning.", root);
        }
        finally
        {
            cancellation.Dispose();
        }

        if (generation != _indexWarmGeneration || _disposed)
            return;

        _indexWarmCancellation = null;
        _activeIndexWarmFolder = null;
        IsIndexWarmActive = false;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));

        if (ready)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Index warm-up completed for {Root} in {ElapsedSeconds:0.0}s.",
                root,
                stopwatch.Elapsed.TotalSeconds);
            ShowIndexWarmReadyStatus(root);
        }
        else
        {
            if (!loadable)
                YaguLog.For("ContentIndex").LogInformation(
                    "Index warm-up skipped for {Root}: the index is outside the configured in-process size policy.",
                    root);
            _ = RefreshIndexStatusAsync([root], UseContentIndex && _settings.EnableContentIndex);
        }
    }

    /// <summary>Cancels an active warm before a search and remembers its root for automatic restart when
    /// the search ends. Returns false when no warm was active.</summary>
    public bool PauseContentIndexWarmupForSearch()
    {
        if (!_dispatcher.HasThreadAccess)
            return false;
        if (!IsIndexWarmActive || string.IsNullOrWhiteSpace(_activeIndexWarmFolder))
            return false;

        _resumeIndexWarmFolder = _activeIndexWarmFolder;
        _activeIndexWarmFolder = null;
        ++_indexWarmGeneration; // makes the cancelled run's completion stale
        CancellationTokenSource? cancellation = _indexWarmCancellation;
        _indexWarmCancellation = null;
        try { cancellation?.Cancel(); } catch { }
        IsIndexWarmActive = false;
        IsIndexWarmPausedForSearch = true;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));
        ShowIndexWarmPausedStatus();
        return true;
    }

    /// <summary>Restarts a warm that was paused (or queued) for a search. Safe to call after every search;
    /// it is a no-op when no root is waiting.</summary>
    public void ResumeContentIndexWarmupAfterSearch()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(ResumeContentIndexWarmupAfterSearch);
            return;
        }
        if (_shutdownRequested || IsSearching || string.IsNullOrWhiteSpace(_resumeIndexWarmFolder))
            return;

        string root = _resumeIndexWarmFolder;
        _resumeIndexWarmFolder = null;
        IsIndexWarmPausedForSearch = false;
        OnPropertyChanged(nameof(ActiveIndexWarmFolder));
        StartContentIndexWarmup(root);
    }

    private void ShowIndexWarmPreparingStatus(string root)
    {
        if (!_settings.ShowIndexStatusInMainWindow || IsIndexBuildActive)
            return;
        IndexStatusGlyph = "\uE895";
        IndexStatusText = "Indexing: preparing...";
        IndexStatusTooltip = $"Loading the content index for {root} into the query cache. "
            + "A search can start now, but it will pause this warm-up and run without index acceleration."
            + BuildIndexDateDetails();
        ShowIndexBuildPercent = false;
        ShowIndexStatus = true;
    }

    private void ShowIndexWarmPausedStatus()
    {
        if (!_settings.ShowIndexStatusInMainWindow || IsIndexBuildActive)
            return;
        IndexStatusGlyph = "\uE769";
        IndexStatusText = "Indexing: warm-up paused";
        IndexStatusTooltip = "Index warm-up is paused while the search runs. It resumes automatically when the search finishes."
            + BuildIndexDateDetails();
        ShowIndexBuildPercent = false;
        ShowIndexStatus = true;
    }

    private void ShowIndexWarmReadyStatus(string root)
    {
        if (!_settings.ShowIndexStatusInMainWindow || IsIndexBuildActive)
            return;
        IndexStatusGlyph = ContentIndexUiStatus.AvailabilityGlyph(IndexAvailability.Available);
        IndexStatusText = ContentIndexUiStatus.ReadyLabel;
        IndexStatusTooltip = $"The content index for {root} is warmed and ready for accelerated searches."
            + BuildIndexDateDetails();
        ShowIndexBuildPercent = false;
        ShowIndexStatus = true;
        ApplyAllDriveIndexHealthStatus(force: true);
    }
}
