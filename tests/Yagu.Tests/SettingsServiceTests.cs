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
                IncludeGlobs = "*.cs;*.xaml",
                ExcludeGlobs = "bin;obj",
                IncludeFilterModeIndex = 1,
                ExcludeFilterModeIndex = 1,
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
            Assert.DoesNotContain("IncludeGlobs", json);
            Assert.DoesNotContain("ExcludeGlobs", json);
            Assert.DoesNotContain("IncludeFilterModeIndex", json);
            Assert.DoesNotContain("ExcludeFilterModeIndex", json);
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
            Assert.Equal(string.Empty, loaded.IncludeGlobs);
            Assert.Equal(AppSettings.DefaultExcludeGlobs, loaded.ExcludeGlobs);
            Assert.Equal(0, loaded.IncludeFilterModeIndex);
            Assert.Equal(0, loaded.ExcludeFilterModeIndex);
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
    public void PushRecent_WithTimestamps_RecordsTimeAndDeduplicatesCaseInsensitively()
    {
        var list = new List<string>();
        var times = new Dictionary<string, DateTimeOffset>();
        var before = DateTimeOffset.Now.AddSeconds(-1);

        SettingsService.PushRecent(list, times, "Foo", 10);
        SettingsService.PushRecent(list, times, "foo", 10); // same entry, different case

        Assert.Single(list);
        Assert.Equal("foo", list[0]);
        Assert.Single(times);
        Assert.True(times.ContainsKey("foo"));
        Assert.False(times.ContainsKey("Foo"));
        Assert.True(times["foo"] >= before);
    }

    [Fact]
    public void PushRecent_WithTimestamps_MovesExistingToFrontAndRefreshesTime()
    {
        var list = new List<string>();
        var times = new Dictionary<string, DateTimeOffset>();
        SettingsService.PushRecent(list, times, "a", 10);
        SettingsService.PushRecent(list, times, "b", 10);
        var firstA = times["a"];

        SettingsService.PushRecent(list, times, "a", 10);

        Assert.Equal(new[] { "a", "b" }, list);
        Assert.True(times["a"] >= firstA);
    }

    [Fact]
    public void PushRecent_WithTimestamps_TrimsTimestampsWhenCapping()
    {
        var list = new List<string>();
        var times = new Dictionary<string, DateTimeOffset>();
        for (int i = 0; i < 10; i++) SettingsService.PushRecent(list, times, $"item{i}", 3);

        Assert.Equal(3, list.Count);
        Assert.Equal(3, times.Count);
        foreach (var key in times.Keys) Assert.Contains(key, list); // only survivors keep timestamps
    }

    [Fact]
    public void PushRecent_WithTimestamps_EmptyValueIsNoOp()
    {
        var list = new List<string>();
        var times = new Dictionary<string, DateTimeOffset>();
        SettingsService.PushRecent(list, times, "  ", 10);
        Assert.Empty(list);
        Assert.Empty(times);
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
    public void Defaults_MaxMatchesPerLineAndAbsoluteMaxResults()
    {
        var s = new AppSettings();
        Assert.Equal(5_000, s.MaxMatchesPerLine);
        Assert.Equal(2_000_000, s.AbsoluteMaxResults);
    }

    [Fact]
    public void RoundTrip_MaxMatchesPerLineAndAbsoluteMaxResults()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-perline-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { MaxMatchesPerLine = 1234, AbsoluteMaxResults = 987_654 };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.Equal(1234, loaded.MaxMatchesPerLine);
            Assert.Equal(987_654, loaded.AbsoluteMaxResults);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_ClampsNegativeLimitsToZero()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-neg-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, "{\"MaxMatchesPerLine\":-5,\"AbsoluteMaxResults\":-10}");
            var svc = new SettingsService(tmp);
            var loaded = svc.Load();
            Assert.Equal(0, loaded.MaxMatchesPerLine);
            Assert.Equal(0, loaded.AbsoluteMaxResults);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void RoundTrip_FoundryModelUpdateFields()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-foundry-update-" + Guid.NewGuid() + ".json");
        try
        {
            var checkUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var alertUtc = new DateTimeOffset(2026, 6, 2, 9, 30, 0, TimeSpan.Zero);
            var svc = new SettingsService(tmp);
            var s = new AppSettings
            {
                FoundryModelUpdateAlertsEnabled = false,
                LastFoundryModelCheckUtc = checkUtc,
                LastFoundryModelAlertUtc = alertUtc,
            };
            s.KnownFoundryModelIds.Add("phi-4-mini-cuda-gpu:1");
            s.KnownFoundryModelIds.Add("qwen2.5-cpu:1");
            svc.Save(s);

            var loaded = svc.Load();
            Assert.False(loaded.FoundryModelUpdateAlertsEnabled);
            Assert.Equal(checkUtc, loaded.LastFoundryModelCheckUtc);
            Assert.Equal(alertUtc, loaded.LastFoundryModelAlertUtc);
            Assert.Equal(
                new[] { "phi-4-mini-cuda-gpu:1", "qwen2.5-cpu:1" },
                loaded.KnownFoundryModelIds);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void FoundryModelUpdateFields_DefaultToEnabledWithEmptyBaseline()
    {
        var s = new AppSettings();
        Assert.True(s.FoundryModelUpdateAlertsEnabled);
        Assert.Empty(s.KnownFoundryModelIds);
        Assert.Null(s.LastFoundryModelCheckUtc);
        Assert.Null(s.LastFoundryModelAlertUtc);
    }

    [Fact]
    public void SemanticModelQualificationFields_DefaultToNotCompletedEmptyAlias()
    {
        var s = new AppSettings();
        Assert.False(s.SemanticModelQualificationCompleted);
        Assert.Equal(string.Empty, s.SemanticQualifiedModelAlias);
    }

    [Fact]
    public void RoundTrip_SemanticModelQualificationFields()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-semantic-qual-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings
            {
                SemanticModelQualificationCompleted = true,
                SemanticQualifiedModelAlias = "phi-4-mini",
            };
            svc.Save(s);

            var loaded = svc.Load();
            Assert.True(loaded.SemanticModelQualificationCompleted);
            Assert.Equal("phi-4-mini", loaded.SemanticQualifiedModelAlias);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void RoundTrip_PreviewEditorGutterColor()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-editor-gutter-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var settings = new AppSettings { PreviewEditorGutterColor = "#FF123456" };
            svc.Save(settings);

            var loaded = svc.Load();

            Assert.Equal("#FF123456", loaded.PreviewEditorGutterColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Defaults_PreviewEditorTextColor_IsAutoEmpty()
    {
        var settings = new AppSettings();

        Assert.Equal(string.Empty, AppSettings.DefaultPreviewEditorTextColor);
        Assert.Equal(string.Empty, settings.PreviewEditorTextColor);
    }

    [Fact]
    public void RoundTrip_PreviewEditorTextColor_Override()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-editor-text-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var settings = new AppSettings { PreviewEditorTextColor = "#FF112233" };
            svc.Save(settings);

            var loaded = svc.Load();

            Assert.Equal("#FF112233", loaded.PreviewEditorTextColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_EmptyPreviewEditorTextColor_StaysAuto()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-editor-text-empty-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"PreviewEditorTextColor":""}""");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();

            Assert.Equal(string.Empty, loaded.PreviewEditorTextColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_InvalidPreviewEditorTextColor_FallsBackToAuto()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-editor-text-invalid-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"PreviewEditorTextColor":"not-a-color"}""");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();

            Assert.Equal(string.Empty, loaded.PreviewEditorTextColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_PreviewEditorTextColor_NormalizesShorthandHex()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-editor-text-normalize-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"PreviewEditorTextColor":"123456"}""");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();

            Assert.Equal("#FF123456", loaded.PreviewEditorTextColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_ResultListMatchHighlightColor_NormalizesWithoutUiDependency()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-result-highlight-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"ResultListMatchHighlightColor":"123456"}""");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();

            Assert.Equal("#FF123456", loaded.ResultListMatchHighlightColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_InvalidResultListMatchHighlightColor_UsesDefault()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-result-highlight-invalid-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"ResultListMatchHighlightColor":"not-a-color"}""");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();

            Assert.Equal(AppSettings.DefaultResultListMatchHighlightColor, loaded.ResultListMatchHighlightColor);
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
    public void RoundTrip_SuppressEverythingNotRunningPrompt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-everything-prompt-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            Assert.False(new AppSettings().SuppressEverythingNotRunningPrompt);
            var s = new AppSettings { SuppressEverythingNotRunningPrompt = true };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.True(loaded.SuppressEverythingNotRunningPrompt);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Theory]
    [InlineData("tesseract", "tesseract")]
    [InlineData("Tesseract", "tesseract")]
    [InlineData("  TESSERACT  ", "tesseract")]
    [InlineData("paddle", "paddle")]
    [InlineData("paddleocr", "paddle")]
    [InlineData("paddlesharp", "paddle")]
    [InlineData("PaddleSharp", "paddle")]
    [InlineData("", "paddle")]
    [InlineData("   ", "paddle")]
    [InlineData(null, "paddle")]
    [InlineData("totally-unknown", "paddle")]
    public void NormalizeImageOcrEngine_MapsToKnownIds(string? input, string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeImageOcrEngine(input));
    }

    [Fact]
    public void Defaults_ImageOcrSettings()
    {
        var s = new AppSettings();
        Assert.False(s.SearchImageText);
        Assert.Equal(AppSettings.EffectiveDefaultImageOcrEngine, s.ImageOcrEngine);
        Assert.Equal("paddle", AppSettings.DefaultImageOcrEngine);
    }

    [Fact]
    public void EffectiveDefaultImageOcrEngine_IsPaddleWhereSupported_TesseractOnX86()
    {
        // PaddleSharp is the preferred default (faster + more accurate on CPU), but PaddleOCR's native
        // runtime is win-x64 only, so on x86 the effective default falls back to Tesseract. The test
        // runner's architecture decides which branch is live; both branches are covered deterministically
        // by ResolveDefaultImageOcrEngine / CoerceImageOcrEngineForArch below.
        Assert.Equal(
            AppSettings.PaddleOcrSupported ? "paddle" : "tesseract",
            AppSettings.EffectiveDefaultImageOcrEngine);
        Assert.Equal("paddle", AppSettings.DefaultImageOcrEngine); // preferred engine is unchanged
    }

    [Theory]
    [InlineData(true, "paddle")]
    [InlineData(false, "tesseract")]
    public void ResolveDefaultImageOcrEngine_FallsBackToTesseractWhenPaddleUnsupported(bool paddleSupported, string expected)
    {
        Assert.Equal(expected, AppSettings.ResolveDefaultImageOcrEngine(paddleSupported));
    }

    [Theory]
    [InlineData("paddle", true, "paddle")]
    [InlineData("paddle", false, "tesseract")] // PaddleOCR is x64-only; coerced on x86
    [InlineData("tesseract", true, "tesseract")]
    [InlineData("tesseract", false, "tesseract")]
    public void CoerceImageOcrEngineForArch_CoercesPaddleToTesseractOnX86(string engine, bool paddleSupported, string expected)
    {
        Assert.Equal(expected, AppSettings.CoerceImageOcrEngineForArch(engine, paddleSupported));
    }

    [Fact]
    public void RoundTrip_SearchImageTextAndImageOcrEngine()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-ocr-roundtrip-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { SearchImageText = true, ImageOcrEngine = "tesseract" };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.True(loaded.SearchImageText);
            Assert.Equal("tesseract", loaded.ImageOcrEngine);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_NormalizesPersistedImageOcrEngine()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-ocr-normalize-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"ImageOcrEngine":"PaddleSharp"}""");
            var svc = new SettingsService(tmp);
            Assert.Equal("paddle", svc.Load().ImageOcrEngine);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_UnknownImageOcrEngine_FallsBackToDefault()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-ocr-unknown-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"ImageOcrEngine":"nonsense"}""");
            var svc = new SettingsService(tmp);
            Assert.Equal("paddle", svc.Load().ImageOcrEngine);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public async Task LoadAsync_NormalizesPersistedImageOcrEngine()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-ocr-normalize-async-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"ImageOcrEngine":"Tesseract"}""");
            var svc = new SettingsService(tmp);
            var loaded = await svc.LoadAsync();
            Assert.Equal("tesseract", loaded.ImageOcrEngine);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Theory]
    [InlineData("EnglishV4", "EnglishV4")]
    [InlineData("englishv4", "EnglishV4")]
    [InlineData("  ENGLISHV3  ", "EnglishV3")]
    [InlineData("chinesev4", "ChineseV4")]
    [InlineData("ChineseV5", "ChineseV5")]
    [InlineData("", "ChineseV5")]
    [InlineData("   ", "ChineseV5")]
    [InlineData(null, "ChineseV5")]
    [InlineData("not-a-model", "ChineseV5")]
    public void NormalizeImageOcrModel_MapsToCanonicalKnownModels(string? input, string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeImageOcrModel(input));
    }

    [Theory]
    [InlineData(0, 0)]            // 0 = unlimited (native resolution) preserved
    [InlineData(-5, 0)]          // negative collapses to unlimited
    [InlineData(640, 640)]
    [InlineData(960, 960)]
    [InlineData(100, 320)]       // below floor clamps up
    [InlineData(99999, 4096)]    // above ceiling clamps down
    public void NormalizeImageOcrMaxSide_ClampsToValidRange(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeImageOcrMaxSide(input));
    }

    [Fact]
    public void Defaults_ImageOcrQualitySettings()
    {
        var s = new AppSettings();
        Assert.Equal("ChineseV5", s.ImageOcrModel);
        Assert.Equal("ChineseV5", AppSettings.DefaultImageOcrModel);
        Assert.Equal(960, s.ImageOcrMaxSide);
        Assert.Equal(960, AppSettings.DefaultImageOcrMaxSide);
    }

    [Fact]
    public void RoundTrip_ImageOcrQualitySettings()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-ocr-quality-roundtrip-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { ImageOcrModel = "ChineseV5", ImageOcrMaxSide = 1536 };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.Equal("ChineseV5", loaded.ImageOcrModel);
            Assert.Equal(1536, loaded.ImageOcrMaxSide);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Load_NormalizesPersistedImageOcrModelAndMaxSide()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-ocr-quality-normalize-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, """{"ImageOcrModel":"chinesev4","ImageOcrMaxSide":99999}""");
            var svc = new SettingsService(tmp);
            var loaded = svc.Load();
            Assert.Equal("ChineseV4", loaded.ImageOcrModel);
            Assert.Equal(4096, loaded.ImageOcrMaxSide);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Defaults_PinStartupDirectory_IsOffAndEmpty()
    {
        var s = new AppSettings();
        Assert.False(s.PinStartupDirectory);
        Assert.True(string.IsNullOrEmpty(s.PinnedStartupDirectory));
    }

    [Fact]
    public void RoundTrip_PinStartupDirectory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-pin-dir-roundtrip-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var s = new AppSettings { PinStartupDirectory = true, PinnedStartupDirectory = @"D:\Projects" };
            svc.Save(s);
            var loaded = svc.Load();
            Assert.True(loaded.PinStartupDirectory);
            Assert.Equal(@"D:\Projects", loaded.PinnedStartupDirectory);
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
    public void Defaults_GutterColors_UseSharedPreviewAndContrastEditorDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal("#FF9CDCFE", AppSettings.DefaultPreviewGutterColor);
        Assert.Equal(AppSettings.DefaultPreviewGutterColor, settings.PreviewGutterContextColor);
        Assert.Equal(AppSettings.DefaultPreviewGutterColor, settings.PreviewGutterMatchColor);
        Assert.Equal(AppSettings.DefaultPreviewEditorGutterColor, settings.PreviewEditorGutterColor);
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
    public void Defaults_ExtensionPrefilters_SeparateMediaDataAndBinaryExtensions()
    {
        var s = new AppSettings();
        Assert.Contains("wasm", s.BinaryExtensions);
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

    [Fact]
    public void Load_LegacyPreviewGutterColors_MigrateToSharedBlueDefault()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-gutter-color-migration-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp, $$"""{"PreviewGutterContextColor":"{{AppSettings.LegacyDefaultPreviewGutterContextColor}}","PreviewGutterMatchColor":"{{AppSettings.LegacyDefaultPreviewGutterMatchColor}}","PreviewEditorGutterColor":""}""");
            var svc = new SettingsService(tmp);

            var loaded = svc.Load();

            Assert.Equal(AppSettings.DefaultPreviewGutterColor, loaded.PreviewGutterContextColor);
            Assert.Equal(AppSettings.DefaultPreviewGutterColor, loaded.PreviewGutterMatchColor);
            Assert.Equal(AppSettings.DefaultPreviewEditorGutterColor, loaded.PreviewEditorGutterColor);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
