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

    private readonly string? _modelName;

    public PaddleOcrEngine(string? modelName = null)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();
    }

    /// <summary>
    /// Test/diagnostics hook: forces the worker path to a specific value (authoritative — if the
    /// file does not exist, the engine reports the worker as unavailable instead of probing the
    /// standard locations).
    /// </summary>
    internal PaddleOcrEngine(string? modelName, string? workerPathOverride)
        : base(workerPathOverride)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();
    }

    public override string Id => OcrEngineFactory.PaddleId;

    public override string DisplayName => "PaddleSharp";

    protected override string LogSource => "OcrPaddle";

    protected override void ConfigureWorkerEnvironment(IDictionary<string, string?> environment)
    {
        environment[EngineEnvVar] = OcrEngineFactory.PaddleId;
        if (_modelName is not null)
        {
            environment[ModelEnvVar] = _modelName;
        }
    }
}
