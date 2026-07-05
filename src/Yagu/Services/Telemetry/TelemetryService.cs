using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace Yagu.Services.Telemetry;

/// <summary>
/// The SILENT, consent-gated telemetry channel. When (and only when)
/// <see cref="TelemetryGate.ShouldSendTelemetry"/> is true, it batches anonymized error summaries and
/// performance measurements and POSTs them to the proxy Function on a background timer. Every call is
/// a hard no-op when telemetry is disabled, unconfigured, or headless, so it is always safe to invoke
/// from anywhere (including hot paths and exception handlers).
/// <para>
/// Privacy: error details pass through <see cref="TelemetryScrubber"/> (paths redacted) and nothing
/// here ever carries search queries, file contents, directory names, or machine identifiers — only a
/// random install GUID, coarse app/OS version, scrubbed exception type/message/stack, and timings.
/// </para>
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime singleton; the flush timer is disposed in Shutdown() and the semaphore lives for the app's lifetime by design.")]
public sealed class TelemetryService
{
    public static TelemetryService Instance { get; } = new();

    private const int MaxQueued = 256;
    private const int MaxStackChars = 2048;

    private readonly ConcurrentQueue<TelemetryEvent> _queue = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private Timer? _flushTimer;
    private string _installId = string.Empty;
    private int _started;

    private TelemetryService() { }

    /// <summary>Seeds the install id and starts the periodic flush timer (idempotent). Safe to call
    /// even when telemetry is disabled — sending still gates on <see cref="TelemetryGate"/> at flush
    /// time, so a later opt-in starts working without a restart.</summary>
    public void Initialize(string installId)
    {
        _installId = installId ?? string.Empty;
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _flushTimer = new Timer(static s => _ = ((TelemetryService)s!).FlushAsync(), this, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20));
    }

    /// <summary>Records a scrubbed error summary. No-op unless telemetry is enabled.</summary>
    public void TrackError(string source, Exception? exception)
    {
        if (!TelemetryGate.ShouldSendTelemetry)
            return;

        TelemetryScrubber.ScrubbedException scrub = TelemetryScrubber.Describe(exception);
        string stack = scrub.StackTrace;
        if (stack.Length > MaxStackChars)
            stack = stack[..MaxStackChars];

        var ev = new TelemetryEvent
        {
            Kind = "error",
            Name = source,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Properties =
            {
                ["source"] = source,
                ["exceptionType"] = scrub.Type,
                ["message"] = scrub.Message,
                ["stack"] = stack,
            },
        };
        Enqueue(ev);
    }

    /// <summary>Records a performance measurement (e.g. startup or search duration). No-op unless
    /// telemetry is enabled. Callers must pass only non-identifying property values.</summary>
    public void TrackPerformance(string name, double durationMs, IReadOnlyDictionary<string, double>? extraMeasurements = null, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (!TelemetryGate.ShouldSendTelemetry)
            return;

        var ev = new TelemetryEvent
        {
            Kind = "perf",
            Name = name,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Measurements = { ["durationMs"] = durationMs },
        };
        if (extraMeasurements is not null)
            foreach (var kv in extraMeasurements)
                ev.Measurements[kv.Key] = kv.Value;
        if (properties is not null)
            foreach (var kv in properties)
                ev.Properties[kv.Key] = TelemetryScrubber.Scrub(kv.Value);
        Enqueue(ev);
    }

    private void Enqueue(TelemetryEvent ev)
    {
        if (_queue.Count >= MaxQueued)
            return; // Drop rather than grow unbounded during an error storm.
        _queue.Enqueue(ev);
    }

    /// <summary>Posts any queued events as a single envelope. Safe to call concurrently; serialized
    /// internally. Returns immediately when nothing is queued or telemetry is off.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!TelemetryGate.ShouldSendTelemetry || _queue.IsEmpty)
            return;
        if (TelemetryConfig.TelemetryEndpoint is not { } endpoint)
            return;

        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new List<TelemetryEvent>();
            while (batch.Count < MaxQueued && _queue.TryDequeue(out TelemetryEvent? ev))
                batch.Add(ev);
            if (batch.Count == 0)
                return;

            var envelope = new TelemetryEnvelope
            {
                InstallId = _installId,
                SessionId = _sessionId,
                AppVersion = AppInfo.Version,
                Os = RuntimeInformation.OSDescription,
                Events = batch,
            };

            string json = JsonSerializer.Serialize(envelope, TelemetryJsonContext.Default.TelemetryEnvelope);
            await TelemetryHttp.PostJsonAsync(endpoint, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>Final best-effort flush on shutdown (bounded so it never delays exit noticeably).</summary>
    public void Shutdown()
    {
        try
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            if (TelemetryGate.ShouldSendTelemetry && !_queue.IsEmpty)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                FlushAsync(cts.Token).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Never block or throw during shutdown.
        }
    }
}
