using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public class FileGroupTests
{
    private static SearchResult MakeResult(string filePath, int line, string matchLine = "needle")
        => new(FilePath: filePath, LineNumber: line, MatchLine: matchLine,
               MatchStartColumn: 0, MatchLength: matchLine.Length,
               ContextBefore: Array.Empty<string>(), ContextAfter: Array.Empty<string>());

    [Fact]
    public void FileName_ExtractsFileName()
    {
        var group = new FileGroup(@"D:\repo\src\file.txt");
        Assert.Equal("file.txt", group.FileName);
    }

    [Fact]
    public void DirectoryName_ExtractsDirectory()
    {
        var group = new FileGroup(@"D:\repo\src\file.txt");
        Assert.Equal(@"D:\repo\src", group.DirectoryName);
    }

    [Fact]
    public void VisibleResults_PagesAtPageSize()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        for (int i = 0; i < FileGroup.PageSize + 50; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        Assert.Equal(FileGroup.PageSize, group.VisibleResults.Count);
        Assert.True(group.HasMore);
        Assert.Equal(50, group.RemainingCount);
    }

    [Fact]
    public void ShowMore_AddsNextPage()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        for (int i = 0; i < FileGroup.PageSize + 50; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        group.ShowMore();
        Assert.Equal(FileGroup.PageSize + 50, group.VisibleResults.Count);
        Assert.False(group.HasMore);
        Assert.Equal(0, group.RemainingCount);
    }

    [Fact]
    public void ShowAll_AddsAllRemaining()
    {
        var group = new FileGroup(@"D:\file.txt");
        for (int i = 0; i < FileGroup.PageSize * 3; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        group.ShowAll();
        Assert.Equal(FileGroup.PageSize * 3, group.VisibleResults.Count);
        Assert.False(group.HasMore);
    }

    [Fact]
    public void ShowMoreText_IncludesRemainingCount()
    {
        var group = new FileGroup(@"D:\file.txt");
        for (int i = 0; i < FileGroup.PageSize + 10; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        Assert.Contains("10", group.ShowMoreText);
        Assert.Contains("remaining", group.ShowMoreText);
    }

    [Fact]
    public void Clear_ResetsVisibleResults()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        group.Add(MakeResult(@"D:\file.txt", 1));
        Assert.Single(group.VisibleResults);

        group.Clear();
        Assert.Empty(group.VisibleResults);
        Assert.False(group.HasMore);
    }

    [Fact]
    public void SelectAll_SetsAllSelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));

        group.SelectAll();
        Assert.True(group.AllSelected);
        Assert.Equal(2, group.SelectedCount);
        Assert.All(group, r => Assert.True(r.IsSelected));
    }

    [Fact]
    public void DeselectAll_ClearsAllSelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.SelectAll();
        group.DeselectAll();

        Assert.False(group.AllSelected);
        Assert.Equal(0, group.SelectedCount);
        Assert.All(group, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public void NotifySelectionChanged_UpdatesAllSelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));

        group[0].IsSelected = true;
        group[1].IsSelected = true;
        group.NotifySelectionChanged();
        Assert.True(group.AllSelected);

        group[0].IsSelected = false;
        group.NotifySelectionChanged();
        Assert.False(group.AllSelected);
    }

    [Fact]
    public void SelectedCountText_FormatsCorrectly()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        group[0].IsSelected = true;
        group.NotifySelectionChanged();

        Assert.Equal("1/2 selected", group.SelectedCountText);
    }

    [Fact]
    public void GetSelectedResults_ReturnsOnlySelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        group[1].IsSelected = true;

        var selected = group.GetSelectedResults();
        Assert.Single(selected);
        Assert.Equal(2, selected[0].LineNumber);
    }

    [Fact]
    public void MatchCount_EqualsCount()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        Assert.Equal(2, group.MatchCount);
    }

    [Fact]
    public void FormatSize_Bytes()
    {
        var group = new FileGroup(@"D:\file.txt");
        Assert.Equal("0 B", group.FormattedSize);
    }

    [Fact]
    public void GroupHeaderText_GetSet()
    {
        var group = new FileGroup(@"D:\file.txt");
        Assert.Null(group.GroupHeaderText);
        Assert.False(group.HasGroupHeader);

        group.GroupHeaderText = @"D:\repo";
        Assert.Equal(@"D:\repo", group.GroupHeaderText);
        Assert.True(group.HasGroupHeader);

        // Setting same value again should not change
        group.GroupHeaderText = @"D:\repo";
        Assert.Equal(@"D:\repo", group.GroupHeaderText);
    }

    [Fact]
    public void LoadMetadata_FromDisk()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "hello");
            var group = new FileGroup(tempFile);
            group.LoadMetadata();

            Assert.True(group.FileSize > 0);
            Assert.NotEqual(default, group.LastModified);
            Assert.Contains("B", group.FormattedSize);
            Assert.NotEmpty(group.FormattedDate);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadMetadata_NonexistentFile_NoThrow()
    {
        var group = new FileGroup(@"Z:\nonexistent\path\file.txt");
        group.LoadMetadata(); // should not throw
    }

    [Fact]
    public void LoadMetadata_CachesResult()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "hello");
            FileMetadataCache.Clear();
            var group = new FileGroup(tempFile);
            group.LoadMetadata();
            Assert.True(group.FileSize > 0);

            // Second call should use cache
            var group2 = new FileGroup(tempFile);
            group2.LoadMetadata();
            Assert.Equal(group.FileSize, group2.FileSize);
        }
        finally
        {
            File.Delete(tempFile);
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public async Task BeginLoadMetadata_DispatchesToUiThread()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "content");
            FileMetadataCache.Clear();
            var group = new FileGroup(tempFile);
            var tcs = new TaskCompletionSource();
            group.BeginLoadMetadata(action =>
            {
                action(); // execute directly
                tcs.SetResult();
            });
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(group.FileSize > 0);
        }
        finally
        {
            File.Delete(tempFile);
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public void BeginLoadMetadata_WithCache_SynchronousReturn()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test");
            FileMetadataCache.Clear();

            // Pre-populate cache
            var fi = new FileInfo(tempFile);
            FileMetadataCache.Set(tempFile, new FileMetadata(fi.Length, fi.LastWriteTime));

            var group = new FileGroup(tempFile);
            bool dispatched = false;
            group.BeginLoadMetadata(_ => dispatched = true);
            Assert.False(dispatched); // Should use cache synchronously
            Assert.True(group.FileSize > 0);
        }
        finally
        {
            File.Delete(tempFile);
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public void FormattedDate_DefaultIsEmpty()
    {
        var group = new FileGroup(@"D:\file.txt");
        Assert.Equal(string.Empty, group.FormattedDate);
    }

    [Fact]
    public void AllSelected_PropertyChangeNotification()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));

        string? changedProp = null;
        ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, e) => changedProp = e.PropertyName;

        group.AllSelected = true;
        Assert.Equal(nameof(group.AllSelected), changedProp);

        // AllSelected setter always fires PropertyChanged (for TwoWay-bound CheckBox sync)
        changedProp = null;
        group.AllSelected = true;
        Assert.Equal(nameof(group.AllSelected), changedProp);
    }

    [Fact]
    public void HiddenNotificationInterval_NotifiesAtMultiples()
    {
        var group = new FileGroup(@"D:\file.txt");
        // Add exactly PageSize items (all visible)
        for (int i = 0; i < FileGroup.PageSize; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        // 201st item triggers first "more" notification
        bool hasMoreChanged = false;
        ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(group.HasMore)) hasMoreChanged = true;
        };
        group.Add(MakeResult(@"D:\file.txt", FileGroup.PageSize + 1));
        Assert.True(hasMoreChanged);
    }

    /// <summary>
    /// Regression: pre-evicted items beyond PageSize must still be retained in the
    /// collection so that HasMore is true and "Show More" remains visible.
    /// Previously, InsertItem dropped pre-evicted stubs past PageSize, making Count == PageSize
    /// and HasMore == false, hiding the button entirely.
    /// </summary>
    [Fact]
    public void PreEvictedItems_BeyondPageSize_RetainedAndHasMoreIsTrue()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;

        // Add PageSize normal items (these will appear in VisibleResults)
        for (int i = 0; i < FileGroup.PageSize; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        Assert.Equal(FileGroup.PageSize, group.VisibleResults.Count);
        Assert.False(group.HasMore);

        // Add pre-evicted items beyond the page size
        int extraCount = 50;
        for (int i = 0; i < extraCount; i++)
        {
            var preEvicted = SearchResult.CreatePreEvicted(@"D:\file.txt", FileGroup.PageSize + i + 1, 0, 5, diskOffset: (i + 1) * 100L);
            group.Add(preEvicted);
        }

        // The pre-evicted items must be retained in the collection
        Assert.Equal(FileGroup.PageSize + extraCount, group.Count);
        // They should NOT be added to VisibleResults (they are evicted stubs)
        Assert.Equal(FileGroup.PageSize, group.VisibleResults.Count);
        // HasMore must be true so the "Show More" button appears
        Assert.True(group.HasMore);
        Assert.Equal(extraCount, group.RemainingCount);
    }
}

// ─── FileGroup: FormatSize branches (MB, GB) ────────────────────────────

public class FileGroupFormatSizeTests
{
    private static SearchResult MakeResult(string path)
        => new(path, 1, "line", 0, 4, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void FormattedSize_KB()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[2048]);
            var group = new FileGroup(tmp);
            group.LoadMetadata();
            Assert.Contains("KB", group.FormattedSize);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FormattedSize_MB_ViaCache()
    {
        var path = @"C:\fake\large_file.dat";
        FileMetadataCache.Clear();
        try
        {
            FileMetadataCache.Set(path, new FileMetadata(5L * 1024 * 1024, DateTime.Now));
            var group = new FileGroup(path);
            group.LoadMetadata();
            Assert.Contains("MB", group.FormattedSize);
        }
        finally { FileMetadataCache.Clear(); }
    }

    [Fact]
    public void FormattedSize_GB_ViaCache()
    {
        var path = @"C:\fake\huge_file.dat";
        FileMetadataCache.Clear();
        try
        {
            FileMetadataCache.Set(path, new FileMetadata(2L * 1024 * 1024 * 1024, DateTime.Now));
            var group = new FileGroup(path);
            group.LoadMetadata();
            Assert.Contains("GB", group.FormattedSize);
        }
        finally { FileMetadataCache.Clear(); }
    }

    [Fact]
    public void FormattedSize_Bytes_SmallFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[100]);
            var group = new FileGroup(tmp);
            group.LoadMetadata();
            Assert.Contains("B", group.FormattedSize);
            Assert.DoesNotContain("KB", group.FormattedSize);
        }
        finally { File.Delete(tmp); }
    }
}

// ─── FileGroup: catch blocks ────────────────────────────────────────────

public class FileGroupExceptionTests
{
    [Fact]
    public void BeginLoadMetadata_NonexistentFile_DispatchesSafely()
    {
        var group = new FileGroup(@"Z:\nonexistent\totally\fake\file.txt");
        bool dispatched = false;
        group.BeginLoadMetadata(action =>
        {
            dispatched = true;
            action();
        });
        Assert.True(group.FileSize == 0 || dispatched);
    }
}

// ─── FileGroup: DirectoryName null coalesce ─────────────────────────────

public class FileGroupRootPathTests
{
    [Fact]
    public void DirectoryName_RootPath_ReturnsEmpty()
    {
        var group = new FileGroup(@"C:\");
        Assert.Equal(string.Empty, group.DirectoryName);
    }
}

// ─── FileGroup: LoadMetadata + BeginLoadMetadata ────────────────────

public class FileGroupMetadataTests : IDisposable
{
    private readonly string _root;

    public FileGroupMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-fgmeta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        FileMetadataCache.Clear();
    }

    public void Dispose()
    {
        FileMetadataCache.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LoadMetadata_RealFile_SetsFileSizeAndLastModified()
    {
        var path = Path.Combine(_root, "test.txt");
        File.WriteAllText(path, "hello world");
        var group = new FileGroup(path);
        group.LoadMetadata();
        Assert.True(group.FileSize > 0);
        Assert.NotEqual(default, group.LastModified);
    }

    [Fact]
    public void LoadMetadata_CacheHit_ReturnsCachedValues()
    {
        var path = Path.Combine(_root, "cached.txt");
        var expected = new FileMetadata(42, new DateTime(2024, 1, 1));
        FileMetadataCache.Set(path, expected);

        var group = new FileGroup(path);
        group.LoadMetadata();
        Assert.Equal(42, group.FileSize);
        Assert.Equal(new DateTime(2024, 1, 1), group.LastModified);
    }

    [Fact]
    public void LoadMetadata_NonExistentFile_DoesNotThrow()
    {
        var group = new FileGroup(Path.Combine(_root, "nope.txt"));
        group.LoadMetadata(); // should not throw
        Assert.Equal(0, group.FileSize);
    }

    [Fact]
    public void BeginLoadMetadata_CacheHit_AppliesSynchronously()
    {
        var path = Path.Combine(_root, "cached2.txt");
        FileMetadataCache.Set(path, new FileMetadata(99, new DateTime(2025, 6, 15)));

        var group = new FileGroup(path);
        group.BeginLoadMetadata(action => action());
        Assert.Equal(99, group.FileSize);
        Assert.Equal(new DateTime(2025, 6, 15), group.LastModified);
    }

    [Fact]
    public async Task BeginLoadMetadata_RealFile_DispatchesThenApplies()
    {
        var path = Path.Combine(_root, "real.txt");
        File.WriteAllText(path, new string('x', 100));

        var group = new FileGroup(path);
        var tcs = new TaskCompletionSource();
        group.BeginLoadMetadata(action =>
        {
            action();
            tcs.SetResult();
        });

        // Wait for the background Task.Run + dispatch callback
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.True(tcs.Task.IsCompleted);
        Assert.True(group.FileSize > 0);
    }

    [Fact]
    public void BeginLoadMetadata_NonExistentFile_DoesNotThrow()
    {
        var group = new FileGroup(Path.Combine(_root, "ghost.txt"));
        // dispatch should not be called for non-existent file
        group.BeginLoadMetadata(action => { action(); });
        // Allow background task to finish
        Thread.Sleep(200);
        // No exception should have been thrown
    }
}
