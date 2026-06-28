namespace Yagu.Services.Ocr;

/// <summary>
/// Helpers for deciding whether a file is an image that the OCR pipeline can process.
/// The default extension set mirrors <c>AppSettings.DefaultImageOcrExtensions</c>.
/// </summary>
public static class ImageOcrSupport
{
    /// <summary>Default OCR-able image extensions (lowercase, no leading dot).</summary>
    public static readonly IReadOnlySet<string> DefaultImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "webp",
        };

    /// <summary>
    /// True when <paramref name="path"/> has an extension in <paramref name="extensions"/>
    /// (or the default image set when none is supplied).
    /// </summary>
    public static bool IsImageCandidate(string path, IReadOnlySet<string>? extensions = null)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        ext = ext.TrimStart('.');
        return (extensions ?? DefaultImageExtensions).Contains(ext);
    }
}
