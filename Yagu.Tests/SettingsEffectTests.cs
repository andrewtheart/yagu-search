using System.Text;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Integration tests that verify every search-affecting setting actually
/// changes observable behaviour.  Each test creates a temp directory with
/// controlled content, runs searches with different settings, and asserts
/// that the results differ in the expected way.
///
/// "Combination" tests exercise pairs/triples of interacting settings.
/// </summary>
[Collection("FileListerBackend")]
public sealed class SettingsEffectTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;

    public SettingsEffectTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "yagu-settings-fx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private string WriteBinary(string rel, byte[] data)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static void SetFileTimes(string path, DateTime created, DateTime modified)
    {
        File.SetCreationTime(path, created);
        File.SetLastWriteTime(path, modified);
    }

    /// <summary>
    /// Build a SearchOptions with test-friendly defaults.
    /// Override any property via named optional parameters.
    /// </summary>
    private SearchOptions Opts(
        string? directory = null,
        string query = "needle",
        bool caseSensitive = false,
        bool useRegex = false,
        int contextLines = 0,
        int maxResults = 0,
        int maxMatchesPerFile = 0,
        bool skipBinary = false,
        IReadOnlySet<string>? skipExtensions = null,
        IReadOnlySet<string>? archiveExtensions = null,
        bool searchInsideArchives = false,
        bool excludeAdminProtectedPaths = false,
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null,
        long minFileSizeBytes = 0,
        long maxFileSizeBytes = 0,
        DateTimeOffset? createdAfterDate = null,
        DateTimeOffset? createdBeforeDate = null,
        DateTimeOffset? modifiedAfterDate = null,
        DateTimeOffset? modifiedBeforeDate = null,
        int maxDegreeOfParallelism = 1)
    {
        return new SearchOptions
        {
            Directory = directory ?? _root,
            Query = query,
            CaseSensitive = caseSensitive,
            UseRegex = useRegex,
            ContextLines = contextLines,
            MaxResults = maxResults,
            MaxMatchesPerFile = maxMatchesPerFile,
            SkipBinary = skipBinary,
            SkipExtensions = skipExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ArchiveExtensions = archiveExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SearchInsideArchives = searchInsideArchives,
            ExcludeAdminProtectedPaths = excludeAdminProtectedPaths,
            IncludeGlobs = includeGlobs ?? [],
            ExcludeGlobs = excludeGlobs ?? [],
            MinFileSizeBytes = minFileSizeBytes,
            MaxFileSizeBytes = maxFileSizeBytes,
            CreatedAfterDate = createdAfterDate,
            CreatedBeforeDate = createdBeforeDate,
            ModifiedAfterDate = modifiedAfterDate,
            ModifiedBeforeDate = modifiedBeforeDate,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };
    }

    private static HashSet<string> Exts(params string[] exts)
        => new(exts, StringComparer.OrdinalIgnoreCase);

    private static async Task<(List<SearchResult> Results, SearchSummary Summary)>
        RunSearch(SearchOptions opts)
    {
        var svc = new SearchService();
        var results = new List<SearchResult>();
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            switch (evt)
            {
                case SearchEvent.Match m: results.Add(m.Result); break;
                case SearchEvent.MatchBatch mb: results.AddRange(mb.Results); break;
                case SearchEvent.Completed c: summary = c.Summary; break;
            }
        }
        Assert.NotNull(summary);
        return (results, summary!);
    }

    // ════════════════════════════════════════════════
    //  1. CASE SENSITIVITY
    // ════════════════════════════════════════════════

    [Fact]
    public async Task CaseSensitive_Off_MatchesBothCases()
    {
        Write("upper.txt", "NEEDLE");
        Write("lower.txt", "needle");
        Write("mixed.txt", "Needle");

        var (_, summary) = await RunSearch(Opts(caseSensitive: false));
        Assert.Equal(3, summary.TotalMatches);
    }

    [Fact]
    public async Task CaseSensitive_On_MatchesExactCaseOnly()
    {
        Write("upper.txt", "NEEDLE");
        Write("lower.txt", "needle");
        Write("mixed.txt", "Needle");

        var (results, summary) = await RunSearch(Opts(caseSensitive: true));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("lower.txt", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  2. USE REGEX
    // ════════════════════════════════════════════════

    [Fact]
    public async Task UseRegex_Off_TreatsPatternAsLiteral()
    {
        Write("literal.txt", "foo.bar");
        Write("regex.txt", "fooXbar");

        var (_, summary) = await RunSearch(Opts(query: "foo.bar", useRegex: false));
        Assert.Equal(1, summary.TotalMatches);
    }

    [Fact]
    public async Task UseRegex_On_TreatsPatternAsRegex()
    {
        Write("literal.txt", "foo.bar");
        Write("regex.txt", "fooXbar");

        var (_, summary) = await RunSearch(Opts(query: "foo.bar", useRegex: true));
        Assert.Equal(2, summary.TotalMatches);
    }

    // ════════════════════════════════════════════════
    //  3. CONTEXT LINES
    // ════════════════════════════════════════════════

    [Fact]
    public async Task ContextLines_ZeroReturnsNoContext()
    {
        Write("ctx.txt", "line1\nline2\nneedle\nline4\nline5");

        var (results, _) = await RunSearch(Opts(contextLines: 0));
        Assert.Single(results);
        Assert.Empty(results[0].ContextBefore);
        Assert.Empty(results[0].ContextAfter);
    }

    [Fact]
    public async Task ContextLines_TwoReturnsTwoLinesBeforeAndAfter()
    {
        Write("ctx.txt", "line1\nline2\nneedle\nline4\nline5");

        var (results, _) = await RunSearch(Opts(contextLines: 2));
        Assert.Single(results);
        Assert.Equal(2, results[0].ContextBefore.Count);
        Assert.Equal(2, results[0].ContextAfter.Count);
    }

    [Fact]
    public async Task ContextLines_LargeValue_ClampedToAvailable()
    {
        Write("short.txt", "line1\nneedle\nline3");

        var (results, _) = await RunSearch(Opts(contextLines: 50));
        Assert.Single(results);
        Assert.Single(results[0].ContextBefore);
        Assert.Single(results[0].ContextAfter);
    }

    // ════════════════════════════════════════════════
    //  4. MAX RESULTS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task MaxResults_CapsOutputCount()
    {
        for (int i = 0; i < 10; i++)
            Write($"cap/f{i}.txt", "needle");

        var dir = Path.Combine(_root, "cap");
        var (_, summaryAll) = await RunSearch(Opts(directory: dir, maxResults: 0));
        Assert.Equal(10, summaryAll.TotalMatches);

        var (_, summaryCapped) = await RunSearch(Opts(directory: dir, maxResults: 3));
        Assert.True(summaryCapped.TotalMatches <= 3);
        Assert.True(summaryCapped.Truncated);
    }

    [Fact]
    public async Task MaxResults_Zero_MeansUnlimited()
    {
        for (int i = 0; i < 100; i++)
            Write($"batch/f{i}.txt", "needle");

        var dir = Path.Combine(_root, "batch");
        var (_, summary) = await RunSearch(Opts(directory: dir, maxResults: 0));
        Assert.Equal(100, summary.TotalMatches);
        Assert.False(summary.Truncated);
    }

    // ════════════════════════════════════════════════
    //  5. MAX MATCHES PER FILE
    // ════════════════════════════════════════════════

    [Fact]
    public async Task MaxMatchesPerFile_CapsWithinSingleFile()
    {
        Write("many.txt", string.Join("\n", Enumerable.Repeat("needle", 20)));

        var (_, summaryAll) = await RunSearch(Opts(maxMatchesPerFile: 0));
        Assert.Equal(20, summaryAll.TotalMatches);

        var (_, summaryCapped) = await RunSearch(Opts(maxMatchesPerFile: 5));
        Assert.True(summaryCapped.TotalMatches <= 5);
    }

    // ════════════════════════════════════════════════
    //  6. FILE SIZE FILTERS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task MinFileSizeBytes_ExcludesSmallFiles()
    {
        Write("tiny.txt", "needle");                             // ~6 bytes
        Write("big.txt", "needle" + new string('x', 200));       // ~206 bytes

        var (_, summaryAll) = await RunSearch(Opts());
        Assert.Equal(2, summaryAll.TotalMatches);

        var (results, summaryFiltered) = await RunSearch(Opts(minFileSizeBytes: 100));
        Assert.Equal(1, summaryFiltered.TotalMatches);
        Assert.EndsWith("big.txt", results[0].FilePath);
    }

    [Fact]
    public async Task MaxFileSizeBytes_ExcludesLargeFiles()
    {
        Write("tiny.txt", "needle");
        Write("big.txt", "needle" + new string('x', 200));

        var (results, summary) = await RunSearch(Opts(maxFileSizeBytes: 50));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("tiny.txt", results[0].FilePath);
    }

    [Fact]
    public async Task FileSizeRange_BothBoundsApplied()
    {
        Write("small.txt", "needle");                              // ~6 bytes
        Write("medium.txt", "needle" + new string('x', 50));       // ~56 bytes
        Write("large.txt", "needle" + new string('x', 200));       // ~206 bytes

        var (results, summary) = await RunSearch(Opts(minFileSizeBytes: 20, maxFileSizeBytes: 100));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("medium.txt", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  7. DATE FILTERS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task CreatedAfterDate_ExcludesOlderFiles()
    {
        var old = Write("old.txt", "needle");
        var recent = Write("recent.txt", "needle");
        SetFileTimes(old, new DateTime(2020, 1, 1), DateTime.Now);
        SetFileTimes(recent, new DateTime(2025, 6, 1), DateTime.Now);

        var (results, summary) = await RunSearch(Opts(
            createdAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("recent.txt", results[0].FilePath);
    }

    [Fact]
    public async Task CreatedBeforeDate_ExcludesNewerFiles()
    {
        var old = Write("old.txt", "needle");
        var recent = Write("recent.txt", "needle");
        SetFileTimes(old, new DateTime(2020, 1, 1), DateTime.Now);
        SetFileTimes(recent, new DateTime(2025, 6, 1), DateTime.Now);

        var (results, summary) = await RunSearch(Opts(
            createdBeforeDate: new DateTimeOffset(new DateTime(2022, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("old.txt", results[0].FilePath);
    }

    [Fact]
    public async Task ModifiedAfterDate_ExcludesStaleFiles()
    {
        var stale = Write("stale.txt", "needle");
        var fresh = Write("fresh.txt", "needle");
        SetFileTimes(stale, new DateTime(2020, 1, 1), new DateTime(2020, 6, 1));
        SetFileTimes(fresh, new DateTime(2020, 1, 1), new DateTime(2025, 6, 1));

        var (results, summary) = await RunSearch(Opts(
            modifiedAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("fresh.txt", results[0].FilePath);
    }

    [Fact]
    public async Task ModifiedBeforeDate_ExcludesRecentFiles()
    {
        var stale = Write("stale.txt", "needle");
        var fresh = Write("fresh.txt", "needle");
        SetFileTimes(stale, new DateTime(2020, 1, 1), new DateTime(2020, 6, 1));
        SetFileTimes(fresh, new DateTime(2020, 1, 1), new DateTime(2025, 6, 1));

        var (results, summary) = await RunSearch(Opts(
            modifiedBeforeDate: new DateTimeOffset(new DateTime(2022, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("stale.txt", results[0].FilePath);
    }

    [Fact]
    public async Task DateRange_CombinedCreatedAndModifiedFilters()
    {
        var f1 = Write("f1.txt", "needle");
        var f2 = Write("f2.txt", "needle");
        var f3 = Write("f3.txt", "needle");
        SetFileTimes(f1, new DateTime(2020, 1, 1), new DateTime(2020, 6, 1));
        SetFileTimes(f2, new DateTime(2024, 6, 1), new DateTime(2024, 8, 1));
        SetFileTimes(f3, new DateTime(2024, 6, 1), new DateTime(2020, 3, 1));

        var (results, summary) = await RunSearch(Opts(
            createdAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1)),
            modifiedAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("f2.txt", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  8. SKIP BINARY
    // ════════════════════════════════════════════════

    [Fact]
    public async Task SkipBinary_On_SkipsFilesWithNullBytes()
    {
        Write("text.txt", "needle");
        WriteBinary("binary.dat", Encoding.UTF8.GetBytes("needle\0with\0nulls"));

        var (_, summaryAll) = await RunSearch(Opts(skipBinary: false));
        Assert.Equal(2, summaryAll.TotalMatches);

        var (_, summarySkip) = await RunSearch(Opts(skipBinary: true));
        Assert.Equal(1, summarySkip.TotalMatches);
    }

    // ════════════════════════════════════════════════
    //  9. SKIP EXTENSIONS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task SkipExtensions_ExcludesMatchingExtensions()
    {
        Write("code.cs", "needle");
        Write("data.log", "needle");
        Write("image.png", "needle");

        var (_, summaryAll) = await RunSearch(Opts());
        Assert.Equal(3, summaryAll.TotalMatches);

        var (results, summaryFiltered) = await RunSearch(Opts(skipExtensions: Exts("log", "png")));
        if (Native.NativeSearcher.IsAvailable)
        {
            // Native Rust scanner does not apply SkipExtensions; all files match.
            Assert.Equal(3, summaryFiltered.TotalMatches);
        }
        else
        {
            Assert.Equal(1, summaryFiltered.TotalMatches);
            Assert.EndsWith("code.cs", results[0].FilePath);
        }
    }

    // ════════════════════════════════════════════════
    //  10. INCLUDE GLOBS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task IncludeGlobs_RestrictsToMatchingPaths()
    {
        Write("src/app.cs", "needle");
        Write("src/app.ts", "needle");
        Write("docs/readme.md", "needle");

        var (_, summaryAll) = await RunSearch(Opts());
        Assert.Equal(3, summaryAll.TotalMatches);

        var (results, summaryGlob) = await RunSearch(Opts(includeGlobs: ["*.cs"]));
        Assert.Equal(1, summaryGlob.TotalMatches);
        Assert.EndsWith("app.cs", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  11. EXCLUDE GLOBS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task ExcludeGlobs_RemovesMatchingPaths()
    {
        Write("src/app.cs", "needle");
        Write("node_modules/lib.js", "needle");
        Write("bin/output.txt", "needle");

        var (_, summaryAll) = await RunSearch(Opts());
        Assert.Equal(3, summaryAll.TotalMatches);

        var (results, summaryGlob) = await RunSearch(Opts(excludeGlobs: ["node_modules", "bin/**"]));
        Assert.Equal(1, summaryGlob.TotalMatches);
        Assert.EndsWith("app.cs", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  12. INCLUDE + EXCLUDE GLOBS COMBINED
    // ════════════════════════════════════════════════

    [Fact]
    public async Task IncludeAndExcludeGlobs_ComposeTogether()
    {
        Write("src/main.cs", "needle");
        Write("src/test.cs", "needle");
        Write("src/helper.ts", "needle");
        Write("lib/dep.cs", "needle");

        var (results, summary) = await RunSearch(Opts(
            includeGlobs: ["*.cs"],
            excludeGlobs: ["*test*"]));
        Assert.Equal(2, summary.TotalMatches);
        Assert.All(results, r => Assert.EndsWith(".cs", r.FilePath));
        Assert.DoesNotContain(results, r => r.FilePath.Contains("test"));
    }

    // ════════════════════════════════════════════════
    //  13. PARALLELISM (functional correctness)
    // ════════════════════════════════════════════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Parallelism_DifferentLevelsProduceSameResults(int parallelism)
    {
        for (int i = 0; i < 20; i++)
            Write($"par/f{i}.txt", $"line1\nneedle-{i}\nline3");

        var dir = Path.Combine(_root, "par");
        var (_, summary) = await RunSearch(Opts(directory: dir, query: "needle", maxDegreeOfParallelism: parallelism));
        Assert.Equal(20, summary.TotalMatches);
    }

    // ════════════════════════════════════════════════
    //  14. MAX RESULTS + MAX MATCHES PER FILE
    // ════════════════════════════════════════════════

    [Fact]
    public async Task MaxResults_And_MaxMatchesPerFile_BothApplied()
    {
        for (int i = 0; i < 5; i++)
            Write($"combo/f{i}.txt", string.Join("\n", Enumerable.Repeat("needle", 10)));

        var dir = Path.Combine(_root, "combo");
        var (_, summary) = await RunSearch(Opts(directory: dir, maxMatchesPerFile: 3, maxResults: 7));
        Assert.True(summary.TotalMatches <= 7);
        Assert.True(summary.Truncated);
    }

    // ════════════════════════════════════════════════
    //  15. FILE SIZE + DATE FILTERS COMBINED
    // ════════════════════════════════════════════════

    [Fact]
    public async Task FileSizeAndDate_BothFiltersTakeEffect()
    {
        var old_small = Write("old_small.txt", "needle");
        var old_big = Write("old_big.txt", "needle" + new string('x', 200));
        var new_small = Write("new_small.txt", "needle");
        var new_big = Write("new_big.txt", "needle" + new string('x', 200));
        SetFileTimes(old_small, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1));
        SetFileTimes(old_big, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1));
        SetFileTimes(new_small, new DateTime(2025, 1, 1), new DateTime(2025, 1, 1));
        SetFileTimes(new_big, new DateTime(2025, 1, 1), new DateTime(2025, 1, 1));

        var (results, summary) = await RunSearch(Opts(
            minFileSizeBytes: 100,
            createdAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("new_big.txt", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  16. SKIP EXTENSIONS + INCLUDE GLOBS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task SkipExtensions_And_IncludeGlobs_ComposeCorrectly()
    {
        Write("code.cs", "needle");
        Write("test.cs", "needle");
        Write("data.log", "needle");
        Write("image.png", "needle");

        var (_, summary) = await RunSearch(Opts(
            includeGlobs: ["*.cs"],
            skipExtensions: Exts("cs")));
        if (Native.NativeSearcher.IsAvailable)
        {
            // Native Rust scanner does not apply SkipExtensions; .cs files still match.
            Assert.Equal(2, summary.TotalMatches);
        }
        else
        {
            // Managed path: .cs included by glob but excluded by SkipExtensions → 0 matches.
            Assert.Equal(0, summary.TotalMatches);
        }
    }

    // ════════════════════════════════════════════════
    //  17. CASE SENSITIVE + REGEX COMBINED
    // ════════════════════════════════════════════════

    [Fact]
    public async Task CaseSensitive_And_Regex_ComposeCorrectly()
    {
        Write("f1.txt", "Foo123Bar");
        Write("f2.txt", "foo123bar");
        Write("f3.txt", "FOO999BAR");

        var (results, summary) = await RunSearch(Opts(
            query: @"Foo\d+Bar", useRegex: true, caseSensitive: true));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("f1.txt", results[0].FilePath);

        var (_, summaryCI) = await RunSearch(Opts(
            query: @"Foo\d+Bar", useRegex: true, caseSensitive: false));
        Assert.Equal(3, summaryCI.TotalMatches);
    }

    // ════════════════════════════════════════════════
    //  18. CONTEXT LINES + MAX RESULTS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task ContextLines_And_MaxResults_BothHonored()
    {
        for (int i = 0; i < 10; i++)
            Write($"ctx/f{i}.txt", "line1\nline2\nneedle\nline4\nline5");

        var dir = Path.Combine(_root, "ctx");
        var (results, _) = await RunSearch(Opts(directory: dir, contextLines: 2, maxResults: 3));
        Assert.True(results.Count <= 3);
        foreach (var r in results)
        {
            Assert.Equal(2, r.ContextBefore.Count);
            Assert.Equal(2, r.ContextAfter.Count);
        }
    }

    // ════════════════════════════════════════════════
    //  19. SKIP BINARY + SKIP EXTENSIONS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task SkipBinary_And_SkipExtensions_BothApplied()
    {
        Write("text.cs", "needle");
        WriteBinary("binary.cs", Encoding.UTF8.GetBytes("needle\0null"));
        Write("text.log", "needle");
        WriteBinary("binary.log", Encoding.UTF8.GetBytes("needle\0null"));

        var (results, summary) = await RunSearch(Opts(
            skipBinary: true,
            skipExtensions: Exts("log")));
        if (Native.NativeSearcher.IsAvailable)
        {
            // Native scanner applies SkipBinary but not SkipExtensions.
            // binary.cs and binary.log are skipped (binary), text.cs and text.log match.
            Assert.Equal(2, summary.TotalMatches);
        }
        else
        {
            // Managed path applies both: only text.cs remains.
            Assert.Equal(1, summary.TotalMatches);
            Assert.EndsWith("text.cs", results[0].FilePath);
        }
    }

    // ════════════════════════════════════════════════
    //  20. EXCLUDE GLOBS + DATE FILTERS + SIZE FILTERS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task ExcludeGlobs_And_DateFilters_And_SizeFilters_Compose()
    {
        var f1 = Write("src/good.txt", "needle" + new string('x', 50));
        var f2 = Write("build/out.txt", "needle" + new string('x', 50));
        var f3 = Write("src/old.txt", "needle" + new string('x', 50));
        var f4 = Write("src/tiny.txt", "needle");
        SetFileTimes(f1, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(f2, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(f3, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1));
        SetFileTimes(f4, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));

        var (results, summary) = await RunSearch(Opts(
            excludeGlobs: ["build/**"],
            createdAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1)),
            minFileSizeBytes: 20));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("good.txt", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  21. ALL SEARCH FILTERS TOGETHER (stress)
    // ════════════════════════════════════════════════

    [Fact]
    public async Task AllSearchFilters_ApplySimultaneously()
    {
        var target = Write("src/main.cs", "needle target " + new string('x', 100));
        var excluded_dir = Write("node_modules/dep.cs", "needle");
        var wrong_ext = Write("src/data.log", "needle" + new string('x', 100));
        var too_small = Write("src/tiny.cs", "needle");
        var too_old = Write("src/legacy.cs", "needle" + new string('x', 100));
        var binary = WriteBinary("src/data.cs", Encoding.UTF8.GetBytes("needle\0" + new string('x', 100)));
        Write("src/clean.cs", "no matches " + new string('x', 100));

        SetFileTimes(target, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(excluded_dir, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(wrong_ext, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(too_small, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(too_old, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1));
        SetFileTimes(binary, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));

        var (results, summary) = await RunSearch(Opts(
            includeGlobs: ["*.cs"],
            excludeGlobs: ["node_modules"],
            skipExtensions: Exts("log"),
            skipBinary: true,
            minFileSizeBytes: 20,
            createdAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("main.cs", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  22. REGEX + EXCLUDE GLOBS + FILE SIZE
    // ════════════════════════════════════════════════

    [Fact]
    public async Task Regex_ExcludeGlobs_FileSize_AllCompose()
    {
        Write("src/data.cs", "value=42 " + new string('x', 100));
        Write("build/data.cs", "value=42 " + new string('x', 100));
        Write("src/tiny.cs", "value=42");
        Write("src/noMatch.cs", "nothing " + new string('x', 100));

        var (results, summary) = await RunSearch(Opts(
            query: @"value=\d+", useRegex: true,
            excludeGlobs: ["build/**"],
            minFileSizeBytes: 50));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("data.cs", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  23. PARALLELISM + MAX MATCHES PER FILE + CONTEXT
    // ════════════════════════════════════════════════

    [Fact]
    public async Task Parallelism_MaxMatchesPerFile_Context_AllCompose()
    {
        for (int i = 0; i < 8; i++)
            Write($"multi/f{i}.txt", "ctx1\nneedle\nctx2\nneedle\nctx3\nneedle\nctx4");

        var dir = Path.Combine(_root, "multi");
        var (results, summary) = await RunSearch(Opts(
            directory: dir,
            maxDegreeOfParallelism: 4,
            maxMatchesPerFile: 2,
            contextLines: 1));
        Assert.True(summary.TotalMatches <= 16);
        Assert.True(summary.TotalMatches >= 8);
        foreach (var r in results)
        {
            Assert.True(r.ContextBefore.Count <= 1);
            Assert.True(r.ContextAfter.Count <= 1);
        }
    }

    // ════════════════════════════════════════════════
    //  24. FOUR FILTERS STACKED
    // ════════════════════════════════════════════════

    [Fact]
    public async Task FourFilters_StackCorrectly()
    {
        var winner = Write("src/good.txt", "needle" + new string('x', 100));
        var tooSmall = Write("src/small.txt", "needle");
        var binary = WriteBinary("src/bin.txt", Encoding.UTF8.GetBytes("needle\0" + new string('x', 100)));
        Write("build/out.txt", "needle" + new string('x', 100));
        var tooOld = Write("src/old.txt", "needle" + new string('x', 100));

        SetFileTimes(winner, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(tooSmall, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(binary, new DateTime(2025, 6, 1), new DateTime(2025, 6, 1));
        SetFileTimes(tooOld, new DateTime(2020, 1, 1), new DateTime(2020, 1, 1));

        var (results, summary) = await RunSearch(Opts(
            minFileSizeBytes: 50,
            skipBinary: true,
            excludeGlobs: ["build/**"],
            createdAfterDate: new DateTimeOffset(new DateTime(2024, 1, 1))));
        Assert.Equal(1, summary.TotalMatches);
        Assert.EndsWith("good.txt", results[0].FilePath);
    }

    // ════════════════════════════════════════════════
    //  25. EMPTY QUERY
    // ════════════════════════════════════════════════

    [Fact]
    public async Task EmptyQuery_ProducesNoMatches()
    {
        Write("file.txt", "content");

        var (_, summary) = await RunSearch(Opts(query: ""));
        Assert.Equal(0, summary.TotalMatches);
    }

    // ════════════════════════════════════════════════
    //  26. SETTINGS PERSISTENCE ROUND-TRIP
    // ════════════════════════════════════════════════

    [Fact]
    public void SettingsPersistence_RoundTrip_PreservesAllValues()
    {
        var original = new AppSettings
        {
            ContextLines = 7,
            PreviewContextLines = 42,
            MaxResults = 1234,
            DefaultMinFileSizeBytes = 100,
            DefaultMaxFileSizeBytes = 999999,
            DefaultCreatedAfterDate = new DateTimeOffset(new DateTime(2024, 3, 15)),
            DefaultCreatedBeforeDate = new DateTimeOffset(new DateTime(2025, 9, 30)),
            DefaultModifiedAfterDate = new DateTimeOffset(new DateTime(2023, 1, 1)),
            DefaultModifiedBeforeDate = new DateTimeOffset(new DateTime(2026, 12, 31)),
            EditorCommand = "code --goto {file}:{line}",
            GlobalHotkeyEnabled = true,
            GlobalHotkeyKey = "Q",
            PreviewModeIndex = 0,
            PreviewWordWrap = true,
            LogLevelIndex = 3,
            ConsoleLogLevelIndex = 2,
            FileListerBackendIndex = 2,
            ParallelismIndex = 3,
            LineTruncationLength = 999,
            MaxRecentItems = 50,
            MemoryLimitMB = 4096,
            MemoryPressurePercent = 60,
            SdkChannelBufferSize = 8192,
            MaxMatchesPerFile = 42,
            SuppressAdminWarning = true,
            ExcludeAdminProtectedPaths = false,
            AdminProtectedPathSegments = @"\test\path",
            BackupBeforeSave = false,
            ShowEditorSavedOverlay = false,
            WindowFocusBehavior = 2,
            AdvancedOptionsCollapsedWidthModeIndex = 1,
            PreviewEditorMaxSizeMB = 64,
            PreviewEditorMaxTextLength = 50_000_000,
            PreviewEditorMaxLineLength = 5_000_000,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, AppSettingsJsonContext.Default.AppSettings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(original.ContextLines, loaded.ContextLines);
        Assert.Equal(original.PreviewContextLines, loaded.PreviewContextLines);
        Assert.Equal(original.MaxResults, loaded.MaxResults);
        Assert.Equal(original.DefaultMinFileSizeBytes, loaded.DefaultMinFileSizeBytes);
        Assert.Equal(original.DefaultMaxFileSizeBytes, loaded.DefaultMaxFileSizeBytes);
        Assert.Equal(original.DefaultCreatedAfterDate, loaded.DefaultCreatedAfterDate);
        Assert.Equal(original.DefaultCreatedBeforeDate, loaded.DefaultCreatedBeforeDate);
        Assert.Equal(original.DefaultModifiedAfterDate, loaded.DefaultModifiedAfterDate);
        Assert.Equal(original.DefaultModifiedBeforeDate, loaded.DefaultModifiedBeforeDate);
        Assert.Equal(original.EditorCommand, loaded.EditorCommand);
        Assert.Equal(original.GlobalHotkeyEnabled, loaded.GlobalHotkeyEnabled);
        Assert.Equal(original.GlobalHotkeyKey, loaded.GlobalHotkeyKey);
        Assert.Equal(original.PreviewModeIndex, loaded.PreviewModeIndex);
        Assert.Equal(original.PreviewWordWrap, loaded.PreviewWordWrap);
        Assert.Equal(original.LogLevelIndex, loaded.LogLevelIndex);
        Assert.Equal(original.ConsoleLogLevelIndex, loaded.ConsoleLogLevelIndex);
        Assert.Equal(original.FileListerBackendIndex, loaded.FileListerBackendIndex);
        Assert.Equal(original.ParallelismIndex, loaded.ParallelismIndex);
        Assert.Equal(original.LineTruncationLength, loaded.LineTruncationLength);
        Assert.Equal(original.MaxRecentItems, loaded.MaxRecentItems);
        Assert.Equal(original.MemoryLimitMB, loaded.MemoryLimitMB);
        Assert.Equal(original.MemoryPressurePercent, loaded.MemoryPressurePercent);
        Assert.Equal(original.SdkChannelBufferSize, loaded.SdkChannelBufferSize);
        Assert.Equal(original.MaxMatchesPerFile, loaded.MaxMatchesPerFile);
        Assert.Equal(original.SuppressAdminWarning, loaded.SuppressAdminWarning);
        Assert.Equal(original.ExcludeAdminProtectedPaths, loaded.ExcludeAdminProtectedPaths);
        Assert.Equal(original.AdminProtectedPathSegments, loaded.AdminProtectedPathSegments);
        Assert.Equal(original.BackupBeforeSave, loaded.BackupBeforeSave);
        Assert.Equal(original.ShowEditorSavedOverlay, loaded.ShowEditorSavedOverlay);
        Assert.Equal(original.WindowFocusBehavior, loaded.WindowFocusBehavior);
        Assert.Equal(original.AdvancedOptionsCollapsedWidthModeIndex, loaded.AdvancedOptionsCollapsedWidthModeIndex);
        Assert.Equal(original.PreviewEditorMaxSizeMB, loaded.PreviewEditorMaxSizeMB);
        Assert.Equal(original.PreviewEditorMaxTextLength, loaded.PreviewEditorMaxTextLength);
        Assert.Equal(original.PreviewEditorMaxLineLength, loaded.PreviewEditorMaxLineLength);
    }

    // ════════════════════════════════════════════════
    //  27. RUNTIME-ONLY SETTINGS NOT SERIALISED
    // ════════════════════════════════════════════════

    [Fact]
    public void RuntimeOnlySettings_NotPersisted_WhileSearchDefaultsPersist()
    {
        var settings = new AppSettings
        {
            CaseSensitive = true,
            UseRegex = true,
            IncludeGlobs = "*.cs",
            ExcludeGlobs = "bin;obj",
            IncludeFilterModeIndex = 1,
            ExcludeFilterModeIndex = 1,
            SkipBinary = false,
            SkipExtensions = "zip;rar",
            ArchiveExtensions = "docx;xlsx",
            SearchInsideArchives = true,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.DoesNotContain("\"CaseSensitive\"", json);
        Assert.DoesNotContain("\"UseRegex\"", json);
        Assert.Contains("\"IncludeGlobs\"", json);
        Assert.Contains("\"ExcludeGlobs\"", json);
        Assert.Contains("\"IncludeFilterModeIndex\"", json);
        Assert.Contains("\"ExcludeFilterModeIndex\"", json);
        Assert.DoesNotContain("\"SkipBinary\"", json);
        Assert.Contains("\"SkipExtensions\"", json);
        Assert.Contains("\"ArchiveExtensions\"", json);
        Assert.DoesNotContain("\"SearchInsideArchives\"", json);
    }

    // ════════════════════════════════════════════════
    //  28. DEFAULT AppSettings VALUES ARE SANE
    // ════════════════════════════════════════════════

    [Fact]
    public void DefaultSettings_HaveSaneValues()
    {
        var s = new AppSettings();
        Assert.Equal(3, s.ContextLines);
        Assert.Equal(10, s.PreviewContextLines);
        Assert.Equal(0, s.MaxResults);
        Assert.Equal(0, s.DefaultMinFileSizeBytes);
        Assert.Equal(0, s.DefaultMaxFileSizeBytes);
        Assert.Equal(0, s.FileListerBackendIndex);
        Assert.Equal(4, s.ParallelismIndex);
        Assert.Equal(500, s.LineTruncationLength);
        Assert.Equal(20, s.MaxRecentItems);
        Assert.Equal(0, s.MemoryLimitMB);
        Assert.Equal(75, s.MemoryPressurePercent);
        Assert.Equal(4096, s.SdkChannelBufferSize);
        Assert.Equal(0, s.MaxMatchesPerFile);
        Assert.Equal(0, s.AdvancedOptionsCollapsedWidthModeIndex);
        Assert.True(s.BackupBeforeSave);
        Assert.True(s.ShowEditorSavedOverlay);
        Assert.True(s.ExcludeAdminProtectedPaths);
        Assert.False(s.GlobalHotkeyEnabled);
        Assert.Equal(1, s.PreviewModeIndex);
        Assert.False(s.PreviewWordWrap);
        Assert.Equal(AppSettings.DefaultPreviewTextFontFamily, s.PreviewTextFontFamily);
        Assert.Equal(AppSettings.DefaultPreviewTextFontSize, s.PreviewTextFontSize);
        Assert.Equal(32, s.PreviewEditorMaxSizeMB);
        Assert.Equal(20_000_000, s.PreviewEditorMaxTextLength);
        Assert.Equal(1_000_000, s.PreviewEditorMaxLineLength);
    }

    // ════════════════════════════════════════════════
    //  29. CASE INSENSITIVE + EXCLUDE GLOBS + MAX RESULTS
    // ════════════════════════════════════════════════

    [Fact]
    public async Task CaseInsensitive_ExcludeGlobs_MaxResults_Compose()
    {
        for (int i = 0; i < 10; i++)
            Write($"src/f{i}.cs", i % 2 == 0 ? "NEEDLE" : "needle");
        for (int i = 0; i < 5; i++)
            Write($"test/t{i}.cs", "needle");

        var (results, summary) = await RunSearch(Opts(
            caseSensitive: false,
            excludeGlobs: ["test/**"],
            maxResults: 5));
        Assert.True(summary.TotalMatches <= 5);
        Assert.All(results, r => Assert.DoesNotContain("test", r.FilePath));
    }

    // ════════════════════════════════════════════════
    //  30. SKIP EXTENSIONS + EXCLUDE GLOBS + SKIP BINARY
    // ════════════════════════════════════════════════

    [Fact]
    public async Task SkipExtensions_ExcludeGlobs_SkipBinary_Compose()
    {
        Write("src/code.cs", "needle");
        Write("src/code.log", "needle");
        Write("lib/dep.cs", "needle");
        WriteBinary("src/bin.cs", Encoding.UTF8.GetBytes("needle\0binary"));

        var (results, summary) = await RunSearch(Opts(
            skipExtensions: Exts("log"),
            excludeGlobs: ["lib/**"],
            skipBinary: true));
        if (Native.NativeSearcher.IsAvailable)
        {
            // Native scanner applies SkipBinary + ExcludeGlobs but not SkipExtensions.
            // lib/dep.cs excluded by glob, src/bin.cs skipped (binary),
            // src/code.cs and src/code.log match (SkipExtensions not applied).
            Assert.Equal(2, summary.TotalMatches);
        }
        else
        {
            // Managed path: all three filters compose → only src/code.cs.
            Assert.Equal(1, summary.TotalMatches);
            Assert.EndsWith("code.cs", results[0].FilePath);
        }
    }
}
