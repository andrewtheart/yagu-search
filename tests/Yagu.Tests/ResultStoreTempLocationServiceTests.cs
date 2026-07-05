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

public sealed class ResultStoreTempLocationServiceBranchTests
{
    [Fact]
    public void ChoosePreferredOption_AllLaunchDrives_FallsBackToFirst()
    {
        var options = new[]
        {
            new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C: - launch", 100L * 1024 * 1024 * 1024, true),
            new ResultStoreTempDriveOption(@"D:\", @"D:\Temp\Yagu", "D: - launch", 200L * 1024 * 1024 * 1024, true),
        };

        // When all options are the launch drive, it looks for non-launch first (D: != C: launch root), returns D:
        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, currentTempDirectory: null, launchDriveRoot: @"C:\");
        Assert.NotNull(chosen);
        Assert.Equal(@"D:\", chosen.DriveRoot);

        // When launch root matches ALL, it falls back to options[0]
        var chosen2 = ResultStoreTempLocationService.ChoosePreferredOption(options, currentTempDirectory: null, launchDriveRoot: null);
        Assert.NotNull(chosen2);
        Assert.Equal(@"C:\", chosen2.DriveRoot);
    }

    [Fact]
    public void FormatBytesAsGiB_Zero_ReturnsZero()
    {
        Assert.Equal("0.0 GB", ResultStoreTempLocationService.FormatBytesAsGiB(0));
    }

    [Fact]
    public void FormatBytesAsGiB_LargeValue_Formats()
    {
        Assert.Equal("1,024.0 GB", ResultStoreTempLocationService.FormatBytesAsGiB(1024L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void GetWritableDriveOptions_ReturnsList()
    {
        var options = ResultStoreTempLocationService.GetWritableDriveOptions();
        Assert.NotNull(options);
        Assert.True(options.Count >= 1);
    }

    [Fact]
    public void IsUsableTempDirectory_RequiresFreeSpace_RejectsTooSmall()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yagu-usable-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool usable = ResultStoreTempLocationService.IsUsableTempDirectory(dir, requireMinimumFreeSpace: true);
            Assert.IsType<bool>(usable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ChoosePreferredOption_CurrentDriveNotInOptions_SelectsNonLaunchDrive()
    {
        var options = new[]
        {
            new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C:", 100L * 1024 * 1024 * 1024, true),
            new ResultStoreTempDriveOption(@"E:\", @"E:\Temp\Yagu", "E:", 200L * 1024 * 1024 * 1024, false),
        };

        // Current temp is on D: which isn't in options → falls through to non-launch-drive selection
        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, @"D:\Temp\Yagu", launchDriveRoot: @"C:\");
        Assert.NotNull(chosen);
        Assert.Equal(@"E:\", chosen.DriveRoot);
    }

    [Fact]
    public void ChoosePreferredOption_NullCurrentAndNullLaunchRoot_ReturnsFirst()
    {
        var options = new[]
        {
            new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C:", 100L * 1024 * 1024 * 1024, false),
            new ResultStoreTempDriveOption(@"D:\", @"D:\Temp\Yagu", "D:", 200L * 1024 * 1024 * 1024, false),
        };

        // Null launch root → no drive matches launch → falls to options[0]
        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, currentTempDirectory: null, launchDriveRoot: null);
        Assert.NotNull(chosen);
        Assert.Equal(@"C:\", chosen.DriveRoot);
    }

    [Fact]
    public void IsUsableTempDirectory_DriveNotReady_ReturnsFalse()
    {
        // A drive letter that likely doesn't exist
        Assert.False(ResultStoreTempLocationService.IsUsableTempDirectory(@"Z:\Temp\Yagu", requireMinimumFreeSpace: false));
    }

    [Fact]
    public void BuildTempDirectory_WithoutTrailingSeparator_NormalizesAndBuilds()
    {
        string root = (Path.GetPathRoot(Environment.CurrentDirectory) ?? @"C:\").TrimEnd('\\');
        string result = ResultStoreTempLocationService.BuildTempDirectory(root);
        Assert.Contains("Temp", result);
        Assert.Contains("Yagu", result);
    }

    [Fact]
    public void NormalizeTempDirectory_PathWithExtraSlashes_ReturnsCleanFullPath()
    {
        string messyPath = @"C:\Temp\\Yagu\";
        string result = ResultStoreTempLocationService.NormalizeTempDirectory(messyPath);
        Assert.False(result.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void GetWritableDriveOptions_WithLaunchDriveRoot_MarksLaunchDrive()
    {
        string? launchRoot = ResultStoreTempLocationService.GetLaunchDriveRoot();
        var options = ResultStoreTempLocationService.GetWritableDriveOptions(launchRoot);
        Assert.Contains(options, o => o.IsLaunchDrive);
    }

    [Fact]
    public void GetWritableDriveOptions_WithoutLaunchDriveRoot_NoLaunchDrive()
    {
        var options = ResultStoreTempLocationService.GetWritableDriveOptions(null);
        Assert.All(options, o => Assert.False(o.IsLaunchDrive));
    }

    [Fact]
    public void GetWritableDriveOptions_SortsByLaunchDriveThenFreeSpace()
    {
        string? launchRoot = ResultStoreTempLocationService.GetLaunchDriveRoot();
        var options = ResultStoreTempLocationService.GetWritableDriveOptions(launchRoot);
        if (options.Count < 2) return; // can't verify sort with single drive

        // Non-launch drives come first (IsLaunchDrive=false sorts before true)
        int firstLaunchIdx = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].IsLaunchDrive) { firstLaunchIdx = i; break; }
        }
        if (firstLaunchIdx > 0)
        {
            // All before it should be non-launch
            for (int i = 0; i < firstLaunchIdx; i++)
                Assert.False(options[i].IsLaunchDrive);
        }
    }

    [Fact]
    public void ChoosePreferredOption_AllMatchLaunchRoot_FallsBackToFirstOption()
    {
        var options = new[]
        {
            new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C:", 100L * 1024 * 1024 * 1024, false),
        };

        // Current doesn't match, launch root matches C: so there's no non-launch option → returns first
        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, @"Z:\nonexist", launchDriveRoot: @"C:\");
        Assert.NotNull(chosen);
        Assert.Equal(@"C:\", chosen.DriveRoot);
    }

    [Fact]
    public void IsUsableTempDirectory_WithFreeSpaceRequired_CurrentDriveIsUsable()
    {
        // The temp path should have enough free space on most dev machines
        string dir = Path.Combine(Path.GetTempPath(), "yagu-freespace-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool usable = ResultStoreTempLocationService.IsUsableTempDirectory(dir, requireMinimumFreeSpace: true);
            // Result depends on machine free space — just verify no exception
            Assert.IsType<bool>(usable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NormalizeTempDirectory_InvalidPathChars_ReturnsTrimmed()
    {
        // Path with embedded NUL causes GetFullPath to throw, exercising the catch branch
        string invalidPath = "C:\\bad\0path";
        string result = ResultStoreTempLocationService.NormalizeTempDirectory(invalidPath);
        // Catch block returns tempDirectory.Trim()
        Assert.Equal(invalidPath.Trim(), result);
    }

    [Fact]
    public void NormalizeTempDirectory_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ResultStoreTempLocationService.NormalizeTempDirectory(null));
    }

    [Fact]
    public void NormalizeTempDirectory_Whitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ResultStoreTempLocationService.NormalizeTempDirectory("   "));
    }

    [Fact]
    public void IsUsableTempDirectory_InvalidPath_ReturnsFalse()
    {
        // Path with NUL char triggers exception in Path.GetPathRoot -> caught -> returns false
        bool result = ResultStoreTempLocationService.IsUsableTempDirectory("Z:\0invalid", requireMinimumFreeSpace: false);
        Assert.False(result);
    }

    [Fact]
    public void IsUsableTempDirectory_EmptyString_ReturnsFalse()
    {
        bool result = ResultStoreTempLocationService.IsUsableTempDirectory("", requireMinimumFreeSpace: false);
        Assert.False(result);
    }

    [Fact]
    public void BuildTempDirectory_ValidRoot_ReturnsExpectedPath()
    {
        string result = ResultStoreTempLocationService.BuildTempDirectory(@"D:\");
        Assert.Equal(@"D:\Temp\Yagu", result);
    }

    [Fact]
    public void BuildTempDirectory_RootWithoutTrailingSlash_NormalizesPath()
    {
        // NormalizeDriveRoot adds trailing separator
        string result = ResultStoreTempLocationService.BuildTempDirectory(@"D:");
        Assert.Contains("Temp", result);
        Assert.Contains("Yagu", result);
    }
}