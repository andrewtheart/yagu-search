using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Result state exposed to the results view: match/skip counters, throughput, the result store and
/// its collections, recent-directory and query-history suggestions.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int MatchesFound { get; set; }
    [ObservableProperty] public partial int FilesSkipped { get; set; }
    [ObservableProperty] public partial bool HasPerformedSearch { get; set; }
    [ObservableProperty] public partial int AccessDeniedCount { get; set; }
    [ObservableProperty] public partial bool Truncated { get; set; }
    [ObservableProperty] public partial bool Degraded { get; set; }
    [ObservableProperty] public partial string DegradedNoticeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilesPerSecondText { get; set; } = string.Empty;

    /// <summary>UTC time when the last search started.</summary>
    public DateTime SearchStartedUtc => _searchStartedUtc;
    /// <summary>Duration of the last completed search.</summary>
    public TimeSpan LastSearchElapsed => _lastSearchElapsed;
    /// <summary>Total bytes scanned in the last/current search.</summary>
    public long BytesScanned => _bytesScanned;

    /// <summary>Disk-backed store for evicted results. Null before first search.</summary>
    public ResultStore? ActiveResultStore => _resultStore;

    public event EventHandler? ResultGroupsChanging;

    /// <summary>
    /// Raised when the active search is terminated because the result temp-file drive became too full.
    /// The argument is the user-facing termination message. The View surfaces this as a modal notice
    /// (with a link to the disk-space threshold setting) in addition to the inline status/error text.
    /// </summary>
    public event Action<string>? SearchTerminatedByLowDiskSpace;

    public ObservableCollection<FileGroup> ResultGroups => _resultCollection.VisibleGroups;
    public BatchObservableCollection<object> ResultRows { get; } = new();
    public ObservableCollection<string> RecentDirectories { get; } = [];
    public ObservableCollection<HistorySuggestion> DirectorySuggestions { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];
    /// <summary>Autocomplete history for the Semantic (natural-language) query mode, kept separate
    /// from <see cref="SearchHistory"/> so Traditional and Semantic suggestions never mix.</summary>
    public ObservableCollection<string> SemanticSearchHistory { get; } = [];

    private DateTimeOffset? LookupRecentDirectoryTimestamp(string value)
        => _settings.RecentDirectoryTimes.TryGetValue(value, out var t) ? t : null;

    /// <summary>
    /// Builds the query autocomplete dropdown items for the active mode (Semantic vs Traditional),
    /// filtered by <paramref name="filter"/> (substring, case-insensitive), annotated with each entry's
    /// last-used timestamp, and sorted newest-first. Entries without a timestamp (recorded before
    /// timestamps were tracked) sort to the end while preserving their existing relative order.
    /// </summary>
    public List<HistorySuggestion> BuildQuerySuggestionItems(string? filter)
    {
        var history = IsSemanticQueryMode ? SemanticSearchHistory : SearchHistory;
        var times = IsSemanticQueryMode ? _settings.SemanticSearchHistoryTimes : _settings.SearchHistoryTimes;

        string trimmed = filter?.Trim() ?? string.Empty;
        IEnumerable<string> values = trimmed.Length == 0
            ? history
            : history.Where(entry => entry.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        return values
            .Select((value, index) => (value, index, ts: times.TryGetValue(value, out var t) ? (DateTimeOffset?)t : null))
            .OrderByDescending(x => x.ts ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.index)
            .Select(x => new HistorySuggestion(x.value, x.ts))
            .ToList();
    }

    public bool HasResults => ResultGroups.Count > 0;
    public bool ShowEmptyState => !IsSearching && ResultGroups.Count == 0;
    public bool HasFallbackReason => !string.IsNullOrEmpty(FallbackReason);
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);
    public int OtherSkippedCount => Math.Max(0, FilesSkipped - AccessDeniedCount);

    public List<SearchResult> GetAllSelectedResults()
    {
        return _resultCollection.GetAllSelectedResults();
    }
}
