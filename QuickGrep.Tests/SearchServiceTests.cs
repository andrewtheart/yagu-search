using System.Text;
using System.Runtime.CompilerServices;
using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Tests;

public class SearchServiceTests : IDisposable
{
    private readonly string _root;
    public SearchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
    }

    [Fact]
    public async Task EndToEnd_FindsMatchesAcrossFiles()
    {
        Write("a.txt", "foo\nbar\nfoo");
        Write(@"sub\b.txt", "FOO");
        Write("ignore.bin", "\0\0\0");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "foo",
            CaseSensitive = false,
            UseRegex = false,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        int matches = 0;
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            switch (evt)
            {
                case SearchEvent.Match: matches++; break;
                case SearchEvent.MatchBatch mb: matches += mb.Results.Count; break;
                case SearchEvent.Completed c: summary = c.Summary; break;
            }
        }
        Assert.Equal(3, matches);
        Assert.NotNull(summary);
        Assert.Equal(3, summary!.TotalMatches);
        Assert.Equal(2, summary.FilesWithMatches);
    }

    [Fact]
    public async Task InvalidRegex_EmitsError()
    {
        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "(unclosed",
            UseRegex = true,
        };

        bool error = false;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Error) error = true;
        }
        Assert.True(error);
    }

    [Fact]
    public async Task ResultCap_TruncatesEarly()
    {
        for (int i = 0; i < 5; i++)
            Write($"f{i}.txt", string.Join('\n', Enumerable.Repeat("hit", 50)));

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "hit",
            MaxResults = 10,
            MaxFileSizeBytes = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }
        Assert.NotNull(summary);
        Assert.True(summary!.Truncated);
    }

    [Fact]
    public async Task IncludeGlob_FiltersFiles()
    {
        Write("a.cs", "needle");
        Write("a.txt", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            IncludeGlobs = new[] { "cs" },
            MaxFileSizeBytes = 0,
        };
        int matches = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) matches++;
            else if (evt is SearchEvent.MatchBatch mb) matches += mb.Results.Count;
        }
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task Progress_EmitsWhileDiscoveryIsStillRunningWithoutMatches()
    {
        Write("a.txt", "quiet file");
        Write("b.txt", "another quiet file");

        var files = new[]
        {
            Path.Combine(_root, "a.txt"),
            Path.Combine(_root, "b.txt"),
        };
        var svc = new SearchService(new DelayedFileLister(files, TimeSpan.FromMilliseconds(350)), new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        bool discoveryCompleteSeen = false;
        bool progressBeforeDiscoveryComplete = false;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Progress progress && !discoveryCompleteSeen && progress.Snapshot.TotalFiles > 0)
            {
                progressBeforeDiscoveryComplete = true;
            }
            else if (evt is SearchEvent.DiscoveryComplete)
            {
                discoveryCompleteSeen = true;
            }
        }

        Assert.True(progressBeforeDiscoveryComplete);
    }

    [Fact]
    public async Task Progress_UsesFileListerKnownTotalAsDenominator()
    {
        Write("a.txt", "quiet file");
        Write("b.txt", "another quiet file");

        var files = new[]
        {
            Path.Combine(_root, "a.txt"),
            Path.Combine(_root, "b.txt"),
        };
        var svc = new SearchService(new DelayedFileLister(files, TimeSpan.FromMilliseconds(350), knownTotalFiles: 1_000_000), new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchProgress? progressBeforeDiscoveryComplete = null;
        bool discoveryCompleteSeen = false;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Progress progress && !discoveryCompleteSeen && progress.Snapshot.FilesScanned > 0)
            {
                progressBeforeDiscoveryComplete ??= progress.Snapshot;
            }
            else if (evt is SearchEvent.DiscoveryComplete)
            {
                discoveryCompleteSeen = true;
            }
        }

        Assert.NotNull(progressBeforeDiscoveryComplete);
        Assert.Equal(1_000_000, progressBeforeDiscoveryComplete!.TotalFiles);
        Assert.True(progressBeforeDiscoveryComplete.FilesScanned < progressBeforeDiscoveryComplete.TotalFiles);
    }

    [Fact]
    public async Task SearchSummary_CountsGlobFilteredFilesAsCompleted()
    {
        Write("keep.txt", "needle");
        Write("skip.cs", "needle");

        var files = new[]
        {
            Path.Combine(_root, "keep.txt"),
            Path.Combine(_root, "skip.cs"),
        };
        var svc = new SearchService(new DelayedFileLister(files, TimeSpan.Zero, knownTotalFiles: 2), new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            IncludeGlobs = new[] { "*.txt" },
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed completed)
                summary = completed.Summary;
        }

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.TotalFiles);
        Assert.Equal(2, summary.FilesScanned);
        Assert.Equal(1, summary.FilesSkipped);
        Assert.Equal(1, summary.SkipReasons?.Other);
        Assert.Equal(1, summary.TotalMatches);
    }

    [Fact]
    public async Task FileNameOnly_CountsDiscoveredFilesAsCompleted()
    {
        Write("alpha.txt", "content is not searched");
        Write("beta.txt", "content is not searched");

        var files = new[]
        {
            Path.Combine(_root, "alpha.txt"),
            Path.Combine(_root, "beta.txt"),
        };
        var svc = new SearchService(new DelayedFileLister(files, TimeSpan.Zero, knownTotalFiles: 2), new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "nomatch",
            SearchMode = SearchMode.FileNames,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed completed)
                summary = completed.Summary;
        }

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.TotalFiles);
        Assert.Equal(2, summary.FilesScanned);
        Assert.Equal(0, summary.FilesSkipped);
    }

    [Fact]
    public async Task SearchSummary_CountsSkippedDirectoriesFromFileLister()
    {
        var svc = new SearchService(new SkippedDirectoryFileLister(skippedDirectories: 2), new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed completed)
                summary = completed.Summary;
        }

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.FilesSkipped);
        Assert.Equal(2, summary.SkipReasons?.Directories);
    }

    [Fact]
    public async Task SearchSummary_CountsAccessDeniedDirectoriesSeparately()
    {
        var svc = new SearchService(new SkippedDirectoryFileLister(skippedDirectories: 2, accessDeniedDirectories: 1), new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed completed)
                summary = completed.Summary;
        }

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.FilesSkipped);
        Assert.Equal(1, summary.SkipReasons?.AccessDenied);
        Assert.Equal(1, summary.SkipReasons?.Directories);
    }

    private sealed class DelayedFileLister(IReadOnlyList<string> files, TimeSpan delayAfterFirst, int knownTotalFiles = 0) : IFileLister
    {
        public string? FallbackReason => null;
        public int SkippedDirectories => 0;
        public int AccessDeniedDirectories => 0;
        public int KnownTotalFiles { get; } = knownTotalFiles;

        public async IAsyncEnumerable<string> ListFilesAsync(
            string directory,
            IReadOnlyList<string> includeExtensions,
            int maxFiles,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (files.Count == 0) yield break;

            yield return files[0];
            await Task.Delay(delayAfterFirst, cancellationToken).ConfigureAwait(false);

            for (int i = 1; i < files.Count; i++)
            {
                yield return files[i];
            }
        }
    }

    private sealed class SkippedDirectoryFileLister(int skippedDirectories, int accessDeniedDirectories = 0) : IFileLister
    {
        public string? FallbackReason => null;
        public int SkippedDirectories { get; private set; }
        public int AccessDeniedDirectories { get; private set; }
        public int KnownTotalFiles => 0;

        public async IAsyncEnumerable<string> ListFilesAsync(
            string directory,
            IReadOnlyList<string> includeExtensions,
            int maxFiles,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            SkippedDirectories = skippedDirectories;
            AccessDeniedDirectories = accessDeniedDirectories;
            yield break;
        }
    }
}
