using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexBuildCoordinatorTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-coordinator", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _indexRoot;

    public IndexBuildCoordinatorTests()
    {
        _root = Path.Combine(_sandbox, "corpus");
        _indexRoot = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "the planner creates a staged content index");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "another planner document");
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task MissingWorker_FallsBackToTheSharedExecutor()
    {
        string missing = Path.Combine(_sandbox, "missing-worker.exe");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(missing));
        IndexBuildSuccess result = await coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), useWorker: true, CancellationToken.None);

        Assert.Equal(2, result.IndexedCount);
        Assert.Contains("2 indexed", result.Summary);
        Assert.NotNull(new ContentIndexStore(Paths(), result.ScopeId).TryOpenCurrent());

        IndexMaintenanceSuccess maintenance = await coordinator.RunMaintenancePreferWorkerAsync(
            new IndexMaintenanceOperation
            {
                StorageDirectory = _indexRoot,
                Mode = IndexMaintenanceOperation.ModeBuildDue,
                Settings = new IndexMaintenanceSettings(),
                Roots = new[] { new IndexMaintenanceRootOperation { Root = _root, Policy = BuildOperation().Policy } },
            }, true, CancellationToken.None);
        Assert.Equal(1, maintenance.Skipped);

        IndexValidationResult validation = await coordinator.ValidatePreferWorkerAsync(
            new IndexValidationOperation { StorageDirectory = _indexRoot, Root = _root },
            true,
            CancellationToken.None);
        Assert.True(validation.Valid);
    }

    [Fact]
    public async Task WorkerDisabled_RunsTheSameSharedExecutorInProcess()
    {
        var coordinator = new IndexBuildCoordinator();
        IndexBuildSuccess result = await coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), useWorker: false, CancellationToken.None);

        Assert.Equal(2, result.IndexedCount);
        Assert.Equal("gen-000001", result.ActiveBaseGenerationId);
        Assert.Equal("gen-000001", result.LastPublishedArtifactId);

        IndexMaintenanceSuccess maintenance = await coordinator.RunMaintenancePreferWorkerAsync(
            new IndexMaintenanceOperation
            {
                StorageDirectory = _indexRoot,
                Mode = IndexMaintenanceOperation.ModeBuildDue,
                Settings = new IndexMaintenanceSettings(),
                Roots = new[]
                {
                    new IndexMaintenanceRootOperation { Root = _root, Policy = BuildOperation().Policy },
                },
            },
            useWorker: false,
            CancellationToken.None);
        Assert.Equal(1, maintenance.Skipped);

        IndexValidationResult validation = await coordinator.ValidatePreferWorkerAsync(
            new IndexValidationOperation { StorageDirectory = _indexRoot, Root = _root },
            useWorker: false,
            CancellationToken.None);
        Assert.True(validation.Valid);
    }

    [Fact]
    public async Task BusyWorker_DoesNotFallBackOrDoubleWrite()
    {
        string? worker = FindWorkerExe();
        if (worker is null)
            return;

        using IndexMutationContext lease = IndexMutationContext.Acquire(Paths());
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(worker));

        await Assert.ThrowsAsync<IndexWriteBusyException>(() => coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), useWorker: true, CancellationToken.None));
        Assert.Null(new ContentIndexStore(Paths(), ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public async Task RealMaintenanceWorker_BuildsValidatesAndExitsReleasingTheLease()
    {
        string? worker = FindWorkerExe();
        if (worker is null)
            return;

        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(worker));
        var progress = new List<IndexBuildProgress>();
        IndexBuildSuccess result = await coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(),
            useWorker: true,
            CancellationToken.None,
            progress.Add);

        Assert.Equal(2, result.IndexedCount);
        Assert.NotEmpty(progress);
        Assert.NotNull(new ContentIndexStore(Paths(), result.ScopeId).TryOpenCurrent());

        IndexValidationResult validation = await coordinator.ValidatePreferWorkerAsync(
            new IndexValidationOperation
            {
                StorageDirectory = _indexRoot,
                Root = _root,
                RetainedGenerations = 2,
            },
            useWorker: true,
            CancellationToken.None);
        Assert.True(validation.Valid, validation.FailureReason);
        Assert.Equal(2, validation.DocumentCount);

        // Both short-lived workers exited and released the OS file lock before returning.
        using IndexMutationContext reacquired = IndexMutationContext.Acquire(Paths());
    }

    [Fact]
    public async Task RealMaintenanceWorker_BuildsMissingThenSkipsFreshRoot()
    {
        string? worker = FindWorkerExe();
        if (worker is null)
            return;

        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(worker));
        IndexMaintenanceOperation operation = new()
        {
            StorageDirectory = _indexRoot,
            RetainedGenerations = 2,
            Mode = IndexMaintenanceOperation.ModeBuildDue,
            Settings = new IndexMaintenanceSettings { BuildMemoryBudgetMB = 64 },
            Roots = new[]
            {
                new IndexMaintenanceRootOperation
                {
                    Root = _root,
                    Policy = BuildOperation().Policy,
                },
            },
        };

        IndexMaintenanceSuccess first = await coordinator.RunMaintenancePreferWorkerAsync(
            operation, useWorker: true, CancellationToken.None);
        Assert.Equal(1, first.Built);
        Assert.Equal(IndexMaintenanceActions.Built, Assert.Single(first.Roots).Action);

        IndexMaintenanceSuccess second = await coordinator.RunMaintenancePreferWorkerAsync(
            operation, useWorker: true, CancellationToken.None);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(IndexMaintenanceActions.Skipped, Assert.Single(second.Roots).Action);
    }

    [Fact]
    public async Task Cancellation_DoesNotFallBackOrPublishAStagedFirstBuild()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var coordinator = new IndexBuildCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), useWorker: false, cts.Token));
        Assert.Null(new ContentIndexStore(Paths(), ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public void OutcomeMapping_CoversFallbackTerminalAndEveryTypedFailure()
    {
        Assert.Throws<ArgumentNullException>(() => new IndexBuildCoordinator(null!));
        Assert.True(IndexBuildCoordinator.ShouldFallback(new IndexMaintenanceWorkerResult(false, false, true, null, "missing")));
        Assert.False(IndexBuildCoordinator.ShouldFallback(new IndexMaintenanceWorkerResult(true, true, true, null, "crashed")));
        Assert.True(IndexBuildCoordinator.ShouldFallback(new IndexMaintenanceWorkerResult(true, false, true, null, "crashed-before-accept")));
        Assert.False(IndexBuildCoordinator.ShouldFallback(new IndexMaintenanceWorkerResult(true, true, false, null, "running")));
        Assert.False(IndexBuildCoordinator.ShouldFallback(new IndexMaintenanceWorkerResult(true, false, false,
            new IndexWorkerMessage { Type = "result", OutcomeKind = "busy" }, null)));
        var expectedTerminal = new IndexWorkerMessage { Type = "result", Ok = true };
        Assert.Same(expectedTerminal, IndexBuildCoordinator.RequireTerminal(
            new IndexMaintenanceWorkerResult(true, true, false, expectedTerminal, null)));
        Assert.Throws<IOException>(() => IndexBuildCoordinator.RequireTerminal(
            new IndexMaintenanceWorkerResult(true, false, true, null, "gone")));
        IOException defaultTerminalError = Assert.Throws<IOException>(() => IndexBuildCoordinator.RequireTerminal(
            new IndexMaintenanceWorkerResult(true, false, true, null, null)));
        Assert.Contains("terminal result", defaultTerminalError.Message);

        IndexBuildCoordinator.ThrowIfFailed(new IndexWorkerMessage { Ok = true }, CancellationToken.None, _indexRoot);
        IndexBuildCoordinator.ThrowIfFailed(new IndexWorkerMessage { Ok = true, OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Ok }, CancellationToken.None, _indexRoot);
        Assert.Throws<OperationCanceledException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Cancelled }, CancellationToken.None, _indexRoot));
        IndexDiskFullException disk = Assert.Throws<IndexDiskFullException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage
            {
                OutcomeKind = IndexWorkerProtocol.OutcomeKinds.DiskFull,
                DriveName = "C:",
                UsedPercent = 91,
                ThresholdPercent = 90,
            }, CancellationToken.None, _indexRoot));
        Assert.Equal("C:", disk.DriveDisplayName);
        Assert.Throws<DirectoryNotFoundException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { OutcomeKind = IndexWorkerProtocol.OutcomeKinds.DirectoryNotFound }, CancellationToken.None, _indexRoot));
        Assert.Throws<IndexWriteBusyException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Busy }, CancellationToken.None, _indexRoot));
        Assert.Throws<InvalidDataException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Error, Error = "bad" }, CancellationToken.None, _indexRoot));
        Assert.Throws<InvalidDataException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { OutcomeKind = "unexpected", Error = null }, CancellationToken.None, _indexRoot));
        Assert.Throws<InvalidDataException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { Ok = true, OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Error }, CancellationToken.None, _indexRoot));
        IndexDiskFullException fallbackDrive = Assert.Throws<IndexDiskFullException>(() => IndexBuildCoordinator.ThrowIfFailed(
            new IndexWorkerMessage { OutcomeKind = IndexWorkerProtocol.OutcomeKinds.DiskFull }, CancellationToken.None, _indexRoot));
        Assert.False(string.IsNullOrWhiteSpace(fallbackDrive.DriveDisplayName));
    }

    [Fact]
    public async Task WorkerBackedBuildMaintenanceAndValidation_HonorCancellationWithoutFallback()
    {
        string worker = FakeWorker("acceptOnly");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(
            worker, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100)));

        using (var cts = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.BuildFullScopePreferWorkerAsync(
                BuildOperation(), true, cts.Token, _ => cts.Cancel()));
        }

        using (var cts = new CancellationTokenSource())
        {
            IndexMaintenanceOperation maintenance = new()
            {
                StorageDirectory = _indexRoot,
                Mode = IndexMaintenanceOperation.ModeBuildDue,
                Settings = new IndexMaintenanceSettings(),
                Roots = new[] { new IndexMaintenanceRootOperation { Root = _root, Policy = BuildOperation().Policy } },
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunMaintenancePreferWorkerAsync(
                maintenance, true, cts.Token, (_, _, _) => cts.Cancel()));
        }

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ValidatePreferWorkerAsync(
                new IndexValidationOperation { StorageDirectory = _indexRoot, Root = _root }, true, cts.Token));
        }
    }

    [Fact]
    public async Task WorkerBuild_RoutesPdfStageProgressSeparately()
    {
        string worker = FakeWorker("pdfNormal");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(
            worker, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        var raw = new List<IndexBuildProgress>();
        var pdf = new List<PdfBuildProgress>();

        IndexBuildSuccess result = await coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), true, CancellationToken.None, raw.Add, pdf.Add);

        Assert.Equal("ok", result.Summary);
        Assert.Empty(raw);
        Assert.Single(pdf);
    }

    [Fact]
    public async Task WorkerBuild_RoutesAndParsesPostBuildCatchUp()
    {
        string worker = FakeWorker("postBuildCatchUpNormal");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(
            worker, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        var catchUpProgress = new List<int>();

        IndexBuildSuccess result = await coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(),
            true,
            CancellationToken.None,
            postBuildCatchUpProgress: catchUpProgress.Add);

        Assert.Equal(new[] { 99 }, catchUpProgress);
        Assert.Equal(new PostBuildCatchUpResult(
            true,
            30_000,
            IncrementalUpdateOutcome.SegmentAppended,
            30_001,
            true,
            true), result.PostBuildCatchUp);
    }

    [Fact]
    public async Task WorkerBuild_RejectsUnknownCheckedPostBuildCatchUpOutcome()
    {
        string worker = FakeWorker("postBuildCatchUpInvalid");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(
            worker, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            coordinator.BuildFullScopePreferWorkerAsync(
                BuildOperation(), true, CancellationToken.None));

        Assert.Contains("post-build catch-up outcome", error.Message);
    }

    [Fact]
    public async Task AcceptedWorkerCrash_IsSurfacedAndNeverRetriedInProcess()
    {
        string worker = FakeWorker("acceptThenExit");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(
            worker, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));

        await Assert.ThrowsAsync<IOException>(() => coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), true, CancellationToken.None));

        Assert.Null(new ContentIndexStore(Paths(), ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public async Task WorkerResults_HandleNullOptionalBuildFieldsAndMissingMaintenanceJson()
    {
        string worker = FakeWorker("buildNullFields");
        var coordinator = new IndexBuildCoordinator(() => new IndexMaintenanceWorkerClient(
            worker, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        IndexBuildSuccess build = await coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), true, CancellationToken.None);
        Assert.Equal(ContentIndexManager.ScopeIdForRoot(_root), build.ScopeId);
        Assert.Equal(string.Empty, build.ActiveBaseGenerationId);
        Assert.Equal(string.Empty, build.LastPublishedArtifactId);
        Assert.Equal(string.Empty, build.Summary);

        IndexMaintenanceSuccess maintenance = await coordinator.RunMaintenancePreferWorkerAsync(
            new IndexMaintenanceOperation
            {
                StorageDirectory = _indexRoot,
                Mode = IndexMaintenanceOperation.ModeBuildDue,
                Settings = new IndexMaintenanceSettings(),
                Roots = new[] { new IndexMaintenanceRootOperation { Root = _root, Policy = BuildOperation().Policy } },
            },
            true,
            CancellationToken.None);
        Assert.Empty(maintenance.Roots);
    }

    private IndexBuildOperation BuildOperation() => new()
    {
        StorageDirectory = _indexRoot,
        RetainedGenerations = 2,
        Root = _root,
        Policy = IndexIngestionPolicySnapshot.FromPolicy(new IndexIngestionPolicy(
            0, null, null, includeHiddenFiles: true, followReparsePoints: false, maxDepth: 0)),
        BuildMemoryBudgetMB = 256,
        BuildParallelism = 4,
        MaxDiskUsagePercent = 0,
    };

    private IContentIndexPathProvider Paths() => new FixedContentIndexPathProvider(_indexRoot);

    private string FakeWorker(string scenario)
    {
        string sourceDirectory = FindFakeWorkerOutput();
        string executable = Path.Combine(_sandbox, $"coordinator-{scenario}.exe");
        foreach (string source in Directory.GetFiles(sourceDirectory))
        {
            string destination = Path.GetFileName(source).Equals("Yagu.FakeIndexWorker.exe", StringComparison.OrdinalIgnoreCase)
                ? executable
                : Path.Combine(_sandbox, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
        }
        File.WriteAllText(executable + ".scenario", scenario);
        return executable;
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

    private static string? FindWorkerExe()
    {
        string repoRoot = FindRepoRoot();
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(repoRoot, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Yagu.slnx.");
    }
}
