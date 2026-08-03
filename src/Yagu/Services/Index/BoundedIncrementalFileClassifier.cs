using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Runs incremental-maintenance file opens/reads on a replaceable background I/O lane. A filesystem
/// filter, cloud provider, or device must not be able to hold the maintenance worker forever inside a
/// synchronous <see cref="FileStream"/> open. A timed-out file is treated as unreadable, which makes the
/// resolver tombstone its current/prior aliases so searches safely read it live after publication.
/// </summary>
internal sealed class BoundedIncrementalFileClassifier : IDisposable
{
    internal static readonly TimeSpan DefaultFileTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultCancellationGrace = TimeSpan.FromSeconds(2);
    internal const int DefaultMaximumAbandonedLanes = 2;

    private readonly Func<string, CancellationToken, IncrementalFileRead?> _read;
    private readonly CancellationToken _operationToken;
    private readonly TimeSpan _fileTimeout;
    private readonly TimeSpan _cancellationGrace;
    private readonly int _maximumAbandonedLanes;
    private readonly object _gate = new();
    private IoLane? _lane;
    private int _abandonedLanes;
    private bool _disposed;

    public BoundedIncrementalFileClassifier(
        IndexIngestionPolicy policy,
        CancellationToken operationToken)
        : this(policy, operationToken, DefaultFileTimeout)
    {
    }

    public BoundedIncrementalFileClassifier(
        IndexIngestionPolicy policy,
        CancellationToken operationToken,
        TimeSpan fileTimeout)
        : this(
            ContentIndexIncrementalUpdater.CreateCancellableFileReadClassifier(policy),
            operationToken,
            fileTimeout,
            DefaultCancellationGrace,
            DefaultMaximumAbandonedLanes)
    {
    }

    internal BoundedIncrementalFileClassifier(
        Func<string, CancellationToken, IncrementalFileRead?> read,
        CancellationToken operationToken,
        TimeSpan fileTimeout,
        TimeSpan cancellationGrace,
        int maximumAbandonedLanes)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        if (fileTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(fileTimeout));
        if (cancellationGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancellationGrace));
        if (maximumAbandonedLanes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAbandonedLanes));
        _operationToken = operationToken;
        _fileTimeout = fileTimeout;
        _cancellationGrace = cancellationGrace;
        _maximumAbandonedLanes = maximumAbandonedLanes;
    }

    public IncrementalFileRead? Read(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _operationToken.ThrowIfCancellationRequested();

        IoLane lane = GetOrCreateLane();
        var request = new ReadRequest(path, _operationToken);
        lane.Queue(request);

        try
        {
            if (request.Completed.Wait(_fileTimeout, _operationToken))
            {
                try { return request.GetResult(); }
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
        bool cancellationRequested = lane.CancelPendingSynchronousIo();
        bool stopped = request.Completed.Wait(_cancellationGrace);
        if (stopped)
        {
            request.Dispose();
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental file read timed out after {TimeoutSeconds:F0}s and was cancelled: '{Path}'. The file will scan live.",
                _fileTimeout.TotalSeconds, path);
            return null;
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

        YaguLog.For("ContentIndex").LogWarning(
            "Incremental file read timed out after {TimeoutSeconds:F0}s and did not stop (CancelSynchronousIo={CancellationRequested}) for '{Path}'. Abandoned I/O lane {Abandoned}/{Maximum}; the file will scan live.",
            _fileTimeout.TotalSeconds, cancellationRequested, path, abandoned, _maximumAbandonedLanes);

        if (abandoned >= _maximumAbandonedLanes)
        {
            throw new IOException(
                $"Incremental indexing stopped after {abandoned} file opens/reads exceeded the {_fileTimeout.TotalSeconds:F0}-second timeout. The previous index remains unchanged.");
        }
        return null;
    }

    private IoLane GetOrCreateLane()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _lane ??= new IoLane(_read);
        }
    }

    public void Dispose()
    {
        IoLane? lane;
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

    internal sealed class ReadRequest : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private Exception? _error;
        private IncrementalFileRead? _result;
        private int _completed;
        private int _disposeWhenCompleted;
        private int _disposed;

        public ReadRequest(string path, CancellationToken operationToken)
        {
            Path = path;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        }

        public string Path { get; }
        public CancellationToken Token => _cancellation.Token;
        public ManualResetEventSlim Completed { get; } = new(initialState: false);

        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void SetResult(IncrementalFileRead? result) => _result = result;
        public void SetError(Exception error) => _error = error;
        public void SignalCompleted()
        {
            Completed.Set();
            Volatile.Write(ref _completed, 1);
            if (Volatile.Read(ref _disposeWhenCompleted) != 0)
                Dispose();
        }

        public void DisposeWhenCompleted()
        {
            Volatile.Write(ref _disposeWhenCompleted, 1);
            if (Volatile.Read(ref _completed) != 0)
                Dispose();
        }

        public IncrementalFileRead? GetResult()
        {
            if (_error is not null)
                ExceptionDispatchInfo.Capture(_error).Throw();
            return _result;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cancellation.Dispose();
            Completed.Dispose();
        }
    }

    internal sealed class IoLane : IDisposable
    {
        private const uint ThreadTerminateAccess = 0x0001;
        private readonly Func<string, CancellationToken, IncrementalFileRead?> _read;
        private readonly BlockingCollection<ReadRequest> _queue = new(boundedCapacity: 1);
        private readonly ManualResetEventSlim _started = new(initialState: false);
        private readonly Thread _thread;
        private SafeWaitHandle? _threadHandle;
        private bool _abandoned;
        private bool _disposed;

        public IoLane(Func<string, CancellationToken, IncrementalFileRead?> read)
        {
            _read = read;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Yagu incremental file I/O",
            };
            _thread.Start();
            _started.Wait();
        }

        public void Queue(ReadRequest request)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Add(request);
        }

        private void Run()
        {
            uint threadId = GetCurrentThreadId();
            SafeWaitHandle threadHandle = OpenThread(ThreadTerminateAccess, inheritHandle: false, threadId);
            _threadHandle = threadHandle;
            _started.Set();

            try
            {
                foreach (ReadRequest request in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        request.SetResult(_read(request.Path, request.Token));
                    }
                    catch (Exception ex)
                    {
                        request.SetError(ex);
                    }
                    finally
                    {
                        request.SignalCompleted();
                    }
                }
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
            // the IsClosed check above, so the P/Invoke may marshal an already-disposed SafeWaitHandle and
            // throw ObjectDisposedException. A benign teardown race must never surface as a fatal build error.
            return TryCancelSynchronousIo(handle, CancelSynchronousIo);
        }

        internal static bool TryCancelSynchronousIo(
            SafeWaitHandle handle,
            Func<SafeWaitHandle, bool> cancel)
        {
            try { return cancel(handle); }
            catch (ObjectDisposedException) { return false; }
        }

        public void Abandon()
        {
            if (_abandoned)
                return;
            _abandoned = true;
            _queue.CompleteAdding();
        }

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

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelSynchronousIo(SafeWaitHandle threadHandle);
    }
}

/// <summary>Small ownership wrapper for the fixed set of independent full-build I/O watchdog lanes.</summary>
internal sealed class BoundedIncrementalFileClassifierPool : IDisposable
{
    private readonly BoundedIncrementalFileClassifier[] _lanes;

    public BoundedIncrementalFileClassifierPool(
        int count,
        Func<int, BoundedIncrementalFileClassifier> factory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentNullException.ThrowIfNull(factory);
        _lanes = new BoundedIncrementalFileClassifier[count];
        for (int i = 0; i < count; i++)
            _lanes[i] = factory(i);
    }

    public BoundedIncrementalFileClassifier this[int index] => _lanes[index];

    public void Dispose()
    {
        foreach (BoundedIncrementalFileClassifier lane in _lanes)
            lane.Dispose();
    }
}
