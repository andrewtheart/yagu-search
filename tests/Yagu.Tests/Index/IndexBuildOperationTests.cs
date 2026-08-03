using System.Text.Json;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexBuildOperationTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-operation", Guid.NewGuid().ToString("N"));

    public IndexBuildOperationTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("YAGU_PDFTOTEXT", null);
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void PolicySnapshot_RoundTripsEveryBehaviorBearingField_AndIsIndependent()
    {
        var policy = new IndexIngestionPolicy(
            1234,
            new[] { "**/bin/**", "*.tmp" },
            new HashSet<string>(new[] { "dll", "exe" }, StringComparer.OrdinalIgnoreCase),
            includeHiddenFiles: true,
            followReparsePoints: true,
            maxDepth: 7,
            reAdmitGlobs: new[] { "**/bin/keep/**" });

        IndexIngestionPolicySnapshot snapshot = IndexIngestionPolicySnapshot.FromPolicy(policy);
        IndexIngestionPolicy restored = snapshot.ToPolicy();

        Assert.Equal(policy.MaxFileSizeBytes, restored.MaxFileSizeBytes);
        Assert.Equal(policy.ExcludedGlobs, restored.ExcludedGlobs);
        Assert.True(policy.ExcludedExtensions.SetEquals(restored.ExcludedExtensions));
        Assert.Equal(policy.IncludeHiddenFiles, restored.IncludeHiddenFiles);
        Assert.Equal(policy.FollowReparsePoints, restored.FollowReparsePoints);
        Assert.Equal(policy.MaxDepth, restored.MaxDepth);
        Assert.Equal(policy.ReAdmitGlobs, restored.ReAdmitGlobs);

        snapshot.ExcludedGlobs[0] = "changed";
        Assert.Equal("**/bin/**", policy.ExcludedGlobs[0]);

        snapshot.ExcludedGlobs = null!;
        snapshot.ExcludedExtensions = null!;
        snapshot.ReAdmitGlobs = null!;
        IndexIngestionPolicy emptyCollections = snapshot.ToPolicy();
        Assert.Empty(emptyCollections.ExcludedGlobs);
        Assert.Empty(emptyCollections.ExcludedExtensions);
        Assert.Empty(emptyCollections.ReAdmitGlobs);
    }

    [Fact]
    public void Factory_CapturesNormalizedSettingsAtDispatch()
    {
        string root = Path.Combine(_sandbox, "root");
        string storage = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(root);
        var settings = new AppSettings
        {
            IndexStorageDirectory = storage,
            IndexRetainedGenerationCount = 3,
            IndexBuildMemoryBudgetMB = 128,
            IndexBuildWorkerParallelism = 7,
            IndexMaxDiskUsagePercent = 88,
            IndexBuildPdfTextExtendedSource = true,
            IndexBuildImageTextExtendedSource = true,
            IndexMaxFileSizeMB = 12,
            IndexExcludedGlobs = "**/obj/**",
        };

        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(settings, root, rebuild: true);
        settings.IndexBuildMemoryBudgetMB = 512;
        settings.IndexExcludedGlobs = "changed";

        Assert.Equal(Path.GetFullPath(storage).TrimEnd('\\'), operation.StorageDirectory.TrimEnd('\\'));
        Assert.Equal(3, operation.RetainedGenerations);
        Assert.Equal(128, operation.BuildMemoryBudgetMB);
        int expectedParallelism = IndexWorkerParallelism.ResolveBuildDegree(
            7, Environment.ProcessorCount, IndexWorkerParallelism.DetectedPhysicalCoreCount, 128,
            settings.LimitParallelismOnHdd, DiskTypeDetector.IsHardDisk(root));
        Assert.Equal(expectedParallelism, operation.BuildParallelism);
        Assert.Equal(88, operation.MaxDiskUsagePercent);
        Assert.True(operation.Rebuild);
        Assert.True(operation.BuildPdfText);
        Assert.True(operation.BuildImageText);
        Assert.Equal(settings.ImageOcrEngine, operation.ImageOcrEngine);
        Assert.Contains("png", operation.ImageOcrExtensions);
        Assert.True(operation.ProduceV3QueryStructures);
        Assert.True(operation.Policy.IndexBinaryAsciiContent);
        Assert.Contains("**/obj/**", operation.Policy.ExcludedGlobs);

        IndexValidationOperation validation = IndexBuildOperationFactory.CreateValidation(settings, root);
        Assert.Equal(operation.StorageDirectory, validation.StorageDirectory);
        Assert.Equal(operation.Root, validation.Root);

        settings.IndexMaxDeltaSegments = 17;
        settings.IndexCompactionThresholdMB = 333;
        settings.IndexMaxAutoCompactionSizeMB = 444;
        settings.IndexMaxJournalCatchupRecords = 12_345;
        settings.IndexAutoRepair = false;
        IndexMaintenanceOperation maintenance = IndexBuildOperationFactory.CreateMaintenance(
            settings,
            new[] { root, root },
            IndexMaintenanceOperation.ModeIncremental,
            rebuildWhenDirty: false);
        Assert.Single(maintenance.Roots); // normalized + deduplicated before dispatch
        Assert.Equal(17, maintenance.Settings.MaxDeltaSegments);
        Assert.Equal(333, maintenance.Settings.CompactionThresholdMB);
        Assert.Equal(444, maintenance.Settings.MaxAutoCompactionSizeMB);
        Assert.Equal(12_345, maintenance.Settings.MaxJournalCatchupRecords);
        Assert.False(maintenance.AllowFullRebuildFallback);
        Assert.True(maintenance.AllowCompatibilityRebuild);
        Assert.False(maintenance.ForceRefresh);
        Assert.True(maintenance.Settings.ProduceV3QueryStructures);
        Assert.True(maintenance.Settings.BuildImageText);
        Assert.True(maintenance.Roots[0].Policy.IndexBinaryAsciiContent);
        Assert.False(maintenance.Settings.AutoRepair);
        int expectedMaintenanceParallelism = IndexWorkerParallelism.ResolveBuildDegree(
            7, Environment.ProcessorCount, IndexWorkerParallelism.DetectedPhysicalCoreCount, 512,
            settings.LimitParallelismOnHdd, DiskTypeDetector.IsHardDisk(root));
        Assert.Equal(expectedMaintenanceParallelism, maintenance.Roots[0].BuildParallelism);
    }

    [Fact]
    public void Validator_NormalizesDefaultsAndRejectsInvalidInputs()
    {
        var valid = new IndexBuildOperation
        {
            StorageDirectory = Path.Combine(_sandbox, "index"),
            Root = Path.Combine(_sandbox, "root"),
            RetainedGenerations = 0,
            BuildMemoryBudgetMB = 0,
            BuildParallelism = int.MaxValue,
            MaxDiskUsagePercent = 100,
            ImageOcrEngine = " TESSERACT ",
            ImageOcrModel = " ",
            ImageOcrMaxSide = 0,
            ImageOcrExtensions = null!,
        };
        IndexOperationValidator.Validate(valid);
        Assert.Equal(1, valid.RetainedGenerations);
        Assert.Equal(IndexBuildDefaults.MemoryBudgetMB, valid.BuildMemoryBudgetMB);
        Assert.Equal(IndexWorkerParallelism.Maximum, valid.BuildParallelism);
        Assert.Equal(99, valid.MaxDiskUsagePercent);
        Assert.Equal("tesseract", valid.ImageOcrEngine);
        Assert.Equal("ChineseV5", valid.ImageOcrModel);
        Assert.Equal(0, valid.ImageOcrMaxSide);
        Assert.Empty(valid.ImageOcrExtensions);

        valid.Version++;
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(valid));
        valid.Version = IndexBuildOperation.CurrentVersion;
        valid.StorageDirectory = "relative";
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(valid));
        valid.StorageDirectory = Path.Combine(_sandbox, "index");
        valid.Root = "relative";
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(valid));
        valid.Root = Path.Combine(_sandbox, "root");
        valid.StorageDirectory = " ";
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(valid));
    }

    [Fact]
    public void MaintenanceValidation_EnforcesModeAndRootCap()
    {
        IndexMaintenanceOperation operation = ValidMaintenance();
        operation.Roots[0].BuildParallelism = int.MaxValue;
        operation.Settings.ImageOcrEngine = null!;
        operation.Settings.ImageOcrModel = " ";
        operation.Settings.ImageOcrMaxSide = 0;
        IndexOperationValidator.Validate(operation);
        Assert.Equal(IndexMaintenanceOperation.ModeIncremental, operation.Mode);
        Assert.Equal(IndexWorkerParallelism.Maximum, operation.Roots[0].BuildParallelism);
        Assert.Equal("paddle", operation.Settings.ImageOcrEngine);
        Assert.Equal("ChineseV5", operation.Settings.ImageOcrModel);
        Assert.Equal(0, operation.Settings.ImageOcrMaxSide);

        operation.Mode = "unknown";
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));

        operation = ValidMaintenance();
        operation.Version++;
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
        operation = ValidMaintenance();
        operation.Settings = null!;
        Assert.Throws<ArgumentNullException>(() => IndexOperationValidator.Validate(operation));
        operation = ValidMaintenance();
        operation.Roots[0].Policy = null!;
        Assert.Throws<ArgumentNullException>(() => IndexOperationValidator.Validate(operation));
        operation = ValidMaintenance();
        operation.Roots = Array.Empty<IndexMaintenanceRootOperation>();
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
        operation = ValidMaintenance();
        operation.Roots = null!;
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
        operation = ValidMaintenance();
        operation.Roots = Enumerable.Range(0, IndexBuildDefaults.MaxOperationRoots + 1)
            .Select(i => new IndexMaintenanceRootOperation
            {
                Root = Path.Combine(_sandbox, "r" + i),
                Policy = new IndexIngestionPolicySnapshot(),
            }).ToArray();
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
    }

    [Fact]
    public void ValidationValidator_NormalizesAndRejectsVersionAndRelativePaths()
    {
        var operation = new IndexValidationOperation
        {
            StorageDirectory = Path.Combine(_sandbox, "index"),
            Root = Path.Combine(_sandbox, "root"),
            RetainedGenerations = 0,
        };
        IndexOperationValidator.Validate(operation);
        Assert.Equal(1, operation.RetainedGenerations);

        operation.Version++;
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
        operation.Version = IndexValidationOperation.CurrentVersion;
        operation.Root = "relative";
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
        operation.Root = " ";
        Assert.Throws<InvalidDataException>(() => IndexOperationValidator.Validate(operation));
    }

    [Fact]
    public void PdfToolResolver_HonorsExistingMissingAndDefaultPaths()
    {
        string tool = Path.Combine(_sandbox, "pdftotext.exe");
        File.WriteAllText(tool, "fake");
        Environment.SetEnvironmentVariable("YAGU_PDFTOTEXT", tool);
        Assert.Equal(tool, IndexWorkerToolPaths.ResolvePdfTextToolPath());
        Environment.SetEnvironmentVariable("YAGU_PDFTOTEXT", Path.Combine(_sandbox, "missing.exe"));
        Assert.Null(IndexWorkerToolPaths.ResolvePdfTextToolPath());
        Environment.SetEnvironmentVariable("YAGU_PDFTOTEXT", null);
        _ = IndexWorkerToolPaths.ResolvePdfTextToolPath(); // bundled-default probe (usually absent in tests)

        string workerDirectory = Path.Combine(_sandbox, "app", "index-worker");
        string expectedParentTool = Path.Combine(_sandbox, "app", "pdftotext", "pdftotext.exe");
        Assert.Equal(expectedParentTool, IndexWorkerToolPaths.ResolvePdfTextToolPath(
            null, workerDirectory, path => string.Equals(path, expectedParentTool, StringComparison.OrdinalIgnoreCase)));
        Assert.Null(IndexWorkerToolPaths.ResolvePdfTextToolPath(null, _sandbox, _ => false));
        Assert.Throws<ArgumentException>(() => IndexWorkerToolPaths.ResolvePdfTextToolPath(null, " ", _ => false));
        Assert.Throws<ArgumentNullException>(() => IndexWorkerToolPaths.ResolvePdfTextToolPath(null, _sandbox, null!));
    }

    [Fact]
    public void WorkerSafeDefaultsAndFixedProvider_CoverBothArchitecturesAndGuards()
    {
        Assert.Equal(384, IndexBuildDefaults.MemoryBudgetMBFor(true));
        Assert.Equal(192, IndexBuildDefaults.MemoryBudgetMBFor(false));
        Assert.Contains(IndexBuildDefaults.MemoryBudgetMB, new[] { 192, 384 });

        Assert.Throws<ArgumentException>(() => new FixedContentIndexPathProvider(" "));
        var provider = new FixedContentIndexPathProvider(_sandbox);
        Assert.Equal(Path.Combine(provider.IndexRoot, "scope"), provider.GetScopeDirectory("scope"));
        Assert.Throws<ArgumentException>(() => provider.GetScopeDirectory(""));
    }

    [Fact]
    public void OperationJson_RoundTripsBuildMaintenanceAndValidation()
    {
        var build = new IndexBuildOperation
        {
            StorageDirectory = Path.Combine(_sandbox, "index"),
            Root = Path.Combine(_sandbox, "root"),
            Policy = new IndexIngestionPolicySnapshot { ExcludedGlobs = new[] { "*.tmp" } },
            BuildParallelism = 3,
        };
        string buildJson = JsonSerializer.Serialize(build, IndexOperationJsonContext.Default.IndexBuildOperation);
        Assert.Equal("*.tmp", JsonSerializer.Deserialize(buildJson, IndexOperationJsonContext.Default.IndexBuildOperation)!.Policy.ExcludedGlobs[0]);
        Assert.Equal(3, JsonSerializer.Deserialize(buildJson, IndexOperationJsonContext.Default.IndexBuildOperation)!.BuildParallelism);

        IndexMaintenanceOperation maintenance = ValidMaintenance();
        string maintenanceJson = JsonSerializer.Serialize(maintenance, IndexOperationJsonContext.Default.IndexMaintenanceOperation);
        Assert.Single(JsonSerializer.Deserialize(maintenanceJson, IndexOperationJsonContext.Default.IndexMaintenanceOperation)!.Roots);

        var validation = new IndexValidationOperation
        {
            StorageDirectory = Path.Combine(_sandbox, "index"),
            Root = Path.Combine(_sandbox, "root"),
        };
        string validationJson = JsonSerializer.Serialize(validation, IndexOperationJsonContext.Default.IndexValidationOperation);
        Assert.Equal(validation.Root, JsonSerializer.Deserialize(validationJson, IndexOperationJsonContext.Default.IndexValidationOperation)!.Root);
    }

    private IndexMaintenanceOperation ValidMaintenance() => new()
    {
        StorageDirectory = Path.Combine(_sandbox, "index"),
        Mode = IndexMaintenanceOperation.ModeIncremental,
        Settings = new IndexMaintenanceSettings(),
        Roots = new[]
        {
            new IndexMaintenanceRootOperation
            {
                Root = Path.Combine(_sandbox, "root"),
                Policy = new IndexIngestionPolicySnapshot(),
            },
        },
    };
}
