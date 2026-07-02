namespace Yagu.Helpers;

/// <summary>
/// Pure math for wheel/pinch-driven preview text zoom. Kept dependency-free
/// (no WinUI) so the accumulate → step → clamp logic behind
/// <c>MainWindow.AdjustPreviewTextZoom</c> is unit-testable in isolation.
/// </summary>
internal static class PreviewZoomMath
{
    /// <summary>Smallest preview font size the zoom can reach (points).</summary>
    internal const int MinFontSize = 6;

    /// <summary>Largest preview font size the zoom can reach (points).</summary>
    internal const int MaxFontSize = 72;

    /// <summary>
    /// Wheel units per one-point font step. A standard mouse notch is 120 units;
    /// a precision-touchpad pinch emits many smaller deltas that accumulate to a
    /// notch, so both feel like smooth single-point steps.
    /// </summary>
    internal const int WheelUnitsPerStep = 120;

    /// <summary>
    /// Accumulates <paramref name="wheelDelta"/> into <paramref name="accumulator"/>
    /// and returns the resulting preview font size, clamped to
    /// [<see cref="MinFontSize"/>, <see cref="MaxFontSize"/>]. Whole steps consumed
    /// from the accumulator are subtracted back out so sub-notch remainders carry
    /// to the next call. When no whole step is available the current size is
    /// returned unchanged (still clamped).
    /// </summary>
    internal static int ApplyWheelZoom(int currentFontSize, int wheelDelta, ref int accumulator)
    {
        accumulator += wheelDelta;
        int steps = accumulator / WheelUnitsPerStep;
        if (steps == 0)
            return Math.Clamp(currentFontSize, MinFontSize, MaxFontSize);

        accumulator -= steps * WheelUnitsPerStep;
        return Math.Clamp(currentFontSize + steps, MinFontSize, MaxFontSize);
    }
}
