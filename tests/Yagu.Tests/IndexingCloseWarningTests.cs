using Yagu.Services.Index;

namespace Yagu.Tests;

public sealed class IndexingCloseWarningTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MainWindowSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.IndexingCloseWarning.cs");
    private static readonly string LauncherSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Launcher.cs");
    private static readonly string AppUpdateSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AppUpdate.cs");
    private static readonly string ViewModelSource = MainViewModelPartials.Text;

    [Fact]
    public void FullBuildUserExit_ExplainsDiscardAndCompleteRestart()
    {
        IndexingCloseWarningContent warning = IndexingCloseWarning.Build(
            IndexingCloseTrigger.UserExit,
            isIncremental: false,
            activeFolder: @"C:\Source");

        Assert.Equal("Indexing is still in progress", warning.Title);
        Assert.Contains(@"“C:\Source”", warning.Message);
        Assert.Contains("partial workspace will be discarded", warning.Message);
        Assert.Contains("complete build must start again later", warning.Message);
        Assert.Contains("previous complete index", warning.Message);
        Assert.Equal("Keep Yagu open", warning.KeepOpenButtonText);
        Assert.Equal("Exit anyway", warning.ExitButtonText);
    }

    [Fact]
    public void IncrementalUserExit_ExplainsCheckpointReplayAndPossibleRebuild()
    {
        IndexingCloseWarningContent warning = IndexingCloseWarning.Build(
            IndexingCloseTrigger.UserExit,
            isIncremental: true,
            activeFolder: "  ");

        Assert.Contains("the active folder", warning.Message);
        Assert.Contains("replay from the last committed checkpoint", warning.Message);
        Assert.Contains("complete rebuild will be required", warning.Message);
    }

    [Fact]
    public void WindowsSessionEnding_ExplainsBlockedRequestAndRetry()
    {
        IndexingCloseWarningContent warning = IndexingCloseWarning.Build(
            IndexingCloseTrigger.WindowsSessionEnding,
            isIncremental: false,
            activeFolder: null);

        Assert.Equal("Windows requested shutdown during indexing", warning.Title);
        Assert.Contains("restart, shutdown, or sign-out", warning.Message);
        Assert.Contains("stopped that request", warning.Message);
        Assert.Contains("retry the Windows operation", warning.Message);
        Assert.Equal("Exit Yagu anyway", warning.ExitButtonText);
    }

    [Fact]
    public void AppUpdate_ExplainsInstallerWillWaitForExit()
    {
        IndexingCloseWarningContent warning = IndexingCloseWarning.Build(
            IndexingCloseTrigger.AppUpdate,
            isIncremental: true,
            activeFolder: @" D:\Documents ");

        Assert.Equal("Indexing is still in progress", warning.Title);
        Assert.Contains(@"“D:\Documents”", warning.Message);
        Assert.Contains("downloaded update will not start", warning.Message);
        Assert.Equal("Keep indexing", warning.KeepOpenButtonText);
        Assert.Equal("Install and exit anyway", warning.ExitButtonText);
    }

    [Fact]
    public void MainWindow_GatesEveryRealExitAndWindowsSessionEnd()
    {
        Assert.Contains("private const uint WmQueryEndSession = 0x0011;", MainWindowSource);
        Assert.Contains("if (_forceClose || !ViewModel.IsIndexBuildActive)", MainWindowSource);
        Assert.Contains("ShowTitleBar = false", MainWindowSource);
        Assert.Contains("DefaultButton = YaguDialogDefaultButton.Primary", MainWindowSource);
        Assert.Contains("if (message == WmQueryEndSession && TryBlockWindowsSessionEnd())", LauncherSource);
        Assert.Contains("args.Cancel = true;", LauncherSource);
        Assert.Contains(
            "RequestApplicationExit(IndexingCloseTrigger.WindowsSessionEnding, warning);",
            MainWindowSource);
        Assert.Contains(
            "capturedWarning is null && !ViewModel.IsIndexBuildActive",
            MainWindowSource);
        Assert.Contains(
            "capturedWarning ?? IndexingCloseWarning.Build(",
            MainWindowSource);
        Assert.True(
            LauncherSource.IndexOf("if (ViewModel.CloseToTray)", StringComparison.Ordinal)
                < LauncherSource.IndexOf("if (ViewModel.IsIndexBuildActive)", StringComparison.Ordinal),
            "Close-to-tray must remain a safe non-exit path that does not show the interruption warning.");
        Assert.True(
            CountOccurrences(LauncherSource, "RequestApplicationExit(IndexingCloseTrigger.UserExit);") >= 3,
            "Direct close, first tray-mode exit, and tray Exit must all use the guarded exit path.");

        int offerInstaller = AppUpdateSource.IndexOf(
            "private async Task OfferVerifiedInstallerAsync(",
            StringComparison.Ordinal);
        Assert.True(offerInstaller >= 0, "Expected the verified installer offer flow.");
        string installerFlow = AppUpdateSource[offerInstaller..];
        int updateConfirmation = installerFlow.IndexOf(
            "ConfirmExitWhileIndexingAsync(IndexingCloseTrigger.AppUpdate)",
            StringComparison.Ordinal);
        int finalVerification = installerFlow.IndexOf(
            "AppUpdateChecker.VerifyDownloadedAssetAsync(installerPath, release.Installer)",
            StringComparison.Ordinal);
        int installerStart = installerFlow.IndexOf(
            "Process.Start(new ProcessStartInfo(installerPath)",
            StringComparison.Ordinal);
        Assert.True(
            updateConfirmation >= 0
                && finalVerification > updateConfirmation
                && installerStart > finalVerification,
            "The final verification must follow every user-controlled wait and precede installer launch.");
        Assert.Contains("public bool IsActiveIndexBuildIncremental => _activeIndexBuildIsIncremental;", ViewModelSource);
        Assert.Contains(
            "BeginIndexBuildActivity(normalizedRoot, isIncremental: true);",
            ViewModelSource);
        Assert.Contains(
            "public void BeginIndexBuildActivity(string? folder = null, bool isIncremental = false)",
            ViewModelSource);
        Assert.Contains("_activeIndexBuildIsIncremental = isIncremental;", ViewModelSource);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
