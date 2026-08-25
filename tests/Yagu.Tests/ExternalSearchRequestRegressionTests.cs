namespace Yagu.Tests;

/// <summary>
/// Source-pins the "external search request" plumbing shared by the Explorer "Search with Yagu"
/// context menu (a second launch forwarding its folder to the running instance) and the system-tray
/// "Quick search" popup. These files are Win32/WinUI-coupled and not compiled into the test assembly,
/// so the wiring is validated by asserting on the source text.
/// </summary>
public sealed class ExternalSearchRequestRegressionTests
{
    private static readonly string ProgramSource = Read("src", "Yagu", "Program.cs");
    private static readonly string AppSource = Read("src", "Yagu", "App.xaml.cs");
    private static readonly string TrayIconSource = Read("src", "Yagu", "Helpers", "TrayIcon.cs");
    private static readonly string SearchRequestIpcSource = Read(
        "src", "Yagu", "Helpers", "SearchRequestIpc.cs");
    private static readonly string QuickSearchSource = Read(
        "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.QuickSearch.cs");
    private static readonly string LauncherSource = Read(
        "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Launcher.cs");
    private static readonly string MainWindowSource = Read(
        "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs");

    // ---- Task 1: Open-in-Yagu directory forwarding ----

    [Fact]
    public void ParseDirArg_UsesPositionalDirectoryParsing()
    {
        // The context menu passes the folder as a bare positional arg, so ParseDirArg must delegate to
        // StartupArgs.ParseDirectory (which understands both --dir and a bare existing directory).
        Assert.Contains("Helpers.StartupArgs.ParseDirectory(args)", AppSource);
    }

    [Fact]
    public void SecondLaunch_ForwardsDirectoryAndQueryBeforeActivating()
    {
        int block = ProgramSource.IndexOf("if (!createdNew)", StringComparison.Ordinal);
        Assert.True(block >= 0, "The single-instance secondary-launch block was not found.");
        string tail = ProgramSource[block..Math.Min(ProgramSource.Length, block + 1200)];

        AssertContainsInOrder(tail,
            "App.ParseDirArg(args)",
            "App.ParseStringArg(args, \"--query\")",
            "Helpers.SearchRequestSender.TrySend(new Helpers.SearchRequest(",
            "return;",
            "ActivateExistingInstance();");
    }

    [Fact]
    public void SearchRequestIpc_SenderAndListenerShareNullTerminatedUtf16Framing()
    {
        string listener = ExtractWindow(SearchRequestIpcSource,
            "private IntPtr WndProc", 1600);
        string sender = ExtractWindow(SearchRequestIpcSource,
            "internal static class SearchRequestSender", 1800);

        Assert.Contains("cds.dwData == (IntPtr)CopyDataId", listener);
        Assert.Contains("Marshal.PtrToStringUni(cds.lpData, (int)(cds.cbData / 2))", listener);
        Assert.Contains("SearchRequestCodec.TryDecode(payload, out var request)", listener);
        Assert.Contains("RequestReceived?.Invoke(request);", listener);

        Assert.Contains("Marshal.StringToHGlobalUni(payload)", sender);
        Assert.Contains("cbData = (payload.Length + 1) * 2", sender);
        Assert.Contains("dwData = (IntPtr)SearchRequestListener.CopyDataId", sender);
        Assert.Contains("SMTO_ABORTIFHUNG, 3000", sender);
    }

    // ---- Task 2: Tray context menu + inline quick search ----

    [Fact]
    public void TrayIcon_RaisesContextMenuRequestedInsteadOfBuildingAWin32Menu()
    {
        Assert.Contains("public event Action<int, int>? ContextMenuRequested;", TrayIconSource);
        Assert.Contains("ContextMenuRequested?.Invoke(pt.x, pt.y);", TrayIconSource);
        // Foreground first, so the themed menu window can take activation and dismiss when it loses it.
        AssertContainsInOrder(TrayIconSource,
            "SetForegroundWindow(_hwnd);",
            "GetCursorPos(out POINT pt);",
            "ContextMenuRequested?.Invoke(pt.x, pt.y);");

        // The Win32 popup menu is gone, so none of its plumbing may linger.
        Assert.DoesNotContain("TrackPopupMenuEx", TrayIconSource);
        Assert.DoesNotContain("AppendMenuW", TrayIconSource);
        Assert.DoesNotContain("CMD_QUICK_SEARCH", TrayIconSource);
    }

    [Fact]
    public void Launcher_WiresContextMenuRequestedToTheThemedMenu()
    {
        AssertContainsInOrder(LauncherSource,
            "_trayIcon.ContextMenuRequested += (x, y) =>",
            "DispatcherQueue.TryEnqueue(() => ShowTrayContextMenu(x, y));");
    }

    // ---- Shared apply path ----

    [Fact]
    public void ApplyExternalSearchRequest_SetsDirectoryQueryAndOptionallyRuns()
    {
        string apply = ExtractWindow(QuickSearchSource,
            "internal void ApplyExternalSearchRequest", 1600);

        AssertContainsInOrder(apply,
            "DispatcherQueue.HasThreadAccess",             // marshalled to the UI thread
            "RestoreWindowFromTray();",                    // brought forward / un-docked
            "ViewModel.Directory = string.Empty;",         // blank dir clears to all drives
            "ViewModel.Directory = dir;",                  // existing dir overrides pinned startup dir
            "ViewModel.SetDirectoryFromArgs(dir);",        // non-existent dir surfaces the error
            "ViewModel.Query = request.Query;",
            "request.RunSearch && !string.IsNullOrWhiteSpace(ViewModel.Query)",
            "_ = StartSearchFromUiAsync();");
    }

    [Fact]
    public void TrayQuickSearch_IsInlineInTheMenuAndCarriesEveryOption()
    {
        string menu = Read("src", "Yagu", "UI", "Windows", "TrayMenuWindow.cs");

        // Choosing Quick search expands the panel in place; the menu is not dismissed.
        string toggle = ExtractWindow(menu, "private void ToggleQuickSearchPanel()", 400);
        Assert.Contains("_quickSearchPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;", toggle);
        Assert.DoesNotContain("CloseMenu();", toggle);

        // Scope, query, all four search-box toggles, and the Traditional/Semantic switch.
        Assert.Contains("Text = \"Directory\"", menu);
        Assert.Contains("Text = \"Pattern\"", menu);
        Assert.Contains("Content = \"Traditional\"", menu);
        Assert.Contains("Content = \"Semantic\"", menu);
        foreach (string option in new[] { "Regex", "Case", "Multiline", "Exact" })
            Assert.Contains($"Content = \"{option}\"", menu);

        // Title-bar-less, and dismissed by deactivation or Esc.
        Assert.Contains("presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);", menu);
        Assert.Contains("WindowActivationState.Deactivated", menu);
        Assert.Contains("Windows.System.VirtualKey.Escape", menu);

        // Borderless: a stroked root grid drew a visible light outline around the menu.
        string root = ExtractWindow(menu, "private Grid BuildRoot(ElementTheme theme)", 1200);
        Assert.DoesNotContain("BorderThickness", root);
        Assert.DoesNotContain("BorderBrush", root);

        // The applied search restores every option before running.
        string apply = ExtractWindow(QuickSearchSource, "private void ApplyTrayQuickSearch", 900);
        AssertContainsInOrder(apply,
            "ViewModel.IsSemanticQueryMode = request.Semantic;",
            "ViewModel.UseRegex = request.UseRegex || request.Multiline;",
            "ViewModel.CaseSensitive = request.CaseSensitive;",
            "ViewModel.Multiline = request.Multiline;",
            "ViewModel.ExactMatch = request.ExactMatch;",
            "ApplyExternalSearchRequest(new SearchRequest(");
    }

    [Fact]
    public void TrayMenu_ItemsAreClickableAcrossTheFullRow()
    {
        string menu = Read("src", "Yagu", "UI", "Windows", "TrayMenuWindow.cs");
        string item = ExtractWindow(menu, "private static Button BuildMenuItem", 1000);

        AssertContainsInOrder(item,
            "HorizontalAlignment = HorizontalAlignment.Stretch,",
            "HorizontalContentAlignment = HorizontalAlignment.Left,",
            "Background = new SolidColorBrush(Colors.Transparent),",
            "button.Click += (_, _) => onClick();");
        Assert.DoesNotContain("Background = null", item);
    }

    [Fact]
    public void TrayMenu_HasNoCaptionButtons_AndAnchorsToTheClickedPoint()
    {
        string menu = Read("src", "Yagu", "UI", "Windows", "TrayMenuWindow.cs");

        // A context menu must never show caption buttons. Two layers can draw them, so both are ruled out:
        // the SDK title bar (which ExtendsContentIntoTitleBar keeps alive) and the DWM non-client caption.
        Assert.DoesNotContain("ExtendsContentIntoTitleBar =", menu);
        Assert.Contains(
            "(style & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox))",
            menu);
        Assert.Contains("| WsPopup", menu);
        Assert.Contains("SwpFrameChanged", menu);

        // A failed style read returns 0; writing a style synthesized from it would clear WS_VISIBLE.
        Assert.Contains("if (style == 0)", menu);

        // Applied when the window is created, then re-asserted after it is realized, because the presenter
        // call can silently no-op beforehand and showing the window can restore WinUI's frame.
        Assert.Contains("ApplyBorderlessPopupFrame(hwnd);", menu);
        string activated = ExtractWindow(menu, "private void OnActivated", 700);
        AssertContainsInOrder(activated,
            "ReassertPopupFrame();",
            "DispatcherQueue.TryEnqueue(ReassertPopupFrame);");
        string reassert = ExtractWindow(menu, "private void ReassertPopupFrame()", 300);
        AssertContainsInOrder(reassert,
            "TryConfigurePresenter();",
            "ApplyBorderlessPopupFrame(");

        // Each presenter setting fails independently, so a rejected call cannot strand IsAlwaysOnTop (which
        // keeps the menu above the taskbar) or IsShownInSwitchers.
        string presenter = ExtractWindow(menu, "private void TryConfigurePresenter", 800);
        Assert.Contains("try { _appWindow.IsShownInSwitchers = false; }", presenter);
        int switchers = presenter.IndexOf("_appWindow.IsShownInSwitchers", StringComparison.Ordinal);
        int border = presenter.IndexOf("presenter.SetBorderAndTitleBar(", StringComparison.Ordinal);
        Assert.True(switchers >= 0 && border > switchers, "Both presenter settings must be present.");
        Assert.Contains("presenter.IsAlwaysOnTop = true;", presenter);

        // The menu corner tracks the cursor, sized for the display that was actually clicked.
        Assert.Contains("double scale = GetAnchorScale();", menu);
        Assert.Contains("int x = _anchorX - width;", menu);
        Assert.Contains("int y = _anchorY - height;", menu);
        Assert.Contains("MonitorFromPoint(new POINT { X = _anchorX, Y = _anchorY }", menu);

        // Both dimensions come from the measured content, so the menu is only as large as it needs to be.
        // MaxMenuWidthDip is the measure constraint (an upper bound), never the width itself.
        Assert.Contains("Math.Clamp(_root.DesiredSize.Width, 40, MaxMenuWidthDip)", menu);
        Assert.Contains("Math.Max(_root.DesiredSize.Height, 40)", menu);
        Assert.DoesNotContain("int width = (int)Math.Ceiling(MaxMenuWidthDip * scale);", menu);
    }

    // ---- Listener lifecycle ----

    [Fact]
    public void SearchRequestListener_IsInitializedAndDisposed()
    {
        AssertContainsInOrder(MainWindowSource,
            "InitializeGlobalHotkey();",
            "InitializeSearchRequestListener();");
        Assert.Contains("DisposeSearchRequestListener();", MainWindowSource);

        Assert.Contains("_searchRequestListener.RequestReceived += OnSearchRequestReceived;", QuickSearchSource);
        Assert.Contains("_searchRequestListener.Dispose();", QuickSearchSource);
    }

    private static string ExtractWindow(string source, string marker, int length)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker '{marker}' was not found.");
        return source[start..Math.Min(source.Length, start + length)];
    }

    private static void AssertContainsInOrder(string source, params string[] expected)
    {
        int position = 0;
        foreach (string item in expected)
        {
            int found = source.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Yagu.slnx.");
    }
}
