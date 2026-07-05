using System.Text.Json.Serialization;

namespace Yagu.TelemetryFunction;

// Wire contract — these MUST match the client DTOs in
// Yagu/Services/Telemetry/TelemetryPayloads.cs (camelCase JSON property names).

internal sealed class TelemetryEnvelope
{
    [JsonPropertyName("installId")] public string InstallId { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("events")] public List<TelemetryEvent> Events { get; set; } = new();
}

internal sealed class TelemetryEvent
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("timestampUtc")] public string TimestampUtc { get; set; } = string.Empty;
    [JsonPropertyName("properties")] public Dictionary<string, string> Properties { get; set; } = new();
    [JsonPropertyName("measurements")] public Dictionary<string, double> Measurements { get; set; } = new();
}

internal sealed class BugReportPayload
{
    [JsonPropertyName("installId")] public string InstallId { get; set; } = string.Empty;
    [JsonPropertyName("correlationId")] public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("exceptionType")] public string ExceptionType { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("stackTrace")] public string StackTrace { get; set; } = string.Empty;
    [JsonPropertyName("gpu")] public string Gpu { get; set; } = string.Empty;
    [JsonPropertyName("npu")] public string Npu { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("userComment")] public string UserComment { get; set; } = string.Empty;
    [JsonPropertyName("settingsJson")] public string SettingsJson { get; set; } = string.Empty;
    [JsonPropertyName("logTail")] public string LogTail { get; set; } = string.Empty;
}

internal sealed class BugReportResponse
{
    [JsonPropertyName("correlationId")] public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
}
