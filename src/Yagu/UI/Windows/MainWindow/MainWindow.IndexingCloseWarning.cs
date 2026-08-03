using Microsoft.UI.Xaml.Controls;
using Yagu.Services.Index;

namespace Yagu;

public sealed partial class MainWindow
{
    private const uint WmQueryEndSession = 0x0011;
    private bool _indexingCloseWarningOpen;

    private void RequestApplicationExit(
        IndexingCloseTrigger trigger,
        IndexingCloseWarningContent? capturedWarning = null)
        => DispatcherQueue.TryEnqueue(async () => await RequestApplicationExitAsync(trigger, capturedWarning));

    private async Task RequestApplicationExitAsync(
        IndexingCloseTrigger trigger,
        IndexingCloseWarningContent? capturedWarning)
    {
        if (!await ConfirmExitWhileIndexingAsync(trigger, capturedWarning))
            return;

        _forceClose = true;
        Close();
    }

    private async Task<bool> ConfirmExitWhileIndexingAsync(
        IndexingCloseTrigger trigger,
        IndexingCloseWarningContent? capturedWarning = null)
    {
        if (capturedWarning is null && !ViewModel.IsIndexBuildActive)
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
        if (_forceClose || !ViewModel.IsIndexBuildActive)
            return false;

        IndexingCloseWarningContent warning = IndexingCloseWarning.Build(
            IndexingCloseTrigger.WindowsSessionEnding,
            ViewModel.IsActiveIndexBuildIncremental,
            ViewModel.ActiveIndexBuildFolder);
        RequestApplicationExit(IndexingCloseTrigger.WindowsSessionEnding, warning);
        return true;
    }
}
