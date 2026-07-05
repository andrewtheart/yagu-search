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

    // Task/modality fragments that disqualify a model from text-only semantic translation. Matched
    // against BOTH the catalog Task field AND the model alias/id: the catalog Task is frequently unset
    // (e.g. the whisper-tiny CPU build reports no Task), so relying on Task alone lets a speech/embedding
    // model slip through as "unknown task: assume usable" and get auto-selected — then fail every request
    // (whisper is ASR with a 448-token context that cannot hold the ~8.5K-token system prompt). The model
    // NAME reliably carries the modality token ("openai-whisper-tiny-generic-cpu"), so we screen it too.
    private static readonly string[] ExcludedTaskFragments =
        ["embed", "audio", "transcription", "whisper", "speech", "vision", "image", "rerank"];

    /// <summary>
    /// Default accelerator-build preference order: GPU first (its less-quantized build is the most
    /// accurate for this structured-JSON task), then NPU, then CPU. Used when no user override is set.
    /// </summary>
    public static readonly IReadOnlyList<DeviceType> DefaultDeviceOrder =
        [DeviceType.GPU, DeviceType.NPU, DeviceType.CPU];

    /// <summary>
    /// Parses a comma/semicolon-separated device-order string (e.g. "NPU,GPU,CPU") into an ordered,
    /// de-duplicated device list. Unrecognized tokens are ignored; any of GPU/NPU/CPU missing from the
    /// input are appended in <see cref="DefaultDeviceOrder"/> order. Empty/invalid input yields the
    /// default order, so callers always get a complete, valid ranking.
    /// </summary>
    public static IReadOnlyList<DeviceType> ParseDeviceOrder(string? order)
    {
        if (string.IsNullOrWhiteSpace(order)) return DefaultDeviceOrder;

        var result = new List<DeviceType>(3);
        foreach (var token in order.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            DeviceType? device = token.ToUpperInvariant() switch
            {
                "GPU" => DeviceType.GPU,
                "NPU" => DeviceType.NPU,
                "CPU" => DeviceType.CPU,
                _ => null,
            };
            if (device is { } d && !result.Contains(d)) result.Add(d);
        }
        // Ensure every device is present so ranking is total even if the user omitted one.
        foreach (var d in DefaultDeviceOrder)
            if (!result.Contains(d)) result.Add(d);
        return result.Count == 0 ? DefaultDeviceOrder : result;
    }

    /// <summary>
    /// Resolves the model to use. When <paramref name="overrideAlias"/> is non-empty, that model is
    /// fetched directly (throws via the SDK if it does not exist). Otherwise the hardware-filtered
    /// catalog is ranked by <see cref="SelectAlias"/>.
    /// </summary>
    public static async Task<IModel?> SelectAsync(ICatalog catalog, string? overrideAlias, CancellationToken cancellationToken)
        => await SelectAsync(catalog, overrideAlias, DefaultDeviceOrder, availableDevices: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Resolves the model to use, honoring a caller-supplied <paramref name="deviceOrder"/> when
    /// choosing which accelerator build of the chosen family to run.
    /// </summary>
    public static async Task<IModel?> SelectAsync(ICatalog catalog, string? overrideAlias, IReadOnlyList<DeviceType>? deviceOrder, CancellationToken cancellationToken)
        => await SelectAsync(catalog, overrideAlias, deviceOrder, availableDevices: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Backward-compatible overload without a memory budget (the memory guard is disabled).</summary>
    public static async Task<IModel?> SelectAsync(ICatalog catalog, string? overrideAlias, IReadOnlyList<DeviceType>? deviceOrder, IReadOnlySet<DeviceType>? availableDevices, CancellationToken cancellationToken)
        => await SelectAsync(catalog, overrideAlias, deviceOrder, availableDevices, availableMemoryMb: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Resolves the model to use, honoring a caller-supplied <paramref name="deviceOrder"/> (which
    /// accelerator build to PREFER) and <paramref name="availableDevices"/> (which execution devices
    /// this machine can ACTUALLY run). Variants whose device is not in <paramref name="availableDevices"/>
    /// are hard-excluded so a GPU/NPU build is never loaded on hardware that would crash during
    /// inference: Foundry's <c>download_and_register_eps</c> registers DirectML on virtually any Windows
    /// box (even one with no usable GPU), so a "generic-gpu" variant can LOAD yet fault (native access
    /// violation) on the first inference. Pass a null <paramref name="availableDevices"/> to disable the
    /// filter (legacy behavior). CPU should always be included by the caller.
    /// </summary>
    public static async Task<IModel?> SelectAsync(ICatalog catalog, string? overrideAlias, IReadOnlyList<DeviceType>? deviceOrder, IReadOnlySet<DeviceType>? availableDevices, int? availableMemoryMb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        deviceOrder ??= DefaultDeviceOrder;

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
                return await PreferAccurateVariantAsync(catalog, direct, deviceOrder, availableDevices, cancellationToken).ConfigureAwait(false);
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

        // Resolve each family to the variant this machine will ACTUALLY run. Foundry's family-level Info
        // (Runtime.DeviceType, FileSizeMb) reflects its GPU/DirectML default, which is UNRELIABLE on a
        // CPU-only box: DirectML registers on almost any Windows machine, so every chat family reports
        // "GPU". A reported-device filter then drops EVERY chat model, leaving only the non-chat
        // (embedding/whisper) families that happen to report CPU -- which are correctly rejected, yielding
        // a bogus "no compatible model" even though the picker lists dozens of CPU chat models. Descending
        // to the best runnable variant (device parsed from the id via ResolveVariantDevice, honoring
        // availableDevices) gives the REAL device AND the CPU build's size -- matching the picker and what
        // PreferAccurateVariantAsync will load. Families with no runnable variant are dropped (never
        // auto-picked, so a GPU/NPU-only family can't be chosen then crash). ListModelsAsync items may not
        // carry Variants, so re-fetch each family by alias (as the picker does); this is a once-per-session
        // path so the extra catalog lookups are cheap.
        var candidates = new List<ModelCandidate>(models.Count);
        foreach (var m in models)
        {
            if (m is null) continue;
            string alias = !string.IsNullOrWhiteSpace(m.Alias) ? m.Alias : m.Info?.Alias ?? m.Id;
            IModel family = await catalog.GetModelAsync(alias, cancellationToken).ConfigureAwait(false) ?? m;
            IModel? runnable = BestRunnableVariant(family, deviceOrder, availableDevices);
            if (runnable is null) continue; // no build this machine can run
            candidates.Add(new ModelCandidate(
                Alias: alias,
                FileSizeMb: runnable.Info?.FileSizeMb,
                Task: runnable.Info?.Task,
                Device: ResolveVariantDevice(runnable)));
        }
        if (candidates.Count == 0)
        {
            LogService.Instance.Warning(LogSource, "No catalog model has a variant this machine can run.");
            return null;
        }
        LogService.Instance.Verbose(LogSource, $"Ranking {candidates.Count} runnable catalog model(s).");

        string? chosenAlias = SelectAlias(candidates, availableMemoryMb);
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
            return await PreferAccurateVariantAsync(catalog, chosenFamily, deviceOrder, availableDevices, cancellationToken).ConfigureAwait(false);

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
        ICatalog catalog, IModel family, IReadOnlyList<DeviceType> deviceOrder, IReadOnlySet<DeviceType>? availableDevices, CancellationToken cancellationToken)
    {
        List<IModel>? variants = null;
        try { variants = family.Variants?.ToList(); } catch { /* SDK may not populate variants */ }

        if (variants is null || variants.Count <= 1) return family;

        IModel? best = null;
        int bestScore = int.MinValue;
        foreach (var v in variants)
        {
            if (v is null) continue;
            // Hard-exclude variants whose execution device this machine cannot actually run. Foundry's
            // download_and_register_eps registers DirectML on virtually any Windows box (even one with
            // no usable GPU), so a "generic-gpu" variant can LOAD yet crash (native access violation)
            // during inference. availableDevices is what Yagu's own capability detector confirmed; CPU is
            // always included, so an unaccelerated machine deterministically falls back to the CPU build.
            if (availableDevices is not null)
            {
                DeviceType variantDevice = ResolveVariantDevice(v);
                if (!availableDevices.Contains(variantDevice)) continue;
            }
            int score = VariantAccuracyScore(v, deviceOrder);
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
    private static int VariantAccuracyScore(IModel variant, IReadOnlyList<DeviceType> deviceOrder)
    {
        string id = (variant.Id ?? variant.Alias ?? string.Empty).ToLowerInvariant();
        DeviceType device = ResolveVariantDevice(variant);

        // Earlier in the configured order = higher tier. A device not listed scores 0.
        int idx = -1;
        for (int i = 0; i < deviceOrder.Count; i++)
            if (deviceOrder[i] == device) { idx = i; break; }
        int deviceTier = idx >= 0 ? (deviceOrder.Count - idx) * 100 : 0;

        int runtimeBonus =
            id.Contains("cuda") ? 40 :
            id.Contains("generic") ? 30 :   // DirectML generic GPU/CPU build (less quantized)
            id.Contains("trtrtx") || id.Contains("tensorrt") ? 20 :
            id.Contains("openvino") ? 10 :
            0;

        return deviceTier + runtimeBonus;
    }

    /// <summary>
    /// Determines a variant's execution device from its ID (the reliable signal — e.g. "...-generic-cpu:3",
    /// "...-cuda-gpu:5", "...-qnn-npu:1"), falling back to the catalog-reported DeviceType. Foundry can
    /// report a misleading FAMILY-level DeviceType (it reflects the registered execution provider —
    /// DirectML registers on almost any Windows box, including Windows Sandbox — not the real hardware),
    /// so the ID string is trusted first.
    /// </summary>
    internal static DeviceType ResolveVariantDevice(IModel variant)
    {
        string id = (variant?.Id ?? variant?.Alias ?? string.Empty).ToLowerInvariant();
        if (id.Contains("-cpu")) return DeviceType.CPU;
        if (id.Contains("-npu") || id.Contains("qnn") || id.Contains("vitisai")) return DeviceType.NPU;
        if (id.Contains("-gpu") || id.Contains("cuda") || id.Contains("directml") ||
            id.Contains("trtrtx") || id.Contains("tensorrt")) return DeviceType.GPU;
        return variant?.Info?.Runtime?.DeviceType ?? DeviceType.CPU;
    }

    /// <summary>
    /// Returns the best variant of <paramref name="family"/> that runs on one of
    /// <paramref name="availableDevices"/> (device determined by <see cref="ResolveVariantDevice"/>), or
    /// null when the family has no runnable variant. Used by the model picker to show the build Yagu will
    /// actually use rather than Foundry's misleading family-level default. With a null
    /// <paramref name="availableDevices"/> the device filter is skipped.
    /// </summary>
    public static IModel? BestRunnableVariant(IModel family, IReadOnlyList<DeviceType>? deviceOrder, IReadOnlySet<DeviceType>? availableDevices)
    {
        if (family is null) return null;
        deviceOrder ??= DefaultDeviceOrder;

        List<IModel>? variants = null;
        try { variants = family.Variants?.Where(v => v is not null).ToList(); } catch { /* SDK may not populate */ }
        if (variants is null || variants.Count == 0) variants = [family];

        IModel? best = null;
        int bestScore = int.MinValue;
        foreach (var v in variants)
        {
            if (availableDevices is not null && !availableDevices.Contains(ResolveVariantDevice(v))) continue;
            int score = VariantAccuracyScore(v, deviceOrder);
            if (score > bestScore) { best = v; bestScore = score; }
        }
        return best;
    }

    /// <summary>
    /// Pure ranking over candidate models. Order: (1) earliest match in
    /// <see cref="PreferredAliasFragments"/>; ties broken by device (NPU &gt; GPU &gt; CPU) then
    /// smaller file size. (2) If nothing matches the preference list, the smallest eligible
    /// (non-embedding/audio/vision) text model. Returns null when no eligible model exists.
    ///
    /// Auto-selection excludes reasoning/chain-of-thought models (their &lt;think&gt; prose violates
    /// the strict-JSON contract and they are far heavier at generation time). When
    /// <paramref name="availableMemoryMb"/> is supplied (CPU inference), models whose weights plus
    /// KV-cache/runtime headroom cannot fit available physical RAM are dropped first — such a model
    /// downloads and even loads, then OOMs ("bad allocation") while allocating the large prompt's
    /// token sequences.
    /// </summary>
    public static string? SelectAlias(IReadOnlyList<ModelCandidate> candidates, int? availableMemoryMb = null)
    {
        if (candidates is null || candidates.Count == 0) return null;

        var eligible = candidates.Where(IsAutoSelectable).ToList();
        if (eligible.Count == 0) eligible = candidates.Where(IsTextChatCandidate).ToList();
        if (eligible.Count == 0)
        {
            // Nothing text-chat-capable in the catalog. NEVER fall back to a task-incompatible model
            // (embedding / audio / whisper / vision): it cannot perform JSON translation and would fail
            // every request (e.g. whisper-tiny's 448-token context). Returning null makes the caller
            // report that no compatible model is available instead of silently selecting an unusable one.
            LogService.Instance.Warning(LogSource,
                "No text-chat-capable model in catalog; refusing to fall back to a non-chat model.");
            return null;
        }

        // Memory guard (CPU only): prefer models that actually fit. If NOTHING fits the budget, give
        // the lightest model the best chance rather than the highest-preference (heaviest) one, which
        // would just OOM again.
        if (availableMemoryMb is { } budgetMb)
        {
            var fits = eligible.Where(c => FitsInMemory(c, budgetMb)).ToList();
            if (fits.Count > 0)
                eligible = fits;
            else
                return SmallestAlias(eligible);
        }

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
        return SmallestAlias(eligible);
    }

    private static string SmallestAlias(IReadOnlyList<ModelCandidate> eligible) => eligible
        .OrderBy(c => c.FileSizeMb ?? int.MaxValue)
        .ThenByDescending(c => DeviceWeight(c.Device))
        .ThenBy(c => c.Alias, StringComparer.OrdinalIgnoreCase)
        .First().Alias;

    /// <summary>Whether a candidate's estimated footprint (weights + a size-scaled headroom) fits within
    /// <paramref name="availableMemoryMb"/>. Delegates the pure math to <see cref="ModelMemoryBudget"/>
    /// (unit-tested; this file pulls in Foundry types and cannot be). Models of unknown size pass.</summary>
    private static bool FitsInMemory(ModelCandidate candidate, int availableMemoryMb)
        => ModelMemoryBudget.Fits(candidate.FileSizeMb, availableMemoryMb);

    // Reasoning / chain-of-thought model markers, excluded from AUTO selection (an explicit user
    // override can still force one). Matched as a lowercase substring of the alias — chiefly to stop the
    // "phi-4-mini" preference fragment from accidentally selecting "phi-4-mini-reasoning".
    private static readonly string[] ReasoningAliasFragments = ["reasoning"];

    private static bool IsAutoSelectable(ModelCandidate c)
        => IsTextChatCandidate(c) && !IsReasoningAlias(c.Alias);

    /// <summary>Whether <paramref name="alias"/> names a reasoning / chain-of-thought model (e.g.
    /// <c>phi-4-reasoning</c>). Such models are excluded from AUTO selection but can be chosen by an
    /// explicit user override; the translator then tunes generation (longer token budget, looser
    /// sampling, <c>&lt;think&gt;</c> stripping) so the model's reasoning trace doesn't crowd out the
    /// JSON plan. Exposed so the translator can apply that reasoning-specific configuration.</summary>
    internal static bool IsReasoningAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return false;
        string a = alias.ToLowerInvariant();
        return ReasoningAliasFragments.Any(f => a.Contains(f, StringComparison.Ordinal));
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
        // Screen the alias/id FIRST: the catalog Task field is frequently null/unset (notably the
        // whisper-tiny CPU build), so a speech/embedding/vision model would otherwise pass the
        // "unknown task: assume usable" branch below, get auto-selected as the smallest model, and then
        // fail hard (e.g. whisper's 448-token context vs the ~8.5K-token prompt). The model NAME always
        // carries the modality token, so an excluded fragment in the alias disqualifies it outright.
        string alias = c.Alias?.ToLowerInvariant() ?? string.Empty;
        if (alias.Length > 0 && ExcludedTaskFragments.Any(f => alias.Contains(f, StringComparison.Ordinal)))
            return false;

        if (string.IsNullOrWhiteSpace(c.Task)) return true; // unknown task: assume usable (name already screened)
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

    /// <summary>
    /// Whether a catalog model — identified by BOTH its alias/id and its task — is usable for text
    /// chat / structured translation. Prefer this over the task-only overload: the catalog Task field
    /// is frequently unset (e.g. the whisper-tiny CPU build), so screening the alias/id catches
    /// speech/embedding/vision models that a task-only check would let through.
    /// </summary>
    public static bool IsTextChatModel(string? alias, string? task) =>
        IsTextChatCandidate(new ModelCandidate(alias ?? string.Empty, null, task, DeviceType.CPU));

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
