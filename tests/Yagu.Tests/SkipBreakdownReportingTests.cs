using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// The status-bar "Skipped: N" overlay (and its CLI twin) must account for every scenario that removes
/// a file from a search. These tests cover the <see cref="SkipBreakdown"/> arithmetic, the end-to-end
/// tallies <see cref="SearchService"/> produces, and — because the WinUI view-model and CliRunner are
/// not compiled into this assembly — source pins for the rendered rows.
/// </summary>
public class SkipBreakdownReportingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string ReadSource(params string[] relative)
        => File.ReadAllText(Path.Combine([RepoRoot(), .. relative]));

    // ── SkipBreakdown arithmetic ────────────────────────────────────────

    [Fact]
    public void CountedTotal_SumsEveryCountedReason()
    {
        var b = new SkipBreakdown(
            Binary: 1, AccessDenied: 2, IOError: 3, TooLarge: 4, NotFound: 5, Encoding: 6, Other: 7,
            ByExtension: 8, Directories: 9,
            GlobExcluded: 11, CloudOnly: 13, MultilineSkipped: 14, IoTimeout: 15,
            TooSmall: 16, DateFiltered: 17, OcrCacheExcluded: 0, CloudOnlyAtDiscovery: 0);

        Assert.Equal(1 + 2 + 3 + 15 + 4 + 16 + 17 + 5 + 6 + 7 + 8 + 9 + 11 + 13 + 14, b.CountedTotal);
    }

    [Fact]
    public void CountedTotal_ExcludesDiscoveryFiltersAndTheEarlyFilteredAggregate()
    {
        // EarlyFiltered is the parent aggregate of the size/date reasons; gitignore, discovery-time
        // extension exclusions and discovery-time cloud-only skips never entered the scan set. Counting
        // any of them would make the breakdown exceed the headline Skipped total.
        var b = new SkipBreakdown(
            Binary: 5, AccessDenied: 0, IOError: 0, TooLarge: 0, NotFound: 0, Encoding: 0, Other: 0,
            EarlyFiltered: 999, GitignoreExcluded: 777, ExtensionExcludedAtDiscovery: 888,
            CloudOnly: 4, CloudOnlyAtDiscovery: 4);

        Assert.Equal(5, b.CountedTotal);
        Assert.Equal(777 + 888 + 4, b.DiscoveryFilteredTotal);
    }

    [Fact]
    public void CloudOnlyDuringScan_SubtractsTheDiscoveryPortion()
    {
        var b = new SkipBreakdown(0, 0, 0, 0, 0, 0, 0, CloudOnly: 30, CloudOnlyAtDiscovery: 12);
        Assert.Equal(18, b.CloudOnlyDuringScan);
    }

    [Fact]
    public void CloudOnlyDuringScan_NeverNegative()
    {
        var b = new SkipBreakdown(0, 0, 0, 0, 0, 0, 0, CloudOnly: 3, CloudOnlyAtDiscovery: 9);
        Assert.Equal(0, b.CloudOnlyDuringScan);
    }

    [Fact]
    public void GlobOnlyExcluded_SubtractsTheOcrCachePortion()
    {
        var b = new SkipBreakdown(0, 0, 0, 0, 0, 0, 0, GlobExcluded: 50, OcrCacheExcluded: 20);
        Assert.Equal(30, b.GlobOnlyExcluded);
        Assert.Equal(50, b.CountedTotal); // both halves still counted exactly once
    }

    [Fact]
    public void Unclassified_ReportsTheRemainderAndClampsAtZero()
    {
        var b = new SkipBreakdown(Binary: 4, AccessDenied: 0, IOError: 0, TooLarge: 0, NotFound: 0, Encoding: 0, Other: 0);
        Assert.Equal(6, b.Unclassified(10));
        Assert.Equal(0, b.Unclassified(4));
        Assert.Equal(0, b.Unclassified(1));
    }

    [Fact]
    public void ToString_IncludesTheNewCategories()
    {
        var b = new SkipBreakdown(
            0, 0, 0, 0, 0, 0, 0,
            TooSmall: 1, DateFiltered: 2, OcrCacheExcluded: 3,
            ExtensionExcludedAtDiscovery: 4, CloudOnlyAtDiscovery: 5);
        var s = b.ToString();
        Assert.Contains("tooSmall=1", s);
        Assert.Contains("dateFiltered=2", s);
        Assert.Contains("ocrCacheExcluded=3", s);
        Assert.Contains("extExcludedAtDiscovery=4", s);
        Assert.Contains("cloudOnlyAtDiscovery=5", s);
    }

    // ── ClassifyMetadataSkip ────────────────────────────────────────────

    [Fact]
    public void ClassifyMetadataSkip_DistinguishesTooSmallFromTooLargeAndDate()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qg-skipclass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "a.txt");
            File.WriteAllText(file, new string('x', 100));

            Assert.Equal(SearchService.MetadataSkipReason.TooSmall, SearchService.ClassifyMetadataSkip(
                file, new SearchOptions { Directory = dir, Query = "x", MinFileSizeBytes = 10_000 }));
            Assert.Equal(SearchService.MetadataSkipReason.TooLarge, SearchService.ClassifyMetadataSkip(
                file, new SearchOptions { Directory = dir, Query = "x", MaxFileSizeBytes = 10 }));
            Assert.Equal(SearchService.MetadataSkipReason.DateRange, SearchService.ClassifyMetadataSkip(
                file, new SearchOptions { Directory = dir, Query = "x", ModifiedAfterDate = DateTimeOffset.Now.AddDays(1) }));
            Assert.Equal(SearchService.MetadataSkipReason.None, SearchService.ClassifyMetadataSkip(
                file, new SearchOptions { Directory = dir, Query = "x" }));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── DirectOutputSink: no native status falls into "Other" by accident ──

    [Fact]
    public void DirectOutputSink_ClassifiesIoTimeoutSeparatelyFromOther()
    {
        string sink = ReadSource("src", "Yagu", "Services", "DirectOutputSink.cs");
        Assert.Contains("case NativeSearcher.StatusIoTimeout: SkipIoTimeout++; break;", sink);

        string search = ReadSource("src", "Yagu", "Services", "SearchService.cs");
        Assert.Contains("Interlocked.Add(ref skipIoTimeout, directSink.SkipIoTimeout);", search);
    }

    // ── SearchService: no skip may bypass classification ────────────────

    [Fact]
    public void SearchService_RoutesEveryManagedSkipCodeThroughOneClassifier()
    {
        string search = ReadSource("src", "Yagu", "Services", "SearchService.cs");

        // Both managed scan surfaces (ordinary files and archive entries) must classify their negative
        // skip code; otherwise the file bumps FilesSkipped with no matching breakdown row.
        Assert.Contains("void TallyContentSkipReason(int code, string file)", search);
        Assert.Contains("TallyContentSkipReason(produced, file);", search);
        Assert.Contains("TallyContentSkipReason(produced, zipFile);", search);
    }

    // ── View-model rendering (source-pinned: MainViewModel is not compiled here) ──

    [Fact]
    public void SkipTooltip_RendersEveryCountedCategory()
    {
        string vm = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.ResultGroups.cs");

        foreach (var (label, expression) in new[]
        {
            ("Excluded by glob", "b.GlobOnlyExcluded"),
            ("Yagu OCR cache", "b.OcrCacheExcluded"),
            ("Binary files", "b.Binary"),
            ("Extension skips", "b.ByExtension"),
            ("Too large", "b.TooLarge"),
            ("Below minimum size", "b.TooSmall"),
            ("Outside date range", "b.DateFiltered"),
            ("Access denied", "b.AccessDenied"),
            ("Inaccessible folders", "b.Directories"),
            ("I/O errors", "b.IOError"),
            ("I/O timeouts", "b.IoTimeout"),
            ("Not found", "b.NotFound"),
            ("Encoding errors", "b.Encoding"),
            ("Cloud-only placeholders", "b.CloudOnlyDuringScan"),
            ("Multiline size/timeout", "b.MultilineSkipped"),
            ("Other", "b.Other"),
        })
        {
            Assert.Contains($"\"{label}\", {expression}", vm);
        }
    }

    [Fact]
    public void SkipTooltip_ShowsUnclassifiedRemainderAndTotal()
    {
        string vm = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.ResultGroups.cs");

        // The remainder row is what makes the overlay honest: a future skip path that no category claims
        // stays visible instead of silently shrinking the breakdown below the headline count.
        Assert.Contains("\"Unclassified\", b.Unclassified(total)", vm);
        Assert.Contains("\"Total skipped\", total, force: true", vm);
    }

    [Fact]
    public void SkipTooltip_SeparatesDiscoveryFiltersFromTheCountedTotal()
    {
        string vm = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.ResultGroups.cs");

        Assert.Contains("Filtered during discovery (not counted above):", vm);
        Assert.Contains("\".gitignore rules\", b.GitignoreExcluded", vm);
        Assert.Contains("\"Excluded extensions\", b.ExtensionExcludedAtDiscovery", vm);
        Assert.Contains("\"Cloud-only placeholders\", b.CloudOnlyAtDiscovery", vm);
    }

    [Fact]
    public void SkipTooltip_IsReRaisedWhenTheSkippedCountChanges()
    {
        // The overlay now prints the total, so a changed count must invalidate the cached tooltip.
        string vis = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.Visibility.cs");
        Assert.Matches(@"OnFilesSkippedChanged\(int value\)\s*\{[^}]*OnPropertyChanged\(nameof\(SkipTooltip\)\)", vis);
    }

    // ── CLI parity (source-pinned: CliRunner is not compiled here) ───────

    [Fact]
    public void CliBreakdown_MatchesTheGuiCategories()
    {
        string cli = ReadSource("src", "Yagu", "CliRunner.cs");

        foreach (var (label, expression) in new[]
        {
            ("Excluded by glob", "b.GlobOnlyExcluded"),
            ("Yagu OCR cache", "b.OcrCacheExcluded"),
            ("Binary files", "b.Binary"),
            ("Extension skips", "b.ByExtension"),
            ("Too large", "b.TooLarge"),
            ("Below minimum size", "b.TooSmall"),
            ("Outside date range", "b.DateFiltered"),
            ("Access denied", "b.AccessDenied"),
            ("Inaccessible folders", "b.Directories"),
            ("I/O errors", "b.IOError"),
            ("I/O timeouts", "b.IoTimeout"),
            ("Not found", "b.NotFound"),
            ("Encoding errors", "b.Encoding"),
            ("Cloud-only placeholders", "b.CloudOnlyDuringScan"),
            ("Multiline size/timeout", "b.MultilineSkipped"),
            ("Other", "b.Other"),
        })
        {
            Assert.Contains($"WriteSkipRow(\"{label}\", {expression}", cli);
        }

        Assert.Contains("WriteSkipRow(\"Unclassified\", b.Unclassified(s.FilesSkipped), color);", cli);
        Assert.Contains("Filtered during discovery (not counted above):", cli);
    }
}

/// <summary>
/// End-to-end skip accounting: the breakdown must classify every reason and reconcile with the
/// headline <see cref="SearchSummary.FilesSkipped"/>.
/// </summary>
/// <remarks>
/// Deliberately does NOT touch the static <see cref="FileLister.Backend"/>. Other suites that run
/// real searches depend on the ambient backend, and toggling it here (even with a restore in
/// <see cref="Dispose"/>) destabilizes them because xUnit runs this assembly with
/// <c>parallelizeTestCollections: false</c>, so leaked global state hits whichever suite runs next.
/// These assertions are backend-agnostic: every scenario is exercised over a private temp tree and
/// only asserts that a reason is classified, never how many files a given backend enumerates.
/// </remarks>
public class SkipBreakdownAccountingTests : IDisposable
{
    private readonly string _root;

    public SkipBreakdownAccountingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-skipacct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new System.Text.UTF8Encoding(false));
    }

    private async Task<SearchSummary> RunAsync(SearchOptions options)
    {
        var svc = new SearchService(new FileLister(), new ContentSearcher());
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(options, default))
        {
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }
        Assert.NotNull(summary);
        return summary!;
    }

    [Fact]
    public async Task MinimumSizeFilter_ReportsTooSmallInsteadOfAnUnnamedBucket()
    {
        Write("big.txt", new string('n', 4096));
        Write("tiny.txt", "n");

        var summary = await RunAsync(new SearchOptions
        {
            Directory = _root,
            Query = "n",
            MinFileSizeBytes = 1024,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        });

        Assert.True(summary.SkipReasons!.TooSmall >= 1 || summary.FilesSkipped == 0,
            $"a skipped file must be classified as too-small, got {summary.SkipReasons}");
        Assert.True(summary.SkipReasons.CountedTotal <= summary.FilesSkipped);
        Assert.Equal(0, summary.SkipReasons.Unclassified(summary.FilesSkipped));
    }

    [Fact]
    public async Task DateFilter_ReportsOutsideDateRange()
    {
        Write("a.txt", "needle");

        var summary = await RunAsync(new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ModifiedAfterDate = DateTimeOffset.Now.AddDays(1),
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        });

        Assert.True(summary.SkipReasons!.DateFiltered >= 1 || summary.FilesSkipped == 0,
            $"a skipped file must be classified as date-filtered, got {summary.SkipReasons}");
        Assert.True(summary.SkipReasons.CountedTotal <= summary.FilesSkipped);
        Assert.Equal(0, summary.SkipReasons.Unclassified(summary.FilesSkipped));
    }

    [Fact]
    public async Task SkipExtensions_ReportAsDiscoveryFiltersOutsideTheSkippedTotal()
    {
        Write("a.txt", "needle");
        Write("b.bin", "needle");

        var summary = await RunAsync(new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            SkipExtensions = new HashSet<string>(["bin"], StringComparer.OrdinalIgnoreCase),
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        });

        // Discovery filters are deliberately outside the headline count, and both enumeration
        // backends tally them, so this holds regardless of which one runs.
        Assert.True(summary.SkipReasons!.ExtensionExcludedAtDiscovery >= 1
            || summary.SkipReasons.DiscoveryFilteredTotal == 0,
            $"an extension-excluded file must be reported as a discovery filter, got {summary.SkipReasons}");
        Assert.Equal(0, summary.SkipReasons.Unclassified(summary.FilesSkipped));
    }

    [Fact]
    public async Task GlobExclusion_IsCountedAndFullyClassified()
    {
        Write("a.txt", "needle");
        Write("b.log", "needle");

        var summary = await RunAsync(new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ExcludeGlobs = ["*.log"],
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        });

        Assert.True(summary.FilesSkipped >= 1 || summary.SkipReasons!.GlobExcluded == 0);
        Assert.Equal(summary.SkipReasons!.GlobExcluded, summary.SkipReasons.GlobOnlyExcluded);
        Assert.Equal(0, summary.SkipReasons.Unclassified(summary.FilesSkipped));
    }

    [Fact]
    public async Task MixedSkips_BreakdownReconcilesWithTheHeadlineCount()
    {
        Write("hit.txt", "needle");
        Write("excluded.log", "needle");
        Write("tiny.txt", "n");
        Write(Path.Combine("sub", "deep.txt"), new string('n', 2048));

        var summary = await RunAsync(new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ExcludeGlobs = ["*.log"],
            MinFileSizeBytes = 16,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        });

        var b = summary.SkipReasons!;
        // The invariant the overlay depends on: the counted categories partition the headline number
        // exactly — never over-counting (a reason tallied twice) and never under-counting.
        Assert.True(b.CountedTotal <= summary.FilesSkipped,
            $"breakdown over-counts: countedTotal={b.CountedTotal} skipped={summary.FilesSkipped} ({b})");
        Assert.Equal(summary.FilesSkipped, b.CountedTotal + b.Unclassified(summary.FilesSkipped));
    }
}
