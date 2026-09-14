using System.Net;
using Yagu.Services.Mcp;

namespace Yagu.McpServer;

/// <summary>
/// The three checks that stand between the loopback HTTP endpoint and anything that is not the client
/// the user configured: where it binds, who it accepts, and which pages may talk to it.
/// </summary>
internal static class HttpTransportSecurity
{
    /// <summary>Header carrying the bearer token. Never logged, and never accepted from the query string.</summary>
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Binds Kestrel to loopback on <b>both</b> IP stacks.
    /// </summary>
    /// <remarks>
    /// Clients routinely use <c>http://localhost:port</c>, and on Windows <c>localhost</c> resolves to
    /// <c>::1</c> before <c>127.0.0.1</c>. Binding only IPv4 produces a server that works from a literal
    /// IP and mysteriously refuses connections from <c>localhost</c>. Binding <see cref="IPAddress.Any"/>
    /// would expose the endpoint to the network, which this must never do.
    /// IPv6 support is tested BEFORE the endpoint is registered: <see cref="KestrelServerOptions.Listen"/>
    /// only records it, and the socket is not bound until the host starts, so a try/catch here would not
    /// see the failure and an IPv6-disabled machine would fail to start the whole HTTP server.
    /// </remarks>
    public static void ListenOnLoopbackOnly(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions kestrel, int port)
    {
        ArgumentNullException.ThrowIfNull(kestrel);

        kestrel.Listen(IPAddress.Loopback, port);

        if (System.Net.Sockets.Socket.OSSupportsIPv6)
            kestrel.Listen(IPAddress.IPv6Loopback, port);

        // Nothing else may be added later by configuration.
        kestrel.AllowAlternateSchemes = false;
    }

    /// <summary>
    /// True when a request carries the expected bearer token.
    /// </summary>
    public static bool IsAuthorized(Microsoft.AspNetCore.Http.HttpRequest request, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(AuthorizationHeader, out var values))
            return false;

        foreach (string? value in values)
        {
            // RFC 7235 defines auth-scheme as case-insensitive, so a client sending "bearer <token>"
            // would otherwise get a bare 401 with nothing to explain why.
            if (value is null || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (McpTokenStore.IsValid(value[BearerPrefix.Length..].Trim(), expectedToken))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the request's <c>Host</c> names loopback. Delegates to the shared, unit-tested
    /// <see cref="McpLoopbackOrigin"/> so the running server and the tests cannot disagree.
    /// </summary>
    public static bool IsHostAllowed(string? host) => McpLoopbackOrigin.IsHostAllowed(host);

    /// <summary>
    /// True when the request's <c>Origin</c> is acceptable. Delegates to the shared, unit-tested
    /// <see cref="McpLoopbackOrigin"/> so the running server and the tests cannot disagree.
    /// </summary>
    public static bool IsOriginAllowed(string? origin) => McpLoopbackOrigin.IsAllowed(origin);
}
