using System.Text.Json;
using Yagu.Services;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// The shared client-registration rules used by both the Settings tab and the first-run prompt.
/// </summary>
/// <remarks>
/// <see cref="McpClientSetup.Apply"/> defaults to the real per-user client configuration (VS Code's
/// <c>mcp.json</c> and friends), so tests pass its optional path seams and write only to a temporary
/// directory.
/// </remarks>
public class McpClientSetupTests
{
    [Fact]
    public void InstallIsBlocked_WhenTheServerPayloadIsMissing()
    {
        string? reason = McpClientSetup.DescribeInstallBlock(serverInstalled: false, allowedRootCount: 3);

        // Registering against a path with no executable reports success, then fails inside the client.
        Assert.NotNull(reason);
        Assert.Contains("installer", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingServer_IsReportedAheadOfMissingFolders()
    {
        // Both are wrong, but re-running the installer is the step that unblocks the other.
        string? reason = McpClientSetup.DescribeInstallBlock(serverInstalled: false, allowedRootCount: 0);

        Assert.NotNull(reason);
        Assert.Contains("installer", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallIsBlocked_WhenNoFolderHasBeenGranted()
    {
        string? reason = McpClientSetup.DescribeInstallBlock(serverInstalled: true, allowedRootCount: 0);

        Assert.NotNull(reason);
        Assert.Contains("folder", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallIsAllowed_OnceAServerAndAFolderExist()
    {
        Assert.Null(McpClientSetup.DescribeInstallBlock(serverInstalled: true, allowedRootCount: 1));
    }

    [Fact]
    public void InstallIsBlocked_WhenTheClientConfigurationLocationDoesNotExist()
    {
        string? reason = McpClientSetup.DescribeInstallBlock(
            serverInstalled: true,
            allowedRootCount: 1,
            configLocationExists: false);

        Assert.NotNull(reason);
        Assert.Contains("does not appear to be installed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveIsBlocked_WhenTheClientHasNoYaguEntry()
    {
        string? reason = McpClientSetup.DescribeRemoveBlock(clientInstalled: false);

        Assert.NotNull(reason);
        Assert.Contains("nothing to remove", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveIsAllowed_WhenTheClientCarriesAnEntry()
    {
        Assert.Null(McpClientSetup.DescribeRemoveBlock(clientInstalled: true));
    }

    [Fact]
    public void HttpEntry_IsUsedOnlyByClientsThatSupportItAndOnlyWhenEnabled()
    {
        // The enum is internal, so these cannot be [Theory] parameters.
        foreach (McpClientKind client in new[] { McpClientKind.VsCode, McpClientKind.VisualStudio })
        {
            Assert.False(McpClientSetup.UsesHttp(new AppSettings { McpHttpEnabled = false }, client));
            Assert.True(McpClientSetup.UsesHttp(new AppSettings { McpHttpEnabled = true }, client));
        }
    }

    [Fact]
    public void ClientsWithoutHttpSupport_AlwaysGetStdio()
    {
        // A stdio client handed the HTTP command line waits on a pipe the server never writes to.
        foreach (McpClientKind client in new[] { McpClientKind.CopilotCli, McpClientKind.ClaudeDesktop })
            Assert.False(McpClientSetup.UsesHttp(new AppSettings { McpHttpEnabled = true }, client));
    }

    [Fact]
    public void Describe_NamesTheClientAndAsksForARestart()
    {
        string message = McpClientSetup.Describe(
            new AppSettings(),
            McpClientKind.VsCode,
            new McpRegistrationResult(McpRegistrationOutcome.Installed));

        Assert.Contains(McpConfigSnippetBuilder.DisplayName(McpClientKind.VsCode), message, StringComparison.Ordinal);
        Assert.Contains("Restart", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_AMalformedConfig_SaysItWasLeftUntouched()
    {
        string message = McpClientSetup.Describe(
            new AppSettings(),
            McpClientKind.ClaudeDesktop,
            new McpRegistrationResult(McpRegistrationOutcome.Malformed, "unbalanced brace"));

        // A file Yagu could not parse must never look like it was rewritten.
        Assert.Contains("left untouched", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unbalanced brace", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_HttpRegistration_MentionsTheEndpointAndTheTokenPrompt()
    {
        string message = McpClientSetup.Describe(
            new AppSettings { McpHttpEnabled = true },
            McpClientKind.VsCode,
            new McpRegistrationResult(McpRegistrationOutcome.Installed));

        Assert.Contains("127.0.0.1", message, StringComparison.Ordinal);
        Assert.Contains("token", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_StdioRegistration_MentionsNoToken()
    {
        string message = McpClientSetup.Describe(
            new AppSettings { McpHttpEnabled = false },
            McpClientKind.VsCode,
            new McpRegistrationResult(McpRegistrationOutcome.Installed));

        Assert.DoesNotContain("token", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_RemovalAndAbsence_AreDistinguishable()
    {
        string removed = McpClientSetup.Describe(
            new AppSettings(), McpClientKind.VsCode, new McpRegistrationResult(McpRegistrationOutcome.Removed));
        string absent = McpClientSetup.Describe(
            new AppSettings(), McpClientKind.VsCode, new McpRegistrationResult(McpRegistrationOutcome.NotPresent));

        Assert.NotEqual(removed, absent);
    }

    [Fact]
    public void Describe_UpdateAndFailure_AreDistinctAndCarryFailureDetail()
    {
        string updated = McpClientSetup.Describe(
            new AppSettings(), McpClientKind.VsCode, new McpRegistrationResult(McpRegistrationOutcome.Updated));
        string failed = McpClientSetup.Describe(
            new AppSettings(), McpClientKind.VsCode, new McpRegistrationResult(McpRegistrationOutcome.Failed, "disk full"));

        Assert.Contains("Updated", updated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disk full", failed, StringComparison.Ordinal);
        Assert.NotEqual(updated, failed);
    }

    // ── Apply: the step that actually hands out the grant ──

    private sealed class TempSetup : IDisposable
    {
        public TempSetup()
        {
            Root = Path.Combine(Path.GetTempPath(), "yagu-mcp-setup-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, "mcp.json");
            ServerExePath = Path.Combine(Root, "Yagu.McpServer.exe");
            YaguExePath = Path.Combine(Root, "Yagu.exe");
            TokenDirectory = Path.Combine(Root, "token");
        }

        public string Root { get; }
        public string ConfigPath { get; }
        public string ServerExePath { get; }
        public string YaguExePath { get; }
        public string TokenDirectory { get; }

        public string ReadConfig() => File.ReadAllText(ConfigPath);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static McpRegistrationResult Apply(TempSetup temp, AppSettings settings, McpClientKind client, bool install) =>
        McpClientSetup.Apply(
            settings, client, install, temp.ConfigPath, temp.ServerExePath, temp.YaguExePath, temp.TokenDirectory);

    [Fact]
    public void Apply_WritesAStdioEntryThatLaunchesTheServerItself()
    {
        using var temp = new TempSetup();

        McpRegistrationResult result = Apply(
            temp, new AppSettings { McpHttpEnabled = false, McpAllowedRoots = [temp.Root] }, McpClientKind.VsCode, install: true);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);
        Assert.True(McpClientSetup.IsInstalledIn(temp.ConfigPath, McpClientKind.VsCode));

        string config = temp.ReadConfig();

        // The client runs the server binary directly; a stdio entry must not carry an HTTP url.
        Assert.Contains("Yagu.McpServer.exe", config, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", config, StringComparison.Ordinal);
        Assert.DoesNotContain(McpConfigSnippetBuilder.TokenInputId, config, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WritesAnHttpEntryWithTheTokenHeldAsAnInputReference()
    {
        using var temp = new TempSetup();

        McpRegistrationResult result = Apply(
            temp, new AppSettings { McpHttpEnabled = true, McpAllowedRoots = [temp.Root] }, McpClientKind.VsCode, install: true);

        Assert.Equal(McpRegistrationOutcome.Installed, result.Outcome);

        string config = temp.ReadConfig();
        Assert.Contains("127.0.0.1", config, StringComparison.Ordinal);

        // The token is referenced, never written: a secret in this file lands in the user's dotfiles.
        Assert.Contains($"${{input:{McpConfigSnippetBuilder.TokenInputId}}}", config, StringComparison.Ordinal);
        string token = File.ReadAllText(McpTokenStore.TokenPathIn(temp.TokenDirectory)).Trim();
        Assert.NotEmpty(token);
        Assert.DoesNotContain(token, config, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_SwitchingFromHttpBackToStdio_DropsTheStrandedTokenPrompt()
    {
        using var temp = new TempSetup();
        var settings = new AppSettings { McpHttpEnabled = true, McpAllowedRoots = [temp.Root] };

        Apply(temp, settings, McpClientKind.VsCode, install: true);
        Assert.Contains(McpConfigSnippetBuilder.TokenInputId, temp.ReadConfig(), StringComparison.Ordinal);

        settings.McpHttpEnabled = false;
        McpRegistrationResult result = Apply(temp, settings, McpClientKind.VsCode, install: true);

        // Left behind, the input keeps prompting for a token no stdio entry ever reads.
        Assert.Equal(McpRegistrationOutcome.Updated, result.Outcome);
        Assert.DoesNotContain(McpConfigSnippetBuilder.TokenInputId, temp.ReadConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ReApplyingANarrowerGrant_RewritesTheCommandLineRatherThanAppending()
    {
        using var temp = new TempSetup();
        string kept = Path.Combine(temp.Root, "kept");
        string revoked = Path.Combine(temp.Root, "revoked");
        Directory.CreateDirectory(kept);
        Directory.CreateDirectory(revoked);

        var settings = new AppSettings { McpHttpEnabled = false, McpAllowedRoots = [kept, revoked] };
        Apply(temp, settings, McpClientKind.VsCode, install: true);
        Assert.Contains(revoked, GrantedRoots(temp), StringComparer.OrdinalIgnoreCase);

        settings.McpAllowedRoots = [kept];
        McpRegistrationResult result = Apply(temp, settings, McpClientKind.VsCode, install: true);

        // The stored command line IS the grant: a folder left in it stays served after being revoked.
        Assert.Equal(McpRegistrationOutcome.Updated, result.Outcome);
        string[] roots = GrantedRoots(temp);
        Assert.Contains(kept, roots, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(revoked, roots, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folders the written entry actually grants, read back out of its argument list.</summary>
    private static string[] GrantedRoots(TempSetup temp)
    {
        using JsonDocument document = JsonDocument.Parse(temp.ReadConfig());
        JsonElement args = document.RootElement
            .GetProperty(McpConfigSnippetBuilder.ServersPropertyName(McpClientKind.VsCode))
            .GetProperty(McpConfigSnippetBuilder.ServerName)
            .GetProperty("args");

        var roots = new List<string>();
        JsonElement[] items = [.. args.EnumerateArray()];
        for (int i = 0; i < items.Length - 1; i++)
        {
            if (items[i].GetString() == "--allowed-root")
                roots.Add(items[i + 1].GetString() ?? string.Empty);
        }

        return [.. roots];
    }

    [Fact]
    public void Apply_RemovingTakesOutBothTheServerEntryAndItsTokenInput()
    {
        using var temp = new TempSetup();
        var settings = new AppSettings { McpHttpEnabled = true, McpAllowedRoots = [temp.Root] };

        Apply(temp, settings, McpClientKind.VsCode, install: true);
        Assert.True(McpClientSetup.IsInstalledIn(temp.ConfigPath, McpClientKind.VsCode));

        McpRegistrationResult removed = Apply(temp, settings, McpClientKind.VsCode, install: false);

        Assert.Equal(McpRegistrationOutcome.Removed, removed.Outcome);
        Assert.False(McpClientSetup.IsInstalledIn(temp.ConfigPath, McpClientKind.VsCode));
        Assert.DoesNotContain(McpConfigSnippetBuilder.TokenInputId, temp.ReadConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RemovingFromAConfigThatNeverHadYagu_ReportsNotPresent()
    {
        using var temp = new TempSetup();

        McpRegistrationResult result = Apply(temp, new AppSettings(), McpClientKind.VsCode, install: false);

        Assert.Equal(McpRegistrationOutcome.NotPresent, result.Outcome);
    }

    [Fact]
    public void Apply_DefaultExecutableAndTokenPaths_AreSafeWhenRemovingFromATemporaryConfig()
    {
        using var temp = new TempSetup();

        McpRegistrationResult result = McpClientSetup.Apply(
            new AppSettings(),
            McpClientKind.VsCode,
            install: false,
            configPath: temp.ConfigPath);

        Assert.Equal(McpRegistrationOutcome.NotPresent, result.Outcome);
        Assert.False(File.Exists(temp.ConfigPath));
    }

    [Fact]
    public void Apply_UnknownClientDefaultConfigPath_CannotTouchLiveConfiguration()
    {
        var unknown = (McpClientKind)9999;

        McpRegistrationResult result = McpClientSetup.Apply(
            new AppSettings(), unknown, install: false);

        Assert.Equal(McpRegistrationOutcome.Failed, result.Outcome);
        Assert.Contains("No configuration path", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AClientThatCannotSpeakHttp_GetsStdioEvenWithHttpEnabled()
    {
        using var temp = new TempSetup();

        Apply(
            temp, new AppSettings { McpHttpEnabled = true, McpAllowedRoots = [temp.Root] },
            McpClientKind.ClaudeDesktop, install: true);

        string config = temp.ReadConfig();
        Assert.DoesNotContain("127.0.0.1", config, StringComparison.Ordinal);
        Assert.Contains("Yagu.McpServer.exe", config, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_AMalformedConfig_IsLeftExactlyAsItWas()
    {
        using var temp = new TempSetup();
        const string Garbage = "{ this is not json";
        File.WriteAllText(temp.ConfigPath, Garbage);

        McpRegistrationResult result = Apply(
            temp, new AppSettings { McpAllowedRoots = [temp.Root] }, McpClientKind.VsCode, install: true);

        // Rewriting a file Yagu could not parse would silently destroy the user's other servers.
        Assert.Equal(McpRegistrationOutcome.Malformed, result.Outcome);
        Assert.Equal(Garbage, temp.ReadConfig());
    }

    [Fact]
    public void Apply_PreservesOtherServersAlreadyInTheConfig()
    {
        using var temp = new TempSetup();
        File.WriteAllText(temp.ConfigPath, """
            { "servers": { "someone-else": { "command": "other.exe" } } }
            """);

        Apply(temp, new AppSettings { McpAllowedRoots = [temp.Root] }, McpClientKind.VsCode, install: true);

        string config = temp.ReadConfig();
        Assert.Contains("someone-else", config, StringComparison.Ordinal);
        Assert.Contains("other.exe", config, StringComparison.Ordinal);
        Assert.Contains(McpConfigSnippetBuilder.ServerName, config, StringComparison.Ordinal);
    }

    [Fact]
    public void IsInstalledIn_IsFalseForAMissingConfigFile()
    {
        using var temp = new TempSetup();

        Assert.False(McpClientSetup.IsInstalledIn(temp.ConfigPath, McpClientKind.VsCode));
    }

    [Fact]
    public void IsServerInstalled_AgreesWithTheResolvedServerPath()
    {
        // Read-only, so it is safe to run against the real installation. Every Install button is gated
        // on this, and disagreeing with the path the snippet names would enable a button that cannot work.
        Assert.Equal(
            File.Exists(McpConfigSnippetBuilder.ResolveServerExecutablePath()),
            McpClientSetup.IsServerInstalled());
    }

    [Fact]
    public void IsInstalled_ReadsTheRealConfigWithoutThrowingForAnyClient()
    {
        // Also read-only. It runs for every client while the Settings tab is being built, so a client
        // whose configuration is missing, malformed, or in a folder that does not exist must return
        // false rather than take the window down.
        foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
        {
            bool installed = McpClientSetup.IsInstalled(client);
            Assert.Equal(
                McpClientSetup.IsInstalledIn(McpConfigSnippetBuilder.ResolveConfigPath(client), client),
                installed);
        }
    }
}
