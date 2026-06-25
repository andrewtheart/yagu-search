using System;
using System.IO;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the first-run "AI search will run on the CPU" warning: the persisted once-shown flag
/// (exercised at runtime), plus source-pins for the ViewModel gating/dismiss logic and the titleless
/// warning modal — the ViewModel and MainWindow pull in WindowsAppSDK/Foundry so they cannot run
/// headless here.
/// </summary>
public sealed class CpuSemanticWarningTests
{
    [Fact]
    public void CpuSemanticWarningShown_DefaultsFalse_AndRoundTrips()
    {
        Assert.False(new AppSettings().CpuSemanticWarningShown);

        var tmp = Path.Combine(Path.GetTempPath(), "qg-cpuwarn-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            svc.Save(new AppSettings { CpuSemanticWarningShown = true });
            Assert.True(svc.Load().CpuSemanticWarningShown);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void ViewModel_GatesWarning_ToCpuOnlyFirstRun_AndAppliesTraditionalDefault()
    {
        string src = File.ReadAllText(Path.Combine(Root, "Yagu", "ViewModels", "MainViewModel.cs"));

        // Shown only when semantic search is available, no GPU/NPU was detected, and not shown before.
        Assert.Contains(
            "SemanticSearchAvailable && !SemanticHardwareAccelerated && !_settings.CpuSemanticWarningShown",
            src);

        // Dismiss marks it shown (so it never reappears) and, on accept, pins Traditional as the default
        // AND switches the search bar to Traditional immediately.
        Assert.Contains("public async Task DismissCpuSemanticWarningAsync(bool useTraditionalDefault)", src);
        Assert.Contains("_settings.CpuSemanticWarningShown = true;", src);
        Assert.Contains("DefaultToTraditionalSearchMode = true;", src);
        Assert.Contains("IsSemanticQueryMode = false;", src);

        // When the user keeps AI search anyway, Semantic becomes the selected mode and the persisted
        // default (both in the search bar and in settings).
        Assert.Contains("IsSemanticQueryMode = true;", src);
        Assert.Contains("DefaultToTraditionalSearchMode = false;", src);
    }

    [Fact]
    public void Startup_ShowsTitlelessWarningModalWithWarningGlyph()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        // Wired into the awaited first-run startup sequence.
        Assert.Contains("await ShowCpuSemanticWarningIfNeededAsync();", src);
        Assert.Contains("if (!ViewModel.ShouldShowCpuSemanticWarning)", src);
        // Don't stack on another startup prompt.
        Assert.Contains("YaguDialog.HasOpenOwnedWindow(_hwnd)", src);
        // Warning glyph + no title bar, matching the app's other warning dialogs.
        Assert.Contains("TitleGlyph = \"\\uE7BA\"", src);
        Assert.Contains("ShowTitleBar = false", src);
        // Accepting the primary action applies the Traditional default.
        Assert.Contains("ViewModel.DismissCpuSemanticWarningAsync(result == YaguDialogResult.Primary)", src);
        // Shared dialog only — never the WinUI ContentDialog.
        Assert.DoesNotContain("ContentDialog", src);
    }

    private static string Root => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
