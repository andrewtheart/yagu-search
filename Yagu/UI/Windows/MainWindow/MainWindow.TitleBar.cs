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
using Microsoft.UI;
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
/// Title-bar inset calculations and native caption button theming.
/// </summary>
public sealed partial class MainWindow
{
    private void UpdateTitleBarInsets()
    {
        try
        {
            double rightDip = 0;
            if (_nativeCaptionButtonsVisible)
            {
                var titleBar = AppWindow?.TitleBar;
                if (titleBar is not null)
                {
                    var scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
                    rightDip = titleBar.RightInset / scale;
                }

                if (rightDip < 48) rightDip = 148;
            }
            else
            {
                rightDip = 8;
            }

            TitleBarActions.Margin = new Thickness(0, 0, rightDip, 0);

            double actionWidth = TitleBarActions.ActualWidth;
            if (actionWidth <= 0)
                actionWidth = _nativeCaptionButtonsVisible ? 80 : 120;
            AppTitleBar.Padding = new Thickness(16, 0, rightDip + actionWidth + 16, 0);
        }
        catch { /* AppWindow not always available; ignore */ }
    }

    private void SetNativeCaptionButtonsVisible(bool visible)
    {
        _nativeCaptionButtonsVisible = visible;
        CloseWindowButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateTitleBarInsets();
    }

    private static Style? TryGetApplicationStyle(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var value) && value is Style style
                ? style
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyTitleBarButtonTheme()
    {
        ElementTheme actualTheme;
        try
        {
            actualTheme = AppThemeService.ResolveEffectiveTheme(RootGrid, ViewModel.ThemeModeIndex);
            AppThemeService.ApplyTitleBarButtonTheme(
                AppWindow,
                actualTheme);
        }
        catch
        {
            actualTheme = ElementTheme.Dark;
        }

        ApplyCustomTitleBarForeground(actualTheme);
    }

    private void ApplyCustomTitleBarForeground(ElementTheme actualTheme)
    {
        var foreground = new SolidColorBrush(actualTheme == ElementTheme.Light ? Colors.Black : Colors.White);
        ApplyCustomTitleBarForeground(AppTitleBar, foreground);
        ApplyCustomTitleBarForeground(TitleBarActions, foreground);
    }

    private static void ApplyCustomTitleBarForeground(DependencyObject root, Brush foreground)
    {
        switch (root)
        {
            case Button button:
                button.Foreground = foreground;
                break;
            case FontIcon icon:
                icon.Foreground = foreground;
                break;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            ApplyCustomTitleBarForeground(VisualTreeHelper.GetChild(root, i), foreground);
        }
    }

    private void ApplyAppTheme()
    {
        AppThemeService.ApplyRequestedTheme(RootGrid, ViewModel.ThemeModeIndex);
        ApplyTitleBarButtonTheme();
    }
}
