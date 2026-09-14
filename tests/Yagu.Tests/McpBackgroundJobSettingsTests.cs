using Yagu.McpServer;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// The ceilings on the MCP server's background-job machinery.
/// </summary>
/// <remarks>
/// These used to be constants inside the server. They are settings because the right value is a
/// property of the user's machine, not of Yagu: a four-hour index-maintenance ceiling is reasonable on
/// a spinning disk and absurd on an NVMe dev box, and the server never sees either.
/// </remarks>
public class McpBackgroundJobSettingsTests
{
    [Fact]
    public void Defaults_AreBoundedAndInRange()
    {
        var settings = new AppSettings();

        Assert.InRange(settings.McpMaxRetainedJobs,
            AppSettings.MinimumMcpMaxRetainedJobs, AppSettings.MaximumMcpMaxRetainedJobs);
        Assert.InRange(settings.McpMaxRunningJobs,
            AppSettings.MinimumMcpMaxRunningJobs, AppSettings.MaximumMcpMaxRunningJobs);
        Assert.InRange(settings.McpMaxJobResultChars,
            AppSettings.MinimumMcpMaxJobResultChars, AppSettings.MaximumMcpMaxJobResultChars);
        Assert.InRange(settings.McpCallTimeoutSeconds,
            AppSettings.MinimumMcpCallTimeoutSeconds, AppSettings.MaximumMcpCallTimeoutSeconds);
        Assert.InRange(settings.McpMaintenanceTimeoutMinutes,
            AppSettings.MinimumMcpMaintenanceTimeoutMinutes, AppSettings.MaximumMcpMaintenanceTimeoutMinutes);
        Assert.InRange(settings.McpModelTimeoutMinutes,
            AppSettings.MinimumMcpModelTimeoutMinutes, AppSettings.MaximumMcpModelTimeoutMinutes);
    }

    [Theory]
    [InlineData(0, AppSettings.DefaultMcpMaxRetainedJobs)]
    [InlineData(-1, AppSettings.DefaultMcpMaxRetainedJobs)]
    [InlineData(int.MaxValue, AppSettings.MaximumMcpMaxRetainedJobs)]
    [InlineData(1, AppSettings.MinimumMcpMaxRetainedJobs)]
    public void NormalizeRetainedJobs_ClampsToRange(int stored, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeMcpMaxRetainedJobs(stored));

    [Theory]
    [InlineData(0, AppSettings.DefaultMcpMaxRunningJobs)]
    [InlineData(int.MaxValue, AppSettings.MaximumMcpMaxRunningJobs)]
    public void NormalizeRunningJobs_ClampsToRange(int stored, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeMcpMaxRunningJobs(stored));

    [Theory]
    [InlineData(0, AppSettings.DefaultMcpCallTimeoutSeconds)]
    [InlineData(1, AppSettings.MinimumMcpCallTimeoutSeconds)]
    [InlineData(int.MaxValue, AppSettings.MaximumMcpCallTimeoutSeconds)]
    public void NormalizeCallTimeout_ClampsToRange(int stored, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeMcpCallTimeoutSeconds(stored));

    [Theory]
    [InlineData(0, AppSettings.DefaultMcpMaintenanceTimeoutMinutes)]
    [InlineData(int.MaxValue, AppSettings.MaximumMcpMaintenanceTimeoutMinutes)]
    public void NormalizeMaintenanceTimeout_ClampsToRange(int stored, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeMcpMaintenanceTimeoutMinutes(stored));

    [Theory]
    [InlineData(0, AppSettings.DefaultMcpModelTimeoutMinutes)]
    [InlineData(int.MaxValue, AppSettings.MaximumMcpModelTimeoutMinutes)]
    public void NormalizeModelTimeout_ClampsToRange(int stored, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeMcpModelTimeoutMinutes(stored));

    [Fact]
    public void Load_ClampsOutOfRangeStoredValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"yagu-mcp-jobs-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings
            {
                McpMaxRetainedJobs = int.MaxValue,
                McpMaxRunningJobs = -3,
                McpMaxJobResultChars = 1,
                McpCallTimeoutSeconds = int.MaxValue,
                McpMaintenanceTimeoutMinutes = 0,
                McpModelTimeoutMinutes = int.MaxValue,
            });

            AppSettings loaded = service.Load();

            Assert.Equal(AppSettings.MaximumMcpMaxRetainedJobs, loaded.McpMaxRetainedJobs);
            Assert.Equal(AppSettings.DefaultMcpMaxRunningJobs, loaded.McpMaxRunningJobs);
            Assert.Equal(AppSettings.MinimumMcpMaxJobResultChars, loaded.McpMaxJobResultChars);
            Assert.Equal(AppSettings.MaximumMcpCallTimeoutSeconds, loaded.McpCallTimeoutSeconds);
            Assert.Equal(AppSettings.DefaultMcpMaintenanceTimeoutMinutes, loaded.McpMaintenanceTimeoutMinutes);
            Assert.Equal(AppSettings.MaximumMcpModelTimeoutMinutes, loaded.McpModelTimeoutMinutes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RestoreDefaults_ResetsEveryJobSetting()
    {
        var settings = new AppSettings
        {
            McpMaxRetainedJobs = 999,
            McpMaxRunningJobs = 33,
            McpMaxJobResultChars = 999_999,
            McpCallTimeoutSeconds = 999,
            McpMaintenanceTimeoutMinutes = 999,
            McpModelTimeoutMinutes = 99,
        };

        settings.ResetMcpServerSettings();

        Assert.Equal(AppSettings.DefaultMcpMaxRetainedJobs, settings.McpMaxRetainedJobs);
        Assert.Equal(AppSettings.DefaultMcpMaxRunningJobs, settings.McpMaxRunningJobs);
        Assert.Equal(AppSettings.DefaultMcpMaxJobResultChars, settings.McpMaxJobResultChars);
        Assert.Equal(AppSettings.DefaultMcpCallTimeoutSeconds, settings.McpCallTimeoutSeconds);
        Assert.Equal(AppSettings.DefaultMcpMaintenanceTimeoutMinutes, settings.McpMaintenanceTimeoutMinutes);
        Assert.Equal(AppSettings.DefaultMcpModelTimeoutMinutes, settings.McpModelTimeoutMinutes);
    }

    [Fact]
    public void TheGrantCommandLine_CarriesEveryJobCeiling()
    {
        // The server takes its whole configuration from this command line, so a setting that is not
        // emitted here is a setting the running server never sees.
        var settings = new AppSettings
        {
            McpAllowedRoots = [Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)],
            McpMaxRetainedJobs = 77,
            McpMaxRunningJobs = 3,
            McpMaxJobResultChars = 9_999,
            McpCallTimeoutSeconds = 45,
            McpMaintenanceTimeoutMinutes = 90,
            McpModelTimeoutMinutes = 7,
        };

        List<string> args = Services.Mcp.McpConfigSnippetBuilder.BuildServerArguments(settings, @"C:\Yagu\Yagu.exe");

        AssertFlag(args, "--max-retained-jobs", "77");
        AssertFlag(args, "--max-running-jobs", "3");
        AssertFlag(args, "--max-job-result-chars", "9999");
        AssertFlag(args, "--call-timeout-seconds", "45");
        AssertFlag(args, "--maintenance-timeout-minutes", "90");
        AssertFlag(args, "--model-timeout-minutes", "7");
    }

    [Fact]
    public void TheServerResolvesEveryJobCeilingFromItsCommandLine()
    {
        string root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
        [
            "--yagu-exe", exe,
            "--allowed-root", root,
            "--max-retained-jobs", "77",
            "--max-running-jobs", "3",
            "--max-job-result-chars", "9999",
            "--call-timeout-seconds", "45",
            "--maintenance-timeout-minutes", "90",
            "--model-timeout-minutes", "7",
        ]);

        Assert.Null(options.Failure);
        Assert.Equal(77, options.MaxRetainedJobs);
        Assert.Equal(3, options.MaxRunningJobs);
        Assert.Equal(9_999, options.MaxJobResultChars);
        Assert.Equal(TimeSpan.FromSeconds(45), options.DefaultCallTimeout);
        Assert.Equal(TimeSpan.FromMinutes(90), options.MaintenanceTimeout);
        Assert.Equal(TimeSpan.FromMinutes(7), options.ModelTimeout);
    }

    [Theory]
    [InlineData("--max-retained-jobs")]
    [InlineData("--max-running-jobs")]
    [InlineData("--max-job-result-chars")]
    [InlineData("--call-timeout-seconds")]
    [InlineData("--maintenance-timeout-minutes")]
    [InlineData("--model-timeout-minutes")]
    public void AnInvalidJobCeiling_IsRefusedRatherThanDefaulted(string flag)
    {
        string root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            ["--yagu-exe", exe, "--allowed-root", root, flag, "0"]);

        Assert.NotNull(options.Failure);
        Assert.Contains(flag, options.Failure);
    }

    [Fact]
    public void TheJobRegistryHonorsTheConfiguredRunningLimit()
    {
        var registry = new BridgeJobRegistry(maxRetained: 8, maxRunning: 2, maxResultChars: 4096);
        using var block = new SemaphoreSlim(0);

        Assert.Equal(2, registry.MaxRunningJobs);

        BridgeJob? first = registry.Start("t", "first", async token => { await block.WaitAsync(token); return "a"; });
        BridgeJob? second = registry.Start("t", "second", async token => { await block.WaitAsync(token); return "b"; });
        BridgeJob? third = registry.Start("t", "third", _ => Task.FromResult("c"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third); // Refused rather than queued, so work cannot accumulate unboundedly.

        block.Release(2);
        registry.Dispose();
    }

    private static void AssertFlag(List<string> args, string flag, string expected)
    {
        int index = args.IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"'{flag}' was not emitted into the server command line.");
        Assert.Equal(expected, args[index + 1]);
    }
}
