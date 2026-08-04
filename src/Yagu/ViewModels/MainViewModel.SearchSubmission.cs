using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Search submission and preparation: the entry point that turns a submitted query into a search
/// run (translating it first in semantic mode), plus the preparation-phase cancellation state that
/// lets the user abort before the scan starts.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Entry point for an interactive search submission. In Semantic mode the natural-language
    /// query is first translated by the local model and applied to this view-model; in Traditional
    /// mode it goes straight to <see cref="StartSearchAsync"/>.
    /// </summary>
    public async Task SubmitSearchAsync(Func<Task<bool>>? postTranslationGate = null)
    {
        // Re-entrancy guard: a second submit (Enter in the query box, F5, a double-click on the
        // Search button) while a semantic translation is already in flight would start a concurrent
        // model inference on the same chat client and corrupt its output ("the model did not return
        // a JSON object"). Ignore additional submits until the translation finishes or is cancelled.
        if (IsTranslatingSemanticQuery) return;

        if (IsSemanticQueryMode && SemanticSearchAvailable)
        {
            // Clear any previous semantic search's resolved settings from Advanced Options back to the
            // saved defaults before this run; a new semantic search re-applies its own. This runs ONLY on
            // a Semantic submit — a Traditional search must NEVER read from or write to Advanced Options,
            // so whatever the user typed there (e.g. an include glob) is used verbatim and left untouched.
            ResetVisibleSemanticResolution();

            // Capture the typed NL text (translation overwrites Query) and snapshot the filter defaults.
            _pendingSemanticHistoryEntry = Query?.Trim();
            var defaultsSnapshot = CaptureSearchDefaults();
            var outcome = await TranslateSemanticQueryAsync().ConfigureAwait(true);
            if (outcome == SemanticTranslationOutcome.Aborted) return;
            if (outcome is SemanticTranslationOutcome.Applied or SemanticTranslationOutcome.Salvaged)
                _semanticDefaultsSnapshot = defaultsSnapshot; // armed: StartSearchAsync leaves the plan visible
            else
            {
                // No plan and nothing to salvage (e.g. a bare token like "#define") — fall back to a
                // plain Traditional search of the typed text. (A salvaged plan already set its own
                // "best guess" status inside TranslateSemanticQueryAsync.)
                ErrorText = string.Empty;
                // A single-token query already set an accurate passthrough status inside
                // TranslateSemanticQueryAsync; only show the generic model-failure message when the
                // translator left the status blank.
                if (string.IsNullOrEmpty(SemanticStatusText))
                    SemanticStatusText = "AI couldn't interpret that — searching for the text directly.";
            }
        }

        try
        {
            // Run an optional pre-search gate AFTER any semantic translation, so it sees the resolved
            // search target (include globs / literal query) the model produced rather than the raw
            // natural-language text. Used for the excluded-extension warning.
            if (postTranslationGate is not null && !await postTranslationGate().ConfigureAwait(true))
                return;

            // The user clicked Cancel during the pre-search gate phase (before the scan committed) — abort.
            if (IsSearchPreparationCancellationRequested)
                return;

            await StartSearchAsync().ConfigureAwait(true);
        }
        finally
        {
            // If the run didn't reach the commit point in StartSearchAsync (gate cancelled, or an early
            // validation error returned), revert the plan now — a cancelled semantic search should not
            // leave its resolution behind. A committed search sets _semanticResolutionVisible and is left
            // visible on purpose (reset at the start of the next search).
            if (_semanticDefaultsSnapshot is { } leftover && !_semanticResolutionVisible)
            {
                RestoreSearchDefaults(leftover);
                _semanticDefaultsSnapshot = null;
                await PersistSettingsAsync().ConfigureAwait(true);
            }
        }
    }

    private CancellationTokenSource? _searchPrepareCts;

    /// <summary>True once the user has requested cancellation of the in-progress pre-search preparation
    /// (Cancel clicked while the pre-scan gates are still running, before <see cref="IsSearching"/> flips).</summary>
    public bool IsSearchPreparationCancellationRequested => _searchPrepareCts?.IsCancellationRequested == true;

    /// <summary>Marks the start of the pre-search preparation phase (semantic offers + warning gates), so
    /// the Cancel button and an indeterminate progress bar appear immediately instead of after the
    /// multi-second gate work. Returns a token that <see cref="CancelSearchPreparation"/> cancels.</summary>
    public CancellationToken BeginSearchPreparation()
    {
        _searchPrepareCts?.Dispose();
        _searchPrepareCts = new CancellationTokenSource();
        IsPreparingSearch = true;
        return _searchPrepareCts.Token;
    }

    /// <summary>Ends the preparation phase (the scan committed, or a gate aborted the run). Clears the
    /// "Canceling.." state only when no file scan is actually running.</summary>
    public void EndSearchPreparation()
    {
        IsPreparingSearch = false;
        if (!IsSearching) IsCancelling = false;
        _searchPrepareCts?.Dispose();
        _searchPrepareCts = null;
    }

    /// <summary>Requests cancellation of the in-progress preparation (Cancel clicked before the scan
    /// starts). Shows the disabled "Canceling.." state; the gate phase aborts at its next checkpoint.</summary>
    public void CancelSearchPreparation()
    {
        if (!IsPreparingSearch) return;
        IsCancelling = true;
        try { _searchPrepareCts?.Cancel(); }
        catch (Exception ex) { YaguLog.For("Search").LogWarning(ex, "Cancel preparation failed"); }
    }

    [RelayCommand]
    public Task CancelAsync()
    {
        // Only flip into the "Canceling.." state when there's actually a run to cancel — CancelAsync is
        // also called on session load/close where nothing is in flight.
        if (IsSearching) IsCancelling = true;
        try { _cts?.Cancel(); } catch (Exception ex) { YaguLog.For("Search").LogWarning(ex, "Cancel failed"); }
        return Task.CompletedTask;
    }
}
