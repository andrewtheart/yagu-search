namespace Yagu.Services.Ocr;

/// <summary>
/// Describes the one-time external download an OCR engine needs before it can run, used to warn the
/// user (and to decide whether a warning is needed at all). Produced by each
/// <see cref="WorkerOcrEngine"/> from <see cref="OcrAssetPaths"/>.
/// </summary>
public sealed record OcrAssetRequirement
{
    /// <summary>Human-readable engine name (e.g. "PaddleSharp", "Tesseract").</summary>
    public required string EngineDisplayName { get; init; }

    /// <summary>True when one or more components are missing and must be downloaded.</summary>
    public required bool DownloadNeeded { get; init; }

    /// <summary>Approximate total bytes that would be downloaded (0 when nothing is missing).</summary>
    public required long ApproxBytes { get; init; }

    /// <summary>Short labels for each missing component, e.g. "OCR engine runtime (~349 MB)".</summary>
    public required IReadOnlyList<string> MissingComponents { get; init; }

    /// <summary>Approximate total download size in megabytes, rounded up.</summary>
    public int ApproxMb => OcrAssetPaths.ToApproxMb(ApproxBytes);
}

/// <summary>
/// Single decision point that gates any external OCR asset download behind explicit user consent.
/// <para>
/// The UI layer registers <see cref="PromptAsync"/> at startup to show a warning dialog; headless
/// hosts (CLI, tests) leave it null. Consent, once granted, is remembered for the rest of the
/// process via <see cref="ConsentGranted"/> (seeded from the persisted
/// <c>OcrDownloadConsented</c> setting), so the user is asked at most once. This type is pure
/// managed and has no UI dependency, so it links cleanly into the test project.
/// </para>
/// </summary>
public static class OcrDownloadGate
{
    /// <summary>
    /// True when the user has already approved OCR asset downloads (this process). The app seeds
    /// this from the persisted <c>OcrDownloadConsented</c> setting at startup; the prompt sets it
    /// when the user approves.
    /// </summary>
    public static volatile bool ConsentGranted;

    /// <summary>
    /// UI hook that warns the user before any external download and returns <c>true</c> to proceed.
    /// Set by the app at startup (marshals to the UI thread and shows a modal). When null (headless
    /// CLI / tests), a download that lacks prior consent is refused rather than started silently.
    /// </summary>
    public static Func<OcrAssetRequirement, Task<bool>>? PromptAsync;

    // Serializes prompting so that concurrent OCR inits (multiple search roots/files) ask at most
    // once: later callers wait and then observe the first caller's granted consent.
    private static readonly SemaphoreSlim PromptLock = new(1, 1);

    /// <summary>
    /// Decides whether the engine may proceed to download <paramref name="requirement"/>. Returns
    /// true immediately when nothing is missing or consent was already granted; otherwise prompts
    /// (when a UI hook is registered) and remembers an approval. Never initiates the download
    /// itself — it only authorizes the worker to do so.
    /// </summary>
    public static async Task<bool> EnsureAllowedAsync(OcrAssetRequirement requirement)
    {
        if (!requirement.DownloadNeeded)
        {
            return true;
        }

        if (ConsentGranted)
        {
            return true;
        }

        Func<OcrAssetRequirement, Task<bool>>? prompt = PromptAsync;
        if (prompt is null)
        {
            return false;
        }

        await PromptLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Another concurrent caller may have obtained consent while we waited.
            if (ConsentGranted)
            {
                return true;
            }

            bool approved = await prompt(requirement).ConfigureAwait(false);
            if (approved)
            {
                ConsentGranted = true;
            }

            return approved;
        }
        finally
        {
            PromptLock.Release();
        }
    }
}
