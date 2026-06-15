using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;
namespace Yagu;

/// <summary>
/// Admin notices, settings/help windows, HDD warning prompts, and drag/drop.
/// </summary>
public sealed partial class MainWindow
{
    private const int SettingsPerformanceTabIndex = 2;
    private const int SettingsDisplayTabIndex = 3;

    private async void OnAdminLearnMore(object sender, RoutedEventArgs e)
    {
        var segments = Yagu.Services.FileLister.ParseAdminProtectedSegments(ViewModel.AdminProtectedPathSegments);
        if (segments.Count == 0) segments.AddRange(Yagu.Services.FileLister.DefaultAdminProtectedPathSegments);
        await AdminProtectedPathsDialog.ShowAsync(_hwnd, segments);
    }

    private async void OnObeyGitignoreToggled(object sender, RoutedEventArgs e)
    {
        // Only show the dialog when the toggle is being turned on (not during initial load).
        if (!_isLoaded) return;
        if (sender is ToggleSwitch ts && !ts.IsOn) return;

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = ".gitignore precedence",
                Content = "Should .gitignore exclusions take precedence over your Include filter?\n\n" +
                          "Yes - files excluded by .gitignore will be skipped even if they match your Include filter.\n\n" +
                          "No - your Include filter takes priority; matching files will be searched even if .gitignore would exclude them.",
                PrimaryButtonText = "Yes, .gitignore wins",
                SecondaryButtonText = "No, Include filter wins",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 620,
                Height = 330,
            });
        ViewModel.GitignoreTakesPrecedence = result != YaguDialogResult.Secondary;
    }

    private async Task PickFolderAsync(Windows.Storage.Pickers.FolderPicker picker)
    {
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.Directory = folder.Path;
    }

    private SettingsWindow? _settingsWindow;

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsTab();
    }

    private void OpenSettingsTab(int? tabIndex = null)
    {
        // If a settings window is already open, activate it instead of creating a new one.
        if (_settingsWindow is not null)
        {
            try
            {
                _settingsWindow.Activate();
                _settingsWindow.BringInFrontOfMainWindow();
                if (tabIndex.HasValue)
                    _settingsWindow.SelectTab(tabIndex.Value);
                return;
            }
            catch { _settingsWindow = null; }
        }

        _settingsWindow = new SettingsWindow(ViewModel, _hotkeyService, _hwnd, ApplyWordWrap, ApplyPreviewSectionBackgrounds, OpenHelpWindow, SuppressLauncherHideToTrayForOwnedWindowClose);
        _settingsWindow.Closed += (_, _) =>
        {
            SuppressLauncherHideToTrayForOwnedWindowClose();
            _settingsWindow = null;
            Activate();
        };
        _settingsWindow.Activate();
        _settingsWindow.BringInFrontOfMainWindow();
        if (tabIndex.HasValue)
            _settingsWindow.SelectTab(tabIndex.Value);
    }

    /// <summary>
    /// Tracks which disks have already shown the HDD parallelism warning this session.
    /// Keyed by drive root (e.g. "C:\"). Reset only when the app restarts so the dialog
    /// shows at most once per disk per Yagu session.
    /// </summary>
    private readonly HashSet<string> _hddWarningShownDrives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the search directory is on a rotational HDD and, if LimitParallelismOnHdd is enabled,
    /// forces parallelism to 1 and warns the user. Returns false if the user cancels.
    /// </summary>
    private async Task<bool> CheckHddAndWarnAsync()
    {
        if (!ViewModel.LimitParallelismOnHdd) return true;
        if (string.IsNullOrWhiteSpace(ViewModel.Directory)) return true;
        if (!Helpers.DiskTypeDetector.IsHardDisk(ViewModel.Directory)) return true;

        // Force parallelism to 1 (sequential)
        ViewModel.ParallelismIndex = 1;

        // Only warn once per disk per session; subsequent searches on the same disk
        // still limit parallelism but skip the dialog until the app is restarted.
        var driveKey = GetHddWarningDriveKey(ViewModel.Directory);
        if (!_hddWarningShownDrives.Add(driveKey)) return true;

        var contentPanel = new StackPanel { Spacing = 8, MinWidth = 360 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = "The selected search directory is on a rotational hard disk (HDD). " +
                   "Parallelism has been set to 1 thread to avoid excessive disk thrashing.",
            TextWrapping = TextWrapping.Wrap,
        });

        var secondBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };
        secondBlock.Inlines.Add(new Run { Text = "You can increase parallelism or disable this warning in " });
        var settingsLink = new Hyperlink();
        settingsLink.Inlines.Add(new Run { Text = "Settings \u2192 Performance" });
        var openSettingsRequested = false;
        YaguDialog? hddDialog = null;
        settingsLink.Click += (_, _) =>
        {
            // The dialog is modal (the owner window is disabled while it is shown), so opening
            // the Settings window now would leave it behind the disabled owner. Close the dialog
            // first, then open Settings -> Performance after it has fully dismissed.
            openSettingsRequested = true;
            hddDialog?.AcceptClose();
        };
        secondBlock.Inlines.Add(settingsLink);
        secondBlock.Inlines.Add(new Run { Text = "." });
        contentPanel.Children.Add(secondBlock);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "HDD detected - parallelism limited",
                Content = contentPanel,
                PrimaryButtonText = "Continue search",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                ShowTitleBar = false,
                Width = 560,
                Height = 330,
                MaxContentHeight = 220,
            },
            dlg => hddDialog = dlg);

        if (openSettingsRequested)
        {
            OpenSettingsTab(SettingsPerformanceTabIndex);
            return false;
        }

        return result == YaguDialogResult.Primary;
    }

    /// <summary>
    /// Returns a stable per-disk key for the HDD warning dedup set. Uses the path root
    /// (drive letter or UNC share) so repeated searches under the same disk share one key.
    /// </summary>
    private static string GetHddWarningDriveKey(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            if (!string.IsNullOrEmpty(root)) return root.TrimEnd('\\', '/').ToUpperInvariant();
        }
        catch { /* fall through to the raw directory below */ }
        return directory.ToUpperInvariant();
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        if (!_forceClose && ViewModel.CloseToTray)
        {
            HideToTray(isCloseToTray: true);
            return;
        }
        Close();
    }

    private HelpWindow? _helpWindow;

    private void OnOpenCredits(object sender, RoutedEventArgs e)
        => OpenHelpWindow();

    private void OpenHelpWindow()
    {
        if (_helpWindow is not null)
        {
            try
            {
                _helpWindow.Activate();
                _helpWindow.BringInFrontOfMainWindow(_hwnd);
                return;
            }
            catch { _helpWindow = null; }
        }

        var helpPath = Path.Combine(AppContext.BaseDirectory, "HELP.html");
        _helpWindow = new HelpWindow(_hwnd, helpPath, CurrentAppWindowTitle);
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Activate();
        _helpWindow.BringInFrontOfMainWindow(_hwnd);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFolder f)
            {
                ViewModel.Directory = f.Path;
                return;
            }
        }
    }
}
