using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Yagu;

/// <summary>
/// Wires the <c>StickyHorizontalScrollBar</c> overlay to the currently-active
/// per-section <see cref="ScrollViewer"/> in multi-section preview mode.
///
/// Per-section scrollers host horizontal overflow but their native bars sit
/// at the bottom of full content height (off-screen). This overlay surfaces
/// the actual horizontal extent at the viewport bottom and drives the active
/// section's scroller bidirectionally.
/// </summary>
public partial class MainWindow
{
    private ScrollViewer? _stickyHScrollSource;
    private bool _stickyHScrollSyncing;

    /// <summary>
    /// Picks the active section's scroller and resyncs the sticky bar to it.
    /// Safe to call repeatedly; cheap when no work to do.
    /// </summary>
    private void UpdateStickyHorizontalScrollBar()
    {
        if (StickyHorizontalScrollBar is null)
            return;

        // Only meaningful in multi-section mode with wrap disabled.
        if (PreviewSectionsPanel is null ||
            PreviewSectionsPanel.Visibility != Visibility.Visible ||
            ViewModel?.PreviewWordWrap == true)
        {
            HideStickyHorizontalScrollBar();
            return;
        }

        var source = ResolveActiveSectionScroller();
        if (source is null || source.ScrollableWidth <= 0.5 || source.ViewportWidth <= 0.5)
        {
            HideStickyHorizontalScrollBar();
            return;
        }

        if (!ReferenceEquals(_stickyHScrollSource, source))
            _stickyHScrollSource = source;

        _stickyHScrollSyncing = true;
        try
        {
            StickyHorizontalScrollBar.Minimum = 0;
            StickyHorizontalScrollBar.Maximum = source.ScrollableWidth;
            StickyHorizontalScrollBar.ViewportSize = source.ViewportWidth;
            StickyHorizontalScrollBar.LargeChange = source.ViewportWidth;
            StickyHorizontalScrollBar.SmallChange = 32;
            StickyHorizontalScrollBar.Value = source.HorizontalOffset;
            StickyHorizontalScrollBar.Visibility = Visibility.Visible;
        }
        finally
        {
            _stickyHScrollSyncing = false;
        }
    }

    private void HideStickyHorizontalScrollBar()
    {
        if (StickyHorizontalScrollBar is not null &&
            StickyHorizontalScrollBar.Visibility != Visibility.Collapsed)
        {
            StickyHorizontalScrollBar.Visibility = Visibility.Collapsed;
        }
        _stickyHScrollSource = null;
    }

    /// <summary>
    /// Active source preference:
    /// 1) The section containing the current match.
    /// 2) The currently-active section (from <c>_activeSectionNav</c>).
    /// 3) The first expanded section in the panel.
    /// </summary>
    private ScrollViewer? ResolveActiveSectionScroller()
    {
        if (_currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count)
        {
            var block = _matchParagraphs[_currentMatchIndex].block;
            if (_sectionMatchNavs.TryGetValue(block, out var sn))
                return sn.Scroller;
        }

        if (_activeSectionNav is not null)
            return _activeSectionNav.Scroller;

        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (!expander.IsExpanded) continue;
            if (expander.Tag is RichTextBlock block &&
                _sectionMatchNavs.TryGetValue(block, out var sn))
            {
                return sn.Scroller;
            }
        }
        return null;
    }

    /// <summary>
    /// Hooked on every per-section ScrollViewer at creation. Pulls offset
    /// changes from the active source into the sticky bar's <c>Value</c>.
    /// </summary>
    private void OnSectionScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_stickyHScrollSyncing) return;
        if (!ReferenceEquals(sender, _stickyHScrollSource))
        {
            // Source may have changed (e.g. active section switched); refresh.
            UpdateStickyHorizontalScrollBar();
            return;
        }
        if (sender is not ScrollViewer sv) return;
        _stickyHScrollSyncing = true;
        try
        {
            // Resync extent in case content width changed underfoot.
            if (StickyHorizontalScrollBar.Maximum != sv.ScrollableWidth)
                StickyHorizontalScrollBar.Maximum = sv.ScrollableWidth;
            if (StickyHorizontalScrollBar.ViewportSize != sv.ViewportWidth)
            {
                StickyHorizontalScrollBar.ViewportSize = sv.ViewportWidth;
                StickyHorizontalScrollBar.LargeChange = sv.ViewportWidth;
            }
            StickyHorizontalScrollBar.Value = sv.HorizontalOffset;
            // Hide if there's no longer overflow.
            if (sv.ScrollableWidth <= 0.5)
                HideStickyHorizontalScrollBar();
        }
        finally
        {
            _stickyHScrollSyncing = false;
        }
    }

    /// <summary>
    /// User-driven (thumb drag, track click, arrow buttons): push the new
    /// offset into the active source scroller.
    /// </summary>
    private void OnStickyHorizontalScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (_stickyHScrollSyncing) return;
        var src = _stickyHScrollSource;
        if (src is null) return;
        _stickyHScrollSyncing = true;
        try
        {
            src.ChangeView(e.NewValue, null, null, disableAnimation: true);
        }
        finally
        {
            _stickyHScrollSyncing = false;
        }
    }

    /// <summary>
    /// Catches initial layout and post-layout size changes (viewport or extent)
    /// which <see cref="ScrollViewer.ViewChanged"/> does not raise when offset
    /// remains at 0. Deferred to the dispatcher so we read the post-layout
    /// values, not the pre-layout snapshot.
    /// </summary>
    private void OnSectionScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DispatcherQueue is null) { UpdateStickyHorizontalScrollBar(); return; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            UpdateStickyHorizontalScrollBar);
    }
}
