using System.Text.RegularExpressions;
using Yagu.Models;

namespace Yagu.Services.Ai;

internal static class SemanticSimpleQuery
{
    private static readonly Regex Prefix = new(
        @"\A(?:(?:find|show|list)\s+)?(?:(?:all|every|any)\s+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Content = new(
        """\A(?<scope>.+?)\s+(?:that\s+)?(?:contains?|containing|mentions?|mentioning|with|has)\s+(?:(?:the\s+word|a)\s+)?(?<term>"[^"\r\n]+"|'[^'\r\n]+'|[\w./+#@-]+)(?:\s+comment)?(?:\s+in\s+(?:it|them))?\s*\z""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool TryBuildPlan(string? query, out SemanticSearchPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(query))
            return false;
        string text = query.Trim();
        if (!text.Any(char.IsWhiteSpace))
            return false;
        text = Prefix.Replace(text, string.Empty, 1);
        var content = Content.Match(text);
        string scope = content.Success ? content.Groups["scope"].Value : text;
        if (!SemanticFileTypes.TryResolve(scope, out var globs))
            return false;
        if (!content.Success && globs.Count == 0)
            return false;

        string? term = content.Success ? content.Groups["term"].Value : null;
        if (term is not null && term[0] is '\'' or '"')
            term = term[1..^1];
        if (term is not null && string.IsNullOrWhiteSpace(term))
            return false;
        plan = new SemanticSearchPlan
        {
            Pattern = term,
            SearchMode = term is null ? "filenames" : "content",
            UseRegex = term is null ? null : false,
            ExactMatch = term?.Any(char.IsWhiteSpace) == true,
            IncludeGlobs = globs.Count > 0 ? globs : null,
        };
        return true;
    }
}