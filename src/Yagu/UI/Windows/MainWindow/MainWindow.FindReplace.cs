using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yagu.Services;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu;

/// <summary>
/// Find bar, replace, and replace-in-all-files logic.
/// </summary>
public sealed partial class MainWindow
{
    private string? _previewEditorFindHighlightNeedle;
    private bool _previewEditorFindHighlightMatchCase;
    private int _previewEditorActiveFindSelectionVersion;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewEditorActiveFindSelectionRetryTimer;
    private int _replaceAllFilesOperationVersion;

    private bool _findBarDragging;
    private Windows.Foundation.Point _findBarDragStart;
    private double _findBarTranslateStartX;
    private double _findBarTranslateStartY;

    // Opacity applied to the find modal surface when focus is inside vs. elsewhere.
    private const double FindBarActiveOpacity = 1.0;
    private const double FindBarInactiveSurfaceOpacity = 0.45;
    private const double FindBarInactiveCloseOpacity = 0.85;
    private bool _findBarFocusHandlersHooked;

    private void OnOpenFindReplaceBar(object sender, RoutedEventArgs e)
    {
        OpenFindBar(showReplace: true);
    }

    private void OpenFindBar(bool showReplace)
    {
        // Reset the floating modal to its default anchored position so it is
        // always fully visible when (re)opened, even after window resizes.
        if (FindBarTranslate is not null)
        {
            FindBarTranslate.X = 0;
            FindBarTranslate.Y = 0;
        }

        FindBar.Visibility = Visibility.Visible;
        HookFindBarFocusHandlers();
        // Opaque on open; it only dims once focus moves outside the modal.
        SetFindBarActive(true);
        bool inEditor = PreviewEditor.Visibility == Visibility.Visible;
        LogFindVerbose($"OpenFindBar: showReplace={showReplace}, {FindSurfaceDescription()}, selectedTextLength={(inEditor ? PreviewEditor.SelectedText.Length : 0)}");
        if (showReplace)
        {
            ReplaceRow.Visibility = Visibility.Visible;
            FindReplaceToggle.IsChecked = true;
            ReplaceOneButton.IsEnabled = inEditor;
            ReplaceAllButton.IsEnabled = inEditor;
            ReplaceInFilesButton.IsEnabled = ViewModel.HasResults;
        }

        // Pre-fill with selected text from the editor
        if (PreviewEditor.Visibility == Visibility.Visible && PreviewEditor.SelectedText.Length > 0 && !PreviewEditor.SelectedText.Contains('\n'))
            FindTextBox.Text = PreviewEditor.SelectedText;

        SyncPreviewEditorFindHighlights();
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    private void OnCloseFindBar(object sender, RoutedEventArgs e)
    {
        CloseFindBar();
    }

    private void CloseFindBar()
    {
        LogFindVerbose($"CloseFindBar: previousIndex={_findIndex}, {FindSurfaceDescription()}");
        CancelPreviewEditorActiveFindSelectionRefresh();
        FindBar.Visibility = Visibility.Collapsed;
        ReplaceRow.Visibility = Visibility.Collapsed;
        FindReplaceToggle.IsChecked = false;
        _findIndex = -1;
        FindStatusText.Text = string.Empty;

        // Clear any preview block highlight
        if (_findHighlightBlock is not null)
        {
            _findHighlightBlock.TextHighlighters.Clear();
            _findHighlightBlock = null;
        }
        ClearPreviewEditorFindHighlights();

        // Return focus to the editor or preview
        if (PreviewEditor.Visibility == Visibility.Visible)
            PreviewEditor.Focus(FocusState.Programmatic);
    }

    private void CancelPreviewEditorActiveFindSelectionRefresh()
    {
        _previewEditorActiveFindSelectionVersion++;
        _previewEditorActiveFindSelectionRetryTimer?.Stop();
        _previewEditorActiveFindSelectionRetryTimer = null;
    }

    private void OnFindBarDragPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement handle)
            return;

        _findBarDragStart = e.GetCurrentPoint(PreviewContentHost).Position;
        _findBarTranslateStartX = FindBarTranslate.X;
        _findBarTranslateStartY = FindBarTranslate.Y;
        _findBarDragging = handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnFindBarDragPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_findBarDragging)
            return;

        var current = e.GetCurrentPoint(PreviewContentHost).Position;
        double newX = _findBarTranslateStartX + (current.X - _findBarDragStart.X);
        double newY = _findBarTranslateStartY + (current.Y - _findBarDragStart.Y);

        // Keep the modal within the preview content bounds. The bar is anchored
        // top-right with a 12/56px margin, so X grows negative as it moves left.
        const double marginRight = 12;
        const double marginTop = 56;
        double hostWidth = PreviewContentHost.ActualWidth;
        double hostHeight = PreviewContentHost.ActualHeight;
        double barWidth = FindBar.ActualWidth;
        double barHeight = FindBar.ActualHeight;

        double minX = -Math.Max(0, hostWidth - barWidth - marginRight);
        double maxX = marginRight;
        double minY = -marginTop;
        double maxY = Math.Max(minY, hostHeight - barHeight - marginTop);

        FindBarTranslate.X = Math.Clamp(newX, Math.Min(minX, maxX), Math.Max(minX, maxX));
        FindBarTranslate.Y = Math.Clamp(newY, Math.Min(minY, maxY), Math.Max(minY, maxY));
        e.Handled = true;
    }

    private void OnFindBarDragPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_findBarDragging)
            return;

        _findBarDragging = false;
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // Subscribes (once) to focus changes so the modal can dim when focus moves
    // out of it and become fully opaque again when focus returns.
    private void HookFindBarFocusHandlers()
    {
        if (_findBarFocusHandlersHooked || FindBar is null)
            return;

        FindBar.GotFocus += OnFindBarGotFocus;
        FindBar.LostFocus += OnFindBarLostFocus;
        _findBarFocusHandlersHooked = true;
    }

    private void OnFindBarGotFocus(object sender, RoutedEventArgs e)
    {
        SetFindBarActive(true);
    }

    private void OnFindBarLostFocus(object sender, RoutedEventArgs e)
    {
        // The focused element updates after this event; re-check on the next tick.
        DispatcherQueue.TryEnqueue(() => SetFindBarActive(IsFocusWithinFindBar()));
    }

    private bool IsFocusWithinFindBar()
    {
        if (FindBar is null || FindBar.XamlRoot is null)
            return false;

        if (FocusManager.GetFocusedElement(FindBar.XamlRoot) is not DependencyObject focused)
            return false;

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, FindBar))
                return true;
        }

        return false;
    }

    private void SetFindBarActive(bool active)
    {
        if (FindBarSurface is not null)
            FindBarSurface.Opacity = active ? FindBarActiveOpacity : FindBarInactiveSurfaceOpacity;
        if (FindCloseButton is not null)
            FindCloseButton.Opacity = active ? FindBarActiveOpacity : FindBarInactiveCloseOpacity;
    }

    private void OnFindReplaceToggle(object sender, RoutedEventArgs e)
    {
        bool show = FindReplaceToggle.IsChecked == true;
        LogFindVerbose($"FindReplaceToggle: showReplace={show}, {FindSurfaceDescription()}");
        ReplaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Single-file replace buttons only make sense in the editor
        bool inEditor = PreviewEditor.Visibility == Visibility.Visible;
        ReplaceOneButton.IsEnabled = inEditor;
        ReplaceAllButton.IsEnabled = inEditor;
        ReplaceInFilesButton.IsEnabled = ViewModel.HasResults;
    }

    private void OnFindTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                             .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) FindPrevious(focusEditor: false); else FindNext(focusEditor: false);
            e.Handled = true;
        }
    }

    private void OnReplaceTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter) { ReplaceOne(focusEditor: false); e.Handled = true; }
    }

    private void OnFindTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _findIndex = -1; // reset so next find starts from current selection
        LogFindVerbose($"FindTextChanged: needle={DescribeFindText(FindTextBox.Text)}, resetIndex=true, {FindSurfaceDescription()}");
        SyncPreviewEditorFindHighlights();
        UpdateFindStatus();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _findIndex = -1;
        LogFindVerbose($"FindOptionChanged: matchCase={FindMatchCaseCheckBox.IsChecked == true}, needle={DescribeFindText(FindTextBox.Text)}, {FindSurfaceDescription()}");
        SyncPreviewEditorFindHighlights();
        UpdateFindStatus();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindPrevious();
    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();
    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    private StringComparison FindComparison =>
        FindMatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private string FindTarget => PreviewEditor.Visibility == Visibility.Visible ? GetPreviewEditorText() : GetPreviewBlockText();

    private static void LogFindVerbose(string message)
    {
        if (LogService.Instance.IsVerboseEnabled)
            YaguLog.For("FindReplace").LogDebug("{Message}", message);
    }

    private string FindSurfaceDescription()
    {
        if (PreviewEditor.Visibility == Visibility.Visible)
        {
            return $"surface=editor, wordWrap={PreviewEditor.WordWrap}, searchOpen={PreviewEditor.SearchIsOpen}, selection={DescribePreviewEditorSelection()}";
        }

        return $"surface=preview, sectionsVisible={PreviewSectionsPanel.Visibility == Visibility.Visible}, previewWrap={ViewModel.PreviewWordWrap}";
    }

    private string DescribePreviewEditorSelection()
    {
        try
        {
            var selection = PreviewEditor.CurrentSelectionOrdered;
            return selection is { } s
                ? $"{s.StartLinePos}:{s.StartCharacterPos}-{s.EndLinePos}:{s.EndCharacterPos}"
                : "<none>";
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string DescribeFindText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "<empty>";
        var escaped = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        if (escaped.Length > 80)
            escaped = escaped[..80] + "...";
        return $"'{escaped}' len={text.Length}";
    }

    private static int GetFindLineNumber(string text, int index)
    {
        int clamped = Math.Clamp(index, 0, text.Length);
        int line = 1;
        for (int i = 0; i < clamped; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private string GetPreviewBlockText()
    {
        var sb = new StringBuilder();
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            foreach (var block in EnumeratePreviewSectionBlocks())
                AppendBlockText(block, sb);
        }
        else
        {
            AppendBlockText(PreviewBlock, sb);
        }
        return sb.ToString();
    }

    private static void AppendBlockText(RichTextBlock richBlock, StringBuilder sb)
    {
        foreach (var block in richBlock.Blocks)
        {
            if (block is Microsoft.UI.Xaml.Documents.Paragraph p)
            {
                foreach (var inline in p.Inlines)
                {
                    if (inline is Microsoft.UI.Xaml.Documents.Run run) sb.Append(run.Text);
                    else if (inline is Microsoft.UI.Xaml.Documents.Span span)
                    {
                        foreach (var inner in span.Inlines)
                        {
                            if (inner is Microsoft.UI.Xaml.Documents.Run innerRun) sb.Append(innerRun.Text);
                        }
                    }
                }
                sb.AppendLine();
            }
        }
    }

    private void FindNext(bool focusEditor = true)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            LogFindVerbose($"FindNext: ignored empty needle, {FindSurfaceDescription()}");
            return;
        }
        var haystack = FindTarget;
        if (haystack.Length == 0)
        {
            LogFindVerbose($"FindNext: no content, needle={DescribeFindText(needle)}, {FindSurfaceDescription()}");
            FindStatusText.Text = "No content";
            return;
        }

        int previousIndex = _findIndex;
        int startPos = _findIndex >= 0 ? _findIndex + needle.Length : 0;
        if (startPos >= haystack.Length) startPos = 0;

        int idx = haystack.IndexOf(needle, startPos, FindComparison);
        bool wrapped = false;
        if (idx < 0 && startPos > 0)
        {
            wrapped = true;
            idx = haystack.IndexOf(needle, 0, FindComparison); // wrap around
        }

        if (idx < 0)
        {
            LogFindVerbose($"FindNext: no match, needle={DescribeFindText(needle)}, haystackLen={haystack.Length}, previousIndex={previousIndex}, startPos={startPos}, wrapped={wrapped}, {FindSurfaceDescription()}");
            FindStatusText.Text = "No matches";
            _findIndex = -1;
            return;
        }

        LogFindVerbose($"FindNext: found, needle={DescribeFindText(needle)}, haystackLen={haystack.Length}, previousIndex={previousIndex}, startPos={startPos}, resultIndex={idx}, resultLine={GetFindLineNumber(haystack, idx)}, wrapped={wrapped}, {FindSurfaceDescription()}");

        _findIndex = idx;
        SelectFindMatch(idx, needle.Length, focusEditor);
        UpdateFindStatus();
    }

    private void FindPrevious(bool focusEditor = true)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            LogFindVerbose($"FindPrevious: ignored empty needle, {FindSurfaceDescription()}");
            return;
        }
        var haystack = FindTarget;
        if (haystack.Length == 0)
        {
            LogFindVerbose($"FindPrevious: no content, needle={DescribeFindText(needle)}, {FindSurfaceDescription()}");
            FindStatusText.Text = "No content";
            return;
        }

        int previousIndex = _findIndex;
        int startPos = _findIndex > 0 ? _findIndex - 1 : haystack.Length - 1;

        // Search backwards by scanning substring before startPos
        int idx = haystack.LastIndexOf(needle, startPos, FindComparison);
        bool wrapped = false;
        if (idx < 0 && startPos < haystack.Length - 1)
        {
            wrapped = true;
            idx = haystack.LastIndexOf(needle, haystack.Length - 1, FindComparison); // wrap around
        }

        if (idx < 0)
        {
            LogFindVerbose($"FindPrevious: no match, needle={DescribeFindText(needle)}, haystackLen={haystack.Length}, previousIndex={previousIndex}, startPos={startPos}, wrapped={wrapped}, {FindSurfaceDescription()}");
            FindStatusText.Text = "No matches";
            _findIndex = -1;
            return;
        }

        LogFindVerbose($"FindPrevious: found, needle={DescribeFindText(needle)}, haystackLen={haystack.Length}, previousIndex={previousIndex}, startPos={startPos}, resultIndex={idx}, resultLine={GetFindLineNumber(haystack, idx)}, wrapped={wrapped}, {FindSurfaceDescription()}");

        _findIndex = idx;
        SelectFindMatch(idx, needle.Length, focusEditor);
        UpdateFindStatus();
    }

    private void SelectFindMatch(int index, int length, bool focusEditor = true)
    {
        LogFindVerbose($"SelectFindMatch: index={index}, length={length}, before={FindSurfaceDescription()}");
        if (PreviewEditor.Visibility == Visibility.Visible)
        {
            SyncPreviewEditorFindHighlights();
            if (focusEditor)
                PreviewEditor.Focus(FocusState.Programmatic);
            SelectPreviewEditorText(index, length);
            int line = ScrollPreviewEditorMatchIntoView(index);
            QueuePreviewEditorActiveFindSelectionRefresh(index, length, line);
            LogFindVerbose($"SelectFindMatch: editor selected, index={index}, length={length}, after={FindSurfaceDescription()}");
        }
        else
        {
            HighlightFindMatchInPreviewBlock(index, length);
        }
    }

    /// <summary>
    /// Centers the editor viewport on the line containing the given character index.
    /// TextControlBox's <c>SetSelection</c> does not auto-scroll, so navigation through
    /// matches would otherwise leave the next match off-screen.
    /// </summary>
    private int ScrollPreviewEditorMatchIntoView(int index)
    {
        try
        {
            int line = GetPreviewEditorLineForIndex(index);
            PreviewEditor.ScrollLineToCenter(line);
            LogFindVerbose($"ScrollPreviewEditorMatchIntoView: index={index}, line={line}, wordWrap={PreviewEditor.WordWrap}");
            return line;
        }
        catch (Exception ex)
        {
            LogFindVerbose($"ScrollPreviewEditorMatchIntoView failed: index={index}, error={ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private int GetPreviewEditorLineForIndex(int index)
    {
        var text = GetPreviewEditorText();
        int clamped = Math.Clamp(index, 0, text.Length);
        int line = 0;
        for (int i = 0; i < clamped; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private void QueuePreviewEditorActiveFindSelectionRefresh(int index, int length, int line)
    {
        int version = ++_previewEditorActiveFindSelectionVersion;

        void Refresh(string source)
        {
            if (version != _previewEditorActiveFindSelectionVersion)
                return;
            if (PreviewEditor.Visibility != Visibility.Visible || FindBar.Visibility != Visibility.Visible)
                return;
            if (_findIndex != index)
                return;

            try
            {
                PreviewEditor.ScrollLineToCenter(line);
                SelectPreviewEditorText(index, length);
                LogFindVerbose($"RefreshActiveEditorFindSelection: source={source}, index={index}, length={length}, line={line}, {FindSurfaceDescription()}");
            }
            catch (Exception ex)
            {
                LogFindVerbose($"RefreshActiveEditorFindSelection failed: source={source}, index={index}, error={ex.GetType().Name}: {ex.Message}");
            }
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Refresh("dispatcher"));

        _previewEditorActiveFindSelectionRetryTimer?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        _previewEditorActiveFindSelectionRetryTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(75);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_previewEditorActiveFindSelectionRetryTimer, timer))
                _previewEditorActiveFindSelectionRetryTimer = null;
            Refresh("timer");
        };
        timer.Start();
    }

    /// <summary>
    /// Clears any previous find highlight, maps the global text index to a
    /// specific RichTextBlock section, applies a TextHighlighter, and scrolls
    /// the match into view.
    /// </summary>
    private void HighlightFindMatchInPreviewBlock(int globalIndex, int length)
    {
        // Clear previous highlight
        if (_findHighlightBlock is not null)
        {
            _findHighlightBlock.TextHighlighters.Clear();
            _findHighlightBlock = null;
        }

        if (PreviewSectionsPanel.Visibility != Visibility.Visible && PreviewBlock.Visibility != Visibility.Visible)
            return;

        // Determine which block(s) to search.
        var blocks = PreviewSectionsPanel.Visibility == Visibility.Visible
            ? EnumeratePreviewSectionBlocks().ToList()
            : new List<RichTextBlock> { PreviewBlock };

        // Walk blocks counting chars (matching GetPreviewBlockText's output) to
        // find which block the match is in and compute the block-local offset.
        int offset = 0;
        foreach (var block in blocks)
        {
            int blockSearchLen = 0; // length in the search text and RichTextBlock text model (\r\n separators)
            int blockTextLen = 0;

            foreach (var b in block.Blocks)
            {
                if (b is not Microsoft.UI.Xaml.Documents.Paragraph p) continue;
                int paraLen = GetParagraphTextLength(p);
                int searchParaLen = paraLen + 2; // paragraph text + \r\n

                if (offset + blockSearchLen + searchParaLen > globalIndex && globalIndex >= offset + blockSearchLen)
                {
                    // Match starts in this paragraph
                    int localOffset = blockTextLen + (globalIndex - offset - blockSearchLen);
                    ApplyFindHighlighter(block, localOffset, length);
                    ScrollFindMatchIntoView(block, p);
                    return;
                }

                blockSearchLen += searchParaLen;
                blockTextLen += searchParaLen;
            }

            // Check if match starts in this block but spans across paragraphs
            if (globalIndex >= offset && globalIndex < offset + blockSearchLen)
            {
                int localOffset = MapSearchOffsetToBlockOffset(block, globalIndex - offset);
                ApplyFindHighlighter(block, localOffset, length);
                ScrollFindMatchIntoView(block, FindParagraphAtSearchOffset(block, globalIndex - offset));
                return;
            }

            offset += blockSearchLen;
        }
    }

    private static int GetParagraphTextLength(Microsoft.UI.Xaml.Documents.Paragraph p)
    {
        int len = 0;
        foreach (var inline in p.Inlines)
        {
            if (inline is Microsoft.UI.Xaml.Documents.Run run)
                len += run.Text?.Length ?? 0;
            else if (inline is Microsoft.UI.Xaml.Documents.Span span)
            {
                foreach (var inner in span.Inlines)
                {
                    if (inner is Microsoft.UI.Xaml.Documents.Run innerRun)
                        len += innerRun.Text?.Length ?? 0;
                }
            }
        }
        return len;
    }

    private static int MapSearchOffsetToBlockOffset(RichTextBlock block, int searchOffset)
    {
        int searchPos = 0;
        int blockPos = 0;
        foreach (var b in block.Blocks)
        {
            if (b is not Microsoft.UI.Xaml.Documents.Paragraph p) continue;
            int paraLen = GetParagraphTextLength(p);
            if (searchOffset < searchPos + paraLen)
                return blockPos + (searchOffset - searchPos);
            searchPos += paraLen + 2; // \r\n
            blockPos += paraLen + 2;  // \r\n
        }
        return blockPos;
    }

    private void ApplyFindHighlighter(RichTextBlock block, int startIndex, int length)
    {
        LogFindVerbose($"ApplyFindHighlighter: startIndex={startIndex}, length={length}, block={block.Name}, previewWrap={ViewModel.PreviewWordWrap}");
        _findHighlightBlock = block;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(130, 64, 156, 255)),
        };
        highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange
        {
            StartIndex = startIndex,
            Length = length,
        });
        block.TextHighlighters.Clear();
        block.TextHighlighters.Add(highlighter);
    }

    private void ScrollFindMatchIntoView(
        RichTextBlock block,
        Microsoft.UI.Xaml.Documents.Paragraph? paragraph)
    {
        if (paragraph is not null)
        {
            // Match navigation already has a layout-settling retry ladder for exact paragraphs.
            // Reuse it here: centering the whole RichTextBlock always computes the same offset, so
            // Next/Previous appeared inert whenever every find hit lived in one tall preview section.
            ScrollPreviewToLine(block, paragraph, forceCenter: true);
            return;
        }

        // A cross-paragraph needle can start in a separator for which no exact paragraph resolves.
        // Keep a safe block-level fallback rather than dropping navigation entirely.
        try
        {
            block.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
        catch { /* block might not be in visual tree */ }
    }

    private static Microsoft.UI.Xaml.Documents.Paragraph? FindParagraphAtSearchOffset(
        RichTextBlock block,
        int searchOffset)
    {
        int searchPos = 0;
        foreach (var candidate in block.Blocks)
        {
            if (candidate is not Microsoft.UI.Xaml.Documents.Paragraph paragraph)
                continue;
            int paragraphLength = GetParagraphTextLength(paragraph);
            if (searchOffset < searchPos + paragraphLength + 2)
                return paragraph;
            searchPos += paragraphLength + 2;
        }
        return null;
    }

    private void UpdateFindStatus()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = string.Empty; ClearPreviewEditorFindHighlights(); return; }
        var haystack = FindTarget;
        int count = 0;
        int pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, FindComparison)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        FindStatusText.Text = count == 0 ? "No matches" : $"{count} match{(count == 1 ? "" : "es")}";
        LogFindVerbose($"UpdateFindStatus: needle={DescribeFindText(needle)}, count={count}, currentIndex={_findIndex}, {FindSurfaceDescription()}");
    }

    private void SyncPreviewEditorFindHighlights(bool force = false)
    {
        if (PreviewEditor.Visibility != Visibility.Visible)
            return;

        var needle = FindTextBox.Text;
        bool matchCase = FindMatchCaseCheckBox.IsChecked == true;
        if (FindBar.Visibility != Visibility.Visible || string.IsNullOrEmpty(needle))
        {
            LogFindVerbose($"SyncPreviewEditorFindHighlights: clearing, force={force}, findBarVisible={FindBar.Visibility == Visibility.Visible}, needle={DescribeFindText(needle)}, {FindSurfaceDescription()}");
            ClearPreviewEditorFindHighlights();
            return;
        }

        if (!force
            && string.Equals(_previewEditorFindHighlightNeedle, needle, StringComparison.Ordinal)
            && _previewEditorFindHighlightMatchCase == matchCase)
        {
            LogFindVerbose($"SyncPreviewEditorFindHighlights: unchanged, force={force}, needle={DescribeFindText(needle)}, matchCase={matchCase}, {FindSurfaceDescription()}");
            return;
        }

        try
        {
            var result = PreviewEditor.BeginSearch(needle, wholeWord: false, matchCase: matchCase);
            _previewEditorFindHighlightNeedle = needle;
            _previewEditorFindHighlightMatchCase = matchCase;
            LogFindVerbose($"SyncPreviewEditorFindHighlights: BeginSearch result={result}, force={force}, needle={DescribeFindText(needle)}, matchCase={matchCase}, {FindSurfaceDescription()}");
        }
        catch (Exception ex)
        {
            YaguLog.For("Find").LogDebug(ex, "Could not update editor find highlights for '{Needle}'", needle);
        }
    }

    private void ClearPreviewEditorFindHighlights()
    {
        try
        {
            LogFindVerbose($"ClearPreviewEditorFindHighlights: before={FindSurfaceDescription()}");
            PreviewEditor.EndSearch();
            _previewEditorFindHighlightNeedle = null;
            _previewEditorFindHighlightMatchCase = false;
            LogFindVerbose($"ClearPreviewEditorFindHighlights: done, after={FindSurfaceDescription()}");
        }
        catch (Exception ex)
        {
            YaguLog.For("Find").LogDebug(ex, "Could not clear editor find highlights");
        }
    }

    private void ReplaceOne(bool focusEditor = true)
    {
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var text = GetPreviewEditorText();
        int replaceAt = _findIndex;
        LogFindVerbose($"ReplaceOne: start, needle={DescribeFindText(needle)}, replacementLen={ReplaceTextBox.Text.Length}, replaceAt={replaceAt}, textLen={text.Length}, {FindSurfaceDescription()}");
        if (replaceAt < 0
            || replaceAt + needle.Length > text.Length
            || !text.AsSpan(replaceAt, needle.Length).Equals(needle.AsSpan(), FindComparison))
        {
            replaceAt = text.IndexOf(needle, FindComparison);
            if (replaceAt < 0)
            {
                FindStatusText.Text = "No matches";
                LogFindVerbose($"ReplaceOne: no match after fallback, needle={DescribeFindText(needle)}, {FindSurfaceDescription()}");
                return;
            }
        }

        var replacement = ReplaceTextBox.Text;
        var updated = text.Remove(replaceAt, needle.Length).Insert(replaceAt, replacement);
        _suppressPreviewEditorTextChanged = true;
        LoadPreviewEditorText(updated);
        _suppressPreviewEditorTextChanged = false;
        _previewEditorDirty = true;
        _findIndex = replaceAt;
        SyncPreviewEditorFindHighlights();
        SelectFindMatch(replaceAt, replacement.Length, focusEditor);
        UpdatePreviewEditorButtons();
        FindNext(focusEditor);
        LogFindVerbose($"ReplaceOne: done, replacedAt={replaceAt}, replacementLen={replacement.Length}, {FindSurfaceDescription()}");
    }

    private void ReplaceAll()
    {
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var replacement = ReplaceTextBox.Text;
        var text = GetPreviewEditorText();
        LogFindVerbose($"ReplaceAll: start, needle={DescribeFindText(needle)}, replacementLen={replacement.Length}, textLen={text.Length}, {FindSurfaceDescription()}");
        var sb = new StringBuilder(text.Length);
        int count = 0;
        int pos = 0;
        while (true)
        {
            int idx = text.IndexOf(needle, pos, FindComparison);
            if (idx < 0) { sb.Append(text, pos, text.Length - pos); break; }
            sb.Append(text, pos, idx - pos);
            sb.Append(replacement);
            count++;
            pos = idx + needle.Length;
        }

        if (count > 0)
        {
            _suppressPreviewEditorTextChanged = true;
            LoadPreviewEditorText(sb.ToString());
            _suppressPreviewEditorTextChanged = false;
            _previewEditorDirty = true;
            UpdatePreviewEditorButtons();
        }

        _findIndex = -1;
        SyncPreviewEditorFindHighlights(force: true);
        FindStatusText.Text = count > 0 ? $"Replaced {count}" : "No matches";
        LogFindVerbose($"ReplaceAll: done, count={count}, forceSynced=true, {FindSurfaceDescription()}");
    }

    private sealed record ReplaceFilePlan(string Path, long Count);

    private sealed record ReplaceFileWriteResult(string Path, string? ReplacedText, string? Error)
    {
        public bool Written => ReplacedText is not null && Error is null;
    }

    private static ReplaceFilePlan? ScanFileForReplacementPlan(
        string path,
        string needle,
        StringComparison comparison)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            Encoding encoding = Helpers.EncodingDetector.DetectEncoding(stream);
            if (encoding is UTF8Encoding)
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            long count = Helpers.LiteralTextOperations.CountNonOverlapping(reader, needle, comparison);
            return count > 0 ? new ReplaceFilePlan(path, count) : null;
        }
        catch (Exception ex)
        {
            YaguLog.For("FindReplace").LogWarning(ex,
                "Skipped file while planning replace-all (unreadable or unprocessable): {Path}", path);
            return null;
        }
    }

    private static ReplaceFileWriteResult RewriteOneReplacementFile(
        ReplaceFilePlan plan,
        string needle,
        string replacement,
        StringComparison comparison,
        bool backupEnabled)
    {
        try
        {
            Encoding encoding;
            string original;
            using (var stream = new FileStream(plan.Path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
            {
                encoding = Helpers.EncodingDetector.DetectEncoding(stream);
                if (encoding is UTF8Encoding)
                    encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                original = reader.ReadToEnd();
                encoding = reader.CurrentEncoding;
            }

            var builder = new StringBuilder(original.Length);
            int position = 0;
            int replacements = 0;
            while (true)
            {
                int match = original.IndexOf(needle, position, comparison);
                if (match < 0)
                {
                    builder.Append(original, position, original.Length - position);
                    break;
                }
                builder.Append(original, position, match - position);
                builder.Append(replacement);
                position = match + needle.Length;
                replacements++;
            }

            if (replacements == 0)
                return new ReplaceFileWriteResult(plan.Path, null, null); // file changed after planning

            string replaced = builder.ToString();
            if (TextHasUnencodableCharacters(replaced, encoding))
                return new ReplaceFileWriteResult(plan.Path, null, "replacement cannot be represented by the file encoding");

            if (backupEnabled)
            {
                string backupPath = plan.Path + ".yagubak";
                if (!File.Exists(backupPath))
                {
                    File.Copy(plan.Path, backupPath, overwrite: false);
                }
                else
                {
                    int suffix = 2;
                    while (File.Exists($"{plan.Path}.yagubak-{suffix}")) suffix++;
                    File.Copy(plan.Path, $"{plan.Path}.yagubak-{suffix}", overwrite: false);
                }
            }

            File.WriteAllText(plan.Path, replaced, encoding);
            return new ReplaceFileWriteResult(plan.Path, replaced, null);
        }
        catch (Exception ex)
        {
            YaguLog.For("FindReplace").LogWarning(ex, "Failed to replace text in file: {Path}", plan.Path);
            return new ReplaceFileWriteResult(plan.Path, null, ex.Message);
        }
    }

    private void UpdateReplaceFilesProgress(string verb, int completed, int total)
    {
        int percent = total <= 0 ? 0 : (int)Math.Clamp(completed * 100L / total, 0, 100);
        ShowProgressOverlay($"{verb} {completed:N0} of {total:N0} files\u2026", percent);
    }

    private async void OnReplaceInAllFiles(object sender, RoutedEventArgs e)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = "Enter text to find"; return; }

        var replacement = ReplaceTextBox.Text;
        var comparison = FindComparison;
        var groups = ViewModel.ResultGroups.ToList();

        if (groups.Count == 0) { FindStatusText.Text = "No result files"; return; }

        int operationVersion = ++_replaceAllFilesOperationVersion;
        ReplaceInFilesButton.IsEnabled = false;
        FindStatusText.Text = "Scanning files…";
        ShowProgressOverlay($"Scanning {groups.Count:N0} result files for replacements\u2026", 0);
        await Task.Yield(); // paint the overlay before disk I/O begins

        var scanStopwatch = Stopwatch.StartNew();
        long scanStartWorkingSet = Environment.WorkingSet;
        YaguLog.For("FindReplace").LogInformation(
            "Replace-all planning started: files={FileCount:N0}, workingSet={WorkingSetMb:N0} MB",
            groups.Count, scanStartWorkingSet / (1024 * 1024));

        try
        {
            // First pass retains only path + occurrence count. The previous implementation retained BOTH
            // the original and rewritten full text for every matching file until the confirmation dialog;
            // a broad search followed by Replace in Files therefore grew Yagu to multiple gigabytes.
            var plans = await Task.Run(() =>
            {
                var list = new List<ReplaceFilePlan>();
                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    if (!group.IsArchiveEntry && File.Exists(group.FilePath))
                    {
                        var plan = ScanFileForReplacementPlan(group.FilePath, needle, comparison);
                        if (plan is not null)
                            list.Add(plan);
                    }

                    int completed = i + 1;
                    if ((completed & 0x3F) == 0 || completed == groups.Count)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (operationVersion == _replaceAllFilesOperationVersion)
                                UpdateReplaceFilesProgress("Scanned", completed, groups.Count);
                        });
                    }
                }
                return list;
            });

            HideProgressOverlay();
            scanStopwatch.Stop();
            YaguLog.For("FindReplace").LogInformation(
                "Replace-all planning complete: matchingFiles={MatchingFiles:N0}, elapsed={ElapsedMs:N0} ms, workingSet={WorkingSetMb:N0} MB",
                plans.Count, scanStopwatch.ElapsedMilliseconds, Environment.WorkingSet / (1024 * 1024));

            if (plans.Count == 0)
            {
                FindStatusText.Text = "No matches in any file";
                return;
            }

            long totalReplacements = plans.Sum(plan => plan.Count);

            // Confirm before writing. This is an in-content, title-bar-less YaguDialog—not an owned
            // captioned Window—so it follows the same modal surface and theme rules as the rest of Yagu.
            var choice = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Replace in All Files",
                    TitleGlyph = "\uE721", // Find/Replace
                    Content = $"Replace {totalReplacements:N0} occurrence{(totalReplacements == 1 ? "" : "s")} across {plans.Count:N0} file{(plans.Count == 1 ? "" : "s")}?",
                    PrimaryButtonText = "Replace",
                    CloseButtonText = "Cancel",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    ShowTitleBar = false,
                    Width = 520,
                    Height = 270,
                });
            if (choice != YaguDialogResult.Primary)
            {
                FindStatusText.Text = "Cancelled";
                return;
            }

            // Second pass rewrites exactly one file at a time. Revalidation happens immediately on the UI
            // thread, so at most one full rewritten document is retained instead of every document.
            int written = 0;
            int errors = 0;
            bool backupEnabled = ViewModel.BackupBeforeSave;
            bool currentPreviewAffected = false;
            ShowProgressOverlay($"Replacing text in {plans.Count:N0} files\u2026", 0);
            await Task.Yield();
            for (int i = 0; i < plans.Count; i++)
            {
                ReplaceFileWriteResult outcome = await Task.Run(() => RewriteOneReplacementFile(
                    plans[i], needle, replacement, comparison, backupEnabled));

                if (outcome.Written && outcome.ReplacedText is not null)
                {
                    written++;
                    ViewModel.RevalidateFileResults(outcome.Path, outcome.ReplacedText);
                    if (_previewResult is { } current
                        && string.Equals(outcome.Path, current.FilePath, StringComparison.OrdinalIgnoreCase))
                        currentPreviewAffected = true;
                }
                else if (outcome.Error is not null)
                {
                    errors++;
                }

                UpdateReplaceFilesProgress("Updated", i + 1, plans.Count);
            }

            HideProgressOverlay();

            // Refresh preview if the currently shown file was affected.
            if (currentPreviewAffected && _previewResult is { } currentResult)
                await ShowSingleFilePreviewAsync(currentResult, fullFile: false);

            var statusParts = new List<string> { $"Replaced in {written:N0} file{(written == 1 ? "" : "s")}" };
            if (errors > 0) statusParts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            FindStatusText.Text = string.Join(", ", statusParts);
            ViewModel.StatusText = FindStatusText.Text;
        }
        catch (Exception ex)
        {
            YaguLog.For("FindReplace").LogError(ex, "Replace-all operation failed");
            FindStatusText.Text = $"Replace failed: {ex.Message}";
        }
        finally
        {
            if (operationVersion == _replaceAllFilesOperationVersion)
            {
                HideProgressOverlay();
                ReplaceInFilesButton.IsEnabled = true;
            }
        }
    }
}
