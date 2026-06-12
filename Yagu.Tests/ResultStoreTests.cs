using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

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
    public void DeleteOrphanedTempFiles_DeletesOnlyExistingYaguResultFiles()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "qg-result-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var cutoff = DateTime.UtcNow;
            var oldResultFile = Path.Combine(tempDirectory, $"yagu-results-{Guid.NewGuid():N}.tmp");
            var newResultFile = Path.Combine(tempDirectory, $"yagu-results-{Guid.NewGuid():N}.tmp");
            var unrelatedYaguFile = Path.Combine(tempDirectory, "yagu-other.tmp");

            File.WriteAllText(oldResultFile, "old");
            File.WriteAllText(newResultFile, "new");
            File.WriteAllText(unrelatedYaguFile, "other");
            File.SetLastWriteTimeUtc(oldResultFile, cutoff.AddMinutes(-1));
            File.SetLastWriteTimeUtc(newResultFile, cutoff.AddMinutes(1));
            File.SetLastWriteTimeUtc(unrelatedYaguFile, cutoff.AddMinutes(-1));

            int deleted = ResultStore.DeleteOrphanedTempFiles(tempDirectory, cutoff);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(oldResultFile));
            Assert.True(File.Exists(newResultFile));
            Assert.True(File.Exists(unrelatedYaguFile));
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

    [Fact]
    public async Task CleanupOrphanedTempFilesAsync_DeletesOldOrphanedFiles()
    {
        // Create a file that matches the orphan pattern with an old write time
        var orphan = Path.Combine(Path.GetTempPath(), $"yagu-results-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(orphan, "stale data");
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

            await ResultStore.CleanupOrphanedTempFilesAsync();

            Assert.False(File.Exists(orphan));
        }
        finally
        {
            try { File.Delete(orphan); } catch { }
        }
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
        var orphan = Path.Combine(_dir, "yagu-results-abc123.tmp");
        File.WriteAllText(orphan, "test");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void DeleteOrphanedTempFiles_SkipsRecentFiles()
    {
        var recent = Path.Combine(_dir, "yagu-results-recent.tmp");
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

    [Fact]
    public void DeleteOrphanedTempFiles_LockedFile_DoesNotThrow()
    {
        var locked = Path.Combine(_dir, "yagu-results-locked.tmp");
        File.WriteAllText(locked, "locked");
        File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddHours(-1));

        // Hold the file open so Delete() throws IOException inside the catch block
        using var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(locked));
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

    [Fact]
    public void Write_LongString_RoundtripsCorrectly()
    {
        // Exercises WriteStringFast's pooled-buffer path (strings > 40 chars)
        using var store = new ResultStore();
        var longMatch = new string('X', 200) + "MARKER" + new string('Y', 200);
        var longBefore = new string('A', 100);
        var longAfter = new string('B', 100);

        long offset = store.Write(longMatch, [longBefore], [longAfter]);
        var (matchLine, before, after) = store.Read(offset);

        Assert.Equal(longMatch, matchLine);
        Assert.Single(before);
        Assert.Equal(longBefore, before[0]);
        Assert.Single(after);
        Assert.Equal(longAfter, after[0]);
    }

    [Fact]
    public void WriteBatch_LongStrings_RoundtripCorrectly()
    {
        // Exercises WriteStringFast via WriteBatch path
        using var store = new ResultStore();
        var longLine = new string('Z', 300);
        var result = new SearchResult(
            FilePath: @"C:\test.txt",
            LineNumber: 1,
            MatchLine: longLine,
            MatchStartColumn: 0,
            MatchLength: 5,
            ContextBefore: [new string('C', 80)],
            ContextAfter: [new string('D', 80)]);

        store.WriteBatch(writeOne => result.EvictWith(writeOne));
        result.Hydrate(store);

        Assert.Equal(longLine, result.MatchLine);
        Assert.Equal(new string('C', 80), result.ContextBefore[0]);
        Assert.Equal(new string('D', 80), result.ContextAfter[0]);
    }

    [Fact]
    public void Evict_WithoutPriorShortPreviewAccess_PreservesPreview()
    {
        // Verifies the EnsureShortPreview() fix: evict without ever reading ShortPreview
        using var store = new ResultStore();
        var result = new SearchResult(
            FilePath: @"C:\test.txt",
            LineNumber: 1,
            MatchLine: "short match line",
            MatchStartColumn: 6,
            MatchLength: 5,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());

        // Deliberately do NOT access result.ShortPreview before eviction
        result.Evict(store);

        Assert.True(result.IsEvicted);
        // After eviction, MatchLine should hold the short preview (== full line for short strings)
        Assert.Equal("short match line", result.MatchLine);
        Assert.Equal("short match line", result.ShortPreview);
    }

    [Fact]
    public void Dispose_WithQueuedEviction_DeletesTempFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "qg-result-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string path;
            var store = new ResultStore(tempDirectory);
            try
            {
                path = store.TempFilePath;
                var result = new SearchResult(
                    FilePath: @"C:\repo\file.txt",
                    LineNumber: 1,
                    MatchLine: "needle",
                    MatchStartColumn: 0,
                    MatchLength: 6,
                    ContextBefore: ["before"],
                    ContextAfter: ["after"]);

                Assert.True(store.EnqueueEvict(result));
                Assert.True(File.Exists(path));
            }
            finally
            {
                store.Dispose();
            }

            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }
}

public class ResultStoreAppCloseRegressionTests
{
    [Fact]
    public void MainWindowCloseDisposesViewModelResultStore()
    {
        string root = FindRepoRoot();
        string mainWindowSource = File.ReadAllText(Path.Combine(root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(root, "Yagu", "ViewModels", "MainViewModel.cs"));
        string resultStoreSource = File.ReadAllText(Path.Combine(root, "Yagu", "Services", "ResultStore.cs"));

        string closedHandler = ExtractWindowAfter(mainWindowSource, "this.Closed += (_, _) =>", 400);
        AssertContainsInOrder(closedHandler,
            "Dispose();",
            "LogService.Instance.Flush();");

        string mainWindowDispose = ExtractMethodWindow(mainWindowSource, "Dispose", 1600);
        Assert.Contains("ViewModel.Dispose();", mainWindowDispose);

        string viewModelDispose = ExtractMethodWindow(viewModelSource, "Dispose", 1200);
        Assert.Contains("_resultStore?.Dispose();", viewModelDispose);

        string resultStoreDispose = ExtractMethodWindow(resultStoreSource, "Dispose", 1200);
        Assert.Contains("DeleteTempFile(_path);", resultStoreDispose);

        string deleteTempFile = ExtractMethodWindow(resultStoreSource, "DeleteTempFile", 800);
        Assert.Contains("File.Delete(path);", deleteTempFile);
        Assert.Contains("LogService.Instance.Warning(\"ResultStore\"", deleteTempFile);
    }

    private static string ExtractWindowAfter(string source, string marker, int window)
    {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException($"Marker '{marker}' not found.");
        int end = Math.Min(source.Length, index + window);
        return source[index..end];
    }

    private static string ExtractMethodWindow(string source, string methodName, int window)
    {
        int index = FindMethodDefinition(source, methodName);
        int end = Math.Min(source.Length, index + window);
        return source[index..end];
    }

    private static int FindMethodDefinition(string source, string methodName)
    {
        string needle = methodName + "(";
        int search = 0;
        while (true)
        {
            int index = source.IndexOf(needle, search, StringComparison.Ordinal);
            if (index < 0) break;

            int lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = source.IndexOf('\n', index);
            if (lineEnd < 0) lineEnd = source.Length;
            string line = source[lineStart..lineEnd];
            if (line.Contains("public ", StringComparison.Ordinal) ||
                line.Contains("private ", StringComparison.Ordinal) ||
                line.Contains("protected ", StringComparison.Ordinal))
            {
                return lineStart;
            }

            search = index + needle.Length;
        }

        throw new InvalidOperationException($"Method definition '{methodName}' not found.");
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int offset = 0;
        foreach (string item in expected)
        {
            int index = text.IndexOf(item, offset, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find '{item}' after offset {offset}.");
            offset = index + item.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln).");
    }
}
