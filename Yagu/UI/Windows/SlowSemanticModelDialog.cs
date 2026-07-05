using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Services.Ai;
using System.Diagnostics.CodeAnalysis;

namespace Yagu;

/// <summary>The user's response to the slow-interpretation prompt.</summary>
/// <param name="ChosenAlias">The alias of a faster model to switch to and re-run with (already
/// downloaded and set as default by the dialog), or null when the user chose to keep waiting.</param>
/// <param name="DontWarnAgain">Whether the user ticked "Don't show this warning again for this model".</param>
internal sealed record SlowSemanticModelChoice(string? ChosenAlias, bool DontWarnAgain);

/// <summary>
/// Borderless warning modal shown when on-device AI interpretation runs long (30s+). Offers a list of
/// smaller/faster models that can run on this machine; on "Use this model" it downloads the pick (with a
/// progress bar), makes it the default, and returns its alias so the caller can re-run the search. A
/// "Don't show this warning again for this model" checkbox lets the user permanently suppress the prompt
/// for the exact variant that was running.
/// </summary>
[SuppressMessage(
    "Reliability", "CA1001",
    Justification = "The CancellationTokenSource is cancelled and disposed in OnClosed when the window closes.")]
internal sealed class SlowSemanticModelDialog : Window
{
    public delegate Task SwitchDelegate(
        string alias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken);

    private const int DialogWidth = 580;
    private const int DialogHeight = 560;

    private static readonly HashSet<SlowSemanticModelDialog> OpenWindows = new();

    private readonly TaskCompletionSource<SlowSemanticModelChoice> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private readonly ElementTheme _theme;
    private readonly string _currentModelName;
    private readonly int _elapsedSeconds;
    private readonly IReadOnlyList<SemanticModelOption> _options;
    private readonly SwitchDelegate _switchModel;
    private readonly CancellationTokenSource _cts = new();

    private Grid _root = null!;
    private TextBlock _titleText = null!;
    private TextBlock _subtitleText = null!;
    private Border _bodyHost = null!;
    private StackPanel _footer = null!;
    private CheckBox? _dontWarnCheckBox;

    private SemanticModelOption? _selected;
    private string? _resultAlias;
    private bool _completed;

    private SlowSemanticModelDialog(
        IntPtr ownerHwnd, ElementTheme theme, string currentModelName, int elapsedSeconds,
        IReadOnlyList<SemanticModelOption> options, SwitchDelegate switchModel)
    {
        _ownerHwnd = ownerHwnd;
        _theme = theme;
        _currentModelName = string.IsNullOrWhiteSpace(currentModelName) ? "The AI model" : currentModelName.Trim();
        _elapsedSeconds = elapsedSeconds;
        _options = options;
        _switchModel = switchModel;
        _selected = options.Count > 0 ? options[0] : null;

        Title = "Semantic Search";
        Content = BuildSkeleton();
        Closed += OnClosed;

        // Hide the OS title bar reliably (SetBorderAndTitleBar alone is not enough); matches the
        // title-bar-less pattern used by MainWindow/SettingsWindow/SemanticModelDownloadDialog.
        ExtendsContentIntoTitleBar = true;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);

        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = Title;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, DialogWidth, DialogHeight, minHeight: 320);
        TryConfigurePresenter(appWindow);
        TrySetIcon(appWindow);
    }

    public static Task<SlowSemanticModelChoice> ShowAsync(
        IntPtr ownerHwnd, ElementTheme theme, string currentModelName, int elapsedSeconds,
        IReadOnlyList<SemanticModelOption> options, SwitchDelegate switchModel)
    {
        var dialog = new SlowSemanticModelDialog(ownerHwnd, theme, currentModelName, elapsedSeconds, options, switchModel);
        return dialog.ShowModalAsync();
    }

    public static bool HasOpenOwnedWindow(IntPtr ownerHwnd)
    {
        foreach (var window in OpenWindows)
        {
            if (window._ownerHwnd == ownerHwnd)
                return true;
        }
        return false;
    }

    private Task<SlowSemanticModelChoice> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);

        ShowChooseState();
        return _completion.Task;
    }

    // ── Skeleton ──────────────────────────────────────────────────────────────────────────────

    private Grid BuildSkeleton()
    {
        _root = new Grid
        {
            Padding = new Thickness(28, 26, 28, 24),
            RowSpacing = 16,
        };
        Yagu.Services.AppThemeService.ApplyThemedDialogSurface(_root, _theme);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

        var header = new StackPanel { Spacing = 6 };

        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleLine.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA", // warning triangle
            FontSize = 20,
            Foreground = new SolidColorBrush(Colors.Gold),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _titleText = new TextBlock
        {
            Text = "AI interpretation is taking a while",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLine.Children.Add(_titleText);
        header.Children.Add(titleLine);

        _subtitleText = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.82,
        };
        header.Children.Add(_subtitleText);
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        _bodyHost = new Border { VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_bodyHost, 1);
        _root.Children.Add(_bodyHost);

        _footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        Grid.SetRow(_footer, 2);
        _root.Children.Add(_footer);

        _root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                Decline();
            }
        };

        return _root;
    }

    // ── States ────────────────────────────────────────────────────────────────────────────────

    private ProgressBar? _progressBar;
    private TextBlock? _progressText;

    private void ShowChooseState()
    {
        _titleText.Text = "AI interpretation is taking a while";
        _subtitleText.Text =
            $"\u201C{_currentModelName}\u201D has been interpreting your request for over {_elapsedSeconds} seconds. " +
            "Switch to a smaller, faster model below — it downloads if needed, becomes your default, and the search re-runs.";

        var panel = new StackPanel { Spacing = 12 };

        var list = new StackPanel { Spacing = 8 };
        foreach (var option in _options)
            list.Children.Add(BuildOptionRow(option));
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 280,
        });

        _dontWarnCheckBox = new CheckBox
        {
            Content = "Don't show this warning again for this model",
            IsChecked = _dontWarnCheckBox?.IsChecked ?? false,
            Margin = new Thickness(0, 2, 0, 0),
        };
        panel.Children.Add(_dontWarnCheckBox);

        _bodyHost.Child = panel;

        _footer.Children.Clear();
        AddFooterButton("Keep waiting", accent: false, () => Decline());
        AddPrimaryButton();
    }

    private void ShowDownloadingState()
    {
        string name = _selected?.DisplayName ?? "model";
        _titleText.Text = $"Switching to {name}…";
        _subtitleText.Text = "Downloading if needed, then re-running your search. The model is cached for next time.";

        var panel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        _progressBar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        _progressText = new TextBlock { Text = "Starting…", Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_progressText);
        panel.Children.Add(_progressBar);
        _bodyHost.Child = panel;

        _footer.Children.Clear();
        AddFooterButton("Cancel", accent: false, () => Decline());
    }

    private void ShowErrorState(string message)
    {
        _titleText.Text = "Couldn't switch the model";
        _subtitleText.Text = "Your original search is still running with the current model.";

        _bodyHost.Child = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _footer.Children.Clear();
        AddFooterButton("Keep waiting", accent: false, () => Decline());
        AddFooterButton("Try again", accent: true, () => ShowChooseState());
    }

    private Border BuildOptionRow(SemanticModelOption option)
    {
        var radio = new RadioButton
        {
            GroupName = "SlowSemanticModelOption",
            IsChecked = ReferenceEquals(option, _selected),
            Tag = option,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
        };
        radio.Checked += (s, _) =>
        {
            if (s is RadioButton { Tag: SemanticModelOption o })
            {
                _selected = o;
                RefreshPrimaryButton();
            }
        };

        var content = new StackPanel { Spacing = 3 };

        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(new TextBlock
        {
            Text = option.DisplayName,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (option.IsCached)
            titleLine.Children.Add(BuildPill("Downloaded", accent: false));
        if (option.IsBelowRecommended)
        {
            var warn = new FontIcon
            {
                Glyph = "\uE7BA", // warning
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xA8, 0x17)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(warn, "This model is faster but may produce less accurate results than the current model.");
            titleLine.Children.Add(warn);
        }
        content.Children.Add(titleLine);

        var detailParts = new List<string>();
        detailParts.Add(option.IsCached ? "Already downloaded" : FormatSize(option.SizeBytes));
        if (!string.IsNullOrEmpty(option.DeviceLabel))
            detailParts.Add(option.DeviceLabel!);
        if (option.IsBelowRecommended)
            detailParts.Add("may be less accurate");
        content.Children.Add(new TextBlock
        {
            Text = string.Join("  \u00b7  ", detailParts),
            FontSize = 12,
            Opacity = 0.72,
        });

        radio.Content = content;

        return new Border
        {
            Background = ResourceBrush("LayerFillColorDefaultBrush", ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ResourceBrush("ControlElevationBorderBrush", ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = radio,
        };
    }

    private static Border BuildPill(string text, bool accent)
    {
        var brushKey = accent ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush";
        return new Border
        {
            Background = ResourceBrush(brushKey, accent
                ? ColorHelper.FromArgb(0xFF, 0x4C, 0x8B, 0xF5)
                : ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
                    : ResourceBrush("TextFillColorPrimaryBrush", ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            },
        };
    }

    // ── Async flow ────────────────────────────────────────────────────────────────────────────

    private async Task StartSwitchAsync()
    {
        if (_selected is null) return;
        string alias = _selected.Alias;

        ShowDownloadingState();
        var progress = new Progress<SemanticTranslationProgress>(OnProgress);
        try
        {
            await _switchModel(alias, progress, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested) return;

            _resultAlias = alias;
            Complete();
        }
        catch (OperationCanceledException)
        {
            // Dialog closing.
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                ShowErrorState($"The model switch failed: {ex.Message}");
        }
    }

    private void OnProgress(SemanticTranslationProgress p)
    {
        if (_progressBar is null || _progressText is null) return;
        if (p.Percent is { } pct)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = Math.Clamp(pct, 0, 100);
        }
        else
        {
            _progressBar.IsIndeterminate = true;
        }
        _progressText.Text = p.Message;
    }

    // ── Footer buttons ────────────────────────────────────────────────────────────────────────

    private Button? _primaryButton;

    private void AddPrimaryButton()
    {
        _primaryButton = new Button
        {
            MinWidth = 130,
            Padding = new Thickness(18, 8, 18, 8),
            IsEnabled = _selected is not null,
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            _primaryButton.Style = accentStyle;
        _primaryButton.Click += (_, _) => _ = StartSwitchAsync();
        _footer.Children.Add(_primaryButton);
        RefreshPrimaryButton();
    }

    private void RefreshPrimaryButton()
    {
        if (_primaryButton is null) return;
        _primaryButton.IsEnabled = _selected is not null;
        _primaryButton.Content = _selected is { IsCached: true } ? "Use this model" : "Download & use";
    }

    private Button AddFooterButton(string text, bool accent, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 110,
            Padding = new Thickness(18, 8, 18, 8),
        };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            button.Style = accentStyle;
        button.Click += (_, _) => onClick();
        _footer.Children.Add(button);
        return button;
    }

    // ── Completion ────────────────────────────────────────────────────────────────────────────

    private void Decline()
    {
        _resultAlias = null;
        Complete();
    }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        try { _cts.Cancel(); } catch { }
        Close();
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is not { } b || b <= 0) return "size unknown";
        double mb = b / (1024d * 1024d);
        if (mb >= 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{mb / 1024d:0.0} GB download");
        return string.Create(CultureInfo.InvariantCulture, $"{mb:0} MB download");
    }

    private static Brush ResourceBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private static void TryConfigurePresenter(AppWindow appWindow)
    {
        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
        }
        catch { }
    }

    private static void TrySetIcon(AppWindow appWindow)
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OpenWindows.Remove(this);
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();

        if (_ownerHwnd != IntPtr.Zero)
        {
            EnableWindow(_ownerHwnd, true);
            SetForegroundWindow(_ownerHwnd);
        }

        bool dontWarnAgain = _dontWarnCheckBox?.IsChecked == true;
        _completion.TrySetResult(new SlowSemanticModelChoice(_resultAlias, dontWarnAgain));
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
