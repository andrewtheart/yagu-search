using System.Runtime.CompilerServices;
using Yagu.Models;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Branch-complete unit tests for <see cref="SearchService.AggregateManyAsync"/> — the multi-root
/// "search all drives" orchestrator. The per-root searcher is injected as a synthetic event stream
/// so every switch arm, the count==0/count==1 fast paths, the global MaxResults cap, and the
/// cancellation break are exercised deterministically without any file I/O.
/// </summary>
public class SearchServiceAggregateManyTests
{
    private static SearchOptions Opt(int maxResults = 0) =>
        new() { Directory = "X", Query = "q", MaxResults = maxResults };

    private static SearchResult Result(string path = "f.txt") =>
        new(path, 1, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>());

    private static SearchSummary Summary(
        int totalFiles = 0, int filesScanned = 0, int filesWithMatches = 0, int totalMatches = 0,
        long bytes = 0, bool truncated = false, bool degraded = false, bool cancelled = false, string? fallback = null)
        => new(totalFiles, filesScanned, 0, filesWithMatches, totalMatches, bytes, TimeSpan.Zero, cancelled, truncated, degraded, fallback);

    private static async IAsyncEnumerable<SearchEvent> StreamOf(
        SearchEvent[] events, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    // Serves one queued event array per root invocation and records how many roots were actually started.
    private static (Func<SearchOptions, CancellationToken, IAsyncEnumerable<SearchEvent>> run, Func<int> started) RunQueue(params SearchEvent[][] perRoot)
    {
        int started = 0;
        IAsyncEnumerable<SearchEvent> Run(SearchOptions _, CancellationToken ct)
        {
            var events = perRoot[started];
            started++;
            return StreamOf(events, ct);
        }
        return (Run, () => started);
    }

    private static async Task<List<SearchEvent>> Drain(IAsyncEnumerable<SearchEvent> stream)
    {
        var list = new List<SearchEvent>();
        await foreach (var e in stream)
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task EmptyList_EmitsSingleZeroCompleted_WithoutStartingAnyRoot()
    {
        var (run, started) = RunQueue();

        var outp = await Drain(SearchService.AggregateManyAsync(Array.Empty<SearchOptions>(), run, default));

        var completed = Assert.IsType<SearchEvent.Completed>(Assert.Single(outp));
        Assert.Equal(0, completed.Summary.TotalMatches);
        Assert.Equal(0, started());
    }

    [Fact]
    public async Task SingleRoot_ForwardsEventsVerbatim_WithoutReaggregating()
    {
        var summary = Summary(totalMatches: 1);
        var (run, started) = RunQueue(new SearchEvent[]
        {
            new SearchEvent.Match(Result()),
            new SearchEvent.Completed(summary),
        });

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt() }, run, default));

        Assert.Equal(1, started());
        Assert.Collection(outp,
            e => Assert.IsType<SearchEvent.Match>(e),
            e => Assert.Same(summary, Assert.IsType<SearchEvent.Completed>(e).Summary)); // verbatim
    }

    [Fact]
    public async Task MultiRoot_ForwardsAllEventArms_SuppressesIntermediateCompletion_AndAggregatesSummary()
    {
        var root1 = new SearchEvent[]
        {
            new SearchEvent.DiscoveryComplete(2),
            new SearchEvent.Match(Result("a")),
            new SearchEvent.MatchBatch(new[] { Result("b"), Result("c") }),
            new SearchEvent.SourceBackedMatchBatch(new[] { new SourceBackedMatch("d", 1, 0, 1, 0) }),
            new SearchEvent.Fallback("es.exe fallback"),               // default switch arm
            new SearchEvent.ScanCompleted(Summary(totalMatches: 4)),   // intermediate -> suppressed
            new SearchEvent.Completed(Summary(totalMatches: 4, filesScanned: 5, filesWithMatches: 3, totalFiles: 2, bytes: 100, degraded: true, fallback: "root1-reason")),
        };
        var root2 = new SearchEvent[]
        {
            new SearchEvent.DiscoveryComplete(3),
            new SearchEvent.Completed(Summary(totalMatches: 1, filesScanned: 2, filesWithMatches: 1, totalFiles: 3, bytes: 50, truncated: true, fallback: "root2-reason")),
        };
        var (run, started) = RunQueue(root1, root2);

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(), Opt() }, run, default));

        Assert.Equal(2, started());

        // Cumulative DiscoveryComplete totals (2, then 2+3=5).
        var discoveries = outp.OfType<SearchEvent.DiscoveryComplete>().ToList();
        Assert.Equal(new[] { 2, 5 }, discoveries.Select(d => d.TotalFiles));

        // Every payload arm forwarded exactly once.
        Assert.Single(outp.OfType<SearchEvent.Match>());
        Assert.Single(outp.OfType<SearchEvent.MatchBatch>());
        Assert.Single(outp.OfType<SearchEvent.SourceBackedMatchBatch>());
        // Per-root Fallback notices are suppressed in multi-root when the run produced matches, so the
        // "no results" warning from one drive never appears next to a full result set.
        Assert.Empty(outp.OfType<SearchEvent.Fallback>());

        // Intermediate ScanCompleted/Completed suppressed -> exactly one final pair, at the very end.
        Assert.Single(outp.OfType<SearchEvent.ScanCompleted>());
        var completed = Assert.Single(outp.OfType<SearchEvent.Completed>());
        Assert.IsType<SearchEvent.ScanCompleted>(outp[^2]);
        Assert.Same(completed, outp[^1]);

        // Aggregated summary.
        Assert.Equal(5, completed.Summary.TotalMatches);     // 4 + 1
        Assert.Equal(7, completed.Summary.FilesScanned);     // 5 + 2
        Assert.Equal(4, completed.Summary.FilesWithMatches); // 3 + 1
        Assert.Equal(5, completed.Summary.TotalFiles);       // 2 + 3
        Assert.Equal(150, completed.Summary.BytesScanned);   // 100 + 50
        Assert.True(completed.Summary.Degraded);             // root1
        Assert.True(completed.Summary.Truncated);            // root2
        Assert.Equal("root1-reason", completed.Summary.FallbackReason); // first non-null wins
    }

    [Fact]
    public async Task MultiRoot_NoMatchesAnywhere_ReEmitsSingleFallbackReasonAtEnd()
    {
        // One drive reports a fallback reason but neither drive returns matches.
        var root1 = new SearchEvent[]
        {
            new SearchEvent.Fallback("Everything SDK returned no results"),
            new SearchEvent.Completed(Summary(fallback: "Everything SDK returned no results")),
        };
        var root2 = new SearchEvent[]
        {
            new SearchEvent.Completed(Summary()),
        };
        var (run, _) = RunQueue(root1, root2);

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(), Opt() }, run, default));

        // The per-root Fallback is suppressed mid-stream, but because the whole run found nothing the
        // reason is re-emitted exactly once — and before the final ScanCompleted/Completed pair.
        var fallback = Assert.Single(outp.OfType<SearchEvent.Fallback>());
        Assert.Equal("Everything SDK returned no results", fallback.Reason);
        int fallbackIndex = outp.IndexOf(fallback);
        Assert.IsType<SearchEvent.ScanCompleted>(outp[fallbackIndex + 1]);
        Assert.IsType<SearchEvent.Completed>(outp[fallbackIndex + 2]);
    }

    [Fact]
    public async Task MultiRoot_MatchesPresent_SuppressesFallbackEntirely()
    {
        var root1 = new SearchEvent[]
        {
            new SearchEvent.Match(Result("a")),
            new SearchEvent.Fallback("Everything SDK returned no results"),
            new SearchEvent.Completed(Summary(totalMatches: 1, filesWithMatches: 1)),
        };
        var root2 = new SearchEvent[] { new SearchEvent.Completed(Summary()) };
        var (run, _) = RunQueue(root1, root2);

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(), Opt() }, run, default));

        Assert.Single(outp.OfType<SearchEvent.Match>());
        Assert.Empty(outp.OfType<SearchEvent.Fallback>()); // results present -> no fallback notice surfaced
    }

    [Fact]
    public async Task GlobalMaxResultsCap_StopsAfterCapReached_MarksTruncated_AndSkipsRemainingRoots()
    {
        var root1 = new SearchEvent[] { new SearchEvent.Match(Result()), new SearchEvent.Completed(Summary()) };
        var root2 = new SearchEvent[] { new SearchEvent.Match(Result()), new SearchEvent.Completed(Summary()) };
        var (run, started) = RunQueue(root1, root2);

        // perRootOptions[0].MaxResults == 1 is the global cap.
        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(maxResults: 1), Opt(maxResults: 1) }, run, default));

        Assert.Equal(1, started());                          // root2 never started
        Assert.Single(outp.OfType<SearchEvent.Match>());     // only root1's match forwarded
        var completed = Assert.Single(outp.OfType<SearchEvent.Completed>());
        Assert.True(completed.Summary.Truncated);
    }

    [Fact]
    public async Task PreCancelledToken_BreaksBeforeStartingRoots_AndEmitsCancelledCompleted()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (run, started) = RunQueue(Array.Empty<SearchEvent>(), Array.Empty<SearchEvent>());

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(), Opt() }, run, cts.Token));

        Assert.Equal(0, started());
        var completed = Assert.Single(outp.OfType<SearchEvent.Completed>());
        Assert.True(completed.Summary.Cancelled);
    }
}
