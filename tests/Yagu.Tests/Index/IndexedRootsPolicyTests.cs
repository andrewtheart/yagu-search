using System.Linq;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="IndexedRootsPolicy"/> (plan §6.1): canonicalizing, de-duplicating, adding, and
/// removing the persisted list of folders registered for content indexing.
/// </summary>
public sealed class IndexedRootsPolicyTests
{
    [Fact]
    public void Normalize_DropsBlanksAndDeDuplicatesCaseInsensitively()
    {
        var result = IndexedRootsPolicy.Normalize(new[]
        {
            @"C:\Projects",
            "   ",
            @"C:\Projects\",       // trailing separator → same as C:\Projects
            @"c:\projects",        // different case → same
            @"D:\Data",
            null!,
        });

        Assert.Equal(2, result.Count);
        Assert.Equal(@"C:\Projects", result[0]);
        Assert.Equal(@"D:\Data", result[1]);
    }

    [Fact]
    public void Normalize_Null_ReturnsEmpty()
        => Assert.Empty(IndexedRootsPolicy.Normalize(null));

    [Fact]
    public void Normalize_CapsAtMax()
    {
        var many = Enumerable.Range(0, IndexedRootsPolicy.MaxIndexedRoots + 20)
            .Select(i => $@"C:\r{i}")
            .ToArray();
        Assert.Equal(IndexedRootsPolicy.MaxIndexedRoots, IndexedRootsPolicy.Normalize(many).Count);
    }

    [Fact]
    public void Normalize_DropsNonBlankInputsThatCanonicalizeToEmpty()
    {
        // A bare separator is not whitespace, but NormalizePath collapses/trims it to the empty
        // string, so it must be dropped rather than added as a phantom root.
        var result = IndexedRootsPolicy.Normalize(new[] { "/", @"\", @"C:\Keep" });
        Assert.Equal(new[] { @"C:\Keep" }, result);
    }

    [Fact]
    public void Add_PathThatCanonicalizesToEmpty_IsNoOp()
    {
        var list = IndexedRootsPolicy.Add(new[] { @"C:\a" }, "/");
        Assert.Equal(new[] { @"C:\a" }, list);
    }

    [Fact]
    public void Add_WhenAtCap_IsNoOp()
    {
        var full = Enumerable.Range(0, IndexedRootsPolicy.MaxIndexedRoots).Select(i => $@"C:\r{i}").ToArray();
        var list = IndexedRootsPolicy.Add(full, @"D:\brandnew");
        Assert.Equal(IndexedRootsPolicy.MaxIndexedRoots, list.Count);
        Assert.DoesNotContain(@"D:\brandnew", list);
    }

    [Fact]
    public void Add_NewRoot_Appends()
    {
        var list = IndexedRootsPolicy.Add(new[] { @"C:\a" }, @"D:\b");
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, list);
    }

    [Fact]
    public void Add_ExistingRoot_IsNoOp()
    {
        var list = IndexedRootsPolicy.Add(new[] { @"C:\a" }, @"c:\a\");
        Assert.Single(list);
        Assert.Equal(@"C:\a", list[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normalize_OverlappingParentAndChild_KeepsOnlyBroaderRoot(bool parentFirst)
    {
        string[] roots = parentFirst
            ? new[] { @"C:\", @"C:\src" }
            : new[] { @"C:\src", @"C:\" };

        Assert.Equal(new[] { @"C:\" }, IndexedRootsPolicy.Normalize(roots));
    }

    [Fact]
    public void Add_ChildAlreadyCoveredByParent_DoesNotCreateDuplicateRoot()
    {
        List<string> roots = IndexedRootsPolicy.Add(new[] { @"C:\" }, @"C:\src");

        Assert.Equal(new[] { @"C:\" }, roots);
    }

    [Fact]
    public void Add_Parent_ReplacesCoveredDescendantsButKeepsOtherVolumes()
    {
        List<string> roots = IndexedRootsPolicy.Add(
            new[] { @"C:\src", @"D:\data", @"C:\Users\me" }, @"C:\");

        Assert.Equal(new[] { @"C:\", @"D:\data" }, roots);
    }

    [Fact]
    public void Covers_UsesDirectoryBoundary_NotStringPrefix()
    {
        Assert.True(IndexedRootsPolicy.Covers(@"C:\", @"C:\src"));
        Assert.True(IndexedRootsPolicy.Covers(@"C:\src", @"C:\src\Yagu"));
        Assert.False(IndexedRootsPolicy.Covers(@"C:\src", @"C:\src-old"));
        Assert.False(IndexedRootsPolicy.Covers(@"C:\src", @"D:\src"));
    }

    [Fact]
    public void Covers_BlankOrEmptyNormalizedInput_ReturnsFalse()
    {
        Assert.False(IndexedRootsPolicy.Covers(" ", @"C:\src"));
        Assert.False(IndexedRootsPolicy.Covers(@"C:\src", " "));
        Assert.False(IndexedRootsPolicy.Covers("/", @"C:\src"));
        Assert.False(IndexedRootsPolicy.Covers(@"C:\src", "/"));
    }

    [Fact]
    public void FindBestCoveringRoot_PicksMostSpecificRawOverlap()
    {
        string? covering = IndexedRootsPolicy.FindBestCoveringRoot(
            new[] { @"C:\", @"C:\src", @"D:\" }, @"C:\src\Yagu");

        Assert.Equal(@"C:\src", covering);
        Assert.Collection(
            IndexedRootsPolicy.FindCoveredDescendants(
                new[] { @"C:\src", @"C:\Users\me", @"D:\data" }, @"C:\"),
            root => Assert.Equal(@"C:\src", root),
            root => Assert.Equal(@"C:\Users\me", root));
    }

    [Fact]
    public void FindBestCoveringRoot_NullBlankAndBlankEntries_ReturnNull()
    {
        Assert.Null(IndexedRootsPolicy.FindBestCoveringRoot(null, @"C:\src"));
        Assert.Null(IndexedRootsPolicy.FindBestCoveringRoot(new[] { @"C:\" }, " "));
        Assert.Null(IndexedRootsPolicy.FindBestCoveringRoot(new[] { " ", @"D:\" }, @"C:\src"));
    }

    [Fact]
    public void FindCoveredDescendants_NullOrBlankAncestor_ReturnsEmpty()
    {
        Assert.Empty(IndexedRootsPolicy.FindCoveredDescendants(null, @"C:\"));
        Assert.Empty(IndexedRootsPolicy.FindCoveredDescendants(new[] { @"C:\src" }, " "));
    }

    [Fact]
    public void Add_Blank_IsNoOp()
        => Assert.Single(IndexedRootsPolicy.Add(new[] { @"C:\a" }, "  "));

    [Fact]
    public void Remove_ExistingRoot_RemovesCaseInsensitively()
    {
        var list = IndexedRootsPolicy.Remove(new[] { @"C:\a", @"D:\b" }, @"c:\a");
        Assert.Equal(new[] { @"D:\b" }, list);
    }

    [Fact]
    public void Remove_MissingRoot_IsNoOp()
        => Assert.Equal(2, IndexedRootsPolicy.Remove(new[] { @"C:\a", @"D:\b" }, @"E:\c").Count);

    [Fact]
    public void Contains_MatchesCanonicalized()
    {
        var roots = new[] { @"C:\a", @"D:\b" };
        Assert.True(IndexedRootsPolicy.Contains(roots, @"c:\a\"));
        Assert.False(IndexedRootsPolicy.Contains(roots, @"E:\c"));
        Assert.False(IndexedRootsPolicy.Contains(roots, "  "));
    }

    [Fact]
    public void Normalize_CapsAtMaxIndexedRoots()
    {
        var many = Enumerable.Range(0, IndexedRootsPolicy.MaxIndexedRoots + 20).Select(i => $@"C:\r{i}");
        Assert.Equal(IndexedRootsPolicy.MaxIndexedRoots, IndexedRootsPolicy.Normalize(many).Count);
    }

    [Fact]
    public void Add_AtCap_DoesNotGrow()
    {
        var full = Enumerable.Range(0, IndexedRootsPolicy.MaxIndexedRoots).Select(i => $@"C:\r{i}").ToList();
        var result = IndexedRootsPolicy.Add(full, @"D:\new");
        Assert.Equal(IndexedRootsPolicy.MaxIndexedRoots, result.Count);
        Assert.DoesNotContain(@"D:\new", result);
    }

    [Fact]
    public void Add_BlankPath_IsNoOp()
        => Assert.Equal(1, IndexedRootsPolicy.Add(new[] { @"C:\a" }, "  ").Count);

    [Fact]
    public void Remove_BlankPath_IsNoOp()
        => Assert.Equal(2, IndexedRootsPolicy.Remove(new[] { @"C:\a", @"D:\b" }, "  ").Count);
}
