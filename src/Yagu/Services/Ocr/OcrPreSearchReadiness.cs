namespace Yagu.Services.Ocr;

/// <summary>
/// What the pre-search OCR gate should do for one search. Pure and UI-free so the decision is unit
/// testable; the window layer only renders the outcome.
/// </summary>
public enum OcrPreSearchAction
{
    /// <summary>Image-text search is off, or every component is already present — start the search immediately.</summary>
    Proceed,

    /// <summary>Components are missing and the user has not approved a download yet — ask first.</summary>
    AskForConsent,

    /// <summary>Components are missing and the download was already approved — download before searching.</summary>
    Download,
}

/// <summary>
/// Decides, before a search runs, whether image-text (OCR) search needs its one-time component
/// download. The engine normally discovers this lazily on a background thread partway into a search,
/// which leaves the user waiting on a search that silently cannot do OCR yet. Resolving it up front
/// lets the window prompt, download with visible progress, and only then start the search.
/// </summary>
public static class OcrPreSearchReadiness
{
    /// <summary>
    /// Decides what must happen before a search that has image-text search enabled.
    /// </summary>
    /// <param name="searchImageText">Whether this search actually uses OCR.</param>
    /// <param name="requirement">What the effective engine still needs, from <see cref="IOcrEngine.DescribeAssetRequirement"/>.</param>
    /// <param name="consentGranted">Whether the user already approved OCR downloads.</param>
    public static OcrPreSearchAction Decide(bool searchImageText, OcrAssetRequirement? requirement, bool consentGranted)
    {
        if (!searchImageText || requirement is null || !requirement.DownloadNeeded)
            return OcrPreSearchAction.Proceed;

        return consentGranted ? OcrPreSearchAction.Download : OcrPreSearchAction.AskForConsent;
    }

    /// <summary>Progress line for the download dialog, e.g. "Downloading… 42s elapsed".</summary>
    public static string DescribeElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return elapsed.TotalMinutes >= 1
            ? $"Downloading\u2026 {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s elapsed"
            : $"Downloading\u2026 {elapsed.Seconds}s elapsed";
    }

    /// <summary>Sentence describing what is being fetched, e.g. "PaddleSharp: OCR engine runtime (~349 MB)".</summary>
    public static string DescribeComponents(OcrAssetRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return requirement.MissingComponents.Count > 0
            ? $"{requirement.EngineDisplayName}: {string.Join(", ", requirement.MissingComponents)}"
            : $"{requirement.EngineDisplayName}: about {requirement.ApproxMb} MB";
    }
}
