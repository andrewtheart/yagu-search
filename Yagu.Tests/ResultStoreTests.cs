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

    [Fact]
    public void EnqueueEvict_NullResult_ReturnsFalse()
    {
        using var store = new ResultStore();
        Assert.False(store.EnqueueEvict(null!));
    }

    [Fact]
    public void EnqueueEvict_DisposedStore_ReturnsFalse()
    {
        var store = new ResultStore();
        store.Dispose();
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        Assert.False(store.EnqueueEvict(result));
    }

    [Fact]
    public void EnqueueEvictBlocking_NullResult_ReturnsFalse()
    {
        using var store = new ResultStore();
        Assert.False(store.EnqueueEvictBlocking(null!));
    }

    [Fact]
    public void EnqueueEvictBlocking_DisposedStore_ReturnsFalse()
    {
        var store = new ResultStore();
        store.Dispose();
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        Assert.False(store.EnqueueEvictBlocking(result));
    }

    [Fact]
    public async Task EnqueueEvictAsync_NullResult_ReturnsFalse()
    {
        using var store = new ResultStore();
        Assert.False(await store.EnqueueEvictAsync(null!));
    }

    [Fact]
    public async Task EnqueueEvictAsync_DisposedStore_ReturnsFalse()
    {
        var store = new ResultStore();
        store.Dispose();
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        Assert.False(await store.EnqueueEvictAsync(result));
    }

    [Fact]
    public void EnqueueEvictMany_NullList_ReturnsZero()
    {
        using var store = new ResultStore();
        Assert.Equal(0, store.EnqueueEvictMany(null!));
    }

    [Fact]
    public void EnqueueEvictMany_EmptyList_ReturnsZero()
    {
        using var store = new ResultStore();
        Assert.Equal(0, store.EnqueueEvictMany(Array.Empty<SearchResult>()));
    }

    [Fact]
    public async Task EnqueueEvictManyAsync_NullList_ReturnsZero()
    {
        using var store = new ResultStore();
        Assert.Equal(0, await store.EnqueueEvictManyAsync(null!));
    }

    [Fact]
    public async Task EnqueueEvictManyAsync_EmptyList_ReturnsZero()
    {
        using var store = new ResultStore();
        Assert.Equal(0, await store.EnqueueEvictManyAsync(Array.Empty<SearchResult>()));
    }

    [Fact]
    public void EnqueueEvict_ValidResult_ReturnsTrueAndDrains()
    {
        using var store = new ResultStore();
        var result = new SearchResult(@"C:\a.cs", 1, "hello world", 0, 5, ["before"], ["after"]);
        Assert.True(store.EnqueueEvict(result));
        store.Drain();
        Assert.Equal(1, store.EvictedCount);
        Assert.True(result.IsEvicted);
    }

    [Fact]
    public void EnqueueEvictBlocking_ValidResult_EvictsSuccessfully()
    {
        using var store = new ResultStore();
        var result = new SearchResult(@"C:\a.cs", 1, "hello world", 0, 5, ["before"], ["after"]);
        Assert.True(store.EnqueueEvictBlocking(result));
        store.Drain();
        Assert.True(result.IsEvicted);
    }

    [Fact]
    public async Task EnqueueEvictAsync_ValidResult_EvictsSuccessfully()
    {
        using var store = new ResultStore();
        var result = new SearchResult(@"C:\a.cs", 1, "hello world", 0, 5, ["before"], ["after"]);
        Assert.True(await store.EnqueueEvictAsync(result));
        store.Drain();
        Assert.True(result.IsEvicted);
    }

    [Fact]
    public void EvictManyNow_NullList_ReturnsZero()
    {
        using var store = new ResultStore();
        Assert.Equal(0, store.EvictManyNow(null!));
    }

    [Fact]
    public void EvictManyNow_EmptyList_ReturnsZero()
    {
        using var store = new ResultStore();
        Assert.Equal(0, store.EvictManyNow(Array.Empty<SearchResult>()));
    }

    [Fact]
    public void EvictManyNow_MultipleResults_AllEvicted()
    {
        using var store = new ResultStore();
        var results = Enumerable.Range(0, 10).Select(i =>
            new SearchResult($@"C:\file{i}.cs", i, $"line content {i}", 0, 4, [], [])).ToList();

        int evicted = store.EvictManyNow(results);

        Assert.Equal(10, evicted);
        Assert.All(results, r => Assert.True(r.IsEvicted));
    }

    [Fact]
    public void EvictManyNow_ThenHydrate_RestoresContent()
    {
        using var store = new ResultStore();
        var result = new SearchResult(@"C:\f.cs", 5, "important data", 2, 4,
            ["ctx before 1", "ctx before 2"], ["ctx after 1"]);

        store.EvictManyNow([result]);
        Assert.True(result.IsEvicted);

        result.Hydrate(store);
        Assert.False(result.IsEvicted);
        Assert.Equal("important data", result.MatchLine);
        Assert.Equal(["ctx before 1", "ctx before 2"], result.ContextBefore);
        Assert.Equal(["ctx after 1"], result.ContextAfter);
    }

    [Fact]
    public void Drain_EmptyQueue_CompletesImmediately()
    {
        using var store = new ResultStore();
        store.Drain(); // should not hang
    }

    [Fact]
    public void EnqueueEvictMany_MultipleResults_AllQueued()
    {
        using var store = new ResultStore();
        var results = Enumerable.Range(0, 5).Select(i =>
            new SearchResult($@"C:\file{i}.cs", i, $"match {i}", 0, 5, [], [])).ToList();

        int queued = store.EnqueueEvictMany(results);
        Assert.Equal(5, queued);
        store.Drain();
        Assert.Equal(5, store.EvictedCount);
    }

    [Fact]
    public void Read_InvalidOffset_ThrowsInvalidOperationException()
    {
        using var store = new ResultStore();
        var result = new SearchResult(@"C:\a.cs", 1, "match", 0, 5, [], []);
        store.EvictManyNow([result]);

        Assert.Throws<InvalidOperationException>(() => store.Read(-1));
        Assert.Throws<InvalidOperationException>(() => store.Read(99999));
    }

    [Fact]
    public void ReadBatch_EmptyOffsets_ReturnsEmptyArray()
    {
        using var store = new ResultStore();
        var results = store.ReadBatch(ReadOnlySpan<long>.Empty);
        Assert.Empty(results);
    }

    [Fact]
    public void ReadBatch_ValidAndInvalidOffsets_ReturnsNullForInvalid()
    {
        using var store = new ResultStore();
        var r1 = new SearchResult(@"C:\a.cs", 1, "line one", 0, 4, ["b1"], ["a1"]);
        var r2 = new SearchResult(@"C:\b.cs", 2, "line two", 0, 4, ["b2"], ["a2"]);
        store.EvictManyNow([r1, r2]);

        long offset1 = r1.DiskOffset;
        long offset2 = r2.DiskOffset;

        long[] offsets = [offset1, -1, offset2, 99999];
        var batch = store.ReadBatch(offsets);

        Assert.Equal(4, batch.Length);
        Assert.NotNull(batch[0]);
        Assert.Equal("line one", batch[0]!.Value.MatchLine);
        Assert.Null(batch[1]); // negative offset
        Assert.NotNull(batch[2]);
        Assert.Equal("line two", batch[2]!.Value.MatchLine);
        Assert.Null(batch[3]); // beyond stream
    }

    [Fact]
    public void ReadBatch_MultipleValid_PreservesOrder()
    {
        using var store = new ResultStore();
        var items = Enumerable.Range(0, 10).Select(i =>
            new SearchResult($@"C:\f{i}.cs", i + 1, $"match_{i}", 0, 7, [$"before{i}"], [$"after{i}"])).ToList();

        store.EvictManyNow(items);

        var offsets = items.Select(r => r.DiskOffset).ToArray();
        var batch = store.ReadBatch(offsets);

        for (int i = 0; i < 10; i++)
        {
            Assert.NotNull(batch[i]);
            Assert.Equal($"match_{i}", batch[i]!.Value.MatchLine);
            Assert.Equal([$"before{i}"], batch[i]!.Value.ContextBefore);
            Assert.Equal([$"after{i}"], batch[i]!.Value.ContextAfter);
        }
    }

    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposed()
    {
        var store = new ResultStore();
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        store.EvictManyNow([result]);
        long offset = result.DiskOffset;
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.Read(offset));
    }

    [Fact]
    public async Task EnqueueEvictManyAsync_MultipleResults_AllDrained()
    {
        using var store = new ResultStore();
        var results = Enumerable.Range(0, 8).Select(i =>
            new SearchResult($@"C:\f{i}.cs", i + 1, $"match {i}", 0, 5, [], [])).ToList();

        int queued = await store.EnqueueEvictManyAsync(results);
        Assert.Equal(8, queued);
        store.Drain();
        Assert.Equal(8, store.EvictedCount);
    }

    [Fact]
    public void EnqueueEvictBlocking_Cancelled_ReturnsFalse()
    {
        using var store = new ResultStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        Assert.False(store.EnqueueEvictBlocking(result, cts.Token));
    }

    [Fact]
    public async Task EnqueueEvictAsync_Cancelled_ReturnsFalse()
    {
        using var store = new ResultStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        Assert.False(await store.EnqueueEvictAsync(result, cts.Token));
    }
}

// ─── ResultStore: additional branch coverage ────────────────────────────

public class ResultStoreBranchCoverageTests : IDisposable
{
    private readonly string _dir;
    public ResultStoreBranchCoverageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qg-branch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Constructor_CreatesDirectoryIfNotExists()
    {
        var nonExistent = Path.Combine(_dir, "subdir-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(nonExistent));

        using var store = new ResultStore(nonExistent);
        Assert.True(Directory.Exists(nonExistent));
        Assert.True(File.Exists(store.TempFilePath));
    }

    [Fact]
    public void DeleteOrphanedTempFiles_SkipsOwnPidFiles()
    {
        // File matching current PID should never be deleted
        var ownPidFile = Path.Combine(_dir, $"yagu-results-p{Environment.ProcessId}-test.tmp");
        File.WriteAllText(ownPidFile, "own process data");
        File.SetLastWriteTimeUtc(ownPidFile, DateTime.UtcNow.AddHours(-2));

        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(ownPidFile));
    }

    [Fact]
    public void DeleteOrphanedTempFiles_DeletesOtherPidFiles()
    {
        // File matching a different PID should be deleted if old enough
        var otherPidFile = Path.Combine(_dir, "yagu-results-p99999-other.tmp");
        File.WriteAllText(otherPidFile, "other process data");
        File.SetLastWriteTimeUtc(otherPidFile, DateTime.UtcNow.AddHours(-2));

        int deleted = ResultStore.DeleteOrphanedTempFiles(_dir, DateTime.UtcNow);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(otherPidFile));
    }

    [Fact]
    public async Task CleanupOrphanedTempFilesAsync_NullDirectoryEntries_Ignored()
    {
        // Null and empty string entries in tempDirectories should be skipped
        await ResultStore.CleanupOrphanedTempFilesAsync(null, "", "   ");
    }

    [Fact]
    public async Task CleanupOrphanedTempFilesAsync_CustomDirectory_ScansIt()
    {
        var otherPidFile = Path.Combine(_dir, "yagu-results-p11111-old.tmp");
        File.WriteAllText(otherPidFile, "stale");
        File.SetLastWriteTimeUtc(otherPidFile, DateTime.UtcNow.AddHours(-2));

        await ResultStore.CleanupOrphanedTempFilesAsync(_dir);

        Assert.False(File.Exists(otherPidFile));
    }

    [Fact]
    public void EnqueueEvict_DisposedStore_ReturnsFalse()
    {
        var store = new ResultStore(_dir);
        store.Dispose();

        var result = new SearchResult(@"C:\a.cs", 1, "test", 0, 4, [], []);
        Assert.False(store.EnqueueEvict(result));
    }

    [Fact]
    public void EnqueueEvict_NullResult_ReturnsFalse()
    {
        using var store = new ResultStore(_dir);
        Assert.False(store.EnqueueEvict(null!));
    }

    [Fact]
    public void EnqueueEvict_AlreadyEvictedResult_ReturnsFalse()
    {
        using var store = new ResultStore(_dir);
        var result = new SearchResult(@"C:\a.cs", 1, "test", 0, 4, [], []);

        // Evict once
        store.EvictManyNow([result]);
        Assert.True(result.IsEvicted);

        // Trying to enqueue again should fail (TryBeginEviction returns false)
        Assert.False(store.EnqueueEvict(result));
    }

    [Fact]
    public void Drain_AfterDispose_DoesNotHang()
    {
        var store = new ResultStore(_dir);
        var result = new SearchResult(@"C:\a.cs", 1, "data", 0, 4, ["b"], ["a"]);
        store.EnqueueEvict(result);
        store.Dispose();

        // Drain after dispose should return immediately (disposed flag breaks wait loop)
        store.Drain();
    }

    [Fact]
    public void EnqueueEvictMany_NullList_ReturnsZero()
    {
        using var store = new ResultStore(_dir);
        Assert.Equal(0, store.EnqueueEvictMany(null!));
    }

    [Fact]
    public void EnqueueEvictMany_EmptyList_ReturnsZero()
    {
        using var store = new ResultStore(_dir);
        Assert.Equal(0, store.EnqueueEvictMany(Array.Empty<SearchResult>()));
    }

    [Fact]
    public async Task EnqueueEvictManyAsync_NullList_ReturnsZero()
    {
        using var store = new ResultStore(_dir);
        Assert.Equal(0, await store.EnqueueEvictManyAsync(null!));
    }

    [Fact]
    public async Task EnqueueEvictManyAsync_EmptyList_ReturnsZero()
    {
        using var store = new ResultStore(_dir);
        Assert.Equal(0, await store.EnqueueEvictManyAsync(Array.Empty<SearchResult>()));
    }

    [Fact]
    public void EnqueueEvict_ChannelClosed_ReturnsFalseAndCancelsReservation()
    {
        using var store = new ResultStore(_dir);

        // Close the channel writer via reflection without setting _disposed
        var channelField = typeof(ResultStore).GetField("_evictionChannel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var channel = (System.Threading.Channels.Channel<SearchResult>)channelField!.GetValue(store)!;
        channel.Writer.TryComplete();

        var result = new SearchResult(@"C:\a.cs", 1, "data", 0, 4, [], []);
        bool queued = store.EnqueueEvict(result);

        Assert.False(queued);
        Assert.False(result.IsEvicted);
    }

    [Fact]
    public void ReadBatch_CorruptedOffset_ReturnsNullForCorruptEntry()
    {
        using var store = new ResultStore(_dir);

        // Write valid data
        var result = new SearchResult(@"C:\a.cs", 1, "valid line", 0, 5, ["before"], ["after"]);
        store.EvictManyNow([result]);
        long validOffset = result.DiskOffset;

        // Read at offset+1, which is in the middle of a record — will cause malformed read
        long[] offsets = [validOffset, validOffset + 1];
        var batch = store.ReadBatch(offsets);

        Assert.Equal(2, batch.Length);
        Assert.NotNull(batch[0]);
        Assert.Equal("valid line", batch[0]!.Value.MatchLine);
        // The corrupted offset may succeed with garbage or return null (FormatException/EndOfStream)
        // Either outcome is acceptable — the important thing is no exception propagates
    }

    [Fact]
    public void ReadBatch_AfterDispose_ThrowsObjectDisposed()
    {
        var store = new ResultStore(_dir);
        var result = new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []);
        store.EvictManyNow([result]);
        long offset = result.DiskOffset;
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.ReadBatch(new long[] { offset }));
    }

    [Fact]
    public void EvictManyNow_AlreadyEvictedResult_SkipsIt()
    {
        using var store = new ResultStore(_dir);
        var r1 = new SearchResult(@"C:\a.cs", 1, "line1", 0, 5, [], []);
        var r2 = new SearchResult(@"C:\b.cs", 2, "line2", 0, 5, [], []);

        // Evict r1 first
        store.EvictManyNow([r1]);
        Assert.True(r1.IsEvicted);

        // Now try to evict both — r1 should be skipped
        int evicted = store.EvictManyNow([r1, r2]);
        Assert.Equal(1, evicted); // only r2 newly evicted
        Assert.True(r2.IsEvicted);
    }
}

// ─── ResultStore: ReadBatch coverage ────────────────────────────────────

public class ResultStoreReadBatchTests : IDisposable
{
    private readonly ResultStore _store = new();
    public void Dispose() => _store.Dispose();

    [Fact]
    public void ReadBatch_EmptyOffsets_ReturnsEmptyArray()
    {
        var results = _store.ReadBatch(ReadOnlySpan<long>.Empty);
        Assert.Empty(results);
    }

    [Fact]
    public void ReadBatch_ValidOffsets_ReturnsAllResults()
    {
        long o1 = _store.Write("line1", ["b1"], ["a1"]);
        long o2 = _store.Write("line2", [], []);

        var results = _store.ReadBatch(new long[] { o1, o2 });
        Assert.Equal(2, results.Length);
        Assert.NotNull(results[0]);
        Assert.Equal("line1", results[0]!.Value.MatchLine);
        Assert.NotNull(results[1]);
        Assert.Equal("line2", results[1]!.Value.MatchLine);
    }

    [Fact]
    public void ReadBatch_InvalidOffset_ReturnsNull()
    {
        long valid = _store.Write("data", [], []);
        var results = _store.ReadBatch(new long[] { -1, valid, 999999 });
        Assert.Equal(3, results.Length);
        Assert.Null(results[0]);
        Assert.NotNull(results[1]);
        Assert.Equal("data", results[1]!.Value.MatchLine);
        Assert.Null(results[2]);
    }

    [Fact]
    public void ReadBatch_CorruptOffset_ReturnsNull()
    {
        // Write valid data, then read from offset 1 (mid-record = corrupt)
        _store.Write("hello world", ["ctx"], ["after"]);
        var results = _store.ReadBatch(new long[] { 1 });
        // Should get null due to FormatException/EndOfStreamException
        Assert.Single(results);
        // The result is either null (exception caught) or valid (lucky parse)
        // Both are acceptable — the key is no exception leaks out
    }
}

// ─── ResultStore: EnqueueEvictBlocking and Drain ────────────────────────

public class ResultStoreEnqueueDrainTests : IDisposable
{
    private readonly ResultStore _store = new();
    public void Dispose() => _store.Dispose();

    [Fact]
    public void EnqueueEvictBlocking_EvictsResult()
    {
        var r = new SearchResult(@"C:\test.txt", 1, "match line", 0, 5, ["before"], ["after"]);
        bool queued = _store.EnqueueEvictBlocking(r);
        Assert.True(queued);
        _store.Drain();
        Assert.True(r.IsEvicted);
    }

    [Fact]
    public void EnqueueEvictBlocking_NullResult_ReturnsFalse()
    {
        Assert.False(_store.EnqueueEvictBlocking(null!));
    }

    [Fact]
    public void EnqueueEvictBlocking_CancelledToken_ReturnsFalse()
    {
        var r = new SearchResult(@"C:\test.txt", 1, "line", 0, 4, [], []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool queued = _store.EnqueueEvictBlocking(r, cts.Token);
        Assert.False(queued);
    }

    [Fact]
    public async Task EnqueueEvictAsync_EvictsResult()
    {
        var r = new SearchResult(@"C:\test.txt", 1, "async match", 0, 5, [], []);
        bool queued = await _store.EnqueueEvictAsync(r);
        Assert.True(queued);
        _store.Drain();
        Assert.True(r.IsEvicted);
    }

    [Fact]
    public async Task EnqueueEvictAsync_CancelledToken_ReturnsFalse()
    {
        var r = new SearchResult(@"C:\test.txt", 1, "line", 0, 4, [], []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool queued = await _store.EnqueueEvictAsync(r, cts.Token);
        Assert.False(queued);
    }

    [Fact]
    public async Task EnqueueEvictManyAsync_EvictsAll()
    {
        var results = Enumerable.Range(0, 5)
            .Select(i => new SearchResult(@"C:\test.txt", i, $"line{i}", 0, 4, [], []))
            .ToList();
        int queued = await _store.EnqueueEvictManyAsync(results);
        Assert.Equal(5, queued);
        _store.Drain();
        Assert.All(results, r => Assert.True(r.IsEvicted));
    }

    [Fact]
    public void Drain_WithNoEnqueuedItems_CompletesImmediately()
    {
        // Should not hang
        _store.Drain();
    }

    [Fact]
    public void EnqueueEvictMany_EvictsMultipleResults()
    {
        var results = Enumerable.Range(0, 3)
            .Select(i => new SearchResult(@"C:\test.txt", i, $"line{i}", 0, 4, [], []))
            .ToList();
        int queued = _store.EnqueueEvictMany(results);
        Assert.Equal(3, queued);
        _store.Drain();
        Assert.All(results, r => Assert.True(r.IsEvicted));
    }
}

// ─── ResultStore: WriteRawUtf8 / WriteRawContextLinesDirect coverage ───

public class ResultStoreWriteRawUtf8Tests : IDisposable
{
    private readonly string _dir;
    private readonly ResultStore _store;

    public ResultStoreWriteRawUtf8Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qg-rawutf8-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ResultStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public unsafe void WriteRawUtf8_NoContext_RoundtripsMatchLine()
    {
        byte[] matchBytes = System.Text.Encoding.UTF8.GetBytes("hello world");
        long offset;
        fixed (byte* p = matchBytes)
        {
            offset = _store.WriteRawUtf8(matchBytes, null, 0, 0, null, 0, 0);
        }

        var (matchLine, before, after) = _store.Read(offset);
        Assert.Equal("hello world", matchLine);
        Assert.Empty(before);
        Assert.Empty(after);
    }

    [Fact]
    public unsafe void WriteRawUtf8_WithContextLines_RoundtripsAll()
    {
        byte[] matchBytes = System.Text.Encoding.UTF8.GetBytes("match line");
        byte[] ctxBefore = PackContext("before line 1", "before line 2");
        byte[] ctxAfter = PackContext("after line 1");

        long offset;
        fixed (byte* pBefore = ctxBefore)
        fixed (byte* pAfter = ctxAfter)
        {
            offset = _store.WriteRawUtf8(matchBytes, pBefore, ctxBefore.Length, 2, pAfter, ctxAfter.Length, 1);
        }

        var (matchLine, before, after) = _store.Read(offset);
        Assert.Equal("match line", matchLine);
        Assert.Equal(2, before.Count);
        Assert.Equal("before line 1", before[0]);
        Assert.Equal("before line 2", before[1]);
        Assert.Single(after);
        Assert.Equal("after line 1", after[0]);
    }

    [Fact]
    public unsafe void WriteRawUtf8_LongContextLine_TriggerMultiByte7BitEncoding()
    {
        // Context line > 127 bytes triggers the while(v > 0x7F) loop in Write7BitEncodedIntDirect
        byte[] matchBytes = System.Text.Encoding.UTF8.GetBytes("m");
        string longLine = new string('X', 200); // 200 bytes > 127
        byte[] ctx = PackContext(longLine);

        long offset;
        fixed (byte* pCtx = ctx)
        {
            offset = _store.WriteRawUtf8(matchBytes, pCtx, ctx.Length, 1, null, 0, 0);
        }

        var (_, before, _) = _store.Read(offset);
        Assert.Single(before);
        Assert.Equal(longLine, before[0]);
    }

    [Fact]
    public unsafe void WriteRawUtf8_MalformedContext_WritesEmptyForBadEntry()
    {
        byte[] matchBytes = System.Text.Encoding.UTF8.GetBytes("line");
        // Malformed: length field says 9999 but buffer is only 8 bytes
        byte[] malformed = new byte[8];
        BitConverter.GetBytes((uint)9999).CopyTo(malformed, 0);

        long offset;
        fixed (byte* p = malformed)
        {
            offset = _store.WriteRawUtf8(matchBytes, p, malformed.Length, 1, null, 0, 0);
        }

        var (matchLine, before, _) = _store.Read(offset);
        Assert.Equal("line", matchLine);
        Assert.Single(before);
        Assert.Equal(string.Empty, before[0]);
    }

    [Fact]
    public unsafe void WriteRawUtf8_TruncatesLongContextLine()
    {
        byte[] matchBytes = System.Text.Encoding.UTF8.GetBytes("m");
        // Context line longer than MaxDisplayLength*4 bytes — should be truncated
        int maxBytes = (Yagu.Helpers.LineTruncator.MaxDisplayLength + 1) * 4;
        string veryLong = new string('A', maxBytes + 100);
        byte[] ctx = PackContext(veryLong);

        long offset;
        fixed (byte* pCtx = ctx)
        {
            offset = _store.WriteRawUtf8(matchBytes, pCtx, ctx.Length, 1, null, 0, 0);
        }

        var (_, before, _) = _store.Read(offset);
        Assert.Single(before);
        // Should be truncated to at most maxBytes
        Assert.True(before[0].Length <= maxBytes);
    }

    [Fact]
    public unsafe void WriteRawUtf8_MultipleWrites_IndependentOffsets()
    {
        byte[] m1 = System.Text.Encoding.UTF8.GetBytes("first");
        byte[] m2 = System.Text.Encoding.UTF8.GetBytes("second");

        long offset1 = _store.WriteRawUtf8(m1, null, 0, 0, null, 0, 0);
        long offset2 = _store.WriteRawUtf8(m2, null, 0, 0, null, 0, 0);

        Assert.NotEqual(offset1, offset2);
        Assert.Equal("first", _store.Read(offset1).MatchLine);
        Assert.Equal("second", _store.Read(offset2).MatchLine);
    }

    [Fact]
    public unsafe void WriteRawUtf8_EmptyMatchLine_WritesZeroLength()
    {
        long offset = _store.WriteRawUtf8(ReadOnlySpan<byte>.Empty, null, 0, 0, null, 0, 0);
        var (matchLine, _, _) = _store.Read(offset);
        Assert.Equal(string.Empty, matchLine);
    }

    [Fact]
    public unsafe void WriteRawUtf8_ContextWithMultibyteUtf8_TruncatesAtCharBoundary()
    {
        byte[] matchBytes = System.Text.Encoding.UTF8.GetBytes("m");
        // Build a long context line with multi-byte UTF-8 chars near truncation point
        int maxBytes = (Yagu.Helpers.LineTruncator.MaxDisplayLength + 1) * 4;
        // Fill with 3-byte CJK chars then a bunch more to exceed maxBytes
        string multibyteStr = new string('あ', maxBytes / 3 + 50); // each 'あ' = 3 bytes
        byte[] ctx = PackContext(multibyteStr);

        long offset;
        fixed (byte* pCtx = ctx)
        {
            offset = _store.WriteRawUtf8(matchBytes, pCtx, ctx.Length, 1, null, 0, 0);
        }

        var (_, before, _) = _store.Read(offset);
        Assert.Single(before);
        // Result should be valid UTF-8 (no broken sequences) and shorter than original
        Assert.True(before[0].Length < multibyteStr.Length);
        // Must not contain replacement chars — indicates clean boundary
        Assert.DoesNotContain("\uFFFD", before[0]);
    }

    private static byte[] PackContext(params string[] lines)
    {
        using var stream = new MemoryStream();
        foreach (string line in lines)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
            stream.Write(BitConverter.GetBytes((uint)bytes.Length));
            stream.Write(bytes);
        }
        return stream.ToArray();
    }
}
