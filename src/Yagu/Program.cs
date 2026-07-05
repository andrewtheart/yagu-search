using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WinRT;

namespace Yagu;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("user32.dll")]   private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")]   private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")]   private static extern bool IsWindowVisible(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private const int SW_RESTORE = 9;
    private const int SW_HIDE = 0;
    private const string GuiChildFlag = "--yagu-gui-child";

    /// <summary>
    /// Custom entry point that intercepts <c>--cli</c> before any WinUI3 initialisation
    /// so the process can run headlessly against the parent console.
    /// </summary>
    [global::System.STAThread]
    private static void Main(string[] args)
    {
        bool isGuiChild = ConsumeGuiChildFlag(ref args);

        bool isCli = args.Any(a => string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase))
                  || args.Any(a => IsHelpFlag(a));

        // Hide the console window immediately for GUI launches to prevent a brief flash.
        if (!isCli)
        {
            nint consoleWnd = GetConsoleWindow();
            if (consoleWnd != 0)
                ShowWindow(consoleWnd, SW_HIDE);
        }

        // Help flags: print CLI help and exit without launching GUI.
        if (args.Any(a => IsHelpFlag(a)))
        {
            CliRunner.RunHelp();
            Environment.Exit(0);
            return;
        }

        // CLI mode: runs entirely without the WinUI runtime.
        if (args.Any(a => string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode = CliRunner.Run(args);
            Environment.Exit(exitCode);
            return;
        }

        if (!isGuiChild && TryRelaunchDetachedGui(args))
            return;

        FreeConsole();

        // If launched as part of an elevated relaunch, wait for the old (non-elevated)
        // process to fully exit so its single-instance mutex is released.
        WaitForPredecessorExit(args);

        // Enforce single instance via a named mutex. Retry briefly to absorb the small
        // race window where a relaunch (e.g. "Restart as administrator") is still
        // releasing the mutex from the previous process.
        bool createdNew = false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            App.InstanceMutex = new Mutex(true, @"Global\YaguSingleInstance", out createdNew);
            if (createdNew) break;
            App.InstanceMutex.Dispose();
            App.InstanceMutex = null;
            Thread.Sleep(150);
        }
        if (!createdNew)
        {
            // Another instance already owns the mutex — activate it and exit.
            ActivateExistingInstance();
            return;
        }

        // Pass any --dir / --query arguments so App.OnLaunched can pick them up.
        App.StartupDirectory = App.ParseDirArg(args);
        App.StartupQuery = App.ParseStringArg(args, "--query");
        App.StartupWindowFocusBehavior = App.ParseWindowFocusBehaviorArg(args);

        // Normal GUI mode — mirrors the WinUI-generated entry point.
        ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool ConsumeGuiChildFlag(ref string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, GuiChildFlag, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        args = args.Where((_, i) => i != index).ToArray();
        return true;
    }

    private static bool TryRelaunchDetachedGui(string[] args)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            psi.ArgumentList.Add(GuiChildFlag);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Process.Start(psi)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find the existing Yagu process's main window and bring it to the foreground.
    /// </summary>
    private static void ActivateExistingInstance()
    {
        int currentPid = Environment.ProcessId;
        try
        {
            foreach (var proc in Process.GetProcessesByName("Yagu"))
            {
                if (proc.Id == currentPid) { proc.Dispose(); continue; }

                // Find a visible top-level window belonging to the other Yagu process.
                nint targetHwnd = 0;
                uint targetPid = (uint)proc.Id;
                proc.Dispose();

                EnumWindows((hWnd, _) =>
                {
                    uint _threadId = GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == targetPid && IsWindowVisible(hWnd))
                    {
                        targetHwnd = hWnd;
                        return false; // stop enumeration
                    }
                    return true;
                }, 0);

                if (targetHwnd != 0)
                {
                    ShowWindow(targetHwnd, SW_RESTORE);
                    SetForegroundWindow(targetHwnd);
                    return;
                }
            }
        }
        catch
        {
            // Best-effort — if we can't find/activate the window, just exit quietly.
        }
    }

    /// <summary>
    /// If the command line includes <c>--wait-for-pid &lt;n&gt;</c>, block until that
    /// process has exited (with a generous timeout). Used by elevated relaunch to
    /// avoid racing the predecessor's single-instance mutex.
    /// </summary>
    private static void WaitForPredecessorExit(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--wait-for-pid", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(args[i + 1], out int pid)) return;
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.WaitForExit(5000);
            }
            catch
            {
                // Process already gone — nothing to wait for.
            }
            return;
        }
    }

    private static bool IsHelpFlag(string arg) =>
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "-help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "-?", StringComparison.Ordinal) ||
        string.Equals(arg, "/?", StringComparison.Ordinal) ||
        string.Equals(arg, "?", StringComparison.Ordinal) ||
        string.Equals(arg, "/help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "/h", StringComparison.OrdinalIgnoreCase);
}
