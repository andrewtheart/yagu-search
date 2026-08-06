using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Yagu.Helpers;

namespace Yagu;

public sealed partial class HelpWindow : Window
{
    private const string FindScript = """
        (function() {
            if (window.yaguHelpFind) return;

            const style = document.createElement('style');
            style.textContent = `
                mark[data-yagu-help-find="true"] {
                    background: #ffe16b;
                    color: #111;
                    border-radius: 2px;
                    padding: 0 1px;
                }
                mark[data-yagu-help-find="true"].yagu-find-current {
                    background: #ff8a3d;
                    outline: 2px solid #ffffff;
                }
            `;
            document.head.appendChild(style);

            let matches = [];
            let current = -1;

            function clearMatches() {
                const parents = new Set();
                document.querySelectorAll('mark[data-yagu-help-find="true"]').forEach(mark => {
                    const parent = mark.parentNode;
                    mark.replaceWith(document.createTextNode(mark.textContent || ''));
                    if (parent) parents.add(parent);
                });
                parents.forEach(parent => parent.normalize());
                matches = [];
                current = -1;
            }

            function activate(index) {
                matches.forEach(match => match.classList.remove('yagu-find-current'));
                if (!matches.length) {
                    current = -1;
                    return;
                }

                current = ((index % matches.length) + matches.length) % matches.length;
                matches[current].classList.add('yagu-find-current');
                matches[current].scrollIntoView({ block: 'center', behavior: 'smooth' });
            }

            function escapeRegExp(value) {
                return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            }

            window.yaguHelpFind = function(query, useRegex) {
                clearMatches();
                if (!query) return { count: 0, current: -1, error: '' };

                let source;
                try {
                    source = useRegex ? query : escapeRegExp(query);
                    new RegExp(source, 'gi');
                } catch (error) {
                    return { count: 0, current: -1, error: String(error.message || error) };
                }

                const rejected = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEXTAREA', 'INPUT', 'BUTTON']);
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
                    acceptNode(node) {
                        const parent = node.parentElement;
                        if (!parent || rejected.has(parent.tagName) || !node.nodeValue) {
                            return NodeFilter.FILTER_REJECT;
                        }
                        return NodeFilter.FILTER_ACCEPT;
                    }
                });

                const nodes = [];
                while (walker.nextNode()) nodes.push(walker.currentNode);

                nodes.forEach(node => {
                    const text = node.nodeValue;
                    const expression = new RegExp(source, 'gi');
                    const found = [];
                    let result;
                    while ((result = expression.exec(text)) !== null) {
                        if (!result[0].length) {
                            expression.lastIndex += 1;
                            continue;
                        }
                        found.push({ index: result.index, length: result[0].length });
                    }
                    if (!found.length) return;

                    const fragment = document.createDocumentFragment();
                    let offset = 0;
                    found.forEach(foundMatch => {
                        fragment.appendChild(document.createTextNode(text.slice(offset, foundMatch.index)));
                        const mark = document.createElement('mark');
                        mark.dataset.yaguHelpFind = 'true';
                        mark.textContent = text.slice(foundMatch.index, foundMatch.index + foundMatch.length);
                        fragment.appendChild(mark);
                        offset = foundMatch.index + foundMatch.length;
                    });
                    fragment.appendChild(document.createTextNode(text.slice(offset)));
                    node.replaceWith(fragment);
                });

                matches = Array.from(document.querySelectorAll('mark[data-yagu-help-find="true"]'));
                activate(0);
                return { count: matches.length, current, error: '' };
            };

            window.yaguHelpFindMove = function(delta) {
                activate(current + delta);
                return { count: matches.length, current, error: '' };
            };
        })();
        """;

    private static nint s_webView2LoaderHandle;
    private readonly string _helpPath;
    private readonly string _appTitle;
    private int _findGeneration;

    public HelpWindow(IntPtr mainHwnd, string helpPath, string appTitle)
    {
        _helpPath = helpPath;
        _appTitle = appTitle;
        InitializeComponent();

        // Title-bar-less modal: hide the OS caption strip reliably. Setting ExtendsContentIntoTitleBar
        // directly on the Window guarantees no title bar even if the OverlappedPresenter call below
        // fails to apply (matches MainWindow/SettingsWindow/ResultStoreTempLocationWindow). A custom
        // top-right close button (CloseButton) is the close affordance; no SetTitleBar is called, so
        // all content -- including that button -- stays interactive.
        ExtendsContentIntoTitleBar = true;

        HelpWebView.Loaded += OnHelpWebViewLoaded;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, mainHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = Title;
        const int windowWidth = 980;
        const int windowHeight = 720;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, mainHwnd, windowWidth, windowHeight);

        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
        }
        catch { }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
        _ = RunFindAsync();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
        => _ = RunFindAsync();

    private void OnRegexOptionChanged(object sender, RoutedEventArgs e)
        => _ = RunFindAsync();

    private void OnFindTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;

        e.Handled = true;
        _ = MoveFindAsync(1);
    }

    private void OnPreviousMatchClick(object sender, RoutedEventArgs e)
        => _ = MoveFindAsync(-1);

    private void OnNextMatchClick(object sender, RoutedEventArgs e)
        => _ = MoveFindAsync(1);

    private async void OnCloseFindClick(object sender, RoutedEventArgs e)
    {
        _findGeneration++;
        FindPanel.Visibility = Visibility.Collapsed;
        MatchStatusText.Text = "No matches";
        PreviousMatchButton.IsEnabled = false;
        NextMatchButton.IsEnabled = false;

        if (HelpWebView.CoreWebView2 is not null)
        {
            try
            {
                await HelpWebView.CoreWebView2.ExecuteScriptAsync("window.yaguHelpFind('', false)");
            }
            catch { }
        }
    }

    private async Task RunFindAsync()
    {
        if (HelpWebView.CoreWebView2 is null) return;

        int generation = ++_findGeneration;
        string query = FindTextBox.Text;
        bool useRegex = RegexCheckBox.IsChecked == true;
        try
        {
            string result = await HelpWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.yaguHelpFind({JsString(query)}, {(useRegex ? "true" : "false")})");
            if (generation == _findGeneration)
                ApplyFindResult(result);
        }
        catch
        {
            if (generation == _findGeneration)
                ApplyFindResult(count: 0, current: -1, error: "Search is unavailable");
        }
    }

    private async Task MoveFindAsync(int delta)
    {
        if (HelpWebView.CoreWebView2 is null || !NextMatchButton.IsEnabled) return;

        int generation = ++_findGeneration;
        try
        {
            string result = await HelpWebView.CoreWebView2.ExecuteScriptAsync($"window.yaguHelpFindMove({delta})");
            if (generation == _findGeneration)
                ApplyFindResult(result);
        }
        catch { }
    }

    private void ApplyFindResult(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        ApplyFindResult(
            root.GetProperty("count").GetInt32(),
            root.GetProperty("current").GetInt32(),
            root.GetProperty("error").GetString());
    }

    private void ApplyFindResult(int count, int current, string? error)
    {
        bool hasMatches = count > 0;
        PreviousMatchButton.IsEnabled = hasMatches;
        NextMatchButton.IsEnabled = hasMatches;
        MatchStatusText.Text = !string.IsNullOrEmpty(error)
            ? "Invalid regex"
            : hasMatches
                ? $"{current + 1} of {count}"
                : "No matches";
        ToolTipService.SetToolTip(MatchStatusText, error);
    }

    public void BringInFrontOfMainWindow(IntPtr mainHwnd)
        => WindowForegroundHelper.BringOwnedWindowToFront(this, mainHwnd);

    private void OnHelpWebViewLoaded(object sender, RoutedEventArgs e)
    {
        HelpWebView.Loaded -= OnHelpWebViewLoaded;
        _ = LoadHelpAsync();
    }

    private async Task LoadHelpAsync()
    {
        string? html = null;
        try
        {
            if (!File.Exists(_helpPath))
            {
                ShowFallback($"The generated help file was not found:\n\n{_helpPath}\n\nRebuild Yagu to regenerate HELP.html.");
                return;
            }

            html = await File.ReadAllTextAsync(_helpPath);
            EnsureWebView2LoaderLoaded();

            // Point WebView2 at a per-user, writable user-data folder (the default beside the exe is
            // read-only for a non-elevated all-users install, which would fail WebView2 init).
            Yagu.Helpers.WebView2Support.ConfigureUserDataFolder();
            var environment = await CoreWebView2Environment.CreateAsync();
            await HelpWebView.EnsureCoreWebView2Async(environment);
            HelpWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            HelpWebView.CoreWebView2.NavigationCompleted += OnHelpNavigationCompleted;
            HelpWebView.CoreWebView2.Navigate(new Uri(_helpPath).AbsoluteUri);
            FallbackPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowFallback($"WebView2 failed to initialize or render HELP.html.\n\n{ex}\n\nHelp file:\n{_helpPath}", html);
        }
    }

    private static void EnsureWebView2LoaderLoaded()
    {
        if (s_webView2LoaderHandle != 0) return;

        string loaderPath = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
        if (File.Exists(loaderPath))
            s_webView2LoaderHandle = NativeLibrary.Load(loaderPath);
    }

    private void ShowFallback(string message, string? html = null)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        HelpWebView.Visibility = Visibility.Collapsed;
        FallbackMessageText.Text = message;
        FallbackHelpText.Text = string.IsNullOrWhiteSpace(html)
            ? string.Empty
            : HtmlToPlainText(html);
        FallbackPanel.Visibility = Visibility.Visible;
    }

    private static string HtmlToPlainText(string html)
    {
        string text = Regex.Replace(html, "<script[\\s\\S]*?</script>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<style[\\s\\S]*?</style>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</(h[1-6]|p|div|li|tr|table|blockquote|pre)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \t]+", " ");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private async void OnHelpNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        const string darkCss = @"
            html { background-color: #202020 !important; color: #e0e0e0 !important; }
            body { color: #e0e0e0 !important; }
            a { color: #6cb6ff !important; }
            code, pre { background-color: #2d2d2d !important; color: #d4d4d4 !important; }
            table, th, td { border-color: #444 !important; }
            h1, h2, h3, h4, h5, h6 { color: #ffffff !important; }
        ";
        string script = $"var s=document.createElement('style');s.textContent=`{darkCss}`;document.head.appendChild(s);";
        await sender.ExecuteScriptAsync(script);

        // The navigation blade is the pandoc table of contents (nav#TOC) styled by help-style.html.
        // Inject the running app's title/version into the blade header so it brands the sidebar.
        string brandScript =
            "(function(){var t=document.getElementById('TOC');if(!t||document.getElementById('toc-brand'))return;" +
            "var d=document.createElement('div');d.id='toc-brand';" +
            "var a=document.createElement('span');a.className='toc-brand-title';a.textContent=" + JsString("Yagu Help") + ";" +
            "var b=document.createElement('span');b.className='toc-brand-sub';b.textContent=" + JsString(_appTitle) + ";" +
            "d.appendChild(a);d.appendChild(b);t.insertBefore(d,t.firstChild);})();";
        await sender.ExecuteScriptAsync(brandScript);
        await sender.ExecuteScriptAsync(FindScript);

        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    // Renders a string as a safe double-quoted JavaScript string literal for embedding in injected script.
    private static string JsString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

}
