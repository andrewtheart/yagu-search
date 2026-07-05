using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace Yagu;

/// <summary>
/// Window-level keyboard routing for find/replace, go-to-line, and match navigation.
/// </summary>
public sealed partial class MainWindow
{
    // ── Find / Replace bar ─────────────────────────────────────────────

    private int _findIndex = -1; // last match start index in the editor text
    private RichTextBlock? _findHighlightBlock; // block with active find highlight

    private void InitializeHelpKeyboardShortcut()
    {
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var helpAccelerator = new KeyboardAccelerator { Key = VirtualKey.F1 };
        helpAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            OpenHelpWindow();
        };
        RootGrid.KeyboardAccelerators.Add(helpAccelerator);
    }

    private void OnRootGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.F1)
        {
            e.Handled = true;
            OpenHelpWindow();
        }
        else if (e.Key == Windows.System.VirtualKey.F && ctrl)
        {
            e.Handled = true;
            OpenFindBar(showReplace: false);
        }
        else if (e.Key == Windows.System.VirtualKey.H && ctrl)
        {
            e.Handled = true;
            OpenFindBar(showReplace: true);
        }
        else if (e.Key == Windows.System.VirtualKey.C && ctrl
            && TryCopyActivePreviewCustomSelection(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.A && ctrl
            && TrySelectAllPreviewContent(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.G && ctrl && !shift)
        {
            var src = e.OriginalSource as DependencyObject;
            if (PreviewEditor.Visibility == Visibility.Visible
                && (src is null || IsElementWithin(src, PreviewEditor)))
            {
                e.Handled = true;
                _ = ShowGoToLineDialogForEditorAsync();
            }
            else if (HasNavigablePreviewSurfaceForGoToLine())
            {
                e.Handled = true;
                _ = ShowGoToLineDialogForPreviewAsync();
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Enter
            && !ctrl
            && TryHandlePreviewMatchEnter(e.OriginalSource as DependencyObject, shift))
        {
            e.Handled = true;
        }
    }

    private bool TryHandlePreviewMatchEnter(DependencyObject? source, bool shift)
    {
        if (source is null)
            return false;
        if (!HasNavigablePreviewMatchSurface())
            return false;
        if (!IsElementWithin(source, PreviewScrollViewer))
            return false;
        if (IsPreviewEnterReservedByFocusedControl(source))
            return false;

        if (shift)
            OnPrevMatch(PrevMatchButton, new RoutedEventArgs());
        else
            OnNextMatch(NextMatchButton, new RoutedEventArgs());
        return true;
    }

    private bool HasNavigablePreviewMatchSurface()
    {
        if (PreviewPanelBorder.Visibility != Visibility.Visible)
            return false;
        if (PreviewScrollViewer.Visibility != Visibility.Visible)
            return false;
        if (MatchNavPanel.Visibility != Visibility.Visible)
            return false;
        if (_matchParagraphs.Count + _lazyMatchCount <= 0)
            return false;

        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
            return PreviewSectionsPanel.Children.OfType<Expander>().Any(expander => expander.IsExpanded);

        return PreviewBlock.Visibility == Visibility.Visible && PreviewBlock.Blocks.Count > 0;
    }

    private static bool IsElementWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static bool IsPreviewEnterReservedByFocusedControl(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox
                or PasswordBox
                or RichEditBox
                or AutoSuggestBox
                or NumberBox
                or ComboBox
                or CalendarDatePicker
                or DatePicker
                or TimePicker
                or ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
