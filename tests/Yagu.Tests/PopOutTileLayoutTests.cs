using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class PopOutTileLayoutTests
{
    private static readonly TileRect Work = new(0, 0, 1920, 1000);

    [Theory]
    [InlineData(0)] // Grid
    [InlineData(1)] // Columns
    [InlineData(2)] // Rows
    public void IsTiling_TrueForTilingModes(int modeIndex)
        => Assert.True(PopOutTileLayout.IsTiling(PopOutTileLayout.FromIndex(modeIndex)));

    [Theory]
    [InlineData(3)] // Cascade
    [InlineData(4)] // Manual
    public void IsTiling_FalseForNonTilingModes(int modeIndex)
        => Assert.False(PopOutTileLayout.IsTiling(PopOutTileLayout.FromIndex(modeIndex)));

    [Fact]
    public void Compute_NonTilingMode_ReturnsEmpty()
        => Assert.Empty(PopOutTileLayout.Compute(3, PopOutArrangement.Cascade, Work));

    [Fact]
    public void Compute_ZeroOrNegativeCount_ReturnsEmpty()
    {
        Assert.Empty(PopOutTileLayout.Compute(0, PopOutArrangement.Grid, Work));
        Assert.Empty(PopOutTileLayout.Compute(-2, PopOutArrangement.Grid, Work));
    }

    [Fact]
    public void Compute_SingleWindow_FillsWorkArea()
    {
        var tiles = PopOutTileLayout.Compute(1, PopOutArrangement.Grid, Work, gap: 8);
        Assert.Single(tiles);
        Assert.Equal(new TileRect(0, 0, 1920, 1000), tiles[0]);
    }

    [Fact]
    public void Compute_TwoWindows_Grid_SideBySide()
    {
        // ceil(sqrt(2)) = 2 cols, ceil(2/2) = 1 row -> side by side.
        var tiles = PopOutTileLayout.Compute(2, PopOutArrangement.Grid, Work, gap: 10);
        Assert.Equal(2, tiles.Length);
        Assert.Equal(0, tiles[0].X);
        Assert.Equal(0, tiles[0].Y);
        Assert.Equal(1000, tiles[0].Height);
        Assert.Equal(955, tiles[0].Width);            // (1920 - 10) / 2
        Assert.Equal(965, tiles[1].X);                // 955 + 10 gap
        Assert.Equal(0, tiles[1].Y);
    }

    [Fact]
    public void Compute_ThreeWindows_Grid_TwoTopOneWideBottom()
    {
        // ceil(sqrt(3)) = 2 cols, ceil(3/2) = 2 rows. Row 0 has 2, row 1 has 1 (stretched full width).
        var tiles = PopOutTileLayout.Compute(3, PopOutArrangement.Grid, Work, gap: 10);
        Assert.Equal(3, tiles.Length);

        int cellH = (1000 - 10) / 2; // 495
        // Top row: two half-width cells.
        Assert.Equal(new TileRect(0, 0, 955, cellH), tiles[0]);
        Assert.Equal(965, tiles[1].X);
        Assert.Equal(0, tiles[1].Y);
        Assert.Equal(955, tiles[1].Width);
        // Bottom row: single window stretched to full width, second row Y.
        Assert.Equal(0, tiles[2].X);
        Assert.Equal(cellH + 10, tiles[2].Y);
        Assert.Equal(1920, tiles[2].Width);
    }

    [Fact]
    public void Compute_FourWindows_Grid_TwoByTwo()
    {
        var tiles = PopOutTileLayout.Compute(4, PopOutArrangement.Grid, Work, gap: 10);
        Assert.Equal(4, tiles.Length);
        int cellH = (1000 - 10) / 2;
        // All four are half-width, half-height.
        Assert.All(tiles, t => Assert.Equal(955, t.Width));
        Assert.All(tiles, t => Assert.Equal(cellH, t.Height));
        Assert.Equal(0, tiles[0].X);
        Assert.Equal(965, tiles[1].X);
        Assert.Equal(0, tiles[2].X);
        Assert.Equal(965, tiles[3].X);
        Assert.Equal(cellH + 10, tiles[2].Y);
    }

    [Fact]
    public void Compute_Columns_AllSideBySideFullHeight()
    {
        var tiles = PopOutTileLayout.Compute(3, PopOutArrangement.Columns, Work, gap: 10);
        Assert.Equal(3, tiles.Length);
        Assert.All(tiles, t => Assert.Equal(1000, t.Height));
        Assert.All(tiles, t => Assert.Equal(0, t.Y));
        int expectedWidth = (1920 - 10 * 2) / 3;
        Assert.All(tiles, t => Assert.Equal(expectedWidth, t.Width));
    }

    [Fact]
    public void Compute_Rows_AllStackedFullWidth()
    {
        var tiles = PopOutTileLayout.Compute(3, PopOutArrangement.Rows, Work, gap: 10);
        Assert.Equal(3, tiles.Length);
        Assert.All(tiles, t => Assert.Equal(1920, t.Width));
        Assert.All(tiles, t => Assert.Equal(0, t.X));
        int expectedHeight = (1000 - 10 * 2) / 3;
        Assert.All(tiles, t => Assert.Equal(expectedHeight, t.Height));
    }

    [Fact]
    public void Compute_OffsetWorkArea_IsHonored()
    {
        var work = new TileRect(100, 50, 800, 600);
        var tiles = PopOutTileLayout.Compute(1, PopOutArrangement.Grid, work, gap: 8);
        Assert.Equal(new TileRect(100, 50, 800, 600), tiles[0]);
    }

    [Theory]
    [InlineData(0, 0)]  // Grid
    [InlineData(1, 1)]  // Columns
    [InlineData(2, 2)]  // Rows
    [InlineData(3, 3)]  // Cascade
    [InlineData(4, 4)]  // Manual
    [InlineData(99, 0)] // out-of-range -> Grid
    [InlineData(-1, 0)] // out-of-range -> Grid
    public void FromIndex_MapsCorrectly(int index, int expectedValue)
        => Assert.Equal(expectedValue, (int)PopOutTileLayout.FromIndex(index));
}
