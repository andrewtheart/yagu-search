using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Services;
using Yagu.Services.Logging;
using Yagu.Services.Ocr;

namespace Yagu;

/// <summary>
/// Pre-search gate for image-text (OCR) search: when a search will use OCR but the engine's one-time
/// components are still missing, ask and download them <b>before</b> the search runs.
/// <para>
/// Without this the engine discovers the missing assets lazily on a background thread partway into the
/// search, so the user waits on a search that silently cannot do OCR yet, and the consent prompt
/// appears over already-streaming results.
/// </para>
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _appToastTimer;

    // Engines whose components this session already proved ready. EnsureReadyAsync succeeding is the
    // authoritative signal, so it wins over the on-disk probe: if the probe ever misreads a present
    // install (it did for PP-OCRv5's mid-name role directories), the gate still cannot nag every search.
    private readonly HashSet<string> _ocrComponentsVerified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs before every other pre-search gate. Returns false only when the user cancels outright; a
    /// declined or failed download still lets the search run (OCR simply finds nothing until the
    /// components exist), which is why the failure path offers "Search anyway".
    /// </summary>
    private async Task<bool> CheckOcrComponentsAndWarnAsync()
    {
        if (!ViewModel.SearchImageText)
            return true;
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return true;

        string engineId = AppSettings.NormalizeImageOcrEngine(ViewModel.ImageOcrEngine);
        if (_ocrComponentsVerified.Contains(engineId))
            return true;

        OcrAssetRequirement requirement;
        IOcrEngine engine;
        try
        {
            engine = OcrEngineFactory.Create(
                engineId,
                AppSettings.NormalizeImageOcrModel(ViewModel.ImageOcrModel),
                AppSettings.NormalizeImageOcrMaxSide(ViewModel.ImageOcrMaxSide));
            requirement = engine.DescribeAssetRequirement();
        }
        catch (Exception ex)
        {
            // Probing must never block a search; the engine still gates its own download later.
            YaguLog.For("OcrConsent").LogWarning(ex, "Could not probe OCR components before the search: {Error}", ex.Message);
            return true;
        }

        OcrPreSearchAction action = OcrPreSearchReadiness.Decide(
            ViewModel.SearchImageText, requirement, OcrDownloadGate.ConsentGranted);
        if (action == OcrPreSearchAction.Proceed)
        {
            DisposeOcrEngine(engine);
            return true;
        }

        try
        {
            if (action == OcrPreSearchAction.AskForConsent
                && !await OcrDownloadConsentDialog.RequestConsentAsync(this, requirement).ConfigureAwait(true))
            {
                // Declining is not a cancelled search — it just means this search has no OCR.
                return true;
            }

            return await DownloadOcrComponentsWithProgressAsync(engine, requirement, engineId).ConfigureAwait(true);
        }
        finally
        {
            DisposeOcrEngine(engine);
        }
    }

    /// <summary>
    /// Downloads the missing components while showing a modal progress dialog, and does not return
    /// until they are ready, the download fails, or the user cancels. On failure the user chooses
    /// between searching without OCR and abandoning the search.
    /// </summary>
    private async Task<bool> DownloadOcrComponentsWithProgressAsync(
        IOcrEngine engine,
        OcrAssetRequirement requirement,
        string engineId)
    {
        // Not scoped with "using": on the cancel path the abandoned download still holds this token,
        // and disposing it underneath that task can fault it. It owns no timer, so leaking it is cheap.
        var cancellation = new CancellationTokenSource();

        var elapsedText = new TextBlock
        {
            Text = OcrPreSearchReadiness.DescribeElapsed(TimeSpan.Zero),
            FontSize = 13,
            Opacity = 0.9,
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "Downloading OCR components. The search starts as soon as they are ready.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = OcrPreSearchReadiness.DescribeComponents(requirement),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
        panel.Children.Add(elapsedText);

        // The engine reports no byte-level progress, so show honest elapsed time rather than a fake bar.
        var started = System.Diagnostics.Stopwatch.StartNew();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => elapsedText.Text = OcrPreSearchReadiness.DescribeElapsed(started.Elapsed);
        timer.Start();

        YaguDialog? dialog = null;
        Task<YaguDialogResult> dialogTask = YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Downloading OCR components",
                Content = panel,
                PrimaryButtonText = null,
                CloseButtonText = "Cancel search",
                Width = 520,
                Height = 300,
                ShowTitleBar = false,
                ShowTopRightCloseButton = false,
                TitleGlyph = "\uE896", // Download
            },
            d => dialog = d);

        Task<OcrResult> download = Task.Run(() => engine.EnsureReadyAsync(cancellation.Token));
        Task finished = await Task.WhenAny(download, dialogTask).ConfigureAwait(true);

        timer.Stop();

        if (finished == dialogTask)
        {
            // The user dismissed the dialog: stop the download and abandon the search. Observe the
            // abandoned task so a cancellation/IO fault cannot surface as an UnobservedTaskException.
            cancellation.Cancel();
            _ = download.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            YaguLog.For("OcrConsent").LogInformation("OCR component download cancelled by the user; search abandoned.");
            return false;
        }

        dialog?.Close();
        await dialogTask.ConfigureAwait(true);

        OcrResult result;
        try
        {
            result = await download.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result = OcrResult.Fail(ex.Message);
        }
        cancellation.Dispose();

        if (result.Success)
        {
            _ocrComponentsVerified.Add(engineId);
            ShowAppToast("OCR components downloaded");
            return true;
        }

        YaguLog.For("OcrConsent").LogWarning("OCR component download failed: {Error}", result.Error ?? "unknown error");
        return await ConfirmSearchWithoutOcrAsync(result.Error).ConfigureAwait(true);
    }

    /// <summary>Offers to run the search without OCR after a failed download.</summary>
    private async Task<bool> ConfirmSearchWithoutOcrAsync(string? error)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "The OCR components could not be downloaded.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });
        if (!string.IsNullOrWhiteSpace(error))
        {
            panel.Children.Add(new TextBlock
            {
                Text = error,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7,
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "You can search anyway — everything except text inside images is still searched.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });

        YaguDialogResult choice = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "OCR components unavailable",
                Content = panel,
                PrimaryButtonText = "Search anyway",
                CloseButtonText = "Cancel search",
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 520,
                Height = 320,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                TitleGlyph = "\uE7BA", // Warning
            });

        return choice == YaguDialogResult.Primary;
    }

    private static void DisposeOcrEngine(IOcrEngine engine)
    {
        try
        {
            if (engine is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            YaguLog.For("OcrConsent").LogDebug(ex, "Disposing the probed OCR engine failed: {Error}", ex.Message);
        }
    }

    /// <summary>Shows the app-level snackbar at the bottom of the window for a few seconds.</summary>
    private void ShowAppToast(string message)
    {
        if (AppToast is null || AppToastText is null)
            return;

        AppToastText.Text = message;
        AppToast.Visibility = Visibility.Visible;

        if (_appToastTimer is null)
        {
            _appToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _appToastTimer.Tick += (_, _) => HideAppToast();
        }
        _appToastTimer.Stop();
        _appToastTimer.Start();
    }

    private void HideAppToast()
    {
        _appToastTimer?.Stop();
        if (AppToast is not null)
            AppToast.Visibility = Visibility.Collapsed;
    }

    private void OnAppToastDismissClick(object sender, RoutedEventArgs e) => HideAppToast();
}
