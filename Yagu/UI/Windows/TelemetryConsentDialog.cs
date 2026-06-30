using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// First-run, one-time consent dialog that asks whether the user wants to help improve Yagu by
/// enabling anonymized telemetry and/or bug reporting. The two consents are independent (either,
/// both, or neither). Whatever the user chooses is persisted and the dialog is never shown again —
/// dismissing it (top-right close) is treated as declining both. Registered/invoked from
/// <see cref="App"/> after the main window is shown; title-bar-less per the modal convention.
/// </summary>
internal static class TelemetryConsentDialog
{
    private static int s_open;

    /// <summary>Shows the consent dialog if it hasn't been shown before, persisting the user's choice.
    /// Safe to call from any thread; no-ops if already shown or another instance is open.</summary>
    public static Task RequestConsentAsync(MainWindow? window)
    {
        DispatcherQueue? dispatcher = window?.DispatcherQueue;
        if (window is null || dispatcher is null)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = dispatcher.TryEnqueue(async void () =>
        {
            try
            {
                await ShowOnUiThreadAsync(window).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("TelemetryConsent", $"Consent dialog failed: {ex.Message}", ex);
            }
            finally
            {
                tcs.TrySetResult();
            }
        });

        if (!enqueued)
            tcs.TrySetResult();

        return tcs.Task;
    }

    private static async Task ShowOnUiThreadAsync(MainWindow window)
    {
        if (Interlocked.CompareExchange(ref s_open, 1, 0) != 0)
            return;

        try
        {
            IntPtr ownerHwnd = WindowForegroundHelper.GetWindowHandle(window);
            if (ownerHwnd == IntPtr.Zero)
                return;

            var telemetryToggle = new ToggleSwitch
            {
                Header = "Send anonymous performance & error reports",
                IsOn = false,
                OffContent = "Off",
                OnContent = "On",
            };
            var bugReportToggle = new ToggleSwitch
            {
                Header = "Ask me to send a bug report when something goes wrong",
                IsOn = false,
                OffContent = "Off",
                OnContent = "On",
            };

            YaguDialogResult result = await YaguDialog.ShowAsync(
                ownerHwnd,
                new YaguDialogOptions
                {
                    Title = "Help improve Yagu?",
                    Content = BuildContent(telemetryToggle, bugReportToggle),
                    PrimaryButtonText = "Save & continue",
                    CloseButtonText = "No thanks",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    Width = 600,
                    Height = 560,
                    MaxContentHeight = 480,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    TitleGlyph = "\uE9D9", // Diagnostic
                });

            // "No thanks" / dismiss = decline both. Either way we record the prompt as shown so the
            // user is never asked again.
            bool telemetry = result == YaguDialogResult.Primary && telemetryToggle.IsOn;
            bool bugReporting = result == YaguDialogResult.Primary && bugReportToggle.IsOn;

            try
            {
                await window.ViewModel.MarkTelemetryConsentAsync(telemetry, bugReporting).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("TelemetryConsent", $"Unable to persist telemetry consent: {ex.Message}", ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref s_open, 0);
        }
    }

    private static StackPanel BuildContent(ToggleSwitch telemetryToggle, ToggleSwitch bugReportToggle)
    {
        var panel = new StackPanel { Spacing = 14 };

        panel.Children.Add(new TextBlock
        {
            Text = "Yagu can optionally send information to its developer to help fix bugs and improve "
                 + "performance. This is entirely your choice and you can change it any time in Settings.",
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(telemetryToggle);
        panel.Children.Add(new TextBlock
        {
            Text = "Anonymous reports include app/OS version, error types, and timings. They never "
                 + "include your search terms, file names, file contents, or folder paths.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(0, -6, 0, 0),
        });

        panel.Children.Add(bugReportToggle);
        panel.Children.Add(new TextBlock
        {
            Text = "When something goes wrong, Yagu will show you exactly what a bug report contains "
                 + "(error details, your settings file, and a log) and only send it if you click Submit. "
                 + "You can add your email if you'd like a reply.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(0, -6, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Reports are sent securely to Yagu's own service — never directly to any third party.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.9,
        });

        return panel;
    }
}
