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
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryModelSelector.cs"));

        // SelectAsync gained an availableDevices set, threaded into the variant chooser.
        Assert.Contains("IReadOnlySet<DeviceType>? availableDevices, CancellationToken cancellationToken)", source);
        Assert.Contains("PreferAccurateVariantAsync(catalog, direct, deviceOrder, availableDevices, cancellationToken)", source);
        Assert.Contains("PreferAccurateVariantAsync(catalog, chosenFamily, deviceOrder, availableDevices, cancellationToken)", source);

        // The hard exclusion itself: a variant whose device is not available is skipped. Device is read
        // from the variant ID (Foundry's reported DeviceType is unreliable — DirectML registers in
        // Sandbox), so a CPU build is correctly identified on a CPU-only machine.
        Assert.Contains("DeviceType variantDevice = ResolveVariantDevice(v);", source);
        Assert.Contains("if (!availableDevices.Contains(variantDevice)) continue;", source);
        Assert.Contains("id.Contains(\"-cpu\")", source);

        // Auto-selection resolves each family's REAL runnable variant (device from the id via
        // ResolveVariantDevice / BestRunnableVariant, NOT Foundry's unreliable family-level DeviceType,
        // which reports "GPU" on a CPU-only box because DirectML registers everywhere) and DROPS families
        // with no runnable variant. This both prevents auto-choosing a GPU/NPU-only family AND stops CPU
        // chat families being wrongly dropped (the "no compatible model" bug on the 4 GB CPU-only box).
        Assert.Contains("BestRunnableVariant(family, deviceOrder, availableDevices)", source);
        Assert.Contains("Device: ResolveVariantDevice(runnable)", source);
        Assert.Contains("if (runnable is null) continue;", source);
    }

    [Fact]
    public void Picker_OnlyListsAndRecommendsRunnableDeviceVariants()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

        // The model picker must not list/recommend GPU/NPU variants on a CPU-only machine (DirectML
        // registers even in Windows Sandbox, so those builds look compatible yet crash on inference).
        // It resolves each family to the best variant the machine can actually run and shows ITS device.
        Assert.Contains("var availableDevices = AvailableDevices();", source);
        Assert.Contains("FoundryModelSelector.BestRunnableVariant(family, _deviceOrder, availableDevices)", source);
        Assert.Contains("DeviceLabelOf(FoundryModelSelector.ResolveVariantDevice(variant))", source);
    }

    [Fact]
    public void Translator_PassesDetectedAcceleratorsToSelector()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

        Assert.Contains("public void SetAvailableAccelerators(bool hasGpu, bool hasNpu)", source);
        Assert.Contains("private HashSet<DeviceType> AvailableDevices()", source);
        Assert.Contains("var set = new HashSet<DeviceType> { DeviceType.CPU };", source);
        // The GPU build is offered ONLY when the GPU can actually run a model. An integrated GPU (no
        // dedicated VRAM) crashes on the ONNX WebGPU/DirectML path, so it is excluded and falls back to CPU.
        Assert.Contains("if (GpuInferencePolicy.CanUseGpuForInference(_hasGpu, _gpuMemoryBytes)) set.Add(DeviceType.GPU);", source);
        // Both selection call sites (translate path + prepare path) pass the available devices AND the
        // CPU-only memory budget.
        Assert.Contains("FoundryModelSelector.SelectAsync(catalog, _preferredAlias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken)", source);
        Assert.Contains("FoundryModelSelector.SelectAsync(catalog, alias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken)", source);
    }

    [Fact]
    public void Selector_ExcludesReasoningModelsAndModelsThatDoNotFitMemory()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryModelSelector.cs"));

        // Reasoning / chain-of-thought models are dropped from AUTO selection — the "phi-4-mini"
        // preference fragment must not accidentally pick "phi-4-mini-reasoning".
        Assert.Contains("ReasoningAliasFragments", source);
        Assert.Contains("\"reasoning\"", source);
        Assert.Contains("IsAutoSelectable", source);
        Assert.Contains("!IsReasoningAlias(c.Alias)", source);

        // Memory guard: SelectAlias takes a budget and drops models whose weights + headroom won't fit,
        // so a too-large model is not auto-selected (it would load then OOM during generation). The
        // SCALED-headroom math is extracted to the pure, unit-tested ModelMemoryBudget; the selector
        // just delegates to it (this file pulls in Foundry types and cannot itself be unit-tested).
        Assert.Contains("SelectAlias(IReadOnlyList<ModelCandidate> candidates, int? availableMemoryMb", source);
        Assert.Contains("ModelMemoryBudget.Fits(candidate.FileSizeMb, availableMemoryMb)", source);
        Assert.Contains("SelectAlias(candidates, availableMemoryMb)", source);
    }

    [Fact]
    public void Translator_CapsAutoSelectionByAvailableMemoryOnCpuOnlyMachines()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

        // The budget applies only to CPU inference (null on accelerated machines, where the model runs
        // in device memory), and reads available physical RAM. An integrated GPU is treated as CPU-only
        // here (it is excluded from inference), so it correctly GETS the RAM budget.
        Assert.Contains("private int? AvailableMemoryBudgetMb()", source);
        Assert.Contains("if (GpuInferencePolicy.CanUseGpuForInference(_hasGpu, _gpuMemoryBytes) || _hasNpu) return null;", source);
        Assert.Contains("GlobalMemoryStatusEx(ref status)", source);
        Assert.Contains("status.ullAvailPhys", source);
    }

    [Fact]
    public void Interface_ExposesSetAvailableAccelerators()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs"));
        Assert.Contains("void SetAvailableAccelerators(bool hasGpu, bool hasNpu);", source);
    }

    [Fact]
    public void Gui_And_Cli_TellTranslatorTheDetectedHardware()
    {
        string vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        string cli = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("_semanticTranslator.SetAvailableAccelerators(_semanticHasGpu, _semanticHasNpu);", vm);
        // CLI centralizes detection in one helper that both semantic entry points invoke.
        Assert.Contains("translator.SetAvailableAccelerators(hasGpu, hasNpu);", cli);
        Assert.Contains("ApplyDetectedAccelerators(translator);", cli);
    }

    [Fact]
    public void Translator_UpgradesToLargerModelOnAmpleGpuVram()
    {
        string translator = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));
        string iface = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs"));
        string vm = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        string cli = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Interface + translator expose a VRAM setter and a VRAM-budget helper.
        Assert.Contains("void SetGpuMemoryBytes(long dedicatedVideoMemoryBytes);", iface);
        Assert.Contains("public void SetGpuMemoryBytes(long dedicatedVideoMemoryBytes)", translator);
        Assert.Contains("private int AvailableVramBudgetMb()", translator);
        Assert.Contains("if (!_hasGpu || _gpuMemoryBytes <= 0) return 0;", translator);

        // Auto-select (no user override) upgrades to the policy's larger model when VRAM allows, then
        // ALWAYS falls back to normal auto-selection (so an unavailable upgrade never yields null).
        Assert.Contains("HighAccuracyModelPolicy.UpgradeAliasFor(AvailableVramBudgetMb())", translator);
        Assert.Contains("string.IsNullOrWhiteSpace(_preferredAlias)", translator);
        Assert.Contains("model ??= await FoundryModelSelector.SelectAsync(catalog, _preferredAlias, _deviceOrder, AvailableDevices(), AvailableMemoryBudgetMb(), cancellationToken)", translator);

        // GUI + CLI feed detected dedicated VRAM to the translator.
        Assert.Contains("_semanticTranslator.SetGpuMemoryBytes(SafeDetectGpuMemoryBytes());", vm);
        Assert.Contains("translator.SetGpuMemoryBytes(gpuMemoryBytes);", cli);
    }

    [Fact]
    public void Selector_ExcludesNonChatModelsByAliasNotJustTask()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryModelSelector.cs"));

        // Whisper regression: on a CPU-only, low-RAM box the whisper-tiny build reports no catalog Task,
        // so a task-only screen let it pass as "usable" and it was auto-selected as the smallest model —
        // then failed because whisper is ASR with a 448-token context vs the ~8.5K-token prompt. The
        // modality exclusion must ALSO screen the alias/id, where the "whisper" token always appears.
        Assert.Contains("\"whisper\"", source);
        Assert.Contains("ExcludedTaskFragments.Any(f => alias.Contains(f, StringComparison.Ordinal))", source);

        // The last-ditch fallback must NEVER re-select a task-incompatible model. When nothing is
        // text-chat-capable the selector returns null so the caller reports "no compatible model".
        Assert.DoesNotContain("candidates.ToList(); // fall back to anything compatible", source);
        Assert.Contains("refusing to fall back to a non-chat model", source);

        // Alias-aware helper the model picker uses to screen the catalog list.
        Assert.Contains("public static bool IsTextChatModel(string? alias, string? task)", source);
    }

    [Fact]
    public void Picker_ScreensModelListByAliasAndTask()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

        // The Settings model picker must screen by alias AND task so whisper/embedding models (whose
        // catalog Task is often unset) never appear, and must not re-add every model when the filtered
        // list is empty (that is what surfaced whisper).
        Assert.Contains("FoundryModelSelector.IsTextChatModel(AliasOf(m), m.Info?.Task)", source);
        Assert.DoesNotContain("usable = models.Where(m => m is not null).Select(m => m!).ToList();", source);
    }

    [Fact]
    public void Translator_CondensesPromptOnlyUnderExtremeMemoryPressure()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));

        // The condense-gating + {{TODAY}} substitution now live in the pure, unit-tested
        // SemanticPromptText.BuildSystemPrompt; the translator only supplies the live inputs (template,
        // today, the CPU-only memory budget, and the threshold). Below the threshold it condenses;
        // otherwise (null budget on accelerated hardware, or ample RAM) it sends the identical full prompt.
        Assert.Contains("PromptCondenseMemoryThresholdMb", source);
        Assert.Contains("SemanticPromptText.BuildSystemPrompt(", source);
        Assert.Contains("AvailableMemoryBudgetMb(),", source);
        Assert.Contains("PromptCondenseMemoryThresholdMb);", source);
    }

    [Fact]
    public void Picker_FlagsModelsTooLargeForAvailableMemory()
    {
        string translator = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs"));
        string dialog = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "SemanticModelDownloadDialog.cs"));
        string contract = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs"));

        // The option carries a memory-fit flag computed from the CPU-only budget + scaled headroom, so a
        // model that can't load (e.g. a 12 GB model on a 4 GB machine) is flagged rather than silently
        // selectable.
        Assert.Contains("bool ExceedsAvailableMemory", contract);
        Assert.Contains("int? memBudgetMb = AvailableMemoryBudgetMb();", translator);
        Assert.Contains("ExceedsAvailableMemory = memBudgetMb is { } budget && sizeMb is { } sz && !ModelMemoryBudget.Fits(sz, budget)", translator);

        // The picker dialog renders a visible warning on such options.
        Assert.Contains("option.ExceedsAvailableMemory", dialog);
        Assert.Contains("Too large for this PC's memory", dialog);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
