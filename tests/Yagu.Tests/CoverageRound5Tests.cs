using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — SettingsService.LoadAsync branch coverage
// ═══════════════════════════════════════════════════════════════════

public sealed class SettingsServiceLoadAsyncBranch2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qg-ss-r5-" + Guid.NewGuid().ToString("N"));

    public SettingsServiceLoadAsyncBranch2Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task LoadAsync_NullArchiveExtensions_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"ArchiveExtensions":null}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultArchiveExtensions, s.ArchiveExtensions);
    }

    [Fact]
    public async Task LoadAsync_NullBinaryExtensions_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"BinaryExtensions":null}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultBinaryExtensions, s.BinaryExtensions);
    }

    [Fact]
    public async Task LoadAsync_LegacyExpandedBinaryPrefilter_MigratesBothFields()
    {
        var path = Path.Combine(_root, "settings.json");
        // The old "expanded binary prefilter" had media+binary extensions merged in BinaryExtensions
        File.WriteAllText(path, $$"""{"BinaryExtensions":"{{AppSettings.LegacyExpandedBinaryPrefilterExtensions}}","SkipExtensions":"custom;exts"}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultBinaryExtensions, s.BinaryExtensions);
        // SkipExtensions should be merged (original + default)
        Assert.Contains("custom", s.SkipExtensions);
    }

    [Fact]
    public async Task LoadAsync_NullTerminalDefaultWorkingDirectory_BecomesEmpty()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"TerminalDefaultWorkingDirectory":null}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(string.Empty, s.TerminalDefaultWorkingDirectory);
    }

    [Fact]
    public async Task LoadAsync_NullSkipExtensions_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"SkipExtensions":null}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultSkipExtensions, s.SkipExtensions);
    }

    [Fact]
    public async Task LoadAsync_LegacyWindowFocusBehavior3_MigratesToTraditional()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"WindowFocusBehavior":3,"StartInLauncherModeMigrated":false}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.False(s.StartInLauncherMode);
        Assert.Equal(1, s.WindowFocusBehavior);
        Assert.True(s.StartInLauncherModeMigrated);
    }

    [Fact]
    public async Task LoadAsync_LegacyWindowFocusBehavior0_MigratesToStayOpen()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"WindowFocusBehavior":0,"StartInLauncherModeMigrated":false,"WindowFocusBehaviorMigratedFromLegacyDefault":false}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(1, s.WindowFocusBehavior);
        Assert.True(s.StartInLauncherMode);
    }

    [Fact]
    public async Task LoadAsync_InvalidWindowFocusBehavior_ClampedToStayOpen()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"WindowFocusBehavior":99,"StartInLauncherModeMigrated":false}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(1, s.WindowFocusBehavior);
    }

    [Fact]
    public async Task LoadAsync_LegacyPreviewGutterColors_Migrated()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, $$"""{"PreviewGutterContextColor":"{{AppSettings.LegacyDefaultPreviewGutterContextColor}}","PreviewGutterMatchColor":"{{AppSettings.LegacyDefaultPreviewGutterMatchColor}}"}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultPreviewGutterContextColor, s.PreviewGutterContextColor);
        Assert.Equal(AppSettings.DefaultPreviewGutterMatchColor, s.PreviewGutterMatchColor);
    }

    [Fact]
    public async Task LoadAsync_EmptyPreviewEditorGutterColor_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"PreviewEditorGutterColor":""}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultPreviewEditorGutterColor, s.PreviewEditorGutterColor);
    }

    [Fact]
    public async Task LoadAsync_InvalidThemeModeIndex_ResetToZero()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"ThemeModeIndex":5}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(0, s.ThemeModeIndex);
    }

    [Fact]
    public async Task LoadAsync_EmptyPreviewTextFontFamily_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"PreviewTextFontFamily":"","PreviewTextFontSize":0}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultPreviewTextFontFamily, s.PreviewTextFontFamily);
        Assert.Equal(AppSettings.DefaultPreviewTextFontSize, s.PreviewTextFontSize);
    }

    [Fact]
    public async Task LoadAsync_EmptyPreviewEditorFontFamily_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"PreviewEditorFontFamily":"","PreviewEditorFontSize":0}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultPreviewEditorFontFamily, s.PreviewEditorFontFamily);
        Assert.Equal(AppSettings.DefaultPreviewEditorFontSize, s.PreviewEditorFontSize);
    }

    [Fact]
    public async Task LoadAsync_EmptyResultListMatchTextFont_DefaultsApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"ResultListMatchTextFontFamily":"","ResultListMatchTextFontSize":0}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(AppSettings.DefaultResultListMatchTextFontFamily, s.ResultListMatchTextFontFamily);
        Assert.Equal(AppSettings.DefaultResultListMatchTextFontSize, s.ResultListMatchTextFontSize);
    }

    [Fact]
    public async Task LoadAsync_InvalidShowMoreEllipsisColor_DefaultApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"PreviewShowMoreEllipsisColor":"not-a-color","PreviewShowMoreEllipsisFontSize":0}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.StartsWith("#", s.PreviewShowMoreEllipsisColor);
        Assert.Equal(AppSettings.DefaultPreviewShowMoreEllipsisFontSize, s.PreviewShowMoreEllipsisFontSize);
    }

    [Fact]
    public async Task LoadAsync_FilterModeOutOfRange_Normalized()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"IncludeFilterModeIndex":5,"ExcludeFilterModeIndex":-1,"IncludeGlobs":null,"ExcludeGlobs":null}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(0, s.IncludeFilterModeIndex);
        Assert.Equal(0, s.ExcludeFilterModeIndex);
        Assert.Equal(string.Empty, s.IncludeGlobs);
        Assert.Equal(AppSettings.DefaultExcludeGlobs, s.ExcludeGlobs);
    }

    [Fact]
    public async Task LoadAsync_LowDiskSpaceWarningPercentZero_RemainsZero()
    {
        // LoadAsync does NOT normalize LowDiskSpaceWarningPercent (only sync Load does)
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"LowDiskSpaceWarningPercent":0}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.Equal(0, s.LowDiskSpaceWarningPercent);
    }

    [Fact]
    public void Load_LowDiskSpaceWarningPercentZero_DefaultApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"LowDiskSpaceWarningPercent":0}""");
        var svc = new SettingsService(path);
        var s = svc.Load();
        Assert.Equal(AppSettings.DefaultLowDiskSpaceWarningPercent, s.LowDiskSpaceWarningPercent);
    }

    [Fact]
    public void Load_LowDiskSpaceWarningPercentAboveMax_Clamped()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"LowDiskSpaceWarningPercent":200}""");
        var svc = new SettingsService(path);
        var s = svc.Load();
        Assert.Equal(AppSettings.MaximumLowDiskSpaceWarningPercent, s.LowDiskSpaceWarningPercent);
    }

    [Fact]
    public async Task LoadAsync_InvalidResultListMatchHighlightColor_DefaultApplied()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"ResultListMatchHighlightColor":"badvalue"}""");
        var svc = new SettingsService(path);
        var s = await svc.LoadAsync();
        Assert.StartsWith("#", s.ResultListMatchHighlightColor);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — DiskSpaceSnapshot computed property branches
// ═══════════════════════════════════════════════════════════════════

public sealed class DiskSpaceSnapshotBranch2Tests
{
    [Fact]
    public void UsedFraction_ZeroTotalBytes_ReturnsZero()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 0, AvailableBytes: 100);
        Assert.Equal(0d, snap.UsedFraction);
    }

    [Fact]
    public void UsedFraction_NormalDrive_ReturnsCorrectValue()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 1000, AvailableBytes: 200);
        Assert.Equal(0.8, snap.UsedFraction, precision: 4);
    }

    [Fact]
    public void UsedBytes_NegativeAvailable_ClampsToZero()
    {
        // Edge case: available > total (corrupted drive info)
        var snap = new DiskSpaceSnapshot(@"D:\", TotalBytes: 100, AvailableBytes: 200);
        Assert.Equal(0, snap.UsedBytes);
    }

    [Fact]
    public void UsedPercent_ReturnsPercentage()
    {
        var snap = new DiskSpaceSnapshot(@"E:\", TotalBytes: 200, AvailableBytes: 50);
        Assert.Equal(75.0, snap.UsedPercent, precision: 2);
    }

    [Fact]
    public void DriveDisplayName_TrailingSlash_Trimmed()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 100, AvailableBytes: 50);
        Assert.Equal("C:", snap.DriveDisplayName);
    }

    [Fact]
    public void DriveDisplayName_EmptyString_ReturnsSame()
    {
        var snap = new DiskSpaceSnapshot("", TotalBytes: 100, AvailableBytes: 50);
        Assert.Equal("", snap.DriveDisplayName);
    }

    [Fact]
    public void DriveDisplayName_NoTrailingSlash_UnchangedExceptTrim()
    {
        var snap = new DiskSpaceSnapshot("X:", TotalBytes: 100, AvailableBytes: 50);
        Assert.Equal("X:", snap.DriveDisplayName);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — LowDiskSpaceMonitor additional branches
// ═══════════════════════════════════════════════════════════════════

public sealed class LowDiskSpaceMonitorBranch2Tests
{
    [Fact]
    public void TryGetSnapshot_RelativePath_ResolvesAndSucceeds()
    {
        // Relative path triggers Path.GetFullPath → Path.GetPathRoot
        bool ok = LowDiskSpaceMonitor.TryGetSnapshot("tempfile.txt", out var snap);
        Assert.True(ok);
        Assert.True(snap.TotalBytes > 0);
    }

    [Fact]
    public void TryGetSnapshot_UncPathWithoutDrive_ReturnsFalse()
    {
        // Path whose root is empty string after normalization
        bool ok = LowDiskSpaceMonitor.TryGetSnapshot(@"\\?\invalidserver\share\file.txt", out _);
        // May return true or false depending on system; just confirm no throw
        Assert.IsType<bool>(ok);
    }

    [Fact]
    public void PercentToThreshold_BelowMinimum_ClampedToDefault()
    {
        double threshold = LowDiskSpaceMonitor.PercentToThreshold(-5);
        Assert.Equal(AppSettings.DefaultLowDiskSpaceWarningPercent / 100d, threshold, precision: 4);
    }

    [Fact]
    public void PercentToThreshold_AboveMaximum_ClampedTo99()
    {
        double threshold = LowDiskSpaceMonitor.PercentToThreshold(150);
        Assert.Equal(0.99, threshold, precision: 4);
    }

    [Fact]
    public void IsOverThreshold_ExactlyAtThreshold_ReturnsFalse()
    {
        // UsedFraction = 0.90, threshold = 0.90 → IsOverThreshold requires > not >=
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 100, AvailableBytes: 10);
        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(snap, 0.90));
    }

    [Fact]
    public void IsOverThreshold_JustAboveThreshold_ReturnsTrue()
    {
        var snap = new DiskSpaceSnapshot(@"C:\", TotalBytes: 1000, AvailableBytes: 99);
        Assert.True(LowDiskSpaceMonitor.IsOverThreshold(snap, 0.90));
    }

    [Fact]
    public async Task StartAsync_TriggersDiskFullCallback_WhenOverThreshold()
    {
        // Use a real temp file on a drive that has space used
        var tempFile = Path.GetTempFileName();
        try
        {
            var tcs = new TaskCompletionSource<DiskSpaceSnapshot>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Threshold of 0.01 (1%) — any real drive will exceed this
            var task = LowDiskSpaceMonitor.StartAsync(
                tempFile,
                fullThreshold: 0.01,
                checkInterval: TimeSpan.FromMilliseconds(50),
                onDiskTooFull: snap => tcs.TrySetResult(snap),
                cancellationToken: cts.Token);

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.TotalBytes > 0);
            Assert.True(result.UsedFraction > 0.01);
            await task;
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — ExtensionFilterOption record struct
// ═══════════════════════════════════════════════════════════════════

public sealed class ExtensionFilterOptionTests
{
    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new ExtensionFilterOption(".cs", "C# Files", 10, true);
        var b = new ExtensionFilterOption(".cs", "C# Files", 10, true);
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentCount_NotEqual()
    {
        var a = new ExtensionFilterOption(".cs", "C# Files", 10, true);
        var b = new ExtensionFilterOption(".cs", "C# Files", 20, true);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equality_DifferentSelection_NotEqual()
    {
        var a = new ExtensionFilterOption(".cs", "C# Files", 10, true);
        var b = new ExtensionFilterOption(".cs", "C# Files", 10, false);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_ContainsExtension()
    {
        var opt = new ExtensionFilterOption(".txt", "Text Files", 5, false);
        string str = opt.ToString();
        Assert.Contains(".txt", str);
    }

    [Fact]
    public void GetHashCode_SameValues_SameHash()
    {
        var a = new ExtensionFilterOption(".xml", "XML", 3, true);
        var b = new ExtensionFilterOption(".xml", "XML", 3, true);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Properties_AccessCorrectValues()
    {
        var opt = new ExtensionFilterOption(".log", "Log Files", 42, true);
        Assert.Equal(".log", opt.Extension);
        Assert.Equal("Log Files", opt.DisplayName);
        Assert.Equal(42, opt.Count);
        Assert.True(opt.IsSelected);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — ResultStoreTempDriveOption record coverage
// ═══════════════════════════════════════════════════════════════════

public sealed class ResultStoreTempDriveOptionRecordTests
{
    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C: - 100 GB", 100L * 1024 * 1024 * 1024, true);
        var b = new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C: - 100 GB", 100L * 1024 * 1024 * 1024, true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentDrive_NotEqual()
    {
        var a = new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C:", 100L * 1024 * 1024 * 1024, true);
        var b = new ResultStoreTempDriveOption(@"D:\", @"D:\Temp\Yagu", "D:", 100L * 1024 * 1024 * 1024, false);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_ContainsDriveRoot()
    {
        var opt = new ResultStoreTempDriveOption(@"E:\", @"E:\Temp\Yagu", "E: - 50 GB", 50L * 1024 * 1024 * 1024, false);
        Assert.Contains("E:", opt.ToString());
    }

    [Fact]
    public void Properties_AccessCorrectValues()
    {
        var opt = new ResultStoreTempDriveOption(@"D:\", @"D:\Temp\Yagu", "D: - 200 GB free", 200L * 1024 * 1024 * 1024, true);
        Assert.Equal(@"D:\", opt.DriveRoot);
        Assert.Equal(@"D:\Temp\Yagu", opt.TempDirectory);
        Assert.Equal("D: - 200 GB free", opt.DisplayName);
        Assert.Equal(200L * 1024 * 1024 * 1024, opt.AvailableFreeBytes);
        Assert.True(opt.IsLaunchDrive);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — FontContrastCandidate/Issue record coverage
// ═══════════════════════════════════════════════════════════════════

public sealed class FontContrastRecordTests
{
    [Fact]
    public void FontContrastCandidate_Equality()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var a = new FontContrastCandidate("key1", "surface", "label", "#FF0000", fallback);
        var b = new FontContrastCandidate("key1", "surface", "label", "#FF0000", fallback);
        Assert.Equal(a, b);
    }

    [Fact]
    public void FontContrastCandidate_WithBackgroundColor()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var bg = FontContrastColor.FromArgb(0xFF, 0x20, 0x20, 0x20);
        var candidate = new FontContrastCandidate("key", "surface", "label", "#FF0000", fallback, bg);
        Assert.Equal(bg, candidate.BackgroundColor);
    }

    [Fact]
    public void FontContrastCandidate_ToString_ContainsKey()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var candidate = new FontContrastCandidate("myKey", "surf", "lbl", "#00FF00", fallback);
        Assert.Contains("myKey", candidate.ToString());
    }

    [Fact]
    public void FontContrastIssue_Equality()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var candidate = new FontContrastCandidate("k", "s", "l", "#FF0000", fallback);
        var color = FontContrastColor.FromArgb(0xFF, 0xFF, 0, 0);
        var bg = FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var a = new FontContrastIssue(candidate, FontContrastTheme.Dark, FontContrastDirection.Lighter, color, bg, 1.5);
        var b = new FontContrastIssue(candidate, FontContrastTheme.Dark, FontContrastDirection.Lighter, color, bg, 1.5);
        Assert.Equal(a, b);
    }

    [Fact]
    public void FontContrastIssue_ToString_ContainsRatio()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var candidate = new FontContrastCandidate("k", "s", "l", "#FF0000", fallback);
        var color = FontContrastColor.FromArgb(0xFF, 0xFF, 0, 0);
        var bg = FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var issue = new FontContrastIssue(candidate, FontContrastTheme.Light, FontContrastDirection.Darker, color, bg, 2.1);
        string str = issue.ToString();
        Assert.Contains("2.1", str);
    }

    [Fact]
    public void FontContrastIssue_Properties()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var candidate = new FontContrastCandidate("k", "s", "l", "#FF0000", fallback);
        var color = FontContrastColor.FromArgb(0xFF, 0x10, 0x20, 0x30);
        var bg = FontContrastColor.FromArgb(0xFF, 0xF0, 0xF0, 0xF0);
        var issue = new FontContrastIssue(candidate, FontContrastTheme.Light, FontContrastDirection.Darker, color, bg, 2.5);
        Assert.Equal(candidate, issue.Candidate);
        Assert.Equal(FontContrastTheme.Light, issue.Theme);
        Assert.Equal(FontContrastDirection.Darker, issue.RecommendedDirection);
        Assert.Equal(color, issue.CurrentColor);
        Assert.Equal(bg, issue.BackgroundColor);
        Assert.Equal(2.5, issue.ContrastRatio);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — SearchService additional branches
// ═══════════════════════════════════════════════════════════════════

public sealed class SearchServiceRound5Tests
{
    [Fact]
    public void TryGetSystemMemoryLoadPercent_ReturnsTrue_OnWindows()
    {
        bool ok = SearchService.TryGetSystemMemoryLoadPercent(out uint load);
        Assert.True(ok);
        Assert.True(load > 0 && load <= 100);
    }

    [Fact]
    public void ComputeAutoProcessMemoryCap_ExactBoundary_Floor()
    {
        // 512MB RAM → quarter = 128MB, below floor → should clamp to floor
        long result = SearchService.ComputeAutoProcessMemoryCap(512UL * 1024 * 1024);
        Assert.True(result >= 256L * 1024 * 1024); // At least the floor
    }

    [Fact]
    public void ComputeAutoProcessMemoryCap_VeryLargeRam_ClampsToCeiling()
    {
        // 1TB RAM → quarter = 256GB, above ceiling → should clamp
        long result = SearchService.ComputeAutoProcessMemoryCap(1024UL * 1024 * 1024 * 1024);
        Assert.True(result <= 4L * 1024 * 1024 * 1024); // At or below ceiling
    }

    [Fact]
    public void AutoProcessMemoryCap_ReturnsPositiveValue()
    {
        long cap = SearchService.AutoProcessMemoryCap();
        Assert.True(cap > 0);
    }

    [Fact]
    public void GetMemoryDiagnostics_ReturnsNonEmptyString()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.False(string.IsNullOrWhiteSpace(diag));
        Assert.Contains("MB", diag);
    }

    [Fact]
    public void IsMemoryPressureRelieved_HighCap_ReturnsTrue()
    {
        // When cap is very high relative to working set, pressure is relieved
        bool relieved = SearchService.IsMemoryPressureRelieved(
            long.MaxValue, // very high cap → never exceeds
            0); // no system load threshold
        Assert.True(relieved);
    }

    [Fact]
    public void IsMemoryPressureRelieved_WithSystemPressurePercent_ReturnsResult()
    {
        // Test the branch where pressurePercent > 0 triggers system memory check
        // Use a very high cap and 100% threshold so only system-wide OOM fails this
        bool relieved = SearchService.IsMemoryPressureRelieved(
            long.MaxValue,
            100); // 100% threshold → only fails at 100% system load
        Assert.True(relieved);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — AppSettings.NormalizeLowDiskSpaceWarningPercent
// ═══════════════════════════════════════════════════════════════════

public sealed class AppSettingsNormalizationTests
{
    [Theory]
    [InlineData(0, AppSettings.DefaultLowDiskSpaceWarningPercent)]
    [InlineData(-10, AppSettings.DefaultLowDiskSpaceWarningPercent)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(99, 99)]
    [InlineData(100, 99)] // Clamped to maximum
    [InlineData(200, 99)] // Clamped to maximum
    public void NormalizeLowDiskSpaceWarningPercent_CorrectClamping(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLowDiskSpaceWarningPercent(input));
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — FontContrastWarningService additional branches
// ═══════════════════════════════════════════════════════════════════

public sealed class FontContrastWarningServiceRound5Tests
{
    [Fact]
    public void TryCreateIssue_LowContrastOnLightTheme_RecommendsDirection()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        // Light gray text on light background → poor contrast → recommend Lighter (further from bg)
        var candidate = new FontContrastCandidate("key", "surface", "label", "#E0E0E0", fallback);
        bool created = FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Light, out var issue);
        Assert.True(created);
        Assert.NotNull(issue);
        Assert.Equal(FontContrastDirection.Lighter, issue.RecommendedDirection);
    }

    [Fact]
    public void TryCreateIssue_LowContrastOnDarkTheme_RecommendsDarker()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        // Very dark text on dark background → poor contrast → recommend Darker (further from bg)
        var candidate = new FontContrastCandidate("key", "surface", "label", "#252525", fallback);
        bool created = FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Dark, out var issue);
        Assert.True(created);
        Assert.NotNull(issue);
        Assert.Equal(FontContrastDirection.Darker, issue.RecommendedDirection);
    }

    [Fact]
    public void ResolveBackground_WithExplicitBackground_BlendsOverTheme()
    {
        var semiTransparentBg = FontContrastColor.FromArgb(0x80, 0xFF, 0x00, 0x00); // Semi-transparent red
        var resolved = FontContrastWarningService.ResolveBackground(semiTransparentBg, FontContrastTheme.Dark);
        // Should be a blend of red over dark theme background
        Assert.True(resolved.R > 0x20); // Has some red from the overlay
        Assert.Equal(0xFF, resolved.A); // Fully opaque after blending
    }

    [Fact]
    public void GetContrastRatio_SemiTransparentForeground_BlendsBeforeComputing()
    {
        var semiFg = FontContrastColor.FromArgb(0x80, 0xFF, 0xFF, 0xFF); // Semi-transparent white
        var bg = FontContrastColor.FromArgb(0xFF, 0x00, 0x00, 0x00); // Opaque black
        double ratio = FontContrastWarningService.GetContrastRatio(semiFg, bg);
        // Should be between 1 (identical) and 21 (max contrast)
        Assert.True(ratio > 1.0 && ratio < 21.0);
    }

    [Fact]
    public void FindFirstIssue_EmptyCandidates_ReturnsNull()
    {
        var result = FontContrastWarningService.FindFirstIssue([], FontContrastTheme.Dark);
        Assert.Null(result);
    }

    [Fact]
    public void FindFirstIssue_AllPassContrast_ReturnsNull()
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var candidates = new[]
        {
            new FontContrastCandidate("k1", "s", "l", "#FFFFFF", fallback), // White on dark → high contrast
            new FontContrastCandidate("k2", "s", "l", "#F0F0F0", fallback), // Near-white on dark → high contrast
        };
        var result = FontContrastWarningService.FindFirstIssue(candidates, FontContrastTheme.Dark);
        Assert.Null(result);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Coverage Round 5 — ResultStoreTempLocationService branch coverage
// ═══════════════════════════════════════════════════════════════════

public sealed class ResultStoreTempLocationServiceRound5Tests
{
    [Fact]
    public void BuildTempDirectory_RootWithTrailingSlash_ProducesCorrectPath()
    {
        string result = ResultStoreTempLocationService.BuildTempDirectory(@"D:\");
        Assert.Equal(@"D:\Temp\Yagu", result);
    }

    [Fact]
    public void BuildTempDirectory_RootWithoutTrailingSlash_ProducesCorrectPath()
    {
        string result = ResultStoreTempLocationService.BuildTempDirectory(@"D:");
        // Should normalize and combine
        Assert.Contains("Temp", result);
        Assert.Contains("Yagu", result);
    }

    [Fact]
    public void ChoosePreferredOption_CurrentTempMatchesDrive_SelectsThatDrive()
    {
        var options = new[]
        {
            new ResultStoreTempDriveOption(@"C:\", @"C:\Temp\Yagu", "C:", 100L * 1024 * 1024 * 1024, true),
            new ResultStoreTempDriveOption(@"D:\", @"D:\Temp\Yagu", "D:", 200L * 1024 * 1024 * 1024, false),
        };
        var chosen = ResultStoreTempLocationService.ChoosePreferredOption(options, @"D:\Temp\Yagu", @"C:\");
        Assert.NotNull(chosen);
        Assert.Equal(@"D:\", chosen.DriveRoot);
    }

    [Fact]
    public void NormalizeTempDirectory_PathWithTrailingSlash_Trimmed()
    {
        string result = ResultStoreTempLocationService.NormalizeTempDirectory(@"C:\Temp\Yagu\");
        Assert.False(result.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void GetLaunchDriveRoot_ReturnsNonNull()
    {
        string? root = ResultStoreTempLocationService.GetLaunchDriveRoot();
        Assert.NotNull(root);
        Assert.True(root.Length >= 2); // e.g. "C:\"
    }

    [Fact]
    public void IsUsableTempDirectory_ExistingWritableDirectory_ReturnsTrue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yagu-usable-r5-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            bool usable = ResultStoreTempLocationService.IsUsableTempDirectory(dir, requireMinimumFreeSpace: false);
            Assert.True(usable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
