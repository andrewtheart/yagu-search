using System.Diagnostics;

namespace Yagu.Services;

/// <summary>
/// Lightweight per-file diagnostics. Writes a detailed Info log line whenever a
/// scanned file's path contains any of the watched substrings (case-insensitive).
/// Use this to bracket the lifecycle of a suspect file (sniff → scan → emit) and
/// correlate it with GC/memory/threadpool stats.
/// </summary>
public static class FileWatchDiagnostics
{
    /// <summary>
    /// Substrings to watch for. Empty by default — this is an opt-in diagnostic:
    /// register a suspect path substring at runtime via <see cref="Add"/> to
    /// bracket its scan/emit lifecycle, and <see cref="Clear"/> to reset.
    /// </summary>
    private static readonly object s_lock = new();
    private static readonly HashSet<string> s_patterns = new(StringComparer.OrdinalIgnoreCase);

    // Lock-free read snapshot — updated on Add/Clear. Readers never contend.
    private static volatile string[] s_snapshot = [];

    public static void Add(string substring)
    {
        lock (s_lock) { s_patterns.Add(substring); s_snapshot = [.. s_patterns]; }
    }

    public static void Clear()
    {
        lock (s_lock) { s_patterns.Clear(); s_snapshot = []; }
    }

    public static bool IsWatched(string filePath)
    {
        var patterns = s_snapshot;
        if (patterns.Length == 0) return false;
        foreach (var p in patterns)
            if (filePath.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Emit a checkpoint log line capturing memory/GC/threadpool state.</summary>
    public static void Checkpoint(string filePath, string phase, long elapsedMs = -1, string? extra = null)
    {
        var proc = Process.GetCurrentProcess();
        long workingSetMB = proc.WorkingSet64 / (1024 * 1024);
        long privateMB = proc.PrivateMemorySize64 / (1024 * 1024);
        long managedMB = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
        var info = GC.GetGCMemoryInfo();
        long heapMB = info.HeapSizeBytes / (1024 * 1024);
        long fragMB = info.FragmentedBytes / (1024 * 1024);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
        ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);
        long pendingItems = ThreadPool.PendingWorkItemCount;
        long completedItems = ThreadPool.CompletedWorkItemCount;
        int threads = proc.Threads.Count;

        string elapsedStr = elapsedMs >= 0 ? $" elapsed={elapsedMs}ms" : "";
        string extraStr = extra is null ? "" : $" {extra}";
        LogService.Instance.Info("WATCH",
            $"[{phase}] {Path.GetFileName(filePath)}{elapsedStr}{extraStr} | " +
            $"WS={workingSetMB:N0}MB Priv={privateMB:N0}MB Heap={heapMB:N0}MB Mng={managedMB:N0}MB Frag={fragMB:N0}MB | " +
            $"GC[g0={gen0} g1={gen1} g2={gen2}] | " +
            $"TP[workers={workerMax - workerAvail}/{workerMax} pending={pendingItems} completed={completedItems}] | " +
            $"threads={threads}");
    }
}
