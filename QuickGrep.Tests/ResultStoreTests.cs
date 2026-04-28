using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Tests;

public class ResultStoreTests
{
    [Fact]
    public void WriteBatch_EvictsPayloadAndWritesVisibleBytes()
    {
        using var store = new ResultStore();
        var result = new SearchResult(
            FilePath: @"D:\repo\file.txt",
            LineNumber: 12,
            MatchLine: "needle in a haystack",
            MatchStartColumn: 0,
            MatchLength: 6,
            ContextBefore: ["before"],
            ContextAfter: ["after"]);

        store.WriteBatch(writeOne => result.EvictWith(writeOne));

        Assert.True(result.IsEvicted);
        Assert.Equal(1, store.EvictedCount);
        Assert.True(new FileInfo(store.TempFilePath).Length > 0);

        result.Hydrate(store);

        Assert.False(result.IsEvicted);
        Assert.Equal("needle in a haystack", result.MatchLine);
        Assert.Equal(["before"], result.ContextBefore);
        Assert.Equal(["after"], result.ContextAfter);
    }

    [Fact]
    public void DeleteOrphanedTempFiles_DeletesOnlyExistingQuickGrepResultFiles()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "qg-result-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var cutoff = DateTime.UtcNow;
            var oldResultFile = Path.Combine(tempDirectory, $"quickgrep-results-{Guid.NewGuid():N}.tmp");
            var newResultFile = Path.Combine(tempDirectory, $"quickgrep-results-{Guid.NewGuid():N}.tmp");
            var unrelatedQuickGrepFile = Path.Combine(tempDirectory, "quickgrep-other.tmp");

            File.WriteAllText(oldResultFile, "old");
            File.WriteAllText(newResultFile, "new");
            File.WriteAllText(unrelatedQuickGrepFile, "other");
            File.SetLastWriteTimeUtc(oldResultFile, cutoff.AddMinutes(-1));
            File.SetLastWriteTimeUtc(newResultFile, cutoff.AddMinutes(1));
            File.SetLastWriteTimeUtc(unrelatedQuickGrepFile, cutoff.AddMinutes(-1));

            int deleted = ResultStore.DeleteOrphanedTempFiles(tempDirectory, cutoff);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(oldResultFile));
            Assert.True(File.Exists(newResultFile));
            Assert.True(File.Exists(unrelatedQuickGrepFile));
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }
}
