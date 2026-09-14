using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Yagu;

/// <summary>
/// Lets a disabled control explain itself on hover.
/// </summary>
/// <remarks>
/// A disabled WinUI control receives no pointer input, so a tooltip set on the control itself never
/// appears — the one state where the user most needs the explanation is the one state that cannot show
/// it. The tooltip therefore lives on an always-enabled wrapper placed around the control.
/// </remarks>
internal static class DisabledReasonTooltip
{
    /// <summary>Puts <paramref name="control"/> inside an enabled host that can carry a tooltip.</summary>
    public static Grid Host(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);

        var host = new Grid { HorizontalAlignment = control.HorizontalAlignment };
        host.Children.Add(control);
        return host;
    }

    /// <summary>Shows <paramref name="reason"/> on hover while <paramref name="control"/> is disabled.</summary>
    public static void Set(Grid host, Control control, string? reason)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(control);

        ToolTipService.SetToolTip(
            host,
            control.IsEnabled || string.IsNullOrWhiteSpace(reason) ? null : reason);
    }

    /// <summary>Hosts a control built once per refresh, carrying its disabled explanation.</summary>
    public static FrameworkElement Wrap(Control control, string? reason)
    {
        Grid host = Host(control);
        Set(host, control, reason);
        return host;
    }
}
