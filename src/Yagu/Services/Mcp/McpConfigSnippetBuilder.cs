using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Yagu.Services.Mcp;

/// <summary>The MCP clients Yagu can produce a configuration snippet for.</summary>
internal enum McpClientKind
{
    VsCode,
    VisualStudio,
    CopilotCli,
    ClaudeDesktop,
}

/// <summary>
/// Builds the JSON an MCP client needs in order to launch Yagu's stdio server with the roots,
/// capabilities, and concurrency the user configured.
/// </summary>
/// <remarks>
/// The server takes its whole configuration from its own command line, so this snippet <b>is</b> the
/// grant: a root or capability missing here is a power the client does not get. Every value therefore
/// goes through the same normalization the server applies, so what the user copies cannot differ from
/// what a launched server would honor.
/// </remarks>
internal static class McpConfigSnippetBuilder
{
    /// <summary>The server's key in the client's configuration file.</summary>
    public const string ServerName = "yagu";

    /// <summary>Where the staged server lives relative to <c>Yagu.exe</c>.</summary>
    public const string ServerDirectoryName = "mcp-server";

    public const string ServerExecutableName = "Yagu.McpServer.exe";

    /// <summary>
    /// The command line the client should launch the server with. Capabilities and roots are emitted in
    /// a stable normalized order so re-copying the snippet after an unrelated settings change produces
    /// an identical file.
    /// </summary>
    public static List<string> BuildServerArguments(AppSettings settings, string yaguExePath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var args = new List<string> { "--yagu-exe", yaguExePath };

        foreach (string root in AppSettings.NormalizeMcpAllowedRoots(settings.McpAllowedRoots))
        {
            args.Add("--allowed-root");
            args.Add(root);
        }

        foreach (string capability in AppSettings.NormalizeMcpCapabilities(settings.McpCapabilities))
        {
            args.Add("--capability");
            args.Add(capability);
        }

        args.Add("--max-concurrent");
        args.Add(AppSettings.NormalizeMcpMaxConcurrentCalls(settings.McpMaxConcurrentCalls)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Exports and saved sessions collect every match in memory before writing, so the server needs
        // its own ceiling rather than trusting the count a client asks for.
        args.Add("--max-export-results");
        args.Add(AppSettings.NormalizeMcpMaxExportResults(settings.McpMaxExportResults)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Job retention and timeouts. A 4-hour maintenance ceiling suits a spinning disk and is absurd
        // on an NVMe dev box, so these travel with the grant rather than living as server constants.
        AddNumber(args, "--max-retained-jobs", AppSettings.NormalizeMcpMaxRetainedJobs(settings.McpMaxRetainedJobs));
        AddNumber(args, "--max-running-jobs", AppSettings.NormalizeMcpMaxRunningJobs(settings.McpMaxRunningJobs));
        AddNumber(args, "--max-job-result-chars", AppSettings.NormalizeMcpMaxJobResultChars(settings.McpMaxJobResultChars));
        AddNumber(args, "--call-timeout-seconds", AppSettings.NormalizeMcpCallTimeoutSeconds(settings.McpCallTimeoutSeconds));
        AddNumber(args, "--maintenance-timeout-minutes", AppSettings.NormalizeMcpMaintenanceTimeoutMinutes(settings.McpMaintenanceTimeoutMinutes));
        AddNumber(args, "--model-timeout-minutes", AppSettings.NormalizeMcpModelTimeoutMinutes(settings.McpModelTimeoutMinutes));

        return args;
    }

    private static void AddNumber(List<string> args, string flag, int value)
    {
        args.Add(flag);
        args.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The command line for the loopback HTTP server. Note what is <b>not</b> here: the bearer token.
    /// It is read from the per-user token file, because a command line is readable by every process.
    /// </summary>
    public static List<string> BuildHttpServerArguments(AppSettings settings, string yaguExePath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<string> args = BuildServerArguments(settings, yaguExePath);
        args.Add("--transport");
        args.Add("http");
        args.Add("--port");
        args.Add(AppSettings.NormalizeMcpHttpPort(settings.McpHttpPort)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        return args;
    }

    /// <summary>Renders the snippet for one client. The result is a fragment to merge, not a whole file.</summary>
    public static string BuildClientConfig(McpClientKind client, string serverExePath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions()))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(ServersPropertyName(client));
            writer.WritePropertyName(ServerName);
            WriteServerEntry(writer, client, serverExePath, arguments);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Just the entry's value object, for merging into an existing configuration file. The registrar
    /// splices this in as text, so it must stand alone.
    /// </summary>
    public static string BuildServerEntry(McpClientKind client, string serverExePath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions()))
            WriteServerEntry(writer, client, serverExePath, arguments);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ── Loopback HTTP entries ──

    /// <summary>The id the bearer token is declared under, referenced by the entry's header.</summary>
    public const string TokenInputId = "yagu-mcp-token";

    /// <summary>The property holding a client's input-variable definitions.</summary>
    public const string InputsPropertyName = "inputs";

    /// <summary>
    /// True when this client can be wired to the loopback HTTP endpoint instead of launching the server.
    /// </summary>
    /// <remarks>
    /// VS Code and Visual Studio only, because only they expand <c>${input:}</c> — which is what keeps the
    /// bearer token in the editor's secret storage instead of the configuration file. Copilot CLI's file
    /// is read natively by the Agent Host, which does not expand it and would send the literal
    /// <c>${input:…}</c> text as the token; Claude Desktop's file has no HTTP entry shape at all. Both
    /// therefore stay on stdio, which needs no token in the first place.
    /// </remarks>
    public static bool SupportsHttpEntry(McpClientKind client) =>
        client is McpClientKind.VsCode or McpClientKind.VisualStudio;

    /// <summary>The loopback URL a client connects to. Literal IPv4, so it cannot depend on name resolution.</summary>
    public static string HttpUrl(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"http://127.0.0.1:{AppSettings.NormalizeMcpHttpPort(settings.McpHttpPort).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The entry for a client that connects to the running loopback server rather than starting one.
    /// </summary>
    /// <remarks>
    /// The token is referenced, never embedded. A literal token here would be written into a file that
    /// Settings Sync replicates to every machine you sign in on.
    /// </remarks>
    public static string BuildHttpServerEntry(AppSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions()))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "http");
            writer.WriteString("url", HttpUrl(settings));
            writer.WriteStartObject("headers");
            writer.WriteString("Authorization", $"Bearer ${{input:{TokenInputId}}}");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The matching <c>inputs</c> definition, which makes the client prompt for the token once.</summary>
    public static string BuildTokenInput()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions()))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "promptString");
            writer.WriteString("id", TokenInputId);
            writer.WriteString("description", $"Yagu MCP loopback token (copy it from {McpTokenStore.TokenPath})");
            writer.WriteBoolean("password", true);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The whole HTTP snippet, including the token input the entry references.</summary>
    public static string BuildHttpClientConfig(McpClientKind client, AppSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions()))
        {
            writer.WriteStartObject();

            writer.WriteStartObject(ServersPropertyName(client));
            writer.WritePropertyName(ServerName);
            writer.WriteRawValue(BuildHttpServerEntry(settings));
            writer.WriteEndObject();

            writer.WriteStartArray(InputsPropertyName);
            writer.WriteRawValue(BuildTokenInput());
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonWriterOptions WriterOptions() => new()
    {
        Indented = true,
        // Paths are shown to a human and pasted into a config file, so keep non-ASCII readable
        // rather than \uXXXX-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void WriteServerEntry(
        Utf8JsonWriter writer, McpClientKind client, string serverExePath, IReadOnlyList<string> arguments)
    {
        writer.WriteStartObject();

        switch (client)
        {
            case McpClientKind.VsCode:
            case McpClientKind.VisualStudio:
                writer.WriteString("type", "stdio");
                break;
            case McpClientKind.CopilotCli:
                writer.WriteString("type", "local");
                break;
        }

        writer.WriteString("command", serverExePath);

        writer.WriteStartArray("args");
        foreach (string argument in arguments)
            writer.WriteStringValue(argument);
        writer.WriteEndArray();

        if (client == McpClientKind.CopilotCli)
        {
            writer.WriteStartArray("tools");
            writer.WriteStringValue("*");
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>VS Code and Visual Studio use <c>servers</c>; Copilot CLI and Claude use <c>mcpServers</c>.</summary>
    public static string ServersPropertyName(McpClientKind client) => client switch
    {
        McpClientKind.VsCode or McpClientKind.VisualStudio => "servers",
        _ => "mcpServers",
    };

    public static string DisplayName(McpClientKind client) => client switch
    {
        McpClientKind.VsCode => "VS Code",
        McpClientKind.VisualStudio => "Visual Studio",
        McpClientKind.CopilotCli => "Copilot CLI",
        McpClientKind.ClaudeDesktop => "Claude Desktop",
        _ => client.ToString(),
    };

    /// <summary>The name each client is selected by on the command line, and its accepted aliases.</summary>
    public static bool TryParseClient(string? value, out McpClientKind client)
    {
        client = McpClientKind.VsCode;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "vscode" or "code" or "vs-code":
                client = McpClientKind.VsCode;
                return true;
            case "vs" or "visualstudio" or "visual-studio":
                client = McpClientKind.VisualStudio;
                return true;
            case "copilot-cli" or "copilot" or "copilotcli":
                client = McpClientKind.CopilotCli;
                return true;
            case "claude" or "claude-desktop" or "claudedesktop":
                client = McpClientKind.ClaudeDesktop;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The canonical command-line name for each client, for help text and error messages.</summary>
    public static string CommandLineName(McpClientKind client) => client switch
    {
        McpClientKind.VsCode => "vscode",
        McpClientKind.VisualStudio => "vs",
        McpClientKind.CopilotCli => "copilot-cli",
        McpClientKind.ClaudeDesktop => "claude",
        _ => client.ToString().ToLowerInvariant(),
    };

    /// <summary>Where the snippet has to be merged. Never overwrite the file — it may hold other servers.</summary>
    public static string ConfigLocation(McpClientKind client) => client switch
    {
        McpClientKind.VsCode => @"%APPDATA%\Code\User\mcp.json",
        McpClientKind.VisualStudio => @"%USERPROFILE%\.mcp.json",
        McpClientKind.CopilotCli => @"%USERPROFILE%\.copilot\mcp-config.json",
        McpClientKind.ClaudeDesktop => @"%APPDATA%\Claude\claude_desktop_config.json",
        _ => string.Empty,
    };

    /// <summary>
    /// The real path <see cref="ConfigLocation"/> refers to for this user.
    /// </summary>
    /// <remarks>
    /// VS Code keeps Stable, Insiders, and each non-default profile in separate directories. Preferring
    /// Stable whenever its folder merely exists writes a configuration the running edition never reads,
    /// so an existing <c>mcp.json</c> wins, most recently written first.
    /// </remarks>
    public static string ResolveConfigPath(McpClientKind client)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return client switch
        {
            McpClientKind.VsCode => ResolveVsCodePath(appData),
            McpClientKind.VisualStudio => Path.Combine(userProfile, ".mcp.json"),
            McpClientKind.CopilotCli => Path.Combine(userProfile, ".copilot", "mcp-config.json"),
            McpClientKind.ClaudeDesktop => Path.Combine(appData, "Claude", "claude_desktop_config.json"),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Every VS Code configuration Yagu could reasonably target, most recently written first.
    /// </summary>
    /// <remarks>
    /// Surfaced so the UI can say when more than one exists. Yagu writes ONE file, and silently picking
    /// among several is how "installed" ends up pointing at an edition or profile the user does not run.
    /// </remarks>
    public static IReadOnlyList<string> VsCodeConfigCandidates() =>
        VsCodeConfigCandidatesIn(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>As <see cref="VsCodeConfigCandidates"/>, against an explicit roaming-data root.</summary>
    internal static IReadOnlyList<string> VsCodeConfigCandidatesIn(string appData) =>
        VsCodeConfigCandidatesIn(appData, Directory.EnumerateDirectories);

    internal static IReadOnlyList<string> VsCodeConfigCandidatesIn(
        string appData,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        var found = new List<(string Path, DateTime Written)>();

        foreach (string edition in new[] { "Code", "Code - Insiders" })
        {
            string user = Path.Combine(appData, edition, "User");
            AddIfPresent(found, Path.Combine(user, "mcp.json"));

            string profiles = Path.Combine(user, "profiles");
            try
            {
                if (Directory.Exists(profiles))
                {
                    foreach (string profile in enumerateDirectories(profiles))
                        AddIfPresent(found, Path.Combine(profile, "mcp.json"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. found.OrderByDescending(f => f.Written).Select(f => f.Path)];

        static void AddIfPresent(List<(string, DateTime)> into, string candidate)
        {
            try
            {
                var info = new FileInfo(candidate);
                if (info.Exists)
                    into.Add((candidate, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    internal static string ResolveVsCodePath(string appData)
    {
        if (VsCodeConfigCandidatesIn(appData) is [string newest, ..])
            return newest;

        // Nothing configured yet: fall back to whichever edition is actually installed.
        string stable = Path.Combine(appData, "Code", "User", "mcp.json");
        string insiders = Path.Combine(appData, "Code - Insiders", "User", "mcp.json");

        if (Directory.Exists(Path.GetDirectoryName(stable)!))
            return stable;

        return Directory.Exists(Path.GetDirectoryName(insiders)!) ? insiders : stable;
    }

    /// <summary>The running <c>Yagu.exe</c>, which the server shells out to for every tool call.</summary>
    public static string ResolveYaguExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(processPath)
            ? Path.Combine(AppContext.BaseDirectory, "Yagu.exe")
            : processPath;
    }

    /// <summary>The staged server beside the app. May not exist if the app was built without it.</summary>
    public static string ResolveServerExecutablePath() =>
        Path.Combine(AppContext.BaseDirectory, ServerDirectoryName, ServerExecutableName);
}
