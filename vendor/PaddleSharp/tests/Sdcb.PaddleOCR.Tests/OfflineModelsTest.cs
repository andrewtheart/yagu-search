using OpenCvSharp;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace Sdcb.PaddleOCR.Tests;

public class OfflineModelsTest(ITestOutputHelper console)
{
    [Fact]
    public void FastCheckOCREnglishV3()
    {
        Console.WriteLine($"Running EnglishV3 test on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (RuntimeInformation.OSArchitecture == Architecture.X64)
            {
                // macOS-x64 have onnx, but not have mkldnn, it's buggy for in memory EnglishV3, so skip this test
                // E0623 03:57:55.708065 159170560 onnxruntime_predictor.cc:354] Got invalid dimensions for input: x for the following indices
                // index: 2 Got: 320 Expected: 960
                // Please fix either the inputs or the model.
                Console.WriteLine("Skipping EnglishV3 test on macOS x64 due to known issues with ONNX model.");
                return;
            }
            else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                // EnglishV3 is not working in macos-arm64, so we use EnglishV4 instead: https://github.com/PaddlePaddle/Paddle/issues/72413
                // ----------------------
                // Error Message Summary:
                // ----------------------
                // NotFoundError: No allocator found for the place, Place(undefined:0)
                //   [Hint: Expected iter != allocators.end(), but received iter == allocators.end().] (at /Users/runner/work/PaddleSharp/PaddleSharp/paddle-src/paddle/phi/core/memory/allocation/allocator_facade.cc:381)
                //   [operator < matmul > error]
                // The active test run was aborted. Reason: Test host process crashed
                Console.WriteLine("Skipping EnglishV3 test on macOS arm64 because this issue: https://github.com/PaddlePaddle/Paddle/issues/72413");
                return;
            }
        }

        FullOcrModel model = LocalFullModels.EnglishV3;
        FastCheck(model);
    }

    [Fact]
    public void FastCheckOCREnglishV4()
    {
        // skip for osx-arm64
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            Console.WriteLine("Skipping EnglishV4 test on macOS arm64 due to known issues: https://github.com/PaddlePaddle/Paddle/issues/72413");
            return;
        }

        FullOcrModel model = LocalFullModels.EnglishV4;
        FastCheck(model);
    }

    [Fact]
    public void FastCheckOCRChineseV4()
    {
        // skip for osx-arm64
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            Console.WriteLine("Skipping EnglishV4 test on macOS arm64 due to known issues: https://github.com/PaddlePaddle/Paddle/issues/72413");
            return;
        }

        Console.WriteLine($"Running ChineseV4 test on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        FullOcrModel model = LocalFullModels.ChineseV4;
        FastCheck(model);
    }

    [Fact]
    public void FastCheckOCRChineseV5()
    {
        Console.WriteLine($"Running ChineseV5 test on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        FullOcrModel model = LocalFullModels.ChineseV5;
        FastCheck(model);
    }

    private void FastCheck(FullOcrModel model)
    {
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
                PaddleOcrResult result = all.Run(src);
                console.WriteLine("Detected all texts: \n" + result.Text);
                Console.WriteLine("Detected all texts: \n" + result.Text);
                foreach (PaddleOcrResultRegion region in result.Regions)
                {
                    console.WriteLine($"Text: {region.Text}, Score: {region.Score}, RectCenter: {region.Rect.Center}, RectSize:    {region.Rect.Size}, Angle: {region.Rect.Angle}");
                }
            }
        }
    }

    [Fact]
    public async Task QueuedOCR()
    {
        Console.WriteLine($"Running QueuedOCR test on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        FullOcrModel model = LocalFullModels.ChineseV5;

        // from: https://visualstudio.microsoft.com/wp-content/uploads/2021/11/Home-page-extension-visual-updated.png
        byte[] sampleImageData = File.ReadAllBytes(@"./samples/vsext.png");

        using QueuedPaddleOcrAll all = new(() => new PaddleOcrAll(model)
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        }, consumerCount: 1, boundedCapacity: 64);

        {
            // Load local file by following code:
            // using (Mat src2 = Cv2.ImRead(@"C:\test.jpg"))
            using (Mat src = Cv2.ImDecode(sampleImageData, ImreadModes.Color))
            {
                PaddleOcrResult result = await all.Run(src);
                console.WriteLine("Detected all texts: \n" + result.Text);
                Console.WriteLine("Detected all texts: \n" + result.Text);
                foreach (PaddleOcrResultRegion region in result.Regions)
                {
                    console.WriteLine($"Text: {region.Text}, Score: {region.Score}, RectCenter: {region.Rect.Center}, RectSize:    {region.Rect.Size}, Angle: {region.Rect.Angle}");
                }
            }
        }
    }
}