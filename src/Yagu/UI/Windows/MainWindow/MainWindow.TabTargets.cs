using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Yagu.Helpers;
using Yagu.ViewModels;

namespace Yagu;

/// <summary>
/// The one-time "where should Tab go?" callout for the directory and search-pattern boxes. Each box has
/// controls overlaid at its trailing edge, so Tab lands on one of those rather than on the next major
/// control; the callout asks once which the user prefers and remembers the answer.
///
/// The named control is resolved at prompt time from the first usable child of the box's inline-control
/// panel, so inserting a new control at the head of that panel re-points both the prompt text and the
/// destination without any change here.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record SearchInputTabRoute(
        SearchInputTabScope Scope,
        AutoSuggestBox Source,
        Panel InlineControls,
        string SkipTargetLabel);

    private SearchInputTabRoute? ResolveSearchInputTabRoute(object sender) => sender switch
    {
        _ when ReferenceEquals(sender, DirectoryBox) => new SearchInputTabRoute(
            SearchInputTabScope.Directory, DirectoryBox, DirectoryInlineControls, "the search pattern box"),
        _ when ReferenceEquals(sender, QueryBox) => new SearchInputTabRoute(
            SearchInputTabScope.SearchPattern, QueryBox, InlineSearchToggles, "the Search button"),
        _ => null,
    };

    /// <summary>The control a "skip past the inline controls" answer jumps to.</summary>
    private FrameworkElement? ResolveTabSkipTarget(SearchInputTabScope scope)
    {
        if (scope == SearchInputTabScope.Directory)
            return QueryBox;

        // Only one of the two search actions is ever visible (SplitButton while idle, plain button while running).
        return SearchSplitButton.Visibility == Visibility.Visible ? SearchSplitButton : SearchCancelButton;
    }

    private void OnSearchInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || e.Handled)
            return;

        if (IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
            return;

        SearchInputTabRoute? route = ResolveSearchInputTabRoute(sender);
        if (route is null || route.Source.IsSuggestionListOpen)
            return;

        FrameworkElement? inlineTarget = FirstTabbableDescendant(route.InlineControls);
        FrameworkElement? skipTarget = ResolveTabSkipTarget(route.Scope);
        if (inlineTarget is null || skipTarget is null)
            return; // Nothing to choose between (e.g. the toggles are hidden in semantic mode) — plain Tab.

        if (ViewModel.HasPromptedTabTarget(route.Scope))
        {
            e.Handled = true;
            MoveFocusTo(ViewModel.TabSkipsInlineControls(route.Scope) ? skipTarget : inlineTarget);
            return;
        }

        if (TabTargetTeachingTip.IsOpen || YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        e.Handled = true;
        ShowTabTargetPrompt(route, inlineTarget, skipTarget);
    }

    private void ShowTabTargetPrompt(SearchInputTabRoute route, FrameworkElement inlineTarget, FrameworkElement skipTarget)
    {
        string inlineLabel = DescribeTabTarget(inlineTarget);

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"Tab from here can move to \u201c{inlineLabel}\u201d, which sits inside this box, "
                   + $"or skip past it to {route.SkipTargetLabel}.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(BuildTabTargetChoiceButton(
            $"Go to \u201c{inlineLabel}\u201d", route.Scope, skipInlineControls: false, inlineTarget));
        body.Children.Add(BuildTabTargetChoiceButton(
            $"Skip to {route.SkipTargetLabel}", route.Scope, skipInlineControls: true, skipTarget));
        body.Children.Add(new TextBlock
        {
            Text = "Yagu remembers your choice. Reset it in Settings \u25B8 Reminders and Warnings.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        TabTargetTeachingTip.Target = route.Source;
        TabTargetTeachingTip.Content = body;
        TabTargetTeachingTip.IsOpen = true;
    }

    private Button BuildTabTargetChoiceButton(
        string content, SearchInputTabScope scope, bool skipInlineControls, FrameworkElement focusTarget)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) =>
        {
            TabTargetTeachingTip.IsOpen = false;
            _ = ViewModel.RecordTabTargetChoiceAsync(scope, skipInlineControls);
            MoveFocusTo(focusTarget);
        };
        return button;
    }

    /// <summary>Focus moves are deferred so they run after the callout has finished closing and released focus.</summary>
    private void MoveFocusTo(FrameworkElement target)
        => DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Keyboard));

    /// <summary>
    /// The first control inside <paramref name="panel"/> a Tab press could land on. Walks nested panels so
    /// a future control can be grouped without breaking the lookup.
    /// </summary>
    private static FrameworkElement? FirstTabbableDescendant(Panel panel)
    {
        if (panel.Visibility != Visibility.Visible)
            return null;

        foreach (UIElement child in panel.Children)
        {
            if (child is Panel nested)
            {
                if (FirstTabbableDescendant(nested) is { } found)
                    return found;
                continue;
            }

            if (child is Control { Visibility: Visibility.Visible, IsEnabled: true, IsTabStop: true } control)
                return control;
        }

        return null;
    }

    private static string DescribeTabTarget(FrameworkElement element)
        => TabTargetLabel.For(
            AutomationProperties.GetName(element),
            ToolTipService.GetToolTip(element) switch
            {
                string text => text,
                ToolTip { Content: string text } => text,
                _ => null,
            },
            element.Name);

    private static bool IsKeyDown(VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
