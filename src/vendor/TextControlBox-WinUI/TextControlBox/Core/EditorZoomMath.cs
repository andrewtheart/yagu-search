using System;

namespace TextControlBoxNS.Core;

/// <summary>
/// Pure math for wheel/pinch-driven editor zoom. Kept dependency-free (no WinUI) so the
/// accumulate → step → clamp logic behind <c>PointerActionsManager.PointerWheelAction</c>'s
/// Ctrl+wheel / pinch branch is unit-testable in isolation, mirroring the preview pane's
/// <c>PreviewZoomMath</c>.
///
/// <para>Why this exists: a precision-touchpad pinch is delivered by Windows as many SMALL
/// Ctrl-modified wheel deltas. The legacy editor code did <c>_ZoomFactor += delta / 20</c> with
/// INTEGER division, so any <c>|delta| &lt; 20</c> truncated to 0 and a real pinch did nothing —
/// only a full 120-unit mouse notch ever moved the zoom. Accumulating sub-notch deltas into whole
/// percent steps makes the pinch feel like smooth single-percent steps, exactly like the preview.</para>
/// </summary>
internal static class EditorZoomMath
{
    /// <summary>Smallest editor zoom factor (percent).</summary>
    internal const int MinZoom = 4;

    /// <summary>Largest editor zoom factor (percent).</summary>
    internal const int MaxZoom = 400;

    /// <summary>
    /// Wheel units per one-percent zoom step. Chosen as 20 so a standard 120-unit mouse notch
    /// still moves the zoom by 6% (matching the legacy <c>delta / 20</c> behavior), while a
    /// precision-touchpad pinch's small deltas accumulate to whole steps instead of truncating away.
    /// </summary>
    internal const int WheelUnitsPerPercent = 20;

    /// <summary>
    /// Accumulates <paramref name="wheelDelta"/> into <paramref name="accumulator"/> and returns the
    /// resulting zoom factor, clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>]. Whole percent
    /// steps consumed from the accumulator are subtracted back out so sub-notch remainders carry to the
    /// next call. When no whole step is available the current factor is returned unchanged (still clamped).
    /// </summary>
    internal static int ApplyWheelZoom(int currentZoomFactor, int wheelDelta, ref int accumulator)
    {
        accumulator += wheelDelta;
        int steps = accumulator / WheelUnitsPerPercent;
        if (steps == 0)
            return Math.Clamp(currentZoomFactor, MinZoom, MaxZoom);

        accumulator -= steps * WheelUnitsPerPercent;
        return Math.Clamp(currentZoomFactor + steps, MinZoom, MaxZoom);
    }
}
