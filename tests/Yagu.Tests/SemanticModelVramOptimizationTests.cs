using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the two on-device semantic-model VRAM optimizations, which live in
/// WinUI/Foundry-coupled files that cannot be exercised at runtime in the test host:
/// <list type="bullet">
/// <item>"Release model after use" — an AI-settings toggle that unloads the model from VRAM
/// (<see cref="Yagu.Services.Ai.ISemanticQueryTranslator.SetUnloadAfterUse"/> →
/// <c>IModel.UnloadAsync</c>) right after each translation finishes.</item>
/// <item>Context-window clamp — before loading, the model's over-large window is reduced to
/// <c>ModelContextBudget.OptimizedContextTokens</c> so its KV cache / accelerator buffers reserve
/// far less VRAM, re-applied on every load so a Foundry re-download is re-clamped.</item>
/// </list>
/// The pure clamp math and file editing are unit-tested in <see cref="GenAiConfigReaderTests"/> and
/// <see cref="ModelContextBudgetTests"/>; these pins guard the wiring across the interface, translator,
/// view model, settings, and persisted config.
/// </summary>
public sealed class SemanticModelVramOptimizationTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", Path.Combine(parts)));

    [Fact]
    public void UnloadAfterUse_IsExposedOnTheInterface()
    {
        string iface = Read("Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs");
        Assert.Contains("void SetUnloadAfterUse(bool unloadAfterUse);", iface);
    }

    [Fact]
    public void Translator_UnloadsModelAfterUseWhenEnabled()
    {
        string translator = Read("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        // Flag + setter.
        Assert.Contains("private bool _unloadAfterUse;", translator);
        Assert.Contains("public void SetUnloadAfterUse(bool unloadAfterUse)", translator);

        // The unload path uses the SDK's IModel.UnloadAsync and drops the cached references so the next
        // translation reloads.
        Assert.Contains("private async Task UnloadLoadedModelAfterUseAsync()", translator);
        Assert.Contains("await model.UnloadAsync().ConfigureAwait(false);", translator);

        // TranslateAsync releases the model in a finally so it happens on success, failure, watchdog, or
        // cancellation — but only when the setting is on.
        Assert.Contains("if (_unloadAfterUse)", translator);
        Assert.Contains("await UnloadLoadedModelAfterUseAsync().ConfigureAwait(false);", translator);
    }

    [Fact]
    public void UnloadCurrentModel_IsExposedOnInterfaceAndImplementedByTranslator()
    {
        string iface = Read("Yagu", "Services", "Ai", "ISemanticQueryTranslator.cs");
        string translator = Read("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        // On-demand eviction the qualification sweep uses to keep only one candidate model resident.
        Assert.Contains("Task UnloadCurrentModelAsync(CancellationToken cancellationToken);", iface);
        Assert.Contains("public Task UnloadCurrentModelAsync(CancellationToken cancellationToken) => UnloadLoadedModelAfterUseAsync();", translator);
    }

    [Fact]
    public void Translator_SerializesTeardownAgainstInFlightNativeOps()
    {
        // ROOT-CAUSE FIX for the deterministic onnxruntime-genai 0xc0000005 use-after-free: the Foundry
        // SDK does not guard IModel.UnloadAsync() against an in-flight native load/inference, so a managed-
        // "cancelled" but still-running (wedged) native op gets its model freed underneath it → crash. The
        // translator now tracks the last native op and makes every unload await it (bounded) before freeing;
        // a wedged op that won't drain SKIPS the unload and leaves the model resident (freed on process exit).
        string translator = Read("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        // Tracking field + bounded drain gate.
        Assert.Contains("private volatile Task _lastNativeModelOp", translator);
        Assert.Contains("private void TrackNativeModelOp(Task op)", translator);
        Assert.Contains("private async Task<bool> TryDrainNativeModelOpAsync()", translator);
        Assert.Contains("await Task.WhenAny(op, Task.Delay(UnloadDrainTimeout))", translator);

        // Every native op is tracked so the drain can wait on it (inference, load, streaming).
        Assert.Contains("TrackNativeModelOp(inferenceTask)", translator);
        Assert.Contains("TrackNativeModelOp(loadTask)", translator);
        Assert.Contains("TrackNativeModelOp(streamTask)", translator);

        // Both unload paths gate on the drain and SKIP (leave resident) rather than free under a running op.
        int drainCalls = System.Text.RegularExpressions.Regex.Matches(translator, @"TryDrainNativeModelOpAsync\(\)").Count;
        Assert.True(drainCalls >= 3, $"expected the drain guard at both unload sites plus its definition, found {drainCalls}");
        Assert.Contains("Skipping unload", translator);
    }

    [Fact]
    public void Translator_ClampsContextWindowBeforeLoadingToSaveVram()
    {
        string translator = Read("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        // The clamp is invoked BEFORE LoadAsync (which sizes the KV cache / activation buffers), and is
        // skipped for reasoning models (their long <think> output needs the full window).
        Assert.Contains("private async Task ClampModelContextWindowAsync(IModel model, bool isReasoning, CancellationToken cancellationToken)", translator);
        Assert.Contains("await ClampModelContextWindowAsync(model, willBeReasoning, cancellationToken).ConfigureAwait(false);", translator);
        Assert.Contains("GenAiConfigReader.TryClampContextWindow(", translator);
        Assert.Contains("ModelContextBudget.OptimizedContextTokens", translator);

        // Ordering: EnsureModelContextFits (checks the window is big enough) then the clamp, then load.
        int fits = translator.IndexOf("EnsureModelContextFits(model);", System.StringComparison.Ordinal);
        int clamp = translator.IndexOf("await ClampModelContextWindowAsync(", System.StringComparison.Ordinal);
        int load = translator.IndexOf("model.LoadAsync(", System.StringComparison.Ordinal);
        Assert.True(fits >= 0 && clamp > fits && load > clamp,
            $"Expected EnsureModelContextFits < ClampModelContextWindowAsync < LoadAsync (fits={fits}, clamp={clamp}, load={load}).");
    }

    [Fact]
    public void Translator_SettlesVramAfterUnloadBeforeTheNextLoad()
    {
        // Switch-wedge mitigation: after unloading a model, WDDM lags before reclaiming its VRAM, and
        // loading the next model (then its first inference) onto a still-near-full card wedges onnxruntime-
        // genai. The translator marks a settle-pending on unload and waits VramSettleAfterUnload before the
        // NEXT load — only on a switch, never the cold first load.
        string translator = Read("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        Assert.Contains("private static readonly TimeSpan VramSettleAfterUnload", translator);
        Assert.Contains("private volatile bool _vramSettlePending;", translator);
        Assert.Contains("_vramSettlePending = true;", translator);  // set right after UnloadAsync
        Assert.Contains("await Task.Delay(VramSettleAfterUnload, cancellationToken)", translator);
        Assert.Contains("_vramSettlePending = false;", translator); // cleared so it fires once per switch

        // The settle delay is paid BEFORE LoadAsync.
        int settle = translator.IndexOf("await Task.Delay(VramSettleAfterUnload", System.StringComparison.Ordinal);
        int load = translator.IndexOf("model.LoadAsync(", System.StringComparison.Ordinal);
        Assert.True(settle >= 0 && load > settle,
            $"Expected the VRAM settle delay before LoadAsync (settle={settle}, load={load}).");
    }

    [Fact]
    public void Translator_DiscoversRelocatedFoundryCacheAndClampsEveryCopy()
    {
        string translator = Read("Yagu", "Services", "Ai", "FoundryLocalSemanticQueryTranslator.cs");

        // Foundry Local's cache can be moved to another drive; its real location lives in
        // %USERPROFILE%\.foundry\foundry.config.json (cacheDirectoryPath). Yagu MUST read it so the clamp
        // and the context-fit guard target the copy the runtime actually loads — otherwise the model loads
        // at its full (e.g. 128K) window and reserves ~16 GB of KV cache plus gigabytes of host RAM.
        Assert.Contains("private static string? ReadFoundryConfiguredCacheDir()", translator);
        Assert.Contains("\".foundry\", \"foundry.config.json\"", translator);
        Assert.Contains("cacheDirectoryPath", translator);
        Assert.Contains("ReadFoundryConfiguredCacheDir();", translator);

        // The clamp shrinks EVERY on-disk copy of the variant (not just one), so a relocated copy that
        // GetPathAsync misses is still clamped.
        Assert.Contains("private async Task<IReadOnlyCollection<string>> ResolveModelDirectoriesAsync(", translator);
        Assert.Contains("foreach (string dir in modelDirs)", translator);
    }

    [Fact]
    public void ViewModel_MirrorsUnloadSettingToTranslatorAndPersists()
    {
        string vm = Read("Yagu", "ViewModels", "MainViewModel.cs");

        Assert.Contains("public partial bool SemanticUnloadModelAfterUse { get; set; }", vm);
        // Pushed to the translator at construction and on change; persisted.
        Assert.Contains("_semanticTranslator.SetUnloadAfterUse(_settings.SemanticUnloadModelAfterUse);", vm);
        Assert.Contains("partial void OnSemanticUnloadModelAfterUseChanged(bool value)", vm);
        Assert.Contains("_semanticTranslator?.SetUnloadAfterUse(value);", vm);
        Assert.Contains("_settings.SemanticUnloadModelAfterUse = SemanticUnloadModelAfterUse;", vm);
    }

    [Fact]
    public void Settings_ExposeReleaseAfterUseToggleOnAiTab()
    {
        string settings = Read("Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");

        Assert.Contains("AddSettingsGroupBox(g, \"GPU Memory\")", settings);
        Assert.Contains("Release model from memory after each search", settings);
        Assert.Contains("_viewModel.SemanticUnloadModelAfterUse = unloadToggle.IsOn;", settings);
    }

    [Fact]
    public void AppSettings_PersistsUnloadAfterUseFlag()
    {
        string settingsService = Read("Yagu", "Services", "SettingsService.cs");
        Assert.Contains("public bool SemanticUnloadModelAfterUse { get; set; }", settingsService);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
