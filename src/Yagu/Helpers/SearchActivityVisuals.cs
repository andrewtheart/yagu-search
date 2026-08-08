namespace Yagu.Helpers;

/// <summary>How the bottom-left activity glyph should look for a given search state.</summary>
internal readonly record struct SearchActivityVisual(string Glyph, double Opacity, string AutomationName, bool Animate);

/// <summary>Pure state and geometry behind the bottom-left search activity glyph.</summary>
internal static class SearchActivityVisuals
{
    internal const string SearchGlyph = "\uE721";
    internal const string CompletedGlyph = "\uE930";

    /// <summary>
    /// The glyph, opacity, accessible name, and whether to animate. "Complete" is sticky per session:
    /// it is only reported once a real scan has been observed, so a window that has never searched
    /// stays on the dimmed "Ready to search" magnifier instead of claiming a finished search.
    /// </summary>
    internal static SearchActivityVisual Resolve(bool busy, bool sawScan)
    {
        if (busy)
            return new SearchActivityVisual(SearchGlyph, 0.82, "Search in progress", Animate: true);

        return sawScan
            ? new SearchActivityVisual(CompletedGlyph, 1.0, "Search complete", Animate: false)
            : new SearchActivityVisual(SearchGlyph, 0.72, "Ready to search", Animate: false);
    }

    /// <summary>
    /// Keyframes of the figure eight the glyph traces while a search runs, as (seconds, x, y) offsets
    /// from its resting position. The first half loops right, the second half mirrors it through the
    /// origin to loop left, and both ends rest at the origin so the loop repeats seamlessly.
    /// </summary>
    internal static IReadOnlyList<(double Seconds, double X, double Y)> FigureEightKeyframes { get; } =
    [
        (0.0, 0, 0),
        (0.1, 2.1, -1.6),
        (0.2, 4, -2.2),
        (0.3, 5.1, -1.6),
        (0.4, 5.5, 0),
        (0.5, 5.1, 1.6),
        (0.6, 4, 2.2),
        (0.7, 2.1, 1.6),
        (0.8, 0, 0),
        (0.9, -2.1, 1.6),
        (1.0, -4, 2.2),
        (1.1, -5.1, 1.6),
        (1.2, -5.5, 0),
        (1.3, -5.1, -1.6),
        (1.4, -4, -2.2),
        (1.5, -2.1, -1.6),
        (1.6, 0, 0),
    ];
}
