namespace Yagu.Tests;

/// <summary>
/// Regression coverage for the "Added to preview" snackbar — the dismissible toast shown over the
/// full-file editor when files are previewed from the left panel while the editor is open (the
/// preview surface is hidden behind the editor, so the user would otherwise get no confirmation).
///
/// The behavior lives entirely in WinUI-coupled <c>MainWindow</c> partial classes and XAML, which are
/// not compiled into Yagu.Tests, so these tests pin the source-level contracts (per the repo's
/// source-pin convention).
/// </summary>
public sealed class PreviewAddedToastRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string PreviewEditorSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewEditor.cs"));
    private static readonly string PreviewCommandsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewCommands.cs"));

    [Fact]
    public void Xaml_SnackbarIsBottomAnchoredAndCollapsedByDefault()
    {
        // The toast lives inside the editor's grid (Canvas.ZIndex over the editor) and starts hidden.
        Assert.Contains("x:Name=\"PreviewAddedToast\"", MainWindowXaml);
        AssertContainsInOrder(MainWindowXaml,
            "x:Name=\"PreviewAddedToast\"",
            "Visibility=\"Collapsed\"",
            "VerticalAlignment=\"Bottom\"");
        Assert.Contains("x:Name=\"PreviewAddedToastText\" Text=\"Added to preview\"", MainWindowXaml);
    }

    [Fact]
    public void Xaml_SnackbarHasViewInPreviewAndDismissActions()
    {
        AssertContainsInOrder(MainWindowXaml,
            "x:Name=\"PreviewAddedToast\"",
            "Click=\"OnPreviewAddedToastViewClick\"",
            "Text=\"View in preview\"",
            "Click=\"OnPreviewAddedToastDismissClick\"");
    }

    [Fact]
    public void ShowToast_SetsTargetPluralizesTextAndAutoHides()
    {
        AssertContainsInOrder(PreviewEditorSource,
            "private void ShowPreviewAddedToast(string targetFile, int addedCount)",
            "if (string.IsNullOrEmpty(targetFile))",
            "return;",
            "_previewAddedToastTargetFile = targetFile;",
            "PreviewAddedToastText.Text = addedCount > 1",
            "$\"{addedCount} files added to preview\"",
            ": \"Added to preview\";",
            "PreviewAddedToast.Visibility = Visibility.Visible;");
    }

    [Fact]
    public void ShowToast_UsesAnAutoHideTimerThatRestartsOnEachShow()
    {
        AssertContainsInOrder(PreviewEditorSource,
            "_previewAddedToastTimer = DispatcherQueue.CreateTimer();",
            "_previewAddedToastTimer.Interval = TimeSpan.FromSeconds(3);",
            "_previewAddedToastTimer.Tick += (_, _) => HidePreviewAddedToast();",
            "_previewAddedToastTimer.Stop();",
            "_previewAddedToastTimer.Start();");
    }

    [Fact]
    public void HideToast_StopsTimerAndCollapsesTheBanner()
    {
        AssertContainsInOrder(PreviewEditorSource,
            "private void HidePreviewAddedToast()",
            "_previewAddedToastTimer?.Stop();",
            "PreviewAddedToast.Visibility = Visibility.Collapsed;");
    }

    [Fact]
    public void ViewInPreview_LeavesEditorRespectingUnsavedEditsThenScrollsToDrawer()
    {
        AssertContainsInOrder(PreviewEditorSource,
            "private async void OnPreviewAddedToastViewClick(object sender, RoutedEventArgs e)",
            "string? target = _previewAddedToastTargetFile;",
            "HidePreviewAddedToast();",
            "if (PreviewEditor.Visibility == Visibility.Visible)",
            "if (HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync())",
            "return;",
            "ClosePreviewEditor();",
            "TryScrollToPreviewSection(target!));");
    }

    [Fact]
    public void DismissButton_JustHidesTheToast()
    {
        Assert.Contains(
            "private void OnPreviewAddedToastDismissClick(object sender, RoutedEventArgs e) => HidePreviewAddedToast();",
            PreviewEditorSource);
    }

    [Fact]
    public void EditorVisibilityChange_AlwaysDismissesTheToastSoItCanNeverGoStale()
    {
        // SetPreviewEditorVisible hides the toast on ANY visibility change (show or hide).
        AssertContainsInOrder(PreviewEditorSource,
            "private void SetPreviewEditorVisible(bool visible)",
            "HidePreviewAddedToast();");
    }

    [Fact]
    public void PreviewCallSites_OnlyShowToastWhileTheEditorIsVisible()
    {
        // Single-group preview path.
        AssertContainsInOrder(PreviewCommandsSource,
            "if (PreviewEditor.Visibility == Visibility.Visible)",
            "ShowPreviewAddedToast(group.FilePath, byFile.Count);");

        // Multi-file selection path targets the last-added drawer.
        AssertContainsInOrder(PreviewCommandsSource,
            "if (PreviewEditor.Visibility == Visibility.Visible)",
            "string? toastTarget = newFiles.Count > 0",
            "? newFiles.Keys.Last()",
            "ShowPreviewAddedToast(toastTarget, byFile.Count);");
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int position = 0;
        foreach (var item in expected)
        {
            int found = text.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
