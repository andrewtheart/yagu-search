using System.Diagnostics;

namespace Yagu.Services;

/// <summary>
/// Explicit-consent wrapper around Everything's documented live configuration commands. Unlike editing
/// Everything.ini, <c>-add-volumes</c> is safely forwarded to the running Everything instance; <c>-rescan</c>
/// starts/refreshes FAT, network-drive, and folder indexes. Yagu never calls this automatically.
/// </summary>
internal static class EverythingIndexConfigurator
{
    internal static Func<ProcessStartInfo, Process?> ProcessStarter { get; set; } = Process.Start;
    internal static Func<Process, CancellationToken, Task> WaitForExitAsyncCore { get; set; }
        = static (process, cancellationToken) => process.WaitForExitAsync(cancellationToken);
    internal static Func<Process, int> ReadExitCode { get; set; } = static process => process.ExitCode;

    internal static IReadOnlyList<string> NormalizeRootVolumes(IEnumerable<string> paths)
    {
        var roots = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                string? root = Path.GetPathRoot(path.Replace('/', '\\'));
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                string display = root.Length >= 2 && root[1] == ':' ? root[..2] : root.TrimEnd('\\');
                if (!roots.Contains(display, StringComparer.OrdinalIgnoreCase))
                    roots.Add(display);
            }
            catch { /* malformed target — skip */ }
        }
        return roots;
    }

    internal static Task<bool> AddVolumesAndRescanAsync(
        string everythingExePath,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
        => AddVolumesAndRescanAsync(
            everythingExePath,
            paths,
            fileExists: File.Exists,
            runAsync: RunAsync,
            cancellationToken);

    internal static async Task<bool> AddVolumesAndRescanAsync(
        string everythingExePath,
        IEnumerable<string> paths,
        Func<string, bool> fileExists,
        Func<ProcessStartInfo, CancellationToken, Task<bool>> runAsync,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> roots = NormalizeRootVolumes(paths);
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(everythingExePath) || !fileExists(everythingExePath))
            return false;

        try
        {
            var add = new ProcessStartInfo(everythingExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            add.ArgumentList.Add("-add-volumes");
            add.ArgumentList.Add(string.Join(';', roots));
            if (!await runAsync(add, cancellationToken).ConfigureAwait(false))
                return false;

            // Adding a volume schedules indexing. Ask FAT/folder indexes to start/rescan immediately as
            // well; for NTFS/ReFS this is harmless and Everything can ignore an inapplicable rescan.
            foreach (string root in roots)
            {
                var rescan = new ProcessStartInfo(everythingExePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                rescan.ArgumentList.Add("-rescan");
                rescan.ArgumentList.Add(root + "\\");
                if (!await runAsync(rescan, cancellationToken).ConfigureAwait(false))
                    return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task<bool> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using Process? process = ProcessStarter(startInfo);
        if (process is null)
            return false;
        await WaitForExitAsyncCore(process, cancellationToken).ConfigureAwait(false);
        return ReadExitCode(process) == 0;
    }
}
