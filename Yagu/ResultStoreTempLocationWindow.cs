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

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
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
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Search Result Temp Files",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

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

        body.Children.Add(CreateBodyText($"Yagu writes search result temp files while memory-saving mode is active. Yagu was launched from {launchDrive ?? "an unknown drive"}. To minimize disk contention, choosing a different drive can help."));
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
        Foreground = new SolidColorBrush(Colors.White),
    };

    private static TextBlock CreateMutedText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Opacity = 0.75,
        Foreground = new SolidColorBrush(Colors.White),
    };

    private static void AddFooter(Grid root, string buttonText, Action onClick)
    {
        var footer = new StackPanel
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
        footer.Children.Add(button);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
    }

    private void Accept(ResultStoreTempDriveOption? selectedOption)
    {
        _result = new ResultStoreTempLocationWindowResult(true, selectedOption);
        Close();
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

}