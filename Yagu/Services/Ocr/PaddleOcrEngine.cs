using System.Globalization;
namespace Yagu.Services.Ocr;

/// <summary>
/// OCR engine backed by PaddleSharp, hosted in <c>Yagu.OcrWorker.exe</c> (the default engine).
/// <para>
/// All process/protocol plumbing lives in <see cref="WorkerOcrEngine"/>; this subclass only selects
/// the PaddleSharp backend and an optional model. PaddleSharp's native stack is not Native-AOT
/// compatible, so it runs out-of-process; this type itself is pure managed.
/// </para>
/// </summary>
public sealed class PaddleOcrEngine : WorkerOcrEngine
{
    /// <summary>Environment variable that selects the PaddleOCR model (e.g. <c>EnglishV4</c>).</summary>
    public const string ModelEnvVar = "YAGU_OCR_MODEL";

    /// <summary>Environment variable that caps the PaddleOCR detection resolution (longest image side,
    /// in pixels). <c>0</c> means unlimited; the variable is omitted to use the worker default.</summary>
    public const string MaxSideEnvVar = "YAGU_OCR_MAX_SIDE";

    /// <summary>Environment variable pointing the worker at the native runtime directory (bundled payload
    /// when present, else the per-user download cache). Read by the worker's <c>NativeRuntime</c>.</summary>
    public const string RuntimeDirEnvVar = "YAGU_OCR_RUNTIME_DIR";

    /// <summary>Environment variable pointing the worker at the PP-OCR model directory (bundled payload
    /// when present, else the per-user download cache).</summary>
    public const string ModelDirEnvVar = "YAGU_OCR_MODEL_DIR";

    private readonly string? _modelName;
    private readonly int _maxSide;

    public PaddleOcrEngine(string? modelName = null, int maxSide = -1)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();
        _maxSide = maxSide;
    }

    /// <summary>
    /// Test/diagnostics hook: forces the worker path to a specific value (authoritative — if the
    /// file does not exist, the engine reports the worker as unavailable instead of probing the
    /// standard locations).
    /// </summary>
    internal PaddleOcrEngine(string? modelName, string? workerPathOverride, int maxSide = -1)
        : base(workerPathOverride)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();
        _maxSide = maxSide;
    }

    public override string Id => OcrEngineFactory.PaddleId;

    public override string DisplayName => "PaddleSharp";

    protected override string LogSource => "OcrPaddle";

    protected override void ConfigureWorkerEnvironment(IDictionary<string, string?> environment)
    {
        environment[EngineEnvVar] = OcrEngineFactory.PaddleId;
        // Point the worker at the bundled payload when present (download-free), else the writable cache.
        environment[RuntimeDirEnvVar] = OcrAssetPaths.PaddleRuntimeDir();
        environment[ModelDirEnvVar] = OcrAssetPaths.PaddleModelDir();
        if (_modelName is not null)
        {
            environment[ModelEnvVar] = _modelName;
        }
        if (_maxSide >= 0)
        {
            environment[MaxSideEnvVar] = _maxSide.ToString(CultureInfo.InvariantCulture);
        }
    }

    protected override OcrAssetRequirement DescribeAssetRequirement()
    {
        bool nativePresent = OcrAssetPaths.PaddleNativePresent(OcrAssetPaths.PaddleRuntimeDir());
        bool modelsPresent = OcrAssetPaths.PaddleModelsPresent(OcrAssetPaths.PaddleModelDir());
        return OcrAssetPaths.BuildPaddleRequirement(DisplayName, nativePresent, modelsPresent);
    }
}
