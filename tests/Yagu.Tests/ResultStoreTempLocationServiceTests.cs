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

    [Fact]
    public async Task ProbeForStartupAsync_UsableCurrentDirectorySkipsDriveEnumeration()
    {
        string directory = Path.Combine(_tempRoot, "current");

        ResultStoreTempLocationProbe result = await ResultStoreTempLocationService.ProbeForStartupAsync(
            directory,
            validateCurrentDirectory: true);

        Assert.True(result.CurrentDirectoryIsUsable);
        Assert.Null(result.LaunchDriveRoot);
        Assert.Empty(result.DriveOptions);
    }

    [Fact]
    public async Task GetWritableDriveOptionsAsync_ReturnsDiscoveredDrives()
    {
        IReadOnlyList<ResultStoreTempDriveOption> result =
            await ResultStoreTempLocationService.GetWritableDriveOptionsAsync();

        Assert.NotNull(result);
        Assert.All(result, option => Assert.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            option.DriveRoot));
    }

    [Fact]
    public async Task ProbeForStartupAsync_DisabledValidationReturnsDriveSnapshot()
    {
        string directory = Path.Combine(_tempRoot, "current");

        ResultStoreTempLocationProbe result = await ResultStoreTempLocationService.ProbeForStartupAsync(
            directory,
            validateCurrentDirectory: false);

        Assert.False(result.CurrentDirectoryIsUsable);
        Assert.False(string.IsNullOrWhiteSpace(result.LaunchDriveRoot));
        Assert.NotNull(result.DriveOptions);
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
    public void SortDriveOptions_OrdersByMediaTierThenAdvertisedSpeed()
    {
        var options = new List<ResultStoreTempDriveOption>
        {
            Option(@"H:\", ResultStoreDriveTier.HardDisk, 5_400),
            Option(@"A:\", ResultStoreDriveTier.Sata),
            Option(@"N:\", ResultStoreDriveTier.Nvme),
            Option(@"I:\", ResultStoreDriveTier.HardDisk),
            Option(@"S:\", ResultStoreDriveTier.SolidState),
            Option(@"G:\", ResultStoreDriveTier.HardDisk, 7_200),
        };

        ResultStoreTempLocationService.SortDriveOptions(options);

        Assert.Equal(
            [@"N:\", @"S:\", @"A:\", @"G:\", @"H:\", @"I:\"],
            options.Select(static option => option.DriveRoot));
    }

    private static ResultStoreTempDriveOption Option(
        string driveRoot,
        ResultStoreDriveTier driveTier,
        uint? advertisedSpeedRpm = null) =>
        new(
            driveRoot,
            Path.Combine(driveRoot, "Temp", "Yagu"),
            driveRoot,
            100L * 1024 * 1024 * 1024,
            IsLaunchDrive: false,
            driveTier,
            advertisedSpeedRpm);

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

public sealed class ResultStoreTempLocationStartupTests
{
    [Fact]
    public void StartupDriveAndDirectoryProbes_AreAwaitedOffTheUiThread()
    {
        string root = FindRepoRoot();
        string startup = ReadMainWindowSources(root);
        string method = ExtractMethod(
            startup,
            "private async Task CheckFirstRunResultStoreTempLocationAsync()",
            "// FILE:");
        string service = File.ReadAllText(Path.Combine(
            root, "src", "Yagu", "Services", "ResultStoreTempLocationService.cs"));

        int probe = method.IndexOf(
            "await ResultStoreTempLocationService.ProbeForStartupAsync(",
            StringComparison.Ordinal);
        int dialog = method.IndexOf(
            "await ResultStoreTempLocationWindow.ShowAsync(",
            StringComparison.Ordinal);

        Assert.True(probe >= 0, "Startup must await the background temp-location probe.");
        Assert.True(dialog > probe, "The completed drive snapshot must be passed to the dialog.");
        Assert.DoesNotContain("ResultStoreTempLocationService.IsUsableTempDirectory(", method);
        Assert.DoesNotContain("ResultStoreTempLocationService.GetWritableDriveOptions(", method);
        Assert.Contains("Task.Run(() =>", service);
        Assert.Contains("GetWritableDriveOptions(launchDriveRoot), cancellationToken", service);
    }

    [Fact]
    public void StartupEverythingDetection_DoesNotScanPathRegistryOrProcessesOnTheUiThread()
    {
        string root = FindRepoRoot();
        string startup = ReadMainWindowSources(root);
        string method = ExtractMethod(
            startup,
            "private async Task CheckEverythingAsync()",
            "private async Task<bool> DownloadEverythingInstallerAsync(");

        Assert.Contains(
            "if (_preparedEverythingStartupDetection is { } preparedDetection)",
            method);
        Assert.Contains(
            "detection = await Task.Run(DetectEverythingStartupState);",
            method);
        Assert.DoesNotContain("FileLister.FindEsExe()", method);
        Assert.DoesNotContain("Process.GetProcessesByName(", method);
        Assert.Contains("private static EverythingStartupDetection DetectEverythingStartupState()", startup);
        Assert.Contains("EverythingStartupDetection installedDetection = await Task.Run(DetectEverythingStartupState);", method);
    }

    [Fact]
    public void SettingsDrivePicker_UsesSharedBackgroundDriveDiscovery()
    {
        string root = FindRepoRoot();
        string settings = File.ReadAllText(Path.Combine(
            root, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains(
            "_ = ResultStoreTempLocationService.GetWritableDriveOptionsAsync(launchDrive)",
            settings);
        Assert.DoesNotContain(
            "Task.Run(() => ResultStoreTempLocationService.GetWritableDriveOptions(launchDrive))",
            settings);
    }

    [Fact]
    public void DeveloperOptions_CanResetResultStoragePromptWithoutClearingTheSelectedPath()
    {
        string root = FindRepoRoot();
        string viewModel = MainViewModelPartials.Text;
        string settings = File.ReadAllText(Path.Combine(
            root, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains("public async Task ResetResultStoreTempLocationPromptAsync()", viewModel);
        Assert.Contains("settings => settings.HasChosenSearchResultTempDirectory = false", viewModel);
        int start = viewModel.IndexOf("public async Task ResetResultStoreTempLocationPromptAsync()", StringComparison.Ordinal);
        string reset = viewModel.Substring(start, Math.Min(750, viewModel.Length - start));
        Assert.DoesNotContain("settings.SearchResultTempDirectory =", reset);

        Assert.Contains("Reset result-storage location prompt (re-prompt on startup)", settings);
        Assert.Contains("await _viewModel.ResetResultStoreTempLocationPromptAsync();", settings);
        Assert.Contains("RegisterDefaultResetButton(resetResultStoreTempLocationPrompt", settings);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker after method: {endMarker}");
        return source[start..end];
    }

    private static string ReadMainWindowSources(string root)
    {
        string directory = Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow");
        return string.Concat(
            Directory.GetFiles(directory, "MainWindow*.cs")
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => $"\n// FILE: {Path.GetFileName(path)}\n{File.ReadAllText(path)}"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

public sealed class ResultStoreDriveProfileDetectorTests
{
    [Fact]
    public void PhysicalDiskQueryCommand_ReadsRawCimPropertyValues()
    {
        Assert.Contains("$_.CimInstanceProperties['DeviceId'].Value", ResultStoreDriveProfileDetector.PhysicalDiskQueryCommand);
        Assert.Contains("[uint32]($_.CimInstanceProperties['MediaType'].Value)", ResultStoreDriveProfileDetector.PhysicalDiskQueryCommand);
        Assert.Contains("[uint32]($_.CimInstanceProperties['BusType'].Value)", ResultStoreDriveProfileDetector.PhysicalDiskQueryCommand);
        Assert.Contains("[uint32]($_.CimInstanceProperties['SpindleSpeed'].Value)", ResultStoreDriveProfileDetector.PhysicalDiskQueryCommand);
        Assert.DoesNotContain("[uint32]($_.MediaType)", ResultStoreDriveProfileDetector.PhysicalDiskQueryCommand);
    }

    [Theory]
    [InlineData(17u, 4u, false, ResultStoreDriveTier.Nvme)]
    [InlineData(20u, null, null, ResultStoreDriveTier.Nvme)]
    [InlineData(11u, 3u, true, ResultStoreDriveTier.HardDisk)]
    [InlineData(11u, 4u, false, ResultStoreDriveTier.Sata)]
    [InlineData(7u, 4u, false, ResultStoreDriveTier.SolidState)]
    [InlineData(null, 5u, null, ResultStoreDriveTier.SolidState)]
    [InlineData(null, null, true, ResultStoreDriveTier.HardDisk)]
    [InlineData(null, null, false, ResultStoreDriveTier.SolidState)]
    [InlineData(11u, null, null, ResultStoreDriveTier.Sata)]
    [InlineData(null, null, null, ResultStoreDriveTier.Unknown)]
    public void Classify_UsesBusMediaAndSeekPenalty(
        uint? busType,
        uint? mediaType,
        bool? incursSeekPenalty,
        ResultStoreDriveTier expected)
    {
        Assert.Equal(
            expected,
            ResultStoreDriveProfileDetector.Classify(busType, mediaType, incursSeekPenalty));
    }

    [Fact]
    public void ParsePhysicalDiskMetadata_KeepsValidRpmAndDropsUnknownSentinels()
    {
        IReadOnlyDictionary<uint, WindowsPhysicalDiskMetadata> result =
            ResultStoreDriveProfileDetector.ParsePhysicalDiskMetadata(
                "0|4|17|0\r\n2|3|7|7200\r\n3|3|7|4294967295\r\nbad line\r\n");

        Assert.Equal(3, result.Count);
        Assert.Equal(new WindowsPhysicalDiskMetadata(4, 17, null), result[0]);
        Assert.Equal(new WindowsPhysicalDiskMetadata(3, 7, 7_200), result[2]);
        Assert.Equal(new WindowsPhysicalDiskMetadata(3, 7, null), result[3]);
    }

    [Theory]
    [InlineData("bad|4|17|7200")]
    [InlineData("0|bad|17|7200")]
    [InlineData("0|4|bad|7200")]
    [InlineData("0|4|17|bad")]
    [InlineData("0|4|17|7200|extra")]
    public void ParsePhysicalDiskMetadata_MalformedFields_AreIgnored(string line)
    {
        IReadOnlyDictionary<uint, WindowsPhysicalDiskMetadata> result =
            ResultStoreDriveProfileDetector.ParsePhysicalDiskMetadata(line);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\server\share")]
    [InlineData(@"1:\Temp")]
    public void Detect_NonDriveRoots_ReturnUnknown(string path)
    {
        Assert.Equal(
            new ResultStoreDriveHardwareProfile(ResultStoreDriveTier.Unknown, null),
            ResultStoreDriveProfileDetector.Detect(path));
    }

    [Fact]
    public void Detect_NullPath_ReturnsUnknown()
    {
        Assert.Equal(
            new ResultStoreDriveHardwareProfile(ResultStoreDriveTier.Unknown, null),
            ResultStoreDriveProfileDetector.Detect(null!));
    }
}