using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Services;
using System.Globalization;

namespace Yagu;

public sealed partial class App : Application, IDisposable
{
    public static string? StartupDirectory { get; set; }
    public static string? StartupQuery { get; set; }
    public static int? StartupWindowFocusBehavior { get; set; }
    public static Mutex? InstanceMutex { get; set; }
    public static string LaunchWorkingDirectory { get; } = ResolveLaunchWorkingDirectory();
    public static string CrashLogPath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "yagu-crash.log");

    private static string ResolveLaunchWorkingDirectory()
    {
        try
        {
            string currentDirectory = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
                return currentDirectory;
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>UI thread dispatcher. Captured during <see cref="OnLaunched"/>; null until then.
    /// Models (e.g. SearchResult) use this to marshal PropertyChanged events that drive
    /// x:Bind setters which must run on the UI thread.</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UIDispatcher { get; private set; }

    private MainWindow? _window;
    private int _isShowingUnhandledExceptionDialog;

    // Dedupes bug-report offers so a repeating Critical error doesn't pop the modal over and over.
    private readonly Yagu.Services.Telemetry.BugReportThrottle _bugReportThrottle = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Initialize logging from persisted settings
        var settings = new SettingsService().Load();
        LogService.Instance.RotateIfNeeded();
        LogService.InitFromSettings((LogLevel)settings.LogLevelIndex, (LogLevel)settings.ConsoleLogLevelIndex);
        FileLister.Backend = (FileListerBackend)settings.FileListerBackendIndex;
        _ = ResultStore.CleanupOrphanedTempFilesAsync(settings.SearchResultTempDirectory);

        // Purge OCR text left behind by Yagu instances that are no longer running (e.g. a run that
        // exited mid image-search). Best-effort, off the UI thread so it never delays startup.
        _ = Task.Run(static () => Yagu.Services.Ocr.OcrTextCache.Cleanup());

        // Eagerly load System.IO.Compression on a background thread so the
        // first archive search doesn't pay the ~20 ms assembly-load + JIT cost.
        Task.Run(ZipArchiveSearcher.WarmUp);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (StartupDirectory is null)
                StartupDirectory = ParseDirArg(System.Environment.GetCommandLineArgs());
            _window = new MainWindow(StartupDirectory, StartupQuery, StartupWindowFocusBehavior);
            UIDispatcher = _window.DispatcherQueue;

            // Warn before any external OCR download (engine runtime / models / language data). The gate
            // is invoked from a background OCR-init thread; the dialog marshals to the UI thread itself.
            Yagu.Services.Ocr.OcrDownloadGate.PromptAsync =
                requirement => OcrDownloadConsentDialog.RequestConsentAsync(_window, requirement);

            Models.SearchResult.HydrationDispatcher = action =>
            {
                var dispatcher = UIDispatcher;
                if (dispatcher is null || dispatcher.HasThreadAccess)
                    action();
                else
                    dispatcher.TryEnqueue(() => action());
            };
            _window.Activate();
            _window.FocusSearchOnLaunch();

            // Route Critical log entries (including unhandled-exception crash logs) to the optional,
            // consent-gated telemetry + bug-report subsystems. Both calls self-gate on user consent.
            LogService.Instance.CriticalLogged += OnCriticalLogged;

            // First-run telemetry/bug-report consent ("Help improve Yagu?") is sequenced into MainWindow's
            // OnContentLoaded startup-modal chain (ShowTelemetryConsentIfNeededAsync) rather than fired here,
            // so it never stacks on top of another first-run prompt (e.g. the result-store temp-location
            // modal) - only one startup modal is shown at a time.
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            ShowUnhandledExceptionMessageBox("OnLaunched", ex);
            throw;
        }
    }

    public void Dispose()
    {
        UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        LogService.Instance.CriticalLogged -= OnCriticalLogged;
        Yagu.Services.Telemetry.TelemetryService.Instance.Shutdown();
        Models.SearchResult.HydrationDispatcher = null;
        _window?.Dispose();
        _window = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Handles every Critical log entry (crashes included). Records a scrubbed telemetry error
    /// when telemetry consent is on, and offers the (reviewed) bug-report dialog when bug-reporting
    /// consent is on — throttled so a repeating fault doesn't spam the user. Never throws.</summary>
    private void OnCriticalLogged(string source, string message, Exception? ex)
    {
        try
        {
            Yagu.Services.Telemetry.TelemetryService.Instance.TrackError("Critical:" + source, ex);

            if (!Yagu.Services.Telemetry.TelemetryGate.ShouldOfferBugReport)
                return;

            string scrubbed = Yagu.Services.Telemetry.TelemetryScrubber.Scrub(ex?.Message ?? message);
            string signature = Yagu.Services.Telemetry.BugReportThrottle.Signature(
                source, ex?.GetType().FullName ?? "none", scrubbed);
            if (_bugReportThrottle.ShouldOffer(signature))
                BugReportDialog.Offer(_window, source, ex);
        }
        catch { /* telemetry/bug-report must never destabilize logging or the app */ }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("UnhandledException", e.Exception);
        e.Handled = TryShowUnhandledExceptionDialog("UnhandledException", e.Exception);
        if (!e.Handled)
            ShowUnhandledExceptionMessageBox("UnhandledException", e.Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTaskException", e.Exception);
        e.SetObserved();

        var app = Current as App;
        if (app?.TryShowUnhandledExceptionDialog("UnobservedTaskException", e.Exception) != true)
            ShowUnhandledExceptionMessageBox("UnobservedTaskException", e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        LogCrash("AppDomain.UnhandledException", exception);
        ShowUnhandledExceptionMessageBox("AppDomain.UnhandledException", exception);
    }

    private bool TryShowUnhandledExceptionDialog(string source, Exception? exception)
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
            return false;

        if (Interlocked.Exchange(ref _isShowingUnhandledExceptionDialog, 1) == 1)
            return true;

        if (dispatcher.TryEnqueue(() => _ = ShowUnhandledExceptionDialogAsync(source, exception)))
            return true;

        Interlocked.Exchange(ref _isShowingUnhandledExceptionDialog, 0);
        return false;
    }

    private async Task ShowUnhandledExceptionDialogAsync(string source, Exception? exception)
    {
        try
        {
            if (_window?.Content is not FrameworkElement root || root.XamlRoot is null)
            {
                ShowUnhandledExceptionMessageBox(source, exception);
                return;
            }

            var detailsBlock = new TextBlock
            {
                Text = BuildUnhandledExceptionDetails(source, exception, maxExceptionChars: 64 * 1024),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
            };

            var detailsViewer = new ScrollViewer
            {
                Content = detailsBlock,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 240,
                MaxHeight = 420,
                Width = 640,
            };

            var content = new StackPanel
            {
                Spacing = 8,
                Width = 640,
            };
            content.Children.Add(new TextBlock
            {
                Text = "Yagu caught an unexpected error. The details were also written to the crash log.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = CrashLogPath,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.7,
            });
            content.Children.Add(detailsViewer);

            await YaguDialog.ShowAsync(
                WindowForegroundHelper.GetWindowHandle(_window),
                new YaguDialogOptions
                {
                    Title = "Unexpected error",
                    TitleGlyph = "\uEA39", // Error badge
                    Content = content,
                    CloseButtonText = "Close",
                    DefaultButton = YaguDialogDefaultButton.Close,
                    Width = 720,
                    Height = 620,
                    MaxContentHeight = 460,
                    IsResizable = true,
                    ShowTitleBar = false,
                });
        }
        catch
        {
            ShowUnhandledExceptionMessageBox(source, exception);
        }
        finally
        {
            Interlocked.Exchange(ref _isShowingUnhandledExceptionDialog, 0);
        }
    }

    private static string BuildUnhandledExceptionDetails(string source, Exception? exception, int maxExceptionChars)
    {
        var exceptionText = exception?.ToString() ?? "No exception object was provided.";
        if (maxExceptionChars > 0 && exceptionText.Length > maxExceptionChars)
            exceptionText = exceptionText[..maxExceptionChars] + Environment.NewLine + "... truncated; see the crash log for the full exception.";

        var details = new StringBuilder();
        details.AppendLine(CultureInfo.InvariantCulture, $"Source: {source}");
        details.AppendLine(CultureInfo.InvariantCulture, $"Time (UTC): {DateTime.UtcNow:O}");
        details.AppendLine(CultureInfo.InvariantCulture, $"Crash log: {CrashLogPath}");
        details.AppendLine();
        details.AppendLine(exceptionText);
        return details.ToString();
    }

    private static void ShowUnhandledExceptionMessageBox(string source, Exception? exception)
    {
        try
        {
            var details = BuildUnhandledExceptionDetails(source, exception, maxExceptionChars: 8000);
            _ = MessageBoxW(IntPtr.Zero, details, "Yagu unexpected error", 0x00000010 | 0x00002000);
        }
        catch { /* last resort - the process may already be terminating */ }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    internal static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var msg = $"[{DateTime.UtcNow:O}] {source}: {ex}\n";
            File.AppendAllText(CrashLogPath, msg);
            LogService.Instance.Critical("App", $"{source}: {ex?.Message}", ex);
            LogService.Instance.Flush();
        }
        catch { /* last resort — nothing we can do */ }
    }

    internal static string? ParseDirArg(string[] args) => ParseStringArg(args, "--dir");

    internal static int? ParseWindowFocusBehaviorArg(string[] args)
    {
        var value = ParseStringArg(args, "--window-mode")
            ?? ParseStringArg(args, "--windowing-mode")
            ?? ParseStringArg(args, "--window-focus-behavior");
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim().Trim('"').ToLowerInvariant().Replace("_", "-");
        return normalized switch
        {
            "0" or "minimize" or "minimize-to-tray" or "tray" => 0,
            "1" or "stay-open" or "stayopen" or "open" => 1,
            "2" or "always-on-top" or "alwaysontop" or "top" => 2,
            "3" or "traditional" or "traditional-window" or "desktop" or "full-window" or "fullwindow" => 3,
            _ => null,
        };
    }

    internal static string? ParseStringArg(string[] args, string name)
    {
        if (args is null) return null;
        var prefix = name + "=";
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, name, System.StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim().Trim('"');
            if (a.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return a[prefix.Length..].Trim().Trim('"');
        }
        return null;
    }
}
