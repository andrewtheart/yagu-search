using System.Collections.ObjectModel;
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

    public IReadOnlyList<FileGroup> AllGroups => _allGroups;
    public ObservableCollection<FileGroup> VisibleGroups { get; } = [];

    public string ResultFilter { get; set; } = string.Empty;
    public string FileNameFilter { get; set; } = string.Empty;
    public int SortModeIndex { get; set; }
    public int SortDirectionIndex { get; set; }
    public bool GroupByDirectory { get; set; }

    public void Clear()
    {
        VisibleGroups.Clear();
        _allGroups.Clear();
        _index.Clear();
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
        bool resultAvailabilityChanged = false;
        if (evictNewResults && resultStore is not null)
        {
            resultStore.WriteBatch(writeOne =>
            {
                for (int resultIndex = 0; resultIndex < results.Count; resultIndex++)
                {
                    resultAvailabilityChanged |= Add(results[resultIndex], initializeNewGroup, evictNewResult: true, writeOne);
                }
            });
        }
        else
        {
            for (int resultIndex = 0; resultIndex < results.Count; resultIndex++)
            {
                resultAvailabilityChanged |= Add(results[resultIndex], initializeNewGroup, evictNewResult: false);
            }
        }

        return resultAvailabilityChanged;
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
        var filtered = _allGroups.Where(MatchesFilter).ToList();
        bool ascending = SortDirectionIndex == 1;

        List<FileGroup> sortedList;
        if (GroupByDirectory)
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

        if (GroupByDirectory)
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

        VisibleGroups.Clear();
        foreach (var group in sortedList)
            VisibleGroups.Add(group);
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
}
