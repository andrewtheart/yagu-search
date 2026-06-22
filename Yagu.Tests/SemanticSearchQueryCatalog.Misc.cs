using System.Collections.Generic;
using System.Text;
using Yagu.Models;
using static Yagu.Tests.SearchScenarioRunner;

namespace Yagu.Tests;

public static partial class SemanticSearchQueryCatalog
{
    // Cross-cutting scenarios: binary handling, hidden files, skip-extensions,
    // multi-term OR queries, capability combinations, and content edge cases.
    private static IEnumerable<SearchScenario> MiscScenarios()
    {
        // ── Binary handling ─────────────────────────────────────────────────
        // A file containing a NUL byte is detected as binary and skipped by
        // default; opt-in with SearchBinary() to scan it as text.
        yield return Scn("binary-nul-skipped-by-default")
            .File("text.txt", "needle")
            .BinaryFile("data.dat", NullByteText("needle"))
            .Query("needle").Substring()
            .ExpectFiles("text.txt").Build();

        yield return Scn("binary-nul-searched-with-search-binary")
            .File("text.txt", "needle")
            .BinaryFile("data.dat", NullByteText("needle"))
            .Query("needle").Substring().SearchBinary()
            .ExpectContains("text.txt", "data.dat").Build();

        yield return Scn("binary-png-skipped-by-default")
            .File("text.txt", "needle")
            .BinaryFile("image.png", Combine(PngHeader, Encoding.ASCII.GetBytes("needle")))
            .Query("needle").Substring()
            .ExpectFiles("text.txt").Build();

        yield return Scn("binary-nul-only-skipped-empty")
            .BinaryFile("only.dat", NullByteText("needle"))
            .Query("needle").Substring()
            .ExpectNoMatches().Build();

        yield return Scn("binary-two-nul-searched-with-search-binary")
            .BinaryFile("a.dat", NullByteText("needle"))
            .BinaryFile("b.dat", NullByteText("needle"))
            .Query("needle").Substring().SearchBinary()
            .ExpectContains("a.dat", "b.dat").Build();

        yield return Scn("binary-png-and-nul-default-skips-both")
            .File("text.txt", "needle")
            .BinaryFile("image.png", Combine(PngHeader, Encoding.ASCII.GetBytes("needle")))
            .BinaryFile("blob.dat", NullByteText("needle"))
            .Query("needle").Substring()
            .ExpectFiles("text.txt").Build();

        // ── Hidden files ────────────────────────────────────────────────────
        yield return Scn("hidden-included-by-default")
            .File("visible.txt", "needle")
            .HiddenFile("secret.txt", "needle")
            .Query("needle").Substring()
            .ExpectFiles("secret.txt", "visible.txt").Build();

        yield return Scn("hidden-excluded-when-no-hidden")
            .File("visible.txt", "needle")
            .HiddenFile("secret.txt", "needle")
            .Query("needle").Substring().NoHidden()
            .ExpectFiles("visible.txt").Build();

        yield return Scn("hidden-only-no-hidden-empty")
            .HiddenFile("secret.txt", "needle")
            .Query("needle").Substring().NoHidden()
            .ExpectNoMatches().Build();

        yield return Scn("hidden-nested-excluded-when-no-hidden")
            .File("visible.txt", "needle")
            .HiddenFile("sub/secret.txt", "needle")
            .Query("needle").Substring().NoHidden()
            .ExpectFiles("visible.txt").Build();

        yield return Scn("hidden-mixed-counts-default")
            .File("visible.txt", "needle")
            .HiddenFile("secret.txt", "needle")
            .Query("needle").Substring()
            .ExpectTotal(2).Build();

        // ── Skip extensions ─────────────────────────────────────────────────
        yield return Scn("skip-extension-excludes-files")
            .File("keep.txt", "needle")
            .File("drop.log", "needle")
            .Query("needle").Substring().SkipExtensions("log")
            .ExpectFiles("keep.txt").Build();

        yield return Scn("skip-extension-multiple")
            .File("keep.txt", "needle")
            .File("drop.log", "needle")
            .File("scratch.tmp", "needle")
            .Query("needle").Substring().SkipExtensions("log", "tmp")
            .ExpectFiles("keep.txt").Build();

        yield return Scn("skip-extension-keeps-others")
            .File("a.cs", "needle")
            .File("b.md", "needle")
            .File("c.log", "needle")
            .Query("needle").Substring().SkipExtensions("log")
            .ExpectFiles("a.cs", "b.md").Build();

        yield return Scn("skip-extension-case-insensitive")
            .File("a.txt", "needle")
            .File("b.LOG", "needle")
            .Query("needle").Substring().SkipExtensions("log")
            .ExpectFiles("a.txt").Build();

        yield return Scn("skip-extension-only-one-left")
            .File("a.log", "needle")
            .File("b.tmp", "needle")
            .File("c.txt", "needle")
            .Query("needle").Substring().SkipExtensions("log", "tmp")
            .ExpectFiles("c.txt").Build();

        // ── Multi-term OR / no-match ────────────────────────────────────────
        yield return Scn("multiterm-substring-is-or")
            .File("a.txt", "cat")
            .File("b.txt", "dog")
            .File("c.txt", "fish")
            .Query("cat dog").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        yield return Scn("no-matches-returns-empty")
            .File("a.txt", "alpha")
            .File("b.txt", "beta")
            .Query("needle").Substring()
            .ExpectNoMatches().Build();

        yield return Scn("multiterm-three-or")
            .File("a.txt", "red")
            .File("b.txt", "green")
            .File("c.txt", "blue")
            .File("d.txt", "black")
            .Query("red green blue").Substring()
            .ExpectFiles("a.txt", "b.txt", "c.txt").Build();

        yield return Scn("multiterm-overlapping-terms")
            .File("a.txt", "nation")
            .File("b.txt", "international")
            .File("c.txt", "other")
            .Query("nation international").Substring()
            .ExpectFiles("a.txt", "b.txt").Build();

        // ── Cross-cutting combinations ──────────────────────────────────────
        yield return Scn("regex-with-include-ext")
            .File("a.cs", "foo123")
            .File("b.txt", "foo123")
            .Regex(@"foo\d+").Include("*.cs")
            .ExpectFiles("a.cs").Build();

        yield return Scn("wholeword-with-exclude-segment")
            .File("src/code.txt", "async run")
            .File("node_modules/x.txt", "async run")
            .Query("async").WholeWord().Exclude("node_modules")
            .ExpectFiles("src/code.txt").Build();

        yield return Scn("case-sensitive-with-include")
            .File("a.cs", "Error")
            .File("b.cs", "error")
            .Query("Error").CaseSensitive().Include("*.cs")
            .ExpectFiles("a.cs").Build();

        yield return Scn("regex-case-sensitive-with-depth")
            .File("top.txt", "ERR")
            .File("a/deep.txt", "err")
            .Regex("err").CaseSensitive().MaxDepth(1)
            .ExpectFiles("a/deep.txt").Build();

        yield return Scn("substring-with-size-and-ext")
            .File("a.cs", Sized(200))
            .File("b.cs", Sized(20))
            .File("c.txt", Sized(200))
            .Query("needle").Substring().Include("*.cs").MinSize(80)
            .ExpectFiles("a.cs").Build();

        yield return Scn("mode-filenames-with-include-ext")
            .File("match.cs", "body")
            .File("match.txt", "body")
            .File("other.cs", "body")
            .Query("match").Mode(SearchMode.FileNames).Include("*.cs")
            .ExpectFiles("match.cs").Build();

        yield return Scn("regex-with-exclude-regex")
            .File("keep/a.txt", "v1")
            .File("skipdir/b.txt", "v1")
            .Regex(@"v\d").ExcludeRegex(@"/skipdir/")
            .ExpectFiles("keep/a.txt").Build();

        yield return Scn("multiterm-with-maxmatches")
            .File("a.txt", "cat\ndog\ncat\ndog\ncat")
            .Query("cat dog").Substring().MaxMatchesPerFile(2)
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("hidden-with-include-ext")
            .File("visible.cs", "needle")
            .HiddenFile("secret.cs", "needle")
            .File("note.txt", "needle")
            .Query("needle").Substring().Include("*.cs")
            .ExpectFiles("secret.cs", "visible.cs").Build();

        yield return Scn("skipext-with-regex")
            .File("a.txt", "foo1")
            .File("b.log", "foo1")
            .Regex(@"foo\d").SkipExtensions("log")
            .ExpectFiles("a.txt").Build();

        // ── Content edge cases ──────────────────────────────────────────────
        yield return Scn("crlf-line-endings")
            .File("a.txt", "alpha\r\nneedle\r\nbeta")
            .Query("needle").Substring()
            .ExpectMatchesInFile("a.txt", 1).Build();

        yield return Scn("very-long-line")
            .File("a.txt", "start " + new string('a', 5000) + " needle end")
            .Query("needle").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("token-at-eof-no-newline")
            .File("a.txt", "line1\nneedle")
            .Query("needle").Substring()
            .ExpectMatchesInFile("a.txt", 1).Build();

        yield return Scn("blank-lines-between-matches")
            .File("a.txt", "needle\n\n\nneedle")
            .Query("needle").Substring()
            .ExpectMatchesInFile("a.txt", 2).Build();

        yield return Scn("many-files-same-token")
            .File("f00.txt", "needle").File("f01.txt", "needle").File("f02.txt", "needle")
            .File("f03.txt", "needle").File("f04.txt", "needle").File("f05.txt", "needle")
            .File("f06.txt", "needle").File("f07.txt", "needle").File("f08.txt", "needle")
            .File("f09.txt", "needle")
            .Query("needle").Substring()
            .ExpectTotal(10).Build();

        yield return Scn("deeply-nested-single-match")
            .File("a/b/c/d/e/deep.txt", "needle")
            .File("a/b/c/other.txt", "haystack")
            .Query("needle").Substring()
            .ExpectFiles("a/b/c/d/e/deep.txt").Build();

        yield return Scn("mixed-case-corpus-insensitive")
            .File("a.txt", "NEEDLE")
            .File("b.txt", "needle")
            .File("c.txt", "NeEdLe")
            .Query("needle").Substring()
            .ExpectTotal(3).Build();

        yield return Scn("special-chars-literal-substring")
            .File("a.txt", "a+b=c")
            .File("b.txt", "axbyc")
            .Query("a+b").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("unicode-content-ascii-query")
            .File("a.txt", "über test café")
            .File("b.txt", "plain words")
            .Query("test").Substring()
            .ExpectFiles("a.txt").Build();

        yield return Scn("empty-file-no-match")
            .File("a.txt", "")
            .File("b.txt", "needle")
            .Query("needle").Substring()
            .ExpectFiles("b.txt").Build();

        yield return Scn("whitespace-content-no-token")
            .File("a.txt", "   \n  \n")
            .File("b.txt", "needle")
            .Query("needle").Substring()
            .ExpectFiles("b.txt").Build();
    }

    // PNG 8-byte magic signature; the binary detector flags this as non-text.
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    // ASCII text carrying an embedded NUL byte so the binary detector treats
    // the file as binary; the leading line keeps the query token searchable
    // once SearchBinary() is enabled.
    private static byte[] NullByteText(string token)
        => Combine(Encoding.ASCII.GetBytes(token + "\n"), new byte[] { 0x00, 0x00, (byte)'t', (byte)'a', (byte)'i', (byte)'l' });
}
