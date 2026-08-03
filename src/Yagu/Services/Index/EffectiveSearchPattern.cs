using System.Text.RegularExpressions;
using Yagu.Models;

namespace Yagu.Services.Index;

/// <summary>
/// The resolved effective search pattern (plan §4). It reproduces the literal-term parsing,
/// whole-word wrapping, multi-term alternation, and multiline-literal escaping that
/// <see cref="SearchService"/> performs before it runs a search, so the trigram planner and the
/// live verifier reason about the <em>same</em> query. The ordering of the transformations here
/// is deliberately identical to <see cref="SearchService"/> (source-pinned).
/// </summary>
public sealed class EffectiveSearchPattern
{
    /// <summary>The effective pattern text: a regex when <see cref="IsRegex"/> is true, otherwise a raw literal.</summary>
    public string Pattern { get; }

    /// <summary>True when <see cref="Pattern"/> is a regular expression (as opposed to a plain literal substring).</summary>
    public bool IsRegex { get; }

    /// <summary>Case-sensitive match. A case-insensitive query is eligible for the index only when its
    /// pattern is pure ASCII (the planner folds each trigram to its ASCII case variants); a non-ASCII
    /// case-insensitive pattern stays ineligible (plan §4).</summary>
    public bool CaseSensitive { get; }

    /// <summary>Cross-line (multiline) matching is enabled.</summary>
    public bool Multiline { get; }

    /// <summary>Under multiline, <c>.</c> also matches newlines.</summary>
    public bool DotAll { get; }

    /// <summary>True when the resolved pattern is empty (whitespace-only query); the planner treats this as ineligible.</summary>
    public bool IsEmpty { get; }

    public EffectiveSearchPattern(string pattern, bool isRegex, bool caseSensitive, bool multiline, bool dotAll)
    {
        Pattern = pattern ?? string.Empty;
        IsRegex = isRegex;
        CaseSensitive = caseSensitive;
        Multiline = multiline;
        DotAll = dotAll;
        IsEmpty = Pattern.Length == 0;
    }

    /// <summary>
    /// Resolves the effective pattern for a set of search options, mirroring the reduction order in
    /// <see cref="SearchService"/>: explicit regex passes through; a whole-word (exact) literal becomes
    /// a <c>\b</c>-wrapped regex; multiple literal terms become an alternation regex; a single literal
    /// under multiline is promoted to an escaped regex; otherwise it stays a plain literal.
    /// </summary>
    public static EffectiveSearchPattern Resolve(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Coalesce the query once up front; every reduction path below reuses it so there are no
        // repeated (and partly unreachable) null-coalescing branches.
        string query = options.Query ?? string.Empty;
        bool caseSensitive = options.CaseSensitive;
        bool multiline = options.Multiline;
        bool dotAll = options.MultilineDotAll;

        if (options.UseRegex)
            return new EffectiveSearchPattern(query, isRegex: true, caseSensitive, multiline, dotAll);

        var terms = SearchQueryParser.ParseLiteralTerms(query, options.ExactMatch);
        if (terms.Count == 0)
            return new EffectiveSearchPattern(string.Empty, isRegex: false, caseSensitive, multiline, dotAll);

        if (options.ExactMatch)
        {
            string wordPattern = SearchQueryParser.BuildLiteralRegexPattern(query, exactMatch: true)!;
            return new EffectiveSearchPattern(wordPattern, isRegex: true, caseSensitive, multiline, dotAll);
        }

        if (terms.Count > 1)
        {
            string alternation = SearchQueryParser.BuildLiteralAlternation(terms);
            return new EffectiveSearchPattern(alternation, isRegex: true, caseSensitive, multiline, dotAll);
        }

        if (multiline)
        {
            string literalPattern = Regex.Escape(terms[0]);
            return new EffectiveSearchPattern(literalPattern, isRegex: true, caseSensitive, multiline, dotAll);
        }

        return new EffectiveSearchPattern(terms[0], isRegex: false, caseSensitive, multiline, dotAll);
    }
}
