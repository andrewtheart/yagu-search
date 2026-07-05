using System.Text.Json;
using Yagu.Services.Telemetry;

namespace Yagu.Tests;

public class TelemetryPayloadsTests
{
    [Fact]
    public void TelemetryEnvelope_SerializesWithCamelCaseNames()
    {
        var envelope = new TelemetryEnvelope
        {
            InstallId = "abc123",
            SessionId = "sess1",
            AppVersion = "1.2.3.4",
            Os = "Windows",
            Events =
            {
                new TelemetryEvent
                {
                    Kind = "error",
                    Name = "UnhandledException",
                    TimestampUtc = "2024-01-01T00:00:00.0000000Z",
                    Properties = { ["exceptionType"] = "System.Exception" },
                    Measurements = { ["durationMs"] = 12.5 },
                },
            },
        };

        string json = JsonSerializer.Serialize(envelope, TelemetryJsonContext.Default.TelemetryEnvelope);

        Assert.Contains("\"installId\":\"abc123\"", json);
        Assert.Contains("\"sessionId\":\"sess1\"", json);
        Assert.Contains("\"appVersion\":\"1.2.3.4\"", json);
        Assert.Contains("\"events\":", json);
        Assert.Contains("\"exceptionType\":\"System.Exception\"", json);
        Assert.Contains("\"durationMs\":12.5", json);
    }

    [Fact]
    public void BugReportPayload_RoundTrips()
    {
        var payload = new BugReportPayload
        {
            InstallId = "i1",
            CorrelationId = "c1",
            AppVersion = "1.0.0.0",
            Os = "Windows 11",
            Source = "Critical:Search",
            ExceptionType = "System.IO.IOException",
            Message = "disk error",
            StackTrace = "at Foo()",
            Gpu = "NVIDIA RTX 4070",
            Npu = "Intel AI Boost",
            Email = "user@example.com",
            UserComment = "happened while searching",
            SettingsJson = "{\"a\":1}",
            LogTail = "log line",
        };

        string json = JsonSerializer.Serialize(payload, TelemetryJsonContext.Default.BugReportPayload);
        BugReportPayload? back = JsonSerializer.Deserialize<BugReportPayload>(json, TelemetryJsonContext.Default.BugReportPayload);

        Assert.NotNull(back);
        Assert.Equal(payload.CorrelationId, back!.CorrelationId);
        Assert.Equal(payload.ExceptionType, back.ExceptionType);
        Assert.Equal(payload.Email, back.Email);
        Assert.Equal(payload.SettingsJson, back.SettingsJson);
        Assert.Equal(payload.LogTail, back.LogTail);
        Assert.Contains("\"correlationId\":\"c1\"", json);
    }

    [Fact]
    public void BugReportResponse_RoundTrips()
    {
        var response = new BugReportResponse { CorrelationId = "xyz", Status = "received" };

        string json = JsonSerializer.Serialize(response, TelemetryJsonContext.Default.BugReportResponse);
        BugReportResponse? back = JsonSerializer.Deserialize<BugReportResponse>(json, TelemetryJsonContext.Default.BugReportResponse);

        Assert.NotNull(back);
        Assert.Equal("xyz", back!.CorrelationId);
        Assert.Equal("received", back.Status);
        Assert.Contains("\"status\":\"received\"", json);
    }
}
