using System.Collections.Generic;

namespace Yagu.Services.Ai;

/// <summary>
/// Pure policy for upgrading AUTO model selection to a larger, more-accurate model when the machine
/// has ample GPU VRAM. A strong GPU should run the better model instead of the small low-latency
/// default (the ranked <c>PreferredAliasFragments</c> list otherwise always picks the smallest capable
/// family, e.g. phi-4-mini, regardless of how much GPU headroom exists). Weak / low-VRAM machines and
/// CPU/NPU-only machines get no upgrade and keep the small default; an explicit user model override
/// bypasses this entirely. Kept separate and pure so it is directly unit-testable — FoundryModelSelector
/// and the translator pull in Foundry SDK types and cannot be compiled into the test assembly.
/// </summary>
public static class HighAccuracyModelPolicy
{
    /// <summary>
    /// Larger, more-accurate model families to prefer, each gated by a minimum dedicated-VRAM budget
    /// (in MB). Ordered best-first. Deliberately small and conservative: phi-4 (14B) needs a big card
    /// (~16 GB+) to run well, so a smaller GPU keeps the lightweight default. Aliases are the Foundry
    /// family aliases; the caller confirms one actually resolves + fits the machine before loading it.
    /// </summary>
    public static readonly IReadOnlyList<(string Alias, int MinVramMb)> Models =
    [
        ("phi-4", 16384),
    ];

    /// <summary>
    /// Returns the best (most-accurate) alias from <see cref="Models"/> whose VRAM bar is met by
    /// <paramref name="availableVramMb"/>, or <c>null</c> when none qualifies — in which case auto
    /// selection keeps its normal small-model default. A non-positive budget (unknown VRAM, or a
    /// CPU/NPU-only machine) always yields <c>null</c>.
    /// </summary>
    public static string? UpgradeAliasFor(int availableVramMb)
    {
        if (availableVramMb <= 0)
            return null;

        foreach ((string alias, int minVramMb) in Models)
            if (availableVramMb >= minVramMb)
                return alias;

        return null;
    }
}
