using Yagu.Services;

namespace Yagu.Tests;

public sealed class EverythingAssetPathsTests
{
    [Theory]
    [InlineData(true, "Everything-1.4.1.1032.x64-Setup.exe")]
    [InlineData(false, "Everything-1.4.1.1032.x86-Setup.exe")]
    public void SetupFileName_MatchesVoidtoolsNaming(bool is64Bit, string expected)
    {
        Assert.Equal(expected, EverythingAssetPaths.SetupFileName(is64Bit));
    }

    [Theory]
    [InlineData(true, "https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe")]
    [InlineData(false, "https://www.voidtools.com/Everything-1.4.1.1032.x86-Setup.exe")]
    public void DownloadUrl_PointsAtVoidtoolsOverHttps(bool is64Bit, string expected)
    {
        Assert.Equal(expected, EverythingAssetPaths.DownloadUrl(is64Bit));
        Assert.StartsWith("https://", EverythingAssetPaths.DownloadUrl(is64Bit));
    }

    [Fact]
    public void BundledRoot_IsEverythingSetupFolderBesideApp()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "everything-setup"), EverythingAssetPaths.BundledRoot);
    }

    [Fact]
    public void BundledInstallerPath_ReturnsNull_WhenNotStaged()
    {
        // The test host is not the offline edition, so no bundled setup is staged beside it.
        string x64 = Path.Combine(EverythingAssetPaths.BundledRoot, EverythingAssetPaths.SetupFileName(true));
        if (File.Exists(x64)) return; // defensive: a real payload happens to be present

        Assert.Null(EverythingAssetPaths.BundledInstallerPath(true));
        Assert.Null(EverythingAssetPaths.BundledInstallerPath(false));
    }

    [Fact]
    public void BundledInstallerPath_ReturnsPath_WhenSetupPresent()
    {
        string root = EverythingAssetPaths.BundledRoot;
        bool createdRoot = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, EverythingAssetPaths.SetupFileName(is64Bit: true));
        bool createdFile = !File.Exists(file);
        try
        {
            if (createdFile)
            {
                File.WriteAllText(file, "stub");
            }

            Assert.Equal(file, EverythingAssetPaths.BundledInstallerPath(true));
        }
        finally
        {
            if (createdFile)
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }

            if (createdRoot)
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
