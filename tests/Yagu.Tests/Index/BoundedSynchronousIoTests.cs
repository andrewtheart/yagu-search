using System.Reflection;
using Microsoft.Win32.SafeHandles;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class BoundedSynchronousIoTests
{
    [Fact]
    public void TryExecute_ReturnsResult()
    {
        using var io = new BoundedSynchronousIo<int>(TimeSpan.FromSeconds(1));
        Assert.True(io.TryExecute(_ => 42, CancellationToken.None, out int value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryExecute_CooperativeTimeout_ReturnsFalseThenReusesLane()
    {
        using var io = new BoundedSynchronousIo<int>(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));
        Assert.False(io.TryExecute(token =>
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return 1;
        }, CancellationToken.None, out _));
        Assert.True(io.TryExecute(_ => 7, CancellationToken.None, out int value));
        Assert.Equal(7, value);
    }

    [Fact]
    public void TryExecute_UserCancellationPropagates()
    {
        using var io = new BoundedSynchronousIo<int>(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        Assert.Throws<OperationCanceledException>(() => io.TryExecute(token =>
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return 0;
        }, cancellation.Token, out _));
    }

    [Fact]
    public void TryExecute_UncooperativeTimeout_AbandonsLaneThenReusesReplacement()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            using var io = new BoundedSynchronousIo<int>(
                TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20), maximumAbandonedLanes: 2);
            // An operation that ignores cancellation forces the lane to be abandoned (not merely reused).
            Assert.False(io.TryExecute(_ => { release.Wait(); return 1; }, CancellationToken.None, out _));
            // A fresh replacement lane still serves the next operation.
            Assert.True(io.TryExecute(_ => 7, CancellationToken.None, out int value));
            Assert.Equal(7, value);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void TryExecute_TooManyUnstoppableOperations_FailsAfterAbandonLimit()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            using var io = new BoundedSynchronousIo<int>(
                TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(10), maximumAbandonedLanes: 1);
            IOException error = Assert.Throws<IOException>(() =>
                io.TryExecute(_ => { release.Wait(); return 0; }, CancellationToken.None, out _));
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void TryExecute_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedSynchronousIo<int>(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedSynchronousIo<int>(TimeSpan.FromSeconds(1), maximumAbandonedLanes: 0));
        using var io = new BoundedSynchronousIo<int>(TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentNullException>(() => io.TryExecute(null!, CancellationToken.None, out _));
    }

    [Fact]
    public void TryExecute_AfterDispose_Throws()
    {
        var io = new BoundedSynchronousIo<int>(TimeSpan.FromSeconds(1));
        io.Dispose();
        io.Dispose(); // idempotent second dispose hits the already-disposed early return
        Assert.Throws<ObjectDisposedException>(() => io.TryExecute(_ => 1, CancellationToken.None, out _));
    }

    [Fact]
    public void CompletedRequest_DisposeWhenCompletedAndRepeatedDispose_AreSafe()
    {
        var request = new BoundedSynchronousIo<int>.Request(_ => 42, CancellationToken.None);

        request.Run();
        request.DisposeWhenCompleted();

        request.Dispose();
    }

    [Fact]
    public async Task PendingRequest_DisposeWhenCompleted_DisposesAfterRun()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var request = new BoundedSynchronousIo<int>.Request(_ =>
        {
            entered.Set();
            release.Wait();
            return 42;
        }, CancellationToken.None);
        Task run = Task.Run(request.Run);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));

        request.DisposeWhenCompleted();
        release.Set();
        await run;

        Assert.Throws<ObjectDisposedException>(() => request.Completed.Wait(TimeSpan.Zero));
    }

    [Fact]
    public void FailedRequest_GetResult_RethrowsOperationError()
    {
        var expected = new IOException("read failed");
        using var request = new BoundedSynchronousIo<int>.Request(_ => throw expected, CancellationToken.None);

        request.Run();

        Assert.Same(expected, Assert.Throws<IOException>(() => request.GetResult()));
    }

    [Fact]
    public void Lane_DisposeIsIdempotent()
    {
        var lane = new BoundedSynchronousIo<int>.Lane();

        lane.Dispose();
        lane.Dispose();
    }

    [Fact]
    public void CancelPendingSynchronousIo_RejectsInvalidAndClosedHandles()
    {
        var lane = new BoundedSynchronousIo<int>.Lane();
        FieldInfo handleField = typeof(BoundedSynchronousIo<int>.Lane)
            .GetField("_threadHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (SafeWaitHandle)handleField.GetValue(lane)!;
        try
        {
            using var invalid = new SafeWaitHandle(IntPtr.Zero, ownsHandle: false);
            handleField.SetValue(lane, invalid);
            Assert.False(lane.CancelPendingSynchronousIo());

            var closed = new SafeWaitHandle(new IntPtr(1), ownsHandle: false);
            closed.Dispose();
            handleField.SetValue(lane, closed);
            Assert.False(lane.CancelPendingSynchronousIo());
        }
        finally
        {
            handleField.SetValue(lane, original);
            lane.Dispose();
        }
    }

    [Fact]
    public void TryCancelSynchronousIo_HandlesNativeResultAndDisposalRace()
    {
        using var handle = new SafeWaitHandle(new IntPtr(1), ownsHandle: false);

        Assert.True(BoundedSynchronousIo<int>.Lane.TryCancelSynchronousIo(handle, _ => true));
        Assert.False(BoundedSynchronousIo<int>.Lane.TryCancelSynchronousIo(
            handle,
            _ => throw new ObjectDisposedException("thread handle")));
    }

    [Fact]
    public void CancelPendingSynchronousIo_GuardsAgainstTheThreadHandleDisposalRace()
    {
        // The lane thread's Run() finally disposes _threadHandle when its queue completes, which races the
        // IsClosed check in CancelPendingSynchronousIo — the P/Invoke can then marshal an already-disposed
        // SafeWaitHandle and throw ObjectDisposedException. Both bounded-I/O lanes MUST swallow that benign
        // teardown race; a live index build observed it escape as a fatal "Cannot access a disposed object.
        // Object name: 'Microsoft.Win32.SafeHandles.SafeWaitHandle'." terminal error.
        string root = FindRepoRoot();
        foreach (string relative in new[]
        {
            Path.Combine("src", "Yagu", "Services", "Index", "BoundedSynchronousIo.cs"),
            Path.Combine("src", "Yagu", "Services", "Index", "BoundedIncrementalFileClassifier.cs"),
        })
        {
            string source = File.ReadAllText(Path.Combine(root, relative));
            int cancelIndex = source.IndexOf("public bool CancelPendingSynchronousIo()", StringComparison.Ordinal);
            Assert.True(cancelIndex >= 0, $"{relative} should declare CancelPendingSynchronousIo().");
            string body = source[cancelIndex..Math.Min(source.Length, cancelIndex + 1000)];
            Assert.Contains("try { return", body);
            Assert.Contains("catch (ObjectDisposedException) { return false; }", body);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
