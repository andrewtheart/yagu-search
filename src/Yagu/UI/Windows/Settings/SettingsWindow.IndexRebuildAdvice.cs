using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Yagu.Services.Index;
using Yagu.Services.Logging;

namespace Yagu;

public sealed partial class SettingsWindow
{
    /// <summary>
    /// Saves all settings, advances the clean snapshot, and then offers an explicit rebuild only when the
    /// saved changes affect persisted index build output. Returns true when Settings must remain open (the
    /// user chose to review Indexing while rebuilding is currently unavailable).
    /// </summary>
    private async Task<bool> SaveSettingsAndOfferIndexRebuildAsync()
    {
        ContentIndexSettingsSnapshot before = _cleanContentIndexSettings
            ?? ContentIndexSettingsChangeAdvisor.Capture(_viewModel.Settings);
        ContentIndexSettingsSnapshot after = ContentIndexSettingsChangeAdvisor.Capture(_viewModel.Settings);
        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(before, after);

        await _viewModel.PersistSettingsAsync();
        MarkSettingsClean();

        if (!advice.HasRecommendation)
            return false;

        return await OfferIndexRebuildAfterSettingsChangeAsync(advice);
    }

    private async Task<bool> OfferIndexRebuildAfterSettingsChangeAsync(ContentIndexSettingsChangeAdvice advice)
    {
        bool canRebuildNow = _viewModel.Settings.EnableContentIndex && !_indexActionInProgress;
        bool replacesActiveBuild = canRebuildNow && _viewModel.IsIndexBuildActive;
        string action = canRebuildNow
            ? replacesActiveBuild
                ? advice.AffectedRoots.Count == 1 ? "Stop and rebuild now" : $"Stop and rebuild all {advice.AffectedRoots.Count}"
                : advice.AffectedRoots.Count == 1 ? "Rebuild now" : $"Rebuild all {advice.AffectedRoots.Count} now"
            : "Review Indexing";

        YaguDialogResult result = await YaguDialog.ShowAsync(
            _settingsHwnd,
            new YaguDialogOptions
            {
                Title = advice.AffectedRoots.Count == 1
                    ? "Index rebuild recommended"
                    : $"Rebuild {advice.AffectedRoots.Count} indexes?",
                TitleGlyph = "\uE895", // Sync / rebuild
                Content = BuildIndexRebuildRecommendationText(advice, canRebuildNow, replacesActiveBuild),
                PrimaryButtonText = action,
                SecondaryButtonText = "Later",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Secondary,
                RequestedTheme = RootGrid.ActualTheme,
                Width = 700,
                Height = 500,
                MaxContentHeight = 360,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });

        if (result != YaguDialogResult.Primary)
        {
            SetIndexStatus($"Saved. Rebuild recommended for {advice.AffectedRoots.Count} maintained index(es).");
            return false;
        }

        SelectTabByHeader("Indexing");
        _indexManageRoot = advice.AffectedRoots[0];
        _indexManageScopeId = string.Empty;
        RefreshIndexedRootsRadios();

        if (!canRebuildNow)
        {
            SetIndexStatus(_viewModel.Settings.EnableContentIndex
                ? "Another index operation is active. Rebuild the affected folders when it finishes."
                : "Content indexing is disabled. Enable it, then rebuild the affected folders to apply these settings.");
            return true;
        }

        await RunRecommendedIndexRebuildsAsync(advice.AffectedRoots);
        return false;
    }

    private static string BuildIndexRebuildRecommendationText(
        ContentIndexSettingsChangeAdvice advice,
        bool canRebuildNow,
        bool replacesActiveBuild)
    {
        var text = new StringBuilder();
        text.AppendLine("The saved changes affect what index builds store:");
        foreach (ContentIndexRebuildReason reason in advice.Reasons)
            text.Append("\u2022 ").AppendLine(reason.Description);

        text.AppendLine();
        text.AppendLine(advice.AffectedRoots.Count == 1 ? "Affected maintained folder:" : "Affected maintained folders:");
        const int MaxDisplayedRoots = 8;
        foreach (string root in advice.AffectedRoots.Take(MaxDisplayedRoots))
            text.Append("\u2022 ").AppendLine(root);
        if (advice.AffectedRoots.Count > MaxDisplayedRoots)
            text.Append("\u2022 \u2026 and ").Append(advice.AffectedRoots.Count - MaxDisplayedRoots).AppendLine(" more");

        text.AppendLine();
        text.Append("Search results remain correct before rebuilding: files not represented by the old index are read live, ")
            .Append("and missing optional format-v3 query files safely fall back to live scanning. Rebuilding updates acceleration coverage and uses a staged, ")
            .Append("atomic replacement, but a large folder can take substantial time.");
        if (replacesActiveBuild)
        {
            text.Append(" The current indexing operation will stop first. Its incomplete staged work will be discarded, ")
                .Append("and the previous complete index remains active until the replacement commits.");
        }
        if (canRebuildNow && advice.AffectedRoots.Count > 1)
            text.Append(" The folders will rebuild sequentially and the operation can be cancelled from this tab.");
        return text.ToString();
    }

    /// <summary>Runs the explicitly-approved rebuilds sequentially under the existing single-writer protocol.</summary>
    private async Task RunRecommendedIndexRebuildsAsync(IReadOnlyList<string> requestedRoots)
    {
        string[] roots = IndexedRootsPolicy.Normalize(requestedRoots)
            .Where(Directory.Exists)
            .ToArray();
        if (roots.Length == 0)
        {
            SetIndexStatus("None of the affected source folders is currently available. Rebuild when the folders are online.");
            return;
        }

        var coordinator = new IndexBuildCoordinator();
        using var cts = new CancellationTokenSource();

        _indexActionInProgress = true;
        _indexBuildCts = cts;
        RefreshIndexManagementButtons();

        int completed = 0;
        int failed = requestedRoots.Count - roots.Length;
        bool cancelled = false;
        bool drainTimedOut = false;
        bool rebuildActivityStarted = false;
        try
        {
            if (_viewModel.IsIndexBuildActive)
            {
                SetIndexStatus("Stopping the current index operation before rebuilding with the saved settings...");
                drainTimedOut = !await _viewModel.CancelActiveIndexBuildForReplacementAsync(cts.Token);
            }

            if (!drainTimedOut)
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token,
                    _viewModel.IndexBuildCancellationToken);
                _viewModel.BeginIndexBuildActivity(roots[0]);
                rebuildActivityStarted = true;

                for (int i = 0; i < roots.Length; i++)
                {
                    string root = roots[i];
                    linkedCts.Token.ThrowIfCancellationRequested();
                    _indexManageRoot = root;
                    _indexManageScopeId = string.Empty;
                    RefreshIndexedRootsRadios();
                    SetIndexStatus($"Rebuilding {i + 1} of {roots.Length}: {root}");
                    _viewModel.ReportIndexBuildProgress(root, -1);
                    long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);

                    try
                    {
                        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(
                            _viewModel.Settings,
                            root,
                            rebuild: true);
                        await coordinator.BuildFullScopePreferWorkerAsync(
                            operation,
                            _viewModel.Settings.IndexUseNativeWorker,
                            linkedCts.Token,
                            progress: progress => _viewModel.ReportIndexBuildProgress(
                                root,
                                IndexBuildProgressEstimate.Percent(progress.BytesCrawled, driveUsedBytes),
                                IndexBuildStages.RawBuild),
                            pdfProgress: progress => _viewModel.ReportIndexBuildProgress(
                                root,
                                progress.Total <= 0 ? -1 : 90 + Math.Clamp(progress.Processed * 5 / progress.Total, 0, 5),
                                IndexBuildStages.Pdf),
                            imageOcrProgress: progress => _viewModel.ReportIndexBuildProgress(
                                root,
                                progress.Total <= 0 ? -1 : 95 + Math.Clamp(progress.Processed * 4 / progress.Total, 0, 4),
                                IndexBuildStages.Ocr),
                            postBuildCatchUpProgress: _ => _viewModel.ReportIndexBuildProgress(
                                root, 99, IndexBuildStages.PostBuildCatchUp));
                        completed++;
                    }
                    catch (IndexDiskFullException ex)
                    {
                        failed++;
                        _viewModel.OnIndexBuildStoppedForDiskSpace(
                            ex.DriveDisplayName,
                            ex.UsedPercent,
                            ex.ThresholdPercent);
                        YaguLog.For("ContentIndex").LogWarning(
                            "Recommended rebuild for '{Root}' stopped because the index drive reached its disk limit.",
                            root);
                        break;
                    }
                    catch (IndexWriteBusyException)
                    {
                        failed++;
                        SetIndexStatus("Another index operation acquired the writer. Retry the remaining rebuilds when it finishes.");
                        break;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        failed++;
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        failed++;
                        YaguLog.For("ContentIndex").LogWarning(
                            ex,
                            "Recommended rebuild failed for '{Root}'; continuing with remaining roots.",
                            root);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            if (rebuildActivityStarted)
                _viewModel.EndIndexBuildActivity();
            _indexActionInProgress = false;
            _indexBuildCts = null;
            RefreshIndexManagementButtons();
            _viewModel.RefreshAllDriveIndexStatus();
            await RefreshIndexStorageStatsAsync();
        }

        SetIndexStatus(drainTimedOut
            ? "The current index operation did not stop in time, so nothing was rebuilt. Try again once it finishes."
            : cancelled
                ? $"Rebuild cancelled after {completed} of {roots.Length} folder(s)."
                : failed == 0
                    ? $"Rebuilt {completed} affected index(es) with the saved settings."
                    : $"Rebuilt {completed}; {failed} affected folder(s) could not be rebuilt.");
    }
}
