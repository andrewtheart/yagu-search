using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Recognises a file-name query that is really a file glob ("*.ps1") so it can be applied as a file
/// filter instead of being matched against the name as text.
/// </summary>
/// <remarks>
/// <para>
/// Windows file names cannot contain <c>*</c> or <c>?</c>, so in file-name mode such a query can never
/// match anything literally: it is a guaranteed-empty search with the regex toggle off, and an
/// "invalid regex" error with it on. Neither outcome is what the user asked for.
/// </para>
/// <para>
/// The natural-language path already turns "ps1 files" into an include glob plus a match-all name
/// enumeration, so routing here is what makes the typed and the spoken query agree.
/// </para>
/// </remarks>
internal static class FileNameGlobQuery
{
    /// <summary>Enumerates every file name, once the glob itself has moved to the include filter.</summary>
    public const string MatchAllPattern = ".";

    /// <summary>
    /// True when <paramref name="query"/> should be applied as an include filter rather than matched as
    /// text; <paramref name="globs"/> then carries the tokens to filter with.
    /// </summary>
    /// <param name="existingIncludeGlobs">
    /// Routing is skipped when an include filter is already set: include patterns combine as OR, so
    /// adding to them would silently widen the search when the user meant to narrow it.
    /// </param>
    public static bool TryRoute(
        SearchMode searchMode,
        bool useRegex,
        string? query,
        IReadOnlyList<string>? existingIncludeGlobs,
        out IReadOnlyList<string> globs)
    {
        globs = [];

        // With the regex toggle on, * and ? are the user's own syntax and must be left alone.
        if (searchMode != SearchMode.FileNames || useRegex)
            return false;

        if (existingIncludeGlobs is { Count: > 0 })
            return false;

        return TrySplitGlobTokens(query, out globs);
    }

    /// <summary>Splits a query into glob tokens, or returns false when any token is ordinary text.</summary>
    public static bool TrySplitGlobTokens(string? query, out IReadOnlyList<string> globs)
    {
        globs = [];
        if (string.IsNullOrWhiteSpace(query))
            return false;

        string[] tokens = query.Split(
            [' ', '\t', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
            return false;

        // Every token has to be a glob. A mixed query like "report *.pdf" plausibly means "named report
        // AND a pdf", which an OR-combined include list cannot express, so it is left alone.
        foreach (string token in tokens)
        {
            if (!token.Contains('*') && !token.Contains('?'))
                return false;
        }

        globs = tokens;
        return true;
    }

    /// <summary>Status text explaining that the query was applied as a file filter.</summary>
    public static string DescribeRouting(IReadOnlyList<string> globs) =>
        $"Searching file names matching {string.Join(", ", globs)}.";

    /// <summary>Characters that only ever appear in a regular expression, never in a plain file glob.</summary>
    private static readonly char[] RegexOnlySyntax = ['\\', '[', ']', '(', ')', '^', '$', '+', '{', '}', '|'];

    /// <summary>
    /// True when the query reads as a file glob rather than a regular expression.
    /// </summary>
    /// <remarks>
    /// Deliberately stricter than <see cref="TrySplitGlobTokens"/>, which only has to prove that a
    /// literal name match is impossible. This one drives a suggestion shown to someone who asked for a
    /// regex, so a pattern carrying regex-only syntax must not be second-guessed just because it also
    /// contains a star — otherwise an ordinary regex typo gets told it is a glob.
    /// </remarks>
    public static bool LooksLikeGlob(string? query)
    {
        if (!TrySplitGlobTokens(query, out IReadOnlyList<string> globs))
            return false;

        foreach (string token in globs)
        {
            if (token.IndexOfAny(RegexOnlySyntax) >= 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reports an invalid regex, and says what it probably was when the pattern is really a file glob.
    /// </summary>
    /// <remarks>
    /// A bare .NET parser message ("quantifier * following nothing") describes the failure without
    /// naming the thing the user was actually trying to do, which is the whole of the confusion.
    /// </remarks>
    public static string DescribeInvalidRegex(string? query, string regexMessage)
    {
        string reported = $"Invalid regex: {regexMessage}";
        return LooksLikeGlob(query)
            ? $"{reported} \u2014 \u201c{query!.Trim()}\u201d looks like a file glob rather than a regular expression."
            : reported;
    }
}
