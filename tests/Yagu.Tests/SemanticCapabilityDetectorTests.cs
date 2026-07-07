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
        File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
    private static readonly string SettingsSource =
        File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
    private static readonly string SettingsWindowSource =
        File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));
    private static readonly string MainWindowXaml =
        File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

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
    [InlineData("Standard VGA Graphics Adapter", "PCI\\VEN_0000&DEV_0000")]
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

    [Theory]
    [InlineData("VMware SVGA 3D", "PCI\\VEN_15AD&DEV_0405")]
    [InlineData("Oracle VirtualBox Graphics Adapter", "PCI\\VEN_80EE&DEV_BEEF")]
    [InlineData("VirtualBox Graphics Adapter (WDDM)", "PCI\\VEN_80EE&DEV_BEEF")]
    [InlineData("Red Hat QXL controller", "PCI\\VEN_1B36&DEV_0100")]
    [InlineData("Parsec Virtual Display Adapter", "PCI\\VEN_0000&DEV_0000")]
    [InlineData("Citrix Indirect Display Adapter", "PCI\\VEN_5853&DEV_0001")]
    public void IsHardwareAccelerator_RejectsHypervisorVirtualGpusOnPciBus(string driverDesc, string matchingDeviceId)
    {
        // Guest GPUs presented by hypervisors sit on the PCI bus with real-looking IDs, so they pass
        // the physical-bus check and must be excluded by driver-description name instead.
        Assert.False(GpuNpuCapabilityDetector.IsHardwareAccelerator(driverDesc, matchingDeviceId));
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

    // ── Dedicated GPU VRAM detection (drives the larger-model auto-upgrade) ──

    [Fact]
    public void GetMaxDedicatedGpuMemoryBytes_ReturnsLargestRealGpuMemory()
    {
        const long gb24 = 24L * 1024 * 1024 * 1024;
        const long gb1 = 1L * 1024 * 1024 * 1024;
        var detector = DetectorWith(
            display:
            [
                new("NVIDIA GeForce RTX 5090", "PCI\\VEN_10DE&DEV_2B85", gb24),
                new("Intel(R) UHD Graphics", "PCI\\VEN_8086&DEV_9BC4", gb1),
                new("Microsoft Basic Render Driver", "ROOT\\BasicRender", 4096), // software: excluded
            ],
            compute: []);
        Assert.Equal(gb24, detector.GetMaxDedicatedGpuMemoryBytes());
    }

    [Fact]
    public void GetMaxDedicatedGpuMemoryBytes_ZeroWhenOnlySoftwareAdapters()
    {
        var detector = DetectorWith(
            display: [new("Microsoft Basic Render Driver", "ROOT\\BasicRender", 8192)],
            compute: []);
        Assert.Equal(0, detector.GetMaxDedicatedGpuMemoryBytes());
    }

    [Fact]
    public void GetMaxDedicatedGpuMemoryBytes_ZeroWhenReaderThrows()
    {
        var detector = new GpuNpuCapabilityDetector(
            _ => throw new InvalidOperationException("registry unreadable"));
        Assert.Equal(0, detector.GetMaxDedicatedGpuMemoryBytes());
    }

    // ── Human-readable adapter descriptions (bug-report diagnostics) ──

    [Fact]
    public void GetGpuDescriptions_ReturnsRealGpuNames_ExcludingSoftwareEmptyAndDuplicates()
    {
        var detector = DetectorWith(
            display:
            [
                new("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786"),
                new("  NVIDIA GeForce RTX 4070  ", "PCI\\VEN_10DE&DEV_2786"), // dup after trim: excluded
                new("Intel(R) UHD Graphics", "PCI\\VEN_8086&DEV_9A60"),
                new("Microsoft Basic Render Driver", "ROOT\\BasicRender"),    // software: excluded
                new("", "PCI\\VEN_1002&DEV_744C"),                             // empty desc: skipped
            ],
            compute: []);

        Assert.Equal(new[] { "NVIDIA GeForce RTX 4070", "Intel(R) UHD Graphics" }, detector.GetGpuDescriptions());
    }

    [Fact]
    public void GetNpuDescriptions_ReflectsComputeClassOnly()
    {
        var detector = DetectorWith(
            display: [new("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786")],
            compute: [new("Intel(R) AI Boost", "ACPI\\NPU0000")]);

        Assert.Equal(new[] { "Intel(R) AI Boost" }, detector.GetNpuDescriptions());
        Assert.Equal(new[] { "NVIDIA GeForce RTX 4070" }, detector.GetGpuDescriptions());
    }

    [Fact]
    public void GetDescriptions_EmptyWhenReaderThrows()
    {
        var detector = new GpuNpuCapabilityDetector(
            _ => throw new InvalidOperationException("registry unreadable"));
        Assert.Empty(detector.GetGpuDescriptions());
        Assert.Empty(detector.GetNpuDescriptions());
    }

    [Fact]
    public void Interface_ExposesDescriptionsForDiagnostics()
    {
        // Descriptions are part of the detector's public contract (used by bug reports), so they must
        // be reachable through the ISemanticCapabilityDetector seam, not only the concrete type.
        ISemanticCapabilityDetector detector = DetectorWith(
            display: [new("AMD Radeon RX 7900", "PCI\\VEN_1002&DEV_744C")],
            compute: [new("Intel(R) AI Boost", "ACPI\\NPU0000")]);

        Assert.Equal(new[] { "AMD Radeon RX 7900" }, detector.GetGpuDescriptions());
        Assert.Equal(new[] { "Intel(R) AI Boost" }, detector.GetNpuDescriptions());
    }

    [Fact]
    public void ReadsEachDeviceClassAtMostOnce_AcrossRepeatedQueries()
    {
        // Hardware is fixed for a process's lifetime, so the detector must memoize each device
        // class instead of re-walking the registry on every query (HasGpu/HasNpu/HasAccelerated/
        // GetMaxDedicatedGpuMemoryBytes/descriptions would otherwise read the display class 4×).
        var readCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var detector = new GpuNpuCapabilityDetector(classKeyPath =>
        {
            readCounts[classKeyPath] = readCounts.TryGetValue(classKeyPath, out int n) ? n + 1 : 1;
            if (classKeyPath.Contains(DisplayGuid, StringComparison.OrdinalIgnoreCase))
                return new GpuNpuCapabilityDetector.DeviceClassEntry[]
                {
                    new("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786", 24L * 1024 * 1024 * 1024),
                };
            return new GpuNpuCapabilityDetector.DeviceClassEntry[]
            {
                new("Some NPU", "ACPI\\NPU0000"),
            };
        });

        // Exercise every query path that walks a device class.
        _ = detector.HasGpu();
        _ = detector.HasNpu();
        _ = detector.HasAcceleratedHardware();
        _ = detector.GetMaxDedicatedGpuMemoryBytes();
        _ = detector.GetGpuDescriptions();
        _ = detector.GetNpuDescriptions();

        Assert.Equal(2, readCounts.Count); // only the display + compute classes
        Assert.All(readCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void ViewModel_WiresGpuVramIntoTranslatorForLargerModelUpgrade()
    {
        // The VM must detect dedicated VRAM and hand it to the translator so auto-selection can upgrade
        // to a larger, more accurate model on a strong GPU.
        Assert.Contains("_semanticTranslator.SetGpuMemoryBytes(SafeDetectGpuMemoryBytes());", ViewModelSource);
        Assert.Contains("_capabilityDetector.GetMaxDedicatedGpuMemoryBytes()", ViewModelSource);
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
