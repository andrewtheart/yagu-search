using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yagu.Services;

internal readonly record struct SystemMemorySnapshot(
    uint LoadPercent,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes);

internal interface ISystemMemoryProvider
{
    bool TryGetSnapshot(out SystemMemorySnapshot snapshot);
}

internal sealed class WindowsSystemMemoryProvider : ISystemMemoryProvider
{
    public bool TryGetSnapshot(out SystemMemorySnapshot snapshot)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            snapshot = new SystemMemorySnapshot(
                status.MemoryLoad,
                status.TotalPhysicalBytes,
                status.AvailablePhysicalBytes);
            return true;
        }

        snapshot = default;
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }
}

internal static class ProcessMemoryTrimmer
{
    public static void TrimCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        EmptyWorkingSet(process.Handle);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);
}