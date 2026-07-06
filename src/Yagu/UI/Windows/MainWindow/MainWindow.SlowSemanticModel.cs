using Yagu.Services.Ai;

namespace Yagu;

/// <summary>
/// Watches the on-device AI interpretation phase and, when it runs long, offers a smaller/faster model.
/// The 30-second watchdog runs alongside a semantic search; if the user picks a faster model the dialog
/// downloads it, makes it the default, and the search re-runs with it.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>How long semantic translation may run before Yagu offers a smaller/faster model.</summary>
    private static readonly TimeSpan SlowSemanticModelWarningDelay = TimeSpan.FromSeconds(30);

    private bool _slowSemanticWatchActive;
    private bool _slowSemanticRerunNeeded;

    /// <summary>
    /// Runs a search while watching for a slow AI interpretation phase. In Semantic mode, a watchdog
    /// runs alongside the translation; if it exceeds <see cref="SlowSemanticModelWarningDelay"/> the user
    /// is offered a smaller/faster model and, on switch, the search re-runs with it. Traditional searches
    /// and re-entrant calls submit directly.
    /// </summary>
    private async Task SubmitSearchWithSlowModelWatchAsync()
    {
        // In Traditional mode, first offer to switch a natural-language query to AI (Semantic) search.
        // If the user accepts, IsSemanticQueryMode flips to true below and this submit runs as Semantic.
        await MaybeOfferSemanticSuggestionAsync();

        // Then, if the query is still Traditional and contains a literal "\n" escape while Multiline is
        // off, offer to switch Multiline (and Regex) on so the escape matches a real line break.
        await MaybeOfferMultilineSuggestionAsync();

        if (_slowSemanticWatchActive || !ViewModel.IsSemanticQueryMode || !ViewModel.SemanticSearchAvailable)
        {
            await ViewModel.SubmitSearchAsync(RunPreSearchWarningGatesAsync);
            return;
        }

        _slowSemanticWatchActive = true;
        try
        {
            while (true)
            {
                _slowSemanticRerunNeeded = false;
                using var watchCts = new CancellationTokenSource();
                var watch = MonitorSlowSemanticInterpretationAsync(watchCts.Token);
                try
                {
                    await ViewModel.SubmitSearchAsync(RunPreSearchWarningGatesAsync);
                }
                finally
                {
                    // Stop the watchdog timer (no-op once the dialog is already showing).
                    watchCts.Cancel();
                }

                await watch;

                // Re-run only when the watchdog cancelled the in-flight translation to switch models;
                // otherwise the original translation completed (or was user-cancelled) and we're done.
                if (!_slowSemanticRerunNeeded)
                    break;
            }
        }
        finally
        {
            _slowSemanticWatchActive = false;
        }
    }

    /// <summary>
    /// Waits out the slow-model threshold and, if translation is still running, shows the prompt offering
    /// a smaller/faster model. Never throws into the caller — all failures fall back to leaving the
    /// original translation running.
    /// </summary>
    private async Task MonitorSlowSemanticInterpretationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SlowSemanticModelWarningDelay, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // Translation finished before the threshold.
        }

        if (token.IsCancellationRequested || !ViewModel.IsTranslatingSemanticQuery)
            return;

        string? modelKey = ViewModel.CurrentSemanticModelKey;
        if (ViewModel.IsSlowSemanticModelWarningSuppressed(modelKey))
            return;
        if (YaguDialog.HasOpenOwnedWindow(_hwnd) || SlowSemanticModelDialog.HasOpenOwnedWindow(_hwnd))
            return;

        IReadOnlyList<SemanticModelOption> faster;
        try
        {
            faster = await ViewModel.GetFasterSemanticModelOptionsAsync(null, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return; // Catalog query failed — silently skip the prompt.
        }

        // The translation may have finished while we listed models, or there may be nothing faster.
        if (token.IsCancellationRequested || !ViewModel.IsTranslatingSemanticQuery || faster.Count == 0)
            return;

        try
        {
            var choice = await SlowSemanticModelDialog.ShowAsync(
                _hwnd,
                RootGrid.ActualTheme,
                ViewModel.CurrentSemanticModelName,
                (int)SlowSemanticModelWarningDelay.TotalSeconds,
                faster,
                SwitchSlowSemanticModelAsync);

            if (choice.DontWarnAgain)
                await ViewModel.SuppressSlowSemanticModelWarningAsync(modelKey).ConfigureAwait(true);
        }
        catch
        {
            // Never let the watchdog fault the search flow.
        }
    }

    /// <summary>
    /// Cancels the in-flight (slow) translation, then downloads and selects the chosen faster model as
    /// the new default. Invoked by the slow-model dialog when the user confirms a switch. Cancelling
    /// first is required so the old model is never unloaded mid-inference. Sets the re-run flag up front
    /// so the search re-runs even if the user later dismisses an error.
    /// </summary>
    private async Task SwitchSlowSemanticModelAsync(
        string alias, IProgress<SemanticTranslationProgress>? progress, CancellationToken token)
    {
        _slowSemanticRerunNeeded = true;
        ViewModel.CancelSemanticTranslation();
        await ViewModel.PrepareSemanticModelAsync(alias, progress, token).ConfigureAwait(true);
    }
}
