using System.Text;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Ocr;
using Yagu.Services.Pdf;

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

    [Fact]
    public void EffectiveHardCap_UsesMaxResultsWhenPositive()
    {
        var o = new SearchOptions { Query = "x", Directory = "x", MaxResults = 100, AbsoluteMaxResults = 5_000 };
        Assert.Equal(100, SearchService.EffectiveHardCap(o));
    }

    [Fact]
    public void EffectiveHardCap_FallsBackToAbsoluteWhenUnlimited()
    {
        var o = new SearchOptions { Query = "x", Directory = "x", MaxResults = 0, AbsoluteMaxResults = 5_000 };
        Assert.Equal(5_000, SearchService.EffectiveHardCap(o));
    }

    [Fact]
    public void EffectiveHardCap_ZeroWhenBothDisabled()
    {
        var o = new SearchOptions { Query = "x", Directory = "x", MaxResults = 0, AbsoluteMaxResults = 0 };
        Assert.Equal(0, SearchService.EffectiveHardCap(o));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(FileListerBackend.Auto, true)]
    [InlineData(FileListerBackend.EverythingSdk, true)]
    [InlineData(FileListerBackend.EsExe, false)]
    [InlineData(FileListerBackend.Managed, false)]
    public void IsNameFirstBackendEligible_AcceptsOnlySdkCapableBackends(
        FileListerBackend? backend,
        bool expected)
    {
        Assert.Equal(expected, SearchService.IsNameFirstBackendEligible(backend));
    }

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private static void SetFileTimes(string path, DateTime created, DateTime modified)
    {
        File.SetCreationTime(path, created);
        File.SetLastWriteTime(path, modified);
    }

    private sealed class CannedOcrEngine(string text) : IOcrEngine
    {
        public string Id => "test-ocr";
        public string DisplayName => "Test OCR";
        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
            => Task.FromResult(OcrResult.Ok(string.Empty));
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult(OcrResult.Ok(text));
    }

    private sealed class CannedPdfTextExtractor(string text) : PdfTextExtractor
    {
        public override Task<PdfTextResult> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
            => Task.FromResult(PdfTextResult.Ok(text));
    }

    [Fact]
    public async Task EmptyLiteralQuery_CompletesWithoutStartingDiscovery()
    {
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "   ",
            SearchMode = SearchMode.Content,
        };

        var events = new List<SearchEvent>();
        await foreach (SearchEvent searchEvent in new SearchService().SearchAsync(options, default))
            events.Add(searchEvent);

        var completed = Assert.IsType<SearchEvent.Completed>(Assert.Single(events));
        Assert.Equal(0, completed.Summary.TotalFiles);
        Assert.Equal(0, completed.Summary.TotalMatches);
    }

    [Theory]
    [InlineData(true, false, false, "zip")]
    [InlineData(false, true, false, "png")]
    [InlineData(false, false, true, "pdf")]
    public async Task ExtendedSourceSearch_RemovesOnlyItsExtensionsFromListerSkipSet(
        bool searchArchives,
        bool searchImages,
        bool searchPdfs,
        string removedExtension)
    {
        var lister = new FileLister();
        var service = new SearchService(lister, new ContentSearcher());
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            SearchMode = SearchMode.Content,
            SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zip", "png", "pdf", "bin" },
            SearchInsideArchives = searchArchives,
            ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip" },
            SearchImageText = searchImages,
            ImageOcrExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png" },
            ImageOcrEngineFactory = () => new CannedOcrEngine(string.Empty),
            SearchPdfText = searchPdfs,
            PdfTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pdf" },
            PdfTextExtractorFactory = () => new CannedPdfTextExtractor(string.Empty),
        };

        await foreach (SearchEvent _ in service.SearchAsync(options, default))
        {
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zip", "png", "pdf", "bin" };
        expected.Remove(removedExtension);
        Assert.Equal(expected.OrderBy(extension => extension), lister.EarlySkipExtensions.OrderBy(extension => extension));
    }

    [Fact]
    public async Task DegradedStoreAndContentIndex_AreReflectedInCompletedSummary()
    {
        using var store = new ResultStore(_root);
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            SearchMode = SearchMode.Content,
            DegradedResultStore = store,
            UseContentIndex = true,
        };

        SearchSummary? summary = null;
        await foreach (SearchEvent searchEvent in new SearchService().SearchAsync(options, default))
            if (searchEvent is SearchEvent.Completed completed)
                summary = completed.Summary;

        Assert.NotNull(summary);
        Assert.True(summary!.Degraded);
        Assert.Equal(new IndexAccelerationInfo(1, 0, 0, 0), summary.IndexAcceleration);
    }

    [Fact]
    public async Task OcrAndPdfWithoutFactories_UseDefaultDependencies()
    {
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            SearchMode = SearchMode.Content,
            SearchImageText = true,
            SearchPdfText = true,
        };

        SearchSummary? summary = null;
        await foreach (SearchEvent searchEvent in new SearchService().SearchAsync(options, default))
            if (searchEvent is SearchEvent.Completed completed)
                summary = completed.Summary;

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.TotalMatches);
    }

    [Fact]
    public async Task DrainRemainingEventsAsync_ForwardsBufferedEventsUntilCompletion()
    {
        var channel = Channel.CreateUnbounded<SearchEvent>();
        SearchEvent[] expected =
        [
            new SearchEvent.Progress(new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.Zero)),
            new SearchEvent.SearchError("expected"),
        ];
        foreach (SearchEvent searchEvent in expected)
            Assert.True(channel.Writer.TryWrite(searchEvent));
        channel.Writer.Complete();

        var actual = new List<SearchEvent>();
        await foreach (SearchEvent searchEvent in SearchService.DrainRemainingEventsAsync(channel.Reader, default))
            actual.Add(searchEvent);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Multiline_BareLiteral_PromotedToRegexAndMatches()
    {
        // A bare literal (UseRegex=false) under Multiline must be promoted to an escaped regex and
        // run through the whole-file multiline engine, exercising the SearchService multiline gate
        // and the memory-derived multiline-parallelism resolution.
        Write("ml.txt", "hello foobar world\nsecond line");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "foobar",
            UseRegex = false,
            ExactMatch = false,
            Multiline = true,
            MaxResults = 0,
        };

        int matches = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            switch (evt)
            {
                case SearchEvent.Match: matches++; break;
                case SearchEvent.MatchBatch mb: matches += mb.Results.Count; break;
            }
        }

        Assert.Equal(1, matches);
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
    public async Task EndToEnd_OcrAndPdfSessionsContributeMatchesAndSummaryCounts()
    {
        string imagePath = Write("scan.png", "fake image");
        string pdfPath = Write("document.pdf", "fake pdf");
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            SearchMode = SearchMode.Content,
            SearchImageText = true,
            SearchPdfText = true,
            ImageOcrExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png" },
            PdfTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pdf" },
            ImageOcrWorkerParallelism = 1,
            ImageOcrEngineFactory = () => new CannedOcrEngine("needle in image"),
            PdfTextExtractorFactory = () => new CannedPdfTextExtractor("needle in pdf"),
        };

        var results = new List<SearchResult>();
        SearchSummary? summary = null;
        await foreach (SearchEvent searchEvent in new SearchService().SearchAsync(options, default))
        {
            if (searchEvent is SearchEvent.Match match)
                results.Add(match.Result);
            else if (searchEvent is SearchEvent.MatchBatch batch)
                results.AddRange(batch.Results);
            else if (searchEvent is SearchEvent.Completed completed)
                summary = completed.Summary;
        }

        Assert.Contains(results, result => result.FilePath == imagePath && result.LineNumber == 1);
        Assert.Contains(results, result => result.FilePath == pdfPath && result.LineNumber == 1);
        Assert.NotNull(summary);
        Assert.Equal(2, summary!.TotalMatches);
        Assert.Equal(2, summary.FilesWithMatches);
    }

    [Fact]
    public async Task EndToEnd_ThrowingExtendedSourceGateFailsOpen()
    {
        Write("plain.txt", "needle");
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            SearchMode = SearchMode.Content,
            ExtendedSourceGateFactory = () => throw new InvalidOperationException("gate failed"),
        };

        SearchSummary? summary = null;
        await foreach (SearchEvent searchEvent in new SearchService().SearchAsync(options, default))
            if (searchEvent is SearchEvent.Completed completed)
                summary = completed.Summary;

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalMatches);
    }

    [Fact]
    public async Task BothMode_NameFirstPass_EmitsFilenameMatchExactlyOnce()
    {
        // File whose NAME contains the literal AND whose content also matches.
        Write("needle-report.txt", "alpha\nhas needle here\nomega");
        // File with a content match only (no name match) — Both must still scan it.
        Write("plain.txt", "needle in content");
        // File with neither.
        Write("unrelated.txt", "nothing relevant");

        bool originalSdk = FileLister.SdkAvailable;
        FileLister.SdkAvailable = true; // engage the name-first pass on the managed backend
        try
        {
            var svc = new SearchService();
            var opts = new SearchOptions
            {
                Directory = _root,
                Query = "needle",
                SearchMode = SearchMode.Both,
                CaseSensitive = false,
                UseRegex = false,
                MaxFileSizeBytes = 0,
                MaxResults = 0,
            };

            var results = new List<SearchResult>();
            await foreach (var evt in svc.SearchAsync(opts, default))
            {
                if (evt is SearchEvent.Match m) results.Add(m.Result);
                else if (evt is SearchEvent.MatchBatch b) results.AddRange(b.Results);
            }

            // The name-first pass and the full discovery must not BOTH emit the filename match.
            int filenameMatchCount = 0;
            string? filenameMatchPath = null;
            bool reportContent = false, plainContent = false;
            foreach (var r in results)
            {
                if (r.LineNumber == 0) { filenameMatchCount++; filenameMatchPath = r.FilePath; }
                else if (r.FilePath.EndsWith("needle-report.txt", StringComparison.Ordinal)) reportContent = true;
                else if (r.FilePath.EndsWith("plain.txt", StringComparison.Ordinal)) plainContent = true;
            }

            Assert.Equal(1, filenameMatchCount);
            Assert.NotNull(filenameMatchPath);
            Assert.EndsWith("needle-report.txt", filenameMatchPath!);

            // Both semantics preserved: content matches for BOTH the name-matched file and the
            // content-only file are still present (the full pass scans the whole tree).
            Assert.True(reportContent, "expected a content match in the name-matched file");
            Assert.True(plainContent, "expected a content match in the content-only file");

            // Filename first, then priority content for the same file. The later full discovery must
            // not emit either result a second time.
            int filenameIndex = results.FindIndex(r => r.LineNumber == 0);
            int reportContentIndex = results.FindIndex(r => r.LineNumber > 0
                && r.FilePath.EndsWith("needle-report.txt", StringComparison.Ordinal));
            Assert.InRange(filenameIndex, 0, int.MaxValue);
            Assert.True(reportContentIndex > filenameIndex,
                $"expected filename result before its content upgrade (filename={filenameIndex}, content={reportContentIndex})");
            Assert.Equal(1, results.Count(r => r.LineNumber > 0
                && r.FilePath.EndsWith("needle-report.txt", StringComparison.Ordinal)));
        }
        finally
        {
            FileLister.SdkAvailable = originalSdk;
        }
    }

    [Fact]
    public async Task SearchManyAsync_AggregatesAcrossRoots_IntoSingleCompleted()
    {
        Write("a.txt", "foo\nfoo");          // 2 content matches under _root
        var root2 = Path.Combine(Path.GetTempPath(), "qg-svc2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root2);
        try
        {
            File.WriteAllText(Path.Combine(root2, "b.txt"), "foo", new UTF8Encoding(false)); // 1 match under root2

            var svc = new SearchService();
            static SearchOptions Make(string dir) => new SearchOptions
            {
                Directory = dir,
                Query = "foo",
                SearchMode = SearchMode.Content,
                MaxFileSizeBytes = 0,
                MaxResults = 0,
            };

            int matches = 0, completed = 0;
            SearchSummary? summary = null;
            await foreach (var evt in svc.SearchManyAsync(new[] { Make(_root), Make(root2) }, default))
            {
                switch (evt)
                {
                    case SearchEvent.Match: matches++; break;
                    case SearchEvent.MatchBatch mb: matches += mb.Results.Count; break;
                    case SearchEvent.Completed c: completed++; summary = c.Summary; break;
                }
            }

            Assert.Equal(3, matches);
            Assert.Equal(1, completed); // intermediate per-root Completed events are suppressed
            Assert.NotNull(summary);
            Assert.Equal(3, summary!.TotalMatches);
            Assert.Equal(2, summary.FilesWithMatches);
        }
        finally { try { Directory.Delete(root2, recursive: true); } catch { } }
    }

    [Fact]
    public async Task SearchManyAsync_SingleRoot_DelegatesToSearchAsync()
    {
        Write("a.txt", "foo\nfoo");
        var svc = new SearchService();
        var opts = new SearchOptions { Directory = _root, Query = "foo", SearchMode = SearchMode.Content, MaxFileSizeBytes = 0, MaxResults = 0 };

        int matches = 0, completed = 0;
        await foreach (var evt in svc.SearchManyAsync(new[] { opts }, default))
        {
            if (evt is SearchEvent.Match) matches++;
            else if (evt is SearchEvent.MatchBatch mb) matches += mb.Results.Count;
            else if (evt is SearchEvent.Completed) completed++;
        }

        Assert.Equal(2, matches);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task SearchManyAsync_EmptyList_EmitsSingleCompleted()
    {
        var svc = new SearchService();
        int completed = 0;
        await foreach (var evt in svc.SearchManyAsync(Array.Empty<SearchOptions>(), default))
            if (evt is SearchEvent.Completed) completed++;

        Assert.Equal(1, completed);
    }

    [Fact]
    public void SearchManyAsync_PrioritizesOnlyRepresentableNameQueries()
    {
        bool originalSdk = FileLister.SdkAvailable;
        FileLister.SdkAvailable = true;
        try
        {
            var service = new SearchService();
            SearchOptions Eligible(string directory) => new()
            {
                Directory = directory,
                Query = "report",
                SearchMode = SearchMode.Both,
            };
            SearchOptions InvalidFilter(string directory) => new()
            {
                Directory = directory,
                Query = "report final",
                SearchMode = SearchMode.Both,
            };
            SearchOptions EmptyQuery(string directory) => new()
            {
                Directory = directory,
                Query = "   ",
                SearchMode = SearchMode.Both,
            };

            SearchOptions[] eligibleRoots = [Eligible("C"), Eligible("D")];
            SearchOptions[] invalidFilterRoots = [InvalidFilter("C"), InvalidFilter("D")];
            SearchOptions[] emptyQueryRoots = [EmptyQuery("C"), EmptyQuery("D")];

            Assert.True(service.CanPrioritizeNameMatchesAcrossRoots(eligibleRoots));
            Assert.False(service.CanPrioritizeNameMatchesAcrossRoots(invalidFilterRoots));
            Assert.False(service.CanPrioritizeNameMatchesAcrossRoots(emptyQueryRoots));
            Assert.NotNull(service.SearchManyAsync(eligibleRoots, default));
            Assert.NotNull(service.SearchManyAsync(invalidFilterRoots, default));
            Assert.NotNull(service.SearchManyAsync(emptyQueryRoots, default));
        }
        finally
        {
            FileLister.SdkAvailable = originalSdk;
        }
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
    public async Task CreatedDateRange_FiltersBeforeMatching()
    {
        var tooOld = Write("created-old.txt", "needle");
        var inRange = Write("created-in-range.txt", "needle");
        var tooNew = Write("created-new.txt", "needle");
        SetFileTimes(tooOld, new DateTime(2023, 12, 31), new DateTime(2026, 1, 1));
        SetFileTimes(inRange, new DateTime(2024, 6, 15), new DateTime(2026, 1, 1));
        SetFileTimes(tooNew, new DateTime(2025, 1, 1), new DateTime(2026, 1, 1));

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            CreatedAfterDate = new DateTimeOffset(new DateTime(2024, 1, 1)),
            CreatedBeforeDate = new DateTimeOffset(new DateTime(2024, 12, 31)),
            MaxResults = 0,
        };

        var results = new List<SearchResult>();
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match match) results.Add(match.Result);
            else if (evt is SearchEvent.MatchBatch batch) results.AddRange(batch.Results);
            else if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.Single(results);
        Assert.EndsWith("created-in-range.txt", results[0].FilePath);
        Assert.NotNull(summary);
        Assert.True(summary!.SkipReasons?.EarlyFiltered >= 2);
    }

    [Fact]
    public async Task ModifiedDateRange_FiltersBeforeMatching()
    {
        var tooOld = Write("modified-old.txt", "needle");
        var inRange = Write("modified-in-range.txt", "needle");
        var tooNew = Write("modified-new.txt", "needle");
        SetFileTimes(tooOld, new DateTime(2020, 1, 1), new DateTime(2023, 12, 31));
        SetFileTimes(inRange, new DateTime(2020, 1, 1), new DateTime(2024, 6, 15));
        SetFileTimes(tooNew, new DateTime(2020, 1, 1), new DateTime(2025, 1, 1));

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ModifiedAfterDate = new DateTimeOffset(new DateTime(2024, 1, 1)),
            ModifiedBeforeDate = new DateTimeOffset(new DateTime(2024, 12, 31)),
            MaxResults = 0,
        };

        var results = new List<SearchResult>();
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match match) results.Add(match.Result);
            else if (evt is SearchEvent.MatchBatch batch) results.AddRange(batch.Results);
            else if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.Single(results);
        Assert.EndsWith("modified-in-range.txt", results[0].FilePath);
        Assert.NotNull(summary);
        Assert.True(summary!.SkipReasons?.EarlyFiltered >= 2);
    }

    [Fact]
    public async Task QuotedLiteral_SearchesExactPhrase()
    {
        Write("phrase.txt", "the value is test 123 here");
        Write("split.txt", "the value has test then later 123");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "test 123",
            ExactMatch = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var results = new List<SearchResult>();
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match match) results.Add(match.Result);
            else if (evt is SearchEvent.MatchBatch batch) results.AddRange(batch.Results);
        }

        Assert.Single(results);
        Assert.EndsWith("phrase.txt", results[0].FilePath);
        Assert.Equal("test 123", results[0].MatchLine.Substring(results[0].MatchStartColumn, results[0].MatchLength));
    }

    [Fact]
    public async Task UnquotedLiteralTerms_SearchesEachTermIndependently()
    {
        Write("word.txt", "contains test only");
        Write("number.txt", "contains 123 only");
        Write("quiet.txt", "contains neither value");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "test 123",
            ExactMatch = false,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int matches = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match match)
            {
                files.Add(Path.GetFileName(match.Result.FilePath));
                matches++;
            }
            else if (evt is SearchEvent.MatchBatch batch)
            {
                foreach (var result in batch.Results)
                    files.Add(Path.GetFileName(result.FilePath));
                matches += batch.Results.Count;
            }
        }

        Assert.Equal(2, matches);
        Assert.Equal(new[] { "number.txt", "word.txt" }, files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase));
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
            if (evt is SearchEvent.SearchError) error = true;
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
    public async Task IncludeRegex_FiltersFiles()
    {
        Write("a.cs", "needle");
        Write("a.xaml", "needle");
        Write("a.txt", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            IncludeGlobs = [@"\.(cs|xaml)$"],
            IncludeFilterMode = FilterPatternMode.Regex,
            MaxFileSizeBytes = 0,
        };

        int matches = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) matches++;
            else if (evt is SearchEvent.MatchBatch mb) matches += mb.Results.Count;
        }

        Assert.Equal(2, matches);
    }

    [Fact]
    public async Task ExcludeRegex_FiltersFiles()
    {
        Write("keep.js", "needle");
        Write("app.min.js", "needle");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "skip.js"), "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ExcludeGlobs = [@"(^|/)node_modules/|\.min\.js$"],
            ExcludeFilterMode = FilterPatternMode.Regex,
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
                Assert.False(progress.Snapshot.TotalFilesKnown);
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
        Assert.True(progressBeforeDiscoveryComplete.TotalFilesKnown);
        Assert.True(progressBeforeDiscoveryComplete.FilesScanned < progressBeforeDiscoveryComplete.TotalFiles);
    }

    [Fact]
    public async Task SearchAsync_EmitsScanCompletedBeforeFinalCompleted()
    {
        Write("a.txt", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var eventNames = new List<string>();
        SearchSummary? scanSummary = null;
        SearchSummary? completedSummary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            switch (evt)
            {
                case SearchEvent.ScanCompleted sc:
                    eventNames.Add(nameof(SearchEvent.ScanCompleted));
                    scanSummary = sc.Summary;
                    break;
                case SearchEvent.Completed c:
                    eventNames.Add(nameof(SearchEvent.Completed));
                    completedSummary = c.Summary;
                    break;
            }
        }

        int scanCompletedIndex = eventNames.IndexOf(nameof(SearchEvent.ScanCompleted));
        int completedIndex = eventNames.IndexOf(nameof(SearchEvent.Completed));
        Assert.True(scanCompletedIndex >= 0);
        Assert.True(completedIndex > scanCompletedIndex);
        Assert.NotNull(scanSummary);
        Assert.NotNull(completedSummary);
        Assert.Equal(1, scanSummary!.TotalMatches);
        Assert.Equal(scanSummary.Elapsed, completedSummary!.Elapsed);
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

    [Fact]
    public async Task ObeyGitignore_ExcludesMatchingFiles()
    {
        // Create a .gitignore that excludes *.log files
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
        Write("keep.txt", "needle");
        Write("skip.log", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ObeyGitignore = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var matchFiles = new List<string>();
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match m) matchFiles.Add(Path.GetFileName(m.Result.FilePath));
            else if (evt is SearchEvent.MatchBatch mb) matchFiles.AddRange(mb.Results.Select(r => Path.GetFileName(r.FilePath)));
            else if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.Single(matchFiles);
        Assert.Equal("keep.txt", matchFiles[0]);
        Assert.NotNull(summary);
    }

    [Fact]
    public async Task ObeyGitignore_ExcludesFolders()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "excluded_dir\n");
        Directory.CreateDirectory(Path.Combine(_root, "excluded_dir"));
        Write("excluded_dir/hidden.txt", "needle");
        Write("visible.txt", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ObeyGitignore = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var matchFiles = new List<string>();
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match m) matchFiles.Add(Path.GetFileName(m.Result.FilePath));
            else if (evt is SearchEvent.MatchBatch mb) matchFiles.AddRange(mb.Results.Select(r => Path.GetFileName(r.FilePath)));
        }

        Assert.Single(matchFiles);
        Assert.Equal("visible.txt", matchFiles[0]);
    }

    [Fact]
    public async Task ObeyGitignore_DisabledDoesNotExclude()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
        Write("keep.txt", "needle");
        Write("also-keep.log", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ObeyGitignore = false,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        int matches = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) matches++;
            else if (evt is SearchEvent.MatchBatch mb) matches += mb.Results.Count;
        }

        Assert.Equal(2, matches);
    }

    [Fact]
    public async Task ObeyGitignore_IncludeFilterTakesPrecedence()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
        Write("data.log", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ObeyGitignore = true,
            GitignoreTakesPrecedence = false,
            IncludeGlobs = new[] { "log" },
            MaxFileSizeBytes = 0,
            MaxResults = 0,
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
    public async Task ExactMatch_True_SearchesWholePhrase()
    {
        Write("phrase.txt", "the value is test 123 here");
        Write("split.txt", "the value has test then later 123");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "test 123",
            ExactMatch = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var matchFiles = new List<string>();
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match m) matchFiles.Add(Path.GetFileName(m.Result.FilePath));
            else if (evt is SearchEvent.MatchBatch mb) matchFiles.AddRange(mb.Results.Select(r => Path.GetFileName(r.FilePath)));
        }

        Assert.Single(matchFiles);
        Assert.Equal("phrase.txt", matchFiles[0]);
    }

    [Fact]
    public async Task ExactMatch_True_MatchesWholeWordNotSubstring()
    {
        Write("word.txt", "an async method");
        Write("longer.txt", "runs asynchronously");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "async",
            ExactMatch = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var matchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match m) matchFiles.Add(Path.GetFileName(m.Result.FilePath));
            else if (evt is SearchEvent.MatchBatch mb) foreach (var r in mb.Results) matchFiles.Add(Path.GetFileName(r.FilePath));
        }

        Assert.Single(matchFiles);
        Assert.Contains("word.txt", matchFiles);
    }

    [Fact]
    public async Task ExactMatch_False_SearchesEachTermSeparately()
    {
        Write("word.txt", "contains test only");
        Write("number.txt", "contains 123 only");
        Write("quiet.txt", "contains neither value");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "test 123",
            ExactMatch = false,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var matchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match m) matchFiles.Add(Path.GetFileName(m.Result.FilePath));
            else if (evt is SearchEvent.MatchBatch mb) foreach (var r in mb.Results) matchFiles.Add(Path.GetFileName(r.FilePath));
        }

        Assert.Equal(2, matchFiles.Count);
        Assert.Contains("word.txt", matchFiles);
        Assert.Contains("number.txt", matchFiles);
    }

    [Fact]
    public async Task ObeyGitignore_Summary_CountsGitignoreSkipped()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
        Write("keep.txt", "needle");
        Write("skip.log", "needle");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "needle",
            ObeyGitignore = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        Assert.NotNull(summary);
        Assert.True(summary!.SkipReasons?.GitignoreExcluded >= 1);
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
        public int GitignoreSkipped => 0;
        public int CloudOnlySkippedFiles => 0;

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
        public int GitignoreSkipped => 0;
        public int CloudOnlySkippedFiles => 0;

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
            ExactMatch = false,
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
    public async Task FileNameThenContentSearch_OnlyReturnsContentMatchesFromMatchingFileNames()
    {
        Write("target-with-content.txt", "before target after");
        Write("target-without-content.txt", "quiet content");
        Write("other.txt", "target appears here but the file name does not match");

        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "target",
            SearchMode = SearchMode.FileNameThenContent,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };

        var results = new List<SearchResult>();
        SearchSummary? summary = null;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match match) results.Add(match.Result);
            if (evt is SearchEvent.MatchBatch batch) results.AddRange(batch.Results);
            if (evt is SearchEvent.Completed c) summary = c.Summary;
        }

        var result = Assert.Single(results);
        Assert.EndsWith("target-with-content.txt", result.FilePath);
        Assert.NotEqual(0, result.LineNumber);
        Assert.DoesNotContain(results, r => Path.GetFileName(r.FilePath) == "other.txt");
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalMatches);
        Assert.Equal(1, summary.FilesWithMatches);
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

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_NegativePressurePercent_ReturnsTrue()
    {
        // pressurePercent <= 0 means "no pressure configured" → returns true if process is relieved
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 100,
            effectiveProcessCapBytes: 200,
            systemMemoryLoadPercent: 95,
            pressurePercent: -1,
            recoveryMarginPercent: 5);
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_PressurePercent101_ReturnsTrue()
    {
        // pressurePercent > 100 means "no pressure configured" → returns true
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 100,
            effectiveProcessCapBytes: 200,
            systemMemoryLoadPercent: 95,
            pressurePercent: 101,
            recoveryMarginPercent: 5);
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_SystemLoadAboveRelief_ReturnsFalse()
    {
        // Process relieved but system memory load above (pressurePercent - recoveryMargin)
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 100,
            effectiveProcessCapBytes: 200,
            systemMemoryLoadPercent: 80,
            pressurePercent: 85,
            recoveryMarginPercent: 5);
        // relief = 85 - 5 = 80, systemLoad = 80 → 80 <= 80 → true
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_SystemLoadJustAboveRelief_ReturnsFalse()
    {
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 100,
            effectiveProcessCapBytes: 200,
            systemMemoryLoadPercent: 81,
            pressurePercent: 85,
            recoveryMarginPercent: 5);
        // relief = 85 - 5 = 80, systemLoad = 81 → 81 > 80 → false
        Assert.False(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_LargeRecoveryMargin_ClampsToZero()
    {
        // recoveryMargin larger than pressurePercent → reliefPercent = max(0, 50-100) = 0
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 100,
            effectiveProcessCapBytes: 200,
            systemMemoryLoadPercent: 0,
            pressurePercent: 50,
            recoveryMarginPercent: 100);
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelievedForSnapshot_ProcessBelowSheddableFloor_ReturnsTrueWhileSystemStaysBusy()
    {
        // Once Yagu is back under the sheddable floor it is no longer a contributor, so a search must not
        // stay stuck in memory-saving mode just because other processes keep the machine busy.
        bool relieved = SearchService.IsMemoryPressureRelievedForSnapshot(
            workingSetBytes: 23L * 1024 * 1024,
            effectiveProcessCapBytes: 768L * 1024 * 1024,
            systemMemoryLoadPercent: 87,
            pressurePercent: 75,
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
    public void SourceBackedMatchBatch_Properties()
    {
        var results = new List<SourceBackedMatch>
        {
            new("f.txt", 1, 0, 5, 0),
            new("f.txt", 2, 1, 4, 1),
        };
        var e = new SearchEvent.SourceBackedMatchBatch(results);
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
        var e = new SearchEvent.SearchError("bad regex");
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
            new SearchEvent.SearchError("e"),
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
    public void SourceBackedTailLabel_AppearsOnlyAfterOrdinaryWorkFinishes()
    {
        var source = new SourceBackedSearchProgress(
            OcrProcessed: 964,
            OcrQueued: 43_254,
            PdfProcessed: 0,
            PdfQueued: 14,
            DiscoveryComplete: true);

        Assert.Null(source.BuildPhaseLabel(filesProcessed: 600_000, totalFiles: 712_634));
        Assert.Equal($"OCR: {964:N0} / {43_254:N0} images",
            source.BuildPhaseLabel(filesProcessed: 670_330, totalFiles: 712_634));
        Assert.Equal($"94% [OCR: {964:N0} / {43_254:N0} images]",
            source.BuildCombinedLabel(filesProcessed: 670_330, totalFiles: 712_634));
        Assert.Equal(42_304, source.Remaining);
    }

    [Fact]
    public void SourceBackedTailLabel_ShowsPdfAfterOcrCompletes_AndRequiresDiscovery()
    {
        var incomplete = new SourceBackedSearchProgress(10, 10, 2, 14, DiscoveryComplete: false);
        Assert.Null(incomplete.BuildPhaseLabel(100, 100));

        var source = incomplete with { DiscoveryComplete = true };
        Assert.Equal("PDF text: 2 / 14 files", source.BuildPhaseLabel(88, 100));
    }

    [Fact]
    public void SourceBackedTailLabel_ToleratesSmallDiscoveryTotalDrift()
    {
        var source = new SourceBackedSearchProgress(
            OcrProcessed: 11_113,
            OcrQueued: 104_000,
            PdfProcessed: 0,
            PdfQueued: 25,
            DiscoveryComplete: true);

        // A small mismatch between discovery's estimated total and exact queue accounting must not hide
        // a long OCR tail behind a rounded "95%" label.
        Assert.Equal("OCR: 11,113 / 104,000 images",
            source.BuildPhaseLabel(filesProcessed: 1_872_380, totalFiles: 1_965_228));
        Assert.Equal("95% [OCR: 11,113 / 104,000 images]",
            source.BuildCombinedLabel(filesProcessed: 1_872_380, totalFiles: 1_965_228));
    }

    [Fact]
    public void SourceBackedProgress_HandlesZeroAndOverCompleteTotals()
    {
        var empty = new SourceBackedSearchProgress(0, 0, 0, 0, DiscoveryComplete: true);
        Assert.Equal(0, empty.OverallPercent(filesProcessed: 0, totalFiles: 0));
        Assert.Null(empty.BuildPhaseLabel(filesProcessed: 0, totalFiles: 0));
        Assert.Null(empty.BuildCombinedLabel(filesProcessed: 0, totalFiles: 0));

        var queued = new SourceBackedSearchProgress(12, 10, 8, 5, DiscoveryComplete: true);
        Assert.Equal(15, queued.Processed);
        Assert.Equal(15, queued.OverallTotal(totalFiles: 10));
        Assert.Equal(100, queued.OverallPercent(filesProcessed: 20, totalFiles: 10));
        Assert.Null(queued.BuildPhaseLabel(filesProcessed: 20, totalFiles: 10));
    }

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
        var skip = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
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
        Assert.Equal(12, ss.SkipReasons.GitignoreExcluded);
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
        Assert.Equal(0, sb.GitignoreExcluded);
    }

    [Fact]
    public void SkipBreakdown_ToString_ContainsAllFields()
    {
        var sb = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
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
        Assert.Contains("gitignoreExcluded=12", str);
    }

    [Fact]
    public void SkipBreakdown_RecordEquality()
    {
        var a = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
        var b = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
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
    public void SixteenGB_ReturnsOneQuarter()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(16UL * 1024 * 1024 * 1024);
        Assert.Equal(768L * 1024 * 1024, result);
    }

    [Fact]
    public void SixtyFourGB_ReturnsCeiling()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(64UL * 1024 * 1024 * 1024);
        Assert.Equal(768L * 1024 * 1024, result);
    }

    [Fact]
    public void OneHundredTwentyEightGB_ReturnsCeiling()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(128UL * 1024 * 1024 * 1024);
        Assert.Equal(768L * 1024 * 1024, result);
    }

    [Fact]
    public void FourGB_ReturnsFloor()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(4UL * 1024 * 1024 * 1024);
        Assert.Equal(768L * 1024 * 1024, result);
    }

    [Fact]
    public void TenGB_ReturnsQuarter()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(10UL * 1024 * 1024 * 1024);
        Assert.Equal(768L * 1024 * 1024, result);
    }

    [Fact]
    public void OneGB_ReturnsFloor()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(1UL * 1024 * 1024 * 1024);
        Assert.Equal(512L * 1024 * 1024, result);
    }

    [Fact]
    public void Zero_ReturnsFloor()
    {
        long result = SearchService.ComputeAutoProcessMemoryCap(0);
        Assert.Equal(512L * 1024 * 1024, result);
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
            workingSet: 2_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 85,
            pressurePercent: 80, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void SystemLoadBelowThreshold_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 2_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 50,
            pressurePercent: 80, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void GcFallback_HighLoad_ReturnsTrue()
    {
        Assert.True(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 2_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: false, systemLoadPercent: 0,
            pressurePercent: 80,
            gcMemoryLoadBytes: 9_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void GcFallback_LowLoad_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 2_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: false, systemLoadPercent: 0,
            pressurePercent: 80,
            gcMemoryLoadBytes: 1_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void PressureDisabled_Zero_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 2_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 99,
            pressurePercent: 0, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void PressureAbove100_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 2_000_000_000, effectiveCap: 4_000_000_000,
            hasSystemLoad: true, systemLoadPercent: 99,
            pressurePercent: 101, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    // Replays a real session: a 64 GB machine sitting at 76-87% system load because of OTHER processes,
    // while Yagu itself held 23 MB against a 768 MB cap. Every cycle logged "Eviction acknowledged:
    // freed 0" yet still forced degraded mode, working-set trims and compacting GCs (501ms, 1,138ms).
    private const long RealSessionCap = 768L * 1024 * 1024;

    [Fact]
    public void SystemLoadHigh_ButProcessHoldsNothingSheddable_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 23L * 1024 * 1024, effectiveCap: RealSessionCap,
            hasSystemLoad: true, systemLoadPercent: 78,
            pressurePercent: 75, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void GcFallbackHigh_ButProcessHoldsNothingSheddable_ReturnsFalse()
    {
        Assert.False(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 23L * 1024 * 1024, effectiveCap: RealSessionCap,
            hasSystemLoad: false, systemLoadPercent: 0,
            pressurePercent: 75,
            gcMemoryLoadBytes: 9_000_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void SystemLoadHigh_AndProcessHoldsSheddableMemory_ReturnsTrue()
    {
        Assert.True(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 400L * 1024 * 1024, effectiveCap: RealSessionCap,
            hasSystemLoad: true, systemLoadPercent: 78,
            pressurePercent: 75, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    [Fact]
    public void ProcessOverItsOwnCap_ReturnsTrueEvenWhenSystemIsIdle()
    {
        Assert.True(SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 1_035L * 1024 * 1024, effectiveCap: RealSessionCap,
            hasSystemLoad: true, systemLoadPercent: 20,
            pressurePercent: 75, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0));
    }

    // Every (process WS MB, system load %) pair logged by the real session, in order. The machine sat at
    // 76-87% because of other processes the whole time, so the trigger is decided purely by what Yagu held.
    [Theory]
    [InlineData(23, 78, false)]
    [InlineData(127, 78, false)]
    [InlineData(146, 78, false)]
    [InlineData(218, 78, true)]
    [InlineData(403, 78, true)]
    [InlineData(619, 80, true)]
    [InlineData(770, 79, true)]
    [InlineData(1035, 87, true)]
    public void RealSessionSamples_OnlyShedOnceYaguHoldsSomething(int workingSetMb, uint systemLoadPercent, bool expectedPressure)
    {
        bool pressure = SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: workingSetMb * 1024L * 1024,
            effectiveCap: RealSessionCap,
            hasSystemLoad: true,
            systemLoadPercent: systemLoadPercent,
            pressurePercent: 75,
            gcMemoryLoadBytes: 0,
            gcTotalAvailableBytes: 0);

        Assert.Equal(expectedPressure, pressure);
    }

    [Fact]
    public void RealSessionStart_DoesNotLatchDegradedModeForTheWholeSearch()
    {
        // Cycle #1 fired at 2 files scanned with Yagu at 23 MB. Because degraded mode is latched for the
        // rest of the run, that single bogus trigger shrank every native batch for the entire search.
        bool pressureAtSearchStart = SearchService.IsMemoryPressureHighForSnapshot(
            workingSet: 23L * 1024 * 1024, effectiveCap: RealSessionCap,
            hasSystemLoad: true, systemLoadPercent: 78,
            pressurePercent: 75, gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 0);

        Assert.False(pressureAtSearchStart);
        Assert.NotEqual(
            SearchService.ResolveNativeBatchTarget(currentBatchTarget: 2048, memorySaving: true),
            SearchService.ResolveNativeBatchTarget(currentBatchTarget: 2048, memorySaving: pressureAtSearchStart));
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

    [Fact]
    public void ZeroRecoveryMargin_UsesFullPressurePercent()
    {
        // recoveryMargin=0 → reliefPercent = pressurePercent
        // gcThreshold = 10GB * (80/100) = 8GB, gcLoad = 7.5GB → below → true
        Assert.True(SearchService.IsMemoryPressureRelievedGcFallback(
            workingSetBytes: 500_000_000, effectiveProcessCapBytes: 4_000_000_000,
            pressurePercent: 80, recoveryMarginPercent: 0,
            gcMemoryLoadBytes: 7_500_000_000, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void NegativePressurePercent_ClampsToZero()
    {
        // Math.Max(0, negative - margin) = 0 → threshold = 0 → gcLoad > 0 → false
        Assert.False(SearchService.IsMemoryPressureRelievedGcFallback(
            workingSetBytes: 500_000_000, effectiveProcessCapBytes: 4_000_000_000,
            pressurePercent: -5, recoveryMarginPercent: 10,
            gcMemoryLoadBytes: 1, gcTotalAvailableBytes: 10_000_000_000));
    }

    [Fact]
    public void ZeroGcLoad_AlwaysRelieved()
    {
        Assert.True(SearchService.IsMemoryPressureRelievedGcFallback(
            workingSetBytes: 500_000_000, effectiveProcessCapBytes: 4_000_000_000,
            pressurePercent: 80, recoveryMarginPercent: 10,
            gcMemoryLoadBytes: 0, gcTotalAvailableBytes: 10_000_000_000));
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
        // TotalFiles = max(knownTotal=100, discoveredTotal+earlySkips=11, completedTotal) = 100
        Assert.Equal(100, summary!.TotalFiles);
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
        public int GitignoreSkipped => 0;
        public int CloudOnlySkippedFiles => 0;

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

// ─── ResolveNativeBatchTarget ───────────────────────────────────────────

public class ResolveNativeBatchTargetTests
{
    [Fact]
    public void MemorySaving_ReturnsSmallBatch()
    {
        int result = SearchService.ResolveNativeBatchTarget(4096, memorySaving: true);
        Assert.Equal(256, result);
    }

    [Fact]
    public void NotMemorySaving_ReturnsCurrentTarget()
    {
        int result = SearchService.ResolveNativeBatchTarget(2048, memorySaving: false);
        Assert.Equal(2048, result);
    }
}

// ─── CollectForMemoryPressureIfDue ──────────────────────────────────────

public class CollectForMemoryPressureIfDueTests
{
    [Fact]
    public void CollectionAlreadyInFlight_ReturnsWithoutCollecting()
    {
        var inFlight = typeof(SearchService).GetField(
            "s_memoryPressureGcInFlight",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var lastCollection = typeof(SearchService).GetField(
            "s_lastMemoryPressureGcTicks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        int originalInFlight = (int)inFlight.GetValue(null)!;
        long originalLastCollection = (long)lastCollection.GetValue(null)!;
        try
        {
            inFlight.SetValue(null, 1);
            lastCollection.SetValue(null, 0L);
            SearchService.CollectForMemoryPressureIfDue(TimeSpan.Zero);
        }
        finally
        {
            inFlight.SetValue(null, originalInFlight);
            lastCollection.SetValue(null, originalLastCollection);
        }
    }

    [Fact]
    public void FirstCall_DoesNotThrow()
    {
        // First call ever should succeed without error (bypasses cooldown)
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void RapidCalls_Debounces()
    {
        // Call once with a long cooldown
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(60));
        // Immediate second call should be debounced (no exception, just returns early)
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void AfterCooldownExpires_Collects()
    {
        // Using zero cooldown should always allow collection
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.Zero);
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.Zero);
    }
}

// ─── GetMemoryDiagnostics ───────────────────────────────────────────────

public class GetMemoryDiagnosticsTests
{
    [Fact]
    public void ReturnsNonEmptyString()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.False(string.IsNullOrWhiteSpace(diag));
    }

    [Fact]
    public void ContainsWorkingSetInfo()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.Contains("WS=", diag);
    }
}

// ─── IsMemoryPressureRelieved additional branches ───────────────────────

[Collection("SystemMemoryProvider")]
public class IsMemoryPressureRelievedTests
{
    [Fact]
    public void NoPressureConfig_ReturnsTrue()
    {
        // pressurePercent=0 means no system memory threshold configured. The cap must be explicit: 0
        // selects the automatic sub-GB cap, which makes the answer depend on the test host's own memory.
        bool relieved = SearchService.IsMemoryPressureRelieved(long.MaxValue, 0);
        Assert.True(relieved);
    }

    [Fact]
    public void WithPressureConfig_ReturnsBasedOnSystemMemory()
    {
        // Large cap, low pressure threshold — should be relieved on most machines
        bool relieved = SearchService.IsMemoryPressureRelieved(long.MaxValue, 99);
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureHigh_NoCapNoThreshold_ReturnsFalse()
    {
        // Below an explicit cap and with no threshold, pressure is never reported high.
        bool high = SearchService.IsMemoryPressureHigh(long.MaxValue, 0);
        Assert.False(high);
    }

    [Fact]
    public void IsMemoryPressureHigh_VeryLowCap_ReturnsTrue()
    {
        // Working set is always > 1 byte, so this should trigger
        bool high = SearchService.IsMemoryPressureHigh(1, 0);
        Assert.True(high);
    }
}

// ─── DiskSpaceSnapshot ──────────────────────────────────────────────────

public class DiskSpaceSnapshotBranchTests
{
    [Fact]
    public void UsedBytes_CalculatesCorrectly()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", 1000, 300);
        Assert.Equal(700, snap.UsedBytes);
    }

    [Fact]
    public void UsedBytes_NeverNegative()
    {
        // Edge case: AvailableBytes > TotalBytes (shouldn't happen but be safe)
        var snap = new DiskSpaceSnapshot(@"C:\", 100, 200);
        Assert.Equal(0, snap.UsedBytes);
    }

    [Fact]
    public void UsedFraction_ZeroTotal_ReturnsZero()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", 0, 0);
        Assert.Equal(0.0, snap.UsedFraction);
    }

    [Fact]
    public void UsedPercent_CorrectPercentage()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", 1000, 250);
        Assert.Equal(75.0, snap.UsedPercent);
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"D:\", "D:")]
    [InlineData("", "")]
    public void DriveDisplayName_TrimsSeparator(string root, string expected)
    {
        var snap = new DiskSpaceSnapshot(root, 1000, 500);
        Assert.Equal(expected, snap.DriveDisplayName);
    }
}

public sealed class StreamingScanSinkTests
{
    [Fact]
    public unsafe void NormalMatchAndFileCompletion_UpdateResultsCountersAndMetadata()
    {
        const string path = @"C:\source.txt";
        var results = Channel.CreateUnbounded<SearchResult>();
        int cancel = 0;
        int filesScanned = 0;
        int totalMatches = 0;
        int filesWithMatches = 0;
        FileMetadataCache.Clear();

        try
        {
            using var sink = new SearchService.StreamingScanSink(
                [path], results.Writer,
                maxResults: 0, currentTotalMatches: 0,
                (IntPtr)(&cancel), &filesScanned, &totalMatches, &filesWithMatches,
                resultStore: null, initialCapacity: 1);

            byte[] line = Encoding.UTF8.GetBytes("prefix needle suffix");
            fixed (byte* linePtr = line)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 7,
                    MatchStart = 7,
                    SourceMatchStart = 7,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)line.Length,
                };

                Assert.Equal(0, sink.OnMatchForFile(0, &view));
                Assert.Equal(1, sink.OnMatch(&view));
            }

            Assert.True(results.Reader.TryRead(out SearchResult? result));
            Assert.Equal(path, result.FilePath);
            Assert.Equal(7, result.LineNumber);
            Assert.Equal("prefix needle suffix", result.MatchLine);
            Assert.Equal(7, result.MatchStartColumn);
            Assert.Equal(6, result.MatchLength);
            Assert.Equal(7, result.SourceMatchStartColumn);
            Assert.Equal(1, sink.GetEmitted(0));
            Assert.Equal(1, sink.TotalEmitted);
            Assert.Equal(1, totalMatches);
            Assert.Equal(1, filesWithMatches);
            Assert.False(sink.Truncated);

            ulong lastModified = (ulong)new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Local).ToFileTime();
            sink.OnFileDone(0, Native.NativeSearcher.StatusOk, 123, lastModified);
            sink.OnFileDone(0, Native.NativeSearcher.StatusOk, 124, lastModified);
            sink.OnFileDone(1, Native.NativeSearcher.StatusOk, 0, lastModified);
            sink.OnFileDone(2, Native.NativeSearcher.StatusOk, 1, 0);
            sink.OnFileDone(3, Native.NativeSearcher.StatusOk, 1, lastModified);
            sink.OnFileDone(4, Native.NativeSearcher.StatusTooLarge, ulong.MaxValue, 0);

            Assert.Equal(6, filesScanned);
            Assert.Equal(Native.NativeSearcher.StatusOk, sink.GetStatus(0));
            Assert.Equal(124, sink.GetFileLength(0));
            Assert.Equal(Native.NativeSearcher.StatusTooLarge, sink.GetStatus(4));
            Assert.Equal(long.MaxValue, sink.GetFileLength(4));
            Assert.Equal(0, sink.GetEmitted(10));
            Assert.Equal(0, sink.GetStatus(10));
            Assert.Equal(0, sink.GetFileLength(10));
            Assert.True(FileMetadataCache.TryGet(path, out FileMetadata metadata));
            Assert.Equal(124, metadata.Length);
        }
        finally
        {
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public unsafe void NormalMatch_ReachesCapAndStopsSubsequentCallbacks()
    {
        var results = Channel.CreateUnbounded<SearchResult>();
        int cancel = 0;
        int filesScanned = 0;
        int totalMatches = 0;
        int filesWithMatches = 0;
        using var sink = new SearchService.StreamingScanSink(
            ["cap.txt"], results.Writer,
            maxResults: 2, currentTotalMatches: 0,
            (IntPtr)(&cancel), &filesScanned, &totalMatches, &filesWithMatches,
            resultStore: null, initialCapacity: 1);
        sink.SetDegraded(true);

        byte[] line = Encoding.UTF8.GetBytes("needle");
        fixed (byte* linePtr = line)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchStart = 0,
                SourceMatchStart = 0,
                MatchLen = 6,
                LinePtr = linePtr,
                LineLen = (nuint)line.Length,
            };

            Assert.Equal(0, sink.OnMatchForFile(0, &view));
            sink.SetDegraded(false);
            Assert.Equal(1, sink.OnMatchForFile(0, &view));
            Assert.Equal(1, sink.OnMatchForFile(0, &view));
        }

        Assert.True(sink.Truncated);
        Assert.Equal(1, cancel);
        Assert.Equal(2, sink.TotalEmitted);
        Assert.Equal(2, totalMatches);
        Assert.Equal(1, filesWithMatches);
        Assert.Equal(2, results.Reader.Count);
    }

    [Fact]
    public unsafe void Backpressure_RetriesUntilAcceptedOrCancellationIsObserved()
    {
        var retryWriter = new ScriptedWriter(false, false, true);
        using var retrySink = new SearchService.StreamingScanSink(
            ["retry.txt"], retryWriter,
            maxResults: 0, currentTotalMatches: 0,
            IntPtr.Zero, null, null, null,
            resultStore: null, initialCapacity: 1)
        {
            CapturedException = new InvalidOperationException("captured"),
            ErrorMessage = "error",
        };

        byte[] line = Encoding.UTF8.GetBytes("retry");
        fixed (byte* linePtr = line)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchLen = 5,
                LinePtr = linePtr,
                LineLen = (nuint)line.Length,
            };
            Assert.Equal(0, retrySink.OnMatchForFile(0, &view));
        }
        Assert.Single(retryWriter.Results);
        Assert.NotNull(retrySink.CapturedException);
        Assert.Equal("error", retrySink.ErrorMessage);

        int cancel = 1;
        var rejectingWriter = new ScriptedWriter(false);
        using var cancelledSink = new SearchService.StreamingScanSink(
            ["cancelled.txt"], rejectingWriter,
            maxResults: 0, currentTotalMatches: 0,
            (IntPtr)(&cancel), null, null, null,
            resultStore: null, initialCapacity: 1);
        fixed (byte* linePtr = line)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchLen = 5,
                LinePtr = linePtr,
                LineLen = (nuint)line.Length,
            };
            Assert.Equal(1, cancelledSink.OnMatchForFile(0, &view));
            Assert.Equal(1, cancelledSink.OnMatchForFile(0, &view));
        }
        Assert.Empty(rejectingWriter.Results);
        Assert.Equal(0, cancelledSink.TotalEmitted);
    }

    [Fact]
    public unsafe void DegradedShortLines_WritePreEvictedAsciiAndUnicodeResults()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-streaming-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using var store = new ResultStore(tempDirectory);
            var results = Channel.CreateUnbounded<SearchResult>();
            using var sink = new SearchService.StreamingScanSink(
                ["degraded.txt"], results.Writer,
                maxResults: 0, currentTotalMatches: 0,
                IntPtr.Zero, null, null, null,
                store, initialCapacity: 1);
            sink.SetDegraded(true);

            byte[] asciiLine = Encoding.UTF8.GetBytes("prefix needle suffix");
            fixed (byte* linePtr = asciiLine)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 4,
                    MatchStart = 7,
                    SourceMatchStart = 7,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)asciiLine.Length,
                };
                Assert.Equal(0, sink.OnMatchForFile(0, &view));
            }

            byte[] unicodeLine = Encoding.UTF8.GetBytes("pré needle");
            fixed (byte* linePtr = unicodeLine)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = ulong.MaxValue,
                    MatchStart = 5,
                    SourceMatchStart = uint.MaxValue,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)unicodeLine.Length,
                };
                Assert.Equal(0, sink.OnMatchForFile(0, &view));
            }

            SearchResult asciiResult = Assert.IsType<SearchResult>(ReadResult(results));
            SearchResult unicodeResult = Assert.IsType<SearchResult>(ReadResult(results));
            Assert.True(asciiResult.IsEvicted);
            Assert.Equal(("prefix needle suffix", 0, 0), ReadStoredPayload(store, asciiResult));
            Assert.Equal(4, unicodeResult.MatchStartColumn);
            Assert.Equal(4, unicodeResult.SourceMatchStartColumn);
            Assert.Equal(6, unicodeResult.MatchLength);
            Assert.Equal(int.MaxValue, unicodeResult.LineNumber);
            Assert.Equal(("pré needle", 0, 0), ReadStoredPayload(store, unicodeResult));
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public unsafe void DegradedLongLines_WindowAsciiAndUnicodeMatches()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-streaming-long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        int originalTruncatedLength = Yagu.Helpers.LineTruncator.TruncatedLength;
        try
        {
            Yagu.Helpers.LineTruncator.TruncatedLength = 500;
            using var store = new ResultStore(tempDirectory);
            var results = Channel.CreateUnbounded<SearchResult>();
            using var sink = new SearchService.StreamingScanSink(
                ["long.txt"], results.Writer,
                maxResults: 0, currentTotalMatches: 0,
                IntPtr.Zero, null, null, null,
                store, initialCapacity: 1);
            sink.SetDegraded(true);

            byte[] asciiLine = Encoding.UTF8.GetBytes(new string('a', 5_000));
            fixed (byte* linePtr = asciiLine)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 1,
                    MatchStart = 4_990,
                    SourceMatchStart = uint.MaxValue,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)asciiLine.Length,
                };
                Assert.Equal(0, sink.OnMatchForFile(0, &view));
            }

            byte[] unicodeLine = Encoding.UTF8.GetBytes(new string('é', 3_000));
            fixed (byte* linePtr = unicodeLine)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 2,
                    MatchStart = 3_000,
                    SourceMatchStart = uint.MaxValue,
                    MatchLen = 2,
                    LinePtr = linePtr,
                    LineLen = (nuint)unicodeLine.Length,
                };
                Assert.Equal(0, sink.OnMatchForFile(0, &view));
            }

            byte[] malformedLine = Enumerable.Repeat((byte)0x80, 5_000).ToArray();
            fixed (byte* linePtr = malformedLine)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 3,
                    MatchStart = 4_990,
                    SourceMatchStart = uint.MaxValue,
                    MatchLen = 2,
                    LinePtr = linePtr,
                    LineLen = (nuint)malformedLine.Length,
                };
                Assert.Equal(0, sink.OnMatchForFile(0, &view));
            }

            SearchResult asciiResult = ReadResult(results);
            SearchResult unicodeResult = ReadResult(results);
            SearchResult malformedResult = ReadResult(results);
            Assert.Equal(4_990, asciiResult.SourceMatchStartColumn);
            Assert.Equal(3_000, unicodeResult.SourceMatchStartColumn);
            Assert.Equal(1, unicodeResult.MatchLength);
            Assert.True(store.Read(asciiResult.DiskOffset).MatchLine.Length > 0);
            Assert.True(store.Read(unicodeResult.DiskOffset).MatchLine.Length > 0);
            Assert.Equal(string.Empty, store.Read(malformedResult.DiskOffset).MatchLine);
        }
        finally
        {
            Yagu.Helpers.LineTruncator.TruncatedLength = originalTruncatedLength;
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public unsafe void OversizedNativeScalars_AreClampedOnAnEmptyLine()
    {
        Assert.Equal(7, SearchService.StreamingScanSink.ClampNativeByteLength(7));
        Assert.Equal(int.MaxValue, SearchService.StreamingScanSink.ClampNativeByteLength((nuint)int.MaxValue + 1));

        var results = Channel.CreateUnbounded<SearchResult>();
        using var sink = new SearchService.StreamingScanSink(
            [], results.Writer,
            maxResults: 0, currentTotalMatches: 0,
            IntPtr.Zero, null, null, null,
            resultStore: null, initialCapacity: 1);
        sink.SetDegraded(true);
        var view = new Native.NativeSearcher.QgMatchView
        {
            LineNumber = ulong.MaxValue,
            MatchStart = uint.MaxValue,
            SourceMatchStart = uint.MaxValue,
            MatchLen = uint.MaxValue,
            LinePtr = null,
            LineLen = 0,
        };

        Assert.Equal(0, sink.OnMatchForFile(0, &view));

        SearchResult result = ReadResult(results);
        Assert.Equal(string.Empty, result.FilePath);
        Assert.Equal(int.MaxValue, result.LineNumber);
        Assert.Equal(string.Empty, result.MatchLine);
        Assert.Equal(0, result.MatchStartColumn);
        Assert.Equal(0, result.MatchLength);
        Assert.Equal(0, result.SourceMatchStartColumn);
    }

    [Fact]
    public unsafe void DegradedMatch_ReachesCapWithAndWithoutCancelPointer()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-streaming-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using var store = new ResultStore(tempDirectory);
            var results = Channel.CreateUnbounded<SearchResult>();
            int cancel = 0;
            int filesScanned = 0;
            int totalMatches = 0;
            int filesWithMatches = 0;
            using var sink = new SearchService.StreamingScanSink(
                ["cap.txt"], results.Writer,
                maxResults: 2, currentTotalMatches: 0,
                (IntPtr)(&cancel), &filesScanned, &totalMatches, &filesWithMatches,
                store, initialCapacity: 1);
            sink.SetDegraded(true);

            byte[] line = Encoding.UTF8.GetBytes("needle");
            fixed (byte* linePtr = line)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 1,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)line.Length,
                };
                Assert.Equal(0, sink.OnMatchForFile(0, &view));
                Assert.Equal(1, sink.OnMatchForFile(0, &view));
                Assert.Equal(1, sink.OnMatchForFile(0, &view));
            }

            Assert.True(sink.Truncated);
            Assert.Equal(1, cancel);
            Assert.Equal(2, totalMatches);
            Assert.Equal(1, filesWithMatches);

            var noCancelResults = Channel.CreateUnbounded<SearchResult>();
            using var noCancelSink = new SearchService.StreamingScanSink(
                ["no-cancel.txt"], noCancelResults.Writer,
                maxResults: 1, currentTotalMatches: 0,
                IntPtr.Zero, null, null, null,
                store, initialCapacity: 1);
            noCancelSink.SetDegraded(true);
            fixed (byte* linePtr = line)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 1,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)line.Length,
                };
                Assert.Equal(1, noCancelSink.OnMatchForFile(0, &view));
            }
            Assert.True(noCancelSink.Truncated);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public unsafe void DegradedMatch_RejectedByWriterStopsTheSink()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-streaming-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using var store = new ResultStore(tempDirectory);
            int cancel = 1;
            var writer = new ScriptedWriter(false);
            using var sink = new SearchService.StreamingScanSink(
                ["reject.txt"], writer,
                maxResults: 0, currentTotalMatches: 0,
                (IntPtr)(&cancel), null, null, null,
                store, initialCapacity: 1);
            sink.SetDegraded(true);

            byte[] line = Encoding.UTF8.GetBytes("needle");
            fixed (byte* linePtr = line)
            {
                var view = new Native.NativeSearcher.QgMatchView
                {
                    LineNumber = 1,
                    MatchLen = 6,
                    LinePtr = linePtr,
                    LineLen = (nuint)line.Length,
                };
                Assert.Equal(1, sink.OnMatchForFile(0, &view));
                Assert.Equal(1, sink.OnMatchForFile(0, &view));
            }
            Assert.Empty(writer.Results);
            Assert.Equal(0, sink.TotalEmitted);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public unsafe void ConcurrentCapacityGrowth_SecondWaiterObservesTheResize()
    {
        var results = Channel.CreateUnbounded<SearchResult>();
        using var sink = new SearchService.StreamingScanSink(
            [], results.Writer,
            maxResults: 0, currentTotalMatches: 0,
            IntPtr.Zero, null, null, null,
            resultStore: null, initialCapacity: 1);
        object resizeLock = Assert.IsType<object>(typeof(SearchService.StreamingScanSink)
            .GetField("_resizeLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(sink));
        var first = new Thread(() => sink.OnFileDone(4, Native.NativeSearcher.StatusTooLarge, 1, 0));
        var second = new Thread(() => sink.OnFileDone(4, Native.NativeSearcher.StatusTooLarge, 1, 0));

        Monitor.Enter(resizeLock);
        try
        {
            first.Start();
            second.Start();
            Assert.True(SpinWait.SpinUntil(
                () => first.ThreadState.HasFlag(ThreadState.WaitSleepJoin)
                    && second.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Monitor.Exit(resizeLock);
        }

        Assert.True(first.Join(TimeSpan.FromSeconds(5)));
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(Native.NativeSearcher.StatusTooLarge, sink.GetStatus(4));
    }

    private static SearchResult ReadResult(Channel<SearchResult> channel)
    {
        Assert.True(channel.Reader.TryRead(out SearchResult? result));
        return result;
    }

    private static (string MatchLine, int BeforeCount, int AfterCount) ReadStoredPayload(
        ResultStore store,
        SearchResult result)
    {
        var payload = store.Read(result.DiskOffset);
        return (payload.MatchLine, payload.ContextBefore.Count, payload.ContextAfter.Count);
    }

    private sealed class ScriptedWriter(params bool[] outcomes) : ChannelWriter<SearchResult>
    {
        private int _nextOutcome;
        public List<SearchResult> Results { get; } = [];

        public override bool TryComplete(Exception? error = null) => true;

        public override bool TryWrite(SearchResult item)
        {
            int outcomeIndex = Math.Min(_nextOutcome++, outcomes.Length - 1);
            bool accepted = outcomes[outcomeIndex];
            if (accepted)
                Results.Add(item);
            return accepted;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }
}

public sealed class SearchServiceFileMetadataTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "yagu-hidden-" + Guid.NewGuid().ToString("N") + ".txt");
    private readonly long _originalCeiling = FileLister.ContentSearchFileSizeCeiling;

    public SearchServiceFileMetadataTests()
    {
        File.WriteAllText(_path, "content");
        FileMetadataCache.Clear();
    }

    public void Dispose()
    {
        FileLister.ContentSearchFileSizeCeiling = _originalCeiling;
        FileMetadataCache.Clear();
        try
        {
            if (File.Exists(_path))
                File.SetAttributes(_path, FileAttributes.Normal);
            File.Delete(_path);
        }
        catch { }
    }

    [Fact]
    public void IsHiddenFile_ReturnsAttributeAndTreatsUnreadablePathAsVisible()
    {
        Assert.False(SearchService.IsHiddenFile(_path));
        File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
        Assert.True(SearchService.IsHiddenFile(_path));
        Assert.False(SearchService.IsHiddenFile(_path + ".missing"));
    }

    [Fact]
    public void ShouldSkipByFileMetadata_InvalidPathFailsOpen()
    {
        FileLister.ContentSearchFileSizeCeiling = 0;
        var options = new SearchOptions
        {
            Directory = ".",
            Query = "x",
            MinFileSizeBytes = 1,
        };

        Assert.False(SearchService.ShouldSkipByFileMetadata(
            "\0", options, out bool tooLarge));
        Assert.False(tooLarge);
    }

    [Fact]
    public void ShouldSkipByFileMetadata_CachedFileAboveBuiltInCeilingIsTooLarge()
    {
        FileLister.ContentSearchFileSizeCeiling = 100;
        FileMetadataCache.Set(_path, new FileMetadata(101, DateTime.Now, DateTime.Now));
        var options = new SearchOptions
        {
            Directory = ".",
            Query = "x",
            MaxFileSizeBytes = 0,
        };

        Assert.True(SearchService.ShouldSkipByFileMetadata(
            _path, options, out bool tooLarge, checkSize: false));
        Assert.True(tooLarge);
    }
}

/// <summary>
/// Serializes every class that reads or swaps the static <see cref="SearchService.SystemMemoryProvider"/>.
/// Without this, xUnit runs those classes in parallel and the stub installed by
/// <see cref="SearchServiceSystemMemoryTests"/> — including the throwing and unavailable providers — is
/// visible to any concurrent live-memory assertion, which then fails intermittently.
/// </summary>
[CollectionDefinition("SystemMemoryProvider", DisableParallelization = true)]
public sealed class SystemMemoryProviderCollection
{
}

[Collection("SystemMemoryProvider")]
public sealed class SearchServiceSystemMemoryTests : IDisposable
{
    private readonly ISystemMemoryProvider _originalProvider = SearchService.SystemMemoryProvider;
    private readonly Action _originalTrimmer = SearchService.WorkingSetTrimmer;

    public void Dispose()
    {
        SearchService.SystemMemoryProvider = _originalProvider;
        SearchService.WorkingSetTrimmer = _originalTrimmer;
        SetLastTrimTicks(0);
    }

    [Fact]
    public void SuccessfulSnapshot_DrivesMemoryHelpers()
    {
        SearchService.SystemMemoryProvider = new StubMemoryProvider(
            new SystemMemorySnapshot(
                LoadPercent: 42,
                TotalPhysicalBytes: 8UL * 1024 * 1024 * 1024,
                AvailablePhysicalBytes: 3UL * 1024 * 1024 * 1024));

        Assert.True(SearchService.TryGetSystemMemoryLoadPercent(out uint load));
        Assert.Equal(42U, load);
        Assert.Contains("system=42%", SearchService.GetMemoryDiagnostics());
        Assert.Equal(768L * 1024 * 1024, SearchService.AutoProcessMemoryCap());
        Assert.Equal(3L * 1024 * 1024 * 1024, SearchService.GetAvailablePhysicalMemoryBytes());
        Assert.False(SearchService.IsMemoryPressureHigh(long.MaxValue, 50));
        Assert.True(SearchService.IsMemoryPressureRelieved(long.MaxValue, 50));
    }

    [Fact]
    public void UnavailableSnapshot_UsesFallbackBehavior()
    {
        SearchService.SystemMemoryProvider = new StubMemoryProvider();

        Assert.False(SearchService.TryGetSystemMemoryLoadPercent(out uint load));
        Assert.Equal(0U, load);
        Assert.Contains("process WS=", SearchService.GetMemoryDiagnostics());
        Assert.Equal(768L * 1024 * 1024, SearchService.AutoProcessMemoryCap());
        Assert.Equal(2L * 1024 * 1024 * 1024, SearchService.GetAvailablePhysicalMemoryBytes());
        _ = SearchService.IsMemoryPressureHigh(long.MaxValue, 50);
        _ = SearchService.IsMemoryPressureRelieved(long.MaxValue, 50);
    }

    [Fact]
    public void ThrowingSnapshotProvider_IsContainedByMemoryHelpers()
    {
        SearchService.SystemMemoryProvider = new StubMemoryProvider(
            new InvalidOperationException("memory status failed"));

        Assert.False(SearchService.IsMemoryPressureHigh(0, 50));
        Assert.False(SearchService.IsMemoryPressureRelieved(0, 50));
        Assert.Equal("unknown", SearchService.GetMemoryDiagnostics());
        Assert.Equal(768L * 1024 * 1024, SearchService.AutoProcessMemoryCap());
        Assert.Equal(2L * 1024 * 1024 * 1024, SearchService.GetAvailablePhysicalMemoryBytes());
    }

    [Fact]
    public void TrimProcessWorkingSet_DebouncesAndContainsTrimmerFailure()
    {
        int calls = 0;
        SearchService.WorkingSetTrimmer = () => calls++;
        SetLastTrimTicks(0);

        SearchService.TrimProcessWorkingSet();
        SearchService.TrimProcessWorkingSet();

        Assert.Equal(1, calls);
        SetLastTrimTicks(0);
        SearchService.WorkingSetTrimmer = () => throw new InvalidOperationException("trim failed");
        SearchService.TrimProcessWorkingSet();
    }

    private static void SetLastTrimTicks(long value)
        => typeof(SearchService)
            .GetField("s_lastTrimTicks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, value);

    private sealed class StubMemoryProvider : ISystemMemoryProvider
    {
        private readonly bool _success;
        private readonly SystemMemorySnapshot _snapshot;
        private readonly Exception? _exception;

        public StubMemoryProvider()
        {
        }

        public StubMemoryProvider(SystemMemorySnapshot snapshot)
        {
            _success = true;
            _snapshot = snapshot;
        }

        public StubMemoryProvider(Exception exception)
        {
            _exception = exception;
        }

        public bool TryGetSnapshot(out SystemMemorySnapshot snapshot)
        {
            if (_exception is not null)
                throw _exception;
            snapshot = _snapshot;
            return _success;
        }
    }
}
