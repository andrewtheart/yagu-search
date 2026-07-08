using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Yagu.Services.Ai;

/// <summary>Coarse phases reported while a model-qualification sweep runs.</summary>
public enum SemanticQualificationStage
{
    /// <summary>Enumerating the hardware-appropriate candidate models.</summary>
    EnumeratingCandidates,

    /// <summary>Downloading/loading a candidate before probing it.</summary>
    PreparingCandidate,

    /// <summary>Running a probe query against the current candidate.</summary>
    Probing,

    /// <summary>The sweep finished.</summary>
    Done,
}

/// <summary>Progress update for a running model-qualification sweep, suitable for direct display.</summary>
public sealed class SemanticQualificationProgress
{
    public required SemanticQualificationStage Stage { get; init; }

    /// <summary>Alias of the candidate currently being prepared/probed, when applicable.</summary>
    public string? CandidateAlias { get; init; }

    /// <summary>1-based index of the current candidate in the ladder, when applicable.</summary>
    public int CandidateIndex { get; init; }

    /// <summary>Total number of candidates in the ladder, when known.</summary>
    public int CandidateCount { get; init; }

    /// <summary>1-based index of the current probe within the candidate, when applicable.</summary>
    public int ProbeIndex { get; init; }

    /// <summary>Total number of probes per candidate, when known.</summary>
    public int ProbeCount { get; init; }

    /// <summary>Human-readable one-line status suitable for direct display.</summary>
    public string Message => Stage switch
    {
        SemanticQualificationStage.EnumeratingCandidates => "Finding models that fit this machine…",
        SemanticQualificationStage.PreparingCandidate =>
            CandidateCount > 0
                ? $"Preparing {CandidateAlias} ({CandidateIndex}/{CandidateCount})…"
                : $"Preparing {CandidateAlias}…",
        SemanticQualificationStage.Probing =>
            ProbeCount > 0
                ? $"Testing {CandidateAlias} — query {ProbeIndex}/{ProbeCount}…"
                : $"Testing {CandidateAlias}…",
        SemanticQualificationStage.Done => "Model check complete.",
        _ => "Working…",
    };
}

/// <summary>
/// Drives a first-run model-qualification sweep against a live <see cref="ISemanticQueryTranslator"/>.
/// It enumerates the hardware-appropriate candidate models, then adapts the translator into the
/// probe-runner delegate <see cref="ModelQualification.QualifyAsync"/> expects: for each candidate it
/// selects and prepares the model once (untimed), then times a single <see cref="ISemanticQueryTranslator.TranslateAsync"/>
/// per probe and resolves the plan with <see cref="SemanticPlanApplier"/> so <see cref="SemanticProbeScorer"/>
/// can score it.
/// </summary>
/// <remarks>
/// This runner probes the model <b>in-process</b>. A model that fails with a managed exception (load
/// failure, timeout, disposed inference) is converted to <see cref="ProbeOutcome.Crash"/> and the
/// candidate is abandoned. A hard native abort (e.g. a WebGPU/DirectML fail-fast or an OpenVINO
/// prep-callback crash) still takes the host down — moving probing out-of-process is the follow-up
/// that closes that gap. Because the driver takes the probe-runner as a delegate, swapping in a
/// worker-backed runner later needs no change here.
/// <para>
/// The sweep mutates the translator's model override as it probes; on return the override is left at
/// the last-probed candidate. Callers should explicitly apply their final model choice afterwards.
/// </para>
/// </remarks>
public sealed class SemanticModelQualificationRunner
{
    /// <summary>Small slack added to a query's time limit before the hard backstop fires, so a model
    /// that honors cancellation gets a moment to unwind cleanly (surfacing as a real cancellation)
    /// before we give up waiting and treat it as a wedged, over-limit query.</summary>
    private const int QueryTimeoutBackstopGraceMs = 500;

    /// <summary>Default cap on how many candidates the first-run sweep probes. The ladder is ordered
    /// best-first (the hardware-recommended model, then the known-good preferred families by rank), so
    /// the models most likely to qualify sit at the top. A machine's Foundry catalog can list dozens of
    /// runnable models (28 was observed), and probing every one — each a download + load + full probe
    /// set — is needlessly slow when none of the top picks qualifies: the exotic tail is neither
    /// preferred nor likely to beat the best-effort fallback already found. Callers pass this as
    /// <c>maxCandidates</c>; the sweep still stops early the moment a candidate qualifies.</summary>
    public const int DefaultMaxCandidates = 5;

    /// <summary>Neutral, representative query used to warm a freshly-loaded model ONCE (untimed) before the
    /// first timed probe. A model's very first inference pays a one-time cost (graph compilation, JIT,
    /// execution-provider kernel selection/autotuning) that can dwarf its steady-state latency; without a
    /// warmup, that cost lands on the first timed probe and can falsely trip the per-query latency gate on
    /// an otherwise-fast model. Running one throwaway inference first absorbs it, so the timed probes
    /// measure warm (steady-state) latency \u2014 which is what the user actually experiences.</summary>
    public const string WarmupQuery = "list files modified in the last day";

    private readonly ISemanticQueryTranslator _translator;
    private readonly string? _defaultDirectory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, bool>? _directoryExists;
    private readonly int _maxCandidates;

    /// <param name="translator">The live translator to qualify models through.</param>
    /// <param name="defaultDirectory">Directory used to resolve probe plans that name no location.</param>
    /// <param name="nowProvider">Clock used to resolve relative dates in probes (defaults to <see cref="DateTimeOffset.Now"/>).</param>
    /// <param name="directoryExists">Optional probe used to reject a hallucinated directory in a plan.</param>
    /// <param name="maxCandidates">Caps how many candidates are tried (0 = no cap). The sweep stops early
    /// once a candidate qualifies regardless of this cap.</param>
    public SemanticModelQualificationRunner(
        ISemanticQueryTranslator translator,
        string? defaultDirectory = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<string, bool>? directoryExists = null,
        int maxCandidates = 0)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _defaultDirectory = defaultDirectory;
        _now = nowProvider ?? (() => DateTimeOffset.Now);
        _directoryExists = directoryExists;
        _maxCandidates = maxCandidates < 0 ? 0 : maxCandidates;
    }

    /// <summary>
    /// Runs the qualification sweep and returns the qualified model (if any), the best-effort fallback,
    /// and per-candidate reports. Returns an empty result when no candidate models are available.
    /// </summary>
    /// <param name="probes">The probe set to run against each candidate.</param>
    /// <param name="thresholds">User-chosen time limits: how long to wait for a model to load, and how
    /// long a simple / complex query may take before the candidate is abandoned as too slow.</param>
    /// <param name="progress">Optional progress sink for the running sweep.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    public async Task<ModelQualificationResult> RunAsync(
        IReadOnlyList<SemanticProbe> probes,
        ModelQualificationThresholds thresholds,
        IProgress<SemanticQualificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(thresholds);

        progress?.Report(new SemanticQualificationProgress
        {
            Stage = SemanticQualificationStage.EnumeratingCandidates,
        });

        IReadOnlyList<string> candidates = await EnumerateCandidatesAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            progress?.Report(new SemanticQualificationProgress { Stage = SemanticQualificationStage.Done });
            return new ModelQualificationResult { Reports = Array.Empty<CandidateQualificationReport>() };
        }

        int candidateCount = candidates.Count;
        int probeCount = probes.Count;

        // Prepare each candidate at most once (download/load is not counted toward probe latency).
        string? preparedAlias = null;

        async Task<ProbeOutcome> ProbeRunner(string alias, SemanticProbe probe, CancellationToken token)
        {
            int candidateIndex = IndexOf(candidates, alias) + 1;

            if (!string.Equals(preparedAlias, alias, StringComparison.OrdinalIgnoreCase))
            {
                // Evict the previously-probed candidate from memory BEFORE loading the next one so only a
                // single model is ever resident. Foundry Local does not evict on load — models accumulate —
                // so walking a ladder of candidates would otherwise pile them up and OOM ("bad allocation").
                // Best-effort and never throws; done before SetModelOverride below (which drops Yagu's
                // reference to the loaded model, after which it could no longer be unloaded).
                if (preparedAlias is not null)
                    await _translator.UnloadCurrentModelAsync(token).ConfigureAwait(false);

                progress?.Report(new SemanticQualificationProgress
                {
                    Stage = SemanticQualificationStage.PreparingCandidate,
                    CandidateAlias = alias,
                    CandidateIndex = candidateIndex,
                    CandidateCount = candidateCount,
                    ProbeCount = probeCount,
                });

                try
                {
                    _translator.SetModelOverride(alias);
                    var loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    bool loadLeaked = false;
                    try
                    {
                        if (thresholds.ModelLoadMaxMs > 0)
                            loadCts.CancelAfter(thresholds.ModelLoadMaxMs);

                        Task loadTask = _translator.PrepareModelAsync(alias, null, loadCts.Token);

                        // A wedged NATIVE load can ignore the cancellation token entirely and never return —
                        // the same failure class as a wedged inference (Foundry Local's load_model has been
                        // observed to hang intermittently when switching off a large resident model). So the
                        // CancelAfter above is not enough on its own. Race the load against a Task.Delay
                        // backstop so the sweep ABANDONS this candidate instead of hanging forever on the
                        // first probe. A leaked wedged load is observed and its linked CTS disposed once it
                        // finally returns.
                        if (thresholds.ModelLoadMaxMs > 0 && !loadTask.IsCompleted)
                        {
                            Task backstop = Task.Delay(thresholds.ModelLoadMaxMs + QueryTimeoutBackstopGraceMs, token);
                            if (await Task.WhenAny(loadTask, backstop).ConfigureAwait(false) == backstop)
                            {
                                // The sweep itself may have been cancelled while we waited.
                                token.ThrowIfCancellationRequested();
                                if (!loadCts.IsCancellationRequested)
                                    loadCts.Cancel();
                                loadLeaked = true;
                                ObserveAndDisposeInBackground(loadTask, loadCts);
                                return ProbeOutcome.Crash();
                            }
                        }

                        await loadTask.ConfigureAwait(false);
                        preparedAlias = alias;
                    }
                    finally
                    {
                        if (!loadLeaked)
                            loadCts.Dispose();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Loading exceeded the user's model-load time limit — abandon this candidate as if it
                    // had crashed so the driver moves on to the next one.
                    return ProbeOutcome.Crash();
                }
                catch (Exception)
                {
                    // Could not load this candidate at all — treat it as a crash so the driver abandons it.
                    return ProbeOutcome.Crash();
                }

                // Absorb the model's one-time cold-start cost with a single untimed warmup inference so the
                // FIRST timed probe below measures warm (steady-state) latency instead of falsely tripping
                // the per-query gate on graph-compilation/JIT overhead. Best-effort: a warmup failure or
                // timeout is swallowed and probing proceeds as before, so it can only help a candidate.
                await WarmUpModelAsync(alias, thresholds, token).ConfigureAwait(false);
            }

            progress?.Report(new SemanticQualificationProgress
            {
                Stage = SemanticQualificationStage.Probing,
                CandidateAlias = alias,
                CandidateIndex = candidateIndex,
                CandidateCount = candidateCount,
                ProbeIndex = IndexOf(probes, probe) + 1,
                ProbeCount = probeCount,
            });

            var context = new SemanticTranslationContext
            {
                Now = _now(),
                DefaultDirectory = _defaultDirectory,
                OriginalQuery = probe.Query,
                DirectoryExists = _directoryExists,
            };

            // Enforce the user's per-query time limit as a HARD backstop. TranslateAsync has its own
            // internal watchdog, but that is far longer than the user's patience (e.g. 300s for a
            // reasoning model) and — worse — a wedged native inference can ignore the cancellation token
            // entirely and never return, which would hang the whole sweep on a single query (the symptom
            // the user hit: stuck on "query 1/6" for 10+ minutes). So we (a) pass a limit-bound token so a
            // well-behaved model stops at the user's deadline, and (b) race the translation against a
            // Task.Delay backstop so the sweep advances even when the inference ignores cancellation.
            // Either way an over-limit query is reported as too slow, abandoning the candidate.
            int queryLimitMs = thresholds.QueryMaxMs(probe.Complexity);

            var stopwatch = Stopwatch.StartNew();
            var queryCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            bool leaked = false;
            try
            {
                if (queryLimitMs > 0)
                    queryCts.CancelAfter(queryLimitMs);

                Task<SemanticTranslationResult> translateTask =
                    _translator.TranslateAsync(probe.Query, context, null, queryCts.Token);

                if (queryLimitMs > 0 && !translateTask.IsCompleted)
                {
                    Task backstop = Task.Delay(queryLimitMs + QueryTimeoutBackstopGraceMs, token);
                    if (await Task.WhenAny(translateTask, backstop).ConfigureAwait(false) == backstop)
                    {
                        // The sweep itself may have been cancelled while we waited.
                        token.ThrowIfCancellationRequested();

                        // The inference blew past the user's limit (and, if wedged, ignored the
                        // cancellation token). Ask it to stop, stop waiting on it, and report the query as
                        // too slow so the driver abandons this candidate. A leaked wedged task is observed
                        // and its linked CTS disposed once it finally returns.
                        stopwatch.Stop();
                        if (!queryCts.IsCancellationRequested)
                            queryCts.Cancel();
                        leaked = true;
                        ObserveAndDisposeInBackground(translateTask, queryCts);
                        return ProbeOutcome.Miss(Math.Max((int)stopwatch.ElapsedMilliseconds, queryLimitMs + 1));
                    }
                }

                SemanticTranslationResult result;
                try
                {
                    result = await translateTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Our per-query watchdog cancelled the inference (the model honored the deadline) —
                    // report it as too slow so the candidate is abandoned.
                    stopwatch.Stop();
                    return ProbeOutcome.Miss(Math.Max((int)stopwatch.ElapsedMilliseconds, queryLimitMs + 1));
                }
                catch (Exception)
                {
                    stopwatch.Stop();
                    return ProbeOutcome.Crash((int)stopwatch.ElapsedMilliseconds);
                }
                stopwatch.Stop();

                int latencyMs = (int)stopwatch.ElapsedMilliseconds;
                if (!result.Success || result.Plan is null)
                {
                    return ProbeOutcome.Miss(latencyMs);
                }

                ResolvedSearchPlan resolved = SemanticPlanApplier.Resolve(result.Plan, context);
                return ProbeOutcome.Answered(resolved, latencyMs);
            }
            finally
            {
                if (!leaked)
                    queryCts.Dispose();
            }
        }

        ModelQualificationResult qualification = await ModelQualification
            .QualifyAsync(candidates, probes, thresholds, ProbeRunner, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new SemanticQualificationProgress { Stage = SemanticQualificationStage.Done });
        return qualification;
    }

    /// <summary>Observes a leaked (possibly wedged) translation task so its eventual completion cannot
    /// raise an unobserved-exception, and disposes the linked cancellation source once it finally
    /// returns — keeping the token valid for as long as the abandoned inference still references it.</summary>
    private static void ObserveAndDisposeInBackground(Task task, IDisposable linkedSource) =>
        _ = task.ContinueWith(
            t =>
            {
                _ = t.Exception; // observe any fault so it does not resurface on the finalizer thread
                linkedSource.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Runs a single UNTIMED warmup translation (<see cref="WarmupQuery"/>) against a freshly-prepared
    /// model so its one-time cold-start cost is paid here rather than inflating the first timed probe.
    /// Best-effort: any warmup fault or timeout is swallowed and probing proceeds normally (the first probe
    /// simply pays the cold-start cost as before), so warmup can only help a candidate, never change its
    /// verdict. It is bounded by the user's model-load budget and the same hard <see cref="Task.Delay"/>
    /// backstop the timed probes use, so a wedged warmup that ignores cancellation cannot hang the sweep.
    /// Only a cancellation of the overall sweep propagates.
    /// </summary>
    private async Task WarmUpModelAsync(string alias, ModelQualificationThresholds thresholds, CancellationToken token)
    {
        var context = new SemanticTranslationContext
        {
            Now = _now(),
            DefaultDirectory = _defaultDirectory,
            OriginalQuery = WarmupQuery,
            DirectoryExists = _directoryExists,
        };

        int budgetMs = thresholds.ModelLoadMaxMs;
        var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool leaked = false;
        try
        {
            if (budgetMs > 0)
                warmupCts.CancelAfter(budgetMs);

            Task<SemanticTranslationResult> warmupTask =
                _translator.TranslateAsync(WarmupQuery, context, null, warmupCts.Token);

            if (budgetMs > 0 && !warmupTask.IsCompleted)
            {
                Task backstop = Task.Delay(budgetMs + QueryTimeoutBackstopGraceMs, token);
                if (await Task.WhenAny(warmupTask, backstop).ConfigureAwait(false) == backstop)
                {
                    // The sweep itself may have been cancelled while we waited.
                    token.ThrowIfCancellationRequested();

                    // The warmup blew past the load budget (and, if wedged, ignored cancellation). Ask it to
                    // stop, stop waiting on it, and proceed to timed probing anyway. A leaked wedged task is
                    // observed and its linked CTS disposed once it finally returns.
                    if (!warmupCts.IsCancellationRequested)
                        warmupCts.Cancel();
                    leaked = true;
                    ObserveAndDisposeInBackground(warmupTask, warmupCts);
                    return;
                }
            }

            try
            {
                await warmupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // the overall sweep was cancelled — propagate
            }
            catch
            {
                // Warmup faulted or hit its own budget-bound token — ignore and proceed to timed probing.
            }
        }
        finally
        {
            if (!leaked)
                warmupCts.Dispose();
        }
    }

    /// <summary>Enumerates candidate aliases (recommended first, de-duplicated), capped by
    /// <c>maxCandidates</c>. Returns empty when the translator lists no runnable models.</summary>
    private async Task<IReadOnlyList<string>> EnumerateCandidatesAsync(
        IProgress<SemanticQualificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SemanticModelOption> options;
        try
        {
            options = await _translator.ListModelOptionsAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var ordered = new List<string>(options.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Aliases of "novel" families — ones the static preference list has never seen (IsPreferredFamily
        // is false). Tracked so the cap below can guarantee at least one of them is probed.
        var novel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(SemanticModelOption option)
        {
            if (string.IsNullOrWhiteSpace(option.Alias) || !seen.Add(option.Alias))
                return;
            ordered.Add(option.Alias);
            if (!option.IsPreferredFamily)
                novel.Add(option.Alias);
        }

        // Recommended candidate leads the ladder.
        foreach (var option in options)
        {
            if (option.IsRecommended)
                Consider(option);
        }

        // Then the remainder in the order the translator ranked them.
        foreach (var option in options)
        {
            Consider(option);
        }

        if (_maxCandidates > 0 && ordered.Count > _maxCandidates)
        {
            var capped = ordered.GetRange(0, _maxCandidates);

            // Reserve the last slot for a novel family (one absent from the static preference list) so a
            // newly released, potentially superior model always gets probed instead of being permanently
            // pinned below the cap by the static ranking. Only act when the cap would otherwise be filled
            // entirely by known/preferred families AND a novel family sits just below the cut — then swap
            // the weakest kept candidate (the last slot) for the highest-ordered novel one. Requires the cap
            // to leave room beyond the leading recommended pick (>= 2 slots) so the recommended model is
            // never displaced.
            if (_maxCandidates >= 2 && !capped.Exists(a => novel.Contains(a)))
            {
                for (int i = _maxCandidates; i < ordered.Count; i++)
                {
                    if (novel.Contains(ordered[i]))
                    {
                        capped[_maxCandidates - 1] = ordered[i];
                        break;
                    }
                }
            }

            ordered = capped;
        }

        return ordered;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int IndexOf(IReadOnlyList<SemanticProbe> list, SemanticProbe value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], value))
                return i;
        }
        return -1;
    }
}
