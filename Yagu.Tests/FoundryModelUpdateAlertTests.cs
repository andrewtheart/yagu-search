using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the new-Foundry-model alert wiring. The ViewModel, MainWindow, and
/// SettingsWindow pull in WindowsAppSDK/Foundry so they can't be exercised at runtime here; these pins
/// guard the persistence fields, gating, and UI hookup against regressions.
/// </summary>
public sealed class FoundryModelUpdateAlertTests
{
    [Fact]
    public void AppSettings_PersistsFoundryModelBaselineAndToggle()
    {
        string src = File.ReadAllText(Path.Combine(Root, "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool FoundryModelUpdateAlertsEnabled { get; set; } = true;", src);
        Assert.Contains("public List<string> KnownFoundryModelIds { get; set; } = [];", src);
        Assert.Contains("public DateTimeOffset? LastFoundryModelCheckUtc { get; set; }", src);
        Assert.Contains("public DateTimeOffset? LastFoundryModelAlertUtc { get; set; }", src);
    }

    [Fact]
    public void ViewModel_CheckMethod_IsGatedThrottledAndSeedsBaseline()
    {
        string src = File.ReadAllText(Path.Combine(Root, "Yagu", "ViewModels", "MainViewModel.cs"));

        // Gating: alerts on + semantic on + has actually used semantic search (so we never trigger a
        // model/EP download just to check), plus the once/day throttle.
        Assert.Contains(
            "!FoundryModelUpdateAlertsEnabled || !_settings.SemanticSearchEnabled || !_settings.SemanticModelDownloaded",
            src);
        Assert.Contains("FoundryModelUpdateChecker.ShouldCheck(", src);
        Assert.Contains(
            "FoundryModelUpdateChecker.Detect(_settings.KnownFoundryModelIds, currentModels, hasBaseline)",
            src);

        // An empty/failed catalog query must NOT clobber the baseline.
        Assert.Contains("if (currentModels.Count == 0)", src);

        // Commits the refreshed baseline + check time.
        Assert.Contains("_settings.KnownFoundryModelIds = result.CurrentIds.ToList();", src);
        Assert.Contains("_settings.LastFoundryModelCheckUtc = DateTimeOffset.UtcNow;", src);

        // The toggle is persisted on change.
        Assert.Contains("partial void OnFoundryModelUpdateAlertsEnabledChanged(bool value)", src);
    }

    [Fact]
    public void Startup_FiresFoundryCheck_AndModalUsesSharedCustomDialog()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        Assert.Contains("_ = CheckForNewFoundryModelsAsync();", src);
        Assert.Contains("await ViewModel.CheckForNewFoundryModelsAsync(", src);
        // Don't stack on another startup dialog.
        Assert.Contains("YaguDialog.HasOpenOwnedWindow(_hwnd)", src);
        Assert.Contains("YaguDialog.ShowAsync(", src);
        // "Don't alert me again" flips the master setting; primary opens the existing picker.
        Assert.Contains("ViewModel.FoundryModelUpdateAlertsEnabled = false;", src);
        Assert.Contains("SemanticModelDownloadDialog.ShowAsync(", src);
        // Shared dialog only — never the WinUI ContentDialog.
        Assert.DoesNotContain("ContentDialog", src);
    }

    [Fact]
    public void SettingsAiTab_HasModelAlertToggle()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains("Alert me when new on-device models are available", src);
        Assert.Contains("_viewModel.FoundryModelUpdateAlertsEnabled = true;", src);
        Assert.Contains("_viewModel.FoundryModelUpdateAlertsEnabled = false;", src);
        // Greys out with the rest of the AI controls when semantic search is disabled.
        Assert.Contains("dependentControls.Add(modelAlerts);", src);
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
