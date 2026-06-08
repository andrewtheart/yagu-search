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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}