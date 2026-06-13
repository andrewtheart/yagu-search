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
            tb.FontStyle = string.IsNullOrEmpty(tb.Text) || IsFilterExampleText(tb)
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal;
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

        return string.Equals(text, ViewModel.ExcludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, AppSettings.DefaultExcludeGlobs, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnSearchCancelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSearching)
        {
            await ViewModel.CancelAsync();
        }
        else
        {
            HideQuerySuggestions();
            if (!await ClearPreviewPanelForNewSearchAsync()) return;
            if (!await CheckHddAndWarnAsync()) return;
            CollapseAdvancedOptionsForSearch();
            await ViewModel.StartSearchAsync();
        }
    }

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var submittedQuery = args.ChosenSuggestion as string;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = args.QueryText;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = sender.Text;

        if (!string.IsNullOrEmpty(submittedQuery))
            ViewModel.Query = submittedQuery;

        HideQuerySuggestions(sender);
        if (!await ClearPreviewPanelForNewSearchAsync()) return;
        if (!await CheckHddAndWarnAsync()) return;
        CollapseAdvancedOptionsForSearch();
        await ViewModel.StartSearchAsync();
    }

    private void CollapseAdvancedOptionsForSearch()
    {
        if (AdvancedOptionsExpander.IsExpanded)
            AdvancedOptionsExpander.IsExpanded = false;
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter is handled by OnQuerySubmitted — only handle Escape here.
        if (e.Key == VirtualKey.Escape && ViewModel.IsSearching)
        {
            e.Handled = true;
            await ViewModel.CancelAsync();
        }
        // Down arrow opens the search history dropdown.
        else if (e.Key == VirtualKey.Down && !QueryBox.IsSuggestionListOpen
                 && !AreQuerySuggestionsSuppressed()
                 && ViewModel.SearchHistory.Count > 0)
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

    private List<string> BuildQuerySuggestions(string? queryText)
    {
        string filter = queryText?.Trim() ?? string.Empty;
        if (filter.Length == 0)
            return ViewModel.SearchHistory.ToList();

        return ViewModel.SearchHistory
            .Where(entry => entry.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

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
        if (AreQuerySuggestionsSuppressed() || ViewModel.SearchHistory.Count == 0)
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

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        _ = PickFolderAsync(picker);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handlers are bound as instance methods.")]
    private void OnDirectoryQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // User pressed Enter in the directory box — just accept the text (already bound).
    }

    private void OnDirectoryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only respond to user typing, not programmatic changes.
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _ = ViewModel.UpdateDirectorySuggestionsAsync(sender.Text);
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handlers are bound as instance methods.")]
    private void OnDirectorySuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string chosen)
        {
            // Append trailing backslash so user can continue drilling down.
            sender.Text = chosen.EndsWith('\\') ? chosen : chosen + '\\';
        }
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
