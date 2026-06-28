using OpenCvSharp;
using Tesseract;

namespace Yagu.OcrWorker;

/// <summary>
/// Tesseract-backed OCR engine. Mirrors the proven default quality settings used by the Memory app:
/// the pure-LSTM engine (<c>--oem 1</c>), the high-accuracy <c>tessdata_best</c> English model, and a
/// three-pass <c>extract_text</c> pipeline over a preprocessed (2x upscaled, grayscale, sharpened,
/// contrast-boosted) image. All three passes always run (the Memory app's <c>adaptive=False</c>
/// default) for the highest quality.
/// <para>
/// The native leptonica/tesseract binaries are vendored under <c>x64/</c> beside this worker (no
/// native download is needed). Only the <c>eng.traineddata</c> language data is lazy-downloaded to
/// %LOCALAPPDATA%\Yagu on first use.
/// </para>
/// </summary>
internal sealed class TesseractWorkerEngine : IWorkerOcrEngine
{
    // tessdata_best English model (same source the Memory app bundles). LSTM-only, high accuracy.
    private const string EngTrainedDataUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata";
    private const string Language = "eng";

    // PIL's ImageFilter.SHARPEN is a fixed 3x3 convolution: [[-2,-2,-2],[-2,32,-2],[-2,-2,-2]] / 16.
    private static readonly float[] SharpenKernel =
    {
        -2f / 16f, -2f / 16f, -2f / 16f,
        -2f / 16f, 32f / 16f, -2f / 16f,
        -2f / 16f, -2f / 16f, -2f / 16f,
    };

    private readonly TesseractEngine _engine;
    private readonly Action<string> _log;

    private TesseractWorkerEngine(TesseractEngine engine, Action<string> log)
    {
        _engine = engine;
        _log = log;
    }

    public static async Task<TesseractWorkerEngine> CreateAsync(Action<string> log)
    {
        string tessdataDir = ResolveTessdataDir();
        log($"tessdataDir={tessdataDir}");

        await EnsureTrainedDataAsync(tessdataDir, log).ConfigureAwait(false);

        // Make the native loader search beside this worker exe (x64\tesseract50.dll,
        // x64\leptonica-1.82.0.dll) regardless of the working directory Yagu launched us with.
        TesseractEnviornment.CustomSearchPath = AppContext.BaseDirectory;

        // --oem 1 (pure LSTM), matching the Memory app's engine configuration.
        var engine = new TesseractEngine(tessdataDir, Language, EngineMode.LstmOnly);
        log("tesseract engine ready");
        return new TesseractWorkerEngine(engine, log);
    }

    public string Recognize(string imagePath)
    {
        // Decode via byte buffer so non-ASCII paths work (OpenCV's ImRead uses the ANSI codepage).
        byte[] bytes = File.ReadAllBytes(imagePath);
        using Mat original = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (original.Empty())
        {
            throw new InvalidOperationException("unreadable image");
        }

        using Mat preprocessed = Preprocess(original);

        var collected = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1 (--psm 3, Auto): automatic page segmentation on the preprocessed image.
        string detail = RunPass(preprocessed, PageSegMode.Auto).Trim();
        foreach (string line in SplitLines(detail))
        {
            collected.Add(line);
        }

        // Pass 2 (--psm 6, SingleBlock) on the preprocessed image: a uniform block of text; catches
        // overlapping-window text the auto pass can miss. Only unique non-empty lines are merged in.
        detail = MergeExtra(detail, collected, RunPass(preprocessed, PageSegMode.SingleBlock));

        // Pass 3 (--psm 6, SingleBlock) on the ORIGINAL resolution: large/prominent text.
        detail = MergeExtra(detail, collected, RunPass(original, PageSegMode.SingleBlock));

        return detail;
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>
    /// Mirrors the Memory app's <c>_preprocess_for_ocr</c>: 2x upscale (Lanczos), grayscale, PIL
    /// SHARPEN convolution, then a 1.5x contrast boost (<c>out = 1.5*pixel - 0.5*mean</c>).
    /// </summary>
    private static Mat Preprocess(Mat colorBgr)
    {
        using var upscaled = new Mat();
        Cv2.Resize(colorBgr, upscaled, new Size(colorBgr.Width * 2, colorBgr.Height * 2), 0, 0, InterpolationFlags.Lanczos4);

        using var gray = new Mat();
        Cv2.CvtColor(upscaled, gray, ColorConversionCodes.BGR2GRAY);

        using Mat kernel = Mat.FromPixelData(3, 3, MatType.CV_32FC1, SharpenKernel);
        using var sharpened = new Mat();
        Cv2.Filter2D(gray, sharpened, MatType.CV_8U, kernel);

        // PIL ImageEnhance.Contrast uses the mean of the (grayscale) image as the blend midpoint.
        double mean = Math.Round(Cv2.Mean(sharpened).Val0);
        var enhanced = new Mat();
        sharpened.ConvertTo(enhanced, MatType.CV_8U, 1.5, -0.5 * mean);
        return enhanced;
    }

    private string RunPass(Mat image, PageSegMode mode)
    {
        try
        {
            Cv2.ImEncode(".png", image, out byte[] buffer);
            using Pix pix = Pix.LoadFromMemory(buffer);
            using Page page = _engine.Process(pix, mode);
            return page.GetText() ?? string.Empty;
        }
        catch (Exception ex)
        {
            // Mirror the Memory app: a failing pass must not lose text recognized by earlier passes.
            _log($"tesseract pass ({mode}) failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Mirrors the Memory app's <c>_merge_extra</c>: appends only the non-empty lines from
    /// <paramref name="newText"/> that were not already collected.
    /// </summary>
    private static string MergeExtra(string detail, HashSet<string> collected, string newText)
    {
        newText = newText.Trim();
        if (newText.Length == 0)
        {
            return detail;
        }

        var extra = new List<string>();
        foreach (string line in SplitLines(newText))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (collected.Add(line))
            {
                extra.Add(line);
            }
        }

        if (extra.Count == 0)
        {
            return detail;
        }

        string joined = string.Join("\n", extra);
        return detail.Length == 0 ? joined : detail + "\n" + joined;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static async Task EnsureTrainedDataAsync(string tessdataDir, Action<string> log)
    {
        Directory.CreateDirectory(tessdataDir);
        string target = Path.Combine(tessdataDir, "eng.traineddata");
        if (File.Exists(target))
        {
            log("tessdata already staged (eng.traineddata)");
            return;
        }

        log("downloading eng.traineddata (tessdata_best) ...");
        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
        string tempPath = Path.Combine(tessdataDir, $"eng.traineddata.{Guid.NewGuid():N}.tmp");
        try
        {
            using (Stream networkStream = await http.GetStreamAsync(EngTrainedDataUrl).ConfigureAwait(false))
            using (FileStream fileStream = File.Create(tempPath))
            {
                await networkStream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            File.Move(tempPath, target, overwrite: true);
            log("eng.traineddata staged");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the temp download.
                }
            }
        }
    }

    private static string ResolveTessdataDir() =>
        Environment.GetEnvironmentVariable("YAGU_OCR_TESSDATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yagu", "ocr-runtime", "tesseract", "tessdata");
}
