using System.Text.Json;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class SettingsServiceExtendedCoverageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsServiceExtendedCoverageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yagu_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsDefaults()
    {
        var service = new SettingsService(Path.Combine(_tempDir, "nonexistent.json"));
        var settings = service.Load();
        Assert.NotNull(settings);
        Assert.Equal(3, settings.ContextLines);
        Assert.Equal(AppSettings.DefaultExcludeGlobs, settings.ExcludeGlobs);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var service = new SettingsService(_settingsPath);
        var settings = new AppSettings
        {
            LastDirectory = @"C:\test",
            CaseSensitive = true,
            ContextLines = 5,
        };
        service.Save(settings);

        var loaded = service.Load();
        Assert.Equal(@"C:\test", loaded.LastDirectory);
        Assert.Equal(5, loaded.ContextLines);
        // CaseSensitive is [JsonIgnore] so should NOT persist
        Assert.False(loaded.CaseSensitive);
    }

    [Fact]
    public async Task SaveAsyncAndLoadAsync_RoundTrips()
    {
        var service = new SettingsService(_settingsPath);
        var settings = new AppSettings
        {
            LastDirectory = @"D:\projects",
            ContextLines = 7,
            EditorCommand = "code -g {file}:{line}",
        };
        await service.SaveAsync(settings);

        var loaded = await service.LoadAsync();
        Assert.Equal(@"D:\projects", loaded.LastDirectory);
        Assert.Equal(7, loaded.ContextLines);
        Assert.Equal("code -g {file}:{line}", loaded.EditorCommand);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "{ this is not valid json !@#$");
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.NotNull(settings);
        Assert.Equal(3, settings.ContextLines); // default
    }

    [Fact]
    public void Load_MaxResultsExceedsCeiling_ClampsToCeiling()
    {
        var json = JsonSerializer.Serialize(new { MaxResults = 999_999 });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.True(settings.MaxResults <= SearchOptions.MaxResultsCeiling);
    }

    [Fact]
    public void Load_NullSkipExtensions_SetsDefault()
    {
        var json = JsonSerializer.Serialize(new { SkipExtensions = (string?)null });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultSkipExtensions, settings.SkipExtensions);
    }

    [Fact]
    public void Load_NullArchiveExtensions_SetsDefault()
    {
        var json = JsonSerializer.Serialize(new { ArchiveExtensions = (string?)null });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultArchiveExtensions, settings.ArchiveExtensions);
    }

    [Fact]
    public void Load_LegacySkipExtensions_Migrated()
    {
        var json = JsonSerializer.Serialize(new { SkipExtensions = AppSettings.LegacyDefaultSkipExtensions });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultSkipExtensions, settings.SkipExtensions);
    }

    [Fact]
    public void Load_LegacyExpandedBinaryPrefilter_Migrated()
    {
        var json = JsonSerializer.Serialize(new
        {
            BinaryExtensions = AppSettings.LegacyExpandedBinaryPrefilterExtensions,
            SkipExtensions = "png;jpg"
        });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultBinaryExtensions, settings.BinaryExtensions);
        // SkipExtensions should be merged with default
        Assert.Contains("png", settings.SkipExtensions);
    }

    [Fact]
    public void Load_NullBinaryExtensions_SetsDefault()
    {
        var json = JsonSerializer.Serialize(new { BinaryExtensions = (string?)null });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultBinaryExtensions, settings.BinaryExtensions);
    }

    [Fact]
    public void Load_InvalidThemeMode_NormalizedToZero()
    {
        var json = JsonSerializer.Serialize(new { ThemeModeIndex = 99 });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(0, settings.ThemeModeIndex);
    }

    [Fact]
    public void Load_InvalidFilterModeIndex_NormalizedToZero()
    {
        var json = JsonSerializer.Serialize(new { IncludeFilterModeIndex = 5, ExcludeFilterModeIndex = -1 });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(0, settings.IncludeFilterModeIndex);
        Assert.Equal(0, settings.ExcludeFilterModeIndex);
    }

    [Fact]
    public void Load_EmptyFontFamily_SetsDefault()
    {
        var json = JsonSerializer.Serialize(new { PreviewTextFontFamily = "", PreviewEditorFontFamily = "" });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultPreviewTextFontFamily, settings.PreviewTextFontFamily);
        Assert.Equal(AppSettings.DefaultPreviewEditorFontFamily, settings.PreviewEditorFontFamily);
    }

    [Fact]
    public void Load_FontSizeOutOfRange_Clamped()
    {
        var json = JsonSerializer.Serialize(new { PreviewTextFontSize = 200, PreviewEditorFontSize = 2 });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(72, settings.PreviewTextFontSize);
        Assert.Equal(6, settings.PreviewEditorFontSize);
    }

    [Fact]
    public void Load_FontSizeZero_SetsDefault()
    {
        var json = JsonSerializer.Serialize(new { PreviewTextFontSize = 0, PreviewEditorFontSize = 0 });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultPreviewTextFontSize, settings.PreviewTextFontSize);
        Assert.Equal(AppSettings.DefaultPreviewEditorFontSize, settings.PreviewEditorFontSize);
    }

    [Fact]
    public void Load_WindowFocusBehavior3_MigratedToTraditional()
    {
        var json = JsonSerializer.Serialize(new { WindowFocusBehavior = 3, StartInLauncherModeMigrated = false });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.False(settings.StartInLauncherMode);
        Assert.Equal(1, settings.WindowFocusBehavior);
        Assert.True(settings.StartInLauncherModeMigrated);
    }

    [Fact]
    public void Load_WindowFocusBehavior0_LegacyDefault_MigratedToStayOpen()
    {
        var json = JsonSerializer.Serialize(new
        {
            WindowFocusBehavior = 0,
            WindowFocusBehaviorMigratedFromLegacyDefault = false,
            StartInLauncherModeMigrated = false
        });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(1, settings.WindowFocusBehavior);
        Assert.True(settings.StartInLauncherMode);
    }

    [Fact]
    public void Load_WindowFocusBehavior_AlreadyMigrated_NoChange()
    {
        var json = JsonSerializer.Serialize(new
        {
            WindowFocusBehavior = 2,
            StartInLauncherModeMigrated = true
        });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(2, settings.WindowFocusBehavior);
    }

    [Fact]
    public void Load_LegacyPreviewGutterColors_Migrated()
    {
        var json = JsonSerializer.Serialize(new
        {
            PreviewGutterContextColor = AppSettings.LegacyDefaultPreviewGutterContextColor,
            PreviewGutterMatchColor = AppSettings.LegacyDefaultPreviewGutterMatchColor,
        });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultPreviewGutterContextColor, settings.PreviewGutterContextColor);
        Assert.Equal(AppSettings.DefaultPreviewGutterMatchColor, settings.PreviewGutterMatchColor);
    }

    [Fact]
    public void Load_NullGutterColors_SetToDefault()
    {
        var json = JsonSerializer.Serialize(new
        {
            PreviewGutterContextColor = (string?)null,
            PreviewGutterMatchColor = (string?)null,
            PreviewEditorGutterColor = (string?)null,
        });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultPreviewGutterContextColor, settings.PreviewGutterContextColor);
        Assert.Equal(AppSettings.DefaultPreviewGutterMatchColor, settings.PreviewGutterMatchColor);
        Assert.Equal(AppSettings.DefaultPreviewEditorGutterColor, settings.PreviewEditorGutterColor);
    }

    [Fact]
    public void Load_NullTerminalWorkingDirectory_SetsEmpty()
    {
        var json = JsonSerializer.Serialize(new { TerminalDefaultWorkingDirectory = (string?)null });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(string.Empty, settings.TerminalDefaultWorkingDirectory);
    }

    [Fact]
    public void PushRecent_AddsToFront()
    {
        var list = new List<string> { "old1", "old2" };
        SettingsService.PushRecent(list, "new");
        Assert.Equal("new", list[0]);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void PushRecent_DeduplicatesCaseInsensitive()
    {
        var list = new List<string> { "First", "Second", "Third" };
        SettingsService.PushRecent(list, "second");
        Assert.Equal("second", list[0]);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void PushRecent_EnforcesMax()
    {
        var list = new List<string>();
        for (int i = 0; i < 25; i++)
            SettingsService.PushRecent(list, $"item_{i}");
        Assert.Equal(AppSettings.MaxRecent, list.Count);
        Assert.Equal("item_24", list[0]);
    }

    [Fact]
    public void PushRecent_IgnoresWhitespace()
    {
        var list = new List<string> { "existing" };
        SettingsService.PushRecent(list, "   ");
        Assert.Single(list);
    }

    [Fact]
    public void DefaultPath_IsInAppData()
    {
        var path = SettingsService.DefaultPath();
        Assert.Contains("Yagu", path);
        Assert.EndsWith("settings.json", path);
    }

    [Fact]
    public void Load_EmptyResultListMatchFontFamily_SetsDefault()
    {
        var json = JsonSerializer.Serialize(new { ResultListMatchTextFontFamily = "" });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultResultListMatchTextFontFamily, settings.ResultListMatchTextFontFamily);
    }

    [Fact]
    public void Load_ResultListMatchFontSizeOutOfRange_Clamped()
    {
        var json = JsonSerializer.Serialize(new { ResultListMatchTextFontSize = 200 });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(72, settings.ResultListMatchTextFontSize);
    }

    [Fact]
    public void Load_InvalidHighlightColor_NormalizesToDefault()
    {
        var json = JsonSerializer.Serialize(new { ResultListMatchHighlightColor = "not_a_color" });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(AppSettings.DefaultResultListMatchHighlightColor, settings.ResultListMatchHighlightColor);
    }

    [Fact]
    public void Load_6DigitHexColor_NormalizesWithAlpha()
    {
        var json = JsonSerializer.Serialize(new { ResultListMatchHighlightColor = "#FFD700" });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal("#FFFFD700", settings.ResultListMatchHighlightColor);
    }

    [Fact]
    public void Load_8DigitHexColor_Preserved()
    {
        var json = JsonSerializer.Serialize(new { ResultListMatchHighlightColor = "#80FF0000" });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal("#80FF0000", settings.ResultListMatchHighlightColor);
    }

    [Fact]
    public void Load_NullIncludeExcludeGlobs_SetsDefaults()
    {
        var json = JsonSerializer.Serialize(new { IncludeGlobs = (string?)null, ExcludeGlobs = (string?)null });
        File.WriteAllText(_settingsPath, json);
        var service = new SettingsService(_settingsPath);
        var settings = service.Load();
        Assert.Equal(string.Empty, settings.IncludeGlobs);
        Assert.Equal(AppSettings.DefaultExcludeGlobs, settings.ExcludeGlobs);
    }
}
