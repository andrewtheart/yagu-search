using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Yagu.Services.Ai;
using System.Diagnostics.CodeAnalysis;

namespace Yagu.Services.Telemetry;

/// <summary>
/// Builds and submits EXPLICIT, user-reviewed bug reports. Unlike the silent telemetry channel, a bug
/// report is only sent after the user sees exactly what it contains (in the bug-report dialog) and
/// clicks Submit. It therefore deliberately includes the full settings file, a log tail, GPU/NPU
/// details, the exception/stack, and an optional contact email — all reviewable before sending.
/// <para>
/// The proxy Function stores the log + settings in blob storage keyed by the correlation id and
/// records a matching Application Insights event, so a report can be correlated to its uploaded
/// artifacts.
/// </para>
/// </summary>
public sealed class BugReportService
{
    public static BugReportService Instance { get; } = new();

    // Cap the log tail so a huge log never bloats the upload; the most recent entries matter most.
    private const int MaxLogTailBytes = 256 * 1024;
    private const int MaxSettingsBytes = 256 * 1024;

    private string _installId = string.Empty;

    private BugReportService() { }

    public void Initialize(string installId) => _installId = installId ?? string.Empty;

    /// <summary>
    /// Gathers everything a bug report would contain so the dialog can show the user exactly what will
    /// be submitted. Reads the settings file and a tail of the log, detects GPU/NPU, and captures the
    /// exception details. Generates a fresh correlation id. Never throws — missing pieces come back
    /// empty.
    /// </summary>
    public BugReportPayload BuildPayload(string source, Exception? exception, string? prefillEmail = null)
    {
        var payload = new BugReportPayload
        {
            InstallId = _installId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            AppVersion = AppInfo.Version,
            Os = SafeOsDescription(),
            Source = source ?? string.Empty,
            Email = (prefillEmail ?? string.Empty).Trim(),
        };

        if (exception is not null)
        {
            payload.ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            payload.Message = exception.Message ?? string.Empty;
            payload.StackTrace = exception.StackTrace ?? string.Empty;
        }

        (payload.Gpu, payload.Npu) = SafeDetectHardware();
        payload.SettingsJson = SafeReadTail(SettingsService.DefaultPath(), MaxSettingsBytes);
        payload.LogTail = SafeReadTail(LogService.DefaultLogPath(), MaxLogTailBytes);

        return payload;
    }

    /// <summary>Submits a (possibly user-edited) bug report. Returns the server response on success or
    /// null on failure. Never throws.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match the singleton's BuildPayload call pattern and allow future per-instance state.")]
    public async Task<BugReportResponse?> SubmitAsync(BugReportPayload payload, CancellationToken cancellationToken = default)
    {
        if (payload is null || TelemetryConfig.BugReportEndpoint is not { } endpoint)
            return null;

        try
        {
            string json = JsonSerializer.Serialize(payload, TelemetryJsonContext.Default.BugReportPayload);
            string? response = await TelemetryHttp.PostJsonAsync(endpoint, json, cancellationToken).ConfigureAwait(false);
            if (response is null)
                return null;

            try
            {
                return JsonSerializer.Deserialize(response, TelemetryJsonContext.Default.BugReportResponse)
                       ?? new BugReportResponse { CorrelationId = payload.CorrelationId, Status = "ok" };
            }
            catch
            {
                // Server accepted but returned a non-JSON body; treat as success using our id.
                return new BugReportResponse { CorrelationId = payload.CorrelationId, Status = "ok" };
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("BugReport", $"Submit failed: {ex.Message}", ex);
            return null;
        }
    }

    private static (string Gpu, string Npu) SafeDetectHardware()
    {
        try
        {
            var detector = new GpuNpuCapabilityDetector();
            string gpu = string.Join("; ", detector.GetGpuDescriptions());
            string npu = string.Join("; ", detector.GetNpuDescriptions());
            return (gpu, npu);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string SafeOsDescription()
    {
        try { return RuntimeInformation.OSDescription; }
        catch { return string.Empty; }
    }

    /// <summary>Reads up to <paramref name="maxBytes"/> from the END of a UTF-8 text file (most recent
    /// content), or the whole file when smaller. Returns empty on any error or when missing.</summary>
    private static string SafeReadTail(string path, int maxBytes)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            // FileShare.Delete lets a concurrent atomic replace (settings save) or log rotation
            // proceed without blocking us; our handle keeps reading a complete snapshot of the file
            // as it was at open time, so we never capture a half-written or truncated tail.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length <= maxBytes)
                return ReadAllText(fs);

            fs.Seek(-maxBytes, SeekOrigin.End);
            string tail = ReadAllText(fs);
            // Drop a partial first line so the tail starts cleanly.
            int firstNewline = tail.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline < tail.Length - 1)
                tail = "...(truncated)\n" + tail[(firstNewline + 1)..];
            return tail;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadAllText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
