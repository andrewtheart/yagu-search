using System.Diagnostics;
using System.Text;

namespace Yagu.Services.Pdf;

/// <summary>Outcome of a PDF text-extraction operation.</summary>
/// <param name="Success">True when text was extracted (possibly empty).</param>
/// <param name="Text">Extracted plain text (empty on failure).</param>
/// <param name="Error">Human-readable failure reason, or <c>null</c> on success.</param>
public readonly record struct PdfTextResult(bool Success, string Text, string? Error)
{
    public static PdfTextResult Ok(string text) => new(true, text ?? string.Empty, null);

    public static PdfTextResult Fail(string error) => new(false, string.Empty, error);
}

/// <summary>
/// Extracts the embedded text layer of a PDF by shelling out to the bundled Xpdf
/// <c>pdftotext.exe</c> (the same proven technique dnGrep uses). This mirrors the image-OCR pipeline
/// (<see cref="Yagu.Services.Ocr.WorkerOcrEngine"/>) conceptually — a native document format is
/// turned into searchable plain text out-of-process — but is far simpler: <c>pdftotext.exe</c> is a
/// self-contained command-line tool, so this class is <b>pure managed</b> (just <see cref="Process"/>
/// + stdio) and therefore Native-AOT safe. It degrades gracefully (logs once, returns failures) when
/// the tool is missing, so PDF-text search simply yields no matches instead of throwing.
/// </summary>
public sealed class PdfTextExtractor
{
    /// <summary>Environment variable that overrides the <c>pdftotext.exe</c> path (tests/dev).</summary>
    public const string ToolPathEnvVar = "YAGU_PDFTOTEXT";

    /// <summary>Stable identifier used as the cache engine key so PDF text never collides with OCR text.</summary>
    public const string EngineId = "pdftotext";

    // Hard per-file wall-clock cap so a malformed/encrypted PDF that makes pdftotext spin can never
    // stall an extraction worker indefinitely. Generous enough for large real documents.
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(60);

    // Process-wide guard so a missing tool is reported exactly once (avoids spamming yagu.log on every
    // PDF / every search when the tool is not installed, e.g. a stripped portable build).
    private static int _missingToolLogged;

    private readonly string? _toolPathOverride;
    private readonly bool _hasToolPathOverride;

    public PdfTextExtractor()
    {
    }

    /// <summary>
    /// Test/diagnostics hook: forces the tool path to a specific value (authoritative — if the file
    /// does not exist, extraction reports the tool as unavailable instead of probing the standard
    /// locations).
    /// </summary>
    internal PdfTextExtractor(string? toolPathOverride)
    {
        _toolPathOverride = toolPathOverride;
        _hasToolPathOverride = true;
    }

    public string Id => EngineId;

    /// <summary>
    /// True when <c>pdftotext.exe</c> can be located. Cheap (a file-exists probe); safe to call once
    /// per search before enqueuing PDFs. Logs a single actionable warning the first time the tool is
    /// missing so a stripped build's "PDFs silently return no matches" is diagnosable in yagu.log.
    /// </summary>
    public bool EnsureAvailable()
    {
        if (ResolveToolPath() is not null) return true;
        LogMissingOnce();
        return false;
    }

    /// <summary>
    /// Extracts the text layer of <paramref name="pdfPath"/> using <c>pdftotext</c>, returning the
    /// recognized plain text on success. Never throws for expected failures (missing tool, malformed
    /// PDF, timeout) — those are surfaced as <see cref="PdfTextResult.Fail"/>.
    /// </summary>
    public async Task<PdfTextResult> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
    {
        string? tool = ResolveToolPath();
        if (tool is null)
        {
            LogMissingOnce();
            return PdfTextResult.Fail("pdftotext.exe not found");
        }

        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // pdftotext emits UTF-8 when asked (-enc UTF-8); decode its stdout as UTF-8.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(tool) ?? AppContext.BaseDirectory,
        };
        // ArgumentList quotes each token safely (no manual escaping / command-injection surface).
        //   -enc UTF-8  emit UTF-8 text            -eol unix  normalize line endings
        //   -q          suppress status/error text  <file>  input      -  write to stdout
        psi.ArgumentList.Add("-enc");
        psi.ArgumentList.Add("UTF-8");
        psi.ArgumentList.Add("-eol");
        psi.ArgumentList.Add("unix");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add(pdfPath);
        psi.ArgumentList.Add("-");

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return PdfTextResult.Fail("pdftotext failed to start");
        }
        catch (Exception ex)
        {
            return PdfTextResult.Fail($"pdftotext failed to start: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExtractionTimeout);

        try
        {
            // Read stdout to completion while the process runs, then await exit — avoids a deadlock
            // where a large text layer fills the stdout pipe before we start draining it.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string text = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false); // drained and discarded (diagnostics only)

            // pdftotext exit codes: 0 = ok; 1 = open error; 2 = output error; 3 = permissions; 99 = other.
            // Treat any run that produced text as usable (best-effort); a clean exit with empty text is
            // a legitimate "no extractable text" (e.g. a scanned/image-only PDF).
            if (process.ExitCode == 0 || text.Length > 0)
                return PdfTextResult.Ok(text);

            return PdfTextResult.Fail($"pdftotext exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timed out (not caller cancellation): kill the stuck process and report failure.
            TryKill(process);
            return PdfTextResult.Fail("pdftotext timed out");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return PdfTextResult.Fail($"pdftotext failed: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort — the process may have already exited.
        }
    }

    /// <summary>
    /// Resolves the <c>pdftotext.exe</c> path. Order: explicit test override → <see cref="ToolPathEnvVar"/>
    /// environment override → the app's own install directory (<c>&lt;app&gt;\pdftotext\pdftotext.exe</c>).
    /// <para>
    /// SECURITY (binary planting): the tool is loaded ONLY from the ACL-protected install directory (or
    /// the explicit env override) — never from a per-user-writable path such as <c>%LOCALAPPDATA%</c>.
    /// A signed app auto-executing an .exe from a predictable user-writable location would let non-admin
    /// malware plant a malicious <c>pdftotext.exe</c> and have it run inside Yagu's process tree on the
    /// next PDF search. This matches the OCR/semantic worker probe policy. (pdftotext is a third-party
    /// tool with a different publisher than Yagu, so — unlike the OCR/semantic workers — a same-publisher
    /// Authenticode check is intentionally not applied; the install-dir-only probe is the control.)
    /// </para>
    /// </summary>
    internal string? ResolveToolPath()
    {
        if (_hasToolPathOverride)
        {
            return !string.IsNullOrEmpty(_toolPathOverride) && File.Exists(_toolPathOverride)
                ? _toolPathOverride
                : null;
        }

        string? overridePath = Environment.GetEnvironmentVariable(ToolPathEnvVar);
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        string besideApp = Path.Combine(AppContext.BaseDirectory, "pdftotext", "pdftotext.exe");
        return File.Exists(besideApp) ? besideApp : null;
    }

    private static void LogMissingOnce()
    {
        if (Interlocked.Exchange(ref _missingToolLogged, 1) != 0)
            return;

        string besideApp = Path.Combine(AppContext.BaseDirectory, "pdftotext", "pdftotext.exe");
        LogService.Instance.Warning(
            "PdfText",
            "PDF-text search is unavailable: pdftotext.exe was not found, so PDF files cannot be " +
            "converted to text and PDF searches will return no matches. Probed: " + ToolPathEnvVar +
            " environment variable and \"" + besideApp + "\".");
    }
}
