using OpenCvSharp;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Sdcb.PaddleOCR.Tests;

public class OnlineModelsTest(ITestOutputHelper console)
{
    [Fact]
    public async Task FastCheckOCR()
    {
        // EnglishV3 is not working in macos-arm64, so we use ChineseV5 instead: https://github.com/PaddlePaddle/Paddle/issues/72413
        // ----------------------
        // Error Message Summary:
        // ----------------------
        // NotFoundError: No allocator found for the place, Place(undefined:0)
        //   [Hint: Expected iter != allocators.end(), but received iter == allocators.end().] (at /Users/runner/work/PaddleSharp/PaddleSharp/paddle-src/paddle/phi/core/memory/allocation/allocator_facade.cc:381)
        //   [operator < matmul > error]
        // The active test run was aborted. Reason: Test host process crashed
        FullOcrModel model = await (OnlineFullModels.ChineseV5 with { ClsModel = null }).DownloadAsync();

        // from: https://visualstudio.microsoft.com/wp-content/uploads/2021/11/Home-page-extension-visual-updated.png
        byte[] sampleImageData = File.ReadAllBytes(@"./samples/vsext.png");

        using (PaddleOcrAll all = new(model)
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        })
        {
            // Load local file by following code:
            // using (Mat src2 = Cv2.ImRead(@"C:\test.jpg"))
            using (Mat src = Cv2.ImDecode(sampleImageData, ImreadModes.Color))
            {
                Stopwatch sw = Stopwatch.StartNew();
                PaddleOcrResult result = all.Run(src);
                console.WriteLine($"lapsed={sw.ElapsedMilliseconds} ms");
                console.WriteLine("Detected all texts: \n" + result.Text);
                foreach (PaddleOcrResultRegion region in result.Regions)
                {
                    console.WriteLine($"Text: {region.Text}, Score: {region.Score}, RectCenter: {region.Rect.Center}, RectSize:    {region.Rect.Size}, Angle: {region.Rect.Angle}");
                }
            }
        }
    }

    [Fact]
    public async Task V4DetOnly()
    {
        DetectionModel detModel = await OnlineDetectionModel.ChineseV4.DownloadAsync();

        using (Mat src = Cv2.ImRead(@"./samples/5ghz.jpg"))
        using (PaddleOcrDetector r = new(detModel))
        {
            Stopwatch sw = Stopwatch.StartNew();
            RotatedRect[] rects = r.Run(src);
            console.WriteLine($"elapsed={sw.ElapsedMilliseconds}ms");
            console.WriteLine($"Detected {rects.Length} rects.");
        }
    }

    [Fact]
    public async Task V4RecOnly()
    {
        RecognizationModel recModel = await LocalDictOnlineRecognizationModel.ChineseV4.DownloadAsync();

        using (Mat src = Cv2.ImRead(@"./samples/5ghz.jpg"))
        using (PaddleOcrRecognizer r = new(recModel))
        {
            Stopwatch sw = Stopwatch.StartNew();
            PaddleOcrRecognizerResult result = r.Run(src);
            console.WriteLine($"elapsed={sw.ElapsedMilliseconds}ms");
            console.WriteLine(result.ToString());
            Assert.True(result.Score > 0.9);
        }
    }
}