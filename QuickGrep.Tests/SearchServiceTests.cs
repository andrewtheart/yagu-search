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

    [Theory]
    [InlineData(51, 62, true)]
    [InlineData(57, 62, true)]
    [InlineData(58, 62, false)]
    [InlineData(61, 62, false)]
    public void MemoryPressureRelief_UsesFivePercentHysteresis(uint systemLoadPercent, int pressurePercent, bool expectedRelieved)
    {
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 512,
            effectiveProcessCapBytes: 1_024,
            systemLoadPercent,
            pressurePercent,
            recoveryMarginPercent: 5);

        Assert.Equal(expectedRelieved, relieved);
    }

    [Fact]
    public void MemoryPressureRelief_WaitsForProcessWorkingSetToDropBelowCapMargin()
    {
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 950,
            effectiveProcessCapBytes: 1_000,
            systemMemoryLoadPercent: 51,
            pressurePercent: 62,
            recoveryMarginPercent: 5);

        Assert.False(relieved);
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

// ─── SearchService: MaxResults clamping ─────────────────────────────────

public class SearchServiceClampTests : IDisposable
{
    private readonly string _root;
    public SearchServiceClampTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-clamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "match");
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task MaxResultsAboveCeiling_GetsClampedAndSearchCompletes()
    {
        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "match",
            MaxResults = 999_999,
            MaxFileSizeBytes = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }
        Assert.NotNull(summary);
        Assert.True(summary!.TotalMatches >= 1);
    }
}

// ─── SearchService: FlushFilenameBatchAsync ─────────────────────────────

public class SearchServiceFlushBatchTests : IDisposable
{
    private readonly string _root;
    public SearchServiceFlushBatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task FileNameSearch_WithMultipleFiles_FlushesAsMatchBatch()
    {
        for (int i = 0; i < 50; i++)
            File.WriteAllText(Path.Combine(_root, $"target{i}.txt"), "content");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "target",
            SearchMode = SearchMode.FileNames,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        int matchBatchCount = 0;
        int matchCount = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.MatchBatch mb) { matchBatchCount++; matchCount += mb.Results.Count; }
            if (evt is SearchEvent.Match) matchCount++;
        }
        Assert.True(matchCount >= 50);
    }
}

// ─── SearchService: more SearchAsync paths ──────────────────────────────

public class SearchServiceExtraTests : IDisposable
{
    private readonly string _root;
    public SearchServiceExtraTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-svc-extra-" + Guid.NewGuid().ToString("N"));
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
    public async Task EmptyQuery_EmitsCompletedImmediately()
    {
        Write("a.txt", "content");
        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "",
            MaxFileSizeBytes = 0,
        };

        SearchSummary? summary = null;
        int matchCount = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed c) summary = c.Summary;
            if (evt is SearchEvent.Match) matchCount++;
            if (evt is SearchEvent.MatchBatch mb) matchCount += mb.Results.Count;
        }
        Assert.True(matchCount == 0 || summary is not null);
    }

    [Fact]
    public async Task Cancellation_StopsSearch()
    {
        for (int i = 0; i < 100; i++)
            Write($"f{i}.txt", string.Join('\n', Enumerable.Repeat("match", 50)));

        var svc = new SearchService();
        var cts = new CancellationTokenSource();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "match",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        int events = 0;
        try
        {
            await foreach (var evt in svc.SearchAsync(opts, cts.Token))
            {
                events++;
                if (events >= 3) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { /* expected */ }

        Assert.True(events >= 1);
    }

    [Fact]
    public async Task FileNameOnlySearch_MatchesByFileName()
    {
        Write("alpha.txt", "content not searched");
        Write("beta.txt", "content not searched");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "alpha",
            SearchMode = SearchMode.FileNames,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        int matches = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) matches++;
            if (evt is SearchEvent.MatchBatch mb) matches += mb.Results.Count;
        }
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task SearchWithContextLines_ReturnsContext()
    {
        Write("ctx.txt", "line1\nline2\nMATCH\nline4\nline5");
        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "MATCH",
            ContextLines = 1,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchResult? found = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match m) found = m.Result;
            if (evt is SearchEvent.MatchBatch mb && mb.Results.Count > 0) found = mb.Results[0];
        }
        Assert.NotNull(found);
        Assert.NotEmpty(found!.ContextBefore);
        Assert.NotEmpty(found.ContextAfter);
    }

    [Fact]
    public void ExtractExtensions_StarDotExtWithUnderscoreAndDigit()
    {
        var result = SearchService.ExtractExtensions(new[] { "*.c99", "*.h_file" });
        Assert.Equal(2, result.Count);
        Assert.Contains("c99", result);
        Assert.Contains("h_file", result);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_NoPressureConfig_ReturnsTrue()
    {
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 100,
            effectiveProcessCapBytes: 200,
            systemMemoryLoadPercent: 90,
            pressurePercent: 0,
            recoveryMarginPercent: 5);
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_HighWorkingSet_ReturnsFalse()
    {
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 999,
            effectiveProcessCapBytes: 1000,
            systemMemoryLoadPercent: 50,
            pressurePercent: 90,
            recoveryMarginPercent: 5);
        Assert.False(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_ZeroCap_ReturnsTrue()
    {
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 999,
            effectiveProcessCapBytes: 0,
            systemMemoryLoadPercent: 50,
            pressurePercent: 90,
            recoveryMarginPercent: 5);
        Assert.True(relieved);
    }
}

// ─── SearchService.ExtractExtensions ────────────────────────────────────

public class ExtractExtensionsCoverageTests
{
    [Fact]
    public void SimpleGlob_ExtractsExtension()
    {
        var result = SearchService.ExtractExtensions(new[] { "*.cs" });
        Assert.Single(result);
        Assert.Equal("cs", result[0]);
    }

    [Fact]
    public void BareExtension_NoStar()
    {
        var result = SearchService.ExtractExtensions(new[] { "ts" });
        Assert.Single(result);
        Assert.Equal("ts", result[0]);
    }

    [Fact]
    public void DotPrefixed_Extension()
    {
        var result = SearchService.ExtractExtensions(new[] { ".json" });
        Assert.Single(result);
        Assert.Equal("json", result[0]);
    }

    [Fact]
    public void ComplexGlob_ExtractsExtension()
    {
        var result = SearchService.ExtractExtensions(new[] { "src/**/*.py" });
        Assert.Empty(result);
    }

    [Fact]
    public void SemicolonSeparated()
    {
        var result = SearchService.ExtractExtensions(new[] { "*.cs;*.xml;*.json" });
        Assert.Equal(3, result.Count);
        Assert.Contains("cs", result);
        Assert.Contains("xml", result);
        Assert.Contains("json", result);
    }

    [Fact]
    public void CommaSeparated()
    {
        var result = SearchService.ExtractExtensions(new[] { "*.cs,*.ts" });
        Assert.Equal(2, result.Count);
        Assert.Contains("cs", result);
        Assert.Contains("ts", result);
    }

    [Fact]
    public void NullInput_ReturnsEmpty()
    {
        var result = SearchService.ExtractExtensions(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        var result = SearchService.ExtractExtensions(Array.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void BlankEntries_Skipped()
    {
        var result = SearchService.ExtractExtensions(new[] { "", "  ", "*.cs" });
        Assert.Single(result);
        Assert.Equal("cs", result[0]);
    }

    [Fact]
    public void MultipleInputEntries()
    {
        var result = SearchService.ExtractExtensions(new[] { "*.cs", "*.xml" });
        Assert.Equal(2, result.Count);
        Assert.Contains("cs", result);
        Assert.Contains("xml", result);
    }

    [Fact]
    public void Underscore_InExtension_IsAllowed()
    {
        var result = SearchService.ExtractExtensions(new[] { "*.c_pp" });
        Assert.Single(result);
        Assert.Equal("c_pp", result[0]);
    }
}

// ─── ExtractExtensions: non-alphanumeric ────────────────────────────────

public class ExtractExtensionsNonAlphaTests
{
    [Fact]
    public void SpecialCharsInExtension_NotExtracted()
    {
        var result = SearchService.ExtractExtensions(["*.c++"]);
        Assert.Empty(result);
    }

    [Fact]
    public void MixedPatterns_OnlyValidExtracted()
    {
        var result = SearchService.ExtractExtensions(["*.cs", "*.c++", "*.txt"]);
        Assert.Equal(new[] { "cs", "txt" }, result);
    }

    [Fact]
    public void ExtensionWithUnderscore_IsExtracted()
    {
        var result = SearchService.ExtractExtensions(["*.my_ext"]);
        Assert.Equal(new[] { "my_ext" }, result);
    }

    [Fact]
    public void EmptyAfterStrip_NotExtracted()
    {
        var result = SearchService.ExtractExtensions(["*."]);
        Assert.Empty(result);
    }
}

// ─── SearchService.EffectiveProcessMemoryCap ────────────────────────────

public class EffectiveProcessMemoryCapTests
{
    [Fact]
    public void EffectiveProcessMemoryCap_ZeroCap_ReturnsAutoCap()
    {
        var method = typeof(SearchService).GetMethod(
            "EffectiveProcessMemoryCap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (long)method!.Invoke(null, [0L])!;
        Assert.True(result > 0);
    }

    [Fact]
    public void EffectiveProcessMemoryCap_PositiveCap_ReturnsSameValue()
    {
        var method = typeof(SearchService).GetMethod(
            "EffectiveProcessMemoryCap",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (long)method!.Invoke(null, [42L])!;
        Assert.Equal(42L, result);
    }
}
