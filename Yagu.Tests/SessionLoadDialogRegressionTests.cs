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