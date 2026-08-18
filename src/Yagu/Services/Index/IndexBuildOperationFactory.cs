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
            MinimumFreeSpaceMB = settings.IndexMinimumFreeSpaceMB,
            HaltUpdatesWhenReclamationBlocked = settings.IndexHaltUpdatesWhenReclamationBlocked,
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
            SizeManagementMode = IndexSizeManagementModes.Normalize(settings.IndexSizeManagementMode),
            SizeBudgetMB = AppSettings.NormalizeIndexMaxDiskSizeMB(settings.IndexMaxDiskSizeMB),
            CoalesceMaxSegmentMB = AppSettings.NormalizeIndexCoalesceMaxSegmentMB(settings.IndexCoalesceMaxSegmentMB),
            CoalesceMaxBatchMB = AppSettings.NormalizeIndexCoalesceMaxBatchMB(settings.IndexCoalesceMaxBatchMB),
            CoalesceMinRun = AppSettings.NormalizeIndexCoalesceMinRun(settings.IndexCoalesceMinRun),
            CoalesceMaxRunsPerPass = AppSettings.NormalizeIndexCoalesceMaxRunsPerPass(settings.IndexCoalesceMaxRunsPerPass),
            RootSizePolicies = IndexSizeManagementPolicy.Normalize(settings.IndexedRootSizePolicies),
            MaxJournalCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(settings.IndexMaxJournalCatchupRecords),
            RescanOnJournalGap = settings.IndexRescanOnJournalGap,
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

/// <summary>Host-side companion to the best-effort failure marker stored on an index volume.</summary>
internal static class IndexAutomaticCompactionRetryState
{
    public static bool IsActive(AppSettings settings, string root, DateTimeOffset nowUtc)
    {
        string normalized = IndexScopeIdentity.NormalizePath(root);
        if (!IndexSizeManagementPolicy.Resolve(settings, normalized).AllowsCompaction)
            return false;
        return settings.IndexAutomaticCompactionRetryAfterUtcByRoot.TryGetValue(
                normalized,
                out DateTimeOffset retryAfterUtc)
            && retryAfterUtc > nowUtc.ToUniversalTime();
    }

    public static void ApplyResults(
        AppSettings settings,
        IReadOnlyList<IndexMaintenanceRootResult> roots,
        DateTimeOffset nowUtc)
    {
        DateTimeOffset retryAfterUtc = nowUtc.ToUniversalTime().Add(
            ContentIndexManager.AutomaticCompactionRetryDelay);
        foreach (IndexMaintenanceRootResult result in roots)
        {
            string root = IndexScopeIdentity.NormalizePath(result.Root);
            bool compactCapable = IndexSizeManagementPolicy.Resolve(settings, root).AllowsCompaction;
            if (compactCapable
                && (result.Outcome == "compactionFailed"
                    || result.Action == IndexMaintenanceActions.SizeBudgetReached))
            {
                settings.IndexAutomaticCompactionRetryAfterUtcByRoot[root] = retryAfterUtc;
            }
            else if (result.Action is IndexMaintenanceActions.Compacted or IndexMaintenanceActions.Built)
            {
                settings.IndexAutomaticCompactionRetryAfterUtcByRoot.Remove(root);
            }
        }
    }

    public static async Task RecordAsync(
        SettingsService service,
        AppSettings liveSettings,
        IReadOnlyList<IndexMaintenanceRootResult> roots,
        DateTimeOffset nowUtc)
    {
        if (roots.Count == 0)
            return;
        void Apply(AppSettings settings) => ApplyResults(settings, roots, nowUtc);
        if (!await service.UpdateAsync(Apply, afterCommit: () => Apply(liveSettings)).ConfigureAwait(false))
            Apply(liveSettings);
    }

    public static async Task ClearAsync(
        SettingsService service,
        AppSettings liveSettings,
        string root)
    {
        string normalized = IndexScopeIdentity.NormalizePath(root);
        void Clear(AppSettings settings) =>
            settings.IndexAutomaticCompactionRetryAfterUtcByRoot.Remove(normalized);
        if (!await service.UpdateAsync(Clear, afterCommit: () => Clear(liveSettings)).ConfigureAwait(false))
            Clear(liveSettings);
    }
}
