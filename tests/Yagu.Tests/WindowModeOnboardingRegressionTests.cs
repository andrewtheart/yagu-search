using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the first-launch "choose your window style" onboarding modal. The
/// modal, its three stylistic mode cards, the settings flag, and the startup wiring live in WinUI files
/// that are not compiled into the test assembly, so their contract is pinned here as source substrings.
/// </summary>
public sealed class WindowModeOnboardingRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string OnboardingSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.WindowModeOnboarding.cs"));
    private static readonly string StartupChecksSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
    private static readonly string SettingsServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

    [Fact]
    public void Settings_HasPromptedWindowModeFlag()
        => Assert.Contains("public bool HasPromptedWindowMode { get; set; }", SettingsServiceSource);

    [Fact]
    public void DeveloperOptions_HasResetWindowStylePromptButton()
    {
        // A Developer Options ▸ Reminders and Warnings reset button re-shows the window-style prompt by
        // clearing HasPromptedWindowMode.
        Assert.Contains("Reset window style prompt (re-prompt on startup)", SettingsWindowSource);
        Assert.Contains("_viewModel.Settings.HasPromptedWindowMode = false;", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetWindowStylePrompt", SettingsWindowSource);
    }

    [Fact]
    public void Onboarding_IsOneTime_GatedAndMarkedShown()
    {
        Assert.Contains("private async Task CheckFirstRunWindowModeAsync()", OnboardingSource);
        Assert.Contains("if (ViewModel.Settings.HasPromptedWindowMode)", OnboardingSource);
        Assert.Contains("ViewModel.Settings.HasPromptedWindowMode = true;", OnboardingSource);
        Assert.Contains("await ViewModel.PersistSettingsAsync();", OnboardingSource);
    }

    [Fact]
    public void Onboarding_ShowsTitleBarlessYaguDialogWithThreeCards()
    {
        Assert.Contains("YaguDialog.ShowAsync(", OnboardingSource);
        Assert.Contains("ShowTitleBar = false", OnboardingSource);
        Assert.Contains("BuildLauncherMockup(onTop: false)", OnboardingSource);
        Assert.Contains("BuildLauncherMockup(onTop: true)", OnboardingSource);
        Assert.Contains("BuildTraditionalMockup()", OnboardingSource);
        // Stylistic, not a screenshot: mock-ups are drawn from theme brushes.
        Assert.Contains("ThemeBrush(", OnboardingSource);
        Assert.DoesNotContain("ContentDialog", OnboardingSource);
    }

    [Fact]
    public void Onboarding_CardsAreSelectableTilesWithAccentAurora()
    {
        // The whole window mock-up tile is the selectable item -- no radio buttons.
        Assert.DoesNotContain("RadioButton", OnboardingSource);
        Assert.DoesNotContain("GroupName", OnboardingSource);
        // Each card is a focusable Border that selects on tap / Enter / Space.
        Assert.Contains("private Border BuildWindowModeCard(", OnboardingSource);
        Assert.Contains("IsTabStop = true", OnboardingSource);
        Assert.Contains("card.Tapped += (_, _) => PreviewWindowModeCard(captured);", OnboardingSource);
        Assert.Contains("PreviewWindowModeCard(captured);", OnboardingSource);
        // Selection paints a blue accent border + wash ("aurora").
        Assert.Contains("private void SelectWindowModeCard(int index)", OnboardingSource);
        Assert.Contains("AccentAuroraBrush()", OnboardingSource);
        Assert.Contains("ThemeBrush(\"AccentFillColorDefaultBrush\")", OnboardingSource);
    }

    [Fact]
    public void Onboarding_ClickingACardLivePreviewsTheWindow_AndSkipReverts()
    {
        // Clicking a card selects it AND switches the live window to that style as a temporary preview.
        Assert.Contains("private void PreviewWindowModeCard(int index)", OnboardingSource);
        Assert.Contains("SelectWindowModeCard(index);", OnboardingSource);
        Assert.Contains("_windowModePreviewActive = true;", OnboardingSource);
        Assert.Contains("ApplyWindowModeChoice(index);", OnboardingSource);
        // The baseline is captured when the picker opens so a Skip can restore it.
        Assert.Contains("CaptureWindowModePreviewBaseline();", OnboardingSource);
        Assert.Contains("private void RevertWindowModePreview()", OnboardingSource);
        // Result handling: keep on "Use this style", revert on Skip after a preview.
        Assert.Contains("ApplyWindowModeChoice(_windowModePickIndex);", OnboardingSource);
        Assert.Contains("else if (_windowModePreviewActive)", OnboardingSource);
        Assert.Contains("RevertWindowModePreview();", OnboardingSource);
    }

    [Fact]
    public void Onboarding_TraditionalWindowIsFirstAndSelectedByDefault()
    {
        int traditional = OnboardingSource.IndexOf("0, \"Traditional window\"", StringComparison.Ordinal);
        int compact = OnboardingSource.IndexOf("1, \"Compact launcher\"", StringComparison.Ordinal);
        int onTop = OnboardingSource.IndexOf("2, \"Launcher, always on top\"", StringComparison.Ordinal);
        Assert.True(traditional >= 0 && compact >= 0 && onTop >= 0, "All three cards must be built.");
        Assert.True(traditional < compact && compact < onTop, "Traditional window must be the first card.");
        // Traditional (index 0) is the default selection.
        Assert.Contains("SelectWindowModeCard(0)", OnboardingSource);
    }

    [Fact]
    public void Onboarding_MapsThreeModesToSettingsAndLivePinState()
    {
        // Compact launcher (hides to tray)
        Assert.Contains("ViewModel.WindowFocusBehavior = 0;", OnboardingSource);
        Assert.Contains("_pinState = PinState.MinimizeToTray;", OnboardingSource);
        // Launcher, always on top
        Assert.Contains("ViewModel.WindowFocusBehavior = 2;", OnboardingSource);
        Assert.Contains("_pinState = PinState.AlwaysOnTop;", OnboardingSource);
        // Traditional window
        Assert.Contains("ViewModel.StartInLauncherMode = false;", OnboardingSource);
        Assert.Contains("_pinState = PinState.FullWindow;", OnboardingSource);
        // The pick is applied live.
        Assert.Contains("ApplyPinState();", OnboardingSource);
        // Switching from the traditional window into a launcher mode must fully enter launcher mode so
        // the results pane collapses (RestoreToLauncherChrome alone leaves it visible from traditional).
        Assert.Contains("EnterLauncherMode();", OnboardingSource);
    }

    [Fact]
    public void Onboarding_IsWiredIntoStartupChainAfterTelemetryConsent()
    {
        Assert.Contains("await CheckFirstRunWindowModeAsync();", StartupChecksSource);
        int telemetry = StartupChecksSource.IndexOf("await ShowTelemetryConsentIfNeededAsync();", StringComparison.Ordinal);
        int windowMode = StartupChecksSource.IndexOf("await CheckFirstRunWindowModeAsync();", StringComparison.Ordinal);
        Assert.True(telemetry >= 0 && windowMode > telemetry, "Window-mode prompt should follow telemetry consent in the startup chain.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
