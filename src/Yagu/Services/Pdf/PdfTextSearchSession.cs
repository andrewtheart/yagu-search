using System.Threading.Channels;
using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services.Ocr;

namespace Yagu.Services.Pdf;

/// <summary>
/// A background PDF text-extraction pipeline that runs decoupled from the main (Rust) file scan.
/// PDF paths are enqueued cheaply by the discovery/scan loop (never blocking it); a small pool of
/// worker tasks then runs <c>pdftotext</c> on each file off the scan threads, searches the extracted
/// text for the query, and writes any matches into the shared result channel so they appear in the
/// results panel as they are found.
///
/// This is the PDF analogue of <see cref="Yagu.Services.Ocr.ImageOcrSearchSession"/>. Extracted text
/// is cached per file (see <see cref="OcrTextCache"/>, keyed by the <c>pdftotext</c> engine id so it
/// never collides with OCR text) so repeated searches and the preview drawer can reuse it without
/// re-running the extractor.
/// </summary>
public sealed class PdfTextSearchSession
{
    private readonly PdfTextExtractor _extractor;
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
    private volatile bool _started;

    public PdfTextSearchSession(
        PdfTextExtractor extractor,
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
        _extractor = extractor;
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

        // Unbounded so the scan loop's enqueue never blocks on extraction back-pressure; entries are
        // just path strings. The worker pool drains them off the scan threads.
        _paths = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public string EngineId => _extractor.Id;

    /// <summary>Starts the worker pool. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < _workerCount; i++)
            _workers.Add(Task.Run(WorkerLoopAsync, CancellationToken.None));
    }

    /// <summary>Enqueues a PDF for extraction. Non-blocking; returns false once completed.</summary>
    public bool TryEnqueue(string pdfPath) => _paths.Writer.TryWrite(pdfPath);

    /// <summary>Signals that no more PDFs will be enqueued.</summary>
    public void Complete() => _paths.Writer.TryComplete();

    /// <summary>Awaits completion of all extraction workers (call after <see cref="Complete"/>).</summary>
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
                    await ProcessPdfAsync(path).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Verbose("PdfTextSearch", $"PDF extraction failed for {path}: {ex.Message}");
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

    private async Task ProcessPdfAsync(string path)
    {
        if (!_cache.TryGet(path, _extractor.Id, out string text))
        {
            PdfTextResult extracted = await _extractor.ExtractAsync(path, _cancellationToken).ConfigureAwait(false);
            if (!extracted.Success)
            {
                LogService.Instance.Verbose("PdfTextSearch", $"pdftotext could not read {path}: {extracted.Error}");
                return;
            }

            text = extracted.Text;
            _cache.Set(path, _extractor.Id, text);
        }

        if (string.IsNullOrEmpty(text)) return;

        var matches = OcrTextMatcher.Match(path, text, _regex, _literal, _comparison, _contextLines, _maxMatchesPerFile);
        if (matches.Count == 0) return;

        foreach (var result in matches)
            await _sink.WriteAsync(result, _cancellationToken).ConfigureAwait(false);

        _onFileMatched(matches.Count);
    }
}
