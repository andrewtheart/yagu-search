using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace Yagu.OcrWorker;

/// <summary>
/// PaddleSharp-backed OCR engine (the default). Lazy-downloads its native runtime and models to
/// %LOCALAPPDATA%\Yagu on first use, then runs the PP-OCR pipeline in-process (this worker is the
/// isolated, non-AOT host described in <see cref="Program"/>).
/// </summary>
internal sealed class PaddleWorkerEngine : IWorkerOcrEngine
{
    private readonly PaddleOcrAll _ocr;

    private PaddleWorkerEngine(PaddleOcrAll ocr) => _ocr = ocr;

    public static async Task<PaddleWorkerEngine> CreateAsync(Action<string> log)
    {
        string runtimeDir = ResolveRuntimeDir();
        string modelDir = ResolveModelDir();
        string modelName = Environment.GetEnvironmentVariable("YAGU_OCR_MODEL") ?? PaddleModelResolver.DefaultModelName;

        log($"runtimeDir={runtimeDir}");
        log($"modelDir={modelDir}");
        log($"model={modelName}");

        await NativeRuntime.EnsureAsync(runtimeDir, log).ConfigureAwait(false);
        NativeRuntime.AddNativeSearchDirectory(runtimeDir);

        // Control where PaddleSharp caches its downloaded models.
        Settings.GlobalModelDirectory = modelDir;
        OnlineFullModels online = PaddleModelResolver.Resolve(modelName);
        if (!ModelsPresent(modelDir))
        {
            DownloadGuard.EnsureAllowed("language models");
        }

        FullOcrModel model = await online.DownloadAsync().ConfigureAwait(false);

        var ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };

        // Optional detection-resolution cap (longest image side). 0 = unlimited (null), absent = the
        // PaddleSharp default. Higher caps improve small-text accuracy at the cost of speed.
        string? maxSideRaw = Environment.GetEnvironmentVariable("YAGU_OCR_MAX_SIDE");
        if (int.TryParse(maxSideRaw, out int maxSide))
        {
            ocr.Detector.MaxSize = maxSide <= 0 ? null : maxSide;
            log($"detectorMaxSize={(ocr.Detector.MaxSize is { } ms ? ms.ToString() : "unlimited")}");
        }

        return new PaddleWorkerEngine(ocr);
    }

    public string Recognize(string imagePath)
    {
        // Decode via byte buffer so non-ASCII paths work (OpenCV's ImRead uses the ANSI codepage).
        byte[] bytes = File.ReadAllBytes(imagePath);
        using Mat image = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (image.Empty())
        {
            throw new InvalidOperationException("unreadable image");
        }

        PaddleOcrResult result = _ocr.Run(image);
        return result.Text ?? string.Empty;
    }

    public void Dispose() => _ocr.Dispose();

    private static string ResolveRuntimeDir() =>
        Environment.GetEnvironmentVariable("YAGU_OCR_RUNTIME_DIR")
        ?? Path.Combine(LocalAppData(), "Yagu", "ocr-runtime", "paddle", "native");

    private static string ResolveModelDir() =>
        Environment.GetEnvironmentVariable("YAGU_OCR_MODEL_DIR")
        ?? Path.Combine(LocalAppData(), "Yagu", "ocr-runtime", "paddle", "models");

    /// <summary>
    /// True when <paramref name="modelDir"/> already holds a usable PP-OCR set: detection (<c>*_det</c>),
    /// recognition (<c>*_rec</c>) and classification (<c>*_cls</c>) folders, each with an
    /// <c>inference.pdiparams</c> weight file. Matches what PaddleSharp writes on download, so the
    /// download guard only trips when a fetch would actually occur.
    /// </summary>
    private static bool ModelsPresent(string modelDir) =>
        HasModelWithSuffix(modelDir, "_det")
        && HasModelWithSuffix(modelDir, "_rec")
        && HasModelWithSuffix(modelDir, "_cls");

    private static bool HasModelWithSuffix(string modelDir, string suffix)
    {
        if (!Directory.Exists(modelDir))
        {
            return false;
        }

        foreach (string sub in Directory.EnumerateDirectories(modelDir))
        {
            if (Path.GetFileName(sub).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(sub, "inference.pdiparams")))
            {
                return true;
            }
        }

        return false;
    }

    private static string LocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
