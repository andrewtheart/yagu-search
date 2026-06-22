using System.Collections.Generic;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // Include/exclude filter scenarios: glob-path extension matches, path-segment
    // matches, wildcard globs, and full-path regex filters. Every file contains the
    // query token; the filter alone decides which files survive.
    private static IEnumerable<SearchScenario> FilterScenarios()
    {
        const string N = "needle";

        // ── Include by extension (glob-path) ─────────────────────────────────
        yield return Scn("include-ext-star-cs")
            .File("code.cs", N).File("readme.txt", N)
            .Query(N).Substring().Include("*.cs")
            .ExpectFiles("code.cs").Build();

        yield return Scn("include-ext-bare-cs")
            .File("code.cs", N).File("readme.txt", N)
            .Query(N).Substring().Include("cs")
            .ExpectFiles("code.cs").Build();

        yield return Scn("include-ext-txt-bare")
            .File("a.txt", N).File("b.cs", N)
            .Query(N).Substring().Include("txt")
            .ExpectFiles("a.txt").Build();

        yield return Scn("include-ext-comma-two")
            .File("a.cs", N).File("b.js", N).File("c.md", N)
            .Query(N).Substring().Include("*.cs,*.js")
            .ExpectFiles("a.cs", "b.js").Build();

        yield return Scn("include-ext-two-args")
            .File("a.cs", N).File("b.js", N).File("c.md", N)
            .Query(N).Substring().Include("*.cs", "*.js")
            .ExpectFiles("a.cs", "b.js").Build();

        yield return Scn("include-ext-uppercase")
            .File("code.cs", N).File("readme.txt", N)
            .Query(N).Substring().Include("*.CS")
            .ExpectFiles("code.cs").Build();

        yield return Scn("include-ext-json")
            .File("data.json", N).File("data.xml", N)
            .Query(N).Substring().Include("json")
            .ExpectFiles("data.json").Build();

        yield return Scn("include-ext-no-match")
            .File("a.txt", N).File("b.txt", N)
            .Query(N).Substring().Include("*.md")
            .ExpectNoMatches().Build();

        yield return Scn("include-ext-three")
            .File("x.a", N).File("x.b", N).File("x.c", N).File("x.d", N)
            .Query(N).Substring().Include("*.a,*.b,*.c")
            .ExpectFiles("x.a", "x.b", "x.c").Build();

        yield return Scn("include-ext-tsx-not-ts")
            .File("comp.tsx", N).File("comp.ts", N)
            .Query(N).Substring().Include("*.tsx")
            .ExpectFiles("comp.tsx").Build();

        yield return Scn("include-ext-bare-md")
            .File("readme.md", N).File("readme.txt", N)
            .Query(N).Substring().Include("md")
            .ExpectFiles("readme.md").Build();

        yield return Scn("include-ext-env")
            .File("a.env", N).File("b.txt", N)
            .Query(N).Substring().Include("*.env")
            .ExpectFiles("a.env").Build();

        yield return Scn("include-ext-and-bare-mixed")
            .File("a.cs", N).File("b.js", N).File("c.md", N)
            .Query(N).Substring().Include("*.cs", "js")
            .ExpectFiles("a.cs", "b.js").Build();

        // ── Include by glob path ─────────────────────────────────────────────
        yield return Scn("include-glob-src-cs")
            .File("src/app.cs", N).File("src/app.js", N).File("lib/app.cs", N)
            .Query(N).Substring().Include("src/**/*.cs")
            .ExpectFiles("src/app.cs").Build();

        yield return Scn("include-glob-double-star-cs")
            .File("deep/x.cs", N).File("y.cs", N).File("z.js", N)
            .Query(N).Substring().Include("**/*.cs")
            .ExpectFiles("deep/x.cs", "y.cs").Build();

        yield return Scn("include-glob-question")
            .File("file1.txt", N).File("file10.txt", N).File("file.txt", N)
            .Query(N).Substring().Include("file?.txt")
            .ExpectFiles("file1.txt").Build();

        yield return Scn("include-glob-folder-file")
            .File("logs/a.txt", N).File("logs/sub/b.txt", N).File("other/c.txt", N)
            .Query(N).Substring().Include("logs/*.txt")
            .ExpectFiles("logs/a.txt").Build();

        yield return Scn("include-glob-prefix-star")
            .File("app.txt", N).File("apple.md", N).File("myapp.txt", N)
            .Query(N).Substring().Include("app*")
            .ExpectFiles("app.txt", "apple.md").Build();

        yield return Scn("include-glob-data-question-json")
            .File("data1.json", N).File("data.json", N).File("data12.json", N)
            .Query(N).Substring().Include("data?.json")
            .ExpectFiles("data1.json").Build();

        // ── Include by full-path regex ───────────────────────────────────────
        yield return Scn("include-regex-ts")
            .File("src/app.ts", N).File("src/app.js", N)
            .Query(N).Substring().IncludeRegex(@"\.ts$")
            .ExpectFiles("src/app.ts").Build();

        yield return Scn("include-regex-ext-alternation")
            .File("a.cs", N).File("b.js", N).File("c.md", N)
            .Query(N).Substring().IncludeRegex(@"\.(cs|js)$")
            .ExpectFiles("a.cs", "b.js").Build();

        yield return Scn("include-regex-digit-name")
            .File("file1.txt", N).File("fileA.txt", N)
            .Query(N).Substring().IncludeRegex(@"\d+\.txt$")
            .ExpectFiles("file1.txt").Build();

        yield return Scn("include-regex-folder")
            .File("src/a.txt", N).File("lib/b.txt", N)
            .Query(N).Substring().IncludeRegex(@"/src/")
            .ExpectFiles("src/a.txt").Build();

        yield return Scn("include-regex-folder-case-insensitive")
            .File("src/a.txt", N).File("lib/b.txt", N)
            .Query(N).Substring().IncludeRegex(@"/SRC/")
            .ExpectFiles("src/a.txt").Build();

        yield return Scn("include-regex-anchored-leaf")
            .File("src/main.cs", N).File("src/other.cs", N)
            .Query(N).Substring().IncludeRegex(@"/main\.[a-z]+$")
            .ExpectFiles("src/main.cs").Build();

        // ── Exclude by extension ─────────────────────────────────────────────
        yield return Scn("exclude-ext-bare-log")
            .File("data.txt", N).File("data.log", N)
            .Query(N).Substring().Exclude("log")
            .ExpectFiles("data.txt").Build();

        yield return Scn("exclude-ext-glob-tmp")
            .File("a.txt", N).File("b.tmp", N)
            .Query(N).Substring().Exclude("*.tmp")
            .ExpectFiles("a.txt").Build();

        yield return Scn("exclude-ext-comma-two")
            .File("a.txt", N).File("b.log", N).File("c.tmp", N)
            .Query(N).Substring().Exclude("*.log,*.tmp")
            .ExpectFiles("a.txt").Build();

        yield return Scn("exclude-ext-keeps-multiple")
            .File("a.cs", N).File("b.bak", N).File("c.js", N)
            .Query(N).Substring().Exclude("bak")
            .ExpectFiles("a.cs", "c.js").Build();

        yield return Scn("exclude-ext-min-txt")
            .File("a.txt", N).File("a.min.txt", N)
            .Query(N).Substring().Exclude("*.min.txt")
            .ExpectFiles("a.txt").Build();

        // ── Exclude by path segment ──────────────────────────────────────────
        yield return Scn("exclude-segment-node-modules")
            .File("src/code.js", N).File("node_modules/pkg/index.js", N)
            .Query(N).Substring().Exclude("node_modules")
            .ExpectFiles("src/code.js").Build();

        yield return Scn("exclude-segment-vendor")
            .File("app/main.txt", N).File("vendor/lib.txt", N)
            .Query(N).Substring().Exclude("vendor")
            .ExpectFiles("app/main.txt").Build();

        yield return Scn("exclude-segment-coverage")
            .File("src/a.txt", N).File("coverage/report.txt", N)
            .Query(N).Substring().Exclude("coverage")
            .ExpectFiles("src/a.txt").Build();

        yield return Scn("exclude-two-segments")
            .File("keep/a.txt", N).File("node_modules/b.txt", N).File("coverage/c.txt", N)
            .Query(N).Substring().Exclude("node_modules,coverage")
            .ExpectFiles("keep/a.txt").Build();

        yield return Scn("exclude-segment-packages")
            .File("root.txt", N).File("packages/p.txt", N)
            .Query(N).Substring().Exclude("packages")
            .ExpectFiles("root.txt").Build();

        yield return Scn("exclude-segment-build-out")
            .File("src/a.txt", N).File("build_out/b.txt", N)
            .Query(N).Substring().Exclude("build_out")
            .ExpectFiles("src/a.txt").Build();

        yield return Scn("exclude-glob-bin-double-star")
            .File("src/a.txt", N).File("src/bin/b.txt", N)
            .Query(N).Substring().Exclude("**/bin/**")
            .ExpectFiles("src/a.txt").Build();

        // ── Exclude by full-path regex ───────────────────────────────────────
        yield return Scn("exclude-regex-tmp-folder")
            .File("keep/data.txt", N).File("tmp/data.txt", N)
            .Query(N).Substring().ExcludeRegex(@"[\\/]tmp[\\/]")
            .ExpectFiles("keep/data.txt").Build();

        yield return Scn("exclude-regex-bak")
            .File("a.txt", N).File("b.bak", N)
            .Query(N).Substring().ExcludeRegex(@"\.bak$")
            .ExpectFiles("a.txt").Build();

        yield return Scn("exclude-regex-test-files")
            .File("app.txt", N).File("app.test.txt", N)
            .Query(N).Substring().ExcludeRegex(@"\.test\.txt$")
            .ExpectFiles("app.txt").Build();

        yield return Scn("exclude-regex-ext-alternation")
            .File("a.txt", N).File("b.log", N).File("c.tmp", N)
            .Query(N).Substring().ExcludeRegex(@"\.(log|tmp)$")
            .ExpectFiles("a.txt").Build();

        yield return Scn("exclude-regex-numeric-dir")
            .File("api/v1/a.txt", N).File("api/stable/b.txt", N)
            .Query(N).Substring().ExcludeRegex(@"/v\d+/")
            .ExpectFiles("api/stable/b.txt").Build();

        yield return Scn("exclude-regex-dist-dir")
            .File("app/dist/bundle.txt", N).File("app/src/main.txt", N)
            .Query(N).Substring().ExcludeRegex(@"/dist/")
            .ExpectFiles("app/src/main.txt").Build();

        // ── Include + exclude combinations ───────────────────────────────────
        yield return Scn("include-ext-exclude-segment")
            .File("src/a.cs", N).File("node_modules/b.cs", N).File("src/c.txt", N)
            .Query(N).Substring().Include("*.cs").Exclude("node_modules")
            .ExpectFiles("src/a.cs").Build();

        yield return Scn("include-ext-exclude-ext")
            .File("a.txt", N).File("a.min.txt", N)
            .Query(N).Substring().Include("txt").Exclude("*.min.txt")
            .ExpectFiles("a.txt").Build();

        yield return Scn("include-glob-exclude-ext")
            .File("src/a.txt", N).File("src/b.skip.txt", N).File("lib/c.txt", N)
            .Query(N).Substring().Include("src/**/*.txt").Exclude("*.skip.txt")
            .ExpectFiles("src/a.txt").Build();

        yield return Scn("exclude-wins-over-include")
            .File("a.cs", N).File("b.cs", N)
            .Query(N).Substring().Include("*.cs").Exclude("*.cs")
            .ExpectNoMatches().Build();

        // ── Filters gate content search ──────────────────────────────────────
        yield return Scn("include-filters-before-content")
            .File("a.cs", N).File("b.txt", N)
            .Query(N).Substring().Include("*.cs")
            .ExpectFiles("a.cs").Build();

        yield return Scn("exclude-filters-before-content")
            .File("a.txt", N).File("b.log", N)
            .Query(N).Substring().Exclude("*.log")
            .ExpectFiles("a.txt").Build();

        yield return Scn("include-multiple-content-subset")
            .File("a.cs", N).File("b.js", N).File("c.md", N).File("d.txt", N)
            .Query(N).Substring().Include("*.cs", "*.js")
            .ExpectFiles("a.cs", "b.js").Build();
    }
}
