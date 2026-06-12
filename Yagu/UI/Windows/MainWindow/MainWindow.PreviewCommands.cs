using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
namespace Yagu;

/// <summary>
/// Preview commands, wrap/layout toggles, session/export actions, and multi-file preview orchestration.
/// </summary>
public sealed partial class MainWindow
{
    private async void OnShowFullFile(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Info("Preview", "OnShowFullFile: button clicked");
        var targets = GetFullFilePreviewTargets();
        if (targets.Count == 0)
        {
            LogService.Instance.Info("Preview", "OnShowFullFile: no targets found");
            ShowPreviewMessage("Select a file or match in the results list first.");
            ViewModel.StatusText = "Select a file or match before showing the full file.";
            return;
        }

        LogService.Instance.Info("Preview", $"OnShowFullFile: {targets.Count} target(s), files=[{string.Join(", ", targets.Select(t => System.IO.Path.GetFileName(t.FilePath)))}]");
        await ShowFullFilePreviewAsync(targets);
        LogService.Instance.Info("Preview", "OnShowFullFile: completed");
    }

    private void OnWrapModeOptionClicked(object sender, RoutedEventArgs e)
    {
        int mode = ReferenceEquals(sender, WrapModeWrap) || ReferenceEquals(sender, EditorWrapModeWrap)
            ? (int)Models.PreviewWrapMode.Wrap
            : (int)Models.PreviewWrapMode.NoWrap;

        int previousMode = ViewModel.PreviewWrapModeIndex;
        ViewModel.PreviewWrapModeIndex = mode;
        ViewModel.PreviewWordWrap = mode == 0;
        SyncWrapModeToggles(mode);

        // Switching to/from NoWrap requires a full preview rebuild because the
        // paragraph segmentation (4096-char chunks) is baked into the content.
        bool needsRebuild = previousMode == (int)Models.PreviewWrapMode.NoWrap
                         || mode == (int)Models.PreviewWrapMode.NoWrap;
        if (needsRebuild)
            RefreshCurrentPreview(preserveScroll: true);
        else
            _ = ApplyWrapModeAsync((Models.PreviewWrapMode)mode);
    }

    private void SyncWrapModeToggles(int mode)
    {
        bool wrap = NormalizePreviewWrapModeIndex(mode) == (int)Models.PreviewWrapMode.Wrap;
        WrapModeWrap.IsChecked = wrap;
        WrapModeNone.IsChecked = !wrap;
        EditorWrapModeWrap.IsChecked = wrap;
        EditorWrapModeNone.IsChecked = !wrap;
    }

    private static int NormalizePreviewWrapModeIndex(int mode)
        => mode == (int)Models.PreviewWrapMode.Wrap
            ? (int)Models.PreviewWrapMode.Wrap
            : (int)Models.PreviewWrapMode.NoWrap;

    private async void OnPreviewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
    }

    private async void OnLayoutOptionClicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, LayoutConcatenated))
            ViewModel.PreviewModeIndex = 0;
        else if (ReferenceEquals(sender, LayoutMultiHighlight))
            ViewModel.PreviewModeIndex = 1;

        SyncLayoutToggles(ViewModel.PreviewModeIndex);
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
    }

    private void SyncLayoutToggles(int index)
    {
        LayoutConcatenated.IsChecked = index == 0;
        LayoutMultiHighlight.IsChecked = index == 1;
    }

    private static void SetHorizontalPreviewScroll(ScrollViewer scrollViewer, bool enabled)
    {
        scrollViewer.HorizontalScrollMode = enabled ? ScrollMode.Enabled : ScrollMode.Disabled;
        scrollViewer.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
    }

    private static void ApplyPreviewHorizontalScrollForWrap(ScrollViewer scrollViewer, bool wrap)
        => SetHorizontalPreviewScroll(scrollViewer, enabled: !wrap);

    /// <summary>
    /// Section-scoped variant: keeps the native horizontal scrollbar hidden
    /// (it would be anchored at the bottom of huge section content, off-screen).
    /// The shared <c>StickyHorizontalScrollBar</c> overlay surfaces the horizontal
    /// extent within the viewport instead.
    /// </summary>
    private static void ApplyPreviewHorizontalScrollForWrapSection(ScrollViewer scrollViewer, bool wrap)
    {
        scrollViewer.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
        scrollViewer.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
    }

    // Synchronous variant retained for callers that already run inside an async preview
    // refresh (e.g. ShowSingleFilePreviewAsync). Only safe when the number of preview
    // sections is small or known. Prefer ApplyWrapModeAsync for user-initiated toggles.
    private void ApplyWordWrap(bool wrap)
    {
        var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

        InvalidatePendingMatchScrolls();
        UnboxCurrentMatch();

        if (PreviewBlock.TextWrapping != wrapping)
            PreviewBlock.TextWrapping = wrapping;
        ConfigurePreviewSelectionMode(PreviewBlock);
        ApplyPreviewEditorWordWrap(_previewEditorForcedWrap || wrap);
        foreach (var block in EnumeratePreviewSectionBlocks())
        {
            if (block.TextWrapping != wrapping)
                block.TextWrapping = wrapping;
            ConfigurePreviewSelectionMode(block);
        }
        if (PreviewSectionsPanel.Visibility != Visibility.Visible)
            ApplyPreviewHorizontalScrollForWrap(PreviewScrollViewer, wrap);
        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (expander.Content is Grid g && g.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv)
                ApplyPreviewHorizontalScrollForWrapSection(sv, wrap);
            else if (expander.Content is ScrollViewer sv2)
                ApplyPreviewHorizontalScrollForWrapSection(sv2, wrap);
        }
        UpdateStickyHorizontalScrollBar();
    }

    private bool _applyingWordWrap;

    private async Task ApplyWrapModeAsync(Models.PreviewWrapMode mode)
    {
        if (_applyingWordWrap) return;
        _applyingWordWrap = true;
        try
        {
            WordWrapDropDown.IsEnabled = false;
            EditorWordWrapDropDown.IsEnabled = false;
            bool wrap = mode == Models.PreviewWrapMode.Wrap;
            var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            LogService.Instance.Info("Preview", $"ApplyWrapModeAsync: start mode={mode}");

            InvalidatePendingMatchScrolls();
            UnboxCurrentMatch();

            // Cheap, always-visible controls first.
            if (PreviewBlock.TextWrapping != wrapping)
                PreviewBlock.TextWrapping = wrapping;
            ConfigurePreviewSelectionMode(PreviewBlock);
            ApplyPreviewEditorWordWrap(_previewEditorForcedWrap || wrap);
            if (PreviewSectionsPanel.Visibility != Visibility.Visible)
                ApplyPreviewHorizontalScrollForWrap(PreviewScrollViewer, wrap);

            // Snapshot the expanders so we don't touch the panel children mid-iteration if
            // anything reflows during a yield.
            var expanders = PreviewSectionsPanel.Children.OfType<Expander>().ToList();
            var totalSections = expanders.Count;
            if (totalSections > 0)
                ViewModel.StatusText = $"Applying word wrap to {totalSections} section(s)...";

            int processed = 0;
            foreach (var expander in expanders)
            {
                // Always toggle the per-section scrollbar (cheap, doesn't relayout the text).
                if (expander.Content is Grid g && g.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv)
                    ApplyPreviewHorizontalScrollForWrapSection(sv, wrap);
                else if (expander.Content is ScrollViewer sv2)
                    ApplyPreviewHorizontalScrollForWrapSection(sv2, wrap);

                // Only re-measure expanded sections; collapsed ones are not visible and
                // will pick up the current wrap state when re-expanded (see Expanding
                // handler in AddPreviewSection).
                if (expander.IsExpanded && expander.Tag is RichTextBlock block)
                {
                    if (block.TextWrapping != wrapping)
                        block.TextWrapping = wrapping;
                    ConfigurePreviewSelectionMode(block);

                    processed++;
                    // Yield to the UI thread between heavy sections so the toggle remains
                    // responsive and the window keeps painting.
                    if (processed % 2 == 0)
                    {
                        await Task.Yield();
                        await DispatchIdleAsync();
                    }
                }
            }

            ViewModel.StatusText = string.Empty;
            LogService.Instance.Info("Preview", $"ApplyWrapModeAsync: done mode={mode}, sections={totalSections}, processedExpanded={processed}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", $"ApplyWrapModeAsync failed: mode={mode}", ex);
            ViewModel.StatusText = "Could not apply word wrap to the current preview.";
        }
        finally
        {
            WordWrapDropDown.IsEnabled = true;
            EditorWordWrapDropDown.IsEnabled = true;
            _applyingWordWrap = false;
        }
    }

    private Task<object?> DispatchIdleAsync()
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult(null)))
            tcs.TrySetResult(null);
        return tcs.Task;
    }

    private async void RefreshCurrentPreview(bool preserveScroll = false)
    {
        LogService.Instance.Verbose("Preview", $"RefreshCurrentPreview called preserveScroll={preserveScroll}");
        if (PreviewEditor.Visibility == Visibility.Visible) return;

        double restoreHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
        double restoreVerticalOffset = PreviewScrollViewer.VerticalOffset;
        int restoreMatchIndex = preserveScroll ? _currentMatchIndex : -1;
        bool previousSuppressInitialMatchAutoScroll = _suppressInitialMatchAutoScroll;
        if (preserveScroll)
            _suppressInitialMatchAutoScroll = true;

        try
        {
            var selected = ViewModel.GetAllSelectedResults();
            if (selected.Count >= 2)
            {
                await UpdateMultiSelectPreviewAsync();
                return;
            }

            if (_previewResult is null) return;
            ViewModel.HydrateResult(_previewResult);
            await ShowSingleFilePreviewAsync(_previewResult, fullFile: false);
        }
        finally
        {
            if (preserveScroll)
            {
                _suppressInitialMatchAutoScroll = previousSuppressInitialMatchAutoScroll;
                RestorePreviewScrollOffset(restoreHorizontalOffset, restoreVerticalOffset);
                RestoreActiveMatchAfterPreviewRefresh(restoreMatchIndex);
            }
        }
    }

    private void RestorePreviewScrollOffset(double horizontalOffset, double verticalOffset)
    {
        if (double.IsNaN(horizontalOffset) || double.IsInfinity(horizontalOffset)
            || double.IsNaN(verticalOffset) || double.IsInfinity(verticalOffset))
        {
            return;
        }

        void ApplyRestore()
        {
            if (PreviewScrollViewer.Visibility != Visibility.Visible)
                return;

            double targetX = Math.Clamp(horizontalOffset, 0, Math.Max(0, PreviewScrollViewer.ScrollableWidth));
            double targetY = Math.Clamp(verticalOffset, 0, Math.Max(0, PreviewScrollViewer.ScrollableHeight));
            PreviewScrollViewer.ChangeView(targetX, targetY, null, disableAnimation: true);
            UpdateStickyFileHeader();
            QueueActiveMatchOverlayRefresh();
        }

        ApplyRestore();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyRestore);
    }

    private void RestoreActiveMatchAfterPreviewRefresh(int matchIndex)
    {
        if (matchIndex < 0 || _matchParagraphs.Count == 0)
            return;

        _currentMatchIndex = Math.Clamp(matchIndex, 0, _matchParagraphs.Count - 1);
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];

        // Guard against stale entries: if the paragraph has been removed from
        // its parent block (e.g. by a preview rebuild that did not refresh
        // _matchParagraphs), touching it crashes Microsoft.UI.Xaml.dll with
        // COMException E_FAIL inside GetCharacterRect.
        if (block is null || para is null || !block.Blocks.Contains(para))
            return;

        try
        {
            BoxMatchRun(para, matchInPara);
            MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
            QueueActiveMatchOverlayRefresh();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            LogService.Instance.Warning("Preview", "RestoreActiveMatchAfterPreviewRefresh: skipping due to stale paragraph", ex);
        }
    }

    private async Task<bool> ClearPreviewPanelForNewSearchAsync()
    {
        if (PreviewEditor.Visibility == Visibility.Visible && HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync())
            return false;

        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        FullFileButton.IsEnabled = true;
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        CompletePreviewContentUpdate();

        // Reset select-all checkbox for the new search.
        SelectAllFilesCheckBox.IsChecked = false;

        // Collapse the preview panel so only the file list shows during search
        CollapsePreviewPanel();

        return true;
    }

    private void OnOpenInDefaultApp(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_previewResult.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to open in default app: {_previewResult.FilePath}", ex); }
    }

    private async void OnOpenInEditor(object sender, RoutedEventArgs e)
    {
        var target = ResolveCurrentPreviewEditorTarget();
        if (target is null)
        {
            ViewModel.StatusText = "Select or preview a file before opening the editor.";
            LogService.Instance.Warning("Preview", "OnOpenInEditor: no preview result or selected result available");
            return;
        }

        await ShowFullFileEditorAsync(target, scrollToMatch: false);
    }

    private async void OnExpandAllSections(object sender, RoutedEventArgs e)
    {
        ExpandAllSectionsButton.IsEnabled = false;
        try
        {
            await MaterializeAllLazySectionsAsync();
        }
        finally
        {
            ExpandAllSectionsButton.IsEnabled = true;
            UpdateExpandAllButtonVisibility();
        }
    }

    private void UpdateExpandAllButtonVisibility()
    {
        ExpandAllSectionsButton.Visibility = _lazySections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnClearPreview(object sender, RoutedEventArgs e)
    {
        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        PreviewSectionsPanel.Children.Clear();
        FullFileButton.IsEnabled = true;
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        HideMatchNavPanel();
        CompletePreviewContentUpdate();

        // Building a large preview leaves a lot of long-lived allocations on
        // Gen2 (Paragraph/Run/Inline trees) and the LOH (string[] file-content
        // buffers). Plain Clear() drops references but the GC won't release
        // segments back to the OS without a forced collection, so the user
        // sees process memory stay high. Run a compacting Gen2 GC + LOH
        // compaction here — this is rare and user-initiated, so the cost is
        // acceptable in exchange for visibly reclaiming memory.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private async void OnClearResults(object sender, RoutedEventArgs e)
    {
        ResultsOptionsFlyout.Hide();

        // Clear preview pane
        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        PreviewSectionsPanel.Children.Clear();
        FullFileButton.IsEnabled = true;
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        HideMatchNavPanel();
        CompletePreviewContentUpdate();

        // Clear results, dispose temp store, and GC
        await ViewModel.ClearResultsAsync();
    }

    private async void OnSaveSession(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Yagu Session", new List<string> { Services.SessionFileService.FileExtension });
        picker.SuggestedFileName = BuildSessionFileSuggestedName(ViewModel.Query);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            int count = await ViewModel.SaveSessionAsync(file.Path);
            ViewModel.StatusText = $"Saved session ({count:N0} matches) to {file.Path}";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", $"Save session failed: {file.Path}", ex);
            ViewModel.ErrorText = $"Save session failed: {ex.Message}";
        }
    }

    private async void OnLoadSession(object sender, RoutedEventArgs e)
    {
        string? path = await ChooseSessionFileToLoadAsync();
        if (path is null) return;

        await LoadSessionFileAsync(path);
    }

    private async Task<string?> ChooseSessionFileToLoadAsync()
    {
        SessionFileDiscoveryResult discovery;
        try
        {
            ViewModel.StatusText = "Finding saved Yagu sessions...";
            using var discoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            discovery = await new SessionFileDiscoveryService().FindSessionFilesAsync(discoveryCts.Token);
        }
        catch (OperationCanceledException ex)
        {
            LogService.Instance.Warning("MainWindow", "Fast session discovery timed out; falling back to Windows picker.", ex);
            return await PickSessionFileWithWindowsDialogAsync();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Fast session discovery failed; falling back to Windows picker.", ex);
            return await PickSessionFileWithWindowsDialogAsync();
        }

        if (!discovery.FastSearchAvailable)
        {
            LogService.Instance.Info("MainWindow", $"Fast session discovery unavailable: {discovery.Error ?? "unknown reason"}");
            return await PickSessionFileWithWindowsDialogAsync();
        }

        LogService.Instance.Info("MainWindow", $"Fast session discovery found {discovery.Files.Count:N0} session file(s) via {discovery.Backend}.");
        var hwnd = GetMainWindowHandle();
        var result = await SessionLoadDialog.ShowAsync(hwnd, discovery.Files, RootGrid.ActualTheme);
        return result.Action switch
        {
            SessionLoadDialogAction.Load when !string.IsNullOrWhiteSpace(result.Path) => result.Path,
            SessionLoadDialogAction.Browse => await PickSessionFileWithWindowsDialogAsync(),
            _ => null,
        };
    }

    private async Task<string?> PickSessionFileWithWindowsDialogAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(Services.SessionFileService.FileExtension);
        picker.FileTypeFilter.Add("*");

        var hwnd = GetMainWindowHandle();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task LoadSessionFileAsync(string path)
    {
        ClearPreviewStateForSessionLoad();

        try
        {
            var header = await ViewModel.LoadSessionAsync(path);
            LogService.Instance.Info("MainWindow",
                $"Loaded session: {header.ResultCount:N0} declared results, query='{header.Query}', root='{header.SearchRoot}'");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", $"Load session failed: {path}", ex);
            ViewModel.ErrorText = $"Load session failed: {ex.Message}";
        }
    }

    private void ClearPreviewStateForSessionLoad()
    {
        // Clear preview pane state — the previously-selected result no longer applies.
        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
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

    private IntPtr GetMainWindowHandle()
    {
        if (_hwnd != IntPtr.Zero)
            return _hwnd;

        return WinRT.Interop.WindowNative.GetWindowHandle(this);
    }

    private static string BuildSessionFileSuggestedName(string query)
    {
        var safeQuery = string.IsNullOrWhiteSpace(query)
            ? "yagu-session"
            : new string(query.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safeQuery.Length > 40) safeQuery = safeQuery[..40];
        return $"{safeQuery}-{DateTime.Now:yyyyMMdd-HHmmss}";
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_previewResult.FilePath}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to show in Explorer: {_previewResult.FilePath}", ex); }
    }

    private void OnShowFileGroupInExplorer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to show in Explorer: {path}", ex); }
    }

    // ── Group mode menu handlers ──────────────────────────────────

    private void OnGroupModeNone(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.None; }
    private void OnGroupFolderAZ(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Folder; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupFolderZA(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Folder; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupDateModifiedRecent(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModified; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupDateModifiedOlder(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModified; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupDateCreatedRecent(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeCreated; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupDateCreatedOlder(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeCreated; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupDateModCreatedRecent(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModifiedCreated; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupDateModCreatedOlder(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModifiedCreated; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupExtensionAZ(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Extension; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupExtensionZA(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Extension; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupFileSizeSmallLarge(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.FileSize; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupFileSizeLargeSmall(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.FileSize; ViewModel.GroupSortDirectionIndex = 1; }

    private void OnResultGroupHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultGroupHeaderRow header })
            ViewModel.ToggleResultGroupExpansion(header);
    }

    // ── Sort menu handlers ────────────────────────────────────────

    private void OnSortFlyoutOpening(object sender, object e)
    {
        RefreshSortDirectionButtons();
    }

    private void RefreshSortDirectionButtons()
    {
        UpdateSortDirectionButtons(SortMatchesAscButton, SortMatchesDescButton, 1);
        UpdateSortDirectionButtons(SortDateModifiedAscButton, SortDateModifiedDescButton, 2);
        UpdateSortDirectionButtons(SortFileSizeAscButton, SortFileSizeDescButton, 3);
        UpdateSortDirectionButtons(SortFileNameAscButton, SortFileNameDescButton, 4);
    }

    private void UpdateSortDirectionButtons(Button ascButton, Button descButton, int sortModeIndex)
    {
        int? direction = ViewModel.GetSortDirectionIndex(sortModeIndex);
        ApplySortArrowState(ascButton, selected: direction == 1);
        ApplySortArrowState(descButton, selected: direction == 0);
    }

    private static void ApplySortArrowState(Button button, bool selected)
    {
        var brush = new SolidColorBrush(selected
            ? Microsoft.UI.Colors.White
            : Microsoft.UI.ColorHelper.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        button.Foreground = brush;
        if (button.Content is FontIcon icon)
            icon.Foreground = brush;
        button.Opacity = selected ? 1.0 : 0.72;
    }

    private void ToggleSortDirection(int sortModeIndex, int sortDirectionIndex)
    {
        if (ViewModel.GetSortDirectionIndex(sortModeIndex) == sortDirectionIndex)
            ViewModel.RemoveSortSelection(sortModeIndex);
        else
            ViewModel.ApplySortSelection(sortModeIndex, sortDirectionIndex);

        RefreshSortDirectionButtons();
    }

    private void OnSortNone(object sender, RoutedEventArgs e) { ViewModel.ApplySortSelection(0, 0); RefreshSortDirectionButtons(); SortFlyout.Hide(); }
    private void OnSortMatchesDesc(object sender, RoutedEventArgs e) => ToggleSortDirection(1, 0);
    private void OnSortMatchesAsc(object sender, RoutedEventArgs e) => ToggleSortDirection(1, 1);
    private void OnSortDateModifiedDesc(object sender, RoutedEventArgs e) => ToggleSortDirection(2, 0);
    private void OnSortDateModifiedAsc(object sender, RoutedEventArgs e) => ToggleSortDirection(2, 1);
    private void OnSortFileSizeDesc(object sender, RoutedEventArgs e) => ToggleSortDirection(3, 0);
    private void OnSortFileSizeAsc(object sender, RoutedEventArgs e) => ToggleSortDirection(3, 1);
    private void OnSortFileNameDesc(object sender, RoutedEventArgs e) => ToggleSortDirection(4, 0);
    private void OnSortFileNameAsc(object sender, RoutedEventArgs e) => ToggleSortDirection(4, 1);

    // ── Date filter menu handlers ─────────────────────────────────

    private void OnDateFilterNone(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.None;
    private void OnDateFilterPastDay(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastDay;
    private void OnDateFilterPastWeek(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastWeek;
    private void OnDateFilterPastTwoWeeks(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastTwoWeeks;
    private void OnDateFilterPastMonth(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastMonth;
    private void OnDateFilterPastThreeMonths(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastThreeMonths;
    private void OnDateFilterPastSixMonths(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastSixMonths;
    private void OnDateFilterPastNineMonths(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastNineMonths;
    private void OnDateFilterPastYear(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastYear;
    private void OnDateFilterPastTwoYears(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastTwoYears;
    private void OnDateFilterPastThreeYears(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastThreeYears;
    private void OnDateFilterPastFiveYears(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastFiveYears;

    private void OnFilterFlyoutOpening(object sender, object e) => RebuildExtensionFilterSubMenu();

    private void RebuildExtensionFilterSubMenu()
    {
        ExtensionFilterSubMenu.Items.Clear();
        var options = ViewModel.GetExtensionFilterOptions();
        if (options.Count == 0)
        {
            ExtensionFilterSubMenu.Items.Add(new MenuFlyoutItem
            {
                Text = "No extensions available",
                IsEnabled = false,
            });
            return;
        }

        var clearItem = new MenuFlyoutItem { Text = "All extensions" };
        clearItem.Click += OnClearExtensionFilterClicked;
        ExtensionFilterSubMenu.Items.Add(clearItem);
        ExtensionFilterSubMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var option in options)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{option.DisplayName} ({option.Count:N0})",
                IsChecked = option.IsSelected,
                Tag = option.Extension,
            };
            item.Click += OnExtensionFilterItemClicked;
            ExtensionFilterSubMenu.Items.Add(item);
        }
    }

    private void OnClearExtensionFilterClicked(object sender, RoutedEventArgs e) => ViewModel.ClearExtensionFilter();

    private void OnExtensionFilterItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || item.Tag is not string extension)
            return;

        var options = ViewModel.GetExtensionFilterOptions();
        var selectedExtensions = options
            .Where(option => option.IsSelected)
            .Select(option => option.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (item.IsChecked)
            selectedExtensions.Add(extension);
        else
            selectedExtensions.Remove(extension);

        if (selectedExtensions.Count == 0 || selectedExtensions.Count == options.Count)
            ViewModel.ClearExtensionFilter();
        else
            ViewModel.SetExtensionFilter(selectedExtensions);
    }

    private void OnMatchLineLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBlock rtb) return;
        RenderResultMatchLine(rtb);
    }

    private void RenderResultMatchLine(RichTextBlock rtb)
    {
        var dc = rtb.DataContext;
        if (dc is not SearchResult r) return;

        ApplyResultMatchTextStyle(rtb);
        rtb.Blocks.Clear();
        var para = new Paragraph();
        int matchStart = r.IsEvicted ? r.ShortPreviewMatchStart : r.MatchStartColumn;
        HighlightInline(para, r.MatchLine, matchStart, r.MatchLength);
        rtb.Blocks.Add(para);
    }

    private void ApplyResultMatchTextSettings()
    {
        _resultMatchTextBrush = new SolidColorBrush(ColorStringHelper.Parse(
            ViewModel.ResultListMatchHighlightColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00)));
    }

    private void ApplyResultMatchTextStyle(RichTextBlock rtb)
    {
        string family = string.IsNullOrWhiteSpace(ViewModel.ResultListMatchTextFontFamily)
            ? AppSettings.DefaultResultListMatchTextFontFamily
            : ViewModel.ResultListMatchTextFontFamily.Trim();

        int size = Math.Clamp(
            ViewModel.ResultListMatchTextFontSize <= 0 ? AppSettings.DefaultResultListMatchTextFontSize : ViewModel.ResultListMatchTextFontSize,
            6,
            72);

        rtb.FontFamily = new FontFamily(family);
        rtb.FontSize = size;
    }

    private void RefreshVisibleResultMatchLines()
    {
        if (ResultsList is null) return;
        RefreshVisibleResultMatchLines(ResultsList);
    }

    private void RefreshVisibleResultMatchLines(Microsoft.UI.Xaml.DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is RichTextBlock rtb && rtb.DataContext is SearchResult)
                RenderResultMatchLine(rtb);

            RefreshVisibleResultMatchLines(child);
        }
    }

    private void OnSelectAllFilesChecked(object sender, RoutedEventArgs e)
    {
        _suppressPreviewUpdate = true;
        try
        {
            foreach (var g in ViewModel.ResultGroups)
                g.SelectAll();
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private void OnSelectAllFilesUnchecked(object sender, RoutedEventArgs e)
    {
        _suppressPreviewUpdate = true;
        try
        {
            foreach (var g in ViewModel.ResultGroups)
                g.DeselectAll();
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private async void OnFileGroupCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not FileGroup group)
        {
            // DataContext not set (e.g. during container recycling) — revert checkbox to unchecked.
            if (sender is CheckBox orphan)
                SetFileGroupCheckBoxState(orphan, false);
            return;
        }

        bool shouldSelect = checkBox.IsChecked == true;
        int currentIndex = ViewModel.ResultGroups.IndexOf(group);
        var groupsToPreview = new List<FileGroup>();
        bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                       .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        LogService.Instance.Info("Preview",
            $"OnFileGroupCheckBoxClicked: file='{group.FilePath}', shouldSelect={shouldSelect}, matchCount={group.Count}, index={currentIndex}");

        _suppressPreviewUpdate = true;
        try
        {
            if (isShift && currentIndex >= 0)
            {
                int anchor = _lastCheckedGroupIndex >= 0 ? _lastCheckedGroupIndex : currentIndex;
                int lo = Math.Min(anchor, currentIndex);
                int hi = Math.Max(anchor, currentIndex);
                for (int i = lo; i <= hi; i++)
                {
                    if (shouldSelect)
                    {
                        ViewModel.ResultGroups[i].SelectAll();
                        groupsToPreview.Add(ViewModel.ResultGroups[i]);
                    }
                    else
                    {
                        ViewModel.ResultGroups[i].DeselectAll();
                    }
                }
            }
            else if (shouldSelect)
            {
                group.SelectAll();
                groupsToPreview.Add(group);
            }
            else
            {
                group.DeselectAll();
            }
        }
        finally
        {
            _suppressPreviewUpdate = false;
        }

        // Force-sync checkbox to model to prevent divergence from OneWay binding.
        SetFileGroupCheckBoxState(checkBox, group.AllSelected);

        _lastCheckedGroupIndex = currentIndex;

        if (groupsToPreview.Count > 0)
        {
            if (!ViewModel.FileHeaderCheckAddsToPreview) return;
            try
            {
                await EnsureFileGroupsInPreviewAsync(groupsToPreview, group.FilePath);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"OnFileGroupCheckBoxClicked: failed to add checked file(s) to preview: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else if (!shouldSelect)
        {
            // Remove preview sections for deselected file groups.
            IEnumerable<FileGroup> deselectedGroups;
            if (isShift && currentIndex >= 0)
            {
                int anchor = _lastCheckedGroupIndex >= 0 ? _lastCheckedGroupIndex : currentIndex;
                int lo = Math.Min(anchor, currentIndex);
                int hi = Math.Max(anchor, currentIndex);
                deselectedGroups = ViewModel.ResultGroups.Skip(lo).Take(hi - lo + 1);
            }
            else
            {
                deselectedGroups = new[] { group };
            }

            foreach (var g in deselectedGroups)
            {
                if (TryFindPreviewSection(g.FilePath, out _, out var block))
                    RemovePreviewSection(block, g.FilePath);
            }
        }
    }

    private async Task EnsureFileGroupsInPreviewAsync(IReadOnlyList<FileGroup> groups, string scrollToFile)
    {
        var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileGroup in groups)
        {
            if (PreviewSectionExists(fileGroup.FilePath))
                continue;

            var selectedResults = GetPreviewableResults(fileGroup);
            if (selectedResults.Count > 0)
                newFiles[fileGroup.FilePath] = selectedResults;
        }

        if (newFiles.Count > 0)
        {
            await PrependPreviewSectionsForFilesAsync(newFiles, scrollToFile);
        }
        else if (PreviewSectionExists(scrollToFile))
        {
            TryScrollToPreviewSection(scrollToFile);
        }
    }

    private void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressPreviewUpdate)
        {
            LogService.Instance.Info("Preview", $"OnSelectAllChecked: SUPPRESSED (group={(sender is FrameworkElement f && f.DataContext is FileGroup fg ? fg.FilePath : "?")}");
            return;
        }
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            // Skip spurious events from ListView virtualization/recycling:
            // the Checked event fires when a recycled container is re-bound,
            // but the model already has AllSelected == true.
            if (g.AllSelected)
            {
                LogService.Instance.Info("Preview", $"OnSelectAllChecked: SKIP (model already AllSelected) file='{g.FilePath}'");
                return;
            }

            int currentIndex = ViewModel.ResultGroups.IndexOf(g);
            LogService.Instance.Info("Preview", $"OnSelectAllChecked: file='{g.FilePath}', matchCount={g.Count}, index={currentIndex}");

            // Shift+Click: check all from previous anchor to clicked item
            bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                           .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isShift && currentIndex >= 0)
            {
                _suppressPreviewUpdate = true;
                try
                {
                    int anchor = _lastCheckedGroupIndex >= 0 ? _lastCheckedGroupIndex : currentIndex;
                    int lo = Math.Min(anchor, currentIndex);
                    int hi = Math.Max(anchor, currentIndex);
                    for (int i = lo; i <= hi; i++)
                        ViewModel.ResultGroups[i].SelectAll();
                }
                finally { _suppressPreviewUpdate = false; }
            }
            else
            {
                _suppressPreviewUpdate = true;
                try { g.SelectAll(); }
                finally { _suppressPreviewUpdate = false; }
            }

            _lastCheckedGroupIndex = currentIndex;
        }
    }

    private void OnSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressPreviewUpdate)
        {
            LogService.Instance.Info("Preview", $"OnSelectAllUnchecked: SUPPRESSED (group={(sender is FrameworkElement f && f.DataContext is FileGroup fg ? fg.FilePath : "?")}");
            return;
        }
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            // Skip spurious events from ListView virtualization/recycling:
            // when a selected item scrolls off-screen, the container is recycled
            // and CheckBox.Unchecked fires even though the model is still selected.
            if (!g.AllSelected)
            {
                LogService.Instance.Info("Preview", $"OnSelectAllUnchecked: SKIP (model already deselected) file='{g.FilePath}'");
                return;
            }

            int currentIndex = ViewModel.ResultGroups.IndexOf(g);
            LogService.Instance.Info("Preview", $"OnSelectAllUnchecked: file='{g.FilePath}', index={currentIndex}");

            // Shift+Click: uncheck all from previous anchor to clicked item
            bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                           .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isShift && currentIndex >= 0)
            {
                _suppressPreviewUpdate = true;
                try
                {
                    int anchor = _lastCheckedGroupIndex >= 0 ? _lastCheckedGroupIndex : currentIndex;
                    int lo = Math.Min(anchor, currentIndex);
                    int hi = Math.Max(anchor, currentIndex);
                    for (int i = lo; i <= hi; i++)
                        ViewModel.ResultGroups[i].DeselectAll();
                }
                finally { _suppressPreviewUpdate = false; }
            }
            else
            {
                _suppressPreviewUpdate = true;
                try { g.DeselectAll(); }
                finally { _suppressPreviewUpdate = false; }
            }

            _lastCheckedGroupIndex = currentIndex;
        }
    }

    private void OnResultsListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.A &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _suppressPreviewUpdate = true;
            try
            {
                foreach (var g in ViewModel.ResultGroups)
                    g.SelectAll();
            }
            finally { _suppressPreviewUpdate = false; }
            e.Handled = true;
        }
    }

    private async void OnShowMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileGroup g })
        {
            double? restoreVerticalOffset = CaptureResultsListVerticalOffset();
            _resultsListShowMoreRestoreInProgress = restoreVerticalOffset.HasValue;

            if (sender is Control control)
                control.IsEnabled = false;

            try
            {
                int shown = await ShowMoreVisibleResultsIncrementalAsync(g, FileGroup.PageSize, restoreVerticalOffset).ConfigureAwait(true);
                if (shown > 0)
                {
                    QueueRestoreResultsListVerticalOffsetAfterShowMore(restoreVerticalOffset, g.FilePath);
                }
                else
                {
                    _resultsListShowMoreRestoreInProgress = false;
                    CaptureResultsListScrollPosition();
                }
            }
            catch (Exception ex)
            {
                _resultsListShowMoreRestoreInProgress = false;
                LogService.Instance.Warning("Results",
                    $"OnShowMoreClicked failed for '{g.FilePath}': {ex.GetType().Name}: {ex.Message}");
                ViewModel.StatusText = "Could not show more matches for this file.";
            }
            finally
            {
                if (sender is Control controlToRestore)
                    controlToRestore.IsEnabled = g.HasMore;
            }
        }
    }

    private void OnShowMoreTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private async Task<int> ShowMoreVisibleResultsIncrementalAsync(FileGroup group, int requestedCount, double? restoreVerticalOffset = null)
    {
        LogService.Instance.Info("FileGroup",
            $"ShowMoreIncremental START: file='{System.IO.Path.GetFileName(group.FilePath)}', " +
            $"requested={requestedCount}, Count={group.Count}, VisibleCount={group.VisibleResults.Count}, " +
            $"HasMore={group.HasMore}, RemainingCount={group.RemainingCount}");

        if (requestedCount <= 0 || !group.HasMore)
        {
            LogService.Instance.Info("FileGroup",
                $"ShowMoreIncremental EARLY EXIT: requested={requestedCount}, HasMore={group.HasMore}");
            return 0;
        }

        int remainingToShow = Math.Min(requestedCount, group.RemainingCount);
        int totalShown = 0;
        var sw = Stopwatch.StartNew();

        while (remainingToShow > 0 && group.HasMore)
        {
            int chunkSize = Math.Min(VisibleResultShowMoreBatchSize, remainingToShow);
            int start = group.VisibleResults.Count;
            int end = Math.Min(group.Count, start + chunkSize);
            if (end <= start)
                break;

            await HydrateRangeAsync(group, start, end).ConfigureAwait(true);
            int shown = group.ShowMore(end - start);
            if (shown <= 0)
                break;

            if (restoreVerticalOffset is double pinnedOffset)
                ApplyResultsListVerticalOffsetAfterShowMore(pinnedOffset, group.FilePath, log: false);

            totalShown += shown;
            remainingToShow -= shown;
            if (remainingToShow > 0 && group.HasMore)
                await Task.Yield();
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds >= 250)
        {
            LogService.Instance.Info("Results",
                $"ShowMoreVisibleResultsIncrementalAsync: file='{System.IO.Path.GetFileName(group.FilePath)}', requested={requestedCount:N0}, elapsed={sw.ElapsedMilliseconds}ms");
        }

        return totalShown;
    }

    private void ShowMoreVisibleResultsIncremental(FileGroup group, int requestedCount)
    {
        LogService.Instance.Info("FileGroup",
            $"ShowMoreIncremental SYNC START: file='{System.IO.Path.GetFileName(group.FilePath)}', " +
            $"requested={requestedCount}, Count={group.Count}, VisibleCount={group.VisibleResults.Count}, " +
            $"HasMore={group.HasMore}, RemainingCount={group.RemainingCount}");

        if (requestedCount <= 0 || !group.HasMore)
        {
            LogService.Instance.Info("FileGroup",
                $"ShowMoreIncremental SYNC EARLY EXIT: requested={requestedCount}, HasMore={group.HasMore}");
            return;
        }

        int remainingToShow = Math.Min(requestedCount, group.RemainingCount);
        var sw = Stopwatch.StartNew();

        while (remainingToShow > 0 && group.HasMore)
        {
            int chunkSize = Math.Min(VisibleResultShowMoreBatchSize, remainingToShow);
            int start = group.VisibleResults.Count;
            int end = Math.Min(group.Count, start + chunkSize);
            if (end <= start)
                break;

            HydrateRange(group, start, end);
            int shown = group.ShowMore(end - start);
            if (shown <= 0)
                break;

            remainingToShow -= shown;
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds >= 25)
        {
            LogService.Instance.Info("Results",
                $"ShowMoreVisibleResultsIncremental SYNC: file='{System.IO.Path.GetFileName(group.FilePath)}', requested={requestedCount:N0}, elapsed={sw.ElapsedMilliseconds}ms");
        }
    }

    private void OnCopyFileGroupPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && !string.IsNullOrWhiteSpace(path))
            SetClipboardText(path, "file group path");
    }

    private void OnFileHeaderContextMenuOpening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            var contextGroup = GetFileHeaderContextGroup(flyout)
                ?? flyout.Items.OfType<MenuFlyoutItem>()
                    .Select(GetFileHeaderContextGroup)
                    .FirstOrDefault(g => g is not null);

            // First item: "Preview <filename>"
            if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem singleItem)
            {
                string fileName = contextGroup is not null ? System.IO.Path.GetFileName(contextGroup.FilePath) : "";
                singleItem.Text = $"Preview {fileName}";
                singleItem.Tag = contextGroup;
            }

            // Second item: "Preview all selected (x)" — hidden when ≤1 checked
            int checkedCount = GetCheckedFileGroups().Count;
            if (flyout.Items.Count > 1 && flyout.Items[1] is MenuFlyoutItem previewAllItem)
            {
                previewAllItem.Text = $"Preview all selected ({checkedCount})";
                previewAllItem.Tag = contextGroup;
                previewAllItem.Visibility = checkedCount > 1
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
            }

            // Update copy/save items: plural only when >1 file checked
            int count = checkedCount > 0 ? checkedCount : 1; // at least the right-clicked file
            bool plural = count > 1;
            // Items layout: [0]=Preview, [1]=PreviewAll, [2]=Sep, [3]=CopyPath, [4]=Sep, [5]=CopyPaths, [6]=CopyWithContent, [7]=Sep, [8]=SavePaths, [9]=SaveWithContent
            foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
            {
                if (item.Text.StartsWith("Copy Selected File Path", StringComparison.Ordinal))
                    item.Text = plural ? "Copy Selected File Paths" : "Copy Selected File Path";
                else if (item.Text.StartsWith("Copy Selected File", StringComparison.Ordinal))
                    item.Text = plural ? "Copy Selected Files With Content" : "Copy Selected File With Content";
                else if (item.Text.StartsWith("Save Selected File Path", StringComparison.Ordinal))
                    item.Text = plural ? "Save Selected File Paths\u2026" : "Save Selected File Path\u2026";
                else if (item.Text.StartsWith("Save Selected File", StringComparison.Ordinal))
                    item.Text = plural ? "Save Selected Files With Content\u2026" : "Save Selected File With Content\u2026";
            }
        }
    }

    private void OnResultsContextMenuOpening(object sender, object e)
    {
        var checkedGroups = GetCheckedFileGroups();
        var contextGroup = checkedGroups.Count == 0 ? GetRecentResultsContextMenuGroup() : null;
        int checkedCount = checkedGroups.Count;

        // "Preview <filename>" — always visible, shows right-clicked file name
        string fileName = contextGroup is not null
            ? System.IO.Path.GetFileName(contextGroup.FilePath)
            : checkedCount == 1
                ? System.IO.Path.GetFileName(checkedGroups[0].FilePath)
                : "";
        CtxPreviewSingle.Text = $"Preview {fileName}";
        CtxPreviewSingle.Tag = contextGroup ?? (checkedCount == 1 ? checkedGroups[0] : null);
        CtxPreviewSingle.Visibility = !string.IsNullOrEmpty(fileName)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        // "Preview all selected (x)" — only when >1 checked
        CtxPreviewSelected.Text = $"Preview all selected ({checkedCount})";
        CtxPreviewSelected.Tag = contextGroup;
        CtxPreviewSelected.Visibility = checkedCount > 1
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        int count = checkedCount > 0 ? checkedCount : contextGroup is null ? 0 : 1;
        bool plural = count > 1;
        CtxCopyPaths.Text = plural ? "Copy File Paths" : "Copy File Path";
        CtxCopyWithContent.Text = plural ? "Copy Files With Content" : "Copy File With Content";
        CtxSavePaths.Text = plural ? "Save File Paths\u2026" : "Save File Path\u2026";
        CtxSaveWithContent.Text = plural ? "Save Files With Content\u2026" : "Save File With Content\u2026";
    }

    private void OnResultsListPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ResultsList);
        if (!point.Properties.IsRightButtonPressed)
            return;

        CaptureResultsContextMenuGroup(e.OriginalSource as DependencyObject);
    }

    private void OnResultsListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CaptureResultsContextMenuGroup(e.OriginalSource as DependencyObject);
    }

    private void CaptureResultsContextMenuGroup(DependencyObject? source)
    {
        _lastResultsContextMenuGroup = FindContextFileGroup(source);
        _lastResultsContextMenuTick = Environment.TickCount64;
    }

    private FileGroup? GetRecentResultsContextMenuGroup()
    {
        long elapsed = Environment.TickCount64 - _lastResultsContextMenuTick;
        return elapsed is >= 0 and < 2000 ? _lastResultsContextMenuGroup : null;
    }

    private async void OnPreviewSingleFile(object sender, RoutedEventArgs e)
    {
        FileGroup? group = null;
        if (sender is FrameworkElement fe && fe.Tag is FileGroup tagGroup)
            group = tagGroup;
        else if (sender is FrameworkElement fe2 && fe2.Tag is string filePath)
            group = FindFileGroup(filePath);

        if (group is null)
        {
            group = GetRecentResultsContextMenuGroup();
        }

        if (group is null) return;

        LogService.Instance.Info("Preview", $"OnPreviewSingleFile: {System.IO.Path.GetFileName(group.FilePath)}");
        _suppressPreviewUpdate = true;
        try
        {
            group.SelectAll();
            var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in GetPreviewableResults(group))
            {
                if (!byFile.TryGetValue(r.FilePath, out var list))
                {
                    list = new List<SearchResult>();
                    byFile[r.FilePath] = list;
                }
                list.Add(r);
            }
            if (byFile.Count == 0) return;

            var existing = GetExistingPreviewFilePaths();
            if (existing.Contains(group.FilePath))
            {
                TryScrollToPreviewSection(group.FilePath);
            }
            else
            {
                await PrependPreviewSectionsForFilesAsync(byFile, byFile.Keys.First());
            }
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private async void OnPreviewSelectedFiles(object sender, RoutedEventArgs e)
    {
        var selectedGroups = GetPreviewFileGroups(sender);
        var groupNames = selectedGroups.Select(g => g.FilePath).ToList();
        LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: {groupNames.Count} groups selected: [{string.Join(", ", groupNames.Select(System.IO.Path.GetFileName))}]");
        _suppressPreviewUpdate = true;
        try
        {
            // Select all match results within each checked FileGroup.
            foreach (var g in selectedGroups)
                g.SelectAll();

            // Gather results only from the checked groups.
            var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in selectedGroups)
            {
                foreach (var r in GetPreviewableResults(g))
                {
                    if (!byFile.TryGetValue(r.FilePath, out var list))
                    {
                        list = new List<SearchResult>();
                        byFile[r.FilePath] = list;
                    }
                    list.Add(r);
                }
            }
            if (byFile.Count == 0)
            {
                LogService.Instance.Info("Preview", "OnPreviewSelectedFiles: no selected results, returning");
                return;
            }

            // Determine which files are new vs already present on the right panel.
            var existing = GetExistingPreviewFilePaths();
            LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: byFile={byFile.Count}, existingOnPanel={existing.Count}");
            var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
            string? firstExistingFile = null;
            foreach (var (filePath, results) in byFile)
            {
                if (existing.Contains(filePath))
                    firstExistingFile ??= filePath;
                else
                    newFiles[filePath] = results;
            }

            bool isSingleFile = byFile.Count == 1;

            LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: newFiles={newFiles.Count}, isSingleFile={isSingleFile}, firstExistingFile='{firstExistingFile}'");
            if (newFiles.Count > 0)
            {
                // For single-file: scroll to the new file after prepending.
                // For multi-file: no scroll.
                string? scrollTo = isSingleFile ? newFiles.Keys.First() : null;
                await PrependPreviewSectionsForFilesAsync(newFiles, scrollTo);
            }
            else if (isSingleFile && firstExistingFile is not null)
            {
                // Single file already present — just scroll to it.
                LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: single file already on panel, scrolling to '{firstExistingFile}'");
                TryScrollToPreviewSection(firstExistingFile);
            }
            // Multi-file with all already present — nothing to do.
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private List<FileGroup> GetPreviewFileGroups(object sender)
    {
        var checkedGroups = GetCheckedFileGroups();
        if (checkedGroups.Count > 0)
            return checkedGroups;

        var contextGroup = GetFileHeaderContextGroup(sender);
        return contextGroup is null ? checkedGroups : [contextGroup];
    }

    private static List<SearchResult> GetPreviewableResults(IEnumerable<SearchResult> results)
    {
        var previewable = results.ToList();
        if (previewable.Any(result => result.LineNumber > 0))
            previewable.RemoveAll(result => result.LineNumber <= 0);
        return previewable;
    }

    private static List<SearchResult> GetPreviewableResults(FileGroup group)
    {
        int limit = GetPreviewResultSnapshotLimit();
        List<SearchResult> candidates = group.AllSelected
            ? group.GetPreviewSnapshot(limit)
            : limit == int.MaxValue
                ? group.Where(result => result.IsSelected).ToList()
                : group.Where(result => result.IsSelected).Take(limit).ToList();

        return GetPreviewableResults(candidates);
    }

    private static int GetPreviewResultSnapshotLimit() => int.MaxValue;

    private FileGroup? GetFileHeaderContextGroup(object? sender)
    {
        if (sender is MenuFlyout { Target: FrameworkElement target })
        {
            if (target.DataContext is FileGroup targetGroup)
                return targetGroup;

            var taggedTargetGroup = GetFileHeaderContextGroup(target);
            if (taggedTargetGroup is not null)
                return taggedTargetGroup;
        }

        if (sender is FrameworkElement element)
        {
            if (element.Tag is FileGroup taggedGroup)
                return taggedGroup;

            if (element.Tag is string filePath)
                return FindFileGroup(filePath);

            if (element.DataContext is FileGroup dataContextGroup)
                return dataContextGroup;
        }

        return null;
    }

    private FileGroup? FindContextFileGroup(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement element)
                continue;

            if (element.Tag is FileGroup taggedGroup)
                return taggedGroup;

            if (element.Tag is string filePath)
            {
                var taggedPathGroup = FindFileGroup(filePath);
                if (taggedPathGroup is not null)
                    return taggedPathGroup;
            }

            if (element.DataContext is FileGroup dataContextGroup)
                return dataContextGroup;

            if (element.DataContext is SearchResult result)
            {
                var parentGroup = FindParentGroup(result);
                if (parentGroup is not null)
                    return parentGroup;
            }
        }

        return null;
    }

    private FileGroup? FindFileGroup(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        return ViewModel.ResultGroups.FirstOrDefault(g =>
            string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    private void OnCopySelectedFilePaths(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;
        SetClipboardText(SelectedFileExportService.BuildPathListText(paths), "selected file paths");
    }

    private async void OnCopySelectedFilesWithContent(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;

        try
        {
            var text = await SelectedFileExportService.BuildFilesWithContentTextAsync(paths).ConfigureAwait(true);
            SetClipboardText(text, "selected files with content");
            ViewModel.StatusText = $"Copied {paths.Count:N0} selected file(s) with content.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Could not copy selected files with content", ex);
            ViewModel.StatusText = $"Could not copy selected files with content: {ex.Message}";
        }
    }

    private async void OnSaveSelectedFilePaths(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;

        try
        {
            var file = await PickTextExportFileAsync("Yagu_Selected_File_Paths").ConfigureAwait(true);
            if (file is null) return;

            await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 128 * 1024, leaveOpen: false);
            await SelectedFileExportService.WritePathListAsync(paths, writer).ConfigureAwait(true);
            ViewModel.StatusText = $"Saved {paths.Count:N0} selected file path(s) to {file.Path}.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Could not save selected file paths", ex);
            ViewModel.StatusText = $"Could not save selected file paths: {ex.Message}";
        }
    }

    private async void OnSaveSelectedFilesWithContent(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;

        try
        {
            var file = await PickTextExportFileAsync("Yagu_Selected_Files_With_Content").ConfigureAwait(true);
            if (file is null) return;

            await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 128 * 1024, leaveOpen: false);
            await SelectedFileExportService.WriteFilesWithContentAsync(paths, writer).ConfigureAwait(true);
            ViewModel.StatusText = $"Saved {paths.Count:N0} selected file(s) with content to {file.Path}.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Could not save selected files with content", ex);
            ViewModel.StatusText = $"Could not save selected files with content: {ex.Message}";
        }
    }

    private List<string> GetSelectedFilePaths()
    {
        return GetCheckedFileGroups()
            .Where(g => !string.IsNullOrWhiteSpace(g.FilePath))
            .Select(g => g.FilePath)
            .ToList();
    }

    /// <summary>Returns all FileGroups whose header checkbox is checked (AllSelected).</summary>
    private List<FileGroup> GetCheckedFileGroups()
    {
        return ViewModel.ResultGroups.Where(g => g.AllSelected).ToList();
    }

    private async Task<Windows.Storage.StorageFile?> PickTextExportFileAsync(string suggestedFileName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text File", new List<string> { ".txt" });
        picker.SuggestedFileName = suggestedFileName;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        return await picker.PickSaveFileAsync();
    }

    private void OnCopyPreviewFilePath(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        SetClipboardText(_previewResult.FilePath, "preview file path");
    }

    private void SetPreviewFileLabel(string text, string? tooltip = null)
    {
        PreviewFileLabel.Text = text;
        ToolTipService.SetToolTip(PreviewFileLabel, string.IsNullOrWhiteSpace(tooltip) ? text : tooltip);
        if (!string.IsNullOrWhiteSpace(text)) EnsureWidthForPreview();
    }

    /// <summary>
    /// When a preview file is shown while the window is still at the narrow
    /// launcher width (~1400 dip), grow it horizontally to a normal width so
    /// the preview pane has room. Position stays anchored on the left.
    /// </summary>
    private void EnsureWidthForPreview()
    {
        try
        {
            if (AppWindow is null) return;
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;
            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            int desiredWidth = (int)(1200 * scale);
            int curWidth = AppWindow.Size.Width;
            if (curWidth >= desiredWidth) return;

            int curX = AppWindow.Position.X;
            int curY = AppWindow.Position.Y;
            int curHeight = AppWindow.Size.Height;
            int maxWidth = Math.Max(0, wa.X + wa.Width - curX);
            int newWidth = Math.Min(desiredWidth, maxWidth);
            if (newWidth <= curWidth) return;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(curX, curY, newWidth, curHeight));
        }
        catch { }
    }

    private FileGroup? FindParentGroup(SearchResult r)
    {
        foreach (var g in ViewModel.ResultGroups)
        {
            if (string.Equals(g.FilePath, r.FilePath, StringComparison.OrdinalIgnoreCase))
                return g;
        }
        return null;
    }

    private List<FullFilePreviewTarget> GetFullFilePreviewTargets()
    {
        var selectedMatches = ViewModel.GetAllSelectedResults();
        if (selectedMatches.Count > 0)
        {
            LogService.Instance.Info("Preview", $"GetFullFilePreviewTargets: using {selectedMatches.Count} selected matches");
            return BuildFullFilePreviewTargets(selectedMatches);
        }

        var selectedGroups = GetCheckedFileGroups()
            .Where(g => g.Count > 0)
            .ToList();
        if (selectedGroups.Count > 0)
        {
            LogService.Instance.Info("Preview", $"GetFullFilePreviewTargets: using {selectedGroups.Count} checked file groups");
            var targets = new List<FullFilePreviewTarget>(selectedGroups.Count);
            foreach (var group in selectedGroups)
                targets.Add(new FullFilePreviewTarget(group.FilePath, group.ToList()));
            return targets;
        }

        if (_previewResult is null)
        {
            LogService.Instance.Info("Preview", "GetFullFilePreviewTargets: no preview result, returning empty");
            return [];
        }

        var parent = FindParentGroup(_previewResult);
        var matches = parent is null ? new List<SearchResult> { _previewResult } : parent.ToList();
        LogService.Instance.Info("Preview", $"GetFullFilePreviewTargets: fallback to current preview file='{System.IO.Path.GetFileName(_previewResult.FilePath)}', matches={matches.Count}");
        return [new FullFilePreviewTarget(_previewResult.FilePath, matches)];
    }

    private static List<FullFilePreviewTarget> BuildFullFilePreviewTargets(IReadOnlyList<SearchResult> results)
    {
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!byFile.TryGetValue(result.FilePath, out var matches))
            {
                matches = new List<SearchResult>();
                byFile[result.FilePath] = matches;
            }
            matches.Add(result);
        }

        var targets = new List<FullFilePreviewTarget>(byFile.Count);
        foreach (var (filePath, matches) in byFile)
        {
            var previewableMatches = GetPreviewableResults(matches);
            if (previewableMatches.Count > 0)
                targets.Add(new FullFilePreviewTarget(filePath, previewableMatches));
        }
        return targets;
    }

    private void OnCopySelectedLines(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0 && sender is FrameworkElement fe && fe.DataContext is SearchResult single)
            selected = new List<SearchResult> { single };
        if (selected.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        foreach (var r in selected)
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{r.FilePath}:{r.LineNumber}: {r.MatchLine}");

        try
        {
            var pkg = new DataPackage();
            pkg.SetText(sb.ToString());
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { LogService.Instance.Verbose("MainWindow", "Clipboard unavailable for copy selected", ex); }
    }

    private static void SetClipboardText(string text, string description)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { LogService.Instance.Verbose("MainWindow", $"Clipboard unavailable for copy {description}", ex); }
    }

    private void OnCopySingleLine(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText($"{r.FilePath}:{r.LineNumber}: {r.MatchLine}");
                Clipboard.SetContent(pkg);
            }
            catch (Exception ex) { LogService.Instance.Verbose("MainWindow", "Clipboard unavailable for copy single", ex); }
        }
    }

    private async void OnExportSelectedLines(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0 && sender is FrameworkElement fe && fe.DataContext is SearchResult single)
            selected = new List<SearchResult> { single };
        if (selected.Count == 0) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text File", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });
        picker.SuggestedFileName = "Yagu_Export";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var sb = new System.Text.StringBuilder();
        string ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (ext == ".csv")
        {
            sb.AppendLine("\"File\",\"Line\",\"Match\"");
            foreach (var r in selected)
            {
                var escaped = r.MatchLine.Replace("\"", "\"\"");
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"\"{r.FilePath}\",{r.LineNumber},\"{escaped}\"");
            }
        }
        else
        {
            foreach (var r in selected)
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{r.FilePath}:{r.LineNumber}: {r.MatchLine}");
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
    }

    private async void OnExportHtmlReport(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0) return;
        var groups = selected
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HtmlReportExportService.FileMatchGroup(g.Key, Path.GetFileName(g.Key), g.ToList()))
            .ToList();
        if (groups.Count == 0) return;

        // Show export options dialog
        var exportOptions = await ReportExportDialog.ShowAsync(_hwnd, ViewModel.ContextLines);
        if (exportOptions is null) return;

        // Pick file extension based on format
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        switch (exportOptions.Format)
        {
            case Services.ReportFormat.Json:
                picker.FileTypeChoices.Add("JSON File", new List<string> { ".json" });
                picker.SuggestedFileName = "Yagu_Report";
                break;
            case Services.ReportFormat.Csv:
                picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });
                picker.SuggestedFileName = "Yagu_Report";
                break;
            default:
                picker.FileTypeChoices.Add("HTML File", new List<string> { ".html" });
                picker.SuggestedFileName = "Yagu_Report";
                break;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // Hydrate all results before writing
        foreach (var group in groups)
            foreach (var r in group.Results)
                ViewModel.HydrateResult(r);

        int totalMatches = groups.Sum(g => g.Results.Count);

        await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        using var w = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: false);

        var stats = new HtmlReportExportService.SearchStats(
            ViewModel.SearchStartedUtc,
            ViewModel.LastSearchElapsed,
            ViewModel.FilesScanned,
            ViewModel.BytesScanned);

        switch (exportOptions.Format)
        {
            case Services.ReportFormat.Json:
                await Services.ReportExportService.WriteJsonReportAsync(w, ViewModel.Query, groups, stats, exportOptions).ConfigureAwait(false);
                break;
            case Services.ReportFormat.Csv:
                await Services.ReportExportService.WriteCsvReportAsync(w, ViewModel.Query, groups, exportOptions).ConfigureAwait(false);
                break;
            default:
                await HtmlReportExportService.WriteMultiFileReportAsync(w, ViewModel.Query, groups, stats).ConfigureAwait(false);
                break;
        }

        var formatName = exportOptions.Format.ToString().ToUpperInvariant();
        DispatcherQueue.TryEnqueue(() =>
            ViewModel.StatusText = $"Exported {formatName} report ({groups.Count:N0} files, {totalMatches:N0} matches) to {file.Path}");
    }

    private static string BuildHighlightedMatchHtml(string line, int matchStart, int matchLength)
        => HtmlReportExportService.BuildHighlightedMatchHtml(line, matchStart, matchLength);

    private void AttachPreviewBlockContextFlyout(RichTextBlock block)
    {
        var flyout = new MenuFlyout();

        block.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) =>
            {
                var current = e.GetCurrentPoint(block);
                if (current.Properties.IsRightButtonPressed)
                    CapturePreviewBlockContextPoint(block, current.Position);
            }),
            handledEventsToo: true);

        var copyWithLines = new MenuFlyoutItem { Text = "Copy (with line numbers)", Icon = new SymbolIcon(Symbol.Copy) };
        copyWithLines.Click += (_, _) => CopyPreviewSelection(block, withLineNumbers: true);
        flyout.Items.Add(copyWithLines);

        var copyWithout = new MenuFlyoutItem { Text = "Copy (without line numbers)", Icon = new SymbolIcon(Symbol.Copy), KeyboardAcceleratorTextOverride = "Ctrl+C" };
        copyWithout.Click += (_, _) => CopyPreviewSelection(block, withLineNumbers: false);
        flyout.Items.Add(copyWithout);

        var editFileItem = new MenuFlyoutItem { Text = "Edit file", Icon = new SymbolIcon(Symbol.Edit) };
        editFileItem.Click += async (_, _) => await EditPreviewFileFromContextMenuAsync(block);
        flyout.Items.Add(editFileItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var wrapSubItem = new MenuFlyoutSubItem { Text = "Word wrap", Icon = new FontIcon { Glyph = "\uE8B3" } };
        var ctxWrap = new ToggleMenuFlyoutItem { Text = "Word wrap" };
        ctxWrap.IsChecked = ViewModel.PreviewWrapModeIndex == 0;
        ctxWrap.Click += (_, _) => { OnWrapModeOptionClicked(WrapModeWrap, new RoutedEventArgs()); };
        wrapSubItem.Items.Add(ctxWrap);
        var ctxNoWrap = new ToggleMenuFlyoutItem { Text = "No wrap" };
        ctxNoWrap.IsChecked = NormalizePreviewWrapModeIndex(ViewModel.PreviewWrapModeIndex) == (int)Models.PreviewWrapMode.NoWrap;
        ctxNoWrap.Click += (_, _) => { OnWrapModeOptionClicked(WrapModeNone, new RoutedEventArgs()); };
        wrapSubItem.Items.Add(ctxNoWrap);
        flyout.Items.Add(wrapSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var displaySettingsItem = new MenuFlyoutItem { Text = "Change preview fonts/colors...", Icon = new SymbolIcon(Symbol.Setting) };
        displaySettingsItem.Click += (_, _) => OpenSettingsTab(SettingsDisplayTabIndex);
        flyout.Items.Add(displaySettingsItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var exportFileItem = new MenuFlyoutItem { Text = "Export report (HTML/JSON/CSV)", Icon = new FontIcon { Glyph = "\uE9F9" } };
        exportFileItem.Click += async (_, _) =>
        {
            var filePath = block.Tag as string;
            if (string.IsNullOrEmpty(filePath)) return;
            await ExportSingleFileHtmlReportAsync(filePath);
        };
        flyout.Items.Add(exportFileItem);

        flyout.Opening += (_, _) =>
        {
            bool wrap = NormalizePreviewWrapModeIndex(ViewModel.PreviewWrapModeIndex) == (int)Models.PreviewWrapMode.Wrap;
            ctxWrap.IsChecked = wrap;
            ctxNoWrap.IsChecked = !wrap;
            bool hasSelection = HasPreviewCustomSelection(block) || !string.IsNullOrEmpty(block.SelectedText);
            copyWithLines.IsEnabled = hasSelection;
            copyWithout.IsEnabled = hasSelection;
            editFileItem.IsEnabled = !string.IsNullOrWhiteSpace(ResolvePreviewBlockFilePath(block));
        };
        block.ContextFlyout = flyout;
    }

    private void CapturePreviewBlockContextPoint(RichTextBlock block, Windows.Foundation.Point point)
    {
        _lastPreviewContextMenuBlock = block;
        _lastPreviewContextMenuPoint = point;
        _lastPreviewContextMenuFilePath = ResolvePreviewBlockFilePath(block);
        _lastPreviewContextMenuTick = Environment.TickCount64;
    }

    private bool TryGetPreviewBlockContextPoint(RichTextBlock block, string filePath, out Windows.Foundation.Point point)
    {
        point = _lastPreviewContextMenuPoint;
        if (!ReferenceEquals(_lastPreviewContextMenuBlock, block)) return false;
        if (Environment.TickCount64 - _lastPreviewContextMenuTick > PreviewContextMenuPointMaxAgeMs) return false;
        return string.Equals(_lastPreviewContextMenuFilePath, filePath, StringComparison.OrdinalIgnoreCase);
    }

    private void CopyPreviewSelection(RichTextBlock block, bool withLineNumbers)
    {
        if (TryBuildPreviewCustomSelectionText(block, withLineNumbers, out string customSelectedText))
        {
            var customDataPackage = new DataPackage();
            customDataPackage.SetText(customSelectedText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(customDataPackage);
            return;
        }

        string selectedText = block.SelectedText;
        if (string.IsNullOrEmpty(selectedText)) return;

        string textToCopy;
        // Detect whether the gutter (indicator + line number + separator) is rendered
        // inline in each paragraph (single-file PreviewBlock) or in a separate gutter
        // RichTextBlock (multi-section view). When inline, the first three Run inlines
        // of each paragraph are gutter chrome and must be stripped before re-prefixing.
        bool hasInlineGutter = !_sectionGutterBlocks.ContainsKey(block);
        LogService.Instance.Info("Preview",
            $"CopyPreviewSelection: withLineNumbers={withLineNumbers}, hasInlineGutter={hasInlineGutter}, " +
            $"isPreviewBlock={ReferenceEquals(block, PreviewBlock)}, wrapMode={ViewModel.PreviewWrapModeIndex}, " +
            $"selectedLen={selectedText.Length}, blockBlocks={block.Blocks.Count}");

        if (withLineNumbers)
        {
            var selStart = block.SelectionStart;
            var selEnd = block.SelectionEnd;
            if (selStart is null || selEnd is null)
            {
                textToCopy = selectedText;
            }
            else
            {
                int startOff = selStart.Offset;
                int endOff = selEnd.Offset;
                var sb = new StringBuilder();
                bool first = true;
                int lastEmittedLineNum = -1;
                int paraCount = 0;
                int missingLineNumCount = 0;
                int continuationCount = 0;
                foreach (var b in block.Blocks)
                {
                    if (b is not Paragraph para) continue;
                    var pStart = para.ContentStart;
                    var pEnd = para.ContentEnd;
                    if (pStart is null || pEnd is null) continue;
                    if (pEnd.Offset <= startOff) continue;
                    if (pStart.Offset >= endOff) break;

                    bool cwtHit = s_paragraphLineNumbers.TryGetValue(para, out var rawTag);
                    bool contTag = s_paragraphIsContinuation.TryGetValue(para, out _);

                    // In the multi-section (separate-gutter) view, structural paragraphs
                    // (──── Line N ──── separators, AddGapIndicator content spacers,
                    // truncation notices, etc.) are appended to the content block directly
                    // and never tagged in either CWT. Skip them so the copied text only
                    // contains real source lines.
                    if (!hasInlineGutter && !cwtHit && !contTag)
                        continue;

                    string lineText = ExtractParagraphContent(para, hasInlineGutter);
                    int lineNum = ResolveParagraphLineNumber(para, hasInlineGutter);
                    bool isContinuation = contTag
                                          || (lineNum > 0 && lineNum == lastEmittedLineNum);
                    paraCount++;
                    if (lineNum <= 0) missingLineNumCount++;
                    if (isContinuation) continuationCount++;
                    if (paraCount <= 5)
                    {
                        LogService.Instance.Info("Preview",
                            $"  para[{paraCount - 1}]: lineNum={lineNum}, cwtHit={cwtHit}, " +
                            $"cwtTagType={rawTag?.GetType().Name ?? "null"}, contTag={contTag}, " +
                            $"isContinuation={isContinuation}, inlinesCount={para.Inlines.Count}");
                    }

                    if (!first) sb.AppendLine();
                    first = false;

                    if (lineNum > 0 && !isContinuation)
                    {
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{lineNum,5} \u2502 {lineText}");
                        lastEmittedLineNum = lineNum;
                    }
                    else
                    {
                        // Blank gutter for continuation segments or unknown line numbers,
                        // matching the visual style ("      \u2502 ").
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"      \u2502 {lineText}");
                    }
                }
                LogService.Instance.Info("Preview",
                    $"CopyPreviewSelection: emitted {paraCount} paragraphs, " +
                    $"{missingLineNumCount} missing lineNum, {continuationCount} continuations.");
                textToCopy = sb.ToString();
            }
        }
        else
        {
            // No line numbers requested. Walk paragraphs and emit pure content.
            var selStart = block.SelectionStart;
            var selEnd = block.SelectionEnd;
            if (selStart is null || selEnd is null)
            {
                textToCopy = selectedText;
            }
            else
            {
                int startOff = selStart.Offset;
                int endOff = selEnd.Offset;
                var sb = new StringBuilder();
                bool first = true;
                foreach (var b in block.Blocks)
                {
                    if (b is not Paragraph para) continue;
                    var pStart = para.ContentStart;
                    var pEnd = para.ContentEnd;
                    if (pStart is null || pEnd is null) continue;
                    if (pEnd.Offset <= startOff) continue;
                    if (pStart.Offset >= endOff) break;

                    // Skip structural paragraphs (separators, gap spacers) in the
                    // multi-section view; they aren't real source lines.
                    if (!hasInlineGutter
                        && !s_paragraphLineNumbers.TryGetValue(para, out _)
                        && !s_paragraphIsContinuation.TryGetValue(para, out _))
                    {
                        continue;
                    }

                    string lineText = ExtractParagraphContent(para, hasInlineGutter);
                    if (!first) sb.AppendLine();
                    first = false;
                    sb.Append(lineText);
                }
                textToCopy = sb.ToString();
            }
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(textToCopy);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    /// <summary>
    /// Concatenates the text of all Run inlines in a paragraph, optionally skipping
    /// the first three inlines which form the inline gutter chrome
    /// (indicator + lineNum + separator) when the paragraph uses inline gutter mode.
    /// </summary>
    private static string ExtractParagraphContent(Paragraph para, bool hasInlineGutter)
    {
        var sb = new StringBuilder();
        int skip = hasInlineGutter ? 3 : 0;
        int idx = 0;
        foreach (var inline in para.Inlines)
        {
            if (idx++ < skip) continue;
            if (inline is Run run) sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the source line number for a paragraph. Prefers the
    /// <see cref="s_paragraphLineNumbers"/> CWT but falls back to parsing the
    /// gutter Run (2nd inline) when present, so copy still works even if the
    /// CWT registration was missed.
    /// </summary>
    private static int ResolveParagraphLineNumber(Paragraph para, bool hasInlineGutter)
    {
        if (s_paragraphLineNumbers.TryGetValue(para, out var tag) && tag is int lineNum && lineNum > 0)
            return lineNum;

        if (!hasInlineGutter) return -1;

        // Inline gutter layout: [indicator][gutterRun ("{lineNum,5} " or "      ")][gutterSep]
        int idx = 0;
        foreach (var inline in para.Inlines)
        {
            if (idx == 1 && inline is Run gutter)
            {
                string text = gutter.Text;
                if (!string.IsNullOrWhiteSpace(text)
                    && int.TryParse(text.AsSpan().Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                    && parsed > 0)
                {
                    return parsed;
                }
                return -1;
            }
            idx++;
            if (idx > 1) break;
        }
        return -1;
    }

    private static int GetParagraphLineNumber(RichTextBlock block, Microsoft.UI.Xaml.Documents.TextPointer? pointer)
    {
        if (pointer is null) return 1;
        // Walk paragraphs to find which one the selection starts in,
        // using ConditionalWeakTable set during preview line construction.
        var paragraphs = block.Blocks.OfType<Paragraph>().ToList();
        int lastKnownLine = 1;
        foreach (var para in paragraphs)
        {
            if (s_paragraphLineNumbers.TryGetValue(para, out var tag) && tag is int lineNum)
                lastKnownLine = lineNum;
            var paraStart = para.ContentStart;
            var paraEnd = para.ContentEnd;
            if (paraStart is not null && paraEnd is not null
                && pointer.Offset >= paraStart.Offset && pointer.Offset <= paraEnd.Offset)
                return lastKnownLine;
        }
        return lastKnownLine;
    }

    private async Task ExportSingleFileHtmlReportAsync(string filePath)
    {
        var group = ViewModel.ResultGroups.FirstOrDefault(g =>
            string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (group is null || group.Count == 0) return;

        // Show export options dialog (same as the main report button)
        var exportOptions = await ReportExportDialog.ShowAsync(_hwnd, ViewModel.ContextLines);
        if (exportOptions is null) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        switch (exportOptions.Format)
        {
            case Services.ReportFormat.Json:
                picker.FileTypeChoices.Add("JSON File", new List<string> { ".json" });
                picker.SuggestedFileName = $"Yagu_Report_{Path.GetFileNameWithoutExtension(filePath)}";
                break;
            case Services.ReportFormat.Csv:
                picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });
                picker.SuggestedFileName = $"Yagu_Report_{Path.GetFileNameWithoutExtension(filePath)}";
                break;
            default:
                picker.FileTypeChoices.Add("HTML File", new List<string> { ".html" });
                picker.SuggestedFileName = $"Yagu_Report_{Path.GetFileNameWithoutExtension(filePath)}";
                break;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // Hydrate all results before writing
        foreach (var result in group)
            ViewModel.HydrateResult(result);

        int totalMatches = group.Count;

        await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        using var w = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: false);

        var fileGroup = new HtmlReportExportService.FileMatchGroup(group.FilePath, group.FileName, group.ToList());
        var stats = new HtmlReportExportService.SearchStats(
            ViewModel.SearchStartedUtc,
            ViewModel.LastSearchElapsed,
            ViewModel.FilesScanned,
            ViewModel.BytesScanned);

        switch (exportOptions.Format)
        {
            case Services.ReportFormat.Json:
                await Services.ReportExportService.WriteJsonReportAsync(w, ViewModel.Query, [fileGroup], stats, exportOptions).ConfigureAwait(false);
                break;
            case Services.ReportFormat.Csv:
                await Services.ReportExportService.WriteCsvReportAsync(w, ViewModel.Query, [fileGroup], exportOptions).ConfigureAwait(false);
                break;
            default:
                await HtmlReportExportService.WriteSingleFileReportAsync(w, ViewModel.Query, fileGroup).ConfigureAwait(false);
                break;
        }

        var formatName = exportOptions.Format.ToString().ToUpperInvariant();
        ViewModel.StatusText = $"Exported {formatName} report ({totalMatches:N0} matches) for {Path.GetFileName(filePath)} to {file.Path}";
    }

    private void HighlightInline(Paragraph para, string line, int matchStart, int matchLength)
    {
        var displayLine = LineTruncator.TruncateAroundMatch(line, matchStart, matchLength);
        line = displayLine.Text;
        matchStart = displayLine.MatchStart;
        if (matchStart >= 0 && matchStart < line.Length && matchLength > 0)
        {
            int safeLen = Math.Min(matchLength, line.Length - matchStart);
            if (matchStart > 0) para.Inlines.Add(new Run { Text = line[..matchStart] });
            var hit = new Run { Text = line.Substring(matchStart, safeLen) };
            hit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            hit.Foreground = _resultMatchTextBrush;
            para.Inlines.Add(hit);
            if (matchStart + safeLen < line.Length)
                para.Inlines.Add(new Run { Text = line[(matchStart + safeLen)..] });
        }
        else
        {
            para.Inlines.Add(new Run { Text = line });
        }
    }

    private async Task UpdatePreviewAsync(SearchResult r)
    {
        LogService.Instance.Info("Preview", $"UpdatePreviewAsync: file='{r.FilePath}', line={r.LineNumber}");
        if (!TryLeavePreviewEditorForPreviewChange()) return;

        BeginPreviewContentUpdate();
        EnsurePreviewPanelVisible();

        // Hydrate from disk if this result was evicted during memory pressure.
        ViewModel.HydrateResult(r);
        _previewResult = r;
        SetPreviewFileLabel(r.FilePath);
        PreviewToolbarContent.Visibility = Visibility.Visible;
        await ShowSingleFilePreviewAsync(r, fullFile: false);
    }

    private async Task UpdateMultiSelectPreviewAsync(SearchResult? scrollTarget = null, bool scrollToTop = false)
    {
        LogService.Instance.Info("Preview", $"UpdateMultiSelectPreviewAsync: scrollTarget='{scrollTarget?.FilePath}', scrollToTop={scrollToTop}");
        int gen = ++_previewUpdateGen;

        if (!TryLeavePreviewEditorForPreviewChange()) return;

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0)
        {
            EnsurePreviewPanelVisible();
            ShowPreviewBlockSurface();
            PreviewBlock.Blocks.Clear();
            SetPreviewFileLabel(string.Empty);
            PreviewToolbarContent.Visibility = Visibility.Collapsed;
            _previewResult = null;
            CompletePreviewContentUpdate();
            return;
        }

        BeginPreviewContentUpdate();
        EnsurePreviewPanelVisible();

        // Hydrate any evicted results before rendering the preview.
        foreach (var r in selected)
            ViewModel.HydrateResult(r);

        // Group by file first so we know file count for the loading message.
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in selected)
        {
            if (!byFile.TryGetValue(r.FilePath, out var list))
            {
                list = new List<SearchResult>();
                byFile[r.FilePath] = list;
            }
            list.Add(r);
        }

        // Show loading indicator immediately for large previews.
        if (byFile.Count > EffectivePreviewSectionPageSize)
            ShowPreviewLoading($"Reading {byFile.Count:N0} files\u2026");

        PreviewToolbarContent.Visibility = Visibility.Visible;
        LogService.Instance.Info("Preview", $"UpdateMultiSelectPreviewAsync: byFile={byFile.Count} files, {selected.Count} results, mode={ViewModel.PreviewModeIndex}, gen={gen}");
        if (ViewModel.PreviewModeIndex == 1)
            await ShowMultiHighlightPreviewAsync(selected, byFile, scrollTarget, gen, scrollToTop);
        else
            await ShowConcatenatedPreviewAsync(selected, byFile, scrollTarget, gen, scrollToTop);
    }

    private async Task ShowConcatenatedPreviewAsync(
        List<SearchResult> selected,
        Dictionary<string, List<SearchResult>> byFile,
        SearchResult? scrollTarget, int gen, bool scrollToTop)
    {
        LogService.Instance.Info("Preview", $"ShowConcatenatedPreviewAsync: {byFile.Count} files, {selected.Count} results, gen={gen}");
        ShowPreviewSectionsSurface();
        _matchParagraphs.Clear();
        _sectionOverflow.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        int previewLines = ViewModel.PreviewContextLines;
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
        SetPreviewMatchTotals(ComputeMatchCount(selected, null, isHighlight: false, previewLines, rx), byFile.Count);

        RichTextBlock? scrollBlock = null;
        Paragraph? scrollPara = null;

        // Reorder so scrollTarget's file comes first (will appear at top)
        var orderedFiles = OrderByFileFirst(byFile, scrollTarget?.FilePath).ToList();

        // Determine which files to render in this page.
        int pageEnd = Math.Min(orderedFiles.Count, EffectivePreviewSectionPageSize);
        var pageFiles = orderedFiles.GetRange(0, pageEnd);

        // Batch-read page file contents off the UI thread.
        var fileContents = await ReadAllFileContentsAsync(pageFiles);
        if (_previewUpdateGen != gen) return;
        HidePreviewLoading();

        int fileIndex = 0;
        foreach (var (filePath, results) in pageFiles)
        {
            var fileSw = System.Diagnostics.Stopwatch.StartNew();
            var (section, _) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results);

            fileContents.TryGetValue(filePath, out string[]? allLines);
            bool truncatePreviewLines = ShouldTruncateInitialPreviewLines();
            int parasInFile = 0;

            int renderedResults = 0;
            int startingBlocks = section.Blocks.Count;
            int cap = Math.Min(results.Count, EffectiveMaxMatchesPerSection);
            var matchLineNums = new HashSet<int>(results.Select(r => r.LineNumber));
            bool renderedFileNameOnlyPreview = false;
            foreach (var r in results)
            {
                if (renderedResults >= cap || section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                    break;

                bool isFileNameOnlyPreview = r.LineNumber <= 0;
                if (isFileNameOnlyPreview && renderedFileNameOnlyPreview)
                {
                    renderedResults++;
                    continue;
                }

                if (!isFileNameOnlyPreview)
                {
                    // Separator between matches in same file
                    var sep = new Paragraph();
                    var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                    var lineChar = '\u2500'; // ─ box-drawing horizontal
                    const int shortLeft = 6;
                    const int shortRight = 6;
                    var sepRun = new Run { Text = $"{new string(lineChar, shortLeft)}{label}{new string(lineChar, shortRight)}" };
                    sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                    sep.Inlines.Add(sepRun);
                    sep.Margin = new Thickness(0, 8, 0, 4);
                    section.Blocks.Add(sep);
                    SyncGutterSpacer(section, sep.Margin);
                }

                var lines = GetPreviewLines(r, allLines, previewLines, fullFile: isFileNameOnlyPreview);
                foreach (var (line, lineNum) in lines)
                {
                    if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                        break;

                    bool isMatchLine = !isFileNameOnlyPreview && matchLineNums.Contains(lineNum);
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    var firstPara = AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, isFileNameOnlyPreview ? null : rx, truncate: !isFileNameOnlyPreview && truncatePreviewLines,
                        isMatchLine ? _matchParagraphs : null, sn, out int addedParagraphs,
                        maxParagraphs: MaxPreviewBlocksPerSection - (section.Blocks.Count - startingBlocks));
                    parasInFile += addedParagraphs;

                    if (scrollTarget is not null && lineNum == r.LineNumber
                        && r.LineNumber == scrollTarget.LineNumber
                        && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        scrollBlock = section;
                        scrollPara = firstPara;
                    }
                }

                renderedResults++;
                renderedFileNameOnlyPreview |= isFileNameOnlyPreview;
            }

            if (results.Count > renderedResults)
            {
                var notice = AppendTruncationNotice(section, results.Count, renderedResults);
                RegisterSectionOverflow(section,
                    filePath: filePath,
                    remainingResults: results.GetRange(renderedResults, results.Count - renderedResults),
                    allLines: allLines,
                    previewLines: previewLines,
                    rx: rx,
                    originalTotal: results.Count,
                    renderedSoFar: renderedResults,
                    noticePara: notice);
            }

            fileSw.Stop();
            LogService.Instance.Verbose("Preview", $"ShowConcatenatedPreviewAsync: file='{System.IO.Path.GetFileName(filePath)}', results={results.Count}, paragraphs={parasInFile}, elapsed={fileSw.ElapsedMilliseconds}ms");

            // Yield to the UI thread periodically so the app stays responsive.
            if (++fileIndex % PreviewYieldBatchSize == 0)
            {
                await Task.Delay(1).ConfigureAwait(true);
                if (_previewUpdateGen != gen) return;
            }
        }

        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];

        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();

        if (scrollBlock is not null && scrollPara is not null)
            SetCurrentMatchToParagraph(scrollBlock, scrollPara);

        if (scrollToTop)
            PreviewScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        else if (scrollBlock is not null && scrollPara is not null)
            ScrollPreviewToLine(scrollBlock, scrollPara);

        // Auto-load remaining files using the efficient off-tree batched approach.
        if (pageEnd < orderedFiles.Count)
            await AutoLoadRemainingSectionsAsync(orderedFiles, pageEnd, selected, gen);
    }

    private async Task ShowMultiHighlightPreviewAsync(
        List<SearchResult> selected,
        Dictionary<string, List<SearchResult>> byFile,
        SearchResult? scrollTarget, int gen, bool scrollToTop)
    {
        LogService.Instance.Info("Preview", $"ShowMultiHighlightPreviewAsync: {byFile.Count} files, {selected.Count} results, gen={gen}");
        ShowPreviewSectionsSurface();
        _matchParagraphs.Clear();
        _sectionOverflow.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
        SetPreviewMatchTotals(ComputeMatchCount(selected, null, isHighlight: true, ViewModel.PreviewContextLines, rx), byFile.Count);

        RichTextBlock? scrollBlock = null;
        Paragraph? scrollPara = null;

        // Reorder so scrollTarget's file comes first (will appear at top)
        var orderedFiles = OrderByFileFirst(byFile, scrollTarget?.FilePath).ToList();

        // Determine which files to render in this page.
        int pageEnd = Math.Min(orderedFiles.Count, EffectivePreviewSectionPageSize);
        var pageFiles = orderedFiles.GetRange(0, pageEnd);

        // Batch-read page file contents off the UI thread.
        var fileContents = await ReadAllFileContentsAsync(pageFiles);
        if (_previewUpdateGen != gen) return;
        HidePreviewLoading();

        int fileIndex = 0;
        foreach (var (filePath, results) in pageFiles)
        {
            var fileSw = System.Diagnostics.Stopwatch.StartNew();
            var (section, _) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results);
            int sectionMatchStart = _matchParagraphs.Count;
            int startingBlocks = section.Blocks.Count;

            fileContents.TryGetValue(filePath, out string[]? allLines);
            bool truncatePreviewLines = ShouldTruncateInitialPreviewLines();
            if (results.All(result => result.LineNumber <= 0))
            {
                BuildConcatenatedSection(section, results, allLines, ViewModel.PreviewContextLines, rx: null);
                fileSw.Stop();
                LogService.Instance.Verbose("Preview", $"ShowMultiHighlightPreviewAsync: filename-only file='{System.IO.Path.GetFileName(filePath)}', results={results.Count}, blocks={section.Blocks.Count}, elapsed={fileSw.ElapsedMilliseconds}ms");

                if (++fileIndex % PreviewYieldBatchSize == 0)
                {
                    await YieldLowAsync();
                    if (_previewUpdateGen != gen) return;
                }

                continue;
            }

            bool initiallyCapped = results.Count > EffectiveMaxMatchesPerSection;
            var cappedResults = initiallyCapped ? results.GetRange(0, EffectiveMaxMatchesPerSection) : results;
            int previewLines = ViewModel.PreviewContextLines;
            int lastRenderedLine1 = 0;

            if (allLines != null)
            {
                // Compute line ranges to display (union of all match windows)
                var ranges = new List<(int start, int end)>();
                foreach (var lineNum in cappedResults.Select(r => r.LineNumber).Distinct().OrderBy(n => n))
                {
                    int s = Math.Max(0, lineNum - 1 - previewLines);
                    int e = Math.Min(allLines.Length - 1, lineNum - 1 + previewLines);
                    ranges.Add((s, e));
                }

                // Merge overlapping ranges
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
                    if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                        break;

                    if (!firstRange)
                    {
                        AddGapIndicator(section);
                    }
                    firstRange = false;

                    for (int i = start; i <= end; i++)
                    {
                        if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                            break;

                        int lineNum = i + 1;
                        bool isMatchLine = matchByLine.TryGetValue(lineNum, out var matchResult);
                        matchResult ??= cappedResults[0];
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        var firstPara = AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatchLine, matchResult, rx, truncate: truncatePreviewLines, _matchParagraphs, sn, out _,
                            maxParagraphs: MaxPreviewBlocksPerSection - (section.Blocks.Count - startingBlocks));
                        lastRenderedLine1 = lineNum;

                        if (scrollTarget is not null && isMatchLine && lineNum == scrollTarget.LineNumber
                            && string.Equals(filePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = firstPara;
                        }
                    }
                }
            }
            else
            {
                // Fallback: concatenated style
                var fallbackMatchLines = new HashSet<int>(cappedResults.Select(r => r.LineNumber));
                int fallbackIndex = 0;
                foreach (var r in cappedResults)
                {
                    if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                        break;

                    var lines = GetPreviewLines(r, null, ViewModel.PreviewContextLines, fullFile: false);
                    foreach (var (line, lineNum) in lines)
                    {
                        if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                            break;

                        bool isMatchLine = fallbackMatchLines.Contains(lineNum);
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        var firstPara = AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: truncatePreviewLines,
                            lineNum == r.LineNumber ? _matchParagraphs : null, sn, out _,
                            maxParagraphs: MaxPreviewBlocksPerSection - (section.Blocks.Count - startingBlocks));

                        if (scrollTarget is not null && lineNum == r.LineNumber
                            && r.LineNumber == scrollTarget.LineNumber
                            && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = firstPara;
                        }
                    }
                    fallbackIndex++;
                }
            }

            int renderedCount = allLines != null
                ? CountPrefixResultsThroughLine(results, lastRenderedLine1, allLines.Length)
                : Math.Min(results.Count, _matchParagraphs.Count - sectionMatchStart);
            int actualMatchEntries = _matchParagraphs.Count - sectionMatchStart;
            // A long single-line file can render only a truncated window with a
            // handful of visible highlights even though every SearchResult is on
            // that same source line. Treating the source line as fully rendered
            // hides the overflow and makes the nav labels say "1 of 5" instead
            // of the file's real match count.
            if (allLines != null && renderedCount > actualMatchEntries && actualMatchEntries < results.Count)
                renderedCount = actualMatchEntries;
            int remainingBlockBudget = Math.Max(0, MaxPreviewBlocksPerSection - (section.Blocks.Count - startingBlocks));
            if (allLines != null && remainingBlockBudget > 0 && renderedCount < Math.Min(EffectiveMaxMatchesPerSection, results.Count))
            {
                _sectionMatchNavs.TryGetValue(section, out var sn);
                var pending = results.Skip(renderedCount).ToList();
                AppendHighlightMatchWindows(
                    section,
                    pending,
                    allLines,
                    rx,
                    sn,
                    ViewModel.PreviewContextLines,
                    EffectiveMaxMatchesPerSection - renderedCount,
                    EffectiveMaxMatchesPerSection - renderedCount,
                    remainingBlockBudget,
                    out int consumed,
                    out _,
                    out int appendLastRenderedLine,
                    lastRenderedLine1,
                    truncatePreviewLines);
                lastRenderedLine1 = Math.Max(lastRenderedLine1, appendLastRenderedLine);
                renderedCount = Math.Min(results.Count, renderedCount + consumed);
            }
            else if (allLines == null)
            {
                renderedCount = Math.Min(EffectiveMaxMatchesPerSection, results.Count);
            }
            var remaining = results.Skip(renderedCount).ToList();
            if (remaining.Count > 0)
            {
                var notice = AppendTruncationNotice(section, results.Count, renderedCount);
                RegisterSectionOverflow(section,
                    filePath: filePath,
                    remainingResults: remaining,
                    allLines: allLines,
                    previewLines: ViewModel.PreviewContextLines,
                    rx: rx,
                    originalTotal: results.Count,
                    renderedSoFar: renderedCount,
                    noticePara: notice,
                    isHighlightMode: allLines != null,
                    lastRenderedLine: lastRenderedLine1);
            }

            fileSw.Stop();
            LogService.Instance.Verbose("Preview", $"ShowMultiHighlightPreviewAsync: file='{System.IO.Path.GetFileName(filePath)}', results={results.Count}, blocks={section.Blocks.Count}, elapsed={fileSw.ElapsedMilliseconds}ms");

            // Yield to the UI thread periodically so the app stays responsive.
            if (++fileIndex % PreviewYieldBatchSize == 0)
            {
                await Task.Delay(1).ConfigureAwait(true);
                if (_previewUpdateGen != gen) return;
            }
        }

        // Add "Show more" button if there are remaining files.
        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];

        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();

        // Activate the section for the clicked file and align global nav with it.
        if (scrollBlock is not null && scrollPara is not null)
            SetCurrentMatchToParagraph(scrollBlock, scrollPara);
        else if (scrollBlock is not null)
            ActivateSectionForBlock(scrollBlock);

        if (scrollToTop)
            PreviewScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        else if (scrollBlock is not null && scrollPara is not null)
            ScrollPreviewToLine(scrollBlock, scrollPara);

        // Auto-load remaining files using the efficient off-tree batched approach.
        if (pageEnd < orderedFiles.Count)
            await AutoLoadRemainingSectionsAsync(orderedFiles, pageEnd, selected, gen);
    }
}
