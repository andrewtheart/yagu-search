using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class WindowsJobObjectTests
{
    [Fact]
    public void CreateKillOnClose_PublicFactoryNeverThrows()
    {
        using WindowsJobObject job = WindowsJobObject.CreateKillOnClose();
        _ = job.IsInvalid;
    }

    [Fact]
    public void CreateKillOnClose_NonWindowsOrCreateFailureReturnsInvalidJob()
    {
        using WindowsJobObject nonWindows = Create(isWindows: false);
        using WindowsJobObject createFailure = Create(createJob: (_, _) => IntPtr.Zero);

        Assert.True(nonWindows.IsInvalid);
        Assert.True(createFailure.IsInvalid);
        Assert.False(nonWindows.Assign(new IntPtr(5)));
    }

    [Fact]
    public void CreateKillOnClose_ConfigurationFailureClosesHandle()
    {
        IntPtr closed = IntPtr.Zero;
        using WindowsJobObject job = Create(
            setInformation: (_, infoClass, info, infoLength) =>
            {
                Assert.Equal((uint)9, infoClass);
                Assert.NotEqual(IntPtr.Zero, info);
                Assert.True(infoLength > 0);
                return false;
            },
            closeHandle: handle =>
            {
                closed = handle;
                return true;
            });

        Assert.True(job.IsInvalid);
        Assert.Equal(new IntPtr(123), closed);
    }

    [Fact]
    public void CreateKillOnClose_ExceptionsReturnInvalidAndCleanupBestEffort()
    {
        int closeCalls = 0;
        using WindowsJobObject createThrows = Create(
            createJob: (_, _) => throw new InvalidOperationException("create failed"),
            closeHandle: _ =>
            {
                closeCalls++;
                return true;
            });
        Assert.True(createThrows.IsInvalid);
        Assert.Equal(0, closeCalls);

        using WindowsJobObject configureThrows = Create(
            setInformation: (_, _, _, _) => throw new InvalidOperationException("configure failed"),
            closeHandle: _ =>
            {
                closeCalls++;
                throw new InvalidOperationException("close failed");
            });
        Assert.True(configureThrows.IsInvalid);
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public void Assign_ValidatesHandleAndReturnsNativeOutcome()
    {
        var calls = new List<(IntPtr Job, IntPtr Process)>();
        using WindowsJobObject job = Create(assignProcess: (jobHandle, processHandle) =>
        {
            calls.Add((jobHandle, processHandle));
            return processHandle == new IntPtr(7);
        });

        Assert.False(job.IsInvalid);
        Assert.False(job.Assign(IntPtr.Zero));
        Assert.True(job.Assign(new IntPtr(7)));
        Assert.False(job.Assign(new IntPtr(8)));
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(new IntPtr(123), call.Job));
    }

    [Fact]
    public void Assign_NativeExceptionReturnsFalse()
    {
        using WindowsJobObject job = Create(
            assignProcess: (_, _) => throw new InvalidOperationException("assign failed"));

        Assert.False(job.Assign(new IntPtr(7)));
    }

    [Fact]
    public void Dispose_ClosesOnceClearsHandleAndSwallowsCloseFailure()
    {
        int closes = 0;
        WindowsJobObject job = Create(closeHandle: _ =>
        {
            closes++;
            throw new InvalidOperationException("close failed");
        });

        job.Dispose();
        job.Dispose();

        Assert.Equal(1, closes);
        Assert.True(job.IsInvalid);
    }

    private static WindowsJobObject Create(
        bool isWindows = true,
        Func<IntPtr, IntPtr, IntPtr>? createJob = null,
        WindowsJobObject.JobInformationSetter? setInformation = null,
        WindowsJobObject.JobProcessAssigner? assignProcess = null,
        Func<IntPtr, bool>? closeHandle = null)
        => WindowsJobObject.CreateKillOnClose(
            isWindows,
            createJob ?? ((_, _) => new IntPtr(123)),
            setInformation ?? ((_, _, _, _) => true),
            assignProcess ?? ((_, _) => true),
            closeHandle ?? (_ => true));
}