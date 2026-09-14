namespace Yagu.Services.Mcp;

/// <summary>
/// Installs and removes Yagu's entry in an AI client's MCP configuration.
/// </summary>
/// <remarks>
/// Shared by the Settings tab and the first-run prompt. Registering a client is the step that actually
/// hands out the grant, so the two surfaces must not be able to write subtly different entries — the
/// stdio/HTTP choice and the token-input cleanup in particular.
/// </remarks>
internal static class McpClientSetup
{
    /// <summary>True when the optional server payload was installed alongside Yagu.</summary>
    public static bool IsServerInstalled() =>
        File.Exists(McpConfigSnippetBuilder.ResolveServerExecutablePath());

    /// <summary>True when <paramref name="client"/>'s configuration already carries Yagu's entry.</summary>
    public static bool IsInstalled(McpClientKind client) =>
        IsInstalledIn(McpConfigSnippetBuilder.ResolveConfigPath(client), client);

    /// <summary>As <see cref="IsInstalled(McpClientKind)"/>, against an explicit configuration file.</summary>
    internal static bool IsInstalledIn(string configPath, McpClientKind client) =>
        McpClientRegistrar.IsInstalled(
            configPath,
            McpConfigSnippetBuilder.ServersPropertyName(client),
            McpConfigSnippetBuilder.ServerName);

    /// <summary>True when this client would be given the loopback HTTP entry rather than a stdio one.</summary>
    public static bool UsesHttp(AppSettings settings, McpClientKind client) =>
        settings.McpHttpEnabled && McpConfigSnippetBuilder.SupportsHttpEntry(client);

    /// <summary>Adds, updates, or removes Yagu's entry in <paramref name="client"/>'s configuration.</summary>
    internal static McpRegistrationResult Apply(
        AppSettings settings,
        McpClientKind client,
        bool install,
        string? configPath = null,
        string? serverExePath = null,
        string? yaguExePath = null,
        string? tokenDirectory = null)
    {
        configPath ??= McpConfigSnippetBuilder.ResolveConfigPath(client);
        if (string.IsNullOrWhiteSpace(configPath))
            return new McpRegistrationResult(
                McpRegistrationOutcome.Failed,
                $"No configuration path is known for MCP client '{client}'.");

        return ApplyTo(
            settings, client, install,
            configPath,
            serverExePath ?? McpConfigSnippetBuilder.ResolveServerExecutablePath(),
            yaguExePath ?? McpConfigSnippetBuilder.ResolveYaguExecutablePath(),
            tokenDirectory ?? McpTokenStore.DirectoryPath);
    }

    /// <summary>
    /// As <see cref="Apply"/>, against explicit paths.
    /// </summary>
    /// <remarks>
    /// The seam exists so the grant-writing logic can be exercised against a temporary directory. Every
    /// path this touches is a real per-user client configuration, so a test that went through
    /// <see cref="Apply"/> would rewrite the developer's own VS Code or Claude entry.
    /// </remarks>
    internal static McpRegistrationResult ApplyTo(
        AppSettings settings,
        McpClientKind client,
        bool install,
        string configPath,
        string serverExePath,
        string yaguExePath,
        string tokenDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string container = McpConfigSnippetBuilder.ServersPropertyName(client);

        if (install && UsesHttp(settings, client))
        {
            // Connects to the running loopback server instead of launching one. The token is referenced
            // as ${input:...} so it stays in the client's secret storage, never in this file.
            McpTokenStore.EnsureTokenIn(tokenDirectory);

            return McpClientRegistrar.Install(
                configPath, container, McpConfigSnippetBuilder.ServerName,
                McpConfigSnippetBuilder.BuildHttpServerEntry(settings),
                McpConfigSnippetBuilder.TokenInputId,
                McpConfigSnippetBuilder.BuildTokenInput());
        }

        if (install)
        {
            // A stdio entry: the client launches the server itself and speaks to it over the pipe it
            // owns. Handing it the HTTP command line while still declaring "stdio" produces a server
            // listening on a socket and a client waiting on stdout that never speaks.
            List<string> arguments = McpConfigSnippetBuilder.BuildServerArguments(settings, yaguExePath);

            McpRegistrationResult result = McpClientRegistrar.Install(
                configPath, container, McpConfigSnippetBuilder.ServerName,
                McpConfigSnippetBuilder.BuildServerEntry(client, serverExePath, arguments));

            // Switching back from the HTTP entry leaves its token input behind, still prompting.
            if (result.Succeeded && McpConfigSnippetBuilder.SupportsHttpEntry(client))
                McpClientRegistrar.RemoveInput(configPath, McpConfigSnippetBuilder.TokenInputId);

            return result;
        }

        return McpClientRegistrar.Remove(
            configPath, container, McpConfigSnippetBuilder.ServerName, McpConfigSnippetBuilder.TokenInputId);
    }

    /// <summary>The message to show for <paramref name="result"/>.</summary>
    public static string Describe(AppSettings settings, McpClientKind client, McpRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(result);

        string display = McpConfigSnippetBuilder.DisplayName(client);
        string httpNote = UsesHttp(settings, client)
            ? $" It connects to {McpConfigSnippetBuilder.HttpUrl(settings)} and will ask once for the token "
              + $"stored at {McpTokenStore.TokenPath}."
            : string.Empty;

        return result.Outcome switch
        {
            McpRegistrationOutcome.Installed => $"Added Yagu to {display}. Restart it to pick up the server.{httpNote}",
            McpRegistrationOutcome.Updated => $"Updated Yagu's entry in {display}. Restart it to pick up the change.{httpNote}",
            McpRegistrationOutcome.Removed => $"Removed Yagu from {display}.",
            McpRegistrationOutcome.NotPresent => $"{display} had no Yagu entry.",
            McpRegistrationOutcome.Malformed =>
                $"{display}'s configuration could not be parsed, so it was left untouched: {result.Detail}",
            _ => $"Could not update {display}: {result.Detail}",
        };
    }

    /// <summary>
    /// Why a client's Install button is unavailable, or null when it can be used.
    /// </summary>
    /// <remarks>
    /// A server with no allowed folder authorizes nothing and refuses to start, and registering against
    /// a missing executable reports success and then fails silently inside the client. Installing for a
    /// client that is not on this machine would create that application's configuration directory on its
    /// behalf, so it is blocked rather than silently producing state for software the user does not have.
    /// </remarks>
    public static string? DescribeInstallBlock(bool serverInstalled, int allowedRootCount, bool configLocationExists = true) =>
        !serverInstalled
            ? "The MCP server is not part of this installation, so there is nothing for a client to run. "
              + "Re-run the Yagu installer and tick \u201cInstall the MCP server\u201d."
            : allowedRootCount == 0
                ? "Choose at least one folder first \u2014 a server with no folder authorizes nothing and refuses to run."
                : !configLocationExists
                    ? "This client does not appear to be installed \u2014 its configuration folder does not exist. "
                      + "Install the client first, or copy the snippet below by hand."
                    : null;

    /// <summary>Why a client's Remove button is unavailable, or null when it can be used.</summary>
    public static string? DescribeRemoveBlock(bool clientInstalled) =>
        clientInstalled ? null : "Yagu has no entry in this client's configuration, so there is nothing to remove.";
}
