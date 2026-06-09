using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
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
    private readonly TaskCompletionSource<bool> _terminalReadyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            TerminalWebView.Visibility = Visibility.Visible;
            TerminalChevronIcon.Glyph = "\uE70D"; // ChevronDown — click to collapse
        }
        else
        {
            TerminalRow.Height = new GridLength(0);
            TerminalWebView.Visibility = Visibility.Collapsed;
            TerminalChevronIcon.Glyph = "\uE70E"; // ChevronUp — click to expand
        }
    }

    private async Task SendTextToTerminalAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        await EnsureTerminalPaneExpandedAsync();
        await WaitForTerminalReadyAsync();

        StartConPtySession();
        _terminalService?.WriteInput(text);
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

    private void FocusTerminal()
    {
        TerminalWebView.Focus(FocusState.Programmatic);
        TerminalWebView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"focus\"}");
    }

    private async Task InitializeTerminalAsync()
    {
        try
        {
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

            TerminalWebView.CoreWebView2.WebMessageReceived += OnTerminalWebMessage;

            // Navigate to terminal.html
            string terminalHtmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal.html");
            if (File.Exists(terminalHtmlPath))
            {
                TerminalWebView.CoreWebView2.Navigate(new Uri(terminalHtmlPath).AbsoluteUri);
            }
            else
            {
                // Fallback: try virtual host mapping
                TerminalWebView.CoreWebView2.Navigate("https://yagu-terminal/terminal.html");
            }

            _terminalInitialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Terminal init failed: {ex}");
        }
    }

    private void OnTerminalWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string json = args.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(json)) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string type = root.GetProperty("type").GetString() ?? "";

        switch (type)
        {
            case "ready":
                StartConPtySession();
                _terminalReadyCompletion.TrySetResult(true);
                break;
            case "input":
                string data = root.GetProperty("data").GetString() ?? "";
                _terminalService?.WriteInput(data);
                break;
            case "resize":
                _terminalColumns = Math.Max(1, root.GetProperty("cols").GetInt32());
                _terminalRows = Math.Max(1, root.GetProperty("rows").GetInt32());
                _terminalService?.Resize(_terminalColumns, _terminalRows);
                break;
            case "resetTerminal":
                ResetTerminalSession();
                break;
            case "openHelp":
                OpenHelpWindow();
                break;
        }
    }

    private void StartConPtySession()
    {
        if (_terminalService is not null) return;

        int sessionGeneration = ++_terminalSessionGeneration;
        var terminalService = new ConPtyTerminalService();
        terminalService.OutputReceived += text => OnTerminalOutput(text, sessionGeneration);
        terminalService.ProcessExited += exitCode => OnTerminalProcessExited(exitCode, sessionGeneration);
        _terminalService = terminalService;
        try
        {
            terminalService.Start(cols: _terminalColumns, rows: _terminalRows, workingDirectory: ResolveTerminalWorkingDirectory());
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_terminalService, terminalService))
                _terminalService = null;
            terminalService.Dispose();
            LogService.Instance.Warning("Terminal", "Failed to start ConPTY terminal session", ex);
            OnTerminalOutput($"\r\n\x1b[91m[Terminal failed to start: {ex.Message}]\x1b[0m\r\n", sessionGeneration);
        }
    }

    private void ResetTerminalSession()
    {
        DisposeTerminal();
        StartConPtySession();
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
                string escaped = EscapeForJson(text);
                TerminalWebView.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"output\",\"data\":\"{escaped}\"}}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Terminal output error: {ex.Message}");
            }
        });
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
                        sb.Append($"\\u{(int)c:X4}");
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
