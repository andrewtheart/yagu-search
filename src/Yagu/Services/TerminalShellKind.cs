using System.Text;

namespace Yagu.Services;

/// <summary>Which shell backs the embedded terminal pane.</summary>
internal enum TerminalShellKind
{
    Cmd = 0,
    PowerShell = 1,
}

/// <summary>
/// Helpers for launching and identifying the embedded terminal's shell. The PowerShell backend
/// is a no-echo REPL launched via <c>-EncodedCommand</c> so it is display-compatible with the
/// <c>cmd /Q</c> path: the shell never echoes the submitted command (the terminal frontend does
/// the single local echo), non-terminating errors render as clean text instead of CLIXML, and
/// the prompt tracks the current directory just like cmd's <c>$P$G</c> prompt.
///
/// To make interactive prompts visible (mandatory-parameter binding such as bare
/// <c>Get-Item</c>, <c>Read-Host</c>, choice prompts) the REPL runs each command in a child
/// runspace bound to a custom <see cref="System.Management.Automation.Host.PSHost"/> whose UI
/// routes prompts and output through the process's redirected <c>stdout</c>/<c>stdin</c> instead
/// of the raw Win32 console (which is lost under pipe redirection). Without this, a cmdlet that
/// needs a mandatory parameter would silently block forever because the "Supply values for the
/// following parameters" prompt is written to a console that does not exist. If the custom host
/// cannot be compiled at runtime the REPL degrades to a simple in-process loop.
/// </summary>
internal static class TerminalShell
{
    /// <summary>Maps the persisted settings index (0 = cmd, 1 = PowerShell) to a shell kind.</summary>
    public static TerminalShellKind FromSettingsIndex(int index)
        => index == 1 ? TerminalShellKind.PowerShell : TerminalShellKind.Cmd;

    /// <summary>Maps a shell kind back to its persisted settings index.</summary>
    public static int ToSettingsIndex(TerminalShellKind kind)
        => kind == TerminalShellKind.PowerShell ? 1 : 0;

    /// <summary>Clamps an arbitrary persisted value to a supported shell index.</summary>
    public static int NormalizeSettingsIndex(int index)
        => index == 1 ? 1 : 0;

    // A custom PSHost whose UI writes prompts and output to the process's stdout and reads answers
    // from stdin. Running user commands in a runspace bound to this host is what makes interactive
    // prompts (mandatory parameters, Read-Host, choice prompts) visible under pipe redirection, and
    // renders non-terminating errors as clean text instead of CLIXML. Embedded as base64 so the
    // PowerShell launcher script never has to escape the C# here-string.
    private const string PowerShellHostSource = """
        using System;
        using System.Collections.Generic;
        using System.Collections.ObjectModel;
        using System.Globalization;
        using System.Management.Automation;
        using System.Management.Automation.Host;
        using System.Security;

        public class YaguRawUi : PSHostRawUserInterface {
            public override ConsoleColor ForegroundColor { get; set; }
            public override ConsoleColor BackgroundColor { get; set; }
            public override Coordinates CursorPosition { get; set; }
            public override Coordinates WindowPosition { get; set; }
            public override int CursorSize { get; set; }
            public override Size BufferSize { get; set; }
            public override Size WindowSize { get; set; }
            public override Size MaxWindowSize { get { return new Size(120, 50); } }
            public override Size MaxPhysicalWindowSize { get { return new Size(120, 50); } }
            public override string WindowTitle { get; set; }
            public override bool KeyAvailable { get { return false; } }
            public YaguRawUi() {
                BufferSize = new Size(120, 9999);
                WindowSize = new Size(120, 50);
                CursorPosition = new Coordinates(0, 0);
                WindowPosition = new Coordinates(0, 0);
                ForegroundColor = ConsoleColor.Gray;
                BackgroundColor = ConsoleColor.Black;
                WindowTitle = "Yagu";
            }
            public override void FlushInputBuffer() { }
            public override BufferCell[,] GetBufferContents(Rectangle r) { return new BufferCell[0, 0]; }
            public override KeyInfo ReadKey(ReadKeyOptions options) { return new KeyInfo(0, ' ', 0, false); }
            public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
            public override void SetBufferContents(Rectangle r, BufferCell fill) { }
            public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
        }

        public class YaguHostUi : PSHostUserInterface {
            private YaguRawUi _raw = new YaguRawUi();
            public override PSHostRawUserInterface RawUI { get { return _raw; } }
            private static string Clean(string label) {
                if (string.IsNullOrEmpty(label)) return label;
                return label.Replace("&", string.Empty);
            }
            public override void Write(string value) { Console.Out.Write(value); }
            public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { Console.Out.Write(value); }
            public override void WriteLine() { Console.Out.WriteLine(); }
            public override void WriteLine(string value) { Console.Out.WriteLine(value); }
            public override void WriteLine(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { Console.Out.WriteLine(value); }
            public override void WriteErrorLine(string value) { Console.Out.WriteLine(value); }
            public override void WriteDebugLine(string message) { Console.Out.WriteLine("DEBUG: " + message); }
            public override void WriteVerboseLine(string message) { Console.Out.WriteLine("VERBOSE: " + message); }
            public override void WriteWarningLine(string message) { Console.Out.WriteLine("WARNING: " + message); }
            public override void WriteProgress(long sourceId, ProgressRecord record) { }
            public override string ReadLine() { return Console.In.ReadLine(); }
            public override SecureString ReadLineAsSecureString() {
                var s = new SecureString();
                string line = Console.In.ReadLine();
                if (line != null) { foreach (char c in line) { s.AppendChar(c); } }
                s.MakeReadOnly();
                return s;
            }
            public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) {
                if (!string.IsNullOrEmpty(caption)) { Console.Out.WriteLine(caption); }
                if (!string.IsNullOrEmpty(message)) { Console.Out.WriteLine(message); }
                var results = new Dictionary<string, PSObject>();
                foreach (var fd in descriptions) {
                    string label = string.IsNullOrEmpty(fd.Label) ? fd.Name : Clean(fd.Label);
                    Console.Out.Write(label + ": ");
                    Console.Out.Flush();
                    string answer = Console.In.ReadLine();
                    results[fd.Name] = answer == null ? null : PSObject.AsPSObject(answer);
                }
                return results;
            }
            public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) {
                if (!string.IsNullOrEmpty(caption)) { Console.Out.WriteLine(caption); }
                if (!string.IsNullOrEmpty(message)) { Console.Out.WriteLine(message); }
                return defaultChoice < 0 ? 0 : defaultChoice;
            }
            public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) { return null; }
            public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) { return null; }
        }

        public class YaguHost : PSHost {
            private YaguHostUi _ui = new YaguHostUi();
            private Guid _id = Guid.NewGuid();
            public override string Name { get { return "YaguTerminalHost"; } }
            public override Version Version { get { return new Version(1, 0); } }
            public override Guid InstanceId { get { return _id; } }
            public override PSHostUserInterface UI { get { return _ui; } }
            public override CultureInfo CurrentCulture { get { return CultureInfo.CurrentCulture; } }
            public override CultureInfo CurrentUICulture { get { return CultureInfo.CurrentUICulture; } }
            public override void SetShouldExit(int exitCode) { }
            public override void EnterNestedPrompt() { }
            public override void ExitNestedPrompt() { }
            public override void NotifyBeginApplication() { }
            public override void NotifyEndApplication() { }
        }
        """;

    // The launcher script. It compiles the custom host (base64-decoded above), then runs a REPL
    // that prints its own "PS <cwd>> " prompt (identical to the cmd path so the directory guard and
    // completion keep working) and dot-sources each line in a host-bound runspace so assignments and
    // "cd" persist and interactive prompts surface on stdout. If the host cannot be compiled it falls
    // back to a simple in-process loop that at least runs commands and renders errors as clean text.
    private const string PowerShellReplTemplate = """
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}
        try { [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}
        $useYaguHost = $false
        try {
          $src = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__YAGU_HOST_B64__'))
          Add-Type -TypeDefinition $src -ReferencedAssemblies 'System.Management.Automation' -ErrorAction Stop
          $useYaguHost = $true
        } catch { $useYaguHost = $false }
        if ($useYaguHost) {
          $yaguHost = New-Object YaguHost
          $rs = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($yaguHost)
          $rs.Open()
          $wrapper = '. ([ScriptBlock]::Create($__yaguLine)) 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ } } | Out-Default'
          while ($true) {
            $loc = $rs.SessionStateProxy.Path.CurrentLocation.Path
            [Console]::Out.Write('PS ' + $loc + '> ')
            [Console]::Out.Flush()
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            if ($line.Trim().Length -eq 0) { continue }
            $rs.SessionStateProxy.SetVariable('__yaguLine', $line)
            $psx = [PowerShell]::Create()
            $psx.Runspace = $rs
            [void]$psx.AddScript($wrapper)
            try { [void]$psx.Invoke() } catch { [Console]::Out.WriteLine($_.Exception.Message) }
            $psx.Dispose()
          }
        } else {
          while ($true) {
            [Console]::Out.Write('PS ' + (Get-Location).Path + '> ')
            [Console]::Out.Flush()
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            try {
              & ([ScriptBlock]::Create($line)) 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ }
              } | Out-Default
            } catch {
              if ($_.Exception.InnerException) { $_.Exception.InnerException.Message | Out-Default }
              else { $_.ToString() | Out-Default }
            }
          }
        }
        """;

    /// <summary>The PowerShell REPL script embedded via <c>-EncodedCommand</c>.</summary>
    public static string PowerShellReplScript
        => PowerShellReplTemplate.Replace(
            "__YAGU_HOST_B64__",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(PowerShellHostSource)),
            StringComparison.Ordinal);

    /// <summary>Encodes a PowerShell script for the <c>-EncodedCommand</c> argument (base64 UTF-16LE).</summary>
    public static string EncodePowerShellCommand(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
