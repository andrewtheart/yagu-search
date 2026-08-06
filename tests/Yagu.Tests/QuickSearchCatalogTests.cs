using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// The user-managed quick-search list semantics: first-run seeding, load-time canonicalization, and the
/// add / edit / reorder / delete operations the Quick searches tab performs before persisting.
/// </summary>
public sealed class QuickSearchCatalogTests
{
    private static List<QuickSearchItem> SampleList() =>
    [
        new() { Id = "a", Label = "Alpha", Pattern = "alpha" },
        new() { Id = "b", Label = "Bravo", Pattern = "bravo" },
        new() { Id = "c", Label = "Charlie", Pattern = "charlie" },
    ];

    [Fact]
    public void Defaults_SeedEveryBuiltInPresetExceptTheCanonicalCodeAnnotationAction()
    {
        var defaults = QuickSearchCatalog.Defaults();

        Assert.NotEmpty(defaults);
        // The code-annotation search stays a fixed button (CLI --todos twin), not a deletable list entry.
        Assert.DoesNotContain(defaults, i => i.Id == QuickSearchPresets.CodeAnnotationsKey);

        foreach (var item in defaults)
        {
            var preset = QuickSearchPresets.Find(item.Id);
            Assert.NotNull(preset);
            // Seeded entries must reproduce the preset exactly, so both stay one source of truth.
            Assert.Equal(preset!.Pattern, item.Pattern);
            Assert.Equal(preset.CaseSensitive, item.CaseSensitive);
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
            Assert.False(string.IsNullOrWhiteSpace(item.Glyph));
            Assert.True(item.UseRegex);
            Assert.False(item.Semantic);
        }
    }

    [Fact]
    public void Normalize_TrimsRepairsAndDropsUnusableEntries()
    {
        var normalized = QuickSearchCatalog.Normalize(
        [
            new QuickSearchItem { Id = "  ", Label = "  Spaced  ", Pattern = "  todo  ", Glyph = "  " },
            new QuickSearchItem { Id = "dupe", Label = "First", Pattern = "one" },
            new QuickSearchItem { Id = "dupe", Label = "Second", Pattern = "two" },
            new QuickSearchItem { Id = "blank", Label = "No pattern", Pattern = "   " },
        ]);

        // The blank-pattern entry is unusable and is dropped; the rest survive.
        Assert.Equal(3, normalized.Count);
        Assert.Equal("Spaced", normalized[0].Label);
        Assert.Equal("todo", normalized[0].Pattern);
        Assert.Equal(QuickSearchItem.DefaultGlyph, normalized[0].Glyph);
        Assert.False(string.IsNullOrWhiteSpace(normalized[0].Id));

        // A duplicate id would make edit/delete ambiguous, so the second copy is re-keyed.
        Assert.NotEqual(normalized[1].Id, normalized[2].Id);
    }

    [Fact]
    public void Normalize_BlankLabelFallsBackToThePattern()
    {
        var normalized = QuickSearchCatalog.Normalize([new QuickSearchItem { Pattern = "needle" }]);
        Assert.Equal("needle", Assert.Single(normalized).Label);
    }

    [Fact]
    public void Normalize_MultilineForcesRegexToMatchTheSearchBoxCoupling()
    {
        var normalized = QuickSearchCatalog.Normalize(
            [new QuickSearchItem { Pattern = "a\\nb", UseRegex = false, Multiline = true }]);

        var item = Assert.Single(normalized);
        Assert.True(item.Multiline);
        Assert.True(item.UseRegex);
    }

    [Fact]
    public void Normalize_EmptyListIsPreservedSoDeletingEveryItemSticks()
    {
        Assert.Empty(QuickSearchCatalog.Normalize([]));
        Assert.Empty(QuickSearchCatalog.Normalize(null));
    }

    [Fact]
    public void Normalize_SkipsNullEntriesFromAHandEditedFile()
    {
        // A JSON array with a literal null deserializes to a null element; it must be dropped rather
        // than crash the load and lose every other quick search.
        var normalized = QuickSearchCatalog.Normalize(
            [null!, new QuickSearchItem { Id = "kept", Pattern = "needle" }, null!]);

        Assert.Equal("kept", Assert.Single(normalized).Id);
    }

    [Fact]
    public void Normalize_TreatsNullTextFieldsAsEmptyRatherThanThrowing()
    {
        // A hand-edited settings file can null out any string field; normalization must repair each one
        // exactly as it repairs a blank, so a null never reaches the search box.
        var normalized = QuickSearchCatalog.Normalize(
        [
            new QuickSearchItem { Id = null!, Label = null!, Glyph = null!, Tooltip = null!, Directory = null!, Pattern = "  needle  " },
            new QuickSearchItem { Pattern = null! },
        ]);

        var item = Assert.Single(normalized);
        Assert.Equal("needle", item.Pattern);
        Assert.Equal("needle", item.Label);
        Assert.Equal(QuickSearchItem.DefaultGlyph, item.Glyph);
        Assert.Equal(string.Empty, item.Tooltip);
        Assert.Equal(string.Empty, item.Directory);
        Assert.False(string.IsNullOrWhiteSpace(item.Id));
    }

    [Fact]
    public void Move_ReordersAndClampsAtBothEnds()
    {
        var items = SampleList();

        Assert.True(QuickSearchCatalog.Move(items, "c", -1));
        Assert.Equal(["a", "c", "b"], items.Select(i => i.Id));

        Assert.True(QuickSearchCatalog.Move(items, "a", 1));
        Assert.Equal(["c", "a", "b"], items.Select(i => i.Id));

        // Moving off either edge is a no-op rather than an error.
        Assert.False(QuickSearchCatalog.Move(items, "c", -1));
        Assert.False(QuickSearchCatalog.Move(items, "b", 5));
        Assert.Equal(["c", "a", "b"], items.Select(i => i.Id));
    }

    [Fact]
    public void Move_UnknownIdOrZeroDeltaChangesNothing()
    {
        var items = SampleList();
        Assert.False(QuickSearchCatalog.Move(items, "missing", 1));
        Assert.False(QuickSearchCatalog.Move(items, "a", 0));
        Assert.False(QuickSearchCatalog.Move(null, "a", 1));
        Assert.Equal(["a", "b", "c"], items.Select(i => i.Id));
    }

    [Fact]
    public void Remove_DeletesOnlyTheRequestedItem()
    {
        var items = SampleList();

        Assert.True(QuickSearchCatalog.Remove(items, "b"));
        Assert.Equal(["a", "c"], items.Select(i => i.Id));
        Assert.False(QuickSearchCatalog.Remove(items, "b"));
    }

    [Fact]
    public void Upsert_AppendsNewItemsAndReplacesExistingOnesInPlace()
    {
        var items = SampleList();

        Assert.True(QuickSearchCatalog.Upsert(items, new QuickSearchItem { Id = "b", Label = "Edited", Pattern = "edited" }));
        Assert.Equal(3, items.Count);
        Assert.Equal(["a", "b", "c"], items.Select(i => i.Id));
        Assert.Equal("Edited", items[1].Label);
        Assert.Equal("edited", items[1].Pattern);

        Assert.True(QuickSearchCatalog.Upsert(items, new QuickSearchItem { Label = "Delta", Pattern = "delta" }));
        Assert.Equal(4, items.Count);
        Assert.Equal("Delta", items[3].Label);
        Assert.False(string.IsNullOrWhiteSpace(items[3].Id));
    }

    [Fact]
    public void Upsert_RejectsAnItemWithNoPattern()
    {
        var items = SampleList();

        Assert.False(QuickSearchCatalog.Upsert(items, new QuickSearchItem { Id = "d", Label = "Blank", Pattern = "  " }));
        Assert.False(QuickSearchCatalog.Upsert(items, null));
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void Upsert_WithNoListIsANoOp()
    {
        Assert.False(QuickSearchCatalog.Upsert(null, new QuickSearchItem { Id = "a", Pattern = "alpha" }));
    }

    [Fact]
    public void Upsert_RoundTripsEverySavedOption()
    {
        var items = new List<QuickSearchItem>();
        QuickSearchCatalog.Upsert(items, new QuickSearchItem
        {
            Id = "opt",
            Label = "Options",
            Pattern = "p",
            UseRegex = true,
            CaseSensitive = true,
            Multiline = true,
            ExactMatch = true,
            Semantic = true,
        });

        var saved = Assert.Single(items);
        Assert.True(saved.CaseSensitive);
        Assert.True(saved.Multiline);
        Assert.True(saved.ExactMatch);
        Assert.True(saved.Semantic);
    }

    [Fact]
    public void IndexOf_MatchesCaseInsensitivelyAndReportsMisses()
    {
        var items = SampleList();
        Assert.Equal(1, QuickSearchCatalog.IndexOf(items, "B"));
        Assert.Equal(-1, QuickSearchCatalog.IndexOf(items, "missing"));
        Assert.Equal(-1, QuickSearchCatalog.IndexOf(items, null));
        Assert.Equal(-1, QuickSearchCatalog.IndexOf(null, "a"));
    }

    [Fact]
    public void IndexOf_SkipsNullEntriesInsteadOfThrowing()
    {
        List<QuickSearchItem> items = [null!, new QuickSearchItem { Id = "b", Pattern = "bravo" }];
        Assert.Equal(1, QuickSearchCatalog.IndexOf(items, "b"));
    }

    private static QuickSearchOptions SampleOptions() => new()
    {
        SearchModeIndex = 2,
        MultilineDotAll = true,
        IncludeFilterModeIndex = 1,
        IncludeGlobs = "  *.cs  ",
        ExcludeFilterModeIndex = 1,
        ExcludeGlobs = "  bin  ",
        ObeyGitignore = true,
        SkipExtensions = " .tmp ",
        SearchBinary = true,
        BinaryExtensions = " .dll ",
        SearchInsideArchives = true,
        ArchiveExtensions = " .zip ",
        SearchOnlineOnlyFiles = true,
        SearchHiddenFiles = false,
        SearchImageText = true,
        ImageOcrEngine = " tesseract ",
        SearchPdfText = true,
        UseContentIndex = true,
        MinFileSizeBytes = 1024,
        MaxFileSizeBytes = 4096,
        CreatedAfterDate = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
        CreatedBeforeDate = new DateTimeOffset(2024, 2, 3, 0, 0, 0, TimeSpan.Zero),
        ModifiedAfterDate = new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero),
        ModifiedBeforeDate = new DateTimeOffset(2024, 4, 5, 0, 0, 0, TimeSpan.Zero),
        MaxSearchDepth = 3,
    };

    [Fact]
    public void Normalize_KeepsTheCapturedAdvancedOptionsSnapshotAndTrimsIt()
    {
        var normalized = QuickSearchCatalog.Normalize(
            [new QuickSearchItem { Pattern = "p", Options = SampleOptions() }]);

        var options = Assert.Single(normalized).Options;
        Assert.NotNull(options);
        // Every captured field survives the round-trip, so a saved item restores the whole drawer.
        Assert.Equal(2, options!.SearchModeIndex);
        Assert.True(options.MultilineDotAll);
        Assert.Equal(1, options.IncludeFilterModeIndex);
        Assert.Equal("*.cs", options.IncludeGlobs);
        Assert.Equal("bin", options.ExcludeGlobs);
        Assert.True(options.ObeyGitignore);
        Assert.Equal(".tmp", options.SkipExtensions);
        Assert.True(options.SearchBinary);
        Assert.Equal(".dll", options.BinaryExtensions);
        Assert.True(options.SearchInsideArchives);
        Assert.Equal(".zip", options.ArchiveExtensions);
        Assert.True(options.SearchOnlineOnlyFiles);
        Assert.False(options.SearchHiddenFiles);
        Assert.True(options.SearchImageText);
        Assert.Equal("tesseract", options.ImageOcrEngine);
        Assert.True(options.SearchPdfText);
        Assert.True(options.UseContentIndex);
        Assert.Equal(1024, options.MinFileSizeBytes);
        Assert.Equal(4096, options.MaxFileSizeBytes);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), options.CreatedAfterDate);
        Assert.Equal(new DateTimeOffset(2024, 2, 3, 0, 0, 0, TimeSpan.Zero), options.CreatedBeforeDate);
        Assert.Equal(new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero), options.ModifiedAfterDate);
        Assert.Equal(new DateTimeOffset(2024, 4, 5, 0, 0, 0, TimeSpan.Zero), options.ModifiedBeforeDate);
        Assert.Equal(3, options.MaxSearchDepth);
    }

    [Fact]
    public void Normalize_ItemWithoutCapturedOptionsStaysNull()
    {
        var normalized = QuickSearchCatalog.Normalize([new QuickSearchItem { Pattern = "p" }]);
        var item = Assert.Single(normalized);
        Assert.Null(item.Options);
        Assert.False(item.HasOptions);
    }

    [Fact]
    public void NormalizedOptions_ClampsOutOfRangeValuesFromAHandEditedFile()
    {
        var options = new QuickSearchOptions
        {
            SearchModeIndex = 99,
            IncludeFilterModeIndex = -4,
            ExcludeFilterModeIndex = 7,
            MinFileSizeBytes = -10,
            MaxFileSizeBytes = -1,
            MaxSearchDepth = double.NaN,
        }.Normalized();

        Assert.Equal(3, options.SearchModeIndex);
        Assert.Equal(0, options.IncludeFilterModeIndex);
        Assert.Equal(1, options.ExcludeFilterModeIndex);
        Assert.Equal(0, options.MinFileSizeBytes);
        Assert.Equal(0, options.MaxFileSizeBytes);
        // NaN and negatives both mean "unlimited", which is the empty Max depth box.
        Assert.Null(options.MaxSearchDepth);
        Assert.Null(new QuickSearchOptions { MaxSearchDepth = -2 }.Normalized().MaxSearchDepth);
    }

    [Fact]
    public void NormalizedOptions_LeavesAnAlreadyUnlimitedDepthAlone()
    {
        // Null already means "unlimited"; the clamp must not invent a value for it.
        Assert.Null(new QuickSearchOptions { MaxSearchDepth = null }.Normalized().MaxSearchDepth);
        Assert.Equal(5, new QuickSearchOptions { MaxSearchDepth = 5 }.Normalized().MaxSearchDepth);
    }

    [Fact]
    public void NormalizedOptions_TreatsNullTextFieldsAsEmpty()
    {
        // Every free-text option can be nulled by hand-editing settings.json; normalization must yield
        // empty strings rather than throwing while the settings file is being loaded.
        var options = new QuickSearchOptions
        {
            IncludeGlobs = null!,
            ExcludeGlobs = null!,
            SkipExtensions = null!,
            BinaryExtensions = null!,
            ArchiveExtensions = null!,
            ImageOcrEngine = null!,
        }.Normalized();

        Assert.Equal(string.Empty, options.IncludeGlobs);
        Assert.Equal(string.Empty, options.ExcludeGlobs);
        Assert.Equal(string.Empty, options.SkipExtensions);
        Assert.Equal(string.Empty, options.BinaryExtensions);
        Assert.Equal(string.Empty, options.ArchiveExtensions);
        Assert.Equal(string.Empty, options.ImageOcrEngine);
    }

    [Fact]
    public void Clone_DeepCopiesTheCapturedOptionsSoEditingADraftCannotLeak()
    {
        var original = new QuickSearchItem { Id = "a", Pattern = "p", Options = SampleOptions() };
        var copy = original.Clone();

        copy.Options!.SearchModeIndex = 0;
        copy.Options.IncludeGlobs = "changed";

        Assert.Equal(2, original.Options!.SearchModeIndex);
        Assert.Equal("  *.cs  ", original.Options.IncludeGlobs);
    }

    [Fact]
    public void Upsert_CarriesTheCapturedOptionsThrough()
    {
        var items = new List<QuickSearchItem>();
        Assert.True(QuickSearchCatalog.Upsert(items,
            new QuickSearchItem { Id = "opt", Pattern = "p", Options = SampleOptions() }));

        Assert.True(Assert.Single(items).HasOptions);
    }

    [Fact]
    public void Normalize_TrimsTheSearchDirectoryAndKeepsIt()
    {
        var normalized = QuickSearchCatalog.Normalize(
            [new QuickSearchItem { Pattern = "p", Directory = "  C:\\src  " }]);

        var item = Assert.Single(normalized);
        Assert.Equal("C:\\src", item.Directory);
        Assert.False(item.SearchesAllDrives);
    }

    [Fact]
    public void SearchesAllDrives_IsTrueWhenNoFolderWasChosen()
    {
        // An unset folder means "start at the root of every drive", matching an empty directory box.
        Assert.True(new QuickSearchItem().SearchesAllDrives);
        Assert.True(new QuickSearchItem { Directory = "   " }.SearchesAllDrives);
        Assert.True(Assert.Single(QuickSearchCatalog.Normalize(
            [new QuickSearchItem { Pattern = "p", Directory = "   " }])).SearchesAllDrives);
    }

    [Fact]
    public void Clone_CopiesTheSearchDirectory()
    {
        var copy = new QuickSearchItem { Pattern = "p", Directory = "D:\\logs" }.Clone();
        Assert.Equal("D:\\logs", copy.Directory);
    }

    [Fact]
    public void Upsert_CarriesTheSearchDirectoryThrough()
    {
        var items = SampleList();
        Assert.True(QuickSearchCatalog.Upsert(items,
            new QuickSearchItem { Id = "b", Pattern = "bravo", Directory = "E:\\work" }));

        Assert.Equal("E:\\work", items[1].Directory);
    }
}
