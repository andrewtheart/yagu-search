using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services.Ocr;

namespace Yagu.Services.Ai;

internal static class SemanticWholeFileExclusion
{
    private static readonly Regex Query = new(
        """^\s*(?:(?:find|show|list)\s+)?(?<scope>.+?)\s+(?:that\s+)?(?:mention|contain|mentioning|containing|with)\s+(?<required>"[^"\r\n]+"|'[^'\r\n]+'|[\w./+#@-]+)\s+but\s+(?:not|without)\s+(?<excluded>"[^"\r\n]+"|'[^'\r\n]+'|[\w./+#@-]+)\s*$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool TryBuildPlan(string? query, out SemanticSearchPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(query))
            return false;
        var match = Query.Match(query);
        if (!match.Success || !SemanticFileTypes.TryResolve(match.Groups["scope"].Value, out var globs))
            return false;

        // This shortcut matches a regex against the file's own bytes, so it cannot see text that only
        // exists after PDF extraction, OCR, or archive expansion — hence the three toggles below are
        // forced off. When EVERY resolved type is one of those container/binary kinds ("pdfs that mention
        // X but not Y"), taking the shortcut would therefore return nothing at all. Decline instead, so
        // the normal plan path runs and deterministically turns the matching toggle on.
        if (globs.Count > 0 && globs.All(IsExtractionOnlyGlob))
            return false;

        string required = Unquote(match.Groups["required"].Value);
        string excluded = Unquote(match.Groups["excluded"].Value);
        plan = new SemanticSearchPlan
        {
            Pattern = @"\A(?![\s\S]*" + Regex.Escape(excluded) + @")(?=[\s\S]*" + Regex.Escape(required) + @")[\s\S]+\z",
            UseRegex = true,
            ExactMatch = false,
            Multiline = true,
            SearchMode = "content",
            IncludeGlobs = globs.Count > 0 ? globs : null,
            SearchInsideArchives = false,
            SearchImageText = false,
            SearchPdfText = false,
        };
        return true;
    }

    private static string Unquote(string value) => value[0] is '\'' or '"' ? value[1..^1] : value;

    /// <summary>
    /// Whether a glob targets a type whose text is only reachable through PDF extraction, OCR, or archive
    /// expansion — the three things this shortcut cannot do.
    /// </summary>
    private static bool IsExtractionOnlyGlob(string glob)
    {
        int dot = glob.LastIndexOf('.');
        if (dot < 0)
            return false;

        string extension = glob[(dot + 1)..].Trim();
        if (extension.Length == 0)
            return false;

        return extension.Equals("pdf", StringComparison.OrdinalIgnoreCase)
            || ImageOcrSupport.DefaultImageExtensions.Contains(extension)
            || ArchiveExtensions.Value.Contains(extension);
    }

    private static readonly Lazy<HashSet<string>> ArchiveExtensions = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in AppSettings.DefaultArchiveExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(entry.TrimStart('*', '.'));
        return set;
    });
}