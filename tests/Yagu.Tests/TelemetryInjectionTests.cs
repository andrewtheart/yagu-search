using System;
using System.IO;
using Yagu.Services.Telemetry;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Guards the build-time telemetry injection: the committed source must stay offline-by-default and
/// must not carry the real proxy endpoint or app token (those are injected from a gitignored
/// <c>telemetry.local.props</c> / env vars by <c>Yagu.csproj</c>). The test build never injects, so
/// <see cref="TelemetryConfig"/> is always inert here.
/// </summary>
public sealed class TelemetryInjectionTests
{
    [Fact]
    public void LogicFile_IsPartial_AndCommitsNoEndpointOrToken()
    {
        string config = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "Services", "Telemetry", "TelemetryConfig.cs"));

        Assert.Contains("public static partial class TelemetryConfig", config);
        // The real values live only in the (swappable) companion partial, never in the logic file.
        Assert.DoesNotContain("public const string ProxyBaseUrl", config);
        Assert.DoesNotContain("public const string AppToken", config);
    }

    [Fact]
    public void DefaultsFile_KeepsBuildOffline()
    {
        string defaults = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "Services", "Telemetry", "TelemetryConfig.Defaults.cs"));

        Assert.Contains("public static partial class TelemetryConfig", defaults);
        Assert.Contains("public const string ProxyBaseUrl = PlaceholderBaseUrl;", defaults);
        Assert.Contains("public const string AppToken = \"\";", defaults);
    }

    [Fact]
    public void UninjectedBuild_IsInert()
    {
        Assert.False(TelemetryConfig.IsConfigured);
        Assert.Null(TelemetryConfig.TelemetryEndpoint);
        Assert.Null(TelemetryConfig.BugReportEndpoint);
    }

    [Fact]
    public void BuildPlumbing_InjectsWhenConfigured_AndKeepsSecretsOutOfGit()
    {
        string csproj = File.ReadAllText(Path.Combine(Root, "src", "Yagu", "Yagu.csproj"));
        Assert.Contains("GenerateYaguTelemetryConfig", csproj);
        Assert.Contains("telemetry.local.props", csproj);
        Assert.Contains("YAGU_PROXY_URL", csproj);
        // When configured, the offline placeholder is swapped out for the generated values.
        Assert.Contains("TelemetryConfig.Defaults.cs", csproj);

        Assert.True(
            File.Exists(Path.Combine(Root, "scripts", "generate-telemetry-config.ps1")),
            "The telemetry config generator script must exist.");

        string gitignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        Assert.Contains("telemetry.local.props", gitignore);
    }

    private static string Root => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
