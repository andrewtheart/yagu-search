using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu;

internal sealed record ResultStoreTempLocationWindowResult(
    bool Accepted,
    ResultStoreTempDriveOption? SelectedOption);

internal sealed class ResultStoreTempLocationWindow : Window
{
    private static readonly HashSet<ResultStoreTempLocationWindow> OpenWindows = new();

    private readonly TaskCompletionSource<ResultStoreTempLocationWindowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private readonly ResultStoreTempLocationWindowResult _dismissedResult = new(false, null);
    private ResultStoreTempLocationWindowResult? _result;
    private readonly AppWindow _appWindow;

    private ResultStoreTempLocationWindow(
        IntPtr ownerHwnd,
        string? launchDrive,
        IReadOnlyList<ResultStoreTempDriveOption> options,
        string? currentTempDirectory)
    {
        _ownerHwnd = ownerHwnd;
        Title = "Search Result Temp Files";
        Content = BuildContent(launchDrive, options, currentTempDirectory);
        Closed += OnClosed;

        // Hide the OS title bar reliably. Setting this Window property directly
        // (outside the presenter try/catch below) guarantees the caption strip is
        // not drawn even if the OverlappedPresenter configuration fails to apply,
        // matching the title-bar-less pattern used by MainWindow/SettingsWindow.
        ExtendsContentIntoTitleBar = true;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        appWindow.Title = Title;

        int width = 720;
        int height = options.Count == 0 ? 340 : 470;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, width, height, minHeight: 300);

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

    public static Task<ResultStoreTempLocationWindowResult> ShowAsync(
        IntPtr ownerHwnd,
        string? launchDrive,
        IReadOnlyList<ResultStoreTempDriveOption> options,
        string? currentTempDirectory)
    {
        var window = new ResultStoreTempLocationWindow(ownerHwnd, launchDrive, options, currentTempDirectory);
        return window.ShowModalAsync();
    }

    private Task<ResultStoreTempLocationWindowResult> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);
        return _completion.Task;
    }

    private Grid BuildContent(
        string? launchDrive,
        IReadOnlyList<ResultStoreTempDriveOption> options,
        string? currentTempDirectory)
    {
        var root = new Grid
        {
            Padding = new Thickness(32, 28, 32, 28),
        };
        root.Loaded += (_, _) =>
        {
            // The 720x470 guess above (WindowForegroundHelper.CenterWindowOverOwner) is a rough
            // upper bound, not the actual content height, so without this pass the fixed-size Star
            // row leaves a large empty gap above the footer button. Fit the window to the real
            // content height once the content is measurable, and once more after layout settles
            // (text can wrap to an extra line on the first pass).
            AutoSizeHeightToContent(root);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => AutoSizeHeightToContent(root));
        };
        // Honor the Yagu theme (Auto/Dark/Light) instead of the previous hardcoded dark surface.
        AppThemeService.ApplyThemedDialogSurface(root, ElementTheme.Default);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Search Result Temp Files",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleLine.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7", // Folder
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleLine.Children.Add(title);
        Grid.SetRow(titleLine, 0);
        root.Children.Add(titleLine);

        var body = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 18, 0, 20),
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        if (options.Count == 0)
        {
            body.Children.Add(CreateBodyText($"Yagu was launched from {launchDrive ?? "an unknown drive"}. No writable drive with at least 50 GB free is currently available, so Yagu will use the Windows temp folder for search result temp files."));
            body.Children.Add(CreateMutedText($"Current fallback: {Path.GetTempPath()}"));
            AddFooter(root, "OK", () => Accept(null));
            return root;
        }

        body.Children.Add(CreateBodyText($"Yagu writes search result temp files while memory-saving mode is active. Yagu was launched from {launchDrive ?? "an unknown drive"}. Changing to a different drive would likely only help if Yagu is installed on a mechanical hard drive (HDD) — on an SSD or NVMe drive the default location is already fast."));
        body.Children.Add(CreateMutedText("Choose any available and writable drive with at least 50 GB free:"));

        var drivePicker = new ComboBox
        {
            MinWidth = 560,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var selectedOption = ResultStoreTempLocationService.ChoosePreferredOption(
            options,
            currentTempDirectory,
            launchDrive);

        for (int i = 0; i < options.Count; i++)
        {
            drivePicker.Items.Add(new ComboBoxItem
            {
                Content = options[i].DisplayName,
                Tag = options[i],
            });

            if (Equals(options[i], selectedOption))
                drivePicker.SelectedIndex = i;
        }

        if (drivePicker.SelectedIndex < 0)
            drivePicker.SelectedIndex = 0;

        body.Children.Add(drivePicker);

        var pathPreview = CreateMutedText(string.Empty);
        body.Children.Add(pathPreview);

        void UpdatePathPreview()
        {
            if (drivePicker.SelectedItem is ComboBoxItem item && item.Tag is ResultStoreTempDriveOption option)
                pathPreview.Text = $"Temp files will be written under {option.TempDirectory}.";
        }

        drivePicker.SelectionChanged += (_, _) => UpdatePathPreview();
        UpdatePathPreview();

        AddFooter(root, "Use selected drive", () =>
        {
            if (drivePicker.SelectedItem is ComboBoxItem item && item.Tag is ResultStoreTempDriveOption option)
                Accept(option);
        });

        return root;
    }

    private static TextBlock CreateBodyText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 15,
        LineHeight = 22,
    };

    private static TextBlock CreateMutedText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Opacity = 0.75,
    };

    private void AddFooter(Grid root, string buttonText, Action onClick)
    {
        var footer = new Grid
        {
            ColumnSpacing = 20,
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (YaguDialog.GetStartupProgress(_ownerHwnd) is { } startupProgress)
        {
            FrameworkElement progress = YaguDialog.CreateStartupProgressElement(startupProgress);
            Grid.SetColumn(progress, 0);
            footer.Children.Add(progress);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var button = new Button
        {
            Content = buttonText,
            MinWidth = 220,
            Padding = new Thickness(18, 8, 18, 8),
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            button.Style = accentStyle;

        button.Click += (_, _) => onClick();
        buttons.Children.Add(button);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
    }

    private void Accept(ResultStoreTempDriveOption? selectedOption)
    {
        _result = new ResultStoreTempLocationWindowResult(true, selectedOption);
        Close();
    }

    /// <summary>
    /// Grows or shrinks the window's height to exactly fit its content (mirrors
    /// YaguDialog.AutoSizeHeightToContent), so the fixed 720x470 guess passed to
    /// <see cref="WindowForegroundHelper.CenterWindowOverOwner"/> in the constructor never leaves dead
    /// space below the footer button. The width stays fixed; the content's natural height is measured
    /// at that width (unbounded height) and the window is resized and re-centered —
    /// <see cref="WindowForegroundHelper.CenterWindowOverOwner"/> clamps the result to the monitor work area.
    /// </summary>
    private void AutoSizeHeightToContent(FrameworkElement root)
    {
        var xamlRoot = root.XamlRoot;
        if (xamlRoot is null)
            return;

        double scale = xamlRoot.RasterizationScale;
        if (scale <= 0)
            scale = 1.0;

        var currentSize = _appWindow.Size; // physical pixels
        if (currentSize.Width <= 0)
            return;

        // Measure at the CLIENT width (outer width minus the left/right frame) so text wrapping matches
        // what is actually rendered; fall back to the outer width before the client size is reported.
        int clientWidthPhysical = _appWindow.ClientSize.Width > 0 ? _appWindow.ClientSize.Width : currentSize.Width;
        double widthDip = clientWidthPhysical / scale;

        // Natural content height at the current width. Measuring outside the layout pass (in Loaded)
        // is safe; DesiredSize reflects what the content wants before the window constrained it.
        root.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
        double desiredHeightDip = root.DesiredSize.Height;
        if (desiredHeightDip <= 0)
            return;

        // desiredHeightDip is the CLIENT (content) height; add back the non-client frame (border +
        // resize grip) so the outer window height passed to CenterWindowOverOwner doesn't clip the
        // last line of content. A small safety pad absorbs sub-pixel measurement rounding.
        int chromeHeight = NonClientFrameHeight();
        int desiredHeightPhysical = (int)Math.Ceiling((desiredHeightDip + 2) * scale) + chromeHeight;
        if (Math.Abs(desiredHeightPhysical - currentSize.Height) <= 2)
            return; // already the right height

        WindowForegroundHelper.CenterWindowOverOwner(_appWindow, _ownerHwnd, currentSize.Width, desiredHeightPhysical, minHeight: 300);
    }

    /// <summary>Top+bottom non-client frame height in physical pixels at this window's DPI (border +
    /// padded resize grip), so the auto-sized CLIENT content height is not clipped by the frame.</summary>
    private int NonClientFrameHeight()
    {
        try
        {
            int dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            int frameY = GetSystemMetricsForDpi(33 /* SM_CYFRAME */, (uint)dpi);
            int padded = GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, (uint)dpi);
            return (frameY + padded) * 2;
        }
        catch
        {
            return 0;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OpenWindows.Remove(this);

        if (_ownerHwnd != IntPtr.Zero)
        {
            EnableWindow(_ownerHwnd, true);
            SetForegroundWindow(_ownerHwnd);
        }

        _completion.TrySetResult(_result ?? _dismissedResult);
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

}