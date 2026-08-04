using CommunityToolkit.Mvvm.ComponentModel;

namespace Yagu.ViewModels;

/// <summary>
/// Search-query inputs: the query/directory text, the matching toggles (case, regex, exact,
/// multiline), the last-run query echo used by the results view, the inline calculator, and the
/// code-annotation / quick-search presets that pre-fill the query box.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryIndexed))]
    [NotifyPropertyChangedFor(nameof(CurrentDirectoryIndexRoot))]
    public partial string Directory { get; set; } = string.Empty;
    [ObservableProperty] public partial string Query { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CaseSensitive { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial bool ExactMatch { get; set; } = true;

    /// <summary>When true, the query regex runs over the whole file so a single match can span line
    /// breaks (ripgrep <c>-U</c>). Strictly opt-in; initialized from <see cref="SettingsService"/>.</summary>
    [ObservableProperty] public partial bool Multiline { get; set; }

    /// <summary>When true and <see cref="Multiline"/> is on, <c>.</c> also matches newlines (dot-all).</summary>
    [ObservableProperty] public partial bool MultilineDotAll { get; set; }

    /// <summary>
    /// The regex toggle follows the multiline toggle: cross-line matching is only meaningful in regex
    /// mode (a plain literal is split on whitespace — newlines included — so it can never span a line
    /// break). Turning Multiline ON enables Regex; turning Multiline OFF disables Regex. Exact-match
    /// (whole-word) is the inverse: it is a single-token concept that regex overrides anyway, so it is
    /// unchecked while Multiline is on and restored when Multiline turns off.
    /// </summary>
    partial void OnMultilineChanged(bool value)
    {
        UseRegex = value;
        ExactMatch = !value;
    }

    /// <summary>The pattern + flags the MOST RECENT search actually ran with, captured at search
    /// start. For a semantic search these are the model's RESOLVED literal pattern and flags — not
    /// the natural-language box text (which stays in <see cref="Query"/> for display) nor the user
    /// defaults that the semantic run restores afterward. Preview/editor match highlighting reads
    /// these so it boxes exactly the matches the engine found, independent of later Query/flag drift.</summary>
    public string LastSearchPattern { get; private set; } = string.Empty;
    public bool LastSearchCaseSensitive { get; private set; }
    public bool LastSearchUseRegex { get; private set; }
    public bool LastSearchExactMatch { get; private set; } = true;
    public bool LastSearchMultiline { get; private set; }
    public bool LastSearchMultilineDotAll { get; private set; }

    public Microsoft.UI.Xaml.Visibility HasQueryText =>
        string.IsNullOrEmpty(Query) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQueryText));
        UpdateInlineCalculatorResult(value);
    }

    // ── Inline calculator / unit converter ──
    /// <summary>The formatted inline answer (e.g. <c>"5 km = 3.106856 miles"</c>) when the current
    /// query is a math expression or unit conversion; empty otherwise. Shown as a small banner below
    /// the search box so the user never has to leave Yagu for a quick calculation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineCalculatorResultVisibility))]
    public partial string InlineCalculatorResultText { get; set; } = string.Empty;

    /// <summary>Just the answer (no expression), for the banner's Copy button.</summary>
    public string InlineCalculatorCopyValue { get; private set; } = string.Empty;

    public Microsoft.UI.Xaml.Visibility InlineCalculatorResultVisibility =>
        string.IsNullOrEmpty(InlineCalculatorResultText)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    private void UpdateInlineCalculatorResult(string? query)
    {
        // Only Traditional mode has a literal query box; a natural-language request is never a sum.
        var result = IsSemanticQueryMode ? null : Yagu.Helpers.InlineCalculator.Evaluate(query);
        InlineCalculatorCopyValue = result?.Value ?? string.Empty;
        InlineCalculatorResultText = result?.Display ?? string.Empty;
    }

    /// <summary>Loads the canonical "find code annotations" search — a whole-word regex over
    /// TODO/FIXME/HACK/BUG/XXX/NOTE/OPTIMIZE/REVIEW — into the search box in Traditional regex mode.
    /// The caller submits the search afterwards.</summary>
    public void ApplyCodeAnnotationPreset()
    {
        IsSemanticQueryMode = false;
        UseRegex = true;
        CaseSensitive = true;
        ExactMatch = false;
        Query = Yagu.Helpers.CodeAnnotationQuery.Pattern;
    }

    /// <summary>Loads a curated developer "quick search" (see <see cref="Yagu.Helpers.QuickSearchPresets"/>)
    /// into the search box in Traditional regex mode. The caller submits the search afterwards.</summary>
    public void ApplyQuickSearchPreset(Yagu.Helpers.QuickSearchPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        IsSemanticQueryMode = false;
        UseRegex = true;
        ExactMatch = false;
        CaseSensitive = preset.CaseSensitive;
        Query = preset.Pattern;
    }
}
