using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Services;
namespace Yagu;

/// <summary>
/// Title-bar inset calculations and native caption button theming.
/// </summary>
public sealed partial class MainWindow
{
    // Standard Windows caption-button strip width (3 buttons) in DIPs. Used as a transient fallback
    // when the system hasn't reported its real RightInset yet, matching the XAML design-time padding.
    private const double FallbackCaptionButtonsWidthDip = 148;

    // Bounds the self-correcting re-runs of UpdateTitleBarInsets so a host that keeps native caption
    // buttons but never reports a RightInset (e.g. Windows Sandbox) can't spin the dispatcher forever.
    private int _titleBarInsetRetries;

    private void UpdateTitleBarInsets()
    {
        try
        {
            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            if (scale <= 0) scale = 1.0;

            // RightInset is the width (in physical pixels) the system reserved for ITS OWN caption
            // buttons (minimize/maximize/close). It automatically shrinks in window modes that hide the
            // maximize and/or minimize buttons, and is 0 when the system draws no caption buttons at all
            // — so we trust it instead of assuming a fixed three-button (148 dip) width, which was wrong
            // whenever min/max are hidden.
            double reservedDip = 0;
            var titleBar = AppWindow?.TitleBar;
            if (titleBar is not null)
                reservedDip = titleBar.RightInset / scale;
            if (reservedDip < 0) reservedDip = 0;

            // The system is drawing its own caption buttons exactly when it reserved space for them.
            // Fall back to the requested intent so a transient RightInset == 0 during startup doesn't
            // momentarily reveal the custom close in a mode that does have native buttons.
            bool systemCaptionButtons = reservedDip > 0 || _nativeCaptionButtonsVisible;

            // Show the custom close ONLY when the system draws none of its own — otherwise we duplicate
            // the system close (the "two X" bug). This holds even when a mode asked to hide the native
            // buttons but the host (e.g. Windows Sandbox) keeps showing them anyway.
            CloseWindowButton.Visibility = systemCaptionButtons ? Visibility.Collapsed : Visibility.Visible;

            // Sit the custom action buttons immediately to the left of whatever the system actually drew
            // (reservedDip), with a small edge gap when there are no native buttons. When native buttons
            // ARE expected but the system hasn't reported RightInset yet (a transient 0 during startup or
            // a mode/DPI switch), fall back to the standard caption-button width so the icons never land
            // under the caption buttons — then re-run once the real inset is available to settle exactly.
            double rightDip;
            if (reservedDip > 0)
            {
                rightDip = reservedDip;
                _titleBarInsetRetries = 0;
            }
            else if (systemCaptionButtons)
            {
                rightDip = FallbackCaptionButtonsWidthDip;
                ScheduleTitleBarInsetRetry();
            }
            else
            {
                rightDip = 8;
                _titleBarInsetRetries = 0;
            }

            TitleBarActions.Margin = new Thickness(0, 0, rightDip, 0);

            double actionWidth = TitleBarActions.ActualWidth;
            if (actionWidth <= 0)
                actionWidth = 120;
            AppTitleBar.Padding = new Thickness(16, 0, rightDip + actionWidth + 16, 0);
        }
        catch { /* AppWindow not always available; ignore */ }
    }

    // Re-run inset calculation on the next idle tick. RightInset is frequently 0 on the first pass
    // after construction or a presenter/DPI change and only becomes valid once the system finishes
    // laying out its caption buttons; this lets the action icons snap to the exact position without a
    // visible resize or mode switch. Capped so it can't loop indefinitely.
    private void ScheduleTitleBarInsetRetry()
    {
        if (_titleBarInsetRetries >= 10) return;
        _titleBarInsetRetries++;
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            UpdateTitleBarInsets);
    }

    private void SetNativeCaptionButtonsVisible(bool visible)
    {
        _nativeCaptionButtonsVisible = visible;
        // Custom close visibility and the action-bar insets are derived from the system's reserved
        // caption-button width (RightInset) inside UpdateTitleBarInsets, so they stay correct even when
        // a host keeps the native buttons despite this requested state.
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
            case Control control:
                control.Foreground = foreground;
                break;
            case TextBlock textBlock:
                textBlock.Foreground = foreground;
                break;
            case FontIcon icon:
                icon.Foreground = foreground;
                break;
            case IconElement iconElement:
                iconElement.Foreground = foreground;
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
