using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using Yagu.Services;

namespace Yagu.Models;

/// <summary>
/// Group of <see cref="SearchResult"/> instances that share the same file path.
/// Used as the grouping key in the results ListView.
/// </summary>
public sealed class FileGroup : ObservableCollection<SearchResult>
{
    public const int PageSize = 200;
    private const int HiddenNotificationInterval = PageSize;

    public string FilePath { get; }

    /// <summary>True when this group represents a file inside a ZIP archive.</summary>
    public bool IsArchiveEntry => ZipArchiveSearcher.IsArchivePath(FilePath);

    /// <summary>
    /// Display-friendly file name. For archive entries shows e.g. "archive.zip?/entry.txt".
    /// </summary>
    public string FileName
    {
        get
        {
            if (!IsArchiveEntry) return System.IO.Path.GetFileName(FilePath);
            var (archivePath, entryPath) = ZipArchiveSearcher.SplitArchivePath(FilePath);
            return $"{System.IO.Path.GetFileName(archivePath)}{ZipArchiveSearcher.ArchiveSeparator}/{entryPath}";
        }
    }

    /// <summary>
    /// Directory containing the outermost archive (or the file itself for non-archive paths).
    /// </summary>
    public string DirectoryName
    {
        get
        {
            if (!IsArchiveEntry) return System.IO.Path.GetDirectoryName(FilePath) ?? string.Empty;
            var (archivePath, _) = ZipArchiveSearcher.SplitArchivePath(FilePath);
            return System.IO.Path.GetDirectoryName(archivePath) ?? string.Empty;
        }
    }

    /// <summary>Capped subset of items currently rendered in the UI.</summary>
    public ObservableCollection<SearchResult> VisibleResults { get; } = new();

    public FileGroup(string filePath)
    {
        FilePath = filePath;
        CollectionChanged += OnSelfChanged;
    }

    private void OnSelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            bool addedToVisible = false;
            foreach (SearchResult item in e.NewItems)
            {
                if (VisibleResults.Count < PageSize)
                {
                    VisibleResults.Add(item);
                    addedToVisible = true;
                }
            }
            if (Count == PageSize + 1 || (!addedToVisible && Count % HiddenNotificationInterval == 0))
                NotifyMoreStateChanged();
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            VisibleResults.Clear();
            NotifyMoreStateChanged();
        }
    }

    public bool HasMore => VisibleResults.Count < Count;
    public int RemainingCount => Count - VisibleResults.Count;
    public string ShowMoreText => $"Show more ({RemainingCount:N0} remaining)";

    public void ShowMore()
    {
        int start = VisibleResults.Count;
        int end = Math.Min(Count, start + PageSize);
        for (int i = start; i < end; i++)
            VisibleResults.Add(this[i]);
        NotifyMoreStateChanged();
    }

    public void ShowAll()
    {
        int start = VisibleResults.Count;
        for (int i = start; i < Count; i++)
            VisibleResults.Add(this[i]);
        NotifyMoreStateChanged();
    }

    private void NotifyMoreStateChanged()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasMore)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(RemainingCount)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowMoreText)));
    }

    public int MatchCount => Count;

    public long FileSize { get; private set; }
    public DateTime LastModified { get; private set; }
    public string FormattedSize => FormatSize(FileSize);
    public string FormattedDate => LastModified == default ? string.Empty : LastModified.ToString("yyyy-MM-dd HH:mm");

    private string? _groupHeaderText;
    public string? GroupHeaderText
    {
        get => _groupHeaderText;
        set
        {
            if (_groupHeaderText != value)
            {
                _groupHeaderText = value;
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(GroupHeaderText)));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasGroupHeader)));
            }
        }
    }
    public bool HasGroupHeader => !string.IsNullOrEmpty(GroupHeaderText);

    public void LoadMetadata()
    {
        // For archive entries, resolve to the outermost file on disk
        string physicalPath = IsArchiveEntry ? ZipArchiveSearcher.SplitArchivePath(FilePath).ArchivePath : FilePath;

        if (FileMetadataCache.TryGet(physicalPath, out var cached))
        {
            ApplyMetadata(cached.Length, cached.LastModified);
            return;
        }

        try
        {
            var fi = new System.IO.FileInfo(physicalPath);
            if (fi.Exists)
            {
                var metadata = new FileMetadata(fi.Length, fi.LastWriteTime);
                FileMetadataCache.Set(physicalPath, metadata);
                ApplyMetadata(metadata.Length, metadata.LastModified);
            }
        }
        catch (Exception ex) { LogService.Instance.Verbose("FileGroup", $"Cannot load metadata for {FilePath}", ex); }
    }

    /// <summary>
    /// Load file metadata on a worker thread and dispatch the resulting property
    /// notifications back to the UI. Avoids stalling the UI dispatcher with one
    /// FileInfo syscall per result group on huge searches. The <paramref name="dispatch"/>
    /// delegate is responsible for marshalling its action onto the UI thread.
    /// </summary>
    public void BeginLoadMetadata(Action<Action> dispatch)
    {
        // For archive entries, resolve to the outermost file on disk
        string physicalPath = IsArchiveEntry ? ZipArchiveSearcher.SplitArchivePath(FilePath).ArchivePath : FilePath;

        if (FileMetadataCache.TryGet(physicalPath, out var cached))
        {
            ApplyMetadata(cached.Length, cached.LastModified);
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            long size = 0;
            DateTime modified = default;
            try
            {
                var fi = new System.IO.FileInfo(physicalPath);
                if (fi.Exists)
                {
                    size = fi.Length;
                    modified = fi.LastWriteTime;
                    FileMetadataCache.Set(physicalPath, new FileMetadata(size, modified));
                }
            }
            catch (Exception ex) { LogService.Instance.Verbose("FileGroup", $"Cannot load metadata for {FilePath}", ex); return; }

            dispatch(() =>
            {
                ApplyMetadata(size, modified);
            });
        });
    }

    private void ApplyMetadata(long size, DateTime modified)
    {
        FileSize = size;
        LastModified = modified;
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(FileSize)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(LastModified)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(FormattedSize)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(FormattedDate)));
    }

    private bool _allSelected;
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (_allSelected != value)
            {
                _allSelected = value;
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(AllSelected)));
            }
        }
    }

    public int SelectedCount => this.Count(r => r.IsSelected);
    public string SelectedCountText => $"{SelectedCount}/{Count} selected";

    public void SelectAll()
    {
        foreach (var r in this) r.IsSelected = true;
        AllSelected = true;
        NotifySelectionChanged();
    }

    public void DeselectAll()
    {
        foreach (var r in this) r.IsSelected = false;
        AllSelected = false;
        NotifySelectionChanged();
    }

    public void NotifySelectionChanged()
    {
        AllSelected = this.All(r => r.IsSelected);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCount)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCountText)));
    }

    public IReadOnlyList<SearchResult> GetSelectedResults() => this.Where(r => r.IsSelected).ToList();

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }
}
