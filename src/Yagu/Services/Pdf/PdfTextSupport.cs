namespace Yagu.Services.Pdf;

/// <summary>
/// Helpers for deciding whether a file is a PDF that the text-extraction pipeline can process.
/// Mirrors <see cref="Yagu.Services.Ocr.ImageOcrSupport"/> for the image-OCR pipeline; the default
/// extension set matches <c>AppSettings.DefaultPdfTextExtensions</c>.
/// </summary>
public static class PdfTextSupport
{
    /// <summary>Default PDF extensions (lowercase, no leading dot).</summary>
    public static readonly IReadOnlySet<string> DefaultPdfExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pdf",
        };

    /// <summary>
    /// True when <paramref name="path"/> has an extension in <paramref name="extensions"/>
    /// (or the default PDF set when none is supplied).
    /// </summary>
    public static bool IsPdfCandidate(string path, IReadOnlySet<string>? extensions = null)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        ext = ext.TrimStart('.');
        return (extensions ?? DefaultPdfExtensions).Contains(ext);
    }
}
