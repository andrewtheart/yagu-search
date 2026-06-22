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
    /// <summary>
    /// Ordered preference of small/fast instruct model alias fragments. Earlier entries win.
    /// Matched case-insensitively as a substring of the catalog alias so minor naming/version
    /// differences (e.g. "phi-3.5-mini-instruct-generic-gpu") still resolve.
    ///
    /// <c>phi-3.5-mini</c> leads because it is the most accurate small instruct model for this
    /// structured JSON-extraction task (it reliably keeps dates, picks the right size direction,
    /// and distinguishes created-vs-modified). <c>qwen2.5-1.5b</c> is the next-best lighter option,
    /// and the tiny <c>qwen2.5-0.5b</c> sits at the very end as a last-resort fallback only — it is
    /// NOT reliable enough on its own (it drops dates, mis-routes search terms, and emits invalid
    /// JSON), so it is used solely when nothing larger can run on the machine.
    /// </summary>
    public static readonly IReadOnlyList<string> PreferredAliasFragments =
    [
        "phi-3.5-mini",
        "qwen2.5-1.5b",
        "phi-3-mini",
        "phi-4-mini",
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
            return await catalog.GetModelAsync(overrideAlias.Trim(), cancellationToken).ConfigureAwait(false);

        var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models is null || models.Count == 0) return null;

        var candidates = models
            .Where(m => m is not null)
            .Select(m => new ModelCandidate(
                Alias: !string.IsNullOrWhiteSpace(m.Alias) ? m.Alias : m.Info?.Alias ?? m.Id,
                FileSizeMb: m.Info?.FileSizeMb,
                Task: m.Info?.Task,
                Device: m.Info?.Runtime.DeviceType ?? DeviceType.CPU))
            .ToList();

        string? chosenAlias = SelectAlias(candidates);
        if (chosenAlias is null) return null;

        return models.FirstOrDefault(m =>
            string.Equals(m.Alias, chosenAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Info?.Alias, chosenAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Id, chosenAlias, StringComparison.OrdinalIgnoreCase));
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
