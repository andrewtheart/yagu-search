using Yagu.McpServer;
using Yagu.Services;
using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// The ceiling on tools that collect every match in memory before writing.
/// </summary>
/// <remarks>
/// A search streams and is bounded by the response budget. An export or a saved session is not: the
/// child process holds the whole result set before it writes anything, and the count comes from the
/// client. Without a ceiling, one large number turns a bounded tool into an unbounded one.
/// </remarks>
public class McpExportCeilingTests
{
    [Fact]
    public void Default_IsBoundedNotUnlimited()
    {
        var settings = new AppSettings();

        Assert.Equal(AppSettings.DefaultMcpMaxExportResults, settings.McpMaxExportResults);
        Assert.InRange(settings.McpMaxExportResults,
            AppSettings.MinimumMcpMaxExportResults, AppSettings.MaximumMcpMaxExportResults);
    }

    [Theory]
    [InlineData(0, AppSettings.DefaultMcpMaxExportResults)]
    [InlineData(-5, AppSettings.DefaultMcpMaxExportResults)]
    [InlineData(int.MaxValue, AppSettings.MaximumMcpMaxExportResults)]
    [InlineData(25_000, 25_000)]
    public void Normalize_ClampsToTheSupportedRange(int stored, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeMcpMaxExportResults(stored));

    [Fact]
    public void Load_ClampsAnOutOfRangeStoredValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"yagu-export-ceiling-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings { McpMaxExportResults = int.MaxValue });

            Assert.Equal(AppSettings.MaximumMcpMaxExportResults, service.Load().McpMaxExportResults);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TheCeilingReachesTheServerOnItsCommandLine()
    {
        // The server takes its whole configuration from argv, so a ceiling that never gets emitted is a
        // ceiling that does not exist.
        var settings = new AppSettings
        {
            McpAllowedRoots = [@"C:\Projects"],
            McpCapabilities = ["Search", "ExportWrite"],
            McpMaxExportResults = 1234,
        };

        List<string> args = McpConfigSnippetBuilder.BuildServerArguments(settings, @"C:\App\Yagu.exe");

        int index = args.IndexOf("--max-export-results");
        Assert.True(index >= 0, "the export ceiling was not emitted into the server command line.");
        Assert.Equal("1234", args[index + 1]);
    }

    [Fact]
    public void TheServerParsesTheCeiling()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
        [
            "--yagu-exe", CurrentExecutable(),
            "--allowed-root", Path.GetTempPath(),
            "--max-export-results", "777",
        ]);

        Assert.Null(options.Failure);
        Assert.Equal(777, options.MaxExportResults);
    }

    [Fact]
    public void TheServerRejectsAnUnusableCeiling()
    {
        foreach (string value in new[] { "0", "-1", "banana" })
        {
            YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            [
                "--yagu-exe", CurrentExecutable(),
                "--allowed-root", Path.GetTempPath(),
                "--max-export-results", value,
            ]);

            Assert.NotNull(options.Failure);
        }
    }

    [Fact]
    public void AnAbsentCeilingFallsBackToABoundedDefault()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
        [
            "--yagu-exe", CurrentExecutable(),
            "--allowed-root", Path.GetTempPath(),
        ]);

        Assert.Null(options.Failure);
        Assert.Equal(YaguMcpServerOptions.DefaultMaxExportResults, options.MaxExportResults);
    }

    /// <summary>The resolver requires an existing --yagu-exe; any real file satisfies that check.</summary>
    private static string CurrentExecutable() => typeof(McpExportCeilingTests).Assembly.Location;
}
