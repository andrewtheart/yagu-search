using Yagu.Services;

namespace Yagu.Tests;

public sealed class ResultStoreTempLocationServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "YaguTempLocationTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    [Fact]
    public void BuildTempDirectory_NormalizesDriveRoot()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory) ?? @"C:\";

        string result = ResultStoreTempLocationService.BuildTempDirectory(root.TrimEnd(Path.DirectorySeparatorChar));

        Assert.Equal(Path.Combine(root, "Temp", "Yagu"), result);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeTempDirectory_BlankValues_ReturnEmpty(string? value, string expected)
    {
        Assert.Equal(expected, ResultStoreTempLocationService.NormalizeTempDirectory(value));
    }

    [Fact]
    public void NormalizeTempDirectory_ValidPath_TrimsEndingSeparatorAndExpandsFullPath()
    {
        string path = Path.Combine(_tempRoot, "child") + Path.DirectorySeparatorChar;

        string result = ResultStoreTempLocationService.NormalizeTempDirectory(path);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), result);
    }

    [Fact]
    public void NormalizeTempDirectory_InvalidPath_ReturnsTrimmedOriginal()
    {
        string invalid = "  bad\0path  ";

        string result = ResultStoreTempLocationService.NormalizeTempDirectory(invalid);

        Assert.Equal(invalid.Trim(), result);
    }

    [Fact]
    public void FormatBytesAsGiB_FormatsOneDecimalPlace()
    {
        Assert.Equal("1.5 GB", ResultStoreTempLocationService.FormatBytesAsGiB(1536L * 1024 * 1024));
    }

    [Fact]
    public void ChoosePreferredOption_ReturnsCurrentDirectoryDriveWhenPresent()
    {
        var options = new[]
        {
            Option(@"C:\", isLaunchDrive: false),
            Option(@"D:\", isLaunchDrive: true),
        };

        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, @"D:\Temp\Yagu", launchDriveRoot: @"D:\");

        Assert.NotNull(chosen);
        Assert.Equal(@"D:\", chosen.DriveRoot);
    }

    [Fact]
    public void ChoosePreferredOption_PrefersNonLaunchDriveWhenCurrentMissing()
    {
        var options = new[]
        {
            Option(@"D:\", isLaunchDrive: true),
            Option(@"E:\", isLaunchDrive: false),
        };

        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, currentTempDirectory: null, launchDriveRoot: @"D:\");

        Assert.NotNull(chosen);
        Assert.Equal(@"E:\", chosen.DriveRoot);
    }

    [Fact]
    public void ChoosePreferredOption_ReturnsNullForEmptyOptions()
    {
        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(Array.Empty<ResultStoreTempDriveOption>(), null);

        Assert.Null(chosen);
    }

    [Fact]
    public void IsUsableTempDirectory_RejectsBlankAndInvalidRoots()
    {
        Assert.False(ResultStoreTempLocationService.IsUsableTempDirectory(null, requireMinimumFreeSpace: false));
        Assert.False(ResultStoreTempLocationService.IsUsableTempDirectory("   ", requireMinimumFreeSpace: false));
        Assert.False(ResultStoreTempLocationService.IsUsableTempDirectory(@"?:\Temp\Yagu", requireMinimumFreeSpace: false));
    }

    [Fact]
    public void IsUsableTempDirectory_CreatesAndProbesWritableDirectory()
    {
        string directory = Path.Combine(_tempRoot, "store");

        bool usable = ResultStoreTempLocationService.IsUsableTempDirectory(directory, requireMinimumFreeSpace: false);

        Assert.True(usable);
        Assert.True(Directory.Exists(directory));
        Assert.Empty(Directory.GetFiles(directory, ".yagu-write-test-*.tmp"));
    }

    [Fact]
    public void GetLaunchDriveRoot_ReturnsNormalizedRoot()
    {
        string? root = ResultStoreTempLocationService.GetLaunchDriveRoot();

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), root);
    }

    private static ResultStoreTempDriveOption Option(string driveRoot, bool isLaunchDrive) =>
        new(
            driveRoot,
            Path.Combine(driveRoot, "Temp", "Yagu"),
            $"{driveRoot} - 100.0 GB free" + (isLaunchDrive ? " - launch drive" : string.Empty),
            100L * 1024 * 1024 * 1024,
            isLaunchDrive);
}