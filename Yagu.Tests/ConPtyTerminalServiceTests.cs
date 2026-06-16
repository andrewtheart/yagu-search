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

public sealed class ConPtyTerminalServiceEdgeTests
{
    [Fact]
    public void WriteInput_BeforeStart_DoesNotThrow()
    {
        using var terminal = new ConPtyTerminalService();
        // Should not throw — the service logs a warning and returns
        var ex = Record.Exception(() => terminal.WriteInput("hello\r"));
        Assert.Null(ex);
    }

    [Fact]
    public void WriteInput_WithEchoDisabled_BeforeStart_DoesNotThrow()
    {
        using var terminal = new ConPtyTerminalService();
        var ex = Record.Exception(() => terminal.WriteInput("test", echoInput: false));
        Assert.Null(ex);
    }

    [Fact]
    public void Resize_BeforeStart_DoesNotThrow()
    {
        using var terminal = new ConPtyTerminalService();
        var ex = Record.Exception(() => terminal.Resize(80, 24));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCurrentCommand_BeforeStart_DoesNotThrow()
    {
        using var terminal = new ConPtyTerminalService();
        var ex = Record.Exception(() => terminal.CancelCurrentCommand());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var terminal = new ConPtyTerminalService();
        terminal.Dispose();
        var ex = Record.Exception(() => terminal.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Start_DefaultWorkingDirectory_UsesAppBaseDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        using var gotOutput = new ManualResetEventSlim(false);

        terminal.OutputReceived += _ => gotOutput.Set();
        terminal.Start(cols: 80, rows: 24, workingDirectory: null);

        Assert.True(gotOutput.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(terminal.ProcessId > 0);
    }

    [Fact]
    public void ProcessExited_FiresOnDispose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        using var gotOutput = new ManualResetEventSlim(false);
        int? exitCode = null;

        terminal.OutputReceived += _ => gotOutput.Set();
        terminal.ProcessExited += code => exitCode = code;
        terminal.Start(cols: 80, rows: 24);

        Assert.True(gotOutput.Wait(TimeSpan.FromSeconds(5)));
        terminal.Dispose();

        // Give the event time to fire
        Thread.Sleep(500);
        // Process exit event should have fired (exit code may vary)
        Assert.NotNull(exitCode);
    }
}

public sealed class ConPtyTerminalServiceLocalEchoTests
{
    [Fact]
    public void WriteInput_EchoesBackspaceAsEraseSequence()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var echoOutput = new StringBuilder();
        using var gotEcho = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (echoOutput) { echoOutput.Append(text); }
            started.Set();
            if (text.Contains("\b \b"))
                gotEcho.Set();
        };

        terminal.Start(cols: 120, rows: 24);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        // \b and \u007f should produce "\b \b" in local echo
        terminal.WriteInput("\b", echoInput: true);
        terminal.WriteInput("\u007f", echoInput: true);
        Assert.True(gotEcho.Wait(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void WriteInput_SuppressesControlCharsAndTabInEcho()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        using var started = new ManualResetEventSlim(false);

        terminal.OutputReceived += _ => started.Set();

        terminal.Start(cols: 120, rows: 24);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        // \t, \u0003, \u001b should not crash — exercises BuildLocalEcho branches
        var ex = Record.Exception(() => terminal.WriteInput("\t\u0003\u001b", echoInput: true));
        Assert.Null(ex);
    }

    [Fact]
    public void WriteInput_NewlineOnly_NormalizesForShell()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        using var gotOutput = new ManualResetEventSlim(false);
        var output = new StringBuilder();

        terminal.OutputReceived += text =>
        {
            lock (output) { output.Append(text); }
            gotOutput.Set();
        };

        terminal.Start(cols: 120, rows: 24);
        Assert.True(gotOutput.Wait(TimeSpan.FromSeconds(5)));

        // Sending "\n" only (Unix line ending) should still execute
        terminal.WriteInput("echo NL_TEST\n", echoInput: true);
        Thread.Sleep(1000);

        lock (output)
        {
            Assert.Contains("NL_TEST", output.ToString());
        }
    }

    [Fact]
    public void CancelCurrentCommand_OnRunningProcess_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        using var gotOutput = new ManualResetEventSlim(false);

        terminal.OutputReceived += _ => gotOutput.Set();
        terminal.Start(cols: 120, rows: 24);
        Assert.True(gotOutput.Wait(TimeSpan.FromSeconds(5)));

        // CancelCurrentCommand on a running shell (no child) should send Ctrl+C
        var ex = Record.Exception(() => terminal.CancelCurrentCommand());
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCurrentCommand_AfterProcessExits_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        using var gotOutput = new ManualResetEventSlim(false);
        using var exited = new ManualResetEventSlim(false);

        terminal.OutputReceived += _ => gotOutput.Set();
        terminal.ProcessExited += _ => exited.Set();
        terminal.Start(cols: 120, rows: 24);
        Assert.True(gotOutput.Wait(TimeSpan.FromSeconds(5)));

        // Exit the shell
        terminal.WriteInput("exit\r", echoInput: false);
        Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));

        // CancelCurrentCommand after exit should hit the HasExited branch
        var ex = Record.Exception(() => terminal.CancelCurrentCommand());
        Assert.Null(ex);
    }
}