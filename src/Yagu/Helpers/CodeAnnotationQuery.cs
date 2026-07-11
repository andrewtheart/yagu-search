namespace Yagu.Helpers;

/// <summary>
/// Builds the canonical regular expression that finds code-annotation comments — the "TODO tree" a
/// developer keeps meaning to come back to. Both the UI quick-action and the CLI <c>--todos</c> flag
/// reduce to this one regex + regex mode, so the two surfaces stay in lock-step (and the search is a
/// plain regex search under the hood, no special engine plumbing).
/// </summary>
public static class CodeAnnotationQuery
{
    /// <summary>The annotation markers matched, in priority order. Uppercase by convention, which is
    /// how these tags are written in real source, so a case-sensitive search stays precise.</summary>
    public static readonly string[] Markers = ["TODO", "FIXME", "HACK", "BUG", "XXX", "NOTE", "OPTIMIZE", "REVIEW"];

    /// <summary>
    /// A whole-word, alternation regex over <see cref="Markers"/>, e.g.
    /// <c>\b(TODO|FIXME|HACK|BUG|XXX|NOTE|OPTIMIZE|REVIEW)\b</c>. Run in regex mode (case-sensitive
    /// recommended) to surface every outstanding annotation across a codebase.
    /// </summary>
    public static string Pattern { get; } = $@"\b({string.Join('|', Markers)})\b";
}
