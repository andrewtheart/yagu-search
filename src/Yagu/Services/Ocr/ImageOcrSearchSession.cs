using System.Threading.Channels;
using Yagu.Models;
using System.Text.RegularExpressions;

namespace Yagu.Services.Ocr;

/// <summary>
/// A background OCR pipeline that runs decoupled from the main (Rust) file scan. Image paths are
/// enqueued cheaply by the discovery/scan loop (never blocking it); a small pool of worker tasks
/// then OCRs each image off the scan threads, searches the recognized text for the query, and
/// writes any matches into the shared result channel so they appear in the results panel as they
/// are found.
///
/// Recognized text is cached per image (see <see cref="OcrTextCache"/>) so repeated searches and
/// the preview drawer can reuse it without re-running the recognizer.
/// </summary>
public sealed class ImageOcrSearchSession
{
    private readonly IOcrEngine _engine;
    private readonly OcrTextCache _cache;
    private readonly Regex? _regex;
    private readonly string? _literal;
    private readonly StringComparison _comparison;
    private readonly int _contextLines;
    private readonly int _maxMatchesPerFile;
    private readonly ChannelWriter<SearchResult> _sink;
    private readonly Action _onFileProcessed;
    private readonly Action<int> _onFileMatched;
    private readonly Func<bool>? _shouldStop;
    private readonly int _workerCount;
    private readonly CancellationToken _cancellationToken;

    private readonly Channel<string> _paths;
    private readonly List<Task> _workers = new();
    private readonly object _ensureLock = new();
    private Task<OcrResult>? _ensureTask;
    private volatile bool _started;

    public ImageOcrSearchSession(
        IOcrEngine engine,
        OcrTextCache cache,
        Regex? regex,
        string? literal,
        StringComparison comparison,
        int contextLines,
        int maxMatchesPerFile,
        ChannelWriter<SearchResult> sink,
        Action onFileProcessed,
        Action<int> onFileMatched,
        int workerCount,
        CancellationToken cancellationToken,
        Func<bool>? shouldStop = null)
    {
        _engine = engine;
        _cache = cache;
        _regex = regex;
        _literal = literal;
        _comparison = comparison;
        _contextLines = contextLines;
        _maxMatchesPerFile = maxMatchesPerFile;
        _sink = sink;
        _onFileProcessed = onFileProcessed;
        _onFileMatched = onFileMatched;
        _workerCount = Math.Max(1, workerCount);
        _cancellationToken = cancellationToken;
        _shouldStop = shouldStop;

        // Unbounded so the scan loop's enqueue never blocks on OCR back-pressure; entries are just
        // path strings. The worker pool drains them off the scan threads.
        _paths = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public string EngineId => _engine.Id;

    /// <summary>Starts the worker pool. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < _workerCount; i++)
            _workers.Add(Task.Run(WorkerLoopAsync, CancellationToken.None));
    }

    /// <summary>Enqueues an image for OCR. Non-blocking; returns false once completed.</summary>
    public bool TryEnqueue(string imagePath) => _paths.Writer.TryWrite(imagePath);

    /// <summary>Signals that no more images will be enqueued.</summary>
    public void Complete() => _paths.Writer.TryComplete();

    /// <summary>Awaits completion of all OCR workers (call after <see cref="Complete"/>).</summary>
    public Task DrainAsync() => _workers.Count == 0 ? Task.CompletedTask : Task.WhenAll(_workers);

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (string path in _paths.Reader.ReadAllAsync(_cancellationToken).ConfigureAwait(false))
            {
                if (_cancellationToken.IsCancellationRequested) break;
                if (_shouldStop?.Invoke() == true)
                {
                    _onFileProcessed();
                    continue;
                }

                try
                {
                    await ProcessImageAsync(path).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Verbose("OcrSearch", $"OCR failed for {path}: {ex.Message}");
                }
                finally
                {
                    _onFileProcessed();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on cancellation.
        }
    }

    private async Task ProcessImageAsync(string path)
    {
        if (!_cache.TryGet(path, _engine.Id, out string text))
        {
            OcrResult ready = await EnsureEngineReadyAsync().ConfigureAwait(false);
            if (!ready.Success)
                return; // Engine unavailable — skip silently; reason already logged once.

            OcrResult recognized = await _engine.RecognizeAsync(path, _cancellationToken).ConfigureAwait(false);
            if (!recognized.Success)
            {
                LogService.Instance.Verbose("OcrSearch", $"OCR engine '{_engine.Id}' could not read {path}: {recognized.Error}");
                return;
            }

            text = recognized.Text;
            _cache.Set(path, _engine.Id, text);
        }

        if (string.IsNullOrEmpty(text)) return;

        var matches = OcrTextMatcher.Match(path, text, _regex, _literal, _comparison, _contextLines, _maxMatchesPerFile);
        if (matches.Count == 0) return;

        foreach (var result in matches)
            await _sink.WriteAsync(result, _cancellationToken).ConfigureAwait(false);

        _onFileMatched(matches.Count);
    }

    private Task<OcrResult> EnsureEngineReadyAsync()
    {
        lock (_ensureLock)
        {
            return _ensureTask ??= _engine.EnsureReadyAsync(_cancellationToken);
        }
    }
}
