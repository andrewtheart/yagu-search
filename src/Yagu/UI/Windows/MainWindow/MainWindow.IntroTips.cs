using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Models;
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
            ApplyDrawerLabelSettings(target);
            if (target is Grid headerGrid && _realizedFileGroupHeaders.Add(headerGrid))
                headerGrid.Unloaded += OnFileGroupHeaderUnloaded;
            QueueDelayedFileDrawerIntroTip(target);
        }
    }

    private void OnFileGroupHeaderUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid headerGrid)
        {
            _realizedFileGroupHeaders.Remove(headerGrid);
            headerGrid.Unloaded -= OnFileGroupHeaderUnloaded;
            CancelDelayedFileDrawerIntroTip(headerGrid);
        }
    }

    private void CancelDelayedFileDrawerIntroTip(FrameworkElement target)
    {
        if (!ReferenceEquals(_fileDrawerIntroTipDelayTarget, target))
            return;

        _fileDrawerIntroTipDelayTimer?.Stop();
        if (_fileDrawerIntroTipDelayTimer is { } timer)
            timer.Tick -= OnFileDrawerIntroTipDelayTick;
        _fileDrawerIntroTipDelayTimer = null;
        _fileDrawerIntroTipDelayTarget = null;
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
        if (target is null || !IsRenderedFileDrawerIntroTipTarget(target))
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

    private bool IsRenderedFileDrawerIntroTipTarget(FrameworkElement target)
    {
        if (target is not Grid headerGrid
            || !_realizedFileGroupHeaders.Contains(headerGrid)
            || !target.IsLoaded
            || target.Visibility != Visibility.Visible
            || target.DataContext is not FileGroup { FilePath.Length: > 0 }
            || target.XamlRoot is null
            || target.XamlRoot != ResultsList.XamlRoot
            || target.ActualWidth <= 0
            || target.ActualHeight <= 0
            || ResultsList.ActualWidth <= 0
            || ResultsList.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Rect bounds = target.TransformToVisual(ResultsList).TransformBounds(
                new Windows.Foundation.Rect(0, 0, target.ActualWidth, target.ActualHeight));
            return bounds.Right > 0
                && bounds.Bottom > 0
                && bounds.Left < ResultsList.ActualWidth
                && bounds.Top < ResultsList.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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