using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Yagu.Services;

namespace Yagu.Services;

/// <summary>
/// Launches a configurable external editor at a specific file/line.
/// </summary>
public sealed class EditorLauncher
{
    public const string DefaultCommand = "code --goto \"{file}:{line}\"";

    public string Command { get; set; } = DefaultCommand;

    /// <summary>Test seam: when set, replaces Process.Start calls.</summary>
    internal static Action<ProcessStartInfo>? TestProcessLauncher;


    private static void LaunchProcess(ProcessStartInfo psi)
    {
        if (TestProcessLauncher != null) TestProcessLauncher(psi);
        else Process.Start(psi);
    }

    public bool Open(string filePath, int line)
    {
        // For archive entries, extract to a temp file first
        string actualPath = filePath;
        if (ZipArchiveSearcher.IsArchivePath(filePath))
        {
            try
            {
                actualPath = ZipArchiveSearcher.ExtractToTempFileAsync(filePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("EditorLauncher", $"Failed to extract archive entry for editing: {filePath}", ex);
                return false;
            }
        }

        var rendered = Command.Replace("{file}", actualPath).Replace("{line}", line.ToString());
        var (exe, args) = Split(rendered);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
            };
            LaunchProcess(psi);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("EditorLauncher", $"Failed to open editor for {filePath}:{line}", ex);
            return false;
        }
    }

    public static bool OpenContainingFolder(string filePath)
    {
        try
        {
            // For archive entries, open the folder containing the archive itself
            string physicalPath = ZipArchiveSearcher.IsArchivePath(filePath)
                ? ZipArchiveSearcher.SplitArchivePath(filePath).ArchivePath
                : filePath;
            var dir = Path.GetDirectoryName(physicalPath);
            if (string.IsNullOrEmpty(dir)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{physicalPath}\"",
                UseShellExecute = true,
            };
            LaunchProcess(psi);
            return true;
        }
        catch (Exception ex) { LogService.Instance.Warning("EditorLauncher", $"Failed to open folder for {filePath}", ex); return false; }
    }


    public static bool OpenTerminalAt(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{dir}\"",
                UseShellExecute = true,
            };
            LaunchProcess(psi);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("EditorLauncher", "wt.exe not available, trying powershell", ex);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = Path.GetDirectoryName(filePath) ?? ".",
                    UseShellExecute = true,
                };
                LaunchProcess(psi);
                return true;
            }
            catch (Exception ex2) { LogService.Instance.Warning("EditorLauncher", "Failed to open terminal", ex2); return false; }
        }
    }

    internal static (string exe, string args) Split(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return ("", "");
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int close = command.IndexOf('"', 1);
            if (close > 0)
            {
                var exe = command.Substring(1, close - 1);
                var rest = command.Substring(close + 1).TrimStart();
                return (exe, rest);
            }
        }
        var space = command.IndexOf(' ');
        if (space < 0) return (command, "");
        return (command[..space], command[(space + 1)..]);
    }
}
