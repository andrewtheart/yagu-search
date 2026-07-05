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
        if (terms.Count == 0)
            return null;

        // Exact match means "whole words only": wrap the (single) trimmed query
        // term in word boundaries so e.g. "async" matches the word "async" but
        // not "asynchronously".
        if (exactMatch)
            return BuildWholeWordPattern(terms[0]);

        return BuildLiteralAlternation(terms);
    }

    /// <summary>
    /// Builds a regex that matches <paramref name="term"/> as a whole word.
    /// A <c>\b</c> word-boundary assertion is added on a side only when the
    /// adjacent character of the term is itself a word character, so a query
    /// made of punctuation (e.g. <c>"+="</c>) still matches as a literal
    /// substring. This mirrors "match whole word" in common editors and
    /// grep's <c>--word-regexp</c> for word-edged terms.
    /// </summary>
    public static string BuildWholeWordPattern(string term)
    {
        string escaped = Regex.Escape(term);
        if (term.Length == 0)
            return escaped;

        string prefix = IsWordCharacter(term[0]) ? @"\b" : string.Empty;
        string suffix = IsWordCharacter(term[^1]) ? @"\b" : string.Empty;
        return prefix + escaped + suffix;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void AddCurrentTerm(List<string> terms, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        terms.Add(current.ToString());
        current.Clear();
    }
}