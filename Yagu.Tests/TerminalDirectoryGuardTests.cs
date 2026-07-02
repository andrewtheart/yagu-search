using Yagu.Services;

namespace Yagu.Tests;

public sealed class TerminalDirectoryGuardTests
{
    [Fact]
    public void BuildChangeDirectoryProbeCommand_ChangesDriveAndEchoesMarkerOnlyAfterSuccess()
    {
        string command = TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand(@"C:\Program Files\Yagu", "__MARKER__");

        Assert.Equal("cd /d \"C:\\Program Files\\Yagu\" && echo __MARKER__", command);
    }

    [Fact]
    public void BuildChangeDirectoryProbeCommand_PowerShell_UsesSetLocationAndWriteOutput()
    {
        string command = TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand(
            @"C:\Program Files\Yagu", "__MARKER__", TerminalShellKind.PowerShell);

        Assert.Equal("Set-Location -LiteralPath 'C:\\Program Files\\Yagu'; Write-Output '__MARKER__'", command);
    }

    [Fact]
    public void BuildChangeDirectoryProbeCommand_PowerShell_EscapesSingleQuotes()
    {
        string command = TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand(
            @"C:\O'Brien", "M", TerminalShellKind.PowerShell);

        Assert.Contains("Set-Location -LiteralPath 'C:\\O''Brien'", command);
        Assert.Contains("Write-Output 'M'", command);
    }

    [Fact]
    public void TryExtractPromptDirectory_StripsPowerShellPromptPrefix()
    {
        string output = "__MARKER__\r\nPS C:\\src\\Yagu> ";

        Assert.True(TerminalDirectoryGuard.TryExtractPromptDirectory(output, "__MARKER__", out string directory));
        Assert.Equal(@"C:\src\Yagu", directory);
    }

    [Fact]
    public void TryExtractPromptDirectory_ReadsDirectoryFromPromptAfterMarker()
    {
        string output = "__MARKER__\r\nC:\\src\\Yagu\\Yagu\\bin\\Debug\\net10.0-windows10.0.19041.0> ";

        Assert.True(TerminalDirectoryGuard.TryExtractPromptDirectory(output, "__MARKER__", out string directory));
        Assert.Equal(@"C:\src\Yagu\Yagu\bin\Debug\net10.0-windows10.0.19041.0", directory);
    }

    [Fact]
    public void DirectoriesEqual_NormalizesTrailingSeparatorsAndCase()
    {
        Assert.True(TerminalDirectoryGuard.DirectoriesEqual(@"C:\SRC\Yagu\", @"c:\src\yagu"));
    }

    [Fact]
    public void RemoveMarkerLine_HidesGuardMarkerButKeepsVerifiedPrompt()
    {
        string filtered = TerminalDirectoryGuard.RemoveMarkerLine("__MARKER__\r\nC:\\src\\Yagu> ", "__MARKER__");

        Assert.Equal("\r\nC:\\src\\Yagu> ", filtered);
    }

    [Fact]
    public void CreateMarker_StartsWithPrefixAndEndsWith__()
    {
        string marker = TerminalDirectoryGuard.CreateMarker();
        Assert.StartsWith(TerminalDirectoryGuard.MarkerPrefix, marker);
        Assert.EndsWith("__", marker);
    }

    [Fact]
    public void CreateMarker_GeneratesUniqueValues()
    {
        var markers = Enumerable.Range(0, 10).Select(_ => TerminalDirectoryGuard.CreateMarker()).ToList();
        Assert.Equal(markers.Distinct().Count(), markers.Count);
    }

    [Fact]
    public void TryExtractPromptDirectory_NoMarkerInOutput_ReturnsFalse()
    {
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory("random output\r\nstuff", "NONEXISTENT", out _));
    }

    [Fact]
    public void TryExtractPromptDirectory_EmptyInput_ReturnsFalse()
    {
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory("", "MARKER", out _));
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory(null!, "MARKER", out _));
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory("text", "", out _));
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory("text", null!, out _));
    }

    [Fact]
    public void TryExtractPromptDirectory_NoAngleBracketAfterMarker_ReturnsFalse()
    {
        string output = "__MARKER__\r\nC:\\Users\\test without prompt delimiter";
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory(output, "__MARKER__", out _));
    }

    [Fact]
    public void DirectoriesEqual_DifferentPaths_ReturnsFalse()
    {
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual(@"C:\Users\alice", @"C:\Users\bob"));
    }

    [Fact]
    public void DirectoriesEqual_BothEmpty_ReturnsFalse()
    {
        // Empty normalizes to empty, and the method requires non-empty for a match
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual("", ""));
    }

    [Fact]
    public void DirectoriesEqual_OneEmpty_ReturnsFalse()
    {
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual(@"C:\Users", ""));
    }

    [Fact]
    public void DirectoriesEqual_QuotedPath_Normalized()
    {
        Assert.True(TerminalDirectoryGuard.DirectoriesEqual(@"""C:\Users\test""", @"C:\Users\test"));
    }

    [Fact]
    public void DirectoriesEqual_EnvironmentVariable_Expanded()
    {
        string temp = Path.GetFullPath(Environment.ExpandEnvironmentVariables("%TEMP%"));
        Assert.True(TerminalDirectoryGuard.DirectoriesEqual("%TEMP%", temp));
    }

    [Fact]
    public void DirectoriesEqual_WhitespaceOnly_ReturnsFalse()
    {
        // Whitespace-only normalizes to empty, and empty never matches
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual("   ", "  "));
    }

    [Fact]
    public void BuildChangeDirectoryProbeCommand_EscapesDoubleQuotes()
    {
        string cmd = TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand(@"C:\path ""test""", "M");
        Assert.Contains(@"""""test""""", cmd);
        Assert.Contains("echo M", cmd);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerInMiddle_RemovesOnlyMarkerLine()
    {
        string output = "first line\n__MARKER__\nthird line";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MARKER__");
        Assert.Equal("first line\nthird line", result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerAtEndNoTrailingNewline_RemovesMarkerLine()
    {
        string output = "first line\n__MARKER__";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MARKER__");
        Assert.Equal("first line\n", result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerAtStartNoNewlineBeforeContent_PrependsCrLf()
    {
        // When marker is at start (lineStart=0) and after text doesn't start with \r or \n
        string output = "__MARKER__content after";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MARKER__");
        // before is empty, after starts with non-newline content => prepends \r\n
        Assert.Equal("\r\ncontent after", result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerOnly_ReturnsEmpty()
    {
        string result = TerminalDirectoryGuard.RemoveMarkerLine("__MARKER__", "__MARKER__");
        Assert.Equal("", result);
    }

    [Fact]
    public void TryExtractPromptDirectory_WhitespaceBetweenMarkerAndDirectory_Skips()
    {
        string output = "__MARKER__\r\n   C:\\Users\\test> ";
        Assert.True(TerminalDirectoryGuard.TryExtractPromptDirectory(output, "__MARKER__", out string dir));
        Assert.Equal(@"C:\Users\test", dir);
    }

    [Fact]
    public void TryExtractPromptDirectory_PromptImmediatelyAfterMarker_Works()
    {
        // No whitespace between marker end and directory
        string output = "__MARKER__C:\\src> ";
        Assert.True(TerminalDirectoryGuard.TryExtractPromptDirectory(output, "__MARKER__", out string dir));
        Assert.Equal(@"C:\src", dir);
    }

    [Fact]
    public void TryExtractPromptDirectory_DirectoryIsWhitespaceOnly_ReturnsFalse()
    {
        // After whitespace skip, cursor is at '>', so promptEnd <= cursor
        string output = "__MARKER__   > ";
        Assert.False(TerminalDirectoryGuard.TryExtractPromptDirectory(output, "__MARKER__", out _));
    }

    [Fact]
    public void DirectoriesEqual_InvalidPath_ReturnsFalse()
    {
        // Invalid path triggers catch in NormalizeDirectoryForComparison → empty → false
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual("C:\\valid", "?:\0bad"));
    }
}