using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexMaintenanceWorkerClientTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-fake-index-worker", Guid.NewGuid().ToString("N"));

    public IndexMaintenanceWorkerClientTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task NormalProtocol_AcceptsReportsProgressReturnsTerminalAndDisposesIdempotently()
    {
        string worker = FakeWorker("normal");
        var progress = new List<IndexWorkerMessage>();
        var client = NewClient(worker);

        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), progress.Add, CancellationToken.None);

        Assert.True(result.WorkerStarted, result.Failure);
        Assert.True(result.Accepted);
        Assert.True(result.WorkerExited);
        Assert.True(result.Terminal!.Ok);
        Assert.Single(progress);
        Assert.Equal(50, progress[0].Percent);
        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task TerminalRejectionBeforeAcceptance_IsReturnedAndNotTreatedAsUnavailable()
    {
        await using var client = NewClient(FakeWorker("rejectBusy"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.True(result.WorkerStarted);
        Assert.False(result.Accepted);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Busy, result.Terminal!.OutcomeKind);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("nullMessage")]
    [InlineData("progressBeforeAccepted")]
    [InlineData("duplicateAccepted")]
    [InlineData("unknownMessage")]
    public async Task ProtocolViolations_AreFatalAndNeverHang(string scenario)
    {
        await using var client = NewClient(FakeWorker(scenario));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.True(result.WorkerStarted);
        Assert.True(result.WorkerExited);
        Assert.Null(result.Terminal);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("initError")]
    [InlineData("initErrorNoText")]
    public async Task InvalidHandshake_ReportsUnavailableBeforeAcceptance(string scenario)
    {
        await using var client = NewClient(FakeWorker(scenario));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.False(result.WorkerStarted);
        Assert.False(result.Accepted);
        Assert.Null(result.Terminal);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task AcceptanceDeadline_TerminatesSilentWorker()
    {
        await using var client = NewClient(FakeWorker("silent"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);

        Assert.True(result.WorkerStarted);
        Assert.False(result.Accepted);
        Assert.True(result.WorkerExited);
        Assert.Contains("accept", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationBeforeAcceptance_SendsTargetedCancelWithoutFallback()
    {
        string worker = FakeWorker("silent");
        await using var client = NewClient(worker);
        using var cts = new CancellationTokenSource();
        Task<IndexMaintenanceWorkerResult> execution = client.ExecuteAsync(Request(), null, cts.Token);
        for (int i = 0; i < 100 && !File.Exists(worker + ".request"); i++)
            await Task.Delay(10);
        Assert.True(File.Exists(worker + ".request"), "Fake worker did not receive the operation request.");
        cts.Cancel();
        IndexMaintenanceWorkerResult result = await execution;
        Assert.False(result.Accepted);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Cancelled, result.Terminal!.OutcomeKind);
    }

    [Fact]
    public async Task Cancellation_SendsTargetedCancelAndReceivesTerminalCancellation()
    {
        await using var client = NewClient(FakeWorker("acceptOnly"));
        using var cts = new CancellationTokenSource();
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), _ => cts.Cancel(), cts.Token);

        Assert.True(result.Accepted);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Cancelled, result.Terminal!.OutcomeKind);
    }

    [Fact]
    public async Task CancellationGrace_KillsAWorkerThatIgnoresCancel()
    {
        await using var client = NewClient(FakeWorker("ignoreCancel"));
        using var cts = new CancellationTokenSource();
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), _ => cts.Cancel(), cts.Token);

        Assert.True(result.Accepted);
        Assert.True(result.WorkerExited);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Cancelled, result.Terminal!.OutcomeKind);
    }

    [Fact]
    public async Task CancellationSurvivesACancelWriteFailure()
    {
        await using var client = NewClient(FakeWorker("closeInput"));
        using var cts = new CancellationTokenSource();
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), _ => cts.Cancel(), cts.Token);
        Assert.True(result.WorkerExited);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Cancelled, result.Terminal!.OutcomeKind);
    }

    [Fact]
    public async Task ProgressCallbackException_DoesNotStopTheReader()
    {
        await using var client = NewClient(FakeWorker("stderr"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(
            Request(), _ => throw new InvalidOperationException("UI callback failed"), CancellationToken.None);

        Assert.True(result.Terminal!.Ok);
    }

    [Theory]
    [InlineData("blankNormal")]
    [InlineData("lateUnknown")]
    public async Task BenignBlankAndLateUnknownMessages_DoNotStopTheReader(string scenario)
    {
        await using var client = NewClient(FakeWorker(scenario));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.True(result.Terminal!.Ok, result.Failure);
    }

    [Fact]
    public async Task DuplicateTerminal_IsDetectedWithoutHanging()
    {
        await using var client = NewClient(FakeWorker("duplicateTerminal"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.True(result.WorkerExited);
        Assert.NotNull(result.Terminal);
    }

    [Fact]
    public async Task ExitImmediatelyAfterReady_FailsTheOperationPromptly()
    {
        await using var client = NewClient(FakeWorker("exitAfterReady"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.True(result.WorkerExited);
        Assert.Null(result.Terminal);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task WorkerDiesWithoutTerminal_DoesNotLeaveAnUnobservedTaskException()
    {
        // FailChannel faults the accepted/terminal completion sources when the worker's
        // output stream closes before a terminal result. ExecuteAsync only inspects their
        // Status/Result, so without an explicit fault observer the faulted task's exception
        // is rethrown by the finalizer as an UnobservedTaskException (seen in production as
        // "maintenance worker output stream closed").
        var leaked = new List<Exception>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (Exception inner in e.Exception.Flatten().InnerExceptions)
            {
                if (inner is IOException io && io.Message.Contains("maintenance worker", StringComparison.Ordinal))
                {
                    lock (leaked)
                        leaked.Add(inner);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await RunExitAfterReadyAndReleaseClientAsync();

            // Force the faulted TaskExceptionHolder finalizers to run: without the fix this
            // is when an unobserved fault would be reported.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        Assert.Empty(leaked);
    }

    // Runs and fully releases the client in a nested scope so its faulted completion-source
    // tasks become collectible (and thus finalizable) before the caller forces a GC.
    private async Task RunExitAfterReadyAndReleaseClientAsync()
    {
        // The worker accepts then closes its output stream without a terminal result, which is
        // the exact production path that faults the pending _terminal source via
        // FailChannel("maintenance worker output stream closed").
        var client = NewClient(FakeWorker("acceptThenExit"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.True(result.Accepted);
        Assert.Null(result.Terminal);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task MissingWorkerAndNonWorkerExecutable_FailBeforeAcceptance()
    {
        await using (var missing = NewClient(Path.Combine(_sandbox, "missing.exe")))
        {
            IndexMaintenanceWorkerResult result = await missing.ExecuteAsync(Request(), null, CancellationToken.None);
            Assert.False(result.WorkerStarted);
        }

        await using var invalid = NewClient(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        IndexMaintenanceWorkerResult invalidResult = await invalid.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.False(invalidResult.WorkerStarted);
    }

    [Fact]
    public async Task UntrustedWorker_IsRejectedBeforeLaunch()
    {
        static bool Reject(string _, out string failure) { failure = "fake trust rejection"; return false; }
        string worker = FakeWorker("normal");
        await using var client = new IndexMaintenanceWorkerClient(
            worker,
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100),
            skipTrust: false,
            trustVerifier: Reject);
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.False(result.WorkerStarted);
        Assert.Contains("trust", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(worker + ".request"));
    }

    [Fact]
    public void DisposeWithoutStart_IsIdempotent()
    {
        var client = NewClient(Path.Combine(_sandbox, "missing.exe"));
        client.Dispose();
        client.Dispose();

        var productionClient = new IndexMaintenanceWorkerClient();
        productionClient.Dispose();
    }

    [Fact]
    public async Task ShutdownTimeoutForcesExit_AndSynchronousDisposeCleansProcessObjects()
    {
        var client = NewClient(FakeWorker("ignoreShutdown"));
        IndexMaintenanceWorkerResult result = await client.ExecuteAsync(Request(), null, CancellationToken.None);
        Assert.True(result.WorkerExited);
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void ResolveWorkerPath_CoversOverrideEnvironmentBundledAndMissingProbes()
    {
        const string app = @"C:\app";
        Assert.Equal("override", IndexMaintenanceWorkerClient.ResolveWorkerPath(
            true, "override", "environment", app, path => path == "override"));
        Assert.Null(IndexMaintenanceWorkerClient.ResolveWorkerPath(
            true, "missing", "environment", app, _ => false));
        Assert.Equal("environment", IndexMaintenanceWorkerClient.ResolveWorkerPath(
            false, null, "environment", app, path => path == "environment"));
        string bundled = Path.Combine(app, "index-worker", "Yagu.IndexWorker.exe");
        Assert.Equal(bundled, IndexMaintenanceWorkerClient.ResolveWorkerPath(
            false, null, null, app, path => path == bundled));
        Assert.Null(IndexMaintenanceWorkerClient.ResolveWorkerPath(false, null, null, app, _ => false));
        Assert.Throws<ArgumentException>(() => IndexMaintenanceWorkerClient.ResolveWorkerPath(false, null, null, " ", _ => false));
        Assert.Throws<ArgumentNullException>(() => IndexMaintenanceWorkerClient.ResolveWorkerPath(false, null, null, app, null!));
    }

    [Fact]
    public async Task ProcessShellHelpers_CoverTrustExitAndUninitializedStreams()
    {
        static bool Trusted(string _, out string failure) { failure = ""; return true; }
        static bool Untrusted(string _, out string failure) { failure = "untrusted"; return false; }

        Assert.True(IndexMaintenanceWorkerClient.IsWorkerTrusted(true, "worker", Untrusted, out string overrideFailure));
        Assert.Empty(overrideFailure);
        Assert.True(IndexMaintenanceWorkerClient.IsWorkerTrusted(false, "worker", Trusted, out _));
        Assert.False(IndexMaintenanceWorkerClient.IsWorkerTrusted(false, "worker", Untrusted, out string failure));
        Assert.Equal("untrusted", failure);
        Assert.Throws<ArgumentNullException>(() => IndexMaintenanceWorkerClient.IsWorkerTrusted(false, "worker", null!, out _));

        Assert.False(IndexMaintenanceWorkerClient.HasExited(() => false));
        Assert.True(IndexMaintenanceWorkerClient.HasExited(() => true));
        Assert.True(IndexMaintenanceWorkerClient.HasExited(() => throw new InvalidOperationException()));
        Assert.Throws<ArgumentNullException>(() => IndexMaintenanceWorkerClient.HasExited(null!));

        var client = NewClient(Path.Combine(_sandbox, "missing.exe"));
        await Assert.ThrowsAsync<IOException>(() => client.WriteRequestAsync(Request(), CancellationToken.None));
        await client.SendInitialRequestAsync(Request()); // converts the same write failure into channel failure
        await client.SendCancelBestEffortAsync(1); // cancel send is explicitly best-effort
        var stream = new MemoryStream();
        var reader = new StreamReader(stream);
        reader.Dispose();
        await client.PumpStandardErrorAsync(reader); // defensive catch: disposed reader
        client.Dispose();
    }

    private IndexMaintenanceWorkerClient NewClient(string worker)
        => new(worker, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

    private static IndexWorkerRequest Request() => new()
    {
        Op = IndexWorkerProtocol.Ops.BuildScope,
        OperationJson = "{}",
    };

    private string FakeWorker(string scenario)
    {
        string sourceDir = FindFakeWorkerOutput();
        string exe = Path.Combine(_sandbox, $"fake-{scenario}.exe");
        foreach (string source in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(source);
            string destination = name.Equals("Yagu.FakeIndexWorker.exe", StringComparison.OrdinalIgnoreCase)
                ? exe
                : Path.Combine(_sandbox, name);
            File.Copy(source, destination, overwrite: true);
        }
        File.WriteAllText(exe + ".scenario", scenario);
        return exe;
    }

    private static string FindFakeWorkerOutput()
    {
        string repo = FindRepoRoot();
        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string directory = Path.Combine(repo, "tests", "Yagu.FakeIndexWorker", "bin", configuration, "net10.0");
            if (File.Exists(Path.Combine(directory, "Yagu.FakeIndexWorker.exe")))
                return directory;
        }
        throw new FileNotFoundException("The fake index worker was not built.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Yagu.slnx.");
    }
}
