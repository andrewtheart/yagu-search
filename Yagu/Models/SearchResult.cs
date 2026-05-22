using System.ComponentModel;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.Models;

/// <summary>
/// A single line with its file line number, used for context display.
/// </summary>
public sealed record ContextLine(int LineNum, string Text);

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
    public IReadOnlyList<string> ContextBefore { get; internal set; } = ContextBefore;
    public IReadOnlyList<string> ContextAfter { get; internal set; } = ContextAfter;

    private const int ShortPreviewLength = 120;
    private ShortPreviewInfo? _shortPreview;

    /// <summary>Short preview kept in memory even when evicted, centered around the match when possible.
    /// Lazily created on first access to avoid allocating the string for non-visible items.</summary>
    public string ShortPreview => EnsureShortPreview().Text;

    /// <summary>Match start adjusted for <see cref="ShortPreview" />.</summary>
    public int ShortPreviewMatchStart => EnsureShortPreview().MatchStart;

    private ShortPreviewInfo EnsureShortPreview()
        => _shortPreview ??= CreateShortPreview(MatchLine, MatchStartColumn, MatchLength);

    private const long InMemoryOffset = -1;
    private const long EvictingOffset = -2;
    private long _diskOffset = InMemoryOffset;

    /// <summary>Byte offset in the <see cref="ResultStore"/> temp file, or -1 if in memory.</summary>
    public long DiskOffset => Volatile.Read(ref _diskOffset);

    /// <summary>True when heavy data has been evicted to disk.</summary>
    public bool IsEvicted => DiskOffset >= 0;

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
        if (Interlocked.CompareExchange(ref _diskOffset, EvictingOffset, InMemoryOffset) != InMemoryOffset)
            return false;

        try
        {
            long offset = writer(MatchLine, ContextBefore, ContextAfter);
            // Materialize the short preview before discarding MatchLine —
            // it's needed for display even while the full data is on disk.
            MatchLine = EnsureShortPreview().Text;
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NumberedBefore)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NumberedAfter)));
    }

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

    public IReadOnlyList<ContextLine> NumberedBefore
    {
        get
        {
            int startLine = LineNumber - ContextBefore.Count;
            var list = new ContextLine[ContextBefore.Count];
            for (int i = 0; i < ContextBefore.Count; i++)
                list[i] = new ContextLine(startLine + i, ContextBefore[i]);
            return list;
        }
    }

    public IReadOnlyList<ContextLine> NumberedAfter
    {
        get
        {
            int startLine = LineNumber + 1;
            var list = new ContextLine[ContextAfter.Count];
            for (int i = 0; i < ContextAfter.Count; i++)
                list[i] = new ContextLine(startLine + i, ContextAfter[i]);
            return list;
        }
    }
}
