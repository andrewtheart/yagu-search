using System;
using System.IO;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the "AI interpretation is taking a while" feature: the persisted per-variant suppression
/// list (exercised at runtime), plus source-pins for the watchdog wiring, model-switch/re-run flow, the
/// ViewModel helpers, and the titleless warning modal — the ViewModel/MainWindow/dialog pull in
/// WindowsAppSDK/Foundry so they cannot run headless here.
/// </summary>
public sealed class SlowSemanticModelWarningTests
{
    [Fact]
    public void SuppressedSlowSemanticModelKeys_DefaultsEmpty_AndRoundTrips()
    {
        Assert.Empty(new AppSettings().SuppressedSlowSemanticModelKeys);

        var tmp = Path.Combine(Path.GetTempPath(), "qg-slowmodel-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            var settings = new AppSettings();
            settings.SuppressedSlowSemanticModelKeys.Add("Phi-4-mini-reasoning-generic-cpu:3");
            svc.Save(settings);

            var loaded = svc.Load();
            Assert.Contains("Phi-4-mini-reasoning-generic-cpu:3", loaded.SuppressedSlowSemanticModelKeys);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Translator_ExposesCurrentVariantKey()
    {
        string iface = File.ReadAllText(Path.Combine(Root, "Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs"));
        Assert.Contains("string? CurrentModelKey { get; }", iface);

        string impl = File.ReadAllText(Path.Combine(Root, "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));
        // The variant id is captured wherever a model is selected/loaded, and exposed as the key
        // (preferring the variant id over the alias).
        Assert.Contains("public string? SelectedModelId { get; private set; }", impl);
        Assert.Contains("SelectedModelId = model.Id;", impl);
        Assert.Contains("public string? CurrentModelKey =>", impl);
        // The key is cleared when the loaded model is dropped, so a stale variant id is never reported.
        Assert.Contains("SelectedModelId = null;", impl);
    }

    [Fact]
    public void ViewModel_ExposesSlowModelHelpers_BackedByAdvisor()
    {
        string src = File.ReadAllText(Path.Combine(Root, "Yagu", "ViewModels", "MainViewModel.SlowSemanticModel.cs"));

        Assert.Contains("public string? CurrentSemanticModelKey => _semanticTranslator?.CurrentModelKey;", src);
        Assert.Contains("public bool IsSlowSemanticModelWarningSuppressed(string? modelKey)", src);
        Assert.Contains("public async Task SuppressSlowSemanticModelWarningAsync(string? modelKey)", src);
        Assert.Contains("_settings.SuppressedSlowSemanticModelKeys.Add(modelKey.Trim());", src);
        Assert.Contains("await PersistSettingsAsync()", src);
        // Faster-model listing delegates to the pure advisor and the translator's runnable-model list.
        Assert.Contains("public async Task<IReadOnlyList<SemanticModelOption>> GetFasterSemanticModelOptionsAsync(", src);
        Assert.Contains("SlowSemanticModelAdvisor.SelectFasterOptions(", src);
    }

    [Fact]
    public void MainWindow_RunsThirtySecondWatchdog_AndReRunsOnSwitch()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SlowSemanticModel.cs"));

        // 30-second threshold, only in Semantic mode, guarded against re-entrancy.
        Assert.Contains("TimeSpan.FromSeconds(30)", src);
        Assert.Contains("await Task.Delay(SlowSemanticModelWarningDelay, token)", src);
        Assert.Contains("!ViewModel.IsSemanticQueryMode || !ViewModel.SemanticSearchAvailable", src);

        // Only show while still translating, never stacked on another owned dialog, and skipped when
        // the running variant is suppressed.
        Assert.Contains("!ViewModel.IsTranslatingSemanticQuery", src);
        Assert.Contains("ViewModel.IsSlowSemanticModelWarningSuppressed(modelKey)", src);
        Assert.Contains("SlowSemanticModelDialog.HasOpenOwnedWindow(_hwnd)", src);

        // Switch path: cancel the in-flight (slow) translation BEFORE preparing the new model so the old
        // model is never unloaded mid-inference; the new pick downloads + becomes the default, then the
        // search re-runs with it.
        Assert.Contains("ViewModel.CancelSemanticTranslation();", src);
        Assert.Contains("await ViewModel.PrepareSemanticModelAsync(alias, progress, token)", src);
        Assert.Contains("_slowSemanticRerunNeeded = true;", src);
        Assert.Contains("if (!_slowSemanticRerunNeeded)", src);

        // The opt-out is honored for the exact running variant key.
        Assert.Contains("await ViewModel.SuppressSlowSemanticModelWarningAsync(modelKey)", src);
    }

    [Fact]
    public void MainWindow_RoutesSemanticSearchesThroughTheWatchdog()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));
        // Both submit entry points go through the watchdog wrapper rather than calling SubmitSearchAsync
        // directly, so a slow interpretation is always detected.
        Assert.Contains("await SubmitSearchWithSlowModelWatchAsync();", src);
        Assert.DoesNotContain("await ViewModel.SubmitSearchAsync(RunPreSearchWarningGatesAsync);", src);
    }

    [Fact]
    public void Dialog_IsTitlelessWarningModal_WithSuppressionCheckbox()
    {
        string src = File.ReadAllText(Path.Combine(Root, "Yagu", "UI", "Windows", "SlowSemanticModelDialog.cs"));

        // Titleless modal recipe (ExtendsContentIntoTitleBar + SetBorderAndTitleBar), matching the app's
        // other owned windows.
        Assert.Contains("ExtendsContentIntoTitleBar = true;", src);
        Assert.Contains("SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);", src);

        // Warning glyph + the per-model opt-out checkbox + the two actions.
        Assert.Contains("Glyph = \"\\uE7BA\"", src);
        Assert.Contains("Colors.Gold", src);
        Assert.Contains("Don't show this warning again for this model", src);
        Assert.Contains("Keep waiting", src);
        Assert.Contains("Use this model", src);

        // Never the WinUI ContentDialog.
        Assert.DoesNotContain("ContentDialog", src);
    }

    private static string Root => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
