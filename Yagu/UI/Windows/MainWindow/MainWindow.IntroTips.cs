using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.ViewModels;

namespace Yagu;

/// <summary>
/// First-time introductory callouts for the results and preview panes.
/// </summary>
public sealed partial class MainWindow
{
    private enum IntroTipKind
    {
        FileDrawer,
        FileDrawerLineNumber,
        PreviewMatch,
    }

    private static readonly TimeSpan FileDrawerIntroTipDelay = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _fileDrawerIntroTipDelayTimer;
    private FrameworkElement? _fileDrawerIntroTipDelayTarget;

    private void OnFileGroupHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target)
        {
            QueueDelayedFileDrawerIntroTip(target);
            ApplyDrawerLabelSettings(target);
            if (target is Grid headerGrid && _realizedFileGroupHeaders.Add(headerGrid))
                headerGrid.Unloaded += OnFileGroupHeaderUnloaded;
        }
    }

    private void OnFileGroupHeaderUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid headerGrid)
        {
            _realizedFileGroupHeaders.Remove(headerGrid);
            headerGrid.Unloaded -= OnFileGroupHeaderUnloaded;
        }
    }

    private void QueueDelayedFileDrawerIntroTip(FrameworkElement target)
    {
        if (!ShouldShowIntroTip(IntroTipKind.FileDrawer)
            || _fileDrawerIntroTipDelayTimer is not null)
        {
            return;
        }

        _fileDrawerIntroTipDelayTarget = target;

        var timer = new DispatcherTimer { Interval = FileDrawerIntroTipDelay };
        timer.Tick += OnFileDrawerIntroTipDelayTick;
        _fileDrawerIntroTipDelayTimer = timer;
        timer.Start();
    }

    private void OnFileDrawerIntroTipDelayTick(object? sender, object e)
    {
        var timer = _fileDrawerIntroTipDelayTimer;
        if (timer is not null)
        {
            timer.Stop();
            timer.Tick -= OnFileDrawerIntroTipDelayTick;
            _fileDrawerIntroTipDelayTimer = null;
        }

        var target = _fileDrawerIntroTipDelayTarget;
        _fileDrawerIntroTipDelayTarget = null;
        if (target is null)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TryOpenIntroTip(
                IntroTipKind.FileDrawer,
                target,
                "Double click or right click to preview this file",
                TeachingTipPlacementMode.Right);
        });
    }

    private void OnMatchLineNumberLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target)
        {
            QueueIntroTip(
                IntroTipKind.FileDrawerLineNumber,
                target,
                "Select a line number to preview just that line number + context",
                TeachingTipPlacementMode.Right);
        }
    }

    private void TryShowPreviewMatchIntroTip()
    {
        if (ActiveMatchOverlay.Visibility != Visibility.Visible)
            return;

        QueueIntroTip(
            IntroTipKind.PreviewMatch,
            ActiveMatchWordMarker,
            "Double click on any match to jump to it in a file editor",
            TeachingTipPlacementMode.Top);
    }

    /// <summary>
    /// Hides the active introductory teaching tip once the user performs the
    /// action it describes (e.g. double-clicking a preview match to jump to the
    /// editor). No-op when no tip is currently open.
    /// </summary>
    private void DismissActiveIntroTip()
    {
        if (IntroTeachingTip.IsOpen)
            IntroTeachingTip.IsOpen = false;
    }

    private void QueueIntroTip(IntroTipKind kind, FrameworkElement target, string title, TeachingTipPlacementMode placement)
    {
        if (!ShouldShowIntroTip(kind))
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TryOpenIntroTip(kind, target, title, placement);
        });
    }

    private void TryOpenIntroTip(IntroTipKind kind, FrameworkElement target, string title, TeachingTipPlacementMode placement)
    {
        if (!ShouldShowIntroTip(kind)
            || IntroTeachingTip.IsOpen
            || target.XamlRoot is null
            || target.ActualWidth <= 0
            || target.ActualHeight <= 0)
        {
            return;
        }

        IntroTeachingTip.Target = target;
        IntroTeachingTip.Title = title;
        IntroTeachingTip.Subtitle = string.Empty;
        IntroTeachingTip.PreferredPlacement = placement;
        IntroTeachingTip.IsOpen = true;

        _ = MarkIntroTipShownAsync(kind);
    }

    private bool ShouldShowIntroTip(IntroTipKind kind)
        => kind switch
        {
            IntroTipKind.FileDrawer => !ViewModel.HasShownFileDrawerIntroTip,
            IntroTipKind.FileDrawerLineNumber => !ViewModel.HasShownFileDrawerLineNumberIntroTip,
            IntroTipKind.PreviewMatch => !ViewModel.HasShownPreviewMatchIntroTip,
            _ => false,
        };

    private Task MarkIntroTipShownAsync(IntroTipKind kind)
        => kind switch
        {
            IntroTipKind.FileDrawer => ViewModel.MarkFileDrawerIntroTipShownAsync(),
            IntroTipKind.FileDrawerLineNumber => ViewModel.MarkFileDrawerLineNumberIntroTipShownAsync(),
            IntroTipKind.PreviewMatch => ViewModel.MarkPreviewMatchIntroTipShownAsync(),
            _ => Task.CompletedTask,
        };
}