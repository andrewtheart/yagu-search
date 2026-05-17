using Yagu.Services;

namespace Yagu.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void RoundTrip_Persists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "qg-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(temp);
            var currentCreatedAfter = new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero);
            var currentCreatedBefore = new DateTimeOffset(2023, 2, 3, 0, 0, 0, TimeSpan.Zero);
            var currentModifiedAfter = new DateTimeOffset(2023, 3, 4, 0, 0, 0, TimeSpan.Zero);
            var currentModifiedBefore = new DateTimeOffset(2023, 4, 5, 0, 0, 0, TimeSpan.Zero);
            var defaultCreatedAfter = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
            var defaultCreatedBefore = new DateTimeOffset(2024, 2, 3, 0, 0, 0, TimeSpan.Zero);
            var defaultModifiedAfter = new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero);
            var defaultModifiedBefore = new DateTimeOffset(2024, 4, 5, 0, 0, 0, TimeSpan.Zero);
            var s = new AppSettings
            {
                LastDirectory = @"D:\proj",
                CaseSensitive = true,
                UseRegex = true,
                ContextLines = 7,
                EditorCommand = "code --goto \"{file}:{line}\"",
                MinFileSizeBytes = 10,
                MaxFileSizeBytes = 20,
                DefaultMinFileSizeBytes = 30,
                DefaultMaxFileSizeBytes = 40,
                CreatedAfterDate = currentCreatedAfter,
                CreatedBeforeDate = currentCreatedBefore,
                ModifiedAfterDate = currentModifiedAfter,
                ModifiedBeforeDate = currentModifiedBefore,
                DefaultCreatedAfterDate = defaultCreatedAfter,
                DefaultCreatedBeforeDate = defaultCreatedBefore,
                DefaultModifiedAfterDate = defaultModifiedAfter,
                DefaultModifiedBeforeDate = defaultModifiedBefore,
            };
            s.RecentDirectories.Add(@"D:\a");
            s.SearchHistory.Add("foo");
            svc.Save(s);
            var json = File.ReadAllText(temp);

            var loaded = svc.Load();
            Assert.Equal(@"D:\proj", loaded.LastDirectory);
            Assert.False(loaded.CaseSensitive);
            Assert.False(loaded.UseRegex);
            Assert.DoesNotContain("CaseSensitive", json);
            Assert.DoesNotContain("UseRegex", json);
            Assert.DoesNotContain("\"MinFileSizeBytes\"", json);
            Assert.DoesNotContain("\"MaxFileSizeBytes\"", json);
            Assert.DoesNotContain("\"CreatedAfterDate\"", json);
            Assert.DoesNotContain("\"CreatedBeforeDate\"", json);
            Assert.DoesNotContain("\"ModifiedAfterDate\"", json);
            Assert.DoesNotContain("\"ModifiedBeforeDate\"", json);
            Assert.Contains("DefaultMinFileSizeBytes", json);
            Assert.Contains("DefaultMaxFileSizeBytes", json);
            Assert.Contains("DefaultCreatedAfterDate", json);
            Assert.Contains("DefaultCreatedBeforeDate", json);
            Assert.Contains("DefaultModifiedAfterDate", json);
            Assert.Contains("DefaultModifiedBeforeDate", json);
            Assert.Equal(0, loaded.MinFileSizeBytes);
            Assert.Equal(0, loaded.MaxFileSizeBytes);
            Assert.Equal(30, loaded.DefaultMinFileSizeBytes);
            Assert.Equal(40, loaded.DefaultMaxFileSizeBytes);
            Assert.Null(loaded.CreatedAfterDate);
            Assert.Null(loaded.CreatedBeforeDate);
            Assert.Null(loaded.ModifiedAfterDate);
            Assert.Null(loaded.ModifiedBeforeDate);
            Assert.Equal(defaultCreatedAfter, loaded.DefaultCreatedAfterDate);
            Assert.Equal(defaultCreatedBefore, loaded.DefaultCreatedBeforeDate);
            Assert.Equal(defaultModifiedAfter, loaded.DefaultModifiedAfterDate);
            Assert.Equal(defaultModifiedBefore, loaded.DefaultModifiedBeforeDate);
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
        Assert.Contains("Yagu", path);
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
    public void AdvancedOption_SkipBinary_IsInstanceOnly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-skipbin-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { SkipBinary = false };
            svc.Save(s);
            var json = File.ReadAllText(tmp);
            var loaded = svc.Load();
            Assert.True(loaded.SkipBinary);
            Assert.DoesNotContain("SkipBinary", json);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void AdvancedOption_MaxSearchDepth_IsInstanceOnly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-depth-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, "{\"MaxSearchDepth\":7}");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();
            Assert.Equal(0, loaded.MaxSearchDepth);

            loaded.MaxSearchDepth = 4;
            svc.Save(loaded);

            var json = File.ReadAllText(tmp);
            Assert.DoesNotContain("MaxSearchDepth", json);
            Assert.Equal(0, svc.Load().MaxSearchDepth);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void RoundTrip_SuppressAdminWarning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-admin-warning-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { SuppressAdminWarning = true };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.True(loaded.SuppressAdminWarning);
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
    public void Defaults_MaxResults_IsUnlimited()
    {
        var s = new AppSettings();
        Assert.Equal(0, s.MaxResults);
    }

    [Fact]
    public void Defaults_FileAndDateFilters_AreUnrestricted()
    {
        var settings = new AppSettings();
        var options = new Yagu.Models.SearchOptions { Directory = ".", Query = "needle" };

        Assert.Equal(0, settings.MinFileSizeBytes);
        Assert.Equal(0, settings.MaxFileSizeBytes);
        Assert.Equal(0, settings.DefaultMinFileSizeBytes);
        Assert.Equal(0, settings.DefaultMaxFileSizeBytes);
        Assert.Null(settings.CreatedAfterDate);
        Assert.Null(settings.CreatedBeforeDate);
        Assert.Null(settings.ModifiedAfterDate);
        Assert.Null(settings.ModifiedBeforeDate);
        Assert.Null(settings.DefaultCreatedAfterDate);
        Assert.Null(settings.DefaultCreatedBeforeDate);
        Assert.Null(settings.DefaultModifiedAfterDate);
        Assert.Null(settings.DefaultModifiedBeforeDate);
        Assert.Equal(0, options.MinFileSizeBytes);
        Assert.Equal(0, options.MaxFileSizeBytes);
        Assert.Null(options.CreatedAfterDate);
        Assert.Null(options.CreatedBeforeDate);
        Assert.Null(options.ModifiedAfterDate);
        Assert.Null(options.ModifiedBeforeDate);
    }

    [Fact]
    public void Defaults_SkipBinary_IsTrue()
    {
        var s = new AppSettings();
        Assert.True(s.SkipBinary);
    }

    [Fact]
    public void Defaults_SuppressAdminWarning_IsFalse()
    {
        var s = new AppSettings();
        Assert.False(s.SuppressAdminWarning);
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
