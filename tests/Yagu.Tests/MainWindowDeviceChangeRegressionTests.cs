namespace Yagu.Tests;

public sealed class MainWindowDeviceChangeRegressionTests
{
    [Fact]
    public void DeviceChange_ReusesExistingSubclass_AndDispatchesVolumeWork()
    {
        string root = FindRepoRoot();
        string launcher = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Launcher.cs"));
        string device = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.DeviceChange.cs"));

        Assert.Contains("if (message == WmDeviceChange)", launcher);
        Assert.Contains("CaptureDeviceChangeAndDispatch(wParam, lParam);", launcher);
        Assert.DoesNotContain("SetWindowSubclass", device);
        Assert.Contains("DispatcherQueue.TryEnqueue(() => _ = HandleVolumeChangeAsync(roots, removed));", device);
        Assert.Contains("ViewModel.CancelOperationsForRemovedVolumes(roots);", device);
        Assert.Contains("QueueIndexWatcherHintsRecreation", device);
        Assert.Contains("ViewModel.RefreshAllDriveIndexStatus();", device);
    }

    [Fact]
    public void WatcherRecreation_IsGenerationGuardedAcrossDisposal()
    {
        string root = FindRepoRoot();
        string startup = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
        string window = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));

        Assert.Contains("Interlocked.Increment(ref _indexWatcherHintsGeneration)", startup);
        Assert.Contains("generation != Volatile.Read(ref _indexWatcherHintsGeneration)", startup);
        Assert.Contains("service.Dispose();", startup);
        Assert.Contains("QueueIndexWatcherHintsRecreation(\"startup\")", startup);
        Assert.Contains("Interlocked.Increment(ref _indexWatcherHintsGeneration);", window);
    }

    [Fact]
    public void NativeAndCli_KeepRemovableReadSafetyAndIoDeadlineWired()
    {
        string root = FindRepoRoot();
        string native = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Native", "NativeSearcher.cs"));
        string search = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Services", "SearchService.cs"));
        string cli = File.ReadAllText(Path.Combine(root, "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("return TryReadAbiVersion(QgAbiVersion);", native);
        Assert.Contains("return readAbiVersion() == 8;", native);
        Assert.Contains("AvoidSourceMemoryMap = (byte)(options.AvoidSourceMemoryMap ? 1 : 0)", native);
        Assert.Contains("FileIoTimeoutSeconds = (ushort)Math.Clamp(options.FileIoTimeoutSeconds, 1, 600)", native);
        Assert.Contains("AvoidSourceMemoryMap = options.AvoidSourceMemoryMap", search);
        Assert.Contains("FileIoTimeoutSeconds = options.FileIoTimeoutSeconds", search);
        Assert.Contains("--file-io-timeout", cli);
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Yagu.slnx")))
            dir = Directory.GetParent(dir)?.FullName ?? throw new DirectoryNotFoundException("repo root");
        return dir;
    }
}