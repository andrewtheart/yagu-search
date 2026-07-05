using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class EditorSyntaxHighlightingResolverTests
{
    [Theory]
    [InlineData("Program.cs", EditorSyntaxLanguage.CSharp)]
    [InlineData("main.py", EditorSyntaxLanguage.Python)]
    [InlineData("app.js", EditorSyntaxLanguage.Javascript)]
    [InlineData("data.json", EditorSyntaxLanguage.Json)]
    [InlineData("page.html", EditorSyntaxLanguage.Html)]
    [InlineData("page.htm", EditorSyntaxLanguage.Html)]
    [InlineData("styles.css", EditorSyntaxLanguage.CSS)]
    [InlineData("styles.scss", EditorSyntaxLanguage.CSS)]
    [InlineData("query.sql", EditorSyntaxLanguage.SQL)]
    [InlineData("README.md", EditorSyntaxLanguage.Markdown)]
    [InlineData("doc.markdown", EditorSyntaxLanguage.Markdown)]
    [InlineData("script.lua", EditorSyntaxLanguage.Lua)]
    [InlineData("index.php", EditorSyntaxLanguage.PHP)]
    [InlineData("Main.java", EditorSyntaxLanguage.Java)]
    [InlineData("layout.xaml", EditorSyntaxLanguage.XML)]
    [InlineData("config.xml", EditorSyntaxLanguage.XML)]
    [InlineData("settings.toml", EditorSyntaxLanguage.Toml)]
    [InlineData("settings.ini", EditorSyntaxLanguage.Inifile)]
    [InlineData("module.cpp", EditorSyntaxLanguage.Cpp)]
    [InlineData("header.h", EditorSyntaxLanguage.Cpp)]
    [InlineData("source.cc", EditorSyntaxLanguage.Cpp)]
    [InlineData("run.bat", EditorSyntaxLanguage.Batch)]
    [InlineData("boot.asm", EditorSyntaxLanguage.X86Assembly)]
    [InlineData("part.gcode", EditorSyntaxLanguage.GCode)]
    [InlineData("firmware.hex", EditorSyntaxLanguage.HexFile)]
    [InlineData("paper.tex", EditorSyntaxLanguage.Latex)]
    [InlineData("algo.qs", EditorSyntaxLanguage.QSharp)]
    public void ResolveFromFileName_KnownExtensions_ReturnsExpectedLanguage(string fileName, EditorSyntaxLanguage expected)
    {
        Assert.Equal(expected, EditorSyntaxHighlightingResolver.ResolveFromFileName(fileName));
    }

    [Theory]
    [InlineData("notes.csv", EditorSyntaxLanguage.CSVImproved)]
    [InlineData("run.cmd", EditorSyntaxLanguage.Batch)]
    [InlineData("app.cfg", EditorSyntaxLanguage.Inifile)]
    [InlineData("server.conf", EditorSyntaxLanguage.Inifile)]
    [InlineData("app.ts", EditorSyntaxLanguage.Javascript)]
    [InlineData("view.jsx", EditorSyntaxLanguage.Javascript)]
    [InlineData("view.tsx", EditorSyntaxLanguage.Javascript)]
    [InlineData("bundle.mjs", EditorSyntaxLanguage.Javascript)]
    [InlineData("bundle.cjs", EditorSyntaxLanguage.Javascript)]
    public void ResolveFromFileName_CuratedOverrides_ReturnExpectedLanguage(string fileName, EditorSyntaxLanguage expected)
    {
        Assert.Equal(expected, EditorSyntaxHighlightingResolver.ResolveFromFileName(fileName));
    }

    [Fact]
    public void ResolveFromFileName_Gitignore_ReturnsGitignore()
    {
        Assert.Equal(EditorSyntaxLanguage.Gitignore, EditorSyntaxHighlightingResolver.ResolveFromFileName(".gitignore"));
    }

    [Fact]
    public void ResolveFromFileName_GitignoreWithDirectory_ReturnsGitignore()
    {
        Assert.Equal(EditorSyntaxLanguage.Gitignore,
            EditorSyntaxHighlightingResolver.ResolveFromFileName(@"C:\src\project\.gitignore"));
    }

    [Fact]
    public void ResolveFromFileName_IsCaseInsensitive()
    {
        Assert.Equal(EditorSyntaxLanguage.CSharp, EditorSyntaxHighlightingResolver.ResolveFromFileName("PROGRAM.CS"));
    }

    [Fact]
    public void ResolveFromFileName_UsesFileNameFromFullPath()
    {
        Assert.Equal(EditorSyntaxLanguage.Python,
            EditorSyntaxHighlightingResolver.ResolveFromFileName(@"C:\some\deep\path\script.py"));
    }

    [Theory]
    [InlineData("archive.bin")]
    [InlineData("Compiled.class")]
    public void ResolveFromFileName_BinaryExtensions_ReturnNull(string fileName)
    {
        Assert.Null(EditorSyntaxHighlightingResolver.ResolveFromFileName(fileName));
    }

    [Theory]
    [InlineData("data.xyz")]
    [InlineData("noextension")]
    [InlineData("archive.unknownext")]
    public void ResolveFromFileName_UnknownExtension_ReturnsNull(string fileName)
    {
        Assert.Null(EditorSyntaxHighlightingResolver.ResolveFromFileName(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFromFileName_NullOrEmpty_ReturnsNull(string? filePath)
    {
        Assert.Null(EditorSyntaxHighlightingResolver.ResolveFromFileName(filePath));
    }
}
