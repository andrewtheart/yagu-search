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
        if (!HasUserDefinedIncludeFilterText()) return;

        // If the user previously saved a precedence preference (via "Don't ask again" or the
        // Search Defaults setting), honor it silently instead of prompting again.
        if (ViewModel.GitignorePrecedencePreference is bool saved)
        {
            ViewModel.GitignoreTakesPrecedence = saved;
            return;
        }

        var contentPanel = new StackPanel { Spacing = 12, MinWidth = 360 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = "When a file is both matched by your Include filter and excluded by .gitignore, which one should take precedence?\n\n" +
                   "Yes, .gitignore wins - the file is skipped, even though it matches your Include filter.\n\n" +
                   "No, Include filter wins - the file is searched, even though .gitignore would exclude it.\n\n" +
                   "This choice only applies to files where the two conflict.",
            TextWrapping = TextWrapping.Wrap,
        });
        var dontAskAgain = new CheckBox { Content = "Don't ask again" };
        contentPanel.Children.Add(dontAskAgain);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = ".gitignore vs Include filter",
                Content = contentPanel,
                PrimaryButtonText = "Yes, .gitignore wins",
                SecondaryButtonText = "No, Include filter wins",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                ShowTitleBar = false,
                Width = 620,
                Height = 360,
            });

        bool gitignoreWins = result != YaguDialogResult.Secondary;
        ViewModel.GitignoreTakesPrecedence = gitignoreWins;

        if (dontAskAgain.IsChecked == true)
        {
            ViewModel.GitignorePrecedencePreference = gitignoreWins;
            await ViewModel.PersistSettingsAsync();
        }
    }

    private bool HasUserDefinedIncludeFilterText()
    {
        if (IncludeFilterBox is null)
            return !string.IsNullOrWhiteSpace(ViewModel.IncludeGlobs);

        string text = IncludeFilterBox.Text?.Trim() ?? string.Empty;
        return text.Length > 0 && !IsFilterExampleText(IncludeFilterBox);
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

        // Force parallelism to 1 (sequential) for this session only. This is a temporary override
        // that affects searches in the current session but is NOT written back to the persisted
        // ParallelismIndex setting, so the user's saved preference is preserved across restarts.
        ViewModel.SetSessionParallelismOverride(1);

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
    /// Warns before searching when the query names a file whose extension is currently excluded by an
    /// advanced option (Skip/Binary extensions or an Include/Exclude filter). Returns false to cancel
    /// the search (user chose Cancel). "Include &amp; search" un-excludes the extension, then proceeds.
    /// </summary>
    private async Task<bool> CheckExcludedExtensionAndWarnAsync()
    {
        var warning = ViewModel.TryGetExcludedExtensionWarning();
        if (warning is null) return true;

        string ext = warning.Extension;
        string sources = DescribeExclusionReasons(warning.Reasons);

        var contentPanel = new StackPanel { Spacing = 12, MinWidth = 360 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        message.Inlines.Add(new Run { Text = "You're searching for a file ending in " });
        message.Inlines.Add(new Run { Text = "." + ext, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        message.Inlines.Add(new Run { Text = $", but .{ext} files are currently excluded by your {sources}, so they won't appear in the results." });
        contentPanel.Children.Add(message);

        contentPanel.Children.Add(new TextBlock
        {
            Text = $"Choose \"Include .{ext} & search\" to stop excluding this file type, or search anyway to continue without it.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });

        var dontWarnAgain = new CheckBox { Content = "Don't warn me again about excluded file types" };
        contentPanel.Children.Add(dontWarnAgain);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Excluded file type",
                Content = contentPanel,
                PrimaryButtonText = $"Include .{ext} & search",
                SecondaryButtonText = "Search anyway",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 600,
                Height = 340,
                MaxContentHeight = 230,
            });

        bool dontWarn = dontWarnAgain.IsChecked == true;
        if (dontWarn)
            ViewModel.SuppressExcludedExtensionWarnings = true;

        if (result == YaguDialogResult.Close)
        {
            // Dismissed without choosing (Esc / window close) — cancel the search rather than run one
            // that would not find the file. Still persist the suppress preference if the user set it.
            if (dontWarn) await ViewModel.PersistSettingsAsync();
            return false;
        }

        if (result == YaguDialogResult.Secondary)
        {
            // "Search anyway" — run the search without un-excluding the type.
            if (dontWarn) await ViewModel.PersistSettingsAsync();
            return true;
        }

        // Primary: un-exclude the extension; this persists settings (including the suppress flag if set).
        await ViewModel.IncludeExtensionForSearchAsync(warning);
        return true;
    }

    private static string DescribeExclusionReasons(Yagu.Services.ExtensionExclusionReason reasons)
    {
        var parts = new List<string>();
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.BinaryExtensions)) parts.Add("Binary extensions list");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.SkipExtensions)) parts.Add("Skip extensions list");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.ExcludeFilter)) parts.Add("Exclude filter");
        if (reasons.HasFlag(Yagu.Services.ExtensionExclusionReason.IncludeFilter)) parts.Add("Include filter");
        return parts.Count switch
        {
            0 => "advanced options",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1],
        };
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
            RequestCloseToTray();
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
