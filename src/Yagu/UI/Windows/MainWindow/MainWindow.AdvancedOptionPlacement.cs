using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Yagu;

/// <summary>Guarded, persisted cross-tab placement for Advanced Options rows.</summary>
public sealed partial class MainWindow
{
    private const int AdvancedOptionTabHoverArmMilliseconds = 650;
    private const string AdvancedOptionDragDataFormat = "Yagu.AdvancedOption";

    private sealed class AdvancedOptionRegistration
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string HomeTabKey { get; init; }
        public required FrameworkElement Content { get; init; }
        public required Panel HomeParent { get; init; }
        public required int HomeIndex { get; init; }
        public required int HomeRow { get; init; }
        public required int HomeColumn { get; init; }
        public required int HomeRowSpan { get; init; }
        public required int HomeColumnSpan { get; init; }
        public required Grid Wrapper { get; init; }
    }

    private readonly List<AdvancedOptionRegistration> _advancedOptionRegistrations = [];
    private readonly Dictionary<string, AdvancedOptionRegistration> _advancedOptionsById = new(StringComparer.Ordinal);
    private string? _draggedAdvancedOptionId;
    private string? _advancedOptionHoverTabKey;
    private string? _armedAdvancedOptionTargetTabKey;
    private DispatcherTimer? _advancedOptionHoverTimer;

    private void InitializeAdvancedOptionPlacement()
    {
        RegisterAdvancedOption("search.mode", "Search mode", "search", AdvancedOptionSearchModeRow);
        RegisterAdvancedOption("search.multiline", "Multiline options", "search", AdvancedOptionMultilineRow);
        RegisterAdvancedOption("search.pathFilters", "Path filters", "search", AdvancedOptionPathFiltersRow);
        RegisterAdvancedOption("filters.fileTypes", "File type filter", "filters", AdvancedOptionFileTypeFilterRow);
        RegisterAdvancedOption("filters.binary", "Search binary", "filters", BinaryExtRow);
        RegisterAdvancedOption("filters.archives", "Search archives", "filters", ArchiveExtRow);
        RegisterAdvancedOption("filters.cloudFiles", "Search online-only cloud files", "filters", CloudFilesRow);
        RegisterAdvancedOption("filters.hiddenFiles", "Search hidden files", "filters", HiddenFilesRow);
        RegisterAdvancedOption("filters.imageText", "Search image text", "filters", ImageTextRow);
        RegisterAdvancedOption("filters.pdfText", "Search PDF text", "filters", PdfTextRow);
        RegisterAdvancedOption("filters.contentIndex", "Use content index", "filters", UseContentIndexRow);
        RegisterAdvancedOption("size.minimum", "Minimum file size", "size", AdvancedOptionMinSizeRow);
        RegisterAdvancedOption("size.maximum", "Maximum file size", "size", AdvancedOptionMaxSizeRow);
        RegisterAdvancedOption("dates.created", "Created date", "dates", AdvancedOptionCreatedDateRow);
        RegisterAdvancedOption("dates.modified", "Modified date", "dates", AdvancedOptionModifiedDateRow);
        RegisterAdvancedOption("advanced.maxDepth", "Maximum search depth", "advanced", AdvancedOptionMaxDepthRow);

        RebuildAdvancedOptionPlacement();
    }

    private void RegisterAdvancedOption(string id, string label, string homeTabKey, FrameworkElement content)
    {
        if (VisualTreeHelper.GetParent(content) is not Panel parent)
            return;

        int homeIndex = parent.Children.IndexOf(content);
        int row = Grid.GetRow(content);
        int column = Grid.GetColumn(content);
        int rowSpan = Grid.GetRowSpan(content);
        int columnSpan = Grid.GetColumnSpan(content);

        parent.Children.RemoveAt(homeIndex);
        Grid.SetRow(content, 0);
        Grid.SetColumn(content, 0);
        Grid.SetRowSpan(content, 1);
        Grid.SetColumnSpan(content, 1);

        var wrapper = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = id,
        };
        wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrapper.Children.Add(content);

        var grip = new Button
        {
            Width = 18,
            MinWidth = 18,
            MinHeight = 0,
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            CanDrag = true,
            Tag = id,
            Content = new FontIcon
            {
                Glyph = "\uE76F",
                FontSize = 11,
                Opacity = 0.45,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationProperties.SetName(grip, $"Move {label}");
        ToolTipService.SetToolTip(grip, $"Drag to move {label} to another Advanced Options tab");
        grip.PointerEntered += (_, _) => ((FontIcon)grip.Content).Opacity = 0.9;
        grip.PointerExited += (_, _) => ((FontIcon)grip.Content).Opacity = 0.45;
        grip.DragStarting += OnAdvancedOptionDragStarting;
        grip.DropCompleted += OnAdvancedOptionDropCompleted;
        Grid.SetColumn(grip, 1);
        wrapper.Children.Add(grip);

        Grid.SetRow(wrapper, row);
        Grid.SetColumn(wrapper, column);
        Grid.SetRowSpan(wrapper, rowSpan);
        Grid.SetColumnSpan(wrapper, columnSpan);
        parent.Children.Insert(homeIndex, wrapper);

        var registration = new AdvancedOptionRegistration
        {
            Id = id,
            Label = label,
            HomeTabKey = homeTabKey,
            Content = content,
            HomeParent = parent,
            HomeIndex = homeIndex,
            HomeRow = row,
            HomeColumn = column,
            HomeRowSpan = rowSpan,
            HomeColumnSpan = columnSpan,
            Wrapper = wrapper,
        };
        _advancedOptionRegistrations.Add(registration);
        _advancedOptionsById[id] = registration;
    }

    private void RebuildAdvancedOptionPlacement()
    {
        foreach (AdvancedOptionRegistration registration in _advancedOptionRegistrations)
        {
            if (VisualTreeHelper.GetParent(registration.Wrapper) is Panel parent)
                parent.Children.Remove(registration.Wrapper);
        }

        foreach (AdvancedOptionRegistration registration in _advancedOptionRegistrations)
        {
            string targetTabKey = EffectiveAdvancedOptionTab(registration);
            if (string.Equals(targetTabKey, registration.HomeTabKey, StringComparison.Ordinal))
            {
                RestoreAdvancedOptionHome(registration);
                continue;
            }

            if (ResolveAdvancedOptionsTabContent(targetTabKey) is not StackPanel targetHost)
            {
                RestoreAdvancedOptionHome(registration);
                continue;
            }

            ClearAdvancedOptionGridPlacement(registration.Wrapper);
            targetHost.Children.Insert(AdvancedOptionTargetInsertIndex(targetTabKey, targetHost), registration.Wrapper);
        }
    }

    private string EffectiveAdvancedOptionTab(AdvancedOptionRegistration registration)
        => ViewModel.AdvancedOptionPlacements.TryGetValue(registration.Id, out string? target)
            && ShippedAdvancedOptionsTabOrder.Contains(target, StringComparer.Ordinal)
                ? target
                : registration.HomeTabKey;

    private static int AdvancedOptionTargetInsertIndex(string tabKey, StackPanel host)
        => tabKey is "size" or "dates" or "advanced"
            ? Math.Max(1, host.Children.Count - 1)
            : host.Children.Count;

    private static void ClearAdvancedOptionGridPlacement(FrameworkElement wrapper)
    {
        Grid.SetRow(wrapper, 0);
        Grid.SetColumn(wrapper, 0);
        Grid.SetRowSpan(wrapper, 1);
        Grid.SetColumnSpan(wrapper, 1);
    }

    private static void RestoreAdvancedOptionHome(AdvancedOptionRegistration registration)
    {
        Grid wrapper = registration.Wrapper;
        Grid.SetRow(wrapper, registration.HomeRow);
        Grid.SetColumn(wrapper, registration.HomeColumn);
        Grid.SetRowSpan(wrapper, registration.HomeRowSpan);
        Grid.SetColumnSpan(wrapper, registration.HomeColumnSpan);
        int index = Math.Clamp(registration.HomeIndex, 0, registration.HomeParent.Children.Count);
        registration.HomeParent.Children.Insert(index, wrapper);
    }

    private void OnAdvancedOptionDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string id }
            || !_advancedOptionsById.ContainsKey(id))
        {
            args.Cancel = true;
            return;
        }

        ResetAdvancedOptionDragState();
        _draggedAdvancedOptionId = id;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetData(AdvancedOptionDragDataFormat, id);
        args.Data.SetText(_advancedOptionsById[id].Label);
    }

    private void OnAdvancedOptionDropCompleted(UIElement sender, DropCompletedEventArgs args)
        => ResetAdvancedOptionDragState();

    private void OnAdvancedOptionTabDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(AdvancedOptionDragDataFormat)
            || _draggedAdvancedOptionId is not { } id
            || !_advancedOptionsById.TryGetValue(id, out var registration))
            return; // Let ListView's built-in tab reorder handle its own drag.

        e.Handled = true;
        string? targetTabKey = AdvancedOptionsTabAt(e.GetPosition(AdvancedOptionsTabList));
        if (targetTabKey is null
            || string.Equals(targetTabKey, EffectiveAdvancedOptionTab(registration), StringComparison.Ordinal))
        {
            CancelAdvancedOptionHoverArm();
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (!string.Equals(_advancedOptionHoverTabKey, targetTabKey, StringComparison.Ordinal))
            BeginAdvancedOptionHoverArm(targetTabKey);

        e.AcceptedOperation = string.Equals(_armedAdvancedOptionTargetTabKey, targetTabKey, StringComparison.Ordinal)
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
    }

    private void OnAdvancedOptionTabDragLeave(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(AdvancedOptionDragDataFormat) || _draggedAdvancedOptionId is null)
            return;
        e.Handled = true;
        CancelAdvancedOptionHoverArm();
    }

    private async void OnAdvancedOptionTabDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(AdvancedOptionDragDataFormat)
            || _draggedAdvancedOptionId is not { } id
            || !_advancedOptionsById.TryGetValue(id, out var registration))
            return;

        e.Handled = true;
        string? targetTabKey = AdvancedOptionsTabAt(e.GetPosition(AdvancedOptionsTabList));
        bool armed = targetTabKey is not null
            && string.Equals(_armedAdvancedOptionTargetTabKey, targetTabKey, StringComparison.Ordinal);
        string sourceTabKey = EffectiveAdvancedOptionTab(registration);
        ResetAdvancedOptionDragState();
        if (!armed || targetTabKey is null || string.Equals(sourceTabKey, targetTabKey, StringComparison.Ordinal))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        string targetLabel = AdvancedOptionsTabLabel(targetTabKey);
        string sourceLabel = AdvancedOptionsTabLabel(sourceTabKey);
        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Move Advanced Option",
                TitleGlyph = "\uE8AB",
                Content = $"Move “{registration.Label}” from {sourceLabel} to {targetLabel}? The control and its label move together. You can drag it back later.",
                PrimaryButtonText = "Move option",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 600,
                Height = 280,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
        if (result != YaguDialogResult.Primary)
            return;

        if (string.Equals(targetTabKey, registration.HomeTabKey, StringComparison.Ordinal))
            ViewModel.AdvancedOptionPlacements.Remove(registration.Id);
        else
            ViewModel.AdvancedOptionPlacements[registration.Id] = targetTabKey;

        RebuildAdvancedOptionPlacement();
        SelectAdvancedOptionsTab(targetTabKey);
        await ViewModel.PersistSettingsAsync();
        ViewModel.StatusText = $"Moved {registration.Label} to {targetLabel}.";
    }

    private string? AdvancedOptionsTabAt(Point point)
    {
        for (int i = 0; i < AdvancedOptionsTabs.Count; i++)
        {
            if (AdvancedOptionsTabList.ContainerFromIndex(i) is not ListViewItem container
                || container.ActualHeight <= 0)
            {
                continue;
            }

            Point topLeft = container.TransformToVisual(AdvancedOptionsTabList).TransformPoint(new Point(0, 0));
            if (point.Y >= topLeft.Y && point.Y <= topLeft.Y + container.ActualHeight)
                return AdvancedOptionsTabs[i].Key;
        }
        return null;
    }

    private void BeginAdvancedOptionHoverArm(string targetTabKey)
    {
        CancelAdvancedOptionHoverArm();
        _advancedOptionHoverTabKey = targetTabKey;
        _advancedOptionHoverTimer ??= CreateAdvancedOptionHoverTimer();
        _advancedOptionHoverTimer.Start();
    }

    private DispatcherTimer CreateAdvancedOptionHoverTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AdvancedOptionTabHoverArmMilliseconds),
        };
        timer.Tick += OnAdvancedOptionHoverArmTick;
        return timer;
    }

    private void OnAdvancedOptionHoverArmTick(object? sender, object args)
    {
        _advancedOptionHoverTimer?.Stop();
        if (_draggedAdvancedOptionId is null || _advancedOptionHoverTabKey is not { } targetTabKey)
            return;

        _armedAdvancedOptionTargetTabKey = targetTabKey;
        SelectAdvancedOptionsTab(targetTabKey);
    }

    private void CancelAdvancedOptionHoverArm()
    {
        _advancedOptionHoverTimer?.Stop();
        _advancedOptionHoverTabKey = null;
        _armedAdvancedOptionTargetTabKey = null;
    }

    private void ResetAdvancedOptionDragState()
    {
        CancelAdvancedOptionHoverArm();
        _draggedAdvancedOptionId = null;
    }

    private string AdvancedOptionsTabLabel(string tabKey)
        => AdvancedOptionsTabs.FirstOrDefault(tab => string.Equals(tab.Key, tabKey, StringComparison.Ordinal))?.Label
            ?? tabKey;

    private void DisposeAdvancedOptionPlacement()
    {
        if (_advancedOptionHoverTimer is not null)
        {
            _advancedOptionHoverTimer.Stop();
            _advancedOptionHoverTimer.Tick -= OnAdvancedOptionHoverArmTick;
            _advancedOptionHoverTimer = null;
        }
        _draggedAdvancedOptionId = null;
        _advancedOptionHoverTabKey = null;
        _armedAdvancedOptionTargetTabKey = null;
    }
}