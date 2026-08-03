using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.IndexWorker;

/// <summary>
/// Entry point for the out-of-process content-index worker. Communicates with Yagu over stdin/stdout using
/// line-delimited JSON (see <see cref="IndexWorkerRequest"/> / <see cref="IndexWorkerMessage"/>). Diagnostic
/// logs go to stderr so they never corrupt the protocol stream.
/// <para>
/// The worker is the only Yagu process that loads the native index engine (<c>yagu_core.dll</c>), so a
/// read-time / native access violation is contained here and never fault-fasts the main app.
/// </para>
/// </summary>
internal static class Program
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly object OutLock = new();
    private static readonly SemaphoreSlim WorkLock = new(1, 1);
    // Query ops (open/classify/close/cancel) run on their OWN serialized queue, independent of the build
    // WorkLock: they operate on immutable pinned mapped snapshots, so a build never blocks a query and
    // concurrent per-root query batches QUEUE here (one at a time) instead of being fail-fast rejected as
    // busy (plan §5.2 worker queuing/multiplexing).
    private static readonly SemaphoreSlim QueryLock = new(1, 1);
    // Per-process worker generation stamped on the ready handshake and every query reply so the host can drop
    // a reply from a restarted worker (QueryReplyGate). ProcessId is distinct across restarts.
    private static readonly int WorkerEpoch = Environment.ProcessId;
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> InFlight = new();
    private static readonly ConcurrentDictionary<int, Task> Running = new();
    private static TextWriter _protocolOut = TextWriter.Null;
    private static bool _maintenanceRole;

    private static async Task<int> Main(string[] args)
    {
        IndexCrashInjection.InstallFromEnvironment();
#if DEBUG
        if (IndexCrashHarness.IsRequested(args))
            return IndexCrashHarness.Run(args);
#endif
        _maintenanceRole = args.Any(static value => string.Equals(value, "--maintenance", StringComparison.OrdinalIgnoreCase));
        _protocolOut = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = false };
        Console.SetOut(Console.Error);
        LogService.Instance.FileLevel = LogLevel.None;
        LogService.Instance.ConsoleLevel = LogLevel.Info;

        try
        {
            if (!_maintenanceRole)
            {
                NativeIndexEngine.Install();
                uint abi = NativeIndexEngine.AbiVersion();
                if (abi != IndexWorkerProtocol.RequiredIndexAbiVersion)
                {
                    Send(new IndexWorkerMessage
                    {
                        Type = IndexWorkerProtocol.MessageTypes.Error,
                        Stage = "init",
                        Ok = false,
                        Error = $"index ABI mismatch: worker requires {IndexWorkerProtocol.RequiredIndexAbiVersion}, engine reports {abi}",
                    });
                    Log($"INIT FAILED: ABI mismatch (engine={abi})");
                    return 1;
                }
                Log($"ready (index ABI {abi})");
            }
            Send(new IndexWorkerMessage
            {
                Type = IndexWorkerProtocol.MessageTypes.Ready,
                ControlProtocolVersion = IndexWorkerProtocol.ControlProtocolVersion,
                Epoch = WorkerEpoch,
            });
        }
        catch (Exception ex)
        {
            Send(new IndexWorkerMessage
            {
                Type = IndexWorkerProtocol.MessageTypes.Error,
                Stage = "init",
                Ok = false,
                Error = ex.Message,
            });
            Log("INIT FAILED: " + ex);
            return 1;
        }

        using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            // Defensive: a host whose stdin writer emits a UTF-8 BOM would prepend U+FEFF to the first line.
            line = line.Trim('\uFEFF', '\u200B').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            IndexWorkerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(line, IndexWorkerJsonContext.Default.IndexWorkerRequest);
            }
            catch (Exception ex)
            {
                Log("bad request json: " + ex.Message);
                continue;
            }

            if (request is null)
            {
                continue;
            }

            if (string.Equals(request.Op, IndexWorkerProtocol.Ops.Shutdown, StringComparison.Ordinal))
            {
                Log("shutdown requested");
                break;
            }

            if (string.Equals(request.Op, IndexWorkerProtocol.Ops.CancelBuild, StringComparison.Ordinal))
            {
                if (InFlight.TryGetValue(request.Id, out CancellationTokenSource? cancel))
                {
                    try { cancel.Cancel(); } catch (ObjectDisposedException) { }
                }
                continue;
            }

            Task task = HandleAsync(request);
            Running[request.Id] = task;
            _ = task.ContinueWith(static (_, state) =>
            {
                var (id, running) = ((int, ConcurrentDictionary<int, Task>))state!;
                running.TryRemove(id, out _);
            }, (request.Id, Running), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        foreach (CancellationTokenSource cts in InFlight.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        IndexQueryScopeHost.CloseAll();
        Task all = Task.WhenAll(Running.Values);
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        Log("shutdown");
        return 0;
    }

    private static async Task HandleAsync(IndexWorkerRequest request)
    {
        // Query-session ops run on their own serialized queue (never fail-fast busy), independent of builds.
        if (IndexQueryScopeHost.IsQueryOp(request.Op))
        {
            await HandleQueryAsync(request).ConfigureAwait(false);
            return;
        }

        if (!await WorkLock.WaitAsync(0).ConfigureAwait(false))
        {
            Send(IndexWorkerBuildHost.MapFailure(request.Id, new IndexWriteBusyException("worker")));
            return;
        }

        var cts = new CancellationTokenSource();
        IndexMutationContext? mutation = null;
        try
        {
            if (IsMaintenanceOp(request.Op))
            {
                (IndexMutationContext acquiredMutation, object operation) = IndexWorkerBuildHost.ValidateAndAcquire(request);
                mutation = acquiredMutation;
                InFlight[request.Id] = cts;
                Send(new IndexWorkerMessage
                {
                    Type = IndexWorkerProtocol.MessageTypes.Accepted,
                    Id = request.Id,
                    Ok = true,
                });
                Send(IndexWorkerBuildHost.Execute(request, mutation, operation, cts.Token, Send));
                return;
            }

            if (_maintenanceRole)
            {
                Send(Fail(request.Id, $"op '{request.Op}' is unavailable in maintenance mode"));
                return;
            }

            IndexWorkerMessage result = request.Op switch
            {
                IndexWorkerProtocol.Ops.Ping => new IndexWorkerMessage
                {
                    Type = IndexWorkerProtocol.MessageTypes.Result,
                    Id = request.Id,
                    Ok = true,
                },
                IndexWorkerProtocol.Ops.Extract => Extract(request),
                IndexWorkerProtocol.Ops.QueryContentBin => QueryContentBin(request),
                IndexWorkerProtocol.Ops.OpenQueryScope or IndexWorkerProtocol.Ops.ClassifyPaths or IndexWorkerProtocol.Ops.CloseQueryScope
                    => IndexQueryScopeHost.Handle(request),
                _ => Fail(request.Id, $"unknown op '{request.Op}'"),
            };
            Send(result);
        }
        catch (Exception ex)
        {
            Send(IsMaintenanceOp(request.Op)
                ? IndexWorkerBuildHost.MapFailure(request.Id, ex)
                : Fail(request.Id, ex.Message));
        }
        finally
        {
            InFlight.TryRemove(request.Id, out _);
            mutation?.Dispose();
            cts.Dispose();
            WorkLock.Release();
        }
    }

    /// <summary>
    /// Runs a mapped query-session op on the serialized query queue (plan §5.2). Query ops never fail-fast
    /// busy: concurrent per-root batches wait here and run one at a time. Every reply is stamped with the
    /// worker <see cref="WorkerEpoch"/> so the host can drop a reply from a restarted worker.
    /// </summary>
    private static async Task HandleQueryAsync(IndexWorkerRequest request)
    {
        await QueryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            IndexWorkerMessage reply;
            try
            {
                reply = IndexQueryScopeHost.Handle(request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reply = Fail(request.Id, ex.Message);
            }
            reply.Epoch = WorkerEpoch;
            Send(reply);
        }
        finally
        {
            QueryLock.Release();
        }
    }

    private static bool IsMaintenanceOp(string op) => op is
        IndexWorkerProtocol.Ops.BuildScope or
        IndexWorkerProtocol.Ops.RefreshAuto or
        IndexWorkerProtocol.Ops.ValidateScope;

    private static IndexWorkerMessage Extract(IndexWorkerRequest request)
    {
        if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
        {
            return Fail(request.Id, "file not found");
        }

        byte[] data = File.ReadAllBytes(request.Path);
        (int verdict, uint[] trigrams) = NativeIndexEngine.ExtractTrigrams(data);
        return new IndexWorkerMessage
        {
            Type = IndexWorkerProtocol.MessageTypes.Result,
            Id = request.Id,
            Ok = true,
            Verdict = verdict,
            TrigramsBase64 = IndexWorkerProtocol.EncodeTrigrams(trigrams),
        };
    }

    private static IndexWorkerMessage QueryContentBin(IndexWorkerRequest request)
    {
        if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
        {
            return Fail(request.Id, "content.bin not found");
        }

        byte[] contentBin = File.ReadAllBytes(request.Path);
        byte[] rpn = string.IsNullOrEmpty(request.QueryRpnBase64)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(request.QueryRpnBase64);

        int[] candidates = NativeIndexEngine.QueryContentBin(contentBin, rpn);
        return new IndexWorkerMessage
        {
            Type = IndexWorkerProtocol.MessageTypes.Result,
            Id = request.Id,
            Ok = true,
            CandidatesBase64 = IndexWorkerProtocol.EncodeCandidates(candidates),
        };
    }

    private static IndexWorkerMessage Fail(int id, string error) => new()
    {
        Type = IndexWorkerProtocol.MessageTypes.Result,
        Id = id,
        Ok = false,
        Error = error,
    };

    private static void Send(IndexWorkerMessage message)
    {
        string json = JsonSerializer.Serialize(message, IndexWorkerJsonContext.Default.IndexWorkerMessage);
        lock (OutLock)
        {
            _protocolOut.Write(json);
            _protocolOut.Write('\n');
            _protocolOut.Flush();
        }
    }

    private static void Log(string message) => Console.Error.WriteLine("[indexworker] " + message);
}
