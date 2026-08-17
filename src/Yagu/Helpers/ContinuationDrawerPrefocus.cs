namespace Yagu.Helpers;

/// <summary>
/// Picks which continuation drawer of the file being previewed should be painted "focused" while the
/// user scrolls. A long file's preview rolls over into sibling "continued" drawers, and only one drawer
/// carries the selected background; choosing it from a look-ahead band means the next drawer is already
/// highlighted by the time it scrolls into view.
/// </summary>
internal static class ContinuationDrawerPrefocus
{
    /// <summary>Vertical span of a drawer in the preview scroll viewer's content coordinates.</summary>
    internal readonly record struct DrawerExtent(double Top, double Bottom);

    /// <summary>
    /// Index into <paramref name="siblings"/> of the drawer to focus, or -1 when none is close enough yet.
    /// <paramref name="siblings"/> must be ordered walking away from the currently focused drawer in the
    /// scroll direction (so index 0 is its immediate neighbour).
    /// </summary>
    internal static int SelectDrawer(
        IReadOnlyList<DrawerExtent> siblings,
        double viewportTop,
        double viewportHeight,
        bool scrollingDown,
        double lookAheadViewports)
    {
        if (siblings.Count == 0 || viewportHeight <= 0)
            return -1;

        double viewportBottom = viewportTop + viewportHeight;
        double band = viewportHeight * Math.Max(0, lookAheadViewports);
        double probe = scrollingDown ? viewportBottom + band : viewportTop - band;

        for (int i = 0; i < siblings.Count; i++)
        {
            DrawerExtent drawer = siblings[i];
            if (drawer.Bottom <= drawer.Top)
                return -1; // unmeasured drawer: its position is unknown, so stop rather than guess

            if (scrollingDown)
            {
                if (drawer.Bottom <= viewportTop) continue; // a fast flick already carried past this one
                if (drawer.Top > probe) return -1;          // still beyond the look-ahead band
            }
            else
            {
                if (drawer.Top >= viewportBottom) continue;
                if (drawer.Bottom < probe) return -1;
            }

            return i;
        }

        return -1;
    }
}
