using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
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

        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock
        {
            Text = "Some paths are not accessible by non-administrative processes. Currently, Yagu is configured to skip the following administrator-only paths while running in non-administrator mode:",
            TextWrapping = TextWrapping.Wrap,
        });

        // Use a ScrollViewer + StackPanel of TextBlocks instead of a multiline TextBox.
        // WinUI TextBox programmatic Text with multiple newlines is fiddly; a list of
        // TextBlocks is simpler and renders reliably.
        var listPanel = new StackPanel { Spacing = 2 };
        foreach (var seg in segments)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = seg,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                IsTextSelectionEnabled = true,
            });
        }
        var scroller = new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 220,
            Padding = new Thickness(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlElevationBorderBrush"],
            CornerRadius = new CornerRadius(4),
        };
        sp.Children.Add(scroller);

        sp.Children.Add(new TextBlock
        {
            Text = "This list is not exhaustive, and some other protected paths may be inaccessible and fail during search.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "To modify this list, please go to the Settings page (click the gear on the top right of the app).",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var dlg = new ContentDialog
        {
            Title = "Admin-protected paths",
            Content = sp,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private async void OnObeyGitignoreToggled(object sender, RoutedEventArgs e)
    {
        // Only show the dialog when the toggle is being turned on (not during initial load).
        if (!_isLoaded) return;
        if (sender is ToggleSwitch ts && !ts.IsOn) return;

        var dialog = new ContentDialog
        {
            Title = ".gitignore precedence",
            Content = "Should .gitignore exclusions take precedence over your Include filter?\n\n" +
                      "Yes — files excluded by .gitignore will be skipped even if they match your Include filter.\n\n" +
                      "No — your Include filter takes priority; matching files will be searched even if .gitignore would exclude them.",
            PrimaryButtonText = "Yes, .gitignore wins",
            SecondaryButtonText = "No, Include filter wins",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        ViewModel.GitignoreTakesPrecedence = result != ContentDialogResult.Secondary;
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

        _settingsWindow = new SettingsWindow(ViewModel, _hotkeyService, _hwnd, ApplyWordWrap, ApplyPreviewSectionBackgrounds, OpenHelpWindow);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
        _settingsWindow.BringInFrontOfMainWindow();
        if (tabIndex.HasValue)
            _settingsWindow.SelectTab(tabIndex.Value);
    }

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
        settingsLink.Click += (_, _) =>
        {
            OpenSettingsTab(SettingsPerformanceTabIndex);
        };
        secondBlock.Inlines.Add(settingsLink);
        secondBlock.Inlines.Add(new Run { Text = "." });
        contentPanel.Children.Add(secondBlock);

        var dialog = new ContentDialog
        {
            Title = "HDD detected — parallelism limited",
            Content = contentPanel,
            PrimaryButtonText = "Continue search",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            Resources =
            {
                ["ContentDialogMaxHeight"] = double.PositiveInfinity,
            },
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        _helpWindow = new HelpWindow(_hwnd, helpPath);
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
