using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Logging;
using System.Collections.ObjectModel;
using System.Globalization;
namespace Yagu;

/// <summary>
/// Search input, query suggestions, directory entry, and live search controls.
/// </summary>
public sealed partial class MainWindow
{
    // Missing/stale index warnings are actionable but should not nag before every search. A root that
    // the user explicitly chose to scan live is suppressed only for this process; rebuilding/adding or
    // restarting naturally gives the readiness preflight another opportunity to report current state.
    private readonly HashSet<string> _contentIndexReadinessWarningsAcknowledged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cloudScanWarningsAcknowledged = new(StringComparer.OrdinalIgnoreCase);

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_autoScrollEnabled || ViewModel.ResultRows.Count == 0) return;
        if (_resultsListTopRestoreInProgress) return;
        if (_resultsListShowMoreRestoreInProgress) return;
        if (_resultsListWasAtTop) return;
        ScrollResultsListToBottom();
    }

    private void UpdateSparkline()
    {
        var samples = _diskUtilService.GetSamples();

        // Update gauge bar and label even with few samples
        if (samples.Count > 0)
        {
            var latest = samples[^1];
            double gaugeContainerWidth = DiskGaugeBar.Parent is FrameworkElement parent ? parent.ActualWidth : 0;
            if (gaugeContainerWidth > 0)
                DiskGaugeBar.Width = latest.UtilizationPct / 100.0 * gaugeContainerWidth;

            DiskGaugeLabel.Text = $"{latest.MBPerSec:N0} MB/s \u00b7 {latest.UtilizationPct:N0}%";
        }
        else
        {
            DiskGaugeBar.Width = 0;
            DiskGaugeLabel.Text = string.Empty;
        }

        // Sparkline needs at least 2 points
        if (samples.Count < 2)
        {
            ThroughputSparkline.Points.Clear();
            return;
        }

        double width = ThroughputSparkline.ActualWidth;
        double height = ThroughputSparkline.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Plot disk MB/s
        double max = 1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].MBPerSec > max) max = samples[i].MBPerSec;
        }

        var pts = ThroughputSparkline.Points;
        pts.Clear();
        double xStep = width / (samples.Count - 1);
        for (int i = 0; i < samples.Count; i++)
        {
            double x = i * xStep;
            double y = height - (samples[i].MBPerSec / max * (height - 2)) - 1;
            pts.Add(new Windows.Foundation.Point(x, y));
        }
    }

    private void SetAutoScrollEnabled(bool enabled)
    {
        _autoScrollEnabled = enabled;
        if (AutoScrollResultsCheckBox.IsChecked != enabled)
            AutoScrollResultsCheckBox.IsChecked = enabled;
    }

    private void OnAutoScrollResultsChanged(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = AutoScrollResultsCheckBox.IsChecked == true;
        if (_autoScrollEnabled && ViewModel.ResultRows.Count > 0)
            ScrollResultsListToBottom();
    }

    private void OnFilterBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.FontStyle = Windows.UI.Text.FontStyle.Normal;
    }

    private void OnFilterBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (IsFilterExampleText(tb))
            tb.Text = string.Empty;

        tb.PlaceholderText = string.Empty;
    }

    private void OnFilterBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (string.IsNullOrEmpty(tb.Text))
        {
            if (ReferenceEquals(tb, IncludeFilterBox))
                tb.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
            else
                tb.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
        }
    }

    private bool IsFilterExampleText(TextBox textBox)
    {
        string text = textBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return false;

        if (ReferenceEquals(textBox, IncludeFilterBox))
            return string.Equals(text, ViewModel.IncludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase);

        return string.Equals(text, ViewModel.ExcludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnSearchCancelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsTranslatingSemanticQuery)
        {
            // The AI is mid-translation — clicking Cancel aborts the model inference, not a file scan.
            ViewModel.CancelSemanticTranslation();
            return;
        }
        if (ViewModel.IsSearching)
        {
            await ViewModel.CancelAsync();
            return;
        }
        if (ViewModel.IsPreparingSearch)
        {
            // Still in the pre-scan gate phase (no file scan to cancel yet) — abort the preparation so
            // the pending run never starts.
            ViewModel.CancelSearchPreparation();
            return;
        }
        await StartSearchFromUiAsync();
    }

    // SplitButton primary action — only visible while idle, so it always starts a search.
    private async void OnSearchSplitButtonClick(SplitButton sender, SplitButtonClickEventArgs args) =>
        await StartSearchFromUiAsync();

    private async Task StartSearchFromUiAsync()
    {
        HideQuerySuggestions();
        if (!await ClearPreviewPanelForNewSearchAsync()) return;
        CollapseAdvancedOptionsForSearch();
        // The HDD and excluded-extension warnings run as a gate inside SubmitSearchAsync, AFTER any
        // semantic translation, so they evaluate the directory/target the AI model actually resolved
        // (e.g. a query resolving to "C:\" — an SSD — no longer shows a spurious HDD warning first).
        await SubmitSearchWithSlowModelWatchAsync();
    }

    // Copies just the answer (no expression) from the inline calculator banner.
    private void OnCopyInlineCalculatorResult(object sender, RoutedEventArgs e)
    {
        var value = ViewModel.InlineCalculatorCopyValue;
        if (!string.IsNullOrEmpty(value))
            SetClipboardText(value, "calculator result");
    }

    // "Find code annotations" quick action: loads the canonical TODO/FIXME regex into the box (in
    // Traditional regex mode) and runs the search, so outstanding annotations surface in one click.
    private async void OnFindCodeAnnotations(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyCodeAnnotationPreset();
        await StartSearchFromUiAsync();
    }

    // Developer "quick search" buttons on the Advanced Options ▸ Quick searches tab. Each button's Tag
    // is a QuickSearchPresets key; loading the matching preset's regex (Traditional mode) and running the
    // search surfaces the target in one click.
    private async void OnQuickSearch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key
            && Yagu.Helpers.QuickSearchPresets.Find(key) is { } preset)
        {
            ViewModel.ApplyQuickSearchPreset(preset);
            await StartSearchFromUiAsync();
        }
    }

    private bool _querySubmitInProgress;

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var submittedQuery = (args.ChosenSuggestion as Yagu.Models.HistorySuggestion)?.Value;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = args.QueryText;

        await SubmitQueryAsync(sender, submittedQuery);
    }

    private async Task SubmitQueryAsync(AutoSuggestBox sender, string? submittedQuery = null)
    {
        // KeyDown is registered with handledEventsToo because the inner TextBox can consume Enter.
        // If AutoSuggestBox did raise QuerySubmitted first, both routes converge here and this guard
        // prevents one key press from launching two searches.
        if (_querySubmitInProgress)
            return;

        _querySubmitInProgress = true;
        try
        {
            if (string.IsNullOrEmpty(submittedQuery))
                submittedQuery = sender.Text;

            bool textApplied = false;
            if (!string.IsNullOrEmpty(submittedQuery))
            {
                // Show the chosen text in the box as the VERY FIRST thing, before any search work. Setting
                // sender.Text directly makes a clicked history item appear immediately instead of only once
                // the UI thread next yields — the delay the user sees in Semantic mode, where translation
                // briefly occupies the thread before the bound text repaints.
                if (sender.Text != submittedQuery)
                    sender.Text = submittedQuery;
                ViewModel.Query = submittedQuery;
                textApplied = true;
            }

            HideQuerySuggestions(sender);

            // Let the box paint the chosen text before the (possibly slow) search pipeline begins.
            if (textApplied)
                await YieldUntilRenderedAsync();

            if (!await ClearPreviewPanelForNewSearchAsync()) return;
            CollapseAdvancedOptionsForSearch();
            await SubmitSearchWithSlowModelWatchAsync();
        }
        finally
        {
            _querySubmitInProgress = false;
        }
    }

    /// <summary>
    /// Completes after the UI thread has processed its pending layout/render work, so a visual change
    /// made immediately before the call (e.g. the query text set from a chosen history item) paints
    /// before slower follow-up work (such as semantic translation) starts occupying the thread. A
    /// Low-priority dispatcher callback runs after the in-flight frame, giving the text time to show.
    /// </summary>
    private Task YieldUntilRenderedAsync()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => rendered.SetResult()))
            rendered.SetResult();
        return rendered.Task;
    }

    /// <summary>
    /// Combined pre-search warning gate, run by <see cref="MainViewModel.SubmitSearchAsync"/> AFTER
    /// any semantic translation. Running both notices here (rather than before the search) means a
    /// semantic search evaluates them against the directory/target the AI model resolved — so a query
    /// that resolves to an SSD no longer shows a spurious HDD warning before the model has even run.
    /// Returns false to abort the search. The HDD check runs first, then the excluded-extension check.
    /// </summary>
    private async Task<bool> RunPreSearchWarningGatesAsync()
    {
        if (ViewModel.IsSearchPreparationCancellationRequested) return false;
        if (!await CheckCloudDriveScanAndWarnAsync()) return false;
        if (ViewModel.IsSearchPreparationCancellationRequested) return false;
        if (!await CheckEverythingIndexCoverageAndWarnAsync()) return false;
        if (ViewModel.IsSearchPreparationCancellationRequested) return false;
        if (!await CheckContentIndexReadinessAndWarnAsync()) return false;
        if (ViewModel.IsSearchPreparationCancellationRequested) return false;
        if (!await CheckIndexWarmupAndWarnAsync()) return false;
        if (!await CheckHddAndWarnAsync())
        {
            ViewModel.ResumeContentIndexWarmupAfterSearch();
            return false;
        }
        if (!await CheckExcludedExtensionAndWarnAsync())
        {
            ViewModel.ResumeContentIndexWarmupAfterSearch();
            return false;
        }
        if (!await CheckMatchEverythingPatternAndWarnAsync())
        {
            ViewModel.ResumeContentIndexWarmupAfterSearch();
            return false;
        }
        return true;
    }

    /// <summary>
    /// Warns before the first scan of each cloud-backed drive in this app session. Even when Yagu skips
    /// known online-only placeholders, provider metadata and ordinary-looking files may hydrate on access;
    /// a broad scan can therefore consume bandwidth and local disk unexpectedly.
    /// </summary>
    private async Task<bool> CheckCloudDriveScanAndWarnAsync()
    {
        string[] cloudRoots = ViewModel.ResolveTargetRoots()
            .Select(path => Path.GetPathRoot(path))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => !_cloudScanWarningsAcknowledged.Contains(root))
            .Where(root =>
            {
                try { return DriveEnumerator.IsLikelyCloudDrive(new DriveInfo(root)); }
                catch { return false; }
            })
            .ToArray();
        if (cloudRoots.Length == 0)
            return true;
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return false;

        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = cloudRoots.Length == 1
                ? "This search includes a cloud-backed drive:"
                : "This search includes cloud-backed drives:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, cloudRoots),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Scanning can cause the cloud provider to download files or metadata on demand. "
                 + "This may use significant network bandwidth and local disk space, especially for a broad search.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Yagu will continue to skip placeholders it can identify as online-only unless “Search online-only cloud files” is enabled, but the provider ultimately controls hydration.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });

        YaguDialogResult result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Cloud drive scan may download files",
                TitleGlyph = "\uE753",
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content = panel,
                PrimaryButtonText = "Search cloud drive",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 640,
                Height = 420,
                MaxContentHeight = 320,
            });
        if (result != YaguDialogResult.Primary)
            return false;

        foreach (string root in cloudRoots)
            _cloudScanWarningsAcknowledged.Add(root);
        return true;
    }

    /// <summary>
    /// Warns before an index-enabled search would silently fall back to a potentially very expensive
    /// live scan because a target has no usable index or its change-journal checkpoint is no longer
    /// provably fresh. Query-shape bypasses are omitted because rebuilding cannot help them. The check
    /// uses manifest/file-id metadata plus the same bounded USN replay as the mapped worker; no index is
    /// opened, mapped, built, or mutated unless the user selects an explicit action.
    /// </summary>
    private async Task<bool> CheckContentIndexReadinessAndWarnAsync()
    {
        if (!ViewModel.Settings.EnableContentIndex || !ViewModel.UseContentIndex)
            return true;

        IReadOnlyList<string> roots = ViewModel.ResolveTargetRoots();
        if (roots.Count == 0)
            return true;

        string query = ViewModel.Query;
        bool caseSensitive = ViewModel.CaseSensitive;
        bool useRegex = ViewModel.UseRegex;
        bool exactMatch = ViewModel.ExactMatch;
        bool multiline = ViewModel.Multiline;
        bool multilineDotAll = ViewModel.MultilineDotAll;
        bool skipBinary = ViewModel.SkipBinary;
        string storageDir = ViewModel.Settings.IndexStorageDirectory;
        string[] registeredRoots = ViewModel.Settings.IndexedRoots.ToArray();
        string[] acknowledgedWarnings = _contentIndexReadinessWarningsAcknowledged.ToArray();
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(ViewModel.Settings.IndexRetainedGenerationCount);
        int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(ViewModel.Settings.IndexMaxJournalCatchupRecords);

        ContentIndexReadinessIssue[] issues;
        try
        {
            issues = await Task.Run(() =>
            {
                var paths = DefaultContentIndexPathProvider.Create(storageDir);
                ContentIndexFreshnessEvaluator.JournalReader reader =
                    ContentIndexFreshnessEvaluator.CreateReader(
                        maxCatchupRecords,
                        TimeSpan.FromSeconds(AppSettings.NormalizeFileIoTimeoutSeconds(ViewModel.Settings.FileIoTimeoutSeconds)));
                var found = new List<ContentIndexReadinessIssue>();
                foreach (string root in roots)
                {
                    string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
                    string? registeredCover = IndexedRootsPolicy.FindBestCoveringRoot(registeredRoots, normalizedRoot);
                    string warningRoot = IndexScopeIdentity.NormalizePath(registeredCover ?? normalizedRoot);
                    // Once Search live acknowledged this physical root's missing/stale state, avoid even
                    // replaying its large change journal on later searches in the same process.
                    if (acknowledgedWarnings.Contains($"{ContentIndexReadinessIssueKind.Missing}|{warningRoot}", StringComparer.OrdinalIgnoreCase)
                        || acknowledgedWarnings.Contains($"{ContentIndexReadinessIssueKind.RefreshRequired}|{warningRoot}", StringComparer.OrdinalIgnoreCase))
                        continue;

                    var options = new SearchOptions
                    {
                        Directory = root,
                        Query = query,
                        CaseSensitive = caseSensitive,
                        UseRegex = useRegex,
                        ExactMatch = exactMatch,
                        Multiline = multiline,
                        MultilineDotAll = multilineDotAll,
                        SkipBinary = skipBinary,
                        UseContentIndex = true,
                    };
                    ContentIndexReadinessIssue? issue = ContentIndexReadinessChecker.CheckRoot(
                        paths, root, registeredRoots, options, retained, reader);
                    if (issue is not null
                        && !found.Any(existing => string.Equals(existing.WarningKey, issue.WarningKey, StringComparison.OrdinalIgnoreCase)))
                        found.Add(issue);
                }
                return found.ToArray();
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Notification preflight fails open. The authoritative search gate still fails safe.
            YaguLog.For("ContentIndex").LogDebug(ex, "Content-index readiness UI preflight failed; continuing search.");
            return true;
        }

        issues = issues
            .Where(issue => !_contentIndexReadinessWarningsAcknowledged.Contains(issue.WarningKey))
            .ToArray();
        if (issues.Length == 0)
            return true;
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return false;

        YaguLog.For("ContentIndex").LogInformation(
            "Pre-search readiness warning: {IssueCount} actionable index issue(s): {Issues}",
            issues.Length,
            string.Join("; ", issues.Select(issue => $"{issue.SearchRoot}: {issue.Reason}")));

        var panel = new StackPanel { Spacing = 12, MinWidth = 500 };
        panel.Children.Add(new TextBlock
        {
            Text = issues.Length == 1
                ? "This search includes a drive or folder whose content index needs attention."
                : "This search includes drives or folders whose content indexes need attention.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Without a usable, fresh index Yagu must scan that root live, which can take much longer. "
                 + "For a folder that is not indexed yet, you can wait behind the blocking progress overlay, "
                 + "or let indexing run in the background while this search continues live. You can also search "
                 + "live without indexing, or cancel.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });

        ContentIndexReadinessIssue? requestedAction = null;
        string? requestedActionKind = null;
        YaguDialog? readinessDialog = null;
        foreach (ContentIndexReadinessIssue issue in issues)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { Spacing = 3 };
            details.Children.Add(new TextBlock
            {
                Text = issue.SearchRoot,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            details.Children.Add(new TextBlock
            {
                Text = DescribeContentIndexReadinessIssue(issue),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.8,
            });
            row.Children.Add(details);

            var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            void AddAction(string label, string kind)
            {
                var action = new Button
                {
                    Content = label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 128,
                };
                action.Click += (_, _) =>
                {
                    requestedAction = issue;
                    requestedActionKind = kind;
                    readinessDialog?.AcceptClose();
                };
                actions.Children.Add(action);
            }

            if (issue.Kind == ContentIndexReadinessIssueKind.Missing)
            {
                if (issue.CanRebuild)
                {
                    AddAction("Build & wait", "rebuild");
                    AddAction("Build & search now", "build-search");
                }
                else
                {
                    AddAction("Index & wait", "add-wait");
                    AddAction("Index & search now", "add-search");
                }
            }
            else if (!issue.Repairable)
            {
                // The index remains visible in Settings as needing attention, but this volume cannot
                // provide a supported change journal, so neither update nor rebuild can restore freshness.
            }
            else if (CanAttemptIncrementalIndexRefresh(issue))
            {
                AddAction(IsJournalCatchupLimitIssue(issue) ? "Increase limit & update" : "Update index", "incremental");
                AddAction("Rebuild index", "rebuild");
            }
            else
            {
                if (issue.CanRebuild)
                {
                    AddAction("Rebuild index", "rebuild");
                }
                else
                {
                    AddAction("Index & wait", "add-wait");
                    AddAction("Index & search now", "add-search");
                }
            }
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            panel.Children.Add(new Border
            {
                Background = TryGetPreviewBrushResource("CardBackgroundFillColorSecondaryBrush"),
                BorderBrush = TryGetPreviewBrushResource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = row,
            });
        }

        YaguDialogResult result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Content index needs attention",
                TitleGlyph = "\uE7BA",
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content = panel,
                PrimaryButtonText = "Search live",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 680,
                Height = Math.Min(620, 330 + (issues.Length * 100)),
                MaxContentHeight = 500,
            },
            dialog => readinessDialog = dialog);

        if (requestedAction is { } actionIssue)
        {
            if (requestedActionKind == "incremental")
            {
                int? increasedLimit = IsJournalCatchupLimitIssue(actionIssue)
                    ? ComputeRaisedJournalCatchupLimit(ViewModel.Settings.IndexMaxJournalCatchupRecords)
                    : null;
                await ViewModel.RefreshCurrentIndexIncrementallyAsync(actionIssue.IndexRoot, increasedLimit);
                foreach (ContentIndexReadinessIssue issue in issues)
                    _contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);
                return true; // continue the pending search; live scan remains authoritative if repair did not complete
            }
            else if (requestedActionKind == "rebuild" && actionIssue.CanRebuild)
            {
                await ViewModel.RebuildCurrentIndexBlockingAsync(new[] { actionIssue.IndexRoot });
                foreach (ContentIndexReadinessIssue issue in issues)
                    _contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);
                return true;
            }
            else if (requestedActionKind == "add-wait")
            {
                if (!await ConfirmLargeFolderIfNeededAsync(actionIssue.SearchRoot))
                    return false;
                await ViewModel.AddFolderToIndexAndBuildBlockingAsync(actionIssue.SearchRoot);
                foreach (ContentIndexReadinessIssue issue in issues)
                    _contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);
                return true;
            }
            else if (requestedActionKind == "add-search" && await ConfirmLargeFolderIfNeededAsync(actionIssue.SearchRoot))
            {
                await ViewModel.AddFolderToIndexAndBuildAsync(actionIssue.SearchRoot);
                foreach (ContentIndexReadinessIssue issue in issues)
                    _contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);
                return true; // the background build runs while this search uses the authoritative live path
            }
            else if (requestedActionKind == "build-search" && actionIssue.CanRebuild)
            {
                ViewModel.RebuildRegisteredIndexNow(actionIssue.IndexRoot);
                foreach (ContentIndexReadinessIssue issue in issues)
                    _contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);
                return true; // the registered root builds in the background while this search runs live
            }
            return false;
        }

        if (result != YaguDialogResult.Primary)
            return false;

        foreach (ContentIndexReadinessIssue issue in issues)
            _contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);
        return true;
    }

    private static string DescribeContentIndexReadinessIssue(ContentIndexReadinessIssue issue)
    {
        if (issue.Kind == ContentIndexReadinessIssueKind.Missing)
            return "No usable content index exists for this root. It will be scanned live.";
        if (issue.Reason.Contains("Incomplete", StringComparison.OrdinalIgnoreCase))
            return "The bounded change-journal replay reached its record limit. This can happen on a busy drive soon after a rebuild. "
                 + "Increase the limit and try a safe incremental update first; if continuity still cannot be proven, Yagu keeps the existing index unchanged.";
        if (issue.Reason.Contains("UnsupportedChangeJournal", StringComparison.OrdinalIgnoreCase))
            return "This volume does not provide a supported change journal, so Yagu cannot freshness-validate this index. Searches safely scan this folder live; rebuilding would not help.";
        if (issue.Reason.Contains("GapDetected", StringComparison.OrdinalIgnoreCase))
            return "The drive change journal no longer covers the index checkpoint. Rebuild to restore freshness.";
        if (issue.Reason.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase))
            return "The saved index checkpoint is ahead of the drive's live change journal, usually because the journal was reset or recreated. Rebuild to establish a valid checkpoint.";
        if (issue.Reason.Contains("JournalIdChanged", StringComparison.OrdinalIgnoreCase))
            return "The drive change journal was reset after this index was built. Rebuild to establish a new checkpoint.";
        if (issue.Reason.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
            || issue.Reason.Contains("JournalUnavailable", StringComparison.OrdinalIgnoreCase))
            return "The drive change journal could not be read, so index freshness cannot be proven. Rebuild or retry when the drive is available.";
        if (issue.Reason.Contains("UnknownRecordVersion", StringComparison.OrdinalIgnoreCase))
            return "The drive returned an unsupported change-journal record version. Rebuild the index; Yagu will scan live if freshness remains unprovable.";
        if (issue.Reason.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase))
            return "This index layer has no usable change-journal checkpoint. Rebuild to establish a fresh checkpoint.";
        if (issue.Reason.Contains("freshness inputs unreadable", StringComparison.OrdinalIgnoreCase))
            return "The index layer's freshness metadata could not be read. Rebuild to replace the unreadable layer safely.";
        if (issue.Reason.Contains("Error", StringComparison.OrdinalIgnoreCase))
            return "The change journal returned an error, so index freshness cannot be proven. Rebuild or search live.";
        return "Index freshness cannot be proven. Rebuild before searching for normal accelerated performance.";
    }

    private static bool IsJournalCatchupLimitIssue(ContentIndexReadinessIssue issue)
        => issue.Reason.Contains("Incomplete", StringComparison.OrdinalIgnoreCase);

    private static bool CanAttemptIncrementalIndexRefresh(ContentIndexReadinessIssue issue)
    {
        if (!issue.Registered || issue.Kind != ContentIndexReadinessIssueKind.RefreshRequired)
            return false;
        string reason = issue.Reason;
        if (reason.Contains("GapDetected", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("JournalIdChanged", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("UnknownRecordVersion", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("freshness inputs unreadable", StringComparison.OrdinalIgnoreCase))
            return false;
        return reason.Contains("Incomplete", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeRaisedJournalCatchupLimit(int current)
    {
        long normalized = AppSettings.NormalizeIndexMaxJournalCatchupRecords(current);
        long raised = Math.Max(normalized + AppSettings.DefaultIndexMaxJournalCatchupRecords, normalized * 4L);
        return (int)Math.Min(AppSettings.MaximumIndexMaxJournalCatchupRecords, raised);
    }

    /// <summary>
    /// When the current Traditional-mode query reads like a natural-language request (e.g. "files on C
    /// containing the word test"), offers a one-time switch to AI (Semantic) search. Accepting switches
    /// the search bar to Semantic for this run; either choice can be made permanent via "Don't remind me
    /// again". A no-op when Semantic search isn't usable, the query is literal, or the prompt was dismissed.
    /// </summary>
    private async Task MaybeOfferSemanticSuggestionAsync()
    {
        if (!ViewModel.ShouldOfferSemanticSuggestion(ViewModel.Query))
            return;
        // Don't stack on top of another owned modal.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var (content, dontRemind) = BuildSemanticSuggestionContent();
        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "This looks like an AI search",
                TitleGlyph = "\uF4A5",
                Content = content,
                PrimaryButtonText = "Switch to AI search",
                CloseButtonText = "Keep Traditional",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 560,
                Height = 340,
            });

        await ViewModel.ApplySemanticSuggestionAsync(
            switchToSemantic: result == YaguDialogResult.Primary,
            dontRemind: dontRemind.IsChecked == true);
    }

    /// <summary>Body of the semantic-suggestion prompt: an explanation plus a "Don't remind me again"
    /// checkbox (returned so the caller can read its state after the dialog closes).</summary>
    private static (FrameworkElement Content, CheckBox DontRemind) BuildSemanticSuggestionContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Your search looks like a natural-language question. AI (Semantic) search can interpret "
                 + "phrases like \u201cfiles on C: containing the word test\u201d and turn them into the right "
                 + "filters automatically.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Switch to AI search to run it that way, or keep Traditional search to match your text "
                 + "literally.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        });

        var dontRemind = new CheckBox
        {
            Content = "Don't remind me again",
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(dontRemind);
        return (panel, dontRemind);
    }

    /// <summary>
    /// When the current Traditional-mode query contains a literal "\n" escape while Multiline search is
    /// off, offers a one-time switch to Multiline (which also enables Regex) so the escape matches a real
    /// line break. Accepting switches Multiline on for this run; either choice can be made permanent via
    /// "Don't warn me again". A no-op when Multiline is already on, the query has no "\n", the search is
    /// Semantic, or the prompt was dismissed.
    /// </summary>
    private async Task MaybeOfferMultilineSuggestionAsync()
    {
        if (!ViewModel.ShouldOfferMultilineSuggestion(ViewModel.Query))
            return;
        // Don't stack on top of another owned modal.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var (content, dontWarn) = BuildMultilineSuggestionContent();
        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "This looks like a multiline search",
                TitleGlyph = "\uE8A1",
                Content = content,
                PrimaryButtonText = "Switch to Multiline",
                CloseButtonText = "Search as-is",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 560,
                Height = 340,
            });

        await ViewModel.ApplyMultilineSuggestionAsync(
            switchToMultiline: result == YaguDialogResult.Primary,
            dontRemind: dontWarn.IsChecked == true);
    }

    /// <summary>Body of the multiline-suggestion prompt: an explanation plus a "Don't warn me again"
    /// checkbox (returned so the caller can read its state after the dialog closes).</summary>
    private static (FrameworkElement Content, CheckBox DontWarn) BuildMultilineSuggestionContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Your search contains a \u201c\\n\u201d escape, which only matches a real line break when "
                 + "Multiline search is on. Multiline also turns on Regex so the escape is interpreted.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Switch to Multiline to match across lines, or search as-is to match the two characters "
                 + "\u201c\\n\u201d literally.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        });

        var dontWarn = new CheckBox
        {
            Content = "Don't warn me again",
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(dontWarn);
        return (panel, dontWarn);
    }

    private void OnSelectTraditionalMode(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSemanticQueryMode = false;
    }

    private async void OnSelectSemanticMode(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.SemanticSearchAvailable)
            return;

        // Already downloaded a model before — just switch.
        if (ViewModel.IsSemanticModelDownloaded)
        {
            ViewModel.IsSemanticQueryMode = true;
            return;
        }

        // First time: ask the user to download a local model. Semantic search can't run without one.
        var chosenAlias = await ShowSemanticModelDownloadDialogAsync();
        if (chosenAlias is not null)
        {
            ViewModel.IsSemanticQueryMode = true;
        }
        else
        {
            // Declined or failed — stay in Traditional mode and re-sync the menu highlight.
            ViewModel.IsSemanticQueryMode = false;
            UpdateSearchModeMenuHighlight();
        }
    }

    private void OnSearchModeFlyoutOpening(object? sender, object e) => UpdateSearchModeMenuHighlight();

    /// <summary>
    /// Marks the active query mode with a subtle highlight background instead of a radio bullet.
    /// </summary>
    private void UpdateSearchModeMenuHighlight()
    {
        Microsoft.UI.Xaml.Media.Brush? highlight = null;
        if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var res))
            highlight = res as Microsoft.UI.Xaml.Media.Brush;

        var semantic = ViewModel.IsSemanticQueryMode;
        SemanticModeItem.Background = semantic ? highlight : null;
        TraditionalModeItem.Background = semantic ? null : highlight;
    }

    /// <summary>
    /// Shows the borderless first-run model-download modal. Returns the chosen model alias on a
    /// successful download (empty string means "use the recommended/auto model"), or null when the
    /// user declined or the download failed.
    /// </summary>
    private Task<string?> ShowSemanticModelDownloadDialogAsync() =>
        SemanticModelDownloadDialog.ShowAsync(
            _hwnd,
            RootGrid.ActualTheme,
            (progress, token) => ViewModel.GetSemanticModelOptionsAsync(progress, token),
            (alias, progress, token) => ViewModel.PrepareSemanticModelAsync(alias, progress, token),
            ViewModel.SemanticModelAlias);

    private void CollapseAdvancedOptionsForSearch()
    {
        AdvancedOptionsFlyout?.Hide();
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SubmitQueryAsync(sender as AutoSuggestBox ?? QueryBox);
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.IsTranslatingSemanticQuery)
        {
            e.Handled = true;
            ViewModel.CancelSemanticTranslation();
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.IsSearching)
        {
            e.Handled = true;
            await ViewModel.CancelAsync();
        }
        // Down arrow opens the search history dropdown.
        else if (e.Key == VirtualKey.Down && !QueryBox.IsSuggestionListOpen
                 && !AreQuerySuggestionsSuppressed()
                 && ActiveQueryHistory().Count > 0)
        {
            ApplyQuerySuggestions(QueryBox, open: true);
        }
    }

    private void HideQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        _querySuggestionsUserOpened = false;
        _querySuggestionsDetached = true;
        _hideSuggestionsTick = Environment.TickCount64;
        target.IsSuggestionListOpen = false;
        target.ItemsSource = null;
        target.IsSuggestionListOpen = false;
        // The AutoSuggestBox sometimes re-opens its popup after QuerySubmitted.
        // Fight back with a deferred close.
        DispatcherQueue.TryEnqueue(() =>
        {
            target.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => target.IsSuggestionListOpen = false);
        });
    }

    private void RestoreQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        if (AreQuerySuggestionsSuppressed())
        {
            target.IsSuggestionListOpen = false;
            return;
        }

        ApplyQuerySuggestions(target, open: false);
    }

    private void ApplyQuerySuggestions(AutoSuggestBox target, bool open)
    {
        if (AreQuerySuggestionsSuppressed())
        {
            target.IsSuggestionListOpen = false;
            return;
        }

        if (_querySuggestionsDetached)
        {
            if (Environment.TickCount64 - _hideSuggestionsTick < 400)
            {
                target.IsSuggestionListOpen = false;
                return;
            }

            _querySuggestionsDetached = false;
        }

        if (open)
            _querySuggestionsUserOpened = true;

        var suggestions = BuildQuerySuggestions(target.Text);
        target.ItemsSource = suggestions;
        target.IsSuggestionListOpen = open && suggestions.Count > 0;
    }

    private List<Yagu.Models.HistorySuggestion> BuildQuerySuggestions(string? queryText)
        => ViewModel.BuildQuerySuggestionItems(queryText);

    /// <summary>The autocomplete history that backs the query box for the active search mode:
    /// the Semantic natural-language history in Semantic mode, otherwise the Traditional history.</summary>
    private ObservableCollection<string> ActiveQueryHistory()
        => ViewModel.IsSemanticQueryMode ? ViewModel.SemanticSearchHistory : ViewModel.SearchHistory;

    private bool AreQuerySuggestionsSuppressed()
        => Environment.TickCount64 < _suppressQuerySuggestionsUntilTick
           || YaguDialog.HasOpenOwnedWindow(_hwnd);

    private void SuppressQuerySuggestionsFor(int milliseconds, AutoSuggestBox? box = null)
    {
        long until = Environment.TickCount64 + milliseconds;
        if (until > _suppressQuerySuggestionsUntilTick)
            _suppressQuerySuggestionsUntilTick = until;

        HideQuerySuggestions(box);
    }

    /// <summary>
    /// Invoked just before one of our owned modal dialogs is shown. Closes the directory and query
    /// history dropdowns so a suggestion popup (each hosted in its own top-level window) can't sit
    /// above the dialog, and so neither box pops its list open as keyboard focus moves to the dialog.
    /// </summary>
    private void OnModalDialogPreparingToShow(IntPtr ownerHwnd)
    {
        // React only to dialogs owned by this window. Before our HWND is cached (very early startup
        // dialogs) fall through and collapse anyway — there is a single MainWindow and these are the
        // only history dropdowns it owns.
        if (_hwnd != IntPtr.Zero && ownerHwnd != _hwnd)
            return;

        CollapseInputSuggestionDropdowns();
        // Keep the Advanced Options drawer open when a modal is raised from within it (e.g. the
        // .gitignore-vs-Include-filter prompt) — the modal deactivating the window must not dismiss it.
        SuppressAdvancedOptionsFlyoutDismissForModal();
    }

    /// <summary>
    /// Force the query/directory suggestion list shut whenever it tries to open while one of our
    /// owned modal dialogs is up. Each suggestion list is a windowed popup that would otherwise float
    /// above the modal; the timed suppression in <see cref="CollapseInputSuggestionDropdowns"/> only
    /// covers the moment the dialog opens, while a dialog can stay open indefinitely and the box can
    /// re-open its list on its own. This catches every re-open for the dialog's whole lifetime.
    /// </summary>
    private void OnInputSuggestionListOpenChanged(DependencyObject sender, DependencyProperty dp)
    {
        // Force the list shut while any owned modal is up. YaguDialog modals are covered by
        // HasOpenOwnedWindow; non-YaguDialog owned windows (the AI-model qualification / font-contrast
        // dialogs) hold the list shut by parking the suppression tick, so honor that too — otherwise
        // the windowed suggestion popup floats above the modal.
        if (sender is not AutoSuggestBox box)
            return;

        if (box.IsSuggestionListOpen
            && (YaguDialog.HasOpenOwnedWindow(_hwnd) || Environment.TickCount64 < _suppressQuerySuggestionsUntilTick))
        {
            box.IsSuggestionListOpen = false;
            return;
        }

        // Narrow each history dropdown to the right edge of its first overlaid command target instead
        // of letting the popup run beneath every trailing button. Run after popup layout (Low priority)
        // so the framework's own full-AutoSuggestBox-width pinning has already run and ours wins.
        if (box.IsSuggestionListOpen)
        {
            if (ReferenceEquals(box, QueryBox))
            {
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    ConstrainQuerySuggestionListWidth);
            }
            else if (ReferenceEquals(box, DirectoryBox))
            {
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    ConstrainDirectorySuggestionListWidth);
            }
        }
    }

    // The directory history dropdown right edge should line up with the RIGHT edge of the pin-star
    // button. The index and Browse controls remain outside the popup, matching the directory bar's
    // visual command grouping while leaving the pin target included in the dropdown width.
    private double _directorySuggestionTargetWidth;

    private void ConstrainDirectorySuggestionListWidth()
    {
        var xamlRoot = DirectoryBox.XamlRoot;
        if (xamlRoot is null || !DirectoryBox.IsSuggestionListOpen
            || PinStartupDirectoryButton.ActualWidth <= 0)
        {
            return;
        }

        double pinRight = PinStartupDirectoryButton
            .TransformToVisual(DirectoryBox)
            .TransformPoint(new Windows.Foundation.Point(PinStartupDirectoryButton.ActualWidth, 0)).X;
        if (pinRight <= 40 || pinRight > DirectoryBox.ActualWidth + 0.5)
            return;
        _directorySuggestionTargetWidth = pinRight;

        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is not FrameworkElement card || !IsDescendantOf(popup, DirectoryBox))
                continue;

            ApplyDirectorySuggestionCardWidth(card);
            card.SizeChanged -= OnDirectorySuggestionCardSizeChanged;
            card.SizeChanged += OnDirectorySuggestionCardSizeChanged;
            break;
        }
    }

    private void ApplyDirectorySuggestionCardWidth(FrameworkElement card)
    {
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.MinWidth = 0;
        card.MaxWidth = _directorySuggestionTargetWidth;
    }

    private void OnDirectorySuggestionCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement card
            && _directorySuggestionTargetWidth > 0
            && card.ActualWidth > _directorySuggestionTargetWidth + 0.5)
        {
            ApplyDirectorySuggestionCardWidth(card);
        }
    }

    // The query history dropdown right edge should stop at the "Match case" toggle, not extend under
    // the overlaid Case/Regex/Multiline/Exact strip. The framework sizes the suggestion popup to the
    // full AutoSuggestBox width, so we clamp the popup card's MaxWidth to the toggle strip's left edge.
    private double _querySuggestionTargetWidth;

    private void ConstrainQuerySuggestionListWidth()
    {
        var xamlRoot = QueryBox.XamlRoot;
        if (xamlRoot is null || !QueryBox.IsSuggestionListOpen)
            return;

        // Only relevant when the toggle strip is overlaid (Traditional mode). With no toggles shown
        // (Semantic mode) the full-width dropdown is fine, so leave it alone.
        if (InlineSearchToggles.Visibility != Visibility.Visible || CaseSensitiveToggle.ActualWidth <= 0)
            return;

        double aaLeft = CaseSensitiveToggle
            .TransformToVisual(QueryBox)
            .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        if (aaLeft <= 40) // not laid out yet / implausibly small — skip rather than clip to nothing
            return;
        _querySuggestionTargetWidth = aaLeft;

        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is not FrameworkElement card || !IsDescendantOf(popup, QueryBox))
                continue;

            ApplyQuerySuggestionCardWidth(card);
            // Re-clamp if the framework widens the card back on a later layout pass.
            card.SizeChanged -= OnQuerySuggestionCardSizeChanged;
            card.SizeChanged += OnQuerySuggestionCardSizeChanged;
            break;
        }
    }

    private void ApplyQuerySuggestionCardWidth(FrameworkElement card)
    {
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.MinWidth = 0;
        card.MaxWidth = _querySuggestionTargetWidth;
    }

    private void OnQuerySuggestionCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement card
            && _querySuggestionTargetWidth > 0
            && card.ActualWidth > _querySuggestionTargetWidth + 0.5)
        {
            ApplyQuerySuggestionCardWidth(card);
        }
    }


    /// <summary>
    /// Closes the directory and query history dropdowns and briefly suppresses them, fighting the
    /// deferred re-open an <see cref="AutoSuggestBox"/> can trigger as focus changes.
    /// </summary>
    private void CollapseInputSuggestionDropdowns()
    {
        SuppressQuerySuggestionsFor(750, QueryBox);

        DirectoryBox.IsSuggestionListOpen = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            DirectoryBox.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => DirectoryBox.IsSuggestionListOpen = false);
        });
    }

    private void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && !AreQuerySuggestionsSuppressed())
        {
            ApplyQuerySuggestions(sender, open: sender.IsSuggestionListOpen || _querySuggestionsUserOpened);
        }
        else if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            // A programmatic query change — e.g. a semantic search whose natural-language query was
            // translated into a concrete literal pattern and written back into this box — must NOT
            // pop the history dropdown open. Deliberate opens (Down arrow, pointer focus) set
            // IsSuggestionListOpen directly without changing Text, so they never reach this branch.
            // The AutoSuggestBox can re-open its popup just after the change, so close it now and
            // again on the next tick (mirrors OnDirectoryTextChanged).
            sender.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => sender.IsSuggestionListOpen = false);
        }
    }

    /// <summary>
    /// In compact launcher mode the window is sized to its content height. The query and directory
    /// boxes wrap and grow with multi-line text, so re-fit the launcher whenever either one's height
    /// changes to keep the action bar below them (Search, load-session, and terminal buttons) visible
    /// at all times. Beyond each box's MaxHeight the text scrolls internally, so the window never
    /// outgrows the work area.
    /// </summary>
    private void OnSearchInputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_launcherMode && e.NewSize.Height != e.PreviousSize.Height)
            PositionLauncherWindow();

        if (ReferenceEquals(sender, DirectoryBox) && DirectoryBox.IsSuggestionListOpen)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ConstrainDirectorySuggestionListWidth);
        else if (ReferenceEquals(sender, QueryBox) && QueryBox.IsSuggestionListOpen)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ConstrainQuerySuggestionListWidth);
    }

    private void OnQueryClearClick(object sender, RoutedEventArgs e)
    {
        SuppressQuerySuggestionsFor(250, QueryBox);
        QueryBox.Text = string.Empty;
        ViewModel.Query = string.Empty;
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void OnQueryBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(QueryBox);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;

        DispatcherQueue.TryEnqueue(ShowQuerySuggestionsFromPointerFocus);
    }

    private void ShowQuerySuggestionsFromPointerFocus()
    {
        if (AreQuerySuggestionsSuppressed() || ActiveQueryHistory().Count == 0)
            return;

        ApplyQuerySuggestions(QueryBox, open: true);
        QueryBox.Focus(FocusState.Pointer);
    }

    private void OnQueryLostFocus(object sender, RoutedEventArgs e)
    {
        _querySuggestionsUserOpened = false;
        if (!AreQuerySuggestionsSuppressed())
            RestoreQuerySuggestions(sender as AutoSuggestBox);
    }

    private async void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        _directoryBrowseInProgress = true;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            string? folderPath = Helpers.Win32FileDialog.SelectFolder(hwnd, "Select Search Directory");
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                ViewModel.Directory = folderPath;
                DirectoryBox.Text = folderPath;
                int suggestionCount = await ViewModel.UpdateDirectorySuggestionsForSelectedDirectoryAsync(folderPath);
                DirectoryBox.ItemsSource = ViewModel.DirectorySuggestions;
                DirectoryBox.Focus(FocusState.Programmatic);
                DirectoryBox.IsSuggestionListOpen = suggestionCount > 0;
                ViewModel.RefreshCurrentIndexStatus();
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "Folder browse dialog failed.");
            ViewModel.StatusText = "Could not open the folder browse dialog.";
        }
        finally
        {
            _directoryBrowseInProgress = false;
        }
    }

    private void OnDirectoryQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // QuerySubmitted can run before x:Bind has committed the latest edit. Copy the submitted text
        // explicitly, then refresh proactive health for that root without starting a search.
        ViewModel.Directory = sender.Text;
        ViewModel.RefreshCurrentIndexStatus();
    }

    private async void OnPinStartupDirectory(object sender, RoutedEventArgs e)
    {
        // The ToggleButton's IsChecked has already flipped by the time Click fires. Its prior visual
        // state reflected whether the box was showing the pinned directory, so the flipped value is the
        // user's intent: checked = pin the CURRENT box value as the startup default; unchecked = clear
        // the pin. SetStartupDirectoryPinnedAsync snapshots the current directory and persists now, so
        // the pin survives even if the user never runs a search this session.
        bool pinned = (sender as ToggleButton)?.IsChecked == true;
        await ViewModel.SetStartupDirectoryPinnedAsync(pinned);
        // Re-sync the full star visual (checked highlight, glyph, tooltip) to the derived "the box IS the
        // pinned dir" value, which can differ from the raw toggle (e.g. trying to pin an empty box pins
        // nothing). This also re-asserts the checked state the user's click just changed.
        UpdatePinStartupDirectoryIcon(ViewModel.IsCurrentDirectoryPinned);
    }

    /// <summary>Syncs the pin star's full visual state — checked highlight, glyph (outline vs filled),
    /// and tooltip — to <paramref name="pinned"/>, the derived "the box currently shows the pinned
    /// startup directory" value (not merely "a pin exists").
    ///
    /// The checked state is driven HERE from code-behind, NOT an x:Bind on <c>IsChecked</c>: a OneWay
    /// x:Bind to a user-toggleable control is permanently disabled by the framework the first time the
    /// user clicks it (a OneWay binding can't write back, so it stops fighting user input). After that
    /// the star would freeze on its last value and never un-highlight when the box moved off the pinned
    /// directory. Calling this from the directory-change / IsCurrentDirectoryPinned PropertyChanged
    /// paths keeps the star correct.</summary>
    private void UpdatePinStartupDirectoryIcon(bool pinned)
    {
        PinStartupDirectoryButton.IsChecked = pinned;
        PinStartupDirectoryIcon.Glyph = pinned ? "\uE735" : "\uE734";
        ToolTipService.SetToolTip(
            PinStartupDirectoryButton,
            pinned
                ? "Unpin — start with an empty directory next launch"
                : "Pin this directory as the startup default");
    }

    /// <summary>Click handler for the index glyph next to the pin star. The <see cref="ToggleButton"/>'s
    /// IsChecked has already flipped, so the new value is the user's intent: checked = add the current box
    /// directory to the content index (and start a background build); unchecked = unregister it. A very
    /// large root is confirmed first (same gate as the onboarding flow). The button's highlighted "selected"
    /// state is then re-synced to the derived <see cref="MainViewModel.IsCurrentDirectoryIndexed"/> value.</summary>
    private async void OnIndexCurrentDirectory(object sender, RoutedEventArgs e)
    {
        string folder = (ViewModel.Directory ?? string.Empty).Trim();
        bool wantIndexed = (sender as ToggleButton)?.IsChecked == true;

        if (string.IsNullOrWhiteSpace(folder))
        {
            ViewModel.StatusText = "Enter a folder in the directory box to add it to the content index.";
            UpdateIndexDirectoryIcon(ViewModel.IsCurrentDirectoryIndexed);
            return;
        }

        // Keep the query/directory suggestion dropdowns (their own top-level windows) parked shut across
        // any confirmation modal shown below so they can't float above it.
        using var suppression = ParkInputSuggestionsForModal();
        try
        {
            if (wantIndexed && !ViewModel.IsCurrentDirectoryIndexed)
            {
                // Warn before an unattended build of a whole drive / very large system folder.
                if (!await ConfirmLargeFolderIfNeededAsync(folder))
                    return;
                await ViewModel.AddFolderToIndexAndBuildAsync(folder);
            }
            else if (!wantIndexed && ViewModel.IsCurrentDirectoryIndexed)
            {
                if (!IndexedRootsPolicy.Contains(ViewModel.Settings.IndexedRoots, folder))
                {
                    ViewModel.StatusText = $"{folder} is covered by the broader index root {ViewModel.CurrentDirectoryIndexRoot}. Manage that root in Settings > Indexing.";
                    return;
                }
                await ViewModel.RemoveFolderFromIndexAsync(folder);
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Toggling the directory content index failed.");
        }
        finally
        {
            // Re-assert the derived highlight (the raw toggle can differ from reality, e.g. a cancelled
            // large-folder confirmation, or an already-indexed folder).
            UpdateIndexDirectoryIcon(ViewModel.IsCurrentDirectoryIndexed);
        }
    }

    /// <summary>Syncs the index toggle's "selected" highlight and tooltip to <paramref name="indexed"/>,
    /// the derived "the box directory is a registered index root" value. Like the pin star, the checked
    /// state is driven from code-behind (not a self-disabling OneWay x:Bind) so it stays correct as the box
    /// directory changes.</summary>
    private void UpdateIndexDirectoryIcon(bool indexed)
    {
        IndexDirectoryButton.IsChecked = indexed;
        ToolTipService.SetToolTip(
            IndexDirectoryButton,
            indexed
                ? IndexedRootsPolicy.Contains(ViewModel.Settings.IndexedRoots, ViewModel.Directory)
                    ? "This directory is an explicit content-index root — click to remove it"
                    : $"This directory is covered by the content-index root {ViewModel.CurrentDirectoryIndexRoot}. Manage it in Settings > Indexing."
                : "Add this directory to the content index");
    }

    private void OnDirectoryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            // User typing: fetch subdirectory suggestions for the new text.
            _ = ViewModel.UpdateDirectorySuggestionsAsync(sender.Text);
        }
        else if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange && !_directoryBrowseInProgress)
        {
            // A programmatic Directory change — e.g. a semantic search applying its resolved
            // directory and then restoring the user's default as the search starts — must NOT pop
            // the history dropdown open. (The Browse button sets the text too, but opens the list
            // deliberately afterward, so it is excluded via _directoryBrowseInProgress.) The
            // AutoSuggestBox can re-open its popup just after the change, so close it now and again
            // on the next tick.
            sender.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => sender.IsSuggestionListOpen = false);
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handlers are bound as instance methods.")]
    private void OnDirectorySuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is Yagu.Models.HistorySuggestion suggestion)
        {
            // Append trailing backslash so user can continue drilling down.
            string chosen = suggestion.Value;
            sender.Text = chosen.EndsWith('\\') ? chosen : chosen + '\\';
        }
    }

    /// <summary>
    /// Closes the directory history dropdown when the user presses anywhere outside the directory
    /// box. An <see cref="AutoSuggestBox"/>'s suggestion list only auto-closes when the box loses
    /// keyboard focus, so clicking a non-focusable surface (an icon, an empty panel, the results
    /// background) would otherwise leave the dropdown stranded open. Wired window-wide on RootGrid.
    /// Presses on the suggestion items themselves route through the popup layer rather than RootGrid,
    /// so they never reach this handler and choosing a suggestion still works.
    /// </summary>
    private void OnRootPointerPressedDismissDirectorySuggestions(object sender, PointerRoutedEventArgs e)
    {
        if (!DirectoryBox.IsSuggestionListOpen) return;
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, DirectoryBox)) return;
        DirectoryBox.IsSuggestionListOpen = false;
    }

    /// <summary>Walks the visual tree from <paramref name="node"/> upward, returning true if
    /// <paramref name="ancestor"/> is the node itself or any of its ancestors.</summary>
    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private void OnRestartAsAdmin(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            // Strip any pre-existing --wait-for-pid <n> tokens, then append our own
            // pointing at the current process so the elevated instance waits for us
            // to fully exit (and release the single-instance mutex) before starting.
            var existing = Environment.GetCommandLineArgs().Skip(1).ToList();
            for (int i = existing.Count - 2; i >= 0; i--)
            {
                if (string.Equals(existing[i], "--wait-for-pid", StringComparison.OrdinalIgnoreCase))
                {
                    existing.RemoveAt(i + 1);
                    existing.RemoveAt(i);
                }
            }
            existing.Add("--wait-for-pid");
            existing.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            var args = string.Join(" ", existing.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            // Release the single-instance mutex BEFORE starting the elevated process,
            // so there's no race where the new instance sees the mutex still owned.
            try
            {
                App.InstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException) { /* not owned — ignore */ }
            App.InstanceMutex?.Dispose();
            App.InstanceMutex = null;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt — re-acquire the mutex so this instance
            // remains the single instance, then do nothing.
            try
            {
                App.InstanceMutex = new Mutex(true, @"Global\YaguSingleInstance", out _);
            }
            catch { /* best-effort */ }
        }
    }

    private async void OnDontShowAdminWarningAgain(object sender, RoutedEventArgs e)
    {
        ViewModel.SuppressAdminWarning = true;
        await ViewModel.PersistSettingsAsync();
        AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_launcherMode)
            PositionLauncherWindow();
    }

    private void OnAdminBannerCloseClick(object sender, RoutedEventArgs e)
    {
        AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_launcherMode)
            PositionLauncherWindow();
        FocusSearchBox();
    }
}
