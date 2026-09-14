using Yagu.Services.Mcp;

namespace Yagu.McpServer;

/// <summary>How a client reaches this server.</summary>
internal enum McpTransport
{
    /// <summary>The client launches the server and owns its lifetime. No port, no token.</summary>
    Stdio,

    /// <summary>Loopback HTTP, authenticated with the per-user bearer token.</summary>
    Http,
}

/// <summary>Resolved startup configuration for the server process.</summary>
internal sealed class YaguMcpServerOptions
{
    public const string ServerVersion = "1.0.0";

    /// <summary>The loopback port used when none is configured.</summary>
    public const int DefaultHttpPort = 8377;

    /// <summary>Ceiling for tools that collect every match in memory before writing.</summary>
    public const int DefaultMaxExportResults = 50_000;

    /// <summary>Defaults for the background-job machinery, matching the shipped settings defaults.</summary>
    public const int DefaultMaxRetainedJobs = 64;
    public const int DefaultMaxRunningJobs = 8;
    public const int DefaultMaxJobResultChars = 256 * 1024;

    public required string YaguExePath { get; init; }
    public required McpAuthorizationPolicy AuthorizationPolicy { get; init; }
    public int MaxConcurrentCalls { get; init; } = 4;
    public int MaxExportResults { get; init; } = DefaultMaxExportResults;
    public int MaxRetainedJobs { get; init; } = DefaultMaxRetainedJobs;
    public int MaxRunningJobs { get; init; } = DefaultMaxRunningJobs;
    public int MaxJobResultChars { get; init; } = DefaultMaxJobResultChars;
    public TimeSpan DefaultCallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Ceiling for index build/compact/validate, which can legitimately run for hours.</summary>
    public TimeSpan MaintenanceTimeout { get; init; } = TimeSpan.FromHours(4);

    /// <summary>Ceiling for a model-backed call, and for how long one may queue behind another.</summary>
    public TimeSpan ModelTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public bool Verbose { get; init; }
    public string? Failure { get; init; }

    public McpTransport Transport { get; init; } = McpTransport.Stdio;
    public int HttpPort { get; init; } = DefaultHttpPort;

    /// <summary>
    /// The bearer token for <see cref="McpTransport.Http"/>, read from
    /// <see cref="McpTokenStore"/> — never from the command line, which every process can read.
    /// </summary>
    public string? HttpToken { get; init; }

    public static YaguMcpServerOptions Invalid(string failure) => new()
    {
        YaguExePath = string.Empty,
        AuthorizationPolicy = new McpAuthorizationPolicy([], McpCapability.None),
        Failure = failure,
    };
}

/// <summary>
/// Parses the server's own command line.
/// </summary>
/// <remarks>
/// Allowed roots and capabilities are supplied by whoever launches the server (the Settings UI writes
/// them into the client's MCP config). There is no default root: a server started without one
/// authorizes nothing, which is the safe failure.
/// </remarks>
internal static class YaguMcpServerOptionsResolver
{
    public static YaguMcpServerOptions Resolve(string[] args, Func<string?>? ensureHttpToken = null)
    {
        string? exePath = null;
        var roots = new List<string>();
        McpCapability capabilities = McpCapability.None;
        int maxConcurrent = 4;
        int maxExportResults = YaguMcpServerOptions.DefaultMaxExportResults;
        int maxRetainedJobs = YaguMcpServerOptions.DefaultMaxRetainedJobs;
        int maxRunningJobs = YaguMcpServerOptions.DefaultMaxRunningJobs;
        int maxJobResultChars = YaguMcpServerOptions.DefaultMaxJobResultChars;
        int callTimeoutSeconds = 120;
        int maintenanceTimeoutMinutes = 240;
        int modelTimeoutMinutes = 30;
        bool verbose = false;
        var transport = McpTransport.Stdio;
        int port = YaguMcpServerOptions.DefaultHttpPort;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--yagu-exe" when i + 1 < args.Length:
                    exePath = args[++i];
                    break;
                case "--allowed-root" when i + 1 < args.Length:
                    roots.Add(args[++i]);
                    break;
                case "--capability" when i + 1 < args.Length:
                    if (!Enum.TryParse(args[++i], ignoreCase: true, out McpCapability parsed))
                        return YaguMcpServerOptions.Invalid($"yagu-mcp: unknown capability '{args[i]}'.");
                    capabilities |= parsed;
                    break;
                case "--max-concurrent" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxConcurrent) || maxConcurrent < 1)
                        return YaguMcpServerOptions.Invalid($"yagu-mcp: --max-concurrent must be a positive integer.");
                    break;
                case "--max-export-results" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxExportResults) || maxExportResults < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --max-export-results must be a positive integer.");
                    break;
                case "--max-retained-jobs" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxRetainedJobs) || maxRetainedJobs < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --max-retained-jobs must be a positive integer.");
                    break;
                case "--max-running-jobs" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxRunningJobs) || maxRunningJobs < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --max-running-jobs must be a positive integer.");
                    break;
                case "--max-job-result-chars" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxJobResultChars) || maxJobResultChars < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --max-job-result-chars must be a positive integer.");
                    break;
                case "--call-timeout-seconds" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out callTimeoutSeconds) || callTimeoutSeconds < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --call-timeout-seconds must be a positive integer.");
                    break;
                case "--maintenance-timeout-minutes" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maintenanceTimeoutMinutes) || maintenanceTimeoutMinutes < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --maintenance-timeout-minutes must be a positive integer.");
                    break;
                case "--model-timeout-minutes" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out modelTimeoutMinutes) || modelTimeoutMinutes < 1)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --model-timeout-minutes must be a positive integer.");
                    break;
                case "--transport" when i + 1 < args.Length:
                    if (!TryParseTransport(args[++i], out transport))
                        return YaguMcpServerOptions.Invalid($"yagu-mcp: unknown transport '{args[i]}'. Expected 'stdio' or 'http'.");
                    break;
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out port) || port is < 1 or > 65535)
                        return YaguMcpServerOptions.Invalid("yagu-mcp: --port must be between 1 and 65535.");
                    break;
                case "--token":
                    // Accepting a token here would publish it: a command line is readable by every
                    // process on the machine. Fail loudly rather than silently ignoring it, so nobody
                    // believes a token was applied.
                    return YaguMcpServerOptions.Invalid(
                        "yagu-mcp: --token is not supported. The token is read from the per-user token file, "
                        + "because a command line is readable by any process on this machine.");
                case "--verbose":
                    verbose = true;
                    break;
            }
        }

        exePath ??= ResolveSiblingYaguExe();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return YaguMcpServerOptions.Invalid($"yagu-mcp: Yagu.exe was not found (looked for '{exePath}'). Pass --yagu-exe <path>.");

        if (roots.Count == 0)
            return YaguMcpServerOptions.Invalid("yagu-mcp: at least one --allowed-root is required; a server with no root authorizes nothing.");

        var policy = new McpAuthorizationPolicy(roots, capabilities);
        if (policy.AllowedRoots.Count == 0)
            return YaguMcpServerOptions.Invalid("yagu-mcp: no --allowed-root survived validation; roots must be absolute local paths.");

        string? token = null;
        if (transport == McpTransport.Http)
        {
            token = (ensureHttpToken ?? McpTokenStore.EnsureToken)();
            if (string.IsNullOrEmpty(token))
            {
                return YaguMcpServerOptions.Invalid(
                    $"yagu-mcp: the HTTP transport needs a token, but '{McpTokenStore.TokenPath}' could not be read or created. "
                    + "An unauthenticated loopback endpoint will not be started.");
            }
        }

        return new YaguMcpServerOptions
        {
            YaguExePath = Path.GetFullPath(exePath),
            AuthorizationPolicy = policy,
            MaxConcurrentCalls = maxConcurrent,
            MaxExportResults = maxExportResults,
            MaxRetainedJobs = maxRetainedJobs,
            MaxRunningJobs = maxRunningJobs,
            MaxJobResultChars = maxJobResultChars,
            DefaultCallTimeout = TimeSpan.FromSeconds(callTimeoutSeconds),
            MaintenanceTimeout = TimeSpan.FromMinutes(maintenanceTimeoutMinutes),
            ModelTimeout = TimeSpan.FromMinutes(modelTimeoutMinutes),
            Verbose = verbose,
            Transport = transport,
            HttpPort = port,
            HttpToken = token,
        };
    }

    public static bool TryParseTransport(string? value, out McpTransport transport)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stdio" or "stdin" or "std":
                transport = McpTransport.Stdio;
                return true;
            case "http" or "https" or "loopback":
                transport = McpTransport.Http;
                return true;
            default:
                transport = McpTransport.Stdio;
                return false;
        }
    }

    /// <summary>The server is staged in &lt;app&gt;\mcp-server\, so Yagu.exe is one directory up.</summary>
    private static string ResolveSiblingYaguExe()
    {
        string parent = Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        return Path.Combine(parent, "Yagu.exe");
    }
}
