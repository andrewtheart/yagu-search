using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public class SearchResultTests
{
    [Fact]
    public void ShortPreview_ShortLine_ReturnsFullLine()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "short line", 0, 5, [], []);
        Assert.Equal("short line", r.ShortPreview);
        Assert.Equal(0, r.ShortPreviewMatchStart);
    }

    [Fact]
    public void ShortPreview_LongLine_TruncatesWithEllipsis()
    {
        string longLine = new string('x', 300);
        var r = new SearchResult(@"C:\f.cs", 1, longLine, 150, 10, [], []);
        Assert.Contains("…", r.ShortPreview);
        Assert.True(r.ShortPreview.Length < longLine.Length);
    }

    [Fact]
    public void ShortPreview_NegativeMatchStart_FallsBackToPrefix()
    {
        string longLine = new string('a', 200);
        var r = new SearchResult(@"C:\f.cs", 1, longLine, -1, 0, [], []);
        // Should not throw; just truncates from beginning
        Assert.NotNull(r.ShortPreview);
    }

    [Fact]
    public void ShortPreview_MatchStartBeyondLength_FallsBackToPrefix()
    {
        string longLine = new string('a', 200);
        var r = new SearchResult(@"C:\f.cs", 1, longLine, 999, 5, [], []);
        Assert.NotNull(r.ShortPreview);
    }

    [Fact]
    public void ShortPreview_NullMatchLine_ReturnsEmpty()
    {
        var r = new SearchResult(@"C:\f.cs", 1, null!, 0, 0, [], []);
        Assert.Equal(string.Empty, r.ShortPreview);
    }

    [Fact]
    public void IsEvicted_NewResult_IsFalse()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        Assert.False(r.IsEvicted);
        Assert.False(r.IsEvicting);
    }

    [Fact]
    public void TryBeginEviction_SucceedsOnce_FailsSubsequently()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        Assert.True(r.TryBeginEviction());
        Assert.True(r.IsEvicting);
        Assert.False(r.TryBeginEviction());
    }

    [Fact]
    public void CancelEvictionReservation_ResetsToInMemory()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        r.TryBeginEviction();
        Assert.True(r.IsEvicting);
        r.CancelEvictionReservation();
        Assert.False(r.IsEvicting);
        Assert.False(r.IsEvicted);
    }

    [Fact]
    public void CreatePreEvicted_SetsCorrectOffsetAndEmptyPayload()
    {
        var r = SearchResult.CreatePreEvicted(@"C:\f.cs", 42, 5, 10, diskOffset: 1234, sourceMatchStartColumn: 7);
        Assert.True(r.IsEvicted);
        Assert.Equal(1234, r.DiskOffset);
        Assert.Equal(@"C:\f.cs", r.FilePath);
        Assert.Equal(42, r.LineNumber);
        Assert.Equal(5, r.MatchStartColumn);
        Assert.Equal(10, r.MatchLength);
        Assert.Equal(string.Empty, r.MatchLine);
        Assert.Empty(r.ContextBefore);
        Assert.Empty(r.ContextAfter);
    }

    [Fact]
    public void EvictWith_WritesAndMarksEvicted()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "match text", 0, 5, ["before"], ["after"]);
        bool writerCalled = false;
        long FakeWriter(string ml, IReadOnlyList<string> cb, IReadOnlyList<string> ca)
        {
            writerCalled = true;
            Assert.Equal("match text", ml);
            Assert.Equal(["before"], cb);
            Assert.Equal(["after"], ca);
            return 100;
        }

        bool evicted = r.EvictWith(FakeWriter);

        Assert.True(evicted);
        Assert.True(writerCalled);
        Assert.True(r.IsEvicted);
        Assert.Equal(100, r.DiskOffset);
        // ShortPreview is materialized since EvictWith uses materializePreview=true
        Assert.NotNull(r.ShortPreview);
    }

    [Fact]
    public void EvictWithLight_EvictsWithoutMaterializingPreview()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "hello world", 0, 5, ["b"], ["a"]);
        long FakeWriter(string ml, IReadOnlyList<string> cb, IReadOnlyList<string> ca) => 200;

        bool evicted = r.EvictWithLight(FakeWriter);

        Assert.True(evicted);
        Assert.True(r.IsEvicted);
        Assert.Equal(200, r.DiskOffset);
    }

    [Fact]
    public void EvictWith_AlreadyEvicted_ReturnsFalse()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        r.EvictWith((_, _, _) => 50);
        Assert.False(r.EvictWith((_, _, _) => 99));
    }

    [Fact]
    public void Hydrate_RestoresPayload()
    {
        using var store = new ResultStore();
        var r = new SearchResult(@"C:\f.cs", 1, "match data", 2, 5, ["line1", "line2"], ["line3"]);
        r.Evict(store);
        Assert.True(r.IsEvicted);

        r.Hydrate(store);
        Assert.False(r.IsEvicted);
        Assert.Equal("match data", r.MatchLine);
        Assert.Equal(["line1", "line2"], r.ContextBefore);
        Assert.Equal(["line3"], r.ContextAfter);
    }

    [Fact]
    public void Hydrate_NotEvicted_NoOp()
    {
        using var store = new ResultStore();
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        r.Hydrate(store); // should not throw
        Assert.Equal("text", r.MatchLine);
    }

    [Fact]
    public void HydrateFrom_RestoresPayload()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "original", 0, 5, ["b"], ["a"]);
        r.EvictWith((_, _, _) => 42);
        Assert.True(r.IsEvicted);

        r.HydrateFrom("restored", ["rb"], ["ra"]);
        Assert.False(r.IsEvicted);
        Assert.Equal("restored", r.MatchLine);
        Assert.Equal(["rb"], r.ContextBefore);
        Assert.Equal(["ra"], r.ContextAfter);
    }

    [Fact]
    public void HydrateFrom_NotEvicted_NoOp()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        r.HydrateFrom("other", ["x"], ["y"]);
        Assert.Equal("text", r.MatchLine); // unchanged
    }

    [Fact]
    public void NumberedBefore_ComputesCorrectLineNumbers()
    {
        var r = new SearchResult(@"C:\f.cs", 10, "match", 0, 5, ["line1", "line2", "line3"], []);
        var numbered = r.NumberedBefore;
        Assert.Equal(3, numbered.Count);
        Assert.Equal(7, numbered[0].LineNum);
        Assert.Equal(8, numbered[1].LineNum);
        Assert.Equal(9, numbered[2].LineNum);
    }

    [Fact]
    public void NumberedAfter_ComputesCorrectLineNumbers()
    {
        var r = new SearchResult(@"C:\f.cs", 10, "match", 0, 5, [], ["a1", "a2"]);
        var numbered = r.NumberedAfter;
        Assert.Equal(2, numbered.Count);
        Assert.Equal(11, numbered[0].LineNum);
        Assert.Equal(12, numbered[1].LineNum);
    }

    [Fact]
    public void IsSelected_DefaultFalse_CanBeToggled()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        Assert.False(r.IsSelected);
        r.IsSelected = true;
        Assert.True(r.IsSelected);
    }

    [Fact]
    public void PropertyChanged_FiresOnIsSelectedChange()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, [], []);
        var raised = new List<string>();
        r.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        r.IsSelected = true;
        r.IsSelected = true; // same value, should not fire again
        r.IsSelected = false;

        Assert.Equal(["IsSelected", "IsSelected"], raised);
    }

    [Fact]
    public void RaiseHydrationPropertyChanged_FiresWithoutDispatcher()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, ["b"], ["a"]);
        var raised = new List<string>();
        r.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Evict and hydrate to trigger RaiseHydrationPropertyChanged
        using var store = new ResultStore();
        r.Evict(store);
        raised.Clear();

        SearchResult.HydrationDispatcher = null;
        r.Hydrate(store);

        Assert.Contains("NumberedBefore", raised);
        Assert.Contains("NumberedAfter", raised);
    }

    [Fact]
    public void RaiseHydrationPropertyChanged_WithDispatcher_MarshalsThroughDispatcher()
    {
        var r = new SearchResult(@"C:\f.cs", 1, "text", 0, 4, ["b"], ["a"]);
        var raised = new List<string>();
        r.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        using var store = new ResultStore();
        r.Evict(store);
        raised.Clear();

        bool dispatched = false;
        SearchResult.HydrationDispatcher = action => { dispatched = true; action(); };

        r.Hydrate(store);

        Assert.True(dispatched);
        Assert.Contains("NumberedBefore", raised);
        Assert.Contains("NumberedAfter", raised);

        SearchResult.HydrationDispatcher = null;
    }
}
