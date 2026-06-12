using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.Models;

/// <summary>
/// Maintains the grouped result collections that back the search results UI.
/// Kept free of WinUI dependencies so large-result UI performance can be tested directly.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Existing internal model name is widely used by tests and view-model code.")]
public sealed class SearchResultCollection
{
    internal const int EvictionBatchSize = 2048;

    private readonly List<FileGroup> _allGroups = [];
    private readonly Dictionary<string, FileGroup> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _extensionFilters = new(StringComparer.OrdinalIgnoreCase);
    private GlobMatcher? _globMatcher;

    public IReadOnlyList<FileGroup> AllGroups => _allGroups;
    public BatchObservableCollection<FileGroup> VisibleGroups { get; } = new();

    public string FileNameFilter { get; set; } = string.Empty;
    public string IncludeGlobs { get; set; } = string.Empty;
    public string ExcludeGlobs { get; set; } = string.Empty;
    public FilterPatternMode IncludeFilterMode { get; set; } = FilterPatternMode.GlobPath;
    public FilterPatternMode ExcludeFilterMode { get; set; } = FilterPatternMode.GlobPath;
    public int SortModeIndex { get; set; }
    public int SortDirectionIndex { get; set; }
    private readonly List<SortCriterion> _sortCriteria = [];
    public IReadOnlyList<SortCriterion> SortCriteria => _sortCriteria;
    public GroupMode GroupMode { get; set; }
    public int GroupSortDirectionIndex { get; set; }
    public DateRangeFilter DateRangeFilter { get; set; }

    public void SetExtensionFilters(IEnumerable<string> extensions)
    {
        _extensionFilters.Clear();
        foreach (string extension in extensions)
        {
            string normalized = NormalizeExtensionFilter(extension);
            if (!string.IsNullOrWhiteSpace(normalized))
                _extensionFilters.Add(normalized);
        }
    }

    public IReadOnlyList<ExtensionFilterOption> GetExtensionFilterOptions()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _allGroups)
        {
            if (!MatchesFilter(group, includeExtensionFilter: false))
                continue;

            counts.TryGetValue(group.Extension, out int currentCount);
            counts[group.Extension] = currentCount + 1;
        }

        return counts
            .Select(pair => new ExtensionFilterOption(
                pair.Key,
                FormatExtensionDisplayName(pair.Key),
                pair.Value,
                _extensionFilters.Contains(pair.Key)))
            .OrderBy(option => string.Equals(option.Extension, FileGroup.NoExtensionLabel, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetSortCriteria(IEnumerable<SortCriterion> criteria)
    {
        _sortCriteria.Clear();
        var seenModes = new HashSet<int>();
        foreach (var criterion in criteria)
        {
            if (criterion.SortModeIndex <= 0 || !seenModes.Add(criterion.SortModeIndex))
                continue;

            int direction = criterion.SortDirectionIndex == 1 ? 1 : 0;
            _sortCriteria.Add(new SortCriterion(criterion.SortModeIndex, direction));
        }
    }

    public void ClearSortCriteria() => _sortCriteria.Clear();

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

    /// <summary>Remove an entire file group and its results from the collection.</summary>
    public void RemoveGroup(FileGroup group)
    {
        _index.Remove(group.FilePath);
        _allGroups.Remove(group);
        VisibleGroups.Remove(group);
        group.Cleanup();
    }

    /// <summary>Look up the <see cref="FileGroup"/> for the given file path, or null if not found.</summary>
    public FileGroup? FindGroup(string filePath) =>
        _index.TryGetValue(filePath, out var group) ? group : null;

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

        void AddCore(SearchResult result)
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
        }

        // Always add results to the collection without holding the ResultStore lock.
        // This keeps the UI dispatcher out of paging-file writes while degraded.
        for (int i = 0; i < results.Count; i++)
            AddCore(results[i]);

        // Hand off eviction to the ResultStore's single drain task. This eliminates
        // per-batch Task.Run + WriteBatch lock contention with bulk EvictAll calls
        // (previously the cause of 12-second WriteBatch lock-hold spikes).
        if (evictNewResults && resultStore is not null)
            resultStore.EnqueueEvictMany(results);

        // Flush new groups to VisibleGroups in one batch notification.
        if (newVisibleGroups is not null)
            VisibleGroups.AddRange(newVisibleGroups);

        return wasEmpty && _allGroups.Count > 0;
    }

    /// <summary>
    /// Enqueues every in-memory result for asynchronous eviction by the
    /// <see cref="ResultStore"/>'s background drain task. Returns immediately
    /// without waiting for disk writes to complete. Callers that need to know
    /// when bytes have actually hit disk should call
    /// <see cref="ResultStore.Drain"/> themselves (off the UI thread).
    /// </summary>
    /// <returns>The number of results that were newly enqueued for eviction.</returns>
    public int EvictAll(ResultStore? resultStore)
    {
        if (resultStore is null) return 0;

        // Snapshot the group list so we can safely iterate off the UI thread
        // while new groups may still be appended by the dispatcher.
        var groupsSnapshot = _allGroups.ToArray();
        int enqueued = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var group in groupsSnapshot)
        {
            // Use index-based iteration: the UI thread may append to the
            // group concurrently, but existing indices remain stable and
            // EnqueueEvict is idempotent (no-op if already queued/evicted).
            int count = group.Count;
            for (int i = 0; i < count; i++)
            {
                var result = group[i];
                if (result.IsEvicted)
                    continue;

                if (resultStore.EnqueueEvictBlocking(result))
                    enqueued++;
            }
        }

        sw.Stop();
        LogService.Instance.Info("SearchResultCollection",
            $"EvictAll enqueued {enqueued:N0} results for async paging in {sw.ElapsedMilliseconds}ms (drain runs on background thread)");

        return enqueued;
    }

    public void ApplySortAndFilter()
    {
        _globMatcher = (!string.IsNullOrWhiteSpace(IncludeGlobs) || !string.IsNullOrWhiteSpace(ExcludeGlobs))
            ? new GlobMatcher(
                SplitFilterPatterns(IncludeGlobs, IncludeFilterMode),
                SplitFilterPatterns(ExcludeGlobs, ExcludeFilterMode),
                IncludeFilterMode,
                ExcludeFilterMode)
            : null;

        var filtered = _allGroups.Where(group => MatchesFilter(group)).ToList();
        var sortCriteria = GetEffectiveSortCriteria();
        bool groupAscending = GroupSortDirectionIndex == 0;
        bool groupByDirectory = GroupMode == GroupMode.Folder;
        bool groupByDateRange = IsDateRangeGroupMode(GroupMode);
        bool groupByExtension = GroupMode == GroupMode.Extension;
        bool groupByFileSize = GroupMode == GroupMode.FileSize;

        foreach (var group in _allGroups)
            group.GroupHeaderText = null;

        List<FileGroup> sortedList;
        if (groupByDirectory)
        {
            var groupsByDirectory = filtered.GroupBy(group => group.DirectoryName, StringComparer.OrdinalIgnoreCase);
            var orderedGroups = groupAscending
                ? groupsByDirectory.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                : groupsByDirectory.OrderByDescending(group => group.Key, StringComparer.OrdinalIgnoreCase);
            sortedList = orderedGroups.SelectMany(group => ApplySortChain(group, sortCriteria)).ToList();
        }
        else if (groupByDateRange)
        {
            var bucketOrder = GetDateRangeBucketOrder(GroupMode);
            var classified = filtered
                .Select(group => (Group: group, Bucket: ClassifyDateRangeBucket(GetDateRangeGroupDate(group, GroupMode), GroupMode)))
                .ToList();

            IOrderedEnumerable<(FileGroup Group, string Bucket)> ordered = groupAscending
                ? classified.OrderBy(item => bucketOrder.TryGetValue(item.Bucket, out var order) ? order : 999)
                : classified.OrderByDescending(item => bucketOrder.TryGetValue(item.Bucket, out var order) ? order : 999);

            sortedList = ApplySortChain(ordered, sortCriteria).Select(item => item.Group).ToList();

            string? lastBucket = null;
            var classifiedDict = classified.ToDictionary(item => item.Group, item => item.Bucket);
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
        else if (groupByExtension)
        {
            var groupsByExtension = filtered.GroupBy(group => group.Extension, StringComparer.OrdinalIgnoreCase);
            var orderedGroups = groupAscending
                ? groupsByExtension.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                : groupsByExtension.OrderByDescending(group => group.Key, StringComparer.OrdinalIgnoreCase);
            sortedList = orderedGroups.SelectMany(group => ApplySortChain(group, sortCriteria)).ToList();
        }
        else if (groupByFileSize)
        {
            var bucketOrder = GetFileSizeBucketOrder();
            var classified = filtered
                .Select(group => (Group: group, Bucket: ClassifyFileSizeBucket(group.FileSize)))
                .ToList();

            var ordered = groupAscending
                ? classified.OrderBy(item => bucketOrder.TryGetValue(item.Bucket, out var order) ? order : 999)
                : classified.OrderByDescending(item => bucketOrder.TryGetValue(item.Bucket, out var order) ? order : 999);

            sortedList = ApplySortChain(ordered, sortCriteria).Select(item => item.Group).ToList();

            string? lastBucket = null;
            var classifiedDict = classified.ToDictionary(item => item.Group, item => item.Bucket);
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
            sortedList = ApplySortChain(filtered, sortCriteria).ToList();
        }

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
        else if (groupByExtension)
        {
            string? lastExtension = null;
            foreach (var group in sortedList)
            {
                if (!string.Equals(group.Extension, lastExtension, StringComparison.OrdinalIgnoreCase))
                {
                    group.GroupHeaderText = group.Extension;
                    lastExtension = group.Extension;
                }
            }
        }

        VisibleGroups.ReplaceAll(sortedList);
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

    private bool MatchesFilter(FileGroup group, bool includeExtensionFilter = true)
    {
        if (_globMatcher is not null && !_globMatcher.Matches(group.FilePath))
            return false;

        if (!string.IsNullOrWhiteSpace(FileNameFilter))
        {
            if (!MatchesFileTextFilter(group, FileNameFilter))
                return false;
        }

        if (DateRangeFilter != DateRangeFilter.None && !MatchesDateRange(group.ModifiedOrCreated, DateRangeFilter))
            return false;

        if (includeExtensionFilter && _extensionFilters.Count > 0 && !_extensionFilters.Contains(group.Extension))
            return false;

        return true;
    }

    private static bool MatchesFileTextFilter(FileGroup group, string filter)
    {
        string value = filter.Trim();
        if (value.Length == 0)
            return true;

        return ContainsFilter(group.FileName, value)
            || ContainsFilter(group.FilePath, value)
            || ContainsFilter(group.FormattedSize, value)
            || ContainsFilter(group.FormattedDate, value);
    }

    private static bool ContainsFilter(string candidate, string filter)
    {
        if (candidate.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!HasWhitespace(candidate) && !HasWhitespace(filter))
            return false;

        string compactCandidate = RemoveWhitespace(candidate);
        string compactFilter = RemoveWhitespace(filter);
        return compactFilter.Length > 0
            && compactCandidate.Contains(compactFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWhitespace(string value)
        => value.Any(static ch => char.IsWhiteSpace(ch));

    private static string RemoveWhitespace(string value)
        => string.Concat(value.Where(static ch => !char.IsWhiteSpace(ch)));

    internal static string NormalizeExtensionFilter(string extension)
    {
        string value = extension.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (string.Equals(value, FileGroup.NoExtensionLabel, StringComparison.OrdinalIgnoreCase))
            return FileGroup.NoExtensionLabel;

        return value.TrimStart('.', '*').ToLowerInvariant();
    }

    internal static string FormatExtensionDisplayName(string extension)
        => string.Equals(extension, FileGroup.NoExtensionLabel, StringComparison.OrdinalIgnoreCase)
            ? FileGroup.NoExtensionLabel
            : $".{extension}";

    private static void EvictNewResultIfNeeded(
        SearchResult result,
        bool evictNewResult,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictedResultWriter)
    {
        if (!evictNewResult || result.IsEvicted || evictedResultWriter is null)
            return;

        result.EvictWith(evictedResultWriter);
    }

    private static string[] SplitFilterPatterns(string s, FilterPatternMode mode)
    {
        if (string.IsNullOrWhiteSpace(s))
            return [];

        return mode == FilterPatternMode.Regex
            ? [s.Trim()]
            : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private IReadOnlyList<SortCriterion> GetEffectiveSortCriteria()
    {
        if (_sortCriteria.Count > 0)
            return _sortCriteria;

        return SortModeIndex <= 0
            ? Array.Empty<SortCriterion>()
            : [new SortCriterion(SortModeIndex, SortDirectionIndex == 1 ? 1 : 0)];
    }

    private static IEnumerable<FileGroup> ApplySortChain(IEnumerable<FileGroup> groups, IReadOnlyList<SortCriterion> criteria)
    {
        if (criteria.Count == 0)
            return groups;

        IOrderedEnumerable<FileGroup>? ordered = null;
        foreach (var criterion in criteria)
        {
            ordered = ordered is null
                ? OrderByCriterion(groups, criterion)
                : ThenByCriterion(ordered, criterion);
        }

        return ordered ?? groups;
    }

    private static IOrderedEnumerable<(FileGroup Group, string Bucket)> ApplySortChain(
        IOrderedEnumerable<(FileGroup Group, string Bucket)> ordered,
        IReadOnlyList<SortCriterion> criteria)
    {
        foreach (var criterion in criteria)
            ordered = ThenByCriterion(ordered, criterion);

        return ordered;
    }

    private static IOrderedEnumerable<FileGroup> OrderByCriterion(IEnumerable<FileGroup> groups, SortCriterion criterion)
    {
        bool ascending = criterion.SortDirectionIndex == 1;
        return criterion.SortModeIndex switch
        {
            2 => ascending ? groups.OrderBy(group => group.LastModified) : groups.OrderByDescending(group => group.LastModified),
            3 => ascending ? groups.OrderBy(group => group.FileSize) : groups.OrderByDescending(group => group.FileSize),
            4 => ascending
                ? groups.OrderBy(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.FilePath, StringComparer.OrdinalIgnoreCase)
                : groups.OrderByDescending(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(group => group.FilePath, StringComparer.OrdinalIgnoreCase),
            _ => ascending ? groups.OrderBy(group => group.MatchCount) : groups.OrderByDescending(group => group.MatchCount),
        };
    }

    private static IOrderedEnumerable<FileGroup> ThenByCriterion(IOrderedEnumerable<FileGroup> groups, SortCriterion criterion)
    {
        bool ascending = criterion.SortDirectionIndex == 1;
        return criterion.SortModeIndex switch
        {
            2 => ascending ? groups.ThenBy(group => group.LastModified) : groups.ThenByDescending(group => group.LastModified),
            3 => ascending ? groups.ThenBy(group => group.FileSize) : groups.ThenByDescending(group => group.FileSize),
            4 => ascending
                ? groups.ThenBy(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.FilePath, StringComparer.OrdinalIgnoreCase)
                : groups.ThenByDescending(group => group.FileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(group => group.FilePath, StringComparer.OrdinalIgnoreCase),
            _ => ascending ? groups.ThenBy(group => group.MatchCount) : groups.ThenByDescending(group => group.MatchCount),
        };
    }

    private static IOrderedEnumerable<(FileGroup Group, string Bucket)> ThenByCriterion(
        IOrderedEnumerable<(FileGroup Group, string Bucket)> groups,
        SortCriterion criterion)
    {
        bool ascending = criterion.SortDirectionIndex == 1;
        return criterion.SortModeIndex switch
        {
            2 => ascending ? groups.ThenBy(item => item.Group.LastModified) : groups.ThenByDescending(item => item.Group.LastModified),
            3 => ascending ? groups.ThenBy(item => item.Group.FileSize) : groups.ThenByDescending(item => item.Group.FileSize),
            4 => ascending
                ? groups.ThenBy(item => item.Group.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Group.FilePath, StringComparer.OrdinalIgnoreCase)
                : groups.ThenByDescending(item => item.Group.FileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(item => item.Group.FilePath, StringComparer.OrdinalIgnoreCase),
            _ => ascending ? groups.ThenBy(item => item.Group.MatchCount) : groups.ThenByDescending(item => item.Group.MatchCount),
        };
    }

    internal static string ClassifyFileSizeBucket(long fileSize)
    {
        if (fileSize < 1 * MB) return "< 1MB";
        if (fileSize < 5 * MB) return "1 - 5 MB";
        if (fileSize < 10 * MB) return "5 - 10MB";
        if (fileSize < 50 * MB) return "10 - 50 MB";
        if (fileSize < 100 * MB) return "50 - 100MB";
        if (fileSize < 500 * MB) return "100 - 500MB";
        if (fileSize < 1 * GB) return "500 - 1GB";
        if (fileSize < 2 * GB) return "1GB - 2GB";
        if (fileSize < 5 * GB) return "2GB - 5GB";
        if (fileSize < 50 * GB) return "5GB - 50GB";
        if (fileSize < 100 * GB) return "50GB - 100GB";
        if (fileSize < 500 * GB) return "100GB - 500GB";
        if (fileSize < 1 * TB) return "500GB - 1TB";
        if (fileSize < 2 * TB) return "1TB - 2TB";
        if (fileSize < 3 * TB) return "2TB - 3TB";
        if (fileSize < 4 * TB) return "3TB - 4TB";
        if (fileSize < 5 * TB) return "4TB - 5TB";
        if (fileSize < 10 * TB) return "5TB - 10TB";
        return "10TB+";
    }

    private static Dictionary<string, int> GetFileSizeBucketOrder()
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (var name in FileSizeBucketNames)
            order[name] = index++;
        return order;
    }

    private const long KB = 1024L;
    private const long MB = 1024L * KB;
    private const long GB = 1024L * MB;
    private const long TB = 1024L * GB;

    private static readonly string[] FileSizeBucketNames =
    [
        "< 1MB",
        "1 - 5 MB",
        "5 - 10MB",
        "10 - 50 MB",
        "50 - 100MB",
        "100 - 500MB",
        "500 - 1GB",
        "1GB - 2GB",
        "2GB - 5GB",
        "5GB - 50GB",
        "50GB - 100GB",
        "100GB - 500GB",
        "500GB - 1TB",
        "1TB - 2TB",
        "2TB - 3TB",
        "3TB - 4TB",
        "4TB - 5TB",
        "5TB - 10TB",
        "10TB+",
    ];

    internal static bool MatchesDateRange(DateTime modifiedOrCreated, DateRangeFilter filter)
    {
        if (filter == DateRangeFilter.None) return true;
        if (modifiedOrCreated == default) return false;

        var now = DateTime.Now;
        return modifiedOrCreated >= GetDateRangeCutoff(now, filter);
    }

    internal static string ClassifyDateRangeBucket(DateTime modifiedOrCreated)
        => ClassifyDateRangeBucket(modifiedOrCreated, GroupMode.DateRangeModifiedCreated);

    internal static string ClassifyDateRangeBucket(DateTime date, GroupMode mode)
    {
        if (date == default) return "Unknown date";

        var prefix = GetDateRangeBucketPrefix(mode);
        var now = DateTime.Now;
        if (date >= now.AddDays(-1)) return $"{prefix} past day";
        if (date >= now.AddDays(-7)) return $"{prefix} past week";
        if (date >= now.AddDays(-14)) return $"{prefix} past 2 weeks";
        if (date >= now.AddMonths(-1)) return $"{prefix} past month";
        if (date >= now.AddMonths(-3)) return $"{prefix} past 3 months";
        if (date >= now.AddMonths(-6)) return $"{prefix} past 6 months";
        if (date >= now.AddMonths(-9)) return $"{prefix} past 9 months";
        if (date >= now.AddYears(-1)) return $"{prefix} past year";
        if (date >= now.AddYears(-2)) return $"{prefix} past two years";
        if (date >= now.AddYears(-3)) return $"{prefix} past three years";
        if (date >= now.AddYears(-5)) return $"{prefix} past 5 years";
        return "a long time ago";
    }

    internal static Dictionary<string, int> GetDateRangeBucketOrder()
        => GetDateRangeBucketOrder(GroupMode.DateRangeModifiedCreated);

    internal static Dictionary<string, int> GetDateRangeBucketOrder(GroupMode mode)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int idx = 0;
        foreach (var name in GetDateRangeBucketNames(mode))
            order[name] = idx++;
        return order;
    }

    private static string[] GetDateRangeBucketNames(GroupMode mode)
    {
        var prefix = GetDateRangeBucketPrefix(mode);
        return
        [
            $"{prefix} past day",
            $"{prefix} past week",
            $"{prefix} past 2 weeks",
            $"{prefix} past month",
            $"{prefix} past 3 months",
            $"{prefix} past 6 months",
            $"{prefix} past 9 months",
            $"{prefix} past year",
            $"{prefix} past two years",
            $"{prefix} past three years",
            $"{prefix} past 5 years",
            "a long time ago",
            "Unknown date",
        ];
    }

    private static string GetDateRangeBucketPrefix(GroupMode mode) => mode switch
    {
        GroupMode.DateRangeModified => "Modified",
        GroupMode.DateRangeCreated => "Created",
        _ => "Modified/Created",
    };

    private static DateTime GetDateRangeGroupDate(FileGroup group, GroupMode mode) => mode switch
    {
        GroupMode.DateRangeModified => group.LastModified,
        GroupMode.DateRangeCreated => group.Created,
        _ => group.ModifiedOrCreated,
    };

    private static bool IsDateRangeGroupMode(GroupMode mode) => mode is
        GroupMode.DateRangeModified or
        GroupMode.DateRangeCreated or
        GroupMode.DateRangeModifiedCreated;

    private static DateTime GetDateRangeCutoff(DateTime now, DateRangeFilter filter) => filter switch
    {
        DateRangeFilter.PastDay => now.AddDays(-1),
        DateRangeFilter.PastWeek => now.AddDays(-7),
        DateRangeFilter.PastTwoWeeks => now.AddDays(-14),
        DateRangeFilter.PastMonth => now.AddMonths(-1),
        DateRangeFilter.PastThreeMonths => now.AddMonths(-3),
        DateRangeFilter.PastSixMonths => now.AddMonths(-6),
        DateRangeFilter.PastNineMonths => now.AddMonths(-9),
        DateRangeFilter.PastYear => now.AddYears(-1),
        DateRangeFilter.PastTwoYears => now.AddYears(-2),
        DateRangeFilter.PastThreeYears => now.AddYears(-3),
        DateRangeFilter.PastFiveYears => now.AddYears(-5),
        _ => DateTime.MinValue,
    };

    internal static string ClassifyDateBucket(DateTime lastModified, GroupMode mode)
    {
        if (lastModified == default) return "Unknown date";

        var now = DateTime.Now;
        var today = now.Date;

        if (lastModified.Date == today) return "Today";
        if (mode == GroupMode.DateToday) return "Older";

        if (lastModified.Date == today.AddDays(-1)) return "Yesterday";
        if (mode == GroupMode.DateYesterday) return "Older";

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
        return "Older";
    }

    private static readonly string[] LegacyDateBucketNames =
    [
        "Today", "Yesterday", "This week", "This month", "This year",
        "Past 2 years", "Past 5 years", "Past 10 years", "Past 20 years",
        "Past 30 years", "Past 50 years", "Older", "Unknown date"
    ];

    internal static Dictionary<string, int> GetDateBucketOrder(GroupMode mode)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int idx = 0;
        foreach (var name in LegacyDateBucketNames)
            order[name] = idx++;
        return order;
    }
}

public readonly record struct ExtensionFilterOption(
    string Extension,
    string DisplayName,
    int Count,
    bool IsSelected);
