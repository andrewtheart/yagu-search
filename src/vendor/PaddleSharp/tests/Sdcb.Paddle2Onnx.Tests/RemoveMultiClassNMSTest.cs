using Sdcb.PaddleOCR.Models.Online;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace Sdcb.Paddle2Onnx.Tests;

[Trait("Category", "WindowsOnly")]
public class RemoveMultiClassNMSTest(ITestOutputHelper console)
{
    [Fact]
    public async Task NormalOCRDetectionModel_Should_CanExport()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            console.WriteLine("Skipping test on non-Windows platform.");
            return;
        }

        // Arrange
        await LocalDictOnlineRecognizationModel.ChineseV3.DownloadAsync();
        string dir = Path.Combine(Settings.GlobalModelDirectory, LocalDictOnlineRecognizationModel.ChineseV3.Name);
        string modelFile = Path.Combine(dir, "inference.pdmodel");
        string paramsFile = Path.Combine(dir, "inference.pdiparams");
        byte[] onnxModel = Paddle2OnnxConverter.ConvertToOnnx(modelFile, paramsFile);

        // Act
        byte[] newOnnxModel = Paddle2OnnxConverter.RemoveOnnxMultiClassNMS(onnxModel);

        // Assert
        Assert.NotEmpty(newOnnxModel);
        console.WriteLine($"onnxModel: {onnxModel.Length} bytes");
        console.WriteLine($"newOnnxModel: {newOnnxModel.Length} bytes");
    }
}