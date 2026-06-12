namespace Yagu.Tests;

public sealed class TerminalWarningRegressionTests
{
    [Fact]
    public void TerminalService_UsesRedirectedShellAndScopedCleanup()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "ConPtyTerminalService.cs"));

        Assert.Contains("RedirectStandardInput = true", source);
        Assert.Contains("RedirectStandardOutput = true", source);
        Assert.Contains("RedirectStandardError = true", source);
        Assert.Contains("startInfo.ArgumentList.Add(\"/Q\");", source);
        Assert.Contains("BuildLocalEcho(text)", source);
        Assert.Contains("_process.Kill(entireProcessTree: true);", source);
        Assert.DoesNotContain("Stop-Process", source);
        Assert.DoesNotContain("taskkill", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerminalJsonEscaping_FormatsUnicodeEscapesInvariantly()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));

        Assert.Contains("System.Globalization.CultureInfo.InvariantCulture", source);
        Assert.Contains("ToString(\"X4\", System.Globalization.CultureInfo.InvariantCulture)", source);
        Assert.DoesNotContain("sb.Append($\"\\\\u{(int)c:X4}\");", source);
    }

    [Fact]
    public void TypedClsCommand_ClearsTerminalScrollback()
    {
        string terminalSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));
        string terminalHtml = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Assets", "terminal.html"));

        Assert.Contains("TrackTerminalInputForClearCommand(data)", terminalSource);
        Assert.Contains("IsTerminalClearCommand", terminalSource);
        Assert.Contains("ScheduleTerminalScrollbackClear();", terminalSource);
        Assert.Contains("PostTerminalClearToWebView();", terminalSource);
        Assert.Contains("PostWebMessageAsJson(\"{\\\"type\\\":\\\"clear\\\"}\")", terminalSource);
        Assert.Contains("msg.type === 'clear'", terminalHtml);
        Assert.Contains("term.clear();", terminalHtml);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}