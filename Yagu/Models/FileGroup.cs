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

    /// <summary>Count of evicted results that were not retained in the collection (disk-only).</summary>
    private int _evictedOnlyCount;

    /// <summary>
    /// 24-byte compact record for an evicted match. Stored in <see cref="_evictedStubs"/> instead of
    /// the full <see cref="SearchResult"/> (~88 B + per-record overhead) while the group is collapsed.
    /// FilePath is implicit (held once on <see cref="FilePath"/>). Stubs are materialized into
    /// <see cref="Items"/> on demand when the group is expanded (see <see cref="MaterializeEvictedStubs"/>).
    /// </summary>
    internal readonly record struct EvictedStub(int LineNumber, int MatchStartColumn, int MatchLength, long DiskOffset);

    /// <summary>
    /// Compact stub list for collapsed groups. Each entry is a struct (24 B vs ~88 B for SearchResult)
    /// and the SearchResult instance itself is never retained — it is GC'd immediately after
    /// <see cref="InsertItem"/> returns, dropping retained heap by ~1.4 GB on a full-disk scan.
    /// </summary>
    private List<EvictedStub>? _evictedStubs;

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

    /// <summary>Subset of items currently rendered in the UI. Populated lazily when the group expands.</summary>
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
    // Cached event args (avoid per-insert allocation on the hot path).
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_countChanged = new("Count");
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_indexerChanged = new("Item[]");

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

        // Fast path for the common case: group is collapsed (no expanded UI rows bound to
        // this collection). Bypass ObservableCollection's per-item NotifyCollectionChanged
        // allocation (~100 B each × 10M matches = ~1 GB allocated in Iter 14). The Add
        // notification has no observers besides OnSelfChanged, which we inline here. When
        // the group later becomes expanded, ShowMore() backfills VisibleResults from Items.
        if (!_isExpanded)
        {
            // Iter 16: for pre-evicted items (degraded mode = always-on), drop the
            // SearchResult reference entirely and remember the match as a 24-byte struct.
            // The SearchResult is recreated lazily via MaterializeEvictedStubs() on expand.
            if (item.IsEvicted)
            {
                (_evictedStubs ??= new List<EvictedStub>(64))
                    .Add(new EvictedStub(item.LineNumber, item.MatchStartColumn, item.MatchLength, item.DiskOffset));
                _evictedOnlyCount++;
                int totalStub = Items.Count + _evictedOnlyCount;
                bool notifyStub = totalStub == PageSize + 1
                              || (totalStub % HiddenNotificationInterval) == 0;
                if (notifyStub)
                {
                    OnPropertyChanged(s_countChanged);
                    OnPropertyChanged(s_indexerChanged);
                    NotifyMoreStateChanged();
                    OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MatchCount)));
                }
                return;
            }

            Items.Insert(index, item);
            // Coalesced bookkeeping — same thresholds as the OnSelfChanged Add branch's
            // "hidden growth" notify. PageSize / HiddenNotificationInterval throttle.
            int count = Count;
            bool notify = count == PageSize + 1
                       || (count % HiddenNotificationInterval) == 0;
            if (notify)
            {
                OnPropertyChanged(s_countChanged);
                OnPropertyChanged(s_indexerChanged);
                NotifyMoreStateChanged();
            }
            return;
        }

        base.InsertItem(index, item);
    }

    /// <summary>
    /// Iter 16: Materialize any compact <see cref="EvictedStub"/> entries into real
    /// <see cref="SearchResult"/> instances stored in <see cref="Items"/>. Called from
    /// the <see cref="IsExpanded"/> setter (true transition) and from the UI's
    /// expansion handler before <see cref="ShowMore"/> indexes into the group.
    /// </summary>
    public void MaterializeEvictedStubs()
    {
        var stubs = _evictedStubs;
        if (stubs is null || stubs.Count == 0) return;
        _evictedStubs = null;
        int materialized = stubs.Count;
        if (Items is List<SearchResult> list)
            list.EnsureCapacity(list.Count + materialized);
        for (int i = 0; i < stubs.Count; i++)
        {
            var s = stubs[i];
            Items.Add(SearchResult.CreatePreEvicted(FilePath, s.LineNumber, s.MatchStartColumn, s.MatchLength, s.DiskOffset));
        }
        _evictedOnlyCount -= materialized;
        OnPropertyChanged(s_countChanged);
        OnPropertyChanged(s_indexerChanged);
        NotifyMoreStateChanged();
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MatchCount)));
    }

    /// <summary>
    /// Release all held data so the SearchResult strings can be GC'd even if this
    /// FileGroup is temporarily kept alive by a pending metadata task or UI binding.
    /// </summary>
    public void Cleanup()
    {
        _cleaned = true;
        _evictedOnlyCount = 0;
        _evictedStubs = null;
        CollectionChanged -= OnSelfChanged;
        VisibleResults.Clear();
        Clear();          // base ObservableCollection items
    }

    private void OnSelfChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            bool addedToVisible = false;
            if (IsExpanded)
            {
                foreach (SearchResult item in e.NewItems)
                {
                    if (VisibleResults.Count < PageSize && !item.IsEvicted)
                    {
                        _ = item.ShortPreview;
                        VisibleResults.Add(item);
                        addedToVisible = true;
                    }
                }
            }
            bool notify = Count == PageSize + 1
                || (!addedToVisible && Count % HiddenNotificationInterval == 0)
                || (!addedToVisible && IsExpanded && Count == VisibleResults.Count + 1);
            if (notify)
            {
                NotifyMoreStateChanged();
            }
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

    // Iter 16: include unmaterialized evicted stubs in the "more" calculation so the
    // expand-evicted code path always finds work to do (and the UI's "Show more" text
    // reflects the true remaining count, not just the materialized Items count).
    public bool HasMore => VisibleResults.Count < (Count + _evictedOnlyCount);
    public int RemainingCount => (Count + _evictedOnlyCount) - VisibleResults.Count;
    public string ShowMoreText => $"Show more ({RemainingCount:N0} remaining)";

    public void ClearVisibleResults()
    {
        if (VisibleResults.Count == 0) return;
        VisibleResults.Clear();
        NotifyMoreStateChanged();
    }

    public int ShowMore(int maxItems = PageSize)
    {
        if (maxItems <= 0)
            return 0;

        int start = VisibleResults.Count;
        int end = Math.Min(Count, start + maxItems);
        if (end <= start)
        {
            NotifyMoreStateChanged();
            return 0;
        }

        var batch = new List<SearchResult>(end - start);
        int evictedCount = 0;
        int emptyMatchCount = 0;
        for (int i = start; i < end; i++)
        {
            _ = this[i].ShortPreview;
            if (this[i].IsEvicted) evictedCount++;
            if (this[i].MatchLine.Length == 0) emptyMatchCount++;
            batch.Add(this[i]);
        }
        if (evictedCount > 0 || emptyMatchCount > 0)
        {
            Services.LogService.Instance.Info("FileGroup",
                $"ShowMore: file='{System.IO.Path.GetFileName(FilePath)}', start={start}, end={end}, " +
                $"batchSize={batch.Count}, stillEvicted={evictedCount}, emptyMatchLine={emptyMatchCount}");
        }
        VisibleResults.AddRange(batch);
        NotifyMoreStateChanged();
        return batch.Count;
    }

    public void ShowAll()
    {
        int start = VisibleResults.Count;
        var batch = new List<SearchResult>(Count - start);
        for (int i = start; i < Count; i++)
        {
            _ = this[i].ShortPreview;
            batch.Add(this[i]);
        }
        VisibleResults.AddRange(batch);
        NotifyMoreStateChanged();
    }

    private void NotifyMoreStateChanged()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasMore)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(RemainingCount)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowMoreText)));
    }

    public int MatchCount => Count + HiddenMatchCount + _evictedOnlyCount;

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
                // Iter 16: hydrate compact stubs into real SearchResult instances BEFORE
                // any code that indexes into Items (ShowMore, selection, preview) runs.
                if (value)
                    MaterializeEvictedStubs();
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
        // Iter 16: SelectAll on a collapsed evicted-only group needs the SearchResult
        // instances to actually exist before we can flip IsSelected on them.
        MaterializeEvictedStubs();
        foreach (var r in this) r.IsSelected = true;
        AllSelected = true;
        NotifySelectionChanged();
    }

    public void DeselectAll()
    {
        MaterializeEvictedStubs();
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
