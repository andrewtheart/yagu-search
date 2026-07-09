namespace Yagu.Helpers;

/// <summary>
/// Classifies a search pattern as "matches everything" so the UI can warn before a runaway
/// content search. A bare match-everything regex (<c>.</c>, <c>.*</c>, <c>[\s\S]</c>, …) run over
/// file <em>contents</em> emits one match per character (or per line) of every file — millions of
/// noise matches on minified/one-line files — and is almost always a mistake (the user meant a
/// literal <c>.</c>, or meant to search file names). The engine is already crash-safe (per-line cap
/// + absolute ceiling); this classifier drives the *advisory* nudge only.
/// </summary>
public static class SearchPatternClassifier
{
    /// <summary>
    /// True when <paramref name="pattern"/>, interpreted as a REGEX, matches (nearly) every character
    /// or line — i.e. the whole pattern is a "match everything" token rather than a real search term.
    /// Only the <em>entire</em> trimmed pattern is considered: a pattern that merely <em>contains</em>
    /// <c>.</c> (e.g. <c>foo.bar</c>, <c>v\d.\d</c>) is a real search and returns false. The caller must
    /// gate on regex mode being ON — a LITERAL <c>.</c> (regex off) is the period character, not a
    /// match-all, and this method is not meaningful for it.
    /// </summary>
    public static bool IsMatchEverythingRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string s = pattern.Trim();
        return s is "." or ".*" or ".+" or ".?"
            or "^.*$" or "^.+$" or "^.*" or "^.+" or ".*$" or ".+$"
            or "(?s)." or "(?s).*" or "(?s).+"
            or "[\\s\\S]" or "[\\S\\s]" or "[\\w\\W]" or "[\\W\\w]";
    }
}
