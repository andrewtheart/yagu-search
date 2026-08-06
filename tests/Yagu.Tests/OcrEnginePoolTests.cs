using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class OcrEnginePoolTests
{
    private sealed class FakeEngine : IOcrEngine, IAsyncDisposable, IDisposable
    {
        private readonly Func<CancellationToken, Task<OcrResult>>? _ready;
        private readonly Func<string, CancellationToken, Task<OcrResult>>? _recognize;
        private readonly Action? _dispose;
        private readonly Func<ValueTask>? _disposeAsync;

        public FakeEngine(
            string id = "fake",
            string displayName = "Fake",
            Func<CancellationToken, Task<OcrResult>>? ready = null,
            Func<string, CancellationToken, Task<OcrResult>>? recognize = null,
            Action? dispose = null,
            Func<ValueTask>? disposeAsync = null)
        {
            Id = id;
            DisplayName = displayName;
            _ready = ready;
            _recognize = recognize;
            _dispose = dispose;
            _disposeAsync = disposeAsync;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public int EnsureCalls;
        public int RecognizeCalls;
        public int DisposeCalls;

        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref EnsureCalls);
            return _ready?.Invoke(cancellationToken) ?? Task.FromResult(OcrResult.Ok(string.Empty));
        }

        public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RecognizeCalls);
            if (_recognize is not null)
                return await _recognize(imagePath, cancellationToken).ConfigureAwait(false);
            return OcrResult.Ok(imagePath);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCalls);
            _dispose?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            if (_disposeAsync is not null)
                return _disposeAsync();
            _dispose?.Invoke();
            return default;
        }
    }

    private sealed class DisposableOnlyEngine : IOcrEngine, IDisposable
    {
        public int DisposeCalls;
        public string Id => "fake";
        public string DisplayName => "DisposableOnly";

        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
            => Task.FromResult(OcrResult.Ok(string.Empty));

        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult(OcrResult.Ok(imagePath));

        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    [Fact]
    public void Pool_ExposesPrimaryIdentityAndWorkerCount()
    {
        using var pool = new OcrEnginePool(
            new IOcrEngine[]
            {
                new FakeEngine(id: "paddle", displayName: "Paddle OCR"),
                new FakeEngine(id: "paddle", displayName: "Other lane"),
            });

        Assert.Equal("paddle", pool.Id);
        Assert.Equal("Paddle OCR", pool.DisplayName);
        Assert.Equal(2, pool.WorkerCount);
    }

    [Fact]
    public async Task Pool_UsesIndependentEnginesConcurrently()
    {
        int active = 0;
        int maxActive = 0;
        var twoActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<OcrResult> BlockRecognition(string _, CancellationToken __)
        {
            int current = Interlocked.Increment(ref active);
            int observed;
            while (current > (observed = Volatile.Read(ref maxActive)))
                Interlocked.CompareExchange(ref maxActive, current, observed);
            if (current >= 2)
                twoActive.TrySetResult();
            try { await release.Task; }
            finally { Interlocked.Decrement(ref active); }
            return OcrResult.Ok("ok");
        }

        var first = new FakeEngine(recognize: BlockRecognition);
        var second = new FakeEngine(recognize: BlockRecognition);
        await using var pool = new OcrEnginePool(new IOcrEngine[] { first, second });

        Assert.True((await pool.EnsureReadyAsync(CancellationToken.None)).Success);
        Task<OcrResult> one = pool.RecognizeAsync("one", CancellationToken.None);
        Task<OcrResult> two = pool.RecognizeAsync("two", CancellationToken.None);

        await twoActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        OcrResult[] results = await Task.WhenAll(one, two);

        Assert.Equal(2, maxActive);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(2, first.RecognizeCalls + second.RecognizeCalls);
    }

    [Fact]
    public async Task Pool_InitializesPrimaryBeforeStartingSecondaryWorkers()
    {
        var primaryReady = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeEngine(ready: _ => primaryReady.Task);
        var secondary = new FakeEngine();
        await using var pool = new OcrEnginePool(new IOcrEngine[] { primary, secondary });

        Task<OcrResult> ensure = pool.EnsureReadyAsync(CancellationToken.None);
        Assert.Equal(1, primary.EnsureCalls);
        Assert.Equal(0, secondary.EnsureCalls);

        primaryReady.TrySetResult(OcrResult.Ok(string.Empty));
        Assert.True((await ensure).Success);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref secondary.EnsureCalls) == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EnsureReady_CancellationReturnsFailureResult()
    {
        var gate = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine(ready: _ => gate.Task),
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OcrResult result = await pool.EnsureReadyAsync(cts.Token);

        Assert.False(result.Success);
        Assert.Equal("OCR initialization canceled.", result.Error);
        gate.TrySetResult(OcrResult.Ok(string.Empty));
    }

    [Fact]
    public async Task RecognizeAsync_ReturnsInitializationFailureWithoutDispatchingRecognition()
    {
        var engine = new FakeEngine(ready: _ => Task.FromResult(OcrResult.Fail("init failed")));
        await using var pool = new OcrEnginePool(new IOcrEngine[] { engine });

        OcrResult result = await pool.RecognizeAsync("x.png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("init failed", result.Error);
        Assert.Equal(0, engine.RecognizeCalls);
    }

    [Fact]
    public async Task RecognizeAsync_CancellationWhileWaitingForEngineReturnsCanceledFailure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine(recognize: async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return OcrResult.Ok("held");
        });
        await using var pool = new OcrEnginePool(new IOcrEngine[] { engine });

        Task<OcrResult> held = pool.RecognizeAsync("held.png", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        Task<OcrResult> canceled = pool.RecognizeAsync("queued.png", cts.Token);
        cts.Cancel();

        OcrResult canceledResult = await canceled;
        Assert.False(canceledResult.Success);
        Assert.Equal("OCR canceled.", canceledResult.Error);

        release.TrySetResult();
        OcrResult heldResult = await held;
        Assert.True(heldResult.Success);
    }

    [Fact]
    public async Task RecognizeAsync_ClosedPoolWithoutAvailableWorkersReturnsClosedFailure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine(recognize: async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return OcrResult.Ok("held");
        });
        var pool = new OcrEnginePool(new IOcrEngine[] { engine });

        Task<OcrResult> held = pool.RecognizeAsync("held.png", CancellationToken.None);
        pool.Dispose();

        OcrResult closed = await pool.RecognizeAsync("next.png", CancellationToken.None);
        Assert.False(closed.Success);
        Assert.Equal("OCR worker pool is closed.", closed.Error);

        release.TrySetResult();
        await held;
    }

    [Fact]
    public async Task EnsureReady_WhenDisposedDuringPrimaryInitializationReturnsClosedFailure()
    {
        var primaryReady = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine(ready: _ => primaryReady.Task),
        });

        Task<OcrResult> ensureTask = pool.EnsureReadyAsync(CancellationToken.None);
        ValueTask disposeTask = pool.DisposeAsync();
        primaryReady.TrySetResult(OcrResult.Ok("ready"));
        await disposeTask;

        OcrResult result = await ensureTask;
        Assert.False(result.Success);
        Assert.Equal("OCR worker pool is closed.", result.Error);
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryEngine()
    {
        var first = new FakeEngine();
        var second = new FakeEngine();
        var pool = new OcrEnginePool(new IOcrEngine[] { first, second });

        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public void Dispose_IdempotentSecondCallReturnsImmediately()
    {
        var engine = new FakeEngine();
        var pool = new OcrEnginePool(new IOcrEngine[] { engine });

        pool.Dispose();
        pool.Dispose();

        Assert.Equal(1, engine.DisposeCalls);
    }

    [Fact]
    public void Dispose_SwallowsDisposalFailuresAndDisposesAllEngines()
    {
        var first = new FakeEngine(dispose: () => throw new InvalidOperationException("boom"));
        var second = new FakeEngine();
        var pool = new OcrEnginePool(new IOcrEngine[] { first, second });

        pool.Dispose();

        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task Dispose_CatchesFaultedPrimaryAndSecondaryInitializationTasks()
    {
        var primaryReady = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondary = new FakeEngine(ready: _ => Task.FromException<OcrResult>(new InvalidOperationException("secondary init failed")));
        var pool = new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine(ready: _ => primaryReady.Task),
            secondary,
        });

        Task<OcrResult> ensure = pool.EnsureReadyAsync(CancellationToken.None);
        primaryReady.TrySetResult(OcrResult.Ok("ready"));
        Assert.True((await ensure).Success);
        Assert.True(SpinWait.SpinUntil(() => secondary.EnsureCalls == 1, TimeSpan.FromSeconds(5)));

        pool.Dispose();
    }

    [Fact]
    public async Task SecondaryInitialization_NonSuccessResult_IsIgnoredWithoutThrowing()
    {
        var secondary = new FakeEngine(ready: _ => Task.FromResult(OcrResult.Fail("secondary not ready")));
        await using var pool = new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine(ready: _ => Task.FromResult(OcrResult.Ok("ready"))),
            secondary,
        });

        OcrResult ensure = await pool.EnsureReadyAsync(CancellationToken.None);

        Assert.True(ensure.Success);
        Assert.True(SpinWait.SpinUntil(() => secondary.EnsureCalls == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dispose_CatchesFaultedPrimaryInitializationTask()
    {
        var pool = new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine(ready: _ => Task.FromException<OcrResult>(new InvalidOperationException("primary init failed"))),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.EnsureReadyAsync(CancellationToken.None));

        pool.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_UsesSynchronousDisposeWhenAsyncNotAvailable()
    {
        var asyncEngine = new FakeEngine();
        var syncEngine = new DisposableOnlyEngine();
        var pool = new OcrEnginePool(new IOcrEngine[] { asyncEngine, syncEngine });

        await pool.DisposeAsync();

        Assert.Equal(1, asyncEngine.DisposeCalls);
        Assert.Equal(1, syncEngine.DisposeCalls);
    }

    [Fact]
    public async Task DisposeAsync_SwallowsInitializationAndDisposalFailures()
    {
        var primaryReady = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondary = new FakeEngine(ready: _ => Task.FromException<OcrResult>(new InvalidOperationException("secondary init failed")));
        var failingAsync = new FakeEngine(
            ready: _ => primaryReady.Task,
            disposeAsync: () => ValueTask.FromException(new InvalidOperationException("dispose failed")));
        var pool = new OcrEnginePool(new IOcrEngine[] { failingAsync, secondary });

        Task<OcrResult> ensure = pool.EnsureReadyAsync(CancellationToken.None);
        primaryReady.TrySetResult(OcrResult.Ok("ready"));
        Assert.True((await ensure).Success);
        Assert.True(SpinWait.SpinUntil(() => secondary.EnsureCalls == 1, TimeSpan.FromSeconds(5)));

        await pool.DisposeAsync();
        await pool.DisposeAsync();
        Assert.Equal(1, failingAsync.DisposeCalls);
    }

    [Fact]
    public async Task DisposeAsync_CatchesFaultedPrimaryInitializationTask()
    {
        var pool = new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine(ready: _ => Task.FromException<OcrResult>(new InvalidOperationException("primary init failed"))),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.EnsureReadyAsync(CancellationToken.None));

        await pool.DisposeAsync();
    }

    [Fact]
    public void Constructor_RejectsEmptyOrMixedEnginePools()
    {
        Assert.Throws<ArgumentException>(() => new OcrEnginePool(Array.Empty<IOcrEngine>()));
        Assert.Throws<ArgumentException>(() => new OcrEnginePool(new IOcrEngine[] { null! }));
        Assert.Throws<ArgumentException>(() => new OcrEnginePool(new IOcrEngine[]
        {
            new FakeEngine("paddle"),
            new FakeEngine("tesseract"),
        }));
    }
}
