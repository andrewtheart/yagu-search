using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WinRT;
using Yagu;
using Yagu.Services;

namespace Yagu;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("user32.dll")]   private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    /// <summary>
    /// Custom entry point that intercepts <c>--cli</c> before any WinUI3 initialisation
    /// so the process can run headlessly against the parent console.
    /// </summary>
    [global::System.STAThread]
    private static void Main(string[] args)
    {
        // CLI mode: runs entirely without the WinUI runtime.
        if (args.Any(a => string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode = CliRunner.Run(args);
            Environment.Exit(exitCode);
            return;
        }

        // GUI mode: hide the console window that Exe output type creates.
        var con = GetConsoleWindow();
        if (con != 0) ShowWindow(con, SW_HIDE);

        // Detect if another instance is already running.
        bool anotherInstanceRunning = !Mutex.TryOpenExisting("Global\\YaguSingleInstance", out _);
        if (anotherInstanceRunning)
        {
            // We are the first — create and hold the mutex for our lifetime.
            App.InstanceMutex = new Mutex(true, "Global\\YaguSingleInstance", out _);
        }
        else
        {
            // Another instance already owns the mutex.
            var settings = new SettingsService().Load();
            if (!settings.SuppressMultiInstanceWarning)
                App.AnotherInstanceDetected = true;
        }

        // Pass any --dir / --query arguments so App.OnLaunched can pick them up.
        App.StartupDirectory = App.ParseDirArg(args);
        App.StartupQuery = App.ParseStringArg(args, "--query");

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
}
