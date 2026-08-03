using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yagu.Services.Index;

/// <summary>
/// Executes one synchronous kernel-I/O operation at a time on a dedicated replaceable thread. Timeout
/// cancellation targets that exact thread with <c>CancelSynchronousIo</c>; an uncooperative lane is abandoned
/// and replaced up to a fixed cap, preventing one filter/device call from hanging its owner forever.
/// </summary>
internal sealed class BoundedSynchronousIo<T> : IDisposable
{
    internal static readonly TimeSpan DefaultCancellationGrace = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _grace;
    private readonly int _maximumAbandonedLanes;
    private readonly object _gate = new();
    private Lane? _lane;
    private int _abandonedLanes;
    private bool _disposed;

    public BoundedSynchronousIo(TimeSpan timeout, TimeSpan? cancellationGrace = null, int maximumAbandonedLanes = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAbandonedLanes, 1);
        _timeout = timeout;
        _grace = cancellationGrace ?? DefaultCancellationGrace;
        _maximumAbandonedLanes = maximumAbandonedLanes;
    }

    public bool TryExecute(
        Func<CancellationToken, T> operation,
        CancellationToken operationToken,
        out T? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(operation);
        operationToken.ThrowIfCancellationRequested();

        Lane lane = GetOrCreateLane();
        var request = new Request(operation, operationToken);
        lane.Queue(request);
        try
        {
            if (request.Completed.Wait(_timeout, operationToken))
            {
                try
                {
                    value = request.GetResult();
                    return true;
                }
                finally { request.Dispose(); }
            }
        }
        catch (OperationCanceledException)
        {
            request.Cancel();
            lane.CancelPendingSynchronousIo();
            request.DisposeWhenCompleted();
            throw;
        }

        request.Cancel();
        lane.CancelPendingSynchronousIo();
        if (request.Completed.Wait(_grace))
        {
            request.Dispose();
            value = default;
            return false;
        }

        int abandoned;
        lock (_gate)
        {
            if (ReferenceEquals(_lane, lane))
                _lane = null;
            lane.Abandon();
            abandoned = ++_abandonedLanes;
        }
        request.DisposeWhenCompleted();
        if (abandoned >= _maximumAbandonedLanes)
            throw new IOException($"Synchronous I/O stopped after {abandoned} operations exceeded the {_timeout.TotalSeconds:F0}-second timeout.");
        value = default;
        return false;
    }

    private Lane GetOrCreateLane()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _lane ??= new Lane();
        }
    }

    public void Dispose()
    {
        Lane? lane;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            lane = _lane;
            _lane = null;
        }
        lane?.Dispose();
    }

    internal sealed class Request : IDisposable
    {
        private readonly Func<CancellationToken, T> _operation;
        private readonly CancellationTokenSource _cancellation;
        private T? _result;
        private Exception? _error;
        private int _completed;
        private int _disposeWhenCompleted;
        private int _disposed;

        public Request(Func<CancellationToken, T> operation, CancellationToken operationToken)
        {
            _operation = operation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        }

        public ManualResetEventSlim Completed { get; } = new(false);

        public void Run()
        {
            try { _result = _operation(_cancellation.Token); }
            catch (Exception ex) { _error = ex; }
            finally
            {
                Completed.Set();
                Volatile.Write(ref _completed, 1);
                if (Volatile.Read(ref _disposeWhenCompleted) != 0)
                    Dispose();
            }
        }

        public T? GetResult()
        {
            if (_error is not null)
                ExceptionDispatchInfo.Capture(_error).Throw();
            return _result;
        }

        public void Cancel()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void DisposeWhenCompleted()
        {
            Volatile.Write(ref _disposeWhenCompleted, 1);
            if (Volatile.Read(ref _completed) != 0)
                Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cancellation.Dispose();
            Completed.Dispose();
        }
    }

    internal sealed class Lane : IDisposable
    {
        private const uint ThreadTerminateAccess = 0x0001;
        private readonly BlockingCollection<Request> _queue = new(1);
        private readonly ManualResetEventSlim _started = new(false);
        private readonly Thread _thread;
        private SafeWaitHandle? _threadHandle;
        private bool _disposed;

        public Lane()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Yagu bounded synchronous I/O" };
            _thread.Start();
            _started.Wait();
        }

        public void Queue(Request request) => _queue.Add(request);

        private void Run()
        {
            SafeWaitHandle threadHandle = BoundedSynchronousIoNative.OpenThread(
                ThreadTerminateAccess,
                false,
                BoundedSynchronousIoNative.GetCurrentThreadId());
            _threadHandle = threadHandle;
            _started.Set();
            try
            {
                foreach (Request request in _queue.GetConsumingEnumerable())
                    request.Run();
            }
            finally
            {
                threadHandle.Dispose();
                _threadHandle = null;
            }
        }

        public bool CancelPendingSynchronousIo()
        {
            SafeWaitHandle? handle = _threadHandle;
            if (handle is not { IsInvalid: false, IsClosed: false })
                return false;
            // The lane thread's Run() finally disposes _threadHandle when its queue completes; that can race
            // the check above, so the P/Invoke may marshal an already-disposed SafeWaitHandle and throw
            // ObjectDisposedException. A benign teardown race must never surface as a fatal error.
            return TryCancelSynchronousIo(handle, BoundedSynchronousIoNative.CancelSynchronousIo);
        }

        internal static bool TryCancelSynchronousIo(
            SafeWaitHandle handle,
            Func<SafeWaitHandle, bool> cancel)
        {
            try { return cancel(handle); }
            catch (ObjectDisposedException) { return false; }
        }

        public void Abandon() => _queue.CompleteAdding();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _queue.CompleteAdding();
            CancelPendingSynchronousIo();
            if (_thread.Join(DefaultCancellationGrace))
            {
                _queue.Dispose();
                _started.Dispose();
            }
        }

    }
}

internal static class BoundedSynchronousIoNative
{
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeWaitHandle OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelSynchronousIo(SafeWaitHandle threadHandle);
}
