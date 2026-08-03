using System;
using System.Runtime.InteropServices;

namespace Yagu.Services.Index;

/// <summary>
/// A Windows Job Object created with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Every process assigned to the
/// job is terminated by the OS when the last handle to the job closes — which happens automatically when the
/// Yagu process exits, <b>including a hard crash or force-kill</b>. Assigning the index worker to this job is
/// the primary guarantee that the worker can never outlive the app as an orphan; the startup
/// <see cref="OrphanedWorkerCleanup"/> sweep is a belt-and-braces backstop for the (rare) case where the job
/// could not be created or the process was assigned to a different job first.
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private IntPtr _handle;
    private readonly JobProcessAssigner _assignProcess;
    private readonly Func<IntPtr, bool> _closeHandle;
    private bool _disposed;

    internal delegate bool JobInformationSetter(IntPtr job, uint infoClass, IntPtr info, uint infoLength);
    internal delegate bool JobProcessAssigner(IntPtr job, IntPtr process);

    private WindowsJobObject(
        IntPtr handle,
        JobProcessAssigner assignProcess,
        Func<IntPtr, bool> closeHandle)
    {
        _handle = handle;
        _assignProcess = assignProcess;
        _closeHandle = closeHandle;
    }

    /// <summary>True on a non-Windows OS or when the job could not be created / configured (the caller then
    /// relies solely on the stdin-EOF exit + startup orphan sweep).</summary>
    public bool IsInvalid => _handle == IntPtr.Zero;

    /// <summary>Creates a kill-on-close job, or a handle-less instance (<see cref="IsInvalid"/>) if the OS
    /// calls fail. Never throws.</summary>
    public static WindowsJobObject CreateKillOnClose()
        => CreateKillOnClose(
            OperatingSystem.IsWindows(),
            CreateJobObject,
            SetInformationJobObject,
            AssignProcessToJobObject,
            CloseHandle);

    internal static WindowsJobObject CreateKillOnClose(
        bool isWindows,
        Func<IntPtr, IntPtr, IntPtr> createJob,
        JobInformationSetter setInformation,
        JobProcessAssigner assignProcess,
        Func<IntPtr, bool> closeHandle)
    {
        if (!isWindows)
        {
            return new WindowsJobObject(IntPtr.Zero, assignProcess, closeHandle);
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = createJob(IntPtr.Zero, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                return new WindowsJobObject(IntPtr.Zero, assignProcess, closeHandle);
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!setInformation(handle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                {
                    closeHandle(handle);
                    return new WindowsJobObject(IntPtr.Zero, assignProcess, closeHandle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            return new WindowsJobObject(handle, assignProcess, closeHandle);
        }
        catch
        {
            if (handle != IntPtr.Zero)
            {
                try { closeHandle(handle); }
                catch { /* ignore */ }
            }

            return new WindowsJobObject(IntPtr.Zero, assignProcess, closeHandle);
        }
    }

    /// <summary>Assigns <paramref name="processHandle"/> to the job. Returns false (best-effort) when the job
    /// is invalid or the OS call fails; the caller still runs the worker, just without the kill-on-close net.</summary>
    public bool Assign(IntPtr processHandle)
    {
        if (IsInvalid || processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _assignProcess(_handle, processHandle);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { _closeHandle(_handle); }
            catch { /* ignore */ }
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
