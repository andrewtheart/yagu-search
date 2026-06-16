using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ResultRowProjectionTests
{
    [Fact]
    public void BuildRows_GroupModeNone_ReturnsGroupsDirectly()
    {
        var group = new FileGroup(@"C:\file.cs");
        group.Add(new SearchResult(@"C:\file.cs", 1, "hello", 0, 5, [], []));

        var rows = ResultRowProjection.BuildRows([group], GroupMode.None, new Dictionary<string, bool>());

        Assert.Single(rows);
        Assert.Same(group, rows[0]);
    }

    [Fact]
    public void BuildRows_GroupModeNone_MultipleGroups_AllReturned()
    {
        var g1 = new FileGroup(@"C:\a.cs");
        var g2 = new FileGroup(@"C:\b.cs");
        g1.Add(new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []));
        g2.Add(new SearchResult(@"C:\b.cs", 2, "y", 0, 1, [], []));

        var rows = ResultRowProjection.BuildRows(new[] { g1, g2 }, GroupMode.None, new Dictionary<string, bool>());

        Assert.Equal(2, rows.Count);
        Assert.Same(g1, rows[0]);
        Assert.Same(g2, rows[1]);
    }

    [Fact]
    public void BuildRows_WithGroupHeaders_CreatesHeaderRowsAndNestsGroups()
    {
        var g1 = new FileGroup(@"C:\src\a.cs") { GroupHeaderText = "src" };
        var g2 = new FileGroup(@"C:\src\b.cs") { GroupHeaderText = "src" };
        g1.Add(new SearchResult(@"C:\src\a.cs", 1, "x", 0, 1, [], []));
        g2.Add(new SearchResult(@"C:\src\b.cs", 2, "y", 0, 1, [], []));

        var expandState = new Dictionary<string, bool>();
        var rows = ResultRowProjection.BuildRows([g1, g2], GroupMode.Folder, expandState);

        // Each time a group has HasGroupHeader, FlushHeaderGroup fires for the previous batch
        // then the current group starts a new batch. Final FlushHeaderGroup after the loop.
        // g1: FlushHeaderGroup (noop—null), sets header="src", adds g1 to headerGroups
        // g2: FlushHeaderGroup (header with g1), sets header="src", adds g2 to headerGroups
        // End: FlushHeaderGroup (header with g2)
        // Result: header(1 file) + g1 + header(1 file) + g2 = 4
        Assert.Equal(4, rows.Count);
        Assert.IsType<ResultGroupHeaderRow>(rows[0]);
        var header = (ResultGroupHeaderRow)rows[0];
        Assert.Equal("src", header.Title);
        Assert.Equal(1, header.FileCount);
        Assert.True(header.IsExpanded);
    }

    [Fact]
    public void BuildRows_CollapsedGroup_OmitsChildren()
    {
        var g1 = new FileGroup(@"C:\src\a.cs") { GroupHeaderText = "src" };
        g1.Add(new SearchResult(@"C:\src\a.cs", 1, "x", 0, 1, [], []));

        string headerKey = ResultRowProjection.BuildHeaderKey(GroupMode.Folder, "src");
        var expandState = new Dictionary<string, bool> { [headerKey] = false };

        var rows = ResultRowProjection.BuildRows([g1], GroupMode.Folder, expandState);

        // Header only, group not included
        Assert.Single(rows);
        var header = Assert.IsType<ResultGroupHeaderRow>(rows[0]);
        Assert.False(header.IsExpanded);
    }

    [Fact]
    public void BuildRows_GroupsWithoutHeaders_AddedDirectly()
    {
        // If a group has no GroupHeaderText, it's added directly without a header
        var g1 = new FileGroup(@"C:\a.cs");
        g1.Add(new SearchResult(@"C:\a.cs", 1, "x", 0, 1, [], []));

        var rows = ResultRowProjection.BuildRows([g1], GroupMode.Folder, new Dictionary<string, bool>());

        Assert.Single(rows);
        Assert.Same(g1, rows[0]);
    }

    [Fact]
    public void BuildRows_MixedHeaderAndNoHeader_HandledCorrectly()
    {
        // First group has no header, second has header
        var g1 = new FileGroup(@"C:\root.cs"); // no header
        var g2 = new FileGroup(@"C:\src\a.cs") { GroupHeaderText = "src" };
        g1.Add(new SearchResult(@"C:\root.cs", 1, "x", 0, 1, [], []));
        g2.Add(new SearchResult(@"C:\src\a.cs", 2, "y", 0, 1, [], []));

        var rows = ResultRowProjection.BuildRows([g1, g2], GroupMode.Folder, new Dictionary<string, bool>());

        // g1 added directly (no header), then g2 gets a header
        Assert.Equal(3, rows.Count);
        Assert.Same(g1, rows[0]);
        Assert.IsType<ResultGroupHeaderRow>(rows[1]);
        Assert.Same(g2, rows[2]);
    }

    [Theory]
    [InlineData(GroupMode.Folder, "src", "1|src")]
    [InlineData(GroupMode.Extension, ".cs", "3|.cs")]
    [InlineData(GroupMode.FileSize, null, "4|")]
    public void BuildHeaderKey_FormatsCorrectly(GroupMode mode, string? text, string expected)
    {
        Assert.Equal(expected, ResultRowProjection.BuildHeaderKey(mode, text));
    }
}
