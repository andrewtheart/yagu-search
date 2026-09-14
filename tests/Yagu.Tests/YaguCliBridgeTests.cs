using System.Diagnostics;
using System.Text;
using Yagu.McpServer;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Index;

namespace Yagu.Tests;

/// <summary>
/// Drives <see cref="YaguCliBridge"/> against a real child process (<c>Yagu.FakeCli</c>).
/// </summary>
/// <remarks>
/// Every child-process safety property of the MCP server lives in the bridge: argument passing without
/// a shell, draining both pipes concurrently, bounding the stdout wait so an orphaned worker cannot hang
/// a request for ever, and killing the whole tree on timeout. None of those can be demonstrated with a
/// fake collaborator, because the bug in each case is in how a real OS process behaves.
/// <para>
/// Deliberately NOT tagged Slow: CI filters that category out, and these guard a hang and an argument
/// injection rather than a performance number. The whole class runs in about twenty seconds.
/// </para>
/// </remarks>
public sealed class YaguCliBridgeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

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

    private static YaguCliBridge Bridge() => new(FakeCliPath(), maxConcurrentCalls: 4);

    private static List<string> Scenario(string name) => ["--fake-scenario", name];

    [Fact]
    public async Task ACleanRun_IsCompletedAndCarriesItsRecords()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Failure);
        Assert.Contains(result.Records, r => r.Type == MachineProtocol.Types.Match);
        Assert.Contains(result.Records, r => r.Type == MachineProtocol.Types.Summary);
    }

    [Fact]
    public async Task ArgumentsSurviveVerbatim_WithoutAShellResplittingThem()
    {
        // The bridge uses ArgumentList, so a value can carry spaces, embedded quotes, an ampersand, a
        // percent sign or a trailing backslash without changing the argument boundaries. Building a
        // command-line string instead would corrupt every one of these.
        string[] hostile =
        [
            @"a value with spaces",
            @"quote"" inside",
            @"amp & caret ^ percent %PATH%",
            @"C:\trailing\backslash\",
            @"semi;colon | pipe",
        ];

        using YaguCliBridge bridge = Bridge();

        List<string> arguments = [.. Scenario("echo-args"), .. hostile];
        YaguCliResult result = await bridge.InvokeAsync(arguments, Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);

        List<string> echoed = [.. result.Records
            .Where(r => r.Type == MachineProtocol.Types.Result)
            .Select(r => r.GetString("value") ?? string.Empty)];

        foreach (string expected in hostile)
            Assert.Contains(expected, echoed);
    }

    [Fact]
    public async Task AChildThatNeverClosesItsProtocolStream_IsReportedAsCrashed()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("no-summary"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Crashed, result.Outcome);
        Assert.Contains("without completing its protocol stream", result.Failure);
    }

    [Fact]
    public async Task AMalformedLine_IsAProtocolErrorRatherThanAnException()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("malformed"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.ProtocolError, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task ANonZeroExit_IsCompletedWithTheExitCodePreserved()
    {
        // The run finished its protocol stream, so it is a completed call that failed - not a crash.
        // Collapsing the two would stop a client telling "your query was invalid" from "Yagu died".
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("nonzero-exit"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task AHangingChild_TimesOutAndIsKilled()
    {
        using YaguCliBridge bridge = Bridge();

        var stopwatch = Stopwatch.StartNew();
        YaguCliResult result = await bridge.InvokeAsync(
            Scenario("hang"), TimeSpan.FromSeconds(3), CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(YaguCliOutcome.TimedOut, result.Outcome);
        Assert.Contains("limit", result.Failure);

        // Returned near its own deadline rather than waiting on a process that never exits.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"the timed-out call took {stopwatch.Elapsed.TotalSeconds:N1}s to return.");

        // Whatever arrived before the timeout is still reported, so a cancelled call can explain itself.
        Assert.Contains(result.Records, r => r.Type == MachineProtocol.Types.Match);
    }

    [Fact]
    public async Task ACancelledCall_ReturnsPromptlyAndIsNotReportedAsATimeout()
    {
        using YaguCliBridge bridge = Bridge();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        YaguCliResult result = await bridge.InvokeAsync(Scenario("hang"), Generous, cts.Token);

        Assert.Equal(YaguCliOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancelled", result.Failure);
    }

    [Fact]
    public async Task AnOrphanHoldingStdout_DoesNotHangTheCall()
    {
        // The regression this exists for: Yagu's workers inherit its stdout handle, so the pipe does not
        // reach EOF just because the child exited. An unbounded await on the reader held the request -
        // and its concurrency lease - for the life of the server.
        using YaguCliBridge bridge = Bridge();

        var stopwatch = Stopwatch.StartNew();
        YaguCliResult result = await bridge.InvokeAsync(
            Scenario("orphan-holds-stdout"), Generous, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(45),
            $"the call took {stopwatch.Elapsed.TotalSeconds:N1}s, which means it waited on the orphan's pipe.");

        // The records that arrived before the child exited are intact, so bounding the drain does not
        // cost the response.
        Assert.Contains(result.Records, r => r.Type == MachineProtocol.Types.Summary);
        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task TheChildGetsAClosedStdin_SoItCannotEatTheClientsFrames()
    {
        // Under the stdio transport the server's own stdin IS the client's JSON-RPC stream. An
        // unredirected child inherits that handle, and one Console.ReadLine would consume frames.
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("read-stdin"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);

        MachineRecord record = result.Records.First(r => r.Type == MachineProtocol.Types.Result);
        Assert.Equal("<eof>", record.GetString("stdinLine"));
    }

    [Fact]
    public async Task AChildThatFloodsStderr_DoesNotDeadlockTheStdoutReader()
    {
        // Reading one pipe to completion while the other fills its buffer is the classic deadlock. Both
        // are drained concurrently from process start, so a noisy child still completes.
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("noisy-stderr"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);
        Assert.Contains(result.Records, r => r.Type == MachineProtocol.Types.Match);
        Assert.False(string.IsNullOrEmpty(result.StandardError));
    }

    [Fact]
    public async Task NonAsciiPathsAndText_SurviveTheProtocolRoundTrip()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("unicode"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);

        MachineRecord match = result.Records.First(r => r.Type == MachineProtocol.Types.Match);
        string path = match.GetString("path") ?? string.Empty;
        string text = match.GetString("text") ?? string.Empty;

        // Compared by content, not by console rendering: a terminal showing "?" proves nothing.
        Assert.Contains("\u00e9", path, StringComparison.Ordinal);
        Assert.Contains("\u4e2d", path, StringComparison.Ordinal);
        Assert.Contains("\ud83d\ude00", path, StringComparison.Ordinal);
        Assert.Contains("caf\u00e9", text, StringComparison.Ordinal);
        Assert.Contains("\t", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingExecutable_IsReportedRatherThanThrown()
    {
        using var bridge = new YaguCliBridge(
            Path.Combine(Path.GetTempPath(), "yagu-does-not-exist-" + Guid.NewGuid().ToString("n") + ".exe"),
            maxConcurrentCalls: 1);

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Crashed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task RelativeMissingExecutable_UsesTheCurrentDirectoryAndReportsACrash()
    {
        string missing = "Yagu.DoesNotExist-" + Guid.NewGuid().ToString("n") + ".exe";
        using var bridge = new YaguCliBridge(missing, maxConcurrentCalls: 1);

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Crashed, result.Outcome);
        Assert.Contains(missing, result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RootExecutablePath_UsesTheCurrentDirectoryAndReportsACrash()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        using var bridge = new YaguCliBridge(root, maxConcurrentCalls: 1);

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Crashed, result.Outcome);
        Assert.Contains(root, result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessStarterReturningNull_IsReportedAsACrash()
    {
        using var bridge = new YaguCliBridge(
            FakeCliPath(),
            maxConcurrentCalls: 1,
            startProcess: _ => null);

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Crashed, result.Outcome);
        Assert.Contains("could not start", result.Failure);
    }

    [Fact]
    public async Task InvalidJobObject_FallsBackToExplicitProcessCleanup()
    {
        using var bridge = new YaguCliBridge(
            FakeCliPath(),
            maxConcurrentCalls: 1,
            createJob: InvalidJob);

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ClosingAnAlreadyBrokenStdin_IsBestEffort()
    {
        var stdin = new ThrowingTextWriter();

        YaguCliBridge.CloseStandardInput(stdin);

        Assert.True(stdin.CloseAttempted);
    }

    [Fact]
    public async Task ReaderIoFailure_IsSwallowedAfterPreservingParsedRecords()
    {
        using var reader = new StreamReader(new ThrowingReadStream());
        var records = new List<MachineRecord>();

        await YaguCliBridge.ReadRecordsAsync(reader, records, new object(), _ => { });

        Assert.Empty(records);
    }

    [Fact]
    public void KillingAnAlreadyDisposedProcess_IsBestEffort()
    {
        var process = new Process();
        process.Dispose();

        YaguCliBridge.KillTree(process);
    }

    [Fact]
    public async Task ConcurrentCalls_AllCompleteIndependently()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None)));

        Assert.All(results, r => Assert.Equal(YaguCliOutcome.Completed, r.Outcome));
        Assert.All(results, r => Assert.Contains(r.Records, x => x.Type == MachineProtocol.Types.Summary));
    }

    [Fact]
    public async Task AnExecutableThatIsNotThere_IsReportedWithThePathThatWasTried()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "Yagu.DoesNotExist-" + Guid.NewGuid().ToString("n") + ".exe");
        using var bridge = new YaguCliBridge(missing, maxConcurrentCalls: 1);

        YaguCliResult result = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);

        // The MCP server ships beside Yagu.exe but can be launched from anywhere by a client, so a
        // mis-resolved path is a real deployment failure. It has to name the path, not just fail.
        Assert.Equal(YaguCliOutcome.Crashed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(CliExitCode.InternalFailure, result.ExitCode);
        Assert.NotNull(result.Failure);
        Assert.Contains(missing, result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task Succeeded_TracksBothTheOutcomeAndTheExitCode()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult ok = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);
        Assert.True(ok.Succeeded);

        // A run that COMPLETED but reported a non-success exit code is not a success: the tools key off
        // this to decide whether to render records or an error.
        YaguCliResult refused = await bridge.InvokeAsync(Scenario("nonzero-exit"), Generous, CancellationToken.None);
        Assert.Equal(YaguCliOutcome.Completed, refused.Outcome);
        Assert.False(CliExitCode.IsSuccess(refused.ExitCode));
        Assert.False(refused.Succeeded);
    }

    [Fact]
    public async Task Summary_IsTheClosingRecordAndIsNullWhenTheRunProducedNone()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult withSummary = await bridge.InvokeAsync(Scenario("normal"), Generous, CancellationToken.None);
        Assert.NotNull(withSummary.Summary);
        Assert.Equal(MachineProtocol.Types.Summary, withSummary.Summary!.Value.Type);

        // A stream that ended without its summary is a crash, and Summary must not invent one.
        YaguCliResult truncated = await bridge.InvokeAsync(Scenario("no-summary"), Generous, CancellationToken.None);
        Assert.Null(truncated.Summary);
    }

    [Fact]
    public async Task BlankLinesBetweenRecords_AreSkippedRatherThanReportedAsABrokenStream()
    {
        using YaguCliBridge bridge = Bridge();

        YaguCliResult result = await bridge.InvokeAsync(Scenario("blank-lines"), Generous, CancellationToken.None);

        Assert.Equal(YaguCliOutcome.Completed, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Single(result.Records, r => r.Type == MachineProtocol.Types.Match);
        Assert.NotNull(result.Summary);
    }

    [Fact]
    public void TheBridgeReportsWhetherModelWorkIsSerializedAcrossProcesses()
    {
        using YaguCliBridge bridge = Bridge();

        // Surfaced so a server can say so at startup: without the machine-wide gate, three clients can
        // each load a model whose measured peak working set is ~2.65 GB.
        Assert.True(bridge.HasMachineWideModelGate);
    }

    private static WindowsJobObject InvalidJob() => WindowsJobObject.CreateKillOnClose(
        isWindows: false,
        createJob: (_, _) => IntPtr.Zero,
        setInformation: (_, _, _, _) => false,
        assignProcess: (_, _) => false,
        closeHandle: _ => true);

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public bool CloseAttempted { get; private set; }

        protected override void Dispose(bool disposing)
        {
            CloseAttempted = true;
            throw new IOException("broken stdin");
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("broken stdout");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("broken stdout"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
