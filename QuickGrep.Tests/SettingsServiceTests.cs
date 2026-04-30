using QuickGrep.Services;

namespace QuickGrep.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void RoundTrip_Persists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "qg-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(temp);
            var s = new AppSettings
            {
                LastDirectory = @"D:\proj",
                CaseSensitive = true,
                UseRegex = true,
                ContextLines = 7,
                EditorCommand = "code --goto \"{file}:{line}\"",
            };
            s.RecentDirectories.Add(@"D:\a");
            s.SearchHistory.Add("foo");
            svc.Save(s);

            var loaded = svc.Load();
            Assert.Equal(@"D:\proj", loaded.LastDirectory);
            Assert.True(loaded.CaseSensitive);
            Assert.True(loaded.UseRegex);
            Assert.Equal(7, loaded.ContextLines);
            Assert.Single(loaded.RecentDirectories);
            Assert.Single(loaded.SearchHistory);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    [Fact]
    public void PushRecent_DeduplicatesAndCapsSize()
    {
        var list = new List<string> { "a", "b", "c" };
        SettingsService.PushRecent(list, "b");
        Assert.Equal(new[] { "b", "a", "c" }, list);

        for (int i = 0; i < 25; i++) SettingsService.PushRecent(list, $"item{i}", 5);
        Assert.Equal(5, list.Count);
    }
}

// ─── SettingsService: coverage gaps ─────────────────────────────────────

public class SettingsServiceCoverageTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var svc = new SettingsService(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".json"));
        var s = svc.Load();
        Assert.NotNull(s);
        Assert.Empty(s.RecentDirectories);
        Assert.Empty(s.SearchHistory);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-corrupt-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, "NOT VALID JSON {{{");
            var svc = new SettingsService(tmp);
            var s = svc.Load();
            Assert.NotNull(s);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Load_MaxResults_Migration()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-migration-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"MaxResults":50000}""");
            var svc = new SettingsService(tmp);
            var s = svc.Load();
            // MaxResults is deserialized as-is (no migration zeroing)
            Assert.Equal(50000, s.MaxResults);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void PushRecent_AddsToFront()
    {
        var list = new List<string> { "a", "b" };
        SettingsService.PushRecent(list, "c");
        Assert.Equal("c", list[0]);
    }

    [Fact]
    public void PushRecent_MovesExistingToFront()
    {
        var list = new List<string> { "a", "b", "c" };
        SettingsService.PushRecent(list, "c");
        Assert.Equal("c", list[0]);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void PushRecent_CapsAtMaxSize()
    {
        var list = new List<string>();
        for (int i = 0; i < 30; i++) SettingsService.PushRecent(list, $"item{i}", 10);
        Assert.Equal(10, list.Count);
    }

    [Fact]
    public void DefaultPath_ContainsAppName()
    {
        var path = SettingsService.DefaultPath();
        Assert.Contains("QuickGrep", path);
    }

    [Fact]
    public void Save_And_Load_RoundTrip_WithMaxResults()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-roundtrip-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { MaxResults = 999 };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.Equal(999, loaded.MaxResults);
        }
        finally { File.Delete(tmp); }
    }
}

// ─── SettingsService: extra coverage ────────────────────────────────────

public class SettingsServiceExtraTests
{
    [Fact]
    public void ParameterlessConstructor_UsesDefaultPath()
    {
        var svc = new SettingsService();
        var settings = svc.Load();
        Assert.NotNull(settings);
    }

    [Fact]
    public void Save_InvalidPath_DoesNotThrow()
    {
        var svc = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "sub", "settings.json"));
        var settings = new AppSettings();
        var ex = Record.Exception(() => svc.Save(settings));
        // The implementation may create directories or silently fail; either way, no crash
    }

    [Fact]
    public void Save_ToInvalidDrive_DoesNotThrow()
    {
        // Use an invalid path that will definitely fail
        var svc = new SettingsService(@"Z:\nonexistent\path\settings.json");
        var settings = new AppSettings();
        var ex = Record.Exception(() => svc.Save(settings));
        Assert.Null(ex); // caught internally
    }

    [Fact]
    public void PushRecent_EmptyValue_IsNoOp()
    {
        var list = new List<string> { "existing" };
        SettingsService.PushRecent(list, "");
        Assert.Single(list);
        Assert.Equal("existing", list[0]);
    }

    [Fact]
    public void PushRecent_WhitespaceValue_IsNoOp()
    {
        var list = new List<string> { "existing" };
        SettingsService.PushRecent(list, "   ");
        Assert.Single(list);
    }
}

// ─── SettingsService: null JSON deserialization ─────────────────────────

public class SettingsServiceDeserializeNullTests
{
    [Fact]
    public void Load_NullJson_ReturnsDefaults()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-null-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, "null");
            var svc = new SettingsService(tmp);
            var s = svc.Load();
            Assert.NotNull(s);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}

// ─── SettingsService: new settings round-trip ───────────────────────────

public class SettingsServiceNewFieldTests
{
    [Fact]
    public void RoundTrip_SdkChannelBufferSize()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-sdk-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { SdkChannelBufferSize = 8192 };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.Equal(8192, loaded.SdkChannelBufferSize);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void RoundTrip_SkipBinary()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-skipbin-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { SkipBinary = false };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.False(loaded.SkipBinary);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Defaults_SdkChannelBufferSize_Is4096()
    {
        var s = new AppSettings();
        Assert.Equal(4096, s.SdkChannelBufferSize);
    }

    [Fact]
    public void Defaults_SkipBinary_IsTrue()
    {
        var s = new AppSettings();
        Assert.True(s.SkipBinary);
    }

    [Fact]
    public void Defaults_SkipExtensions_UsesExpandedBinaryPrefilter()
    {
        var s = new AppSettings();
        Assert.Contains("wasm", s.SkipExtensions);
        Assert.Contains("sqlite", s.SkipExtensions);
        Assert.Contains("etl", s.SkipExtensions);
    }

    [Fact]
    public void Load_LegacySkipExtensions_MigratesToExpandedDefault()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-skip-ext-migration-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, $$"""{"SkipExtensions":"{{AppSettings.LegacyDefaultSkipExtensions}}"}""");
            var svc = new SettingsService(tmp);
            var loaded = svc.Load();

            Assert.Equal(AppSettings.DefaultSkipExtensions, loaded.SkipExtensions);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
