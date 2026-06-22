using System.Collections.Generic;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // Result-limit scenarios: MaxMatchesPerFile (a deterministic per-file cap),
    // MaxResults (a soft streaming threshold — only asserted when set safely above
    // the actual match count so it never clips), and MaxSearchDepth (Managed walker:
    // root files are depth 0; depth N includes files up to N directory levels deep).
    private static IEnumerable<SearchScenario> LimitScenarios()
    {
        // ── MaxMatchesPerFile (deterministic cap) ────────────────────────────
        yield return Scn("max-matches-per-file-caps-rows")
            .File("a.txt", "needle\nneedle\nneedle\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("max-matches-per-file-one")
            .File("a.txt", "needle\nneedle\nneedle\nneedle\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(1)
            .ExpectMatchesInFile("a.txt", 1).Build();

        yield return Scn("max-matches-per-file-three")
            .File("a.txt", "needle\nneedle\nneedle\nneedle\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(3)
            .ExpectMatchesInFile("a.txt", 3).Build();

        yield return Scn("max-matches-per-file-above-count")
            .File("a.txt", "needle\nneedle\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(10)
            .ExpectMatchesInFile("a.txt", 3).Build();

        yield return Scn("max-matches-per-file-two-files")
            .File("a.txt", "needle\nneedle\nneedle\nneedle\nneedle")
            .File("b.txt", "needle\nneedle\nneedle\nneedle\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).ExpectMatchesInFile("b.txt", 2).ExpectTotal(4).Build();

        yield return Scn("max-matches-per-file-exact-equal")
            .File("a.txt", "needle\nneedle\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(3)
            .ExpectMatchesInFile("a.txt", 3).Build();

        yield return Scn("max-matches-per-file-with-regex")
            .File("a.txt", "n1\nn2\nn3\nn4")
            .Regex(@"\d").MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("max-matches-per-file-multiterm")
            .File("a.txt", "cat\ndog\ncat\ndog")
            .Query("cat dog").Substring().MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("max-matches-among-mixed-lines")
            .File("a.txt", "needle\nmiss\nneedle\nmiss\nneedle")
            .Query("needle").Substring().MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("max-results-and-maxmatches-combo")
            .File("a.txt", "needle\nneedle\nneedle\nneedle\nneedle")
            .Query("needle").Substring().MaxResults(100).MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).Build();

        // ── MaxResults (non-clipping assertions only) ────────────────────────
        yield return Scn("max-results-above-count-returns-all")
            .File("a.txt", "needle")
            .File("b.txt", "needle")
            .File("c.txt", "needle")
            .Query("needle").Substring().MaxResults(100)
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("max-results-large-returns-all")
            .File("a.txt", "needle").File("b.txt", "needle").File("c.txt", "needle")
            .File("d.txt", "needle").File("e.txt", "needle")
            .Query("needle").Substring().MaxResults(1000)
            .ExpectFiles("a.txt", "b.txt", "c.txt", "d.txt", "e.txt").Build();

        yield return Scn("max-results-unlimited-zero")
            .File("a.txt", "needle").File("b.txt", "needle")
            .File("c.txt", "needle").File("d.txt", "needle")
            .Query("needle").Substring().MaxResults(0)
            .ExpectFiles("a.txt", "b.txt", "c.txt", "d.txt").Build();

        yield return Scn("max-results-high-with-many-lines")
            .File("a.txt", "needle\nneedle\nneedle\nneedle\nneedle\nneedle\nneedle\nneedle\nneedle\nneedle")
            .Query("needle").Substring().MaxResults(100)
            .ExpectMatchesInFile("a.txt", 10).ExpectTotal(10).Build();

        yield return Scn("max-results-above-total-multi-file")
            .File("a.txt", "needle\nneedle")
            .File("b.txt", "needle\nneedle")
            .File("c.txt", "needle\nneedle")
            .Query("needle").Substring().MaxResults(50)
            .ExpectTotal(6).Build();

        // ── MaxSearchDepth (Managed walker) ──────────────────────────────────
        yield return Scn("max-depth-1-includes-first-level")
            .File("top.txt", "needle")
            .File("a/deep.txt", "needle")
            .File("a/b/deeper.txt", "needle")
            .Query("needle").Substring().MaxDepth(1)
            .ExpectFiles("a/deep.txt", "top.txt").Build();

        yield return Scn("max-depth-2-includes-second-level")
            .File("top.txt", "needle")
            .File("a/l1.txt", "needle")
            .File("a/b/l2.txt", "needle")
            .File("a/b/c/l3.txt", "needle")
            .Query("needle").Substring().MaxDepth(2)
            .ExpectFiles("a/b/l2.txt", "a/l1.txt", "top.txt").Build();

        yield return Scn("max-depth-1-excludes-deeper-contains")
            .File("top.txt", "needle")
            .File("a/deep.txt", "needle")
            .File("a/b/deeper.txt", "needle")
            .Query("needle").Substring().MaxDepth(1)
            .ExpectContains("top.txt").ExpectExcludes("a/b/deeper.txt").Build();

        yield return Scn("max-depth-unlimited-finds-all")
            .File("top.txt", "needle")
            .File("a/b/c/d/deep.txt", "needle")
            .Query("needle").Substring().MaxDepth(0)
            .ExpectFiles("a/b/c/d/deep.txt", "top.txt").Build();

        yield return Scn("max-depth-2-flat-tree-all")
            .File("top.txt", "needle")
            .File("a/x.txt", "needle")
            .Query("needle").Substring().MaxDepth(2)
            .ExpectFiles("a/x.txt", "top.txt").Build();
    }
}
