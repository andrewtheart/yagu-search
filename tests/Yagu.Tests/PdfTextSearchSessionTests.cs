using System.Text;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services.Ocr;
using Yagu.Services.Pdf;

namespace Yagu.Tests;

public sealed class PdfTextSearchSessionTests : IDisposable
{
    private readonly string _root;
    private readonly OcrTextCache _cache;

    public PdfTextSearchSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-pdfsession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _cache = new OcrTextCache(Path.Combine(_root, "cache"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string WritePdf(string name)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, "%PDF-fake-bytes", new UTF8Encoding(false));
        return p;
    }

    // Returns canned extraction results per path; counts invocations.
    private sealed class FakePdfTextExtractor : PdfTextExtractor
    {
        private readonly IReadOnlyDictionary<string, PdfTextResult> _results;
        public int ExtractCalls;

        public FakePdfTextExtractor(IReadOnlyDictionary<string, PdfTextResult> results) => _results = results;

        public override Task<PdfTextResult> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ExtractCalls);
            return Task.FromResult(_results.TryGetValue(pdfPath, out var r) ? r : PdfTextResult.Fail("no text"));
        }
    }

    private sealed class ThrowingPdfTextExtractor : PdfTextExtractor
    {
        private readonly Exception _toThrow;
        public ThrowingPdfTextExtractor(Exception toThrow) => _toThrow = toThrow;
        public override Task<PdfTextResult> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
            => throw _toThrow;
    }

    private PdfTextSearchSession CreateSession(
        PdfTextExtractor extractor,
        Channel<SearchResult> sink,
        Action onProcessed,
        Action<int> onMatched,
        Func<bool>? shouldStop = null,
        CancellationToken cancellationToken = default)
        => new(
            extractor,
            _cache,
            regex: null,
            literal: "needle",
            comparison: StringComparison.OrdinalIgnoreCase,
            contextLines: 0,
            maxMatchesPerFile: 0,
            sink: sink.Writer,
            onFileProcessed: onProcessed,
            onFileMatched: onMatched,
            workerCount: 1,
            cancellationToken: cancellationToken,
            shouldStop: shouldStop);

    private static async Task<List<SearchResult>> DrainSinkAsync(
        PdfTextSearchSession session, Channel<SearchResult> sink, IEnumerable<string> paths)
    {
        session.Start();
        foreach (var p in paths) session.TryEnqueue(p);
        session.Complete();
        await session.DrainAsync();
        sink.Writer.TryComplete();

        var results = new List<SearchResult>();
        await foreach (var r in sink.Reader.ReadAllAsync())
            results.Add(r);
        return results;
    }

    [Fact]
    public async Task Session_ExtractsPdfAndWritesMatchesToSink()
    {
        string pdf = WritePdf("a.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Ok("this has a needle in it"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0, matched = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), n => Interlocked.Add(ref matched, n));

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Single(results);
        Assert.Equal(pdf, results[0].FilePath);
        Assert.Contains("needle", results[0].MatchLine);
        Assert.Equal(1, processed);
        Assert.Equal(1, matched);
        Assert.Equal(1, extractor.ExtractCalls);
        Assert.Equal("pdftotext", session.EngineId);
    }

    [Fact]
    public async Task Session_UsesCachedTextWithoutReExtracting()
    {
        string pdf = WritePdf("cached.pdf");
        // Pre-seed the cache for the pdftotext engine id, then a failing extractor: a cache hit means
        // ExtractAsync is never called yet the match is still found.
        _cache.Set(pdf, "pdftotext", "the needle is cached");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>()); // any call -> Fail
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0, matched = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), n => Interlocked.Add(ref matched, n));

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Single(results);
        Assert.Equal(0, extractor.ExtractCalls);
        Assert.Equal(1, matched);
    }

    [Fact]
    public async Task Session_CachesExtractedTextForReuse()
    {
        string pdf = WritePdf("reuse.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Ok("a needle here"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(extractor, sink, () => { }, _ => { });
        await DrainSinkAsync(session, sink, new[] { pdf });

        // The session must have written the extracted text into the shared cache.
        Assert.True(_cache.TryGet(pdf, "pdftotext", out var cached));
        Assert.Equal("a needle here", cached);
        Assert.Equal(1, extractor.ExtractCalls);
    }

    [Fact]
    public async Task Session_ExtractionFailure_ProducesNoMatchesButStillCountsProcessed()
    {
        string pdf = WritePdf("fail.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Fail("encrypted"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0, matched = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), n => Interlocked.Add(ref matched, n));

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Empty(results);
        Assert.Equal(1, processed);
        Assert.Equal(0, matched);
        Assert.False(_cache.TryGet(pdf, "pdftotext", out _)); // failed extraction is not cached
    }

    [Fact]
    public async Task Session_EmptyExtractedText_ProducesNoMatches()
    {
        string pdf = WritePdf("empty.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Ok(string.Empty),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), _ => { });

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Empty(results);
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task Session_NoMatchInText_ProducesNoResults()
    {
        string pdf = WritePdf("nomatch.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Ok("nothing relevant here"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0, matched = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), n => Interlocked.Add(ref matched, n));

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Empty(results);
        Assert.Equal(1, processed);
        Assert.Equal(0, matched);
    }

    [Fact]
    public async Task Session_ShouldStop_SkipsProcessingButCountsFile()
    {
        string pdf = WritePdf("stop.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Ok("a needle"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), _ => { }, shouldStop: () => true);

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Empty(results);
        Assert.Equal(1, processed);
        Assert.Equal(0, extractor.ExtractCalls); // skipped before extraction
    }

    [Fact]
    public async Task Session_ExtractorThrows_IsCaughtAndFileStillCounted()
    {
        string pdf = WritePdf("throw.pdf");
        var extractor = new ThrowingPdfTextExtractor(new InvalidOperationException("boom"));
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), _ => { });

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Empty(results);
        Assert.Equal(1, processed); // finally still runs
    }

    [Fact]
    public async Task Session_ExtractorThrowsOperationCanceled_BreaksWorkerLoop()
    {
        string pdf = WritePdf("cancel.pdf");
        var extractor = new ThrowingPdfTextExtractor(new OperationCanceledException());
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), _ => { });

        var results = await DrainSinkAsync(session, sink, new[] { pdf });

        Assert.Empty(results);
        // The inner OperationCanceledException breaks the worker loop; the file is still counted via finally.
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task Session_MultipleFiles_AllProcessed()
    {
        string a = WritePdf("m1.pdf"), b = WritePdf("m2.pdf"), c = WritePdf("m3.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [a] = PdfTextResult.Ok("needle one"),
            [b] = PdfTextResult.Ok("no hit"),
            [c] = PdfTextResult.Ok("another needle"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0, matched = 0;
        var session = CreateSession(extractor, sink, () => Interlocked.Increment(ref processed), n => Interlocked.Add(ref matched, n));

        var results = await DrainSinkAsync(session, sink, new[] { a, b, c });

        Assert.Equal(2, results.Count);
        Assert.Equal(3, processed);
        Assert.Equal(2, matched);
    }

    [Fact]
    public async Task CancelledToken_WorkerLoopExitsCleanly()
    {
        string pdf = WritePdf("ct.pdf");
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>
        {
            [pdf] = PdfTextResult.Ok("a needle"),
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = CreateSession(extractor, sink, () => { }, _ => { }, cancellationToken: cts.Token);

        // A pre-cancelled token makes the reader throw OperationCanceledException, caught by the outer
        // handler; DrainAsync completes without faulting.
        session.Start();
        session.TryEnqueue(pdf);
        session.Complete();
        await session.DrainAsync();

        sink.Writer.TryComplete();
        var results = new List<SearchResult>();
        await foreach (var r in sink.Reader.ReadAllAsync())
            results.Add(r);
        Assert.Empty(results);
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(extractor, sink, () => { }, _ => { });

        session.Start();
        session.Start(); // second call is a no-op
        session.Complete();
    }

    [Fact]
    public async Task DrainAsync_BeforeStart_CompletesImmediately()
    {
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(extractor, sink, () => { }, _ => { });

        // No workers were started, so DrainAsync is a completed task.
        await session.DrainAsync();
    }

    [Fact]
    public void TryEnqueue_ReturnsFalseAfterComplete()
    {
        var extractor = new FakePdfTextExtractor(new Dictionary<string, PdfTextResult>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(extractor, sink, () => { }, _ => { });

        Assert.True(session.TryEnqueue(WritePdf("q.pdf")));
        session.Complete();
        Assert.False(session.TryEnqueue(WritePdf("q2.pdf")));
    }
}
