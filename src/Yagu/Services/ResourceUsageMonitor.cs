using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Yagu.Native;

namespace Yagu.Services;

internal enum IndexStorageSizeSource
{
    Everything,
    EsExe,
    FileSystem,
}

internal readonly record struct IndexStorageSizeMeasurement(
    long Bytes,
    IndexStorageSizeSource Source,
    bool Complete);

internal interface IIndexStorageEsExeOps
{
    string? FindExecutable();
    bool TryGetTotalSize(string executable, string searchQuery, CancellationToken cancellationToken, out long totalBytes);
}

[ExcludeFromCodeCoverage]
internal sealed class RealIndexStorageEsExeOps : IIndexStorageEsExeOps
{
    internal static readonly RealIndexStorageEsExeOps Instance = new();

    public string? FindExecutable() => FileLister.FindEsExe();

    public bool TryGetTotalSize(
        string executable,
        string searchQuery,
        CancellationToken cancellationToken,
        out long totalBytes)
    {
        totalBytes = 0;
        ProcessStartInfo startInfo = ResourceUsageMonitor.BuildEsTotalSizeStartInfo(executable, searchQuery);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();

            return process.ExitCode == 0
                && long.TryParse(
                    outputTask.Result.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out totalBytes)
                && totalBytes >= 0;
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }
    }
}

/// <summary>
/// Pure, allocation-light helpers behind the status-bar resource indicators: how much disk space this
/// process's evicted-result temp files and all local content-index data occupy, and how much system RAM
/// Yagu (plus its out-of-process workers) is using out of the machine's installed physical memory. The
/// measurement itself is driven off the UI thread by the view model; index storage is additionally cached
/// and never refreshed during a search. Nothing here touches live <see cref="ResultStore"/> streams.
/// </summary>
internal static class ResourceUsageMonitor
{
    /// <summary>Glob matching the current process's evicted-result temp files (see
    /// <see cref="ResultStore"/>, which names them <c>yagu-results-p{ProcessId}-{Guid}.tmp</c>).</summary>
    internal static string TempResultSearchPatternForProcess(int processId)
        => $"yagu-results-p{processId}-*.tmp";

    /// <summary>
    /// Sums the on-disk byte length of THIS process's evicted-result temp files in
    /// <paramref name="tempDirectory"/> (falling back to the system temp path when null/empty). Reads only
    /// directory metadata — it never opens the files — so it is safe to call while a search is actively
    /// writing to the store. Never throws: any enumeration/metadata error yields the bytes counted so far.
    /// </summary>
    internal static long SumProcessTempResultBytes(string? tempDirectory, int processId)
        => SumProcessTempResultBytes(
            tempDirectory,
            processId,
            static dir => Directory.Exists(dir),
            static (dir, pattern) => Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly),
            static path => new FileInfo(path).Length);

    internal static long SumProcessTempResultBytes(
        string? tempDirectory,
        int processId,
        Func<string, bool> directoryExists,
        Func<string, string, IEnumerable<string>> enumerateFiles,
        Func<string, long> getFileLength)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(enumerateFiles);
        ArgumentNullException.ThrowIfNull(getFileLength);

        string dir = string.IsNullOrWhiteSpace(tempDirectory) ? Path.GetTempPath() : tempDirectory;
        long total = 0;
        try
        {
            if (!directoryExists(dir))
                return 0;
            foreach (string path in enumerateFiles(dir, TempResultSearchPatternForProcess(processId)))
            {
                try { total += getFileLength(path); }
                catch { /* file vanished / locked metadata — skip it */ }
            }
        }
        catch { /* directory disappeared or access denied — return what we have */ }
        return total;
    }

    /// <summary>Formats a byte count as a compact human-readable string (binary units): "0 B", "512 KB",
    /// "1.4 GB". Whole bytes have no decimal; larger units keep one.</summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(long)value} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    /// <summary>The status-bar temp-usage label, e.g. "Temp: 1.4 GB".</summary>
    internal static string FormatTempStatus(long tempBytes) => $"Temp: {FormatBytes(tempBytes)}";

    /// <summary>The tooltip for the temp-usage indicator.</summary>
    internal static string BuildTempTooltip(long tempBytes, string? tempDirectory)
    {
        string dir = string.IsNullOrWhiteSpace(tempDirectory) ? Path.GetTempPath() : tempDirectory;
        return $"Disk space used by Yagu's evicted search results ({FormatBytes(tempBytes)}).\n"
             + "Results are paged to disk under memory pressure and cleaned up when the search is replaced or Yagu exits.\n"
             + $"Location: {dir}";
    }

    /// <summary>
    /// Measures all files under the content-index storage root using the selected file-listing backend.
    /// Auto follows the normal Yagu order (Everything SDK, es.exe, managed); a forced SDK or es.exe backend
    /// uses only that Everything route before the managed fallback. The SDK's process-global state is
    /// serialized with every other Yagu SDK query. If the selected Everything route is unavailable or
    /// incomplete, a cancellation-aware background filesystem metadata scan supplies the fallback.
    /// </summary>
    internal static IndexStorageSizeMeasurement MeasureTotalIndexStorageBytes(
        string? indexRoot,
        FileListerBackend backend,
        CancellationToken cancellationToken)
        => MeasureTotalIndexStorageBytes(
            indexRoot,
            backend,
            RealEverythingSdkOps.Instance,
            RealIndexStorageEsExeOps.Instance,
            static root => Directory.Exists(root),
            SumIndexStorageWithFileSystem,
            cancellationToken);

    internal static IndexStorageSizeMeasurement MeasureTotalIndexStorageBytes(
        string? indexRoot,
        FileListerBackend backend,
        IEverythingSdkOps sdk,
        IIndexStorageEsExeOps esExe,
        CancellationToken cancellationToken)
        => MeasureTotalIndexStorageBytes(
            indexRoot,
            backend,
            sdk,
            esExe,
            static root => Directory.Exists(root),
            SumIndexStorageWithFileSystem,
            cancellationToken);

    internal static IndexStorageSizeMeasurement MeasureTotalIndexStorageBytes(
        string? indexRoot,
        FileListerBackend backend,
        IEverythingSdkOps sdk,
        IIndexStorageEsExeOps esExe,
        Func<string, bool> directoryExists,
        Func<string, CancellationToken, IndexStorageSizeMeasurement> managedMeasurement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(managedMeasurement);

        string root = string.IsNullOrWhiteSpace(indexRoot) ? string.Empty : indexRoot.Trim();
        if (root.Length == 0 || !directoryExists(root))
            return new IndexStorageSizeMeasurement(0, IndexStorageSizeSource.FileSystem, Complete: true);

        if ((backend == FileListerBackend.Auto || backend == FileListerBackend.EverythingSdk)
            && TrySumIndexStorageWithEverything(
                root,
                sdk,
                cancellationToken,
                out long everythingBytes))
        {
            return new IndexStorageSizeMeasurement(
                everythingBytes,
                IndexStorageSizeSource.Everything,
                Complete: true);
        }

        if ((backend == FileListerBackend.Auto || backend == FileListerBackend.EsExe)
            && TrySumIndexStorageWithEsExe(root, esExe, cancellationToken, out long esExeBytes))
        {
            return new IndexStorageSizeMeasurement(
                esExeBytes,
                IndexStorageSizeSource.EsExe,
                Complete: true);
        }

        return managedMeasurement(root, cancellationToken);
    }

    internal static bool TrySumIndexStorageWithEverything(
        string indexRoot,
        IEverythingSdkOps sdk,
        CancellationToken cancellationToken,
        out long totalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        ArgumentNullException.ThrowIfNull(sdk);
        totalBytes = 0;

        lock (sdk.SyncLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!sdk.IsDBLoaded())
                    return false;

                sdk.Reset();
                sdk.SetSearch(BuildEverythingIndexStorageQuery(indexRoot));
                sdk.SetMatchCase(false);
                sdk.SetMatchPath(false);
                sdk.SetOffset(0);
                sdk.SetMax(uint.MaxValue);
                sdk.SetRequestFlags(EverythingSdk.EVERYTHING_REQUEST_SIZE);

                if (!sdk.Query(bWait: true))
                    return false;

                uint returned = sdk.GetNumResults();
                if (returned == 0 || returned != sdk.GetTotResults())
                    return false;

                long measured = 0;
                for (uint i = 0; i < returned; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!sdk.GetResultSize(i, out long size) || size < 0)
                        return false;
                    measured = SaturatingAdd(measured, size);
                }

                totalBytes = measured;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException
                or SEHException)
            {
                return false;
            }
            finally
            {
                TryResetEverything(sdk);
            }
        }
    }

    internal static string BuildEverythingIndexStorageQuery(string indexRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        string normalized = indexRoot.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string path = normalized.Contains(' ')
            ? $"\"{normalized}\""
            : normalized;
        return $"file:{path}";
    }

    internal static bool TrySumIndexStorageWithEsExe(
        string indexRoot,
        IIndexStorageEsExeOps esExe,
        CancellationToken cancellationToken,
        out long totalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        ArgumentNullException.ThrowIfNull(esExe);
        cancellationToken.ThrowIfCancellationRequested();
        totalBytes = 0;

        string? executable = esExe.FindExecutable();
        if (string.IsNullOrWhiteSpace(executable)
            || !esExe.TryGetTotalSize(
                executable,
                BuildEverythingIndexStorageQuery(indexRoot),
                cancellationToken,
                out long measured)
            || measured <= 0)
        {
            return false;
        }

        totalBytes = measured;
        return true;
    }

    internal static ProcessStartInfo BuildEsTotalSizeStartInfo(string executable, string searchQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchQuery);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-get-total-size");
        startInfo.ArgumentList.Add("-size-format");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-no-digit-grouping");
        startInfo.ArgumentList.Add(searchQuery);
        return startInfo;
    }

    internal static IndexStorageSizeMeasurement SumIndexStorageWithFileSystem(
        string indexRoot,
        CancellationToken cancellationToken)
        => SumIndexStorageWithFileSystem(
            indexRoot,
            cancellationToken,
            static root => Directory.Exists(root),
            static root =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };

                return Directory.EnumerateFiles(root, "*", options);
            },
            static path => new FileInfo(path).Length);

    internal static IndexStorageSizeMeasurement SumIndexStorageWithFileSystem(
        string indexRoot,
        CancellationToken cancellationToken,
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateFiles,
        Func<string, long> getFileLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(enumerateFiles);
        ArgumentNullException.ThrowIfNull(getFileLength);

        if (!directoryExists(indexRoot))
            return new IndexStorageSizeMeasurement(0, IndexStorageSizeSource.FileSystem, Complete: true);

        long total = 0;
        bool complete = true;
        try
        {
            foreach (string path in enumerateFiles(indexRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    total = SaturatingAdd(total, getFileLength(path));
                }
                catch (Exception ex) when (ex is FileNotFoundException
                    or DirectoryNotFoundException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or SecurityException)
                {
                    complete = false;
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or SecurityException)
        {
            complete = false;
        }

        return new IndexStorageSizeMeasurement(total, IndexStorageSizeSource.FileSystem, complete);
    }

    /// <summary>The status-bar content-index disk-usage label.</summary>
    internal static string FormatIndexStatus(long indexBytes) => $"Index: {FormatBytes(indexBytes)}";

    internal static string BuildIndexTooltip(IndexStorageSizeMeasurement measurement, string indexRoot)
    {
        string source = measurement.Source switch
        {
            IndexStorageSizeSource.Everything => "Measured through the Everything SDK",
            IndexStorageSizeSource.EsExe => "Measured through es.exe",
            _ => "Measured by a background filesystem metadata scan",
        };
        string completeness = measurement.Complete
            ? string.Empty
            : "\nSome inaccessible or changing files could not be counted; the next refresh will retry.";
        return $"Disk space used by all Yagu content-index data ({FormatBytes(measurement.Bytes)}).\n"
             + "Includes every indexed folder plus retained generations, PDF text, format-v3 structures, and temporary/recovery data.\n"
             + $"{source}; refreshed at most once per minute.{completeness}\n"
             + $"Location: {indexRoot}";
    }

    private static long SaturatingAdd(long total, long value)
        => value > long.MaxValue - total ? long.MaxValue : total + value;

    private static void TryResetEverything(IEverythingSdkOps sdk)
    {
        try
        {
            sdk.Reset();
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or SEHException)
        {
        }
    }

    /// <summary>The used/total RAM percentage (0 when total is unknown).</summary>
    internal static double RamPercent(long usedBytes, long totalBytes)
        => totalBytes > 0 ? Math.Clamp(usedBytes * 100.0 / totalBytes, 0, 100) : 0;

    /// <summary>The status-bar RAM label, e.g. "RAM: 2.4 GB / 64.0 GB (3.8%)".</summary>
    internal static string FormatRamStatus(long usedBytes, long totalBytes)
    {
        if (totalBytes <= 0)
            return $"RAM: {FormatBytes(usedBytes)}";
        return string.Create(CultureInfo.InvariantCulture,
            $"RAM: {FormatBytes(usedBytes)} / {FormatBytes(totalBytes)} ({RamPercent(usedBytes, totalBytes):0.0}%)");
    }

    /// <summary>The tooltip for the RAM indicator: a per-process breakdown followed by the total line.</summary>
    internal static string BuildRamTooltip(IReadOnlyList<(string Name, long Bytes)> breakdown, long usedBytes, long totalBytes)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("System RAM used by Yagu and its worker processes:\n");
        foreach ((string name, long bytes) in breakdown)
            sb.Append(CultureInfo.InvariantCulture, $"  {name}: {FormatBytes(bytes)}\n");
        if (totalBytes > 0)
            sb.Append(CultureInfo.InvariantCulture, $"Total: {FormatBytes(usedBytes)} of {FormatBytes(totalBytes)} ({RamPercent(usedBytes, totalBytes):0.0}%)");
        else
            sb.Append(CultureInfo.InvariantCulture, $"Total: {FormatBytes(usedBytes)}");
        return sb.ToString();
    }

    /// <summary>
    /// The machine's installed physical memory in bytes, preferring the SMBIOS "installed" figure
    /// (<c>GetPhysicallyInstalledSystemMemory</c>, which reports the clean marketing size, e.g. exactly
    /// 64 GB) and falling back to <c>GlobalMemoryStatusEx</c>'s total physical. Returns 0 when neither is
    /// available (the caller then omits the "/ total (%)" suffix).
    /// </summary>
    internal static long GetTotalPhysicalMemoryBytes()
        => GetTotalPhysicalMemoryBytes(
            static () =>
            {
                bool success = GetPhysicallyInstalledSystemMemory(out long kilobytes);
                return (success, kilobytes);
            },
            static () =>
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                bool success = GlobalMemoryStatusEx(ref status);
                return (success, status.ullTotalPhys);
            });

    internal static long GetTotalPhysicalMemoryBytes(
        Func<(bool Success, long Kilobytes)> tryGetInstalledKilobytes,
        Func<(bool Success, ulong TotalPhysicalBytes)> tryGetTotalPhysicalBytes)
    {
        ArgumentNullException.ThrowIfNull(tryGetInstalledKilobytes);
        ArgumentNullException.ThrowIfNull(tryGetTotalPhysicalBytes);

        try
        {
            (bool success, long kilobytes) = tryGetInstalledKilobytes();
            if (success && kilobytes > 0)
                return kilobytes * 1024L;
        }
        catch { /* fall through to GlobalMemoryStatusEx */ }

        try
        {
            (bool success, ulong totalPhysicalBytes) = tryGetTotalPhysicalBytes();
            if (success && totalPhysicalBytes > 0)
                return (long)totalPhysicalBytes;
        }
        catch { /* unknown */ }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
