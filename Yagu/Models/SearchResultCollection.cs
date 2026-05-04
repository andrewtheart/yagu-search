using System.Collections.ObjectModel;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.Models;

/// <summary>
/// Maintains the grouped result collections that back the search results UI.
/// Kept free of WinUI dependencies so large-result UI performance can be tested directly.
/// </summary>
public sealed class SearchResultCollection
{
    private readonly List<FileGroup> _allGroups = [];
    private readonly Dictionary<string, FileGroup> _index = new(StringComparer.OrdinalIgnoreCase);
    private GlobMatcher? _globMatcher;

    public IReadOnlyList<FileGroup> AllGroups => _allGroups;
    public BatchObservableCollection<FileGroup> VisibleGroups { get; } = new();

    public string ResultFilter { get; set; } = string.Empty;
    public string FileNameFilter { get; set; } = string.Empty;
    public string IncludeGlobs { get; set; } = string.Empty;
    public string ExcludeGlobs { get; set; } = string.Empty;
    public int SortModeIndex { get; set; }
    public int SortDirectionIndex { get; set; }
    public GroupMode GroupMode { get; set; }

    public void Clear()
    {
        foreach (var group in _allGroups)
            group.Cleanup();
        VisibleGroups.Clear();
        _allGroups.Clear();
        _allGroups.TrimExcess();
        _index.Clear();
        _index.TrimExcess();
    }

    public bool Add(
        SearchResult result,
        Action<FileGroup>? initializeNewGroup = null,
        bool evictNewResult = false,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictedResultWriter = null)
    {
        var path = result.FilePath;
        bool wasEmpty = _allGroups.Count == 0;
        if (!_index.TryGetValue(path, out var group))
        {
            group = new FileGroup(path);
            initializeNewGroup?.Invoke(group);
            _index[path] = group;
            _allGroups.Add(group);
            if (MatchesFilter(group))
            {
                VisibleGroups.Add(group);
            }
        }

        group.Add(result);
        EvictNewResultIfNeeded(result, evictNewResult, evictedResultWriter);
        return wasEmpty && _allGroups.Count > 0;
    }

    public bool AddRange(
        IReadOnlyList<SearchResult> results,
        Action<FileGroup>? initializeNewGroup = null,
        bool evictNewResults = false,
        ResultStore? resultStore = null)
    {
        if (results.Count == 0) return false;

        bool wasEmpty = _allGroups.Count == 0;
        // Collect newly-created groups so we can batch-add them to VisibleGroups
        // with a single Reset notification instead of one per group.
        List<FileGroup>? newVisibleGroups = null;

        void AddCore(SearchResult result, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictWriter)
        {
            var path = result.FilePath;
            if (!_index.TryGetValue(path, out var group))
            {
                group = new FileGroup(path);
                initializeNewGroup?.Invoke(group);
                _index[path] = group;
                _allGroups.Add(group);
                if (MatchesFilter(group))
                    (newVisibleGroups ??= []).Add(group);
            }
            group.Add(result);
            if (evictWriter is not null)
                EvictNewResultIfNeeded(result, evictNewResults, evictWriter);
        }

        if (evictNewResults && resultStore is not null)
        {
            resultStore.WriteBatch(writeOne =>
            {
                for (int i = 0; i < results.Count; i++)
                    AddCore(results[i], writeOne);
            });
        }
        else
        {
            for (int i = 0; i < results.Count; i++)
                AddCore(results[i], null);
        }

        // Flush new groups to VisibleGroups in one batch notification.
        if (newVisibleGroups is not null)
            VisibleGroups.AddRange(newVisibleGroups);

        return wasEmpty && _allGroups.Count > 0;
    }

    public int EvictAll(ResultStore? resultStore)
    {
        if (resultStore is null) return 0;

        int evicted = 0;
        resultStore.WriteBatch(writeOne =>
        {
            foreach (var group in _allGroups)
            {
                foreach (var result in group)
                {
                    if (!result.IsEvicted)
                    {
                        result.EvictWith(writeOne);
                        evicted++;
                    }
                }
            }
        });

        return evicted;
    }

    public void ApplySortAndFilter()
    {
        _globMatcher = (!string.IsNullOrWhiteSpace(IncludeGlobs) || !string.IsNullOrWhiteSpace(ExcludeGlobs))
            ? new GlobMatcher(SplitGlobs(IncludeGlobs), SplitGlobs(ExcludeGlobs))
            : null;

        var filtered = _allGroups.Where(MatchesFilter).ToList();
        bool ascending = SortDirectionIndex == 1;
        bool groupByDirectory = GroupMode == GroupMode.Folder;
        bool groupByDate = GroupMode >= GroupMode.DateToday;

        List<FileGroup> sortedList;
        if (groupByDirectory)
        {
            if (SortModeIndex == 0)
            {
                sortedList = filtered
                    .GroupBy(group => group.DirectoryName, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group)
                    .ToList();
            }
            else
            {
                var byDirectory = filtered.OrderBy(group => group.DirectoryName, StringComparer.OrdinalIgnoreCase);
                sortedList = (SortModeIndex switch
                {
                    2 => ascending ? byDirectory.ThenBy(group => group.LastModified) : byDirectory.ThenByDescending(group => group.LastModified),
                    3 => ascending ? byDirectory.ThenBy(group => group.FileSize) : byDirectory.ThenByDescending(group => group.FileSize),
                    4 => ascending
                        ? byDirectory.ThenBy(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.FilePath, StringComparer.OrdinalIgnoreCase)
                        : byDirectory.ThenByDescending(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(group => group.FilePath, StringComparer.OrdinalIgnoreCase),
                    _ => ascending ? byDirectory.ThenBy(group => group.MatchCount) : byDirectory.ThenByDescending(group => group.MatchCount),
                }).ToList();
            }
        }
        else if (groupByDate)
        {
            // Classify each group into a date bucket, then sort by bucket order + secondary sort
            var bucketOrder = GetDateBucketOrder(GroupMode);
            var classified = filtered
                .Select(g => (Group: g, Bucket: ClassifyDateBucket(g.LastModified, GroupMode)))
                .ToList();

            IOrderedEnumerable<(FileGroup Group, string Bucket)> ordered;
            if (ascending)
                ordered = classified.OrderBy(x => bucketOrder.TryGetValue(x.Bucket, out var o) ? o : 999);
            else
                ordered = classified.OrderByDescending(x => bucketOrder.TryGetValue(x.Bucket, out var o) ? o : 999);

            sortedList = (SortModeIndex switch
            {
                2 => ascending ? ordered.ThenBy(x => x.Group.LastModified) : ordered.ThenByDescending(x => x.Group.LastModified),
                3 => ascending ? ordered.ThenBy(x => x.Group.FileSize) : ordered.ThenByDescending(x => x.Group.FileSize),
                4 => ascending
                    ? ordered.ThenBy(x => x.Group.FileName, StringComparer.OrdinalIgnoreCase)
                    : ordered.ThenByDescending(x => x.Group.FileName, StringComparer.OrdinalIgnoreCase),
                _ => ascending ? ordered.ThenBy(x => x.Group.MatchCount) : ordered.ThenByDescending(x => x.Group.MatchCount),
            }).Select(x => x.Group).ToList();

            // Assign date bucket headers
            string? lastBucket = null;
            var classifiedDict = classified.ToDictionary(x => x.Group, x => x.Bucket);
            foreach (var group in sortedList)
            {
                var bucket = classifiedDict[group];
                if (!string.Equals(bucket, lastBucket, StringComparison.Ordinal))
                {
                    group.GroupHeaderText = bucket;
                    lastBucket = bucket;
                }
            }
        }
        else
        {
            sortedList = (SortModeIndex switch
            {
                0 => filtered,
                2 => ascending ? filtered.OrderBy(group => group.LastModified).ToList() : filtered.OrderByDescending(group => group.LastModified).ToList(),
                3 => ascending ? filtered.OrderBy(group => group.FileSize).ToList() : filtered.OrderByDescending(group => group.FileSize).ToList(),
                4 => ascending
                    ? filtered.OrderBy(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
                    : filtered.OrderByDescending(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(group => group.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => ascending ? filtered.OrderBy(group => group.MatchCount).ToList() : filtered.OrderByDescending(group => group.MatchCount).ToList(),
            });
        }

        foreach (var group in _allGroups)
            group.GroupHeaderText = null;

        if (groupByDirectory)
        {
            string? lastDirectory = null;
            foreach (var group in sortedList)
            {
                if (!string.Equals(group.DirectoryName, lastDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    group.GroupHeaderText = group.DirectoryName;
                    lastDirectory = group.DirectoryName;
                }
            }
        }
        // Date bucket headers are already assigned above in the groupByDate branch

        VisibleGroups.Clear();
        VisibleGroups.AddRange(sortedList);
    }

    public List<SearchResult> GetAllSelectedResults()
    {
        var results = new List<SearchResult>();
        foreach (var group in VisibleGroups)
        {
            foreach (var result in group)
            {
                if (result.IsSelected) results.Add(result);
            }
        }

        return results;
    }

    private bool MatchesFilter(FileGroup group)
    {
        if (_globMatcher is not null && !_globMatcher.Matches(group.FilePath))
            return false;

        if (!string.IsNullOrWhiteSpace(FileNameFilter))
        {
            if (!group.FileName.Contains(FileNameFilter, StringComparison.OrdinalIgnoreCase)
                && !group.FilePath.Contains(FileNameFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (string.IsNullOrWhiteSpace(ResultFilter)) return true;
        var filter = ResultFilter;
        if (group.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var result in group)
        {
            if (result.MatchLine.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static void EvictNewResultIfNeeded(
        SearchResult result,
        bool evictNewResult,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictedResultWriter)
    {
        if (!evictNewResult || result.IsEvicted || evictedResultWriter is null)
            return;

        result.EvictWith(evictedResultWriter);
    }

    private static string[] SplitGlobs(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── Date-based grouping helpers ────────────────────────────────

    internal static string ClassifyDateBucket(DateTime lastModified, GroupMode mode)
    {
        if (lastModified == default) return "Unknown date";

        var now = DateTime.Now;
        var today = now.Date;

        if (lastModified.Date == today) return "Today";
        if (mode == GroupMode.DateToday) return "Older";

        if (lastModified.Date == today.AddDays(-1)) return "Yesterday";
        if (mode == GroupMode.DateYesterday) return "Older";

        // Monday-based week start
        int dow = (int)today.DayOfWeek;
        var startOfWeek = today.AddDays(-(dow == 0 ? 6 : dow - 1));
        if (lastModified.Date >= startOfWeek) return "This week";
        if (mode == GroupMode.DateThisWeek) return "Older";

        if (lastModified.Year == today.Year && lastModified.Month == today.Month) return "This month";
        if (mode == GroupMode.DateThisMonth) return "Older";

        if (lastModified.Year == today.Year) return "This year";
        if (mode == GroupMode.DateThisYear) return "Older";

        if (lastModified >= today.AddYears(-2)) return "Past 2 years";
        if (mode == GroupMode.DatePast2Years) return "Older";

        if (lastModified >= today.AddYears(-5)) return "Past 5 years";
        if (mode == GroupMode.DatePast5Years) return "Older";

        if (lastModified >= today.AddYears(-10)) return "Past 10 years";
        if (mode == GroupMode.DatePast10Years) return "Older";

        if (lastModified >= today.AddYears(-20)) return "Past 20 years";
        if (mode == GroupMode.DatePast20Years) return "Older";

        if (lastModified >= today.AddYears(-30)) return "Past 30 years";
        if (mode == GroupMode.DatePast30Years) return "Older";

        if (lastModified >= today.AddYears(-50)) return "Past 50 years";
        // DatePast50Years is the widest — everything falls through to Older
        return "Older";
    }

    private static readonly string[] DateBucketNames =
    [
        "Today", "Yesterday", "This week", "This month", "This year",
        "Past 2 years", "Past 5 years", "Past 10 years", "Past 20 years",
        "Past 30 years", "Past 50 years", "Older", "Unknown date"
    ];

    internal static Dictionary<string, int> GetDateBucketOrder(GroupMode mode)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int idx = 0;
        foreach (var name in DateBucketNames)
            order[name] = idx++;
        return order;
    }
}
