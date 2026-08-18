using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Logging;

namespace Yagu;

/// <summary>
/// Explains, and fixes, the "this index stopped updating because it hit its storage budget" state.
/// <para>
/// The condition is easy to miss: the index is structurally healthy and searches stay complete, so
/// nothing looks broken — it simply stops being maintained and quietly gets less useful. This dialog
/// says that in plain language, closes as soon as the user chooses a remedy, and hands any ongoing
/// work to the main-window index status indicator so the rest of the UI remains usable.
/// </para>
/// </summary>
public sealed partial class MainWindow
{
    // One prompt per root per session: the condition persists across every maintenance pass, so without
    // this the user would be interrupted repeatedly by a dialog they already answered.
    private readonly HashSet<string> _indexSizeBudgetPrompted = new(StringComparer.OrdinalIgnoreCase);

    // Several roots can hit their budget in the same health refresh. Detection is per-root and
    // fire-and-forget, so without a queue each notification would pass the "a modal is already open"
    // check before any of them had actually opened, and the dialogs would stack on top of each other.
    private readonly Queue<string> _indexSizeBudgetQueue = new();
    private bool _indexSizeBudgetPumping;

    /// <summary>Queues <paramref name="indexRoot"/> and shows the queued dialogs one at a time.</summary>
    private async Task ShowIndexSizeBudgetDialogAsync(string indexRoot, bool fromUserAction)
    {
        if (string.IsNullOrWhiteSpace(indexRoot))
            return;
        if (!fromUserAction && !_indexSizeBudgetPrompted.Add(IndexScopeIdentity.NormalizePath(indexRoot)))
            return;

        _indexSizeBudgetQueue.Enqueue(indexRoot);
        if (_indexSizeBudgetPumping)
            return;

        _indexSizeBudgetPumping = true;
        try
        {
            while (_indexSizeBudgetQueue.Count > 0)
                await ShowOneIndexSizeBudgetDialogAsync(_indexSizeBudgetQueue.Dequeue()).ConfigureAwait(true);
        }
        finally
        {
            _indexSizeBudgetPumping = false;
        }
    }

    /// <summary>Shows the remediation dialog for one root, then refreshes health.</summary>
    private async Task ShowOneIndexSizeBudgetDialogAsync(string indexRoot)
    {
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        HideIndexStatusHoverOverlay();

        IndexSizeBudgetDiagnosis diagnosis = await Task.Run(
            () => DiagnoseIndexSizeBudget(indexRoot)).ConfigureAwait(true);
        if (!diagnosis.AtBudget)
        {
            // The index may still be updating yet be unable to shed its accumulated history; that is a
            // different problem with different wording and different fixes.
            IndexReclamationDiagnosis reclamation = await Task.Run(
                () => DiagnoseIndexReclamation(indexRoot)).ConfigureAwait(true);
            if (reclamation.ReclamationBlocked)
                await ShowIndexSizeAttentionDialogAsync(indexRoot, reclamation).ConfigureAwait(true);
            return;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = indexRoot,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = diagnosis.Explain(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = diagnosis.ExplainWhyAutomaticCleanupFailed(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose what to do:",
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
        });

        YaguDialog? dialog = null;
        foreach (IndexSizeBudgetRemedy remedy in diagnosis.Remedies())
        {
            IndexSizeBudgetRemedy captured = remedy;
            var action = new Button
            {
                Content = IndexSizeBudgetAdvisor.RemedyLabel(remedy),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 5, 14, 5),
            };
            action.Click += async (_, _) =>
            {
                dialog?.AcceptClose();
                await ApplyIndexSizeBudgetRemedyAsync(indexRoot, captured, diagnosis).ConfigureAwait(true);
            };

            panel.Children.Add(action);
            panel.Children.Add(new TextBlock
            {
                Text = IndexSizeBudgetAdvisor.RemedyDescription(remedy, diagnosis),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "This index has stopped updating",
                Content = panel,
                PrimaryButtonText = null,
                CloseButtonText = "Decide later",
                RequestedTheme = RootGrid.ActualTheme,
                Width = 640,
                Height = 620,
                MaxContentHeight = 560,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                TitleGlyph = "\uE7BA", // Warning
            },
            d => dialog = d);

        ViewModel.RefreshAllDriveIndexStatus();
    }

    /// <summary>Measures the index and describes its budget state. Runs off the UI thread.</summary>
    private IndexSizeBudgetDiagnosis DiagnoseIndexSizeBudget(string indexRoot)
    {
        try
        {
            AppSettings settings = ViewModel.Settings;
            var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
            var manager = new ContentIndexManager(
                provider, AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount));
            return IndexSizeBudgetAdvisor.Diagnose(
                IndexSizeManagementPolicy.Resolve(settings, indexRoot),
                manager.GetActiveIndexBytesForRoot(indexRoot));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Could not diagnose the index size budget for '{Root}'.", indexRoot);
            return IndexSizeBudgetDiagnosis.Healthy;
        }
    }

    /// <summary>Measures what the index is made of and whether its history can still be reclaimed.</summary>
    private IndexReclamationDiagnosis DiagnoseIndexReclamation(string indexRoot)
    {
        try
        {
            AppSettings settings = ViewModel.Settings;
            var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
            var manager = new ContentIndexManager(
                provider, AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount));
            return manager.DiagnoseReclamation(
                indexRoot,
                IndexSizeManagementPolicy.Resolve(settings, indexRoot),
                AppSettings.NormalizeIndexMaxDeltaSegments(settings.IndexMaxDeltaSegments),
                AppSettings.NormalizeIndexCompactionThresholdMB(settings.IndexCompactionThresholdMB));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Could not diagnose index reclamation for '{Root}'.", indexRoot);
            return IndexReclamationDiagnosis.Healthy;
        }
    }

    /// <summary>
    /// Explains an index that is <b>still updating</b> but can no longer reclaim the history those updates
    /// leave behind, and applies the chosen fix. Deliberately does not offer the legacy "allow compaction"
    /// action: that persisted an uncapped setting for the index and let a later background pass attempt an
    /// unbounded fold. Compacting here is a one-shot, user-approved worker operation instead.
    /// </summary>
    private async Task ShowIndexSizeAttentionDialogAsync(string indexRoot, IndexReclamationDiagnosis diagnosis)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = indexRoot,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = diagnosis.Explain(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = diagnosis.ExplainWhyAutomaticCleanupIsUnavailable(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose what to do:",
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
        });

        YaguDialog? dialog = null;
        foreach (IndexSizeAttentionRemedy remedy in diagnosis.Remedies())
        {
            IndexSizeAttentionRemedy captured = remedy;
            var action = new Button
            {
                Content = IndexReclamationAdvisor.RemedyLabel(remedy),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 5, 14, 5),
            };
            action.Click += async (_, _) =>
            {
                dialog?.AcceptClose();
                await ApplyIndexSizeAttentionRemedyAsync(indexRoot, captured).ConfigureAwait(true);
            };

            panel.Children.Add(action);
            panel.Children.Add(new TextBlock
            {
                Text = IndexReclamationAdvisor.RemedyDescription(remedy, diagnosis),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "This index is still updating, but it cannot be cleaned up",
                Content = panel,
                PrimaryButtonText = null,
                CloseButtonText = "Decide later",
                RequestedTheme = RootGrid.ActualTheme,
                Width = 640,
                Height = 640,
                MaxContentHeight = 580,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                TitleGlyph = "\uE7BA", // Warning
            },
            d => dialog = d);

        ViewModel.RefreshAllDriveIndexStatus();
    }

    private async Task ApplyIndexSizeAttentionRemedyAsync(
        string indexRoot,
        IndexSizeAttentionRemedy remedy)
    {
        try
        {
            switch (remedy)
            {
                case IndexSizeAttentionRemedy.CompactNow:
                    await CompactIndexNowAsync(indexRoot).ConfigureAwait(true);
                    break;

                case IndexSizeAttentionRemedy.Delete:
                    await DeleteIndexInBackgroundAsync(indexRoot).ConfigureAwait(true);
                    break;

                case IndexSizeAttentionRemedy.ReviewSizeSettings:
                    OpenSettingsToIndexingTab();
                    break;

                case IndexSizeAttentionRemedy.Rebuild:
                    ViewModel.RebuildRegisteredIndexNow(indexRoot);
                    break;
            }

            YaguLog.For("ContentIndex").LogInformation(
                "User applied size-attention remedy {Remedy} to '{Root}'.", remedy, indexRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Size-attention remedy {Remedy} failed for '{Root}'.", remedy, indexRoot);
        }
        finally
        {
            ViewModel.RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>
    /// Runs one explicit compaction of <paramref name="indexRoot"/> in the short-lived maintenance worker.
    /// It never persists an "uncapped" setting — the cap only governs automatic passes.
    /// </summary>
    private async Task CompactIndexNowAsync(string indexRoot)
    {
        AppSettings settings = ViewModel.Settings;
        IndexMaintenanceOperation operation = IndexBuildOperationFactory.CreateMaintenance(
            settings, new[] { indexRoot }, IndexMaintenanceOperation.ModeCompactOnly, rebuildWhenDirty: false);
        var coordinator = new IndexBuildCoordinator();
        ViewModel.BeginIndexBuildActivity(indexRoot, isIncremental: true);
        ViewModel.ReportIndexBuildProgress(indexRoot, 2, IndexUpdateStages.CompactAnalyzing);
        try
        {
            IndexMaintenanceSuccess result = await coordinator.RunMaintenancePreferWorkerAsync(
                operation,
                settings.IndexUseNativeWorker,
                ViewModel.IndexBuildCancellationToken,
                (root, percent, stage) => DispatcherQueue.TryEnqueue(
                    () => ViewModel.ReportIndexBuildProgress(root, percent, stage))).ConfigureAwait(true);
            await ViewModel.RecordAutomaticCompactionMaintenanceResultsAsync(
                result.Roots,
                DateTimeOffset.UtcNow).ConfigureAwait(true);
            if (result.Roots.Any(static root => root.Action == IndexMaintenanceActions.Failed))
                throw new IOException("The index could not be compacted; the existing index remains valid.");
        }
        finally
        {
            ViewModel.EndIndexBuildActivity();
        }
    }

    /// <summary>
    /// Applies one remedy after the warning dialog has closed. Long-running work reports through the
    /// bottom-right index status indicator; settings-only remedies refresh that status when persisted.
    /// </summary>
    private async Task ApplyIndexSizeBudgetRemedyAsync(
        string indexRoot,
        IndexSizeBudgetRemedy remedy,
        IndexSizeBudgetDiagnosis diagnosis)
    {
        try
        {
            switch (remedy)
            {
                case IndexSizeBudgetRemedy.RaiseBudget:
                    await SetIndexSizeOverrideAsync(indexRoot, budgetMB: diagnosis.SuggestedBudgetMB).ConfigureAwait(true);
                    break;

                case IndexSizeBudgetRemedy.AllowCompaction:
                    // 0 = no cap, so the next pass may fold this index regardless of its size.
                    await SetIndexSizeOverrideAsync(indexRoot, compactionCapMB: 0).ConfigureAwait(true);
                    break;

                case IndexSizeBudgetRemedy.Delete:
                    await DeleteIndexInBackgroundAsync(indexRoot).ConfigureAwait(true);
                    break;

                case IndexSizeBudgetRemedy.Rebuild:
                    ViewModel.RebuildRegisteredIndexNow(indexRoot);
                    break;
            }

            YaguLog.For("ContentIndex").LogInformation(
                "User applied size-budget remedy {Remedy} to '{Root}'.", remedy, indexRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Size-budget remedy {Remedy} failed for '{Root}'.", remedy, indexRoot);
        }
        finally
        {
            ViewModel.RefreshAllDriveIndexStatus();
        }
    }

    private async Task DeleteIndexInBackgroundAsync(string indexRoot)
    {
        ViewModel.BeginIndexBuildActivity(indexRoot, isIncremental: true);
        ViewModel.ReportIndexBuildProgress(indexRoot, -1, IndexUpdateStages.Deleting);
        try
        {
            await Task.Run(() => DeleteIndexForRoot(indexRoot)).ConfigureAwait(true);
        }
        finally
        {
            ViewModel.EndIndexBuildActivity();
        }
    }

    /// <summary>Pins one axis of this index's size policy, leaving the rest inherited, and persists it.</summary>
    private async Task SetIndexSizeOverrideAsync(string indexRoot, int budgetMB = -1, int compactionCapMB = -1)
    {
        AppSettings settings = ViewModel.Settings;
        IndexedRootSizePolicy? existing = IndexSizeManagementPolicy.Find(settings.IndexedRootSizePolicies, indexRoot);
        settings.IndexedRootSizePolicies = IndexSizeManagementPolicy.Set(
            settings.IndexedRootSizePolicies,
            new IndexedRootSizePolicy
            {
                Path = IndexScopeIdentity.NormalizePath(indexRoot),
                Mode = existing?.Mode ?? string.Empty,
                SizeBudgetMB = budgetMB >= 0 ? budgetMB : existing?.SizeBudgetMB ?? -1,
                MaxAutoCompactionSizeMB = compactionCapMB >= 0 ? compactionCapMB : existing?.MaxAutoCompactionSizeMB ?? -1,
            });
        await ViewModel.PersistSettingsAsync().ConfigureAwait(true);
    }

    private bool DeleteIndexForRoot(string indexRoot)
    {
        AppSettings settings = ViewModel.Settings;
        var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
        var manager = new ContentIndexManager(
            provider, AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount));
        return manager.DeleteScope(ContentIndexManager.ScopeIdForRoot(indexRoot));
    }
}
