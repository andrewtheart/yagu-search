using System.Runtime.InteropServices;

namespace Yagu.Helpers;

/// <summary>Reads the Windows session's global last-input timestamp for the content-index idle trigger.
/// Failure is reported as <c>null</c> so automatic maintenance fails open by doing nothing.</summary>
public static partial class SystemIdleDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Returns the elapsed time since the most recent keyboard/mouse input in this Windows
    /// session, or <c>null</c> when the platform call is unavailable.</summary>
    public static TimeSpan? TryGetIdleTime()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
                return null;
            uint elapsed = unchecked((uint)Environment.TickCount64 - info.TickCount);
            return TimeSpan.FromMilliseconds(elapsed);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pure threshold decision used by tests and the UI timer.</summary>
    public static bool HasBeenIdleFor(TimeSpan? idleTime, TimeSpan requiredIdle)
        => idleTime is { } idle && requiredIdle >= TimeSpan.Zero && idle >= requiredIdle;
}
