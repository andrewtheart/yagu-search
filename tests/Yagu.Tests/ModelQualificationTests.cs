using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>Unit tests for <see cref="ModelQualification"/> — the ladder driver that probes candidates
/// until one clears the qualification bar. Probing is stubbed with an in-memory runner so the pure
/// selection/aggregation logic is exercised without any Foundry/model dependency.</summary>
public sealed class ModelQualificationTests
{
    private static readonly SemanticProbe P1 = new()
    {
        Query = "png", Complexity = SemanticProbeComplexity.Simple, ExpectedIncludeGlob = "*.png",
    };
    private static readonly SemanticProbe P2 = new()
    {
        Query = "error", Complexity = SemanticProbeComplexity.Simple, ExpectedPatternContains = "error",
    };
    private static readonly SemanticProbe P3 = new()
    {
        Query = "recent", Complexity = SemanticProbeComplexity.Complex, ExpectedHasDateFilter = true,
    };

    private static readonly IReadOnlyList<SemanticProbe> Probes = [P1, P2, P3];

    // The sweep uses the user-chosen limits; tests use the defaults (10s simple / 15s complex).
    private static readonly ModelQualificationThresholds Thresholds = ModelQualificationThresholds.Default;

    private static ResolvedSearchPlan PassingPlan(SemanticProbe p) => new()
    {
        SearchMode = p.ExpectedSearchMode,
        IncludeGlobs = p.ExpectedIncludeGlob is null ? null : new[] { p.ExpectedIncludeGlob },
        Pattern = p.ExpectedPatternContains,
        ModifiedAfterDate = p.ExpectedHasDateFilter == true ? DateTimeOffset.Now : null,
    };

    /// <summary>Builds a runner from a per-(alias) function returning an outcome for each probe.</summary>
    private static Func<string, SemanticProbe, CancellationToken, Task<ProbeOutcome>> Runner(
        Func<string, SemanticProbe, ProbeOutcome> f) =>
        (alias, probe, _) => Task.FromResult(f(alias, probe));

    [Fact]
    public async Task QualifyAsync_FirstCandidatePasses_StopsEarly()
    {
        var runner = Runner((alias, probe) => ProbeOutcome.Answered(PassingPlan(probe), 1_000));

        var result = await ModelQualification.QualifyAsync(["phi-4-mini", "qwen2.5-1.5b"], Probes, Thresholds, runner);

        Assert.Equal("phi-4-mini", result.QualifiedModelAlias);
        Assert.True(result.AnyQualified);
        Assert.Single(result.Reports); // did not bother probing the second candidate
    }

    [Fact]
    public async Task QualifyAsync_FirstFailsAccuracy_SecondPasses()
    {
        var runner = Runner((alias, probe) =>
            alias == "weak"
                ? ProbeOutcome.Answered(new ResolvedSearchPlan(), 1_000)   // satisfies nothing → all miss
                : ProbeOutcome.Answered(PassingPlan(probe), 1_000));

        var result = await ModelQualification.QualifyAsync(["weak", "good"], Probes, Thresholds, runner);

        Assert.Equal("good", result.QualifiedModelAlias);
        Assert.Equal(2, result.Reports.Count);
        Assert.Equal(0.0, result.Reports[0].Accuracy);
        Assert.False(result.Reports[0].Verdict.Passed);
        Assert.True(result.Reports[1].Verdict.Passed);
    }

    [Fact]
    public async Task QualifyAsync_CrashAbandonsCandidateImmediately()
    {
        var runner = Runner((alias, probe) =>
        {
            if (alias == "crashy")
                return probe == P1 ? ProbeOutcome.Answered(PassingPlan(probe), 1_000) : ProbeOutcome.Crash();
            return ProbeOutcome.Answered(PassingPlan(probe), 1_000);
        });

        var result = await ModelQualification.QualifyAsync(["crashy", "stable"], Probes, Thresholds, runner);

        Assert.Equal("stable", result.QualifiedModelAlias);
        var crashyReport = result.Reports[0];
        Assert.True(crashyReport.Crashed);
        Assert.False(crashyReport.Verdict.Passed);
        // Probing stopped at the crash — only the first probe plus the crashing one were recorded.
        Assert.Equal(2, crashyReport.Probes.Count);
    }

    [Fact]
    public async Task QualifyAsync_SingleProbeOverLatencyCeiling_AbandonsCandidateImmediately()
    {
        int slowpokeProbeCalls = 0;
        var runner = Runner((alias, probe) =>
        {
            if (alias == "slowpoke")
            {
                slowpokeProbeCalls++;
                // The first probe blows the user's per-query limit; the rest would be fast + correct.
                int latency = probe == P1 ? Thresholds.SimpleQueryMaxMs + 1 : 1_000;
                return ProbeOutcome.Answered(PassingPlan(probe), latency);
            }
            return ProbeOutcome.Answered(PassingPlan(probe), 1_000);
        });

        var result = await ModelQualification.QualifyAsync(["slowpoke", "stable"], Probes, Thresholds, runner);

        Assert.Equal("stable", result.QualifiedModelAlias);
        var slow = result.Reports[0];
        Assert.False(slow.Verdict.Passed);
        // Abandoned after the first over-limit probe — the remaining two were never run.
        Assert.Equal(1, slowpokeProbeCalls);
        Assert.Single(slow.Probes);
        Assert.Contains("limit", slow.Verdict.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QualifyAsync_NoneQualify_ReportsBestEffortByAccuracy()
    {
        // "half" passes 2/3 (below the 75% bar); "quarter" passes 1/3.
        var runner = Runner((alias, probe) =>
        {
            bool pass = alias switch
            {
                "half" => probe != P3,   // P1,P2 pass; P3 miss  → 2/3 ≈ 67%
                _ => probe == P1,          // only P1 passes       → 1/3 ≈ 33%
            };
            return ProbeOutcome.Answered(pass ? PassingPlan(probe) : new ResolvedSearchPlan(), 1_000);
        });

        var result = await ModelQualification.QualifyAsync(["quarter", "half"], Probes, Thresholds, runner);

        Assert.Null(result.QualifiedModelAlias);
        Assert.False(result.AnyQualified);
        Assert.Equal("half", result.BestEffortModelAlias);
    }

    [Fact]
    public async Task QualifyAsync_BestEffortTieBreaksByMedianLatency()
    {
        // Both candidates pass exactly 1/3 (same accuracy); "fast" has lower latency.
        var runner = Runner((alias, probe) =>
        {
            bool pass = probe == P1;
            int latency = alias == "fast" ? 2_000 : 8_000;
            return ProbeOutcome.Answered(pass ? PassingPlan(probe) : new ResolvedSearchPlan(), latency);
        });

        var result = await ModelQualification.QualifyAsync(["slow", "fast"], Probes, Thresholds, runner);

        Assert.Null(result.QualifiedModelAlias);
        Assert.Equal("fast", result.BestEffortModelAlias);
    }

    [Fact]
    public async Task QualifyAsync_BestEffort_RanksOnRunProbeAccuracy_NotDilutedByUnrunProbes()
    {
        // Six simple probes, each satisfied only by its own glob.
        var probes = Enumerable.Range(0, 6)
            .Select(i => new SemanticProbe
            {
                Query = $"q{i}", Complexity = SemanticProbeComplexity.Simple, ExpectedIncludeGlob = $"*.x{i}",
            })
            .ToList();

        int overLimit = Thresholds.SimpleQueryMaxMs + 1;

        var runner = Runner((alias, probe) =>
        {
            int idx = probes.IndexOf(probe);
            if (alias == "fast-capped")
            {
                // Answers its first three probes correctly; the third is over the per-query limit, so the
                // candidate is abandoned there. It runs 3 probes and passes all 3 (effective accuracy 1.0),
                // but only 3 of 6 in absolute terms (full accuracy 0.5).
                return ProbeOutcome.Answered(PassingPlan(probe), idx == 2 ? overLimit : 1_000);
            }

            // "complete" runs all six, passing four (full & effective accuracy ≈ 0.67) — below the 75% bar.
            bool pass = idx < 4;
            return ProbeOutcome.Answered(pass ? PassingPlan(probe) : new ResolvedSearchPlan(), 1_000);
        });

        var result = await ModelQualification.QualifyAsync(["fast-capped", "complete"], probes, Thresholds, runner);

        Assert.Null(result.QualifiedModelAlias); // neither clears 75%
        // The old logic ranked best-effort on FULL accuracy (0.5 vs 0.67) and would pick "complete"; ranking
        // on effective (run-probe) accuracy correctly prefers the fast candidate that got everything it ran
        // right instead of the slower one that merely completed more diluted probes.
        Assert.Equal("fast-capped", result.BestEffortModelAlias);

        var capped = result.Reports.Single(r => r.ModelAlias == "fast-capped");
        Assert.Equal(0.5, capped.Accuracy, 3);
        Assert.Equal(1.0, capped.EffectiveAccuracy, 3);
    }

    [Fact]
    public async Task QualifyAsync_AllCandidatesCrash_BestEffortIsNull()
    {
        var runner = Runner((alias, probe) => ProbeOutcome.Crash());

        var result = await ModelQualification.QualifyAsync(["a", "b"], Probes, Thresholds, runner);

        Assert.Null(result.QualifiedModelAlias);
        Assert.Null(result.BestEffortModelAlias);
        Assert.All(result.Reports, r => Assert.True(r.Crashed));
    }

    [Fact]
    public async Task QualifyAsync_TooSlow_FailsEvenWhenAccurate()
    {
        int slow = Thresholds.SimpleQueryMaxMs + 5_000;
        var runner = Runner((alias, probe) => ProbeOutcome.Answered(PassingPlan(probe), slow));

        var result = await ModelQualification.QualifyAsync(["accurate-but-slow"], Probes, Thresholds, runner);

        Assert.Null(result.QualifiedModelAlias);
        Assert.False(result.Reports[0].Verdict.Passed);
        Assert.Contains("limit", result.Reports[0].Verdict.Reason, System.StringComparison.OrdinalIgnoreCase);
        // Abandoned after the first over-ceiling probe, so only that probe was recorded.
        Assert.Single(result.Reports[0].Probes);
        // Still the best effort since the one probe it ran produced correct output.
        Assert.Equal("accurate-but-slow", result.BestEffortModelAlias);
    }

    [Fact]
    public async Task QualifyAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var runner = Runner((alias, probe) =>
        {
            cts.Cancel();
            return ProbeOutcome.Answered(PassingPlan(probe), 1_000);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelQualification.QualifyAsync(["a", "b"], Probes, Thresholds, runner, cts.Token));
    }
}
