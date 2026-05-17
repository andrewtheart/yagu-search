using System.Text;
using System.Text.RegularExpressions;

namespace Yagu.Services;

internal static class SearchQueryParser
{
    public static IReadOnlyList<string> ParseLiteralTerms(string query, bool exactMatch = true)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        // Exact match: the entire query is one term (trimmed).
        if (exactMatch)
            return [query.Trim()];

        // Multi-term: split on whitespace.
        var terms = new List<string>();
        var current = new StringBuilder(query.Length);

        foreach (char ch in query)
        {
            if (char.IsWhiteSpace(ch))
            {
                AddCurrentTerm(terms, current);
                continue;
            }

            current.Append(ch);
        }

        AddCurrentTerm(terms, current);
        return terms;
    }

    public static string BuildLiteralAlternation(IReadOnlyList<string> terms)
    {
        return string.Join("|", terms
            .Where(term => !string.IsNullOrEmpty(term))
            .OrderByDescending(term => term.Length)
            .Select(Regex.Escape));
    }

    public static string? BuildLiteralRegexPattern(string query, bool exactMatch = true)
    {
        var terms = ParseLiteralTerms(query, exactMatch);
        return terms.Count == 0 ? null : BuildLiteralAlternation(terms);
    }

    private static void AddCurrentTerm(List<string> terms, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        terms.Add(current.ToString());
        current.Clear();
    }
}