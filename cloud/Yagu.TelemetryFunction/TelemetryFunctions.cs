using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Yagu.TelemetryFunction;

/// <summary>
/// HTTP proxy endpoints for the Yagu desktop client. Both are token-gated (a rotatable shared secret
/// in app settings) and size-capped. <c>telemetry</c> forwards anonymized diagnostics to Application
/// Insights; <c>bugreport</c> records a user-reviewed report and uploads its attachments to private
/// blob storage under a correlation id.
/// </summary>
public sealed class TelemetryFunctions
{
    // App-level size caps (defense in depth; the host also enforces its own limits).
    private const long MaxTelemetryBytes = 256 * 1024;       // 256 KB
    private const long MaxBugReportBytes = 4 * 1024 * 1024;  // 4 MB
    private const int MaxEventMessageChars = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TelemetryClient _telemetry;
    private readonly BlobServiceClient? _blobServiceClient;
    private readonly ILogger<TelemetryFunctions> _log;

    public TelemetryFunctions(TelemetryClient telemetry, ILogger<TelemetryFunctions> log, BlobServiceClient? blobServiceClient = null)
    {
        _telemetry = telemetry;
        _log = log;
        _blobServiceClient = blobServiceClient;
    }

    [Function("telemetry")]
    public async Task<IActionResult> Telemetry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "telemetry")] HttpRequest req)
    {
        if (!IsTokenValid(req))
            return new UnauthorizedResult();
        if (req.ContentLength > MaxTelemetryBytes)
            return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);

        TelemetryEnvelope? envelope;
        try
        {
            string body = await ReadBodyAsync(req, MaxTelemetryBytes);
            envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rejected malformed telemetry payload.");
            return new BadRequestResult();
        }

        if (envelope is null || envelope.Events.Count == 0)
            return new OkResult();

        foreach (TelemetryEvent ev in envelope.Events)
            ForwardEvent(envelope, ev);

        _telemetry.Flush();
        return new OkResult();
    }

    [Function("bugreport")]
    public async Task<IActionResult> BugReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bugreport")] HttpRequest req)
    {
        if (!IsTokenValid(req))
            return new UnauthorizedResult();
        if (req.ContentLength > MaxBugReportBytes)
            return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);

        BugReportPayload? payload;
        try
        {
            string body = await ReadBodyAsync(req, MaxBugReportBytes);
            payload = JsonSerializer.Deserialize<BugReportPayload>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rejected malformed bug-report payload.");
            return new BadRequestResult();
        }

        if (payload is null)
            return new BadRequestResult();

        string correlationId = string.IsNullOrWhiteSpace(payload.CorrelationId)
            ? Guid.NewGuid().ToString("N")
            : payload.CorrelationId;

        bool stored = await TryUploadAttachmentsAsync(correlationId, payload);
        TrackBugReport(correlationId, payload, stored);

        var response = new BugReportResponse
        {
            CorrelationId = correlationId,
            Status = stored ? "received" : "received-no-storage",
        };
        return new OkObjectResult(response);
    }

    // ── Telemetry forwarding ──────────────────────────────────────────────

    private void ForwardEvent(TelemetryEnvelope envelope, TelemetryEvent ev)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["installId"] = envelope.InstallId,
            ["sessionId"] = envelope.SessionId,
            ["appVersion"] = envelope.AppVersion,
            ["os"] = envelope.Os,
            ["kind"] = ev.Kind,
            ["timestampUtc"] = ev.TimestampUtc,
        };
        foreach (KeyValuePair<string, string> p in ev.Properties)
            props[p.Key] = Trim(p.Value, MaxEventMessageChars);

        string eventName = ev.Kind switch
        {
            "error" => "YaguError",
            "performance" => "YaguPerformance",
            _ => "Yagu:" + (string.IsNullOrWhiteSpace(ev.Name) ? "event" : ev.Name),
        };

        _telemetry.TrackEvent(eventName, props, ev.Measurements.Count == 0 ? null : ev.Measurements);

        // Surface numeric measurements as metrics too, so they're queryable/aggregatable.
        foreach (KeyValuePair<string, double> m in ev.Measurements)
        {
            var metric = new MetricTelemetry(ev.Name + ":" + m.Key, m.Value);
            metric.Properties["installId"] = envelope.InstallId;
            metric.Properties["appVersion"] = envelope.AppVersion;
            _telemetry.TrackMetric(metric);
        }
    }

    private void TrackBugReport(string correlationId, BugReportPayload p, bool stored)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["correlationId"] = correlationId,
            ["installId"] = p.InstallId,
            ["appVersion"] = p.AppVersion,
            ["os"] = p.Os,
            ["source"] = p.Source,
            ["exceptionType"] = p.ExceptionType,
            ["message"] = Trim(p.Message, MaxEventMessageChars),
            ["gpu"] = p.Gpu,
            ["npu"] = p.Npu,
            ["email"] = p.Email,
            ["userComment"] = Trim(p.UserComment, MaxEventMessageChars),
            ["attachmentsStored"] = stored ? "true" : "false",
        };
        _telemetry.TrackEvent("YaguBugReport", props);
        _telemetry.Flush();
    }

    // ── Blob upload (Managed Identity) ────────────────────────────────────

    private async Task<bool> TryUploadAttachmentsAsync(string correlationId, BugReportPayload payload)
    {
        if (_blobServiceClient is null)
            return false;

        try
        {
            string containerName = Environment.GetEnvironmentVariable("BUGREPORT_CONTAINER") ?? "bugreports";
            BlobContainerClient container = _blobServiceClient.GetBlobContainerClient(containerName);
            await container.CreateIfNotExistsAsync(PublicAccessType.None);

            await UploadTextAsync(container, $"{correlationId}/settings.json", payload.SettingsJson, "application/json");
            await UploadTextAsync(container, $"{correlationId}/yagu.log", payload.LogTail, "text/plain");

            // Persist the full report (sans the two large blobs already stored) for follow-up context.
            payload.SettingsJson = string.Empty;
            payload.LogTail = string.Empty;
            string reportJson = JsonSerializer.Serialize(payload, JsonOptions);
            await UploadTextAsync(container, $"{correlationId}/report.json", reportJson, "application/json");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to upload bug-report attachments for {CorrelationId}.", correlationId);
            return false;
        }
    }

    private static async Task UploadTextAsync(BlobContainerClient container, string blobName, string content, string contentType)
    {
        if (string.IsNullOrEmpty(content))
            return;
        BlobClient blob = container.GetBlobClient(blobName);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>True when no server token is configured (open proxy) or the request carries a matching
    /// token. Comparison is fixed-time to avoid leaking the token via timing.</summary>
    private static bool IsTokenValid(HttpRequest req)
    {
        string? expected = Environment.GetEnvironmentVariable("YAGU_APP_TOKEN");
        if (string.IsNullOrEmpty(expected))
            return true; // No token configured: accept (matches a client with an empty token).

        string provided = req.Headers["X-Yagu-App-Token"].ToString();
        if (string.IsNullOrEmpty(provided))
            return false;

        byte[] a = Encoding.UTF8.GetBytes(expected);
        byte[] b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task<string> ReadBodyAsync(HttpRequest req, long maxBytes)
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        char[] buffer = new char[8192];
        var sb = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > maxBytes)
                throw new InvalidDataException("Request body exceeds the allowed size.");
        }
        return sb.ToString();
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..max];
    }
}
