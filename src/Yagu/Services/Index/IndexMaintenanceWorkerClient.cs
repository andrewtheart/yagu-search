using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

internal readonly record struct IndexMaintenanceWorkerResult(
    bool WorkerStarted,
    bool Accepted,
    bool WorkerExited,
    IndexWorkerMessage? Terminal,
    string? Failure);

internal delegate bool IndexWorkerTrustVerifier(string workerPath, out string failure);

/// <summary>One-operation client for the worker's <c>--maintenance</c> role. It is intentionally separate
/// from the long-lived query client: builds cannot starve queries, and disposing this client requires the
/// child process to exit, deterministically returning its managed heap and LOH reservation to Windows.</summary>
internal sealed class IndexMaintenanceWorkerClient : IAsyncDisposable, IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private const string LogSource = "IndexWorker";
    private readonly string? _workerPathOverride;
    private readonly bool _hasWorkerPathOverride;
    private readonly TimeSpan _acceptanceDeadline;
    private readonly TimeSpan _cancellationGrace;
    private readonly TimeSpan _shutdownGrace;
    private readonly IndexWorkerTrustVerifier _trustVerifier;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TaskCompletionSource<IndexWorkerMessage> _ready = NewMessageTcs();
    private TaskCompletionSource<IndexWorkerMessage>? _accepted;
    private TaskCompletionSource<IndexWorkerMessage>? _terminal;
    private Action<IndexWorkerMessage>? _progress;
    private Process? _process;
    private WindowsJobObject? _job;
    private TextWriter? _stdin;
    private int _operationId;
    private bool _disposed;
    private bool _acceptedSeen;
    private bool _terminalSeen;
    private string? _channelFailure;

    public IndexMaintenanceWorkerClient()
    {
        _acceptanceDeadline = TimeSpan.FromSeconds(30);
        _cancellationGrace = TimeSpan.FromSeconds(5);
        _shutdownGrace = TimeSpan.FromSeconds(5);
        _trustVerifier = AuthenticodeVerifier.IsWorkerTrustedForHost;
    }

    internal IndexMaintenanceWorkerClient(string? workerPathOverride)
        : this(workerPathOverride, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
    {
    }

    internal IndexMaintenanceWorkerClient(
        string? workerPathOverride,
        TimeSpan acceptanceDeadline,
        TimeSpan cancellationGrace,
        TimeSpan? shutdownGrace = null,
        bool skipTrust = true,
        IndexWorkerTrustVerifier? trustVerifier = null)
    {
        _workerPathOverride = workerPathOverride;
        _hasWorkerPathOverride = skipTrust;
        _acceptanceDeadline = acceptanceDeadline;
        _cancellationGrace = cancellationGrace;
        _shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(5);
        _trustVerifier = trustVerifier ?? AuthenticodeVerifier.IsWorkerTrustedForHost;
    }

    public async Task<IndexMaintenanceWorkerResult> ExecuteAsync(
        IndexWorkerRequest request,
        Action<IndexWorkerMessage>? progress,
        CancellationToken cancellationToken)
    {
        if (!await StartAsync(cancellationToken).ConfigureAwait(false))
        {
            await StopAndWaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return new IndexMaintenanceWorkerResult(false, false, HasExited(), null, _channelFailure!);
        }

        int id = Interlocked.Increment(ref _operationId);
        request.Id = id;
        _accepted = NewMessageTcs();
        _terminal = NewMessageTcs();
        _progress = progress;
        _acceptedSeen = false;
        _terminalSeen = false;

        await SendInitialRequestAsync(request).ConfigureAwait(false);

        Task<IndexWorkerMessage> acceptedTask = _accepted.Task;
        Task<IndexWorkerMessage> terminalTask = _terminal.Task;
        Task acceptanceDeadline = Task.Delay(_acceptanceDeadline, CancellationToken.None);
        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task first = await Task.WhenAny(acceptedTask, terminalTask, acceptanceDeadline, cancellationTask).ConfigureAwait(false);

        if (first == cancellationTask)
        {
            await CancelAndAwaitTerminalAsync(id).ConfigureAwait(false);
        }
        else if (first == acceptanceDeadline)
        {
            FailChannel("maintenance worker did not accept the operation in time");
        }
        else if (first == acceptedTask && !terminalTask.IsCompleted)
        {
            Task completed = await Task.WhenAny(terminalTask, cancellationTask).ConfigureAwait(false);
            if (completed == cancellationTask)
                await CancelAndAwaitTerminalAsync(id).ConfigureAwait(false);
        }

        IndexWorkerMessage? terminal = _terminal.Task.Status == TaskStatus.RanToCompletion ? _terminal.Task.Result : null;
        bool accepted = _acceptedSeen;
        await StopAndWaitAsync(_shutdownGrace).ConfigureAwait(false);
        return new IndexMaintenanceWorkerResult(true, accepted, HasExited(), terminal, _channelFailure);
    }

    private async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        string? path = ResolveWorkerPath();
        if (path is null)
        {
            _channelFailure = "Yagu.IndexWorker.exe was not found";
            return false;
        }
        if (!IsWorkerTrusted(_hasWorkerPathOverride, path, _trustVerifier, out string trustFailure))
        {
            _channelFailure = "maintenance worker trust check failed: " + trustFailure;
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("--maintenance");
            WindowsJobObject job = WindowsJobObject.CreateKillOnClose();
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            _ = process.Start();
            try { job.Assign(process.SafeHandle.DangerousGetHandle()); } catch { }
            _job = job;
            _process = process;
            _stdin = process.StandardInput;
            _ = Task.Run(() => ReadLoopAsync(process.StandardOutput));
            _ = Task.Run(() => PumpStandardErrorAsync(process.StandardError));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            IndexWorkerMessage ready = await _ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (ready.ControlProtocolVersion != IndexWorkerProtocol.ControlProtocolVersion)
            {
                _channelFailure = $"control protocol mismatch: expected {IndexWorkerProtocol.ControlProtocolVersion}, got {ready.ControlProtocolVersion}";
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            _channelFailure = "maintenance worker ready handshake timed out";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _channelFailure = "maintenance worker failed to start: " + ex.Message;
            return false;
        }
    }

    private async Task CancelAndAwaitTerminalAsync(int id)
    {
        await SendCancelBestEffortAsync(id).ConfigureAwait(false);

        Task completed = await Task.WhenAny(_terminal!.Task, Task.Delay(_cancellationGrace, CancellationToken.None)).ConfigureAwait(false);
        if (completed != _terminal.Task)
        {
            _terminal.TrySetResult(new IndexWorkerMessage
            {
                Type = IndexWorkerProtocol.MessageTypes.Result,
                Id = id,
                Ok = false,
                OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Cancelled,
                Error = "cancelled",
            });
            Kill();
        }
    }

    internal async Task SendInitialRequestAsync(IndexWorkerRequest request)
    {
        try
        {
            await WriteRequestAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailChannel("failed to send maintenance request: " + ex.Message);
        }
    }

    internal async Task SendCancelBestEffortAsync(int id)
    {
        try
        {
            await WriteRequestAsync(new IndexWorkerRequest
            {
                Op = IndexWorkerProtocol.Ops.CancelBuild,
                Id = id,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    internal async Task WriteRequestAsync(IndexWorkerRequest request, CancellationToken cancellationToken)
    {
        TextWriter stdin = _stdin ?? throw new IOException("maintenance worker stdin is unavailable");
        string line = JsonSerializer.Serialize(request, IndexWorkerJsonContext.Default.IndexWorkerRequest);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader stdout)
    {
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                    continue;
                IndexWorkerMessage message = JsonSerializer.Deserialize(line, IndexWorkerJsonContext.Default.IndexWorkerMessage)
                    ?? throw new InvalidDataException("maintenance worker emitted an empty message");
                Dispatch(message);
            }
            if (!_disposed)
                FailChannel("maintenance worker output stream closed");
        }
        catch (Exception ex)
        {
            FailChannel("maintenance worker protocol failure: " + ex.Message);
        }
    }

    private void Dispatch(IndexWorkerMessage message)
    {
        if (message.Type == IndexWorkerProtocol.MessageTypes.Ready)
        {
            _ready.TrySetResult(message);
            return;
        }
        if (message.Type == IndexWorkerProtocol.MessageTypes.Error)
        {
            _channelFailure = message.Error ?? "maintenance worker initialization failed";
            _ready.TrySetException(new InvalidDataException(_channelFailure));
            ObserveIfFaulted(_ready.Task);
            return;
        }
        if (message.Id != _operationId || _terminalSeen)
        {
            // A late message for a cancelled/completed operation is harmless; an impossible duplicate for
            // the current operation is a fatal protocol error.
            if (message.Id == _operationId)
                FailChannel("maintenance worker sent a duplicate terminal/out-of-order message");
            return;
        }

        if (message.Type == IndexWorkerProtocol.MessageTypes.Accepted)
        {
            if (_acceptedSeen)
            {
                FailChannel("maintenance worker accepted the same operation twice");
                return;
            }
            _acceptedSeen = true;
            _accepted!.TrySetResult(message);
            return;
        }
        if (message.Type == IndexWorkerProtocol.MessageTypes.Progress)
        {
            if (!_acceptedSeen)
            {
                FailChannel("maintenance worker reported progress before acceptance");
                return;
            }
            try { _progress?.Invoke(message); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { YaguLog.For(LogSource).LogWarning("maintenance progress callback failed: {Error}", ex.Message); }
            return;
        }
        if (message.Type == IndexWorkerProtocol.MessageTypes.Result)
        {
            _terminalSeen = true;
            _terminal!.TrySetResult(message);
            return;
        }
        FailChannel($"maintenance worker emitted unknown message type '{message.Type}'");
    }

    internal async Task PumpStandardErrorAsync(StreamReader stderr)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Contains("[CRT]", StringComparison.Ordinal))
                    YaguLog.For(LogSource).LogCritical("{WorkerLine}", line);
                else if (line.Contains("[WRN]", StringComparison.Ordinal))
                    YaguLog.For(LogSource).LogWarning("{WorkerLine}", line);
                else if (line.Contains("[INF]", StringComparison.Ordinal))
                    YaguLog.For(LogSource).LogInformation("{WorkerLine}", line);
                else
                    YaguLog.For(LogSource).LogDebug("{WorkerLine}", line);
            }
        }
        catch
        {
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (!_disposed)
            FailChannel("maintenance worker exited");
    }

    private void FailChannel(string failure)
    {
        lock (_gate)
        {
            _channelFailure ??= failure;
            var ex = new IOException(_channelFailure);
            _ready.TrySetException(ex);
            _accepted?.TrySetException(ex);
            _terminal?.TrySetException(ex);
            // Observe the faults synchronously. ExecuteAsync inspects only the tasks' Status/Result
            // (failures are surfaced via IndexMaintenanceWorkerResult.Failure) and an abandoned handshake
            // never awaits _ready, so without this an unawaited fault would be rethrown by the finalizer
            // as an UnobservedTaskException ("maintenance worker output stream closed").
            ObserveIfFaulted(_ready.Task);
            if (_accepted is not null) ObserveIfFaulted(_accepted.Task);
            if (_terminal is not null) ObserveIfFaulted(_terminal.Task);
        }
        Kill();
    }

    // Reads the task's exception (if faulted) purely to mark it observed, so an abandoned faulted
    // completion source is never surfaced by the finalizer as an UnobservedTaskException. The task
    // stays faulted, so any real awaiter still sees the failure.
    private static void ObserveIfFaulted(Task task)
    {
        if (task.IsFaulted)
            _ = task.Exception;
    }

    public async Task StopAndWaitAsync(TimeSpan grace)
    {
        Process? process = _process;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited && _stdin is not null)
            {
                await WriteRequestAsync(new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.Shutdown }, CancellationToken.None).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(grace);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            Kill();
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        }
    }

    private string? ResolveWorkerPath()
        => ResolveWorkerPath(
            _hasWorkerPathOverride || _workerPathOverride is not null,
            _workerPathOverride,
            Environment.GetEnvironmentVariable(IndexWorkerClient.WorkerPathEnvVar),
            AppContext.BaseDirectory,
            File.Exists);

    internal static string? ResolveWorkerPath(
        bool hasOverride,
        string? workerPathOverride,
        string? environmentPath,
        string appBaseDirectory,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (hasOverride)
            return !string.IsNullOrWhiteSpace(workerPathOverride) && fileExists(workerPathOverride) ? workerPathOverride : null;
        if (!string.IsNullOrWhiteSpace(environmentPath) && fileExists(environmentPath))
            return environmentPath;
        string path = Path.Combine(appBaseDirectory, "index-worker", "Yagu.IndexWorker.exe");
        return fileExists(path) ? path : null;
    }

    internal static bool IsWorkerTrusted(
        bool hasPathOverride,
        string path,
        IndexWorkerTrustVerifier verifier,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (hasPathOverride)
        {
            failure = string.Empty;
            return true;
        }
        return verifier(path, out failure);
    }

    private void Kill()
    {
        try
        {
            if (_process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private bool HasExited() => HasExited(() => _process is null || _process.HasExited);

    internal static bool HasExited(Func<bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try { return probe(); }
        catch { return true; }
    }

    private static TaskCompletionSource<IndexWorkerMessage> NewMessageTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAndWaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Kill();
        _process?.Dispose();
        _job?.Dispose();
        _writeLock.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Kill();
        _process?.Dispose();
        _job?.Dispose();
        _writeLock.Dispose();
    }
}
