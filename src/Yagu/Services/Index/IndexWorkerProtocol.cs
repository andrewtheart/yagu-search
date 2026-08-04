using System;
using System.Text.Json.Serialization;

namespace Yagu.Services.Index;

/// <summary>
/// Line-delimited-JSON control protocol shared by the in-app <see cref="IndexWorkerClient"/> proxy and the
/// out-of-process <c>Yagu.IndexWorker.exe</c> host. One JSON object per line over the worker's stdin
/// (requests) and stdout (messages); stderr is diagnostics only.
/// <para>
/// This file is compiled into BOTH the main app (Native AOT) and the worker (non-AOT) via a shared
/// <c>&lt;Compile Include&gt;</c>, so both sides always agree on the wire shape. The DTOs are deliberately
/// flat (only strings / numbers / bools) so the source-gen JSON context stays trivial and AOT-safe; the
/// variable-length primitive payloads (extracted trigram <c>u32</c> arrays and candidate <c>i32</c> arrays)
/// ride as Base64 of their little-endian bytes rather than as JSON arrays, keeping the DTOs flat and the
/// serializer converter-free.
/// </para>
/// <para>
/// The <b>native</b> index engine (<c>yagu_core.dll</c> trigram extraction / posting query, which can
/// fail-fast the host on a read-time / native access violation) is loaded ONLY by the worker. The app never
/// P/Invokes it, so a native fault is contained in the worker process — the app just sees the worker exit and
/// degrades gracefully.
/// </para>
/// </summary>
internal static class IndexWorkerProtocol
{
    /// <summary>Request op names (host → worker).</summary>
    internal static class Ops
    {
        /// <summary>Liveness probe; the worker replies with a <see cref="MessageTypes.Result"/> and <c>ok=true</c>.</summary>
        public const string Ping = "ping";

        /// <summary>Classify a file and return its sorted-distinct trigram set (mirrors the managed
        /// <c>ContentRepresentation.Classify</c> golden reference, but executed by the native engine).</summary>
        public const string Extract = "extract";

        /// <summary>Verify + query a serialized <c>content.bin</c> generation with an RPN trigram query and
        /// return the candidate document-id set.</summary>
        public const string QueryContentBin = "queryContentBin";

        public const string BuildScope = "buildScope";
        public const string RefreshAuto = "refreshAuto";
        public const string ValidateScope = "validateScope";
        public const string CancelBuild = "cancelBuild";

        /// <summary>Open a per-scope mapped query session (plan §5.2): the worker memory-maps the scope's
        /// format-v3 base + segments and pins them, then classifies batches against them (never prunes).</summary>
        public const string OpenQueryScope = "openQueryScope";

        /// <summary>Classify a batch of discovered normalized paths against a pinned query session; the reply
        /// carries one verdict byte per path.</summary>
        public const string ClassifyPaths = "classifyPaths";

        /// <summary>Release a pinned query session and its mapped views.</summary>
        public const string CloseQueryScope = "closeQueryScope";

        /// <summary>Worker-acknowledged cancellation of a pinned query session (plan §5.2): drop the session
        /// and its mappings NOW (abandon in-flight batches for it) and reply with an ack, so the host knows the
        /// session is gone rather than best-effort-forgetting it as <see cref="CloseQueryScope"/> does.</summary>
        public const string CancelSession = "cancelSession";

        /// <summary>Reconcile a pinned <b>pruning</b> query session at barrier B1 (plan §5.2/§5.5). Given each
        /// layer's <c>[B0, B1)</c> dirty content-id set — or a not-certain flag when the host's journal replay
        /// was discontinuous — the worker returns the provisionally-pruned paths that must now be live-scanned
        /// after all (every remaining prune when not certain). Meaningful only for a session opened with
        /// <see cref="IndexQueryOpenRequest.PruningEnabled"/>.</summary>
        public const string ReconcileB1 = "reconcileB1";

        /// <summary>Ask the worker to exit cleanly. The worker also exits on stdin EOF.</summary>
        public const string Shutdown = "shutdown";
    }

    /// <summary>Message type names (worker → host).</summary>
    internal static class MessageTypes
    {
        /// <summary>Emitted once at startup after the native engine loads and the ABI check passes.</summary>
        public const string Ready = "ready";

        public const string Accepted = "accepted";

        public const string Progress = "progress";

        /// <summary>Terminal reply for a request <c>id</c> (carries <c>ok</c> + the op's payload / <c>error</c>).</summary>
        public const string Result = "result";

        /// <summary>Fatal initialization error (carries <c>stage</c> + <c>error</c>); the worker then exits.</summary>
        public const string Error = "error";
    }

    /// <summary>The index FFI ABI version the worker requires from <c>yagu_core.dll</c>. This is the
    /// dedicated <c>qg_index_abi_version()</c> value — decoupled from the search <c>qg_abi_version</c> so the
    /// index protocol can evolve without touching the native search hot path.</summary>
    public const int RequiredIndexAbiVersion = 1;

    /// <summary>Managed control-protocol version, independent of the optional native query ABI.</summary>
    public const int ControlProtocolVersion = 3;

    internal static class OutcomeKinds
    {
        public const string Ok = "ok";
        public const string Cancelled = "cancelled";
        public const string DiskFull = "diskFull";
        public const string DirectoryNotFound = "dirNotFound";
        public const string Busy = "busy";
        public const string Error = "error";
    }

    /// <summary>Encodes a <c>u32</c> trigram array as Base64 of its little-endian bytes (wire form for the
    /// <see cref="Ops.Extract"/> reply).</summary>
    public static string EncodeTrigrams(ReadOnlySpan<uint> trigrams)
    {
        byte[] bytes = new byte[trigrams.Length * sizeof(uint)];
        for (int i = 0; i < trigrams.Length; i++)
        {
            BitWrite(bytes, i * sizeof(uint), trigrams[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    /// <summary>Decodes the Base64 wire form produced by <see cref="EncodeTrigrams"/> back into a
    /// <c>u32</c> array. Returns an empty array for null/empty input; throws <see cref="FormatException"/>
    /// on a malformed length (not a multiple of 4 bytes).</summary>
    public static uint[] DecodeTrigrams(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return Array.Empty<uint>();
        }

        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(uint) != 0)
        {
            throw new FormatException("Trigram payload length is not a multiple of 4.");
        }

        uint[] result = new uint[bytes.Length / sizeof(uint)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (uint)(bytes[(i * 4) + 0]
                | (bytes[(i * 4) + 1] << 8)
                | (bytes[(i * 4) + 2] << 16)
                | (bytes[(i * 4) + 3] << 24));
        }

        return result;
    }

    /// <summary>Encodes an <c>i32</c> candidate-document-id array as Base64 of its little-endian bytes (wire
    /// form for the <see cref="Ops.QueryContentBin"/> reply).</summary>
    public static string EncodeCandidates(ReadOnlySpan<int> candidates)
    {
        byte[] bytes = new byte[candidates.Length * sizeof(int)];
        for (int i = 0; i < candidates.Length; i++)
        {
            BitWrite(bytes, i * sizeof(int), unchecked((uint)candidates[i]));
        }

        return Convert.ToBase64String(bytes);
    }

    /// <summary>Decodes the Base64 wire form produced by <see cref="EncodeCandidates"/> back into an
    /// <c>i32</c> array. Returns an empty array for null/empty input; throws <see cref="FormatException"/>
    /// on a malformed length (not a multiple of 4 bytes).</summary>
    public static int[] DecodeCandidates(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return Array.Empty<int>();
        }

        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(int) != 0)
        {
            throw new FormatException("Candidate payload length is not a multiple of 4.");
        }

        int[] result = new int[bytes.Length / sizeof(int)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = unchecked(bytes[(i * 4) + 0]
                | (bytes[(i * 4) + 1] << 8)
                | (bytes[(i * 4) + 2] << 16)
                | (bytes[(i * 4) + 3] << 24));
        }

        return result;
    }

    private static void BitWrite(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

/// <summary>A single host → worker request line.</summary>
internal sealed class IndexWorkerRequest
{
    /// <summary>One of <see cref="IndexWorkerProtocol.Ops"/>.</summary>
    public string Op { get; set; } = "";

    /// <summary>Correlates a request with its terminal <see cref="IndexWorkerMessage"/>. The
    /// <see cref="IndexWorkerProtocol.Ops.Shutdown"/> op needs no reply and uses 0.</summary>
    public int Id { get; set; }

    /// <summary>The file to classify (<see cref="IndexWorkerProtocol.Ops.Extract"/>) or the
    /// <c>content.bin</c> to query (<see cref="IndexWorkerProtocol.Ops.QueryContentBin"/>).</summary>
    public string? Path { get; set; }

    /// <summary>Base64 of the RPN trigram-query bytes (<see cref="IndexWorkerProtocol.Ops.QueryContentBin"/>).</summary>
    public string? QueryRpnBase64 { get; set; }

    /// <summary>Versioned JSON snapshot for build/maintenance/validation operations.</summary>
    public string? OperationJson { get; set; }

    /// <summary>JSON payload for the query-session ops (<see cref="IndexWorkerProtocol.Ops.OpenQueryScope"/> /
    /// <see cref="IndexWorkerProtocol.Ops.ClassifyPaths"/> / <see cref="IndexWorkerProtocol.Ops.CloseQueryScope"/>).</summary>
    public string? QueryJson { get; set; }
}

/// <summary>A single worker → host message line.</summary>
internal sealed class IndexWorkerMessage
{
    /// <summary>One of <see cref="IndexWorkerProtocol.MessageTypes"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>The request id this message belongs to (0 for <c>ready</c>/<c>error</c>).</summary>
    public int Id { get; set; }

    /// <summary>The worker's generation/epoch (plan §5.2): a per-worker-process value stamped on the
    /// <c>ready</c> handshake and on every query reply. The host uses it (via <c>QueryReplyGate</c>) to drop a
    /// reply from a restarted worker so it is never misapplied to a new session's batch. 0 on non-query replies.</summary>
    public int Epoch { get; set; }

    /// <summary>Terminal-message success flag (whether the worker handled the request without faulting).</summary>
    public bool Ok { get; set; }

    /// <summary>Error text when <see cref="Ok"/> is false, or the init-stage name on an <c>error</c> message.</summary>
    public string? Error { get; set; }

    /// <summary>The init stage on an <c>error</c> message (e.g. <c>"init"</c>).</summary>
    public string? Stage { get; set; }

    /// <summary>The content verdict for an <see cref="IndexWorkerProtocol.Ops.Extract"/> reply
    /// (0 = indexed, 1 = binary, 2 = not BOM-less UTF-8), matching the managed <c>ContentRepresentationVerdict</c>.</summary>
    public int Verdict { get; set; }

    /// <summary>Base64 of the sorted-distinct trigram <c>u32</c> array (<see cref="IndexWorkerProtocol.Ops.Extract"/>).</summary>
    public string? TrigramsBase64 { get; set; }

    /// <summary>Base64 of the candidate document-id <c>i32</c> array (<see cref="IndexWorkerProtocol.Ops.QueryContentBin"/>).</summary>
    public string? CandidatesBase64 { get; set; }

    public int ControlProtocolVersion { get; set; }
    public string? OutcomeKind { get; set; }
    public long BytesCrawled { get; set; }
    public long FilesCrawled { get; set; }
    public int Percent { get; set; } = -1;
    public string? ProgressRoot { get; set; }
    public string? ProgressStage { get; set; }
    public string? ScopeId { get; set; }
    public int IndexedCount { get; set; }
    public int SkippedCount { get; set; }
    public string? Summary { get; set; }
    public int Built { get; set; }
    public int SkippedRoots { get; set; }
    public int Failed { get; set; }
    public string? DriveName { get; set; }
    public double UsedPercent { get; set; }
    public int ThresholdPercent { get; set; }
    public string? PdfStatus { get; set; }
    public int PdfsSeen { get; set; }
    public int PdfAdmitted { get; set; }
    public string? PdfDeterminism { get; set; }
    public string? ImageOcrStatus { get; set; }
    public int ImagesSeen { get; set; }
    public int ImagesAdmitted { get; set; }
    public int ImagesFailed { get; set; }
    public bool PostBuildCatchUpChecked { get; set; }
    public int PostBuildCatchUpThresholdChanges { get; set; }
    public string? PostBuildCatchUpOutcome { get; set; }
    public int PostBuildCatchUpJournalChangeCount { get; set; }
    public bool PostBuildCatchUpChangeCountComplete { get; set; }
    public bool PostBuildCatchUpThresholdExceeded { get; set; }
    public string? ActiveBaseGenerationId { get; set; }
    public long ActivePointerSequence { get; set; }
    public string? LastPublishedArtifactId { get; set; }
    public string? MaintenanceResultJson { get; set; }
    public bool Valid { get; set; }
    public string? FailureReason { get; set; }
    public int DocumentCount { get; set; }
    public int SegmentCount { get; set; }
    public string? RootPath { get; set; }

    /// <summary>JSON payload for a query-session op reply (<see cref="IndexWorkerProtocol.Ops.OpenQueryScope"/> /
    /// <see cref="IndexWorkerProtocol.Ops.ClassifyPaths"/>).</summary>
    public string? QueryResultJson { get; set; }
}

/// <summary>Source-gen JSON context for the flat protocol DTOs (kept minimal and converter-free so it is
/// AOT-safe in the main app).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IndexWorkerRequest))]
[JsonSerializable(typeof(IndexWorkerMessage))]
internal sealed partial class IndexWorkerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
