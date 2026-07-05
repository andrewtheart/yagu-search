using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Ocr;

namespace Yagu;

/// <summary>
/// One-time consent gate shown before Yagu downloads any OCR asset (the native PaddleOCR runtime,
/// PP-OCR models, or Tesseract language data). Registered as <see cref="OcrDownloadGate.PromptAsync"/>
/// at startup; invoked from a background OCR-init thread, so it marshals to the UI thread before
/// showing the (title-bar-less) <see cref="YaguDialog"/>. Approval is persisted so the user is asked
/// at most once across sessions.
/// </summary>
internal static class OcrDownloadConsentDialog
{
    private static int s_open;

    /// <summary>
    /// Warns about the pending one-time download described by <paramref name="requirement"/> and
    /// returns <c>true</c> when the user approves. Safe to call from any thread.
    /// </summary>
    public static Task<bool> RequestConsentAsync(MainWindow? window, OcrAssetRequirement requirement)
    {
        DispatcherQueue? dispatcher = window?.DispatcherQueue;
        if (window is null || dispatcher is null)
        {
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = dispatcher.TryEnqueue(async void () =>
        {
            try
            {
                tcs.TrySetResult(await ShowOnUiThreadAsync(window, requirement).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("OcrConsent", $"Consent dialog failed: {ex.Message}", ex);
                tcs.TrySetResult(false);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetResult(false);
        }

        return tcs.Task;
    }

    private static async Task<bool> ShowOnUiThreadAsync(MainWindow window, OcrAssetRequirement requirement)
    {
        // Only one consent dialog at a time; concurrent OCR inits share the first answer via the gate.
        if (Interlocked.CompareExchange(ref s_open, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            IntPtr ownerHwnd = WindowForegroundHelper.GetWindowHandle(window);
            if (ownerHwnd == IntPtr.Zero)
            {
                return false;
            }

            YaguDialogResult result = await YaguDialog.ShowAsync(
                ownerHwnd,
                new YaguDialogOptions
                {
                    Title = "Download OCR components?",
                    Content = BuildContent(requirement),
                    PrimaryButtonText = "Download now",
                    CloseButtonText = "Not now",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    Width = 560,
                    Height = 360,
                    MaxContentHeight = 320,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    TitleGlyph = "\uE896", // Download
                });

            if (result != YaguDialogResult.Primary)
            {
                return false;
            }

            // Remember the approval across sessions so the warning is shown at most once.
            try
            {
                await window.ViewModel.MarkOcrDownloadConsentedAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Consent still applies this session even if persistence fails.
                LogService.Instance.Warning("OcrConsent", $"Unable to persist OCR download consent: {ex.Message}", ex);
            }

            return true;
        }
        finally
        {
            Interlocked.Exchange(ref s_open, 0);
        }
    }

    private static StackPanel BuildContent(OcrAssetRequirement requirement)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "Image-text (OCR) search reads text inside images. The first time you use it, Yagu "
                 + "needs a one-time download of the OCR engine and its language data.",
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"This will download about {requirement.ApproxMb} MB:",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var list = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
        foreach (string component in requirement.MissingComponents)
        {
            list.Children.Add(new TextBlock
            {
                Text = "\u2022  " + component,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(list);

        panel.Children.Add(new TextBlock
        {
            Text = "The files come from public package feeds (nuget.org and GitHub) over your internet "
                 + "connection. Nothing is downloaded until you choose \u201CDownload now\u201D. To avoid this "
                 + "download entirely, install the Yagu edition that includes OCR components.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        return panel;
    }
}
