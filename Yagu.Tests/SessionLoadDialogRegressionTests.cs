namespace Yagu.Tests;

public sealed class SessionLoadDialogRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PreviewCommandsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewCommands.cs"));
    private static readonly string SessionLoadDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "SessionLoadDialog.cs"));
    private static readonly string YaguDialogSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "YaguDialog.cs"));

    [Fact]
    public void LoadSession_UsesFastDiscoveryBeforeNativePickerFallback()
    {
        Assert.Contains("private async Task<string?> ChooseSessionFileToLoadAsync()", PreviewCommandsSource);
        AssertContainsInOrder(PreviewCommandsSource,
            "new SessionFileDiscoveryService().FindSessionFilesAsync(discoveryCts.Token)",
            "if (!discovery.FastSearchAvailable)",
            "return await PickSessionFileWithWindowsDialogAsync();",
            "SessionLoadDialog.ShowAsync");
    }

    [Fact]
    public void LoadSession_CustomModalIsCenteredSingleSelectionAndBrowseCapable()
    {
        Assert.Contains("YaguDialog.ShowAsync", SessionLoadDialogSource);
        Assert.Contains("WindowForegroundHelper.CenterWindowOverOwner", YaguDialogSource);
        Assert.Contains("Pick a saved session", SessionLoadDialogSource);
        Assert.Contains("Use the column headers to sort by file name, parent folder, size, or last modified time.", SessionLoadDialogSource);
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
    public void LoadSession_CustomModalUsesSortableTableColumns()
    {
        Assert.Contains("internal enum SessionLoadSortColumn", SessionLoadDialogSource);
        Assert.Contains("BuildSessionTable", SessionLoadDialogSource);
        Assert.Contains("CreateSortHeaderButton(\"File name\", HorizontalAlignment.Left)", SessionLoadDialogSource);
        Assert.Contains("CreateSortHeaderButton(\"Parent folder\", HorizontalAlignment.Left)", SessionLoadDialogSource);
        Assert.Contains("CreateSortHeaderButton(\"Size\", HorizontalAlignment.Right)", SessionLoadDialogSource);
        Assert.Contains("CreateSortHeaderButton(\"Modified\", HorizontalAlignment.Right)", SessionLoadDialogSource);
        Assert.Contains("HorizontalContentAlignment = contentAlignment", SessionLoadDialogSource);
        Assert.Contains("SortSessionCandidates", SessionLoadDialogSource);
        Assert.Contains("GetParentDirectory(session.Path)", SessionLoadDialogSource);
        Assert.Contains("FormatSize(session.SizeBytes)", SessionLoadDialogSource);
        Assert.Contains("FormatModified(session.ModifiedUtc)", SessionLoadDialogSource);
        Assert.DoesNotContain("FormatMetadata", SessionLoadDialogSource);

        AssertContainsInOrder(SessionLoadDialogSource,
            "var currentSortColumn = SessionLoadSortColumn.Modified;",
            "var sortAscending = false;",
            "CreateSortHeaderButton(\"File name\", HorizontalAlignment.Left)",
            "CreateSortHeaderButton(\"Parent folder\", HorizontalAlignment.Left)",
            "CreateSortHeaderButton(\"Size\", HorizontalAlignment.Right)",
            "CreateSortHeaderButton(\"Modified\", HorizontalAlignment.Right)");
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
    public void LoadSession_UsesExistingViewModelLoadPathAfterSelection()
    {
        Assert.Contains("private async Task LoadSessionFileAsync(string path)", PreviewCommandsSource);
        AssertContainsInOrder(PreviewCommandsSource,
            "EnsureResultsListVisibleForSessionLoad();",
            "ClearPreviewStateForSessionLoad();",
            "var header = await ViewModel.LoadSessionAsync(path);",
            "Load session failed: {path}");
        AssertContainsInOrder(PreviewCommandsSource,
            "private void EnsureResultsListVisibleForSessionLoad()",
            "if (_launcherMode)",
            "ExitLauncherMode();",
            "_resultsPaneCollapsed = false;",
            "SplitPaneGrid.Visibility = Visibility.Visible;",
            "ApplySplitLayout(SplitLayoutMode.ResultsMaximized);",
            "UpdateBottomStatusBarVisibility();");
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