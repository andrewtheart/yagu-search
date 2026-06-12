using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Yagu.Services;

public enum AppThemeMode
{
    Auto = 0,
    Dark = 1,
    Light = 2,
}

public static class AppThemeService
{
    /// <summary>
    /// The current app-wide theme mode index (0=Auto, 1=Dark, 2=Light).
    /// Set by the main window when the theme changes; read by dialogs to match the theme.
    /// </summary>
    public static int CurrentThemeModeIndex { get; set; }

    public static int NormalizeThemeModeIndex(int value) => value is >= 0 and <= 2 ? value : 0;

    public static ElementTheme ToElementTheme(int themeModeIndex)
        => NormalizeThemeModeIndex(themeModeIndex) switch
        {
            (int)AppThemeMode.Dark => ElementTheme.Dark,
            (int)AppThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

    public static void ApplyRequestedTheme(FrameworkElement root, int themeModeIndex)
    {
        root.RequestedTheme = ToElementTheme(themeModeIndex);
    }

    public static ElementTheme ResolveEffectiveTheme(FrameworkElement root, int themeModeIndex)
    {
        var requestedTheme = ToElementTheme(themeModeIndex);
        return requestedTheme == ElementTheme.Default ? root.ActualTheme : requestedTheme;
    }

    public static void ApplyTitleBarButtonTheme(AppWindow appWindow, ElementTheme actualTheme)
    {
        var titleBar = appWindow.TitleBar;
        bool dark = actualTheme == ElementTheme.Dark;

        if (dark)
        {
            var background = ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20);
            var inactiveForeground = ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);

            titleBar.BackgroundColor = background;
            titleBar.InactiveBackgroundColor = background;
            titleBar.ForegroundColor = Colors.White;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Colors.White;
            return;
        }

        var lightBackground = ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        var lightForeground = Colors.Black;
        var lightInactiveForeground = Colors.Black;

        titleBar.BackgroundColor = lightBackground;
        titleBar.InactiveBackgroundColor = lightBackground;
        titleBar.ForegroundColor = lightForeground;
        titleBar.InactiveForegroundColor = lightInactiveForeground;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = lightForeground;
        titleBar.ButtonInactiveForegroundColor = lightInactiveForeground;
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0xFF, 0xE5, 0xE5, 0xE5);
        titleBar.ButtonHoverForegroundColor = lightForeground;
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0xFF, 0xDA, 0xDA, 0xDA);
        titleBar.ButtonPressedForegroundColor = lightForeground;
    }
}