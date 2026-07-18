using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
    // Wrapping a line longer than this is dangerously expensive for XAML (it can hang or crash). When
    // the editor would wrap a line at or beyond this length, the user is prompted to choose first.
    private const int PreviewEditorWrapPromptLineLength = 200_000;
    private const long PreviewEditorChunkByteLength = 10L * 1024 * 1024;
    private long PreviewEditorMaxByteLength => (long)ViewModel.PreviewEditorMaxSizeMB * 1024 * 1024;
    private long PreviewEditorPopOutMaxByteLength => (long)Math.Max(1, ViewModel.PreviewEditorPopOutMaxSizeMB) * 1024 * 1024;
    private int PreviewEditorMaxTextLength => ViewModel.PreviewEditorMaxTextLength;
    private int PreviewEditorMaxLineLength => ViewModel.PreviewEditorMaxLineLength;
    private bool? _previewEditorWrapOverride;
    private bool _previewEditorChunked;
    private bool _previewEditorChunkLoadInFlight;
    private long _previewEditorLoadedByteLength;
    private long _previewEditorTotalByteLength;
    private Encoding? _previewEditorChunkEncoding;
    private ScrollViewer? _previewEditorScrollViewer;
    private int _previewEditorRevealVersion;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewEditorRevealRetryTimer;
    private const int PreviewEditorSavedOverlayDurationMs = 700;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewEditorSavedOverlayTimer;

    private async void OnSavePreviewEdit(object sender, RoutedEventArgs e)
    {
        await SavePreviewEditAsync();
    }

    private async void OnLoadMorePreviewEditorChunk(object sender, RoutedEventArgs e)
    {
        await LoadMorePreviewEditorChunkAsync(force: true);
    }

    private async void OnClosePreviewEdit(object sender, RoutedEventArgs e)
    {
        if (HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync()) return;
        ClosePreviewEditor();
        RestorePreviewSurfaceAfterEditor();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Pops the in-pane editor out into an independent <see cref="PreviewEditorWindow"/>. For a
    /// fully-loaded file the current (possibly-edited) text is TRANSFERRED with no reload so unsaved
    /// edits move with it. For a chunked (large) file — where the in-pane editor only holds part of
    /// the content — the WHOLE file is reloaded from disk (up to the configurable pop-out size limit)
    /// so the pop-out is fully editable and safe to save. The main pane then reverts to the read-only
    /// preview, restoring the global match paginator untouched.
    /// </summary>
    private async void OnPopOutPreviewEditor(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Info("PreviewEditor",
            $"Pop-out clicked: editorVisible={PreviewEditor.Visibility == Visibility.Visible}, path='{_previewEditorPath}', hasEncoding={_previewEditorEncoding is not null}, chunked={_previewEditorChunked}.");

        if (PreviewEditor.Visibility != Visibility.Visible || _previewEditorPath is null || _previewEditorEncoding is null)
            return;

        long limitBytes = PreviewEditorPopOutMaxByteLength;
        long fileSize = _previewEditorChunked && _previewEditorTotalByteLength > 0
            ? _previewEditorTotalByteLength
            : SafeFileLength(_previewEditorPath);

        if (fileSize > limitBytes)
        {
            ViewModel.StatusText =
                $"This file ({FormatBytes(fileSize)}) is larger than the {ViewModel.PreviewEditorPopOutMaxSizeMB} MB pop-out limit. Increase it in Settings \u2192 Preview \u2192 Built-in editor.";
            return;
        }

        PreviewEditorWindowContext? context;
        if (_previewEditorChunked)
        {
            // The in-pane editor only loaded PART of this large file; unsaved partial edits can't be
            // carried across safely, so require a clean editor, then reload the whole file.
            if (HasRealEditorChanges())
            {
                ViewModel.StatusText = "Save or discard your changes before popping this large file out.";
                return;
            }

            context = await BuildReloadedPreviewEditorWindowContextAsync(_previewEditorPath, limitBytes);
        }
        else
        {
            context = BuildPreviewEditorWindowContext();
        }

        if (context is null)
            return;

        try
        {
            PreviewEditorWindow.Open(context);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"Pop-out failed: {ex.Message}", ex);
            ViewModel.StatusText = $"Could not pop out the editor: {ex.Message}";
            return;
        }

        // The edits are now owned by the pop-out window, so close the in-pane editor without a
        // discard prompt and restore the read-only preview surface (and its match navigation).
        ClosePreviewEditor();
        RestorePreviewSurfaceAfterEditor();
        ViewModel.StatusText = $"Editing {Path.GetFileName(context.FilePath)} in its own window.";
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Builds a pop-out context by reloading the WHOLE file from disk (used when the in-pane
    /// editor was chunked). Returns null and sets a status message if the load fails.</summary>
    private async Task<PreviewEditorWindowContext?> BuildReloadedPreviewEditorWindowContextAsync(string filePath, long limitBytes)
    {
        PreviewTextDocument document;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            document = await LoadPreviewDocumentAsync(
                filePath, cts.Token, enforceLimit: true, fileSizeLimit: limitBytes).ConfigureAwait(true);
        }
        catch (PreviewLoadException ex)
        {
            ViewModel.StatusText = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"Pop-out reload failed: {filePath}", ex);
            ViewModel.StatusText = $"Could not pop out the editor: {ex.Message}";
            return null;
        }

        return new PreviewEditorWindowContext
        {
            OwnerHwnd = _hwnd,
            Theme = AppThemeService.ResolveEffectiveTheme(RootGrid, ViewModel.ThemeModeIndex),
            FilePath = filePath,
            Text = document.Text,
            DiskText = document.Text,
            Encoding = document.Encoding,
            WordWrap = PreviewEditor.WordWrap,
            ZoomFactor = PreviewEditor.ZoomFactor,
            FontFamily = ResolvePreviewEditorFontFamily(),
            FontSize = ResolvePreviewEditorFontSize(),
            TextColor = ResolveEffectiveEditorTextColor(),
            GutterColor = ColorStringHelper.Parse(ViewModel.PreviewEditorGutterColor, Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6)),
            SyntaxHighlightId = ResolvePreviewEditorSyntaxId(filePath),
            ShowLineNumbers = PreviewEditor.ShowLineNumbers,
            ShowLineHighlighter = PreviewEditor.ShowLineHighlighter,
            BackupBeforeSave = ViewModel.BackupBeforeSave,
            Arrangement = ResolvePopOutArrangement(),
        };
    }

    private PreviewEditorWindowContext? BuildPreviewEditorWindowContext()
    {
        if (_previewEditorPath is null || _previewEditorEncoding is null)
            return null;

        string currentText = GetPreviewEditorText();
        string diskText = _previewEditorOriginalText ?? currentText;

        return new PreviewEditorWindowContext
        {
            OwnerHwnd = _hwnd,
            Theme = AppThemeService.ResolveEffectiveTheme(RootGrid, ViewModel.ThemeModeIndex),
            FilePath = _previewEditorPath,
            Text = currentText,
            DiskText = diskText,
            Encoding = _previewEditorEncoding,
            WordWrap = PreviewEditor.WordWrap,
            ZoomFactor = PreviewEditor.ZoomFactor,
            FontFamily = ResolvePreviewEditorFontFamily(),
            FontSize = ResolvePreviewEditorFontSize(),
            TextColor = ResolveEffectiveEditorTextColor(),
            GutterColor = ColorStringHelper.Parse(ViewModel.PreviewEditorGutterColor, Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6)),
            SyntaxHighlightId = ResolvePreviewEditorSyntaxId(_previewEditorPath),
            ShowLineNumbers = PreviewEditor.ShowLineNumbers,
            ShowLineHighlighter = PreviewEditor.ShowLineHighlighter,
            BackupBeforeSave = ViewModel.BackupBeforeSave,
            Arrangement = ResolvePopOutArrangement(),
        };
    }

    private string ResolvePreviewEditorFontFamily()
        => string.IsNullOrWhiteSpace(ViewModel.PreviewEditorFontFamily)
            ? AppSettings.DefaultPreviewEditorFontFamily
            : ViewModel.PreviewEditorFontFamily.Trim();

    private int ResolvePreviewEditorFontSize()
        => Math.Clamp(
            ViewModel.PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : ViewModel.PreviewEditorFontSize,
            6,
            72);

    private Yagu.Helpers.PopOutArrangement ResolvePopOutArrangement()
        => Yagu.Helpers.PopOutTileLayout.FromIndex(ViewModel.PreviewEditorPopOutArrangementIndex);

    /// <summary>
    /// Pops a read-only preview of <paramref name="filePath"/> out into its own independent
    /// <see cref="PreviewEditorWindow"/> (the "pop out drawer" action). The window loads the whole
    /// file read-only, jumps to the first match, and offers an "Edit file" button that unlocks
    /// editing in the SAME window. This does not touch the main window's preview surface or global
    /// match paginator.
    /// </summary>
    private async Task PopOutPreviewDrawerAsync(string filePath, IReadOnlyList<SearchResult>? results)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        LogService.Instance.Info("PreviewEditor", $"Pop-out drawer clicked: path='{filePath}'.");

        PreviewTextDocument document;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            document = await LoadPreviewDocumentAsync(
                filePath, cts.Token, enforceLimit: true, fileSizeLimit: PreviewEditorPopOutMaxByteLength).ConfigureAwait(true);
        }
        catch (PreviewLoadException ex)
        {
            ViewModel.StatusText = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"Pop-out drawer load failed: {filePath}", ex);
            ViewModel.StatusText = $"Could not pop out the preview: {ex.Message}";
            return;
        }

        int scrollToLine = 0;
        if (results is not null)
        {
            foreach (var result in results)
            {
                if (result.LineNumber > 0) { scrollToLine = result.LineNumber; break; }
            }
        }

        bool literalHighlight = !ViewModel.LastSearchUseRegex && !string.IsNullOrEmpty(ViewModel.LastSearchPattern);

        var context = new PreviewEditorWindowContext
        {
            OwnerHwnd = _hwnd,
            Theme = AppThemeService.ResolveEffectiveTheme(RootGrid, ViewModel.ThemeModeIndex),
            FilePath = filePath,
            Text = document.Text,
            DiskText = document.Text,
            Encoding = document.Encoding,
            WordWrap = ViewModel.PreviewWordWrap,
            ZoomFactor = 100,
            FontFamily = ResolvePreviewEditorFontFamily(),
            FontSize = ResolvePreviewEditorFontSize(),
            TextColor = ResolveEffectiveEditorTextColor(),
            GutterColor = ColorStringHelper.Parse(ViewModel.PreviewEditorGutterColor, Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6)),
            SyntaxHighlightId = ResolvePreviewEditorSyntaxId(filePath),
            ShowLineNumbers = true,
            ShowLineHighlighter = true,
            BackupBeforeSave = ViewModel.BackupBeforeSave,
            StartReadOnly = true,
            ScrollToLine = scrollToLine,
            HighlightWord = literalHighlight ? ViewModel.LastSearchPattern : null,
            HighlightWholeWord = ViewModel.LastSearchExactMatch,
            HighlightMatchCase = ViewModel.LastSearchCaseSensitive,
            Arrangement = ResolvePopOutArrangement(),
        };

        try
        {
            PreviewEditorWindow.Open(context);
            ViewModel.StatusText = $"Previewing {Path.GetFileName(filePath)} in its own window.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"Pop-out drawer failed: {ex.Message}", ex);
            ViewModel.StatusText = $"Could not pop out the preview: {ex.Message}";
        }
    }

    private TextControlBoxNS.SyntaxHighlightID ResolvePreviewEditorSyntaxId(string? filePath)
    {
        if (!ViewModel.EditorSyntaxHighlightingEnabled)
            return TextControlBoxNS.SyntaxHighlightID.None;

        var language = EditorSyntaxHighlightingResolver.ResolveFromFileName(filePath);
        return language is null
            ? TextControlBoxNS.SyntaxHighlightID.None
            : MapToSyntaxHighlightId(language.Value);
    }

    private void OnPreviewEditorTextChanged(TextControlBoxNS.TextControlBox sender)
    {
        if (_suppressPreviewEditorTextChanged) return;
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        HidePreviewEditorSavedOverlay();
        _previewEditorDirty = true;
        UpdatePreviewEditorButtons();
    }

    private const int PreviewEditorZoomStep = 10;
    private const int PreviewEditorZoomMin = 30;
    private const int PreviewEditorZoomMax = 400;

    /// <summary>
    /// Appends Zoom in / Zoom out / Reset zoom items to the inline editor's
    /// built-in right-click flyout and wires Ctrl+'+' / Ctrl+'-' / Ctrl+0
    /// keyboard accelerators. Called once from the MainWindow constructor.
    /// </summary>
    private void InitializePreviewEditorZoom()
    {
        try
        {
            PreviewEditor.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            AddPreviewEditorSaveAccelerator();

            var flyout = PreviewEditor.ContextFlyout;
            if (flyout is not null)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());

                var zoomIn = new MenuFlyoutItem
                {
                    Text = "Zoom in",
                    Icon = new SymbolIcon { Symbol = Symbol.ZoomIn },
                };
                zoomIn.Click += (_, _) => AdjustPreviewEditorZoom(+PreviewEditorZoomStep);
                flyout.Items.Add(zoomIn);

                var zoomOut = new MenuFlyoutItem
                {
                    Text = "Zoom out",
                    Icon = new SymbolIcon { Symbol = Symbol.ZoomOut },
                };
                zoomOut.Click += (_, _) => AdjustPreviewEditorZoom(-PreviewEditorZoomStep);
                flyout.Items.Add(zoomOut);

                var zoomReset = new MenuFlyoutItem
                {
                    Text = "Reset zoom",
                };
                zoomReset.Click += (_, _) => SetPreviewEditorZoom(100);
                flyout.Items.Add(zoomReset);

                flyout.Items.Add(new MenuFlyoutSeparator());

                var displaySettingsItem = new MenuFlyoutItem
                {
                    Text = "Change editor font/colors...",
                    Icon = new SymbolIcon(Symbol.Setting),
                };
                displaySettingsItem.Click += (_, _) => OpenSettingsTab(SettingsDisplayTabIndex);
                flyout.Items.Add(displaySettingsItem);
            }

            AddPreviewEditorZoomAccelerator(VirtualKey.Add, +PreviewEditorZoomStep);
            // OEM '=' / '+' key (top row) — KeyboardAccelerator fires on the
            // physical key regardless of shift, so Ctrl+= also zooms in.
            AddPreviewEditorZoomAccelerator((VirtualKey)0xBB, +PreviewEditorZoomStep);
            AddPreviewEditorZoomAccelerator(VirtualKey.Subtract, -PreviewEditorZoomStep);
            // OEM '-'.
            AddPreviewEditorZoomAccelerator((VirtualKey)0xBD, -PreviewEditorZoomStep);
            AddPreviewEditorZoomAccelerator(VirtualKey.Number0, 0);
            AddPreviewEditorZoomAccelerator(VirtualKey.NumberPad0, 0);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", "InitializePreviewEditorZoom failed: " + ex.Message);
        }
    }

    private void AddPreviewEditorZoomAccelerator(VirtualKey key, int deltaPercent)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = VirtualKeyModifiers.Control,
        };
        accelerator.Invoked += (_, args) =>
        {
            if (PreviewEditor.Visibility != Visibility.Visible) return;
            if (deltaPercent == 0) SetPreviewEditorZoom(100);
            else AdjustPreviewEditorZoom(deltaPercent);
            args.Handled = true;
        };
        PreviewEditor.KeyboardAccelerators.Add(accelerator);
    }

    private void AddPreviewEditorSaveAccelerator()
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.S,
            Modifiers = VirtualKeyModifiers.Control,
        };
        accelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            if (PreviewEditor.Visibility != Visibility.Visible) return;
            if (!_previewEditorDirty || _previewEditorPath is null) return;
            await SavePreviewEditAsync();
        };
        PreviewEditor.KeyboardAccelerators.Add(accelerator);
    }

    private void AdjustPreviewEditorZoom(int deltaPercent)
    {
        SetPreviewEditorZoom(PreviewEditor.ZoomFactor + deltaPercent);
    }

    private void SetPreviewEditorZoom(int percent)
    {
        if (percent < PreviewEditorZoomMin) percent = PreviewEditorZoomMin;
        else if (percent > PreviewEditorZoomMax) percent = PreviewEditorZoomMax;
        try { PreviewEditor.ZoomFactor = percent; }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"SetPreviewEditorZoom({percent}) failed: {ex.Message}");
        }
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
        // The double-click is the action the "double click to jump" intro tip
        // describes, so dismiss the tip if it is still showing.
        DismissActiveIntroTip();
        if (await TryEnterPreviewEditorAtPointAsync(block, e.GetPosition(block), filePath))
            e.Handled = true;
    }

    private async Task EditPreviewFileFromContextMenuAsync(RichTextBlock block)
    {
        var filePath = ResolvePreviewBlockFilePath(block);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (TryGetPreviewBlockContextPoint(block, filePath, out var point)
            && await TryEnterPreviewEditorAtPointAsync(block, point, filePath))
            return;

        var target = ResolvePreviewEditorFallbackResult(filePath);
        if (target is not null)
            await ShowFullFileEditorAsync(target, scrollToMatch: false);
    }

    private string? ResolvePreviewBlockFilePath(RichTextBlock block)
    {
        if (block.Tag is string tagPath && !string.IsNullOrWhiteSpace(tagPath))
            return tagPath;

        if (ReferenceEquals(block, PreviewBlock))
            return _previewResult?.FilePath;

        return null;
    }

    private SearchResult ResolvePreviewEditorFallbackResult(string filePath)
    {
        var result = ViewModel.ResultGroups
            .FirstOrDefault(g => string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            ?.FirstOrDefault();
        if (result is not null)
            return result;

        if (_previewResult is not null
            && string.Equals(_previewResult.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return _previewResult;

        return new SearchResult(filePath, 1, string.Empty, 0, 0,
            Array.Empty<string>(), Array.Empty<string>());
    }

    private SearchResult? ResolveCurrentPreviewEditorTarget()
    {
        if (_previewResult is not null)
            return _previewResult;

        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            if (_currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count)
            {
                var activeBlock = _matchParagraphs[_currentMatchIndex].block;
                var activePath = ResolvePreviewBlockFilePath(activeBlock);
                if (!string.IsNullOrWhiteSpace(activePath))
                    return ResolvePreviewEditorFallbackResult(activePath);
            }

            foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
            {
                if (!expander.IsExpanded)
                    continue;

                var block = FindFirstDescendantRichTextBlock(expander);
                var path = block is null ? null : ResolvePreviewBlockFilePath(block);
                if (!string.IsNullOrWhiteSpace(path))
                    return ResolvePreviewEditorFallbackResult(path);
            }

            foreach (var block in _sectionGutterBlocks.Keys)
            {
                var path = ResolvePreviewBlockFilePath(block);
                if (!string.IsNullOrWhiteSpace(path))
                    return ResolvePreviewEditorFallbackResult(path);
            }
        }

        var selected = ViewModel.GetAllSelectedResults();
        return selected.Count > 0 ? selected[0] : null;
    }

    private async Task<bool> TryEnterPreviewEditorAtPointAsync(
        RichTextBlock block,
        Windows.Foundation.Point point,
        string? filePath)
    {
        try
        {
            if (filePath is null)
            {
                LogService.Instance.Verbose("PreviewEditor",
                    $"TryEnterPreviewEditorAtPoint: abort — filePath null, point=({point.X:N1},{point.Y:N1})");
                return false;
            }

            // Image/PDF previews show extracted text (OCR / pdftotext); double-click / inline editing is a NOOP.
            if (IsExtractedTextPreviewPath(filePath))
            {
                LogService.Instance.Verbose("PreviewEditor",
                    $"TryEnterPreviewEditorAtPoint: abort — image preview is read-only, file='{Path.GetFileName(filePath)}'");
                return false;
            }

            // Abort only if the section body is not laid out yet (collapsed/unmeasured).
            // This is a side-effect-free, resolve-on-first-try check — NOT the stateful
            // overlay-centering settle ladder, which deliberately returns false on first
            // contact and would silently swallow a one-shot double-click.
            if (!IsPreviewSectionBodyLaidOutForPointer(block, out string layoutReason))
            {
                LogService.Instance.Verbose("PreviewEditor",
                    $"TryEnterPreviewEditorAtPoint: abort — section not laid out ({layoutReason}), file='{Path.GetFileName(filePath)}', point=({point.X:N1},{point.Y:N1})");
                return false;
            }

            TextPointer? tp;
            try { tp = block.GetPositionFromPoint(point); }
            catch (Exception ex)
            {
                LogService.Instance.Verbose("PreviewEditor",
                    $"TryEnterPreviewEditorAtPoint: GetPositionFromPoint threw {ex.GetType().Name}, point=({point.X:N1},{point.Y:N1})");
                tp = null;
            }
            int lineNum = tp is null ? -1 : ResolveLineNumberAtPointer(block, tp);
            if (lineNum <= 0)
            {
                LogService.Instance.Verbose("PreviewEditor",
                    $"TryEnterPreviewEditorAtPoint: abort — no line at point=({point.X:N1},{point.Y:N1}), tpOffset={(tp is null ? "null" : tp.Offset.ToString(CultureInfo.InvariantCulture))}, lineNum={lineNum}, file='{Path.GetFileName(filePath)}'");
                return false;
            }

            var clickedPara = FindParagraphAtOffset(block, tp!.Offset);
            int clickedMatchIndex = clickedPara is null ? -1 : ResolveMatchIndexAtPointer(clickedPara, tp);

            // Reuse an existing SearchResult for this file when possible (it has
            // MatchLine/column data that ScrollEditorToMatch uses for highlighting).
            // Prefer the result whose LineNumber and match index match the clicked
            // run so the editor highlights the word the user double-clicked, not
            // just the first match on that line.
            var fileGroup = ViewModel.ResultGroups
                .FirstOrDefault(g => string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            SearchResult? existing = ResolveSearchResultAtPreviewPoint(fileGroup, lineNum, clickedMatchIndex)
                ?? fileGroup?.FirstOrDefault(r => r.LineNumber == lineNum)
                ?? fileGroup?.FirstOrDefault();
            SearchResult target;
            if (existing is not null && existing.LineNumber == lineNum)
            {
                target = existing;
            }
            else
            {
                // No exact SearchResult for this line (e.g. a context line with
                // yellow-highlighted regex hits).  Use the search regex to find
                // the first match on that line so the editor can highlight it.
                int col = 0, len = 0;
                string matchLine = string.Empty;
                var rx = BuildSearchHighlightRegex();
                if (rx is not null)
                {
                    var paraText = new StringBuilder();
                    if (clickedPara is not null)
                    {
                        // In two-column layout, gutter runs are in a separate
                        // block — content inlines start at index 0.
                        bool hasGutterBlock = _sectionGutterBlocks.ContainsKey(block);
                        int startInline = hasGutterBlock ? 0 : 3;
                        for (int pi = startInline; pi < clickedPara.Inlines.Count; pi++)
                            if (clickedPara.Inlines[pi] is Run pr) paraText.Append(pr.Text);
                    }
                    matchLine = paraText.ToString();
                    var rxMatches = rx.Matches(matchLine);
                    var rxMatch = rxMatches.Count > 0
                        ? rxMatches[Math.Clamp(clickedMatchIndex, 0, rxMatches.Count - 1)]
                        : null;
                    if (rxMatch is { Success: true })
                    {
                        col = rxMatch.Index;
                        len = rxMatch.Length;
                    }
                }
                target = existing is not null
                    ? existing with { LineNumber = lineNum, MatchStartColumn = col, SourceMatchStartColumn = col, MatchLength = len, MatchLine = matchLine }
                    : new SearchResult(filePath, lineNum, matchLine, col, len,
                        Array.Empty<string>(), Array.Empty<string>())
                    { SourceMatchStartColumn = col };
            }

            await ShowFullFileEditorAsync(target, scrollToMatch: true);
            LogService.Instance.Verbose("PreviewEditor",
                $"TryEnterPreviewEditorAtPoint: opened editor file='{Path.GetFileName(filePath)}', line={lineNum}, matchIndex={clickedMatchIndex}, col={target.MatchStartColumn}, len={target.MatchLength}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"TryEnterPreviewEditorAtPointAsync threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static SearchResult? ResolveSearchResultAtPreviewPoint(FileGroup? fileGroup, int lineNum, int clickedMatchIndex)
    {
        if (fileGroup is null)
            return null;

        var lineResults = fileGroup
            .Where(r => r.LineNumber == lineNum)
            .OrderBy(r => r.SourceMatchStartColumn)
            .ThenBy(r => r.MatchStartColumn)
            .ThenBy(r => r.MatchLength)
            .ToList();
        if (lineResults.Count == 0)
            return null;

        if ((uint)clickedMatchIndex < (uint)lineResults.Count)
            return lineResults[clickedMatchIndex];

        return lineResults[0];
    }

    private int ResolveMatchIndexAtPointer(Paragraph para, TextPointer tp)
    {
        var matches = GetMatchRunsForParagraph(para);
        if (matches.Count == 0)
            return -1;

        int localOffset = Math.Max(0, tp.Offset - para.ContentStart.Offset);
        for (int i = 0; i < matches.Count; i++)
        {
            var (run, column) = matches[i];
            int length = run.Text?.Length ?? 0;
            if (localOffset >= column && localOffset <= column + length)
                return i;
        }

        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < matches.Count; i++)
        {
            var (run, column) = matches[i];
            int length = run.Text?.Length ?? 0;
            int startDistance = Math.Abs(localOffset - column);
            int endDistance = Math.Abs(localOffset - (column + length));
            int distance = Math.Min(startDistance, endDistance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
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

            // Prefer the line number stored via the ConditionalWeakTable (works
            // for both inline-gutter and two-column-gutter layouts).
            if (s_paragraphLineNumbers.TryGetValue(para, out var lineNumObj) && lineNumObj is int storedNum && storedNum > 0)
                return storedNum;

            // Fallback: parse from inline gutter runs (legacy single-block layout).
            for (int i = 0; i < Math.Min(3, para.Inlines.Count); i++)
            {
                if (para.Inlines[i] is not Run r) continue;
                var trimmed = r.Text.AsSpan().Trim();
                if (trimmed.IsEmpty) continue;
                int digits = 0;
                while (digits < trimmed.Length && char.IsDigit(trimmed[digits])) digits++;
                if (digits > 0 && int.TryParse(trimmed[..digits], out int n) && n > 0)
                    return n;
            }
            return -1;
        }
        return -1;
    }

    private static Paragraph? FindParagraphAtOffset(RichTextBlock block, int offset)
    {
        foreach (var b in block.Blocks)
        {
            if (b is not Paragraph para) continue;
            if (offset >= para.ContentStart.Offset && offset <= para.ContentEnd.Offset)
                return para;
        }
        return null;
    }

    private async Task ShowFullFileEditorAsync(SearchResult result, bool scrollToMatch)
    {
        LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: start file='{Path.GetFileName(result.FilePath)}', scrollToMatch={scrollToMatch}");
        if (IsExtractedTextPreviewPath(result.FilePath))
        {
            LogService.Instance.Info("Preview", "ShowFullFileEditorAsync: blocked - extracted-text (image/PDF) preview is read-only");
            return;
        }
        var editorSw = Stopwatch.StartNew();
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
                    if (fileInfo.Exists)
                    {
                        if (ShouldUseChunkedPreviewEditor(fileInfo))
                        {
                            await ShowChunkedPreviewEditorAsync(result, fileInfo, scrollToMatch, cts.Token).ConfigureAwait(true);
                            return;
                        }
                    }
                }
                catch (PreviewLoadException) { throw; }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview",
                        $"ShowFullFileEditorAsync: could not preflight editor file: {ex.GetType().Name}: {ex.Message}");
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

            var text = document.Text;
            bool singleLine = !text.Contains('\n');
            var wrapDecision = await ResolvePreviewEditorWrapAsync(result.FilePath, document.MaxLineLength, singleLine);
            if (!wrapDecision.proceed)
            {
                RestorePreviewSurfaceAfterEditor();
                ViewModel.StatusText = "Canceled opening the file for editing.";
                return;
            }

            _previewEditorPath = result.FilePath;
            _previewEditorEncoding = document.Encoding;
            ResetPreviewEditorChunkState(clearUi: false);
            ApplyPreviewEditorSyntaxHighlighting(result.FilePath);

            // Archive entries are read-only (cannot save back into zip)
            bool isArchive = ZipArchiveSearcher.IsArchivePath(result.FilePath);
            PreviewEditor.IsReadOnly = isArchive;

            _previewEditorWrapOverride = wrapDecision.wrapOverride;
            ApplyPreviewEditorWordWrap(wrapDecision.wrap);
            if (document.MaxLineLength >= PreviewEditorWrapPromptLineLength)
            {
                LogService.Instance.Info("Preview",
                    $"ShowFullFileEditorAsync: long line, wrap={wrapDecision.wrap}, override={wrapDecision.wrapOverride}, maxLen={document.MaxLineLength:N0}");
            }

            // Assign while collapsed so the editor does one document load instead of
            // repeatedly re-laying out an ever-growing text buffer.
            var textSetSw = Stopwatch.StartNew();
            _suppressPreviewEditorTextChanged = true;
            LoadPreviewEditorText(text);
            ResetPreviewEditorCaretToTop();
            _previewEditorOriginalText = GetPreviewEditorText();
            _suppressPreviewEditorTextChanged = false;
            textSetSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: assigned editor text, textLen={text.Length:N0}, elapsed={textSetSw.ElapsedMilliseconds}ms");

            _previewEditorDirty = false;
            UpdateEditorDirtyIndicator();

            var showEditorSw = Stopwatch.StartNew();
            SetPreviewEditorVisible(true);
            showEditorSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: editor visible, elapsed={showEditorSw.ElapsedMilliseconds}ms");

            PreviewEditor.Focus(FocusState.Programmatic);

            var scrollSw = Stopwatch.StartNew();
            if (scrollToMatch)
            {
                ScrollEditorToMatch(text, result);
                scrollSw.Stop();
                LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: scrolled to match line={result.LineNumber}, elapsed={scrollSw.ElapsedMilliseconds}ms");
            }
            else
            {
                QueuePreviewEditorScrollToTop();
                scrollSw.Stop();
                LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: queued scroll to top, elapsed={scrollSw.ElapsedMilliseconds}ms");
            }

            // The editor may fire deferred TextChanged after a bulk text
            // load (line-ending normalisation, layout pass, etc.).  If that
            // sets _previewEditorDirty even though the text is unchanged,
            // clear it again so the indicator doesn't show a false positive.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_previewEditorOriginalText is not null
                    && PreviewEditor.Visibility == Visibility.Visible
                    && _previewEditorDirty
                    && GetPreviewEditorText() == _previewEditorOriginalText)
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
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: done file='{Path.GetFileName(result.FilePath)}', elapsed={editorSw.ElapsedMilliseconds}ms");
            if (ReferenceEquals(_previewLoadCts, cts))
                _previewLoadCts = null;
            cts.Dispose();
            FullFileButton.IsEnabled = _previewResult is not null;
            UpdatePreviewEditorButtons();
        }
    }

    private bool ShouldUseChunkedPreviewEditor(FileInfo fileInfo)
        => fileInfo.Length > PreviewEditorChunkByteLength
            || fileInfo.Length > PreviewEditorMaxByteLength
            || fileInfo.Length > PreviewEditorMaxTextLength
            || (PreviewEditorMaxLineLength > 0 && fileInfo.Length > PreviewEditorMaxLineLength);

    private enum PreviewEditorWrapChoice { NoWrap, Wrap, Cancel }

    /// <summary>
    /// Decides the word-wrap mode for opening <paramref name="filePath"/> in the editor. Wrapping a
    /// pathologically long line is extremely slow and can crash XAML, so when the editor would wrap a
    /// line at or beyond <see cref="PreviewEditorWrapPromptLineLength"/> the user is prompted to choose
    /// between opening without wrap (recommended), with wrap anyway, or cancelling. Returns whether to
    /// proceed, the effective wrap state, and whether that wrap state is a forced override.
    /// </summary>
    private async Task<(bool proceed, bool wrap, bool? wrapOverride)> ResolvePreviewEditorWrapAsync(
        string filePath, int maxLineLength, bool singleLine)
    {
        // Single-line files default to wrap for readability; multi-line files honour the user setting.
        bool desiredWrap = ViewModel.PreviewWordWrap || singleLine;
        if (desiredWrap && maxLineLength >= PreviewEditorWrapPromptLineLength)
        {
            // Honor a saved "Don't remind me again" preference and skip the prompt entirely.
            switch (ViewModel.PreviewLongLineWarningIndex)
            {
                case 1: return (true, false, (bool?)false); // always open without word wrap
                case 2: return (true, true, (bool?)true);   // always open with word wrap
            }

            var (choice, dontRemind) = await PromptPreviewEditorLongLineWrapAsync(filePath, maxLineLength);
            // Persist the chosen behavior so the warning never appears again (also shown in Settings).
            if (dontRemind && choice is PreviewEditorWrapChoice.NoWrap or PreviewEditorWrapChoice.Wrap)
            {
                ViewModel.PreviewLongLineWarningIndex = choice == PreviewEditorWrapChoice.NoWrap ? 1 : 2;
                await ViewModel.PersistSettingsAsync();
            }

            // wrapOverride LOCKS the editor to the explicit choice so a later surface-wide wrap
            // application (driven by the global PreviewWordWrap setting) can't flip it; a manual
            // wrap-button toggle clears the override (see OnWrapModeOptionClicked).
            return choice switch
            {
                PreviewEditorWrapChoice.NoWrap => (true, false, (bool?)false),  // force no-wrap (safe)
                PreviewEditorWrapChoice.Wrap => (true, true, (bool?)true),      // force wrap (user override)
                _ => (false, false, (bool?)null),                              // cancel
            };
        }

        // No prompt: single-line files auto-wrap (force, overriding a global no-wrap); multi-line files
        // follow the global setting with NO override so the wrap button toggles them normally.
        return singleLine ? (true, true, (bool?)true) : (true, ViewModel.PreviewWordWrap, (bool?)null);
    }

    private async Task<(PreviewEditorWrapChoice choice, bool dontRemind)> PromptPreviewEditorLongLineWrapAsync(string filePath, int maxLineLength)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = $"\u201c{Path.GetFileName(filePath)}\u201d contains a very long line ({maxLineLength:N0} characters).",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Turning on word wrap for a line this long can make the editor extremely slow and may crash the app. "
                 + "Opening without word wrap is much faster, and you can still toggle wrap afterwards.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });
        // When checked, the button the user clicks becomes the saved preference (also editable in
        // Settings ▸ Built-In Editor) and this warning never appears again.
        var dontRemindCheckBox = new CheckBox
        {
            Content = "Don't remind me again (remember my choice)",
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(dontRemindCheckBox);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "This file has a very long line",
                TitleGlyph = "\uE7BA", // Warning
                Content = panel,
                PrimaryButtonText = "Open without word wrap",
                SecondaryButtonText = "Open with word wrap",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 600,
                Height = 360,
                MaxContentHeight = 260,
            });

        var choice = result switch
        {
            YaguDialogResult.Primary => PreviewEditorWrapChoice.NoWrap,
            YaguDialogResult.Secondary => PreviewEditorWrapChoice.Wrap,
            _ => PreviewEditorWrapChoice.Cancel,
        };
        return (choice, dontRemindCheckBox.IsChecked == true);
    }

    private async Task ShowChunkedPreviewEditorAsync(SearchResult result, FileInfo fileInfo, bool scrollToMatch, CancellationToken cancellationToken)
    {
        var chunkSw = Stopwatch.StartNew();
        var chunk = await LoadPreviewEditorChunkAsync(
            result.FilePath,
            offset: 0,
            maxBytes: PreviewEditorChunkByteLength,
            encoding: null,
            cancellationToken).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
            return;

        bool chunkSingleLine = !chunk.Text.Contains('\n');
        var wrapDecision = await ResolvePreviewEditorWrapAsync(result.FilePath, chunk.MaxLineLength, chunkSingleLine);
        if (!wrapDecision.proceed)
        {
            RestorePreviewSurfaceAfterEditor();
            ViewModel.StatusText = "Canceled opening the file for editing.";
            return;
        }

        _previewEditorPath = result.FilePath;
        _previewEditorEncoding = chunk.Encoding;
        _previewEditorChunkEncoding = chunk.Encoding;
        _previewEditorChunked = true;
        _previewEditorLoadedByteLength = chunk.NextByteOffset;
        _previewEditorTotalByteLength = chunk.TotalByteLength;

        ApplyPreviewEditorSyntaxHighlighting(result.FilePath);

        PreviewEditor.IsReadOnly = false;
        _previewEditorWrapOverride = wrapDecision.wrapOverride;
        ApplyPreviewEditorWordWrap(wrapDecision.wrap);

        _suppressPreviewEditorTextChanged = true;
        LoadPreviewEditorText(chunk.Text);
        ResetPreviewEditorCaretToTop();
        _previewEditorOriginalText = null;
        _suppressPreviewEditorTextChanged = false;
        _previewEditorDirty = false;
        UpdateEditorDirtyIndicator();

        SetPreviewEditorVisible(true);
        PreviewEditor.Focus(FocusState.Programmatic);
        if (scrollToMatch)
            ScrollEditorToMatch(chunk.Text, result);
        else
            QueuePreviewEditorScrollToTop();
        UpdatePreviewEditorChunkUi();
        HookPreviewEditorChunkScroll();

        chunkSw.Stop();
        LogService.Instance.Info("Preview",
            $"ShowChunkedPreviewEditorAsync: loaded first chunk file='{fileInfo.Name}', totalBytes={fileInfo.Length:N0}, loadedBytes={_previewEditorLoadedByteLength:N0}, textLen={chunk.Text.Length:N0}, maxLineLen={chunk.MaxLineLength:N0}, elapsed={chunkSw.ElapsedMilliseconds}ms");
        ViewModel.StatusText = $"Loaded {FormatBytes(_previewEditorLoadedByteLength)} of {FormatBytes(_previewEditorTotalByteLength)} for editing.";
    }

    private async Task LoadMorePreviewEditorChunkAsync(bool force = false)
    {
        if (!_previewEditorChunked || _previewEditorPath is null || _previewEditorEncoding is null)
            return;
        if (_previewEditorChunkLoadInFlight || _previewEditorLoadedByteLength >= _previewEditorTotalByteLength)
            return;
        if (_previewEditorDirty)
        {
            ViewModel.StatusText = "Save or close current edits before loading more of this file.";
            return;
        }

        _previewEditorChunkLoadInFlight = true;
        UpdatePreviewEditorChunkUi();
        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;
        var loadMoreSw = Stopwatch.StartNew();

        try
        {
            var chunk = await LoadPreviewEditorChunkAsync(
                _previewEditorPath,
                _previewEditorLoadedByteLength,
                PreviewEditorChunkByteLength,
                _previewEditorChunkEncoding ?? _previewEditorEncoding,
                cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested || chunk.Text.Length == 0)
                return;

            long previousBytes = _previewEditorLoadedByteLength;
            _suppressPreviewEditorTextChanged = true;
            LoadPreviewEditorText(GetPreviewEditorText() + chunk.Text);
            _previewEditorOriginalText = null;
            _suppressPreviewEditorTextChanged = false;
            _previewEditorDirty = false;
            _previewEditorLoadedByteLength = chunk.NextByteOffset;
            _previewEditorTotalByteLength = chunk.TotalByteLength;
            _previewEditorChunkEncoding = chunk.Encoding;

            loadMoreSw.Stop();
            LogService.Instance.Info("Preview",
                $"LoadMorePreviewEditorChunkAsync: appended chunk file='{Path.GetFileName(_previewEditorPath)}', fromBytes={previousBytes:N0}, toBytes={_previewEditorLoadedByteLength:N0}, textLen={chunk.Text.Length:N0}, maxLineLen={chunk.MaxLineLength:N0}, elapsed={loadMoreSw.ElapsedMilliseconds}ms");
            ViewModel.StatusText = $"Loaded {FormatBytes(_previewEditorLoadedByteLength)} of {FormatBytes(_previewEditorTotalByteLength)} for editing.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", "Could not load more editor text", ex);
            ViewModel.StatusText = $"Could not load more editor text: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_previewLoadCts, cts))
                _previewLoadCts = null;
            cts.Dispose();
            _previewEditorChunkLoadInFlight = false;
            UpdatePreviewEditorChunkUi();
            UpdatePreviewEditorButtons();
        }
    }

    private static async Task<PreviewEditorChunk> LoadPreviewEditorChunkAsync(
        string filePath,
        long offset,
        long maxBytes,
        Encoding? encoding,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long totalBytes = stream.Length;
        if (offset < 0 || offset > totalBytes)
            throw new PreviewLoadException("Could not load editor chunk: the saved file changed on disk.");

        if (offset == 0)
        {
            int probeSize = (int)Math.Min(BinaryDetector.SampleBytes, Math.Max(0, totalBytes));
            if (probeSize > 0)
            {
                var probe = new byte[probeSize];
                int probeRead = await stream.ReadAsync(probe.AsMemory(0, probeSize), cancellationToken).ConfigureAwait(false);
                if (BinaryDetector.IsBinary(probe.AsSpan(0, probeRead)))
                    throw new PreviewLoadException("Full-file editing is only available for non-binary text files.");

                encoding ??= NormalizePreviewEncoding(EncodingDetector.DetectEncoding(probe.AsSpan(0, probeRead)));
                offset = GetBomLength(probe.AsSpan(0, probeRead));
            }
        }

        encoding ??= NormalizePreviewEncoding(Encoding.UTF8);
        stream.Position = offset;

        int bytesToRead = (int)Math.Min(Math.Max(0, totalBytes - offset), maxBytes + 8);
        if (bytesToRead == 0)
            return new PreviewEditorChunk(string.Empty, encoding, totalBytes, offset, 0);

        var bytes = new byte[bytesToRead];
        int bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(bytesRead, bytes.Length - bytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            bytesRead += read;
        }

        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytesRead)];
        decoder.Convert(
            bytes,
            0,
            bytesRead,
            chars,
            0,
            chars.Length,
            flush: offset + bytesRead >= totalBytes,
            out int bytesUsed,
            out int charsUsed,
            out _);

        if (bytesUsed == 0 && bytesRead > 0)
            throw new PreviewLoadException("Could not decode the next editor chunk.");

        string text = new(chars, 0, charsUsed);
        return new PreviewEditorChunk(text, encoding, totalBytes, offset + bytesUsed, GetMaxLineLength(text));
    }

    private static Encoding NormalizePreviewEncoding(Encoding encoding)
        => encoding is UTF8Encoding
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            : encoding;

    private static int GetBomLength(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF) return 3;
        if (header.Length >= 4 && header[0] == 0xFF && header[1] == 0xFE && header[2] == 0 && header[3] == 0) return 4;
        if (header.Length >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 0xFE && header[3] == 0xFF) return 4;
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE) return 2;
        if (header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF) return 2;
        return 0;
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

    private void ResetPreviewEditorCaretToTop()
    {
        try
        {
            PreviewEditor.SetCursorPosition(0, 0, scrollIntoView: false);
            PreviewEditor.ClearSelection();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", $"ResetPreviewEditorCaretToTop failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void QueuePreviewEditorScrollToTop()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (PreviewEditor.Visibility != Visibility.Visible) return;
                PreviewEditor.SetCursorPosition(0, 0, scrollIntoView: false);
                PreviewEditor.ClearSelection();
                PreviewEditor.ScrollTopIntoView();
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview", $"QueuePreviewEditorScrollToTop failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void ScrollEditorToMatch(string text, SearchResult result)
    {
        var selection = ResolvePreviewEditorMatchSelection(text, result);

        // Defer the scroll+select to a Low-priority callback so the editor
        // control has completed its layout pass and knows its line positions.
        // Without this, ScrollLineToCenter is a no-op on freshly-loaded text.
        int targetLineIndex = selection.TargetLineIndex;
        int capturedStart = selection.Start;
        int capturedLength = selection.Length;
        if (LogService.Instance.IsVerboseEnabled)
        {
            LogService.Instance.Verbose("PreviewEditor", $"ScrollEditorToMatch: file='{Path.GetFileName(result.FilePath)}', line={result.LineNumber}, matchColumn={result.MatchStartColumn}, sourceMatchColumn={result.SourceMatchStartColumn}, resolvedColumn={selection.Column}, matchLength={result.MatchLength}, selectStart={capturedStart}, selectLength={capturedLength}, targetLineIndex={targetLineIndex}, source={selection.Source}, wordWrap={PreviewEditor.WordWrap}");
        }

        int revealVersion = ++_previewEditorRevealVersion;
        void RevealMatch(string source)
        {
            try
            {
                if (revealVersion != _previewEditorRevealVersion) return;
                if (PreviewEditor.Visibility != Visibility.Visible) return;
                if (capturedLength > 0 && capturedStart >= 0)
                {
                    int len = GetPreviewEditorTextLength();
                    int s = Math.Min(capturedStart, len);
                    int l = Math.Min(capturedLength, Math.Max(0, len - s));
                    SelectPreviewEditorText(s, l);
                }
                PreviewEditor.ScrollLineToCenter(targetLineIndex);
                PreviewEditor.ScrollIntoViewHorizontallyCentered();
                if (LogService.Instance.IsVerboseEnabled)
                    LogService.Instance.Verbose("PreviewEditor", $"RevealEditorMatch: source={source}, line={targetLineIndex}, start={capturedStart}, length={capturedLength}, wordWrap={PreviewEditor.WordWrap}");
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview", $"ScrollEditorToMatch deferred scroll failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RevealMatch("dispatcher"));

        _previewEditorRevealRetryTimer?.Stop();
        var retryTimer = DispatcherQueue.CreateTimer();
        _previewEditorRevealRetryTimer = retryTimer;
        retryTimer.Interval = TimeSpan.FromMilliseconds(75);
        retryTimer.IsRepeating = false;
        retryTimer.Tick += (_, _) =>
        {
            retryTimer.Stop();
            if (ReferenceEquals(_previewEditorRevealRetryTimer, retryTimer))
                _previewEditorRevealRetryTimer = null;
            RevealMatch("timer");
        };
        retryTimer.Start();
    }

    private readonly record struct PreviewEditorMatchSelection(
        int Start,
        int Length,
        int Column,
        int TargetLineIndex,
        string Source);

    private PreviewEditorMatchSelection ResolvePreviewEditorMatchSelection(string text, SearchResult result)
    {
        int targetLineIndex = Math.Max(0, result.LineNumber - 1);
        if (!TryGetEditorLineInfo(text, result.LineNumber, out var lineText, out int lineEditorStart))
        {
            return new PreviewEditorMatchSelection(lineEditorStart, 0, 0, targetLineIndex, "line-missing");
        }

        int requestedColumn = Math.Clamp(Math.Max(result.SourceMatchStartColumn, result.MatchStartColumn), 0, lineText.Length);
        int resolvedColumn = requestedColumn;
        int resolvedLength = Math.Clamp(result.MatchLength, 0, Math.Max(0, lineText.Length - resolvedColumn));
        string source = "result-column";

        var rx = BuildSearchHighlightRegex();
        if (rx is not null && TryFindNearestRegexMatch(lineText, rx, requestedColumn, out int regexColumn, out int regexLength))
        {
            resolvedColumn = regexColumn;
            resolvedLength = regexLength;
            source = "regex-line-match";
        }

        return new PreviewEditorMatchSelection(
            lineEditorStart + resolvedColumn,
            resolvedLength,
            resolvedColumn,
            targetLineIndex,
            source);
    }

    private static bool TryGetEditorLineInfo(string text, int lineNumber, out string lineText, out int editorLineStart)
    {
        lineText = string.Empty;
        editorLineStart = 0;
        if (lineNumber <= 0) return false;

        int currentLine = 1;
        int sourceIndex = 0;
        int editorIndex = 0;

        while (sourceIndex < text.Length && currentLine < lineNumber)
        {
            int lineStart = sourceIndex;
            while (sourceIndex < text.Length && text[sourceIndex] != '\r' && text[sourceIndex] != '\n')
                sourceIndex++;

            editorIndex += sourceIndex - lineStart;

            if (sourceIndex >= text.Length)
                return false;

            if (text[sourceIndex] == '\r' && sourceIndex + 1 < text.Length && text[sourceIndex + 1] == '\n')
                sourceIndex += 2;
            else
                sourceIndex++;

            editorIndex++;
            currentLine++;
        }

        if (currentLine != lineNumber)
            return false;

        editorLineStart = editorIndex;
        int contentStart = sourceIndex;
        while (sourceIndex < text.Length && text[sourceIndex] != '\r' && text[sourceIndex] != '\n')
            sourceIndex++;

        lineText = text[contentStart..sourceIndex];
        return true;
    }

    private static bool TryFindNearestRegexMatch(string lineText, Regex rx, int targetColumn, out int matchColumn, out int matchLength)
    {
        matchColumn = 0;
        matchLength = 0;
        Match? best = null;
        int bestDistance = int.MaxValue;

        foreach (Match match in rx.Matches(lineText))
        {
            if (!match.Success || match.Length <= 0)
                continue;

            int distance = targetColumn >= match.Index && targetColumn <= match.Index + match.Length
                ? 0
                : Math.Min(Math.Abs(match.Index - targetColumn), Math.Abs(match.Index + match.Length - targetColumn));

            if (distance < bestDistance)
            {
                best = match;
                bestDistance = distance;
            }
        }

        if (best is null)
            return false;

        matchColumn = best.Index;
        matchLength = best.Length;
        return true;
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

            var textToSave = GetPreviewEditorText();
            if (TextHasUnencodableCharacters(textToSave, _previewEditorEncoding))
            {
                if (_previewEditorChunked && _previewEditorLoadedByteLength < _previewEditorTotalByteLength)
                {
                    ViewModel.StatusText = "Could not save: loaded edits contain characters that cannot be written with the original encoding.";
                    return false;
                }

                var encChoice = await YaguDialog.ShowAsync(
                    _hwnd,
                    new YaguDialogOptions
                    {
                        Title = "Encoding Warning",
                        TitleGlyph = "\uE7BA", // Warning
                        Content = $"This file contains characters that cannot be represented in {GetEncodingDisplayName(_previewEditorEncoding)}. Save as UTF-8 instead?",
                        PrimaryButtonText = "Save as UTF-8",
                        CloseButtonText = "Cancel",
                        DefaultButton = YaguDialogDefaultButton.Primary,
                        Width = 560,
                        Height = 280,
                    });
                if (encChoice != YaguDialogResult.Primary) return false;
                _previewEditorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            await SavePreviewEditorTextToDiskAsync(textToSave).ConfigureAwait(true);
            await VerifyPreviewEditorSavedTextAsync(textToSave).ConfigureAwait(true);
            _previewEditorOriginalText = _previewEditorChunked ? null : textToSave;
            _previewEditorDirty = false;
            UpdatePreviewEditorButtons();

            if (!_previewEditorChunked || _previewEditorLoadedByteLength >= _previewEditorTotalByteLength)
            {
                // Re-validate search results for this file against the saved content.
                bool fileStillHasMatches = ViewModel.RevalidateFileResults(_previewEditorPath, textToSave);
                if (!fileStillHasMatches && _previewResult?.FilePath is not null &&
                    string.Equals(_previewResult.FilePath, _previewEditorPath, StringComparison.OrdinalIgnoreCase))
                {
                    _previewResult = null;
                }
            }

            ViewModel.StatusText = $"Saved {_previewEditorPath}.";
            ShowPreviewEditorSavedOverlay();
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

    private async Task SavePreviewEditorTextToDiskAsync(string textToSave)
    {
        if (_previewEditorPath is null || _previewEditorEncoding is null)
            return;

        if (!_previewEditorChunked)
        {
            await File.WriteAllTextAsync(_previewEditorPath, textToSave, _previewEditorEncoding).ConfigureAwait(true);
            return;
        }

        var tempPath = _previewEditorPath + $".yagutmp-{Guid.NewGuid():N}";
        long encodedLoadedBytes = 0;
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var writer = new StreamWriter(output, _previewEditorEncoding, bufferSize: 128 * 1024, leaveOpen: true))
                {
                    await writer.WriteAsync(textToSave).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                encodedLoadedBytes = output.Position;

                if (_previewEditorLoadedByteLength < _previewEditorTotalByteLength)
                {
                    await using var input = new FileStream(
                        _previewEditorPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    input.Position = _previewEditorLoadedByteLength;
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, _previewEditorPath, overwrite: true);
            long totalByteLength = new FileInfo(_previewEditorPath).Length;
            await ApplyPreviewEditorChunkSaveStateAsync(encodedLoadedBytes, totalByteLength).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    private async Task ApplyPreviewEditorChunkSaveStateAsync(long loadedByteLength, long totalByteLength)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                _previewEditorLoadedByteLength = loadedByteLength;
                _previewEditorTotalByteLength = totalByteLength;
                UpdatePreviewEditorChunkUi();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!queued)
        {
            completion.TrySetException(new InvalidOperationException("Could not enqueue preview editor chunk UI update."));
        }

        await completion.Task.ConfigureAwait(false);
    }

    private async Task VerifyPreviewEditorSavedTextAsync(string expectedText)
    {
        if (_previewEditorPath is null || _previewEditorEncoding is null || _previewEditorChunked)
            return;

        var savedText = await File.ReadAllTextAsync(_previewEditorPath, _previewEditorEncoding).ConfigureAwait(true);
        if (!string.Equals(savedText, expectedText, StringComparison.Ordinal))
            throw new IOException("Saved file verification failed: the file contents on disk do not match the editor text.");
    }

    private void ShowPreviewEditorSavedOverlay()
    {
        _previewEditorSavedOverlayTimer?.Stop();
        _previewEditorSavedOverlayTimer = null;

        if (!ViewModel.ShowEditorSavedOverlay || PreviewEditor.Visibility != Visibility.Visible)
        {
            PreviewEditorSavedOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewEditorSavedOverlay.Visibility = Visibility.Visible;

        var timer = DispatcherQueue.CreateTimer();
        _previewEditorSavedOverlayTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(PreviewEditorSavedOverlayDurationMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_previewEditorSavedOverlayTimer, timer))
                _previewEditorSavedOverlayTimer = null;
            PreviewEditorSavedOverlay.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private void HidePreviewEditorSavedOverlay()
    {
        _previewEditorSavedOverlayTimer?.Stop();
        _previewEditorSavedOverlayTimer = null;
        PreviewEditorSavedOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Returns true if the caller should proceed (edits were saved or discarded), false to cancel.</summary>
    private async Task<bool> ConfirmDiscardPreviewEditAsync()
    {
        var choice = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Unsaved changes",
                TitleGlyph = "\uE74E", // Save
                Content = "The right-panel editor has unsaved changes.",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 500,
                Height = 270,
                ShowTitleBar = false,
                // Sit in the top-right near the editor's Save/Back controls instead of covering the
                // center of the window, so the underlying edit stays visible while deciding.
                Placement = YaguDialogPlacement.TopRightOverOwner,
            });
        if (choice == YaguDialogResult.Primary)
        {
            // Save first, then allow the close/navigation to proceed.
            return await SavePreviewEditAsync();
        }
        return choice == YaguDialogResult.Secondary; // Discard -> true, Cancel -> false
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
        HidePreviewEditorSavedOverlay();
        _previewEditorRevealVersion++;
        _previewEditorRevealRetryTimer?.Stop();
        _previewEditorRevealRetryTimer = null;
        _previewEditorPath = null;
        _previewEditorEncoding = null;
        _previewEditorDirty = false;
        _previewEditorOriginalText = null;
        _previewEditorWrapOverride = null;
        ResetPreviewEditorChunkState();

        if (clearText)
        {
            _suppressPreviewEditorTextChanged = true;
            LoadPreviewEditorText(string.Empty);
            _suppressPreviewEditorTextChanged = false;
        }

        UpdatePreviewEditorButtons();
    }

    private void SetPreviewEditorVisible(bool visible)
    {
        PreviewEditorContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewEditor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewScrollViewer.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        PreviewHeaderBar.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        // Any editor-visibility change dismisses the "added to preview" snackbar so it can never
        // reappear stale the next time the editor is shown.
        HidePreviewAddedToast();

        if (visible)
        {
            PreviewEmptyState.Visibility = Visibility.Collapsed;
            HideStickyFileHeader();
            // Showing the editor collapses the preview surface. Cancel any in-flight
            // match-navigation scroll/overlay retries queued by the last Next/Prev
            // navigation; otherwise they keep firing against the now-collapsed
            // PreviewScrollViewer and detached preview runs, dereferencing torn-down
            // native XAML layout and crashing (access violation in Microsoft.UI.Xaml.dll).
            CancelPendingPreviewMatchNavigation();
            SectionNavOverlay.Visibility = Visibility.Collapsed;
            MatchNavPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateMatchNavPanel();
            UpdatePreviewEmptyState();
            // Sections added while the editor covered the preview were deferred as
            // lazy/collapsed (see PrependPreviewSectionsForFilesAsync) so the
            // surface could be shown instantly. Now that the preview is visible
            // again, materialize the ones in view so the user sees content instead
            // of a wall of collapsed drawers.
            if (_lazySections.Count > 0)
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    MaterializeVisibleLazySections);
        }

        if (visible)
        {
            PreviewEditorPathText.Text = _previewEditorPath ?? string.Empty;
            ToolTipService.SetToolTip(PreviewEditorPathBar, _previewEditorPath ?? string.Empty);
            SyncWrapModeToggles(ViewModel.PreviewWrapModeIndex);
            SyncPreviewEditorFindHighlights();
        }
        else
        {
            CancelPreviewEditorActiveFindSelectionRefresh();
            if (FindBar.Visibility == Visibility.Visible)
                CloseFindBar();
            else
                ClearPreviewEditorFindHighlights();
        }

        // Editor-mode group (now in its own Grid.Column="1" panel)
        EditorButtonsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // Hide view/action buttons while editing
        FullFileButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        OpenInDefaultAppButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        OpenInEditorButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        ClearPreviewButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        PreviewContextPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        LayoutDropDown.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        if (visible)
            PreviewToolbarContent.Visibility = Visibility.Visible;
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        UpdatePreviewEditorChunkUi();
    }

    private void ApplyPreviewEditorWordWrap(bool wrap)
    {
        bool before = PreviewEditor.WordWrap;
        PreviewEditor.WordWrap = wrap;
        if (wrap)
            PreviewEditor.HorizontalScroll = 0;
        PreviewEditor.UpdateLayout();
        if (LogService.Instance.IsVerboseEnabled)
        {
            LogService.Instance.Verbose("PreviewEditor", $"ApplyPreviewEditorWordWrap: before={before}, requested={wrap}, after={PreviewEditor.WordWrap}, override={_previewEditorWrapOverride}, setting={ViewModel.PreviewWordWrap}, visible={PreviewEditor.Visibility == Visibility.Visible}");
        }
    }

    private void ApplyPreviewEditorFontSettings()
    {
        string family = string.IsNullOrWhiteSpace(ViewModel.PreviewEditorFontFamily)
            ? AppSettings.DefaultPreviewEditorFontFamily
            : ViewModel.PreviewEditorFontFamily.Trim();
        int size = Math.Clamp(
            ViewModel.PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : ViewModel.PreviewEditorFontSize,
            6,
            72);

        try
        {
            PreviewEditor.FontFamily = new FontFamily(family);
            PreviewEditor.FontSize = size;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"ApplyPreviewEditorFontSettings failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Selects a syntax-highlighting language for the built-in editor based on the
    /// file name/extension. Disables highlighting when the user has turned the
    /// feature off or when no matching language is found.
    /// </summary>
    private void ApplyPreviewEditorSyntaxHighlighting(string? filePath)
    {
        try
        {
            if (!ViewModel.EditorSyntaxHighlightingEnabled)
            {
                PreviewEditor.EnableSyntaxHighlighting = false;
                PreviewEditor.SyntaxHighlighting = null;
                return;
            }

            var language = EditorSyntaxHighlightingResolver.ResolveFromFileName(filePath);
            if (language is null)
            {
                PreviewEditor.EnableSyntaxHighlighting = false;
                PreviewEditor.SyntaxHighlighting = null;
                return;
            }

            var languageId = MapToSyntaxHighlightId(language.Value);
            PreviewEditor.EnableSyntaxHighlighting = true;
            PreviewEditor.SelectSyntaxHighlightingById(languageId);

            if (LogService.Instance.IsVerboseEnabled)
            {
                LogService.Instance.Verbose("PreviewEditor",
                    $"ApplyPreviewEditorSyntaxHighlighting: file='{Path.GetFileName(filePath)}', language={languageId}");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditor", $"ApplyPreviewEditorSyntaxHighlighting failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static TextControlBoxNS.SyntaxHighlightID MapToSyntaxHighlightId(EditorSyntaxLanguage language) => language switch
    {
        EditorSyntaxLanguage.Batch => TextControlBoxNS.SyntaxHighlightID.Batch,
        EditorSyntaxLanguage.Cpp => TextControlBoxNS.SyntaxHighlightID.Cpp,
        EditorSyntaxLanguage.CSharp => TextControlBoxNS.SyntaxHighlightID.CSharp,
        EditorSyntaxLanguage.Inifile => TextControlBoxNS.SyntaxHighlightID.Inifile,
        EditorSyntaxLanguage.Toml => TextControlBoxNS.SyntaxHighlightID.TOML,
        EditorSyntaxLanguage.CSS => TextControlBoxNS.SyntaxHighlightID.CSS,
        EditorSyntaxLanguage.CSVImproved => TextControlBoxNS.SyntaxHighlightID.CSVImproved,
        EditorSyntaxLanguage.GCode => TextControlBoxNS.SyntaxHighlightID.GCode,
        EditorSyntaxLanguage.Gitignore => TextControlBoxNS.SyntaxHighlightID.Gitignore,
        EditorSyntaxLanguage.HexFile => TextControlBoxNS.SyntaxHighlightID.HexFile,
        EditorSyntaxLanguage.Html => TextControlBoxNS.SyntaxHighlightID.Html,
        EditorSyntaxLanguage.Java => TextControlBoxNS.SyntaxHighlightID.Java,
        EditorSyntaxLanguage.Javascript => TextControlBoxNS.SyntaxHighlightID.Javascript,
        EditorSyntaxLanguage.Json => TextControlBoxNS.SyntaxHighlightID.Json,
        EditorSyntaxLanguage.Latex => TextControlBoxNS.SyntaxHighlightID.Latex,
        EditorSyntaxLanguage.Lua => TextControlBoxNS.SyntaxHighlightID.Lua,
        EditorSyntaxLanguage.Markdown => TextControlBoxNS.SyntaxHighlightID.Markdown,
        EditorSyntaxLanguage.PHP => TextControlBoxNS.SyntaxHighlightID.PHP,
        EditorSyntaxLanguage.Python => TextControlBoxNS.SyntaxHighlightID.Python,
        EditorSyntaxLanguage.QSharp => TextControlBoxNS.SyntaxHighlightID.QSharp,
        EditorSyntaxLanguage.XML => TextControlBoxNS.SyntaxHighlightID.XML,
        EditorSyntaxLanguage.SQL => TextControlBoxNS.SyntaxHighlightID.SQL,
        EditorSyntaxLanguage.X86Assembly => TextControlBoxNS.SyntaxHighlightID.x86Assembly,
        _ => TextControlBoxNS.SyntaxHighlightID.None,
    };

    private void UpdatePreviewEditorChunkUi()
    {
        if (!_previewEditorChunked || _previewEditorTotalByteLength <= 0)
        {
            PreviewEditorChunkStatusText.Visibility = Visibility.Collapsed;
            PreviewEditorChunkPanel.Visibility = Visibility.Collapsed;
            return;
        }

        bool fullyLoaded = _previewEditorLoadedByteLength >= _previewEditorTotalByteLength;
        PreviewEditorChunkStatusText.Visibility = Visibility.Visible;
        PreviewEditorChunkStatusText.Text = fullyLoaded
            ? $"Loaded {FormatBytes(_previewEditorTotalByteLength)}"
            : $"Loaded {FormatBytes(_previewEditorLoadedByteLength)} of {FormatBytes(_previewEditorTotalByteLength)}";
        PreviewEditorChunkPanel.Visibility = fullyLoaded ? Visibility.Collapsed : Visibility.Visible;
        LoadMorePreviewEditorChunkButton.IsEnabled = !fullyLoaded && !_previewEditorChunkLoadInFlight && !_previewEditorDirty;
        LoadMorePreviewEditorChunkButton.Visibility = fullyLoaded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HookPreviewEditorChunkScroll()
    {
        if (!_previewEditorChunked)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var scrollViewer = EditorScrollHelper.FindFirstScrollViewerInTree(PreviewEditor);
            if (scrollViewer is null || ReferenceEquals(scrollViewer, _previewEditorScrollViewer))
                return;

            if (_previewEditorScrollViewer is not null)
                _previewEditorScrollViewer.ViewChanged -= OnPreviewEditorScrollViewChanged;

            _previewEditorScrollViewer = scrollViewer;
            _previewEditorScrollViewer.ViewChanged += OnPreviewEditorScrollViewChanged;
        });
    }

    private void OnPreviewEditorScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || sender is not ScrollViewer scrollViewer)
            return;
        if (!_previewEditorChunked || _previewEditorChunkLoadInFlight || _previewEditorDirty)
            return;
        if (_previewEditorLoadedByteLength >= _previewEditorTotalByteLength)
            return;
        if (scrollViewer.ScrollableHeight <= 0)
            return;

        double remaining = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
        double threshold = Math.Max(200, scrollViewer.ViewportHeight * 0.75);
        if (remaining <= threshold)
            _ = LoadMorePreviewEditorChunkAsync();
    }

    private void ResetPreviewEditorChunkState(bool clearUi = true)
    {
        _previewEditorChunked = false;
        _previewEditorChunkLoadInFlight = false;
        _previewEditorLoadedByteLength = 0;
        _previewEditorTotalByteLength = 0;
        _previewEditorChunkEncoding = null;
        if (_previewEditorScrollViewer is not null)
        {
            _previewEditorScrollViewer.ViewChanged -= OnPreviewEditorScrollViewChanged;
            _previewEditorScrollViewer = null;
        }

        if (clearUi)
        {
            PreviewEditorChunkStatusText.Text = string.Empty;
            PreviewEditorChunkStatusText.Visibility = Visibility.Collapsed;
            PreviewEditorChunkPanel.Visibility = Visibility.Collapsed;
            LoadMorePreviewEditorChunkButton.IsEnabled = true;
            LoadMorePreviewEditorChunkButton.Visibility = Visibility.Visible;
        }
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
            SetHorizontalPreviewScroll(PreviewScrollViewer, enabled: false);

            UpdateMatchNavPanel();
            if (_activeSectionNav is null && _sectionMatchNavs.Count == 1)
            {
                var sectionNav = _sectionMatchNavs.Values.FirstOrDefault();
                if (sectionNav is not null && sectionNav.Matches.Count > 1)
                    _activeSectionNav = sectionNav;
            }

            HighlightActiveExpander();
            UpdateSectionNavOverlay();
            UpdateStickyFileHeader();
            UpdatePreviewEmptyState();
            return;
        }

        if (PreviewBlock.Blocks.Count > 0)
        {
            PreviewMessagePanel.Visibility = Visibility.Visible;
            PreviewBlock.Visibility = Visibility.Visible;
            PreviewSectionsPanel.Visibility = Visibility.Collapsed;
            SetPerFileToolbarVisibility(Visibility.Visible);
            PreviewToolbarContent.Visibility = _previewResult is not null ? Visibility.Visible : Visibility.Collapsed;
            ApplyPreviewHorizontalScrollForWrap(PreviewScrollViewer, ViewModel.PreviewWordWrap);
            HideStickyFileHeader();
            UpdatePreviewEmptyState();
            return;
        }

        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        _previewResult = null;
        UpdatePreviewEmptyState();
    }

    // ----------------------------------------------------------------------------------------------
    // "Added to preview" snackbar — feedback for previewing files from the left panel while the
    // full-file editor is open (the preview surface is hidden behind the editor, so the user would
    // otherwise get no confirmation). The View button leaves the editor and scrolls the preview list
    // to the last-added drawer.
    // ----------------------------------------------------------------------------------------------
    private string? _previewAddedToastTargetFile;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewAddedToastTimer;

    /// <summary>Shows the "added to preview" snackbar over the editor, remembering the drawer to jump
    /// to. Only meaningful while the editor is visible; callers gate on that.</summary>
    private void ShowPreviewAddedToast(string targetFile, int addedCount)
    {
        if (string.IsNullOrEmpty(targetFile))
            return;

        _previewAddedToastTargetFile = targetFile;
        PreviewAddedToastText.Text = addedCount > 1
            ? $"{addedCount} files added to preview"
            : "Added to preview";
        PreviewAddedToast.Visibility = Visibility.Visible;

        if (_previewAddedToastTimer is null)
        {
            _previewAddedToastTimer = DispatcherQueue.CreateTimer();
            _previewAddedToastTimer.Interval = TimeSpan.FromSeconds(3);
            _previewAddedToastTimer.Tick += (_, _) => HidePreviewAddedToast();
        }
        _previewAddedToastTimer.Stop();
        _previewAddedToastTimer.Start();
    }

    private void HidePreviewAddedToast()
    {
        _previewAddedToastTimer?.Stop();
        if (PreviewAddedToast is not null)
            PreviewAddedToast.Visibility = Visibility.Collapsed;
    }

    private async void OnPreviewAddedToastViewClick(object sender, RoutedEventArgs e)
    {
        string? target = _previewAddedToastTargetFile;
        HidePreviewAddedToast();

        // Leave the editor (respecting unsaved edits) so the preview surface becomes visible again.
        if (PreviewEditor.Visibility == Visibility.Visible)
        {
            if (HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync())
                return; // user chose to keep editing — leave the editor open
            ClosePreviewEditor();
        }

        // Scroll to the added drawer after the restored surface has laid out.
        if (!string.IsNullOrEmpty(target))
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => TryScrollToPreviewSection(target!));
        }
    }

    private void OnPreviewAddedToastDismissClick(object sender, RoutedEventArgs e) => HidePreviewAddedToast();

    /// <summary>
    /// Returns true only if the editor text actually differs from the
    /// originally-loaded content.  This avoids false positives caused by
    /// the editor raising deferred TextChanged events after a bulk load.
    /// </summary>
    private bool HasRealEditorChanges() =>
        _previewEditorDirty
        && (_previewEditorChunked
            || (_previewEditorOriginalText is not null && GetPreviewEditorText() != _previewEditorOriginalText));

    private string GetPreviewEditorText() => PreviewEditor.GetText();

    private int GetPreviewEditorTextLength() => (int)Math.Min(int.MaxValue, PreviewEditor.CharacterCount());

    private void LoadPreviewEditorText(string text)
    {
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("PreviewEditor", $"LoadPreviewEditorText: length={text.Length}, wordWrap={PreviewEditor.WordWrap}");
        PreviewEditor.LoadText(text, autodetectTabsSpaces: false);
        PreviewEditor.ClearUndoRedoHistory();
    }

    private void SelectPreviewEditorText(int start, int length)
    {
        int textLength = GetPreviewEditorTextLength();
        int clampedStart = Math.Clamp(start, 0, textLength);
        int clampedLength = Math.Clamp(length, 0, Math.Max(0, textLength - clampedStart));
        if (LogService.Instance.IsVerboseEnabled)
        {
            LogService.Instance.Verbose("PreviewEditor", $"SelectPreviewEditorText: requestedStart={start}, requestedLength={length}, textLength={textLength}, clampedStart={clampedStart}, clampedLength={clampedLength}, wordWrap={PreviewEditor.WordWrap}, searchOpen={PreviewEditor.SearchIsOpen}");
        }
        PreviewEditor.SetActiveSearchSelection(clampedStart, clampedLength);
    }

    private void UpdatePreviewEditorButtons()
    {
        SavePreviewEditButton.IsEnabled = PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && _previewEditorPath is not null;
        UpdatePreviewEditorChunkUi();
        UpdateEditorDirtyIndicator();
    }

    private void UpdateEditorDirtyIndicator()
    {
        PreviewEditorDirtyAsterisk.Visibility =
            PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && _previewEditorPath is not null
                ? Visibility.Visible
                : Visibility.Collapsed;

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
