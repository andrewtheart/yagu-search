using Yagu.Services;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu;

/// <summary>
/// The <c>--mcp-*</c> command family: the command-line counterpart of the
/// <b>Settings ▸ MCP Server</b> tab, so wiring up an AI client is scriptable and does not require the GUI.
/// </summary>
/// <remarks>
/// These all read the <b>global</b> per-user profile settings rather than resolving a <c>.yagu.json</c>
/// from the working directory. An MCP grant belongs to the user, and silently picking up whichever
/// workspace the shell happened to be in is exactly the hazard <c>--settings-source</c> exists to prevent.
/// </remarks>
internal static partial class CliRunner
{
    private const string McpConfigFlag = "--mcp-config";
    private const string McpInstallFlag = "--mcp-install";
    private const string McpRemoveFlag = "--mcp-remove";
    private const string McpStatusFlag = "--mcp-status";
    private const string McpRegenerateTokenFlag = "--mcp-regenerate-token";
    private const string McpAutostartFlag = "--mcp-autostart";
    internal const string McpClientFlag = "--mcp-client";

    /// <summary>Every flag that makes this a console command rather than a GUI launch.</summary>
    public static readonly string[] McpCommandFlags =
    [
        McpConfigFlag, McpInstallFlag, McpRemoveFlag, McpStatusFlag, McpRegenerateTokenFlag, McpAutostartFlag,
    ];

    /// <summary>Flags in the <c>--mcp-*</c> family that consume the following token as their value.</summary>
    private static readonly string[] McpValueFlags =
    [
        McpInstallFlag, McpRemoveFlag, McpAutostartFlag, McpClientFlag,
    ];

    /// <summary>
    /// The <c>--mcp-*</c> command in <paramref name="args"/>, or <see langword="null"/> if there is none.
    /// </summary>
    /// <remarks>
    /// The scan stops at the first token that is not part of the <c>--mcp-*</c> family, so a flag name
    /// can only be a command when it arrives in command position. Matching anywhere in the array would
    /// also match a flag name that arrived as somebody else's <i>value</i>: the MCP server passes an
    /// agent-supplied query straight through as <c>--pattern &lt;query&gt;</c>, so searching for the
    /// literal text "--mcp-regenerate-token" would rotate the loopback token every configured client
    /// holds instead of running the search.
    /// </remarks>
    private static string? FindMcpCommand(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (McpCommandFlags.Contains(token, StringComparer.OrdinalIgnoreCase))
                return token;
            if (!McpValueFlags.Contains(token, StringComparer.OrdinalIgnoreCase))
                return null;
            i++; // Skip the value this flag consumes so it cannot be read as a command.
        }
        return null;
    }

    public static bool IsMcpCommand(string[] args) => FindMcpCommand(args) is not null;

    public static int RunMcpCommand(string[] args)
    {
        string? command = FindMcpCommand(args);
        if (string.Equals(command, McpConfigFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpConfig(args);
        if (string.Equals(command, McpStatusFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpStatus();
        if (string.Equals(command, McpRegenerateTokenFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpRegenerateToken();
        if (string.Equals(command, McpAutostartFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpAutostart(args);
        if (string.Equals(command, McpInstallFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpRegistration(args, McpInstallFlag, install: true);
        if (string.Equals(command, McpRemoveFlag, StringComparison.OrdinalIgnoreCase)) return RunMcpRegistration(args, McpRemoveFlag, install: false);
        return CliExitCode.InvalidInput;
    }

    // ── Install / remove ──

    private static int RunMcpRegistration(string[] args, string flag, bool install)
    {
        string? requested = ValueAfter(args, flag);
        if (string.IsNullOrWhiteSpace(requested))
        {
            Console.Error.WriteLine($"error: {flag} needs a client: vscode, vs, copilot-cli, claude, or all.");
            return CliExitCode.InvalidInput;
        }

        List<McpClientKind> clients;
        if (string.Equals(requested.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            clients = [.. Enum.GetValues<McpClientKind>()];
        }
        else if (McpConfigSnippetBuilder.TryParseClient(requested, out McpClientKind single))
        {
            clients = [single];
        }
        else
        {
            Console.Error.WriteLine($"error: unknown client '{requested}'. Expected vscode, vs, copilot-cli, claude, or all.");
            return CliExitCode.InvalidInput;
        }

        if (!TryLoadMcpSettings(out AppSettings settings, out int failure))
            return failure;

        if (install && settings.McpAllowedRoots.Count == 0)
        {
            Console.Error.WriteLine(
                "error: no allowed folders are configured, so an installed configuration could not work. "
                + "Add at least one folder in Settings > MCP Server first.");
            return CliExitCode.InvalidInput;
        }

        string serverExe = McpConfigSnippetBuilder.ResolveServerExecutablePath();

        // The MCP server is an optional install. Writing a client entry for a path with no executable
        // behind it reports success and then fails silently inside the client.
        if (install && !File.Exists(serverExe))
        {
            Console.Error.WriteLine(
                $"error: the MCP server was not found at {serverExe}, so a client could not start it. "
                + "Re-run the Yagu installer and tick \"Install the MCP server\".");
            return CliExitCode.InvalidInput;
        }

        List<string> serverArgs = McpConfigSnippetBuilder.BuildServerArguments(
            settings, McpConfigSnippetBuilder.ResolveYaguExecutablePath());

        int worst = CliExitCode.Success;

        foreach (McpClientKind client in clients)
        {
            string path = McpConfigSnippetBuilder.ResolveConfigPath(client);
            string container = McpConfigSnippetBuilder.ServersPropertyName(client);
            string display = McpConfigSnippetBuilder.DisplayName(client);
            bool http = settings.McpHttpEnabled && McpConfigSnippetBuilder.SupportsHttpEntry(client);

            McpRegistrationResult result;
            if (install && http)
            {
                result = McpClientRegistrar.Install(path, container, McpConfigSnippetBuilder.ServerName,
                    McpConfigSnippetBuilder.BuildHttpServerEntry(settings),
                    McpConfigSnippetBuilder.TokenInputId,
                    McpConfigSnippetBuilder.BuildTokenInput());
            }
            else if (install)
            {
                result = McpClientRegistrar.Install(path, container, McpConfigSnippetBuilder.ServerName,
                    McpConfigSnippetBuilder.BuildServerEntry(client, serverExe, serverArgs));
            }
            else
            {
                result = McpClientRegistrar.Remove(path, container, McpConfigSnippetBuilder.ServerName,
                    McpConfigSnippetBuilder.TokenInputId);
            }

            string transport = install ? (http ? "  [loopback HTTP]" : "  [stdio]") : string.Empty;
            Console.WriteLine($"{display,-16} {DescribeOutcome(result)}{transport}  ({path})");

            if (!result.Succeeded)
                worst = CliExitCode.PartialFailure;
        }

        if (install)
        {
            Console.WriteLine("Restart the client so it picks up the new server.");

            if (settings.McpHttpEnabled)
            {
                Console.WriteLine(
                    $"HTTP clients prompt once for the token stored at {McpTokenStore.TokenPath}; "
                    + "it is never written into their configuration.");
            }
        }

        return worst;
    }

    private static string DescribeOutcome(McpRegistrationResult result) => result.Outcome switch
    {
        McpRegistrationOutcome.Installed => "installed",
        McpRegistrationOutcome.Updated => "updated",
        McpRegistrationOutcome.Removed => "removed",
        McpRegistrationOutcome.NotPresent => "not present",
        McpRegistrationOutcome.Malformed => $"REFUSED - the file could not be parsed, so it was left untouched: {result.Detail}",
        _ => $"FAILED - {result.Detail}",
    };

    // ── Status ──

    private static int RunMcpStatus()
    {
        if (!TryLoadMcpSettings(out AppSettings settings, out int failure))
            return failure;

        string serverExe = McpConfigSnippetBuilder.ResolveServerExecutablePath();

        Console.WriteLine($"Server            {(File.Exists(serverExe) ? serverExe : $"NOT INSTALLED (expected {serverExe})")}");
        Console.WriteLine($"Allowed folders   {settings.McpAllowedRoots.Count}");
        foreach (string root in settings.McpAllowedRoots)
            Console.WriteLine($"                  {root}");
        Console.WriteLine($"Capabilities      {(settings.McpCapabilities.Count == 0 ? "(none)" : string.Join(", ", settings.McpCapabilities))}");
        Console.WriteLine($"Replace writes    {(settings.McpAllowReplaceWrites ? "ENABLED" : "preview only")}");
        Console.WriteLine($"HTTP transport    {(settings.McpHttpEnabled ? $"enabled on loopback port {AppSettings.NormalizeMcpHttpPort(settings.McpHttpPort)}" : "disabled (stdio only)")}");
        Console.WriteLine($"Start at sign-in  {(McpLoginHelper.Query().Registered ? $"registered as scheduled task \"{McpLoginHelper.TaskName}\"" : "not registered")}");
        // The token itself is never printed: it would land in shell history and terminal scrollback.
        Console.WriteLine($"HTTP token        {(McpTokenStore.TryReadToken() is null ? "not created yet" : $"stored at {McpTokenStore.TokenPath}")}");

        Console.WriteLine();
        Console.WriteLine("Clients:");
        foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
        {
            string path = McpConfigSnippetBuilder.ResolveConfigPath(client);
            bool installed = McpClientRegistrar.IsInstalled(path, McpConfigSnippetBuilder.ServersPropertyName(client), McpConfigSnippetBuilder.ServerName);
            Console.WriteLine($"  {McpConfigSnippetBuilder.DisplayName(client),-16} {(installed ? "installed" : "not installed"),-14} {path}");
        }

        return CliExitCode.Success;
    }

    // ── Token ──

    private static int RunMcpRegenerateToken()
    {
        string? token = McpTokenStore.Regenerate(McpTokenStore.DirectoryPath);
        if (token is null)
        {
            Console.Error.WriteLine($"error: could not write the token file at {McpTokenStore.TokenPath}.");
            return CliExitCode.InternalFailure;
        }

        // Deliberately not printed. Every client reads it from the same file the server does.
        Console.WriteLine($"A new HTTP token was generated and stored at {McpTokenStore.TokenPath}.");
        Console.WriteLine("Restart the MCP server so it loads the new token; any client holding the old one will be refused.");
        return CliExitCode.Success;
    }

    // ── Autostart ──

    private static int RunMcpAutostart(string[] args)
    {
        string? value = ValueAfter(args, McpAutostartFlag);
        bool on;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "enable" or "enabled":
                on = true;
                break;
            case "off" or "false" or "disable" or "disabled":
                on = false;
                break;
            default:
                Console.Error.WriteLine($"error: {McpAutostartFlag} needs 'on' or 'off'.");
                return CliExitCode.InvalidInput;
        }

        if (!TryLoadMcpSettings(out AppSettings settings, out int failure))
            return failure;

        if (!on)
        {
            McpLoginHelperState off = McpLoginHelper.Unregister(McpLoginHelper.RunSchtasks);
            if (off.Detail is not null && off.Registered)
            {
                Console.Error.WriteLine($"error: could not remove the scheduled task: {off.Detail}");
                return CliExitCode.InternalFailure;
            }

            if (!TryPersistAutostartState(settings, enabled: false))
            {
                Console.Error.WriteLine(
                    "error: the scheduled task was removed but the setting could not be saved, so Settings still "
                    + "shows start-at-sign-in as on and saving there would re-register it.");
                return CliExitCode.InternalFailure;
            }

            Console.WriteLine("The MCP server will no longer start at sign-in.");
            return CliExitCode.Success;
        }

        if (settings.McpAllowedRoots.Count == 0)
        {
            Console.Error.WriteLine(
                "error: no allowed folders are configured, so the server would refuse to run. "
                + "Add at least one folder in Settings > MCP Server first.");
            return CliExitCode.InvalidInput;
        }

        string serverExe = McpConfigSnippetBuilder.ResolveServerExecutablePath();
        if (!File.Exists(serverExe))
        {
            Console.Error.WriteLine($"error: the MCP server was not found at {serverExe}.");
            return CliExitCode.InvalidInput;
        }

        McpLoginHelperState state = McpLoginHelper.Register(
            serverExe,
            McpConfigSnippetBuilder.BuildHttpServerArguments(settings, McpConfigSnippetBuilder.ResolveYaguExecutablePath()),
            McpLoginHelper.RunSchtasks);

        if (!state.Registered)
        {
            Console.Error.WriteLine($"error: could not register the scheduled task: {state.Detail}");
            return CliExitCode.InternalFailure;
        }

        if (!TryPersistAutostartState(settings, enabled: true))
        {
            Console.Error.WriteLine(
                "error: the scheduled task was registered but the setting could not be saved, so Settings still "
                + "shows start-at-sign-in as off.");
            return CliExitCode.InternalFailure;
        }

        Console.WriteLine($"Registered scheduled task \"{McpLoginHelper.TaskName}\".");
        Console.WriteLine($"The MCP server will listen on http://127.0.0.1:{AppSettings.NormalizeMcpHttpPort(settings.McpHttpPort)} from the next sign-in.");
        return CliExitCode.Success;
    }

    /// <summary>
    /// Saves the sign-in state after the scheduler change succeeded.
    /// </summary>
    /// <remarks>
    /// The task and the setting must agree. Changing only the task lets the Settings tab keep showing the
    /// opposite state and silently re-apply it on the next save; enabling autostart also implies the HTTP
    /// transport, because that is the command line the task is registered with.
    /// <para>
    /// Uses <see cref="SettingsService.UpdateAsync"/> rather than <c>Save</c>: <c>Save</c> logs and
    /// swallows every write failure, so this reported success for a setting that never reached disk and
    /// the error text above — written precisely for that case — could never appear.
    /// </para>
    /// </remarks>
    private static bool TryPersistAutostartState(AppSettings settings, bool enabled)
    {
        try
        {
            bool saved = new SettingsService(SettingsService.ResolveInstanceSettingsPath())
                .UpdateAsync(stored =>
                {
                    stored.McpStartAtLogin = enabled;
                    if (enabled)
                        stored.McpHttpEnabled = true;
                })
                .GetAwaiter()
                .GetResult();

            if (!saved)
                return false;

            // Only after the write committed, so an in-memory snapshot can never claim a state the
            // file does not hold.
            settings.McpStartAtLogin = enabled;
            if (enabled)
                settings.McpHttpEnabled = true;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ── Shared ──

    private static bool TryLoadMcpSettings(out AppSettings settings, out int failureExitCode)
    {
        failureExitCode = CliExitCode.Success;
        try
        {
            settings = new SettingsService(SettingsService.ResolveInstanceSettingsPath()).Load();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"error: could not read settings: {ex.Message}");
            settings = new AppSettings();
            failureExitCode = CliExitCode.InternalFailure;
            return false;
        }
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                continue;
            return i + 1 < args.Length ? args[i + 1] : string.Empty;
        }
        return null;
    }
}
