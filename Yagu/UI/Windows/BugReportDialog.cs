using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Telemetry;
using System.Text;

namespace Yagu;

/// <summary>
/// User-reviewed bug-report dialog. When a critical error occurs and the user has opted into bug
/// reporting, this shows EXACTLY what would be submitted — error/stack, GPU/NPU, the settings file,
/// and a log tail — plus an optional email and comment. Nothing is sent unless the user clicks
/// Submit. Invoked from <see cref="App"/>; marshals to the UI thread and is single-instance.
/// </summary>
internal static class BugReportDialog
{
    private static int s_open;

    /// <summary>Offers a bug report for <paramref name="exception"/> from <paramref name="source"/>.
    /// Safe to call from any thread; no-ops when another report dialog is already open.</summary>
    public static void Offer(MainWindow? window, string source, Exception? exception)
    {
        DispatcherQueue? dispatcher = window?.DispatcherQueue;
        if (window is null || dispatcher is null)
            return;

        dispatcher.TryEnqueue(async void () =>
        {
            try
            {
                await ShowOnUiThreadAsync(window, source, exception).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("BugReport", $"Bug-report dialog failed: {ex.Message}", ex);
            }
        });
    }

    private static async Task ShowOnUiThreadAsync(MainWindow window, string source, Exception? exception)
    {
        if (Interlocked.CompareExchange(ref s_open, 1, 0) != 0)
            return;

        try
        {
            IntPtr ownerHwnd = WindowForegroundHelper.GetWindowHandle(window);
            if (ownerHwnd == IntPtr.Zero)
                return;

            string prefillEmail = window.ViewModel.BugReportContactEmail;
            BugReportPayload payload = BugReportService.Instance.BuildPayload(source, exception, prefillEmail);

            var emailBox = new TextBox
            {
                Header = "Your email (optional — only if you'd like a reply)",
                PlaceholderText = "you@example.com",
                Text = payload.Email,
            };
            var commentBox = new TextBox
            {
                Header = "What were you doing when this happened? (optional)",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 64,
            };

            YaguDialogResult result = await YaguDialog.ShowAsync(
                ownerHwnd,
                new YaguDialogOptions
                {
                    Title = "Send a bug report?",
                    Content = BuildContent(payload, emailBox, commentBox),
                    PrimaryButtonText = "Submit report",
                    CloseButtonText = "Don't send",
                    DefaultButton = YaguDialogDefaultButton.Close,
                    Width = 720,
                    Height = 640,
                    MaxContentHeight = 480,
                    IsResizable = true,
                    ShowTitleBar = false,
                    ShowTopRightCloseButton = true,
                    TitleGlyph = "\uEBE8", // Bug
                });

            if (result != YaguDialogResult.Primary)
                return;

            payload.Email = emailBox.Text?.Trim() ?? string.Empty;
            payload.UserComment = commentBox.Text?.Trim() ?? string.Empty;

            // Remember the email to pre-fill next time.
            if (!string.IsNullOrWhiteSpace(payload.Email))
            {
                try { await window.ViewModel.SetBugReportContactEmailAsync(payload.Email).ConfigureAwait(true); }
                catch (Exception ex) { LogService.Instance.Verbose("BugReport", $"Could not persist email: {ex.Message}"); }
            }

            BugReportResponse? response = await BugReportService.Instance.SubmitAsync(payload).ConfigureAwait(true);
            await ShowResultAsync(ownerHwnd, response);
        }
        finally
        {
            Interlocked.Exchange(ref s_open, 0);
        }
    }

    private static async Task ShowResultAsync(IntPtr ownerHwnd, BugReportResponse? response)
    {
        bool ok = response is not null;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = ok
                ? "Thanks! Your bug report was sent."
                : "Sorry — the bug report couldn't be sent right now (you may be offline). Nothing was uploaded.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (ok && !string.IsNullOrWhiteSpace(response!.CorrelationId))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Reference ID: " + response.CorrelationId,
                IsTextSelectionEnabled = true,
                Opacity = 0.8,
            });
        }

        await YaguDialog.ShowAsync(
            ownerHwnd,
            new YaguDialogOptions
            {
                Title = ok ? "Report sent" : "Report not sent",
                TitleGlyph = ok ? "\uE930" : "\uEA39", // Completed / Error badge
                Content = panel,
                CloseButtonText = "Close",
                DefaultButton = YaguDialogDefaultButton.Close,
                Width = 480,
                Height = 240,
                MaxContentHeight = 160,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
            });
    }

    private static ScrollViewer BuildContent(BugReportPayload payload, TextBox emailBox, TextBox commentBox)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "Yagu hit a problem. You can send a report to help get it fixed. Below is EXACTLY "
                 + "what will be sent — please review it. Your settings and log may contain file paths "
                 + "and recent search terms.",
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(emailBox);
        panel.Children.Add(commentBox);

        panel.Children.Add(BuildSummaryBlock(payload));

        panel.Children.Add(BuildPreviewExpander("Error details", BuildErrorText(payload), expanded: true));
        panel.Children.Add(BuildPreviewExpander("Settings file (settings.json)", payload.SettingsJson, expanded: false));
        panel.Children.Add(BuildPreviewExpander("Log tail (yagu.log)", payload.LogTail, expanded: false));

        // The review content (intro, email, comment, summary, and three expanders) is taller than the
        // dialog's capped body height, so wrap it in a vertical ScrollViewer. The dialog applies its
        // MaxContentHeight to this element, so it scrolls instead of clipping the lower expanders.
        // Horizontal scrolling stays off — long lines scroll inside each expander's own TextBox.
        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 0, 12, 0),
        };
        return scroller;
    }

    private static StackPanel BuildSummaryBlock(BugReportPayload payload)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(KeyValue("App version", payload.AppVersion));
        panel.Children.Add(KeyValue("OS", payload.Os));
        if (!string.IsNullOrWhiteSpace(payload.Gpu)) panel.Children.Add(KeyValue("GPU", payload.Gpu));
        if (!string.IsNullOrWhiteSpace(payload.Npu)) panel.Children.Add(KeyValue("NPU", payload.Npu));
        panel.Children.Add(KeyValue("Reference ID", payload.CorrelationId));
        return panel;
    }

    private static TextBlock KeyValue(string key, string value) => new()
    {
        Text = key + ": " + (string.IsNullOrWhiteSpace(value) ? "(none)" : value),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Opacity = 0.85,
    };

    private static string BuildErrorText(BugReportPayload payload)
    {
        var sb = new StringBuilder();
        sb.Append("Source: ").AppendLine(payload.Source);
        if (!string.IsNullOrWhiteSpace(payload.ExceptionType)) sb.Append("Type: ").AppendLine(payload.ExceptionType);
        if (!string.IsNullOrWhiteSpace(payload.Message)) sb.Append("Message: ").AppendLine(payload.Message);
        if (!string.IsNullOrWhiteSpace(payload.StackTrace)) { sb.AppendLine().AppendLine(payload.StackTrace); }
        return sb.ToString();
    }

    private static Expander BuildPreviewExpander(string header, string content, bool expanded)
    {
        // IMPORTANT: AcceptsReturn (and TextWrapping) MUST be set BEFORE Text. A TextBox is single-line
        // by default, and assigning multi-line Text while single-line silently truncates everything
        // after the first line break. Object initializers assign in source order, so Text is set last.
        var textBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Height = 160,
            Text = string.IsNullOrEmpty(content) ? "(empty)" : content,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

        return new Expander
        {
            Header = header,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = textBox,
        };
    }
}
