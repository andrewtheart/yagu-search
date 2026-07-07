using System;
using System.Collections.Generic;

namespace Yagu.Services.Ai;

/// <summary>
/// Pure scorer that decides whether a model's resolved plan satisfies a qualification probe.
/// A probe passes only when EVERY non-null expectation on the probe is met; a null/absent plan
/// (the model produced no parseable output) always fails. Kept free of Foundry/WinUI dependencies
/// so it is fully unit-testable.
/// </summary>
public static class SemanticProbeScorer
{
    /// <summary>True when <paramref name="plan"/> satisfies all specified expectations of
    /// <paramref name="probe"/>. A null plan (no interpretation produced) fails.</summary>
    public static bool Passes(SemanticProbe probe, ResolvedSearchPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (plan is null)
            return false;

        if (probe.ExpectedSearchMode is { } mode && plan.SearchMode != mode)
            return false;

        if (probe.ExpectedIncludeGlob is { } glob && !ContainsGlob(plan.IncludeGlobs, glob))
            return false;

        if (probe.ExpectedPatternContains is { } term &&
            (plan.Pattern is null ||
             plan.Pattern.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0))
            return false;

        if (probe.ExpectedHasDateFilter is true && !HasAnyDateFilter(plan))
            return false;

        if (probe.ExpectedSearchHidden is { } hidden && plan.SearchHiddenFiles != hidden)
            return false;

        if (probe.ExpectedExcludeGlobContains is { } exSub && !ContainsGlobSubstring(plan.ExcludeGlobs, exSub))
            return false;

        if (probe.ExpectedSearchInsideArchives is { } arch && plan.SearchInsideArchives != arch)
            return false;

        if (probe.ExpectedSearchImageText is { } ocr && plan.SearchImageText != ocr)
            return false;

        if (probe.ExpectedUseRegex is { } rx && plan.UseRegex != rx)
            return false;

        if (probe.ExpectedExactMatch is { } exact && plan.ExactMatch != exact)
            return false;

        if (probe.ExpectedMultiline is { } ml && plan.Multiline != ml)
            return false;

        if (probe.ExpectedObeyGitignore is { } gi && plan.ObeyGitignore != gi)
            return false;

        if (probe.ExpectedHasCreatedBefore is true && plan.CreatedBeforeDate is null)
            return false;

        return true;
    }

    /// <summary>Fraction (0..1) of <paramref name="probes"/> whose paired plan passes. Both lists must
    /// be the same length and aligned by index; an empty set scores 0.</summary>
    public static double Accuracy(IReadOnlyList<SemanticProbe> probes, IReadOnlyList<ResolvedSearchPlan?> plans)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(plans);
        if (probes.Count == 0)
            return 0.0;
        if (probes.Count != plans.Count)
            throw new ArgumentException("probes and plans must be the same length.", nameof(plans));

        int passed = 0;
        for (int i = 0; i < probes.Count; i++)
            if (Passes(probes[i], plans[i]))
                passed++;
        return (double)passed / probes.Count;
    }

    private static bool ContainsGlob(IReadOnlyList<string>? globs, string expected)
    {
        if (globs is null)
            return false;
        foreach (var g in globs)
            if (string.Equals(g, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool ContainsGlobSubstring(IReadOnlyList<string>? globs, string needle)
    {
        if (globs is null)
            return false;
        foreach (var g in globs)
            if (g is not null && g.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static bool HasAnyDateFilter(ResolvedSearchPlan plan) =>
        plan.CreatedAfterDate is not null ||
        plan.CreatedBeforeDate is not null ||
        plan.ModifiedAfterDate is not null ||
        plan.ModifiedBeforeDate is not null;
}
