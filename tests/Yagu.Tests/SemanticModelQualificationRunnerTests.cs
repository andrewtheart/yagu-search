using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Orchestration tests for <see cref="SemanticModelQualificationRunner"/> driven by an in-memory fake
/// <see cref="ISemanticQueryTranslator"/>. These verify candidate ordering (incl. smaller-family-variant
/// first), probing every candidate for the comparison list, prepare-once-per-candidate, crash
/// abandonment, best-effort selection, the candidate cap and the empty-catalog path — independent of any
/// real model.
/// </summary>
public sealed class SemanticModelQualificationRunnerTests
{
    // A probe with no expectations passes whenever the translator returns any parseable plan.
    private static SemanticProbe PassAnything(string query = "find things") =>
        new() { Query = query, Complexity = SemanticProbeComplexity.Simple };

    // The sweep uses the user-chosen limits; tests use the defaults (60s load / 10s simple / 15s complex).
    private static readonly ModelQualificationThresholds Thresholds = ModelQualificationThresholds.Default;

    // A probe that only passes when the resolved plan includes the "*.png" glob.
    private static SemanticProbe RequiresPngGlob(string query = "find things") =>
        new() { Query = query, Complexity = SemanticProbeComplexity.Simple, ExpectedIncludeGlob = "*.png" };

    private static SemanticModelOption Option(string alias, bool recommended = false, bool preferred = false, long sizeBytes = 0) =>
        new() { Alias = alias, DisplayName = alias, IsRecommended = recommended, IsPreferredFamily = preferred, SizeBytes = sizeBytes == 0 ? null : sizeBytes };

    private static SemanticTranslationResult PngPlan() =>
        SemanticTranslationResult.Ok(new SemanticSearchPlan { IncludeGlobs = new List<string> { "*.png" } });

    private static SemanticTranslationResult EmptyPlan() =>
        SemanticTranslationResult.Ok(new SemanticSearchPlan());

    [Fact]
    public async Task RunAsync_ProbesAllCandidatesForTheComparisonList_EvenAfterOnePasses()
    {
        // All candidates pass. The sweep no longer stops at the first pass — it probes every candidate so
        // the result presents a full comparison list; the earliest (fastest on a latency tie) is recommended.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B"), Option("C") },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { PassAnything(), PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.True(result.AnyQualified);
        Assert.Equal("A", result.QualifiedModelAlias);
        Assert.Equal(new[] { "A", "B", "C" }, result.Reports.Select(r => r.ModelAlias).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, translator.Prepared);
    }

    [Fact]
    public async Task RunAsync_OrdersRecommendedFirst_ThenCatalogOrder()
    {
        // All candidates fail so every one is probed and the ladder order is observable.
        var translator = new FakeTranslator
        {
            Options = { Option("X"), Option("Y", recommended: true), Option("Z") },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.False(result.AnyQualified);
        Assert.Equal(new[] { "Y", "X", "Z" }, result.Reports.Select(r => r.ModelAlias).ToArray());
    }

    [Fact]
    public async Task RunAsync_ExcludesReasoningModelsFromTheCandidateLadder()
    {
        // Reasoning / chain-of-thought models (alias contains "reasoning") can never be auto-selected, and
        // probing them just drags the sweep out for minutes (every probe times out then wedges). They must
        // be kept off the ladder entirely — even when one is flagged "recommended" by the catalog.
        var translator = new FakeTranslator
        {
            Options =
            {
                Option("phi-4-mini-reasoning", recommended: true),
                Option("phi-4-reasoning"),
                Option("phi-3.5-mini"),
            },
            TranslateBehavior = (_, _) => EmptyPlan(), // all fail, so every ELIGIBLE candidate is probed
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        // Only the non-reasoning candidate was probed; the two reasoning models were never on the ladder.
        Assert.Equal(new[] { "phi-3.5-mini" }, result.Reports.Select(r => r.ModelAlias).ToArray());
        Assert.DoesNotContain(result.Reports, r => SemanticModelQualificationRunner.IsReasoningAlias(r.ModelAlias));
    }

    [Fact]
    public void IsReasoningAlias_MatchesReasoningModelsCaseInsensitively()
    {
        Assert.True(SemanticModelQualificationRunner.IsReasoningAlias("phi-4-mini-reasoning"));
        Assert.True(SemanticModelQualificationRunner.IsReasoningAlias("Phi-4-Reasoning-cuda-gpu:3"));
        Assert.False(SemanticModelQualificationRunner.IsReasoningAlias("phi-4-mini"));
        Assert.False(SemanticModelQualificationRunner.IsReasoningAlias("phi-3.5-mini"));
        Assert.False(SemanticModelQualificationRunner.IsReasoningAlias(null));
    }

    [Fact]
    public async Task RunAsync_TriesSmallerSameFamilyVariantFirst_AndPrefersIt()
    {
        // phi-4 (recommended, large) and phi-4-mini (smaller) both pass. The smaller sibling is probed
        // FIRST (family reorder); the larger sibling is ALSO probed so it stays in the comparison list. On
        // the latency tie the earlier one — the faster mini — is the recommendation.
        var translator = new FakeTranslator
        {
            Options =
            {
                Option("phi-4", recommended: true, sizeBytes: 9_000_000_000),
                Option("phi-4-mini", sizeBytes: 4_800_000_000),
            },
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("phi-4-mini", result.QualifiedModelAlias);
        Assert.Equal(new[] { "phi-4-mini", "phi-4" }, translator.Prepared); // mini first, then the full version
        Assert.Equal(new[] { "phi-4-mini", "phi-4" }, result.Reports.Select(r => r.ModelAlias).ToArray());
    }

    [Fact]
    public async Task RunAsync_SmallerSiblingFailsAccuracy_FallsBackToLargerRecommended()
    {
        // The smaller sibling is tried first but does not clear the bar; the larger recommended model is
        // then probed and qualifies.
        var translator = new FakeTranslator
        {
            Options =
            {
                Option("phi-4", recommended: true, sizeBytes: 9_000_000_000),
                Option("phi-4-mini", sizeBytes: 4_800_000_000),
            },
            TranslateBehavior = (alias, _) => alias == "phi-4" ? PngPlan() : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("phi-4", result.QualifiedModelAlias);
        Assert.Equal(new[] { "phi-4-mini", "phi-4" }, result.Reports.Select(r => r.ModelAlias).ToArray());
    }

    [Theory]
    [InlineData("phi-4", "phi-4-mini", true)]
    [InlineData("phi-4-mini", "phi-4", true)]
    [InlineData("phi-3.5", "phi-3.5-mini", true)]
    [InlineData("phi-4", "phi-4", true)]
    [InlineData("phi-4", "phi-3.5-mini", false)]
    [InlineData("phi-4-mini", "phi-3.5-mini", false)]
    [InlineData("qwen2.5-0.5b", "qwen2.5-1.5b", false)] // same length, neither is a prefix of the other
    public void AreSameFamily_DetectsHyphenDelimitedPrefixVariants(string a, string b, bool expected)
    {
        Assert.Equal(expected, SemanticModelQualificationRunner.AreSameFamily(a, b));
    }

    [Fact]
    public void ReorderSmallerFamilyVariantsFirst_PutsSmallerVariantBeforeLarger_KeepingCrossFamilyOrder()
    {
        var ladder = new List<string> { "phi-4", "phi-4-mini", "phi-3.5-mini", "qwen2.5-1.5b" };
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["phi-4"] = 9_000_000_000,
            ["phi-4-mini"] = 4_800_000_000,
            ["phi-3.5-mini"] = 2_530_000_000,
            ["qwen2.5-1.5b"] = 1_780_000_000,
        };

        SemanticModelQualificationRunner.ReorderSmallerFamilyVariantsFirst(ladder, sizes);

        // The phi-4 family is emitted at its first slot, smaller-first; the single-member families keep
        // their relative order.
        Assert.Equal(new[] { "phi-4-mini", "phi-4", "phi-3.5-mini", "qwen2.5-1.5b" }, ladder);
    }

    [Fact]
    public void ReorderSmallerFamilyVariantsFirst_NoFamilies_LeavesOrderUnchanged()
    {
        var ladder = new List<string> { "A", "B", "C" };
        SemanticModelQualificationRunner.ReorderSmallerFamilyVariantsFirst(
            ladder, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "A", "B", "C" }, ladder);
    }

    [Fact]
    public async Task RunAsync_FirstCandidateFailsAccuracy_SecondQualifies()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (alias, _) => alias == "B" ? PngPlan() : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("B", result.QualifiedModelAlias);
        Assert.Equal(2, result.Reports.Count);
        Assert.Equal("B", result.BestEffortModelAlias);
        Assert.Equal(0.0, result.Reports[0].Accuracy);
        Assert.Equal(1.0, result.Reports[1].Accuracy);
    }

    [Fact]
    public async Task RunAsync_UnloadsEachCandidateBeforePreparingTheNext()
    {
        // A fails accuracy so the sweep advances to B. Only one model may be resident at a time, so the
        // runner must evict A from memory BEFORE preparing B (Foundry Local does not evict on load, so
        // otherwise probing a ladder of models would accumulate them and OOM with "bad allocation").
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (alias, _) => alias == "B" ? PngPlan() : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("B", result.QualifiedModelAlias);
        // A (the intermediate candidate) is evicted exactly once, and that eviction happens while A is
        // still the resident model (before B is prepared). The qualified final model B is left resident
        // for immediate use, so the sweep never unloads it.
        Assert.Equal(new[] { "A" }, translator.Unloaded);
        Assert.Equal(new[] { "A", "B" }, translator.Prepared);
    }

    [Fact]
    public async Task RunAsync_ProbesBothQualifiers_EvictsTheEarlierAndLeavesTheLastResident()
    {
        // Both candidates pass. The sweep probes both for the comparison list; only one model may be
        // resident at a time, so the earlier one (A) is evicted before the next (B) is prepared, and the
        // last-probed (B) is left resident. A still wins the recommendation on the latency tie.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("A", result.QualifiedModelAlias); // tie on latency → earlier (higher-ranked)
        Assert.Equal(new[] { "A", "B" }, translator.Prepared);
        Assert.Equal(new[] { "A" }, translator.Unloaded);
    }

    [Fact]
    public async Task RunAsync_TranslateThrows_AbandonsCandidateAsCrash_AndTriesNext()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (alias, _) =>
                alias == "A" ? throw new InvalidOperationException("boom") : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { PassAnything(), PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("B", result.QualifiedModelAlias);
        var crashed = result.Reports.Single(r => r.ModelAlias == "A");
        Assert.True(crashed.Crashed);
        Assert.Single(crashed.Probes); // abandoned after the first probe crashed
        Assert.Equal("B", result.BestEffortModelAlias);
    }

    [Fact]
    public async Task RunAsync_AllCandidatesCrash_NoQualifiedNoBestEffort()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => throw new InvalidOperationException("boom"),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.False(result.AnyQualified);
        Assert.Null(result.QualifiedModelAlias);
        Assert.Null(result.BestEffortModelAlias);
    }

    [Fact]
    public async Task RunAsync_PrepareThrows_TreatedAsCrash_AndTriesNext()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            PrepareBehavior = alias => alias == "A"
                ? Task.FromException(new InvalidOperationException("cannot load"))
                : Task.CompletedTask,
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("B", result.QualifiedModelAlias);
        var crashed = result.Reports.Single(r => r.ModelAlias == "A");
        Assert.True(crashed.Crashed);
    }

    [Fact]
    public async Task RunAsync_ModelLoadExceedsLimit_AbandonsCandidateAsCrash_AndTriesNext()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("slow-loader", recommended: true), Option("B") },
            // "slow-loader" takes far longer to load than the tiny load limit below; "B" loads instantly.
            PrepareDelayProvider = alias => alias == "slow-loader" ? TimeSpan.FromSeconds(30) : TimeSpan.Zero,
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        var thresholds = ModelQualificationThresholds.Default with { ModelLoadMaxMs = 30 };

        var result = await runner.RunAsync(new[] { PassAnything() }, thresholds, progress: null, CancellationToken.None);

        // The slow-loading candidate is abandoned as if it crashed; the next candidate qualifies.
        Assert.Equal("B", result.QualifiedModelAlias);
        var slow = result.Reports.Single(r => r.ModelAlias == "slow-loader");
        Assert.True(slow.Crashed);
    }

    [Fact]
    public async Task RunAsync_ModelLoadWedgesIgnoringCancellation_AbandonsCandidateAsCrash_AndTriesNext()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("wedged-loader", recommended: true), Option("B") },
            // "wedged-loader" hangs on LOAD far past the tiny load limit AND ignores the cancellation token
            // (like a real wedged native load_model — observed intermittently when switching off a large
            // resident model). The CancelAfter alone can't cut it off; the runner's LOAD backstop must, so
            // the sweep advances instead of hanging forever on the first probe. "B" loads instantly.
            PrepareDelayProvider = alias => alias == "wedged-loader" ? TimeSpan.FromSeconds(2) : TimeSpan.Zero,
            PrepareHonorsCancellation = false,
            TranslateBehavior = (alias, _) => alias == "B" ? PngPlan() : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        // Tiny load limit so the wedged load trips the backstop quickly.
        var thresholds = ModelQualificationThresholds.Default with { ModelLoadMaxMs = 50 };

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, thresholds, progress: null, CancellationToken.None);

        // The wedged-loading candidate is abandoned as if it crashed; the next candidate qualifies.
        Assert.Equal("B", result.QualifiedModelAlias);
        var wedged = result.Reports.Single(r => r.ModelAlias == "wedged-loader");
        Assert.True(wedged.Crashed);
    }

    [Fact]
    public async Task RunAsync_QueryWedgesIgnoringCancellation_AbandonsAsTooSlow_AndTriesNext()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("wedged", recommended: true), Option("B") },
            // "wedged" hangs on inference far past the tiny per-query limit and ignores the cancellation
            // token (like a real wedged native inference); the runner's hard backstop must cut it off so
            // the sweep advances instead of stalling on this one query. "B" answers instantly.
            TranslateDelayProvider = alias => alias == "wedged" ? TimeSpan.FromSeconds(2) : TimeSpan.Zero,
            TranslateHonorsCancellation = false,
            TranslateBehavior = (alias, _) => alias == "B" ? PngPlan() : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        // Tiny per-query limits so the wedged candidate trips the backstop quickly.
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50, LatencyToleranceMs = 0 };

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, thresholds, progress: null, CancellationToken.None);

        // The wedged candidate is abandoned as too slow (a latency violation, not a crash) and the next
        // candidate qualifies — the sweep never stalls on the hung query.
        Assert.Equal("B", result.QualifiedModelAlias);
        var wedged = result.Reports.Single(r => r.ModelAlias == "wedged");
        Assert.False(wedged.Crashed);
        Assert.False(wedged.Verdict.Passed);
        // A wedge gets ONE bounded retry before abandonment (the retry also wedged here), so the one probe
        // ran twice — never an infinite loop.
        Assert.Equal(2, translator.TranslateCallsByAlias["wedged"]);
    }

    [Fact]
    public async Task RunAsync_ProbeWedgesOnceThenAnswers_RetriesAndQualifies()
    {
        // A wedge is INTERMITTENT: the first attempt at a probe stalls past the deadline while ignoring
        // cancellation, but the SAME query answers correctly on the very next attempt (as observed with
        // phi-4, which answers every probe in ~5s yet occasionally wedges one to 30-48s). The runner must
        // drain the stuck op and RETRY the probe rather than disqualify an otherwise-correct model.
        int calls = 0;
        var translator = new FakeTranslator
        {
            Options = { Option("flaky", recommended: true) },
            // The first timed inference wedges (2s, ignores cancellation → backstopped); the retry is fast.
            TranslateDelayProvider = _ => calls++ == 0 ? TimeSpan.FromSeconds(2) : TimeSpan.Zero,
            TranslateHonorsCancellation = false,
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        // Tiny per-query limit so the 2s first attempt trips the backstop and is treated as wedged.
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50, LatencyToleranceMs = 0 };

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, thresholds, progress: null, CancellationToken.None);

        // The transient wedge did NOT disqualify the model — the retry answered correctly and it qualified.
        Assert.Equal("flaky", result.QualifiedModelAlias);
        Assert.True(result.Reports.Single().Verdict.Passed);
        // Exactly two timed inferences for the one probe: the wedge + the successful retry.
        Assert.Equal(2, translator.TranslateCallsByAlias["flaky"]);
    }

    [Fact]
    public async Task RunAsync_QueryExceedsLimitButHonorsCancellation_AbandonsAsTooSlow_AndTriesNext()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("slow", recommended: true), Option("B") },
            // "slow" takes far longer than the per-query limit but DOES honor cancellation, so the
            // runner's limit-bound token stops it promptly (no backstop leak needed).
            TranslateDelayProvider = alias => alias == "slow" ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
            TranslateHonorsCancellation = true,
            TranslateBehavior = (alias, _) => alias == "B" ? PngPlan() : EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50, LatencyToleranceMs = 0 };

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, thresholds, progress: null, CancellationToken.None);

        Assert.Equal("B", result.QualifiedModelAlias);
        var slow = result.Reports.Single(r => r.ModelAlias == "slow");
        Assert.False(slow.Crashed);
        Assert.False(slow.Verdict.Passed);
    }

    [Fact]
    public async Task RunAsync_PreparesEachCandidateOncePerSweep()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(
            new[] { PassAnything(), PassAnything(), PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("A", result.QualifiedModelAlias);
        Assert.Equal(new[] { "A" }, translator.Prepared); // prepared once, not per probe
        Assert.Equal(new[] { "A" }, translator.Warmed);   // warmed once, right after prepare
        Assert.Equal(3, translator.TranslateCallsByAlias["A"]); // one translate per probe (warmup excluded)
    }

    [Fact]
    public async Task RunAsync_MaxCandidates_CapsTheLadder()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B"), Option("C"), Option("D") },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator, maxCandidates: 2);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.False(result.AnyQualified);
        Assert.Equal(2, result.Reports.Count);
        Assert.Equal(new[] { "A", "B" }, result.Reports.Select(r => r.ModelAlias).ToArray());
    }

    [Fact]
    public void DefaultMaxCandidates_IsASmallPositiveCap()
    {
        // The first-run sweep passes this cap so it probes only the best-first top candidates instead of
        // the machine's entire Foundry catalog (dozens of models). It must be a small positive number,
        // never the "no cap" sentinel (0).
        Assert.InRange(SemanticModelQualificationRunner.DefaultMaxCandidates, 1, 10);
    }

    [Fact]
    public async Task RunAsync_DefaultMaxCandidates_StopsBeforeExhaustingLargeCatalog()
    {
        // A large catalog (mimicking the observed 28-model machine) must not be probed end-to-end when
        // nothing qualifies: the sweep stops at DefaultMaxCandidates.
        var translator = new FakeTranslator { TranslateBehavior = (_, _) => EmptyPlan() };
        translator.Options.Add(Option("A", recommended: true));
        for (int i = 1; i < 28; i++)
            translator.Options.Add(Option($"model-{i:D2}"));

        var runner = new SemanticModelQualificationRunner(
            translator, maxCandidates: SemanticModelQualificationRunner.DefaultMaxCandidates);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.False(result.AnyQualified);
        Assert.Equal(SemanticModelQualificationRunner.DefaultMaxCandidates, result.Reports.Count);
        Assert.Equal("A", result.Reports[0].ModelAlias);
    }

    [Fact]
    public async Task RunAsync_WarmsCandidateOnceBeforeProbing()
    {
        // Both candidates fail so each is prepared, warmed once, then probed — in ladder order.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal(new[] { "A", "B" }, translator.Warmed);
    }

    [Fact]
    public async Task RunAsync_WarmupFailure_IsBestEffort_CandidateStillProbedAndQualifies()
    {
        // The warmup inference faults, but that must NOT abandon the candidate: probing proceeds and the
        // model qualifies on its timed probes exactly as if warmup had merely been skipped.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            WarmupThrows = true,
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("A", result.QualifiedModelAlias);
        Assert.Equal(new[] { "A" }, translator.Warmed); // warmup was attempted
    }

    [Fact]
    public async Task RunAsync_ReservesLastSlotForNovelModel_WhenCapIsAllPreferredFamilies()
    {
        // The cap is filled by known preferred families with a novel family just below the cut. The sweep
        // must swap the weakest kept preferred candidate for the novel one so a newly released model is
        // never permanently starved by the static ranking. All candidates fail so the ladder is observable.
        var translator = new FakeTranslator
        {
            Options =
            {
                Option("phi-a", recommended: true, preferred: true),
                Option("phi-b", preferred: true),
                Option("phi-c", preferred: true),
                Option("novel-x"), // novel: IsPreferredFamily == false
            },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator, maxCandidates: 3);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.False(result.AnyQualified);
        // phi-c (the weakest kept preferred family) is displaced by the novel model.
        Assert.Equal(new[] { "phi-a", "phi-b", "novel-x" }, result.Reports.Select(r => r.ModelAlias).ToArray());
    }

    [Fact]
    public async Task RunAsync_DoesNotReserveExtraSlot_WhenNovelModelAlreadyWithinCap()
    {
        // A novel family already sits within the cap, so no reservation/displacement happens — the top
        // three by order are probed as-is (the second novel model below the cap is NOT force-included).
        var translator = new FakeTranslator
        {
            Options =
            {
                Option("phi-a", recommended: true, preferred: true),
                Option("novel-y"),                // novel, within the cap
                Option("phi-b", preferred: true),
                Option("novel-z"),                // novel, below the cap
            },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator, maxCandidates: 3);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal(new[] { "phi-a", "novel-y", "phi-b" }, result.Reports.Select(r => r.ModelAlias).ToArray());
    }

    [Fact]
    public async Task RunAsync_NoModels_ReturnsEmptyResult()
    {
        var translator = new FakeTranslator();
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.False(result.AnyQualified);
        Assert.Empty(result.Reports);
        Assert.Null(result.BestEffortModelAlias);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressPhases()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        var stages = new List<SemanticQualificationStage>();
        var progress = new Progress<SemanticQualificationProgress>(p => stages.Add(p.Stage));

        await runner.RunAsync(new[] { PassAnything() }, Thresholds, progress, CancellationToken.None);

        // Progress is dispatched via the synchronization context; give posted callbacks a chance to run.
        await Task.Yield();
        Assert.Contains(SemanticQualificationStage.Done, stages);
    }

    [Fact]
    public async Task RunAsync_UsesReliableNonStreamingTranslationForProbes()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        await runner.RunAsync(new[] { PassAnything(), PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        // The prober scores through the RELIABLE non-streaming path (TranslateAsync). The SDK's token-
        // streaming API intermittently STALLS (a call can hang without yielding a token until the deadline,
        // falsely disqualifying a fast model), so it is deliberately NOT used for the scored inference.
        Assert.Equal(2, translator.TranslateCallsByAlias["A"]);
        Assert.False(translator.StreamingCallsByAlias.ContainsKey("A"));
    }

    [Fact]
    public async Task RunAsync_ReportsProbeQueryModelResponseAndVerdict()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => SemanticTranslationResult.Ok(new SemanticSearchPlan(), "MODEL-RAW-OUTPUT"),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        var sink = new SyncProgress<SemanticQualificationProgress>();

        await runner.RunAsync(new[] { PassAnything("find png files") }, Thresholds, sink, CancellationToken.None);

        var probing = sink.Items.Single(p => p.Stage == SemanticQualificationStage.Probing);
        Assert.Equal("find png files", probing.ProbeQuery);

        // The completed model answer is surfaced as a ProbeToken so the transcript can reveal it.
        Assert.Contains(sink.Items, p =>
            p.Stage == SemanticQualificationStage.ProbeToken && p.TokenDelta == "MODEL-RAW-OUTPUT");

        var completed = sink.Items.Single(p => p.Stage == SemanticQualificationStage.ProbeCompleted);
        Assert.True(completed.ProbePassed);
        Assert.Equal(SemanticProbeFailureReason.None, completed.FailureReason);
        Assert.Equal("find png files", completed.ProbeQuery);
    }

    [Fact]
    public async Task RunAsync_ProbeCompleted_ClassifiesInaccurateAnswer()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => EmptyPlan(), // parses, but has no *.png glob
        };
        var runner = new SemanticModelQualificationRunner(translator);
        var sink = new SyncProgress<SemanticQualificationProgress>();

        await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, sink, CancellationToken.None);

        var completed = sink.Items.Single(p => p.Stage == SemanticQualificationStage.ProbeCompleted);
        Assert.False(completed.ProbePassed);
        Assert.Equal(SemanticProbeFailureReason.Inaccurate, completed.FailureReason);
    }

    [Fact]
    public async Task RunAsync_ProbeCompleted_ClassifiesCrashedAndTooSlow()
    {
        // Crash: the inference throws.
        var crasher = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (alias, _) => alias == "A" ? throw new InvalidOperationException("boom") : PngPlan(),
        };
        var crashSink = new SyncProgress<SemanticQualificationProgress>();
        await new SemanticModelQualificationRunner(crasher)
            .RunAsync(new[] { RequiresPngGlob() }, Thresholds, crashSink, CancellationToken.None);
        Assert.Contains(crashSink.Items, p =>
            p.Stage == SemanticQualificationStage.ProbeCompleted
            && p.CandidateAlias == "A" && p.FailureReason == SemanticProbeFailureReason.Crashed);

        // Too slow: the inference honors cancellation but blows the tiny per-query limit.
        var slow = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateDelayProvider = alias => alias == "A" ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
            TranslateHonorsCancellation = true,
            TranslateBehavior = (alias, _) => alias == "B" ? PngPlan() : EmptyPlan(),
        };
        var slowSink = new SyncProgress<SemanticQualificationProgress>();
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50, LatencyToleranceMs = 0 };
        await new SemanticModelQualificationRunner(slow)
            .RunAsync(new[] { RequiresPngGlob() }, thresholds, slowSink, CancellationToken.None);
        Assert.Contains(slowSink.Items, p =>
            p.Stage == SemanticQualificationStage.ProbeCompleted
            && p.CandidateAlias == "A" && p.FailureReason == SemanticProbeFailureReason.TooSlow);
    }

    [Fact]
    public async Task RunAsync_FailedProbe_HoldsVisibleBeforeAdvancing()
    {
        // A single candidate that answers but fails accuracy. With a positive failed-probe hold, the sweep
        // must pause on that failure so a UI can show it — the whole run takes at least the hold.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator, failedProbeHoldMs: 250);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 200, $"expected the failed-probe hold to delay the sweep, elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RunAsync_PassingProbes_DoNotHold()
    {
        // A passing probe must NOT incur the failed-probe hold, even when the hold is large.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator, failedProbeHoldMs: 5000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"a passing sweep must not pause; elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RunAsync_SlowButAccurateProbe_PassesWithWarning()
    {
        // The model answers correctly but a little over the HARD limit (yet within the tolerance band): it
        // must PASS — flagged as a slow warning — rather than fail the candidate.
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateDelayProvider = _ => TimeSpan.FromMilliseconds(120),
            TranslateHonorsCancellation = true,
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        // Hard limit 50 ms (probe takes ~120 ms → over the limit) but a 5 s tolerance keeps it a pass.
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50, LatencyToleranceMs = 5_000 };
        var sink = new SyncProgress<SemanticQualificationProgress>();

        await runner.RunAsync(new[] { RequiresPngGlob() }, thresholds, sink, CancellationToken.None);

        var completed = sink.Items.Single(p => p.Stage == SemanticQualificationStage.ProbeCompleted);
        Assert.True(completed.ProbePassed);
        Assert.True(completed.ProbeSlowWarning);
        Assert.Equal(SemanticProbeFailureReason.None, completed.FailureReason);
    }

    [Fact]
    public async Task RunAsync_AlreadyCanceled_Throws()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true) },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(new[] { PassAnything() }, Thresholds, progress: null, cts.Token));
    }

    /// <summary>Captures progress reports synchronously as the runner emits them. The runner calls
    /// <see cref="IProgress{T}.Report"/> inline (unlike a <see cref="Progress{T}"/> sink, which marshals
    /// asynchronously), so every report is present by the time the sweep completes — no dispatcher drain.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly object _gate = new();
        public List<T> Items { get; } = new();
        public void Report(T value)
        {
            lock (_gate)
                Items.Add(value);
        }
    }

    private sealed class FakeTranslator : ISemanticQueryTranslator
    {
        public List<SemanticModelOption> Options { get; } = new();
        public List<string> Prepared { get; } = new();

        /// <summary>Aliases explicitly evicted between candidates via <see cref="UnloadCurrentModelAsync"/>,
        /// in order — the previous candidate each time the sweep advances to the next.</summary>
        public List<string> Unloaded { get; } = new();

        /// <summary>Aliases warmed via the runner's untimed warmup inference, in order. One entry per
        /// prepared candidate.</summary>
        public List<string> Warmed { get; } = new();
        public Dictionary<string, int> TranslateCallsByAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-alias count of STREAMING translate calls — the runner drives timed probes through
        /// <see cref="TranslateStreamingAsync"/>, so this should mirror the timed-probe count.</summary>
        public Dictionary<string, int> StreamingCallsByAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The text emitted to the streaming token sink, one entry per streamed probe.</summary>
        public List<string> StreamedTokens { get; } = new();

        public Func<string, string, SemanticTranslationResult>? TranslateBehavior { get; init; }
        public Func<string, Task>? PrepareBehavior { get; init; }

        /// <summary>When true, the untimed warmup inference faults — exercising the runner's best-effort
        /// handling (a warmup failure must not abandon the candidate).</summary>
        public bool WarmupThrows { get; init; }

        /// <summary>Optional per-alias artificial load delay that observes the cancellation token, so a
        /// small model-load limit trips the runner's load-timeout path for a specific candidate.</summary>
        public Func<string, TimeSpan>? PrepareDelayProvider { get; init; }

        /// <summary>Optional per-alias artificial inference delay, so a small per-query limit trips the
        /// runner's per-query timeout for a specific candidate.</summary>
        public Func<string, TimeSpan>? TranslateDelayProvider { get; init; }

        /// <summary>When false (the default for the delay path), the inference delay ignores the
        /// cancellation token — modelling a wedged native inference that never returns and must be cut
        /// off by the runner's hard backstop rather than by cancellation.</summary>
        public bool TranslateHonorsCancellation { get; init; }

        /// <summary>When false, the prepare/load delay ignores the cancellation token — modelling a wedged
        /// native model load (Foundry Local <c>load_model</c>) that never returns and must be cut off by
        /// the runner's LOAD backstop rather than by cancellation. Defaults true so the existing
        /// load-timeout test (which relies on cancellation) is unaffected.</summary>
        public bool PrepareHonorsCancellation { get; init; } = true;

        private string? _currentOverride;

        public bool IsAvailable => true;
        public string? CurrentModelKey => _currentOverride;

        public Task<IReadOnlyList<SemanticModelOption>> ListModelOptionsAsync(
            IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SemanticModelOption>>(Options);

        public async Task PrepareModelAsync(
            string? modelAlias, IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Prepared.Add(modelAlias ?? "");
            TimeSpan delay = PrepareDelayProvider?.Invoke(modelAlias ?? "") ?? TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, PrepareHonorsCancellation ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            if (PrepareBehavior is not null)
                await PrepareBehavior(modelAlias ?? "").ConfigureAwait(false);
        }

        public async Task<SemanticTranslationResult> TranslateAsync(
            string naturalLanguageQuery, SemanticTranslationContext context,
            IProgress<SemanticTranslationProgress>? progress, CancellationToken cancellationToken)
        {
            string alias = _currentOverride ?? "";

            // The runner warms each freshly-loaded model once (untimed) with a fixed sentinel query before
            // the timed probes. Record it separately and keep it an instant no-op so it neither inflates the
            // per-probe translate counts nor incurs the artificial per-alias delay (which models slow
            // INFERENCE, not warmup). WarmupThrows forces the warmup to fault for best-effort coverage.
            if (naturalLanguageQuery == SemanticModelQualificationRunner.WarmupQuery)
            {
                Warmed.Add(alias);
                if (WarmupThrows)
                    throw new InvalidOperationException("warmup boom");
                return EmptyPlanResult();
            }

            TranslateCallsByAlias[alias] = TranslateCallsByAlias.GetValueOrDefault(alias) + 1;
            TimeSpan delay = TranslateDelayProvider?.Invoke(alias) ?? TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
            {
                // A wedged inference ignores the cancellation token entirely; a well-behaved one stops
                // when the runner's per-query watchdog cancels it.
                await Task.Delay(delay, TranslateHonorsCancellation ? cancellationToken : CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Like the real async translator, a throwing behavior faults the returned task (it never
            // throws synchronously), so the runner observes it on await.
            return TranslateBehavior?.Invoke(alias, naturalLanguageQuery) ?? EmptyPlanResult();
        }

        public async Task<SemanticTranslationResult> TranslateStreamingAsync(
            string naturalLanguageQuery, SemanticTranslationContext context,
            Action<string>? onToken, CancellationToken cancellationToken)
        {
            string alias = _currentOverride ?? "";

            // The runner warms via TranslateAsync (not this streaming path); mirror the warmup no-op
            // defensively so a stray warmup query can't inflate the timed-probe counts.
            if (naturalLanguageQuery == SemanticModelQualificationRunner.WarmupQuery)
            {
                Warmed.Add(alias);
                if (WarmupThrows)
                    throw new InvalidOperationException("warmup boom");
                return EmptyPlanResult();
            }

            TranslateCallsByAlias[alias] = TranslateCallsByAlias.GetValueOrDefault(alias) + 1;
            StreamingCallsByAlias[alias] = StreamingCallsByAlias.GetValueOrDefault(alias) + 1;

            TimeSpan delay = TranslateDelayProvider?.Invoke(alias) ?? TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, TranslateHonorsCancellation ? cancellationToken : CancellationToken.None)
                    .ConfigureAwait(false);
            }

            SemanticTranslationResult result = TranslateBehavior?.Invoke(alias, naturalLanguageQuery) ?? EmptyPlanResult();

            // Emit the model output as a streamed delta so the runner surfaces ProbeToken progress; a real
            // model always streams something, so fall back to the query text when there's no raw output.
            string streamed = !string.IsNullOrEmpty(result.RawModelOutput) ? result.RawModelOutput : naturalLanguageQuery;
            if (onToken is not null && !string.IsNullOrEmpty(streamed))
            {
                onToken(streamed);
                StreamedTokens.Add(streamed);
            }

            return result;
        }

        private static SemanticTranslationResult EmptyPlanResult() =>
            SemanticTranslationResult.Ok(new SemanticSearchPlan());

        public void SetModelOverride(string? modelAlias) => _currentOverride = modelAlias;
        public void SetEnabled(bool enabled) { }
        public void SetDevicePreferenceOrder(string? order) { }
        public void SetAvailableAccelerators(bool hasGpu, bool hasNpu) { }
        public void SetGpuMemoryBytes(long dedicatedVideoMemoryBytes) { }
        public void SetUnloadAfterUse(bool unloadAfterUse) { }

        // Records the model resident at unload time (the previous candidate — SetModelOverride for the next
        // has not run yet), so tests can assert the sweep evicts each candidate before preparing the next.
        public Task UnloadCurrentModelAsync(CancellationToken cancellationToken)
        {
            Unloaded.Add(_currentOverride ?? "");
            return Task.CompletedTask;
        }

        public void RefreshCatalog() { }
    }
}
