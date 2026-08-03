using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>Terminal result of an <see cref="IndexWorkerClient.ExtractAsync"/> call.</summary>
internal readonly record struct IndexWorkerExtractResult(bool Success, string? Error, int Verdict, uint[] Trigrams)
{
    public static IndexWorkerExtractResult Fail(string error) => new(false, error, 0, Array.Empty<uint>());
}

/// <summary>Terminal result of an <see cref="IndexWorkerClient.QueryContentBinAsync"/> call.</summary>
internal readonly record struct IndexWorkerQueryResult(bool Success, string? Error, int[] Candidates)
{
    public static IndexWorkerQueryResult Fail(string error) => new(false, error, Array.Empty<int>());
}

/// <summary>Terminal result of an <see cref="IndexWorkerClient.ReconcileB1Async"/> call (plan §5.5): the
/// provisional paths that must now be live-scanned and whether the B1 reconciliation was certain (false ⇒
/// every prune was rescued, so the host must treat the scope as unaccelerated for net-pruning accounting).
/// <see cref="Success"/> is false on any worker failure — the host then replays its recovery spool so no
/// pruned path is ever lost.</summary>
internal readonly record struct IndexWorkerReconcileResult(bool Success, IReadOnlyList<string> RescuePaths, bool PruningCertain)
{
    public static IndexWorkerReconcileResult Fail() => new(false, Array.Empty<string>(), false);
}

/// <summary>
/// In-app proxy for the out-of-process <c>Yagu.IndexWorker.exe</c> host. Pure managed (no native
/// dependencies), so it is safe to compile into the Native-AOT app and link into the test project — all the
/// native index work lives in the worker. Launches the worker under a kill-on-close
/// <see cref="WindowsJobObject"/> so it can never outlive the app, verifies the worker's Authenticode
/// signature in signed builds, and exchanges line-delimited JSON over stdin/stdout. Degrades gracefully
/// (failure results, never throws to the caller) when the worker is missing, untrusted, or crashes.
/// </summary>
internal sealed class IndexWorkerClient : IDisposable
{
    /// <summary>Environment variable that overrides the worker executable path (used by tests/dev). When set,
    /// the Authenticode trust gate is skipped (the override is an internal test/dev seam only).</summary>
    public const string WorkerPathEnvVar = "YAGU_INDEX_WORKER";

    private const string LogSource = "IndexWorker";

    // BOM-less UTF-8 for the worker's stdin: Encoding.UTF8 emits a 3-byte BOM on the first write, which would
    // corrupt the first JSON request line and hang the worker's deserializer.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static int _missingWorkerLogged;

    private readonly string? _workerPathOverride;
    private readonly bool _hasWorkerPathOverride;
    private readonly Func<ProcessStartInfo, IIndexWorkerProcess> _processFactory;
    private readonly IndexWorkerTrustVerifier _trustVerifier;
    private readonly TimeSpan _readyTimeout;
    private readonly Action<WindowsJobObject, nint> _jobAssigner;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IndexWorkerMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TaskCompletionSource<IndexWorkerMessage>? _readyTcs;

    private Task<bool>? _initTask;
    private WindowsJobObject? _job;
    private IIndexWorkerProcess? _process;
    private TextWriter? _stdin;
    private volatile bool _disposed;
    private int _nextId;
    private int _sessionId;
    // The worker generation reported on the ready handshake (plan §5.2); stamped onto classify requests and
    // validated against each reply's echoed epoch so a reply from a restarted worker is dropped.
    private int _workerEpoch;

    public IndexWorkerClient()
        : this(
            workerPathOverride: null,
            hasWorkerPathOverride: false,
            IndexWorkerProcessFactory.Create,
            AuthenticodeVerifier.IsWorkerTrustedForHost,
            TimeSpan.FromSeconds(30),
            static (job, handle) => job.Assign(handle))
    {
    }

    /// <summary>Test/diagnostics constructor: forces the worker path (authoritative — if the file is missing
    /// the client reports the worker unavailable instead of probing the install directory).</summary>
    internal IndexWorkerClient(string? workerPathOverride)
        : this(
            workerPathOverride,
            hasWorkerPathOverride: true,
            IndexWorkerProcessFactory.Create,
            AuthenticodeVerifier.IsWorkerTrustedForHost,
            TimeSpan.FromSeconds(30),
            static (job, handle) => job.Assign(handle))
    {
    }

    internal IndexWorkerClient(
        string? workerPathOverride,
        bool hasWorkerPathOverride,
        Func<ProcessStartInfo, IIndexWorkerProcess> processFactory,
        IndexWorkerTrustVerifier trustVerifier,
        TimeSpan readyTimeout,
        Action<WindowsJobObject, nint> jobAssigner)
    {
        _workerPathOverride = workerPathOverride;
        _hasWorkerPathOverride = hasWorkerPathOverride;
        _processFactory = processFactory;
        _trustVerifier = trustVerifier;
        _readyTimeout = readyTimeout;
        _jobAssigner = jobAssigner;
    }

    /// <summary>Starts the worker (single-flight) and waits for its <c>ready</c> handshake. Returns false when
    /// the worker is unavailable / untrusted / failed to initialize.</summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        Task<bool> init;
        lock (_gate)
        {
            if (_initTask is null || _initTask.IsFaulted || _initTask.IsCanceled
                || (_initTask.IsCompletedSuccessfully && !_initTask.Result))
            {
                CleanupProcessUnderGate();
                _readyTcs = new TaskCompletionSource<IndexWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _initTask = InitializeAsync();
            }
            init = _initTask;
        }

        try
        {
            bool ready = await init.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_initTask, init))
                        _initTask = null;
                }
            }
            return ready;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For(LogSource).LogWarning("index worker initialization failed: {Error}", ex.Message);
            lock (_gate)
            {
                if (ReferenceEquals(_initTask, init))
                    _initTask = null;
            }
            return false;
        }
    }

    /// <summary>Classifies a file via the worker and returns its verdict + sorted-distinct trigram set.</summary>
    public async Task<IndexWorkerExtractResult> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        IndexWorkerMessage? reply = await SendAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.Extract, Path = path },
            cancellationToken).ConfigureAwait(false);

        if (reply is null)
        {
            return IndexWorkerExtractResult.Fail("index worker is not running.");
        }

        if (!reply.Ok)
        {
            return IndexWorkerExtractResult.Fail(reply.Error ?? "extract failed");
        }

        return new IndexWorkerExtractResult(true, null, reply.Verdict, IndexWorkerProtocol.DecodeTrigrams(reply.TrigramsBase64));
    }

    /// <summary>Verifies + queries a serialized <c>content.bin</c> with RPN <paramref name="queryRpn"/>.</summary>
    public async Task<IndexWorkerQueryResult> QueryContentBinAsync(string contentBinPath, ReadOnlyMemory<byte> queryRpn, CancellationToken cancellationToken)
    {
        string rpnBase64 = queryRpn.Length == 0 ? string.Empty : Convert.ToBase64String(queryRpn.Span);
        IndexWorkerMessage? reply = await SendAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.QueryContentBin, Path = contentBinPath, QueryRpnBase64 = rpnBase64 },
            cancellationToken).ConfigureAwait(false);

        if (reply is null)
        {
            return IndexWorkerQueryResult.Fail("index worker is not running.");
        }

        if (!reply.Ok)
        {
            return IndexWorkerQueryResult.Fail(reply.Error ?? "query failed");
        }

        return new IndexWorkerQueryResult(true, null, IndexWorkerProtocol.DecodeCandidates(reply.CandidatesBase64));
    }

    /// <summary>Opens a pinned mapped query session in the worker for a scope (plan §5.2). Returns the
    /// worker's open result, or null when the worker is unavailable / failed (→ the host live-scans). An
    /// <see cref="IndexQueryOpenResult.Accelerable"/> of false means the scope is not mapped-queryable.</summary>
    public async Task<IndexQueryOpenResult?> OpenQueryScopeAsync(IndexQueryOpenRequest spec, CancellationToken cancellationToken)
    {
        var roundTripTimer = Stopwatch.StartNew();
        string json = JsonSerializer.Serialize(spec, IndexQueryJsonContext.Default.IndexQueryOpenRequest);
        IndexWorkerMessage? reply = await SendAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.OpenQueryScope, QueryJson = json }, cancellationToken).ConfigureAwait(false);
        if (reply is null || !reply.Ok)
        {
            return null;
        }
        IndexQueryOpenResult? result = DeserializeQueryResult(
            reply.QueryResultJson ?? "",
            IndexQueryJsonContext.Default.IndexQueryOpenResult);
        roundTripTimer.Stop();
        if (result?.Diagnostics is { } diagnostics)
            diagnostics.HostRoundTripMs = roundTripTimer.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Classifies a batch of normalized paths against a pinned query session; returns one verdict
    /// byte per path (see <see cref="IndexQueryWorkerProtocol.Verdicts"/>), or null on any worker failure. The
    /// framing args (plan §5.2) stamp the request with the worker <see cref="_workerEpoch"/> and a per-session
    /// <paramref name="batchSeq"/>, validate the echoed reply via <see cref="QueryReplyGate"/> (a mis-routed /
    /// stale reply → null → live-scan), and abandon the wait once <paramref name="deadlineUnixMs"/> (0 = none)
    /// elapses so a hung worker degrades to a live scan.</summary>
    public async Task<byte[]?> ClassifyPathsAsync(
        int sessionId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken,
        long batchSeq = 0,
        long deadlineUnixMs = 0)
    {
        int epoch = Volatile.Read(ref _workerEpoch);
        var spec = new IndexQueryClassifyRequest
        {
            SessionId = sessionId,
            Epoch = epoch,
            BatchSeq = batchSeq,
            DeadlineUnixMs = deadlineUnixMs,
            PathsBase64 = IndexQueryWorkerProtocol.EncodePaths(paths),
        };
        string json = JsonSerializer.Serialize(spec, IndexQueryJsonContext.Default.IndexQueryClassifyRequest);

        // Deadline abandonment: stop waiting for a hung worker once the batch deadline passes → live-scan.
        CancellationTokenSource? linked = null;
        CancellationToken effectiveToken = cancellationToken;
        if (deadlineUnixMs != 0)
        {
            long remainMs = deadlineUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remainMs <= 0)
                return null; // already past the deadline → abandon
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromMilliseconds(remainMs));
            effectiveToken = linked.Token;
        }

        IndexWorkerMessage? reply;
        try
        {
            reply = await SendAsync(
                new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.ClassifyPaths, QueryJson = json }, effectiveToken).ConfigureAwait(false);
        }
        finally
        {
            linked?.Dispose();
        }

        if (reply is null || !reply.Ok)
        {
            return null;
        }
        IndexQueryClassifyResult? result = DeserializeQueryResult(
            reply.QueryResultJson,
            IndexQueryJsonContext.Default.IndexQueryClassifyResult);
        if (result is null)
        {
            return null;
        }

        // Late/stale-reply guard: the id-correlated transport already routes the reply to this call, but
        // validate the echoed epoch / session / batch so a worker that ever misroutes a reply is caught and
        // that batch live-scans (never applies the wrong batch's verdicts).
        var replyHeader = new QueryFrameHeader(reply.Epoch, result.SessionId, result.BatchSeq, paths.Count, 0, 0);
        QueryReplyDisposition disposition = QueryReplyGate.Classify(epoch, sessionId, batchSeq, replyHeader);
        if (disposition != QueryReplyDisposition.Accept)
        {
            YaguLog.For(LogSource).LogWarning(
                "dropping classify reply for session {SessionId} batch {BatchSeq}: {Disposition}.", sessionId, batchSeq, disposition);
            return null;
        }

        return IndexQueryWorkerProtocol.DecodeVerdicts(result.VerdictsBase64);
    }

    /// <summary>Releases a pinned query session (best effort; a failure is harmless — the worker drops all
    /// sessions on exit).</summary>
    public async Task CloseQueryScopeAsync(int sessionId, CancellationToken cancellationToken)
    {
        var spec = new IndexQueryClassifyRequest { SessionId = sessionId };
        string json = JsonSerializer.Serialize(spec, IndexQueryJsonContext.Default.IndexQueryClassifyRequest);
        _ = await SendAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.CloseQueryScope, QueryJson = json }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Worker-acknowledged cancellation of a pinned query session (plan §5.2): asks the worker to
    /// drop the session and its mappings NOW and waits for its ack. Best effort — a failure is harmless (the
    /// worker drops all sessions on exit); used when a search is cancelled mid-flight.</summary>
    public async Task CancelSessionAsync(int sessionId, CancellationToken cancellationToken)
    {
        var spec = new IndexQueryClassifyRequest { SessionId = sessionId };
        string json = JsonSerializer.Serialize(spec, IndexQueryJsonContext.Default.IndexQueryClassifyRequest);
        _ = await SendAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.CancelSession, QueryJson = json }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles a pinned <b>pruning</b> query session at barrier B1 (plan §5.5). Sends each layer's
    /// <c>[B0, B1)</c> dirty content-id set, or — when <paramref name="certain"/> is false because the host's
    /// journal replay was discontinuous — asks the worker for a total rescue of every remaining prune. Returns
    /// the provisional paths that must now be live-scanned plus whether pruning stayed certain; a failed
    /// result (<see cref="IndexWorkerReconcileResult.Success"/> = false) means the host must replay its
    /// recovery spool so no pruned path is lost. The reply's epoch/session are validated so a reply from a
    /// restarted worker is dropped.</summary>
    public async Task<IndexWorkerReconcileResult> ReconcileB1Async(
        int sessionId,
        IReadOnlySet<long> baseDirty,
        IReadOnlyList<IReadOnlySet<long>> segmentDirties,
        bool certain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseDirty);
        ArgumentNullException.ThrowIfNull(segmentDirties);

        int epoch = Volatile.Read(ref _workerEpoch);
        string[] segDirty;
        if (certain)
        {
            segDirty = new string[segmentDirties.Count];
            for (int i = 0; i < segmentDirties.Count; i++)
                segDirty[i] = IndexQueryWorkerProtocol.EncodeContentIds(segmentDirties[i]);
        }
        else
        {
            segDirty = Array.Empty<string>();
        }

        var spec = new IndexQueryReconcileRequest
        {
            SessionId = sessionId,
            Epoch = epoch,
            Certain = certain,
            BaseDirtyBase64 = certain ? IndexQueryWorkerProtocol.EncodeContentIds(baseDirty) : "",
            SegmentDirtiesBase64 = segDirty,
        };
        string json = JsonSerializer.Serialize(spec, IndexQueryJsonContext.Default.IndexQueryReconcileRequest);

        IndexWorkerMessage? reply = await SendAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.ReconcileB1, QueryJson = json }, cancellationToken).ConfigureAwait(false);
        if (reply is null || !reply.Ok)
        {
            return IndexWorkerReconcileResult.Fail();
        }
        IndexQueryReconcileResult? result = DeserializeQueryResult(
            reply.QueryResultJson,
            IndexQueryJsonContext.Default.IndexQueryReconcileResult);
        if (result is null)
        {
            return IndexWorkerReconcileResult.Fail();
        }

        // Late/stale-reply guard: drop a reply for a stale epoch / wrong session (batch sequence is not part
        // of a reconcile, so pass 0 both ways).
        var replyHeader = new QueryFrameHeader(reply.Epoch, result.SessionId, 0, 0, 0, 0);
        QueryReplyDisposition disposition = QueryReplyGate.Classify(epoch, sessionId, 0, replyHeader);
        if (disposition != QueryReplyDisposition.Accept)
        {
            YaguLog.For(LogSource).LogWarning(
                "dropping reconcileB1 reply for session {SessionId}: {Disposition}.", sessionId, disposition);
            return IndexWorkerReconcileResult.Fail();
        }

        return new IndexWorkerReconcileResult(true, IndexQueryWorkerProtocol.DecodePaths(result.RescuePathsBase64), result.PruningCertain);
    }

    /// <summary>Test/benchmark-only: the worker process' peak working set in bytes (0 when no worker is
    /// running/measurable). The Stage-2 memory benchmark uses it to show the worker's resident set is paged
    /// (mapped-page working set + a fresh-.NET baseline), not an ~8x in-process deserialize of the index.</summary>
    internal long WorkerPeakWorkingSetBytes
    {
        get
        {
            IIndexWorkerProcess? process = _process;
            if (process is null)
                return 0;
            try { process.Refresh(); return process.HasExited ? 0 : process.PeakWorkingSetBytes; }
            catch { return 0; }
        }
    }

    private async Task<IndexWorkerMessage?> SendAsync(IndexWorkerRequest request, CancellationToken cancellationToken)
    {
        if (!await EnsureReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        IIndexWorkerProcess? process = _process;
        TextWriter? stdin = _stdin;
        if (process is null || stdin is null || process.HasExited)
        {
            return null;
        }

        int id = Interlocked.Increment(ref _nextId);
        request.Id = id;
        var tcs = new TaskCompletionSource<IndexWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        string line = JsonSerializer.Serialize(request, IndexWorkerJsonContext.Default.IndexWorkerRequest);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            YaguLog.For(LogSource).LogDebug("failed to send request: {Error}", ex.Message);
            return null;
        }
        finally
        {
            _writeLock.Release();
        }

        await using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            (ConcurrentDictionary<int, TaskCompletionSource<IndexWorkerMessage>> pending, int requestId, CancellationToken token) =
                ((ConcurrentDictionary<int, TaskCompletionSource<IndexWorkerMessage>>, int, CancellationToken))state!;
            if (pending.TryRemove(requestId, out TaskCompletionSource<IndexWorkerMessage>? pendingTcs))
            {
                pendingTcs.TrySetCanceled(token);
            }
        }, (_pending, id, cancellationToken));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> InitializeAsync()
    {
        TaskCompletionSource<IndexWorkerMessage> readyTcs = _readyTcs
            ?? throw new InvalidOperationException("The worker ready source was not initialized.");
        int sessionId = ++_sessionId;
        string? workerPath = ResolveWorkerPath();
        if (workerPath is null)
        {
            LogMissingWorkerOnce();
            return false;
        }

        // SECURITY: in a signed, shipped build, refuse to launch a worker that is not signed by the same
        // publisher as Yagu itself. This blocks a planted or tampered worker from running inside the signed
        // app's process tree. In unsigned local/dev builds the host is unsigned, so this is a no-op. The
        // path-override seam is the internal test/dev constructor only (never set by the production factory).
        if (!_hasWorkerPathOverride
            && !_trustVerifier(workerPath, out string trustFailure))
        {
            YaguLog.For(LogSource).LogWarning("refusing to launch index worker \"{WorkerPath}\": {TrustFailure}.", workerPath, trustFailure);
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Kill-on-close job so the worker dies with the app even on a hard crash. Created BEFORE Start so
            // we can assign the process the instant it exists.
            WindowsJobObject job = WindowsJobObject.CreateKillOnClose();

            IIndexWorkerProcess process = _processFactory(startInfo);
            process.Exited += (_, _) => OnProcessExited(process, sessionId, readyTcs);

            if (!process.Start())
            {
                process.Dispose();
                job.Dispose();
                return false;
            }

            try { _jobAssigner(job, process.Handle); }
            catch { /* best-effort; startup orphan sweep is the backstop */ }

            _job = job;
            _process = process;
            _stdin = process.StandardInput;

            _ = Task.Run(() => PumpStandardErrorAsync(process.StandardError));
            _ = Task.Run(() => ReadLoopAsync(process.StandardOutput, process, sessionId, readyTcs));

            using var timeout = new CancellationTokenSource(_readyTimeout);
            try
            {
                IndexWorkerMessage ready = await readyTcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (ready.ControlProtocolVersion != IndexWorkerProtocol.ControlProtocolVersion)
                {
                    YaguLog.For(LogSource).LogWarning(
                        "index worker control protocol mismatch: host requires {Expected}, worker reports {Actual}.",
                        IndexWorkerProtocol.ControlProtocolVersion, ready.ControlProtocolVersion);
                    try { process.Kill(); } catch { }
                    return false;
                }
                Volatile.Write(ref _workerEpoch, ready.Epoch);
                YaguLog.For(LogSource).LogInformation("index worker ready (pid {Pid}).", SafeId(process));
                return true;
            }
            catch (OperationCanceledException)
            {
                YaguLog.For(LogSource).LogWarning("index worker did not signal ready in time.");
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For(LogSource).LogWarning("index worker failed to start: {Error}", ex.Message);
            return false;
        }
    }

    private async Task ReadLoopAsync(
        StreamReader stdout,
        IIndexWorkerProcess process,
        int sessionId,
        TaskCompletionSource<IndexWorkerMessage> readyTcs)
    {
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                DispatchLine(line, process, sessionId, readyTcs);
            }
        }
        catch (Exception ex)
        {
            YaguLog.For(LogSource).LogDebug("index worker read loop ended: {Error}", ex.Message);
        }
        finally
        {
            if (IsCurrentSession(process, sessionId))
            {
                readyTcs.TrySetException(new IOException("index worker output stream closed."));
                FailAllPending("index worker output stream closed.");
            }
        }
    }

    private void DispatchLine(
        string line,
        IIndexWorkerProcess process,
        int sessionId,
        TaskCompletionSource<IndexWorkerMessage> readyTcs)
    {
        if (!IsCurrentSession(process, sessionId))
            return;
        IndexWorkerMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(line, IndexWorkerJsonContext.Default.IndexWorkerMessage);
        }
        catch (Exception ex)
        {
            FailProtocolChannel("index worker emitted malformed JSON: " + ex.Message, process, sessionId, readyTcs);
            return;
        }

        if (message is null)
        {
            FailProtocolChannel("index worker emitted an empty JSON message.", process, sessionId, readyTcs);
            return;
        }

        switch (message.Type)
        {
            case IndexWorkerProtocol.MessageTypes.Ready:
                readyTcs.TrySetResult(message);
                break;

            case IndexWorkerProtocol.MessageTypes.Error:
                YaguLog.For(LogSource).LogDebug("index worker init error: {Error}", message.Error ?? "unknown");
                readyTcs.TrySetException(new InvalidDataException(message.Error ?? "index worker initialization failed"));
                break;

            case IndexWorkerProtocol.MessageTypes.Result:
                if (_pending.TryRemove(message.Id, out TaskCompletionSource<IndexWorkerMessage>? tcs))
                {
                    tcs.TrySetResult(message);
                }

                break;

            default:
                FailProtocolChannel($"index worker emitted unknown message type '{message.Type}'.", process, sessionId, readyTcs);
                break;
        }
    }

    private static async Task PumpStandardErrorAsync(StreamReader stderr)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length != 0)
                {
                    YaguLog.For(LogSource).LogDebug("{WorkerLine}", line);
                }
            }
        }
        catch
        {
            // Worker exited; nothing more to log.
        }
    }

    private void OnProcessExited(
        IIndexWorkerProcess process,
        int sessionId,
        TaskCompletionSource<IndexWorkerMessage> readyTcs)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process) || _sessionId != sessionId)
                return;
            readyTcs.TrySetException(new IOException("index worker exited."));
            FailAllPending("index worker exited.");
            _initTask = null;
        }
    }

    private void FailProtocolChannel(
        string reason,
        IIndexWorkerProcess process,
        int sessionId,
        TaskCompletionSource<IndexWorkerMessage> readyTcs)
    {
        if (!IsCurrentSession(process, sessionId))
            return;
        YaguLog.For(LogSource).LogWarning("{Reason}", reason);
        readyTcs.TrySetException(new InvalidDataException(reason));
        FailAllPending(reason);
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch { }
    }

    private bool IsCurrentSession(IIndexWorkerProcess process, int sessionId)
    {
        lock (_gate)
            return ReferenceEquals(_process, process) && _sessionId == sessionId;
    }

    private void CleanupProcessUnderGate()
    {
        try
        {
            if (_process is { HasExited: false } liveProcess)
                liveProcess.Kill();
        }
        catch { }

        try { _process?.Dispose(); } catch { }
        _process = null;
        _stdin = null;
        _job?.Dispose();
        _job = null;
    }

    private void FailAllPending(string reason)
    {
        foreach (int key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out TaskCompletionSource<IndexWorkerMessage>? tcs))
            {
                tcs.TrySetResult(new IndexWorkerMessage { Type = IndexWorkerProtocol.MessageTypes.Result, Ok = false, Error = reason });
            }
        }
    }

    private string? ResolveWorkerPath()
    {
        if (_hasWorkerPathOverride)
        {
            return !string.IsNullOrEmpty(_workerPathOverride) && File.Exists(_workerPathOverride) ? _workerPathOverride : null;
        }

        string? env = Environment.GetEnvironmentVariable(WorkerPathEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        string local = Path.Combine(AppContext.BaseDirectory, "index-worker", "Yagu.IndexWorker.exe");
        return File.Exists(local) ? local : null;
    }

    private static void LogMissingWorkerOnce()
    {
        if (Interlocked.Exchange(ref _missingWorkerLogged, 1) != 0)
        {
            return;
        }

        string local = Path.Combine(AppContext.BaseDirectory, "index-worker", "Yagu.IndexWorker.exe");
        YaguLog.For(LogSource).LogWarning(
            "Yagu.IndexWorker.exe not found (probed {EnvVar} and '{LocalPath}'); native index acceleration is unavailable.",
            WorkerPathEnvVar, local);
    }

    private static T? DeserializeQueryResult<T>(string? json, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (string.IsNullOrEmpty(json))
            return default;

        try { return JsonSerializer.Deserialize(json, jsonTypeInfo); }
        catch (JsonException) { return default; }
    }

    private static int SafeId(IIndexWorkerProcess process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Ask the worker to exit cleanly, then force-kill if it lingers; the job object is the final backstop.
        try
        {
            TextWriter? stdin = _stdin;
            IIndexWorkerProcess? process = _process;
            if (stdin is not null && process is not null && !process.HasExited)
            {
                string line = JsonSerializer.Serialize(
                    new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.Shutdown },
                    IndexWorkerJsonContext.Default.IndexWorkerRequest);
                stdin.WriteLine(line);
                stdin.Flush();
            }
        }
        catch { /* ignore */ }

        try
        {
            IIndexWorkerProcess? process = _process;
            if (process is not null && !process.HasExited)
            {
                process.Kill();
            }
        }
        catch { /* ignore */ }

        try { _process?.Dispose(); }
        catch { /* ignore */ }

        _job?.Dispose();
        _writeLock.Dispose();
        FailAllPending("index worker disposed.");
    }
}
