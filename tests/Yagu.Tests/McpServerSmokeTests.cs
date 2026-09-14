using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Yagu.Tests;

/// <summary>
/// Drives the real <c>Yagu.McpServer</c> over stdio and checks the protocol surface end to end.
/// </summary>
/// <remarks>
/// <para>
/// This test is deliberately <b>fast and untagged</b> so it runs in CI, which filters
/// <c>Category!=Slow&amp;Category!=GPU</c>. A protocol test hidden behind <c>Category=Slow</c> would let
/// stdio initialization, tool discovery, stdout pollution, and tool-schema compatibility regress with no
/// required check standing in the way.
/// </para>
/// <para>
/// The server binary is guaranteed to exist because Yagu.Tests takes a build-only
/// <c>ProjectReference</c> on it.
/// </para>
/// </remarks>
public sealed class McpServerSmokeTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Server_Initializes_ListsTools_AndShutsDownCleanly()
    {
        using var session = McpStdioSession.Start(AllowedRoot());

        JsonElement initialize = await session.RequestAsync(
            1, "initialize",
            """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}""");

        JsonElement initializeResult = initialize.GetProperty("result");
        Assert.Equal("yagu", initializeResult.GetProperty("serverInfo").GetProperty("name").GetString());

        string instructions = initializeResult.GetProperty("instructions").GetString() ?? string.Empty;
        Assert.Contains("across an entire Windows drive", instructions, StringComparison.Ordinal);
        Assert.Contains("instead of shell commands or workspace search", instructions, StringComparison.Ordinal);
        Assert.Contains("find all instances of the string", instructions, StringComparison.Ordinal);
        Assert.Contains("all files on C:\\", instructions, StringComparison.Ordinal);
        Assert.Contains("do not reduce maxMatchesPerFile", instructions, StringComparison.Ordinal);
        Assert.Contains("'all files with'", instructions, StringComparison.Ordinal);
        Assert.Contains("maxMatchesPerFile=1", instructions, StringComparison.Ordinal);

        await session.NotifyAsync("notifications/initialized");

        JsonElement tools = await session.RequestAsync(2, "tools/list", null);
        var names = tools.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("yagu_calc", names);
        Assert.Contains("yagu_read_file", names);

        // Index maintenance is only usable if every step of the lifecycle is reachable: a client that
        // can build but not enroll a folder, or enroll but never compact, is stuck.
        foreach (string tool in new[]
        {
            "yagu_index_add_root", "yagu_index_remove_root", "yagu_index_build_start",
            "yagu_index_compact_start", "yagu_index_validate_start",
        })
        {
            Assert.Contains(tool, names);
        }

        int exitCode = session.CloseAndWait();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task SearchTool_AdvertisesFilesystemIntentAndOneMatchPerFileOption()
    {
        using var session = McpStdioSession.Start(AllowedRoot());

        await session.RequestAsync(
            1, "initialize",
            """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}""");
        await session.NotifyAsync("notifications/initialized");

        JsonElement tools = await session.RequestAsync(2, "tools/list", null);
        JsonElement search = tools.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "yagu_search");

        string description = search.GetProperty("description").GetString() ?? string.Empty;
        Assert.Contains("instead of shell commands or workspace search", description, StringComparison.Ordinal);
        Assert.Contains("find all instances of the string X", description, StringComparison.Ordinal);
        Assert.Contains("all files on C:\\", description, StringComparison.Ordinal);
        Assert.Contains("outside the current workspace", description, StringComparison.Ordinal);
        Assert.Contains("all occurrences", description, StringComparison.Ordinal);
        Assert.Contains("all files containing", description, StringComparison.Ordinal);

        JsonElement properties = search.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("maxMatchesPerFile", out JsonElement maxMatchesPerFile));
        Assert.Contains("all files contain", maxMatchesPerFile.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("'string'", properties.GetProperty("wholeWord").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("text inside files", properties.GetProperty("searchMode").GetProperty("description").GetString(), StringComparison.Ordinal);

        session.CloseAndWait();
    }

    [Fact]
    public async Task ReplacePreviewCanNarrowScopeWithExcludeGlobs()
    {
        // yagu_search accepted excludes from the start; the replace preview did not, so there was no way
        // to keep a sweep out of generated or vendored folders -- the narrowing a caller most wants
        // before committing a replacement.
        using var session = McpStdioSession.Start(AllowedRoot());

        await session.RequestAsync(
            1, "initialize",
            """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}""");
        await session.NotifyAsync("notifications/initialized");

        JsonElement tools = await session.RequestAsync(2, "tools/list", null);
        JsonElement preview = tools.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "yagu_replace_preview");

        JsonElement properties = preview.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("includeGlobs", out _), "yagu_replace_preview lost includeGlobs.");
        Assert.True(properties.TryGetProperty("excludeGlobs", out _), "yagu_replace_preview cannot exclude paths.");

        session.CloseAndWait();
    }

    [Fact]
    public async Task EveryToolDeclaresANameDescriptionAndObjectSchema()
    {
        // A tool the model cannot understand is worse than a missing tool: it will be called wrongly.
        using var session = McpStdioSession.Start(AllowedRoot());

        await session.RequestAsync(
            1, "initialize",
            """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}""");
        await session.NotifyAsync("notifications/initialized");

        JsonElement tools = await session.RequestAsync(2, "tools/list", null);

        foreach (JsonElement tool in tools.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            string? name = tool.GetProperty("name").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.StartsWith("yagu_", name, StringComparison.Ordinal);

            Assert.True(tool.TryGetProperty("description", out var description), $"{name} has no description.");
            Assert.False(string.IsNullOrWhiteSpace(description.GetString()), $"{name} has an empty description.");

            Assert.True(tool.TryGetProperty("inputSchema", out var schema), $"{name} has no inputSchema.");
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }

        session.CloseAndWait();
    }

    [Fact]
    public async Task StdoutCarriesNothingButJsonRpcFrames()
    {
        // stdout IS the transport. One stray Console.WriteLine anywhere in the server or its
        // dependencies corrupts the stream, and the client fails with a parse error that points
        // nowhere near the cause.
        using var session = McpStdioSession.Start(AllowedRoot());

        await session.RequestAsync(
            1, "initialize",
            """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}""");
        await session.NotifyAsync("notifications/initialized");
        await session.RequestAsync(2, "tools/list", null);

        session.CloseAndWait();

        foreach (string line in session.StdoutLines)
        {
            JsonDocument parsed = JsonDocument.Parse(line);
            Assert.Equal("2.0", parsed.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    [Fact]
    public async Task ReadingOutsideAnAllowedRoot_IsRefused()
    {
        using var session = McpStdioSession.Start(AllowedRoot());

        await session.RequestAsync(
            1, "initialize",
            """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}""");
        await session.NotifyAsync("notifications/initialized");

        JsonElement response = await session.RequestAsync(
            2, "tools/call",
            """{"name":"yagu_read_file","arguments":{"path":"C:\\Windows\\System32\\drivers\\etc\\hosts","firstLine":1,"lastLine":1}}""");

        string text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        Assert.Contains("outside-allowed-roots", text, StringComparison.Ordinal);

        session.CloseAndWait();
    }

    [Fact]
    public async Task ServerStartedWithoutAnAllowedRoot_RefusesToRun()
    {
        // A server that authorized everything by default would be the worst possible failure mode.
        using var session = McpStdioSession.Start(allowedRoot: null);

        int exitCode = session.CloseAndWait();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("allowed-root", session.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static string AllowedRoot() => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>A live stdio session against the built server.</summary>
    private sealed class McpStdioSession : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly List<string> _stdoutLines = [];

        private McpStdioSession(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public IReadOnlyList<string> StdoutLines => _stdoutLines;

        public string StandardError { get; private set; } = string.Empty;

        public static McpStdioSession Start(string? allowedRoot)
        {
            string repoRoot = FindRepoRoot();
            string configuration = ResolveConfiguration();
            string serverDll = Path.Combine(
                repoRoot, "src", "Yagu.McpServer", "bin", configuration, "net10.0", "Yagu.McpServer.dll");

            Assert.True(
                File.Exists(serverDll),
                $"The MCP server was not built at '{serverDll}'. Yagu.Tests takes a build-only ProjectReference on it, so this should not happen.");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            psi.ArgumentList.Add(serverDll);

            // The protocol surface under test never invokes the bridge, so pointing --yagu-exe at any
            // existing file keeps startup validation satisfied without depending on a built Yagu.exe.
            psi.ArgumentList.Add("--yagu-exe");
            psi.ArgumentList.Add(ResolveYaguExeOrPlaceholder(repoRoot, configuration));

            if (allowedRoot is not null)
            {
                psi.ArgumentList.Add("--allowed-root");
                psi.ArgumentList.Add(allowedRoot);
                psi.ArgumentList.Add("--capability");
                psi.ArgumentList.Add("Read");
                psi.ArgumentList.Add("--capability");
                psi.ArgumentList.Add("Search");
            }

            Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("could not start the MCP server.");

            return new McpStdioSession(process);
        }

        public async Task<JsonElement> RequestAsync(int id, string method, string? paramsJson)
        {
            string payload = paramsJson is null
                ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
                : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""";

            await _process.StandardInput.WriteLineAsync(payload);
            await _process.StandardInput.FlushAsync();

            string line = await ReadLineAsync();
            _stdoutLines.Add(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }

        public async Task NotifyAsync(string method)
        {
            await _process.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","method":"{{method}}"}""");
            await _process.StandardInput.FlushAsync();
        }

        public int CloseAndWait()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit((int)ReadTimeout.TotalMilliseconds))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            StandardError = SafeStderr();
            return _process.HasExited ? _process.ExitCode : -1;
        }

        private async Task<string> ReadLineAsync()
        {
            Task<string?> read = _process.StandardOutput.ReadLineAsync();
            Task completed = await Task.WhenAny(read, Task.Delay(ReadTimeout));

            if (completed != read)
                throw new TimeoutException($"the server produced no response within {ReadTimeout.TotalSeconds:N0}s. stderr: {SafeStderr()}");

            return await read ?? throw new InvalidOperationException($"the server closed stdout. stderr: {SafeStderr()}");
        }

        private string SafeStderr()
        {
            try
            {
                return _stderr.Wait(TimeSpan.FromSeconds(2)) ? _stderr.Result : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string ResolveConfiguration()
        {
            // .../tests/Yagu.Tests/bin/<Configuration>/<tfm>/
            var tfm = new DirectoryInfo(AppContext.BaseDirectory);
            return tfm.Parent?.Name ?? "Debug";
        }

        private static string ResolveYaguExeOrPlaceholder(string repoRoot, string configuration)
        {
            string yaguExe = Path.Combine(
                repoRoot, "src", "Yagu", "bin", configuration, "net10.0-windows10.0.19041.0", "Yagu.exe");

            return File.Exists(yaguExe)
                ? yaguExe
                : Path.Combine(repoRoot, "src", "Yagu.McpServer", "bin", configuration, "net10.0", "Yagu.McpServer.dll");
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

            _process.Dispose();
        }
    }
}
