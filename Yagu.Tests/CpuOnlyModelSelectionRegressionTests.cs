namespace Yagu.Tests;

/// <summary>
/// Pins the CPU-only model-selection safeguard. On a machine with no usable GPU/NPU, Foundry's
/// <c>download_and_register_eps</c> still registers DirectML, so a "generic-gpu" model variant can
/// LOAD yet crash (native access violation) during the first inference. Yagu's capability detector
/// already knows the real hardware, so the model selector must hard-exclude variants for absent
/// accelerators and deterministically fall back to the CPU build. These are source pins because the
/// Foundry-coupled files are not compiled into the test assembly.
/// </summary>
public sealed class CpuOnlyModelSelectionRegressionTests
{
    [Fact]
    public void Selector_HardExcludesVariantsForUnavailableDevices()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ai", "FoundryModelSelector.cs"));

        // SelectAsync gained an availableDevices set, threaded into the variant chooser.
        Assert.Contains("IReadOnlySet<DeviceType>? availableDevices, CancellationToken cancellationToken)", source);
        Assert.Contains("PreferAccurateVariantAsync(catalog, direct, deviceOrder, availableDevices, cancellationToken)", source);
        Assert.Contains("PreferAccurateVariantAsync(catalog, chosenFamily, deviceOrder, availableDevices, cancellationToken)", source);

        // The hard exclusion itself: a variant whose device is not available is skipped.
        Assert.Contains("DeviceType variantDevice = v.Info?.Runtime?.DeviceType ?? DeviceType.CPU;", source);
        Assert.Contains("if (!availableDevices.Contains(variantDevice)) continue;", source);
    }

    [Fact]
    public void Translator_PassesDetectedAcceleratorsToSelector()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

        Assert.Contains("public void SetAvailableAccelerators(bool hasGpu, bool hasNpu)", source);
        Assert.Contains("private HashSet<DeviceType> AvailableDevices()", source);
        Assert.Contains("var set = new HashSet<DeviceType> { DeviceType.CPU };", source);
        // Both selection call sites (translate path + prepare path) pass the available devices.
        Assert.Contains("FoundryModelSelector.SelectAsync(catalog, _preferredAlias, _deviceOrder, AvailableDevices(), cancellationToken)", source);
        Assert.Contains("FoundryModelSelector.SelectAsync(catalog, alias, _deviceOrder, AvailableDevices(), cancellationToken)", source);
    }

    [Fact]
    public void Interface_ExposesSetAvailableAccelerators()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs"));
        Assert.Contains("void SetAvailableAccelerators(bool hasGpu, bool hasNpu);", source);
    }

    [Fact]
    public void Gui_And_Cli_TellTranslatorTheDetectedHardware()
    {
        string vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "ViewModels", "MainViewModel.cs"));
        string cli = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "CliRunner.cs"));

        Assert.Contains("_semanticTranslator.SetAvailableAccelerators(_semanticHasGpu, _semanticHasNpu);", vm);
        Assert.Contains("translator.SetAvailableAccelerators(cliHasGpu, cliHasNpu);", cli);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
