using System.Runtime.InteropServices;

namespace Yagu.Helpers;

/// <summary>
/// Reads the machine's AC/battery power state via <c>GetSystemPowerStatus</c> (no elevation), so opt-in
/// background index builds can pause on battery (plan §6.1 <c>IndexPauseOnBattery</c>). It <b>fails open</b>:
/// an unknown or failed read reports "not on battery", so a desktop (no battery) or an API failure never
/// blocks indexing.
/// </summary>
internal static class PowerLineStatus
{
    // ACLineStatus: 0 = offline (running on battery), 1 = online (AC), 255 = unknown.
    private const byte AcOffline = 0;

    /// <summary>
    /// True only when the system is confirmed to be running on battery (AC power offline). Any unknown
    /// state or P/Invoke failure returns false (treated as AC), so indexing is never blocked by an
    /// unreadable power state.
    /// </summary>
    public static bool IsOnBattery()
    {
        try
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
                return status.ACLineStatus == AcOffline;
        }
        catch
        {
            // P/Invoke unavailable → fail open (treat as AC / plugged in).
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
