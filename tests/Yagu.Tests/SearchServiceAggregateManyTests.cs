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

    private static SearchResult Result(string path = "f.txt", int line = 1) =>
        new(path, line, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>());

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
    public async Task PriorityPass_EmptyRoots_EmitsSingleZeroCompleted()
    {
        var (run, started) = RunQueue();

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(
            Array.Empty<SearchOptions>(), run, default));

        var completed = Assert.IsType<SearchEvent.Completed>(Assert.Single(output));
        Assert.Equal(0, completed.Summary.TotalMatches);
        Assert.Equal(0, started());
    }

    [Fact]
    public async Task PriorityPass_NoNameHits_SkipsPriorityContentAndRunsFullSweep()
    {
        var invocations = new List<SearchOptions>();
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            invocations.Add(options);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new SearchEvent.Completed(Summary());
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.Both },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Collection(invocations,
            options => Assert.True(options.StopAfterNameFirstPass),
            options =>
            {
                Assert.False(options.StopAfterNameFirstPass);
                Assert.True(options.SuppressNameFirstPass);
            });
        Assert.Single(output.OfType<SearchEvent.Completed>());
    }

    [Fact]
    public async Task PriorityPass_CancelledTruncatedSummary_StopsBeforeFullSweep()
    {
        int calls = 0;
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new SearchEvent.Completed(Summary(cancelled: true, truncated: true));
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.Both },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Equal(1, calls);
        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.True(completed.Summary.Cancelled);
        Assert.True(completed.Summary.Truncated);
    }

    [Fact]
    public async Task PriorityPass_CancellationBetweenContentRoots_SkipsRemainingPriorityContent()
    {
        using var cts = new CancellationTokenSource();
        var invocations = new List<(string Root, SearchMode Mode)>();
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            invocations.Add((options.Directory, options.SearchMode));
            await Task.Yield();

            if (options.StopAfterNameFirstPass)
            {
                yield return new SearchEvent.Match(Result(options.Directory + "-hit.exe", line: 0));
                yield return new SearchEvent.Completed(Summary(totalMatches: 1));
                yield break;
            }

            if (options.SearchMode == SearchMode.FileNameThenContent)
                cts.Cancel();
            yield return new SearchEvent.Completed(Summary());
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.Both },
            new SearchOptions { Directory = "D", Query = "hit", SearchMode = SearchMode.Both },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, cts.Token));

        Assert.Equal(new[]
        {
            ("C", SearchMode.FileNames),
            ("D", SearchMode.FileNames),
            ("C", SearchMode.FileNameThenContent),
        }, invocations);
        Assert.True(Assert.Single(output.OfType<SearchEvent.Completed>()).Summary.Cancelled);
    }

    [Fact]
    public async Task AllRoots_NameAndPriorityContentPassesRunBeforeAnyFullSweep()
    {
        var invocations = new List<(string Root, bool Priority, bool Suppressed, bool Index, IReadOnlySet<string>? Names, IReadOnlySet<string>? Content)>();

        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            invocations.Add((options.Directory, options.StopAfterNameFirstPass, options.SuppressNameFirstPass,
                options.UseContentIndex, options.PreEmittedFileNamePaths, options.PreScannedContentPaths));

            string priorityPath = options.Directory + "-installer.exe";
            if (options.StopAfterNameFirstPass)
            {
                // Phase 1A publishes filename rows only; every root must finish this phase before any
                // content-priority work starts.
                yield return new SearchEvent.MatchBatch(new[] { Result(priorityPath, line: 0) });
                yield return new SearchEvent.Completed(Summary(totalFiles: 1, filesScanned: 1,
                    filesWithMatches: 1, totalMatches: 1));
                yield break;
            }

            if (options.SearchMode == SearchMode.FileNameThenContent)
            {
                // Phase 1B upgrades the already-visible filename group with content.
                yield return new SearchEvent.MatchBatch(new[] { Result(priorityPath, line: 7) });
                yield return new SearchEvent.Completed(Summary(totalFiles: 1, filesScanned: 1,
                    filesWithMatches: 1, totalMatches: 1, bytes: 10));
                yield break;
            }

            // The full pass should receive both roots' prepass sets and emit only a new content-only hit.
            Assert.True(options.PreEmittedFileNamePaths?.Contains("C-installer.exe"));
            Assert.True(options.PreEmittedFileNamePaths?.Contains("D-installer.exe"));
            Assert.True(options.PreScannedContentPaths?.Contains("C-installer.exe"));
            Assert.True(options.PreScannedContentPaths?.Contains("D-installer.exe"));
            yield return new SearchEvent.MatchBatch(new[] { Result(options.Directory + "-content-only.txt") });
            yield return new SearchEvent.Completed(Summary(totalFiles: 10, filesScanned: 10,
                filesWithMatches: 1, totalMatches: 1, bytes: 100));
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "installer", SearchMode = SearchMode.Both, UseContentIndex = true, MaxResults = 0 },
            new SearchOptions { Directory = "D", Query = "installer", SearchMode = SearchMode.Both, UseContentIndex = true, MaxResults = 0 },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        // Strict phase ordering: names from C + D, priority content from C + D, then full C + D. Index is
        // disabled only for the tiny priority phases so index startup cannot delay known Everything hits.
        Assert.Collection(invocations,
            x => { Assert.Equal("C", x.Root); Assert.True(x.Priority); Assert.False(x.Suppressed); Assert.False(x.Index); },
            x => { Assert.Equal("D", x.Root); Assert.True(x.Priority); Assert.False(x.Suppressed); Assert.False(x.Index); },
            x => { Assert.Equal("C", x.Root); Assert.False(x.Priority); Assert.True(x.Suppressed); Assert.False(x.Index); },
            x => { Assert.Equal("D", x.Root); Assert.False(x.Priority); Assert.True(x.Suppressed); Assert.False(x.Index); },
            x => { Assert.Equal("C", x.Root); Assert.False(x.Priority); Assert.True(x.Suppressed); Assert.True(x.Index); },
            x => { Assert.Equal("D", x.Root); Assert.False(x.Priority); Assert.True(x.Suppressed); Assert.True(x.Index); });

        var results = output.OfType<SearchEvent.MatchBatch>().SelectMany(batch => batch.Results).ToList();
        Assert.Equal(new[]
        {
            "C-installer.exe:0", "D-installer.exe:0",
            "C-installer.exe:7", "D-installer.exe:7",
            "C-content-only.txt:1", "D-content-only.txt:1",
        }, results.Select(result => $"{result.FilePath}:{result.LineNumber}"));

        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.Equal(6, completed.Summary.TotalMatches); // four priority results + two full-sweep results
        Assert.Equal(220, completed.Summary.BytesScanned); // 10+10 priority + 100+100 full
    }

    [Fact]
    public async Task AllRoots_FileNameOnly_UsesOnlyPriorityPasses_NoFullSweep()
    {
        int calls = 0;
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            Assert.True(options.StopAfterNameFirstPass);
            Assert.Equal(SearchMode.FileNames, options.SearchMode);
            yield return new SearchEvent.MatchBatch(new[] { Result(options.Directory + "-hit.exe", line: 0) });
            yield return new SearchEvent.Completed(Summary(totalFiles: 1, filesScanned: 1,
                filesWithMatches: 1, totalMatches: 1));
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.FileNames },
            new SearchOptions { Directory = "D", Query = "hit", SearchMode = SearchMode.FileNames },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Equal(2, calls);
        Assert.Equal(2, output.OfType<SearchEvent.MatchBatch>().Sum(batch => batch.Results.Count));
        Assert.Equal(2, Assert.Single(output.OfType<SearchEvent.Completed>()).Summary.TotalMatches);
    }

    [Fact]
    public async Task AllRoots_SingletonMatchBeyondHardCap_IsNotForwarded()
    {
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new SearchEvent.Match(Result(options.Directory + "-first.exe", line: 0));
            yield return new SearchEvent.Match(Result(options.Directory + "-beyond-cap.exe", line: 0));
            yield return new SearchEvent.Completed(Summary(totalFiles: 2, filesScanned: 2,
                filesWithMatches: 2, totalMatches: 2));
        }

        var roots = new[]
        {
            new SearchOptions
            {
                Directory = "C",
                Query = "hit",
                SearchMode = SearchMode.FileNames,
                MaxResults = 1,
            },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        var match = Assert.Single(output.OfType<SearchEvent.Match>());
        Assert.Equal("C-first.exe", match.Result.FilePath);
        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.Equal(1, completed.Summary.TotalMatches);
        Assert.True(completed.Summary.Truncated);
    }

    [Fact]
    public async Task PriorityPass_ForwardsPayloadsAndSuppressesControlEvents()
    {
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new SearchEvent.Match(Result("single.exe", line: 0));
            yield return new SearchEvent.MatchBatch(new[] { Result("batch.exe", line: 0) });
            yield return new SearchEvent.SourceBackedMatchBatch(new[]
            {
                new SourceBackedMatch("source.txt", 1, 0, 1, 0),
            });
            yield return new SearchEvent.SearchError("expected diagnostic");
            yield return new SearchEvent.Progress(new SearchProgress(0, 0, 0, 0, 0, 0, TimeSpan.Zero, 0));
            yield return new SearchEvent.DiscoveryComplete(3);
            yield return new SearchEvent.Fallback("ignored priority fallback");
            yield return new SearchEvent.ScanCompleted(Summary(totalMatches: 3));
            yield return new SearchEvent.Completed(Summary(totalFiles: 3, filesScanned: 3,
                filesWithMatches: 3, totalMatches: 3, bytes: 42, degraded: true));
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.FileNames },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Single(output.OfType<SearchEvent.Match>());
        Assert.Single(output.OfType<SearchEvent.MatchBatch>());
        Assert.Single(output.OfType<SearchEvent.SourceBackedMatchBatch>());
        Assert.Single(output.OfType<SearchEvent.SearchError>());
        Assert.Empty(output.OfType<SearchEvent.Progress>());
        Assert.Empty(output.OfType<SearchEvent.DiscoveryComplete>());
        Assert.Empty(output.OfType<SearchEvent.Fallback>());
        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.Equal(3, completed.Summary.TotalMatches);
        Assert.Equal(42, completed.Summary.BytesScanned);
        Assert.True(completed.Summary.Degraded);
    }

    [Fact]
    public async Task PriorityPass_SourceBackedBatchIsTrimmedAtHardCap()
    {
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new SearchEvent.MatchBatch(new[] { Result("name.exe", line: 0) });
            yield return new SearchEvent.SourceBackedMatchBatch(new[]
            {
                new SourceBackedMatch("name.exe", 1, 0, 1, 0),
                new SourceBackedMatch("beyond-cap.txt", 1, 0, 1, 0),
            });
            yield return new SearchEvent.Completed(Summary(totalFiles: 2, filesScanned: 2,
                filesWithMatches: 2, totalMatches: 3));
        }

        var roots = new[]
        {
            new SearchOptions
            {
                Directory = "C",
                Query = "hit",
                SearchMode = SearchMode.FileNames,
                MaxResults = 2,
            },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Single(Assert.Single(output.OfType<SearchEvent.SourceBackedMatchBatch>()).Results);
        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.Equal(2, completed.Summary.TotalMatches);
        Assert.True(completed.Summary.Truncated);
    }

    [Fact]
    public async Task PriorityPass_ExactHardCapDoesNotStartContentOrFullSweep()
    {
        int calls = 0;
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (options.StopAfterNameFirstPass)
                yield return new SearchEvent.Match(Result("C-hit.exe", line: 0));
            yield return new SearchEvent.Completed(Summary(totalFiles: 1, filesScanned: 1,
                filesWithMatches: 1, totalMatches: 1));
        }

        var roots = new[]
        {
            new SearchOptions
            {
                Directory = "C",
                Query = "hit",
                SearchMode = SearchMode.Both,
                MaxResults = 1,
            },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Equal(1, calls);
        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.Equal(1, completed.Summary.TotalMatches);
        Assert.True(completed.Summary.Truncated);
    }

    [Fact]
    public async Task PriorityPass_PreCancelledTokenEmitsCancelledSummaryWithoutStartingRoot()
    {
        int calls = 0;
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            calls++;
            await Task.Yield();
            yield break;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.FileNames },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, cts.Token));

        Assert.Equal(0, calls);
        Assert.True(Assert.Single(output.OfType<SearchEvent.Completed>()).Summary.Cancelled);
    }

    [Fact]
    public async Task PriorityPass_AggregatedSummarySaturatesNumericTotals()
    {
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (options.StopAfterNameFirstPass)
            {
                yield return new SearchEvent.Match(Result("C-hit.exe", line: 0));
                yield return new SearchEvent.Completed(Summary(bytes: 1));
                yield break;
            }

            if (options.SearchMode == SearchMode.FileNameThenContent)
            {
                yield return new SearchEvent.Completed(Summary());
                yield break;
            }

            yield return new SearchEvent.Completed(Summary(
                totalFiles: int.MaxValue,
                filesWithMatches: int.MaxValue,
                totalMatches: int.MaxValue,
                bytes: long.MaxValue));
        }

        var roots = new[]
        {
            new SearchOptions { Directory = "C", Query = "hit", SearchMode = SearchMode.Both },
        };

        var output = await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        var completed = Assert.Single(output.OfType<SearchEvent.Completed>());
        Assert.Equal(int.MaxValue, completed.Summary.TotalMatches);
        Assert.Equal(long.MaxValue, completed.Summary.BytesScanned);
    }

    [Theory]
    [InlineData(SearchMode.Content, false, null)]
    [InlineData(SearchMode.Both, true, null)]
    [InlineData(SearchMode.Both, false, FileListerBackend.Managed)]
    public async Task IneligibleNameFirstQuery_RunsOnlyFullPass(
        SearchMode mode,
        bool useRegex,
        FileListerBackend? backend)
    {
        int calls = 0;
        async IAsyncEnumerable<SearchEvent> Run(
            SearchOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            Assert.False(options.StopAfterNameFirstPass);
            Assert.False(options.SuppressNameFirstPass);
            yield return new SearchEvent.Completed(Summary());
        }

        var roots = new[]
        {
            new SearchOptions
            {
                Directory = "C",
                Query = "hit",
                SearchMode = mode,
                UseRegex = useRegex,
                FileListerBackendOverride = backend,
            },
        };

        await Drain(SearchService.PrioritizeNameMatchesAcrossRootsAsync(roots, Run, default));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOrEmptyList_EmitsSingleZeroCompleted_WithoutStartingAnyRoot(bool useNull)
    {
        var (run, started) = RunQueue();
        IReadOnlyList<SearchOptions> roots = useNull ? null! : Array.Empty<SearchOptions>();

        var outp = await Drain(SearchService.AggregateManyAsync(roots, run, default));

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
            new SearchEvent.Progress(new SearchProgress(1, 2, 0, 0, 0, 0, TimeSpan.Zero, 0)),
            new SearchEvent.Match(Result("a")),
            new SearchEvent.MatchBatch(new[] { Result("b"), Result("c") }),
            new SearchEvent.SourceBackedMatchBatch(new[] { new SourceBackedMatch("d", 1, 0, 1, 0) }),
            new SearchEvent.Fallback("es.exe fallback"),
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
        Assert.Single(outp.OfType<SearchEvent.Progress>());
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

    [Fact]
    public async Task Completed_AggregatesIndexAccelerationAcrossRoots()
    {
        // Root A: opted in and accelerated (pruned 10, rescued 1). Root B: opted in but bypassed. Root C:
        // never opted in (null). The aggregate sums requested/accelerated roots and pruned/rescued totals.
        SearchSummary WithIndex(IndexAccelerationInfo? info)
            => new(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null, null, info);

        var rootA = new SearchEvent[] { new SearchEvent.Completed(WithIndex(new IndexAccelerationInfo(1, 1, 10, 1))) };
        var rootB = new SearchEvent[] { new SearchEvent.Completed(WithIndex(new IndexAccelerationInfo(1, 0, 0, 0))) };
        var rootC = new SearchEvent[] { new SearchEvent.Completed(WithIndex(null)) };
        var (run, _) = RunQueue(rootA, rootB, rootC);

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(), Opt(), Opt() }, run, default));

        var completed = Assert.Single(outp.OfType<SearchEvent.Completed>());
        var agg = completed.Summary.IndexAcceleration;
        Assert.NotNull(agg);
        Assert.Equal(2, agg!.RequestedRoots);   // A + B opted in
        Assert.Equal(1, agg.AcceleratedRoots);  // only A accelerated
        Assert.Equal(10, agg.FilesPruned);
        Assert.Equal(1, agg.FilesRescued);
    }

    [Fact]
    public async Task Completed_IndexAccelerationNull_WhenNoRootOptedIn()
    {
        SearchSummary NoIndex()
            => new(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null);

        var rootA = new SearchEvent[] { new SearchEvent.Completed(NoIndex()) };
        var rootB = new SearchEvent[] { new SearchEvent.Completed(NoIndex()) };
        var (run, _) = RunQueue(rootA, rootB);

        var outp = await Drain(SearchService.AggregateManyAsync(new[] { Opt(), Opt() }, run, default));

        var completed = Assert.Single(outp.OfType<SearchEvent.Completed>());
        Assert.Null(completed.Summary.IndexAcceleration);
    }
}
