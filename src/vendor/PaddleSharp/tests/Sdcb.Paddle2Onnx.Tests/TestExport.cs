using Sdcb.PaddleOCR.Models.Details;
using Sdcb.PaddleOCR.Models.Online;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace Sdcb.Paddle2Onnx.Tests;

[Trait("Category", "WindowsOnly")]
public class TestExport(ITestOutputHelper console)
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
        FileDetectionModel fd = await OnlineDetectionModel.ChineseV3.DownloadAsync();
        string modelFile = Path.Combine(fd.DirectoryPath, "inference.pdmodel");
        string paramsFile = Path.Combine(fd.DirectoryPath, "inference.pdiparams");

        // Act
        byte[] onnxModel = Paddle2OnnxConverter.ConvertToOnnx(modelFile, paramsFile);

        // Assert
        Assert.NotEmpty(onnxModel);
    }

    [Fact]
    public async Task NormalOCRDetectionModelBuffer_Should_CanExport()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            console.WriteLine("Skipping test on non-Windows platform.");
            return;
        }

        // Arrange
        FileDetectionModel fd = await OnlineDetectionModel.ChineseV3.DownloadAsync();
        string modelFile = Path.Combine(fd.DirectoryPath, "inference.pdmodel");
        string paramsFile = Path.Combine(fd.DirectoryPath, "inference.pdiparams");
        byte[] modelBuffer = File.ReadAllBytes(modelFile);
        byte[] paramsBuffer = File.ReadAllBytes(paramsFile);

        // Act
        byte[] onnxModel = Paddle2OnnxConverter.ConvertToOnnx(modelBuffer, paramsBuffer);

        // Assert
        Assert.NotEmpty(onnxModel);
    }
}