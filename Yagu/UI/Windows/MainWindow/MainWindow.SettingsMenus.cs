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
/// Explorer context-menu registration, extension menu commands, and skip-count overlay controls.
/// </summary>
public sealed partial class MainWindow
{
    // ── First-run context menu prompt ──────────────────────────────────
    private const string ContextMenuRegKeyDir = @"Software\Classes\Directory\shell\Yagu";
    private const string ContextMenuRegKeyBg  = @"Software\Classes\Directory\Background\shell\Yagu";
    private const string ContextMenuText = "Search with Yagu";

    private async Task CheckFirstRunContextMenuAsync()
    {
        if (ViewModel.HasCompletedFirstRun)
            return;

        // Mark first run complete regardless of what the user chooses
        ViewModel.HasCompletedFirstRun = true;
        await ViewModel.PersistSettingsAsync();

        // If context menu is already registered, nothing to do
        if (IsContextMenuRegistered())
            return;

        if (await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Add Explorer Context Menu?",
                Content = "Would you like to add a \"Search with Yagu\" option to the Windows Explorer right-click menu?\n\nThis lets you quickly search any folder by right-clicking it.",
                PrimaryButtonText = "Yes, add it",
                CloseButtonText = "No thanks",
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 560,
                Height = 300,
            }) != YaguDialogResult.Primary)
            return;

        try
        {
            RegisterContextMenu();

            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Context Menu Installed",
                    Content = "The \"Search with Yagu\" context menu has been added.\n\nTo use it: right-click any folder in Windows Explorer and select \"Search with Yagu\". Yagu will open with that folder ready to search.",
                    CloseButtonText = "OK",
                    DefaultButton = YaguDialogDefaultButton.Close,
                    Width = 560,
                    Height = 320,
                });
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("ContextMenu", "Failed to register context menu", ex);

            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Context Menu Registration Failed",
                    Content = $"Could not register the context menu entry:\n{ex.Message}",
                    CloseButtonText = "OK",
                    DefaultButton = YaguDialogDefaultButton.Close,
                    Width = 560,
                    Height = 300,
                });
        }
    }

    private static bool IsContextMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ContextMenuRegKeyDir);
        return key != null;
    }

    private static void RegisterContextMenu()
    {
        var exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Yagu.exe");

        foreach (var regPath in new[] { ContextMenuRegKeyDir, ContextMenuRegKeyBg })
        {
            using var shellKey = Registry.CurrentUser.CreateSubKey(regPath);
            shellKey.SetValue(null, ContextMenuText);
            shellKey.SetValue("Icon", exePath);

            using var cmdKey = Registry.CurrentUser.CreateSubKey(regPath + @"\command");
            cmdKey.SetValue(null, $"\"{exePath}\" --dir \"%V\"");
        }
    }

    // ── Skip-extensions dropdown ──────────────────────────────────
    private void OnSkipExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnSkipExtensionToggled();

    private void OnSkipExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.SkipExtensionItems) item.IsEnabled = true;
        ViewModel.OnSkipExtensionToggled();
    }

    private void OnSkipExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.SkipExtensionItems) item.IsEnabled = false;
        ViewModel.OnSkipExtensionToggled();
    }

    // ── Binary-extensions dropdown ───────────────────────────────
    private void OnBinaryExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnBinaryExtensionToggled();

    private void OnBinaryExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.BinaryExtensionItems) item.IsEnabled = true;
        ViewModel.OnBinaryExtensionToggled();
    }

    private void OnBinaryExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.BinaryExtensionItems) item.IsEnabled = false;
        ViewModel.OnBinaryExtensionToggled();
    }

    // ── Archive-extensions dropdown ───────────────────────────────
    private void OnArchiveExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnArchiveExtensionToggled();

    private void OnArchiveExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.ArchiveExtensionItems) item.IsEnabled = true;
        ViewModel.OnArchiveExtensionToggled();
    }

    private void OnArchiveExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.ArchiveExtensionItems) item.IsEnabled = false;
        ViewModel.OnArchiveExtensionToggled();
    }

    // ── Skip-count breakdown overlay ─────────────────────────────
    private bool _resultsPaneCollapsed;
    private int _resultsPaneExpandedWindowHeight;

    private void OnToggleResultsPane(object sender, RoutedEventArgs e)
    {
        _resultsPaneCollapsed = !_resultsPaneCollapsed;

        if (_resultsPaneCollapsed)
        {
            _resultsPaneExpandedWindowHeight = AppWindow?.Size.Height ?? 0;
            SplitPaneRow.Height = new GridLength(0);
            ProgressRow.Height = new GridLength(0);
            SplitPaneGrid.Visibility = Visibility.Collapsed;
        }
        else
        {
            SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
            ProgressRow.Height = GridLength.Auto;
            SplitPaneGrid.Visibility = Visibility.Visible;
        }

        UpdateBottomStatusBarVisibility();

        if (_resultsPaneCollapsed)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitWindowHeightToVisibleContent);
        else
            RestoreResultsPaneExpandedWindowHeight();
    }

    private void FitWindowHeightToVisibleContent()
    {
        try
        {
            if (AppWindow is null) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;

            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            double measureWidthDip = AppWindow.ClientSize.Width > 0
                ? AppWindow.ClientSize.Width / scale
                : Math.Max(1, RootGrid.ActualWidth);

            RootGrid.UpdateLayout();
            RootGrid.Measure(new Windows.Foundation.Size(measureWidthDip, double.PositiveInfinity));

            int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            int desiredHeight = (int)Math.Ceiling((Math.Max(MinimumLauncherHeightDip, RootGrid.DesiredSize.Height) + 2) * scale) + chromeHeight;
            if (desiredHeight >= AppWindow.Size.Height - 4) return;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var wa = displayArea?.WorkArea ?? default;
            int maxHeight = wa.Height > 0 ? Math.Max(0, wa.Y + wa.Height - AppWindow.Position.Y) : desiredHeight;
            if (maxHeight > 0)
                desiredHeight = Math.Min(desiredHeight, maxHeight);

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                desiredHeight));
        }
        catch { }
    }

    private void RestoreResultsPaneExpandedWindowHeight()
    {
        try
        {
            if (AppWindow is null || _resultsPaneExpandedWindowHeight <= 0) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;
            if (AppWindow.Size.Height >= _resultsPaneExpandedWindowHeight - 4) return;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var wa = displayArea?.WorkArea ?? default;
            int restoredHeight = _resultsPaneExpandedWindowHeight;
            if (wa.Height > 0)
                restoredHeight = Math.Min(restoredHeight, Math.Max(0, wa.Y + wa.Height - AppWindow.Position.Y));

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                restoredHeight));
        }
        catch { }
    }

    private void OnSkipCountTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        SkipBreakdownOverlay.Visibility =
            SkipBreakdownOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void OnSkipCountPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            tb.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
    }

    private void OnSkipCountPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            tb.TextDecorations = Windows.UI.Text.TextDecorations.None;
    }
}
