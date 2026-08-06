namespace Yagu.Tests;

/// <summary>
/// Source-pin tests for the in-app folder browser (UI/Controls/FolderBrowseFlyout.cs). It is WinUI-coupled
/// so it cannot be exercised directly; these guard the properties that make it safe and usable — off-thread
/// enumeration, an explicit "all drives" outcome, and the layout rules WinUI silently breaks.
/// </summary>
public sealed class FolderBrowseFlyoutTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Source = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Controls", "FolderBrowseFlyout.cs"));

    [Fact]
    public void EnumeratesFoldersOffTheUiThread()
    {
        // DriveInfo.GetDrives and Directory.EnumerateDirectories block for seconds on removable/network
        // volumes, which would freeze the drawer the flyout is hosted in.
        Assert.Contains("await Task.Run(() => Enumerate(current))", Source);
        Assert.Contains("private static Listing Enumerate(string? path)", Source);
    }

    [Fact]
    public void ListsDrivesAtTheTopLevelAndSubfoldersBelow()
    {
        string enumerate = Extract(Source, "private static Listing Enumerate");
        Assert.Contains("DriveInfo.GetDrives()", enumerate);
        Assert.Contains("System.IO.Directory.EnumerateDirectories(path)", enumerate);
        // A not-ready drive throws when its label is read, so the walk must survive it.
        Assert.Contains("drive.IsReady", enumerate);
    }

    [Fact]
    public void SurfacesUnreadableFoldersInsteadOfThrowing()
    {
        string enumerate = Extract(Source, "private static Listing Enumerate");
        Assert.Contains("catch (UnauthorizedAccessException)", enumerate);
        Assert.Contains("catch (IOException", enumerate);
        Assert.Contains("listing.Error", enumerate);
    }

    [Fact]
    public void OffersAnExplicitAllDrivesOutcome()
    {
        // Empty means "every drive from its root", so it must be reachable as a deliberate choice.
        Assert.Contains("Content = \"Search all drives\"", Source);
        Assert.Contains("Pick(string.Empty)", Source);
        Assert.Contains("Leave the folder empty to search every drive from its root.", Source);
    }

    [Fact]
    public void SelectingAFolderIsDisabledWhileBrowsingTheDriveList()
    {
        // The drive list has no folder to return, so "Use this folder" must not be clickable there.
        Assert.Contains("selectButton.IsEnabled = current is not null;", Source);
    }

    [Fact]
    public void ReopensAtTheFolderTheFieldCurrentlyPointsAt()
    {
        Assert.Contains("flyout.Opened += (_, _) => Navigate(getInitialPath());", Source);
    }

    [Fact]
    public void WalkingUpFromADriveRootFallsBackToTheDriveList()
    {
        string parent = Extract(Source, "private static string? ParentOf");
        Assert.Contains("GetParent(path.TrimEnd('\\\\', '/'))?.FullName", parent);
        Assert.Contains("return null;", parent);
    }

    [Fact]
    public void WrappingHintUsesAGridNotAHorizontalStackPanel()
    {
        // A horizontal StackPanel measures children at infinite width, which disables TextWrapping.
        int hint = Source.IndexOf("var hint = new TextBlock", StringComparison.Ordinal);
        int footer = Source.IndexOf("var footer = new Grid", StringComparison.Ordinal);
        Assert.True(hint >= 0 && footer > hint, "The wrapping hint must be laid out by a Grid.");
        Assert.Contains("footer.Children.Add(hint);", Source);
    }

    [Fact]
    public void DoesNotUseTheWinAppSdkFolderPicker()
    {
        // FolderPicker.PickSingleFolderAsync throws COMException 0x80004005 in Yagu's unpackaged AOT build.
        Assert.DoesNotContain("FolderPicker", Source);
        Assert.DoesNotContain("PickSingleFolderAsync", Source);
    }

    private static string Extract(string source, string anchor)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Anchor not found: {anchor}");

        int end = source.Length;
        foreach (string boundary in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    /// " })
        {
            int next = source.IndexOf(boundary, start + anchor.Length, StringComparison.Ordinal);
            if (next >= 0 && next < end)
                end = next;
        }
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
