using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

    private bool ShouldTruncatePreviewLines()
        => ViewModel.PreviewWrapModeIndex != (int)Models.PreviewWrapMode.NoWrap;

    private bool ShouldTruncateOverflowPreviewLines()
        => ShouldTruncatePreviewLines()
           || ViewModel.PreviewWrapModeIndex == (int)Models.PreviewWrapMode.NoWrap;

    private int GetEffectiveSegmentSize()
        => ViewModel.PreviewWrapModeIndex == (int)Models.PreviewWrapMode.NoWrap
            ? PreviewLineLayoutSegmentCharsNoWrap
            : PreviewLineLayoutSegmentChars;

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
        if (contentBlocks.Count == 0 || contentBlocks.Count != gutterBlocks.Count) return;

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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, object> s_gutterWrappedContinuationCounts = new();

    private static void SetGutterWrappedContinuationRows(Paragraph gutterParagraph, int visualLineCount)
    {
        int targetContinuationRows = Math.Max(0, visualLineCount - 1);
        int currentContinuationRows = 0;
        if (s_gutterWrappedContinuationCounts.TryGetValue(gutterParagraph, out var raw)
            && raw is int storedRows
            && storedRows > 0)
        {
            currentContinuationRows = storedRows;
        }

        while (currentContinuationRows > 0 && gutterParagraph.Inlines.Count >= 2)
        {
            gutterParagraph.Inlines.RemoveAt(gutterParagraph.Inlines.Count - 1);
            gutterParagraph.Inlines.RemoveAt(gutterParagraph.Inlines.Count - 1);
            currentContinuationRows--;
        }

        for (int i = 0; i < targetContinuationRows; i++)
        {
            gutterParagraph.Inlines.Add(new LineBreak());
            gutterParagraph.Inlines.Add(new Run { Text = "       │ ", Foreground = s_gutterSepBrush });
        }

        s_gutterWrappedContinuationCounts.Remove(gutterParagraph);
        if (targetContinuationRows > 0)
            s_gutterWrappedContinuationCounts.Add(gutterParagraph, targetContinuationRows);
    }

    /// <summary>Adds a spacer paragraph to the gutter block to keep it aligned with non-line paragraphs in the content.</summary>
    private void SyncGutterSpacer(RichTextBlock section, Thickness margin)
    {
        if (_sectionGutterBlocks.TryGetValue(section, out var gb))
        {
            var spacer = new Paragraph { Margin = margin };
            spacer.Inlines.Add(new Run { Text = "       │ ", Foreground = s_gutterSepBrush });
            gb.Blocks.Add(spacer);
        }
    }

    /// <summary>
    /// Adds a gap indicator (⋮) between non-contiguous line ranges.
    /// When a gutter block exists, the indicator goes in the gutter and a
    /// blank spacer is added to the content block to keep them aligned.
    /// </summary>
    private void AddGapIndicator(RichTextBlock section)
    {
        if (_sectionGutterBlocks.TryGetValue(section, out var gb))
        {
            var gutterGap = new Paragraph();
            var gutterGapRun = new Run { Text = "      \u22EE" };
            gutterGapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
            gutterGap.Inlines.Add(gutterGapRun);
            gutterGap.Inlines.Add(new Run { Text = "│ ", Foreground = s_gutterSepBrush });
            gb.Blocks.Add(gutterGap);

            var contentSpacer = new Paragraph();
            contentSpacer.Inlines.Add(new Run { Text = "\u22EE", Foreground = _transparentBrush });
            section.Blocks.Add(contentSpacer);
        }
        else
        {
            var gap = new Paragraph();
            var gapRun = new Run { Text = "  \u22EE" };
            gapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
            gap.Inlines.Add(gapRun);
            section.Blocks.Add(gap);
        }
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
    private static async Task<Dictionary<string, string[]?>> ReadAllFileContentsAsync(
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

            Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
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
        int totalMatches = _previewTotalMatchCount > 0 ? _previewTotalMatchCount : _matchParagraphs.Count + _lazyMatchCount + deferredMatchCount;
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
        var buildSw = System.Diagnostics.Stopwatch.StartNew();
        bool truncatePreviewLines = ShouldTruncatePreviewLines();
        int parasBuilt = 0;
        int renderedResults = 0;
        int startingBlocks = section.Blocks.Count;
        int maxMatches = EffectiveInitialMaxMatchesPerSection;
        int maxBlocks = EffectiveInitialMaxPreviewBlocksPerSection;
        int cap = Math.Min(results.Count, maxMatches);
        foreach (var r in results)
        {
            if (renderedResults >= cap || section.Blocks.Count - startingBlocks >= maxBlocks)
                break;

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
                gutterSep.Inlines.Add(new Run { Text = "       │ ", Foreground = s_gutterSepBrush });
                gb.Blocks.Add(gutterSep);
            }

            var lines = GetPreviewLines(r, allLines, previewLines, fullFile: false);
            foreach (var (line, lineNum) in lines)
            {
                if (section.Blocks.Count - startingBlocks >= maxBlocks)
                    break;

                bool isMatchLine = lineNum == r.LineNumber;
                _sectionMatchNavs.TryGetValue(section, out var sn);
                AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: truncatePreviewLines,
                    lineNum == r.LineNumber ? _matchParagraphs : null, sn, out int addedParagraphs);
                parasBuilt += addedParagraphs;
            }

            renderedResults++;
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
    }

    // Yield to the UI dispatcher after this many paragraphs have been added during
    // a section build, so the window stays responsive while large highlight sections
    // (e.g. minified/blob files producing 1000+ paragraphs) are constructed.
    private const int BuildHighlightYieldEvery = 200;

    private async Task BuildHighlightSectionAsync(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        var buildSw = System.Diagnostics.Stopwatch.StartNew();
        bool truncatePreviewLines = ShouldTruncatePreviewLines();
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
                int start = Math.Max(0, matchLineNumber - 1 - previewLines);
                int end = Math.Min(allLines.Length - 1, matchLineNumber - 1 + previewLines);
                var matchResult = results.First(result => result.LineNumber == matchLineNumber);
                _sectionMatchNavs.TryGetValue(section, out var singleLineSectionNav);

                for (int i = start; i <= end; i++)
                {
                    int lineNum = i + 1;
                    bool isMatchLine = lineNum == matchLineNumber;
                    AddPreviewLineParagraphs(
                        section,
                        allLines[i],
                        lineNum,
                        isMatchLine,
                        matchResult,
                        rx,
                        truncate: truncatePreviewLines,
                        isMatchLine ? _matchParagraphs : null,
                        singleLineSectionNav,
                        out int addedParagraphs);
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

            var ranges = new List<(int start, int end)>();
            foreach (var lineNum in cappedResults.Select(r => r.LineNumber).Distinct().OrderBy(n => n))
            {
                int s = Math.Max(0, lineNum - 1 - previewLines);
                int e = Math.Min(allLines.Length - 1, lineNum - 1 + previewLines);
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
            foreach (var (start, end) in merged)
            {
                if (section.Blocks.Count - startingBlocks >= maxBlocks)
                    break;

                if (!firstRange)
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
                    AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatchLine, matchResult, rx, truncate: truncatePreviewLines, _matchParagraphs, sn, out int addedParagraphs);
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
            }
            } // end else (multi-line range rendering)
        }
        else
        {
            var matchLineNums = new HashSet<int>(cappedResults.Select(r => r.LineNumber));
            foreach (var r in cappedResults)
            {
                if (section.Blocks.Count - startingBlocks >= maxBlocks)
                    break;

                var lines = GetPreviewLines(r, null, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    if (section.Blocks.Count - startingBlocks >= maxBlocks)
                        break;

                    bool isMatchLine = matchLineNums.Contains(lineNum);
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: truncatePreviewLines,
                        lineNum == r.LineNumber ? _matchParagraphs : null, sn, out int addedParagraphs);
                    parasBuilt += addedParagraphs;
                    parasSinceYield += addedParagraphs;
                    if (parasSinceYield >= BuildHighlightYieldEvery)
                    {
                        parasSinceYield = 0;
                        yieldCount++;
                        await DispatchIdleAsync();
                    }
                }
            }
        }

        int actualMatchEntries = _matchParagraphs.Count - sectionMatchStart;
        int renderedCount = allLines != null
            ? CountPrefixResultsThroughLine(results, lastRenderedLine1, allLines.Length)
            : Math.Min(results.Count, actualMatchEntries);
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
    }

    /// <summary>
    /// Pre-computes the total regex match count for a section without building any UI elements.
    /// Used for lazy sections so the global nav label can show the correct total.
    /// </summary>
    private static int ComputeMatchCount(
        List<SearchResult> results, string[]? allLines,
        bool isHighlight, int previewLines, Regex? rx)
    {
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

        var matSw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Instance.Info("Preview", $"MaterializeLazySection: file='{System.IO.Path.GetFileName(lazy.FilePath)}', matches={lazy.MatchCount}");

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

        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
        if (lazy.IsHighlight)
            await BuildHighlightSectionAsync(section, lazy.Results, allLines, lazy.PreviewLines, rx);
        else
            BuildConcatenatedSection(section, lazy.Results, allLines, lazy.PreviewLines, rx);

        _lazyMatchCount -= lazy.MatchCount;
        matSw.Stop();
        LogService.Instance.Info("Preview", $"MaterializeLazySection complete: file='{System.IO.Path.GetFileName(lazy.FilePath)}', elapsed={matSw.ElapsedMilliseconds}ms, remainingLazy={_lazySections.Count}");
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

    private static string[] ReadAllLinesWithEncodingSync(string filePath)
    {
        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var encoding = Helpers.EncodingDetector.DetectEncoding(fs);
        if (encoding is System.Text.UTF8Encoding)
            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
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
    private static async Task<string[]> ReadAllLinesWithEncodingAsync(string filePath)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var encoding = Helpers.EncodingDetector.DetectEncoding(fs);
        if (encoding is System.Text.UTF8Encoding)
            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
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
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);

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
            bool isMatch = matchLines.Contains(lineNum);
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

    private static List<(string line, int lineNum)> GetPreviewLines(SearchResult r, string[]? allLines, int previewLines, bool fullFile)
    {
        var lines = new List<(string, int)>();
        int matchLineNum = r.LineNumber;

        if (allLines != null)
        {
            int matchIdx = matchLineNum - 1;
            if (matchIdx < 0) matchIdx = 0;
            if (matchIdx >= allLines.Length) matchIdx = allLines.Length - 1;

            int startLine, endLine;
            if (fullFile)
            {
                startLine = 0;
                endLine = allLines.Length - 1;
            }
            else
            {
                startLine = Math.Max(0, matchIdx - previewLines);
                endLine = Math.Min(allLines.Length - 1, matchIdx + previewLines);
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
        var singleSw = System.Diagnostics.Stopwatch.StartNew();
        ShowPreviewBlockSurface();
        PreviewBlock.Tag = r.FilePath;

        ViewModel.HydrateResult(r);

        string[]? allLines = null;
        // Read the file for context lines so we always show the configured PreviewContextLines
        // amount, not just whatever the SearchResult stored (which may be fewer or empty after eviction).
        try { allLines = await ReadAllLinesWithEncodingAsync(r.FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogService.Instance.Verbose("Preview", $"ShowSingleFilePreviewAsync: cannot read file '{r.FilePath}', using stored context: {ex.GetType().Name}");
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
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);

        int lineCount = 0;
        bool truncatePreviewLines = !fullFile && ShouldTruncatePreviewLines();
        var lines = GetPreviewLines(r, allLines, ViewModel.PreviewContextLines, fullFile);
        int maxLineLen = 0;
        foreach (var (l, _) in lines)
            if (l is not null && l.Length > maxLineLen) maxLineLen = l.Length;
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync rebuild: wrapMode={ViewModel.PreviewWrapModeIndex}, segmentCap={GetEffectiveSegmentSize()}, truncate={truncatePreviewLines}, lines={lines.Count}, maxLineLen={maxLineLen}");
        foreach (var (line, lineNum) in lines)
        {
            bool isMatchLine = lineNum == r.LineNumber;
            AddPreviewLineParagraphs(PreviewBlock, line, lineNum, isMatchLine, r, rx, truncate: truncatePreviewLines, null, null, out int addedParagraphs);
            lineCount += addedParagraphs;
        }
        singleSw.Stop();
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync complete: lines={lineCount}, blocks={PreviewBlock.Blocks.Count}, elapsed={singleSw.ElapsedMilliseconds}ms");

        }
        finally
        {
            _previewMutating = false;
        }
    }

    private void ShowPreviewBlockSurface()
    {
        PreviewScrollViewer.Padding = new Thickness(16, 12, 16, 12);
        PreviewSectionsPanel.Children.Clear();
        PreviewSectionsPanel.Visibility = Visibility.Collapsed;
        PreviewBlock.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Visible);
        HideMatchNavPanel();
        // Restore outer horizontal scroll for single-file block view.
        ApplyPreviewHorizontalScrollForWrap(PreviewScrollViewer, ViewModel.PreviewWordWrap);
        HideStickyHorizontalScrollBar();
    }

    private void ShowPreviewSectionsSurface()
    {
        LogService.Instance.Info("Preview", $"ShowPreviewSectionsSurface: clearing {PreviewSectionsPanel.Children.Count} existing sections");
        PreviewScrollViewer.Padding = new Thickness(0, 0, 0, 0);
        PreviewBlock.Blocks.Clear();
        PreviewBlock.Visibility = Visibility.Collapsed;
        PreviewSectionsPanel.Children.Clear();
        PreviewSectionsPanel.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Collapsed);
        HideMatchNavPanel();
        // Sections have their own per-section horizontal scroll; outer viewer stays vertical-only.
        SetHorizontalPreviewScroll(PreviewScrollViewer, enabled: false);
        HideStickyHorizontalScrollBar();
    }

    private void ShowPreviewLoading(string message = "Loading preview…")
    {
        PreviewScrollViewer.Padding = new Thickness(16, 12, 16, 12);
        PreviewMessagePanel.Visibility = Visibility.Collapsed;
        PreviewSectionsPanel.Visibility = Visibility.Collapsed;
        PreviewLoadingText.Text = message;
        PreviewLoadingRing.IsActive = true;
        PreviewLoadingPanel.Visibility = Visibility.Visible;
    }

    private void HidePreviewLoading()
    {
        PreviewLoadingRing.IsActive = false;
        PreviewLoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowProgressOverlay(string message, int percent)
    {
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
    }

    private void SetPerFileToolbarVisibility(Visibility visibility)
    {
        CopyPreviewFilePathButton.Visibility = visibility;
        PreviewToolbarSeparator.Visibility = visibility;
        FullFileButton.Visibility = visibility;
        OpenInDefaultAppButton.Visibility = visibility;
        OpenInEditorButton.Visibility = visibility;
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
        LogService.Instance.Verbose("Preview", $"AddPreviewSection: file='{System.IO.Path.GetFileName(filePath)}', detail='{detail}', expanded={isExpanded}, addToPanel={addToPanel}");
        bool wrap = ViewModel.PreviewWrapModeIndex == (int)Models.PreviewWrapMode.Wrap;
        var block = new RichTextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsTextSelectionEnabled = true,
            Tag = filePath,
        };
        AttachPreviewBlockContextFlyout(block);

        var gutterBlock = new RichTextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsTextSelectionEnabled = false,
        };
        _sectionGutterBlocks[block] = gutterBlock;

        // Sync gutter paragraph heights after layout so the line numbers stay
        // aligned with content paragraphs that wrap to multiple visual lines.
        block.SizeChanged += (_, _) => ScheduleGutterSync(block);

        var content = new Border
        {
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
        var sectionGrid = new Grid();
        sectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gutterBorder = new Border
        {
            Padding = new Thickness(8, 0, 0, 0),
            Child = gutterBlock,
        };
        Grid.SetColumn(gutterBorder, 0);
        sectionGrid.Children.Add(gutterBorder);
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
                    UpdateExpandAllButtonVisibility();
                    if (await MaterializeLazySectionAsync(b) && MatchNavPanel.Visibility == Visibility.Visible)
                        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
                    // Re-apply the current wrap state in case the user toggled wrap while
                    // this section was collapsed (we skip collapsed sections in
                    // ApplyWordWrapAsync to keep the toggle responsive for huge previews).
                    var wrap = ViewModel.PreviewWordWrap;
                    b.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    if (exp.Content is Grid eg && eg.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer scroller)
                        ApplyPreviewHorizontalScrollForWrapSection(scroller, wrap);
                    else if (exp.Content is ScrollViewer scroller2)
                        ApplyPreviewHorizontalScrollForWrapSection(scroller2, wrap);
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
            RegisterSectionMatchTotal(block, results.Count);
        _blockExpanderCache[block] = expander;
        _expanderFilePaths[expander] = filePath;
        _expanderHeaderArgs[expander] = (filePath, detail, block, results);
        ApplyPreviewSectionContentBackground(expander, ReferenceEquals(_activeSectionNav?.Block, block));
        if (addToPanel)
        {
            PreviewSectionsPanel.Children.Add(expander);
            InvalidateScrollPositionCache();
            _lastHighlightedActiveBlock = null;
        }
        return (block, expander);
    }

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

        var infoPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        infoPanel.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });

        infoPanel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(filePath),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 360,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            infoPanel.Children.Add(new TextBlock
            {
                Text = detail,
                Opacity = 0.65,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        grid.Children.Add(infoPanel);

        // Per-file action buttons — right-aligned
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(buttonPanel, 1);

        var path = filePath; // capture for lambdas

        if (sectionBlock is not null && sectionResults is not null)
        {
            var fullFileBtn = new Button
            {
                Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                Content = new FontIcon { Glyph = "\uE81E", FontSize = 12 },
            };
            ToolTipService.SetToolTip(fullFileBtn, "Show full file");
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
        };
        ToolTipService.SetToolTip(editorBtn, "Edit file");
        editorBtn.Click += async (_, _) =>
        {
            var result = ViewModel.ResultGroups
                .FirstOrDefault(g => string.Equals(g.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?.FirstOrDefault();
            if (result is not null)
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
            Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
            ShowPreviewSectionsSurface();
            SetPreviewMatchTotals(targets.Sum(t => t.Matches.Count), targets.Count);

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
                    LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: loading file '{System.IO.Path.GetFileName(target.FilePath)}', matches={target.Matches.Count}");
                    var document = await LoadPreviewDocumentAsync(target.FilePath, cts.Token, fileSizeLimit: EffectiveFullFilePreviewLimitBytes).ConfigureAwait(true);
                    LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: loaded '{System.IO.Path.GetFileName(target.FilePath)}', bytes={document.ByteLength:N0}, textLen={document.Text.Length:N0}");
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
        RegisterSectionMatchTotal(block, target.Matches.Count);
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
        var renderSw = System.Diagnostics.Stopwatch.StartNew();
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
        LogService.Instance.Info("Preview", $"RenderFullFileDocument: file='{System.IO.Path.GetFileName(target.FilePath)}', lines={lineNumber - 1}, matches={matchByLine.Count}, blocks={section.Blocks.Count}, elapsed={renderSw.ElapsedMilliseconds}ms");

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
        if (sender is RichTextBlock rtb)
            await EnterPreviewEditorAtPointAsync(rtb, e, _previewResult?.FilePath);
    }

    // EnterPreviewEditorAtPointAsync, ResolveLineNumberAtPointer, ShowFullFileEditorAsync,
    // and ScrollEditorToMatch moved to MainWindow.PreviewEditor.cs.

    private static async Task<PreviewTextDocument> LoadPreviewDocumentAsync(string filePath, CancellationToken cancellationToken, bool enforceLimit = true, long fileSizeLimit = 0)
    {
        long effectiveLimit = fileSizeLimit > 0 ? fileSizeLimit : 1L * 1024 * 1024 * 1024;
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Instance.Verbose("Preview", $"LoadPreviewDocumentAsync: start file='{System.IO.Path.GetFileName(filePath)}'");

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
            LogService.Instance.Info("Preview", $"LoadPreviewDocumentAsync: done file='{System.IO.Path.GetFileName(filePath)}', bytes={info.Length:N0}, textLen={text.Length:N0}, maxLineLen={maxLineLength:N0}, elapsed={loadSw.ElapsedMilliseconds}ms");
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

    private static Regex? BuildHighlightRegex(string query, bool caseSensitive, bool useRegex, bool exactMatch = true)
    {
        if (string.IsNullOrEmpty(query)) return null;
        try
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            string? pattern = useRegex ? query : SearchQueryParser.BuildLiteralRegexPattern(query, exactMatch);
            if (string.IsNullOrEmpty(pattern)) return null;
            return new Regex(pattern, options);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", "Invalid highlight regex", ex);
            return null;
        }
    }

    private SolidColorBrush s_matchGutterBrush = new(Microsoft.UI.Colors.LimeGreen);

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

    private SolidColorBrush s_contextGutterBrush = new(Windows.UI.Color.FromArgb(255, 80, 80, 80));
    private static readonly SolidColorBrush s_gutterSepBrush = new(Windows.UI.Color.FromArgb(255, 60, 60, 60));
    private SolidColorBrush s_contextTextBrush = new(Windows.UI.Color.FromArgb(255, 110, 110, 110));
    private SolidColorBrush s_matchAccentBrush = new(Windows.UI.Color.FromArgb(255, 70, 140, 70));
    private static readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);
    private SolidColorBrush _matchTextBrush = new(Microsoft.UI.Colors.Gold);
    private SolidColorBrush _matchLineBrush = new(Microsoft.UI.Colors.White);
    private Windows.UI.Color _overlayColor = Microsoft.UI.Colors.OrangeRed;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, object> s_paragraphLineNumbers = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, object> s_paragraphPrimaryResults = new();
    /// <summary>Marks paragraphs that are continuation segments of a long source line (no leading line number gutter).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, object> s_paragraphIsContinuation = new();

    private void ApplyPreviewColors()
    {
        var vm = ViewModel;
        s_contextGutterBrush = new SolidColorBrush(ColorStringHelper.Parse(vm.PreviewGutterContextColor, Windows.UI.Color.FromArgb(0xFF, 0x50, 0x50, 0x50)));
        s_matchGutterBrush = new SolidColorBrush(ColorStringHelper.Parse(vm.PreviewGutterMatchColor, Windows.UI.Color.FromArgb(0xFF, 0x32, 0xCD, 0x32)));
        var matchTextColor = ColorStringHelper.Parse(vm.PreviewMatchTextColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        _matchTextBrush = new SolidColorBrush(matchTextColor);
        _overlayColor = ColorStringHelper.Parse(vm.PreviewOverlayColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x00));
        _matchLineBrush = new SolidColorBrush(ColorStringHelper.Parse(vm.PreviewMatchLineColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
        s_contextTextBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 110, 110, 110));
        s_matchAccentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 140, 70));

        // Update overlay elements
        ActiveMatchBand.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, _overlayColor.R, _overlayColor.G, _overlayColor.B));
        ActiveMatchBand.BorderBrush = new SolidColorBrush(_overlayColor);
        ActiveMatchWordMarker.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, _overlayColor.R, _overlayColor.G, _overlayColor.B));
        ActiveMatchWordMarker.BorderBrush = new SolidColorBrush(_overlayColor);
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
        out int paragraphsAdded)
    {
        line ??= string.Empty;
        if (truncate)
            line = isMatchLine ? TruncatePreviewLineAroundResult(line, result, rx).Text : TruncatePreviewLine(line, rx);

        _sectionGutterBlocks.TryGetValue(section, out var gutterBlock);

        Paragraph? firstParagraph = null;
        bool addedMatchEntries = false;
        paragraphsAdded = 0;

        foreach (var segment in EnumeratePreviewLineLayoutSegments(line))
        {
            bool isContinuation = firstParagraph is not null;
            var para = MakePreviewParagraph(segment, lineNum, isMatchLine, result, rx, truncate: false, continuationGutter: isContinuation, gutterBlock: gutterBlock);
            section.Blocks.Add(para);
            firstParagraph ??= para;
            paragraphsAdded++;

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

        return firstParagraph ?? throw new InvalidOperationException("Preview line renderer did not create a paragraph.");
    }

    private readonly record struct PreviewLineWindow(string Text, int SourceStart, int SourceEnd);

    private static PreviewLineWindow TruncatePreviewLineAroundResult(string? line, SearchResult result, Regex? rx)
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

        if (LineTruncator.TruncatedLength == 0
            || line.Length <= LineTruncator.MaxDisplayLength
            || matchStart < 0
            || matchLength <= 0
            || matchStart >= line.Length)
        {
            return new PreviewLineWindow(LineTruncator.Truncate(line), 0, line.Length);
        }

        int safeMatchLength = Math.Min(matchLength, line.Length - matchStart);
        int visibleMatchLength = Math.Min(safeMatchLength, LineTruncator.TruncatedLength);
        int contextChars = Math.Max(0, (LineTruncator.TruncatedLength - visibleMatchLength) / 2);

        int start = Math.Max(0, matchStart - contextChars);
        int end = Math.Min(line.Length, start + LineTruncator.TruncatedLength);
        if (end - start < LineTruncator.TruncatedLength)
            start = Math.Max(0, end - LineTruncator.TruncatedLength);

        string prefix = start > 0 ? LineTruncator.Ellipsis : string.Empty;
        string suffix = end < line.Length ? LineTruncator.Ellipsis : string.Empty;
        string text = string.Concat(prefix, line.AsSpan(start, end - start), suffix);
        return new PreviewLineWindow(text, start, end);
    }

    private static int ResolveSourceMatchStart(string line, SearchResult result, Regex? rx)
    {
        int candidate = result.MatchStartColumn;
        if (IsSourceMatchAt(line, candidate, result.MatchLength, rx))
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
        bool continuationGutter = false)
    {
        line ??= string.Empty;
        var window = truncate
            ? TruncatePreviewLineAroundResult(line, result, rx)
            : new PreviewLineWindow(line, 0, line.Length);
        _sectionGutterBlocks.TryGetValue(section, out var gutterBlock);
        Paragraph? firstParagraph = null;
        bool addedMatchEntries = false;
        paragraphsAdded = 0;
        matchEntriesAdded = 0;

        foreach (var segment in EnumeratePreviewLineLayoutSegments(window.Text))
        {
            bool isContinuation = firstParagraph is not null || continuationGutter;
            var para = MakePreviewParagraph(segment, lineNum, isMatchLine: true, result, rx, truncate: false, continuationGutter: isContinuation, gutterBlock: gutterBlock);
            section.Blocks.Add(para);
            firstParagraph ??= para;
            paragraphsAdded++;

            if (matchParagraphs is null)
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
            int added = matchParagraphs.Count - beforeCount;
            if (added > 0)
            {
                addedMatchEntries = true;
                matchEntriesAdded += added;
            }
        }

        if (matchParagraphs is not null && !addedMatchEntries && firstParagraph is not null)
        {
            int beforeCount = matchParagraphs.Count;
            AddMatchEntries(matchParagraphs, sectionNav, section, firstParagraph, string.Empty, rx: null);
            matchEntriesAdded += matchParagraphs.Count - beforeCount;
        }

        return firstParagraph ?? throw new InvalidOperationException("Preview line renderer did not create a paragraph.");
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

        int anchorLimit = Math.Min(pendingResults.Count, maxResultsToConsume);
        var ranges = new List<(int start, int end)>();
        for (int i = 0; i < anchorLimit; i++)
        {
            var result = pendingResults[i];
            int lineIndex = result.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= allLines.Length)
                continue;

            int start = Math.Max(0, lineIndex - previewLines);
            int end = Math.Min(allLines.Length - 1, lineIndex + previewLines);
            // previouslyRenderedLine is 1-indexed; convert to 0-indexed for array comparisons
            int previouslyRenderedIndex = previouslyRenderedLine > 0 ? previouslyRenderedLine - 1 : -1;
            if (end <= previouslyRenderedIndex)
                continue;

            start = Math.Max(start, previouslyRenderedIndex + 1);
            if (ranges.Count > 0 && start <= ranges[^1].end + 1)
                ranges[^1] = (ranges[^1].start, Math.Max(ranges[^1].end, end));
            else
                ranges.Add((start, end));
        }

        if (ranges.Count == 0)
        {
            // All pending results are on lines already rendered (usually multiple
            // selected matches on one very long/truncated line). Add separate
            // windows around each result so navigation/highlighting stays one
            // entry per selected match instead of collapsing by line number.
            int maxResults = Math.Min(anchorLimit, pendingResults.Count);
            if (!truncatePreviewLines)
            {
                consumedResults = maxResults;
                for (int i = 0; i < maxResults; i++)
                    lastRenderedLine = Math.Max(lastRenderedLine, pendingResults[i].LineNumber);
                return 0;
            }

            AddGapIndicator(section);
            paragraphsAdded++;

            for (int i = 0; i < maxResults; i++)
            {
                if (addedMatchEntries >= maxAdditionalMatchEntries || paragraphsAdded >= maxAdditionalBlocks)
                    break;

                var result = pendingResults[i];
                int lineIndex = result.LineNumber - 1;
                if (lineIndex < 0 || lineIndex >= allLines.Length)
                    continue;

                if (paragraphsAdded >= maxAdditionalBlocks)
                    break;

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
                    continuationGutter: true);

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

            if (hasExistingRenderedLines && start > lastRenderedLine)
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
                    out int addedParagraphs);

                paragraphsAdded += addedParagraphs;
                if (isMatchLine)
                    addedMatchEntries += _matchParagraphs.Count - beforeCount;
                lastRenderedLine = lineNumber;
            }
        }

        consumedResults = CountPrefixResultsThroughLine(pendingResults, lastRenderedLine, allLines.Length);

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

    private Paragraph MakePreviewParagraph(string line, int lineNum, bool isMatchLine, SearchResult r, Regex? rx, bool truncate = true, bool continuationGutter = false, RichTextBlock? gutterBlock = null)
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
        var indicator = new Run { Text = continuationGutter ? " " : (isMatchLine ? "│" : " ") };
        indicator.Foreground = isMatchLine ? s_matchAccentBrush : s_contextTextBrush;

        var gutterRun = new Run { Text = continuationGutter ? "      " : $"{lineNum,5} " };
        gutterRun.Foreground = continuationGutter ? s_contextGutterBrush : (isMatchLine ? s_matchGutterBrush : s_contextGutterBrush);
        var gutterSep = new Run { Text = "│ " };
        gutterSep.Foreground = s_gutterSepBrush;

        if (gutterBlock is not null)
        {
            // Emit gutter to the separate gutter block.
            var gutterPara = new Paragraph();
            gutterPara.Inlines.Add(indicator);
            gutterPara.Inlines.Add(gutterRun);
            gutterPara.Inlines.Add(gutterSep);
            gutterBlock.Blocks.Add(gutterPara);
        }
        else
        {
            // Inline gutter (legacy path for PreviewBlock single-file view).
            para.Inlines.Add(indicator);
            para.Inlines.Add(gutterRun);
            para.Inlines.Add(gutterSep);
        }

        // Keep gutter/nav semantics tied to actual match lines, but color every
        // visible regex hit yellow so context lines don't show unhighlighted matches.
        if (rx != null)
        {
            int lastIdx = 0;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(line))
            {
                if (m.Index > lastIdx)
                {
                    var before = new Run { Text = line[lastIdx..m.Index] };
                    if (!isMatchLine) before.Foreground = s_contextTextBrush;
                    else before.Foreground = _matchLineBrush;
                    para.Inlines.Add(before);
                }
                var hit = new Run { Text = m.Value };
                hit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                hit.Foreground = _matchTextBrush;
                para.Inlines.Add(hit);
                lastIdx = m.Index + m.Length;
            }
            if (lastIdx < line.Length)
            {
                var tail = new Run { Text = line[lastIdx..] };
                if (!isMatchLine) tail.Foreground = s_contextTextBrush;
                else tail.Foreground = _matchLineBrush;
                para.Inlines.Add(tail);
            }
        }
        else
        {
            var plain = new Run { Text = line };
            if (!isMatchLine) plain.Foreground = s_contextTextBrush;
            else plain.Foreground = _matchLineBrush;
            para.Inlines.Add(plain);
        }

        return para;
    }

    private static string TruncatePreviewLine(string line, Regex? rx)
    {
        if (rx is not null)
        {
            var match = rx.Match(line);
            if (match.Success && match.Length > 0)
                return LineTruncator.TruncateAroundMatch(line, match.Index, match.Length).Text;
        }

        return LineTruncator.Truncate(line);
    }

}
