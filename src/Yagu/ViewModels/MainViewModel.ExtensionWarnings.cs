using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// The "your search skipped this extension" warning: detecting when the query's file type is
/// excluded by the skip/binary/archive lists and re-including it for the next search.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Returns the predicted excluded-extension warning for the current query and advanced options, or
    /// null when there is nothing to warn about (the query does not name a file whose extension is
    /// currently excluded). Does NOT consider <see cref="SuppressExcludedExtensionWarnings"/> — the caller
    /// decides whether to SHOW the warning or silently apply the remembered default action
    /// (<see cref="IncludeExcludedExtensionByDefault"/>), both of which still need this warning's data.
    /// </summary>
    internal ExcludedExtensionWarning? TryGetExcludedExtensionWarning()
    {
        // Note: this runs AFTER semantic translation (via the SubmitSearchAsync gate), so in Semantic
        // mode Query/IncludeGlobs already reflect the model's resolved plan (e.g. include glob *.exe).

        // Archive universe = every known archive type (saved defaults + whatever is active). Contents are
        // only "searched" for the active archive types when Search-inside-archives is on.
        var archiveUniverse = ParseExtensionSet(SettingsArchiveExtensions);
        foreach (var ext in ParseExtensionSet(ArchiveExtensions))
            archiveUniverse.Add(ext);
        var archiveSearched = SearchInsideArchives
            ? ParseExtensionSet(ArchiveExtensions)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return ExcludedExtensionPredictor.Predict(
            Query,
            UseRegex,
            ExactMatch,
            (SearchMode)SearchModeIndex,
            ParseExtensionSet(SkipExtensions),
            ParseExtensionSet(BinaryExtensions),
            IncludeGlobs,
            IncludeFilterMode,
            EffectiveExcludeGlobsText,
            ExcludeFilterMode,
            archiveUniverse,
            archiveSearched);
    }

    /// <summary>
    /// Makes <paramref name="warning"/>'s extension findable for the CURRENT search only, by adjusting
    /// the offending Advanced Options list(s) transiently — nothing is written to the saved settings, and
    /// every control is reset back to the saved defaults once the search finishes (see
    /// <see cref="ResetAdvancedOptionsToSavedDefaults"/>). The rule per list:
    /// <list type="bullet">
    /// <item>Skip: keep skipping everything EXCEPT this extension (so only it is scanned).</item>
    /// <item>Binary: turn on binary search and select ONLY this binary type (skip every other binary type).</item>
    /// <item>Archive: turn on archive search and select ONLY this archive type.</item>
    /// <item>Include/Exclude filter: edit the session-only filter so the extension is no longer filtered out.</item>
    /// </list>
    /// </summary>
    internal Task IncludeExtensionForSearchAsync(ExcludedExtensionWarning warning)
    {
        string ext = warning.Extension;

        // Mark that this search transiently changed Advanced Options so they are reset to the saved
        // defaults once the search finishes (see OnIsSearchingChanged / ResetAdvancedOptionsToSavedDefaults).
        _advancedOptionsTransientlyChanged = true;

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.BinaryExtensions))
            EnableBinarySearchForExtension(ext);

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.SkipExtensions))
            UnskipExtensionForSearch(ext);

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.ArchiveExtensions))
            EnableArchiveSearchForExtension(ext);

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.ExcludeFilter))
        {
            // EffectiveExcludeGlobsText may be the built-in default; materialize it minus the extension
            // into the editable (session) ExcludeGlobs so the file is no longer excluded.
            ExcludeGlobs = ExcludedExtensionPredictor.RemoveExtensionToken(EffectiveExcludeGlobsText, ext);
        }

        if (warning.Reasons.HasFlag(ExtensionExclusionReason.IncludeFilter))
        {
            // The restrictive Include filter omits this extension — add it so the file is included.
            IncludeGlobs = ExcludedExtensionPredictor.AppendExtensionToken(IncludeGlobs, ext);
        }

        // Deliberately NOT persisted: the change applies only to this search and is reverted afterward.
        return Task.CompletedTask;
    }

    /// <summary>Skip rule: stop skipping <paramref name="ext"/> (so it is scanned) while keeping every
    /// other skip-extension skipped. Session-only edit of the active Skip Extensions list.</summary>
    private void UnskipExtensionForSearch(string ext)
    {
        var universe = ParseExtensionSet(SettingsSkipExtensions);
        foreach (var e in ParseExtensionSet(SkipExtensions))
            universe.Add(e);
        universe.Remove(ext);

        var newSkip = string.Join(';', universe.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
        if (!string.Equals(SkipExtensions, newSkip, StringComparison.OrdinalIgnoreCase))
        {
            SkipExtensions = newSkip;
            SyncSkipExtensionItems();
        }
    }

    /// <summary>Binary rule: turn on binary search and SELECT ONLY <paramref name="ext"/> in the binary
    /// dropdown (every other binary type stays skipped). Session-only — internally BinaryExtensions is the
    /// skip list, so "select only ext" means "skip everything except ext".</summary>
    private void EnableBinarySearchForExtension(string ext)
    {
        if (!SearchBinary)
            SearchBinary = true;

        var universe = ParseExtensionSet(SettingsBinaryExtensions);
        foreach (var e in ParseExtensionSet(BinaryExtensions))
            universe.Add(e);
        universe.Add(ext);

        var newSkip = string.Join(';', universe.Where(e => !string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
        if (!string.Equals(BinaryExtensions, newSkip, StringComparison.OrdinalIgnoreCase))
        {
            BinaryExtensions = newSkip;
            SyncBinaryExtensionItems();
        }
    }

    /// <summary>Archive rule: turn on archive search and SELECT ONLY <paramref name="ext"/> in the archive
    /// dropdown (every other archive type disabled). Session-only edit of the active Archive list.</summary>
    private void EnableArchiveSearchForExtension(string ext)
    {
        if (!SearchInsideArchives)
            SearchInsideArchives = true;

        if (!string.Equals(ArchiveExtensions, ext, StringComparison.OrdinalIgnoreCase))
        {
            ArchiveExtensions = ext;
            SyncArchiveExtensionItems();
        }
    }
}
