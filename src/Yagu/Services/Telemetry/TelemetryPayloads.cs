using System.Text.Json.Serialization;

namespace Yagu.Services.Telemetry;

/// <summary>
/// Wire contract for the silent telemetry channel. One envelope carries a small batch of anonymized
/// events to the proxy Function, which forwards them to Application Insights. Contains NO file paths,
/// search queries, file contents, directory names, or machine identifiers — only a random install
/// GUID, coarse app/OS version, and scrubbed events.
/// </summary>
public sealed class TelemetryEnvelope
{
    /// <summary>Random, non-PII identifier generated once per install. Lets us count distinct
    /// installs without identifying the user or machine.</summary>
    [JsonPropertyName("installId")] public string InstallId { get; set; } = string.Empty;

    /// <summary>Random identifier for the current process/session (lets events be grouped).</summary>
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;
    [JsonPropertyName("events")] public List<TelemetryEvent> Events { get; set; } = new();
}

/// <summary>A single anonymized telemetry event: an error summary or a performance measurement.</summary>
public sealed class TelemetryEvent
{
    /// <summary>"error" or "perf".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

    /// <summary>Short event name, e.g. "Startup", "Search", or an error source like "UnhandledException".</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("timestampUtc")] public string TimestampUtc { get; set; } = string.Empty;

    /// <summary>Scrubbed string dimensions (e.g. exception type). Never contains paths or queries.</summary>
    [JsonPropertyName("properties")] public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>Numeric measurements (e.g. durationMs, resultCount).</summary>
    [JsonPropertyName("measurements")] public Dictionary<string, double> Measurements { get; set; } = new();
}

/// <summary>
/// Wire contract for the EXPLICIT bug-report channel. Unlike <see cref="TelemetryEnvelope"/>, this is
/// only sent after the user reviews the exact contents in a dialog and clicks Submit. It may include
/// the user's settings file and log tail (which can contain paths and recent searches) precisely
/// because the user has seen and approved them. The proxy Function uploads <see cref="SettingsJson"/>
/// and <see cref="LogTail"/> to blob storage under <see cref="CorrelationId"/> and records a matching
/// Application Insights event.
/// </summary>
public sealed class BugReportPayload
{
    [JsonPropertyName("installId")] public string InstallId { get; set; } = string.Empty;
    [JsonPropertyName("correlationId")] public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;

    /// <summary>Where the error came from (e.g. "UnhandledException", "Critical:Search").</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("exceptionType")] public string ExceptionType { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("stackTrace")] public string StackTrace { get; set; } = string.Empty;

    /// <summary>Best-effort GPU description(s); empty when none detected.</summary>
    [JsonPropertyName("gpu")] public string Gpu { get; set; } = string.Empty;

    /// <summary>Best-effort NPU description(s); empty when none detected.</summary>
    [JsonPropertyName("npu")] public string Npu { get; set; } = string.Empty;

    /// <summary>Optional contact email the user typed (so we can follow up). Empty when not provided.</summary>
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;

    /// <summary>Optional free-text the user added describing what they were doing.</summary>
    [JsonPropertyName("userComment")] public string UserComment { get; set; } = string.Empty;

    /// <summary>Raw contents of the user's settings.json (reviewed by the user before sending).</summary>
    [JsonPropertyName("settingsJson")] public string SettingsJson { get; set; } = string.Empty;

    /// <summary>Tail of the user's yagu.log (reviewed by the user before sending).</summary>
    [JsonPropertyName("logTail")] public string LogTail { get; set; } = string.Empty;
}

/// <summary>Response from the bug-report endpoint, echoing the correlation id used for blob storage.</summary>
public sealed class BugReportResponse
{
    [JsonPropertyName("correlationId")] public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
}

/// <summary>AOT-safe source-generated serialization for the telemetry/bug-report wire types. Mirrors
/// the existing <c>AppSettingsJsonContext</c> pattern so no reflection-based serializer is needed
/// (required because Yagu publishes with <c>PublishAot=true</c>).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(TelemetryEnvelope))]
[JsonSerializable(typeof(BugReportPayload))]
[JsonSerializable(typeof(BugReportResponse))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext { }
