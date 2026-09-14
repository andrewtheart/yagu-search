using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Net;
using System.Net.Sockets;
using Yagu.McpServer;
using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// The HTTP transport's authentication and anti-browser controls. These are the checks standing between
/// the loopback endpoint and anything that is not the client the user configured, so they are tested
/// directly rather than only through a running server.
/// </summary>
public class McpHttpTransportSecurityTests
{
    [Theory]
    [InlineData("Bearer expected", true)]
    [InlineData("bearer expected", true)]
    [InlineData("bEaReR expected", true)]
    [InlineData("Bearer   expected  ", true)]
    [InlineData("Basic expected", false)]
    [InlineData("Bearer wrong", false)]
    [InlineData("Bearer Expected", false)]
    [InlineData("Bearer ", false)]
    [InlineData("Bearerexpected", false)]
    [InlineData("", false)]
    public void IsAuthorized_ValidatesBearerSchemeAndToken(string header, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        Assert.Equal(expected, HttpTransportSecurity.IsAuthorized(context.Request, "expected"));
    }

    [Fact]
    public void IsAuthorized_DoesNotAcceptCredentialsFromQueryString()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?access_token=expected&Authorization=Bearer%20expected");

        Assert.False(HttpTransportSecurity.IsAuthorized(context.Request, "expected"));
    }

    [Fact]
    public void IsAuthorized_MultipleValues_RequiresOneValidBearerToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = new StringValues([null, "Basic expected", "Bearer wrong"]);
        Assert.False(HttpTransportSecurity.IsAuthorized(context.Request, "expected"));

        context.Request.Headers.Authorization = new StringValues([null, "Bearer wrong", "bearer expected"]);
        Assert.True(HttpTransportSecurity.IsAuthorized(context.Request, "expected"));
    }

    [Fact]
    public void HttpGuards_RejectNullCollaborators()
    {
        Assert.Throws<ArgumentNullException>(() => HttpTransportSecurity.IsAuthorized(null!, "expected"));
        Assert.Throws<ArgumentNullException>(() => HttpTransportSecurity.ListenOnLoopbackOnly(null!, 0));
    }

    [Fact]
    public async Task ListenOnLoopbackOnly_BindsAvailableIpStacksAndServesRequests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AllowAlternateSchemes = true;
            HttpTransportSecurity.ListenOnLoopbackOnly(options, 0);
            Assert.False(options.AllowAlternateSchemes);
        });
        await using var application = builder.Build();
        application.MapGet("/", () => "loopback-test");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await application.StartAsync(deadline.Token);
        try
        {
            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.Equal(Socket.OSSupportsIPv6 ? 2 : 1, addresses.Count);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            foreach (string address in addresses)
            {
                var uri = new Uri(address);
                Assert.True(IPAddress.IsLoopback(IPAddress.Parse(uri.Host.Trim('[', ']'))));
                Assert.Equal("loopback-test", await client.GetStringAsync(uri, deadline.Token));
            }
        }
        finally
        {
            await application.StopAsync(CancellationToken.None);
        }
    }

    // ── Origin ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentOrigin_IsAllowed_BecauseNativeClientsSendNone(string? origin)
        => Assert.True(HttpTransportSecurity.IsOriginAllowed(origin));

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.5.6.7")]
    [InlineData("https://localhost:443")]
    [InlineData("http://[::1]:9000")]
    public void LoopbackOrigins_AreAllowed(string origin)
        => Assert.True(HttpTransportSecurity.IsOriginAllowed(origin));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://192.168.1.10")]
    [InlineData("http://10.0.0.5:8377")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://notlocalhost")]
    public void ForeignOrigins_AreRejected(string origin)
        => Assert.False(HttpTransportSecurity.IsOriginAllowed(origin));

    // ── Host ──

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8377")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:8377")]
    [InlineData("127.5.6.7:80")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8377")]
    [InlineData("LOCALHOST:8377")]
    public void LoopbackHosts_AreAllowed(string host)
        => Assert.True(HttpTransportSecurity.IsHostAllowed(host));

    [Theory]
    // An absent Origin is allowed so native clients work, so a rebound name that resolves to 127.0.0.1
    // gets past that check whenever the attacker can suppress the header. The Host is what still names
    // the attacker's domain.
    [InlineData("rebind.evil.com")]
    [InlineData("rebind.evil.com:8377")]
    [InlineData("localhost.evil.com")]
    [InlineData("192.168.1.10:8377")]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1/../evil")]
    [InlineData("evil.com@127.0.0.1")]
    [InlineData("localhost:invalid")]
    [InlineData("localhost:65536")]
    [InlineData("[::1")]
    [InlineData(":8377")]
    [InlineData("localhost?query")]
    [InlineData("localhost#fragment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForeignHosts_AreRejected(string? host)
        => Assert.False(HttpTransportSecurity.IsHostAllowed(host));

    [Fact]
    public void NullOrigin_IsRejected()
    {
        // A sandboxed iframe or a file:// page sends the literal "null" — the context an attacker can
        // most easily arrange, so it must not be mistaken for "no origin".
        Assert.False(McpLoopbackOrigin.IsAllowed("null"));
        Assert.False(McpLoopbackOrigin.IsAllowed("NULL"));
    }

    [Theory]
    [InlineData("file:///C:/evil.html")]
    [InlineData("chrome-extension://abcdef")]
    [InlineData("ws://localhost:3000")]
    [InlineData("not a uri")]
    [InlineData("//localhost")]
    public void NonHttpSchemesAndGarbage_AreRejected(string origin)
        => Assert.False(McpLoopbackOrigin.IsAllowed(origin));

    // ── Token ──

    [Fact]
    public void Token_MatchesItself()
    {
        Assert.True(McpTokenStore.IsValid("s3cret-token", "s3cret-token"));
    }

    [Theory]
    [InlineData("s3cret-token", "s3cret-toke")]   // prefix
    [InlineData("s3cret-token", "s3cret-tokenX")] // extension
    [InlineData("s3cret-token", "S3CRET-TOKEN")]  // case
    [InlineData("s3cret-token", "totally-different")]
    public void Token_RejectsAnythingButAnExactMatch(string expected, string presented)
        => Assert.False(McpTokenStore.IsValid(presented, expected));

    [Theory]
    [InlineData(null, "expected")]
    [InlineData("", "expected")]
    [InlineData("presented", null)]
    [InlineData("presented", "")]
    [InlineData(null, null)]
    public void Token_RejectsMissingValues(string? presented, string? expected)
        => Assert.False(McpTokenStore.IsValid(presented, expected));

    [Fact]
    public void Token_ComparesHashesSoLengthIsNotRevealed()
    {
        // FixedTimeEquals short-circuits on a length mismatch, so comparing raw bytes would leak the
        // token's length. Hashing first makes both operands 32 bytes; this asserts the observable
        // consequence — a one-character token and a long one are both simply rejected.
        Assert.False(McpTokenStore.IsValid("a", new string('x', 512)));
        Assert.False(McpTokenStore.IsValid(new string('x', 512), "a"));
    }

    // ── Token storage ──

    [Fact]
    public void GeneratedTokens_AreLongRandomAndUrlSafe()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 32; i++)
        {
            string token = InvokeGenerate();

            // 256 bits of entropy, base64url with no padding.
            Assert.Equal(43, token.Length);
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
            Assert.True(tokens.Add(token), "a generated token repeated, so it is not random.");
        }
    }

    /// <summary>Reaches the private generator the same way <c>Regenerate</c> does, without touching the user's real token file.</summary>
    private static string InvokeGenerate()
    {
        var method = typeof(McpTokenStore).GetMethod("GenerateToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }
}
