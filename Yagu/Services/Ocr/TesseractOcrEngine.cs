namespace Yagu.Services.Ocr;

/// <summary>
/// OCR engine backed by Tesseract (the vendored charlesw wrapper), hosted in
/// <c>Yagu.OcrWorker.exe</c>. Mirrors the Memory app's default quality settings (pure-LSTM engine,
/// <c>tessdata_best</c> English model, three-pass extraction); see the worker's
/// <c>TesseractWorkerEngine</c> for the pipeline.
/// <para>
/// All process/protocol plumbing lives in <see cref="WorkerOcrEngine"/>; this subclass only selects
/// the Tesseract backend. Tesseract's native stack runs out-of-process; this type itself is pure
/// managed.
/// </para>
/// </summary>
public sealed class TesseractOcrEngine : WorkerOcrEngine
{
    /// <summary>Environment variable pointing the worker at the tessdata directory (bundled payload
    /// when present, else the per-user download cache that holds <c>eng.traineddata</c>).</summary>
    public const string TessdataDirEnvVar = "YAGU_OCR_TESSDATA_DIR";

    public TesseractOcrEngine()
    {
    }

    /// <summary>
    /// Test/diagnostics hook: forces the worker path to a specific value (authoritative — if the
    /// file does not exist, the engine reports the worker as unavailable instead of probing the
    /// standard locations).
    /// </summary>
    internal TesseractOcrEngine(string? workerPathOverride)
        : base(workerPathOverride)
    {
    }

    public override string Id => OcrEngineFactory.TesseractId;

    public override string DisplayName => "Tesseract";

    protected override string LogSource => "OcrTesseract";

    protected override void ConfigureWorkerEnvironment(IDictionary<string, string?> environment)
    {
        environment[EngineEnvVar] = OcrEngineFactory.TesseractId;
        // Point the worker at the bundled tessdata when present (download-free), else the cache.
        environment[TessdataDirEnvVar] = OcrAssetPaths.TesseractDataDir();
    }

    protected override OcrAssetRequirement DescribeAssetRequirement()
    {
        // Tesseract's native binaries are vendored beside the worker; only eng.traineddata downloads.
        bool dataPresent = OcrAssetPaths.TesseractDataPresent(OcrAssetPaths.TesseractDataDir());
        return OcrAssetPaths.BuildTesseractRequirement(DisplayName, dataPresent);
    }
}
