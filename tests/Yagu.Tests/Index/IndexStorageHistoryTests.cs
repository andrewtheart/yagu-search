using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexStorageHistoryTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(), "yagu-index-history", Guid.NewGuid().ToString("N"));

    public IndexStorageHistoryTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private IndexStorageHistoryStore NewStore()
        => new(Path.Combine(_sandbox, IndexStorageHistoryStore.FileName));

    [Fact]
    public void RecordIfDue_IsHourlyAndProjectsAllOrOneDrive()
    {
        var store = NewStore();
        DateTimeOffset start = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        int captures = 0;
        IndexStorageSummary Capture()
        {
            captures++;
            return Summary(
                (@"C:\", 100),
                (@"C:\src", 50),
                (@"D:\data", 25),
                (null, 5));
        }

        Assert.True(store.TryRecordIfDue(Capture, retentionDays: 30, start));
        Assert.False(store.TryRecordIfDue(Capture, retentionDays: 30, start.AddMinutes(59)));
        Assert.True(store.TryRecordIfDue(Capture, retentionDays: 30, start.AddHours(1)));
        Assert.Equal(2, captures);

        IReadOnlyList<IndexStorageHistorySample> samples = store.Read(30, start.AddHours(1));
        Assert.Equal(2, samples.Count);
        Assert.Equal(new[] { @"C:\", @"D:\" }, IndexStorageHistoryStore.AvailableDrives(samples));
        Assert.All(samples, sample => Assert.Equal(180, sample.TotalBytes));
        Assert.All(samples, sample => Assert.Equal(150, sample.BytesByDrive[@"C:\"]));
        Assert.All(samples, sample => Assert.Equal(25, sample.BytesByDrive[@"D:\"]));
        Assert.All(IndexStorageHistoryStore.BuildSeries(samples, null), point => Assert.Equal(180, point.Bytes));
        Assert.All(IndexStorageHistoryStore.BuildSeries(samples, @"C:\"), point => Assert.Equal(150, point.Bytes));
        Assert.All(IndexStorageHistoryStore.BuildSeries(samples, @"E:\"), point => Assert.Equal(0, point.Bytes));
    }

    [Fact]
    public void RetentionPrunesOldSamplesAndPersistsEmptyAfterDeletion()
    {
        var store = NewStore();
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.True(store.TryRecordIfDue(() => Summary((@"C:\", 100)), 1, now.AddDays(-2)));
        Assert.True(store.TryRecordIfDue(() => Summary((@"C:\", 80)), 1, now));

        IReadOnlyList<IndexStorageHistorySample> samples = store.Read(1, now);
        IndexStorageHistorySample sample = Assert.Single(samples);
        Assert.Equal(80, sample.TotalBytes);

        Assert.True(store.TryRecordIfDue(
            () => new IndexStorageSummary([], 0, 0, _sandbox),
            1,
            now.AddHours(1)));
        Assert.Equal(0, store.Read(1, now.AddHours(1))[^1].TotalBytes);
    }

    [Fact]
    public void MissingEmptyAndCorruptHistoryFailClosed()
    {
        var store = NewStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Empty(store.Read(30, now));
        Assert.False(store.TryRecordIfDue(
            () => new IndexStorageSummary([], 0, 0, _sandbox),
            30,
            now));

        File.WriteAllText(Path.Combine(_sandbox, IndexStorageHistoryStore.FileName), "not json");
        Assert.Empty(store.Read(30, now));
    }

    [Fact]
    public void MaximumRetentionAndRootCount_ProjectToOneBoundedHourlySeries()
    {
        const int hours = AppSettings.MaximumIndexStorageHistoryRetentionDays * 24;
        string[] drives = Enumerable.Range(0, IndexedRootsPolicy.MaxIndexedRoots)
            .Select(index => IndexScopeIdentity.NormalizePath($@"\\server\share{index:D2}\"))
            .ToArray();
        DateTimeOffset start = new(2025, 8, 16, 0, 0, 0, TimeSpan.Zero);
        IndexStorageHistorySample[] samples = Enumerable.Range(0, hours)
            .Select(hour => new IndexStorageHistorySample
            {
                TimestampUtc = start.AddHours(hour),
                TotalBytes = hour,
                BytesByDrive = drives.ToDictionary(
                    static drive => drive,
                    _ => (long)hour,
                    StringComparer.OrdinalIgnoreCase),
            })
            .ToArray();

        IReadOnlyList<IndexStorageHistoryPoint> collective =
            IndexStorageHistoryStore.BuildSeries(samples, null);
        IReadOnlyList<IndexStorageHistoryPoint> oneDrive =
            IndexStorageHistoryStore.BuildSeries(samples, drives[^1]);

        Assert.Equal(hours, collective.Count);
        Assert.Equal(hours, oneDrive.Count);
        Assert.Equal(hours - 1, collective[^1].Bytes);
        Assert.Equal(hours - 1, oneDrive[^1].Bytes);
    }

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexStorageHistoryRetentionDays)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(9999, AppSettings.MaximumIndexStorageHistoryRetentionDays)]
    public void RetentionSettingNormalizes(int value, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexStorageHistoryRetentionDays(value));

    private IndexStorageSummary Summary(params (string? Root, long Bytes)[] values)
    {
        IndexStorageStat[] indexes = values.Select((value, index) => new IndexStorageStat(
            index.ToString(), value.Root, value.Bytes, 0, 0, null,
            IndexStorageHealth.Healthy, RootExists: true, Problem: null)).ToArray();
        return new IndexStorageSummary(indexes, values.Sum(static value => value.Bytes), 0, _sandbox);
    }
}