using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Yagu.Services.Index;

namespace Yagu.IndexWorker;

/// <summary>
/// Worker-side handler for mapped query-session ops (plan §5.2):
/// <c>openQueryScope</c> memory-maps a scope's format-v3 base + segments and pins a
/// <see cref="V3MappedLayeredQuerySession"/>; <c>classifyPaths</c> classifies a batch against it (returning
/// one verdict byte per path), <c>reconcileB1</c> rescues provisional prunes that changed during the
/// search, and <c>closeQueryScope</c> releases the pinned mappings. A request can be shadow-only or active
/// pruning; active mode records fresh nonmembers provisionally until B1 reconciliation. Because readers
/// are memory-mapped, resident memory tracks touched pages rather than the full index size.
/// <para>
/// Every failure is contained here: a scope that is not query-ready (missing / un-upgraded v3, or a layer
/// without a tombstone index) replies <see cref="IndexQueryOpenResult.Accelerable"/> = false so the host
/// falls back to a live scan; a torn/corrupt mapped block surfaces as a failed reply. The host treats any
/// non-accelerable / failed reply as "live-scan this scope".
/// </para>
/// </summary>
internal static class IndexQueryScopeHost
{
    private sealed class PinnedScope : IDisposable
    {
        public required ContentIndexV3Reader BaseReader { get; init; }
        public required IReadOnlyList<ContentIndexV3Reader> SegmentReaders { get; init; }
        public required V3MappedLayeredQuerySession Session { get; init; }
        public required int Parallelism { get; init; }

        /// <summary>When true (plan §6 Stage 4), <c>classifyPaths</c> routes fresh nonmembers for pruning so
        /// they can be reconciled at B1; when false (Stage-2 shadow) classify is a pure classifier.</summary>
        public required bool PruningEnabled { get; init; }

        public void Dispose()
        {
            BaseReader.Dispose();
            foreach (ContentIndexV3Reader reader in SegmentReaders)
                reader.Dispose();
        }
    }

    private static readonly ConcurrentDictionary<int, PinnedScope> Sessions = new();

    /// <summary>Whether an op name is one of the mapped query-session ops handled here.</summary>
    public static bool IsQueryOp(string op) => op is
        IndexWorkerProtocol.Ops.OpenQueryScope or
        IndexWorkerProtocol.Ops.ClassifyPaths or
        IndexWorkerProtocol.Ops.ReconcileB1 or
        IndexWorkerProtocol.Ops.CloseQueryScope or
        IndexWorkerProtocol.Ops.CancelSession;

    public static IndexWorkerMessage Handle(IndexWorkerRequest request) => request.Op switch
    {
        IndexWorkerProtocol.Ops.OpenQueryScope => Open(request),
        IndexWorkerProtocol.Ops.ClassifyPaths => Classify(request),
        IndexWorkerProtocol.Ops.ReconcileB1 => ReconcileB1(request),
        IndexWorkerProtocol.Ops.CloseQueryScope => Close(request),
        IndexWorkerProtocol.Ops.CancelSession => Cancel(request),
        _ => Fail(request.Id, $"unknown query op '{request.Op}'"),
    };

    /// <summary>Releases every pinned session (called on worker shutdown).</summary>
    public static void CloseAll()
    {
        int removed = 0;
        foreach (int sessionId in new List<int>(Sessions.Keys))
        {
            if (Sessions.TryRemove(sessionId, out PinnedScope? scope))
            {
                scope.Dispose();
                removed++;
            }
        }
        Log($"query-session close-all removed={removed} active={Sessions.Count}");
    }

    private static IndexWorkerMessage Open(IndexWorkerRequest request)
    {
        var workerOpenTimer = Stopwatch.StartNew();
        IndexQueryOpenRequest spec = JsonSerializer.Deserialize(request.QueryJson ?? "", IndexQueryJsonContext.Default.IndexQueryOpenRequest)
            ?? throw new InvalidDataException("openQueryScope payload was empty");

        // Replacing a session with the same id (e.g. a host retry) releases the prior mappings first.
        if (Sessions.TryRemove(spec.SessionId, out PinnedScope? prior))
        {
            prior.Dispose();
            Log($"query-session replace id={spec.SessionId} active={Sessions.Count}");
        }

        ContentIndexV3Reader? baseReader = null;
        var segmentReaders = new List<ContentIndexV3Reader>(spec.SegmentDirs.Length);
        try
        {
            var mapOpenTimer = Stopwatch.StartNew();
            baseReader = ContentIndexV3Format.TryOpen(spec.BaseDir);
            if (baseReader is null)
                return OpenResult(request.Id, accelerable: false, 0, "base generation is not query-ready");

            foreach (string segmentDir in spec.SegmentDirs)
            {
                ContentIndexV3Reader? segReader = ContentIndexV3Format.TryOpen(segmentDir);
                if (segReader is null)
                {
                    DisposeAll(baseReader, segmentReaders);
                    return OpenResult(request.Id, accelerable: false, 0, "a segment is not query-ready");
                }
                segmentReaders.Add(segReader);
            }

            // All-or-nothing tombstone coverage: any layer without a tombstone index could silently miss a
            // tombstone, so fall back to the in-process layered evaluation.
            if (!V3MappedLayeredQuerySession.AllLayersHaveTombstoneIndex(baseReader, segmentReaders))
            {
                DisposeAll(baseReader, segmentReaders);
                return OpenResult(request.Id, accelerable: false, 0, "a layer has no tombstone index");
            }
            mapOpenTimer.Stop();

            V3MappedLayeredQuerySession session = BuildSession(spec, baseReader, segmentReaders);

            var scope = new PinnedScope
            {
                BaseReader = baseReader,
                SegmentReaders = segmentReaders,
                Session = session,
                Parallelism = Math.Clamp(spec.Parallelism, 1, IndexWorkerParallelism.Maximum),
                PruningEnabled = spec.PruningEnabled,
            };
            Sessions[spec.SessionId] = scope;
            workerOpenTimer.Stop();
            var diagnostics = new IndexQueryOpenDiagnostics
            {
                LayerCount = session.LayerCount,
                PathRecordCount = session.PathRecordCount,
                TombstoneRecordCount = session.TombstoneRecordCount,
                DistinctRouteHashCount = session.DistinctRouteHashCount,
                CandidatesEvaluatedInWorker = session.CandidatesEvaluatedInWorker,
                MapOpenMs = mapOpenTimer.Elapsed.TotalMilliseconds,
                CandidateEvaluationMs = session.CandidateEvaluationMs,
                RoutingIndexMs = session.RoutingIndexMs,
                WorkerOpenMs = workerOpenTimer.Elapsed.TotalMilliseconds,
            };
            Log($"query-session open id={spec.SessionId} base='{spec.BaseDir}' segments={spec.SegmentDirs.Length} " +
                $"pruning={spec.PruningEnabled} parallelism={scope.Parallelism} active={Sessions.Count} " +
                $"layers={diagnostics.LayerCount} routeRecords={diagnostics.RouteRecordCount} " +
                $"distinctRoutes={diagnostics.DistinctRouteHashCount} amplification={diagnostics.RouteRecordAmplification:F3} " +
                $"mapMs={diagnostics.MapOpenMs:F1} candidatesMs={diagnostics.CandidateEvaluationMs:F1} " +
                $"routingMs={diagnostics.RoutingIndexMs:F1} workerOpenMs={diagnostics.WorkerOpenMs:F1}");
            return OpenResult(request.Id, accelerable: true, session.CandidateCount, null, diagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DisposeAll(baseReader, segmentReaders);
            return Fail(request.Id, "openQueryScope failed: " + ex.Message);
        }
    }

    private static V3MappedLayeredQuerySession BuildSession(
        IndexQueryOpenRequest spec, ContentIndexV3Reader baseReader, IReadOnlyList<ContentIndexV3Reader> segmentReaders)
    {
        var baseDirty = DecodeDirty(spec.BaseDirtyBase64);
        var segmentDirties = new DirtyContentSet[segmentReaders.Count];
        for (int i = 0; i < segmentReaders.Count; i++)
            segmentDirties[i] = DecodeDirty(Index(spec.SegmentDirtiesBase64, i));

        // When the host supplied candidate ids (a deterministic shadow comparison), use them; otherwise the
        // worker evaluates them itself from the wire query over its mapped v3 postings — so a large-scope
        // query needs no host-held index.
        if (!string.IsNullOrEmpty(spec.BaseCandidatesBase64) || string.IsNullOrEmpty(spec.QueryRpnBase64))
        {
            IReadOnlySet<int> baseCandidates = DecodeCandidateSet(spec.BaseCandidatesBase64);
            var segmentCandidates = new IReadOnlySet<int>[segmentReaders.Count];
            for (int i = 0; i < segmentReaders.Count; i++)
                segmentCandidates[i] = DecodeCandidateSet(Index(spec.SegmentCandidatesBase64, i));
            return V3MappedLayeredQuerySession.BeginWithCandidates(
                baseReader, segmentReaders, baseCandidates, segmentCandidates, baseDirty, segmentDirties);
        }

        TrigramExpression query = TrigramQueryRpn.Decode(Convert.FromBase64String(spec.QueryRpnBase64!));
        return V3MappedLayeredQuerySession.Begin(
            baseReader, segmentReaders, query, baseDirty, segmentDirties,
            Math.Clamp(spec.Parallelism, 1, IndexWorkerParallelism.Maximum));
    }

    private static IndexWorkerMessage Classify(IndexWorkerRequest request)
    {
        IndexQueryClassifyRequest spec = JsonSerializer.Deserialize(request.QueryJson ?? "", IndexQueryJsonContext.Default.IndexQueryClassifyRequest)
            ?? throw new InvalidDataException("classifyPaths payload was empty");

        if (!Sessions.TryGetValue(spec.SessionId, out PinnedScope? scope))
            return Fail(request.Id, $"no open query session {spec.SessionId}");

        // Deadline abandonment (plan §5.2): a batch whose deadline already elapsed while it was queued is not
        // worth classifying — fail it so the host live-scans that batch instead of paying for stale work.
        if (spec.DeadlineUnixMs != 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > spec.DeadlineUnixMs)
            return Fail(request.Id, $"classify batch {spec.BatchSeq} deadline expired");

        string[] paths = IndexQueryWorkerProtocol.DecodePaths(spec.PathsBase64);
        var verdicts = new byte[paths.Length];
        IReadOnlyList<IndexPathClassification> classifications = scope.Session.ClassifyBatch(
            paths, scope.Parallelism, recordPruning: scope.PruningEnabled);
        for (int i = 0; i < paths.Length; i++)
        {
            verdicts[i] = IndexQueryWorkerProtocol.VerdictFor(classifications[i]);
        }

        var result = new IndexQueryClassifyResult
        {
            SessionId = spec.SessionId,
            BatchSeq = spec.BatchSeq,
            VerdictsBase64 = IndexQueryWorkerProtocol.EncodeVerdicts(verdicts),
        };
        return new IndexWorkerMessage
        {
            Type = IndexWorkerProtocol.MessageTypes.Result,
            Id = request.Id,
            Ok = true,
            QueryResultJson = JsonSerializer.Serialize(result, IndexQueryJsonContext.Default.IndexQueryClassifyResult),
        };
    }

    private static IndexWorkerMessage ReconcileB1(IndexWorkerRequest request)
    {
        IndexQueryReconcileRequest spec = JsonSerializer.Deserialize(request.QueryJson ?? "", IndexQueryJsonContext.Default.IndexQueryReconcileRequest)
            ?? throw new InvalidDataException("reconcileB1 payload was empty");

        if (!Sessions.TryGetValue(spec.SessionId, out PinnedScope? scope))
            return Fail(request.Id, $"no open query session {spec.SessionId}");

        // Not-certain (the host's B1 journal replay was discontinuous) → fail safe by rescuing EVERY remaining
        // prune; the dirty sets are ignored. Otherwise rescue only the provisional paths whose layer's content
        // became dirty over [B0, B1). A count mismatch throws (caught by the worker dispatcher → the host
        // live-scans / replays its recovery spool).
        IReadOnlyList<string> rescue;
        if (!spec.Certain)
        {
            rescue = scope.Session.DrainAllProvisional();
        }
        else
        {
            DirtyContentSet baseDirty = DecodeDirty(spec.BaseDirtyBase64);
            var segmentDirties = new DirtyContentSet[spec.SegmentDirtiesBase64.Length];
            for (int i = 0; i < segmentDirties.Length; i++)
                segmentDirties[i] = DecodeDirty(spec.SegmentDirtiesBase64[i]);
            rescue = scope.Session.ReconcileAtB1(baseDirty, segmentDirties);
        }

        var result = new IndexQueryReconcileResult
        {
            SessionId = spec.SessionId,
            RescuePathsBase64 = IndexQueryWorkerProtocol.EncodePaths(rescue),
            PruningCertain = spec.Certain,
        };
        return new IndexWorkerMessage
        {
            Type = IndexWorkerProtocol.MessageTypes.Result,
            Id = request.Id,
            Ok = true,
            QueryResultJson = JsonSerializer.Serialize(result, IndexQueryJsonContext.Default.IndexQueryReconcileResult),
        };
    }

    private static IndexWorkerMessage Cancel(IndexWorkerRequest request)
    {
        IndexQueryClassifyRequest spec = JsonSerializer.Deserialize(request.QueryJson ?? "", IndexQueryJsonContext.Default.IndexQueryClassifyRequest)
            ?? throw new InvalidDataException("cancelSession payload was empty");
        // Worker-acknowledged cancellation: drop the session + its mappings now, then ack (Ok even if it was
        // already gone — the host only needs to know it no longer exists).
        bool removed = Sessions.TryRemove(spec.SessionId, out PinnedScope? scope);
        if (removed)
            scope.Dispose();
        Log($"query-session cancel id={spec.SessionId} removed={removed} active={Sessions.Count}");
        return new IndexWorkerMessage { Type = IndexWorkerProtocol.MessageTypes.Result, Id = request.Id, Ok = true };
    }

    private static IndexWorkerMessage Close(IndexWorkerRequest request)
    {
        IndexQueryClassifyRequest spec = JsonSerializer.Deserialize(request.QueryJson ?? "", IndexQueryJsonContext.Default.IndexQueryClassifyRequest)
            ?? throw new InvalidDataException("closeQueryScope payload was empty");
        bool removed = Sessions.TryRemove(spec.SessionId, out PinnedScope? scope);
        if (removed)
            scope.Dispose();
        Log($"query-session close id={spec.SessionId} removed={removed} active={Sessions.Count}");
        return new IndexWorkerMessage { Type = IndexWorkerProtocol.MessageTypes.Result, Id = request.Id, Ok = true };
    }

    private static IndexWorkerMessage OpenResult(
        int id,
        bool accelerable,
        int candidateCount,
        string? bypassReason,
        IndexQueryOpenDiagnostics? diagnostics = null)
    {
        var result = new IndexQueryOpenResult
        {
            Accelerable = accelerable,
            CandidateCount = candidateCount,
            BypassReason = bypassReason,
            Diagnostics = diagnostics,
        };
        return new IndexWorkerMessage
        {
            Type = IndexWorkerProtocol.MessageTypes.Result,
            Id = id,
            Ok = true,
            QueryResultJson = JsonSerializer.Serialize(result, IndexQueryJsonContext.Default.IndexQueryOpenResult),
        };
    }

    private static IReadOnlySet<int> DecodeCandidateSet(string? base64)
        => new HashSet<int>(IndexWorkerProtocol.DecodeCandidates(base64));

    private static DirtyContentSet DecodeDirty(string? base64)
    {
        var dirty = new DirtyContentSet();
        foreach (int id in IndexWorkerProtocol.DecodeCandidates(base64))
            dirty.MarkDirty(id);
        return dirty;
    }

    private static string Index(string[] array, int i) => i >= 0 && i < array.Length ? array[i] : "";

    private static void DisposeAll(ContentIndexV3Reader? baseReader, List<ContentIndexV3Reader> segmentReaders)
    {
        baseReader?.Dispose();
        foreach (ContentIndexV3Reader reader in segmentReaders)
            reader.Dispose();
    }

    private static IndexWorkerMessage Fail(int id, string error) => new()
    {
        Type = IndexWorkerProtocol.MessageTypes.Result,
        Id = id,
        Ok = false,
        Error = error,
    };

    private static void Log(string message) => Console.Error.WriteLine("[indexworker] " + message);
}
