using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Pins that the first-run "Help improve Yagu?" telemetry consent is SEQUENCED into MainWindow's
/// startup-modal chain rather than fired independently from <c>App.OnLaunched</c>. It previously raced
/// the other first-run prompts (e.g. the result-store temp-location modal) and stacked on top of them;
/// the fix guarantees only one startup modal shows at a time. These are source pins because the
/// WinUI/Foundry-coupled startup files are not compiled into the test assembly.
/// </summary>
public class TelemetryConsentStartupSequencingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    [Fact]
    public void App_OnLaunched_DoesNotShowTelemetryConsentDirectly()
    {
        string app = File.ReadAllText(Path.Combine(RepoRoot(), "Yagu", "App.xaml.cs"));

        // The consent must NOT be fired from App.OnLaunched anymore (that path bypassed the startup-modal
        // sequencing and stacked the dialog on top of another first-run prompt).
        Assert.DoesNotContain("TelemetryConsentDialog.RequestConsentAsync", app);
    }

    [Fact]
    public void StartupChain_AwaitsTelemetryConsentInSequence()
    {
        string startup = File.ReadAllText(Path.Combine(RepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        // OnContentLoaded awaits the consent as part of the sequential first-run chain, BEFORE the
        // result-store temp-location prompt, so the two can never overlap.
        int consent = startup.IndexOf("await ShowTelemetryConsentIfNeededAsync();", StringComparison.Ordinal);
        int resultStore = startup.IndexOf("await CheckFirstRunResultStoreTempLocationAsync();", StringComparison.Ordinal);
        Assert.True(consent >= 0, "OnContentLoaded must await ShowTelemetryConsentIfNeededAsync().");
        Assert.True(resultStore >= 0, "OnContentLoaded must await CheckFirstRunResultStoreTempLocationAsync().");
        Assert.True(consent < resultStore, "Telemetry consent must be sequenced before the result-store temp-location prompt.");
    }

    [Fact]
    public void TelemetryConsentCheck_GatesAndDelegatesToDialog()
    {
        string startup = File.ReadAllText(Path.Combine(RepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        // The check is a one-time, don't-stack-on-another-modal gate that delegates to the consent dialog.
        Assert.Contains("private async Task ShowTelemetryConsentIfNeededAsync()", startup);
        Assert.Contains("if (ViewModel.TelemetryConsentPromptShown)", startup);
        Assert.Contains("if (YaguDialog.HasOpenOwnedWindow(_hwnd))", startup);
        Assert.Contains("await TelemetryConsentDialog.RequestConsentAsync(this);", startup);
    }
}
