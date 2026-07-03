namespace Yagu.Tests;

/// <summary>
/// Pins the WebView2-runtime handling for the embedded terminal. The terminal is the only WebView2
/// consumer; a clean Windows install (e.g. Sandbox) lacks the Edge WebView2 Runtime, so
/// <c>CoreWebView2Environment.CreateAsync()</c> throws and the pane was a black box. Two safeguards:
/// (1) the installer silently installs the runtime from a bundled installer (the full offline
/// Standalone Installer in the offline edition, else the online Evergreen bootstrapper), and
/// (2) the app shows an actionable in-pane message if the runtime is still missing. These are source
/// pins because the UI/installer files are not compiled into the test assembly.
/// </summary>
public sealed class WebView2PrerequisiteRegressionTests
{
    [Fact]
    public void TerminalPane_ShowsActionableMessageWhenWebView2Missing()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        string terminalCs = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));

        // A hidden overlay panel with a WebView2-runtime download link lives over the terminal WebView.
        Assert.Contains("x:Name=\"TerminalWebView2MissingPanel\"", xaml);
        Assert.Contains("LinkId=2124703", xaml);

        // The WebView2 init failure path reveals it instead of leaving a black box.
        Assert.Contains("private void ShowTerminalWebView2MissingMessage()", terminalCs);
        Assert.Contains("ShowTerminalWebView2MissingMessage();", terminalCs);
        Assert.Contains("TerminalWebView2MissingPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible", terminalCs);
    }

    [Fact]
    public void Installer_SilentlyInstallsWebView2RuntimeWhenMissing()
    {
        string iss = File.ReadAllText(Path.Combine(FindRepoRoot(), "installer", "yagu-installer.iss"));

        Assert.Contains("function WebView2RuntimeInstalled(): Boolean;", iss);
        // The Evergreen runtime's EdgeUpdate client GUID, used to skip install when already present.
        Assert.Contains("{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", iss);
        Assert.Contains("procedure InstallWebView2Runtime();", iss);
        Assert.Contains("MicrosoftEdgeWebView2Setup.exe", iss);
        Assert.Contains("/silent /install", iss);
        // Wired into post-install (after the required WAR install) and best-effort (never Aborts).
        Assert.Contains("InstallWebView2Runtime();", iss);
    }

    [Fact]
    public void Build_StagesWebView2BootstrapperPrerequisite()
    {
        string buildPs = File.ReadAllText(Path.Combine(FindRepoRoot(), "build-installer.ps1"));
        string scriptPs = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "webview2-prereq.ps1"));

        Assert.Contains("Copy-YaguWebView2Prerequisite", buildPs);
        Assert.Contains("function Copy-YaguWebView2Prerequisite", scriptPs);
        Assert.Contains("MicrosoftEdgeWebView2Setup.exe", scriptPs);
        Assert.Contains("Prerequisites\\WebView2", scriptPs);
    }

    [Fact]
    public void OfflineEdition_BundlesFullWebView2StandaloneInstaller()
    {
        string buildPs = File.ReadAllText(Path.Combine(FindRepoRoot(), "build-installer.ps1"));
        string scriptPs = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "webview2-prereq.ps1"));
        string iss = File.ReadAllText(Path.Combine(FindRepoRoot(), "installer", "yagu-installer.iss"));

        // The offline edition stages the FULL standalone installer (installs WebView2 with NO internet,
        // unlike the online bootstrapper) via Microsoft's stable x64 standalone fwlink.
        Assert.Contains("function Copy-YaguWebView2StandalonePrerequisite", scriptPs);
        Assert.Contains("linkid=2124701", scriptPs);
        Assert.Contains("MicrosoftEdgeWebView2RuntimeInstallerX64.exe", scriptPs);
        Assert.Contains("Copy-YaguWebView2StandalonePrerequisite -RepoRoot $repoRoot -DestinationRoot $stagingDir", buildPs);

        // The Inno installer prefers the offline standalone over the online bootstrapper.
        Assert.Contains("MicrosoftEdgeWebView2RuntimeInstallerX64.exe", iss);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
