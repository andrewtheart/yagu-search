using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Covers the rule that an archive matching on its file name only must never be previewed as raw bytes,
/// while archives that also matched on content (or on content only) keep their normal preview. The
/// predicate is a pure helper (real unit tests); the MainWindow wiring that consumes it is source-pinned
/// because WinUI-coupled files are not compiled into Yagu.Tests.
/// </summary>
public sealed class ArchivePreviewSupportTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PreviewBuilderSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewBuilder.cs"));

    [Theory]
    [InlineData(@"C:\temp\bundle.zip")]
    [InlineData(@"C:\temp\lib.jar")]
    [InlineData(@"C:\temp\package.nupkg")]
    [InlineData(@"C:\temp\report.docx")]
    [InlineData(@"C:\temp\archive.7z")]
    [InlineData(@"C:\temp\image.iso")]
    [InlineData(@"C:\temp\UPPER.ZIP")]
    public void IsArchiveContainerPath_IsTrueForArchiveContainers(string path)
    {
        Assert.True(ArchivePreviewSupport.IsArchiveContainerPath(path));
    }

    [Theory]
    [InlineData(@"C:\temp\notes.txt")]
    [InlineData(@"C:\temp\Program.cs")]
    [InlineData(@"C:\temp\no-extension")]
    [InlineData(@"C:\temp\report.pdf")]
    [InlineData("")]
    [InlineData(null)]
    public void IsArchiveContainerPath_IsFalseForReadableFiles(string? path)
    {
        Assert.False(ArchivePreviewSupport.IsArchiveContainerPath(path));
    }

    [Fact]
    public void IsArchiveContainerPath_TreatsFilesInsideAnArchiveAsPreviewable()
    {
        // Entries extracted out of an archive are previewed from their decompressed bytes, so only the
        // trailing segment decides — "outer.zip?/readme.txt" is text, "outer.zip?/inner.zip" is not.
        Assert.False(ArchivePreviewSupport.IsArchiveContainerPath(@"C:\temp\outer.zip?/readme.txt"));
        Assert.False(ArchivePreviewSupport.IsArchiveContainerPath(@"C:\temp\outer.zip?/docs/notes.md"));
        Assert.True(ArchivePreviewSupport.IsArchiveContainerPath(@"C:\temp\outer.zip?/inner.zip"));
        Assert.True(ArchivePreviewSupport.IsArchiveContainerPath(@"C:\temp\outer.zip?/nested/inner.7z"));
    }

    [Fact]
    public void IsFileNameOnlyArchivePreview_OnlyBlocksArchivesWithoutAContentMatch()
    {
        // LineNumber 0 marks a file-name match.
        Assert.True(ArchivePreviewSupport.IsFileNameOnlyArchivePreview(@"C:\temp\bundle.zip", 0));

        // A content match inside the archive's raw bytes still previews (this is today's behavior for
        // archives that matched on both name and content, or on content only).
        Assert.False(ArchivePreviewSupport.IsFileNameOnlyArchivePreview(@"C:\temp\bundle.zip", 12));

        // Non-archives keep their file-name-only whole-file preview.
        Assert.False(ArchivePreviewSupport.IsFileNameOnlyArchivePreview(@"C:\temp\notes.txt", 0));
    }

    [Fact]
    public void BuildFileNameOnlyArchivePreviewMessage_NamesTheArchiveAndPointsAtArchiveSearch()
    {
        string message = ArchivePreviewSupport.BuildFileNameOnlyArchivePreviewMessage(@"C:\temp\bundle.zip");
        Assert.Contains("bundle.zip", message);
        Assert.Contains("matched on its file name only", message);
        Assert.Contains("Search inside archives", message);

        Assert.StartsWith("This archive", ArchivePreviewSupport.BuildFileNameOnlyArchivePreviewMessage(null));
    }

    [Fact]
    public void ShowSingleFilePreview_ReplacesFileNameOnlyArchiveBytesWithANote()
    {
        // Reading the container at all would decode its raw bytes into mojibake, so the guard must run
        // before the read and must also block the full-file entry point.
        Assert.Contains(
            "bool isFileNameOnlyArchivePreview = ArchivePreviewSupport.IsFileNameOnlyArchivePreview(r.FilePath, r.LineNumber);",
            PreviewBuilderSource);
        Assert.Contains(
            "FullFileButton.IsEnabled = !isExtractedTextPreview && !isFileNameOnlyArchivePreview;",
            PreviewBuilderSource);
        AssertContainsInOrder(PreviewBuilderSource,
            "bool isFileNameOnlyArchivePreview = ArchivePreviewSupport.IsFileNameOnlyArchivePreview(r.FilePath, r.LineNumber);",
            "if (isFileNameOnlyArchivePreview)",
            "AddPreviewPlainNote(PreviewBlock, ArchivePreviewSupport.BuildFileNameOnlyArchivePreviewMessage(r.FilePath));");
    }

    [Fact]
    public void BuildConcatenatedSection_ShowsTheNoteOnlyWhenEveryResultIsAnArchiveFileNameMatch()
    {
        Assert.Contains(
            "private static bool IsFileNameOnlyArchiveSection(List<SearchResult> results)",
            PreviewBuilderSource);
        AssertContainsInOrder(PreviewBuilderSource,
            "private static bool IsFileNameOnlyArchiveSection(List<SearchResult> results)",
            "=> results.Count > 0",
            "&& results.All(result => result.LineNumber <= 0)",
            "&& ArchivePreviewSupport.IsArchiveContainerPath(results[0].FilePath);");

        AssertContainsInOrder(PreviewBuilderSource,
            "private void BuildConcatenatedSection(",
            "if (IsFileNameOnlyArchiveSection(results))",
            "AddPreviewPlainNote(section, ArchivePreviewSupport.BuildFileNameOnlyArchivePreviewMessage(results[0].FilePath));",
            "SyncGutterSpacer(section, new Thickness(0));");
    }

    [Fact]
    public void SectionLoading_SkipsReadingArchivesThatOnlyRenderTheNote()
    {
        AssertContainsInOrder(PreviewBuilderSource,
            "private async Task<Dictionary<string, string[]?>> ReadAllFileContentsAsync(",
            "if (IsFileNameOnlyArchiveSection(results))",
            "result[filePath] = null;");

        Assert.Contains(
            "if (allLines is null && !IsFileNameOnlyArchiveSection(lazy.Results))",
            PreviewBuilderSource);
    }

    private static void AssertContainsInOrder(string source, params string[] fragments)
    {
        int index = 0;
        foreach (var fragment in fragments)
        {
            int found = source.IndexOf(fragment, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{fragment}' after index {index}.");
            index = found + fragment.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
