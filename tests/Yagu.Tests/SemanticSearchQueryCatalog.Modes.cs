using System.Collections.Generic;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // Search-mode scenarios across Content, FileNames, Both, and FileNameThenContent.
    // Filename matching is evaluated against the leaf name (Path.GetFileName) using the
    // same query semantics (literal/regex/case). Both emits a filename row plus content
    // rows; FileNameThenContent requires a name match but emits only content rows.
    private static IEnumerable<SearchScenario> ModeScenarios()
    {
        // ── Content mode ─────────────────────────────────────────────────────
        yield return Scn("mode-content-only-ignores-filename")
            .File("needle-name.txt", "nothing relevant")
            .File("other.txt", "has needle inside")
            .Query("needle").Substring().Mode(SearchMode.Content)
            .ExpectFiles("other.txt").Build();

        yield return Scn("mode-content-default-ignores-filename")
            .File("report.txt", "quiet data")
            .File("x.txt", "the report value")
            .Query("report").Substring().Mode(SearchMode.Content)
            .ExpectFiles("x.txt").Build();

        yield return Scn("mode-content-multiple-files")
            .File("a.txt", "alpha hit beta")
            .File("b.txt", "no relevant text")
            .File("c.txt", "hit again")
            .Query("hit").Substring().Mode(SearchMode.Content)
            .ExpectFiles("a.txt", "c.txt").Build();

        yield return Scn("mode-content-counts-lines")
            .File("a.txt", "hit\nhit\nmiss")
            .Query("hit").Substring().Mode(SearchMode.Content)
            .ExpectMatchesInFile("a.txt", 2).Build();

        // ── FileNames mode ───────────────────────────────────────────────────
        yield return Scn("mode-filenames-only")
            .File("needle-name.txt", "nothing relevant")
            .File("other.txt", "has needle inside")
            .Query("needle").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("needle-name.txt").Build();

        yield return Scn("mode-filenames-substring-leaf")
            .File("alpha-config.txt", "x")
            .File("beta.txt", "x")
            .Query("config").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("alpha-config.txt").Build();

        yield return Scn("mode-filenames-token-in-name")
            .File("data1.log", "x")
            .File("data2.log", "x")
            .File("other.log", "x")
            .Query("data").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("data1.log", "data2.log").Build();

        yield return Scn("mode-filenames-empty-content-name-match")
            .File("match-me.txt", "")
            .File("ignore.txt", "")
            .Query("match").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("match-me.txt").Build();

        yield return Scn("mode-filenames-case-insensitive")
            .File("README.md", "x")
            .File("notes.md", "x")
            .Query("readme").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("README.md").Build();

        yield return Scn("mode-filenames-case-sensitive")
            .File("README.md", "x")
            .File("readme_lower.md", "x")
            .Query("readme").Substring().CaseSensitive().Mode(SearchMode.FileNames)
            .ExpectFiles("readme_lower.md").Build();

        yield return Scn("mode-filenames-whole-word")
            .File("log.txt", "x")
            .File("catalog.txt", "x")
            .Query("log").WholeWord().Mode(SearchMode.FileNames)
            .ExpectFiles("log.txt").Build();

        yield return Scn("mode-filenames-regex")
            .File("v1.txt", "x")
            .File("v2.txt", "x")
            .File("main.txt", "x")
            .Regex(@"v\d").Mode(SearchMode.FileNames)
            .ExpectFiles("v1.txt", "v2.txt").Build();

        yield return Scn("mode-filenames-count-one-per-file")
            .File("needle-needle.txt", "x")
            .Query("needle").Substring().Mode(SearchMode.FileNames)
            .ExpectMatchesInFile("needle-needle.txt", 1).ExpectTotal(1).Build();

        yield return Scn("mode-filenames-multiterm")
            .File("foo.txt", "x")
            .File("bar.txt", "x")
            .File("baz.txt", "x")
            .Query("foo bar").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("bar.txt", "foo.txt").Build();

        yield return Scn("mode-filenames-no-match")
            .File("alpha.txt", "x")
            .File("beta.txt", "x")
            .Query("zzz").Substring().Mode(SearchMode.FileNames)
            .ExpectNoMatches().Build();

        yield return Scn("mode-filenames-dir-token-ignored")
            .File("sub-needle/file.txt", "x")
            .Query("needle").Substring().Mode(SearchMode.FileNames)
            .ExpectNoMatches().Build();

        yield return Scn("mode-filenames-total-equals-files")
            .File("rep-a.txt", "x")
            .File("rep-b.txt", "x")
            .File("rep-c.txt", "x")
            .Query("rep").Substring().Mode(SearchMode.FileNames)
            .ExpectTotal(3).Build();

        yield return Scn("mode-filenames-substring-version")
            .File("config.yaml", "x")
            .File("config.json", "x")
            .File("readme.md", "x")
            .Query("config").Substring().Mode(SearchMode.FileNames)
            .ExpectFiles("config.json", "config.yaml").Build();

        // ── Both mode ────────────────────────────────────────────────────────
        yield return Scn("mode-both-name-and-content")
            .File("needle-name.txt", "nothing relevant")
            .File("other.txt", "has needle inside")
            .Query("needle").Substring().Mode(SearchMode.Both)
            .ExpectFiles("needle-name.txt", "other.txt").ExpectTotal(2).Build();

        yield return Scn("mode-both-name-only")
            .File("tagged.txt", "nothing relevant")
            .Query("tag").Substring().Mode(SearchMode.Both)
            .ExpectFiles("tagged.txt").ExpectTotal(1).Build();

        yield return Scn("mode-both-content-only")
            .File("plain.txt", "a tag here")
            .Query("tag").Substring().Mode(SearchMode.Both)
            .ExpectFiles("plain.txt").ExpectTotal(1).Build();

        yield return Scn("mode-both-name-plus-content-rows")
            .File("tag-tag.txt", "tag\ntag")
            .Query("tag").Substring().Mode(SearchMode.Both)
            .ExpectMatchesInFile("tag-tag.txt", 3).ExpectTotal(3).Build();

        yield return Scn("mode-both-distinct-files")
            .File("x.txt", "find me")
            .File("find.txt", "other text")
            .Query("find").Substring().Mode(SearchMode.Both)
            .ExpectFiles("find.txt", "x.txt").ExpectTotal(2).Build();

        yield return Scn("mode-both-regex")
            .File("err1.txt", "all good")
            .File("ok.txt", "err5 here")
            .Regex(@"err\d").Mode(SearchMode.Both)
            .ExpectFiles("err1.txt", "ok.txt").ExpectTotal(2).Build();

        yield return Scn("mode-both-case-sensitive")
            .File("ERR.txt", "value")
            .File("low.txt", "err here")
            .Query("err").Substring().CaseSensitive().Mode(SearchMode.Both)
            .ExpectFiles("low.txt").Build();

        yield return Scn("mode-both-multiterm")
            .File("alpha.txt", "nothing")
            .File("beta-file.txt", "x")
            .File("gamma.txt", "alpha here")
            .Query("alpha beta").Substring().Mode(SearchMode.Both)
            .ExpectFiles("alpha.txt", "beta-file.txt", "gamma.txt").Build();

        // ── FileNameThenContent mode ─────────────────────────────────────────
        yield return Scn("mode-filename-then-content")
            .File("target-with.txt", "before target after")
            .File("target-without.txt", "quiet content")
            .File("other.txt", "target appears but name differs")
            .Query("target").Substring().Mode(SearchMode.FileNameThenContent)
            .ExpectFiles("target-with.txt").Build();

        yield return Scn("mode-ftc-requires-name")
            .File("keep-me.txt", "keep value")
            .File("other.txt", "keep value")
            .Query("keep").Substring().Mode(SearchMode.FileNameThenContent)
            .ExpectFiles("keep-me.txt").ExpectTotal(1).Build();

        yield return Scn("mode-ftc-name-match-no-content")
            .File("named.txt", "nothing here")
            .Query("name").Substring().Mode(SearchMode.FileNameThenContent)
            .ExpectNoMatches().Build();

        yield return Scn("mode-ftc-content-rows-only")
            .File("data-data.txt", "data\ndata")
            .Query("data").Substring().Mode(SearchMode.FileNameThenContent)
            .ExpectMatchesInFile("data-data.txt", 2).ExpectTotal(2).Build();

        yield return Scn("mode-ftc-regex")
            .File("log1.txt", "see log1 entry")
            .File("log2.txt", "no match line")
            .Regex(@"log\d").Mode(SearchMode.FileNameThenContent)
            .ExpectFiles("log1.txt").ExpectTotal(1).Build();

        yield return Scn("mode-ftc-multiterm")
            .File("alpha-name.txt", "has beta here")
            .File("other.txt", "alpha beta")
            .Query("alpha beta").Substring().Mode(SearchMode.FileNameThenContent)
            .ExpectFiles("alpha-name.txt").ExpectTotal(1).Build();

        yield return Scn("mode-ftc-no-name-match")
            .File("a.txt", "xyz")
            .File("b.txt", "xyz")
            .Query("needle").Substring().Mode(SearchMode.FileNameThenContent)
            .ExpectNoMatches().Build();

        yield return Scn("mode-ftc-case-sensitive")
            .File("Data.txt", "data here")
            .File("data_low.txt", "data here")
            .Query("data").Substring().CaseSensitive().Mode(SearchMode.FileNameThenContent)
            .ExpectFiles("data_low.txt").ExpectTotal(1).Build();

        yield return Scn("mode-both-content-counts")
            .File("scan.txt", "hit\nhit")
            .Query("hit").Substring().Mode(SearchMode.Both)
            .ExpectMatchesInFile("scan.txt", 2).ExpectTotal(2).Build();
    }
}
