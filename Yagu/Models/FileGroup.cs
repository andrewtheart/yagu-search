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

    /// <summary>
    /// Optional per-file hard cap on stored matches. Default <see cref="int.MaxValue"/>
    /// (effectively unlimited). When set to a finite value, matches beyond the cap are
    /// dropped and counted in <see cref="HiddenMatchCount"/>. Bound from
    /// <c>AppSettings.MaxMatchesPerFile</c> at app startup and on settings change
    /// (0 in settings = unlimited / <see cref="int.MaxValue"/> here).
    /// </summary>
    public static int MaxMatchesPerGroup { get; set; } = int.MaxValue;

    public string FilePath { get; }

    /// <summary>Number of matches that were dropped due to <see cref="MaxMatchesPerGroup"/>.</summary>
    public int HiddenMatchCount { get; private set; }
    public bool HasHiddenMatches => HiddenMatchCount > 0;

    /// <summary>True when this group represents a file inside an archive.</summary>
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

    public string Extension
    {
        get
        {
            int dotIndex = FilePath.LastIndexOf('.');
            if (dotIndex < 0 || dotIndex == FilePath.Length - 1)
                return "No extension";

            var extension = FilePath[(dotIndex + 1)..];
            return extension.IndexOfAny(['\\', '/', '?']) >= 0
                ? "No extension"
                : extension.ToLowerInvariant();
        }
    }

    /// <summary>Capped subset of items currently rendered in the UI.</summary>
    public BatchObservableCollection<SearchResult> VisibleResults { get; } = new();

    private volatile bool _cleaned;

    public FileGroup(string filePath)
    {
        FilePath = filePath;
        CollectionChanged += OnSelfChanged;
    }

    /// <summary>
    /// Enforces <see cref="MaxMatchesPerGroup"/>. Once the cap is reached, additional
    /// inserts increment <see cref="HiddenMatchCount"/> and the SearchResult reference
    /// is dropped immediately, so its heavy MatchLine/Context strings become eligible
    /// for GC without ever being retained by this group.
    /// </summary>
    protected override void InsertItem(int index, SearchResult item)
    {
        if (Count >= MaxMatchesPerGroup)
        {
            HiddenMatchCount++;
            if (HiddenMatchCount == 1)
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasHiddenMatches)));
            if ((HiddenMatchCount & 0xFF) == 0)
            {
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HiddenMatchCount)));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MatchCount)));
            }
            return;
        }
        base.InsertItem(index, item);
    }

    /// <summary>
    /// Release all held data so the SearchResult strings can be GC'd even if this
    /// FileGroup is temporarily kept alive by a pending metadata task or UI binding.
    /// </summary>
    public void Cleanup()
    {
        _cleaned = true;
        CollectionChanged -= OnSelfChanged;
        VisibleResults.Clear();
        Clear();          // base ObservableCollection items
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
                    _ = item.ShortPreview; // force creation before async eviction clears MatchLine
                    VisibleResults.Add(item);
                    addedToVisible = true;
                }
            }
            if (Count == PageSize + 1 || (!addedToVisible && Count % HiddenNotificationInterval == 0))
                NotifyMoreStateChanged();
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (SearchResult item in e.OldItems)
                VisibleResults.Remove(item);
            NotifyMoreStateChanged();
            NotifySelectionChanged();
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MatchCount)));
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
        var batch = new List<SearchResult>(end - start);
        for (int i = start; i < end; i++)
            batch.Add(this[i]);
        VisibleResults.AddRange(batch);
        NotifyMoreStateChanged();
    }

    public void ShowAll()
    {
        int start = VisibleResults.Count;
        var batch = new List<SearchResult>(Count - start);
        for (int i = start; i < Count; i++)
            batch.Add(this[i]);
        VisibleResults.AddRange(batch);
        NotifyMoreStateChanged();
    }

    private void NotifyMoreStateChanged()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasMore)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(RemainingCount)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowMoreText)));
    }

    public int MatchCount => Count + HiddenMatchCount;

    public long FileSize { get; private set; }
    public DateTime LastModified { get; private set; }
    public DateTime Created { get; private set; }
    public DateTime ModifiedOrCreated => LaterOf(LastModified, Created);
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
            ApplyMetadata(cached.Length, cached.LastModified, cached.Created);
            return;
        }

        try
        {
            var fi = new System.IO.FileInfo(physicalPath);
            if (fi.Exists)
            {
                var metadata = new FileMetadata(fi.Length, fi.LastWriteTime, fi.CreationTime);
                FileMetadataCache.Set(physicalPath, metadata);
                ApplyMetadata(metadata.Length, metadata.LastModified, metadata.Created);
            }
            else if (cached != default)
            {
                ApplyMetadata(cached.Length, cached.LastModified, cached.Created);
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
    public void BeginLoadMetadata(Action<Action> dispatch, CancellationToken cancellationToken = default, Action<FileGroup>? metadataApplied = null)
    {
        // For archive entries, resolve to the outermost file on disk
        string physicalPath = IsArchiveEntry ? ZipArchiveSearcher.SplitArchivePath(FilePath).ArchivePath : FilePath;

        if (FileMetadataCache.TryGet(physicalPath, out var cached))
        {
            ApplyMetadata(cached.Length, cached.LastModified, cached.Created);
            metadataApplied?.Invoke(this);
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            if (_cleaned || cancellationToken.IsCancellationRequested) return;
            bool hasCached = FileMetadataCache.TryGet(physicalPath, out var cachedMetadata);
            long size = hasCached ? cachedMetadata.Length : 0;
            DateTime modified = hasCached ? cachedMetadata.LastModified : default;
            DateTime created = hasCached ? cachedMetadata.Created : default;
            try
            {
                var fi = new System.IO.FileInfo(physicalPath);
                if (fi.Exists)
                {
                    size = fi.Length;
                    modified = fi.LastWriteTime;
                    created = fi.CreationTime;
                    FileMetadataCache.Set(physicalPath, new FileMetadata(size, modified, created));
                }
            }
            catch (Exception ex) { LogService.Instance.Verbose("FileGroup", $"Cannot load metadata for {FilePath}", ex); return; }

            if (_cleaned || cancellationToken.IsCancellationRequested) return;
            dispatch(() =>
            {
                if (!_cleaned)
                {
                    ApplyMetadata(size, modified, created);
                    metadataApplied?.Invoke(this);
                }
            });
        });
    }

    private void ApplyMetadata(long size, DateTime modified, DateTime created)
    {
        FileSize = size;
        LastModified = modified;
        Created = created;
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(FileSize)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(LastModified)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Created)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ModifiedOrCreated)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(FormattedSize)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(FormattedDate)));
    }

    private static DateTime LaterOf(DateTime first, DateTime second)
    {
        if (first == default) return second;
        if (second == default) return first;
        return first >= second ? first : second;
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    private bool _allSelected;
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            _allSelected = value;
            // Always raise PropertyChanged to re-sync the TwoWay-bound CheckBox,
            // which can diverge from the model after user clicks.
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(AllSelected)));
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
        AllSelected = Count > 0 && this.All(r => r.IsSelected);
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
