using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Yagu.Services.Index;

/// <summary>
/// The <b>query-session</b> sub-protocol carried inside the existing line-JSON control protocol (plan §5.2,
/// Stage 2 shadow mode). The host opens a per-scope query session in the worker, which memory-maps the
/// scope's format-v3 structures (base + segments) and pins them; the host then streams batches of discovered
/// normalized paths and the worker returns a per-path classification verdict (it <b>never prunes</b> in
/// shadow mode — it only reports what it would classify, so the host can compare against the in-process
/// oracle and measure the worker's paged footprint). The op payloads ride as a single JSON string on
/// <see cref="IndexWorkerRequest.QueryJson"/> / <see cref="IndexWorkerMessage.QueryResultJson"/> so the flat
/// control DTOs stay AOT-simple.
/// <para>
/// Stage 2 keeps the existing line-JSON transport; the framed binary transport, cancellation, bounded
/// channels, and the recovery spool are Stage 3.
/// </para>
/// </summary>
internal static class IndexQueryWorkerProtocol
{
    /// <summary>Per-path classification verdict codes (one byte per path in a <see cref="Ops.ClassifyPaths"/>
    /// reply). They mirror the closed <see cref="IndexPathClassification"/> cases the mapped classifier can
    /// produce; only <see cref="Nonmember"/> is the prunable kind.</summary>
    internal static class Verdicts
    {
        public const byte Unindexed = 0;     // absent from every layer, or tombstoned
        public const byte DirtyByUsn = 1;    // changed since build / no captured identity → live-scan
        public const byte Member = 2;        // fresh posting member → live-scan (index-accelerated provenance)
        public const byte Nonmember = 3;     // fresh posting NONMEMBER → the only prunable kind
    }

    /// <summary>Maps an <see cref="IndexPathClassification"/> to its wire verdict byte. The mapped classifier
    /// only ever produces Member/Nonmember/DirtyByUsn/Unindexed, so this is total for worker output; the host
    /// maps its oracle's classification identically for the shadow comparison.</summary>
    public static byte VerdictFor(IndexPathClassification classification) => classification switch
    {
        IndexPathClassification.FreshIndexedMember => Verdicts.Member,
        IndexPathClassification.FreshIndexedNonmember => Verdicts.Nonmember,
        IndexPathClassification.DirtyByUsn => Verdicts.DirtyByUsn,
        _ => Verdicts.Unindexed, // Unindexed / SpecialSource / UntrustedRoot all live-scan and never prune
    };

    /// <summary>Encodes a batch of normalized paths as Base64 of their newline-joined UTF-8 bytes (normalized
    /// paths never contain a newline, so this is unambiguous).</summary>
    public static string EncodePaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(paths[i]);
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Decodes the wire form produced by <see cref="EncodePaths"/>.</summary>
    public static string[] DecodePaths(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return Array.Empty<string>();
        string joined = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return joined.Split('\n');
    }

    /// <summary>Encodes per-path verdict bytes as Base64.</summary>
    public static string EncodeVerdicts(ReadOnlySpan<byte> verdicts)
        => verdicts.Length == 0 ? string.Empty : Convert.ToBase64String(verdicts);

    /// <summary>Decodes the wire form produced by <see cref="EncodeVerdicts"/>.</summary>
    public static byte[] DecodeVerdicts(string? base64)
        => string.IsNullOrEmpty(base64) ? Array.Empty<byte>() : Convert.FromBase64String(base64);

    /// <summary>Encodes a dirty content-id set (as an <c>i32</c> array — content ids live in the on-disk u32
    /// id space) using the shared little-endian candidate encoding.</summary>
    public static string EncodeContentIds(IReadOnlySet<long> contentIds)
    {
        ArgumentNullException.ThrowIfNull(contentIds);
        if (contentIds.Count == 0)
            return string.Empty;
        var buffer = new int[contentIds.Count];
        int i = 0;
        foreach (long id in contentIds)
            buffer[i++] = (int)id;
        return IndexWorkerProtocol.EncodeCandidates(buffer);
    }
}

/// <summary>Host → worker payload for <see cref="IndexWorkerProtocol.Ops.OpenQueryScope"/>. Identifies the
/// scope's base + segment generation directories and carries the per-layer candidate content-id sets and B0
/// dirty content-id sets so the worker's classification uses the exact same inputs as the in-process oracle
/// (a deterministic shadow comparison). Candidate/dirty arrays are Base64 of little-endian <c>i32</c>s;
/// <see cref="SegmentDirs"/> are oldest → newest, and the segment arrays are 1:1 with them.</summary>
internal sealed class IndexQueryOpenRequest
{
    public int SessionId { get; set; }
    /// <summary>Maximum worker lanes used to evaluate mapped layers and classify one path batch. The
    /// worker clamps this to its supported range. One preserves the legacy serialized behavior.</summary>
    public int Parallelism { get; set; } = 1;
    public string BaseDir { get; set; } = "";
    public string[] SegmentDirs { get; set; } = Array.Empty<string>();
    public string BaseCandidatesBase64 { get; set; } = "";
    public string[] SegmentCandidatesBase64 { get; set; } = Array.Empty<string>();
    public string BaseDirtyBase64 { get; set; } = "";
    public string[] SegmentDirtiesBase64 { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional Base64 of the RPN trigram query. When <see cref="BaseCandidatesBase64"/> is empty the worker
    /// evaluates the candidate set for every layer itself, over its memory-mapped v3 postings — so a
    /// large-scope query needs no host-computed candidates (the host never holds the index). When candidates
    /// are supplied they take precedence (a deterministic shadow comparison). At least one must be present.
    /// </summary>
    public string? QueryRpnBase64 { get; set; }

    /// <summary>
    /// When true the session tracks a <b>provisional prune set</b> (plan §6 Stage 4): each
    /// <see cref="IndexWorkerProtocol.Ops.ClassifyPaths"/> routes a fresh posting nonmember for pruning
    /// (records it, keyed by its path) so it can be reconciled at barrier B1 via
    /// <see cref="IndexWorkerProtocol.Ops.ReconcileB1"/>. When false (the Stage-2 shadow default) classify is
    /// a pure, side-effect-free classifier that tracks nothing.
    /// </summary>
    public bool PruningEnabled { get; set; }
}

/// <summary>Local-only phase timings and structural fragmentation measurements collected while opening a
/// mapped query session. These values are logged and benchmarked; they are never sent through telemetry.</summary>
internal sealed class IndexQueryOpenDiagnostics
{
    public int LayerCount { get; set; }
    public long PathRecordCount { get; set; }
    public long TombstoneRecordCount { get; set; }
    public int DistinctRouteHashCount { get; set; }
    public bool CandidatesEvaluatedInWorker { get; set; }
    public double MapOpenMs { get; set; }
    public double CandidateEvaluationMs { get; set; }
    public double RoutingIndexMs { get; set; }
    public double WorkerOpenMs { get; set; }

    /// <summary>Host-observed request/response time, including process startup or IPC queueing. Set after
    /// deserialization and deliberately omitted from the worker's JSON payload.</summary>
    [JsonIgnore]
    public double HostRoundTripMs { get; set; }

    [JsonIgnore]
    public long RouteRecordCount => PathRecordCount + TombstoneRecordCount;

    [JsonIgnore]
    public long SupersededRouteRecordCount => Math.Max(0, RouteRecordCount - DistinctRouteHashCount);

    [JsonIgnore]
    public double RouteRecordAmplification => DistinctRouteHashCount <= 0
        ? (RouteRecordCount == 0 ? 1 : double.PositiveInfinity)
        : RouteRecordCount / (double)DistinctRouteHashCount;
}

/// <summary>Worker → host result for <see cref="IndexWorkerProtocol.Ops.OpenQueryScope"/>.</summary>
internal sealed class IndexQueryOpenResult
{
    /// <summary>True when the worker mapped every layer's v3 (with a tombstone index) and pinned a query
    /// session; false means the scope is not mapped-queryable and the host must live-scan (fall back).</summary>
    public bool Accelerable { get; set; }
    public int CandidateCount { get; set; }
    public string? BypassReason { get; set; }
    public IndexQueryOpenDiagnostics? Diagnostics { get; set; }
}

/// <summary>Host → worker payload for <see cref="IndexWorkerProtocol.Ops.ClassifyPaths"/>: a batch of
/// normalized paths (Base64, newline-joined) to classify against a pinned session. The framing fields
/// (plan §5.2) let the worker reject a stale batch and let the host route/validate the reply: <see cref="Epoch"/>
/// is the worker generation the host believes it is talking to, <see cref="BatchSeq"/> is the per-session
/// batch sequence (echoed in the reply), and <see cref="DeadlineUnixMs"/> (0 = none) lets the worker skip a
/// batch whose deadline already elapsed while it was queued.</summary>
internal sealed class IndexQueryClassifyRequest
{
    public int SessionId { get; set; }
    public int Epoch { get; set; }
    public long BatchSeq { get; set; }
    public long DeadlineUnixMs { get; set; }
    public string PathsBase64 { get; set; } = "";
}

/// <summary>Worker → host result for <see cref="IndexWorkerProtocol.Ops.ClassifyPaths"/>: one verdict byte
/// per requested path (see <see cref="IndexQueryWorkerProtocol.Verdicts"/>). Echoes <see cref="SessionId"/>
/// and <see cref="BatchSeq"/> (with the worker epoch on the enclosing message) so the host's
/// <see cref="QueryReplyGate"/> can drop a reply routed to the wrong session/batch/worker generation.</summary>
internal sealed class IndexQueryClassifyResult
{
    public int SessionId { get; set; }
    public long BatchSeq { get; set; }
    public string VerdictsBase64 { get; set; } = "";
}

/// <summary>Host → worker payload for <see cref="IndexWorkerProtocol.Ops.ReconcileB1"/> (plan §5.5): each
/// layer's <c>[B0, B1)</c> dirty content-id set (Base64 of little-endian <c>i32</c>s, the base plus one per
/// segment oldest → newest, matching <see cref="IndexQueryOpenRequest.SegmentDirtiesBase64"/>). When
/// <see cref="Certain"/> is false the host's B1 journal replay was discontinuous/uncertain, so the worker
/// rescues <b>every</b> remaining provisional path (the dirty fields are ignored). <see cref="Epoch"/> is the
/// worker generation the host believes it is talking to, so a reply from a restarted worker is dropped.</summary>
internal sealed class IndexQueryReconcileRequest
{
    public int SessionId { get; set; }
    public int Epoch { get; set; }
    public bool Certain { get; set; }
    public string BaseDirtyBase64 { get; set; } = "";
    public string[] SegmentDirtiesBase64 { get; set; } = Array.Empty<string>();
}

/// <summary>Worker → host result for <see cref="IndexWorkerProtocol.Ops.ReconcileB1"/>: the provisional
/// paths that must now be live-scanned (Base64, newline-joined, as <see cref="IndexQueryWorkerProtocol.EncodePaths"/>)
/// and whether the reconciliation was <see cref="PruningCertain"/> (false ⇒ every prune was rescued — the
/// host must treat the scope as unaccelerated for net-pruning accounting). Echoes <see cref="SessionId"/> so
/// the host can drop a reply routed to the wrong session.</summary>
internal sealed class IndexQueryReconcileResult
{
    public int SessionId { get; set; }
    public string RescuePathsBase64 { get; set; } = "";
    public bool PruningCertain { get; set; }
}

/// <summary>Source-gen JSON context for the query-session payload DTOs (AOT-safe, converter-free).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IndexQueryOpenRequest))]
[JsonSerializable(typeof(IndexQueryOpenDiagnostics))]
[JsonSerializable(typeof(IndexQueryOpenResult))]
[JsonSerializable(typeof(IndexQueryClassifyRequest))]
[JsonSerializable(typeof(IndexQueryClassifyResult))]
[JsonSerializable(typeof(IndexQueryReconcileRequest))]
[JsonSerializable(typeof(IndexQueryReconcileResult))]
internal sealed partial class IndexQueryJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
