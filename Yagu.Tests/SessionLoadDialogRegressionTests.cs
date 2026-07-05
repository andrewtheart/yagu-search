using System.Globalization;

namespace Yagu.Tests;

public sealed class SessionLoadDialogRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PreviewCommandsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewCommands.cs"));
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
    private static readonly string SessionLoadDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "SessionLoadDialog.cs"));
    private static readonly string YaguDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "YaguDialog.cs"));

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
            "private async void OnLoadSession",
            "private async Task<string?> ChooseSessionFileToLoadAsync");
        AssertContainsInOrder(loadCommand,
            "string previousStatusText = ViewModel.StatusText;",
            "string? path = await ChooseSessionFileToLoadAsync(previousStatusText);",
            "if (path is null) return;");

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
        AssertContainsInOrder(SessionLoadDialogSource,
            "var header = new StackPanel",
            "Text = \"Load session\"",
            "Saved sessions reopen previous Yagu results without rerunning the search.",
            "Select a .yagu-session file from the list, or choose Browse... to pick one manually.",
            "No .yagu-session files found by Everything.",
            "root.Children.Add(header);");

        Assert.Contains("FontWeight = Microsoft.UI.Text.FontWeights.SemiBold", SessionLoadDialogSource);
        Assert.Contains("TextWrapping = TextWrapping.WrapWholeWords", SessionLoadDialogSource);
    }

    [Fact]
    public void LoadSession_UsesExistingViewModelLoadPathAfterSelection()
    {
        Assert.Contains("private async Task LoadSessionFileAsync(string path)", PreviewCommandsSource);
        AssertContainsInOrder(PreviewCommandsSource,
            "ClearPreviewStateForSessionLoad();",
            "var header = await ViewModel.LoadSessionAsync(path);",
            "Load session failed: {path}");
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
            "private static Grid BuildTableRow(SessionFileCandidate session)",
            "string directory = Path.GetDirectoryName(session.Path)",
            "TextTrimming = TextTrimming.CharacterEllipsis",
            "ToolTipService.SetToolTip(dirBlock, directory)");
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}