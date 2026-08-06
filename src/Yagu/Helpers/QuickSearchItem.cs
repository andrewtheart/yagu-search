namespace Yagu.Helpers;

/// <summary>
/// A user-editable one-click search persisted in settings and rendered on the Advanced Options ▸
/// <b>Quick searches</b> tab. Unlike the built-in <see cref="QuickSearchPreset"/> catalog (which is only a
/// pattern + case flag), an item also carries its own presentation (label/glyph/tooltip) and the full
/// option set the search box exposes, so loading one restores exactly the search the user saved.
/// </summary>
public sealed class QuickSearchItem
{
    /// <summary>Search glyph, used when an item does not specify one.</summary>
    public const string DefaultGlyph = "\uE721";

    /// <summary>Stable identifier; generated when the user adds an item and never shown in the UI.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Button text.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Segoe Fluent Icons glyph shown left of <see cref="Label"/>.</summary>
    public string Glyph { get; set; } = DefaultGlyph;

    /// <summary>Hover description. Empty falls back to the pattern.</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>The query loaded into the search box.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Folder the search runs in. Empty means the search starts at the root of every drive, matching what
    /// an empty directory box does everywhere else in the app.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>True when the item searches every drive rather than one folder.</summary>
    public bool SearchesAllDrives => string.IsNullOrWhiteSpace(Directory);

    public bool UseRegex { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool Multiline { get; set; }
    public bool ExactMatch { get; set; }

    /// <summary>True runs the item as a natural-language Semantic search instead of Traditional.</summary>
    public bool Semantic { get; set; }

    /// <summary>
    /// Every Advanced Option as it was when the item was saved with "Save current options", or null for an
    /// item created from the tab's inline editor. Null means running the item leaves the drawer untouched;
    /// non-null restores the whole drawer. Only the four inline toggles above are editable in the UI — the
    /// rest are carried and replayed verbatim.
    /// </summary>
    public QuickSearchOptions? Options { get; set; }

    /// <summary>True when the item carries a full Advanced Options snapshot.</summary>
    public bool HasOptions => Options is not null;

    public QuickSearchItem Clone() => new()
    {
        Id = Id,
        Label = Label,
        Glyph = Glyph,
        Tooltip = Tooltip,
        Pattern = Pattern,
        Directory = Directory,
        UseRegex = UseRegex,
        CaseSensitive = CaseSensitive,
        Multiline = Multiline,
        ExactMatch = ExactMatch,
        Semantic = Semantic,
        Options = Options?.Clone(),
    };
}
