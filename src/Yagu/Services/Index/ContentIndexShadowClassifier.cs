using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Runs a scope's per-path classification in the out-of-process <b>mapped query worker</b> (plan §6 Stage 2
/// shadow mode) and collects metrics — <b>without ever pruning</b>. It opens a pinned worker query session
/// over the scope's memory-mapped format-v3 base + segments, classifies a batch of discovered paths, closes
/// the session, and (optionally) diffs the worker's verdicts against the in-process oracle, logging any
/// mismatch. Because it never affects which files are scanned, running it alongside the authoritative
/// in-process gate is risk-free; it exists to prove the worker classification matches the oracle and to
/// measure the worker's paged footprint before pruning is enabled (Stage 4).
/// <para>
/// Fail-safe by contract: a worker that is unavailable, un-accelerable, or that faults yields a
/// non-accelerable <see cref="ShadowMetrics"/> (the host would live-scan); it never throws to the caller.
/// </para>
/// </summary>
internal sealed class ContentIndexShadowClassifier
{
    private const string LogSource = "ContentIndex";
    private readonly Func<IndexQueryOpenRequest, CancellationToken, Task<IndexQueryOpenResult?>> _openScope;
    private readonly Func<int, IReadOnlyList<string>, CancellationToken, Task<byte[]?>> _classifyPaths;
    private readonly Func<int, CancellationToken, Task> _closeScope;

    public ContentIndexShadowClassifier(IndexWorkerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _openScope = client.OpenQueryScopeAsync;
        _classifyPaths = (sessionId, paths, cancellationToken) =>
            client.ClassifyPathsAsync(sessionId, paths, cancellationToken);
        _closeScope = client.CloseQueryScopeAsync;
    }

    internal ContentIndexShadowClassifier(
        Func<IndexQueryOpenRequest, CancellationToken, Task<IndexQueryOpenResult?>> openScope,
        Func<int, IReadOnlyList<string>, CancellationToken, Task<byte[]?>> classifyPaths,
        Func<int, CancellationToken, Task> closeScope)
    {
        _openScope = openScope ?? throw new ArgumentNullException(nameof(openScope));
        _classifyPaths = classifyPaths ?? throw new ArgumentNullException(nameof(classifyPaths));
        _closeScope = closeScope ?? throw new ArgumentNullException(nameof(closeScope));
    }

    /// <summary>Per-layer inputs for a shadow classification (candidate + B0 dirty content-id sets, matching
    /// the in-process gate's inputs so the comparison is deterministic).</summary>
    public sealed record ShadowScope(
        int SessionId,
        string BaseDir,
        IReadOnlyList<string> SegmentDirs,
        IReadOnlySet<int> BaseCandidates,
        IReadOnlyList<IReadOnlySet<int>> SegmentCandidates,
        IReadOnlySet<long> BaseDirty,
        IReadOnlyList<IReadOnlySet<long>> SegmentDirties);

    /// <summary>The outcome + measurements of one shadow classification pass.</summary>
    public sealed record ShadowMetrics(
        bool Accelerable,
        int CandidateCount,
        int PathCount,
        long OpenMs,
        long ClassifyMs,
        int MismatchCount,
        string? BypassReason)
    {
        public IndexQueryOpenDiagnostics? OpenDiagnostics { get; init; }

        /// <summary>A non-accelerable pass (worker unavailable / un-upgraded scope / fault) — the host would
        /// live-scan.</summary>
        public static ShadowMetrics NotAccelerable(int pathCount, long openMs, string reason)
            => new(false, 0, pathCount, openMs, 0, 0, reason);
    }

    /// <summary>
    /// Opens a worker query session for <paramref name="scope"/>, classifies <paramref name="paths"/>, closes
    /// the session, and (when <paramref name="oracleVerdict"/> is supplied) counts + logs any verdict that
    /// differs from the in-process oracle. Returns the pass metrics; never prunes and never throws.
    /// </summary>
    public async Task<ShadowMetrics> RunAsync(
        ShadowScope scope,
        IReadOnlyList<string> paths,
        Func<string, byte>? oracleVerdict,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(paths);

        var sw = Stopwatch.StartNew();
        try
        {
            var openRequest = new IndexQueryOpenRequest
            {
                SessionId = scope.SessionId,
                BaseDir = scope.BaseDir,
                SegmentDirs = scope.SegmentDirs.ToArray(),
                BaseCandidatesBase64 = IndexWorkerProtocol.EncodeCandidates(scope.BaseCandidates.ToArray()),
                SegmentCandidatesBase64 = scope.SegmentCandidates.Select(s => IndexWorkerProtocol.EncodeCandidates(s.ToArray())).ToArray(),
                BaseDirtyBase64 = IndexQueryWorkerProtocol.EncodeContentIds(scope.BaseDirty),
                SegmentDirtiesBase64 = scope.SegmentDirties.Select(IndexQueryWorkerProtocol.EncodeContentIds).ToArray(),
            };

            IndexQueryOpenResult? open = await _openScope(openRequest, cancellationToken).ConfigureAwait(false);
            long openMs = sw.ElapsedMilliseconds;
            if (open is null || !open.Accelerable)
            {
                return ShadowMetrics.NotAccelerable(paths.Count, openMs, open?.BypassReason ?? "worker unavailable");
            }

            sw.Restart();
            byte[]? verdicts = await _classifyPaths(scope.SessionId, paths, cancellationToken).ConfigureAwait(false);
            long classifyMs = sw.ElapsedMilliseconds;
            await _closeScope(scope.SessionId, cancellationToken).ConfigureAwait(false);

            if (verdicts is null)
            {
                return ShadowMetrics.NotAccelerable(paths.Count, openMs, "classify failed");
            }

            int mismatches = CountMismatches(paths, verdicts, oracleVerdict);
            var metrics = new ShadowMetrics(
                true,
                open.CandidateCount,
                paths.Count,
                openMs,
                classifyMs,
                mismatches,
                null)
            {
                OpenDiagnostics = open.Diagnostics,
            };
            YaguLog.For(LogSource).LogInformation(
                "shadow classify: scope='{BaseDir}' segments={Segments} paths={Paths} candidates={Candidates} openMs={OpenMs} classifyMs={ClassifyMs} mismatches={Mismatches}.",
                scope.BaseDir, scope.SegmentDirs.Count, paths.Count, open.CandidateCount, openMs, classifyMs, mismatches);
            return metrics;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure → treat as a live-scan (never let shadow mode surface an error to the search).
            YaguLog.For(LogSource).LogDebug(ex, "shadow classify failed for scope '{BaseDir}'.", scope.BaseDir);
            return ShadowMetrics.NotAccelerable(paths.Count, sw.ElapsedMilliseconds, "shadow error: " + ex.Message);
        }
    }

    private static int CountMismatches(IReadOnlyList<string> paths, byte[] verdicts, Func<string, byte>? oracleVerdict)
    {
        if (oracleVerdict is null)
            return 0;

        int mismatches = 0;
        int count = Math.Min(paths.Count, verdicts.Length);
        for (int i = 0; i < count; i++)
        {
            byte expected = oracleVerdict(paths[i]);
            if (verdicts[i] != expected)
            {
                mismatches++;
                YaguLog.For(LogSource).LogWarning(
                    "shadow classify MISMATCH for '{Path}': worker={WorkerVerdict} oracle={OracleVerdict}.",
                    paths[i], verdicts[i], expected);
            }
        }
        return mismatches;
    }
}
