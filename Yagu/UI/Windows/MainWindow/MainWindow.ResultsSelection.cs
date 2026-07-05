using System.Diagnostics.CodeAnalysis;
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
using Yagu.ViewModels;
namespace Yagu;

/// <summary>
/// Result group expansion, selection gestures, match checkbox behavior, and checked-match preview updates.
/// </summary>
public sealed partial class MainWindow
{
    private const int FileGroupCollapseVisibleResultsClearDelayMs = 250;

    private readonly HashSet<FileGroup> _visibleResultsEnsureInProgress = new();

    private async void OnFileGroupExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is FileGroup g)
        {
            try
            {
                // Iter 16: ensure any compact evicted-stubs are materialized into Items
                // before the EnsureVisible code path reads group.Count and indexes into
                // the group via ShowMore.
                g.MaterializeEvictedStubs();
                await EnsureVisibleResultsForExpandedGroupSerializedAsync(g, "expanding").ConfigureAwait(true);

                if (!ReferenceEquals(sender.DataContext, g))
                    return;

                // Force the ListView's virtualizing panel to re-measure this item container
                // after content was added, so it allocates the correct height.
                InvalidateListViewItemContainer(sender);
                sender.InvalidateMeasure();
                QueueResultsFileOverlayUpdate();

                LogService.Instance.Info("Preview", $"OnFileGroupExpanding: expand only file='{g.FilePath}', matchCount={g.Count}");
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"OnFileGroupExpanding threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void InvalidateListViewItemContainer(DependencyObject element)
    {
        // Walk up the visual tree to find the ListViewItem container
        var current = element;
        while (current != null)
        {
            if (current is ListViewItem lvi)
            {
                lvi.InvalidateMeasure();
                // Also invalidate the panel that hosts list view items
                if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(lvi) is FrameworkElement panel)
                    panel.InvalidateMeasure();
                break;
            }
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handlers are bound as instance methods.")]
    private void OnFileGroupCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (sender.DataContext is FileGroup g)
            _ = ClearVisibleResultsAfterCollapseAsync(g);
    }

    private async Task ClearVisibleResultsAfterCollapseAsync(FileGroup group)
    {
        try
        {
            await Task.Delay(FileGroupCollapseVisibleResultsClearDelayMs).ConfigureAwait(false);

            bool enqueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (!group.IsExpanded)
                    {
                        LogService.Instance.Info("Preview", $"OnFileGroupCollapsed: clearing visible results file='{group.FilePath}', matchCount={group.Count}");
                        group.ClearVisibleResults();
                        QueueResultsFileOverlayUpdate();
                    }
                    else if (LogService.Instance.IsVerboseEnabled)
                    {
                        LogService.Instance.Verbose("Preview", $"OnFileGroupCollapsed: ignored transient collapse file='{group.FilePath}', matchCount={group.Count}");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview", $"OnFileGroupCollapsed clear failed for '{group.FilePath}': {ex.GetType().Name}: {ex.Message}", ex);
                }
            });

            if (!enqueued)
                LogService.Instance.Warning("Preview", $"OnFileGroupCollapsed clear was skipped because the dispatcher rejected the callback for '{group.FilePath}'");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", $"OnFileGroupCollapsed clear scheduling failed for '{group.FilePath}': {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private async Task EnsureVisibleResultsForExpandedGroupAsync(FileGroup group)
    {
        LogService.Instance.Info("FileGroup",
            $"EnsureVisible START: file='{Path.GetFileName(group.FilePath)}', " +
            $"Count={group.Count}, VisibleCount={group.VisibleResults.Count}, HasMore={group.HasMore}, IsExpanded={group.IsExpanded}");

        // OOM safety net: under critical memory pressure, materializing more rows
        // into the XAML tree can trigger a non-recoverable failfast. Pause and
        // surface a message instead of crashing.
        if (!TryEnsureResultsMemoryHeadroom("expanding file group", group))
            return;

        try
        {
            if (group.VisibleResults.Count == 0 && group.Count > 0)
            {
                await ShowMoreVisibleResultsIncrementalAsync(group, FileGroup.PageSize).ConfigureAwait(true);

                // Dump sample items to diagnose render issue
                int sampleCount = Math.Min(10, group.VisibleResults.Count);
                for (int i = 0; i < sampleCount; i++)
                {
                    var r = group.VisibleResults[i];
                    LogService.Instance.Info("FileGroup",
                        $"EnsureVisible SAMPLE[{i}]: line={r.LineNumber}, IsEvicted={r.IsEvicted}, " +
                        $"MatchLine.Length={r.MatchLine.Length}, DiskOffset={r.DiskOffset}, " +
                        $"MatchLine='{(r.MatchLine.Length > 60 ? r.MatchLine[..60] : r.MatchLine)}'");
                }

                LogService.Instance.Info("FileGroup",
                    $"EnsureVisible AFTER ShowMore: file='{Path.GetFileName(group.FilePath)}', " +
                    $"Count={group.Count}, VisibleCount={group.VisibleResults.Count}, HasMore={group.HasMore}");
                return;
            }

            await HydrateVisibleResultsAsync(group).ConfigureAwait(true);
        }
        catch (OutOfMemoryException ex)
        {
            HandleResultsOutOfMemory("expanding file group", group, ex);
        }
    }

    private async Task EnsureVisibleResultsForExpandedGroupSerializedAsync(FileGroup group, string caller)
    {
        if (!_visibleResultsEnsureInProgress.Add(group))
        {
            LogService.Instance.Verbose("FileGroup",
                $"EnsureVisible skipped duplicate caller={caller} file='{Path.GetFileName(group.FilePath)}'");
            return;
        }

        try
        {
            await EnsureVisibleResultsForExpandedGroupAsync(group).ConfigureAwait(true);
        }
        finally
        {
            _visibleResultsEnsureInProgress.Remove(group);
        }
    }

    private void EnsureVisibleResultsForExpandedGroup(FileGroup group)
    {
        LogService.Instance.Info("FileGroup",
            $"EnsureVisible SYNC START: file='{Path.GetFileName(group.FilePath)}', " +
            $"Count={group.Count}, VisibleCount={group.VisibleResults.Count}, HasMore={group.HasMore}, IsExpanded={group.IsExpanded}");

        if (group.VisibleResults.Count == 0 && group.Count > 0)
        {
            ShowMoreVisibleResultsIncremental(group, FileGroup.PageSize);

            int sampleCount = Math.Min(10, group.VisibleResults.Count);
            for (int i = 0; i < sampleCount; i++)
            {
                var r = group.VisibleResults[i];
                LogService.Instance.Info("FileGroup",
                    $"EnsureVisible SYNC SAMPLE[{i}]: line={r.LineNumber}, IsEvicted={r.IsEvicted}, " +
                    $"MatchLine.Length={r.MatchLine.Length}, DiskOffset={r.DiskOffset}, " +
                    $"MatchLine='{(r.MatchLine.Length > 60 ? r.MatchLine[..60] : r.MatchLine)}'");
            }

            LogService.Instance.Info("FileGroup",
                $"EnsureVisible SYNC AFTER ShowMore: file='{Path.GetFileName(group.FilePath)}', " +
                $"Count={group.Count}, VisibleCount={group.VisibleResults.Count}, HasMore={group.HasMore}");
            return;
        }

        HydrateVisibleResults(group);
    }

    private async Task HydrateVisibleResultsAsync(FileGroup group)
    {
        var evicted = group.VisibleResults.Where(item => item.IsEvicted).ToList();
        if (evicted.Count == 0) return;

        var payloads = await Task.Run(() => ViewModel.ReadHydrationPayloads(evicted)).ConfigureAwait(true);
        MainViewModel.ApplyHydrationPayloads(payloads);
    }

    private void HydrateVisibleResults(FileGroup group)
    {
        var evicted = group.VisibleResults.Where(item => item.IsEvicted).ToList();
        if (evicted.Count == 0) return;

        ViewModel.HydrateResults(evicted);
    }

    private async Task HydrateRangeAsync(FileGroup group, int start, int end)
    {
        var evicted = new List<SearchResult>();
        for (int i = start; i < end; i++)
        {
            if (group[i].IsEvicted)
                evicted.Add(group[i]);
        }

        LogService.Instance.Info("FileGroup",
            $"HydrateRange: file='{Path.GetFileName(group.FilePath)}', " +
            $"range=[{start},{end}), evictedToHydrate={evicted.Count}, totalInRange={end - start}");

        if (evicted.Count == 0) return;

        var payloads = await Task.Run(() => ViewModel.ReadHydrationPayloads(evicted)).ConfigureAwait(true);
        MainViewModel.ApplyHydrationPayloads(payloads);

        int stillEvicted = evicted.Count(r => r.IsEvicted);
        int stillEmpty = evicted.Count(r => r.MatchLine.Length == 0);
        if (stillEvicted > 0 || stillEmpty > 0)
        {
            LogService.Instance.Warning("FileGroup",
                $"HydrateRange AFTER: file='{Path.GetFileName(group.FilePath)}', " +
                $"stillEvicted={stillEvicted}, stillEmptyMatchLine={stillEmpty} (of {evicted.Count} attempted)");
        }
    }

    private void HydrateRange(FileGroup group, int start, int end)
    {
        var evicted = new List<SearchResult>();
        for (int i = start; i < end; i++)
        {
            if (group[i].IsEvicted)
                evicted.Add(group[i]);
        }

        LogService.Instance.Info("FileGroup",
            $"HydrateRange SYNC: file='{Path.GetFileName(group.FilePath)}', " +
            $"range=[{start},{end}), evictedToHydrate={evicted.Count}, totalInRange={end - start}");

        if (evicted.Count == 0) return;

        ViewModel.HydrateResults(evicted);

        int stillEvicted = evicted.Count(r => r.IsEvicted);
        int stillEmpty = evicted.Count(r => r.MatchLine.Length == 0);
        if (stillEvicted > 0 || stillEmpty > 0)
        {
            LogService.Instance.Warning("FileGroup",
                $"HydrateRange SYNC AFTER: file='{Path.GetFileName(group.FilePath)}', " +
                $"stillEvicted={stillEvicted}, stillEmptyMatchLine={stillEmpty} (of {evicted.Count} attempted)");
        }
    }

    private void OnFileGroupHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsControlKeyDown()
            || sender is not FrameworkElement header
            || header.DataContext is not FileGroup group
            || IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            return;
        }

        var point = e.GetCurrentPoint(header);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _ctrlFileHeaderGestureGroup = group;
        _ctrlFileHeaderGestureWasExpanded = group.IsExpanded;
        _ctrlFileHeaderGesturePointerId = e.Pointer.PointerId;
        e.Handled = true;
    }

    private async void OnFileGroupHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement header
            || header.DataContext is not FileGroup group
            || IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            return;
        }

        bool isTrackedCtrlHeaderClick = ReferenceEquals(group, _ctrlFileHeaderGestureGroup)
            && e.Pointer.PointerId == _ctrlFileHeaderGesturePointerId;
        if (!isTrackedCtrlHeaderClick)
            return;

        e.Handled = true;
        bool wasExpanded = _ctrlFileHeaderGestureWasExpanded;
        ClearCtrlFileHeaderGesture();
        await SelectFileGroupMatchesAndPreviewAsync(group, "ctrl click", preserveExpansionState: wasExpanded);
    }

    private async void OnFileGroupHeaderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement header
            || header.DataContext is not FileGroup g
            || g.Count == 0)
        {
            return;
        }

        if (IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            LogService.Instance.Info("Preview", $"OnFileGroupHeaderDoubleTapped: command click ignored file='{g.FilePath}', isExpanded={g.IsExpanded}");
            return;
        }

        e.Handled = true;
        await SelectFileGroupMatchesAndPreviewAsync(g, "double click");
    }

    private async Task SelectFileGroupMatchesAndPreviewAsync(FileGroup group, string reason, bool? preserveExpansionState = null)
    {
        LogService.Instance.Info("Preview", $"SelectFileGroupMatchesAndPreviewAsync: reason='{reason}', file='{group.FilePath}', matchCount={group.Count}");

        if (preserveExpansionState.HasValue)
            group.IsExpanded = preserveExpansionState.Value;

        try
        {
            SelectFileGroupMatches(group);
            _initialMatchScrolled = false;

            var results = GetPreviewableResults(group);
            if (results.Count > 0)
            {
                if (TryScrollToPreviewSection(group.FilePath))
                    return;
                var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase)
                {
                    [group.FilePath] = results
                };
                await PrependPreviewSectionsForFilesAsync(newFiles, group.FilePath);
            }
        }
        finally
        {
            if (preserveExpansionState.HasValue)
            {
                bool targetState = preserveExpansionState.Value;
                group.IsExpanded = targetState;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    group.IsExpanded = targetState;
                });
            }
        }
    }

    private void ClearCtrlFileHeaderGesture()
    {
        _ctrlFileHeaderGestureGroup = null;
        _ctrlFileHeaderGestureWasExpanded = false;
        _ctrlFileHeaderGesturePointerId = 0;
    }

    private static bool IsControlKeyDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsInsideHeaderCommand(DependencyObject? source, DependencyObject headerRoot)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, headerRoot); current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
                return true;
        }

        return false;
    }

    private void SelectFileGroupMatches(FileGroup group)
    {
        if (group.AllSelected && group.SelectedCount == group.Count)
            return;

        LogService.Instance.Info("Preview", $"SelectFileGroupMatches: file='{group.FilePath}', matchCount={group.Count}");
        _suppressPreviewUpdate = true;
        try
        {
            group.SelectAll();
        }
        finally
        {
            _suppressPreviewUpdate = false;
        }
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe)
        {
            var g = fe.DataContext as FileGroup
                ?? (fe.DataContext is SearchResult r ? FindParentGroup(r) : null);
            if (g is not null && g.Count > 0)
                ViewModel.OpenInEditor(g[0]);
        }
    }

    private async void OnMatchLineTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't trigger preview when user clicks the checkbox itself
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;

        if (sender is FrameworkElement { DataContext: SearchResult result })
        {
            result.IsSelected = !result.IsSelected;
            UpdateSelectionForMatchLine(result, nameof(OnMatchLineTapped));
            LogService.Instance.Info("Preview",
                $"OnMatchLineTapped: selection preview file='{result.FilePath}', line={result.LineNumber}, isSelected={result.IsSelected}");
            await UpdatePreviewForMatchSelectionAsync(result);
        }
    }

    private async void OnMatchLineCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: SearchResult result } toggle)
        {
            if (toggle.IsChecked is bool isChecked && result.IsSelected != isChecked)
                result.IsSelected = isChecked;

            UpdateSelectionForMatchLine(result, nameof(OnMatchLineCheckBoxClicked));
            await UpdatePreviewForMatchSelectionAsync(result);
        }
    }

    private async Task UpdatePreviewForMatchSelectionAsync(SearchResult result)
    {
        if (result.IsSelected)
        {
            if (!ViewModel.MatchLineCheckAddsToPreview) return;
            try
            {
                // Always add the newly checked match incrementally so the existing
                // preview sections are preserved. Rebuilding the whole multi-select
                // preview here tears down and re-renders every section, which the
                // user sees as all previews flickering away and back. The
                // incremental path appends to (or prepends) only the affected
                // section while keeping match totals and nav in sync.
                await EnsureCheckedMatchInPreviewAsync(result);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"UpdatePreviewForMatchSelectionAsync: failed to add selected line to preview for '{result.FilePath}' line {result.LineNumber}: {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        var remainingSelected = ViewModel.GetAllSelectedResults();
        if (remainingSelected.Count >= 1)
            // Deselecting down to a single checked match must keep the multi-section
            // sections surface (file drawer, per-section match navigation, selected
            // preview background) so the end state matches the surface produced by
            // checking a single match (EnsureCheckedMatchInPreviewAsync builds a
            // section). Routing the count==1 case to ShowSingleFilePreviewAsync used
            // the single-file PreviewBlock surface instead, which dropped the file
            // drawer and match-nav buttons and showed the per-file toolbar buttons —
            // the user saw the preview "change to an unexpected version".
            await UpdateMultiSelectPreviewAsync();
        else
        {
            _previewResult = null;
            SetPreviewFileLabel(string.Empty);
            ShowPreviewBlockSurface();
            PreviewBlock.Blocks.Clear();
            PreviewSectionsPanel.Children.Clear();
            PreviewToolbarContent.Visibility = Visibility.Collapsed;
            _matchParagraphs.Clear();
            InvalidateParagraphIndexCache();
            _currentMatchIndex = -1;
            HideMatchNavPanel();
            CompletePreviewContentUpdate();
        }
    }

    private void UpdateSelectionForMatchLine(SearchResult result, string caller)
    {
        FindParentGroup(result)?.NotifySelectionChanged();

        var selected = ViewModel.GetAllSelectedResults();
        LogService.Instance.Info("Preview", $"{caller}: selection only file='{result.FilePath}', line={result.LineNumber}, isSelected={result.IsSelected}, totalSelected={selected.Count}");
    }

    private async Task EnsureCheckedMatchInPreviewAsync(SearchResult result)
    {
        if (!result.IsSelected)
            return;

        ViewModel.HydrateResult(result);

        LogService.Instance.Info("Preview",
            $"EnsureCheckedMatchInPreviewAsync: file='{result.FilePath}', line={result.LineNumber}");

        if (!TryFindPreviewSection(result.FilePath, out var expander, out var section))
        {
            var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase)
            {
                [result.FilePath] = [result]
            };
            await PrependPreviewSectionsForFilesAsync(newFiles, result.FilePath, result);
            RefreshPreviewSectionHeaderForSelectedMatches(result.FilePath);
            return;
        }

        await RevealCheckedMatchInPreviewSectionAsync(result);
        RefreshPreviewSectionHeaderForSelectedMatches(result.FilePath);
    }

    private async Task RevealCheckedMatchInPreviewSectionAsync(SearchResult result)
    {
        if (!TryFindPreviewSection(result.FilePath, out var expander, out var section))
        {
            TryScrollToPreviewSection(result.FilePath);
            return;
        }

        EnsurePreviewPanelVisible();
        EnsureSectionsSurface();
        PreviewToolbarContent.Visibility = Visibility.Visible;
        expander.IsExpanded = true;
        await MaterializeLazySectionAsync(section);

        // A file-name-only match (LineNumber <= 0) — e.g. a filename hit, or an image
        // that was OCR'd but produced no text — has no navigable match line. Its section
        // already renders the full file-name preview, so there is nothing to reveal here.
        // Falling through would call AppendCheckedMatchContextAsync (no match paragraph is
        // ever registered for these), appending a duplicate copy of the file's content.
        if (result.LineNumber <= 0)
        {
            TryScrollToPreviewSection(result.FilePath);
            return;
        }

        if (!TryFindPreviewMatchParagraph(section, result, out var paragraph, out var matchInPara))
        {
            await AppendCheckedMatchContextAsync(section, result);
            ReorderMatchParagraphsToPreviewSectionOrder();
            if (!TryFindPreviewMatchParagraph(section, result, out paragraph, out matchInPara))
            {
                LogService.Instance.Warning("Preview",
                    $"EnsureCheckedMatchInPreviewAsync: appended line but could not locate match paragraph for '{result.FilePath}' line {result.LineNumber}");
                TryScrollToPreviewSection(result.FilePath);
                return;
            }
        }

        // A truncated window rendered with targetOnlyMatchEntry registers only the
        // clicked target occurrence as navigable. When this click reuses an existing
        // same-line window whose resolved run is a sibling occurrence without its own
        // nav entry, register it on demand so the overlay can actually move to it.
        EnsureNavEntryForParagraphMatch(section, paragraph, matchInPara);
        ApplyMatchColorToParagraphMatch(paragraph, matchInPara);
        SetCurrentMatchToMatch(section, paragraph, matchInPara);
        try
        {
            section.UpdateLayout();
            PreviewScrollViewer.UpdateLayout();
        }
        catch { }
        ScrollPreviewToLine(section, paragraph);
    }

    private async Task AppendCheckedMatchContextAsync(RichTextBlock section, SearchResult result)
    {
        Regex? rx = BuildSearchHighlightRegex();
        int previewLines = ViewModel.PreviewContextLines;
        string[]? allLines = null;

        try
        {
            allLines = await Task.Run(() => ReadAllLinesWithEncodingSync(result.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogService.Instance.Verbose("Preview",
                $"AppendCheckedMatchContextAsync: using stored context for '{result.FilePath}' line {result.LineNumber}: {ex.GetType().Name}: {ex.Message}");
        }

        _previewMutating = true;
        try
        {
        bool isHighlight = ViewModel.PreviewModeIndex == 1;
        _sectionMatchNavs.TryGetValue(section, out var sectionNav);
        int matchCountBeforeAppend = _matchParagraphs.Count;

        // In highlight mode, if the match line is already rendered (as context),
        // promote it in-place rather than appending duplicate context.
        if (isHighlight && TryPromoteContextLineToMatch(section, result, allLines, rx, sectionNav))
        {
            AddPreviewMatchTotals(Math.Max(1, _matchParagraphs.Count - matchCountBeforeAppend), 0);
            UpdateMatchNavPanel();
            UpdateSectionMatchNavPanels();
            return;
        }

        // Determine the last rendered line to avoid backwards line numbers when appending forward.
        int lastRenderedLine = isHighlight ? GetSectionLastRenderedLine(section) : 0;
        int firstRenderedLine = isHighlight && section.Blocks.Count > 0 ? GetSectionFirstRenderedLine(section) : 0;
        bool truncatePreviewLines = ShouldTruncateOverflowPreviewLines();

        var lines = GetPreviewLines(result, allLines, previewLines, fullFile: false);

        bool appendedExistingLineWindow = false;

        // In highlight mode, remove lines that are already rendered to avoid duplicates.
        if (isHighlight && lastRenderedLine > 0)
        {
            var renderedLineNumbers = GetRenderedLineNumbers(section);
            bool targetLineAlreadyRendered = renderedLineNumbers.Contains(result.LineNumber);
            lines = lines.Where(l => !renderedLineNumbers.Contains(l.lineNum)).ToList();
            if (lines.Count == 0)
            {
                if (!targetLineAlreadyRendered)
                    return;

                if (!truncatePreviewLines)
                    return;

                string lineForWindow = (allLines != null && result.LineNumber >= 1 && result.LineNumber <= allLines.Length)
                    ? allLines[result.LineNumber - 1]
                    : result.MatchLine;

                int contentStartIndex = section.Blocks.Count;
                int gutterStartIndex = GetGutterBlockCount(section);
                // Color/register ONLY the checked occurrence in this same-line window.
                // Without targetOnlyMatchEntry the window colors every regex match it
                // spans, so an unselected sibling occurrence sharing the window (e.g. a
                // second "test" inside "vitest") would wrongly render in the match color.
                // Mirrors the initial-build, overflow, and match-nav same-line windows.
                AddPreviewLineParagraphsAroundResult(
                    section,
                    lineForWindow,
                    result.LineNumber,
                    result,
                    rx,
                    _matchParagraphs,
                    sectionNav,
                    out int addedParagraphs,
                    out int matchEntriesAdded,
                    truncate: truncatePreviewLines,
                    continuationGutter: true,
                    targetOnlyMatchEntry: true,
                    maxParagraphs: MaxPreviewBlocksPerExpandChunk);
                MoveAppendedPreviewLineBesideExistingLine(
                    section,
                    result.LineNumber,
                    contentStartIndex,
                    gutterStartIndex,
                    addedParagraphs);

                if (matchEntriesAdded == 0)
                    return;

                appendedExistingLineWindow = true;
            }
        }

        // Determine whether the new content should be prepended (all new lines before existing content).
        bool shouldPrepend = !appendedExistingLineWindow && isHighlight && firstRenderedLine > 0 && lines.Count > 0
            && lines.Max(l => l.lineNum) < firstRenderedLine;

        if (!appendedExistingLineWindow && section.Blocks.Count > 0)
        {
            if (isHighlight)
            {
                if (shouldPrepend)
                {
                    // Save existing blocks, clear, we'll re-add after the new content.
                    var existingBlocks = new List<Block>();
                    for (int i = 0; i < section.Blocks.Count; i++)
                        existingBlocks.Add(section.Blocks[i]);
                    section.Blocks.Clear();

                    List<Block>? existingGutterBlocks = null;
                    RichTextBlock? gutterBlock = null;
                    if (_sectionGutterBlocks.TryGetValue(section, out gutterBlock))
                    {
                        existingGutterBlocks = new List<Block>();
                        for (int i = 0; i < gutterBlock.Blocks.Count; i++)
                            existingGutterBlocks.Add(gutterBlock.Blocks[i]);
                        gutterBlock.Blocks.Clear();
                    }

                    // Add new lines first.
                    foreach (var (line, lineNum) in lines)
                    {
                        bool isMatchLine = lineNum == result.LineNumber;
                        AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, result, rx, truncate: truncatePreviewLines, _matchParagraphs, sectionNav, out _,
                            maxParagraphs: MaxPreviewBlocksPerExpandChunk);
                    }

                    // Add gap indicator between new and old content only when
                    // there is at least one omitted source line between them.
                    if (ShouldAddGapBetweenRenderedLines(lines.Max(l => l.lineNum), firstRenderedLine))
                        AddGapIndicator(section);

                    // Re-add existing blocks.
                    foreach (var block in existingBlocks)
                        section.Blocks.Add(block);
                    if (existingGutterBlocks is not null && gutterBlock is not null)
                    {
                        foreach (var block in existingGutterBlocks)
                            gutterBlock.Blocks.Add(block);
                    }
                }
                else
                {
                    // Append after existing content with a gap indicator only
                    // when there is at least one omitted source line between them.
                    if (lines.Count > 0 && ShouldAddGapBetweenRenderedLines(lastRenderedLine, lines.Min(l => l.lineNum)))
                        AddGapIndicator(section);
                }
            }
            else
            {
                var separator = new Paragraph();
                var separatorRun = new Run { Text = $"{new string('\u2500', 6)}\u00A0Line\u00A0{result.LineNumber}\u00A0{new string('\u2500', 6)}" };
                separatorRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                separator.Inlines.Add(separatorRun);
                separator.Margin = new Thickness(0, 8, 0, 4);
                section.Blocks.Add(separator);
                SyncGutterSpacer(section, separator.Margin);
            }
        }

        // Append lines (skipped for prepend path which already added them above).
        if (!appendedExistingLineWindow && !shouldPrepend)
        {
            foreach (var (line, lineNum) in lines)
            {
                bool isMatchLine = lineNum == result.LineNumber;
                AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, result, rx, truncate: truncatePreviewLines, _matchParagraphs, sectionNav, out _,
                    maxParagraphs: MaxPreviewBlocksPerExpandChunk);
            }
        }

        InvalidateParagraphIndexCache(section);
        if (sectionNav is not null)
            sectionNav.IndexByMatch = null;

        AddPreviewMatchTotals(Math.Max(1, _matchParagraphs.Count - matchCountBeforeAppend), 0);

        var totalFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        var (deferredFileCount, deferredMatchCount) = GetDeferredCounts();
        int totalMatches = GetStableMatchNavTotal();
        int grandFileCount = _previewTotalFileCount > 0 ? _previewTotalFileCount : totalFiles + deferredFileCount;
        SetPreviewFileLabel(
            $"{totalMatches:N0} selected matches across {grandFileCount:N0} file(s)",
            string.Join(Environment.NewLine, GetExistingPreviewFilePaths()));
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        UpdateExpandAllButtonVisibility();
        }
        finally
        {
            _previewMutating = false;
        }
    }

    /// <summary>
    /// If the target line is already rendered as a context line in the section,
    /// promotes it to a highlighted match line and registers it in _matchParagraphs.
    /// Returns true if promotion succeeded (no appending needed).
    /// </summary>
    private bool TryPromoteContextLineToMatch(
        RichTextBlock section,
        SearchResult result,
        string[]? allLines,
        Regex? rx,
        SectionMatchNav? sectionNav)
    {
        // Find existing paragraph for the target line number using the
        // ConditionalWeakTable (works for sections with separate gutter blocks).
        int blockIdx = -1;
        Paragraph? existingPara = null;
        for (int i = 0; i < section.Blocks.Count; i++)
        {
            if (section.Blocks[i] is Paragraph para
                && s_paragraphLineNumbers.TryGetValue(para, out var lineObj)
                && lineObj is int lineNumber
                && lineNumber == result.LineNumber)
            {
                blockIdx = i;
                existingPara = para;
                break;
            }
        }

        if (existingPara is null)
            return false;

        if (IsMatchParagraphRegistered(section, existingPara))
            return false;

        // Promote content paragraph: clear context foreground from text runs.
        foreach (var inline in existingPara.Inlines)
        {
            if (inline is Run run)
            {
                var localValue = run.ReadLocalValue(Run.ForegroundProperty);
                if (localValue != Microsoft.UI.Xaml.DependencyProperty.UnsetValue
                    && ReferenceEquals(localValue, s_contextTextBrush))
                    run.Foreground = _matchLineBrush;
            }
        }

        // Promote gutter paragraph: change indicator and colors.
        if (_sectionGutterBlocks.TryGetValue(section, out var gutterBlock)
            && blockIdx < gutterBlock.Blocks.Count
            && gutterBlock.Blocks[blockIdx] is Paragraph gutterPara)
        {
            var inlines = gutterPara.Inlines;
            if (inlines.Count >= 2)
            {
                if (inlines[0] is Run indicator)
                {
                    indicator.Text = "\u2502"; // │
                    indicator.Foreground = s_matchAccentBrush;
                }
                if (inlines[1] is Run gutterNum)
                {
                    gutterNum.Foreground = s_matchGutterBrush;
                }
            }
        }

        // Register in _matchParagraphs so match navigation can find it.
        string lineText = (allLines != null && result.LineNumber >= 1 && result.LineNumber <= allLines.Length)
            ? allLines[result.LineNumber - 1]
            : result.MatchLine;
        AddMatchEntries(_matchParagraphs, sectionNav, section, existingPara, lineText, rx, minimumOne: true);
        int sourceColumn = result.SourceMatchStartColumn >= 0
            ? result.SourceMatchStartColumn
            : result.MatchStartColumn;
        if (TryResolveMatchInParaBySourceColumn(existingPara, sourceColumn, out int matchInPara, out _, out _))
            ApplyMatchColorToParagraphMatch(existingPara, matchInPara);

        InvalidateParagraphIndexCache(section);
        if (sectionNav is not null)
            sectionNav.IndexByMatch = null;
        return true;
    }

    private bool IsMatchParagraphRegistered(RichTextBlock section, Paragraph paragraph)
    {
        if (_sectionMatchNavs.TryGetValue(section, out var sectionNav)
            && sectionNav.Matches.Any(match => ReferenceEquals(match.para, paragraph)))
        {
            return true;
        }

        return _matchParagraphs.Any(match => ReferenceEquals(match.block, section)
            && ReferenceEquals(match.para, paragraph));
    }

    /// <summary>
    /// Returns the highest 1-based line number rendered in a section,
    /// or 0 if no line-number paragraphs are found.
    /// </summary>
    private static int GetSectionLastRenderedLine(RichTextBlock section)
    {
        int lastLine = 0;
        for (int i = section.Blocks.Count - 1; i >= 0; i--)
        {
            if (section.Blocks[i] is Paragraph para
                && s_paragraphLineNumbers.TryGetValue(para, out var lineObj)
                && lineObj is int lineNumber
                && lineNumber > 0)
            {
                lastLine = lineNumber;
                break;
            }
        }
        return lastLine;
    }

    private static int GetSectionFirstRenderedLine(RichTextBlock section)
    {
        for (int i = 0; i < section.Blocks.Count; i++)
        {
            if (section.Blocks[i] is Paragraph para
                && s_paragraphLineNumbers.TryGetValue(para, out var lineObj)
                && lineObj is int lineNumber
                && lineNumber > 0)
            {
                return lineNumber;
            }
        }
        return 0;
    }

    private static SortedSet<int> GetRenderedLineNumbers(RichTextBlock section)
    {
        var set = new SortedSet<int>();
        for (int i = 0; i < section.Blocks.Count; i++)
        {
            if (section.Blocks[i] is Paragraph para
                && s_paragraphLineNumbers.TryGetValue(para, out var lineObj)
                && lineObj is int lineNumber
                && lineNumber > 0)
            {
                set.Add(lineNumber);
            }
        }
        return set;
    }

    /// <summary>
    /// Removes the last block from a section (and its corresponding gutter block)
    /// to undo an optimistically-added gap indicator when no content follows.
    /// </summary>
    private void RemoveLastBlock(RichTextBlock section)
    {
        if (section.Blocks.Count > 0)
            section.Blocks.RemoveAt(section.Blocks.Count - 1);
        if (_sectionGutterBlocks.TryGetValue(section, out var gutterBlock) && gutterBlock.Blocks.Count > 0)
            gutterBlock.Blocks.RemoveAt(gutterBlock.Blocks.Count - 1);
    }

    private bool TryFindPreviewMatchParagraph(
        RichTextBlock section,
        SearchResult result,
        out Paragraph paragraph,
        out int matchInPara)
    {
        (Paragraph paragraph, int matchInPara)? exact = null;
        var fallbackCandidates = new List<(Paragraph paragraph, int matchInPara)>();

        void ConsiderCandidate(Paragraph candidateParagraph, int candidateMatchInPara)
        {
            if (!TryGetPreviewParagraphLineNumber(candidateParagraph, out int lineNumber)
                || lineNumber != result.LineNumber)
            {
                return;
            }

            if (IsPreviewParagraphForResult(candidateParagraph, result))
            {
                exact ??= (candidateParagraph, candidateMatchInPara);
                return;
            }

            // Collect each distinct same-line paragraph once; the correct one is
            // chosen later by window containment.
            foreach (var existing in fallbackCandidates)
            {
                if (ReferenceEquals(existing.paragraph, candidateParagraph))
                    return;
            }

            fallbackCandidates.Add((candidateParagraph, candidateMatchInPara));
        }

        paragraph = null!;
        matchInPara = 0;

        if (_sectionMatchNavs.TryGetValue(section, out var sectionNav))
        {
            foreach (var match in sectionNav.Matches)
            {
                ConsiderCandidate(match.para, match.matchInPara);
                if (exact is not null)
                    break;
            }
        }

        if (exact is null)
        {
            foreach (var match in _matchParagraphs)
            {
                if (!ReferenceEquals(match.block, section))
                    continue;

                ConsiderCandidate(match.para, match.matchInPara);
                if (exact is not null)
                    break;
            }
        }

        if (exact is { } found)
        {
            paragraph = found.paragraph;
            matchInPara = found.matchInPara;

            // The nav entry's stored ordinal can be a fallback (run 0): a freshly-appended
            // single-target window registers run 0 when TryGetTargetMatchOrdinalInSegment
            // can't place the target, and IsSameResultTarget collapses distinct source
            // occurrences that share the same truncated display column. Re-resolve by source
            // column so the box lands on the run matching THIS occurrence, mirroring the
            // containing/fullLine path below.
            int exactWindowStart = 0;
            string exactRunCols = string.Empty;
            if (result.SourceMatchStartColumn >= 0
                && TryResolveMatchInParaBySourceColumn(found.paragraph, result.SourceMatchStartColumn, out int resolvedExact, out exactWindowStart, out exactRunCols))
            {
                matchInPara = resolvedExact;
            }

            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("Preview", $"TryFindPreviewMatchParagraph: path=exact, line={result.LineNumber}, srcCol={result.SourceMatchStartColumn}, matchCol={result.MatchStartColumn}, matchLen={result.MatchLength}, windowStart={exactWindowStart}, runSrcCols=[{exactRunCols}], resolvedMatchInPara={matchInPara}, fallbacks={fallbackCandidates.Count}");
            return true;
        }

        if (fallbackCandidates.Count > 0)
        {
            // A single selected occurrence on the line can always reuse the existing
            // paragraph; there is no sibling occurrence to disambiguate from.
            if (!HasMultipleSelectedResultsOnLine(result))
            {
                paragraph = fallbackCandidates[0].paragraph;
                matchInPara = fallbackCandidates[0].matchInPara;
                if (LogService.Instance.IsVerboseEnabled)
                    LogService.Instance.Verbose("Preview", $"TryFindPreviewMatchParagraph: path=single, line={result.LineNumber}, srcCol={result.SourceMatchStartColumn}, matchCol={result.MatchStartColumn}, resolvedMatchInPara={matchInPara}, fallbacks={fallbackCandidates.Count}");
                return true;
            }

            // Multiple selected occurrences share this line. A long line renders a
            // bounded window around each occurrence, so reuse an existing paragraph
            // ONLY when this occurrence's source column actually falls inside that
            // window (or the paragraph rendered the full untruncated line). Far-apart
            // occurrences are not in any existing window and must get their own
            // appended window instead of collapsing onto the nearest rendered run.
            int sourceColumn = result.SourceMatchStartColumn >= 0
                ? result.SourceMatchStartColumn
                : result.MatchStartColumn;

            (Paragraph paragraph, int matchInPara)? containing = null;
            (Paragraph paragraph, int matchInPara)? fullLine = null;
            foreach (var candidate in fallbackCandidates)
            {
                if (TryGetParagraphMatchWindow(candidate.paragraph, out int windowStart, out int windowEnd))
                {
                    if (sourceColumn >= windowStart && sourceColumn < windowEnd)
                    {
                        containing = candidate;
                        break;
                    }
                }
                else
                {
                    // No recorded window => full untruncated line => contains every
                    // occurrence on the line.
                    fullLine ??= candidate;
                }
            }

            if ((containing ?? fullLine) is not { } chosen)
            {
                // This occurrence is not inside any rendered same-line window; signal
                // the caller to append a dedicated window for it.
                if (LogService.Instance.IsVerboseEnabled)
                    LogService.Instance.Verbose("Preview", $"TryFindPreviewMatchParagraph: path=append(no-window), line={result.LineNumber}, srcCol={result.SourceMatchStartColumn}, matchCol={result.MatchStartColumn}, fallbacks={fallbackCandidates.Count}");
                return false;
            }

            // Determine the correct matchInPara by matching the result's column
            // against the match runs in the chosen paragraph.
            if (TryResolveMatchInParaBySourceColumn(chosen.paragraph, sourceColumn, out int resolvedIdx, out int chosenWindowStart, out string runCols))
            {
                paragraph = chosen.paragraph;
                matchInPara = resolvedIdx;
                if (LogService.Instance.IsVerboseEnabled)
                    LogService.Instance.Verbose("Preview", $"TryFindPreviewMatchParagraph: path={(containing is not null ? "containing" : "fullLine")}, line={result.LineNumber}, srcCol={result.SourceMatchStartColumn}, matchCol={result.MatchStartColumn}, sourceColumn={sourceColumn}, windowStart={chosenWindowStart}, runSrcCols=[{runCols}], resolvedMatchInPara={matchInPara}, fallbacks={fallbackCandidates.Count}");
                return true;
            }

            // Fallback: can't determine column, use first match in paragraph.
            paragraph = chosen.paragraph;
            matchInPara = chosen.matchInPara;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves which bold match run inside <paramref name="paragraph"/> corresponds to the
    /// occurrence at <paramref name="sourceColumn"/> (a file-line absolute column). Dense
    /// same-line windows render several occurrences as separate bold runs in one paragraph,
    /// so the run nearest the target source column is the correct one. Each run's
    /// paragraph-relative display column is converted to source space via the paragraph's
    /// recorded window start (column 0 for full untruncated lines). Returns false when the
    /// paragraph has no match runs or the column is unknown, leaving the caller's ordinal
    /// untouched.
    /// </summary>
    private bool TryResolveMatchInParaBySourceColumn(
        Paragraph paragraph,
        int sourceColumn,
        out int matchInPara,
        out int windowStart,
        out string runSourceColumnsLog)
    {
        matchInPara = 0;
        windowStart = TryGetParagraphMatchWindow(paragraph, out int ws, out _) ? ws : 0;
        runSourceColumnsLog = string.Empty;

        var runs = GetMatchRunsForParagraph(paragraph);
        if (runs.Count == 0 || sourceColumn < 0)
            return false;

        int bestIndex = 0;
        int bestDist = int.MaxValue;
        for (int i = 0; i < runs.Count; i++)
        {
            int runSourceColumn = windowStart + runs[i].column;
            int dist = Math.Abs(runSourceColumn - sourceColumn);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        matchInPara = bestIndex;
        if (LogService.Instance.IsVerboseEnabled)
        {
            int logWindowStart = windowStart;
            runSourceColumnsLog = string.Join(",", runs.Select(r => logWindowStart + r.column));
        }
        return true;
    }

    private bool HasMultipleSelectedResultsOnLine(SearchResult result)
    {
        int count = 0;
        foreach (var selected in ViewModel.GetAllSelectedResults())
        {
            if (selected.LineNumber == result.LineNumber
                && string.Equals(selected.FilePath, result.FilePath, StringComparison.OrdinalIgnoreCase)
                && ++count > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPreviewParagraphForResult(Paragraph paragraph, SearchResult result)
    {
        return s_paragraphPrimaryResults.TryGetValue(paragraph, out var stored)
            && stored is SearchResult paragraphResult
            && (ReferenceEquals(paragraphResult, result) || IsSameResultTarget(paragraphResult, result));
    }

    private static bool IsSameResultTarget(SearchResult left, SearchResult right)
    {
        return left.LineNumber == right.LineNumber
            && left.MatchStartColumn == right.MatchStartColumn
            && left.MatchLength == right.MatchLength
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.MatchLine, right.MatchLine, StringComparison.Ordinal);
    }

    private static bool TryGetPreviewParagraphLineNumber(Paragraph paragraph, out int lineNumber)
    {
        lineNumber = 0;

        // Prefer the ConditionalWeakTable (works for sections with separate gutter blocks).
        if (s_paragraphLineNumbers.TryGetValue(paragraph, out var lineObj) && lineObj is int stored && stored > 0)
        {
            lineNumber = stored;
            return true;
        }

        // Fallback: parse from inline gutter runs (legacy single-block path).
        var gutter = paragraph.Inlines.OfType<Run>().Skip(1).FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(gutter))
            return false;

        var text = gutter.AsSpan().Trim();
        int spaceIndex = text.IndexOf(' ');
        if (spaceIndex >= 0)
            text = text[..spaceIndex];

        return int.TryParse(text, out lineNumber);
    }
}
