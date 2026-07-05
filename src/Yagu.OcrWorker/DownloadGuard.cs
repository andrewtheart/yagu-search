namespace Yagu.OcrWorker;

/// <summary>
/// Hard enforcement point for "no external OCR download without user consent". The host
/// (<c>WorkerOcrEngine</c>) warns the user and, only after consent, starts this worker with
/// <c>YAGU_OCR_ALLOW_DOWNLOAD=1</c>. Every place that would fetch native binaries or models over the
/// network calls <see cref="EnsureAllowed"/> first, so even if the host's presence heuristic is
/// wrong the worker fails fast instead of silently downloading.
/// <para>
/// Downloads are blocked only on an explicit <c>"0"</c>. An absent variable leaves standalone/dev
/// runs of the worker behaving exactly as before (downloads permitted).
/// </para>
/// </summary>
internal static class DownloadGuard
{
    private const string AllowEnvVar = "YAGU_OCR_ALLOW_DOWNLOAD";

    internal static bool DownloadsAllowed =>
        !string.Equals(Environment.GetEnvironmentVariable(AllowEnvVar), "0", StringComparison.Ordinal);

    /// <summary>
    /// Throws <see cref="OcrDownloadNotAllowedException"/> when a download of <paramref name="what"/>
    /// is required but the host did not authorize it.
    /// </summary>
    internal static void EnsureAllowed(string what)
    {
        if (!DownloadsAllowed)
        {
            throw new OcrDownloadNotAllowedException(
                $"OCR {what} must be downloaded, but the download was not authorized. " +
                "The Yagu app must obtain user consent before the worker can fetch OCR assets.");
        }
    }
}

/// <summary>Raised when the worker would need to download an OCR asset without authorization.</summary>
internal sealed class OcrDownloadNotAllowedException : Exception
{
    public OcrDownloadNotAllowedException(string message)
        : base(message)
    {
    }
}
