using TextControlBoxNS.Core;

namespace Yagu.Tests;

/// <summary>
/// Executable coverage for <see cref="EditorZoomMath.ApplyWheelZoom"/> — the pure
/// accumulate → step → clamp math behind the built-in editor's pinch/Ctrl-wheel zoom.
/// The WinUI glue in <c>PointerActionsManager.PointerWheelAction</c> is source-pinned
/// separately in <see cref="PreviewCoreRegressionTests"/> (it cannot run in the headless
/// test host). This mirrors <see cref="PreviewZoomMathTests"/>; it exists because the
/// editor's percent-based zoom previously used integer <c>delta / 20</c>, which silently
/// truncated a precision-touchpad pinch's small deltas to zero (the pinch "did nothing").
/// </summary>
public sealed class EditorZoomMathTests
{
    [Fact]
    public void SubStepDelta_DoesNotChangeZoom_ButAccumulates()
    {
        // 12 units < one 20-unit percent step: zoom unchanged, remainder kept (this is the exact
        // case the old integer `delta / 20` dropped, so a pinch never zoomed).
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(currentZoomFactor: 100, wheelDelta: 12, ref acc);

        Assert.Equal(100, result);
        Assert.Equal(12, acc);
    }

    [Fact]
    public void TwoSubStepDeltas_AccumulateIntoOneStep()
    {
        int acc = 0;
        int first = EditorZoomMath.ApplyWheelZoom(100, 12, ref acc);
        Assert.Equal(100, first);   // steps == 0 branch
        Assert.Equal(12, acc);

        int second = EditorZoomMath.ApplyWheelZoom(100, 8, ref acc);
        Assert.Equal(101, second);  // 12 + 8 == 20 -> one percent up
        Assert.Equal(0, acc);       // remainder fully consumed
    }

    [Fact]
    public void FullMouseNotch_MovesZoomBySixPercent_MatchingLegacyDelta20()
    {
        // A standard 120-unit mouse notch must still move the zoom by 6% (120 / 20), preserving the
        // legacy `delta / 20` feel for mouse Ctrl+wheel.
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(100, 120, ref acc);

        Assert.Equal(106, result);
        Assert.Equal(0, acc);
    }

    [Fact]
    public void FullStepDown_DecrementsByOnePercent()
    {
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(100, -EditorZoomMath.WheelUnitsPerPercent, ref acc);

        Assert.Equal(99, result);
        Assert.Equal(0, acc);
    }

    [Fact]
    public void PositiveRemainder_CarriesToAccumulator()
    {
        // 50 / 20 == 2 percent steps; 50 - 40 == 10 remainder carried forward.
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(100, 50, ref acc);

        Assert.Equal(102, result);
        Assert.Equal(10, acc);
    }

    [Fact]
    public void NegativeRemainder_CarriesToAccumulator()
    {
        // Integer division truncates toward zero: -50 / 20 == -2 steps; -50 - (-40) == -10 remainder.
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(100, -50, ref acc);

        Assert.Equal(98, result);
        Assert.Equal(-10, acc);
    }

    [Fact]
    public void ZoomIn_ClampsAtMaxZoom()
    {
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(EditorZoomMath.MaxZoom, EditorZoomMath.WheelUnitsPerPercent, ref acc);

        Assert.Equal(EditorZoomMath.MaxZoom, result);
    }

    [Fact]
    public void ZoomOut_ClampsAtMinZoom()
    {
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(EditorZoomMath.MinZoom, -EditorZoomMath.WheelUnitsPerPercent, ref acc);

        Assert.Equal(EditorZoomMath.MinZoom, result);
    }

    [Fact]
    public void ManyStepsUp_ClampedToMax_NotOvershot()
    {
        int acc = 0;
        int result = EditorZoomMath.ApplyWheelZoom(100, 1000 * EditorZoomMath.WheelUnitsPerPercent, ref acc);

        Assert.Equal(EditorZoomMath.MaxZoom, result);
    }

    [Fact]
    public void SubStepDelta_WithOutOfRangeCurrentZoom_ReturnsClampedZoom()
    {
        // steps == 0 branch still clamps a stale/out-of-range current factor.
        int accLow = 0;
        Assert.Equal(EditorZoomMath.MinZoom, EditorZoomMath.ApplyWheelZoom(1, 5, ref accLow));

        int accHigh = 0;
        Assert.Equal(EditorZoomMath.MaxZoom, EditorZoomMath.ApplyWheelZoom(1000, 5, ref accHigh));
    }
}
