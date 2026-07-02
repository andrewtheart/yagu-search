using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Executable coverage for <see cref="PreviewZoomMath.ApplyWheelZoom"/> — the pure
/// accumulate → step → clamp math behind preview pinch/Ctrl-wheel zoom. The WinUI
/// glue in <c>MainWindow.AdjustPreviewTextZoom</c> is source-pinned separately in
/// <see cref="PreviewCoreRegressionTests"/> (it cannot run in the headless test host).
/// </summary>
public sealed class PreviewZoomMathTests
{
    [Fact]
    public void SubNotchDelta_DoesNotChangeSize_ButAccumulates()
    {
        // 60 units < one 120-unit step: size unchanged, remainder kept for next event.
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(currentFontSize: 14, wheelDelta: 60, ref acc);

        Assert.Equal(14, result);
        Assert.Equal(60, acc);
    }

    [Fact]
    public void TwoSubNotchDeltas_AccumulateIntoOneStep()
    {
        int acc = 0;
        int first = PreviewZoomMath.ApplyWheelZoom(14, 60, ref acc);
        Assert.Equal(14, first);   // steps == 0 branch
        Assert.Equal(60, acc);

        int second = PreviewZoomMath.ApplyWheelZoom(14, 60, ref acc);
        Assert.Equal(15, second);  // 60 + 60 == 120 -> one step up
        Assert.Equal(0, acc);      // remainder fully consumed
    }

    [Fact]
    public void FullNotchUp_IncrementsByOne_AndConsumesAccumulator()
    {
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(14, PreviewZoomMath.WheelUnitsPerStep, ref acc);

        Assert.Equal(15, result);
        Assert.Equal(0, acc);
    }

    [Fact]
    public void FullNotchDown_DecrementsByOne()
    {
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(14, -PreviewZoomMath.WheelUnitsPerStep, ref acc);

        Assert.Equal(13, result);
        Assert.Equal(0, acc);
    }

    [Fact]
    public void MultipleNotchesUp_IncrementByStepCount()
    {
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(14, 3 * PreviewZoomMath.WheelUnitsPerStep, ref acc);

        Assert.Equal(17, result);
        Assert.Equal(0, acc);
    }

    [Fact]
    public void PositiveRemainder_CarriesToAccumulator()
    {
        // 200 / 120 == 1 step; 200 - 120 == 80 remainder carried forward.
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(14, 200, ref acc);

        Assert.Equal(15, result);
        Assert.Equal(80, acc);
    }

    [Fact]
    public void NegativeRemainder_CarriesToAccumulator()
    {
        // Integer division truncates toward zero: -200 / 120 == -1 step;
        // -200 - (-1 * 120) == -80 remainder carried forward.
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(14, -200, ref acc);

        Assert.Equal(13, result);
        Assert.Equal(-80, acc);
    }

    [Fact]
    public void ZoomIn_ClampsAtMaxFontSize()
    {
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(PreviewZoomMath.MaxFontSize, PreviewZoomMath.WheelUnitsPerStep, ref acc);

        Assert.Equal(PreviewZoomMath.MaxFontSize, result);
    }

    [Fact]
    public void ZoomOut_ClampsAtMinFontSize()
    {
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(PreviewZoomMath.MinFontSize, -PreviewZoomMath.WheelUnitsPerStep, ref acc);

        Assert.Equal(PreviewZoomMath.MinFontSize, result);
    }

    [Fact]
    public void ManyNotchesUp_ClampedToMax_NotOvershot()
    {
        int acc = 0;
        int result = PreviewZoomMath.ApplyWheelZoom(14, 100 * PreviewZoomMath.WheelUnitsPerStep, ref acc);

        Assert.Equal(PreviewZoomMath.MaxFontSize, result);
    }

    [Fact]
    public void SubNotchDelta_WithOutOfRangeCurrentSize_ReturnsClampedSize()
    {
        // steps == 0 branch still clamps a stale/out-of-range current size.
        int accLow = 0;
        Assert.Equal(PreviewZoomMath.MinFontSize, PreviewZoomMath.ApplyWheelZoom(3, 10, ref accLow));

        int accHigh = 0;
        Assert.Equal(PreviewZoomMath.MaxFontSize, PreviewZoomMath.ApplyWheelZoom(100, 10, ref accHigh));
    }
}
