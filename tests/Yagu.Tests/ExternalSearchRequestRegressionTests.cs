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

    // ---- Task 2: Tray quick-search menu item + event ----

    [Fact]
    public void TrayIcon_ExposesQuickSearchMenuItemAndEvent()
    {
        Assert.Contains("public event Action? QuickSearchRequested;", TrayIconSource);
        Assert.Contains("CMD_QUICK_SEARCH = 4", TrayIconSource);
        Assert.Contains("case CMD_QUICK_SEARCH: QuickSearchRequested?.Invoke(); break;", TrayIconSource);
        Assert.Contains("AppendMenuW(hMenu, MF_STRING, CMD_QUICK_SEARCH, \"Quick search", TrayIconSource);
    }

    [Fact]
    public void Launcher_WiresQuickSearchRequestedToDialog()
    {
        AssertContainsInOrder(LauncherSource,
            "_trayIcon.QuickSearchRequested += () =>",
            "DispatcherQueue.TryEnqueue(async () => await ShowTrayQuickSearchAsync());");
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
    public void QuickSearchDialog_IsTitleBarLessAndSubmitsOnEnter()
    {
        string dialog = ExtractWindow(QuickSearchSource,
            "internal async Task ShowTrayQuickSearchAsync", 3400);

        AssertContainsInOrder(dialog,
            "YaguDialog.HasOpenOwnedWindow(_hwnd)",        // never stacks on another modal
            "PrimaryButtonText = \"Search\"",
            "ShowTitleBar = false,",                       // modal-no-title-bar rule
            "Windows.System.VirtualKey.Enter",             // Enter submits
            "dialog.AcceptPrimary();",
            "YaguDialogResult.Primary",
            "ApplyExternalSearchRequest(new SearchRequest(");
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
