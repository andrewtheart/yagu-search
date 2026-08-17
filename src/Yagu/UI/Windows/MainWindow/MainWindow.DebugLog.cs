using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Logging;
using Microsoft.Extensions.Logging;
using YaguLogLevel = Yagu.Services.LogLevel;

namespace Yagu;

/// <summary>Developer-only live tail of the active Yagu log.</summary>
public sealed partial class MainWindow
{
    private const string AllLogCategories = "All categories";

    private readonly List<LogTailEntry> _debugLogEntries = [];
    private readonly ObservableCollection<LogTailEntry> _debugLogVisibleEntries = [];
    private LogTailReader? _debugLogReader;
    private DispatcherTimer? _debugLogTimer;
    private bool _updatingDebugLogControls;
    private bool _debugLogFollowTailInitialized;

    private void OnDebugLogFlyoutOpened(object? sender, object e)
    {
        _debugLogReader ??= new LogTailReader(LogService.Instance.LogFilePath);
        if (!ReferenceEquals(DebugLogList.ItemsSource, _debugLogVisibleEntries))
            DebugLogList.ItemsSource = _debugLogVisibleEntries;

        _updatingDebugLogControls = true;
        try
        {
            if (!_debugLogFollowTailInitialized)
            {
                _debugLogFollowTailInitialized = true;
                DebugLogFollowTail.IsChecked = true;
            }
            if (DebugLogCategoryFilter.Items.Count == 0)
                DebugLogCategoryFilter.Items.Add(AllLogCategories);
            DebugLogCategoryFilter.SelectedIndex = Math.Max(0, DebugLogCategoryFilter.SelectedIndex);
            DebugLogSeverityFilter.SelectedIndex = Math.Max(0, DebugLogSeverityFilter.SelectedIndex);
            DebugLogSinceFilter.SelectedIndex = Math.Max(0, DebugLogSinceFilter.SelectedIndex);
            DebugLogFileLevel.SelectedIndex = (int)LogService.Instance.FileLevel + 1;
            DebugLogConsoleLevel.SelectedIndex = (int)LogService.Instance.ConsoleLevel + 1;
        }
        finally
        {
            _updatingDebugLogControls = false;
        }

        RefreshDebugLogTail();
        _debugLogTimer ??= CreateDebugLogTimer();
        _debugLogTimer.Start();
    }

    private DispatcherTimer CreateDebugLogTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += OnDebugLogTimerTick;
        return timer;
    }

    private void OnDebugLogTimerTick(object? sender, object e) => RefreshDebugLogTail();

    private void OnDebugLogFlyoutClosed(object? sender, object e) => _debugLogTimer?.Stop();

    private void OnDebugLogCloseClicked(object sender, RoutedEventArgs e) => DebugLogFlyout.Hide();

    private void OnDebugLogRefreshNow(object sender, RoutedEventArgs e) => RefreshDebugLogTail();

    private void OnCopyDebugLogEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string { Length: > 0 } rawText })
            SetClipboardText(rawText, "log entry");
    }

    private async void OnDebugLogExportVisible(object sender, RoutedEventArgs e)
    {
        string[] visibleEntries = _debugLogVisibleEntries.Select(static entry => entry.RawText).ToArray();
        if (visibleEntries.Length == 0)
            return;

        string? path;
        try
        {
            path = Win32FileDialog.Save(
                _hwnd,
                "Export visible Yagu logs",
                $"Yagu_Logs_{DateTime.Now:yyyyMMdd_HHmmss}",
                "log",
                [
                    ("Log files", "*.log"),
                    ("Text files", "*.txt"),
                    ("All files", "*.*"),
                ]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "Could not open the live-log export dialog.");
            DebugLogStatusText.Text = $"Could not export logs: {ex.Message}";
            return;
        }

        if (path is null)
            return;

        try
        {
            string text = string.Join(Environment.NewLine, visibleEntries) + Environment.NewLine;
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            DebugLogStatusText.Text = $"Exported {visibleEntries.Length:N0} visible entries to {path}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "Could not export visible live-log entries to '{Path}'.", path);
            DebugLogStatusText.Text = $"Could not export logs: {ex.Message}";
        }
    }

    private void RefreshDebugLogTail()
    {
        if (_debugLogReader is null)
            return;

        try
        {
            // The normal sink flushes every two seconds. While the developer panel is open, flush once
            // per panel tick so the view behaves as a live tail. Both writer and reader use shared access.
            LogService.Instance.Flush();
            LogTailReadBatch batch = _debugLogReader.ReadNew();
            if (batch.Reset)
                _debugLogEntries.Clear();
            if (batch.Entries.Count > 0)
                _debugLogEntries.AddRange(batch.Entries);

            RefreshDebugLogCategories();
            ApplyDebugLogFilters();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLogStatusText.Text = $"Could not read log: {ex.Message}";
        }
    }

    private void RefreshDebugLogCategories()
    {
        string selected = DebugLogCategoryFilter.SelectedItem as string ?? AllLogCategories;
        string[] categories = _debugLogEntries
            .Select(static entry => entry.Category)
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool unchanged = DebugLogCategoryFilter.Items.Count == categories.Length + 1
            && string.Equals(DebugLogCategoryFilter.Items[0] as string, AllLogCategories, StringComparison.Ordinal)
            && categories.Select((category, index) =>
                    string.Equals(DebugLogCategoryFilter.Items[index + 1] as string, category, StringComparison.Ordinal))
                .All(static match => match);
        if (unchanged)
            return;

        _updatingDebugLogControls = true;
        try
        {
            DebugLogCategoryFilter.Items.Clear();
            DebugLogCategoryFilter.Items.Add(AllLogCategories);
            foreach (string category in categories)
                DebugLogCategoryFilter.Items.Add(category);
            DebugLogCategoryFilter.SelectedItem = categories.Contains(selected, StringComparer.OrdinalIgnoreCase)
                ? categories.First(category => string.Equals(category, selected, StringComparison.OrdinalIgnoreCase))
                : AllLogCategories;
        }
        finally
        {
            _updatingDebugLogControls = false;
        }
    }

    private void OnDebugLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingDebugLogControls)
            ApplyDebugLogFilters();
    }

    private void OnDebugLogTextFilterChanged(object sender, TextChangedEventArgs e) => ApplyDebugLogFilters();

    private void OnDebugLogFollowTailChanged(object sender, RoutedEventArgs e)
    {
        if (DebugLogFollowTail.IsChecked == true)
            ScrollDebugLogToEnd();
    }

    private void ApplyDebugLogFilters()
    {
        if (DebugLogList is null || DebugLogStatusText is null)
            return;

        string? category = DebugLogCategoryFilter.SelectedIndex > 0
            ? DebugLogCategoryFilter.SelectedItem as string
            : null;
        YaguLogLevel? severity = DebugLogSeverityFilter.SelectedIndex > 0
            ? (YaguLogLevel)(DebugLogSeverityFilter.SelectedIndex - 1)
            : null;
        DateTimeOffset? since = DebugLogSinceFilter.SelectedIndex switch
        {
            1 => DateTimeOffset.UtcNow.AddMinutes(-1),
            2 => DateTimeOffset.UtcNow.AddMinutes(-5),
            3 => DateTimeOffset.UtcNow.AddMinutes(-15),
            4 => DateTimeOffset.UtcNow.AddHours(-1),
            _ => null,
        };

        IReadOnlyList<LogTailEntry> filtered = LogTailFilter.Apply(
            _debugLogEntries,
            category,
            severity,
            since,
            DebugLogTextFilter.Text);
        bool visibleEntriesChanged = SynchronizeDebugLogVisibleEntries(filtered);
        DebugLogExportButton.IsEnabled = filtered.Count > 0;
        DebugLogStatusText.Text =
            $"{filtered.Count:N0} of {_debugLogEntries.Count:N0} entries  |  {_debugLogReader?.LogPath}";

        if (visibleEntriesChanged && DebugLogFollowTail.IsChecked == true && filtered.Count > 0)
            DebugLogList.ScrollIntoView(filtered[^1], ScrollIntoViewAlignment.Leading);
    }

    private bool SynchronizeDebugLogVisibleEntries(IReadOnlyList<LogTailEntry> filtered)
    {
        int commonPrefix = 0;
        int commonLimit = Math.Min(_debugLogVisibleEntries.Count, filtered.Count);
        while (commonPrefix < commonLimit
            && ReferenceEquals(_debugLogVisibleEntries[commonPrefix], filtered[commonPrefix]))
        {
            commonPrefix++;
        }

        if (commonPrefix == _debugLogVisibleEntries.Count && commonPrefix == filtered.Count)
            return false;

        while (_debugLogVisibleEntries.Count > commonPrefix)
            _debugLogVisibleEntries.RemoveAt(_debugLogVisibleEntries.Count - 1);
        for (int index = commonPrefix; index < filtered.Count; index++)
            _debugLogVisibleEntries.Add(filtered[index]);
        return true;
    }

    private void ScrollDebugLogToEnd()
    {
        if (DebugLogList.ItemsSource is IReadOnlyList<LogTailEntry> { Count: > 0 } entries)
            DebugLogList.ScrollIntoView(entries[^1], ScrollIntoViewAlignment.Leading);
    }

    private async void OnDebugLogFileLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDebugLogControls || DebugLogFileLevel.SelectedIndex < 0)
            return;

        _updatingDebugLogControls = true;
        try
        {
            ViewModel.FileLogLevelIndex = DebugLogFileLevel.SelectedIndex - 1;
            int effective = (int)LogService.Instance.FileLevel;
            if (ViewModel.FileLogLevelIndex != effective)
                ViewModel.FileLogLevelIndex = effective;
            DebugLogFileLevel.SelectedIndex = effective + 1;
        }
        finally
        {
            _updatingDebugLogControls = false;
        }
        await ViewModel.PersistSettingsAsync();
        RefreshDebugLogTail();
    }

    private async void OnDebugLogConsoleLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDebugLogControls || DebugLogConsoleLevel.SelectedIndex < 0)
            return;

        _updatingDebugLogControls = true;
        try
        {
            ViewModel.ConsoleLogLevelIndex = DebugLogConsoleLevel.SelectedIndex - 1;
            DebugLogConsoleLevel.SelectedIndex = (int)LogService.Instance.ConsoleLevel + 1;
        }
        finally
        {
            _updatingDebugLogControls = false;
        }
        await ViewModel.PersistSettingsAsync();
        RefreshDebugLogTail();
    }

    private void DisposeDebugLogPanel()
    {
        if (_debugLogTimer is not null)
        {
            _debugLogTimer.Stop();
            _debugLogTimer.Tick -= OnDebugLogTimerTick;
            _debugLogTimer = null;
        }
        _debugLogReader = null;
        _debugLogEntries.Clear();
        _debugLogVisibleEntries.Clear();
    }
}

/// <summary>Maps log levels to conventional, theme-aware severity colors.</summary>
public sealed partial class DebugLogSeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string resourceKey = value is YaguLogLevel level
            ? level switch
            {
            YaguLogLevel.Critical => "SystemFillColorCriticalBrush",
            YaguLogLevel.Warning => "SystemFillColorCautionBrush",
            YaguLogLevel.Info => "AccentTextFillColorPrimaryBrush",
                _ => "TextFillColorSecondaryBrush",
            }
            : "TextFillColorSecondaryBrush";

        return Application.Current.Resources.TryGetValue(resourceKey, out object resource)
            && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}