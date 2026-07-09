using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Automation;
using Windows.System;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
using Yagu.ViewModels;
using System.Diagnostics;
using System.Globalization;
using XamlFontFamily = Microsoft.UI.Xaml.Media.FontFamily;

namespace Yagu;

public sealed partial class SettingsWindow : Window
{
    private static IReadOnlyList<string>? _systemFontFamilyNames;

    private readonly MainViewModel _viewModel;
    private readonly HotkeyService _hotkeyService;
    private readonly IntPtr _mainHwnd;
    private readonly IntPtr _settingsHwnd;
    private readonly AppWindow _appWindow;
    private readonly Action<bool>? _applyWordWrap;
    private readonly Action? _applyPreviewSectionBackgrounds;
    private readonly Action _openHelp;
    private readonly Action? _suppressOwnerHideToTray;
    private readonly List<UIElement> _tabPages = new();
    private readonly List<string> _tabHeaders = new();
    private readonly HashSet<UIElement> _dirtyTrackedElements = new();
    private readonly Dictionary<UIElement, object?> _cleanSettingValues = new();
    private readonly List<LazyColorSettingState> _lazyColorSettings = new();
    private readonly List<Action> _fontContrastStatusRefreshers = new();
    private readonly List<Action> _defaultResetButtonRefreshers = new();
    private bool _settingsDirty;
    private bool _settingDirtyTrackingEnabled;
    private bool _settingsContentBuilt;
    private int? _pendingSelectTabIndex;
    private string? _pendingSelectTabHeader;
    private bool _cleanHasShownFileDrawerIntroTip;
    private bool _cleanHasShownFileDrawerLineNumberIntroTip;
    private bool _cleanHasShownPreviewMatchIntroTip;
    private DispatcherTimer? _fontContrastCheckTimer;
    private DispatcherTimer? _deferredSettingsContentBuildTimer;
    private bool _settingsCloseConfirmed;
    private bool _settingsClosePromptOpen;
    private bool _searchableEntriesExtracted;

    private static readonly Windows.UI.Color ContrastReadableGreen = Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43);
    private static readonly Windows.UI.Color ContrastUnreadableRed = Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38);
    private const int MaxComboBoxItemsIndexedForSettingsSearch = 80;

    /// <summary>Flat registry of every setting built during BuildSettingsContent for search filtering.</summary>
    private readonly List<SettingEntry> _settingEntries = new();

    private readonly List<UIElement> _searchResultContainers = new();

    /// <summary>Represents a single setting item with searchable text and its UI elements.</summary>
    private sealed class SettingEntry
    {
        public required string TabHeader { get; init; }
        public required int TabIndex { get; init; }
        public required string Label { get; init; }
        public string? Description { get; init; }
        public string? ControlText { get; init; }
        public UIElement? TargetElement { get; init; }
        public bool Matches(string query)
        {
            return Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (Description is not null && Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                || (ControlText is not null && ControlText.Contains(query, StringComparison.OrdinalIgnoreCase))
                || TabHeader.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class LazyColorSettingState
    {
        public required Func<Windows.UI.Color> GetColor { get; init; }
        public required Action<Windows.UI.Color> RestoreColor { get; init; }
        public Windows.UI.Color CleanColor { get; set; }
    }

    private sealed class LazyColorSettingHandle
    {
        public required Func<Windows.UI.Color> GetColor { get; init; }
        public required Action<Windows.UI.Color> SetColor { get; init; }

        public Windows.UI.Color Color
        {
            get => GetColor();
            set => SetColor(value);
        }
    }

    public SettingsWindow(MainViewModel viewModel, HotkeyService hotkeyService, IntPtr mainHwnd, Action<bool>? applyWordWrap, Action? applyPreviewSectionBackgrounds, Action openHelp, Action? suppressOwnerHideToTray = null)
    {
        _viewModel = viewModel;
        _hotkeyService = hotkeyService;
        _mainHwnd = mainHwnd;
        _applyWordWrap = applyWordWrap;
        _applyPreviewSectionBackgrounds = applyPreviewSectionBackgrounds;
        _openHelp = openHelp;
        _suppressOwnerHideToTray = suppressOwnerHideToTray;
        InitializeComponent();

        InitializeHelpKeyboardShortcut();

        // Set up custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Size and center over the owner window
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _settingsHwnd = hwnd;
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, mainHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        // Sized so the widest tab content (620px fixed-width filter rows plus the 200px tab
        // rail, pane padding and vertical scrollbar) fits without horizontal scrolling — which
        // stays disabled — and so most tabs need little or no vertical scrolling. The helper
        // clamps to the monitor work area, so this is safe on smaller displays.
        const int w = 1040, h = 920;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, mainHwnd, w, h);

        ApplySettingsTheme();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplySettingsTitleBarButtonTheme();
            RefreshFontContrastStatusIndicators();
            QueueFontContrastCheck();
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        appWindow.Closing += OnSettingsAppWindowClosing;
        Closed += OnSettingsWindowClosed;

        // Set window icon to match the main Yagu window
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
        if (File.Exists(icoPath))
            appWindow.SetIcon(icoPath);

        ShowSettingsLoadingPlaceholder();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (IsFirstTimeIntroductoryTooltipProperty(e.PropertyName) && !_settingsDirty)
            CaptureCleanFirstTimeIntroductoryTooltipValues();

        RefreshDefaultResetButtons();

        if (e.PropertyName == nameof(MainViewModel.ThemeModeIndex))
        {
            ApplySettingsTheme();
            QueueFontContrastCheck();
            return;
        }

        if (IsFontContrastRelevantProperty(e.PropertyName))
            QueueFontContrastCheck();
    }

    private void QueueFontContrastCheck()
    {
        _fontContrastCheckTimer ??= CreateFontContrastCheckTimer();
        _fontContrastCheckTimer.Stop();
        _fontContrastCheckTimer.Start();
    }

    private DispatcherTimer CreateFontContrastCheckTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += OnFontContrastCheckTimerTick;
        return timer;
    }

    private async void OnFontContrastCheckTimerTick(object? sender, object e)
    {
        _fontContrastCheckTimer?.Stop();

        await FontContrastWarningDialog.ShowIfNeededAsync(
            _mainHwnd,
            _viewModel,
            ResolveFontContrastTheme());
    }

    private FontContrastTheme ResolveFontContrastTheme()
        => AppThemeService.ResolveEffectiveTheme(RootGrid, _viewModel.ThemeModeIndex) == ElementTheme.Light
            ? FontContrastTheme.Light
            : FontContrastTheme.Dark;

    private static bool IsFontContrastRelevantProperty(string? propertyName)
        => propertyName is nameof(MainViewModel.SelectedPreviewContentBackgroundColor)
            or nameof(MainViewModel.UnselectedPreviewContentBackgroundColor)
            or nameof(MainViewModel.PreviewGutterContextColor)
            or nameof(MainViewModel.PreviewGutterMatchColor)
            or nameof(MainViewModel.PreviewEditorGutterColor)
            or nameof(MainViewModel.PreviewMatchTextColor)
            or nameof(MainViewModel.PreviewMatchLineColor)
            or nameof(MainViewModel.ResultListMatchHighlightColor);

    /// <summary>Opens the active Yagu log file in Notepad. Resolves notepad.exe by its full System32
    /// path so the launch does not depend on the process working directory or PATH (an installed app
    /// runs from C:\Program Files\Yagu, where a bare "notepad.exe" with UseShellExecute=false fails
    /// with "The system cannot find the file specified"). Falls back to opening the log with its
    /// default handler via the shell. Swallows any launch failure (logged) so it never crashes the
    /// Settings window.</summary>
    private static void OpenLogFileInNotepad(string logPath)
    {
        try
        {
            string notepadPath = Path.Combine(System.Environment.SystemDirectory, "notepad.exe");
            if (File.Exists(notepadPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = notepadPath,
                    Arguments = $"\"{logPath}\"",
                    UseShellExecute = false,
                });
                return;
            }

            // Notepad not present at the canonical location (e.g. Store-only Notepad): let the shell
            // open the log file with whatever text handler is registered.
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            LogService.Instance.Warning("Settings", $"Failed to open log file in Notepad: {ex.Message}", ex);
        }
    }

    private void ApplySettingsTheme()
    {
        AppThemeService.ApplyRequestedTheme(RootGrid, _viewModel.ThemeModeIndex);
        ApplySettingsTitleBarButtonTheme();
        RefreshFontContrastStatusIndicators();
    }

    private void RefreshFontContrastStatusIndicators()
    {
        foreach (var refresh in _fontContrastStatusRefreshers.ToArray())
        {
            try { refresh(); }
            catch { }
        }
    }

    private void RefreshDefaultResetButtons()
    {
        foreach (var refresh in _defaultResetButtonRefreshers.ToArray())
        {
            try { refresh(); }
            catch { }
        }
    }

    private void RegisterDefaultResetButton(Button button, Func<bool> isCurrentDefault)
    {
        void Refresh() => button.IsEnabled = !isCurrentDefault();
        _defaultResetButtonRefreshers.Add(Refresh);
        Refresh();
    }

    private static bool SettingStringEquals(string? currentValue, string defaultValue)
        => string.Equals(currentValue ?? string.Empty, defaultValue, StringComparison.Ordinal);

    private static bool SettingColorEquals(string? currentHex, string defaultHex, Windows.UI.Color fallback)
        => ColorStringHelper.Parse(currentHex ?? string.Empty, fallback)
            .Equals(ColorStringHelper.Parse(defaultHex, fallback));

    private void ApplySettingsTitleBarButtonTheme()
    {
        try
        {
            AppThemeService.ApplyTitleBarButtonTheme(
                _appWindow,
                AppThemeService.ResolveEffectiveTheme(RootGrid, _viewModel.ThemeModeIndex));
        }
        catch { }
    }

    public void BringInFrontOfMainWindow()
        => WindowForegroundHelper.BringOwnedWindowToFront(this, _mainHwnd);

    private void InitializeHelpKeyboardShortcut()
    {
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var helpAccelerator = new KeyboardAccelerator { Key = VirtualKey.F1 };
        helpAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            _openHelp();
        };
        RootGrid.KeyboardAccelerators.Add(helpAccelerator);
    }

    private void OnSettingsRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.F1)
            return;

        e.Handled = true;
        _openHelp();
    }

    /// <summary>Navigate to a specific tab by zero-based index.</summary>
    public void SelectTab(int index)
    {
        if (index < 0)
            return;

        if (!_settingsContentBuilt)
        {
            _pendingSelectTabIndex = index;
            return;
        }

        if (index >= TabList.Items.Count)
            return;

        bool restoreNormalTabView = _isSearchActive
            || !string.IsNullOrWhiteSpace(SearchBox.Text)
            || TabList.Visibility != Visibility.Visible;

        if (restoreNormalTabView)
        {
            SearchBox.Text = string.Empty;
            _isSearchActive = false;
            TabList.Visibility = Visibility.Visible;
            ClearSearchResultContainers();
        }

        TabList.SelectedIndex = index;
        SettingsContent.Children.Clear();
        if (index < _tabPages.Count)
            AddSettingsContentChild(_tabPages[index]);
    }

    /// <summary>Navigate to a tab by its header text (case-insensitive). Because tabs are sorted
    /// alphabetically after building, callers that only know a tab's name (e.g. "AI") should use this
    /// instead of a hard-coded index. Safe to call before the deferred settings content is built.</summary>
    public void SelectTabByHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return;

        if (!_settingsContentBuilt)
        {
            _pendingSelectTabHeader = header;
            return;
        }

        int index = _tabHeaders.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            SelectTab(index);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        await _viewModel.PersistSettingsAsync();
        MarkSettingsClean();
    }

    private void AttachSettingDirtyHandlers()
    {
        foreach (var page in _tabPages)
            AttachSettingDirtyHandlers(page);
    }

    private void AttachSettingDirtyHandlers(UIElement element)
    {
        if (!_dirtyTrackedElements.Add(element))
            return;

        switch (element)
        {
            case TextBox textBox:
                textBox.TextChanged += (_, _) => MarkSettingsDirty();
                break;
            case NumberBox numberBox:
                numberBox.ValueChanged += (_, _) => MarkSettingsDirty();
                ApplyNumericInputMaxLength(numberBox);
                break;
            case ComboBox comboBox:
                comboBox.SelectionChanged += (_, _) => MarkSettingsDirty();
                break;
            case CheckBox checkBox:
                checkBox.Checked += (_, _) => MarkSettingsDirty();
                checkBox.Unchecked += (_, _) => MarkSettingsDirty();
                break;
            case ToggleSwitch toggleSwitch:
                toggleSwitch.Toggled += (_, _) => MarkSettingsDirty();
                break;
            case CalendarDatePicker calendarDatePicker:
                calendarDatePicker.DateChanged += (_, _) => MarkSettingsDirty();
                break;
            case ColorPicker:
                break;
        }

        if (element is Border { Child: UIElement child })
            AttachSettingDirtyHandlers(child);
        else if (element is Panel panel)
            foreach (var childElement in panel.Children)
                AttachSettingDirtyHandlers(childElement);
    }

    // Digit count of Int32.MaxValue (2,147,483,647). Numeric setting boxes are backed by integer
    // (or small double) settings, so capping the editable text at 10 characters lets a user type any
    // value across the full range of the backing type while blocking absurd over-long input. Only
    // typed/pasted input is limited — NumberBox still displays programmatically-set values in full.
    private const int NumericSettingMaxInputLength = 10;

    // NumberBox hosts its editable text in a templated inner TextBox, so the max length can only be
    // applied once the control template is realized (after the control loads into the visual tree).
    private static void ApplyNumericInputMaxLength(NumberBox numberBox)
    {
        void Apply()
        {
            if (FindFirstDescendantTextBox(numberBox) is { } inputBox)
                inputBox.MaxLength = NumericSettingMaxInputLength;
        }

        if (numberBox.IsLoaded)
            Apply();
        else
            numberBox.Loaded += (_, _) => Apply();
    }

    private static TextBox? FindFirstDescendantTextBox(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject childObject = VisualTreeHelper.GetChild(root, i);
            if (childObject is TextBox textBox)
                return textBox;

            if (FindFirstDescendantTextBox(childObject) is { } nested)
                return nested;
        }

        return null;
    }

    private void MarkSettingsDirty(bool requireValueChanges = true)
    {
        if (!_settingDirtyTrackingEnabled || _settingsDirty)
            return;

        if (requireValueChanges && !HaveTrackedSettingValueChanges())
            return;

        _settingsDirty = true;
        SaveButton.IsEnabled = true;
    }

    private void MarkSettingsClean()
    {
        _settingsDirty = false;
        SaveButton.IsEnabled = false;
        CaptureCleanSettingValues();
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs e)
    {
        _suppressOwnerHideToTray?.Invoke();
        StopDeferredSettingsContentBuildTimer();
        RestoreUnsavedSettingsIfNeeded();
        _appWindow.Closing -= OnSettingsAppWindowClosing;
        _fontContrastCheckTimer?.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void RestoreUnsavedSettingsIfNeeded()
    {
        if (!HasSettingValueChanges())
            return;

        _settingDirtyTrackingEnabled = false;
        try
        {
            foreach (var (element, cleanValue) in _cleanSettingValues.ToArray())
                RestoreSettingValue(element, cleanValue);
            foreach (var lazyColor in _lazyColorSettings)
                lazyColor.RestoreColor(lazyColor.CleanColor);
            RestoreCleanFirstTimeIntroductoryTooltipValues();
        }
        finally
        {
            _settingsDirty = false;
            _settingDirtyTrackingEnabled = true;
        }
    }

    private bool HasSettingValueChanges()
    {
        if (_settingsDirty)
            return true;

        return HaveTrackedSettingValueChanges();
    }

    private bool HaveTrackedSettingValueChanges()
    {
        foreach (var element in _dirtyTrackedElements)
        {
            if (!TryGetSettingValue(element, out var currentValue))
                continue;
            if (!_cleanSettingValues.TryGetValue(element, out var cleanValue)
                || !Equals(cleanValue, currentValue))
            {
                return true;
            }
        }

        foreach (var lazyColor in _lazyColorSettings)
        {
            if (!lazyColor.GetColor().Equals(lazyColor.CleanColor))
                return true;
        }

        return HaveFirstTimeIntroductoryTooltipValuesChanged();
    }

    private void OnSettingsContentPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideSettingEditor(source))
            return;

        SettingsContentScrollViewer.Focus(FocusState.Pointer);
        SettingsContentScrollViewer.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MarkSettingsDirtyIfCurrentValuesChanged);
    }

    private static bool IsInsideSettingEditor(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox
                or NumberBox
                or ComboBox
                or CheckBox
                or RadioButton
                or ToggleSwitch
                or CalendarDatePicker
                or ColorPicker
                or Slider
                or Button
                or HyperlinkButton)
            {
                return true;
            }
        }

        return false;
    }

    private void CaptureCleanSettingValues()
    {
        _cleanSettingValues.Clear();
        foreach (var element in _dirtyTrackedElements)
        {
            if (TryGetSettingValue(element, out var value))
                _cleanSettingValues[element] = value;
        }

        foreach (var lazyColor in _lazyColorSettings)
            lazyColor.CleanColor = lazyColor.GetColor();

        CaptureCleanFirstTimeIntroductoryTooltipValues();
    }

    private void CaptureCleanFirstTimeIntroductoryTooltipValues()
    {
        _cleanHasShownFileDrawerIntroTip = _viewModel.HasShownFileDrawerIntroTip;
        _cleanHasShownFileDrawerLineNumberIntroTip = _viewModel.HasShownFileDrawerLineNumberIntroTip;
        _cleanHasShownPreviewMatchIntroTip = _viewModel.HasShownPreviewMatchIntroTip;
    }

    private bool HaveFirstTimeIntroductoryTooltipValuesChanged()
        => _viewModel.HasShownFileDrawerIntroTip != _cleanHasShownFileDrawerIntroTip
           || _viewModel.HasShownFileDrawerLineNumberIntroTip != _cleanHasShownFileDrawerLineNumberIntroTip
           || _viewModel.HasShownPreviewMatchIntroTip != _cleanHasShownPreviewMatchIntroTip;

    private void RestoreCleanFirstTimeIntroductoryTooltipValues()
        => _viewModel.RestoreFirstTimeIntroductoryTooltips(
            _cleanHasShownFileDrawerIntroTip,
            _cleanHasShownFileDrawerLineNumberIntroTip,
            _cleanHasShownPreviewMatchIntroTip);

    private static bool IsFirstTimeIntroductoryTooltipProperty(string? propertyName)
        => propertyName is nameof(MainViewModel.HasShownFileDrawerIntroTip)
            or nameof(MainViewModel.HasShownFileDrawerLineNumberIntroTip)
            or nameof(MainViewModel.HasShownPreviewMatchIntroTip);

    private bool AreFirstTimeIntroductoryTooltipsReset()
        => !_viewModel.HasShownFileDrawerIntroTip
           && !_viewModel.HasShownFileDrawerLineNumberIntroTip
           && !_viewModel.HasShownPreviewMatchIntroTip;

    private void MarkSettingsDirtyIfCurrentValuesChanged()
    {
        if (!_settingDirtyTrackingEnabled || _settingsDirty)
            return;

        if (HaveTrackedSettingValueChanges())
            MarkSettingsDirty();
    }

    private static bool TryGetSettingValue(UIElement element, out object? value)
    {
        switch (element)
        {
            case TextBox textBox:
                value = textBox.Text ?? string.Empty;
                return true;
            case NumberBox numberBox:
                // Capture only the committed numeric Value. NumberBox.Text is formatted
                // asynchronously after the control template loads, so comparing it would
                // report a spurious change when Settings is closed without any edits.
                value = numberBox.Value;
                return true;
            case ComboBox comboBox:
                value = comboBox.SelectedIndex;
                return true;
            case CheckBox checkBox:
                value = checkBox.IsChecked;
                return true;
            case ToggleSwitch toggleSwitch:
                value = toggleSwitch.IsOn;
                return true;
            case CalendarDatePicker calendarDatePicker:
                value = calendarDatePicker.Date;
                return true;
            case ColorPicker colorPicker:
                value = colorPicker.Color;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static void RestoreSettingValue(UIElement element, object? value)
    {
        switch (element)
        {
            case TextBox textBox:
                textBox.Text = value as string ?? string.Empty;
                break;
            case NumberBox numberBox when value is double numberValue:
                numberBox.Value = numberValue;
                break;
            case ComboBox comboBox when value is int selectedIndex:
                comboBox.SelectedIndex = selectedIndex;
                break;
            case CheckBox checkBox:
                checkBox.IsChecked = value is bool isChecked ? isChecked : null;
                break;
            case ToggleSwitch toggleSwitch when value is bool isOn:
                toggleSwitch.IsOn = isOn;
                break;
            case CalendarDatePicker calendarDatePicker:
                calendarDatePicker.Date = value is DateTimeOffset date ? date : null;
                break;
            case ColorPicker colorPicker when value is Windows.UI.Color color:
                colorPicker.Color = color;
                break;
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, RootGrid);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Begin();

        QueueDeferredSettingsContentBuild();
    }

    private void ShowSettingsLoadingPlaceholder()
    {
        SearchBox.IsEnabled = false;
        TabList.IsEnabled = false;
        SaveButton.IsEnabled = false;
        SettingsContent.Children.Clear();
        SettingsContent.Children.Add(new TextBlock
        {
            Text = "Loading settings...",
            Opacity = 0.65,
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 0),
        });
    }

    private void QueueDeferredSettingsContentBuild()
    {
        if (_settingsContentBuilt || _deferredSettingsContentBuildTimer is not null)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        timer.Tick += OnDeferredSettingsContentBuildTimerTick;
        _deferredSettingsContentBuildTimer = timer;
        timer.Start();
    }

    private void OnDeferredSettingsContentBuildTimerTick(object? sender, object e)
    {
        StopDeferredSettingsContentBuildTimer();
        BuildDeferredSettingsContent();
    }

    private void StopDeferredSettingsContentBuildTimer()
    {
        var timer = _deferredSettingsContentBuildTimer;
        if (timer is null)
            return;

        timer.Stop();
        timer.Tick -= OnDeferredSettingsContentBuildTimerTick;
        _deferredSettingsContentBuildTimer = null;
    }

    private void BuildDeferredSettingsContent()
    {
        if (_settingsContentBuilt)
            return;

        SettingsContent.Children.Clear();
        BuildSettingsContent();
        AttachSettingDirtyHandlers();
        _settingsContentBuilt = true;

        int selectedIndex = _pendingSelectTabIndex.GetValueOrDefault(0);
        _pendingSelectTabIndex = null;
        // A pending header request wins over a stale index: tabs are sorted alphabetically after they are
        // built, so the caller (who only knows the tab's name, e.g. "AI") can't supply a stable index.
        if (_pendingSelectTabHeader is { } pendingHeader)
        {
            _pendingSelectTabHeader = null;
            int headerIndex = _tabHeaders.FindIndex(h => string.Equals(h, pendingHeader, StringComparison.OrdinalIgnoreCase));
            if (headerIndex >= 0)
                selectedIndex = headerIndex;
        }

        if (selectedIndex < 0 || selectedIndex >= TabList.Items.Count)
            selectedIndex = 0;

        SearchBox.IsEnabled = true;
        TabList.IsEnabled = true;
        TabList.SelectedIndex = selectedIndex;
        SettingsContent.Children.Clear();
        if (selectedIndex < _tabPages.Count)
            AddSettingsContentChild(_tabPages[selectedIndex]);

        MarkSettingsClean();
        _settingDirtyTrackingEnabled = true;
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        await RequestSettingsWindowCloseAsync();
    }

    private async void OnSettingsAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_settingsCloseConfirmed || !HasSettingValueChanges())
            return;

        args.Cancel = true;
        await ShowUnsavedSettingsClosePromptAsync();
    }

    private async Task RequestSettingsWindowCloseAsync()
    {
        if (_settingsCloseConfirmed || !HasSettingValueChanges())
        {
            CloseSettingsWindowWithoutPrompt();
            return;
        }

        await ShowUnsavedSettingsClosePromptAsync();
    }

    private async Task ShowUnsavedSettingsClosePromptAsync()
    {
        if (_settingsClosePromptOpen)
            return;

        _settingsClosePromptOpen = true;
        try
        {
            var result = await YaguDialog.ShowAsync(
                _settingsHwnd,
                new YaguDialogOptions
                {
                    Title = "Unsaved settings",
                    TitleGlyph = "\uE74E", // Save
                    Content = "You have unsaved settings changes. Save them before closing Settings?",
                    PrimaryButtonText = "Save and close",
                    SecondaryButtonText = "Discard changes",
                    CloseButtonText = "Keep editing",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    RequestedTheme = RootGrid.ActualTheme,
                    Width = 600,
                    Height = 260,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                });

            if (result == YaguDialogResult.Primary)
            {
                SaveButton.IsEnabled = false;
                await _viewModel.PersistSettingsAsync();
                MarkSettingsClean();
                CloseSettingsWindowWithoutPrompt();
            }
            else if (result == YaguDialogResult.Secondary)
            {
                RestoreUnsavedSettingsIfNeeded();
                CloseSettingsWindowWithoutPrompt();
            }
        }
        finally
        {
            _settingsClosePromptOpen = false;
        }
    }

    private void CloseSettingsWindowWithoutPrompt()
    {
        _settingsCloseConfirmed = true;
        _suppressOwnerHideToTray?.Invoke();
        Close();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsContentBuilt) return;
        if (_isSearchActive) return;
        int idx = TabList.SelectedIndex;
        SettingsContent.Children.Clear();
        if (idx >= 0 && idx < _tabPages.Count)
            AddSettingsContentChild(_tabPages[idx]);
    }

    private bool _isSearchActive;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingsContentBuilt) return;

        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            // Restore normal tab view.
            _isSearchActive = false;
            TabList.Visibility = Visibility.Visible;
            ClearSearchResultContainers();
            SettingsContent.Children.Clear();

            int idx = TabList.SelectedIndex;
            if (idx >= 0 && idx < _tabPages.Count)
                AddSettingsContentChild(_tabPages[idx]);
            return;
        }

        _isSearchActive = true;
        TabList.Visibility = Visibility.Collapsed;
        EnsureSearchableEntriesExtracted();
        ClearSearchResultContainers();
        SettingsContent.Children.Clear();

        string? lastHeader = null;
        foreach (var entry in _settingEntries)
        {
            if (!entry.Matches(query))
                continue;

            // Show tab header as a group header when it changes.
            if (!string.Equals(lastHeader, entry.TabHeader, StringComparison.Ordinal))
            {
                lastHeader = entry.TabHeader;
                SettingsContent.Children.Add(new TextBlock
                {
                    Text = entry.TabHeader,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 16,
                    Margin = new Thickness(0, 12, 0, 4),
                    Opacity = 0.9,
                });
            }

            var container = CreateSearchResultEntry(entry);
            _searchResultContainers.Add(container);
            SettingsContent.Children.Add(container);
        }

        if (lastHeader is null)
        {
            SettingsContent.Children.Add(new TextBlock
            {
                Text = "No matching settings found.",
                Opacity = 0.6,
                Margin = new Thickness(0, 20, 0, 0),
            });
        }
    }

    private Border CreateSearchResultEntry(SettingEntry entry)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = entry.Label,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!string.IsNullOrWhiteSpace(entry.ControlText))
        {
            content.Children.Add(new TextBlock
            {
                Text = TrimSearchResultText(entry.ControlText),
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var jumpButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new FontIcon
            {
                Glyph = "\uE8A7",
                FontFamily = new XamlFontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
            },
        };
        ToolTipService.SetToolTip(jumpButton, "Open section");
        AutomationProperties.SetName(jumpButton, "Open section");
        jumpButton.Click += (_, _) => JumpToSetting(entry);

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(content);
        Grid.SetColumn(jumpButton, 1);
        row.Children.Add(jumpButton);

        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x08, 0x80, 0x80, 0x80)),
            Child = row,
        };
    }

    private void JumpToSetting(SettingEntry entry)
    {
        SelectTab(entry.TabIndex);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            entry.TargetElement?.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0.15,
            });
        });
    }

    private static string TrimSearchResultText(string text)
    {
        const int maxLength = 220;
        string normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private void ClearSearchResultContainers()
    {
        foreach (var container in _searchResultContainers)
        {
            DetachFromParent(container);
            if (container is Panel panel)
                panel.Children.Clear();
        }
        _searchResultContainers.Clear();
    }

    private void AddSettingsContentChild(UIElement element)
    {
        if (TryDetachForReparent(element))
            SettingsContent.Children.Add(element);
    }

    private static bool TryDetachForReparent(UIElement element)
    {
        if (element is not FrameworkElement frameworkElement)
            return true;

        DetachFromParent(element);
        return frameworkElement.Parent is null;
    }

    private static void DetachFromParent(UIElement element)
    {
        if (element is not FrameworkElement fe || fe.Parent is null)
            return;

        switch (fe.Parent)
        {
            case Panel parentPanel:
                parentPanel.Children.Remove(element);
                break;
            case Border border when ReferenceEquals(border.Child, element):
                border.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, element):
                contentControl.Content = null;
                break;
            case ScrollViewer scrollViewer when ReferenceEquals(scrollViewer.Content, element):
                scrollViewer.Content = null;
                break;
        }
    }

    /// <summary>
    /// After all tabs are built, walks each tab page to extract searchable setting entries.
    /// Groups children into logical entries: a label/heading element followed by its
    /// control(s) and optional description text until the next label/heading.
    /// </summary>
    private void ExtractSearchableEntries()
    {
        _settingEntries.Clear();
        for (int t = 0; t < _tabPages.Count && t < _tabHeaders.Count; t++)
        {
            string tabHeader = _tabHeaders[t];
            if (_tabPages[t] is not StackPanel page) continue;

            var entryElements = new List<UIElement>();
            string? entryLabel = null;
            string? entryDescription = null;

            var children = EnumerateSearchableChildren(page).ToList();
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                string? text = ExtractText(child);
                bool isLabel = IsLabelElement(child);

                if (isLabel && entryElements.Count > 0 && entryLabel is not null)
                {
                    // Flush previous entry.
                    var captured = new List<UIElement>(entryElements);
                    string capturedLabel = entryLabel;
                    string? capturedDesc = entryDescription;
                    string capturedControlText = ExtractControlSearchText(captured);
                    string capturedTab = tabHeader;
                    _settingEntries.Add(new SettingEntry
                    {
                        TabHeader = capturedTab,
                        TabIndex = t,
                        Label = capturedLabel,
                        Description = capturedDesc,
                        ControlText = capturedControlText,
                        TargetElement = captured.FirstOrDefault(IsLabelElement) ?? captured.FirstOrDefault(),
                    });
                    entryElements.Clear();
                    entryDescription = null;
                }

                if (isLabel)
                    entryLabel = text ?? "Setting";

                entryElements.Add(child);

                // Capture description text (small font TextBlock that follows a control).
                if (!isLabel && child is TextBlock tb && tb.FontSize <= 12 && tb.Opacity < 1.0)
                    entryDescription = (entryDescription is null ? "" : entryDescription + " ") + (text ?? "");
            }

            // Flush last entry.
            if (entryElements.Count > 0 && entryLabel is not null)
            {
                var captured = new List<UIElement>(entryElements);
                string capturedLabel = entryLabel;
                string? capturedDesc = entryDescription;
                string capturedControlText = ExtractControlSearchText(captured);
                string capturedTab = tabHeader;
                _settingEntries.Add(new SettingEntry
                {
                    TabHeader = capturedTab,
                    TabIndex = t,
                    Label = capturedLabel,
                    Description = capturedDesc,
                    ControlText = capturedControlText,
                    TargetElement = captured.FirstOrDefault(IsLabelElement) ?? captured.FirstOrDefault(),
                });
            }
        }
    }

    private void EnsureSearchableEntriesExtracted()
    {
        if (_searchableEntriesExtracted)
            return;

        ExtractSearchableEntries();
        _searchableEntriesExtracted = true;
    }

    private static IEnumerable<UIElement> EnumerateSearchableChildren(Panel parent)
    {
        foreach (var child in parent.Children)
        {
            if (child is Border { Child: UIElement groupChild })
            {
                foreach (var nested in EnumerateSearchableGroupChild(groupChild))
                    yield return nested;
                continue;
            }

            yield return child;
        }
    }

    private static IEnumerable<UIElement> EnumerateSearchableGroupChild(UIElement element)
    {
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                foreach (var nested in EnumerateSearchableGroupChild(child))
                    yield return nested;
            }
            yield break;
        }

        yield return element;
    }

    private static bool IsLabelElement(UIElement element)
    {
        if (element is TextBlock tb && tb.FontSize >= 13 && tb.Opacity >= 0.9)
            return true;
        if (element is StackPanel sp && sp.Orientation == Orientation.Horizontal)
        {
            // NextSearchLabel pattern: horizontal StackPanel with TextBlock + icon.
            foreach (var c in sp.Children)
                if (c is TextBlock) return true;
        }
        return false;
    }

    private static string? ExtractText(UIElement element)
    {
        if (element is TextBlock tb)
            return tb.Text;
        if (element is CheckBox cb)
            return ExtractContentText(cb.Content);
        if (element is ToggleSwitch ts)
            return (ExtractContentText(ts.OnContent) ?? "") + " " + (ExtractContentText(ts.OffContent) ?? "");
        if (element is StackPanel sp)
        {
            // NextSearchLabel or similar composite label.
            foreach (var c in sp.Children)
            {
                if (c is TextBlock innerTb) return innerTb.Text;
            }
        }
        return null;
    }

    private static string ExtractControlSearchText(IEnumerable<UIElement> elements)
    {
        var parts = new List<string>();
        foreach (var element in elements)
            CollectControlSearchText(element, parts);

        return string.Join(' ', parts);
    }

    private static void CollectControlSearchText(UIElement element, List<string> parts)
    {
        switch (element)
        {
            case TextBlock textBlock:
                AddSearchTextPart(parts, textBlock.Text);
                break;
            case TextBox textBox:
                AddSearchTextPart(parts, textBox.Text);
                AddSearchTextPart(parts, textBox.PlaceholderText);
                break;
            case NumberBox numberBox:
                AddSearchTextPart(parts, numberBox.Text);
                AddSearchTextPart(parts, numberBox.PlaceholderText);
                if (!double.IsNaN(numberBox.Value))
                    AddSearchTextPart(parts, numberBox.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case ComboBox comboBox:
                AddSearchTextPart(parts, comboBox.PlaceholderText);
                CollectObjectSearchText(comboBox.SelectedItem, parts);
                if (comboBox.Items.Count <= MaxComboBoxItemsIndexedForSettingsSearch)
                {
                    foreach (var item in comboBox.Items)
                        CollectObjectSearchText(item, parts);
                }
                break;
            case ToggleSwitch toggleSwitch:
                CollectObjectSearchText(toggleSwitch.OnContent, parts);
                CollectObjectSearchText(toggleSwitch.OffContent, parts);
                break;
            case ContentControl contentControl:
                CollectObjectSearchText(contentControl.Content, parts);
                break;
            case Border { Child: UIElement child }:
                CollectControlSearchText(child, parts);
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    CollectControlSearchText(child, parts);
                break;
        }
    }

    private static void CollectObjectSearchText(object? value, List<string> parts)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                AddSearchTextPart(parts, text);
                return;
            case UIElement element:
                CollectControlSearchText(element, parts);
                return;
            case char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                AddSearchTextPart(parts, Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }

        if (value.GetType().IsEnum)
            AddSearchTextPart(parts, value.ToString());
    }

    private static void AddSearchTextPart(List<string> parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(text.Trim());
    }

    private static string? ExtractContentText(object? content)
    {
        if (content is string s) return s;
        if (content is TextBlock tb) return tb.Text;
        if (content is StackPanel sp)
        {
            foreach (var c in sp.Children)
                if (c is TextBlock innerTb) return innerTb.Text;
        }
        return content?.ToString();
    }

    private StackPanel AddTab(string header)
    {
        var page = new StackPanel { Spacing = 8 };
        _tabPages.Add(page);
        _tabHeaders.Add(header);

        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        item.Children.Add(new FontIcon
        {
            Glyph = SettingsGroupIcon(header),
            FontSize = 15,
        });
        item.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        TabList.Items.Add(item);
        return page;
    }

    /// <summary>
    /// Reorders the built tab pages, their headers, and the visible tab-rail items into
    /// case-insensitive alphabetical order by header. Called once after all tabs are added so the
    /// settings tabs always display A–Z. Keeps <see cref="_tabPages"/>, <see cref="_tabHeaders"/>,
    /// and TabList.Items in lockstep so positional tab indices (search navigation, deep links)
    /// stay consistent with what the user sees.
    /// </summary>
    private void SortTabsAlphabetically()
    {
        var order = Enumerable.Range(0, _tabPages.Count)
            .OrderBy(i => _tabHeaders[i], StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sortedPages = order.Select(i => _tabPages[i]).ToList();
        var sortedHeaders = order.Select(i => _tabHeaders[i]).ToList();
        var sortedItems = order.Select(i => TabList.Items[i]).ToList();

        _tabPages.Clear();
        _tabPages.AddRange(sortedPages);
        _tabHeaders.Clear();
        _tabHeaders.AddRange(sortedHeaders);

        TabList.Items.Clear();
        foreach (var item in sortedItems)
            TabList.Items.Add(item);
    }

    private void QueueHotkeyAvailabilityLoad(CheckBox hotkey, ComboBox hotkeyCombo, TextBlock availabilityStatus)
    {
        var hwnd = _mainHwnd;

        // The probe (RegisterHotKey/UnregisterHotKey for each Ctrl+Shift+letter) MUST run on the UI
        // thread that owns the main window. Win32 RegisterHotKey "cannot associate a hot key with a
        // window created by another thread", so probing from a background thread pool thread fails for
        // EVERY letter and makes it look like no combinations are available at all. The settings
        // window lives on the same UI thread as the main window, so its DispatcherQueue owns _mainHwnd.
        // The 26 register/unregister calls are fast; enqueue at low priority so building the tab
        // stays responsive while still running on the correct thread.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            IReadOnlyList<char> availableKeys;
            try
            {
                availableKeys = _hotkeyService.GetAvailableCtrlShiftLetterKeys(hwnd);
            }
            catch
            {
                availabilityStatus.Text = "Unable to check available shortcuts right now.";
                availabilityStatus.Visibility = Visibility.Visible;
                return;
            }

            ApplyHotkeyAvailability(availableKeys, hotkey, hotkeyCombo, availabilityStatus);
        });
    }

    private void ApplyHotkeyAvailability(IReadOnlyList<char> availableHotkeyKeys, CheckBox hotkey, ComboBox hotkeyCombo, TextBlock availabilityStatus)
    {
        bool wasDirtyTrackingEnabled = _settingDirtyTrackingEnabled;
        _settingDirtyTrackingEnabled = false;

        try
        {
            var selectedHotkeyKey = HotkeyService.ChooseAvailableKey(availableHotkeyKeys, _viewModel.GlobalHotkeyKey);
            if (selectedHotkeyKey is char selectedKey && !string.Equals(_viewModel.GlobalHotkeyKey, selectedKey.ToString(), StringComparison.OrdinalIgnoreCase))
                _viewModel.GlobalHotkeyKey = selectedKey.ToString();

            hotkey.IsEnabled = availableHotkeyKeys.Count > 0;
            hotkeyCombo.IsEnabled = availableHotkeyKeys.Count > 0;
            hotkeyCombo.Items.Clear();

            foreach (var key in availableHotkeyKeys)
            {
                hotkeyCombo.Items.Add(new ComboBoxItem
                {
                    Content = HotkeyService.FormatCtrlShift(key),
                    Tag = key.ToString(),
                });
            }

            if (selectedHotkeyKey is char hotkeyKey)
            {
                for (int itemIndex = 0; itemIndex < hotkeyCombo.Items.Count; itemIndex++)
                {
                    if (hotkeyCombo.Items[itemIndex] is ComboBoxItem item &&
                        string.Equals(item.Tag as string, hotkeyKey.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        hotkeyCombo.SelectedIndex = itemIndex;
                        break;
                    }
                }
            }
            else
            {
                hotkeyCombo.Items.Add("No Ctrl+Shift+letter combinations available");
                hotkeyCombo.SelectedIndex = 0;
            }

            availabilityStatus.Text = availableHotkeyKeys.Count == 0
                ? "No Ctrl+Shift+letter combinations are currently available. Close the app using one or change another app's shortcut, then reopen Settings."
                : string.Empty;
            availabilityStatus.Visibility = availableHotkeyKeys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (TryGetSettingValue(hotkey, out var hotkeyValue))
                _cleanSettingValues[hotkey] = hotkeyValue;
            if (TryGetSettingValue(hotkeyCombo, out var hotkeyComboValue))
                _cleanSettingValues[hotkeyCombo] = hotkeyComboValue;
        }
        finally
        {
            _settingDirtyTrackingEnabled = wasDirtyTrackingEnabled;
        }
    }

    private static StackPanel NextSearchLabel(string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = text });
        var icon = new FontIcon { Glyph = "\uE72C", FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(icon, "Takes effect on the next search");
        panel.Children.Add(icon);
        return panel;
    }

    private void AddPreviewContentColorSetting(
        StackPanel parent,
        string label,
        string description,
        string currentHex,
        Windows.UI.Color fallback,
        Action<string> assign,
        bool showContrastStatus = false)
        => AddColorSetting(
            parent,
            label,
            description,
            currentHex,
            fallback,
            assign,
            _applyPreviewSectionBackgrounds,
            showContrastStatus ? ResolveFontContrastTheme : null,
            _fontContrastStatusRefreshers.Add,
            showContrastStatus ? GetPreviewContentContrastStatus : null);

    // Built-in editor body-text color. Unlike other preview colors this supports an "Auto" mode (empty
    // PreviewEditorTextColor) because the editor renders on a theme-colored card, so a single fixed color would
    // be unreadable in one theme. The checkbox is the sole writer of the Auto/override decision and the color row
    // only writes to the view model while the override is enabled, so Cancel/restore reconciles in any order.
    private void AddEditorTextColorSetting(StackPanel parent)
    {
        var overrideCheckBox = new CheckBox
        {
            Content = "Override editor text color (otherwise follows the light/dark theme)",
            IsChecked = !string.IsNullOrWhiteSpace(_viewModel.PreviewEditorTextColor),
            Margin = new Thickness(0, 8, 0, 0),
        };
        parent.Children.Add(overrideCheckBox);

        var colorPanel = new StackPanel();
        string initialHex = string.IsNullOrWhiteSpace(_viewModel.PreviewEditorTextColor)
            ? "#FFFFFFFF"
            : _viewModel.PreviewEditorTextColor;

        LazyColorSettingHandle handle = AddColorSetting(
            colorPanel,
            "Editor text:",
            "Color of body text in the built-in editor. Turn off the override to follow the light/dark theme automatically.",
            initialHex,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            value =>
            {
                if (overrideCheckBox.IsChecked == true)
                    _viewModel.PreviewEditorTextColor = value;
            },
            afterChange: null,
            contrastThemeProvider: ResolveFontContrastTheme,
            registerContrastStatusRefresher: _fontContrastStatusRefreshers.Add);
        parent.Children.Add(colorPanel);

        colorPanel.Visibility = overrideCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        overrideCheckBox.Checked += (_, _) =>
        {
            colorPanel.Visibility = Visibility.Visible;
            _viewModel.PreviewEditorTextColor = ColorStringHelper.ToHex(handle.GetColor());
        };
        overrideCheckBox.Unchecked += (_, _) =>
        {
            colorPanel.Visibility = Visibility.Collapsed;
            _viewModel.PreviewEditorTextColor = string.Empty;
        };
    }

    private readonly record struct ContrastStatus(double Ratio, string Label);

    private static ContrastStatus GetThemeSampleContrastStatus(Windows.UI.Color color, FontContrastTheme theme)
    {
        var background = FontContrastWarningService.GetThemeSampleBackground(theme);
        double ratio = FontContrastWarningService.GetContrastRatio(ToFontContrastColor(color), background);
        return new ContrastStatus(ratio, "Contrast ratio");
    }

    private ContrastStatus GetPreviewContentContrastStatus(Windows.UI.Color color, FontContrastTheme theme)
    {
        var foreground = ToFontContrastColor(color);
        var selectedBackground = ResolvePreviewContentBackground(
            _viewModel.SelectedPreviewContentBackgroundColor,
            FontContrastColor.FromArgb(0xFF, 0x00, 0x00, 0x00),
            theme);
        var unselectedBackground = ResolvePreviewContentBackground(
            _viewModel.UnselectedPreviewContentBackgroundColor,
            FontContrastColor.FromArgb(0x00, 0x00, 0x00, 0x00),
            theme);
        double selectedRatio = FontContrastWarningService.GetContrastRatio(foreground, selectedBackground);
        double unselectedRatio = FontContrastWarningService.GetContrastRatio(foreground, unselectedBackground);

        return selectedRatio <= unselectedRatio
            ? new ContrastStatus(selectedRatio, "Selected preview contrast")
            : new ContrastStatus(unselectedRatio, "Unselected preview contrast");
    }

    private static FontContrastColor ResolvePreviewContentBackground(string value, FontContrastColor fallback, FontContrastTheme theme)
        => FontContrastWarningService.ResolveBackground(FontContrastColor.Parse(value, fallback), theme);

    private static ComboBox CreateFontFamilyPicker(string currentValue, string defaultValue, Action<string> assign)
    {
        var picker = new ComboBox
        {
            PlaceholderText = defaultValue,
            MinWidth = 280,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        string selectedValue = string.IsNullOrWhiteSpace(currentValue)
            ? defaultValue.Trim()
            : currentValue.Trim();
        ComboBoxItem? selectedItem = null;

        foreach (string fontFamily in BuildInitialFontFamilyOptions(selectedValue, defaultValue))
        {
            var item = CreateFontFamilyItem(fontFamily);
            picker.Items.Add(item);

            if (string.Equals(fontFamily, selectedValue, StringComparison.OrdinalIgnoreCase))
                selectedItem = item;
        }

        picker.SelectedItem = selectedItem;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string fontFamily })
                assign(fontFamily);
        };

        bool systemFontsLoaded = false;
        picker.DropDownOpened += (_, _) =>
        {
            if (systemFontsLoaded)
                return;

            systemFontsLoaded = true;
            PopulateFontFamilyPicker(picker, defaultValue);
        };

        return picker;
    }

    private static void PopulateFontFamilyPicker(ComboBox picker, string defaultValue)
    {
        string selectedValue = picker.SelectedItem is ComboBoxItem { Tag: string selectedFontFamily }
            ? selectedFontFamily
            : defaultValue.Trim();

        foreach (string fontFamily in BuildFontFamilyOptions(selectedValue, defaultValue))
        {
            if (FindFontFamilyItem(picker, fontFamily) is null)
                picker.Items.Add(CreateFontFamilyItem(fontFamily));
        }

        SelectFontFamily(picker, selectedValue);
    }

    private static void SelectFontFamily(ComboBox picker, string fontFamily)
    {
        string normalized = fontFamily.Trim();
        ComboBoxItem? item = FindFontFamilyItem(picker, normalized);

        if (item is null)
        {
            item = CreateFontFamilyItem(normalized);
            picker.Items.Insert(0, item);
        }

        picker.SelectedItem = item;
    }

    private static ComboBoxItem? FindFontFamilyItem(ComboBox picker, string fontFamily)
        => picker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => candidate.Tag is string candidateFontFamily
                && string.Equals(candidateFontFamily, fontFamily, StringComparison.OrdinalIgnoreCase));

    private static ComboBoxItem CreateFontFamilyItem(string fontFamily)
    {
        var row = new Grid
        {
            ColumnSpacing = 16,
            MinWidth = 260,
            MaxWidth = 480,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = fontFamily,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var sample = new TextBlock
        {
            Text = "AaBbCc 123",
            FontFamily = new XamlFontFamily(fontFamily),
            FontSize = 14,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sample, 1);
        row.Children.Add(sample);
        ToolTipService.SetToolTip(row, fontFamily);

        return new ComboBoxItem
        {
            Content = row,
            Tag = fontFamily,
            MinHeight = 32,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static List<string> BuildFontFamilyOptions(string currentValue, string defaultValue)
    {
        var options = BuildInitialFontFamilyOptions(currentValue, defaultValue);

        foreach (string fontFamily in GetSystemFontFamilyNames())
            AddFontFamilyOption(options, fontFamily);

        return options;
    }

    private static List<string> BuildInitialFontFamilyOptions(string currentValue, string defaultValue)
    {
        var options = new List<string>();
        AddFontFamilyOption(options, currentValue);
        AddFontFamilyOption(options, defaultValue);

        return options;
    }

    private static void AddFontFamilyOption(List<string> options, string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
            return;

        string normalized = fontFamily.Trim();
        if (!options.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            options.Add(normalized);
    }

    private static IReadOnlyList<string> GetSystemFontFamilyNames()
    {
        if (_systemFontFamilyNames is not null)
            return _systemFontFamilyNames;

        try
        {
            _systemFontFamilyNames = CanvasTextFormat.GetSystemFontFamilies()
                .Where(fontFamily => !string.IsNullOrWhiteSpace(fontFamily))
                .Select(fontFamily => fontFamily.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(fontFamily => fontFamily, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            _systemFontFamilyNames =
            [
                AppSettings.DefaultPreviewTextFontFamily,
                AppSettings.DefaultResultListMatchTextFontFamily,
                "Cascadia Mono",
                "Courier New",
                "Segoe UI",
                "Arial",
            ];
        }

        return _systemFontFamilyNames;
    }

    private LazyColorSettingHandle AddColorSetting(
        StackPanel parent,
        string label,
        string description,
        string currentHex,
        Windows.UI.Color fallback,
        Action<string> assign,
        Action? afterChange = null,
        Func<FontContrastTheme>? contrastThemeProvider = null,
        Action<Action>? registerContrastStatusRefresher = null,
        Func<Windows.UI.Color, FontContrastTheme, ContrastStatus>? contrastStatusProvider = null)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var selectedColor = ColorStringHelper.Parse(currentHex, fallback);

        var colorSwatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
            Background = new SolidColorBrush(selectedColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var colorText = new TextBlock
        {
            Text = ColorStringHelper.ToHex(selectedColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var buttonContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        buttonContent.Children.Add(colorSwatch);
        buttonContent.Children.Add(colorText);

        var editColorButton = new Button
        {
            Content = buttonContent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
        };
        parent.Children.Add(editColorButton);

        ColorPicker? picker = null;
        bool suppressColorDirty = false;

        Windows.UI.Color currentColor() => picker?.Color ?? selectedColor;

        void refreshColorButton()
        {
            colorSwatch.Background = new SolidColorBrush(selectedColor);
            colorText.Text = ColorStringHelper.ToHex(selectedColor);
        }

        FontIcon? contrastIcon = null;
        TextBlock? contrastText = null;
        void refreshContrastStatus()
        {
            if (contrastThemeProvider is null || contrastIcon is null || contrastText is null)
                return;

            var theme = contrastThemeProvider();
            var color = currentColor();
            var status = contrastStatusProvider?.Invoke(color, theme)
                ?? GetThemeSampleContrastStatus(color, theme);
            double ratio = status.Ratio;
            bool readable = ratio >= FontContrastWarningService.MinimumReadableContrastRatio;
            var statusColor = readable ? ContrastReadableGreen : ContrastUnreadableRed;
            contrastIcon.Glyph = readable ? "\uE73E" : "\uE711";
            contrastIcon.Foreground = new SolidColorBrush(statusColor);
            contrastText.Foreground = new SolidColorBrush(statusColor);
            contrastText.Text = string.IsNullOrWhiteSpace(status.Label)
                ? $"Contrast ratio: {ratio:F1}:1"
                : $"{status.Label}: {ratio:F1}:1";
        }

        void setColor(Windows.UI.Color color, bool markDirty)
        {
            selectedColor = color;
            assign(ColorStringHelper.ToHex(color));
            afterChange?.Invoke();
            refreshColorButton();
            if (markDirty)
                MarkSettingsDirty();
            refreshContrastStatus();
        }

        var colorState = new LazyColorSettingState
        {
            GetColor = currentColor,
            RestoreColor = color =>
            {
                suppressColorDirty = true;
                try
                {
                    if (picker is not null)
                        picker.Color = color;
                    setColor(color, markDirty: false);
                }
                finally
                {
                    suppressColorDirty = false;
                }
            },
            CleanColor = selectedColor,
        };
        _lazyColorSettings.Add(colorState);
        var colorHandle = new LazyColorSettingHandle
        {
            GetColor = currentColor,
            SetColor = color =>
            {
                if (picker is not null)
                    picker.Color = color;
                else
                    setColor(color, markDirty: true);
            },
        };

        editColorButton.Click += (_, _) =>
        {
            if (picker is null)
            {
                picker = new ColorPicker
                {
                    Color = selectedColor,
                    IsAlphaEnabled = true,
                    Width = 160,
                    MaxWidth = 180,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                picker.ColorChanged += (_, args) => setColor(args.NewColor, markDirty: !suppressColorDirty);
                editColorButton.Flyout = new Flyout { Content = picker };
            }
            else
            {
                picker.Color = selectedColor;
            }

            editColorButton.Flyout.ShowAt(editColorButton);
        };

        if (contrastThemeProvider is not null)
        {
            contrastIcon = new FontIcon
            {
                FontSize = 13,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center,
            };
            contrastText = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var contrastRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            contrastRow.Children.Add(contrastIcon);
            contrastRow.Children.Add(contrastText);
            parent.Children.Add(contrastRow);
            refreshContrastStatus();
            registerContrastStatusRefresher?.Invoke(refreshContrastStatus);
        }

        var reset = new Button
        {
            Content = "Reset",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
        };
        reset.Click += (_, _) =>
        {
            if (picker is not null)
                picker.Color = fallback;
            else
                setColor(fallback, markDirty: true);
        };
        RegisterDefaultResetButton(reset, () => currentColor().Equals(fallback));
        parent.Children.Add(reset);

        parent.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        return colorHandle;
    }

    private static FontContrastColor ToFontContrastColor(Windows.UI.Color color)
        => FontContrastColor.FromArgb(color.A, color.R, color.G, color.B);

    private static StackPanel AddSettingsGroupBox(StackPanel parent, string title)
    {
        var body = new StackPanel { Spacing = 8 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
        });
        content.Children.Add(body);

        parent.Children.Add(new Border
        {
            Margin = new Thickness(0, 8, 0, 4),
            Padding = new Thickness(14, 12, 14, 14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = GetSettingsGroupBrush("ControlStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            Background = GetSettingsGroupBrush("LayerFillColorDefaultBrush", Windows.UI.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Child = content,
        });

        return body;
    }

    private static Brush GetSettingsGroupBrush(string resourceKey, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object resource) && resource is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private static string SettingsGroupIcon(string header) => header switch
    {
        "Search Defaults" => "\uE721",
        "Search Limits" => "\uE74C",
        "OCR" => "\uE7C5",
        "Performance" => "\uE9F5",
        "Display" => "\uE7B5",
        "Editor" => "\uE70F",
        "Window" => "\uE737",
        "Interaction" => "\uE7C4",
        "Terminal Emulator" => "\uE756",
        "Developer Options" => "\uE713",
        "Shortcuts & History" => "\uE765",
        "AI" => "\uE99A",
        _ => "\uE7FC",
    };

    private void AddResultTempDriveSetting(StackPanel parent)
    {
        // GetLaunchDriveRoot() is cheap (no disk readiness/free-space probing), so it is safe to run inline.
        string? launchDrive = ResultStoreTempLocationService.GetLaunchDriveRoot();

        parent.Children.Add(NextSearchLabel("Search result temp-file drive:"));
        parent.Children.Add(new TextBlock
        {
            Text = $"Yagu was launched from {launchDrive ?? "an unknown drive"}. Choosing a different drive can reduce disk contention. Only writable drives with at least 50 GB free are listed.",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var picker = new ComboBox
        {
            MinWidth = 340,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };
        picker.Items.Add(new ComboBoxItem { Content = "Detecting eligible drives\u2026" });
        picker.SelectedIndex = 0;
        parent.Children.Add(picker);

        var status = new TextBlock
        {
            Text = "Temp files will be written under the selected drive's Temp\\Yagu folder starting with the next search.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
        parent.Children.Add(status);

        // Enumerating writable drives probes drive readiness, free space, and write access (creating a probe
        // file per eligible drive). That synchronous disk I/O blocked the Settings build and made the window
        // open extremely slowly, so it now runs off the UI thread and populates the picker when complete.
        QueueResultTempDriveLoad(picker, status, launchDrive);
    }

    private void QueueResultTempDriveLoad(ComboBox picker, TextBlock status, string? launchDrive)
    {
        _ = Task.Run(() => ResultStoreTempLocationService.GetWritableDriveOptions(launchDrive))
            .ContinueWith(task =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    IReadOnlyList<ResultStoreTempDriveOption> options = task.IsCompletedSuccessfully
                        ? task.Result
                        : Array.Empty<ResultStoreTempDriveOption>();
                    PopulateResultTempDriveOptions(picker, status, options, launchDrive);
                });
            });
    }

    private void PopulateResultTempDriveOptions(
        ComboBox picker,
        TextBlock status,
        IReadOnlyList<ResultStoreTempDriveOption> options,
        string? launchDrive)
    {
        bool wasDirtyTrackingEnabled = _settingDirtyTrackingEnabled;
        _settingDirtyTrackingEnabled = false;

        try
        {
            picker.Items.Clear();

            if (options.Count == 0)
            {
                picker.IsEnabled = false;
                picker.Items.Add(new ComboBoxItem { Content = "No eligible drive (50 GB free, writable) found" });
                picker.SelectedIndex = 0;
                status.Text = "No writable drive with at least 50 GB free is currently available. Yagu will use the Windows temp folder until an eligible drive is selected.";
                if (TryGetSettingValue(picker, out var emptyValue))
                    _cleanSettingValues[picker] = emptyValue;
                return;
            }

            ResultStoreTempDriveOption? selectedOption = ResultStoreTempLocationService.ChoosePreferredOption(
                options,
                _viewModel.SearchResultTempDirectory,
                launchDrive);

            for (int i = 0; i < options.Count; i++)
            {
                picker.Items.Add(new ComboBoxItem
                {
                    Content = options[i].DisplayName,
                    Tag = options[i],
                });

                if (Equals(options[i], selectedOption))
                    picker.SelectedIndex = i;
            }

            if (picker.SelectedIndex < 0 && picker.Items.Count > 0)
                picker.SelectedIndex = 0;

            picker.IsEnabled = true;
            picker.SelectionChanged += (_, _) => ApplyResultTempDriveSelection(picker);
            ApplyResultTempDriveSelection(picker);

            status.Text = "Temp files will be written under the selected drive's Temp\\Yagu folder starting with the next search.";

            if (TryGetSettingValue(picker, out var pickerValue))
                _cleanSettingValues[picker] = pickerValue;
        }
        finally
        {
            _settingDirtyTrackingEnabled = wasDirtyTrackingEnabled;
        }
    }

    private void ApplyResultTempDriveSelection(ComboBox picker)
    {
        if (picker.SelectedItem is ComboBoxItem item && item.Tag is ResultStoreTempDriveOption option)
        {
            _viewModel.SearchResultTempDirectory = option.TempDirectory;
            _viewModel.HasChosenSearchResultTempDirectory = true;
        }
    }

    private void AddTerminalEmulationSetting(StackPanel parent)
    {
        parent.Children.Add(new TextBlock { Text = "Default working directory:" });

        var workingDirectory = new TextBox
        {
            Text = _viewModel.TerminalDefaultWorkingDirectory,
            PlaceholderText = App.LaunchWorkingDirectory,
            Width = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        workingDirectory.TextChanged += (_, _) => _viewModel.TerminalDefaultWorkingDirectory = workingDirectory.Text;
        parent.Children.Add(workingDirectory);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        var browse = new Button
        {
            Content = "Browse...",
            Padding = new Thickness(10, 4, 10, 4),
        };
        browse.Click += (_, _) => PickTerminalWorkingDirectory(workingDirectory);
        buttonRow.Children.Add(browse);

        var useDefault = new Button
        {
            Content = "Use default",
            Padding = new Thickness(10, 4, 10, 4),
        };
        useDefault.Click += (_, _) =>
        {
            workingDirectory.Text = string.Empty;
            _viewModel.TerminalDefaultWorkingDirectory = string.Empty;
        };
        RegisterDefaultResetButton(useDefault, () => SettingStringEquals(_viewModel.TerminalDefaultWorkingDirectory, string.Empty));
        buttonRow.Children.Add(useDefault);
        parent.Children.Add(buttonRow);

        parent.Children.Add(new TextBlock
        {
            Text = $"Leave blank to use the Yagu launch directory: {App.LaunchWorkingDirectory}",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void AddTerminalShellSetting(StackPanel parent)
    {
        parent.Children.Add(new TextBlock { Text = "Default shell for the embedded terminal:" });

        var shell = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        shell.Items.Add("Command Prompt (cmd.exe)");
        shell.Items.Add("PowerShell");
        shell.SelectedIndex = TerminalShell.NormalizeSettingsIndex(_viewModel.TerminalShellKindIndex);
        shell.SelectionChanged += (_, _) => _viewModel.TerminalShellKindIndex = shell.SelectedIndex;
        parent.Children.Add(shell);

        parent.Children.Add(new TextBlock
        {
            Text = "PowerShell is the default. The choice applies the next time the terminal starts; use the Shell dropdown in the terminal toolbar to switch a running session live.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void PickTerminalWorkingDirectory(TextBox target)
    {
        try
        {
            string? folderPath = Win32FileDialog.SelectFolder(_settingsHwnd, "Select Terminal Working Directory");
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            target.Text = folderPath;
            _viewModel.TerminalDefaultWorkingDirectory = folderPath;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Settings", "Terminal working directory browse dialog failed.", ex);
        }
    }

    private void BuildSettingsContent()
    {
        // ── Search Defaults ──
        {
            var g = AddTab("Search Defaults");
            var contextGroup = AddSettingsGroupBox(g, "Match Context");
            var filterGroup = AddSettingsGroupBox(g, "Default Include/Exclude Filters");

            contextGroup.Children.Add(NextSearchLabel("Context lines (lines shown before & after each match in results):"));
            var ctx = new NumberBox { Value = _viewModel.ContextLines, Minimum = 0, Maximum = 50 };
            ctx.ValueChanged += (_, args) => _viewModel.ContextLines = (int)args.NewValue;
            contextGroup.Children.Add(ctx);
            contextGroup.Children.Add(new TextBlock { Text = "Controls the surrounding lines shown under each match in the results list. Set to 0 for match lines only.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            contextGroup.Children.Add(new TextBlock { Text = "Preview context lines (lines shown around each match in the preview panel):" });
            var prevCtx = new NumberBox { Value = _viewModel.PreviewContextLines, Minimum = 0 };
            prevCtx.ValueChanged += (_, args) => _viewModel.PreviewContextLines = (int)args.NewValue;
            contextGroup.Children.Add(prevCtx);
            contextGroup.Children.Add(new TextBlock { Text = "Used when Yagu builds preview snippets around each match. Larger values give more context but render more text.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var includeHeader = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Width = 620 };
            includeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            includeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            includeHeader.Children.Add(NextSearchLabel("Default include patterns (comma/semicolon-separated):"));
            var incMode = new ComboBox
            {
                SelectedIndex = _viewModel.IncludeFilterModeIndex == 1 ? 1 : 0,
                Width = 104,
                MinWidth = 0,
                Padding = new Thickness(8, 0, 8, 0),
            };
            incMode.Items.Add("Glob");
            incMode.Items.Add("Regex");
            ToolTipService.SetToolTip(incMode, "Interpret include patterns as globs or regular expressions");
            Grid.SetColumn(incMode, 1);
            includeHeader.Children.Add(incMode);
            filterGroup.Children.Add(includeHeader);

            var incGlobs = new TextBox { Text = _viewModel.IncludeGlobs, PlaceholderText = _viewModel.IncludeFilterPlaceholder, Width = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
            incGlobs.TextChanged += (_, _) => _viewModel.IncludeGlobs = incGlobs.Text;
            incMode.SelectionChanged += (_, _) =>
            {
                if (incMode.SelectedIndex >= 0)
                    _viewModel.IncludeFilterModeIndex = incMode.SelectedIndex;
                incGlobs.PlaceholderText = _viewModel.IncludeFilterPlaceholder;
            };
            filterGroup.Children.Add(incGlobs);
            filterGroup.Children.Add(new TextBlock { Text = "Leave blank to include every eligible file. Multiple entries can be separated with commas or semicolons.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var excludeHeader = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Width = 620 };
            excludeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            excludeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            excludeHeader.Children.Add(NextSearchLabel("Default exclude patterns (comma/semicolon-separated):"));
            var excMode = new ComboBox
            {
                SelectedIndex = _viewModel.ExcludeFilterModeIndex == 1 ? 1 : 0,
                Width = 104,
                MinWidth = 0,
                Padding = new Thickness(8, 0, 8, 0),
            };
            excMode.Items.Add("Glob");
            excMode.Items.Add("Regex");
            ToolTipService.SetToolTip(excMode, "Interpret exclude patterns as globs or regular expressions");
            Grid.SetColumn(excMode, 1);
            excludeHeader.Children.Add(excMode);
            filterGroup.Children.Add(excludeHeader);

            var excGlobs = new TextBox { Text = _viewModel.ExcludeGlobs, PlaceholderText = _viewModel.ExcludeFilterPlaceholder, Width = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
            excGlobs.TextChanged += (_, _) => _viewModel.ExcludeGlobs = excGlobs.Text;
            excMode.SelectionChanged += (_, _) =>
            {
                if (excMode.SelectedIndex >= 0)
                    _viewModel.ExcludeFilterModeIndex = excMode.SelectedIndex;
                excGlobs.PlaceholderText = _viewModel.ExcludeFilterPlaceholder;
            };
            filterGroup.Children.Add(excGlobs);
            filterGroup.Children.Add(new TextBlock { Text = "Exclude patterns are applied before content scanning, so broad excludes are the cheapest way to skip generated folders or noisy file trees.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var precedenceGroup = AddSettingsGroupBox(g, "Gitignore Conflicts");
            precedenceGroup.Children.Add(NextSearchLabel("When .gitignore and your Include filter conflict:"));
            var precedence = new ComboBox
            {
                Width = 260,
                MinWidth = 0,
                Padding = new Thickness(8, 0, 8, 0),
            };
            precedence.Items.Add("Ask me each time");
            precedence.Items.Add(".gitignore wins");
            precedence.Items.Add("Include filter wins");
            precedence.SelectedIndex = _viewModel.GitignorePrecedencePreference switch
            {
                true => 1,
                false => 2,
                _ => 0,
            };
            precedence.SelectionChanged += (_, _) =>
            {
                _viewModel.GitignorePrecedencePreference = precedence.SelectedIndex switch
                {
                    1 => true,
                    2 => false,
                    _ => (bool?)null,
                };
                MarkSettingsDirty(requireValueChanges: false);
            };
            precedenceGroup.Children.Add(precedence);
            precedenceGroup.Children.Add(new TextBlock { Text = "Controls which side wins when a file is both matched by your Include filter and excluded by .gitignore (only relevant when Obey .gitignore is on). Leave on \"Ask me each time\" to be prompted; the prompt's \"Don't ask again\" option also updates this setting.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Search Limits ──
        {
            var g = AddTab("Search Limits");
            var resultLimitsGroup = AddSettingsGroupBox(g, "Result Caps");
            var defaultFiltersGroup = AddSettingsGroupBox(g, "Size and Date Defaults");
            var pathTypeGroup = AddSettingsGroupBox(g, "Path and File Type Filters");
            var archiveGroup = AddSettingsGroupBox(g, "Archive Search");

            resultLimitsGroup.Children.Add(NextSearchLabel("Max results (0 = unlimited):"));
            var max = new NumberBox { Value = _viewModel.MaxResults, Minimum = 0 };
            max.ValueChanged += (_, args) => _viewModel.MaxResults = (int)args.NewValue;
            resultLimitsGroup.Children.Add(max);
            resultLimitsGroup.Children.Add(new TextBlock { Text = "Stops the search after this many matches. Set to 0 for no limit; the hard ceiling and memory pressure controls still protect against runaway usage.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            resultLimitsGroup.Children.Add(NextSearchLabel("Max results ceiling (hard cap on max results):"));
            var resultsCeiling = new NumberBox { Value = _viewModel.MaxResultsCeiling, Minimum = 1000, Maximum = 10_000_000 };
            resultsCeiling.ValueChanged += (_, args) => _viewModel.MaxResultsCeiling = (int)args.NewValue;
            resultLimitsGroup.Children.Add(resultsCeiling);
            resultLimitsGroup.Children.Add(new TextBlock { Text = "Absolute ceiling for Max Results regardless of per-search setting. Values above this are clamped down. Must be at least 1,000.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            resultLimitsGroup.Children.Add(NextSearchLabel("Absolute results safety limit (0 = unlimited):"));
            var absoluteMax = new NumberBox { Value = _viewModel.AbsoluteMaxResults, Minimum = 0, Maximum = int.MaxValue };
            absoluteMax.ValueChanged += (_, args) => _viewModel.AbsoluteMaxResults = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
            resultLimitsGroup.Children.Add(absoluteMax);
            resultLimitsGroup.Children.Add(new TextBlock { Text = "Hard backstop on total matches that applies even when \"Max results\" is 0 (unlimited). 0 (the default) disables it — no truncation; memory-pressure eviction (results paged to disk) and the per-line cap still protect against runaway usage. Set a positive value to cap an unbounded match-everything search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            defaultFiltersGroup.Children.Add(NextSearchLabel("Default file size filter (MB):"));
            var sizeDefaults = new Grid { ColumnSpacing = 8 };
            sizeDefaults.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sizeDefaults.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var minSizePanel = new StackPanel { Spacing = 4 };
            minSizePanel.Children.Add(new TextBlock { Text = "Minimum MB", FontSize = 12, Opacity = 0.75 });
            var minSize = new NumberBox { Value = _viewModel.DefaultMinFileSizeMB, Minimum = 0, PlaceholderText = "0" };
            minSize.ValueChanged += (_, args) =>
            {
                _viewModel.DefaultMinFileSizeMB = args.NewValue;
                _viewModel.MinFileSizeMB = _viewModel.DefaultMinFileSizeMB;
            };
            minSizePanel.Children.Add(minSize);

            var maxSizePanel = new StackPanel { Spacing = 4 };
            maxSizePanel.Children.Add(new TextBlock { Text = "Maximum MB", FontSize = 12, Opacity = 0.75 });
            var maxSize = new NumberBox { Value = _viewModel.DefaultMaxFileSizeMB, Minimum = 0, PlaceholderText = "0" };
            maxSize.ValueChanged += (_, args) =>
            {
                _viewModel.DefaultMaxFileSizeMB = args.NewValue;
                _viewModel.MaxFileSizeMB = _viewModel.DefaultMaxFileSizeMB;
            };
            maxSizePanel.Children.Add(maxSize);

            Grid.SetColumn(minSizePanel, 0);
            Grid.SetColumn(maxSizePanel, 1);
            sizeDefaults.Children.Add(minSizePanel);
            sizeDefaults.Children.Add(maxSizePanel);
            defaultFiltersGroup.Children.Add(sizeDefaults);
            defaultFiltersGroup.Children.Add(new TextBlock { Text = "Both 0 = any size. These defaults fill the Advanced Options size filter when Yagu starts; changes in Advanced Options remain temporary.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            defaultFiltersGroup.Children.Add(NextSearchLabel("Default created date filter:"));
            var createdDefaults = new Grid { ColumnSpacing = 8 };
            createdDefaults.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            createdDefaults.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var createdAfterPanel = new StackPanel { Spacing = 4 };
            createdAfterPanel.Children.Add(new TextBlock { Text = "Created after", FontSize = 12, Opacity = 0.75 });
            var createdAfter = new CalendarDatePicker { Date = _viewModel.DefaultCreatedAfterDate, PlaceholderText = "Any" };
            createdAfter.DateChanged += (_, args) =>
            {
                _viewModel.DefaultCreatedAfterDate = args.NewDate;
                _viewModel.CreatedAfterDate = args.NewDate;
            };
            createdAfterPanel.Children.Add(createdAfter);
            var createdBeforePanel = new StackPanel { Spacing = 4 };
            createdBeforePanel.Children.Add(new TextBlock { Text = "Created before", FontSize = 12, Opacity = 0.75 });
            var createdBefore = new CalendarDatePicker { Date = _viewModel.DefaultCreatedBeforeDate, PlaceholderText = "Any" };
            createdBefore.DateChanged += (_, args) =>
            {
                _viewModel.DefaultCreatedBeforeDate = args.NewDate;
                _viewModel.CreatedBeforeDate = args.NewDate;
            };
            createdBeforePanel.Children.Add(createdBefore);
            Grid.SetColumn(createdAfterPanel, 0);
            Grid.SetColumn(createdBeforePanel, 1);
            createdDefaults.Children.Add(createdAfterPanel);
            createdDefaults.Children.Add(createdBeforePanel);
            defaultFiltersGroup.Children.Add(createdDefaults);

            defaultFiltersGroup.Children.Add(NextSearchLabel("Default modified date filter:"));
            var modifiedDefaults = new Grid { ColumnSpacing = 8 };
            modifiedDefaults.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modifiedDefaults.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var modifiedAfterPanel = new StackPanel { Spacing = 4 };
            modifiedAfterPanel.Children.Add(new TextBlock { Text = "Modified after", FontSize = 12, Opacity = 0.75 });
            var modifiedAfter = new CalendarDatePicker { Date = _viewModel.DefaultModifiedAfterDate, PlaceholderText = "Any" };
            modifiedAfter.DateChanged += (_, args) =>
            {
                _viewModel.DefaultModifiedAfterDate = args.NewDate;
                _viewModel.ModifiedAfterDate = args.NewDate;
            };
            modifiedAfterPanel.Children.Add(modifiedAfter);
            var modifiedBeforePanel = new StackPanel { Spacing = 4 };
            modifiedBeforePanel.Children.Add(new TextBlock { Text = "Modified before", FontSize = 12, Opacity = 0.75 });
            var modifiedBefore = new CalendarDatePicker { Date = _viewModel.DefaultModifiedBeforeDate, PlaceholderText = "Any" };
            modifiedBefore.DateChanged += (_, args) =>
            {
                _viewModel.DefaultModifiedBeforeDate = args.NewDate;
                _viewModel.ModifiedBeforeDate = args.NewDate;
            };
            modifiedBeforePanel.Children.Add(modifiedBefore);
            Grid.SetColumn(modifiedAfterPanel, 0);
            Grid.SetColumn(modifiedBeforePanel, 1);
            modifiedDefaults.Children.Add(modifiedAfterPanel);
            modifiedDefaults.Children.Add(modifiedBeforePanel);
            defaultFiltersGroup.Children.Add(modifiedDefaults);
            defaultFiltersGroup.Children.Add(new TextBlock { Text = "Blank = any date. These defaults fill the Advanced Options date filters when Yagu starts; changes in Advanced Options remain temporary.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            var clearDateDefaults = new Button { Content = "Clear date defaults", HorizontalAlignment = HorizontalAlignment.Left };
            clearDateDefaults.Click += (_, _) =>
            {
                createdAfter.Date = null;
                createdBefore.Date = null;
                modifiedAfter.Date = null;
                modifiedBefore.Date = null;
                _viewModel.DefaultCreatedAfterDate = null;
                _viewModel.DefaultCreatedBeforeDate = null;
                _viewModel.DefaultModifiedAfterDate = null;
                _viewModel.DefaultModifiedBeforeDate = null;
                _viewModel.CreatedAfterDate = null;
                _viewModel.CreatedBeforeDate = null;
                _viewModel.ModifiedAfterDate = null;
                _viewModel.ModifiedBeforeDate = null;
            };
            RegisterDefaultResetButton(clearDateDefaults,
                () => _viewModel.DefaultCreatedAfterDate is null
                    && _viewModel.DefaultCreatedBeforeDate is null
                    && _viewModel.DefaultModifiedAfterDate is null
                    && _viewModel.DefaultModifiedBeforeDate is null);
            defaultFiltersGroup.Children.Add(clearDateDefaults);

            var searchBinary = new ToggleSwitch { OnContent = NextSearchLabel("Search binary files"), OffContent = NextSearchLabel("Search binary files"), IsOn = _viewModel.SearchBinary };
            searchBinary.Toggled += (_, _) => _viewModel.SearchBinary = searchBinary.IsOn;
            pathTypeGroup.Children.Add(searchBinary);
            pathTypeGroup.Children.Add(new TextBlock { Text = "When enabled, files detected as binary (null bytes, magic bytes) are included in content search. Off by default for faster searching.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var searchCloud = new ToggleSwitch { OnContent = NextSearchLabel("Search online-only cloud files"), OffContent = NextSearchLabel("Search online-only cloud files"), IsOn = _viewModel.SearchOnlineOnlyFiles };
            searchCloud.Toggled += (_, _) => _viewModel.SearchOnlineOnlyFiles = searchCloud.IsOn;
            pathTypeGroup.Children.Add(searchCloud);
            pathTypeGroup.Children.Add(new TextBlock { Text = "When enabled, OneDrive/Google Drive ‘online-only’ placeholder files are downloaded on demand and searched — but only when the sync provider is running to serve them. Off by default: cloud-only files are skipped so the scan can never hang waiting on a download that may never complete. Searching them can be slow and use network/disk.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var searchHidden = new ToggleSwitch { OnContent = NextSearchLabel("Search hidden files"), OffContent = NextSearchLabel("Search hidden files"), IsOn = _viewModel.SearchHiddenFiles };
            searchHidden.Toggled += (_, _) => _viewModel.SearchHiddenFiles = searchHidden.IsOn;
            pathTypeGroup.Children.Add(searchHidden);
            pathTypeGroup.Children.Add(new TextBlock { Text = "When enabled (default), files and folders carrying the Windows Hidden attribute are included in searches. When disabled, hidden items are excluded — the file walker skips them and the Everything backends filter them with !attrib:h. System files (e.g. pagefile.sys, hiberfil.sys) are always skipped by the file walker regardless of this setting. This is the default for the Advanced Options ▸ Content options toggle.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var allDrivesNetwork = new ToggleSwitch { OnContent = NextSearchLabel("All-drives search includes network drives"), OffContent = NextSearchLabel("All-drives search includes network drives"), IsOn = _viewModel.SearchAllDrivesIncludesNetwork };
            allDrivesNetwork.Toggled += (_, _) => _viewModel.SearchAllDrivesIncludesNetwork = allDrivesNetwork.IsOn;
            pathTypeGroup.Children.Add(allDrivesNetwork);

            var allDrivesRemovable = new ToggleSwitch { OnContent = NextSearchLabel("All-drives search includes removable/USB drives"), OffContent = NextSearchLabel("All-drives search includes removable/USB drives"), IsOn = _viewModel.SearchAllDrivesIncludesRemovable };
            allDrivesRemovable.Toggled += (_, _) => _viewModel.SearchAllDrivesIncludesRemovable = allDrivesRemovable.IsOn;
            pathTypeGroup.Children.Add(allDrivesRemovable);

            var allDrivesCloud = new ToggleSwitch { OnContent = NextSearchLabel("All-drives search includes cloud drives"), OffContent = NextSearchLabel("All-drives search includes cloud drives"), IsOn = _viewModel.SearchAllDrivesIncludesCloud };
            allDrivesCloud.Toggled += (_, _) => _viewModel.SearchAllDrivesIncludesCloud = allDrivesCloud.IsOn;
            pathTypeGroup.Children.Add(allDrivesCloud);
            pathTypeGroup.Children.Add(new TextBlock { Text = "When the directory box is left empty, Yagu searches all drives. Fixed internal drives are always included; enable these to also search ready network/mapped drives, removable/USB drives, and detected cloud-backed drives (e.g. Google Drive). They are off by default because they can be slow, metered, or trigger downloads.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var allDrivesFullScan = new ToggleSwitch { OnContent = NextSearchLabel("All-drives search does a full scan (bypass Everything index)"), OffContent = NextSearchLabel("All-drives search does a full scan (bypass Everything index)"), IsOn = _viewModel.SearchAllDrivesForceFullScan };
            allDrivesFullScan.Toggled += (_, _) => _viewModel.SearchAllDrivesForceFullScan = allDrivesFullScan.IsOn;
            pathTypeGroup.Children.Add(allDrivesFullScan);
            pathTypeGroup.Children.Add(new TextBlock { Text = "Off by default. When off, the all-drives sweep uses the fast Everything index for any drive it covers (including drives you added manually in Everything's settings) and automatically falls back to a built-in full scan only for drives Everything does not index. Turn this on to walk every drive directly with the built-in scanner — slower, but it also catches drives whose Everything index is partial (e.g. folders you excluded from indexing in Everything).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipAdmin = new CheckBox { Content = NextSearchLabel("Skip admin-protected paths when not elevated"), IsChecked = _viewModel.ExcludeAdminProtectedPaths };
            skipAdmin.Checked += (_, _) => _viewModel.ExcludeAdminProtectedPaths = true;
            skipAdmin.Unchecked += (_, _) => _viewModel.ExcludeAdminProtectedPaths = false;
            pathTypeGroup.Children.Add(skipAdmin);
            pathTypeGroup.Children.Add(new TextBlock { Text = "When the process is not elevated, exclude directories that always require admin (System Volume Information, $Recycle.Bin, Windows\\System32\\config, Windows\\Installer, etc.). Speeds up search by skipping guaranteed access-denied trees. No effect when running as administrator.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var adminSegLabel = NextSearchLabel("Admin-protected path segments (one per line or semicolon-separated):");
            adminSegLabel.Margin = new Thickness(0, 4, 0, 0);
            pathTypeGroup.Children.Add(adminSegLabel);
            var adminSeg = new TextBox
            {
                Text = _viewModel.AdminProtectedPathSegments,
                PlaceholderText = @"\Windows\System32\config;\System Volume Information",
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Height = 100,
                MaxWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            adminSeg.TextChanged += (_, _) => _viewModel.AdminProtectedPathSegments = adminSeg.Text;
            pathTypeGroup.Children.Add(adminSeg);
            var resetAdminSeg = new Button { Content = "Reset to defaults", Margin = new Thickness(0, 4, 0, 0) };
            resetAdminSeg.Click += (_, _) =>
            {
                adminSeg.Text = AppSettings.DefaultAdminProtectedPathSegments;
                _viewModel.AdminProtectedPathSegments = adminSeg.Text;
            };
            RegisterDefaultResetButton(resetAdminSeg,
                () => SettingStringEquals(_viewModel.AdminProtectedPathSegments, AppSettings.DefaultAdminProtectedPathSegments));
            pathTypeGroup.Children.Add(resetAdminSeg);
            pathTypeGroup.Children.Add(new TextBlock { Text = "Each entry is a path substring like \\Windows\\System32\\config. Anchored with backslashes so it matches the folder anywhere in the tree. Only applied when the process is not elevated.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipExtLabel = NextSearchLabel("Skip extensions (semicolon-separated, no dots):");
            skipExtLabel.Margin = new Thickness(0, 4, 0, 0);
            pathTypeGroup.Children.Add(skipExtLabel);
            var skipExt = new TextBox { Text = _viewModel.SettingsSkipExtensions, PlaceholderText = "e.g. svg;sqlite;etl;log", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            skipExt.TextChanged += (_, _) =>
            {
                _viewModel.SettingsSkipExtensions = skipExt.Text;
                _viewModel.SkipExtensions = skipExt.Text;
            };
            pathTypeGroup.Children.Add(skipExt);
            pathTypeGroup.Children.Add(new TextBlock { Text = "Files with these extensions are skipped entirely before content search. Default media, document, data, and dump prefilters live here; add custom text/project extensions as needed.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var binaryExtLabel = NextSearchLabel("Binary extensions (semicolon-separated, no dots):");
            binaryExtLabel.Margin = new Thickness(0, 4, 0, 0);
            pathTypeGroup.Children.Add(binaryExtLabel);
            var binaryExt = new TextBox { Text = _viewModel.SettingsBinaryExtensions, PlaceholderText = "e.g. exe;dll;pdb;wasm", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            binaryExt.TextChanged += (_, _) =>
            {
                _viewModel.SettingsBinaryExtensions = binaryExt.Text;
                _viewModel.BinaryExtensions = binaryExt.Text;
            };
            pathTypeGroup.Children.Add(binaryExt);
            var resetBinaryExt = new Button { Content = "Reset binary extensions", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            resetBinaryExt.Click += (_, _) =>
            {
                binaryExt.Text = AppSettings.DefaultBinaryExtensions;
                _viewModel.SettingsBinaryExtensions = binaryExt.Text;
                _viewModel.BinaryExtensions = binaryExt.Text;
            };
            RegisterDefaultResetButton(resetBinaryExt,
                () => SettingStringEquals(_viewModel.SettingsBinaryExtensions, AppSettings.DefaultBinaryExtensions));
            pathTypeGroup.Children.Add(resetBinaryExt);
            pathTypeGroup.Children.Add(new TextBlock { Text = "These populate the Binary ext dropdown shown beside Skip Extensions when Search binary is enabled. Use this for compiled binary and build artifact extensions that should remain skipped even during binary search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var excludedExtLabel = new TextBlock
            {
                Text = "When a search targets an excluded file type:",
                Margin = new Thickness(0, 8, 0, 2),
                FontSize = 12,
                Opacity = 0.9,
            };
            pathTypeGroup.Children.Add(excludedExtLabel);
            var excludedExtChoice = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            excludedExtChoice.Items.Add("Ask me each time (recommended)");
            excludedExtChoice.Items.Add("Always include the excluded file type");
            excludedExtChoice.Items.Add("Always search without it");
            excludedExtChoice.SelectedIndex = !_viewModel.SuppressExcludedExtensionWarnings
                ? 0
                : (_viewModel.IncludeExcludedExtensionByDefault ? 1 : 2);
            excludedExtChoice.SelectionChanged += (_, _) =>
            {
                switch (excludedExtChoice.SelectedIndex)
                {
                    case 1:
                        _viewModel.SuppressExcludedExtensionWarnings = true;
                        _viewModel.IncludeExcludedExtensionByDefault = true;
                        break;
                    case 2:
                        _viewModel.SuppressExcludedExtensionWarnings = true;
                        _viewModel.IncludeExcludedExtensionByDefault = false;
                        break;
                    default:
                        _viewModel.SuppressExcludedExtensionWarnings = false;
                        break;
                }
            };
            pathTypeGroup.Children.Add(excludedExtChoice);
            pathTypeGroup.Children.Add(new TextBlock { Text = "Yagu warns before searching if your query names a file whose extension is currently excluded by the Skip or Binary extension lists or an Include/Exclude filter, so those files would not appear in results. Choose to be asked each time, to always include the excluded type, or to always search without it.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var archiveExtLabel = NextSearchLabel("Archive extensions (semicolon-separated, no dots):");
            archiveExtLabel.Margin = new Thickness(0, 4, 0, 0);
            archiveGroup.Children.Add(archiveExtLabel);
            var archiveExt = new TextBox { Text = _viewModel.SettingsArchiveExtensions, PlaceholderText = "e.g. zip;jar;docx;xlsx;pptx;epub", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            archiveExt.TextChanged += (_, _) =>
            {
                _viewModel.SettingsArchiveExtensions = archiveExt.Text;
                _viewModel.ArchiveExtensions = archiveExt.Text;
            };
            archiveGroup.Children.Add(archiveExt);
            archiveGroup.Children.Add(new TextBlock { Text = "Extensions that are ZIP-like containers. When 'Search archives' is on, these are removed from the skip list so they reach the content searcher. Detection still uses file-header magic bytes.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            archiveGroup.Children.Add(NextSearchLabel("Max archive nesting depth (0 = 5):"));
            var archiveNesting = new NumberBox { Value = _viewModel.ArchiveMaxNestingDepth, Minimum = 0, Maximum = 50 };
            archiveNesting.ValueChanged += (_, args) => _viewModel.ArchiveMaxNestingDepth = (int)args.NewValue;
            archiveGroup.Children.Add(archiveNesting);
            archiveGroup.Children.Add(new TextBlock { Text = "How many levels deep to search nested archives (zip inside zip). Higher values find deeply nested content but may be slower. 0 uses the default (5).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            archiveGroup.Children.Add(NextSearchLabel("Max archive entry size (MB, 0 = 64):"));
            var archiveEntrySize = new NumberBox { Value = _viewModel.ArchiveMaxEntryMB, Minimum = 0, Maximum = 4096 };
            archiveEntrySize.ValueChanged += (_, args) => _viewModel.ArchiveMaxEntryMB = (int)args.NewValue;
            archiveGroup.Children.Add(archiveEntrySize);
            archiveGroup.Children.Add(new TextBlock { Text = "Individual files inside archives larger than this are skipped. Higher values search larger archive entries but use more memory. 0 uses the default (64 MB).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── OCR ──
        {
            var g = AddTab("OCR");
            var ocrEnableGroup = AddSettingsGroupBox(g, "Image Text Search");
            var ocrQualityGroup = AddSettingsGroupBox(g, "Recognition Quality");

            // Master OCR toggle (migrated from the Search Limits tab).
            var searchImageText = new ToggleSwitch { OnContent = NextSearchLabel("Search image text (OCR)"), OffContent = NextSearchLabel("Search image text (OCR)"), IsOn = _viewModel.SearchImageText };
            searchImageText.Toggled += (_, _) => _viewModel.SearchImageText = searchImageText.IsOn;
            ocrEnableGroup.Children.Add(searchImageText);
            ocrEnableGroup.Children.Add(new TextBlock { Text = "When enabled, raster images (PNG/JPG/BMP/GIF/TIFF/WebP) are OCR'd on a background queue and their recognized text is searched. Off by default. OCR runs on a separate, non-blocking thread so it never slows the file scan — image matches appear as each image finishes processing. This is the default for the Advanced Options ▸ Filters toggle.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // OCR engine (migrated from the Search Limits tab).
            var ocrEngineLabel = NextSearchLabel("OCR engine:");
            ocrEngineLabel.Margin = new Thickness(0, 4, 0, 0);
            ocrEnableGroup.Children.Add(ocrEngineLabel);
            var ocrEngineCombo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            // PaddleOCR's native runtime is win-x64 only, so it can't run on x86 — disable that option
            // there (NormalizeImageOcrEngine also coerces a persisted "paddle" to "tesseract" on x86).
            ocrEngineCombo.Items.Add(new ComboBoxItem
            {
                Content = AppSettings.PaddleOcrSupported ? "PaddleSharp (recommended)" : "PaddleSharp (unavailable on 32-bit x86)",
                Tag = "paddle",
                IsEnabled = AppSettings.PaddleOcrSupported,
            });
            ocrEngineCombo.Items.Add(new ComboBoxItem { Content = "Tesseract", Tag = "tesseract" });
            ocrEngineCombo.SelectedIndex =
                string.Equals(AppSettings.NormalizeImageOcrEngine(_viewModel.ImageOcrEngine), "tesseract", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ocrEnableGroup.Children.Add(ocrEngineCombo);
            ocrEnableGroup.Children.Add(new TextBlock { Text = "PaddleSharp (the default) generally gives higher accuracy on screenshots and documents. It runs on the CPU (MKL-accelerated) — no GPU or NPU is required or used. Tesseract is a lighter alternative that can be faster on low-end CPUs. The selected engine's runtime and models are downloaded on first use.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // ── Recognition Quality (applies to PaddleSharp; Tesseract uses a fixed pipeline) ──
            ocrQualityGroup.Children.Add(new TextBlock { Text = "These settings tune the PaddleSharp engine. The Tesseract engine uses a fixed recognition pipeline and ignores them.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            var presetLabel = NextSearchLabel("Quality preset:");
            ocrQualityGroup.Children.Add(presetLabel);
            var presetCombo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            presetCombo.Items.Add(new ComboBoxItem { Content = "Fast (English v3, 640 px)", Tag = "Fast" });
            presetCombo.Items.Add(new ComboBoxItem { Content = "Balanced (Chinese+English v5, 960 px)", Tag = "Balanced" });
            presetCombo.Items.Add(new ComboBoxItem { Content = "Accurate (Chinese+English v5, 1536 px)", Tag = "Accurate" });
            presetCombo.Items.Add(new ComboBoxItem { Content = "Custom", Tag = "Custom" });
            ocrQualityGroup.Children.Add(presetCombo);
            ocrQualityGroup.Children.Add(new TextBlock { Text = "Quick presets that set the recognition model and detection resolution together. Choosing a model or resolution below that does not match a preset switches this to Custom.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var modelLabel = NextSearchLabel("Recognition model:");
            modelLabel.Margin = new Thickness(0, 4, 0, 0);
            ocrQualityGroup.Children.Add(modelLabel);
            var modelCombo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            modelCombo.Items.Add(new ComboBoxItem { Content = "English (v3) — fastest", Tag = "EnglishV3" });
            modelCombo.Items.Add(new ComboBoxItem { Content = "English (v4)", Tag = "EnglishV4" });
            modelCombo.Items.Add(new ComboBoxItem { Content = "Chinese + English (v4)", Tag = "ChineseV4" });
            modelCombo.Items.Add(new ComboBoxItem { Content = "Chinese + English (v5) — recommended, most accurate", Tag = "ChineseV5" });
            ocrQualityGroup.Children.Add(modelCombo);
            ocrQualityGroup.Children.Add(new TextBlock { Text = "The PaddleSharp recognition model. English models are fastest for Latin-script text; the Chinese+English models also recognize CJK characters and v5 is the most accurate overall. Models are downloaded on first use.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var resolutionLabel = NextSearchLabel("Detection resolution (longest side, px):");
            resolutionLabel.Margin = new Thickness(0, 4, 0, 0);
            ocrQualityGroup.Children.Add(resolutionLabel);
            var resolutionCombo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            resolutionCombo.Items.Add(new ComboBoxItem { Content = "640 — fastest", Tag = "640" });
            resolutionCombo.Items.Add(new ComboBoxItem { Content = "960 — balanced", Tag = "960" });
            resolutionCombo.Items.Add(new ComboBoxItem { Content = "1280", Tag = "1280" });
            resolutionCombo.Items.Add(new ComboBoxItem { Content = "1536", Tag = "1536" });
            resolutionCombo.Items.Add(new ComboBoxItem { Content = "2048", Tag = "2048" });
            resolutionCombo.Items.Add(new ComboBoxItem { Content = "Unlimited (native resolution)", Tag = "0" });
            ocrQualityGroup.Children.Add(resolutionCombo);
            ocrQualityGroup.Children.Add(new TextBlock { Text = "The image is downscaled so its longest side is at most this many pixels before text detection. Larger values find smaller text at the cost of speed and memory. Unlimited uses the image's native resolution (slowest).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // Wiring: a guard flag prevents the preset ⇄ model/resolution selections from feeding back
            // into one another while one is programmatically updating the others.
            bool syncing = false;

            static void SelectComboByTag(ComboBox combo, string tag)
            {
                foreach (var item in combo.Items)
                {
                    if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        return;
                    }
                }
            }

            static string PresetForModelAndResolution(string model, int maxSide) => model switch
            {
                "EnglishV3" when maxSide == 640 => "Fast",
                "ChineseV5" when maxSide == 960 => "Balanced",
                "ChineseV5" when maxSide == 1536 => "Accurate",
                _ => "Custom",
            };

            string CurrentModel() => AppSettings.NormalizeImageOcrModel(_viewModel.ImageOcrModel);
            int CurrentMaxSide() => AppSettings.NormalizeImageOcrMaxSide(_viewModel.ImageOcrMaxSide);

            void UpdateQualityEnabled()
            {
                bool paddle = !string.Equals(AppSettings.NormalizeImageOcrEngine(_viewModel.ImageOcrEngine), "tesseract", StringComparison.OrdinalIgnoreCase);
                presetCombo.IsEnabled = paddle;
                modelCombo.IsEnabled = paddle;
                resolutionCombo.IsEnabled = paddle;
            }

            // Seed the selections from the view model BEFORE attaching change handlers so the initial
            // sync never marks settings dirty or recurses.
            SelectComboByTag(modelCombo, CurrentModel());
            SelectComboByTag(resolutionCombo, CurrentMaxSide().ToString(CultureInfo.InvariantCulture));
            SelectComboByTag(presetCombo, PresetForModelAndResolution(CurrentModel(), CurrentMaxSide()));
            UpdateQualityEnabled();

            ocrEngineCombo.SelectionChanged += (_, _) =>
            {
                if (ocrEngineCombo.SelectedItem is ComboBoxItem { Tag: string engineId })
                    _viewModel.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(engineId);
                UpdateQualityEnabled();
            };

            presetCombo.SelectionChanged += (_, _) =>
            {
                if (syncing) return;
                if (presetCombo.SelectedItem is not ComboBoxItem { Tag: string preset }) return;
                (string Model, int MaxSide)? target = preset switch
                {
                    "Fast" => ("EnglishV3", 640),
                    "Balanced" => ("ChineseV5", 960),
                    "Accurate" => ("ChineseV5", 1536),
                    _ => null, // Custom: keep the current model/resolution.
                };
                if (target is null) return;
                syncing = true;
                _viewModel.ImageOcrModel = target.Value.Model;
                _viewModel.ImageOcrMaxSide = target.Value.MaxSide;
                SelectComboByTag(modelCombo, target.Value.Model);
                SelectComboByTag(resolutionCombo, target.Value.MaxSide.ToString(CultureInfo.InvariantCulture));
                syncing = false;
            };

            modelCombo.SelectionChanged += (_, _) =>
            {
                if (modelCombo.SelectedItem is ComboBoxItem { Tag: string model })
                    _viewModel.ImageOcrModel = AppSettings.NormalizeImageOcrModel(model);
                if (syncing) return;
                syncing = true;
                SelectComboByTag(presetCombo, PresetForModelAndResolution(CurrentModel(), CurrentMaxSide()));
                syncing = false;
            };

            resolutionCombo.SelectionChanged += (_, _) =>
            {
                if (resolutionCombo.SelectedItem is ComboBoxItem { Tag: string raw } &&
                    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxSide))
                {
                    _viewModel.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(maxSide);
                }
                if (syncing) return;
                syncing = true;
                SelectComboByTag(presetCombo, PresetForModelAndResolution(CurrentModel(), CurrentMaxSide()));
                syncing = false;
            };
        }

        // ── Performance ──
        {
            var g = AddTab("Performance");
            var searchEngineGroup = AddSettingsGroupBox(g, "Search Engine");
            var memoryGroup = AddSettingsGroupBox(g, "Memory Saving Mode");
            var scanSafeguardsGroup = AddSettingsGroupBox(g, "Content Scan Safeguards");
            var nativeTuningGroup = AddSettingsGroupBox(g, "Native Scanner Tuning");

            searchEngineGroup.Children.Add(new TextBlock { Text = "File-listing backend (how files are discovered before searching):" });
            var backend = new ComboBox();
            backend.Items.Add("Auto (SDK → es.exe → .NET)");
            backend.Items.Add("Everything SDK only (in-process, fastest)");
            backend.Items.Add("es.exe only (process spawn)");
            backend.Items.Add(".NET enumeration only (no Everything dependency)");
            backend.SelectedIndex = _viewModel.FileListerBackendIndex;
            backend.SelectionChanged += (_, _) => _viewModel.FileListerBackendIndex = backend.SelectedIndex;
            searchEngineGroup.Children.Add(backend);
            searchEngineGroup.Children.Add(new TextBlock { Text = "Auto tries the Everything SDK first, then es.exe, then .NET recursive enumeration. Requires voidtools Everything to be running for SDK/es.exe.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            searchEngineGroup.Children.Add(NextSearchLabel("Content-search parallelism (concurrent file scan threads):"));
            var parallelism = new ComboBox();
            parallelism.Items.Add($"Safe cap (up to {Math.Min(16, Environment.ProcessorCount)})");
            parallelism.Items.Add("1 thread (sequential, HDD safe)");
            parallelism.Items.Add($"Half cores ({Math.Max(1, Environment.ProcessorCount / 2)})");
            parallelism.Items.Add($"2× cores ({Environment.ProcessorCount * 2}, I/O heavy)");
            parallelism.Items.Add($"All cores ({Math.Max(1, Environment.ProcessorCount)})");
            parallelism.SelectedIndex = _viewModel.ParallelismIndex;
            parallelism.SelectionChanged += (_, _) => _viewModel.ParallelismIndex = parallelism.SelectedIndex;
            searchEngineGroup.Children.Add(parallelism);

            searchEngineGroup.Children.Add(NextSearchLabel("I/O worker oversubscription (scan threads = parallelism × factor):"));
            var oversub = new ComboBox();
            oversub.Items.Add("Auto (SSD/NVMe: 1×, HDD: 2×) — recommended");
            oversub.Items.Add("1× (lowest CPU, coolest)");
            oversub.Items.Add("2× (max throughput on cold full-drive sweeps)");
            oversub.Items.Add("3× (aggressive, highest CPU)");
            oversub.SelectedIndex = _viewModel.IoOversubscriptionIndex;
            oversub.SelectionChanged += (_, _) => _viewModel.IoOversubscriptionIndex = oversub.SelectedIndex;
            searchEngineGroup.Children.Add(oversub);
            searchEngineGroup.Children.Add(new TextBlock { Text = "How many concurrent file-scan worker threads the native scanner spawns, as a multiple of the parallelism setting above. Oversubscription overlaps per-file disk latency during cold sweeps but wastes CPU (and generates heat) when files are already cached. Auto uses 1× on SSD/NVMe and 2× on rotational HDDs.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var hddToggle = new ToggleSwitch
            {
                IsOn = _viewModel.LimitParallelismOnHdd,
                OnContent = "Limit parallelism on HDD (set to 1 thread)",
                OffContent = "Limit parallelism on HDD (set to 1 thread)",
                Margin = new Thickness(0, 4, 0, 0),
            };
            hddToggle.Toggled += (_, _) => _viewModel.LimitParallelismOnHdd = hddToggle.IsOn;
            searchEngineGroup.Children.Add(hddToggle);
            searchEngineGroup.Children.Add(new TextBlock { Text = "When enabled, if the search target is on a rotational hard disk, Yagu automatically sets parallelism to 1 to avoid excessive disk thrashing. Disable to allow any parallelism level on HDDs.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var hddWarnToggle = new ToggleSwitch
            {
                IsOn = !_viewModel.SuppressHddParallelismWarnings,
                OnContent = "Warn before searching an HDD",
                OffContent = "Warn before searching an HDD",
                Margin = new Thickness(0, 4, 0, 0),
            };
            hddWarnToggle.Toggled += (_, _) => _viewModel.SuppressHddParallelismWarnings = !hddWarnToggle.IsOn;
            searchEngineGroup.Children.Add(hddWarnToggle);
            searchEngineGroup.Children.Add(new TextBlock { Text = "When enabled, Yagu shows a one-time-per-disk notice before searching a rotational hard disk. This only controls the notice; it does not change whether parallelism is limited (above). Only applies while parallelism limiting on HDD is enabled.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            searchEngineGroup.Children.Add(NextSearchLabel("SDK channel buffer size:"));
            var sdkBuf = new NumberBox { Value = _viewModel.SdkChannelBufferSize, Minimum = 16, Maximum = 1000000 };
            sdkBuf.ValueChanged += (_, args) => _viewModel.SdkChannelBufferSize = (int)args.NewValue;
            searchEngineGroup.Children.Add(sdkBuf);
            searchEngineGroup.Children.Add(new TextBlock { Text = "Number of file paths buffered between the Everything SDK producer thread and the consumer. Higher values may improve throughput on large directories but use more memory. Only applies when using the Everything SDK backend.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            memoryGroup.Children.Add(new TextBlock { Text = "These settings control when Yagu starts paging results to disk. If the hard cap is set above 0, it takes precedence over the system pressure percentage. Changes apply to the next search.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            AddResultTempDriveSetting(memoryGroup);

            memoryGroup.Children.Add(NextSearchLabel("Temp-drive full warning threshold (%):"));
            var lowDiskWarning = new NumberBox { Value = _viewModel.LowDiskSpaceWarningPercent, Minimum = AppSettings.MinimumLowDiskSpaceWarningPercent, Maximum = AppSettings.MaximumLowDiskSpaceWarningPercent };
            lowDiskWarning.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    _viewModel.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent((int)args.NewValue);
            };
            memoryGroup.Children.Add(lowDiskWarning);
            memoryGroup.Children.Add(new TextBlock { Text = "Yagu terminates the active search if the search result temp-file drive is more than this full. Checked every 30 seconds. Default 90%.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            memoryGroup.Children.Add(NextSearchLabel("System memory pressure limit (%, 0 = disabled):"));
            var memPressure = new NumberBox { Value = _viewModel.MemoryPressurePercent, Minimum = 0, Maximum = 100 };
            memPressure.ValueChanged += (_, args) => _viewModel.MemoryPressurePercent = (int)args.NewValue;
            memoryGroup.Children.Add(memPressure);
            memoryGroup.Children.Add(new TextBlock { Text = "Yagu enters memory-saving mode when total machine RAM usage exceeds this %. Recommended for most users.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            memoryGroup.Children.Add(NextSearchLabel("Process memory hard cap (MB, 0 = use pressure % above):"));
            var memLimit = new NumberBox { Value = _viewModel.MemoryLimitMB, Minimum = 0, Maximum = 65536 };
            memLimit.ValueChanged += (_, args) => _viewModel.MemoryLimitMB = (int)args.NewValue;
            memoryGroup.Children.Add(memLimit);
            memoryGroup.Children.Add(new TextBlock { Text = "When set above 0, memory-saving mode activates when the Yagu process exceeds this working-set size regardless of system memory pressure. Leave at 0 to use the pressure % instead.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            scanSafeguardsGroup.Children.Add(NextSearchLabel("Max matches per file (0 = unlimited):"));
            var maxPerFile = new NumberBox { Value = _viewModel.MaxMatchesPerFile, Minimum = 0, Maximum = int.MaxValue };
            maxPerFile.ValueChanged += (_, args) => _viewModel.MaxMatchesPerFile = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
            scanSafeguardsGroup.Children.Add(maxPerFile);
            scanSafeguardsGroup.Children.Add(new TextBlock { Text = "Optional cap on stored matches per file. Useful for taming pathological files (massive logs, generated dumps) that would otherwise dominate memory. Leave at 0 for unlimited matches. Applies to subsequent searches.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            scanSafeguardsGroup.Children.Add(NextSearchLabel("Max matches per line (0 = unlimited):"));
            var maxPerLine = new NumberBox { Value = _viewModel.MaxMatchesPerLine, Minimum = 0, Maximum = int.MaxValue };
            maxPerLine.ValueChanged += (_, args) => _viewModel.MaxMatchesPerLine = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
            scanSafeguardsGroup.Children.Add(maxPerLine);
            scanSafeguardsGroup.Children.Add(new TextBlock { Text = "Caps how many matches a single line can emit before the scanner moves on. Tames a match-everything pattern (e.g. the regex \".\") on very long minified lines, which would otherwise produce millions of matches. Leave at 0 for unlimited. Applies to subsequent searches.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            scanSafeguardsGroup.Children.Add(NextSearchLabel("Content-search file size ceiling (MB, 0 = no ceiling):"));
            var fileSizeCeiling = new NumberBox { Value = _viewModel.ContentSearchFileSizeMB, Minimum = 0, Maximum = 10240 };
            fileSizeCeiling.ValueChanged += (_, args) => _viewModel.ContentSearchFileSizeMB = (int)args.NewValue;
            scanSafeguardsGroup.Children.Add(fileSizeCeiling);
            scanSafeguardsGroup.Children.Add(new TextBlock { Text = "Files larger than this are skipped during content search when no explicit max-size filter is set. Prevents the scanner from blocking for minutes on huge files. Set to 0 to disable (no ceiling).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            nativeTuningGroup.Children.Add(NextSearchLabel("MMF concurrency limit (0 = default 16):"));
            var mmfLimit = new NumberBox { Value = _viewModel.MmfConcurrencyLimit, Minimum = 0, Maximum = 256 };
            mmfLimit.ValueChanged += (_, args) => _viewModel.MmfConcurrencyLimit = (int)args.NewValue;
            nativeTuningGroup.Children.Add(mmfLimit);
            nativeTuningGroup.Children.Add(new TextBlock { Text = "Maximum concurrent memory-mapped file views. Higher values use more virtual address space but may improve throughput on NVMe. Set to 0 for the default (16). Applies to the next search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            nativeTuningGroup.Children.Add(NextSearchLabel("Native scanner concurrency limit (0 = default):"));
            var nativeLimit = new NumberBox { Value = _viewModel.NativeConcurrencyLimit, Minimum = 0, Maximum = 256 };
            nativeLimit.ValueChanged += (_, args) => _viewModel.NativeConcurrencyLimit = (int)args.NewValue;
            nativeTuningGroup.Children.Add(nativeLimit);
            nativeTuningGroup.Children.Add(new TextBlock { Text = "Maximum concurrent Rust native scans. Higher values improve throughput on fast NVMe storage. Set to 0 for the default (min(64, CPU cores × 2)). Applies to the next search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Display ──
        {
            var g = AddTab("Display");
            var appAppearanceGroup = AddSettingsGroupBox(g, "Application Appearance");
            var fileMatchListGroup = AddSettingsGroupBox(g, "File Match List");
            var previewViewerGroup = AddSettingsGroupBox(g, "Preview Viewer");
            var editorAppearanceGroup = AddSettingsGroupBox(g, "Built-In Editor Appearance");
            var overlayGroup = AddSettingsGroupBox(g, "Overlay & Drawer Fonts");

            appAppearanceGroup.Children.Add(new TextBlock { Text = "Theme:" });
            var themeMode = new ComboBox { SelectedIndex = AppThemeService.NormalizeThemeModeIndex(_viewModel.ThemeModeIndex), MinWidth = 220 };
            themeMode.Items.Add("Auto (system theme)");
            themeMode.Items.Add("Dark mode");
            themeMode.Items.Add("Light mode");
            themeMode.SelectionChanged += (_, _) =>
            {
                if (themeMode.SelectedIndex >= 0)
                    _viewModel.ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(themeMode.SelectedIndex);
            };
            appAppearanceGroup.Children.Add(themeMode);
            appAppearanceGroup.Children.Add(new TextBlock { Text = "Auto follows the current Windows app theme. Dark and Light keep Yagu on that theme until changed.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            fileMatchListGroup.Children.Add(new TextBlock { Text = "Line truncation length (characters):" });
            var trunc = new NumberBox { Value = _viewModel.LineTruncationLength, Minimum = 0, Maximum = 10000 };
            trunc.ValueChanged += (_, args) => _viewModel.LineTruncationLength = (int)args.NewValue;
            fileMatchListGroup.Children.Add(trunc);
            fileMatchListGroup.Children.Add(new TextBlock { Text = "Lines longer than this are truncated in the results list to prevent UI slowdowns from extremely long lines. Set to 0 to disable truncation.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            fileMatchListGroup.Children.Add(new TextBlock { Text = "Results list match text", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) });

            fileMatchListGroup.Children.Add(new TextBlock { Text = "Font family:" });
            var resultMatchFontFamily = CreateFontFamilyPicker(
                _viewModel.ResultListMatchTextFontFamily,
                AppSettings.DefaultResultListMatchTextFontFamily,
                value => _viewModel.ResultListMatchTextFontFamily = value);
            fileMatchListGroup.Children.Add(resultMatchFontFamily);

            fileMatchListGroup.Children.Add(new TextBlock { Text = "Font size:" });
            var resultMatchFontSize = new NumberBox
            {
                Value = _viewModel.ResultListMatchTextFontSize,
                Minimum = 6,
                Maximum = 72,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            resultMatchFontSize.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    _viewModel.ResultListMatchTextFontSize = (int)Math.Clamp(args.NewValue, 6, 72);
            };
            fileMatchListGroup.Children.Add(resultMatchFontSize);

            var resultMatchColor = AddColorSetting(
                fileMatchListGroup,
                "Highlighted match text:",
                "Color of the matched substring inside each result-list match line. Default is gold.",
                _viewModel.ResultListMatchHighlightColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00),
                value => _viewModel.ResultListMatchHighlightColor = value,
                contrastThemeProvider: ResolveFontContrastTheme,
                registerContrastStatusRefresher: _fontContrastStatusRefreshers.Add);

            var resetResultMatchText = new Button
            {
                Content = "Reset result match text",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
            };
            resetResultMatchText.Click += (_, _) =>
            {
                SelectFontFamily(resultMatchFontFamily, AppSettings.DefaultResultListMatchTextFontFamily);
                resultMatchFontSize.Value = AppSettings.DefaultResultListMatchTextFontSize;
                resultMatchColor.Color = ColorStringHelper.Parse(
                    AppSettings.DefaultResultListMatchHighlightColor,
                    Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
                _viewModel.ResultListMatchTextFontFamily = AppSettings.DefaultResultListMatchTextFontFamily;
                _viewModel.ResultListMatchTextFontSize = AppSettings.DefaultResultListMatchTextFontSize;
                _viewModel.ResultListMatchHighlightColor = AppSettings.DefaultResultListMatchHighlightColor;
            };
            RegisterDefaultResetButton(resetResultMatchText,
                () => SettingStringEquals(_viewModel.ResultListMatchTextFontFamily, AppSettings.DefaultResultListMatchTextFontFamily)
                    && _viewModel.ResultListMatchTextFontSize == AppSettings.DefaultResultListMatchTextFontSize
                    && SettingColorEquals(
                        _viewModel.ResultListMatchHighlightColor,
                        AppSettings.DefaultResultListMatchHighlightColor,
                        Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00)));
            fileMatchListGroup.Children.Add(resetResultMatchText);
            fileMatchListGroup.Children.Add(new TextBlock { Text = "Used by the left results pane match lines. Context lines and line-number buttons keep their compact default styling.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            previewViewerGroup.Children.Add(new TextBlock { Text = "Preview layout:" });
            var previewMode = new ComboBox();
            previewMode.Items.Add("Concatenated (separate match snippets)");
            previewMode.Items.Add("Multi-highlight (unified file view)");
            previewMode.SelectedIndex = _viewModel.PreviewModeIndex;
            previewMode.SelectionChanged += (_, _) => _viewModel.PreviewModeIndex = previewMode.SelectedIndex;
            previewViewerGroup.Children.Add(previewMode);

            var wordWrap = new CheckBox { Content = "Word wrap in preview panel", IsChecked = _viewModel.PreviewWordWrap };
            wordWrap.Checked += (_, _) =>
            {
                _viewModel.PreviewWordWrap = true;
                _viewModel.PreviewWrapModeIndex = (int)PreviewWrapMode.Wrap;
                _applyWordWrap?.Invoke(true);
            };
            wordWrap.Unchecked += (_, _) =>
            {
                _viewModel.PreviewWordWrap = false;
                _viewModel.PreviewWrapModeIndex = (int)PreviewWrapMode.NoWrap;
                _applyWordWrap?.Invoke(false);
            };
            previewViewerGroup.Children.Add(wordWrap);

            previewViewerGroup.Children.Add(new TextBlock { Text = "Preview text font", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) });

            previewViewerGroup.Children.Add(new TextBlock { Text = "Font family:" });
            var previewTextFontFamily = CreateFontFamilyPicker(
                _viewModel.PreviewTextFontFamily,
                AppSettings.DefaultPreviewTextFontFamily,
                value => _viewModel.PreviewTextFontFamily = value);
            previewViewerGroup.Children.Add(previewTextFontFamily);

            previewViewerGroup.Children.Add(new TextBlock { Text = "Font size:" });
            var previewTextFontSize = new NumberBox
            {
                Value = _viewModel.PreviewTextFontSize,
                Minimum = 6,
                Maximum = 72,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            previewTextFontSize.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    _viewModel.PreviewTextFontSize = (int)Math.Clamp(args.NewValue, 6, 72);
            };
            previewViewerGroup.Children.Add(previewTextFontSize);

            var resetPreviewTextFont = new Button
            {
                Content = "Reset preview text font",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
            };
            resetPreviewTextFont.Click += (_, _) =>
            {
                SelectFontFamily(previewTextFontFamily, AppSettings.DefaultPreviewTextFontFamily);
                previewTextFontSize.Value = AppSettings.DefaultPreviewTextFontSize;
                _viewModel.PreviewTextFontFamily = AppSettings.DefaultPreviewTextFontFamily;
                _viewModel.PreviewTextFontSize = AppSettings.DefaultPreviewTextFontSize;
            };
            RegisterDefaultResetButton(resetPreviewTextFont,
                () => SettingStringEquals(_viewModel.PreviewTextFontFamily, AppSettings.DefaultPreviewTextFontFamily)
                    && _viewModel.PreviewTextFontSize == AppSettings.DefaultPreviewTextFontSize);
            previewViewerGroup.Children.Add(resetPreviewTextFont);
            previewViewerGroup.Children.Add(new TextBlock { Text = "Used by preview pane line text and line-number gutters.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Selected preview content background:",
                "Background for the content body of the active preview section. Default is black.",
                _viewModel.SelectedPreviewContentBackgroundColor,
                Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
                value => _viewModel.SelectedPreviewContentBackgroundColor = value);

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Unselected preview content background:",
                "Background for preview section content that is not active. Default is transparent so it follows the app theme.",
                _viewModel.UnselectedPreviewContentBackgroundColor,
                Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00),
                value => _viewModel.UnselectedPreviewContentBackgroundColor = value);

            previewViewerGroup.Children.Add(new TextBlock { Text = "Preview font colors", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) });

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Preview gutter text:",
                "Color of line numbers and separator pipes in the preview content gutter.",
                _viewModel.PreviewGutterContextColor,
                Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE),
                value => _viewModel.PreviewGutterContextColor = value,
                showContrastStatus: true);

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Matched preview gutter text:",
                "Color of line numbers in the preview gutter for matched lines.",
                _viewModel.PreviewGutterMatchColor,
                Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE),
                value => _viewModel.PreviewGutterMatchColor = value,
                showContrastStatus: true);

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Match highlight text:",
                "Color of the highlighted match text (the search term occurrence). Default is gold.",
                _viewModel.PreviewMatchTextColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00),
                value => _viewModel.PreviewMatchTextColor = value,
                showContrastStatus: true);

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Active match overlay:",
                "Color of the overlay border/underline on the currently-active match. Default is orange-red.",
                _viewModel.PreviewOverlayColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x00),
                value => _viewModel.PreviewOverlayColor = value);

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Matched line text:",
                "Color of text on matched lines (non-highlighted portions). Default is white.",
                _viewModel.PreviewMatchLineColor,
                Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                value => _viewModel.PreviewMatchLineColor = value,
                showContrastStatus: true);

            previewViewerGroup.Children.Add(new TextBlock { Text = "Show more ellipsis", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) });
            previewViewerGroup.Children.Add(new TextBlock { Text = "The clickable \u2026 markers shown where a preview line or section is truncated and can be expanded.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            previewViewerGroup.Children.Add(new TextBlock { Text = "Font size:" });
            var ellipsisFontSize = new NumberBox
            {
                Value = _viewModel.PreviewShowMoreEllipsisFontSize,
                Minimum = 6,
                Maximum = 72,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            ellipsisFontSize.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    _viewModel.PreviewShowMoreEllipsisFontSize = (int)Math.Clamp(args.NewValue, 6, 72);
            };
            previewViewerGroup.Children.Add(ellipsisFontSize);

            var resetEllipsisFont = new Button
            {
                Content = "Reset ellipsis size",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
            };
            resetEllipsisFont.Click += (_, _) =>
            {
                ellipsisFontSize.Value = AppSettings.DefaultPreviewShowMoreEllipsisFontSize;
                _viewModel.PreviewShowMoreEllipsisFontSize = AppSettings.DefaultPreviewShowMoreEllipsisFontSize;
            };
            RegisterDefaultResetButton(resetEllipsisFont,
                () => _viewModel.PreviewShowMoreEllipsisFontSize == AppSettings.DefaultPreviewShowMoreEllipsisFontSize);
            previewViewerGroup.Children.Add(resetEllipsisFont);

            AddPreviewContentColorSetting(
                previewViewerGroup,
                "Ellipsis color:",
                "Color of the clickable \u2026 show-more markers. Default is DodgerBlue.",
                _viewModel.PreviewShowMoreEllipsisColor,
                Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF),
                value => _viewModel.PreviewShowMoreEllipsisColor = value);

            previewViewerGroup.Children.Add(new TextBlock { Text = "Preview section limits", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });

            previewViewerGroup.Children.Add(new TextBlock { Text = "Auto-load matches on scroll (matches to load when reaching end of truncated section, 0 = disabled):" });
            var autoLoad = new NumberBox { Value = _viewModel.PreviewAutoLoadMatches, Minimum = 0, Maximum = 5000, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
            autoLoad.ValueChanged += (_, args) => _viewModel.PreviewAutoLoadMatches = (int)args.NewValue;
            previewViewerGroup.Children.Add(autoLoad);
            previewViewerGroup.Children.Add(new TextBlock { Text = "When a preview section is truncated, this controls how many more matches are appended as you scroll to the end. Set to 0 to require the Load more button.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            previewViewerGroup.Children.Add(new TextBlock { Text = "Max matches per file section before truncation (0 = 500):" });
            var maxPerSection = new NumberBox { Value = _viewModel.MaxMatchesPerSection, Minimum = 0, Maximum = 100_000, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
            maxPerSection.ValueChanged += (_, args) => _viewModel.MaxMatchesPerSection = (int)args.NewValue;
            previewViewerGroup.Children.Add(maxPerSection);
            previewViewerGroup.Children.Add(new TextBlock { Text = "Limits how many matches are rendered per file section before the 'Load more' overflow button appears. Higher values show more upfront but may slow the UI with dense files. 0 uses the default (500).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            previewViewerGroup.Children.Add(new TextBlock { Text = "File sections per page (0 = 50):" });
            var sectionPageSize = new NumberBox { Value = _viewModel.PreviewSectionPageSize, Minimum = 0, Maximum = 10_000, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
            sectionPageSize.ValueChanged += (_, args) => _viewModel.PreviewSectionPageSize = (int)args.NewValue;
            previewViewerGroup.Children.Add(sectionPageSize);
            previewViewerGroup.Children.Add(new TextBlock { Text = "How many file sections are rendered before a 'Show more' button. Higher values load more files at once but can cause layout delays. 0 uses the default (50).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            previewViewerGroup.Children.Add(new TextBlock { Text = "Full-file preview size limit (MB, 0 = 1024):" });
            var fullFileLimit = new NumberBox { Value = _viewModel.FullFilePreviewLimitMB, Minimum = 0, Maximum = 10_240, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
            fullFileLimit.ValueChanged += (_, args) => _viewModel.FullFilePreviewLimitMB = (int)args.NewValue;
            previewViewerGroup.Children.Add(fullFileLimit);
            previewViewerGroup.Children.Add(new TextBlock { Text = "Maximum file size allowed for the full-file preview tab. Files larger than this limit will show an error instead. 0 uses the default (1024 MB / 1 GB).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            editorAppearanceGroup.Children.Add(new TextBlock { Text = "Built-in editor font", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 0) });

            editorAppearanceGroup.Children.Add(new TextBlock { Text = "Font family:" });
            var editorFontFamily = CreateFontFamilyPicker(
                _viewModel.PreviewEditorFontFamily,
                AppSettings.DefaultPreviewEditorFontFamily,
                value => _viewModel.PreviewEditorFontFamily = value);
            editorAppearanceGroup.Children.Add(editorFontFamily);

            editorAppearanceGroup.Children.Add(new TextBlock { Text = "Font size:" });
            var editorFontSize = new NumberBox
            {
                Value = _viewModel.PreviewEditorFontSize,
                Minimum = 6,
                Maximum = 72,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            editorFontSize.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    _viewModel.PreviewEditorFontSize = (int)Math.Clamp(args.NewValue, 6, 72);
            };
            editorAppearanceGroup.Children.Add(editorFontSize);

            var resetEditorFont = new Button
            {
                Content = "Reset editor font",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
            };
            resetEditorFont.Click += (_, _) =>
            {
                SelectFontFamily(editorFontFamily, AppSettings.DefaultPreviewEditorFontFamily);
                editorFontSize.Value = AppSettings.DefaultPreviewEditorFontSize;
                _viewModel.PreviewEditorFontFamily = AppSettings.DefaultPreviewEditorFontFamily;
                _viewModel.PreviewEditorFontSize = AppSettings.DefaultPreviewEditorFontSize;
            };
            RegisterDefaultResetButton(resetEditorFont,
                () => SettingStringEquals(_viewModel.PreviewEditorFontFamily, AppSettings.DefaultPreviewEditorFontFamily)
                    && _viewModel.PreviewEditorFontSize == AppSettings.DefaultPreviewEditorFontSize);
            editorAppearanceGroup.Children.Add(resetEditorFont);
            editorAppearanceGroup.Children.Add(new TextBlock { Text = "Used by the built-in editor for full-file editing. Zoom controls still scale from this base size.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            AddPreviewContentColorSetting(
                editorAppearanceGroup,
                "Editor gutter text:",
                "Color of line numbers in the built-in editor gutter.",
                _viewModel.PreviewEditorGutterColor,
                Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE),
                value => _viewModel.PreviewEditorGutterColor = value,
                showContrastStatus: true);

            AddEditorTextColorSetting(editorAppearanceGroup);

            // ── Overlay & Drawer Fonts ──
            overlayGroup.Children.Add(new TextBlock { Text = "File list sticky overlay", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 });
            overlayGroup.Children.Add(new TextBlock { Text = "Height (px):" });
            var fileListOverlayHeight = new NumberBox { Value = _viewModel.FileListOverlayHeight, Minimum = 20, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            fileListOverlayHeight.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.FileListOverlayHeight = (int)Math.Clamp(args.NewValue, 20, 100); };
            overlayGroup.Children.Add(fileListOverlayHeight);

            overlayGroup.Children.Add(new TextBlock { Text = "Font size:" });
            var fileListOverlayFontSize = new NumberBox { Value = _viewModel.FileListOverlayFontSize, Minimum = 6, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            fileListOverlayFontSize.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.FileListOverlayFontSize = (int)Math.Clamp(args.NewValue, 6, 72); };
            overlayGroup.Children.Add(fileListOverlayFontSize);

            overlayGroup.Children.Add(new TextBlock { Text = "Font family:" });
            var fileListOverlayFontFamily = CreateFontFamilyPicker(_viewModel.FileListOverlayFontFamily, AppSettings.DefaultFileListOverlayFontFamily, value => _viewModel.FileListOverlayFontFamily = value);
            overlayGroup.Children.Add(fileListOverlayFontFamily);

            overlayGroup.Children.Add(new TextBlock { Text = "Font color:" });
            AddPreviewContentColorSetting(overlayGroup, "File name:", "Color of the file name text in the results-list sticky overlay.",
                _viewModel.FileListOverlayFontColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), value => _viewModel.FileListOverlayFontColor = value);

            overlayGroup.Children.Add(new TextBlock { Text = "Preview sticky file header", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 16, 0, 0) });
            overlayGroup.Children.Add(new TextBlock { Text = "Height (px):" });
            var previewStickyHeight = new NumberBox { Value = _viewModel.PreviewStickyHeaderHeight, Minimum = 20, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            previewStickyHeight.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.PreviewStickyHeaderHeight = (int)Math.Clamp(args.NewValue, 20, 100); };
            overlayGroup.Children.Add(previewStickyHeight);

            overlayGroup.Children.Add(new TextBlock { Text = "File name font size:" });
            var stickyFileNameSize = new NumberBox { Value = _viewModel.PreviewStickyHeaderFileNameFontSize, Minimum = 6, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            stickyFileNameSize.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.PreviewStickyHeaderFileNameFontSize = (int)Math.Clamp(args.NewValue, 6, 72); };
            overlayGroup.Children.Add(stickyFileNameSize);

            overlayGroup.Children.Add(new TextBlock { Text = "File name font family:" });
            var stickyFileNameFamily = CreateFontFamilyPicker(_viewModel.PreviewStickyHeaderFileNameFontFamily, AppSettings.DefaultPreviewStickyHeaderFileNameFontFamily, value => _viewModel.PreviewStickyHeaderFileNameFontFamily = value);
            overlayGroup.Children.Add(stickyFileNameFamily);

            AddPreviewContentColorSetting(overlayGroup, "File name color:", "Color of the file name in the preview sticky header.",
                _viewModel.PreviewStickyHeaderFileNameFontColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), value => _viewModel.PreviewStickyHeaderFileNameFontColor = value);

            overlayGroup.Children.Add(new TextBlock { Text = "Detail font size:" });
            var stickyDetailSize = new NumberBox { Value = _viewModel.PreviewStickyHeaderDetailFontSize, Minimum = 6, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            stickyDetailSize.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.PreviewStickyHeaderDetailFontSize = (int)Math.Clamp(args.NewValue, 6, 72); };
            overlayGroup.Children.Add(stickyDetailSize);

            overlayGroup.Children.Add(new TextBlock { Text = "Detail font family:" });
            var stickyDetailFamily = CreateFontFamilyPicker(_viewModel.PreviewStickyHeaderDetailFontFamily, AppSettings.DefaultPreviewStickyHeaderDetailFontFamily, value => _viewModel.PreviewStickyHeaderDetailFontFamily = value);
            overlayGroup.Children.Add(stickyDetailFamily);

            AddPreviewContentColorSetting(overlayGroup, "Detail color:", "Color of the detail text (match count) in the preview sticky header.",
                _viewModel.PreviewStickyHeaderDetailFontColor, Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF), value => _viewModel.PreviewStickyHeaderDetailFontColor = value);

            overlayGroup.Children.Add(new TextBlock { Text = "File list drawer labels", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 16, 0, 0) });

            overlayGroup.Children.Add(new TextBlock { Text = "File name font size:" });
            var drawerFileNameSize = new NumberBox { Value = _viewModel.DrawerFileNameFontSize, Minimum = 6, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            drawerFileNameSize.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.DrawerFileNameFontSize = (int)Math.Clamp(args.NewValue, 6, 72); };
            overlayGroup.Children.Add(drawerFileNameSize);

            overlayGroup.Children.Add(new TextBlock { Text = "File name font family:" });
            var drawerFileNameFamily = CreateFontFamilyPicker(_viewModel.DrawerFileNameFontFamily, AppSettings.DefaultDrawerFileNameFontFamily, value => _viewModel.DrawerFileNameFontFamily = value);
            overlayGroup.Children.Add(drawerFileNameFamily);

            AddPreviewContentColorSetting(overlayGroup, "File name color:", "Color of file names in the file list drawer headers.",
                _viewModel.DrawerFileNameFontColor, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), value => _viewModel.DrawerFileNameFontColor = value);

            overlayGroup.Children.Add(new TextBlock { Text = "Directory font size:" });
            var drawerDirSize = new NumberBox { Value = _viewModel.DrawerDirectoryFontSize, Minimum = 6, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            drawerDirSize.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.DrawerDirectoryFontSize = (int)Math.Clamp(args.NewValue, 6, 72); };
            overlayGroup.Children.Add(drawerDirSize);

            overlayGroup.Children.Add(new TextBlock { Text = "Directory font family:" });
            var drawerDirFamily = CreateFontFamilyPicker(_viewModel.DrawerDirectoryFontFamily, AppSettings.DefaultDrawerDirectoryFontFamily, value => _viewModel.DrawerDirectoryFontFamily = value);
            overlayGroup.Children.Add(drawerDirFamily);

            AddPreviewContentColorSetting(overlayGroup, "Directory color:", "Color of directory paths in the file list drawer headers.",
                _viewModel.DrawerDirectoryFontColor, Windows.UI.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), value => _viewModel.DrawerDirectoryFontColor = value);

            overlayGroup.Children.Add(new TextBlock { Text = "Metadata (date/size) font size:" });
            var drawerMetaSize = new NumberBox { Value = _viewModel.DrawerMetadataFontSize, Minimum = 6, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            drawerMetaSize.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) _viewModel.DrawerMetadataFontSize = (int)Math.Clamp(args.NewValue, 6, 72); };
            overlayGroup.Children.Add(drawerMetaSize);

            overlayGroup.Children.Add(new TextBlock { Text = "Metadata font family:" });
            var drawerMetaFamily = CreateFontFamilyPicker(_viewModel.DrawerMetadataFontFamily, AppSettings.DefaultDrawerMetadataFontFamily, value => _viewModel.DrawerMetadataFontFamily = value);
            overlayGroup.Children.Add(drawerMetaFamily);

            AddPreviewContentColorSetting(overlayGroup, "Metadata color:", "Color of the modified date and file size text in drawer headers.",
                _viewModel.DrawerMetadataFontColor, Windows.UI.Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF), value => _viewModel.DrawerMetadataFontColor = value);

            overlayGroup.Children.Add(new TextBlock { Text = "Changes to overlay and drawer fonts apply to new results loaded after saving settings.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Editor ──
        {
            var g = AddTab("Editor");
            var externalEditorGroup = AddSettingsGroupBox(g, "External Editor");
            var saveSafetyGroup = AddSettingsGroupBox(g, "Built-In Editor Saves");
            var editorAppearanceGroup = AddSettingsGroupBox(g, "Built-In Editor Appearance");
            var fileLimitGroup = AddSettingsGroupBox(g, "Built-In Editor Limits");

            externalEditorGroup.Children.Add(new TextBlock { Text = "Editor command ({file} = full file path, {line} = line number):" });
            var editor = new TextBox { Text = _viewModel.EditorCommand, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            editor.TextChanged += (_, _) => _viewModel.EditorCommand = editor.Text;
            externalEditorGroup.Children.Add(editor);
            externalEditorGroup.Children.Add(new TextBlock { Text = "Used when opening a result in an external editor. Include {file}; include {line} when the editor supports jumping directly to a line.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var backup = new CheckBox { Content = "Backup file before saving", IsChecked = _viewModel.BackupBeforeSave };
            backup.Checked += (_, _) => _viewModel.BackupBeforeSave = true;
            backup.Unchecked += (_, _) => _viewModel.BackupBeforeSave = false;
            saveSafetyGroup.Children.Add(backup);
            saveSafetyGroup.Children.Add(new TextBlock { Text = "When enabled, the original file is copied to <filename>.yagubak before saving changes from the built-in editor. If a .yagubak already exists, uses .yagubak-2, .yagubak-3, etc.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var savedOverlay = new CheckBox { Content = "Show saved confirmation after saving", IsChecked = _viewModel.ShowEditorSavedOverlay };
            savedOverlay.Checked += (_, _) => _viewModel.ShowEditorSavedOverlay = true;
            savedOverlay.Unchecked += (_, _) => _viewModel.ShowEditorSavedOverlay = false;
            saveSafetyGroup.Children.Add(savedOverlay);
            saveSafetyGroup.Children.Add(new TextBlock { Text = "When enabled, the built-in editor briefly shows a Saved confirmation after the file has been written successfully.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var syntaxHighlight = new CheckBox { Content = "Syntax coloring based on file type", IsChecked = _viewModel.EditorSyntaxHighlightingEnabled };
            syntaxHighlight.Checked += (_, _) => _viewModel.EditorSyntaxHighlightingEnabled = true;
            syntaxHighlight.Unchecked += (_, _) => _viewModel.EditorSyntaxHighlightingEnabled = false;
            editorAppearanceGroup.Children.Add(syntaxHighlight);
            editorAppearanceGroup.Children.Add(new TextBlock { Text = "When enabled, the built-in editor colors code (keywords, strings, comments, etc.) based on the file's name or extension. Applies to files opened after the change. Supported types include C#, C/C++, Java, JavaScript/TypeScript, Python, JSON, XML/XAML, HTML, CSS, SQL, Markdown, Lua, PHP, and more.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            editorAppearanceGroup.Children.Add(new TextBlock { Text = "Long-line warning:", Margin = new Thickness(0, 8, 0, 0) });
            var longLineWarning = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
            longLineWarning.Items.Add(new ComboBoxItem { Content = "Ask every time" });
            longLineWarning.Items.Add(new ComboBoxItem { Content = "Always open without word wrap" });
            longLineWarning.Items.Add(new ComboBoxItem { Content = "Always open with word wrap" });
            longLineWarning.SelectedIndex = Math.Clamp(_viewModel.PreviewLongLineWarningIndex, 0, 2);
            longLineWarning.SelectionChanged += (_, _) =>
            {
                if (longLineWarning.SelectedIndex >= 0)
                    _viewModel.PreviewLongLineWarningIndex = longLineWarning.SelectedIndex;
            };
            editorAppearanceGroup.Children.Add(longLineWarning);
            editorAppearanceGroup.Children.Add(new TextBlock { Text = "Controls the warning shown when opening a file with a very long line in the built-in editor. \u201cAsk every time\u201d shows the warning dialog; the other options skip it and always use that wrap mode. Choosing \u201cDon't remind me again\u201d in the dialog sets this automatically.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            fileLimitGroup.Children.Add(new TextBlock { Text = "Files exceeding any of these limits will not open in the built-in editor. Use the external editor or Show in Explorer for very large files.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            fileLimitGroup.Children.Add(new TextBlock { Text = "Max file size (MB):" });
            var edMaxSize = new NumberBox { Value = _viewModel.PreviewEditorMaxSizeMB, Minimum = 1, Maximum = 1024 };
            edMaxSize.ValueChanged += (_, args) => _viewModel.PreviewEditorMaxSizeMB = (int)args.NewValue;
            fileLimitGroup.Children.Add(edMaxSize);

            fileLimitGroup.Children.Add(new TextBlock { Text = "Max total characters:" });
            var edMaxText = new NumberBox { Value = _viewModel.PreviewEditorMaxTextLength, Minimum = 100_000, Maximum = 200_000_000 };
            edMaxText.ValueChanged += (_, args) => _viewModel.PreviewEditorMaxTextLength = (int)args.NewValue;
            fileLimitGroup.Children.Add(edMaxText);

            fileLimitGroup.Children.Add(new TextBlock { Text = "Max single-line length (characters):" });
            var edMaxLine = new NumberBox { Value = _viewModel.PreviewEditorMaxLineLength, Minimum = 10_000, Maximum = 100_000_000 };
            edMaxLine.ValueChanged += (_, args) => _viewModel.PreviewEditorMaxLineLength = (int)args.NewValue;
            fileLimitGroup.Children.Add(edMaxLine);
            fileLimitGroup.Children.Add(new TextBlock { Text = "Single-line length is checked separately because minified or generated files can be small enough to load but expensive to lay out.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            fileLimitGroup.Children.Add(new TextBlock { Text = "Max pop-out size (MB):", Margin = new Thickness(0, 8, 0, 0) });
            var edPopOutSize = new NumberBox { Value = _viewModel.PreviewEditorPopOutMaxSizeMB, Minimum = 1, Maximum = 2048 };
            edPopOutSize.ValueChanged += (_, args) => _viewModel.PreviewEditorPopOutMaxSizeMB = (int)Math.Clamp(args.NewValue, 1, 2048);
            fileLimitGroup.Children.Add(edPopOutSize);
            fileLimitGroup.Children.Add(new TextBlock { Text = "Largest file that can be \u201cpopped out\u201d into its own editor/preview window. Popping out loads the whole file (not chunked), so very large values can be slow to open. Default 100 MB.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            fileLimitGroup.Children.Add(new TextBlock { Text = "Multiple pop-out windows arrange as:", Margin = new Thickness(0, 8, 0, 0) });
            var popOutArrange = new ComboBox { MinWidth = 220 };
            popOutArrange.Items.Add(new ComboBoxItem { Content = "Grid (auto)" });
            popOutArrange.Items.Add(new ComboBoxItem { Content = "Columns (side by side)" });
            popOutArrange.Items.Add(new ComboBoxItem { Content = "Rows (stacked)" });
            popOutArrange.Items.Add(new ComboBoxItem { Content = "Cascade (overlapping)" });
            popOutArrange.Items.Add(new ComboBoxItem { Content = "Manual (I place them)" });
            popOutArrange.SelectedIndex = Math.Clamp(_viewModel.PreviewEditorPopOutArrangementIndex, 0, 4);
            popOutArrange.SelectionChanged += (_, _) =>
            {
                if (popOutArrange.SelectedIndex >= 0)
                    _viewModel.PreviewEditorPopOutArrangementIndex = popOutArrange.SelectedIndex;
            };
            fileLimitGroup.Children.Add(popOutArrange);
            fileLimitGroup.Children.Add(new TextBlock { Text = "When two or more editor/preview windows are open, tiling modes auto-fit them into the screen (a short final row stretches to fill the width). Cascade offsets them; Manual leaves them where you put them.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Window ──
        {
            var g = AddTab("Window");
            var startupGroup = AddSettingsGroupBox(g, "Startup");
            var focusTrayGroup = AddSettingsGroupBox(g, "Focus and Tray");
            var layoutGroup = AddSettingsGroupBox(g, "Window Layout");

            var startInLauncher = new CheckBox { Content = "Start in compact launcher mode", IsChecked = _viewModel.StartInLauncherMode };
            startInLauncher.Checked += (_, _) => _viewModel.StartInLauncherMode = true;
            startInLauncher.Unchecked += (_, _) => _viewModel.StartInLauncherMode = false;
            startupGroup.Children.Add(startInLauncher);
            startupGroup.Children.Add(new TextBlock { Text = "When enabled (default), Yagu launches as a small search bar (like Spotlight/Alfred). When disabled, Yagu launches as a traditional window with title bar and results pane.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            focusTrayGroup.Children.Add(new TextBlock { Text = "Launcher focus-loss behavior:" });
            var focusBehavior = new ComboBox();
            focusBehavior.Items.Add("Minimize to system tray");
            focusBehavior.Items.Add("Stay open (default)");
            focusBehavior.Items.Add("Always on top (stay above all windows)");
            // Clamp legacy/out-of-range values onto the new 0..2 range (3 = old Traditional window).
            focusBehavior.SelectedIndex = _viewModel.WindowFocusBehavior is >= 0 and <= 2
                ? _viewModel.WindowFocusBehavior
                : 1;
            focusBehavior.SelectionChanged += (_, _) => _viewModel.WindowFocusBehavior = focusBehavior.SelectedIndex;
            focusTrayGroup.Children.Add(focusBehavior);
            focusTrayGroup.Children.Add(new TextBlock { Text = "Controls what happens when the window loses focus, in any mode (compact launcher or traditional window). You can also override it for the current session with the pin button next to Browse.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var closeToTray = new CheckBox { Content = "Dock to system tray when closed (instead of exiting)", IsChecked = _viewModel.CloseToTray };
            closeToTray.Checked += (_, _) => _viewModel.CloseToTray = true;
            closeToTray.Unchecked += (_, _) => _viewModel.CloseToTray = false;
            focusTrayGroup.Children.Add(closeToTray);
            focusTrayGroup.Children.Add(new TextBlock { Text = "When enabled, closing the window hides Yagu to the system tray. Right-click the tray icon to reopen or exit.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var maximizeOnStartup = new CheckBox { Content = "Maximize window on startup", IsChecked = _viewModel.MaximizeOnStartup };
            maximizeOnStartup.Checked += (_, _) => _viewModel.MaximizeOnStartup = true;
            maximizeOnStartup.Unchecked += (_, _) => _viewModel.MaximizeOnStartup = false;
            layoutGroup.Children.Add(maximizeOnStartup);
            layoutGroup.Children.Add(new TextBlock { Text = "When enabled, the main window starts maximized instead of its default size.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            layoutGroup.Children.Add(new TextBlock { Text = "Traditional window launch position:", Margin = new Thickness(0, 8, 0, 0) });
            var launchPosition = new ComboBox();
            launchPosition.Items.Add("Centered (default)");
            launchPosition.Items.Add("Top Left");
            launchPosition.Items.Add("Top Middle");
            launchPosition.Items.Add("Top Right");
            launchPosition.Items.Add("Middle Left");
            launchPosition.Items.Add("Middle Right");
            launchPosition.Items.Add("Bottom Left");
            launchPosition.Items.Add("Bottom Middle");
            launchPosition.Items.Add("Bottom Right");
            launchPosition.SelectedIndex = _viewModel.LaunchWindowPosition is >= 0 and <= 8
                ? _viewModel.LaunchWindowPosition
                : 0;
            launchPosition.SelectionChanged += (_, _) =>
            {
                if (launchPosition.SelectedIndex >= 0)
                    _viewModel.LaunchWindowPosition = launchPosition.SelectedIndex;
            };
            layoutGroup.Children.Add(launchPosition);
            layoutGroup.Children.Add(new TextBlock { Text = "Where the main window appears on screen when Yagu launches as a traditional window. Ignored when \u201CMaximize window on startup\u201D is on or while in the compact launcher.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            layoutGroup.Children.Add(new TextBlock { Text = "Compact launcher launch position:", Margin = new Thickness(0, 8, 0, 0) });
            var launcherPosition = new ComboBox();
            launcherPosition.Items.Add("Centered");
            launcherPosition.Items.Add("Top Left");
            launcherPosition.Items.Add("Top Middle (default)");
            launcherPosition.Items.Add("Top Right");
            launcherPosition.Items.Add("Middle Left");
            launcherPosition.Items.Add("Middle Right");
            launcherPosition.Items.Add("Bottom Left");
            launcherPosition.Items.Add("Bottom Middle");
            launcherPosition.Items.Add("Bottom Right");
            launcherPosition.SelectedIndex = _viewModel.LauncherWindowPosition is >= 0 and <= 8
                ? _viewModel.LauncherWindowPosition
                : 2;
            launcherPosition.SelectionChanged += (_, _) =>
            {
                if (launcherPosition.SelectedIndex >= 0)
                    _viewModel.LauncherWindowPosition = launcherPosition.SelectedIndex;
            };
            layoutGroup.Children.Add(launcherPosition);
            layoutGroup.Children.Add(new TextBlock { Text = "Where the compact launcher (Spotlight-style search bar) appears on screen at launch. Defaults to Top Middle.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Interaction ──
        {
            var g = AddTab("Interaction");
            var selectionPreviewGroup = AddSettingsGroupBox(g, "Selection to Preview");

            selectionPreviewGroup.Children.Add(new TextBlock { Text = "These options decide whether checking items in the results list immediately adds them to the preview pane.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            var fileHeaderCheck = new CheckBox { Content = "Checking a file header adds it to the preview pane", IsChecked = _viewModel.FileHeaderCheckAddsToPreview };
            fileHeaderCheck.Checked += (_, _) => _viewModel.FileHeaderCheckAddsToPreview = true;
            fileHeaderCheck.Unchecked += (_, _) => _viewModel.FileHeaderCheckAddsToPreview = false;
            selectionPreviewGroup.Children.Add(fileHeaderCheck);
            selectionPreviewGroup.Children.Add(new TextBlock { Text = "When enabled, selecting the checkbox on a file header in the results list immediately shows that file's matches in the preview pane.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var matchLineCheck = new CheckBox { Content = "Checking a match line adds it to the preview pane", IsChecked = _viewModel.MatchLineCheckAddsToPreview };
            matchLineCheck.Checked += (_, _) => _viewModel.MatchLineCheckAddsToPreview = true;
            matchLineCheck.Unchecked += (_, _) => _viewModel.MatchLineCheckAddsToPreview = false;
            selectionPreviewGroup.Children.Add(matchLineCheck);
            selectionPreviewGroup.Children.Add(new TextBlock { Text = "When enabled, selecting the checkbox on an individual match line immediately shows that match in the preview pane.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Terminal Emulator ──
        {
            var g = AddTab("Terminal Emulator");
            var shellGroup = AddSettingsGroupBox(g, "Default Shell");
            var workingDirectoryGroup = AddSettingsGroupBox(g, "Working Directory");

            AddTerminalShellSetting(shellGroup);
            AddTerminalEmulationSetting(workingDirectoryGroup);
        }

        // ── Developer Options ──
        {
            var g = AddTab("Developer Options");
            var diagnosticsGroup = AddSettingsGroupBox(g, "Diagnostics UI");
            var remindersGroup = AddSettingsGroupBox(g, "Reminders and Warnings");
            var loggingGroup = AddSettingsGroupBox(g, "Logging");

            var showMemoryPressureLabel = new CheckBox
            {
                Content = "Show memory pressure warning label",
                IsChecked = _viewModel.ShowMemoryPressureWarningLabel,
            };
            showMemoryPressureLabel.Checked += (_, _) => _viewModel.ShowMemoryPressureWarningLabel = true;
            showMemoryPressureLabel.Unchecked += (_, _) => _viewModel.ShowMemoryPressureWarningLabel = false;
            diagnosticsGroup.Children.Add(showMemoryPressureLabel);
            diagnosticsGroup.Children.Add(new TextBlock
            {
                Text = "Controls only the orange toolbar label shown while Yagu is paging results to disk. Memory-saving mode still activates normally.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var showStatsForNerds = new CheckBox
            {
                Content = "Stats for nerds",
                IsChecked = _viewModel.ShowStatsForNerds,
            };
            showStatsForNerds.Checked += (_, _) => _viewModel.ShowStatsForNerds = true;
            showStatsForNerds.Unchecked += (_, _) => _viewModel.ShowStatsForNerds = false;
            diagnosticsGroup.Children.Add(showStatsForNerds);
            diagnosticsGroup.Children.Add(new TextBlock
            {
                Text = "Shows the files/second and MB/s text, plus the disk throughput sparkline, MB/s, and utilization percentage in the bottom status bar.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var showBuildNumberInTitleBar = new CheckBox
            {
                Content = "Show build number in title bar",
                IsChecked = _viewModel.ShowBuildNumberInTitleBar,
            };
            showBuildNumberInTitleBar.Checked += (_, _) => _viewModel.ShowBuildNumberInTitleBar = true;
            showBuildNumberInTitleBar.Unchecked += (_, _) => _viewModel.ShowBuildNumberInTitleBar = false;
            diagnosticsGroup.Children.Add(showBuildNumberInTitleBar);
            diagnosticsGroup.Children.Add(new TextBlock
            {
                Text = "Adds the current Yagu version to the main title bar for diagnostics and screenshots. Hidden by default.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var showAutoScrollResultsCheckbox = new CheckBox
            {
                Content = "Show Auto-scroll checkbox",
                IsChecked = _viewModel.ShowAutoScrollResultsCheckbox,
            };
            showAutoScrollResultsCheckbox.Checked += (_, _) => _viewModel.ShowAutoScrollResultsCheckbox = true;
            showAutoScrollResultsCheckbox.Unchecked += (_, _) => _viewModel.ShowAutoScrollResultsCheckbox = false;
            diagnosticsGroup.Children.Add(showAutoScrollResultsCheckbox);
            diagnosticsGroup.Children.Add(new TextBlock
            {
                Text = "Shows the results-toolbar Auto-scroll checkbox for testing searches that continuously append result rows. Hidden by default.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var resetFontContrastReminder = new Button
            {
                Content = "Reset font contrast reminders",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 12, 0, 0),
            };
            resetFontContrastReminder.Click += (_, _) =>
            {
                _viewModel.ResetFontContrastReminderState();
                MarkSettingsDirty(requireValueChanges: false);
                resetFontContrastReminder.Content = "Font contrast reminders reset";
                resetFontContrastReminder.IsEnabled = false;
            };
            RegisterDefaultResetButton(resetFontContrastReminder,
                () => !_viewModel.SuppressFontContrastWarnings && _viewModel.FontContrastReminderAfterUtc is null);
            remindersGroup.Children.Add(resetFontContrastReminder);
            remindersGroup.Children.Add(new TextBlock
            {
                Text = "Allows theme/font contrast warnings to appear again after Remind me later or Don't remind me again.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var resetGitignorePrecedenceWarning = new Button
            {
                Content = "Reset .gitignore vs include filter warning",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 12, 0, 0),
            };
            resetGitignorePrecedenceWarning.Click += (_, _) =>
            {
                _viewModel.ResetGitignorePrecedencePreference();
                MarkSettingsDirty(requireValueChanges: false);
                resetGitignorePrecedenceWarning.Content = ".gitignore vs include filter warning reset";
                resetGitignorePrecedenceWarning.IsEnabled = false;
            };
            RegisterDefaultResetButton(resetGitignorePrecedenceWarning,
                () => _viewModel.GitignorePrecedencePreference is null);
            remindersGroup.Children.Add(resetGitignorePrecedenceWarning);
            remindersGroup.Children.Add(new TextBlock
            {
                Text = "Re-enables the prompt asking whether .gitignore or your Include filter wins after you chose Don't ask again or set a fixed preference in Search Defaults.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var resetMultilineNewlinePrompt = new Button
            {
                Content = "Reset multiline search prompt",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 12, 0, 0),
            };
            resetMultilineNewlinePrompt.Click += async (_, _) =>
            {
                await _viewModel.ResetMultilineNewlineSuggestionAsync();
                MarkSettingsDirty(requireValueChanges: false);
                resetMultilineNewlinePrompt.Content = "Multiline search prompt reset";
                resetMultilineNewlinePrompt.IsEnabled = false;
            };
            RegisterDefaultResetButton(resetMultilineNewlinePrompt,
                () => !_viewModel.MultilineNewlineSuggestionDismissed);
            remindersGroup.Children.Add(resetMultilineNewlinePrompt);
            remindersGroup.Children.Add(new TextBlock
            {
                Text = "Re-enables the prompt that offers to switch on Multiline search when your query contains a literal \u201c\\n\u201d escape, after you chose Don't warn me again.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var resetFirstTimeIntroTips = new Button
            {
                Content = "Reset first-time introductory tooltips",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 12, 0, 0),
            };
            resetFirstTimeIntroTips.Click += (_, _) =>
            {
                _viewModel.ResetFirstTimeIntroductoryTooltips();
                MarkSettingsDirty();
                RefreshDefaultResetButtons();
                resetFirstTimeIntroTips.Content = "First-time introductory tooltips reset";
            };
            RegisterDefaultResetButton(resetFirstTimeIntroTips, AreFirstTimeIntroductoryTooltipsReset);
            remindersGroup.Children.Add(resetFirstTimeIntroTips);
            remindersGroup.Children.Add(new TextBlock
            {
                Text = "Allows the file drawer, line-number, and preview-match introductory tooltips to appear again.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var resetCloseToTrayReminder = new Button
            {
                Content = "Reset close-to-tray reminder",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 12, 0, 0),
            };
            resetCloseToTrayReminder.Click += (_, _) =>
            {
                _viewModel.HasShownCloseToTrayNotification = false;
                MarkSettingsDirty(requireValueChanges: false);
                resetCloseToTrayReminder.Content = "Close-to-tray reminder reset";
                resetCloseToTrayReminder.IsEnabled = false;
            };
            RegisterDefaultResetButton(resetCloseToTrayReminder,
                () => !_viewModel.HasShownCloseToTrayNotification);
            remindersGroup.Children.Add(resetCloseToTrayReminder);
            remindersGroup.Children.Add(new TextBlock
            {
                Text = "Shows the explanatory dialog again the next time you close Yagu while \u201cDock to system tray when closed\u201d is enabled.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var resetSemanticModelCheck = new Button
            {
                Content = "Reset AI model check (re-prompt on startup)",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 12, 0, 0),
            };
            resetSemanticModelCheck.Click += async (_, _) =>
            {
                await _viewModel.ResetSemanticModelQualificationAsync();
                MarkSettingsDirty(requireValueChanges: false);
                RefreshDefaultResetButtons();
                resetSemanticModelCheck.Content = "AI model check reset";
            };
            RegisterDefaultResetButton(resetSemanticModelCheck,
                () => !_viewModel.HasSemanticModelQualificationState);
            remindersGroup.Children.Add(resetSemanticModelCheck);
            remindersGroup.Children.Add(new TextBlock
            {
                Text = "Re-runs the first-run AI (Semantic) model check on the next startup: clears the recorded result, forgets the selected model, and re-enables AI search so Yagu offers to test which models fit this PC again.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            // Reset admin warning
            if (_viewModel.SuppressAdminWarning)
            {
                var resetAdmin = new Button { Content = "Re-enable admin privilege warning", FontSize = 12, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0) };
                resetAdmin.Click += (_, _) =>
                {
                    _viewModel.SuppressAdminWarning = false;
                    MarkSettingsDirty(requireValueChanges: false);
                    resetAdmin.Content = "Admin warning re-enabled ✓";
                    resetAdmin.IsEnabled = false;
                };
                RegisterDefaultResetButton(resetAdmin, () => !_viewModel.SuppressAdminWarning);
                remindersGroup.Children.Add(resetAdmin);
                remindersGroup.Children.Add(new TextBlock { Text = "The non-administrator warning was previously dismissed. Click to show it again on next launch.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            }

            // Reset Everything-not-running prompt
            if (_viewModel.SuppressEverythingNotRunningPrompt)
            {
                var resetEverythingPrompt = new Button { Content = "Re-enable Everything not-running prompt", FontSize = 12, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0) };
                resetEverythingPrompt.Click += (_, _) =>
                {
                    _viewModel.SuppressEverythingNotRunningPrompt = false;
                    MarkSettingsDirty(requireValueChanges: false);
                    resetEverythingPrompt.Content = "Everything prompt re-enabled ✓";
                    resetEverythingPrompt.IsEnabled = false;
                };
                RegisterDefaultResetButton(resetEverythingPrompt, () => !_viewModel.SuppressEverythingNotRunningPrompt);
                remindersGroup.Children.Add(resetEverythingPrompt);
                remindersGroup.Children.Add(new TextBlock { Text = "The \u201cEverything Search is not running\u201d prompt was previously dismissed with \u201cDon\u2019t show this again\u201d. Click to show it again on next launch.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            }

            loggingGroup.Children.Add(new TextBlock { Text = "File log level:" });
            var fileLogRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var fileLogLevel = new ComboBox();
            fileLogLevel.Items.Add("None (logging disabled)");
            fileLogLevel.Items.Add("Critical (errors only)");
            fileLogLevel.Items.Add("Warning (errors + warnings)");
            fileLogLevel.Items.Add("Info (general activity)");
            fileLogLevel.Items.Add("Verbose (all details, may slow performance)");
            fileLogLevel.SelectedIndex = _viewModel.FileLogLevelIndex + 1;
            var fileLogWarn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            fileLogWarn.Children.Add(new FontIcon { Glyph = "\uE7BA", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold) });
            fileLogWarn.Children.Add(new TextBlock { Text = "Verbose logging will degrade performance", FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold), VerticalAlignment = VerticalAlignment.Center });
            if (fileLogLevel.SelectedIndex == 4) fileLogWarn.Visibility = Visibility.Visible;
            fileLogLevel.SelectionChanged += (_, _) =>
            {
                _viewModel.FileLogLevelIndex = fileLogLevel.SelectedIndex - 1;
                fileLogWarn.Visibility = fileLogLevel.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            };
            fileLogRow.Children.Add(fileLogLevel);
            fileLogRow.Children.Add(fileLogWarn);
            loggingGroup.Children.Add(fileLogRow);

            loggingGroup.Children.Add(new TextBlock { Text = "Console log level:" });
            var consoleLogRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var consoleLogLevel = new ComboBox();
            consoleLogLevel.Items.Add("None (logging disabled)");
            consoleLogLevel.Items.Add("Critical (errors only)");
            consoleLogLevel.Items.Add("Warning (errors + warnings)");
            consoleLogLevel.Items.Add("Info (general activity)");
            consoleLogLevel.Items.Add("Verbose (all details, may slow performance)");
            consoleLogLevel.SelectedIndex = _viewModel.ConsoleLogLevelIndex + 1;
            var consoleLogWarn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            consoleLogWarn.Children.Add(new FontIcon { Glyph = "\uE7BA", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold) });
            consoleLogWarn.Children.Add(new TextBlock { Text = "Verbose logging will degrade performance", FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold), VerticalAlignment = VerticalAlignment.Center });
            if (consoleLogLevel.SelectedIndex == 4) consoleLogWarn.Visibility = Visibility.Visible;
            consoleLogLevel.SelectionChanged += (_, _) =>
            {
                _viewModel.ConsoleLogLevelIndex = consoleLogLevel.SelectedIndex - 1;
                consoleLogWarn.Visibility = consoleLogLevel.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            };
            consoleLogRow.Children.Add(consoleLogLevel);
            consoleLogRow.Children.Add(consoleLogWarn);
            loggingGroup.Children.Add(consoleLogRow);

            // Log file path is a clickable link that opens the active log file in Notepad.
            var logPath = LogService.DefaultLogPath();
            var logFileBlock = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
            logFileBlock.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Log file: " });
            var logHyperlink = new Microsoft.UI.Xaml.Documents.Hyperlink();
            logHyperlink.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = logPath });
            logHyperlink.Click += (_, _) => OpenLogFileInNotepad(logPath);
            logFileBlock.Inlines.Add(logHyperlink);
            ToolTipService.SetToolTip(logFileBlock, "Open the log file in Notepad");
            loggingGroup.Children.Add(logFileBlock);

            // "Clear log file" empties the current log file's contents on demand.
            var clearLogButton = new Button { Content = "Clear log file", Margin = new Thickness(0, 4, 0, 0) };
            ToolTipService.SetToolTip(clearLogButton, "Erase the contents of the current log file.");
            clearLogButton.Click += (_, _) =>
            {
                bool cleared = LogService.Instance.Clear();
                clearLogButton.Content = cleared ? "Log file cleared \u2713" : "Could not clear log file";
                clearLogButton.IsEnabled = false;
                var resetTimer = DispatcherQueue.CreateTimer();
                resetTimer.Interval = TimeSpan.FromSeconds(2);
                resetTimer.IsRepeating = false;
                resetTimer.Tick += (_, _) =>
                {
                    resetTimer.Stop();
                    clearLogButton.Content = "Clear log file";
                    clearLogButton.IsEnabled = true;
                };
                resetTimer.Start();
            };
            loggingGroup.Children.Add(clearLogButton);
        }

        // ── Shortcuts & History ──
        {
            var g = AddTab("Shortcuts & History");
            var hotkeyGroup = AddSettingsGroupBox(g, "Global Hotkey");
            var historyGroup = AddSettingsGroupBox(g, "Recent Items");

            var hotkey = new CheckBox { Content = "Enable global hotkey", IsChecked = _viewModel.GlobalHotkeyEnabled, IsEnabled = false };
            hotkey.Checked += (_, _) => _viewModel.GlobalHotkeyEnabled = true;
            hotkey.Unchecked += (_, _) => _viewModel.GlobalHotkeyEnabled = false;
            hotkeyGroup.Children.Add(hotkey);
            hotkeyGroup.Children.Add(new TextBlock { Text = "Yagu registers Ctrl+Shift+letter combinations that are not already taken by Windows or another app.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            hotkeyGroup.Children.Add(new TextBlock { Text = "Global hotkey:" });
            var hotkeyCombo = new ComboBox { IsEnabled = false };
            hotkeyCombo.Items.Add("Checking available shortcuts...");
            hotkeyCombo.SelectedIndex = 0;
            hotkeyCombo.SelectionChanged += (_, _) =>
            {
                if (hotkeyCombo.SelectedItem is ComboBoxItem item && item.Tag is string key)
                    _viewModel.GlobalHotkeyKey = key;
            };
            hotkeyGroup.Children.Add(hotkeyCombo);

            var hotkeyAvailabilityStatus = new TextBlock
            {
                Text = "Checking available shortcuts...",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            };
            hotkeyGroup.Children.Add(hotkeyAvailabilityStatus);
            QueueHotkeyAvailabilityLoad(hotkey, hotkeyCombo, hotkeyAvailabilityStatus);

            historyGroup.Children.Add(new TextBlock { Text = "Max recent directories / queries to remember:" });
            var recent = new NumberBox { Value = _viewModel.MaxRecentItems, Minimum = 1, Maximum = 100 };
            recent.ValueChanged += (_, args) => _viewModel.MaxRecentItems = (int)args.NewValue;
            historyGroup.Children.Add(recent);
            historyGroup.Children.Add(new TextBlock { Text = "Controls how many directory and Traditional search-query suggestions Yagu keeps in the launcher and search fields.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            historyGroup.Children.Add(new TextBlock { Text = "Max semantic queries to remember:", Margin = new Thickness(0, 8, 0, 0) });
            var semanticRecent = new NumberBox { Value = _viewModel.MaxSemanticRecentItems, Minimum = 1, Maximum = 100 };
            semanticRecent.ValueChanged += (_, args) => _viewModel.MaxSemanticRecentItems = (int)args.NewValue;
            historyGroup.Children.Add(semanticRecent);
            historyGroup.Children.Add(new TextBlock { Text = "Controls how many natural-language Semantic-mode queries Yagu keeps for autocomplete, separate from Traditional search history.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── AI ──
        {
            var g = AddTab("AI");
            var aiGroup = AddSettingsGroupBox(g, "AI (Semantic) Search");
            var modelGroup = AddSettingsGroupBox(g, "Model");
            var memoryGroup = AddSettingsGroupBox(g, "GPU Memory");
            var deviceGroup = AddSettingsGroupBox(g, "Accelerator Preference");
            var hardwareGroup = AddSettingsGroupBox(g, "Detected Hardware");

            // Controls disabled while the feature is off; re-enabled live by the toggle below.
            var dependentControls = new List<Control>();

            var enableToggle = new ToggleSwitch
            {
                IsOn = _viewModel.SemanticSearchAvailable,
                OnContent = "Enable AI (semantic) search",
                OffContent = "Enable AI (semantic) search",
            };
            enableToggle.Toggled += (_, _) =>
            {
                _viewModel.SemanticSearchAvailable = enableToggle.IsOn;
                foreach (var c in dependentControls) c.IsEnabled = enableToggle.IsOn;
                MarkSettingsDirty(requireValueChanges: false);
            };
            aiGroup.Children.Add(enableToggle);
            aiGroup.Children.Add(new TextBlock { Text = "Lets you search with plain-English requests (e.g. \"png files on C: modified last year\"), translated on-device by a small local AI model. No query ever leaves your PC. Turn off to use only Traditional search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            bool overrideEnabled = _viewModel.SemanticDefaultOverrideEnabled;
            var defaultTraditional = new CheckBox
            {
                Content = NextSearchLabel("Default to Traditional search mode"),
                IsChecked = overrideEnabled && _viewModel.DefaultToTraditionalSearchMode,
                IsEnabled = overrideEnabled,
                Margin = new Thickness(0, 4, 0, 0),
            };
            defaultTraditional.Checked += (_, _) => { _viewModel.DefaultToTraditionalSearchMode = true; MarkSettingsDirty(requireValueChanges: false); };
            defaultTraditional.Unchecked += (_, _) => { _viewModel.DefaultToTraditionalSearchMode = false; MarkSettingsDirty(requireValueChanges: false); };
            aiGroup.Children.Add(defaultTraditional);
            aiGroup.Children.Add(new TextBlock
            {
                Text = overrideEnabled
                    ? "Your machine has a GPU/NPU that can run AI search, so the search bar defaults to Semantic. Check this to default to Traditional instead. You can always switch modes from the search-button chevron."
                    : "Disabled: no supported GPU/NPU was detected, so Yagu always defaults to Traditional search. Semantic mode can still be selected manually from the search-button chevron when available.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var modelAlerts = new CheckBox
            {
                Content = "Alert me when new on-device models are available",
                IsChecked = _viewModel.FoundryModelUpdateAlertsEnabled,
                Margin = new Thickness(0, 4, 0, 0),
            };
            modelAlerts.Checked += (_, _) => { _viewModel.FoundryModelUpdateAlertsEnabled = true; MarkSettingsDirty(requireValueChanges: false); };
            modelAlerts.Unchecked += (_, _) => { _viewModel.FoundryModelUpdateAlertsEnabled = false; MarkSettingsDirty(requireValueChanges: false); };
            aiGroup.Children.Add(modelAlerts);
            aiGroup.Children.Add(new TextBlock
            {
                Text = "About once a day, Yagu checks Foundry Local for new, updated, or variant on-device models and shows a one-time alert. Only runs after you've used AI search at least once.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
            dependentControls.Add(modelAlerts);

            // ── Model ──
            modelGroup.Children.Add(NextSearchLabel("Current model:"));
            var modelValue = new TextBlock
            {
                Text = _viewModel.CurrentSemanticModelDisplay,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            modelGroup.Children.Add(modelValue);

            // Resolve + show the ACTUAL current model in the background (non-blocking) so automatic mode
            // shows e.g. "phi-4 (automatic)" instead of a generic label, even before the first AI search.
            if (_viewModel.SemanticSearchAvailable)
            {
                _ = ResolveModelDisplayAsync();
                async Task ResolveModelDisplayAsync()
                {
                    try { modelValue.Text = await _viewModel.ResolveCurrentSemanticModelDisplayAsync(null, CancellationToken.None); }
                    catch { /* leave whatever is shown */ }
                }
            }

            var modelButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            var chooseModel = new Button { Content = "Choose / download model\u2026" };
            chooseModel.Click += async (_, _) =>
            {
                var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
                await SemanticModelDownloadDialog.ShowAsync(
                    _settingsHwnd,
                    theme,
                    (progress, token) => _viewModel.GetSemanticModelOptionsAsync(progress, token),
                    (alias, progress, token) => _viewModel.PrepareSemanticModelAsync(alias, progress, token),
                    _viewModel.SemanticModelAlias);
                modelValue.Text = _viewModel.CurrentSemanticModelDisplay;
            };
            var resetModel = new Button { Content = "Use recommended (automatic)" };
            resetModel.Click += async (_, _) =>
            {
                await _viewModel.ClearSemanticModelOverrideAsync();
                modelValue.Text = _viewModel.CurrentSemanticModelDisplay;
                try { modelValue.Text = await _viewModel.ResolveCurrentSemanticModelDisplayAsync(null, CancellationToken.None); }
                catch { /* leave the generic automatic label */ }
            };
            modelButtons.Children.Add(chooseModel);
            modelButtons.Children.Add(resetModel);
            modelGroup.Children.Add(modelButtons);
            modelGroup.Children.Add(new TextBlock { Text = "Pick which on-device model translates your requests. The recommended model is the best fit for your hardware; in the picker, any model that is not the one you're currently using is flagged \"Results may vary.\" Changing the model takes effect on your next AI search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            dependentControls.Add(chooseModel);
            dependentControls.Add(resetModel);

            // ── Re-run model probe ──
            var probeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            var rerunProbe = new Button { Content = "Re-run model probe\u2026" };
            rerunProbe.Click += async (_, _) =>
            {
                rerunProbe.IsEnabled = false;
                try
                {
                    var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
                    var result = await SemanticModelQualificationDialog.ShowAsync(
                        _settingsHwnd,
                        theme,
                        (thresholds, progress, token) => _viewModel.RunSemanticModelQualificationAsync(thresholds, progress, token));

                    if (result.Cancelled)
                        return; // User cancelled the sweep; leave settings untouched.

                    if (result.SwitchToTraditional)
                    {
                        // The user chose "switch to Traditional" from the no-usable-model notice: honor it.
                        await _viewModel.DeclineAndDisableSemanticSearchAsync();
                    }
                    else if (result.Accepted && result.Result is not null)
                    {
                        await _viewModel.ApplySemanticModelQualificationAsync(result.Result, accepted: true, result.ChosenAlias);
                    }
                    else if (result.Result is not null)
                    {
                        // Finished but skipped: record the recommendation without switching models.
                        await _viewModel.ApplySemanticModelQualificationAsync(result.Result, accepted: false);
                    }

                    // Reflect any model/enabled change back into the tab.
                    enableToggle.IsOn = _viewModel.SemanticSearchAvailable;
                    modelValue.Text = _viewModel.CurrentSemanticModelDisplay;
                    try { modelValue.Text = await _viewModel.ResolveCurrentSemanticModelDisplayAsync(null, CancellationToken.None); }
                    catch { /* leave whatever is shown */ }
                }
                finally
                {
                    rerunProbe.IsEnabled = _viewModel.SemanticSearchAvailable;
                }
            };
            probeRow.Children.Add(rerunProbe);
            modelGroup.Children.Add(probeRow);
            modelGroup.Children.Add(new TextBlock { Text = "Re-test the AI models that fit this PC with a mix of sample searches and pick the fastest one that answers accurately \u2014 the same check Yagu runs on first setup. Handy after downloading new models or changing hardware.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            dependentControls.Add(rerunProbe);

            // ── Refresh Foundry cache ──
            var refreshRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            var refreshCache = new Button { Content = "Refresh Foundry cache" };
            refreshCache.Click += async (_, _) =>
            {
                refreshCache.IsEnabled = false;
                modelValue.Text = "Refreshing\u2026";
                try
                {
                    modelValue.Text = await _viewModel.RefreshFoundryCacheAsync(null, CancellationToken.None);
                }
                catch
                {
                    modelValue.Text = _viewModel.CurrentSemanticModelDisplay;
                }
                finally
                {
                    refreshCache.IsEnabled = _viewModel.SemanticSearchAvailable;
                }
            };
            refreshRow.Children.Add(refreshCache);
            modelGroup.Children.Add(refreshRow);
            modelGroup.Children.Add(new TextBlock { Text = "Re-scan Foundry Local for models you've downloaded or updated out of band, and re-resolve the current model shown above. Takes effect on your next AI search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            dependentControls.Add(refreshCache);

            // ── GPU memory ──
            var unloadToggle = new ToggleSwitch
            {
                IsOn = _viewModel.SemanticUnloadModelAfterUse,
                OnContent = "Release model from memory after each search",
                OffContent = "Release model from memory after each search",
            };
            unloadToggle.Toggled += (_, _) =>
            {
                _viewModel.SemanticUnloadModelAfterUse = unloadToggle.IsOn;
                MarkSettingsDirty(requireValueChanges: false);
            };
            memoryGroup.Children.Add(unloadToggle);
            memoryGroup.Children.Add(new TextBlock
            {
                Text = "The on-device model can occupy several GB of GPU VRAM while loaded. On by default: the model is unloaded as soon as each AI search finishes, freeing that memory for other apps, and reloaded on the next search (a few seconds). Turn off to keep the model resident for the fastest back-to-back searches. Either way, translation quality is identical.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
            dependentControls.Add(unloadToggle);

            // ── Accelerator preference ──
            deviceGroup.Children.Add(NextSearchLabel("Preferred accelerator order (which device runs the model):"));
            var deviceOrders = new (string Value, string Display)[]
            {
                ("GPU,NPU,CPU", "GPU \u2192 NPU \u2192 CPU"),
                ("GPU,CPU,NPU", "GPU \u2192 CPU \u2192 NPU"),
                ("NPU,GPU,CPU", "NPU \u2192 GPU \u2192 CPU"),
                ("NPU,CPU,GPU", "NPU \u2192 CPU \u2192 GPU"),
                ("CPU,GPU,NPU", "CPU \u2192 GPU \u2192 NPU"),
                ("CPU,NPU,GPU", "CPU \u2192 NPU \u2192 GPU"),
            };
            var deviceCombo = new ComboBox { Width = 240, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0) };
            foreach (var o in deviceOrders) deviceCombo.Items.Add(o.Display);
            int curDeviceIdx = Array.FindIndex(deviceOrders, o => string.Equals(o.Value, _viewModel.SemanticDevicePreferenceOrder, StringComparison.OrdinalIgnoreCase));
            deviceCombo.SelectedIndex = curDeviceIdx >= 0 ? curDeviceIdx : 0;
            deviceCombo.SelectionChanged += (_, _) =>
            {
                if (deviceCombo.SelectedIndex >= 0)
                {
                    _viewModel.SemanticDevicePreferenceOrder = deviceOrders[deviceCombo.SelectedIndex].Value;
                    MarkSettingsDirty(requireValueChanges: false);
                }
            };
            deviceGroup.Children.Add(deviceCombo);
            deviceGroup.Children.Add(new TextBlock { Text = "Yagu prefers the first listed device that can run the model. GPU usually gives the most accurate results for this task; NPU is most power-efficient. Applies to your next AI search \u2014 no restart needed.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            dependentControls.Add(deviceCombo);

            // ── Detected hardware (read-only) ──
            hardwareGroup.Children.Add(new TextBlock
            {
                Text = $"GPU: {(_viewModel.SemanticHasGpu ? "Detected" : "Not detected")}\nNPU: {(_viewModel.SemanticHasNpu ? "Detected" : "Not detected")}\nCPU: Always available (fallback)",
                TextWrapping = TextWrapping.Wrap,
            });
            hardwareGroup.Children.Add(new TextBlock { Text = "Detected via the Windows device registry. The available model builds and accelerators ultimately depend on what Foundry Local can run on this machine.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            foreach (var c in dependentControls) c.IsEnabled = _viewModel.SemanticSearchAvailable;
        }

        // ── Privacy ──
        {
            var g = AddTab("Privacy");
            var helpGroup = AddSettingsGroupBox(g, "Help Improve Yagu");
            var statusGroup = AddSettingsGroupBox(g, "What's Sent & Where");

            var telemetryToggle = new ToggleSwitch
            {
                IsOn = _viewModel.TelemetryEnabledSetting,
                OnContent = "Send anonymous performance & error reports",
                OffContent = "Send anonymous performance & error reports",
            };
            telemetryToggle.Toggled += (_, _) =>
            {
                _viewModel.TelemetryEnabledSetting = telemetryToggle.IsOn;
                MarkSettingsDirty(requireValueChanges: false);
            };
            helpGroup.Children.Add(telemetryToggle);
            helpGroup.Children.Add(new TextBlock
            {
                Text = "Silently shares anonymized, aggregate diagnostics \u2014 app version, OS, error types and timing \u2014 so problems can be spotted and fixed. File paths and search text are stripped out before anything is sent, and nothing is sent in command-line mode.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            var bugReportToggle = new ToggleSwitch
            {
                IsOn = _viewModel.BugReportingEnabledSetting,
                OnContent = "Offer to send a bug report when an error occurs",
                OffContent = "Offer to send a bug report when an error occurs",
                Margin = new Thickness(0, 8, 0, 0),
            };
            bugReportToggle.Toggled += (_, _) =>
            {
                _viewModel.BugReportingEnabledSetting = bugReportToggle.IsOn;
                MarkSettingsDirty(requireValueChanges: false);
            };
            helpGroup.Children.Add(bugReportToggle);
            helpGroup.Children.Add(new TextBlock
            {
                Text = "When a serious error happens, Yagu offers a dialog that shows you exactly what would be sent \u2014 including your settings file and a tail of the log \u2014 and only submits if you choose to. Independent of the anonymous reports above.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            helpGroup.Children.Add(NextSearchLabel("Contact email for bug reports (optional):"));
            var emailBox = new TextBox
            {
                Text = _viewModel.BugReportContactEmail,
                PlaceholderText = "you@example.com",
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            emailBox.LostFocus += (_, _) =>
            {
                _viewModel.BugReportContactEmail = emailBox.Text?.Trim() ?? string.Empty;
                MarkSettingsDirty(requireValueChanges: false);
            };
            helpGroup.Children.Add(emailBox);
            helpGroup.Children.Add(new TextBlock
            {
                Text = "Pre-fills the bug-report dialog so a developer can follow up. Leave blank to report anonymously.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            statusGroup.Children.Add(new TextBlock
            {
                Text = Yagu.Services.Telemetry.TelemetryConfig.IsConfigured
                    ? "Reports are sent to a private endpoint maintained by this build. No third-party analytics or advertising services are used."
                    : "This build has no reporting endpoint configured yet, so nothing is sent regardless of the toggles above \u2014 your choices are remembered for when one is added.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        SortTabsAlphabetically();
    }
}
