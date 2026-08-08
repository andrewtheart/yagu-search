using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.Logging;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Logging;

namespace Yagu;

/// <summary>
/// "Add a folder to the content index" onboarding: the clickable main-window index-status indicator and
/// the one-time first-run prompt. Both offer to add one or more folders (the picked folder, a chosen
/// ancestor "subpart of the path", or further folders picked via "Add another folder…") to the index,
/// warning first when a chosen folder is a very large root. All dialogs are title-bar-less
/// <see cref="YaguDialog"/>s; the actual opt-in + background build is done by the view model.
/// </summary>
public sealed partial class MainWindow
{
    private const int IndexStatusHoverHideDelayMs = 350;
    private const double IndexStatusHoverGap = 8;
    private const double IndexStatusHoverEdgeInset = 8;
    private bool _indexStatusPointerOverIndicator;
    private bool _indexStatusPointerOverHoverPanel;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _indexStatusHoverHideTimer;
    private IReadOnlyList<string> _indexStatusHoverRepairRoots = Array.Empty<string>();

    /// <summary>Click handler for the status-bar index indicator. When a searched folder has no index yet
    /// it offers to add one; otherwise it opens the Indexing settings tab.</summary>
    private async void OnIndexStatusTapped(object sender, TappedRoutedEventArgs e)
        => await ActivateIndexStatusAsync();

    /// <summary>Keyboard activation for the (now focusable) index indicator: Enter/Space performs the
    /// primary action (same as a click); the Menu/Shift+F10 key opens the pause/resume menu via
    /// <see cref="OnIndexStatusContextRequested"/>.</summary>
    private async void OnIndexStatusKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            await ActivateIndexStatusAsync();
        }
    }

    /// <summary>The primary index-indicator action, shared by click and keyboard activation.</summary>
    private async Task ActivateIndexStatusAsync()
    {
        try
        {
            HideIndexStatusHoverOverlay();
            if (ViewModel.IndexStatusCanAddFolder)
            {
                string folder = ViewModel.IndexStatusFoldersWithoutIndex[0];
                await ShowAddFolderToIndexDialogAsync(folder);
            }
            else
            {
                OpenSettingsToIndexingTab();
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Index-status activation failed.");
        }
    }

    private void OnIndexStatusPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _indexStatusPointerOverIndicator = true;
        CancelIndexStatusHoverHide();
        // Only hint clickability (underline) when the click will actually do something useful.
        if ((ViewModel.IndexStatusCanAddFolder || ViewModel.IndexStatusCanBuildRegisteredFolder)
            && IndexStatusTextBlock is not null)
            IndexStatusTextBlock.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

        UpdateIndexStatusHoverActions();
        // Match the stable Skipped status overlay: this surface remains in the main visual tree rather
        // than opening a WinUI popup. Index-progress binding updates can therefore re-render its content
        // without dismissing/reopening the surface or generating synthetic target pointer transitions.
        ShowIndexStatusHoverOverlay();
    }

    private void ShowIndexStatusHoverOverlay()
    {
        if (IndexStatusHoverOverlay is null)
            return;

        IndexStatusHoverOverlay.Visibility = Visibility.Visible;
        PositionIndexStatusHoverOverlay();
    }

    /// <summary>Centers the overview immediately above the status label and clamps it to the window.</summary>
    private void PositionIndexStatusHoverOverlay()
    {
        if (IndexStatusHoverOverlay?.Visibility != Visibility.Visible
            || IndexStatusIndicator is null
            || RootGrid is null
            || IndexStatusIndicator.ActualWidth <= 0)
        {
            return;
        }

        IndexStatusHoverOverlay.UpdateLayout();
        double overlayWidth = IndexStatusHoverOverlay.ActualWidth > 0
            ? IndexStatusHoverOverlay.ActualWidth
            : IndexStatusHoverOverlay.DesiredSize.Width;
        double overlayHeight = IndexStatusHoverOverlay.ActualHeight > 0
            ? IndexStatusHoverOverlay.ActualHeight
            : IndexStatusHoverOverlay.DesiredSize.Height;
        if (overlayWidth <= 0 || overlayHeight <= 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (IndexStatusHoverOverlay?.Visibility == Visibility.Visible)
                    PositionIndexStatusHoverOverlay();
            });
            return;
        }

        var anchor = IndexStatusIndicator.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double left = anchor.X + (IndexStatusIndicator.ActualWidth / 2) - (overlayWidth / 2);
        double top = anchor.Y - overlayHeight - IndexStatusHoverGap;
        double maxLeft = Math.Max(IndexStatusHoverEdgeInset,
            RootGrid.ActualWidth - overlayWidth - IndexStatusHoverEdgeInset);
        double maxTop = Math.Max(IndexStatusHoverEdgeInset,
            RootGrid.ActualHeight - overlayHeight - IndexStatusHoverEdgeInset);
        IndexStatusHoverOverlay.Margin = new Thickness(
            Math.Clamp(left, IndexStatusHoverEdgeInset, maxLeft),
            Math.Clamp(top, IndexStatusHoverEdgeInset, maxTop),
            0,
            0);
    }

    private void OnIndexStatusHoverOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IndexStatusHoverOverlay?.Visibility == Visibility.Visible)
            PositionIndexStatusHoverOverlay();
    }

    private void OnIndexStatusHoverOverlayPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape
            || IndexStatusHoverOverlay?.Visibility != Visibility.Visible)
        {
            return;
        }

        if (IndexStatusAutomaticIndexingComboBox?.IsDropDownOpen == true)
            IndexStatusAutomaticIndexingComboBox.IsDropDownOpen = false;
        HideIndexStatusHoverOverlay();
        e.Handled = true;
    }

    private void OnIndexStatusPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _indexStatusPointerOverIndicator = false;
        if (IndexStatusTextBlock is not null)
            IndexStatusTextBlock.TextDecorations = Windows.UI.Text.TextDecorations.None;
        ScheduleIndexStatusHoverHide();
    }

    private void OnIndexStatusHoverPanelPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _indexStatusPointerOverHoverPanel = true;
        CancelIndexStatusHoverHide();
    }

    private void OnIndexStatusHoverPanelPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _indexStatusPointerOverHoverPanel = false;
        ScheduleIndexStatusHoverHide();
    }

    private void UpdateIndexStatusHoverActions()
    {
        RebuildIndexStatusHealthRows();

        bool canRepair = ViewModel.TryGetCurrentIndexFreshnessRepairTarget(
                out string actionLabel,
                out IReadOnlyList<string> repairRoots)
            && !ViewModel.IsIndexBuildActive
            && !ViewModel.IsIndexingPaused
            && !ViewModel.IsIndexRebuildBlocking;
        bool hasPerRootRepairAction = ViewModel.AllDriveIndexHealth.Any(static entry => entry.CanRepair);

        _indexStatusHoverRepairRoots = canRepair ? repairRoots.ToArray() : Array.Empty<string>();
        if (IndexStatusRepairButton is not null)
            IndexStatusRepairButton.Visibility = canRepair && !hasPerRootRepairAction
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (canRepair && IndexStatusRepairButtonText is not null)
            IndexStatusRepairButtonText.Text = actionLabel;

        bool automaticIndexingOff = ViewModel.Settings.EnableContentIndex
            && string.Equals(
                AppSettings.NormalizeIndexBuildTrigger(ViewModel.Settings.IndexBuildTrigger),
                AppSettings.DefaultIndexBuildTrigger,
                StringComparison.OrdinalIgnoreCase);
        if (IndexStatusAutomaticIndexingPanel is not null)
            IndexStatusAutomaticIndexingPanel.Visibility =
                automaticIndexingOff ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildIndexStatusHealthRows()
    {
        if (IndexStatusAllDriveHealthRows is null)
            return;

        IndexStatusAllDriveHealthRows.Children.Clear();
        IReadOnlyList<IndexRootHealthEntry> entries = ViewModel.AllDriveIndexHealth;
        if (entries.Count == 0)
        {
            IndexStatusAllDriveHealthRows.Children.Add(new TextBlock
            {
                Text = ViewModel.AllDriveIndexStatusText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
            return;
        }

        bool mutationActionsEnabled = !ViewModel.IsIndexBuildActive
            && !ViewModel.IsIndexingPaused
            && !ViewModel.IsIndexRebuildBlocking;
        bool settingsActionsEnabled = !ViewModel.IsIndexRebuildBlocking;
        foreach (IndexRootHealthEntry entry in entries)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(new TextBlock
            {
                Text = entry.Root,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            details.Children.Add(new TextBlock
            {
                Text = entry.Status,
                FontSize = 11,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(details);

            var actions = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (entry.CanAddToIndex && entry.AddRoot is { } addRoot)
            {
                Button add = CreateIndexStatusHealthActionButton(
                    "Add to index",
                    $"Add {addRoot} to the content index and choose when it is kept up to date",
                    mutationActionsEnabled);
                add.Click += async (_, _) => await RunIndexStatusAddToIndexActionAsync(addRoot);
                actions.Children.Add(add);
            }
            else if (entry.CanBuildNow && entry.BuildRoot is { } buildRoot)
            {
                Button build = CreateIndexStatusHealthActionButton(
                    "Build now",
                    $"Build the content index for the already-maintained folder {buildRoot}",
                    mutationActionsEnabled);
                build.Click += async (_, _) => await RunIndexStatusBuildNowActionAsync(buildRoot);
                actions.Children.Add(build);
            }
            else if (entry.CanMaintain && entry.MaintainRoot is { } maintainRoot)
            {
                Button maintain = CreateIndexStatusHealthActionButton(
                    "Maintain",
                    $"Add {maintainRoot} to maintained folders without rebuilding its existing index",
                    settingsActionsEnabled);
                maintain.Click += async (_, _) => await RunIndexStatusMaintainActionAsync(maintainRoot);
                actions.Children.Add(maintain);

                if (entry.CanDeleteStoredIndex && entry.DeleteRoot is { } deleteRoot)
                {
                    Button delete = CreateIndexStatusHealthActionButton(
                        "Delete index",
                        $"Delete the unmaintained stored index for {deleteRoot}",
                        mutationActionsEnabled);
                    delete.Click += async (_, _) => await RunIndexStatusDeleteActionAsync(deleteRoot);
                    actions.Children.Add(delete);
                }
            }
            else if (entry.SizeBudgetRoot is { } budgetRoot)
            {
                Button fix = CreateIndexStatusHealthActionButton(
                    "Fix…",
                    $"Explain why {budgetRoot} stopped updating and choose how to fix it",
                    settingsActionsEnabled);
                fix.Click += async (_, _) => await ShowIndexSizeBudgetDialogAsync(budgetRoot, fromUserAction: true);
                actions.Children.Add(fix);
            }
            else if (entry.CanIncrementallyRefresh && entry.IncrementalRoot is { } incrementalRoot)
            {
                Button update = CreateIndexStatusHealthActionButton(
                    "Increase limit & update",
                    $"Raise the journal catch-up limit and safely update {incrementalRoot}",
                    mutationActionsEnabled);
                update.Click += async (_, _) => await RunIndexStatusHealthActionAsync(incrementalRoot, incremental: true);
                actions.Children.Add(update);

                Button rebuild = CreateIndexStatusHealthActionButton(
                    "Rebuild",
                    $"Completely rebuild the index for {incrementalRoot}",
                    mutationActionsEnabled);
                rebuild.Click += async (_, _) => await RunIndexStatusHealthActionAsync(incrementalRoot, incremental: false);
                actions.Children.Add(rebuild);
            }
            else if (entry.CanRepair && entry.RepairRoot is { } repairRoot)
            {
                Button rebuild = CreateIndexStatusHealthActionButton(
                    "Rebuild",
                    $"Rebuild the index for {repairRoot}",
                    mutationActionsEnabled);
                rebuild.Click += async (_, _) => await RunIndexStatusHealthActionAsync(repairRoot, incremental: false);
                actions.Children.Add(rebuild);
            }

            if (actions.Children.Count > 0)
            {
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
            }
            IndexStatusAllDriveHealthRows.Children.Add(row);
        }
    }

    private static Button CreateIndexStatusHealthActionButton(
        string label,
        string tooltip,
        bool enabled)
    {
        var button = new Button
        {
            Content = label,
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 11,
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private async Task RunIndexStatusHealthActionAsync(string root, bool incremental)
    {
        HideIndexStatusHoverOverlay();
        try
        {
            if (incremental)
            {
                int raisedLimit = ComputeRaisedJournalCatchupLimit(
                    ViewModel.Settings.IndexMaxJournalCatchupRecords);
                await ViewModel.RefreshCurrentIndexIncrementallyAsync(root, raisedLimit);
            }
            else
            {
                await ViewModel.RebuildCurrentIndexBlockingAsync(new[] { root });
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Index-status health action failed for '{Root}'.", root);
        }
    }

    /// <summary>Runs the first build for an already-registered root whose index has never been built.
    /// Separate from <see cref="RunIndexStatusHealthActionAsync"/> so the blocking overlay and its cancel
    /// button report a build, not a rebuild of an index that does not exist yet.</summary>
    private async Task RunIndexStatusBuildNowActionAsync(string root)
    {
        HideIndexStatusHoverOverlay();
        try
        {
            await ViewModel.BuildRegisteredIndexBlockingAsync(root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Building the index for '{Root}' from the status flyout failed.", root);
        }
    }

    /// <summary>Opts an eligible-but-unindexed drive in straight from the status flyout. It reuses the
    /// standard add-folder modal rather than a bespoke one, so the same row also lets the user choose the
    /// build trigger(s) and update mode, narrow the scope to a subfolder, and get the large-root warning
    /// before an unattended whole-drive build starts.</summary>
    private async Task RunIndexStatusAddToIndexActionAsync(string root)
    {
        HideIndexStatusHoverOverlay();
        try
        {
            await ShowAddFolderToIndexDialogAsync(root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Adding '{Root}' to the index from the status flyout failed.", root);
        }
    }

    private async Task RunIndexStatusMaintainActionAsync(string root)
    {
        HideIndexStatusHoverOverlay();
        try
        {
            await ViewModel.MaintainExistingIndexAsync(root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Maintaining the existing index for '{Root}' failed.", root);
        }
    }

    private async Task RunIndexStatusDeleteActionAsync(string root)
    {
        HideIndexStatusHoverOverlay();
        using var suggestionSuppression = ParkInputSuggestionsForModal();
        YaguDialogResult result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Delete stored index",
                TitleGlyph = "\uE74D",
                Content = $"Delete the unmaintained content index for:\n{root}\n\nSearches will scan this drive live. This does not delete any files from the drive.",
                PrimaryButtonText = "Delete index",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 600,
                Height = 300,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
        if (result != YaguDialogResult.Primary)
            return;

        try
        {
            await ViewModel.DeleteStoredIndexAsync(root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Deleting the existing index for '{Root}' failed.", root);
        }
    }

    private async void OnIndexStatusRepairClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> roots = _indexStatusHoverRepairRoots;
        HideIndexStatusHoverOverlay();
        if (roots.Count > 0)
            await ViewModel.RebuildCurrentIndexBlockingAsync(roots);
    }

    private void OnIndexStatusOpenSettingsFromHoverClick(object sender, RoutedEventArgs e)
    {
        HideIndexStatusHoverOverlay();
        OpenSettingsToIndexingTab();
    }

    private void OnIndexStatusAutomaticIndexingDropDownOpened(object sender, object e)
        => CancelIndexStatusHoverHide();

    private void OnIndexStatusAutomaticIndexingDropDownClosed(object sender, object e)
        => ScheduleIndexStatusHoverHide();

    private async void OnIndexStatusAutomaticIndexingSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox
            || comboBox.SelectedItem is not ComboBoxItem { Tag: string trigger })
            return;

        comboBox.IsEnabled = false;
        try
        {
            await ViewModel.SetAutomaticIndexingPresetAsync(trigger);
            comboBox.SelectedIndex = -1;
            UpdateIndexStatusHoverActions();
        }
        finally
        {
            comboBox.IsEnabled = true;
        }
    }

    private void OnCancelIndexRebuildClick(object sender, RoutedEventArgs e)
        => ViewModel.CancelCurrentIndexRebuild();

    private void ScheduleIndexStatusHoverHide()
    {
        CancelIndexStatusHoverHide();
        var timer = DispatcherQueue.CreateTimer();
        _indexStatusHoverHideTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(IndexStatusHoverHideDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Ignore a stale queued tick from an older pointer transition.
            if (!ReferenceEquals(_indexStatusHoverHideTimer, timer))
                return;
            _indexStatusHoverHideTimer = null;
            if (!_indexStatusPointerOverIndicator
                && !_indexStatusPointerOverHoverPanel
                && IndexStatusAutomaticIndexingComboBox?.IsDropDownOpen != true)
                HideIndexStatusHoverOverlay();
        };
        timer.Start();
    }

    private void CancelIndexStatusHoverHide()
    {
        _indexStatusHoverHideTimer?.Stop();
        _indexStatusHoverHideTimer = null;
    }

    private void HideIndexStatusHoverOverlay()
    {
        CancelIndexStatusHoverHide();
        _indexStatusPointerOverIndicator = false;
        _indexStatusPointerOverHoverPanel = false;
        if (IndexStatusTextBlock is not null)
            IndexStatusTextBlock.TextDecorations = Windows.UI.Text.TextDecorations.None;
        if (IndexStatusHoverOverlay is not null)
            IndexStatusHoverOverlay.Visibility = Visibility.Collapsed;
    }

    // ── Spinning glyph while a build is actively running ──

    private bool _indexBuildSpinRunning;

    /// <summary>
    /// Starts or stops the continuous rotation of the status-bar index glyph. The glyph spins a full
    /// circle (~1.1s per revolution, forever) while a build is <b>actively</b> running, and stops
    /// (resetting to upright) when indexing is idle, paused, or finished. Driven from the ViewModel
    /// PropertyChanged handler for index build/warm activity and pause state.
    /// </summary>
    private void UpdateIndexBuildSpinAnimation()
    {
        if ((ViewModel.IsIndexBuildActive || ViewModel.IsIndexWarmActive)
            && !ViewModel.IsIndexingPaused
            && !ViewModel.IsIndexWarmPausedForSearch)
            StartIndexBuildSpin();
        else
            StopIndexBuildSpin();
    }

    private void StartIndexBuildSpin()
    {
        // Idempotent: restarting a ProgressRing resets its animation, so overlapping/redundant build/warm
        // notifications must never re-arm it. The ring's native animated visual runs on the compositor.
        // We deliberately don't rotate the tiny status FontIcon anymore: even a compositor-driven 12px
        // glyph is a rasterized bitmap whose diagonals pixel-snap as it rotates, which looks like frame
        // stutter under load. The fixed 16px icon slot also prevents percentage-width layout jumps.
        if (_indexBuildSpinRunning)
            return;
        if (IndexStatusGlyphHost is null || IndexStatusProgressRing is null)
            return;

        IndexStatusGlyphHost.Visibility = Visibility.Collapsed;
        IndexStatusProgressRing.Visibility = Visibility.Visible;
        IndexStatusProgressRing.IsActive = true;
        _indexBuildSpinRunning = true;
    }

    private void StopIndexBuildSpin()
    {
        _indexBuildSpinRunning = false;
        if (IndexStatusGlyphHost is null || IndexStatusProgressRing is null)
            return;
        IndexStatusProgressRing.IsActive = false;
        IndexStatusProgressRing.Visibility = Visibility.Collapsed;
        IndexStatusGlyphHost.Visibility = Visibility.Visible;
    }

    /// <summary>Context-menu request on the index indicator (mouse right-click OR the keyboard Menu /
    /// Shift+F10 key on the focused indicator): a registered-but-unbuilt root offers an immediate rebuild,
    /// and every index label offers an "Options" submenu (pause / disable this run / disable
    /// persistently) — which toggles to the matching enable commands once the index has been turned off.
    /// Using <c>ContextRequested</c> (not <c>RightTapped</c>) makes every command keyboard-accessible.
    /// The hover overlay is drawn over the indicator and shares this handler, so hovering (which shows the
    /// overlay) never costs the user the right-click menu.</summary>
    private void OnIndexStatusContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // Anchor on the indicator, not the sender: the request may come from the overlay we hide below.
        FrameworkElement anchor = IndexStatusIndicator ?? (FrameworkElement)sender;
        bool hasPosition = e.TryGetPosition(anchor, out Windows.Foundation.Point pos);
        HideIndexStatusHoverOverlay();
        var menu = new MenuFlyout();

        if (!ViewModel.Settings.EnableContentIndex)
        {
            // Persistently disabled (the indicator is kept visible as "Index: off" so this menu stays
            // reachable). Mirror the enabled layout: an "Options" submenu whose persistent toggle now reads
            // "Enable indexing (persistent)" (the inverse of "Disable indexing (persistent)") and turns the
            // whole feature back on + saves.
            var optionsSubMenu = new MenuFlyoutSubItem
            {
                Text = "Options",
                Icon = new FontIcon { Glyph = "\uE712" }, // More
            };
            var enablePersistent = new MenuFlyoutItem
            {
                Text = "Enable indexing (persistent)",
                Icon = new FontIcon { Glyph = "\uE768" }, // Play
            };
            enablePersistent.Click += (_, _) => _ = ViewModel.EnableContentIndexFromStatusMenuAsync();
            optionsSubMenu.Items.Add(enablePersistent);
            menu.Items.Add(optionsSubMenu);
        }
        else
        {
            // A built index for the searched root(s): show its date and let a click rebuild it behind a
            // full-window blocking overlay. Hidden while a build/rebuild is already running or paused.
            if (ViewModel.TryGetCurrentIndexRebuildTarget(out string indexDateLabel, out IReadOnlyList<string> builtRoots)
                && !ViewModel.IsIndexBuildActive
                && !ViewModel.IsIndexingPaused
                && !ViewModel.IsIndexRebuildBlocking)
            {
                var indexDate = new MenuFlyoutItem
                {
                    Text = indexDateLabel, // "Index date: MM/ddd/yyyy HH:mm (click to rebuild)"
                    Icon = new FontIcon { Glyph = "\uE787" }, // Calendar
                };
                indexDate.Click += (_, _) => _ = ViewModel.RebuildCurrentIndexBlockingAsync(builtRoots);
                menu.Items.Add(indexDate);
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            bool canRebuildRegistered = ViewModel.IndexStatusCanBuildRegisteredFolder
                && !ViewModel.IsIndexBuildActive
                && !ViewModel.IsIndexingPaused;
            if (canRebuildRegistered)
            {
                string root = ViewModel.IndexStatusRegisteredFoldersWithoutIndex[0];
                var rebuild = new MenuFlyoutItem
                {
                    Text = $"Rebuild now ({root})",
                    Icon = new FontIcon { Glyph = "\uE72C" }, // Refresh
                };
                rebuild.Click += (_, _) => ViewModel.RebuildRegisteredIndexNow(root);
                menu.Items.Add(rebuild);
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            // "Options" is inert on its own — it only expands this submenu on hover/click.
            var disableSubMenu = new MenuFlyoutSubItem
            {
                Text = "Options",
                Icon = new FontIcon { Glyph = "\uE712" }, // More
            };

            // Pause / resume the active build. Pause is disabled when no build is currently running.
            if (ViewModel.IsIndexingPaused)
            {
                var resume = new MenuFlyoutItem { Text = "Resume indexing", Icon = new FontIcon { Glyph = "\uE768" } }; // Play
                resume.Click += (_, _) => ViewModel.ResumeIndexing();
                disableSubMenu.Items.Add(resume);
            }
            else
            {
                var pause = new MenuFlyoutItem
                {
                    Text = "Pause indexing",
                    Icon = new FontIcon { Glyph = "\uE769" }, // Pause
                    IsEnabled = ViewModel.CanPauseIndexing,
                };
                pause.Click += (_, _) => ViewModel.PauseIndexing();
                disableSubMenu.Items.Add(pause);
            }

            // Toggle the per-session "use the index" flag (session-only; not saved).
            if (ViewModel.UseContentIndex)
            {
                var disableThisRun = new MenuFlyoutItem
                {
                    Text = "Disable index (this run)",
                    Icon = new FontIcon { Glyph = "\uE823" }, // History (temporary / session)
                };
                disableThisRun.Click += (_, _) => ViewModel.DisableContentIndexThisRun();
                disableSubMenu.Items.Add(disableThisRun);
            }
            else
            {
                var enableThisRun = new MenuFlyoutItem
                {
                    Text = "Use index (this run)",
                    Icon = new FontIcon { Glyph = "\uE768" }, // Play
                };
                enableThisRun.Click += (_, _) => ViewModel.EnableContentIndexThisRun();
                disableSubMenu.Items.Add(enableThisRun);
            }

            // Persistent: turn the content-index feature off and save it.
            var disablePersistent = new MenuFlyoutItem
            {
                Text = "Disable indexing (persistent)",
                Icon = new FontIcon { Glyph = "\uEA39" }, // Blocked
            };
            disablePersistent.Click += (_, _) => _ = ViewModel.DisableContentIndexPersistentlyAsync();
            disableSubMenu.Items.Add(disablePersistent);

            menu.Items.Add(disableSubMenu);
        }

        // Pointer request carries a position; a keyboard request does not — anchor to the element instead.
        if (hasPosition)
            menu.ShowAt(anchor, pos);
        else
            menu.ShowAt(anchor);
        e.Handled = true;
    }

    /// <summary>Opens the Settings window on the Indexing tab (resolved by header, since tabs are sorted
    /// alphabetically so the index isn't fixed).</summary>
    private void OpenSettingsToIndexingTab()
    {
        OpenSettingsTab();
        _settingsWindow?.SelectTabByHeader("Indexing");
    }

    /// <summary>
    /// The one-time first-run "add a folder to the index?" prompt. Shown once (tracked by
    /// <see cref="AppSettings.HasPromptedIndexOnboarding"/>); if the user chooses a folder it flows into
    /// <see cref="ShowAddFolderToIndexDialogAsync"/> (which warns for a very large folder). Never throws.
    /// </summary>
    private async Task CheckFirstRunIndexOnboardingAsync()
    {
        if (ViewModel.Settings.HasPromptedIndexOnboarding)
            return;

        if (string.IsNullOrWhiteSpace(ViewModel.Settings.IndexStorageDirectory)
            && DefaultContentIndexPathProvider.TryGetPreservedStorageDirectory(out string preservedStorageDirectory))
        {
            ViewModel.Settings.IndexStorageDirectory = preservedStorageDirectory;
            await ViewModel.PersistSettingsAsync();
            DefaultContentIndexPathProvider.ClearPreservedStorageDirectory();
        }

        // Registered roots prove that this settings file has already completed index setup. Older builds can
        // rewrite the shared settings file without newer fields, so migrate that known configuration quietly.
        if (ViewModel.Settings.IndexedRoots.Count > 0)
        {
            ViewModel.Settings.HasPromptedIndexOnboarding = true;
            await ViewModel.PersistSettingsAsync();
            return;
        }

        // Belt-and-braces: if another owned modal is still up, retry next launch (don't mark shown yet).
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        IReadOnlyList<string> reusableRoots = await Task.Run(() => new ContentIndexManager(
            DefaultContentIndexPathProvider.Create(ViewModel.Settings.IndexStorageDirectory),
            ViewModel.Settings.IndexRetainedGenerationCount).GetReusableStoredIndexRoots());
        if (reusableRoots.Count > 0)
        {
            try
            {
                string rootList = string.Join("\n", reusableRoots.Take(5).Select(root => $"\u2022 {root}"));
                if (reusableRoots.Count > 5)
                    rootList += $"\n\u2022 and {reusableRoots.Count - 5} more";

                using var preservedIndexSuppression = ParkInputSuggestionsForModal();
                var preservedChoice = await YaguDialog.ShowAsync(
                    _hwnd,
                    new YaguDialogOptions
                    {
                        Title = "Existing content indexes found",
                        TitleGlyph = "\uE8F1", // list/library
                        Content =
                            "Yagu found content indexes preserved from an earlier installation:\n\n"
                            + rootList
                            + "\n\nUse these indexes again without rebuilding them? You can review or remove them later in "
                            + "Settings \u25B8 Indexing.",
                        PrimaryButtonText = "Use existing indexes",
                        SecondaryButtonText = "Choose a different folder\u2026",
                        CloseButtonText = "Not now",
                        DefaultButton = YaguDialogDefaultButton.Primary,
                        RequestedTheme = RootGrid.ActualTheme,
                        ShowTitleBar = false,
                        ShowTopRightCloseButton = true,
                        Width = 640,
                        Height = 420,
                    });

                if (preservedChoice == YaguDialogResult.Primary)
                {
                    foreach (string root in reusableRoots)
                        ViewModel.Settings.IndexedRoots = IndexedRootsPolicy.Add(ViewModel.Settings.IndexedRoots, root);
                    ViewModel.Settings.EnableContentIndex = true;
                    ViewModel.Settings.UseContentIndexByDefault = true;
                }

                ViewModel.Settings.HasPromptedIndexOnboarding = true;
                await ViewModel.PersistSettingsAsync();
                if (preservedChoice != YaguDialogResult.Secondary)
                    return;
            }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "Preserved-index onboarding failed.");
                return;
            }
        }

        // Mark shown regardless of the choice, so the prompt never nags on later launches.
        ViewModel.Settings.HasPromptedIndexOnboarding = true;
        await ViewModel.PersistSettingsAsync();

        // The native folder picker — and the moment between our YaguDialogs closing and it opening — is not
        // covered by the YaguDialog-scoped suggestion suppression, so a windowed query/directory suggestion
        // popup (its own top-level window) can float above it. Park the dropdowns shut for the whole flow.
        using var suppression = ParkInputSuggestionsForModal();

        try
        {
            var choice = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Speed up searches with an index?",
                    TitleGlyph = "\uE8F1", // list/library
                    Content =
                        "Yagu can build a content index for a folder you search often, so future searches can skip files "
                        + "that cannot contain a match. Matching files are always still read live from disk.\n\n"
                        + "Would you like to choose a folder to index now? You can always manage indexes later in "
                        + "Settings \u25B8 Indexing.",
                    PrimaryButtonText = "Choose a folder\u2026",
                    CloseButtonText = "Not now",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    RequestedTheme = RootGrid.ActualTheme,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    Width = 600,
                    Height = 360,
                });

            if (choice != YaguDialogResult.Primary)
                return;

            string? folder = Win32FileDialog.SelectFolder(_hwnd, "Select a folder to index");
            if (string.IsNullOrWhiteSpace(folder))
                return;

            await ShowAddFolderToIndexDialogAsync(folder, applyFirstRunDriveIndexingProfile: true);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "First-run index onboarding failed.");
        }
    }

    /// <summary>
    /// Shows the "add this folder to the index" dialog for <paramref name="folder"/>. The user picks the
    /// folder or one of its ancestors ("subpart of the path"), and may pick <em>additional, unrelated</em>
    /// folders via "Add another folder…" before committing; a very large chosen root triggers a warning
    /// before the build starts. On confirmation the feature is enabled and a background build begins.
    /// </summary>
    private async Task ShowAddFolderToIndexDialogAsync(string folder, bool applyFirstRunDriveIndexingProfile = false)
    {
        IReadOnlyList<string> initialChoices = IndexOnboardingPlan.PathChoices(folder);
        if (initialChoices.Count == 0)
            return;

        // Keep the query/directory suggestion dropdowns parked shut across this multi-dialog flow (including
        // the bounded size-probe gap where no modal is up) so a windowed suggestion popup can't float above.
        using var suppression = ParkInputSuggestionsForModal();

        // A choice already covered by an equal or broader registered root cannot be "added" again. Flag
        // those below so C:\ prevents a duplicate C:\src index, and if every choice is covered explain that
        // instead of offering a no-op add.
        IReadOnlyList<string> indexedRoots = ViewModel.Settings.IndexedRoots;
        bool IsAlreadyCovered(string candidate) => IndexedRootsPolicy.FindBestCoveringRoot(indexedRoots, candidate) is not null;
        if (initialChoices.All(IsAlreadyCovered))
        {
            await ShowFolderAlreadyCoveredDialogAsync(folder);
            return;
        }

        // Path choices accumulate across "Add another folder…" rounds: each picked folder contributes itself
        // and its ancestors, so unrelated trees (C:\src AND D:\data) can be added in one pass. Selections are
        // held here — not in the discarded controls — so re-showing the dialog never loses the user's work.
        var choices = new List<string>();
        var seenChoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void MergeChoices(IEnumerable<string> more)
        {
            foreach (string candidate in more)
            {
                if (seenChoices.Add(candidate))
                    choices.Add(candidate);
            }
        }
        MergeChoices(initialChoices);

        // Default-check the first choice that can actually be added (not one already in the index).
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (choices.FirstOrDefault(c => !IsAlreadyCovered(c)) is { } firstAddable)
            checkedPaths.Add(firstAddable);

        var triggerFlags = new (string Flag, string Display)[]
        {
            ("AtStartup", "When Yagu starts"),
            ("WhenIdle", "When the machine is idle"),
            ("Continuous", "Continuously while Yagu is open"),
            ("OnSchedule", "On a schedule (configure in Settings)"),
        };
        var updateModes = new (string Mode, string Display)[]
        {
            (AppSettings.IndexUpdateModeAutomaticIncremental, "Automatic incremental \u2014 apply small delta updates when changed (recommended)"),
            (AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty, "Automatic full rebuild when changed"),
            (AppSettings.DefaultIndexUpdateMode, "Manual full rebuild \u2014 only create missing indexes"),
        };

        string initialBuildTrigger = applyFirstRunDriveIndexingProfile
            ? AppSettings.IndexBuildTriggerContinuous
            : ViewModel.Settings.IndexBuildTrigger;
        string initialUpdateMode = applyFirstRunDriveIndexingProfile
            ? AppSettings.IndexUpdateModeAutomaticIncremental
            : ViewModel.Settings.IndexUpdateMode;
        var selectedTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (flag, _) in triggerFlags)
        {
            if (AppSettings.IndexBuildTriggerHas(initialBuildTrigger, flag))
                selectedTriggers.Add(flag);
        }
        // An automatic trigger paired with the default Manual full rebuild only ever creates MISSING
        // indexes, so an existing index silently goes stale. Preselect the recommended mode for the current
        // trigger selection, and keep tracking it live until the user overrides the combo themselves.
        string updateMode = ContentIndexBuildScheduler.RecommendedUpdateMode(
            string.Join(",", selectedTriggers), initialUpdateMode);
        bool updateModeOverridden = false;

        List<string> chosen;
        while (true)
        {
            // Multi-select: the user may add the chosen folder AND/OR one or more of its ancestors at once,
            // so each addable path choice is an independent CheckBox (not a single-select radio).
            var folderChecks = new List<CheckBox>(choices.Count);
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "Add one or more folders to the content index so future searches over them can skip files "
                     + "that cannot contain a match. Matching files are always still read live from disk.",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Index which folder(s)?",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });

            foreach (string choice in choices)
            {
                bool already = IsAlreadyCovered(choice);
                string label = choice;
                if (IndexOnboardingPlan.IsLikelyLargeRoot(choice))
                    label += "   (whole drive or large system folder)";
                if (already)
                    label += "   \u2014 already covered by the index";
                var cb = new CheckBox
                {
                    Content = label,
                    Tag = choice,
                    IsEnabled = !already, // can't add a folder that is already an index root
                    IsChecked = !already && checkedPaths.Contains(choice),
                };
                folderChecks.Add(cb);
                panel.Children.Add(cb);
            }
            panel.Children.Add(new TextBlock
            {
                Text = "Use \u201cAdd another folder\u2026\u201d to include a folder outside this path; your selections above are kept.",
                Opacity = 0.75,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            // Build trigger(s): the same combinable choices as Settings ▸ Indexing so the user can decide up
            // front how these folders' indexes are kept up to date. None checked = Manual (only builds on
            // request). Seeded from the current setting.
            panel.Children.Add(new TextBlock
            {
                Text = "Keep the index up to date:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            var triggerChecks = new List<(CheckBox Check, string Flag)>(triggerFlags.Length);
            var triggerColumn = new StackPanel { Spacing = 2 };
            foreach (var (flag, display) in triggerFlags)
            {
                var cb = new CheckBox
                {
                    Content = display,
                    MinWidth = 0,
                    IsChecked = selectedTriggers.Contains(flag),
                };
                triggerChecks.Add((cb, flag));
                triggerColumn.Children.Add(cb);
            }
            panel.Children.Add(triggerColumn);
            panel.Children.Add(new TextBlock
            {
                Text = "With none selected, indexing is Manual and only runs when you ask. The schedule and "
                     + "update mode below apply to every indexed folder, not just this one. You can change "
                     + "them anytime in Settings \u25B8 Indexing.",
                Opacity = 0.75,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            // Update mode: what those automatic passes actually DO. Selecting an automatic trigger while this
            // stayed on Manual full rebuild is the bug this control exists to prevent, so it follows the
            // recommendation as triggers are toggled — until the user picks a mode themselves.
            panel.Children.Add(new TextBlock
            {
                Text = "When a folder changes:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            var updateModeCombo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (mode, display) in updateModes)
                updateModeCombo.Items.Add(new ComboBoxItem { Content = display, Tag = mode });
            void SelectUpdateMode(string mode)
            {
                foreach (object item in updateModeCombo.Items)
                {
                    if (item is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
                    {
                        updateModeCombo.SelectedItem = item;
                        return;
                    }
                }
            }
            SelectUpdateMode(updateMode);
            var updateModeHint = new TextBlock
            {
                Opacity = 0.75,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            void RefreshUpdateModeHint()
            {
                updateModeHint.Text = ContentIndexBuildScheduler.IsStaleAutomaticCombination(
                    string.Join(",", triggerChecks.Where(t => t.Check.IsChecked == true).Select(t => t.Flag)),
                    updateModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : updateMode)
                    ? "Manual full rebuild only creates MISSING indexes \u2014 the automatic trigger(s) above will never "
                      + "refresh an index you already have, so it goes stale and searches fall back to a live scan."
                    : "Incremental applies small append-only delta updates and periodically compacts them; a full "
                      + "rebuild re-indexes the whole folder. Both fall back to a live scan when the index is stale.";
            }
            updateModeCombo.SelectionChanged += (_, _) =>
            {
                if (updateModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
                    && !string.Equals(tag, updateMode, StringComparison.OrdinalIgnoreCase))
                {
                    updateMode = tag;
                    updateModeOverridden = true; // an explicit pick wins over the trigger-driven default
                }
                RefreshUpdateModeHint();
            };
            foreach (var (check, _) in triggerChecks)
            {
                void OnTriggerToggled(object sender, RoutedEventArgs e)
                {
                    if (!updateModeOverridden)
                    {
                        string trigger = string.Join(",", triggerChecks.Where(t => t.Check.IsChecked == true).Select(t => t.Flag));
                        string recommended = ContentIndexBuildScheduler.RecommendedUpdateMode(trigger, initialUpdateMode);
                        updateMode = recommended;
                        SelectUpdateMode(recommended);
                    }
                    RefreshUpdateModeHint();
                }
                check.Checked += OnTriggerToggled;
                check.Unchecked += OnTriggerToggled;
            }
            RefreshUpdateModeHint();
            panel.Children.Add(updateModeCombo);
            panel.Children.Add(updateModeHint);

            // "Included Locations" context (inspired by the Windows Indexing Options dialog).
            panel.Children.Add(new TextBlock
            {
                Text = "Currently indexed folders:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            if (indexedRoots.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "None yet.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                foreach (string existing in indexedRoots)
                    panel.Children.Add(new TextBlock { Text = "\u2022 " + existing, Opacity = 0.85, TextWrapping = TextWrapping.Wrap });
            }

            var result = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Add folders to the content index",
                    TitleGlyph = "\uE8F1",
                    Content = panel,
                    PrimaryButtonText = "Add to index",
                    SecondaryButtonText = "Add another folder\u2026",
                    CloseButtonText = "Cancel",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    RequestedTheme = RootGrid.ActualTheme,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    Width = 680,
                    Height = 620,
                    MaxContentHeight = 520,
                });

            // Carry every selection forward: a re-show (or the commit below) must reflect what the user
            // last saw, not the state this round started with.
            checkedPaths.Clear();
            foreach (var cb in folderChecks)
            {
                if (cb.IsChecked == true && cb.Tag is string path)
                    checkedPaths.Add(path);
            }
            selectedTriggers.Clear();
            foreach (var (check, flag) in triggerChecks)
            {
                if (check.IsChecked == true)
                    selectedTriggers.Add(flag);
            }
            if (updateModeCombo.SelectedItem is ComboBoxItem { Tag: string selectedMode })
                updateMode = selectedMode;

            if (result == YaguDialogResult.Secondary)
            {
                string? another = Win32FileDialog.SelectFolder(_hwnd, "Select another folder to index");
                if (!string.IsNullOrWhiteSpace(another))
                {
                    IReadOnlyList<string> moreChoices = IndexOnboardingPlan.PathChoices(another);
                    MergeChoices(moreChoices);
                    // Pre-check the folder the user just picked (or its nearest addable ancestor).
                    if (moreChoices.FirstOrDefault(c => !IsAlreadyCovered(c)) is { } newlyAddable)
                        checkedPaths.Add(newlyAddable);
                }
                continue;
            }

            if (result != YaguDialogResult.Primary)
                return;

            // Only addable (not-already-covered) checked choices proceed; the covered ones are disabled above.
            chosen = choices.Where(c => checkedPaths.Contains(c) && !IsAlreadyCovered(c)).ToList();
            break;
        }

        if (chosen.Count == 0)
            return;

        // Warn once before an unattended build if ANY chosen folder is a very large root.
        foreach (string candidate in chosen)
        {
            if (!await ConfirmLargeFolderIfNeededAsync(candidate))
                return;
        }

        // Resolve the selected build trigger(s) into the combined setting value ("Manual" if none).
        string buildTrigger = AppSettings.NormalizeIndexBuildTrigger(string.Join(",", selectedTriggers));

        await ViewModel.AddFoldersToIndexAndBuildAsync(
            chosen,
            buildTrigger,
            updateMode,
            applyFirstRunDriveIndexingProfile);

        string folderList = chosen.Count == 1
            ? chosen[0]
            : string.Join("\n", chosen.Select(f => "\u2022 " + f));
        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Indexing started",
                TitleGlyph = "\uE930", // completed
                Content =
                    $"Yagu is building a content index for:\n{folderList}\n\n"
                    + "This runs in the background and does not change any search results \u2014 it only lets future "
                    + "searches skip files that cannot match. You can track or manage it in Settings \u25B8 Indexing.",
                CloseButtonText = "OK",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 600,
                Height = 380,
            });
    }

    /// <summary>
    /// Shown when the chosen folder is already covered by an equal or broader registered root, so there is
    /// nothing new to add. Registration does not imply that a usable generation has been built yet.
    /// </summary>
    private async Task ShowFolderAlreadyCoveredDialogAsync(string folder)
    {
        string normalized = IndexScopeIdentity.NormalizePath(folder);
        string coveringRoot = IndexedRootsPolicy.FindBestCoveringRoot(ViewModel.Settings.IndexedRoots, normalized)
            ?? normalized;
        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Already covered by the content index",
                TitleGlyph = "\uE8F1",
                Content =
                    $"{normalized} is already covered by the registered index root:\n{coveringRoot}\n\n"
                    + "Yagu uses that one broader index for searches under this folder, so no duplicate child index is needed. "
                    + "If the broader index has not been built yet, open Settings \u25B8 Indexing and choose Build now. "
                    + "You can also rebuild it, change which files it covers, or remove it there.",
                PrimaryButtonText = "Open Settings",
                CloseButtonText = "OK",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 600,
                Height = 340,
            });

        if (result == YaguDialogResult.Primary)
            OpenSettingsToIndexingTab();
    }

    /// <summary>
    /// If <paramref name="folder"/> is a very large root (cheap heuristic, or a bounded on-disk file-count
    /// probe reaches <see cref="IndexOnboardingPlan.LargeFolderFileThreshold"/>), shows a warning dialog and
    /// returns whether the user chose to proceed. For an ordinary folder it returns true without a prompt.
    /// </summary>
    private async Task<bool> ConfirmLargeFolderIfNeededAsync(string folder)
    {
        bool likelyLarge = IndexOnboardingPlan.IsLikelyLargeRoot(folder);
        long probedCount = -1;
        if (!likelyLarge)
        {
            probedCount = await Task.Run(() => BoundedFileCount(folder, IndexOnboardingPlan.LargeFolderFileThreshold));
            likelyLarge = probedCount >= IndexOnboardingPlan.LargeFolderFileThreshold;
        }

        if (!likelyLarge)
            return true;

        string sizeNote = probedCount >= IndexOnboardingPlan.LargeFolderFileThreshold
            ? $"It contains at least {IndexOnboardingPlan.LargeFolderFileThreshold:N0} files."
            : "It looks like a whole drive or a large system folder.";

        var warn = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Index a very large folder?",
                TitleGlyph = "\uE7BA", // caution triangle
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content =
                    $"{folder}\n\n{sizeNote} Building an index for it can take a long time and use a lot of disk "
                    + "space (the index is stored under your index storage folder).\n\n"
                    + "You can pick a smaller subfolder instead, or add it anyway.",
                PrimaryButtonText = "Add anyway",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 600,
                Height = 360,
            });

        return warn == YaguDialogResult.Primary;
    }

    /// <summary>
    /// Counts files under <paramref name="path"/> recursively, stopping as soon as it reaches
    /// <paramref name="cap"/> or a short time budget elapses (returning <paramref name="cap"/> in that case,
    /// so a folder too slow to fully enumerate is treated as large). Best-effort and never throws.
    /// </summary>
    private static long BoundedFileCount(string path, int cap)
    {
        long count = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };
            foreach (string _ in Directory.EnumerateFiles(path, "*", options))
            {
                if (++count >= cap)
                    return count;
                if ((count & 0x3FFF) == 0 && stopwatch.Elapsed > TimeSpan.FromSeconds(2))
                    return cap; // too slow to fully enumerate → treat as large
            }
        }
        catch
        {
            // Best effort — report whatever was counted before the failure.
        }
        return count;
    }

    /// <summary>
    /// Parks the query/directory suggestion dropdowns shut (and bumps the owned-modal depth) for the
    /// lifetime of the returned scope, then lets the suppression linger ~1s as focus returns. Use around a
    /// flow that shows a native picker or a non-YaguDialog window and the intervening focus gaps, which the
    /// YaguDialog-scoped suppression (PreparingToShowModal / HasOpenOwnedWindow) does not cover — otherwise a
    /// windowed suggestion popup (its own top-level window) can float above it.
    /// </summary>
    private IDisposable ParkInputSuggestionsForModal()
    {
        long previous = _suppressQuerySuggestionsUntilTick;
        _suppressQuerySuggestionsUntilTick = long.MaxValue;
        CollapseInputSuggestionDropdowns();
        _ownedModalWindowDepth++;
        return new InputSuggestionSuppressionScope(this, previous);
    }

    private sealed class InputSuggestionSuppressionScope(MainWindow owner, long previousSuppressionTick) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._suppressQuerySuggestionsUntilTick = Math.Max(previousSuppressionTick, Environment.TickCount64 + 1000);
            owner.HideQuerySuggestions(owner.QueryBox);
            owner._ownedModalWindowDepth = Math.Max(0, owner._ownedModalWindowDepth - 1);
        }
    }
}
