using Yagu.Services.Telemetry;

namespace Yagu.Tests;

public class TelemetryConfigTests
{
    [Fact]
    public void IsConfigured_IsFalse_WithPlaceholderUrl()
    {
        // The shipped source keeps the placeholder URL so an unconfigured build stays fully offline.
        Assert.False(TelemetryConfig.IsConfigured);
    }

    [Fact]
    public void Endpoints_AreNull_WhenUnconfigured()
    {
        Assert.Null(TelemetryConfig.TelemetryEndpoint);
        Assert.Null(TelemetryConfig.BugReportEndpoint);
    }

    [Fact]
    public void Paths_AreApiRoutes()
    {
        Assert.Equal("/api/telemetry", TelemetryConfig.TelemetryPath);
        Assert.Equal("/api/bugreport", TelemetryConfig.BugReportPath);
    }
}
