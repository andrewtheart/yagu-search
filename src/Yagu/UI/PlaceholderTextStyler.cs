using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Yagu.UI;

/// <summary>
/// Attached property that renders a text-entry control's <c>PlaceholderText</c> (the greyed-out
/// "example" hint) in italic, without italicising the user's typed text. WinUI controls expose
/// the hint through a template part named <c>PlaceholderTextContentPresenter</c>; this helper
/// finds that part and switches its font style. Works for <see cref="TextBox"/>,
/// <see cref="AutoSuggestBox"/>, and <see cref="NumberBox"/> (the latter two host an inner
/// <see cref="TextBox"/> that carries the same template part).
/// </summary>
public static class PlaceholderTextStyler
{
    public static readonly DependencyProperty ItalicProperty =
        DependencyProperty.RegisterAttached(
            "Italic",
            typeof(bool),
            typeof(PlaceholderTextStyler),
            new PropertyMetadata(false, OnItalicChanged));

    public static bool GetItalic(DependencyObject obj) => (bool)obj.GetValue(ItalicProperty);

    public static void SetItalic(DependencyObject obj, bool value) => obj.SetValue(ItalicProperty, value);

    private static void OnItalicChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.Loaded -= OnLoaded;
        if ((bool)e.NewValue)
        {
            fe.Loaded += OnLoaded;
            if (fe.IsLoaded)
                ApplyItalic(fe);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            ApplyItalic(fe);
    }

    private static void ApplyItalic(FrameworkElement root)
    {
        if (TrySetItalic(root))
            return;

        // The template part may not be realized on the first Loaded pass (e.g. the inner TextBox
        // of an AutoSuggestBox/NumberBox). Retry once after the current layout cycle.
        root.DispatcherQueue?.TryEnqueue(() => TrySetItalic(root));
    }

    private static bool TrySetItalic(FrameworkElement root)
    {
        var presenter = FindByName(root, "PlaceholderTextContentPresenter");
        switch (presenter)
        {
            case Control control:
                control.FontStyle = Windows.UI.Text.FontStyle.Italic;
                return true;
            case TextBlock textBlock:
                textBlock.FontStyle = Windows.UI.Text.FontStyle.Italic;
                return true;
            default:
                return false;
        }
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return fe;

            var found = FindByName(child, name);
            if (found is not null)
                return found;
        }

        return null;
    }
}
