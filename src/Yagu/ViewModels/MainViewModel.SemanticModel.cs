using Yagu.Services.Ai;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// On-device semantic model management: listing/preparing model options, resolving the current
/// model for display, the one-time model qualification flow, and new-model update alerts.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Enumerates the locally-runnable model options for the first-run download prompt.</summary>
    public Task<IReadOnlyList<SemanticModelOption>> GetSemanticModelOptionsAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return Task.FromResult<IReadOnlyList<SemanticModelOption>>(Array.Empty<SemanticModelOption>());
        return _semanticTranslator.ListModelOptionsAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Resolves the human-readable name of the model that AI search will actually use right now, for
    /// display in Settings: a pinned override by name, else the loaded automatic model, else the
    /// recommended automatic model (resolved by querying the catalog). Falls back to a generic label on
    /// any failure. Does NOT change any state or reset the cache.
    /// </summary>
    public async Task<string> ResolveCurrentSemanticModelDisplayAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        // A pinned override, or an already-loaded automatic model, is authoritative and needs no query.
        if (!string.IsNullOrWhiteSpace(SemanticModelAlias))
            return SemanticModelAlias;
        string? loaded = (_semanticTranslator as FoundryLocalSemanticQueryTranslator)?.SelectedModelAlias;
        if (!string.IsNullOrWhiteSpace(loaded))
            return $"{loaded} (automatic)";

        // Automatic mode with nothing loaded yet: resolve the recommended model from the catalog.
        try
        {
            var options = await GetSemanticModelOptionsAsync(progress, cancellationToken).ConfigureAwait(true);
            var recommended = options.FirstOrDefault(o => o.IsRecommended);
            if (recommended is not null && !string.IsNullOrWhiteSpace(recommended.Alias))
                return $"{recommended.Alias} (automatic)";
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to the generic label */ }

        return "Automatic (recommended for your hardware)";
    }

    /// <summary>
    /// Clears the cached Foundry Local model catalog and loaded model (picking up models downloaded or
    /// updated out of band), then re-resolves and returns the current model's display name. Used by the
    /// "Refresh Foundry cache" button in Settings.
    /// </summary>
    public async Task<string> RefreshFoundryCacheAsync(
        IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        _semanticTranslator?.RefreshCatalog();
        OnPropertyChanged(nameof(CurrentSemanticModelDisplay));
        return await ResolveCurrentSemanticModelDisplayAsync(progress, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Downloads and selects the given semantic model, persisting it as the chosen model.</summary>
    public async Task PrepareSemanticModelAsync(
        string? modelAlias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            throw new InvalidOperationException("Semantic search is not available on this machine.");

        await _semanticTranslator.PrepareModelAsync(modelAlias, progress, cancellationToken).ConfigureAwait(true);

        _settings.SemanticModelAlias = modelAlias?.Trim() ?? string.Empty;
        SemanticModelAlias = _settings.SemanticModelAlias;
        _settings.SemanticModelDownloaded = true;
        OnPropertyChanged(nameof(IsSemanticModelDownloaded));
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Reverts to automatic (recommended) model selection. Applied live — the next semantic
    /// search re-selects the best model for the current hardware and device order — and persisted.</summary>
    public async Task ClearSemanticModelOverrideAsync()
    {
        _semanticTranslator?.SetModelOverride(null);
        _settings.SemanticModelAlias = string.Empty;
        SemanticModelAlias = string.Empty;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    // ── First-run AI-model qualification ──

    /// <summary>True when the one-time first-run AI-model qualification should be offered: AI (Semantic)
    /// search is available/enabled and the sweep has not been run yet.</summary>
    public bool ShouldOfferSemanticModelQualification =>
        SemanticModelQualificationCoordinator.ShouldOffer(_settings, SemanticSearchAvailable);

    /// <summary>
    /// Runs the first-run model-qualification sweep against this machine: enumerates the runnable models,
    /// probes each with a mix of simple and complex queries (<see cref="SemanticProbeSet.Default"/>), and
    /// returns the qualified model (if any), a best-effort fallback, and per-candidate reports. The user's
    /// chosen <paramref name="thresholds"/> decide how long to wait for a model to load and how slow a
    /// query may be before a candidate is abandoned. The sweep may download models and run inference, so
    /// it can take minutes — honor <paramref name="cancellationToken"/> so the user can cancel. Probing is
    /// in-process for now; a crashy model that faults with a managed exception is abandoned, but a hard
    /// native abort still ends the app until the out-of-process worker lands.
    /// </summary>
    public async Task<ModelQualificationResult> RunSemanticModelQualificationAsync(
        ModelQualificationThresholds thresholds,
        IProgress<SemanticQualificationProgress>? progress, CancellationToken cancellationToken)
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            throw new InvalidOperationException("Semantic search is not available on this machine.");

        // The runner prepares each candidate once and warms it up so every TIMED probe measures steady-
        // state inference latency. The "release model from memory after each search" setting (ON by
        // default) defeats that: it unloads the model after EVERY inference, so each timed probe reloads
        // the model from scratch inside its own timed window (~5-6s for a 14B model like phi-4), inflating
        // per-probe latency past the per-query limit and disqualifying otherwise-accurate large models as
        // "too slow". Keep each candidate's model resident across its probes for the sweep — the runner
        // already unloads the previous candidate before loading the next, so only one model is ever
        // resident — then restore the user's setting (and free VRAM) afterwards.
        bool restoreUnloadAfterUse = _settings.SemanticUnloadModelAfterUse;
        _semanticTranslator.SetUnloadAfterUse(false);
        try
        {
            var runner = new SemanticModelQualificationRunner(
                _semanticTranslator,
                defaultDirectory: null,
                directoryExists: System.IO.Directory.Exists,
                maxCandidates: SemanticModelQualificationRunner.DefaultMaxCandidates,
                failedProbeHoldMs: SemanticModelQualificationRunner.DefaultFailedProbeHoldMs);
            return await runner.RunAsync(SemanticProbeSet.Default, thresholds, progress, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _semanticTranslator.SetUnloadAfterUse(restoreUnloadAfterUse);
            if (restoreUnloadAfterUse)
            {
                // The user wants the model released when idle; the sweep left one resident. Free it.
                try { await _semanticTranslator.UnloadCurrentModelAsync(CancellationToken.None).ConfigureAwait(true); }
                catch { /* best-effort: freeing VRAM must never fail the sweep result */ }
            }
        }
    }

    /// <summary>
    /// Folds a finished qualification sweep into settings and, when the user accepts a model, selects it
    /// live and persists it. Pass the user's override as <paramref name="chosenAlias"/>; null accepts the
    /// sweep's recommendation. Marks the one-time check complete either way.
    /// </summary>
    public async Task ApplySemanticModelQualificationAsync(
        ModelQualificationResult result, bool accepted, string? chosenAlias = null)
    {
        SemanticModelQualificationCoordinator.ApplyResult(_settings, result, accepted, chosenAlias);

        // Reflect the (possibly new) effective model in the UI + translator.
        SemanticModelAlias = _settings.SemanticModelAlias;
        _semanticTranslator?.SetModelOverride(
            string.IsNullOrWhiteSpace(_settings.SemanticModelAlias) ? null : _settings.SemanticModelAlias);
        OnPropertyChanged(nameof(CurrentSemanticModelDisplay));
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Marks the first-run model check as declined (so it is not re-offered) without selecting a
    /// model. Use for an explicit "skip"; a plain "not now" should leave settings untouched so the offer
    /// returns next launch.</summary>
    public async Task DeclineSemanticModelQualificationAsync()
    {
        SemanticModelQualificationCoordinator.MarkDeclined(_settings);
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>The user refused the first-run model check. Because AI (Semantic) search needs a model
    /// that was validated on this PC to be reliable, turn the feature OFF and mark the one-time check
    /// complete so re-enabling it later (from Settings) does not re-offer the sweep. The user can opt back
    /// in and pick a model themselves — at their own risk — from the AI settings tab.</summary>
    public async Task DeclineAndDisableSemanticSearchAsync()
    {
        // Mark the check complete first so the persist triggered by the toggle below already carries it.
        SemanticModelQualificationCoordinator.MarkDeclined(_settings);
        // Turning the toggle off persists SemanticSearchEnabled=false and disables the translator live.
        SemanticSearchAvailable = false;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>True once the first-run model check has run (or was declined) or a model has been recorded,
    /// i.e. there is qualification state that <see cref="ResetSemanticModelQualificationAsync"/> would
    /// clear. Used to enable/disable the Developer Options "reset" button.</summary>
    public bool HasSemanticModelQualificationState =>
        _settings.SemanticModelQualificationCompleted
        || !string.IsNullOrEmpty(_settings.SemanticQualifiedModelAlias)
        || !string.IsNullOrEmpty(_settings.SemanticModelAlias);

    /// <summary>Developer action: clear the first-run AI-model qualification back to a fresh-install state
    /// and re-enable AI (Semantic) search, so the model check is offered again on the next startup. Forgets
    /// the recommended and selected model so the re-run starts from the automatic pick.</summary>
    public async Task ResetSemanticModelQualificationAsync()
    {
        if (!await PersistPromptResetAsync(settings =>
        {
            SemanticModelQualificationCoordinator.Reset(settings);
            settings.SemanticSearchEnabled = true;
        }).ConfigureAwait(true))
            return;

        // Update the live state without invoking the broad-save property handlers: the targeted update
        // above is already durable and must not sweep unrelated Settings-window edits into the file.
        bool queryModeWasInitialized = _queryModeInitialized;
        _queryModeInitialized = false;
        try
        {
            SemanticSearchAvailable = true;
            // Drop any live model override so the re-run sweep starts from the automatic pick.
            SemanticModelAlias = string.Empty;
        }
        finally
        {
            _queryModeInitialized = queryModeWasInitialized;
        }
        _semanticTranslator?.SetEnabled(true);
        _semanticTranslator?.SetModelOverride(null);
        OnPropertyChanged(nameof(CurrentSemanticModelDisplay));
        OnPropertyChanged(nameof(HasSemanticModelQualificationState));
    }

    /// <summary>
    /// Checks the Foundry Local catalog for newly-available, updated, or variant text-chat models and
    /// returns the ones the user has not seen, so the caller can show a one-time alert. Self-gating: it
    /// no-ops (returns empty) when alerts are disabled, semantic search is off/unavailable, the user has
    /// never used semantic search (so a catalog query would needlessly initialize Foundry), or it was
    /// already checked within <see cref="FoundryModelUpdateChecker.DefaultCheckInterval"/>. The very first
    /// successful check silently seeds the baseline and returns empty. Persists the refreshed baseline and
    /// check time. Failures (offline, etc.) are swallowed and leave the baseline unchanged.
    /// </summary>
    public async Task<IReadOnlyList<FoundryModelChange>> CheckForNewFoundryModelsAsync(CancellationToken cancellationToken)
    {
        var none = (IReadOnlyList<FoundryModelChange>)Array.Empty<FoundryModelChange>();

        if (!FoundryModelUpdateAlertsEnabled || !_settings.SemanticSearchEnabled || !_settings.SemanticModelDownloaded)
            return none;
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return none;
        if (!FoundryModelUpdateChecker.ShouldCheck(
                _settings.LastFoundryModelCheckUtc, DateTimeOffset.UtcNow, FoundryModelUpdateChecker.DefaultCheckInterval))
            return none;

        try
        {
            var options = await _semanticTranslator.ListModelOptionsAsync(null, cancellationToken).ConfigureAwait(true);
            var currentModels = options
                .Where(o => !string.IsNullOrEmpty(o.Id))
                .Select(o => new FoundryModelDescriptor(o.Id!, o.Alias, o.DeviceLabel, o.SizeBytes))
                .ToList();

            // An empty/failed catalog query must not clobber the baseline (it would mask real models
            // next time, or — on the very first run — seed an empty baseline).
            if (currentModels.Count == 0)
                return none;

            bool hasBaseline = _settings.LastFoundryModelCheckUtc is not null || _settings.KnownFoundryModelIds.Count > 0;
            var result = FoundryModelUpdateChecker.Detect(_settings.KnownFoundryModelIds, currentModels, hasBaseline);

            _settings.KnownFoundryModelIds = result.CurrentIds.ToList();
            _settings.LastFoundryModelCheckUtc = DateTimeOffset.UtcNow;
            if (result.Changes.Count > 0)
                _settings.LastFoundryModelAlertUtc = DateTimeOffset.UtcNow;
            await PersistSettingsAsync().ConfigureAwait(true);

            YaguLog.For("SemanticSearch").LogInformation(
                "Foundry model update check: {CatalogCount} catalog model(s), {NewCount} new, baselineSeeded={BaselineSeeded}.",
                currentModels.Count, result.Changes.Count, result.BaselineSeeded);
            return result.Changes;
        }
        catch (OperationCanceledException)
        {
            return none;
        }
        catch (Exception ex)
        {
            YaguLog.For("SemanticSearch").LogWarning(ex, "Foundry model update check failed: {Error}", ex.Message);
            return none;
        }
    }
}
