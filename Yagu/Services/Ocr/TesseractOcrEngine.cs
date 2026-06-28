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
    }
}
