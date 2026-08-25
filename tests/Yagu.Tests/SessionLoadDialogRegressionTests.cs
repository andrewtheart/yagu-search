using System.Globalization;

namespace Yagu.Tests;

public sealed class SessionLoadDialogRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PreviewCommandsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewCommands.cs"));
    private static readonly string MainViewModelSource = MainViewModelPartials.Text;
    private static readonly string SessionLoadDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "SessionLoadDialog.cs"));
    private static readonly string YaguDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "YaguDialog.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string MainWindowKeyboardSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Keyboard.cs"));
    private static readonly string MainWindowLauncherSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Launcher.cs"));
    private static readonly string MainWindowWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));
    private static readonly string MainWindowTerminalSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));
    private static readonly string TerminalHtml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Assets", "terminal.html"));

    [Fact]
    public void LoadSession_UsesFastDiscoveryBeforeNativePickerFallback()
    {
        Assert.Contains("private async Task<string?> ChooseSessionFileToLoadAsync(string previousStatusText)", PreviewCommandsSource);
        AssertContainsInOrder(PreviewCommandsSource,
            "new SessionFileDiscoveryService().FindSessionFilesAsync(discoveryCts.Token)",
            "if (!discovery.FastSearchAvailable)",
            "return await PickSessionFileWithWindowsDialogAsync(previousStatusText);",
            "SessionLoadDialog.ShowAsync");
    }

    [Fact]
    public void LoadSession_CancelRestoresPreviousStatusText()
    {
        Assert.Contains("private const string FindingSavedYaguSessionsStatus = \"Finding saved Yagu sessions...\";", PreviewCommandsSource);

        string loadCommand = ExtractWindow(
            PreviewCommandsSource,
            "private async Task ShowLoadSessionDialogAsync()",
            "private async Task<string?> ChooseSessionFileToLoadAsync");
        AssertContainsInOrder(loadCommand,
            "if (_sessionLoadDialogOpening || !ViewModel.IsSessionIdle || YaguDialog.HasOpenOwnedWindow(_hwnd))",
            "_sessionLoadDialogOpening = true;",
            "string previousStatusText = ViewModel.StatusText;",
            "string? path = await ChooseSessionFileToLoadAsync(previousStatusText).ConfigureAwait(true);",
            "if (path is null)",
            "await LoadSessionFileAsync(path)",
            "_sessionLoadDialogOpening = false;");

        string chooseMethod = ExtractWindow(
            PreviewCommandsSource,
            "private async Task<string?> ChooseSessionFileToLoadAsync",
            "private async Task<string?> PickSessionFileWithWindowsDialogAsync");
        AssertContainsInOrder(chooseMethod,
            "ViewModel.StatusText = FindingSavedYaguSessionsStatus;",
            "SessionLoadDialogAction.Browse => await PickSessionFileWithWindowsDialogAsync(previousStatusText),",
            "_ => RestoreStatusAfterCanceledSessionLoad(previousStatusText),");

        string pickerMethod = ExtractWindow(
            PreviewCommandsSource,
            "private async Task<string?> PickSessionFileWithWindowsDialogAsync",
            "private async Task LoadSessionFileAsync");
        AssertContainsInOrder(pickerMethod,
            "var file = await picker.PickSingleFileAsync();",
            "return file?.Path ?? RestoreStatusAfterCanceledSessionLoad(previousStatusText);");

        AssertContainsInOrder(PreviewCommandsSource,
            "private string? RestoreStatusAfterCanceledSessionLoad(string previousStatusText)",
            "if (ViewModel.StatusText == FindingSavedYaguSessionsStatus)",
            "ViewModel.StatusText = previousStatusText;",
            "return null;");
    }

    [Fact]
    public void CtrlO_LoadsSavedSessionAcrossMainWindowAndTerminalFocus()
    {
        Assert.Equal(2, CountOccurrences(
            MainWindowXaml,
            "ToolTipService.ToolTip=\"Load a previously saved .yagu-session file (Ctrl+O)\""));

        Assert.Contains("Key = VirtualKey.O", MainWindowKeyboardSource);
        Assert.Contains("Modifiers = VirtualKeyModifiers.Control", MainWindowKeyboardSource);
        Assert.Contains("_ = ShowLoadSessionDialogAsync();", MainWindowKeyboardSource);
        Assert.Contains("Windows.System.VirtualKey.O && ctrl && !shift", MainWindowKeyboardSource);

        Assert.Contains("IsLoadSessionShortcutMessage(message, wParam)", MainWindowLauncherSource);
        Assert.Contains("wParam.ToUInt32() == VkO", MainWindowLauncherSource);
        Assert.Contains("IsVirtualKeyDown(VkControl)", MainWindowLauncherSource);
        Assert.Contains("private const uint VkO = 0x4F;", MainWindowWindowSource);
        Assert.Contains("private static extern short GetKeyState(int virtualKey);", MainWindowWindowSource);

        Assert.Contains("case \"openSession\":", MainWindowTerminalSource);
        Assert.Contains("_ = ShowLoadSessionDialogAsync();", MainWindowTerminalSource);
        Assert.Contains("event.key.toLowerCase() === 'o'", TerminalHtml);
        Assert.Contains("type: 'openSession'", TerminalHtml);
    }

    [Fact]
    public void LoadSession_CustomModalIsCenteredSingleSelectionAndBrowseCapable()
    {
        Assert.Contains("YaguDialog.ShowAsync", SessionLoadDialogSource);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner", YaguDialogSource);
        Assert.Contains("SelectionMode = ListViewSelectionMode.Single", SessionLoadDialogSource);
        Assert.Contains("IsItemClickEnabled = true", SessionLoadDialogSource);
        Assert.Contains("item.Tapped += (_, _) => loadPath(session.Path);", SessionLoadDialogSource);
        Assert.Contains("item.DoubleTapped += (_, _) => loadPath(session.Path);", SessionLoadDialogSource);
        Assert.Contains("TryGetSessionCandidate(args.ClickedItem, out var session)", SessionLoadDialogSource);
        Assert.Contains("completed = true;", SessionLoadDialogSource);
        Assert.Contains("dialog?.AcceptSecondary();", SessionLoadDialogSource);
        Assert.Contains("PrimaryButtonText = \"Browse...\"", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_RowsExposeDeleteAction_AndRefreshAfterDeletion()
    {
        Assert.Contains("Content = BuildTableRow(session, DeleteSessionFromListAsync)", SessionLoadDialogSource);
        AssertContainsInOrder(SessionLoadDialogSource,
            "async Task DeleteSessionFromListAsync(SessionFileCandidate session)",
            "if (!await deleteSession(session))",
            "SessionPickerList.RemoveByPath(sortedSessions, session.Path);",
            "sessionsChanged(sortedSessions.Count);",
            "RebuildList();");
        Assert.Contains("Glyph = \"\\uE74D\"", SessionLoadDialogSource);
        Assert.Contains("AutomationProperties.SetName(deleteButton, $\"Delete {fileName}\");", SessionLoadDialogSource);
        Assert.Contains("ToolTipService.SetToolTip(deleteButton, $\"Delete {fileName}\");", SessionLoadDialogSource);
        Assert.Contains("deleteButton.Tapped += (_, args) => args.Handled = true;", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_DeleteRequiresOwnedConfirmation_AndReportsFailures()
    {
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(dialog)", SessionLoadDialogSource);
        AssertContainsInOrder(SessionLoadDialogSource,
            "private static async Task<bool> DeleteSessionAsync",
            "Title = \"Delete saved session?\"",
            "PrimaryButtonText = \"Delete\"",
            "CloseButtonText = \"Keep file\"",
            "DefaultButton = YaguDialogDefaultButton.Close",
            "File.Delete(session.Path);",
            "Title = \"Couldn't delete session\"");
        Assert.True(CountOccurrences(SessionLoadDialogSource, "ShowTitleBar = false") >= 3);
    }

    [Fact]
    public void LoadSession_CustomModalSuppressesTitleBar()
    {
        Assert.Contains("ShowTitle = false", SessionLoadDialogSource);
        Assert.Contains("ShowTitleBar = false", SessionLoadDialogSource);
        Assert.Contains("public bool ShowTitle { get; init; } = true;", YaguDialogSource);
        Assert.Contains("public bool ShowTitleBar { get; init; } = true;", YaguDialogSource);
        Assert.Contains("if (options.ShowTitle)", YaguDialogSource);
        Assert.Contains("presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);", YaguDialogSource);
    }

    [Fact]
    public void LoadSession_CustomModalHasInContentTitleAndGuidance()
    {
        string buildContent = ExtractWindow(
            SessionLoadDialogSource,
            "private static Grid BuildContent(",
            "private enum SortColumn");
        AssertContainsInOrder(buildContent,
            "var header = new StackPanel",
            "Text = \"Load session\"",
            "Saved sessions reopen previous Yagu results without rerunning the search.",
            "Select a .yagu-session file from the list, or choose Browse... to pick one manually.",
            "Text = SessionPickerList.BuildSummary(sessions.Count)",
            "root.Children.Add(header);");

        Assert.Contains("FontWeight = Microsoft.UI.Text.FontWeights.SemiBold", buildContent);
        Assert.Contains("TextWrapping = TextWrapping.WrapWholeWords", buildContent);
        // The summary wording itself is unit-tested in SessionPickerListTests.
        Assert.Contains("summary.Text = SessionPickerList.BuildSummary(count)", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_UsesExistingViewModelLoadPathAfterSelection()
    {
        Assert.Contains("private async Task LoadSessionFileAsync(string path)", PreviewCommandsSource);
        AssertContainsInOrder(PreviewCommandsSource,
            "ClearPreviewStateForSessionLoad();",
            "var header = await ViewModel.LoadSessionAsync(path);",
            "Load session failed: {Path}");
    }

    [Fact]
    public void LoadSession_RestoresNormalCompletionStatusFromSavedStats()
    {
        string loadMethod = ExtractWindow(
            MainViewModelSource,
            "public async Task<SessionFileService.SessionHeader> LoadSessionAsync",
            "private void BeginSessionProgress(string initialText)");

        Assert.Contains("StatusText = BuildCompletionStatus(displaySummary, header.Stats.Elapsed);", loadMethod);
        Assert.Contains("FilesScanned: header.Stats.FilesScanned", loadMethod);
        Assert.Contains("BytesScanned: header.Stats.BytesScanned", loadMethod);
        Assert.Contains("FilesWithMatches: actualFileCount", loadMethod);
        Assert.Contains("TotalMatches: loadedCount", loadMethod);
        Assert.DoesNotContain("StatusText = loadedStatus", loadMethod);
        Assert.DoesNotContain("Loaded session:", loadMethod);
    }

    [Fact]
    public void LoadSession_RestoresTheSearchParametersSoPreviewHighlightingMatchesTheResults()
    {
        // No search runs when a session is loaded, so BuildSearchHighlightRegex would fall back to
        // the LIVE search-box toggles. ExactMatch defaults to ON (whole word), so a session saved
        // from a substring search highlighted only the occurrences that happened to be whole words.
        string loadMethod = ExtractWindow(
            MainViewModelSource,
            "public async Task<SessionFileService.SessionHeader> LoadSessionAsync",
            "private void BeginSessionProgress(string initialText)");
        Assert.Contains("ApplyLoadedSessionSearchParameters(h);", loadMethod);

        string apply = ExtractWindow(
            MainViewModelSource,
            "private void ApplyLoadedSessionSearchParameters",
            "public async Task<int> SaveSessionAsync");
        AssertContainsInOrder(apply,
            "if (header.SearchOptions is { } options)",
            "LastSearchExactMatch = options.ExactMatch;");
        // The pattern travels with the flags, so a session cannot pair one search's pattern with
        // another search's flags.
        Assert.Contains("options.Pattern", apply);
        // Legacy sessions clear only the two flags that can LOSE a highlight.
        Assert.Contains("LastSearchExactMatch = false;", apply);
        Assert.Contains("LastSearchCaseSensitive = false;", apply);
        // A shareable file must never be able to turn its stored pattern into a live regex: line-mode
        // regexes have no match timeout, so that would hand it a UI-thread hang.
        Assert.Contains("LastSearchUseRegex = UseRegex;", apply);
        Assert.DoesNotContain("LastSearchUseRegex = options.UseRegex;", apply);

        // Saving records what the results were actually produced with, not the live toggles.
        string capture = ExtractWindow(
            MainViewModelSource,
            "private SessionFileService.SessionSearchOptions CaptureSessionSearchOptions()",
            "private void ApplyLoadedSessionSearchParameters");
        Assert.Contains("string.IsNullOrEmpty(LastSearchPattern)", capture);
        Assert.Contains("Pattern: LastSearchPattern,", capture);
        Assert.Contains("searchOptions: CaptureSessionSearchOptions()", MainViewModelSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // Sortable table structure
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void LoadSession_TableHasSortableColumns()
    {
        Assert.Contains("private enum SortColumn { Name, Directory, Size, Created }", SessionLoadDialogSource);
        Assert.Contains("TextBlock nameHeader = CreateSortableHeader(\"Name\", SortColumn.Name);", SessionLoadDialogSource);
        Assert.Contains("TextBlock dirHeader = CreateSortableHeader(\"Directory\", SortColumn.Directory);", SessionLoadDialogSource);
        Assert.Contains("TextBlock sizeHeader = CreateSortableHeader(\"Size\", SortColumn.Size);", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_DefaultSortByCreatedDescending()
    {
        AssertContainsInOrder(SessionLoadDialogSource,
            "sessions.OrderByDescending(s => s.CreatedUtc ?? DateTimeOffset.MinValue).ToList()",
            "var currentSort = SortColumn.Created;",
            "var currentAscending = false;");
    }

    [Fact]
    public void LoadSession_ColumnHeadersShowSortArrowIndicators()
    {
        AssertContainsInOrder(SessionLoadDialogSource,
            "string arrow = currentAscending ? \" \\u25B2\" : \" \\u25BC\";",
            "nameHeader.Text = \"Name\" + (currentSort == SortColumn.Name ? arrow : \"\");",
            "dirHeader.Text = \"Directory\" + (currentSort == SortColumn.Directory ? arrow : \"\");",
            "sizeHeader.Text = \"Size\" + (currentSort == SortColumn.Size ? arrow : \"\");",
            "createdHeader.Text = \"Created\" + (currentSort == SortColumn.Created ? arrow : \"\");");
    }

    [Fact]
    public void LoadSession_SortTogglesBetweenAscendingAndDescending()
    {
        AssertContainsInOrder(SessionLoadDialogSource,
            "void SortBy(SortColumn column)",
            "if (currentSort == column)",
            "currentAscending = !currentAscending;",
            "currentSort = column;",
            "currentAscending = column is SortColumn.Name or SortColumn.Directory;");
    }

    [Fact]
    public void LoadSession_SortsByAllColumnsCorrectly()
    {
        Assert.Contains("SortColumn.Name => currentAscending", SessionLoadDialogSource);
        Assert.Contains("sortedSessions.OrderBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)", SessionLoadDialogSource);
        Assert.Contains("SortColumn.Directory => currentAscending", SessionLoadDialogSource);
        Assert.Contains("sortedSessions.OrderBy(s => Path.GetDirectoryName(s.Path)", SessionLoadDialogSource);
        Assert.Contains("SortColumn.Size => currentAscending", SessionLoadDialogSource);
        Assert.Contains("sortedSessions.OrderBy(s => s.SizeBytes ?? 0)", SessionLoadDialogSource);
        Assert.Contains("SortColumn.Created => currentAscending", SessionLoadDialogSource);
        Assert.Contains("sortedSessions.OrderBy(s => s.CreatedUtc ?? DateTimeOffset.MinValue)", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_FormatByteSizeUsesSmartUnits()
    {
        AssertContainsInOrder(SessionLoadDialogSource,
            "private static string FormatByteSize(long bytes)",
            "string[] units = [\"B\", \"KB\", \"MB\", \"GB\"];",
            "while (value >= 1024 && unitIndex < units.Length - 1)",
            "value /= 1024;",
            "unitIndex++;");
    }

    [Fact]
    public void LoadSession_FormatByteSizeFormatsCorrectly()
    {
        // Bytes: no decimals
        Assert.Contains("$\"{value:N0} {units[unitIndex]}\"", SessionLoadDialogSource);
        // KB/MB/GB: one decimal
        Assert.Contains("$\"{value:N1} {units[unitIndex]}\"", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_TableRowHasDirectoryTooltip()
    {
        AssertContainsInOrder(SessionLoadDialogSource,
            "private static Grid BuildTableRow(SessionFileCandidate session, Func<SessionFileCandidate, Task> deleteSession)",
            "string directory = Path.GetDirectoryName(session.Path)",
            "TextTrimming = TextTrimming.CharacterEllipsis",
            "ToolTipService.SetToolTip(dirBlock, directory)");
    }

    [Fact]
    public void LoadSession_HeaderColumnSpacingMatchesRow_SoHeadersAlignToCells()
    {
        // The column header grid and BuildTableRow's row grid must use the SAME ColumnSpacing (8), or every
        // header after Name drifts left of its cells by a cumulative 8px per column.
        Assert.Contains("new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 8 };", SessionLoadDialogSource);
        Assert.Contains("var row = new Grid { ColumnSpacing = 8 };", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_TableRowShowsFormattedSizeAndDate()
    {
        Assert.Contains("session.SizeBytes.HasValue ? FormatByteSize(session.SizeBytes.Value) : ", SessionLoadDialogSource);
        Assert.Contains("session.CreatedUtc.Value.ToLocalTime().ToString(\"g\", CultureInfo.CurrentCulture)", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_HeaderClicksCallSortBy()
    {
        Assert.Contains("nameHeader.Tapped += (_, _) => SortBy(SortColumn.Name);", SessionLoadDialogSource);
        Assert.Contains("dirHeader.Tapped += (_, _) => SortBy(SortColumn.Directory);", SessionLoadDialogSource);
        Assert.Contains("sizeHeader.Tapped += (_, _) => SortBy(SortColumn.Size);", SessionLoadDialogSource);
        Assert.Contains("createdHeader.Tapped += (_, _) => SortBy(SortColumn.Created);", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_CreateSortableHeaderHasCorrectStyle()
    {
        AssertContainsInOrder(SessionLoadDialogSource,
            "private static TextBlock CreateSortableHeader(string text, SortColumn _)",
            "FontSize = 12,",
            "FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,",
            "Opacity = 0.8,");
    }

    private static string ExtractWindow(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find start marker '{startMarker}'.");

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find end marker '{endMarker}'.");
        return source[start..end];
    }

    private static void AssertContainsInOrder(string text, params string[] parts)
    {
        int index = 0;
        foreach (var part in parts)
        {
            int found = text.IndexOf(part, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{part}' after index {index}.");
            index = found + part.Length;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}