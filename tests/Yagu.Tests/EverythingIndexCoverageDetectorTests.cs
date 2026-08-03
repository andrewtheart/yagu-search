using Yagu.Services;

namespace Yagu.Tests;

public sealed class EverythingIndexCoverageDetectorTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-everything-coverage-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, bool> _originalFileExists = EverythingIndexCoverageDetector.FileExists;
    private readonly Func<string, string> _originalReadAllText = EverythingIndexCoverageDetector.ReadAllText;
    private readonly Func<string, IEnumerable<string>> _originalReadLines = EverythingIndexCoverageDetector.ReadLines;

    public EverythingIndexCoverageDetectorTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        EverythingIndexCoverageDetector.FileExists = _originalFileExists;
        EverythingIndexCoverageDetector.ReadAllText = _originalReadAllText;
        EverythingIndexCoverageDetector.ReadLines = _originalReadLines;
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_CoversEnabledNtfsRefsAndRecursiveFolderIndexes()
    {
        var config = EverythingIndexCoverageDetector.Parse("""
            [Everything]
            ntfs_volume_paths=C:,E:,F:
            ntfs_volume_includes=1,1,0
            ntfs_volume_roots=,,
            ntfs_volume_include_onlys=,,
            refs_volume_paths=D:
            refs_volume_includes=1
            folders=G:\indexed,H:\one-level
            folder_subfolders=1,0
            """);

        Assert.True(config.Covers(@"C:\Users\andre"));
        Assert.True(config.Covers(@"D:\a\b\c"));
        Assert.True(config.Covers(@"E:\"));
        Assert.True(config.Covers(@"G:\indexed\nested"));
        Assert.False(config.Covers(@"F:\"));            // volume explicitly disabled
        Assert.False(config.Covers(@"H:\one-level"));    // not recursive enough for a Yagu recursive search
        Assert.False(config.Covers(@"I:\"));
    }

    [Fact]
    public void Parse_DisabledIncludesAndNonRecursiveFolders_AreNotCovered()
    {
        var config = EverythingIndexCoverageDetector.Parse("""
            [Everything]
            ntfs_volume_paths=C:,D:
            ntfs_volume_includes=0,1
            folders=E:\one-level
            folder_subfolders=0
            """);

        Assert.False(config.Covers(@"C:\"));
        Assert.True(config.Covers(@"D:\nested"));
        Assert.False(config.Covers(@"E:\one-level"));
        Assert.False(config.Covers("bad\0path"));
    }

    [Fact]
    public void Parse_VolumeIncludeOnly_CoversOnlyConfiguredSubtree()
    {
        var config = EverythingIndexCoverageDetector.Parse("""
            [Everything]
            ntfs_volume_paths=J:
            ntfs_volume_includes=1
            ntfs_volume_include_onlys=projects;K:\\absolute
            """);

        Assert.True(config.Covers(@"J:\projects\Yagu"));
        Assert.True(config.Covers(@"K:\absolute\child"));
        Assert.False(config.Covers(@"J:\Windows"));
        Assert.False(config.Covers(@"J:\"));
    }

    [Fact]
    public void Parse_QuotedCsvPathWithComma_RemainsOneFolder()
    {
        var config = EverythingIndexCoverageDetector.Parse("""
            [Everything]
            folders="K:\a,b",L:\plain
            folder_subfolders=1,1
            """);

        Assert.True(config.Covers(@"K:\a,b\child"));
        Assert.True(config.Covers(@"L:\plain\child"));
        Assert.False(config.Covers(@"K:\a"));
    }

    [Fact]
    public void FindActiveConfigPath_AppDataStubUsesRoamingIni()
    {
        string exeDir = Path.Combine(_sandbox, "install");
        string appData = Path.Combine(_sandbox, "roaming");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(Path.Combine(appData, "Everything"));
        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), "[Everything]\napp_data=1\n");
        string roamingIni = Path.Combine(appData, "Everything", "Everything.ini");
        File.WriteAllText(roamingIni, "[Everything]\nntfs_volume_paths=C:\n");

        Assert.Equal(roamingIni, EverythingIndexCoverageDetector.FindActiveConfigPath(exe, appData));
    }

    [Fact]
    public void FindActiveConfigPath_PortableUsesBesideExeIni()
    {
        string exeDir = Path.Combine(_sandbox, "portable");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        string ini = Path.Combine(exeDir, "Everything.ini");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(ini, "[Everything]\napp_data=0\n");

        Assert.Equal(ini, EverythingIndexCoverageDetector.FindActiveConfigPath(exe, Path.Combine(_sandbox, "none")));
    }

    [Fact]
    public void FindActiveConfigPath_AppDataModeWithoutRoamingIni_ReturnsNull()
    {
        string exeDir = Path.Combine(_sandbox, "appdata-missing");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), "[Everything]\napp_data=1\n");

        Assert.Null(EverythingIndexCoverageDetector.FindActiveConfigPath(exe, Path.Combine(_sandbox, "roaming-missing")));
    }

    [Fact]
    public void FindActiveConfigPath_WithoutBesideIni_FallsBackToRoaming()
    {
        string exeDir = Path.Combine(_sandbox, "roaming-only");
        string appData = Path.Combine(_sandbox, "roaming2");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(Path.Combine(appData, "Everything"));
        string exe = Path.Combine(exeDir, "Everything.exe");
        string roamingIni = Path.Combine(appData, "Everything", "Everything.ini");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(roamingIni, "[Everything]\nntfs_volume_paths=C:\n");

        Assert.Equal(roamingIni, EverythingIndexCoverageDetector.FindActiveConfigPath(exe, appData));
    }

    [Fact]
    public void FindActiveConfigPath_WhenExePathHasNoDirectory_ReturnsNull()
        => Assert.Null(EverythingIndexCoverageDetector.FindActiveConfigPath("Everything.exe", _sandbox));

    [Fact]
    public void FindActiveConfigPath_AppDataIniPathIsDirectory_ReturnsNull()
    {
        string exeDir = Path.Combine(_sandbox, "appdata-dir");
        string appData = Path.Combine(_sandbox, "roaming-dir");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(Path.Combine(appData, "Everything", "Everything.ini"));

        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), "[Everything]\napp_data=1\n");

        Assert.Null(EverythingIndexCoverageDetector.FindActiveConfigPath(exe, appData));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindActiveConfigPath_BlankExePath_ReturnsNull(string exePath)
        => Assert.Null(EverythingIndexCoverageDetector.FindActiveConfigPath(exePath, _sandbox));

    [Fact]
    public void FindUncoveredPaths_ReturnsMissingPathsAndFailsOpenWithoutConfig()
    {
        string exeDir = Path.Combine(_sandbox, "installed");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), """
            [Everything]
            app_data=0
            ntfs_volume_paths=C:,D:
            ntfs_volume_includes=1,1
            """);

        var missing = EverythingIndexCoverageDetector.FindUncoveredPaths(
            [@"C:\Users", @"D:\a\b", @"F:\folder", @"F:\folder", "  "], exe, _sandbox);
        Assert.NotNull(missing);
        Assert.Equal(new[] { @"F:\folder" }, missing);

        Assert.Null(EverythingIndexCoverageDetector.FindUncoveredPaths(
            [@"C:\"], Path.Combine(_sandbox, "missing", "Everything.exe"), _sandbox));
    }

    [Fact]
    public void FindUncoveredPaths_HandlesOtherSectionsAndComments()
    {
        string exeDir = Path.Combine(_sandbox, "section-parse");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), """
            [Other]
            app_data=1
            [Everything]
            ; comment
            ntfs_volume_paths=C:
            ntfs_volume_includes=yes
            """);

        IReadOnlyList<string>? uncovered = EverythingIndexCoverageDetector.FindUncoveredPaths(
            [@"C:\test", @"D:\test"],
            exe,
            _sandbox);

        Assert.NotNull(uncovered);
        Assert.Equal([@"D:\test"], uncovered);
    }

    [Fact]
    public void FindUncoveredPaths_WhenConfigReadThrows_ReturnsNull()
    {
        string exeDir = Path.Combine(_sandbox, "read-failure");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        string ini = Path.Combine(exeDir, "Everything.ini");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(ini, "[Everything]\napp_data=0\n");

        EverythingIndexCoverageDetector.ReadAllText = _ => throw new IOException("boom");

        IReadOnlyList<string>? uncovered = EverythingIndexCoverageDetector.FindUncoveredPaths(
            [@"C:\test"],
            exe,
            _sandbox);

        Assert.Null(uncovered);
    }

    [Fact]
    public void FindConfirmedUncoveredPaths_RunningEverythingTreatsSavedNegativeAsUnknown()
    {
        string exeDir = Path.Combine(_sandbox, "live-config");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), """
            [Everything]
            app_data=0
            ntfs_volume_paths=C:
            ntfs_volume_includes=1
            fat_volume_paths=
            """);

        // The on-disk INI says F is absent, but while Everything is running that may only mean F was
        // added in the UI after startup and the in-memory FAT scan/config has not yet been saved.
        Assert.Empty(EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(
            [@"F:\\"], exe, everythingRunning: true, roamingAppData: _sandbox)!);
        Assert.Equal(new[] { @"F:\\" }, EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(
            [@"F:\\"], exe, everythingRunning: false, roamingAppData: _sandbox));
    }

    [Fact]
    public void FindConfirmedUncoveredPaths_WhenConfigUnknown_PropagatesNull()
    {
        Assert.Null(EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(
            [@"C:\\"],
            Path.Combine(_sandbox, "missing", "Everything.exe"),
            everythingRunning: false,
            roamingAppData: _sandbox));
    }

    [Fact]
    public void FindConfirmedUncoveredPaths_WhenNoUncovered_ReturnsEmpty()
    {
        string exeDir = Path.Combine(_sandbox, "none-uncovered");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(Path.Combine(exeDir, "Everything.ini"), """
            [Everything]
            app_data=0
            ntfs_volume_paths=C:
            ntfs_volume_includes=1
            """);

        IReadOnlyList<string>? uncovered = EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(
            [@"C:\\folder"],
            exe,
            everythingRunning: true,
            roamingAppData: _sandbox);

        Assert.NotNull(uncovered);
        Assert.Empty(uncovered!);
    }

    [Fact]
    public void FindActiveConfigPath_WhenReadingBesideIniThrows_FallsBackToBesideIni()
    {
        string exeDir = Path.Combine(_sandbox, "readlines-throws");
        Directory.CreateDirectory(exeDir);
        string exe = Path.Combine(exeDir, "Everything.exe");
        string ini = Path.Combine(exeDir, "Everything.ini");
        File.WriteAllBytes(exe, []);
        File.WriteAllText(ini, "[Everything]\napp_data=1\n");

        EverythingIndexCoverageDetector.ReadLines = _ => throw new IOException("cannot read");

        Assert.Equal(ini, EverythingIndexCoverageDetector.FindActiveConfigPath(exe, Path.Combine(_sandbox, "roaming-none")));
    }

    [Fact]
    public void FindActiveConfigPath_NullAppDataAndMalformedIniKey_UsesDefaultRoamingCandidate()
    {
        string exe = Path.Combine(_sandbox, "default-roaming", "Everything.exe");
        string besideIni = Path.Combine(Path.GetDirectoryName(exe)!, "Everything.ini");
        string roamingIni = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Everything",
            "Everything.ini");
        EverythingIndexCoverageDetector.FileExists = path => path == besideIni || path == roamingIni;
        EverythingIndexCoverageDetector.ReadLines = _ => ["[Everything]", "=ignored", "other=1", "app_data=1"];

        Assert.Equal(roamingIni, EverythingIndexCoverageDetector.FindActiveConfigPath(exe, roamingAppData: null));
    }

    [Fact]
    public void Parse_NullText_ReturnsEmptyConfiguration()
    {
        EverythingIndexCoverageDetector.Configuration config = EverythingIndexCoverageDetector.Parse(null!);

        Assert.Empty(config.IndexedRoots);
        Assert.False(config.Covers(@"C:\anything"));
    }

    [Fact]
    public void Parse_HandlesIncludeOnlyAbsoluteRelativeAndUnknownBoolValues()
    {
        var config = EverythingIndexCoverageDetector.Parse("""
            [Everything]
            ntfs_volume_paths=M:
            ntfs_volume_includes=1
            ntfs_volume_roots=M:\root
            ntfs_volume_include_onlys=child|N:\fixed
            folders=P:\single,Q:\recursive
            folder_subfolders=no,
            """);

        Assert.True(config.Covers(@"M:\root\child\nested"));
        Assert.True(config.Covers(@"N:\fixed\nested"));
        Assert.False(config.Covers(@"P:\single"));
        Assert.True(config.Covers(@"Q:\recursive\deep"));
    }

    [Fact]
    public void Parse_IgnoresEverythingLinesWithoutEquals()
    {
        var config = EverythingIndexCoverageDetector.Parse("""
            [Everything]
            this_line_has_no_equals
            folders=Z:\indexed
            folder_subfolders=1
            """);

        Assert.True(config.Covers(@"Z:\indexed\child"));
    }

    [Fact]
    public void Configuration_Covers_RejectsSiblingWithSamePrefix()
    {
        var config = new EverythingIndexCoverageDetector.Configuration([
            new EverythingIndexCoverageDetector.IndexedRoot(@"R:\base", Recursive: true)
        ]);

        Assert.False(config.Covers(@"R:\base2\child"));
        Assert.True(config.Covers(@"R:\base\child"));
    }

    [Fact]
    public void Configuration_Covers_SkipsInvalidRootAndRejectsWhitespaceTarget()
    {
        var config = new EverythingIndexCoverageDetector.Configuration([
            new EverythingIndexCoverageDetector.IndexedRoot("bad\0root", Recursive: true),
            new EverythingIndexCoverageDetector.IndexedRoot(@"S:\ok", Recursive: true)
        ]);

        Assert.False(config.Covers("   "));
        Assert.True(config.Covers(@"S:\ok\nested"));
    }

    [Fact]
    public void SplitCsv_NullValue_IsTreatedAsSingleEmptyField()
        => Assert.Equal([string.Empty], EverythingIndexCoverageDetector.SplitCsv(null!));

    [Fact]
    public void SplitCsv_HandlesQuotedCommasAndEscapedQuotes()
    {
        List<string> parts = EverythingIndexCoverageDetector.SplitCsv("\"A,1\",\"B\"\"2\",C,,");

        Assert.Equal(["A,1", "B\"2", "C", string.Empty, string.Empty], parts);
    }

    [Fact]
    public void SplitCsv_EmptyValue_ReturnsSingleEmptyField()
        => Assert.Equal([string.Empty], EverythingIndexCoverageDetector.SplitCsv(string.Empty));

    [Fact]
    public void Configurator_NormalizeRootVolumes_UsesRootDriveAndDeduplicates()
    {
        Assert.Equal(new[] { "D:", "F:" }, EverythingIndexConfigurator.NormalizeRootVolumes(
            [@"D:\\a\\b", @"d:\\other", @"F:\\"]));
    }
}
