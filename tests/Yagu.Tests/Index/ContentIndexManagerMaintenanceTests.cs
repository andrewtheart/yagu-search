using System.Text;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ContentIndexManagerMaintenanceTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _file;
    private readonly string _indexRoot;
    private readonly IContentIndexPathProvider _paths;

    public ContentIndexManagerMaintenanceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-manager-maintenance", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "corpus");
        _file = Path.Combine(_root, "file.txt");
        _indexRoot = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(_root);
        File.WriteAllText(_file, "indexed content here", new UTF8Encoding(false));
        _paths = new DefaultContentIndexPathProvider(_indexRoot, _indexRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void GetScopeFreshnessStatus_DistinguishesMissingFreshAndDirty()
    {
        var manager = new ContentIndexManager(_paths);
        ContentIndexManager.ScopeFreshnessStatus missing = manager.GetScopeFreshnessStatus(
            Path.Combine(_sandbox, "missing"), FreshReader);
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Missing, missing.State);
        Assert.False(missing.NeedsAttention);
        Assert.False(missing.NeedsUpdate);
        Assert.False(manager.IsScopeStale(Path.Combine(_sandbox, "missing"), FreshReader));

        UsnFileIdentity identity = PublishGeneration();
        ContentIndexManager.ScopeFreshnessStatus fresh = manager.GetScopeFreshnessStatus(_root, FreshReader);
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Fresh, fresh.State);
        Assert.Null(fresh.Problem);
        Assert.Equal(0, fresh.DirtyCount);

        ContentIndexManager.ScopeFreshnessStatus dirty = manager.GetScopeFreshnessStatus(_root, DirtyReader(identity));
        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Dirty, dirty.State);
        Assert.True(dirty.NeedsUpdate);
        Assert.False(dirty.NeedsAttention);
        Assert.Equal(1, dirty.DirtyCount);
        Assert.True(manager.IsScopeStale(_root, DirtyReader(identity)));
    }

    [Theory]
    [InlineData(UsnReadStatus.CheckpointAhead, true, "ahead")]
    [InlineData(UsnReadStatus.JournalIdChanged, true, "reset")]
    [InlineData(UsnReadStatus.GapDetected, true, "contains every change")]
    [InlineData(UsnReadStatus.UnknownRecordVersion, false, "unsupported")]
    [InlineData(UsnReadStatus.Error, false, "could not be read")]
    [InlineData(UsnReadStatus.IoTimeout, false, "did not answer")]
    [InlineData(UsnReadStatus.Unavailable, false, "no usable")]
    [InlineData(UsnReadStatus.Incomplete, false, "Increase the limit")]
    [InlineData(UsnReadStatus.VolumeMismatch, false, "not the volume")]
    public void GetScopeFreshnessStatus_ExplainsJournalFailures(
        UsnReadStatus rawStatus,
        bool requiresRebuild,
        string expectedProblem)
    {
        PublishGeneration();
        var manager = new ContentIndexManager(_paths);
        ContentIndexFreshnessEvaluator.JournalReader reader = (_, since) =>
            new UsnReadResult(rawStatus, since, Array.Empty<UsnChange>());

        ContentIndexManager.ScopeFreshnessStatus status = manager.GetScopeFreshnessStatus(_root, reader);

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Uncertain, status.State);
        Assert.Equal(rawStatus, status.RawStatus);
        Assert.Equal(requiresRebuild, status.RequiresRebuild);
        Assert.True(status.NeedsAttention);
        Assert.Contains(expectedProblem, status.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetScopeFreshnessStatus_IdentityMismatchRequestsCompatibilityRebuild()
    {
        PublishGeneration(identityHigh: 0x600);
        ContentIndexFreshnessEvaluator.JournalReader reader = (_, since) => new UsnReadResult(
            UsnReadStatus.Ok,
            new UsnCheckpoint(since.JournalId, since.NextUsn + 1),
            [new UsnChange(new UsnFileIdentity(0x3000000000067600, 0), 1)]);

        ContentIndexManager.ScopeFreshnessStatus status = new ContentIndexManager(_paths)
            .GetScopeFreshnessStatus(_root, reader);

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Dirty, status.State);
        Assert.Equal(UsnReadStatus.IdentityMismatch, status.RawStatus);
        Assert.True(status.RequiresRebuild);
        Assert.Equal(1, status.DirtyCount);
        Assert.Contains("older file-identity format", status.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetScopeFreshnessStatus_CheckpointInvalidReflectsFilesystemCapability(bool supportsJournal)
    {
        PublishGeneration(checkpoint: UsnCheckpoint.None);
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            changeJournalSupportReader: _ => supportsJournal);

        ContentIndexManager.ScopeFreshnessStatus status = manager.GetScopeFreshnessStatus(_root, FreshReader);

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Uncertain, status.State);
        Assert.Equal(supportsJournal, status.RequiresRebuild);
        Assert.Contains(supportsJournal ? "Rebuild required" : "cannot be freshness-validated",
            status.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetScopeFreshnessStatus_WholeVolumeCountsUnknownChanges()
    {
        string volumeRoot = Path.GetPathRoot(_root)!;
        PublishGeneration(root: volumeRoot);
        var unknown = new UsnFileIdentity(99_999, 0);

        ContentIndexManager.ScopeFreshnessStatus status = new ContentIndexManager(_paths)
            .GetScopeFreshnessStatus(volumeRoot, (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 1),
                [new UsnChange(unknown, 1)]));

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Dirty, status.State);
        Assert.Equal(1, status.DirtyCount);
    }

    [Fact]
    public void GetScopeFreshnessStatus_BoundVolumeUnavailableOrChangedFailsClosed()
    {
        VolumeBinding binding = VolumeBindingReader.TryCapture(_root)
            ?? throw new InvalidOperationException("The test volume could not be identified.");
        PublishGeneration(binding: binding);

        var unavailable = new ContentIndexManager(_paths, 2, contentReader: null,
            volumeBindingReader: _ => null);
        ContentIndexManager.ScopeFreshnessStatus disconnected = unavailable.GetScopeFreshnessStatus(_root, FreshReader);
        Assert.Equal(UsnReadStatus.VolumeMismatch, disconnected.RawStatus);
        Assert.False(disconnected.RequiresRebuild);
        Assert.Contains("disconnected", disconnected.Problem, StringComparison.OrdinalIgnoreCase);

        var changed = new ContentIndexManager(_paths, 2, contentReader: null,
            volumeBindingReader: _ => binding with { VolumeSerialNumber = binding.VolumeSerialNumber + 1 });
        ContentIndexManager.ScopeFreshnessStatus mismatch = changed.GetScopeFreshnessStatus(_root, FreshReader);
        Assert.Equal(UsnReadStatus.VolumeMismatch, mismatch.RawStatus);
        Assert.True(mismatch.RequiresRebuild);
        Assert.Contains("does not match", mismatch.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetScopeFreshnessStatus_UnexpectedFailureReturnsUncertain()
    {
        var manager = new ContentIndexManager(new ThrowingPathProvider(_indexRoot));

        ContentIndexManager.ScopeFreshnessStatus status = manager.GetScopeFreshnessStatus(_root, FreshReader);

        Assert.Equal(ContentIndexManager.ScopeFreshnessState.Uncertain, status.State);
        Assert.Equal(UsnReadStatus.Error, status.RawStatus);
        Assert.Contains(nameof(InvalidOperationException), status.Problem);
    }

    [Fact]
    public void TryReanchorFreshScope_AdvancesOnlyContinuousCleanBase()
    {
        PublishGeneration();
        var manager = new ContentIndexManager(_paths);

        Assert.True(manager.TryReanchorFreshScope(_root, FreshReader));
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        Assert.Equal(new UsnCheckpoint(1, 110),
            store.TryReadCurrentFreshnessInputs()!.Value.Manifest.FreshnessCheckpoint);

        Assert.False(manager.TryReanchorFreshScope(_root, DirtyReader(new UsnFileIdentity(4242, 0))));
        Assert.False(manager.TryReanchorFreshScope(_root, (_, since) =>
            new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>())));
        Assert.False(manager.TryReanchorFreshScope(Path.Combine(_sandbox, "missing"), FreshReader));
    }

    [Fact]
    public void TryReanchorFreshScope_WholeVolumeUnknownChangeDoesNotAdvance()
    {
        string volumeRoot = Path.GetPathRoot(_root)!;
        PublishGeneration(root: volumeRoot);

        bool advanced = new ContentIndexManager(_paths).TryReanchorFreshScope(
            volumeRoot,
            (_, since) => new UsnReadResult(
                UsnReadStatus.Ok,
                new UsnCheckpoint(since.JournalId, since.NextUsn + 1),
                [new UsnChange(new UsnFileIdentity(99_999, 0), 1)]));

        Assert.False(advanced);
    }

    [Fact]
    public void ValidateScopeUnderLease_ReportsMissingAndValidScopes()
    {
        var manager = new ContentIndexManager(_paths);
        using (IndexMutationContext mutation = IndexMutationContext.Acquire(_paths))
        {
            IndexValidationResult missing = manager.ValidateScopeUnderLease(mutation, _root);
            Assert.False(missing.Valid);
            Assert.Contains("No index", missing.FailureReason, StringComparison.OrdinalIgnoreCase);
        }

        PublishGeneration();
        using IndexMutationContext validationMutation = IndexMutationContext.Acquire(_paths);
        IndexValidationResult valid = manager.ValidateScopeUnderLease(validationMutation, _root);
        Assert.True(valid.Valid);
        Assert.Equal(1, valid.DocumentCount);
        Assert.Equal(0, valid.SegmentCount);
        Assert.Equal(IndexScopeIdentity.NormalizePath(_root), valid.RootPath);
    }

    [Fact]
    public void ResolveBestAvailableIndexRoot_FailuresAndNullRegistrationDegradeToSearchRoot()
    {
        string normalized = IndexScopeIdentity.NormalizePath(_root);
        var throwing = new ContentIndexManager(new ThrowingIoPathProvider(_indexRoot));

        Assert.Equal(normalized, throwing.ResolveBestAvailableIndexRoot(_root, [Path.GetPathRoot(_root)!]));
        Assert.Equal(normalized, new ContentIndexManager(_paths).ResolveBestAvailableIndexRoot(_root, null));
    }

    [Theory]
    [InlineData("NTFS", true)]
    [InlineData("refs", true)]
    [InlineData("FAT32", false)]
    [InlineData(null, false)]
    public void VolumeFormatSupportsChangeJournal_RecognizesTrustedFormats(string? format, bool expected)
        => Assert.Equal(expected, ContentIndexManager.VolumeFormatSupportsChangeJournal(format));

    [Fact]
    public void VolumeSupportsChangeJournal_RejectsUnrootedAndInvalidPaths()
    {
        Assert.False(ContentIndexManager.VolumeSupportsChangeJournal("relative"));
        Assert.False(ContentIndexManager.VolumeSupportsChangeJournal("\0"));
        Assert.True(ContentIndexManager.VolumeSupportsChangeJournal(_root));

        string? unusedRoot = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(value => $"{(char)value}:\\")
            .FirstOrDefault(candidate => !DriveInfo.GetDrives().Any(
                drive => string.Equals(drive.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        if (unusedRoot is not null)
            Assert.False(ContentIndexManager.VolumeSupportsChangeJournal(unusedRoot));
    }

    [Fact]
    public void PureHelpers_CoverFallbackAndDescendantDepth()
    {
        Assert.Equal(
            "Index freshness cannot currently be proven. Searches safely scan live.",
            ContentIndexManager.DescribeFreshnessProblem(
                RootFreshnessVerdict.JournalDiscontinuity, UsnReadStatus.Ok, requiresRebuild: false));
        Assert.Equal(1, ContentIndexManager.DepthUnder(_root, Path.Combine(_root, "file.txt")));
        Assert.Equal(2, ContentIndexManager.DepthUnder(_root, Path.Combine(_root, "sub", "file.txt")));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexManager(null!));
    }

    [Fact]
    public void FileSystemHelpers_FailOpenAcrossExpectedErrors()
    {
        Assert.Empty(ContentIndexManager.SafeGetDirectories(() => false, () => throw new InvalidOperationException()));
        Assert.Equal(["a", "b"], ContentIndexManager.SafeGetDirectories(() => true, () => ["a", "b"]));
        foreach (Exception error in new Exception[] { new IOException("io"), new UnauthorizedAccessException("denied") })
        {
            Assert.Empty(ContentIndexManager.SafeGetDirectories(
                () => throw error,
                () => throw new InvalidOperationException()));
        }

        Assert.Equal(30, ContentIndexManager.DirectorySizeBytes(() => ["a", "b"], file => file == "a" ? 10 : 20));
        foreach (Exception error in new Exception[] { new IOException("vanished"), new UnauthorizedAccessException("locked") })
        {
            Assert.Equal(10, ContentIndexManager.DirectorySizeBytes(
                () => ["a", "b"],
                file => file == "a" ? 10 : throw error));
            Assert.Equal(0, ContentIndexManager.DirectorySizeBytes(
                () => throw error,
                _ => throw new InvalidOperationException()));
        }
    }

    [Fact]
    public void RealDiskUsedPercent_HandlesDriveStatesAndExpectedErrors()
    {
        Assert.Equal(75, ContentIndexManager.RealDiskUsedPercent(
            _root, _ => (IsReady: true, TotalSize: 100, AvailableFreeSpace: 25)));
        Assert.Null(ContentIndexManager.RealDiskUsedPercent(
            _root, _ => (IsReady: false, TotalSize: 100, AvailableFreeSpace: 25)));
        Assert.Null(ContentIndexManager.RealDiskUsedPercent(
            _root, _ => (IsReady: true, TotalSize: 0, AvailableFreeSpace: 0)));

        foreach (Exception error in new Exception[]
                 {
                     new ArgumentException("bad path"),
                     new IOException("io"),
                     new NotSupportedException("unsupported"),
                     new UnauthorizedAccessException("denied"),
                 })
        {
            Assert.Null(ContentIndexManager.RealDiskUsedPercent(
                _root,
                _ => throw error));
        }
    }

    [Fact]
    public void MetadataAndStorageStats_ReportMissingSource()
    {
        var emptyManager = new ContentIndexManager(_paths);
        Assert.False(emptyManager.HasReadableStoredIndex());
        Assert.Empty(emptyManager.GetReusableStoredIndexRoots());
        PublishGeneration();
        Assert.Equal([_root], new ContentIndexManager(_paths).GetReusableStoredIndexRoots());
        Directory.Delete(_root, recursive: true);
        var manager = new ContentIndexManager(_paths);

        Assert.True(manager.HasReadableStoredIndex());
        Assert.Empty(manager.GetReusableStoredIndexRoots());
        IndexMetadataStatus metadata = manager.GetMetadataStatusForRoot(_root);
        Assert.Equal(IndexStorageHealth.SourceMissing, metadata.Health);
        Assert.Contains("no longer exists", metadata.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.HasCurrentIndex(_root));

        IndexStorageStat storage = Assert.Single(manager.GetStorageStats().Indexes);
        Assert.Equal(IndexStorageHealth.SourceMissing, storage.Health);
        Assert.False(storage.RootExists);
        Assert.True(storage.Readable);
    }

    [Fact]
    public void MetadataAndStorageStats_HandleEmptyScopeDirectory()
    {
        string scopeId = new('c', 32);
        Directory.CreateDirectory(_paths.GetScopeDirectory(scopeId));
        var manager = new ContentIndexManager(_paths);

        Assert.False(manager.HasReadableStoredIndex());
        IndexMetadataStatus metadata = manager.GetMetadataStatus(scopeId);
        Assert.False(metadata.Exists);
        Assert.False(metadata.MetadataReadable);

        IndexStorageStat storage = Assert.Single(manager.GetStorageStats().Indexes);
        Assert.Null(storage.RootPath);
        Assert.False(storage.RootExists);
        Assert.False(storage.Readable);
    }

    [Fact]
    public void ValidateScopeUnderLease_CountsDeltaSegments()
    {
        PublishGeneration();
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        string addedPath = Path.Combine(_root, "added.txt");
        var builder = new ContentIndexGenerationBuilder(Policy());
        builder.AddDocument(addedPath, Encoding.UTF8.GetBytes("added indexed content"));
        ContentIndexGeneration added = builder.Build(
            scopeId, "volume", _root, new UsnCheckpoint(1, 110), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, scopeId).PublishSegment(
            new ContentIndexDeltaSegment(added, Array.Empty<string>()));

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexValidationResult result = new ContentIndexManager(_paths)
            .ValidateScopeUnderLease(mutation, _root);

        Assert.True(result.Valid);
        Assert.Equal(2, result.DocumentCount);
        Assert.Equal(1, result.SegmentCount);
    }

    [Fact]
    public void ActiveLayerHelpers_ReportBreakdownTrendAndReclamationWithoutOpeningContent()
    {
        PublishGeneration();
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        DateTimeOffset incrementalUtc = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var segmentBuilder = new ContentIndexGenerationBuilder(Policy());
        segmentBuilder.AddDocument(
            Path.Combine(_root, "added.txt"),
            Encoding.UTF8.GetBytes("added indexed content"));
        ContentIndexGeneration added = segmentBuilder.Build(
            scopeId,
            "volume",
            _root,
            new UsnCheckpoint(1, 110),
            incrementalUtc,
            lastIncrementalUpdateUtc: incrementalUtc);
        new ContentIndexStore(_paths, scopeId).PublishSegment(
            new ContentIndexDeltaSegment(added, Array.Empty<string>()));

        var manager = new ContentIndexManager(_paths);
        ActiveLayerStorageBreakdown? breakdown = manager.TryReadActiveLayerStorageBreakdownForRoot(_root);
        ActiveLayerStorageTrend? trend = manager.TryReadActiveLayerStorageTrendForRoot(_root);
        IndexReclamationDiagnosis diagnosis = manager.DiagnoseReclamation(
            _root,
            EffectiveIndexSizePolicy.Default,
            maxDeltaSegments: 8,
            compactionThresholdMB: 256);

        Assert.NotNull(breakdown);
        Assert.Equal(1, breakdown!.Value.BaseCount);
        Assert.Equal(1, breakdown.Value.IncrementalCount);
        Assert.NotNull(trend);
        Assert.Equal(breakdown.Value, trend!.Value.Breakdown);
        Assert.Equal(incrementalUtc, trend.Value.OldestIncrementalBuiltUtc);
        Assert.False(diagnosis.ReclamationBlocked);
        Assert.Null(manager.TryReadActiveLayerStorageBreakdownForRoot("   "));
        Assert.Null(manager.TryReadActiveLayerStorageTrendForRoot("   "));
        Assert.Equal(IndexReclamationDiagnosis.Healthy, manager.DiagnoseReclamation(
            "   ", EffectiveIndexSizePolicy.Default, 8, 256));
        Assert.Equal(IndexReclamationDiagnosis.Healthy, manager.DiagnoseReclamation(
            Path.Combine(_sandbox, "not-indexed"), EffectiveIndexSizePolicy.Default, 8, 256));
        EffectiveIndexSizePolicy noCleanup = EffectiveIndexSizePolicy.Default with
        {
            Mode = IndexSizeManagementModes.Off,
        };
        Assert.Equal(IndexReclamationDiagnosis.Healthy, manager.DiagnoseReclamation(
            _root, noCleanup, 8, 256));
    }

    [Fact]
    public void MaintenanceMethods_OrdinaryProviderFailuresReturnFalse()
    {
        var manager = new ContentIndexManager(new ThrowingPathProvider(_indexRoot));

        Assert.False(manager.TryReanchorFreshScope(_root, FreshReader));
        Assert.False(manager.CompactScopeIfOverSegmented(
            _root, Policy(), new IndexMaintenanceSettings(), DateTimeOffset.UtcNow));
        Assert.Null(manager.TryReadActiveLayerStorageBreakdownForRoot(_root));
        Assert.Null(manager.TryReadActiveLayerStorageTrendForRoot(_root));
        Assert.Equal(IndexReclamationDiagnosis.Healthy, manager.DiagnoseReclamation(
            _root, EffectiveIndexSizePolicy.Default, 8, 256));
    }

    [Fact]
    public void ClearAll_LockedScopeIsLeftInPlaceAndReportedAsFailure()
    {
        string removable = Path.Combine(_indexRoot, new string('a', 32));
        string locked = Path.Combine(_indexRoot, new string('b', 32));
        Directory.CreateDirectory(removable);
        Directory.CreateDirectory(locked);
        string lockedFile = Path.Combine(locked, "held.bin");
        File.WriteAllText(lockedFile, "held");

        int removed;
        using (FileStream stream = File.Open(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            removed = new ContentIndexManager(_paths).ClearAll();

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(locked));
        Directory.Delete(locked, recursive: true);
    }

    [Fact]
    public void ClearAll_MissingStorageRootReturnsZero()
    {
        var paths = new DefaultContentIndexPathProvider(
            Path.Combine(_sandbox, "never-created-index"),
            Path.Combine(_sandbox, "never-created-temp"));

        Assert.Equal(0, new ContentIndexManager(paths).ClearAll());
    }

    private UsnFileIdentity PublishGeneration(
        UsnCheckpoint? checkpoint = null,
        ulong identityHigh = 0,
        string? root = null,
        VolumeBinding? binding = null)
    {
        string generationRoot = root ?? _root;
        var identity = new UsnFileIdentity(4242, identityHigh);
        ulong serial = binding?.VolumeSerialNumber ?? 9;
        FileIdentity? IdentityProvider(string _) => new(serial, identity);
        string scopeId = ContentIndexManager.ScopeIdForRoot(generationRoot);
        var builder = new ContentIndexGenerationBuilder(Policy(), identityProvider: IdentityProvider);
        if (binding is { } volumeBinding)
            builder.SeedVolumeBinding(volumeBinding);
        builder.AddDocument(_file, Encoding.UTF8.GetBytes("indexed content here"));
        ContentIndexGeneration generation = builder.Build(
            scopeId, "volume", generationRoot, checkpoint ?? new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, scopeId).Publish(generation);
        return identity;
    }

    private static IndexIngestionPolicy Policy() => new(0, null, null, true, false, 0);

    private static ContentIndexFreshnessEvaluator.JournalReader FreshReader
        => (_, since) => new UsnReadResult(
            UsnReadStatus.Ok,
            new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
            Array.Empty<UsnChange>());

    private static ContentIndexFreshnessEvaluator.JournalReader DirtyReader(UsnFileIdentity identity)
        => (_, since) => new UsnReadResult(
            UsnReadStatus.Ok,
            new UsnCheckpoint(since.JournalId, since.NextUsn + 10),
            [new UsnChange(identity, 1)]);

    private sealed class ThrowingPathProvider(string indexRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => throw new InvalidOperationException("broken provider");
    }

    private sealed class ThrowingIoPathProvider(string indexRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => throw new IOException("unavailable index storage");
    }
}