using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// Result of planning a trigram query for an <see cref="EffectiveSearchPattern"/> (plan §4).
/// Either an <see cref="Eligible"/> boolean trigram expression that is a required-superset filter,
/// or <see cref="Ineligible"/> with a human-readable reason (the search then uses the unchanged
/// live-scan pipeline). Eligibility affects performance only, never correctness.
/// </summary>
public abstract record TrigramPlan
{
    private TrigramPlan() { }

    /// <summary>The pattern can be accelerated; <see cref="Query"/> prunes non-matching documents.</summary>
    public sealed record Eligible(TrigramExpression Query) : TrigramPlan;

    /// <summary>The pattern cannot be safely accelerated; live-scan instead.</summary>
    public sealed record Ineligible(string Reason) : TrigramPlan;
}

/// <summary>
/// Converts an <see cref="EffectiveSearchPattern"/> into a monotone boolean trigram query, or an
/// <see cref="TrigramPlan.Ineligible"/> verdict (plan §4). The reduction is deliberately
/// conservative: it emits only AND/OR of <em>required</em> substrings — never negation — so the
/// resulting query is a required superset of every true match. Unsupported or ambiguous regex
/// constructs, case-insensitive queries, and patterns with no guaranteed trigram are ineligible and
/// fall back to a full live scan.
/// </summary>
public static class TrigramQueryPlanner
{
    // Bounds that keep the analysis allocation- and time-bounded. Correctness never depends on these
    // (exceeding a bound only yields Ineligible / a broader candidate set).
    private const int ExactSetCountCap = 8;
    private const int ExactStringMaxLen = 64;
    private const int RepeatExpandMax = 16;
    private const int AnalysisNodeBudget = 200_000;

    public const string ReasonEmptyQuery = "empty query";
    public const string ReasonCaseInsensitive = "case-insensitive acceleration requires an ASCII pattern";
    public const string ReasonUnsupportedRegex = "unsupported regex construct";
    public const string ReasonNoRequiredTrigram = "no required trigram";
    public const string ReasonMatchesNothing = "query matches nothing";
    public const string ReasonWorkBudget = "planner work budget exceeded";

    /// <summary>Plans a trigram query for the given effective pattern.</summary>
    public static TrigramPlan Plan(EffectiveSearchPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.IsEmpty)
            return new TrigramPlan.Ineligible(ReasonEmptyQuery);

        // Case-insensitive acceleration (plan §4/§11.6). Supported when the effective pattern is pure
        // ASCII: every required trigram is expanded to its ASCII case variants (see FoldCaseInsensitive).
        // A non-ASCII case-insensitive pattern would need Unicode case folding — multi-byte and
        // window-shifting — which a fixed byte trigram cannot represent, so it stays ineligible.
        TrigramFold fold = TrigramFold.None;
        if (!pattern.CaseSensitive)
        {
            if (!IsAsciiPattern(pattern.Pattern))
                return new TrigramPlan.Ineligible(ReasonCaseInsensitive);

            // A plain literal runs through the byte matchers (native ASCII fast path / .NET
            // OrdinalIgnoreCase), which fold ONLY ASCII a-z<->A-Z. A regex-form pattern (whole-word,
            // multi-term, or multiline literal) runs through the Unicode-case-folding regex engines,
            // where 'k' also matches U+212A and 's' also matches U+017F — multi-byte fold members a
            // fixed trigram cannot capture, so those trigrams are dropped. Either way the folded
            // query remains a required superset of the matcher.
            fold = pattern.IsRegex ? TrigramFold.AsciiDropUnicodeFoldable : TrigramFold.Ascii;
        }

        RegexInfo info;
        if (!pattern.IsRegex)
        {
            info = RegexInfo.LiteralString(pattern.Pattern);
        }
        else
        {
            var parser = new RegexParser(pattern.Pattern);
            Node? node = parser.Parse();
            if (node is null)
                return new TrigramPlan.Ineligible(ReasonUnsupportedRegex);

            var analyzer = new Analyzer();
            info = analyzer.Analyze(node);
            if (analyzer.BudgetExceeded)
                return new TrigramPlan.Ineligible(ReasonWorkBudget);
        }

        RegexInfo.Flush(info);
        TrigramExpression query = FoldCaseInsensitive(info.Match, fold);

        return query.Kind == TrigramExpression.NodeKind.All
            ? new TrigramPlan.Ineligible(ReasonNoRequiredTrigram)
            : new TrigramPlan.Eligible(query);
    }

    // ─────────────────── Case-insensitive trigram folding ───────────────────

    /// <summary>How required trigrams are expanded to cover a case-insensitive query.</summary>
    private enum TrigramFold
    {
        /// <summary>No folding (case-sensitive query).</summary>
        None,
        /// <summary>Expand each ASCII letter byte to both cases. Used for the plain-literal byte
        /// matchers (native ASCII fast path / .NET OrdinalIgnoreCase), which fold ONLY ASCII.</summary>
        Ascii,
        /// <summary>ASCII folding, but drop any trigram whose bytes have a multi-byte Unicode fold
        /// member (k/K/s/S) — the regex engines fold those to U+212A / U+017F.</summary>
        AsciiDropUnicodeFoldable,
    }

    private static bool IsAsciiPattern(string s)
    {
        foreach (char c in s)
            if (c > '\u007F') return false;
        return true;
    }

    /// <summary>
    /// Rewrites every trigram leaf of a required-superset expression so it also covers the
    /// case-insensitive occurrences of the same query. Each substitution only weakens a leaf — a
    /// trigram becomes an OR of its ASCII case variants, or <see cref="TrigramExpression.All"/> when
    /// dropped — so the monotone result stays a required superset (dropping/weakening a factor can only
    /// enlarge the candidate set, never hide a match).
    /// </summary>
    private static TrigramExpression FoldCaseInsensitive(TrigramExpression expr, TrigramFold fold)
    {
        if (fold == TrigramFold.None)
            return expr;

        switch (expr.Kind)
        {
            case TrigramExpression.NodeKind.Trigram:
                return FoldTrigram(expr.Trigram, fold);
            case TrigramExpression.NodeKind.And:
            {
                TrigramExpression acc = TrigramExpression.All;
                foreach (var child in expr.Children)
                    acc = TrigramExpression.And(acc, FoldCaseInsensitive(child, fold));
                return acc;
            }
            case TrigramExpression.NodeKind.Or:
            {
                TrigramExpression acc = TrigramExpression.None;
                foreach (var child in expr.Children)
                    acc = TrigramExpression.Or(acc, FoldCaseInsensitive(child, fold));
                return acc;
            }
            default: // All / None
                return expr;
        }
    }

    private static TrigramExpression FoldTrigram(Trigram trigram, TrigramFold fold)
    {
        byte b0 = trigram.Byte0, b1 = trigram.Byte1, b2 = trigram.Byte2;

        // Under the Unicode-folding matchers a trigram touching k/K/s/S is not guaranteed present (the
        // content may use the multi-byte U+212A / U+017F fold variant), so it is dropped (not required).
        if (fold == TrigramFold.AsciiDropUnicodeFoldable &&
            (HasMultiByteFold(b0) || HasMultiByteFold(b1) || HasMultiByteFold(b2)))
            return TrigramExpression.All;

        var (l0, u0) = CasePair(b0);
        var (l1, u1) = CasePair(b1);
        var (l2, u2) = CasePair(b2);

        TrigramExpression acc = TrigramExpression.None;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                for (int k = 0; k < 2; k++)
                    acc = TrigramExpression.Or(acc, TrigramExpression.OfTrigram(new Trigram(
                        i == 0 ? l0 : u0,
                        j == 0 ? l1 : u1,
                        k == 0 ? l2 : u2)));
        return acc;
    }

    /// <summary>
    /// The ASCII letters whose Unicode case-fold class (as used by the .NET CultureInvariant regex and
    /// the Rust regex crate) also contains a NON-ASCII, multi-byte code point, so a fixed byte trigram
    /// cannot represent that position under Unicode case folding: 'k'/'K' ↔ U+212A KELVIN SIGN (both
    /// engines) and 's'/'S' ↔ U+017F LATIN SMALL LETTER LONG S (the Rust regex crate). Derived from
    /// Unicode CaseFolding.txt and pinned by
    /// <c>TrigramQueryPlannerTests.CaseFold_MultiByteFoldableLetters_CoverDotNetRegexFolding</c>.
    /// </summary>
    private static bool HasMultiByteFold(byte b)
        => b is (byte)'k' or (byte)'K' or (byte)'s' or (byte)'S';

    private static (byte Lower, byte Upper) CasePair(byte b)
    {
        if (b is >= (byte)'A' and <= (byte)'Z') return ((byte)(b + 32), b);
        if (b is >= (byte)'a' and <= (byte)'z') return (b, (byte)(b - 32));
        return (b, b);
    }

    // ─────────────────────────── Regex AST ───────────────────────────

    private abstract record Node
    {
        public abstract RegexInfo Analyze(Analyzer analyzer);
    }

    private sealed record LiteralNode(int Codepoint) : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => RegexInfo.Literal(Codepoint);
    }

    /// <summary>A positive, ASCII-only enumerable character class (e.g. <c>[abc]</c>, <c>[a-f]</c>).</summary>
    private sealed record ByteSetNode(IReadOnlyList<int> Bytes) : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => RegexInfo.ByteSet(Bytes);
    }

    /// <summary>Any single character — <c>.</c>, a shorthand class (<c>\d\w\s</c>), or a negated/non-ASCII class.</summary>
    private sealed record AnyCharNode : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => RegexInfo.AnyChar();
    }

    /// <summary>Zero-width — an anchor (<c>^ $ \b \A \z</c>) or epsilon.</summary>
    private sealed record EmptyNode : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => RegexInfo.Empty();
    }

    private sealed record ConcatNode(IReadOnlyList<Node> Parts) : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => analyzer.AnalyzeConcat(Parts);
    }

    private sealed record AltNode(IReadOnlyList<Node> Branches) : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => analyzer.AnalyzeAlt(Branches);
    }

    /// <summary><c>Max == -1</c> means unbounded.</summary>
    private sealed record RepeatNode(Node Child, int Min, int Max) : Node
    {
        public override RegexInfo Analyze(Analyzer analyzer) => analyzer.AnalyzeRepeat(this);
    }

    // ─────────────────────── Conservative regex parser ───────────────────────

    /// <summary>
    /// Recursive-descent parser for the conservative .NET-regex subset the planner understands.
    /// Returns <c>null</c> for any unsupported construct (lookaround, backreferences, named/inline
    /// groups, conditionals) or a syntax error, which makes the whole query ineligible.
    /// </summary>
    private sealed class RegexParser
    {
        private readonly string _p;
        private int _pos;
        private bool _failed;

        public RegexParser(string pattern) => _p = pattern;

        public Node? Parse()
        {
            Node? node = ParseAlternation();
            if (_failed || node is null || _pos != _p.Length)
                return null;
            return node;
        }

        private char Peek(int offset = 0)
        {
            int i = _pos + offset;
            return i < _p.Length ? _p[i] : '\0';
        }

        private Node? ParseAlternation()
        {
            var branches = new List<Node>();
            Node? first = ParseConcat();
            if (first is null) return null;
            branches.Add(first);
            while (Peek() == '|')
            {
                _pos++;
                Node? next = ParseConcat();
                if (next is null) return null;
                branches.Add(next);
            }
            return branches.Count == 1 ? branches[0] : new AltNode(branches);
        }

        private Node? ParseConcat()
        {
            var parts = new List<Node>();
            while (_pos < _p.Length && Peek() != '|' && Peek() != ')')
            {
                Node? atom = ParseRepeat();
                if (atom is null) return null;
                parts.Add(atom);
            }
            if (parts.Count == 0) return new EmptyNode();
            return parts.Count == 1 ? parts[0] : new ConcatNode(parts);
        }

        private Node? ParseRepeat()
        {
            Node? atom = ParseAtom();
            if (atom is null) return null;

            char c = Peek();
            if (c == '*') { _pos++; atom = new RepeatNode(atom, 0, -1); }
            else if (c == '+') { _pos++; atom = new RepeatNode(atom, 1, -1); }
            else if (c == '?') { _pos++; atom = new RepeatNode(atom, 0, 1); }
            else if (c == '{')
            {
                Node repeated = ParseBrace(atom);
                // If the brace was not a valid quantifier, atom is returned unchanged and '{' stays
                // unconsumed — the loop below is skipped and '{' is parsed as a literal next time.
                if (!ReferenceEquals(repeated, atom))
                {
                    atom = repeated;
                    // consume a trailing lazy/possessive marker
                    if (Peek() == '?' || Peek() == '+') _pos++;
                }
                return atom;
            }
            else
            {
                return atom;
            }

            // consume a trailing lazy/possessive marker after * + ?
            if (Peek() == '?' || Peek() == '+') _pos++;
            return atom;
        }

        private Node ParseBrace(Node atom)
        {
            int save = _pos;
            _pos++; // consume '{'
            int min = ReadInt();
            if (min < 0) { _pos = save; return atom; }
            int max;
            if (Peek() == ',')
            {
                _pos++;
                if (Peek() == '}') { max = -1; }
                else { max = ReadInt(); if (max < 0) { _pos = save; return atom; } }
            }
            else
            {
                max = min;
            }
            if (Peek() != '}') { _pos = save; return atom; }
            _pos++; // consume '}'
            return new RepeatNode(atom, min, max);
        }

        private int ReadInt()
        {
            int start = _pos;
            long value = 0;
            while (_pos < _p.Length && _p[_pos] >= '0' && _p[_pos] <= '9')
            {
                value = value * 10 + (_p[_pos] - '0');
                if (value > int.MaxValue) value = int.MaxValue;
                _pos++;
            }
            return _pos == start ? -1 : (int)value;
        }

        private Node? ParseAtom()
        {
            char c = Peek();
            switch (c)
            {
                case '(': return ParseGroup();
                case '[': return ParseClass();
                case '.': _pos++; return new AnyCharNode();
                case '^':
                case '$': _pos++; return new EmptyNode();
                case '\\': return ParseEscape();
                case '{': _pos++; return new LiteralNode('{');
                case '*':
                case '+':
                case '?':
                case ')':
                    _failed = true;
                    return null;
                default:
                    return ParseLiteralChar();
            }
        }

        private LiteralNode ParseLiteralChar()
        {
            char c = _p[_pos++];
            if (char.IsHighSurrogate(c) && _pos < _p.Length && char.IsLowSurrogate(_p[_pos]))
            {
                int cp = char.ConvertToUtf32(c, _p[_pos]);
                _pos++;
                return new LiteralNode(cp);
            }
            return new LiteralNode(c);
        }

        private Node? ParseGroup()
        {
            _pos++; // consume '('
            if (Peek() == '?')
            {
                _pos++;
                if (Peek() == ':')
                {
                    _pos++;
                    Node? inner = ParseAlternation();
                    if (inner is null) return null;
                    if (Peek() != ')') { _failed = true; return null; }
                    _pos++;
                    return inner;
                }
                // Lookaround, named groups, inline options, conditionals, atomic groups → unsupported.
                _failed = true;
                return null;
            }
            Node? body = ParseAlternation();
            if (body is null) return null;
            if (Peek() != ')') { _failed = true; return null; }
            _pos++;
            return body;
        }

        private Node? ParseEscape()
        {
            _pos++; // consume '\'
            if (_pos >= _p.Length) { _failed = true; return null; }
            char e = _p[_pos++];
            switch (e)
            {
                case 'd': case 'D': case 'w': case 'W': case 's': case 'S':
                    return new AnyCharNode();
                case 'b': case 'B': case 'A': case 'Z': case 'z': case 'G':
                    return new EmptyNode();
                case 'n': return new LiteralNode('\n');
                case 'r': return new LiteralNode('\r');
                case 't': return new LiteralNode('\t');
                case 'f': return new LiteralNode('\f');
                case 'v': return new LiteralNode('\v');
                case 'a': return new LiteralNode('\a');
                case 'e': return new LiteralNode(0x1B);
                case '0': return new LiteralNode(0);
                case 'x': { int v = ReadHex(2); return v < 0 ? FailNull() : new LiteralNode(v); }
                case 'u': { int v = ReadHex(4); return v < 0 ? FailNull() : new LiteralNode(v); }
                default:
                    if (e is >= '1' and <= '9') { _failed = true; return null; } // backreference
                    if (char.IsLetter(e)) { _failed = true; return null; }        // unknown letter escape
                    return new LiteralNode(e);                                     // escaped punctuation
            }
        }

        private Node? FailNull()
        {
            _failed = true;
            return null;
        }

        private int ReadHex(int count)
        {
            if (_pos + count > _p.Length) return -1;
            int v = 0;
            for (int i = 0; i < count; i++)
            {
                int d = HexValue(_p[_pos + i]);
                if (d < 0) return -1;
                v = v * 16 + d;
            }
            _pos += count;
            return v;
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        private Node? ParseClass()
        {
            _pos++; // consume '['
            bool negated = false;
            if (Peek() == '^') { negated = true; _pos++; }

            bool simple = !negated;
            var members = new List<int>();
            bool first = true;
            while (true)
            {
                if (_pos >= _p.Length) { _failed = true; return null; } // unterminated class
                char c = _p[_pos];
                if (c == ']' && !first) { _pos++; break; }
                first = false;

                if (c == '\\')
                {
                    // Any escape inside a class conservatively demotes it to "any char".
                    simple = false;
                    _pos += 2;
                    if (_pos > _p.Length) { _failed = true; return null; }
                    continue;
                }

                int lo = c;
                _pos++;
                if (_pos + 1 < _p.Length && _p[_pos] == '-' && _p[_pos + 1] != ']')
                {
                    char hiChar = _p[_pos + 1];
                    if (hiChar == '\\')
                    {
                        simple = false;
                        _pos++; // consume '-'; the backslash is handled next iteration
                        continue;
                    }
                    _pos += 2; // consume '-' and the high char
                    int hi = hiChar;
                    if (lo > 0x7F || hi > 0x7F || hi < lo)
                        simple = false;
                    else
                        for (int b = lo; b <= hi; b++) members.Add(b);
                }
                else
                {
                    if (lo > 0x7F) simple = false;
                    else members.Add(lo);
                }
            }

            if (!simple) return new AnyCharNode();
            var distinct = members.Distinct().ToList();
            return new ByteSetNode(distinct);
        }
    }

    // ───────────────────── Required-trigram analysis ─────────────────────

    private sealed class Analyzer
    {
        private int _budget = AnalysisNodeBudget;

        public bool BudgetExceeded { get; private set; }

        public RegexInfo Analyze(Node node)
        {
            if (--_budget <= 0)
            {
                BudgetExceeded = true;
                return RegexInfo.AnyChar();
            }

            return node.Analyze(this);
        }

        public RegexInfo AnalyzeConcat(IReadOnlyList<Node> parts)
        {
            RegexInfo acc = Analyze(parts[0]);
            for (int i = 1; i < parts.Count; i++)
                acc = RegexInfo.Concat(acc, Analyze(parts[i]));
            return acc;
        }

        public RegexInfo AnalyzeAlt(IReadOnlyList<Node> branches)
        {
            RegexInfo acc = Analyze(branches[0]);
            for (int i = 1; i < branches.Count; i++)
                acc = RegexInfo.Alternate(acc, Analyze(branches[i]));
            return acc;
        }

        public RegexInfo AnalyzeRepeat(RepeatNode r)
        {
            if (r.Max == 0)
                return RegexInfo.Empty();              // e{0} → epsilon
            if (r.Min == 0)
                return RegexInfo.AnyMatch();           // *, ?, {0,m} → emptyable, nothing required

            if (r.Min == r.Max && r.Min <= RepeatExpandMax)
            {
                RegexInfo acc = Analyze(r.Child);
                for (int k = 1; k < r.Min; k++)
                    acc = RegexInfo.Concat(acc, Analyze(r.Child));
                return acc;
            }

            // Variable repetition with min >= 1 (or a huge fixed count): require the child at least once.
            RegexInfo child = Analyze(r.Child);
            RegexInfo.Flush(child);
            return RegexInfo.FromMatch(child.Match, child.CanEmpty);
        }
    }

    // ─────────────────────── RegexInfo (byte-string sets) ───────────────────────

    /// <summary>
    /// Analysis state for a sub-expression: whether it can match empty, an optional finite set of
    /// exact byte-strings it can match, and the required-trigram query for the parts that are not
    /// captured exactly. Byte-strings are stored as .NET strings whose chars each hold one canonical
    /// UTF-8 byte (0–255), mirroring the index representation (with CR normalized to LF, plan §3.2).
    /// </summary>
    private sealed class RegexInfo
    {
        public bool CanEmpty;
        public HashSet<string>? Exact;
        public TrigramExpression Match = TrigramExpression.All;

        public static RegexInfo Empty() => new() { CanEmpty = true, Exact = new HashSet<string> { string.Empty } };

        public static RegexInfo AnyMatch() => new() { CanEmpty = true, Exact = null };

        public static RegexInfo AnyChar() => new() { CanEmpty = false, Exact = null };

        public static RegexInfo Literal(int codepoint)
            => new() { CanEmpty = false, Exact = new HashSet<string> { PackCodepoint(codepoint) } };

        public static RegexInfo ByteSet(IReadOnlyList<int> bytes)
        {
            var set = new HashSet<string>();
            foreach (int b in bytes)
                set.Add(new string((char)NormalizeByte(b), 1));
            return new RegexInfo { CanEmpty = false, Exact = set, Match = TrigramExpression.All };
        }

        public static RegexInfo LiteralString(string literal)
            => new() { CanEmpty = literal.Length == 0, Exact = new HashSet<string> { PackString(literal) } };

        public static RegexInfo FromMatch(TrigramExpression match, bool canEmpty)
            => new() { CanEmpty = canEmpty, Exact = null, Match = match };

        public static void Flush(RegexInfo info)
        {
            if (info.Exact is not null)
            {
                info.Match = TrigramExpression.And(info.Match, ExactSetQuery(info.Exact));
                info.Exact = null;
            }
        }

        public static RegexInfo Concat(RegexInfo x, RegexInfo y)
        {
            var result = new RegexInfo { CanEmpty = x.CanEmpty && y.CanEmpty };
            bool xExact = x.Exact is not null;
            bool yExact = y.Exact is not null;

            if (xExact && yExact)
            {
                if (CanCross(x.Exact!, y.Exact!))
                {
                    result.Exact = Cross(x.Exact!, y.Exact!);
                    result.Match = TrigramExpression.And(x.Match, y.Match);
                }
                else
                {
                    Flush(x);
                    Flush(y);
                    result.Exact = null;
                    result.Match = TrigramExpression.And(x.Match, y.Match);
                }
            }
            else if (xExact) // x exact, y non-exact: x is the complete left run — flush it.
            {
                Flush(x);
                result.Exact = null;
                result.Match = TrigramExpression.And(x.Match, y.Match);
            }
            else if (yExact) // x non-exact, y exact: start a fresh exact run that can keep growing
            {                // rightward (so trailing literals after a non-exact node — e.g. "bar" in
                             // "foo.*bar" — still contribute their trigrams). Carry x's required match.
                result.Exact = y.Exact;
                result.Match = TrigramExpression.And(x.Match, y.Match);
            }
            else // both non-exact
            {
                result.Exact = null;
                result.Match = TrigramExpression.And(x.Match, y.Match);
            }
            return result;
        }

        public static RegexInfo Alternate(RegexInfo x, RegexInfo y)
        {
            var result = new RegexInfo { CanEmpty = x.CanEmpty || y.CanEmpty };
            if (x.Exact is not null && y.Exact is not null && x.Exact.Count + y.Exact.Count <= ExactSetCountCap)
            {
                var union = new HashSet<string>(x.Exact);
                union.UnionWith(y.Exact);
                result.Exact = union;
                result.Match = TrigramExpression.Or(x.Match, y.Match);
            }
            else
            {
                Flush(x);
                Flush(y);
                result.Exact = null;
                result.Match = TrigramExpression.Or(x.Match, y.Match);
            }
            return result;
        }

        private static bool CanCross(HashSet<string> a, HashSet<string> b)
        {
            if ((long)a.Count * b.Count > ExactSetCountCap)
                return false;
            int maxA = 0, maxB = 0;
            foreach (string s in a) maxA = Math.Max(maxA, s.Length);
            foreach (string s in b) maxB = Math.Max(maxB, s.Length);
            return maxA + maxB <= ExactStringMaxLen;
        }

        private static HashSet<string> Cross(HashSet<string> a, HashSet<string> b)
        {
            var result = new HashSet<string>();
            foreach (string x in a)
                foreach (string y in b)
                    result.Add(x + y);
            return result;
        }

        private static TrigramExpression ExactSetQuery(HashSet<string> set)
        {
            TrigramExpression acc = TrigramExpression.None;
            foreach (string s in set)
                acc = TrigramExpression.Or(acc, TrigramsOf(s));
            return acc;
        }

        private static TrigramExpression TrigramsOf(string packed)
        {
            if (packed.Length < 3)
                return TrigramExpression.All;
            TrigramExpression acc = TrigramExpression.All;
            for (int i = 0; i + 3 <= packed.Length; i++)
                acc = TrigramExpression.And(acc, TrigramExpression.OfTrigram(
                    new Trigram((byte)packed[i], (byte)packed[i + 1], (byte)packed[i + 2])));
            return acc;
        }

        private static int NormalizeByte(int b) => b == '\r' ? '\n' : b;

        private static string PackCodepoint(int codepoint)
        {
            var bytes = new List<byte>(4);
            AppendUtf8(codepoint, bytes);
            return Pack(bytes);
        }

        private static string PackString(string literal)
        {
            var bytes = new List<byte>(literal.Length);
            foreach (Rune rune in literal.EnumerateRunes())
                AppendUtf8(rune.Value, bytes);
            return Pack(bytes);
        }

        private static void AppendUtf8(int codepoint, List<byte> dst)
        {
            int cp = NormalizeByte(codepoint);
            if (cp < 0x80)
            {
                dst.Add((byte)cp);
            }
            else if (cp < 0x800)
            {
                dst.Add((byte)(0xC0 | (cp >> 6)));
                dst.Add((byte)(0x80 | (cp & 0x3F)));
            }
            else if (cp < 0x10000)
            {
                dst.Add((byte)(0xE0 | (cp >> 12)));
                dst.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
                dst.Add((byte)(0x80 | (cp & 0x3F)));
            }
            else
            {
                dst.Add((byte)(0xF0 | (cp >> 18)));
                dst.Add((byte)(0x80 | ((cp >> 12) & 0x3F)));
                dst.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
                dst.Add((byte)(0x80 | (cp & 0x3F)));
            }
        }

        private static string Pack(List<byte> bytes)
        {
            var chars = new char[bytes.Count];
            for (int i = 0; i < bytes.Count; i++)
                chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }
}
