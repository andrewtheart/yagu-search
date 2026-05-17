using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yagu.Helpers;

/// <summary>
/// Detects whether a given drive letter is backed by a rotational (HDD) or solid-state (SSD) disk.
/// Uses the IOCTL_STORAGE_QUERY_PROPERTY DeviceIoControl call, which works without elevation.
/// </summary>
internal static class DiskTypeDetector
{
    /// <summary>
    /// Returns true if the drive root (e.g. "C:\") is backed by a rotational hard disk.
    /// Returns false for SSDs, NVMe, RAM disks, network drives, or if detection fails.
    /// </summary>
    public static bool IsHardDisk(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || !char.IsLetter(root[0]))
                return false;

            // Open the physical drive device for the volume
            string volumePath = $@"\\.\{root[0]}:";
            using var handle = CreateFileW(
                volumePath,
                0, // No read/write access needed
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return false;

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = 7, // StorageDeviceSeekPenaltyProperty
                QueryType = 0,  // PropertyStandardQuery
            };

            int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            int resultSize = Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>();
            IntPtr queryPtr = Marshal.AllocHGlobal(querySize);
            IntPtr resultPtr = Marshal.AllocHGlobal(resultSize);
            try
            {
                Marshal.StructureToPtr(query, queryPtr, false);

                bool success = DeviceIoControl(
                    handle,
                    IOCTL_STORAGE_QUERY_PROPERTY,
                    queryPtr, (uint)querySize,
                    resultPtr, (uint)resultSize,
                    out _,
                    IntPtr.Zero);

                if (!success)
                    return false;

                var descriptor = Marshal.PtrToStructure<DEVICE_SEEK_PENALTY_DESCRIPTOR>(resultPtr);
                // IncursSeekPenalty == true means rotational (HDD)
                return descriptor.IncursSeekPenalty;
            }
            finally
            {
                Marshal.FreeHGlobal(queryPtr);
                Marshal.FreeHGlobal(resultPtr);
            }
        }
        catch
        {
            return false;
        }
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public int Version;
        public int Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
