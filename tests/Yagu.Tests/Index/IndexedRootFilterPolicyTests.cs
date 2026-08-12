using System.Collections.Generic;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="IndexedRootFilterPolicy"/> (plan §6.1): the per-folder build-time glob overrides
/// that layer on top of the global excludes. Canonicalization, lookup by path, and — the load-bearing
/// behavior — the gitignore-style precedence where a per-root include glob re-admits a path a broader
/// exclude would drop (e.g. index <c>node_modules</c> under one folder while excluding it globally).
/// </summary>
public sealed class IndexedRootFilterPolicyTests
{
    private static AppSettings WithGlobalExclude(string globalExclude)
        => new() { EnableContentIndex = true, IndexExcludedGlobs = globalExclude };

    [Fact]
    public void Normalize_Null_ReturnsEmpty()
        => Assert.Empty(IndexedRootFilterPolicy.Normalize(null));

    [Fact]
    public void Normalize_CanonicalizesTrimsDropsBlankAndInert_AndDeDupsLastWins()
    {
        var input = new List<IndexedRootFilter>
        {
            new() { Path = @"C:\a\", ExcludeGlobs = "  **/bin/**  " }, // trailing sep + trim
            new() { Path = "   ", ExcludeGlobs = "x" },               // blank path -> drop
            new() { Path = @"C:\b", IncludeGlobs = "", ExcludeGlobs = "" }, // no globs -> inert -> drop
            new() { Path = @"c:\a", IncludeGlobs = "**/keep/**" },    // same path (case-insensitive) -> last wins
        };

        var result = IndexedRootFilterPolicy.Normalize(input);

        Assert.Single(result);
        // NormalizePath preserves case; last-wins dedup keeps the last entry's path + globs.
        Assert.Equal(@"c:\a", result[0].Path);
        Assert.Equal("**/keep/**", result[0].IncludeGlobs);
        Assert.Equal(string.Empty, result[0].ExcludeGlobs);
    }

    [Fact]
    public void Normalize_NullEntriesAndFieldsAreDropped_AndResultIsCapped()
    {
        var input = new List<IndexedRootFilter>
        {
            null!,
            new() { Path = null!, ExcludeGlobs = "x" },
            new() { Path = @"C:\inert", IncludeGlobs = null!, ExcludeGlobs = null! },
        };
        input.AddRange(Enumerable.Range(0, IndexedRootFilterPolicy.MaxFilters + 1)
            .Select(index => new IndexedRootFilter
            {
                Path = $@"C:\root-{index}",
                ExcludeGlobs = "**/bin/**",
            }));

        List<IndexedRootFilter> result = IndexedRootFilterPolicy.Normalize(input);

        Assert.Equal(IndexedRootFilterPolicy.MaxFilters, result.Count);
        Assert.Equal(@"C:\root-0", result[0].Path);
        Assert.Equal($@"C:\root-{IndexedRootFilterPolicy.MaxFilters - 1}", result[^1].Path);
    }

    [Fact]
    public void Find_MatchesByCanonicalPath()
    {
        var filters = new List<IndexedRootFilter> { new() { Path = @"C:\proj", ExcludeGlobs = "x" } };
        Assert.NotNull(IndexedRootFilterPolicy.Find(filters, @"c:\proj\"));
        Assert.Null(IndexedRootFilterPolicy.Find(filters, @"C:\other"));
        Assert.Null(IndexedRootFilterPolicy.Find(filters, "   "));
        Assert.Null(IndexedRootFilterPolicy.Find(null, @"C:\proj"));
    }

    [Fact]
    public void Find_SkipsNullEntriesAndNullPaths()
    {
        var expected = new IndexedRootFilter { Path = @"C:\proj", ExcludeGlobs = "x" };
        var filters = new List<IndexedRootFilter>
        {
            null!,
            new() { Path = null!, ExcludeGlobs = "x" },
            expected,
        };

        Assert.Same(expected, IndexedRootFilterPolicy.Find(filters, @"c:\proj\"));
    }

    [Fact]
    public void ResolvePolicy_NoFilter_UsesGlobalExcludesOnly()
    {
        var settings = WithGlobalExclude("**/node_modules/**");
        var policy = IndexedRootFilterPolicy.ResolvePolicy(settings, @"C:\other");

        Assert.True(policy.IsGloballyExcluded(@"C:\other\node_modules\x.js"));
        Assert.False(policy.IsGloballyExcluded(@"C:\other\src\x.js"));
        Assert.Empty(policy.ReAdmitGlobs);
    }

    [Fact]
    public void ResolvePolicy_PerRootInclude_ReAdmitsAGloballyExcludedPath_ForThatRootOnly()
    {
        // The load-bearing case: exclude node_modules globally, but index it under C:\test\andrew.
        var settings = WithGlobalExclude("**/node_modules/**");
        settings.IndexedRootFilters = new List<IndexedRootFilter>
        {
            new() { Path = @"C:\test\andrew", IncludeGlobs = "**/node_modules/**" },
        };

        var here = IndexedRootFilterPolicy.ResolvePolicy(settings, @"C:\test\andrew");
        Assert.False(here.IsGloballyExcluded(@"C:\test\andrew\node_modules\pkg\index.js")); // re-admitted here
        Assert.NotEmpty(here.ReAdmitGlobs);

        var elsewhere = IndexedRootFilterPolicy.ResolvePolicy(settings, @"C:\test\bob");
        Assert.True(elsewhere.IsGloballyExcluded(@"C:\test\bob\node_modules\pkg\index.js")); // still excluded elsewhere
    }

    [Fact]
    public void ResolvePolicy_PerRootExclude_AddsOnTopOfGlobalExcludes()
    {
        var settings = WithGlobalExclude(string.Empty); // no global excludes
        settings.IndexedRootFilters = new List<IndexedRootFilter>
        {
            new() { Path = @"C:\proj", ExcludeGlobs = "**/dist/**" },
        };

        var policy = IndexedRootFilterPolicy.ResolvePolicy(settings, @"C:\proj");
        Assert.True(policy.IsGloballyExcluded(@"C:\proj\dist\bundle.js"));
        Assert.False(policy.IsGloballyExcluded(@"C:\proj\src\a.ts"));
    }

    [Fact]
    public void AddExcludedPaths_PreservesExistingPatternsAndNormalizesLiteralPaths()
    {
        var filters = new[]
        {
            new IndexedRootFilter
            {
                Path = @"C:\",
                IncludeGlobs = @"C:\Windows\Temp\keep",
                ExcludeGlobs = "**/bin/**",
            },
        };

        List<IndexedRootFilter> result = IndexedRootFilterPolicy.AddExcludedPaths(
            filters,
            @"c:\",
            [@"C:/Windows", @"c:\WINDOWS", @"C:\Program Files"]);

        IndexedRootFilter filter = Assert.Single(result);
        Assert.Equal(@"C:\Windows\Temp\keep", filter.IncludeGlobs);
        Assert.Equal("**/bin/**; C:\\Windows; C:\\Program Files", filter.ExcludeGlobs);

        var settings = WithGlobalExclude(string.Empty);
        settings.IndexedRootFilters = result;
        IndexIngestionPolicy policy = IndexedRootFilterPolicy.ResolvePolicy(settings, @"C:\");
        Assert.True(policy.IsGloballyExcluded(@"c:\windows\system32\kernel32.dll"));
        Assert.True(policy.IsGloballyExcluded(@"C:\PROGRAM FILES\Yagu\Yagu.exe"));
        Assert.False(policy.IsGloballyExcluded(@"C:\Windows-old\notes.txt"));
    }

    [Fact]
    public void AddExcludedPaths_KeepsACommaInsideALiteralPathAsOneEntry()
    {
        List<IndexedRootFilter> result = IndexedRootFilterPolicy.AddExcludedPaths(
            null,
            @"C:\",
            [@"C:\Program Files\Acme, Inc"]);

        var settings = WithGlobalExclude(string.Empty);
        settings.IndexedRootFilters = result;
        IndexIngestionPolicy policy = IndexedRootFilterPolicy.ResolvePolicy(settings, @"C:\");
        Assert.True(policy.IsGloballyExcluded(@"C:\Program Files\Acme, Inc\app.dll"));
        Assert.False(policy.IsGloballyExcluded(@"C:\Program Files\Acme\app.dll"));
        Assert.False(policy.IsGloballyExcluded(@"C:\Inc\app.dll"));
    }

    [Fact]
    public void AddExcludedPaths_InvalidRootOrMissingPaths_LeavesExistingFiltersUnchanged()
    {
        var existing = new[]
        {
            new IndexedRootFilter { Path = @"C:\existing", ExcludeGlobs = "**/bin/**" },
        };

        foreach (List<IndexedRootFilter> result in new[]
        {
            IndexedRootFilterPolicy.AddExcludedPaths(existing, "   ", [@"C:\Windows"]),
            IndexedRootFilterPolicy.AddExcludedPaths(existing, @"C:\", null),
        })
        {
            IndexedRootFilter filter = Assert.Single(result);
            Assert.Equal(@"C:\existing", filter.Path);
            Assert.Equal("**/bin/**", filter.ExcludeGlobs);
            Assert.Equal(string.Empty, filter.IncludeGlobs);
        }
    }

    [Fact]
    public void AddExcludedPaths_EmptyOrBlankPaths_DoNotCreateAnInertFilter()
    {
        Assert.Empty(IndexedRootFilterPolicy.AddExcludedPaths(null, @"C:\", []));
        Assert.Empty(IndexedRootFilterPolicy.AddExcludedPaths(null, @"C:\", [null!, "", "   "]));
    }

    [Fact]
    public void FindRootsAffectedByLiteralPathFilters_GlobalLiteralPath_AffectsEveryRoot()
    {
        var settings = WithGlobalExclude(@"**/bin/**; C:\Windows");
        settings.IndexedRoots = [@"C:\src", @"D:\data"];

        Assert.Equal(
            [@"C:\src", @"D:\data"],
            IndexedRootFilterPolicy.FindRootsAffectedByLiteralPathFilters(settings));
    }

    [Fact]
    public void FindRootsAffectedByLiteralPathFilters_PerRootLiteralPath_AffectsOnlyThatRoot()
    {
        var settings = WithGlobalExclude("**/bin/**");
        settings.IndexedRoots = [@"C:\src", @"D:\data"];
        settings.IndexedRootFilters =
        [
            new IndexedRootFilter { Path = @"C:\src", ExcludeGlobs = "*.min.js" },
            new IndexedRootFilter { Path = @"D:\data", ExcludeGlobs = @"D:\data\archive" },
        ];

        Assert.Equal([@"D:\data"], IndexedRootFilterPolicy.FindRootsAffectedByLiteralPathFilters(settings));
    }

    [Fact]
    public void FindRootsAffectedByLiteralPathFilters_GlobsOnly_ReturnsEmpty()
    {
        var settings = WithGlobalExclude("**/bin/**, *.min.js, node_modules");
        settings.IndexedRoots = [@"C:\src"];
        settings.IndexedRootFilters =
        [
            new IndexedRootFilter { Path = @"C:\src", IncludeGlobs = @"C:\src\**\keep\**" },
        ];

        Assert.Empty(IndexedRootFilterPolicy.FindRootsAffectedByLiteralPathFilters(settings));
    }

    [Fact]
    public void FindRootsAffectedByLiteralPathFilters_NoRegisteredRoots_ReturnsEmpty()
    {
        var settings = WithGlobalExclude(@"C:\Windows");

        Assert.Empty(IndexedRootFilterPolicy.FindRootsAffectedByLiteralPathFilters(settings));
    }
}
