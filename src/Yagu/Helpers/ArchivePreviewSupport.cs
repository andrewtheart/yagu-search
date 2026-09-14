using Yagu.Services;

namespace Yagu.Helpers;

/// <summary>
/// Decides whether a preview target is an archive container whose raw bytes must never be rendered as
/// preview text. Mirrors <see cref="Yagu.Services.Pdf.PdfTextSupport"/> and
/// <see cref="Yagu.Services.Ocr.ImageOcrSupport"/> for the archive case.
/// </summary>
public static class ArchivePreviewSupport
{
    /// <summary>
    /// Archive/container extensions (lowercase, no leading dot), parsed from
    /// <see cref="AppSettings.DefaultArchiveExtensions"/>. Deliberately the built-in list rather than the
    /// user's configured set: a .zip is unreadable as text whether or not "Search inside archives" is
    /// enabled or that extension happens to be ticked.
    /// </summary>
    public static readonly IReadOnlySet<string> ArchiveExtensions = ParseDefaultArchiveExtensions();

    private static HashSet<string> ParseDefaultArchiveExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in AppSettings.DefaultArchiveExtensions.Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.TrimStart('.');
            if (ext.Length > 0) set.Add(ext);
        }
        return set;
    }

    /// <summary>
    /// True when <paramref name="path"/> points at an archive container itself (e.g. <c>C:\a\b.zip</c>, or
    /// the nested <c>outer.zip?/inner.zip</c>) rather than at a file stored inside one
    /// (<c>outer.zip?/readme.txt</c>, which previews normally).
    /// </summary>
    public static bool IsArchiveContainerPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // For nested archive paths, the trailing segment is the file actually being previewed.
        var segment = path.AsSpan();
        int sep = segment.LastIndexOf(ZipArchiveSearcher.ArchiveSeparator);
        if (sep >= 0) segment = segment[(sep + 1)..].TrimStart('/');

        var ext = Path.GetExtension(segment);
        return ext.Length > 1 && ArchiveExtensions.Contains(ext[1..].ToString());
    }

    /// <summary>
    /// True when the preview target is an archive that matched on its file name only
    /// (<paramref name="lineNumber"/> of 0 marks a file-name match). Such a result has no readable text to
    /// show — previewing it would dump the container's raw bytes as mojibake. Archives that also matched on
    /// content keep their normal preview, as do files stored inside an archive.
    /// </summary>
    public static bool IsFileNameOnlyArchivePreview(string? path, int lineNumber)
        => lineNumber <= 0 && IsArchiveContainerPath(path);

    /// <summary>Message rendered in the preview pane in place of an archive's unreadable raw bytes.</summary>
    public static string BuildFileNameOnlyArchivePreviewMessage(string? path)
    {
        string name = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
        string subject = name.Length > 0 ? $"\u201C{name}\u201D" : "This archive";
        return $"{subject} matched on its file name only. Archives have no readable text to preview \u2014 "
             + "turn on \u201CSearch inside archives\u201D to match and preview the files stored inside it.";
    }
}
