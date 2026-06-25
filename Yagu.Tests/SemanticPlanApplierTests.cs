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
        public bool SearchHiddenFiles { get; set; } = true;
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

    // ---- extension de-pluralization ----------------------------------------

    private static readonly HashSet<string> DepluralizeKnown =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // singular extensions a model might wrongly pluralize...
            "exe", "png", "pdf", "doc", "jpeg", "txt", "log",
            // ...and real s-ending extensions that must be left alone.
            "cs", "css", "js", "ts", "xls", "class", "props", "hs", "as", "a", "h",
        };

    [Theory]
    [InlineData("*.exes", "*.exe")]
    [InlineData("*.pngs", "*.png")]
    [InlineData("*.pdfs", "*.pdf")]
    [InlineData("*.docs", "*.doc")]
    [InlineData("*.jpegs", "*.jpeg")]
    [InlineData("*.txts", "*.txt")]
    [InlineData("**/*.exes", "**/*.exe")]
    [InlineData("C:\\dir\\*.pngs", "C:\\dir\\*.png")]
    [InlineData(".exes", ".exe")]
    [InlineData("*.EXES", "*.EXE")]            // casing of kept characters is preserved
    public void DepluralizeGlobExtension_RepairsPluralizedExtensions(string glob, string expected)
    {
        Assert.Equal(expected, SemanticPlanApplier.DepluralizeGlobExtension(glob, DepluralizeKnown));
    }

    [Theory]
    [InlineData("*.cs")]      // singular "c" is known but "cs" is itself a real extension
    [InlineData("*.css")]     // "css" is real; "cs" is real -> guarded by known-plural check
    [InlineData("*.js")]
    [InlineData("*.ts")]
    [InlineData("*.xls")]
    [InlineData("*.class")]
    [InlineData("*.props")]
    [InlineData("*.hs")]      // singular "h" known, but "hs" would de-pluralize to 1 char -> skipped
    [InlineData("*.as")]      // singular "a" known, but 1-char target -> skipped
    [InlineData("docs")]      // no extension dot -> folder-name token left intact
    [InlineData("builds")]
    [InlineData("*.unknowns")] // neither plural nor singular is known -> not a pluralization
    [InlineData("*.tar.gz")]  // compound: last ext "gz" doesn't end in 's'
    public void DepluralizeGlobExtension_LeavesLegitimateTokensUnchanged(string glob)
    {
        Assert.Equal(glob, SemanticPlanApplier.DepluralizeGlobExtension(glob, DepluralizeKnown));
    }

    [Fact]
    public void DepluralizeGlobExtension_NullOrEmptyKnownSet_ReturnsInput()
    {
        Assert.Equal("*.exes", SemanticPlanApplier.DepluralizeGlobExtension("*.exes", null!));
        Assert.Equal("*.exes", SemanticPlanApplier.DepluralizeGlobExtension(
            "*.exes", new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void KnownFileExtensions_Default_ContainsCommonExtensions()
    {
        var known = KnownFileExtensions.Default;
        // Curated + Yagu defaults guarantee these regardless of machine registry state.
        Assert.Contains("exe", known);
        Assert.Contains("png", known);
        Assert.Contains("pdf", known);
        Assert.Contains("cs", known);
        Assert.Contains("css", known);
        Assert.Contains("js", known);
        Assert.Contains("xls", known);
        // Pluralized forms must NOT be present so the de-pluralizer fires.
        Assert.DoesNotContain("exes", known);
        Assert.DoesNotContain("pngs", known);
    }

    [Fact]
    public void Resolve_PluralizedExtensionGlob_IsDepluralized()
    {
        // The reported regression: "exe files" -> model emits "*.exes".
        var plan = new SemanticSearchPlan { Pattern = "yagu", IncludeGlobs = new() { "*.exes" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new[] { "*.exe" }, resolved.IncludeGlobs);
    }

    [Theory]
    [InlineData("*.cs")]
    [InlineData("*.css")]
    [InlineData("*.js")]
    [InlineData("*.ts")]
    [InlineData("*.xls")]
    public void Resolve_LegitimateSEndingExtensionGlob_IsPreserved(string glob)
    {
        var plan = new SemanticSearchPlan { Pattern = "x", IncludeGlobs = new() { glob } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new[] { glob }, resolved.IncludeGlobs);
    }

    // ---- language-name -> extension correction -----------------------------

    [Theory]
    [InlineData("c# files on C:/", "cs", "c")]
    [InlineData("c sharp files", "cs", "c")]
    [InlineData("csharp scripts", "cs", "c")]
    [InlineData("c++ source on D:", "cpp", "c")]
    [InlineData("c plus plus files", "cpp", "c")]
    [InlineData("f# files", "fs", "f")]
    [InlineData("fsharp programs", "fs", "f")]
    [InlineData("objective-c files", "m", null)]
    public void LanguageExtensionGlobs_FromQuery_DetectsLanguageFileTypes(string query, string canonical, string? stripped)
    {
        var hits = LanguageExtensionGlobs.FromQuery(query);

        var hit = Assert.Single(hits);
        Assert.Equal(canonical, hit.Canonical);
        Assert.Equal(stripped, hit.Stripped);
    }

    [Theory]
    [InlineData("files containing \"c#\"")]          // quoted -> a content term, not a file filter
    [InlineData("notes about c# in general")]        // no file-type noun after the language
    [InlineData("python files")]                      // plain-word language the model maps itself
    [InlineData("find the abc# token files")]        // "c#" is inside a larger token
    [InlineData("")]
    [InlineData(null)]
    public void LanguageExtensionGlobs_FromQuery_IgnoresNonFileTypeMentions(string? query)
    {
        Assert.Empty(LanguageExtensionGlobs.FromQuery(query));
    }

    [Fact]
    public void Resolve_CSharpFiles_ModelGuessedWrongExtension_IsCorrectedToCs()
    {
        // The reported bug: "c# files ..." -> phi-4-mini emits include "*.c".
        var plan = new SemanticSearchPlan { Pattern = "test", IncludeGlobs = new() { "*.c" } };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "c# files on C:/ containing the word \"test\"" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Equal(new[] { "*.cs" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_CSharpFiles_ModelEmittedNoGlob_AddsCs()
    {
        // The other observed failure mode: the model emits no include glob at all.
        var plan = new SemanticSearchPlan { Pattern = "test" };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "c# files containing test" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Equal(new[] { "*.cs" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_CAndCSharpFiles_ModelGotBothRight_KeepsBoth()
    {
        // When the model already produced the canonical extension, its sibling globs are trusted —
        // a deliberate "C and C# files" request must keep both *.c and *.cs.
        var plan = new SemanticSearchPlan { Pattern = "x", IncludeGlobs = new() { "*.c", "*.cs" } };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "c and c# files" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Equal(new[] { "*.c", "*.cs" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_CSharpAsContentTerm_DoesNotAddGlob()
    {
        // "c#" inside quotes is a content term; it must not become a file-type filter.
        var plan = new SemanticSearchPlan { Pattern = "c#", SearchMode = "content" };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "files containing \"c#\"" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Null(resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_NoOriginalQuery_LeavesModelGlobsUntouched()
    {
        // Backward compatible: callers (and tests) that don't supply the raw query are unaffected.
        var plan = new SemanticSearchPlan { Pattern = "test", IncludeGlobs = new() { "*.c" } };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(new[] { "*.c" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_ObjectiveCFiles_AddsM_WithNoStrippedExtensionToRemove()
    {
        // Objective-C has no predictable wrong guess (Stripped is null), so the canonical *.m is added
        // without removing any sibling glob — exercises the Stripped-is-null path of the language fix.
        var plan = new SemanticSearchPlan { Pattern = "test" };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "objective-c files containing test" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Equal(new[] { "*.m" }, resolved.IncludeGlobs);
    }

    // ---- explicit "*.ext" extension requests (".com files") ----------------

    [Fact]
    public void Resolve_DotComFiles_OverridesHallucinatedPattern_ForcesGlobAndFilenameMode()
    {
        // Reported bug: ".com files on C:/" -> phi-4-mini emits a content search for "*.gitignore".
        var plan = new SemanticSearchPlan
        {
            Directory = "C:/",
            Pattern = "*.gitignore",
            SearchMode = "content",
            UseRegex = true,
        };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = ".com files on C:/" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Equal(new[] { "*.com" }, resolved.IncludeGlobs);
        Assert.Equal(SearchMode.FileNames, resolved.SearchMode);
        Assert.Equal(".", resolved.Pattern);          // match-all filename synthesis
        Assert.True(resolved.UseRegex);
    }

    [Fact]
    public void Resolve_DotComFilesWithContentTerm_KeepsContentSearch()
    {
        // "containing MZ" is a real content term, so a genuine content search is preserved — only the
        // include glob is forced from the explicit ".com".
        var plan = new SemanticSearchPlan { Pattern = "MZ", SearchMode = "content" };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = ".com files containing MZ" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.Equal(new[] { "*.com" }, resolved.IncludeGlobs);
        Assert.Equal("MZ", resolved.Pattern);
        Assert.Equal(SearchMode.Content, resolved.SearchMode);
    }

    [Fact]
    public void ApplyExplicitExtensionGlobs_DottedToken_ForcesGlob()
    {
        var include = new List<string>();

        var forced = SemanticPlanApplier.ApplyExplicitExtensionGlobs(include, ".com files on C:/");

        Assert.Equal(new[] { "com" }, forced);
        Assert.Contains("*.com", include);
    }

    [Fact]
    public void ApplyExplicitExtensionGlobs_BareCategoryWord_NotForced()
    {
        // "text" is a known extension token, but without a leading dot it is a category phrase the
        // model maps to *.txt; only an explicit ".ext" is forced.
        var include = new List<string>();

        var forced = SemanticPlanApplier.ApplyExplicitExtensionGlobs(include, "text files on C:/");

        Assert.Empty(forced);
        Assert.Empty(include);
    }

    [Fact]
    public void ApplyExplicitExtensionGlobs_ExtensionInsideFilename_NotForced()
    {
        // ".docx" embedded in a filename must not be read as a standalone extension request.
        var include = new List<string>();

        var forced = SemanticPlanApplier.ApplyExplicitExtensionGlobs(include, "find report.docx files");

        Assert.Empty(forced);
    }

    [Fact]
    public void ApplyExplicitExtensionGlobs_AlreadyPresent_NoDuplicate()
    {
        var include = new List<string> { "*.com" };

        var forced = SemanticPlanApplier.ApplyExplicitExtensionGlobs(include, ".com files");

        Assert.Equal(new[] { "com" }, forced);
        Assert.Equal(new[] { "*.com" }, include);
    }

    [Fact]
    public void ApplyExplicitExtensionGlobs_RepeatedExtension_ForcedOnce()
    {
        // The same explicit extension named twice must dedupe — forced once, single glob.
        var include = new List<string>();

        var forced = SemanticPlanApplier.ApplyExplicitExtensionGlobs(include, ".log files and .log extensions");

        Assert.Equal(new[] { "log" }, forced);
        Assert.Equal(new[] { "*.log" }, include);
    }

    [Fact]
    public void ApplyExplicitExtensionGlobs_DotlessExistingGlob_StillForcesExtension()
    {
        // An include entry without a dot ("everything") must not block forcing the explicit extension —
        // exercises the no-dot path of the bare-extension comparison.
        var include = new List<string> { "everything" };

        var forced = SemanticPlanApplier.ApplyExplicitExtensionGlobs(include, ".com files");

        Assert.Equal(new[] { "com" }, forced);
        Assert.Contains("*.com", include);
    }

    [Theory]
    [InlineData("*.gitignore")]
    [InlineData("a?b")]
    [InlineData(".com")]
    [InlineData(".CS")]
    public void LooksLikeGlobToken_GlobShapedToken_True(string pattern)
        => Assert.True(SemanticPlanApplier.LooksLikeGlobToken(pattern));

    [Theory]
    [InlineData("MZ")]              // plain content term
    [InlineData("hello world")]    // multi-word content term
    [InlineData("report.docx ok")] // has a dot but not a leading-dot bare extension
    [InlineData(".")]              // single dot — not long enough to be an extension
    [InlineData(".co m")]          // leading dot but contains a non-extension char
    [InlineData("")]              // empty after trim
    [InlineData("   ")]            // whitespace-only -> empty after trim
    public void LooksLikeGlobToken_ContentTerm_False(string pattern)
        => Assert.False(SemanticPlanApplier.LooksLikeGlobToken(pattern));

    // ---- binary-extension detection (.com/.exe are skipped by name) --------

    private static readonly HashSet<string> KnownBinaryExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "com", "exe", "dll", "cpl", "scr" };

    [Fact]
    public void GetBinaryExtensionsToEnable_BinaryGlobs_ReturnsBinaryExtensions()
    {
        var result = SemanticPlanApplier.GetBinaryExtensionsToEnable(
            new[] { "*.com", "*.txt", "*.exe" }, KnownBinaryExtensions);

        Assert.Equal(new[] { "com", "exe" }, result);
    }

    [Fact]
    public void GetBinaryExtensionsToEnable_NoBinaryGlobs_ReturnsEmpty()
    {
        Assert.Empty(SemanticPlanApplier.GetBinaryExtensionsToEnable(
            new[] { "*.txt", "*.png" }, KnownBinaryExtensions));
    }

    [Fact]
    public void GetBinaryExtensionsToEnable_NullOrEmptyInputs_ReturnEmpty()
    {
        var emptyKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(SemanticPlanApplier.GetBinaryExtensionsToEnable(null, KnownBinaryExtensions));
        Assert.Empty(SemanticPlanApplier.GetBinaryExtensionsToEnable(Array.Empty<string>(), KnownBinaryExtensions));
        Assert.Empty(SemanticPlanApplier.GetBinaryExtensionsToEnable(new[] { "*.com" }, null!));
        Assert.Empty(SemanticPlanApplier.GetBinaryExtensionsToEnable(new[] { "*.com" }, emptyKnown));
    }

    // ---- hidden-file intent ("hidden files" -> the Search-hidden toggle, not an exclude glob) ----

    [Theory]
    [InlineData("hidden files")]
    [InlineData("all hidden files on C:")]
    [InlineData("show hidden files")]
    [InlineData("include hidden files")]
    [InlineData("png files including hidden files")]
    [InlineData("list the hidden folders")]
    public void DetectHiddenFilePreference_IncludeRequests_ReturnTrue(string query)
        => Assert.True(SemanticPlanApplier.DetectHiddenFilePreference(query));

    [Theory]
    [InlineData("all png files that are not a hidden file")]
    [InlineData("not hidden files")]
    [InlineData("no hidden files")]
    [InlineData("exclude hidden files")]
    [InlineData("without hidden files")]
    [InlineData("non-hidden files")]
    [InlineData("png files but skip hidden files")]
    [InlineData("find logs, ignore hidden files")]
    public void DetectHiddenFilePreference_ExcludeRequests_ReturnFalse(string query)
        => Assert.False(SemanticPlanApplier.DetectHiddenFilePreference(query));

    [Theory]
    [InlineData("all png files")]
    [InlineData("files containing the word hidden")]   // "hidden" is a content term, not a file filter
    [InlineData("")]
    [InlineData(null)]
    public void DetectHiddenFilePreference_NoHiddenFileMention_ReturnsNull(string? query)
        => Assert.Null(SemanticPlanApplier.DetectHiddenFilePreference(query));

    [Fact]
    public void DetectHiddenFilePreference_NegationInEarlierClause_DoesNotFlipInclude()
    {
        // The negation belongs to the first clause; the hidden mention is a separate include request.
        Assert.True(SemanticPlanApplier.DetectHiddenFilePreference("exclude tmp files, show hidden files"));
    }

    [Fact]
    public void Resolve_NotHiddenFiles_TogglesHiddenOff_AndDropsDotfileExcludeGlob()
    {
        // The reported bug: "all png files that are not a hidden file" -> the model approximates "not
        // hidden" with a dotfile-exclusion glob. The toggle should drive it and that glob be dropped.
        var plan = new SemanticSearchPlan
        {
            Pattern = "",
            IncludeGlobs = new() { "*.png" },
            ExcludeGlobs = new() { @".\.[a-zA-Z0-9].+" },
        };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "all png files that are not a hidden file" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.False(resolved.SearchHiddenFiles);
        Assert.Null(resolved.ExcludeGlobs);                 // the dotfile-exclusion glob was removed
        Assert.Equal(new[] { "*.png" }, resolved.IncludeGlobs);
    }

    [Fact]
    public void Resolve_NotHidden_KeepsRealExcludesDropsOnlyDotfileGlob()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = "",
            IncludeGlobs = new() { "*.png" },
            ExcludeGlobs = new() { "*.mov", ".*" },
        };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "png files that are not hidden" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.False(resolved.SearchHiddenFiles);
        Assert.Equal(new[] { "*.mov" }, resolved.ExcludeGlobs);
    }

    [Fact]
    public void Resolve_HiddenFiles_TogglesHiddenOn()
    {
        var plan = new SemanticSearchPlan { Pattern = "" };
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "find all hidden files" };

        var resolved = SemanticPlanApplier.Resolve(plan, ctx);

        Assert.True(resolved.SearchHiddenFiles);
    }

    [Fact]
    public void Resolve_NoHiddenMention_LeavesToggleUnset_UnlessModelSetsIt()
    {
        var ctx = new SemanticTranslationContext { Now = Now, OriginalQuery = "all png files" };

        Assert.Null(SemanticPlanApplier.Resolve(
            new SemanticSearchPlan { Pattern = "", IncludeGlobs = new() { "*.png" } }, ctx).SearchHiddenFiles);

        // The model's own searchHidden is the fallback when the query itself is silent on hidden files.
        Assert.False(SemanticPlanApplier.Resolve(
            new SemanticSearchPlan { Pattern = "", IncludeGlobs = new() { "*.png" }, SearchHidden = false }, ctx).SearchHiddenFiles);
    }

    [Fact]
    public void BuildExplanation_HiddenPreference_StatedInPlainWords()
    {
        Assert.Contains("excluding hidden files", SemanticPlanApplier.BuildExplanation(
            new ResolvedSearchPlan { SearchHiddenFiles = false }));
        Assert.Contains("including hidden files", SemanticPlanApplier.BuildExplanation(
            new ResolvedSearchPlan { SearchHiddenFiles = true }));
    }


    [Fact]
    public void BuildExplanation_UsesResolvedPattern_NotModelProse()
    {
        // The "yagursd" regression: the explanation must come from the resolved pattern, not the model.
        var resolved = new ResolvedSearchPlan
        {
            Directory = "D:/",
            Pattern = "yagu",
            SearchMode = SearchMode.FileNames,
            IncludeGlobs = new[] { "*.exe" },
            Explanation = "Listing .exes files on D:/ whose names contain the string 'yagursd.",
        };

        string text = SemanticPlanApplier.BuildExplanation(resolved);

        Assert.Contains("D:/", text);
        Assert.Contains("file names", text);
        Assert.Contains("yagu", text);
        Assert.DoesNotContain("yagursd", text);
        Assert.Contains("*.exe", text);
    }

    [Fact]
    public void BuildExplanation_RegexPattern_DescribedAsRegex()
    {
        var resolved = new ResolvedSearchPlan { Directory = "C:/", Pattern = @"\d+", UseRegex = true, SearchMode = SearchMode.Content };
        string text = SemanticPlanApplier.BuildExplanation(resolved);
        Assert.Contains("regular expression", text);
        Assert.Contains(@"\d+", text);
        Assert.Contains("file contents", text);
    }

    [Fact]
    public void BuildExplanation_IncludesSizeAndDateAndExcludeClauses()
    {
        var resolved = new ResolvedSearchPlan
        {
            Directory = "C:/",
            Pattern = "report",
            SearchMode = SearchMode.Both,
            ExcludeGlobs = new[] { "*.tmp" },
            MinFileSizeBytes = 100L * 1024 * 1024,
            ModifiedAfterDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        string text = SemanticPlanApplier.BuildExplanation(resolved);

        Assert.Contains("excluding *.tmp", text);
        Assert.Contains("larger than 100 MB", text);
        Assert.Contains("modified after 2026-01-01", text);
    }

    [Fact]
    public void BuildExplanation_EmptyPlan_FallsBackToGenericSummary()
    {
        string text = SemanticPlanApplier.BuildExplanation(new ResolvedSearchPlan());
        Assert.Equal("Searching the current directory \u2014 file names and contents.", text);
    }
}
