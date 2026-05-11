using System.ComponentModel;
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

    /// <summary>Short preview kept in memory even when evicted (first ~120 chars).</summary>
    public string ShortPreview { get; } = MatchLine.Length <= 120 ? MatchLine : MatchLine[..120] + "…";

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
            MatchLine = ShortPreview;
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
        Volatile.Write(ref _diskOffset, InMemoryOffset);
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
