using System.Collections.Generic;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // Literal / substring / whole-word / case-sensitivity / multi-term query scenarios.
    private static IEnumerable<SearchScenario> LiteralScenarios()
    {
        // ── Basic substring presence ─────────────────────────────────────────
        yield return Scn("literal-substring-finds-two-files")
            .File("a.txt", "hello world")
            .File("b.txt", "hello there")
            .File("c.txt", "goodbye")
            .Query("hello").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("literal-substring-single-file")
            .File("a.txt", "the quick brown fox")
            .File("b.txt", "lazy dog sleeps")
            .Query("brown").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("literal-substring-all-three")
            .File("a.txt", "alpha token here")
            .File("b.txt", "token at start")
            .File("c.txt", "ends with token")
            .Query("token").Substring()
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("literal-substring-partial-word")
            .File("a.txt", "hello")
            .File("b.txt", "yellow")
            .Query("ell").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("literal-substring-prefix")
            .File("a.txt", "configuration")
            .File("b.txt", "configure")
            .File("c.txt", "deconfigured")
            .Query("config").Substring()
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("literal-substring-suffix")
            .File("a.txt", "running")
            .File("b.txt", "swimming")
            .File("c.txt", "static")
            .Query("ing").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("literal-substring-none-present")
            .File("a.txt", "alpha")
            .File("b.txt", "beta")
            .Query("gamma").Substring()
            .ExpectNoMatches().Build();

        yield return Scn("literal-substring-embedded")
            .File("a.txt", "abcdNEEDLEefgh")
            .Query("NEEDLE").Substring()
            .ExpectFiles("a.txt").ExpectMatchText("NEEDLE").Build();

        // ── Case sensitivity ─────────────────────────────────────────────────
        yield return Scn("literal-case-insensitive-three")
            .File("upper.txt", "Hello")
            .File("lower.txt", "hello")
            .File("shout.txt", "HELLO")
            .Query("hello").Substring()
            .ExpectFiles("lower.txt", "shout.txt", "upper.txt").Build();

        yield return Scn("literal-case-sensitive-lower")
            .File("upper.txt", "Hello")
            .File("lower.txt", "hello")
            .File("shout.txt", "HELLO")
            .Query("hello").Substring().CaseSensitive()
            .ExpectFiles("lower.txt").Build();

        yield return Scn("literal-case-sensitive-upper")
            .File("upper.txt", "Hello")
            .File("lower.txt", "hello")
            .File("shout.txt", "HELLO")
            .Query("HELLO").Substring().CaseSensitive()
            .ExpectFiles("shout.txt").Build();

        yield return Scn("literal-case-sensitive-titlecase")
            .File("title.txt", "Error: disk full")
            .File("lower.txt", "error: disk full")
            .Query("Error").Substring().CaseSensitive()
            .ExpectFiles("title.txt").Build();

        yield return Scn("literal-case-sensitive-mixed-token")
            .File("a.txt", "MyClassName")
            .File("b.txt", "myclassname")
            .Query("MyClassName").Substring().CaseSensitive()
            .ExpectFiles("a.txt").Build();

        yield return Scn("literal-case-insensitive-mixed-token")
            .File("a.txt", "MyClassName")
            .File("b.txt", "myclassname")
            .Query("myclassname").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        // ── Whole-word vs substring ──────────────────────────────────────────
        yield return Scn("wholeword-excludes-partial")
            .File("a.txt", "async pipeline")
            .File("b.txt", "asynchronously runs")
            .Query("async").WholeWord()
            .ExpectFiles("a.txt").Build();

        yield return Scn("wholeword-matches-boundaries")
            .File("a.txt", "the cat sat")
            .File("b.txt", "category list")
            .File("c.txt", "scatter plot")
            .Query("cat").WholeWord()
            .ExpectFiles("a.txt").Build();

        yield return Scn("substring-matches-inside-words")
            .File("a.txt", "the cat sat")
            .File("b.txt", "category list")
            .File("c.txt", "scatter plot")
            .Query("cat").Substring()
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("wholeword-punctuation-adjacent")
            .File("a.txt", "value=42; end")
            .File("b.txt", "evaluation")
            .Query("value").WholeWord()
            .ExpectFiles("a.txt").Build();

        yield return Scn("wholeword-token-with-underscore")
            .File("a.txt", "max_count = 5")
            .File("b.txt", "max_counter = 9")
            .Query("max_count").WholeWord()
            .ExpectFiles("a.txt").Build();

        yield return Scn("wholeword-digits-boundary")
            .File("a.txt", "port 8080 open")
            .File("b.txt", "id80801 here")
            .Query("8080").WholeWord()
            .ExpectFiles("a.txt").Build();

        // ── Multi-term substring (OR) ────────────────────────────────────────
        yield return Scn("multiterm-or-two")
            .File("foo.txt", "foo here")
            .File("bar.txt", "bar here")
            .File("both.txt", "foo and bar")
            .File("none.txt", "baz here")
            .Query("foo bar").Substring()
            .ExpectFiles("bar.txt", "both.txt", "foo.txt").Build();

        yield return Scn("multiterm-or-three")
            .File("a.txt", "red apple")
            .File("b.txt", "green grass")
            .File("c.txt", "blue sky")
            .File("d.txt", "white cloud")
            .Query("red green blue").Substring()
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("multiterm-or-overlap-counts")
            .File("a.txt", "cat\ndog\nbird")
            .Query("cat dog").Substring()
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("multiterm-or-case-sensitive")
            .File("a.txt", "Cat and Dog")
            .File("b.txt", "cat and dog")
            .Query("Cat Dog").Substring().CaseSensitive()
            .ExpectFiles("a.txt").Build();

        // ── Phrase whole-word (query with spaces) ───────────────────────────
        yield return Scn("phrase-wholeword-exact")
            .File("a.txt", "the value is test 123 here")
            .File("b.txt", "test then later 123")
            .Query("test 123").WholeWord()
            .ExpectFiles("a.txt").ExpectMatchText("test 123").Build();

        yield return Scn("phrase-wholeword-not-split")
            .File("a.txt", "hello world program")
            .File("b.txt", "world hello reversed")
            .Query("hello world").WholeWord()
            .ExpectFiles("a.txt").Build();

        // ── Punctuation / symbol queries (substring) ────────────────────────
        yield return Scn("symbol-plus-equals")
            .File("a.txt", "total += amount")
            .File("b.txt", "total = amount")
            .Query("+=").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("symbol-arrow")
            .File("a.txt", "items.map(x => x.id)")
            .File("b.txt", "items.map(function)")
            .Query("=>").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("symbol-namespace-colons")
            .File("a.txt", "std::vector<int>")
            .File("b.txt", "std.vector here")
            .Query("::").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("symbol-currency")
            .File("a.txt", "price is $100 today")
            .File("b.txt", "price is 100 today")
            .Query("$100").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("symbol-hashtag")
            .File("a.txt", "#TODO fix this")
            .File("b.txt", "TODO fix this")
            .Query("#TODO").Substring()
            .ExpectFiles("a.txt").Build();

        // ── Per-file match counts (matching lines) ──────────────────────────
        yield return Scn("count-three-matching-lines")
            .File("a.txt", "needle\nfiller\nneedle\nfiller\nneedle")
            .Query("needle").Substring()
            .ExpectMatchesInFile("a.txt", 3).Build();

        yield return Scn("count-one-of-many-lines")
            .File("a.txt", "alpha\nbeta\ngamma needle\ndelta")
            .Query("needle").Substring()
            .ExpectMatchesInFile("a.txt", 1).Build();

        yield return Scn("count-two-files-total")
            .File("a.txt", "needle\nneedle")
            .File("b.txt", "needle")
            .Query("needle").Substring()
            .ExpectTotal(3).Build();

        yield return Scn("count-zero-when-absent")
            .File("a.txt", "alpha\nbeta\ngamma")
            .Query("omega").Substring()
            .ExpectTotal(0).Build();

        // ── Numeric tokens ───────────────────────────────────────────────────
        yield return Scn("numeric-token-substring")
            .File("a.txt", "version 2024 build")
            .File("b.txt", "year 2023 only")
            .Query("2024").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("numeric-token-whole-word")
            .File("a.txt", "code 42 final")
            .File("b.txt", "code 424 final")
            .Query("42").WholeWord()
            .ExpectFiles("a.txt").Build();

        yield return Scn("numeric-decimal")
            .File("a.txt", "pi is 3.14 approx")
            .File("b.txt", "pi is 314 approx")
            .Query("3.14").Substring()
            .ExpectFiles("a.txt").Build();

        // ── Unicode / non-ASCII content (file-match assertions) ─────────────
        yield return Scn("unicode-accented")
            .File("a.txt", "café au lait")
            .File("b.txt", "cafe au lait")
            .Query("café").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("unicode-cjk")
            .File("a.txt", "検索 results")
            .File("b.txt", "search results")
            .Query("検索").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("unicode-cyrillic")
            .File("a.txt", "привет world")
            .File("b.txt", "hello world")
            .Query("привет").Substring()
            .ExpectFiles("a.txt").Build();

        // ── Whitespace handling ──────────────────────────────────────────────
        yield return Scn("tab-separated-token")
            .File("a.txt", "name\tvalue\tdesc")
            .File("b.txt", "name value desc")
            .Query("value").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("leading-trailing-query-trimmed-wholeword")
            .File("a.txt", "the term here")
            .Query("  term  ").WholeWord()
            .ExpectFiles("a.txt").Build();

        // ── Nested directories ───────────────────────────────────────────────
        yield return Scn("nested-dirs-found")
            .File("a/b/c/deep.txt", "needle")
            .File("shallow.txt", "needle")
            .Query("needle").Substring()
            .ExpectFiles("a/b/c/deep.txt", "shallow.txt").Build();

        yield return Scn("nested-dirs-distinct-tokens")
            .File("src/main.txt", "compile target")
            .File("src/util/helper.txt", "compile helper")
            .File("docs/readme.txt", "documentation only")
            .Query("compile").Substring()
            .ExpectFiles("src/main.txt", "src/util/helper.txt").Build();

        // ── Empty / trivial queries ──────────────────────────────────────────
        yield return Scn("empty-query-no-results")
            .File("a.txt", "anything")
            .Query("").Substring()
            .ExpectNoMatches().Build();

        yield return Scn("whitespace-only-query-no-results")
            .File("a.txt", "anything")
            .Query("   ").Substring()
            .ExpectNoMatches().Build();

        // ── Matched-text precision ───────────────────────────────────────────
        yield return Scn("matched-text-substring-token")
            .File("a.txt", "prefix-MIDDLE-suffix")
            .Query("MIDDLE").Substring()
            .ExpectMatchText("MIDDLE").Build();

        yield return Scn("matched-text-wholeword-token")
            .File("a.txt", "a wholeword token b")
            .Query("wholeword").WholeWord()
            .ExpectMatchText("wholeword").Build();

        yield return Scn("matched-text-case-preserved")
            .File("a.txt", "the Function call")
            .Query("function").Substring()
            .ExpectMatchText("Function").Build();

        // ── Repeated / overlapping tokens ────────────────────────────────────
        yield return Scn("repeated-token-distinct-lines")
            .File("a.txt", "log\nlog\nlog\nlog\nlog")
            .Query("log").Substring()
            .ExpectMatchesInFile("a.txt", 5).Build();

        yield return Scn("token-at-line-edges")
            .File("a.txt", "edge middle text\nmore text edge")
            .Query("edge").Substring()
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("longer-of-two-overlapping-terms")
            .File("a.txt", "internationalization")
            .Query("international nation").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("token-only-in-deep-file")
            .File("top.txt", "surface content")
            .File("a/mid.txt", "middle content")
            .File("a/b/leaf.txt", "buried treasure")
            .Query("treasure").Substring()
            .ExpectFiles("a/b/leaf.txt").Build();

        yield return Scn("mixed-extensions-content-token")
            .File("a.cs", "shared marker")
            .File("b.js", "shared marker")
            .File("c.md", "shared marker")
            .Query("marker").Substring()
            .ExpectFiles("a.cs", "b.js", "c.md").Build();

        yield return Scn("literal-dot-is-literal-not-regex")
            .File("a.txt", "file.name here")
            .File("b.txt", "fileXname here")
            .Query("file.name").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("literal-star-is-literal-not-regex")
            .File("a.txt", "value a*b done")
            .File("b.txt", "value aXb done")
            .Query("a*b").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("literal-parens-are-literal")
            .File("a.txt", "call func() now")
            .File("b.txt", "call func now")
            .Query("func()").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("literal-bracket-is-literal")
            .File("a.txt", "arr[0] = 1")
            .File("b.txt", "arr 0 = 1")
            .Query("arr[0]").Substring()
            .ExpectFiles("a.txt").Build();
    }
}
