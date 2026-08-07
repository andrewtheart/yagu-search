using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the status-bar resource indicators (disk temp, total index storage, and
/// RAM used by Yagu and its worker processes). The view-model wiring and XAML live in files not compiled
/// into the test assembly, so their contracts are pinned here as source substrings. The pure math is
/// unit-tested in <see cref="ResourceUsageMonitorTests"/>.
/// </summary>
public sealed class ResourceUsageIndicatorRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static readonly string MainViewModelSource = MainViewModelPartials.Text;
    private static readonly string MainWindowXaml = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml");
    private static readonly string SettingsServiceSource = Read("src", "Yagu", "Services", "SettingsService.cs");
    private static readonly string SettingsWindowSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");
    private static readonly string OrphanedWorkerCleanupSource = Read("src", "Yagu", "Services", "OrphanedWorkerCleanup.cs");

    [Fact]
    public void ViewModel_ExposesTempIndexAndRamIndicatorProperties()
    {
        Assert.Contains("public partial string TempUsageText { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string TempUsageTooltip { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string IndexUsageText { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string IndexUsageTooltip { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string RamUsageText { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string RamUsageTooltip { get; set; }", MainViewModelSource);
    }

    [Fact]
    public void ViewModel_RunsMonitorOffThreadEveryTenSeconds_AndStopsOnDispose()
    {
        // The monitor is started once from the constructor and torn down on Dispose.
        Assert.Contains("StartResourceUsageMonitor();", MainViewModelSource);
        Assert.Contains("StopResourceUsageMonitor();", MainViewModelSource);
        Assert.Contains("private async Task RunResourceUsageMonitorAsync(CancellationTokenSource cts)", MainViewModelSource);

        // A 10 s cadence, and the measurement runs off the UI thread (PeriodicTimer awaited with
        // ConfigureAwait(false); the first sample explicitly via Task.Run).
        Assert.Contains("new PeriodicTimer(TimeSpan.FromSeconds(10))", MainViewModelSource);
        Assert.Contains("await Task.Run(() => MeasureAndPublishResourceUsage(cts.Token), cts.Token).ConfigureAwait(false);", MainViewModelSource);
        Assert.Contains("await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)", MainViewModelSource);

        // Only the formatted labels are marshalled back to the UI thread.
        Assert.Contains("_dispatcher.TryEnqueue(() =>", MainViewModelSource);
        Assert.Contains("TempUsageText = tempText;", MainViewModelSource);
        Assert.Contains("IndexUsageText = indexText;", MainViewModelSource);
        Assert.Contains("RamUsageText = ramText;", MainViewModelSource);
    }

    [Fact]
    public void ViewModel_MeasuresTempFilesAndAttributesWorkerRamByParentPid()
    {
        Assert.Contains("ResourceUsageMonitor.SumProcessTempResultBytes(SearchResultTempDirectory, Environment.ProcessId)", MainViewModelSource);
        Assert.Contains("ResourceUsageMonitor.GetTotalPhysicalMemoryBytes()", MainViewModelSource);
        // Sum this process + only its own worker children (parent PID == this process).
        Assert.Contains("foreach (string workerName in OrphanedWorkerCleanup.WorkerProcessNames)", MainViewModelSource);
        Assert.Contains("OrphanedWorkerCleanup.GetParentProcessId(worker.Id) == myPid", MainViewModelSource);
        Assert.Contains("worker.WorkingSet64", MainViewModelSource);
    }

    [Fact]
    public void ViewModel_CachesIndexSizeSkipsDuringSearchAndHonorsSelectedBackend()
    {
        Assert.Contains("IndexStorageSizeRefreshInterval = TimeSpan.FromMinutes(1)", MainViewModelSource);
        Assert.Contains("private IndexStorageSizeMeasurement? MeasureIndexStorageUsage(", MainViewModelSource);
        Assert.Contains("if (IsSearching)", MainViewModelSource);
        Assert.Contains("CancelIndexStorageMeasurement();", MainViewModelSource);
        Assert.Contains("Volatile.Read(ref _indexStorageMeasurementCts)?.Cancel();", MainViewModelSource);
        Assert.Contains("if (IsSearching)", MainViewModelSource);
        Assert.Contains("&& measurementCts.IsCancellationRequested)", MainViewModelSource);
        Assert.Contains("ResourceUsageMonitor.MeasureTotalIndexStorageBytes(", MainViewModelSource);
        Assert.Contains("var backend = (FileListerBackend)FileListerBackendIndex;", MainViewModelSource);
        Assert.Contains("bool sameBackend = _cachedIndexStorageBackend == backend;", MainViewModelSource);
        Assert.Contains("_cachedIndexStorageBackend = backend;", MainViewModelSource);
        Assert.Contains("_nextIndexStorageSizeRefreshUtc = now + IndexStorageSizeRefreshInterval;", MainViewModelSource);
    }

    [Fact]
    public void OrphanedWorkerCleanup_ExposesParentPidHelperForAttribution()
    {
        Assert.Contains("internal static int GetParentProcessId(int pid)", OrphanedWorkerCleanupSource);
        Assert.Contains("internal static readonly string[] WorkerProcessNames", OrphanedWorkerCleanupSource);
    }

    [Fact]
    public void MainWindowXaml_HasTempIndexAndRamStatusBarIndicators()
    {
        Assert.Contains("x:Name=\"ResourceUsageStatusCluster\" ColumnSpacing=\"8\" Margin=\"0,0,10,0\"", MainWindowXaml);
        Assert.Contains("<ColumnDefinition Width=\"104\" />", MainWindowXaml);
        Assert.Contains("<ColumnDefinition Width=\"108\" />", MainWindowXaml);
        Assert.Contains("<ColumnDefinition Width=\"220\" />", MainWindowXaml);
        Assert.Contains("x:Name=\"TempUsageBlock\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.TempUsageText, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.TempUsageTooltip, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("x:Name=\"TempIndexStatusSeparator\" Grid.Column=\"1\" Text=\"|\"", MainWindowXaml);
        Assert.Contains("x:Name=\"IndexUsageBlock\" Grid.Column=\"2\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexUsageText, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexUsageTooltip, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("x:Name=\"IndexRamStatusSeparator\" Grid.Column=\"3\" Text=\"|\"", MainWindowXaml);
        Assert.Contains("x:Name=\"RamUsageBlock\" Grid.Column=\"4\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.RamUsageText, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.RamUsageTooltip, Mode=OneWay}\"", MainWindowXaml);
        Assert.DoesNotContain("x:Name=\"SkipCountBlock\"", MainWindowXaml);
    }

    [Fact]
    public void DeveloperOptions_HidesResourceUsageClusterByDefault_AndPersistsTheFlag()
    {
        Assert.Contains("public bool ShowResourceUsageInStatusBar { get; set; }", SettingsServiceSource);
        Assert.DoesNotContain("ShowResourceUsageInStatusBar { get; set; } = true", SettingsServiceSource);
        Assert.Contains("ShowResourceUsageInStatusBar = _settings.ShowResourceUsageInStatusBar;", MainViewModelSource);
        Assert.Contains("_settings.ShowResourceUsageInStatusBar = ShowResourceUsageInStatusBar;", MainViewModelSource);
        Assert.Contains("ResourceUsageStatusVisibility =>", MainViewModelSource);
        Assert.Contains("OnShowResourceUsageInStatusBarChanged", MainViewModelSource);

        Assert.Contains("Content = \"Show resource usage in status bar\"", SettingsWindowSource);
        Assert.Contains("IsChecked = _viewModel.ShowResourceUsageInStatusBar", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowResourceUsageInStatusBar = true", SettingsWindowSource);
        Assert.Contains("_viewModel.ShowResourceUsageInStatusBar = false", SettingsWindowSource);

        Assert.Contains("x:Name=\"ResourceUsageStatusCluster\"", MainWindowXaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.ResourceUsageStatusVisibility, Mode=OneWay}\"", MainWindowXaml);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
