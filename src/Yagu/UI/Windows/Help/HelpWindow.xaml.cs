using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Yagu.Helpers;

namespace Yagu;

public sealed partial class HelpWindow : Window
{
    private static nint s_webView2LoaderHandle;
    private readonly string _helpPath;
    private readonly string _appTitle;

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
