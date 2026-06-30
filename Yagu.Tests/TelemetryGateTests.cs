using Yagu.Services.Telemetry;

namespace Yagu.Tests;

public class TelemetryGateTests : IDisposable
{
    public void Dispose()
    {
        // Reset the process-wide gate so state never leaks into other tests.
        TelemetryGate.TelemetryEnabled = false;
        TelemetryGate.BugReportingEnabled = false;
        TelemetryGate.Headless = false;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ShouldSendTelemetry_IsFalse_WhenEndpointUnconfigured_EvenIfConsented()
    {
        TelemetryGate.TelemetryEnabled = true;
        TelemetryGate.Headless = false;
        // No configured endpoint in source, so consent alone must not enable sending.
        Assert.False(TelemetryGate.ShouldSendTelemetry);
    }

    [Fact]
    public void ShouldOfferBugReport_IsFalse_WhenEndpointUnconfigured_EvenIfConsented()
    {
        TelemetryGate.BugReportingEnabled = true;
        TelemetryGate.Headless = false;
        Assert.False(TelemetryGate.ShouldOfferBugReport);
    }

    [Fact]
    public void Headless_SuppressesEverything()
    {
        TelemetryGate.TelemetryEnabled = true;
        TelemetryGate.BugReportingEnabled = true;
        TelemetryGate.Headless = true;
        Assert.False(TelemetryGate.ShouldSendTelemetry);
        Assert.False(TelemetryGate.ShouldOfferBugReport);
    }

    [Fact]
    public void Flags_AreIndependentlyToggleable()
    {
        TelemetryGate.TelemetryEnabled = true;
        TelemetryGate.BugReportingEnabled = false;
        Assert.True(TelemetryGate.TelemetryEnabled);
        Assert.False(TelemetryGate.BugReportingEnabled);

        TelemetryGate.TelemetryEnabled = false;
        TelemetryGate.BugReportingEnabled = true;
        Assert.False(TelemetryGate.TelemetryEnabled);
        Assert.True(TelemetryGate.BugReportingEnabled);
    }
}
