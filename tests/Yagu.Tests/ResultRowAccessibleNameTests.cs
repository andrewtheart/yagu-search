using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="ResultRowAccessibleName"/>. Without an explicit automation name a WinUI
/// ListView announces each row as the item's type name — live UIAutomation showed every results row
/// reporting "Yagu.Models.FileGroup" to screen readers.
/// </summary>
public sealed class ResultRowAccessibleNameTests
{
    [Fact]
    public void ForFileGroup_AnnouncesFileMatchesDirectoryAndState()
        => Assert.Equal(
            "notes.txt, 3 matches, in C:\\work, collapsed",
            ResultRowAccessibleName.ForFileGroup("notes.txt", "C:\\work", 3, isExpanded: false));

    [Fact]
    public void ForFileGroup_SingleMatch_UsesTheSingular()
        => Assert.Contains("1 match,", ResultRowAccessibleName.ForFileGroup("a.txt", "C:\\", 1, isExpanded: false));

    [Fact]
    public void ForFileGroup_ManyMatches_AreGrouped()
        => Assert.Contains($"{12345:N0} matches", ResultRowAccessibleName.ForFileGroup("a.txt", "C:\\", 12345, isExpanded: false));

    [Fact]
    public void ForFileGroup_ReportsExpansionState()
    {
        Assert.EndsWith("expanded", ResultRowAccessibleName.ForFileGroup("a.txt", "C:\\", 1, isExpanded: true));
        Assert.EndsWith("collapsed", ResultRowAccessibleName.ForFileGroup("a.txt", "C:\\", 1, isExpanded: false));
    }

    [Fact]
    public void ForFileGroup_MissingDirectory_IsOmittedRatherThanAnnouncedEmpty()
    {
        string name = ResultRowAccessibleName.ForFileGroup("a.txt", "  ", 2, isExpanded: false);

        Assert.Equal("a.txt, 2 matches, collapsed", name);
        Assert.DoesNotContain(" in ,", name);
    }

    [Fact]
    public void ForFileGroup_MissingFileName_StillAnnouncesSomething()
        => Assert.StartsWith("(unnamed file)", ResultRowAccessibleName.ForFileGroup("", "C:\\", 1, isExpanded: false));

    [Fact]
    public void ForGroupHeader_AnnouncesTitleSummaryAndState()
        => Assert.Equal(
            "C:\\work, 2 files | 5 matches, expanded",
            ResultRowAccessibleName.ForGroupHeader("C:\\work", "2 files | 5 matches", isExpanded: true));

    [Fact]
    public void ForGroupHeader_MissingSummary_IsOmitted()
        => Assert.Equal("C:\\work, collapsed", ResultRowAccessibleName.ForGroupHeader("C:\\work", "", isExpanded: false));

    [Fact]
    public void ForGroupHeader_MissingTitle_StillAnnouncesSomething()
        => Assert.StartsWith("(unnamed group)", ResultRowAccessibleName.ForGroupHeader(" ", "1 file", isExpanded: false));

    [Fact]
    public void For_GroupHeaderRow_UsesItsTitleAndSummary()
    {
        var row = new ResultGroupHeaderRow("k", "C:\\work", fileCount: 2, matchCount: 5, isExpanded: true);

        string? name = ResultRowAccessibleName.For(row);

        Assert.NotNull(name);
        Assert.Contains("C:\\work", name);
        Assert.Contains(row.SummaryText, name);
        Assert.DoesNotContain(nameof(ResultGroupHeaderRow), name);
    }

    [Fact]
    public void For_UnknownRow_ReturnsNullSoTheContainerNameIsLeftAlone()
    {
        Assert.Null(ResultRowAccessibleName.For(null));
        Assert.Null(ResultRowAccessibleName.For("a plain string"));
        Assert.Null(ResultRowAccessibleName.For(new object()));
    }

    [Fact]
    public void EveryProducedName_IsHumanReadable_NotATypeName()
    {
        string[] names =
        [
            ResultRowAccessibleName.ForFileGroup("a.txt", "C:\\", 1, isExpanded: false),
            ResultRowAccessibleName.ForGroupHeader("C:\\", "1 file", isExpanded: false),
        ];

        Assert.All(names, n => Assert.DoesNotContain("Yagu.Models", n, StringComparison.Ordinal));
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void ExpansionHandlers_RefreshTheMaterializedContainerName()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Yagu",
            "UI",
            "Windows",
            "MainWindow",
            "MainWindow.ResultsSelection.cs"));

        Assert.Contains("UpdateFileGroupAccessibleName(g, isExpanded: true);", source);
        Assert.Contains("UpdateFileGroupAccessibleName(g, isExpanded: false);", source);
        Assert.Contains("ResultsList.ContainerFromItem(group)", source);
        Assert.Contains("ResultRowAccessibleName.ForFileGroup(", source);
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Yagu.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
