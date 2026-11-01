using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for the preview pane's single-line syntax classifier and its palette.
/// </summary>
public sealed class PreviewSyntaxHighlighterTests
{
    private static PreviewTokenKind? KindAt(string text, EditorSyntaxLanguage language, string fragment)
    {
        int index = text.IndexOf(fragment, StringComparison.Ordinal);
        Assert.True(index >= 0, $"fragment '{fragment}' not present in '{text}'");
        foreach (var token in PreviewSyntaxHighlighter.Tokenize(text, language))
        {
            if (index >= token.Start && index < token.End)
                return token.Kind;
        }
        return null;
    }

    private static string TextOf(string text, PreviewSyntaxHighlighter.Token token) =>
        text.Substring(token.Start, token.Length);

    // ── C / C++ ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("int main(void) {", "int", PreviewTokenKind.Keyword)]
    [InlineData("int main(void) {", "void", PreviewTokenKind.Keyword)]
    [InlineData("unsigned long x = 42;", "42", PreviewTokenKind.Number)]
    [InlineData("puts(\"hello\");", "\"hello\"", PreviewTokenKind.StringLiteral)]
    [InlineData("x++; // bump it", "// bump it", PreviewTokenKind.Comment)]
    [InlineData("/* block */ int y;", "/* block */", PreviewTokenKind.Comment)]
    [InlineData("size_t n = 0;", "size_t", PreviewTokenKind.Keyword)]
    [InlineData("static constexpr bool ok = true;", "constexpr", PreviewTokenKind.Keyword)]
    public void Cpp_ClassifiesCoreTokens(string line, string fragment, PreviewTokenKind expected)
    {
        Assert.Equal(expected, KindAt(line, EditorSyntaxLanguage.Cpp, fragment));
    }

    [Fact]
    public void Cpp_ColorsPreprocessorDirectiveAndAngleBracketHeader()
    {
        const string line = "#include <stdio.h>";
        Assert.Equal(PreviewTokenKind.Preprocessor, KindAt(line, EditorSyntaxLanguage.Cpp, "#include"));
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt(line, EditorSyntaxLanguage.Cpp, "<stdio.h>"));
    }

    [Fact]
    public void Cpp_ColorsIndentedPreprocessorDirective()
    {
        Assert.Equal(PreviewTokenKind.Preprocessor, KindAt("  #define MAX 10", EditorSyntaxLanguage.Cpp, "#define"));
    }

    [Fact]
    public void Cpp_HashInsideCodeIsNotAPreprocessorDirective()
    {
        Assert.Null(KindAt("int a = b # c;", EditorSyntaxLanguage.Cpp, "#"));
    }

    [Fact]
    public void Cpp_KeywordInsideStringIsNotColoredSeparately()
    {
        const string line = "const char* s = \"int void return\";";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp);

        var stringToken = Assert.Single(tokens, t => t.Kind == PreviewTokenKind.StringLiteral);
        Assert.Equal("\"int void return\"", TextOf(line, stringToken));

        // The keywords inside the literal must not produce their own tokens.
        Assert.DoesNotContain(tokens, t => t.Kind == PreviewTokenKind.Keyword && t.Start > stringToken.Start && t.Start < stringToken.End);
    }

    [Fact]
    public void Cpp_KeywordInsideLineCommentIsNotColoredSeparately()
    {
        const string line = "x = 1; // return int";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp);
        var comment = Assert.Single(tokens, t => t.Kind == PreviewTokenKind.Comment);
        Assert.Equal("// return int", TextOf(line, comment));
        Assert.DoesNotContain(tokens, t => t.Kind == PreviewTokenKind.Keyword && t.Start > comment.Start);
    }

    [Fact]
    public void Cpp_UnterminatedBlockCommentRunsToEndOfLine()
    {
        const string line = "int a; /* opened here";
        var comment = Assert.Single(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp), t => t.Kind == PreviewTokenKind.Comment);
        Assert.Equal("/* opened here", TextOf(line, comment));
    }

    [Fact]
    public void Cpp_IdentifiersThatMerelyContainKeywordsAreNotColored()
    {
        Assert.Null(KindAt("int interesting = printf_int(1);", EditorSyntaxLanguage.Cpp, "interesting"));
        Assert.Null(KindAt("int interesting = printf_int(1);", EditorSyntaxLanguage.Cpp, "printf_int"));
    }

    [Fact]
    public void Cpp_CharLiteralIsAString()
    {
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt("char c = 'a';", EditorSyntaxLanguage.Cpp, "'a'"));
    }

    [Fact]
    public void Cpp_EscapedQuoteDoesNotEndTheString()
    {
        const string line = "puts(\"a\\\"b\"); int x;";
        var stringToken = Assert.Single(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp), t => t.Kind == PreviewTokenKind.StringLiteral);
        Assert.Equal("\"a\\\"b\"", TextOf(line, stringToken));
    }

    // ── other languages ───────────────────────────────────────────────────────

    [Fact]
    public void CSharp_ClassifiesKeywordsAndStrings()
    {
        Assert.Equal(PreviewTokenKind.Keyword, KindAt("public static void Main()", EditorSyntaxLanguage.CSharp, "public"));
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt("var s = \"hi\";", EditorSyntaxLanguage.CSharp, "\"hi\""));
    }

    [Fact]
    public void Python_UsesHashComments()
    {
        Assert.Equal(PreviewTokenKind.Comment, KindAt("x = 1  # note", EditorSyntaxLanguage.Python, "# note"));
        Assert.Equal(PreviewTokenKind.Keyword, KindAt("def foo(): return None", EditorSyntaxLanguage.Python, "def"));
    }

    [Fact]
    public void Javascript_SupportsTemplateLiterals()
    {
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt("const a = `x${y}`;", EditorSyntaxLanguage.Javascript, "`x${y}`"));
        Assert.Equal(PreviewTokenKind.Keyword, KindAt("const a = 1;", EditorSyntaxLanguage.Javascript, "const"));
    }

    [Fact]
    public void Json_ColorsPropertyNamesDistinctlyFromStringValues()
    {
        const string line = "{ \"name\": \"yagu\", \"count\": 3, \"ok\": true }";
        Assert.Equal(PreviewTokenKind.Tag, KindAt(line, EditorSyntaxLanguage.Json, "\"name\""));
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt(line, EditorSyntaxLanguage.Json, "\"yagu\""));
        Assert.Equal(PreviewTokenKind.Number, KindAt(line, EditorSyntaxLanguage.Json, "3"));
        Assert.Equal(PreviewTokenKind.Keyword, KindAt(line, EditorSyntaxLanguage.Json, "true"));
    }

    [Fact]
    public void Sql_KeywordsAreCaseInsensitive()
    {
        Assert.Equal(PreviewTokenKind.Keyword, KindAt("select * from t", EditorSyntaxLanguage.SQL, "select"));
        Assert.Equal(PreviewTokenKind.Keyword, KindAt("SELECT * FROM t", EditorSyntaxLanguage.SQL, "SELECT"));
        Assert.Equal(PreviewTokenKind.Comment, KindAt("select 1 -- why", EditorSyntaxLanguage.SQL, "-- why"));
    }

    [Fact]
    public void Xml_ColorsElementNamesAttributesAndValues()
    {
        const string line = "<Grid x:Name=\"Root\" Width=\"10\">";
        Assert.Equal(PreviewTokenKind.Tag, KindAt(line, EditorSyntaxLanguage.XML, "<Grid"));
        Assert.Equal(PreviewTokenKind.Attribute, KindAt(line, EditorSyntaxLanguage.XML, "x:Name"));
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt(line, EditorSyntaxLanguage.XML, "\"Root\""));
    }

    [Fact]
    public void Html_ColorsComments()
    {
        Assert.Equal(PreviewTokenKind.Comment, KindAt("<!-- hidden -->", EditorSyntaxLanguage.Html, "<!-- hidden -->"));
    }

    [Fact]
    public void Markdown_ColorsHeadingsAndInlineCode()
    {
        Assert.Equal(PreviewTokenKind.Tag, KindAt("## Section", EditorSyntaxLanguage.Markdown, "## Section"));
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt("use `grep` here", EditorSyntaxLanguage.Markdown, "`grep`"));
    }

    [Fact]
    public void Ini_ColorsSectionHeadersAndKeys()
    {
        Assert.Equal(PreviewTokenKind.Tag, KindAt("[server]", EditorSyntaxLanguage.Inifile, "[server]"));
        Assert.Equal(PreviewTokenKind.Attribute, KindAt("port = 8080", EditorSyntaxLanguage.Inifile, "port"));
        Assert.Equal(PreviewTokenKind.Comment, KindAt("; a note", EditorSyntaxLanguage.Inifile, "; a note"));
    }

    [Fact]
    public void Batch_TreatsRemAsAWholeLineComment()
    {
        Assert.Equal(PreviewTokenKind.Comment, KindAt("rem build the app", EditorSyntaxLanguage.Batch, "rem build the app"));
        Assert.Equal(PreviewTokenKind.Comment, KindAt("REM build", EditorSyntaxLanguage.Batch, "REM build"));
        Assert.Equal(PreviewTokenKind.Attribute, KindAt("echo %PATH%", EditorSyntaxLanguage.Batch, "%PATH%"));
    }

    [Fact]
    public void Php_ColorsVariables()
    {
        Assert.Equal(PreviewTokenKind.Attribute, KindAt("$name = 'x';", EditorSyntaxLanguage.PHP, "$name"));
    }

    [Fact]
    public void Css_ColorsAtRulesAndProperties()
    {
        Assert.Equal(PreviewTokenKind.Keyword, KindAt("@media screen {", EditorSyntaxLanguage.CSS, "@media"));
        Assert.Equal(PreviewTokenKind.Attribute, KindAt("color: red;", EditorSyntaxLanguage.CSS, "color"));
    }

    // ── general contract ──────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_ReturnsOrderedNonOverlappingTokensWithinBounds()
    {
        const string line = "#include <stdio.h> /* c */ int x = 0x1F; // done";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp);

        Assert.NotEmpty(tokens);
        int previousEnd = 0;
        foreach (var token in tokens)
        {
            Assert.True(token.Start >= previousEnd, "tokens must not overlap and must be ordered");
            Assert.True(token.Length > 0);
            Assert.True(token.End <= line.Length, "tokens must stay inside the scanned text");
            previousEnd = token.End;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_EmptyOrBlankLineProducesNoTokens(string line)
    {
        Assert.Empty(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp));
    }

    [Fact]
    public void Tokenize_RespectsTokenCap()
    {
        string line = string.Join(' ', Enumerable.Repeat("int", 200));
        Assert.Equal(5, PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp, maxTokens: 5).Count);
    }

    [Fact]
    public void Tokenize_AllResolverLanguagesAreSupportedAndDoNotThrow()
    {
        const string sample = "#include <a.h> int x = 1; /* c */ \"s\" 'c' <tag a=\"b\"> [s] key = 1 -- x // y % z";
        foreach (EditorSyntaxLanguage language in Enum.GetValues<EditorSyntaxLanguage>())
        {
            Assert.True(PreviewSyntaxHighlighter.IsSupported(language));
            var tokens = PreviewSyntaxHighlighter.Tokenize(sample, language);
            int previousEnd = 0;
            foreach (var token in tokens)
            {
                Assert.True(token.Start >= previousEnd, $"{language}: overlapping tokens");
                Assert.True(token.End <= sample.Length, $"{language}: token past end of text");
                previousEnd = token.End;
            }
        }
    }

    [Fact]
    public void Tokenize_UnknownLanguageFallsBackToPlainCodeRules()
    {
        // An enum value from a newer resolver must still use the safe generic scanner rather than
        // throwing while building the preview pane.
        const string line = "value \"quoted\" 42";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, (EditorSyntaxLanguage)9999);

        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt(line, (EditorSyntaxLanguage)9999, "\"quoted\""));
        Assert.Equal(PreviewTokenKind.Number, KindAt(line, (EditorSyntaxLanguage)9999, "42"));
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void CodeScanner_NewlineResetsPreprocessorIndent()
    {
        // A directive on the second rendered line must be recognized relative to that line's indent;
        // otherwise a multi-line preview slice loses highlighting after the first newline.
        const string line = "int x;\n  #define VALUE 1";

        Assert.Equal(PreviewTokenKind.Preprocessor, KindAt(line, EditorSyntaxLanguage.Cpp, "#define"));
    }

    [Fact]
    public void Latex_BackslashCommandsArePreprocessorTokens()
    {
        // LaTeX commands are the semantic landmarks in TeX files; they should be colored even though
        // the language disables normal quoted-string rules.
        const string line = "\\section{Intro}";

        Assert.Equal(PreviewTokenKind.Preprocessor, KindAt(line, EditorSyntaxLanguage.Latex, "\\section"));
    }

    [Fact]
    public void Cpp_NonIncludePreprocessorDirectiveDoesNotScanHeader()
    {
        // Only include/import directives should color an angle-bracket target; other preprocessor
        // lines must resume scanning after the directive word.
        const string line = "#pragma once";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp);

        var token = Assert.Single(tokens);
        Assert.Equal(PreviewTokenKind.Preprocessor, token.Kind);
        Assert.Equal("#pragma", TextOf(line, token));
    }

    [Fact]
    public void Cpp_IncludeAtTokenCapDoesNotAppendHeaderToken()
    {
        // The token cap is a long-line crash guard; when the directive consumes the final token budget,
        // the angle-bracket header must be skipped rather than overflowing the cap.
        const string line = "#include <vector>";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp, maxTokens: 1);

        var token = Assert.Single(tokens);
        Assert.Equal(PreviewTokenKind.Preprocessor, token.Kind);
        Assert.Equal("#include", TextOf(line, token));
    }

    [Fact]
    public void Cpp_IncludeWithQuotedHeaderLeavesHeaderToGenericStringScanner()
    {
        // The preprocessor fast path only owns angle-bracket headers; quoted includes should fall back
        // to the normal string scanner so token ordering remains non-overlapping.
        const string line = "#include \"local.h\"";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp);

        Assert.Equal(["#include", "\"local.h\""], tokens.Select(t => TextOf(line, t)).ToArray());
        Assert.Equal(PreviewTokenKind.Preprocessor, tokens[0].Kind);
        Assert.Equal(PreviewTokenKind.StringLiteral, tokens[1].Kind);
    }

    [Fact]
    public void Xml_SkipsDeclarationPrefixAndClosingSlashBeforeTagName()
    {
        // XML declarations and closing tags begin with punctuation before the name; the scanner must
        // skip it so attributes and tag names remain aligned.
        const string declaration = "<?xml version='1.0'?>";
        const string closing = "</Grid>";

        Assert.Equal(PreviewTokenKind.Tag, KindAt(declaration, EditorSyntaxLanguage.XML, "<?xml"));
        Assert.Equal(PreviewTokenKind.Attribute, KindAt(declaration, EditorSyntaxLanguage.XML, "version"));
        Assert.Equal(PreviewTokenKind.StringLiteral, KindAt(declaration, EditorSyntaxLanguage.XML, "'1.0'"));
        Assert.Equal(PreviewTokenKind.Tag, KindAt(closing, EditorSyntaxLanguage.XML, "</Grid"));
    }

    [Fact]
    public void Markdown_ColorsBlockQuoteAndFenceLines()
    {
        // Block quotes and fenced-code markers are whole-line Markdown constructs; classifying only a
        // prefix would leave the rest of the rendered line in the wrong brush.
        Assert.Equal(PreviewTokenKind.Comment, KindAt("> quoted text", EditorSyntaxLanguage.Markdown, "> quoted text"));
        Assert.Equal(PreviewTokenKind.Preprocessor, KindAt("```csharp", EditorSyntaxLanguage.Markdown, "```csharp"));
        Assert.Equal(PreviewTokenKind.Preprocessor, KindAt("~~~", EditorSyntaxLanguage.Markdown, "~~~"));
    }

    [Fact]
    public void Markdown_ListMarkerLinkLabelAndTargetAreDistinctTokens()
    {
        // A Markdown list item can contain a link; the bullet, label, and URL each need their own
        // token so syntax color does not bleed across the line.
        const string line = "- see [docs](https://example.test)";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Markdown);

        Assert.Equal(PreviewTokenKind.Keyword, KindAt(line, EditorSyntaxLanguage.Markdown, "-"));
        Assert.Equal(PreviewTokenKind.Keyword, KindAt(line, EditorSyntaxLanguage.Markdown, "[docs]"));
        Assert.Equal(PreviewTokenKind.Attribute, KindAt(line, EditorSyntaxLanguage.Markdown, "https://example.test"));
        Assert.Equal(["-", "[docs]", "https://example.test"], tokens.Select(t => TextOf(line, t)).ToArray());
    }

    [Fact]
    public void Markdown_UnclosedLinkPiecesDoNotCreateTargetTokens()
    {
        // Unclosed link syntax must be ignored instead of creating a zero-length target token, which
        // would corrupt the non-overlapping token contract for malformed Markdown.
        const string line = "see []( and [broken";
        var tokens = PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Markdown);

        Assert.Contains(tokens, t => t.Kind == PreviewTokenKind.Keyword && TextOf(line, t) == "[]");
        Assert.DoesNotContain(tokens, t => t.Kind == PreviewTokenKind.Attribute);
        Assert.DoesNotContain(tokens, t => TextOf(line, t) == "[broken");
    }

    [Fact]
    public void CodeString_UnterminatedBeforeNewlineStopsAtLineEnd()
    {
        // An unterminated string literal must stop at end-of-line rather than swallowing the rest of
        // the preview; one stray quote should not gray out every following line.
        const string line = "\"first line\nint next;";
        var token = Assert.Single(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp), t => t.Kind == PreviewTokenKind.StringLiteral);

        Assert.Equal(PreviewTokenKind.StringLiteral, token.Kind);
        Assert.Equal("\"first line", TextOf(line, token));
    }

    [Fact]
    public void CodeString_UnterminatedAtEndStopsAtBufferEnd()
    {
        // A preview slice can end in the middle of a string; the token must be clamped to the slice
        // rather than reading past the rendered text.
        const string line = "var s = \"tail";
        var token = Assert.Single(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.CSharp), t => t.Kind == PreviewTokenKind.StringLiteral);

        Assert.Equal("\"tail", TextOf(line, token));
    }

    [Fact]
    public void NumberScanner_KeepsExponentSignWithLiteral()
    {
        // Scientific notation must keep the exponent sign inside the number token, otherwise "1e-9"
        // is split and the minus sign is rendered as plain text.
        const string line = "double x = 1e-9;";

        Assert.Equal(PreviewTokenKind.Number, KindAt(line, EditorSyntaxLanguage.Cpp, "1e-9"));
        Assert.Contains(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Cpp), t => TextOf(line, t) == "1e-9");
    }

    [Fact]
    public void Css_KeyWithoutAssignmentAtEndIsNotColoredAsAttribute()
    {
        // CSS key coloring requires an actual ':' or '='; a partial line ending after an identifier
        // should stay plain until the assignment appears.
        const string line = "color   ";

        Assert.Empty(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.CSS));
    }

    [Fact]
    public void Json_StringAtEndWithoutColonIsValueNotProperty()
    {
        // A JSON string followed only by whitespace is a value token, not a property name; otherwise
        // incomplete preview slices would color trailing values as object keys.
        const string line = "\"tail\"   ";

        var token = Assert.Single(PreviewSyntaxHighlighter.Tokenize(line, EditorSyntaxLanguage.Json));
        Assert.Equal(PreviewTokenKind.StringLiteral, token.Kind);
        Assert.Equal("\"tail\"", TextOf(line, token));
    }

    // ── palette ───────────────────────────────────────────────────────────────

    [Fact]
    public void Palette_ReturnsDistinctColorsPerKindInBothThemes()
    {
        foreach (bool isLight in new[] { true, false })
        {
            var seen = new HashSet<uint>();
            foreach (PreviewTokenKind kind in Enum.GetValues<PreviewTokenKind>())
            {
                uint color = PreviewSyntaxPalette.GetColor(kind, isLight);
                Assert.Equal(0xFFu, color >> 24); // fully opaque
                Assert.True(seen.Add(color), $"duplicate color for {kind} (light={isLight})");
            }
        }
    }

    [Fact]
    public void Palette_ContextColorIsBlendedTowardTheContextGray()
    {
        foreach (PreviewTokenKind kind in Enum.GetValues<PreviewTokenKind>())
        {
            uint full = PreviewSyntaxPalette.GetColor(kind, isLightTheme: false);
            uint dimmed = PreviewSyntaxPalette.GetContextColor(kind, isLightTheme: false);
            Assert.Equal(PreviewSyntaxPalette.Blend(full, PreviewSyntaxPalette.ContextGray, PreviewSyntaxPalette.ContextDimAmount), dimmed);
        }
    }

    [Theory]
    [InlineData(0.0, 0xFF000000u)]
    [InlineData(1.0, 0xFFFFFFFFu)]
    public void Palette_BlendEndpointsReturnTheEndpointColors(double amount, uint expected)
    {
        Assert.Equal(expected, PreviewSyntaxPalette.Blend(0xFF000000u, 0xFFFFFFFFu, amount));
    }

    [Fact]
    public void Palette_BlendClampsOutOfRangeAmounts()
    {
        Assert.Equal(0xFF000000u, PreviewSyntaxPalette.Blend(0xFF000000u, 0xFFFFFFFFu, -5));
        Assert.Equal(0xFFFFFFFFu, PreviewSyntaxPalette.Blend(0xFF000000u, 0xFFFFFFFFu, 5));
    }
}
