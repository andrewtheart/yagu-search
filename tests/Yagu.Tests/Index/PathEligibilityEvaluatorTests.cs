using System;
using System.Collections.Generic;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="PathEligibilityEvaluator"/> (plan §5): the stateless early-lane filter
/// reproduction (root, hidden, size, date, extension, glob) and the gitignore-disables-the-lane gate.
/// </summary>
public sealed class PathEligibilityEvaluatorTests
{
    private static SearchOptions Options(
        string directory = @"C:\src",
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null,
        IReadOnlySet<string>? skip = null,
        bool searchHidden = true,
        long minSize = 0,
        long maxSize = 0,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        bool obeyGitignore = false)
        => new()
        {
            Directory = directory,
            Query = "x",
            IncludeGlobs = includeGlobs ?? Array.Empty<string>(),
            ExcludeGlobs = excludeGlobs ?? Array.Empty<string>(),
            SkipExtensions = skip ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SearchHiddenFiles = searchHidden,
            MinFileSizeBytes = minSize,
            MaxFileSizeBytes = maxSize,
            CreatedAfterDate = createdAfter,
            CreatedBeforeDate = createdBefore,
            ObeyGitignore = obeyGitignore,
        };

    private static PathEligibilityCandidate File(
        string path = @"C:\src\a\b.cs",
        long size = 100,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null,
        bool hidden = false)
        => new(path, size, created, modified, hidden);

    [Fact]
    public void CanEvaluate_FalseWhenGitignoreActive()
    {
        Assert.False(new PathEligibilityEvaluator(Options(obeyGitignore: true)).CanEvaluate);
        Assert.True(new PathEligibilityEvaluator(Options(obeyGitignore: false)).CanEvaluate);
    }

    [Fact]
    public void Evaluate_Eligible_NormalFileUnderRoot()
        => Assert.Equal(PathEligibilityResult.Eligible,
            new PathEligibilityEvaluator(Options()).Evaluate(File()));

    [Fact]
    public void Evaluate_ExcludedByRoot()
        => Assert.Equal(PathEligibilityResult.ExcludedByRoot,
            new PathEligibilityEvaluator(Options()).Evaluate(File(path: @"D:\other\x.cs")));

    [Fact]
    public void Evaluate_EmptyRoot_AllowsAnyPath()
        => Assert.Equal(PathEligibilityResult.Eligible,
            new PathEligibilityEvaluator(Options(directory: "")).Evaluate(File(path: @"Z:\deep\x.cs")));

    [Fact]
    public void Evaluate_RootItself_IsUnderRoot()
        => Assert.Equal(PathEligibilityResult.Eligible,
            new PathEligibilityEvaluator(Options(directory: @"C:\src")).Evaluate(File(path: @"C:\src")));

    [Fact]
    public void Evaluate_ExtensionlessFile_IsNotSkipped_EvenWithSkipList()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tmp", "log" };
        Assert.Equal(PathEligibilityResult.Eligible,
            new PathEligibilityEvaluator(Options(skip: skip)).Evaluate(File(path: @"C:\src\Makefile")));
    }

    [Fact]
    public void Evaluate_ExcludedByHidden_WhenNotSearchingHidden()
        => Assert.Equal(PathEligibilityResult.ExcludedByHidden,
            new PathEligibilityEvaluator(Options(searchHidden: false)).Evaluate(File(hidden: true)));

    [Fact]
    public void Evaluate_HiddenAllowed_WhenSearchingHidden()
        => Assert.Equal(PathEligibilityResult.Eligible,
            new PathEligibilityEvaluator(Options(searchHidden: true)).Evaluate(File(hidden: true)));

    [Theory]
    [InlineData(5, PathEligibilityResult.ExcludedBySize)]   // below min
    [InlineData(5000, PathEligibilityResult.ExcludedBySize)] // above max
    [InlineData(500, PathEligibilityResult.Eligible)]
    public void Evaluate_SizeRange(long size, PathEligibilityResult expected)
        => Assert.Equal(expected,
            new PathEligibilityEvaluator(Options(minSize: 10, maxSize: 1000)).Evaluate(File(size: size)));

    [Fact]
    public void Evaluate_DateRange_ExcludesTooOld()
    {
        var options = Options(createdAfter: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var old = File(created: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var recent = File(created: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(PathEligibilityResult.ExcludedByDate, new PathEligibilityEvaluator(options).Evaluate(old));
        Assert.Equal(PathEligibilityResult.Eligible, new PathEligibilityEvaluator(options).Evaluate(recent));
    }

    [Fact]
    public void Evaluate_DateFilterActive_ButUnknownDate_Excluded()
    {
        var options = Options(createdAfter: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(PathEligibilityResult.ExcludedByDate,
            new PathEligibilityEvaluator(options).Evaluate(File(created: null)));
    }

    [Fact]
    public void Evaluate_DateRange_ExcludesTooNew_ViaBeforeBound()
    {
        var options = Options(createdBefore: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tooNew = File(created: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var old = File(created: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(PathEligibilityResult.ExcludedByDate, new PathEligibilityEvaluator(options).Evaluate(tooNew));
        Assert.Equal(PathEligibilityResult.Eligible, new PathEligibilityEvaluator(options).Evaluate(old));
    }

    [Fact]
    public void Constructor_NullCollections_DefaultToEmpty_AndEvaluates()
    {
        var options = new SearchOptions
        {
            Directory = @"C:\src",
            Query = "x",
            IncludeGlobs = null!,
            ExcludeGlobs = null!,
            SkipExtensions = null!,
        };
        var eval = new PathEligibilityEvaluator(options);
        Assert.True(eval.CanEvaluate);
        Assert.Equal(PathEligibilityResult.Eligible, eval.Evaluate(File()));
    }

    [Fact]
    public void Evaluate_DriveRootDirectory_MatchesFilesUnderIt()
    {
        // A bare drive root already ends in '\\', exercising the prefix branch that does not re-append it.
        Assert.Equal(PathEligibilityResult.Eligible,
            new PathEligibilityEvaluator(Options(directory: @"C:\")).Evaluate(File(path: @"C:\anything\x.cs")));
    }

    [Fact]
    public void Constructor_ExcludeGlobsOnly_SetsHasGlobsViaSecondOperand()
    {
        // IncludeGlobs empty + ExcludeGlobs non-empty exercises the second operand of the _hasGlobs check.
        var eval = new PathEligibilityEvaluator(Options(excludeGlobs: new[] { "node_modules" }));
        Assert.True(eval.CanEvaluate);
    }

    [Fact]
    public void Constructor_IncludeGlobs_SetsHasGlobsViaFirstOperand()
    {
        // IncludeGlobs non-empty makes the first operand true (short-circuits the _hasGlobs OR).
        var eval = new PathEligibilityEvaluator(Options(includeGlobs: new[] { "*.cs" }));
        Assert.True(eval.CanEvaluate);
    }

    [Fact]
    public void Evaluate_ModifiedDateFilter_ExcludesOutOfRange()
    {
        var options = new SearchOptions
        {
            Directory = @"C:\src",
            Query = "x",
            IncludeGlobs = Array.Empty<string>(),
            ExcludeGlobs = Array.Empty<string>(),
            SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SearchHiddenFiles = true,
            ModifiedAfterDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var eval = new PathEligibilityEvaluator(options);
        Assert.Equal(PathEligibilityResult.ExcludedByDate,
            eval.Evaluate(File(modified: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))));
        Assert.Equal(PathEligibilityResult.Eligible,
            eval.Evaluate(File(modified: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Evaluate_ExcludedByExtension()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png" };
        Assert.Equal(PathEligibilityResult.ExcludedByExtension,
            new PathEligibilityEvaluator(Options(skip: skip)).Evaluate(File(path: @"C:\src\img.PNG")));
    }

    [Fact]
    public void Evaluate_ExcludedByGlob()
    {
        var evaluator = new PathEligibilityEvaluator(Options(excludeGlobs: new[] { "node_modules" }));
        Assert.Equal(PathEligibilityResult.ExcludedByGlob,
            evaluator.Evaluate(File(path: @"C:\src\node_modules\x.js")));
        Assert.Equal(PathEligibilityResult.Eligible,
            evaluator.Evaluate(File(path: @"C:\src\app\x.js")));
    }
}
