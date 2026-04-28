using System.ComponentModel;
using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Tests;

// ─── ContextLine ────────────────────────────────────────────────────────

public class ContextLineTests
{
    [Fact]
    public void Properties_ReturnConstructorValues()
    {
        var cl = new ContextLine(42, "hello world");
        Assert.Equal(42, cl.LineNum);
        Assert.Equal("hello world", cl.Text);
    }

    [Fact]
    public void RecordEquality()
    {
        var a = new ContextLine(1, "abc");
        var b = new ContextLine(1, "abc");
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ContextLine(2, "abc"));
        Assert.NotEqual(a, new ContextLine(1, "xyz"));
    }

    [Fact]
    public void Deconstruction()
    {
        var (num, text) = new ContextLine(7, "line");
        Assert.Equal(7, num);
        Assert.Equal("line", text);
    }
}

// ─── SearchResult ───────────────────────────────────────────────────────

public class SearchResultCoverageTests
{
    private static SearchResult MakeResult(
        string matchLine = "hello world",
        int lineNumber = 10,
        IReadOnlyList<string>? before = null,
        IReadOnlyList<string>? after = null)
    {
        return new SearchResult(
            FilePath: @"C:\test\file.txt",
            LineNumber: lineNumber,
            MatchLine: matchLine,
            MatchStartColumn: 0,
            MatchLength: 5,
            ContextBefore: before ?? Array.Empty<string>(),
            ContextAfter: after ?? Array.Empty<string>());
    }

    [Fact]
    public void ShortPreview_ShortLine_ReturnsFullLine()
    {
        var r = MakeResult("short line");
        Assert.Equal("short line", r.ShortPreview);
    }

    [Fact]
    public void ShortPreview_LongLine_TruncatesAt120()
    {
        var longLine = new string('A', 200);
        var r = MakeResult(longLine);
        Assert.Equal(longLine[..120] + "…", r.ShortPreview);
        Assert.Equal(121, r.ShortPreview.Length);
    }

    [Fact]
    public void ShortPreview_Exactly120Chars_NoTruncation()
    {
        var exact = new string('B', 120);
        var r = MakeResult(exact);
        Assert.Equal(exact, r.ShortPreview);
    }

    [Fact]
    public void IsEvicted_Initially_False()
    {
        var r = MakeResult();
        Assert.False(r.IsEvicted);
        Assert.Equal(-1, r.DiskOffset);
    }

    [Fact]
    public void Evict_And_Hydrate_RoundTrip()
    {
        using var store = new ResultStore();
        var r = MakeResult("original line", before: new[] { "before1" }, after: new[] { "after1" });

        r.Evict(store);
        Assert.True(r.IsEvicted);
        Assert.True(r.DiskOffset >= 0);
        Assert.Equal(r.ShortPreview, r.MatchLine);
        Assert.Empty(r.ContextBefore);
        Assert.Empty(r.ContextAfter);

        r.Hydrate(store);
        Assert.False(r.IsEvicted);
        Assert.Equal("original line", r.MatchLine);
        Assert.Single(r.ContextBefore);
        Assert.Equal("before1", r.ContextBefore[0]);
        Assert.Single(r.ContextAfter);
        Assert.Equal("after1", r.ContextAfter[0]);
    }

    [Fact]
    public void Evict_Twice_IsNoOp()
    {
        using var store = new ResultStore();
        var r = MakeResult();
        r.Evict(store);
        var offset1 = r.DiskOffset;
        r.Evict(store); // second evict is no-op
        Assert.Equal(offset1, r.DiskOffset);
    }

    [Fact]
    public void Hydrate_WhenNotEvicted_IsNoOp()
    {
        using var store = new ResultStore();
        var r = MakeResult("line");
        r.Hydrate(store);
        Assert.Equal("line", r.MatchLine);
        Assert.False(r.IsEvicted);
    }

    [Fact]
    public void EvictWith_Works()
    {
        using var store = new ResultStore();
        var r = MakeResult("test line", before: new[] { "b" }, after: new[] { "a" });

        store.WriteBatch(writeOne =>
        {
            r.EvictWith(writeOne);
        });

        Assert.True(r.IsEvicted);
        Assert.Equal(r.ShortPreview, r.MatchLine);
    }

    [Fact]
    public void IsSelected_FiresPropertyChanged()
    {
        var r = MakeResult();
        var raised = new List<string>();
        r.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        r.IsSelected = true;
        r.IsSelected = true; // same value, no fire
        r.IsSelected = false;

        Assert.Equal(2, raised.Count);
        Assert.All(raised, name => Assert.Equal("IsSelected", name));
    }

    [Fact]
    public void NumberedBefore_CorrectLineNumbers()
    {
        var r = MakeResult(lineNumber: 10, before: new[] { "line7", "line8", "line9" });
        var numbered = r.NumberedBefore;
        Assert.Equal(3, numbered.Count);
        Assert.Equal(7, numbered[0].LineNum);
        Assert.Equal("line7", numbered[0].Text);
        Assert.Equal(8, numbered[1].LineNum);
        Assert.Equal(9, numbered[2].LineNum);
    }

    [Fact]
    public void NumberedAfter_CorrectLineNumbers()
    {
        var r = MakeResult(lineNumber: 10, after: new[] { "line11", "line12" });
        var numbered = r.NumberedAfter;
        Assert.Equal(2, numbered.Count);
        Assert.Equal(11, numbered[0].LineNum);
        Assert.Equal("line11", numbered[0].Text);
        Assert.Equal(12, numbered[1].LineNum);
    }

    [Fact]
    public void NumberedBefore_Empty_ReturnsEmpty()
    {
        var r = MakeResult(lineNumber: 5);
        Assert.Empty(r.NumberedBefore);
    }

    [Fact]
    public void NumberedAfter_Empty_ReturnsEmpty()
    {
        var r = MakeResult(lineNumber: 5);
        Assert.Empty(r.NumberedAfter);
    }
}

// ─── SearchResult: MatchLength access ───────────────────────────────────

public class SearchResultMatchLengthTest
{
    [Fact]
    public void MatchLength_ReturnsCorrectValue()
    {
        var r = new SearchResult("f.txt", 1, "hello world", 0, 5,
            Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(5, r.MatchLength);
    }

    [Fact]
    public void MatchStartColumn_ReturnsCorrectValue()
    {
        var r = new SearchResult("f.txt", 1, "hello world", 6, 5,
            Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(6, r.MatchStartColumn);
    }
}
