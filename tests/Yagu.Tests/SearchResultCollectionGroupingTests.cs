using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public class SearchResultCollectionGroupingTests
{
    private static SearchResult MakeResult(string filePath, string matchLine = "needle")
        => new(FilePath: filePath, LineNumber: 1, MatchLine: matchLine,
               MatchStartColumn: 0, MatchLength: matchLine.Length,
               ContextBefore: Array.Empty<string>(), ContextAfter: Array.Empty<string>());

    [Fact]
    public void DateRangeFilter_UsesModifiedOrCreatedDate()
    {
        using var temp = new TemporaryDirectory();
        var recentByCreation = temp.CreateFile("created-recent.txt", DateTime.Now.AddYears(-4), DateTime.Now.AddMonths(-6));
        var old = temp.CreateFile("old.txt", DateTime.Now.AddYears(-4), DateTime.Now.AddYears(-4));

        var collection = new SearchResultCollection
        {
            DateRangeFilter = DateRangeFilter.PastTwoYears,
        };

        collection.Add(MakeResult(recentByCreation), group => group.LoadMetadata());
        collection.Add(MakeResult(old), group => group.LoadMetadata());
        collection.ApplySortAndFilter();

        Assert.Single(collection.VisibleGroups);
        Assert.Equal(recentByCreation, collection.VisibleGroups[0].FilePath);
    }

    [Fact]
    public void GroupByDateRange_AssignsRequestedBucketHeaders()
    {
        using var temp = new TemporaryDirectory();
        var week = temp.CreateFile("week.txt", DateTime.Now.AddDays(-2), DateTime.Now.AddDays(-2));
        var sixMonths = temp.CreateFile("six-months.txt", DateTime.Now.AddMonths(-4), DateTime.Now.AddMonths(-4));
        var longAgo = temp.CreateFile("long-ago.txt", DateTime.Now.AddYears(-6), DateTime.Now.AddYears(-6));

        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.DateRangeModifiedCreated,
            SortModeIndex = 0,
            SortDirectionIndex = 0,
        };

        collection.Add(MakeResult(week), group => group.LoadMetadata());
        collection.Add(MakeResult(sixMonths), group => group.LoadMetadata());
        collection.Add(MakeResult(longAgo), group => group.LoadMetadata());
        collection.ApplySortAndFilter();

        var headers = collection.VisibleGroups
            .Select(group => group.GroupHeaderText)
            .Where(header => header is not null)
            .Select(header => header!)
            .ToArray();

        Assert.Equal([
            "Modified/Created past week",
            "Modified/Created past 6 months",
            "a long time ago",
        ], headers);
    }

    [Fact]
    public void GroupByDateRange_CanSortBucketsOlderFirstAndItemsByName()
    {
        using var temp = new TemporaryDirectory();
        var recent = temp.CreateFile("z-recent.txt", DateTime.Now.AddDays(-2), DateTime.Now.AddDays(-2));
        var older = temp.CreateFile("a-older.txt", DateTime.Now.AddMonths(-4), DateTime.Now.AddMonths(-4));

        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.DateRangeModifiedCreated,
            GroupSortDirectionIndex = 1,
            SortModeIndex = 4,
            SortDirectionIndex = 1,
        };

        collection.Add(MakeResult(recent), group => group.LoadMetadata());
        collection.Add(MakeResult(older), group => group.LoadMetadata());
        collection.ApplySortAndFilter();

        Assert.Equal(older, collection.VisibleGroups[0].FilePath);
        Assert.Equal("Modified/Created past 6 months", collection.VisibleGroups[0].GroupHeaderText);
        Assert.Equal(recent, collection.VisibleGroups[1].FilePath);
        Assert.Equal("Modified/Created past week", collection.VisibleGroups[1].GroupHeaderText);
    }

    [Fact]
    public void GroupByDateRangeModified_UsesLastModifiedDate()
    {
        using var temp = new TemporaryDirectory();
        var createdRecent = temp.CreateFile("created-recent.txt", DateTime.Now.AddYears(-6), DateTime.Now.AddDays(-2));
        var modifiedRecent = temp.CreateFile("modified-recent.txt", DateTime.Now.AddDays(-2), DateTime.Now.AddYears(-6));

        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.DateRangeModified,
            SortModeIndex = 0,
            SortDirectionIndex = 0,
        };

        collection.Add(MakeResult(createdRecent), group => group.LoadMetadata());
        collection.Add(MakeResult(modifiedRecent), group => group.LoadMetadata());
        collection.ApplySortAndFilter();

        Assert.Equal(modifiedRecent, collection.VisibleGroups[0].FilePath);
        Assert.Equal("Modified past week", collection.VisibleGroups[0].GroupHeaderText);
        Assert.Equal("a long time ago", collection.VisibleGroups[1].GroupHeaderText);
    }

    [Fact]
    public void GroupByDateRangeCreated_UsesCreatedDate()
    {
        using var temp = new TemporaryDirectory();
        var createdRecent = temp.CreateFile("created-recent.txt", DateTime.Now.AddYears(-6), DateTime.Now.AddDays(-2));
        var modifiedRecent = temp.CreateFile("modified-recent.txt", DateTime.Now.AddDays(-2), DateTime.Now.AddYears(-6));

        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.DateRangeCreated,
            SortModeIndex = 0,
            SortDirectionIndex = 0,
        };

        collection.Add(MakeResult(createdRecent), group => group.LoadMetadata());
        collection.Add(MakeResult(modifiedRecent), group => group.LoadMetadata());
        collection.ApplySortAndFilter();

        Assert.Equal(createdRecent, collection.VisibleGroups[0].FilePath);
        Assert.Equal("Created past week", collection.VisibleGroups[0].GroupHeaderText);
        Assert.Equal("a long time ago", collection.VisibleGroups[1].GroupHeaderText);
    }

    [Fact]
    public void GroupByExtension_UsesLettersAfterLastPeriod()
    {
        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.Extension,
            GroupSortDirectionIndex = 0,
        };

        collection.Add(MakeResult(@"C:\repo\a.CS"));
        collection.Add(MakeResult(@"C:\repo\b.txt"));
        collection.Add(MakeResult(@"C:\repo\README"));
        collection.ApplySortAndFilter();

        var headers = collection.VisibleGroups
            .Select(group => group.GroupHeaderText)
            .Where(header => header is not null)
            .Select(header => header!)
            .ToArray();

        Assert.Equal(["cs", "No extension", "txt"], headers);
    }

    [Fact]
    public void GroupByExtension_CanSortHeadersDescending()
    {
        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.Extension,
            GroupSortDirectionIndex = 1,
        };

        collection.Add(MakeResult(@"C:\repo\a.CS"));
        collection.Add(MakeResult(@"C:\repo\b.txt"));
        collection.Add(MakeResult(@"C:\repo\README"));
        collection.ApplySortAndFilter();

        var headers = collection.VisibleGroups
            .Select(group => group.GroupHeaderText)
            .Where(header => header is not null)
            .Select(header => header!)
            .ToArray();

        Assert.Equal(["txt", "No extension", "cs"], headers);
    }

    [Fact]
    public void GroupByExtension_SortsItemsWithinHeaderBySelectedMode()
    {
        FileMetadataCache.Clear();
        var root = $@"C:\repo\{Guid.NewGuid():N}";
        var large = $@"{root}\a.cs";
        var small = $@"{root}\z.cs";

        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.Extension,
            SortModeIndex = 3,
            SortDirectionIndex = 1,
        };

        AddWithMetadata(collection, large, 10 * MB);
        AddWithMetadata(collection, small, 1 * MB);
        collection.ApplySortAndFilter();

        Assert.Equal(small, collection.VisibleGroups[0].FilePath);
        Assert.Equal(large, collection.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void GroupByFolder_CanSortHeadersDescendingAndItemsByMatchCount()
    {
        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.Folder,
            GroupSortDirectionIndex = 1,
            SortModeIndex = 1,
            SortDirectionIndex = 0,
        };

        collection.Add(MakeResult(@"C:\alpha\one.txt"));
        collection.Add(MakeResult(@"C:\zeta\single.txt"));
        collection.Add(MakeResult(@"C:\zeta\double.txt"));
        collection.Add(MakeResult(@"C:\zeta\double.txt", "second"));
        collection.ApplySortAndFilter();

        Assert.Equal(@"C:\zeta\double.txt", collection.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\zeta", collection.VisibleGroups[0].GroupHeaderText);
        Assert.Equal(@"C:\zeta\single.txt", collection.VisibleGroups[1].FilePath);
        Assert.Equal(@"C:\alpha\one.txt", collection.VisibleGroups[2].FilePath);
        Assert.Equal(@"C:\alpha", collection.VisibleGroups[2].GroupHeaderText);
    }

    [Fact]
    public void GroupByFileSize_SortsBucketsNumerically()
    {
        FileMetadataCache.Clear();
        var root = $@"C:\repo\{Guid.NewGuid():N}";
        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.FileSize,
            GroupSortDirectionIndex = 0,
        };

        AddWithMetadata(collection, $@"{root}\huge.bin", 6 * TB);
        AddWithMetadata(collection, $@"{root}\medium.bin", 75 * MB);
        AddWithMetadata(collection, $@"{root}\small.bin", 900 * KB);
        collection.ApplySortAndFilter();

        var ascendingHeaders = GetHeaders(collection);
        Assert.Equal(["< 1MB", "50 - 100MB", "5TB - 10TB"], ascendingHeaders);

        collection.GroupSortDirectionIndex = 1;
        collection.ApplySortAndFilter();

        var descendingHeaders = GetHeaders(collection);
        Assert.Equal(["5TB - 10TB", "50 - 100MB", "< 1MB"], descendingHeaders);
    }

    [Fact]
    public void ClassifyFileSizeBucket_CoversRequestedRanges()
    {
        Assert.Equal("< 1MB", SearchResultCollection.ClassifyFileSizeBucket(0));
        Assert.Equal("1 - 5 MB", SearchResultCollection.ClassifyFileSizeBucket(1 * MB));
        Assert.Equal("5 - 10MB", SearchResultCollection.ClassifyFileSizeBucket(5 * MB));
        Assert.Equal("10 - 50 MB", SearchResultCollection.ClassifyFileSizeBucket(10 * MB));
        Assert.Equal("50 - 100MB", SearchResultCollection.ClassifyFileSizeBucket(50 * MB));
        Assert.Equal("100 - 500MB", SearchResultCollection.ClassifyFileSizeBucket(100 * MB));
        Assert.Equal("500 - 1GB", SearchResultCollection.ClassifyFileSizeBucket(500 * MB));
        Assert.Equal("1GB - 2GB", SearchResultCollection.ClassifyFileSizeBucket(1 * GB));
        Assert.Equal("2GB - 5GB", SearchResultCollection.ClassifyFileSizeBucket(2 * GB));
        Assert.Equal("5GB - 50GB", SearchResultCollection.ClassifyFileSizeBucket(5 * GB));
        Assert.Equal("50GB - 100GB", SearchResultCollection.ClassifyFileSizeBucket(50 * GB));
        Assert.Equal("100GB - 500GB", SearchResultCollection.ClassifyFileSizeBucket(100 * GB));
        Assert.Equal("500GB - 1TB", SearchResultCollection.ClassifyFileSizeBucket(500 * GB));
        Assert.Equal("1TB - 2TB", SearchResultCollection.ClassifyFileSizeBucket(1 * TB));
        Assert.Equal("2TB - 3TB", SearchResultCollection.ClassifyFileSizeBucket(2 * TB));
        Assert.Equal("3TB - 4TB", SearchResultCollection.ClassifyFileSizeBucket(3 * TB));
        Assert.Equal("4TB - 5TB", SearchResultCollection.ClassifyFileSizeBucket(4 * TB));
        Assert.Equal("5TB - 10TB", SearchResultCollection.ClassifyFileSizeBucket(5 * TB));
        Assert.Equal("10TB+", SearchResultCollection.ClassifyFileSizeBucket(10 * TB));
    }

    [Fact]
    public void DateRangeFilter_CoversAllCutoffs()
    {
        var now = DateTime.Now;

        Assert.True(SearchResultCollection.MatchesDateRange(now, DateRangeFilter.None));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddHours(-12), DateRangeFilter.PastDay));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddDays(-6), DateRangeFilter.PastWeek));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddDays(-13), DateRangeFilter.PastTwoWeeks));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddDays(-20), DateRangeFilter.PastMonth));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddMonths(-2), DateRangeFilter.PastThreeMonths));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddMonths(-5), DateRangeFilter.PastSixMonths));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddMonths(-8), DateRangeFilter.PastNineMonths));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddMonths(-11), DateRangeFilter.PastYear));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddMonths(-23), DateRangeFilter.PastTwoYears));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddMonths(-35), DateRangeFilter.PastThreeYears));
        Assert.True(SearchResultCollection.MatchesDateRange(now.AddYears(-4), DateRangeFilter.PastFiveYears));
        Assert.False(SearchResultCollection.MatchesDateRange(default, DateRangeFilter.PastDay));
    }

    private static void AddWithMetadata(SearchResultCollection collection, string filePath, long fileSize)
    {
        FileMetadataCache.Set(filePath, new FileMetadata(fileSize, DateTime.Now, DateTime.Now));
        collection.Add(MakeResult(filePath), group => group.LoadMetadata());
    }

    private static string[] GetHeaders(SearchResultCollection collection) => collection.VisibleGroups
        .Select(group => group.GroupHeaderText)
        .Where(header => header is not null)
        .Select(header => header!)
        .ToArray();

    private const long KB = 1024L;
    private const long MB = 1024L * KB;
    private const long GB = 1024L * MB;
    private const long TB = 1024L * GB;

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"YaguTests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string CreateFile(string name, DateTime lastWriteTime, DateTime creationTime)
        {
            var path = Path.Combine(_path, name);
            File.WriteAllText(path, "needle");
            File.SetCreationTime(path, creationTime);
            File.SetLastWriteTime(path, lastWriteTime);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }
}