using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Yagu.Services;

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

    private void OnOpenFindReplaceBar(object sender, RoutedEventArgs e)
    {
        OpenFindBar(showReplace: true);
    }

    private void OpenFindBar(bool showReplace)
    {
        FindBar.Visibility = Visibility.Visible;
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

    private void LogFindVerbose(string message)
    {
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("FindReplace", message);
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
            int blockSearchLen = 0; // length in the search text (\r\n separators)
            int blockTextLen = 0;   // length in block's text model (1-char separators)

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
                    ScrollBlockIntoView(block);
                    return;
                }

                blockSearchLen += searchParaLen;
                blockTextLen += paraLen + 1; // +1 for paragraph separator in text model
            }

            // Check if match starts in this block but spans across paragraphs
            if (globalIndex >= offset && globalIndex < offset + blockSearchLen)
            {
                int localOffset = MapSearchOffsetToBlockOffset(block, globalIndex - offset);
                ApplyFindHighlighter(block, localOffset, length);
                ScrollBlockIntoView(block);
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
        // searchOffset uses \r\n (2) per paragraph separator; block model uses 1.
        int searchPos = 0;
        int blockPos = 0;
        foreach (var b in block.Blocks)
        {
            if (b is not Microsoft.UI.Xaml.Documents.Paragraph p) continue;
            int paraLen = GetParagraphTextLength(p);
            if (searchOffset < searchPos + paraLen)
                return blockPos + (searchOffset - searchPos);
            searchPos += paraLen + 2; // \r\n
            blockPos += paraLen + 1;  // paragraph separator
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
                Windows.UI.Color.FromArgb(180, 255, 185, 0)),
        };
        highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange
        {
            StartIndex = startIndex,
            Length = length,
        });
        block.TextHighlighters.Clear();
        block.TextHighlighters.Add(highlighter);
    }

    private void ScrollBlockIntoView(RichTextBlock block)
    {
        try
        {
            var transform = block.TransformToVisual(PreviewScrollViewer);
            var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            // Center the block in the viewport rather than parking it 1/3 from the top.
            double targetOffset = PreviewScrollViewer.VerticalOffset + point.Y
                                  - (PreviewScrollViewer.ViewportHeight - block.ActualHeight) / 2;
            targetOffset = Math.Max(0, Math.Min(targetOffset, PreviewScrollViewer.ScrollableHeight));
            PreviewScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
        }
        catch { /* block might not be in visual tree */ }
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
            LogService.Instance.Verbose("Find", $"Could not update editor find highlights for '{needle}'", ex);
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
            LogService.Instance.Verbose("Find", "Could not clear editor find highlights", ex);
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

    private async void OnReplaceInAllFiles(object sender, RoutedEventArgs e)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = "Enter text to find"; return; }

        var replacement = ReplaceTextBox.Text;
        var comparison = FindComparison;
        var groups = ViewModel.ResultGroups.ToList();

        if (groups.Count == 0) { FindStatusText.Text = "No result files"; return; }

        ReplaceInFilesButton.IsEnabled = false;
        FindStatusText.Text = "Scanning files…";

        // Collect changes off the UI thread.
        var changes = await Task.Run(() =>
        {
            var list = new List<(string Path, string Original, string Replaced, Encoding Encoding, int Count)>();
            foreach (var group in groups)
            {
                if (group.IsArchiveEntry) continue;
                var path = group.FilePath;
                if (!File.Exists(path)) continue;
                try
                {
                    Encoding encoding;
                    string original;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                    {
                        encoding = Helpers.EncodingDetector.DetectEncoding(stream);
                        if (encoding is System.Text.UTF8Encoding)
                            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                        original = reader.ReadToEnd();
                        encoding = reader.CurrentEncoding;
                    }

                    var sb = new System.Text.StringBuilder(original.Length);
                    int replaceCount = 0;
                    int pos = 0;
                    while (true)
                    {
                        int idx = original.IndexOf(needle, pos, comparison);
                        if (idx < 0) { sb.Append(original, pos, original.Length - pos); break; }
                        sb.Append(original, pos, idx - pos);
                        sb.Append(replacement);
                        replaceCount++;
                        pos = idx + needle.Length;
                    }
                    if (replaceCount > 0)
                    {
                        var replaced = sb.ToString();
                        if (!TextHasUnencodableCharacters(replaced, encoding))
                            list.Add((path, original, replaced, encoding, replaceCount));
                    }
                }
                catch { /* skip unreadable files */ }
            }
            return list;
        });

        if (changes.Count == 0)
        {
            FindStatusText.Text = "No matches in any file";
            ReplaceInFilesButton.IsEnabled = true;
            return;
        }

        int totalReplacements = changes.Sum(c => c.Count);

        // Confirm before writing.
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Replace in All Files",
            Content = $"Replace {totalReplacements:N0} occurrence{(totalReplacements == 1 ? "" : "s")} across {changes.Count:N0} file{(changes.Count == 1 ? "" : "s")}?",
            PrimaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            FindStatusText.Text = "Cancelled";
            ReplaceInFilesButton.IsEnabled = true;
            return;
        }

        // Write changes to disk.
        int written = 0;
        int errors = 0;
        bool backupEnabled = ViewModel.BackupBeforeSave;
        await Task.Run(() =>
        {
            foreach (var (path, _, replaced, encoding, _) in changes)
            {
                try
                {
                    if (backupEnabled)
                    {
                        var bakPath = path + ".yagubak";
                        if (!File.Exists(bakPath))
                            File.Copy(path, bakPath, overwrite: false);
                        else
                        {
                            int suffix = 2;
                            while (File.Exists($"{path}.yagubak-{suffix}")) suffix++;
                            File.Copy(path, $"{path}.yagubak-{suffix}", overwrite: false);
                        }
                    }
                    File.WriteAllText(path, replaced, encoding);
                    Interlocked.Increment(ref written);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

        // Re-validate results for each changed file.
        foreach (var (path, _, replaced, _, _) in changes)
        {
            ViewModel.RevalidateFileResults(path, replaced);
        }

        // Refresh preview if the currently shown file was affected.
        if (_previewResult is { } current && changes.Any(c =>
                string.Equals(c.Path, current.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowSingleFilePreviewAsync(current, fullFile: false);
        }

        var statusParts = new List<string> { $"Replaced in {written:N0} file{(written == 1 ? "" : "s")}" };
        if (errors > 0) statusParts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
        FindStatusText.Text = string.Join(", ", statusParts);
        ViewModel.StatusText = FindStatusText.Text;
        ReplaceInFilesButton.IsEnabled = true;
    }
}
