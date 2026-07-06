using System.Text.RegularExpressions;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Single home for constructing the query <see cref="Regex"/> used by search, replace, and
/// preview highlighting so those paths can never disagree on flags. Adds
/// <see cref="RegexOptions.Multiline"/> when cross-line matching is on,
/// <see cref="RegexOptions.Singleline"/> (dot-matches-newline) when dot-all is on, and a mandatory
/// <see cref="MultilineMatchTimeout"/> DoS guard on the multiline path only — line-mode regexes keep
/// <see cref="Regex.InfiniteMatchTimeout"/> (their existing behavior).
/// </summary>
public static class SearchRegexFactory
{
    /// <summary>
    /// Match timeout applied ONLY to multiline (cross-line) regexes. .NET regex can catastrophically
    /// backtrack; whole-file matching is the DoS-prone case (the Rust linear engine can't hang, .NET
    /// can). This bound — not <see cref="RegexOptions.Compiled"/> — is the real protection; under
    /// Native AOT <c>Compiled</c> degrades to the interpreter but the timeout still fires.
    /// </summary>
    public static readonly TimeSpan MultilineMatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves the <see cref="RegexOptions"/> flags for a search. Base is
    /// <c>Compiled | CultureInvariant</c>, plus <c>IgnoreCase</c> when not case-sensitive, plus
    /// <c>Multiline</c> when cross-line matching is on, plus <c>Singleline</c> when dot-all is on
    /// (only meaningful under multiline).
    /// </summary>
    public static RegexOptions ResolveOptions(bool caseSensitive, bool multiline, bool multilineDotAll)
    {
        var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!caseSensitive) opts |= RegexOptions.IgnoreCase;
        if (multiline)
        {
            opts |= RegexOptions.Multiline;
            if (multilineDotAll)
                opts |= RegexOptions.Singleline;
        }
        return opts;
    }

    /// <summary>Resolves the <see cref="RegexOptions"/> flags from a <see cref="SearchOptions"/>.</summary>
    public static RegexOptions ResolveOptions(SearchOptions options)
        => ResolveOptions(options.CaseSensitive, options.Multiline, options.MultilineDotAll);

    /// <summary>
    /// Builds a <see cref="Regex"/> for <paramref name="pattern"/> with the resolved flags. The
    /// <see cref="MultilineMatchTimeout"/> is applied only when <paramref name="multiline"/> is true;
    /// line-mode regexes get <see cref="Regex.InfiniteMatchTimeout"/> (current behavior — the factory
    /// must not retrofit a timeout onto them).
    /// </summary>
    public static Regex Build(string pattern, bool caseSensitive, bool multiline, bool multilineDotAll)
    {
        var opts = ResolveOptions(caseSensitive, multiline, multilineDotAll);
        var timeout = multiline ? MultilineMatchTimeout : Regex.InfiniteMatchTimeout;
        return new Regex(pattern, opts, timeout);
    }

    /// <summary>Builds a <see cref="Regex"/> for <paramref name="pattern"/> from a <see cref="SearchOptions"/>.</summary>
    public static Regex Build(string pattern, SearchOptions options)
        => Build(pattern, options.CaseSensitive, options.Multiline, options.MultilineDotAll);
}
