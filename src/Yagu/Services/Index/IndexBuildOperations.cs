using System.Text.Json.Serialization;
using Yagu.Services.Pdf;

namespace Yagu.Services.Index;

/// <summary>Worker-safe constants used by index build snapshots. They deliberately do not depend on
/// <c>AppSettings</c>, so the maintenance worker never needs to link the settings/persistence graph.</summary>
internal static class IndexBuildDefaults
{
    public static int MemoryBudgetMB => MemoryBudgetMBFor(Environment.Is64BitProcess);
    internal static int MemoryBudgetMBFor(bool is64BitProcess) => is64BitProcess ? 384 : 192;
    public const int RetainedGenerations = 2;
    public const int MaxOperationRoots = 64;
    public const int MaxOperationJsonBytes = 1024 * 1024;
    public const int FileIoTimeoutSeconds = 30;
    public const int MinimumFileIoTimeoutSeconds = 1;
    public const int MaximumFileIoTimeoutSeconds = 600;
}

/// <summary>Serializable immutable-at-dispatch representation of <see cref="IndexIngestionPolicy"/>.</summary>
internal sealed class IndexIngestionPolicySnapshot
{
    public long MaxFileSizeBytes { get; set; }
    public string[] ExcludedGlobs { get; set; } = Array.Empty<string>();
    public string[] ExcludedExtensions { get; set; } = Array.Empty<string>();
    public bool IncludeHiddenFiles { get; set; }
    public bool FollowReparsePoints { get; set; }
    public int MaxDepth { get; set; }
    public string[] ReAdmitGlobs { get; set; } = Array.Empty<string>();
    public bool IndexBinaryAsciiContent { get; set; }

    public static IndexIngestionPolicySnapshot FromPolicy(IndexIngestionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new IndexIngestionPolicySnapshot
        {
            MaxFileSizeBytes = policy.MaxFileSizeBytes,
            ExcludedGlobs = policy.ExcludedGlobs.ToArray(),
            ExcludedExtensions = policy.ExcludedExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            IncludeHiddenFiles = policy.IncludeHiddenFiles,
            FollowReparsePoints = policy.FollowReparsePoints,
            MaxDepth = policy.MaxDepth,
            ReAdmitGlobs = policy.ReAdmitGlobs.ToArray(),
            IndexBinaryAsciiContent = policy.IndexBinaryAsciiContent,
        };
    }

    public IndexIngestionPolicy ToPolicy() => new(
        Math.Max(0, MaxFileSizeBytes),
        ExcludedGlobs ?? Array.Empty<string>(),
        new HashSet<string>(ExcludedExtensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
        IncludeHiddenFiles,
        FollowReparsePoints,
        Math.Max(0, MaxDepth),
        ReAdmitGlobs ?? Array.Empty<string>(),
        IndexBinaryAsciiContent);
}

/// <summary>Narrow, versioned snapshot for one explicit full build.</summary>
internal sealed class IndexBuildOperation
{
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;
    public string StorageDirectory { get; set; } = "";
    public int RetainedGenerations { get; set; } = IndexBuildDefaults.RetainedGenerations;
    public string Root { get; set; } = "";
    public IndexIngestionPolicySnapshot Policy { get; set; } = new();
    public int BuildMemoryBudgetMB { get; set; }
    public int BuildParallelism { get; set; } = 1;
    public int MaxDiskUsagePercent { get; set; }
    public int FileIoTimeoutSeconds { get; set; } = IndexBuildDefaults.FileIoTimeoutSeconds;
    public bool Rebuild { get; set; }
    public bool BuildPdfText { get; set; }
    public bool BuildImageText { get; set; }
    public string ImageOcrEngine { get; set; } = "paddle";
    public string ImageOcrModel { get; set; } = "ChineseV5";
    public int ImageOcrMaxSide { get; set; } = 960;
    public int ImageOcrWorkerParallelism { get; set; } = 1;
    public string[] ImageOcrExtensions { get; set; } = ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "webp"];

    /// <summary>Produce the additive format-v3 query structures during this build (plan §5.1). Off by
    /// default; no query path reads them yet.</summary>
    public bool ProduceV3QueryStructures { get; set; }
    /// <summary>Incremental-maintenance settings used only for the staged post-build freshness check.
    /// A negative threshold disables the check for internal/test callers; app-created operations always
    /// snapshot the normalized persisted setting.</summary>
    public IndexMaintenanceSettings PostBuildCatchUpSettings { get; set; } = new();
}

/// <summary>Worker-safe settings consumed by automatic refresh/compaction.</summary>
public sealed class IndexMaintenanceSettings
{
    public int BuildMemoryBudgetMB { get; set; }
    public int MaxDiskUsagePercent { get; set; }
    public bool BuildPdfText { get; set; }
    public bool BuildImageText { get; set; }
    public string ImageOcrEngine { get; set; } = "paddle";
    public string ImageOcrModel { get; set; } = "ChineseV5";
    public int ImageOcrMaxSide { get; set; } = 960;
    public int ImageOcrWorkerParallelism { get; set; } = 1;
    public string[] ImageOcrExtensions { get; set; } = ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "webp"];
    public bool ProduceV3QueryStructures { get; set; }
    public bool AutoRepair { get; set; } = true;
    public int MaxDeltaSegments { get; set; } = 8;
    public int CompactionThresholdMB { get; set; } = 256;
    public int MaxAutoCompactionSizeMB { get; set; } = 512;
    /// <summary>The configurable USN catch-up record cap (AppSettings.IndexMaxJournalCatchupRecords). A
    /// journal delta exceeding it reads as <c>UsnReadStatus.Incomplete</c> → the refresh treats freshness as
    /// discontinuous and needs a full rebuild rather than trusting a partial delta.</summary>
    public int MaxJournalCatchupRecords { get; set; } = 500_000;
    public int PostBuildCatchUpThresholdChanges { get; set; } = -1;
    public int FileIoTimeoutSeconds { get; set; } = IndexBuildDefaults.FileIoTimeoutSeconds;
}

/// <summary>One ordered root and its already-resolved ingestion policy.</summary>
internal sealed class IndexMaintenanceRootOperation
{
    public string Root { get; set; } = "";
    public IndexIngestionPolicySnapshot Policy { get; set; } = new();
    public int BuildParallelism { get; set; } = 1;
}

/// <summary>Narrow, versioned snapshot for an ordered automatic maintenance pass.</summary>
internal sealed class IndexMaintenanceOperation
{
    public const int CurrentVersion = 5;
    public const string ModeBuildDue = "buildDue";
    public const string ModeIncremental = "incremental";

    public int Version { get; set; } = CurrentVersion;
    public string StorageDirectory { get; set; } = "";
    public int RetainedGenerations { get; set; } = IndexBuildDefaults.RetainedGenerations;
    public string Mode { get; set; } = ModeIncremental;
    public bool RebuildWhenDirty { get; set; }
    /// <summary>When an incremental refresh cannot prove journal continuity, permit a full rebuild.
    /// Automatic and user-requested incremental maintenance set this false so a large drive is never
    /// unexpectedly rebuilt for an hour; explicit full-build operations remain separate.</summary>
    public bool AllowFullRebuildFallback { get; set; } = true;
    /// <summary>Permit the bounded one-time rebuild required to migrate an older ReFS identity format.
    /// Automatic maintenance keeps this enabled; the user's explicitly incremental-only action disables it.</summary>
    public bool AllowCompatibilityRebuild { get; set; } = true;
    /// <summary>Bypass the cheap "proven dirty" preflight and attempt the journal refresh directly.
    /// Used only by the user's explicit Update action (typically after raising the catch-up cap); normal
    /// background passes retain the cheap preflight and never load a large fresh index unnecessarily.</summary>
    public bool ForceRefresh { get; set; }
    public IndexMaintenanceSettings Settings { get; set; } = new();
    public IndexMaintenanceRootOperation[] Roots { get; set; } = Array.Empty<IndexMaintenanceRootOperation>();
}

/// <summary>Narrow validation request; structural validation is intentionally separate from metadata status.</summary>
internal sealed class IndexValidationOperation
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string StorageDirectory { get; set; } = "";
    public int RetainedGenerations { get; set; } = IndexBuildDefaults.RetainedGenerations;
    public string Root { get; set; } = "";
}

internal static class IndexMaintenanceActions
{
    public const string Built = "built";
    public const string DeltaAppended = "deltaAppended";
    public const string Compacted = "compacted";
    public const string Reanchored = "reanchored";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>Bounded result for one root in an automatic maintenance pass.</summary>
internal sealed class IndexMaintenanceRootResult
{
    public string Root { get; set; } = "";
    public string Action { get; set; } = IndexMaintenanceActions.Skipped;
    public string Outcome { get; set; } = "ok";
    public int IndexedCount { get; set; }
    public int SkippedCount { get; set; }
    public string? PdfStatus { get; set; }
    public string? ImageOcrStatus { get; set; }
    public string? Warning { get; set; }
}

internal sealed class IndexMaintenanceResultEnvelope
{
    public int Version { get; set; } = 1;
    public List<IndexMaintenanceRootResult> Roots { get; set; } = new();
}

/// <summary>Success payload shared by worker-hosted and in-process full builds.</summary>
internal readonly record struct IndexBuildSuccess(
    string ScopeId,
    string ActiveBaseGenerationId,
    long ActivePointerSequence,
    string LastPublishedArtifactId,
    string Summary,
    int IndexedCount,
    int TotalSkipped,
    string? PdfStatus,
    int PdfsSeen,
    int PdfAdmitted,
    string? PdfDeterminism,
    string? ImageOcrStatus,
    int ImagesSeen,
    int ImagesAdmitted,
    int ImagesFailed,
    PostBuildCatchUpResult PostBuildCatchUp);

internal readonly record struct PostBuildCatchUpResult(
    bool Checked,
    int ThresholdChanges,
    IncrementalUpdateOutcome Outcome,
    int JournalChangeCount,
    bool ChangeCountComplete,
    bool ThresholdExceeded)
{
    public bool NeedsAttention => Checked
        && (!ChangeCountComplete
            || Outcome is IncrementalUpdateOutcome.NeedsFullRebuild
                or IncrementalUpdateOutcome.NeedsCompatibilityRebuild);

    public string Describe()
    {
        if (!Checked)
            return string.Empty;
        if (!ChangeCountComplete)
        {
            string observed = JournalChangeCount > 0 ? $" after at least {JournalChangeCount:N0} journal changes" : string.Empty;
            return $"Post-build catch-up could not read a complete change-journal interval{observed}; the new index was kept safe and affected files will live-scan until maintenance catches up or the index is rebuilt.";
        }
        if (!ThresholdExceeded)
        {
            if (JournalChangeCount == 0)
                return "Post-build freshness check found no journal changes since the crawl began.";
            return $"Post-build freshness check found {JournalChangeCount:N0} journal change(s), at or below the {ThresholdChanges:N0}-change catch-up threshold; affected files remain safe through live scanning until normal maintenance.";
        }
        return Outcome switch
        {
            IncrementalUpdateOutcome.SegmentAppended =>
                $"Post-build incremental catch-up applied {JournalChangeCount:N0} journal change(s).",
            IncrementalUpdateOutcome.Compacted =>
                $"Post-build incremental catch-up applied {JournalChangeCount:N0} journal change(s) and compacted the staged index.",
            IncrementalUpdateOutcome.NoChanges =>
                $"Post-build catch-up examined {JournalChangeCount:N0} journal change(s), but none required an index delta.",
            _ =>
                "Post-build catch-up could not prove a safe incremental update; affected files will live-scan until maintenance catches up or the index is rebuilt.",
        };
    }
}

/// <summary>Success payload for one ordered maintenance pass.</summary>
internal readonly record struct IndexMaintenanceSuccess(
    int Built,
    int Skipped,
    int Failed,
    IReadOnlyList<IndexMaintenanceRootResult> Roots);

/// <summary>Structural validation result. Metadata presence alone never produces this result.</summary>
internal readonly record struct IndexValidationResult(
    bool Valid,
    string? FailureReason,
    int DocumentCount,
    int SegmentCount,
    string? RootPath);

/// <summary>Progress emitted while building an optional PDF namespace.</summary>
public readonly record struct PdfBuildProgress(int Processed, int Total);

/// <summary>Progress emitted while building an optional image OCR namespace.</summary>
public readonly record struct ImageOcrBuildProgress(int Processed, int Total);

/// <summary>Validates untrusted operation JSON before a worker accepts it.</summary>
internal static class IndexOperationValidator
{
    public static void Validate(IndexBuildOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Version != IndexBuildOperation.CurrentVersion)
            throw new InvalidDataException($"Unsupported build-operation version {operation.Version}.");
        ValidateStorage(operation.StorageDirectory);
        ValidateRoot(operation.Root);
        ArgumentNullException.ThrowIfNull(operation.Policy);
        operation.RetainedGenerations = Math.Max(1, operation.RetainedGenerations);
        operation.BuildMemoryBudgetMB = operation.BuildMemoryBudgetMB > 0 ? operation.BuildMemoryBudgetMB : IndexBuildDefaults.MemoryBudgetMB;
        operation.BuildParallelism = Math.Clamp(operation.BuildParallelism, 1, IndexWorkerParallelism.Maximum);
        operation.MaxDiskUsagePercent = Math.Clamp(operation.MaxDiskUsagePercent, 0, 99);
        operation.FileIoTimeoutSeconds = Math.Clamp(
            operation.FileIoTimeoutSeconds,
            IndexBuildDefaults.MinimumFileIoTimeoutSeconds,
            IndexBuildDefaults.MaximumFileIoTimeoutSeconds);
        ArgumentNullException.ThrowIfNull(operation.PostBuildCatchUpSettings);
        NormalizeMaintenanceSettings(operation.PostBuildCatchUpSettings);
        NormalizeOcrSettings(operation);
    }

    public static void Validate(IndexMaintenanceOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Version != IndexMaintenanceOperation.CurrentVersion)
            throw new InvalidDataException($"Unsupported maintenance-operation version {operation.Version}.");
        ValidateStorage(operation.StorageDirectory);
        if (operation.Mode is not (IndexMaintenanceOperation.ModeBuildDue or IndexMaintenanceOperation.ModeIncremental))
            throw new InvalidDataException($"Unsupported maintenance mode '{operation.Mode}'.");
        if (operation.Roots is null || operation.Roots.Length == 0 || operation.Roots.Length > IndexBuildDefaults.MaxOperationRoots)
            throw new InvalidDataException($"Maintenance operations require 1-{IndexBuildDefaults.MaxOperationRoots} roots.");
        foreach (IndexMaintenanceRootOperation root in operation.Roots)
        {
            ArgumentNullException.ThrowIfNull(root);
            ValidateRoot(root.Root);
            ArgumentNullException.ThrowIfNull(root.Policy);
            root.BuildParallelism = Math.Clamp(root.BuildParallelism, 1, IndexWorkerParallelism.Maximum);
        }
        ArgumentNullException.ThrowIfNull(operation.Settings);
        operation.RetainedGenerations = Math.Max(1, operation.RetainedGenerations);
        NormalizeMaintenanceSettings(operation.Settings);
    }

    public static void Validate(IndexValidationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Version != IndexValidationOperation.CurrentVersion)
            throw new InvalidDataException($"Unsupported validation-operation version {operation.Version}.");
        ValidateStorage(operation.StorageDirectory);
        ValidateRoot(operation.Root);
        operation.RetainedGenerations = Math.Max(1, operation.RetainedGenerations);
    }

    private static void ValidateStorage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The index storage directory must be an absolute path.");
    }

    private static void NormalizeOcrSettings(IndexBuildOperation operation)
    {
        operation.ImageOcrEngine = NormalizeOcrEngine(operation.ImageOcrEngine);
        operation.ImageOcrModel = string.IsNullOrWhiteSpace(operation.ImageOcrModel) ? "ChineseV5" : operation.ImageOcrModel.Trim();
        operation.ImageOcrMaxSide = operation.ImageOcrMaxSide <= 0 ? 0 : Math.Clamp(operation.ImageOcrMaxSide, 320, 4096);
        operation.ImageOcrWorkerParallelism = Math.Clamp(operation.ImageOcrWorkerParallelism, 1, 4);
        operation.ImageOcrExtensions = NormalizeOcrExtensions(operation.ImageOcrExtensions);
    }

    private static void NormalizeOcrSettings(IndexMaintenanceSettings settings)
    {
        settings.ImageOcrEngine = NormalizeOcrEngine(settings.ImageOcrEngine);
        settings.ImageOcrModel = string.IsNullOrWhiteSpace(settings.ImageOcrModel) ? "ChineseV5" : settings.ImageOcrModel.Trim();
        settings.ImageOcrMaxSide = settings.ImageOcrMaxSide <= 0 ? 0 : Math.Clamp(settings.ImageOcrMaxSide, 320, 4096);
        settings.ImageOcrWorkerParallelism = Math.Clamp(settings.ImageOcrWorkerParallelism, 1, 4);
        settings.ImageOcrExtensions = NormalizeOcrExtensions(settings.ImageOcrExtensions);
    }

    private static void NormalizeMaintenanceSettings(IndexMaintenanceSettings settings)
    {
        settings.BuildMemoryBudgetMB = settings.BuildMemoryBudgetMB > 0
            ? settings.BuildMemoryBudgetMB
            : IndexBuildDefaults.MemoryBudgetMB;
        settings.MaxDiskUsagePercent = Math.Clamp(settings.MaxDiskUsagePercent, 0, 99);
        settings.MaxDeltaSegments = Math.Clamp(settings.MaxDeltaSegments, 1, 64);
        settings.CompactionThresholdMB = Math.Clamp(settings.CompactionThresholdMB, 1, 8192);
        settings.MaxAutoCompactionSizeMB = Math.Max(0, settings.MaxAutoCompactionSizeMB);
        settings.MaxJournalCatchupRecords = Math.Clamp(settings.MaxJournalCatchupRecords, 1, 100_000_000);
        settings.PostBuildCatchUpThresholdChanges = settings.PostBuildCatchUpThresholdChanges < 0
            ? -1
            : Math.Min(settings.PostBuildCatchUpThresholdChanges, 100_000_000);
        settings.FileIoTimeoutSeconds = Math.Clamp(
            settings.FileIoTimeoutSeconds,
            IndexBuildDefaults.MinimumFileIoTimeoutSeconds,
            IndexBuildDefaults.MaximumFileIoTimeoutSeconds);
        NormalizeOcrSettings(settings);
    }

    private static string NormalizeOcrEngine(string? engine)
        => string.Equals(engine?.Trim(), "tesseract", StringComparison.OrdinalIgnoreCase) ? "tesseract" : "paddle";

    private static string[] NormalizeOcrExtensions(IEnumerable<string>? extensions)
        => (extensions ?? Array.Empty<string>())
            .Select(static extension => extension.Trim().TrimStart('.').ToLowerInvariant())
            .Where(static extension => extension.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("Each index root must be an absolute path.");
    }
}

/// <summary>Path provider for a normalized effective storage root. Unlike the app-side default provider,
/// it performs no settings lookup and is safe to construct inside the worker.</summary>
internal sealed class FixedContentIndexPathProvider : IContentIndexPathProvider
{
    public FixedContentIndexPathProvider(string indexRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        IndexRoot = Path.GetFullPath(indexRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string IndexRoot { get; }

    public string GetScopeDirectory(string scopeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        return Path.Combine(IndexRoot, scopeId);
    }
}

/// <summary>Resolves pdftotext from the app directory even when called from the nested index-worker directory.</summary>
internal static class IndexWorkerToolPaths
{
    public static string? ResolvePdfTextToolPath()
        => ResolvePdfTextToolPath(
            Environment.GetEnvironmentVariable(PdfTextExtractor.ToolPathEnvVar),
            AppContext.BaseDirectory,
            File.Exists);

    internal static string? ResolvePdfTextToolPath(
        string? configured,
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(configured))
            return fileExists(configured) ? configured : null;

        baseDirectory = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string appDirectory = string.Equals(Path.GetFileName(baseDirectory), "index-worker", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(baseDirectory, ".."))
            : baseDirectory;
        string candidate = Path.Combine(appDirectory, "pdftotext", "pdftotext.exe");
        return fileExists(candidate) ? candidate : null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IndexBuildOperation))]
[JsonSerializable(typeof(IndexMaintenanceOperation))]
[JsonSerializable(typeof(IndexValidationOperation))]
[JsonSerializable(typeof(IndexMaintenanceResultEnvelope))]
internal sealed partial class IndexOperationJsonContext : JsonSerializerContext
{
}
