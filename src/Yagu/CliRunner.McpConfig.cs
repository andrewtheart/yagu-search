using Yagu.Services;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu;

/// <summary>
/// <c>--mcp-config</c> — prints the JSON an MCP client needs in order to launch Yagu's server, so the
/// **Settings ▸ MCP Server ▸ Copy configuration** button has a command-line counterpart.
/// </summary>
/// <remarks>
/// This deliberately reads the <b>global</b> per-user profile settings rather than resolving a
/// <c>.yagu.json</c> from the working directory. An MCP grant belongs to the user, and silently picking
/// up whichever workspace the shell happened to be in is exactly the hazard <c>--settings-source</c>
/// exists to prevent.
/// </remarks>
internal static partial class CliRunner
{
    public static int RunMcpConfig(string[] args)
    {
        var client = McpClientKind.VsCode;
        string? requested = ValueAfter(args, McpClientFlag);
        if (requested is not null && !McpConfigSnippetBuilder.TryParseClient(requested, out client))
        {
            Console.Error.WriteLine(
                $"error: unknown --mcp-client '{requested}'. Expected vscode, vs, copilot-cli or claude.");
            return CliExitCode.InvalidInput;
        }

        AppSettings settings;
        try
        {
            settings = new SettingsService(SettingsService.ResolveInstanceSettingsPath()).Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"error: could not read settings: {ex.Message}");
            return CliExitCode.InternalFailure;
        }

        string serverExe = McpConfigSnippetBuilder.ResolveServerExecutablePath();
        bool http = settings.McpHttpEnabled && McpConfigSnippetBuilder.SupportsHttpEntry(client);

        // Checked before anything reaches stdout: `--mcp-config > mcp.json` must not leave a complete,
        // non-working file behind on the exit code that says the configuration cannot work.
        if (settings.McpAllowedRoots.Count == 0)
        {
            Console.Error.WriteLine(
                "error: no allowed folders are configured, so this configuration cannot work — a server "
                + "started without one authorizes nothing and refuses to run. Add at least one folder in "
                + "Settings > MCP Server, then run this again.");
            return CliExitCode.InvalidInput;
        }

        if (http)
        {
            Console.Out.WriteLine(McpConfigSnippetBuilder.BuildHttpClientConfig(client, settings));
        }
        else
        {
            List<string> serverArgs = McpConfigSnippetBuilder.BuildServerArguments(
                settings, McpConfigSnippetBuilder.ResolveYaguExecutablePath());
            Console.Out.WriteLine(McpConfigSnippetBuilder.BuildClientConfig(client, serverExe, serverArgs));
        }

        Console.Out.Flush();

        // Everything below is guidance, not output: stdout stays pure so `--mcp-config > mcp.json` works.
        Console.Error.WriteLine(
            $"Merge the above into {McpConfigSnippetBuilder.ConfigLocation(client)} for {McpConfigSnippetBuilder.DisplayName(client)}.");
        Console.Error.WriteLine("Merge it — do not replace the file, which may already list other servers.");

        if (http)
        {
            Console.Error.WriteLine(
                $"This connects to {McpConfigSnippetBuilder.HttpUrl(settings)}. The client prompts once for the "
                + $"token stored at {McpTokenStore.TokenPath} and keeps it in its own secret storage.");
        }
        else if (!File.Exists(serverExe))
        {
            Console.Error.WriteLine($"warning: the MCP server was not found at {serverExe}, so no client can start it.");
        }

        return CliExitCode.Success;
    }
}
