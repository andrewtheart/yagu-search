using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class TerminalShellTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    [InlineData(99, 0)]
    public void FromSettingsIndex_MapsOnlyOneToPowerShell(int index, int expectedIndex)
    {
        Assert.Equal(expectedIndex, TerminalShell.ToSettingsIndex(TerminalShell.FromSettingsIndex(index)));
    }

    [Fact]
    public void ToSettingsIndex_RoundTripsShellKind()
    {
        Assert.Equal(0, TerminalShell.ToSettingsIndex(TerminalShellKind.Cmd));
        Assert.Equal(1, TerminalShell.ToSettingsIndex(TerminalShellKind.PowerShell));
        Assert.Equal(TerminalShellKind.Cmd, TerminalShell.FromSettingsIndex(0));
        Assert.Equal(TerminalShellKind.PowerShell, TerminalShell.FromSettingsIndex(1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-5, 0)]
    [InlineData(2, 0)]
    public void NormalizeSettingsIndex_ClampsToSupportedValues(int index, int expected)
    {
        Assert.Equal(expected, TerminalShell.NormalizeSettingsIndex(index));
    }

    [Fact]
    public void PrefixCurrentDirectoryExecutable_PowerShell_PrefixesBareExeWithDotSlash()
    {
        // PowerShell will not run "Yagu.exe" from the CWD; it needs ".\Yagu.exe".
        const string command = "Yagu.exe --cli --directory C:\\ --pattern foo";
        Assert.Equal(
            ".\\Yagu.exe --cli --directory C:\\ --pattern foo",
            TerminalShell.PrefixCurrentDirectoryExecutable(command, TerminalShellKind.PowerShell));
    }

    [Fact]
    public void PrefixCurrentDirectoryExecutable_Cmd_LeavesBareExeUnchanged()
    {
        // cmd.exe resolves a bare exe name from the current directory, so no prefix is needed.
        const string command = "Yagu.exe --cli --directory C:\\ --pattern foo";
        Assert.Equal(command, TerminalShell.PrefixCurrentDirectoryExecutable(command, TerminalShellKind.Cmd));
    }

    [Theory]
    [InlineData(".\\Yagu.exe --cli")]           // already relative
    [InlineData("\"Yagu.exe\" --cli")]          // quoted
    [InlineData("C:\\Program Files\\Yagu\\Yagu.exe --cli")] // rooted path
    [InlineData("& Yagu.exe --cli")]            // call operator already present
    [InlineData("Get-ChildItem -Recurse")]      // not an exe (a cmdlet)
    [InlineData("")]                             // empty
    public void PrefixCurrentDirectoryExecutable_PowerShell_LeavesNonBareExeUnchanged(string command)
    {
        Assert.Equal(command, TerminalShell.PrefixCurrentDirectoryExecutable(command, TerminalShellKind.PowerShell));
    }

    [Fact]
    public void PowerShellReplScript_IsANoEchoLineReader()
    {
        string script = TerminalShell.PowerShellReplScript;

        // The REPL must read stdin itself (no shell echo) and capture non-terminating errors as text.
        Assert.Contains("[Console]::In.ReadLine()", script);
        Assert.Contains("[ScriptBlock]::Create($line)", script);
        Assert.Contains("2>&1", script);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", script);
    }

    [Fact]
    public void PowerShellReplScript_EmbedsCustomHostForInteractivePrompts()
    {
        string script = TerminalShell.PowerShellReplScript;

        // The base64 placeholder must be substituted (never leak into the launched script).
        Assert.DoesNotContain("__YAGU_HOST_B64__", script);
        // A custom PSHost-backed runspace is what makes mandatory-parameter / Read-Host prompts visible.
        Assert.Contains("Add-Type", script);
        Assert.Contains("RunspaceFactory", script);
        Assert.Contains("New-Object YaguHost", script);
        // Dot-sourcing persists variables and `cd` across submissions.
        Assert.Contains(". ([ScriptBlock]::Create($__yaguLine))", script);
    }

    [Fact]
    public void PowerShellReplScript_EmbeddedHostSourceDecodesToPsHostImplementation()
    {
        string script = TerminalShell.PowerShellReplScript;

        // Extract the embedded base64 host source and confirm it is a real PSHost implementation
        // that routes interactive prompts through the console streams.
        var match = System.Text.RegularExpressions.Regex.Match(
            script, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)");
        Assert.True(match.Success, "The REPL script did not embed a base64 host payload.");

        string hostSource = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));

        Assert.Contains("class YaguHost : PSHost", hostSource);
        Assert.Contains("public override Dictionary<string, PSObject> Prompt(", hostSource);
        Assert.Contains("Console.In.ReadLine()", hostSource);
        Assert.Contains("Console.Out.Write", hostSource);
    }

    [Fact]
    public void EncodePowerShellCommand_ProducesBase64Utf16LeThatDecodesBack()
    {
        const string script = "Write-Output 'hi'";

        string encoded = TerminalShell.EncodePowerShellCommand(script);
        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Equal(script, decoded);
    }
}
