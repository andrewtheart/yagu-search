using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>A streamed output delta arrived for the probe currently being answered. Carries the
    /// incremental text in <see cref="SemanticQualificationProgress.TokenDelta"/> so a live chat
    /// transcript can grow the assistant's response token-by-token.</summary>
    ProbeToken,

    /// <summary>A probe finished. Carries the pass/fail verdict, the reason it failed (if any), and the
    /// measured latency so the transcript can stamp the answer with a ✓/✗ status.</summary>
    ProbeCompleted,

    /// <summary>The sweep finished.</summary>
    Done,
}

/// <summary>Why a single probe did not pass, for display in the qualification transcript.</summary>
public enum SemanticProbeFailureReason
{
    /// <summary>The probe passed — not a failure.</summary>
    None,

    /// <summary>The model produced no usable/parseable plan (empty or garbage output).</summary>
    NoAnswer,

    /// <summary>The model answered but the plan did not match what the probe expected.</summary>
    Inaccurate,

    /// <summary>The model exceeded the user's per-query time limit for the probe's complexity.</summary>
    TooSlow,

    /// <summary>The model crashed / failed to load while answering the probe.</summary>
    Crashed,
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

    /// <summary>The natural-language probe query being sent to the model (on <see cref="SemanticQualificationStage.Probing"/>
    /// and <see cref="SemanticQualificationStage.ProbeCompleted"/>). Null for a candidate that failed to
    /// load before any probe ran.</summary>
    public string? ProbeQuery { get; init; }

    /// <summary>An incremental chunk of the model's streamed output (on <see cref="SemanticQualificationStage.ProbeToken"/>).</summary>
    public string? TokenDelta { get; init; }

    /// <summary>On <see cref="SemanticQualificationStage.ProbeCompleted"/>, whether the probe passed.</summary>
    public bool ProbePassed { get; init; }

    /// <summary>On <see cref="SemanticQualificationStage.ProbeCompleted"/>, true when the probe PASSED but
    /// answered slower than its hard per-query limit (within the grace band) — shown as a "slow" warning
    /// rather than a failure.</summary>
    public bool ProbeSlowWarning { get; init; }

    /// <summary>On <see cref="SemanticQualificationStage.ProbeCompleted"/>, why the probe failed (if it did).</summary>
    public SemanticProbeFailureReason FailureReason { get; init; }

    /// <summary>On <see cref="SemanticQualificationStage.ProbeCompleted"/>, the measured probe latency in ms.</summary>
    public int LatencyMs { get; init; }

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
        SemanticQualificationStage.ProbeToken => $"Testing {CandidateAlias}…",
        SemanticQualificationStage.ProbeCompleted =>
            ProbePassed
                ? $"{CandidateAlias} answered query {ProbeIndex}/{ProbeCount} correctly."
                : $"{CandidateAlias} failed query {ProbeIndex}/{ProbeCount}.",
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

    /// <summary>How many times a WEDGED timed probe is retried before the candidate is abandoned. A wedge
    /// is a native inference that ignored the cancellation token and blew the deadline — an INTERMITTENT
    /// stall (transient GPU/VRAM pressure), so the exact same query usually answers in a few seconds on the
    /// next attempt. Retrying once keeps a single transient wedge from disqualifying an otherwise-correct
    /// model (e.g. phi-4, which answers every probe correctly in ~5s but occasionally wedges one to 30-48s).
    /// The retry only runs AFTER the wedged native op has drained (see <see cref="DrainWedgedInferenceAsync"/>)
    /// so it never runs two concurrent inferences on one onnxruntime model. A probe that HONORS the deadline
    /// (honestly too slow, not wedged) is NOT retried.</summary>
    private const int WedgeRetryCount = 1;

    /// <summary>Upper bound (ms) on how long the sweep waits for a wedged native inference to finally drain
    /// before issuing a retry. Chosen above the translator's ~45s inference watchdog so an ordinary wedge
    /// (which returns when that watchdog fires) drains and the retry can proceed; a wedge that outlives even
    /// this is treated as hard-hung and the candidate is abandoned (leak + observe) as before.</summary>
    private const int WedgeDrainBudgetMs = 50_000;

    /// <summary>Classification of one timed inference attempt (see <see cref="TimedAttempt"/>).</summary>
    private enum TimedAttemptStatus
    {
        /// <summary>The model returned a result within the deadline (may still be a wrong/unparseable plan).</summary>
        Answered,

        /// <summary>The model HONORED the deadline's cancellation and stopped — an honestly too-slow answer,
        /// not a transient wedge, so it is not retried.</summary>
        HonoredDeadline,

        /// <summary>The inference threw a non-cancellation exception.</summary>
        Faulted,

        /// <summary>The inference blew the deadline AND ignored cancellation (a wedged native op still
        /// running). Carries the live task + CTS so the caller can drain it before a retry, or leak+observe
        /// it on abandon.</summary>
        Wedged,
    }

    /// <summary>The outcome of one timed inference attempt. A <see cref="TimedAttemptStatus.Wedged"/> attempt
    /// carries the still-running native task and its linked CTS (undisposed) so the caller can drain it
    /// before a retry or leak+observe it on abandon; every other status has already disposed its CTS.</summary>
    private readonly struct TimedAttempt
    {
        public TimedAttemptStatus Status { get; init; }
        public SemanticTranslationResult? Result { get; init; }
        public int LatencyMs { get; init; }
        public Task<SemanticTranslationResult>? WedgedTask { get; init; }
        public CancellationTokenSource? WedgedCts { get; init; }
    }


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

    /// <summary>Default time (ms) a FAILED probe (too slow / inaccurate / crashed) is held visible before
    /// the sweep advances, so the qualification dialog can show the user WHY a step failed before moving
    /// on to the next model. The engine default is 0 (no pacing — unit tests run at full speed); the
    /// interactive caller (the first-run dialog) opts into <see cref="DefaultFailedProbeHoldMs"/>.</summary>
    public const int DefaultFailedProbeHoldMs = 2000;

    private readonly ISemanticQueryTranslator _translator;
    private readonly string? _defaultDirectory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, bool>? _directoryExists;
    private readonly int _maxCandidates;
    private readonly int _failedProbeHoldMs;

    /// <param name="translator">The live translator to qualify models through.</param>
    /// <param name="defaultDirectory">Directory used to resolve probe plans that name no location.</param>
    /// <param name="nowProvider">Clock used to resolve relative dates in probes (defaults to <see cref="DateTimeOffset.Now"/>).</param>
    /// <param name="directoryExists">Optional probe used to reject a hallucinated directory in a plan.</param>
    /// <param name="maxCandidates">Caps how many candidates are tried (0 = no cap). The sweep stops early
    /// once a candidate qualifies regardless of this cap.</param>
    /// <param name="failedProbeHoldMs">How long (ms) to hold a failed probe visible before advancing, so a
    /// live UI can show the failure. 0 (default) = no pause, for fast unit tests.</param>
    public SemanticModelQualificationRunner(
        ISemanticQueryTranslator translator,
        string? defaultDirectory = null,
        Func<DateTimeOffset>? nowProvider = null,
        Func<string, bool>? directoryExists = null,
        int maxCandidates = 0,
        int failedProbeHoldMs = 0)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _defaultDirectory = defaultDirectory;
        _now = nowProvider ?? (() => DateTimeOffset.Now);
        _directoryExists = directoryExists;
        _maxCandidates = maxCandidates < 0 ? 0 : maxCandidates;
        _failedProbeHoldMs = failedProbeHoldMs < 0 ? 0 : failedProbeHoldMs;
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

        // Classifies why a probe did not pass, for the transcript's ✗ chip.
        static SemanticProbeFailureReason ClassifyFailure(ProbeOutcome outcome, bool passed, int queryLimitMs)
        {
            if (passed) return SemanticProbeFailureReason.None;
            if (outcome.Crashed) return SemanticProbeFailureReason.Crashed;
            if (!outcome.Completed)
                return queryLimitMs > 0 && outcome.LatencyMs > queryLimitMs
                    ? SemanticProbeFailureReason.TooSlow
                    : SemanticProbeFailureReason.NoAnswer;
            return SemanticProbeFailureReason.Inaccurate; // parseable plan, but it didn't match the probe
        }

        // Collapses a raw model response to a single bounded log line (the raw JSON can contain newlines).
        static string TruncateForLog(string? s)
        {
            const int max = 2000;
            if (string.IsNullOrWhiteSpace(s)) return "(none)";
            string t = s.Trim().ReplaceLineEndings(" ");
            return t.Length <= max ? t : t[..max] + "... (truncated)";
        }

        // Reports a probe's pass/fail verdict, then — when it FAILED — holds it visible for the configured
        // pause so a live UI can show the user why before the sweep advances. Returns the same outcome so
        // the qualification driver's abandon logic is unchanged.
        async Task<ProbeOutcome> CompleteProbeAsync(
            ProbeOutcome outcome, SemanticProbe probe, string alias, int candidateIndex, int queryLimitMs, CancellationToken token,
            string? rawModelOutput = null)
        {
            bool passed = outcome.Completed && SemanticProbeScorer.Passes(probe, outcome.Plan);
            // A correct-but-slow probe (answered over the hard limit but within the tolerance band, so it
            // wasn't cancelled) still PASSES, flagged as a warning.
            bool slowWarning = passed && queryLimitMs > 0 && outcome.LatencyMs > queryLimitMs;
            SemanticProbeFailureReason reason = ClassifyFailure(outcome, passed, queryLimitMs);
            int probeNumber = IndexOf(probes, probe) + 1;
            Yagu.Services.LogService.Instance.Info(
                "Semantic.Probe",
                $"[{alias}] probe {probeNumber}/{probeCount} '{probe.Query}' -> " +
                $"{(passed ? (slowWarning ? "PASS(slow)" : "PASS") : "FAIL:" + reason)} ({outcome.LatencyMs} ms, limit {queryLimitMs} ms).");
            // On failure, also log the RAW model output (not just the resolved plan, which SemanticPlanApplier
            // already logs at VRB) so a genuine model error can be told apart from a plan-applier mapping gap.
            // "(none)" when the model produced no answer (wedge / timeout / crash).
            if (!passed)
                Yagu.Services.LogService.Instance.Info(
                    "Semantic.Probe",
                    $"[{alias}] probe {probeNumber} raw model output: {TruncateForLog(rawModelOutput)}");
            progress?.Report(new SemanticQualificationProgress
            {
                Stage = SemanticQualificationStage.ProbeCompleted,
                CandidateAlias = alias,
                CandidateIndex = candidateIndex,
                CandidateCount = candidateCount,
                ProbeIndex = IndexOf(probes, probe) + 1,
                ProbeCount = probeCount,
                ProbeQuery = probe.Query,
                ProbePassed = passed,
                ProbeSlowWarning = slowWarning,
                FailureReason = reason,
                LatencyMs = outcome.LatencyMs,
            });
            if (!passed && _failedProbeHoldMs > 0)
                await Task.Delay(_failedProbeHoldMs, token).ConfigureAwait(false);
            return outcome;
        }

        // A candidate that never loaded is shown as a failed step (no query) and held the same way.
        async Task<ProbeOutcome> CompleteLoadFailureAsync(string alias, int candidateIndex, CancellationToken token)
        {
            Yagu.Services.LogService.Instance.Info("Semantic.Probe", $"[{alias}] failed to load — skipping candidate.");
            progress?.Report(new SemanticQualificationProgress
            {
                Stage = SemanticQualificationStage.ProbeCompleted,
                CandidateAlias = alias,
                CandidateIndex = candidateIndex,
                CandidateCount = candidateCount,
                ProbeCount = probeCount,
                ProbePassed = false,
                FailureReason = SemanticProbeFailureReason.Crashed,
                LatencyMs = 0,
            });
            if (_failedProbeHoldMs > 0)
                await Task.Delay(_failedProbeHoldMs, token).ConfigureAwait(false);
            return ProbeOutcome.Crash();
        }

        async Task<ProbeOutcome> ProbeRunner(string alias, SemanticProbe probe, CancellationToken token)
        {
            int candidateIndex = IndexOf(candidates, alias) + 1;
            int probeNumber = IndexOf(probes, probe) + 1;

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
                                return await CompleteLoadFailureAsync(alias, candidateIndex, token).ConfigureAwait(false);
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
                    return await CompleteLoadFailureAsync(alias, candidateIndex, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Could not load this candidate at all — treat it as a crash so the driver abandons it.
                    return await CompleteLoadFailureAsync(alias, candidateIndex, token).ConfigureAwait(false);
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
                ProbeIndex = probeNumber,
                ProbeCount = probeCount,
                ProbeQuery = probe.Query,
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
            // The inference is allowed to run to the limit PLUS the grace band: a correct answer that lands
            // in [limit, limit+tolerance] still passes (flagged "slow"), so the deadline that cancels /
            // backstops the query is the tolerant one. CompleteProbeAsync still gets the HARD limit so it
            // can tell a fast pass from a slow-but-accepted one.
            int deadlineMs = queryLimitMs > 0 ? queryLimitMs + thresholds.LatencyToleranceMs : 0;

            // One timed inference attempt: races the RELIABLE non-streaming translation against the user's
            // deadline + grace and classifies the outcome. The SDK's token-streaming API intermittently
            // STALLS (a back-to-back / post-switch CompleteChatStreamingAsync can never yield a token), which
            // would falsely disqualify a model that answers the same query in a few seconds non-streaming; so
            // the sweep uses TranslateAsync and the transcript's "streamed" answer is a UI-only reveal of the
            // completed response. A WEDGE (backstop tripped because the native inference ignored the
            // cancellation token) hands the still-running task + CTS back for draining/retry; every other
            // status owns and disposes its CTS here.
            async Task<TimedAttempt> RunTimedAttemptAsync()
            {
                var sw = Stopwatch.StartNew();
                var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                bool wedgedOut = false;
                try
                {
                    if (deadlineMs > 0)
                        attemptCts.CancelAfter(deadlineMs);

                    Task<SemanticTranslationResult> translateTask =
                        _translator.TranslateAsync(probe.Query, context, null, attemptCts.Token);

                    if (deadlineMs > 0 && !translateTask.IsCompleted)
                    {
                        Task backstop = Task.Delay(deadlineMs + QueryTimeoutBackstopGraceMs, token);
                        if (await Task.WhenAny(translateTask, backstop).ConfigureAwait(false) == backstop)
                        {
                            // The sweep itself may have been cancelled while we waited.
                            token.ThrowIfCancellationRequested();

                            // Over the limit + grace AND ignored cancellation: a wedged native op. Ask it to
                            // stop and hand the live task back so the caller can drain it before a retry.
                            sw.Stop();
                            if (!attemptCts.IsCancellationRequested)
                                attemptCts.Cancel();
                            wedgedOut = true;
                            return new TimedAttempt
                            {
                                Status = TimedAttemptStatus.Wedged,
                                LatencyMs = Math.Max((int)sw.ElapsedMilliseconds, deadlineMs + 1),
                                WedgedTask = translateTask,
                                WedgedCts = attemptCts,
                            };
                        }
                    }

                    try
                    {
                        SemanticTranslationResult result = await translateTask.ConfigureAwait(false);
                        sw.Stop();
                        return new TimedAttempt
                        {
                            Status = TimedAttemptStatus.Answered,
                            Result = result,
                            LatencyMs = (int)sw.ElapsedMilliseconds,
                        };
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // Our per-query watchdog cancelled the inference (the model HONORED the deadline) —
                        // an honestly too-slow answer, not a transient wedge, so it is not retried.
                        sw.Stop();
                        return new TimedAttempt
                        {
                            Status = TimedAttemptStatus.HonoredDeadline,
                            LatencyMs = Math.Max((int)sw.ElapsedMilliseconds, deadlineMs + 1),
                        };
                    }
                    catch (Exception)
                    {
                        sw.Stop();
                        return new TimedAttempt { Status = TimedAttemptStatus.Faulted, LatencyMs = (int)sw.ElapsedMilliseconds };
                    }
                }
                finally
                {
                    if (!wedgedOut)
                        attemptCts.Dispose();
                }
            }

            TimedAttempt attempt = await RunTimedAttemptAsync().ConfigureAwait(false);

            // A WEDGE is INTERMITTENT — the same query, run again, usually completes in a few seconds. Rather
            // than disqualify an otherwise-correct model on ONE transient stall, retry the probe once.
            // CRITICAL: the wedged native op is still running (it ignored cancellation), so we must let it
            // DRAIN before issuing the retry — starting a second inference on the same onnxruntime model while
            // the first is still executing runs two concurrent native ops and can crash. If the wedged op does
            // not drain within the bounded budget it is treated as hard-hung: leak+observe it and abandon the
            // candidate as too slow, exactly as before.
            for (int retriesLeft = WedgeRetryCount; attempt.Status == TimedAttemptStatus.Wedged && retriesLeft > 0; retriesLeft--)
            {
                if (!await DrainWedgedInferenceAsync(attempt.WedgedTask!, attempt.WedgedCts!, token).ConfigureAwait(false))
                    break; // hard-hung — keep the wedged attempt and fall through to abandon it below

                attempt = await RunTimedAttemptAsync().ConfigureAwait(false);
            }

            switch (attempt.Status)
            {
                case TimedAttemptStatus.Wedged:
                    // Still wedged after the retry budget (or hard-hung): leak the stuck native op (observed +
                    // its CTS disposed when it finally returns) and report the query as too slow.
                    ObserveAndDisposeInBackground(attempt.WedgedTask!, attempt.WedgedCts!);
                    return await CompleteProbeAsync(
                        ProbeOutcome.Miss(attempt.LatencyMs), probe, alias, candidateIndex, queryLimitMs, token).ConfigureAwait(false);

                case TimedAttemptStatus.HonoredDeadline:
                    return await CompleteProbeAsync(
                        ProbeOutcome.Miss(attempt.LatencyMs), probe, alias, candidateIndex, queryLimitMs, token).ConfigureAwait(false);

                case TimedAttemptStatus.Faulted:
                    return await CompleteProbeAsync(
                        ProbeOutcome.Crash(attempt.LatencyMs), probe, alias, candidateIndex, queryLimitMs, token).ConfigureAwait(false);

                default: // Answered
                {
                    SemanticTranslationResult result = attempt.Result!;

                    // Reveal the model's finished answer in the transcript (the dialog animates it as a
                    // typewriter reveal, so it still looks streamed without the SDK streaming stall risk).
                    if (progress is not null && !string.IsNullOrEmpty(result.RawModelOutput))
                        progress.Report(new SemanticQualificationProgress
                        {
                            Stage = SemanticQualificationStage.ProbeToken,
                            CandidateAlias = alias,
                            CandidateIndex = candidateIndex,
                            CandidateCount = candidateCount,
                            ProbeIndex = probeNumber,
                            ProbeCount = probeCount,
                            TokenDelta = result.RawModelOutput,
                        });

                    if (!result.Success || result.Plan is null)
                        return await CompleteProbeAsync(
                            ProbeOutcome.Miss(attempt.LatencyMs), probe, alias, candidateIndex, queryLimitMs, token,
                            result.RawModelOutput).ConfigureAwait(false);

                    ResolvedSearchPlan resolved = SemanticPlanApplier.Resolve(result.Plan, context);
                    return await CompleteProbeAsync(
                        ProbeOutcome.Answered(resolved, attempt.LatencyMs), probe, alias, candidateIndex, queryLimitMs, token,
                        result.RawModelOutput).ConfigureAwait(false);
                }
            }
        }

        ModelQualificationResult qualification = await ModelQualification
            .QualifyAsync(candidates, probes, thresholds, ProbeRunner, cancellationToken)
            .ConfigureAwait(false);

        foreach (var r in qualification.Reports)
        {
            Yagu.Services.LogService.Instance.Info(
                "Semantic.Probe",
                $"Candidate '{r.ModelAlias}': verdict={(r.Verdict.Passed ? "QUALIFIED" : "REJECTED")} ({r.Verdict.Reason}), " +
                $"accuracy={r.EffectiveAccuracy * 100:0}% ({r.Probes.Count(p => p.Passed)}/{r.ScoredProbeCount} answered, {r.Probes.Count} of {probes.Count} run), " +
                $"median={r.MedianLatencyMs} ms, max={r.MaxLatencyMs} ms, crashed={r.Crashed}.");
        }
        Yagu.Services.LogService.Instance.Info(
            "Semantic.Probe",
            $"Sweep complete: qualified='{qualification.QualifiedModelAlias ?? "<none>"}', " +
            $"bestEffort='{qualification.BestEffortModelAlias ?? "<none>"}'.");

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
    /// Awaits a wedged inference until it finally returns (drains), bounded by <see cref="WedgeDrainBudgetMs"/>,
    /// so the native model is idle again BEFORE a retry issues a fresh inference — starting a second inference
    /// on the same onnxruntime model while the first is still executing runs two concurrent native ops and can
    /// crash. Returns true once the wedged op drained (its CTS disposed here); false if it is still hung after
    /// the budget (hard-wedged), in which case the caller leaks + observes it. Only overall-sweep cancellation
    /// propagates.
    /// </summary>
    private static async Task<bool> DrainWedgedInferenceAsync(
        Task<SemanticTranslationResult> wedgedTask, CancellationTokenSource wedgedCts, CancellationToken token)
    {
        Task drainBackstop = Task.Delay(WedgeDrainBudgetMs, token);
        if (await Task.WhenAny(wedgedTask, drainBackstop).ConfigureAwait(false) == drainBackstop)
        {
            // The sweep itself may have been cancelled while we waited.
            token.ThrowIfCancellationRequested();
            return false; // still hung after the drain budget — hard-wedged
        }

        _ = wedgedTask.Exception; // the task has completed; observe any fault so it does not resurface
        wedgedCts.Dispose();
        return true;
    }

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
    /// <c>maxCandidates</c>, then reordered so a family's smaller/faster variant is tried before its
    /// larger sibling. Returns empty when the translator lists no runnable models.</summary>
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
            if (string.IsNullOrWhiteSpace(option.Alias))
                return;
            // Reasoning / chain-of-thought models can NEVER be auto-selected (their <think> traces break
            // the strict-JSON contract — see FoundryModelSelector.IsAutoSelectable), so probing them is pure
            // wasted time: every probe blows the per-query limit and then WEDGES (the native inference keeps
            // running long past the deadline), dragging the sweep out for minutes, yet the model could never
            // be the one chosen. Keep them off the candidate ladder entirely.
            if (IsReasoningAlias(option.Alias))
                return;
            if (!seen.Add(option.Alias))
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

        // Within a family (one alias is a '-'-delimited prefix of another, e.g. "phi-4" and
        // "phi-4-mini"), try the SMALLER variant first: a mini that clears the same probes runs faster,
        // and since the sweep stops at the first qualifier, ordering it ahead of its larger sibling lets
        // the faster model win when both are accurate. Done AFTER the cap so it only REORDERS — never
        // changes — the set of probed candidates. Cross-family order (recommended-first, then rank) is kept.
        var sizeByAlias = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var sizeOption in options)
            if (!string.IsNullOrWhiteSpace(sizeOption.Alias))
                sizeByAlias[sizeOption.Alias] = sizeOption.SizeBytes ?? 0;
        ReorderSmallerFamilyVariantsFirst(ordered, sizeByAlias);

        return ordered;
    }

    /// <summary>Whether <paramref name="alias"/> names a reasoning / chain-of-thought model (e.g.
    /// <c>phi-4-mini-reasoning</c>). Mirrors <c>FoundryModelSelector.IsReasoningAlias</c> as a
    /// self-contained string check so this runner — which is compiled into the test assembly — takes no
    /// compile dependency on the Foundry-coupled selector. Such models are excluded from the qualification
    /// ladder because they can never be auto-selected and only slow the sweep down.</summary>
    internal static bool IsReasoningAlias(string? alias) =>
        !string.IsNullOrWhiteSpace(alias) && alias.Contains("reasoning", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reorders the ladder so that, within a model family (one alias is a '-'-delimited prefix
    /// of another — e.g. <c>phi-4</c> and <c>phi-4-mini</c>), the SMALLER-download variant is tried
    /// first. The sweep stops at the first qualifier, so ordering a faster mini ahead of its larger
    /// sibling makes the faster model win when both clear the probes. Each family is emitted at the
    /// position of its earliest-ranked member, so cross-family order (recommended-first, then rank) is
    /// preserved.</summary>
    internal static void ReorderSmallerFamilyVariantsFirst(List<string> ordered, IReadOnlyDictionary<string, long> sizeByAlias)
    {
        if (ordered.Count < 2)
            return;

        // Family key for each slot = the earliest ladder alias it shares a family with (else itself).
        var familyOf = new string[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            familyOf[i] = ordered[i];
            for (int j = 0; j < i; j++)
            {
                if (AreSameFamily(ordered[j], ordered[i]))
                {
                    familyOf[i] = familyOf[j];
                    break;
                }
            }
        }

        long SizeKey(string alias)
        {
            long size = sizeByAlias.TryGetValue(alias, out var v) ? v : 0;
            return size <= 0 ? long.MaxValue : size; // unknown size = sort last (prefer a known small one)
        }

        var result = new List<string>(ordered.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ordered.Count; i++)
        {
            string family = familyOf[i];
            if (!emitted.Add(family))
                continue; // this family was already emitted at its first-appearance slot

            var members = new List<int>();
            for (int k = 0; k < ordered.Count; k++)
                if (string.Equals(familyOf[k], family, StringComparison.OrdinalIgnoreCase))
                    members.Add(k);

            // Smallest known download first; an unknown size sorts last; original order breaks ties.
            members.Sort((a, b) =>
            {
                int c = SizeKey(ordered[a]).CompareTo(SizeKey(ordered[b]));
                return c != 0 ? c : a.CompareTo(b);
            });
            foreach (int m in members)
                result.Add(ordered[m]);
        }

        ordered.Clear();
        ordered.AddRange(result);
    }

    /// <summary>Two aliases are the same model family when one is a '-'-delimited prefix of the other
    /// (e.g. <c>phi-4</c> and <c>phi-4-mini</c>). Case-insensitive; identical aliases are the same
    /// family. Distinct same-length aliases (e.g. <c>qwen2.5-0.5b</c> / <c>qwen2.5-1.5b</c>) count as
    /// different families since neither is a '-'-delimited prefix of the other.</summary>
    internal static bool AreSameFamily(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        if (a.Length == b.Length)
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        string shorter = a.Length < b.Length ? a : b;
        string longer = a.Length < b.Length ? b : a;
        return longer.StartsWith(shorter + "-", StringComparison.OrdinalIgnoreCase);
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
