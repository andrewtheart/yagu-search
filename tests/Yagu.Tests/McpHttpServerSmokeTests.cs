using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Drives the real server's loopback HTTP transport and checks the three controls that stand between it
/// and anything that is not the configured client.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>fast and untagged</b> so it runs in CI, which filters
/// <c>Category!=Slow&amp;Category!=GPU</c>. These are security properties: hiding them behind a tag would
/// let authentication, the loopback binding, or the anti-browser check regress with no required check
/// standing in the way. The unit tests in <c>McpHttpTransportSecurityTests</c> cover the decision logic;
/// only a running server can show that the decisions are actually wired into the pipeline.
/// </para>
/// <para>
/// The token is whatever the server itself creates on startup. The test never regenerates it, so a
/// developer's existing token is not disturbed.
/// </para>
/// </remarks>
public sealed class McpHttpServerSmokeTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task LoopbackHttp_RefusesTheUnauthenticated_AndServesTheAuthenticated()
    {
        using var server = HttpServer.Start();
        Assert.True(await server.WaitUntilListeningAsync(StartTimeout), server.Diagnostics());

        string token = McpTokenStore.TryReadToken()
            ?? throw new InvalidOperationException("the server did not create a token file.");

        // No credential at all.
        Assert.Equal(HttpStatusCode.Unauthorized, await server.InitializeAsync(auth: null, origin: null));

        // A wrong credential, and a correct-prefix credential: neither may be accepted.
        Assert.Equal(HttpStatusCode.Unauthorized, await server.InitializeAsync($"Bearer {token}x", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await server.InitializeAsync($"Bearer {token[..^4]}", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await server.InitializeAsync("Basic abcdef", null));

        // A page on the web must not be able to drive a local index, even holding a valid token.
        Assert.Equal(HttpStatusCode.Forbidden, await server.InitializeAsync($"Bearer {token}", "https://evil.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, await server.InitializeAsync($"Bearer {token}", "null"));

        // A loopback page, and a native client that sends no Origin at all, are both fine.
        Assert.Equal(HttpStatusCode.OK, await server.InitializeAsync($"Bearer {token}", "http://localhost:3000"));
        Assert.Equal(HttpStatusCode.OK, await server.InitializeAsync($"Bearer {token}", null));
    }

    [Fact]
    public async Task LoopbackHttp_BindsOnlyToLoopback_OnBothIpStacks()
    {
        using var server = HttpServer.Start();
        Assert.True(await server.WaitUntilListeningAsync(StartTimeout), server.Diagnostics());

        IPAddress[] bound = IPGlobalPropertiesListeners(server.Port);

        // Without this the Assert.All below would pass vacuously on an empty list, which is exactly how
        // a "loopback only" check quietly stops checking anything.
        Assert.NotEmpty(bound);

        Assert.All(bound, address => Assert.True(
            IPAddress.IsLoopback(address),
            $"the server is listening on {address}, which is reachable from the network."));

        // localhost resolves to ::1 before 127.0.0.1 on Windows, so an IPv4-only bind would look fine
        // here and then refuse every real client that uses the hostname.
        string token = McpTokenStore.TryReadToken()!;
        Assert.Equal(HttpStatusCode.OK, await server.InitializeAsync($"Bearer {token}", null, useHostname: true));
    }

    [Fact]
    public void ServerRefusesToStartWithAnHttpTokenItCannotHave()
    {
        // A token supplied on the command line would be readable by every process on the machine, so it
        // is rejected outright rather than silently ignored.
        using var server = HttpServer.Start(extraArgs: ["--token", "hunter2"]);

        Assert.True(server.WaitForExit(TimeSpan.FromSeconds(30)), "the server kept running despite --token.");
        Assert.NotEqual(0, server.ExitCode);
        Assert.Contains("--token is not supported", server.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress[] IPGlobalPropertiesListeners(int port) =>
        [.. System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == port)
            .Select(endpoint => endpoint.Address)];

    /// <summary>A live HTTP server process on a port nothing else is using.</summary>
    private sealed class HttpServer : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

        private HttpServer(Process process, int port)
        {
            _process = process;
            Port = port;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public int Port { get; }

        public string StandardError => _stderr.IsCompleted ? _stderr.Result : string.Empty;

        public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

        public static HttpServer Start(IReadOnlyList<string>? extraArgs = null)
        {
            string repoRoot = FindRepoRoot();
            string configuration = ResolveConfiguration();
            string serverDll = Path.Combine(
                repoRoot, "src", "Yagu.McpServer", "bin", configuration, "net10.0", "Yagu.McpServer.dll");

            Assert.True(File.Exists(serverDll), $"The MCP server was not built at '{serverDll}'.");

            int port = FreePort();

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            psi.ArgumentList.Add(serverDll);
            psi.ArgumentList.Add("--yagu-exe");
            psi.ArgumentList.Add(serverDll);
            psi.ArgumentList.Add("--allowed-root");
            psi.ArgumentList.Add(repoRoot);
            psi.ArgumentList.Add("--capability");
            psi.ArgumentList.Add("Read");
            psi.ArgumentList.Add("--transport");
            psi.ArgumentList.Add("http");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            foreach (string argument in extraArgs ?? [])
                psi.ArgumentList.Add(argument);

            Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("could not start the MCP server.");

            return new HttpServer(process, port);
        }

        public async Task<bool> WaitUntilListeningAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                    return false;

                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, Port);
                    return true;
                }
                catch (SocketException)
                {
                    await Task.Delay(150);
                }
            }

            return false;
        }

        public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit((int)timeout.TotalMilliseconds);

        public async Task<HttpStatusCode> InitializeAsync(string? auth, string? origin, bool useHostname = false)
        {
            string host = useHostname ? "localhost" : "127.0.0.1";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{host}:{Port}/")
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}""",
                    Encoding.UTF8,
                    "application/json"),
            };

            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            if (auth is not null)
                request.Headers.TryAddWithoutValidation("Authorization", auth);
            if (origin is not null)
                request.Headers.TryAddWithoutValidation("Origin", origin);

            using HttpResponseMessage response = await _client.SendAsync(request);
            return response.StatusCode;
        }

        public string Diagnostics() =>
            _process.HasExited
                ? $"the server exited with {_process.ExitCode}: {StandardError}"
                : "the server never started listening.";

        /// <summary>Asks the OS for an unused port so parallel or CI runs cannot collide.</summary>
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string ResolveConfiguration()
            => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best effort.
            }

            _client.Dispose();
            _process.Dispose();
        }
    }
}
