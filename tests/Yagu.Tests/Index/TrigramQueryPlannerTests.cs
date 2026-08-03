using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="TrigramQueryPlanner"/> (plan §4): eligibility gating, conservative regex
/// reduction, and the top-priority <b>superset guarantee</b> — every document that truly matches an
/// eligible query must evaluate <c>true</c> against the planned trigram expression. The superset
/// property is validated both with hand-picked cases and a randomized differential test against the
/// real .NET regex/substring verifier.
/// </summary>
public sealed class TrigramQueryPlannerTests
{
    private static EffectiveSearchPattern Literal(string s, bool caseSensitive = true)
        => new(s, isRegex: false, caseSensitive, multiline: false, dotAll: false);

    private static EffectiveSearchPattern Rx(string s, bool caseSensitive = true)
        => new(s, isRegex: true, caseSensitive, multiline: false, dotAll: false);

    private static HashSet<Trigram> DocTrigrams(string ascii)
    {
        var verdict = ContentRepresentation.Classify(Encoding.UTF8.GetBytes(ascii), out var list);
        Assert.Equal(ContentRepresentationVerdict.Indexed, verdict);
        return new HashSet<Trigram>(list);
    }

    private static TrigramExpression Eligible(EffectiveSearchPattern p)
    {
        var plan = TrigramQueryPlanner.Plan(p);
        var eligible = Assert.IsType<TrigramPlan.Eligible>(plan);
        return eligible.Query;
    }

    private static string IneligibleReason(EffectiveSearchPattern p)
        => Assert.IsType<TrigramPlan.Ineligible>(TrigramQueryPlanner.Plan(p)).Reason;

    // ─────────────────────────── Eligibility gating ───────────────────────────

    [Fact]
    public void Plan_EmptyPattern_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonEmptyQuery, IneligibleReason(Literal("")));

    [Fact]
    public void Plan_CaseInsensitiveNonAsciiLiteral_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonCaseInsensitive, IneligibleReason(Literal("caf\u00e9\u00e9\u00e9", caseSensitive: false)));

    [Fact]
    public void Plan_CaseInsensitiveNonAsciiRegex_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonCaseInsensitive, IneligibleReason(Rx("\\bstra\u00dfe\\b", caseSensitive: false)));

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    public void Plan_ShortLiteral_Ineligible(string literal)
        => Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram, IneligibleReason(Literal(literal)));

    [Fact]
    public void Plan_DotStar_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram, IneligibleReason(Rx(".*")));

    [Fact]
    public void Plan_SingleDot_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram, IneligibleReason(Rx(".")));

    [Fact]
    public void Plan_DigitClass_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram, IneligibleReason(Rx(@"\d+")));

    [Fact]
    public void Plan_EmptyAlternationBranch_Ineligible()
        => Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram, IneligibleReason(Rx("foo|")));

    [Theory]
    [InlineData("(?=foo)bar")]      // lookahead
    [InlineData("(?!foo)bar")]      // negative lookahead
    [InlineData("(?<=foo)bar")]     // lookbehind
    [InlineData(@"(foo)\1")]        // backreference
    [InlineData("(?<name>foo)")]    // named group
    [InlineData("(?i)foo")]         // inline options
    [InlineData("(foo")]            // unbalanced paren
    [InlineData(@"\p{L}bar")]       // unknown escape
    public void Plan_UnsupportedConstructs_Ineligible(string pattern)
        => Assert.Equal(TrigramQueryPlanner.ReasonUnsupportedRegex, IneligibleReason(Rx(pattern)));

    // ─────────────────────────── Eligible reductions ───────────────────────────

    [Fact]
    public void Plan_Literal_RequiresAllTrigrams()
    {
        var query = Eligible(Literal("hello"));
        Assert.True(query.Evaluate(DocTrigrams("say hello world")));
        Assert.False(query.Evaluate(DocTrigrams("help")));
    }

    [Fact]
    public void Plan_RegexLiteral_RequiresAllTrigrams()
    {
        var query = Eligible(Rx("hello"));
        Assert.True(query.Evaluate(DocTrigrams("hello")));
        Assert.False(query.Evaluate(DocTrigrams("hell")));
    }

    [Fact]
    public void Plan_DotStarBetweenLiterals_RequiresBoth()
    {
        var query = Eligible(Rx("foo.*bar"));
        Assert.True(query.Evaluate(DocTrigrams("foo and bar")));
        Assert.False(query.Evaluate(DocTrigrams("foo only")));
        Assert.False(query.Evaluate(DocTrigrams("bar only")));
    }

    [Fact]
    public void Plan_Alternation_RequiresEitherBranch()
    {
        var query = Eligible(Rx("abc|xyz"));
        Assert.True(query.Evaluate(DocTrigrams("--abc--")));
        Assert.True(query.Evaluate(DocTrigrams("--xyz--")));
        Assert.False(query.Evaluate(DocTrigrams("--pqr--")));
    }

    [Fact]
    public void Plan_WholeWordWrapping_RequiresInnerLiteral()
    {
        var query = Eligible(Rx(@"\bfoo\b"));
        Assert.True(query.Evaluate(DocTrigrams("a foo b")));
        Assert.False(query.Evaluate(DocTrigrams("a f b")));
    }

    [Fact]
    public void Plan_NonCapturingGroup_RequiresBoth()
    {
        var query = Eligible(Rx("(?:foo)bar"));
        Assert.True(query.Evaluate(DocTrigrams("foobar")));
        Assert.False(query.Evaluate(DocTrigrams("foo")));
    }

    [Fact]
    public void Plan_OptionalCharBetweenLiterals_KeepsFixedPrefixTrigrams()
    {
        // colou?r → must contain "col" and "olo".
        var query = Eligible(Rx("colou?r"));
        Assert.True(query.Evaluate(DocTrigrams("colour")));
        Assert.True(query.Evaluate(DocTrigrams("color")));
        Assert.False(query.Evaluate(DocTrigrams("collar")));
    }

    [Fact]
    public void Plan_FixedRepeat_ExpandsToLiteral()
    {
        var query = Eligible(Rx("a{3}"));
        Assert.True(query.Evaluate(DocTrigrams("baaad")));
        Assert.False(query.Evaluate(DocTrigrams("baad")));
    }

    [Fact]
    public void Plan_AsciiCharClassBetweenLiterals_RequiresLiterals()
    {
        var query = Eligible(Rx("foo[abc]bar"));
        Assert.True(query.Evaluate(DocTrigrams("fooabar")));
        Assert.False(query.Evaluate(DocTrigrams("fooqux")));
    }

    [Fact]
    public void Plan_PlusRepeatOfMultiCharGroup_RequiresGroupTrigrams()
    {
        var query = Eligible(Rx("(?:abc)+"));
        Assert.True(query.Evaluate(DocTrigrams("abcabc")));
        Assert.False(query.Evaluate(DocTrigrams("ab")));
    }

    // ─────────────────── Regex construct coverage (parser + analyzer) ───────────────────

    [Fact]
    public void Plan_CapturingGroup_RequiresInnerTrigrams()
    {
        var query = Eligible(Rx("(foo)bar"));
        Assert.True(query.Evaluate(DocTrigrams("foobar")));
        Assert.False(query.Evaluate(DocTrigrams("foo")));
    }

    [Theory]
    [InlineData("(?:abc){2,}", "abcabc")]   // {n,} unbounded, min>=1 -> variable repeat
    [InlineData("abcd{2,5}", "abcddd")]      // {n,m} min>=1 on trailing literal
    [InlineData("(?:abc){2,5}", "abcabc")]   // {n,m} on a multi-char group
    [InlineData("food{0}bar", "foobar")]     // {0} -> epsilon, cross keeps growing
    public void Plan_BraceQuantifiers_RequireExpectedTrigrams(string pattern, string matchingDoc)
    {
        var query = Eligible(Rx(pattern));
        Assert.Matches(pattern, matchingDoc);                 // sanity: real regex agrees
        Assert.True(query.Evaluate(DocTrigrams(matchingDoc))); // superset: query must not prune it
    }

    [Theory]
    [InlineData(@"a\x62c", "abc")]           // \xNN hex escape (0x62 == 'b')
    [InlineData(@"fo\x6Fbar", "foobar")]     // \xNN with uppercase hex digit 'F'
    [InlineData(@"fo\x6fbar", "foobar")]     // \xNN with lowercase hex digit 'f'
    [InlineData(@"a\u0062c", "abc")]         // \uNNNN unicode escape
    [InlineData(@"foo\u00e9bar", "foo\u00e9bar")] // 2-byte UTF-8 codepoint (e-acute)
    [InlineData(@"foo\u20acbar", "foo\u20acbar")] // 3-byte UTF-8 codepoint (euro sign)
    [InlineData(@"foo\.bar", "foo.bar")]     // escaped punctuation -> literal dot
    [InlineData(@"foo\tbar", "foo\tbar")]    // control escape \t
    [InlineData("foo\\nbar", "foo\nbar")]    // \n literal newline escape
    [InlineData("foo\\rbar", "foo\rbar")]    // \r literal carriage return (normalized to LF)
    [InlineData(@"\Afoobar\z", "foobar")]    // \A / \z anchors -> epsilon
    [InlineData(@"\Gfoobar", "foobar")]      // \G anchor
    [InlineData(@"foo\Bbar", "foobar")]      // \B (non-word-boundary) anchor -> epsilon
    [InlineData("^foobar$", "foobar")]       // ^ / $ line anchors -> epsilon
    public void Plan_SupportedEscapes_RequireExpectedTrigrams(string pattern, string matchingDoc)
    {
        var query = Eligible(Rx(pattern));
        Assert.True(query.Evaluate(DocTrigrams(matchingDoc)));
    }

    [Theory]
    [InlineData(@"foo\abar")]  // \a bell (0x07) - eligible but a matching doc would be binary
    [InlineData(@"foo\ebar")]  // \e escape (0x1B)
    [InlineData(@"foo\fbar")]  // \f form feed
    [InlineData(@"foo\vbar")]  // \v vertical tab
    [InlineData(@"foo\0bar")]  // \0 NUL
    public void Plan_ControlEscapes_AreEligible(string pattern)
        => Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(Rx(pattern)));

    [Theory]
    [InlineData(@"foo\xZZbar")]  // invalid hex digits
    [InlineData(@"ab\x1")]       // truncated hex at end of pattern
    [InlineData(@"ab\uZZZZ")]    // invalid unicode digits
    [InlineData(@"ab\u12")]      // truncated unicode escape
    public void Plan_InvalidNumericEscapes_Ineligible(string pattern)
        => Assert.Equal(TrigramQueryPlanner.ReasonUnsupportedRegex, IneligibleReason(Rx(pattern)));

    [Theory]
    [InlineData("foo[^xyz]bar", "fooqbar")]  // negated class -> any-char
    [InlineData("foo[a-f]bar", "foocbar")]   // ascii range -> byte set
    [InlineData("foo[abc]bar", "fooabar")]   // ascii set
    [InlineData(@"foo[\d]bar", "foo5bar")]   // escape in class demotes to any-char
    [InlineData(@"foo[a-\d]bar", "foo5bar")] // range whose high end is an escape -> any-char
    [InlineData("foo[a-]bar", "foo-bar")]    // trailing dash is a literal member
    [InlineData("foo[\u00e0-\u00ff]bar", "foo\u00e9bar")] // non-ascii range -> any-char
    [InlineData("foo[\u00e9]bar", "foo\u00e9bar")] // non-ascii singleton -> any-char
    public void Plan_CharacterClasses_RequireLiteralTrigrams(string pattern, string matchingDoc)
    {
        var query = Eligible(Rx(pattern));
        // Every class variant still requires the surrounding "foo"/"bar" literals.
        Assert.True(query.Evaluate(DocTrigrams(matchingDoc)));
        Assert.False(query.Evaluate(DocTrigrams("qqqqqqq")));
    }

    [Theory]
    [InlineData("*foo")]        // leading quantifier
    [InlineData("+foo")]
    [InlineData("?foo")]
    [InlineData("foo[abc")]     // unterminated character class
    [InlineData("foo(bar")]     // unterminated group
    public void Plan_MalformedConstructs_Ineligible(string pattern)
        => Assert.Equal(TrigramQueryPlanner.ReasonUnsupportedRegex, IneligibleReason(Rx(pattern)));

    [Theory]
    [InlineData("foo|*")]
    [InlineData("(?:*)")]
    [InlineData("(?:foo")]
    [InlineData("(*)")]
    [InlineData("foo\\")]
    [InlineData("foo[\\")]
    public void Plan_AdditionalMalformedBoundaries_Ineligible(string pattern)
        => Assert.Equal(TrigramQueryPlanner.ReasonUnsupportedRegex, IneligibleReason(Rx(pattern)));

    [Fact]
    public void Plan_InvalidBrace_TreatedAsLiteralBrace()
    {
        // "{cd" is not a valid quantifier, so '{' is parsed as a literal and the whole thing is a literal run.
        var query = Eligible(Rx("ab{cd"));
        Assert.True(query.Evaluate(DocTrigrams("ab{cd")));
    }

    [Fact]
    public void Plan_BraceWithNonNumericMax_TreatedAsLiteral()
    {
        var query = Eligible(Rx("xy{2,z}w"));
        Assert.True(query.Evaluate(DocTrigrams("xy{2,z}w")));
    }

    [Theory]
    [InlineData("abc{2")]
    [InlineData("abc{2,3")]
    public void Plan_UnterminatedNumericBrace_TreatedAsLiteral(string pattern)
    {
        var query = Eligible(Rx(pattern));
        Assert.True(query.Evaluate(DocTrigrams(pattern)));
    }

    [Fact]
    public void Plan_OversizedBraceCount_IsBoundedAndConservative()
        => Assert.Equal(
            TrigramQueryPlanner.ReasonNoRequiredTrigram,
            IneligibleReason(Rx("abc{999999999999999999999}")));

    [Theory]
    [InlineData("(?:abc){2}?")]
    [InlineData("(?:abc){2}+")]
    [InlineData("(?:abc)+?")]
    [InlineData("(?:abc)++")]
    public void Plan_LazyAndPossessiveQuantifiers_RemainEligible(string pattern)
        => Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(Rx(pattern)));

    [Fact]
    public void Plan_SurrogatePairLiteral_RequiresMultibyteTrigrams()
    {
        // An astral-plane codepoint (emoji) exercises the surrogate-pair path + 4-byte UTF-8 packing.
        var query = Eligible(Rx("hello x\U0001F600yz world"));
        Assert.True(query.Evaluate(DocTrigrams("hello x\U0001F600yz world")));
    }

    [Fact]
    public void Plan_ExactSetCrossProduct_RequiresOneCombination()
    {
        // {ab,cd} x {ef,gh} -> {abef, abgh, cdef, cdgh}; each 4-char combination has trigrams.
        var query = Eligible(Rx("(?:ab|cd)(?:ef|gh)"));
        Assert.True(query.Evaluate(DocTrigrams("--abef--")));
        Assert.True(query.Evaluate(DocTrigrams("--cdgh--")));
        Assert.False(query.Evaluate(DocTrigrams("--abgx--")));
    }

    [Fact]
    public void Plan_CrossProductExceedingCap_FlushesButStaysSuperset()
    {
        // Four 2-way alternations -> 16 combinations exceeds the exact-set cap, forcing a flush; the
        // planned query must still never hide a real match.
        var pattern = "(?:ab|cd)(?:ef|gh)(?:ij|kl)(?:mn|op)";
        var query = Eligible(Rx(pattern));
        var doc = "abefijmn";
        Assert.Matches(pattern, doc);
        Assert.True(query.Evaluate(DocTrigrams(doc)));
    }

    [Fact]
    public void Plan_AlternationExceedingCap_FlushesButStaysSuperset()
    {
        // Nine branches exceeds the exact-set cap on the union path.
        var pattern = "(?:abc|def|ghi|jkl|mno|pqr|stu|vwx|yza)";
        var query = Eligible(Rx(pattern));
        foreach (var doc in new[] { "abc", "yza", "mno" })
            Assert.True(query.Evaluate(DocTrigrams("--" + doc + "--")));
    }

    [Theory]
    [InlineData("|abc")]
    [InlineData(".*|abc")]
    [InlineData("abc|.*")]
    public void Plan_EmptyableOrNonExactAlternation_HasNoRequiredTrigram(string pattern)
        => Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram, IneligibleReason(Rx(pattern)));

    [Fact]
    public void Plan_WorkBudgetExceeded_Ineligible()
    {
        // Deeply nested bounded repeats expand past the analyzer node budget, so the planner gives up
        // conservatively (live-scan) rather than spending unbounded time.
        var pattern = "(?:(?:(?:(?:a{16}){16}){16}){16}){16}";
        Assert.Equal(TrigramQueryPlanner.ReasonWorkBudget, IneligibleReason(Rx(pattern)));
    }

    // ─────────────────────────── Differential superset guarantee ───────────────────────────


    [Fact]
    public void Differential_LiteralQueries_NeverHideAMatch()
    {
        var rng = new Random(1234);
        const string alphabet = "abcdef";
        int checks = 0;

        for (int i = 0; i < 400; i++)
        {
            string literal = RandomWord(rng, alphabet, 3, 6);
            var plan = TrigramQueryPlanner.Plan(Literal(literal));
            if (plan is not TrigramPlan.Eligible eligible)
                continue;

            for (int d = 0; d < 25; d++)
            {
                string doc = RandomWord(rng, alphabet, 0, 20);
                if (doc.Contains(literal, StringComparison.Ordinal))
                {
                    Assert.True(
                        eligible.Query.Evaluate(DocTrigrams(doc.Length == 0 ? "x" : doc)),
                        $"Literal '{literal}' matched doc '{doc}' but the trigram query pruned it.");
                    checks++;
                }
            }
        }
        Assert.True(checks > 0, "Differential literal test performed no positive checks.");
    }

    [Fact]
    public void Differential_RegexQueries_NeverHideAMatch()
    {
        var rng = new Random(98765);
        const string alphabet = "abcdef";
        int checks = 0;

        for (int i = 0; i < 800; i++)
        {
            string pattern = RandomPattern(rng, alphabet);
            Regex regex;
            try { regex = new Regex(pattern, RegexOptions.CultureInvariant); }
            catch (ArgumentException) { continue; }

            var plan = TrigramQueryPlanner.Plan(Rx(pattern));
            if (plan is not TrigramPlan.Eligible eligible)
                continue;

            for (int d = 0; d < 20; d++)
            {
                string doc = RandomWord(rng, alphabet, 1, 24);
                if (regex.IsMatch(doc))
                {
                    Assert.True(
                        eligible.Query.Evaluate(DocTrigrams(doc)),
                        $"Pattern '{pattern}' matched doc '{doc}' but the trigram query pruned it.");
                    checks++;
                }
            }
        }
        Assert.True(checks > 0, "Differential regex test performed no positive checks.");
    }

    // ─────────────────────────── Case-insensitive ASCII folding ───────────────────────────

    [Fact]
    public void Plan_CaseInsensitiveLiteral_MatchesAnyCasing()
    {
        var query = Eligible(Literal("hello", caseSensitive: false));
        Assert.True(query.Evaluate(DocTrigrams("say hello world")));
        Assert.True(query.Evaluate(DocTrigrams("say HELLO world")));
        Assert.True(query.Evaluate(DocTrigrams("say HeLLo world")));
        Assert.False(query.Evaluate(DocTrigrams("help")));
    }

    [Fact]
    public void Plan_CaseInsensitiveLiteral_KeepsKAndSTrigrams()
    {
        // A plain literal runs through the ASCII-only byte matchers (Kelvin/long-s never match), so
        // k/s trigrams stay required and the query is still selective.
        var query = Eligible(Literal("task", caseSensitive: false));
        Assert.True(query.Evaluate(DocTrigrams("a TASK here")));
        Assert.False(query.Evaluate(DocTrigrams("nothing relevant")));
    }

    [Fact]
    public void Plan_CaseInsensitiveLiteral_FoldsUpperLowerAndNonLetterBytes()
    {
        var query = Eligible(Literal("A1b", caseSensitive: false));
        Assert.True(query.Evaluate(DocTrigrams("a1B")));
        Assert.False(query.Evaluate(DocTrigrams("a2B")));
    }

    [Fact]
    public void Plan_CaseInsensitiveWholeWord_MatchesAnyCasing()
    {
        // Whole-word wraps the literal in \b...\b (a regex form → Unicode-folding engines).
        var query = Eligible(Rx(@"\bhello\b", caseSensitive: false));
        Assert.True(query.Evaluate(DocTrigrams("HELLO")));
        Assert.True(query.Evaluate(DocTrigrams("hello")));
        Assert.False(query.Evaluate(DocTrigrams("hxllo")));
    }

    [Fact]
    public void Plan_CaseInsensitiveRegex_DropsTrigramsThatCouldBeMultiByteFolds()
    {
        // Every trigram of "pass" contains 's' (a multi-byte fold to U+017F under the regex engines),
        // so all trigrams are dropped and the query cannot be accelerated (it transparently live-scans).
        Assert.Equal(TrigramQueryPlanner.ReasonNoRequiredTrigram,
            IneligibleReason(Rx("pass", caseSensitive: false)));
    }

    [Theory]
    [InlineData("KAB")]
    [InlineData("SAB")]
    public void Plan_CaseInsensitiveRegex_DropsUppercaseUnicodeFoldableTrigrams(string pattern)
        => Assert.Equal(
            TrigramQueryPlanner.ReasonNoRequiredTrigram,
            IneligibleReason(Rx(pattern, caseSensitive: false)));

    [Fact]
    public void Plan_CaseInsensitiveRegex_KeepsSafeTrigramsWhenSomeContainK()
    {
        // "market": 'k' appears in ark/rke/ket, so only "mar" survives as a required trigram.
        var query = Eligible(Rx("market", caseSensitive: false));
        Assert.True(query.Evaluate(DocTrigrams("MARKET")));
        Assert.True(query.Evaluate(DocTrigrams("in the market")));
        Assert.True(query.Evaluate(DocTrigrams("MARble")));  // not pruned though it lacks k/t trigrams
        Assert.False(query.Evaluate(DocTrigrams("no relevant words")));
    }

    [Fact]
    public void Differential_CaseInsensitiveLiteral_NeverHideAMatch()
    {
        var rng = new Random(555);
        const string alphabet = "abcKSks"; // includes k/K/s/S to exercise the fold
        int checks = 0;
        for (int i = 0; i < 500; i++)
        {
            string literal = RandomWord(rng, alphabet, 3, 6);
            var plan = TrigramQueryPlanner.Plan(Literal(literal, caseSensitive: false));
            if (plan is not TrigramPlan.Eligible eligible)
                continue;
            for (int d = 0; d < 20; d++)
            {
                string doc = RandomWord(rng, "abcKSks ", 0, 24);
                if (doc.Contains(literal, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(
                        eligible.Query.Evaluate(DocTrigrams(doc.Length == 0 ? "x" : doc)),
                        $"Literal '{literal}' matched '{doc}' (OrdinalIgnoreCase) but the trigram query pruned it.");
                    checks++;
                }
            }
        }
        Assert.True(checks > 0, "Differential case-insensitive literal test performed no positive checks.");
    }

    [Fact]
    public void Differential_CaseInsensitiveRegex_NeverHideAMatch_IncludingUnicodeFolds()
    {
        var rng = new Random(24680);
        const string alphabet = "abcdefks";
        var opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        // The multi-byte fold variants the regex engine matches case-insensitively.
        string[] extras = { "\u212A" /* Kelvin → k */, "\u017F" /* long s → s */ };
        int checks = 0;
        for (int i = 0; i < 800; i++)
        {
            string pattern = RandomPattern(rng, alphabet);
            Regex regex;
            try { regex = new Regex(pattern, opts); }
            catch (ArgumentException) { continue; }

            var plan = TrigramQueryPlanner.Plan(Rx(pattern, caseSensitive: false));
            if (plan is not TrigramPlan.Eligible eligible)
                continue;

            for (int d = 0; d < 20; d++)
            {
                string doc = RandomWord(rng, alphabet, 1, 20);
                if (rng.Next(3) == 0)
                {
                    int at = rng.Next(doc.Length + 1);
                    doc = doc[..at] + extras[rng.Next(extras.Length)] + doc[at..];
                }
                if (regex.IsMatch(doc))
                {
                    Assert.True(
                        eligible.Query.Evaluate(DocTrigrams(doc)),
                        $"Pattern '{pattern}' matched '{doc}' (IgnoreCase) but the trigram query pruned it.");
                    checks++;
                }
            }
        }
        Assert.True(checks > 0, "Differential case-insensitive regex test performed no positive checks.");
    }

    [Fact]
    public void CaseFold_MultiByteFoldableLetters_CoverDotNetRegexFolding()
    {
        // Re-derive the ASCII letters whose .NET CultureInvariant IgnoreCase fold class contains a
        // non-ASCII (multi-byte) code point, and assert the planner's hardcoded drop set (k/K/s/S —
        // which also covers the Rust regex crate's long-s fold) is a superset. If a future runtime
        // adds a fold, this fails loudly so the drop set can be widened.
        var opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        var multiByte = new HashSet<char>();
        foreach (char letter in "abcdefghijklmnopqrstuvwxyz")
        {
            var re = new Regex("^" + letter + "$", opts);
            for (int c = 0x80; c <= 0xFFFF; c++)
            {
                if (c is >= 0xD800 and <= 0xDFFF) continue;
                if (re.IsMatch(((char)c).ToString())) { multiByte.Add(letter); break; }
            }
        }
        Assert.Contains('k', multiByte);                          // sanity: the scan works (Kelvin U+212A)
        Assert.Subset(new HashSet<char> { 'k', 's' }, multiByte); // multiByte ⊆ the planner's drop set
    }

    private static string RandomWord(Random rng, string alphabet, int min, int max)
    {
        int len = rng.Next(min, max + 1);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }

    private static string RandomPattern(Random rng, string alphabet)
    {
        string a = RandomWord(rng, alphabet, 1, 4);
        string b = RandomWord(rng, alphabet, 1, 4);
        return rng.Next(9) switch
        {
            0 => a,
            1 => a + b,
            2 => a + ".*" + b,
            3 => "(?:" + a + "|" + b + ")",
            4 => a + "[" + alphabet + "]" + b,
            5 => a + "x?" + b,
            6 => a + "+" + b,
            7 => "(?:" + a + "){2}",
            _ => a + "." + b,
        };
    }
}
