using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Yagu.Services;
namespace Yagu;

/// <summary>
/// Search input, query suggestions, directory entry, and live search controls.
/// </summary>
public sealed partial class MainWindow
{
    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_autoScrollEnabled || ViewModel.ResultRows.Count == 0) return;
        if (_resultsListTopRestoreInProgress) return;
        if (_resultsListShowMoreRestoreInProgress) return;
        if (_resultsListWasAtTop) return;
        ScrollResultsListToBottom();
    }

    private void UpdateSparkline()
    {
        var samples = _diskUtilService.GetSamples();

        // Update gauge bar and label even with few samples
        if (samples.Count > 0)
        {
            var latest = samples[^1];
            double gaugeContainerWidth = DiskGaugeBar.Parent is FrameworkElement parent ? parent.ActualWidth : 0;
            if (gaugeContainerWidth > 0)
                DiskGaugeBar.Width = latest.UtilizationPct / 100.0 * gaugeContainerWidth;

            DiskGaugeLabel.Text = $"{latest.MBPerSec:N0} MB/s \u00b7 {latest.UtilizationPct:N0}%";
        }
        else
        {
            DiskGaugeBar.Width = 0;
            DiskGaugeLabel.Text = string.Empty;
        }

        // Sparkline needs at least 2 points
        if (samples.Count < 2)
        {
            ThroughputSparkline.Points.Clear();
            return;
        }

        double width = ThroughputSparkline.ActualWidth;
        double height = ThroughputSparkline.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Plot disk MB/s
        double max = 1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].MBPerSec > max) max = samples[i].MBPerSec;
        }

        var pts = ThroughputSparkline.Points;
        pts.Clear();
        double xStep = width / (samples.Count - 1);
        for (int i = 0; i < samples.Count; i++)
        {
            double x = i * xStep;
            double y = height - (samples[i].MBPerSec / max * (height - 2)) - 1;
            pts.Add(new Windows.Foundation.Point(x, y));
        }
    }

    private void SetAutoScrollEnabled(bool enabled)
    {
        _autoScrollEnabled = enabled;
        if (AutoScrollResultsCheckBox.IsChecked != enabled)
            AutoScrollResultsCheckBox.IsChecked = enabled;
    }

    private void OnAutoScrollResultsChanged(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = AutoScrollResultsCheckBox.IsChecked == true;
        if (_autoScrollEnabled && ViewModel.ResultRows.Count > 0)
            ScrollResultsListToBottom();
    }

    private void OnFilterBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.FontStyle = Windows.UI.Text.FontStyle.Normal;
    }

    private void OnFilterBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (IsFilterExampleText(tb))
            tb.Text = string.Empty;

        tb.PlaceholderText = string.Empty;
    }

    private void OnFilterBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (string.IsNullOrEmpty(tb.Text))
        {
            if (ReferenceEquals(tb, IncludeFilterBox))
                tb.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
            else
                tb.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
        }
    }

    private bool IsFilterExampleText(TextBox textBox)
    {
        string text = textBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return false;

        if (ReferenceEquals(textBox, IncludeFilterBox))
            return string.Equals(text, ViewModel.IncludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase);

        return string.Equals(text, ViewModel.ExcludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnSearchCancelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsTranslatingSemanticQuery)
        {
            // The AI is mid-translation — clicking Cancel aborts the model inference, not a file scan.
            ViewModel.CancelSemanticTranslation();
            return;
        }
        if (ViewModel.IsSearching)
        {
            await ViewModel.CancelAsync();
        }
        else
        {
            await StartSearchFromUiAsync();
        }
    }

    // SplitButton primary action — only visible while idle, so it always starts a search.
    private async void OnSearchSplitButtonClick(SplitButton sender, SplitButtonClickEventArgs args) =>
        await StartSearchFromUiAsync();

    private async Task StartSearchFromUiAsync()
    {
        HideQuerySuggestions();
        if (!await ClearPreviewPanelForNewSearchAsync()) return;
        CollapseAdvancedOptionsForSearch();
        // The HDD and excluded-extension warnings run as a gate inside SubmitSearchAsync, AFTER any
        // semantic translation, so they evaluate the directory/target the AI model actually resolved
        // (e.g. a query resolving to "C:\" — an SSD — no longer shows a spurious HDD warning first).
        await ViewModel.SubmitSearchAsync(RunPreSearchWarningGatesAsync);
    }

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var submittedQuery = (args.ChosenSuggestion as Yagu.Models.HistorySuggestion)?.Value;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = args.QueryText;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = sender.Text;

        bool textApplied = false;
        if (!string.IsNullOrEmpty(submittedQuery))
        {
            // Show the chosen text in the box as the VERY FIRST thing, before any search work. Setting
            // sender.Text directly makes a clicked history item appear immediately instead of only once
            // the UI thread next yields — the delay the user sees in Semantic mode, where translation
            // briefly occupies the thread before the bound text repaints.
            if (sender.Text != submittedQuery)
                sender.Text = submittedQuery;
            ViewModel.Query = submittedQuery;
            textApplied = true;
        }

        HideQuerySuggestions(sender);

        // Let the box paint the chosen text before the (possibly slow) search pipeline begins.
        if (textApplied)
            await YieldUntilRenderedAsync();

        if (!await ClearPreviewPanelForNewSearchAsync()) return;
        CollapseAdvancedOptionsForSearch();
        await ViewModel.SubmitSearchAsync(RunPreSearchWarningGatesAsync);
    }

    /// <summary>
    /// Completes after the UI thread has processed its pending layout/render work, so a visual change
    /// made immediately before the call (e.g. the query text set from a chosen history item) paints
    /// before slower follow-up work (such as semantic translation) starts occupying the thread. A
    /// Low-priority dispatcher callback runs after the in-flight frame, giving the text time to show.
    /// </summary>
    private Task YieldUntilRenderedAsync()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => rendered.SetResult()))
            rendered.SetResult();
        return rendered.Task;
    }

    /// <summary>
    /// Combined pre-search warning gate, run by <see cref="MainViewModel.SubmitSearchAsync"/> AFTER
    /// any semantic translation. Running both notices here (rather than before the search) means a
    /// semantic search evaluates them against the directory/target the AI model resolved — so a query
    /// that resolves to an SSD no longer shows a spurious HDD warning before the model has even run.
    /// Returns false to abort the search. The HDD check runs first, then the excluded-extension check.
    /// </summary>
    private async Task<bool> RunPreSearchWarningGatesAsync()
    {
        if (!await CheckHddAndWarnAsync()) return false;
        return await CheckExcludedExtensionAndWarnAsync();
    }

    private void OnSelectTraditionalMode(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSemanticQueryMode = false;
    }

    private async void OnSelectSemanticMode(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.SemanticSearchAvailable)
            return;

        // Already downloaded a model before — just switch.
        if (ViewModel.IsSemanticModelDownloaded)
        {
            ViewModel.IsSemanticQueryMode = true;
            return;
        }

        // First time: ask the user to download a local model. Semantic search can't run without one.
        var chosenAlias = await ShowSemanticModelDownloadDialogAsync();
        if (chosenAlias is not null)
        {
            ViewModel.IsSemanticQueryMode = true;
        }
        else
        {
            // Declined or failed — stay in Traditional mode and re-sync the menu highlight.
            ViewModel.IsSemanticQueryMode = false;
            UpdateSearchModeMenuHighlight();
        }
    }

    private void OnSearchModeFlyoutOpening(object? sender, object e) => UpdateSearchModeMenuHighlight();

    /// <summary>
    /// Marks the active query mode with a subtle highlight background instead of a radio bullet.
    /// </summary>
    private void UpdateSearchModeMenuHighlight()
    {
        Microsoft.UI.Xaml.Media.Brush? highlight = null;
        if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var res))
            highlight = res as Microsoft.UI.Xaml.Media.Brush;

        var semantic = ViewModel.IsSemanticQueryMode;
        SemanticModeItem.Background = semantic ? highlight : null;
        TraditionalModeItem.Background = semantic ? null : highlight;
    }

    /// <summary>
    /// Shows the borderless first-run model-download modal. Returns the chosen model alias on a
    /// successful download (empty string means "use the recommended/auto model"), or null when the
    /// user declined or the download failed.
    /// </summary>
    private Task<string?> ShowSemanticModelDownloadDialogAsync() =>
        SemanticModelDownloadDialog.ShowAsync(
            _hwnd,
            RootGrid.ActualTheme,
            (progress, token) => ViewModel.GetSemanticModelOptionsAsync(progress, token),
            (alias, progress, token) => ViewModel.PrepareSemanticModelAsync(alias, progress, token),
            ViewModel.SemanticModelAlias);

    private void CollapseAdvancedOptionsForSearch()
    {
        AdvancedOptionsFlyout?.Hide();
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter is handled by OnQuerySubmitted — only handle Escape here.
        if (e.Key == VirtualKey.Escape && ViewModel.IsTranslatingSemanticQuery)
        {
            e.Handled = true;
            ViewModel.CancelSemanticTranslation();
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.IsSearching)
        {
            e.Handled = true;
            await ViewModel.CancelAsync();
        }
        // Down arrow opens the search history dropdown.
        else if (e.Key == VirtualKey.Down && !QueryBox.IsSuggestionListOpen
                 && !AreQuerySuggestionsSuppressed()
                 && ActiveQueryHistory().Count > 0)
        {
            ApplyQuerySuggestions(QueryBox, open: true);
        }
    }

    private void HideQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        _querySuggestionsUserOpened = false;
        _querySuggestionsDetached = true;
        _hideSuggestionsTick = Environment.TickCount64;
        target.IsSuggestionListOpen = false;
        target.ItemsSource = null;
        target.IsSuggestionListOpen = false;
        // The AutoSuggestBox sometimes re-opens its popup after QuerySubmitted.
        // Fight back with a deferred close.
        DispatcherQueue.TryEnqueue(() =>
        {
            target.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => target.IsSuggestionListOpen = false);
        });
    }

    private void RestoreQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        if (AreQuerySuggestionsSuppressed())
        {
            target.IsSuggestionListOpen = false;
            return;
        }

        ApplyQuerySuggestions(target, open: false);
    }

    private void ApplyQuerySuggestions(AutoSuggestBox target, bool open)
    {
        if (AreQuerySuggestionsSuppressed())
        {
            target.IsSuggestionListOpen = false;
            return;
        }

        if (_querySuggestionsDetached)
        {
            if (Environment.TickCount64 - _hideSuggestionsTick < 400)
            {
                target.IsSuggestionListOpen = false;
                return;
            }

            _querySuggestionsDetached = false;
        }

        if (open)
            _querySuggestionsUserOpened = true;

        var suggestions = BuildQuerySuggestions(target.Text);
        target.ItemsSource = suggestions;
        target.IsSuggestionListOpen = open && suggestions.Count > 0;
    }

    private List<Yagu.Models.HistorySuggestion> BuildQuerySuggestions(string? queryText)
        => ViewModel.BuildQuerySuggestionItems(queryText);

    /// <summary>The autocomplete history that backs the query box for the active search mode:
    /// the Semantic natural-language history in Semantic mode, otherwise the Traditional history.</summary>
    private System.Collections.ObjectModel.ObservableCollection<string> ActiveQueryHistory()
        => ViewModel.IsSemanticQueryMode ? ViewModel.SemanticSearchHistory : ViewModel.SearchHistory;

    private bool AreQuerySuggestionsSuppressed()
        => Environment.TickCount64 < _suppressQuerySuggestionsUntilTick;

    private void SuppressQuerySuggestionsFor(int milliseconds, AutoSuggestBox? box = null)
    {
        long until = Environment.TickCount64 + milliseconds;
        if (until > _suppressQuerySuggestionsUntilTick)
            _suppressQuerySuggestionsUntilTick = until;

        HideQuerySuggestions(box);
    }

    private void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && !AreQuerySuggestionsSuppressed())
            ApplyQuerySuggestions(sender, open: sender.IsSuggestionListOpen || _querySuggestionsUserOpened);
    }

    /// <summary>
    /// In compact launcher mode the window is sized to its content height. The query and directory
    /// boxes wrap and grow with multi-line text, so re-fit the launcher whenever either one's height
    /// changes to keep the action bar below them (Search, load-session, and terminal buttons) visible
    /// at all times. Beyond each box's MaxHeight the text scrolls internally, so the window never
    /// outgrows the work area.
    /// </summary>
    private void OnSearchInputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_launcherMode && e.NewSize.Height != e.PreviousSize.Height)
            PositionLauncherWindow();
    }

    private void OnQueryClearClick(object sender, RoutedEventArgs e)
    {
        SuppressQuerySuggestionsFor(250, QueryBox);
        QueryBox.Text = string.Empty;
        ViewModel.Query = string.Empty;
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void OnQueryBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(QueryBox);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;

        DispatcherQueue.TryEnqueue(ShowQuerySuggestionsFromPointerFocus);
    }

    private void ShowQuerySuggestionsFromPointerFocus()
    {
        if (AreQuerySuggestionsSuppressed() || ActiveQueryHistory().Count == 0)
            return;

        ApplyQuerySuggestions(QueryBox, open: true);
        QueryBox.Focus(FocusState.Pointer);
    }

    private void OnQueryLostFocus(object sender, RoutedEventArgs e)
    {
        _querySuggestionsUserOpened = false;
        if (!AreQuerySuggestionsSuppressed())
            RestoreQuerySuggestions(sender as AutoSuggestBox);
    }

    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private async void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        _directoryBrowseInProgress = true;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            string? folderPath = Helpers.Win32FileDialog.SelectFolder(hwnd, "Select Search Directory");
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                ViewModel.Directory = folderPath;
                DirectoryBox.Text = folderPath;
                int suggestionCount = await ViewModel.UpdateDirectorySuggestionsForSelectedDirectoryAsync(folderPath);
                DirectoryBox.ItemsSource = ViewModel.DirectorySuggestions;
                DirectoryBox.Focus(FocusState.Programmatic);
                DirectoryBox.IsSuggestionListOpen = suggestionCount > 0;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Folder browse dialog failed.", ex);
            ViewModel.StatusText = "Could not open the folder browse dialog.";
        }
        finally
        {
            _directoryBrowseInProgress = false;
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handlers are bound as instance methods.")]
    private void OnDirectoryQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // User pressed Enter in the directory box — just accept the text (already bound).
    }

    private void OnDirectoryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            // User typing: fetch subdirectory suggestions for the new text.
            _ = ViewModel.UpdateDirectorySuggestionsAsync(sender.Text);
        }
        else if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange && !_directoryBrowseInProgress)
        {
            // A programmatic Directory change — e.g. a semantic search applying its resolved
            // directory and then restoring the user's default as the search starts — must NOT pop
            // the history dropdown open. (The Browse button sets the text too, but opens the list
            // deliberately afterward, so it is excluded via _directoryBrowseInProgress.) The
            // AutoSuggestBox can re-open its popup just after the change, so close it now and again
            // on the next tick.
            sender.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => sender.IsSuggestionListOpen = false);
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handlers are bound as instance methods.")]
    private void OnDirectorySuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is Yagu.Models.HistorySuggestion suggestion)
        {
            // Append trailing backslash so user can continue drilling down.
            string chosen = suggestion.Value;
            sender.Text = chosen.EndsWith('\\') ? chosen : chosen + '\\';
        }
    }

    /// <summary>
    /// Closes the directory history dropdown when the user presses anywhere outside the directory
    /// box. An <see cref="AutoSuggestBox"/>'s suggestion list only auto-closes when the box loses
    /// keyboard focus, so clicking a non-focusable surface (an icon, an empty panel, the results
    /// background) would otherwise leave the dropdown stranded open. Wired window-wide on RootGrid.
    /// Presses on the suggestion items themselves route through the popup layer rather than RootGrid,
    /// so they never reach this handler and choosing a suggestion still works.
    /// </summary>
    private void OnRootPointerPressedDismissDirectorySuggestions(object sender, PointerRoutedEventArgs e)
    {
        if (!DirectoryBox.IsSuggestionListOpen) return;
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, DirectoryBox)) return;
        DirectoryBox.IsSuggestionListOpen = false;
    }

    /// <summary>Walks the visual tree from <paramref name="node"/> upward, returning true if
    /// <paramref name="ancestor"/> is the node itself or any of its ancestors.</summary>
    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private void OnRestartAsAdmin(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            // Strip any pre-existing --wait-for-pid <n> tokens, then append our own
            // pointing at the current process so the elevated instance waits for us
            // to fully exit (and release the single-instance mutex) before starting.
            var existing = Environment.GetCommandLineArgs().Skip(1).ToList();
            for (int i = existing.Count - 2; i >= 0; i--)
            {
                if (string.Equals(existing[i], "--wait-for-pid", StringComparison.OrdinalIgnoreCase))
                {
                    existing.RemoveAt(i + 1);
                    existing.RemoveAt(i);
                }
            }
            existing.Add("--wait-for-pid");
            existing.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var args = string.Join(" ", existing.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            // Release the single-instance mutex BEFORE starting the elevated process,
            // so there's no race where the new instance sees the mutex still owned.
            try
            {
                App.InstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException) { /* not owned — ignore */ }
            App.InstanceMutex?.Dispose();
            App.InstanceMutex = null;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt — re-acquire the mutex so this instance
            // remains the single instance, then do nothing.
            try
            {
                App.InstanceMutex = new System.Threading.Mutex(true, @"Global\YaguSingleInstance", out _);
            }
            catch { /* best-effort */ }
        }
    }

    private async void OnDontShowAdminWarningAgain(object sender, RoutedEventArgs e)
    {
        ViewModel.SuppressAdminWarning = true;
        await ViewModel.PersistSettingsAsync();
        AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_launcherMode)
            PositionLauncherWindow();
    }

    private void OnAdminBannerCloseClick(object sender, RoutedEventArgs e)
    {
        AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_launcherMode)
            PositionLauncherWindow();
        FocusSearchBox();
    }
}
