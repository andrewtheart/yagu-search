using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Yagu.Helpers;

namespace Yagu;

public sealed partial class HelpWindow : Window
{
    private static nint s_webView2LoaderHandle;
    private readonly string _helpPath;

    public HelpWindow(IntPtr mainHwnd, string helpPath)
    {
        _helpPath = helpPath;
        InitializeComponent();

        AppTitleText.Text = AppInfo.WindowTitle;
        HelpPathText.Text = helpPath;
        HelpWebView.Loaded += OnHelpWebViewLoaded;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, mainHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        const int windowWidth = 980;
        const int windowHeight = 720;
        appWindow.Resize(new SizeInt32(windowWidth, windowHeight));
        CenterOverOwner(appWindow, mainHwnd, windowWidth, windowHeight);
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
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static void CenterOverOwner(AppWindow appWindow, IntPtr ownerHwnd, int width, int height)
    {
        if (ownerHwnd == IntPtr.Zero) return;
        if (!GetWindowRect(ownerHwnd, out var ownerRect)) return;

        int ownerCenterX = (ownerRect.Left + ownerRect.Right) / 2;
        int ownerCenterY = (ownerRect.Top + ownerRect.Bottom) / 2;
        int x = ownerCenterX - width / 2;
        int y = ownerCenterY - height / 2;

        const uint monitorDefaultToNearest = 2;
        var monitor = MonitorFromWindow(ownerHwnd, monitorDefaultToNearest);
        var monitorInfo = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref monitorInfo))
        {
            var workArea = monitorInfo.rcWork;
            if (x < workArea.Left) x = workArea.Left;
            if (y < workArea.Top) y = workArea.Top;
            if (x + width > workArea.Right) x = workArea.Right - width;
            if (y + height > workArea.Bottom) y = workArea.Bottom - height;
        }

        appWindow.Move(new PointInt32(x, y));
    }
}
