using System;
using System.IO;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the hardware-based Semantic default-mode feature: the pure adapter classifier in
/// <see cref="GpuNpuCapabilityDetector"/>, the ViewModel launch-mode resolution wiring, the
/// persisted settings, the Settings override toggle, and the relocation of the interpretation
/// status text out from above the search box.
/// </summary>
public sealed class SemanticCapabilityDetectorTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ViewModelSource =
        File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
    private static readonly string SettingsSource =
        File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "Services", "SettingsService.cs"));
    private static readonly string SettingsWindowSource =
        File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string MainWindowXaml =
        File.ReadAllText(Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

    // ── Pure classifier behavior ──

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786")]
    [InlineData("Intel(R) UHD Graphics", "PCI\\VEN_8086&DEV_9A60")]
    [InlineData("AMD Radeon RX 7900", "pci\\ven_1002&dev_744c")]
    [InlineData("Some NPU", "ACPI\\NPU0000")]
    public void IsHardwareAccelerator_TrueForPhysicalBusAdapters(string driverDesc, string matchingDeviceId)
    {
        Assert.True(GpuNpuCapabilityDetector.IsHardwareAccelerator(driverDesc, matchingDeviceId));
    }

    [Theory]
    [InlineData("Microsoft Basic Render Driver", "ROOT\\BasicRender")]
    [InlineData("Microsoft Basic Display Adapter", "ROOT\\DISPLAY")]
    [InlineData("Microsoft Remote Display Adapter", "SWD\\RemoteDisplay")]
    [InlineData("Microsoft Hyper-V Video", "ROOT\\VID")]
    public void IsHardwareAccelerator_FalseForSoftwareAndVirtualAdapters(string driverDesc, string matchingDeviceId)
    {
        Assert.False(GpuNpuCapabilityDetector.IsHardwareAccelerator(driverDesc, matchingDeviceId));
    }

    [Fact]
    public void IsHardwareAccelerator_FalseWhenDeviceIdMissing()
    {
        Assert.False(GpuNpuCapabilityDetector.IsHardwareAccelerator("NVIDIA GeForce", ""));
        Assert.False(GpuNpuCapabilityDetector.IsHardwareAccelerator("NVIDIA GeForce", "   "));
    }

    [Fact]
    public void IsHardwareAccelerator_RejectsBasicDriverEvenOnPciBus()
    {
        // A software fallback driver bound to a PCI stub must still be rejected by name.
        Assert.False(GpuNpuCapabilityDetector.IsHardwareAccelerator(
            "Microsoft Basic Display Adapter", "PCI\\VEN_0000&DEV_0000"));
    }

    [Fact]
    public void Detector_DoesNotThrow_AndImplementsInterface()
    {
        ISemanticCapabilityDetector detector = new GpuNpuCapabilityDetector();
        // Must never throw on a real machine regardless of hardware; result is environment-specific.
        _ = detector.HasAcceleratedHardware();
    }

    // ── Detector iteration over a fake device-class reader ──

    private const string DisplayGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static GpuNpuCapabilityDetector DetectorWith(
        GpuNpuCapabilityDetector.DeviceClassEntry[] display,
        GpuNpuCapabilityDetector.DeviceClassEntry[] compute)
    {
        return new GpuNpuCapabilityDetector(classKeyPath =>
            classKeyPath.Contains(DisplayGuid, StringComparison.OrdinalIgnoreCase) ? display : compute);
    }

    [Fact]
    public void HasAcceleratedHardware_FalseWhenNoDevicesInEitherClass()
    {
        var detector = DetectorWith([], []);
        Assert.False(detector.HasAcceleratedHardware());
    }

    [Fact]
    public void HasAcceleratedHardware_TrueWhenDisplayClassHasRealGpu()
    {
        var detector = DetectorWith(
            display: [new("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786")],
            compute: []);
        Assert.True(detector.HasAcceleratedHardware());
    }

    [Fact]
    public void HasAcceleratedHardware_TrueWhenOnlyComputeClassHasNpu()
    {
        // Display class holds only a software fallback; the NPU in the compute class must still win.
        var detector = DetectorWith(
            display: [new("Microsoft Basic Display Adapter", "ROOT\\BasicDisplay")],
            compute: [new("Some NPU", "ACPI\\NPU0000")]);
        Assert.True(detector.HasAcceleratedHardware());
    }

    [Fact]
    public void HasAcceleratedHardware_FalseWhenOnlySoftwareAdaptersPresent()
    {
        var detector = DetectorWith(
            display: [new("Microsoft Basic Render Driver", "ROOT\\BasicRender")],
            compute: [new("Microsoft Hyper-V Video", "ROOT\\VID")]);
        Assert.False(detector.HasAcceleratedHardware());
    }

    [Fact]
    public void HasGpu_OnlyReflectsDisplayClass()
    {
        var detector = DetectorWith(
            display: [new("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786")],
            compute: []);
        Assert.True(detector.HasGpu());
        Assert.False(detector.HasNpu());
    }

    [Fact]
    public void HasNpu_OnlyReflectsComputeClass()
    {
        var detector = DetectorWith(
            display: [new("Microsoft Basic Display Adapter", "ROOT\\BasicDisplay")],
            compute: [new("Some NPU", "ACPI\\NPU0000")]);
        Assert.True(detector.HasNpu());
        Assert.False(detector.HasGpu());
    }

    [Fact]
    public void HasAcceleratedHardware_FalseWhenReaderThrows()
    {
        var detector = new GpuNpuCapabilityDetector(
            _ => throw new InvalidOperationException("registry unreadable"));
        Assert.False(detector.HasAcceleratedHardware());
    }

    [Fact]
    public void HasGpu_FalseWhenReaderThrows()
    {
        var detector = new GpuNpuCapabilityDetector(
            _ => throw new InvalidOperationException("registry unreadable"));
        Assert.False(detector.HasGpu());
    }

    [Fact]
    public void HasNpu_FalseWhenReaderThrows()
    {
        var detector = new GpuNpuCapabilityDetector(
            _ => throw new InvalidOperationException("registry unreadable"));
        Assert.False(detector.HasNpu());
    }

    // ── ViewModel launch-mode wiring ──

    [Fact]
    public void ViewModel_ResolvesLaunchModeFromHardwareAndOverride()
    {
        Assert.Contains("ISemanticCapabilityDetector? capabilityDetector = null", ViewModelSource);
        Assert.Contains("_capabilityDetector = capabilityDetector ?? new GpuNpuCapabilityDetector();", ViewModelSource);
        Assert.Contains("SemanticHardwareAccelerated = SafeDetectAcceleratedHardware();", ViewModelSource);
        Assert.Contains("DefaultToTraditionalSearchMode = _settings.DefaultToTraditionalSearchMode;", ViewModelSource);
        Assert.Contains("IsSemanticQueryMode = ResolveLaunchQueryMode();", ViewModelSource);

        string resolve = ExtractWindow(ViewModelSource,
            "private bool ResolveLaunchQueryMode()", "private bool SafeDetectAcceleratedHardware()");
        Assert.Contains("if (!SemanticSearchAvailable) return false;", resolve);
        Assert.Contains("return _settings.LastQueryModeIsSemantic && SemanticHardwareAccelerated;", resolve);
        Assert.Contains("return SemanticHardwareAccelerated && !_settings.DefaultToTraditionalSearchMode;", resolve);
    }

    [Fact]
    public void ViewModel_RecordsExplicitChoiceAndOverrideHandler()
    {
        string changed = ExtractWindow(ViewModelSource,
            "partial void OnIsSemanticQueryModeChanged(bool value)", "partial void OnDefaultToTraditionalSearchModeChanged");
        Assert.Contains("_settings.HasChosenQueryMode = true;", changed);

        string overrideHandler = ExtractWindow(ViewModelSource,
            "partial void OnDefaultToTraditionalSearchModeChanged(bool value)", "private bool ResolveLaunchQueryMode()");
        Assert.Contains("_settings.DefaultToTraditionalSearchMode = value;", overrideHandler);
        Assert.Contains("if (!_settings.HasChosenQueryMode)", overrideHandler);
        Assert.Contains("IsSemanticQueryMode = ResolveLaunchQueryMode();", overrideHandler);
    }

    [Fact]
    public void ViewModel_ExposesOverrideEnabledComputedProperty()
    {
        Assert.Contains(
            "public bool SemanticDefaultOverrideEnabled => SemanticSearchAvailable && SemanticHardwareAccelerated;",
            ViewModelSource);
        // SemanticSearchAvailable stays decoupled from hardware: semantic remains offered manually.
        Assert.Contains("SemanticSearchAvailable = _settings.SemanticSearchEnabled;", ViewModelSource);
    }

    // ── Persisted settings ──

    [Fact]
    public void Settings_PersistsNewDefaultModeFields()
    {
        Assert.Contains("public bool HasChosenQueryMode { get; set; }", SettingsSource);
        Assert.Contains("public bool DefaultToTraditionalSearchMode { get; set; }", SettingsSource);
        Assert.Contains("_settings.DefaultToTraditionalSearchMode = DefaultToTraditionalSearchMode;", ViewModelSource);
    }

    // ── Settings override toggle UI ──

    [Fact]
    public void SettingsWindow_AddsGreyableSemanticDefaultToggle()
    {
        Assert.Contains("AddSettingsGroupBox(g, \"AI (Semantic) Search\")", SettingsWindowSource);
        Assert.Contains("bool overrideEnabled = _viewModel.SemanticDefaultOverrideEnabled;", SettingsWindowSource);
        Assert.Contains("Default to Traditional search mode", SettingsWindowSource);
        Assert.Contains("IsEnabled = overrideEnabled,", SettingsWindowSource);
        Assert.Contains("_viewModel.DefaultToTraditionalSearchMode = true;", SettingsWindowSource);
        Assert.Contains("_viewModel.DefaultToTraditionalSearchMode = false;", SettingsWindowSource);
    }

    // ── Interpretation text relocation ──

    [Fact]
    public void MainWindow_RendersSemanticStatusInStatusPanelNotAboveSearchBox()
    {
        string statusPanel = ExtractWindow(MainWindowXaml,
            "x:Name=\"SearchStatusPanel\"", "Split pane: results + preview");
        Assert.Contains("x:Name=\"QueryModeBar\"", statusPanel);
        Assert.Contains("ViewModel.SemanticStatusText", statusPanel);
        Assert.Contains("TextWrapping=\"Wrap\"", statusPanel);

        // It must NOT sit inside the search-bar StackPanel above the QueryBox anymore.
        string searchBarRegion = ExtractWindow(MainWindowXaml,
            "<!-- Search bar -->", "x:Name=\"InlineSearchToggles\"");
        Assert.DoesNotContain("x:Name=\"QueryModeBar\"", searchBarRegion);
    }

    private static string ExtractWindow(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"End marker not found: {endMarker}");
        return source.Substring(start, end - start);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yagu.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Yagu.sln from the test output directory.");
    }
}
