using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Search-scope filters: context lines, gitignore handling, include/exclude globs and their
/// pattern modes, size and date ranges, the sort/group/date dropdown indexes, and the derived
/// labels and breadcrumbs the results header shows for the active filters.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int ContextLines { get; set; } = 3;
    [ObservableProperty] public partial int PreviewContextLines { get; set; } = 20;
    [ObservableProperty] public partial bool ObeyGitignore { get; set; }
    [ObservableProperty] public partial bool GitignoreTakesPrecedence { get; set; } = true;
    // null = unset (ask via dialog), true = .gitignore wins, false = Include filter wins.
    [ObservableProperty] public partial bool? GitignorePrecedencePreference { get; set; }
    [ObservableProperty] public partial string IncludeGlobs { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExcludeGlobs { get; set; } = string.Empty;
    [ObservableProperty] public partial int IncludeFilterModeIndex { get; set; }
    [ObservableProperty] public partial int ExcludeFilterModeIndex { get; set; }
    [ObservableProperty] public partial long MinFileSizeBytes { get; set; }
    [ObservableProperty] public partial long MaxFileSizeBytes { get; set; }
    [ObservableProperty] public partial long DefaultMinFileSizeBytes { get; set; }
    [ObservableProperty] public partial long DefaultMaxFileSizeBytes { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CreatedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CreatedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ModifiedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ModifiedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultCreatedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultCreatedBeforeDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultModifiedAfterDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? DefaultModifiedBeforeDate { get; set; }
    [ObservableProperty] public partial int MaxResults { get; set; }
    [ObservableProperty] public partial string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    [ObservableProperty] public partial string FileNameFilter { get; set; } = string.Empty;
    [ObservableProperty] public partial int SearchModeIndex { get; set; }
    [ObservableProperty] public partial int SortModeIndex { get; set; }
    [ObservableProperty] public partial int SortDirectionIndex { get; set; }
    [ObservableProperty] public partial int GroupModeIndex { get; set; }
    [ObservableProperty] public partial int GroupSortDirectionIndex { get; set; }
    [ObservableProperty] public partial int DateRangeFilterIndex { get; set; }

    public GroupMode GroupMode => (GroupMode)GroupModeIndex;
    public FilterPatternMode IncludeFilterMode => IncludeFilterModeIndex == 1 ? FilterPatternMode.Regex : FilterPatternMode.GlobPath;
    public FilterPatternMode ExcludeFilterMode => ExcludeFilterModeIndex == 1 ? FilterPatternMode.Regex : FilterPatternMode.GlobPath;
    public string IncludeFilterPlaceholder => IncludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. \.(cs|xaml)$…"
        : "e.g. ts,js,py or *.cs…";
    public string ExcludeFilterPlaceholder => ExcludeFilterMode == FilterPatternMode.Regex
        ? @"e.g. (^|/)node_modules/|\.min\.js$…"
        : $"e.g. {AppSettings.DefaultExcludeGlobs}…";

    // The exclude box shows greyed placeholder example text (e.g. "node_modules;bin;obj;.git")
    // when empty, but that text is ONLY an example — it is NOT applied. An empty box means
    // "no excludes": folders are excluded only when the user explicitly types them, matching the
    // include box. (Previously an empty box silently applied the example list as real excludes,
    // which hid files living in folders like bin/ that the user never chose to exclude.)
    private string EffectiveExcludeGlobsText => ExcludeGlobs ?? string.Empty;
    public string GroupModeLabel => GroupMode switch
    {
        GroupMode.None => "None",
        GroupMode.Folder => "Folder",
        GroupMode.DateRangeModified => "Date range (Modified)",
        GroupMode.DateRangeCreated => "Date range (Created)",
        GroupMode.DateRangeModifiedCreated => "Date range (Modified + Created)",
        GroupMode.Extension => "Extension",
        GroupMode.FileSize => "File size",
        _ => "None",
    };
    public string GroupSortDirectionLabel => GroupMode switch
    {
        GroupMode.FileSize => GroupSortDirectionIndex == 0 ? "Small to large" : "Large to small",
        GroupMode.DateRangeModified or GroupMode.DateRangeCreated or GroupMode.DateRangeModifiedCreated =>
            GroupSortDirectionIndex == 0 ? "Recent first" : "Older first",
        _ => GroupSortDirectionIndex == 0 ? "A-Z" : "Z-A",
    };
    public DateRangeFilter DateRangeFilter => (DateRangeFilter)DateRangeFilterIndex;
    public string DateRangeFilterLabel => DateRangeFilter switch
    {
        DateRangeFilter.None => "Any date",
        DateRangeFilter.PastDay => "Last day",
        DateRangeFilter.PastWeek => "Last week",
        DateRangeFilter.PastTwoWeeks => "Last 2 weeks",
        DateRangeFilter.PastMonth => "Last month",
        DateRangeFilter.PastThreeMonths => "Last 3 months",
        DateRangeFilter.PastSixMonths => "Last 6 months",
        DateRangeFilter.PastNineMonths => "Last 9 months",
        DateRangeFilter.PastYear => "Last year",
        DateRangeFilter.PastTwoYears => "Last 2 years",
        DateRangeFilter.PastThreeYears => "Last 3 years",
        DateRangeFilter.PastFiveYears => "Last 5 years",
        _ => "Any date",
    };
    public bool HasExtensionFilter => _selectedExtensionFilters.Count > 0;
    public string ExtensionFilterLabel => _selectedExtensionFilters.Count switch
    {
        0 => "All extensions",
        1 => SearchResultCollection.FormatExtensionDisplayName(_selectedExtensionFilters.First()),
        _ => $"{_selectedExtensionFilters.Count:N0} extensions",
    };

    // ── Group / Filter menu breadcrumbs ──
    // A short "you are here" path shown at the top of the Group and Filter menus when a selection is
    // active, e.g. "Folder \u203A A-Z" or "By date \u203A Last week", so the current choice is visible
    // without opening the submenus. Built on demand by the menu Opening handlers (no live binding needed).
    public bool HasGroupBreadcrumb => GroupMode != GroupMode.None;
    public string GroupBreadcrumb => HasGroupBreadcrumb
        ? $"{GroupModeLabel}  \u203A  {GroupSortDirectionLabel}"
        : string.Empty;

    public bool HasFilterBreadcrumb => DateRangeFilter != DateRangeFilter.None || HasExtensionFilter;
    public string FilterBreadcrumb
    {
        get
        {
            var parts = new List<string>(2);
            if (DateRangeFilter != DateRangeFilter.None)
                parts.Add($"By date  \u203A  {DateRangeFilterLabel}");
            if (HasExtensionFilter)
                parts.Add($"By extension  \u203A  {ExtensionFilterLabel}");
            return string.Join("      ", parts);
        }
    }

    // When the user picks a concrete precedence preference (dialog or Settings),
    // keep the effective runtime value in sync so the next search honors it immediately.
    partial void OnGitignorePrecedencePreferenceChanged(bool? value)
    {
        if (value is bool preference)
            GitignoreTakesPrecedence = preference;
    }

    public void ResetGitignorePrecedencePreference() => GitignorePrecedencePreference = null;
}
