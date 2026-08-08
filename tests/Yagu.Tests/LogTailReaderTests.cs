using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class LogTailReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yagu-log-tail-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LogTailReaderTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "yagu.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void ReadNew_AllowsActiveWriterAndReadsOnlyAppendedEntries()
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine("[2026-08-07T10:00:00.0000000Z] [WRN] [Search] first");

        var reader = new LogTailReader(_path);
        LogTailReadBatch first = reader.ReadNew();
        Assert.False(first.Reset);
        LogTailEntry firstEntry = Assert.Single(first.Entries);
        Assert.Equal(LogLevel.Warning, firstEntry.Level);
        Assert.Equal("Search", firstEntry.Category);
        Assert.Equal("first", firstEntry.Message);

        writer.WriteLine("[2026-08-07T10:00:01.0000000Z] [INF] [ContentIndex] second");
        LogTailEntry second = Assert.Single(reader.ReadNew().Entries);
        Assert.Equal(LogLevel.Info, second.Level);
        Assert.Equal("ContentIndex", second.Category);
        Assert.Empty(reader.ReadNew().Entries);
    }

    [Fact]
    public void ReadNew_PreservesExceptionContinuationAndResetsAfterTruncate()
    {
        File.WriteAllText(_path,
            "[2026-08-07T10:00:00.0000000Z] [CRT] [MainWindow] failed\n  Exception: boom\n",
            new UTF8Encoding(false));
        var reader = new LogTailReader(_path);
        LogTailEntry entry = Assert.Single(reader.ReadNew().Entries);
        Assert.Contains("Exception: boom", entry.Message);

        File.WriteAllText(_path,
            "[2026-08-07T10:01:00.0000000Z] [VRB] [Terminal] restarted\n",
            new UTF8Encoding(false));
        LogTailReadBatch reset = reader.ReadNew();
        Assert.True(reset.Reset);
        Assert.Equal("restarted", Assert.Single(reset.Entries).Message);
    }

    [Fact]
    public void ReadNew_ContinuationNotifiesBindingsOnTheExistingEntry()
    {
        File.WriteAllText(
            _path,
            "[2026-08-07T10:00:00.0000000Z] [CRT] [MainWindow] failed\n",
            new UTF8Encoding(false));
        var reader = new LogTailReader(_path);
        LogTailEntry entry = Assert.Single(reader.ReadNew().Entries);
        var changed = new List<string?>();
        entry.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        File.AppendAllText(_path, "  Exception: boom\n", new UTF8Encoding(false));
        LogTailReadBatch continuation = reader.ReadNew();

        Assert.Empty(continuation.Entries);
        Assert.Contains("Exception: boom", entry.Message);
        Assert.Equal([nameof(LogTailEntry.Message), nameof(LogTailEntry.RawText)], changed);
    }

    [Fact]
    public void Filter_CoversTimestampSeverityCategoryAndText()
    {
        LogTailEntry search = Parse("[2026-08-07T10:00:00.0000000Z] [WRN] [Search] access denied");
        LogTailEntry index = Parse("[2026-08-07T10:05:00.0000000Z] [INF] [ContentIndex] update complete");
        LogTailEntry terminal = Parse("[2026-08-07T10:10:00.0000000Z] [VRB] [Terminal] command complete");
        LogTailEntry[] entries = [search, index, terminal];

        Assert.Equal([index], LogTailFilter.Apply(entries, "contentindex", null, null, null));
        Assert.Equal([search], LogTailFilter.Apply(entries, null, LogLevel.Warning, null, null));
        Assert.Equal([terminal], LogTailFilter.Apply(entries, null, null, DateTimeOffset.Parse("2026-08-07T10:06:00Z"), null));
        Assert.Equal([index, terminal], LogTailFilter.Apply(entries, null, null, null, "complete"));
    }

    private static LogTailEntry Parse(string line)
    {
        Assert.True(LogTailReader.TryParse(line, out LogTailEntry? entry));
        return entry!;
    }
}