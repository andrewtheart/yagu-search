using Yagu.Helpers;

namespace Yagu.Services.Index;

/// <summary>App-side snapshot factory. This is the only migration component that reads mutable
/// <see cref="AppSettings"/>; the resulting operations contain only normalized worker-safe values.</summary>
internal static class IndexBuildOperationFactory
{
    public static IndexBuildOperation CreateBuild(AppSettings settings, string root, bool rebuild)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
        var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
        int buildParallelism = ResolveBuildParallelism(settings, normalizedRoot);
        var operation = new IndexBuildOperation
        {
            StorageDirectory = provider.IndexRoot,
            RetainedGenerations = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount),
            Root = normalizedRoot,
            Policy = IndexIngestionPolicySnapshot.FromPolicy(IndexedRootFilterPolicy.ResolvePolicy(settings, normalizedRoot)),
            BuildMemoryBudgetMB = settings.EffectiveIndexBuildMemoryBudgetMB,
            BuildParallelism = buildParallelism,
            MaxDiskUsagePercent = settings.EffectiveIndexMaxDiskUsagePercent,
            FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(settings.FileIoTimeoutSeconds),
            Rebuild = rebuild,
            BuildPdfText = settings.IndexBuildPdfTextExtendedSource,
            BuildImageText = settings.IndexBuildImageTextExtendedSource,
            ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(settings.ImageOcrEngine),
            ImageOcrModel = AppSettings.NormalizeImageOcrModel(settings.ImageOcrModel),
            ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(settings.ImageOcrMaxSide),
            ImageOcrWorkerParallelism = ResolveOcrBuildParallelism(settings, normalizedRoot),
            ImageOcrExtensions = SplitExtensions(AppSettings.DefaultImageOcrExtensions),
            ProduceV3QueryStructures = settings.IndexProduceV3QueryStructures,
            PostBuildCatchUpSettings = CreateMaintenanceSettings(settings),
        };
        IndexOperationValidator.Validate(operation);
        return operation;
    }

    public static IndexValidationOperation CreateValidation(AppSettings settings, string root)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var operation = new IndexValidationOperation
        {
            StorageDirectory = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory).IndexRoot,
            RetainedGenerations = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount),
            Root = IndexScopeIdentity.NormalizePath(root),
        };
        IndexOperationValidator.Validate(operation);
        return operation;
    }

    public static IndexMaintenanceOperation CreateMaintenance(
        AppSettings settings,
        IReadOnlyList<string> roots,
        string mode,
        bool rebuildWhenDirty)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roots);
        string[] normalizedRoots = IndexedRootsPolicy.Normalize(roots).Take(IndexBuildDefaults.MaxOperationRoots).ToArray();
        var operation = new IndexMaintenanceOperation
        {
            StorageDirectory = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory).IndexRoot,
            RetainedGenerations = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount),
            Mode = mode,
            RebuildWhenDirty = rebuildWhenDirty,
            // Automatic incremental maintenance must never surprise the user with an expensive full
            // rebuild when a journal gap/cap makes incremental refresh unsafe. The UI can offer a
            // separate explicit rebuild action.
            AllowFullRebuildFallback = !string.Equals(
                mode, IndexMaintenanceOperation.ModeIncremental, StringComparison.Ordinal),
            AllowCompatibilityRebuild = true,
            Settings = CreateMaintenanceSettings(settings),
            Roots = normalizedRoots.Select(root => new IndexMaintenanceRootOperation
            {
                Root = root,
                Policy = IndexIngestionPolicySnapshot.FromPolicy(IndexedRootFilterPolicy.ResolvePolicy(settings, root)),
                BuildParallelism = ResolveBuildParallelism(settings, root),
            }).ToArray(),
        };
        IndexOperationValidator.Validate(operation);
        return operation;
    }

    public static IndexMaintenanceSettings CreateMaintenanceSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new IndexMaintenanceSettings
        {
            BuildMemoryBudgetMB = settings.EffectiveIndexBuildMemoryBudgetMB,
            MaxDiskUsagePercent = settings.EffectiveIndexMaxDiskUsagePercent,
            BuildPdfText = settings.IndexBuildPdfTextExtendedSource,
            BuildImageText = settings.IndexBuildImageTextExtendedSource,
            ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(settings.ImageOcrEngine),
            ImageOcrModel = AppSettings.NormalizeImageOcrModel(settings.ImageOcrModel),
            ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(settings.ImageOcrMaxSide),
            ImageOcrWorkerParallelism = ResolveOcrBuildParallelism(settings, null),
            ImageOcrExtensions = SplitExtensions(AppSettings.DefaultImageOcrExtensions),
            ProduceV3QueryStructures = settings.IndexProduceV3QueryStructures,
            AutoRepair = settings.IndexAutoRepair,
            MaxDeltaSegments = AppSettings.NormalizeIndexMaxDeltaSegments(settings.IndexMaxDeltaSegments),
            CompactionThresholdMB = AppSettings.NormalizeIndexCompactionThresholdMB(settings.IndexCompactionThresholdMB),
            MaxAutoCompactionSizeMB = AppSettings.NormalizeIndexMaxAutoCompactionSizeMB(settings.IndexMaxAutoCompactionSizeMB),
            MaxJournalCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(settings.IndexMaxJournalCatchupRecords),
            PostBuildCatchUpThresholdChanges = AppSettings.NormalizeIndexPostBuildCatchUpThresholdChanges(
                settings.IndexPostBuildCatchUpThresholdChanges),
            FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(settings.FileIoTimeoutSeconds),
        };
    }

    private static int ResolveBuildParallelism(AppSettings settings, string root)
        => IndexWorkerParallelism.ResolveBuildDegree(
            settings.IndexBuildWorkerParallelism,
            Environment.ProcessorCount,
            IndexWorkerParallelism.DetectedPhysicalCoreCount,
            settings.EffectiveIndexBuildMemoryBudgetMB,
            settings.LimitParallelismOnHdd,
            DiskTypeDetector.IsHardDisk(root));

    private static int ResolveOcrBuildParallelism(AppSettings settings, string? root)
        => Ocr.OcrWorkerParallelism.Resolve(
            settings.ImageOcrWorkerParallelism,
            AppSettings.NormalizeImageOcrEngine(settings.ImageOcrEngine),
            Environment.ProcessorCount,
            settings.LimitParallelismOnHdd,
            !string.IsNullOrWhiteSpace(root) && DiskTypeDetector.IsHardDisk(root));

    private static string[] SplitExtensions(string extensions)
        => extensions.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static extension => extension.TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
