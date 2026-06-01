using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
namespace Yagu;

/// <summary>
/// Advanced-options layout synchronization and splitter interaction.
/// </summary>
public sealed partial class MainWindow
{
    private bool _splitterDragging;
    private double _splitterStartX;
    private double _col0StartWidth;
    private double _col2StartWidth;

    private void OnAdvancedOptionsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        ChevronRotate.Angle = 0;
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    private void OnAdvancedOptionsCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        ChevronRotate.Angle = -90;
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    /// <summary>
    /// Forces the root grid to re-measure on every frame during the Expander
    /// expand/collapse animation so the split pane resizes in perfect sync.
    /// </summary>
    private void ListenForExpanderLayoutSync()
    {
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(400);
        debounce.IsRepeating = false;

        void handler(object? s, object? e)
        {
            AdvancedOptionsExpander.InvalidateMeasure();
            RootGrid.UpdateLayout();
            UpdateTopExpandedPreviewMeasurements();
        }

        debounce.Tick += (t, a) =>
        {
            debounce.Stop();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            AdvancedOptionsExpander.InvalidateMeasure();
            RootGrid.UpdateLayout();
            UpdateTopExpandedPreviewMeasurements();
        };

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        debounce.Start();
    }

    /// <summary>
    /// Tracks the expander animation by resizing the window on every
    /// SizeChanged event, keeping content and window perfectly in sync.
    /// A debounce timer detects when the animation has finished and
    /// unsubscribes the handler.
    /// </summary>
    private void ListenForExpanderResize()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;

        void handler(object s, SizeChangedEventArgs e)
        {
            if (_launcherMode) PositionLauncherWindow();
            timer.Stop();
            timer.Start();
        }

        timer.Tick += (t, a) =>
        {
            timer.Stop();
            RootGrid.SizeChanged -= handler;
            if (_launcherMode) PositionLauncherWindow();
        };

        RootGrid.SizeChanged += handler;
        timer.Start();
    }

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        var border = (Border)sender;
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(SplitPaneGrid).Position.X;
        _col0StartWidth = SplitPaneGrid.ColumnDefinitions[0].ActualWidth;
        _col2StartWidth = SplitPaneGrid.ColumnDefinitions[2].ActualWidth;
        border.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        double currentX = e.GetCurrentPoint(SplitPaneGrid).Position.X;
        double delta = currentX - _splitterStartX;
        double newCol0 = _col0StartWidth + delta;
        double newCol2 = _col2StartWidth - delta;
        double minWidth = 200;
        if (newCol0 < minWidth || newCol2 < minWidth) return;
        SplitPaneGrid.ColumnDefinitions[0].Width = new GridLength(newCol0, GridUnitType.Pixel);
        SplitPaneGrid.ColumnDefinitions[2].Width = new GridLength(newCol2, GridUnitType.Pixel);
        e.Handled = true;
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        ((Border)sender).ReleasePointerCapture(e.Pointer);
        // Convert back to star sizing so the layout adapts on window resize.
        double col0 = SplitPaneGrid.ColumnDefinitions[0].ActualWidth;
        double col2 = SplitPaneGrid.ColumnDefinitions[2].ActualWidth;
        double total = col0 + col2;
        if (total > 0)
        {
            SplitPaneGrid.ColumnDefinitions[0].Width = new GridLength(col0 / total, GridUnitType.Star);
            SplitPaneGrid.ColumnDefinitions[2].Width = new GridLength(col2 / total, GridUnitType.Star);
        }
        e.Handled = true;
        QueueActiveMatchOverlayRefresh();
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SplitterBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.Colors.Gray);
        SplitterBorder.Opacity = 0.5;
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            SplitterBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Transparent);
            SplitterBorder.Opacity = 1.0;
        }
    }
}
