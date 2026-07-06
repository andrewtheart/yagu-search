using System.ComponentModel;
using System.Globalization;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.Models;

/// <summary>
/// A single line with its file line number, used for context display.
/// </summary>
public sealed record ContextLine(int LineNum, string Text);

/// <summary>
/// Compact metadata for a match whose payload can be re-read from the source file.
/// Used by degraded streaming searches to avoid constructing a SearchResult per hit.
/// </summary>
public readonly record struct SourceBackedMatch(
    string FilePath,
    int LineNumber,
    int MatchStartColumn,
    int MatchLength,
    int SourceMatchStartColumn);

/// <summary>
/// A single match found in a file.
/// </summary>
public sealed record SearchResult(
    string FilePath,
    int LineNumber,
    string MatchLine,
    int MatchStartColumn,
    int MatchLength,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> ContextAfter) : INotifyPropertyChanged
{
    // Override primary constructor properties with mutable versions to support disk eviction.
    public string MatchLine { get; internal set; } = MatchLine;
    public int MatchStartColumn { get; internal set; } = MatchStartColumn;
    public int MatchLength { get; internal set; } = MatchLength;
    public IReadOnlyList<string> ContextBefore { get; internal set; } = ContextBefore;
    public IReadOnlyList<string> ContextAfter { get; internal set; } = ContextAfter;
    internal int SourceMatchStartColumn { get; set; } = MatchStartColumn;

    /// <summary>
    /// End line (1-based) of a cross-line (multiline) match, or null for a single-line match.
    /// When non-null the match spans from <see cref="LineNumber"/> to this line inclusive.
    /// </summary>
    public int? MatchEndLineNumber { get; internal set; }

    /// <summary>
    /// End column (0-based, exclusive, UTF-16 source column) on <see cref="MatchEndLineNumber"/>
    /// for a cross-line match, or null for a single-line match. Same column space as
    /// <see cref="SourceMatchStartColumn"/>.
    /// </summary>
    public int? MatchEndColumn { get; internal set; }

    /// <summary>True when this result is a cross-line (multiline) span (i.e. carries an end line).</summary>
    public bool IsMultilineMatch => MatchEndLineNumber is not null;

    /// <summary>
    /// Compact results-row label for a cross-line (multiline) match indicating how many additional
    /// lines the span covers (e.g. <c>… (+2 lines)</c>), or empty for a single-line match. This is the
    /// Phase 1 "start line + marker" rendering; full cross-line highlighting is a later phase. Returns
    /// a plain string (no WinUI types) so an empty value renders as a zero-width row element.
    /// </summary>
    public string MultilineSpanLabel
    {
        get
        {
            if (MatchEndLineNumber is not int endLine || endLine <= LineNumber)
                return string.Empty;
            int extra = endLine - LineNumber;
            return string.Create(CultureInfo.InvariantCulture, $"\u2026 (+{extra} line{(extra == 1 ? "" : "s")})");
        }
    }

    private const int ShortPreviewLength = 120;
    private ShortPreviewInfo? _shortPreview;

    internal static Action<Action>? HydrationDispatcher { get; set; }

    /// <summary>Short preview kept in memory even when evicted, centered around the match when possible.
    /// Lazily created on first access to avoid allocating the string for non-visible items.</summary>
    public string ShortPreview => EnsureShortPreview().Text;

    /// <summary>Match start adjusted for <see cref="ShortPreview" />.</summary>
    public int ShortPreviewMatchStart => EnsureShortPreview().MatchStart;

    /// <summary>Gutter label for a match: just the 1-based line number (the column is shown on hover
    /// via <see cref="LineLocationTooltip"/> and is already implied by the match highlight). Keeping
    /// the column out of the chip avoids the wide, content-shoving pill that line:col produced.</summary>
    public string LineLocationDisplay => LineNumber.ToString(CultureInfo.InvariantCulture);

    public string LineLocationTooltip => LineNumber > 0
        ? string.Create(CultureInfo.InvariantCulture, $"Line {LineNumber}, column {GetOneBasedSourceColumn()}")
        : "File name match";

    private ShortPreviewInfo EnsureShortPreview()
        => _shortPreview ??= CreateShortPreview(MatchLine, MatchStartColumn, MatchLength);

    private ShortPreviewInfo? ExistingShortPreview => _shortPreview;

    private const long InMemoryOffset = -1;
    private const long EvictingOffset = -2;
    internal const long SourceBackedOffset = -3;
    private long _diskOffset = InMemoryOffset;

    /// <summary>Byte offset in the <see cref="ResultStore"/> temp file, or -1 if in memory.</summary>
    public long DiskOffset => Volatile.Read(ref _diskOffset);

    /// <summary>True when heavy data has been evicted to disk.</summary>
    public bool IsEvicted => DiskOffset >= 0 || IsSourceBacked;

    /// <summary>True when payload should be re-read from the original source file on demand.</summary>
    internal bool IsSourceBacked => DiskOffset == SourceBackedOffset;

    /// <summary>True when this result has been queued for eviction but has not been written yet.</summary>
    internal bool IsEvicting => DiskOffset == EvictingOffset;

    internal bool TryBeginEviction()
    {
        // Phase 1a multiline stopgap: cross-line (multiline) matches carry span metadata
        // (MatchEndLineNumber/MatchEndColumn) that the disk-eviction stub and session schema
        // do not yet persist (Phase 1b). Pin them in memory so a multiline match cannot collapse
        // to single-line under memory pressure before 1b threads spans through eviction.
        if (IsMultilineMatch)
            return false;
        return Interlocked.CompareExchange(ref _diskOffset, EvictingOffset, InMemoryOffset) == InMemoryOffset;
    }

    /// <summary>
    /// Creates a SearchResult that is already evicted to disk at <paramref name="diskOffset"/>.
    /// Used in degraded mode to avoid allocating full strings that will be immediately evicted.
    /// The result has empty MatchLine and contexts — FileGroup will not retain it visually.
    /// </summary>
    internal static SearchResult CreatePreEvicted(string filePath, int lineNumber, int matchStartColumn, int matchLength, long diskOffset, int? sourceMatchStartColumn = null)
    {
        var result = new SearchResult(
            FilePath: filePath,
            LineNumber: lineNumber,
            MatchLine: string.Empty,
            MatchStartColumn: matchStartColumn,
            MatchLength: matchLength,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());
        result.SourceMatchStartColumn = sourceMatchStartColumn ?? matchStartColumn;
        Volatile.Write(ref result._diskOffset, diskOffset);
        return result;
    }

    internal static SearchResult CreateSourceBacked(string filePath, int lineNumber, int matchStartColumn, int matchLength, int sourceMatchStartColumn)
    {
        var result = new SearchResult(
            FilePath: filePath,
            LineNumber: lineNumber,
            MatchLine: string.Empty,
            MatchStartColumn: matchStartColumn,
            MatchLength: matchLength,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());
        result.SourceMatchStartColumn = sourceMatchStartColumn;
        Volatile.Write(ref result._diskOffset, SourceBackedOffset);
        return result;
    }

    internal void CancelEvictionReservation()
    {
        _ = Interlocked.CompareExchange(ref _diskOffset, InMemoryOffset, EvictingOffset);
    }

    /// <summary>Evict heavy payload (match line + context) to disk, keeping only ShortPreview.</summary>
    public void Evict(ResultStore store)
    {
        EvictWith(store.Write);
    }

    /// <summary>
    /// Evict using a pre-acquired batch writer so the caller can amortize the
    /// <see cref="ResultStore"/> lock + flush across many results.
    /// </summary>
    internal bool EvictWith(Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long> writer)
    {
        if (!TryBeginEviction())
            return false;

        return CompleteReservedEvictionWith(writer, materializePreview: true);
    }

    /// <summary>
    /// Evict without materializing ShortPreview — saves ~266 bytes per result for items
    /// that won't be displayed (e.g. results in collapsed groups during degraded mode).
    /// MatchLine is set to string.Empty; ShortPreview is lazily recreated on hydration.
    /// </summary>
    internal bool EvictWithLight(Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long> writer)
    {
        if (!TryBeginEviction())
            return false;

        return CompleteReservedEvictionWith(writer, materializePreview: false);
    }

    internal bool CompleteReservedEvictionWith(Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long> writer)
        => CompleteReservedEvictionWith(writer, materializePreview: false);

    private bool CompleteReservedEvictionWith(
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long> writer,
        bool materializePreview)
    {
        if (DiskOffset != EvictingOffset)
            return false;

        try
        {
            var preview = materializePreview ? EnsureShortPreview() : ExistingShortPreview;
            long offset = writer(MatchLine, ContextBefore, ContextAfter);
            // Visible rows materialize ShortPreview before queued eviction; keep only
            // those previews. Non-visible rows are hydrated before paging into view,
            // so retaining a short string per hidden match would just recreate the
            // large result graph that disk paging is meant to avoid.
            MatchLine = preview?.Text ?? string.Empty;
            ContextBefore = Array.Empty<string>();
            ContextAfter = Array.Empty<string>();
            Volatile.Write(ref _diskOffset, offset);
            return true;
        }
        catch
        {
            Volatile.Write(ref _diskOffset, InMemoryOffset);
            throw;
        }
    }

    /// <summary>Restore full payload from disk.</summary>
    public void Hydrate(ResultStore store)
    {
        long offset = DiskOffset;
        if (offset < 0) return;

        var (ml, cb, ca) = store.Read(offset);
        MatchLine = ml;
        ContextBefore = cb;
        ContextAfter = ca;
        _shortPreview = null; // invalidate so lazy getter recreates from restored MatchLine
        Volatile.Write(ref _diskOffset, InMemoryOffset);

        // Notify UI so OneWay bindings on context lines refresh after hydration.
        RaiseHydrationPropertyChanged();
    }

    /// <summary>Restore full payload from pre-read data (batch hydration path).</summary>
    internal void HydrateFrom(string matchLine, IReadOnlyList<string> contextBefore, IReadOnlyList<string> contextAfter)
        => HydrateFrom(matchLine, contextBefore, contextAfter, MatchStartColumn, MatchLength, SourceMatchStartColumn);

    internal void HydrateFrom(
        string matchLine,
        IReadOnlyList<string> contextBefore,
        IReadOnlyList<string> contextAfter,
        int matchStartColumn,
        int matchLength,
        int sourceMatchStartColumn)
    {
        if (!IsEvicted) return;
        MatchLine = matchLine;
        MatchStartColumn = matchStartColumn;
        MatchLength = matchLength;
        SourceMatchStartColumn = sourceMatchStartColumn;
        ContextBefore = contextBefore;
        ContextAfter = contextAfter;
        _shortPreview = null;
        Volatile.Write(ref _diskOffset, InMemoryOffset);
        RaiseHydrationPropertyChanged();
    }

    private void RaiseHydrationPropertyChanged()
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        // x:Bind setters for NumberedBefore/NumberedAfter end up calling
        // ItemsControl.set_ItemsSource, which is a UI-thread-only operation.
        // If hydration runs on a worker thread (e.g. SaveSessionAsync or a
        // background prefetch), marshal back to the dispatcher first to avoid
        // 0x8001010E (RPC_E_WRONG_THREAD) and the subsequent native AV in WinUI.
        var dispatcher = HydrationDispatcher;
        if (dispatcher is null)
        {
            handler(this, LocationArgs);
            handler(this, LocationTooltipArgs);
            handler(this, BeforeArgs);
            handler(this, AfterArgs);
            return;
        }

        dispatcher(() =>
        {
            var h = PropertyChanged;
            if (h is null) return;
            h(this, LocationArgs);
            h(this, LocationTooltipArgs);
            h(this, BeforeArgs);
            h(this, AfterArgs);
        });
    }

    private int GetOneBasedSourceColumn()
        => Math.Max(0, SourceMatchStartColumn >= 0 ? SourceMatchStartColumn : MatchStartColumn) + 1;

    private static readonly PropertyChangedEventArgs LocationArgs = new(nameof(LineLocationDisplay));
    private static readonly PropertyChangedEventArgs LocationTooltipArgs = new(nameof(LineLocationTooltip));
    private static readonly PropertyChangedEventArgs BeforeArgs = new(nameof(NumberedBefore));
    private static readonly PropertyChangedEventArgs AfterArgs = new(nameof(NumberedAfter));

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static ShortPreviewInfo CreateShortPreview(string? line, int matchStart, int matchLength)
    {
        line ??= string.Empty;
        if (line.Length <= ShortPreviewLength)
            return new ShortPreviewInfo(line, matchStart);

        if (matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
            return new ShortPreviewInfo(line[..ShortPreviewLength] + LineTruncator.Ellipsis, matchStart);

        int safeMatchLength = Math.Min(matchLength, line.Length - matchStart);
        int visibleMatchLength = Math.Min(safeMatchLength, ShortPreviewLength);
        int contextChars = Math.Max(0, (ShortPreviewLength - visibleMatchLength) / 2);

        int start = Math.Max(0, matchStart - contextChars);
        int end = Math.Min(line.Length, start + ShortPreviewLength);
        if (end - start < ShortPreviewLength)
            start = Math.Max(0, end - ShortPreviewLength);

        bool hasPrefix = start > 0;
        bool hasSuffix = end < line.Length;
        string prefix = hasPrefix ? LineTruncator.Ellipsis : string.Empty;
        string suffix = hasSuffix ? LineTruncator.Ellipsis : string.Empty;
        string text = string.Concat(prefix, line.AsSpan(start, end - start), suffix);
        int displayMatchStart = matchStart - start + prefix.Length;

        return new ShortPreviewInfo(text, displayMatchStart);
    }

    private readonly record struct ShortPreviewInfo(string Text, int MatchStart);

    // Per-row context window trim. Adjacent matches in the same file group have overlapping
    // ±context windows; rendered independently they repeat the same line numbers in the file list.
    // FileGroup sets these (in display order) so each row hides context lines already shown by an
    // earlier row (<= floor) or owned by the next match (>= ceiling). Defaults are a no-op window,
    // so non-list consumers (CLI, preview) see the full stored context.
    private int _contextFloorLineExclusive;
    private int _contextCeilingLineExclusive = int.MaxValue;

    /// <summary>
    /// Restricts the context lines shown by <see cref="NumberedBefore"/>/<see cref="NumberedAfter"/>
    /// to the open interval (<paramref name="floorExclusive"/>, <paramref name="ceilingExclusive"/>),
    /// so adjacent results in a file group do not repeat each other's line numbers. Raises change
    /// notifications when the window changes.
    /// </summary>
    internal void SetContextTrim(int floorExclusive, int ceilingExclusive)
    {
        if (_contextFloorLineExclusive == floorExclusive && _contextCeilingLineExclusive == ceilingExclusive)
            return;
        _contextFloorLineExclusive = floorExclusive;
        _contextCeilingLineExclusive = ceilingExclusive;
        var handler = PropertyChanged;
        if (handler is null) return;
        handler(this, BeforeArgs);
        handler(this, AfterArgs);
    }

    public IReadOnlyList<ContextLine> NumberedBefore
    {
        get
        {
            int startLine = LineNumber - ContextBefore.Count;
            int floor = _contextFloorLineExclusive;
            int ceiling = _contextCeilingLineExclusive;
            var list = new List<ContextLine>(ContextBefore.Count);
            for (int i = 0; i < ContextBefore.Count; i++)
            {
                int lineNum = startLine + i;
                if (lineNum > floor && lineNum < ceiling)
                    list.Add(new ContextLine(lineNum, ContextBefore[i]));
            }
            return list;
        }
    }

    public IReadOnlyList<ContextLine> NumberedAfter
    {
        get
        {
            // After-context is numbered from the END line of the match (for a single-line
            // match MatchEndLineNumber is null, so this is LineNumber + 1 as before).
            int startLine = (MatchEndLineNumber ?? LineNumber) + 1;
            int floor = _contextFloorLineExclusive;
            int ceiling = _contextCeilingLineExclusive;
            var list = new List<ContextLine>(ContextAfter.Count);
            for (int i = 0; i < ContextAfter.Count; i++)
            {
                int lineNum = startLine + i;
                if (lineNum > floor && lineNum < ceiling)
                    list.Add(new ContextLine(lineNum, ContextAfter[i]));
            }
            return list;
        }
    }
}
