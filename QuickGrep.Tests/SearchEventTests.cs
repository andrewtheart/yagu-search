using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Tests;

public class SearchEventCoverageTests
{
    [Fact]
    public void Fallback_Properties()
    {
        var e = new SearchEvent.Fallback("no Everything");
        Assert.Equal("no Everything", e.Reason);
    }

    [Fact]
    public void DiscoveryComplete_Properties()
    {
        var e = new SearchEvent.DiscoveryComplete(42);
        Assert.Equal(42, e.TotalFiles);
    }

    [Fact]
    public void Match_Properties()
    {
        var r = new SearchResult("f.txt", 1, "line", 0, 4, Array.Empty<string>(), Array.Empty<string>());
        var e = new SearchEvent.Match(r);
        Assert.Same(r, e.Result);
    }

    [Fact]
    public void MatchBatch_Properties()
    {
        var results = new List<SearchResult>
        {
            new("f.txt", 1, "line1", 0, 5, Array.Empty<string>(), Array.Empty<string>()),
            new("f.txt", 2, "line2", 0, 5, Array.Empty<string>(), Array.Empty<string>()),
        };
        var e = new SearchEvent.MatchBatch(results);
        Assert.Equal(2, e.Results.Count);
    }

    [Fact]
    public void Progress_Properties()
    {
        var snapshot = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.Zero, 7);
        var e = new SearchEvent.Progress(snapshot);
        Assert.Equal(snapshot, e.Snapshot);
    }

    [Fact]
    public void Error_Properties()
    {
        var e = new SearchEvent.Error("bad regex");
        Assert.Equal("bad regex", e.Message);
    }

    [Fact]
    public void Completed_Properties()
    {
        var summary = new SearchSummary(1, 1, 0, 1, 1, 100, TimeSpan.FromSeconds(1), false, false, false, null);
        var e = new SearchEvent.Completed(summary);
        Assert.Equal(summary, e.Summary);
    }

    [Fact]
    public void MemoryPressure_Properties()
    {
        int acknowledged = -1;
        var e = new SearchEvent.MemoryPressure(
            AcknowledgeEviction: count => acknowledged = count,
            ThresholdPercent: 80,
            Diagnostics: "high memory");

        Assert.Equal(80, e.ThresholdPercent);
        Assert.Equal("high memory", e.Diagnostics);
        e.AcknowledgeEviction(5);
        Assert.Equal(5, acknowledged);
    }

    [Fact]
    public void MemoryPressure_DefaultParams()
    {
        var e = new SearchEvent.MemoryPressure(_ => { });
        Assert.Equal(0, e.ThresholdPercent);
        Assert.Null(e.Diagnostics);
    }

    [Fact]
    public void MemoryPressureRelieved_Properties()
    {
        var e = new SearchEvent.MemoryPressureRelieved("recovered");
        Assert.Equal("recovered", e.Diagnostics);
    }

    [Fact]
    public void MemoryPressureRelieved_DefaultParams()
    {
        var e = new SearchEvent.MemoryPressureRelieved();
        Assert.Null(e.Diagnostics);
    }

    [Fact]
    public void AllSubtypes_AreSearchEvent()
    {
        SearchEvent[] events =
        [
            new SearchEvent.Fallback("r"),
            new SearchEvent.DiscoveryComplete(0),
            new SearchEvent.Match(new SearchResult("f", 1, "l", 0, 1, Array.Empty<string>(), Array.Empty<string>())),
            new SearchEvent.MatchBatch(Array.Empty<SearchResult>()),
            new SearchEvent.Progress(new SearchProgress(0, 0, 0, 0, 0, 0, TimeSpan.Zero)),
            new SearchEvent.Error("e"),
            new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null)),
            new SearchEvent.MemoryPressure(_ => { }),
            new SearchEvent.MemoryPressureRelieved(),
        ];
        Assert.All(events, e => Assert.IsAssignableFrom<SearchEvent>(e));
    }
}
