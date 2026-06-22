using Microsoft.AI.Foundry.Local;

namespace Yagu.Services.Ai;

/// <summary>
/// Picks the best locally-runnable chat model for semantic translation. The Foundry Local
/// catalog's <c>ListModelsAsync</c> already returns only models compatible with the registered
/// hardware execution providers (GPU/NPU/CPU), so this selector layers a preference for small,
/// fast instruct models on top — keeping first-run downloads and latency low on machines without
/// a capable GPU/NPU — while honoring an explicit user override.
/// </summary>
public sealed class FoundryModelSelector
{
    private const string LogSource = "Semantic.ModelSelector";

    /// <summary>
    /// Ordered preference of small/fast instruct model alias fragments. Earlier entries win.
    /// Matched case-insensitively as a substring of the catalog alias so minor naming/version
    /// differences (e.g. "phi-4-mini-instruct-cuda-gpu") still resolve.
    ///
    /// <c>phi-4-mini</c> leads because its ONNX export stays coherent well past 4096 tokens (unlike
    /// the phi-3.5-mini trtrtx export, whose LongRoPE long-factor branch degenerates into token-salad
    /// once input+output exceeds 4096) AND, on a machine with a capable GPU, its less-quantized
    /// CUDA/DirectML build is the most accurate small model for this structured JSON-extraction task.
    /// <c>phi-3.5-mini</c> remains a strong runner-up for machines without a GPU. <c>qwen2.5-1.5b</c>
    /// is the next-best lighter option, and the tiny <c>qwen2.5-0.5b</c> sits at the very end as a
    /// last-resort fallback only — it is NOT reliable enough on its own (it drops dates, mis-routes
    /// search terms, and emits invalid JSON), so it is used solely when nothing larger can run.
    ///
    /// Note: which DEVICE variant of the chosen family is used (GPU vs NPU vs CPU) is decided
    /// separately by <see cref="PreferAccurateVariantAsync"/>, which prefers the less-quantized GPU build
    /// when this machine can run it.
    /// </summary>
    public static readonly IReadOnlyList<string> PreferredAliasFragments =
    [
        "phi-4-mini",
        "phi-3.5-mini",
        "qwen2.5-1.5b",
        "phi-3-mini",
        "qwen2.5-3b",
        "deepseek-r1-1.5b",
        "phi-4",
        "mistral-7b",
        "qwen2.5-7b",
        "qwen2.5-0.5b", // last resort: weak, only when nothing better is available
    ];

    // Task/modality fragments that disqualify a model from text-only semantic translation.
    private static readonly string[] ExcludedTaskFragments =
        ["embed", "audio", "transcription", "whisper", "speech", "vision", "image", "rerank"];

    /// <summary>
    /// Resolves the model to use. When <paramref name="overrideAlias"/> is non-empty, that model is
    /// fetched directly (throws via the SDK if it does not exist). Otherwise the hardware-filtered
    /// catalog is ranked by <see cref="SelectAlias"/>.
    /// </summary>
    public async Task<IModel?> SelectAsync(ICatalog catalog, string? overrideAlias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!string.IsNullOrWhiteSpace(overrideAlias))
        {
            string wanted = overrideAlias.Trim();
            LogService.Instance.Verbose(LogSource, $"Resolving requested model override '{wanted}'.");

            // First let Foundry resolve the value as an ALIAS (e.g. "phi-4-mini"), which yields the
            // family model carrying every hardware-available variant. When it resolves as an alias we
            // still apply the accuracy-oriented variant preference below (so "phi-4-mini" upgrades to
            // the less-quantized GPU build when this machine can run it).
            var direct = await catalog.GetModelAsync(wanted, cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                LogService.Instance.Verbose(LogSource, $"Override '{wanted}' resolved as a family alias.");
                return await PreferAccurateVariantAsync(catalog, direct, cancellationToken).ConfigureAwait(false);
            }

            // The alias lookup above only resolves family aliases, never specific variant Ids
            // (e.g. "Phi-4-mini-instruct-cuda-gpu:5"). When the caller named a concrete variant — to
            // force a specific build — resolve it by its unique model id and honor that exact choice
            // (no preference upgrade: an explicit variant id is a deliberate pin).
            var variant = await catalog.GetModelVariantAsync(wanted, cancellationToken).ConfigureAwait(false);
            if (variant is not null)
            {
                LogService.Instance.Verbose(LogSource, $"Override '{wanted}' resolved as a pinned variant id.");
                return variant;
            }

            // Last resort: the value may be a variant id without its version suffix (e.g. ":5"), or a
            // device-specific id that only appears inside an alias' variant list. Resolve the family
            // alias and match against its variants by id/alias (suffix-insensitive).
            string baseWanted = StripVersionSuffix(wanted);
            foreach (var familyAlias in CandidateAliasesFor(wanted))
            {
                var family = await catalog.GetModelAsync(familyAlias, cancellationToken).ConfigureAwait(false);
                var match = family?.Variants?.FirstOrDefault(v => v is not null && (
                    string.Equals(v.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(StripVersionSuffix(v.Id), baseWanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.Alias, wanted, StringComparison.OrdinalIgnoreCase)));
                if (match is not null)
                {
                    // Re-resolve as a dedicated single-variant handle (see PreferAccurateVariantAsync):
                    // a wrapper from Variants routes load/inference back through the family alias.
                    LogService.Instance.Verbose(LogSource,
                        $"Override '{wanted}' matched variant '{match.Id}' under family alias '{familyAlias}'.");
                    var dedicated = await catalog.GetModelVariantAsync(match.Id, cancellationToken).ConfigureAwait(false);
                    return dedicated ?? match;
                }
            }
            LogService.Instance.Warning(LogSource,
                $"Requested model override '{wanted}' was not found in the catalog; no model selected.");
            return null;
        }

        var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models is null || models.Count == 0)
        {
            LogService.Instance.Warning(LogSource, "Model catalog returned no models for this hardware.");
            return null;
        }

        var candidates = models
            .Where(m => m is not null)
            .Select(m => new ModelCandidate(
                Alias: !string.IsNullOrWhiteSpace(m.Alias) ? m.Alias : m.Info?.Alias ?? m.Id,
                FileSizeMb: m.Info?.FileSizeMb,
                Task: m.Info?.Task,
                Device: m.Info?.Runtime.DeviceType ?? DeviceType.CPU))
            .ToList();
        LogService.Instance.Verbose(LogSource, $"Ranking {candidates.Count} hardware-compatible catalog model(s).");

        string? chosenAlias = SelectAlias(candidates);
        if (chosenAlias is null)
        {
            LogService.Instance.Warning(LogSource, "No eligible text-chat model found among catalog candidates.");
            return null;
        }
        LogService.Instance.Info(LogSource, $"Auto-selected model alias '{chosenAlias}'.");

        // Re-fetch the chosen family by alias so the returned IModel carries the full set of
        // hardware-available variants, then upgrade to the most accurate one this machine can run.
        var chosenFamily = await catalog.GetModelAsync(chosenAlias, cancellationToken).ConfigureAwait(false);
        if (chosenFamily is not null)
            return await PreferAccurateVariantAsync(catalog, chosenFamily, cancellationToken).ConfigureAwait(false);

        return models.FirstOrDefault(m =>
            string.Equals(m.Alias, chosenAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Info?.Alias, chosenAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Id, chosenAlias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Upgrades a resolved family model to its most ACCURATE locally-runnable variant. Foundry's
    /// default per-alias pick favors the lowest-power device (NPU &gt; GPU &gt; CPU), but the
    /// aggressively int4-quantized NPU build is measurably worse at this structured-JSON task than
    /// the less-quantized GPU builds. <see cref="IModel.Variants"/> is already filtered to execution
    /// providers this machine actually has, so a GPU variant is only ever chosen when it can run;
    /// otherwise this naturally falls back to NPU and then CPU.
    ///
    /// The chosen variant is re-resolved through <see cref="ICatalog.GetModelVariantAsync"/> so the
    /// returned IModel is a DEDICATED single-variant handle. Operating on a wrapper pulled straight
    /// from <see cref="IModel.Variants"/> routes load/inference back through the family alias (whose
    /// default device is the NPU), which would silently undo the upgrade. Returns
    /// <paramref name="family"/> unchanged when no better, separately-resolvable variant exists.
    /// </summary>
    private static async Task<IModel> PreferAccurateVariantAsync(
        ICatalog catalog, IModel family, CancellationToken cancellationToken)
    {
        IReadOnlyList<IModel>? variants = null;
        try { variants = family.Variants?.ToList(); } catch { /* SDK may not populate variants */ }

        if (variants is null || variants.Count <= 1) return family;

        IModel? best = null;
        int bestScore = int.MinValue;
        foreach (var v in variants)
        {
            if (v is null) continue;
            int score = VariantAccuracyScore(v);
            if (score > bestScore) { best = v; bestScore = score; }
        }
        if (best is null || string.IsNullOrWhiteSpace(best.Id) ||
            string.Equals(best.Id, family.Id, StringComparison.OrdinalIgnoreCase))
            return family; // already on the best variant

        // Re-resolve as a dedicated single-variant handle so load/inference actually target it.
        LogService.Instance.Verbose(LogSource,
            $"Preferring more-accurate variant '{best.Id}' (score {bestScore}) over family default '{family.Id}'.");
        var dedicated = await catalog.GetModelVariantAsync(best.Id, cancellationToken).ConfigureAwait(false);
        return dedicated ?? family;
    }

    /// <summary>
    /// Accuracy-oriented score for a model variant. Device tier dominates (GPU &gt; NPU &gt; CPU per
    /// the chosen policy); within GPU, less-quantized runtimes win (CUDA &gt; DirectML/generic &gt;
    /// TensorRT-RTX &gt; OpenVINO-GPU).
    /// </summary>
    private static int VariantAccuracyScore(IModel variant)
    {
        string id = (variant.Id ?? variant.Alias ?? string.Empty).ToLowerInvariant();
        DeviceType device = variant.Info?.Runtime.DeviceType ?? DeviceType.CPU;

        int deviceTier = device switch
        {
            DeviceType.GPU => 300,
            DeviceType.NPU => 200,
            DeviceType.CPU => 100,
            _ => 0,
        };

        int runtimeBonus =
            id.Contains("cuda") ? 40 :
            id.Contains("generic") ? 30 :   // DirectML generic GPU/CPU build (less quantized)
            id.Contains("trtrtx") || id.Contains("tensorrt") ? 20 :
            id.Contains("openvino") ? 10 :
            0;

        return deviceTier + runtimeBonus;
    }

    /// <summary>
    /// Pure ranking over candidate models. Order: (1) earliest match in
    /// <see cref="PreferredAliasFragments"/>; ties broken by device (NPU &gt; GPU &gt; CPU) then
    /// smaller file size. (2) If nothing matches the preference list, the smallest eligible
    /// (non-embedding/audio/vision) text model. Returns null when no eligible model exists.
    /// </summary>
    public static string? SelectAlias(IReadOnlyList<ModelCandidate> candidates)
    {
        if (candidates is null || candidates.Count == 0) return null;

        var eligible = candidates.Where(IsTextChatCandidate).ToList();
        if (eligible.Count == 0) eligible = candidates.ToList(); // fall back to anything compatible

        ModelCandidate? best = null;
        int bestRank = int.MaxValue;
        foreach (var c in eligible)
        {
            int rank = PreferenceRank(c.Alias);
            if (rank < bestRank ||
                (rank == bestRank && best is not null && IsBetterTiebreak(c, best.Value)))
            {
                best = c;
                bestRank = rank;
            }
        }

        if (best is { } b && bestRank != int.MaxValue) return b.Alias;

        // No preference match: smallest by size, device as secondary preference.
        var smallest = eligible
            .OrderBy(c => c.FileSizeMb ?? int.MaxValue)
            .ThenByDescending(c => DeviceWeight(c.Device))
            .ThenBy(c => c.Alias, StringComparer.OrdinalIgnoreCase)
            .First();
        return smallest.Alias;
    }

    /// <summary>Removes a trailing catalog version suffix (e.g. ":5") from a model id/alias.</summary>
    private static string StripVersionSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        int colon = value.LastIndexOf(':');
        // Only strip when the tail is purely digits (a version), not a drive-letter or namespace colon.
        if (colon > 0 && colon < value.Length - 1 && value[(colon + 1)..].All(char.IsDigit))
            return value[..colon];
        return value;
    }

    /// <summary>
    /// Family aliases to probe when resolving a concrete variant id. Returns every preference-list
    /// fragment contained in the id (longest first so "phi-4-mini" wins over "phi-4"), plus the id
    /// with its version suffix stripped as a final candidate.
    /// </summary>
    private static IEnumerable<string> CandidateAliasesFor(string variantId)
    {
        string lowered = (variantId ?? string.Empty).ToLowerInvariant();
        foreach (var frag in PreferredAliasFragments
                     .Where(f => lowered.Contains(f, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => f.Length))
            yield return frag;
        string stripped = StripVersionSuffix(variantId);
        if (!string.IsNullOrWhiteSpace(stripped)) yield return stripped;
    }

    private static bool IsTextChatCandidate(ModelCandidate c)
    {
        if (string.IsNullOrWhiteSpace(c.Task)) return true; // unknown task: assume usable
        string task = c.Task.ToLowerInvariant();
        return !ExcludedTaskFragments.Any(task.Contains);
    }

    private static int PreferenceRank(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return int.MaxValue;
        string a = alias.ToLowerInvariant();
        for (int i = 0; i < PreferredAliasFragments.Count; i++)
            if (a.Contains(PreferredAliasFragments[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return int.MaxValue;
    }

    /// <summary>Preference rank of an alias (lower is better; <see cref="int.MaxValue"/> when the
    /// alias is not in the preference list). Exposed for option-list building.</summary>
    public static int RankOf(string alias) => PreferenceRank(alias);

    /// <summary>Whether a catalog model's task makes it usable for text chat / structured
    /// translation (excludes embedding, audio, vision, rerank models).</summary>
    public static bool IsTextChatModel(string? task) =>
        IsTextChatCandidate(new ModelCandidate(string.Empty, null, task, DeviceType.CPU));

    private static bool IsBetterTiebreak(ModelCandidate candidate, ModelCandidate current)
    {
        int dw = DeviceWeight(candidate.Device).CompareTo(DeviceWeight(current.Device));
        if (dw != 0) return dw > 0;
        return (candidate.FileSizeMb ?? int.MaxValue) < (current.FileSizeMb ?? int.MaxValue);
    }

    private static int DeviceWeight(DeviceType device) => device switch
    {
        DeviceType.NPU => 3,
        DeviceType.GPU => 2,
        DeviceType.CPU => 1,
        _ => 0,
    };

    /// <summary>Lightweight projection of a catalog model used by the pure ranking logic.</summary>
    public readonly record struct ModelCandidate(string Alias, int? FileSizeMb, string? Task, DeviceType Device);
}
