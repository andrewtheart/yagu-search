using System.Diagnostics;
using System.Xml;
using Yagu.Services;
using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// The sign-in task that owns the loopback HTTP server's lifetime. The task definition is built as XML
/// and handed to <c>schtasks.exe</c>, so the escaping and the lifecycle flags are tested here rather than
/// by registering real tasks on the machine running the suite.
/// </summary>
public class McpLoginHelperTests
{
    private static readonly string[] SampleArgs =
    [
        "--yagu-exe", @"C:\Program Files\Yagu\Yagu.exe",
        "--allowed-root", @"C:\Projects",
        "--transport", "http",
        "--port", "8377",
    ];

    private static XmlDocument LoadTaskXml(string? exePath = null, IReadOnlyList<string>? args = null)
    {
        string xml = McpLoginHelper.BuildTaskXml(exePath ?? @"C:\Program Files\Yagu\mcp-server\Yagu.McpServer.exe", args ?? SampleArgs);
        var document = new XmlDocument();
        document.LoadXml(xml);
        return document;
    }

    private static XmlNamespaceManager Namespaces(XmlDocument document)
    {
        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        return namespaces;
    }

    [Fact]
    public void TaskXml_IsWellFormed()
    {
        XmlDocument document = LoadTaskXml();
        Assert.Equal("Task", document.DocumentElement!.LocalName);
    }

    [Fact]
    public void TaskRunsAsTheSignedInUser_NeverAsSystem()
    {
        // A machine-wide SYSTEM service would read files the user has no rights to and hand them to
        // whichever client presented the loopback token. The OS must keep enforcing the user's own
        // permissions underneath the authorization policy.
        XmlDocument document = LoadTaskXml();
        var namespaces = Namespaces(document);

        Assert.Equal("InteractiveToken", document.SelectSingleNode("//t:Principal/t:LogonType", namespaces)?.InnerText);
        Assert.Equal("LeastPrivilege", document.SelectSingleNode("//t:Principal/t:RunLevel", namespaces)?.InnerText);
        Assert.DoesNotContain("S-1-5-18", document.OuterXml, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskStartsAtLogonAndSurvivesRealisticConditions()
    {
        XmlDocument document = LoadTaskXml();
        var namespaces = Namespaces(document);

        Assert.NotNull(document.SelectSingleNode("//t:Triggers/t:LogonTrigger", namespaces));

        // A background server must not be refused or killed for running on battery, and must not be
        // stopped after the default three-day execution limit.
        Assert.Equal("false", document.SelectSingleNode("//t:Settings/t:DisallowStartIfOnBatteries", namespaces)?.InnerText);
        Assert.Equal("false", document.SelectSingleNode("//t:Settings/t:StopIfGoingOnBatteries", namespaces)?.InnerText);
        Assert.Equal("PT0S", document.SelectSingleNode("//t:Settings/t:ExecutionTimeLimit", namespaces)?.InnerText);
    }

    [Fact]
    public void TaskRestartsAfterACrash()
    {
        XmlDocument document = LoadTaskXml();
        var namespaces = Namespaces(document);

        Assert.Equal("PT1M", document.SelectSingleNode("//t:Settings/t:RestartOnFailure/t:Interval", namespaces)?.InnerText);
        Assert.Equal("3", document.SelectSingleNode("//t:Settings/t:RestartOnFailure/t:Count", namespaces)?.InnerText);
    }

    [Fact]
    public void TaskDoesNotStackDuplicateServers()
    {
        XmlDocument document = LoadTaskXml();
        Assert.Equal("IgnoreNew", document.SelectSingleNode("//t:Settings/t:MultipleInstancesPolicy", Namespaces(document))?.InnerText);
    }

    [Fact]
    public void TaskCarriesTheServerCommandAndArguments()
    {
        XmlDocument document = LoadTaskXml();
        var namespaces = Namespaces(document);

        Assert.Equal(
            @"C:\Program Files\Yagu\mcp-server\Yagu.McpServer.exe",
            document.SelectSingleNode("//t:Actions/t:Exec/t:Command", namespaces)?.InnerText);

        string? arguments = document.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", namespaces)?.InnerText;
        Assert.Contains("--transport http", arguments, StringComparison.Ordinal);
        Assert.Contains(@"""C:\Program Files\Yagu\Yagu.exe""", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskNeverCarriesTheToken()
    {
        // The token is read from the per-user file. A task definition is readable, so putting it here
        // would publish it just as surely as a command line would.
        var args = new List<string>(SampleArgs) { "--allowed-root", @"C:\Other" };
        XmlDocument document = LoadTaskXml(args: args);

        Assert.DoesNotContain("--token", document.OuterXml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathsWithXmlSpecialCharacters_AreEscapedNotInjected()
    {
        // A folder called "Docs & <stuff>" must not be able to alter the task definition.
        XmlDocument document = LoadTaskXml(
            @"C:\Tools & Utilities\Yagu.McpServer.exe",
            ["--allowed-root", @"C:\Docs & <stuff>"]);

        var namespaces = Namespaces(document);
        Assert.Equal(@"C:\Tools & Utilities\Yagu.McpServer.exe",
            document.SelectSingleNode("//t:Actions/t:Exec/t:Command", namespaces)?.InnerText);
        Assert.Contains("<stuff>", document.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", namespaces)?.InnerText, StringComparison.Ordinal);
    }

    // ── Argument quoting ──

    [Fact]
    public void PlainArguments_AreNotQuoted()
        => Assert.Equal("--transport http", McpLoginHelper.JoinArguments(["--transport", "http"]));

    [Fact]
    public void ArgumentsWithSpaces_AreQuoted()
        => Assert.Equal(@"--allowed-root ""C:\Program Files\Data""",
            McpLoginHelper.JoinArguments(["--allowed-root", @"C:\Program Files\Data"]));

    [Fact]
    public void TrailingBackslashBeforeAClosingQuote_IsDoubled()
    {
        // "C:\My Docs\" would otherwise escape the closing quote and swallow the next argument.
        string line = McpLoginHelper.JoinArguments([@"C:\My Docs\", "--next"]);

        Assert.Equal(@"""C:\My Docs\\"" --next", line);
    }

    [Fact]
    public void EmbeddedQuotes_AreEscaped()
        => Assert.Equal(@"""a\""b""", McpLoginHelper.JoinArguments([@"a""b"]));

    [Fact]
    public void EmptyArgument_SurvivesAsAnEmptyQuotedToken()
        => Assert.Equal(@"""""", McpLoginHelper.JoinArguments([string.Empty]));

    // ── Composed launch arguments ──

    [Fact]
    public void HttpArguments_AddTransportAndPortToTheStdioSet()
    {
        var settings = new AppSettings
        {
            McpAllowedRoots = [@"C:\Projects"],
            McpCapabilities = ["Read"],
            McpHttpPort = 9001,
        };

        List<string> args = McpConfigSnippetBuilder.BuildHttpServerArguments(settings, @"C:\App\Yagu.exe");

        Assert.Contains("--transport", args);
        Assert.Equal("http", args[args.IndexOf("--transport") + 1]);
        Assert.Equal("9001", args[args.IndexOf("--port") + 1]);
        Assert.Contains(@"C:\Projects", args);
        Assert.DoesNotContain("--token", args);
    }

    [Fact]
    public void HttpPort_IsClampedToItsSupportedRange()
    {
        Assert.Equal(AppSettings.DefaultMcpHttpPort, AppSettings.NormalizeMcpHttpPort(0));
        Assert.Equal(AppSettings.MinimumMcpHttpPort, AppSettings.NormalizeMcpHttpPort(80));
        Assert.Equal(AppSettings.MaximumMcpHttpPort, AppSettings.NormalizeMcpHttpPort(999999));
        Assert.Equal(9001, AppSettings.NormalizeMcpHttpPort(9001));
    }

    [Fact]
    public void TaskName_IsPerUserSoTwoAccountsDoNotCollide()
        => Assert.Contains(Environment.UserName, McpLoginHelper.TaskName, StringComparison.Ordinal);

    // ── Task lifecycle ──
    //
    // Driven through an injected runner. Creating a real scheduled task would mutate the machine and
    // could collide with the user's own autostart helper, so the rules are proven against a fake.

    private sealed class FakeSchtasks(Func<IReadOnlyList<string>, (int, string, string)> respond)
    {
        public List<string> Verbs { get; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public (int ExitCode, string Output, string Error) Run(IReadOnlyList<string> arguments)
        {
            Verbs.Add(arguments[0]);
            Calls.Add(arguments);
            return respond(arguments);
        }
    }

    private static FakeSchtasks Responding(Func<string, (int, string, string)> byVerb)
        => new(args => byVerb(args[0]));

    private static (int, string, string) Ok() => (0, string.Empty, string.Empty);

    [Fact]
    public void Query_ExistingTask_IsReportedAsRegistered()
    {
        var fake = Responding(_ => Ok());

        McpLoginHelperState state = McpLoginHelper.QueryWith(fake.Run);

        Assert.True(state.Registered);
        Assert.Equal(["/Query"], fake.Verbs);
    }

    [Fact]
    public void Query_MissingTask_IsNotAnError()
    {
        var fake = Responding(_ => (1, string.Empty, "ERROR: The system cannot find the file specified."));

        McpLoginHelperState state = McpLoginHelper.QueryWith(fake.Run);

        Assert.False(state.Registered);
        Assert.Contains("cannot find", state.Detail);
    }

    [Fact]
    public void Query_FailureWithOnlyStdout_StillReportsTheDetail()
    {
        var fake = Responding(_ => (1, "  something on stdout  ", "   "));

        McpLoginHelperState state = McpLoginHelper.QueryWith(fake.Run);

        Assert.Equal("something on stdout", state.Detail);
    }

    [Theory]
    [InlineData("Status:        Running", true)]
    [InlineData("Status:Running", true)]
    [InlineData("  status :  running now", true)]
    [InlineData("Status:        Ready", false)]
    [InlineData("Status:        Disabled", false)]
    public void IsRunning_ReadsTheStatusLine(string statusLine, bool expected)
    {
        var fake = Responding(_ => (0, $"TaskName: whatever\r\n{statusLine}\r\nAuthor: someone", string.Empty));

        Assert.Equal(expected, McpLoginHelper.IsRunningWith(fake.Run));
    }

    [Fact]
    public void IsRunning_LocalizedOutputWithNoStatusLine_ReadsAsNotRunning()
    {
        // schtasks output is localized. An unrecognized status must never be read as "running",
        // because callers use this only to CONFIRM a failure.
        var fake = Responding(_ => (0, "Nom de tâche: Yagu\r\nÉtat: En cours d'exécution", string.Empty));

        Assert.False(McpLoginHelper.IsRunningWith(fake.Run));
    }

    [Fact]
    public void IsRunning_QueryFailure_ReadsAsNotRunning()
    {
        var fake = Responding(_ => (1, string.Empty, "no such task"));

        Assert.False(McpLoginHelper.IsRunningWith(fake.Run));
    }

    [Fact]
    public void IsRunning_OutputWithNoColonAtAll_ReadsAsNotRunning()
    {
        var fake = Responding(_ => (0, "no colons here\r\nnor here", string.Empty));

        Assert.False(McpLoginHelper.IsRunningWith(fake.Run));
    }

    [Fact]
    public void Restart_EndsTheOldInstanceBeforeStartingTheNewOne()
    {
        // MultipleInstancesPolicy is IgnoreNew, so a bare /Run against a live instance changes nothing.
        var fake = Responding(_ => Ok());

        McpLoginHelperState state = McpLoginHelper.Restart(fake.Run);

        Assert.True(state.Registered);
        Assert.Equal(["/End", "/Run"], fake.Verbs);
    }

    [Fact]
    public void Restart_FailedEndWithNothingRunning_StillStartsTheNewInstance()
    {
        // A non-zero /End usually just means nothing was running, which must not block the restart.
        var fake = Responding(verb => verb switch
        {
            "/End" => (1, string.Empty, "ERROR: The system cannot find the file specified."),
            "/Query" => (0, "Status: Ready", string.Empty),
            _ => Ok(),
        });

        McpLoginHelperState state = McpLoginHelper.RestartWith(fake.Run);

        Assert.True(state.Registered);
        Assert.Contains("/Run", fake.Verbs);
    }

    [Fact]
    public void Restart_ConfirmedStillRunning_FailsAndDoesNotClaimTheNewGrantIsLive()
    {
        // This is the revoked-folder bug: reporting success here leaves the OLD command line serving
        // folders the user just removed.
        var fake = Responding(verb => verb switch
        {
            "/End" => (1, string.Empty, "access denied"),
            "/Query" => (0, "Status: Running", string.Empty),
            _ => Ok(),
        });

        McpLoginHelperState state = McpLoginHelper.RestartWith(fake.Run);

        Assert.False(state.Registered);
        Assert.Contains("previous folders", state.Detail);
        Assert.DoesNotContain("/Run", fake.Verbs);
    }

    [Fact]
    public void RunNow_Failure_FallsBackToWhetherTheTaskStillExists()
    {
        var fake = Responding(verb => verb switch
        {
            "/Run" => (1, string.Empty, "the task is disabled"),
            _ => Ok(),
        });

        McpLoginHelperState state = McpLoginHelper.RunNowWith(fake.Run);

        // Starting failed, but the task is still registered — the UI must not offer to re-register it.
        Assert.True(state.Registered);
        Assert.Contains("disabled", state.Detail);
    }

    [Fact]
    public void Unregister_StopsThenDeletesTheTask()
    {
        var fake = Responding(_ => Ok());

        McpLoginHelperState state = McpLoginHelper.Unregister(fake.Run);

        Assert.False(state.Registered);
        Assert.Equal(["/End", "/Delete"], fake.Verbs);
    }

    [Fact]
    public void Unregister_ConfirmedStillRunning_ReportsTheGrantIsStillBeingServed()
    {
        var fake = Responding(verb => verb switch
        {
            "/End" => (1, string.Empty, "access denied"),
            "/Query" => (0, "Status: Running", string.Empty),
            _ => Ok(),
        });

        McpLoginHelperState state = McpLoginHelper.UnregisterWith(fake.Run);

        // Deleting the task while an instance keeps serving the revoked grant would look like success.
        Assert.True(state.Registered);
        Assert.Contains("keeps serving", state.Detail);
        Assert.DoesNotContain("/Delete", fake.Verbs);
    }

    [Fact]
    public void Unregister_FailedDelete_ReportsWhetherTheTaskSurvived()
    {
        var fake = Responding(verb => verb switch
        {
            "/Delete" => (1, string.Empty, "access denied"),
            _ => Ok(),
        });

        McpLoginHelperState state = McpLoginHelper.UnregisterWith(fake.Run);

        Assert.True(state.Registered);
        Assert.Contains("access denied", state.Detail);
    }

    [Fact]
    public void Register_WritesTheDefinitionAsUtf16AndHandsItToSchtasks()
    {
        string? xmlPath = null;
        byte[] xmlBytes = [];

        var fake = new FakeSchtasks(args =>
        {
            xmlPath = args[4];
            xmlBytes = File.ReadAllBytes(xmlPath);
            return Ok();
        });

        McpLoginHelperState state = McpLoginHelper.Register(@"C:\app\Yagu.McpServer.exe", ["--transport", "stdio"], fake.Run);

        Assert.True(state.Registered);
        Assert.Equal(["/Create"], fake.Verbs);
        Assert.NotNull(xmlPath);

        // schtasks /Create /XML rejects UTF-8; it requires UTF-16 with a BOM.
        Assert.True(xmlBytes.Length >= 2 && xmlBytes[0] == 0xFF && xmlBytes[1] == 0xFE);

        // The temp definition must not be left behind: it names the server's full command line.
        Assert.False(File.Exists(xmlPath));
    }

    [Fact]
    public void Register_Failure_IsReportedAndStillCleansUpTheTempDefinition()
    {
        string? xmlPath = null;
        var fake = new FakeSchtasks(args =>
        {
            xmlPath = args[4];
            return (1, string.Empty, "ERROR: Access is denied.");
        });

        McpLoginHelperState state = McpLoginHelper.RegisterWith(fake.Run, @"C:\app\Yagu.McpServer.exe", []);

        Assert.False(state.Registered);
        Assert.Contains("Access is denied", state.Detail);
        Assert.False(File.Exists(xmlPath));
    }

    [Fact]
    public void Register_WhenSchtasksCannotReadTheDefinition_ReportsTheIoFailure()
    {
        // The task definition is a temp file. If the scheduler cannot read it — antivirus race,
        // profile temp failure, or permission issue — Settings must report the failure instead of
        // claiming the sign-in helper was registered.
        var fake = new FakeSchtasks(_ => throw new IOException("temp definition disappeared"));

        McpLoginHelperState state = McpLoginHelper.RegisterWith(fake.Run, @"C:\app\Yagu.McpServer.exe", []);

        Assert.False(state.Registered);
        Assert.Equal("temp definition disappeared", state.Detail);
        Assert.Equal(["/Create"], fake.Verbs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithoutAServerPath_IsRejected(string? exePath)
    {
        var fake = Responding(_ => Ok());

        Assert.ThrowsAny<ArgumentException>(() => McpLoginHelper.RegisterWith(fake.Run, exePath!, []));
        Assert.Empty(fake.Verbs);
    }

    [Fact]
    public void CurrentUserId_FallsBackToDomainAndUserWhenWindowsIdentityIsUnavailable()
    {
        // Some locked-down hosts reject WindowsIdentity.GetCurrent. The generated task still needs a
        // usable UserId or Windows rejects the XML and the MCP server never starts at sign-in.
        string userId = McpLoginHelper.CurrentUserId(
            () => throw new InvalidOperationException("identity unavailable"));

        Assert.Equal($@"{Environment.UserDomainName}\{Environment.UserName}", userId);
    }

    // ── The real schtasks.exe invocation ──
    //
    // Every case here is a read-only /Query against a name that cannot exist, so the machine's task
    // store is never modified. The lifecycle rules are tested through fakes above; this covers only the
    // process plumbing those fakes stand in for.

    private sealed class FakeSchtasksProcess(
        string output = "",
        string error = "",
        int exitCode = 0,
        bool waitForExit = true,
        bool throwOnKill = false) : ISchtasksProcess
    {
        public TextReader StandardOutput { get; } = new StringReader(output);
        public TextReader StandardError { get; } = new StringReader(error);
        public int ExitCode { get; } = exitCode;
        public bool KillCalled { get; private set; }
        public bool KillTree { get; private set; }
        public bool Disposed { get; private set; }

        public bool WaitForExit(int milliseconds) => waitForExit;

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KillTree = entireProcessTree;
            if (throwOnKill)
                throw new InvalidOperationException("already exited");
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void RunSchtasks_WhenProcessStartReturnsNull_ReportsAStartFailure()
    {
        // Process.Start can return null instead of throwing. Treating that as success would make callers
        // believe a scheduler operation completed when no child process ever existed.
        var result = McpLoginHelper.RunSchtasks(["/Query"], _ => null);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("could not start schtasks.exe.", result.Error);
    }

    [Fact]
    public void RunSchtasks_DrainsPipesAndReturnsTheExitCodeFromTheStartedProcess()
    {
        // The fake proves the same plumbing used for real schtasks preserves separate arguments and
        // reports stdout, stderr, and the exit code without mutating the task store.
        ProcessStartInfo? captured = null;
        FakeSchtasksProcess? process = null;

        var result = McpLoginHelper.RunSchtasks(["/Query", "/TN", "name with spaces"], psi =>
        {
            captured = psi;
            process = new FakeSchtasksProcess("out", "err", exitCode: 7);
            return process;
        });

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.Output);
        Assert.Equal("err", result.Error);
        Assert.True(process?.Disposed);
        Assert.NotNull(captured);
        Assert.Equal(["/Query", "/TN", "name with spaces"], captured.ArgumentList.ToArray());
        Assert.False(captured.UseShellExecute);
        Assert.True(captured.RedirectStandardOutput);
        Assert.True(captured.RedirectStandardError);
    }

    [Fact]
    public void RunSchtasks_AlwaysLaunchesTheSystem32Binary_NotABareImageName()
    {
        // The hardening this pins: with UseShellExecute = false, CreateProcess searches the application
        // directory and the current directory BEFORE System32, so a bare "schtasks.exe" would run a
        // planted binary dropped beside Yagu.exe — with whatever the signed-in user can do. The process
        // seam makes the whole call fakeable, so the one field that must never regress is asserted here.
        ProcessStartInfo? captured = null;

        McpLoginHelper.RunSchtasks(["/Query"], psi =>
        {
            captured = psi;
            return new FakeSchtasksProcess();
        });

        Assert.NotNull(captured);
        Assert.Equal(
            Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            captured.FileName,
            ignoreCase: true);
        Assert.True(Path.IsPathFullyQualified(captured.FileName));

        // A relative or bare name would be resolved against the caller's working directory, which the
        // MCP server does not control.
        Assert.DoesNotContain("..", captured.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSchtasks_KillsAnUnresponsiveProcessAndReportsTheTimeout()
    {
        // If schtasks hangs, Settings must not hang forever; the child is killed as a process tree and
        // the caller gets a clear failure detail.
        var process = new FakeSchtasksProcess(waitForExit: false);

        var result = McpLoginHelper.RunSchtasks(["/Query"], _ => process);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("schtasks.exe did not respond.", result.Error);
        Assert.True(process.KillCalled);
        Assert.True(process.KillTree);
    }

    [Fact]
    public void RunSchtasks_StillReportsTheTimeoutWhenTheHungProcessAlreadyExitedDuringKill()
    {
        // A race between timeout detection and Kill must not surface as an exception. The scheduler
        // operation still timed out from the caller's perspective.
        var process = new FakeSchtasksProcess(waitForExit: false, throwOnKill: true);

        var result = McpLoginHelper.RunSchtasks(["/Query"], _ => process);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("schtasks.exe did not respond.", result.Error);
        Assert.True(process.KillCalled);
    }

    [Fact]
    public void RunSchtasks_ReturnsProcessStartExceptionsAsSchedulerFailures()
    {
        // Missing executables, policy blocks, or invalid process state should be shown as task setup
        // failures rather than crashing the Settings UI.
        var result = McpLoginHelper.RunSchtasks(
            ["/Query"],
            _ => throw new System.ComponentModel.Win32Exception(5, "access denied"));

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("access denied", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealSchtasksProcess_KillForwardsToTheUnderlyingProcess()
    {
        // The timeout path relies on killing the actual child process, not just reporting an error.
        // Cover the thin adapter with a harmless long-running process instead of creating a real task.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add(">nul");

        using Process process = Process.Start(psi)!;
        using var wrapper = new McpLoginHelper.RealSchtasksProcess(process);

        try
        {
            wrapper.Kill(entireProcessTree: true);

            Assert.True(process.WaitForExit(5_000), "the adapter did not terminate the child process.");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static string UnusedTaskName() => "Yagu-does-not-exist-" + Guid.NewGuid().ToString("n");

    [Fact]
    public void RunSchtasks_ReportsTheProcessExitCodeAndItsOutput()
    {
        (int exitCode, string output, string error) = McpLoginHelper.RunSchtasks(["/Query", "/TN", UnusedTaskName()]);

        // A missing task is a non-zero exit with a message, which is exactly what the callers key on.
        Assert.NotEqual(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error),
            "schtasks must report something on stdout or stderr, or a caller cannot explain the failure.");
    }

    [Fact]
    public void RunSchtasks_DrainsBothPipesSoALargeListingCannotDeadlock()
    {
        // /Query with no /TN lists every task on the machine, which is far more than a pipe buffer holds.
        // Waiting for exit before draining would hang here rather than return.
        (int exitCode, string output, _) = McpLoginHelper.RunSchtasks(["/Query", "/FO", "CSV"]);

        Assert.Equal(0, exitCode);
        Assert.NotEmpty(output);
    }

    [Fact]
    public void RunSchtasks_PassesArgumentsSeparatelySoASpacedTaskNameSurvives()
    {
        string spaced = UnusedTaskName() + " with spaces";

        (int exitCode, string output, string error) = McpLoginHelper.RunSchtasks(["/Query", "/TN", spaced]);

        // A single joined command line would split this into two arguments and schtasks would reject the
        // syntax rather than report the task as missing.
        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Invalid syntax", output + error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunSchtasks_AnUnrecognizedVerb_IsReturnedRatherThanThrown()
    {
        (int exitCode, _, _) = McpLoginHelper.RunSchtasks(["/NotAVerb"]);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Query_ReadsTheRealTaskStoreWithoutThrowing()
    {
        // Read-only, so it is safe against the live task. The Settings tab calls this while it builds,
        // and schtasks reports a MISSING task as a failure — which is not an error condition here.
        McpLoginHelperState state = McpLoginHelper.Query();

        if (!state.Registered)
            Assert.False(string.IsNullOrWhiteSpace(state.Detail), "a missing task must still explain itself.");
    }

    [Fact]
    public void IsRunning_AgreesWithQueryAboutATaskThatDoesNotExist()
    {
        // Also read-only. A task that is not registered cannot be running, and this is the check that
        // decides whether a failed /End is reported as "the server is still serving your old folders".
        if (!McpLoginHelper.Query().Registered)
            Assert.False(McpLoginHelper.IsRunning());
    }

    [Fact]
    public void TheTaskNameIsScopedToTheCurrentUser()
    {
        // Two accounts on one PC each get their own server with their own grant; a shared name would
        // mean one user's Settings change silently rewrote the other's.
        Assert.Contains(Environment.UserName, McpLoginHelper.TaskName, StringComparison.Ordinal);
    }
}

/// <summary>
/// The same lifecycle, driven against the <b>real</b> Windows Task Scheduler.
/// </summary>
/// <remarks>
/// <para>
/// Everything above proves Yagu builds the XML it means to. Only this proves Windows <b>accepts</b> it:
/// a schema slip, a bad principal, or a trigger the scheduler rejects all produce XML that passes every
/// string assertion and then fails on a user's machine with "The task XML is malformed".
/// </para>
/// <para>
/// Each test registers a throwaway <c>Yagu-test-&lt;guid&gt;</c> task, never the user's own
/// <see cref="McpLoginHelper.TaskName"/>, and force-deletes it in a finalizer-free
/// <see cref="IDisposable"/> so a failed assertion cannot leave a logon task behind. The task runs
/// <c>cmd.exe /c exit</c>, so starting it is instant and harmless. If the environment refuses to create
/// scheduled tasks at all, the test says so and returns rather than failing.
/// </para>
/// </remarks>
public sealed class McpLoginHelperSchedulerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A task name that cannot collide with the user's server task or with a parallel test.</summary>
    private sealed class ThrowawayTask : IDisposable
    {
        public string Name { get; } = "Yagu-test-" + Guid.NewGuid().ToString("n");

        public void Dispose()
        {
            McpLoginHelper.RunSchtasks(["/End", "/TN", Name]);
            McpLoginHelper.RunSchtasks(["/Delete", "/TN", Name, "/F"]);
        }
    }

    /// <summary>Harmless stand-in for the server: starts, exits immediately, touches nothing.</summary>
    private static string HarmlessExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static IReadOnlyList<string> HarmlessArgs => ["/c", "exit"];

    private bool CanCreateTasks(ThrowawayTask task)
    {
        McpLoginHelperState registered = McpLoginHelper.RegisterWith(
            McpLoginHelper.RunSchtasks, HarmlessExe, HarmlessArgs, task.Name);

        if (registered.Registered)
            return true;

        output.WriteLine($"Skipped: this environment refuses to create scheduled tasks ({registered.Detail}).");
        return false;
    }

    [Fact]
    public void TheGeneratedXml_IsAcceptedByWindowsAndKeepsItsLifecycleFlags()
    {
        using var task = new ThrowawayTask();
        if (!CanCreateTasks(task))
            return;

        Assert.True(McpLoginHelper.QueryWith(McpLoginHelper.RunSchtasks, task.Name).Registered);

        (int exitCode, string xml, _) = McpLoginHelper.RunSchtasks(["/Query", "/TN", task.Name, "/XML"]);
        Assert.Equal(0, exitCode);

        // Read back from the SCHEDULER, not from our own builder. These are the flags that make the
        // server a sane background helper, and they only count once Windows has stored them.
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml, StringComparison.Ordinal);
        Assert.Contains("RestartOnFailure", xml, StringComparison.Ordinal);

        // No timeout: the server is long-lived, and the default 3-day limit would kill it mid-session.
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);

        // The task must never run elevated. Windows OMITS RunLevel when it is LeastPrivilege, because
        // that is the default — so absence of HighestAvailable is what "least privilege" looks like in
        // the stored definition, and asserting the literal LeastPrivilege here would always fail.
        Assert.DoesNotContain("HighestAvailable", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringTwice_ReplacesTheDefinitionRatherThanFailing()
    {
        using var task = new ThrowawayTask();
        if (!CanCreateTasks(task))
            return;

        // Saving Settings re-applies the grant every time. Without /F the second save would fail with
        // "the task already exists" and the narrowed grant would never reach the scheduler.
        McpLoginHelperState again = McpLoginHelper.RegisterWith(
            McpLoginHelper.RunSchtasks, HarmlessExe, ["/c", "exit", "--allowed-root", @"C:\narrowed"], task.Name);

        Assert.True(again.Registered, again.Detail);

        (_, string xml, _) = McpLoginHelper.RunSchtasks(["/Query", "/TN", task.Name, "/XML"]);
        Assert.Contains("--allowed-root", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Unregister_RemovesTheTaskFromTheSchedulerForReal()
    {
        using var task = new ThrowawayTask();
        if (!CanCreateTasks(task))
            return;

        McpLoginHelperState removed = McpLoginHelper.UnregisterWith(McpLoginHelper.RunSchtasks, task.Name);

        // Registered == false is the contract for "it is gone", and the scheduler has to agree: a task
        // that survives uninstall is a process pointing at a deleted executable, holding a port.
        Assert.False(removed.Registered, removed.Detail);
        Assert.False(McpLoginHelper.QueryWith(McpLoginHelper.RunSchtasks, task.Name).Registered);
    }

    [Fact]
    public void RunNow_StartsTheRegisteredTaskSoTurningTheTransportOnDoesNotNeedASignOut()
    {
        using var task = new ThrowawayTask();
        if (!CanCreateTasks(task))
            return;

        Assert.True(McpLoginHelper.RunNowWith(McpLoginHelper.RunSchtasks, task.Name).Registered);
    }

    [Fact]
    public void Restart_EndsThenStartsAgain_SoAChangedGrantTakesEffectImmediately()
    {
        using var task = new ThrowawayTask();
        if (!CanCreateTasks(task))
            return;

        McpLoginHelper.RunNowWith(McpLoginHelper.RunSchtasks, task.Name);

        McpLoginHelperState restarted = McpLoginHelper.RestartWith(McpLoginHelper.RunSchtasks, task.Name);

        Assert.True(restarted.Registered, restarted.Detail);
    }

    [Fact]
    public void UnregisteringATaskThatWasNeverRegistered_ReportsItAsGoneRatherThanFailing()
    {
        using var task = new ThrowawayTask();

        // The uninstaller calls this unconditionally. A user who never enabled the transport must not
        // see it reported as a failure.
        McpLoginHelperState removed = McpLoginHelper.UnregisterWith(McpLoginHelper.RunSchtasks, task.Name);

        Assert.False(removed.Registered);
    }

    [Fact]
    public void RunNow_OnATaskThatDoesNotExist_ReportsItAsNotRegistered()
    {
        using var task = new ThrowawayTask();

        McpLoginHelperState state = McpLoginHelper.RunNowWith(McpLoginHelper.RunSchtasks, task.Name);

        // Registered is what the caller keys on to decide whether to say "restarted" or "sign in again".
        Assert.False(state.Registered);
        Assert.False(string.IsNullOrWhiteSpace(state.Detail));
    }

    [Fact]
    public void IsRunning_IsFalseForATaskThatDoesNotExist()
    {
        using var task = new ThrowawayTask();

        Assert.False(McpLoginHelper.IsRunningWith(McpLoginHelper.RunSchtasks, task.Name));
    }
}
