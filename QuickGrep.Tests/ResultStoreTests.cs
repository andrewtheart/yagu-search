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

// ─── ResultStore: Write/Read round-trip coverage ────────────────────────

public class ResultStoreCoverageTests
{
    [Fact]
    public void Write_Read_RoundTrip()
    {
        using var store = new ResultStore();
        var before = new[] { "ctx-before-1", "ctx-before-2" };
        var after = new[] { "ctx-after-1" };
        long offset = store.Write("match line text", before, after);

        Assert.True(offset >= 0);
        Assert.Equal(1, store.EvictedCount);

        var (ml, cb, ca) = store.Read(offset);
        Assert.Equal("match line text", ml);
        Assert.Equal(2, cb.Count);
        Assert.Equal("ctx-before-1", cb[0]);
        Assert.Equal("ctx-before-2", cb[1]);
        Assert.Single(ca);
        Assert.Equal("ctx-after-1", ca[0]);
    }

    [Fact]
    public void Write_Multiple_IncreasesEvictedCount()
    {
        using var store = new ResultStore();
        store.Write("a", Array.Empty<string>(), Array.Empty<string>());
        store.Write("b", Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(2, store.EvictedCount);
    }

    [Fact]
    public void Dispose_DeletesTempFile()
    {
        string path;
        using (var store = new ResultStore())
        {
            path = store.TempFilePath;
            store.Write("data", Array.Empty<string>(), Array.Empty<string>());
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var store = new ResultStore();
        store.Dispose();
        store.Dispose();
    }

    [Fact]
    public void Write_EmptyContext_RoundTrips()
    {
        using var store = new ResultStore();
        long offset = store.Write("line", Array.Empty<string>(), Array.Empty<string>());
        var (ml, cb, ca) = store.Read(offset);
        Assert.Equal("line", ml);
        Assert.Empty(cb);
        Assert.Empty(ca);
    }
}

// ─── ResultStore: CleanupOrphanedTempFilesAsync ─────────────────────────

public class ResultStoreExtraTests
{
    [Fact]
    public async Task CleanupOrphanedTempFilesAsync_DoesNotThrow()
    {
        await ResultStore.CleanupOrphanedTempFilesAsync();
    }
}

// ─── ResultStore: DeleteOrphanedTempFiles ───────────────────────────────

public class ResultStoreDeleteOrphanedTests : IDisposable
{
    private readonly string _dir;
    public ResultStoreDeleteOrphanedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qg-orphan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void DeleteOrphanedTempFiles_DeletesOldFiles()
    {
        var orphan = Path.Combine(_dir, "quickgrep-results-abc123.tmp");
        File.WriteAllText(orphan, "test");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void DeleteOrphanedTempFiles_SkipsRecentFiles()
    {
        var recent = Path.Combine(_dir, "quickgrep-results-recent.tmp");
        File.WriteAllText(recent, "test");
        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void DeleteOrphanedTempFiles_NonexistentDir_ReturnsZero()
    {
        int deleted = ResultStore.DeleteOrphanedTempFiles(@"Z:\nonexistent", DateTime.UtcNow);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void DeleteOrphanedTempFiles_NoMatchingFiles_ReturnsZero()
    {
        File.WriteAllText(Path.Combine(_dir, "other-file.txt"), "not a match");
        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow);
        Assert.Equal(0, deleted);
    }
}

// ─── ResultStore: null matchLine / null list entries ─────────────────────

public class ResultStoreNullBranchTests : IDisposable
{
    private readonly ResultStore _store;
    public ResultStoreNullBranchTests() { _store = new ResultStore(); }
    public void Dispose() { _store.Dispose(); }

    [Fact]
    public void Write_NullMatchLine_WritesEmptyString()
    {
        long offset = _store.Write(null!, [], []);
        Assert.True(offset >= 0);
        var (match, before, after) = _store.Read(offset);
        Assert.Equal(string.Empty, match);
    }

    [Fact]
    public void Write_NullListEntry_WritesEmptyString()
    {
        var listWithNull = new List<string> { null! };
        long offset = _store.Write("line", listWithNull, listWithNull);
        Assert.True(offset >= 0);
        var (_, before, after) = _store.Read(offset);
        Assert.Equal(string.Empty, before[0]);
        Assert.Equal(string.Empty, after[0]);
    }

    [Fact]
    public void WriteBatch_NullMatchLine_WritesEmptyString()
    {
        long offset = -1;
        _store.WriteBatch(writeOne =>
        {
            offset = writeOne(null!, [], []);
        });
        Assert.True(offset >= 0);
        var (match, _, _) = _store.Read(offset);
        Assert.Equal(string.Empty, match);
    }
}

// ─── ResultStore: Dispose & Cleanup ─────────────────────────────────────

public class ResultStoreDisposeTests
{
    [Fact]
    public void Dispose_DeletesTempFile()
    {
        var store = new ResultStore();
        // Write something so the file definitely exists
        store.Write("test", Array.Empty<string>(), Array.Empty<string>());

        // Get the file path via reflection
        var pathField = typeof(ResultStore).GetField("_path",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var path = (string)pathField!.GetValue(store)!;
        Assert.True(File.Exists(path));

        store.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CleanupOrphanedTempFilesAsync_DoesNotThrow()
    {
        // Exercise the code path; it scans temp directory for old files
        await ResultStore.CleanupOrphanedTempFilesAsync();
    }
}
