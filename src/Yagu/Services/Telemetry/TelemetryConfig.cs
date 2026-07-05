namespace Yagu.Services.Telemetry;

/// <summary>
/// Compile-time endpoint configuration for the optional, consent-gated telemetry and bug-reporting
/// features. Yagu never talks to Application Insights directly: it POSTs to a dedicated proxy Azure
/// Function that holds the real Application Insights connection string and storage credentials
/// server-side (see <c>cloud/Yagu.TelemetryFunction</c>). The client therefore only needs the
/// public Function base URL and a rotatable app token — neither of which can read or manage any
/// telemetry data.
/// <para>
/// The real <see cref="ProxyBaseUrl"/> and <see cref="AppToken"/> are NEVER committed: the
/// offline-by-default placeholders live in <c>TelemetryConfig.Defaults.cs</c>, and a configured
/// build (a gitignored <c>Yagu/telemetry.local.props</c> or the <c>YAGU_PROXY_URL</c>/
/// <c>YAGU_APP_TOKEN</c> environment variables) swaps in a generated partial with the real values
/// (see <c>Yagu.csproj</c>, target <c>GenerateYaguTelemetryConfig</c>). With neither present the
/// build is inert (<see cref="IsConfigured"/> is false) and makes no network call.
/// </para>
/// </summary>
public static partial class TelemetryConfig
{
    /// <summary>Sentinel used by the offline default. Detected by <see cref="IsConfigured"/> so an
    /// unconfigured build stays completely offline.</summary>
    internal const string PlaceholderBaseUrl = "https://REPLACE-WITH-YOUR-FUNCTION.azurewebsites.net";

    // ProxyBaseUrl and AppToken are declared in a partial companion so this logic file never carries
    // the real endpoint or token: TelemetryConfig.Defaults.cs (committed) holds the offline
    // placeholders, and a build-time generated partial replaces it when configured.

    /// <summary>Relative path of the silent telemetry ingestion endpoint on the proxy Function.</summary>
    public const string TelemetryPath = "/api/telemetry";

    /// <summary>Relative path of the (explicit, user-reviewed) bug-report endpoint on the proxy Function.</summary>
    public const string BugReportPath = "/api/bugreport";

    /// <summary>True only when a real proxy URL has been configured. When false, all telemetry and
    /// bug-report senders are hard no-ops so the app makes no network calls.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProxyBaseUrl)
        && !string.Equals(ProxyBaseUrl, PlaceholderBaseUrl, StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(ProxyBaseUrl, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Absolute telemetry endpoint, or null when unconfigured.</summary>
    public static Uri? TelemetryEndpoint => IsConfigured ? new Uri(ProxyBaseUrl.TrimEnd('/') + TelemetryPath) : null;

    /// <summary>Absolute bug-report endpoint, or null when unconfigured.</summary>
    public static Uri? BugReportEndpoint => IsConfigured ? new Uri(ProxyBaseUrl.TrimEnd('/') + BugReportPath) : null;
}
