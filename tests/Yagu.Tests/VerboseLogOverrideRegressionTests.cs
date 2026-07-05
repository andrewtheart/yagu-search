using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Pins the install-time verbose-logging override: the Yagu installer's <c>/VERBOSELOG</c> switch
/// writes <c>HKCU\Software\Yagu\LogLevelOverride = "Verbose"</c>, and <see cref="LogService"/> reads
/// it at startup as a sticky FILE-level floor so verbose logging is active from the very first launch
/// (before the settings UI is reachable behind startup modals) and cannot be silently lowered by the
/// view-model re-applying the saved level during window construction.
/// </summary>
public sealed class VerboseLogOverrideRegressionTests
{
    [Theory]
    [InlineData("verbose", LogLevel.Verbose)]
    [InlineData("Verbose", LogLevel.Verbose)]
    [InlineData("  VERBOSE  ", LogLevel.Verbose)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("none", LogLevel.None)]
    public void ParseLogLevelToken_RecognizedTokens_MapToLevel(string token, LogLevel expected)
        => Assert.Equal(expected, LogService.ParseLogLevelToken(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loud")]
    [InlineData("3")]
    public void ParseLogLevelToken_UnrecognizedOrBlank_ReturnsNull(string? token)
        => Assert.Null(LogService.ParseLogLevelToken(token));

    [Fact]
    public void InstallerFloor_RaisesCurrentFileLevel()
    {
        using var svc = NewLogService();
        svc.FileLevel = LogLevel.Warning;

        svc.InstallerFileFloor = LogLevel.Verbose;

        Assert.Equal(LogLevel.Verbose, svc.FileLevel);
    }

    [Fact]
    public void InstallerFloor_PreventsLoweringFileLevelBelowFloor()
    {
        using var svc = NewLogService();
        svc.InstallerFileFloor = LogLevel.Verbose;

        svc.FileLevel = LogLevel.Warning; // try to lower
        Assert.Equal(LogLevel.Verbose, svc.FileLevel);

        svc.FileLevel = LogLevel.None; // try to disable
        Assert.Equal(LogLevel.Verbose, svc.FileLevel);
    }

    [Fact]
    public void InstallerFloor_AllowsRaisingAboveFloorIsNoOp_ButNeverLowers()
    {
        using var svc = NewLogService();
        svc.InstallerFileFloor = LogLevel.Info;

        // Setting a MORE verbose level than the floor is honored as-is.
        svc.FileLevel = LogLevel.Verbose;
        Assert.Equal(LogLevel.Verbose, svc.FileLevel);

        // Setting a LESS verbose level is clamped back up to the floor.
        svc.FileLevel = LogLevel.Critical;
        Assert.Equal(LogLevel.Info, svc.FileLevel);
    }

    [Fact]
    public void NoFloor_FileLevelBehavesNormally()
    {
        using var svc = NewLogService();
        Assert.Null(svc.InstallerFileFloor);

        svc.FileLevel = LogLevel.Warning;
        Assert.Equal(LogLevel.Warning, svc.FileLevel);

        svc.FileLevel = LogLevel.None;
        Assert.Equal(LogLevel.None, svc.FileLevel);
    }

    [Fact]
    public void ReadInstallerLogLevelOverride_DoesNotThrow()
    {
        // Reads HKCU\Software\Yagu\LogLevelOverride; absent/unreadable must yield null, never throw.
        var ex = Record.Exception(() => LogService.ReadInstallerLogLevelOverride());
        Assert.Null(ex);
    }

    [Fact]
    public void LogService_ExposesInstallerOverrideApiAndRegistryLocation()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "LogService.cs"));

        Assert.Contains("public static void InitFromSettings(LogLevel fileLevel, LogLevel consoleLevel)", source);
        Assert.Contains("ReadInstallerLogLevelOverride()", source);
        Assert.Contains("Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InstallerRegistrySubKey)", source);
        Assert.Contains("InstallerRegistrySubKey = @\"Software\\Yagu\"", source);
        Assert.Contains("LogLevelOverrideValueName = \"LogLevelOverride\"", source);
        // The override is a sticky floor, applied before the saved level so it cannot be lowered.
        Assert.Contains("Instance.InstallerFileFloor = overrideLevel;", source);
        Assert.Contains("_installerFileFloor is { } floor && value < floor ? floor : value", source);
    }

    [Fact]
    public void Startup_GuiAndCli_InitializeViaInitFromSettings()
    {
        string app = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "App.xaml.cs"));
        string cli = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("LogService.InitFromSettings((LogLevel)settings.LogLevelIndex, (LogLevel)settings.ConsoleLogLevelIndex);", app);
        Assert.Contains("LogService.InitFromSettings((LogLevel)settings.LogLevelIndex, LogLevel.Critical);", cli);
    }

    [Fact]
    public void Installer_VerboseLogFlag_WritesOrClearsRegistryOverride()
    {
        string iss = File.ReadAllText(Path.Combine(FindRepoRoot(), "installer", "yagu-installer.iss"));

        Assert.Contains("'/VERBOSELOG'", iss);
        Assert.Contains("RegWriteStringValue(HKCU, 'Software\\Yagu', 'LogLevelOverride', 'Verbose')", iss);
        Assert.Contains("RegDeleteValue(HKCU, 'Software\\Yagu', 'LogLevelOverride')", iss);
        Assert.Contains("ApplyLogLevelOverride();", iss);
    }

    private static LogService NewLogService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yagu-verboseoverride-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new LogService(Path.Combine(dir, "test.log"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
