using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Preview content construction: section building, paragraph rendering,
/// highlight matching, lazy section materialization, and document loading.
/// </summary>
public sealed partial class MainWindow
{
    private static IEnumerable<KeyValuePair<string, List<SearchResult>>> OrderByFileFirst(
        Dictionary<string, List<SearchResult>> byFile, string? firstFilePath)
    {
        if (firstFilePath is null || !byFile.TryGetValue(firstFilePath, out var firstFileResults))
            return byFile;

        // Yield the target file first, then the rest in their original order.
        return Enumerate();
        IEnumerable<KeyValuePair<string, List<SearchResult>>> Enumerate()
        {
            yield return new KeyValuePair<string, List<SearchResult>>(firstFilePath, firstFileResults);
            foreach (var kvp in byFile)
            {
                if (!string.Equals(kvp.Key, firstFilePath, StringComparison.OrdinalIgnoreCase))
                    yield return kvp;
            }
        }
    }

    /// <summary>
    /// Read all lines using the same encoding detection as the search engine
    /// so that line numbers in the preview match the search results.
    /// </summary>

    /// <summary>Number of file sections to build before yielding to the UI message pump.</summary>
    private const int PreviewYieldBatchSize = 32;

    /// <summary>Max file sections to render in one page. Remaining are loaded on demand via "Show more".</summary>
    private const int DefaultPreviewSectionPageSize = 50;
    private int EffectivePreviewSectionPageSize => ViewModel.PreviewSectionPageSize > 0 ? ViewModel.PreviewSectionPageSize : DefaultPreviewSectionPageSize;

    /// <summary>XAML paragraph chunk size for very long physical lines; all text is still rendered.</summary>
    private const int PreviewLineLayoutSegmentChars = 4096;

    /// <summary>
    /// Safety cap for NoWrap mode. Empirically, DirectWrite layout via RichTextBlock
    /// with TextWrapping=NoWrap throws stowed COMException 0x80004005 (E_FAIL) from
    /// Microsoft.UI.Xaml.dll well below the documented ~65535 character limit when
    /// asked to lay out a single very long run on one visual line. 4096 matches the
    /// Wrap-mode segment size and is empirically safe; long source lines will visually
    /// fold into multiple paragraphs in NoWrap mode (compromising strict NoWrap
    /// semantics) but every character is still rendered and no crash occurs.
    /// </summary>
    private const int PreviewLineLayoutSegmentCharsNoWrap = 4096;

    /// <summary>
    /// Upper bound on how many characters a single "Show all" click reveals. A truly
    /// unbounded reveal of an extremely long physical line (e.g. a minified JSON blob of
    /// hundreds of KB on one line) builds one RichTextBlock paragraph with tens of thousands
    /// of wrapped rows and match runs, which hangs the WinUI UI thread. Lines longer than this
    /// reveal a bounded window centered on the current view instead, keeping the prefix/suffix
    /// "Show more" ellipses so the user can keep expanding or open the file in an editor.
    /// </summary>
    private const int PreviewShowAllMaxRevealLength = 20_000;

    private enum PreviewShowMoreEdge
    {
        Prefix,
        Suffix,
    }

    private enum PreviewShowMoreExpandMode
    {
        More,
        All,
    }

    private sealed class PreviewTruncatedLineState
    {
        public PreviewTruncatedLineState(
            string sourceLine,
            int sourceStart,
            int sourceEnd,
            int lineNumber,
            bool isMatchLine,
            SearchResult result,
            Regex? regex)
        {
            SourceLine = sourceLine;
            SourceStart = sourceStart;
            SourceEnd = sourceEnd;
            LineNumber = lineNumber;
            IsMatchLine = isMatchLine;
            Result = result;
            Regex = regex;
        }

        public string SourceLine { get; }
        public int SourceStart { get; set; }
        public int SourceEnd { get; set; }
        public int LineNumber { get; }
        public bool IsMatchLine { get; }
        public SearchResult Result { get; }
        public Regex? Regex { get; }
        public int ContentInlineStart { get; set; }

        // When true, only the originally-selected occurrence (the result's source
        // match) is colored as a match; all other regex hits revealed by a
        // "Show more"/"Show all" expansion stay context-colored. Mirrors the
        // targetOnlyMatchEntry coloring used when the windowed line was first built.
        public bool ColorTargetMatchOnly { get; set; }
    }

    private sealed record PreviewShowMoreAction(
        Paragraph Paragraph,
        PreviewTruncatedLineState State,
        PreviewShowMoreEdge Edge);

    private sealed record PreviewShowMoreScrollSnapshot(
        double PreviewHorizontalOffset,
        double PreviewVerticalOffset,
        ScrollViewer? SectionScroller,
        double SectionHorizontalOffset,
        double SectionVerticalOffset);

    private bool ShouldTruncatePreviewLines()
        => ViewModel.PreviewWrapModeIndex != (int)Models.PreviewWrapMode.NoWrap;

    private bool ShouldTruncateInitialPreviewLines()
        => ShouldTruncatePreviewLines()
           || ViewModel.PreviewWrapModeIndex == (int)Models.PreviewWrapMode.NoWrap;

    private bool ShouldTruncateOverflowPreviewLines()
        => ShouldTruncateInitialPreviewLines();

    private int GetEffectiveSegmentSize()
        => ViewModel.PreviewWrapModeIndex == (int)Models.PreviewWrapMode.NoWrap
            ? PreviewLineLayoutSegmentCharsNoWrap
            : PreviewLineLayoutSegmentChars;

    private int GetPreviewShowMoreMaxWindowLength()
        => Math.Max(50, GetEffectiveSegmentSize() - (2 * LineTruncator.Ellipsis.Length));

    private int GetPreviewShowAllMaxRevealLength()
        => Math.Max(GetEffectiveSegmentSize(), PreviewShowAllMaxRevealLength);

    private int GetPreviewTruncatedLength()
    {
        int configuredLength = LineTruncator.TruncatedLength;
        if (configuredLength == 0)
            return 0;

        return Math.Min(configuredLength, GetPreviewShowMoreMaxWindowLength());
    }

    /// <summary>
    /// Maximum matches to render per file section before truncating.
    /// Prevents multi-second UI freezes when a single file has hundreds
    /// of thousands of matches (e.g. 600K).
    /// </summary>
    private const int DefaultMaxMatchesPerSection = 500;
    private int EffectiveMaxMatchesPerSection => ViewModel.MaxMatchesPerSection > 0 ? ViewModel.MaxMatchesPerSection : DefaultMaxMatchesPerSection;

    /// <summary>
    /// While search workers are still producing results, keep the first live preview
    /// section small enough that WinUI can lay it out without making the window hang.
    /// The section overflow loader can page the remaining matches in after the first
    /// frame is responsive.
    /// </summary>
    private const int ActiveSearchMaxInitialMatchesPerSection = 100;
    private const int ActiveSearchMaxInitialPreviewBlocksPerSection = 100;

    private int EffectiveInitialMaxMatchesPerSection => ViewModel.IsSearching
        ? Math.Min(EffectiveMaxMatchesPerSection, ActiveSearchMaxInitialMatchesPerSection)
        : EffectiveMaxMatchesPerSection;

    /// <summary>
    /// Maximum RichTextBlock blocks to build for an expanded preview section
    /// before registering overflow. WinUI lays these blocks out only after the
    /// section enters the visual tree, so a match cap alone is not enough for
    /// dense hits on a few very long lines.
    /// </summary>
    private const int MaxPreviewBlocksPerSection = 450;

    private int EffectiveInitialMaxPreviewBlocksPerSection => ViewModel.IsSearching
        ? Math.Min(MaxPreviewBlocksPerSection, ActiveSearchMaxInitialPreviewBlocksPerSection)
        : MaxPreviewBlocksPerSection;

    /// <summary>
    /// Number of additional results to materialize per "Next match" click
    /// once a section's overflow has been registered. Smaller than the
    /// initial cap because expanded chunks are appended on the UI thread,
    /// and each result can produce many match runs (long lines split into
    /// multiple paragraphs, multi-occurrence regex matches, etc.).
    /// </summary>
    private const int MaxMatchesPerExpandChunk = 500;

    private const int MaxPreviewBlocksPerExpandChunk = 300;

    /// <summary>
    /// Hard cap on match entries (paragraphs added to <c>_matchParagraphs</c>)
    /// produced by a single <see cref="ExpandSectionNextChunk"/> call. Dense
    /// lines (many regex matches per line) can multiply the result count by
    /// 20× or more, which would freeze the UI thread for seconds.
    /// </summary>
    private const int MaxMatchEntriesPerExpandChunk = 2_000;

    // ── Gutter-height synchronization (word-wrap support) ─────────────────
    //
    // The gutter and content are separate RichTextBlocks in a two-column Grid.
    // Without word wrap both have 1 visual line (20 px) per paragraph, so they
    // stay aligned.  With word wrap the content paragraph can occupy N visual
    // lines while the gutter paragraph stays at 1, causing progressive drift.
    //
    // After layout we measure each content paragraph's rendered height via
    // TextPointer.GetCharacterRect and add explicit gutter continuation rows so
    // wrapped visual lines keep the same separator pipe as normal lines.

    private readonly HashSet<RichTextBlock> _gutterSyncPending = new();

    private void ScheduleGutterSync(RichTextBlock contentBlock)
    {
        if (!_gutterSyncPending.Add(contentBlock)) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gutterSyncPending.Remove(contentBlock);
            SyncGutterParagraphHeights(contentBlock);
        });
    }

    private void SyncGutterParagraphHeights(RichTextBlock contentBlock)
    {
        if (!_sectionGutterBlocks.TryGetValue(contentBlock, out var gutterBlock)) return;
        if (contentBlock.ActualWidth <= 0) return;

        double lineHeight = contentBlock.LineHeight;
        var contentBlocks = contentBlock.Blocks;
        var gutterBlocks = gutterBlock.Blocks;
        if (contentBlocks.Count == 0 || contentBlocks.Count != gutterBlocks.Count)
            return;

        bool isWrapped = contentBlock.TextWrapping == TextWrapping.Wrap;

        for (int i = 0; i < contentBlocks.Count; i++)
        {
            if (contentBlocks[i] is not Paragraph cp || gutterBlocks[i] is not Paragraph gp)
                continue;

            double targetBottom;
            if (isWrapped)
            {
                try
                {
                    var startRect = cp.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    var endRect = cp.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    double contentHeight = Math.Max(lineHeight, endRect.Bottom - startRect.Top);
                    int visualLineCount = Math.Max(1, (int)Math.Ceiling(Math.Max(0, contentHeight - 0.5) / lineHeight));
                    SetGutterWrappedContinuationRows(gp, visualLineCount);
                    targetBottom = cp.Margin.Bottom + Math.Max(0, contentHeight - visualLineCount * lineHeight);
                }
                catch
                {
                    continue; // TextPointer measurement can fail before layout
                }
            }
            else
            {
                // Word wrap off: remove any rows added while wrap was enabled.
                SetGutterWrappedContinuationRows(gp, visualLineCount: 1);
                targetBottom = cp.Margin.Bottom;
            }

            if (Math.Abs(gp.Margin.Bottom - targetBottom) > 0.5)
                gp.Margin = new Thickness(gp.Margin.Left, gp.Margin.Top, gp.Margin.Right, targetBottom);
        }
    }

    private static void SetGutterWrappedContinuationRows(Paragraph gutterParagraph, int visualLineCount)
    {
        int targetContinuationRows = Math.Max(0, visualLineCount - 1);

        // Reconcile against the continuation rows actually present rather than a side table.
        // Each continuation row is a [LineBreak, Run] pair appended after the leading gutter
        // runs, so the LineBreak count is the ground-truth current row count. Counting the
        // real inlines keeps this idempotent even when a gutter paragraph is moved between
        // blocks or re-synced multiple times. A side table keyed on object identity could
        // silently lose its entry, causing a second full set of rows to be appended and
        // leaving the gutter paragraph ~2x taller than its content — which made the gutter
        // line numbers drift upward and appear beside the wrong content line.
        int currentContinuationRows = 0;
        foreach (var inline in gutterParagraph.Inlines)
        {
            if (inline is LineBreak)
                currentContinuationRows++;
        }

        while (currentContinuationRows > targetContinuationRows && gutterParagraph.Inlines.Count >= 2)
        {
            // Remove the trailing [LineBreak, Run] continuation pair.
            gutterParagraph.Inlines.RemoveAt(gutterParagraph.Inlines.Count - 1);
            gutterParagraph.Inlines.RemoveAt(gutterParagraph.Inlines.Count - 1);
            currentContinuationRows--;
        }

        for (int i = currentContinuationRows; i < targetContinuationRows; i++)
        {
            gutterParagraph.Inlines.Add(new LineBreak());
            // Blank gutter spacer for the wrapped row; the separator line itself is the
            // gutter column border (see AddPreviewSection), so no pipe glyph is needed.
            gutterParagraph.Inlines.Add(new Run { Text = "       ", Foreground = s_gutterSepBrush });
        }
    }

    /// <summary>Adds a spacer paragraph to the gutter block to keep it aligned with non-line paragraphs in the content.</summary>
    private void SyncGutterSpacer(RichTextBlock section, Thickness margin)
    {
        if (_sectionGutterBlocks.TryGetValue(section, out var gb))
        {
            var spacer = new Paragraph { Margin = margin };
            spacer.Inlines.Add(new Run { Text = "       ", Foreground = s_gutterSepBrush });
            gb.Blocks.Add(spacer);
        }
    }

    /// <summary>
    /// Adds a gap indicator between non-contiguous source-line ranges.
    /// When a gutter block exists, the indicator goes in the gutter and a
    /// blank spacer is added to the content block to keep them aligned.
    /// </summary>
    private void AddGapIndicator(RichTextBlock section)
    {
        if (_sectionGutterBlocks.TryGetValue(section, out var gb))
        {
            var gutterGap = new Paragraph();
            var gutterGapRun = new Run { Text = "  gap " };
            gutterGapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
            gutterGap.Inlines.Add(gutterGapRun);
            gb.Blocks.Add(gutterGap);

            var contentSpacer = new Paragraph();
            contentSpacer.Inlines.Add(new Run { Text = "gap", Foreground = _transparentBrush });
            section.Blocks.Add(contentSpacer);
        }
        else
        {
            var gap = new Paragraph();
            var gapRun = new Run { Text = "  gap" };
            gapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
            gap.Inlines.Add(gapRun);
            section.Blocks.Add(gap);
        }
    }

    private static bool ShouldAddGapBetweenRenderedLines(int previousLineNumber, int nextLineNumber)
    {
        return previousLineNumber > 0
            && nextLineNumber > 0
            && nextLineNumber - previousLineNumber > 1;
    }

    private int GetGutterBlockCount(RichTextBlock section)
    {
        return _sectionGutterBlocks.TryGetValue(section, out var gutterBlock)
            ? gutterBlock.Blocks.Count
            : -1;
    }

    private void MoveAppendedPreviewLineBesideExistingLine(
        RichTextBlock section,
        int lineNumber,
        int contentStartIndex,
        int gutterStartIndex,
        int appendedBlockCount)
    {
        if (appendedBlockCount <= 0 || contentStartIndex <= 0)
            return;

        int targetContentIndex = FindInsertIndexAfterRenderedLine(section, lineNumber, contentStartIndex);
        if (targetContentIndex < 0 || targetContentIndex >= contentStartIndex)
            return;

        MoveBlockRange(section.Blocks, contentStartIndex, appendedBlockCount, targetContentIndex);

        if (_sectionGutterBlocks.TryGetValue(section, out var gutterBlock)
            && gutterStartIndex >= 0
            && gutterStartIndex + appendedBlockCount <= gutterBlock.Blocks.Count)
        {
            int targetGutterIndex = targetContentIndex + (gutterStartIndex - contentStartIndex);
            MoveBlockRange(gutterBlock.Blocks, gutterStartIndex, appendedBlockCount, targetGutterIndex);
        }
    }

    private static int FindInsertIndexAfterRenderedLine(RichTextBlock section, int lineNumber, int beforeIndex)
    {
        int limit = Math.Min(beforeIndex, section.Blocks.Count);
        for (int i = limit - 1; i >= 0; i--)
        {
            if (section.Blocks[i] is Paragraph para
                && s_paragraphLineNumbers.TryGetValue(para, out var rawLineNumber)
                && rawLineNumber is int candidateLineNumber
                && candidateLineNumber == lineNumber)
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static void MoveBlockRange(BlockCollection blocks, int startIndex, int count, int targetIndex)
    {
        if (count <= 0
            || startIndex < 0
            || startIndex + count > blocks.Count
            || targetIndex < 0
            || targetIndex >= startIndex)
        {
            return;
        }

        var movedBlocks = new List<Block>(count);
        for (int i = 0; i < count; i++)
            movedBlocks.Add(blocks[startIndex + i]);

        for (int i = 0; i < count; i++)
            blocks.RemoveAt(startIndex);

        for (int i = 0; i < movedBlocks.Count; i++)
            blocks.Insert(targetIndex + i, movedBlocks[i]);
    }

    /// <summary>Appends a notice paragraph when a section's matches were truncated.</summary>
    private Paragraph AppendTruncationNotice(RichTextBlock section, int totalMatches, int renderedMatches)
    {
        var notice = new Paragraph { Margin = new Thickness(0, 12, 0, 4) };
        var run = new Run
        {
            Text = $"\u26A0 Showing first {renderedMatches:N0} of {totalMatches:N0} matches. " +
                   "Click \u2193 (Next match) to load more, or open in editor to browse all.",
        };
        run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 160, 60));
        notice.Inlines.Add(run);
        section.Blocks.Add(notice);
        SyncGutterSpacer(section, notice.Margin);
        return notice;
    }

    /// <summary>
    /// Registers a section as having un-rendered overflow matches so that
    /// "Next match" navigation can progressively load more chunks.
    /// </summary>
    private void RegisterSectionOverflow(
        RichTextBlock section, string? filePath, List<SearchResult> remainingResults,
        string[]? allLines, int previewLines, Regex? rx, int originalTotal, int renderedSoFar,
        Paragraph noticePara, bool isHighlightMode = false, int lastRenderedLine = 0)
    {
        _sectionOverflow[section] = new SectionOverflow
        {
            FilePath = filePath,
            RemainingResults = remainingResults,
            AllLines = allLines,
            PreviewLines = previewLines,
            Rx = rx,
            OriginalTotal = originalTotal,
            RenderedSoFar = renderedSoFar,
            NoticePara = noticePara,
            IsHighlightMode = isHighlightMode,
            LastRenderedLine = lastRenderedLine,
        };
    }

    /// <summary>
    /// Batch-read all file contents off the UI thread so the per-file loop only does
    /// XAML element construction (which must be on the UI thread) without interleaving I/O waits.
    /// </summary>
    private async Task<Dictionary<string, string[]?>> ReadAllFileContentsAsync(
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles)
    {
        return await Task.Run(() =>
        {
            var result = new Dictionary<string, string[]?>(orderedFiles.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (filePath, _) in orderedFiles)
            {
                if (result.ContainsKey(filePath)) continue;
                try
                {
                    result[filePath] = ReadAllLinesWithEncodingSync(filePath);
                }
                catch
                {
                    result[filePath] = null;
                }
            }
            return result;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Automatically loads all remaining file sections using the efficient off-tree batched approach
    /// (same code path as "Show all files"). Called after the first page is rendered inline.
    /// </summary>
    private async Task AutoLoadRemainingSectionsAsync(
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int pageStart,
        List<SearchResult> allSelected,
        int gen)
    {
        LogService.Instance.Info("Preview", $"AutoLoadRemainingSectionsAsync: pageStart={pageStart}, totalFiles={orderedFiles.Count}, remaining={orderedFiles.Count - pageStart}, gen={gen}");
        // Create a placeholder panel that LoadMoreSectionsAsync will remove.
        var placeholder = new StackPanel();
        PreviewSectionsPanel.Children.Add(placeholder);
        InvalidateScrollPositionCache();
        await LoadMoreSectionsAsync(placeholder, orderedFiles, pageStart, orderedFiles.Count, allSelected, gen);
    }

    private void AddShowMoreSectionsButton(
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int nextIndex,
        List<SearchResult> allSelected,
        int gen)
    {
        int remaining = orderedFiles.Count - nextIndex;

        var moreBtn = new Button { Content = $"Show {remaining:N0} more file(s)\u2026" };
        var allBtn = new Button { Content = "Show all files" };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 8),
        };
        panel.Children.Add(moreBtn);
        panel.Children.Add(allBtn);

        moreBtn.Click += async (_, _) => await LoadMoreSectionsAsync(panel, orderedFiles, nextIndex, nextIndex + EffectivePreviewSectionPageSize, allSelected, gen);
        allBtn.Click += async (_, _) => await LoadMoreSectionsAsync(panel, orderedFiles, nextIndex, orderedFiles.Count, allSelected, gen);

        PreviewSectionsPanel.Children.Add(panel);
        InvalidateScrollPositionCache();

        // Track for scroll-driven auto-load and for accurate match-nav totals.
        _deferredOrderedFiles = orderedFiles;
        _deferredCursor = nextIndex;
        _deferredAllSelected = allSelected;
        _deferredGen = gen;
        _deferredButtonPanel = panel;
    }

    /// <summary>Max sections kept expanded during bulk "Show all" loads to reduce layout cost.</summary>
    private const int BulkExpandLimit = 3;

    private async Task LoadMoreSectionsAsync(
        StackPanel buttonPanel,
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int pageStart, int requestedEnd,
        List<SearchResult> allSelected,
        int gen)
    {
        LogService.Instance.Info("Preview", $"LoadMoreSectionsAsync: pageStart={pageStart}, requestedEnd={requestedEnd}, totalFiles={orderedFiles.Count}, gen={gen}");
        if (_previewUpdateGen != gen) return;

        // Remove the button panel.
        PreviewSectionsPanel.Children.Remove(buttonPanel);
            InvalidateScrollPositionCache();
        if (ReferenceEquals(_deferredButtonPanel, buttonPanel))
            _deferredButtonPanel = null;

        // When loading all remaining files, process in page-sized chunks
        // with a longer yield between pages so the layout engine stays responsive.
        bool loadingAll = requestedEnd - pageStart > EffectivePreviewSectionPageSize;
        int cursor = pageStart;
        int finalEnd = Math.Min(orderedFiles.Count, requestedEnd);
        int totalToLoad = finalEnd - pageStart;
        int totalSectionsAdded = 0;

        // Show progress overlay for "Show all" operations.
        if (loadingAll)
            ShowProgressOverlay($"Loading {totalToLoad:N0} files\u2026", 0);

        while (cursor < finalEnd)
        {
            int chunkEnd = Math.Min(finalEnd, cursor + EffectivePreviewSectionPageSize);
            var pageFiles = orderedFiles.GetRange(cursor, chunkEnd - cursor);

            // Only pre-read files we will eagerly expand in this chunk.
            // For bulk "Show all" loads, that's at most BulkExpandLimit-many
            // sections across the entire load — the rest are lazy.
            int chunkEagerCount;
            if (!loadingAll) chunkEagerCount = pageFiles.Count;
            else chunkEagerCount = Math.Max(0, Math.Min(pageFiles.Count, BulkExpandLimit - totalSectionsAdded));
            var fileContents = chunkEagerCount > 0
                ? await ReadAllFileContentsAsync(
                    chunkEagerCount == pageFiles.Count ? pageFiles : pageFiles.GetRange(0, chunkEagerCount))
                : new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
            if (_previewUpdateGen != gen) { HideProgressOverlay(); return; }

            Regex? rx = BuildSearchHighlightRegex();
            bool isHighlight = ViewModel.PreviewModeIndex == 1;
            int previewLines = ViewModel.PreviewContextLines;

            // Build sections off-tree to avoid layout cycles, then add in small batches.
            var pendingExpanders = new List<Expander>();
            foreach (var (filePath, results) in pageFiles)
            {
                // Collapse sections beyond the first BulkExpandLimit during bulk loads.
                bool expanded = !loadingAll || totalSectionsAdded < BulkExpandLimit;
                var (section, expander) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results,
                    isExpanded: expanded, addToPanel: false);

                string[]? allLines = null;
                if (expanded)
                    fileContents.TryGetValue(filePath, out allLines);

                if (expanded)
                {
                    // Eagerly build content for expanded sections.
                    if (isHighlight)
                        await BuildHighlightSectionAsync(section, results, allLines, previewLines, rx);
                    else
                        BuildConcatenatedSection(section, results, allLines, previewLines, rx);

                }
                else
                {
                    // Defer content building for collapsed sections (lazy rendering).
                    // Skip pre-reading the file; MaterializeLazySection reads it on demand.
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

                pendingExpanders.Add(expander);
                totalSectionsAdded++;
            }

            // Add built sections to the visual tree in small batches with yields.
            if (pendingExpanders.Count > 0) _lastHighlightedActiveBlock = null;
            for (int i = 0; i < pendingExpanders.Count; i++)
            {
                PreviewSectionsPanel.Children.Add(pendingExpanders[i]);
                InvalidateScrollPositionCache();

                if ((i + 1) % PreviewYieldBatchSize == 0)
                {
                    int filesLoaded = (cursor - pageStart) + i + 1;
                    if (loadingAll)
                        UpdateProgressOverlay((int)((double)filesLoaded / totalToLoad * 100));

                    await YieldLowAsync();
                    if (_previewUpdateGen != gen) { HideProgressOverlay(); return; }
                }
            }

            cursor = chunkEnd;

            // Update progress after completing each chunk.
            if (loadingAll)
                UpdateProgressOverlay((int)((double)(cursor - pageStart) / totalToLoad * 100));

            // Longer yield between pages so layout can fully process the batch.
            if (loadingAll && cursor < finalEnd)
            {
                await Task.Delay(50).ConfigureAwait(true);
                if (_previewUpdateGen != gen) { HideProgressOverlay(); return; }
            }
        }

        HideProgressOverlay();

        // Add another "Show more" button if still more remain.
        if (finalEnd < orderedFiles.Count)
            AddShowMoreSectionsButton(orderedFiles, finalEnd, allSelected, gen);
        else
        {
            // Exhausted — clear deferred state.
            _deferredOrderedFiles = null;
            _deferredAllSelected = null;
            _deferredButtonPanel = null;
            _deferredCursor = 0;
        }

        // Update match count and file count to reflect all loaded files.
        int loadedFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        var (deferredFileCount, deferredMatchCount) = GetDeferredCounts();
        int totalMatches = GetStableMatchNavTotal();
        int grandFileCount = _previewTotalFileCount > 0 ? _previewTotalFileCount : loadedFiles + deferredFileCount;
        SetPreviewFileLabel(
            $"{totalMatches:N0} selected matches across {grandFileCount:N0} file(s)",
            string.Join(Environment.NewLine, orderedFiles.Take(finalEnd).Select(kv => kv.Key)));
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        UpdateExpandAllButtonVisibility();

        // Materialize any lazy sections that already fall within the viewport.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MaterializeVisibleLazySections);
    }

    private void BuildConcatenatedSection(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        ResetPreviewShowMoreDiagnostics();
        var buildSw = Stopwatch.StartNew();
        bool truncatePreviewLines = ViewModel.IsSearching
            ? ShouldTruncateOverflowPreviewLines()
            : ShouldTruncateInitialPreviewLines();
        int parasBuilt = 0;
        int renderedResults = 0;
        int startingBlocks = section.Blocks.Count;
        int maxMatches = EffectiveInitialMaxMatchesPerSection;
        int maxBlocks = EffectiveInitialMaxPreviewBlocksPerSection;
        int cap = Math.Min(results.Count, maxMatches);
        var renderedLineNumbers = new HashSet<int>();
        bool renderedFileNameOnlyPreview = false;
        foreach (var r in results)
        {
            if (renderedResults >= cap || section.Blocks.Count - startingBlocks >= maxBlocks)
                break;

            bool isFileNameOnlyPreview = r.LineNumber <= 0;
            if (isFileNameOnlyPreview && renderedFileNameOnlyPreview)
            {
                renderedResults++;
                continue;
            }

            // Skip results whose line was already rendered (multiple matches on same line).
            if (!isFileNameOnlyPreview && !renderedLineNumbers.Add(r.LineNumber))
            {
                renderedResults++;
                continue;
            }

            if (!isFileNameOnlyPreview)
            {
                var sep = new Paragraph();
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var sepRun = new Run { Text = $"{new string('\u2500', 6)}{label}{new string('\u2500', 6)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                sep.Margin = new Thickness(0, 8, 0, 4);
                section.Blocks.Add(sep);

                // Keep gutter aligned with separator paragraph.
                if (_sectionGutterBlocks.TryGetValue(section, out var gb))
                {
                    var gutterSep = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                    gutterSep.Inlines.Add(new Run { Text = "       ", Foreground = s_gutterSepBrush });
                    gb.Blocks.Add(gutterSep);
                }
            }

            var lines = GetPreviewLines(r, allLines, previewLines, fullFile: isFileNameOnlyPreview);
            foreach (var (line, lineNum) in lines)
            {
                if (section.Blocks.Count - startingBlocks >= maxBlocks)
                    break;

                bool isMatchLine = isFileNameOnlyPreview || lineNum == r.LineNumber;
                _sectionMatchNavs.TryGetValue(section, out var sn);
                // Full-span (Phase 3) multiline: color each spanned line via its forced column range,
                // rendered untruncated so the columns map 1:1 (single-line results yield null → unchanged).
                var forcedSpan = isFileNameOnlyPreview ? null : ComputeMultilineLineSpan(r, lineNum, line?.Length ?? 0);
                AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, isFileNameOnlyPreview ? null : rx, truncate: !isFileNameOnlyPreview && truncatePreviewLines && forcedSpan is null,
                    isMatchLine && !isFileNameOnlyPreview ? _matchParagraphs : null, sn, out int addedParagraphs,
                    maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks),
                    forcedSpan: forcedSpan);
                parasBuilt += addedParagraphs;
            }

            renderedResults++;
            renderedFileNameOnlyPreview |= isFileNameOnlyPreview;
        }

        if (results.Count > renderedResults)
        {
            var notice = AppendTruncationNotice(section, results.Count, renderedResults);
            RegisterSectionOverflow(section,
                filePath: null,
                remainingResults: results.GetRange(renderedResults, results.Count - renderedResults),
                allLines: allLines,
                previewLines: previewLines,
                rx: rx,
                originalTotal: results.Count,
                renderedSoFar: renderedResults,
                noticePara: notice);
        }

        buildSw.Stop();
        LogService.Instance.Info("Preview", $"BuildConcatenatedSection: results={results.Count}, rendered={renderedResults}, paragraphs={parasBuilt}, blocks={section.Blocks.Count}, activeSearch={ViewModel.IsSearching}, caps=(matches={maxMatches}, blocks={maxBlocks}), elapsed={buildSw.ElapsedMilliseconds}ms");
        LogPreviewShowMoreDiagnostics("BuildConcatenatedSection");
    }

    // Yield to the UI dispatcher after this many paragraphs have been added during
    // a section build, so the window stays responsive while large highlight sections
    // (e.g. minified/blob files producing 1000+ paragraphs) are constructed.
    private const int BuildHighlightYieldEvery = 200;

    private async Task BuildHighlightSectionAsync(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        if (results.All(result => result.LineNumber <= 0))
        {
            BuildConcatenatedSection(section, results, allLines, previewLines, rx: null);
            return;
        }

        results = results.Where(result => result.LineNumber > 0).ToList();
        if (allLines is not null && !HasReadablePreviewLine(results, allLines))
        {
            LogService.Instance.Verbose("Preview",
                $"BuildHighlightSection: current file contents do not contain requested line(s) for '{results[0].FilePath}', using stored match context");
            allLines = null;
        }

        ResetPreviewShowMoreDiagnostics();
        var buildSw = Stopwatch.StartNew();
        bool truncatePreviewLines = ViewModel.IsSearching
            ? ShouldTruncateOverflowPreviewLines()
            : ShouldTruncateInitialPreviewLines();
        int parasBuilt = 0;
        int parasSinceYield = 0;
        int yieldCount = 0;
        int sectionMatchStart = _matchParagraphs.Count;
        int startingBlocks = section.Blocks.Count;
        int maxMatches = EffectiveInitialMaxMatchesPerSection;
        int maxBlocks = EffectiveInitialMaxPreviewBlocksPerSection;
        bool initiallyCapped = results.Count > maxMatches;
        var cappedResults = initiallyCapped ? results.GetRange(0, maxMatches) : results;
        int lastRenderedLine1 = 0;
        // When we render from stored per-result context (allLines == null) we merge the
        // overlapping windows into a single ordered set of lines, so this tracks how many
        // results that merged render actually covered (used for the truncation notice below).
        int storedContextCoveredResults = -1;

        if (allLines != null)
        {
            var distinctMatchLines = results
                .Select(result => result.LineNumber)
                .Where(lineNumber => lineNumber >= 1 && lineNumber <= allLines.Length)
                .Distinct()
                .Take(2)
                .ToArray();
            if (distinctMatchLines.Length == 1)
            {
                int matchLineNumber = distinctMatchLines[0];
                var matchResult = results.First(result => result.LineNumber == matchLineNumber);
                // Full-span (Phase 3) multiline: render the whole cross-line span N..M (not just the
                // window around the start line N) plus context on both ends, and color each spanned
                // line via its own forced column range — a per-line regex can never match a
                // cross-line pattern, so the shared rx-based highlighting leaves the span uncolored.
                bool multilineMatch = matchResult.IsMultilineMatch;
                int spanEndLine = matchResult.MatchEndLineNumber ?? matchLineNumber;
                int start = Math.Max(0, matchLineNumber - 1 - previewLines);
                int end = Math.Min(allLines.Length - 1, spanEndLine - 1 + previewLines);
                _sectionMatchNavs.TryGetValue(section, out var singleLineSectionNav);

                for (int i = start; i <= end; i++)
                {
                    if (section.Blocks.Count - startingBlocks >= maxBlocks)
                        break;

                    int lineNum = i + 1;
                    bool isMatchLine = lineNum == matchLineNumber;
                    bool inSpan = multilineMatch && lineNum >= matchLineNumber && lineNum <= spanEndLine;
                    int addedParagraphs;
                    if (isMatchLine && !multilineMatch)
                    {
                        AddPreviewLineParagraphsAroundResult(
                            section,
                            allLines[i],
                            lineNum,
                            matchResult,
                            rx,
                            _matchParagraphs,
                            singleLineSectionNav,
                            out addedParagraphs,
                            out _,
                            truncate: truncatePreviewLines,
                            targetOnlyMatchEntry: true,
                            maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks));
                    }
                    else if (inSpan)
                    {
                        // Spanned line: render untruncated with forced-span coloring. The start line
                        // also registers the navigable match entry (anchored at N); body/end lines are
                        // colored but not separately navigable.
                        AddPreviewLineParagraphs(
                            section,
                            allLines[i],
                            lineNum,
                            isMatchLine,
                            matchResult,
                            rx,
                            truncate: false,
                            isMatchLine ? _matchParagraphs : null,
                            singleLineSectionNav,
                            out addedParagraphs,
                            maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks),
                            forcedSpan: ComputeMultilineLineSpan(matchResult, lineNum, allLines[i].Length));
                    }
                    else
                    {
                        AddPreviewLineParagraphs(
                            section,
                            allLines[i],
                            lineNum,
                            isMatchLine,
                            matchResult,
                            rx,
                            truncate: truncatePreviewLines,
                            null,
                            singleLineSectionNav,
                            out addedParagraphs,
                            maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks));
                    }
                    lastRenderedLine1 = lineNum;
                    parasBuilt += addedParagraphs;
                    parasSinceYield += addedParagraphs;
                    if (parasSinceYield >= BuildHighlightYieldEvery)
                    {
                        parasSinceYield = 0;
                        yieldCount++;
                        await DispatchIdleAsync();
                    }
                }

                // Don't return — fall through to AppendHighlightMatchWindows
                // which renders windowed snippets around additional match positions.
            }
            else
            {

            // Full-span (Phase 3) multiline: collect cross-line spans so body/end lines (which are not
            // any result's start line, so matchByLine misses them) still get colored, and so ranges
            // extend past the start line to the span end.
            var multilineSpans = cappedResults
                .Where(result => result.IsMultilineMatch && result.MatchEndLineNumber is int)
                .Select(result => (result, endLine: result.MatchEndLineNumber!.Value))
                .ToList();
            (int start, int end)? SpanForLine(int lineNumber, int lineLength)
            {
                foreach (var (result, endLine) in multilineSpans)
                {
                    if (lineNumber >= result.LineNumber && lineNumber <= endLine)
                    {
                        var span = ComputeMultilineLineSpan(result, lineNumber, lineLength);
                        if (span is not null)
                            return span;
                    }
                }
                return null;
            }

            var ranges = new List<(int start, int end)>();
            foreach (var lineNum in cappedResults.Select(r => r.LineNumber).Distinct().OrderBy(n => n))
            {
                int s = Math.Max(0, lineNum - 1 - previewLines);
                // Extend to the span end for a multiline match starting on this line.
                int spanEnd = cappedResults
                    .Where(r => r.LineNumber == lineNum)
                    .Select(r => r.MatchEndLineNumber ?? lineNum)
                    .DefaultIfEmpty(lineNum)
                    .Max();
                int e = Math.Min(allLines.Length - 1, spanEnd - 1 + previewLines);
                ranges.Add((s, e));
            }
            var merged = new List<(int start, int end)>();
            foreach (var range in ranges.OrderBy(r => r.start))
            {
                if (merged.Count > 0 && range.start <= merged[^1].end + 1)
                    merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, range.end));
                else
                    merged.Add(range);
            }
            var matchByLine = BuildMatchByLineForRanges(results, merged);
            bool firstRange = true;
            int previousRangeEndLine = 0;
            foreach (var (start, end) in merged)
            {
                if (section.Blocks.Count - startingBlocks >= maxBlocks)
                    break;

                if (!firstRange && ShouldAddGapBetweenRenderedLines(previousRangeEndLine, start + 1))
                {
                    AddGapIndicator(section);
                }
                firstRange = false;
                for (int i = start; i <= end; i++)
                {
                    if (section.Blocks.Count - startingBlocks >= maxBlocks)
                        break;

                    int lineNum = i + 1;
                    bool isMatchLine = matchByLine.TryGetValue(lineNum, out var matchResult);
                    matchResult ??= cappedResults[0];
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    var forcedSpan = SpanForLine(lineNum, allLines[i].Length);
                    AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatchLine, matchResult, rx, truncate: truncatePreviewLines && forcedSpan is null, _matchParagraphs, sn, out int addedParagraphs,
                        maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks),
                        forcedSpan: forcedSpan);
                    lastRenderedLine1 = lineNum;
                    parasBuilt += addedParagraphs;
                    parasSinceYield += addedParagraphs;
                    if (parasSinceYield >= BuildHighlightYieldEvery)
                    {
                        parasSinceYield = 0;
                        yieldCount++;
                        await DispatchIdleAsync();
                    }
                }
                previousRangeEndLine = end + 1;
            }
            } // end else (multi-line range rendering)
        }
        else
        {
            // Each stored-context result carries its own ±previewLines window. When matches sit
            // close together those windows overlap, so rendering them independently repeats the
            // same line numbers (a context line of one match is also a context line — or even the
            // match line — of the next). Merge all windows into a single ordered, de-duplicated map
            // of line number -> text, where a match line always wins over a context line for the
            // same number, then render each line once and insert a gap indicator only where source
            // lines are actually skipped.
            var lineText = new Dictionary<int, string>();
            var matchResultByLine = new Dictionary<int, SearchResult>();
            foreach (var r in cappedResults)
            {
                var lines = GetPreviewLines(r, null, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    if (lineNum == r.LineNumber)
                    {
                        // Match line: authoritative text and the owning result for nav registration.
                        lineText[lineNum] = line;
                        matchResultByLine[lineNum] = r;
                    }
                    else if (!lineText.ContainsKey(lineNum))
                    {
                        lineText[lineNum] = line;
                    }
                }
            }

            _sectionMatchNavs.TryGetValue(section, out var sn);

            // Full-span (Phase 3) multiline: a cross-line pattern (e.g. `a\nb`) never matches within a
            // single rendered line, so the shared rx leaves the spanned lines uncolored. When we render
            // from stored context (the on-disk file couldn't be re-read), color each spanned line from
            // the result's span metadata — the start line uses its stored display-space match columns
            // (its text is the possibly-truncated MatchLine), while body/end lines use the source-column
            // span against their full stored text.
            var storedMultilineResults = cappedResults
                .Where(result => result.IsMultilineMatch && result.MatchEndLineNumber is int)
                .ToList();
            (int start, int end)? StoredSpanForLine(int lineNumber, string lineTextForLine)
            {
                foreach (var result in storedMultilineResults)
                {
                    int endLine = result.MatchEndLineNumber!.Value;
                    if (lineNumber < result.LineNumber || lineNumber > endLine)
                        continue;
                    if (lineNumber == result.LineNumber)
                    {
                        int s = Math.Max(0, result.MatchStartColumn);
                        int e = s + Math.Max(0, result.MatchLength);
                        if (e > s)
                            return (s, e);
                    }
                    else
                    {
                        var span = ComputeMultilineLineSpan(result, lineNumber, lineTextForLine.Length);
                        if (span is not null)
                            return span;
                    }
                }
                return null;
            }

            var renderedLineSet = new HashSet<int>();
            int previousLineNum = 0;
            bool firstLine = true;
            foreach (int lineNum in lineText.Keys.OrderBy(n => n))
            {
                if (section.Blocks.Count - startingBlocks >= maxBlocks)
                    break;

                if (!firstLine && ShouldAddGapBetweenRenderedLines(previousLineNum, lineNum))
                    AddGapIndicator(section);
                firstLine = false;

                bool isMatchLine = matchResultByLine.TryGetValue(lineNum, out var matchResult);
                matchResult ??= cappedResults[0];
                var forcedSpan = StoredSpanForLine(lineNum, lineText[lineNum]);
                AddPreviewLineParagraphs(section, lineText[lineNum], lineNum, isMatchLine, matchResult, rx,
                    truncate: truncatePreviewLines && forcedSpan is null,
                    isMatchLine ? _matchParagraphs : null, sn, out int addedParagraphs,
                    maxParagraphs: maxBlocks - (section.Blocks.Count - startingBlocks),
                    forcedSpan: forcedSpan);
                renderedLineSet.Add(lineNum);
                lastRenderedLine1 = lineNum;
                previousLineNum = lineNum;
                parasBuilt += addedParagraphs;
                parasSinceYield += addedParagraphs;
                if (parasSinceYield >= BuildHighlightYieldEvery)
                {
                    parasSinceYield = 0;
                    yieldCount++;
                    await DispatchIdleAsync();
                }
            }

            // A single rendered match line can satisfy several results (dense same-line matches),
            // so count results by whether their line was rendered rather than by distinct entries.
            storedContextCoveredResults = cappedResults.Count(r => renderedLineSet.Contains(r.LineNumber));
        }

        int actualMatchEntries = _matchParagraphs.Count - sectionMatchStart;
        int renderedCount = allLines != null
            ? CountPrefixResultsThroughLine(results, lastRenderedLine1, allLines.Length)
            : (storedContextCoveredResults >= 0 ? storedContextCoveredResults : Math.Min(results.Count, actualMatchEntries));
        // When a line is truncated, regex matches beyond the truncation window are
        // invisible. Cap renderedCount so overflow is registered for the hidden matches
        // and the match-nav total reflects reality (e.g. single-line minified JSON).
        if (allLines != null && renderedCount > actualMatchEntries && actualMatchEntries < results.Count)
            renderedCount = actualMatchEntries;
        int remainingBlockBudget = Math.Max(0, maxBlocks - (section.Blocks.Count - startingBlocks));
        if (allLines != null && remainingBlockBudget > 0 && renderedCount < Math.Min(maxMatches, results.Count))
        {
            _sectionMatchNavs.TryGetValue(section, out var sn);
            var pending = results.Skip(renderedCount).ToList();
            int addedEntries = AppendHighlightMatchWindows(
                section,
                pending,
                allLines,
                rx,
                sn,
                previewLines,
                maxMatches - renderedCount,
                maxMatches - renderedCount,
                remainingBlockBudget,
                out int consumed,
                out int addedParagraphs,
                out int appendLastRenderedLine,
                lastRenderedLine1,
                truncatePreviewLines);
            lastRenderedLine1 = Math.Max(lastRenderedLine1, appendLastRenderedLine);
            renderedCount = Math.Min(results.Count, renderedCount + consumed);
            parasBuilt += addedParagraphs;
            _ = addedEntries;
        }
        else if (allLines == null && renderedCount == 0)
        {
            renderedCount = Math.Min(maxMatches, results.Count);
        }

        var remaining = results.Skip(renderedCount).ToList();
        if (remaining.Count > 0)
        {
            var notice = AppendTruncationNotice(section, results.Count, renderedCount);
            RegisterSectionOverflow(section,
                filePath: null,
                remainingResults: remaining,
                allLines: allLines,
                previewLines: previewLines,
                rx: rx,
                originalTotal: results.Count,
                renderedSoFar: renderedCount,
                noticePara: notice,
                isHighlightMode: allLines != null,
                lastRenderedLine: lastRenderedLine1);
        }

        buildSw.Stop();
        LogService.Instance.Info("Preview", $"BuildHighlightSection: results={results.Count}, rendered={renderedCount}, paragraphs={parasBuilt}, blocks={section.Blocks.Count}, hasAllLines={allLines != null}, activeSearch={ViewModel.IsSearching}, caps=(matches={maxMatches}, blocks={maxBlocks}), yields={yieldCount}, elapsed={buildSw.ElapsedMilliseconds}ms");
        LogPreviewShowMoreDiagnostics("BuildHighlightSection");
    }

    /// <summary>
    /// Pre-computes the total regex match count for a section without building any UI elements.
    /// Used for lazy sections so the global nav label can show the correct total.
    /// </summary>
    private static int ComputeMatchCount(
        List<SearchResult> results, string[]? allLines,
        bool isHighlight, int previewLines, Regex? rx)
    {
        if (results.All(result => result.LineNumber <= 0))
            return 0;

        results = results.Where(result => result.LineNumber > 0).ToList();
        int total = 0;
        if (isHighlight && allLines != null)
        {
            var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));
            foreach (int lineNum in matchLines)
            {
                int idx = lineNum - 1;
                if (idx >= 0 && idx < allLines.Length)
                    total += CountRegexMatches(allLines[idx], rx);
            }
        }
        else if (isHighlight)
        {
            // Approximation when we haven't read the file yet: count matches
            // across one MatchLine per unique line number. MatchLine may be
            // truncated (ShortPreview), so this can undercount on very long
            // lines, but it's recomputed exactly when the section materializes.
            var seen = new HashSet<int>();
            foreach (var r in results)
            {
                if (seen.Add(r.LineNumber))
                    total += CountRegexMatches(r.MatchLine, rx);
            }
        }
        else
        {
            foreach (var r in results)
                total += CountRegexMatches(r.MatchLine, rx);
        }
        return total;
    }

    /// <summary>
    /// Materializes a lazy section: builds paragraphs, adds match entries, and removes it from the lazy dictionary.
    /// Returns true if the section was lazy and has been materialized.
    /// </summary>
    private async Task<bool> MaterializeLazySectionAsync(RichTextBlock section)
    {
        if (!_lazySections.Remove(section, out var lazy))
            return false;

        var matSw = Stopwatch.StartNew();
        LogService.Instance.Info("Preview", $"MaterializeLazySection: file='{Path.GetFileName(lazy.FilePath)}', matches={lazy.MatchCount}");

        // Lazy file read: bulk inserts skip the upfront read for collapsed
        // sections. Read the single file now, on demand.
        string[]? allLines = lazy.AllLines;
        if (allLines is null)
        {
            try { allLines = ReadAllLinesWithEncodingSync(lazy.FilePath); }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"MaterializeLazySection: read failed for '{lazy.FilePath}': {ex.GetType().Name}: {ex.Message}");
                allLines = null;
            }
        }

        Regex? rx = BuildSearchHighlightRegex();
        if (lazy.IsHighlight)
            await BuildHighlightSectionAsync(section, lazy.Results, allLines, lazy.PreviewLines, rx);
        else
            BuildConcatenatedSection(section, lazy.Results, allLines, lazy.PreviewLines, rx);

        _lazyMatchCount -= lazy.MatchCount;
        matSw.Stop();
        LogService.Instance.Info("Preview", $"MaterializeLazySection complete: file='{Path.GetFileName(lazy.FilePath)}', elapsed={matSw.ElapsedMilliseconds}ms, remainingLazy={_lazySections.Count}");
        return true;
    }

    /// <summary>
    /// Materializes all remaining lazy sections at once, with a progress overlay.
    /// </summary>
    private bool _suppressExpandingHandler;

    private async Task MaterializeAllLazySectionsAsync()
    {
        if (_lazySections.Count == 0) return;

        var lazyBlocks = _lazySections.Keys.ToList();
        int total = lazyBlocks.Count;
        LogService.Instance.Info("Preview", $"MaterializeAllLazySectionsAsync: starting, total={total}");
        ShowProgressOverlay($"Rendering {total:N0} sections\u2026", 0);

        try
        {
            int done = 0;
            foreach (var block in lazyBlocks)
            {
                try
                {
                    await MaterializeLazySectionAsync(block);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview", "MaterializeLazySection failed for one section; skipping.", ex);
                }
                done++;
                if (done % PreviewYieldBatchSize == 0 || done == total)
                {
                    UpdateProgressOverlay(done * 100 / total);
                    // Use DispatchIdleAsync (DispatcherQueue.TryEnqueue) instead of
                    // Task.Delay(1).ConfigureAwait(true). On WinUI 3 the UI thread does
                    // not necessarily have a SynchronizationContext installed, so a
                    // Task.Delay continuation can resume on a threadpool thread; any
                    // subsequent XAML touch from there triggers a CoreMessagingXP
                    // fail-fast (exception 0xE0464645). DispatchIdleAsync guarantees
                    // we resume on the UI thread.
                    await DispatchIdleAsync();
                }
            }

            LogService.Instance.Info("Preview", $"MaterializeAllLazySectionsAsync: materialization complete, expanding sections");
            UpdateMatchNavPanel();
            UpdateSectionMatchNavPanels();
        }
        finally
        {
            // Hide the overlay before the expand phase. Expanding many Expanders triggers
            // WinUI layout passes that can take noticeable time, but the user's "spinner
            // forever at 100%" feedback was that the overlay made it look frozen.
            HideProgressOverlay();
        }

        // Expand all sections without firing the per-section Expanding side effects
        // (which would call MaterializeLazySection (no-op now) and ActivateSectionForBlock,
        // and the latter is O(N) per call so the bulk expand becomes O(N^2) and
        // appears to hang for hundreds of sections).
        //
        // Crash mitigation: with 2000+ Expanders, flipping IsExpanded in a tight loop
        // queues thousands of concurrent expand-state storyboards and content-reveal
        // theme transitions on CoreMessagingXP, which fail-fasts (0xE0464645) when its
        // dispatcher buffers fill up. Two mitigations applied here:
        //   1) Clear the per-Expander ContentTransitions so each expansion does not
        //      schedule the default reveal theme transition (the biggest offender).
        //   2) Yield to the UI dispatcher via DispatchIdleAsync between batches so the
        //      messaging queue can drain. Smaller batch (1) than the materialize loop
        //      because each Expander expansion still triggers a layout pass.
        _suppressExpandingHandler = true;
        try
        {
            int expanded = 0;
            const int expandYieldBatchSize = 1;
            foreach (var child in PreviewSectionsPanel.Children)
            {
                if (child is Expander exp && !exp.IsExpanded)
                {
                    try
                    {
                        // Suppress the content-reveal theme transition that fires
                        // when IsExpanded flips. The chevron-rotation storyboard in
                        // the Expander template still runs but is comparatively cheap.
                        exp.ContentTransitions = null;
                        exp.IsExpanded = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning("Preview", "Failed to expand section.", ex);
                    }
                }
                expanded++;
                if (expanded % expandYieldBatchSize == 0)
                    await DispatchIdleAsync();
            }
        }
        finally
        {
            _suppressExpandingHandler = false;
        }
        LogService.Instance.Info("Preview", "MaterializeAllLazySectionsAsync: done");
    }

    private string[] ReadAllLinesWithEncodingSync(string filePath)
    {
        // Image files have no readable text content. Return their recognized OCR text
        // (split into lines) so the normal preview pipeline — line-number gutter, match
        // highlighting, and the active-match box — renders the recognized text instead of
        // the raw binary bytes. An empty array means "image with no cached OCR text yet".
        if (IsImagePreviewPath(filePath))
            return GetPreviewOcrLines(filePath) ?? Array.Empty<string>();

        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var encoding = Helpers.EncodingDetector.DetectEncoding(fs);
        if (encoding is UTF8Encoding)
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        fs.Position = 0;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();

        if (content.Length == 0)
            return Array.Empty<string>();

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                int end = (i > start && content[i - 1] == '\r') ? i - 1 : i;
                lines.Add(content[start..end]);
                start = i + 1;
            }
        }
        if (start < content.Length)
        {
            int end = content[content.Length - 1] == '\r' ? content.Length - 1 : content.Length;
            lines.Add(content[start..end]);
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Reads all lines from a file, splitting only on <c>\n</c> (stripping optional
    /// trailing <c>\r</c>).  This matches the Rust <c>bstr::ByteSlice::lines()</c>
    /// behaviour used by the native search engine so that line numbers agree between
    /// the searcher and the preview panel.  C#'s <c>StreamReader.ReadLine</c> also
    /// splits on lone <c>\r</c>, which creates phantom extra lines in binary files
    /// and causes the highlighted line to drift from the actual match.
    /// </summary>
    private async Task<string[]> ReadAllLinesWithEncodingAsync(string filePath)
    {
        // See ReadAllLinesWithEncodingSync: images render their recognized OCR text, not bytes.
        if (IsImagePreviewPath(filePath))
            return GetPreviewOcrLines(filePath) ?? Array.Empty<string>();

        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var encoding = Helpers.EncodingDetector.DetectEncoding(fs);
        if (encoding is UTF8Encoding)
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        fs.Position = 0;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync();

        // Split on \n only (matching bstr::lines), strip trailing \r from each line.
        if (content.Length == 0)
            return Array.Empty<string>();

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                int end = (i > start && content[i - 1] == '\r') ? i - 1 : i;
                lines.Add(content[start..end]);
                start = i + 1;
            }
        }
        // Trailing content after last \n (bstr::lines includes it if non-empty).
        if (start < content.Length)
        {
            int end = content[content.Length - 1] == '\r' ? content.Length - 1 : content.Length;
            lines.Add(content[start..end]);
        }
        return lines.ToArray();
    }

    private async Task ExpandSectionToFullFileAsync(RichTextBlock section, string filePath, List<SearchResult> results)
    {
        // Remove lazy section data if it was never rendered
        if (_lazySections.Remove(section, out var lazySec))
        {
            _lazyMatchCount -= lazySec.MatchCount;
            UpdateExpandAllButtonVisibility();
        }

        string[]? allLines = null;
        try { allLines = await ReadAllLinesWithEncodingAsync(filePath); }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", $"Cannot read file for full-file section preview: {filePath}", ex);
            return;
        }

        var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));
        bool hasContentMatches = results.Any(result => result.LineNumber > 0);
        Regex? rx = hasContentMatches
            ? BuildSearchHighlightRegex()
            : null;

        int insertionIndex = _matchParagraphs.FindIndex(m => ReferenceEquals(m.block, section));
        if (insertionIndex < 0)
            insertionIndex = _matchParagraphs.Count;

        int currentSectionOrdinal = -1;
        for (int i = 0, ordinal = 0; i < _matchParagraphs.Count; i++)
        {
            if (!ReferenceEquals(_matchParagraphs[i].block, section))
                continue;

            if (i == _currentMatchIndex)
            {
                currentSectionOrdinal = ordinal;
                break;
            }

            ordinal++;
        }

        UnboxCurrentMatch();

        _matchParagraphs.RemoveAll(m => m.block == section);
        _sectionMatchNavs.TryGetValue(section, out var sn);
        sn?.Matches.Clear();

        section.Blocks.Clear();
        InvalidateParagraphIndexCache(section);
        var sectionMatches = new List<(RichTextBlock block, Paragraph para, int matchInPara)>();
        for (int i = 0; i < allLines.Length; i++)
        {
            int lineNum = i + 1;
            bool isMatch = hasContentMatches && matchLines.Contains(lineNum);
            var matchResult = isMatch
                ? results.FirstOrDefault(r => r.LineNumber == lineNum) ?? results[0]
                : results[0];
            AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatch, matchResult, rx, truncate: false, sectionMatches, sn, out _);
        }

        (Paragraph para, int matchInPara)? matchToReveal = null;
        if (sectionMatches.Count > 0)
        {
            insertionIndex = Math.Clamp(insertionIndex, 0, _matchParagraphs.Count);
            _matchParagraphs.InsertRange(insertionIndex, sectionMatches);

            int revealOrdinal = currentSectionOrdinal >= 0
                ? Math.Min(currentSectionOrdinal, sectionMatches.Count - 1)
                : 0;
            var revealEntry = sectionMatches[revealOrdinal];
            matchToReveal = (revealEntry.para, revealEntry.matchInPara);
        }

        if (allLines.Length == 0)
        {
            var para = new Paragraph();
            var run = new Run { Text = "(empty file)" };
            run.Foreground = s_contextTextBrush;
            para.Inlines.Add(run);
            section.Blocks.Add(para);
        }

        // Update navigation state
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        if (matchToReveal is { } reveal)
        {
            SetCurrentMatchToMatch(section, reveal.para, reveal.matchInPara);
            ScrollPreviewToLine(section, reveal.para);
        }
    }

    private static bool HasReadablePreviewLine(IEnumerable<SearchResult> results, string[] allLines)
        => allLines.Length > 0 && results.Any(result => result.LineNumber >= 1 && result.LineNumber <= allLines.Length);

    private static List<(string line, int lineNum)> GetPreviewLines(SearchResult r, string[]? allLines, int previewLines, bool fullFile)
    {
        var lines = new List<(string, int)>();
        int matchLineNum = r.LineNumber;

        if (fullFile && allLines is { Length: > 0 })
        {
            for (int i = 0; i < allLines.Length; i++)
                lines.Add((allLines[i], i + 1));
        }
        else if (allLines is { Length: > 0 } && matchLineNum >= 1 && matchLineNum <= allLines.Length)
        {
            int matchIdx = matchLineNum - 1;

            int startLine, endLine;
            if (fullFile)
            {
                startLine = 0;
                endLine = allLines.Length - 1;
            }
            else
            {
                // For a cross-line (multiline) match, extend the window to cover the whole span
                // N..M plus context on both ends so full-span (Phase 3) highlighting has every
                // spanned line available. Single-line results (MatchEndLineNumber null) reduce to
                // matchLineNum, i.e. the previous behavior exactly.
                int spanEndLine = r.MatchEndLineNumber ?? matchLineNum;
                startLine = Math.Max(0, matchIdx - previewLines);
                endLine = Math.Min(allLines.Length - 1, (spanEndLine - 1) + previewLines);
            }
            for (int i = startLine; i <= endLine; i++)
            {
                lines.Add((allLines[i], i + 1));
            }
        }
        else
        {
            int ln = matchLineNum - r.ContextBefore.Count;
            foreach (var line in r.ContextBefore) lines.Add((line, ln++));
            lines.Add((r.MatchLine, matchLineNum));
            ln = matchLineNum + 1;
            foreach (var line in r.ContextAfter) lines.Add((line, ln++));
        }
        return lines;
    }

    private async Task ShowSingleFilePreviewAsync(SearchResult r, bool fullFile)
    {
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync: file='{r.FilePath}', line={r.LineNumber}, fullFile={fullFile}");
        bool isFileNameOnlyPreview = r.LineNumber <= 0;
        fullFile |= isFileNameOnlyPreview;
        var singleSw = Stopwatch.StartNew();
        BeginPreviewContentUpdate();
        ShowPreviewBlockSurface();
        PreviewBlock.Tag = r.FilePath;

        ViewModel.HydrateResult(r);

        bool isImagePreview = IsImagePreviewPath(r.FilePath);
        // Image previews are read-only (OCR text) — disable the "full file" editor entry.
        FullFileButton.IsEnabled = !isImagePreview;
        string[]? allLines = null;
        if (isImagePreview)
        {
            // Images are not text files: show a thumbnail and render the recognized OCR
            // text (if any) instead of the raw binary bytes.
            await ShowPreviewImageThumbnailAsync(r.FilePath);
            allLines = GetPreviewOcrLines(r.FilePath);
        }
        else
        {
            HidePreviewImageThumbnail();
            // Read the file for context lines so we always show the configured PreviewContextLines
            // amount, not just whatever the SearchResult stored (which may be fewer or empty after eviction).
            try { allLines = await ReadAllLinesWithEncodingAsync(r.FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                LogService.Instance.Verbose("Preview", $"ShowSingleFilePreviewAsync: cannot read file '{r.FilePath}', using stored context: {ex.GetType().Name}");
            }
        }

        _previewMutating = true;
        try
        {
        // Clear stale match-nav state before clearing Blocks. Single-file
        // preview doesn't repopulate _matchParagraphs, so leaving prior
        // entries around leads to orphaned-Paragraph access later (E_FAIL
        // in Microsoft.UI.Xaml.dll from GetCharacterRect on a removed run).
        UnboxCurrentMatch();
        _matchParagraphs.Clear();
        _paragraphMatchRunCache.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        PreviewBlock.Blocks.Clear();
        Regex? rx = isFileNameOnlyPreview
            ? null
            : BuildSearchHighlightRegex();

        if (isImagePreview && (allLines is null || allLines.Length == 0))
        {
            AddPreviewPlainNote(PreviewBlock,
                "No recognized text for this image. Turn on \u201CSearch image text\u201D and run a search to extract text.");
            singleSw.Stop();
            LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync complete: image with no OCR text, elapsed={singleSw.ElapsedMilliseconds}ms");
        }
        else
        {
        int lineCount = 0;
        bool truncatePreviewLines = !fullFile && ShouldTruncateInitialPreviewLines();
        var lines = GetPreviewLines(r, allLines, ViewModel.PreviewContextLines, fullFile);
        int maxLineLen = 0;
        foreach (var (l, _) in lines)
            if (l is not null && l.Length > maxLineLen) maxLineLen = l.Length;
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync rebuild: wrapMode={ViewModel.PreviewWrapModeIndex}, segmentCap={GetEffectiveSegmentSize()}, truncate={truncatePreviewLines}, lines={lines.Count}, maxLineLen={maxLineLen}");
        foreach (var (line, lineNum) in lines)
        {
            bool isMatchLine = isFileNameOnlyPreview || lineNum == r.LineNumber;
            // Full-span (Phase 3) multiline: color each spanned line via its forced column range and
            // render it untruncated so the columns map 1:1 (single-line results yield null → unchanged).
            var forcedSpan = ComputeMultilineLineSpan(r, lineNum, line?.Length ?? 0);
            AddPreviewLineParagraphs(PreviewBlock, line, lineNum, isMatchLine, r, rx, truncate: truncatePreviewLines && forcedSpan is null, null, null, out int addedParagraphs, forcedSpan: forcedSpan);
            lineCount += addedParagraphs;
        }
        singleSw.Stop();
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync complete: lines={lineCount}, blocks={PreviewBlock.Blocks.Count}, elapsed={singleSw.ElapsedMilliseconds}ms");
        }

        }
        finally
        {
            _previewMutating = false;
            CompletePreviewContentUpdate();
        }
    }

    private void ShowPreviewBlockSurface()
    {
        PreviewScrollViewer.Padding = new Thickness(16, 12, 16, 12);
        PreviewMessagePanel.Visibility = Visibility.Visible;
        HidePreviewImageThumbnail();
        PreviewSectionsPanel.Children.Clear();
        PreviewSectionsPanel.Visibility = Visibility.Collapsed;
        PreviewBlock.TextWrapping = ViewModel.PreviewWordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ConfigurePreviewSelectionMode(PreviewBlock);
        // Keep the single-file block in sync with the configured/zoomed preview font.
        int previewTextFontSize = ResolvePreviewTextFontSize();
        ApplyPreviewTextFontSettings(
            PreviewBlock,
            ResolvePreviewTextFontFamily(),
            previewTextFontSize,
            ResolvePreviewTextLineHeight(previewTextFontSize));
        PreviewBlock.Visibility = Visibility.Visible;
        ApplyPreviewBlockContentBackground();
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Visible);
        HideMatchNavPanel();
        // Restore outer horizontal scroll for single-file block view.
        ApplyPreviewHorizontalScrollForWrap(PreviewScrollViewer, ViewModel.PreviewWordWrap);
        HideStickyHorizontalScrollBar();
        UpdatePreviewEmptyState();
    }

    private void ShowPreviewSectionsSurface()
    {
        LogService.Instance.Info("Preview", $"ShowPreviewSectionsSurface: clearing {PreviewSectionsPanel.Children.Count} existing sections");
        PreviewScrollViewer.Padding = new Thickness(0, 0, 0, 0);
        PreviewBlock.Blocks.Clear();
        PreviewBlock.Visibility = Visibility.Collapsed;
        HidePreviewImageThumbnail();
        ClearPreviewBlockContentBackground();
        PreviewSectionsPanel.Children.Clear();
        PreviewSectionsPanel.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Collapsed);
        HideMatchNavPanel();
        // Sections have their own per-section horizontal scroll; outer viewer stays vertical-only.
        SetHorizontalPreviewScroll(PreviewScrollViewer, enabled: false);
        HideStickyHorizontalScrollBar();
        UpdatePreviewEmptyState();
    }

    private void ApplyPreviewBlockContentBackground()
    {
        var brush = CreatePreviewSectionContentBackgroundBrush(isActive: true);
        PreviewScrollViewer.Background = brush;
        if (PreviewScrollViewer.Content is Grid grid)
            grid.Background = brush;
        PreviewMessagePanel.Background = brush;
    }

    private void ClearPreviewBlockContentBackground()
    {
        PreviewScrollViewer.Background = null;
        if (PreviewScrollViewer.Content is Grid grid)
            grid.Background = null;
        PreviewMessagePanel.Background = null;
    }

    private void ShowPreviewLoading(string message = "Loading preview…")
    {
        PreviewScrollViewer.Padding = new Thickness(16, 12, 16, 12);
        PreviewMessagePanel.Visibility = Visibility.Collapsed;
        ClearPreviewBlockContentBackground();
        PreviewSectionsPanel.Visibility = Visibility.Collapsed;
        PreviewEmptyState.Visibility = Visibility.Collapsed;
        PreviewLoadingText.Text = message;
        PreviewLoadingRing.IsActive = true;
        PreviewLoadingPanel.Visibility = Visibility.Visible;
    }

    private void HidePreviewLoading()
    {
        PreviewLoadingRing.IsActive = false;
        PreviewLoadingPanel.Visibility = Visibility.Collapsed;
        UpdatePreviewEmptyState();
    }

    private void UpdatePreviewEmptyState()
    {
        bool editorVisible = PreviewEditor.Visibility == Visibility.Visible
            || PreviewEditorContainer.Visibility == Visibility.Visible;
        bool busy = PreviewLoadingPanel.Visibility == Visibility.Visible
            || PreviewProgressOverlay.Visibility == Visibility.Visible
            || _previewContentPending;
        bool hasBlockContent = PreviewMessagePanel.Visibility == Visibility.Visible
            && PreviewBlock.Visibility == Visibility.Visible
            && PreviewBlock.Blocks.Count > 0;
        bool hasSections = PreviewSectionsPanel.Visibility == Visibility.Visible
            && PreviewSectionsPanel.Children.OfType<Expander>().Any();
        bool showEmptyState = PreviewPanelBorder.Visibility == Visibility.Visible
            && !editorVisible
            && !busy
            && !hasBlockContent
            && !hasSections;

        PreviewEmptyState.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;

        // When the preview empties out ("Nothing to show") there is no selectable
        // content left, so drop any lingering custom text selection. Its overlay
        // rectangles live on PreviewSelectionOverlay (ZIndex above the empty state)
        // and would otherwise stay painted as ghost highlights no matter which path
        // emptied the preview (clear button, file dismiss, deselect, results clear).
        // This is the single chokepoint every empty-state transition funnels through.
        if (showEmptyState)
            ClearPreviewCustomSelection();
    }

    private void BeginPreviewContentUpdate()
    {
        _previewContentPending = true;
        PreviewEmptyState.Visibility = Visibility.Collapsed;
    }

    private void CompletePreviewContentUpdate()
    {
        _previewContentPending = false;
        UpdatePreviewEmptyState();
    }

    private void ShowProgressOverlay(string message, int percent)
    {
        PreviewEmptyState.Visibility = Visibility.Collapsed;
        PreviewProgressText.Text = message;
        PreviewProgressPercent.Text = $"{percent}%";
        PreviewProgressRing.IsActive = true;
        PreviewProgressOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateProgressOverlay(int percent)
    {
        PreviewProgressPercent.Text = $"{percent}%";
    }

    private void HideProgressOverlay()
    {
        PreviewProgressRing.IsActive = false;
        PreviewProgressOverlay.Visibility = Visibility.Collapsed;
        UpdatePreviewEmptyState();
    }

    private void SetPerFileToolbarVisibility(Visibility visibility)
    {
        CopyPreviewFilePathButton.Visibility = visibility;
        PreviewToolbarSeparator.Visibility = visibility;
        FullFileButton.Visibility = visibility;
        OpenInDefaultAppButton.Visibility = visibility;
        OpenInEditorButton.Visibility = visibility;
    }

    // Eagerly constructed (cheap: it only stores a directory path) so it can be read safely
    // from the background read threads in ReadAllFileContentsAsync / AppendCheckedMatchContextAsync
    // without a lazy-init race.
    private readonly Yagu.Services.Ocr.OcrTextCache _previewOcrTextCache = new();

    private Yagu.Services.Ocr.OcrTextCache PreviewOcrTextCache => _previewOcrTextCache;

    /// <summary>
    /// True when <paramref name="path"/> is an OCR-able image. Image previews show a thumbnail
    /// plus the recognized OCR text and are intentionally not text-editable.
    /// </summary>
    private static bool IsImagePreviewPath(string? path)
        => !string.IsNullOrEmpty(path) && Yagu.Services.Ocr.ImageOcrSupport.IsImageCandidate(path);

    /// <summary>
    /// Returns the cached OCR text for an image split into lines (so the normal preview
    /// match-highlighting pipeline can render it), or <c>null</c> when no recognized text is cached.
    /// </summary>
    private string[]? GetPreviewOcrLines(string path)
    {
        string engineId = AppSettings.NormalizeImageOcrEngine(ViewModel.ImageOcrEngine);
        if (!PreviewOcrTextCache.TryGet(path, engineId, out string text) || string.IsNullOrEmpty(text))
            return null;
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>Loads and shows a bounded thumbnail of the image above the preview text.</summary>
    private async Task ShowPreviewImageThumbnailAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
                DecodePixelHeight = 480,
            };
            await bitmap.SetSourceAsync(stream);
            PreviewImageThumbnail.Source = bitmap;
            PreviewImageThumbnailBorder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Preview",
                $"ShowPreviewImageThumbnailAsync: failed for '{path}': {ex.GetType().Name}: {ex.Message}");
            HidePreviewImageThumbnail();
        }
    }

    private void HidePreviewImageThumbnail()
    {
        PreviewImageThumbnailBorder.Visibility = Visibility.Collapsed;
        PreviewImageThumbnail.Source = null;
    }

    /// <summary>
    /// Builds a bounded image thumbnail that lives inside a section drawer, directly above the
    /// recognized OCR text. Each image section owns its own <see cref="Microsoft.UI.Xaml.Controls.Image"/>
    /// so multiple image previews each render their own picture (rather than a single shared
    /// thumbnail floating above every drawer). The thumbnail is centered in the drawer and carries
    /// an overlaid expand button that opens the original image at full quality/size in a modal.
    /// </summary>
    private Border CreateSectionImageThumbnail(string path)
    {
        var image = new Microsoft.UI.Xaml.Controls.Image
        {
            MaxHeight = 320,
            MaxWidth = 480,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };

        // Expand button overlays the thumbnail's top-right corner. Clicking it opens the
        // full-resolution original in a closable modal. It uses a constant semi-opaque dark
        // "chip" with a white glyph (per-control lightweight styling that pins the Button's
        // per-state theme brushes) so the affordance stays visible on ANY image behind it —
        // light, dark, or busy — in every visual state, instead of blending into the picture.
        var chipGlyphBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        var chipBackground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xB3, 0x00, 0x00, 0x00));
        var chipBackgroundHover = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xCC, 0x00, 0x00, 0x00));
        var chipBackgroundPressed = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x00, 0x00, 0x00));
        var chipBorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        var expandButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE740", FontSize = 12 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Resources =
            {
                ["ButtonBackground"] = chipBackground,
                ["ButtonBackgroundPointerOver"] = chipBackgroundHover,
                ["ButtonBackgroundPressed"] = chipBackgroundPressed,
                ["ButtonBackgroundDisabled"] = chipBackground,
                ["ButtonForeground"] = chipGlyphBrush,
                ["ButtonForegroundPointerOver"] = chipGlyphBrush,
                ["ButtonForegroundPressed"] = chipGlyphBrush,
                ["ButtonBorderBrush"] = chipBorderBrush,
                ["ButtonBorderBrushPointerOver"] = chipBorderBrush,
                ["ButtonBorderBrushPressed"] = chipBorderBrush,
            },
        };
        ToolTipService.SetToolTip(expandButton, "View full size");
        string capturedPath = path;
        expandButton.Click += (_, _) => _ = ShowFullSizeImageModalAsync(capturedPath);

        var overlay = new Grid();
        overlay.Children.Add(image);
        overlay.Children.Add(expandButton);

        var border = new Border
        {
            Margin = new Thickness(8, 4, 8, 12),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = TryGetPreviewBrushResource("CardStrokeColorDefaultBrush"),
            Background = TryGetPreviewBrushResource("CardBackgroundFillColorSecondaryBrush"),
            Child = overlay,
        };
        _ = LoadSectionImageThumbnailAsync(image, path);
        return border;
    }

    /// <summary>Loads the bounded bitmap for a section image thumbnail.</summary>
    private static async Task LoadSectionImageThumbnailAsync(Microsoft.UI.Xaml.Controls.Image image, string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
                DecodePixelHeight = 480,
            };
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Preview",
                $"LoadSectionImageThumbnailAsync: failed for '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the original image at full quality and native resolution in a closable, resizable
    /// modal. The image sits in a pan/zoom <see cref="ScrollViewer"/> so pictures larger than the
    /// modal can be scrolled or zoomed to fit.
    /// </summary>
    private async Task ShowFullSizeImageModalAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var stream = await file.OpenReadAsync())
                await bitmap.SetSourceAsync(stream);

            var image = new Microsoft.UI.Xaml.Controls.Image
            {
                Source = bitmap,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var scroller = new ScrollViewer
            {
                Content = image,
                HorizontalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = 0.1f,
                MaxZoomFactor = 8f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var (width, height) = GetFullSizeImageModalDimensions();
            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = Path.GetFileName(path),
                    TitleGlyph = "\uEB9F", // Picture
                    Content = scroller,
                    CloseButtonText = "Close",
                    ShowTopRightCloseButton = true,
                    DefaultButton = YaguDialogDefaultButton.Close,
                    IsResizable = true,
                    Width = width,
                    Height = height,
                    MaxContentHeight = height,
                });
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"ShowFullSizeImageModalAsync: failed for '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Sizes the full-size image modal to ~85% of the owner monitor's work area.</summary>
    private (int width, int height) GetFullSizeImageModalDimensions()
    {
        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            int width = (int)(display.WorkArea.Width * 0.85);
            int height = (int)(display.WorkArea.Height * 0.85);
            return (Math.Max(640, width), Math.Max(480, height));
        }
        catch
        {
            return (1100, 800);
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush? TryGetPreviewBrushResource(string key)
        => Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;


    private static void AddPreviewPlainNote(RichTextBlock block, string text)
    {
        var para = new Paragraph();
        para.Inlines.Add(new Run { Text = text, FontStyle = Windows.UI.Text.FontStyle.Italic });
        para.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        block.Blocks.Add(para);
    }

    private IEnumerable<RichTextBlock> EnumeratePreviewSectionBlocks()
    {
        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (expander.Content is Grid g
                && g.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv
                && sv.Content is Border { Child: RichTextBlock block })
                yield return block;
            else if (expander.Content is ScrollViewer sv2 && sv2.Content is Border { Child: RichTextBlock block2 })
                yield return block2;
            else if (expander.Content is Border { Child: RichTextBlock legacyBlock })
                yield return legacyBlock;
        }
    }

    private (RichTextBlock block, Expander expander) AddPreviewSection(string filePath, string? detail = null, List<SearchResult>? results = null, bool isExpanded = true, bool addToPanel = true)
    {
        LogService.Instance.Verbose("Preview", $"AddPreviewSection: file='{Path.GetFileName(filePath)}', detail='{detail}', expanded={isExpanded}, addToPanel={addToPanel}");
        bool wrap = ViewModel.PreviewWrapModeIndex == (int)Models.PreviewWrapMode.Wrap;
        string previewTextFontFamily = ResolvePreviewTextFontFamily();
        int previewTextFontSize = ResolvePreviewTextFontSize();
        double previewTextLineHeight = ResolvePreviewTextLineHeight(previewTextFontSize);
        var block = new RichTextBlock
        {
            FontFamily = new FontFamily(previewTextFontFamily),
            FontSize = previewTextFontSize,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            LineHeight = previewTextLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsTextSelectionEnabled = false,
            Tag = filePath,
        };
        AttachPreviewBlockContextFlyout(block);

        var gutterBlock = new RichTextBlock
        {
            FontFamily = new FontFamily(previewTextFontFamily),
            FontSize = previewTextFontSize,
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = previewTextLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsTextSelectionEnabled = false,
        };
        _sectionGutterBlocks[block] = gutterBlock;

        // Sync gutter paragraph heights after layout so the line numbers stay
        // aligned with content paragraphs that wrap to multiple visual lines.
        block.SizeChanged += (_, _) => ScheduleGutterSync(block);

        var content = new Border
        {
            Padding = new Thickness(8, 0, 0, 0),
            Child = block,
        };
        block.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => ActivateSectionForBlock(block)),
            handledEventsToo: true);

        // Double-click anywhere in this section's preview text to open the
        // inline editor positioned at the clicked line. Use AddHandler with
        // handledEventsToo so RichTextBlock's built-in text-selection logic
        // (which marks DoubleTapped handled when it selects a word) doesn't
        // swallow the event before we see it.
        var capturedSectionPath = filePath;
        block.AddHandler(UIElement.DoubleTappedEvent,
            new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(async (s, e) =>
            {
                if (_previewMutating) return;
                // Skip if the custom selection pointer handler already opened the
                // editor for this double-click (native DoubleTapped is normally
                // suppressed, but guard against both firing).
                if (Environment.TickCount64 - _previewEditorPointerOpenTick < PreviewEditorPointerOpenGuardMs) return;
                if (s is RichTextBlock rtb)
                    await EnterPreviewEditorAtPointAsync(rtb, e, capturedSectionPath);
            }),
            handledEventsToo: true);

        var sectionScroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled,
            // Hidden (not Visible): the native bar would render at the bottom of the
            // section's full content height — far below the viewport. The shared
            // StickyHorizontalScrollBar overlay (driven from code-behind) surfaces
            // the actual horizontal extent within the viewport. Mode stays Enabled
            // so keyboard / programmatic scrolling continues to work.
            HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        sectionScroller.ViewChanged += OnSectionScrollerViewChanged;
        // ViewChanged alone misses initial layout (offset stays at 0). SizeChanged
        // on the scroller (viewport) and its content (extent) covers content first
        // appearing, font changes, wrap-mode toggles, and parent-resize cases.
        sectionScroller.SizeChanged += OnSectionScrollerSizeChanged;
        content.SizeChanged += OnSectionScrollerSizeChanged;

        // Two-column grid: fixed gutter (line numbers) + scrollable content.
        // Row 0 hosts the image thumbnail for image previews (zero-height for text
        // files); row 1 holds the gutter + scrollable content.
        var sectionGrid = new Grid();
        sectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sectionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sectionGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Image previews render the picture inside this file's own drawer, directly above
        // its recognized OCR text, so multiple image sections each show their own image
        // instead of a single shared thumbnail floating above all drawers.
        if (IsImagePreviewPath(filePath))
        {
            var imageBorder = CreateSectionImageThumbnail(filePath);
            Grid.SetRow(imageBorder, 0);
            Grid.SetColumn(imageBorder, 0);
            Grid.SetColumnSpan(imageBorder, 2);
            sectionGrid.Children.Add(imageBorder);
        }

        var gutterBorder = new Border
        {
            // Right border draws the continuous gutter separator line. It reuses
            // s_gutterSepBrush (the "Preview gutter text" color) so the line stays
            // user-configurable and updates live alongside the gutter numbers.
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = s_gutterSepBrush,
            Child = gutterBlock,
        };
        Grid.SetRow(gutterBorder, 1);
        Grid.SetColumn(gutterBorder, 0);
        sectionGrid.Children.Add(gutterBorder);
        Grid.SetRow(sectionScroller, 1);
        Grid.SetColumn(sectionScroller, 1);
        sectionGrid.Children.Add(sectionScroller);

        var sectionNav = new SectionMatchNav
        {
            Scroller = sectionScroller,
            Block = block,
        };
        _sectionMatchNavs[block] = sectionNav;
        AttachPreviewSelectionAutoScroll(block);

        var expander = new Expander
        {
            Header = BuildPreviewSectionHeader(filePath, detail, block, results),
            Content = sectionGrid,
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = block,
        };
        expander.Expanding += async (s, _) =>
        {
            InvalidateScrollPositionCache();
            if (_suppressExpandingHandler) return;
            try
            {
                if (s is Expander exp && exp.Tag is RichTextBlock b)
                {
                    // Consume the auto-materialization tag synchronously, before the
                    // first await: a section expanded by the scroll-driven
                    // MaterializeVisibleLazySections sweep must render its content but
                    // must NOT become the active/selected section, otherwise scrolling
                    // past a sibling steals the selected background from the file the
                    // user is reading. Manual expansion (header click), match
                    // navigation, and scroll-to-section are untagged and still activate.
                    bool autoMaterialized = _autoMaterializingSections.Remove(b);
                    UpdateExpandAllButtonVisibility();
                    if (await MaterializeLazySectionAsync(b) && MatchNavPanel.Visibility == Visibility.Visible)
                        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
                    // Re-apply the current wrap state in case the user toggled wrap while
                    // this section was collapsed (we skip collapsed sections in
                    // ApplyWordWrapAsync to keep the toggle responsive for huge previews).
                    var wrap = ViewModel.PreviewWordWrap;
                    b.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    ConfigurePreviewSelectionMode(b);
                    if (exp.Content is Grid eg && eg.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer scroller)
                        ApplyPreviewHorizontalScrollForWrapSection(scroller, wrap);
                    else if (exp.Content is ScrollViewer scroller2)
                        ApplyPreviewHorizontalScrollForWrapSection(scroller2, wrap);
                    if (!autoMaterialized)
                        ActivateSectionForBlock(b);
                    UpdateStickyHorizontalScrollBar();
                }
            }
            catch (Exception ex)
            {
                // A managed exception that escapes a XAML callback fail-fasts the
                // process via CoreMessagingXP. Catch + log so we survive.
                LogService.Instance.Warning("Preview",
                    $"Expander.Expanding handler threw: {ex.GetType().Name}: {ex.Message}");
                try { LogService.Instance.Flush(); } catch { }
            }
        };
        // Tooltip is set on the header grid only (BuildPreviewSectionHeader);
        // setting it on the Expander itself would also show when hovering the
        // content body, which is noisy.
        if (results is not null)
            RegisterSectionMatchTotal(block, CountContentMatchResults(results));
        _blockExpanderCache[block] = expander;
        _expanderFilePaths[expander] = filePath;
        _expanderHeaderArgs[expander] = (filePath, detail, block, results);
        ApplyPreviewSectionContentBackground(expander, ReferenceEquals(_activeSectionNav?.Block, block));
        if (addToPanel)
        {
            PreviewSectionsPanel.Children.Add(expander);
            InvalidateScrollPositionCache();
            _lastHighlightedActiveBlock = null;
            CompletePreviewContentUpdate();
        }
        return (block, expander);
    }

    private static int CountContentMatchResults(IEnumerable<SearchResult> results)
        => results.Count(result => result.LineNumber > 0);

    private Grid BuildPreviewSectionHeader(string filePath, string? detail, RichTextBlock? sectionBlock = null, List<SearchResult>? sectionResults = null)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };

        var infoPanel = new Grid
        {
            MinWidth = 0,
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };

        var fileIcon = new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(fileIcon, 0);
        infoPanel.Children.Add(fileIcon);

        var fileNameText = new TextBlock
        {
            Text = Path.GetFileName(filePath),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = ViewModel.PreviewStickyHeaderFileNameFontSize,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ViewModel.PreviewStickyHeaderFileNameFontFamily),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                ColorStringHelper.Parse(ViewModel.PreviewStickyHeaderFileNameFontColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinWidth = 0,
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(fileNameText, 1);
        infoPanel.Children.Add(fileNameText);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailText = new TextBlock
            {
                Text = detail,
                FontSize = ViewModel.PreviewStickyHeaderDetailFontSize,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ViewModel.PreviewStickyHeaderDetailFontFamily),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    ColorStringHelper.Parse(ViewModel.PreviewStickyHeaderDetailFontColor, Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF))),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(detailText, 2);
            infoPanel.Children.Add(detailText);
        }

        Grid.SetColumn(infoPanel, 0);
        grid.Children.Add(infoPanel);

        // Per-file action buttons — right-aligned near the Expander chevron.
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(buttonPanel, 1);
        Canvas.SetZIndex(buttonPanel, 1);

        var path = filePath; // capture for lambdas

        // Image content matches show a thumbnail + recognized OCR text and are not text-editable,
        // so the text-only actions ("Show full file", "Edit file") don't apply — render them
        // disabled and non-clickable, mirroring the single-file toolbar (FullFileButton.IsEnabled).
        bool isImageMatch = IsImagePreviewPath(filePath);

        if (sectionBlock is not null && sectionResults is not null)
        {
            var fullFileBtn = new Button
            {
                Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                Content = new FontIcon { Glyph = "\uE81E", FontSize = 12 },
                IsEnabled = !isImageMatch,
            };
            ToolTipService.SetToolTip(fullFileBtn, isImageMatch ? "Show full file (not available for images)" : "Show full file");
            var capturedBlock = sectionBlock;
            var capturedResults = sectionResults;
            fullFileBtn.Click += async (_, _) =>
            {
                fullFileBtn.IsEnabled = false;
                await ExpandSectionToFullFileAsync(capturedBlock, path, capturedResults);
            };
            buttonPanel.Children.Add(fullFileBtn);
        }

        var copyBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
        };
        ToolTipService.SetToolTip(copyBtn, "Copy full file path");
        copyBtn.Click += (_, _) => SetClipboardText(path, "section file path");
        buttonPanel.Children.Add(copyBtn);

        var openBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 12 },
        };
        ToolTipService.SetToolTip(openBtn, "Open with default application");
        openBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to open in default app: {path}", ex); }
        };
        buttonPanel.Children.Add(openBtn);

        var editorBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
            IsEnabled = !isImageMatch,
        };
        ToolTipService.SetToolTip(editorBtn, isImageMatch ? "Edit file (not available for images)" : "Edit file");
        editorBtn.Click += async (_, _) =>
        {
            var result = ResolvePreviewEditorFallbackResult(path);
            await ShowFullFileEditorAsync(result, scrollToMatch: false);
        };
        buttonPanel.Children.Add(editorBtn);

        // Open containing folder in Explorer
        var explorerBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uED25", FontSize = 12 },
        };
        ToolTipService.SetToolTip(explorerBtn, "Open containing folder in Explorer");
        explorerBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
            catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to show in Explorer: {path}", ex); }
        };
        buttonPanel.Children.Add(explorerBtn);

        // Export single-file report
        var reportBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE9F9", FontSize = 12 },
        };
        ToolTipService.SetToolTip(reportBtn, "Export report (HTML/JSON/CSV)");
        reportBtn.Click += async (_, _) => await ExportSingleFileHtmlReportAsync(path);
        buttonPanel.Children.Add(reportBtn);

        // Dismiss button — remove this file section from the preview
        var dismissBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
        };
        ToolTipService.SetToolTip(dismissBtn, "Remove from preview");
        if (sectionBlock is not null)
        {
            var capturedBlock = sectionBlock;
            var capturedPath = filePath;
            dismissBtn.Click += (_, _) => RemovePreviewSection(capturedBlock, capturedPath);
        }
        buttonPanel.Children.Add(dismissBtn);

        grid.Children.Add(buttonPanel);

        if (sectionBlock is not null)
        {
            var capturedBlock = sectionBlock;
            var capturedPath = filePath;
            var closeItem = new MenuFlyoutItem { Text = "Close" };
            closeItem.Click += (_, _) => RemovePreviewSection(capturedBlock, capturedPath);
            var flyout = new MenuFlyout();
            flyout.Items.Add(closeItem);
            grid.ContextFlyout = flyout;
        }

        ToolTipService.SetToolTip(grid, filePath);
        return grid;
    }

    private void RemovePreviewSection(RichTextBlock block, string filePath)
    {
        // Find and remove the Expander containing this block
        for (int i = PreviewSectionsPanel.Children.Count - 1; i >= 0; i--)
        {
            if (PreviewSectionsPanel.Children[i] is Expander expander
                && ((expander.Content is Grid g
                     && g.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv
                     && sv.Content is Border border
                     && border.Child == block)
                    || (expander.Content is ScrollViewer sv2
                        && sv2.Content is Border border2
                        && border2.Child == block)))
            {
                PreviewSectionsPanel.Children.RemoveAt(i);
                _blockExpanderCache.Remove(block);
                InvalidateParagraphIndexCache(block);
                _lastHighlightedActiveBlock = null;

                // Remove per-section match nav data
                _sectionMatchNavs.Remove(block);
                _sectionGutterBlocks.Remove(block);
                _expanderFilePaths.Remove(expander);
                _expanderHeaderArgs.Remove(expander);
                if (ReferenceEquals(_stickyHeaderExpander, expander))
                {
                    _stickyHeaderExpander = null;
                    StickyFileHeader.Child = null;
                    StickyFileHeader.Visibility = Visibility.Collapsed;
                }

                // Remove lazy section data if it was never rendered
                if (_lazySections.Remove(block, out var lazy))
                    _lazyMatchCount -= lazy.MatchCount;

                int sectionTotal = _sectionTotalMatchCounts.TryGetValue(block, out int registeredTotal)
                    ? registeredTotal
                    : 0;
                _sectionTotalMatchCounts.Remove(block);
                if (sectionTotal > 0)
                    SubtractPreviewMatchTotals(sectionTotal, files: 1);

                // Remove global matches for this block
                _matchParagraphs.RemoveAll(m => m.block == block);
                _currentMatchIndex = -1;
                UpdateMatchNavPanel();
                UpdateSectionMatchNavPanels();

                // Deselect and collapse the file group in the left panel
                var group = ViewModel.ResultGroups
                    .FirstOrDefault(g => string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (group is not null)
                {
                    group.DeselectAll();
                    group.IsExpanded = false;
                    group.ClearVisibleResults();
                }

                if (!PreviewSectionsPanel.Children.OfType<Expander>().Any())
                {
                    SetPreviewFileLabel(string.Empty);
                    PreviewToolbarContent.Visibility = Visibility.Collapsed;
                    _previewResult = null;
                    HideMatchNavPanel();
                    UpdateExpandAllButtonVisibility();
                }
                UpdatePreviewEmptyState();
                return;
            }
        }
    }

    private async Task ShowFullFilePreviewAsync(IReadOnlyList<FullFilePreviewTarget> targets)
    {
        LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: {targets.Count} targets");
        if (!TryLeavePreviewEditorForPreviewChange()) return;

        _previewLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;

        var tooltip = string.Join(Environment.NewLine, targets.Select(t => t.FilePath));
        SetPreviewFileLabel(
            targets.Count == 1 ? targets[0].FilePath : $"{targets.Count:N0} selected files",
            tooltip);
        ShowPreviewMessage(targets.Count == 1
            ? $"Loading full file preview for {Path.GetFileName(targets[0].FilePath)}..."
            : $"Loading full file preview for {targets.Count:N0} files...");

        try
        {
            Regex? rx = BuildSearchHighlightRegex();
            ShowPreviewSectionsSurface();
            SetPreviewMatchTotals(targets.Sum(t => ComputeMatchCount(t.Matches, null, isHighlight: true, previewLines: 0, rx)), targets.Count);

            int filesLoaded = 0;
            (RichTextBlock block, Paragraph para, int matchInPara)? firstMatch = null;
            (RichTextBlock block, Paragraph para, int matchInPara)? preferredMatch = null;
            foreach (var target in targets)
            {
                cts.Token.ThrowIfCancellationRequested();

                foreach (var result in target.Matches)
                    ViewModel.HydrateResult(result);

                try
                {
                    LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: loading file '{Path.GetFileName(target.FilePath)}', matches={target.Matches.Count}");
                    var document = await LoadPreviewDocumentAsync(target.FilePath, cts.Token, fileSizeLimit: EffectiveFullFilePreviewLimitBytes).ConfigureAwait(true);
                    LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: loaded '{Path.GetFileName(target.FilePath)}', bytes={document.ByteLength:N0}, textLen={document.Text.Length:N0}");
                    var section = AddFullFileSection(target, document.ByteLength);
                    _sectionMatchNavs.TryGetValue(section, out var sectionNav);
                    var renderedMatch = RenderFullFileDocument(section, target, document.Text, rx, _matchParagraphs, sectionNav, _previewResult);
                    firstMatch ??= renderedMatch.firstMatch;
                    preferredMatch ??= renderedMatch.preferredMatch;
                    filesLoaded++;
                }
                catch (OperationCanceledException) { throw; }
                catch (PreviewLoadException ex)
                {
                    var section = AddFullFileSection(target, byteLength: null);
                    AddFullFileError(section, ex.Message);
                }
                catch (OutOfMemoryException ex)
                {
                    const string message = "Not enough memory to load this full file into the right-panel preview.";
                    LogService.Instance.Warning("Preview", message, ex);
                    var section = AddFullFileSection(target, byteLength: null);
                    AddFullFileError(section, message);
                }
                catch (Exception ex)
                {
                    var message = $"Could not load full file: {ex.Message}";
                    LogService.Instance.Warning("Preview", $"Could not load full file: {target.FilePath}", ex);
                    var section = AddFullFileSection(target, byteLength: null);
                    AddFullFileError(section, message);
                }
            }

            _previewResult = targets[0].Matches[0];
            UpdateMatchNavPanel();
            UpdateSectionMatchNavPanels();

            var matchToReveal = preferredMatch ?? firstMatch;
            if (matchToReveal is { } match)
            {
                SetCurrentMatchToMatch(match.block, match.para, match.matchInPara);
                ScrollPreviewToLine(match.block, match.para);
            }

            ViewModel.StatusText = targets.Count == 1
                ? $"Loaded full file preview for {Path.GetFileName(targets[0].FilePath)}."
                : $"Loaded full file preview for {filesLoaded:N0}/{targets.Count:N0} selected files.";
        }
        catch (OperationCanceledException)
        {
            ShowPreviewMessage("Full-file preview cancelled.");
        }
        finally
        {
            if (ReferenceEquals(_previewLoadCts, cts))
                _previewLoadCts = null;
            cts.Dispose();
            FullFileButton.IsEnabled = true;
            UpdatePreviewEditorButtons();
        }
    }

    private RichTextBlock AddFullFileSection(FullFilePreviewTarget target, long? byteLength)
    {
        var detail = byteLength.HasValue ? FormatBytes(byteLength.Value) : null;
        var block = AddPreviewSection(target.FilePath, detail).block;
        RegisterSectionMatchTotal(block, CountContentMatchResults(target.Matches));
        return block;
    }

    private static void AddFullFileError(RichTextBlock section, string message)
    {
        var para = new Paragraph();
        var run = new Run { Text = message };
        run.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        para.Inlines.Add(run);
        section.Blocks.Add(para);
    }

    private (
        (RichTextBlock block, Paragraph para, int matchInPara)? firstMatch,
        (RichTextBlock block, Paragraph para, int matchInPara)? preferredMatch)
        RenderFullFileDocument(
            RichTextBlock section,
            FullFilePreviewTarget target,
            string text,
            Regex? rx,
            List<(RichTextBlock block, Paragraph para, int matchInPara)> matchParagraphs,
            SectionMatchNav? sectionNav,
            SearchResult? preferredResult)
    {
        var renderSw = Stopwatch.StartNew();
        var matchByLine = new Dictionary<int, SearchResult>();
        foreach (var result in target.Matches.OrderBy(r => r.LineNumber))
        {
            if (!matchByLine.ContainsKey(result.LineNumber))
                matchByLine[result.LineNumber] = result;
        }

        using var reader = new StringReader(text);
        string? line;
        int lineNumber = 1;
        bool wroteLine = false;
        (RichTextBlock block, Paragraph para, int matchInPara)? firstMatch = null;
        (RichTextBlock block, Paragraph para, int matchInPara)? preferredMatch = null;
        while ((line = reader.ReadLine()) is not null)
        {
            var isMatchLine = matchByLine.TryGetValue(lineNumber, out var matchResult);
            int beforeCount = matchParagraphs.Count;
            AddPreviewLineParagraphs(section, line, lineNumber, isMatchLine, matchResult ?? target.Matches[0], rx, truncate: false, matchParagraphs, sectionNav, out _);
            if (isMatchLine && matchParagraphs.Count > beforeCount)
            {
                var entry = matchParagraphs[beforeCount];
                firstMatch ??= entry;
                if (preferredResult is not null
                    && lineNumber == preferredResult.LineNumber
                    && string.Equals(target.FilePath, preferredResult.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    preferredMatch ??= entry;
                }
            }
            wroteLine = true;
            lineNumber++;
        }

        renderSw.Stop();
        LogService.Instance.Info("Preview", $"RenderFullFileDocument: file='{Path.GetFileName(target.FilePath)}', lines={lineNumber - 1}, matches={matchByLine.Count}, blocks={section.Blocks.Count}, elapsed={renderSw.ElapsedMilliseconds}ms");

        if (!wroteLine)
        {
            var para = new Paragraph();
            var run = new Run { Text = "(empty file)" };
            run.Foreground = s_contextTextBrush;
            para.Inlines.Add(run);
            section.Blocks.Add(para);
        }

        return (firstMatch, preferredMatch);
    }

    private async void OnPreviewBlockDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (_previewMutating) return;
        // Skip if the custom selection pointer handler already opened the editor
        // for this double-click (native DoubleTapped is normally suppressed, but
        // guard against both firing).
        if (Environment.TickCount64 - _previewEditorPointerOpenTick < PreviewEditorPointerOpenGuardMs) return;
        if (sender is RichTextBlock rtb)
            await EnterPreviewEditorAtPointAsync(rtb, e, _previewResult?.FilePath);
    }

    // EnterPreviewEditorAtPointAsync, ResolveLineNumberAtPointer, ShowFullFileEditorAsync,
    // and ScrollEditorToMatch moved to MainWindow.PreviewEditor.cs.

    private static async Task<PreviewTextDocument> LoadPreviewDocumentAsync(string filePath, CancellationToken cancellationToken, bool enforceLimit = true, long fileSizeLimit = 0)
    {
        long effectiveLimit = fileSizeLimit > 0 ? fileSizeLimit : 1L * 1024 * 1024 * 1024;
        var loadSw = Stopwatch.StartNew();
        LogService.Instance.Verbose("Preview", $"LoadPreviewDocumentAsync: start file='{Path.GetFileName(filePath)}'");

        // Handle archive entry paths: extract the entry to a memory stream
        if (ZipArchiveSearcher.IsArchivePath(filePath))
        {
            LogService.Instance.Verbose("Preview", $"LoadPreviewDocumentAsync: archive path, delegating to LoadArchiveEntryPreviewAsync");
            return await LoadArchiveEntryPreviewAsync(filePath, cancellationToken, enforceLimit, effectiveLimit).ConfigureAwait(false);
        }

        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch (Exception ex) { throw new PreviewLoadException($"Could not inspect full file: {ex.Message}"); }

        if (!info.Exists)
            throw new PreviewLoadException("Could not load full file: it no longer exists.");
        if (enforceLimit && info.Length > effectiveLimit)
            throw new PreviewLoadException($"Full-file preview is limited to {FormatBytes(effectiveLimit)}. This file is {FormatBytes(info.Length)}.");

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int probeSize = (int)Math.Min(BinaryDetector.SampleBytes, Math.Max(0, info.Length));
            if (probeSize > 0)
            {
                var probe = new byte[probeSize];
                int read = await stream.ReadAsync(probe.AsMemory(0, probe.Length), cancellationToken).ConfigureAwait(false);
                if (BinaryDetector.IsBinary(probe.AsSpan(0, read)))
                    throw new PreviewLoadException("Full-file editing is only available for non-binary text files.");
            }

            stream.Position = 0;
            var encoding = EncodingDetector.DetectEncoding(stream);
            // Use a replacement fallback so non-UTF-8 files render with '�' instead of throwing.
            if (encoding is UTF8Encoding)
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            int maxLineLength = GetMaxLineLength(text);
            loadSw.Stop();
            LogService.Instance.Info("Preview", $"LoadPreviewDocumentAsync: done file='{Path.GetFileName(filePath)}', bytes={info.Length:N0}, textLen={text.Length:N0}, maxLineLen={maxLineLength:N0}, elapsed={loadSw.ElapsedMilliseconds}ms");
            return new PreviewTextDocument(text, reader.CurrentEncoding, info.Length, maxLineLength);
        }
        catch (PreviewLoadException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new PreviewLoadException($"Could not load full file: access denied. {ex.Message}"); }
        catch (DecoderFallbackException ex) { throw new PreviewLoadException($"Could not load full file: unsupported text encoding. {ex.Message}"); }
        catch (IOException ex) { throw new PreviewLoadException($"Could not load full file: {ex.Message}"); }
    }

    private static async Task<PreviewTextDocument> LoadArchiveEntryPreviewAsync(string archivePath, CancellationToken cancellationToken, bool enforceLimit = true, long fileSizeLimit = 0)
    {
        long effectiveLimit = fileSizeLimit > 0 ? fileSizeLimit : 1L * 1024 * 1024 * 1024;
        try
        {
            using var ms = await ZipArchiveSearcher.ExtractToMemoryAsync(archivePath, cancellationToken).ConfigureAwait(false);
            long byteLength = ms.Length;

            if (enforceLimit && byteLength > effectiveLimit)
                throw new PreviewLoadException($"Full-file preview is limited to {FormatBytes(effectiveLimit)}. This entry is {FormatBytes(byteLength)}.");

            int probeSize = (int)Math.Min(BinaryDetector.SampleBytes, Math.Max(0, byteLength));
            if (probeSize > 0)
            {
                var probe = new byte[probeSize];
                int read = await ms.ReadAsync(probe.AsMemory(0, probeSize), cancellationToken).ConfigureAwait(false);
                if (BinaryDetector.IsBinary(probe.AsSpan(0, read)))
                    throw new PreviewLoadException("Full-file editing is only available for non-binary text files.");
                ms.Position = 0;
            }

            var encoding = EncodingDetector.DetectEncoding(ms);
            if (encoding is UTF8Encoding)
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            ms.Position = 0;
            using var reader = new StreamReader(ms, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return new PreviewTextDocument(text, reader.CurrentEncoding, byteLength, GetMaxLineLength(text));
        }
        catch (PreviewLoadException) { throw; }
        catch (FileNotFoundException ex) { throw new PreviewLoadException($"Could not find archive entry: {ex.Message}"); }
        catch (InvalidDataException ex) { throw new PreviewLoadException($"Could not read archive: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { throw new PreviewLoadException($"Access denied to archive: {ex.Message}"); }
        catch (IOException ex) { throw new PreviewLoadException($"Could not load archive entry: {ex.Message}"); }
    }

    // SavePreviewEditAsync, ConfirmDiscardPreviewEditAsync, TryLeavePreviewEditorForPreviewChange,
    // ClosePreviewEditor, SetPreviewEditorVisible, HasRealEditorChanges,
    // UpdatePreviewEditorButtons, UpdateEditorDirtyIndicator moved to MainWindow.PreviewEditor.cs.

    private void ShowPreviewMessage(string message, bool showBackButton = false)
    {
        SetPreviewEditorVisible(false);
        if (showBackButton)
        {
            // Preserve existing preview content (sections panel) so the back
            // button can restore it.  Only hide — don't clear.
            PreviewSectionsPanel.Visibility = Visibility.Collapsed;
            PreviewBlock.Visibility = Visibility.Visible;
            HidePreviewLoading();
            SetPerFileToolbarVisibility(Visibility.Visible);
        }
        else
        {
            ShowPreviewBlockSurface();
        }
        PreviewBlock.Blocks.Clear();
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        var para = new Paragraph();
        para.Inlines.Add(new Run { Text = message });
        PreviewBlock.Blocks.Add(para);
        PreviewBackButton.Visibility = showBackButton ? Visibility.Visible : Visibility.Collapsed;
        CompletePreviewContentUpdate();
    }

    private void OnPreviewBackClick(object sender, RoutedEventArgs e)
    {
        PreviewBackButton.Visibility = Visibility.Collapsed;
        RestorePreviewSurfaceAfterEditor();
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : bytes >= mb ? $"{bytes / mb:F1} MB" : bytes >= kb ? $"{bytes / kb:F1} KB" : $"{bytes} B";
    }

    private static int GetMaxLineLength(string text)
    {
        int max = 0;
        int current = 0;
        foreach (char c in text)
        {
            if (c is '\r' or '\n')
            {
                if (current > max) max = current;
                current = 0;
            }
            else
            {
                current++;
            }
        }

        return current > max ? current : max;
    }

    private sealed record PreviewTextDocument(string Text, Encoding Encoding, long ByteLength, int MaxLineLength);

    private sealed record PreviewEditorChunk(string Text, Encoding Encoding, long TotalByteLength, long NextByteOffset, int MaxLineLength);

    private sealed record FullFilePreviewTarget(string FilePath, List<SearchResult> Matches);

    private sealed class PreviewLoadException(string message) : Exception(message);

    private static string GetEncodingDisplayName(Encoding enc)
    {
        var name = enc.WebName.ToUpperInvariant();
        if (name == "UTF-8" && enc.GetPreamble().Length > 0) return "UTF-8 BOM";
        if (name == "UTF-16") return "UTF-16 LE";
        return name;
    }

    /// <summary>
    /// Returns true if <paramref name="text"/> contains characters that cannot
    /// be losslessly encoded with <paramref name="encoding"/> (e.g. lone surrogates).
    /// </summary>
    private static bool TextHasUnencodableCharacters(string text, Encoding encoding)
    {
        try
        {
            var strict = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            strict.GetByteCount(text);
            return false;
        }
        catch (EncoderFallbackException)
        {
            return true;
        }
    }

    private static Regex? BuildHighlightRegex(string query, bool caseSensitive, bool useRegex, bool exactMatch = true, bool multiline = false, bool multilineDotAll = false)
    {
        if (string.IsNullOrEmpty(query)) return null;
        try
        {
            string? pattern = useRegex ? query : SearchQueryParser.BuildLiteralRegexPattern(query, exactMatch);
            if (string.IsNullOrEmpty(pattern)) return null;
            // Route through the shared factory so search, replace, and highlight can never disagree
            // on flags (multiline anchors, dot-all, and the multiline-only match timeout).
            return SearchRegexFactory.Build(pattern, caseSensitive, multiline, multilineDotAll);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", "Invalid highlight regex", ex);
            return null;
        }
    }

    /// <summary>
    /// Builds the preview/editor highlight regex from the parameters the most recent SEARCH actually
    /// ran with (<see cref="MainViewModel.LastSearchPattern"/> &amp; flags), NOT the live
    /// <c>ViewModel.Query</c>/flags. A semantic search keeps the natural-language text in the Query box
    /// while the engine matched the model's resolved literal pattern, and it restores the user's
    /// default flags afterward — so highlighting off the live values boxes nothing. Falls back to the
    /// live Query/flags only before any search has run (LastSearchPattern still empty).
    /// </summary>
    private Regex? BuildSearchHighlightRegex()
        => string.IsNullOrEmpty(ViewModel.LastSearchPattern)
            ? BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch, ViewModel.Multiline, ViewModel.MultilineDotAll)
            : BuildHighlightRegex(ViewModel.LastSearchPattern, ViewModel.LastSearchCaseSensitive, ViewModel.LastSearchUseRegex, ViewModel.LastSearchExactMatch, ViewModel.LastSearchMultiline, ViewModel.LastSearchMultilineDotAll);

    private SolidColorBrush s_matchGutterBrush = new(Windows.UI.Color.FromArgb(255, 156, 220, 254));

    /// <summary>
    /// Returns the number of individual regex match occurrences on a line (minimum 1 for a match line).
    /// </summary>
    private static int CountRegexMatches(string? line, Regex? rx)
    {
        return CountRegexMatches(line, rx, minimumOne: true);
    }

    private static int CountRegexMatches(string? line, Regex? rx, bool minimumOne)
    {
        if (rx is null || string.IsNullOrEmpty(line)) return minimumOne ? 1 : 0;
        int count = rx.Count(line);
        return count > 0 || !minimumOne ? count : 1;
    }

    private static void AddMatchEntries(
        List<(RichTextBlock block, Paragraph para, int matchInPara)> matchParagraphs,
        SectionMatchNav? sn,
        RichTextBlock section, Paragraph para,
        string? line, Regex? rx,
        bool minimumOne = true)
    {
        int count = CountRegexMatches(line, rx, minimumOne);
        for (int i = 0; i < count; i++)
        {
            matchParagraphs.Add((section, para, i));
            sn?.Matches.Add((para, i));
        }
    }

    private SolidColorBrush s_contextGutterBrush = new(Windows.UI.Color.FromArgb(255, 156, 220, 254));
    private static SolidColorBrush s_gutterSepBrush = new(Windows.UI.Color.FromArgb(255, 156, 220, 254));
    private SolidColorBrush s_contextTextBrush = new(Windows.UI.Color.FromArgb(255, 110, 110, 110));
    private SolidColorBrush s_matchAccentBrush = new(Windows.UI.Color.FromArgb(255, 70, 140, 70));
    private static readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);
    private SolidColorBrush _matchTextBrush = new(Microsoft.UI.Colors.Gold);
    private SolidColorBrush _resultMatchTextBrush = new(Microsoft.UI.Colors.Gold);
    private SolidColorBrush _matchLineBrush = new(Microsoft.UI.Colors.White);
    private Windows.UI.Color _overlayColor = Microsoft.UI.Colors.OrangeRed;
    private static readonly ConditionalWeakTable<Paragraph, object> s_paragraphLineNumbers = new();
    private static readonly ConditionalWeakTable<Paragraph, object> s_paragraphPrimaryResults = new();
    /// <summary>Marks paragraphs that are continuation segments of a long source line (no leading line number gutter).</summary>
    private static readonly ConditionalWeakTable<Paragraph, object> s_paragraphIsContinuation = new();
    /// <summary>
    /// Records the character length of the INLINE gutter prefix (indicator + centered line
    /// number + separator) prepended to single-file <c>PreviewBlock</c> paragraphs, so the
    /// drag-selection overlay can clamp its highlight bands to the content column and never
    /// paint over the gutter. Absent for section content blocks (their gutter is a separate
    /// block, so their content already starts at the block's left edge).
    /// </summary>
    private static readonly ConditionalWeakTable<Paragraph, object> s_paragraphInlineGutterLength = new();

    /// <summary>Returns the inline gutter character length recorded for <paramref name="paragraph"/>, if any.</summary>
    private static bool TryGetParagraphInlineGutterLength(Paragraph paragraph, out int gutterCharLength)
    {
        if (s_paragraphInlineGutterLength.TryGetValue(paragraph, out var value) && value is int length)
        {
            gutterCharLength = length;
            return true;
        }
        gutterCharLength = 0;
        return false;
    }
    /// <summary>
    /// Records the source-line column window [SourceStart, SourceEnd) rendered for a
    /// truncated match-line paragraph. Lets same-line occurrence clicks decide whether a
    /// clicked occurrence falls inside an already-rendered window (reuse it) or lies
    /// outside every rendered window (append a dedicated window for it). Absent entry =
    /// the full untruncated line was rendered, which contains every occurrence.
    /// </summary>
    private static readonly ConditionalWeakTable<Paragraph, object> s_paragraphMatchWindows = new();
    private static readonly ConditionalWeakTable<Run, object> s_previewSearchMatchRuns = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> s_previewShowMoreActions = new();
    private const int PreviewShowMoreTooltipHideDelayMs = 700;
    // How far below the top edge of the "Show more" button the cursor should land so the button is
    // directly layered underneath the pointer (the pointer overlaps the button rather than sitting above it).
    private const double PreviewShowMoreTooltipCursorOverlapDip = 6;
    private PreviewShowMoreAction? _previewShowMoreTooltipAction;
    private PreviewShowMoreEdge? _previewShowMoreTooltipEdge;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewShowMoreTooltipHideTimer;
    private bool _previewShowMoreTooltipHandlersAttached;
    private bool _previewShowMorePointerOverMarker;
    private bool _previewShowMorePointerOverPanel;
    private int _previewShowMoreWindowCount;
    private int _previewShowMoreMarkerCount;
    private int _previewShowMorePrefixMarkerCount;
    private int _previewShowMoreSuffixMarkerCount;
    private int _previewShowMoreCappedWindowCount;
    private string? _previewShowMoreSample;

    private void ResetPreviewShowMoreDiagnostics()
    {
        _previewShowMoreWindowCount = 0;
        _previewShowMoreMarkerCount = 0;
        _previewShowMorePrefixMarkerCount = 0;
        _previewShowMoreSuffixMarkerCount = 0;
        _previewShowMoreCappedWindowCount = 0;
        _previewShowMoreSample = null;
    }

    private void LogPreviewShowMoreDiagnostics(string scope)
    {
        if (!LogService.Instance.IsVerboseEnabled && _previewShowMoreMarkerCount == 0)
            return;

        LogService.Instance.Info(
            "PreviewShowMore",
            $"{scope}: windows={_previewShowMoreWindowCount}, markers={_previewShowMoreMarkerCount}, prefix={_previewShowMorePrefixMarkerCount}, suffix={_previewShowMoreSuffixMarkerCount}, capped={_previewShowMoreCappedWindowCount}, configuredTrunc={LineTruncator.TruncatedLength}, effectiveTrunc={GetPreviewTruncatedLength()}, segment={GetEffectiveSegmentSize()}, wrapMode={ViewModel.PreviewWrapModeIndex}, sample={_previewShowMoreSample ?? "none"}");
    }

    private void ApplyPreviewColors()
    {
        var vm = ViewModel;
        var previewGutterColor = ColorStringHelper.Parse(vm.PreviewGutterContextColor, Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        s_contextGutterBrush.Color = previewGutterColor;
        s_gutterSepBrush.Color = previewGutterColor;
        s_matchGutterBrush.Color = ColorStringHelper.Parse(vm.PreviewGutterMatchColor, Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
        PreviewEditor.LineNumberColor = ColorStringHelper.Parse(vm.PreviewEditorGutterColor, Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6));
        PreviewEditor.TextColor = ResolveEffectiveEditorTextColor();
        var matchTextColor = ColorStringHelper.Parse(vm.PreviewMatchTextColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        _matchTextBrush = new SolidColorBrush(matchTextColor);
        _overlayColor = ColorStringHelper.Parse(vm.PreviewOverlayColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x00));
        _matchLineBrush = new SolidColorBrush(ColorStringHelper.Parse(vm.PreviewMatchLineColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
        _previewShowMoreEllipsisBrush.Color = ColorStringHelper.Parse(vm.PreviewShowMoreEllipsisColor, Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
        s_contextTextBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 110, 110, 110));
        s_matchAccentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 140, 70));

        // Update overlay elements
        ActiveMatchBand.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, _overlayColor.R, _overlayColor.G, _overlayColor.B));
        ActiveMatchBand.BorderBrush = new SolidColorBrush(_overlayColor);
        ActiveMatchWordMarker.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, _overlayColor.R, _overlayColor.G, _overlayColor.B));
        ActiveMatchWordMarker.BorderBrush = new SolidColorBrush(_overlayColor);
    }

    // Resolves the color applied to the built-in editor's body text. An empty PreviewEditorTextColor is the
    // "Auto" sentinel that follows the app/system theme (the editor renders on a theme-colored card), so a
    // single fixed default would be unreadable in one theme. Auto resolves to the theme-appropriate default.
    private Windows.UI.Color ResolveEffectiveEditorTextColor()
    {
        string configured = ViewModel.PreviewEditorTextColor;
        if (!string.IsNullOrWhiteSpace(configured))
            return ColorStringHelper.Parse(configured, GetThemeDefaultEditorTextColor());

        return GetThemeDefaultEditorTextColor();
    }

    private Windows.UI.Color GetThemeDefaultEditorTextColor()
    {
        var effectiveTheme = AppThemeService.ResolveEffectiveTheme(RootGrid, ViewModel.ThemeModeIndex);
        return effectiveTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(0xFF, 0x32, 0x32, 0x32) // near-black for light theme cards
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); // white for dark theme cards
    }

    // Re-applies only the editor body-text color (used on theme changes so the Auto sentinel tracks the
    // active light/dark theme without rebuilding every preview brush).
    private void ApplyPreviewEditorTextColor()
    {
        PreviewEditor.TextColor = ResolveEffectiveEditorTextColor();
    }

    private string ResolvePreviewTextFontFamily()
        => string.IsNullOrWhiteSpace(ViewModel.PreviewTextFontFamily)
            ? AppSettings.DefaultPreviewTextFontFamily
            : ViewModel.PreviewTextFontFamily.Trim();

    private int ResolvePreviewTextFontSize()
        => Math.Clamp(
            ViewModel.PreviewTextFontSize <= 0 ? AppSettings.DefaultPreviewTextFontSize : ViewModel.PreviewTextFontSize,
            6,
            72);

    private int ResolvePreviewShowMoreEllipsisFontSize()
        => Math.Clamp(
            ViewModel.PreviewShowMoreEllipsisFontSize <= 0 ? AppSettings.DefaultPreviewShowMoreEllipsisFontSize : ViewModel.PreviewShowMoreEllipsisFontSize,
            6,
            72);

    private static double ResolvePreviewTextLineHeight(int fontSize)
        => Math.Ceiling(Math.Clamp(fontSize, 6, 72) * 1.4);

    private void ApplyPreviewTextFontSettings()
    {
        string family = ResolvePreviewTextFontFamily();
        int size = ResolvePreviewTextFontSize();
        double lineHeight = ResolvePreviewTextLineHeight(size);

        foreach (var block in EnumeratePreviewSectionBlocks())
        {
            ApplyPreviewTextFontSettings(block, family, size, lineHeight);
            if (_sectionGutterBlocks.TryGetValue(block, out var gutterBlock))
                ApplyPreviewTextFontSettings(gutterBlock, family, size, lineHeight);

            InvalidateParagraphIndexCache(block);
            ScheduleGutterSync(block);
        }

        // The single-file block surface (PreviewBlock) uses an inline gutter, so it has
        // no separate gutter block — it still needs the same font/size to honor the
        // setting and to reflow live when the preview is pinch/Ctrl-wheel zoomed.
        if (PreviewBlock is not null)
        {
            ApplyPreviewTextFontSettings(PreviewBlock, family, size, lineHeight);
            InvalidateParagraphIndexCache(PreviewBlock);
        }

        QueueActiveMatchOverlayRefresh();
    }

    private static void ApplyPreviewTextFontSettings(RichTextBlock block, string family, int size, double lineHeight)
    {
        block.FontFamily = new FontFamily(family);
        block.FontSize = size;
        block.LineHeight = lineHeight;
    }

    private void ApplyPreviewShowMoreEllipsisFontSize()
    {
        int size = ResolvePreviewShowMoreEllipsisFontSize();
        foreach (var block in EnumeratePreviewShowMoreHostBlocks())
        {
            foreach (var paragraph in block.Blocks.OfType<Paragraph>())
            {
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline is InlineUIContainer { Child: Border { Child: TextBlock marker } }
                        && s_previewShowMoreActions.TryGetValue(marker, out _))
                    {
                        marker.FontSize = size;
                    }
                }
            }
        }
    }

    private IEnumerable<RichTextBlock> EnumeratePreviewShowMoreHostBlocks()
    {
        foreach (var block in EnumeratePreviewSectionBlocks())
            yield return block;
        if (PreviewBlock is not null)
            yield return PreviewBlock;
    }

    /// <summary>
    /// Computes the half-open source-column range to highlight on <paramref name="lineNum"/> when it
    /// falls inside <paramref name="result"/>'s cross-line (multiline) span, or null when the result
    /// is single-line, the line is outside the span, or the range is empty. Drives full-span
    /// (Phase 3) multiline highlighting where a per-line regex can never match a cross-line pattern.
    /// </summary>
    private static (int start, int end)? ComputeMultilineLineSpan(SearchResult result, int lineNum, int lineLength)
    {
        if (!result.IsMultilineMatch || result.MatchEndLineNumber is not int endLine)
            return null;
        int startCol = result.SourceMatchStartColumn >= 0 ? result.SourceMatchStartColumn : result.MatchStartColumn;
        int endCol = result.MatchEndColumn ?? lineLength;
        return MultilinePreviewSpan.TryGetLineSpan(
            lineNum, lineLength, result.LineNumber, startCol, endLine, endCol, out int s, out int e)
            ? (s, e)
            : null;
    }

    private Paragraph AddPreviewLineParagraphs(
        RichTextBlock section,
        string? line,
        int lineNum,
        bool isMatchLine,
        SearchResult result,
        Regex? rx,
        bool truncate,
        List<(RichTextBlock block, Paragraph para, int matchInPara)>? matchParagraphs,
        SectionMatchNav? sectionNav,
        out int paragraphsAdded,
        int? maxParagraphs = null,
        (int start, int end)? forcedSpan = null)
    {
        line ??= string.Empty;
        var window = truncate
            ? (isMatchLine ? TruncatePreviewLineAroundResult(line, result, rx) : TruncatePreviewLineWindow(line, rx))
            : CreatePreviewLineWindow(line, 0, line.Length);
        var expansionState = CreatePreviewTruncatedLineState(window, line, lineNum, isMatchLine, result, rx);

        // A forced multiline span is expressed in full-line columns and only maps cleanly when the
        // whole line renders as ONE layout segment (no 4096-char splitting shifting the offsets) and
        // untruncated (so display col == source col). Otherwise skip forced coloring — the line still
        // renders, just without the cross-line highlight, rather than mis-coloring a wrong column.
        bool singleSegment = window.Text.Length <= GetEffectiveSegmentSize();
        (int start, int end)? effectiveForcedSpan = (singleSegment && window.SourceStart == 0 && window.SourceEnd >= line.Length)
            ? forcedSpan
            : null;

        _sectionGutterBlocks.TryGetValue(section, out var gutterBlock);

        Paragraph? firstParagraph = null;
        bool addedMatchEntries = false;
        paragraphsAdded = 0;

        foreach (var segment in EnumeratePreviewLineLayoutSegments(window.Text))
        {
            if (maxParagraphs is int limit && paragraphsAdded >= limit)
                break;

            bool isContinuation = firstParagraph is not null;
            var para = MakePreviewParagraph(segment, lineNum, isMatchLine, result, rx, truncate: false, continuationGutter: isContinuation, gutterBlock: gutterBlock, truncationState: isContinuation ? null : expansionState, forcedSpan: isContinuation ? null : effectiveForcedSpan);
            section.Blocks.Add(para);
            firstParagraph ??= para;
            paragraphsAdded++;

            // Record the rendered source-column window for any truncated line, not
            // only match lines. A context line that is later promoted in place by
            // TryPromoteContextLineToMatch keeps this window so far-apart same-line
            // occurrences still append their own dedicated window instead of being
            // collapsed onto the promoted paragraph's nearest run.
            if (expansionState is not null)
                RecordParagraphMatchWindow(para, window.SourceStart, window.SourceEnd);

            if (!isMatchLine || matchParagraphs is null)
                continue;

            int beforeCount = matchParagraphs.Count;
            AddMatchEntries(
                matchParagraphs,
                sectionNav,
                section,
                para,
                segment,
                rx,
                minimumOne: rx is null && !addedMatchEntries);
            if (matchParagraphs.Count > beforeCount)
                addedMatchEntries = true;
        }

        if (isMatchLine && matchParagraphs is not null && !addedMatchEntries && firstParagraph is not null)
            AddMatchEntries(matchParagraphs, sectionNav, section, firstParagraph, string.Empty, rx: null);

        ScheduleGutterSync(section);

        return firstParagraph ?? throw new InvalidOperationException("Preview line renderer did not create a paragraph.");
    }

    private readonly record struct PreviewLineWindow(string Text, int SourceStart, int SourceEnd);

    private static void RecordParagraphMatchWindow(Paragraph para, int sourceStart, int sourceEnd)
        => s_paragraphMatchWindows.AddOrUpdate(para, ValueTuple.Create(sourceStart, sourceEnd));

    /// <summary>
    /// Gets the source-line column window rendered for a truncated match-line paragraph.
    /// Returns false when the paragraph rendered the full untruncated line (no window
    /// recorded), in which case it contains every occurrence on the line.
    /// </summary>
    private static bool TryGetParagraphMatchWindow(Paragraph para, out int sourceStart, out int sourceEnd)
    {
        if (s_paragraphMatchWindows.TryGetValue(para, out var boxed) && boxed is ValueTuple<int, int> window)
        {
            sourceStart = window.Item1;
            sourceEnd = window.Item2;
            return true;
        }

        sourceStart = 0;
        sourceEnd = 0;
        return false;
    }

    /// <summary>
    /// Returns true when the occurrence at <paramref name="sourceColumn"/> on
    /// <paramref name="lineNumber"/> already falls inside a match window rendered in
    /// <paramref name="section"/> (so a navigable match entry for it already exists). A
    /// rendered paragraph for the line with no recorded window means the full untruncated
    /// line was drawn, which contains every occurrence on the line. This is the fine-grained
    /// replacement for line-number-only dedup: a single very long source line (e.g. a one-line
    /// minified file) scatters its occurrences far outside the first truncated window, so each
    /// uncovered occurrence still needs its own dedicated window to be reachable by match nav.
    /// </summary>
    private static bool IsSourceColumnWithinRenderedWindow(RichTextBlock section, int lineNumber, int sourceColumn)
    {
        if (sourceColumn < 0)
            return false;

        foreach (var block in section.Blocks)
        {
            if (block is not Paragraph para
                || !s_paragraphLineNumbers.TryGetValue(para, out var lineObj)
                || lineObj is not int renderedLine
                || renderedLine != lineNumber)
            {
                continue;
            }

            if (!TryGetParagraphMatchWindow(para, out int windowStart, out int windowEnd))
                return true; // full untruncated line contains every occurrence

            if (sourceColumn >= windowStart && sourceColumn < windowEnd)
                return true;
        }

        return false;
    }

    private PreviewTruncatedLineState? CreatePreviewTruncatedLineState(
        PreviewLineWindow window,
        string sourceLine,
        int lineNumber,
        bool isMatchLine,
        SearchResult result,
        Regex? regex,
        bool colorTargetMatchOnly = false)
    {
        if (window.SourceStart <= 0 && window.SourceEnd >= sourceLine.Length)
            return null;

        _previewShowMoreWindowCount++;
        if (LineTruncator.TruncatedLength > GetPreviewTruncatedLength())
            _previewShowMoreCappedWindowCount++;

        _previewShowMoreSample ??= $"line={lineNumber}, textLen={sourceLine.Length}, displayLen={window.Text.Length}, sourceStart={window.SourceStart}, sourceEnd={window.SourceEnd}, hiddenPrefix={window.SourceStart}, hiddenSuffix={sourceLine.Length - window.SourceEnd}";

        return new PreviewTruncatedLineState(
            sourceLine,
            window.SourceStart,
            window.SourceEnd,
            lineNumber,
            isMatchLine,
            result,
            regex)
        {
            ColorTargetMatchOnly = colorTargetMatchOnly,
        };
    }

    private static PreviewLineWindow CreatePreviewLineWindow(string sourceLine, int sourceStart, int sourceEnd)
    {
        sourceLine ??= string.Empty;
        sourceStart = Math.Clamp(sourceStart, 0, sourceLine.Length);
        sourceEnd = Math.Clamp(sourceEnd, sourceStart, sourceLine.Length);

        string prefix = sourceStart > 0 ? LineTruncator.Ellipsis : string.Empty;
        string suffix = sourceEnd < sourceLine.Length ? LineTruncator.Ellipsis : string.Empty;
        string text = string.Concat(prefix, sourceLine.AsSpan(sourceStart, sourceEnd - sourceStart), suffix);
        return new PreviewLineWindow(text, sourceStart, sourceEnd);
    }

    private PreviewLineWindow TruncatePreviewLineWindow(string? line, Regex? rx)
    {
        line ??= string.Empty;
        int truncatedLength = GetPreviewTruncatedLength();
        if (truncatedLength == 0 || line.Length <= truncatedLength * 2)
            return CreatePreviewLineWindow(line, 0, line.Length);

        if (rx is not null)
        {
            var match = rx.Match(line);
            if (match.Success && match.Length > 0)
                return TruncatePreviewLineAroundSourceMatch(line, match.Index, match.Length);
        }

        return CreatePreviewLineWindow(line, 0, Math.Min(line.Length, truncatedLength));
    }

    private PreviewLineWindow TruncatePreviewLineAroundResult(string? line, SearchResult result, Regex? rx)
    {
        line ??= string.Empty;
        int matchStart = ResolveSourceMatchStart(line, result, rx);
        int matchLength = result.MatchLength;
        if (matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
        {
            var match = rx?.Match(line);
            if (match is { Success: true, Length: > 0 })
            {
                matchStart = match.Index;
                matchLength = match.Length;
            }
        }

        int truncatedLength = GetPreviewTruncatedLength();
        if (truncatedLength == 0
            || line.Length <= truncatedLength * 2
            || matchStart < 0
            || matchLength <= 0
            || matchStart >= line.Length)
        {
            return TruncatePreviewLineWindow(line, rx: null);
        }

        return TruncatePreviewLineAroundSourceMatch(line, matchStart, matchLength);
    }

    private PreviewLineWindow TruncatePreviewLineAroundSourceMatch(string line, int matchStart, int matchLength)
    {
        int truncatedLength = GetPreviewTruncatedLength();
        if (truncatedLength == 0)
            return CreatePreviewLineWindow(line, 0, line.Length);

        int safeMatchLength = Math.Min(matchLength, line.Length - matchStart);
        int visibleMatchLength = Math.Min(safeMatchLength, truncatedLength);
        int contextChars = Math.Max(0, (truncatedLength - visibleMatchLength) / 2);

        int start = Math.Max(0, matchStart - contextChars);
        int end = Math.Min(line.Length, start + truncatedLength);
        if (end - start < truncatedLength)
            start = Math.Max(0, end - truncatedLength);

        return CreatePreviewLineWindow(line, start, end);
    }

    private static int ResolveSourceMatchStart(string line, SearchResult result, Regex? rx)
    {
        int candidate = result.SourceMatchStartColumn;
        if (IsSourceMatchAt(line, candidate, result.MatchLength, rx))
            return candidate;

        candidate = result.MatchStartColumn;
        if (candidate != result.SourceMatchStartColumn
            && IsSourceMatchAt(line, candidate, result.MatchLength, rx))
            return candidate;

        string displayLine = result.MatchLine ?? string.Empty;
        if (displayLine.Length > 0)
        {
            bool hasPrefix = displayLine.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal);
            bool hasSuffix = displayLine.EndsWith(LineTruncator.Ellipsis, StringComparison.Ordinal);
            int prefixLength = hasPrefix ? LineTruncator.Ellipsis.Length : 0;
            int suffixLength = hasSuffix ? LineTruncator.Ellipsis.Length : 0;
            int coreLength = displayLine.Length - prefixLength - suffixLength;
            if (coreLength > 0)
            {
                string core = displayLine.Substring(prefixLength, coreLength);
                int coreIndex = line.IndexOf(core, StringComparison.Ordinal);
                if (coreIndex >= 0)
                {
                    int offsetInCore = Math.Clamp(candidate - prefixLength, 0, coreLength);
                    int resolved = coreIndex + offsetInCore;
                    if (IsSourceMatchAt(line, resolved, result.MatchLength, rx))
                        return resolved;
                }
            }
        }

        var match = rx?.Match(line);
        if (match is { Success: true, Length: > 0 })
            return match.Index;

        return candidate;
    }

    private static bool IsSourceMatchAt(string line, int start, int length, Regex? rx)
    {
        if (start < 0 || start >= line.Length || length <= 0)
            return false;

        if (rx is null)
            return start + length <= line.Length;

        var match = rx.Match(line, start);
        return match.Success && match.Index == start;
    }

    private Paragraph AddPreviewLineParagraphsAroundResult(
        RichTextBlock section,
        string? line,
        int lineNum,
        SearchResult result,
        Regex? rx,
        List<(RichTextBlock block, Paragraph para, int matchInPara)>? matchParagraphs,
        SectionMatchNav? sectionNav,
        out int paragraphsAdded,
        out int matchEntriesAdded,
        bool truncate = true,
        bool continuationGutter = false,
        bool targetOnlyMatchEntry = false,
        int? maxParagraphs = null)
    {
        line ??= string.Empty;
        var window = truncate
            ? TruncatePreviewLineAroundResult(line, result, rx)
            : new PreviewLineWindow(line, 0, line.Length);
        // Dense same-line occurrences each render their own match-centered window and are
        // stacked beside the existing physical-line window. Two neighboring windows whose
        // source-column ranges overlap would otherwise draw the shared text in both blocks
        // (the visible duplicated-content artifact). Clip this window so it abuts—but never
        // overlaps—already-rendered sibling windows for the same line, keeping the match
        // itself fully visible.
        if (truncate)
            window = ClipMatchWindowToRenderedSiblings(section, lineNum, line, result, rx, window);
        var expansionState = CreatePreviewTruncatedLineState(window, line, lineNum, isMatchLine: true, result, rx, colorTargetMatchOnly: targetOnlyMatchEntry);
        _sectionGutterBlocks.TryGetValue(section, out var gutterBlock);
        Paragraph? firstParagraph = null;
        bool addedMatchEntries = false;
        paragraphsAdded = 0;
        matchEntriesAdded = 0;
        int segmentDisplayStart = 0;

        foreach (var segment in EnumeratePreviewLineLayoutSegments(window.Text))
        {
            if (maxParagraphs is int limit && paragraphsAdded >= limit)
                break;

            // continuationGutter renders the "N+" gutter for dense same-line match
            // windows; it must NOT suppress the blue show-more ellipsis markers.
            // Only a real continuation segment (the 2nd+ layout segment of THIS
            // window) should drop the markers, so the first segment of every
            // windowed occurrence — including same-line continuation windows —
            // keeps its clickable prefix/suffix ellipsis.
            bool isContinuationSegment = firstParagraph is not null;
            bool useContinuationGutter = isContinuationSegment || continuationGutter;
            HashSet<int>? matchOrdinalsToColor = null;
            int? targetMatchOrdinal = null;
            if (targetOnlyMatchEntry)
            {
                matchOrdinalsToColor = [];
                if (TryGetTargetMatchOrdinalInSegment(line, result, rx, window, segment, segmentDisplayStart, out int colorOrdinal))
                {
                    targetMatchOrdinal = colorOrdinal;
                    matchOrdinalsToColor.Add(colorOrdinal);
                }
            }

            var para = MakePreviewParagraph(segment, lineNum, isMatchLine: true, result, rx, truncate: false, continuationGutter: useContinuationGutter, gutterBlock: gutterBlock, truncationState: isContinuationSegment ? null : expansionState, matchOrdinalsToColor: matchOrdinalsToColor);
            section.Blocks.Add(para);
            firstParagraph ??= para;
            paragraphsAdded++;

            if (expansionState is not null)
                RecordParagraphMatchWindow(para, window.SourceStart, window.SourceEnd);

            if (matchParagraphs is null)
            {
                segmentDisplayStart += segment.Length;
                continue;
            }

            int beforeCount = matchParagraphs.Count;
            if (targetOnlyMatchEntry && targetMatchOrdinal is int matchOrdinal)
            {
                matchParagraphs.Add((section, para, matchOrdinal));
                sectionNav?.Matches.Add((para, matchOrdinal));
            }
            else if (!targetOnlyMatchEntry)
            {
                AddMatchEntries(
                    matchParagraphs,
                    sectionNav,
                    section,
                    para,
                    segment,
                    rx,
                    minimumOne: rx is null && !addedMatchEntries);
            }
            int added = matchParagraphs.Count - beforeCount;
            if (added > 0)
            {
                addedMatchEntries = true;
                matchEntriesAdded += added;
            }

            segmentDisplayStart += segment.Length;
        }

        if (matchParagraphs is not null && !addedMatchEntries && firstParagraph is not null)
        {
            int beforeCount = matchParagraphs.Count;
            AddMatchEntries(matchParagraphs, sectionNav, section, firstParagraph, window.Text, targetOnlyMatchEntry ? null : rx, minimumOne: true);
            matchEntriesAdded += matchParagraphs.Count - beforeCount;
        }

        ScheduleGutterSync(section);

        return firstParagraph ?? throw new InvalidOperationException("Preview line renderer did not create a paragraph.");
    }

    /// <summary>
    /// Clips <paramref name="window"/> so it does not overlap any already-rendered sibling
    /// window for the same physical line in <paramref name="section"/>. Dense same-line
    /// matches each render an independent match-centered window; when two neighbors'
    /// source-column ranges overlap, the shared text is drawn in both blocks (the
    /// duplicated-content artifact). Clipping pulls this window's start past the end of any
    /// left sibling and its end before the start of any right sibling, while always keeping
    /// the occurrence's own match span visible. Returns the window unchanged when there is
    /// no overlap or when clipping would hide the match.
    /// </summary>
    private static PreviewLineWindow ClipMatchWindowToRenderedSiblings(
        RichTextBlock section,
        int lineNum,
        string line,
        SearchResult result,
        Regex? rx,
        PreviewLineWindow window)
    {
        int matchStart = ResolveSourceMatchStart(line, result, rx);
        int matchLength = result.MatchLength;
        if (matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
        {
            var fallback = rx?.Match(line);
            if (fallback is { Success: true, Length: > 0 })
            {
                matchStart = fallback.Index;
                matchLength = fallback.Length;
            }
        }

        if (matchStart < 0 || matchStart >= line.Length)
            return window;

        int matchEnd = Math.Min(line.Length, matchStart + Math.Max(0, matchLength));

        int newStart = window.SourceStart;
        int newEnd = window.SourceEnd;

        for (int i = 0; i < section.Blocks.Count; i++)
        {
            if (section.Blocks[i] is not Paragraph para
                || !s_paragraphLineNumbers.TryGetValue(para, out var lineObj)
                || lineObj is not int renderedLine
                || renderedLine != lineNum
                || !TryGetParagraphMatchWindow(para, out int siblingStart, out int siblingEnd))
            {
                continue;
            }

            // Sibling window entirely left of this occurrence: start after its end.
            if (siblingEnd > newStart && siblingEnd <= matchStart)
                newStart = siblingEnd;

            // Sibling window entirely right of this occurrence: end before its start.
            if (siblingStart < newEnd && siblingStart >= matchEnd)
                newEnd = siblingStart;
        }

        if (newStart >= newEnd || newStart > matchStart || newEnd < matchEnd)
            return window;

        if (newStart == window.SourceStart && newEnd == window.SourceEnd)
            return window;

        return CreatePreviewLineWindow(line, newStart, newEnd);
    }

    private static bool TryGetTargetMatchOrdinalInSegment(
        string sourceLine,
        SearchResult result,
        Regex? rx,
        PreviewLineWindow window,
        string segment,
        int segmentDisplayStart,
        out int matchOrdinal)
    {
        matchOrdinal = 0;

        int matchStart = ResolveSourceMatchStart(sourceLine, result, rx);
        if (matchStart < window.SourceStart || matchStart >= window.SourceEnd)
            return false;

        int targetDisplayStart = matchStart - window.SourceStart;
        if (window.SourceStart > 0)
            targetDisplayStart += LineTruncator.Ellipsis.Length;

        int segmentDisplayEnd = segmentDisplayStart + segment.Length;
        if (targetDisplayStart < segmentDisplayStart || targetDisplayStart >= segmentDisplayEnd)
            return false;

        int prefixLength = segmentDisplayStart == 0
            && window.SourceStart > 0
            && segment.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)
                ? LineTruncator.Ellipsis.Length
                : 0;
        int suffixLength = segmentDisplayEnd == window.Text.Length
            && window.SourceEnd < sourceLine.Length
            && segment.EndsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)
                ? LineTruncator.Ellipsis.Length
                : 0;
        int coreStart = prefixLength;
        int coreEnd = segment.Length - suffixLength;
        if (coreEnd < coreStart)
            return false;

        int targetCoreStart = targetDisplayStart - segmentDisplayStart - coreStart;
        if (targetCoreStart < 0 || targetCoreStart >= coreEnd - coreStart)
            return false;

        if (rx is null)
            return true;

        string coreText = segment[coreStart..coreEnd];
        int ordinal = 0;
        foreach (Match match in rx.Matches(coreText))
        {
            if (match.Index == targetCoreStart
                || (match.Index <= targetCoreStart && targetCoreStart < match.Index + match.Length))
            {
                matchOrdinal = ordinal;
                return true;
            }

            ordinal++;
        }

        return false;
    }

    private static Dictionary<int, SearchResult> BuildMatchByLineForRanges(
        List<SearchResult> results,
        List<(int start, int end)> ranges)
    {
        var matchByLine = new Dictionary<int, SearchResult>();
        if (ranges.Count == 0)
            return matchByLine;

        int rangeIndex = 0;
        foreach (var result in results)
        {
            int lineIndex = result.LineNumber - 1;
            if (lineIndex < 0)
                continue;

            while (rangeIndex < ranges.Count && lineIndex > ranges[rangeIndex].end)
                rangeIndex++;

            if (rangeIndex >= ranges.Count)
                break;

            if (lineIndex >= ranges[rangeIndex].start)
                matchByLine.TryAdd(result.LineNumber, result);
        }

        return matchByLine;
    }

    private static int CountPrefixResultsThroughLine(
        List<SearchResult> results,
        int lastRenderedLine,
        int totalLineCount)
    {
        int count = 0;
        while (count < results.Count)
        {
            int lineNumber = results[count].LineNumber;
            if (lineNumber >= 1 && lineNumber <= totalLineCount && lineNumber > lastRenderedLine)
                break;

            count++;
        }

        return count;
    }

    private static int CapConsumedResultsToVisibleEntriesForTruncatedWindow(
        int consumedResults,
        int addedMatchEntries,
        bool truncatePreviewLines)
    {
        if (!truncatePreviewLines || consumedResults <= addedMatchEntries)
            return consumedResults;

        return Math.Max(0, addedMatchEntries);
    }

    private int AppendHighlightMatchWindows(
        RichTextBlock section,
        List<SearchResult> pendingResults,
        string[] allLines,
        Regex? rx,
        SectionMatchNav? sectionNav,
        int previewLines,
        int maxAdditionalMatchEntries,
        int maxResultsToConsume,
        int maxAdditionalBlocks,
        out int consumedResults,
        out int paragraphsAdded,
        out int lastRenderedLine,
        int previouslyRenderedLine = 0,
        bool truncatePreviewLines = true)
    {
        consumedResults = 0;
        paragraphsAdded = 0;
        int addedMatchEntries = 0;
        lastRenderedLine = Math.Max(0, previouslyRenderedLine);

        // Line numbers already drawn in this section (from the initial render and any
        // earlier overflow chunks). We must never emit one of these again, otherwise
        // overlapping windows around dense same-line matches (common in OCR previews)
        // repeat the same line numbers down the pane.
        var sectionRenderedLines = GetRenderedLineNumbers(section);

        int anchorLimit = Math.Min(pendingResults.Count, maxResultsToConsume);
        // previouslyRenderedLine is 1-indexed; convert to 0-indexed for array comparisons
        int previouslyRenderedIndex = previouslyRenderedLine > 0 ? previouslyRenderedLine - 1 : -1;
        var rawRanges = new List<(int start, int end)>();
        for (int i = 0; i < anchorLimit; i++)
        {
            var result = pendingResults[i];
            int lineIndex = result.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= allLines.Length)
                continue;

            int start = Math.Max(0, lineIndex - previewLines);
            int end = Math.Min(allLines.Length - 1, lineIndex + previewLines);
            if (end <= previouslyRenderedIndex)
                continue;

            start = Math.Max(start, previouslyRenderedIndex + 1);
            rawRanges.Add((start, end));
        }
        // Sort by start before merging. The pending results are not guaranteed to be in
        // ascending line order, so a merge that only looks at the last range can leave
        // overlapping windows that re-render the same lines. Sorting first makes the
        // adjacency merge correct regardless of input order.
        var ranges = new List<(int start, int end)>();
        foreach (var range in rawRanges.OrderBy(r => r.start))
        {
            if (ranges.Count > 0 && range.start <= ranges[^1].end + 1)
                ranges[^1] = (ranges[^1].start, Math.Max(ranges[^1].end, range.end));
            else
                ranges.Add(range);
        }

        if (ranges.Count == 0)
        {
            // All pending results are on lines already rendered. Render a small
            // continuation window around each pending occurrence so every loaded
            // result gets a concrete target for global match navigation.
            int maxResults = Math.Min(anchorLimit, pendingResults.Count);
            if (!truncatePreviewLines)
            {
                consumedResults = maxResults;
                for (int i = 0; i < maxResults; i++)
                    lastRenderedLine = Math.Max(lastRenderedLine, pendingResults[i].LineNumber);
                return 0;
            }

            for (int i = 0; i < maxResults; i++)
            {
                if (addedMatchEntries >= maxAdditionalMatchEntries || paragraphsAdded >= maxAdditionalBlocks)
                    break;

                var result = pendingResults[i];
                int lineIndex = result.LineNumber - 1;
                if (lineIndex < 0 || lineIndex >= allLines.Length)
                    continue;

                // Reuse an existing window only when THIS occurrence's source column
                // actually falls inside one (its match-nav entry already exists). A
                // coarse line-number check is wrong for a single very long source line:
                // every occurrence shares line 1, so it would treat them all as already
                // navigable and silently drop them — leaving match navigation stuck at
                // occurrence 1. Occurrences outside every rendered window get their own
                // dedicated, sibling-clipped window below so nav can reach them.
                int sourceColumn = result.SourceMatchStartColumn >= 0
                    ? result.SourceMatchStartColumn
                    : result.MatchStartColumn;
                if (IsSourceColumnWithinRenderedWindow(section, result.LineNumber, sourceColumn))
                {
                    consumedResults++;
                    lastRenderedLine = Math.Max(lastRenderedLine, result.LineNumber);
                    continue;
                }

                if (paragraphsAdded >= maxAdditionalBlocks)
                    break;

                int contentStartIndex = section.Blocks.Count;
                int gutterStartIndex = GetGutterBlockCount(section);
                AddPreviewLineParagraphsAroundResult(
                    section,
                    allLines[lineIndex],
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
                    maxParagraphs: maxAdditionalBlocks - paragraphsAdded);
                MoveAppendedPreviewLineBesideExistingLine(
                    section,
                    result.LineNumber,
                    contentStartIndex,
                    gutterStartIndex,
                    addedParagraphs);

                paragraphsAdded += addedParagraphs;
                addedMatchEntries += matchEntriesAdded;
                consumedResults++;
                lastRenderedLine = Math.Max(lastRenderedLine, result.LineNumber);
            }

            return addedMatchEntries;
        }

        var matchByLine = BuildMatchByLineForRanges(pendingResults, ranges);
        SearchResult fallbackResult = matchByLine.Values.FirstOrDefault() ?? pendingResults[0];
        bool hasExistingRenderedLines = previouslyRenderedLine > 0;

        int rangeIndex = 0;
        while (rangeIndex < ranges.Count
               && addedMatchEntries < maxAdditionalMatchEntries
               && paragraphsAdded < maxAdditionalBlocks)
        {
            var (start, end) = ranges[rangeIndex++];

            if (hasExistingRenderedLines && ShouldAddGapBetweenRenderedLines(lastRenderedLine, start + 1))
            {
                if (paragraphsAdded >= maxAdditionalBlocks)
                    break;

                AddGapIndicator(section);
                paragraphsAdded++;
            }
            hasExistingRenderedLines = true;

            for (int i = start; i <= end; i++)
            {
                if (paragraphsAdded >= maxAdditionalBlocks || addedMatchEntries >= maxAdditionalMatchEntries)
                    break;

                int lineNumber = i + 1;
                // Never re-draw a line already rendered in this section (gap lines below
                // the high-water mark, or duplicates within this call); doing so repeats
                // the line number further down the pane.
                if (!sectionRenderedLines.Add(lineNumber))
                {
                    lastRenderedLine = lineNumber;
                    continue;
                }
                bool isMatchLine = matchByLine.TryGetValue(lineNumber, out var matchResult);
                matchResult ??= fallbackResult;

                int beforeCount = _matchParagraphs.Count;
                AddPreviewLineParagraphs(
                    section,
                    allLines[i],
                    lineNumber,
                    isMatchLine,
                    matchResult,
                    rx,
                    truncate: truncatePreviewLines,
                    _matchParagraphs,
                    sectionNav,
                    out int addedParagraphs,
                    maxParagraphs: maxAdditionalBlocks - paragraphsAdded);

                paragraphsAdded += addedParagraphs;
                if (isMatchLine)
                    addedMatchEntries += _matchParagraphs.Count - beforeCount;
                lastRenderedLine = lineNumber;
            }
        }

        consumedResults = CountPrefixResultsThroughLine(pendingResults, lastRenderedLine, allLines.Length);
        consumedResults = CapConsumedResultsToVisibleEntriesForTruncatedWindow(
            consumedResults,
            addedMatchEntries,
            truncatePreviewLines);

        return addedMatchEntries;
    }

    private IEnumerable<string> EnumeratePreviewLineLayoutSegments(string line)
    {
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        int segmentSize = GetEffectiveSegmentSize();
        if (line.Length <= segmentSize)
        {
            yield return line;
            yield break;
        }

        for (int start = 0; start < line.Length; start += segmentSize)
        {
            int length = Math.Min(segmentSize, line.Length - start);
            yield return line.Substring(start, length);
        }
    }

    // Formats the gutter line-number token so the digits sit centered within the gutter
    // column as a whole. The column's visual field is the one-cell match/context indicator
    // ("│"/" "/"↳") PLUS this fixed-width run, so the centering biases one cell left to
    // absorb that leading indicator cell — otherwise the number lands half a cell right of
    // the box center (centered only in the space to the right of the indicator). The run
    // width stays fixed so the gutter column edge — and the drawn separator line — stays
    // vertically straight regardless of how many digits a line number has.
    private static string FormatGutterNumber(int lineNum, bool continuation)
    {
        const int runWidth = 6;        // emitted run width; keeps the column edge straight
        const int indicatorCells = 1;  // leading indicator cell sits to the left of this run
        string token = lineNum.ToString(CultureInfo.InvariantCulture);
        if (continuation)
            token += "+";
        if (token.Length >= runWidth)
            return token;
        int totalPad = (runWidth + indicatorCells) - token.Length;
        int leftCells = totalPad / 2;                                  // total left padding incl. indicator cell
        int leftSpaces = Math.Max(0, leftCells - indicatorCells); // spaces before the number
        int rightSpaces = runWidth - token.Length - leftSpaces;       // remainder fills the fixed run width
        if (rightSpaces < 0)
            rightSpaces = 0;
        return new string(' ', leftSpaces) + token + new string(' ', rightSpaces);
    }

    private Paragraph MakePreviewParagraph(string line, int lineNum, bool isMatchLine, SearchResult r, Regex? rx, bool truncate = true, bool continuationGutter = false, RichTextBlock? gutterBlock = null, PreviewTruncatedLineState? truncationState = null, HashSet<int>? matchOrdinalsToColor = null, (int start, int end)? forcedSpan = null)
    {
        line ??= string.Empty;
        if (truncate)
            line = TruncatePreviewLine(line, rx);
        var para = new Paragraph();
        s_paragraphLineNumbers.AddOrUpdate(para, lineNum);
        s_paragraphPrimaryResults.AddOrUpdate(para, r);
        if (continuationGutter)
            s_paragraphIsContinuation.AddOrUpdate(para, true);

        // Match indicator + line number gutter.
        // Use a glyph that Consolas renders at full cell width so match lines
        // align horizontally with context lines (which use a plain space).
        var indicator = new Run { Text = continuationGutter ? "↳" : (isMatchLine ? "│" : " ") };
        indicator.Foreground = continuationGutter ? s_contextGutterBrush : (isMatchLine ? s_matchAccentBrush : s_contextTextBrush);

        var gutterRun = new Run { Text = FormatGutterNumber(lineNum, continuationGutter) };
        gutterRun.Foreground = continuationGutter ? s_contextGutterBrush : (isMatchLine ? s_matchGutterBrush : s_contextGutterBrush);

        if (gutterBlock is not null)
        {
            // Emit gutter to the separate gutter block. The continuous separator line is
            // drawn as the gutter column's right border (see AddPreviewSection), so no pipe
            // glyph is emitted here — a pipe plus the border would render as a doubled line.
            var gutterPara = new Paragraph();
            gutterPara.Inlines.Add(indicator);
            gutterPara.Inlines.Add(gutterRun);
            gutterBlock.Blocks.Add(gutterPara);
        }
        else
        {
            // Inline gutter (legacy path for PreviewBlock single-file view). This block has
            // no forced line height, so the box-drawing pipe tiles into a continuous
            // separator on its own — keep the glyph here.
            var gutterSep = new Run { Text = "│ ", Foreground = s_gutterSepBrush };
            para.Inlines.Add(indicator);
            para.Inlines.Add(gutterRun);
            para.Inlines.Add(gutterSep);
            // Record the inline gutter's character length so the drag-selection overlay can
            // keep its highlight bands off the gutter (they must start at the content column).
            s_paragraphInlineGutterLength.AddOrUpdate(para,
                (indicator.Text?.Length ?? 0) + (gutterRun.Text?.Length ?? 0) + (gutterSep.Text?.Length ?? 0));
        }

        if (truncationState is not null)
            truncationState.ContentInlineStart = para.Inlines.Count;

        AddPreviewTextRuns(para, line, isMatchLine, rx, truncationState, matchOrdinalsToColor, forcedSpan);

        return para;
    }

    private void AddPreviewTextRuns(Paragraph para, string line, bool isMatchLine, Regex? rx, PreviewTruncatedLineState? truncationState, HashSet<int>? matchOrdinalsToColor = null, (int start, int end)? forcedSpan = null)
    {
        bool hasPrefixEllipsis = truncationState is not null
            && truncationState.SourceStart > 0
            && line.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal);
        bool hasSuffixEllipsis = truncationState is not null
            && truncationState.SourceEnd < truncationState.SourceLine.Length
            && line.EndsWith(LineTruncator.Ellipsis, StringComparison.Ordinal);

        int start = hasPrefixEllipsis ? LineTruncator.Ellipsis.Length : 0;
        int end = line.Length - (hasSuffixEllipsis ? LineTruncator.Ellipsis.Length : 0);

        if (hasPrefixEllipsis)
            para.Inlines.Add(CreatePreviewShowMoreInline(para, truncationState!, PreviewShowMoreEdge.Prefix));

        // A forced multiline span maps to full-line columns; only apply it when the rendered text is
        // the untruncated line (no ellipsis trimming shifting the offsets). Multiline-span lines are
        // rendered untruncated by the caller, so this holds in practice.
        (int start, int end)? spanForThisText = (forcedSpan is { } fsp && !hasPrefixEllipsis && !hasSuffixEllipsis)
            ? fsp
            : null;

        if (end > start)
            AddPreviewTextSpanRuns(para, line[start..end], isMatchLine, rx, matchOrdinalsToColor, spanForThisText);

        if (hasSuffixEllipsis)
            para.Inlines.Add(CreatePreviewShowMoreInline(para, truncationState!, PreviewShowMoreEdge.Suffix));
    }

    private void AddPreviewTextSpanRuns(Paragraph para, string text, bool isMatchLine, Regex? rx, HashSet<int>? matchOrdinalsToColor = null, (int start, int end)? forcedSpan = null)
    {
        // Full-span (Phase 3) multiline highlighting: a cross-line pattern never matches within a
        // single rendered line, so rx.Matches(text) below finds nothing on the start/body/end lines
        // of the span. When the caller has already computed the exact column range to highlight on
        // THIS line (from the result's span metadata), color it directly instead of re-matching.
        if (forcedSpan is (int forcedStart, int forcedEnd))
        {
            int fs = Math.Clamp(forcedStart, 0, text.Length);
            int fe = Math.Clamp(forcedEnd, fs, text.Length);
            if (fe > fs)
            {
                if (fs > 0)
                {
                    var pre = new Run { Text = text[..fs] };
                    pre.Foreground = isMatchLine ? _matchLineBrush : s_contextTextBrush;
                    para.Inlines.Add(pre);
                }
                var spanHit = new Run { Text = text[fs..fe] };
                s_previewSearchMatchRuns.AddOrUpdate(spanHit, new object());
                spanHit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                spanHit.Foreground = _matchTextBrush;
                para.Inlines.Add(spanHit);
                if (fe < text.Length)
                {
                    var post = new Run { Text = text[fe..] };
                    post.Foreground = isMatchLine ? _matchLineBrush : s_contextTextBrush;
                    para.Inlines.Add(post);
                }
                return;
            }
            // Empty forced span (clamped away) — fall through to the normal rendering below.
        }

        // Keep match-colored text tied to actual search-result rows added from
        // the results list. Context lines may contain the query text, but they
        // should not look like selected/registered matches in the preview.
        if (rx != null)
        {
            int lastIdx = 0;
            int matchOrdinal = 0;
            foreach (Match m in rx.Matches(text))
            {
                if (m.Index > lastIdx)
                {
                    var before = new Run { Text = text[lastIdx..m.Index] };
                    if (!isMatchLine) before.Foreground = s_contextTextBrush;
                    else before.Foreground = _matchLineBrush;
                    para.Inlines.Add(before);
                }
                var hit = new Run { Text = m.Value };
                s_previewSearchMatchRuns.AddOrUpdate(hit, new object());
                bool useMatchColor = isMatchLine && (matchOrdinalsToColor is null || matchOrdinalsToColor.Contains(matchOrdinal));
                if (useMatchColor)
                {
                    hit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    hit.Foreground = _matchTextBrush;
                }
                else if (!isMatchLine)
                {
                    hit.Foreground = s_contextTextBrush;
                }
                else
                {
                    hit.Foreground = _matchLineBrush;
                }
                para.Inlines.Add(hit);
                lastIdx = m.Index + m.Length;
                matchOrdinal++;
            }
            if (lastIdx < text.Length)
            {
                var tail = new Run { Text = text[lastIdx..] };
                if (!isMatchLine) tail.Foreground = s_contextTextBrush;
                else tail.Foreground = _matchLineBrush;
                para.Inlines.Add(tail);
            }
        }
        else
        {
            var plain = new Run { Text = text };
            if (!isMatchLine) plain.Foreground = s_contextTextBrush;
            else plain.Foreground = _matchLineBrush;
            para.Inlines.Add(plain);
        }
    }

    private readonly SolidColorBrush _previewShowMoreEllipsisBrush = new(Microsoft.UI.Colors.DodgerBlue);

    private InlineUIContainer CreatePreviewShowMoreInline(
        Paragraph paragraph,
        PreviewTruncatedLineState state,
        PreviewShowMoreEdge edge)
    {
        var action = new PreviewShowMoreAction(paragraph, state, edge);
        var marker = new TextBlock
        {
            Text = LineTruncator.Ellipsis,
            Foreground = _previewShowMoreEllipsisBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = ResolvePreviewShowMoreEllipsisFontSize(),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            IsTextSelectionEnabled = false,
        };
        var markerHost = new Border
        {
            Child = marker,
            Background = _transparentBrush,
            MinWidth = 8,
            MinHeight = 14,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
        };
        void showFromPointer(object? _, PointerRoutedEventArgs e)
        {
            _previewShowMorePointerOverMarker = true;
            CancelPreviewShowMoreTooltipHide();
            ShowPreviewShowMoreTooltip(action, e.GetCurrentPoint(PreviewShowMoreTooltipOverlay).Position);
        }

        void queueHideFromMarker(object? _, PointerRoutedEventArgs e)
        {
            _previewShowMorePointerOverMarker = false;
            QueuePreviewShowMoreTooltipHide();
        }

        markerHost.PointerEntered += showFromPointer;
        markerHost.PointerMoved += showFromPointer;
        markerHost.PointerExited += queueHideFromMarker;
        marker.PointerEntered += showFromPointer;
        marker.PointerMoved += showFromPointer;
        marker.PointerExited += queueHideFromMarker;
        markerHost.Tapped += (_, e) =>
        {
            e.Handled = true;
            ExpandPreviewShowMoreFromInlineClick(action);
        };
        var container = new InlineUIContainer { Child = markerHost };

        _previewShowMoreMarkerCount++;
        if (edge == PreviewShowMoreEdge.Prefix)
            _previewShowMorePrefixMarkerCount++;
        else
            _previewShowMoreSuffixMarkerCount++;

        s_previewShowMoreActions.Remove(container);
        s_previewShowMoreActions.Add(container, action);
        s_previewShowMoreActions.Remove(markerHost);
        s_previewShowMoreActions.Add(markerHost, action);
        s_previewShowMoreActions.Remove(marker);
        s_previewShowMoreActions.Add(marker, action);

        return container;
    }

    private void ExpandPreviewShowMoreFromInlineClick(PreviewShowMoreAction action)
    {
        CancelPreviewSelectionAutoScrollForShowMore("show-more-inline-click");
        HidePreviewShowMoreTooltip();
        QueuePreviewTruncatedLineExpansion(action, PreviewShowMoreExpandMode.More);
    }

    private void OnPreviewShowMorePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block
            || !TryGetPreviewShowMoreActionFromPointer(block, e, out var action))
        {
            _previewShowMorePointerOverMarker = false;
            QueuePreviewShowMoreTooltipHide();
            return;
        }

        _previewShowMorePointerOverMarker = true;
        CancelPreviewShowMoreTooltipHide();
        var point = e.GetCurrentPoint(PreviewShowMoreTooltipOverlay).Position;
        ShowPreviewShowMoreTooltip(action, point);
    }

    private void OnPreviewShowMorePointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Keep the action panel available when the pointer moves from the
        // inline marker toward the panel buttons.
    }

    private void ShowPreviewShowMoreTooltip(PreviewShowMoreAction action, Windows.Foundation.Point pointer)
    {
        CancelPreviewSelectionAutoScrollForShowMore("show-more-tooltip");
        EnsurePreviewShowMoreTooltipHandlers();
        CancelPreviewShowMoreTooltipHide();

        var edge = action.Edge;
        bool rebuild = _previewShowMoreTooltipAction is null
            || !ReferenceEquals(_previewShowMoreTooltipAction, action)
            || _previewShowMoreTooltipEdge != edge;

        if (rebuild)
        {
            PreviewShowMoreTooltipContent.Children.Clear();
            var foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            // "Show all" is only offered when it can actually reveal the entire line. For
            // extremely long lines subject to the bounded reveal cap (GetPreviewShowAllMaxRevealLength),
            // clicking "Show all" would only show a window, not everything, so the option is omitted.
            bool canShowEntireLine = action.State.SourceLine.Length <= GetPreviewShowAllMaxRevealLength();
            if (edge == PreviewShowMoreEdge.Prefix)
            {
                PreviewShowMoreTooltipContent.Children.Add(CreatePreviewShowMoreActionButton("\uE72B", "Show more", arrowFirst: true, action, PreviewShowMoreExpandMode.More, foreground));
                if (canShowEntireLine)
                    PreviewShowMoreTooltipContent.Children.Add(CreatePreviewShowMoreActionButton("\uE72B", "Show all", arrowFirst: true, action, PreviewShowMoreExpandMode.All, foreground));
            }
            else
            {
                PreviewShowMoreTooltipContent.Children.Add(CreatePreviewShowMoreActionButton("\uE72A", "Show more", arrowFirst: false, action, PreviewShowMoreExpandMode.More, foreground));
                if (canShowEntireLine)
                    PreviewShowMoreTooltipContent.Children.Add(CreatePreviewShowMoreActionButton("\uE72A", "Show all", arrowFirst: false, action, PreviewShowMoreExpandMode.All, foreground));
            }

            _previewShowMoreTooltipAction = action;
            _previewShowMoreTooltipEdge = edge;
        }

        if (PreviewShowMoreTooltipOverlay.Visibility != Visibility.Visible)
            PreviewShowMoreTooltipOverlay.Visibility = Visibility.Visible;

        PreviewShowMoreTooltipBubble.UpdateLayout();
        double bubbleWidth = Math.Max(190, PreviewShowMoreTooltipBubble.ActualWidth);
        double bubbleHeight = Math.Max(32, PreviewShowMoreTooltipBubble.ActualHeight);
        double maxLeft = Math.Max(4, PreviewShowMoreTooltipOverlay.ActualWidth - bubbleWidth - 4);
        double maxTop = Math.Max(4, PreviewShowMoreTooltipOverlay.ActualHeight - bubbleHeight - 4);

        // Center the "Show more" button (the first action button) directly under the cursor X so it
        // appears centered beneath the pointer; "Show all" follows it in the horizontal stack as usual.
        double showMoreCenterOffset = GetPreviewShowMoreButtonCenterOffset(bubbleWidth);
        double left = Math.Clamp(pointer.X - showMoreCenterOffset, 4, maxLeft);
        // Lift the bubble so the cursor lands just inside the top of the "Show more" button: the button
        // is layered directly underneath the pointer (extending downward) with "Show all" to its right.
        double top = Math.Clamp(pointer.Y - (PreviewShowMoreTooltipBubble.Padding.Top + PreviewShowMoreTooltipCursorOverlapDip), 4, maxTop);

        Canvas.SetLeft(PreviewShowMoreTooltipBubble, left);
        Canvas.SetTop(PreviewShowMoreTooltipBubble, top);
    }

    /// <summary>
    /// Returns the horizontal distance from the tooltip bubble's left edge to the center of the
    /// "Show more" button (the first action button) so the bubble can be positioned to center that
    /// button under the cursor. Falls back to centering the whole bubble when the button is not laid out.
    /// </summary>
    private double GetPreviewShowMoreButtonCenterOffset(double bubbleWidth)
    {
        if (PreviewShowMoreTooltipContent.Children.Count > 0
            && PreviewShowMoreTooltipContent.Children[0] is FrameworkElement showMoreButton
            && showMoreButton.ActualWidth > 0)
        {
            try
            {
                var origin = showMoreButton
                    .TransformToVisual(PreviewShowMoreTooltipBubble)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                return origin.X + (showMoreButton.ActualWidth / 2);
            }
            catch
            {
                return PreviewShowMoreTooltipBubble.Padding.Left + (showMoreButton.ActualWidth / 2);
            }
        }
        return bubbleWidth / 2;
    }

    private Button CreatePreviewShowMoreActionButton(
        string arrowGlyph,
        string label,
        bool arrowFirst,
        PreviewShowMoreAction action,
        PreviewShowMoreExpandMode mode,
        Brush foreground)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var arrow = new TextBlock
        {
            Text = arrowGlyph,
            Foreground = foreground,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = label,
            Foreground = foreground,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (arrowFirst)
        {
            content.Children.Add(arrow);
            content.Children.Add(text);
        }
        else
        {
            content.Children.Add(text);
            content.Children.Add(arrow);
        }

        var button = new Button
        {
            Content = content,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(7, 3, 7, 3),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        button.PointerEntered += (_, _) =>
        {
            _previewShowMorePointerOverPanel = true;
            CancelPreviewShowMoreTooltipHide();
        };
        button.PointerMoved += (_, _) =>
        {
            _previewShowMorePointerOverPanel = true;
            CancelPreviewShowMoreTooltipHide();
        };
        button.PointerExited += (_, _) =>
        {
            _previewShowMorePointerOverPanel = false;
            QueuePreviewShowMoreTooltipHide();
        };
        button.Click += (_, _) =>
        {
            CancelPreviewSelectionAutoScrollForShowMore($"show-more-{mode}");
            _previewShowMorePointerOverPanel = true;
            CancelPreviewShowMoreTooltipHide();
            QueuePreviewTruncatedLineExpansion(action, mode);
        };
        return button;
    }

    private static bool TryGetPreviewShowMoreActionFromPointer(
        RichTextBlock block,
        PointerRoutedEventArgs e,
        out PreviewShowMoreAction action)
    {
        if (TryGetPreviewShowMoreAction(e.OriginalSource, out action))
            return true;

        try
        {
            var point = e.GetCurrentPoint(block).Position;
            foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(point, block))
            {
                if (TryGetPreviewShowMoreAction(element, out action))
                    return true;
            }
        }
        catch (Exception ex)
        {
            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("PreviewShowMore", "Show-more hover hit-test failed", ex);
        }

        action = default!;
        return false;
    }

    private void EnsurePreviewShowMoreTooltipHandlers()
    {
        if (_previewShowMoreTooltipHandlersAttached)
            return;

        _previewShowMoreTooltipHandlersAttached = true;
        PreviewShowMoreTooltipOverlay.PointerPressed += OnPreviewShowMoreTooltipOverlayPointerPressed;
        PreviewShowMoreTooltipBubble.PointerEntered += (_, _) =>
        {
            _previewShowMorePointerOverPanel = true;
            CancelPreviewShowMoreTooltipHide();
        };
        PreviewShowMoreTooltipBubble.PointerMoved += (_, _) =>
        {
            _previewShowMorePointerOverPanel = true;
            CancelPreviewShowMoreTooltipHide();
        };
        PreviewShowMoreTooltipBubble.PointerExited += (_, _) =>
        {
            _previewShowMorePointerOverPanel = false;
            QueuePreviewShowMoreTooltipHide();
        };
    }

    private void OnPreviewShowMoreTooltipOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && IsElementWithin(source, PreviewShowMoreTooltipBubble))
        {
            _previewShowMorePointerOverPanel = true;
            CancelPreviewShowMoreTooltipHide();
            return;
        }

        HidePreviewShowMoreTooltip();
        e.Handled = true;
    }

    private void HidePreviewShowMoreTooltipForContentPointer()
    {
        if (PreviewShowMoreTooltipOverlay.Visibility == Visibility.Visible)
            HidePreviewShowMoreTooltip();
    }

    private void CancelPreviewShowMoreTooltipHide()
    {
        _previewShowMoreTooltipHideTimer?.Stop();
    }

    private void QueuePreviewShowMoreTooltipHide()
    {
        if (PreviewShowMoreTooltipOverlay.Visibility != Visibility.Visible)
            return;
        if (_previewShowMorePointerOverMarker || _previewShowMorePointerOverPanel)
            return;

        if (_previewShowMoreTooltipHideTimer is null)
        {
            _previewShowMoreTooltipHideTimer = DispatcherQueue.CreateTimer();
            _previewShowMoreTooltipHideTimer.Interval = TimeSpan.FromMilliseconds(PreviewShowMoreTooltipHideDelayMs);
            _previewShowMoreTooltipHideTimer.IsRepeating = false;
            _previewShowMoreTooltipHideTimer.Tick += (_, _) =>
            {
                _previewShowMoreTooltipHideTimer?.Stop();
                if (!_previewShowMorePointerOverMarker && !_previewShowMorePointerOverPanel)
                    HidePreviewShowMoreTooltip();
            };
        }

        _previewShowMoreTooltipHideTimer.Stop();
        _previewShowMoreTooltipHideTimer.Start();
    }

    private void HidePreviewShowMoreTooltip()
    {
        if (PreviewShowMoreTooltipOverlay.Visibility == Visibility.Collapsed)
            return;

        CancelPreviewShowMoreTooltipHide();
        PreviewShowMoreTooltipOverlay.Visibility = Visibility.Collapsed;
        _previewShowMoreTooltipAction = null;
        _previewShowMoreTooltipEdge = null;
        _previewShowMorePointerOverMarker = false;
        _previewShowMorePointerOverPanel = false;
    }

    /// <summary>
    /// Dismisses the open "Show more"/"Show all" action panel once the ellipsis that
    /// triggered it has been expanded away, so the panel never lingers over text that
    /// no longer has a marker. "Show all" reveals the whole line (every ellipsis on it
    /// disappears), so it always dismisses; "Show more" dismisses only when the marker
    /// on the panel's own edge is fully expanded. No-op when the panel belongs to a
    /// different line or is already hidden.
    /// </summary>
    private void HidePreviewShowMoreTooltipIfExpandedAway(PreviewShowMoreAction action, PreviewShowMoreExpandMode mode)
    {
        if (PreviewShowMoreTooltipOverlay.Visibility != Visibility.Visible)
            return;
        if (_previewShowMoreTooltipAction is null
            || !ReferenceEquals(_previewShowMoreTooltipAction.State, action.State))
            return;

        var state = action.State;
        bool prefixMarkerVisible = state.SourceStart > 0;
        bool suffixMarkerVisible = state.SourceEnd < state.SourceLine.Length;

        bool markerGone = mode == PreviewShowMoreExpandMode.All
            || (_previewShowMoreTooltipEdge == PreviewShowMoreEdge.Prefix && !prefixMarkerVisible)
            || (_previewShowMoreTooltipEdge == PreviewShowMoreEdge.Suffix && !suffixMarkerVisible);

        if (markerGone)
            HidePreviewShowMoreTooltip();
    }

    private void QueuePreviewTruncatedLineExpansion(PreviewShowMoreAction action, PreviewShowMoreExpandMode mode)
    {
        var state = action.State;
        LogService.Instance.Info(
            "PreviewShowMore",
            $"action mode={mode}, edge={action.Edge}, line={state.LineNumber}, window=({state.SourceStart},{state.SourceEnd}), lineLen={state.SourceLine.Length}, queued=True");

        bool queued = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => ExpandPreviewTruncatedLineSafely(action, mode));
        if (!queued)
        {
            LogService.Instance.Warning(
                "PreviewShowMore",
                $"dispatcher enqueue failed; expanding inline for line={state.LineNumber}, edge={action.Edge}");
            ExpandPreviewTruncatedLineSafely(action, mode);
        }
    }

    private void ExpandPreviewTruncatedLineSafely(PreviewShowMoreAction action, PreviewShowMoreExpandMode mode)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            ExpandPreviewTruncatedLine(action, mode);
            sw.Stop();
            LogService.Instance.Info(
                "PreviewShowMore",
                $"expand complete mode={mode}, line={action.State.LineNumber}, edge={action.Edge}, elapsedMs={sw.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogService.Instance.Critical(
                "PreviewShowMore",
                $"expand failed mode={mode}, line={action.State.LineNumber}, edge={action.Edge}, window=({action.State.SourceStart},{action.State.SourceEnd}), elapsedMs={sw.ElapsedMilliseconds}",
                ex);
        }
    }

    private void ExpandPreviewTruncatedLine(PreviewShowMoreAction action, PreviewShowMoreExpandMode mode)
    {
        var state = action.State;
        int lineLength = state.SourceLine.Length;
        int currentLength = Math.Max(0, state.SourceEnd - state.SourceStart);
        int maxWindowLength = GetPreviewShowMoreMaxWindowLength();
        int chunk = Math.Min(Math.Max(50, LineTruncator.TruncatedLength), maxWindowLength);

        int newStart = state.SourceStart;
        int newEnd = state.SourceEnd;
        int oldStart = state.SourceStart;
        int oldEnd = state.SourceEnd;

        if (mode == PreviewShowMoreExpandMode.All)
        {
            int maxReveal = GetPreviewShowAllMaxRevealLength();
            if (lineLength <= maxReveal)
            {
                newStart = 0;
                newEnd = lineLength;
            }
            else
            {
                int center = state.SourceStart + (currentLength / 2);
                newStart = Math.Clamp(center - (maxReveal / 2), 0, lineLength - maxReveal);
                newEnd = newStart + maxReveal;
            }
        }
        else if (action.Edge == PreviewShowMoreEdge.Prefix && state.SourceStart > 0)
        {
            int add = Math.Min(chunk, state.SourceStart);
            if (currentLength + add <= maxWindowLength)
            {
                newStart = state.SourceStart - add;
            }
            else
            {
                newStart = Math.Max(0, state.SourceStart - add);
                newEnd = Math.Min(lineLength, newStart + maxWindowLength);
            }
        }
        else if (action.Edge == PreviewShowMoreEdge.Suffix && state.SourceEnd < lineLength)
        {
            int add = Math.Min(chunk, lineLength - state.SourceEnd);
            if (currentLength + add <= maxWindowLength)
            {
                newEnd = state.SourceEnd + add;
            }
            else
            {
                newEnd = Math.Min(lineLength, state.SourceEnd + add);
                newStart = Math.Max(0, newEnd - maxWindowLength);
            }
        }

        if (newStart == state.SourceStart && newEnd == state.SourceEnd)
            return;

        LogService.Instance.Info(
            "PreviewShowMore",
            $"expand edge={action.Edge}, line={state.LineNumber}, old=({oldStart},{oldEnd}), new=({newStart},{newEnd}), lineLen={lineLength}, maxWindow={maxWindowLength}");
        var scrollSnapshot = CapturePreviewShowMoreScrollPosition(action);
        try
        {
            state.SourceStart = newStart;
            state.SourceEnd = newEnd;
            RebuildPreviewTruncatedLineParagraph(action.Paragraph, state);
            RestorePreviewShowMoreScrollPosition(scrollSnapshot);
            ReapplyActiveMatchOverlayAfterExpansion(action.Paragraph, oldStart, newStart);
            HidePreviewShowMoreTooltipIfExpandedAway(action, mode);
        }
        catch
        {
            state.SourceStart = oldStart;
            state.SourceEnd = oldEnd;
            throw;
        }
    }

    private PreviewShowMoreScrollSnapshot CapturePreviewShowMoreScrollPosition(PreviewShowMoreAction action)
    {
        ScrollViewer? sectionScroller = TryGetPreviewShowMoreSectionScroller(action.Paragraph);
        return new PreviewShowMoreScrollSnapshot(
            PreviewScrollViewer.HorizontalOffset,
            PreviewScrollViewer.VerticalOffset,
            sectionScroller,
            sectionScroller?.HorizontalOffset ?? 0,
            sectionScroller?.VerticalOffset ?? 0);
    }

    private ScrollViewer? TryGetPreviewShowMoreSectionScroller(Paragraph paragraph)
    {
        if (TryFindPreviewBlockForParagraph(paragraph, out var block)
            && _sectionMatchNavs.TryGetValue(block, out var sectionNav))
        {
            return sectionNav.Scroller;
        }

        return null;
    }

    private bool TryFindPreviewBlockForParagraph(Paragraph paragraph, out RichTextBlock block)
    {
        if (ContainsPreviewParagraph(PreviewBlock, paragraph))
        {
            block = PreviewBlock;
            return true;
        }

        foreach (var candidate in _sectionMatchNavs.Keys)
        {
            if (ContainsPreviewParagraph(candidate, paragraph))
            {
                block = candidate;
                return true;
            }
        }

        block = default!;
        return false;
    }

    private static bool ContainsPreviewParagraph(RichTextBlock block, Paragraph paragraph)
    {
        foreach (var candidate in block.Blocks)
        {
            if (ReferenceEquals(candidate, paragraph))
                return true;
        }

        return false;
    }

    private void RestorePreviewShowMoreScrollPosition(PreviewShowMoreScrollSnapshot snapshot)
    {
        ApplyPreviewShowMoreScrollPosition(snapshot);

        if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                () =>
                {
                    PreviewScrollViewer.UpdateLayout();
                    snapshot.SectionScroller?.UpdateLayout();
                    ApplyPreviewShowMoreScrollPosition(snapshot);
                }))
        {
            LogService.Instance.Warning("PreviewShowMore", "failed to queue deferred scroll restore after expansion");
        }
    }

    private void ApplyPreviewShowMoreScrollPosition(PreviewShowMoreScrollSnapshot snapshot)
    {
        RestoreScrollViewerOffsets(
            PreviewScrollViewer,
            snapshot.PreviewHorizontalOffset,
            snapshot.PreviewVerticalOffset);

        if (snapshot.SectionScroller is not null)
        {
            RestoreScrollViewerOffsets(
                snapshot.SectionScroller,
                snapshot.SectionHorizontalOffset,
                snapshot.SectionVerticalOffset);
        }
    }

    private static void RestoreScrollViewerOffsets(ScrollViewer scroller, double horizontalOffset, double verticalOffset)
    {
        double targetX = ClampScrollOffset(horizontalOffset, scroller.ScrollableWidth);
        double targetY = ClampScrollOffset(verticalOffset, scroller.ScrollableHeight);
        scroller.ChangeView(targetX, targetY, null, disableAnimation: true);
    }

    private static double ClampScrollOffset(double offset, double scrollableLength)
    {
        if (double.IsNaN(offset) || double.IsInfinity(offset))
            return 0;

        double max = double.IsNaN(scrollableLength) || double.IsInfinity(scrollableLength)
            ? 0
            : Math.Max(0, scrollableLength);

        return Math.Clamp(offset, 0, max);
    }

    private void RebuildPreviewTruncatedLineParagraph(Paragraph paragraph, PreviewTruncatedLineState state)
    {
        while (paragraph.Inlines.Count > state.ContentInlineStart)
            paragraph.Inlines.RemoveAt(state.ContentInlineStart);

        // Any window longer than one layout segment must be split into segment-sized runs
        // separated by LineBreaks (single very long runs hang Wrap layout and throw E_FAIL in
        // NoWrap). This covers both a full-line "Show all" and the bounded "Show all" window.
        if ((state.SourceEnd - state.SourceStart) > GetEffectiveSegmentSize())
        {
            AddPreviewFullLineSegmentRuns(paragraph, state);
            return;
        }

        var window = CreatePreviewLineWindow(state.SourceLine, state.SourceStart, state.SourceEnd);
        var matchOrdinalsToColor = BuildTargetColorOrdinals(state, window, window.Text, segmentDisplayStart: 0);
        AddPreviewTextRuns(paragraph, window.Text, state.IsMatchLine, state.Regex, state, matchOrdinalsToColor);
    }

    private void AddPreviewFullLineSegmentRuns(Paragraph paragraph, PreviewTruncatedLineState state)
    {
        var fullWindow = CreatePreviewLineWindow(state.SourceLine, 0, state.SourceLine.Length);
        int segmentLength = Math.Max(50, GetEffectiveSegmentSize() - (2 * LineTruncator.Ellipsis.Length));
        int windowStart = Math.Clamp(state.SourceStart, 0, state.SourceLine.Length);
        int windowEnd = Math.Clamp(state.SourceEnd, windowStart, state.SourceLine.Length);

        // When the revealed window is bounded (a "Show all" on an extremely long line), keep the
        // clickable prefix/suffix ellipses so the user can continue expanding the remaining text.
        if (windowStart > 0)
            paragraph.Inlines.Add(CreatePreviewShowMoreInline(paragraph, state, PreviewShowMoreEdge.Prefix));

        bool firstSegment = true;
        for (int index = windowStart; index < windowEnd; index += segmentLength)
        {
            if (!firstSegment)
                paragraph.Inlines.Add(new LineBreak());

            int length = Math.Min(segmentLength, windowEnd - index);
            string segmentText = state.SourceLine.Substring(index, length);
            var matchOrdinalsToColor = BuildTargetColorOrdinals(state, fullWindow, segmentText, segmentDisplayStart: index);
            AddPreviewTextSpanRuns(paragraph, segmentText, state.IsMatchLine, state.Regex, matchOrdinalsToColor);
            firstSegment = false;
        }

        if (windowEnd <= windowStart)
            AddPreviewTextSpanRuns(paragraph, string.Empty, state.IsMatchLine, state.Regex, BuildTargetColorOrdinals(state, fullWindow, string.Empty, segmentDisplayStart: windowStart));

        if (windowEnd < state.SourceLine.Length)
            paragraph.Inlines.Add(CreatePreviewShowMoreInline(paragraph, state, PreviewShowMoreEdge.Suffix));
    }

    /// <summary>
    /// Computes the set of regex-match ordinals (within <paramref name="segment"/>) that
    /// should be colored as a match after a "Show more"/"Show all" expansion. Returns
    /// <c>null</c> when the line colors every match (the standard preview path), and an
    /// empty set for segments that do not contain the originally-selected occurrence so
    /// that newly revealed, unselected matches stay context-colored.
    /// </summary>
    private static HashSet<int>? BuildTargetColorOrdinals(
        PreviewTruncatedLineState state,
        PreviewLineWindow window,
        string segment,
        int segmentDisplayStart)
    {
        if (!state.ColorTargetMatchOnly)
            return null;

        var ordinals = new HashSet<int>();
        if (TryGetTargetMatchOrdinalInSegment(state.SourceLine, state.Result, state.Regex, window, segment, segmentDisplayStart, out int ordinal))
            ordinals.Add(ordinal);
        return ordinals;
    }

    private string TruncatePreviewLine(string line, Regex? rx)
    {
        return TruncatePreviewLineWindow(line, rx).Text;
    }

}
