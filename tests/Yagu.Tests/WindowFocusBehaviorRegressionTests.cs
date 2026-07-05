namespace Yagu.Tests;

/// <summary>
/// Pins that the configured window focus-loss behavior (minimize to tray / stay open / always on top)
/// applies to the window in EVERY mode, not just the compact launcher. The behavior is tracked by a
/// runtime <c>_focusLossBehavior</c> field seeded from the saved <c>WindowFocusBehavior</c> setting,
/// applied at startup and on live setting changes, and overridable per session via the launcher pin
/// button. Source pins because MainWindow is not compiled into the test assembly.
/// </summary>
public sealed class WindowFocusBehaviorRegressionTests
{
    [Fact]
    public void FocusLossBehavior_AppliesInAllModes_NotTiedToPinStateOrLauncher()
    {
        string launcher = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Launcher.cs"));

        // Runtime field driving the effect in all modes.
        Assert.Contains("private int _focusLossBehavior = 1;", launcher);

        // Minimize-to-tray on deactivation is gated on the configured behavior, NOT the launcher pin state.
        Assert.Contains("args.WindowActivationState == WindowActivationState.Deactivated && _focusLossBehavior == 0", launcher);
        Assert.DoesNotContain("_pinState == PinState.MinimizeToTray", launcher);

        // Always-on-top is applied from the same behavior, regardless of mode.
        Assert.Contains("private void ApplyWindowFocusBehavior()", launcher);
        Assert.Contains("SetAlwaysOnTop(_focusLossBehavior == 2);", launcher);

        // The launcher pin states feed the runtime behavior; full window reverts to the saved setting.
        Assert.Contains("_focusLossBehavior = 0;", launcher);
        Assert.Contains("_focusLossBehavior = 2;", launcher);
        Assert.Contains("_focusLossBehavior = ViewModel.WindowFocusBehavior;", launcher);
    }

    [Fact]
    public void WindowInit_SeedsAndLiveAppliesFocusBehavior()
    {
        string xamlCs = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));

        // Seeded from the saved setting and applied at startup (covers a traditional-mode start).
        Assert.Contains("_focusLossBehavior = ViewModel.WindowFocusBehavior;", xamlCs);
        Assert.Contains("ApplyWindowFocusBehavior();", xamlCs);
        // Re-applied live when the setting changes.
        Assert.Contains("if (e.PropertyName == nameof(ViewModel.WindowFocusBehavior))", xamlCs);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
