using System.Text.Json;
using Yagu.IndexWorker;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexWorkerBuildHostTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-host", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _indexRoot;

    public IndexWorkerBuildHostTests()
    {
        _root = Path.Combine(_sandbox, "root");
        _indexRoot = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "planner host build");
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void ValidateAcquireAndExecute_Build_StreamsProgressAndMapsSuccess()
    {
        IndexWorkerRequest request = BuildRequest();
        (IndexMutationContext mutation, object operation) = IndexWorkerBuildHost.ValidateAndAcquire(request);
        using (mutation)
        {
            var messages = new List<IndexWorkerMessage>();
            IndexWorkerMessage terminal = IndexWorkerBuildHost.Execute(
                request, mutation, operation, CancellationToken.None, messages.Add);

            Assert.True(terminal.Ok, terminal.Error);
            Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Ok, terminal.OutcomeKind);
            Assert.Equal(1, terminal.IndexedCount);
            Assert.NotEmpty(messages);
            Assert.All(messages, message => Assert.Equal(IndexWorkerProtocol.MessageTypes.Progress, message.Type));
        }
    }

    [Fact]
    public void ValidateAcquireAndExecute_MaintenanceAndValidation_MapAllFields()
    {
        var maintenance = new IndexMaintenanceOperation
        {
            StorageDirectory = _indexRoot,
            Mode = IndexMaintenanceOperation.ModeBuildDue,
            Settings = new IndexMaintenanceSettings { BuildMemoryBudgetMB = 64 },
            Roots = new[]
            {
                new IndexMaintenanceRootOperation
                {
                    Root = _root,
                    Policy = new IndexIngestionPolicySnapshot { IncludeHiddenFiles = true },
                },
            },
        };
        var request = new IndexWorkerRequest
        {
            Op = IndexWorkerProtocol.Ops.RefreshAuto,
            Id = 8,
            OperationJson = JsonSerializer.Serialize(maintenance, IndexOperationJsonContext.Default.IndexMaintenanceOperation),
        };
        (IndexMutationContext mutation, object operation) = IndexWorkerBuildHost.ValidateAndAcquire(request);
        using (mutation)
        {
            IndexWorkerMessage terminal = IndexWorkerBuildHost.Execute(
                request, mutation, operation, CancellationToken.None, _ => { });
            Assert.True(terminal.Ok, terminal.Error);
            Assert.Equal(1, terminal.Built);
            Assert.NotNull(terminal.MaintenanceResultJson);
        }

        var validation = new IndexValidationOperation { StorageDirectory = _indexRoot, Root = _root };
        request = new IndexWorkerRequest
        {
            Op = IndexWorkerProtocol.Ops.ValidateScope,
            Id = 9,
            OperationJson = JsonSerializer.Serialize(validation, IndexOperationJsonContext.Default.IndexValidationOperation),
        };
        (mutation, operation) = IndexWorkerBuildHost.ValidateAndAcquire(request);
        using (mutation)
        {
            IndexWorkerMessage terminal = IndexWorkerBuildHost.Execute(
                request, mutation, operation, CancellationToken.None, _ => { });
            Assert.True(terminal.Valid, terminal.FailureReason);
            Assert.Equal(1, terminal.DocumentCount);
            Assert.Equal(_root, terminal.RootPath);
        }
    }

    [Theory]
    [InlineData(IndexWorkerProtocol.Ops.BuildScope)]
    [InlineData(IndexWorkerProtocol.Ops.RefreshAuto)]
    [InlineData(IndexWorkerProtocol.Ops.ValidateScope)]
    public void ValidateAndAcquire_RejectsMissingOperationJson(string op)
    {
        var request = new IndexWorkerRequest { Op = op, Id = 1 };
        Assert.Throws<InvalidDataException>(() => IndexWorkerBuildHost.ValidateAndAcquire(request));
    }

    [Fact]
    public void ValidateAndAcquire_RejectsOversizedAndUnknownPayloads()
    {
        var oversized = new IndexWorkerRequest
        {
            Op = IndexWorkerProtocol.Ops.BuildScope,
            OperationJson = new string('x', IndexBuildDefaults.MaxOperationJsonBytes + 1),
        };
        Assert.Throws<InvalidDataException>(() => IndexWorkerBuildHost.ValidateAndAcquire(oversized));

        var unknown = new IndexWorkerRequest { Op = "unknown", OperationJson = "{}" };
        Assert.Throws<InvalidDataException>(() => IndexWorkerBuildHost.ValidateAndAcquire(unknown));

        foreach (string op in new[]
        {
            IndexWorkerProtocol.Ops.BuildScope,
            IndexWorkerProtocol.Ops.RefreshAuto,
            IndexWorkerProtocol.Ops.ValidateScope,
        })
        {
            Assert.ThrowsAny<Exception>(() => IndexWorkerBuildHost.ValidateAndAcquire(
                new IndexWorkerRequest { Op = op, OperationJson = "{" }));
            Assert.Throws<InvalidDataException>(() => IndexWorkerBuildHost.ValidateAndAcquire(
                new IndexWorkerRequest { Op = op, OperationJson = "null" }));
        }
    }

    [Fact]
    public void Execute_RejectsAnUnknownValidatedOperationObject()
    {
        var paths = new FixedContentIndexPathProvider(_indexRoot);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
        Assert.Throws<InvalidDataException>(() => IndexWorkerBuildHost.Execute(
            new IndexWorkerRequest { Id = 1 },
            mutation,
            new object(),
            CancellationToken.None,
            _ => { }));
    }

    [Fact]
    public void ValidateAndAcquire_MapsLeaseContentionToBusy()
    {
        var paths = new FixedContentIndexPathProvider(_indexRoot);
        using IndexMutationContext held = IndexMutationContext.Acquire(paths);
        Assert.Throws<IndexWriteBusyException>(() => IndexWorkerBuildHost.ValidateAndAcquire(BuildRequest()));
    }

    [Fact]
    public void MapFailure_PreservesEveryTypedOutcome()
    {
        IndexWorkerMessage cancelled = IndexWorkerBuildHost.MapFailure(1, new OperationCanceledException());
        IndexWorkerMessage busy = IndexWorkerBuildHost.MapFailure(2, new IndexWriteBusyException(_indexRoot));
        IndexWorkerMessage disk = IndexWorkerBuildHost.MapFailure(3, new IndexDiskFullException("C:", 91.5, 90));
        IndexWorkerMessage missing = IndexWorkerBuildHost.MapFailure(4, new DirectoryNotFoundException("missing"));
        IndexWorkerMessage error = IndexWorkerBuildHost.MapFailure(5, new InvalidDataException("bad"));

        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Cancelled, cancelled.OutcomeKind);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Busy, busy.OutcomeKind);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.DiskFull, disk.OutcomeKind);
        Assert.Equal("C:", disk.DriveName);
        Assert.Equal(91.5, disk.UsedPercent);
        Assert.Equal(90, disk.ThresholdPercent);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.DirectoryNotFound, missing.OutcomeKind);
        Assert.Equal(IndexWorkerProtocol.OutcomeKinds.Error, error.OutcomeKind);
    }

    private IndexWorkerRequest BuildRequest()
    {
        var operation = new IndexBuildOperation
        {
            StorageDirectory = _indexRoot,
            Root = _root,
            Policy = new IndexIngestionPolicySnapshot { IncludeHiddenFiles = true },
            BuildMemoryBudgetMB = 64,
        };
        return new IndexWorkerRequest
        {
            Op = IndexWorkerProtocol.Ops.BuildScope,
            Id = 7,
            OperationJson = JsonSerializer.Serialize(operation, IndexOperationJsonContext.Default.IndexBuildOperation),
        };
    }
}
