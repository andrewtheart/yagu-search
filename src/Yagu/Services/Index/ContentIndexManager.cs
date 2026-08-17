using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;
using Yagu.Services.Pdf;
using Yagu.Services.Ocr;

namespace Yagu.Services.Index;

/// <summary>Stable CLI exit codes for the index management commands (plan §6.3).</summary>
public enum ContentIndexExitCode
{
    Success = 0,
    InvalidArguments = 2,
    UnsupportedScope = 3,
    Cancelled = 4,
    BuildFailure = 5,
    AlreadyRunning = 6,
}

/// <summary>Status of a scope's index (plan §6.3 <c>--index-status</c>).</summary>
public sealed record IndexScopeStatus(bool Exists, IndexManifest? Manifest, string? Summary);

/// <summary>Cheap manifest-only scope metadata. <see cref="Exists"/> means pointer/manifest presence;
/// it deliberately does not claim that the full base and segments are structurally valid.</summary>
public readonly record struct IndexMetadataStatus(
    bool Exists,
    bool MetadataReadable,
    long DocumentCount,
    int SegmentCount,
    DateTimeOffset? BuiltUtc,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? LastIncrementalUpdateUtc,
    string? RootPath,
    IndexStorageHealth Health,
    string? Problem);

/// <summary>On-disk stats for one scope's index: its id, the indexed root (when readable), the total bytes
/// occupied on disk, the stored content-record count (base + segments), the active segment count, the active
/// generation's build time, and whether a trusted base was readable.</summary>
public readonly record struct IndexStorageStat(
    string ScopeId,
    string? RootPath,
    long SizeBytes,
    long DocumentCount,
    int SegmentCount,
    DateTimeOffset? BuiltUtc,
    IndexStorageHealth Health,
    bool RootExists,
    string? Problem)
{
    public DateTimeOffset? CreatedUtc { get; init; }
    public DateTimeOffset? LastIncrementalUpdateUtc { get; init; }
    public bool Readable => Health is IndexStorageHealth.Healthy or IndexStorageHealth.SourceMissing;
    public bool NeedsRepair => Health is IndexStorageHealth.IncompatibleFormat
        or IndexStorageHealth.IncompatibleRepresentation
        or IndexStorageHealth.CorruptOrIncomplete;
    public bool CanRepair => NeedsRepair && RootExists && !string.IsNullOrWhiteSpace(RootPath);
}

/// <summary>Aggregate storage stats across every scope under the index storage directory.</summary>
public readonly record struct IndexStorageSummary(
    IReadOnlyList<IndexStorageStat> Indexes,
    long TotalSizeBytes,
    long TotalDocuments,
    string StorageDirectory);

/// <summary>
/// Aggregate build content-I/O counters (plan §5.8) — diagnostics only, never affecting the built index.
/// <see cref="ContentBytesRead"/> is the actual number of file bytes physically read by the build content
/// reader; <see cref="PrefixRejectedFiles"/> counts files rejected as binary/BOM after at most the 8 KB
/// sniff prefix; <see cref="FullyReadFiles"/> counts files whose whole body was read (plausible text).
/// </summary>
public readonly record struct IndexBuildIoStats(long ContentBytesRead, int PrefixRejectedFiles, int FullyReadFiles);

/// <summary>Result of building a scope index.</summary>
public sealed record BuildScopeResult(string ScopeId, PublishResult Publish, IndexBuildReport Report)
{
    /// <summary>Aggregate content-I/O counters for the crawl (plan §5.8). Diagnostics only.</summary>
    public IndexBuildIoStats IoStats { get; init; }
}

/// <summary>Progress of a <see cref="ContentIndexManager.BuildScope"/> crawl: the cumulative size and
/// count of files whose metadata has been read so far. Reported periodically (and once at completion) so
/// the UI can show an estimated percent complete. Diagnostics only — never affects the built index.</summary>
public readonly record struct IndexBuildProgress(long BytesCrawled, long FilesCrawled);

/// <summary>Status of a PDF-text extended-source build for one scope (plan §7 Phase 4).</summary>
public enum PdfExtendedSourceBuildStatus
{
    /// <summary>The namespace was proven repeatable and published; PDF pruning can now engage for this scope.</summary>
    Published,
    /// <summary>The extractor was not proven repeatable — the namespace was NOT published (never prunes).</summary>
    SkippedNotDeterministic,
    /// <summary><c>pdftotext.exe</c> could not be located/fingerprinted — nothing built.</summary>
    SkippedToolUnavailable,
    /// <summary>No PDF files were found under the root — nothing to build.</summary>
    NoPdfs,
}

/// <summary>Result of <see cref="ContentIndexManager.BuildPdfExtendedSourceAsync"/>.</summary>
public sealed record PdfExtendedSourceBuildResult(
    string ScopeId,
    PdfExtendedSourceBuildStatus Status,
    int PdfsSeen,
    int Admitted,
    PdfDeterminismVerdict Determinism);

public enum ImageOcrExtendedSourceBuildStatus
{
    Published,
    SkippedEngineUnavailable,
    NoImages,
}

public sealed record ImageOcrExtendedSourceBuildResult(
    string ScopeId,
    ImageOcrExtendedSourceBuildStatus Status,
    int ImagesSeen,
    int Admitted,
    int Failed);

/// <summary>
/// Thrown by <see cref="ContentIndexManager.BuildScope"/> when the index drive reaches the configured
/// used-space limit (plan §11.2). Private staged batches are discarded; the previously active complete
/// index (if any) remains unchanged. The caller surfaces a disk-space warning and stops indexing.
/// </summary>
public sealed class IndexDiskFullException : Exception
{
    public IndexDiskFullException(string driveDisplayName, double usedPercent, int thresholdPercent)
        : base($"Index build stopped: the index drive {driveDisplayName} is {usedPercent:F1}% full (limit {thresholdPercent}%).")
    {
        DriveDisplayName = driveDisplayName;
        UsedPercent = usedPercent;
        ThresholdPercent = thresholdPercent;
    }

    /// <summary>The index storage drive (e.g. <c>C:</c>).</summary>
    public string DriveDisplayName { get; }
    /// <summary>The drive's used-space percentage when the build was stopped.</summary>
    public double UsedPercent { get; }
    /// <summary>The configured used-space limit that was reached.</summary>
    public int ThresholdPercent { get; }
}

/// <summary>
/// High-level managed facade for building and managing scope indexes (plan §5/§6.3). It crawls a root
/// directory, classifies every file via the ingestion policy, builds a generation, and publishes it
/// transactionally through <see cref="ContentIndexStore"/>. It also reports status, deletes one scope,
/// and clears all scopes. Every path is composed through the injected
/// <see cref="IContentIndexPathProvider"/>, and the crawl <b>self-excludes</b> the index storage root
/// (plan §3.6). This is the single-process reference the CLI wraps; production ultimately builds via the
/// isolated Rust worker.
/// </summary>
public sealed partial class ContentIndexManager
{
    private readonly IContentIndexPathProvider _paths;
    private readonly int _retainedGenerations;
    private readonly IIndexFileContentReader _contentReader;
    private readonly Func<string, VolumeBinding?> _volumeBindingReader;
    private readonly Func<string, UsnCheckpoint?> _checkpointReader;
    private readonly Func<string, UsnJournalInfo?> _journalInfoReader;
    private readonly Func<string, string, int, ExtractorFingerprint?> _ocrFingerprintReader;
    private readonly Func<string, bool> _changeJournalSupportReader;
    private readonly IndexCrawlerFileSystem? _crawlerFileSystem;
    private readonly Func<string, ExtendedSourceStore> _extendedSourceStoreFactory;
    private readonly int _progressEveryFiles;
    private readonly int _diskCheckEveryFiles;

    public ContentIndexManager(IContentIndexPathProvider pathProvider, int retainedGenerations = 2)
        : this(pathProvider, retainedGenerations, contentReader: null)
    {
    }

    /// <summary>Cheap freshness state for automatic maintenance. <see cref="Uncertain"/> means the
    /// journal could not prove continuity (for example, a catch-up cap, gap, or reset); it is neither
    /// fresh nor proof that a full rebuild should happen automatically.</summary>
    public enum ScopeFreshnessState
    {
        Missing,
        Fresh,
        Dirty,
        Uncertain,
    }

    /// <summary>Reason-bearing lightweight freshness status for Settings and the main status indicator.
    /// <see cref="RequiresRebuild"/> means the existing bytes remain structurally intact but cannot be
    /// trusted for pruning until rebuilt against a new journal checkpoint.</summary>
    public readonly record struct ScopeFreshnessStatus(
        ScopeFreshnessState State,
        RootFreshnessVerdict Verdict,
        UsnReadStatus RawStatus,
        string? Problem,
        bool RequiresRebuild)
    {
        /// <summary>Number of pending changes relevant to the maintenance decision. For a whole-volume
        /// index this includes new/unindexed identities; for a subfolder it is the known indexed dirty set.</summary>
        public int DirtyCount { get; init; }
        public bool NeedsAttention => State == ScopeFreshnessState.Uncertain;
        public bool NeedsUpdate => State == ScopeFreshnessState.Dirty;
    }

    /// <summary>Reads only manifest + file identities and classifies freshness without loading postings.</summary>
    public ScopeFreshnessState GetScopeFreshnessState(
        string rootDirectory,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
        => GetScopeFreshnessStatus(rootDirectory, journalReader).State;

    /// <summary>Reads only manifest + file identities and returns freshness plus an actionable reason.</summary>
    public ScopeFreshnessStatus GetScopeFreshnessStatus(
        string rootDirectory,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        try
        {
            string scopeId = ScopeIdForRoot(rootDirectory);
            var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
            if (store.TryReadCurrentFreshnessInputs() is not { } inputs)
                return new ScopeFreshnessStatus(
                    ScopeFreshnessState.Missing,
                    RootFreshnessVerdict.CheckpointInvalid,
                    UsnReadStatus.Ok,
                    "No readable active index freshness metadata is available.",
                    RequiresRebuild: false);
            VolumeBinding? mounted = string.IsNullOrWhiteSpace(inputs.Manifest.VolumeGuidPath)
                ? null
                : _volumeBindingReader(inputs.Manifest.NormalizedRootPath);
            string volumeReason = "source volume unavailable";
            if (!string.IsNullOrWhiteSpace(inputs.Manifest.VolumeGuidPath)
                && (mounted is not { } currentVolume
                    || !VolumeBindingReader.MatchesManifest(inputs.Manifest, currentVolume, out volumeReason)))
            {
                return new ScopeFreshnessStatus(
                    ScopeFreshnessState.Uncertain,
                    RootFreshnessVerdict.JournalDiscontinuity,
                    UsnReadStatus.VolumeMismatch,
                    mounted is null
                        ? "The indexed source volume is disconnected or unavailable."
                        : $"The mounted source does not match the indexed volume ({volumeReason}).",
                    RequiresRebuild: mounted is not null);
            }
            FreshnessRead freshness = ContentIndexFreshnessEvaluator.ReadDirtySince(
                inputs.Manifest.NormalizedRootPath, inputs.Manifest.FreshnessCheckpoint, inputs.FileIds, journalReader);
            if (!freshness.IsContinuous)
            {
                bool requiresRebuild = IsRepairableByRebuild(
                    inputs.Manifest.NormalizedRootPath, freshness.Verdict, freshness.RawStatus);
                bool legacyIdentityMismatch = freshness.RawStatus == UsnReadStatus.IdentityMismatch;
                return new ScopeFreshnessStatus(
                    legacyIdentityMismatch ? ScopeFreshnessState.Dirty : ScopeFreshnessState.Uncertain,
                    freshness.Verdict,
                    freshness.RawStatus,
                    DescribeFreshnessProblem(freshness.Verdict, freshness.RawStatus, requiresRebuild),
                    requiresRebuild)
                {
                    DirtyCount = legacyIdentityMismatch ? freshness.JournalChangeCount : 0,
                };
            }
            string normalizedRoot = inputs.Manifest.NormalizedRootPath;
            string volumeRoot = Path.GetPathRoot(normalizedRoot)!;
            bool coversWholeVolume = string.Equals(
                IndexScopeIdentity.NormalizePath(volumeRoot),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase);
            int pendingChanges = coversWholeVolume
                ? freshness.JournalChangeCount
                : freshness.Dirty.Count;
            bool dirty = pendingChanges > 0;
            return new ScopeFreshnessStatus(
                dirty ? ScopeFreshnessState.Dirty : ScopeFreshnessState.Fresh,
                freshness.Verdict,
                freshness.RawStatus,
                dirty ? "Changes were detected since the index was last updated." : null,
                RequiresRebuild: false)
            {
                DirtyCount = pendingChanges,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ScopeFreshnessStatus(
                ScopeFreshnessState.Uncertain,
                RootFreshnessVerdict.JournalDiscontinuity,
                UsnReadStatus.Error,
                $"Index freshness could not be checked ({ex.GetType().Name}). Searches safely scan live; retry validation when the drive is available.",
                RequiresRebuild: false);
        }
    }

    private bool IsRepairableByRebuild(
        string rootPath,
        RootFreshnessVerdict verdict,
        UsnReadStatus rawStatus)
        => rawStatus is UsnReadStatus.CheckpointAhead
            or UsnReadStatus.JournalIdChanged
            or UsnReadStatus.GapDetected
            or UsnReadStatus.IdentityMismatch
        || (verdict == RootFreshnessVerdict.CheckpointInvalid && _changeJournalSupportReader(rootPath));

    internal static bool VolumeSupportsChangeJournal(string rootPath)
    {
        try
        {
            string? volumeRoot = Path.GetPathRoot(rootPath);
            if (string.IsNullOrWhiteSpace(volumeRoot))
                return false;
            return VolumeFormatSupportsChangeJournal(new DriveInfo(volumeRoot).DriveFormat);
        }
        catch
        {
            return false;
        }
    }

    internal static bool VolumeFormatSupportsChangeJournal(string? format)
        => format?.Equals("NTFS", StringComparison.OrdinalIgnoreCase) == true
            || format?.Equals("ReFS", StringComparison.OrdinalIgnoreCase) == true;

    internal static string DescribeFreshnessProblem(
        RootFreshnessVerdict verdict,
        UsnReadStatus rawStatus,
        bool requiresRebuild)
        => rawStatus switch
        {
            UsnReadStatus.CheckpointAhead =>
                "The saved index checkpoint is ahead of the drive's live change journal, usually because the journal was reset or recreated. Rebuild required.",
            UsnReadStatus.JournalIdChanged =>
                "The drive change journal was reset after this index was built. Rebuild required.",
            UsnReadStatus.GapDetected =>
                "The drive change journal no longer contains every change since this index was built. Rebuild required.",
            UsnReadStatus.IdentityMismatch =>
                "This ReFS index uses an older file-identity format. It will be rebuilt once to restore safe incremental updates.",
            UsnReadStatus.VolumeMismatch =>
                "The mounted source is not the volume this index was built from. Reconnect the original volume or rebuild for the current one.",
            UsnReadStatus.Incomplete =>
                "The index is farther behind than the configured journal catch-up limit, so freshness cannot be proven. Increase the limit and update, or rebuild.",
            UsnReadStatus.Unavailable =>
                "The drive has no usable change journal, so index freshness cannot be proven. Searches safely scan live.",
            UsnReadStatus.UnknownRecordVersion =>
                "The drive returned an unsupported change-journal record. Rebuild required before index acceleration can be trusted.",
            UsnReadStatus.Error =>
                "The drive change journal could not be read reliably. Searches safely scan live; retry validation when the drive is available.",
            UsnReadStatus.IoTimeout =>
                "The drive did not answer the freshness check before the configured I/O deadline. Searches safely scan live.",
            _ when verdict == RootFreshnessVerdict.CheckpointInvalid && requiresRebuild =>
                "The index has no usable freshness checkpoint. Rebuild required.",
            _ when verdict == RootFreshnessVerdict.CheckpointInvalid =>
                "The drive does not provide a usable change journal, so this index cannot be freshness-validated. Searches safely scan live.",
            _ => "Index freshness cannot currently be proven. Searches safely scan live.",
        };

    /// <summary>
    /// Test/composition seam allowing a fake <see cref="IIndexFileContentReader"/> to be injected for
    /// deterministic fault-injection and byte-read accounting. Production passes <c>null</c> → the default
    /// one-open prefix-rejecting reader (plan §5.1/§5.2).
    /// </summary>
    internal ContentIndexManager(
        IContentIndexPathProvider pathProvider,
        int retainedGenerations,
        IIndexFileContentReader? contentReader,
        Func<string, VolumeBinding?>? volumeBindingReader = null,
        Func<string, UsnCheckpoint?>? checkpointReader = null,
        Func<string, UsnJournalInfo?>? journalInfoReader = null,
        Func<string, string, int, ExtractorFingerprint?>? ocrFingerprintReader = null,
        Func<string, bool>? changeJournalSupportReader = null,
        IndexCrawlerFileSystem? crawlerFileSystem = null,
        Func<string, ExtendedSourceStore>? extendedSourceStoreFactory = null,
        int progressEveryFiles = 2000,
        int diskCheckEveryFiles = 8192)
    {
        _paths = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        _retainedGenerations = Math.Max(1, retainedGenerations);
        _contentReader = contentReader ?? new IndexFileContentReader();
        _volumeBindingReader = volumeBindingReader ?? VolumeBindingReader.TryCapture;
        _checkpointReader = checkpointReader ?? UsnJournalReader.TryCaptureCheckpoint;
        _journalInfoReader = journalInfoReader ?? UsnJournalReader.TryQueryJournalInfo;
        _ocrFingerprintReader = ocrFingerprintReader ?? ImageOcrExtractorFingerprint.TryCompute;
        _changeJournalSupportReader = changeJournalSupportReader ?? VolumeSupportsChangeJournal;
        _crawlerFileSystem = crawlerFileSystem;
        _extendedSourceStoreFactory = extendedSourceStoreFactory
            ?? (scopeId => new ExtendedSourceStore(_paths, scopeId));
        _progressEveryFiles = Math.Max(1, progressEveryFiles);
        _diskCheckEveryFiles = Math.Max(1, diskCheckEveryFiles);
    }

    /// <summary>
    /// When true, each base-generation publish during a build also writes the additive <b>format-v3 query
    /// structures</b> (plan §5.1) into the generation directory (transactionally). Propagated to the staged
    /// manager used by <see cref="BuildScope"/> and applied to the store used by
    /// <see cref="BuildScopeUnderLease"/>. Off by default (experimental; no query path reads them yet).
    /// </summary>
    public bool ProduceV3QueryStructures { get; set; }

    /// <summary>The canonical scope id for a root directory (volume identity + normalized root, §3.6).</summary>
    public static string ScopeIdForRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        string normalizedRoot = IndexScopeIdentity.NormalizePath(rootDirectory);
        string volume = Path.GetPathRoot(normalizedRoot)!;
        return IndexScopeIdentity.ComputeScopeId(volume, normalizedRoot);
    }

    /// <summary>
    /// Crawls <paramref name="rootDirectory"/>, classifies every file, and publishes a queryable index.
    /// To keep memory bounded on a huge root (e.g. a whole drive), the growing index is <b>spilled to disk
    /// in batches</b> (plan §11): the first batch publishes the base generation and each later batch is
    /// appended as an immutable delta segment (queried newest-first), so peak RAM stays near one batch
    /// instead of the entire generation. The batch size is derived from <paramref name="buildMemoryBudgetMB"/>
    /// (0 → the architecture default) — no extra setting. A small root produces a single batch (one atomic
    /// publish, identical to the pre-paging behavior). Throws <see cref="DirectoryNotFoundException"/> for a
    /// missing root (caller → UnsupportedScope) and honors <paramref name="cancellationToken"/>
    /// (caller → Cancelled).
    /// </summary>
    public BuildScopeResult BuildScope(
        string rootDirectory,
        IndexIngestionPolicy policy,
        CancellationToken cancellationToken = default,
        int buildMemoryBudgetMB = 0,
        int maxDiskUsagePercent = 0,
        Func<string, double?>? diskUsedPercentProbe = null,
        Action<IndexBuildProgress>? progress = null,
        int buildParallelism = 1,
        TimeSpan? fileIoTimeout = null)
    {
        string normalizedRoot = IndexScopeIdentity.NormalizePath(rootDirectory);
        string scopeId = ScopeIdForRoot(normalizedRoot);
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = new ContentIndexBuildTransaction(_paths, scopeId);
        var stagedManager = new ContentIndexManager(
            transaction.Paths,
            _retainedGenerations,
            _contentReader,
            _volumeBindingReader,
            _checkpointReader,
            _journalInfoReader,
            _ocrFingerprintReader,
            _changeJournalSupportReader,
            _crawlerFileSystem,
            _extendedSourceStoreFactory,
            _progressEveryFiles,
            _diskCheckEveryFiles)
        {
            ProduceV3QueryStructures = ProduceV3QueryStructures,
        };
        BuildScopeResult staged = stagedManager.BuildScopeUnderLease(
            mutation, normalizedRoot, policy, cancellationToken, buildMemoryBudgetMB,
            maxDiskUsagePercent, diskUsedPercentProbe, progress, buildParallelism, fileIoTimeout);
        StagedIndexCommitResult commit = transaction.Commit(mutation, _retainedGenerations, StagedPdfCommitMode.Preserve);
        return staged with { Publish = new PublishResult(commit.LastPublishedArtifactId, commit.ActivePointerSequence) };
    }

    internal BuildScopeResult BuildScopeUnderLease(
        IndexMutationContext mutation,
        string rootDirectory,
        IndexIngestionPolicy policy,
        CancellationToken cancellationToken = default,
        int buildMemoryBudgetMB = 0,
        int maxDiskUsagePercent = 0,
        Func<string, double?>? diskUsedPercentProbe = null,
        Action<IndexBuildProgress>? progress = null,
        int buildParallelism = 1,
        TimeSpan? fileIoTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        string normalizedRoot = IndexScopeIdentity.NormalizePath(rootDirectory);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"Index root does not exist: {normalizedRoot}");

        VolumeBinding startVolume = _volumeBindingReader(normalizedRoot)
            ?? throw new IndexVolumeChangedException($"Could not identify the mounted volume for index root '{normalizedRoot}'.");
        if (!startVolume.SupportsChangeJournal)
            throw new NotSupportedException(
                $"The {startVolume.FileSystemName} filesystem does not provide the change-journal guarantees required by the content index.");

        string volume = Path.GetPathRoot(normalizedRoot)!;
        string scopeId = IndexScopeIdentity.ComputeScopeId(volume, normalizedRoot);
        string indexRoot = IndexScopeIdentity.NormalizePath(_paths.IndexRoot);

        // Stop-when-full guard (plan §11.2): abort the build (keeping the partial index already on disk)
        // once the index drive reaches the configured used-space limit. 0 = disabled. The probe is
        // injectable for testing; production reads the real drive.
        int diskLimit = maxDiskUsagePercent;
        Func<string, double?> usedPercentProbe = diskUsedPercentProbe ?? RealDiskUsedPercent;
        string indexDriveName = Path.GetPathRoot(indexRoot)!.TrimEnd('\\', '/');
        void CheckDiskSpace()
        {
            if (diskLimit <= 0)
                return;
            if (usedPercentProbe(indexRoot) is double used && used >= diskLimit)
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "Build stopped: index drive {IndexDrive} is {UsedPercent:F1}% full (>= {DiskLimitPercent}% limit) for scope {Scope}. Staged build discarded; prior index unchanged.",
                    indexDriveName, used, diskLimit, scopeId);
                throw new IndexDiskFullException(indexDriveName, used, diskLimit);
            }
        }

        // Capture the freshness checkpoint BEFORE crawling so any change made during the build is replayed
        // (and the affected content dirtied → live-scanned) at query time — never missed. None when the
        // volume has no readable USN journal, in which case the generation is freshness-untrusted. Every
        // spilled batch shares this one checkpoint (they are all part of a single crawl).
        UsnCheckpoint checkpoint = _checkpointReader(normalizedRoot) ?? UsnCheckpoint.None;

        // A retained document costs roughly its distinct-trigram set + alias path + durable identity
        // (~20 KB), so ~48 admitted documents per MB of budget bounds one in-memory batch to about the
        // budget. Spilling a batch resets the builder, so peak RAM tracks the batch, not the whole root.
        int budgetMB = buildMemoryBudgetMB > 0 ? buildMemoryBudgetMB : IndexBuildDefaults.MemoryBudgetMB;
        // Each lane may retain one dense trigram result until its ordered commit. Keep that transient
        // window inside the same memory budget rather than multiplying peak memory by the configured degree.
        int readParallelism = Math.Min(
            Math.Clamp(buildParallelism, 1, IndexWorkerParallelism.Maximum),
            Math.Max(1, budgetMB / (IndexWorkerParallelism.BuildLaneReserveMB * 2)));
        // Floor is defensive only: a valid budget (>= 64 MB) yields >= 3072, so real builds never hit it;
        // it just lets a tiny injected budget spill in small batches under test.
        int maxBatchDocuments = Math.Max(256, budgetMB * 48);
        // The legacy doc-count estimate assumes text-sized trigram sets (~20 KB/doc). Binary ASCII sets can
        // be denser, so also cap retained document-trigram entries directly. ~8 bytes/entry (packed value +
        // collection overhead amortized) reserves about half the budget for the retained representation;
        // serialization/v3 inversion uses the rest during a flush.
        long maxBatchTrigrams = Math.Max(65_536L, (long)budgetMB * 64 * 1024);

        YaguLog.For("ContentIndex").LogInformation(
            "Build starting: root='{Root}' scope={Scope} budget={BudgetMB}MB parallelism={Parallelism} (~{MaxBatchDocs:N0} docs / {MaxBatchTrigrams:N0} trigrams per batch) checkpoint={JournalId}/{NextUsn} indexRoot='{IndexRoot}'.",
            normalizedRoot, scopeId, budgetMB, readParallelism, maxBatchDocuments, maxBatchTrigrams, checkpoint.JournalId, checkpoint.NextUsn, indexRoot);
        long startTicks = Environment.TickCount64;
        int batchNumber = 0;
        int loggedFileWarnings = 0;
        const int MaxLoggedFileWarnings = 100;

        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
        store.ProduceV3QueryStructures = ProduceV3QueryStructures;
        var report = new IndexBuildReport();
        string[] noRemovals = Array.Empty<string>();

        // Capture each admitted file's durable (volume, FILE_ID_128) identity so the change journal can
        // dirty exactly the affected content at query time (plan §3.5/§3.6). The identity is read from the
        // SAME handle the content reader opens (plan §5.4) — the builder needs no identity provider, so no
        // admitted file is opened a second time. The report is shared across every batch builder so counts
        // accumulate over the whole crawl.
        var builder = new ContentIndexGenerationBuilder(policy, report);
        builder.SeedVolumeBinding(startVolume);
        int batchDocuments = 0;
        bool basePublished = false;
        PublishResult lastPublish = default;

        void FlushBatch()
        {
            // Stop before writing a new batch if the index drive has hit the used-space limit.
            CheckDiskSpace();
            int flushingDocs = batchDocuments;
            bool asSegment = basePublished;
            try
            {
                // Persistence-only finalization (plan §5.5): NO TrigramPostingIndex.Build — the serializer
                // stores per-document trigrams, not postings, so a full build never needs the inverted index.
                // This removes a whole posting-index construction (and its immediate discard) per batch flush.
                ContentIndexBuildBatch batch = builder.BuildForPersistence(scopeId, volume, normalizedRoot, checkpoint, DateTimeOffset.UtcNow);
                lastPublish = asSegment
                    ? store.PublishSegmentFastUnderLease(mutation, new ContentIndexDeltaSegmentBatch(batch, noRemovals))
                    : store.PublishUnderLease(mutation, batch);
                basePublished = true;
                batchNumber++;
                // Batch-level progress at Info so a long paged build is observable in the log without flooding.
                YaguLog.For("ContentIndex").LogInformation(
                    "Build flushed batch {BatchNumber} ({FlushingDocs:N0} docs) as {BatchKind} '{GenerationId}' (scope {Scope}).",
                    batchNumber, flushingDocs, asSegment ? "delta segment" : "base generation", lastPublish.GenerationId, scopeId);
            }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogCritical(ex,
                    "Build FAILED flushing batch {BatchNumber} ({FlushingDocs:N0} docs, asSegment={AsSegment}) for scope {Scope}.",
                    batchNumber + 1, flushingDocs, asSegment, scopeId);
                throw;
            }
            builder = new ContentIndexGenerationBuilder(policy, report);
            builder.SeedVolumeBinding(startVolume);
            batchDocuments = 0;
        }

        // Bail out immediately if the drive is already over the limit before we crawl a single file.
        CheckDiskSpace();

        // Periodic disk check by crawl count catches a drive filling up between batch flushes (e.g. a root
        // of few large files that rarely flushes, or another process consuming space).
        long filesSinceDiskCheck = 0;

        // Progress estimate inputs: the running sum of crawled file sizes + count. Reported to the optional
        // callback every few thousand files (and once at the end) so a UI can estimate percent complete
        // without an expensive pre-count pass.
        long bytesCrawled = 0;
        long filesCrawled = 0;
        long filesSinceProgress = 0;

        // Build content-I/O counters (plan §5.8): actual bytes read, and how many files the one-open reader
        // rejected as binary/BOM after only the 8 KB prefix vs. those whose whole body was read. Diagnostics.
        long contentBytesRead = 0;
        int prefixRejectedFiles = 0;
        int fullyReadFiles = 0;
        TimeSpan effectiveFileIoTimeout = fileIoTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : BoundedIncrementalFileClassifier.DefaultFileTimeout;
        var expectedReadLengths = new long[readParallelism];
        using var boundedReaders = new BoundedIncrementalFileClassifierPool(
            readParallelism,
            laneIndex => new BoundedIncrementalFileClassifier(
                (path, token) =>
                {
                    long expectedLength = Volatile.Read(ref expectedReadLengths[laneIndex]);
                    IndexFileReadResult result = _contentReader.Read(path, expectedLength, policy, token);
                    return new IncrementalFileRead(result.Classification, result.Identity, result.BytesRead);
                },
                cancellationToken,
                effectiveFileIoTimeout,
                BoundedIncrementalFileClassifier.DefaultCancellationGrace,
                BoundedIncrementalFileClassifier.DefaultMaximumAbandonedLanes));

        // Read/classify in a bounded window, then apply results in the exact crawl order. This makes the
        // expensive per-file work parallel without sharing the non-thread-safe builder/report or changing
        // content ids, alias ids, spill boundaries, diagnostics order, and transactional publication.
        var readWindow = new List<(string Path, string NormalizedPath, long Length, IndexSkipReason MetadataVerdict)>(readParallelism);

        (IndexFileReadResult Read, Exception? Error) ReadCandidate(string path, long length, int laneIndex)
        {
            try
            {
                Volatile.Write(ref expectedReadLengths[laneIndex], length);
                IncrementalFileRead? bounded = boundedReaders[laneIndex].Read(path);
                return bounded is null
                    ? (default, new TimeoutException($"File I/O exceeded {effectiveFileIoTimeout.TotalSeconds:F0} seconds."))
                    : (new IndexFileReadResult(
                        bounded.Value.Classification.Reason,
                        bounded.Value.Classification.Trigrams,
                        bounded.Value.BytesRead,
                        bounded.Value.Identity), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (default, ex);
            }
        }

        void CommitReadOutcome(
            (string Path, string NormalizedPath, long Length, IndexSkipReason MetadataVerdict) item,
            IndexFileReadResult read,
            Exception? readError)
        {
            if (item.MetadataVerdict != IndexSkipReason.None)
            {
                report.Record(item.NormalizedPath, item.MetadataVerdict);
                return;
            }

            if (readError is not null)
            {
                report.Record(item.NormalizedPath,
                    readError is TimeoutException ? IndexSkipReason.IoTimeout : IndexSkipReason.AccessDenied);
                if (loggedFileWarnings < MaxLoggedFileWarnings)
                {
                    loggedFileWarnings++;
                    YaguLog.For("ContentIndex").LogDebug("Build skipped (content unreadable) '{File}': {ExType}: {ExMessage}", item.NormalizedPath, readError.GetType().Name, readError.Message);
                    if (loggedFileWarnings == MaxLoggedFileWarnings)
                        YaguLog.For("ContentIndex").LogInformation("Build: further per-file skip details suppressed (>{MaxLoggedWarnings}); see the final report summary for counts.", MaxLoggedFileWarnings);
                }
                return;
            }

            contentBytesRead += read.BytesRead;
            if (read.Reason is IndexSkipReason.Binary or IndexSkipReason.UnsupportedEncoding)
                prefixRejectedFiles++;
            else
                fullyReadFiles++;

            long contentId = builder.AddClassifiedContent(item.NormalizedPath, read.Classification, read.Identity);
            if (contentId >= 0)
            {
                batchDocuments++;
                if (batchDocuments >= maxBatchDocuments || builder.RetainedTrigramCount >= maxBatchTrigrams)
                    FlushBatch();
            }
        }

        void FlushReadWindow()
        {
            if (readWindow.Count == 0)
                return;

            var items = readWindow.ToArray();
            readWindow.Clear();
            var outcomes = new (IndexFileReadResult Read, Exception? Error)[items.Length];
            if (readParallelism == 1 || items.Length == 1)
            {
                for (int i = 0; i < items.Length; i++)
                    outcomes[i] = items[i].MetadataVerdict == IndexSkipReason.None
                        ? ReadCandidate(items[i].Path, items[i].Length, 0)
                        : default;
            }
            else
            {
                var tasks = new Task<(IndexFileReadResult Read, Exception? Error)>[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    int captured = i;
                    tasks[i] = items[i].MetadataVerdict == IndexSkipReason.None
                        ? Task.Run(() => ReadCandidate(items[captured].Path, items[captured].Length, captured), cancellationToken)
                        : Task.FromResult(default((IndexFileReadResult Read, Exception? Error)));
                }
                outcomes = Task.WhenAll(tasks).GetAwaiter().GetResult();
            }

            // Task.WhenAll preserves input order. Only this thread mutates builder/report/store state.
            for (int i = 0; i < items.Length; i++)
                CommitReadOutcome(items[i], outcomes[i].Read, outcomes[i].Error);
        }

        var crawlCompletion = new IndexCrawlCompletion();
        foreach (IndexCrawlEntry entry in IndexFileCrawler.EnumerateFiles(
               normalizedRoot, policy, indexRoot, cancellationToken,
               fileSystem: _crawlerFileSystem, completion: crawlCompletion))
        {
            if (diskLimit > 0 && ++filesSinceDiskCheck >= _diskCheckEveryFiles)
            {
                filesSinceDiskCheck = 0;
                CheckDiskSpace();
            }
            cancellationToken.ThrowIfCancellationRequested();
            string file = entry.Path;
            string normFile = IndexScopeIdentity.NormalizePath(file);

            // Length + attributes come straight from the crawl enumeration record — no per-file
            // File.GetAttributes and no new FileInfo(path) restat in the build loop (plan §5.4). A file
            // that vanishes between enumeration and the content read is caught by the reader open below.
            long length = entry.Length;
            FileAttributes attributes = entry.Attributes;

            // Crawl progress: this file's size is known and readable. Count it toward the estimate and
            // report periodically (cheap; the callback marshals to the UI thread and is throttled there).
            bytesCrawled += length;
            filesCrawled++;
            if (progress is not null && ++filesSinceProgress >= _progressEveryFiles)
            {
                filesSinceProgress = 0;
                progress(new IndexBuildProgress(bytesCrawled, filesCrawled));
            }

            var candidate = new IngestionFileInfo(
                normFile,
                length,
                DepthUnder(normalizedRoot, normFile),
                attributes.HasFlag(FileAttributes.Hidden),
                attributes.HasFlag(FileAttributes.ReparsePoint),
                IsCloudOnly: false);

            IndexSkipReason metadataVerdict = IndexIngestionClassifier.ClassifyFile(candidate, policy);
            readWindow.Add((file, normFile, length, metadataVerdict));
            if (readWindow.Count >= readParallelism)
                FlushReadWindow();
        }

        FlushReadWindow();

        if (!crawlCompletion.IsComplete)
        {
            throw new IOException(
                $"The index crawl became incomplete while reading '{crawlCompletion.FailedDirectory!}': "
                + crawlCompletion.Failure!);
        }

        ValidateBuildCompletion(normalizedRoot, startVolume, checkpoint);

        // Publish the final (partial) batch — this also publishes the base for an empty corpus or one that
        // fit entirely in the first batch. Skip a trailing empty batch when a base was already published.
        if (!basePublished || batchDocuments > 0)
            FlushBatch();

        // Final progress point so the last (< ProgressEveryFiles) chunk is reflected.
        progress?.Invoke(new IndexBuildProgress(bytesCrawled, filesCrawled));

        long elapsedMs = Environment.TickCount64 - startTicks;
        YaguLog.For("ContentIndex").LogInformation(
            "Build complete: root='{Root}' scope={Scope} in {BatchCount} batch(es) ({BuildMode}), {ElapsedMs:N0} ms. " +
            "Content I/O: {ContentBytesRead:N0} bytes read, {PrefixRejected:N0} binary/BOM prefix-rejected, {FullyRead:N0} fully read. {ReportSummary}",
            normalizedRoot, scopeId, batchNumber, batchNumber > 1 ? "paged" : "single generation", elapsedMs,
            contentBytesRead, prefixRejectedFiles, fullyReadFiles, report.Summarize());

        return new BuildScopeResult(scopeId, lastPublish, report)
        {
            IoStats = new IndexBuildIoStats(contentBytesRead, prefixRejectedFiles, fullyReadFiles),
        };
    }

    private void ValidateBuildCompletion(
        string normalizedRoot,
        VolumeBinding startVolume,
        UsnCheckpoint startCheckpoint)
    {
        if (!Directory.Exists(normalizedRoot))
            throw new IndexVolumeChangedException($"The indexed root '{normalizedRoot}' was disconnected during the build.");

        VolumeBinding endVolume = _volumeBindingReader(normalizedRoot)
            ?? throw new IndexVolumeChangedException($"The mounted volume for '{normalizedRoot}' became unavailable during the build.");
        if (!VolumeBindingReader.Matches(startVolume, endVolume))
            throw new IndexVolumeChangedException($"The mounted volume for '{normalizedRoot}' changed during the build.");

        if (startCheckpoint.JournalId == 0)
            throw new IndexVolumeChangedException($"The indexed volume for '{normalizedRoot}' has no trustworthy change-journal checkpoint.");
        UsnJournalInfo? journal = _journalInfoReader(normalizedRoot);
        if (journal is not { } current
            || current.UsnJournalId != startCheckpoint.JournalId
            || startCheckpoint.NextUsn < current.FirstUsn
            || startCheckpoint.NextUsn > current.NextUsn)
        {
            throw new IndexVolumeChangedException(
                $"The change journal for '{normalizedRoot}' reset or lost continuity during the build.");
        }
    }

    /// <summary>
    /// Builds (or refreshes) the PDF-text extended-source namespace for <paramref name="rootDirectory"/>
    /// (plan §7 Phase 4). For each PDF under the root it runs <paramref name="extractor"/>, reduces the
    /// ephemeral text to trigram postings (never storing text — §6.4), captures build-time file identity +
    /// a USN checkpoint for freshness, then runs the determinism repeatability proof. The namespace is
    /// PUBLISHED only when the proof passes; otherwise any stale namespace is deleted so a non-repeatable
    /// tool can never prune. Off-by-default feature — callers gate on <c>settings.IndexBuildPdfTextExtendedSource</c>.
    /// Honors cancellation; extraction failures degrade to live-extraction (never fatal).
    /// </summary>
    public async Task<PdfExtendedSourceBuildResult> BuildPdfExtendedSourceAsync(
        string rootDirectory,
        IndexIngestionPolicy policy,
        PdfTextExtractor extractor,
        CancellationToken cancellationToken = default,
        Action<PdfBuildProgress>? progress = null)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return await BuildPdfExtendedSourceUnderLeaseAsync(
            mutation, rootDirectory, policy, extractor, cancellationToken, progress).ConfigureAwait(false);
    }

    internal async Task<PdfExtendedSourceBuildResult> BuildPdfExtendedSourceUnderLeaseAsync(
        IndexMutationContext mutation,
        string rootDirectory,
        IndexIngestionPolicy policy,
        PdfTextExtractor extractor,
        CancellationToken cancellationToken = default,
        Action<PdfBuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(extractor);

        string normalizedRoot = IndexScopeIdentity.NormalizePath(rootDirectory);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"Index root does not exist: {normalizedRoot}");

        string volume = Path.GetPathRoot(normalizedRoot)!;
        string scopeId = IndexScopeIdentity.ComputeScopeId(volume, normalizedRoot);
        ExtendedSourceStore store = _extendedSourceStoreFactory(scopeId);

        ExtractorFingerprint? fingerprint = PdfExtractorFingerprint.TryCompute(extractor);
        if (fingerprint is null)
        {
            // No usable pdftotext.exe → cannot fingerprint → never prune. Drop any stale namespace.
            store.DeleteUnderLease(mutation, SpecialSourceKind.PdfText);
            YaguLog.For("ContentIndex").LogInformation(
                "PDF extended-source skipped for '{Root}': pdftotext.exe unavailable (scope {Scope}).",
                normalizedRoot, scopeId);
            return new PdfExtendedSourceBuildResult(scopeId, PdfExtendedSourceBuildStatus.SkippedToolUnavailable, 0, 0, PdfDeterminismVerdict.NotProven);
        }

        // Capture the freshness checkpoint BEFORE enumerating so a change during the build is replayed and the
        // affected PDF live-extracted (never missed) at query time.
        UsnCheckpoint checkpoint = _checkpointReader(normalizedRoot) ?? UsnCheckpoint.None;
        string indexRoot = IndexScopeIdentity.NormalizePath(_paths.IndexRoot);
        List<string> pdfPaths = CollectPdfPaths(normalizedRoot, indexRoot, policy, cancellationToken);
        if (pdfPaths.Count == 0)
        {
            // No PDFs → an existing namespace would only mislead the gate into pruning known-old keys; drop it.
            store.DeleteUnderLease(mutation, SpecialSourceKind.PdfText);
            return new PdfExtendedSourceBuildResult(scopeId, PdfExtendedSourceBuildStatus.NoPdfs, 0, 0, PdfDeterminismVerdict.NotProven);
        }

        var populator = new PdfExtendedSourcePopulator(extractor, fingerprint, p => FileIdentityReader.TryGetIdentity(p)?.FileId);
        PdfPopulationResult population = await populator
            .PopulateAsync(pdfPaths, normalizedRoot, checkpoint, cancellationToken: cancellationToken, progress: progress)
            .ConfigureAwait(false);

        if (!population.IsPrunable)
        {
            // Not proven repeatable → NEVER prune. Remove any prior namespace so the gate live-extracts.
            store.DeleteUnderLease(mutation, SpecialSourceKind.PdfText);
            YaguLog.For("ContentIndex").LogInformation(
                "PDF extended-source NOT published for '{Root}': determinism={Determinism} (scope {Scope}); PDFs live-extract.",
                normalizedRoot, population.Determinism, scopeId);
            return new PdfExtendedSourceBuildResult(scopeId, PdfExtendedSourceBuildStatus.SkippedNotDeterministic,
                population.PdfsSeen, population.Admitted, population.Determinism);
        }

        if (!store.PublishUnderLease(mutation, population.Namespace))
            throw new IOException("The PDF-text namespace failed validation or publication.");
        YaguLog.For("ContentIndex").LogInformation(
            "PDF extended-source published for '{Root}': {Admitted}/{PdfsSeen} admitted (scope {Scope}).",
            normalizedRoot, population.Admitted, population.PdfsSeen, scopeId);
        return new PdfExtendedSourceBuildResult(scopeId, PdfExtendedSourceBuildStatus.Published,
            population.PdfsSeen, population.Admitted, population.Determinism);
    }

    /// <summary>Enumerates PDF files under a root that pass the ingestion metadata gate (size/exclusion/hidden).</summary>
    private List<string> CollectPdfPaths(string normalizedRoot, string indexRoot, IndexIngestionPolicy policy, CancellationToken cancellationToken)
    {
        var pdfPaths = new List<string>();
        foreach (IndexCrawlEntry entry in IndexFileCrawler.EnumerateFiles(
                     normalizedRoot, policy, indexRoot, cancellationToken, _crawlerFileSystem))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = entry.Path;
            if (!string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase))
                continue;
            string normFile = IndexScopeIdentity.NormalizePath(file);

            // Length + attributes come from the crawl record — no FileInfo restat (plan §5.4).
            var candidate = new IngestionFileInfo(
                normFile,
                entry.Length,
                DepthUnder(normalizedRoot, normFile),
                entry.Attributes.HasFlag(FileAttributes.Hidden),
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
                IsCloudOnly: false);
            if (IndexIngestionClassifier.ClassifyFile(candidate, policy) != IndexSkipReason.None)
                continue; // excluded/over-cap PDFs are NOT in the namespace → the gate live-extracts them

            pdfPaths.Add(normFile);
        }
        return pdfPaths;
    }

    /// <summary>Builds a positive-only image OCR namespace. It can prioritize prior positive candidates,
    /// but OCR is non-deterministic so nonmembers are always recognized live and are never pruned.</summary>
    public async Task<ImageOcrExtendedSourceBuildResult> BuildImageOcrExtendedSourceAsync(
        string rootDirectory,
        IndexIngestionPolicy policy,
        IOcrEngine engine,
        IReadOnlySet<string> extensions,
        string model,
        int maxSide,
        CancellationToken cancellationToken = default,
        Action<ImageOcrBuildProgress>? progress = null)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return await BuildImageOcrExtendedSourceUnderLeaseAsync(
            mutation, rootDirectory, policy, engine, extensions, model, maxSide, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    internal async Task<ImageOcrExtendedSourceBuildResult> BuildImageOcrExtendedSourceUnderLeaseAsync(
        IndexMutationContext mutation,
        string rootDirectory,
        IndexIngestionPolicy policy,
        IOcrEngine engine,
        IReadOnlySet<string> extensions,
        string model,
        int maxSide,
        CancellationToken cancellationToken = default,
        Action<ImageOcrBuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(extensions);

        string normalizedRoot = IndexScopeIdentity.NormalizePath(rootDirectory);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"Index root does not exist: {normalizedRoot}");
        string scopeId = ScopeIdForRoot(normalizedRoot);
        ExtendedSourceStore store = _extendedSourceStoreFactory(scopeId);
        ExtractorFingerprint? fingerprint = _ocrFingerprintReader(engine.Id, model, maxSide);
        if (fingerprint is null)
        {
            store.DeleteUnderLease(mutation, SpecialSourceKind.ImageOcr);
            return new ImageOcrExtendedSourceBuildResult(
                scopeId, ImageOcrExtendedSourceBuildStatus.SkippedEngineUnavailable, 0, 0, 0);
        }

        UsnCheckpoint checkpoint = _checkpointReader(normalizedRoot) ?? UsnCheckpoint.None;
        string indexRoot = IndexScopeIdentity.NormalizePath(_paths.IndexRoot);
        List<string> imagePaths = CollectImagePaths(normalizedRoot, indexRoot, policy, extensions, cancellationToken);
        if (imagePaths.Count == 0)
        {
            store.DeleteUnderLease(mutation, SpecialSourceKind.ImageOcr);
            return new ImageOcrExtendedSourceBuildResult(scopeId, ImageOcrExtendedSourceBuildStatus.NoImages, 0, 0, 0);
        }

        var populator = new ImageOcrExtendedSourcePopulator(
            engine, fingerprint, path => FileIdentityReader.TryGetIdentity(path)?.FileId);
        try
        {
            ImageOcrPopulationResult population = await populator.PopulateAsync(
                imagePaths, normalizedRoot, checkpoint, cancellationToken, progress).ConfigureAwait(false);
            if (!store.PublishUnderLease(mutation, population.Namespace))
                throw new IOException("The image-text namespace failed validation or publication.");
            YaguLog.For("ContentIndex").LogInformation(
                "Image OCR extended-source published for '{Root}': {Admitted}/{ImagesSeen} admitted, {Failed} failed (scope {Scope}).",
                normalizedRoot, population.Admitted, population.ImagesSeen, population.Failed, scopeId);
            return new ImageOcrExtendedSourceBuildResult(
                scopeId, ImageOcrExtendedSourceBuildStatus.Published,
                population.ImagesSeen, population.Admitted, population.Failed);
        }
        catch (ImageOcrIndexUnavailableException ex)
        {
            store.DeleteUnderLease(mutation, SpecialSourceKind.ImageOcr);
            YaguLog.For("ContentIndex").LogInformation(
                "Image OCR extended-source skipped for '{Root}': {Reason}", normalizedRoot, ex.Message);
            return new ImageOcrExtendedSourceBuildResult(
                scopeId, ImageOcrExtendedSourceBuildStatus.SkippedEngineUnavailable, imagePaths.Count, 0, imagePaths.Count);
        }
    }

    private List<string> CollectImagePaths(
        string normalizedRoot,
        string indexRoot,
        IndexIngestionPolicy policy,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (IndexCrawlEntry entry in IndexFileCrawler.EnumerateFiles(
                     normalizedRoot, policy, indexRoot, cancellationToken, _crawlerFileSystem))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(entry.Path).TrimStart('.');
            if (!extensions.Contains(extension))
                continue;
            string normalized = IndexScopeIdentity.NormalizePath(entry.Path);
            var candidate = new IngestionFileInfo(
                normalized,
                entry.Length,
                DepthUnder(normalizedRoot, normalized),
                entry.Attributes.HasFlag(FileAttributes.Hidden),
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
                IsCloudOnly: false);
            if (IndexIngestionClassifier.ClassifyFile(candidate, policy) == IndexSkipReason.None)
                paths.Add(normalized);
        }
        return paths;
    }

    /// <summary>Reports the status of a scope by id.</summary>
    public IndexScopeStatus GetStatus(string scopeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
        var generation = store.TryOpenCurrent();
        return generation is null
            ? new IndexScopeStatus(false, null, null)
            : new IndexScopeStatus(true, generation.Manifest, generation.Report.Summarize());
    }

    /// <summary>Reports the status of the scope covering a root directory.</summary>
    public IndexScopeStatus GetStatusForRoot(string rootDirectory) => GetStatus(ScopeIdForRoot(rootDirectory));

    /// <summary>Reads only pointer files and manifests. This reports presence/metadata and never labels an
    /// index structurally valid; use <see cref="ValidateScopeUnderLease"/> for full validation.</summary>
    public IndexMetadataStatus GetMetadataStatus(string scopeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
        bool exists = Directory.Exists(store.ScopeDirectory)
            && (File.Exists(Path.Combine(store.ScopeDirectory, "current.a"))
                || File.Exists(Path.Combine(store.ScopeDirectory, "current.b")));
        StoredIndexStat stat = store.ReadStorageStat();
        bool rootExists = stat.RootPath is not null && Directory.Exists(stat.RootPath);
        IndexStorageHealth health = stat.Readable && stat.RootPath is not null && !rootExists
            ? IndexStorageHealth.SourceMissing
            : stat.Health;
        string? problem = health == IndexStorageHealth.SourceMissing
            ? "The indexed source folder no longer exists or is unavailable."
            : stat.Problem;
        return new IndexMetadataStatus(
            exists,
            stat.Readable,
            stat.DocumentCount,
            stat.SegmentCount,
            stat.BuiltUtc,
            stat.CreatedUtc,
            stat.LastIncrementalUpdateUtc,
            stat.RootPath,
            health,
            problem);
    }

    public IndexMetadataStatus GetMetadataStatusForRoot(string rootDirectory)
        => GetMetadataStatus(ScopeIdForRoot(rootDirectory));

    /// <summary>
    /// Size of one root's active index layers (base plus every referenced segment), or 0 when there is
    /// no readable index. Cheap: reads the pointer slot and the layer directory sizes, never the
    /// generation contents.
    /// </summary>
    public long GetActiveIndexBytesForRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return 0;
        try
        {
            var store = new ContentIndexStore(_paths, ScopeIdForRoot(rootDirectory), _retainedGenerations);
            return store.TotalActiveIndexBytes();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "Could not measure the active index for '{Root}'.", rootDirectory);
            return 0;
        }
    }

    /// <summary>
    /// One root's active layers split into base / full-build paging / incremental cohorts, or
    /// <see langword="null"/> when there is no readable index or any active layer cannot be identified.
    /// Cheap: pointer slot, manifests, and directory sizes only.
    /// </summary>
    public ActiveLayerStorageBreakdown? TryReadActiveLayerStorageBreakdownForRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return null;
        try
        {
            var store = new ContentIndexStore(_paths, ScopeIdForRoot(rootDirectory), _retainedGenerations);
            return store.TryReadActiveLayerStorageBreakdown();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "Could not break down the active index layers for '{Root}'.", rootDirectory);
            return null;
        }
    }

    /// <summary>Active storage cohorts plus their incremental time span, read from pointer/manifests and
    /// directory sizes only. Used for user-facing cleanup forecasts without opening index contents.</summary>
    public ActiveLayerStorageTrend? TryReadActiveLayerStorageTrendForRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return null;
        try
        {
            var store = new ContentIndexStore(_paths, ScopeIdForRoot(rootDirectory), _retainedGenerations);
            return store.TryReadActiveLayerStorageTrend();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "Could not read the active index trend for '{Root}'.", rootDirectory);
            return null;
        }
    }

    /// <summary>
    /// Whether one root's accumulated update history needs clean-up, and whether every allowed automatic
    /// path to reclaim it is unavailable. Manifest-only and directory sizes: it never opens content or
    /// posting data. Returns <see cref="IndexReclamationDiagnosis.Healthy"/> when the index cannot be
    /// measured, so an unreadable index is never reported as blocked.
    /// </summary>
    public IndexReclamationDiagnosis DiagnoseReclamation(
        string rootDirectory,
        EffectiveIndexSizePolicy policy,
        int maxDeltaSegments,
        int compactionThresholdMB)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return IndexReclamationDiagnosis.Healthy;
        try
        {
            var store = new ContentIndexStore(_paths, ScopeIdForRoot(rootDirectory), _retainedGenerations);
            if (store.TryReadActiveLayerStorageBreakdown() is not { } breakdown)
                return IndexReclamationDiagnosis.Healthy;
            bool hasRun = policy.AllowsCoalescing && store.TryFindIncrementalSegmentRun(
                policy.CoalesceMinRun,
                EffectiveIndexSizePolicy.MaximumCoalesceRun,
                policy.CoalesceMaxSegmentBytes,
                policy.CoalesceMaxBatchBytes,
                out _);
            return IndexReclamationAdvisor.Diagnose(
                breakdown, policy, maxDeltaSegments, compactionThresholdMB, hasRun);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "Could not diagnose reclamation for '{Root}'.", rootDirectory);
            return IndexReclamationDiagnosis.Healthy;
        }
    }

    public bool HasCurrentIndex(string rootDirectory)
    {
        IndexMetadataStatus status = GetMetadataStatusForRoot(rootDirectory);
        return status.Exists && status.MetadataReadable && status.Health == IndexStorageHealth.Healthy;
    }

    /// <summary>
    /// Chooses the single physical index that should accelerate a search rooted at
    /// <paramref name="searchRoot"/>. A healthy registered ancestor (for example <c>C:\</c> for a
    /// <c>C:\src</c> search) is preferred, so one broad index serves every descendant search and Yagu
    /// never opens parent and child indexes together. If no maintained covering index is usable, an
    /// exact on-disk leftover index remains a compatibility fallback. Returns the normalized search root
    /// when neither exists; callers then follow their normal no-index/live-scan path.
    /// </summary>
    public string ResolveBestAvailableIndexRoot(string searchRoot, IEnumerable<string>? registeredRoots)
    {
        string normalizedSearchRoot = IndexScopeIdentity.NormalizePath(searchRoot);
        IEnumerable<string> covering = (registeredRoots ?? Array.Empty<string>())
            .Where(root => IndexedRootsPolicy.Covers(root, normalizedSearchRoot))
            .Select(IndexScopeIdentity.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(root => root.Length);

        foreach (string root in covering)
        {
            try
            {
                if (HasCurrentIndex(root))
                    return root;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Try the next covering root; failure always degrades to an exact index or live scan.
            }
        }

        try
        {
            if (HasCurrentIndex(normalizedSearchRoot))
                return normalizedSearchRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Return the search root; the caller's ordinary scope open will fail safely to a live scan.
        }
        return normalizedSearchRoot;
    }

    internal IndexValidationResult ValidateScopeUnderLease(
        IndexMutationContext mutation,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        cancellationToken.ThrowIfCancellationRequested();
        string scopeId = ScopeIdForRoot(rootDirectory);
        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
        ContentIndexStore.LayeredIndexHandle? handle = store.TryOpenLayered(
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (handle is null)
        {
            IndexMetadataStatus metadata = GetMetadataStatus(scopeId);
            return new IndexValidationResult(false,
                metadata.Exists ? "The active base or one of its segments is missing, corrupt, or incompatible." : "No index exists.",
                checked((int)Math.Min(int.MaxValue, metadata.DocumentCount)), metadata.SegmentCount, metadata.RootPath);
        }

        long documentCount = handle.Base.Manifest.ContentCount;
        foreach (ContentIndexDeltaSegment segment in handle.Segments)
            documentCount += segment.Added.Manifest.ContentCount;
        return new IndexValidationResult(
            true,
            null,
            checked((int)Math.Min(int.MaxValue, documentCount)),
            handle.Segments.Count,
            handle.Base.Manifest.NormalizedRootPath);
    }

    /// <summary>Returns whether the storage directory contains any scope with readable pointer/manifest
    /// metadata. This is a cheap prior-use probe: it does not load postings, measure directory sizes, or
    /// imply that an unregistered stored index should be trusted for pruning or maintenance.</summary>
    public bool HasReadableStoredIndex()
    {
        foreach (string scopeDir in SafeGetDirectories(_paths.IndexRoot))
        {
            string scopeId = Path.GetFileName(scopeDir);
            if (!IsScopeDirectoryName(scopeId))
                continue;

            StoredIndexStat stat = new ContentIndexStore(_paths, scopeId, _retainedGenerations).ReadStorageStat();
            if (stat.Readable && !string.IsNullOrWhiteSpace(stat.RootPath))
                return true;
        }

        return false;
    }

    /// <summary>Returns source roots from readable stored indexes that can be adopted after settings were
    /// removed. Reads only pointer/manifest metadata and never measures or opens the full index.</summary>
    public IReadOnlyList<string> GetReusableStoredIndexRoots()
    {
        var roots = new List<string>();
        foreach (string scopeDir in SafeGetDirectories(_paths.IndexRoot))
        {
            string scopeId = Path.GetFileName(scopeDir);
            if (!IsScopeDirectoryName(scopeId))
                continue;

            StoredIndexStat stat = new ContentIndexStore(_paths, scopeId, _retainedGenerations).ReadStorageStat();
            if (stat.Readable
                && !string.IsNullOrWhiteSpace(stat.RootPath)
                && Directory.Exists(stat.RootPath)
                && !roots.Contains(stat.RootPath, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(stat.RootPath);
            }
        }

        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots;
    }

    /// <summary>
    /// Gathers on-disk storage stats for every scope under the index storage directory (plan §6.2): each
    /// index's size, stored content-record count, segment count, build time, and root, plus totals. Record/build
    /// metadata is read from <b>manifests only</b> (<see cref="ContentIndexStore.ReadStorageStat"/>), so
    /// reporting a paged multi-GB index never loads a generation into memory. Never throws — an unreadable
    /// scope is reported with <c>Readable == false</c> and its on-disk size still counted.
    /// </summary>
    public IndexStorageSummary GetStorageStats()
    {
        string storageDir = _paths.IndexRoot;
        var indexes = new List<IndexStorageStat>();
        long totalBytes = 0;
        long totalDocuments = 0;

        foreach (string scopeDir in SafeGetDirectories(storageDir))
        {
            string scopeId = Path.GetFileName(scopeDir);
            if (!IsScopeDirectoryName(scopeId))
                continue; // Ignore writer metadata and abandoned staging folders; crash recovery owns those.
            long sizeBytes = DirectorySizeBytes(scopeDir);
            var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
            StoredIndexStat stat = store.ReadStorageStat();
            bool rootExists = stat.RootPath is not null && Directory.Exists(stat.RootPath);
            IndexStorageHealth health = stat.Readable && stat.RootPath is not null && !rootExists
                ? IndexStorageHealth.SourceMissing
                : stat.Health;
            string? problem = health == IndexStorageHealth.SourceMissing
                ? "The indexed source folder no longer exists or is unavailable."
                : stat.Problem;

            totalBytes += sizeBytes;
            totalDocuments += stat.DocumentCount;
            indexes.Add(new IndexStorageStat(
                scopeId, stat.RootPath, sizeBytes, stat.DocumentCount, stat.SegmentCount, stat.BuiltUtc,
                health, rootExists, problem)
            {
                CreatedUtc = stat.CreatedUtc,
                LastIncrementalUpdateUtc = stat.LastIncrementalUpdateUtc,
            });
        }

        // Largest first so the biggest consumers surface at the top of the list.
        indexes.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        YaguLog.For("ContentIndex").LogDebug(
            "Storage stats: {IndexCount} index(es), {TotalBytes:N0} bytes, {TotalDocuments:N0} documents under '{StorageDir}'.",
            indexes.Count, totalBytes, totalDocuments, storageDir);
        return new IndexStorageSummary(indexes, totalBytes, totalDocuments, storageDir);
    }

    private static bool IsScopeDirectoryName(string name)
        => name.Length == 32 && name.All(Uri.IsHexDigit);

    private static IReadOnlyList<string> SafeGetDirectories(string dir)
        => SafeGetDirectories(
            () => Directory.Exists(dir),
            () => Directory.GetDirectories(dir));

    internal static IReadOnlyList<string> SafeGetDirectories(
        Func<bool> directoryExists,
        Func<string[]> getDirectories)
    {
        try
        {
            return directoryExists() ? getDirectories() : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The index storage drive's used-space percentage (0–100), or null when it can't be read.</summary>
    private static double? RealDiskUsedPercent(string path)
        => RealDiskUsedPercent(path, root =>
        {
            var drive = new DriveInfo(root);
            return (drive.IsReady, drive.TotalSize, drive.AvailableFreeSpace);
        });

    internal static double? RealDiskUsedPercent(
        string path,
        Func<string, (bool IsReady, long TotalSize, long AvailableFreeSpace)> driveSpaceReader)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path))!;
            var drive = driveSpaceReader(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return null;
            return (double)(drive.TotalSize - drive.AvailableFreeSpace) * 100 / drive.TotalSize;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long DirectorySizeBytes(string dir)
        => DirectorySizeBytes(
            () => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories),
            file => new FileInfo(file).Length);

    internal static long DirectorySizeBytes(
        Func<IEnumerable<string>> enumerateFiles,
        Func<string, long> fileLengthReader)
    {
        long total = 0;
        try
        {
            foreach (string file in enumerateFiles())
            {
                try { total += fileLengthReader(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip a vanished/locked file */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a partially-readable scope still reports what it could sum.
        }
        return total;
    }

    /// <summary>
    /// True when the change journal proves the scope covering <paramref name="rootDirectory"/> has changed
    /// since its index was built (plan §6.1 — drives <c>AutomaticFullRebuildWhenDirty</c>). Returns false
    /// when there is no index (that is "missing", handled by a build, not a rebuild), when the root is
    /// unchanged, or when freshness cannot be proven (a non-continuous journal). The one exception is a
    /// legacy ReFS identity mismatch after a proven journal change: it is stale so automatic maintenance can
    /// perform the required one-time compatibility rebuild. Never throws — other errors are "not stale".
    /// The journal reader is injectable for testing; production reads the real USN journal.
    /// </summary>
    public bool IsScopeStale(string rootDirectory, ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
        => GetScopeFreshnessState(rootDirectory, journalReader) == ScopeFreshnessState.Dirty;

    /// <summary>
    /// Proactively "re-anchors" a <b>fresh</b> (unchanging) scope's base freshness checkpoint to the current
    /// journal position, so its checkpoint does not silently age out of the fixed-size USN-journal window and
    /// force every future search to bypass the index (the <c>JournalDiscontinuity/GapDetected</c> case). It
    /// replays the change journal from the base checkpoint: only when that replay is <b>continuous</b> (the
    /// checkpoint is not yet purged) AND proves <b>zero</b> content changes under the root is it safe to
    /// advance the checkpoint — the generation's content is still accurate as of the new position. The
    /// advance is a cheap manifest-only rewrite of a base-only scope (<see cref="ContentIndexStore.TryReanchorBaseCheckpoint"/>);
    /// a segmented scope returns false (it re-anchors via compaction/rebuild instead). Returns true only when
    /// it actually advanced the checkpoint. Never throws. The journal reader is injectable for testing.
    /// </summary>
    public bool TryReanchorFreshScope(string rootDirectory, ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return TryReanchorFreshScopeUnderLease(mutation, rootDirectory, journalReader);
    }

    internal bool TryReanchorFreshScopeUnderLease(
        IndexMutationContext mutation,
        string rootDirectory,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        try
        {
            string scopeId = ScopeIdForRoot(rootDirectory);
            var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
            if (store.TryReadCurrentFreshnessInputs() is not { } inputs)
                return false;

            FreshnessRead freshness = ContentIndexFreshnessEvaluator.ReadDirtySince(
                inputs.Manifest.NormalizedRootPath, inputs.Manifest.FreshnessCheckpoint, inputs.FileIds, journalReader);

            // Advance ONLY when the journal is still continuous from the base checkpoint (nothing purged) and
            // proves no content under the root changed — then the index is valid as of the new checkpoint.
            // Whole-volume roots must also retain their checkpoint across unknown identities: they can be new
            // files, or old ReFS entries from the pre-V2-compatible identity format, and maintenance must see
            // them before the checkpoint advances. A subfolder may safely ignore volume-wide unknown records.
            string normalizedRoot = inputs.Manifest.NormalizedRootPath;
            string volumeRoot = Path.GetPathRoot(normalizedRoot)!;
            bool wholeVolume = string.Equals(
                IndexScopeIdentity.NormalizePath(volumeRoot),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase);
            bool hasUnknownWholeVolumeChange = wholeVolume
                && freshness.ResolvedJournalChangeCount < freshness.JournalChangeCount;
            if (!freshness.IsContinuous || freshness.Dirty.Count > 0 || hasUnknownWholeVolumeChange)
                return false;

            return store.TryReanchorBaseCheckpointUnderLease(mutation, freshness.NextCheckpoint);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compacts a scope's layered index into a fresh single base when it has accumulated more delta
    /// segments (or more accumulated delta bytes) than the configured bounds — even when the root is
    /// otherwise <b>fresh</b> (unchanging). A fresh but fragmented index still makes every query load
    /// base + N segments into memory; folding it to one base cuts that per-segment overhead and lets the
    /// base-only (out-of-process worker) query path engage. Returns true when it compacted. Never throws;
    /// a failure leaves the index exactly as-is (the caller keeps serving the layered index).
    /// </summary>
    public bool CompactScopeIfOverSegmented(string rootDirectory, IndexIngestionPolicy policy, IndexMaintenanceSettings settings, DateTimeOffset builtUtc)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return CompactScopeIfOverSegmentedUnderLease(mutation, rootDirectory, policy, settings, builtUtc);
    }

    internal bool CompactScopeIfOverSegmentedUnderLease(
        IndexMutationContext mutation,
        string rootDirectory,
        IndexIngestionPolicy policy,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(settings);
        bool smallSegmentsCoalesced = false;
        try
        {
            string scopeId = ScopeIdForRoot(rootDirectory);
            var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
            store.ProduceV3QueryStructures = settings.ProduceV3QueryStructures;
            int maxSegments = Math.Clamp(settings.MaxDeltaSegments, 1, 64);
            int thresholdMB = Math.Clamp(settings.CompactionThresholdMB, 1, 8192);
            EffectiveIndexSizePolicy size = settings.ResolveSizePolicy(rootDirectory);
            if (!store.ShouldCompact(maxSegments, thresholdMB))
                return false;

            // First collapse only bounded contiguous runs of small segments. This never opens the base or
            // unrelated layers and is therefore safe even when the full index is far above the in-process
            // compaction cap. It also lets a quiet existing scope make progress on fragmentation without
            // waiting for another source change to append a delta.
            var updater = new ContentIndexIncrementalUpdater(store, policy);
            smallSegmentsCoalesced = updater.CoalesceSmallSegmentsUnderLease(
                mutation, maxSegments, CancellationToken.None, size,
                IndexMergeResourceBudget.FromSettings(settings)) > 0;
            if (!store.ShouldCompact(maxSegments, thresholdMB))
                return smallSegmentsCoalesced;

            // Full compaction is a bounded-memory external merge, but a very large automatic pass still does
            // substantial sequential I/O and needs scratch space for sorted runs plus the replacement layer.
            // Above the per-index cap, leave the index segmented until the user explicitly approves that work.
            long indexBytes = store.TotalActiveIndexBytes();
            if (!size.AllowsCompactingIndexOf(indexBytes))
            {
                if (size.ExceedsBudget(indexBytes))
                {
                    YaguLog.For("ContentIndex").LogWarning("Scope {Scope} for '{Root}' is {IndexMB} MB, over its {BudgetMB} MB size budget, but its '{Mode}' size-management mode cannot reclaim further — rebuild this index to reclaim the space.", scopeId, rootDirectory, indexBytes / (1024 * 1024), size.SizeBudgetMB, size.Mode);
                }
                else
                {
                    YaguLog.For("ContentIndex").LogInformation("Skipping auto-compaction of over-segmented scope {Scope} for '{Root}': the index is {IndexMB} MB (> {MaxCompactMB} MB cap, mode '{Mode}') — leaving this large background I/O pass for explicit approval.", scopeId, rootDirectory, indexBytes / (1024 * 1024), size.MaxAutoCompactionSizeMB, size.Mode);
                }
                return smallSegmentsCoalesced;
            }
            YaguLog.For("ContentIndex").LogInformation("Compacting fresh over-segmented scope {Scope} for '{Root}' into a fresh base.", scopeId, rootDirectory);
            RunStreamingCompactionUnderLease(mutation, store, scopeId, rootDirectory, settings, builtUtc, null, CancellationToken.None);
            YaguLog.For("ContentIndex").LogInformation("Compaction complete for scope {Scope} ('{Root}').", scopeId, rootDirectory);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Over-segmented compaction failed for '{Root}' \u2014 left as-is.", rootDirectory);
            return smallSegmentsCoalesced;
        }
    }

    /// <summary>
    /// Explicit, user-approved <b>Compact now</b>: folds every active layer into a fresh base by streaming,
    /// regardless of the automatic-compaction size cap. The cap exists so background maintenance never
    /// starts an expensive fold on its own; it is not a correctness limit, and this path does not persist
    /// any change to it. Throws on failure so the caller can report why; the live index is untouched until
    /// the single pointer flip at the end.
    /// </summary>
    public void CompactScopeNow(
        string rootDirectory,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        CompactScopeNowUnderLease(mutation, rootDirectory, settings, builtUtc, progress, cancellationToken);
    }

    internal void CompactScopeNowUnderLease(
        IndexMutationContext mutation,
        string rootDirectory,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(settings);

        string scopeId = ScopeIdForRoot(rootDirectory);
        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations)
        {
            ProduceV3QueryStructures = settings.ProduceV3QueryStructures,
        };
        RunStreamingCompactionUnderLease(mutation, store, scopeId, rootDirectory, settings, builtUtc, progress, cancellationToken);
    }

    internal static void RunStreamingCompactionUnderLease(
        IndexMutationContext mutation,
        ContentIndexStore store,
        string scopeId,
        string rootDirectory,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        Action<int, string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke(2, IndexUpdateStages.CompactAnalyzing);
        if (!store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs)
            || baseDir is null)
        {
            throw new InvalidDataException("This index has no trusted active layers to compact.");
        }
        if (segmentDirs.Count == 0)
            return; // already a single base

        var layers = new List<string>(segmentDirs.Count + 1) { baseDir };
        layers.AddRange(segmentDirs);

        IndexMergeResourceBudget budget = IndexMergeResourceBudget.FromSettings(settings);
        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(store.IndexRootDirectory);
        var diskGuard = new IndexCompactionDiskGuard(
            store.IndexRootDirectory, budget.MinimumFreeSpaceMB, budget.MaxDiskUsagePercent);

        var mergeProgress = new IndexProgressReporter(
            progress is null
                ? null
                : percent => progress(percent, IndexUpdateStages.CompactMerging));
        mergeProgress.Report(10);
        StreamingSegmentRunMerger.MergeIntoBase(
            layers, workspace, budget.MemoryBudgetBytes, diskGuard,
            store.ProduceV3QueryStructures, builtUtc, mergeProgress.Slice(10, 89),
            cancellationToken);

        progress?.Invoke(90, IndexUpdateStages.CompactPublishing);
        store.CompactFromPreparedUnderLease(mutation, workspace.PreparedDirectory);
        progress?.Invoke(100, IndexUpdateStages.CompactPublishing);
        YaguLog.For("ContentIndex").LogInformation(
            "Streaming compaction folded base + {SegmentCount} segment(s) into a fresh base for scope {Scope} ('{Root}').",
            segmentDirs.Count, scopeId, rootDirectory);
    }

    /// <summary>Deletes one scope's index. Returns false if nothing was there.</summary>
    public bool DeleteScope(string scopeId)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return DeleteScopeUnderLease(mutation, scopeId);
    }

    internal bool DeleteScopeUnderLease(IndexMutationContext mutation, string scopeId)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
        bool existed = Directory.Exists(store.ScopeDirectory);
        store.DeleteScopeUnderLease(mutation);
        if (existed)
            YaguLog.For("ContentIndex").LogInformation("Deleted index for scope {Scope} ('{ScopeDir}').", scopeId, store.ScopeDirectory);
        else
            YaguLog.For("ContentIndex").LogInformation("Delete requested for scope {Scope} but no index existed.", scopeId);
        return existed;
    }

    /// <summary>Deletes every scope's index under the storage root. Returns the number of scopes removed.</summary>
    public int ClearAll()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return ClearAllUnderLease(mutation);
    }

    internal int ClearAllUnderLease(IndexMutationContext mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        int count = 0;
        int failed = 0;
        foreach (string scopeDir in Directory.GetDirectories(_paths.IndexRoot))
        {
            try
            {
                Directory.Delete(scopeDir, recursive: true);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                YaguLog.For("ContentIndex").LogWarning(ex, "Clear-all could not remove scope '{ScopeDir}' (left in place).", scopeDir);
                // Best effort; a locked scope is left in place.
            }
        }
        if (failed > 0)
            YaguLog.For("ContentIndex").LogInformation("Clear-all removed {Count} scope index(es), {Failed} left in place (locked).", count, failed);
        else
            YaguLog.For("ContentIndex").LogInformation("Clear-all removed {Count} scope index(es).", count);
        return count;
    }

    internal static int DepthUnder(string root, string file)
    {
        string relative = file[root.Length..].Trim('\\');
        return relative.Count(c => c == '\\') + 1;
    }
}
