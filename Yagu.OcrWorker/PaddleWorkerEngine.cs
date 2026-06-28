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
        FullOcrModel model = await online.DownloadAsync().ConfigureAwait(false);

        var ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };
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

    private static string LocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
