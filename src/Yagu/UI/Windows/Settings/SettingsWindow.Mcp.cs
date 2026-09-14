using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Logging;
using Yagu.Services.Mcp;

namespace Yagu;

/// <summary>
/// The dedicated <b>MCP Server</b> settings tab (plan Phase 4, step 26). Yagu's MCP server is launched
/// by the AI client over stdio and takes its entire configuration from its own command line, so this tab
/// is where that configuration is authored: which folders a client may reach, which powers it is granted,
/// and how large a single response may get. The generated client snippet is the grant — a folder or
/// capability missing from it is a power the client does not get.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>What a client row renders. Gathered off the UI thread; never recomputed during layout.</summary>
    private sealed record McpClientState(McpClientKind Client, string Path, bool Installed, bool ConfigLocationExists);

    private StackPanel? _mcpRootsPanel;
    private StackPanel? _mcpClientRows;
    private TextBlock? _mcpClientStatus;
    private TextBlock? _mcpGrantSummary;
    private TextBlock? _mcpSnippetText;
    private TextBlock? _mcpSnippetLocation;

    /// <summary>Set when a save could not put the sign-in task into the state the settings describe.</summary>
    private string? _mcpAutostartWarning;

    /// <summary>
    /// The sign-in-task change currently being applied by the autostart toggle, or null when idle.
    /// Awaited by the Save path so a pending change cannot be overwritten by a stale snapshot.
    /// </summary>
    private Task? _mcpAutostartApply;

    /// <summary>Waits for any in-flight autostart toggle to finish. Never throws.</summary>
    private async Task AwaitPendingMcpAutostartAsync()
    {
        if (_mcpAutostartApply is not { } pending)
            return;

        try { await pending.ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("Mcp").LogDebug(ex, "The pending sign-in task change failed.");
        }
        finally { _mcpAutostartApply = null; }
    }

    private McpClientKind _mcpSnippetClient = McpClientKind.VsCode;

    // Allowed folders and capabilities are edited through ad-hoc buttons, not through a tracked control,
    // so the generic clean-value machinery cannot see them. Without their own snapshot, adding a folder
    // and then closing Settings without saving left the grant live in memory, and the next save from
    // anywhere in the app wrote it to disk.
    private List<string>? _cleanMcpAllowedRoots;
    private List<string>? _cleanMcpCapabilities;

    private void CaptureCleanMcpListSettings()
    {
        _cleanMcpAllowedRoots = [.. _viewModel.Settings.McpAllowedRoots];
        _cleanMcpCapabilities = [.. _viewModel.Settings.McpCapabilities];
    }

    private bool HaveMcpListSettingsChanged()
        => (_cleanMcpAllowedRoots is not null
                && !_cleanMcpAllowedRoots.SequenceEqual(_viewModel.Settings.McpAllowedRoots, StringComparer.OrdinalIgnoreCase))
            || (_cleanMcpCapabilities is not null
                && !_cleanMcpCapabilities.SequenceEqual(_viewModel.Settings.McpCapabilities, StringComparer.OrdinalIgnoreCase));

    private void RestoreCleanMcpListSettings()
    {
        if (_cleanMcpAllowedRoots is not null)
            _viewModel.Settings.McpAllowedRoots = [.. _cleanMcpAllowedRoots];
        if (_cleanMcpCapabilities is not null)
            _viewModel.Settings.McpCapabilities = [.. _cleanMcpCapabilities];
    }

    // Reading four client config files (and enumerating VS Code profiles) is filesystem work, so it is
    // snapshotted off the UI thread instead of repeating inside every tab refresh.
    private IReadOnlyList<McpClientState> _mcpClientStates = [];
    private IReadOnlyList<string> _mcpVsCodeCandidates = [];
    private bool _mcpServerExecutableExists;
    private bool _mcpClientStateLoaded;

    private void BuildMcpTab()
    {
        var g = AddTab("MCP Server");

        var overviewGroup = AddSettingsGroupBox(g, "Server");
        var rootsGroup = AddSettingsGroupBox(g, "Allowed Folders");
        var capabilityGroup = AddSettingsGroupBox(g, "Capabilities");
        var limitsGroup = AddSettingsGroupBox(g, "Response Limits");
        var jobsGroup = AddSettingsGroupBox(g, "Background Jobs");
        var httpGroup = AddSettingsGroupBox(g, "Loopback HTTP");
        var clientGroup = AddSettingsGroupBox(g, "Connect a Client");

        var reseed = new List<Action>();

        static TextBlock Description(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };

        // ── Server ──
        overviewGroup.Children.Add(new TextBlock
        {
            Text = "Yagu can act as a Model Context Protocol (MCP) server, letting an AI client such as VS Code, Visual Studio, Copilot CLI, or Claude Desktop run Yagu searches, read bounded windows of files, preview replacements, and manage content indexes on your behalf. Normally the client starts the server itself and talks to it over its standard input and output, so nothing listens on a network port. Nothing leaves this machine either way.",
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        string serverExePath = McpConfigSnippetBuilder.ResolveServerExecutablePath();
        bool serverStaged = File.Exists(serverExePath);
        overviewGroup.Children.Add(new TextBlock
        {
            Text = serverStaged
                ? $"Server: {serverExePath}"
                : $"The MCP server is not installed with this copy of Yagu (expected {serverExePath}). The settings below are still saved, but no client can start the server until it is present.",
            FontSize = 12,
            Opacity = serverStaged ? 0.85 : 1,
            TextWrapping = TextWrapping.Wrap,
        });

        _mcpGrantSummary = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        overviewGroup.Children.Add(_mcpGrantSummary);

        // ── Allowed folders ──
        rootsGroup.Children.Add(Description(
            "A client may only search, read, and write inside these folders \u2014 and every path a result carries is checked on the way back out too. There is deliberately no \u201call drives\u201d option: a server started with no folder authorizes nothing and refuses to run, which is the safe failure."));

        _mcpRootsPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
        rootsGroup.Children.Add(_mcpRootsPanel);

        var addRootButton = new Button { Content = "Add folder\u2026", Padding = new Thickness(12, 4, 12, 4) };
        addRootButton.Click += (_, _) =>
        {
            string? folder = Win32FileDialog.SelectFolder(_settingsHwnd, "Select Folder an MCP Client May Access");
            if (string.IsNullOrWhiteSpace(folder))
                return;

            var roots = new List<string>(_viewModel.Settings.McpAllowedRoots) { folder };
            _viewModel.Settings.McpAllowedRoots = AppSettings.NormalizeMcpAllowedRoots(roots);
            RefreshMcpTab();
            MarkSettingsDirty(requireValueChanges: false);
        };
        rootsGroup.Children.Add(MakeIndexButtonRow(addRootButton));

        // ── Capabilities ──
        capabilityGroup.Children.Add(Description(
            "Separate powers, because they carry very different risk. Reading a file you can already read is not the same as rewriting it, and deleting every content index is not the same as asking one for its status. A capability you leave off is never offered to the client."));

        void AddCapabilityToggle(McpCapability capability, string label, string description)
        {
            var toggle = new ToggleSwitch
            {
                OnContent = label,
                OffContent = label,
                IsOn = HasMcpCapability(capability),
                MinWidth = 0,
            };
            toggle.Toggled += (_, _) =>
            {
                SetMcpCapability(capability, toggle.IsOn);
                RefreshMcpTab();
            };
            capabilityGroup.Children.Add(toggle);
            capabilityGroup.Children.Add(Description(description));
            reseed.Add(() => toggle.IsOn = HasMcpCapability(capability));
        }

        AddCapabilityToggle(McpCapability.Read, "Read files",
            "Return a bounded window of lines from a file inside an allowed folder.");
        AddCapabilityToggle(McpCapability.Search, "Search",
            "Run searches inside an allowed folder, including natural-language (AI) search. Each call requires an explicit folder.");
        AddCapabilityToggle(McpCapability.CloudHydration, "Touch online-only cloud files",
            "Allow searches to open online-only placeholders, which forces the cloud provider to download them. Off by default because it can be slow and metered.");
        AddCapabilityToggle(McpCapability.ExportWrite, "Write reports and sessions",
            "Allow exporting a report or saving a session. The folder the file would land in must itself be an allowed folder.");
        AddCapabilityToggle(McpCapability.Replace, "Preview replacements",
            "Allow the replacement preview, which returns a bounded diff and counts. On its own this grants a preview only \u2014 writing is a separate switch below.");
        AddCapabilityToggle(McpCapability.IndexMaintenance, "Manage content indexes",
            "Allow viewing index status and registered folders, building, validating, compacting, and registering allowed folders. Rebuilding and unregistering require the separate permission below.");
        AddCapabilityToggle(McpCapability.DestructiveIndexOps, "Rebuild or unregister indexes",
            "Allow rebuilding an index from scratch or unregistering an indexed folder inside an allowed folder. Also requires Manage content indexes. Unregistering leaves stored index data intact. No MCP tool deletes an index or clears all indexes.");

        var writeToggle = new ToggleSwitch
        {
            OnContent = "Let it actually rewrite files",
            OffContent = "Let it actually rewrite files",
            IsOn = _viewModel.Settings.McpAllowReplaceWrites,
            MinWidth = 0,
        };
        writeToggle.Toggled += (_, _) => _viewModel.Settings.McpAllowReplaceWrites = writeToggle.IsOn;
        capabilityGroup.Children.Add(writeToggle);
        capabilityGroup.Children.Add(Description(
            "Off by default. Turning this on is the decision, made by you \u2014 an assistant confirming its own request is not a safeguard, because the same model that chose to edit your files sets the flag. When it is on, an assistant can apply a change it previewed: it commits the exact edits you were shown, refuses any file that changed in between rather than re-running the pattern over new content, writes a .yagubak backup beside each file, and rolls the whole batch back if any file fails."));
        reseed.Add(() => writeToggle.IsOn = _viewModel.Settings.McpAllowReplaceWrites);

        // ── Response limits ──
        limitsGroup.Children.Add(Description(
            "Ceilings for a single response, measured in UTF-8 bytes where noted. A match count bounds nothing on its own: one minified line, OCR block, or archive member name can exceed a whole sensible response. Each field is bounded independently, and truncated text is reported as truncated so a client can tell it apart from complete text."));

        NumberBox AddMcpNumberTo(Panel target, string label, double min, double max, Func<AppSettings, double> get, Action<AppSettings, double> set, string description)
        {
            target.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var box = new NumberBox
            {
                Value = get(_viewModel.Settings),
                Minimum = min,
                Maximum = max,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = NumericSettingBoxWidth,
            };
            box.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                {
                    set(_viewModel.Settings, args.NewValue);
                    // Concurrency and export ceilings are baked into the copied snippet's arguments, so a
                    // stale snippet would hand the client the limit the user just changed away from.
                    RefreshMcpTab();
                }
            };
            target.Children.Add(box);
            target.Children.Add(Description(description));
            reseed.Add(() => box.Value = get(_viewModel.Settings));
            return box;
        }

        NumberBox AddMcpNumber(string label, double min, double max, Func<AppSettings, double> get, Action<AppSettings, double> set, string description)
            => AddMcpNumberTo(limitsGroup, label, min, max, get, set, description);

        AddMcpNumber(
            "Largest match line (bytes):",
            MachineOutputBudget.MinLineBytes, MachineOutputBudget.MaxLineBytesCeiling,
            s => s.McpMaxLineBytes,
            (s, v) => s.McpMaxLineBytes = (int)v,
            $"Range {MachineOutputBudget.MinLineBytes:N0}\u2013{MachineOutputBudget.MaxLineBytesCeiling:N0}; default {MachineOutputBudget.Default.MaxLineBytes:N0}. A longer line is cut at a code-point boundary and reported with its true original size.");
        AddMcpNumber(
            "Largest context line (bytes):",
            MachineOutputBudget.MinLineBytes, MachineOutputBudget.MaxLineBytesCeiling,
            s => s.McpMaxContextLineBytes,
            (s, v) => s.McpMaxContextLineBytes = (int)v,
            $"Range {MachineOutputBudget.MinLineBytes:N0}\u2013{MachineOutputBudget.MaxLineBytesCeiling:N0}; default {MachineOutputBudget.Default.MaxContextLineBytes:N0}.");
        AddMcpNumber(
            "Largest path (bytes):",
            MachineOutputBudget.MinPathBytes, MachineOutputBudget.MaxPathBytesCeiling,
            s => s.McpMaxPathBytes,
            (s, v) => s.McpMaxPathBytes = (int)v,
            $"Range {MachineOutputBudget.MinPathBytes:N0}\u2013{MachineOutputBudget.MaxPathBytesCeiling:N0}; default {MachineOutputBudget.Default.MaxPathBytes:N0}.");
        AddMcpNumber(
            "Context lines per match:",
            MachineOutputBudget.MinContextLines, MachineOutputBudget.MaxContextLinesCeiling,
            s => s.McpMaxContextLines,
            (s, v) => s.McpMaxContextLines = (int)v,
            $"Range {MachineOutputBudget.MinContextLines:N0}\u2013{MachineOutputBudget.MaxContextLinesCeiling:N0}; default {MachineOutputBudget.Default.MaxContextLines:N0}. Also caps the replacement preview's line diff.");
        AddMcpNumber(
            "Records per response:",
            MachineOutputBudget.MinRecords, MachineOutputBudget.MaxRecordsCeiling,
            s => s.McpMaxRecords,
            (s, v) => s.McpMaxRecords = (int)v,
            $"Range {MachineOutputBudget.MinRecords:N0}\u2013{MachineOutputBudget.MaxRecordsCeiling:N0}; default {MachineOutputBudget.Default.MaxRecords:N0}. Each call returns one bounded page and reports whether it was truncated; there is no cross-call paging, because every call is a fresh scan with no stable snapshot to page over.");
        AddMcpNumber(
            "Whole response (bytes):",
            MachineOutputBudget.MinResponseBytes, MachineOutputBudget.MaxResponseBytesCeiling,
            s => s.McpMaxResponseBytes,
            (s, v) => s.McpMaxResponseBytes = (long)v,
            $"Range {MachineOutputBudget.MinResponseBytes:N0}\u2013{MachineOutputBudget.MaxResponseBytesCeiling:N0}; default {MachineOutputBudget.Default.MaxResponseBytes:N0}. Enforced as records are written, so a response stops at the ceiling instead of being assembled and then discarded.");
        AddMcpNumber(
            "Concurrent calls per server:",
            AppSettings.MinimumMcpMaxConcurrentCalls, AppSettings.MaximumMcpMaxConcurrentCalls,
            s => s.McpMaxConcurrentCalls,
            (s, v) => s.McpMaxConcurrentCalls = (int)v,
            $"Range {AppSettings.MinimumMcpMaxConcurrentCalls}\u2013{AppSettings.MaximumMcpMaxConcurrentCalls}; default {AppSettings.DefaultMcpMaxConcurrentCalls}. Several bounded calls running at once can still add up, so raise this only if your client benefits from it.");
        AddMcpNumber(
            "Matches collected for an export or session:",
            AppSettings.MinimumMcpMaxExportResults, AppSettings.MaximumMcpMaxExportResults,
            s => s.McpMaxExportResults,
            (s, v) => s.McpMaxExportResults = (int)v,
            $"Range {AppSettings.MinimumMcpMaxExportResults:N0}\u2013{AppSettings.MaximumMcpMaxExportResults:N0}; default {AppSettings.DefaultMcpMaxExportResults:N0}. Exporting a report or saving a session holds every match in memory before writing, unlike a search, which streams. This caps how many an assistant can ask for; it is told when the cap shortened the result, so a partial file is never mistaken for a complete one.");

        // ── Background jobs ──
        jobsGroup.Children.Add(Description(
            "Index maintenance and natural-language search outlive a single tool call, so the server hands back a job id and the assistant polls it. These bound that machinery. Timeouts are wall-clock ceilings, not expected durations \u2014 the right value depends on your disks and your hardware, so they are yours to set rather than fixed in the server."));

        AddMcpNumberTo(
            jobsGroup,
            "Jobs running at once:",
            AppSettings.MinimumMcpMaxRunningJobs, AppSettings.MaximumMcpMaxRunningJobs,
            s => s.McpMaxRunningJobs,
            (s, v) => s.McpMaxRunningJobs = (int)v,
            $"Range {AppSettings.MinimumMcpMaxRunningJobs:N0}\u2013{AppSettings.MaximumMcpMaxRunningJobs:N0}; default {AppSettings.DefaultMcpMaxRunningJobs:N0}. Further starts are refused rather than queued, so an assistant that keeps launching index builds cannot accumulate unbounded work.");
        AddMcpNumberTo(
            jobsGroup,
            "Finished jobs kept for polling:",
            AppSettings.MinimumMcpMaxRetainedJobs, AppSettings.MaximumMcpMaxRetainedJobs,
            s => s.McpMaxRetainedJobs,
            (s, v) => s.McpMaxRetainedJobs = (int)v,
            $"Range {AppSettings.MinimumMcpMaxRetainedJobs:N0}\u2013{AppSettings.MaximumMcpMaxRetainedJobs:N0}; default {AppSettings.DefaultMcpMaxRetainedJobs:N0}. Oldest first once the limit is reached. A job whose result has been dropped reports that it is unknown rather than reporting success with nothing in it.");
        AddMcpNumberTo(
            jobsGroup,
            "Characters kept from a job result:",
            AppSettings.MinimumMcpMaxJobResultChars, AppSettings.MaximumMcpMaxJobResultChars,
            s => s.McpMaxJobResultChars,
            (s, v) => s.McpMaxJobResultChars = (int)v,
            $"Range {AppSettings.MinimumMcpMaxJobResultChars:N0}\u2013{AppSettings.MaximumMcpMaxJobResultChars:N0}; default {AppSettings.DefaultMcpMaxJobResultChars:N0}. A longer result is truncated and says so, because the whole result is held in memory until the job is dropped.");
        AddMcpNumberTo(
            jobsGroup,
            "Ordinary call timeout (seconds):",
            AppSettings.MinimumMcpCallTimeoutSeconds, AppSettings.MaximumMcpCallTimeoutSeconds,
            s => s.McpCallTimeoutSeconds,
            (s, v) => s.McpCallTimeoutSeconds = (int)v,
            $"Range {AppSettings.MinimumMcpCallTimeoutSeconds:N0}\u2013{AppSettings.MaximumMcpCallTimeoutSeconds:N0}; default {AppSettings.DefaultMcpCallTimeoutSeconds:N0}. Applies to searches, reads and index status. On expiry the child process and its whole worker tree are killed, so nothing is left running.");
        AddMcpNumberTo(
            jobsGroup,
            "Index maintenance timeout (minutes):",
            AppSettings.MinimumMcpMaintenanceTimeoutMinutes, AppSettings.MaximumMcpMaintenanceTimeoutMinutes,
            s => s.McpMaintenanceTimeoutMinutes,
            (s, v) => s.McpMaintenanceTimeoutMinutes = (int)v,
            $"Range {AppSettings.MinimumMcpMaintenanceTimeoutMinutes:N0}\u2013{AppSettings.MaximumMcpMaintenanceTimeoutMinutes:N0}; default {AppSettings.DefaultMcpMaintenanceTimeoutMinutes:N0}. Building an index over a large tree on a spinning disk can legitimately take hours; on an NVMe drive the same work is minutes.");
        AddMcpNumberTo(
            jobsGroup,
            "Model-backed call timeout (minutes):",
            AppSettings.MinimumMcpModelTimeoutMinutes, AppSettings.MaximumMcpModelTimeoutMinutes,
            s => s.McpModelTimeoutMinutes,
            (s, v) => s.McpModelTimeoutMinutes = (int)v,
            $"Range {AppSettings.MinimumMcpModelTimeoutMinutes:N0}\u2013{AppSettings.MaximumMcpModelTimeoutMinutes:N0}; default {AppSettings.DefaultMcpModelTimeoutMinutes:N0}. Natural-language search loads the on-device model per call, and measured runs ranged from about nine seconds to many minutes on the same model. Also bounds how long one such call waits behind another.");

        // ── Loopback HTTP ──
        httpGroup.Children.Add(Description(
            "Most clients start the server themselves and talk to it over stdio, which needs nothing here. Turn this on only for a client that connects to a URL instead. The endpoint is bound to loopback on this machine \u2014 it is never reachable from the network \u2014 and every request must present a token that is stored in your profile, never on a command line where any process could read it."));

        var httpToggle = new ToggleSwitch
        {
            OnContent = "Serve over loopback HTTP",
            OffContent = "Serve over loopback HTTP",
            IsOn = _viewModel.Settings.McpHttpEnabled,
            MinWidth = 0,
        };
        httpGroup.Children.Add(httpToggle);
        reseed.Add(() => httpToggle.IsOn = _viewModel.Settings.McpHttpEnabled);

        httpGroup.Children.Add(new TextBlock { Text = "Loopback port:", TextWrapping = TextWrapping.Wrap });
        var portBox = new NumberBox
        {
            Value = _viewModel.Settings.McpHttpPort,
            Minimum = AppSettings.MinimumMcpHttpPort,
            Maximum = AppSettings.MaximumMcpHttpPort,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = NumericSettingBoxWidth,
        };
        portBox.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue))
            {
                _viewModel.Settings.McpHttpPort = AppSettings.NormalizeMcpHttpPort((int)args.NewValue);
                RefreshMcpTab();
            }
        };
        var portBoxHost = DisabledReasonHost(portBox);
        httpGroup.Children.Add(portBoxHost);
        httpGroup.Children.Add(Description(
            $"Range {AppSettings.MinimumMcpHttpPort}\u2013{AppSettings.MaximumMcpHttpPort}; default {AppSettings.DefaultMcpHttpPort}. If another program already holds the port the server will fail to start \u2014 pick a different one."));
        reseed.Add(() => portBox.Value = _viewModel.Settings.McpHttpPort);

        var autostartToggle = new ToggleSwitch
        {
            OnContent = "Start it when I sign in",
            OffContent = "Start it when I sign in",
            IsOn = _viewModel.Settings.McpStartAtLogin,
            MinWidth = 0,
        };
        var autostartHost = DisabledReasonHost(autostartToggle);
        httpGroup.Children.Add(autostartHost);
        httpGroup.Children.Add(Description(
            "Registers a scheduled task that runs the server as you, so it is available whether or not Yagu is open. It never runs as SYSTEM: the server must only ever reach files you can already reach. Removing Yagu removes the task."));
        reseed.Add(() => autostartToggle.IsOn = _viewModel.Settings.McpStartAtLogin);

        var httpStatus = Description(string.Empty);

        void ApplyHttpState()
        {
            portBox.IsEnabled = httpToggle.IsOn;
            autostartToggle.IsEnabled = httpToggle.IsOn;

            const string httpOffReason =
                "Turn on \u201cServe over loopback HTTP\u201d first \u2014 this setting only applies to the HTTP endpoint.";
            SetDisabledReason(portBoxHost, portBox, httpOffReason);
            SetDisabledReason(autostartHost, autostartToggle, httpOffReason);
        }

        httpToggle.Toggled += (_, _) =>
        {
            _viewModel.Settings.McpHttpEnabled = httpToggle.IsOn;
            ApplyHttpState();
            if (!httpToggle.IsOn && autostartToggle.IsOn)
            {
                autostartToggle.IsOn = false;
                _viewModel.Settings.McpStartAtLogin = false;
            }
            RefreshMcpTab();
        };

        autostartToggle.Toggled += (_, _) =>
        {
            bool requested = autostartToggle.IsOn;

            // Register + Restart is up to four schtasks.exe runs, each with a 15s timeout. On the UI
            // thread that is a frozen window; the toggle is disabled instead so the state cannot race.
            autostartToggle.IsEnabled = false;
            httpStatus.Text = requested ? "Registering the scheduled task\u2026" : "Removing the scheduled task\u2026";

            // Published so Save can await it. As a bare async void handler this ran unobserved, and a
            // Save landing mid-flight persisted the PREVIOUS value and then marked settings clean — so
            // the correction this continuation makes was discarded and never written.
            _mcpAutostartApply = ApplyAutostartToggleAsync();

            async Task ApplyAutostartToggleAsync()
            {
                McpAutostartApplyResult result = await Task.Run(() => ApplyMcpAutostart(requested)).ConfigureAwait(true);

                httpStatus.Text = result.Message;

                // The setting and the scheduled task have to agree. Persisting the requested state after a
                // failed register or remove leaves Settings showing the opposite of what the machine does.
                if (result.Applied)
                {
                    _viewModel.Settings.McpStartAtLogin = requested;
                }
                else if (autostartToggle.IsOn != _viewModel.Settings.McpStartAtLogin)
                {
                    autostartToggle.IsOn = _viewModel.Settings.McpStartAtLogin;
                }

                autostartToggle.IsEnabled = httpToggle.IsOn;
            }
        };

        var regenerateButton = new Button { Content = "Regenerate token", Padding = new Thickness(12, 4, 12, 4) };
        regenerateButton.Click += (_, _) =>
        {
            // The token is never displayed or copied to the clipboard: every client reads it from the
            // same per-user file the server does, and a token on screen is a token in a screenshot.
            httpStatus.Text = McpTokenStore.Regenerate(McpTokenStore.DirectoryPath) is null
                ? $"Could not write the token file at {McpTokenStore.TokenPath}."
                : "A new token was generated. Restart the server; any client holding the old token will be refused.";
        };
        httpGroup.Children.Add(MakeIndexButtonRow(regenerateButton));
        httpGroup.Children.Add(httpStatus);
        ApplyHttpState();

        // ── Connect a client ──
        clientGroup.Children.Add(Description(
            "Yagu can write its entry into a client's configuration for you. It merges \u2014 it never replaces the file \u2014 so other servers, comments, and settings Yagu does not recognize are kept, and Remove takes out only Yagu's own entry. A file that cannot be parsed is left untouched rather than repaired."));

        _mcpClientRows = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        clientGroup.Children.Add(_mcpClientRows);

        _mcpClientStatus = Description(string.Empty);
        clientGroup.Children.Add(_mcpClientStatus);

        clientGroup.Children.Add(new TextBlock
        {
            Text = "Or copy it yourself",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        });
        clientGroup.Children.Add(Description(
            "Merge this into the client's configuration file \u2014 do not replace the file, which may already list other servers. Re-copy it after changing anything above; the snippet is what actually grants the folders and capabilities."));

        var clientRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 6, 0, 0) };
        foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
        {
            var radio = new RadioButton
            {
                Content = McpConfigSnippetBuilder.DisplayName(client),
                GroupName = "McpSnippetClient",
                IsChecked = client == _mcpSnippetClient,
                Tag = client,
            };
            radio.Checked += (sender, _) =>
            {
                if (sender is RadioButton { Tag: McpClientKind selected })
                {
                    _mcpSnippetClient = selected;
                    RefreshMcpTab();
                }
            };
            clientRow.Children.Add(radio);
        }
        clientGroup.Children.Add(clientRow);

        _mcpSnippetLocation = Description(string.Empty);
        clientGroup.Children.Add(_mcpSnippetLocation);

        _mcpSnippetText = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        var snippetSurface = new Border
        {
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 260,
                Content = _mcpSnippetText,
            },
        };
        AppThemeService.ApplyThemedGroupBoxSurface(snippetSurface);
        clientGroup.Children.Add(snippetSurface);

        var copyButton = new Button { Content = "Copy configuration", Padding = new Thickness(12, 4, 12, 4) };
        var copyStatus = Description(string.Empty);
        copyButton.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(_mcpSnippetText?.Text ?? string.Empty);
            Clipboard.SetContent(package);
            copyStatus.Text = $"Copied the {McpConfigSnippetBuilder.DisplayName(_mcpSnippetClient)} configuration to the clipboard.";
        };
        clientGroup.Children.Add(MakeIndexButtonRow(copyButton));
        clientGroup.Children.Add(copyStatus);

        // ── Restore defaults ──
        var restoreDefaults = new Button
        {
            Content = "Restore MCP defaults",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 12, 0, 0),
        };
        restoreDefaults.Click += (_, _) =>
        {
            _viewModel.Settings.ResetMcpServerSettings();
            foreach (var apply in reseed)
                apply();
            RefreshMcpTab();
            MarkSettingsDirty(requireValueChanges: false);
        };
        g.Children.Add(restoreDefaults);
        g.Children.Add(Description(
            "Resets everything on this tab, including clearing the allowed folders \u2014 which leaves the server authorizing nothing until you add one back. Click Save afterwards to keep the defaults."));

        RefreshMcpTab();
        ReloadMcpClientStateAsync();
    }

    private bool HasMcpCapability(McpCapability capability) =>
        _viewModel.Settings.McpCapabilities.Contains(capability.ToString(), StringComparer.OrdinalIgnoreCase);
    private void SetMcpCapability(McpCapability capability, bool granted)
    {
        var names = new List<string>(_viewModel.Settings.McpCapabilities);
        names.RemoveAll(n => string.Equals(n, capability.ToString(), StringComparison.OrdinalIgnoreCase));
        if (granted)
            names.Add(capability.ToString());
        _viewModel.Settings.McpCapabilities = AppSettings.NormalizeMcpCapabilities(names);
    }

    /// <summary>
    /// Re-renders everything derived from the current configuration: the allowed-folder rows, the grant
    /// summary, and the client snippet. The snippet is regenerated on every change because it, not the
    /// settings file, is what a client acts on.
    /// </summary>
    private void RefreshMcpTab()
    {
        RefreshMcpRootRows();
        RefreshMcpClientRows();

        var settings = _viewModel.Settings;
        if (_mcpGrantSummary is not null)
        {
            int rootCount = settings.McpAllowedRoots.Count;
            int capabilityCount = settings.McpCapabilities.Count;
            _mcpGrantSummary.Text = rootCount == 0
                ? "No folder is allowed yet, so a client that starts the server will be told it authorizes nothing and the server will refuse to run."
                : $"Clients may reach {rootCount:N0} folder{(rootCount == 1 ? string.Empty : "s")} with {capabilityCount:N0} capabilit{(capabilityCount == 1 ? "y" : "ies")} granted.";
        }

        if (_mcpSnippetLocation is not null)
        {
            _mcpSnippetLocation.Text =
                $"Merge into {McpConfigSnippetBuilder.ConfigLocation(_mcpSnippetClient)} for {McpConfigSnippetBuilder.DisplayName(_mcpSnippetClient)}.";
        }

        if (_mcpSnippetText is not null)
        {
            // Must match what Install writes for this client, or the copied snippet configures a
            // different transport than the button does.
            _mcpSnippetText.Text =
                settings.McpHttpEnabled && McpConfigSnippetBuilder.SupportsHttpEntry(_mcpSnippetClient)
                    ? McpConfigSnippetBuilder.BuildHttpClientConfig(_mcpSnippetClient, settings)
                    : McpConfigSnippetBuilder.BuildClientConfig(
                        _mcpSnippetClient,
                        McpConfigSnippetBuilder.ResolveServerExecutablePath(),
                        McpConfigSnippetBuilder.BuildServerArguments(
                            settings, McpConfigSnippetBuilder.ResolveYaguExecutablePath()));
        }
    }

    private void RefreshMcpRootRows()
    {
        if (_mcpRootsPanel is null)
            return;

        _mcpRootsPanel.Children.Clear();

        var roots = _viewModel.Settings.McpAllowedRoots;
        if (roots.Count == 0)
        {
            _mcpRootsPanel.Children.Add(new TextBlock
            {
                Text = "No folders allowed.",
                FontSize = 12,
                Opacity = 0.6,
            });
            return;
        }

        foreach (string root in roots)
        {
            string captured = root;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = captured,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            });

            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2) };
            remove.Click += (_, _) =>
            {
                var remaining = new List<string>(_viewModel.Settings.McpAllowedRoots);
                remaining.RemoveAll(r => string.Equals(r, captured, StringComparison.OrdinalIgnoreCase));
                _viewModel.Settings.McpAllowedRoots = AppSettings.NormalizeMcpAllowedRoots(remaining);
                RefreshMcpTab();
                MarkSettingsDirty(requireValueChanges: false);
            };
            row.Children.Add(remove);

            _mcpRootsPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// One row per client: where its configuration lives, whether Yagu is in it, and the buttons that
    /// merge Yagu's entry in or take it back out.
    /// </summary>
    private void RefreshMcpClientRows()
    {
        if (_mcpClientRows is null)
            return;

        _mcpClientRows.Children.Clear();

        if (!_mcpClientStateLoaded)
        {
            _mcpClientRows.Children.Add(Description("Checking which clients Yagu is installed in\u2026"));
            return;
        }

        bool serverInstalled = _mcpServerExecutableExists;

        foreach (McpClientState state in _mcpClientStates)
        {
            McpClientKind captured = state.Client;
            bool installed = state.Installed;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = McpConfigSnippetBuilder.DisplayName(captured),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 120,
            });
            row.Children.Add(new TextBlock
            {
                Text = installed ? "installed" : "not installed",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = installed ? 0.9 : 0.55,
                Width = 90,
            });

            var install = new Button
            {
                Content = installed ? "Reinstall" : "Install",
                Padding = new Thickness(10, 2, 10, 2),
                // The MCP server is an optional install, so it can be absent entirely. Registering a
                // client against a path with no executable behind it reports success and then fails
                // silently inside the client. Installing for a client that is not on this machine would
                // create its configuration directory for it, so that is blocked too.
                IsEnabled = _viewModel.Settings.McpAllowedRoots.Count > 0
                    && serverInstalled
                    && state.ConfigLocationExists,
            };
            install.Click += (_, _) => ApplyMcpRegistration(captured, install: true);
            row.Children.Add(WithDisabledReason(install,
                McpClientSetup.DescribeInstallBlock(
                    serverInstalled, _viewModel.Settings.McpAllowedRoots.Count, state.ConfigLocationExists)));

            var remove = new Button
            {
                Content = "Remove",
                Padding = new Thickness(10, 2, 10, 2),
                IsEnabled = installed,
            };
            remove.Click += (_, _) => ApplyMcpRegistration(captured, install: false);
            row.Children.Add(WithDisabledReason(remove, McpClientSetup.DescribeRemoveBlock(installed)));

            _mcpClientRows.Children.Add(row);
        }

        if (_viewModel.Settings.McpAllowedRoots.Count == 0)
        {
            _mcpClientRows.Children.Add(Description(
                "Add an allowed folder above before installing \u2014 a server with no folder refuses to run."));
        }

        if (!serverInstalled)
        {
            _mcpClientRows.Children.Add(Description(
                "The MCP server is not part of this installation, so there is nothing for a client to run. "
                + "Re-run the Yagu installer and tick \u201cInstall the MCP server\u201d."));
        }

        // Yagu writes ONE file. Saying which, when several exist, is what stops "installed" from meaning
        // an edition or profile the user does not actually run.
        if (_mcpVsCodeCandidates.Count > 1)
        {
            _mcpClientRows.Children.Add(Description(
                $"VS Code has {_mcpVsCodeCandidates.Count} configurations (editions or profiles). Yagu uses the most "
                + $"recently changed one: {_mcpVsCodeCandidates[0]}. Merge the snippet below by hand for any other."));
        }

        static TextBlock Description(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>
    /// Re-reads every client's configuration off the UI thread and re-renders the rows when it lands.
    /// </summary>
    private void ReloadMcpClientStateAsync()
    {
        _ = Task.Run(() =>
        {
            var states = new List<McpClientState>();
            bool serverExists = false;
            IReadOnlyList<string> vsCode = [];

            try
            {
                serverExists = File.Exists(McpConfigSnippetBuilder.ResolveServerExecutablePath());
                vsCode = McpConfigSnippetBuilder.VsCodeConfigCandidates();

                foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
                {
                    string path = McpConfigSnippetBuilder.ResolveConfigPath(client);
                    string container = McpConfigSnippetBuilder.ServersPropertyName(client);
                    bool installed = McpClientRegistrar.IsInstalled(path, container, McpConfigSnippetBuilder.ServerName);

                    string? directory = Path.GetDirectoryName(path);
                    bool present = File.Exists(path)
                        || (!string.IsNullOrEmpty(directory) && Directory.Exists(directory));

                    states.Add(new McpClientState(client, path, installed, present));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A client whose configuration cannot be read is reported as not installed rather than
                // taking the Settings window down with it.
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                _mcpClientStates = states;
                _mcpVsCodeCandidates = vsCode;
                _mcpServerExecutableExists = serverExists;
                _mcpClientStateLoaded = true;
                RefreshMcpClientRows();
            });
        });
    }

    /// <summary>
    /// Writes (or removes) Yagu's entry in one client's configuration, using the settings as they stand
    /// right now rather than the last saved ones, so what the user just changed is what gets installed.
    /// </summary>
    private void ApplyMcpRegistration(McpClientKind client, bool install)
    {
        AppSettings settings = _viewModel.Settings;
        McpRegistrationResult result = McpClientSetup.Apply(settings, client, install);

        if (_mcpClientStatus is not null)
            _mcpClientStatus.Text = McpClientSetup.Describe(settings, client, result);

        ReloadMcpClientStateAsync();
    }

    /// <summary>
    /// Re-applies the grant everywhere it is already recorded, after settings are saved.
    /// </summary>
    /// <remarks>
    /// A client's configuration stores the server's <b>command line</b>, and that command line <b>is</b>
    /// the grant. Rewriting only the settings file would leave every already-registered client launching
    /// the server with the folders and capabilities the user just took away — indefinitely, since nothing
    /// else ever rewrites that entry. The sign-in task stores the same command line and needs the same
    /// treatment.
    /// </remarks>
    private void RefreshMcpAutostartRegistration()
    {
        AppSettings settings = _viewModel.Settings;
        var failures = new List<string>();

        foreach (McpClientState state in _mcpClientStates)
        {
            if (!state.Installed)
                continue;

            // The result cannot be discarded: this rewrite IS the revocation. A client whose config
            // still holds the old command line goes on launching the server with the folders and
            // capabilities the user just removed, and nothing else ever rewrites that entry.
            McpRegistrationResult result = McpClientSetup.Apply(settings, state.Client, install: true);
            if (!result.Succeeded)
                failures.Add($"{McpConfigSnippetBuilder.DisplayName(state.Client)}: {result.Detail}");
        }

        ReloadMcpClientStateAsync();

        _mcpAutostartWarning = failures.Count == 0
            ? null
            : "The settings were saved, but these clients still hold the previous folders and capabilities "
              + "because their configuration could not be updated: " + string.Join("; ", failures) + ".";

        if (!settings.McpStartAtLogin || !settings.McpHttpEnabled)
            return;

        (bool ok, string message, bool warn) = ApplyMcpAutostart(enabled: true);
        if (ok && !warn)
            return;

        // Discarding this was how a revoked folder stayed served: the task and the process it started
        // both keep the grant on their command line, so a failure here means the machine no longer
        // matches the settings that were just written. The caller reports it and saves the correction.
        if (!ok && settings.McpAllowedRoots.Count == 0)
            settings.McpStartAtLogin = false;

        _mcpAutostartWarning = _mcpAutostartWarning is null
            ? message
            : _mcpAutostartWarning + " " + message;
    }

    /// <summary>
    /// Outcome of a sign-in-task change.
    /// </summary>
    /// <param name="Applied">
    /// True when the scheduled task now matches the request, so the setting may be persisted. This is
    /// deliberately separate from <paramref name="Warn"/>: a task that registered but whose running server
    /// could not be replaced HAS been applied, yet still needs reporting.
    /// </param>
    /// <param name="Warn">True when the user must be told, even though the request was applied.</param>
    private readonly record struct McpAutostartApplyResult(bool Applied, string Message, bool Warn);

    /// <summary>Registers or removes the sign-in task, reporting whether it actually took effect.</summary>
    private McpAutostartApplyResult ApplyMcpAutostart(bool enabled)
    {
        AppSettings settings = _viewModel.Settings;

        if (!enabled)
        {
            McpLoginHelperState removed = McpLoginHelper.Unregister(McpLoginHelper.RunSchtasks);
            return removed.Registered
                ? new(false, $"Could not remove the scheduled task: {removed.Detail}", Warn: true)
                : new(true, "The server will no longer start at sign-in.", Warn: false);
        }

        // An empty grant is a REVOCATION, not a reason to do nothing. Any task registered from an
        // earlier grant still carries the folders that have since been removed on its command line, and
        // the IgnoreNew policy means the instance it started keeps serving them until the next sign-in.
        if (settings.McpAllowedRoots.Count == 0)
        {
            McpLoginHelperState revoked = McpLoginHelper.Unregister(McpLoginHelper.RunSchtasks);
            return revoked.Registered
                ? new(false, "There are no allowed folders left, and the scheduled task could not be removed, so the "
                           + $"server keeps serving the folders you took away: {revoked.Detail}", Warn: true)
                : new(false, "There are no allowed folders left, so the server was stopped and its sign-in task removed. "
                           + "Add a folder and turn \u201cStart it when I sign in\u201d back on.", Warn: true);
        }

        string serverExe = McpConfigSnippetBuilder.ResolveServerExecutablePath();
        if (!File.Exists(serverExe))
            return new(false, $"The MCP server was not found at {serverExe}.", Warn: true);

        McpLoginHelperState state = McpLoginHelper.Register(
            serverExe,
            McpConfigSnippetBuilder.BuildHttpServerArguments(settings, McpConfigSnippetBuilder.ResolveYaguExecutablePath()),
            McpLoginHelper.RunSchtasks);

        if (!state.Registered)
            return new(false, $"Could not register the scheduled task: {state.Detail}", Warn: true);

        // The running server holds its roots and capabilities in its own command line and the task's
        // IgnoreNew policy blocks a second instance, so rewriting the task alone would leave a revoked
        // folder served until the next sign-in. Replace the instance now.
        McpLoginHelperState restarted = McpLoginHelper.Restart(McpLoginHelper.RunSchtasks);

        string endpoint = $"http://127.0.0.1:{AppSettings.NormalizeMcpHttpPort(settings.McpHttpPort)}";

        // A failed restart still leaves the task correct, so the setting is applied — but it is exactly
        // the case this path exists for (a live server serving folders the user just revoked), and
        // folding it into plain success is why no warning was ever raised for it.
        return restarted.Registered
            ? new(true, $"Registered the scheduled task \u201c{McpLoginHelper.TaskName}\u201d and restarted the server on {endpoint}.", Warn: false)
            : new(true, $"Registered the scheduled task \u201c{McpLoginHelper.TaskName}\u201d, but the running server could not be "
                      + $"restarted, so it keeps its previous folders and capabilities until you sign in again: {restarted.Detail}",
                  Warn: true);
    }

    private static Grid DisabledReasonHost(Control control) => DisabledReasonTooltip.Host(control);

    private static void SetDisabledReason(Grid host, Control control, string? reason) =>
        DisabledReasonTooltip.Set(host, control, reason);

    private static FrameworkElement WithDisabledReason(Control control, string? reason) =>
        DisabledReasonTooltip.Wrap(control, reason);
}
