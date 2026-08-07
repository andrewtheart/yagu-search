using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yagu.Services;

internal readonly record struct ResultStoreDriveHardwareProfile(
    ResultStoreDriveTier Tier,
    uint? AdvertisedSpeedRpm);

internal readonly record struct WindowsPhysicalDiskMetadata(
    uint MediaType,
    uint BusType,
    uint? AdvertisedSpeedRpm);

internal static class ResultStoreDriveProfileDetector
{
    internal const string PhysicalDiskQueryCommand =
        "Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName MSFT_PhysicalDisk " +
        "-ErrorAction SilentlyContinue | ForEach-Object { [Console]::Out.WriteLine((\"{0}|{1}|{2}|{3}\" " +
        "-f $_.CimInstanceProperties['DeviceId'].Value, " +
        "[uint32]($_.CimInstanceProperties['MediaType'].Value), " +
        "[uint32]($_.CimInstanceProperties['BusType'].Value), " +
        "[uint32]($_.CimInstanceProperties['SpindleSpeed'].Value))) }";

    private const uint StorageDeviceProperty = 0;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;

    private const uint MediaTypeHdd = 3;
    private const uint MediaTypeSsd = 4;
    private const uint MediaTypeScm = 5;

    private const uint BusTypeAta = 3;
    private const uint BusTypeSata = 11;
    private const uint BusTypeNvme = 17;
    private const uint BusTypeNvmeOf = 20;

    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    private static readonly Lazy<IReadOnlyDictionary<uint, WindowsPhysicalDiskMetadata>> PhysicalDiskMetadata =
        new(QueryPhysicalDiskMetadata, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ResultStoreDriveHardwareProfile Detect(string driveRoot)
    {
        try
        {
            string? root = Path.GetPathRoot(driveRoot);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || !char.IsLetter(root[0]))
                return new(ResultStoreDriveTier.Unknown, null);

            using SafeFileHandle handle = CreateFileW(
                $@"\\.\{char.ToUpperInvariant(root[0])}:",
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return new(ResultStoreDriveTier.Unknown, null);

            uint? busType = TryGetBusType(handle);
            uint? mediaType = null;
            uint? advertisedSpeedRpm = null;

            uint? deviceNumber = TryGetDeviceNumber(handle);
            if (deviceNumber.HasValue &&
                PhysicalDiskMetadata.Value.TryGetValue(deviceNumber.Value, out WindowsPhysicalDiskMetadata metadata))
            {
                if (metadata.BusType != 0)
                    busType = metadata.BusType;
                if (metadata.MediaType != 0)
                    mediaType = metadata.MediaType;
                advertisedSpeedRpm = metadata.AdvertisedSpeedRpm;
            }

            bool? incursSeekPenalty = TryGetSeekPenalty(handle);
            ResultStoreDriveTier tier = Classify(busType, mediaType, incursSeekPenalty);
            return new(
                tier,
                tier == ResultStoreDriveTier.HardDisk ? advertisedSpeedRpm : null);
        }
        catch
        {
            return new(ResultStoreDriveTier.Unknown, null);
        }
    }

    internal static ResultStoreDriveTier Classify(
        uint? busType,
        uint? mediaType,
        bool? incursSeekPenalty)
    {
        if (busType is BusTypeNvme or BusTypeNvmeOf)
            return ResultStoreDriveTier.Nvme;

        if (mediaType == MediaTypeHdd || incursSeekPenalty == true)
            return ResultStoreDriveTier.HardDisk;

        if (busType is BusTypeAta or BusTypeSata)
            return ResultStoreDriveTier.Sata;

        if (mediaType is MediaTypeSsd or MediaTypeScm || incursSeekPenalty == false)
            return ResultStoreDriveTier.SolidState;

        return ResultStoreDriveTier.Unknown;
    }

    internal static IReadOnlyDictionary<uint, WindowsPhysicalDiskMetadata> ParsePhysicalDiskMetadata(string output)
    {
        var metadataByDevice = new Dictionary<uint, WindowsPhysicalDiskMetadata>();
        foreach (string line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 4 ||
                !uint.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint deviceId) ||
                !uint.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mediaType) ||
                !uint.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint busType) ||
                !uint.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint spindleSpeed))
            {
                continue;
            }

            uint? advertisedSpeedRpm = spindleSpeed is > 1 and < uint.MaxValue
                ? spindleSpeed
                : null;
            metadataByDevice[deviceId] = new(mediaType, busType, advertisedSpeedRpm);
        }

        return metadataByDevice;
    }

    private static IReadOnlyDictionary<uint, WindowsPhysicalDiskMetadata> QueryPhysicalDiskMetadata()
    {
        try
        {
            string powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershellPath))
                return new Dictionary<uint, WindowsPhysicalDiskMetadata>();

            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(PhysicalDiskQueryCommand);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new Dictionary<uint, WindowsPhysicalDiskMetadata>();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                return new Dictionary<uint, WindowsPhysicalDiskMetadata>();
            }

            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            return process.ExitCode == 0
                ? ParsePhysicalDiskMetadata(outputTask.Result)
                : new Dictionary<uint, WindowsPhysicalDiskMetadata>();
        }
        catch
        {
            return new Dictionary<uint, WindowsPhysicalDiskMetadata>();
        }
    }

    private static uint? TryGetDeviceNumber(SafeFileHandle handle)
    {
        bool success = DeviceIoControl(
            handle,
            IoctlStorageGetDeviceNumber,
            IntPtr.Zero,
            0,
            out StorageDeviceNumber deviceNumber,
            (uint)Marshal.SizeOf<StorageDeviceNumber>(),
            out _,
            IntPtr.Zero);
        return success ? deviceNumber.DeviceNumber : null;
    }

    private static uint? TryGetBusType(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceProperty,
            QueryType = PropertyStandardQuery,
        };
        bool success = DeviceIoControl(
            handle,
            IoctlStorageQueryProperty,
            ref query,
            (uint)Marshal.SizeOf<StoragePropertyQuery>(),
            out StorageDeviceDescriptor descriptor,
            (uint)Marshal.SizeOf<StorageDeviceDescriptor>(),
            out _,
            IntPtr.Zero);
        return success ? descriptor.BusType : null;
    }

    private static bool? TryGetSeekPenalty(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = PropertyStandardQuery,
        };
        bool success = DeviceIoControl(
            handle,
            IoctlStorageQueryProperty,
            ref query,
            (uint)Marshal.SizeOf<StoragePropertyQuery>(),
            out DeviceSeekPenaltyDescriptor descriptor,
            (uint)Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
            out _,
            IntPtr.Zero);
        return success ? descriptor.IncursSeekPenalty : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceDescriptor
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public uint BusType;
        public uint RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
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

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out StorageDeviceNumber lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        out StorageDeviceDescriptor lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}