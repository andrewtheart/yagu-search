using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yagu.Models;
using Yagu.Services;
using System.Diagnostics;
namespace Yagu;

/// <summary>
/// Preview panel layout, lazy section management, sticky headers, and section prepending.
/// </summary>
public sealed partial class MainWindow
{
    private SearchResult? _previewResult;
    private bool _previewPanelRevealed;
    private bool _previewContentPending;
    private RichTextBlock? _lastPreviewContextMenuBlock;
    private Windows.Foundation.Point _lastPreviewContextMenuPoint;
    private string? _lastPreviewContextMenuFilePath;
    private long _lastPreviewContextMenuTick;
    private const int PreviewContextMenuPointMaxAgeMs = 30_000;

    // Match navigation state for multi-highlight mode
    private readonly List<(RichTextBlock block, Paragraph para, int matchInPara)> _matchParagraphs = new();
    private int _currentMatchIndex = -1;
    // Bulk match navigation: step size for Ctrl+Click on Next/Prev.
    // 0 = not configured yet (show flyout on Ctrl+Click).
    private int _bulkMatchStep;
    // When true the flyout won't be shown again this session; Ctrl+Click
    // jumps _bulkMatchStep immediately.
    private bool _bulkMatchStepLocked;
    // Set to true once we've auto-scrolled to the first match for the current
    // preview load; reset by HideMatchNavPanel / preview-clear paths so a fresh
    // preview re-triggers the auto-scroll-to-first-match.
    private bool _initialMatchScrolled;
    // Paragraph metrics cache for O(1) lookup and O(1) cumulative-height estimates
    // in large files. Invalidated whenever a block's Blocks collection changes
    // (full-file toggle, section materialize, clear).
    private sealed class ParagraphMetrics
    {
        public required Dictionary<Paragraph, int> IndexByParagraph { get; init; }
        public double[]? PrefixHeights;
        public int PrefixCharsPerLine;
        public double PrefixLineHeight;
    }
    private readonly Dictionary<RichTextBlock, ParagraphMetrics> _paragraphMetricsCache = new();
    private readonly Dictionary<Paragraph, List<(Run run, int column)>> _paragraphMatchRunCache = new();
    private readonly Dictionary<RichTextBlock, Expander> _blockExpanderCache = new();
    private readonly Dictionary<RichTextBlock, double> _blockAbsoluteTopCache = new();

    // Per-section match navigation state (class moved to Models/SectionMatchNav.cs)
    private readonly Dictionary<RichTextBlock, SectionMatchNav> _sectionMatchNavs = new();
    private SectionMatchNav? _activeSectionNav;
    // Section content-block → gutter-block for sticky line-number display.
    private readonly Dictionary<RichTextBlock, RichTextBlock> _sectionGutterBlocks = new();
    // Expander → file path, used to render the sticky file-header overlay
    // (shown when the active expander's own header has scrolled out of view).
    private readonly Dictionary<Expander, string> _expanderFilePaths = new();
    // Cached metadata for rebuilding a section header inside the sticky overlay.
    private readonly Dictionary<Expander, (string FilePath, string? Detail, RichTextBlock Block, List<SearchResult>? Results)> _expanderHeaderArgs = new();
    // The expander whose header is currently mirrored in StickyFileHeader, so we
    // only rebuild + replace its child when the topmost-in-viewport changes.
    private Expander? _stickyHeaderExpander;

    private List<KeyValuePair<string, List<SearchResult>>>? _cachedDeferredCountsList;
    private int _cachedDeferredCountsCursor = -1;
    private (int Files, int Matches) _cachedDeferredCounts;

    // Lazy section rendering — deferred content building for collapsed sections
    private sealed class LazySection
    {
        public required string FilePath { get; init; }
        public required List<SearchResult> Results { get; init; }
        public string[]? AllLines { get; init; }
        public int PreviewLines { get; init; }
        public bool IsHighlight { get; init; }
        public int MatchCount { get; init; } // pre-computed match count for nav label
    }
    private readonly Dictionary<RichTextBlock, LazySection> _lazySections = new();
    private int _lazyMatchCount; // total matches in un-rendered sections
    private int _previewTotalMatchCount;
    private int _previewStableMatchNavTotal;
    private int _previewTotalFileCount;
    private readonly Dictionary<RichTextBlock, int> _sectionTotalMatchCounts = new();
    private bool _previewViewChangedHooked;
    private bool _viewportMaterializePending;
    // Blocks whose section is being expanded by the scroll-driven
    // MaterializeVisibleLazySections sweep. Such sections must render their
    // content but must NOT become the active/selected section — otherwise
    // scrolling a long file far enough to pull a sibling section into the
    // pre-materialization buffer would steal the "selected" background from the
    // file the user is actually reading. The Expander.Expanding handler consumes
    // (removes) the tag synchronously before its first await.
    private readonly HashSet<RichTextBlock> _autoMaterializingSections = new();
    private static readonly Windows.UI.Color s_defaultSelectedPreviewContentBackground = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    private static readonly Windows.UI.Color s_defaultUnselectedPreviewContentBackground = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);

    // Tracks remaining (un-rendered) matches for sections that were truncated to
    // avoid UI freezes on huge files (see MaxMatchesPerSection). Each click of
    // "Next match" past the last rendered match in such a section appends the
    // next chunk of results to the section.
    private sealed class SectionOverflow
    {
        public string? FilePath;
        public required List<SearchResult> RemainingResults;
        public required string[]? AllLines;
        public required int PreviewLines;
        public required Regex? Rx;
        public required int OriginalTotal;
        public required int RenderedSoFar;
        public Paragraph? NoticePara;
        /// <summary>True if the section was rendered in multi-highlight mode
        /// (contiguous line ranges with <c>⋮</c> gap markers) rather than
        /// concat mode (per-match "── Line N ──" separators).</summary>
        public bool IsHighlightMode;
        /// <summary>Highest line index (1-based) already rendered. Used in
        /// highlight mode to clip overlapping context windows so expansion
        /// continues directly from the last rendered line.</summary>
        public int LastRenderedLine;
    }
    private readonly Dictionary<RichTextBlock, SectionOverflow> _sectionOverflow = new();

    // Active match state. The active visual marker is rendered as an overlay;
    // do not mutate Run styling here because RichTextBlock re-renders expensively.
    private (Paragraph para, Run run, int column, int matchInPara)? _activeMatchHighlight;
    private readonly List<Border> _activeMatchExtraWordMarkers = new();
    private int _matchScrollRequestId;
    private bool _activeMatchOverlayRefreshPending;
    private int _activeMatchOverlayUpdateRequestId;
    private int _previewManualScrollVersion;
    private bool _suppressInitialMatchAutoScroll;
    private RichTextBlock? _activeOverlayStabilityBlock;
    private double _activeOverlayLastBlockTop = double.NaN;
    private long _activeOverlayLastMoveTick;
    private int _activeOverlayStablePasses;

    private enum SplitLayoutMode { Split, ResultsMaximized, PreviewMaximized, PreviewTopExpanded }
    private SplitLayoutMode _splitLayoutMode = SplitLayoutMode.ResultsMaximized;
    private bool _topExpandedPreviewLayoutSyncActive;
    // Remembers the layout to restore when toggling out of ResultsMaximized so that
    // collapsing the maximized results panel returns to whatever split mode was active
    // before (e.g. PreviewTopExpanded), not always plain Split.
    private SplitLayoutMode _splitLayoutBeforeResultsMaximized = SplitLayoutMode.Split;

    private void EnsurePreviewPanelVisible()
    {
        if (_previewPanelRevealed && _splitLayoutMode == SplitLayoutMode.PreviewTopExpanded) return;
        _previewPanelRevealed = true;
        ApplySplitLayout(SplitLayoutMode.PreviewTopExpanded);
    }

    private void CollapsePreviewPanel()
    {
        if (!_previewPanelRevealed && _splitLayoutMode == SplitLayoutMode.ResultsMaximized) return;
        _previewPanelRevealed = false;
        ApplySplitLayout(SplitLayoutMode.ResultsMaximized);
    }

    private void ApplySplitLayout(SplitLayoutMode mode)
    {
        _splitLayoutMode = mode;
        ResetTopExpandedPreviewLayout();
        switch (mode)
        {
            case SplitLayoutMode.Split:
                ResultsPanelBorder.Visibility = Visibility.Visible;
                ResultsColumn.Width = new GridLength(2, GridUnitType.Star);
                ResultsColumn.MinWidth = 200;
                SplitterColumn.Width = GridLength.Auto;
                PreviewColumn.Width = new GridLength(3, GridUnitType.Star);
                PreviewColumn.MinWidth = 200;
                SplitterBorder.Visibility = Visibility.Visible;
                PreviewPanelBorder.Visibility = Visibility.Visible;
                _previewPanelRevealed = true;
                ExpandResultsIcon.Glyph = "\uE740"; // FullScreen
                ToolTipService.SetToolTip(ExpandResultsButton, "Maximize file list / hide preview");
                ExpandPreviewIcon.Glyph = "\uE740"; // FullScreen
                PreviewEditorExpandIcon.Glyph = "\uE740"; // FullScreen
                ToolTipService.SetToolTip(PreviewEditorExpandButton, "Maximize editor / hide results");
                break;
            case SplitLayoutMode.ResultsMaximized:
                ResultsPanelBorder.Visibility = Visibility.Visible;
                ResultsColumn.Width = new GridLength(1, GridUnitType.Star);
                ResultsColumn.MinWidth = 200;
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(0);
                PreviewColumn.MinWidth = 0;
                SplitterBorder.Visibility = Visibility.Collapsed;
                PreviewPanelBorder.Visibility = Visibility.Collapsed;
                _previewPanelRevealed = false;
                ExpandResultsIcon.Glyph = "\uE73F"; // BackToWindow
                ToolTipService.SetToolTip(ExpandResultsButton, "Restore split view");
                ExpandPreviewIcon.Glyph = "\uE740";
                PreviewEditorExpandIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(PreviewEditorExpandButton, "Maximize editor / hide results");
                break;
            case SplitLayoutMode.PreviewMaximized:
                ResultsPanelBorder.Visibility = Visibility.Collapsed;
                ResultsColumn.Width = new GridLength(0);
                ResultsColumn.MinWidth = 0;
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
                PreviewColumn.MinWidth = 200;
                SplitterBorder.Visibility = Visibility.Collapsed;
                PreviewPanelBorder.Visibility = Visibility.Visible;
                _previewPanelRevealed = true;
                ExpandResultsIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(ExpandResultsButton, "Maximize file list / hide preview");
                ExpandPreviewIcon.Glyph = "\uE740"; // FullScreen
                PreviewEditorExpandIcon.Glyph = "\uE740"; // FullScreen
                ToolTipService.SetToolTip(PreviewEditorExpandButton, "Expand editor across the top");
                break;
            case SplitLayoutMode.PreviewTopExpanded:
                ApplyTopExpandedPreviewLayout();
                break;
        }

        UpdateBottomStatusBarVisibility();
        UpdatePreviewEmptyState();
        // Reposition the active match overlay after the panel layout changes.
        // Schedule two refreshes: one immediate (Low priority, after current
        // layout pass) and a second deferred one to catch cases where inner
        // RichTextBlock reflow hasn't completed by the first refresh.
        QueueActiveMatchOverlayRefresh();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _activeMatchOverlayRefreshPending = false;
            QueueActiveMatchOverlayRefresh();
        });
    }

    private void ResetTopExpandedPreviewLayout()
    {
        Grid.SetRow(SplitPaneGrid, 4);
        Grid.SetRowSpan(SplitPaneGrid, 1);
        SplitPaneGrid.Margin = new Thickness(16, 2, 16, 4);
        ResultsPanelBorder.Margin = new Thickness(0);

        SearchControlsBorder.Width = double.NaN;
        SearchControlsBorder.MaxWidth = double.PositiveInfinity;
        SearchControlsBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        SearchStatusPanel.Width = double.NaN;
        SearchStatusPanel.MaxWidth = double.PositiveInfinity;
        SearchStatusPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        ApplyTopSearchDrawerCompactState(false);

        Canvas.SetZIndex(SearchControlsBorder, 0);
        Canvas.SetZIndex(SearchStatusPanel, 0);
        Canvas.SetZIndex(SplitPaneGrid, 0);
    }

    private void ApplyTopExpandedPreviewLayout()
    {
        ResultsPanelBorder.Visibility = Visibility.Visible;
        ResultsColumn.Width = new GridLength(2, GridUnitType.Star);
        ResultsColumn.MinWidth = 280;
        SplitterColumn.Width = GridLength.Auto;
        PreviewColumn.Width = new GridLength(3, GridUnitType.Star);
        PreviewColumn.MinWidth = 360;
        SplitterBorder.Visibility = Visibility.Visible;
        PreviewPanelBorder.Visibility = Visibility.Visible;
        _previewPanelRevealed = true;

        Grid.SetRow(SplitPaneGrid, 2);
        Grid.SetRowSpan(SplitPaneGrid, 3);
        SplitPaneGrid.Margin = new Thickness(16, 10, 16, 4);
        Canvas.SetZIndex(SplitPaneGrid, 0);
        Canvas.SetZIndex(SearchControlsBorder, 10);
        Canvas.SetZIndex(SearchStatusPanel, 10);

        UpdateTopExpandedPreviewMeasurements();
        ListenForTopExpandedPreviewLayoutSync();

        ExpandResultsIcon.Glyph = "\uE740";
        ToolTipService.SetToolTip(ExpandResultsButton, "Maximize file list / hide preview");
        ExpandPreviewIcon.Glyph = "\uE70E"; // ChevronUp
        PreviewEditorExpandIcon.Glyph = "\uE70E"; // ChevronUp
        ToolTipService.SetToolTip(PreviewEditorExpandButton, "Restore split preview layout");

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTopExpandedPreviewMeasurements);
    }

    private void ListenForTopExpandedPreviewLayoutSync()
    {
        if (_topExpandedPreviewLayoutSyncActive)
            return;

        _topExpandedPreviewLayoutSyncActive = true;
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(500);
        debounce.IsRepeating = false;

        void handler(object? s, object? e)
            => UpdateTopExpandedPreviewMeasurements();

        debounce.Tick += (t, a) =>
        {
            debounce.Stop();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            _topExpandedPreviewLayoutSyncActive = false;
            UpdateTopExpandedPreviewMeasurements();
        };

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        debounce.Start();
    }

    private void UpdateTopExpandedPreviewMeasurements()
    {
        if (_splitLayoutMode != SplitLayoutMode.PreviewTopExpanded)
            return;

        double availableWidth = Math.Max(0, RootGrid.ActualWidth - SplitPaneGrid.Margin.Left - SplitPaneGrid.Margin.Right);
        if (availableWidth <= 0)
            return;

        double splitterWidth = SplitterBorder.ActualWidth > 0 ? SplitterBorder.ActualWidth : 8;
        double leftWidth = ResultsPanelBorder.ActualWidth > 0 ? ResultsPanelBorder.ActualWidth : ResultsColumn.ActualWidth;
        double previewWidth = PreviewPanelBorder.Visibility == Visibility.Visible ? PreviewPanelBorder.ActualWidth : 0;
        if (previewWidth > 0)
        {
            double widthFromPreview = availableWidth - splitterWidth - previewWidth;
            if (widthFromPreview > 0)
                leftWidth = leftWidth > 0 ? Math.Min(leftWidth, widthFromPreview) : widthFromPreview;
        }

        if (leftWidth <= 0)
        {
            leftWidth = Math.Max(280, (availableWidth - splitterWidth) * 2.0 / 5.0);
            double maxLeftWidth = Math.Max(280, availableWidth - splitterWidth - 360);
            leftWidth = Math.Min(leftWidth, maxLeftWidth);
        }

        double drawerWidth = Math.Min(availableWidth, Math.Max(240, leftWidth));

        SearchControlsBorder.HorizontalAlignment = HorizontalAlignment.Left;
        SearchControlsBorder.Width = drawerWidth;
        SearchControlsBorder.MaxWidth = drawerWidth;
        SearchStatusPanel.HorizontalAlignment = HorizontalAlignment.Left;
        SearchStatusPanel.Width = drawerWidth;
        SearchStatusPanel.MaxWidth = drawerWidth;
        ApplyTopSearchDrawerCompactState(drawerWidth < CompactTopSearchDrawerThreshold);

        double topOffset = SearchControlsBorder.ActualHeight + SearchStatusPanel.ActualHeight + PreviewTopExpandedDrawerGap;
        ResultsPanelBorder.Margin = new Thickness(0, Math.Max(0, topOffset), 0, 0);
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdvancedOptionsDrawerMaxHeight();
        UpdateTopExpandedPreviewMeasurements();
    }

    private void OnTopExpandedPreviewLayoutSourceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTopExpandedPreviewMeasurements();
    }

    private void UpdateBottomStatusBarVisibility()
    {
        bool showStatusBar = !_resultsPaneCollapsed
            && SplitPaneGrid.Visibility == Visibility.Visible
            && (ResultsPanelBorder.Visibility == Visibility.Visible
                || PreviewPanelBorder.Visibility == Visibility.Visible);

        StatusBarRow.Height = showStatusBar ? GridLength.Auto : new GridLength(0);
        if (!showStatusBar)
            SkipBreakdownOverlay.Visibility = Visibility.Collapsed;
        UpdateTerminalChevronVisibility();
    }

    private void OnExpandResultsPanel(object sender, RoutedEventArgs e)
    {
        // Toggle: maximize results <-> previous split layout. Remember the prior mode
        // so we restore to PreviewTopExpanded (etc.) instead of always plain Split.
        if (_splitLayoutMode == SplitLayoutMode.ResultsMaximized)
        {
            var restore = _splitLayoutBeforeResultsMaximized;
            if (restore == SplitLayoutMode.ResultsMaximized) restore = SplitLayoutMode.Split;
            ApplySplitLayout(restore);
        }
        else
        {
            _splitLayoutBeforeResultsMaximized = _splitLayoutMode;
            ApplySplitLayout(SplitLayoutMode.ResultsMaximized);
        }
    }

    private void OnExpandPreviewPanel(object sender, RoutedEventArgs e)
    {
        // Cycle: split/results -> preview-only -> top-expanded preview -> split.
        if (_splitLayoutMode == SplitLayoutMode.PreviewTopExpanded)
            ApplySplitLayout(SplitLayoutMode.Split);
        else if (_splitLayoutMode == SplitLayoutMode.PreviewMaximized)
            ApplySplitLayout(SplitLayoutMode.PreviewTopExpanded);
        else
            ApplySplitLayout(SplitLayoutMode.PreviewMaximized);
    }

    // ── Responsive results list: compact (stacked) vs wide (side-by-side) ───
    private bool _resultsCompactMode;
    private const double ResultsCompactThreshold = 760;

    private void OnResultsListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < ResultsCompactThreshold;
        if (compact != _resultsCompactMode)
        {
            _resultsCompactMode = compact;
            // Update all currently materialized containers
            for (int i = 0; i < ResultsList.Items.Count; i++)
            {
                if (ResultsList.ContainerFromIndex(i) is FrameworkElement container)
                    ApplyResultsCompactState(container, compact);
            }
        }

        QueueResultsFileOverlayUpdate();
    }

    private void OnResultsListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue)
        {
            ApplyResultsCompactState(args.ItemContainer, _resultsCompactMode);
            SyncFileGroupCheckBoxState(args.ItemContainer, args.Item as FileGroup);

            if (args.Item is FileGroup g && g.IsExpanded)
                _ = EnsureVisibleResultsForExpandedGroupFromContainerAsync(g);

            QueueResultsFileOverlayUpdate();
        }
    }

    private async Task EnsureVisibleResultsForExpandedGroupFromContainerAsync(FileGroup group)
    {
        try
        {
            await EnsureVisibleResultsForExpandedGroupSerializedAsync(group, "container").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("FileGroup",
                $"EnsureVisibleResultsForExpandedGroupAsync failed for '{group.FilePath}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the recycled container's visual tree to find the file-group
    /// CheckBox and explicitly re-asserts its <c>IsChecked</c> from
    /// <see cref="FileGroup.AllSelected"/>. The header CheckBox is intentionally
    /// OneWay-bound so recycled visual state cannot write into the wrong group
    /// while live results are re-sorted.
    /// </summary>
    private static void SyncFileGroupCheckBoxState(FrameworkElement container, FileGroup? group)
    {
        if (group is null) return;
        var checkBox = FindFileGroupCheckBox(container);
        if (checkBox is null) return;
        SetFileGroupCheckBoxState(checkBox, group.AllSelected);
    }

    private static void SetFileGroupCheckBoxState(CheckBox checkBox, bool desired)
    {
        checkBox.IsThreeState = false;
        checkBox.IsChecked = desired;
        _ = VisualStateManager.GoToState(checkBox, desired ? "Checked" : "Unchecked", false);
    }

    private static CheckBox? FindFileGroupCheckBox(Microsoft.UI.Xaml.DependencyObject parent)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is CheckBox cb
                && string.Equals(Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(cb),
                    "FileGroupCheckBox", StringComparison.Ordinal))
                return cb;
            var nested = FindFileGroupCheckBox(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void ApplyResultsCompactState(FrameworkElement container, bool compact)
    {
        // Find TextBlocks by Tag within the container's visual tree
        ApplyCompactStateRecursive(container, compact);
    }

    private static void ApplyCompactStateRecursive(Microsoft.UI.Xaml.DependencyObject parent, bool compact)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb)
            {
                if (tb.Tag is string tag)
                {
                    if (tag == "CompactDir")
                        tb.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                    else if (tag == "WideDir")
                    {
                        tb.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                        // Collapse/expand the grid column so it doesn't reserve space when hidden.
                        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(tb) is Grid g
                            && g.ColumnDefinitions.Count > 3)
                        {
                            g.ColumnDefinitions[2].Width = compact
                                ? new GridLength(1, GridUnitType.Star)
                                : new GridLength(320);
                            g.ColumnDefinitions[3].Width = compact
                                ? new GridLength(0)
                                : new GridLength(1, GridUnitType.Star);
                        }
                    }
                }
            }
            else if (child is Microsoft.UI.Xaml.DependencyObject dep)
            {
                ApplyCompactStateRecursive(dep, compact);
            }
        }
    }

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileGroup g && g.Count > 0)
        {
            LogService.Instance.Info("Preview",
                $"OnResultItemClick: no preview change file='{g.FilePath}', matchCount={g.Count}");
        }
    }

    /// <summary>
    /// Scrolls to an existing preview section for the given file path.
    /// Returns true if the section was found and scrolled to.
    /// </summary>
    private bool TryScrollToPreviewSection(string filePath)
    {
        LogService.Instance.Verbose("Preview", $"TryScrollToPreviewSection: looking for '{filePath}' among {PreviewSectionsPanel.Children.OfType<Expander>().Count()} sections");
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path)
                && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    child.IsExpanded = true;
                    if (child.Tag is RichTextBlock block)
                        ActivateSectionForBlock(block);
                    var transform = child.TransformToVisual(PreviewScrollViewer);
                    var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    double targetOffset = PreviewScrollViewer.VerticalOffset + point.Y;
                    targetOffset = Math.Max(0, targetOffset);
                    PreviewScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
                }
                catch { /* Layout not ready — ignore */ }
                return true;
            }
        }
        return false;
    }

    private bool PreviewSectionExists(string filePath)
    {
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path)
                && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFindPreviewSection(string filePath, out Expander expander, out RichTextBlock section)
    {
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path)
                && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase)
                && child.Tag is RichTextBlock block)
            {
                expander = child;
                section = block;
                return true;
            }
        }

        expander = null!;
        section = null!;
        return false;
    }

    private void RefreshPreviewSectionHeaderForSelectedMatches(string filePath)
    {
        if (!TryFindPreviewSection(filePath, out var expander, out var section))
            return;

        var selectedForFile = ViewModel.GetAllSelectedResults()
            .Where(result => string.Equals(result.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (selectedForFile.Count == 0)
            return;

        string detail = $"{selectedForFile.Count:N0} selected match(es)";
        expander.Header = BuildPreviewSectionHeader(filePath, detail, section, selectedForFile);
        _expanderHeaderArgs[expander] = (filePath, detail, section, selectedForFile);

        if (ReferenceEquals(_stickyHeaderExpander, expander))
            StickyFileHeader.Child = BuildPreviewSectionHeader(filePath, detail, section, selectedForFile);
    }

    /// <summary>
    /// Returns the set of file paths currently shown as preview sections.
    /// </summary>
    private HashSet<string> GetExistingPreviewFilePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path))
                paths.Add(path);
        }
        return paths;
    }

    private void ReorderMatchParagraphsToPreviewSectionOrder()
    {
        if (_matchParagraphs.Count <= 1 || PreviewSectionsPanel.Visibility != Visibility.Visible)
            return;

        var orderedBlocks = PreviewSectionsPanel.Children
            .OfType<Expander>()
            .Select(expander => expander.Tag as RichTextBlock)
            .Where(block => block is not null)
            .Cast<RichTextBlock>()
            .ToList();
        if (orderedBlocks.Count <= 1)
            return;

        var buckets = new Dictionary<RichTextBlock, List<(RichTextBlock block, Paragraph para, int matchInPara)>>();
        foreach (var block in orderedBlocks)
            buckets[block] = new List<(RichTextBlock block, Paragraph para, int matchInPara)>();

        List<(RichTextBlock block, Paragraph para, int matchInPara)>? unmatched = null;
        foreach (var entry in _matchParagraphs)
        {
            if (buckets.TryGetValue(entry.block, out var bucket))
                bucket.Add(entry);
            else
                (unmatched ??= new List<(RichTextBlock block, Paragraph para, int matchInPara)>()).Add(entry);
        }

        var reordered = new List<(RichTextBlock block, Paragraph para, int matchInPara)>(_matchParagraphs.Count);
        foreach (var block in orderedBlocks)
            reordered.AddRange(buckets[block]);
        if (unmatched is not null)
            reordered.AddRange(unmatched);

        bool changed = false;
        for (int i = 0; i < reordered.Count; i++)
        {
            if (!ReferenceEquals(reordered[i].block, _matchParagraphs[i].block)
                || !ReferenceEquals(reordered[i].para, _matchParagraphs[i].para)
                || reordered[i].matchInPara != _matchParagraphs[i].matchInPara)
            {
                changed = true;
                break;
            }
        }

        if (!changed)
            return;

        _matchParagraphs.Clear();
        _matchParagraphs.AddRange(reordered);
        InvalidateParagraphIndexCache();
    }

    /// <summary>
    /// Ensures the preview panel is in sections mode. If it is already in sections mode,
    /// existing sections are preserved. If switching from PreviewBlock mode, clears
    /// PreviewBlock and match state but keeps any already-added section children.
    /// </summary>
    private void EnsureSectionsSurface()
    {
        ClearPreviewBlockContentBackground();
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            LogService.Instance.Verbose("Preview", "EnsureSectionsSurface: already visible, preserving existing sections");
            return;
        }
        LogService.Instance.Verbose("Preview", "EnsureSectionsSurface: switching to sections mode, clearing PreviewBlock");

        PreviewBlock.Blocks.Clear();
        PreviewBlock.Visibility = Visibility.Collapsed;
        PreviewSectionsPanel.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Collapsed);
        HideMatchNavPanel();
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        _sectionMatchNavs.Clear();
        _sectionGutterBlocks.Clear();
        SetHorizontalPreviewScroll(PreviewScrollViewer, enabled: false);
        EnsurePreviewViewChangedHooked();
    }

    /// <summary>
    /// Hooks PreviewScrollViewer.ViewChanged once to drive viewport-based
    /// lazy section virtualization. Sections outside the viewport remain
    /// collapsed (and un-materialized) until they scroll close enough to be
    /// visible, capping peak XAML memory in large multi-file previews.
    /// </summary>
    private void EnsurePreviewViewChangedHooked()
    {
        if (_previewViewChangedHooked) return;
        _previewViewChangedHooked = true;
        PreviewScrollViewer.ViewChanged += OnPreviewScrollViewChanged;
        PreviewScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewPointerWheelChanged),
            handledEventsToo: true);
        PreviewScrollViewer.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => NotePreviewManualScrollInput("pointer")),
            handledEventsToo: true);
        PreviewScrollViewer.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (IsPreviewScrollKey(e.Key))
                    NotePreviewManualScrollInput($"key:{e.Key}");
            }),
            handledEventsToo: true);
    }

    private void OnPreviewPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl+wheel — and a precision-touchpad pinch, which Windows delivers as a
        // Ctrl-modified wheel — zooms the preview text instead of scrolling. The
        // synthesized Ctrl from a pinch rides on the wheel message (e.KeyModifiers),
        // not the physical keyboard state, so IsPreviewZoomModifierActive checks both.
        if (PreviewScrollViewer.Visibility == Visibility.Visible)
        {
            var zoomProps = e.GetCurrentPoint(PreviewScrollViewer).Properties;
            if (zoomProps.MouseWheelDelta != 0
                && !zoomProps.IsHorizontalMouseWheel
                && IsPreviewZoomModifierActive(e))
            {
                AdjustPreviewTextZoom(zoomProps.MouseWheelDelta);
                e.Handled = true;
                return;
            }
        }

        NotePreviewManualScrollInput("wheel");

        if (PreviewScrollViewer.Visibility != Visibility.Visible)
            return;

        var properties = e.GetCurrentPoint(PreviewScrollViewer).Properties;
        int delta = properties.MouseWheelDelta;
        if (delta == 0)
            return;

        bool horizontalWheel = properties.IsHorizontalMouseWheel;
        double horizontalOffsetBefore = PreviewScrollViewer.HorizontalOffset;
        double verticalOffsetBefore = PreviewScrollViewer.VerticalOffset;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            ApplyPreviewPointerWheelFallback(delta, horizontalWheel, horizontalOffsetBefore, verticalOffsetBefore));
    }

    private void ApplyPreviewPointerWheelFallback(
        int delta,
        bool horizontalWheel,
        double horizontalOffsetBefore,
        double verticalOffsetBefore)
    {
        if (PreviewScrollViewer.Visibility != Visibility.Visible)
            return;

        if (horizontalWheel)
        {
            if (PreviewScrollViewer.ScrollableWidth <= 0)
                return;
            if (Math.Abs(PreviewScrollViewer.HorizontalOffset - horizontalOffsetBefore) > 0.5)
                return;

            double targetX = Math.Clamp(horizontalOffsetBefore - delta, 0, PreviewScrollViewer.ScrollableWidth);
            if (Math.Abs(targetX - horizontalOffsetBefore) <= 0.5)
                return;

            PreviewScrollViewer.ChangeView(targetX, null, null, disableAnimation: true);
            return;
        }

        if (PreviewScrollViewer.ScrollableHeight <= 0)
            return;
        if (Math.Abs(PreviewScrollViewer.VerticalOffset - verticalOffsetBefore) > 0.5)
            return;

        double targetY = Math.Clamp(verticalOffsetBefore - delta, 0, PreviewScrollViewer.ScrollableHeight);
        if (Math.Abs(targetY - verticalOffsetBefore) <= 0.5)
            return;

        PreviewScrollViewer.ChangeView(null, targetY, null, disableAnimation: true);
    }

    /// <summary>
    /// True when the Control modifier is active for a preview wheel event —
    /// either a physically held Ctrl key or the synthesized Ctrl that a
    /// precision-touchpad pinch-to-zoom rides in on the wheel message.
    /// </summary>
    private static bool IsPreviewZoomModifierActive(PointerRoutedEventArgs e)
        => e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)
            || Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Zooms the preview text by nudging <see cref="MainViewModel.PreviewTextFontSize"/>.
    /// Wheel deltas are accumulated so both a mouse notch (120 units) and the many
    /// small deltas of a touchpad pinch translate to smooth one-point font steps.
    /// The property change reflows every preview surface via ApplyPreviewTextFontSettings.
    /// </summary>
    private int _previewZoomWheelAccumulator;

    private void AdjustPreviewTextZoom(int wheelDelta)
    {
        int current = ResolvePreviewTextFontSize();
        int updated = Yagu.Helpers.PreviewZoomMath.ApplyWheelZoom(current, wheelDelta, ref _previewZoomWheelAccumulator);
        if (updated != ViewModel.PreviewTextFontSize)
            ViewModel.PreviewTextFontSize = updated;
    }

    private void NotePreviewManualScrollInput(string source)
    {
        _lastPreviewManualScrollTick = Environment.TickCount64;
        _previewManualScrollVersion++;
        CancelPendingPreviewMatchNavigation();
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"Preview manual scroll input: source={source}, version={_previewManualScrollVersion}");
    }

    private bool IsPreviewManualScrollActive()
    {
        long lastTick = _lastPreviewManualScrollTick;
        return lastTick != 0 && Environment.TickCount64 - lastTick < 400;
    }

    private static bool IsPreviewScrollKey(Windows.System.VirtualKey key)
        => key is Windows.System.VirtualKey.Up
            or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.Left
            or Windows.System.VirtualKey.Right
            or Windows.System.VirtualKey.PageUp
            or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home
            or Windows.System.VirtualKey.End
            or Windows.System.VirtualKey.Space;

    /// <summary>
    /// Yields to the dispatcher at Low priority. Unlike Task.Yield (which
    /// reposts at Normal priority and can starve Input/Render), this lets
    /// pointer/keyboard input and frame rendering run before we resume —
    /// keeping the window marked "responsive" by Windows during long bulk
    /// preview-build loops.
    /// </summary>
    private Task YieldLowAsync()
    {
        var tcs = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => tcs.SetResult()))
        {
            tcs.SetResult();
        }
        return tcs.Task;
    }

    private void OnPreviewScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        QueueActiveMatchOverlayRefresh();
        RefreshPreviewCustomSelectionOverlay();
        UpdateStickyFileHeader();
        TryAutoLoadMoreOnScroll();
        TryAutoLoadOverflowOnScroll();
        if (e.IsIntermediate) return;
        if (_lazySections.Count == 0) return;
        if (_viewportMaterializePending) return;
        _viewportMaterializePending = true;
        // Defer to the next dispatcher pass so multiple ViewChanged events from
        // a flick/drag coalesce into a single materialization sweep.
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewportMaterializePending = false;
            MaterializeVisibleLazySections();
        });
    }

    /// <summary>
    /// Shows a sticky file-header banner at the top of the preview viewport when
    /// the topmost visible Expander's own header has scrolled above the viewport,
    /// so the user always knows which file the visible content belongs to.
    /// </summary>
    private void UpdateStickyFileHeader()
    {        try
        {
            if (PreviewSectionsPanel.Visibility != Visibility.Visible
                || PreviewSectionsPanel.Children.Count == 0)
            {
                HideStickyFileHeader();
                return;
            }

            var sv = PreviewScrollViewer;
            double vpTop = 0; // viewport-relative top (we transform expanders into ScrollViewer space)
            double vpBottom = sv.ViewportHeight;
            if (vpBottom <= 0)
            {
                HideStickyFileHeader();
                return;
            }

            Expander? topMostInView = null;
            bool anyHeaderAboveViewport = false;
            foreach (var child in PreviewSectionsPanel.Children)
            {
                if (child is not Expander exp) continue;
                Windows.Foundation.Point topLeft;
                double height;
                try
                {
                    var t = exp.TransformToVisual(sv);
                    topLeft = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    height = exp.ActualHeight;
                }
                catch { continue; }
                double expTop = topLeft.Y;
                double expBottom = expTop + height;
                if (expBottom <= vpTop) continue;       // entirely above
                if (expTop >= vpBottom) break;          // entirely below — children are in order
                if (expTop < vpTop)
                {
                    anyHeaderAboveViewport = true;
                    topMostInView = exp;
                    break;                               // first visible-but-header-clipped expander wins
                }
                // Header is in view — no sticky needed for this section.
                break;
            }

            if (!anyHeaderAboveViewport || topMostInView is null
                || !_expanderFilePaths.TryGetValue(topMostInView, out var path))
            {
                HideStickyFileHeader();
                return;
            }

            // Only rebuild the header content when the active section changes —
            // otherwise we'd thrash event handlers on every ViewChanged tick.
            if (!ReferenceEquals(_stickyHeaderExpander, topMostInView))
            {
                FrameworkElement? headerContent = null;
                if (_expanderHeaderArgs.TryGetValue(topMostInView, out var args))
                {
                    headerContent = BuildPreviewSectionHeader(args.FilePath, args.Detail, args.Block, args.Results);
                }
                else
                {
                    headerContent = BuildPreviewSectionHeader(path, detail: null);
                }
                StickyFileHeader.Child = headerContent;
                _stickyHeaderExpander = topMostInView;
            }
            StickyFileHeader.Visibility = Visibility.Visible;
            StickyFileHeader.Height = ViewModel.PreviewStickyHeaderHeight;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"UpdateStickyFileHeader threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HideStickyFileHeader()
    {
        _stickyHeaderExpander = null;
        StickyFileHeader.Child = null;
        StickyFileHeader.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Tap on the sticky file-header banner (outside any of its action buttons)
    /// toggles expand/collapse on the section it represents. Button taps do not
    /// reach here because Button handles the Tapped routed event itself.
    /// </summary>
    private void StickyFileHeader_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.Handled) return;
        var expander = _stickyHeaderExpander;
        if (expander is null) return;

        // The buttons inside the sticky header (Show full file, Copy, Open,
        // Edit, Show in Explorer, Dismiss) raise Click via PointerPressed but
        // do NOT mark the bubbling Tapped event as handled. Without this guard,
        // a click on any of those buttons would also collapse the file
        // expander. Walk up from the original source and bail if a Button is
        // in the path.
        if (e.OriginalSource is DependencyObject src && IsInsideButton(src))
            return;

        try
        {
            expander.IsExpanded = !expander.IsExpanded;
            // After collapse, the topmost section will be a different one; refresh.
            UpdateStickyFileHeader();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"StickyFileHeader_Tapped threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsInsideButton(DependencyObject element)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return true;
        }
        return false;
    }

    /// <summary>
    /// When the scroll position is near the bottom and there are still files
    /// behind a "Show more" button, auto-load the next AutoLoadChunkSize
    /// files. Triggered both by manual scrolling and by match-nav pagination
    /// (Next-match scrolls the active section into view, which fires
    /// ViewChanged → this handler).
    /// </summary>
    private void TryAutoLoadMoreOnScroll()
    {
        if (_autoLoadMoreInFlight) return;
        var list = _deferredOrderedFiles;
        var panel = _deferredButtonPanel;
        var allSelected = _deferredAllSelected;
        if (list is null || panel is null || allSelected is null) return;
        if (_deferredCursor >= list.Count) return;

        var sv = PreviewScrollViewer;
        double vpH = sv.ViewportHeight;
        if (vpH <= 0) return;
        // Trigger when within one viewport of the bottom.
        double remaining = sv.ScrollableHeight - sv.VerticalOffset;
        if (remaining > vpH) return;

        int chunkEnd = Math.Min(list.Count, _deferredCursor + AutoLoadChunkSize);
        int chunkSize = chunkEnd - _deferredCursor;
        int gen = _deferredGen;
        int pageStart = _deferredCursor;
        _autoLoadMoreInFlight = true;

        // Replace the button panel contents with a "Loading N more..." indicator
        // so the user gets feedback during the auto-load. LoadMoreSectionsAsync
        // will remove this panel and (if more remain) re-add a fresh
        // "Show more" / "Show all" button pair.
        try
        {
            panel.Children.Clear();
            panel.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Loading {chunkSize:N0} more file(s)\u2026",
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        catch { /* non-fatal cosmetic update */ }

        _ = AutoLoadMoreChunkAsync(panel, list, pageStart, chunkEnd, allSelected, gen);
    }

    private async Task AutoLoadMoreChunkAsync(
        StackPanel panel,
        List<KeyValuePair<string, List<SearchResult>>> list,
        int pageStart, int chunkEnd,
        List<SearchResult> allSelected, int gen)
    {
        try
        {
            await LoadMoreSectionsAsync(panel, list, pageStart, chunkEnd, allSelected, gen);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"AutoLoadMoreChunkAsync threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _autoLoadMoreInFlight = false;
        }
    }

    /// <summary>
    /// When the user scrolls near the bottom of a section that has overflow
    /// (truncated matches), automatically expand the next chunk so reading
    /// is seamless. The chunk size is controlled by the
    /// <see cref="AppSettings.PreviewAutoLoadMatches"/> setting.
    /// </summary>
    private void TryAutoLoadOverflowOnScroll()
    {
        if (_autoLoadOverflowInFlight) return;
        if (IsOverflowAutoLoadSuppressedForMatchNavigation()) return;
        int autoLoadCount = Math.Min(ViewModel.PreviewAutoLoadMatches, AutoLoadOverflowMaxMatchesPerFrame);
        if (autoLoadCount <= 0) return;
        if (_sectionOverflow.Count == 0) return;

        var sv = PreviewScrollViewer;
        double vpH = sv.ViewportHeight;
        if (vpH <= 0) return;
        double vpBottom = sv.VerticalOffset + vpH;

        double prefetchBuffer = vpH * OverflowPrefetchBufferViewports;

        // Find any section whose truncation notice is within the prefetch buffer
        // of the current scroll position. This runs even during intermediate
        // scrolling so the next chunk is usually present before the user reaches
        // the truncation boundary.
        foreach (var (section, ov) in _sectionOverflow)
        {
            if (ov.NoticePara is null) continue;
            try
            {
                // Get the position of the truncation notice relative to the scroll viewer content.
                var transform = section.TransformToVisual(sv);
                var sectionTop = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y + sv.VerticalOffset;
                double sectionBottom = sectionTop + section.ActualHeight;

                // Trigger when the bottom of the section (where the notice lives)
                // is inside or near the current view.
                if (sectionBottom <= vpBottom + prefetchBuffer && sectionBottom >= sv.VerticalOffset - vpH)
                {
                    _autoLoadOverflowInFlight = true;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                    {
                        try
                        {
                            ExpandOverflowChunk(section, autoLoadCount);
                        }
                        finally
                        {
                            _autoLoadOverflowInFlight = false;
                        }

                        ScheduleOverflowPrefetchContinuation();
                    });
                    return; // Only expand one section per scroll event.
                }
            }
            catch { /* TransformToVisual can throw if not in tree */ }
        }
    }

    private void SuppressOverflowAutoLoadForMatchNavigation()
        => _suppressOverflowAutoLoadUntilTick = Environment.TickCount64 + MatchNavigationOverflowAutoLoadSuppressMs;

    private bool IsOverflowAutoLoadSuppressedForMatchNavigation()
        => Environment.TickCount64 < _suppressOverflowAutoLoadUntilTick;

    private void ScheduleOverflowPrefetchContinuation()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            timer.Stop();
            if (handler is not null)
                timer.Tick -= handler;
            TryAutoLoadOverflowOnScroll();
        };
        timer.Tick += handler;
        timer.Start();
    }

    /// <summary>
    /// Expands up to <paramref name="matchCount"/> matches from a section's
    /// overflow, similar to <see cref="ExpandSectionNextChunk"/> but with a
    /// configurable chunk size.
    /// </summary>
    private void ExpandOverflowChunk(RichTextBlock section, int matchCount)
    {
        var sw = Stopwatch.StartNew();
        if (!_sectionOverflow.TryGetValue(section, out var ov)) return;
        int chunkSize = Math.Min(matchCount, ov.RemainingResults.Count);
        if (ov.IsHighlightMode && ov.AllLines != null && chunkSize > 0)
        {
            int boundaryLine = ov.RemainingResults[chunkSize - 1].LineNumber;
            while (chunkSize < ov.RemainingResults.Count
                   && ov.RemainingResults[chunkSize].LineNumber == boundaryLine)
            {
                chunkSize++;
            }
        }
        if (chunkSize <= 0)
        {
            _sectionOverflow.Remove(section);
            return;
        }

        // Remove the existing truncation notice.
        if (ov.NoticePara != null)
        {
            section.Blocks.Remove(ov.NoticePara);
            // Also remove the corresponding gutter spacer (always the last block).
            if (_sectionGutterBlocks.TryGetValue(section, out var gb) && gb.Blocks.Count > 0)
                gb.Blocks.RemoveAt(gb.Blocks.Count - 1);
        }

        // Compute insertion point in _matchParagraphs.
        int insertAt = -1;
        for (int i = _matchParagraphs.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_matchParagraphs[i].block, section))
            {
                insertAt = i + 1;
                break;
            }
        }
        if (insertAt < 0) insertAt = _matchParagraphs.Count;

        _sectionMatchNavs.TryGetValue(section, out var sn);
        int beforeCount = _matchParagraphs.Count;
        bool truncatePreviewLines = ShouldTruncateOverflowPreviewLines();

        int consumed = 0;
        if (ov.IsHighlightMode && ov.AllLines != null)
        {
            AppendHighlightMatchWindows(
                section,
                ov.RemainingResults,
                ov.AllLines,
                ov.Rx,
                sn,
                ov.PreviewLines,
                MaxMatchEntriesPerExpandChunk,
                chunkSize,
                MaxPreviewBlocksPerExpandChunk,
                out consumed,
                out _,
                out int lastRenderedLine,
                ov.LastRenderedLine,
                truncatePreviewLines);
            ov.LastRenderedLine = Math.Max(ov.LastRenderedLine, lastRenderedLine);

            // Fallback for single-line files: all results are on an already-rendered
            // line, so render a continuation window around each occurrence instead
            // of consuming same-line results without navigable entries.
            if (truncatePreviewLines && consumed == 0 && ov.RemainingResults.Count > 0)
            {
                int blocksAdded = 0;
                for (int ri = 0; ri < chunkSize && ri < ov.RemainingResults.Count; ri++)
                {
                    if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                        break;
                    if (_matchParagraphs.Count - beforeCount >= MaxMatchEntriesPerExpandChunk)
                        break;

                    var r = ov.RemainingResults[ri];

                    int lineIndex = r.LineNumber - 1;
                    string line = (lineIndex >= 0 && lineIndex < ov.AllLines.Length)
                        ? ov.AllLines[lineIndex] : string.Empty;

                    int contentStartIndex = section.Blocks.Count;
                    int gutterStartIndex = GetGutterBlockCount(section);
                    AddPreviewLineParagraphsAroundResult(
                        section, line, r.LineNumber, r, ov.Rx,
                        _matchParagraphs, sn,
                        out int addedParagraphs, out _,
                        truncate: truncatePreviewLines,
                        continuationGutter: true,
                        targetOnlyMatchEntry: true,
                        maxParagraphs: MaxPreviewBlocksPerExpandChunk - blocksAdded);
                    MoveAppendedPreviewLineBesideExistingLine(
                        section,
                        r.LineNumber,
                        contentStartIndex,
                        gutterStartIndex,
                        addedParagraphs);
                    blocksAdded += addedParagraphs;
                    consumed++;
                }
            }
        }
        else
        {
            var matchLineNums = new HashSet<int>(ov.RemainingResults.Take(chunkSize).Select(r => r.LineNumber));
            var renderedLineNumbers = new HashSet<int>();
            int blocksAdded = 0;
            for (int ri = 0; ri < chunkSize; ri++)
            {
                if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                    break;

                var r = ov.RemainingResults[ri];

                // Skip results whose line was already rendered (multiple matches on same line).
                if (!renderedLineNumbers.Add(r.LineNumber))
                {
                    consumed++;
                    continue;
                }

                var sep = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var sepRun = new Run { Text = $"{new string('\u2500', 6)}{label}{new string('\u2500', 6)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                section.Blocks.Add(sep);
                SyncGutterSpacer(section, sep.Margin);
                blocksAdded++;

                var lines = GetPreviewLines(r, ov.AllLines, ov.PreviewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                        break;

                    bool isMatchLine = matchLineNums.Contains(lineNum);
                    AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, ov.Rx, truncate: truncatePreviewLines,
                        lineNum == r.LineNumber ? _matchParagraphs : null, sn, out int addedParagraphs,
                        maxParagraphs: MaxPreviewBlocksPerExpandChunk - blocksAdded);
                    blocksAdded += addedParagraphs;
                }
                consumed++;
                if (_matchParagraphs.Count - beforeCount >= MaxMatchEntriesPerExpandChunk)
                    break;
            }
        }

        ov.RemainingResults.RemoveRange(0, consumed);

        int addedCount = _matchParagraphs.Count - beforeCount;
        if (addedCount > 0 && insertAt < beforeCount)
        {
            var newEntries = _matchParagraphs.GetRange(beforeCount, addedCount);
            _matchParagraphs.RemoveRange(beforeCount, addedCount);
            _matchParagraphs.InsertRange(insertAt, newEntries);
            if (_currentMatchIndex >= insertAt)
                _currentMatchIndex += addedCount;
        }

        InvalidateParagraphIndexCache(section);
        if (sn != null) sn.IndexByMatch = null;

        ov.RenderedSoFar += consumed;

        if (ov.RemainingResults.Count > 0)
        {
            ov.NoticePara = AppendTruncationNotice(section, ov.OriginalTotal, ov.RenderedSoFar);
        }
        else
        {
            ov.NoticePara = null;
            _sectionOverflow.Remove(section);
        }

        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();

        sw.Stop();
        LogService.Instance.Info("Preview", $"ExpandOverflowChunk(scroll): consumed={consumed}, addedEntries={addedCount}, renderedSoFar={ov.RenderedSoFar}, remaining={ov.RemainingResults.Count}, elapsed={sw.ElapsedMilliseconds}ms");
    }

    private void MaterializeVisibleLazySections()
    {
        try
        {
            if (_lazySections.Count == 0) return;
            double vpH = PreviewScrollViewer.ViewportHeight;
            if (vpH <= 0) return;
            // Pre-materialize up to ~1.5 viewports above and below the visible area
            // so users see content immediately on small scrolls instead of a flash
            // of blank/collapsed sections.
            double bufferPx = vpH * 1.5;
            var children = PreviewSectionsPanel.Children;
            // Collect targets first so we don't mutate Expander state while
            // walking the panel (the Expanding handler can reentrantly scroll
            // and call back into us via ViewChanged).
            var toExpand = new List<Expander>();
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not Expander exp || exp.Tag is not RichTextBlock block) continue;
                if (exp.IsExpanded) continue;
                if (!_lazySections.ContainsKey(block)) continue;

                double itemY, itemH;
                try
                {
                    var t = exp.TransformToVisual(PreviewScrollViewer);
                    var p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    itemY = p.Y;
                    itemH = exp.ActualHeight;
                }
                catch
                {
                    continue;
                }

                // Above viewport (and out of buffer) — skip.
                if (itemY + itemH < -bufferPx) continue;
                // Below viewport (and out of buffer) — sections are in document order,
                // so once we're past the buffer we can stop.
                if (itemY > vpH + bufferPx) break;

                toExpand.Add(exp);
            }

            // Expand each target on its own dispatcher tick so any synchronous
            // work done by the Expanding handler (paragraph build, scroll into
            // view, match-nav update) does not run inside the ViewChanged
            // callback or recurse into MaterializeVisibleLazySections.
            foreach (var exp in toExpand)
            {
                var captured = exp;
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (!captured.IsExpanded)
                        {
                            // Tag this block so the Expander.Expanding handler builds
                            // its content but skips ActivateSectionForBlock — a section
                            // pulled in by scrolling must not steal the selected
                            // background from the file the user is reading.
                            if (captured.Tag is RichTextBlock lazyBlock)
                                _autoMaterializingSections.Add(lazyBlock);
                            captured.IsExpanded = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning("Preview",
                            $"MaterializeVisibleLazySections: IsExpanded threw: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"MaterializeVisibleLazySections: sweep threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepends new file sections to the top of the preview panel.
    /// Shows a loading spinner when adding more than 50 files.
    /// </summary>
    private async Task PrependPreviewSectionsForFilesAsync(
        Dictionary<string, List<SearchResult>> newFiles,
        string? scrollToFile,
        SearchResult? scrollTarget = null)
    {
        LogService.Instance.Info("Preview", $"PrependPreviewSectionsForFilesAsync: newFiles={newFiles.Count}, scrollToFile='{scrollToFile}'");
        if (newFiles.Count == 0) return;

        var filesToPrepend = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, results) in newFiles)
        {
            if (_pendingPreviewFilePaths.Contains(filePath))
            {
                LogService.Instance.Info("Preview", $"PrependPreviewSectionsForFilesAsync: skipping pending file '{filePath}'");
                continue;
            }

            if (PreviewSectionExists(filePath))
            {
                LogService.Instance.Info("Preview", $"PrependPreviewSectionsForFilesAsync: skipping existing file '{filePath}'");
                continue;
            }

            _pendingPreviewFilePaths.Add(filePath);
            filesToPrepend[filePath] = results;
        }

        if (filesToPrepend.Count == 0)
        {
            if (scrollTarget is not null)
                await RevealCheckedMatchInPreviewSectionAsync(scrollTarget);
            else if (scrollToFile is not null && PreviewSectionExists(scrollToFile))
                TryScrollToPreviewSection(scrollToFile);
            return;
        }

        try
        {

        BeginPreviewContentUpdate();
        EnsurePreviewPanelVisible();
        EnsureSectionsSurface();
        Regex? rx = BuildSearchHighlightRegex();
        bool isHighlight = ViewModel.PreviewModeIndex == 1;
        int previewLines = ViewModel.PreviewContextLines;
        AddPreviewMatchTotals(
            filesToPrepend.Values.Sum(results => ComputeMatchCount(results, null, isHighlight, previewLines, rx)),
            filesToPrepend.Count);
        PreviewToolbarContent.Visibility = Visibility.Visible;

        // Cap the initial render at PreviewSectionPageSize. Adding 10k+
        // Expanders to a flat StackPanel can crash the WinUI layout engine
        // (native fail-fast in CoreMessagingXP.dll). The remainder is paged
        // in via "Show more" using the same machinery as LoadMoreSectionsAsync.
        var orderedFiles = filesToPrepend.ToList();
        int totalRequested = orderedFiles.Count;
        int pageEnd = Math.Min(totalRequested, EffectivePreviewSectionPageSize);
        bool deferRemainder = pageEnd < totalRequested;
        if (deferRemainder)
            LogService.Instance.Info("Preview",
                $"PrependPreviewSectionsForFilesAsync: capping initial render at {pageEnd:N0}/{totalRequested:N0}; remainder deferred to 'Show more'.");

        bool showSpinner = pageEnd > EffectivePreviewSectionPageSize / 2 || deferRemainder;
        if (showSpinner)
            ShowProgressOverlay($"Adding {pageEnd:N0} of {totalRequested:N0} files\u2026", 0);

        // Batch-read file contents off the UI thread — but only for the
        // sections we will eagerly expand. Lazy/collapsed sections read their
        // file on demand inside MaterializeLazySection.
        var fileList = orderedFiles.GetRange(0, pageEnd);
        bool bulkInsert = fileList.Count > BulkExpandLimit;
        int eagerCount = bulkInsert ? Math.Min(BulkExpandLimit, fileList.Count) : fileList.Count;
        var fileContents = await ReadAllFileContentsAsync(
            eagerCount == fileList.Count ? fileList : fileList.GetRange(0, eagerCount));

        // Build all Expanders OFF-tree first. Adding to PreviewSectionsPanel.Children
        // while the panel is in the visual tree triggers a layout invalidation per
        // insert; with 10k+ files that produces a multi-second UI freeze. Building
        // off-tree is pure C# object construction (no layout cost).
        int filesToAdd = fileList.Count;
        var built = new List<Expander>(filesToAdd);
        int fileIndex = 0;
        foreach (var (filePath, results) in fileList)
        {
            bool expanded = !bulkInsert || fileIndex < BulkExpandLimit;
            var (section, expander) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results,
                isExpanded: expanded, addToPanel: false);

            string[]? allLines = null;
            if (expanded)
                fileContents.TryGetValue(filePath, out allLines);

            if (expanded)
            {
                if (isHighlight)
                    await BuildHighlightSectionAsync(section, results, allLines, previewLines, rx);
                else
                    BuildConcatenatedSection(section, results, allLines, previewLines, rx);
            }
            else
            {
                // Approximate match count without reading the file. Exact count
                // is recomputed when the section materializes.
                int lazyCount = ComputeMatchCount(results, null, isHighlight, previewLines, rx);
                _lazySections[section] = new LazySection
                {
                    FilePath = filePath,
                    Results = results,
                    AllLines = null,
                    PreviewLines = previewLines,
                    IsHighlight = isHighlight,
                    MatchCount = lazyCount,
                };
                _lazyMatchCount += lazyCount;
            }

            built.Add(expander);

            // Yield to the dispatcher periodically. We use a low-priority repost
            // (YieldLowAsync) so Input and Render priority work runs before we
            // resume — without this, Windows flags the window "Not responding"
            // even though we're pumping the queue.
            if (++fileIndex % PreviewYieldBatchSize == 0)
            {
                if (showSpinner)
                    UpdateProgressOverlay(fileIndex * 15 / filesToAdd); // build phase: 0-15%
                await YieldLowAsync();
            }
        }

        // Phase 2: insert built expanders into the live panel in batches.
        // We insert at the head (insertIndex grows) so newer files appear first.
        int insertIndex = 0;
        for (int i = 0; i < built.Count; i++)
        {
            PreviewSectionsPanel.Children.Insert(insertIndex++, built[i]);
            InvalidateScrollPositionCache();
            if (i == 0)
                CompletePreviewContentUpdate();
            if ((i + 1) % PreviewYieldBatchSize == 0)
            {
                if (showSpinner)
                    UpdateProgressOverlay(15 + (i + 1) * 85 / built.Count); // insert phase: 15-100%
                await YieldLowAsync();
            }
        }

        ReorderMatchParagraphsToPreviewSectionOrder();
        UnboxCurrentMatch();
        HideActiveMatchOverlay();
        InvalidatePendingMatchScrolls();
        _currentMatchIndex = -1;
        _initialMatchScrolled = false;

        if (showSpinner)
            HideProgressOverlay();

        // If we capped the initial render, append a "Show more" button so the
        // user can page in the rest. Uses the same LoadMoreSectionsAsync path
        // as the file-group flow.
        if (deferRemainder)
        {
            var allSelectedRemainder = new List<SearchResult>();
            for (int i = pageEnd; i < orderedFiles.Count; i++)
                allSelectedRemainder.AddRange(orderedFiles[i].Value);
            int gen = ++_previewUpdateGen;
            // Stash deferred state so the match-nav label can include not-yet-
            // inserted matches and so scroll-to-bottom can auto-load the next chunk.
            _deferredOrderedFiles = orderedFiles;
            _deferredCursor = pageEnd;
            _deferredAllSelected = allSelectedRemainder;
            _deferredGen = gen;
            AddShowMoreSectionsButton(orderedFiles, pageEnd, allSelectedRemainder, gen);
        }

        // Update match nav and file label to include the new sections.
        var totalFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        var (deferredFileCount, deferredMatchCount) = GetDeferredCounts();
        int totalMatches = GetStableMatchNavTotal();
        int grandFileCount = _previewTotalFileCount > 0 ? _previewTotalFileCount : totalFiles + deferredFileCount;
        SetPreviewFileLabel(
            $"{totalMatches:N0} selected matches across {grandFileCount:N0} file(s)",
            string.Join(Environment.NewLine, GetExistingPreviewFilePaths()));
        _previewResult = filesToPrepend.Values.First().First();
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        UpdateExpandAllButtonVisibility();

        // Scroll to the target file or, for a checked match, to the exact line.
        if (scrollTarget is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                await RevealCheckedMatchInPreviewSectionAsync(scrollTarget);
            });
        }
        else if (scrollToFile is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                TryScrollToPreviewSection(scrollToFile);
            });
        }

        // After layout settles, materialize any lazy sections that already fall
        // within the viewport (e.g. when the viewport is taller than the
        // BulkExpandLimit-many initially expanded sections).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MaterializeVisibleLazySections);

        // Flush log synchronously so a subsequent layout-engine fail-fast still
        // leaves the prior progress lines on disk for diagnostics.
        try { LogService.Instance.Flush(); } catch { }
        }
        finally
        {
            foreach (var filePath in filesToPrepend.Keys)
                _pendingPreviewFilePaths.Remove(filePath);
            if (_previewContentPending)
                CompletePreviewContentUpdate();
        }
    }
}
