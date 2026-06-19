using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
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
    private readonly TaskCompletionSource<bool> _terminalReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _terminalShellReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalDirectoryProbe? _terminalDirectoryProbe;

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

        SetAdvancedOptionsDrawerExpandedWidthState(AdvancedOptionsExpander.IsExpanded);
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
        var showSearchCardBottomActions = !_terminalPaneExpanded && !_advancedOptionsDrawerExpandedWidth;
        PreSearchTerminalChevron.Visibility = showSearchCardBottomActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        SearchCardBottomActions.Visibility = showSearchCardBottomActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        TerminalChevron.Visibility = _terminalPaneExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SendTextToTerminalAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        await EnsureTerminalPaneExpandedAsync();
        await WaitForTerminalReadyAsync();

        StartConPtySession();
        await WaitForTerminalShellReadyAsync();
        await VerifyTerminalDirectoryIsYaguExecutableDirectoryAsync();

        string commandText = text.TrimEnd('\r', '\n');
        PostTerminalPasteTextToWebView(commandText);
        FocusTerminal();
    }

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
            await _terminalShellReadyCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Timed out waiting for terminal shell readiness: {ex.Message}");
        }
    }

    private async Task VerifyTerminalDirectoryIsYaguExecutableDirectoryAsync()
    {
        if (_terminalService is null)
            throw new InvalidOperationException("The terminal shell is not available.");

        string? executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (!TryResolveExistingDirectory(executableDirectory, out string directory))
            throw new InvalidOperationException("Could not resolve the running Yagu executable directory.");

        var probe = new TerminalDirectoryProbe(directory, TerminalDirectoryGuard.CreateMarker());
        _terminalDirectoryProbe = probe;
        string commandText = TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand(directory, probe.Marker).TrimEnd('\r', '\n');
        _terminalService.WriteInput(commandText + "\r", echoInput: false);

        TerminalDirectoryProbeResult result;
        try
        {
            result = await probe.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"The terminal did not confirm it changed to '{directory}'.", ex);
        }
        finally
        {
            if (ReferenceEquals(_terminalDirectoryProbe, probe))
                _terminalDirectoryProbe = null;
        }

        if (!result.Verified)
            throw new InvalidOperationException($"The terminal reported '{result.ActualDirectory}' after cd, expected '{directory}'.");
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
                    bool echoInput = !root.TryGetProperty("echoInput", out var echoInputElement) || echoInputElement.GetBoolean();
                    LogTerminalInput(data);
                    if (_terminalService is null)
                    {
                        LogService.Instance.Warning("Terminal", "Terminal input received before the shell session was available; starting a terminal session.");
                        StartConPtySession();
                    }
                    _terminalService?.WriteInput(data, echoInput);
                    break;
                case "cancelInput":
                    _terminalService?.CancelCurrentCommand();
                    break;
                case "completeInput":
                    CompleteTerminalInput(root);
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

    private void CompleteTerminalInput(JsonElement root)
    {
        int requestId = root.TryGetProperty("requestId", out var requestIdElement) ? requestIdElement.GetInt32() : 0;
        string input = root.TryGetProperty("input", out var inputElement) ? inputElement.GetString() ?? string.Empty : string.Empty;
        int cursor = root.TryGetProperty("cursor", out var cursorElement) ? cursorElement.GetInt32() : input.Length;
        string promptText = root.TryGetProperty("promptText", out var promptElement) ? promptElement.GetString() ?? string.Empty : string.Empty;

        var result = TerminalCompletionService.Complete(
            requestId,
            input,
            cursor,
            promptText,
            ResolveTerminalWorkingDirectory());

        PostTerminalCompletionResultToWebView(result);
    }

    private void PostTerminalCompletionResultToWebView(TerminalCompletionService.Result result)
    {
        if (TerminalWebView.CoreWebView2 is null)
            return;

        string suggestionsJson = "[" + string.Join(",", result.Suggestions.Select(EncodeJsonString)) + "]";
        string json = "{"
            + "\"type\":\"completionResult\","
            + "\"requestId\":" + result.RequestId + ","
            + "\"replacementStart\":" + result.ReplacementStart + ","
            + "\"replacementLength\":" + result.ReplacementLength + ","
            + "\"replacementText\":" + EncodeJsonString(result.ReplacementText) + ","
            + "\"suggestions\":" + suggestionsJson + ","
            + "\"hasMatches\":" + (result.HasMatches ? "true" : "false")
            + "}";
        TerminalWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string EncodeJsonString(string value)
        => "\"" + JavaScriptEncoder.Default.Encode(value ?? string.Empty) + "\"";

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
        _terminalStartupPromptNudged = false;
        _terminalLoggedFirstOutputPost = false;
        _terminalLoggedFirstInput = false;
        var terminalService = new ConPtyTerminalService();
        terminalService.OutputReceived += text => OnTerminalOutput(text, sessionGeneration);
        terminalService.ProcessExited += exitCode => OnTerminalProcessExited(exitCode, sessionGeneration);
        _terminalService = terminalService;
        _terminalShellReadyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        DisposeTerminal();
        StartConPtySession();
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

            PostTerminalPasteTextToWebView(text);
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
                if (TryHandleTerminalDirectoryProbeOutput(terminalText, out string probeFilteredText))
                    terminalText = probeFilteredText;
                if (string.IsNullOrEmpty(terminalText))
                    return;
                if (ContainsPrintableShellText(terminalText))
                    _terminalShellReadyCompletion.TrySetResult(true);
                PostTerminalOutputToWebView(terminalText);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Terminal", "Terminal output post failed", ex);
                System.Diagnostics.Debug.WriteLine($"Terminal output error: {ex.Message}");
            }
        });
    }

    private bool TryHandleTerminalDirectoryProbeOutput(string terminalText, out string filteredText)
    {
        filteredText = terminalText;
        TerminalDirectoryProbe? probe = _terminalDirectoryProbe;
        if (probe is null)
            return false;

        probe.Output.Append(terminalText);
        string bufferedOutput = probe.Output.ToString();
        if (!TerminalDirectoryGuard.TryExtractPromptDirectory(bufferedOutput, probe.Marker, out string actualDirectory))
        {
            filteredText = string.Empty;
            return true;
        }

        bool verified = TerminalDirectoryGuard.DirectoriesEqual(probe.ExpectedDirectory, actualDirectory);
        filteredText = TerminalDirectoryGuard.RemoveMarkerLine(bufferedOutput, probe.Marker);
        _terminalDirectoryProbe = null;
        probe.Completion.TrySetResult(new TerminalDirectoryProbeResult(verified, actualDirectory));
        LogService.Instance.Info("Terminal", verified
            ? $"Verified terminal cwd before generated CLI command: '{actualDirectory}'"
            : $"Terminal cwd verification failed before generated CLI command: expected '{probe.ExpectedDirectory}', actual '{actualDirectory}'");
        return true;
    }

    private static string FilterTerminalOutputForXterm(string text)
    {
        return text
            .Replace("\u001b[?9001h", string.Empty, StringComparison.Ordinal)
            .Replace("\u001b[?1004h", string.Empty, StringComparison.Ordinal);
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

    private void PostTerminalPasteTextToWebView(string text)
    {
        if (TerminalWebView.CoreWebView2 is null) return;

        string escaped = EscapeForJson(text);
        TerminalWebView.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"pasteText\",\"text\":\"{escaped}\"}}");
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

    private sealed class TerminalDirectoryProbe(string expectedDirectory, string marker)
    {
        public string ExpectedDirectory { get; } = expectedDirectory;
        public string Marker { get; } = marker;
        public StringBuilder Output { get; } = new();
        public TaskCompletionSource<TerminalDirectoryProbeResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct TerminalDirectoryProbeResult(bool Verified, string ActualDirectory);
}
