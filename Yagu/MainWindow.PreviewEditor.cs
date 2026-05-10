using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Preview-editor lifecycle: opening files for full-file editing, save/discard
/// flow, dirty tracking, scroll/center on match, and visibility toggling.
/// Extracted from MainWindow.xaml.cs to keep the partial class file size manageable.
/// State fields for the editor still live on the main partial (e.g.,
/// <c>_previewEditorPath</c>, <c>_previewLoadCts</c>) since they are read by
/// non-editor preview code paths.
/// </summary>
public sealed partial class MainWindow
{
    private const int PreviewEditorForceWrapLineLength = 50_000;
    private long PreviewEditorMaxByteLength => (long)ViewModel.PreviewEditorMaxSizeMB * 1024 * 1024;
    private int PreviewEditorMaxTextLength => ViewModel.PreviewEditorMaxTextLength;
    private int PreviewEditorMaxLineLength => ViewModel.PreviewEditorMaxLineLength;
    private bool _previewEditorForcedWrap;

    private async void OnSavePreviewEdit(object sender, RoutedEventArgs e)
    {
        await SavePreviewEditAsync();
    }

    private async void OnClosePreviewEdit(object sender, RoutedEventArgs e)
    {
        if (HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync()) return;
        ClosePreviewEditor();
        RestorePreviewSurfaceAfterEditor();
        await Task.CompletedTask;
    }

    private void OnPreviewEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPreviewEditorTextChanged) return;
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        _previewEditorDirty = true;
        UpdatePreviewEditorButtons();
    }

    /// <summary>
    /// Handles a double-tap inside a preview <see cref="RichTextBlock"/> by opening
    /// the inline editor for the file and placing the caret on the clicked line.
    /// </summary>
    private async Task EnterPreviewEditorAtPointAsync(
        RichTextBlock block,
        Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e,
        string? filePath)
    {
        try
        {
            if (filePath is null) return;

            var pt = e.GetPosition(block);
            var tp = block.GetPositionFromPoint(pt);
            int lineNum = tp is null ? -1 : ResolveLineNumberAtPointer(block, tp);
            if (lineNum <= 0) return;

            // Reuse an existing SearchResult for this file when possible (it has
            // MatchLine/column data that ScrollEditorToMatch uses for highlighting).
            // Prefer the result whose LineNumber matches the clicked line so the
            // editor highlights the exact match the user double-clicked, not the
            // first match in the file.
            var fileGroup = ViewModel.ResultGroups
                .FirstOrDefault(g => string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            SearchResult? existing = fileGroup?.FirstOrDefault(r => r.LineNumber == lineNum)
                ?? fileGroup?.FirstOrDefault();
            SearchResult target = existing is not null && existing.LineNumber == lineNum
                ? existing
                : existing is not null
                    ? existing with { LineNumber = lineNum, MatchStartColumn = 0, MatchLength = 0 }
                    : new SearchResult(filePath, lineNum, string.Empty, 0, 0,
                        Array.Empty<string>(), Array.Empty<string>());

            e.Handled = true;
            await ShowFullFileEditorAsync(target);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"EnterPreviewEditorAtPointAsync threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the paragraphs of <paramref name="block"/> and finds the one
    /// containing <paramref name="tp"/>, then parses the line number from the
    /// gutter <see cref="Run"/> created by <see cref="MakePreviewParagraph"/>.
    /// Returns -1 when the line number cannot be determined.
    /// </summary>
    private static int ResolveLineNumberAtPointer(RichTextBlock block, TextPointer tp)
    {
        int tpOff = tp.Offset;
        foreach (var b in block.Blocks)
        {
            if (b is not Paragraph para) continue;
            int start = para.ContentStart.Offset;
            int end = para.ContentEnd.Offset;
            if (tpOff < start || tpOff > end) continue;

            // The gutter run created by MakePreviewParagraph has text "{lineNum,5} ".
            // Indicator is index 0, gutter is index 1, separator is index 2.
            for (int i = 0; i < Math.Min(3, para.Inlines.Count); i++)
            {
                if (para.Inlines[i] is Run r
                    && int.TryParse(r.Text.AsSpan().Trim(), out int n)
                    && n > 0)
                {
                    return n;
                }
            }
            return -1;
        }
        return -1;
    }

    private async Task ShowFullFileEditorAsync(SearchResult result)
    {
        LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: start file='{System.IO.Path.GetFileName(result.FilePath)}'");
        var editorSw = System.Diagnostics.Stopwatch.StartNew();
        if (PreviewEditor.Visibility == Visibility.Visible && HasRealEditorChanges())
        {
            LogService.Instance.Info("Preview", "ShowFullFileEditorAsync: blocked - editor has unsaved changes");
            ViewModel.StatusText = "Save or close the current editor before loading another full file.";
            return;
        }

        _previewLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;

        ClosePreviewEditor(clearText: true, cancelLoad: false);
        SetPreviewFileLabel(result.FilePath);
        ViewModel.StatusText = "Loading full file for editing...";
        FullFileButton.IsEnabled = false;

        try
        {
            if (!ZipArchiveSearcher.IsArchivePath(result.FilePath))
            {
                try
                {
                    var fileInfo = new FileInfo(result.FilePath);
                    if (fileInfo.Exists && fileInfo.Length > PreviewEditorMaxByteLength)
                    {
                        var message = BuildPreviewEditorLimitMessage(
                            result.FilePath,
                            $"it is {FormatBytes(fileInfo.Length)}; the built-in editor limit is {FormatBytes(PreviewEditorMaxByteLength)}");
                        LogService.Instance.Warning("Preview",
                            $"ShowFullFileEditorAsync: blocked oversized file before load, bytes={fileInfo.Length:N0}, limit={PreviewEditorMaxByteLength:N0}, file='{result.FilePath}'");
                        RestorePreviewSurfaceAfterEditor();
                        ViewModel.StatusText = message;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview",
                        $"ShowFullFileEditorAsync: could not preflight editor file size: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var document = await LoadPreviewDocumentAsync(result.FilePath, cts.Token, enforceLimit: false).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            if (TryGetPreviewEditorLimitReason(document, out var limitReason))
            {
                var message = BuildPreviewEditorLimitMessage(result.FilePath, limitReason);
                LogService.Instance.Warning("Preview",
                    $"ShowFullFileEditorAsync: blocked editor load after read, bytes={document.ByteLength:N0}, textLen={document.Text.Length:N0}, maxLineLen={document.MaxLineLength:N0}, file='{result.FilePath}'");
                RestorePreviewSurfaceAfterEditor();
                ViewModel.StatusText = message;
                return;
            }

            _previewEditorPath = result.FilePath;
            _previewEditorEncoding = document.Encoding;

            // Archive entries are read-only (cannot save back into zip)
            bool isArchive = ZipArchiveSearcher.IsArchivePath(result.FilePath);
            PreviewEditor.IsReadOnly = isArchive;

            var text = document.Text;
            _previewEditorForcedWrap = document.MaxLineLength >= PreviewEditorForceWrapLineLength;
            ApplyPreviewEditorWordWrap(_previewEditorForcedWrap || ViewModel.PreviewWordWrap);
            if (_previewEditorForcedWrap)
            {
                LogService.Instance.Info("Preview",
                    $"ShowFullFileEditorAsync: forcing editor word wrap for long line maxLen={document.MaxLineLength:N0}");
            }

            // Assign while collapsed so TextBox does one document load instead of
            // repeatedly re-laying out an ever-growing text buffer.
            var textSetSw = System.Diagnostics.Stopwatch.StartNew();
            _suppressPreviewEditorTextChanged = true;
            PreviewEditor.Text = text;
            _previewEditorOriginalText = PreviewEditor.Text;
            _suppressPreviewEditorTextChanged = false;
            textSetSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: assigned TextBox text, textLen={text.Length:N0}, elapsed={textSetSw.ElapsedMilliseconds}ms");

            _previewEditorDirty = false;
            UpdateEditorDirtyIndicator();

            var showEditorSw = System.Diagnostics.Stopwatch.StartNew();
            SetPreviewEditorVisible(true);
            showEditorSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: editor visible, elapsed={showEditorSw.ElapsedMilliseconds}ms");

            PreviewEditor.Focus(FocusState.Programmatic);

            // Scroll to the match line and highlight the matched text.
            var scrollSw = System.Diagnostics.Stopwatch.StartNew();
            ScrollEditorToMatch(text, result);
            scrollSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: scrolled to match line={result.LineNumber}, elapsed={scrollSw.ElapsedMilliseconds}ms");

            // WinUI 3 TextBox may fire deferred TextChanged after a bulk text
            // load (line-ending normalisation, layout pass, etc.).  If that
            // sets _previewEditorDirty even though the text is unchanged,
            // clear it again so the indicator doesn't show a false positive.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_previewEditorOriginalText is not null
                    && PreviewEditor.Visibility == Visibility.Visible
                    && _previewEditorDirty
                    && PreviewEditor.Text == _previewEditorOriginalText)
                {
                    _previewEditorDirty = false;
                    UpdatePreviewEditorButtons();
                }
            });

            string label = isArchive ? "viewing (read-only)" : "editing";
            ViewModel.StatusText = $"Loaded {Path.GetFileName(result.FilePath)} ({FormatBytes(document.ByteLength)}, {GetEncodingDisplayName(document.Encoding)}) for {label}.";
        }
        catch (OperationCanceledException)
        {
            ShowPreviewMessage("Full-file load cancelled.", showBackButton: _previewResult is not null);
        }
        catch (PreviewLoadException ex)
        {
            ShowPreviewMessage(ex.Message, showBackButton: _previewResult is not null);
            ViewModel.StatusText = ex.Message;
        }
        catch (OutOfMemoryException ex)
        {
            const string message = "Not enough memory to load this full file into the right-panel editor.";
            LogService.Instance.Warning("Preview", message, ex);
            ShowPreviewMessage(message, showBackButton: _previewResult is not null);
            ViewModel.StatusText = message;
        }
        catch (Exception ex)
        {
            var message = $"Could not load full file: {ex.Message}";
            LogService.Instance.Warning("Preview", $"Could not load full file: {result.FilePath}", ex);
            ShowPreviewMessage(message, showBackButton: _previewResult is not null);
            ViewModel.StatusText = message;
        }
        finally
        {
            editorSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: done file='{System.IO.Path.GetFileName(result.FilePath)}', elapsed={editorSw.ElapsedMilliseconds}ms");
            if (ReferenceEquals(_previewLoadCts, cts))
                _previewLoadCts = null;
            cts.Dispose();
            FullFileButton.IsEnabled = _previewResult is not null;
            UpdatePreviewEditorButtons();
        }
    }

    private bool TryGetPreviewEditorLimitReason(PreviewTextDocument document, out string reason)
    {
        if (document.ByteLength > PreviewEditorMaxByteLength)
        {
            reason = $"it is {FormatBytes(document.ByteLength)}; the built-in editor limit is {FormatBytes(PreviewEditorMaxByteLength)}";
            return true;
        }

        if (document.Text.Length > PreviewEditorMaxTextLength)
        {
            reason = $"it contains {document.Text.Length:N0} characters; the built-in editor limit is {PreviewEditorMaxTextLength:N0}";
            return true;
        }

        if (document.MaxLineLength > PreviewEditorMaxLineLength)
        {
            reason = $"its longest line is {document.MaxLineLength:N0} characters; the built-in editor limit is {PreviewEditorMaxLineLength:N0}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string BuildPreviewEditorLimitMessage(string filePath, string reason)
    {
        return $"Built-in editor skipped {Path.GetFileName(filePath)} because {reason}. Use Open or Show in Explorer for very large files.";
    }

    private void ScrollEditorToMatch(string text, SearchResult result)
    {
        // Find the character offset of the target line (1-based LineNumber).
        int charOffset = 0;
        int currentLine = 1;
        for (int i = 0; i < text.Length && currentLine < result.LineNumber; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                currentLine++;
                charOffset = i + 2;
                i++; // skip \n
            }
            else if (text[i] == '\n')
            {
                currentLine++;
                charOffset = i + 1;
            }
        }

        // TextBox uses \r\n → each line adds 2 chars. But TextBox.Text already has \r\n,
        // so charOffset calculated above is correct. Select the matched portion.
        int selectStart = charOffset + result.MatchStartColumn;
        int selectLength = result.MatchLength;

        // Clamp to valid range.
        if (selectStart > text.Length) selectStart = charOffset;
        if (selectStart + selectLength > text.Length) selectLength = 0;

        // PERF: TextBox.Select() forces the text formatter to lay out the entire
        // document up to the selection. On large files (~2 MB) this can freeze
        // the UI thread for tens of seconds when called BEFORE any scroll has
        // forced layout. For large texts we first scroll the TextBox's inner
        // ScrollViewer programmatically (cheap), then apply the selection from
        // a deferred dispatcher tick — by then layout is already settled near
        // the target line, so Select() only has incremental work to do.
        const int LargeTextThreshold = 256 * 1024;
        if (text.Length > LargeTextThreshold)
        {
            int targetLine = Math.Max(1, result.LineNumber);
            int capturedStart = selectStart;
            int capturedLength = Math.Max(0, selectLength);
            // Defer scrolling so the editor has a chance to render its template first
            // (the inner ScrollViewer is created during template application).
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    var sv = EditorScrollHelper.FindFirstScrollViewerInTree(PreviewEditor);
                    if (sv is null) return;
                    double lineHeight = PreviewEditor.FontSize * 1.4;
                    if (lineHeight < 1) lineHeight = 19;
                    double targetY = (targetLine - 1) * lineHeight;
                    double viewport = sv.ViewportHeight > 0 ? sv.ViewportHeight : 0;
                    double centered = Math.Max(0, targetY - viewport / 2);
                    sv.ChangeView(null, centered, null, disableAnimation: true);

                    // After the scroll has forced layout near the target line,
                    // apply the selection so the matched text is highlighted.
                    // Run this on a subsequent dispatcher tick so ChangeView's
                    // layout pass has time to complete; otherwise Select() may
                    // re-trigger a snap-scroll that fights our centering.
                    if (capturedLength > 0 && capturedStart >= 0)
                    {
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            try
                            {
                                if (PreviewEditor.Visibility != Visibility.Visible) return;
                                int len = PreviewEditor.Text?.Length ?? 0;
                                int s = Math.Min(capturedStart, len);
                                int l = Math.Min(capturedLength, Math.Max(0, len - s));
                                PreviewEditor.Select(s, l);

                                // Select() snaps the scroll so the selection is just
                                // barely on-screen (typically near the bottom edge).
                                // Wait for that scroll to settle, then nudge up by
                                // (viewport/2 - lineHeight) so the match moves from
                                // the bottom edge to the middle of the viewport.
                                // Using a relative offset avoids depending on a
                                // line-height estimate that doesn't match the
                                // TextBox's actual font metrics.
                                CenterEditorOnSelectionAfterScroll();
                            }
                            catch (Exception ex)
                            {
                                LogService.Instance.Warning("Preview", $"ScrollEditorToMatch deferred select failed: {ex.GetType().Name}: {ex.Message}");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview", $"ScrollEditorToMatch deferred scroll failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
            return;
        }

        // Select the match — this auto-scrolls the TextBox so the selection is
        // just barely visible (typically at the bottom edge). Re-center after
        // Select's auto-scroll settles.
        PreviewEditor.Select(selectStart, Math.Max(selectLength, 0));
        CenterEditorOnSelectionAfterScroll();
    }

    /// <summary>
    /// After <c>TextBox.Select()</c> has snapped the selection just barely on-screen
    /// (typically near the bottom edge), wait for the inner ScrollViewer's auto-scroll
    /// to finish, then nudge the offset up so the selected line lands in the middle
    /// of the viewport. Uses a relative shift (current offset − (viewport/2 − lineHeight))
    /// rather than an absolute computed offset so we don't depend on a line-height
    /// estimate matching the TextBox's actual font metrics.
    /// </summary>
    private void CenterEditorOnSelectionAfterScroll()
    {
        var sv = EditorScrollHelper.FindFirstScrollViewerInTree(PreviewEditor);
        if (sv is null) return;

        double lineHeight = EditorScrollHelper.EstimateLineHeight(PreviewEditor.FontSize);

        void Center()
        {
            try
            {
                double newOffset = EditorScrollHelper.ComputeCenterOffset(
                    sv.VerticalOffset, sv.ViewportHeight, sv.ScrollableHeight, lineHeight);
                sv.ChangeView(null, newOffset, null, disableAnimation: true);
            }
            catch { }
        }

        // Wait for Select()'s auto-scroll to land before we re-center, otherwise
        // our ChangeView gets clobbered by Select's pending scroll.
        EventHandler<ScrollViewerViewChangedEventArgs>? handler = null;
        bool centered = false;
        handler = (_, ev) =>
        {
            if (ev.IsIntermediate) return;
            sv.ViewChanged -= handler;
            if (centered) return;
            centered = true;
            Center();
        };
        sv.ViewChanged += handler;

        // Safety net: if Select didn't actually move the viewport (selection was
        // already on-screen), ViewChanged won't fire. Run a deferred Center as a
        // fallback after a couple of dispatcher ticks.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (centered) return;
                centered = true;
                sv.ViewChanged -= handler;
                Center();
            });
        });
    }

    private async Task<bool> SavePreviewEditAsync()
    {
        if (_previewEditorPath is null || _previewEditorEncoding is null) return false;

        SavePreviewEditButton.IsEnabled = false;
        try
        {
            if (ViewModel.BackupBeforeSave && File.Exists(_previewEditorPath))
            {
                var bakPath = _previewEditorPath + ".yagubak";
                if (!File.Exists(bakPath))
                {
                    File.Copy(_previewEditorPath, bakPath, overwrite: false);
                }
                else
                {
                    int suffix = 2;
                    while (File.Exists($"{_previewEditorPath}.yagubak-{suffix}"))
                        suffix++;
                    File.Copy(_previewEditorPath, $"{_previewEditorPath}.yagubak-{suffix}", overwrite: false);
                }
            }

            var textToSave = PreviewEditor.Text;
            if (TextHasUnencodableCharacters(textToSave, _previewEditorEncoding))
            {
                var encDialog = new ContentDialog
                {
                    XamlRoot = ((FrameworkElement)Content).XamlRoot,
                    Title = "Encoding Warning",
                    Content = $"This file contains characters that cannot be represented in {GetEncodingDisplayName(_previewEditorEncoding)}. Save as UTF-8 instead?",
                    PrimaryButtonText = "Save as UTF-8",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                var encChoice = await encDialog.ShowAsync();
                if (encChoice != ContentDialogResult.Primary) return false;
                _previewEditorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            await File.WriteAllTextAsync(_previewEditorPath, textToSave, _previewEditorEncoding).ConfigureAwait(true);
            _previewEditorOriginalText = textToSave;
            _previewEditorDirty = false;
            UpdatePreviewEditorButtons();

            // Re-validate search results for this file against the saved content.
            bool fileStillHasMatches = ViewModel.RevalidateFileResults(_previewEditorPath, PreviewEditor.Text);
            if (!fileStillHasMatches && _previewResult?.FilePath is not null &&
                string.Equals(_previewResult.FilePath, _previewEditorPath, StringComparison.OrdinalIgnoreCase))
            {
                _previewResult = null;
            }

            ViewModel.StatusText = $"Saved {_previewEditorPath}.";
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Could not save file: {ex.Message}";
            LogService.Instance.Warning("Preview", $"Could not save editor contents: {_previewEditorPath}", ex);
            ViewModel.StatusText = message;
            return false;
        }
        finally
        {
            UpdatePreviewEditorButtons();
        }
    }

    /// <summary>Returns true if the caller should proceed (edits were saved or discarded), false to cancel.</summary>
    private async Task<bool> ConfirmDiscardPreviewEditAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Unsaved changes",
            Content = "The right-panel editor has unsaved changes.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary)
        {
            // Save first, then allow the close/navigation to proceed.
            return await SavePreviewEditAsync();
        }
        return choice == ContentDialogResult.Secondary; // Discard → true, Cancel → false
    }

    private bool TryLeavePreviewEditorForPreviewChange()
    {
        if (_previewLoadCts is not null)
        {
            _previewLoadCts.Cancel();
            _previewLoadCts = null;
            FullFileButton.IsEnabled = true;
        }

        if (PreviewEditor.Visibility != Visibility.Visible) return true;
        if (HasRealEditorChanges())
        {
            ViewModel.StatusText = "Save or close the right-panel editor before changing the preview.";
            return false;
        }

        ClosePreviewEditor();
        return true;
    }

    private void ClosePreviewEditor(bool clearText = true, bool cancelLoad = true)
    {
        if (cancelLoad)
        {
            _previewLoadCts?.Cancel();
            _previewLoadCts = null;
        }

        SetPreviewEditorVisible(false);
        _previewEditorPath = null;
        _previewEditorEncoding = null;
        _previewEditorDirty = false;
        _previewEditorOriginalText = null;
        _previewEditorForcedWrap = false;

        if (clearText)
        {
            _suppressPreviewEditorTextChanged = true;
            PreviewEditor.Text = string.Empty;
            _suppressPreviewEditorTextChanged = false;
        }

        UpdatePreviewEditorButtons();
    }

    private void SetPreviewEditorVisible(bool visible)
    {
        PreviewEditorContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewEditor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewScrollViewer.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        if (visible)
        {
            PreviewEditorPathText.Text = _previewEditorPath ?? string.Empty;
            ToolTipService.SetToolTip(PreviewEditorPathBar, _previewEditorPath ?? string.Empty);
        }

        // Editor-mode group
        EditorSeparator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SavePreviewEditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ClosePreviewEditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // Hide view/action buttons while editing
        FullFileButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        OpenInDefaultAppButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        OpenInEditorButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        ShowInExplorerButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        PreviewContextPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        if (visible)
            PreviewToolbarContent.Visibility = Visibility.Visible;
        ApplyWordWrap(ViewModel.PreviewWordWrap);
    }

    private void ApplyPreviewEditorWordWrap(bool wrap)
    {
        var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var horizontalBar = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (PreviewEditor.TextWrapping != wrapping)
            PreviewEditor.TextWrapping = wrapping;
        ScrollViewer.SetHorizontalScrollBarVisibility(PreviewEditor, horizontalBar);
    }

    private void RestorePreviewSurfaceAfterEditor()
    {
        HidePreviewLoading();
        PreviewBackButton.Visibility = Visibility.Collapsed;

        if (PreviewSectionsPanel.Children.Count > 0)
        {
            PreviewMessagePanel.Visibility = Visibility.Collapsed;
            PreviewBlock.Visibility = Visibility.Collapsed;
            PreviewSectionsPanel.Visibility = Visibility.Visible;
            SetPerFileToolbarVisibility(Visibility.Collapsed);
            PreviewToolbarContent.Visibility = Visibility.Visible;
            PreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

            UpdateMatchNavPanel();
            if (_activeSectionNav is null && _sectionMatchNavs.Count == 1)
            {
                var sectionNav = _sectionMatchNavs.Values.FirstOrDefault();
                if (sectionNav is not null && sectionNav.Matches.Count > 1)
                    _activeSectionNav = sectionNav;
            }

            HighlightActiveExpander();
            UpdateSectionNavOverlay();
            return;
        }

        if (PreviewBlock.Blocks.Count > 0)
        {
            PreviewMessagePanel.Visibility = Visibility.Visible;
            PreviewBlock.Visibility = Visibility.Visible;
            PreviewSectionsPanel.Visibility = Visibility.Collapsed;
            SetPerFileToolbarVisibility(Visibility.Visible);
            PreviewToolbarContent.Visibility = _previewResult is not null ? Visibility.Visible : Visibility.Collapsed;
            PreviewScrollViewer.HorizontalScrollBarVisibility = ViewModel.PreviewWordWrap
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
            return;
        }

        ShowPreviewMessage("No matches remain for this file.");
    }

    /// <summary>
    /// Returns true only if the editor text actually differs from the
    /// originally-loaded content.  This avoids false positives caused by
    /// WinUI 3 TextBox raising deferred TextChanged events after a bulk load.
    /// </summary>
    private bool HasRealEditorChanges() =>
        _previewEditorDirty
        && _previewEditorOriginalText is not null
        && PreviewEditor.Text != _previewEditorOriginalText;

    private void UpdatePreviewEditorButtons()
    {
        SavePreviewEditButton.IsEnabled = PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && _previewEditorPath is not null;
        UpdateEditorDirtyIndicator();
    }

    private void UpdateEditorDirtyIndicator()
    {
        if (_previewEditorPath is null) return;
        string baseName = _previewEditorPath;
        // Strip any existing dirty indicator prefix.
        if (PreviewFileLabel.Text.StartsWith("● ", StringComparison.Ordinal))
        {
            var existingTooltip = ToolTipService.GetToolTip(PreviewFileLabel) as string;
            baseName = existingTooltip ?? PreviewFileLabel.Text[2..];
        }
        else
        {
            baseName = PreviewFileLabel.Text;
        }
        SetPreviewFileLabel(
            _previewEditorDirty ? $"● {baseName}" : baseName,
            baseName);
    }
}
