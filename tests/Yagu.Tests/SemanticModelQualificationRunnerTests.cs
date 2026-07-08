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
/// <see cref="ISemanticQueryTranslator"/>. These verify candidate ordering, stop-on-first-pass,
/// prepare-once-per-candidate, crash abandonment, best-effort selection, the candidate cap and the
/// empty-catalog path — independent of any real model.
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

    private static SemanticModelOption Option(string alias, bool recommended = false, bool preferred = false) =>
        new() { Alias = alias, DisplayName = alias, IsRecommended = recommended, IsPreferredFamily = preferred };

    private static SemanticTranslationResult PngPlan() =>
        SemanticTranslationResult.Ok(new SemanticSearchPlan { IncludeGlobs = new List<string> { "*.png" } });

    private static SemanticTranslationResult EmptyPlan() =>
        SemanticTranslationResult.Ok(new SemanticSearchPlan());

    [Fact]
    public async Task RunAsync_FirstRecommendedCandidatePasses_QualifiesItAndStops()
    {
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B"), Option("C") },
            TranslateBehavior = (_, _) => EmptyPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { PassAnything(), PassAnything() }, Thresholds, progress: null, CancellationToken.None);

        Assert.True(result.AnyQualified);
        Assert.Equal("A", result.QualifiedModelAlias);
        Assert.Single(result.Reports);
        Assert.Equal(new[] { "A" }, translator.Prepared);
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
    public async Task RunAsync_SingleQualifyingCandidate_IsNeverUnloaded()
    {
        // The very first candidate qualifies, so there is no "next" to make room for — the runner must not
        // evict the one model it just qualified (the user should be able to use it immediately).
        var translator = new FakeTranslator
        {
            Options = { Option("A", recommended: true), Option("B") },
            TranslateBehavior = (_, _) => PngPlan(),
        };
        var runner = new SemanticModelQualificationRunner(translator);

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, Thresholds, progress: null, CancellationToken.None);

        Assert.Equal("A", result.QualifiedModelAlias);
        Assert.Empty(translator.Unloaded);
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
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50 };

        var result = await runner.RunAsync(new[] { RequiresPngGlob() }, thresholds, progress: null, CancellationToken.None);

        // The wedged candidate is abandoned as too slow (a latency violation, not a crash) and the next
        // candidate qualifies — the sweep never stalls on the hung query.
        Assert.Equal("B", result.QualifiedModelAlias);
        var wedged = result.Reports.Single(r => r.ModelAlias == "wedged");
        Assert.False(wedged.Crashed);
        Assert.False(wedged.Verdict.Passed);
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
        var thresholds = ModelQualificationThresholds.Default with { SimpleQueryMaxMs = 50, ComplexQueryMaxMs = 50 };

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
