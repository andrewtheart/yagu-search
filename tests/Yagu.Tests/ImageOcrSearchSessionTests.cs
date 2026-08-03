using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class ImageOcrSearchSessionTests : IDisposable
{
    private readonly string _root;
    private readonly OcrTextCache _cache;

    public ImageOcrSearchSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-ocrsession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _cache = new OcrTextCache(Path.Combine(_root, "cache"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string WriteImage(string name) => WriteImageWith(name, "fake-image-bytes");

    private string WriteImageWith(string name, string bytes)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, bytes, new UTF8Encoding(false));
        return p;
    }

    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly IReadOnlyDictionary<string, string> _texts;
        private readonly bool _ready;
        public int EnsureCalls;
        public int RecognizeCalls;

        public FakeOcrEngine(IReadOnlyDictionary<string, string> texts, bool ready = true)
        {
            _texts = texts;
            _ready = ready;
        }

        public string Id => "fake";
        public string DisplayName => "Fake OCR";

        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref EnsureCalls);
            return Task.FromResult(_ready ? OcrResult.Ok(string.Empty) : OcrResult.Fail("engine unavailable"));
        }

        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RecognizeCalls);
            return Task.FromResult(_texts.TryGetValue(imagePath, out var t)
                ? OcrResult.Ok(t)
                : OcrResult.Fail("no text"));
        }
    }

    private sealed class ThrowingOcrEngine : IOcrEngine
    {
        private readonly Exception _toThrow;
        public ThrowingOcrEngine(Exception toThrow) => _toThrow = toThrow;

        public string Id => "throwing";
        public string DisplayName => "Throwing OCR";

        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
            => Task.FromResult(OcrResult.Ok(string.Empty));

        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
            => throw _toThrow;
    }

    private static async Task<List<SearchResult>> DrainSinkAsync(
        ImageOcrSearchSession session, Channel<SearchResult> sink, IEnumerable<string> paths)
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

    private ImageOcrSearchSession CreateSession(
        IOcrEngine engine,
        Channel<SearchResult> sink,
        Action onProcessed,
        Action<int> onMatched,
        Func<bool>? shouldStop = null)
        => new(
            engine,
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
            cancellationToken: CancellationToken.None,
            shouldStop: shouldStop);

    [Fact]
    public async Task Session_OcrsImageAndWritesMatchesToSink()
    {
        string img = WriteImage("a.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "this has a needle in it" });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0, matched = 0;

        var session = CreateSession(engine, sink, () => Interlocked.Increment(ref processed),
            n => Interlocked.Add(ref matched, n));
        var results = await DrainSinkAsync(session, sink, new[] { img });

        var r = Assert.Single(results);
        Assert.Equal(img, r.FilePath);
        Assert.Equal(1, processed);
        Assert.Equal(1, matched);
        Assert.Equal(1, engine.RecognizeCalls);
    }

    [Fact]
    public async Task Session_UsesCachedTextAndSkipsEngine()
    {
        string img = WriteImage("b.png");
        _cache.Set(img, "fake", "cached needle line");
        var engine = new FakeOcrEngine(new Dictionary<string, string>());
        var sink = Channel.CreateUnbounded<SearchResult>();

        var session = CreateSession(engine, sink, () => { }, _ => { });
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Single(results);
        Assert.Equal(0, engine.RecognizeCalls);
        Assert.Equal(0, engine.EnsureCalls);
    }

    [Fact]
    public async Task Session_UnavailableEngine_ProducesNoResults()
    {
        string img = WriteImage("c.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "needle" }, ready: false);
        var sink = Channel.CreateUnbounded<SearchResult>();
        int matched = 0, processed = 0;

        var session = CreateSession(engine, sink, () => Interlocked.Increment(ref processed),
            n => Interlocked.Add(ref matched, n));
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
        Assert.Equal(0, matched);
        Assert.Equal(1, processed);
        Assert.Equal(0, engine.RecognizeCalls);
    }

    [Fact]
    public async Task Session_TextWithoutMatch_WritesNothing()
    {
        string img = WriteImage("d.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "nothing relevant here" });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int matched = 0;

        var session = CreateSession(engine, sink, () => { }, n => Interlocked.Add(ref matched, n));
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
        Assert.Equal(0, matched);
    }

    [Fact]
    public async Task Session_ShouldStop_SkipsProcessingButCountsFiles()
    {
        string img = WriteImage("e.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "needle" });
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;

        var session = CreateSession(engine, sink, () => Interlocked.Increment(ref processed),
            _ => { }, shouldStop: () => true);
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
        Assert.Equal(1, processed);
        Assert.Equal(0, engine.RecognizeCalls);
    }

    [Fact]
    public async Task Session_ProcessesMultipleImages()
    {
        string a = WriteImage("m1.png");
        string b = WriteImage("m2.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string>
        {
            [a] = "needle one",
            [b] = "needle two",
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        var processedPaths = new ConcurrentBag<int>();
        int processed = 0;

        var session = CreateSession(engine, sink, () => { processedPaths.Add(Interlocked.Increment(ref processed)); }, _ => { });
        var results = await DrainSinkAsync(session, sink, new[] { a, b });

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FilePath == a);
        Assert.Contains(results, r => r.FilePath == b);
        Assert.Equal(2, processed);
    }

    [Fact]
    public async Task Session_PrioritizedImageRunsBeforeNormallyQueuedImage()
    {
        string normal = WriteImage("normal.png");
        string priority = WriteImage("priority.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string>
        {
            [normal] = "normal needle",
            [priority] = "priority needle",
        });
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(engine, sink, () => { }, _ => { });

        Assert.True(session.TryEnqueue(normal, prioritized: false));
        Assert.True(session.TryEnqueue(priority, prioritized: true));
        session.Start();
        session.Complete();
        await session.DrainAsync();
        sink.Writer.TryComplete();

        var paths = new List<string>();
        await foreach (SearchResult result in sink.Reader.ReadAllAsync())
            paths.Add(result.FilePath);
        Assert.Equal([priority, normal], paths);
    }

    [Fact]
    public void Session_PrioritizedEnqueueAfterCompletionIsRejected()
    {
        var engine = new FakeOcrEngine(new Dictionary<string, string>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(engine, sink, () => { }, _ => { });
        session.Complete();

        Assert.False(session.TryEnqueue("closed.png", prioritized: true));
        Assert.False(session.TryEnqueue("closed.png", prioritized: false));
    }

    [Fact]
    public async Task WaitForNextPathAsync_PrefersPriorityAndReportsDequeue()
    {
        var priority = Channel.CreateUnbounded<string>();
        var normal = Channel.CreateUnbounded<string>();
        Assert.True(normal.Writer.TryWrite("normal.png"));
        Assert.True(priority.Writer.TryWrite("priority.png"));
        int dequeued = 0;

        string? path = await ImageOcrSearchSession.WaitForNextPathAsync(
            priority.Reader, normal.Reader, () => dequeued++, CancellationToken.None);

        Assert.Equal("priority.png", path);
        Assert.Equal(1, dequeued);
    }

    [Fact]
    public async Task WaitForNextPathAsync_ReadsRemainingChannelAfterOtherCompletes()
    {
        var completedPriority = Channel.CreateUnbounded<string>();
        completedPriority.Writer.TryComplete();
        var normal = Channel.CreateUnbounded<string>();
        Task<string?> normalRead = ImageOcrSearchSession.WaitForNextPathAsync(
            completedPriority.Reader, normal.Reader, () => { }, CancellationToken.None).AsTask();
        Assert.False(normalRead.IsCompleted);
        Assert.True(normal.Writer.TryWrite("normal.png"));

        Assert.Equal("normal.png", await normalRead);

        var priority = Channel.CreateUnbounded<string>();
        var completedNormal = Channel.CreateUnbounded<string>();
        completedNormal.Writer.TryComplete();
        Task<string?> priorityRead = ImageOcrSearchSession.WaitForNextPathAsync(
            priority.Reader, completedNormal.Reader, () => { }, CancellationToken.None).AsTask();
        Assert.False(priorityRead.IsCompleted);
        Assert.True(priority.Writer.TryWrite("priority.png"));

        Assert.Equal("priority.png", await priorityRead);
    }

    [Fact]
    public async Task WaitForNextPathAsync_OpenChannelsWaitForPriorityTurn()
    {
        var priority = Channel.CreateUnbounded<string>();
        var normal = Channel.CreateUnbounded<string>();
        int dequeued = 0;
        Task<string?> read = ImageOcrSearchSession.WaitForNextPathAsync(
            priority.Reader, normal.Reader, () => dequeued++, CancellationToken.None).AsTask();
        Assert.False(read.IsCompleted);
        Assert.True(priority.Writer.TryWrite("priority.png"));

        Assert.Equal("priority.png", await read);
        Assert.Equal(1, dequeued);
    }

    [Fact]
    public async Task WaitForNextPathAsync_ReturnsNullWhenChannelsCompleteWithoutItems()
    {
        var priority = Channel.CreateUnbounded<string>();
        var normal = Channel.CreateUnbounded<string>();
        priority.Writer.TryComplete();
        normal.Writer.TryComplete();

        Assert.Null(await ImageOcrSearchSession.WaitForNextPathAsync(
            priority.Reader, normal.Reader, () => { }, CancellationToken.None));
    }

    [Fact]
    public async Task WaitForNextPathAsync_ReturnsNullWhenRemainingChannelCompletesWhileWaiting()
    {
        var completedPriority = Channel.CreateUnbounded<string>();
        completedPriority.Writer.TryComplete();
        var normal = Channel.CreateUnbounded<string>();
        Task<string?> normalRead = ImageOcrSearchSession.WaitForNextPathAsync(
            completedPriority.Reader, normal.Reader, () => { }, CancellationToken.None).AsTask();
        normal.Writer.TryComplete();
        Assert.Null(await normalRead);

        var priority = Channel.CreateUnbounded<string>();
        var completedNormal = Channel.CreateUnbounded<string>();
        completedNormal.Writer.TryComplete();
        Task<string?> priorityRead = ImageOcrSearchSession.WaitForNextPathAsync(
            priority.Reader, completedNormal.Reader, () => { }, CancellationToken.None).AsTask();
        priority.Writer.TryComplete();
        Assert.Null(await priorityRead);
    }

    [Fact]
    public async Task WaitForNextPathAsync_CancellationInterruptsOpenChannels()
    {
        var priority = Channel.CreateUnbounded<string>();
        var normal = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource();
        Task<string?> read = ImageOcrSearchSession.WaitForNextPathAsync(
            priority.Reader, normal.Reader, () => { }, cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task Session_CachesRecognizedTextForReuse()
    {
        string img = WriteImage("f.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "needle cached now" });
        var sink = Channel.CreateUnbounded<SearchResult>();

        var session = CreateSession(engine, sink, () => { }, _ => { });
        await DrainSinkAsync(session, sink, new[] { img });

        // The session should have written the recognized text into the shared cache.
        Assert.True(_cache.TryGet(img, "fake", out string cached));
        Assert.Equal("needle cached now", cached);
    }

    [Fact]
    public async Task Session_StartIsIdempotent()
    {
        string img = WriteImage("idem.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "needle here" });
        var sink = Channel.CreateUnbounded<SearchResult>();

        var session = CreateSession(engine, sink, () => { }, _ => { });
        session.Start();                  // first start spins up the worker pool
        var results = await DrainSinkAsync(session, sink, new[] { img }); // DrainSink's Start() is a no-op

        Assert.Single(results);
    }

    [Fact]
    public async Task Session_RecognizeFailure_ProducesNoResults()
    {
        // Ready engine, but no recognized text for the image -> RecognizeAsync returns failure.
        string img = WriteImage("recog-fail.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;

        var session = CreateSession(engine, sink, () => Interlocked.Increment(ref processed), _ => { });
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
        Assert.Equal(1, processed);
        Assert.Equal(1, engine.RecognizeCalls);
        Assert.False(_cache.TryGet(img, "fake", out _)); // failures are not cached
    }

    [Fact]
    public async Task Session_RecognizeThrows_IsSwallowedAndFileCounted()
    {
        string img = WriteImage("throws.png");
        var engine = new ThrowingOcrEngine(new InvalidOperationException("boom"));
        var sink = Channel.CreateUnbounded<SearchResult>();
        int processed = 0;

        var session = CreateSession(engine, sink, () => Interlocked.Increment(ref processed), _ => { });
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task Session_RecognizeThrowsOperationCanceled_StopsWorkerGracefully()
    {
        string img = WriteImage("oce.png");
        var engine = new ThrowingOcrEngine(new OperationCanceledException());
        var sink = Channel.CreateUnbounded<SearchResult>();

        var session = CreateSession(engine, sink, () => { }, _ => { });
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
    }

    [Fact]
    public void Session_ExposesEngineId()
    {
        var engine = new FakeOcrEngine(new Dictionary<string, string>());
        var sink = Channel.CreateUnbounded<SearchResult>();

        var session = CreateSession(engine, sink, () => { }, _ => { });

        Assert.Equal("fake", session.EngineId);
    }

    [Fact]
    public void Session_DrainBeforeStartIsAlreadyCompleted()
    {
        var engine = new FakeOcrEngine(new Dictionary<string, string>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        var session = CreateSession(engine, sink, () => { }, _ => { });

        Assert.True(session.DrainAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Session_EmptyRecognizedTextProducesNoResults()
    {
        string img = WriteImage("empty-text.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = string.Empty });
        var sink = Channel.CreateUnbounded<SearchResult>();

        var session = CreateSession(engine, sink, () => { }, _ => { });
        var results = await DrainSinkAsync(session, sink, new[] { img });

        Assert.Empty(results);
        Assert.Equal(1, engine.RecognizeCalls);
    }

    [Fact]
    public async Task Session_PreCancelledToken_StopsWorkerViaOuterCatch()
    {
        string img = WriteImage("cancel.png");
        var engine = new FakeOcrEngine(new Dictionary<string, string> { [img] = "needle" });
        var sink = Channel.CreateUnbounded<SearchResult>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var session = new ImageOcrSearchSession(
            engine, _cache, regex: null, literal: "needle",
            comparison: StringComparison.OrdinalIgnoreCase,
            contextLines: 0, maxMatchesPerFile: 0,
            sink: sink.Writer, onFileProcessed: () => { }, onFileMatched: _ => { },
            workerCount: 1, cancellationToken: cts.Token, shouldStop: null);

        session.Start();
        session.TryEnqueue(img);
        session.Complete();

        // The worker's enumeration over the cancelled token throws OperationCanceledException, which
        // the outer catch swallows — DrainAsync must complete without faulting.
        var ex = await Record.ExceptionAsync(() => session.DrainAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Session_CancellationWhileIdleStopsWorkerViaOuterCatch()
    {
        var engine = new FakeOcrEngine(new Dictionary<string, string>());
        var sink = Channel.CreateUnbounded<SearchResult>();
        using var cts = new CancellationTokenSource();
        var session = new ImageOcrSearchSession(
            engine, _cache, regex: null, literal: "needle",
            comparison: StringComparison.OrdinalIgnoreCase,
            contextLines: 0, maxMatchesPerFile: 0,
            sink: sink.Writer, onFileProcessed: () => { }, onFileMatched: _ => { },
            workerCount: 1, cancellationToken: cts.Token, shouldStop: null);

        session.Start();
        cts.Cancel();

        Assert.Null(await Record.ExceptionAsync(() => session.DrainAsync()));
    }
}
