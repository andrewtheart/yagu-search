using TextControlBoxNS.Core;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="ScrollOffsetMath"/> — the pure pixel↔scrollbar-unit conversions behind the
/// editor's Phase 1 <c>IScrollOffsetSource</c> seam. The vendored <c>ScrollBarOffsetSource</c> adapter can't
/// be exercised headless (it wraps a WinUI <c>ScrollBar</c>), so the conversion arithmetic — where a
/// "scroll is 4× too fast/slow" regression would hide — is extracted here and covered directly.
/// </summary>
public sealed class ScrollOffsetMathTests
{
    // Yagu's editor uses DefaultVerticalScrollSensitivity = 4.
    private const int DefaultSensitivity = 4;

    // ── NormalizeSensitivity ──────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(4, 4)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(0, 1)]   // a zero divisor is normalized up to 1
    [InlineData(-3, 1)]  // negatives too
    public void NormalizeSensitivity_FloorsAtOne(int input, int expected)
    {
        Assert.Equal(expected, ScrollOffsetMath.NormalizeSensitivity(input));
    }

    // ── Vertical value ↔ pixels ───────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(10, 4, 40)]     // legacy Value 10 = 40 px at sensitivity 4
    [InlineData(25, 1, 25)]     // sensitivity 1 → 1:1
    [InlineData(7.5, 4, 30)]
    public void VerticalValueToPixels_MultipliesBySensitivity(double value, int sensitivity, double expectedPixels)
    {
        Assert.Equal(expectedPixels, ScrollOffsetMath.VerticalValueToPixels(value, sensitivity));
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(40, 4, 10)]     // 40 px = legacy Value 10 at sensitivity 4
    [InlineData(30, 4, 7.5)]
    [InlineData(25, 1, 25)]
    public void PixelsToVerticalValue_DividesBySensitivity(double pixels, int sensitivity, double expectedValue)
    {
        Assert.Equal(expectedValue, ScrollOffsetMath.PixelsToVerticalValue(pixels, sensitivity));
    }

    [Fact]
    public void PixelsToVerticalValue_ClampsNegativeToZero()
    {
        Assert.Equal(0, ScrollOffsetMath.PixelsToVerticalValue(-500, DefaultSensitivity));
    }

    [Fact]
    public void PixelsToVerticalValue_NormalizesNonPositiveSensitivity()
    {
        // sensitivity 0 → treated as 1, so 40 px stays 40 (no divide-by-zero / infinity).
        Assert.Equal(40, ScrollOffsetMath.PixelsToVerticalValue(40, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(123.5)]
    [InlineData(1_000_000)]
    public void VerticalPixels_RoundTripThroughValue_IsIdentity(double pixels)
    {
        double value = ScrollOffsetMath.PixelsToVerticalValue(pixels, DefaultSensitivity);
        double back = ScrollOffsetMath.VerticalValueToPixels(value, DefaultSensitivity);
        Assert.Equal(pixels, back, precision: 9);
    }

    // ── Vertical extent ↔ maximum ─────────────────────────────────────────────────────────────────
    [Fact]
    public void VerticalMaximumToExtentPixels_AddsViewportAfterScaling()
    {
        // Maximum 10 units × 4 = 40 px scrollable, + 100 px viewport = 140 px total content.
        Assert.Equal(140, ScrollOffsetMath.VerticalMaximumToExtentPixels(10, DefaultSensitivity, 100));
    }

    [Fact]
    public void VerticalExtentPixelsToMaximum_SubtractsViewportBeforeScaling()
    {
        // 140 px content − 100 px viewport = 40 px scrollable ÷ 4 = Maximum 10 units.
        Assert.Equal(10, ScrollOffsetMath.VerticalExtentPixelsToMaximum(140, DefaultSensitivity, 100));
    }

    [Fact]
    public void VerticalExtentPixelsToMaximum_FloorsAtZeroWhenContentSmallerThanViewport()
    {
        Assert.Equal(0, ScrollOffsetMath.VerticalExtentPixelsToMaximum(60, DefaultSensitivity, 100));
    }

    [Theory]
    [InlineData(140, 100)]
    [InlineData(5000, 800)]
    public void VerticalExtent_RoundTripThroughMaximum_IsIdentity(double extentPixels, double viewport)
    {
        double maximum = ScrollOffsetMath.VerticalExtentPixelsToMaximum(extentPixels, DefaultSensitivity, viewport);
        double back = ScrollOffsetMath.VerticalMaximumToExtentPixels(maximum, DefaultSensitivity, viewport);
        Assert.Equal(extentPixels, back, precision: 9);
    }

    // ── Horizontal (already pixels) ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0, 0)]
    [InlineData(250.5, 250.5)]
    [InlineData(-42, 0)]        // negative clamps to 0
    public void ClampHorizontalOffset_FloorsAtZero(double pixels, double expected)
    {
        Assert.Equal(expected, ScrollOffsetMath.ClampHorizontalOffset(pixels));
    }

    [Fact]
    public void HorizontalMaximumToExtentPixels_AddsViewport()
    {
        Assert.Equal(1200, ScrollOffsetMath.HorizontalMaximumToExtentPixels(400, 800));
    }

    [Fact]
    public void HorizontalExtentPixelsToMaximum_SubtractsViewport()
    {
        Assert.Equal(400, ScrollOffsetMath.HorizontalExtentPixelsToMaximum(1200, 800));
    }

    [Fact]
    public void HorizontalExtentPixelsToMaximum_FloorsAtZeroWhenContentNarrowerThanViewport()
    {
        Assert.Equal(0, ScrollOffsetMath.HorizontalExtentPixelsToMaximum(500, 800));
    }

    [Theory]
    [InlineData(1200, 800)]
    [InlineData(50000, 1024)]
    public void HorizontalExtent_RoundTripThroughMaximum_IsIdentity(double extentPixels, double viewport)
    {
        double maximum = ScrollOffsetMath.HorizontalExtentPixelsToMaximum(extentPixels, viewport);
        double back = ScrollOffsetMath.HorizontalMaximumToExtentPixels(maximum, viewport);
        Assert.Equal(extentPixels, back, precision: 9);
    }
}
