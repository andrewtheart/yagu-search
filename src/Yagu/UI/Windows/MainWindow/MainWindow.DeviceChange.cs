using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yagu.Services;
using Yagu.Services.Logging;

namespace Yagu;

public sealed partial class MainWindow
{
    private const uint WmDeviceChange = 0x0219;
    private const uint DbtDeviceArrival = 0x8000;
    private const uint DbtDeviceRemoveComplete = 0x8004;
    private const uint DbtDeviceTypeVolume = 0x00000002;

    private void CaptureDeviceChangeAndDispatch(UIntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero || wParam.ToUInt32() is not (DbtDeviceArrival or DbtDeviceRemoveComplete))
            return;
        DEV_BROADCAST_HDR header;
        try { header = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam); }
        catch { return; }
        if (header.DeviceType != DbtDeviceTypeVolume || header.Size < Marshal.SizeOf<DEV_BROADCAST_VOLUME>())
            return;

        DEV_BROADCAST_VOLUME volume;
        try { volume = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam); }
        catch { return; }
        IReadOnlyList<string> roots = DeviceVolumeChange.ExpandVolumeUnitMask(volume.UnitMask);
        if (roots.Count == 0)
            return;

        bool removed = wParam.ToUInt32() == DbtDeviceRemoveComplete;
        DispatcherQueue.TryEnqueue(() => _ = HandleVolumeChangeAsync(roots, removed));
    }

    private async Task HandleVolumeChangeAsync(IReadOnlyList<string> roots, bool removed)
    {
        if (_disposed)
            return;
        if (removed)
        {
            ViewModel.CancelOperationsForRemovedVolumes(roots);
            try { _previewLoadCts?.Cancel(); } catch { }
            DisposeIndexWatcherHints();
            ViewModel.StatusText = $"{string.Join(", ", roots)} removed; affected operations were cancelled.";
        }
        else
        {
            // Device arrival can precede mount readiness. A short asynchronous settle avoids doing drive I/O
            // in the window procedure while still refreshing promptly.
            await Task.Delay(750).ConfigureAwait(true);
        }

        QueueIndexWatcherHintsRecreation(removed ? "volume removed" : "volume arrived");
        ViewModel.RefreshCurrentIndexStatus();
        ViewModel.RefreshAllDriveIndexStatus();
        YaguLog.For("ContentIndex").LogInformation(
            "Volume {Action}: {Roots}; index health and watcher registrations refreshed.",
            removed ? "removed" : "arrived",
            string.Join(", ", roots));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
        public uint UnitMask;
        public ushort Flags;
    }
}
