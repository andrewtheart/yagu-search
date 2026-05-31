using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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
    private const long PreviewEditorChunkByteLength = 10L * 1024 * 1024;
    private long PreviewEditorMaxByteLength => (long)ViewModel.PreviewEditorMaxSizeMB * 1024 * 1024;
    private int PreviewEditorMaxTextLength => ViewModel.PreviewEditorMaxTextLength;
    private int PreviewEditorMaxLineLength => ViewModel.PreviewEditorMaxLineLength;
    private bool _previewEditorForcedWrap;
    private bool _previewEditorChunked;
    private bool _previewEditorChunkLoadInFlight;
    private long _previewEditorLoadedByteLength;
    private long _previewEditorTotalByteLength;
    private Encoding? _previewEditorChunkEncoding;
    private ScrollViewer? _previewEditorScrollViewer;

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

    private void OnPreviewEditorTextChanged(TextControlBoxNS.TextControlBox sender)
    {
        if (_suppressPreviewEditorTextChanged) return;
        if (PreviewEditor.Visibility != Visibility.Visible) return;
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
            var flyout = PreviewEditor.ContextFlyout;
            if (flyout is not null)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());

                var zoomIn = new MenuFlyoutItem
                {
                    Text = "Zoom in",
                    Icon = new SymbolIcon { Symbol = Symbol.ZoomIn },
                    KeyboardAcceleratorTextOverride = "Ctrl + +",
                };
                zoomIn.Click += (_, _) => AdjustPreviewEditorZoom(+PreviewEditorZoomStep);
                flyout.Items.Add(zoomIn);

                var zoomOut = new MenuFlyoutItem
                {
                    Text = "Zoom out",
                    Icon = new SymbolIcon { Symbol = Symbol.ZoomOut },
                    KeyboardAcceleratorTextOverride = "Ctrl + -",
                };
                zoomOut.Click += (_, _) => AdjustPreviewEditorZoom(-PreviewEditorZoomStep);
                flyout.Items.Add(zoomOut);

                var zoomReset = new MenuFlyoutItem
                {
                    Text = "Reset zoom",
                    KeyboardAcceleratorTextOverride = "Ctrl + 0",
                };
                zoomReset.Click += (_, _) => SetPreviewEditorZoom(100);
                flyout.Items.Add(zoomReset);
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
        try
        {
            if (filePath is null) return;

            // Abort if the section layout is still animating/oscillating — calling
            // GetPositionFromPoint while the native text layout engine is mid-reflow
            // can trigger an access violation in Microsoft.UI.Xaml.dll.
            if (!IsPreviewSectionBodySettledForActiveOverlay(block, out _))
                return;

            var pt = e.GetPosition(block);
            TextPointer? tp;
            try { tp = block.GetPositionFromPoint(pt); }
            catch { tp = null; }
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
                var rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);
                if (rx is not null)
                {
                    var paraText = new System.Text.StringBuilder();
                    var clickedPara = FindParagraphAtOffset(block, tp!.Offset);
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
                    var rxMatch = rx.Match(matchLine);
                    if (rxMatch.Success)
                    {
                        col = rxMatch.Index;
                        len = rxMatch.Length;
                    }
                }
                target = existing is not null
                    ? existing with { LineNumber = lineNum, MatchStartColumn = col, MatchLength = len, MatchLine = matchLine }
                    : new SearchResult(filePath, lineNum, matchLine, col, len,
                        Array.Empty<string>(), Array.Empty<string>());
            }

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
                    if (fileInfo.Exists)
                    {
                        if (ShouldUseChunkedPreviewEditor(fileInfo))
                        {
                            await ShowChunkedPreviewEditorAsync(result, fileInfo, cts.Token).ConfigureAwait(true);
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

            _previewEditorPath = result.FilePath;
            _previewEditorEncoding = document.Encoding;
            ResetPreviewEditorChunkState(clearUi: false);

            // Archive entries are read-only (cannot save back into zip)
            bool isArchive = ZipArchiveSearcher.IsArchivePath(result.FilePath);
            PreviewEditor.IsReadOnly = isArchive;

            var text = document.Text;
                    _previewEditorForcedWrap = false;
            ApplyPreviewEditorWordWrap(ViewModel.PreviewWordWrap);
                    if (document.MaxLineLength >= PreviewEditorForceWrapLineLength)
            {
                LogService.Instance.Info("Preview",
                        $"ShowFullFileEditorAsync: long line loaded with user word-wrap setting, maxLen={document.MaxLineLength:N0}");
            }

            // Auto-enable word wrap for single-line files.
            if (!text.Contains('\n'))
            {
                _previewEditorForcedWrap = true;
                ApplyPreviewEditorWordWrap(true);
            }

            // Assign while collapsed so the editor does one document load instead of
            // repeatedly re-laying out an ever-growing text buffer.
            var textSetSw = System.Diagnostics.Stopwatch.StartNew();
            _suppressPreviewEditorTextChanged = true;
            LoadPreviewEditorText(text);
            _previewEditorOriginalText = GetPreviewEditorText();
            _suppressPreviewEditorTextChanged = false;
            textSetSw.Stop();
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: assigned editor text, textLen={text.Length:N0}, elapsed={textSetSw.ElapsedMilliseconds}ms");

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
            LogService.Instance.Info("Preview", $"ShowFullFileEditorAsync: done file='{System.IO.Path.GetFileName(result.FilePath)}', elapsed={editorSw.ElapsedMilliseconds}ms");
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

    private async Task ShowChunkedPreviewEditorAsync(SearchResult result, FileInfo fileInfo, CancellationToken cancellationToken)
    {
        var chunkSw = System.Diagnostics.Stopwatch.StartNew();
        var chunk = await LoadPreviewEditorChunkAsync(
            result.FilePath,
            offset: 0,
            maxBytes: PreviewEditorChunkByteLength,
            encoding: null,
            cancellationToken).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
            return;

        _previewEditorPath = result.FilePath;
        _previewEditorEncoding = chunk.Encoding;
        _previewEditorChunkEncoding = chunk.Encoding;
        _previewEditorChunked = true;
        _previewEditorLoadedByteLength = chunk.NextByteOffset;
        _previewEditorTotalByteLength = chunk.TotalByteLength;

        PreviewEditor.IsReadOnly = false;
        _previewEditorForcedWrap = false;
        ApplyPreviewEditorWordWrap(ViewModel.PreviewWordWrap);

        _suppressPreviewEditorTextChanged = true;
        LoadPreviewEditorText(chunk.Text);
        _previewEditorOriginalText = null;
        _suppressPreviewEditorTextChanged = false;
        _previewEditorDirty = false;
        UpdateEditorDirtyIndicator();

        SetPreviewEditorVisible(true);
        PreviewEditor.Focus(FocusState.Programmatic);
        ScrollEditorToMatch(chunk.Text, result);
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
        var loadMoreSw = System.Diagnostics.Stopwatch.StartNew();

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

    private void ScrollEditorToMatch(string text, SearchResult result)
    {
        // The editor's SetSelection counts each line break as exactly 1 character
        // regardless of the actual line ending (CRLF/LF/CR).  Compute the offset
        // using that same convention: add lineLength+1 per line traversed.
        int charOffset = 0;
        int currentLine = 1;
        int i = 0;
        while (i < text.Length && currentLine < result.LineNumber)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i += 2; // skip \r\n in the source
            }
            else if (text[i] == '\n' || text[i] == '\r')
            {
                i++;
            }
            else
            {
                charOffset++;
                i++;
                continue;
            }
            // Each newline counts as 1 in the editor's position model.
            charOffset++;
            currentLine++;
        }

        // charOffset is calculated against the text we loaded into the editor.
        // Select the matched portion.
        int selectStart = charOffset + result.MatchStartColumn;
        int selectLength = result.MatchLength;

        // Clamp to valid range.
        if (selectStart > text.Length) selectStart = charOffset;
        if (selectStart + selectLength > text.Length) selectLength = 0;

        // Defer the scroll+select to a Low-priority callback so the editor
        // control has completed its layout pass and knows its line positions.
        // Without this, ScrollLineToCenter is a no-op on freshly-loaded text.
        int targetLineIndex = Math.Max(0, result.LineNumber - 1);
        int capturedStart = selectStart;
        int capturedLength = Math.Max(0, selectLength);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (PreviewEditor.Visibility != Visibility.Visible) return;
                PreviewEditor.ScrollLineToCenter(targetLineIndex);
                if (capturedLength > 0 && capturedStart >= 0)
                {
                    int len = GetPreviewEditorTextLength();
                    int s = Math.Min(capturedStart, len);
                    int l = Math.Min(capturedLength, Math.Max(0, len - s));
                    SelectPreviewEditorText(s, l);
                    PreviewEditor.ScrollLineToCenter(targetLineIndex);
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview", $"ScrollEditorToMatch deferred scroll failed: {ex.GetType().Name}: {ex.Message}");
            }
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

            var textToSave = GetPreviewEditorText();
            if (TextHasUnencodableCharacters(textToSave, _previewEditorEncoding))
            {
                if (_previewEditorChunked && _previewEditorLoadedByteLength < _previewEditorTotalByteLength)
                {
                    ViewModel.StatusText = "Could not save: loaded edits contain characters that cannot be written with the original encoding.";
                    return false;
                }

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

            await SavePreviewEditorTextToDiskAsync(textToSave).ConfigureAwait(true);
            _previewEditorOriginalText = _previewEditorChunked ? null : textToSave;
            _previewEditorDirty = false;
            UpdatePreviewEditorButtons();

            if (!_previewEditorChunked || _previewEditorLoadedByteLength >= _previewEditorTotalByteLength)
            {
                // Re-validate search results for this file against the saved content.
                bool fileStillHasMatches = ViewModel.RevalidateFileResults(_previewEditorPath, GetPreviewEditorText());
                if (!fileStillHasMatches && _previewResult?.FilePath is not null &&
                    string.Equals(_previewResult.FilePath, _previewEditorPath, StringComparison.OrdinalIgnoreCase))
                {
                    _previewResult = null;
                }
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
            _previewEditorLoadedByteLength = encodedLoadedBytes;
            _previewEditorTotalByteLength = new FileInfo(_previewEditorPath).Length;
            UpdatePreviewEditorChunkUi();
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

        if (visible)
        {
            HideStickyFileHeader();
            HideActiveMatchOverlay();
            SectionNavOverlay.Visibility = Visibility.Collapsed;
            MatchNavPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateMatchNavPanel();
        }

        if (visible)
        {
            PreviewEditorPathText.Text = _previewEditorPath ?? string.Empty;
            ToolTipService.SetToolTip(PreviewEditorPathBar, _previewEditorPath ?? string.Empty);
            SyncPreviewEditorFindHighlights();
        }
        else
        {
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
        PreviewEditor.WordWrap = wrap;
    }

    private void UpdatePreviewEditorChunkUi()
    {
        if (!_previewEditorChunked || _previewEditorTotalByteLength <= 0)
        {
            PreviewEditorChunkPanel.Visibility = Visibility.Collapsed;
            return;
        }

        bool fullyLoaded = _previewEditorLoadedByteLength >= _previewEditorTotalByteLength;
        PreviewEditorChunkPanel.Visibility = Visibility.Visible;
        PreviewEditorChunkStatusText.Text = fullyLoaded
            ? $"Loaded {FormatBytes(_previewEditorTotalByteLength)}"
            : $"Loaded {FormatBytes(_previewEditorLoadedByteLength)} of {FormatBytes(_previewEditorTotalByteLength)}";
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
            return;
        }

        ShowPreviewMessage("No matches remain for this file.");
    }

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
        PreviewEditor.LoadText(text, autodetectTabsSpaces: false);
        PreviewEditor.ClearUndoRedoHistory();
    }

    private void SelectPreviewEditorText(int start, int length)
    {
        int textLength = GetPreviewEditorTextLength();
        int clampedStart = Math.Clamp(start, 0, textLength);
        int clampedLength = Math.Clamp(length, 0, Math.Max(0, textLength - clampedStart));
        PreviewEditor.SetSelection(clampedStart, clampedLength);
    }

    private void UpdatePreviewEditorButtons()
    {
        SavePreviewEditButton.IsEnabled = PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && _previewEditorPath is not null;
        UpdatePreviewEditorChunkUi();
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
