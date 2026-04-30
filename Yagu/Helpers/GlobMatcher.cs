using System.Diagnostics.CodeAnalysis;

namespace Yagu.Helpers;

/// <summary>
/// Glob-style include/exclude matcher tailored to absolute Windows paths.
///
/// Supported patterns (comma- or semicolon-separated input):
///   ts                       -> file extension match (.ts)
///   *.ts, *.tsx              -> file extension match (.ts, .tsx)
///   node_modules             -> matches any path that contains a "node_modules" path segment
///   src/**/*.cs              -> any *.cs anywhere under a "src" segment
///   **/foo/*.txt             -> any *.txt directly under a "foo" segment
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<Pattern> _includes;
    private readonly List<Pattern> _excludes;

    public GlobMatcher(IEnumerable<string> includes, IEnumerable<string> excludes)
    {
        _includes = Compile(includes);
        _excludes = Compile(excludes);
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

    private static List<Pattern> Compile(IEnumerable<string> raw)
    {
        var list = new List<Pattern>();
        foreach (var item in raw ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            foreach (var part in item.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(Pattern.Build(part));
            }
        }
        return list;
    }

    private sealed class Pattern
    {
        public enum Kind { Extension, Segment, Regex }
        public Kind PatternKind;
        public string Value = string.Empty;
        public System.Text.RegularExpressions.Regex? Regex;

        public bool IsMatch(string normalizedPath)
        {
            switch (PatternKind)
            {
                case Kind.Extension:
                    return normalizedPath.EndsWith(Value, StringComparison.OrdinalIgnoreCase);
                case Kind.Segment:
                {
                    if (normalizedPath.Contains('/' + Value + '/', StringComparison.OrdinalIgnoreCase)) return true;
                    if (normalizedPath.EndsWith('/' + Value, StringComparison.OrdinalIgnoreCase)) return true;
                    if (normalizedPath.StartsWith(Value + '/', StringComparison.OrdinalIgnoreCase)) return true;
                    return string.Equals(normalizedPath, Value, StringComparison.OrdinalIgnoreCase);
                }
                case Kind.Regex:
                    return Regex!.IsMatch(normalizedPath);
                default:
                    return false;
            }
        }

        public static Pattern Build(string raw)
        {
            var p = raw.Trim();

            // bare token: short alpha-numeric -> file extension (e.g. "ts", "cs", "json")
            //             otherwise              -> path segment      (e.g. "node_modules", "bin", "obj")
            if (!p.Contains('*') && !p.Contains('?') && !p.Contains('/') && !p.Contains('\\') && !p.Contains('.'))
            {
                if (p.Length is > 0 and <= 5 && p.All(char.IsLetterOrDigit))
                    return new Pattern { PatternKind = Kind.Extension, Value = "." + p };
                return new Pattern { PatternKind = Kind.Segment, Value = p };
            }

            // "*.ext"
            if (p.StartsWith("*.") && !p[2..].Contains('*') && !p[2..].Contains('/') && !p[2..].Contains('?'))
                return new Pattern { PatternKind = Kind.Extension, Value = p[1..] };

            // path-free folder name -> segment match (e.g. "node_modules", "bin")
            if (!p.Contains('*') && !p.Contains('?') && !p.Contains('/') && !p.Contains('\\'))
                return new Pattern { PatternKind = Kind.Segment, Value = p };

            // anything else: convert glob to regex
            return new Pattern { PatternKind = Kind.Regex, Regex = GlobToRegex(p) };
        }

        private static System.Text.RegularExpressions.Regex GlobToRegex(string glob)
        {
            var g = glob.Replace('\\', '/');
            var sb = new System.Text.StringBuilder();
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
                else if ("+().|^$[]{}\\".IndexOf(c) >= 0) { sb.Append('\\').Append(c); i++; }
                else { sb.Append(c); i++; }
            }
            sb.Append('$');
            return new System.Text.RegularExpressions.Regex(sb.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        }
    }
}
