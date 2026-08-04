using Yagu.Services.Ai;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Turning a natural-language query into search inputs with the on-device model: the translation
/// run, its outcome states, progress reporting and cancellation. The query never leaves the machine.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Cancels an in-flight semantic translation (the local-model inference that turns a
    /// natural-language query into search settings). Wired to the morphing Cancel button so a user
    /// can abort the AI step the same way they cancel a running file search.</summary>
    public void CancelSemanticTranslation()
    {
        if (IsTranslatingSemanticQuery) IsCancelling = true;
        try { _semanticCts?.Cancel(); } catch { }
        SemanticStatusText = string.Empty;
    }

    /// <summary>Outcome of <see cref="TranslateSemanticQueryAsync"/>.</summary>
    public enum SemanticTranslationOutcome
    {
        /// <summary>The model's plan was applied to this view-model; run the semantic search.</summary>
        Applied,
        /// <summary>The model produced no usable plan, but a deterministic best-guess salvage was applied
        /// from the raw query (file types, content term, OCR, hidden, folder). Run it like a normal plan;
        /// the status line tells the user it is a best guess.</summary>
        Salvaged,
        /// <summary>The model could not produce a usable plan; the caller may fall back to a literal search.</summary>
        Failed,
        /// <summary>Translation was cancelled or there was nothing to translate; do not search.</summary>
        Aborted,
    }

    /// <summary>
    /// Translates the current natural-language <see cref="Query"/> into concrete search settings via
    /// the local model and applies them to this view-model. Returns <see cref="SemanticTranslationOutcome.Applied"/>
    /// when settings were applied, <see cref="SemanticTranslationOutcome.Failed"/> when the model produced no
    /// usable plan (caller may fall back to a literal search), and <see cref="SemanticTranslationOutcome.Aborted"/>
    /// when the user cancelled or there was nothing to translate.
    /// </summary>
    public async Task<SemanticTranslationOutcome> TranslateSemanticQueryAsync()
    {
        if (_semanticTranslator is null || !_semanticTranslator.IsAvailable)
            return SemanticTranslationOutcome.Failed;

        var text = Query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            ErrorText = "Describe what you want to find.";
            return SemanticTranslationOutcome.Aborted;
        }

        // A single token cannot express a natural-language search request. Skip model startup entirely
        // and let the caller run a plain Traditional search for the typed text. Set an accurate status
        // so the caller's generic "AI couldn't interpret that" message is not shown.
        if (SemanticQuerySalvage.IsSingleTokenQuery(text))
        {
            SemanticStatusText = $"\u201C{text}\u201D isn't a natural-language query \u2014 searching for it directly.";
            return SemanticTranslationOutcome.Failed;
        }

        try { _semanticCts?.Cancel(); } catch { }
        _semanticCts?.Dispose();
        _semanticCts = new CancellationTokenSource();
        var token = _semanticCts.Token;

        IsTranslatingSemanticQuery = true;
        SemanticStatusText = "Preparing the local AI model…";
        ErrorText = string.Empty;

        var progress = new Progress<SemanticTranslationProgress>(p =>
        {
            if (!token.IsCancellationRequested) SemanticStatusText = p.Message;
        });

        try
        {
            var context = new SemanticTranslationContext
            {
                Now = DateTimeOffset.Now,
                // Do NOT seed the model with the current box value: the directory must reflect ONLY what
                // the model interprets. A confidently-named path is applied below; anything else leaves
                // the directory box exactly as the user left it.
                DefaultDirectory = null,
                OriginalQuery = text,
                // A model-hallucinated directory that does not exist is treated as "no confident path"
                // (dropped to null), so the directory box is left unchanged rather than pointed at a
                // bogus location.
                DirectoryExists = static d => System.IO.Directory.Exists(d),
            };

            // Run the translation on a background thread. The translator's first-call initialization
            // (Foundry catalog/EP setup, model selection and load) runs SYNCHRONOUSLY up to its first
            // real await — the init SemaphoreSlim.WaitAsync completes inline when uncontended — so calling
            // it directly would block the UI thread on the first semantic search of each launch, delaying
            // the just-set query text from painting. Task.Run keeps that one-time cost off the UI thread;
            // progress still marshals back via the captured context, and ConfigureAwait(true) resumes here
            // on the UI thread to apply the plan.
            var result = await Task.Run(
                () => _semanticTranslator.TranslateAsync(text, context, progress, token), token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                SemanticStatusText = string.Empty;
                return SemanticTranslationOutcome.Aborted;
            }

            if (!result.Success || result.Plan is null)
            {
                // The model returned no usable plan (small on-device models often do this for bare
                // code tokens like "#define", and phi-mini has narrow quirks such as failing "jpg files
                // containing the word secret"). Before dropping to a bare literal search, try a
                // DETERMINISTIC best-guess salvage that rebuilds the obvious parts of the query — file
                // types, a content term, image OCR, hidden-file preference, a known folder — with the
                // same rules the model is taught. When it recovers something, apply it and tell the user
                // it is a best guess; otherwise fall through to the literal fallback.
                if (SemanticQuerySalvage.TryBuildPlan(text, out var salvagePlan))
                {
                    var salvaged = SemanticPlanApplier.ApplyToTarget(salvagePlan, context, this);
                    EnableArchiveSearchForContainerGlobs(salvaged.IncludeGlobs);
                    EnableBinarySearchForBinaryGlobs(salvaged.IncludeGlobs);
                    SemanticStatusText = "AI couldn't interpret that — using our best guess: "
                        + SemanticPlanApplier.BuildExplanation(salvaged, Directory);
                    return SemanticTranslationOutcome.Salvaged;
                }

                SemanticStatusText = string.Empty;
                return SemanticTranslationOutcome.Failed;
            }

            var resolved = SemanticPlanApplier.ApplyToTarget(result.Plan, context, this);
            // Adopt the directory ONLY when the model confidently named one (ApplyToTarget already set it
            // above in that case). When the query does not clearly contain a path, leave the directory box
            // exactly as the user left it instead of clearing it — clearing would silently widen the search
            // to all drives. The HDD check still runs against whatever location is in the box, via the
            // post-translation gate in SubmitSearchAsync.
            EnableArchiveSearchForContainerGlobs(resolved.IncludeGlobs);
            EnableBinarySearchForBinaryGlobs(resolved.IncludeGlobs);
            // Render the summary deterministically from the resolved plan rather than the model's
            // free-text explanation, which small on-device models often garble (e.g. "yagursd").
            // Pass the live directory box as the effective directory so an unscoped query (the model
            // resolves no directory) is described as the box's location — not the misleading "all
            // drives" — since the actual search honors whatever is in the box.
            string interpretation = SemanticPlanApplier.BuildExplanation(resolved, Directory);
            // Surface any warnings the plan raised (e.g. an unsupported content exclusion like "but not
            // X", or an exclusion that would have removed all matches) so the user knows part of the
            // request was not honored instead of silently dropping it. The CLI already prints these.
            if (resolved.Warnings.Count > 0)
                interpretation += "  \u26A0 " + string.Join("  \u26A0 ", resolved.Warnings);
            SemanticStatusText = interpretation;
            return SemanticTranslationOutcome.Applied;
        }
        catch (OperationCanceledException)
        {
            SemanticStatusText = string.Empty;
            return SemanticTranslationOutcome.Aborted;
        }
        catch (Exception ex)
        {
            YaguLog.For("SemanticSearch").LogWarning(ex, "Translation failed: {Error}", ex.Message);
            SemanticStatusText = string.Empty;
            return SemanticTranslationOutcome.Failed;
        }
        finally
        {
            IsTranslatingSemanticQuery = false;
        }
    }
}
