using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.System;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.ViewModels;
using XamlFontFamily = Microsoft.UI.Xaml.Media.FontFamily;

namespace Yagu;

public sealed partial class SettingsWindow : Window
{
    private static IReadOnlyList<string>? _systemFontFamilyNames;

    private static readonly string[] TabHeaders =
    [
        "Search Defaults",
        "Search Limits",
        "Performance",
        "Display",
        "Editor",
        "Window",
        "UI Behaviors",
        "Terminal Emulator",
        "Developer Options",
        "General",
    ];

    private readonly MainViewModel _viewModel;
    private readonly HotkeyService _hotkeyService;
    private readonly IntPtr _mainHwnd;
    private readonly AppWindow _appWindow;
    private readonly Action<bool>? _applyWordWrap;
    private readonly Action? _applyPreviewSectionBackgrounds;
    private readonly Action _openHelp;
    private readonly List<UIElement> _tabPages = new();
    private readonly List<List<UIElement>> _tabPageRootElements = new();
    private readonly HashSet<UIElement> _dirtyTrackedElements = new();
    private readonly Dictionary<UIElement, object?> _cleanSettingValues = new();
    private readonly List<Action> _fontContrastStatusRefreshers = new();
    private bool _settingsDirty;
    private bool _settingDirtyTrackingEnabled;
    private DispatcherTimer? _fontContrastCheckTimer;

    private static readonly Windows.UI.Color ContrastReadableGreen = Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43);
    private static readonly Windows.UI.Color ContrastUnreadableRed = Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38);

    /// <summary>Flat registry of every setting built during BuildSettingsContent for search filtering.</summary>
    private readonly List<SettingEntry> _settingEntries = new();

    private readonly List<Panel> _searchResultContainers = new();

    /// <summary>Represents a single setting item with searchable text and its UI elements.</summary>
    private sealed class SettingEntry
    {
        public required string TabHeader { get; init; }
        public required string Label { get; init; }
        public string? Description { get; init; }
        public string? ControlText { get; init; }
        public required IReadOnlyList<ElementPlacement> OriginalPlacements { get; init; }
        /// <summary>Factory that returns the setting UI elements for display in search results.</summary>
        public required Func<IEnumerable<UIElement>> BuildElements { get; init; }

        public bool Matches(string query)
        {
            return Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (Description is not null && Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                || (ControlText is not null && ControlText.Contains(query, StringComparison.OrdinalIgnoreCase))
                || TabHeader.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ElementPlacement
    {
        public required UIElement Element { get; init; }
        public required Panel Parent { get; init; }
        public required int Index { get; init; }
    }

    public SettingsWindow(MainViewModel viewModel, HotkeyService hotkeyService, IntPtr mainHwnd, Action<bool>? applyWordWrap, Action? applyPreviewSectionBackgrounds, Action openHelp)
    {
        _viewModel = viewModel;
        _hotkeyService = hotkeyService;
        _mainHwnd = mainHwnd;
        _applyWordWrap = applyWordWrap;
        _applyPreviewSectionBackgrounds = applyPreviewSectionBackgrounds;
        _openHelp = openHelp;
        InitializeComponent();

        InitializeHelpKeyboardShortcut();

        // Set up custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Size and center over the owner window
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, mainHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        const int w = 903, h = 820;
        appWindow.Resize(new SizeInt32(w, h));
        CenterOverOwner(appWindow, mainHwnd, w, h);

        ApplySettingsTheme();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplySettingsTitleBarButtonTheme();
            RefreshFontContrastStatusIndicators();
            QueueFontContrastCheck();
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            _fontContrastCheckTimer?.Stop();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };

        // Set window icon to match the main Yagu window
        var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
        if (System.IO.File.Exists(icoPath))
            appWindow.SetIcon(icoPath);

        BuildSettingsContent();
        AttachSettingDirtyHandlers();
        MarkSettingsClean();
        ExtractSearchableEntries();
        TabList.SelectedIndex = 0;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
            RootGrid.XamlRoot,
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
        if (index < 0 || index >= TabList.Items.Count)
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
            RestoreTabPageElements();
        }

        TabList.SelectedIndex = index;
        SettingsContent.Children.Clear();
        if (index < _tabPages.Count)
            SettingsContent.Children.Add(_tabPages[index]);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static void CenterOverOwner(AppWindow appWindow, IntPtr ownerHwnd, int w, int h)
    {
        if (ownerHwnd == IntPtr.Zero) return;
        if (!GetWindowRect(ownerHwnd, out var rc)) return;
        int ownerCx = (rc.Left + rc.Right) / 2;
        int ownerCy = (rc.Top + rc.Bottom) / 2;
        int x = ownerCx - w / 2;
        int y = ownerCy - h / 2;

        // Clamp to the work area of the owner's monitor
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var hMon = MonitorFromWindow(ownerHwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMon, ref mi))
        {
            var work = mi.rcWork;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
            if (x + w > work.Right) x = work.Right - w;
            if (y + h > work.Bottom) y = work.Bottom - h;
        }

        appWindow.Move(new PointInt32(x, y));
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        await _viewModel.PersistSettingsAsync();
        MarkSettingsClean();
        Close();
    }

    private void AttachSettingDirtyHandlers()
    {
        foreach (var page in _tabPages)
            AttachSettingDirtyHandlers(page);

        _settingDirtyTrackingEnabled = true;
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
            case ColorPicker colorPicker:
                colorPicker.ColorChanged += (_, _) => MarkSettingsDirty();
                break;
        }

        if (element is Border { Child: UIElement child })
            AttachSettingDirtyHandlers(child);
        else if (element is Panel panel)
            foreach (var childElement in panel.Children)
                AttachSettingDirtyHandlers(childElement);
    }

    private void MarkSettingsDirty()
    {
        if (!_settingDirtyTrackingEnabled || _settingsDirty)
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
    }

    private void MarkSettingsDirtyIfCurrentValuesChanged()
    {
        if (!_settingDirtyTrackingEnabled || _settingsDirty)
            return;

        foreach (var element in _dirtyTrackedElements)
        {
            if (!TryGetSettingValue(element, out var currentValue))
                continue;
            if (!_cleanSettingValues.TryGetValue(element, out var cleanValue)
                || !Equals(cleanValue, currentValue))
            {
                MarkSettingsDirty();
                return;
            }
        }
    }

    private static bool TryGetSettingValue(UIElement element, out object? value)
    {
        switch (element)
        {
            case TextBox textBox:
                value = textBox.Text ?? string.Empty;
                return true;
            case NumberBox numberBox:
                value = (numberBox.Value, numberBox.Text ?? string.Empty);
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
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSearchActive) return;
        int idx = TabList.SelectedIndex;
        SettingsContent.Children.Clear();
        if (idx >= 0 && idx < _tabPages.Count)
            SettingsContent.Children.Add(_tabPages[idx]);
    }

    private bool _isSearchActive;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            // Restore normal tab view.
            _isSearchActive = false;
            TabList.Visibility = Visibility.Visible;

            // Detach any elements currently in the search results panel.
            ClearSearchResultContainers();
            SettingsContent.Children.Clear();

            // Re-parent all elements back to their owning tab pages.
            RestoreTabPageElements();

            int idx = TabList.SelectedIndex;
            if (idx >= 0 && idx < _tabPages.Count)
                SettingsContent.Children.Add(_tabPages[idx]);
            return;
        }

        _isSearchActive = true;
        TabList.Visibility = Visibility.Collapsed;
        ClearSearchResultContainers();
        SettingsContent.Children.Clear();

        // Detach all elements from their tab page parents so they can be re-parented.
        DetachAllTabPageElements();

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

            // Add the setting's UI elements.
            var container = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 8) };
            foreach (var element in entry.BuildElements())
            {
                if (element is FrameworkElement fe && fe.Parent is Panel p)
                    p.Children.Remove(element);
                container.Children.Add(element);
            }
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

    private void DetachAllTabPageElements()
    {
        foreach (var page in _tabPages)
        {
            if (page is StackPanel sp)
                sp.Children.Clear();
        }
    }

    private void ClearSearchResultContainers()
    {
        foreach (var container in _searchResultContainers)
            container.Children.Clear();
        _searchResultContainers.Clear();
    }

    private void RestoreTabPageElements()
    {
        // Restore each tab's original root children first, including grouped panels.
        for (int t = 0; t < _tabPages.Count; t++)
        {
            if (_tabPages[t] is not StackPanel sp) continue;
            sp.Children.Clear();

            if (t >= _tabPageRootElements.Count) continue;
            foreach (var element in _tabPageRootElements[t])
            {
                DetachFromParent(element);
                sp.Children.Add(element);
            }
        }

        foreach (var entry in _settingEntries)
            RestoreOriginalPlacements(entry.OriginalPlacements);
    }

    private static void RestoreOriginalPlacements(IEnumerable<ElementPlacement> placements)
    {
        foreach (var placement in placements.OrderBy(p => p.Index))
        {
            DetachFromParent(placement.Element);
            int index = Math.Clamp(placement.Index, 0, placement.Parent.Children.Count);
            placement.Parent.Children.Insert(index, placement.Element);
        }
    }

    private static void DetachFromParent(UIElement element)
    {
        if (element is FrameworkElement fe && fe.Parent is Panel parentPanel)
            parentPanel.Children.Remove(element);
    }

    /// <summary>
    /// After all tabs are built, walks each tab page to extract searchable setting entries.
    /// Groups children into logical entries: a label/heading element followed by its
    /// control(s) and optional description text until the next label/heading.
    /// </summary>
    private void ExtractSearchableEntries()
    {
        CaptureTabPageRootElements();

        for (int t = 0; t < _tabPages.Count && t < TabHeaders.Length; t++)
        {
            string tabHeader = TabHeaders[t];
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
                    var capturedPlacements = CaptureOriginalPlacements(captured);
                    _settingEntries.Add(new SettingEntry
                    {
                        TabHeader = capturedTab,
                        Label = capturedLabel,
                        Description = capturedDesc,
                        ControlText = capturedControlText,
                        OriginalPlacements = capturedPlacements,
                        BuildElements = () => captured,
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
                var capturedPlacements = CaptureOriginalPlacements(captured);
                _settingEntries.Add(new SettingEntry
                {
                    TabHeader = capturedTab,
                    Label = capturedLabel,
                    Description = capturedDesc,
                    ControlText = capturedControlText,
                    OriginalPlacements = capturedPlacements,
                    BuildElements = () => captured,
                });
            }
        }
    }

    private void CaptureTabPageRootElements()
    {
        _tabPageRootElements.Clear();
        foreach (var page in _tabPages)
        {
            if (page is StackPanel sp)
                _tabPageRootElements.Add(sp.Children.ToList());
            else
                _tabPageRootElements.Add(new List<UIElement>());
        }
    }

    private static List<ElementPlacement> CaptureOriginalPlacements(IEnumerable<UIElement> elements)
    {
        var placements = new List<ElementPlacement>();
        foreach (var element in elements)
        {
            if (element is not FrameworkElement { Parent: Panel parent })
                continue;

            int index = IndexOfChild(parent, element);
            if (index < 0)
                continue;

            placements.Add(new ElementPlacement
            {
                Element = element,
                Parent = parent,
                Index = index,
            });
        }

        return placements;
    }

    private static int IndexOfChild(Panel parent, UIElement element)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (ReferenceEquals(parent.Children[i], element))
                return i;
        }

        return -1;
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
                    AddSearchTextPart(parts, numberBox.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case ComboBox comboBox:
                AddSearchTextPart(parts, comboBox.PlaceholderText);
                CollectObjectSearchText(comboBox.SelectedItem, parts);
                foreach (var item in comboBox.Items)
                    CollectObjectSearchText(item, parts);
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
                AddSearchTextPart(parts, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
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

        foreach (string fontFamily in BuildFontFamilyOptions(selectedValue, defaultValue))
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

        return picker;
    }

    private static void SelectFontFamily(ComboBox picker, string fontFamily)
    {
        string normalized = fontFamily.Trim();
        ComboBoxItem? item = picker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => candidate.Tag is string candidateFontFamily
                && string.Equals(candidateFontFamily, normalized, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            item = CreateFontFamilyItem(normalized);
            picker.Items.Insert(0, item);
        }

        picker.SelectedItem = item;
    }

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
        var options = new List<string>();
        AddFontFamilyOption(options, currentValue);
        AddFontFamilyOption(options, defaultValue);

        foreach (string fontFamily in GetSystemFontFamilyNames())
            AddFontFamilyOption(options, fontFamily);

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
                AppSettings.DefaultResultListMatchTextFontFamily,
                "Cascadia Mono",
                "Courier New",
                "Segoe UI",
                "Arial",
            ];
        }

        return _systemFontFamilyNames;
    }

    private static ColorPicker AddColorSetting(
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

        var picker = new ColorPicker
        {
            Color = ColorStringHelper.Parse(currentHex, fallback),
            IsAlphaEnabled = true,
            Width = 160,
            MaxWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        FontIcon? contrastIcon = null;
        TextBlock? contrastText = null;
        void refreshContrastStatus()
        {
            if (contrastThemeProvider is null || contrastIcon is null || contrastText is null)
                return;

            var theme = contrastThemeProvider();
            var status = contrastStatusProvider?.Invoke(picker.Color, theme)
                ?? GetThemeSampleContrastStatus(picker.Color, theme);
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

        picker.ColorChanged += (_, args) =>
        {
            assign(ColorStringHelper.ToHex(args.NewColor));
            afterChange?.Invoke();
            refreshContrastStatus();
        };
        parent.Children.Add(picker);

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
        reset.Click += (_, _) => picker.Color = fallback;
        parent.Children.Add(reset);

        parent.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        return picker;
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
        "Performance" => "\uE9F5",
        "Display" => "\uE7B5",
        "Editor" => "\uE70F",
        "Window" => "\uE737",
        "UI Behaviors" => "\uE7C4",
        "Terminal Emulator" => "\uE756",
        "Developer Options" => "\uE713",
        "General" => "\uE713",
        _ => "\uE7FC",
    };

    private void AddResultTempDriveSetting(StackPanel parent)
    {
        string? launchDrive = ResultStoreTempLocationService.GetLaunchDriveRoot();
        var options = ResultStoreTempLocationService.GetWritableDriveOptions(launchDrive);

        parent.Children.Add(NextSearchLabel("Search result temp-file drive:"));
        parent.Children.Add(new TextBlock
        {
            Text = $"Yagu was launched from {launchDrive ?? "an unknown drive"}. Choosing a different drive can reduce disk contention. Only writable drives with at least 50 GB free are listed.",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        if (options.Count == 0)
        {
            parent.Children.Add(new TextBlock
            {
                Text = "No writable drive with at least 50 GB free is currently available. Yagu will use the Windows temp folder until an eligible drive is selected.",
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var picker = new ComboBox
        {
            MinWidth = 340,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

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

        void ApplySelectedDrive()
        {
            if (picker.SelectedItem is ComboBoxItem item && item.Tag is ResultStoreTempDriveOption option)
            {
                _viewModel.SearchResultTempDirectory = option.TempDirectory;
                _viewModel.HasChosenSearchResultTempDirectory = true;
            }
        }

        picker.SelectionChanged += (_, _) => ApplySelectedDrive();

        if (picker.SelectedIndex < 0 && picker.Items.Count > 0)
            picker.SelectedIndex = 0;

        ApplySelectedDrive();

        parent.Children.Add(picker);
        parent.Children.Add(new TextBlock
        {
            Text = "Temp files will be written under the selected drive's Temp\\Yagu folder starting with the next search.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void AddTerminalEmulationSetting(StackPanel parent)
    {
        parent.Children.Add(new TextBlock { Text = "Terminal Emulator", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
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
        browse.Click += async (_, _) => await PickTerminalWorkingDirectoryAsync(workingDirectory);
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

    private async System.Threading.Tasks.Task PickTerminalWorkingDirectoryAsync(TextBox target)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        target.Text = folder.Path;
        _viewModel.TerminalDefaultWorkingDirectory = folder.Path;
    }

    private void BuildSettingsContent()
    {
        // ── Search Defaults ──
        {
            var g = AddTab("Search Defaults");

            g.Children.Add(NextSearchLabel("Context lines (lines shown before & after each match in results):"));
            var ctx = new NumberBox { Value = _viewModel.ContextLines, Minimum = 0, Maximum = 50 };
            ctx.ValueChanged += (_, args) => _viewModel.ContextLines = (int)args.NewValue;
            g.Children.Add(ctx);

            g.Children.Add(new TextBlock { Text = "Preview context lines (lines shown around each match in the preview panel):" });
            var prevCtx = new NumberBox { Value = _viewModel.PreviewContextLines, Minimum = 0 };
            prevCtx.ValueChanged += (_, args) => _viewModel.PreviewContextLines = (int)args.NewValue;
            g.Children.Add(prevCtx);

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
            g.Children.Add(includeHeader);

            var incGlobs = new TextBox { Text = _viewModel.IncludeGlobs, PlaceholderText = _viewModel.IncludeFilterPlaceholder, Width = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
            incGlobs.TextChanged += (_, _) => _viewModel.IncludeGlobs = incGlobs.Text;
            incMode.SelectionChanged += (_, _) =>
            {
                if (incMode.SelectedIndex >= 0)
                    _viewModel.IncludeFilterModeIndex = incMode.SelectedIndex;
                incGlobs.PlaceholderText = _viewModel.IncludeFilterPlaceholder;
            };
            g.Children.Add(incGlobs);

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
            g.Children.Add(excludeHeader);

            var excGlobs = new TextBox { Text = _viewModel.ExcludeGlobs, PlaceholderText = _viewModel.ExcludeFilterPlaceholder, Width = 620, HorizontalAlignment = HorizontalAlignment.Stretch };
            excGlobs.TextChanged += (_, _) => _viewModel.ExcludeGlobs = excGlobs.Text;
            excMode.SelectionChanged += (_, _) =>
            {
                if (excMode.SelectedIndex >= 0)
                    _viewModel.ExcludeFilterModeIndex = excMode.SelectedIndex;
                excGlobs.PlaceholderText = _viewModel.ExcludeFilterPlaceholder;
            };
            g.Children.Add(excGlobs);
        }

        // ── Search Limits ──
        {
            var g = AddTab("Search Limits");

            g.Children.Add(NextSearchLabel("Max results (0 = unlimited, non-zero values capped at 50,000):"));
            var max = new NumberBox { Value = _viewModel.MaxResults, Minimum = 0 };
            max.ValueChanged += (_, args) => _viewModel.MaxResults = (int)args.NewValue;
            g.Children.Add(max);
            g.Children.Add(new TextBlock { Text = "Stops the search after this many matches. Set to 0 for no limit (memory pressure will still protect against runaway usage).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Default file size filter (MB):"));
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
            g.Children.Add(sizeDefaults);
            g.Children.Add(new TextBlock { Text = "Both 0 = any size. These defaults fill the Advanced Options size filter when Yagu starts; changes in Advanced Options remain temporary.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Default created date filter:"));
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
            g.Children.Add(createdDefaults);

            g.Children.Add(NextSearchLabel("Default modified date filter:"));
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
            g.Children.Add(modifiedDefaults);
            g.Children.Add(new TextBlock { Text = "Blank = any date. These defaults fill the Advanced Options date filters when Yagu starts; changes in Advanced Options remain temporary.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
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
            g.Children.Add(clearDateDefaults);

            var searchBinary = new ToggleSwitch { OnContent = NextSearchLabel("Search binary files"), OffContent = NextSearchLabel("Search binary files"), IsOn = _viewModel.SearchBinary };
            searchBinary.Toggled += (_, _) => _viewModel.SearchBinary = searchBinary.IsOn;
            g.Children.Add(searchBinary);
            g.Children.Add(new TextBlock { Text = "When enabled, files detected as binary (null bytes, magic bytes) are included in content search. Off by default for faster searching.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipAdmin = new CheckBox { Content = NextSearchLabel("Skip admin-protected paths when not elevated"), IsChecked = _viewModel.ExcludeAdminProtectedPaths };
            skipAdmin.Checked += (_, _) => _viewModel.ExcludeAdminProtectedPaths = true;
            skipAdmin.Unchecked += (_, _) => _viewModel.ExcludeAdminProtectedPaths = false;
            g.Children.Add(skipAdmin);
            g.Children.Add(new TextBlock { Text = "When the process is not elevated, exclude directories that always require admin (System Volume Information, $Recycle.Bin, Windows\\System32\\config, Windows\\Installer, etc.). Speeds up search by skipping guaranteed access-denied trees. No effect when running as administrator.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var adminSegLabel = NextSearchLabel("Admin-protected path segments (one per line or semicolon-separated):");
            adminSegLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(adminSegLabel);
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
            g.Children.Add(adminSeg);
            var resetAdminSeg = new Button { Content = "Reset to defaults", Margin = new Thickness(0, 4, 0, 0) };
            resetAdminSeg.Click += (_, _) =>
            {
                adminSeg.Text = AppSettings.DefaultAdminProtectedPathSegments;
                _viewModel.AdminProtectedPathSegments = adminSeg.Text;
            };
            g.Children.Add(resetAdminSeg);
            g.Children.Add(new TextBlock { Text = "Each entry is a path substring like \\Windows\\System32\\config. Anchored with backslashes so it matches the folder anywhere in the tree. Only applied when the process is not elevated.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipExtLabel = NextSearchLabel("Skip extensions (semicolon-separated, no dots):");
            skipExtLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(skipExtLabel);
            var skipExt = new TextBox { Text = _viewModel.SettingsSkipExtensions, PlaceholderText = "e.g. svg;sqlite;etl;log", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            skipExt.TextChanged += (_, _) =>
            {
                _viewModel.SettingsSkipExtensions = skipExt.Text;
                _viewModel.SkipExtensions = skipExt.Text;
            };
            g.Children.Add(skipExt);
            g.Children.Add(new TextBlock { Text = "Files with these extensions are skipped entirely. Default media, document, data, and dump prefilters live here; add custom text/project extensions as needed.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var binaryExtLabel = NextSearchLabel("Binary extensions (semicolon-separated, no dots):");
            binaryExtLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(binaryExtLabel);
            var binaryExt = new TextBox { Text = _viewModel.SettingsBinaryExtensions, PlaceholderText = "e.g. exe;dll;pdb;wasm", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            binaryExt.TextChanged += (_, _) =>
            {
                _viewModel.SettingsBinaryExtensions = binaryExt.Text;
                _viewModel.BinaryExtensions = binaryExt.Text;
            };
            g.Children.Add(binaryExt);
            var resetBinaryExt = new Button { Content = "Reset binary extensions", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            resetBinaryExt.Click += (_, _) =>
            {
                binaryExt.Text = AppSettings.DefaultBinaryExtensions;
                _viewModel.SettingsBinaryExtensions = binaryExt.Text;
                _viewModel.BinaryExtensions = binaryExt.Text;
            };
            g.Children.Add(resetBinaryExt);
            g.Children.Add(new TextBlock { Text = "These populate the Binary ext dropdown shown beside Skip Extensions when Search binary is enabled. Use this for compiled binary and build artifact extensions that should remain skipped even during binary search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var archiveExtLabel = NextSearchLabel("Archive extensions (semicolon-separated, no dots):");
            archiveExtLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(archiveExtLabel);
            var archiveExt = new TextBox { Text = _viewModel.SettingsArchiveExtensions, PlaceholderText = "e.g. zip;jar;docx;xlsx;pptx;epub", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            archiveExt.TextChanged += (_, _) =>
            {
                _viewModel.SettingsArchiveExtensions = archiveExt.Text;
                _viewModel.ArchiveExtensions = archiveExt.Text;
            };
            g.Children.Add(archiveExt);
            g.Children.Add(new TextBlock { Text = "Extensions that are ZIP-like containers. When 'Search archives' is on, these are removed from the skip list so they reach the content searcher. Detection still uses file-header magic bytes.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(new TextBlock { Text = "Archive search limits", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });

            g.Children.Add(NextSearchLabel("Max archive nesting depth (0 = 5):"));
            var archiveNesting = new NumberBox { Value = _viewModel.ArchiveMaxNestingDepth, Minimum = 0, Maximum = 50 };
            archiveNesting.ValueChanged += (_, args) => _viewModel.ArchiveMaxNestingDepth = (int)args.NewValue;
            g.Children.Add(archiveNesting);
            g.Children.Add(new TextBlock { Text = "How many levels deep to search nested archives (zip inside zip). Higher values find deeply nested content but may be slower. 0 uses the default (5).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Max archive entry size (MB, 0 = 64):"));
            var archiveEntrySize = new NumberBox { Value = _viewModel.ArchiveMaxEntryMB, Minimum = 0, Maximum = 4096 };
            archiveEntrySize.ValueChanged += (_, args) => _viewModel.ArchiveMaxEntryMB = (int)args.NewValue;
            g.Children.Add(archiveEntrySize);
            g.Children.Add(new TextBlock { Text = "Individual files inside archives larger than this are skipped. Higher values search larger archive entries but use more memory. 0 uses the default (64 MB).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Performance ──
        {
            var g = AddTab("Performance");

            g.Children.Add(NextSearchLabel("Content-search parallelism (concurrent file scan threads):"));
            var parallelism = new ComboBox();
            parallelism.Items.Add($"Auto (safe cap · up to {Math.Min(16, Environment.ProcessorCount)})");
            parallelism.Items.Add("1 thread (sequential, HDD safe)");
            parallelism.Items.Add($"Half cores ({Math.Max(1, Environment.ProcessorCount / 2)})");
            parallelism.Items.Add($"2× cores ({Environment.ProcessorCount * 2}, I/O heavy)");
            parallelism.Items.Add($"All cores ({Math.Max(1, Environment.ProcessorCount)})");
            parallelism.SelectedIndex = _viewModel.ParallelismIndex;
            parallelism.SelectionChanged += (_, _) => _viewModel.ParallelismIndex = parallelism.SelectedIndex;
            g.Children.Add(parallelism);

            var hddToggle = new ToggleSwitch
            {
                IsOn = _viewModel.LimitParallelismOnHdd,
                OnContent = "Limit parallelism on HDD (warn and set to 1 thread)",
                OffContent = "Limit parallelism on HDD (warn and set to 1 thread)",
                Margin = new Thickness(0, 4, 0, 0),
            };
            hddToggle.Toggled += (_, _) => _viewModel.LimitParallelismOnHdd = hddToggle.IsOn;
            g.Children.Add(hddToggle);
            g.Children.Add(new TextBlock { Text = "When enabled, if the search target is on a rotational hard disk, Yagu will automatically set parallelism to 1 and show a warning before searching. Disable to suppress the warning and allow any parallelism level on HDDs.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(new TextBlock { Text = "File-listing backend (how files are discovered before searching):" });
            var backend = new ComboBox();
            backend.Items.Add("Auto (SDK → es.exe → .NET)");
            backend.Items.Add("Everything SDK only (in-process, fastest)");
            backend.Items.Add("es.exe only (process spawn)");
            backend.Items.Add(".NET enumeration only (no Everything dependency)");
            backend.SelectedIndex = _viewModel.FileListerBackendIndex;
            backend.SelectionChanged += (_, _) => _viewModel.FileListerBackendIndex = backend.SelectedIndex;
            g.Children.Add(backend);
            g.Children.Add(new TextBlock { Text = "Auto tries the Everything SDK first, then es.exe, then .NET recursive enumeration. Requires voidtools Everything to be running for SDK/es.exe.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(new TextBlock { Text = "Memory saving mode", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });
            g.Children.Add(new TextBlock { Text = "These two settings control when Yagu enters memory-saving mode. Use one or the other — if the hard cap is set (> 0), it takes precedence over the pressure percentage. Changes apply to the next search.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            AddResultTempDriveSetting(g);

            g.Children.Add(NextSearchLabel("System memory pressure limit (%, 0 = disabled):"));
            var memPressure = new NumberBox { Value = _viewModel.MemoryPressurePercent, Minimum = 0, Maximum = 100 };
            memPressure.ValueChanged += (_, args) => _viewModel.MemoryPressurePercent = (int)args.NewValue;
            g.Children.Add(memPressure);
            g.Children.Add(new TextBlock { Text = "Yagu enters memory-saving mode when total machine RAM usage exceeds this %. Recommended for most users.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Process memory hard cap (MB, 0 = use pressure % above):"));
            var memLimit = new NumberBox { Value = _viewModel.MemoryLimitMB, Minimum = 0, Maximum = 65536 };
            memLimit.ValueChanged += (_, args) => _viewModel.MemoryLimitMB = (int)args.NewValue;
            g.Children.Add(memLimit);
            g.Children.Add(new TextBlock { Text = "When set above 0, memory-saving mode activates when the Yagu process exceeds this working-set size regardless of system memory pressure. Leave at 0 to use the pressure % instead.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("SDK channel buffer size:"));
            var sdkBuf = new NumberBox { Value = _viewModel.SdkChannelBufferSize, Minimum = 16, Maximum = 1000000 };
            sdkBuf.ValueChanged += (_, args) => _viewModel.SdkChannelBufferSize = (int)args.NewValue;
            g.Children.Add(sdkBuf);
            g.Children.Add(new TextBlock { Text = "Number of file paths buffered between the Everything SDK producer thread and the consumer. Higher values may improve throughput on large directories but use more memory. Only applies when using the Everything SDK backend.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Max matches per file (0 = unlimited):"));
            var maxPerFile = new NumberBox { Value = _viewModel.MaxMatchesPerFile, Minimum = 0, Maximum = int.MaxValue };
            maxPerFile.ValueChanged += (_, args) => _viewModel.MaxMatchesPerFile = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
            g.Children.Add(maxPerFile);
            g.Children.Add(new TextBlock { Text = "Optional cap on stored matches per file. Useful for taming pathological files (massive logs, generated dumps) that would otherwise dominate memory. Leave at 0 for unlimited matches. Applies to subsequent searches.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Content-search file size ceiling (MB, 0 = no ceiling):"));
            var fileSizeCeiling = new NumberBox { Value = _viewModel.ContentSearchFileSizeMB, Minimum = 0, Maximum = 10240 };
            fileSizeCeiling.ValueChanged += (_, args) => _viewModel.ContentSearchFileSizeMB = (int)args.NewValue;
            g.Children.Add(fileSizeCeiling);
            g.Children.Add(new TextBlock { Text = "Files larger than this are skipped during content search when no explicit max-size filter is set. Prevents the scanner from blocking for minutes on huge files. Set to 0 to disable (no ceiling).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Max results ceiling (hard cap on max results):"));
            var resultsCeiling = new NumberBox { Value = _viewModel.MaxResultsCeiling, Minimum = 1000, Maximum = 10_000_000 };
            resultsCeiling.ValueChanged += (_, args) => _viewModel.MaxResultsCeiling = (int)args.NewValue;
            g.Children.Add(resultsCeiling);
            g.Children.Add(new TextBlock { Text = "Absolute ceiling for Max Results regardless of per-search setting. Values above this are clamped down. Must be at least 1,000.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("MMF concurrency limit (0 = default 16):"));
            var mmfLimit = new NumberBox { Value = _viewModel.MmfConcurrencyLimit, Minimum = 0, Maximum = 256 };
            mmfLimit.ValueChanged += (_, args) => _viewModel.MmfConcurrencyLimit = (int)args.NewValue;
            g.Children.Add(mmfLimit);
            g.Children.Add(new TextBlock { Text = "Maximum concurrent memory-mapped file views. Higher values use more virtual address space but may improve throughput on NVMe. Set to 0 for the default (16). Applies to the next search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Native scanner concurrency limit (0 = default):"));
            var nativeLimit = new NumberBox { Value = _viewModel.NativeConcurrencyLimit, Minimum = 0, Maximum = 256 };
            nativeLimit.ValueChanged += (_, args) => _viewModel.NativeConcurrencyLimit = (int)args.NewValue;
            g.Children.Add(nativeLimit);
            g.Children.Add(new TextBlock { Text = "Maximum concurrent Rust native scans. Higher values improve throughput on fast NVMe storage. Set to 0 for the default (min(64, CPU cores × 2)). Applies to the next search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Display ──
        {
            var g = AddTab("Display");
            var fileMatchListGroup = AddSettingsGroupBox(g, "File Match List");
            var previewViewerGroup = AddSettingsGroupBox(g, "Preview Viewer");
            var editorAppearanceGroup = AddSettingsGroupBox(g, "Editor");

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
            wordWrap.Checked += (_, _) => { _viewModel.PreviewWordWrap = true; _applyWordWrap?.Invoke(true); };
            wordWrap.Unchecked += (_, _) => { _viewModel.PreviewWordWrap = false; _applyWordWrap?.Invoke(false); };
            previewViewerGroup.Children.Add(wordWrap);

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

            previewViewerGroup.Children.Add(new TextBlock { Text = "Auto-load matches on scroll (matches to load when reaching end of truncated section, 0 = disabled):" });
            var autoLoad = new NumberBox { Value = _viewModel.PreviewAutoLoadMatches, Minimum = 0, Maximum = 5000, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
            autoLoad.ValueChanged += (_, args) => _viewModel.PreviewAutoLoadMatches = (int)args.NewValue;
            previewViewerGroup.Children.Add(autoLoad);

            previewViewerGroup.Children.Add(new TextBlock { Text = "Preview section limits", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });

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
        }

        // ── Editor ──
        {
            var g = AddTab("Editor");

            g.Children.Add(new TextBlock { Text = "Editor command ({file} = full file path, {line} = line number):" });
            var editor = new TextBox { Text = _viewModel.EditorCommand, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            editor.TextChanged += (_, _) => _viewModel.EditorCommand = editor.Text;
            g.Children.Add(editor);

            var backup = new CheckBox { Content = "Backup file before saving", IsChecked = _viewModel.BackupBeforeSave };
            backup.Checked += (_, _) => _viewModel.BackupBeforeSave = true;
            backup.Unchecked += (_, _) => _viewModel.BackupBeforeSave = false;
            g.Children.Add(backup);
            g.Children.Add(new TextBlock { Text = "When enabled, the original file is copied to <filename>.yagubak before saving changes from the built-in editor. If a .yagubak already exists, uses .yagubak-2, .yagubak-3, etc.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(new TextBlock { Text = "Built-in editor limits", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });
            g.Children.Add(new TextBlock { Text = "Files exceeding any of these limits will not open in the built-in editor. Use the external editor or Show in Explorer for very large files.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            g.Children.Add(new TextBlock { Text = "Max file size (MB):" });
            var edMaxSize = new NumberBox { Value = _viewModel.PreviewEditorMaxSizeMB, Minimum = 1, Maximum = 1024 };
            edMaxSize.ValueChanged += (_, args) => _viewModel.PreviewEditorMaxSizeMB = (int)args.NewValue;
            g.Children.Add(edMaxSize);

            g.Children.Add(new TextBlock { Text = "Max total characters:" });
            var edMaxText = new NumberBox { Value = _viewModel.PreviewEditorMaxTextLength, Minimum = 100_000, Maximum = 200_000_000 };
            edMaxText.ValueChanged += (_, args) => _viewModel.PreviewEditorMaxTextLength = (int)args.NewValue;
            g.Children.Add(edMaxText);

            g.Children.Add(new TextBlock { Text = "Max single-line length (characters):" });
            var edMaxLine = new NumberBox { Value = _viewModel.PreviewEditorMaxLineLength, Minimum = 10_000, Maximum = 100_000_000 };
            edMaxLine.ValueChanged += (_, args) => _viewModel.PreviewEditorMaxLineLength = (int)args.NewValue;
            g.Children.Add(edMaxLine);
        }

        // ── Window ──
        {
            var g = AddTab("Window");

            g.Children.Add(new TextBlock { Text = "Startup", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var startInLauncher = new CheckBox { Content = "Start in compact launcher mode", IsChecked = _viewModel.StartInLauncherMode };
            startInLauncher.Checked += (_, _) => _viewModel.StartInLauncherMode = true;
            startInLauncher.Unchecked += (_, _) => _viewModel.StartInLauncherMode = false;
            g.Children.Add(startInLauncher);
            g.Children.Add(new TextBlock { Text = "When enabled (default), Yagu launches as a small search bar (like Spotlight/Alfred). When disabled, Yagu launches as a traditional window with title bar and results pane.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

            g.Children.Add(new TextBlock { Text = "Launcher focus-loss behavior:" });
            var focusBehavior = new ComboBox();
            focusBehavior.Items.Add("Minimize to system tray");
            focusBehavior.Items.Add("Stay open (default)");
            focusBehavior.Items.Add("Always on top (stay above all windows)");
            // Clamp legacy/out-of-range values onto the new 0..2 range (3 = old Traditional window).
            focusBehavior.SelectedIndex = _viewModel.WindowFocusBehavior is >= 0 and <= 2
                ? _viewModel.WindowFocusBehavior
                : 1;
            focusBehavior.SelectionChanged += (_, _) => _viewModel.WindowFocusBehavior = focusBehavior.SelectedIndex;
            g.Children.Add(focusBehavior);
            g.Children.Add(new TextBlock { Text = "Controls what happens when the compact launcher window loses focus. You can override per-session using the pin button next to Browse.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var closeToTray = new CheckBox { Content = "Dock to system tray when closed (instead of exiting)", IsChecked = _viewModel.CloseToTray };
            closeToTray.Checked += (_, _) => _viewModel.CloseToTray = true;
            closeToTray.Unchecked += (_, _) => _viewModel.CloseToTray = false;
            g.Children.Add(closeToTray);
            g.Children.Add(new TextBlock { Text = "When enabled, closing the window hides Yagu to the system tray. Right-click the tray icon to reopen or exit.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var maximizeOnStartup = new CheckBox { Content = "Maximize window on startup", IsChecked = _viewModel.MaximizeOnStartup };
            maximizeOnStartup.Checked += (_, _) => _viewModel.MaximizeOnStartup = true;
            maximizeOnStartup.Unchecked += (_, _) => _viewModel.MaximizeOnStartup = false;
            g.Children.Add(maximizeOnStartup);
            g.Children.Add(new TextBlock { Text = "When enabled, the main window starts maximized instead of its default size.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── UI Behaviors ──
        {
            var g = AddTab("UI Behaviors");

            g.Children.Add(new TextBlock { Text = "Appearance", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

            g.Children.Add(new TextBlock { Text = "Theme:" });
            var themeMode = new ComboBox { SelectedIndex = AppThemeService.NormalizeThemeModeIndex(_viewModel.ThemeModeIndex), MinWidth = 220 };
            themeMode.Items.Add("Auto (system theme)");
            themeMode.Items.Add("Dark mode");
            themeMode.Items.Add("Light mode");
            themeMode.SelectionChanged += (_, _) =>
            {
                if (themeMode.SelectedIndex >= 0)
                    _viewModel.ThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(themeMode.SelectedIndex);
            };
            g.Children.Add(themeMode);
            g.Children.Add(new TextBlock { Text = "Auto follows the current Windows app theme. Dark and Light keep Yagu on that theme until changed.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

            g.Children.Add(new TextBlock { Text = "Selection → Preview Behaviors", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

            var fileHeaderCheck = new CheckBox { Content = "Checking a file header adds it to the preview pane", IsChecked = _viewModel.FileHeaderCheckAddsToPreview };
            fileHeaderCheck.Checked += (_, _) => _viewModel.FileHeaderCheckAddsToPreview = true;
            fileHeaderCheck.Unchecked += (_, _) => _viewModel.FileHeaderCheckAddsToPreview = false;
            g.Children.Add(fileHeaderCheck);
            g.Children.Add(new TextBlock { Text = "When enabled, selecting the checkbox on a file header in the results list immediately shows that file's matches in the preview pane.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var matchLineCheck = new CheckBox { Content = "Checking a match line adds it to the preview pane", IsChecked = _viewModel.MatchLineCheckAddsToPreview };
            matchLineCheck.Checked += (_, _) => _viewModel.MatchLineCheckAddsToPreview = true;
            matchLineCheck.Unchecked += (_, _) => _viewModel.MatchLineCheckAddsToPreview = false;
            g.Children.Add(matchLineCheck);
            g.Children.Add(new TextBlock { Text = "When enabled, selecting the checkbox on an individual match line immediately shows that match in the preview pane.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        // ── Terminal Emulator ──
        {
            var g = AddTab("Terminal Emulator");

            AddTerminalEmulationSetting(g);
        }

        // ── Developer Options ──
        {
            var g = AddTab("Developer Options");

            var showMemoryPressureLabel = new CheckBox
            {
                Content = "Show memory pressure warning label",
                IsChecked = _viewModel.ShowMemoryPressureWarningLabel,
            };
            showMemoryPressureLabel.Checked += (_, _) => _viewModel.ShowMemoryPressureWarningLabel = true;
            showMemoryPressureLabel.Unchecked += (_, _) => _viewModel.ShowMemoryPressureWarningLabel = false;
            g.Children.Add(showMemoryPressureLabel);
            g.Children.Add(new TextBlock
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
            g.Children.Add(showStatsForNerds);
            g.Children.Add(new TextBlock
            {
                Text = "Shows the files/second and MB/s text, plus the disk throughput sparkline, MB/s, and utilization percentage in the bottom status bar.",
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
            g.Children.Add(showAutoScrollResultsCheckbox);
            g.Children.Add(new TextBlock
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
                MarkSettingsDirty();
                resetFontContrastReminder.Content = "Font contrast reminders reset";
                resetFontContrastReminder.IsEnabled = false;
            };
            g.Children.Add(resetFontContrastReminder);
            g.Children.Add(new TextBlock
            {
                Text = "Allows theme/font contrast warnings to appear again after Remind me later or Don't remind me again.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            g.Children.Add(new TextBlock { Text = "File log level:", Margin = new Thickness(0, 12, 0, 0) });
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
            g.Children.Add(fileLogRow);

            g.Children.Add(new TextBlock { Text = "Console log level:" });
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
            g.Children.Add(consoleLogRow);

            g.Children.Add(new TextBlock { Text = $"Log file: {LogService.DefaultLogPath()}", FontSize = 11, Opacity = 0.6 });

            // Reset admin warning
            if (_viewModel.SuppressAdminWarning)
            {
                var resetAdmin = new Button { Content = "Re-enable admin privilege warning", FontSize = 12, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0) };
                resetAdmin.Click += (_, _) =>
                {
                    _viewModel.SuppressAdminWarning = false;
                    MarkSettingsDirty();
                    resetAdmin.Content = "Admin warning re-enabled ✓";
                    resetAdmin.IsEnabled = false;
                };
                g.Children.Add(resetAdmin);
                g.Children.Add(new TextBlock { Text = "The non-administrator warning was previously dismissed. Click to show it again on next launch.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            }
        }

        // ── General ──
        {
            var g = AddTab("General");

            var availableHotkeyKeys = _hotkeyService.GetAvailableCtrlShiftLetterKeys(_mainHwnd);
            var selectedHotkeyKey = HotkeyService.ChooseAvailableKey(availableHotkeyKeys, _viewModel.GlobalHotkeyKey);
            if (selectedHotkeyKey is char selectedKey && !string.Equals(_viewModel.GlobalHotkeyKey, selectedKey.ToString(), StringComparison.OrdinalIgnoreCase))
                _viewModel.GlobalHotkeyKey = selectedKey.ToString();

            var hotkey = new CheckBox { Content = "Enable global hotkey", IsChecked = _viewModel.GlobalHotkeyEnabled, IsEnabled = availableHotkeyKeys.Count > 0 };
            hotkey.Checked += (_, _) => _viewModel.GlobalHotkeyEnabled = true;
            hotkey.Unchecked += (_, _) => _viewModel.GlobalHotkeyEnabled = false;
            g.Children.Add(hotkey);

            g.Children.Add(new TextBlock { Text = "Global hotkey:" });
            var hotkeyCombo = new ComboBox { IsEnabled = availableHotkeyKeys.Count > 0 };
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

            hotkeyCombo.SelectionChanged += (_, _) =>
            {
                if (hotkeyCombo.SelectedItem is ComboBoxItem item && item.Tag is string key)
                    _viewModel.GlobalHotkeyKey = key;
            };
            g.Children.Add(hotkeyCombo);

            g.Children.Add(new TextBlock { Text = "Max recent directories / queries to remember:" });
            var recent = new NumberBox { Value = _viewModel.MaxRecentItems, Minimum = 1, Maximum = 100 };
            recent.ValueChanged += (_, args) => _viewModel.MaxRecentItems = (int)args.NewValue;
            g.Children.Add(recent);
        }
    }
}
