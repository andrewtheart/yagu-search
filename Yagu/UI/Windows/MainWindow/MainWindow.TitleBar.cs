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
        try
        {
            var titleBar = AppWindow?.TitleBar;
            if (titleBar is null) return;

            var darkTitleBar = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20);
            titleBar.BackgroundColor = darkTitleBar;
            titleBar.InactiveBackgroundColor = darkTitleBar;
            titleBar.ForegroundColor = Microsoft.UI.Colors.White;
            titleBar.InactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        }
        catch { }
    }
}
