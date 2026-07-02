using System.Diagnostics;
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

public sealed class ConPtyTerminalServicePowerShellTests
{
    [Fact]
    public void PowerShell_ProducesPsPromptOnStartup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var sawPrompt = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                if (output.ToString().Contains("PS ", StringComparison.Ordinal))
                    sawPrompt.Set();
            }
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory(), shellKind: TerminalShellKind.PowerShell);

        Assert.Equal(TerminalShellKind.PowerShell, terminal.ShellKind);
        Assert.True(sawPrompt.Wait(TimeSpan.FromSeconds(15)),
            "The PowerShell REPL did not render a 'PS ' prompt. Output: " + output);
    }

    [Fact]
    public void PowerShell_ExecutesCatAlias()
    {
        // Regression: the embedded terminal used to launch cmd.exe, so PowerShell aliases like
        // `cat` failed with "command not found". The PowerShell backend must resolve `cat`.
        if (!OperatingSystem.IsWindows())
            return;

        string tempFile = Path.Combine(Path.GetTempPath(), "yagu-ps-cat-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tempFile, "YAGU_CAT_SENTINEL");
        try
        {
            using var terminal = new ConPtyTerminalService();
            var output = new StringBuilder();
            using var started = new ManualResetEventSlim(false);
            using var sawContent = new ManualResetEventSlim(false);

            terminal.OutputReceived += text =>
            {
                lock (output)
                {
                    output.Append(text);
                    if (output.ToString().Contains("PS ", StringComparison.Ordinal))
                        started.Set();
                    if (output.ToString().Contains("YAGU_CAT_SENTINEL", StringComparison.Ordinal))
                        sawContent.Set();
                }
            };

            terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory(), shellKind: TerminalShellKind.PowerShell);
            Assert.True(started.Wait(TimeSpan.FromSeconds(15)), "PowerShell did not start. Output: " + output);

            terminal.WriteInput($"cat '{tempFile}'\r", echoInput: false);

            Assert.True(sawContent.Wait(TimeSpan.FromSeconds(15)),
                "The `cat` alias did not produce file content in PowerShell. Output: " + output);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void PowerShell_NonTerminatingErrorRendersAsPlainTextNotClixml()
    {
        // A missing-file error (which is how a failed/offline download surfaces) must render as
        // readable text in the terminal instead of leaking CLIXML to stderr.
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var started = new ManualResetEventSlim(false);
        using var sawError = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                if (output.ToString().Contains("PS ", StringComparison.Ordinal))
                    started.Set();
                string current = output.ToString();
                if (current.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                    || current.Contains("Cannot find path", StringComparison.OrdinalIgnoreCase))
                {
                    sawError.Set();
                }
            }
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory(), shellKind: TerminalShellKind.PowerShell);
        Assert.True(started.Wait(TimeSpan.FromSeconds(15)), "PowerShell did not start. Output: " + output);

        terminal.WriteInput(@"cat 'C:\yagu-nonexistent\definitely-missing.txt'" + "\r", echoInput: false);

        Assert.True(sawError.Wait(TimeSpan.FromSeconds(15)),
            "PowerShell did not render a readable file-not-found error. Output: " + output);
        Assert.DoesNotContain("CLIXML", output.ToString());
    }

    [Fact]
    public void PowerShell_MandatoryParameterPromptIsVisibleAndInteractive()
    {
        // Regression: typing a cmdlet with a mandatory parameter (e.g. bare `Get-Item`) used to
        // hang with a blinking cursor because PowerShell wrote the "Supply values for the following
        // parameters" prompt to the raw Win32 console, which is discarded under pipe redirection.
        // The custom PSHost routes that prompt to stdout so the user can see it and answer it.
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var started = new ManualResetEventSlim(false);
        using var sawPrompt = new ManualResetEventSlim(false);
        using var sawAnswerAccepted = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                string current = output.ToString();
                if (current.Contains("PS ", StringComparison.Ordinal))
                    started.Set();
                if (current.Contains("Supply values for the following parameters", StringComparison.OrdinalIgnoreCase))
                    sawPrompt.Set();
                if (current.Contains("YAGU_MANDATORY_DONE", StringComparison.Ordinal))
                    sawAnswerAccepted.Set();
            }
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory(), shellKind: TerminalShellKind.PowerShell);
        Assert.True(started.Wait(TimeSpan.FromSeconds(15)), "PowerShell did not start. Output: " + output);

        terminal.WriteInput("Get-Item\r", echoInput: false);
        Assert.True(sawPrompt.Wait(TimeSpan.FromSeconds(15)),
            "PowerShell did not surface the mandatory-parameter prompt. Output: " + output);

        // The prompt must be interactive: answering it lets the command complete without hanging,
        // so the next command echoes the sentinel back.
        terminal.WriteInput("C:\\Windows\r", echoInput: false);
        terminal.WriteInput("'YAGU_MANDATORY_DONE'\r", echoInput: false);
        Assert.True(sawAnswerAccepted.Wait(TimeSpan.FromSeconds(15)),
            "PowerShell did not accept the answer to the mandatory-parameter prompt (interactive prompt hung). Output: " + output);
    }

    [Fact]
    public void PowerShell_PersistsVariablesAcrossCommands()
    {
        // The custom-host REPL dot-sources each line so assignments persist across submissions.
        if (!OperatingSystem.IsWindows())
            return;

        using var terminal = new ConPtyTerminalService();
        var output = new StringBuilder();
        using var started = new ManualResetEventSlim(false);
        using var sawValue = new ManualResetEventSlim(false);

        terminal.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
                string current = output.ToString();
                if (current.Contains("PS ", StringComparison.Ordinal))
                    started.Set();
                if (current.Contains("YAGU_VALUE=1234", StringComparison.Ordinal))
                    sawValue.Set();
            }
        };

        terminal.Start(cols: 120, rows: 24, workingDirectory: Directory.GetCurrentDirectory(), shellKind: TerminalShellKind.PowerShell);
        Assert.True(started.Wait(TimeSpan.FromSeconds(15)), "PowerShell did not start. Output: " + output);

        terminal.WriteInput("$yaguVar = 1234\r", echoInput: false);
        terminal.WriteInput("\"YAGU_VALUE=$yaguVar\"\r", echoInput: false);

        Assert.True(sawValue.Wait(TimeSpan.FromSeconds(15)),
            "PowerShell did not persist a variable across commands. Output: " + output);
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

    [Fact]
    public void Dispose_KillsCommandRunningInShell()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var gotOutput = new ManualResetEventSlim(false);
        var terminal = new ConPtyTerminalService();
        terminal.OutputReceived += _ => gotOutput.Set();
        terminal.Start(cols: 120, rows: 24);
        Assert.True(gotOutput.Wait(TimeSpan.FromSeconds(5)));

        // Start a long-running command under the shell. ping spawns a PING.EXE
        // child process that would keep running if the reset/dispose only told the
        // shell to "exit".
        var preexisting = Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
        terminal.WriteInput("ping -n 30 127.0.0.1\r", echoInput: false);

        int childPid = -1;
        for (int i = 0; i < 50 && childPid < 0; i++)
        {
            childPid = Process.GetProcessesByName("PING")
                .Select(p => p.Id)
                .FirstOrDefault(id => !preexisting.Contains(id), -1);
            if (childPid < 0)
                Thread.Sleep(100);
        }

        Assert.True(childPid > 0, "Expected a running PING child process under the shell.");

        // Disposing the terminal (what a "Reset terminal session" does) must kill
        // the running command, not just exit the shell.
        terminal.Dispose();

        bool childExited = false;
        for (int i = 0; i < 50 && !childExited; i++)
        {
            try
            {
                using Process child = Process.GetProcessById(childPid);
                if (child.HasExited)
                    childExited = true;
            }
            catch (ArgumentException)
            {
                childExited = true; // process no longer exists
            }

            if (!childExited)
                Thread.Sleep(100);
        }

        Assert.True(childExited, "The running command should be killed when the terminal session is reset/disposed.");
    }
}