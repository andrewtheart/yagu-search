using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Models;

namespace Yagu.ViewModels;

/// <summary>
/// Search and session progress: scanned/total counters, the percentage labels, the phase label
/// shown while the file list is still being enumerated, and the detailed progress tooltip.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int FilesScanned { get; set; }
    [ObservableProperty] public partial int TotalFiles { get; set; }

    // .yagu-session save/load progress (0.0..1.0 while busy).
    [ObservableProperty] public partial bool IsSessionBusy { get; set; }
    [ObservableProperty] public partial double SessionProgressPercent { get; set; }
    [ObservableProperty] public partial string SessionProgressText { get; set; } = string.Empty;

    public bool IsSessionIdle => !IsSessionBusy;
    partial void OnIsSessionBusyChanged(bool value) => OnPropertyChanged(nameof(IsSessionIdle));

    // Whole-number percent label for the full-window session busy overlay (e.g. "42%").
    public string SessionProgressPercentLabel => $"{SessionProgressPercent:F0}%";
    partial void OnSessionProgressPercentChanged(double value) => OnPropertyChanged(nameof(SessionProgressPercentLabel));

    public string ProgressTooltip
    {
        get
        {
            if (TotalFiles > 0)
            {
                // FilesScanned can momentarily exceed a slightly stale TotalFiles between 100 ms
                // snapshots; clamp so the tooltip never reads over 100%.
                double pct = Math.Min(100.0, (double)FilesScanned / TotalFiles * 100);
                string baseText = $"{pct:F1}% complete ({FilesScanned:N0} files out of {TotalFiles:N0} total files)";
                string? phase = _sourceBackedSearchProgress?.BuildPhaseLabel(FilesScanned, TotalFiles);
                return phase is null ? baseText : baseText + Environment.NewLine + phase;
            }
            // Total not yet known. A recursive enumeration of a large tree, or a search whose filters
            // exclude every file during discovery, can churn for minutes before a total is available —
            // show an active "discovering" state (with the running processed count when present) so a
            // long discovery never looks frozen on a static "Waiting for file list…".
            if (IsSearching)
            {
                int processed = Math.Max(FilesScanned, FilesSkipped);
                return processed > 0
                    ? $"Discovering files… ({processed:N0} found so far)"
                    : "Discovering files…";
            }
            return "Waiting for file list…";
        }
    }

    private SourceBackedSearchProgress? _sourceBackedSearchProgress;
    private readonly SearchProgressDisplayTracker _searchProgressDisplayTracker = new();

    public double DisplayedSearchProgressPercent => _searchProgressDisplayTracker.Percent;

    /// <summary>Whole-number completion label shown at the far-right edge of the search progress bar.
    /// Empty while discovery has not produced a total. The displayed percentage is monotonic even when
    /// discovery revises its total upward during a scan.</summary>
    public string SearchProgressPercentLabel => TotalFiles > 0
        ? $"{DisplayedSearchProgressPercent:F0}%"
        : string.Empty;

    private string _searchProgressPhaseLabel = string.Empty;

    /// <summary>Right-edge progress text: the normal rounded percent during discovery/native scanning,
    /// then an explicit OCR/PDF counter while slow extraction workers drain their remaining queue.</summary>
    public string SearchProgressRightLabel => SearchProgressIndeterminate
        ? DiscoveryProgressLabel
        : string.IsNullOrEmpty(_searchProgressPhaseLabel)
            ? SearchProgressPercentLabel
            : $"{DisplayedSearchProgressPercent:F0}% [{_searchProgressPhaseLabel}]";

    /// <summary>Shown while the total is still unknown. A backend without an upfront count (managed
    /// enumeration) can discover for minutes, so the live processed count keeps an indeterminate bar
    /// informative instead of blank.</summary>
    private string DiscoveryProgressLabel =>
        FilesScanned > 0 ? $"{FilesScanned:N0} files" : "Discovering\u2026";

    partial void OnFilesScannedChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressTooltip));
        OnPropertyChanged(nameof(SearchProgressPercentLabel));
        OnPropertyChanged(nameof(SearchProgressRightLabel));
    }

    partial void OnTotalFilesChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressTooltip));
        OnPropertyChanged(nameof(SearchProgressPercentLabel));
        OnPropertyChanged(nameof(SearchProgressRightLabel));
    }

    private void UpdateSearchProgressPhaseLabel(SearchProgress progress)
    {
        _sourceBackedSearchProgress = progress.SourceBacked;
        string next = progress.SourceBacked?.BuildPhaseLabel(progress.FilesScanned, progress.TotalFiles)
            ?? string.Empty;
        if (string.Equals(next, _searchProgressPhaseLabel, StringComparison.Ordinal))
            return;
        _searchProgressPhaseLabel = next;
        OnPropertyChanged(nameof(SearchProgressRightLabel));
        OnPropertyChanged(nameof(ProgressTooltip));
    }

    private void UpdateDisplayedSearchProgress(int filesProcessed, int totalFiles, bool indeterminate)
    {
        if (!_searchProgressDisplayTracker.Update(filesProcessed, totalFiles, indeterminate))
            return;

        OnPropertyChanged(nameof(DisplayedSearchProgressPercent));
        OnPropertyChanged(nameof(SearchProgressPercentLabel));
        OnPropertyChanged(nameof(SearchProgressRightLabel));
    }

    private void ResetDisplayedSearchProgress()
    {
        if (!_searchProgressDisplayTracker.Reset())
            return;

        OnPropertyChanged(nameof(DisplayedSearchProgressPercent));
        OnPropertyChanged(nameof(SearchProgressPercentLabel));
        OnPropertyChanged(nameof(SearchProgressRightLabel));
    }
}
