using Yagu.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Yagu.Helpers;

/// <summary>
/// Include/exclude matcher tailored to absolute Windows paths.
///
/// Supported glob/path patterns (comma- or semicolon-separated input):
///   ts                       -> file extension match (.ts)
///   *.ts, *.tsx              -> file extension match (.ts, .tsx)
///   node_modules             -> matches any path that contains a "node_modules" path segment
///   src/**/*.cs              -> any *.cs anywhere under a "src" segment
///   **/foo/*.txt             -> any *.txt directly under a "foo" segment
///   C:/Windows/System32      -> that directory and every descendant (slash style and case ignored)
/// Regex mode treats each input item as one regex matched against the normalized full path.
/// See <see cref="SplitPatternList"/> for how a list is separated into individual patterns.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<Pattern> _includes;
    private readonly List<Pattern> _excludes;

    public GlobMatcher(
        IEnumerable<string> includes,
        IEnumerable<string> excludes,
        FilterPatternMode includeMode = FilterPatternMode.GlobPath,
        FilterPatternMode excludeMode = FilterPatternMode.GlobPath)
    {
        _includes = Compile(includes, includeMode);
        _excludes = Compile(excludes, excludeMode);
    }

    public bool Matches(string fullPath)
    {
        var norm = fullPath.Replace('\\', '/');
        foreach (var p in _excludes)
            if (p.IsMatch(norm)) return false;

        if (_includes.Count == 0) return true;
        foreach (var p in _includes)
            if (p.IsMatch(norm)) return true;
        return false;
    }

    private static List<Pattern> Compile(IEnumerable<string> raw, FilterPatternMode mode)
    {
        var list = new List<Pattern>();
        foreach (var item in raw ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            if (mode == FilterPatternMode.Regex)
            {
                list.Add(Pattern.BuildRegex(item));
                continue;
            }

            foreach (var part in SplitPatternList(item))
            {
                list.Add(Pattern.BuildGlob(part));
            }
        }
        return list;
    }

    /// <summary>
    /// Splits a persisted filter list into individual patterns. Entries are separated by ';' or ',', but a
    /// literal absolute path may legally contain commas ("C:\Program Files\Acme, Inc"), so inside an entry
    /// that starts with a drive or UNC root a comma only separates entries when the text after it starts
    /// another pattern — another rooted path, or a '*'/'?' wildcard. Use ';' to separate anything else.
    /// </summary>
    public static IEnumerable<string> SplitPatternList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsRootedPathStart(entry))
            {
                foreach (var part in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return part;
                continue;
            }

            int start = 0;
            for (int i = 0; i < entry.Length; i++)
            {
                if (entry[i] != ',' || !StartsNewPattern(entry.AsSpan(i + 1).TrimStart()))
                    continue;
                var item = entry.AsSpan(start, i - start).Trim();
                if (item.Length > 0)
                    yield return item.ToString();
                start = i + 1;
            }

            var tail = entry.AsSpan(start).Trim();
            if (tail.Length > 0)
                yield return tail.ToString();
        }
    }

    private static bool StartsNewPattern(ReadOnlySpan<char> text)
        => text.Length > 0 && (text[0] is '*' or '?' || IsRootedPathStart(text));

    /// <summary>True when the text begins with a drive-letter root (C:\ or C:/) or a UNC root.</summary>
    internal static bool IsRootedPathStart(ReadOnlySpan<char> path)
    {
        bool driveRooted = path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/';
        bool uncRooted = path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal);
        return driveRooted || uncRooted;
    }

    private sealed class Pattern
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        public enum Kind { Extension, Segment, RootedPathPrefix, Regex, Invalid }
        public Kind PatternKind;
        public string Value = string.Empty;
        public Regex? Regex;

        public bool IsMatch(string normalizedPath) => PatternKind switch
        {
            Kind.Extension => normalizedPath.EndsWith(Value, StringComparison.OrdinalIgnoreCase),
            Kind.Segment => IsSegmentMatch(normalizedPath),
            Kind.RootedPathPrefix => IsRootedPathPrefixMatch(normalizedPath),
            Kind.Regex => IsRegexMatch(normalizedPath),
            // Kind.Invalid (and any future value) never matches.
            _ => false,
        };

        private bool IsSegmentMatch(string normalizedPath)
        {
            if (normalizedPath.Contains('/' + Value + '/', StringComparison.OrdinalIgnoreCase)) return true;
            if (normalizedPath.EndsWith('/' + Value, StringComparison.OrdinalIgnoreCase)) return true;
            if (normalizedPath.StartsWith(Value + '/', StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(normalizedPath, Value, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsRootedPathPrefixMatch(string normalizedPath)
        {
            if (string.Equals(normalizedPath, Value, StringComparison.OrdinalIgnoreCase))
                return true;
            return normalizedPath.StartsWith(Value + '/', StringComparison.OrdinalIgnoreCase);
        }

        private bool IsRegexMatch(string normalizedPath)
        {
            try
            {
                return Regex!.IsMatch(normalizedPath);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        public static Pattern BuildGlob(string raw)
        {
            var p = raw.Trim();

            if (!p.Contains('*') && !p.Contains('?') && IsRootedPathStart(p))
            {
                return new Pattern
                {
                    PatternKind = Kind.RootedPathPrefix,
                    Value = p.Replace('\\', '/').TrimEnd('/'),
                };
            }

            // bare token: short alpha-numeric -> file extension (e.g. "ts", "cs", "json")
            //             otherwise              -> path segment      (e.g. "node_modules", "bin", "obj")
            if (!p.Contains('*') && !p.Contains('?') && !p.Contains('/') && !p.Contains('\\') && !p.Contains('.'))
            {
                if (p.Length is > 0 and <= 5 && p.All(char.IsLetterOrDigit))
                    return new Pattern { PatternKind = Kind.Extension, Value = "." + p };
                return new Pattern { PatternKind = Kind.Segment, Value = p };
            }

            // "*.ext"
            if (p.StartsWith("*.", StringComparison.Ordinal) && !p[2..].Contains('*') && !p[2..].Contains('/') && !p[2..].Contains('?'))
                return new Pattern { PatternKind = Kind.Extension, Value = p[1..] };

            // path-free folder name -> segment match (e.g. "node_modules", "bin")
            if (!p.Contains('*') && !p.Contains('?') && !p.Contains('/') && !p.Contains('\\'))
                return new Pattern { PatternKind = Kind.Segment, Value = p };

            // anything else: convert glob to regex
            return new Pattern { PatternKind = Kind.Regex, Regex = GlobToRegex(p) };
        }

        public static Pattern BuildRegex(string raw)
        {
            // Callers (Compile) only pass items that already passed
            // string.IsNullOrWhiteSpace, so a trimmed pattern is always non-empty.
            var pattern = raw.Trim();

            try
            {
                return new Pattern
                {
                    PatternKind = Kind.Regex,
                    Regex = new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled,
                        RegexTimeout),
                };
            }
            catch (ArgumentException)
            {
                return new Pattern { PatternKind = Kind.Invalid };
            }
        }

        private static Regex GlobToRegex(string glob)
        {
            var g = glob.Replace('\\', '/');
            var sb = new StringBuilder();
            sb.Append("(^|/)");
            int i = 0;
            while (i < g.Length)
            {
                var c = g[i];
                if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
                {
                    sb.Append(".*");
                    i += 2;
                    if (i < g.Length && g[i] == '/') i++;
                }
                else if (c == '*') { sb.Append("[^/]*"); i++; }
                else if (c == '?') { sb.Append("[^/]"); i++; }
                else if ("+().|^$[]{}\\".Contains(c)) { sb.Append('\\').Append(c); i++; }
                else { sb.Append(c); i++; }
            }
            sb.Append('$');
            return new Regex(
                sb.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                RegexTimeout);
        }
    }
}
