using System.ComponentModel;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for SearchResult eviction/hydration lifecycle, short preview generation,
/// and INotifyPropertyChanged behavior.
/// </summary>
public sealed class SearchResultEvictionTests : IDisposable
{
    private readonly ResultStore _store;

    public SearchResultEvictionTests()
    {
        _store = new ResultStore();
    }

    public void Dispose() => _store.Dispose();

    private static SearchResult Make(string matchLine = "the quick brown fox", int matchStart = 4, int matchLength = 5,
        string[]? before = null, string[]? after = null)
        => new("C:\\src\\file.cs", 10, matchLine, matchStart, matchLength,
            before ?? ["line before"], after ?? ["line after"]);

    [Fact]
    public void IsEvicted_InitiallyFalse()
    {
        var r = Make();
        Assert.False(r.IsEvicted);
    }

    [Fact]
    public void EvictWith_SetsIsEvictedTrue()
    {
        var r = Make();
        bool evicted = r.EvictWith(_store.Write);
        Assert.True(evicted);
        Assert.True(r.IsEvicted);
    }

    [Fact]
    public void EvictWith_ClearsContextData()
    {
        var r = Make(before: ["a", "b"], after: ["c"]);
        r.EvictWith(_store.Write);
        Assert.Empty(r.ContextBefore);
        Assert.Empty(r.ContextAfter);
    }

    [Fact]
    public void Hydrate_RestoresAllData()
    {
        var r = Make(matchLine: "hello world", before: ["pre1", "pre2"], after: ["post1"]);
        r.EvictWith(_store.Write);
        r.Hydrate(_store);
        Assert.False(r.IsEvicted);
        Assert.Equal("hello world", r.MatchLine);
        Assert.Equal(["pre1", "pre2"], r.ContextBefore);
        Assert.Equal(["post1"], r.ContextAfter);
    }

    [Fact]
    public void Hydrate_NotEvicted_DoesNothing()
    {
        var r = Make(matchLine: "original");
        r.Hydrate(_store); // Should not throw or change data
        Assert.Equal("original", r.MatchLine);
        Assert.False(r.IsEvicted);
    }

    [Fact]
    public void EvictWith_AlreadyEvicted_ReturnsFalse()
    {
        var r = Make();
        r.EvictWith(_store.Write);
        bool second = r.EvictWith(_store.Write);
        Assert.False(second);
    }

    [Fact]
    public void TryBeginEviction_OnlySucceedsOnce()
    {
        var r = Make();
        Assert.True(r.TryBeginEviction());
        Assert.False(r.TryBeginEviction());
    }

    [Fact]
    public void CancelEvictionReservation_AllowsReEviction()
    {
        var r = Make();
        r.TryBeginEviction();
        r.CancelEvictionReservation();
        Assert.False(r.IsEvicted);
        Assert.True(r.TryBeginEviction());
    }

    [Fact]
    public void CreatePreEvicted_CreatesAlreadyEvictedResult()
    {
        var r = SearchResult.CreatePreEvicted("C:\\test.cs", 5, 0, 3, 1024);
        Assert.True(r.IsEvicted);
        Assert.Equal(1024, r.DiskOffset);
        Assert.Equal(string.Empty, r.MatchLine);
    }

    [Fact]
    public void HydrateFrom_RestoresExternalData()
    {
        var r = SearchResult.CreatePreEvicted("C:\\test.cs", 5, 0, 3, 100);
        r.HydrateFrom("restored line", ["b1"], ["a1"]);
        Assert.False(r.IsEvicted);
        Assert.Equal("restored line", r.MatchLine);
        Assert.Equal(["b1"], r.ContextBefore);
        Assert.Equal(["a1"], r.ContextAfter);
    }

    [Fact]
    public void HydrateFrom_NotEvicted_DoesNothing()
    {
        var r = Make(matchLine: "keep this");
        r.HydrateFrom("overwrite", [], []);
        Assert.Equal("keep this", r.MatchLine);
    }

    [Fact]
    public void ShortPreview_ShortLine_ReturnsAsIs()
    {
        var r = Make(matchLine: "short line", matchStart: 0, matchLength: 5);
        Assert.Equal("short line", r.ShortPreview);
    }

    [Fact]
    public void ShortPreview_LongLine_TruncatesAroundMatch()
    {
        var longLine = new string('x', 200);
        var r = Make(matchLine: longLine, matchStart: 100, matchLength: 5);
        Assert.True(r.ShortPreview.Length <= 125); // 120 + ellipsis chars
        Assert.Contains("…", r.ShortPreview);
    }

    [Fact]
    public void ShortPreviewMatchStart_ValidIndex()
    {
        var longLine = new string('a', 50) + "NEEDLE" + new string('b', 100);
        var r = Make(matchLine: longLine, matchStart: 50, matchLength: 6);
        int start = r.ShortPreviewMatchStart;
        Assert.True(start >= 0);
        Assert.True(start < r.ShortPreview.Length);
    }

    [Fact]
    public void IsSelected_RaisesPropertyChanged()
    {
        var r = Make();
        string? changedProp = null;
        r.PropertyChanged += (_, e) => changedProp = e.PropertyName;
        r.IsSelected = true;
        Assert.Equal("IsSelected", changedProp);
    }

    [Fact]
    public void IsSelected_SameValue_DoesNotRaiseEvent()
    {
        var r = Make();
        r.IsSelected = false;
        int raised = 0;
        r.PropertyChanged += (_, _) => raised++;
        r.IsSelected = false;
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Hydrate_RaisesPropertyChanged_NumberedBeforeAndAfter()
    {
        var r = Make(before: ["a"], after: ["b"]);
        r.EvictWith(_store.Write);

        var changes = new List<string>();
        r.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);
        r.Hydrate(_store);

        Assert.Contains("NumberedBefore", changes);
        Assert.Contains("NumberedAfter", changes);
    }

    [Fact]
    public void NumberedBefore_ReturnsCorrectLineNumbers()
    {
        var r = new SearchResult("f.cs", 10, "match", 0, 5, ["a", "b", "c"], []);
        var numbered = r.NumberedBefore;
        Assert.Equal(3, numbered.Count);
        Assert.Equal(7, numbered[0].LineNum);
        Assert.Equal(8, numbered[1].LineNum);
        Assert.Equal(9, numbered[2].LineNum);
    }

    [Fact]
    public void NumberedAfter_ReturnsCorrectLineNumbers()
    {
        var r = new SearchResult("f.cs", 10, "match", 0, 5, [], ["x", "y"]);
        var numbered = r.NumberedAfter;
        Assert.Equal(2, numbered.Count);
        Assert.Equal(11, numbered[0].LineNum);
        Assert.Equal(12, numbered[1].LineNum);
    }

    [Fact]
    public void EvictWithLight_DoesNotMaterializePreview()
    {
        var r = Make(matchLine: "content to evict");
        bool evicted = r.EvictWithLight(_store.Write);
        Assert.True(evicted);
        Assert.True(r.IsEvicted);
        // MatchLine set to empty because preview was not materialized
        Assert.Equal(string.Empty, r.MatchLine);
    }

    [Fact]
    public void EnqueueEvict_DrainRoundTrip()
    {
        var r = Make(matchLine: "async eviction test", before: ["ctx"], after: ["ctx2"]);
        bool queued = _store.EnqueueEvict(r);
        Assert.True(queued);
        _store.Drain();
        Assert.True(r.IsEvicted);

        r.Hydrate(_store);
        Assert.Equal("async eviction test", r.MatchLine);
        Assert.Equal(["ctx"], r.ContextBefore);
        Assert.Equal(["ctx2"], r.ContextAfter);
    }

    [Fact]
    public void EnqueueEvictMany_EvictsAllResults()
    {
        var results = Enumerable.Range(0, 10)
            .Select(i => Make(matchLine: $"line {i}"))
            .ToList();

        int count = _store.EnqueueEvictMany(results);
        _store.Drain();

        Assert.Equal(10, count);
        Assert.All(results, r => Assert.True(r.IsEvicted));
    }

    [Fact]
    public void EvictManyNow_ImmediateEviction()
    {
        var results = Enumerable.Range(0, 5)
            .Select(i => Make(matchLine: $"now {i}", before: ["b"], after: ["a"]))
            .ToList();

        int count = _store.EvictManyNow(results);
        Assert.Equal(5, count);
        Assert.All(results, r => Assert.True(r.IsEvicted));
    }
}
