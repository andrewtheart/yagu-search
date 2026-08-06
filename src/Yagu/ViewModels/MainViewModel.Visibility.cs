namespace Yagu.ViewModels;

/// <summary>
/// Visibility bindings for the status bar and results chrome, and the property-changed hooks that
/// raise them. Kept together so a new toggle and the notification that reveals it stay in one place.
/// </summary>
public sealed partial class MainViewModel
{
    public Microsoft.UI.Xaml.Visibility MemoryPressureWarningVisibility =>
        ShowMemoryPressureWarningLabel && !string.IsNullOrWhiteSpace(DegradedNoticeText)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    // The "Filter files…" box only makes sense once a search has produced files. It keys off the
    // UNFILTERED result set (AllGroups), NOT HasResults (which reflects the filtered/visible groups) —
    // otherwise typing a filter that matches nothing would empty the visible groups and hide the very
    // box the user is typing in, trapping them. Its change notification piggybacks on HasResults via
    // the OnPropertyChanged override below (HasResults is raised at every point AllGroups can cross
    // empty/non-empty: first result streamed in, search completion, clear; and harmlessly on filter,
    // where AllGroups is unchanged so the box stays visible).
    public Microsoft.UI.Xaml.Visibility ResultFileFilterVisibility =>
        _resultCollection.AllGroups.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility SkippedInfoVisibility =>
        FilesSkipped > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ResultFilterAreaVisibility =>
        _resultCollection.AllGroups.Count > 0 || FilesSkipped > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static readonly System.ComponentModel.PropertyChangedEventArgs s_resultFileFilterVisibilityChangedArgs =
        new(nameof(ResultFileFilterVisibility));
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_resultFilterAreaVisibilityChangedArgs =
        new(nameof(ResultFilterAreaVisibility));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HasResults))
        {
            base.OnPropertyChanged(s_resultFileFilterVisibilityChangedArgs);
            base.OnPropertyChanged(s_resultFilterAreaVisibilityChangedArgs);
        }
    }

    public Microsoft.UI.Xaml.Visibility StatsForNerdsVisibility =>
        ShowStatsForNerds
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ResourceUsageStatusVisibility =>
        ShowResourceUsageInStatusBar
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility IndexStatusVisibility =>
        ShowIndexStatus
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility AllDriveIndexStatusVisibility =>
        string.IsNullOrWhiteSpace(AllDriveIndexStatusText)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility IndexBuildPercentVisibility =>
        ShowIndexBuildPercent
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility AutoScrollResultsCheckboxVisibility =>
        ShowAutoScrollResultsCheckbox
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnFallbackReasonChanged(string? value) => OnPropertyChanged(nameof(HasFallbackReason));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorText));
    partial void OnDegradedNoticeTextChanged(string value) => OnPropertyChanged(nameof(MemoryPressureWarningVisibility));
    partial void OnShowMemoryPressureWarningLabelChanged(bool value) => OnPropertyChanged(nameof(MemoryPressureWarningVisibility));
    partial void OnShowStatsForNerdsChanged(bool value) => OnPropertyChanged(nameof(StatsForNerdsVisibility));
    partial void OnShowResourceUsageInStatusBarChanged(bool value) => OnPropertyChanged(nameof(ResourceUsageStatusVisibility));
    partial void OnShowAutoScrollResultsCheckboxChanged(bool value) => OnPropertyChanged(nameof(AutoScrollResultsCheckboxVisibility));
    partial void OnShowIndexStatusChanged(bool value) => OnPropertyChanged(nameof(IndexStatusVisibility));

    partial void OnShowIndexBuildPercentChanged(bool value) => OnPropertyChanged(nameof(IndexBuildPercentVisibility));
    partial void OnFilesSkippedChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); OnPropertyChanged(nameof(ProgressTooltip)); OnPropertyChanged(nameof(SkipBreakdownDetails)); OnPropertyChanged(nameof(SkipTotalCount)); OnPropertyChanged(nameof(SkipTooltip)); OnPropertyChanged(nameof(SkippedInfoVisibility)); OnPropertyChanged(nameof(ResultFilterAreaVisibility)); }
    partial void OnAccessDeniedCountChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); }
}
