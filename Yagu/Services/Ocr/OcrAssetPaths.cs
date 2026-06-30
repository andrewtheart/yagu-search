namespace Yagu.Services.Ocr;

/// <summary>
/// Resolves where the out-of-process <c>Yagu.OcrWorker.exe</c> looks for its native runtime and
/// models, and reports which assets are already present versus still need a one-time external
/// download.
/// <para>
/// Two sources are probed, in order:
/// <list type="number">
/// <item><b>Bundled</b> — a payload pre-staged beside the app by the OCR-bundled installer
/// (<c>&lt;app&gt;\ocr-payload\…</c>). When present, the worker is pointed here and no download ever
/// happens (the PaddleSharp/native loaders skip work when the files already exist).</item>
/// <item><b>Cache</b> — the per-user download cache under
/// <c>%LOCALAPPDATA%\Yagu\ocr-runtime\…</c> that the worker writes to on first use. This is the
/// fallback for the lite installer, and the only writable location.</item>
/// </list>
/// The same resolved directories are both (a) passed to the worker via environment variables and
/// (b) used by <see cref="OcrDownloadGate"/> to decide whether to warn before downloading, so the
/// two never disagree.
/// </para>
/// </summary>
public static class OcrAssetPaths
{
    // Approximate installed sizes, used only to render the "~X MB" figure in the consent warning.
    // The native PaddleInference + OpenCV runtime dominates; the PP-OCR models and the Tesseract
    // language data are comparatively tiny.
    public const long PaddleNativeApproxBytes = 349L * 1024 * 1024;
    public const long PaddleModelApproxBytes = 17L * 1024 * 1024;
    public const long TesseractDataApproxBytes = 15L * 1024 * 1024;

    // Native runtime probe files (must both exist for the Paddle worker to load).
    private const string PaddleNativeProbeA = "paddle_inference_c.dll";
    private const string PaddleNativeProbeB = "OpenCvSharpExtern.dll";

    private const string TesseractDataFile = "eng.traineddata";
    private const string ModelParamsFile = "inference.pdiparams";

    /// <summary>Root of the optional bundled OCR payload pre-staged by the OCR-bundled installer.</summary>
    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "ocr-payload");

    private static string BundledPaddleNativeDir => Path.Combine(BundledRoot, "paddle", "native");
    private static string BundledPaddleModelDir => Path.Combine(BundledRoot, "paddle", "models");
    private static string BundledTesseractDir => Path.Combine(BundledRoot, "tesseract", "tessdata");

    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yagu", "ocr-runtime");

    private static string CachePaddleNativeDir => Path.Combine(CacheRoot, "paddle", "native");
    private static string CachePaddleModelDir => Path.Combine(CacheRoot, "paddle", "models");
    private static string CacheTesseractDir => Path.Combine(CacheRoot, "tesseract", "tessdata");

    /// <summary>
    /// Directory the Paddle worker should use for native DLLs: the bundled payload when it contains
    /// the runtime, otherwise the writable per-user cache (the worker's own default location).
    /// </summary>
    public static string PaddleRuntimeDir()
        => PaddleNativePresent(BundledPaddleNativeDir) ? BundledPaddleNativeDir : CachePaddleNativeDir;

    /// <summary>Directory the Paddle worker should use for PP-OCR models (bundled when present, else cache).</summary>
    public static string PaddleModelDir()
        => PaddleModelsPresent(BundledPaddleModelDir) ? BundledPaddleModelDir : CachePaddleModelDir;

    /// <summary>Directory the Tesseract worker should use for language data (bundled when present, else cache).</summary>
    public static string TesseractDataDir()
        => TesseractDataPresent(BundledTesseractDir) ? BundledTesseractDir : CacheTesseractDir;

    /// <summary>True when both Paddle native runtime probe DLLs exist in <paramref name="dir"/>.</summary>
    public static bool PaddleNativePresent(string dir)
        => File.Exists(Path.Combine(dir, PaddleNativeProbeA))
           && File.Exists(Path.Combine(dir, PaddleNativeProbeB));

    /// <summary>
    /// True when <paramref name="dir"/> holds a usable PP-OCR model set: a detection (<c>*_det</c>),
    /// recognition (<c>*_rec</c>) and angle-classification (<c>*_cls</c>) model, each with its weight
    /// file. Matching by suffix keeps this correct across model variants (EnglishV4, ChineseV5, …)
    /// without hard-coding every model's folder name.
    /// </summary>
    public static bool PaddleModelsPresent(string dir)
        => HasModelWithSuffix(dir, "_det")
           && HasModelWithSuffix(dir, "_rec")
           && HasModelWithSuffix(dir, "_cls");

    /// <summary>True when <c>eng.traineddata</c> is present in <paramref name="dir"/>.</summary>
    public static bool TesseractDataPresent(string dir)
        => File.Exists(Path.Combine(dir, TesseractDataFile));

    private static bool HasModelWithSuffix(string dir, string suffix)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(sub);
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(sub, ModelParamsFile)))
            {
                return true;
            }
        }

        return false;
    }

    internal static int ToApproxMb(long bytes) => (int)Math.Ceiling(bytes / (1024.0 * 1024.0));

    /// <summary>
    /// Builds the Paddle engine's download requirement from the (already-probed) presence of its two
    /// components. Pure: the filesystem probing is done by the caller so this branch logic is
    /// deterministically testable.
    /// </summary>
    public static OcrAssetRequirement BuildPaddleRequirement(string engineDisplayName, bool nativePresent, bool modelsPresent)
    {
        var missing = new List<string>(2);
        long bytes = 0;
        if (!nativePresent)
        {
            bytes += PaddleNativeApproxBytes;
            missing.Add($"OCR engine runtime (~{ToApproxMb(PaddleNativeApproxBytes)} MB)");
        }
        if (!modelsPresent)
        {
            bytes += PaddleModelApproxBytes;
            missing.Add($"language models (~{ToApproxMb(PaddleModelApproxBytes)} MB)");
        }

        return new OcrAssetRequirement
        {
            EngineDisplayName = engineDisplayName,
            DownloadNeeded = missing.Count > 0,
            ApproxBytes = bytes,
            MissingComponents = missing,
        };
    }

    /// <summary>
    /// Builds the Tesseract engine's download requirement from the (already-probed) presence of its
    /// English language data. Only <c>eng.traineddata</c> downloads; the native binaries are vendored.
    /// </summary>
    public static OcrAssetRequirement BuildTesseractRequirement(string engineDisplayName, bool dataPresent)
    {
        var missing = new List<string>(1);
        long bytes = 0;
        if (!dataPresent)
        {
            bytes += TesseractDataApproxBytes;
            missing.Add($"English language data (~{ToApproxMb(TesseractDataApproxBytes)} MB)");
        }

        return new OcrAssetRequirement
        {
            EngineDisplayName = engineDisplayName,
            DownloadNeeded = missing.Count > 0,
            ApproxBytes = bytes,
            MissingComponents = missing,
        };
    }
}
