using System.Diagnostics;
using System.Text;
using System.Xml;

namespace Yagu.Services.Mcp;

/// <summary>What the sign-in task currently looks like on this machine.</summary>
internal sealed record McpLoginHelperState(bool Registered, string? Detail = null);

/// <summary>
/// Registers the loopback HTTP server to start when this user signs in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A per-user scheduled task, running as the logged-in user.</b> Never a machine-wide LocalSystem
/// service: such a service would read files the user has no rights to — the SAM hive, other users'
/// profiles — and hand them to whichever client presented the loopback token, which is a local
/// privilege-escalation path that also quietly defeats the whole authorization model, because the OS
/// would no longer be enforcing the user's own permissions underneath it. It also has no coherent answer
/// to "whose token?" on a shared PC.
/// </para>
/// <para>
/// A scheduled task was chosen over the <c>Run</c> registry key because it is the only per-user
/// autostart mechanism with built-in <b>restart-on-failure backoff</b>, and because a user can see and
/// delete it in Task Scheduler without editing the registry. It is driven through <c>schtasks.exe</c>
/// rather than the Task Scheduler COM API, since Yagu is Native AOT and built-in COM is disabled.
/// </para>
/// </remarks>
/// <summary>Performs one <c>schtasks.exe</c> invocation and reports its outcome.</summary>
internal delegate (int ExitCode, string Output, string Error) SchtasksRunner(IReadOnlyList<string> arguments);

/// <summary>Starts one <c>schtasks.exe</c> child process, or returns null when the OS declines.</summary>
/// <remarks>Exists so tests can drive the process-plumbing arms without ever creating a real task.</remarks>
internal delegate ISchtasksProcess? SchtasksProcessStarter(ProcessStartInfo startInfo);

/// <summary>The slice of <see cref="Process"/> that the schtasks plumbing actually uses.</summary>
internal interface ISchtasksProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    bool WaitForExit(int milliseconds);
    void Kill(bool entireProcessTree);
}

internal static class McpLoginHelper
{
    /// <summary>Task name. Per user, so two accounts on one PC do not collide.</summary>
    public static string TaskName => $"Yagu MCP Server ({Environment.UserName})";

    private const int SchtasksTimeoutMs = 15_000;

    /// <summary>True when the sign-in task exists.</summary>
    public static McpLoginHelperState Query() => QueryWith(RunSchtasks);

    /// <summary>
    /// Creates or replaces the sign-in task so it launches the server with the given arguments.
    /// </summary>
    /// <remarks>
    /// The task definition is supplied as XML because the flags needed for a sane background helper —
    /// restart-on-failure, no execution time limit, run whether or not on battery — have no
    /// <c>schtasks /Create</c> switches. Arguments are XML-escaped; a path with an ampersand or a quote
    /// must not be able to alter the definition.
    /// </remarks>
    internal static McpLoginHelperState Register(
        string serverExePath,
        IReadOnlyList<string> arguments,
        SchtasksRunner run) =>
        RegisterWith(run, serverExePath, arguments);

    /// <summary>
    /// Removes the sign-in task and stops a running instance.
    /// </summary>
    /// <remarks>
    /// Called when the user turns the transport off and by the uninstaller: a helper that survives
    /// uninstall is a process pointing at a deleted executable, holding a port.
    /// </remarks>
    internal static McpLoginHelperState Unregister(SchtasksRunner run) =>
        UnregisterWith(run);

    /// <summary>
    /// True when the task has a running instance.
    /// </summary>
    /// <remarks>
    /// <c>schtasks</c> output is localized, so an unrecognized status reads as "not running". Callers
    /// therefore use this only to CONFIRM a failure, never to confirm success.
    /// </remarks>
    public static bool IsRunning() => IsRunningWith(RunSchtasks);

    /// <summary>
    /// Ends the current instance and starts a new one, so a changed grant takes effect immediately.
    /// </summary>
    /// <remarks>
    /// The server reads its allowed roots and capabilities from the command line it was started with, and
    /// <c>MultipleInstancesPolicy=IgnoreNew</c> means a fresh <c>/Run</c> alone is silently dropped while
    /// the old instance is alive. Without the <c>/End</c> first, removing a folder in Settings would keep
    /// serving it until the next sign-in.
    /// </remarks>
    internal static McpLoginHelperState Restart(SchtasksRunner run) =>
        RestartWith(run);

    // The schtasks invocation is a parameter below so the lifecycle rules — especially "only a CONFIRMED
    // still-running instance is a failure" — can be tested without creating a real task on the machine.
    // The task NAME is a parameter for the opposite reason: a test that needs the real scheduler can
    // register a throwaway name instead of replacing the user's own server task.

    internal static McpLoginHelperState QueryWith(SchtasksRunner run, string? taskName = null)
    {
        (int exitCode, string output, string error) = run(["/Query", "/TN", taskName ?? TaskName]);

        if (exitCode == 0)
            return new McpLoginHelperState(true);

        // schtasks reports a missing task as a failure, which is not an error condition for us.
        string detail = string.IsNullOrWhiteSpace(error) ? output : error;
        return new McpLoginHelperState(false, detail.Trim());
    }

    internal static McpLoginHelperState RegisterWith(
        SchtasksRunner run, string serverExePath, IReadOnlyList<string> arguments, string? taskName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExePath);
        ArgumentNullException.ThrowIfNull(arguments);

        string xmlPath = Path.Combine(Path.GetTempPath(), $"yagu-mcp-task-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks /Create /XML requires UTF-16 with a BOM; UTF-8 is rejected with a parse error.
            File.WriteAllText(xmlPath, BuildTaskXml(serverExePath, arguments), new UnicodeEncoding(false, true));

            (int exitCode, string output, string error) = run(
                ["/Create", "/TN", taskName ?? TaskName, "/XML", xmlPath, "/F"]);

            if (exitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                return new McpLoginHelperState(false, detail.Trim());
            }

            return new McpLoginHelperState(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new McpLoginHelperState(false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch (IOException) { }
        }
    }

    internal static McpLoginHelperState UnregisterWith(SchtasksRunner run, string? taskName = null)
    {
        (int endCode, string endOutput, string endError) = run(["/End", "/TN", taskName ?? TaskName]);

        // A non-zero /End usually just means nothing was running. Only a CONFIRMED still-running
        // instance is a failure, and it matters: the server would keep serving the revoked grant.
        if (endCode != 0 && IsRunningWith(run, taskName))
        {
            string endDetail = string.IsNullOrWhiteSpace(endError) ? endOutput : endError;
            return new McpLoginHelperState(true,
                $"the running server could not be stopped, so it keeps serving its current folders: {endDetail.Trim()}");
        }

        (int exitCode, string output, string error) = run(["/Delete", "/TN", taskName ?? TaskName, "/F"]);

        if (exitCode == 0)
            return new McpLoginHelperState(false);

        string detail = string.IsNullOrWhiteSpace(error) ? output : error;
        return new McpLoginHelperState(QueryWith(run, taskName).Registered, detail.Trim());
    }

    internal static bool IsRunningWith(SchtasksRunner run, string? taskName = null)
    {
        (int exitCode, string output, _) = run(["/Query", "/TN", taskName ?? TaskName, "/FO", "LIST", "/V"]);
        if (exitCode != 0)
            return false;

        foreach (string line in output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            if (!line[..colon].Trim().Equals("Status", StringComparison.OrdinalIgnoreCase))
                continue;

            return line[(colon + 1)..].Trim().StartsWith("Running", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    internal static McpLoginHelperState RunNowWith(SchtasksRunner run, string? taskName = null)
    {
        (int exitCode, string output, string error) = run(["/Run", "/TN", taskName ?? TaskName]);
        if (exitCode == 0)
            return new McpLoginHelperState(true);

        string detail = string.IsNullOrWhiteSpace(error) ? output : error;
        return new McpLoginHelperState(QueryWith(run, taskName).Registered, detail.Trim());
    }

    internal static McpLoginHelperState RestartWith(SchtasksRunner run, string? taskName = null)
    {
        (int endCode, string endOutput, string endError) = run(["/End", "/TN", taskName ?? TaskName]);

        // Reporting success without this check is how a revoked folder stays served: MultipleInstancesPolicy
        // is IgnoreNew, so /Run against a surviving instance succeeds while changing nothing.
        if (endCode != 0 && IsRunningWith(run, taskName))
        {
            string detail = string.IsNullOrWhiteSpace(endError) ? endOutput : endError;
            return new McpLoginHelperState(false,
                $"the running server could not be stopped, so it keeps its previous folders and capabilities: {detail.Trim()}");
        }

        return RunNowWith(run, taskName);
    }

    /// <summary>
    /// The Task Scheduler definition. Pure, so the escaping and the lifecycle flags are unit-testable
    /// without touching the machine's task store.
    /// </summary>
    public static string BuildTaskXml(string serverExePath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var xml = new StringBuilder();
        xml.AppendLine("""<?xml version="1.0" encoding="UTF-16"?>""");
        xml.AppendLine("""<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""");
        xml.AppendLine("  <RegistrationInfo>");
        xml.AppendLine($"    <Description>{Escape("Starts the Yagu MCP server on loopback HTTP for this user. Removing this task stops the server from starting at sign-in.")}</Description>");
        xml.AppendLine("  </RegistrationInfo>");

        xml.AppendLine("  <Triggers>");
        xml.AppendLine("    <LogonTrigger>");
        xml.AppendLine("      <Enabled>true</Enabled>");
        xml.AppendLine($"      <UserId>{Escape(CurrentUserId())}</UserId>");
        xml.AppendLine("    </LogonTrigger>");
        xml.AppendLine("  </Triggers>");

        // InteractiveToken: the task runs as the signed-in user with their own token, so the OS keeps
        // enforcing that user's file permissions underneath the authorization policy. No stored password.
        xml.AppendLine("  <Principals>");
        xml.AppendLine("""    <Principal id="Author">""");
        xml.AppendLine($"      <UserId>{Escape(CurrentUserId())}</UserId>");
        xml.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        xml.AppendLine("      <RunLevel>LeastPrivilege</RunLevel>");
        xml.AppendLine("    </Principal>");
        xml.AppendLine("  </Principals>");

        xml.AppendLine("  <Settings>");
        xml.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        // A background server must not be killed for running on battery or stopped after three days.
        xml.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        xml.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        xml.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        xml.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        xml.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        xml.AppendLine("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>");
        xml.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        xml.AppendLine("    <Enabled>true</Enabled>");
        xml.AppendLine("    <Hidden>false</Hidden>");
        xml.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        xml.AppendLine("    <WakeToRun>false</WakeToRun>");
        xml.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        xml.AppendLine("    <Priority>7</Priority>");
        // Crash recovery: retry every minute, up to three times, instead of leaving the port dead.
        xml.AppendLine("    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>");
        xml.AppendLine("  </Settings>");

        xml.AppendLine("  <Actions Context=\"Author\">");
        xml.AppendLine("    <Exec>");
        xml.AppendLine($"      <Command>{Escape(serverExePath)}</Command>");
        xml.AppendLine($"      <Arguments>{Escape(JoinArguments(arguments))}</Arguments>");
        string? workingDirectory = Path.GetDirectoryName(serverExePath);
        if (!string.IsNullOrEmpty(workingDirectory))
            xml.AppendLine($"      <WorkingDirectory>{Escape(workingDirectory)}</WorkingDirectory>");
        xml.AppendLine("    </Exec>");
        xml.AppendLine("  </Actions>");

        xml.AppendLine("</Task>");
        return xml.ToString();
    }

    /// <summary>
    /// Quotes each argument for a Windows command line. The task store holds one string, so this is the
    /// one place where <c>ArgumentList</c>-style safety has to be reproduced by hand.
    /// </summary>
    public static string JoinArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var line = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (line.Length > 0)
                line.Append(' ');

            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }

            line.Append('"');
            for (int i = 0; i < argument.Length; i++)
            {
                int backslashes = 0;
                while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }

                if (i == argument.Length)
                {
                    line.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    line.Append('\\', (backslashes * 2) + 1).Append('"');
                }
                else
                {
                    line.Append('\\', backslashes).Append(argument[i]);
                }
            }
            line.Append('"');
        }

        return line.ToString();
    }

    private static string CurrentUserId()
        => CurrentUserId(System.Security.Principal.WindowsIdentity.GetCurrent);

    /// <remarks>
    /// Takes the identity lookup so tests can prove task XML falls back to the domain\user form when
    /// WindowsIdentity is unavailable. That failure mode matters because a scheduler definition with no
    /// usable UserId is rejected, disabling the sign-in helper for otherwise valid users.
    /// </remarks>
    internal static string CurrentUserId(Func<System.Security.Principal.WindowsIdentity> getCurrentIdentity)
    {
        try
        {
            using var identity = getCurrentIdentity();
            return identity.User?.Value ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }

    private static string Escape(string value)
    {
        var settings = new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment };
        var text = new StringWriter();
        using (XmlWriter writer = XmlWriter.Create(text, settings))
            writer.WriteString(value);
        return text.ToString();
    }

    /// <summary>
    /// The real <c>schtasks.exe</c> invocation behind every public entry point.
    /// </summary>
    /// <remarks>
    /// Internal so a test can drive the process plumbing — argument passing, both pipes, the exit code —
    /// with a read-only <c>/Query</c> against a name that cannot exist. The lifecycle rules above are
    /// tested through <see cref="SchtasksRunner"/> fakes instead, so they never create a real task.
    /// </remarks>
    internal static (int ExitCode, string Output, string Error) RunSchtasks(IReadOnlyList<string> arguments)
        => RunSchtasks(arguments, StartSchtasksProcess);

    /// <remarks>
    /// Takes the process starter so tests can cover the process-plumbing failure modes without creating
    /// real scheduled tasks: Process.Start returning null, an unresponsive child that must be killed,
    /// and platform start failures. The public entry point keeps using the real schtasks.exe path.
    /// </remarks>
    internal static (int ExitCode, string Output, string Error) RunSchtasks(
        IReadOnlyList<string> arguments,
        SchtasksProcessStarter startProcess)
    {
        var psi = new ProcessStartInfo
        {
            // Absolute, not a bare image name: with UseShellExecute = false, CreateProcess searches the
            // application directory and the current directory before System32, so a planted schtasks.exe
            // beside the app would run instead of the real one.
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "schtasks.exe")
                : "schtasks.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            using ISchtasksProcess? process = startProcess(psi);
            if (process is null)
                return (-1, string.Empty, "could not start schtasks.exe.");

            // Both pipes are read before waiting: filling one while draining the other deadlocks.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(SchtasksTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, string.Empty, "schtasks.exe did not respond.");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static ISchtasksProcess? StartSchtasksProcess(ProcessStartInfo psi)
    {
        Process? process = Process.Start(psi);
        return process is null ? null : new RealSchtasksProcess(process);
    }

    /// <summary>Thin adapter that lets the real <see cref="Process"/> satisfy <see cref="ISchtasksProcess"/>.</summary>
    /// <remarks>
    /// Internal, not private, only so a test can prove the adapter really forwards <see cref="Kill"/> to
    /// the child process — the timeout path depends on that, and a silently non-forwarding adapter would
    /// leave a hung <c>schtasks.exe</c> running. It adds no behaviour of its own.
    /// </remarks>
    internal sealed class RealSchtasksProcess(Process process) : ISchtasksProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public void Dispose() => process.Dispose();
    }
}
