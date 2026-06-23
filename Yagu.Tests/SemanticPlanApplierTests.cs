using System;
using System.Collections.Generic;
using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="SemanticPlanApplier"/> — the pure, side-effect-free mapping from a
/// model-produced <see cref="SemanticSearchPlan"/> onto Yagu's concrete search inputs. Covers the
/// match-all synthesis for filter-only queries, drive-shorthand resolution, relative-date parsing,
/// name-exclude→glob expansion, clamping, and application onto a writable target.
/// </summary>
public sealed class SemanticPlanApplierTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static SemanticTranslationContext Context(string? defaultDir = null) =>
        new() { Now = Now, DefaultDirectory = defaultDir };

    /// <summary>Minimal writable surface so we can assert what gets applied.</summary>
    private sealed class FakeTarget : ISemanticPlanTarget
    {
        public string Directory { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public int SearchModeIndex { get; set; }
        public bool CaseSensitive { get; set; }
        public bool UseRegex { get; set; }
        public bool ExactMatch { get; set; } = true;
        public string IncludeGlobs { get; set; } = string.Empty;
        public string ExcludeGlobs { get; set; } = string.Empty;
        public int IncludeFilterModeIndex { get; set; }
        public int ExcludeFilterModeIndex { get; set; }
        public long MinFileSizeBytes { get; set; }
        public long MaxFileSizeBytes { get; set; }
        public DateTimeOffset? CreatedAfterDate { get; set; }
        public DateTimeOffset? CreatedBeforeDate { get; set; }
        public DateTimeOffset? ModifiedAfterDate { get; set; }
        public DateTimeOffset? ModifiedBeforeDate { get; set; }
        public double MaxSearchDepth { get; set; }
        public bool ObeyGitignore { get; set; }
        public bool SearchInsideArchives { get; set; }
        public int SortModeIndex { get; set; }
        public int SortDirectionIndex { get; set; }
        public int GroupModeIndex { get; set; }
        public int GroupSortDirectionIndex { get; set; }
    }

    // ---- match-all synthesis ------------------------------------------------

    [Fact]
    public void Resolve_EmptyPatternWithGlobFilter_SynthesizesMatchAllFilenameRegex()
    {
        var plan = new SemanticSearchPlan { Pattern = "", IncludeGlobs = new() { "*.png" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(".", resolved.Pattern);
        Assert.True(resolved.UseRegex);
        Assert.Equal(SearchMode.FileNames, resolved.SearchMode);
    }

    [Fact]
    public void Resolve_EmptyPatternWithDateFilter_SynthesizesMatchAll()
    {
        var plan = new SemanticSearchPlan { Pattern = null, ModifiedAfter = "1 year ago" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(".", resolved.Pattern);
        Assert.True(resolved.UseRegex);
        Assert.Equal(SearchMode.FileNames, resolved.SearchMode);
    }

    [Fact]
    public void Resolve_EmptyPatternNoFilters_DoesNotSynthesize()
    {
        var plan = new SemanticSearchPlan { Pattern = "  " };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.Pattern);
    }

    [Fact]
    public void Resolve_GlobPatternDuplicatingInclude_DroppedAndSynthesizesMatchAll()
    {
        // Model mistakenly echoes the file-type filter into pattern ("*.png" with include *.png).
        var plan = new SemanticSearchPlan { Pattern = "*.png", SearchMode = "filenames", IncludeGlobs = new() { "*.png" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(".", resolved.Pattern);
        Assert.True(resolved.UseRegex);
        Assert.Equal(SearchMode.FileNames, resolved.SearchMode);
        Assert.Equal(new[] { "*.png" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_RealPatternWithMatchingExtensionTerm_IsPreserved()
    {
        // A genuine search term that merely looks word-like must never be dropped.
        var plan = new SemanticSearchPlan { Pattern = "report", IncludeGlobs = new() { "*.png" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal("report", resolved.Pattern);
    }

    [Fact]
    public void Resolve_NonEmptyPattern_IsPreservedAndNotForcedToRegex()
    {
        var plan = new SemanticSearchPlan { Pattern = "todo", IncludeGlobs = new() { "*.cs" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal("todo", resolved.Pattern);
        Assert.Null(resolved.UseRegex); // not overridden by synthesis
    }

    [Theory]
    [InlineData("*. json", "*.json")]   // small models inject a space after the dot
    [InlineData("* .log", "*.log")]      // ...or before the extension dot
    [InlineData("  *.jsonl  ", "*.jsonl")]
    [InlineData("\"*.csv\"", "*.csv")]
    public void Resolve_MalformedGlobToken_IsSanitized(string raw, string expected)
    {
        var plan = new SemanticSearchPlan { Pattern = "test", IncludeGlobs = new() { raw } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new[] { expected }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_GlobWithInternalSpaceNotNearDot_IsPreserved()
    {
        // A legitimate name fragment with an internal space (not adjacent to a dot/wildcard)
        // must survive sanitization untouched.
        var plan = new SemanticSearchPlan { Pattern = "", IncludeGlobs = new() { "my report.txt" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new[] { "my report.txt" }, resolved.IncludeGlobs);
    }

    // ---- directory resolution ----------------------------------------------

    [Theory]
    [InlineData("C drive", "C:\\")]
    [InlineData("the c drive", "C:\\")]
    [InlineData("C:", "C:\\")]
    [InlineData("d", "D:\\")]
    public void Resolve_DriveShorthand_RootsToDrivePath(string raw, string expected)
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Directory = raw }, Context());
        Assert.Equal(expected, resolved.Directory);
    }

    [Fact]
    public void Resolve_NoDirectory_FallsBackToContextDefault()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan(), Context(@"D:\projects"));
        Assert.Equal(@"D:\projects", resolved.Directory);
    }

    [Fact]
    public void Resolve_FullPath_PassesThroughTrimmed()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Directory = "  \"E:\\data\"  " }, Context());
        Assert.Equal(@"E:\data", resolved.Directory);
    }

    // ---- exclude file names -> globs ---------------------------------------

    [Fact]
    public void Resolve_ExcludeBareFileName_ExpandsToNameAndExtensionGlobs()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ExcludeFileNames = new() { "abc" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.ExcludeGlobs);
        Assert.Contains("abc", resolved.ExcludeGlobs!);
        Assert.Contains("abc.*", resolved.ExcludeGlobs!);
    }

    [Fact]
    public void Resolve_ExcludeGlobsAndNames_AreMerged()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            ExcludeGlobs = new() { "*.mov" },
            ExcludeFileNames = new() { "abc" },
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Contains("*.mov", resolved.ExcludeGlobs!);
        Assert.Contains("abc.*", resolved.ExcludeGlobs!);
    }

    // ---- relative dates -----------------------------------------------------

    [Fact]
    public void Resolve_ModifiedAfterPastYear_ResolvesRelativeToNow()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "in the past year" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(Now.AddYears(-1), resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_ModifiedAfterNDaysAgo_ShiftsByDays()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "30 days ago" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(Now.AddDays(-30), resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_UnparseableDate_IsIgnoredAndWarned()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "whenever" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.ModifiedAfterDate);
        Assert.NotEmpty(resolved.Warnings);
    }

    [Fact]
    public void Resolve_IdenticalModifiedBounds_AreDroppedAsPadding()
    {
        // phi-3.5-mini's padding mode dumps the same date into every field, producing a contradictory
        // "modified between X and X" window. Both modified bounds must be dropped, leaving an unrelated
        // createdBefore intact.
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            CreatedBefore = "2024-06-21",
            ModifiedAfter = "2024-06-21",
            ModifiedBefore = "2024-06-21",
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.ModifiedAfterDate);
        Assert.Null(resolved.ModifiedBeforeDate);
        Assert.NotNull(resolved.CreatedBeforeDate);
        Assert.Contains(resolved.Warnings, w => w.Contains("modified-date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_IdenticalCreatedBounds_AreDroppedAsPadding()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", CreatedAfter = "2024-01-01", CreatedBefore = "2024-01-01" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.CreatedAfterDate);
        Assert.Null(resolved.CreatedBeforeDate);
    }

    [Fact]
    public void Resolve_DistinctDateBounds_AreKept()
    {
        // A genuine range with different bounds must NOT be treated as padding.
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "2024-01-01", ModifiedBefore = "2024-12-31" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.ModifiedAfterDate);
        Assert.NotNull(resolved.ModifiedBeforeDate);
    }

    [Fact]
    public void Resolve_DateCopiedAcrossCreatedAndModified_DropsModifiedWhenExplanationSaysCreated()
    {
        // phi padding: "created before 2024" -> createdBefore AND modifiedBefore both "2024-01-01".
        // Explanation refers to creation, so the duplicated modified bound is dropped.
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            CreatedBefore = "2024-01-01",
            ModifiedBefore = "2024-01-01",
            Explanation = "Listing images created before 2024.",
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.CreatedBeforeDate);
        Assert.Null(resolved.ModifiedBeforeDate);
    }

    [Fact]
    public void Resolve_DateCopiedAcrossCreatedAndModified_KeepsModifiedWhenExplanationSaysModified()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            CreatedBefore = "2024-01-01",
            ModifiedBefore = "2024-01-01",
            Explanation = "Listing files modified before 2024.",
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.ModifiedBeforeDate);
        Assert.Null(resolved.CreatedBeforeDate);
    }

    // ---- clamps + mode parse ------------------------------------------------

    [Fact]
    public void Resolve_NegativeSize_TreatedAsUnset()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", MinFileSizeBytes = -5 };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.MinFileSizeBytes);
    }

    [Fact]
    public void Resolve_ZeroSizePlaceholders_TreatedAsUnset()
    {
        // Weak models pad the JSON with min=0 / max=0 placeholders; a 0-byte bound is never a
        // meaningful filter (max=0 would match nothing), so both must be dropped.
        var plan = new SemanticSearchPlan { Pattern = "x", MinFileSizeBytes = 0, MaxFileSizeBytes = 0 };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.MinFileSizeBytes);
        Assert.Null(resolved.MaxFileSizeBytes);
    }

    [Theory]
    [InlineData("filenames", SearchMode.FileNames)]
    [InlineData("content", SearchMode.Content)]
    [InlineData("both", SearchMode.Both)]
    public void Resolve_SearchMode_ParsesKnownSynonyms(string raw, SearchMode expected)
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SearchMode = raw }, Context());
        Assert.Equal(expected, resolved.SearchMode);
    }

    [Fact]
    public void Resolve_UnknownSearchMode_WarnsAndLeavesNull()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SearchMode = "telepathy" }, Context());
        Assert.Null(resolved.SearchMode);
        Assert.NotEmpty(resolved.Warnings);
    }

    // ---- ApplyToTarget ------------------------------------------------------

    [Fact]
    public void ApplyToTarget_OnlyOverridesFieldsTheModelSet()
    {
        var target = new FakeTarget { Directory = "keep-me", ExactMatch = true };
        var plan = new SemanticSearchPlan
        {
            Pattern = "log",
            IncludeGlobs = new() { "*.txt" },
            ExcludeGlobs = new() { "*.bin" },
        };

        SemanticPlanApplier.ApplyToTarget(plan, Context("keep-me"), target);

        Assert.Equal("keep-me", target.Directory); // default dir applied from context (unchanged value)
        Assert.Equal("log", target.Query);
        Assert.Equal("*.txt", target.IncludeGlobs);
        Assert.Equal((int)FilterPatternMode.GlobPath, target.IncludeFilterModeIndex);
        Assert.Equal("*.bin", target.ExcludeGlobs);
        Assert.Equal((int)FilterPatternMode.GlobPath, target.ExcludeFilterModeIndex);
        Assert.True(target.ExactMatch); // not set by plan -> untouched
    }

    [Fact]
    public void ApplyToTarget_MatchAllSynthesis_SetsFilenameModeAndRegex()
    {
        var target = new FakeTarget();
        var plan = new SemanticSearchPlan { Pattern = "", IncludeGlobs = new() { "*.png" }, ModifiedAfter = "1 year ago" };

        SemanticPlanApplier.ApplyToTarget(plan, Context(@"C:\"), target);

        Assert.Equal(".", target.Query);
        Assert.True(target.UseRegex);
        Assert.Equal((int)SearchMode.FileNames, target.SearchModeIndex);
        Assert.Equal(Now.AddYears(-1), target.ModifiedAfterDate);
    }

    [Fact]
    public void ApplyToTarget_ZeroDepth_MapsToUnlimitedNaN()
    {
        var target = new FakeTarget { MaxSearchDepth = 5 };
        var plan = new SemanticSearchPlan { Pattern = "x", MaxSearchDepth = 0 };

        SemanticPlanApplier.ApplyToTarget(plan, Context(), target);

        Assert.True(double.IsNaN(target.MaxSearchDepth));
    }

    // ---- ToOverlay (CLI surface) -------------------------------------------

    [Fact]
    public void ToOverlay_CarriesResolvedValues()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", IncludeGlobs = new() { "*.cs" }, MaxFileSizeBytes = 1024 };

        var overlay = SemanticPlanApplier.ToOverlay(SemanticPlanApplier.Resolve(plan, Context(@"C:\")));

        Assert.Equal("x", overlay.Query);
        Assert.Equal(@"C:\", overlay.Directory);
        Assert.Equal(1024, overlay.MaxFileSizeBytes);
        Assert.NotNull(overlay.IncludeGlobs);
    }

    // ---- sorting & grouping ------------------------------------------------

    [Theory]
    [InlineData("name", 4)]
    [InlineData("filename", 4)]
    [InlineData("size", 3)]
    [InlineData("date", 2)]
    [InlineData("modified", 2)]
    [InlineData("relevance", 1)]
    [InlineData("matches", 1)]
    [InlineData("directory", 5)]
    [InlineData("folder", 5)]
    public void Resolve_SortBy_MapsToSortModeIndex(string raw, int expected)
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SortBy = raw }, Context());
        Assert.Equal(expected, resolved.SortModeIndex);
    }

    [Fact]
    public void Resolve_SortByWithoutDirection_DefaultsToDescending()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SortBy = "name" }, Context());
        Assert.Equal(4, resolved.SortModeIndex);
        Assert.Equal(0, resolved.SortDirectionIndex); // 0 = descending
    }

    [Theory]
    [InlineData("asc", 1)]
    [InlineData("ascending", 1)]
    [InlineData("a to z", 1)]
    [InlineData("desc", 0)]
    [InlineData("descending", 0)]
    [InlineData("z-a", 0)]
    public void Resolve_SortDirection_Parses(string raw, int expected)
    {
        var resolved = SemanticPlanApplier.Resolve(
            new SemanticSearchPlan { Pattern = "x", SortBy = "name", SortDirection = raw }, Context());
        Assert.Equal(expected, resolved.SortDirectionIndex);
    }

    [Fact]
    public void Resolve_UnknownSortBy_WarnsAndLeavesNull()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SortBy = "telepathy" }, Context());
        Assert.Null(resolved.SortModeIndex);
        Assert.Null(resolved.SortDirectionIndex);
        Assert.Contains(resolved.Warnings, w => w.Contains("sortBy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_NoSort_LeavesIndicesNull()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x" }, Context());
        Assert.Null(resolved.SortModeIndex);
        Assert.Null(resolved.SortDirectionIndex);
    }

    [Theory]
    [InlineData("directory", GroupMode.Folder)]
    [InlineData("folder", GroupMode.Folder)]
    [InlineData("extension", GroupMode.Extension)]
    [InlineData("type", GroupMode.Extension)]
    [InlineData("size", GroupMode.FileSize)]
    [InlineData("modified", GroupMode.DateRangeModified)]
    [InlineData("created", GroupMode.DateRangeCreated)]
    [InlineData("date", GroupMode.DateRangeModifiedCreated)]
    public void Resolve_GroupBy_MapsToGroupMode(string raw, GroupMode expected)
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", GroupBy = raw }, Context());
        Assert.Equal(expected, resolved.GroupMode);
    }

    [Fact]
    public void Resolve_GroupByNone_MapsToNoneWithNaturalDirection()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", GroupBy = "none" }, Context());
        Assert.Equal(GroupMode.None, resolved.GroupMode);
        Assert.Equal(0, resolved.GroupSortDirectionIndex);
    }

    [Theory]
    [InlineData("a-z", 0)]
    [InlineData("recent first", 0)]
    [InlineData("z-a", 1)]
    [InlineData("oldest first", 1)]
    [InlineData("reversed", 1)]
    public void Resolve_GroupDirection_Parses(string raw, int expected)
    {
        var resolved = SemanticPlanApplier.Resolve(
            new SemanticSearchPlan { Pattern = "x", GroupBy = "directory", GroupDirection = raw }, Context());
        Assert.Equal(expected, resolved.GroupSortDirectionIndex);
    }

    [Fact]
    public void Resolve_UnknownGroupBy_WarnsAndLeavesNull()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", GroupBy = "rainbow" }, Context());
        Assert.Null(resolved.GroupMode);
        Assert.Contains(resolved.Warnings, w => w.Contains("groupBy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToOverlay_MapsSortAndGroupToCliKeys()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            SortBy = "name",
            SortDirection = "asc",
            GroupBy = "directory",
            GroupDirection = "z-a",
        };

        var overlay = SemanticPlanApplier.ToOverlay(SemanticPlanApplier.Resolve(plan, Context()));

        Assert.Equal("name", overlay.SortBy);
        Assert.False(overlay.SortDescending); // ascending
        Assert.Equal("directory", overlay.GroupBy);
        Assert.True(overlay.GroupDescending); // reversed
    }

    [Fact]
    public void ToOverlay_SortFieldWithoutDirection_IsDescending()
    {
        var overlay = SemanticPlanApplier.ToOverlay(
            SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SortBy = "size" }, Context()));

        Assert.Equal("size", overlay.SortBy);
        Assert.True(overlay.SortDescending);
    }

    [Fact]
    public void ToOverlay_NoSortOrGroup_LeavesKeysNull()
    {
        var overlay = SemanticPlanApplier.ToOverlay(
            SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x" }, Context()));

        Assert.Null(overlay.SortBy);
        Assert.Null(overlay.SortDescending);
        Assert.Null(overlay.GroupBy);
        Assert.Null(overlay.GroupDescending);
    }

    // ---- null context + cross-family date padding --------------------------

    [Fact]
    public void Resolve_NullContext_UsesDefaultsWithoutThrowing()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x" }, null!);

        Assert.Equal("x", resolved.Pattern);
        Assert.Null(resolved.Directory);
    }

    [Fact]
    public void Resolve_DateCopiedAcrossAfterBounds_DropsModifiedWhenExplanationSaysCreated()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            CreatedAfter = "2024-01-01",
            ModifiedAfter = "2024-01-01",
            Explanation = "Listing images created after 2024.",
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.CreatedAfterDate);
        Assert.Null(resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_DateCopiedAcrossAfterBounds_KeepsModifiedWhenExplanationSaysModified()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            CreatedAfter = "2024-01-01",
            ModifiedAfter = "2024-01-01",
            Explanation = "Listing files modified after 2024.",
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.ModifiedAfterDate);
        Assert.Null(resolved.CreatedAfterDate);
    }

    [Fact]
    public void Resolve_ExplanationMentioningBothCreatedAndModified_KeepsCreatedBound()
    {
        // When the explanation references both, "prefers modified" is false (it requires modified
        // WITHOUT created), so the created bound is the one retained.
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            CreatedBefore = "2024-01-01",
            ModifiedBefore = "2024-01-01",
            Explanation = "Files modified and created before 2024.",
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.CreatedBeforeDate);
        Assert.Null(resolved.ModifiedBeforeDate);
    }

    // ---- directory drive-colon via context default -------------------------

    [Fact]
    public void Resolve_ContextDefaultDriveColon_RootsToDrivePath()
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x" }, Context("d:"));
        Assert.Equal("D:\\", resolved.Directory);
    }

    // ---- search-mode synonyms (full table) ---------------------------------

    [Theory]
    [InlineData("content-and-filenames", SearchMode.Both)]
    [InlineData("all", SearchMode.Both)]
    [InlineData("content and filenames", SearchMode.Both)]
    [InlineData("contents", SearchMode.Content)]
    [InlineData("text", SearchMode.Content)]
    [InlineData("inside", SearchMode.Content)]
    [InlineData("filename", SearchMode.FileNames)]
    [InlineData("names", SearchMode.FileNames)]
    [InlineData("name", SearchMode.FileNames)]
    [InlineData("files", SearchMode.FileNames)]
    [InlineData("filename-then-content", SearchMode.FileNameThenContent)]
    [InlineData("filenamethencontent", SearchMode.FileNameThenContent)]
    [InlineData("name-then-content", SearchMode.FileNameThenContent)]
    public void Resolve_SearchMode_ParsesEverySynonym(string raw, SearchMode expected)
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { Pattern = "x", SearchMode = raw }, Context());
        Assert.Equal(expected, resolved.SearchMode);
    }

    [Fact]
    public void Resolve_FileNamesModeWithoutPatternOrFilters_SynthesizesMatchAll()
    {
        // mode == FileNames alone (no include/exclude/date/size filters) still triggers the match-all
        // synthesis branch.
        var plan = new SemanticSearchPlan { Pattern = null, SearchMode = "filenames" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(".", resolved.Pattern);
        Assert.True(resolved.UseRegex);
        Assert.Equal(SearchMode.FileNames, resolved.SearchMode);
    }

    // ---- glob/name sanitization edge cases ---------------------------------

    [Fact]
    public void Resolve_LoneQuoteGlobToken_IsDroppedAsEmpty()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", IncludeGlobs = new() { "\"", "*.png" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new[] { "*.png" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_ExcludeFileNameThatLooksLikeGlob_IsPassedThroughNotExpanded()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ExcludeFileNames = new() { "abc.txt" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Contains("abc.txt", resolved.ExcludeGlobs!);
        Assert.DoesNotContain("abc.txt.*", resolved.ExcludeGlobs!);
    }

    [Fact]
    public void Resolve_DuplicateExcludeGlobAndName_IsDeduplicated()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "x",
            ExcludeGlobs = new() { "abc.txt" },
            ExcludeFileNames = new() { "abc.txt" },
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(1, System.Linq.Enumerable.Count(resolved.ExcludeGlobs!, g => g == "abc.txt"));
    }

    // ---- date phrases: today / yesterday / timestamps / last-N -------------

    [Fact]
    public void Resolve_Today_ResolvesToStartOfCurrentDay()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "today" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero), resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_Yesterday_ResolvesToStartOfPreviousDay()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", CreatedAfter = "yesterday" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new DateTimeOffset(2025, 6, 14, 0, 0, 0, TimeSpan.Zero), resolved.CreatedAfterDate);
    }

    [Fact]
    public void Resolve_FullTimestamp_KeepsTimeComponent()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "2024-06-15T10:30:00" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.NotNull(resolved.ModifiedAfterDate);
        Assert.Equal(10, resolved.ModifiedAfterDate!.Value.Hour);
        Assert.Equal(30, resolved.ModifiedAfterDate.Value.Minute);
    }

    [Fact]
    public void Resolve_DateShapedButInvalid_IsIgnoredAndWarned()
    {
        // Matches the date-only regex but is not a real calendar date, so both parse attempts fail.
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "2024-13-45" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Null(resolved.ModifiedAfterDate);
        Assert.NotEmpty(resolved.Warnings);
    }

    [Fact]
    public void Resolve_LastWeek_NoNumber_ShiftsBackOneWeek()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "last week" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(Now.AddDays(-7), resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_LastNWeeks_ShiftsBackByWeeks()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "last 3 weeks" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(Now.AddDays(-21), resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_MonthsAgo_ShiftsBackByMonths()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "2 months ago" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(Now.AddMonths(-2), resolved.ModifiedAfterDate);
    }

    [Fact]
    public void Resolve_YearsAgo_ShiftsBackByYears()
    {
        var plan = new SemanticSearchPlan { Pattern = "x", ModifiedAfter = "3 years ago" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(Now.AddYears(-3), resolved.ModifiedAfterDate);
    }

    // ---- ApplyToTarget: null-skip + positive depth -------------------------

    [Fact]
    public void ApplyToTarget_AllNullResolvedPlan_LeavesTargetUntouched()
    {
        var modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var target = new FakeTarget
        {
            Directory = "keep",
            Query = "keep-q",
            SearchModeIndex = 2,
            CaseSensitive = true,
            UseRegex = true,
            ExactMatch = true,
            IncludeGlobs = "inc",
            ExcludeGlobs = "exc",
            IncludeFilterModeIndex = 1,
            ExcludeFilterModeIndex = 1,
            MinFileSizeBytes = 10,
            MaxFileSizeBytes = 20,
            CreatedAfterDate = modified,
            CreatedBeforeDate = modified,
            ModifiedAfterDate = modified,
            ModifiedBeforeDate = modified,
            MaxSearchDepth = 4,
            ObeyGitignore = true,
            SearchInsideArchives = true,
        };

        SemanticPlanApplier.ApplyToTarget(new ResolvedSearchPlan(), target);

        Assert.Equal("keep", target.Directory);
        Assert.Equal("keep-q", target.Query);
        Assert.Equal(2, target.SearchModeIndex);
        Assert.True(target.CaseSensitive);
        Assert.True(target.UseRegex);
        Assert.True(target.ExactMatch);
        Assert.Equal("inc", target.IncludeGlobs);
        Assert.Equal("exc", target.ExcludeGlobs);
        Assert.Equal(1, target.IncludeFilterModeIndex);
        Assert.Equal(1, target.ExcludeFilterModeIndex);
        Assert.Equal(10, target.MinFileSizeBytes);
        Assert.Equal(20, target.MaxFileSizeBytes);
        Assert.Equal(modified, target.CreatedAfterDate);
        Assert.Equal(modified, target.CreatedBeforeDate);
        Assert.Equal(modified, target.ModifiedAfterDate);
        Assert.Equal(modified, target.ModifiedBeforeDate);
        Assert.Equal(4, target.MaxSearchDepth);
        Assert.True(target.ObeyGitignore);
        Assert.True(target.SearchInsideArchives);
    }

    [Fact]
    public void ApplyToTarget_PositiveDepth_MapsToConcreteDepth()
    {
        var target = new FakeTarget();
        var plan = new SemanticSearchPlan { Pattern = "x", MaxSearchDepth = 3 };

        SemanticPlanApplier.ApplyToTarget(plan, Context(), target);

        Assert.Equal(3.0, target.MaxSearchDepth);
    }

    [Fact]
    public void ApplyToTarget_FullyPopulatedResolvedPlan_WritesEveryField()
    {
        var after = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var resolved = new ResolvedSearchPlan
        {
            Directory = "E:\\data",
            Pattern = "term",
            SearchMode = SearchMode.Both,
            CaseSensitive = true,
            UseRegex = true,
            ExactMatch = false,
            IncludeGlobs = new[] { "*.cs" },
            ExcludeGlobs = new[] { "*.bin" },
            MinFileSizeBytes = 100,
            MaxFileSizeBytes = 200,
            CreatedAfterDate = after,
            CreatedBeforeDate = before,
            ModifiedAfterDate = after,
            ModifiedBeforeDate = before,
            MaxSearchDepth = 5,
            ObeyGitignore = true,
            SearchInsideArchives = true,
            SortModeIndex = 4,
            SortDirectionIndex = 1,
            GroupMode = GroupMode.Folder,
            GroupSortDirectionIndex = 1,
        };
        var target = new FakeTarget { ExactMatch = true };

        SemanticPlanApplier.ApplyToTarget(resolved, target);

        Assert.Equal("E:\\data", target.Directory);
        Assert.Equal("term", target.Query);
        Assert.Equal((int)SearchMode.Both, target.SearchModeIndex);
        Assert.True(target.CaseSensitive);
        Assert.True(target.UseRegex);
        Assert.False(target.ExactMatch);
        Assert.Equal("*.cs", target.IncludeGlobs);
        Assert.Equal("*.bin", target.ExcludeGlobs);
        Assert.Equal(100, target.MinFileSizeBytes);
        Assert.Equal(200, target.MaxFileSizeBytes);
        Assert.Equal(after, target.CreatedAfterDate);
        Assert.Equal(before, target.CreatedBeforeDate);
        Assert.Equal(after, target.ModifiedAfterDate);
        Assert.Equal(before, target.ModifiedBeforeDate);
        Assert.Equal(5.0, target.MaxSearchDepth);
        Assert.True(target.ObeyGitignore);
        Assert.True(target.SearchInsideArchives);
        Assert.Equal(4, target.SortModeIndex);
        Assert.Equal(1, target.SortDirectionIndex);
        Assert.Equal((int)GroupMode.Folder, target.GroupModeIndex);
        Assert.Equal(1, target.GroupSortDirectionIndex);
    }

    // ---- explanation time-reference helpers --------------------------------

    [Theory]
    [InlineData("created last week", true)]
    [InlineData("CREATION date filter", true)]
    [InlineData("modified yesterday", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MentionsCreated_DetectsCreationReferences(string? explanation, bool expected)
    {
        Assert.Equal(expected, SemanticPlanApplier.MentionsCreated(explanation));
    }

    [Theory]
    [InlineData("modified last week", true)]
    [InlineData("recently changed", true)]
    [InlineData("files updated today", true)]
    [InlineData("edited docs", true)]
    [InlineData("created only", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MentionsModified_DetectsModificationReferences(string? explanation, bool expected)
    {
        Assert.Equal(expected, SemanticPlanApplier.MentionsModified(explanation));
    }

    // ---- archive-container detection (Office/OpenDocument are ZIPs) ---------

    private static readonly HashSet<string> KnownArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "zip", "7z", "docx", "xlsx", "pptx", "odt", "nupkg" };

    [Fact]
    public void GetArchiveExtensionsToEnable_WordDocGlobs_ReturnsOnlyContainerExtensions()
    {
        // .docx is a ZIP container (needs archive search to read its text); .doc is a binary, not one.
        var result = SemanticPlanApplier.GetArchiveExtensionsToEnable(
            new[] { "*.docx", "*.doc" }, KnownArchiveExtensions);

        Assert.Equal(new[] { "docx" }, result);
    }

    [Fact]
    public void GetArchiveExtensionsToEnable_MixedGlobs_DeduplicatesAndIgnoresNonArchives()
    {
        var result = SemanticPlanApplier.GetArchiveExtensionsToEnable(
            new[] { "**/*.ZIP", "*.txt", "*.zip", "*.png" }, KnownArchiveExtensions);

        Assert.Equal(new[] { "zip" }, result);
    }

    [Fact]
    public void GetArchiveExtensionsToEnable_NoArchiveGlobs_ReturnsEmpty()
    {
        Assert.Empty(SemanticPlanApplier.GetArchiveExtensionsToEnable(
            new[] { "*.txt", "*.png" }, KnownArchiveExtensions));
    }

    [Fact]
    public void GetArchiveExtensionsToEnable_NullOrEmptyInputs_ReturnEmpty()
    {
        Assert.Empty(SemanticPlanApplier.GetArchiveExtensionsToEnable(null, KnownArchiveExtensions));
        Assert.Empty(SemanticPlanApplier.GetArchiveExtensionsToEnable(Array.Empty<string>(), KnownArchiveExtensions));
        Assert.Empty(SemanticPlanApplier.GetArchiveExtensionsToEnable(
            new[] { "*.docx" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("*.docx", "docx")]
    [InlineData("**/*.ZIP", "zip")]
    [InlineData(".odt", "odt")]
    [InlineData("nupkg", "nupkg")]
    [InlineData("C:\\dir\\*.7z", "7z")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExtractGlobExtension_NormalizesToBareLowerCaseExtension(string? glob, string expected)
    {
        Assert.Equal(expected, SemanticPlanApplier.ExtractGlobExtension(glob));
    }
}
