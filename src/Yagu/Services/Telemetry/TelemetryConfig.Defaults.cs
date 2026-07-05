namespace Yagu.Services.Telemetry;

/// <summary>
/// Offline-by-default values for <see cref="TelemetryConfig"/>. Compiled into a committed build so it
/// stays fully inert (<see cref="TelemetryConfig.IsConfigured"/> is false) and makes no network call.
/// <para>
/// When a real proxy URL is supplied at build time — via a gitignored <c>Yagu/telemetry.local.props</c>
/// or the <c>YAGU_PROXY_URL</c>/<c>YAGU_APP_TOKEN</c> environment variables — <c>Yagu.csproj</c>
/// (target <c>GenerateYaguTelemetryConfig</c>) swaps this file out for a generated partial that carries
/// the real endpoint + token, so neither is ever committed to source.
/// </para>
/// </summary>
public static partial class TelemetryConfig
{
    /// <summary>Offline placeholder URL (equals <see cref="PlaceholderBaseUrl"/>) — keeps the build inert.</summary>
    public const string ProxyBaseUrl = PlaceholderBaseUrl;

    /// <summary>No app token by default.</summary>
    public const string AppToken = "";
}
