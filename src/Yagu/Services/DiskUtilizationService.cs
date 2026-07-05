using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yagu.Services;

/// <summary>
/// Polls Windows for disk I/O metrics on a background thread.
/// Reports the current process's read+write bytes/sec (matches Task Manager's per-process
/// "Disk" column, which includes cached reads) and the system-wide physical-disk
/// utilization percentage. Provides rolling samples suitable for sparkline rendering.
/// </summary>
internal sealed class DiskUtilizationService : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<DiskSample> _samples = new();
    private Timer? _timer;
    private PerformanceCounter? _diskTimePct;
    private readonly IntPtr _processHandle = GetCurrentProcess();
    private ulong _prevReadBytes;
    private ulong _prevWriteBytes;
    private long _prevTimestampTicks;
    private bool _hasPrevSample;
    private bool _disposed;

    // Keep 60 seconds of samples at 1-second intervals
    private const int MaxSamples = 60;
    private const int PollIntervalMs = 1000;

    internal readonly record struct DiskSample(double MBPerSec, double UtilizationPct, long TimestampTicks);

    /// <summary>
    /// Gets a snapshot of current samples (thread-safe).
    /// </summary>
    internal List<DiskSample> GetSamples()
    {
        lock (_lock)
            return new List<DiskSample>(_samples);
    }

    /// <summary>
    /// Starts polling disk counters on a background thread.
    /// </summary>
    internal void Start()
    {
        if (_timer != null) return;
        // Initialize counters on the thread pool to avoid blocking UI
        _timer = new Timer(Poll, null, 0, PollIntervalMs);
    }

    /// <summary>
    /// Stops polling.
    /// </summary>
    internal void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Poll(object? state)
    {
        try
        {
            // System-wide physical disk utilization (matches Task Manager's overall % Active Time).
            _diskTimePct ??= new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true);
            float pctTime = _diskTimePct.NextValue();
            double utilization = Math.Clamp(pctTime, 0, 100);

            // Per-process bytes/sec from GetProcessIoCounters. This matches Task Manager's
            // per-process "Disk" column (and includes reads served from the OS file cache),
            // unlike PhysicalDisk Bytes/sec which is system-wide and excludes cached reads.
            double mbPerSec = 0;
            long nowTicks = Environment.TickCount64;
            if (GetProcessIoCounters(_processHandle, out var io))
            {
                if (_hasPrevSample)
                {
                    long dtMs = nowTicks - _prevTimestampTicks;
                    if (dtMs > 0)
                    {
                        ulong deltaRead = io.ReadTransferCount >= _prevReadBytes
                            ? io.ReadTransferCount - _prevReadBytes : 0;
                        ulong deltaWrite = io.WriteTransferCount >= _prevWriteBytes
                            ? io.WriteTransferCount - _prevWriteBytes : 0;
                        double bytesPerSec = (deltaRead + deltaWrite) * 1000.0 / dtMs;
                        mbPerSec = bytesPerSec / (1024.0 * 1024.0);
                    }
                }
                _prevReadBytes = io.ReadTransferCount;
                _prevWriteBytes = io.WriteTransferCount;
                _prevTimestampTicks = nowTicks;
                _hasPrevSample = true;
            }

            var sample = new DiskSample(mbPerSec, utilization, nowTicks);

            lock (_lock)
            {
                _samples.Add(sample);
                if (_samples.Count > MaxSamples)
                    _samples.RemoveRange(0, _samples.Count - MaxSamples);
            }
        }
        catch
        {
            // Performance counters may not be available (permissions, missing category).
            // Silently skip this sample rather than crashing.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _diskTimePct?.Dispose();
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr ProcessHandle, out IO_COUNTERS IoCounters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
