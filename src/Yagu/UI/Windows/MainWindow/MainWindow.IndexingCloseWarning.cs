using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Yagu.Services.Index;
using Yagu.Services.Logging;

namespace Yagu;

public sealed partial class MainWindow
{
    private const uint WmQueryEndSession = 0x0011;
    private static readonly TimeSpan ApplicationExitGracePeriod = TimeSpan.FromSeconds(5);
    private bool _indexingCloseWarningOpen;
    private bool _applicationExitInProgress;
    private bool IsIndexOperationActive => ViewModel.IsIndexBuildActive || ViewModel.IsIndexRebuildBlocking;
    private bool IsOwnedOperationActive => IsIndexOperationActive || ViewModel.IsSearchActive
        || ViewModel.IsTranslatingSemanticQuery || ViewModel.IsIndexWarmActive;

    private void RequestApplicationExit(
        IndexingCloseTrigger trigger,
        IndexingCloseWarningContent? capturedWarning = null)
        => DispatcherQueue.TryEnqueue(async () => await RequestApplicationExitAsync(trigger, capturedWarning));

    private async Task RequestApplicationExitAsync(
        IndexingCloseTrigger trigger,
        IndexingCloseWarningContent? capturedWarning)
    {
        if (_applicationExitInProgress)
            return;
        if (!await ConfirmExitWhileIndexingAsync(trigger, capturedWarning))
            return;

        await CompleteApplicationExitAsync();
    }

    private async Task CompleteApplicationExitAsync()
    {
        if (_applicationExitInProgress)
            return;

        _applicationExitInProgress = true;
        ViewModel.StatusText = "Closing Yagu…";
        try
        {
            bool graceful = await ViewModel.PrepareForShutdownAsync(ApplicationExitGracePeriod).ConfigureAwait(true);
            if (!graceful)
            {
                YaguLog.For("MainWindow").LogWarning(
                    "Application shutdown grace period elapsed; forcing final process-backed cleanup.");
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("MainWindow").LogWarning(ex,
                "Application shutdown cleanup failed; forcing final process-backed cleanup.");
        }
        finally
        {
            _forceClose = true;
            Close();
        }
    }

    private async Task<bool> ConfirmExitWhileIndexingAsync(
        IndexingCloseTrigger trigger,
        IndexingCloseWarningContent? capturedWarning = null)
    {
        if (capturedWarning is null && !IsIndexOperationActive)
            return true;
        if (_indexingCloseWarningOpen)
        {
            RestoreWindowFromTray();
            return false;
        }

        _indexingCloseWarningOpen = true;
        try
        {
            RestoreWindowFromTray();
            IndexingCloseWarningContent warning = capturedWarning ?? IndexingCloseWarning.Build(
                    trigger,
                    ViewModel.IsActiveIndexBuildIncremental,
                    ViewModel.ActiveIndexBuildFolder);
            var message = new TextBlock
            {
                Text = warning.Message,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                MinWidth = 420,
                MaxWidth = 620,
            };

            YaguDialogResult result = await YaguDialog.ShowAsync(_hwnd, new YaguDialogOptions
            {
                Title = warning.Title,
                TitleGlyph = "\uE7BA",
                Content = message,
                PrimaryButtonText = warning.KeepOpenButtonText,
                SecondaryButtonText = warning.ExitButtonText,
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 700,
                Height = 390,
                MaxContentHeight = 250,
            });

            if (result == YaguDialogResult.Secondary)
                return true;

            ViewModel.StatusText = trigger == IndexingCloseTrigger.WindowsSessionEnding
                ? "Windows shutdown was cancelled; Yagu remains open and indexing continues."
                : "Yagu remains open and indexing continues.";
            return false;
        }
        finally
        {
            _indexingCloseWarningOpen = false;
        }
    }

    private bool TryBlockWindowsSessionEnd()
    {
        if (_forceClose || !IsOwnedOperationActive)
            return false;

        IndexingCloseWarningContent warning = IsIndexOperationActive
            ? IndexingCloseWarning.Build(
                IndexingCloseTrigger.WindowsSessionEnding,
                ViewModel.IsActiveIndexBuildIncremental,
                ViewModel.ActiveIndexBuildFolder)
            : new IndexingCloseWarningContent(
                "Windows requested shutdown while Yagu is busy",
                "Windows requested a restart, shutdown, or sign-out. Yagu stopped that request so it can cancel active work and clean up temporary files safely. "
                    + "Choose Exit Yagu, then retry the Windows operation after Yagu closes.",
                "Keep Yagu open",
                "Exit Yagu");
        RequestApplicationExit(IndexingCloseTrigger.WindowsSessionEnding, warning);
        return true;
    }
}
