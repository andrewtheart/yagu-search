using System.Text.Json;
using Yagu.Services;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// The persisted MCP configuration (plan Phase 4, step 25) and the client snippet built from it. The
/// snippet <b>is</b> the grant — the server takes its whole configuration from its command line — so
/// these assert that what is persisted, what is normalized, and what a client would be handed all agree.
/// </summary>
public class McpServerSettingsTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"yagu-mcp-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_GrantReadAndSearchOnly()
    {
        var settings = new AppSettings();

        Assert.Equal(["Read", "Search"], settings.McpCapabilities);
        Assert.Empty(settings.McpAllowedRoots);
        Assert.Equal(AppSettings.DefaultMcpMaxConcurrentCalls, settings.McpMaxConcurrentCalls);
    }

    [Fact]
    public void Defaults_MatchTheShippedResponseBudget()
    {
        var settings = new AppSettings();

        Assert.Equal(MachineOutputBudget.Default, settings.EffectiveMachineOutputBudget);
    }

    [Fact]
    public void NormalizeCapabilities_DropsUnknownNamesAndDeduplicates()
    {
        List<string> normalized = AppSettings.NormalizeMcpCapabilities(
            ["search", "Search", "TotalControl", string.Empty, "  ", "read"]);

        Assert.Equal(["Read", "Search"], normalized);
    }

    [Fact]
    public void NormalizeCapabilities_ReturnsDeclarationOrderSoTheSnippetIsStable()
    {
        List<string> normalized = AppSettings.NormalizeMcpCapabilities(
            ["DestructiveIndexOps", "Search", "Read"]);

        Assert.Equal(["Read", "Search", "DestructiveIndexOps"], normalized);
    }

    [Fact]
    public void NormalizeCapabilities_IgnoresNone()
    {
        Assert.Empty(AppSettings.NormalizeMcpCapabilities(["None"]));
        Assert.Empty(AppSettings.NormalizeMcpCapabilities(null));
    }

    [Fact]
    public void NormalizeAllowedRoots_DropsRootsTheServerWouldRefuse()
    {
        // A relative root would resolve against whatever working directory the server happened to have,
        // and a device path defeats normalization for everything under it. Neither may sit in settings
        // looking effective.
        List<string> normalized = AppSettings.NormalizeMcpAllowedRoots(
            [@"C:\Projects\", "relative\\path", @"\\?\C:\Projects", string.Empty]);

        Assert.Equal([@"C:\Projects"], normalized);
    }

    [Fact]
    public void NormalizeAllowedRoots_CollapsesRepeatsOfTheSameFolder()
    {
        // Granting folders again for each client re-adds the ones already saved, so a repeat has to
        // collapse instead of stacking another row in the allowed-folders list.
        List<string> normalized = AppSettings.NormalizeMcpAllowedRoots(
            [@"C:\Projects", @"C:\Projects\", @"c:\projects", @"C:\Projects", @"D:\Notes"]);

        Assert.Equal([@"C:\Projects", @"D:\Notes"], normalized);
    }

    [Fact]
    public void NormalizeAllowedRoots_IsIdempotentAcrossRepeatedGrants()
    {
        // Reproduces the onboarding flow: the same selection committed once per registered client.
        List<string> roots = [];
        for (int grant = 0; grant < 4; grant++)
            roots = AppSettings.NormalizeMcpAllowedRoots([.. roots, @"C:\Users\Someone\Documents"]);

        Assert.Equal([@"C:\Users\Someone\Documents"], roots);
    }

    [Fact]
    public void NormalizeMaxConcurrentCalls_ClampsToSupportedRange()
    {
        Assert.Equal(AppSettings.MinimumMcpMaxConcurrentCalls, AppSettings.NormalizeMcpMaxConcurrentCalls(0));
        Assert.Equal(AppSettings.MaximumMcpMaxConcurrentCalls, AppSettings.NormalizeMcpMaxConcurrentCalls(9999));
        Assert.Equal(3, AppSettings.NormalizeMcpMaxConcurrentCalls(3));
    }

    [Fact]
    public void Load_ClampsOutOfRangeCeilingsInsteadOfReturningAnUnboundedResponse()
    {
        string path = TempSettingsPath();
        try
        {
            var service = new SettingsService(path);
            var saved = new AppSettings
            {
                McpMaxLineBytes = int.MaxValue,
                McpMaxContextLineBytes = 0,
                McpMaxPathBytes = 0,
                McpMaxContextLines = -5,
                McpMaxRecords = 0,
                McpMaxResponseBytes = long.MaxValue,
                McpMaxConcurrentCalls = 500,
            };
            service.Save(saved);

            AppSettings loaded = service.Load();

            Assert.Equal(MachineOutputBudget.MaxLineBytesCeiling, loaded.McpMaxLineBytes);
            Assert.Equal(MachineOutputBudget.MinLineBytes, loaded.McpMaxContextLineBytes);
            Assert.Equal(MachineOutputBudget.MinPathBytes, loaded.McpMaxPathBytes);
            Assert.Equal(MachineOutputBudget.MinContextLines, loaded.McpMaxContextLines);
            Assert.Equal(MachineOutputBudget.MinRecords, loaded.McpMaxRecords);
            Assert.Equal(MachineOutputBudget.MaxResponseBytesCeiling, loaded.McpMaxResponseBytes);
            Assert.Equal(AppSettings.MaximumMcpMaxConcurrentCalls, loaded.McpMaxConcurrentCalls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_NormalizesPersistedRootsAndCapabilities()
    {
        string path = TempSettingsPath();
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings
            {
                McpAllowedRoots = [@"C:\Projects\", "not-fully-qualified"],
                McpCapabilities = ["search", "bogus"],
            });

            AppSettings loaded = service.Load();

            Assert.Equal([@"C:\Projects"], loaded.McpAllowedRoots);
            Assert.Equal(["Search"], loaded.McpCapabilities);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllowedRootsAndCapabilities()
    {
        string path = TempSettingsPath();
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings
            {
                McpAllowedRoots = [@"C:\Projects", @"D:\Notes"],
                McpCapabilities = ["Read", "Search", "IndexMaintenance"],
                McpMaxConcurrentCalls = 8,
            });

            AppSettings loaded = service.Load();

            Assert.Equal([@"C:\Projects", @"D:\Notes"], loaded.McpAllowedRoots);
            Assert.Equal(["Read", "Search", "IndexMaintenance"], loaded.McpCapabilities);
            Assert.Equal(8, loaded.McpMaxConcurrentCalls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ResetMcpServerSettings_ClearsRootsSoNothingStaysAuthorized()
    {
        var settings = new AppSettings
        {
            McpAllowedRoots = [@"C:\Projects"],
            McpCapabilities = ["Read", "Search", "DestructiveIndexOps"],
            McpMaxRecords = 12,
        };

        settings.ResetMcpServerSettings();

        Assert.Empty(settings.McpAllowedRoots);
        Assert.Equal(["Read", "Search"], settings.McpCapabilities);
        Assert.Equal(MachineOutputBudget.Default.MaxRecords, settings.McpMaxRecords);
    }

    // ── Snippet ──

    private static AppSettings SnippetSettings() => new()
    {
        McpAllowedRoots = [@"C:\Projects"],
        McpCapabilities = ["Search", "Read"],
        McpMaxConcurrentCalls = 6,
    };

    [Fact]
    public void BuildServerArguments_EmitsEveryRootAndCapabilityPlusConcurrency()
    {
        List<string> args = McpConfigSnippetBuilder.BuildServerArguments(SnippetSettings(), @"C:\App\Yagu.exe");

        // Asserted as a prefix rather than the whole vector: the job ceilings are appended after these
        // and have their own coverage, so adding one must not fail an unrelated grant test.
        Assert.Equal(
            ["--yagu-exe", @"C:\App\Yagu.exe",
             "--allowed-root", @"C:\Projects",
             "--capability", "Read",
             "--capability", "Search",
             "--max-concurrent", "6",
             "--max-export-results", AppSettings.DefaultMcpMaxExportResults.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            args.Take(12));

        // Every remaining token is a flag/value pair, so nothing was appended half-formed.
        Assert.Equal(0, (args.Count - 12) % 2);
    }

    [Fact]
    public void BuildServerArguments_NormalizesSoTheSnippetCannotGrantMoreThanTheServerHonors()
    {
        var settings = new AppSettings
        {
            McpAllowedRoots = ["relative\\path"],
            McpCapabilities = ["bogus"],
            McpMaxConcurrentCalls = 9999,
        };

        List<string> args = McpConfigSnippetBuilder.BuildServerArguments(settings, @"C:\App\Yagu.exe");

        Assert.DoesNotContain("--allowed-root", args);
        Assert.DoesNotContain("--capability", args);

        int concurrency = args.IndexOf("--max-concurrent");
        Assert.Equal(
            AppSettings.MaximumMcpMaxConcurrentCalls.ToString(System.Globalization.CultureInfo.InvariantCulture),
            args[concurrency + 1]);
    }

    // The client kind is passed by name because McpClientKind is internal and a public xUnit theory
    // method cannot take a less accessible parameter type.
    [Theory]
    [InlineData(nameof(McpClientKind.VsCode), "servers", "stdio")]
    [InlineData(nameof(McpClientKind.VisualStudio), "servers", "stdio")]
    [InlineData(nameof(McpClientKind.CopilotCli), "mcpServers", "local")]
    [InlineData(nameof(McpClientKind.ClaudeDesktop), "mcpServers", null)]
    public void BuildClientConfig_UsesEachClientsSchema(string clientName, string containerProperty, string? transportType)
    {
        var client = Enum.Parse<McpClientKind>(clientName);
        List<string> args = McpConfigSnippetBuilder.BuildServerArguments(SnippetSettings(), @"C:\App\Yagu.exe");

        string json = McpConfigSnippetBuilder.BuildClientConfig(client, @"C:\App\mcp-server\Yagu.McpServer.exe", args);

        using var document = JsonDocument.Parse(json);
        JsonElement server = document.RootElement
            .GetProperty(containerProperty)
            .GetProperty(McpConfigSnippetBuilder.ServerName);

        Assert.Equal(@"C:\App\mcp-server\Yagu.McpServer.exe", server.GetProperty("command").GetString());
        Assert.Equal(args, server.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToList());

        if (transportType is null)
            Assert.False(server.TryGetProperty("type", out _));
        else
            Assert.Equal(transportType, server.GetProperty("type").GetString());
    }

    [Fact]
    public void BuildClientConfig_IsValidJsonWithEscapedWindowsPaths()
    {
        string json = McpConfigSnippetBuilder.BuildClientConfig(
            McpClientKind.VsCode, @"C:\App\mcp-server\Yagu.McpServer.exe", [@"--allowed-root", @"C:\Projects"]);

        Assert.Contains(@"C:\\App\\mcp-server\\Yagu.McpServer.exe", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void BuildClientConfig_CopilotCliDeclaresItsToolFilter()
    {
        string json = McpConfigSnippetBuilder.BuildClientConfig(
            McpClientKind.CopilotCli, @"C:\App\mcp-server\Yagu.McpServer.exe", []);

        using var document = JsonDocument.Parse(json);
        JsonElement tools = document.RootElement
            .GetProperty("mcpServers")
            .GetProperty(McpConfigSnippetBuilder.ServerName)
            .GetProperty("tools");

        Assert.Equal(["*"], tools.EnumerateArray().Select(t => t.GetString()).ToList());
    }

    [Fact]
    public void EveryClientDeclaresWhereItsConfigLives()
    {
        foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(McpConfigSnippetBuilder.DisplayName(client)));
            Assert.False(string.IsNullOrWhiteSpace(McpConfigSnippetBuilder.ConfigLocation(client)));
        }
    }

    [Fact]
    public void EveryClientRoundTripsThroughItsCommandLineName()
    {
        foreach (McpClientKind client in Enum.GetValues<McpClientKind>())
        {
            Assert.True(McpConfigSnippetBuilder.TryParseClient(McpConfigSnippetBuilder.CommandLineName(client), out var parsed));
            Assert.Equal(client, parsed);
        }
    }

    [Theory]
    [InlineData("vscode", nameof(McpClientKind.VsCode))]
    [InlineData("CODE", nameof(McpClientKind.VsCode))]
    [InlineData(" vs ", nameof(McpClientKind.VisualStudio))]
    [InlineData("visual-studio", nameof(McpClientKind.VisualStudio))]
    [InlineData("copilot", nameof(McpClientKind.CopilotCli))]
    [InlineData("claude-desktop", nameof(McpClientKind.ClaudeDesktop))]
    public void TryParseClient_AcceptsTheDocumentedAliases(string value, string expected)
    {
        Assert.True(McpConfigSnippetBuilder.TryParseClient(value, out var client));
        Assert.Equal(Enum.Parse<McpClientKind>(expected), client);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("emacs")]
    public void TryParseClient_RejectsAnythingElse(string? value)
        => Assert.False(McpConfigSnippetBuilder.TryParseClient(value, out _));
}
