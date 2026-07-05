using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class DiskTypeDetectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsHardDisk_NullOrEmpty_ReturnsFalse(string? path)
    {
        Assert.False(DiskTypeDetector.IsHardDisk(path!));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("noroot")]
    [InlineData("123")]
    public void IsHardDisk_NoValidRoot_ReturnsFalse(string path)
    {
        Assert.False(DiskTypeDetector.IsHardDisk(path));
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"\\?\UNC\server\share")]
    public void IsHardDisk_UncPath_ReturnsFalse(string path)
    {
        // UNC paths don't have a single drive letter root
        Assert.False(DiskTypeDetector.IsHardDisk(path));
    }

    [Fact]
    public void IsHardDisk_NonExistentDrive_ReturnsFalse()
    {
        // Z: is unlikely to exist on the test machine
        Assert.False(DiskTypeDetector.IsHardDisk(@"Z:\nonexistent\path"));
    }

    [Fact]
    public void IsHardDisk_CurrentDrive_DoesNotThrow()
    {
        // The current drive must exist; just ensure it doesn't crash
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var result = DiskTypeDetector.IsHardDisk(root);
        // Result is either true or false — no exception thrown
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsHardDisk_ValidDriveWithSubpath_DoesNotThrow()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "some", "deep", "path");
        var result = DiskTypeDetector.IsHardDisk(path);
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsHardDisk_DriveLetterOnly_ReturnsFalse()
    {
        // "D" alone has no root separator
        Assert.False(DiskTypeDetector.IsHardDisk("D"));
    }

    [Theory]
    [InlineData("1:\\folder")]
    [InlineData("@:\\file")]
    [InlineData("!:\\something")]
    public void IsHardDisk_InvalidDriveLetter_ReturnsFalse(string path)
    {
        Assert.False(DiskTypeDetector.IsHardDisk(path));
    }
}
