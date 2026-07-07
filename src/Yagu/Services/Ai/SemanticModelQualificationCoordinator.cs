using System;

namespace Yagu.Services.Ai;

/// <summary>
/// Pure decide/persist logic for the first-run AI-model qualification flow, kept out of the
/// WinUI-coupled view model so it can be unit-tested. Decides whether the one-time model check should
/// be offered and folds a completed sweep's outcome back into <see cref="Yagu.Services.AppSettings"/>.
/// The heavy lifting (running models) lives in <see cref="SemanticModelQualificationRunner"/>; this type
/// only reads/writes settings.
/// </summary>
public static class SemanticModelQualificationCoordinator
{
    /// <summary>
    /// Whether the first-run qualification should be offered: semantic search is enabled and available,
    /// and the one-time sweep has not yet been completed.
    /// </summary>
    public static bool ShouldOffer(Yagu.Services.AppSettings settings, bool semanticAvailable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return semanticAvailable
            && settings.SemanticSearchEnabled
            && !settings.SemanticModelQualificationCompleted;
    }

    /// <summary>The alias the sweep recommends: the qualified model when one cleared the bar, otherwise
    /// the best-effort fallback. Null when the sweep produced nothing usable.</summary>
    public static string? Suggestion(ModelQualificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.QualifiedModelAlias ?? result.BestEffortModelAlias;
    }

    /// <summary>
    /// Folds a finished sweep into settings: marks the one-time check complete, records the recommended
    /// alias, and — when the user accepts the suggestion — sets it as the effective model override. When
    /// the user picks a different model, pass that alias as <paramref name="chosenAlias"/>.
    /// </summary>
    /// <param name="settings">Settings to update in place.</param>
    /// <param name="result">The completed qualification result.</param>
    /// <param name="accepted">True when the user accepts a model (the suggestion or an override).</param>
    /// <param name="chosenAlias">The model the user chose when accepting; null/empty accepts the
    /// suggested model from <paramref name="result"/>.</param>
    public static void ApplyResult(
        Yagu.Services.AppSettings settings,
        ModelQualificationResult result,
        bool accepted,
        string? chosenAlias = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(result);

        settings.SemanticModelQualificationCompleted = true;

        string? suggestion = Suggestion(result);
        settings.SemanticQualifiedModelAlias = suggestion ?? string.Empty;

        if (!accepted)
            return;

        string? effective = string.IsNullOrWhiteSpace(chosenAlias) ? suggestion : chosenAlias!.Trim();
        if (!string.IsNullOrWhiteSpace(effective))
            settings.SemanticModelAlias = effective;
    }

    /// <summary>Marks the one-time check complete without applying a model, for when the user explicitly
    /// declines ("don't set up now / skip"). A plain "not now" should instead leave settings untouched so
    /// the offer returns on the next launch.</summary>
    public static void MarkDeclined(Yagu.Services.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SemanticModelQualificationCompleted = true;
    }

    /// <summary>Clears the qualification state back to a fresh-install baseline so the one-time first-run
    /// offer is presented again: un-marks the completed flag and forgets both the recommended alias and the
    /// selected model override. Does not touch <see cref="Yagu.Services.AppSettings.SemanticSearchEnabled"/>
    /// — the caller re-enables AI search if it wants <see cref="ShouldOffer"/> to return true.</summary>
    public static void Reset(Yagu.Services.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SemanticModelQualificationCompleted = false;
        settings.SemanticQualifiedModelAlias = string.Empty;
        settings.SemanticModelAlias = string.Empty;
    }
}
