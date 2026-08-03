namespace Yagu.Tests;

public sealed class EverythingIndexCoverageWarningRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static readonly string WarningSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.EverythingIndexCoverage.cs");
    private static readonly string SearchInputSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");
    private static readonly string SettingsServiceSource = Read("src", "Yagu", "Services", "SettingsService.cs");
    private static readonly string MainViewModelSource = Read("src", "Yagu", "ViewModels", "MainViewModel.cs");
    private static readonly string SettingsWindowSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");
    private static readonly string CliRunnerSource = Read("src", "Yagu", "CliRunner.cs");

    [Fact]
    public void WarningRunsBeforeOtherPostTranslationSearchGates()
    {
        AssertContainsInOrder(SearchInputSource,
            "private async Task<bool> RunPreSearchWarningGatesAsync()",
            "CheckEverythingIndexCoverageAndWarnAsync()",
            "CheckIndexWarmupAndWarnAsync()",
            "CheckHddAndWarnAsync()",
            "CheckExcludedExtensionAndWarnAsync()");
    }

    [Fact]
    public void WarningSkipsUnsupportedCasesAndChecksCoverageOffUiThread()
    {
        Assert.Contains("ViewModel.SuppressEverythingIndexCoverageWarning", WarningSource);
        Assert.Contains("ViewModel.FileListerBackendIndex == (int)FileListerBackend.Managed", WarningSource);
        Assert.Contains("string.IsNullOrWhiteSpace(ViewModel.Directory) && ViewModel.SearchAllDrivesForceFullScan", WarningSource);
        Assert.Contains("if (everythingExe is null)", WarningSource); // not installed
        Assert.Contains("IReadOnlyList<string> targets = ViewModel.ResolveTargetRoots();", WarningSource);
        Assert.Contains("await Task.Run(() =>", WarningSource);
        Assert.Contains("EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(", WarningSource);
        Assert.Contains("targets, everythingExe, everythingRunning", WarningSource);
        Assert.Contains("if (uncovered is null || uncovered.Count == 0)", WarningSource);
    }

    [Fact]
    public void DialogListsRootDrives_HasRequestedButtons_AndPersistsOptOut()
    {
        Assert.Contains("The following drive does not appear to be in Everything's index:", WarningSource);
        Assert.Contains("The following drives do not appear to be in Everything's index:", WarningSource);
        Assert.Contains("Adding these root drives to the Everything index is highly recommended", WarningSource);
        Assert.Contains("Tools → Options → Indexes", WarningSource);
        Assert.Contains("not only the nested folder you searched", WarningSource);
        Assert.Contains("return root[..2];", WarningSource); // D:\a\b -> D:

        Assert.Contains("PrimaryButtonText = \"Ok, I added it\"", WarningSource);
        Assert.Contains("SecondaryButtonText = \"Ignore for now\"", WarningSource);
        Assert.Contains("Content = roots.Length == 1 ? \"Add drive to Everything now\" : \"Add drives to Everything now\"", WarningSource);
        Assert.Contains("EverythingIndexConfigurator.AddVolumesAndRescanAsync(", WarningSource);
        Assert.Contains("Content = \"Don't warn me again\"", WarningSource);
        Assert.Contains("ShowTitleBar = false", WarningSource);
        Assert.Contains("ShowTopRightCloseButton = true", WarningSource);
        Assert.Contains("ViewModel.SuppressEverythingIndexCoverageWarning = true;", WarningSource);
        Assert.Contains("await ViewModel.PersistSettingsAsync()", WarningSource);
    }

    [Fact]
    public void SuppressionIsPersistedAndResettable()
    {
        Assert.Contains("public bool SuppressEverythingIndexCoverageWarning { get; set; }", SettingsServiceSource);
        Assert.Contains("public partial bool SuppressEverythingIndexCoverageWarning { get; set; }", MainViewModelSource);
        Assert.Contains("SuppressEverythingIndexCoverageWarning = _settings.SuppressEverythingIndexCoverageWarning;", MainViewModelSource);
        Assert.Contains("_settings.SuppressEverythingIndexCoverageWarning = SuppressEverythingIndexCoverageWarning;", MainViewModelSource);
        Assert.Contains("Re-enable Everything drive-index warning", SettingsWindowSource);
        Assert.Contains("_viewModel.SuppressEverythingIndexCoverageWarning = false;", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetEverythingCoverage", SettingsWindowSource);
        Assert.DoesNotContain("if (_viewModel.SuppressEverythingIndexCoverageWarning)", SettingsWindowSource);
        Assert.Contains("This button is enabled after you choose 'Don't warn me again'.", SettingsWindowSource);
    }

    [Fact]
    public void InteractiveCli_HasEquivalentPromptPersistenceAndAutomaticAdd()
    {
        Assert.Contains("PromptEverythingIndexCoverageAsync(perRootOptions, settings, settingsService)", CliRunnerSource);
        Assert.Contains("if (Console.IsInputRedirected || settings.SuppressEverythingIndexCoverageWarning)", CliRunnerSource);
        Assert.Contains("settings.FileListerBackendIndex == (int)FileListerBackend.Managed", CliRunnerSource);
        Assert.Contains("EverythingIndexCoverageDetector.FindConfirmedUncoveredPaths(", CliRunnerSource);
        Assert.Contains("The following drive does not appear to be in Everything's index:", CliRunnerSource);
        Assert.Contains("[A]dd automatically / [O]k, I added it / [I]gnore for now / [N]ever warn again", CliRunnerSource);
        Assert.Contains("EverythingIndexConfigurator.AddVolumesAndRescanAsync(", CliRunnerSource);
        Assert.Contains("settings.SuppressEverythingIndexCoverageWarning = true;", CliRunnerSource);
        Assert.Contains("await settingsService.SaveAsync(settings)", CliRunnerSource);
    }

    private static void AssertContainsInOrder(string source, params string[] values)
    {
        int offset = 0;
        foreach (string value in values)
        {
            int found = source.IndexOf(value, offset, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{value}' after offset {offset}.");
            offset = found + value.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
