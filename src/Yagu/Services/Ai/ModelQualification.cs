using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Yagu.Services.Ai;

/// <summary>Outcome of running one probe against one candidate model.</summary>
public sealed class ProbeOutcome
{
    /// <summary>True when the model returned a parseable plan (even if wrong). False when it produced
    /// no usable output (empty/garbage), timed out, or the worker crashed.</summary>
    public required bool Completed { get; init; }

    /// <summary>True when the model process crashed or failed to load. A crash abandons the candidate
    /// immediately — the remaining probes are not run.</summary>
    public bool Crashed { get; init; }

    /// <summary>The resolved plan, when <see cref="Completed"/>. Null otherwise.</summary>
    public ResolvedSearchPlan? Plan { get; init; }

    /// <summary>Wall-clock latency of this probe in milliseconds.</summary>
    public required int LatencyMs { get; init; }

    public static ProbeOutcome Crash(int latencyMs = 0) =>
        new() { Completed = false, Crashed = true, LatencyMs = latencyMs };

    public static ProbeOutcome Miss(int latencyMs) =>
        new() { Completed = false, LatencyMs = latencyMs };

    public static ProbeOutcome Answered(ResolvedSearchPlan plan, int latencyMs) =>
        new() { Completed = true, Plan = plan, LatencyMs = latencyMs };
}

/// <summary>Per-probe result recorded in a candidate's qualification report.</summary>
public sealed class ProbeResult
{
    public required string Query { get; init; }
    public required bool Passed { get; init; }
    public required bool Completed { get; init; }
    public required int LatencyMs { get; init; }
}

/// <summary>The full qualification report for a single candidate model.</summary>
public sealed class CandidateQualificationReport
{
    public required string ModelAlias { get; init; }

    /// <summary>Fraction of the FULL probe set the model answered correctly (passed / total probes).
    /// This is the pass/fail metric fed to <see cref="ModelQualificationPolicy"/>; a candidate abandoned
    /// early (crash or latency violation) already fails on that reason, so dividing by the full count here
    /// never changes a verdict.</summary>
    public required double Accuracy { get; init; }

    /// <summary>Fraction of the probes ACTUALLY RUN that the model answered correctly (passed / probes-run).
    /// Unlike <see cref="Accuracy"/>, it does not dilute a candidate that was abandoned early on a crash or
    /// latency violation with probes it never got to attempt, so a fast candidate cut off after a couple of
    /// correct probes is not unfairly out-ranked by a slow-but-complete one. Used only to rank the
    /// best-effort fallback.</summary>
    public required double EffectiveAccuracy { get; init; }

    public required int MedianLatencyMs { get; init; }
    public required int MaxLatencyMs { get; init; }
    public required bool Crashed { get; init; }
    public required QualificationVerdict Verdict { get; init; }
    public required IReadOnlyList<ProbeResult> Probes { get; init; }
}

/// <summary>The result of walking the candidate ladder.</summary>
public sealed class ModelQualificationResult
{
    /// <summary>The first candidate that passed <see cref="ModelQualificationPolicy"/>, or null when
    /// none qualified.</summary>
    public string? QualifiedModelAlias { get; init; }

    /// <summary>The candidate with the highest <see cref="CandidateQualificationReport.EffectiveAccuracy"/>
    /// (accuracy over the probes it actually ran) seen, even if none passed — the sensible fallback to
    /// suggest when nothing clears the bar. Ties break toward the faster (lower median latency) candidate.
    /// Null when no candidate produced any output.</summary>
    public string? BestEffortModelAlias { get; init; }

    /// <summary>Per-candidate reports in the order candidates were probed.</summary>
    public required IReadOnlyList<CandidateQualificationReport> Reports { get; init; }

    /// <summary>True when a candidate cleared the qualification bar.</summary>
    public bool AnyQualified => QualifiedModelAlias is not null;
}

/// <summary>
/// Walks a hardware-appropriate candidate ladder, probing each model with a mixed set of queries and
/// promoting the first one that clears <see cref="ModelQualificationPolicy"/>. Probing itself is
/// injected as a delegate (the caller runs it out-of-process so a crashy model cannot take Yagu down),
/// keeping this driver pure and unit-testable.
/// </summary>
public static class ModelQualification
{
    /// <summary>
    /// Qualifies models in <paramref name="candidateAliases"/> order, stopping at the first that passes.
    /// </summary>
    /// <param name="candidateAliases">Ordered candidate model aliases (best-first).</param>
    /// <param name="probes">The probe set to run against each candidate.</param>
    /// <param name="thresholds">User-chosen per-query time limits used to abandon a candidate that is
    /// too slow for its patience.</param>
    /// <param name="probeRunner">Runs one probe against one candidate and returns its outcome. Should
    /// execute out-of-process so a native crash surfaces as <see cref="ProbeOutcome.Crash"/> rather than
    /// aborting the host.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    public static async Task<ModelQualificationResult> QualifyAsync(
        IReadOnlyList<string> candidateAliases,
        IReadOnlyList<SemanticProbe> probes,
        ModelQualificationThresholds thresholds,
        Func<string, SemanticProbe, CancellationToken, Task<ProbeOutcome>> probeRunner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateAliases);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(probeRunner);

        var reports = new List<CandidateQualificationReport>(candidateAliases.Count);
        string? qualified = null;

        foreach (var alias in candidateAliases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var report = await ProbeCandidateAsync(alias, probes, thresholds, probeRunner, cancellationToken)
                .ConfigureAwait(false);
            reports.Add(report);

            if (report.Verdict.Passed)
            {
                qualified = alias;
                break;
            }
        }

        return new ModelQualificationResult
        {
            QualifiedModelAlias = qualified,
            BestEffortModelAlias = SelectBestEffort(reports),
            Reports = reports,
        };
    }

    private static async Task<CandidateQualificationReport> ProbeCandidateAsync(
        string alias,
        IReadOnlyList<SemanticProbe> probes,
        ModelQualificationThresholds thresholds,
        Func<string, SemanticProbe, CancellationToken, Task<ProbeOutcome>> probeRunner,
        CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>(probes.Count);
        var completedLatencies = new List<int>(probes.Count);
        int passed = 0;
        int maxLatency = 0;
        bool crashed = false;
        LatencyViolation? latencyViolation = null;

        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProbeOutcome outcome = await probeRunner(alias, probe, cancellationToken).ConfigureAwait(false);
            bool probePassed = outcome.Completed && SemanticProbeScorer.Passes(probe, outcome.Plan);
            if (probePassed) passed++;
            if (outcome.LatencyMs > maxLatency) maxLatency = outcome.LatencyMs;
            if (outcome.Completed) completedLatencies.Add(outcome.LatencyMs);

            results.Add(new ProbeResult
            {
                Query = probe.Query,
                Passed = probePassed,
                Completed = outcome.Completed,
                LatencyMs = outcome.LatencyMs,
            });

            if (outcome.Crashed)
            {
                // A crash means the model is fundamentally unusable — abandon it now instead of paying
                // for the remaining probes.
                crashed = true;
                break;
            }

            int limit = thresholds.QueryMaxMs(probe.Complexity);
            if (outcome.LatencyMs > limit)
            {
                // A single probe blew the user's per-query limit for its complexity, so this candidate can
                // never clear the bar no matter how the remaining probes score. Abandon it now rather than
                // paying for more probes that can each take minutes on a weak CPU. The violation carries
                // the true (too-slow) reason for Evaluate.
                latencyViolation = new LatencyViolation(outcome.LatencyMs, limit, probe.Complexity);
                break;
            }
        }

        double accuracy = probes.Count == 0 ? 0.0 : (double)passed / probes.Count;
        // Best-effort ranking uses accuracy over the probes actually RUN (results.Count), so a candidate
        // abandoned early on a crash/latency violation is judged on what it attempted, not penalised for
        // probes it never reached. results.Count == probes.Count for any candidate that ran to completion.
        double effectiveAccuracy = results.Count == 0 ? 0.0 : (double)passed / results.Count;
        int median = Median(completedLatencies);
        var verdict = ModelQualificationPolicy.Evaluate(accuracy, crashed, latencyViolation);

        return new CandidateQualificationReport
        {
            ModelAlias = alias,
            Accuracy = accuracy,
            EffectiveAccuracy = effectiveAccuracy,
            MedianLatencyMs = median,
            MaxLatencyMs = maxLatency,
            Crashed = crashed,
            Verdict = verdict,
            Probes = results,
        };
    }

    /// <summary>Highest effective-accuracy candidate (accuracy over the probes it actually ran), breaking
    /// ties toward the faster median. Ranking on effective — rather than full-set — accuracy stops a fast
    /// candidate that was abandoned early after a run of correct probes from being out-ranked by a slower
    /// one that merely completed more (diluted) probes. Candidates that produced no output at all (crashed
    /// with zero completed probes) are ignored.</summary>
    private static string? SelectBestEffort(IReadOnlyList<CandidateQualificationReport> reports)
    {
        CandidateQualificationReport? best = null;
        foreach (var r in reports)
        {
            if (r.Probes.All(p => !p.Completed))
                continue; // never produced anything usable

            if (best is null ||
                r.EffectiveAccuracy > best.EffectiveAccuracy ||
                (r.EffectiveAccuracy == best.EffectiveAccuracy && r.MedianLatencyMs < best.MedianLatencyMs))
            {
                best = r;
            }
        }
        return best?.ModelAlias;
    }

    /// <summary>Integer median of a latency sample. Returns <see cref="int.MaxValue"/> for an empty
    /// sample so a candidate with no completed probes is treated as unusably slow.</summary>
    private static int Median(List<int> values)
    {
        if (values.Count == 0)
            return int.MaxValue;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (int)(((long)values[mid - 1] + values[mid]) / 2);
    }
}
