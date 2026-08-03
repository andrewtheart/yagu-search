namespace Yagu.Helpers;

/// <summary>
/// A one-click developer search: a regular expression plus the option flags to load into the search box.
/// Every preset is a plain Traditional-mode regex search (no special engine plumbing), so it reduces to
/// the same path a hand-typed regex would take.
/// </summary>
/// <param name="Key">Stable identifier used to wire the XAML button (its <c>Tag</c>) to this preset.</param>
/// <param name="Pattern">The regular expression loaded into the search box.</param>
/// <param name="CaseSensitive">Whether the search runs case-sensitively (some markers are exact API
/// names; others should match any casing).</param>
public sealed record QuickSearchPreset(string Key, string Pattern, bool CaseSensitive);

/// <summary>
/// The curated catalog of developer-focused "quick searches" surfaced on the Advanced Options ▸
/// <b>Quick searches</b> tab. Each entry is only a search <i>definition</i> (pattern + option flags);
/// its label, glyph, and tooltip live in the XAML button so presentation and behavior stay decoupled.
/// The canonical code-annotation preset reuses <see cref="CodeAnnotationQuery.Pattern"/> so the GUI, the
/// CLI <c>--todos</c> flag, and this catalog all share one source of truth.
/// </summary>
public static class QuickSearchPresets
{
    /// <summary>Key of the canonical TODO/FIXME code-annotation preset (also wired to the CLI --todos flag).</summary>
    public const string CodeAnnotationsKey = "code-annotations";

    /// <summary>Every quick search offered in the GUI, in display order.</summary>
    public static readonly IReadOnlyList<QuickSearchPreset> All =
    [
        // TODO / FIXME / HACK / BUG / XXX / NOTE / OPTIMIZE / REVIEW — shares the CLI --todos pattern.
        new QuickSearchPreset(CodeAnnotationsKey, CodeAnnotationQuery.Pattern, CaseSensitive: true),

        // Unresolved Git merge-conflict markers left in a file.
        new QuickSearchPreset("merge-conflicts", @"^(<{7}|={7}|>{7})(\s|$)", CaseSensitive: true),

        // Debug/print statements a developer may have forgotten to remove.
        new QuickSearchPreset(
            "debug-output",
            @"console\.(log|debug|warn|error)|System\.(out|err)\.print|Debug\.(Write|WriteLine|Log)|printf\s*\(|fmt\.Print|\bprint\s*\(",
            CaseSensitive: true),

        // First-pass scan for hardcoded credentials (assignment to a secret-looking name).
        new QuickSearchPreset(
            "secrets",
            @"\b(api[_-]?key|secret|passwo?rd|token|access[_-]?key|client[_-]?secret|connection[_-]?string)\b\s*[:=]",
            CaseSensitive: false),

        // http(s) URLs — auditing endpoints and links.
        new QuickSearchPreset("urls", @"https?://[^\s""'<>)]+", CaseSensitive: false),

        // Email addresses.
        new QuickSearchPreset("emails", @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", CaseSensitive: false),

        // Empty catch blocks that silently swallow errors.
        new QuickSearchPreset("empty-catch", @"catch\s*(\([^)]*\))?\s*\{\s*\}", CaseSensitive: false),

        // APIs marked deprecated / obsolete.
        new QuickSearchPreset("deprecated", @"\bdeprecated\b|\[Obsolete|@Deprecated", CaseSensitive: false),

        // GUIDs / UUIDs.
        new QuickSearchPreset(
            "guids",
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            CaseSensitive: false),
    ];

    /// <summary>Finds the preset with the given <paramref name="key"/>, or <c>null</c> if none matches.</summary>
    public static QuickSearchPreset? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(p => string.Equals(p.Key, key, System.StringComparison.Ordinal));
}
