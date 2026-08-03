using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Services;
using Yagu.Services.Logging;

namespace Yagu;

/// <summary>
/// The one-time first-launch "choose your window style" prompt. It shows three stylistic (non-screenshot)
/// mock-ups of how Yagu opens — a compact launcher that hides to the tray, a launcher pinned on top, or a
/// traditional full window — and saves the pick as the default (and applies it to the current session).
/// The chosen mode maps onto the existing <see cref="MainViewModel.StartInLauncherMode"/> +
/// <see cref="MainViewModel.WindowFocusBehavior"/> settings and the in-session <c>_pinState</c> switch. The
/// dialog is a title-bar-less <see cref="YaguDialog"/> and runs in the awaited startup chain per the
/// modal-no-title-bar convention.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<Border> _windowModeCards = new();
    private int _windowModePickIndex;

    // Captured when the picker opens so "Skip" can restore the window after live previews.
    private bool _windowModePreviewActive;
    private bool _windowModePreviewOrigLauncherMode;
    private PinState _windowModePreviewOrigPinState;
    private bool _windowModePreviewOrigStartInLauncher;
    private int _windowModePreviewOrigFocusBehavior;

    /// <summary>
    /// Shown once (tracked by <see cref="AppSettings.HasPromptedWindowMode"/>). On a confirmed pick it
    /// persists the mode and applies it live; skipping keeps the current default. Never throws.
    /// </summary>
    private async Task CheckFirstRunWindowModeAsync()
    {
        if (ViewModel.Settings.HasPromptedWindowMode)
            return;

        // Belt-and-braces: if another owned modal is still up, retry next launch (don't mark shown yet).
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        // Mark shown regardless of the choice so the prompt never nags on later launches.
        ViewModel.Settings.HasPromptedWindowMode = true;

        try
        {
            _windowModePickIndex = 0;
            // Snapshot the current window style so skipping restores it after any live preview.
            CaptureWindowModePreviewBaseline();
            YaguDialogResult result = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Choose your window style",
                    TitleGlyph = "\uE7B8", // browse/preview
                    Content = BuildWindowModePickerContent(),
                    PrimaryButtonText = "Use this style",
                    CloseButtonText = "Skip",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    RequestedTheme = RootGrid.ActualTheme,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    Width = 600,
                    Height = 560,
                    MaxContentHeight = 660,
                });

            if (result == YaguDialogResult.Primary)
                ApplyWindowModeChoice(_windowModePickIndex);
            else if (_windowModePreviewActive)
                RevertWindowModePreview(); // user skipped after previewing — put the window back
        }
        catch (Exception ex)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "First-run window-mode prompt failed.");
        }

        await ViewModel.PersistSettingsAsync();
    }

    /// <summary>Applies a picked window-style card: persists the mode settings and switches the live window.</summary>
    private void ApplyWindowModeChoice(int index)
    {
        switch (index)
        {
            case 1: // Compact launcher (hides to tray)
                ViewModel.StartInLauncherMode = true;
                ViewModel.WindowFocusBehavior = 0;
                _pinState = PinState.MinimizeToTray;
                break;
            case 2: // Launcher, pinned on top
                ViewModel.StartInLauncherMode = true;
                ViewModel.WindowFocusBehavior = 2;
                _pinState = PinState.AlwaysOnTop;
                break;
            default: // 0: Traditional full window
                ViewModel.StartInLauncherMode = false;
                ViewModel.WindowFocusBehavior = 1;
                _pinState = PinState.FullWindow;
                break;
        }

        // For the launcher pin states, if we're coming FROM the traditional full window we must fully
        // enter launcher mode so the results pane (Sort/Group/Filter toolbar) collapses and the window
        // shrinks to compact chrome. ApplyPinState()/RestoreToLauncherChrome() only collapse the pane
        // when _resultsPaneCollapsed is already true, so from traditional it would leave the results
        // toolbar visible. EnterLauncherMode() seeds _pinState from WindowFocusBehavior (set above).
        if (index != 0 && !_launcherMode)
        {
            EnterLauncherMode();
        }
        else
        {
            ApplyPinState();
        }
    }

    /// <summary>Records the live window style (chrome + settings) when the picker opens so a Skip can restore it.</summary>
    private void CaptureWindowModePreviewBaseline()
    {
        _windowModePreviewActive = false;
        _windowModePreviewOrigLauncherMode = _launcherMode;
        _windowModePreviewOrigPinState = _pinState;
        _windowModePreviewOrigStartInLauncher = ViewModel.StartInLauncherMode;
        _windowModePreviewOrigFocusBehavior = ViewModel.WindowFocusBehavior;
    }

    /// <summary>
    /// Selects a card AND switches the live Yagu window to that style as a temporary preview, so the user
    /// can see traditional vs. compact before committing. The pick is only persisted on "Use this style";
    /// "Skip" restores the baseline captured in <see cref="CaptureWindowModePreviewBaseline"/>.
    /// </summary>
    private void PreviewWindowModeCard(int index)
    {
        SelectWindowModeCard(index);
        _windowModePreviewActive = true;
        try
        {
            ApplyWindowModeChoice(index);
        }
        catch (Exception ex)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "Live window-style preview failed.");
        }
    }

    /// <summary>Restores the window style captured when the picker opened (used when the user skips after previewing).</summary>
    private void RevertWindowModePreview()
    {
        try
        {
            ViewModel.StartInLauncherMode = _windowModePreviewOrigStartInLauncher;
            ViewModel.WindowFocusBehavior = _windowModePreviewOrigFocusBehavior;
            if (_windowModePreviewOrigLauncherMode)
            {
                // Baseline was the compact launcher: re-enter launcher chrome if a preview switched to the
                // full window, then restore the exact pin state it had.
                if (!_launcherMode)
                    EnterLauncherMode();
                _pinState = _windowModePreviewOrigPinState;
                ApplyPinState();
            }
            else
            {
                // Baseline was the traditional full window.
                _pinState = PinState.FullWindow;
                ApplyPinState();
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "Reverting the window-style preview failed.");
        }
        finally
        {
            _windowModePreviewActive = false;
        }
    }

    private FrameworkElement BuildWindowModePickerContent()
    {
        var intro = new TextBlock
        {
            Text = "How would you like Yagu to open? You can change this anytime in Settings \u25B8 Window.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 16),
        };

        _windowModeCards.Clear();
        var cards = new StackPanel { Spacing = 10 };
        cards.Children.Add(BuildWindowModeCard(
            0, "Traditional window",
            "A full window with the results list and file preview side by side.",
            BuildTraditionalMockup()));
        cards.Children.Add(BuildWindowModeCard(
            1, "Compact launcher",
            "A small search bar that pops up on your hotkey and tucks into the tray when you click away.",
            BuildLauncherMockup(onTop: false)));
        cards.Children.Add(BuildWindowModeCard(
            2, "Launcher, always on top",
            "The same compact bar, kept pinned above your other windows.",
            BuildLauncherMockup(onTop: true)));

        var root = new StackPanel();
        root.Children.Add(intro);
        root.Children.Add(cards);
        SelectWindowModeCard(0); // Traditional selected by default
        return root;
    }

    private Border BuildWindowModeCard(int index, string title, string subtitle, FrameworkElement mockup)
    {
        mockup.HorizontalAlignment = HorizontalAlignment.Left;
        mockup.VerticalAlignment = VerticalAlignment.Center;

        var textStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Opacity = 0.75,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        // A full-width row (mockup beside the text) rather than three side-by-side columns, so the cards
        // never overflow/clip regardless of the dialog's effective width at high-DPI.
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(mockup, 0);
        Grid.SetColumn(textStack, 1);
        row.Children.Add(mockup);
        row.Children.Add(textStack);

        // The entire window mock-up card is the selectable item (no radio button); the selected card shows
        // a blue accent border + wash ("aurora"). Focusable + Enter/Space activatable for keyboard use.
        var card = new Border
        {
            Child = row,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        int captured = index;
        card.Tapped += (_, _) => PreviewWindowModeCard(captured);
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                e.Handled = true;
                PreviewWindowModeCard(captured);
            }
        };
        _windowModeCards.Add(card);
        return card;
    }

    /// <summary>Selects one window-style card, applying the blue accent "aurora" to it and clearing the rest.</summary>
    private void SelectWindowModeCard(int index)
    {
        _windowModePickIndex = index;
        for (int i = 0; i < _windowModeCards.Count; i++)
        {
            bool selected = i == index;
            Border card = _windowModeCards[i];
            card.BorderBrush = selected
                ? ThemeBrush("AccentFillColorDefaultBrush")
                : ThemeBrush("CardStrokeColorDefaultBrush");
            card.Background = selected
                ? AccentAuroraBrush()
                : ThemeBrush("CardBackgroundFillColorDefaultBrush", 0x18);
        }
    }

    /// <summary>A translucent accent wash used as the selected card's background (the "aurora" glow).</summary>
    private static Brush AccentAuroraBrush()
    {
        ResourceDictionary res = Application.Current.Resources;
        if (res.ContainsKey("AccentFillColorDefaultBrush") && res["AccentFillColorDefaultBrush"] is SolidColorBrush accent)
        {
            Windows.UI.Color a = accent.Color;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, a.R, a.G, a.B));
        }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0x3B, 0x82, 0xF6));
    }

    // ── Stylistic (non-screenshot) window mock-ups ──

    private static FrameworkElement BuildLauncherMockup(bool onTop)
    {
        var surface = new Border
        {
            Width = 164,
            Height = 112,
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("LayerFillColorDefaultBrush", 0x30),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
        var grid = new Grid();

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
            Width = 152,
        };

        var bar = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Background = ThemeBrush("ControlFillColorDefaultBrush", 0x55),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 8, 0),
        };
        var barContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        barContent.Children.Add(new FontIcon { Glyph = "\uE721", FontSize = 11, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        barContent.Children.Add(FauxBar(96, 6, "TextFillColorTertiaryBrush"));
        bar.Child = barContent;
        stack.Children.Add(bar);

        var rows = new StackPanel { Spacing = 4 };
        rows.Children.Add(FauxBar(152, 7, "TextFillColorTertiaryBrush", opacity: 0.7));
        rows.Children.Add(FauxBar(128, 7, "TextFillColorTertiaryBrush", opacity: 0.5));
        stack.Children.Add(rows);

        grid.Children.Add(stack);

        if (onTop)
        {
            grid.Children.Add(new FontIcon
            {
                Glyph = "\uE718", // pinned
                FontSize = 12,
                Foreground = ThemeBrush("AccentFillColorDefaultBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0),
            });
        }

        surface.Child = grid;
        return surface;
    }

    private static FrameworkElement BuildTraditionalMockup()
    {
        var window = new Border
        {
            Width = 164,
            Height = 112,
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", 0x2A),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Grid { Height = 16, Background = ThemeBrush("LayerFillColorDefaultBrush", 0x45) };
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        for (int i = 0; i < 3; i++)
            dots.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ThemeBrush("TextFillColorTertiaryBrush") });
        titleBar.Children.Add(dots);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var search = new Border
        {
            Height = 16,
            Margin = new Thickness(8, 6, 8, 4),
            CornerRadius = new CornerRadius(4),
            Background = ThemeBrush("ControlFillColorDefaultBrush", 0x55),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
        Grid.SetRow(search, 1);
        root.Children.Add(search);

        var body = new Grid { Margin = new Thickness(8, 2, 8, 8), ColumnSpacing = 6 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var results = new StackPanel { Spacing = 5 };
        for (int i = 0; i < 4; i++)
        {
            results.Children.Add(new Border
            {
                Height = 7,
                CornerRadius = new CornerRadius(3),
                Background = ThemeBrush("TextFillColorTertiaryBrush"),
                Opacity = i == 0 ? 0.9 : 0.55,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }
        Grid.SetColumn(results, 0);
        body.Children.Add(results);

        var preview = new Border
        {
            Background = ThemeBrush("LayerFillColorDefaultBrush", 0x30),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
        };
        var previewLines = new StackPanel { Spacing = 4 };
        foreach (double w in new[] { 1.0, 0.8, 0.9, 0.6, 0.75 })
        {
            previewLines.Children.Add(new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2),
                Background = ThemeBrush("TextFillColorTertiaryBrush"),
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 96 * w,
            });
        }
        preview.Child = previewLines;
        Grid.SetColumn(preview, 1);
        body.Children.Add(preview);

        Grid.SetRow(body, 2);
        root.Children.Add(body);

        window.Child = root;
        return window;
    }

    private static Border FauxBar(double width, double height, string brushKey, double radius = 3, double opacity = 1.0)
    {
        var b = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = ThemeBrush(brushKey),
            Opacity = opacity,
        };
        if (!double.IsNaN(width))
            b.Width = width;
        return b;
    }

    private static Brush ThemeBrush(string key, byte fallbackGray = 0x80)
    {
        ResourceDictionary res = Application.Current.Resources;
        if (res.ContainsKey(key) && res[key] is Brush b)
            return b;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, fallbackGray, fallbackGray, fallbackGray));
    }
}
