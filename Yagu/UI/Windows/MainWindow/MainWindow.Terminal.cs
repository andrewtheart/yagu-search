using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Yagu.Services;

namespace Yagu;

public sealed partial class MainWindow
{
    private ConPtyTerminalService? _terminalService;
    private bool _terminalPaneExpanded;
    private bool _terminalInitialized;
    private int _terminalColumns = 120;
    private int _terminalRows = 24;
    private int _terminalSessionGeneration;
    private bool _terminalWebViewDiagnosticsAttached;
    private bool _terminalLoggedFirstOutputPost;
    private bool _terminalLoggedFirstInput;
    private bool _terminalStartupPromptNudged;
    private readonly StringBuilder _terminalCurrentInputLine = new();
    private readonly TaskCompletionSource<bool> _terminalReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _terminalShellReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnToggleTerminalPane(object sender, RoutedEventArgs e)
    {
        SetTerminalPaneExpanded(!_terminalPaneExpanded);

        if (_terminalPaneExpanded && !_terminalInitialized)
            _ = InitializeTerminalAsync();
    }

    private void SetTerminalPaneExpanded(bool expanded)
    {
        _terminalPaneExpanded = expanded;

        if (_terminalPaneExpanded)
        {
            TerminalRow.Height = new GridLength(250);
            TerminalHost.Visibility = Visibility.Visible;
        }
        else
        {
            TerminalRow.Height = new GridLength(0);
            TerminalHost.Visibility = Visibility.Collapsed;
        }

        UpdateTerminalChevronGlyphs();
        UpdateTerminalChevronVisibility();

        if (_launcherMode)
            PositionLauncherWindow();
    }

    private void UpdateTerminalChevronGlyphs()
    {
        // In this app's icon font, \uE70E renders as an up-pointing chevron and
        // \uE70D as a down-pointing chevron. Show up when expanded, down when collapsed.
        string glyph = _terminalPaneExpanded ? "\uE70E" : "\uE70D";
        TerminalChevronIcon.Glyph = glyph;
        PreSearchTerminalChevronIcon.Glyph = glyph;
    }

    private void UpdateTerminalChevronVisibility()
    {
        bool statusBarChevronVisible = StatusBarRow.Height.IsAuto || StatusBarRow.Height.Value > 0;
        TerminalChevron.Visibility = statusBarChevronVisible ? Visibility.Visible : Visibility.Collapsed;
        PreSearchTerminalChevron.Visibility = statusBarChevronVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task SendTextToTerminalAsync(string text)
    {
        string commandText = text.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(commandText)) return;

        await EnsureTerminalPaneExpandedAsync();
        await WaitForTerminalReadyAsync();

        StartConPtySession();
        await WaitForTerminalShellReadyAsync();
        await ChangeTerminalDirectoryToYaguExecutableDirectoryAsync();
        _terminalService?.WriteInput(commandText);
        FocusTerminal();
    }

    private async Task ChangeTerminalDirectoryToYaguExecutableDirectoryAsync()
    {
        if (_terminalService is null)
            return;

        string executableDirectory = ResolveYaguExecutableDirectoryForTerminalCommand();
        _terminalShellReadyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _terminalService.WriteInput($"cd /d {QuoteCmdArgument(executableDirectory)}\r", echoInput: false);
        await WaitForTerminalShellReadyAsync();
    }

    private static string ResolveYaguExecutableDirectoryForTerminalCommand()
    {
        string? processDirectory = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Path.GetDirectoryName(Environment.ProcessPath)
            : null;

        if (TryResolveExistingDirectory(processDirectory, out string executableDirectory))
            return executableDirectory;

        if (TryResolveExistingDirectory(AppContext.BaseDirectory, out string appDirectory))
            return appDirectory;

        return AppContext.BaseDirectory;
    }

    private static string QuoteCmdArgument(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private async Task EnsureTerminalPaneExpandedAsync()
    {
        SetTerminalPaneExpanded(true);
        TerminalWebView.UpdateLayout();

        if (!_terminalInitialized)
            await InitializeTerminalAsync();

        SetTerminalPaneExpanded(true);
        TerminalWebView.UpdateLayout();
    }

    private async Task WaitForTerminalReadyAsync()
    {
        if (_terminalReadyCompletion.Task.IsCompleted)
            return;

        try
        {
            await _terminalReadyCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Timed out waiting for terminal readiness: {ex.Message}");
        }
    }

    private async Task WaitForTerminalShellReadyAsync()
    {
        if (_terminalShellReadyCompletion.Task.IsCompleted)
            return;

        try
        {
            await _terminalShellReadyCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Timed out waiting for terminal shell readiness: {ex.Message}");
        }
    }

    private void FocusTerminal()
    {
        TerminalWebView.Focus(FocusState.Programmatic);
        TerminalWebView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"focus\"}");
    }

    private async Task InitializeTerminalAsync()
    {
        try
        {
            LogService.Instance.Info("Terminal", "Initializing terminal WebView");
            EnsureWebView2LoaderLoaded();

            var env = await CoreWebView2Environment.CreateAsync();
            await TerminalWebView.EnsureCoreWebView2Async(env);

            // Allow local file access for xterm.js/css
            string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "yagu-terminal", assetsDir, CoreWebView2HostResourceAccessKind.Allow);

            // Also map the root output dir for xterm.js files placed beside the exe
            TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "yagu-assets", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);

            AttachTerminalWebViewDiagnostics(TerminalWebView.CoreWebView2);
            TerminalWebView.CoreWebView2.WebMessageReceived += OnTerminalWebMessage;

            string terminalHtmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal.html");
            if (!File.Exists(terminalHtmlPath))
                LogService.Instance.Warning("Terminal", $"terminal.html was not found at {terminalHtmlPath}; navigating via virtual host anyway.");

            LogService.Instance.Info("Terminal", $"Navigating terminal WebView to https://yagu-terminal/terminal.html from assets {assetsDir}");
            TerminalWebView.CoreWebView2.Navigate("https://yagu-terminal/terminal.html");

            _terminalInitialized = true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Terminal", "Terminal WebView initialization failed", ex);
            System.Diagnostics.Debug.WriteLine($"Terminal init failed: {ex}");
        }
    }

    private void AttachTerminalWebViewDiagnostics(CoreWebView2 coreWebView)
    {
        if (_terminalWebViewDiagnosticsAttached) return;
        _terminalWebViewDiagnosticsAttached = true;

        coreWebView.NavigationCompleted += OnTerminalNavigationCompleted;
        coreWebView.ProcessFailed += OnTerminalWebViewProcessFailed;
        coreWebView.WebResourceResponseReceived += OnTerminalWebResourceResponseReceived;
    }

    private void OnTerminalNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            LogService.Instance.Info("Terminal", "Terminal WebView navigation completed successfully");
            return;
        }

        LogService.Instance.Warning("Terminal", $"Terminal WebView navigation failed: {args.WebErrorStatus}");
    }

    private void OnTerminalWebViewProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        LogService.Instance.Warning("Terminal", $"Terminal WebView process failed: {args.ProcessFailedKind}");
    }

    private void OnTerminalWebResourceResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            string uri = args.Request.Uri ?? string.Empty;
            if (!uri.Contains("yagu-terminal", StringComparison.OrdinalIgnoreCase))
                return;

            int statusCode = args.Response.StatusCode;
            if (statusCode >= 400)
                LogService.Instance.Warning("Terminal", $"Terminal WebView resource failed: {statusCode} {uri}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Terminal", "Failed while reading terminal WebView resource response", ex);
        }
    }

    private void OnTerminalWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string json = args.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";

            switch (type)
            {
                case "ready":
                    LogService.Instance.Info("Terminal", "Terminal page reported ready");
                    StartConPtySession();
                    _terminalReadyCompletion.TrySetResult(true);
                    if (_terminalPaneExpanded)
                        FocusTerminal();
                    break;
                case "input":
                    string data = root.GetProperty("data").GetString() ?? "";
                    LogTerminalInput(data);
                    bool shouldClearScrollback = TrackTerminalInputForClearCommand(data);
                    if (_terminalService is null)
                    {
                        LogService.Instance.Warning("Terminal", "Terminal input received before the shell session was available; starting a terminal session.");
                        StartConPtySession();
                    }
                    _terminalService?.WriteInput(data);
                    if (shouldClearScrollback)
                        ScheduleTerminalScrollbackClear();
                    break;
                case "resize":
                    _terminalColumns = Math.Max(1, root.GetProperty("cols").GetInt32());
                    _terminalRows = Math.Max(1, root.GetProperty("rows").GetInt32());
                    _terminalService?.Resize(_terminalColumns, _terminalRows);
                    break;
                case "resetTerminal":
                    ResetTerminalSession();
                    break;
                case "copyText":
                    CopyTextToClipboard(root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty);
                    break;
                case "requestPaste":
                    PasteClipboardTextToTerminal();
                    break;
                case "openHelp":
                    OpenHelpWindow();
                    break;
                case "hostLog":
                    LogTerminalPageMessage(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Terminal", $"Failed to process terminal web message: {json}", ex);
        }
    }

    private static void LogTerminalPageMessage(JsonElement root)
    {
        string level = root.TryGetProperty("level", out var levelElement) ? levelElement.GetString() ?? "info" : "info";
        string message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
            LogService.Instance.Warning("Terminal", message);
        else
            LogService.Instance.Info("Terminal", message);
    }

    private void StartConPtySession()
    {
        if (_terminalService is not null) return;

        int sessionGeneration = ++_terminalSessionGeneration;
        _terminalShellReadyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _terminalStartupPromptNudged = false;
        _terminalLoggedFirstOutputPost = false;
        _terminalLoggedFirstInput = false;
        var terminalService = new ConPtyTerminalService();
        terminalService.OutputReceived += text => OnTerminalOutput(text, sessionGeneration);
        terminalService.ProcessExited += exitCode => OnTerminalProcessExited(exitCode, sessionGeneration);
        _terminalService = terminalService;
        try
        {
            string workingDirectory = ResolveTerminalWorkingDirectory();
            LogService.Instance.Info("Terminal", $"Starting terminal shell session: cols={_terminalColumns}, rows={_terminalRows}, cwd='{workingDirectory}'");
            terminalService.Start(cols: _terminalColumns, rows: _terminalRows, workingDirectory: workingDirectory);
            LogService.Instance.Info("Terminal", $"Terminal shell session started: shellPid={terminalService.ProcessId}");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_terminalService, terminalService))
                _terminalService = null;
            terminalService.Dispose();
            LogService.Instance.Warning("Terminal", "Failed to start terminal shell session", ex);
            OnTerminalOutput($"\r\n\x1b[91m[Terminal failed to start: {ex.Message}]\x1b[0m\r\n", sessionGeneration);
        }
    }

    private void ResetTerminalSession()
    {
        _terminalCurrentInputLine.Clear();
        DisposeTerminal();
        StartConPtySession();
    }

    private bool TrackTerminalInputForClearCommand(string data)
    {
        bool shouldClearScrollback = false;

        foreach (char c in data)
        {
            switch (c)
            {
                case '\r':
                case '\n':
                    if (IsTerminalClearCommand(_terminalCurrentInputLine.ToString()))
                        shouldClearScrollback = true;
                    _terminalCurrentInputLine.Clear();
                    break;
                case '\b':
                case '\u007f':
                    if (_terminalCurrentInputLine.Length > 0)
                        _terminalCurrentInputLine.Length--;
                    break;
                case '\u0003':
                case '\u001b':
                    _terminalCurrentInputLine.Clear();
                    break;
                default:
                    if (!char.IsControl(c))
                        _terminalCurrentInputLine.Append(c);
                    break;
            }
        }

        return shouldClearScrollback;
    }

    private static bool IsTerminalClearCommand(string line)
        => string.Equals(line.Trim(), "cls", StringComparison.OrdinalIgnoreCase);

    private void ScheduleTerminalScrollbackClear()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(120);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PostTerminalClearToWebView();
        };
        timer.Start();
    }

    private static void CopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Terminal", "Failed to copy terminal selection to clipboard", ex);
        }
    }

    private async void PasteClipboardTextToTerminal()
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;

            string text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text))
                return;

            if (_terminalService is null)
                StartConPtySession();

            _terminalService?.WriteInput(text);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Terminal", "Failed to paste clipboard text into terminal", ex);
        }
    }

    private string ResolveTerminalWorkingDirectory()
    {
        if (TryResolveExistingDirectory(ViewModel.TerminalDefaultWorkingDirectory, out string configuredDirectory))
            return configuredDirectory;

        if (TryResolveExistingDirectory(App.LaunchWorkingDirectory, out string launchDirectory))
            return launchDirectory;

        return AppContext.BaseDirectory;
    }

    private static bool TryResolveExistingDirectory(string? value, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(expanded))
                return false;

            string fullPath = Path.GetFullPath(expanded);
            if (!Directory.Exists(fullPath))
                return false;

            directory = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnTerminalOutput(string text, int sessionGeneration)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (sessionGeneration != _terminalSessionGeneration) return;
                if (TerminalWebView.CoreWebView2 is null) return;
                string terminalText = FilterTerminalOutputForXterm(text);
                if (string.IsNullOrEmpty(terminalText))
                {
                    NudgeCommandShellPromptAfterStartupControlPacket(text);
                    return;
                }
                PostTerminalOutputToWebView(terminalText);
                if (ContainsPrintableShellText(terminalText))
                    _terminalShellReadyCompletion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Terminal", "Terminal output post failed", ex);
                System.Diagnostics.Debug.WriteLine($"Terminal output error: {ex.Message}");
            }
        });
    }


    private static bool ContainsPrintableShellText(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsControl(c) && !char.IsWhiteSpace(c))
                return true;
        }

        return false;
    }

    private static string FilterTerminalOutputForXterm(string text)
    {
        return text
            .Replace("\u001b[?9001h", string.Empty, StringComparison.Ordinal)
            .Replace("\u001b[?1004h", string.Empty, StringComparison.Ordinal);
    }

    private void NudgeCommandShellPromptAfterStartupControlPacket(string originalText)
    {
        if (_terminalStartupPromptNudged) return;
        if (!originalText.Contains("\u001b[?9001h", StringComparison.Ordinal) &&
            !originalText.Contains("\u001b[?1004h", StringComparison.Ordinal))
            return;

        _terminalStartupPromptNudged = true;
        LogService.Instance.Info("Terminal", "Sending startup carriage return to command shell after control-only startup packet.");
        _terminalService?.WriteInput("\r");
    }

    private void PostTerminalOutputToWebView(string terminalText)
    {
        if (TerminalWebView.CoreWebView2 is null) return;

        string escaped = EscapeForJson(terminalText);
        TerminalWebView.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"output\",\"data\":\"{escaped}\"}}");
        if (!_terminalLoggedFirstOutputPost)
        {
            _terminalLoggedFirstOutputPost = true;
            LogService.Instance.Info("Terminal", $"Posted first terminal output to WebView: chars={terminalText.Length}");
        }
    }

    private void PostTerminalClearToWebView()
    {
        if (TerminalWebView.CoreWebView2 is null) return;

        TerminalWebView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"clear\"}");
    }

    private void LogTerminalInput(string data)
    {
        if (_terminalLoggedFirstInput || string.IsNullOrEmpty(data))
            return;

        _terminalLoggedFirstInput = true;
        LogService.Instance.Info("Terminal", $"Terminal host received first input: chars={data.Length}");
    }

    private static string EscapeForJson(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 16);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private void OnTerminalProcessExited(int exitCode, int sessionGeneration)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sessionGeneration != _terminalSessionGeneration) return;
            OnTerminalOutput($"\r\n\x1b[90m[Process exited with code {exitCode}. Press any key to restart.]\x1b[0m\r\n", sessionGeneration);
            // Allow restart on next keypress
            _terminalService?.Dispose();
            _terminalService = null;
        });
    }

    private void DisposeTerminal()
    {
        _terminalSessionGeneration++;
        _terminalService?.Dispose();
        _terminalService = null;
    }

    private static void EnsureWebView2LoaderLoaded()
    {
        string loaderPath = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
        if (File.Exists(loaderPath))
            NativeLibrary.Load(loaderPath);
    }
}
