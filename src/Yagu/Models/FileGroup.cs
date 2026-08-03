using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Logging;
using System.Globalization;

namespace Yagu.Models;

/// <summary>
/// Group of <see cref="SearchResult"/> instances that share the same file path.
/// Used as the grouping key in the results ListView.
/// </summary>
public sealed class FileGroup : ObservableCollection<SearchResult>
{
    public const int PageSize = 200;
    public const string NoExtensionLabel = "No extension";
    private const int HiddenNotificationInterval = PageSize;

    /// <summary>Count of evicted results that were not retained in the collection (disk-only).</summary>
    private int _evictedOnlyCount;

    /// <summary>
    /// Compact record for an evicted match. FilePath is implicit (held once on
    /// <see cref="FilePath"/>). Stubs are varint-encoded into byte pages while the group is
    /// collapsed and materialized into <see cref="Items"/> on demand when the group expands.
    /// </summary>
    internal readonly record struct EvictedStub(int LineNumber, int MatchStartColumn, int MatchLength, int SourceMatchStartColumn, long DiskOffset);

    /// <summary>
    /// Compact paged stub storage for collapsed groups. Byte pages stay below the LOH threshold
    /// and varint encoding avoids retaining a padded struct for every disk-only match.
    /// </summary>
    private const int MinEvictedStubPageBytes = 128;
    private const int MaxEvictedStubPageBytes = 64 * 1024;
    private const int MaxEncodedEvictedStubBytes = 30;
    private List<byte[]>? _evictedStubPages;
    private List<int>? _evictedStubPageLengths;
    private int _nextEvictedStubPageBytes = MinEvictedStubPageBytes;
    private int _evictedStubPageOffset;
    private int _evictedStubCount;
    private int _evictedStubReadPageIndex;
    private int _evictedStubReadPageOffset;
    private int _evictedStubReadIndex;
    private int _nextEvictedStubIndex;

    /// <summary>
    /// Sparse span sidecar for cross-line (multiline, Phase 1b) evicted stubs, keyed by stub
    /// add-order index (which equals decode order). Null unless a multiline stub is added, so the
    /// overwhelmingly common single-line eviction path keeps its exact varint byte layout with
    /// zero size regression. Populated from a multiline <see cref="SearchResult"/> at stub time
    /// and re-applied when the stub is materialized (<see cref="MaterializeEvictedStubs"/>) or
    /// snapshotted (<see cref="AppendPreviewSnapshotFromEvictedStubs"/>).
    /// </summary>
    private Dictionary<int, (int EndLine, int EndColumn)>? _evictedStubSpans;

    /// <summary>
    /// Optional per-file hard cap on stored matches. Default <see cref="int.MaxValue"/>
    /// (effectively unlimited). When set to a finite value, matches beyond the cap are
    /// dropped and counted in <see cref="HiddenMatchCount"/>. Bound from
    /// <c>AppSettings.MaxMatchesPerFile</c> at app startup and on settings change
    /// (0 in settings = unlimited / <see cref="int.MaxValue"/> here).
    /// </summary>
    public static int MaxMatchesPerGroup { get; set; } = int.MaxValue;

    public string FilePath { get; }
    private bool _selectFutureResults;

    /// <summary>
    /// Content-index candidacy provenance for this file (plan §6.2), set once by the view-model when the
    /// index participated in the search. Null when provenance is not shown (feature/setting off or the
    /// index did not participate). Match content is always read live regardless of this value.
    /// </summary>
    private IndexProvenanceKind? _provenance;
    public IndexProvenanceKind? Provenance
    {
        get => _provenance;
        set
        {
            if (_provenance == value)
                return;
            _provenance = value;
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsIndexAccelerated)));
        }
    }

    /// <summary>True when the content index selected this file as a candidate (drives the results-list
    /// "index-accelerated" badge). False/absent files show no badge — a live scan is the default.</summary>
    public bool IsIndexAccelerated => _provenance == IndexProvenanceKind.IndexAccelerated;

    /// <summary>Number of matches that were dropped due to <see cref="MaxMatchesPerGroup"/>.</summary>
    public int HiddenMatchCount { get; private set; }
    public bool HasHiddenMatches => HiddenMatchCount > 0;

    /// <summary>Number of filename-only matches (LineNumber == 0) in this group.</summary>
    private int _fileNameMatchCount;
    private int _storedFileNameMatchCount;

    /// <summary>True when this group has content matches (LineNumber &gt; 0) alongside filename matches.</summary>
    public bool HasContentMatches => (Count + HiddenMatchCount + _evictedOnlyCount) > _fileNameMatchCount;

    /// <summary>True when this group has at least one filename match — a result with LineNumber == 0,
    /// i.e. the file's NAME matched the query. Independent of <see cref="HasContentMatches"/>: a group
    /// can have a name match only, content matches only, or both.</summary>
    public bool HasFileNameMatch => _fileNameMatchCount > 0;

    /// <summary>True when the header represents the complete result and no content drawer exists.</summary>
    public bool IsFileNameOnlyMatch => HasFileNameMatch && !HasContentMatches;

    /// <summary>True when this group represents a file inside an archive.</summary>
    public bool IsArchiveEntry => ZipArchiveSearcher.IsArchivePath(FilePath);

    /// <summary>
    /// Display-friendly file name. For archive entries shows e.g. "archive.zip?/entry.txt".
    /// </summary>
    public string FileName
    {
        get
        {
            if (!IsArchiveEntry) return Path.GetFileName(FilePath);
            var (archivePath, entryPath) = ZipArchiveSearcher.SplitArchivePath(FilePath);
            return $"{Path.GetFileName(archivePath)}{ZipArchiveSearcher.ArchiveSeparator}/{entryPath}";
        }
    }

    /// <summary>
    /// Directory containing the outermost archive (or the file itself for non-archive paths).
    /// </summary>
    public string DirectoryName
    {
        get
        {
            if (!IsArchiveEntry) return Path.GetDirectoryName(FilePath) ?? string.Empty;
            var (archivePath, _) = ZipArchiveSearcher.SplitArchivePath(FilePath);
            return Path.GetDirectoryName(archivePath) ?? string.Empty;
        }
    }

    public string Extension
    {
        get
        {
            int dotIndex = FilePath.LastIndexOf('.');
            if (dotIndex < 0 || dotIndex == FilePath.Length - 1)
                return NoExtensionLabel;

            var extension = FilePath[(dotIndex + 1)..];
            return extension.IndexOfAny(['\\', '/', '?']) >= 0
                ? NoExtensionLabel
                : extension.ToLowerInvariant();
        }
    }

    /// <summary>Subset of items currently rendered in the UI. Populated lazily when the group expands.</summary>
    public BatchObservableCollection<SearchResult> VisibleResults { get; } = new();
    private int _visibleSkipped; // filename matches skipped during ShowMore/ShowAll

    // Running state for trimming overlapping context windows in the file list (see
    // SearchResult.SetContextTrim). Maintained in display (ascending) order as rows are appended so
    // each row hides context lines already shown by an earlier row or owned by the next match.
    private int _lastVisibleLine;
    private SearchResult? _trimPrevResult;
    private int _trimPrevFloor;

    /// <summary>
    /// Registers a result being appended to <see cref="VisibleResults"/> (in display order) and trims
    /// its context window so the file list never repeats a line number already shown by an earlier row
    /// or owned by the next match's line. Call once per result with <c>LineNumber &gt; 0</c>, in the
    /// order rows are rendered.
    /// </summary>
    private void RegisterVisibleForTrim(SearchResult item)
    {
        if (item.LineNumber <= 0)
            return;

        if (_trimPrevResult is not null)
        {
            // The previous row's trailing context must stop before this match's line.
            int prevCeiling = item.LineNumber;
            _trimPrevResult.SetContextTrim(_trimPrevFloor, prevCeiling);
            // A cross-line (multiline) match occupies lines Start..End; its after-context is numbered
            // from the END line, so the last visible line accounts for the span end (single-line
            // results have MatchEndLineNumber null, so this reduces to LineNumber).
            int prevEndLine = _trimPrevResult.MatchEndLineNumber ?? _trimPrevResult.LineNumber;
            int prevAfterMax = Math.Min(prevEndLine + _trimPrevResult.ContextAfter.Count, prevCeiling - 1);
            _lastVisibleLine = Math.Max(_lastVisibleLine, Math.Max(prevEndLine, prevAfterMax));
        }

        int floor = _lastVisibleLine;
        // Ceiling is finalized to the next match's line when the following row registers; until then
        // the (currently last) row shows its full trailing context.
        item.SetContextTrim(floor, int.MaxValue);
        _lastVisibleLine = Math.Max(_lastVisibleLine, item.MatchEndLineNumber ?? item.LineNumber);
        _trimPrevResult = item;
        _trimPrevFloor = floor;
    }

    private void ResetVisibleTrim()
    {
        _lastVisibleLine = 0;
        _trimPrevResult = null;
        _trimPrevFloor = 0;
    }

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
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_hasContentMatchesChanged = new(nameof(HasContentMatches));
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_matchCountChanged = new(nameof(MatchCount));
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_hasFileNameMatchChanged = new(nameof(HasFileNameMatch));
    private static readonly System.ComponentModel.PropertyChangedEventArgs s_isFileNameOnlyMatchChanged = new(nameof(IsFileNameOnlyMatch));

    protected override void InsertItem(int index, SearchResult item)
    {
        bool hadContentMatches = HasContentMatches;
        ApplySelectionIntent(item);

        if (item.LineNumber == 0 && _fileNameMatchCount++ == 0)
        {
            OnPropertyChanged(s_hasFileNameMatchChanged);
            OnPropertyChanged(s_isFileNameOnlyMatchChanged);
        }

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
            NotifyContentMatchStateIfChanged(hadContentMatches);
            return;
        }

        if (item.LineNumber == 0)
            _storedFileNameMatchCount++;

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
                // Phase 1b: preserve a cross-line span through the compact stub. The sidecar is
                // keyed by the stub's add-order index (== _evictedStubCount before AddEvictedStub
                // increments it), so a single-line stub adds nothing to the sidecar or the stream.
                int stubIndex = AddEvictedStub(new EvictedStub(item.LineNumber, item.MatchStartColumn, item.MatchLength, item.SourceMatchStartColumn, item.DiskOffset));
                if (item.MatchEndLineNumber is int stubEndLine)
                    (_evictedStubSpans ??= [])[stubIndex] = (stubEndLine, item.MatchEndColumn ?? 0);
                _evictedOnlyCount++;
                int totalStub = Items.Count + _evictedOnlyCount;
                bool notifyStub = totalStub == PageSize + 1
                              || (totalStub % HiddenNotificationInterval) == 0;
                if (notifyStub)
                {
                    OnPropertyChanged(s_countChanged);
                    OnPropertyChanged(s_indexerChanged);
                    NotifyMoreStateChanged();
                    OnPropertyChanged(s_matchCountChanged);
                }
                NotifyContentMatchStateIfChanged(hadContentMatches);
                return;
            }

            Items.Insert(index, item);
            if (item.IsSelected)
                NotifySelectedCountChanged();
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
            NotifyContentMatchStateIfChanged(hadContentMatches);
            return;
        }

        base.InsertItem(index, item);
        NotifyContentMatchStateIfChanged(hadContentMatches);
        if (item.IsSelected)
            NotifySelectedCountChanged();
    }

    internal void AddSourceBackedMatch(int lineNumber, int matchStartColumn, int matchLength, int sourceMatchStartColumn)
    {
        if (_isExpanded)
        {
            Add(SearchResult.CreateSourceBacked(FilePath, lineNumber, matchStartColumn, matchLength, sourceMatchStartColumn));
            return;
        }

        bool hadContentMatches = HasContentMatches;

        if (lineNumber == 0 && _fileNameMatchCount++ == 0)
        {
            OnPropertyChanged(s_hasFileNameMatchChanged);
            OnPropertyChanged(s_isFileNameOnlyMatchChanged);
        }

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
            NotifyContentMatchStateIfChanged(hadContentMatches);
            return;
        }

        if (lineNumber == 0)
            _storedFileNameMatchCount++;

        AddEvictedStub(new EvictedStub(
            lineNumber,
            matchStartColumn,
            matchLength,
            sourceMatchStartColumn,
            SearchResult.SourceBackedOffset));
        _evictedOnlyCount++;

        int totalStub = Items.Count + _evictedOnlyCount;
        bool notifyStub = totalStub == PageSize + 1
                      || (totalStub % HiddenNotificationInterval) == 0;
        if (notifyStub)
        {
            OnPropertyChanged(s_countChanged);
            OnPropertyChanged(s_indexerChanged);
            NotifyMoreStateChanged();
            OnPropertyChanged(s_matchCountChanged);
        }
        NotifyContentMatchStateIfChanged(hadContentMatches);
    }

    internal void AddSourceBackedMatches(IReadOnlyList<SourceBackedMatch> results, int start, int count)
    {
        if (count <= 0)
            return;

        if (_isExpanded)
        {
            for (int i = 0; i < count; i++)
            {
                var result = results[start + i];
                Add(SearchResult.CreateSourceBacked(
                    FilePath,
                    result.LineNumber,
                    result.MatchStartColumn,
                    result.MatchLength,
                    result.SourceMatchStartColumn));
            }
            return;
        }

        bool hadContentMatches = HasContentMatches;
        int hiddenBefore = HiddenMatchCount;
        int addedStubs = 0;

        for (int i = 0; i < count; i++)
        {
            var result = results[start + i];
            if (result.LineNumber == 0 && _fileNameMatchCount++ == 0)
            {
                OnPropertyChanged(s_hasFileNameMatchChanged);
                OnPropertyChanged(s_isFileNameOnlyMatchChanged);
            }

            if (TotalStoredCount >= MaxMatchesPerGroup)
            {
                HiddenMatchCount++;
                continue;
            }

            if (result.LineNumber == 0)
                _storedFileNameMatchCount++;

            AddEvictedStub(new EvictedStub(
                result.LineNumber,
                result.MatchStartColumn,
                result.MatchLength,
                result.SourceMatchStartColumn,
                SearchResult.SourceBackedOffset));
            _evictedOnlyCount++;
            addedStubs++;
        }

        if (HiddenMatchCount != hiddenBefore)
        {
            if (hiddenBefore == 0)
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasHiddenMatches)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HiddenMatchCount)));
            OnPropertyChanged(s_matchCountChanged);
        }

        if (addedStubs > 0)
        {
            OnPropertyChanged(s_countChanged);
            OnPropertyChanged(s_indexerChanged);
            NotifyMoreStateChanged();
            OnPropertyChanged(s_matchCountChanged);
        }

        NotifyContentMatchStateIfChanged(hadContentMatches);
    }

    private void NotifyContentMatchStateIfChanged(bool hadContentMatches)
    {
        if (hadContentMatches == HasContentMatches)
            return;

        OnPropertyChanged(s_hasContentMatchesChanged);
        OnPropertyChanged(s_isFileNameOnlyMatchChanged);
        OnPropertyChanged(s_matchCountChanged);
    }

    private void ApplySelectionIntent(SearchResult item)
    {
        if (_selectFutureResults || _allSelected)
            item.IsSelected = true;
    }

    private int AddEvictedStub(EvictedStub stub)
    {
        if (_evictedStubPages is null || _evictedStubPages[^1].Length - _evictedStubPageOffset < MaxEncodedEvictedStubBytes)
        {
            (_evictedStubPages ??= []).Add(new byte[_nextEvictedStubPageBytes]);
            (_evictedStubPageLengths ??= []).Add(0);
            _evictedStubPageOffset = 0;
            if (_nextEvictedStubPageBytes < MaxEvictedStubPageBytes)
                _nextEvictedStubPageBytes = Math.Min(MaxEvictedStubPageBytes, _nextEvictedStubPageBytes * 2);
        }

        var page = _evictedStubPages[^1];
        int written = WriteEvictedStub(page.AsSpan(_evictedStubPageOffset), stub);
        _evictedStubPageOffset += written;
        _evictedStubPageLengths![^1] = _evictedStubPageOffset;
        _evictedStubCount++;
        return _nextEvictedStubIndex++;
    }

    private static int WriteEvictedStub(Span<byte> destination, EvictedStub stub)
    {
        int offset = 0;
        offset += WriteVarUInt(destination[offset..], EncodeSignedVarInt(stub.LineNumber));
        offset += WriteVarUInt(destination[offset..], EncodeSignedVarInt(stub.MatchStartColumn));
        offset += WriteVarUInt(destination[offset..], EncodeSignedVarInt(stub.MatchLength));
        offset += WriteVarUInt(destination[offset..], EncodeSignedVarInt(stub.SourceMatchStartColumn));
        offset += WriteVarULong(destination[offset..], (ulong)stub.DiskOffset);
        return offset;
    }

    private static EvictedStub ReadEvictedStub(ReadOnlySpan<byte> source, ref int offset)
    {
        int lineNumber = DecodeSignedVarInt(ReadVarUInt(source, ref offset));
        int matchStartColumn = DecodeSignedVarInt(ReadVarUInt(source, ref offset));
        int matchLength = DecodeSignedVarInt(ReadVarUInt(source, ref offset));
        int sourceMatchStartColumn = DecodeSignedVarInt(ReadVarUInt(source, ref offset));
        long diskOffset = (long)ReadVarULong(source, ref offset);
        return new EvictedStub(lineNumber, matchStartColumn, matchLength, sourceMatchStartColumn, diskOffset);
    }

    private static uint EncodeSignedVarInt(int value) => (uint)((value << 1) ^ (value >> 31));

    private static int DecodeSignedVarInt(uint value) => (int)((value >> 1) ^ (uint)-(int)(value & 1));

    private static int WriteVarUInt(Span<byte> destination, uint value)
    {
        int written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        destination[written++] = (byte)value;
        return written;
    }

    private static int WriteVarULong(Span<byte> destination, ulong value)
    {
        int written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        destination[written++] = (byte)value;
        return written;
    }

    private static uint ReadVarUInt(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = 0;
        int shift = 0;
        while (true)
        {
            byte b = source[offset++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    private static ulong ReadVarULong(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte b = source[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    /// <summary>
    /// Materializes at most <paramref name="maxItems"/> compact <see cref="EvictedStub"/> entries into
    /// real <see cref="SearchResult"/> instances stored in <see cref="Items"/>. Unread encoded pages stay
    /// compact for subsequent Show-more calls, so opening a drawer never allocates every match merely to
    /// display the first page. The parameterless call preserves the full-materialization contract needed
    /// by session/export operations.
    /// </summary>
    public int MaterializeEvictedStubs(int maxItems = int.MaxValue)
    {
        var pages = _evictedStubPages;
        if (pages is null)
            return 0;

        var pageLengths = _evictedStubPageLengths!;
        var spans = _evictedStubSpans;
        int requested = Math.Min(Math.Max(0, maxItems), _evictedStubCount);
        if (requested == 0)
            return 0;
        if (Items is List<SearchResult> list)
            list.EnsureCapacity(list.Count + requested);

        int materialized = 0;
        while (materialized < requested && _evictedStubReadPageIndex < pages.Count)
        {
            int pageIndex = _evictedStubReadPageIndex;
            int pageLength = pageLengths[pageIndex];
            if (_evictedStubReadPageOffset >= pageLength)
            {
                // This page will never be read or written again (writers append only to the last page).
                if (pageIndex < pages.Count - 1)
                    pages[pageIndex] = Array.Empty<byte>();
                _evictedStubReadPageIndex++;
                _evictedStubReadPageOffset = 0;
                continue;
            }

            ReadOnlySpan<byte> page = pages[pageIndex].AsSpan(0, pageLength);
            int offset = _evictedStubReadPageOffset;
            EvictedStub stub = ReadEvictedStub(page, ref offset);
            _evictedStubReadPageOffset = offset;

            var result = stub.DiskOffset == SearchResult.SourceBackedOffset
                ? SearchResult.CreateSourceBacked(FilePath, stub.LineNumber, stub.MatchStartColumn, stub.MatchLength, stub.SourceMatchStartColumn)
                : SearchResult.CreatePreEvicted(FilePath, stub.LineNumber, stub.MatchStartColumn, stub.MatchLength, stub.DiskOffset, stub.SourceMatchStartColumn);
            int stubIndex = _evictedStubReadIndex++;
            if (spans is not null && spans.Remove(stubIndex, out var span))
            {
                result.MatchEndLineNumber = span.EndLine;
                result.MatchEndColumn = span.EndColumn;
            }
            ApplySelectionIntent(result);
            Items.Add(result);
            materialized++;
        }

        _evictedStubCount -= materialized;
        _evictedOnlyCount -= materialized;
        if (_evictedStubCount == 0)
        {
            _evictedStubPages = null;
            _evictedStubPageLengths = null;
            _evictedStubSpans = null;
            _nextEvictedStubPageBytes = MinEvictedStubPageBytes;
            _evictedStubPageOffset = 0;
            _evictedStubReadPageIndex = 0;
            _evictedStubReadPageOffset = 0;
            _evictedStubReadIndex = 0;
            _nextEvictedStubIndex = 0;
        }

        OnPropertyChanged(s_countChanged);
        OnPropertyChanged(s_indexerChanged);
        NotifyMoreStateChanged();
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MatchCount)));
        if (_selectFutureResults || _allSelected)
            NotifySelectedCountChanged();
        return materialized;
    }

    /// <summary>
    /// Release all held data so the SearchResult strings can be GC'd even if this
    /// FileGroup is temporarily kept alive by a pending metadata task or UI binding.
    /// </summary>
    public void Cleanup()
    {
        _cleaned = true;
        _evictedOnlyCount = 0;
        _fileNameMatchCount = 0;
        _storedFileNameMatchCount = 0;
        _visibleSkipped = 0;
        _evictedStubPages = null;
        _evictedStubPageLengths = null;
        _evictedStubSpans = null;
        _nextEvictedStubPageBytes = MinEvictedStubPageBytes;
        _evictedStubPageOffset = 0;
        _evictedStubCount = 0;
        _evictedStubReadPageIndex = 0;
        _evictedStubReadPageOffset = 0;
        _evictedStubReadIndex = 0;
        _nextEvictedStubIndex = 0;
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
                // Never render the filename-match ("0" line) row: a file-name match is conveyed by the
                // header "file name" pill, so a filename-only group shows no rows (and is not expandable).
                bool skipFileNameMatches = HasFileNameMatch;
                if (skipFileNameMatches)
                {
                    // Remove any filename matches that were added before content matches arrived.
                    for (int i = VisibleResults.Count - 1; i >= 0; i--)
                    {
                        if (VisibleResults[i].LineNumber == 0)
                        {
                            VisibleResults.RemoveAt(i);
                            _visibleSkipped++;
                        }
                    }
                }
                foreach (SearchResult item in e.NewItems)
                {
                    if (skipFileNameMatches && item.LineNumber == 0)
                    {
                        _visibleSkipped++;
                        continue;
                    }
                    if (VisibleResults.Count < PageSize && !item.IsEvicted)
                    {
                        _ = item.ShortPreview;
                        RegisterVisibleForTrim(item);
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
            ResetVisibleTrim();
            NotifyMoreStateChanged();
        }
    }

    // Iter 16: include unmaterialized evicted stubs in the "more" calculation so the
    // expand-evicted code path always finds work to do (and the UI's "Show more" text
    // reflects the true remaining count, not just the materialized Items count).
    public bool HasMore => RemainingCount > 0;
    public int RemainingCount => Math.Max(
        0,
        (Count + _evictedOnlyCount) - _storedFileNameMatchCount - VisibleResults.Count);
    public string ShowMoreText => $"Show more ({RemainingCount:N0} remaining)";

    public void ClearVisibleResults()
    {
        if (VisibleResults.Count == 0 && _visibleSkipped == 0) return;
        VisibleResults.Clear();
        _visibleSkipped = 0;
        ResetVisibleTrim();
        NotifyMoreStateChanged();
    }

    public int ShowMore(int maxItems = PageSize)
    {
        if (maxItems <= 0)
            return 0;

        int start = VisibleResults.Count + _visibleSkipped;
        int end = Math.Min(Count, start + maxItems);
        if (end <= start)
        {
            NotifyMoreStateChanged();
            return 0;
        }

        bool skipFileNameMatches = HasFileNameMatch; // filename-match rows are shown via the header pill, not as a "0" line
        var batch = new List<SearchResult>(end - start);
        int evictedCount = 0;
        int emptyMatchCount = 0;
        for (int i = start; i < end; i++)
        {
            if (skipFileNameMatches && this[i].LineNumber == 0)
            {
                _visibleSkipped++;
                continue;
            }
            _ = this[i].ShortPreview;
            if (this[i].IsEvicted) evictedCount++;
            if (this[i].MatchLine.Length == 0) emptyMatchCount++;
            RegisterVisibleForTrim(this[i]);
            batch.Add(this[i]);
        }
        if (evictedCount > 0 || emptyMatchCount > 0)
        {
            YaguLog.For("FileGroup").LogInformation(
                "ShowMore: file='{File}', start={Start}, end={End}, " +
                "batchSize={BatchSize}, stillEvicted={StillEvicted}, emptyMatchLine={EmptyMatchLine}",
                Path.GetFileName(FilePath), start, end, batch.Count, evictedCount, emptyMatchCount);
        }
        VisibleResults.AppendRange(batch);
        NotifyMoreStateChanged();
        return batch.Count;
    }

    public void ShowAll()
    {
        int start = VisibleResults.Count + _visibleSkipped;
        bool skipFileNameMatches = HasFileNameMatch; // filename-match rows are shown via the header pill, not as a "0" line
        var batch = new List<SearchResult>(Count - start);
        for (int i = start; i < Count; i++)
        {
            if (skipFileNameMatches && this[i].LineNumber == 0)
            {
                _visibleSkipped++;
                continue;
            }
            _ = this[i].ShortPreview;
            RegisterVisibleForTrim(this[i]);
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

    public int MatchCount
    {
        get
        {
            int total = Count + HiddenMatchCount + _evictedOnlyCount;
            // Don't count filename matches when there are also content matches.
            if (_fileNameMatchCount > 0 && HasContentMatches)
                total -= _fileNameMatchCount;
            return total;
        }
    }

    public long FileSize { get; private set; }
    public DateTime LastModified { get; private set; }
    public DateTime Created { get; private set; }
    public DateTime ModifiedOrCreated => LaterOf(LastModified, Created);
    public string FormattedSize => FormatSize(FileSize);
    public string FormattedDate => LastModified == default ? string.Empty : LastModified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

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
            var fi = new FileInfo(physicalPath);
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
        catch (Exception ex) { YaguLog.For("FileGroup").LogDebug(ex, "Cannot load metadata for {FilePath}", FilePath); }
    }

    /// <summary>
    /// Load file metadata on a worker thread and dispatch the resulting property
    /// notifications back to the UI. Avoids stalling the UI dispatcher with one
    /// FileInfo syscall per result group on huge searches. The <paramref name="dispatch"/>
    /// delegate is responsible for marshalling its action onto the UI thread.
    /// </summary>
    public void BeginLoadMetadata(Action<Action> dispatch, Action<FileGroup>? metadataApplied = null, CancellationToken cancellationToken = default)
    {
        // For archive entries, resolve to the outermost file on disk
        string physicalPath = IsArchiveEntry ? ZipArchiveSearcher.SplitArchivePath(FilePath).ArchivePath : FilePath;

        if (FileMetadataCache.TryGet(physicalPath, out var cached))
        {
            ApplyMetadata(cached.Length, cached.LastModified, cached.Created);
            metadataApplied?.Invoke(this);
            return;
        }

        _ = Task.Run(() =>
        {
            if (_cleaned || cancellationToken.IsCancellationRequested) return;
            bool hasCached = FileMetadataCache.TryGet(physicalPath, out var cachedMetadata);
            long size = hasCached ? cachedMetadata.Length : 0;
            DateTime modified = hasCached ? cachedMetadata.LastModified : default;
            DateTime created = hasCached ? cachedMetadata.Created : default;
            try
            {
                var fi = new FileInfo(physicalPath);
                if (fi.Exists)
                {
                    size = fi.Length;
                    modified = fi.LastWriteTime;
                    created = fi.CreationTime;
                    FileMetadataCache.Set(physicalPath, new FileMetadata(size, modified, created));
                }
            }
            catch (Exception ex) { YaguLog.For("FileGroup").LogDebug(ex, "Cannot load metadata for {FilePath}", FilePath); return; }

            if (_cleaned || cancellationToken.IsCancellationRequested) return;
            dispatch(() =>
            {
                if (!_cleaned)
                {
                    ApplyMetadata(size, modified, created);
                    metadataApplied?.Invoke(this);
                }
            });
        }, cancellationToken);
    }

    public void BeginLoadMetadata(Action<Action> dispatch, CancellationToken cancellationToken)
        => BeginLoadMetadata(dispatch, metadataApplied: null, cancellationToken);

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
                // Keep the expand/collapse state transition allocation-free. The UI materializes only
                // the compact-stub page it is about to render; full-data operations explicitly call
                // MaterializeEvictedStubs() with the default unlimited count.
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
            _selectFutureResults = value;
            // Always raise PropertyChanged to re-sync the TwoWay-bound CheckBox,
            // which can diverge from the model after user clicks.
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(AllSelected)));
        }
    }

    private int TotalStoredCount => Count + _evictedOnlyCount;

    public int SelectedCount => _allSelected ? TotalStoredCount : this.Count(r => r.IsSelected);
    public string SelectedCountText => $"{SelectedCount}/{TotalStoredCount} selected";

    public void SelectAll()
    {
        _selectFutureResults = true;
        foreach (var r in this) r.IsSelected = true;
        AllSelected = true;
        NotifySelectedCountChanged();
    }

    public void DeselectAll()
    {
        _selectFutureResults = false;
        foreach (var r in this) r.IsSelected = false;
        AllSelected = false;
        NotifySelectedCountChanged();
    }

    public void NotifySelectionChanged()
    {
        AllSelected = TotalStoredCount > 0
            && this.All(r => r.IsSelected)
            && (_evictedOnlyCount == 0 || _selectFutureResults);
        NotifySelectedCountChanged();
    }

    private void NotifySelectedCountChanged()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCount)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCountText)));
    }

    public IReadOnlyList<SearchResult> GetSelectedResults() => this.Where(r => r.IsSelected).ToList();

    public List<SearchResult> GetPreviewSnapshot(int maxResults)
    {
        if (maxResults <= 0)
            return [];

        var results = new List<SearchResult>(Math.Min(maxResults, TotalStoredCount));
        bool skipFileNameMatches = HasContentMatches;

        foreach (var result in this)
        {
            if (skipFileNameMatches && result.LineNumber == 0)
                continue;

            results.Add(result);
            if (results.Count >= maxResults)
                return results;
        }

        AppendPreviewSnapshotFromEvictedStubs(results, maxResults, skipFileNameMatches);
        return results;
    }

    private void AppendPreviewSnapshotFromEvictedStubs(List<SearchResult> results, int maxResults, bool skipFileNameMatches)
    {
        var pages = _evictedStubPages;
        if (pages is null)
            return;

        var pageLengths = _evictedStubPageLengths!;
        var spans = _evictedStubSpans;

        int remaining = _evictedStubCount;
        int stubIndex = _evictedStubReadIndex;
        for (int pageIndex = _evictedStubReadPageIndex; pageIndex < pages.Count && remaining > 0 && results.Count < maxResults; pageIndex++)
        {
            var page = pages[pageIndex].AsSpan(0, pageLengths[pageIndex]);
            int offset = pageIndex == _evictedStubReadPageIndex ? _evictedStubReadPageOffset : 0;
            while (remaining > 0 && offset < page.Length && results.Count < maxResults)
            {
                var stub = ReadEvictedStub(page, ref offset);
                int currentIndex = stubIndex++;
                remaining--;
                if (skipFileNameMatches && stub.LineNumber == 0)
                    continue;

                var result = stub.DiskOffset == SearchResult.SourceBackedOffset
                    ? SearchResult.CreateSourceBacked(
                        FilePath,
                        stub.LineNumber,
                        stub.MatchStartColumn,
                        stub.MatchLength,
                        stub.SourceMatchStartColumn)
                    : SearchResult.CreatePreEvicted(
                        FilePath,
                        stub.LineNumber,
                        stub.MatchStartColumn,
                        stub.MatchLength,
                        stub.DiskOffset,
                        stub.SourceMatchStartColumn);
                if (spans is not null && spans.TryGetValue(currentIndex, out var span))
                {
                    result.MatchEndLineNumber = span.EndLine;
                    result.MatchEndColumn = span.EndColumn;
                }
                ApplySelectionIntent(result);
                results.Add(result);
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }
}
