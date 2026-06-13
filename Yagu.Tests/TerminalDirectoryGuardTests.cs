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
}