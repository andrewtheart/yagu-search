using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Yagu.Helpers;

/// <summary>
/// Helpers for locating and centering content inside a WinUI <see cref="ScrollViewer"/>
/// hosted by another control (e.g., the inner ScrollViewer of a TextBox template).
/// Pure UI utilities — no view-model coupling.
/// </summary>
internal static class EditorScrollHelper
{
    /// <summary>
    /// Depth-first walk of the visual tree returning the first descendant ScrollViewer.
    /// </summary>
    public static ScrollViewer? FindFirstScrollViewerInTree(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindFirstScrollViewerInTree(child);
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>
    /// Computes the new <c>VerticalOffset</c> needed to move a line from the bottom
    /// edge (where <c>TextBox.Select()</c> typically lands it) to the vertical
    /// middle of the viewport.
    ///
    /// Algebra: in ScrollViewer coordinates a line's on-screen Y =
    /// <c>lineAbsoluteY - VerticalOffset</c>. To move the line UP from the bottom
    /// edge (vp - lineHeight) to the middle (vp/2), on-screen Y must DECREASE by
    /// <c>(vp/2 - lineHeight)</c>, which means VerticalOffset must INCREASE by that
    /// amount. Result is clamped to [0, ScrollableHeight].
    /// </summary>
    public static double ComputeCenterOffset(double currentOffset, double viewportHeight, double scrollableHeight, double lineHeight)
    {
        if (viewportHeight <= 0) return currentOffset;
        double newOffset = currentOffset + (viewportHeight / 2 - lineHeight);
        if (newOffset < 0) newOffset = 0;
        if (newOffset > scrollableHeight) newOffset = scrollableHeight;
        return newOffset;
    }

    /// <summary>
    /// Estimates a usable line height from a TextBox's font size, falling back to
    /// 19px when the font size has not been initialized yet.
    /// </summary>
    public static double EstimateLineHeight(double fontSize)
    {
        double lineHeight = fontSize * 1.4;
        return lineHeight < 1 ? 19 : lineHeight;
    }
}
