using System.Text;
using System.Runtime.CompilerServices;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

[Collection("FileListerBackend")]
public class SearchServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public SearchServiceTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(1, 1024)]
    [InlineData(8, 1024)]
    [InlineData(16, 2048)]
    [InlineData(24, 3072)]
    [InlineData(64, 4096)]
    public void ResolveNativeBatchSize_ScalesWithParallelism(int parallelism, int expected)
    {
        Assert.Equal(expected, SearchService.ResolveNativeBatchSize(parallelism));
    }

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
    public async Task FileSizeRange_FiltersBeforeMatching()
    {
        Write("too-small.txt", "needle");
        Write("in-range.txt", "prefix needle suffix");
        Write("too-large.txt", "needle " + new string('x', 80));

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MinFileSizeBytes = 10,
            MaxFileSizeBytes = 40,
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

        Assert.Equal(1, matches);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalMatches);
        Assert.Equal(1, summary.FilesWithMatches);
        Assert.NotNull(summary.SkipReasons);
        Assert.True(summary.SkipReasons!.EarlyFiltered >= 2);
        Assert.True(summary.SkipReasons.TooLarge >= 1);
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
        Assert.Equal(1, summary.SkipReasons?.GlobExcluded);
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
        public int EarlySkippedFiles => 0;
        public int EarlySkippedTooLargeFiles => 0;
        public int EarlyExcludedByExtensionFiles => 0;

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
        public int EarlySkippedFiles => 0;
        public int EarlySkippedTooLargeFiles => 0;
        public int EarlyExcludedByExtensionFiles => 0;

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

[Collection("FileListerBackend")]
public class SearchServiceClampTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public SearchServiceClampTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-clamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "match");
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

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

[Collection("FileListerBackend")]
public class SearchServiceFlushBatchTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public SearchServiceFlushBatchTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

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

[Collection("FileListerBackend")]
public class SearchServiceExtraTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public SearchServiceExtraTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-svc-extra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

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
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) matches++;
            if (evt is SearchEvent.MatchBatch mb) matches += mb.Results.Count;
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }
        Assert.Equal(1, matches);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.FilesWithMatches);
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

// ─── SearchProgress ─────────────────────────────────────────────────────

public class SearchProgressCoverageTests
{
    [Fact]
    public void AllProperties_Accessible()
    {
        var elapsed = TimeSpan.FromSeconds(5);
        var sp = new SearchProgress(100, 200, 50, 30, 10, 1024L * 1024, elapsed, 3);

        Assert.Equal(100, sp.FilesScanned);
        Assert.Equal(200, sp.TotalFiles);
        Assert.Equal(50, sp.MatchesFound);
        Assert.Equal(30, sp.FilesWithMatches);
        Assert.Equal(10, sp.FilesSkipped);
        Assert.Equal(1024L * 1024, sp.BytesScanned);
        Assert.Equal(elapsed, sp.Elapsed);
        Assert.Equal(3, sp.AccessDenied);
    }

    [Fact]
    public void DefaultAccessDenied_IsZero()
    {
        var sp = new SearchProgress(0, 0, 0, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, sp.AccessDenied);
    }

    [Fact]
    public void RecordEquality()
    {
        var a = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.FromSeconds(1), 7);
        var b = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.FromSeconds(1), 7);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Deconstruction()
    {
        var sp = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.FromSeconds(7), 8);
        var (fs, tf, mf, fwm, fsk, bs, el, ad, sr) = sp;
        Assert.Equal(1, fs);
        Assert.Equal(2, tf);
        Assert.Equal(3, mf);
        Assert.Equal(4, fwm);
        Assert.Equal(5, fsk);
        Assert.Equal(6L, bs);
        Assert.Equal(TimeSpan.FromSeconds(7), el);
        Assert.Equal(8, ad);
        Assert.Null(sr);
    }
}

// ─── SearchSummary + SkipBreakdown ──────────────────────────────────────

public class SearchSummaryCoverageTests
{
    [Fact]
    public void AllProperties_Accessible()
    {
        var skip = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var ss = new SearchSummary(
            TotalFiles: 100,
            FilesScanned: 80,
            FilesSkipped: 20,
            FilesWithMatches: 50,
            TotalMatches: 300,
            BytesScanned: 999_999,
            Elapsed: TimeSpan.FromMinutes(1),
            Cancelled: true,
            Truncated: true,
            Degraded: true,
            FallbackReason: "Everything not running",
            SkipReasons: skip);

        Assert.Equal(100, ss.TotalFiles);
        Assert.Equal(80, ss.FilesScanned);
        Assert.Equal(20, ss.FilesSkipped);
        Assert.Equal(50, ss.FilesWithMatches);
        Assert.Equal(300, ss.TotalMatches);
        Assert.Equal(999_999L, ss.BytesScanned);
        Assert.Equal(TimeSpan.FromMinutes(1), ss.Elapsed);
        Assert.True(ss.Cancelled);
        Assert.True(ss.Truncated);
        Assert.True(ss.Degraded);
        Assert.Equal("Everything not running", ss.FallbackReason);
        Assert.NotNull(ss.SkipReasons);
        Assert.Equal(1, ss.SkipReasons!.Binary);
        Assert.Equal(2, ss.SkipReasons.AccessDenied);
        Assert.Equal(3, ss.SkipReasons.IOError);
        Assert.Equal(4, ss.SkipReasons.TooLarge);
        Assert.Equal(5, ss.SkipReasons.NotFound);
        Assert.Equal(6, ss.SkipReasons.Encoding);
        Assert.Equal(7, ss.SkipReasons.Other);
        Assert.Equal(8, ss.SkipReasons.ByExtension);
        Assert.Equal(9, ss.SkipReasons.Directories);
        Assert.Equal(10, ss.SkipReasons.EarlyFiltered);
        Assert.Equal(11, ss.SkipReasons.GlobExcluded);
    }

    [Fact]
    public void NullFallbackReason_And_NullSkipReasons()
    {
        var ss = new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null);
        Assert.Null(ss.FallbackReason);
        Assert.Null(ss.SkipReasons);
    }

    [Fact]
    public void SkipBreakdown_DefaultOptionalParams()
    {
        var sb = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7);
        Assert.Equal(0, sb.ByExtension);
        Assert.Equal(0, sb.Directories);
        Assert.Equal(0, sb.EarlyFiltered);
        Assert.Equal(0, sb.GlobExcluded);
    }

    [Fact]
    public void SkipBreakdown_ToString_ContainsAllFields()
    {
        var sb = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var str = sb.ToString();
        Assert.Contains("binary=1", str);
        Assert.Contains("accessDenied=2", str);
        Assert.Contains("ioError=3", str);
        Assert.Contains("tooLarge=4", str);
        Assert.Contains("notFound=5", str);
        Assert.Contains("encoding=6", str);
        Assert.Contains("other=7", str);
        Assert.Contains("byExtension=8", str);
        Assert.Contains("directories=9", str);
        Assert.Contains("earlyFiltered=10", str);
        Assert.Contains("globExcluded=11", str);
    }

    [Fact]
    public void SkipBreakdown_RecordEquality()
    {
        var a = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var b = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SearchSummary_RecordEquality()
    {
        var a = new SearchSummary(1, 2, 3, 4, 5, 6, TimeSpan.Zero, false, false, false, "reason");
        var b = new SearchSummary(1, 2, 3, 4, 5, 6, TimeSpan.Zero, false, false, false, "reason");
        Assert.Equal(a, b);
    }
}

// ─── ComputeAutoProcessMemoryCap ────────────────────────────────────

public class ComputeAutoProcessMemoryCapTests
{
    [Fact]
    public void SixteenGB_ReturnsHalf()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(16UL * 1024 * 1024 * 1024);
        Assert.Equal(8L * 1024 * 1024 * 1024, result);
    }

    [Fact]
    public void FourGB_ReturnsFloor()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(4UL * 1024 * 1024 * 1024);
        Assert.Equal(2L * 1024 * 1024 * 1024, result);
    }

    [Fact]
    public void OneGB_ReturnsFloor()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(1UL * 1024 * 1024 * 1024);
        Assert.Equal(2L * 1024 * 1024 * 1024, result);
    }

    [Fact]
    public void Zero_ReturnsFloor()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(0);
        Assert.Equal(2L * 1024 * 1024 * 1024, result);
    }
}

// ─── IsMemoryPressureHighForSnapshot ────────────────────────────────

public class IsMemoryPressureHighForSnapshotTests
{
    [Fact]
    public void WorkingSetExceedsCap_ReturnsTrue()
    {
        Assert.True(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 5_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: false, systemLoadPercent: 0,
            pressurePercent: 80, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void SystemLoadExceedsThreshold_ReturnsTrue()
    {
        Assert.True(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 85,
            pressurePercent: 80, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void SystemLoadBelowThreshold_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 50,
            pressurePercent: 80, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void GcFallback_HighLoad_ReturnsTrue()
    {
        Assert.True(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: false, systemLoadPercent: 0,
            pressurePercent: 80,
            gcMemoryLoadBytes: 9_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void GcFallback_LowLoad_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: false, systemLoadPercent: 0,
            pressurePercent: 80,
            gcMemoryLoadBytes: 1_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void PressureDisabled_Zero_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 99,
            pressurePercent: 0, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void PressureAbove100_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 99,
            pressurePercent: 101, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }
}

// ─── IsMemoryPressureRelievedGcFallback ─────────────────────────────

public class IsMemoryPressureRelievedGcFallbackTests
{
    [Fact]
    public void Relieved_ReturnsTrue()
    {
        // Process ws well below cap, GC load well below threshold
        Assert.True(SearchService.IsMemoryPressureRelievedGcFallback(
            workingSetBytes: 500_000_000, effectiveProcessCapBytes: 4_000_000_000,
            pressurePercent: 80, recoveryMarginPercent: 10,
            gcMemoryLoadBytes: 5_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void ProcessNotRelieved_ReturnsFalse()
    {
        // Working set above cap * recovery ratio
        Assert.False(SearchService.IsMemoryPressureRelievedGcFallback(
            workingSetBytes: 4_000_000_000, effectiveProcessCapBytes: 4_000_000_000,
            pressurePercent: 80, recoveryMarginPercent: 10,
            gcMemoryLoadBytes: 1_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void GcAboveRelief_ReturnsFalse()
    {
        // Process relieved but GC memory load too high
        Assert.False(SearchService.IsMemoryPressureRelievedGcFallback(
            workingSetBytes: 500_000_000, effectiveProcessCapBytes: 4_000_000_000,
            pressurePercent: 80, recoveryMarginPercent: 10,
            gcMemoryLoadBytes: 9_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }
}

// ─── SearchEvent record construction ────────────────────────────────────

public class SearchEventTests
{
    [Fact]
    public void MemoryPressure_RoundTrips()
    {
        int acked = 0;
        var evt = new SearchEvent.MemoryPressure(n => acked = n, ThresholdPercent: 85, Diagnostics: "diag");
        Assert.Equal(85, evt.ThresholdPercent);
        Assert.Equal("diag", evt.Diagnostics);
        evt.AcknowledgeEviction(42);
        Assert.Equal(42, acked);
    }

    [Fact]
    public void MemoryPressureRelieved_RoundTrips()
    {
        var evt = new SearchEvent.MemoryPressureRelieved(Diagnostics: "relieved");
        Assert.Equal("relieved", evt.Diagnostics);
    }

    [Fact]
    public void MemoryPressureRelieved_NullDiagnostics()
    {
        var evt = new SearchEvent.MemoryPressureRelieved();
        Assert.Null(evt.Diagnostics);
    }
}

// ─── SearchProgress: SkipReasons field ──────────────────────────────────

public class SearchProgressSkipReasonsTests
{
    [Fact]
    public void SkipReasons_DefaultsToNull()
    {
        var sp = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.Zero, 7);
        Assert.Null(sp.SkipReasons);
    }

    [Fact]
    public void SkipReasons_RoundTrips()
    {
        var breakdown = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var sp = new SearchProgress(10, 20, 30, 40, 50, 60, TimeSpan.FromSeconds(1), 70, breakdown);
        Assert.NotNull(sp.SkipReasons);
        Assert.Equal(10, sp.SkipReasons!.EarlyFiltered);
        Assert.Equal(11, sp.SkipReasons.GlobExcluded);
    }

    [Fact]
    public void Deconstruction_WithSkipReasons()
    {
        var breakdown = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var sp = new SearchProgress(10, 20, 30, 40, 50, 60, TimeSpan.FromSeconds(1), 70, breakdown);
        var (fs, tf, mf, fwm, fsk, bs, el, ad, sr) = sp;
        Assert.Equal(10, fs);
        Assert.Equal(70, ad);
        Assert.NotNull(sr);
        Assert.Equal(10, sr!.EarlyFiltered);
    }

    [Fact]
    public void Equality_WithSkipReasons()
    {
        var b = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var a1 = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.Zero, 7, b);
        var a2 = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.Zero, 7, b);
        Assert.Equal(a1, a2);
    }
}

// ─── SearchService: early-skip accounting ───────────────────────────────

[Collection("FileListerBackend")]
public class SearchServiceEarlySkipTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public SearchServiceEarlySkipTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-early-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public async Task EarlySkips_IncludedInSummaryFilesSkipped()
    {
        Write("a.txt", "needle");

        var lister = new EarlySkippedFileLister(
            [Path.Combine(_root, "a.txt")],
            earlySkippedFiles: 5,
            knownTotalFiles: 6);
        var svc = new SearchService(lister, new ContentSearcher());
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
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.NotNull(summary);
        // FilesSkipped should include the early-skipped files
        Assert.True(summary!.FilesSkipped >= 5, $"FilesSkipped={summary.FilesSkipped} should be >= 5 (earlySkips)");
        Assert.Equal(5, summary.SkipReasons?.EarlyFiltered);
        Assert.Equal(5, summary.SkipReasons?.TooLarge);
    }

    [Fact]
    public async Task EarlySkips_SubtractedFromKnownTotalForProgress()
    {
        Write("a.txt", "needle");

        var lister = new EarlySkippedFileLister(
            [Path.Combine(_root, "a.txt")],
            earlySkippedFiles: 10,
            knownTotalFiles: 100);
        var svc = new SearchService(lister, new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchProgress? lastProgress = null;
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Progress p) lastProgress = p.Snapshot;
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.NotNull(summary);
        // TotalFiles = max(effectiveKnown=90, discovered=1, scanned=1) = 90
        Assert.Equal(90, summary!.TotalFiles);
    }

    [Fact]
    public async Task ProgressSnapshot_ContainsFullSkipBreakdown()
    {
        Write("a.txt", "needle");
        Write("skip.cs", "needle");

        var files = new[]
        {
            Path.Combine(_root, "a.txt"),
            Path.Combine(_root, "skip.cs"),
        };
        var lister = new EarlySkippedFileLister(files, earlySkippedFiles: 3, knownTotalFiles: 5);
        var svc = new SearchService(lister, new ContentSearcher());
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            IncludeGlobs = ["*.txt"],
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.NotNull(summary);
        Assert.NotNull(summary!.SkipReasons);
        Assert.Equal(3, summary.SkipReasons!.EarlyFiltered);
        Assert.Equal(3, summary.SkipReasons.TooLarge);
        Assert.Equal(1, summary.SkipReasons.GlobExcluded); // skip.cs excluded by glob
    }

    private sealed class EarlySkippedFileLister(
        IReadOnlyList<string> files,
        int earlySkippedFiles,
        int knownTotalFiles = 0) : IFileLister
    {
        public string? FallbackReason => null;
        public int SkippedDirectories => 0;
        public int AccessDeniedDirectories => 0;
        public int KnownTotalFiles { get; } = knownTotalFiles;
        public int EarlySkippedFiles { get; } = earlySkippedFiles;
        public int EarlySkippedTooLargeFiles { get; } = earlySkippedFiles;
        public int EarlyExcludedByExtensionFiles => 0;

        public async IAsyncEnumerable<string> ListFilesAsync(
            string directory,
            IReadOnlyList<string> includeExtensions,
            int maxFiles,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var f in files) yield return f;
        }
    }
}

// ─── SearchOptions: SdkChannelBufferSize ────────────────────────────────

public class SearchOptionsSdkChannelBufferTests
{
    [Fact]
    public void SdkChannelBufferSize_DefaultIs4096()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x" };
        Assert.Equal(4096, opts.SdkChannelBufferSize);
    }

    [Fact]
    public void SdkChannelBufferSize_CanBeSet()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x", SdkChannelBufferSize = 512 };
        Assert.Equal(512, opts.SdkChannelBufferSize);
    }
}
