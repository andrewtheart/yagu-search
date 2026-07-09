namespace Yagu.Helpers;

/// <summary>
/// How multiple popped-out editor/preview windows auto-arrange on screen. Modeled on the
/// MultiTerm workbench layout modes (auto grid, fixed columns/rows, plus free placement).
/// </summary>
internal enum PopOutArrangement
{
    /// <summary>Balanced auto grid: cols = ceil(sqrt(n)), rows = ceil(n/cols); a short final row
    /// stretches to fill the width (so 3 windows = 2 on top, 1 wide below).</summary>
    Grid = 0,
    /// <summary>Side-by-side vertical strips (n columns, 1 row).</summary>
    Columns = 1,
    /// <summary>Stacked horizontal strips (1 column, n rows).</summary>
    Rows = 2,
    /// <summary>Overlapping windows offset diagonally (no tiling).</summary>
    Cascade = 3,
    /// <summary>Free placement — Yagu never moves the windows.</summary>
    Manual = 4,
}

/// <summary>A screen rectangle in physical pixels.</summary>
internal readonly record struct TileRect(int X, int Y, int Width, int Height);

/// <summary>
/// Pure geometry for auto-tiling N pop-out windows into a monitor work area. WinUI-free so it can
/// be unit-tested; the window class just applies the returned rectangles via
/// <c>AppWindow.MoveAndResize</c>.
/// </summary>
internal static class PopOutTileLayout
{
    /// <summary>True for arrangements that reposition/resize windows into a tiled grid.</summary>
    public static bool IsTiling(PopOutArrangement mode)
        => mode is PopOutArrangement.Grid or PopOutArrangement.Columns or PopOutArrangement.Rows;

    /// <summary>
    /// Computes one rectangle per window (index order) that tiles <paramref name="count"/> windows
    /// into <paramref name="work"/>. Returns an empty array for non-tiling modes or a non-positive
    /// count. A short final row is stretched to fill the full width.
    /// </summary>
    public static TileRect[] Compute(int count, PopOutArrangement mode, TileRect work, int gap = 8)
    {
        if (count <= 0 || !IsTiling(mode) || work.Width <= 0 || work.Height <= 0)
            return Array.Empty<TileRect>();

        if (gap < 0) gap = 0;

        (int cols, int rows) = ResolveGrid(count, mode);
        var result = new TileRect[count];

        int cellH = Math.Max(1, (work.Height - gap * (rows - 1)) / rows);

        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;

            // Items actually present in this row (the last row may be short); stretch them to fill
            // the full width so there is never an empty gap.
            int itemsInRow = Math.Min(cols, count - row * cols);
            int cellW = Math.Max(1, (work.Width - gap * (itemsInRow - 1)) / itemsInRow);

            int x = work.X + col * (cellW + gap);
            int y = work.Y + row * (cellH + gap);

            result[i] = new TileRect(x, y, cellW, cellH);
        }

        return result;
    }

    private static (int cols, int rows) ResolveGrid(int count, PopOutArrangement mode)
    {
        switch (mode)
        {
            case PopOutArrangement.Columns:
                return (count, 1);
            case PopOutArrangement.Rows:
                return (1, count);
            default: // Grid
                int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
                int rows = Math.Max(1, (int)Math.Ceiling(count / (double)cols));
                return (cols, rows);
        }
    }

    /// <summary>Maps a persisted 0-based settings index to a <see cref="PopOutArrangement"/>.</summary>
    public static PopOutArrangement FromIndex(int index) => index switch
    {
        1 => PopOutArrangement.Columns,
        2 => PopOutArrangement.Rows,
        3 => PopOutArrangement.Cascade,
        4 => PopOutArrangement.Manual,
        _ => PopOutArrangement.Grid,
    };
}
