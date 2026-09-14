namespace Yagu.Services.Mcp;

/// <summary>
/// Decides whether a browser <c>Origin</c> may talk to the loopback MCP endpoint.
/// </summary>
/// <remarks>
/// Pure, and kept out of the ASP.NET-facing code on purpose: this is the check that stands between a
/// random web page the user visits and their local search index, so it is unit-tested directly rather
/// than only through a running server.
/// </remarks>
internal static class McpLoopbackOrigin
{
    /// <summary>
    /// True when the request may proceed.
    /// </summary>
    /// <remarks>
    /// A native MCP client sends no <c>Origin</c> at all, so an absent header is allowed — requiring one
    /// would break every legitimate client. When a header <i>is</i> present the request came from a web
    /// page, and only a loopback page may reach a loopback server. This is what stops a site the user is
    /// browsing from driving their local index, and what makes a DNS-rebinding attack fail even when the
    /// attacker controls a hostname that resolves to 127.0.0.1.
    /// </remarks>
    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return true;

        // A sandboxed iframe or a file:// page sends the literal "null". It is not loopback, and it is
        // exactly the context an attacker can most easily arrange.
        if (string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        if (uri.IsLoopback)
            return true;

        // Uri.IsLoopback covers "localhost", 127.0.0.0/8 and ::1, but not every literal that maps onto
        // loopback, so parse the host as an address as well.
        string host = uri.Host.Trim('[', ']');
        return System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address)
            && System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// True when a request's <c>Host</c> header names loopback.
    /// </summary>
    /// <remarks>
    /// The other half of DNS-rebinding defence. <see cref="IsAllowed"/> deliberately permits an absent
    /// <c>Origin</c> so native clients work, which means a rebound name resolving to 127.0.0.1 gets past
    /// the origin check when the attacker can suppress the header. The bearer token is still required, so
    /// this is defence in depth rather than the only barrier.
    /// </remarks>
    public static bool IsHostAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        string value = host.Trim();

        // A Host header is a bare authority. Anything carrying a path, userinfo, query or fragment is
        // not one, and must not be normalized into looking like one.
        if (value.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            return false;

        if (!Uri.TryCreate("http://" + value, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
            return false;

        if (uri.IsLoopback)
            return true;

        string name = uri.Host.Trim('[', ']');
        return System.Net.IPAddress.TryParse(name, out System.Net.IPAddress? address)
            && System.Net.IPAddress.IsLoopback(address);
    }
}
