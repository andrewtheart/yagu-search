using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Yagu.Services.Telemetry;

/// <summary>
/// Shared <see cref="HttpClient"/> for the telemetry and bug-report channels, plus a small helper to
/// POST a JSON body to the proxy Function with the optional app token header. A single long-lived
/// client avoids socket exhaustion; the short timeout keeps a flaky network from ever blocking the
/// app (callers run these on background tasks and ignore failures).
/// </summary>
internal static class TelemetryHttp
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Yagu/{AppInfo.Version}");
        return client;
    }

    /// <summary>POSTs <paramref name="json"/> to <paramref name="endpoint"/>. Returns the response
    /// body on success (HTTP 2xx) or null on any failure. Never throws.</summary>
    public static async Task<string?> PostJsonAsync(Uri endpoint, string json, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(TelemetryConfig.AppToken))
                request.Headers.TryAddWithoutValidation("X-Yagu-App-Token", TelemetryConfig.AppToken);

            using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Instance.Verbose("Telemetry", $"POST {endpoint.AbsolutePath} returned {(int)response.StatusCode}.");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Telemetry must never disrupt the app; swallow all transport errors.
            LogService.Instance.Verbose("Telemetry", $"POST {endpoint.AbsolutePath} failed: {ex.Message}");
            return null;
        }
    }
}
