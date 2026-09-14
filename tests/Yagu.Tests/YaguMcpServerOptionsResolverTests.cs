using Yagu.McpServer;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers the MCP server's own command-line parsing. The command line <b>is</b> the grant — roots and
/// capabilities arrive no other way — so a mis-parse either hands out authority nobody granted or
/// starts a server that silently authorizes nothing.
/// </summary>
public class YaguMcpServerOptionsResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "yagu-mcp-opts-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly string _root;

    public YaguMcpServerOptionsResolverTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "Yagu.exe");
        File.WriteAllText(_exe, "not a real executable, only its existence is checked");
        _root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string[] BaseArgs(params string[] extra) =>
        ["--yagu-exe", _exe, "--allowed-root", _root, .. extra];

    private YaguMcpServerOptions ResolveWith(params string[] extra) =>
        YaguMcpServerOptionsResolver.Resolve(BaseArgs(extra));

    // ---- the minimum viable server ------------------------------------------------------------

    [Fact]
    public void Resolve_MinimalArguments_ProducesAUsableConfiguration()
    {
        YaguMcpServerOptions options = ResolveWith();

        Assert.Null(options.Failure);
        Assert.Equal(Path.GetFullPath(_exe), options.YaguExePath);
        Assert.Single(options.AuthorizationPolicy.AllowedRoots);
        Assert.Equal(McpTransport.Stdio, options.Transport);
        Assert.Equal(4, options.MaxConcurrentCalls);
        Assert.Equal(YaguMcpServerOptions.DefaultMaxExportResults, options.MaxExportResults);
        Assert.False(options.Verbose);
        Assert.Null(options.HttpToken);
    }

    [Fact]
    public void Resolve_GrantsNoCapabilityUnlessAsked()
    {
        McpAuthorizationPolicy policy = ResolveWith().AuthorizationPolicy;

        // The default must be "authorizes nothing", not "authorizes reads".
        Assert.False(policy.HasCapability(McpCapability.Read));
        Assert.False(policy.HasCapability(McpCapability.Search));
        Assert.False(policy.HasCapability(McpCapability.ExportWrite));
        Assert.False(policy.HasCapability(McpCapability.Replace));
        Assert.False(policy.HasCapability(McpCapability.IndexMaintenance));
        Assert.False(policy.HasCapability(McpCapability.DestructiveIndexOps));
        Assert.False(policy.HasCapability(McpCapability.CloudHydration));
    }

    [Fact]
    public void Resolve_RepeatedAllowedRoots_AreAllKept()
    {
        string second = Path.Combine(_dir, "root2");
        Directory.CreateDirectory(second);

        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            ["--yagu-exe", _exe, "--allowed-root", _root, "--allowed-root", second]);

        Assert.Null(options.Failure);
        Assert.Equal(2, options.AuthorizationPolicy.AllowedRoots.Count);
    }

    [Fact]
    public void Resolve_RepeatedCapabilities_AreUnioned()
    {
        YaguMcpServerOptions options = ResolveWith("--capability", "Read", "--capability", "Search");

        Assert.Null(options.Failure);
        Assert.True(options.AuthorizationPolicy.HasCapability(McpCapability.Read));
        Assert.True(options.AuthorizationPolicy.HasCapability(McpCapability.Search));
        Assert.False(options.AuthorizationPolicy.HasCapability(McpCapability.Replace));
    }

    [Fact]
    public void Resolve_CapabilityNameIsCaseInsensitive()
    {
        YaguMcpServerOptions options = ResolveWith("--capability", "rEaD");

        Assert.Null(options.Failure);
        Assert.True(options.AuthorizationPolicy.HasCapability(McpCapability.Read));
        Assert.False(options.AuthorizationPolicy.HasCapability(McpCapability.Search));
    }

    [Fact]
    public void Resolve_FlagNamesAreCaseInsensitive()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            ["--YAGU-EXE", _exe, "--Allowed-Root", _root, "--VERBOSE"]);

        Assert.Null(options.Failure);
        Assert.True(options.Verbose);
    }

    // ---- refusals ------------------------------------------------------------------------------

    [Fact]
    public void Resolve_NoAllowedRoot_IsRefused()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(["--yagu-exe", _exe]);

        Assert.NotNull(options.Failure);
        Assert.Contains("--allowed-root", options.Failure);
    }

    [Fact]
    public void Resolve_MissingYaguExe_IsRefused()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            ["--yagu-exe", Path.Combine(_dir, "absent.exe"), "--allowed-root", _root]);

        Assert.NotNull(options.Failure);
        Assert.Contains("was not found", options.Failure);
    }

    [Fact]
    public void Resolve_EmptyYaguExe_IsRefused()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            ["--yagu-exe", "", "--allowed-root", _root]);

        Assert.NotNull(options.Failure);
        Assert.Contains("was not found", options.Failure);
    }

    [Fact]
    public void Resolve_RootThatCannotBeNormalized_IsRefused()
    {
        // A relative root would resolve against whatever working directory the server inherited.
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            ["--yagu-exe", _exe, "--allowed-root", "relative\\path"]);

        Assert.NotNull(options.Failure);
        Assert.Contains("survived validation", options.Failure);
    }

    [Fact]
    public void Resolve_UnknownCapability_IsRefusedRatherThanIgnored()
    {
        YaguMcpServerOptions options = ResolveWith("--capability", "TotalControl");

        Assert.NotNull(options.Failure);
        Assert.Contains("unknown capability", options.Failure);
        Assert.Contains("TotalControl", options.Failure);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void Resolve_InvalidMaxConcurrent_IsRefused(string value)
    {
        YaguMcpServerOptions options = ResolveWith("--max-concurrent", value);

        Assert.NotNull(options.Failure);
        Assert.Contains("--max-concurrent", options.Failure);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void Resolve_InvalidMaxExportResults_IsRefused(string value)
    {
        YaguMcpServerOptions options = ResolveWith("--max-export-results", value);

        Assert.NotNull(options.Failure);
        Assert.Contains("--max-export-results", options.Failure);
    }

    [Fact]
    public void Resolve_ValidMaxExportResults_IsRetained()
    {
        YaguMcpServerOptions options = ResolveWith("--max-export-results", "1234");

        Assert.Null(options.Failure);
        Assert.Equal(1234, options.MaxExportResults);
    }

    [Theory]
    [InlineData("--max-retained-jobs")]
    [InlineData("--max-running-jobs")]
    [InlineData("--max-job-result-chars")]
    [InlineData("--call-timeout-seconds")]
    [InlineData("--maintenance-timeout-minutes")]
    [InlineData("--model-timeout-minutes")]
    public void Resolve_MalformedJobCeiling_IsRefused(string flag)
    {
        YaguMcpServerOptions options = ResolveWith(flag, "not-a-number");

        Assert.NotNull(options.Failure);
        Assert.Contains(flag, options.Failure);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("http")]
    public void Resolve_PortOutsideTheValidRange_IsRefused(string value)
    {
        YaguMcpServerOptions options = ResolveWith("--port", value);

        Assert.NotNull(options.Failure);
        Assert.Contains("--port", options.Failure);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    [InlineData("8377")]
    public void Resolve_PortAtTheRangeBoundaries_IsAccepted(string value)
    {
        YaguMcpServerOptions options = ResolveWith("--port", value);

        Assert.Null(options.Failure);
        Assert.Equal(int.Parse(value), options.HttpPort);
    }

    [Fact]
    public void Resolve_UnknownTransport_IsRefused()
    {
        YaguMcpServerOptions options = ResolveWith("--transport", "carrier-pigeon");

        Assert.NotNull(options.Failure);
        Assert.Contains("unknown transport", options.Failure);
    }

    [Fact]
    public void Resolve_TokenOnTheCommandLine_IsRefusedBecauseItWouldPublishTheToken()
    {
        // Every process on the machine can read another process's command line, so accepting a token
        // here would leak it. Failing loudly is the point: silently ignoring it would leave the caller
        // believing the token was applied.
        YaguMcpServerOptions options = ResolveWith("--token", "s3cret");

        Assert.NotNull(options.Failure);
        Assert.Contains("--token is not supported", options.Failure);
    }

    [Fact]
    public void Resolve_TokenFlag_IsRefusedEvenWithNoValueFollowingIt()
    {
        YaguMcpServerOptions options = ResolveWith("--token");

        Assert.NotNull(options.Failure);
        Assert.Contains("--token is not supported", options.Failure);
    }

    // ---- trailing flags and stray arguments ----------------------------------------------------

    [Theory]
    [InlineData("--capability")]
    [InlineData("--max-concurrent")]
    [InlineData("--max-export-results")]
    [InlineData("--transport")]
    [InlineData("--port")]
    [InlineData("--allowed-root")]
    public void Resolve_ValueFlagWithNoValue_IsIgnoredRatherThanConsumingTheNextFlag(string flag)
    {
        // The flag sits last, so its "i + 1 < args.Length" guard fails and it must not throw.
        YaguMcpServerOptions options = ResolveWith(flag);

        Assert.Null(options.Failure);
    }

    [Fact]
    public void Resolve_UnrecognisedArgument_IsIgnored()
    {
        YaguMcpServerOptions options = ResolveWith("--not-a-flag", "value");

        Assert.Null(options.Failure);
    }

    [Fact]
    public void Resolve_LastWinsForSingleValuedFlags()
    {
        YaguMcpServerOptions options = ResolveWith("--max-concurrent", "2", "--max-concurrent", "7");

        Assert.Null(options.Failure);
        Assert.Equal(7, options.MaxConcurrentCalls);
    }

    // ---- transport -----------------------------------------------------------------------------

    [Theory]
    [InlineData("stdio")]
    [InlineData("stdin")]
    [InlineData("std")]
    [InlineData("STDIO")]
    [InlineData("  stdio  ")]
    public void TryParseTransport_StdioAliases(string value)
    {
        Assert.True(YaguMcpServerOptionsResolver.TryParseTransport(value, out McpTransport transport));
        Assert.Equal(McpTransport.Stdio, transport);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("loopback")]
    [InlineData("HTTP")]
    public void TryParseTransport_HttpAliases(string value)
    {
        Assert.True(YaguMcpServerOptionsResolver.TryParseTransport(value, out McpTransport transport));
        Assert.Equal(McpTransport.Http, transport);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tcp")]
    public void TryParseTransport_UnknownValue_FallsBackToStdioAndReportsFailure(string? value)
    {
        Assert.False(YaguMcpServerOptionsResolver.TryParseTransport(value, out McpTransport transport));

        // The out value must still be the safe transport, never an unauthenticated one.
        Assert.Equal(McpTransport.Stdio, transport);
    }

    [Fact]
    public void Resolve_HttpTransport_CarriesATokenReadFromTheStore()
    {
        YaguMcpServerOptions options = ResolveWith("--transport", "http");

        // EnsureToken is idempotent, so this reuses the existing per-user token when one is present.
        Assert.Null(options.Failure);
        Assert.Equal(McpTransport.Http, options.Transport);
        Assert.False(string.IsNullOrEmpty(options.HttpToken));
    }

    [Fact]
    public void Resolve_HttpTransportWithoutAToken_IsRefused()
    {
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(
            BaseArgs("--transport", "http"),
            ensureHttpToken: () => null);

        Assert.NotNull(options.Failure);
        Assert.Contains("needs a token", options.Failure);
        Assert.Equal(McpTransport.Stdio, options.Transport);
    }

    [Fact]
    public void Resolve_StdioTransport_NeverCarriesAToken()
    {
        YaguMcpServerOptions options = ResolveWith("--transport", "stdio");

        Assert.Null(options.Failure);
        Assert.Null(options.HttpToken);
    }

    // ---- the refusal shape ---------------------------------------------------------------------

    [Fact]
    public void Invalid_ProducesAPolicyThatAuthorizesNothing()
    {
        YaguMcpServerOptions options = YaguMcpServerOptions.Invalid("boom");

        Assert.Equal("boom", options.Failure);
        Assert.Empty(options.AuthorizationPolicy.AllowedRoots);
        Assert.False(options.AuthorizationPolicy.HasCapability(McpCapability.Read));
        Assert.False(options.AuthorizationPolicy.HasCapability(McpCapability.Search));
    }

    [Fact]
    public void WithoutYaguExe_TheServerLooksForItOneDirectoryUpFromItself()
    {
        // The server is staged in <app>\mcp-server\, so a client that registered Yagu without the flag
        // still has to find the Yagu.exe it shells out to for every tool call.
        YaguMcpServerOptions options = YaguMcpServerOptionsResolver.Resolve(["--allowed-root", _root]);

        string expected = Path.Combine(
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!, "Yagu.exe");

        if (File.Exists(expected))
        {
            Assert.Null(options.Failure);
            Assert.Equal(Path.GetFullPath(expected), options.YaguExePath, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Nothing there to run: refusing with the path it tried is the only useful report.
            Assert.NotNull(options.Failure);
            Assert.Contains("Yagu.exe", options.Failure, StringComparison.OrdinalIgnoreCase);
        }
    }
}
