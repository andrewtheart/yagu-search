namespace Yagu.Tests;

public sealed class CliRunnerRegressionTests
{
    [Fact]
    public void CliSearch_ForcesConsoleLoggingToCritical()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        Assert.Contains("LogService.Init((LogLevel)settings.LogLevelIndex, LogLevel.Critical);", source);
        Assert.DoesNotContain("LogService.Init((LogLevel)settings.LogLevelIndex, (LogLevel)settings.ConsoleLogLevelIndex);", source);
    }

    [Fact]
    public void CliParser_RecognizesDashHelpAlias()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        Assert.Contains("Eq(tok, \"--help\", \"-help\"", source);
    }

    [Fact]
    public void ProgramHelpShortcut_ExitsProcessAfterPrintingHelp()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Program.cs"));

        Assert.Matches("CliRunner\\.RunHelp\\(\\);\\s*Environment\\.Exit\\(0\\);", source);
    }

    [Fact]
    public void YaguExecutable_UsesConsoleSubsystemForCliHelp()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Yagu.csproj"));

        Assert.Contains("<OutputType>Exe</OutputType>", source);
        Assert.DoesNotContain("<OutputType>WinExe</OutputType>", source);
    }

    [Fact]
    public void ProgramGuiMode_RelaunchesDetachedBeforeStartingWinUi()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Program.cs"));

        Assert.Contains("TryRelaunchDetachedGui(args)", source);
        Assert.Contains("CreateNoWindow = true", source);
        Assert.Contains("FreeConsole();", source);
    }

    [Fact]
    public void CliHelp_IncludesTwoHundredExamples()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        int exampleCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+\d{3}\. ").Count;
        int explanationCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+Does: ").Count;
        int commandCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+Cmd:\s+Yagu\.exe --cli ").Count;

        Assert.Equal(200, exampleCount);
        Assert.Equal(200, explanationCount);
        Assert.Equal(200, commandCount);
        Assert.Contains("EXAMPLES (200):", source);
        Assert.Contains("001. Basic search in the current folder", source);
        Assert.Contains("Does: Finds TODO anywhere under the current directory.", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory . \"TODO\"", source);
        Assert.Contains("100. Search for API key patterns", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory . -e \"api[_-]?key\"", source);
        Assert.Contains("200. Search source and write a compact audit", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory src \"TODO\" -g \"*.cs\" --export .\\reports\\todo-audit.json --export-no-markers", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}