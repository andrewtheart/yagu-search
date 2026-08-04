using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services.Ai;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Semantic (natural-language) search mode: the mode toggle and its persisted default, model
/// selection/aliases, device-preference order, the translating-in-progress state, and every
/// label/visibility the search bar binds to when it switches between traditional and AI mode.
/// </summary>
public sealed partial class MainViewModel
{
    // ── Semantic search (Foundry Local) ──
    /// <summary>True when the search bar is in natural-language (Semantic) mode rather than the
    /// traditional literal/regex mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTraditionalQueryMode))]
    [NotifyPropertyChangedFor(nameof(QueryPlaceholderText))]
    [NotifyPropertyChangedFor(nameof(InlineSearchTogglesVisibility))]
    [NotifyPropertyChangedFor(nameof(QueryModeLabel))]
    [NotifyPropertyChangedFor(nameof(QueryModeGlyph))]
    public partial bool IsSemanticQueryMode { get; set; }

    /// <summary>Inverse of <see cref="IsSemanticQueryMode"/> for binding the Traditional toggle.</summary>
    public bool IsTraditionalQueryMode => !IsSemanticQueryMode;

    /// <summary>Whether the Semantic toggle is offered at all (feature enabled in settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticDefaultOverrideEnabled))]
    public partial bool SemanticSearchAvailable { get; set; }

    /// <summary>True when the machine has a GPU/NPU accelerator capable of running a Semantic model.
    /// Drives the launch-mode default and gates the Settings override.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticDefaultOverrideEnabled))]
    public partial bool SemanticHardwareAccelerated { get; set; }

    /// <summary>User override: when true, default the search bar to Traditional even on accelerated
    /// machines. Bound to the Settings toggle; only editable when <see cref="SemanticDefaultOverrideEnabled"/>.</summary>
    [ObservableProperty]
    public partial bool DefaultToTraditionalSearchMode { get; set; }

    /// <summary>The model alias override the user has chosen (empty = automatic recommended pick).
    /// Mirrors <c>AppSettings.SemanticModelAlias</c>; updated when a model is chosen or reset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSemanticModelDisplay))]
    [NotifyPropertyChangedFor(nameof(HasSemanticModelOverride))]
    public partial string SemanticModelAlias { get; set; } = string.Empty;

    /// <summary>Friendly description of the model currently selected for semantic translation. Shows a
    /// pinned override by name, else the actually-loaded automatic model ("phi-4 (automatic)") when one
    /// is loaded, else a generic "Automatic" label until the first search (or a Refresh) resolves it.</summary>
    public string CurrentSemanticModelDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SemanticModelAlias))
                return SemanticModelAlias;
            string? loaded = (_semanticTranslator as FoundryLocalSemanticQueryTranslator)?.SelectedModelAlias;
            return string.IsNullOrWhiteSpace(loaded)
                ? "Automatic (recommended for your hardware)"
                : $"{loaded} (automatic)";
        }
    }

    /// <summary>Whether the user has pinned a specific model rather than using automatic selection.</summary>
    public bool HasSemanticModelOverride => !string.IsNullOrWhiteSpace(SemanticModelAlias);

    /// <summary>Preferred accelerator order (e.g. "GPU,NPU,CPU") for running the AI model. Applied
    /// live to the translator and persisted.</summary>
    [ObservableProperty]
    public partial string SemanticDevicePreferenceOrder { get; set; } = "GPU,NPU,CPU";

    /// <summary>When true (default), Yagu checks the Foundry Local catalog about once a day and alerts
    /// the user when a new/updated/variant on-device model becomes available. Bound to the AI settings
    /// tab toggle and the alert modal's "Don't alert me again" option.</summary>
    [ObservableProperty]
    public partial bool FoundryModelUpdateAlertsEnabled { get; set; } = true;

    /// <summary>When true (default), the on-device semantic model is unloaded from memory (freeing GPU
    /// VRAM) right after each AI-search translation finishes; the next query reloads it. Set false to keep
    /// the model resident for the fastest repeat queries. Bound to the AI settings tab toggle, applied live
    /// to the translator, and persisted.</summary>
    [ObservableProperty]
    public partial bool SemanticUnloadModelAfterUse { get; set; } = true;

    /// <summary>True when a real GPU was detected (read-only info for the AI settings tab).</summary>
    public bool SemanticHasGpu => _semanticHasGpu;

    /// <summary>True when an NPU was detected (read-only info for the AI settings tab).</summary>
    public bool SemanticHasNpu => _semanticHasNpu;

    /// <summary>The Settings override is editable only when Semantic search is offered AND the machine
    /// has a supported accelerator; otherwise it is greyed out and unset (Traditional is forced anyway).</summary>
    public bool SemanticDefaultOverrideEnabled => SemanticSearchAvailable && SemanticHardwareAccelerated;

    /// <summary>True while a natural-language query is being translated by the local model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticStatusBarVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchModeSplitButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchActionButtonVisibility))]
    public partial bool IsTranslatingSemanticQuery { get; set; }

    partial void OnIsTranslatingSemanticQueryChanged(bool value)
    {
        // When the AI translation step ends, clear the "Canceling.." state — unless a real file scan is
        // still running (a normal semantic run transitions translation → scan; a cancelled one ends both).
        if (!value && !IsSearching) IsCancelling = false;
    }

    /// <summary>Status/progress line shown next to the mode toggle during translation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SemanticStatusBarVisibility))]
    public partial string SemanticStatusText { get; set; } = string.Empty;

    /// <summary>Whether a semantic model has already been downloaded (skip the first-run prompt).</summary>
    public bool IsSemanticModelDownloaded => _settings.SemanticModelDownloaded;

    /// <summary>Short label for the single query-mode dropdown button.</summary>
    public string QueryModeLabel => IsSemanticQueryMode ? "Semantic" : "Traditional";

    /// <summary>Segoe icon glyph for the single query-mode dropdown button.</summary>
    public string QueryModeGlyph => IsSemanticQueryMode ? "\uF4A5" : "\uE721";

    /// <summary>Visibility of the Traditional|Semantic mode bar (feature-gated).</summary>
    public Microsoft.UI.Xaml.Visibility SemanticModeBarVisibility =>
        SemanticSearchAvailable ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The search button is a SplitButton (with a chevron mode picker) only while semantic
    /// search is available AND fully idle. As soon as a search starts — including the semantic
    /// translation phase — it is replaced by the morphing Cancel button so the user can't fire a
    /// second concurrent run (which would corrupt the local model's in-flight inference).</summary>
    public Microsoft.UI.Xaml.Visibility SearchModeSplitButtonVisibility =>
        SemanticSearchAvailable && !IsSearching && !IsPreparingSearch && !IsTranslatingSemanticQuery
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The plain Search/Cancel button is shown when semantic search is unavailable (no mode
    /// chevron) or whenever a search is running — including the semantic translation phase — so it
    /// can morph into the red Cancel action the moment the user clicks Search.</summary>
    public Microsoft.UI.Xaml.Visibility SearchActionButtonVisibility =>
        !SemanticSearchAvailable || IsSearching || IsPreparingSearch || IsTranslatingSemanticQuery
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Visibility of the translation status line — only while translating or when a result
    /// explanation is showing.</summary>
    public Microsoft.UI.Xaml.Visibility SemanticStatusBarVisibility =>
        SemanticSearchAvailable && (IsTranslatingSemanticQuery || !string.IsNullOrEmpty(SemanticStatusText))
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The Case/Regex/Exact inline toggles only apply in Traditional mode.</summary>
    public Microsoft.UI.Xaml.Visibility InlineSearchTogglesVisibility =>
        IsSemanticQueryMode ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Placeholder text that adapts to the current query mode.</summary>
    public string QueryPlaceholderText => IsSemanticQueryMode
        ? "Describe what to find — e.g. \"png files on C: modified in the past year, ignore mov files\""
        : "Search query (Enter to run)";

    partial void OnSemanticSearchAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(SemanticModeBarVisibility));
        OnPropertyChanged(nameof(SearchModeSplitButtonVisibility));
        OnPropertyChanged(nameof(SearchActionButtonVisibility));
        if (!_queryModeInitialized) return;
        // The AI-search toggle: persist, flip the translator live, and leave Semantic mode if turning off.
        _settings.SemanticSearchEnabled = value;
        _semanticTranslator?.SetEnabled(value);
        if (!value) IsSemanticQueryMode = false;
        _ = PersistSettingsAsync();
    }

    partial void OnSemanticDevicePreferenceOrderChanged(string value)
    {
        if (!_queryModeInitialized) return;
        _settings.SemanticDevicePreferenceOrder = value;
        _semanticTranslator?.SetDevicePreferenceOrder(value);
        _ = PersistSettingsAsync();
    }

    partial void OnFoundryModelUpdateAlertsEnabledChanged(bool value)
    {
        if (!_queryModeInitialized) return;
        _settings.FoundryModelUpdateAlertsEnabled = value;
        _ = PersistSettingsAsync();
    }

    partial void OnSemanticUnloadModelAfterUseChanged(bool value)
    {
        if (!_queryModeInitialized) return;
        _settings.SemanticUnloadModelAfterUse = value;
        _semanticTranslator?.SetUnloadAfterUse(value);
        _ = PersistSettingsAsync();
    }

    partial void OnIsSemanticQueryModeChanged(bool value)
    {
        if (!value) SemanticStatusText = string.Empty;
        // The inline calculator only applies to the literal (Traditional) query box.
        UpdateInlineCalculatorResult(Query);
        if (!_queryModeInitialized) return;
        _settings.LastQueryModeIsSemantic = value;
        _settings.HasChosenQueryMode = true;
        _ = PersistSettingsAsync();
    }

    partial void OnDefaultToTraditionalSearchModeChanged(bool value)
    {
        if (!_queryModeInitialized) return;
        _settings.DefaultToTraditionalSearchMode = value;
        // Re-evaluate the launch default only when the user hasn't already pinned a mode this
        // session; respecting an explicit choice avoids yanking the toggle out from under them.
        if (!_settings.HasChosenQueryMode)
            IsSemanticQueryMode = ResolveLaunchQueryMode();
        _ = PersistSettingsAsync();
    }

    /// <summary>Resolves the search bar's launch mode. An explicit prior choice wins; otherwise the
    /// hardware-based default applies (Semantic when accelerated and not overridden, else Traditional).</summary>
    private bool ResolveLaunchQueryMode()
    {
        if (!SemanticSearchAvailable) return false;
        if (_settings.HasChosenQueryMode)
            return _settings.LastQueryModeIsSemantic && SemanticHardwareAccelerated;
        return SemanticHardwareAccelerated && !_settings.DefaultToTraditionalSearchMode;
    }

    /// <summary>Detects accelerated hardware without ever letting a detector fault break startup.</summary>
    private bool SafeDetectAcceleratedHardware()
    {
        try { return _capabilityDetector.HasAcceleratedHardware(); }
        catch (Exception ex)
        {
            YaguLog.For("Capability").LogWarning(ex, "Accelerated-hardware detection failed → assuming no acceleration.");
            return false;
        }
    }

    /// <summary>Runs a capability probe, swallowing any fault as "not present" so startup never breaks.</summary>
    private static bool SafeDetect(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            YaguLog.For("Capability").LogWarning(ex, "A capability probe failed → treating the capability as unavailable.");
            return false;
        }
    }

    /// <summary>Reads the machine's dedicated GPU VRAM (bytes) for the larger-model auto-upgrade
    /// decision, swallowing any fault as 0 (unknown) so startup never breaks.</summary>
    private long SafeDetectGpuMemoryBytes()
    {
        try { return _capabilityDetector.GetMaxDedicatedGpuMemoryBytes(); }
        catch (Exception ex)
        {
            YaguLog.For("Capability").LogWarning(ex, "GPU VRAM detection failed → treating available VRAM as unknown (0).");
            return 0;
        }
    }
}
