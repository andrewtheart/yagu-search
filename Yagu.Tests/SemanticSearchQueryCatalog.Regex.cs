using System.Collections.Generic;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // Regular-expression query scenarios. Patterns stay within the common subset
    // supported by both the .NET and Rust (native) engines — no lookaround or
    // backreferences. Regex search is case-insensitive unless CaseSensitive is set.
    private static IEnumerable<SearchScenario> RegexScenarios()
    {
        // ── Quantifiers ──────────────────────────────────────────────────────
        yield return Scn("regex-digits-quantifier")
            .File("a.txt", "foo123")
            .File("b.txt", "foo")
            .File("c.txt", "bar456")
            .Regex(@"foo\d+")
            .ExpectFiles("a.txt").ExpectMatchText("foo123").Build();

        yield return Scn("regex-star-zero-or-more")
            .File("a.txt", "ac")
            .File("b.txt", "abbbc")
            .File("c.txt", "axc")
            .Regex("ab*c")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-optional")
            .File("a.txt", "color")
            .File("b.txt", "colour")
            .File("c.txt", "colouur")
            .Regex("colou?r")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-exact-repeat")
            .File("a.txt", "id 123 here")
            .File("b.txt", "id 12 here")
            .Regex(@"\d{3}")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-range-repeat")
            .File("a.txt", "ab12cd")
            .File("b.txt", "x1y")
            .Regex(@"\d{2,4}")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-plus-letters-cs")
            .File("a.txt", "abc")
            .File("b.txt", "123")
            .File("c.txt", "ABC")
            .Regex("[a-z]+").CaseSensitive()
            .ExpectFiles("a.txt").Build();

        // ── Anchors ──────────────────────────────────────────────────────────
        yield return Scn("regex-line-anchor-start")
            .File("a.txt", "start of line\nmiddle")
            .File("b.txt", "not start here")
            .Regex(@"^start")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-line-anchor-end")
            .File("a.txt", "ends with END")
            .File("b.txt", "END is at start")
            .Regex(@"END$")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-anchored-full-line")
            .File("a.txt", "exact")
            .File("b.txt", "exact match")
            .File("c.txt", "not exact")
            .Regex(@"^exact$")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-start-digit")
            .File("a.txt", "1st line")
            .File("b.txt", "line 2")
            .Regex(@"^\d")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-line-ends-with-period")
            .File("a.txt", "sentence.")
            .File("b.txt", "no period here")
            .Regex(@"\.$")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-whole-line-digits")
            .File("a.txt", "12345")
            .File("b.txt", "123x")
            .Regex(@"^\d+$")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-anchor-end-digit")
            .File("a.txt", "value ends 9")
            .File("b.txt", "9 starts value")
            .Regex(@"\d$")
            .ExpectFiles("a.txt").Build();

        // ── Character classes ────────────────────────────────────────────────
        yield return Scn("regex-char-class-hex")
            .File("hex.txt", "color #a1b2c3 here")
            .File("plain.txt", "no hex value")
            .Regex(@"#[0-9a-f]{6}")
            .ExpectFiles("hex.txt").ExpectMatchText("#a1b2c3").Build();

        yield return Scn("regex-negated-digit-class")
            .File("a.txt", "abc")
            .File("b.txt", "12345")
            .Regex(@"[^0-9]+")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-digit-class")
            .File("a.txt", "abc1")
            .File("b.txt", "abc")
            .Regex(@"\d")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-word-class")
            .File("a.txt", "hello")
            .File("b.txt", "??? ")
            .Regex(@"\w+")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-whitespace-class")
            .File("a.txt", "a b")
            .File("b.txt", "ab")
            .Regex(@"\s")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-non-word")
            .File("a.txt", "a!b")
            .File("b.txt", "abc")
            .Regex(@"\W")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-non-digit")
            .File("a.txt", "abc")
            .File("b.txt", "123")
            .Regex(@"\D")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-dot-any")
            .File("a.txt", "abc")
            .File("b.txt", "ac")
            .Regex("a.c")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-case-sensitive-uppercase-class")
            .File("a.txt", "ABC")
            .File("b.txt", "abc")
            .Regex("[A-Z]{2,}").CaseSensitive()
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-class-vowels")
            .File("a.txt", "queue")
            .File("b.txt", "rhythm")
            .Regex("[aeiou]+")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-class-alnum-cs")
            .File("a.txt", "abc123")
            .File("b.txt", "___")
            .Regex("[a-z0-9]+").CaseSensitive()
            .ExpectFiles("a.txt").Build();

        // ── Alternation / groups ─────────────────────────────────────────────
        yield return Scn("regex-alternation")
            .File("cat.txt", "a cat sat")
            .File("dog.txt", "a dog ran")
            .File("fish.txt", "a fish swam")
            .Regex("cat|dog")
            .ExpectFiles("cat.txt", "dog.txt").Build();

        yield return Scn("regex-alternation-three")
            .File("xa.txt", "x mark")
            .File("yb.txt", "y mark")
            .File("zc.txt", "z mark")
            .File("wd.txt", "w mark")
            .Regex("x|y|z")
            .ExpectFiles("xa.txt", "yb.txt", "zc.txt").Build();

        yield return Scn("regex-group-quantifier")
            .File("a.txt", "abab")
            .File("b.txt", "ba only")
            .Regex("(ab)+")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-alternation-anchored")
            .File("a.txt", "foo start")
            .File("b.txt", "bar start")
            .File("c.txt", "baz foo")
            .Regex("^(foo|bar)")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-group-alternation")
            .File("a.txt", "gray sky")
            .File("b.txt", "grey sky")
            .File("c.txt", "groy sky")
            .Regex("gr(a|e)y")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-optional-group")
            .File("a.txt", "lock here")
            .File("b.txt", "unlock here")
            .File("c.txt", "no match word")
            .Regex("(un)?lock")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-nested-group-optional-s")
            .File("a.txt", "cats")
            .File("b.txt", "dog")
            .File("c.txt", "fish")
            .Regex("(cat|dog)s?")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-alternation-with-quantifier")
            .File("a.txt", "abcd")
            .File("b.txt", "xy")
            .Regex("(ab|cd)+")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-grouped-optional-prefix")
            .File("a.txt", "bar")
            .File("b.txt", "foobar")
            .File("c.txt", "baz")
            .Regex("(foo)?bar")
            .ExpectFiles("a.txt", "b.txt").Build();

        // ── Word boundaries ──────────────────────────────────────────────────
        yield return Scn("regex-word-boundary")
            .File("a.txt", "the cat sat")
            .File("b.txt", "category list")
            .Regex(@"\bcat\b")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-word-boundary-prefix")
            .File("a.txt", "prefix value")
            .File("b.txt", "the apre word")
            .Regex(@"\bpre")
            .ExpectFiles("a.txt").Build();

        // ── Escapes ──────────────────────────────────────────────────────────
        yield return Scn("regex-escaped-dot")
            .File("a.txt", "foo.bar")
            .File("b.txt", "fooXbar")
            .Regex(@"foo\.bar")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-escaped-paren")
            .File("a.txt", "func() call")
            .File("b.txt", "func call")
            .Regex(@"\(\)")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-escaped-plus")
            .File("a.txt", "a+b sum")
            .File("b.txt", "aXb sum")
            .Regex(@"a\+b")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-escaped-bracket")
            .File("a.txt", "arr[x] value")
            .File("b.txt", "arr x value")
            .Regex(@"\[x\]")
            .ExpectFiles("a.txt").Build();

        // ── Case sensitivity ─────────────────────────────────────────────────
        yield return Scn("regex-case-insensitive-default")
            .File("a.txt", "Error here")
            .File("b.txt", "ERROR here")
            .File("c.txt", "no err")
            .Regex("error")
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("regex-case-sensitive-flag")
            .File("a.txt", "error happened")
            .File("b.txt", "ERROR happened")
            .Regex("error").CaseSensitive()
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-anchored-start-word")
            .File("a.txt", "The cat")
            .File("b.txt", "A The cat")
            .Regex("^The")
            .ExpectFiles("a.txt").Build();

        // ── Real-world-ish composite patterns ────────────────────────────────
        yield return Scn("regex-email-like")
            .File("a.txt", "contact me@host today")
            .File("b.txt", "no at sign here")
            .Regex(@"\w+@\w+")
            .ExpectFiles("a.txt").ExpectMatchText("me@host").Build();

        yield return Scn("regex-ip-like")
            .File("a.txt", "ip 10.0.0.1 here")
            .File("b.txt", "ip 10.0 here")
            .Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}")
            .ExpectFiles("a.txt").ExpectMatchText("10.0.0.1").Build();

        yield return Scn("regex-phone")
            .File("a.txt", "call 555-1234 now")
            .File("b.txt", "call 5551234 now")
            .Regex(@"\d{3}-\d{4}")
            .ExpectFiles("a.txt").ExpectMatchText("555-1234").Build();

        yield return Scn("regex-time")
            .File("a.txt", "meet at 09:30 today")
            .File("b.txt", "meet at 9.30 today")
            .Regex(@"\d{2}:\d{2}")
            .ExpectFiles("a.txt").ExpectMatchText("09:30").Build();

        yield return Scn("regex-version")
            .File("a.txt", "v2.5 release")
            .File("b.txt", "version two")
            .Regex(@"v\d+\.\d+")
            .ExpectFiles("a.txt").ExpectMatchText("v2.5").Build();

        yield return Scn("regex-hex8")
            .File("a.txt", "id deadbeef done")
            .File("b.txt", "id zzzzzzzz done")
            .Regex("[0-9a-f]{8}")
            .ExpectFiles("a.txt").ExpectMatchText("deadbeef").Build();

        yield return Scn("regex-currency")
            .File("a.txt", "cost $50 total")
            .File("b.txt", "cost 50 total")
            .Regex(@"\$\d+")
            .ExpectFiles("a.txt").ExpectMatchText("$50").Build();

        yield return Scn("regex-percentage")
            .File("a.txt", "up 20% today")
            .File("b.txt", "up 20 today")
            .Regex(@"\d+%")
            .ExpectFiles("a.txt").ExpectMatchText("20%").Build();

        yield return Scn("regex-hashtag")
            .File("a.txt", "big #win today")
            .File("b.txt", "no tag here")
            .Regex(@"#\w+")
            .ExpectFiles("a.txt").ExpectMatchText("#win").Build();

        yield return Scn("regex-mention")
            .File("a.txt", "hi @bob there")
            .File("b.txt", "hi bob there")
            .Regex(@"@\w+")
            .ExpectFiles("a.txt").ExpectMatchText("@bob").Build();

        yield return Scn("regex-url-like")
            .File("a.txt", "see http://site now")
            .File("b.txt", "see ftp://site now")
            .File("c.txt", "see https://secure now")
            .Regex(@"https?://\w+")
            .ExpectFiles("a.txt", "c.txt").Build();

        yield return Scn("regex-word-then-digit")
            .File("a.txt", "item5 listed")
            .File("b.txt", "item listed")
            .Regex(@"\w+\d")
            .ExpectFiles("a.txt").Build();

        // ── Quantifier behavior / counts ─────────────────────────────────────
        yield return Scn("regex-double-letter")
            .File("a.txt", "baaad")
            .File("b.txt", "bad")
            .Regex("aa+")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-lazy-quantifier")
            .File("a.txt", "aaa")
            .File("b.txt", "bbb")
            .Regex("a+?")
            .ExpectFiles("a.txt").Build();

        yield return Scn("regex-multiline-anchor-count")
            .File("a.txt", "item one\nitem two\nother thing")
            .Regex("^item")
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("regex-count-digit-lines")
            .File("a.txt", "line1\nline2\nlineX")
            .Regex(@"\d+")
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("regex-dotstar-between")
            .File("a.txt", "foo then bar")
            .File("b.txt", "bar then foo")
            .File("c.txt", "foobar")
            .Regex("foo.*bar")
            .ExpectFiles("a.txt", "c.txt").Build();

        yield return Scn("regex-no-match")
            .File("a.txt", "xyz12")
            .File("b.txt", "abc")
            .Regex(@"xyz\d{5}")
            .ExpectNoMatches().Build();
    }
}
