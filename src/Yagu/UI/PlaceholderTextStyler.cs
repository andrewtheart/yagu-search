using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Yagu.UI;

/// <summary>
/// Attached properties that restyle a text-entry control's <c>PlaceholderText</c> (the greyed-out
/// "example" hint) without touching the user's typed text. WinUI controls expose the hint through a
/// template part named <c>PlaceholderTextContentPresenter</c>; this helper finds that part and
/// adjusts it. Works for <see cref="TextBox"/>, <see cref="AutoSuggestBox"/>, and
/// <see cref="NumberBox"/> (the latter two host an inner <see cref="TextBox"/> that carries the
/// same template part).
/// </summary>
public static class PlaceholderTextStyler
{
    public static readonly DependencyProperty ItalicProperty =
        DependencyProperty.RegisterAttached(
            "Italic",
            typeof(bool),
            typeof(PlaceholderTextStyler),
            new PropertyMetadata(false, OnStyleChanged));

    /// <summary>Lets a long hint fill a multi-line box instead of being clipped. The stock template
    /// gives the hint no TextWrapping, so it stays on one line however tall the box is.</summary>
    public static readonly DependencyProperty WrapProperty =
        DependencyProperty.RegisterAttached(
            "Wrap",
            typeof(bool),
            typeof(PlaceholderTextStyler),
            new PropertyMetadata(false, OnStyleChanged));

    public static bool GetItalic(DependencyObject obj) => (bool)obj.GetValue(ItalicProperty);

    public static void SetItalic(DependencyObject obj, bool value) => obj.SetValue(ItalicProperty, value);

    public static bool GetWrap(DependencyObject obj) => (bool)obj.GetValue(WrapProperty);

    public static void SetWrap(DependencyObject obj, bool value) => obj.SetValue(WrapProperty, value);

    private static DataTemplate? _wrappedPlaceholder;

    // The hint arrives as plain string Content, so wrapping it takes a template. The TextBlock
    // inherits the presenter's placeholder foreground and italic style, and re-binds itself
    // whenever the hint changes (Traditional <-> Semantic mode).
    private static DataTemplate WrappedPlaceholder => _wrappedPlaceholder ??= (DataTemplate)XamlReader.Load(
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
        + "<TextBlock Text=\"{Binding}\" TextWrapping=\"Wrap\" /></DataTemplate>");

    private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.Loaded -= OnLoaded;
        if (GetItalic(fe) || GetWrap(fe))
        {
            fe.Loaded += OnLoaded;
            if (fe.IsLoaded)
                Apply(fe);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            Apply(fe);
    }

    private static void Apply(FrameworkElement root)
    {
        if (TryApply(root))
            return;

        // The template part may not be realized on the first Loaded pass (e.g. the inner TextBox
        // of an AutoSuggestBox/NumberBox). Retry once after the current layout cycle.
        root.DispatcherQueue?.TryEnqueue(() => TryApply(root));
    }

    private static bool TryApply(FrameworkElement root)
    {
        var presenter = FindByName(root, "PlaceholderTextContentPresenter");
        if (presenter is null)
            return false;

        bool italic = GetItalic(root);
        bool wrap = GetWrap(root);
        switch (presenter)
        {
            // Checked ahead of Control: the stock template part is a ContentControl.
            case ContentControl content:
                if (italic)
                    content.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (wrap)
                {
                    // Left alignment would measure the templated TextBlock unbounded, so it would
                    // still never wrap.
                    content.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    content.ContentTemplate = WrappedPlaceholder;
                }

                return true;
            case Control control:
                if (italic)
                    control.FontStyle = Windows.UI.Text.FontStyle.Italic;
                return true;
            case TextBlock textBlock:
                if (italic)
                    textBlock.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (wrap)
                    textBlock.TextWrapping = TextWrapping.Wrap;
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
