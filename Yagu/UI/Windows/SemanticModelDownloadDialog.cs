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

namespace Yagu;

/// <summary>
/// Borderless first-run modal that offers to download a local AI model for Semantic Search. Lists
/// the hardware-compatible models (recommended pick flagged, weaker ones warned), then downloads the
/// chosen model with a progress bar. Returns the chosen alias on success (empty string = use the
/// recommended/auto model) or null when the user declined / the download failed.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA1001",
    Justification = "The CancellationTokenSource is cancelled and disposed in OnClosed when the window closes.")]
internal sealed class SemanticModelDownloadDialog : Window
{
    public delegate Task<IReadOnlyList<SemanticModelOption>> LoadOptionsDelegate(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken);

    public delegate Task DownloadDelegate(
        string? alias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken);

    private const int DialogWidth = 580;
    private const int DialogHeight = 560;

    private static readonly HashSet<SemanticModelDownloadDialog> OpenWindows = new();

    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private readonly ElementTheme _theme;
    private readonly LoadOptionsDelegate _loadOptions;
    private readonly DownloadDelegate _download;
    private readonly string? _currentAlias;
    private readonly CancellationTokenSource _cts = new();

    private Grid _root = null!;
    private TextBlock _titleText = null!;
    private TextBlock _subtitleText = null!;
    private Border _bodyHost = null!;
    private StackPanel _footer = null!;

    private IReadOnlyList<SemanticModelOption> _options = Array.Empty<SemanticModelOption>();
    private SemanticModelOption? _selected;
    private string? _resultAlias;
    private bool _completed;

    private SemanticModelDownloadDialog(
        IntPtr ownerHwnd, ElementTheme theme, LoadOptionsDelegate loadOptions, DownloadDelegate download, string? currentAlias)
    {
        _ownerHwnd = ownerHwnd;
        _theme = theme;
        _loadOptions = loadOptions;
        _download = download;
        _currentAlias = string.IsNullOrWhiteSpace(currentAlias) ? null : currentAlias.Trim();

        Title = "Semantic Search";
        Content = BuildSkeleton();
        Closed += OnClosed;

        // Hide the OS title bar reliably (SetBorderAndTitleBar alone is not enough); matches the
        // title-bar-less pattern used by MainWindow/SettingsWindow/ResultStoreTempLocationWindow.
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

    public static Task<string?> ShowAsync(
        IntPtr ownerHwnd, ElementTheme theme, LoadOptionsDelegate loadOptions, DownloadDelegate download, string? currentAlias = null)
    {
        var dialog = new SemanticModelDownloadDialog(ownerHwnd, theme, loadOptions, download, currentAlias);
        return dialog.ShowModalAsync();
    }

    private async Task<string?> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);

        _ = LoadOptionsAsync();
        return await _completion.Task.ConfigureAwait(true);
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
        _titleText = new TextBlock
        {
            Text = "Semantic Search",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleLine.Children.Add(new FontIcon
        {
            Glyph = "\uE99A", // AI model
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleLine.Children.Add(_titleText);
        _subtitleText = new TextBlock
        {
            Text = "Setting up the on-device AI model…",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.82,
        };
        header.Children.Add(titleLine);
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

        ShowLoadingState("Preparing the on-device AI model…");
        return _root;
    }

    // ── States ────────────────────────────────────────────────────────────────────────────────

    private ProgressBar? _progressBar;
    private TextBlock? _progressText;

    private void ShowLoadingState(string message)
    {
        _titleText.Text = "Semantic Search";
        _subtitleText.Text = "Semantic search runs entirely on your PC. The first time you set it up, Yagu " +
            "downloads a one-time AI runtime for your hardware (usually a few hundred MB), plus the model you choose.";

        var panel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        _progressBar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        _progressText = new TextBlock { Text = message, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_progressText);
        panel.Children.Add(_progressBar);
        _bodyHost.Child = panel;

        _footer.Children.Clear();
        AddFooterButton("Cancel", accent: false, () => Decline());
    }

    private void ShowChooseState()
    {
        _titleText.Text = "Download an AI model";
        _subtitleText.Text = "Choose a model to download. The recommended model is the best fit for your hardware. " +
            "Smaller models marked with a warning may produce less accurate results.";

        var list = new StackPanel { Spacing = 8 };
        foreach (var option in _options)
            list.Children.Add(BuildOptionRow(option));

        _bodyHost.Child = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _footer.Children.Clear();
        AddFooterButton("Not now", accent: false, () => Decline());
        AddPrimaryButton();
    }

    private void ShowDownloadingState()
    {
        string name = _selected?.DisplayName ?? "model";
        _titleText.Text = $"Downloading {name}…";
        _subtitleText.Text = "This runs once. The model is cached on your PC for future searches.";

        var panel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        _progressBar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };
        _progressText = new TextBlock { Text = "Starting download…", Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_progressText);
        panel.Children.Add(_progressBar);
        _bodyHost.Child = panel;

        _footer.Children.Clear();
        AddFooterButton("Cancel", accent: false, () => Decline());
    }

    private void ShowErrorState(string message)
    {
        _titleText.Text = "Couldn't set up the model";
        _subtitleText.Text = "Semantic search needs a downloaded model to run.";

        _bodyHost.Child = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _footer.Children.Clear();
        AddFooterButton("Not now", accent: false, () => Decline());
        if (_options.Count > 0)
            AddFooterButton("Try again", accent: true, () => ShowChooseState());
        else
            AddFooterButton("Try again", accent: true, () => { _ = LoadOptionsAsync(); });
    }

    private Border BuildOptionRow(SemanticModelOption option)
    {
        var radio = new RadioButton
        {
            GroupName = "SemanticModelOption",
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
        if (option.IsRecommended)
            titleLine.Children.Add(BuildPill("Recommended", accent: true));
        if (option.IsCached)
            titleLine.Children.Add(BuildPill("Downloaded", accent: false));

        // The "current" model is the one a search runs right now: the user's pinned override when set,
        // otherwise the recommended pick. Every OTHER model is flagged so the user knows switching may
        // change accuracy.
        bool isCurrent = _currentAlias is not null
            ? string.Equals(option.Alias, _currentAlias, StringComparison.OrdinalIgnoreCase)
            : option.IsRecommended;
        if (isCurrent && !option.IsRecommended)
            titleLine.Children.Add(BuildPill("Current", accent: true));
        if (!isCurrent)
            titleLine.Children.Add(BuildPill("Results may vary", accent: false));
        if (option.IsBelowRecommended)
        {
            var warn = new FontIcon
            {
                Glyph = "\uE7BA", // warning
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xA8, 0x17)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(warn, "This model may produce less accurate results than the recommended model.");
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
            Text = string.Join("  ·  ", detailParts),
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

    // ── Async flows ───────────────────────────────────────────────────────────────────────────

    private async Task LoadOptionsAsync()
    {
        ShowLoadingState("Preparing the on-device AI model…");
        var progress = new Progress<SemanticTranslationProgress>(OnProgress);
        try
        {
            var options = await _loadOptions(progress, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested) return;

            _options = options;
            if (_options.Count == 0)
            {
                ShowErrorState("No compatible on-device AI model is available for this machine.");
                return;
            }

            _selected = _options.FirstOrDefault(o =>
                _currentAlias is not null && string.Equals(o.Alias, _currentAlias, StringComparison.OrdinalIgnoreCase))
                ?? _options.FirstOrDefault(o => o.IsRecommended)
                ?? _options[0];
            ShowChooseState();
        }
        catch (OperationCanceledException)
        {
            // Dialog closing.
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                ShowErrorState($"Could not load model options: {ex.Message}");
        }
    }

    private async Task StartDownloadAsync()
    {
        if (_selected is null) return;

        // Recommended pick is stored as "auto" (empty) so Yagu keeps choosing the best model.
        string? aliasToStore = _selected.IsRecommended ? string.Empty : _selected.Alias;

        ShowDownloadingState();
        var progress = new Progress<SemanticTranslationProgress>(OnProgress);
        try
        {
            await _download(aliasToStore, progress, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested) return;

            _resultAlias = aliasToStore;
            Complete();
        }
        catch (OperationCanceledException)
        {
            // Dialog closing.
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                ShowErrorState($"The model download failed: {ex.Message}");
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
            MinWidth = 110,
            Padding = new Thickness(18, 8, 18, 8),
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            _primaryButton.Style = accentStyle;
        _primaryButton.Click += (_, _) => _ = StartDownloadAsync();
        _footer.Children.Add(_primaryButton);
        RefreshPrimaryButton();
    }

    private void RefreshPrimaryButton()
    {
        if (_primaryButton is null) return;
        _primaryButton.Content = _selected is { IsCached: true } ? "Use this model" : "Download";
    }

    private Button AddFooterButton(string text, bool accent, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
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
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
            if (System.IO.File.Exists(iconPath))
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

        _completion.TrySetResult(_resultAlias);
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
