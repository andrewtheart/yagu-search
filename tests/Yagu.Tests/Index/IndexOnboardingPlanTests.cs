using System.Linq;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>Unit tests for <see cref="IndexOnboardingPlan"/> — the pure "subpart of the path" choices and
/// the large-root warning heuristic behind the index onboarding prompts.</summary>
public sealed class IndexOnboardingPlanTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PathChoices_BlankInput_ReturnsEmpty(string? folder)
    {
        Assert.Empty(IndexOnboardingPlan.PathChoices(folder));
    }

    [Fact]
    public void PathChoices_SeparatorOnlyPath_ReturnsEmpty()
        => Assert.Empty(IndexOnboardingPlan.PathChoices("/"));

    [Fact]
    public void PathChoices_CurrentDriveRootedPath_StopsWhenParentNormalizesToEmpty()
        => Assert.Equal(new[] { @"\folder" }, IndexOnboardingPlan.PathChoices(@"\folder"));

    [Fact]
    public void PathChoices_NestedPath_ReturnsFolderThenAncestorsToDriveRoot()
    {
        var choices = IndexOnboardingPlan.PathChoices(@"C:\Users\andre\src\Yagu");

        Assert.Equal(
            new[] { @"C:\Users\andre\src\Yagu", @"C:\Users\andre\src", @"C:\Users\andre", @"C:\Users", @"C:\" },
            choices);
    }

    [Fact]
    public void PathChoices_MostSpecificFirst_AndDriveRootLast()
    {
        var choices = IndexOnboardingPlan.PathChoices(@"C:\a\b");
        Assert.Equal(@"C:\a\b", choices[0]);
        Assert.Equal(@"C:\", choices[^1]);
    }

    [Fact]
    public void PathChoices_BareDriveLetter_NormalizesToDriveRoot()
    {
        var choices = IndexOnboardingPlan.PathChoices("D:");
        Assert.Equal(new[] { @"D:\" }, choices);
    }

    [Fact]
    public void PathChoices_ForwardSlashesAndTrailingSeparator_AreNormalized()
    {
        var choices = IndexOnboardingPlan.PathChoices("C:/Users/andre/");
        Assert.Equal(new[] { @"C:\Users\andre", @"C:\Users", @"C:\" }, choices);
    }

    [Fact]
    public void PathChoices_DeepPath_IsCappedAtMaxChoices()
    {
        var choices = IndexOnboardingPlan.PathChoices(@"C:\a\b\c\d\e\f\g\h\i\j\k");
        Assert.Equal(IndexOnboardingPlan.MaxPathChoices, choices.Count);
        // Still most-specific first even when capped.
        Assert.Equal(@"C:\a\b\c\d\e\f\g\h\i\j\k", choices[0]);
    }

    [Fact]
    public void PathChoices_ContainsNoDuplicates()
    {
        var choices = IndexOnboardingPlan.PathChoices(@"C:\Users\andre");
        Assert.Equal(choices.Count, choices.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData(@"D:\")]
    public void IsLikelyLargeRoot_BareDriveRoot_IsTrue(string path)
    {
        Assert.True(IndexOnboardingPlan.IsLikelyLargeRoot(path));
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]
    public void IsLikelyLargeRoot_TopLevelKnownHugeDir_IsTrue(string path)
    {
        Assert.True(IndexOnboardingPlan.IsLikelyLargeRoot(path));
    }

    [Theory]
    [InlineData(@"C:\Users\andre")]
    [InlineData(@"C:\Users\andre\src\Yagu")]
    [InlineData(@"D:\Backups\Users")] // "Users" but NOT directly under a drive root
    [InlineData(@"D:\projects\app")]
    public void IsLikelyLargeRoot_OrdinaryOrNestedFolder_IsFalse(string path)
    {
        Assert.False(IndexOnboardingPlan.IsLikelyLargeRoot(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLikelyLargeRoot_BlankInput_IsFalse(string? path)
    {
        Assert.False(IndexOnboardingPlan.IsLikelyLargeRoot(path));
    }

    [Fact]
    public void IsLikelyLargeRoot_SeparatorOnlyPath_IsFalse()
        => Assert.False(IndexOnboardingPlan.IsLikelyLargeRoot("/"));

    [Fact]
    public void IsLikelyLargeRoot_RelativeKnownLeaf_IsFalse()
        => Assert.False(IndexOnboardingPlan.IsLikelyLargeRoot("Users"));

    [Fact]
    public void IsLikelyLargeRoot_UncShareRoot_IsTrue()
    {
        Assert.True(IndexOnboardingPlan.IsLikelyLargeRoot(@"\\server\share"));
    }

    [Fact]
    public void IsLikelyLargeRoot_FolderUnderUncShare_IsFalse()
    {
        Assert.False(IndexOnboardingPlan.IsLikelyLargeRoot(@"\\server\share\projects"));
    }
}
