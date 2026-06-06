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

    private void OnToggleTerminalPane(object sender, RoutedEventArgs e)
    {
        _terminalPaneExpanded = !_terminalPaneExpanded;

        if (_terminalPaneExpanded)
        {
            TerminalRow.Height = new GridLength(250);
            TerminalWebView.Visibility = Visibility.Visible;
            TerminalChevronIcon.Glyph = "\uE70D"; // ChevronDown — click to collapse
            if (!_terminalInitialized)
                _ = InitializeTerminalAsync();
        }
        else
        {
            TerminalRow.Height = new GridLength(0);
            TerminalWebView.Visibility = Visibility.Collapsed;
            TerminalChevronIcon.Glyph = "\uE70E"; // ChevronUp — click to expand
        }
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
                break;
            case "input":
                string data = root.GetProperty("data").GetString() ?? "";
                _terminalService?.WriteInput(data);
                break;
            case "resize":
                int cols = root.GetProperty("cols").GetInt32();
                int rows = root.GetProperty("rows").GetInt32();
                _terminalService?.Resize(cols, rows);
                break;
        }
    }

    private void StartConPtySession()
    {
        if (_terminalService is not null) return;

        _terminalService = new ConPtyTerminalService();
        _terminalService.OutputReceived += OnTerminalOutput;
        _terminalService.ProcessExited += OnTerminalProcessExited;
        _terminalService.Start(cols: 120, rows: 24);
    }

    private void OnTerminalOutput(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
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

    private void OnTerminalProcessExited(int exitCode)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            OnTerminalOutput($"\r\n\x1b[90m[Process exited with code {exitCode}. Press any key to restart.]\x1b[0m\r\n");
            // Allow restart on next keypress
            _terminalService?.Dispose();
            _terminalService = null;
        });
    }

    private void DisposeTerminal()
    {
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
