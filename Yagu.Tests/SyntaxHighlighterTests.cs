using Yagu.Helpers;
using static Yagu.Helpers.SyntaxHighlighter;

namespace Yagu.Tests;

public class SyntaxHighlighterTests
{
    [Fact]
    public void EmptyLine_YieldsNoTokens()
    {
        Assert.Empty(SyntaxHighlighter.Tokenize("", ".cs"));
    }

    [Fact]
    public void NullLine_YieldsNoTokens()
    {
        Assert.Empty(SyntaxHighlighter.Tokenize(null!, ".cs"));
    }

    [Fact]
    public void CSharpKeywords_Detected()
    {
        var tokens = SyntaxHighlighter.Tokenize("public static void Main()", ".cs").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "public static void Main()".Substring(t.Start, t.Length) == "public");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "public static void Main()".Substring(t.Start, t.Length) == "static");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "public static void Main()".Substring(t.Start, t.Length) == "void");
    }

    [Fact]
    public void TypeScriptKeywords_Detected()
    {
        var tokens = SyntaxHighlighter.Tokenize("const x: number = 5;", ".ts").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "const x: number = 5;".Substring(t.Start, t.Length) == "const");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "const x: number = 5;".Substring(t.Start, t.Length) == "number");
    }

    [Fact]
    public void JavaScriptKeywords_Detected()
    {
        var tokens = SyntaxHighlighter.Tokenize("let x = new Object()", ".js").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "let x = new Object()".Substring(t.Start, t.Length) == "let");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "let x = new Object()".Substring(t.Start, t.Length) == "new");
    }

    [Fact]
    public void PythonKeywords_Detected()
    {
        var tokens = SyntaxHighlighter.Tokenize("def foo(): return True", ".py").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "def foo(): return True".Substring(t.Start, t.Length) == "def");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "def foo(): return True".Substring(t.Start, t.Length) == "return");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && "def foo(): return True".Substring(t.Start, t.Length) == "True");
    }

    [Fact]
    public void JsonKeywords_Detected()
    {
        var tokens = SyntaxHighlighter.Tokenize("{ true, false, null }", ".json").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void LineComment_Detected()
    {
        var line = "int x = 5; // comment";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment && line.Substring(t.Start, t.Length).Contains("// comment"));
    }

    [Fact]
    public void HashComment_Detected()
    {
        var line = "x = 5 # comment";
        var tokens = SyntaxHighlighter.Tokenize(line, ".py").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment && line.Substring(t.Start, t.Length).Contains("# comment"));
    }

    [Fact]
    public void String_DoubleQuoted_Detected()
    {
        var line = "var x = \"hello world\";";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.StringLiteral && line.Substring(t.Start, t.Length) == "\"hello world\"");
    }

    [Fact]
    public void String_SingleQuoted_Detected()
    {
        var line = "x = 'hello'";
        var tokens = SyntaxHighlighter.Tokenize(line, ".py").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.StringLiteral && line.Substring(t.Start, t.Length) == "'hello'");
    }

    [Fact]
    public void String_WithEscapes_Detected()
    {
        var line = "var x = \"he\\\"llo\";";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.StringLiteral);
    }

    [Fact]
    public void Number_IntegerDetected()
    {
        var line = "int x = 42;";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Number && line.Substring(t.Start, t.Length) == "42");
    }

    [Fact]
    public void Number_DecimalDetected()
    {
        var line = "double x = 3.14;";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Number && line.Substring(t.Start, t.Length) == "3.14");
    }

    [Fact]
    public void CommentRegion_BlocksOtherTokens()
    {
        // Tokens inside comments should not appear as keywords/strings/numbers
        var line = "// var x = 42";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Keyword);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Number);
    }

    [Fact]
    public void StringRegion_BlocksKeywords()
    {
        var line = "var x = \"return void\";";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        // "return" and "void" inside the string should not be classified as keywords
        var kwTokens = tokens.Where(t => t.Kind == TokenKind.Keyword).ToList();
        foreach (var kw in kwTokens)
        {
            var text = line.Substring(kw.Start, kw.Length);
            Assert.NotEqual("return", text);
            Assert.NotEqual("void", text);
        }
    }

    [Fact]
    public void UnknownExtension_NoKeywords()
    {
        var tokens = SyntaxHighlighter.Tokenize("public static void", ".xyz").ToList();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void NumberInsideString_NotClassifiedAsNumber()
    {
        var line = "var x = \"42\";";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Number);
    }

    [Fact]
    public void NonKeywordIdentifier_NotClassified()
    {
        var line = "myVariable = 5;";
        var tokens = SyntaxHighlighter.Tokenize(line, ".cs").ToList();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Keyword && line.Substring(t.Start, t.Length) == "myVariable");
    }
}
