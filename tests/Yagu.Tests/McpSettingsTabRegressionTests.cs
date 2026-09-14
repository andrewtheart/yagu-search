using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the Settings <b>MCP Server</b> tab (plan Phase 4, step 26). The tab
/// is WinUI code that is not compiled into the test assembly, so its contracts are pinned here as source
/// substrings; the pure configuration logic it drives is unit-tested in <c>McpServerSettingsTests</c>.
/// </summary>
public sealed class McpSettingsTabRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static readonly string McpTabSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.Mcp.cs");
    private static readonly string McpClientSetupSource = Read("src", "Yagu", "Services", "Mcp", "McpClientSetup.cs");
    private static readonly string SettingsWindowSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");
    private static readonly string MachineModeSource = Read("src", "Yagu", "CliRunner.MachineMode.cs");

    [Fact]
    public void SettingsWindow_BuildsTheMcpTab()
    {
        Assert.Contains("AddTab(\"MCP Server\")", McpTabSource);
        Assert.Contains("BuildMcpTab();", SettingsWindowSource);
        Assert.Contains("\"MCP Server\" => \"\\uE71B\",", SettingsWindowSource);
    }

    [Fact]
    public void McpTab_BuildsTheGroupsTheTabIsResponsibleFor()
    {
        foreach (string group in new[] { "Server", "Allowed Folders", "Capabilities", "Response Limits", "Connect a Client" })
            Assert.Contains($"AddSettingsGroupBox(g, \"{group}\")", McpTabSource);
    }

    [Fact]
    public void McpTab_ExposesEveryCapabilityAsItsOwnGate()
    {
        // A capability that has no toggle can never be granted from the UI, and one that is silently
        // bundled with another defeats the point of separating them by risk.
        foreach (McpCapability capability in Enum.GetValues<McpCapability>())
        {
            if (capability == McpCapability.None)
                continue;
            Assert.Contains($"AddCapabilityToggle(McpCapability.{capability},", McpTabSource);
        }
    }

    [Fact]
    public void McpTab_IndexPermissionDescriptionsMatchTheExposedOperations()
    {
        int start = McpTabSource.IndexOf("AddCapabilityToggle(McpCapability.IndexMaintenance,", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = McpTabSource.IndexOf("var writeToggle", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string permissions = McpTabSource[start..end];

        Assert.Contains("Rebuilding and unregistering require the separate permission below.", permissions);
        Assert.Contains("AddCapabilityToggle(McpCapability.DestructiveIndexOps, \"Rebuild or unregister indexes\",", permissions);
        Assert.Contains("Also requires Manage content indexes.", permissions);
        Assert.Contains("Unregistering leaves stored index data intact.", permissions);
        Assert.Contains("No MCP tool deletes an index or clears all indexes.", permissions);
        Assert.DoesNotContain("\"Delete content indexes\"", permissions);

        string help = Read("HELP.md");
        Assert.Contains("| Rebuild or unregister indexes |", help);
        Assert.DoesNotContain("| Delete content indexes |", help);
    }

    [Fact]
    public void McpTab_BindsEveryPersistedCeilingToItsSupportedRange()
    {
        Assert.Contains("MachineOutputBudget.MinLineBytes, MachineOutputBudget.MaxLineBytesCeiling", McpTabSource);
        Assert.Contains("MachineOutputBudget.MinPathBytes, MachineOutputBudget.MaxPathBytesCeiling", McpTabSource);
        Assert.Contains("MachineOutputBudget.MinContextLines, MachineOutputBudget.MaxContextLinesCeiling", McpTabSource);
        Assert.Contains("MachineOutputBudget.MinRecords, MachineOutputBudget.MaxRecordsCeiling", McpTabSource);
        Assert.Contains("MachineOutputBudget.MinResponseBytes, MachineOutputBudget.MaxResponseBytesCeiling", McpTabSource);
        Assert.Contains("AppSettings.MinimumMcpMaxConcurrentCalls, AppSettings.MaximumMcpMaxConcurrentCalls", McpTabSource);

        foreach (string setting in new[]
        {
            "s.McpMaxLineBytes", "s.McpMaxContextLineBytes", "s.McpMaxPathBytes",
            "s.McpMaxContextLines", "s.McpMaxRecords", "s.McpMaxResponseBytes", "s.McpMaxConcurrentCalls",
        })
        {
            Assert.Contains(setting, McpTabSource);
        }
    }

    [Fact]
    public void McpTab_NormalizesRootsThroughTheServersOwnValidation()
    {
        Assert.Contains("AppSettings.NormalizeMcpAllowedRoots(roots);", McpTabSource);
        Assert.Contains("AppSettings.NormalizeMcpAllowedRoots(remaining);", McpTabSource);
    }

    [Fact]
    public void McpTab_MarksRootEditsDirtySoTheyReachTheSaveButton()
    {
        // Adding or removing a root writes straight to the live settings object; nothing else would tell
        // the window there is something to save.
        Assert.Contains("MarkSettingsDirty(requireValueChanges: false);", McpTabSource);
    }

    [Fact]
    public void McpTab_RegeneratesTheClientSnippetOnEveryChange()
    {
        // The snippet is the grant, so a stale one would hand a client the previous configuration.
        Assert.Contains("McpConfigSnippetBuilder.BuildServerArguments(", McpTabSource);
        Assert.Contains("McpConfigSnippetBuilder.BuildClientConfig(", McpTabSource);
        Assert.Contains("RefreshMcpTab();", McpTabSource);
    }

    [Fact]
    public void McpTab_OffersEveryClientAndCopiesTheSnippet()
    {
        Assert.Contains("foreach (McpClientKind client in Enum.GetValues<McpClientKind>())", McpTabSource);
        Assert.Contains("Clipboard.SetContent(package);", McpTabSource);
    }

    [Fact]
    public void MachineMode_UsesThePersistedCeilingsRatherThanTheShippedDefaults()
    {
        Assert.Contains("s_budget = ResolveMachineOutputBudget(args);", MachineModeSource);
        Assert.Contains("ResolveSettingsService(args).Load().EffectiveMachineOutputBudget;", MachineModeSource);
        Assert.Contains("return MachineOutputBudget.Default;", MachineModeSource);
    }

    [Fact]
    public void McpTab_GatesAutostartBehindTheHttpTransport()
    {
        // Registering a sign-in task for a transport that is switched off would leave a server nobody
        // asked for holding a port at every logon.
        Assert.Contains("portBox.IsEnabled = httpToggle.IsOn;", McpTabSource);
        Assert.Contains("autostartToggle.IsEnabled = httpToggle.IsOn;", McpTabSource);
        Assert.Contains("autostartToggle.IsOn = false;", McpTabSource);
    }

    [Fact]
    public void McpTab_NeverShowsOrCopiesTheToken()
    {
        // A token on screen is a token in a screenshot; clients read it from the same per-user file the
        // server does.
        // Prefix match on purpose: which overload writes the file is incidental, and pinning the argument
        // list made an unrelated signature change look like a token-disclosure regression.
        Assert.Contains("McpTokenStore.Regenerate(", McpTabSource);
        Assert.DoesNotContain("McpTokenStore.TryReadToken", McpTabSource);
        Assert.DoesNotContain("package.SetText(McpTokenStore", McpTabSource);
    }

    [Fact]
    public void McpTab_OffersInstallAndRemovePerClient()
    {
        Assert.Contains("McpClientRegistrar.IsInstalled(", McpTabSource);
        Assert.Contains("McpClientRegistrar.Install(", McpClientSetupSource);
        Assert.Contains("McpClientRegistrar.Remove(", McpClientSetupSource);

        // Installing with no allowed folder writes a configuration that cannot work; installing with no
        // server behind the path reports success for something that fails silently inside the client; and
        // installing for a client that is not on this machine would create its config folder on its behalf.
        Assert.Contains("IsEnabled = _viewModel.Settings.McpAllowedRoots.Count > 0", McpTabSource);
        Assert.Contains("&& state.ConfigLocationExists,", McpTabSource);
        Assert.Contains("File.Exists(McpConfigSnippetBuilder.ResolveServerExecutablePath())", McpClientSetupSource);

        // The rows render from a snapshot gathered off the UI thread. Reading four client config files
        // inside a layout pass stalls the window, and RefreshMcpTab runs on every NumberBox keystroke.
        Assert.Contains("private void ReloadMcpClientStateAsync()", McpTabSource);
        Assert.Contains("_ = Task.Run(() =>", McpTabSource);
        Assert.Contains("DispatcherQueue.TryEnqueue(() =>", McpTabSource);

        // A parse failure must be reported, never silently repaired.
        Assert.Contains("McpRegistrationOutcome.Malformed", McpClientSetupSource);
    }

    [Fact]
    public void SavingSettings_ReappliesTheGrantToAlreadyInstalledClients()
    {
        // A client's config stores the server's COMMAND LINE, and that command line is the grant. Saving
        // a narrower grant without rewriting those entries leaves every registered client launching the
        // server with the folders the user just took away, for good.
        Assert.Contains("foreach (McpClientState state in _mcpClientStates)", McpTabSource);
        Assert.Contains("McpClientSetup.Apply(settings, state.Client, install: true);", McpTabSource);

        // Registering the sign-in task alone was the old behaviour and is not sufficient on its own.
        AssertContainsInOrder(
            McpTabSource,
            "private void RefreshMcpAutostartRegistration()",
            "McpClientSetup.Apply(settings, state.Client, install: true);",
            "ApplyMcpAutostart(enabled: true);");
    }

    [Fact]
    public void AutostartToggle_DoesNotBlockTheUiThreadOnSchtasks()
    {
        // Register + Restart is up to four schtasks.exe runs at a 15s timeout each.
        Assert.Contains("await Task.Run(() => ApplyMcpAutostart(requested)).ConfigureAwait(true);", McpTabSource);
        Assert.Contains("autostartToggle.IsEnabled = false;", McpTabSource);

        // The work is PUBLISHED rather than fire-and-forget. As a bare async void handler its correction
        // to McpStartAtLogin landed after a concurrent Save had already persisted the stale value and
        // called MarkSettingsClean, so the correction was never written at all.
        Assert.Contains("_mcpAutostartApply = ApplyAutostartToggleAsync();", McpTabSource);
        Assert.Contains("private Task? _mcpAutostartApply;", McpTabSource);
        Assert.DoesNotContain("autostartToggle.Toggled += async (_, _) =>", McpTabSource);

        string save = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.IndexRebuildAdvice.cs");
        AssertContainsInOrder(save,
            "await AwaitPendingMcpAutostartAsync();",
            "await _viewModel.PersistSettingsAsync();",
            "MarkSettingsClean();");
    }

    [Fact]
    public void McpCommands_AreDispatchedAsConsoleCommands()
    {
        string program = Read("src", "Yagu", "Program.cs");
        string commands = Read("src", "Yagu", "CliRunner.McpCommands.cs");

        Assert.Contains("|| CliRunner.IsMcpCommand(args)", program);
        Assert.Contains("CliRunner.RunMcpCommand(args);", program);

        foreach (string flag in new[]
        {
            "--mcp-config", "--mcp-install", "--mcp-remove", "--mcp-status", "--mcp-regenerate-token", "--mcp-autostart",
        })
        {
            Assert.Contains(flag, commands);
        }

        // The token is never printed: it would land in shell history and terminal scrollback.
        Assert.DoesNotContain("Console.WriteLine(token", commands);
    }

    [Fact]
    public void CliAndMcpCommands_ReturnBeforeTheSingleInstanceMutexIsTaken()
    {
        // Yagu is single-instance via Global\YaguSingleInstance, and the MCP bridge shells out to
        // Yagu.exe --cli for every tool call. If those children reached the mutex they would either be
        // turned into a "activate the existing window" handoff or block the GUI, so a user could not
        // open Yagu while an AI client was driving it. Ordering is the whole guarantee, so pin it.
        string program = Read("src", "Yagu", "Program.cs");

        int mcpDispatch = program.IndexOf("CliRunner.RunMcpCommand(args);", StringComparison.Ordinal);
        int cliDispatch = program.IndexOf("CliRunner.Run(args);", StringComparison.Ordinal);
        int mutex = program.IndexOf(@"Global\YaguSingleInstance", StringComparison.Ordinal);

        Assert.True(mcpDispatch > 0 && cliDispatch > 0 && mutex > 0, "expected both dispatches and the mutex in Program.cs");
        Assert.True(cliDispatch < mutex, "--cli must be dispatched before the single-instance mutex is acquired.");
        Assert.True(mcpDispatch < mutex, "MCP commands must be dispatched before the single-instance mutex is acquired.");
    }

    [Fact]
    public void LoginHelper_RunsAsTheUserAndIsRemovable()
    {
        string helper = Read("src", "Yagu", "Services", "Mcp", "McpLoginHelper.cs");

        Assert.Contains("<LogonType>InteractiveToken</LogonType>", helper);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", helper);
        Assert.Contains("RestartOnFailure", helper);
        Assert.Contains("\"/Delete\", \"/TN\", taskName ?? TaskName, \"/F\"", helper);
    }

    [Fact]
    public void SavingSettings_ReRegistersTheSignInTaskSoARevokedFolderIsActuallyRevoked()
    {
        // The scheduled task stores the server's command line, and that command line IS the grant.
        // Without re-registering on save, removing an allowed folder would revoke it for clients that
        // launch the server themselves while the sign-in server kept serving the folder just taken away.
        Assert.Contains("RefreshMcpAutostartRegistration();", Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.IndexRebuildAdvice.cs"));
        Assert.Contains("private void RefreshMcpAutostartRegistration()", McpTabSource);
        Assert.Contains("ApplyMcpAutostart(enabled: true);", McpTabSource);
    }

    [Fact]
    public void RemovingTheLastAllowedFolder_StopsTheServerAndRemovesItsTask()
    {
        // An empty grant is a REVOCATION, not a reason to do nothing. The task and the instance it
        // started both keep the old folders on their command line, and IgnoreNew blocks a second
        // instance, so returning early left Settings showing no folders while the server served them.
        AssertContainsInOrder(McpTabSource,
            "if (settings.McpAllowedRoots.Count == 0)",
            "McpLoginHelperState revoked = McpLoginHelper.Unregister(",
            "the scheduled task could not be removed");

        // The result used to be discarded, so nothing ever reported that the grant had not been applied.
        Assert.Contains("(bool ok, string message, bool warn) = ApplyMcpAutostart(enabled: true);", McpTabSource);
        Assert.Contains("if (ok && !warn)", McpTabSource);
        Assert.Contains("_mcpAutostartWarning = _mcpAutostartWarning is null", McpTabSource);
        Assert.Contains("settings.McpStartAtLogin = false;", McpTabSource);

        // A registered task whose running server could not be replaced is still serving the revoked
        // folders, so it warns even though the task itself now matches the request.
        Assert.Contains("private readonly record struct McpAutostartApplyResult(bool Applied, string Message, bool Warn);", McpTabSource);
        Assert.Contains("Warn: true)", McpTabSource);

        string save = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.IndexRebuildAdvice.cs");
        AssertContainsInOrder(save,
            "RefreshMcpAutostartRegistration();",
            "if (_mcpAutostartWarning is { } mcpWarning)",
            "await ReportMcpAutostartFailureAsync(mcpWarning);");

        // Settings stays open so the failure cannot be dismissed by the window closing behind it.
        Assert.Contains("mcpNeedsAttention = true;", save);
        Assert.Contains("YaguDialog.ShowAsync(", save);
    }

    [Fact]
    public void ExportAndSession_AreBoundedByAConfigurableCeiling()
    {
        // These are the only tools that hold every match in memory before writing, and the count comes
        // from the client -- so the ceiling is what keeps them bounded.
        string exportTools = Read("src", "Yagu.McpServer", "Tools", "ExportTools.cs");

        Assert.Contains("BoundCollected(maxResults)", exportTools);
        Assert.Contains("options.MaxExportResults", exportTools);

        // A file that looks complete but is not is worse than one the caller knows is partial.
        Assert.Contains("DescribeCeiling(maxResults, bounded)", exportTools);
        Assert.Contains("may be incomplete", exportTools);

        // And it must be a user setting, not a hidden constant.
        Assert.Contains("McpMaxExportResults", McpTabSource);
        Assert.Contains("--max-export-results", Read("src", "Yagu", "Services", "Mcp", "McpConfigSnippetBuilder.cs"));
    }

    [Fact]
    public void OrphanedWorkerSweep_NeverTargetsTheMcpServer()
    {
        // The sweep kills workers from this install directory whose parent is not a live Yagu.exe. A
        // stdio MCP server is parented by VS Code / Visual Studio / Copilot CLI / Claude Desktop, and the
        // sign-in helper has no Yagu.exe parent at all, so adding either here would make the next GUI
        // launch kill a healthy session belonging to another program — or another user.
        Assert.DoesNotContain("Yagu.McpServer", Yagu.Services.OrphanedWorkerCleanup.WorkerProcessNames);

        foreach (string name in Yagu.Services.OrphanedWorkerCleanup.WorkerProcessNames)
            Assert.DoesNotContain("Mcp", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_DocumentsTheServerAndItsSettingsTab()
    {
        string help = Read("HELP.md");

        Assert.Contains("## MCP Server (AI Client Integration)", help);
        Assert.Contains("### MCP Server Tab", help);
        Assert.Contains("Copy configuration", help);

        // The remaining v1 limits are load-bearing: documenting remote access would promise a power the
        // server does not have.
        Assert.Contains("Loopback only", help);
        Assert.Contains("Archives are read-only", help);

        foreach (string tool in new[]
        {
            "yagu_search", "yagu_search_natural_language_start", "yagu_read_file", "yagu_calc",
            "yagu_replace_preview", "yagu_replace_commit", "yagu_export_report", "yagu_index_status",
            "yagu_index_add_root", "yagu_index_remove_root", "yagu_index_compact_start", "yagu_job_cancel",
        })
        {
            Assert.Contains(tool, help);
        }
    }

    [Fact]
    public void Help_DocumentsHowWritesAreAuthorizedAndBounded()
    {
        string help = Read("HELP.md");

        Assert.Contains("### Letting an assistant edit files", help);
        Assert.Contains("Off by default", help);

        // The two properties that make a commit safe must both be stated, because a reader deciding
        // whether to enable writes is relying on them.
        Assert.Contains("never re-runs the search pattern", help);
        Assert.Contains("rolled back", help);
        Assert.Contains(".yagubak", help);
    }

    [Fact]
    public void ClientRegistration_NeverDeclaresOneTransportWhileConfiguringAnother()
    {
        // The defect this pins: an entry that declares "stdio" while carrying the HTTP command line
        // leaves the client waiting on a pipe while the server listens on a socket. The server's launch
        // arguments belong to the sign-in task; a client entry is either stdio or an http URL.
        int install = McpClientSetupSource.IndexOf("internal static McpRegistrationResult Apply(", StringComparison.Ordinal);
        int describe = McpClientSetupSource.IndexOf("public static string Describe", StringComparison.Ordinal);
        Assert.InRange(install, 0, int.MaxValue);
        Assert.InRange(describe, install, int.MaxValue);

        string registration = McpClientSetupSource[install..describe];
        Assert.DoesNotContain("BuildHttpServerArguments", registration);

        Assert.Contains("McpConfigSnippetBuilder.SupportsHttpEntry(client)", registration);
        Assert.Contains("McpConfigSnippetBuilder.BuildHttpServerEntry(settings)", registration);
        Assert.Contains("McpConfigSnippetBuilder.BuildServerArguments(", registration);
    }

    [Fact]
    public void HttpRegistration_AlwaysCarriesTheTokenInputWithTheEntry()
    {
        // A ${input:} header with no matching definition is a broken entry, and an orphaned definition
        // keeps prompting for a server that is gone. Install and Remove must both handle the pair.
        foreach (string source in new[] { McpClientSetupSource, Read("src", "Yagu", "CliRunner.McpCommands.cs") })
        {
            Assert.Contains("McpConfigSnippetBuilder.TokenInputId", source);
            Assert.Contains("McpConfigSnippetBuilder.BuildTokenInput()", source);
        }
    }

    [Fact]
    public void CliAndSettingsAgreeOnWhenToUseTheHttpEntry()
    {
        // Same binary, same grant: installing from the CLI must not produce a different transport than
        // the Settings button for the same client and settings.
        const string gate = "settings.McpHttpEnabled && McpConfigSnippetBuilder.SupportsHttpEntry(client)";

        Assert.Contains(gate, McpClientSetupSource);
        Assert.Contains(gate, Read("src", "Yagu", "CliRunner.McpCommands.cs"));
        Assert.Contains(gate, Read("src", "Yagu", "CliRunner.McpConfig.cs"));
    }

    [Fact]
    public void ChangingGrants_RestartsTheRunningServerRatherThanOnlyRewritingTheTask()
    {
        // The server holds its roots and capabilities in the command line it was started with, and the
        // task's IgnoreNew policy blocks a replacement instance. Rewriting the task alone would keep a
        // folder the user just removed served until the next sign-in.
        Assert.Contains("McpLoginHelper.Restart(McpLoginHelper.RunSchtasks)", McpTabSource);

        string helper = Read("src", "Yagu", "Services", "Mcp", "McpLoginHelper.cs");
        Assert.Contains("internal static McpLoginHelperState Restart(SchtasksRunner run)", helper);
        Assert.Contains("RestartWith(run)", helper);

        // The /End-before-/Run ordering itself is proven at runtime by
        // McpLoginHelperTests.Restart_EndsTheOldInstanceBeforeStartingTheNewOne; this only pins that
        // the restart path still issues /End rather than /Run alone.
        Assert.Contains("run([\"/End\", \"/TN\", taskName ?? TaskName]);", helper);
    }

    [Fact]
    public void AutostartCliCommand_PersistsTheSettingItChanged()
    {
        // Task state and the persisted setting must agree, or Settings shows the opposite state and
        // silently re-applies it on the next save.
        string commands = Read("src", "Yagu", "CliRunner.McpCommands.cs");

        Assert.Contains("TryPersistAutostartState(settings, enabled: false)", commands);
        Assert.Contains("TryPersistAutostartState(settings, enabled: true)", commands);
        Assert.Contains("settings.McpStartAtLogin = enabled;", commands);
    }

    [Fact]
    public void ReplaceCommit_ConfinesEveryWriteAndRefusesALossyReencode()
    {
        string commit = Read("src", "Yagu", "CliRunner.ReplaceCommit.cs");

        // The commit is a SEPARATE process invocation replaying a recorded plan, so nothing else in that
        // run has proved the paths are still inside the grant. Confinement is the only containment on a
        // path that rewrites files, and it belongs in the preflight so a refusal writes nothing at all.
        AssertContainsInOrder(
            commit,
            "var refusals = new List<string>();",
            "if (s_confinement.IsActive && !s_confinement.Allows(file.Path))",
            "refusals.Add($\"{file.Path}: it resolves outside the allowed folders\");",
            "McpReplaceCommitPlan.ReadVerified(file, out _);");

        // The plan's hash proves the bytes on disk are unchanged; it proves nothing about decode/encode
        // being lossless. Both fallbacks are replacing, so one undecodable byte would be rewritten as
        // U+FFFD anywhere in the file and the commit would report success.
        Assert.Contains("if (!CommitReencodesExactly(rawBytes, original, encoding))", commit);
        Assert.Contains("private static bool CommitReencodesExactly(", commit);
        Assert.Contains("byte[] preamble = encoding.GetPreamble();", commit);

        // File.Copy gives the backup the DIRECTORY's DACL, not the original's.
        Assert.Contains("MirrorFileSecurity(file.Path, backup);", commit);

        // The server must pass the roots too, so the writing process enforces them independently of the
        // server's own bookkeeping.
        string replaceTool = Read("src", "Yagu.McpServer", "Tools", "ReplacePreviewTool.cs");
        AssertContainsInOrder(
            replaceTool,
            "List<string> arguments = [\"--replace-commit\", commitId];",
            "SearchTool.AddConfinement(arguments, policy);");
    }

    [Fact]
    public void ReplaceCommit_IsNotCancellableOnceStarted_AndKeepsTheIdAliveForTheTornBatchReport()
    {
        string bridge = Read("src", "Yagu.McpServer", "YaguCliBridge.cs");

        // Cancelling mid-commit kills the writer's process tree. Its rollback lives in a catch block, so
        // a killed writer leaves the batch half-applied. Waiting for the lease stays cancellable (nothing
        // has run yet); only the started process opts out.
        Assert.Contains("bool cancellableOnceStarted = true)", bridge);
        AssertContainsInOrder(
            bridge,
            "using IDisposable lease = await _concurrency.AcquireAsync(workload, cancellationToken)",
            "cancellableOnceStarted ? cancellationToken : CancellationToken.None)");

        string replaceTool = Read("src", "Yagu.McpServer", "Tools", "ReplacePreviewTool.cs");
        Assert.Contains("cancellableOnceStarted: false)", replaceTool);

        // Only a completed run released the .inflight claim, so only then is the id spent. Dropping the
        // scope on timeout made the re-issued id answer "not issued by this server" instead of producing
        // the interrupted-commit report the marker exists to deliver.
        AssertContainsInOrder(
            replaceTool,
            "if (result.Outcome == YaguCliOutcome.Completed)",
            "CommitScopes.TryRemove(commitId, out _);",
            "if (result.Outcome != YaguCliOutcome.Completed)",
            ".yagubak backups beside the affected files.");
    }

    [Fact]
    public void Bridge_BoundsTheStdoutDrainAndNeverHandsOutALiveRecordList()
    {
        string bridge = Read("src", "Yagu.McpServer", "YaguCliBridge.cs");

        // Yagu's workers inherit its stdout handle, so the pipe does not reach EOF just because the child
        // exited. An unbounded await there held the request -- and its concurrency lease -- for ever.
        Assert.Contains("await stdoutTask.WaitAsync(ReaderDrainGrace, linked.Token).ConfigureAwait(false);", bridge);
        Assert.Contains("catch (TimeoutException)", bridge);

        // The reader must stop touching the list before it is handed to a caller, and before the finally
        // block disposes the StreamReader under it.
        Assert.Contains("await QuiesceAsync(stdoutTask).ConfigureAwait(false);", bridge);
        Assert.Contains("Snapshot(records, recordsGate)", bridge);
        Assert.Contains("lock (recordsGate)", bridge);

        // Under stdio the server's own stdin is the client's JSON-RPC stream; an unredirected child
        // inherits that handle.
        Assert.Contains("RedirectStandardInput = true,", bridge);
        Assert.Contains("CloseStandardInput(process.StandardInput);", bridge);
        Assert.Contains("try { stdin.Close(); }", bridge);
        Assert.Contains("catch (IOException) { }", bridge);

        // CreateKillOnClose and Assign report failure by return value, so the old catch block was dead.
        Assert.Contains("if (job.IsInvalid || !job.Assign(process.Handle))", bridge);
    }

    [Fact]
    public void OutboundPaths_FailClosedWhenTheProducerHadToTruncateThem()
    {
        // A shortened path can name a directory that IS inside an allowed root while the real path is
        // not, so containment would be decided on a string that never existed on disk.
        string searchTool = Read("src", "Yagu.McpServer", "Tools", "SearchTool.cs");

        Assert.Contains("record.GetBoolean(\"pathTruncated\") == true", searchTool);
        Assert.Contains("!policy.AuthorizeOutboundPath(path).Allowed", searchTool);
    }

    [Fact]
    public void IndexRootListing_RequiresTheIndexCapability()
    {
        // Outbound filtering hides roots outside the allowed folders, but a client with no index rights
        // must not be able to enumerate which allowed folders are indexed, or their filter policy.
        string indexTools = Read("src", "Yagu.McpServer", "Tools", "IndexTools.cs");

        int roots = indexTools.IndexOf("public async Task<string> RootsAsync", StringComparison.Ordinal);
        Assert.InRange(roots, 0, int.MaxValue);

        string body = indexTools[roots..(roots + 900)];
        Assert.Contains("policy.HasCapability(McpCapability.IndexMaintenance)", body);
    }

    [Fact]
    public void ReplaceCommit_ReplaysRecordedEditsAndNeverRerunsThePattern()
    {
        string commit = Read("src", "Yagu", "CliRunner.ReplaceCommit.cs");

        // Enabling writes in settings is the human authorization step.
        Assert.Contains("settings.McpAllowReplaceWrites", commit);

        // Verify before touching, replay recorded edits, never rebuild a regex here. The verification
        // and the bytes that get edited must come from ONE read: verifying through one handle and then
        // re-opening the file to edit it lets a writer slip in between, so the previewed offsets would
        // be spliced into content nobody verified.
        Assert.Contains("McpReplaceCommitPlan.ReadVerified(file, out byte[] rawBytes)", commit);
        Assert.Contains("McpReplaceCommitPlan.ApplyEdits(original, file.Edits)", commit);
        Assert.DoesNotContain("new Regex(", commit);

        // Every file is checked BEFORE the first write: refusing a stale file part way through would
        // leave the earlier files rewritten with no way to tell which half landed.
        Assert.Contains("PREFLIGHT", commit);
        Assert.Contains("nothing was written", commit);

        // Backup, atomic swap, and whole-batch rollback.
        Assert.Contains(".yagubak", commit);
        Assert.Contains("File.Replace(temp, file.Path", commit);
        Assert.Contains("RollBack(committed);", commit);
    }

    [Fact]
    public void ReplaceCommit_CreatesItsTempFileExclusivelyUnderAnUnpredictableName()
    {
        string commit = Read("src", "Yagu", "CliRunner.ReplaceCommit.cs");

        // A fixed "<target>.yagu-commit-tmp" is a name an attacker can predict: an ordinary file there
        // would be overwritten and then deleted by the failure cleanup, and a link there would carry the
        // write to whatever it points at, outside the folders the preflight pass just enforced.
        Assert.DoesNotContain("file.Path + \".yagu-commit-tmp\"", commit);
        Assert.Contains("$\"{file.Path}.{Guid.NewGuid():n}.yagu-commit-tmp\"", commit);
        Assert.Contains("new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)", commit);

        // WriteAllText re-opens the path with Create, which reintroduces the gap CreateNew closes.
        Assert.DoesNotContain("File.WriteAllText(temp", commit);
    }

    [Fact]
    public void ReplaceCommitRollback_LeavesFilesThatChangedSinceTheyWereCommitted()
    {
        string commit = Read("src", "Yagu", "CliRunner.ReplaceCommit.cs");

        // A rollback undoes THIS batch, and it can only do that while the file still holds what this
        // batch wrote. Between an early file being committed and a later one failing, an editor or build
        // can save over it; restoring unconditionally would discard that save and then delete the only
        // copy of it along with the backup.
        Assert.Contains("private sealed record CommittedFile(string Path, string? BackupPath, string WrittenSha256);", commit);
        Assert.Contains("writtenSha256 = McpReplaceCommitPlan.HashFile(temp);", commit);

        // The hash check has to come BEFORE the restore, or it is not a guard.
        AssertContainsInOrder(commit,
            "private static RollBackOutcome RollBack(List<CommittedFile> committed)",
            "McpReplaceCommitPlan.HashFile(entry.Path), entry.WrittenSha256",
            "rollback-skipped",
            "File.Copy(entry.BackupPath, entry.Path, overwrite: true);");

        // A skipped or failed restore leaves a file still holding this batch's write, so the batch was
        // NOT fully undone. Reporting "every file was rolled back" regardless sent the caller away from
        // exactly the files that still need attention.
        Assert.Contains("private sealed record RollBackOutcome(int RolledBack, IReadOnlyList<string> Unresolved);", commit);
        Assert.Contains("rollback.Unresolved.Count == 0", commit);
        Assert.Contains("could not be and are still modified", commit);
    }

    [Fact]
    public void McpConfigCommand_IsDispatchedBesideCliAndDocumented()
    {
        string program = Read("src", "Yagu", "Program.cs");
        string command = Read("src", "Yagu", "CliRunner.McpConfig.cs");
        string cliHelp = Read("src", "Yagu", "CliRunner.cs");
        string help = Read("HELP.md");

        // It must count as a console command, or the GUI path would swallow it and hide the console.
        Assert.Contains("|| CliRunner.IsMcpCommand(args)", program);
        Assert.Contains(
            "if (string.Equals(command, McpConfigFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpConfig(args);",
            Read("src", "Yagu", "CliRunner.McpCommands.cs"));

        // Stdout stays pure so `--mcp-config > mcp.json` writes a usable file.
        Assert.Contains("Console.Out.WriteLine(McpConfigSnippetBuilder.BuildClientConfig(", command);
        Assert.Contains("Console.Error.WriteLine", command);

        // An MCP grant is per user; a workspace .yagu.json must never widen or narrow it silently.
        Assert.Contains("new SettingsService(SettingsService.ResolveInstanceSettingsPath()).Load()", command);

        // A config with no allowed folder cannot work, so it must not report success -- and it must fail
        // BEFORE writing, or redirecting to a file leaves a complete, non-working config behind.
        AssertContainsInOrder(
            command,
            "settings.McpAllowedRoots.Count == 0",
            "return CliExitCode.InvalidInput;",
            "Console.Out.WriteLine(McpConfigSnippetBuilder.BuildClientConfig(");

        Assert.Contains("--mcp-config", cliHelp);
        Assert.Contains("--mcp-client <client>", cliHelp);
        Assert.Contains("--mcp-config", help);
        Assert.Contains("### From the command line", help);
    }

    [Fact]
    public void HelpHtml_WasRegeneratedAlongsideHelpMarkdown()
    {
        // HELP.html is a generated, tracked artifact shipped as the in-app Help page; a stale one also
        // breaks the release build's post-build guard.
        string generated = Read("src", "Yagu", "HELP.html");

        Assert.Contains("mcp-server-ai-client-integration", generated);
        Assert.Contains("mcp-server-tab", generated);
    }

    [Fact]
    public void Readme_ListsTheServerAsAFeature()
    {
        string readme = Read("README.md");

        Assert.Contains("MCP Server (AI Client Integration)", readme);
        Assert.Contains("Settings \u25b8 MCP Server", readme);
    }

    [Fact]
    public void FirstRunPrompt_IsOfferedOnlyWhenTheServerIsInstalledAndNothingIsGrantedYet()
    {
        string plan = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupDialogProgress.cs");

        // Installing the server grants nothing, so without this the feature looks installed while every
        // client button stays disabled.
        Assert.Contains("McpAllowedRoots,", plan);
        Assert.Contains("if (WillShowMcpAllowedRootsPrompt())", plan);
        Assert.Contains("if (ViewModel.Settings.McpAllowedRootsPromptShown)", plan);
        Assert.Contains("if (ViewModel.Settings.McpAllowedRoots.Count > 0)", plan);
        Assert.Contains("File.Exists(McpConfigSnippetBuilder.ResolveServerExecutablePath())", plan);

        // Startup modals must be serialized through the awaited chain or they stack on each other.
        string startup = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs");
        Assert.Contains(
            "await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.McpAllowedRoots, CheckFirstRunMcpAllowedRootsAsync);",
            startup);

        // The plan only drives the progress badge — RunStartupDialogStepAsync awaits every step whether or
        // not it is in the plan — so a step that does not re-check its own condition runs on EVERY launch.
        string runner = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupDialogProgress.cs");
        Assert.Contains("await action().ConfigureAwait(true);", runner);

        string onboarding = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.McpOnboarding.cs");
        Assert.Contains("if (!WillShowMcpAllowedRootsPrompt())", onboarding);
    }

    [Fact]
    public void FirstRunPrompt_PicksSeveralFoldersWithoutBrowsingForEachOne()
    {
        string onboarding = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.McpOnboarding.cs");

        Assert.Contains("if (YaguDialog.HasOpenOwnedWindow(_hwnd))", onboarding);
        Assert.Contains("ShowTitleBar = false,", onboarding);

        // Checkboxes over suggested folders, with Browse as an addition rather than the only way in.
        Assert.Contains("new CheckBox", onboarding);
        Assert.Contains("BuildMcpRootSuggestions()", onboarding);
        Assert.Contains("Win32FileDialog.SelectFolder(dialogHwnd", onboarding);
        Assert.Contains("AddCandidate(folder, isChecked: true);", onboarding);

        // The prompt is answered once, however it was dismissed.
        Assert.Contains("settings.McpAllowedRootsPromptShown = true;", onboarding);
    }

    [Fact]
    public void FirstRunPrompt_CountsAGrantedFolderOnceAfterItIsCommitted()
    {
        string onboarding = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.McpOnboarding.cs");

        // Registering a client commits the checked folders, which stay checked, so summing the saved
        // roots and the selection would report every committed folder twice.
        Assert.DoesNotContain("settings.McpAllowedRoots.Count + SelectedRoots().Count", onboarding);
        Assert.Contains(
            "NormalizeMcpAllowedRoots([.. settings.McpAllowedRoots, .. SelectedRoots()]).Count",
            onboarding);
    }

    [Fact]
    public void FirstRunPrompt_OffersTheSameClientInstallAndRemoveControlsAsSettings()
    {
        string onboarding = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.McpOnboarding.cs");

        Assert.Contains("foreach (McpClientKind client in Enum.GetValues<McpClientKind>())", onboarding);
        Assert.Contains("McpClientSetup.Apply(settings, captured, install: true)", onboarding);
        Assert.Contains("McpClientSetup.Apply(settings, captured, install: false)", onboarding);

        // A client entry bakes the folders into its command line, so they must be saved BEFORE the
        // entry is generated — not when the dialog is finally dismissed.
        int commit = onboarding.IndexOf("CommitRoots();", StringComparison.Ordinal);
        int apply = onboarding.IndexOf("McpClientSetup.Apply(settings, captured, install: true)", StringComparison.Ordinal);
        Assert.True(commit >= 0 && apply > commit, "CommitRoots() must run before a client is registered.");
    }

    [Fact]
    public void BothSurfaces_RegisterClientsThroughTheOneSharedImplementation()
    {
        // Two hand-rolled copies would drift on the stdio/HTTP choice, which is what decides whether a
        // client talks to the server at all.
        Assert.Contains("McpClientSetup.Apply(settings, client, install)", McpTabSource);
        Assert.Contains("McpClientSetup.Describe(settings, client, result)", McpTabSource);
        Assert.DoesNotContain("McpClientRegistrar.Install(", McpTabSource);
    }

    [Fact]
    public void DisabledButtons_ExplainThemselvesThroughAnEnabledWrapper()
    {
        // A disabled WinUI control receives no pointer input, so a tooltip on the control itself never
        // shows — the one state that most needs an explanation is the one that cannot display it.
        string helper = Read("src", "Yagu", "UI", "Windows", "DisabledReasonTooltip.cs");
        Assert.Contains("host.Children.Add(control);", helper);
        Assert.Contains("ToolTipService.SetToolTip(", helper);
        Assert.Contains("control.IsEnabled || string.IsNullOrWhiteSpace(reason) ? null : reason", helper);

        // Every disabled control in the MCP tab carries a reason.
        Assert.Contains("WithDisabledReason(install,", McpTabSource);
        Assert.Contains("WithDisabledReason(remove,", McpTabSource);
        Assert.Contains("SetDisabledReason(portBoxHost, portBox, httpOffReason)", McpTabSource);
        Assert.Contains("SetDisabledReason(autostartHost, autostartToggle, httpOffReason)", McpTabSource);

        string onboarding = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.McpOnboarding.cs");
        Assert.Contains("DisabledReasonTooltip.Wrap(", onboarding);
    }

    [Fact]
    public void ConfigWrites_SurviveAScannerHoldingTheFileOpen()
    {
        // File.Replace throws "Unable to remove the file to be replaced" when an indexer or antivirus
        // has the destination open — measured at ~1.5% of replaces, which showed up as a client
        // registration that reported failure while nothing was actually wrong.
        string registrar = Read("src", "Yagu", "Services", "Mcp", "McpClientRegistrar.cs");
        Assert.Contains("const int replaceAttempts = 5;", registrar);
        Assert.Contains("catch (IOException) when (attempt < replaceAttempts)", registrar);

        // The retry must not widen the race the guard exists to close: identity is re-verified inside
        // the loop, before every attempt, not once before it.
        int loop = registrar.IndexOf("for (int attempt = 1; ; attempt++)", StringComparison.Ordinal);
        int verify = registrar.IndexOf("current.LastWriteTimeUtc != expectedWriteUtc", loop, StringComparison.Ordinal);
        int replace = registrar.IndexOf("File.Replace(temp, path", loop, StringComparison.Ordinal);
        Assert.True(loop >= 0 && verify > loop && replace > verify,
            "The identity re-check must sit inside the retry loop, ahead of File.Replace.");

        string tokenStore = Read("src", "Yagu", "Services", "Mcp", "McpTokenStore.cs");
        Assert.Contains("ReplaceWithRetry(temp, tokenPath);", tokenStore);
    }

    [Fact]
    public void McpCommandDetection_IsPositional_SoAValueCannotBecomeACommand()
    {
        // The bridge passes an agent-supplied query straight through as `--pattern <query>`, and
        // Program.Main dispatches MCP commands BEFORE the --cli branch. A flat scan over every token
        // therefore let a search for the literal text "--mcp-regenerate-token" rotate the loopback token
        // that every configured client holds. A human searching for that string hit it too.
        string commands = Read("src", "Yagu", "CliRunner.McpCommands.cs");

        Assert.Contains("private static string? FindMcpCommand(string[] args)", commands);
        Assert.Contains("public static bool IsMcpCommand(string[] args) => FindMcpCommand(args) is not null;", commands);

        // The scan stops at the first token outside the --mcp-* family, and skips the value that each
        // value-taking flag consumes.
        AssertContainsInOrder(
            commands,
            "if (McpCommandFlags.Contains(token, StringComparer.OrdinalIgnoreCase))",
            "return token;",
            "if (!McpValueFlags.Contains(token, StringComparer.OrdinalIgnoreCase))",
            "return null;",
            "i++;");

        // The old flat scan must not come back.
        Assert.DoesNotContain("args.Any(a => McpCommandFlags.Contains(a", commands);
    }

    [Fact]
    public void AllowedFoldersAndCapabilities_AreDiscardedOnCancelLikeEveryOtherSetting()
    {
        // These are edited through ad-hoc buttons, not a tracked control, so the generic clean-value
        // machinery cannot see them. Without their own snapshot, adding a folder and closing Settings
        // without saving left the grant live in memory, and the next save from anywhere wrote it to disk.
        Assert.Contains("private void CaptureCleanMcpListSettings()", McpTabSource);
        Assert.Contains("private void RestoreCleanMcpListSettings()", McpTabSource);
        Assert.Contains("private bool HaveMcpListSettingsChanged()", McpTabSource);

        // Cancel has to both DETECT the change and undo it, or the restore never runs.
        string settingsWindow = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");
        Assert.Contains("CaptureCleanMcpListSettings();", settingsWindow);
        Assert.Contains("RestoreCleanMcpListSettings();", settingsWindow);
        Assert.Contains("HaveMcpListSettingsChanged()", settingsWindow);
    }

    [Fact]
    public void RootSuggestions_HaveNoHiddenDisplayCeiling()
    {
        // The only open-ended source is RecentDirectories, which the user's own "Recent items" setting
        // already bounds. A second, hard-coded cap would silently hide folders they asked to remember.
        string onboarding = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.McpOnboarding.cs");

        Assert.DoesNotContain("McpRootSuggestionLimit", onboarding);
        Assert.Contains("ViewModel.Settings.RecentDirectories", onboarding);
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int index = 0;
        foreach (string part in expected)
        {
            int found = text.IndexOf(part, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{part}' after index {index}.");
            index = found + part.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
