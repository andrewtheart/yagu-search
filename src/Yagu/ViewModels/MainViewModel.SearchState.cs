using CommunityToolkit.Mvvm.ComponentModel;

namespace Yagu.ViewModels;

/// <summary>
/// Live search lifecycle state — searching / preparing / cancelling flags, the derived
/// search-active and progress-indeterminate states, and the status/error/fallback text.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchModeSplitButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchActionButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(ProgressTooltip))]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    public partial bool IsSearching { get; set; }

    /// <summary>True from the instant a search is initiated from the UI until the file scan actually
    /// commits (<see cref="IsSearching"/> flips true) or the pre-search gate phase aborts. It lets the
    /// Search button morph to Cancel and the indeterminate progress bar appear immediately, instead of
    /// waiting out the multi-second pre-search gate work (e.g. the content-index journal-replay readiness
    /// check). Cleared in <see cref="ResetStateForNewSearch"/> when the scan commits, or by
    /// <see cref="EndSearchPreparation"/> when a gate aborts the run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    [NotifyPropertyChangedFor(nameof(SearchProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(SearchProgressRightLabel))]
    [NotifyPropertyChangedFor(nameof(SearchModeSplitButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchActionButtonVisibility))]
    public partial bool IsPreparingSearch { get; set; }

    /// <summary>True while a search is either being prepared (pre-scan gates) or actively scanning. Drives
    /// the progress-overlay visibility so the bar also shows during the gate phase.</summary>
    public bool IsSearchActive => IsSearching || IsPreparingSearch;

    /// <summary>True while the progress denominator is provisional: through the fast filename pass and
    /// full discovery. Set when a scan commits and latched false once the backend supplies a trustworthy
    /// total or discovery completes, so a provisional processed/seen-so-far ratio is never displayed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(SearchProgressRightLabel))]
    public partial bool SearchInNameFirstPhase { get; set; }

    /// <summary>The search progress bar is indeterminate while preparing or discovering files, then
    /// determinate once the final total is known.</summary>
    public bool SearchProgressIndeterminate => IsPreparingSearch || SearchInNameFirstPhase;

    /// <summary>True from the instant the user clicks Cancel until the in-flight file scan or semantic
    /// translation actually stops. Cancellation isn't instantaneous — a large search keeps draining
    /// buffered results for a moment after <see cref="CancelAsync"/> fires — so this drives the morphing
    /// Cancel button into a disabled "Canceling.." state, giving immediate feedback and preventing a
    /// second Cancel click while the first is still in progress. Reset automatically when the search or
    /// translation ends (see <see cref="OnIsSearchingChanged"/> / <see cref="OnIsTranslatingSemanticQueryChanged"/>).</summary>
    [ObservableProperty]
    public partial bool IsCancelling { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? FallbackReason { get; set; }
}
