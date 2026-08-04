using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Logging;
using Yagu.ViewModels;

namespace Yagu;

/// <summary>
/// Management actions for the <b>Indexing</b> settings tab (plan §6.1/§6.3). Every action runs the pure
/// <see cref="ContentIndexManager"/> on a background thread and reports its outcome in a status line;
/// destructive actions confirm through a title-bar-less <see cref="YaguDialog"/> (never a WinUI content
/// dialog). A build is cancellable and always leaves the previous generation intact on cancel or
/// failure.
/// </summary>
public sealed partial class SettingsWindow
{
    private Button? _indexBuildButton;
    private Button? _indexRebuildButton;
    private Button? _indexRepairButton;
    private Button? _indexValidateButton;
    private Button? _indexDeleteButton;
    private Button? _indexClearButton;
    private Button? _indexCancelButton;
    private StackPanel? _indexedRootsPanel;
    private TextBlock? _indexStorageSummaryText;
    private StackPanel? _indexStorageStatsPanel;
    private Button? _indexRefreshStatsButton;
    private readonly List<Control> _indexStorageActionControls = [];
    private IndexStorageSummary? _lastIndexStorageSummary;
    private string _indexManageRoot = string.Empty;
    private string _indexManageScopeId = string.Empty;

    // Per-folder-row visuals so a live build can overlay "Indexing… N%" + a progress bar on the exact row
    // being built without rebuilding the whole list on every progress tick. Keyed by normalized root path.
    private readonly Dictionary<string, IndexedRootRowVisuals> _indexedRootRowVisuals = new(StringComparer.OrdinalIgnoreCase);

    // Per-root freshness computed off the UI thread alongside the storage stats: true = the USN journal
    // proves the folder changed since its index was built (rebuild recommended); false = no proven change;
    // absent = unknown (journal unavailable). Keyed by normalized root path.
    private Dictionary<string, bool> _rootStaleByPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ContentIndexManager.ScopeFreshnessStatus> _rootFreshnessByPath = new(StringComparer.OrdinalIgnoreCase);

    private sealed record RootFreshnessSnapshot(
        Dictionary<string, bool> StaleByPath,
        Dictionary<string, ContentIndexManager.ScopeFreshnessStatus> StatusByPath);

    private sealed class IndexedRootRowVisuals
    {
        public IndexedRootRowVisuals(TextBlock detail, ProgressBar progress, string idleDetailText)
        {
            Detail = detail;
            Progress = progress;
            IdleDetailText = idleDetailText;
        }

        public TextBlock Detail { get; }
        public ProgressBar Progress { get; }
        public string IdleDetailText { get; set; }
    }

    private void BuildIndexManagementSection(StackPanel group)
    {
        group.Children.Add(new TextBlock
        {
            Text = "This is the one list of folders Yagu indexes. Add a folder, select it, then use the buttons to build, rebuild, validate, or delete its index \u2014 every button acts on the folder you\u2019ve selected. By default nothing builds on its own; you build here when you want. (Optional: the Build Scheduling group below can rebuild these folders for you automatically. Where the index files are stored on disk is set separately in the Storage group.)",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        _indexManageRoot = string.Empty;
        _indexManageScopeId = string.Empty;

        // The single selectable folder list. Selecting a folder here is what the action buttons operate on.
        BuildIndexedRootsList(group);

        _indexBuildButton = new Button { Content = "Build now", Padding = new Thickness(12, 4, 12, 4) };
        _indexBuildButton.Click += (_, _) => _ = RunIndexBuildAsync(rebuild: false);
        _indexRebuildButton = new Button { Content = "Rebuild", Padding = new Thickness(12, 4, 12, 4) };
        _indexRebuildButton.Click += (_, _) => _ = RunIndexBuildAsync(rebuild: true);
        _indexValidateButton = new Button { Content = "Validate", Padding = new Thickness(12, 4, 12, 4) };
        _indexValidateButton.Click += (_, _) => _ = RunIndexValidateAsync();
        _indexCancelButton = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), Visibility = Visibility.Collapsed };
        _indexCancelButton.Click += (_, _) => _indexBuildCts?.Cancel();
        group.Children.Add(MakeIndexButtonRow(_indexBuildButton, _indexRebuildButton, _indexValidateButton, _indexCancelButton));

        _indexRepairButton = new Button { Content = "Repair index", Padding = new Thickness(12, 4, 12, 4) };
        _indexRepairButton.Click += (_, _) => _ = RunIndexRepairAsync();
        _indexDeleteButton = new Button { Content = "Delete this index", Padding = new Thickness(12, 4, 12, 4) };
        _indexDeleteButton.Click += (_, _) => _ = RunIndexDeleteAsync();
        _indexClearButton = new Button { Content = "Clear all indexes", Padding = new Thickness(12, 4, 12, 4) };
        _indexClearButton.Click += (_, _) => _ = RunIndexClearAllAsync();
        var openButton = new Button { Content = "Open storage folder", Padding = new Thickness(12, 4, 12, 4) };
        openButton.Click += (_, _) => OpenIndexStorageLocation();
        group.Children.Add(MakeIndexButtonRow(_indexRepairButton, _indexDeleteButton, _indexClearButton, openButton));

        _indexStatusText = new TextBlock
        {
            Text = ContentIndexUiStatus.MasterStateSummary(
                _viewModel.Settings.EnableContentIndex, _viewModel.Settings.UseContentIndexByDefault),
            FontSize = 12,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        group.Children.Add(_indexStatusText);

        BuildIndexStorageStats(group);

        RefreshIndexManagementButtons();
    }

    /// <summary>
    /// The storage-stats block (plan §6.2): total on-disk size + a per-index breakdown (root, size,
    /// document count, segment count, build time), read from manifests only so it never loads a paged
    /// multi-GB generation into memory. Refreshed on demand and after every build/delete/clear.
    /// </summary>
    private void BuildIndexStorageStats(StackPanel group)
    {
        group.Children.Add(new TextBlock
        {
            Text = "Index storage",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        });

        _indexStorageSummaryText = new TextBlock
        {
            Text = "Loading index health…",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        };
        group.Children.Add(_indexStorageSummaryText);

        _indexStorageStatsPanel = new StackPanel { Spacing = 10 };
        group.Children.Add(_indexStorageStatsPanel);

        _indexRefreshStatsButton = new Button { Content = "Refresh stats", Padding = new Thickness(12, 4, 12, 4) };
        _indexRefreshStatsButton.Click += (_, _) => _ = RefreshIndexStorageStatsAsync();
        group.Children.Add(MakeIndexButtonRow(_indexRefreshStatsButton));

        _ = RefreshIndexStorageStatsAsync();
    }

    /// <summary>
    /// Recomputes the storage-stats block off the UI thread (crawling the storage directory for sizes and
    /// reading manifests for counts) and renders it. Crash-safe: any failure leaves a short fallback line.
    /// </summary>
    private async Task RefreshIndexStorageStatsAsync()
    {
        if (_indexStorageSummaryText is null || _indexStorageStatsPanel is null)
            return;
        try
        {
            var manager = CreateIndexManager();
            IndexStorageSummary summary = await Task.Run(manager.GetStorageStats).ConfigureAwait(true);
            _lastIndexStorageSummary = summary;
            _rootStaleByPath.Clear();
            _rootFreshnessByPath.Clear();
            RenderIndexStorageStats(summary);
            // Show each folder's size + doc count NOW (GetStorageStats is manifest-only), so the rows leave
            // "checking index…" immediately instead of waiting on the slower per-root journal read below.
            RefreshIndexedRootsRadios();

            // Per-root freshness (USN-proven "changes detected since build") is a slower journal read — do it
            // after the sizes are already visible, then refresh once more to add the freshness marker.
            RootFreshnessSnapshot freshness = await Task.Run(() => ComputeRootStaleness(manager, summary)).ConfigureAwait(true);
            _rootStaleByPath = freshness.StaleByPath;
            _rootFreshnessByPath = freshness.StatusByPath;
            RefreshIndexedRootsRadios();
            RenderIndexStorageStats(summary);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Failed to compute index storage stats");
            _indexStorageSummaryText.Text = "Index storage is temporarily unavailable.";
            _indexStorageStatsPanel.Children.Clear();
        }
    }

    /// <summary>
    /// Renders storage metadata as two scannable groups instead of one dense diagnostic paragraph:
    /// actionable/broken indexes first, then healthy indexes. Each card has a color + glyph + text state
    /// (never color alone), readable metadata, a plain-language explanation, and direct action links.
    /// </summary>
    private void RenderIndexStorageStats(IndexStorageSummary summary)
    {
        if (_indexStorageSummaryText is null || _indexStorageStatsPanel is null)
            return;

        _indexStorageStatsPanel.Children.Clear();
        _indexStorageActionControls.Clear();

        if (summary.Indexes.Count == 0)
        {
            _indexStorageSummaryText.Text = "No content indexes are stored yet.";
            _indexStorageStatsPanel.Children.Add(new TextBlock
            {
                Text = "Add a folder above, then choose Build now.",
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        IndexStorageStat[] attention = summary.Indexes
            .Where(IsStorageIndexAttention)
            .OrderByDescending(stat => stat.SizeBytes)
            .ToArray();
        IndexStorageStat[] healthy = summary.Indexes
            .Where(stat => !IsStorageIndexAttention(stat))
            .OrderByDescending(stat => stat.SizeBytes)
            .ToArray();

        _indexStorageSummaryText.Text =
            $"{ContentIndexUiStatus.FormatBytes(summary.TotalSizeBytes)} total  ·  "
            + $"{healthy.Length} healthy  ·  {attention.Length} need attention  ·  "
            + $"{summary.TotalDocuments:N0} stored content records";

        if (attention.Length > 0)
        {
            _indexStorageStatsPanel.Children.Add(BuildStorageGroupHeader(
                "Needs attention", attention.Length, "\uE7BA", Microsoft.UI.Colors.DarkOrange));
            foreach (IndexStorageStat stat in attention)
                _indexStorageStatsPanel.Children.Add(BuildIndexStorageCard(stat));
        }

        if (healthy.Length > 0)
        {
            _indexStorageStatsPanel.Children.Add(BuildStorageGroupHeader(
                "Healthy indexes", healthy.Length, "\uE930", Microsoft.UI.Colors.LimeGreen));
            foreach (IndexStorageStat stat in healthy)
                _indexStorageStatsPanel.Children.Add(BuildIndexStorageCard(stat));
        }

        RefreshIndexManagementButtons();
    }

    private bool IsStorageIndexAttention(IndexStorageStat stat)
    {
        if (stat.Health != IndexStorageHealth.Healthy || stat.RootPath is null)
            return true;
        if (FindRootFreshnessStatus(stat.RootPath) is { NeedsAttention: true })
            return true;
        return !IndexedRootsPolicy.Contains(_viewModel.Settings.IndexedRoots, stat.RootPath)
            || FindRegisteredCoveringAncestor(stat.RootPath) is not null;
    }

    private static Grid BuildStorageGroupHeader(string title, int count, string glyph, Windows.UI.Color color)
    {
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(icon);

        var text = new TextBlock
        {
            Text = $"{title} ({count})",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        return header;
    }

    private Border BuildIndexStorageCard(IndexStorageStat stat)
    {
        string? coveringRoot = stat.RootPath is null ? null : FindRegisteredCoveringAncestor(stat.RootPath);
        ContentIndexManager.ScopeFreshnessStatus? freshness = FindRootFreshnessStatus(stat.RootPath);
        bool registered = stat.RootPath is not null
            && IndexedRootsPolicy.Contains(_viewModel.Settings.IndexedRoots, stat.RootPath);

        string glyph;
        Windows.UI.Color color;
        string state;
        string explanation;
        if (stat.Health != IndexStorageHealth.Healthy)
        {
            glyph = "\uE711";
            color = Microsoft.UI.Colors.Tomato;
            state = stat.Health switch
            {
                IndexStorageHealth.SourceMissing => "Source folder missing",
                IndexStorageHealth.IncompatibleFormat => "Repair required · old index format",
                IndexStorageHealth.IncompatibleRepresentation => "Repair required · old content representation",
                _ => "Broken or incomplete index",
            };
            if (coveringRoot is not null)
            {
                state += " · redundant";
                explanation = (stat.Problem ?? "The index is not usable.")
                    + $" The maintained index for {coveringRoot} already covers this folder, so delete this broken child index instead of rebuilding a duplicate.";
            }
            else
            {
                explanation = stat.RootPath is null
                    ? (stat.Problem ?? "The original source folder could not be recovered from trustworthy metadata.")
                        + " Yagu will not use this data for searching."
                    : stat.Health == IndexStorageHealth.SourceMissing
                        ? "The source folder no longer exists or is unavailable. Restore it to rebuild, or delete the stored index."
                        : (stat.Problem ?? "The index metadata is damaged or incomplete.")
                            + " Searches safely live-scan this folder until the index is repaired.";
            }
        }
        else if (coveringRoot is not null)
        {
            glyph = "\uE7BA";
            color = Microsoft.UI.Colors.DarkOrange;
            state = "Redundant child index";
            explanation = $"The maintained index for {coveringRoot} already covers this folder. Yagu never opens both indexes for one search. Delete this stored child index to reclaim space.";
        }
        else if (freshness is { RequiresRebuild: true } freshnessIssue)
        {
            glyph = "\uE7BA";
            color = Microsoft.UI.Colors.Tomato;
            state = "Rebuild required · freshness lost";
            explanation = (freshnessIssue.Problem ?? "Yagu cannot prove this index includes every recent file change.")
                + " Searches safely scan this folder live until the index is rebuilt.";
        }
        else if (freshness is { RawStatus: UsnReadStatus.Incomplete } catchupLimit)
        {
            glyph = "\uE7BA";
            color = Microsoft.UI.Colors.DarkOrange;
            state = "Update needed · catch-up limit reached";
            explanation = catchupLimit.Problem
                ?? "Increase the journal catch-up limit and update this index, or rebuild it. Searches safely scan this folder live meanwhile.";
        }
        else if (freshness is { NeedsAttention: true } unavailableFreshness)
        {
            glyph = "\uE7BA";
            color = Microsoft.UI.Colors.DarkOrange;
            state = "Freshness unavailable · live scan only";
            explanation = unavailableFreshness.Problem
                ?? "Yagu cannot currently prove this index includes every recent file change. Searches safely scan this folder live.";
        }
        else if (stat.Health == IndexStorageHealth.Healthy && registered)
        {
            glyph = "\uE930";
            color = Microsoft.UI.Colors.LimeGreen;
            state = "Valid index";
            explanation = "This index is valid, registered, and eligible for normal maintenance.";
        }
        else
        {
            glyph = "\uE7BA";
            color = Microsoft.UI.Colors.DarkOrange;
            state = "Valid but not maintained";
            explanation = "This index is usable, but its folder is not registered for automatic maintenance. Add it back to keep it current, or delete its stored data.";
        }

        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var statusIcon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        content.Children.Add(statusIcon);

        var body = new StackPanel { Spacing = 4 };
        Grid.SetColumn(body, 1);
        content.Children.Add(body);

        string title = stat.RootPath ?? $"Unidentified index data · {stat.ScopeId[..Math.Min(12, stat.ScopeId.Length)]}…";
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = state,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = BuildStorageMetadataLine(stat),
            FontSize = 12,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = explanation,
            FontSize = 12,
            Opacity = 0.94,
            TextWrapping = TextWrapping.Wrap,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        AddIndexStorageActions(actions, stat, coveringRoot, registered);
        if (actions.Children.Count > 0)
            body.Children.Add(actions);

        var borderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color) { Opacity = 0.5 };
        var card = new Border
        {
            Child = content,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"{state}: {title}");
        return card;
    }

    private string BuildStorageMetadataLine(IndexStorageStat stat)
    {
        var parts = new List<string> { ContentIndexUiStatus.FormatBytes(stat.SizeBytes) };
        parts.Add($"{stat.DocumentCount:N0} stored content records");
        if (stat.Health == IndexStorageHealth.Healthy)
            parts.Add(stat.SegmentCount == 0 ? "single generation" : $"base + {stat.SegmentCount} segments");
        if (stat.BuiltUtc is { } built)
            parts.Add($"active generation built {built.LocalDateTime:yyyy-MM-dd HH:mm}");
        if (stat.RootPath is not null
            && FindRootFreshnessStatus(stat.RootPath) is { } freshness)
        {
            parts.Add(freshness.State switch
            {
                ContentIndexManager.ScopeFreshnessState.Dirty => "changes detected",
                ContentIndexManager.ScopeFreshnessState.Fresh => "up to date",
                ContentIndexManager.ScopeFreshnessState.Uncertain when freshness.RequiresRebuild => "freshness lost · rebuild required",
                ContentIndexManager.ScopeFreshnessState.Uncertain when freshness.RawStatus == UsnReadStatus.Incomplete => "catch-up limit reached · increase limit and update",
                ContentIndexManager.ScopeFreshnessState.Uncertain => "freshness unavailable · live scan only",
                _ => "freshness unavailable",
            });
        }
        return string.Join("  ·  ", parts);
    }

    private void AddIndexStorageActions(
        StackPanel actions,
        IndexStorageStat stat,
        string? coveringRoot,
        bool registered)
    {
        if (coveringRoot is not null)
        {
            actions.Children.Add(CreateStorageActionLink(
                "Delete redundant index", () => RunStorageDeleteAsync(stat)));
            return;
        }

        if (stat.CanRepair)
        {
            actions.Children.Add(CreateStorageActionLink(
                "Repair now", () => RunStorageRepairAsync(stat), requiresMaster: true));
            actions.Children.Add(CreateStorageActionLink(
                "Delete stored index", () => RunStorageDeleteAsync(stat)));
            return;
        }

        if (stat.Health != IndexStorageHealth.Healthy || stat.RootPath is null)
        {
            actions.Children.Add(CreateStorageActionLink(
                "Delete stored index", () => RunStorageDeleteAsync(stat)));
            return;
        }

        if (!registered)
        {
            actions.Children.Add(CreateStorageActionLink(
                "Add to maintained folders", () => RegisterStorageRootAsync(stat)));
            actions.Children.Add(CreateStorageActionLink(
                "Delete stored index", () => RunStorageDeleteAsync(stat)));
            return;
        }

        if (FindRootFreshnessStatus(stat.RootPath) is { RequiresRebuild: true })
        {
            actions.Children.Add(CreateStorageActionLink(
                "Rebuild required", () => RunStorageRebuildAsync(stat), requiresMaster: true));
            actions.Children.Add(CreateStorageActionLink(
                "Validate files", () => RunStorageValidateAsync(stat)));
            return;
        }

        if (FindRootFreshnessStatus(stat.RootPath) is { NeedsAttention: true })
        {
            actions.Children.Add(CreateStorageActionLink(
                "Validate files", () => RunStorageValidateAsync(stat)));
            return;
        }

        actions.Children.Add(CreateStorageActionLink(
            "Validate", () => RunStorageValidateAsync(stat)));
        actions.Children.Add(CreateStorageActionLink(
            "Rebuild", () => RunStorageRebuildAsync(stat), requiresMaster: true));
    }

    private HyperlinkButton CreateStorageActionLink(string text, Func<Task> action, bool requiresMaster = false)
    {
        var link = new HyperlinkButton
        {
            Content = text,
            Padding = new Thickness(0, 3, 12, 3),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Tag = requiresMaster,
        };
        link.Click += async (_, _) => await action();
        _indexStorageActionControls.Add(link);
        return link;
    }

    private void SelectStorageStat(IndexStorageStat stat)
    {
        _indexManageRoot = stat.RootPath ?? string.Empty;
        _indexManageScopeId = stat.ScopeId;
        RefreshIndexedRootsRadios();
    }

    private async Task RunStorageRepairAsync(IndexStorageStat stat)
    {
        SelectStorageStat(stat);
        await RunIndexRepairAsync();
    }

    private async Task RunStorageDeleteAsync(IndexStorageStat stat)
    {
        SelectStorageStat(stat);
        await RunIndexDeleteAsync();
    }

    private async Task RunStorageValidateAsync(IndexStorageStat stat)
    {
        SelectStorageStat(stat);
        await RunIndexValidateAsync();
    }

    private async Task RunStorageRebuildAsync(IndexStorageStat stat)
    {
        SelectStorageStat(stat);
        await RunIndexBuildAsync(rebuild: true);
    }

    private Task RegisterStorageRootAsync(IndexStorageStat stat)
    {
        if (stat.RootPath is null)
            return Task.CompletedTask;
        _viewModel.Settings.IndexedRoots = IndexedRootsPolicy.Add(
            _viewModel.Settings.IndexedRoots, stat.RootPath);
        _indexManageRoot = IndexedRootsPolicy.FindBestCoveringRoot(
            _viewModel.Settings.IndexedRoots, stat.RootPath) ?? stat.RootPath;
        _indexManageScopeId = stat.ScopeId;
        MarkSettingsDirty(requireValueChanges: false);
        SetIndexStatus($"Added {_indexManageRoot} to the maintained index folders.");
        RefreshIndexedRootsRadios();
        _viewModel.RefreshAllDriveIndexStatus();
        if (_lastIndexStorageSummary is { } summary)
            RenderIndexStorageStats(summary);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Computes per-root freshness for every on-disk index in <paramref name="summary"/>: <c>true</c> when
    /// the USN journal PROVES the folder changed since its index was built ("changes detected — rebuild"),
    /// <c>false</c> when there is no proven change. Roots whose freshness can't be read are omitted (shown
    /// as unknown). Runs off the UI thread; never throws.
    /// </summary>
    private RootFreshnessSnapshot ComputeRootStaleness(ContentIndexManager manager, IndexStorageSummary summary)
    {
        var stale = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var statuses = new Dictionary<string, ContentIndexManager.ScopeFreshnessStatus>(StringComparer.OrdinalIgnoreCase);
        ContentIndexFreshnessEvaluator.JournalReader reader = ContentIndexFreshnessEvaluator.CreateReader(
            AppSettings.NormalizeIndexMaxJournalCatchupRecords(_viewModel.Settings.IndexMaxJournalCatchupRecords));
        foreach (IndexStorageStat stat in summary.Indexes)
        {
            if (stat.RootPath is null || stat.Health != IndexStorageHealth.Healthy || !stat.RootExists)
                continue;
            try
            {
                string key = IndexScopeIdentity.NormalizePath(stat.RootPath);
                ContentIndexManager.ScopeFreshnessStatus freshness = manager.GetScopeFreshnessStatus(stat.RootPath, reader);
                statuses[key] = freshness;
                if (freshness.State == ContentIndexManager.ScopeFreshnessState.Dirty)
                    stale[key] = true;
                else if (freshness.State == ContentIndexManager.ScopeFreshnessState.Fresh)
                    stale[key] = false;
            }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogDebug("IsScopeStale failed for '{RootPath}': {ExceptionType}", stat.RootPath, ex.GetType().Name);
            }
        }
        return new RootFreshnessSnapshot(stale, statuses);
    }

    /// <summary>
    /// The single folder list (plan §6.1 <c>IndexedRoots</c>): the folders Yagu indexes, shown as a
    /// selectable radio list. The selected folder is <see cref="_indexManageRoot"/> — what every action
    /// button (Build / Rebuild / Validate / Delete) operates on. "Add folder…" registers a folder (also
    /// enrolling it for the automatic build schedule); "Remove selected folder" unregisters it. Writes
    /// directly to <see cref="MainViewModel.Settings"/>'s <c>IndexedRoots</c> through the pure
    /// <see cref="IndexedRootsPolicy"/>, so persistence/cancel-restore work like every other setting.
    /// </summary>
    private void BuildIndexedRootsList(StackPanel group)
    {
        group.Children.Add(new TextBlock
        {
            Text = "Folders you index",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _indexedRootsPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 0) };
        group.Children.Add(_indexedRootsPanel);
        group.Children.Add(new TextBlock
        {
            Text = "Select a folder here, then use the buttons below to build or manage its index. \u201cAdd folder…\u201d also enrolls it in the automatic build schedule (Build Scheduling), so you don\u2019t need a second list. (CLI: --index-add-root / --index-remove-root / --index-list-roots.)",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        var addButton = new Button { Content = "Add folder…", Padding = new Thickness(12, 4, 12, 4) };
        addButton.Click += (_, _) =>
        {
            string? folder = Win32FileDialog.SelectFolder(_settingsHwnd, "Select Folder to Index");
            if (string.IsNullOrWhiteSpace(folder))
                return;
            string requestedRoot = IndexScopeIdentity.NormalizePath(folder);
            List<string> before = IndexedRootsPolicy.Normalize(_viewModel.Settings.IndexedRoots);
            string? existingCover = IndexedRootsPolicy.FindBestCoveringRoot(before, requestedRoot);
            IReadOnlyList<string> coveredDescendants = IndexedRootsPolicy.FindCoveredDescendants(before, requestedRoot);
            _viewModel.Settings.IndexedRoots = IndexedRootsPolicy.Add(before, requestedRoot);
            _indexManageRoot = IndexedRootsPolicy.FindBestCoveringRoot(_viewModel.Settings.IndexedRoots, requestedRoot)
                ?? requestedRoot;
            _indexManageScopeId = string.Empty;
            RefreshIndexedRootsRadios();
            MarkSettingsDirty(requireValueChanges: false);
            _viewModel.RefreshAllDriveIndexStatus();
            if (existingCover is not null)
                SetIndexStatus($"{requestedRoot} is already covered by {existingCover}; no duplicate index root was added.");
            else if (coveredDescendants.Count > 0)
                SetIndexStatus($"Added {requestedRoot} as the broader index root. {coveredDescendants.Count} narrower registration(s) are now covered and will no longer be maintained separately; their existing index data remains available to delete.");
        };
        var removeButton = new Button { Content = "Remove selected folder", Padding = new Thickness(12, 4, 12, 4) };
        removeButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_indexManageRoot))
                return;
            _viewModel.Settings.IndexedRoots = IndexedRootsPolicy.Remove(_viewModel.Settings.IndexedRoots, _indexManageRoot);
            _viewModel.Settings.IndexedRootFilters = RemoveRootFilter(_viewModel.Settings.IndexedRootFilters, _indexManageRoot);
            _indexManageRoot = string.Empty;
            _indexManageScopeId = string.Empty;
            RefreshIndexedRootsRadios();
            MarkSettingsDirty(requireValueChanges: false);
            _viewModel.RefreshAllDriveIndexStatus();
        };
        var filtersButton = new Button { Content = "Filters…", Padding = new Thickness(12, 4, 12, 4) };
        filtersButton.Click += async (_, _) => await ShowRootFilterEditorAsync();
        group.Children.Add(MakeIndexButtonRow(addButton, filtersButton, removeButton));

        RefreshIndexedRootsRadios();
    }

    /// <summary>Rebuilds the selectable folder radios from <c>IndexedRoots</c>; selecting one sets the active target.</summary>
    private void RefreshIndexedRootsRadios()
    {
        if (_indexedRootsPanel is null)
            return;
        _indexedRootsPanel.Children.Clear();
        _indexedRootRowVisuals.Clear();

        var registered = _viewModel.Settings.IndexedRoots ?? new List<string>();
        // Also surface any folder that has an on-disk index but is NOT registered here (an "orphan" —
        // built once, then unregistered; the index file stays on disk until deleted). Without this the
        // "Index storage" block below would list an index the folder list hides, which is confusing.
        var orphans = CollectOrphanIndexRoots(registered);

        var allRoots = new List<(string Root, bool Registered)>();
        foreach (string root in registered)
            allRoots.Add((root, true));
        foreach (string root in orphans)
            allRoots.Add((root, false));
        IndexStorageStat[] unidentified = _lastIndexStorageSummary?.Indexes
            .Where(stat => stat.RootPath is null)
            .ToArray() ?? Array.Empty<IndexStorageStat>();

        if (allRoots.Count == 0 && unidentified.Length == 0)
        {
            _indexManageRoot = string.Empty;
            _indexManageScopeId = string.Empty;
            _indexedRootsPanel.Children.Add(new TextBlock
            {
                Text = "(No folders yet. Click \u201cAdd folder…\u201d to choose one to index.)",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
            RefreshIndexManagementButtons();
            return;
        }

        // Keep either a root or an unidentified scope selected if it still exists; otherwise select the
        // first root, then the first unidentified scope. The latter can only be deleted because no trusted
        // source path exists from which to rebuild it.
        bool selectedRootExists = !string.IsNullOrWhiteSpace(_indexManageRoot)
            && allRoots.Any(x => string.Equals(x.Root, _indexManageRoot, StringComparison.OrdinalIgnoreCase));
        bool selectedScopeExists = string.IsNullOrWhiteSpace(_indexManageRoot)
            && !string.IsNullOrWhiteSpace(_indexManageScopeId)
            && unidentified.Any(x => string.Equals(x.ScopeId, _indexManageScopeId, StringComparison.OrdinalIgnoreCase));
        if (!selectedRootExists && !selectedScopeExists)
        {
            if (allRoots.Count > 0)
            {
                _indexManageRoot = allRoots[0].Root;
                _indexManageScopeId = FindStorageStatForRoot(_indexManageRoot)?.ScopeId ?? string.Empty;
            }
            else
            {
                _indexManageRoot = string.Empty;
                _indexManageScopeId = unidentified[0].ScopeId;
            }
        }

        foreach ((string root, bool registeredRoot) in allRoots)
            _indexedRootsPanel.Children.Add(BuildIndexedRootRow(root, registeredRoot));
        foreach (IndexStorageStat stat in unidentified)
            _indexedRootsPanel.Children.Add(BuildUnidentifiedIndexRow(stat));

        // Overlay a live "Indexing… N%" + progress bar on the row being built, if any.
        UpdateIndexedRootBuildProgress();
        RefreshIndexManagementButtons();
    }

    /// <summary>
    /// The roots that have an on-disk index but are NOT in <paramref name="registered"/> — orphan indexes
    /// (built once, then the folder was unregistered; the index stays until deleted). Surfaced in the folder
    /// list so the user can rebuild or delete them, instead of the list silently disagreeing with the stats.
    /// </summary>
    private List<string> CollectOrphanIndexRoots(IReadOnlyList<string> registered)
    {
        var orphans = new List<string>();
        if (_lastIndexStorageSummary is not { } summary)
            return orphans;
        foreach (IndexStorageStat stat in summary.Indexes)
        {
            if (stat.RootPath is null)
                continue; // unreadable scope with no recoverable root path
            string key = IndexScopeIdentity.NormalizePath(stat.RootPath);
            bool isRegistered = registered.Any(r =>
                string.Equals(IndexScopeIdentity.NormalizePath(r), key, StringComparison.OrdinalIgnoreCase));
            bool alreadyAdded = orphans.Any(o =>
                string.Equals(IndexScopeIdentity.NormalizePath(o), key, StringComparison.OrdinalIgnoreCase));
            if (!isRegistered && !alreadyAdded)
                orphans.Add(stat.RootPath);
        }
        return orphans;
    }

    /// <summary>
    /// Builds a selectable row for scope data whose original source path cannot be recovered from a
    /// checksum-valid manifest. It remains visible and deletable instead of becoming permanent mystery data.
    /// </summary>
    private RadioButton BuildUnidentifiedIndexRow(IndexStorageStat stat)
    {
        string capturedScopeId = stat.ScopeId;
        string shortId = stat.ScopeId.Length > 12 ? stat.ScopeId[..12] + "…" : stat.ScopeId;
        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock
        {
            Text = $"Unidentified index data ({shortId})",
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{ContentIndexUiStatus.FormatBytes(stat.SizeBytes)}  ·  source path unavailable  ·  delete only",
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var radio = new RadioButton
        {
            Content = content,
            GroupName = "YaguIndexedFolders",
            MinWidth = 0,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = string.IsNullOrWhiteSpace(_indexManageRoot)
                && string.Equals(stat.ScopeId, _indexManageScopeId, StringComparison.OrdinalIgnoreCase),
        };
        ToolTipService.SetToolTip(radio,
            $"Scope: {stat.ScopeId}\n{stat.Problem ?? "No checksum-valid manifest could identify the original folder."}\n"
            + "Yagu will never use this data for searching. Select it and click Delete this index to remove only this scope.");
        radio.Checked += (_, _) =>
        {
            _indexManageRoot = string.Empty;
            _indexManageScopeId = capturedScopeId;
            RefreshIndexManagementButtons();
        };
        return radio;
    }

    /// <summary>
    /// Builds one selectable folder row: the path on top, and a muted second line summarizing the index
    /// size / stored content-record count and whether the folder has custom filters. A rich hover tooltip
    /// shows the full path, the index breakdown (size, records, layers, build time), and the per-folder globs.
    /// </summary>
    private RadioButton BuildIndexedRootRow(string root, bool isRegistered)
    {
        string captured = root;
        IndexedRootFilter? filter = IndexedRootFilterPolicy.Find(_viewModel.Settings.IndexedRootFilters, root);
        IndexStorageStat? stat = FindStorageStatForRoot(root);
        string? coveringRoot = FindRegisteredCoveringAncestor(root);
        bool? isStale = _rootStaleByPath.TryGetValue(IndexScopeIdentity.NormalizePath(root), out bool st) ? st : null;
        ContentIndexManager.ScopeFreshnessStatus? freshness = FindRootFreshnessStatus(root);

        var pathText = new TextBlock
        {
            Text = root,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        string idleDetail = FormatRootRowDetail(stat, filter is not null, _lastIndexStorageSummary is not null, isRegistered, isStale, coveringRoot, freshness);
        var detailText = new TextBlock
        {
            Text = idleDetail,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Live-build progress bar for this folder — hidden unless this row's folder is actively building.
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Width = 180,
            Height = 3,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(pathText);
        content.Children.Add(detailText);
        content.Children.Add(progressBar);
        _indexedRootRowVisuals[IndexScopeIdentity.NormalizePath(root)] = new IndexedRootRowVisuals(detailText, progressBar, idleDetail);

        var radio = new RadioButton
        {
            Content = content,
            GroupName = "YaguIndexedFolders",
            MinWidth = 0,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = string.Equals(root, _indexManageRoot, StringComparison.OrdinalIgnoreCase),
        };
        ToolTipService.SetToolTip(radio, BuildIndexedRootTooltip(root, stat, filter, isRegistered, coveringRoot, freshness));
        radio.Checked += (_, _) =>
        {
            _indexManageRoot = captured;
            _indexManageScopeId = stat?.ScopeId ?? string.Empty;
            RefreshIndexManagementButtons();
        };
        return radio;
    }

    /// <summary>The on-disk stat for <paramref name="root"/> from the last storage-stats read, or null.</summary>
    private IndexStorageStat? FindStorageStatForRoot(string root)
    {
        if (_lastIndexStorageSummary is not { } summary)
            return null;
        string key = IndexScopeIdentity.NormalizePath(root);
        foreach (IndexStorageStat stat in summary.Indexes)
        {
            if (stat.RootPath is not null
                && string.Equals(IndexScopeIdentity.NormalizePath(stat.RootPath), key, StringComparison.OrdinalIgnoreCase))
                return stat;
        }
        return null;
    }

    private string? FindRegisteredCoveringAncestor(string root)
    {
        string? covering = IndexedRootsPolicy.FindBestCoveringRoot(_viewModel.Settings.IndexedRoots, root);
        return covering is not null
            && !string.Equals(
                IndexScopeIdentity.NormalizePath(covering),
                IndexScopeIdentity.NormalizePath(root),
                StringComparison.OrdinalIgnoreCase)
            ? covering
            : null;
    }

    private ContentIndexManager.ScopeFreshnessStatus? FindRootFreshnessStatus(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        return _rootFreshnessByPath.TryGetValue(IndexScopeIdentity.NormalizePath(root), out var status)
            ? status
            : null;
    }

    /// <summary>The muted one-line summary under a folder path: index size + stored records (or state) and a filter marker.</summary>
    private static string FormatRootRowDetail(
        IndexStorageStat? stat,
        bool hasFilter,
        bool summaryLoaded,
        bool isRegistered,
        bool? isStale = null,
        string? coveringRoot = null,
        ContentIndexManager.ScopeFreshnessStatus? freshness = null)
    {
        string sizePart;
        if (stat is { RootExists: false, RootPath: not null } missing)
        {
            sizePart = $"{ContentIndexUiStatus.FormatBytes(missing.SizeBytes)}  \u00b7  source folder missing  \u00b7  restore it or delete the index";
        }
        else if (stat is { Health: IndexStorageHealth.Healthy } s)
        {
            sizePart = $"{ContentIndexUiStatus.FormatBytes(s.SizeBytes)}  \u00b7  {s.DocumentCount:N0} stored records";
            // Freshness marker: the USN journal proves whether the folder changed since its index was built.
            if (freshness is { RequiresRebuild: true })
                sizePart += "  \u00b7  freshness lost \u2014 rebuild required";
            else if (freshness is { RawStatus: UsnReadStatus.Incomplete })
                sizePart += "  \u00b7  catch-up limit reached \u2014 increase limit and update";
            else if (freshness is { NeedsAttention: true })
                sizePart += "  \u00b7  freshness unavailable \u2014 live scan only";
            else if (isStale == true)
                sizePart += "  \u00b7  changes detected \u2014 rebuild";
            else if (isStale == false)
                sizePart += "  \u00b7  up to date";
        }
        else if (stat is { NeedsRepair: true } repair)
        {
            sizePart = $"{ContentIndexUiStatus.FormatBytes(repair.SizeBytes)}  \u00b7  {ContentIndexUiStatus.StorageHealthLabel(repair.Health)}";
        }
        else if (!summaryLoaded)
            sizePart = "checking index\u2026";
        else
            sizePart = "not indexed yet";
        if (hasFilter)
            sizePart += "  \u00b7  custom filters";
        if (!isRegistered)
            sizePart += "  \u00b7  leftover index";
        if (coveringRoot is not null)
            sizePart += $"  \u00b7  redundant \u2014 covered by {coveringRoot}";
        return sizePart;
    }

    /// <summary>
    /// Overlays a live "Indexing… N%" label + progress bar on the folder row whose index is currently
    /// building (Settings "Build now" / onboarding), and restores the idle size/docs/freshness text on the
    /// others. Called on every build-state change so the row tracks progress without a full list rebuild.
    /// </summary>
    private void UpdateIndexedRootBuildProgress()
    {
        if (_indexedRootRowVisuals.Count == 0)
            return;

        bool building = _viewModel.IsIndexBuildActive;
        bool paused = _viewModel.IsIndexingPaused;
        string? activeFolder = _viewModel.ActiveIndexBuildFolder;
        string? activeKey = building && !string.IsNullOrWhiteSpace(activeFolder)
            ? IndexScopeIdentity.NormalizePath(activeFolder!)
            : null;
        int percent = _viewModel.IndexBuildPercentValue;

        foreach ((string key, IndexedRootRowVisuals row) in _indexedRootRowVisuals)
        {
            bool isActiveRow = activeKey is not null
                && string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase);
            if (isActiveRow && paused)
            {
                row.Detail.Text = "Indexing paused";
                row.Progress.Visibility = Visibility.Collapsed;
            }
            else if (isActiveRow)
            {
                row.Detail.Text = percent >= 0 ? $"Indexing\u2026 {percent}%" : "Indexing\u2026";
                // Keep motion visible throughout the build so a slowly changing estimate never makes the
                // UI appear frozen. The adjacent detail text still carries the best-known percentage.
                row.Progress.IsIndeterminate = true;
                row.Progress.Visibility = Visibility.Visible;
            }
            else
            {
                row.Detail.Text = row.IdleDetailText;
                row.Progress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Reacts to a <see cref="MainViewModel"/> index-build state change (active/percent/paused): refreshes
    /// the per-row progress overlay, and when a build finishes recomputes the storage stats + freshness so
    /// the row switches back to its up-to-date size/docs line.
    /// </summary>
    private void OnIndexBuildStateChangedForRows(string? propertyName)
    {
        UpdateIndexedRootBuildProgress();
        if (propertyName == nameof(MainViewModel.IsIndexBuildActive) && !_viewModel.IsIndexBuildActive)
        {
            YaguLog.For("ContentIndex").LogDebug("Index build finished; refreshing Settings folder-row stats and freshness.");
            _ = RefreshIndexStorageStatsAsync();
        }
    }

    /// <summary>Builds the modern hover tooltip for a folder row: path, index breakdown, and per-folder globs.</summary>
    private static FrameworkElement BuildIndexedRootTooltip(
        string root,
        IndexStorageStat? stat,
        IndexedRootFilter? filter,
        bool isRegistered,
        string? coveringRoot,
        ContentIndexManager.ScopeFreshnessStatus? freshness)
    {
        var panel = new StackPanel { Spacing = 4, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = root,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (stat is { RootExists: false, RootPath: not null } missing)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Index: {ContentIndexUiStatus.FormatBytes(missing.SizeBytes)}. The source folder no longer exists or is unavailable. Restore the folder to rebuild it, or click Delete this index to remove its stored data.",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (stat is { Health: IndexStorageHealth.Healthy } s)
        {
            string layers = s.SegmentCount == 0 ? "single generation" : $"base + {s.SegmentCount} segment(s)";
            string built = s.BuiltUtc is { } b ? $", active generation built {b.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty;
            panel.Children.Add(new TextBlock
            {
                Text = $"Index: {ContentIndexUiStatus.FormatBytes(s.SizeBytes)}  \u00b7  {s.DocumentCount:N0} stored content records  \u00b7  {layers}{built}",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (stat is { NeedsRepair: true } repair)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Index needs repair: {ContentIndexUiStatus.StorageHealthLabel(repair.Health)}. {repair.Problem} Searches safely live-scan this folder until it is rebuilt. Select it and click Repair index to perform an atomic worker-backed rebuild.",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Index: not built yet \u2014 select this folder and click Build now.",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (freshness is { RequiresRebuild: true } freshnessIssue)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Rebuild required: {freshnessIssue.Problem}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Tomato),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (freshness is { RawStatus: UsnReadStatus.Incomplete } catchupLimit)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Update needed: {catchupLimit.Problem}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (freshness is { NeedsAttention: true } unavailableFreshness)
        {
            panel.Children.Add(new TextBlock
            {
            Text = $"Freshness unavailable: {unavailableFreshness.Problem}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!isRegistered)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Leftover index: you have an index for this folder, but the folder isn\u2019t in your "
                     + "indexed-folders list \u2014 usually because it was indexed once and later removed from the "
                     + "list. The index stays on disk and searches can still use it, but it won\u2019t be kept up to "
                     + "date automatically. Click \u201cAdd folder\u2026\u201d to put it back on your list (and keep it "
                     + "maintained), or select it and click \u201cDelete this index\u201d to free the disk space.",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (coveringRoot is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Redundant index: the registered root {coveringRoot} already covers this entire folder. Searches use that one broader index only; this child index is no longer maintained or opened alongside it. Delete this index to reclaim its disk space.",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        bool hasGlobs = filter is not null && (filter.IncludeGlobs.Length > 0 || filter.ExcludeGlobs.Length > 0);
        if (hasGlobs)
        {
            if (filter!.ExcludeGlobs.Length > 0)
                panel.Children.Add(new TextBlock { Text = $"Exclude (this folder): {filter.ExcludeGlobs}", FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap });
            if (filter.IncludeGlobs.Length > 0)
                panel.Children.Add(new TextBlock { Text = $"Re-admit (this folder): {filter.IncludeGlobs}", FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Filters: global excludes only (click Filters\u2026 to add per-folder globs).",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return panel;
    }

    private static StackPanel MakeIndexButtonRow(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    /// <summary>Removes any per-folder filter override registered for <paramref name="root"/> (by canonical path).</summary>
    private static List<IndexedRootFilter> RemoveRootFilter(IEnumerable<IndexedRootFilter>? filters, string root)
    {
        string key = IndexScopeIdentity.NormalizePath(root ?? string.Empty);
        var list = (filters ?? Enumerable.Empty<IndexedRootFilter>())
            .Where(f => f is not null
                && !string.Equals(IndexScopeIdentity.NormalizePath(f.Path ?? string.Empty), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return IndexedRootFilterPolicy.Normalize(list);
    }

    /// <summary>
    /// Edits the selected folder's per-folder build-time glob overrides (plan §6.1). These layer on top of
    /// the global exclude globs: the exclude box adds folder-only excludes, the include box re-admits paths a
    /// broader exclude would drop (gitignore-style negation — e.g. index <c>node_modules</c> under just this
    /// folder). Build-time only, so a change here only affects what a rebuild ingests, never search results.
    /// </summary>
    private async Task ShowRootFilterEditorAsync()
    {
        string root = _indexManageRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            SetIndexStatus("Select a folder first, then edit its filters.");
            return;
        }

        IndexedRootFilter? existing = IndexedRootFilterPolicy.Find(_viewModel.Settings.IndexedRootFilters, root);

        var excludeBox = new TextBox
        {
            PlaceholderText = "e.g. **/bin/**, *.min.js",
            TextWrapping = TextWrapping.Wrap,
            Text = existing?.ExcludeGlobs ?? string.Empty,
        };
        var includeBox = new TextBox
        {
            PlaceholderText = "e.g. **/node_modules/**  (index it here despite a global exclude)",
            TextWrapping = TextWrapping.Wrap,
            Text = existing?.IncludeGlobs ?? string.Empty,
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = $"Per-folder index filters for:\n{root}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 });
        panel.Children.Add(new TextBlock
        {
            Text = "These layer on top of the global exclude globs (Scope & Ingestion). Build-time only — a file left out is still found by a live search. Comma/semicolon separated.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.6,
        });
        panel.Children.Add(new TextBlock { Text = "Exclude globs (added for this folder only):", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(excludeBox);
        panel.Children.Add(new TextBlock { Text = "Include globs (re-admit paths a broader exclude drops):", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(includeBox);

        var result = await YaguDialog.ShowAsync(
            _settingsHwnd,
            new YaguDialogOptions
            {
                Title = "Folder index filters",
                TitleGlyph = "\uE71C", // Filter
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 640,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
        if (result != YaguDialogResult.Primary)
            return;

        string exclude = excludeBox.Text?.Trim() ?? string.Empty;
        string include = includeBox.Text?.Trim() ?? string.Empty;
        string key = IndexScopeIdentity.NormalizePath(root);

        var filters = RemoveRootFilter(_viewModel.Settings.IndexedRootFilters, root);
        if (exclude.Length > 0 || include.Length > 0)
            filters.Add(new IndexedRootFilter { Path = key, IncludeGlobs = include, ExcludeGlobs = exclude });
        _viewModel.Settings.IndexedRootFilters = IndexedRootFilterPolicy.Normalize(filters);

        RefreshIndexedRootsRadios();
        MarkSettingsDirty(requireValueChanges: false);
        SetIndexStatus(exclude.Length > 0 || include.Length > 0
            ? $"Saved per-folder filters for {root}. Rebuild the index to apply."
            : $"Cleared per-folder filters for {root}.");
        YaguLog.For("ContentIndex").LogInformation("User set per-folder index filters for '{Root}' (include='{Include}', exclude='{Exclude}').", root, include, exclude);
    }

    private void SetIndexStatus(string text)
    {
        if (_indexStatusText is not null)
            _indexStatusText.Text = text;
    }

    private void RefreshIndexManagementButtons()
    {
        bool busy = _indexActionInProgress;
        bool master = _viewModel.Settings.EnableContentIndex;
        bool hasFolder = !string.IsNullOrWhiteSpace(_indexManageRoot);
        bool hasScope = !string.IsNullOrWhiteSpace(_indexManageScopeId) || hasFolder;
        IndexStorageStat? selectedStat = FindSelectedStorageStat();
        bool sourceAvailable = selectedStat is not { RootExists: false, RootPath: not null };
        bool redundantChild = hasFolder && FindRegisteredCoveringAncestor(_indexManageRoot) is not null;
        // Build/Rebuild/Validate/Delete all act on the selected folder, so they need one selected.
        if (_indexBuildButton is not null)
            _indexBuildButton.IsEnabled = master && hasFolder && sourceAvailable && !redundantChild && !busy;
        if (_indexRebuildButton is not null)
            _indexRebuildButton.IsEnabled = master && hasFolder && sourceAvailable && !redundantChild && !busy;
        if (_indexRepairButton is not null)
            _indexRepairButton.IsEnabled = master && selectedStat is { CanRepair: true } && !redundantChild && !busy;
        if (_indexValidateButton is not null)
            _indexValidateButton.IsEnabled = hasFolder && sourceAvailable && !busy;
        if (_indexDeleteButton is not null)
            _indexDeleteButton.IsEnabled = hasScope && !busy;
        if (_indexClearButton is not null)
            _indexClearButton.IsEnabled = !busy;
        if (_indexRefreshStatsButton is not null)
            _indexRefreshStatsButton.IsEnabled = !busy;
        foreach (Control action in _indexStorageActionControls)
        {
            bool requiresMaster = action.Tag is true;
            action.IsEnabled = !busy && (!requiresMaster || master);
        }
        if (_indexCancelButton is not null)
            _indexCancelButton.Visibility = busy && _indexBuildCts is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private IndexStorageStat? FindSelectedStorageStat()
    {
        if (_lastIndexStorageSummary is not { } summary)
            return null;
        if (!string.IsNullOrWhiteSpace(_indexManageScopeId))
        {
            foreach (IndexStorageStat stat in summary.Indexes)
            {
                if (string.Equals(stat.ScopeId, _indexManageScopeId, StringComparison.OrdinalIgnoreCase))
                    return stat;
            }
        }
        return string.IsNullOrWhiteSpace(_indexManageRoot) ? null : FindStorageStatForRoot(_indexManageRoot);
    }

    private ContentIndexManager CreateIndexManager()
    {
        var settings = _viewModel.Settings;
        var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
        return new ContentIndexManager(provider, AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount));
    }

    private async Task RunIndexBuildAsync(bool rebuild)
    {
        if (_indexActionInProgress)
            return;
        if (!_viewModel.Settings.EnableContentIndex)
        {
            SetIndexStatus("Enable content indexing (the master switch above) before building.");
            return;
        }
        string root = _indexManageRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            SetIndexStatus("Choose a folder to index first.");
            return;
        }
        if (FindRegisteredCoveringAncestor(root) is { } coveringRoot)
        {
            SetIndexStatus($"{root} is already covered by {coveringRoot}. Yagu maintains and searches with the broader index only; delete the redundant child index instead of rebuilding it.");
            return;
        }

        var coordinator = new IndexBuildCoordinator();
        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(_viewModel.Settings, root, rebuild);
        var cts = new CancellationTokenSource();

        _indexActionInProgress = true;
        _indexBuildCts = cts;
        RefreshIndexManagementButtons();
        SetIndexStatus($"{(rebuild ? "Rebuilding" : "Building")} index for {root}…");
        YaguLog.For("ContentIndex").LogInformation("User requested {Action} of index for '{Root}' (budget {BudgetMb} MB).", rebuild ? "rebuild" : "build", root, _viewModel.Settings.EffectiveIndexBuildMemoryBudgetMB);
        // Surface "Indexing…" in the main-window index indicator while this build runs (even if the user
        // closes the Settings window). Link to the VM's index-build token so a status-bar "Pause indexing"
        // also stops this build.
        _viewModel.BeginIndexBuildActivity(root);
        // Denominator for the main-window "% complete" estimate: the used space of this root's drive.
        long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _viewModel.IndexBuildCancellationToken);

        try
        {
            IndexBuildSuccess result = await coordinator.BuildFullScopePreferWorkerAsync(
                operation,
                _viewModel.Settings.IndexUseNativeWorker,
                linkedCts.Token,
                progress: p => _viewModel.ReportIndexBuildProgress(IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes)),
                pdfProgress: p => _viewModel.ReportIndexBuildProgress(p.Total <= 0 ? -1 : 90 + Math.Clamp(p.Processed * 5 / p.Total, 0, 5)),
                imageOcrProgress: p => _viewModel.ReportIndexBuildProgress(p.Total <= 0 ? -1 : 95 + Math.Clamp(p.Processed * 4 / p.Total, 0, 4)),
                postBuildCatchUpProgress: _ => _viewModel.ReportIndexBuildProgress(99));
            string ocrSummary = string.IsNullOrWhiteSpace(result.ImageOcrStatus)
                ? string.Empty
                : $" Image-text index: {result.ImageOcrStatus} ({result.ImagesAdmitted:N0}/{result.ImagesSeen:N0} images admitted).";
            string catchUpSummary = result.PostBuildCatchUp.Checked
                ? $" {result.PostBuildCatchUp.Describe()}"
                : string.Empty;
            SetIndexStatus($"Index build complete for {root}. {result.Summary}{ocrSummary}{catchUpSummary}");
        }
        catch (OperationCanceledException)
        {
            YaguLog.For("ContentIndex").LogInformation("Index build for '{Root}' was cancelled/paused.", root);
            SetIndexStatus("Build stopped. The previous index (if any) is unchanged.");
        }
        catch (IndexDiskFullException ex)
        {
            YaguLog.For("ContentIndex").LogWarning("Index build for '{Root}' stopped: {Error}", root, ex.Message);
            SetIndexStatus($"Indexing stopped: {ex.DriveDisplayName} is {ex.UsedPercent:F0}% full (limit {ex.ThresholdPercent}%). Free space or raise the limit above.");
            _viewModel.OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
        }
        catch (IndexWriteBusyException)
        {
            SetIndexStatus("Another index build, refresh, validation, or delete operation is already running.");
        }
        catch (DirectoryNotFoundException ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Index build for '{Root}' failed: folder not found.", root);
            SetIndexStatus($"Folder not found: {root}");
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Index build for '{Root}' failed.", root);
            SetIndexStatus($"Build failed: {ex.Message}");
        }
        finally
        {
            _viewModel.EndIndexBuildActivity();
            _indexActionInProgress = false;
            cts.Dispose();
            _indexBuildCts = null;
            RefreshIndexManagementButtons();
            _ = RefreshIndexStorageStatsAsync();
        }
    }

    private async Task RunIndexValidateAsync()
    {
        if (_indexActionInProgress)
            return;
        string root = _indexManageRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            SetIndexStatus("Choose a folder to validate first.");
            return;
        }

        var coordinator = new IndexBuildCoordinator();
        IndexValidationOperation operation = IndexBuildOperationFactory.CreateValidation(_viewModel.Settings, root);
        _indexActionInProgress = true;
        RefreshIndexManagementButtons();
        SetIndexStatus($"Validating index for {root}…");
        try
        {
            IndexValidationResult result = await coordinator.ValidatePreferWorkerAsync(
                operation, _viewModel.Settings.IndexUseNativeWorker, CancellationToken.None);
            SetIndexStatus(result.Valid
                ? $"Valid index for {root}: {result.DocumentCount:N0} stored content records across base + {result.SegmentCount:N0} segment(s)."
                : $"No valid index for {root} — {result.FailureReason} Searches live-scan this folder.");
        }
        catch (IndexWriteBusyException)
        {
            SetIndexStatus("Another index operation is running; validate again after it finishes.");
        }
        catch (Exception ex)
        {
            SetIndexStatus($"Validate failed: {ex.Message}");
        }
        finally
        {
            _indexActionInProgress = false;
            RefreshIndexManagementButtons();
        }
    }

    private async Task RunIndexRepairAsync()
    {
        if (_indexActionInProgress)
            return;
        IndexStorageStat? selected = FindSelectedStorageStat();
        if (selected is not { CanRepair: true } stat || string.IsNullOrWhiteSpace(stat.RootPath))
        {
            SetIndexStatus("Select an index marked as needing repair. If its source folder is missing or unidentified, delete the stored index instead.");
            return;
        }
        if (FindRegisteredCoveringAncestor(stat.RootPath) is { } coveringRoot)
        {
            SetIndexStatus($"{stat.RootPath} is already covered by {coveringRoot}. Delete this redundant child index instead of repairing a duplicate.");
            return;
        }

        string root = stat.RootPath;
        var confirm = await YaguDialog.ShowAsync(
            _settingsHwnd,
            new YaguDialogOptions
            {
                Title = "Repair content index",
                TitleGlyph = "\uE895", // Sync / rebuild
                Content = $"Repair the content index for:\n{root}\n\n{stat.Problem}\n\nYagu will rebuild it in the isolated worker and keep the current stored data untouched until the replacement is complete and validated.",
                PrimaryButtonText = "Repair now",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 640,
                Height = 340,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
        if (confirm != YaguDialogResult.Primary)
            return;

        _indexManageRoot = root;
        _indexManageScopeId = stat.ScopeId;
        await RunIndexBuildAsync(rebuild: true);
    }

    private async Task RunIndexDeleteAsync()
    {
        if (_indexActionInProgress)
            return;
        string root = _indexManageRoot;
        string scopeId = !string.IsNullOrWhiteSpace(_indexManageScopeId)
            ? _indexManageScopeId
            : !string.IsNullOrWhiteSpace(root)
                ? ContentIndexManager.ScopeIdForRoot(root)
                : string.Empty;
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            SetIndexStatus("Choose a folder or an unidentified stored index to delete first.");
            return;
        }

        string target = !string.IsNullOrWhiteSpace(root) ? root : $"unidentified scope {scopeId}";
        string explanation = !string.IsNullOrWhiteSpace(root)
            ? "Stored postings for that folder are removed. Searches live-scan it until it is rebuilt."
            : "The original source folder could not be recovered from trustworthy metadata. Only this stored scope data is removed.";

        var confirm = await YaguDialog.ShowAsync(
            _settingsHwnd,
            new YaguDialogOptions
            {
                Title = "Delete index",
                TitleGlyph = "\uE74D", // Delete
                Content = $"Delete the content index for:\n{target}\n\n{explanation}",
                PrimaryButtonText = "Delete index",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 600,
                Height = 300,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
        if (confirm != YaguDialogResult.Primary)
            return;

        var manager = CreateIndexManager();
        _indexActionInProgress = true;
        RefreshIndexManagementButtons();
        try
        {
            bool existed = await Task.Run(() => manager.DeleteScope(scopeId));
            SetIndexStatus(existed ? $"Deleted the index for {target}." : $"No index existed for {target}.");
        }
        catch (IndexWriteBusyException)
        {
            SetIndexStatus("Another index operation is running; delete after it finishes.");
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Deleting the index for '{Target}' (scope {Scope}) failed.", target, scopeId);
            SetIndexStatus($"Delete failed: {ex.Message}");
        }
        finally
        {
            _indexActionInProgress = false;
            RefreshIndexManagementButtons();
            _ = RefreshIndexStorageStatsAsync();
            _viewModel.RefreshAllDriveIndexStatus();
        }
    }

    private async Task RunIndexClearAllAsync()
    {
        if (_indexActionInProgress)
            return;

        var confirm = await YaguDialog.ShowAsync(
            _settingsHwnd,
            new YaguDialogOptions
            {
                Title = "Clear all indexes",
                TitleGlyph = "\uE74D", // Delete
                Content = "Delete ALL content index data for every folder. This cannot be undone; indexes must be rebuilt afterwards. Your indexing settings are unchanged.",
                PrimaryButtonText = "Clear everything",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 600,
                Height = 300,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
        if (confirm != YaguDialogResult.Primary)
            return;

        var manager = CreateIndexManager();
        _indexActionInProgress = true;
        RefreshIndexManagementButtons();
        try
        {
            int count = await Task.Run(() => manager.ClearAll());
            SetIndexStatus(count == 0 ? "No index data to clear." : $"Cleared {count} folder index(es).");
        }
        catch (IndexWriteBusyException)
        {
            SetIndexStatus("Another index operation is running; clear indexes after it finishes.");
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Clearing all index data failed.");
            SetIndexStatus($"Clear failed: {ex.Message}");
        }
        finally
        {
            _indexActionInProgress = false;
            RefreshIndexManagementButtons();
            _ = RefreshIndexStorageStatsAsync();
            _viewModel.RefreshAllDriveIndexStatus();
        }
    }

    private void OpenIndexStorageLocation()
    {
        try
        {
            var provider = DefaultContentIndexPathProvider.Create(_viewModel.Settings.IndexStorageDirectory);
            Directory.CreateDirectory(provider.IndexRoot);
            Process.Start(new ProcessStartInfo { FileName = provider.IndexRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetIndexStatus($"Could not open storage location: {ex.Message}");
        }
    }
}
