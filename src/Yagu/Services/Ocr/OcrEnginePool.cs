using System.Threading.Channels;

namespace Yagu.Services.Ocr;

/// <summary>A bounded pool of independent OCR engines. For the production worker-backed engines,
/// every member owns a separate <c>Yagu.OcrWorker.exe</c> process, so concurrent recognition is real
/// process-level parallelism rather than several host tasks queueing into one sequential worker.
/// The primary engine initializes first; only after its assets are ready are secondary workers started,
/// preventing first-use model/runtime download races.</summary>
public sealed class OcrEnginePool : IOcrEngine, IAsyncDisposable, IDisposable
{
    private readonly IReadOnlyList<IOcrEngine> _engines;
    private readonly Channel<IOcrEngine> _available;
    private readonly object _initializationLock = new();
    private Task<OcrResult>? _initializationTask;
    private Task? _secondaryInitializationTask;
    private int _disposed;

    public OcrEnginePool(IReadOnlyList<IOcrEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        if (engines.Count == 0)
            throw new ArgumentException("At least one OCR engine is required.", nameof(engines));

        if (engines[0] is null)
            throw new ArgumentException("OCR engine entries cannot be null.", nameof(engines));
        string id = engines[0].Id;
        if (engines.Any(engine => engine is null || !string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Every OCR engine in a pool must have the same non-null engine id.", nameof(engines));

        _engines = engines.ToArray();
        _available = Channel.CreateBounded<IOcrEngine>(new BoundedChannelOptions(_engines.Count)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string Id => _engines[0].Id;
    public string DisplayName => _engines[0].DisplayName;
    public int WorkerCount => _engines.Count;

    // Every lane is the same engine against the same asset directories, so the primary answers for all.
    public OcrAssetRequirement DescribeAssetRequirement() => _engines[0].DescribeAssetRequirement();

    public async Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        Task<OcrResult> initialization;
        lock (_initializationLock)
            initialization = _initializationTask ??= InitializePrimaryAsync();

        try
        {
            return await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Fail("OCR initialization canceled.");
        }
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        OcrResult ready = await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!ready.Success)
            return ready;

        IOcrEngine engine;
        try
        {
            engine = await _available.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Fail("OCR canceled.");
        }
        catch (ChannelClosedException)
        {
            return OcrResult.Fail("OCR worker pool is closed.");
        }

        try
        {
            return await engine.RecognizeAsync(imagePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
                _available.Writer.TryWrite(engine);
        }
    }

    private async Task<OcrResult> InitializePrimaryAsync()
    {
        OcrResult primary = await _engines[0].EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);
        if (!primary.Success)
            return primary;
        if (Volatile.Read(ref _disposed) != 0)
            return OcrResult.Fail("OCR worker pool is closed.");

        _available.Writer.TryWrite(_engines[0]);
        if (_engines.Count > 1)
            _secondaryInitializationTask = InitializeSecondaryEnginesAsync();
        return primary;
    }

    private async Task InitializeSecondaryEnginesAsync()
    {
        Task<(IOcrEngine Engine, OcrResult Result)>[] initializations = _engines
            .Skip(1)
            .Select(static async engine =>
                (engine, await engine.EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false)))
            .ToArray();

        foreach ((IOcrEngine engine, OcrResult result) in await Task.WhenAll(initializations).ConfigureAwait(false))
        {
            if (result.Success && Volatile.Read(ref _disposed) == 0)
                _available.Writer.TryWrite(engine);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _available.Writer.TryComplete();
        try { _initializationTask?.GetAwaiter().GetResult(); }
        catch { }
        try { _secondaryInitializationTask?.GetAwaiter().GetResult(); }
        catch { }
        foreach (IOcrEngine engine in _engines)
        {
            try
            {
                if (engine is IDisposable disposable)
                    disposable.Dispose();
            }
            catch
            {
                // Best-effort process cleanup; dispose all remaining workers.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _available.Writer.TryComplete();
        Task<OcrResult>? initialization = _initializationTask;
        if (initialization is not null)
        {
            try { await initialization.ConfigureAwait(false); }
            catch { }
        }
        Task? secondary = _secondaryInitializationTask;
        if (secondary is not null)
        {
            try { await secondary.ConfigureAwait(false); }
            catch { }
        }

        foreach (IOcrEngine engine in _engines)
        {
            try
            {
                if (engine is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (engine is IDisposable disposable)
                    disposable.Dispose();
            }
            catch
            {
                // Best-effort process cleanup; dispose all remaining workers.
            }
        }
    }
}
