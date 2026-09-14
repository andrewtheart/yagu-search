using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Yagu.McpServer;

/// <summary>
/// Entry point for the Yagu MCP server.
/// </summary>
/// <remarks>
/// <para>
/// In stdio mode <b>stdout is the transport</b>. Anything else written there — a stray
/// <c>Console.WriteLine</c>, a library's progress banner, an unguarded diagnostic — corrupts the frame
/// stream, and the client disconnects with a parse error that points nowhere near the real cause.
/// <see cref="Console.Out"/> is therefore redirected to stderr before anything else runs, which makes
/// every later accidental write harmless. The SDK opens the real stdout handle itself, and
/// <c>Console.SetOut</c> does not affect <see cref="Console.OpenStandardOutput"/>, so the transport is
/// unaffected by the redirect.
/// </para>
/// <para>
/// Lifetime follows stdin: when the client closes the pipe the host shuts down. The server is never
/// terminated by name from elsewhere — a stdio server is parented by VS Code / Visual Studio / Copilot
/// CLI / Claude Desktop, so a name-based sweep would kill a healthy session belonging to another client
/// or another user.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private const string ServerInstructions =
        "Always use yagu_search instead of shell commands or workspace search when the user asks to "
        + "search file contents across an entire Windows drive, across all files under an absolute "
        + "folder, or outside the current workspace. Examples include 'find all instances of the string "
        + "andrew in all files on C:\\', 'search every file under D:\\ for TODO', and 'which files "
        + "contain invoice2024?'. Also use Yagu for explicit requests to find or list files by name, "
        + "path, or extension, especially for broad roots or when the user names Yagu. Prefer normal "
        + "code-navigation tools only for targeted symbol or implementation lookup. For literal searches, "
        + "use yagu_search. Map 'contains', "
        + "'in it', 'inside', 'mentions', 'text', or 'string' to searchMode='content'; map 'named', "
        + "'filename', 'extension', path, or glob requests to searchMode='filenames'. Set wholeWord=false "
        + "for substring or string requests. 'All instances' or 'all occurrences' asks for every match, "
        + "so do not reduce maxMatchesPerFile. For 'which files contain', 'files containing', or 'all files "
        + "with' requests, set maxMatchesPerFile=1 so one file cannot consume the bounded result page. "
        + "Every filesystem tool "
        + "requires an explicit absolute root within the configured allowed roots. Results are bounded, "
        + "so never promise a complete machine-wide list when the response says it was truncated.";

    private static async Task<int> Main(string[] args)
    {
        var stderrWriter = new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true };
        Console.SetOut(stderrWriter);
        Console.SetError(stderrWriter);

        try
        {
            YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(args);
            if (options.Failure is not null)
            {
                await Console.Error.WriteLineAsync(options.Failure).ConfigureAwait(false);
                return 2;
            }

            return options.Transport == McpTransport.Http
                ? await RunHttpAsync(options).ConfigureAwait(false)
                : await RunStdioAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"yagu-mcp: fatal: {ex}").ConfigureAwait(false);
            return 70;
        }
    }

    private static async Task<int> RunStdioAsync(YaguMcpServerOptions options)
    {
        var builder = Host.CreateApplicationBuilder();

        ConfigureLogging(builder.Logging, options);
        ConfigureSharedServices(builder.Services, options);

        builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = "yagu",
                    Version = YaguMcpServerOptions.ServerVersion,
                };
                server.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Loopback HTTP, authenticated by the per-user bearer token.
    /// </summary>
    /// <remarks>
    /// Unlike stdio, this endpoint outlives any one client and is reachable by anything running on the
    /// machine that can open a socket, so three controls apply before a request reaches a tool: the
    /// listener is bound to loopback on both IP stacks, every request must present the token (compared in
    /// constant time), and a request carrying a browser <c>Origin</c> must come from a loopback page.
    /// </remarks>
    private static async Task<int> RunHttpAsync(YaguMcpServerOptions options)
    {
        string token = options.HttpToken!;

        var builder = WebApplication.CreateSlimBuilder();

        ConfigureLogging(builder.Logging, options);
        ConfigureSharedServices(builder.Services, options);

        builder.WebHost.ConfigureKestrel(kestrel => HttpTransportSecurity.ListenOnLoopbackOnly(kestrel, options.HttpPort));

        builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = "yagu",
                    Version = YaguMcpServerOptions.ServerVersion,
                };
                server.ServerInstructions = ServerInstructions;
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!HttpTransportSecurity.IsHostAllowed(context.Request.Host.Value))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("forbidden host").ConfigureAwait(false);
                return;
            }

            if (!HttpTransportSecurity.IsOriginAllowed(context.Request.Headers.Origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("forbidden origin").ConfigureAwait(false);
                return;
            }

            if (!HttpTransportSecurity.IsAuthorized(context.Request, token))
            {
                // WWW-Authenticate tells a well-behaved client how to retry; the body never echoes the
                // presented value, so a token cannot leak into a log through an error message.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsync("unauthorized").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        app.MapMcp();

        await Console.Error.WriteLineAsync(
            $"yagu-mcp: listening on http://127.0.0.1:{options.HttpPort} (loopback only, bearer token required).")
            .ConfigureAwait(false);

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ConfigureLogging(ILoggingBuilder logging, YaguMcpServerOptions options)
    {
        logging.ClearProviders();
        logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
    }

    private static void ConfigureSharedServices(IServiceCollection services, YaguMcpServerOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(options.AuthorizationPolicy);
        // Factory-registered so the container owns disposal, and so the configured ceilings actually
        // reach the constructors instead of the parameter defaults.
        services.AddSingleton(_ => new YaguCliBridge(
            options.YaguExePath, options.MaxConcurrentCalls, modelGateTimeout: options.ModelTimeout));
        services.AddSingleton(_ => new BridgeJobRegistry(
            options.MaxRetainedJobs, options.MaxRunningJobs, options.MaxJobResultChars));
    }
}
