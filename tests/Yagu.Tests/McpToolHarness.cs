using System.Diagnostics;
using System.Text.Json;
using Yagu.McpServer;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Drives the MCP tool classes against the real <see cref="YaguCliBridge"/> and a real child process.
/// </summary>
/// <remarks>
/// <para>
/// The tools are a translation layer in both directions: a client's arguments become a CLI command
/// line, and the CLI's machine records become the text a model reads. Faking the bridge would test
/// neither end, because the bridge is what turns an argument list into a process and a byte stream back
/// into records. So the tools are pointed at <c>Yagu.FakeCli</c>, which speaks the real protocol.
/// </para>
/// <para>
/// A tool owns its own argument vector, so a test cannot append <c>--fake-scenario</c> to it. The
/// scenario and the frames to emit are therefore passed through the child's environment, set per
/// process on the <see cref="ProcessStartInfo"/> the bridge hands to its start hook. Setting them with
/// <see cref="Environment.SetEnvironmentVariable(string,string)"/> instead would be process-global and
/// would race every other test in the parallel run.
/// </para>
/// </remarks>
internal sealed class McpToolHarness : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _tempPaths = [];

    /// <summary>A timeout long enough that a loaded machine never turns a pass into a flake.</summary>
    public static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    /// <summary>The directory tests use as their allowed root. Real, so path resolution is real.</summary>
    public string Root { get; } = CreateTempDirectory();

    /// <summary>Builds a bridge whose child emits <paramref name="frames"/> verbatim, then exits.</summary>
    public YaguCliBridge Emitting(IEnumerable<string> frames, int exitCode = 0, string? standardError = null)
    {
        string framesFile = Path.Combine(NewTempDirectory(), "frames.jsonl");
        File.WriteAllLines(framesFile, frames);

        return Build(environment =>
        {
            environment["YAGU_FAKE_SCENARIO"] = "emit";
            environment["YAGU_FAKE_FRAMES"] = framesFile;
            environment["YAGU_FAKE_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (standardError is not null)
                environment["YAGU_FAKE_STDERR"] = standardError;
        });
    }

    /// <summary>Builds a bridge running one of Yagu.FakeCli's named scenarios.</summary>
    public YaguCliBridge Scenario(string scenario)
        => Build(environment => environment["YAGU_FAKE_SCENARIO"] = scenario);

    /// <summary>
    /// Builds a bridge that records the argument vector the tool produced and then emits
    /// <paramref name="frames"/>, so one call can assert both the command line and the rendering.
    /// </summary>
    public YaguCliBridge Recording(out List<string> arguments, params string[] frames)
    {
        var captured = new List<string>();
        arguments = captured;

        string framesFile = Path.Combine(NewTempDirectory(), "frames.jsonl");
        File.WriteAllLines(framesFile, frames);

        return Build(
            environment =>
            {
                environment["YAGU_FAKE_SCENARIO"] = "emit";
                environment["YAGU_FAKE_FRAMES"] = framesFile;
            },
            captured);
    }

    private YaguCliBridge Build(Action<IDictionary<string, string?>> configure, List<string>? captured = null)
    {
        var bridge = new YaguCliBridge(
            FakeCliPath(),
            maxConcurrentCalls: 4,
            startProcess: psi =>
            {
                configure(psi.Environment);
                captured?.AddRange(psi.ArgumentList);
                return Process.Start(psi);
            });

        _owned.Add(bridge);
        return bridge;
    }

    /// <summary>A policy over <see cref="Root"/> with the capabilities the tool under test needs.</summary>
    public McpAuthorizationPolicy Policy(McpCapability capabilities) => new([Root], capabilities);

    /// <summary>Options wired to a policy. The exe path is unused: the bridge is supplied directly.</summary>
    public static YaguMcpServerOptions Options(
        McpAuthorizationPolicy policy, int maxExportResults = 50_000)
        => new()
        {
            YaguExePath = "unused.exe",
            AuthorizationPolicy = policy,
            MaxExportResults = maxExportResults,
            DefaultCallTimeout = Generous,
            MaintenanceTimeout = Generous,
            ModelTimeout = Generous,
        };

    /// <summary>Creates a real file under <see cref="Root"/> and returns its full path.</summary>
    public string MakeFile(string relativePath, string contents = "x")
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    private string NewTempDirectory()
    {
        string path = CreateTempDirectory();
        _tempPaths.Add(path);
        return path;
    }

    /// <summary>Builds one protocol frame. <c>&lt;REQUESTID&gt;</c> is substituted by the child.</summary>
    public static string Frame(string type, object data, long sequence = 1)
        => "{\"schemaVersion\":1,\"type\":\"" + type + "\",\"sequence\":" + sequence
         + ",\"requestId\":\"<REQUESTID>\",\"data\":" + JsonSerializer.Serialize(data) + "}";

    /// <summary>The closing summary frame. Without one the bridge reports the run as a crash.</summary>
    public static string Summary(object? data = null, long sequence = 99)
        => Frame("summary", data ?? new { returnedRecords = 0, truncated = false, exitCode = 0 }, sequence);

    /// <summary>Decodes one frame into a record, through the same reader the bridge uses.</summary>
    /// <remarks>
    /// Rendering is a pure function of the decoded records, so the render helpers are driven directly
    /// rather than through a child process. Going through <see cref="MachineOutputReader.TryParse"/>
    /// rather than constructing the struct keeps the <see cref="JsonElement"/> payloads identical to
    /// the ones a real run produces, which is what the truncation and context branches actually read.
    /// </remarks>
    public static MachineRecord Record(string type, object data, long sequence = 1)
    {
        string line = Frame(type, data, sequence).Replace("<REQUESTID>", "test", StringComparison.Ordinal);
        Assert.True(MachineOutputReader.TryParse(line, out MachineRecord record, out string? error), error);
        return record;
    }

    /// <summary>A completed result carrying <paramref name="records"/>, for the render helpers.</summary>
    public static YaguCliResult Completed(
        IEnumerable<MachineRecord> records, int exitCode = 0, string standardError = "")
        => new(YaguCliOutcome.Completed, exitCode, [.. records], Failure: null, standardError);

    private static string FakeCliPath()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string path = Path.Combine(
            FindRepoRoot(), "tests", "Yagu.FakeCli", "bin", configuration, "net10.0", "Yagu.FakeCli.exe");

        Assert.True(File.Exists(path), $"Yagu.FakeCli was not built at '{path}'.");
        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "yagu-mcp-tool-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            try { owned.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        foreach (string path in _tempPaths.Append(Root))
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
