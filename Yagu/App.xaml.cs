using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Services;

namespace Yagu;

public partial class App : Application
{
    public static string? StartupDirectory { get; set; }
    public static string? StartupQuery { get; set; }
    public static bool AnotherInstanceDetected { get; set; }
    public static Mutex? InstanceMutex { get; set; }
    public static string CrashLogPath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "yagu-crash.log");
    private MainWindow? _window;
    private int _isShowingUnhandledExceptionDialog;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Initialize logging from persisted settings
        var settings = new SettingsService().Load();
        LogService.Instance.RotateIfNeeded();
        LogService.Init((LogLevel)settings.LogLevelIndex);
        FileLister.Backend = (FileListerBackend)settings.FileListerBackendIndex;
        _ = ResultStore.CleanupOrphanedTempFilesAsync();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (StartupDirectory is null)
                StartupDirectory = ParseDirArg(System.Environment.GetCommandLineArgs());
            _window = new MainWindow(StartupDirectory, StartupQuery);
            _window.Activate();

            if (AnotherInstanceDetected)
                _ = ShowMultiInstanceDialogAsync();
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            ShowUnhandledExceptionMessageBox("OnLaunched", ex);
            throw;
        }
    }

    private async Task ShowMultiInstanceDialogAsync()
    {
        try
        {
            if (_window?.Content is not FrameworkElement root || root.XamlRoot is null)
                return;

            var dontRemindCheckBox = new CheckBox { Content = "Don't remind me again" };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "Another instance of Yagu is already running. Do you want to run a second instance?",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(dontRemindCheckBox);

            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Yagu is already running",
                Content = content,
                PrimaryButtonText = "Run anyway",
                CloseButtonText = "Exit",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();

            if (dontRemindCheckBox.IsChecked == true)
            {
                var settingsService = new SettingsService();
                var settings = settingsService.Load();
                settings.SuppressMultiInstanceWarning = true;
                settingsService.Save(settings);
            }

            if (result != ContentDialogResult.Primary)
            {
                _window?.Close();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("App", "Failed to show multi-instance dialog", ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("UnhandledException", e.Exception);
        e.Handled = TryShowUnhandledExceptionDialog("UnhandledException", e.Exception);
        if (!e.Handled)
            ShowUnhandledExceptionMessageBox("UnhandledException", e.Exception);
    }

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
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

            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Unexpected error",
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync();
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
        details.AppendLine($"Source: {source}");
        details.AppendLine($"Time (UTC): {DateTime.UtcNow:O}");
        details.AppendLine($"Crash log: {CrashLogPath}");
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
