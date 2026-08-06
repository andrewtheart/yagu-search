namespace Yagu.Helpers;

/// <summary>
/// Pure helpers for the persisted, user-editable <see cref="QuickSearchItem"/> list: the first-run seed
/// (built from the curated <see cref="QuickSearchPresets"/> catalog so both stay one source of truth),
/// canonicalization on load, and the add/edit/delete/reorder operations the Quick searches tab performs.
/// Kept free of UI types so the list semantics are unit-testable.
/// </summary>
public static class QuickSearchCatalog
{
    /// <summary>
    /// Presentation for each built-in preset, keyed by <see cref="QuickSearchPreset.Key"/>. The canonical
    /// code-annotation search is deliberately absent: it stays a fixed action on the tab because it is the
    /// GUI twin of the CLI <c>--todos</c> flag, so it is not part of the user-managed list.
    /// </summary>
    private static readonly (string Key, string Label, string Glyph, string Tooltip)[] BuiltInPresentation =
    [
        ("merge-conflicts", "Merge conflict markers", "\uE8AB",
            "Find unresolved Git merge-conflict markers left in files after a bad merge."),
        ("debug-output", "Leftover debug output", "\uEBE8",
            "Find leftover debug output \u2014 console.log, print(), printf, System.out.print, Debug.Write/Log, fmt.Print."),
        ("secrets", "Possible secrets / credentials", "\uE72E",
            "First-pass scan for hardcoded credentials \u2014 assignments to api key, secret, password, token, access key, or connection string."),
        ("urls", "URLs / links", "\uE71B",
            "Find every http(s) URL \u2014 handy for auditing endpoints and links."),
        ("emails", "Email addresses", "\uE715",
            "Find email addresses in the files."),
        ("empty-catch", "Empty catch blocks", "\uE7BA",
            "Find empty catch blocks that silently swallow exceptions."),
        ("deprecated", "Deprecated / obsolete markers", "\uE81C",
            "Find APIs marked deprecated or obsolete \u2014 @deprecated, [Obsolete], @Deprecated."),
        ("guids", "GUIDs / UUIDs", "\uE8AC",
            "Find GUIDs / UUIDs in the files."),
    ];

    /// <summary>The first-run list: every built-in preset, in catalog order.</summary>
    public static List<QuickSearchItem> Defaults()
    {
        var items = new List<QuickSearchItem>(BuiltInPresentation.Length);
        foreach (var (key, label, glyph, tooltip) in BuiltInPresentation)
        {
            if (QuickSearchPresets.Find(key) is not { } preset)
                continue;
            items.Add(new QuickSearchItem
            {
                Id = key,
                Label = label,
                Glyph = glyph,
                Tooltip = tooltip,
                Pattern = preset.Pattern,
                UseRegex = true,
                CaseSensitive = preset.CaseSensitive,
                Multiline = false,
                ExactMatch = false,
                Semantic = false,
            });
        }
        return items;
    }

    /// <summary>
    /// Canonicalizes a persisted list: trims text, drops entries with no pattern, restores a blank label or
    /// glyph, and repairs missing/duplicate ids. An empty result is honored (the user may delete them all) —
    /// seeding the defaults is a separate first-run decision, not something load-time repair re-applies.
    /// </summary>
    public static List<QuickSearchItem> Normalize(IEnumerable<QuickSearchItem>? items)
    {
        var result = new List<QuickSearchItem>();
        if (items is null)
            return result;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null)
                continue;

            string pattern = (item.Pattern ?? string.Empty).Trim();
            if (pattern.Length == 0)
                continue;

            string label = (item.Label ?? string.Empty).Trim();
            if (label.Length == 0)
                label = pattern;

            string glyph = (item.Glyph ?? string.Empty).Trim();
            if (glyph.Length == 0)
                glyph = QuickSearchItem.DefaultGlyph;

            string id = (item.Id ?? string.Empty).Trim();
            if (id.Length == 0 || !seenIds.Add(id))
            {
                id = NewId();
                seenIds.Add(id);
            }

            result.Add(new QuickSearchItem
            {
                Id = id,
                Label = label,
                Glyph = glyph,
                Tooltip = (item.Tooltip ?? string.Empty).Trim(),
                Pattern = pattern,
                Directory = (item.Directory ?? string.Empty).Trim(),
                // Multiline requires regex, mirroring the search box's own Regex/Multiline coupling.
                UseRegex = item.UseRegex || item.Multiline,
                CaseSensitive = item.CaseSensitive,
                Multiline = item.Multiline,
                ExactMatch = item.ExactMatch,
                Semantic = item.Semantic,
                Options = item.Options?.Normalized(),
            });
        }

        return result;
    }

    /// <summary>Generates an id for a newly added item.</summary>
    public static string NewId() => "qs-" + Guid.NewGuid().ToString("N");

    /// <summary>Finds the index of <paramref name="id"/>, or -1.</summary>
    public static int IndexOf(IReadOnlyList<QuickSearchItem>? items, string? id)
    {
        if (items is null || string.IsNullOrEmpty(id))
            return -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i]?.Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Moves the item <paramref name="delta"/> places (negative = earlier). Clamped at the ends, so a
    /// move off either edge is a no-op rather than an error. Returns true when the order changed.
    /// </summary>
    public static bool Move(List<QuickSearchItem>? items, string? id, int delta)
    {
        if (items is null || delta == 0)
            return false;

        int index = IndexOf(items, id);
        if (index < 0)
            return false;

        int target = Math.Clamp(index + delta, 0, items.Count - 1);
        if (target == index)
            return false;

        var item = items[index];
        items.RemoveAt(index);
        items.Insert(target, item);
        return true;
    }

    /// <summary>Removes the item with the given id. Returns true when one was removed.</summary>
    public static bool Remove(List<QuickSearchItem>? items, string? id)
    {
        int index = IndexOf(items, id);
        if (index < 0)
            return false;
        items!.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Replaces the item carrying <paramref name="edited"/>'s id, or appends it when the id is new.
    /// Returns false when the edit carries no pattern (the one field an item cannot go without).
    /// </summary>
    public static bool Upsert(List<QuickSearchItem>? items, QuickSearchItem? edited)
    {
        if (items is null || edited is null)
            return false;
        if (string.IsNullOrWhiteSpace(edited.Pattern))
            return false;

        var normalized = Normalize(new[] { edited });
        if (normalized.Count == 0)
            return false;

        var value = normalized[0];
        value.Id = string.IsNullOrWhiteSpace(edited.Id) ? NewId() : edited.Id.Trim();

        int index = IndexOf(items, value.Id);
        if (index >= 0)
            items[index] = value;
        else
            items.Add(value);
        return true;
    }
}
