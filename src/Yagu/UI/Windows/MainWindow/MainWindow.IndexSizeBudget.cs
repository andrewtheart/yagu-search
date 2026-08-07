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
/// says that in plain language and applies the chosen fix in place, reporting the outcome inline
/// rather than sending the user off to Settings.
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
            return;

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
            Visibility = Visibility.Collapsed,
        };
        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Visibility = Visibility.Collapsed,
        };

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

        var buttons = new List<Button>();
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
            action.Click += async (_, _) => await ApplyIndexSizeBudgetRemedyAsync(
                indexRoot, captured, diagnosis, buttons, status, progress, () => dialog?.Close());
            buttons.Add(action);

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

        panel.Children.Add(progress);
        panel.Children.Add(status);

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

    /// <summary>
    /// Applies one remedy without leaving the dialog. The settings-only remedies finish in place and
    /// report inline; a rebuild hands off to the existing full-window build overlay, which is itself a
    /// live, cancellable progress surface.
    /// </summary>
    private async Task ApplyIndexSizeBudgetRemedyAsync(
        string indexRoot,
        IndexSizeBudgetRemedy remedy,
        IndexSizeBudgetDiagnosis diagnosis,
        IReadOnlyList<Button> buttons,
        TextBlock status,
        ProgressBar progress,
        Action closeDialog)
    {
        foreach (Button button in buttons)
            button.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        status.Visibility = Visibility.Visible;
        status.Text = "Working…";

        try
        {
            switch (remedy)
            {
                case IndexSizeBudgetRemedy.RaiseBudget:
                    await SetIndexSizeOverrideAsync(indexRoot, budgetMB: diagnosis.SuggestedBudgetMB).ConfigureAwait(true);
                    status.Text = $"Limit raised to {diagnosis.SuggestedBudgetMB:N0} MB. This index will start updating again at the next maintenance pass.";
                    break;

                case IndexSizeBudgetRemedy.AllowCompaction:
                    // 0 = no cap, so the next pass may fold this index regardless of its size.
                    await SetIndexSizeOverrideAsync(indexRoot, compactionCapMB: 0).ConfigureAwait(true);
                    status.Text = "Compaction enabled for this index. It will be compacted at the next maintenance pass, which briefly uses a lot of memory.";
                    break;

                case IndexSizeBudgetRemedy.Delete:
                    bool deleted = await Task.Run(() => DeleteIndexForRoot(indexRoot)).ConfigureAwait(true);
                    status.Text = deleted
                        ? "Index deleted. Searches read every file directly until you build it again."
                        : "There was nothing stored to delete.";
                    ViewModel.RefreshAllDriveIndexStatus();
                    break;

                case IndexSizeBudgetRemedy.Rebuild:
                    status.Text = "Starting the rebuild…";
                    // Close this dialog first: the rebuild shows its own full-window progress overlay.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        DispatcherQueue.TryEnqueue(async void () =>
                        {
                            try { await ViewModel.RebuildCurrentIndexBlockingAsync(new[] { indexRoot }); }
                            catch (Exception ex)
                            {
                                YaguLog.For("ContentIndex").LogWarning(ex, "Size-budget rebuild failed for '{Root}'.", indexRoot);
                            }
                        });
                    });
                    closeDialog();
                    return;
            }

            YaguLog.For("ContentIndex").LogInformation(
                "User applied size-budget remedy {Remedy} to '{Root}'.", remedy, indexRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Size-budget remedy {Remedy} failed for '{Root}'.", remedy, indexRoot);
            status.Text = $"That did not work: {ex.Message}";
            foreach (Button button in buttons)
                button.IsEnabled = true;
        }
        finally
        {
            progress.Visibility = Visibility.Collapsed;
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
