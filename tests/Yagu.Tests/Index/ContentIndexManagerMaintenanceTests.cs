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
    public void StoredIndexDiscovery_IgnoresDirectoriesThatAreNotScopeIds()
    {
        // A recovery workspace or user-created folder under the index root must not be mistaken for a
        // reusable index root and silently added back to settings.
        Directory.CreateDirectory(Path.Combine(_indexRoot, "not-a-scope"));

        Assert.False(new ContentIndexManager(_paths).HasReadableStoredIndex());
        Assert.Empty(new ContentIndexManager(_paths).GetReusableStoredIndexRoots());

        PublishGeneration();

        Assert.True(new ContentIndexManager(_paths).HasReadableStoredIndex());
        Assert.Equal([_root], new ContentIndexManager(_paths).GetReusableStoredIndexRoots());
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
    public void AutomaticCompactionFailureMarker_RoundTripsBackoffAndClears()
    {
        PublishGeneration();
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId);
        DateTimeOffset failedUtc = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        ContentIndexManager.RecordAutomaticCompactionFailure(
            store,
            new IOException("scratch disk unavailable"),
            failedUtc);

        var manager = new ContentIndexManager(_paths);
        IndexAutomaticCompactionFailure failure = Assert.IsType<IndexAutomaticCompactionFailure>(
            manager.TryReadAutomaticCompactionFailureForRoot(_root));
        Assert.Equal(failedUtc, failure.FailedUtc);
        Assert.Equal(failedUtc.Add(ContentIndexManager.AutomaticCompactionRetryDelay), failure.RetryAfterUtc);
        Assert.Contains("scratch disk unavailable", failure.Reason);

        ContentIndexManager.ClearAutomaticCompactionFailure(store);
        Assert.Null(manager.TryReadAutomaticCompactionFailureForRoot(_root));

        ContentIndexManager.RecordAutomaticCompactionFailure(
            store,
            new IOException("retryable failure"),
            failedUtc);
        Assert.NotNull(manager.TryReadAutomaticCompactionFailureForRoot(_root));
        PublishGeneration(new UsnCheckpoint(1, 200));
        Assert.Null(manager.TryReadAutomaticCompactionFailureForRoot(_root));
    }

    [Fact]
    public void AutomaticCompactionFailureMarker_MalformedAndLockedMarkersAreIgnored()
    {
        PublishGeneration();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        string marker = Path.Combine(store.ScopeDirectory, ContentIndexManager.AutomaticCompactionFailureFile);

        File.WriteAllText(marker, "not-a-date" + Environment.NewLine + "reason");
        Assert.False(ContentIndexManager.TryReadAutomaticCompactionFailure(store, out _));

        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine + "reason");
        using var locked = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // A half-written or locked retry marker cannot be trusted to suppress maintenance.
        Assert.False(ContentIndexManager.TryReadAutomaticCompactionFailure(store, out _));
    }

    [Fact]
    public void AutomaticCompactionFailureMarker_WriteAndClearFailuresAreNonFatal()
    {
        string fileBackedScope = Path.Combine(_sandbox, "scope-is-a-file");
        File.WriteAllText(fileBackedScope, "not a directory");
        var fileBackedStore = new ContentIndexStore(
            new FixedScopePathProvider(_indexRoot, fileBackedScope),
            new string('a', 32));

        // Failure telemetry is best-effort; a read-only or otherwise invalid storage location must not
        // make the original compaction failure worse by throwing while recording the marker.
        ContentIndexManager.RecordAutomaticCompactionFailure(
            fileBackedStore,
            new IOException("original compaction failure"),
            DateTimeOffset.UtcNow);
        Assert.Equal("not a directory", File.ReadAllText(fileBackedScope));

        PublishGeneration();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        ContentIndexManager.RecordAutomaticCompactionFailure(store, new IOException("clear me"), DateTimeOffset.UtcNow);
        string marker = Path.Combine(store.ScopeDirectory, ContentIndexManager.AutomaticCompactionFailureFile);
        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ContentIndexManager.ClearAutomaticCompactionFailure(store);
            Assert.True(File.Exists(marker));
        }

        ContentIndexManager.ClearAutomaticCompactionFailure(store);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void DiskHeadroomFailure_RecordsItsCause_AndUsesTheShortRecheckBackoff()
    {
        PublishGeneration();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        DateTimeOffset failedUtc = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        ContentIndexManager.RecordAutomaticCompactionFailure(
            store,
            new IndexCompactionDiskGuardException("X:\\", "drive X:\\ would exceed the 90% full limit you set"),
            failedUtc);

        var manager = new ContentIndexManager(_paths);
        IndexAutomaticCompactionFailure failure = Assert.IsType<IndexAutomaticCompactionFailure>(
            manager.TryReadAutomaticCompactionFailureForRoot(_root));
        Assert.Equal(IndexAutomaticCompactionFailureCause.DiskHeadroom, failure.Cause);
        Assert.Equal(
            failedUtc.Add(ContentIndexManager.AutomaticCompactionDiskRetryDelay),
            failure.RetryAfterUtc);
        Assert.True(
            ContentIndexManager.AutomaticCompactionDiskRetryDelay
                < ContentIndexManager.AutomaticCompactionRetryDelay,
            "A self-correcting disk stop must be re-checked sooner than a generic failure.");
        Assert.True(manager.IsAutomaticCompactionDeferredForDiskSpace(_root));
    }

    [Fact]
    public void ShouldDeferAutomaticCompaction_StopsDeferring_OnceTheDriveIsBackInsideTheConfiguredLimits()
    {
        PublishGeneration();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        DateTimeOffset failedUtc = DateTimeOffset.UtcNow;
        var settings = new IndexMaintenanceSettings { MinimumFreeSpaceMB = 1024, MaxDiskUsagePercent = 90 };

        ContentIndexManager.RecordAutomaticCompactionFailure(
            store,
            new IndexCompactionDiskGuardException("X:\\", "not enough free space"),
            failedUtc);

        // The sandbox volume is a real drive with headroom, so the blocking condition has already cleared.
        Assert.False(ContentIndexManager.ShouldDeferAutomaticCompaction(
            store, settings, failedUtc.AddSeconds(1), out IndexAutomaticCompactionFailure cleared));
        Assert.Equal(IndexAutomaticCompactionFailureCause.DiskHeadroom, cleared.Cause);

        // A cause that is not self-correcting still waits out its full backoff.
        ContentIndexManager.RecordAutomaticCompactionFailure(store, new IOException("scratch gone"), failedUtc);
        Assert.True(ContentIndexManager.ShouldDeferAutomaticCompaction(
            store, settings, failedUtc.AddSeconds(1), out IndexAutomaticCompactionFailure held));
        Assert.Equal(IndexAutomaticCompactionFailureCause.Unknown, held.Cause);
        Assert.False(ContentIndexManager.ShouldDeferAutomaticCompaction(
            store,
            settings,
            failedUtc.Add(ContentIndexManager.AutomaticCompactionRetryDelay).AddSeconds(1),
            out _));

        // No marker at all never defers.
        ContentIndexManager.ClearAutomaticCompactionFailure(store);
        Assert.False(ContentIndexManager.ShouldDeferAutomaticCompaction(store, settings, failedUtc, out _));
    }

    [Fact]
    public void CanResumeDiskDeferredCompaction_RequiresBothADiskCause_AndARestoredVolume()
    {
        PublishGeneration();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        var manager = new ContentIndexManager(_paths);
        DateTimeOffset failedUtc = DateTimeOffset.UtcNow;

        // A generic failure is never resumed by a disk probe.
        ContentIndexManager.RecordAutomaticCompactionFailure(store, new IOException("scratch gone"), failedUtc);
        Assert.False(manager.CanResumeDiskDeferredCompaction(
            _root, 1024, 90, probe: _ => new IndexVolumeSpace("X:\\", 1000, 1000)));

        ContentIndexManager.RecordAutomaticCompactionFailure(
            store, new IndexCompactionDiskGuardException("X:\\", "not enough free space"), failedUtc);
        Assert.False(manager.CanResumeDiskDeferredCompaction(
            _root, 1024, 0, probe: _ => new IndexVolumeSpace("X:\\", 100L * 1024 * 1024 * 1024, 1024)));
        Assert.True(manager.CanResumeDiskDeferredCompaction(
            _root,
            1024,
            0,
            probe: _ => new IndexVolumeSpace("X:\\", 100L * 1024 * 1024 * 1024, 50L * 1024 * 1024 * 1024)));

        ContentIndexManager.ClearAutomaticCompactionFailure(store);
        Assert.False(manager.CanResumeDiskDeferredCompaction(
            _root, 1024, 0, probe: _ => new IndexVolumeSpace("X:\\", 1000, 1000)));
    }

    [Fact]
    public void CompactionDisabled_ClearsPriorAutomaticCompactionFailureMarker()
    {
        PublishGeneration();
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId);
        for (int index = 0; index < 2; index++)
        {
            var segment = new ContentIndexDeltaSegmentBuilder(Policy());
            segment.AddChangedDocument(
                Path.Combine(_root, $"delta-{index}.txt"),
                Encoding.UTF8.GetBytes($"delta content {index}"));
            store.PublishSegmentFast(segment.Build(
                scopeId,
                Path.GetPathRoot(_root)!,
                IndexScopeIdentity.NormalizePath(_root),
                new UsnCheckpoint(1, 200 + index),
                DateTimeOffset.UtcNow));
        }
        ContentIndexManager.RecordAutomaticCompactionFailure(
            store,
            new IOException("prior failure"),
            DateTimeOffset.UtcNow);

        var settings = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 1,
            CompactionThresholdMB = 8192,
            SizeManagementMode = IndexSizeManagementModes.Off,
        };
        Assert.False(new ContentIndexManager(_paths).CompactScopeIfOverSegmented(
            _root,
            Policy(),
            settings,
            DateTimeOffset.UtcNow));
        Assert.Null(new ContentIndexManager(_paths).TryReadAutomaticCompactionFailureForRoot(_root));
    }

    [Fact]
    public void AutomaticCompaction_RecentFailureDefersAnotherAttemptWithoutChangingSegments()
    {
        ContentIndexStore store = PublishDeltaSegments(2);
        ContentIndexManager.RecordAutomaticCompactionFailure(
            store,
            new IOException("previous merge failed"),
            DateTimeOffset.UtcNow);
        var settings = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 1,
            CompactionThresholdMB = 8192,
            CoalesceMinRun = 64,
        };

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexAutomaticCompactionOutcome outcome = new ContentIndexManager(_paths)
            .CompactScopeIfOverSegmentedUnderLease(mutation, _root, Policy(), settings, DateTimeOffset.UtcNow);

        // A retry marker protects the disk from hammering a failing compaction loop; the live layered
        // index remains queryable and unchanged until the backoff expires.
        Assert.Equal(IndexAutomaticCompactionOutcome.Failed, outcome);
        Assert.Equal(2, store.ActiveSegmentCount());
    }

    [Fact]
    public void AutomaticCompaction_OverBudgetWithoutAReclaimModeLeavesTheLayeredIndexInPlace()
    {
        ContentIndexStore store = PublishLargeDeltaSegments();
        Assert.True(store.TotalActiveIndexBytes() > 1024 * 1024, "Setup requires an index above the 1 MB budget.");
        var settings = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 1,
            CompactionThresholdMB = 8192,
            CoalesceMinRun = 64,
            SizeBudgetMB = 1,
            SizeManagementMode = IndexSizeManagementModes.Off,
        };

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexAutomaticCompactionOutcome outcome = new ContentIndexManager(_paths)
            .CompactScopeIfOverSegmentedUnderLease(mutation, _root, Policy(), settings, DateTimeOffset.UtcNow);

        // Size-budget warnings must not perform a surprise compaction when the user selected "off".
        Assert.Equal(IndexAutomaticCompactionOutcome.NotNeeded, outcome);
        Assert.True(store.ActiveSegmentCount() > 0);
    }

    [Fact]
    public void AutomaticCompaction_FailedStreamingFoldRecordsRetryMarker()
    {
        ContentIndexStore store = PublishDeltaSegments(2);
        var settings = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 1,
            CompactionThresholdMB = 8192,
            CoalesceMinRun = 64,
            MinimumFreeSpaceMB = int.MaxValue,
            MaxDiskUsagePercent = 99,
        };

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexAutomaticCompactionOutcome outcome = new ContentIndexManager(_paths)
            .CompactScopeIfOverSegmentedUnderLease(mutation, _root, Policy(), settings, DateTimeOffset.UtcNow);

        // The failed automatic pass must leave a marker so background maintenance backs off instead of
        // retrying an impossible disk-headroom compaction every tick.
        Assert.Equal(IndexAutomaticCompactionOutcome.Failed, outcome);
        Assert.True(ContentIndexManager.TryReadAutomaticCompactionFailure(
            store,
            out IndexAutomaticCompactionFailure failure));
        Assert.Equal(IndexAutomaticCompactionFailureCause.DiskHeadroom, failure.Cause);
    }

    [Fact]
    public void CompactScopeNow_CancellationPropagatesWithoutRecordingAFailureMarker()
    {
        ContentIndexStore store = PublishDeltaSegments(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        // User-initiated cancellation is not a compaction failure and must not arm retry backoff.
        Assert.ThrowsAny<OperationCanceledException>(() => new ContentIndexManager(_paths)
            .CompactScopeNowUnderLease(
                mutation,
                _root,
                new IndexMaintenanceSettings(),
                DateTimeOffset.UtcNow,
                cancellationToken: cancellation.Token));
        Assert.False(ContentIndexManager.TryReadAutomaticCompactionFailure(store, out _));
    }

    [Fact]
    public void CompactScopeNow_PublicWrapperReturnsWhenScopeIsAlreadySingleBase()
    {
        PublishGeneration();
        var manager = new ContentIndexManager(_paths);

        manager.CompactScopeNow(_root, new IndexMaintenanceSettings(), DateTimeOffset.UtcNow);

        // "Compact now" on an already-folded index is a no-op, not a failure marker that would suppress
        // future automatic maintenance.
        Assert.Null(manager.TryReadAutomaticCompactionFailureForRoot(_root));
    }

    [Fact]
    public void CompactScopeNow_MissingActiveLayersRecordsFailureBeforeRethrowing()
    {
        var manager = new ContentIndexManager(_paths);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            manager.CompactScopeNow(_root, new IndexMaintenanceSettings(), DateTimeOffset.UtcNow));

        // A corrupt or missing pointer should be reported to the caller and persisted as retry state so
        // background maintenance does not spin on the same broken index.
        Assert.Contains("no trusted active layers", error.Message, StringComparison.OrdinalIgnoreCase);
        IndexAutomaticCompactionFailure failure = Assert.IsType<IndexAutomaticCompactionFailure>(
            manager.TryReadAutomaticCompactionFailureForRoot(_root));
        Assert.Equal(IndexAutomaticCompactionFailureCause.Unknown, failure.Cause);
    }

    [Fact]
    public void TryReadAutomaticCompactionFailureForRoot_RejectsBlankOrUnusableRoots()
    {
        // The status surface polls this helper defensively; an empty box or unavailable index provider
        // should simply mean "no deferral marker".
        Assert.Null(new ContentIndexManager(_paths).TryReadAutomaticCompactionFailureForRoot("   "));
        Assert.Null(new ContentIndexManager(new ThrowingPathProvider(_indexRoot))
            .TryReadAutomaticCompactionFailureForRoot(_root));
    }

    [Fact]
    public void TryGetVolumeTotalBytes_InvalidPathReturnsZero()
    {
        var method = typeof(ContentIndexManager).GetMethod(
            "TryGetVolumeTotalBytes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        long bytes = Assert.IsType<long>(method!.Invoke(null, ["\0bad-path"]));

        // A projected cloud download should disable only the percentage projection if Windows refuses to
        // parse the root, never abort the index build.
        Assert.Equal(0, bytes);
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

    private ContentIndexStore PublishDeltaSegments(int count)
    {
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId);
        var baseBuilder = new ContentIndexGenerationBuilder(Policy());
        baseBuilder.AddDocument(_file, Encoding.UTF8.GetBytes("indexed content here"));
        store.Publish(baseBuilder.Build(
            scopeId,
            Path.GetPathRoot(_root)!,
            IndexScopeIdentity.NormalizePath(_root),
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow));
        for (int index = 0; index < count; index++)
        {
            var segment = new ContentIndexDeltaSegmentBuilder(Policy());
            segment.AddChangedDocument(
                Path.Combine(_root, $"delta-{index}.txt"),
                Encoding.UTF8.GetBytes($"delta content {index}"));
            store.PublishSegmentFast(segment.Build(
                scopeId,
                Path.GetPathRoot(_root)!,
                IndexScopeIdentity.NormalizePath(_root),
                new UsnCheckpoint(1, 200 + index),
                DateTimeOffset.UtcNow.AddSeconds(index)));
        }
        return store;
    }

    private ContentIndexStore PublishLargeDeltaSegments()
    {
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId);
        var baseBuilder = new ContentIndexGenerationBuilder(Policy());
        baseBuilder.AddDocument(_file, Encoding.UTF8.GetBytes("indexed content here"));
        store.Publish(baseBuilder.Build(
            scopeId,
            Path.GetPathRoot(_root)!,
            IndexScopeIdentity.NormalizePath(_root),
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow));
        for (int layer = 0; layer < 3; layer++)
        {
            var segment = new ContentIndexDeltaSegmentBuilder(Policy());
            for (int file = 0; file < 250; file++)
            {
                string body = string.Concat(Enumerable.Range(0, 40)
                    .Select(_ => Guid.NewGuid().ToString("N")));
                segment.AddChangedDocument(
                    Path.Combine(_root, $"large-{layer}-{file}.txt"),
                    Encoding.UTF8.GetBytes(body));
            }
            store.PublishSegmentFast(segment.Build(
                scopeId,
                Path.GetPathRoot(_root)!,
                IndexScopeIdentity.NormalizePath(_root),
                new UsnCheckpoint(1, 500 + layer),
                DateTimeOffset.UtcNow.AddSeconds(layer)));
        }
        return store;
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

    private sealed class FixedScopePathProvider(string indexRoot, string scopeDirectory) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => scopeDirectory;
    }
}