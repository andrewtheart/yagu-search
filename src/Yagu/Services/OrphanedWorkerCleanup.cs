using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services;

/// <summary>
/// Terminates leftover Yagu worker processes — the out-of-process semantic (<c>Yagu.SemanticWorker.exe</c>),
/// OCR (<c>Yagu.OcrWorker.exe</c>) and content-index (<c>Yagu.IndexWorker.exe</c>) hosts — that are still
/// running from a PREVIOUS Yagu run. When Yagu crashes or is force-killed, its worker children can be
/// orphaned and keep holding VRAM / RAM / CPU. This is run ONCE by the primary single-instance at startup,
/// before this instance spawns any of its own workers.
/// </summary>
/// <remarks>
/// A worker is treated as an orphan (and killed) only when BOTH hold, so a legitimately busy worker is never
/// taken down:
/// <list type="bullet">
/// <item>it was launched from THIS install's directory (a different install, or a look-alike planted
/// elsewhere, is left alone); and</item>
/// <item>its parent process is not a currently-running <c>Yagu.exe</c> — i.e. the Yagu that spawned it is
/// gone. This deliberately spares the workers of a concurrent <c>--cli</c> run (a live Yagu.exe parent),
/// which does NOT participate in the single-instance mutex.</item>
/// </list>
/// Best-effort throughout: any failure to enumerate, inspect, or kill a process is swallowed.
/// </remarks>
internal static class OrphanedWorkerCleanup
{
    /// <summary>Process names (without the <c>.exe</c> extension) of the out-of-process Yagu workers.</summary>
    internal static readonly string[] WorkerProcessNames = { "Yagu.SemanticWorker", "Yagu.OcrWorker", "Yagu.IndexWorker" };

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessBasicInformation = 0;

    /// <summary>True when <paramref name="modulePath"/> lives under <paramref name="baseDir"/> — i.e. the
    /// worker was launched from THIS install. A null/empty path (couldn't be read) is treated as NOT ours, so
    /// an unidentifiable process is left alone rather than killed.</summary>
    internal static bool IsWorkerFromInstall(string? modulePath, string? baseDir)
    {
        if (string.IsNullOrEmpty(modulePath) || string.IsNullOrEmpty(baseDir))
            return false;
        string root = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return modulePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds and kills orphaned worker processes from this install. Best-effort; never throws.</summary>
    [ExcludeFromCodeCoverage]
    public static void KillOrphanedWorkers()
    {
        string baseDir;
        try { baseDir = AppContext.BaseDirectory; }
        catch { return; }

        KillOrphanedWorkers(
            baseDir,
            GetLiveYaguProcessIds(),
            Process.GetProcessesByName,
            TryGetModulePath,
            GetParentProcessId,
            proc => proc.Kill(entireProcessTree: true));
    }

    internal static void KillOrphanedWorkers(
        string baseDir,
        IReadOnlySet<int> liveYaguPids,
        Func<string, Process[]> getProcessesByName,
        Func<Process, string?> getModulePath,
        Func<int, int> getParentProcessId,
        Action<Process> killProcess)
    {
        foreach (string name in WorkerProcessNames)
        {
            Process[] processes;
            try { processes = getProcessesByName(name); }
            catch { continue; }

            foreach (Process proc in processes)
            {
                try
                {
                    // Only touch workers launched from our own install directory.
                    if (!IsWorkerFromInstall(getModulePath(proc), baseDir))
                        continue;

                    // Leave alone a worker whose parent is still a live Yagu.exe (e.g. a concurrent --cli run).
                    int parentPid = getParentProcessId(proc.Id);
                    if (parentPid > 0 && liveYaguPids.Contains(parentPid))
                        continue;

                    killProcess(proc);
                    YaguLog.For("Startup").LogWarning(
                        "Terminated orphaned worker {Name} (pid {Pid}, parent {ParentPid}) left over from a previous run.",
                        name, SafeId(proc), parentPid);
                }
                catch
                {
                    // Access denied / already exited / couldn't inspect — skip it.
                }
                finally
                {
                    try { proc.Dispose(); }
                    catch { /* ignore */ }
                }
            }
        }
    }

    private static HashSet<int> GetLiveYaguProcessIds()
    {
        var pids = new HashSet<int>();
        try
        {
            foreach (Process p in Process.GetProcessesByName("Yagu"))
            {
                try { pids.Add(p.Id); }
                catch { /* ignore */ }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { /* ignore */ }
        return pids;
    }

    private static string? TryGetModulePath(Process proc)
    {
        try { return proc.MainModule?.FileName; }
        catch { return null; }
    }

    private static int SafeId(Process proc)
    {
        try { return proc.Id; }
        catch { return -1; }
    }

    /// <summary>Returns the parent process id of <paramref name="pid"/>, or -1 when it can't be read. Used
    /// both to spare a worker whose Yagu parent is still alive and to attribute a worker's RAM to THIS
    /// Yagu instance for the status-bar resource indicator.</summary>
    internal static int GetParentProcessId(int pid)
    {
        nint handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == nint.Zero)
            return -1;
        try
        {
            var info = default(PROCESS_BASIC_INFORMATION);
            int status = NtQueryInformationProcess(handle, ProcessBasicInformation, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : -1;
        }
        catch { return -1; }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
