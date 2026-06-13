using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ConPtyTerminalServiceTests
{
    [Fact]
    public void CommandShell_VerifiesChangedDirectoryBeforeGeneratedCommandInsertion()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string tempDirectory = Path.Combine(Path.GetTempPath(), "yagu-terminal-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using var terminal = new ConPtyTerminalService();
            var output = new StringBuilder();
            string marker = TerminalDirectoryGuard.CreateMarker();
            using var verifiedDirectory = new ManualResetEventSlim(false);
            using var receivedAnyOutput = new ManualResetEventSlim(false);

            terminal.OutputReceived += text =>
            {
                lock (output)
                {
                    output.Append(text);
                    if (TerminalDirectoryGuard.TryExtractPromptDirectory(output.ToString(), marker, out string actualDirectory)
                        && TerminalDirectoryGuard.DirectoriesEqual(tempDirectory, actualDirectory))
                    {
                        verifiedDirectory.Set();
                    }
                }

                receivedAnyOutput.Set();
            };

            terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory());
            Assert.True(receivedAnyOutput.Wait(TimeSpan.FromSeconds(5)), "The command shell did not produce any startup output.");

            terminal.WriteInput(TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand(tempDirectory, marker) + "\r", echoInput: false);

            Assert.True(verifiedDirectory.Wait(TimeSpan.FromSeconds(5)),
                "The command shell did not prove the generated-command cwd guard changed directories. Output: " + output + " Hex: " + ToHex(output.ToString()));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CommandShell_ReceivesInputAndReturnsCommandOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var receivedExpectedOutput = new ManualResetEventSlim(false);
        using var receivedAnyOutput = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
            }

            receivedAnyOutput.Set();
            if (text.Contains("YAGU_OK", StringComparison.OrdinalIgnoreCase))
                receivedExpectedOutput.Set();
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory());
        Assert.True(receivedAnyOutput.Wait(TimeSpan.FromSeconds(5)), "The command shell did not produce any startup output.");

        terminal.WriteInput("echo YAGU_OK\r");

        Assert.True(receivedExpectedOutput.Wait(TimeSpan.FromSeconds(5)),
            "The command shell did not echo command output after terminal input. Output: " + output + " Hex: " + ToHex(output.ToString()));
    }

    [Fact]
    public void CommandShell_ExecutesCommandWhenEnterSendsCarriageReturnOnly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // xterm sends a lone '\r' for Enter, but the redirected cmd.exe stdin pipe
        // only executes a line once it receives a newline. This verifies the service
        // normalizes the line ending so the command actually runs (rather than the
        // text merely appearing via local echo). "42" only appears if cmd evaluated
        // the expression, since it is not part of the typed command text.
        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var receivedComputedResult = new ManualResetEventSlim(false);
        using var receivedAnyOutput = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
            }

            receivedAnyOutput.Set();
            lock (output)
            {
                if (output.ToString().Contains("42", StringComparison.Ordinal))
                    receivedComputedResult.Set();
            }
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory());
        Assert.True(receivedAnyOutput.Wait(TimeSpan.FromSeconds(5)), "The command shell did not produce any startup output.");

        // A lone carriage return must still execute the command.
        terminal.WriteInput("set /a 6*7\r");

        Assert.True(receivedComputedResult.Wait(TimeSpan.FromSeconds(5)),
            "The command shell did not execute the command after a carriage-return Enter. Output: " + output + " Hex: " + ToHex(output.ToString()));
    }

    [Fact]
    public void CommandShell_CanExecuteInputWithoutLocalEcho()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var receivedExpectedOutput = new ManualResetEventSlim(false);
        using var receivedAnyOutput = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
            }

            receivedAnyOutput.Set();
            if (text.Contains("HIDDEN_ECHO_SENTINEL", StringComparison.OrdinalIgnoreCase))
                receivedExpectedOutput.Set();
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory());
        Assert.True(receivedAnyOutput.Wait(TimeSpan.FromSeconds(5)), "The command shell did not produce any startup output.");

        terminal.WriteInput("echo HIDDEN_ECHO_SENTINEL\r", echoInput: false);

        Assert.True(receivedExpectedOutput.Wait(TimeSpan.FromSeconds(5)),
            "The command shell did not execute hidden terminal input. Output: " + output + " Hex: " + ToHex(output.ToString()));

        Assert.DoesNotContain("echo HIDDEN_ECHO_SENTINEL", output.ToString());
    }

    private static string ToHex(string value)
    {
        return string.Join(' ', value.Select(c => ((int)c).ToString("X4")));
    }
}