using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the pure Indexing UI presentation helper <see cref="ContentIndexUiStatus"/> (plan §6.2,
/// Phase 2). Every provenance/coverage decision is derived from the single §3.5 classification plus
/// settings, so these lock the glyph/label/tooltip mapping and the show/hide gates that the thin WinUI
/// layers depend on.
/// </summary>
public sealed class ContentIndexUiStatusTests
{
    [Fact]
    public void ProvenanceFor_FreshMember_IsIndexAccelerated()
        => Assert.Equal(
            IndexProvenanceKind.IndexAccelerated,
            ContentIndexUiStatus.ProvenanceFor(new IndexPathClassification.FreshIndexedMember(1, 2)));

    [Fact]
    public void ProvenanceFor_SpecialSource_IsExtractedSource()
        => Assert.Equal(
            IndexProvenanceKind.ExtractedSource,
            ContentIndexUiStatus.ProvenanceFor(new IndexPathClassification.SpecialSource(SpecialSourceKind.ImageOcr)));

    [Theory]
    [MemberData(nameof(LiveScannedClassifications))]
    public void ProvenanceFor_EverythingElse_IsLiveScanned(IndexPathClassification classification)
        => Assert.Equal(IndexProvenanceKind.LiveScanned, ContentIndexUiStatus.ProvenanceFor(classification));

    public static IEnumerable<object[]> LiveScannedClassifications() =>
    [
        [new IndexPathClassification.FreshIndexedNonmember(1, 2)],
        [new IndexPathClassification.DirtyByUsn(2, "changed")],
        [new IndexPathClassification.Unindexed("over-cap")],
        [new IndexPathClassification.UntrustedRoot("network")],
    ];

    [Fact]
    public void ProvenanceFor_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => ContentIndexUiStatus.ProvenanceFor(null!));

    [Theory]
    [InlineData(IndexProvenanceKind.IndexAccelerated, "Index-accelerated")]
    [InlineData(IndexProvenanceKind.LiveScanned, "Live-scanned")]
    [InlineData(IndexProvenanceKind.ExtractedSource, "Extracted source")]
    public void ProvenanceLabel_MatchesKind(IndexProvenanceKind kind, string expected)
        => Assert.Equal(expected, ContentIndexUiStatus.ProvenanceLabel(kind));

    [Theory]
    [InlineData(IndexProvenanceKind.IndexAccelerated)]
    [InlineData(IndexProvenanceKind.LiveScanned)]
    [InlineData(IndexProvenanceKind.ExtractedSource)]
    public void ProvenanceGlyph_IsNonEmptyAndDistinctFromLabel(IndexProvenanceKind kind)
        => Assert.False(string.IsNullOrWhiteSpace(ContentIndexUiStatus.ProvenanceGlyph(kind)));

    [Theory]
    [InlineData(IndexProvenanceKind.IndexAccelerated)]
    [InlineData(IndexProvenanceKind.LiveScanned)]
    [InlineData(IndexProvenanceKind.ExtractedSource)]
    public void ProvenanceTooltip_AlwaysStatesContentIsReadLive(IndexProvenanceKind kind)
        => Assert.Contains("read live", ContentIndexUiStatus.ProvenanceTooltip(kind));

    [Theory]
    [InlineData(false, true, true, false)]   // master off → never shown
    [InlineData(true, false, true, false)]   // setting off → never shown
    [InlineData(true, true, false, false)]   // index did not participate → not shown
    [InlineData(true, true, true, true)]     // all conditions met → shown
    public void ShouldShowProvenance_RequiresAllThreeConditions(bool enable, bool setting, bool participated, bool expected)
        => Assert.Equal(expected, ContentIndexUiStatus.ShouldShowProvenance(enable, setting, participated));

    [Theory]
    [InlineData(false, true, 3, 1, IndexSearchCoverage.Off)]  // disabled
    [InlineData(true, false, 3, 1, IndexSearchCoverage.Off)]  // not used this search
    [InlineData(true, true, 0, 4, IndexSearchCoverage.Bypassed)]
    [InlineData(true, true, 2, 1, IndexSearchCoverage.Partial)]
    [InlineData(true, true, 3, 0, IndexSearchCoverage.Full)]
    public void Coverage_DerivesFromCounts(bool enabled, bool used, int accelerated, int live, IndexSearchCoverage expected)
        => Assert.Equal(expected, ContentIndexUiStatus.Coverage(enabled, used, accelerated, live));

    [Theory]
    [InlineData(IndexSearchCoverage.Full, "fully accelerated")]
    [InlineData(IndexSearchCoverage.Partial, "partially accelerated")]
    [InlineData(IndexSearchCoverage.Bypassed, "bypassed")]
    [InlineData(IndexSearchCoverage.Off, "off")]
    public void CoverageLabel_MentionsState(IndexSearchCoverage coverage, string fragment)
        => Assert.Contains(fragment, ContentIndexUiStatus.CoverageLabel(coverage));

    [Fact]
    public void CoverageLabel_NeverReadsAsTheDiskFullWarning()
    {
        // "Index: full" (full coverage — everything worked) sat one word away from "Index: disk full"
        // (the drive ran out of room), so the success label was read as a storage failure. No coverage
        // label may end in the standalone word "full" again. ("fully accelerated" is unambiguous.)
        foreach (IndexSearchCoverage coverage in Enum.GetValues<IndexSearchCoverage>())
        {
            string label = ContentIndexUiStatus.CoverageLabel(coverage);
            Assert.DoesNotContain("full", label.Split(' '), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Indexes: all healthy")]
    [InlineData("Index: accelerating")]
    [InlineData("Index: fully accelerated")]
    [InlineData("Index: ready")]
    public void IsFullSuccessLabel_AcceptsEveryUnqualifiedSuccessState(string label)
        => Assert.True(ContentIndexUiStatus.IsFullSuccessLabel(label));

    [Theory]
    // Qualified acceleration: a root still needs attention, so the green check must stay off.
    [InlineData("Index: accelerating (1 of 4 needs attention)")]
    [InlineData("Index: partially accelerating")]
    [InlineData("Index: partially accelerated")]
    [InlineData("Index: 3/4 drives healthy")]
    [InlineData("Index: available \u00b7 not accelerated")]
    [InlineData("Index: bypassed")]
    [InlineData("Index: off")]
    [InlineData("Index: disk full")]
    [InlineData("Index: rebuild required")]
    [InlineData("Index: 1 drive needs build")]
    [InlineData("Index: freshness unavailable")]
    [InlineData("Index: no maintained indexes")]
    [InlineData("Index: not built for this folder")]
    [InlineData("Index: update needed")]
    [InlineData("Indexing paused")]
    [InlineData("Indexing\u2026 42%")]
    [InlineData("indexes: all healthy")] // case-sensitive by design
    [InlineData("")]
    [InlineData(null)]
    public void IsFullSuccessLabel_RejectsQualifiedAndFailingStates(string? label)
        => Assert.False(ContentIndexUiStatus.IsFullSuccessLabel(label));

    [Fact]
    public void FullSuccessLabels_AreShortEnoughToSurviveTheStatusBarClamp()
    {
        // The status-bar setter clamps through TrimStatusLabel. A success label long enough to be
        // ellipsized would no longer match IsFullSuccessLabel, silently dropping the green check.
        string[] successLabels =
        [
            ContentIndexUiStatus.AllHealthyLabel,
            ContentIndexUiStatus.AcceleratingLabel,
            ContentIndexUiStatus.FullyAcceleratedLabel,
            ContentIndexUiStatus.ReadyLabel,
        ];

        foreach (string label in successLabels)
        {
            Assert.True(label.Length <= ContentIndexUiStatus.StatusLabelMaxLength, label);
            Assert.True(ContentIndexUiStatus.IsFullSuccessLabel(ContentIndexUiStatus.TrimStatusLabel(label)), label);
        }
    }

    [Fact]
    public void FullSuccessLabels_MatchTheProducersThatEmitThem()
    {
        Assert.Equal(ContentIndexUiStatus.FullyAcceleratedLabel, ContentIndexUiStatus.CoverageLabel(IndexSearchCoverage.Full));
        Assert.Equal(
            ContentIndexUiStatus.AllHealthyLabel,
            ContentIndexUiStatus.AllDriveHealthLabel([new(@"C:\", IndexRootHealthKind.Healthy, "healthy")]));
    }

    [Fact]
    public void AllDriveHealthLabel_QualifiedByAttention_IsNeverAFullSuccessLabel()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.RebuildRequired, "attention"),
        ];

        string label = ContentIndexUiStatus.AllDriveHealthLabel(roots, IndexSearchCoverage.Full);

        Assert.Equal("Index: accelerating (1 of 2 needs attention)", label);
        Assert.False(ContentIndexUiStatus.IsFullSuccessLabel(label));
    }

    [Theory]
    [InlineData(IndexSearchCoverage.Full)]
    [InlineData(IndexSearchCoverage.Partial)]
    [InlineData(IndexSearchCoverage.Bypassed)]
    [InlineData(IndexSearchCoverage.Off)]
    public void CoverageGlyph_IsNonEmpty(IndexSearchCoverage coverage)
        => Assert.False(string.IsNullOrWhiteSpace(ContentIndexUiStatus.CoverageGlyph(coverage)));

    [Theory]
    [InlineData(IndexSearchCoverage.Full)]
    [InlineData(IndexSearchCoverage.Partial)]
    [InlineData(IndexSearchCoverage.Bypassed)]
    [InlineData(IndexSearchCoverage.Off)]
    public void CoverageTooltip_AlwaysStatesMatchingFilesReadLive(IndexSearchCoverage coverage)
        => Assert.Contains("read live", ContentIndexUiStatus.CoverageTooltip(coverage, filesPruned: 5));

    [Fact]
    public void CoverageTooltip_ReportsSkippedCount_WhenAccelerated()
    {
        Assert.Contains("skipped 1,234 file", ContentIndexUiStatus.CoverageTooltip(IndexSearchCoverage.Full, filesPruned: 1234));
        // Bypassed/off never claim skipped files even if a stray count is passed.
        Assert.DoesNotContain("skipped", ContentIndexUiStatus.CoverageTooltip(IndexSearchCoverage.Bypassed, filesPruned: 1234));
    }

    [Fact]
    public void CoverageCliSummary_MatchesCoverageState()
    {
        Assert.Contains("accelerated", ContentIndexUiStatus.CoverageCliSummary(IndexSearchCoverage.Full, 500));
        Assert.Contains("skipped 500 file", ContentIndexUiStatus.CoverageCliSummary(IndexSearchCoverage.Full, 500));
        Assert.Equal("Content index: accelerated.", ContentIndexUiStatus.CoverageCliSummary(IndexSearchCoverage.Full, 0));
        Assert.Contains("partial", ContentIndexUiStatus.CoverageCliSummary(IndexSearchCoverage.Partial, 7));
        Assert.Contains("not used", ContentIndexUiStatus.CoverageCliSummary(IndexSearchCoverage.Bypassed, 0));
        Assert.Null(ContentIndexUiStatus.CoverageCliSummary(IndexSearchCoverage.Off, 0)); // ordinary live-scan → print nothing

        // ASCII-only: CLI output must not mojibake on non-UTF-8 Windows consoles (no em-dash/smart chars).
        foreach (var c in new[] { IndexSearchCoverage.Full, IndexSearchCoverage.Partial, IndexSearchCoverage.Bypassed })
            Assert.All(ContentIndexUiStatus.CoverageCliSummary(c, 3)!, ch => Assert.True(ch < 128, $"non-ASCII char U+{(int)ch:X4} in CLI summary"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShouldShowStatus_RequiresFeatureAndSetting(bool enable, bool setting)
        => Assert.Equal(enable && setting, ContentIndexUiStatus.ShouldShowStatus(enable, setting));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void EffectiveDefaultUseIndex_RequiresBoth(bool master, bool byDefault, bool expected)
        => Assert.Equal(expected, ContentIndexUiStatus.EffectiveDefaultUseIndex(master, byDefault));

    [Fact]
    public void FormatMegabytes_ZeroOrNegative_ReportsUnset()
    {
        Assert.Contains("unset", ContentIndexUiStatus.FormatMegabytes(0));
        Assert.Contains("unset", ContentIndexUiStatus.FormatMegabytes(-5));
    }

    [Fact]
    public void FormatMegabytes_SmallValue_HasNoGigabytes()
    {
        string text = ContentIndexUiStatus.FormatMegabytes(512);
        Assert.Contains("512 MB", text);
        Assert.DoesNotContain("GB", text);
    }

    [Fact]
    public void FormatMegabytes_LargeValue_IncludesGigabytes()
    {
        string text = ContentIndexUiStatus.FormatMegabytes(4096);
        Assert.Contains("4096 MB", text);
        Assert.Contains("4.0 GB", text);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-10, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatBytes_ScalesToHumanReadableUnits(long bytes, string expected)
        => Assert.Equal(expected, ContentIndexUiStatus.FormatBytes(bytes));

    [Fact]
    public void FormatStorageLines_Empty_SaysNoIndexes()
    {
        var summary = new IndexStorageSummary(Array.Empty<IndexStorageStat>(), 0, 0, @"C:\store");
        var lines = ContentIndexUiStatus.FormatStorageLines(summary);
        Assert.Single(lines);
        Assert.Contains("empty", lines[0]);
    }

    [Fact]
    public void FormatStorageLines_HeaderAndPerIndexBreakdown()
    {
        var built = new DateTimeOffset(2026, 7, 20, 16, 44, 0, TimeSpan.Zero);
        var summary = new IndexStorageSummary(
            new[]
            {
                new IndexStorageStat(@"C:\proj".GetHashCode().ToString(), @"C:\proj", 1_800_000_000, 200_000, 10, built,
                    IndexStorageHealth.Healthy, RootExists: true, Problem: null),
                new IndexStorageStat("deadbeef", RootPath: null, 4096, 0, 0, BuiltUtc: null,
                    IndexStorageHealth.CorruptOrIncomplete, RootExists: false, Problem: "No valid manifest."),
            },
            TotalSizeBytes: 1_800_004_096,
            TotalDocuments: 200_000,
            StorageDirectory: @"C:\store");

        var lines = ContentIndexUiStatus.FormatStorageLines(summary);

        // Header: total size + index count + stored content-record count.
        Assert.Contains("Index storage:", lines[0]);
        Assert.Contains("1.7 GB", lines[0]);
        Assert.Contains("2 index(es)", lines[0]);
        Assert.Contains("200,000 stored content records", lines[0]);

        // Readable index: root, size, stored records, layered segment count, build time.
        Assert.Contains(@"C:\proj", lines[1]);
        Assert.Contains("1.7 GB", lines[1]);
        Assert.Contains("200,000 stored content records", lines[1]);
        Assert.Contains("base + 10 segment(s)", lines[1]);
        Assert.Contains("active generation built", lines[1]);

        // Unidentified damaged index: size + exact repair state, no misleading partial label.
        Assert.Contains("deadbeef", lines[2]);
        Assert.Contains("corrupt or incomplete", lines[2]);
        Assert.Contains("can be deleted", lines[2]);
        Assert.DoesNotContain("unreadable or partial", lines[2]);
    }

    [Fact]
    public void FormatStorageLines_ReadableIndex_WithoutBuildTime_OmitsBuiltSuffix()
    {
        // A readable index with no recorded build time and no delta segments exercises the null-BuiltUtc
        // branch (no "built ..." suffix) and the single-generation layer label.
        var summary = new IndexStorageSummary(
            new[] { new IndexStorageStat("s0", @"C:\fresh", 5000, 3, 0, BuiltUtc: null,
                IndexStorageHealth.Healthy, RootExists: true, Problem: null) },
            TotalSizeBytes: 5000,
            TotalDocuments: 3,
            StorageDirectory: @"C:\store");

        var lines = ContentIndexUiStatus.FormatStorageLines(summary);

        Assert.Contains(@"C:\fresh", lines[1]);
        Assert.Contains("single generation", lines[1]);
        Assert.DoesNotContain("built", lines[1]);
    }

    [Fact]
    public void FormatStorageLines_SingleGenerationIndex_SaysSingleGeneration()
    {
        var summary = new IndexStorageSummary(
            new[] { new IndexStorageStat("s", @"C:\a", 5000, 42, 0, DateTimeOffset.UtcNow,
                IndexStorageHealth.Healthy, RootExists: true, Problem: null) },
            5000, 42, @"C:\store");
        var lines = ContentIndexUiStatus.FormatStorageLines(summary);
        Assert.Contains("single generation", lines[1]);
        Assert.DoesNotContain("segment", lines[1]);
    }

    [Theory]
    [InlineData(IndexStorageHealth.SourceMissing, "source folder missing")]
    [InlineData(IndexStorageHealth.IncompatibleFormat, "old index format")]
    [InlineData(IndexStorageHealth.IncompatibleRepresentation, "old content representation")]
    [InlineData(IndexStorageHealth.CorruptOrIncomplete, "corrupt or incomplete")]
    [InlineData(IndexStorageHealth.Healthy, "healthy")]
    public void StorageHealthLabel_ExplainsExactState(IndexStorageHealth health, string expected)
        => Assert.Contains(expected, ContentIndexUiStatus.StorageHealthLabel(health), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void FormatStorageLines_IncompatibleRecoverableIndex_OffersRepair()
    {
        var summary = new IndexStorageSummary(
            new[] { new IndexStorageStat("scope", @"E:\", 8192, 12, 0, DateTimeOffset.UtcNow,
                IndexStorageHealth.IncompatibleRepresentation, RootExists: true,
                Problem: "Built with content representation v1; this build requires v3.") },
            8192, 12, @"C:\store");

        string line = ContentIndexUiStatus.FormatStorageLines(summary)[1];

        Assert.Contains(@"E:\", line);
        Assert.Contains("old content representation", line);
        Assert.Contains("Repair index", line);
        Assert.DoesNotContain("unreadable or partial", line);
    }

    [Fact]
    public void FormatStorageLines_UnrepairableIndexWithoutProblem_UsesSafeDefault()
    {
        var summary = new IndexStorageSummary(
            new[] { new IndexStorageStat("unknown", RootPath: null, 128, 0, 0, BuiltUtc: null,
                IndexStorageHealth.CorruptOrIncomplete, RootExists: false, Problem: null) },
            128, 0, @"C:\store");

        string line = ContentIndexUiStatus.FormatStorageLines(summary)[1];

        Assert.Contains("No trustworthy root metadata", line, StringComparison.Ordinal);
        Assert.Contains("can be deleted", line, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterStateSummary_Off_StatesFeatureIsOff()
    {
        string text = ContentIndexUiStatus.MasterStateSummary(enableContentIndex: false, useContentIndexByDefault: true);
        Assert.Contains("off", text);
    }

    [Fact]
    public void MasterStateSummary_OnByDefault_MentionsUsedByDefault()
    {
        string text = ContentIndexUiStatus.MasterStateSummary(enableContentIndex: true, useContentIndexByDefault: true);
        Assert.Contains("used by default", text);
    }

    [Fact]
    public void MasterStateSummary_OnNotDefault_MentionsOptIn()
    {
        string text = ContentIndexUiStatus.MasterStateSummary(enableContentIndex: true, useContentIndexByDefault: false);
        Assert.Contains("opt in", text);
    }

    [Theory]
    [InlineData(false, true, 3, 3, IndexAvailability.Off)]     // feature off
    [InlineData(true, false, 3, 3, IndexAvailability.NotRequested)] // opted out
    [InlineData(true, true, 0, 3, IndexAvailability.None)]     // opted in, none built
    [InlineData(true, true, 0, 0, IndexAvailability.None)]     // no roots
    [InlineData(true, true, 2, 3, IndexAvailability.Partial)]  // some built
    [InlineData(true, true, 3, 3, IndexAvailability.Available)] // all built
    public void Availability_DerivesFromExistenceCounts(bool enable, bool used, int withIndex, int total, IndexAvailability expected)
        => Assert.Equal(expected, ContentIndexUiStatus.Availability(enable, used, withIndex, total));

    [Theory]
    [InlineData(IndexAvailability.Off, false)]
    [InlineData(IndexAvailability.NotRequested, true)]
    [InlineData(IndexAvailability.None, true)]
    [InlineData(IndexAvailability.Partial, true)]
    [InlineData(IndexAvailability.Available, true)]
    public void ShouldShowAvailability_HiddenOnlyWhenOff(IndexAvailability availability, bool expected)
        => Assert.Equal(expected, ContentIndexUiStatus.ShouldShowAvailability(availability));

    [Theory]
    [InlineData(IndexAvailability.Available)]
    [InlineData(IndexAvailability.Partial)]
    [InlineData(IndexAvailability.None)]
    [InlineData(IndexAvailability.NotRequested)]
    [InlineData(IndexAvailability.Off)]
    public void AvailabilityLabel_MentionsIndex(IndexAvailability availability)
        => Assert.Contains("Index", ContentIndexUiStatus.AvailabilityLabel(availability));

    [Theory]
    [InlineData(IndexAvailability.Available)]
    [InlineData(IndexAvailability.Partial)]
    [InlineData(IndexAvailability.None)]
    [InlineData(IndexAvailability.NotRequested)]
    [InlineData(IndexAvailability.Off)]
    public void AvailabilityGlyph_IsNonEmpty(IndexAvailability availability)
        => Assert.False(string.IsNullOrWhiteSpace(ContentIndexUiStatus.AvailabilityGlyph(availability)));

    [Theory]
    [InlineData(IndexAvailability.Available)]
    [InlineData(IndexAvailability.Partial)]
    [InlineData(IndexAvailability.None)]
    [InlineData(IndexAvailability.NotRequested)]
    [InlineData(IndexAvailability.Off)]
    public void AvailabilityTooltip_AlwaysStatesFilesReadLive(IndexAvailability availability)
        => Assert.Contains("read live", ContentIndexUiStatus.AvailabilityTooltip(availability));

    [Theory]
    [InlineData("AtStartup", "at app startup")]
    [InlineData("WhenIdle", "when your PC is idle")]
    [InlineData("Continuous", "continuously while Yagu is open")]
    [InlineData("OnSchedule", "on your schedule")]
    [InlineData("WhenEnabled", "when the feature is enabled")]
    public void SchedulingHint_ExplainsAutomaticTriggers(string trigger, string expected)
        => Assert.Contains(expected, ContentIndexUiStatus.SchedulingHint(trigger));

    [Fact]
    public void SchedulingHint_ListsEveryActiveTrigger_WhenSeveralAreCombined()
    {
        string hint = ContentIndexUiStatus.SchedulingHint("AtStartup, OnSchedule");
        Assert.Contains("at app startup", hint);
        Assert.Contains("on your schedule", hint);
        Assert.Contains(" and ", hint);
        Assert.DoesNotContain("Automatic indexing is off", hint);
    }

    [Fact]
    public void SchedulingHint_ThreeTriggers_UsesCommaListGrammar()
    {
        string hint = ContentIndexUiStatus.SchedulingHint("AtStartup, OnSchedule, WhenEnabled");

        Assert.Contains("at app startup, on your schedule, and when the feature is enabled", hint);
    }

    [Fact]
    public void SchedulingHint_ContinuousSupersedesRedundantIdlePhrase()
    {
        string hint = ContentIndexUiStatus.SchedulingHint("WhenIdle, Continuous");

        Assert.Contains("continuously while Yagu is open", hint);
        Assert.DoesNotContain("when your PC is idle", hint);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-unknown")]
    public void SchedulingHint_ManualOrUnknown_SaysAutomaticIsOff(string? trigger)
    {
        string hint = ContentIndexUiStatus.SchedulingHint(trigger);
        Assert.Contains("Automatic indexing is off", hint);
        Assert.Contains("only when you start a build", hint);
    }

    [Fact]
    public void SchedulingHint_IsCaseInsensitive()
        => Assert.Equal(
            ContentIndexUiStatus.SchedulingHint("AtStartup"),
            ContentIndexUiStatus.SchedulingHint("atstartup"));

    [Fact]
    public void MaintenanceAlreadyRunningNote_NamesTheFolderAndPromisesNoNewBuild()
    {
        string note = ContentIndexUiStatus.MaintenanceAlreadyRunningNote(@"C:\");

        Assert.Contains(@"already running for C:\", note);
        Assert.Contains("reads files directly", note);
        // The card this replaces offered to start a build; the note must not repeat that promise.
        Assert.DoesNotContain("can build", note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaintenanceAlreadyRunningNote_UntrackedPass_StillSaysIndexingIsRunning(string? folder)
    {
        string note = ContentIndexUiStatus.MaintenanceAlreadyRunningNote(folder);

        Assert.StartsWith("Indexing is already running.", note, StringComparison.Ordinal);
        Assert.Contains("reads files directly", note);
    }

    [Fact]
    public void AllDriveHealth_MixedFailures_UsesAttentionPrecedence()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.RebuildRequired, "rebuild required", @"D:\"),
            new(@"E:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"F:\", IndexRootHealthKind.FreshnessUnavailable, "live scan only"),
        ];

        Assert.Equal("Index: 2 of 4 need attention", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uE7BA", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.Contains("2 drives or indexed folders", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.True(roots[1].CanRepair);
        Assert.False(roots[3].CanRepair);
    }

    [Fact]
    public void AllDriveHealth_CatchupLimit_OffersIncrementalRecoveryWithoutClaimingRebuildIsRequired()
    {
        var root = new IndexRootHealthEntry(
            @"C:\",
            IndexRootHealthKind.FreshnessUnavailable,
            "catch-up limit reached",
            IncrementalRoot: @"C:\");

        Assert.True(root.NeedsAttention);
        Assert.True(root.CanIncrementallyRefresh);
        Assert.Equal(@"C:\", root.IncrementalRoot);
        Assert.False(root.CanRepair);
        Assert.Null(root.RepairRoot);
    }

    [Theory]
    [InlineData(IndexSearchCoverage.Full, "Index: accelerating (2 of 4 need attention)")]
    [InlineData(IndexSearchCoverage.Partial, "Index: partially accelerating (2 of 4 need attention)")]
    public void AllDriveHealth_ActiveAcceleration_PreservesActivityAndAttentionCount(
        IndexSearchCoverage coverage,
        string expected)
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.RebuildRequired, "rebuild required", @"D:\"),
            new(@"E:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"F:\", IndexRootHealthKind.FreshnessUnavailable, "live scan only"),
        ];

        Assert.Equal(expected, ContentIndexUiStatus.AllDriveHealthLabel(roots, coverage));
    }

    [Fact]
    public void AllDriveHealth_ActiveAcceleration_UsesSingularAttentionGrammar()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.FreshnessUnavailable, "live scan only"),
            new(@"E:\", IndexRootHealthKind.Healthy, "healthy"),
        ];

        Assert.Equal(
            "Index: accelerating (1 of 3 needs attention)",
            ContentIndexUiStatus.AllDriveHealthLabel(roots, IndexSearchCoverage.Full));
    }

    [Fact]
    public void AllDriveHealth_OneUnavailableIndex_UsesAggregateLabelInsteadOfGlobalUnavailableClaim()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"E:\", IndexRootHealthKind.FreshnessUnavailable, "live scan only"),
        ];

        Assert.Equal("Index: 1 of 3 needs attention", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uE7BA", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.Contains("One drive or indexed folder", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }

    [Fact]
    public void AllDriveHealth_AllHealthy_ReportsEveryDriveHealthy()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.Healthy, "healthy"),
        ];

        Assert.Equal("Indexes: all healthy", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uE9F5", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.Contains("Every ready local drive", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }

    [Fact]
    public void AllDriveHealth_EmptySnapshot_ReportsNoReadyDrives()
    {
        IndexRootHealthEntry[] roots = Array.Empty<IndexRootHealthEntry>();

        Assert.Equal("Index: no ready drives", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Contains("No ready local drives", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }

    [Fact]
    public void AllDriveHealth_OnlyUnindexedUnmaintainedDrives_ReportsNoMaintainedIndexes()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.NotIndexed, "not indexed"),
            new(@"D:\", IndexRootHealthKind.NotIndexed, "not indexed"),
        ];

        Assert.Equal("Index: no maintained indexes", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uEA39", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.Contains("Unindexed unmaintained drives are informational", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.Contains("excluded from overall health totals and warnings", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }

    [Fact]
    public void AllDriveHealth_LeftoverIndex_IsInformationalAndNotAHealthWarning()
    {
        IndexRootHealthEntry leftover = ContentIndexUiStatus.UnregisteredRootHealth(@"F:\", hasStoredIndex: true);

        Assert.False(leftover.NeedsAttention);
        Assert.False(leftover.IsHealthy);
        Assert.True(leftover.HasStoredIndex);
        Assert.Equal(IndexRootHealthKind.LeftoverIndex, leftover.Kind);
        Assert.Contains("leftover index — not maintained", leftover.Status);
        Assert.True(leftover.CanMaintain);
        Assert.Equal(@"F:\", leftover.MaintainRoot);
        Assert.True(leftover.CanDeleteStoredIndex);
        Assert.Equal(@"F:\", leftover.DeleteRoot);
        // A leftover root already has an index on disk, so the quick "Add to index" affordance must
        // stay off — Maintain (adopt the existing index) is the correct, non-destructive action.
        Assert.False(leftover.CanAddToIndex);
        Assert.Null(leftover.AddRoot);
        Assert.Equal("Index: no maintained indexes", ContentIndexUiStatus.AllDriveHealthLabel([leftover]));
        Assert.Equal("\uE9F5", ContentIndexUiStatus.AllDriveHealthGlyph([leftover]));
        Assert.Contains("excluded from overall health totals and warnings", ContentIndexUiStatus.AllDriveHealthSummary([leftover]));
    }

    [Fact]
    public void AllDriveHealth_HealthyAndLeftoverRoot_DoesNotRaiseWarning()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"F:\", IndexRootHealthKind.LeftoverIndex, "leftover index — not maintained"),
        ];

        Assert.Equal("Indexes: all healthy", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uE9F5", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.DoesNotContain(roots, static root => root.NeedsAttention);
        Assert.Contains("Every maintained index included in overall health is healthy", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.Contains("excluded from overall health totals and warnings", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }

    [Fact]
    public void AllDriveHealth_UnmaintainedRows_AreExcludedFromAttentionAndAccelerationDenominators()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.FreshnessUnavailable, "live scan only"),
            new(@"E:\", IndexRootHealthKind.NotIndexed, "not indexed — not maintained"),
            new(@"F:\", IndexRootHealthKind.LeftoverIndex, "leftover index — not maintained"),
        ];

        Assert.Equal("Index: 1 of 2 needs attention", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal(
            "Index: accelerating (1 of 2 needs attention)",
            ContentIndexUiStatus.AllDriveHealthLabel(roots, IndexSearchCoverage.Full));
    }

    [Fact]
    public void AllDriveHealth_AttentionWithInformationalRows_AppendsExclusionSummary()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.BuildRequired, "build required"),
            new(@"D:\", IndexRootHealthKind.NotIndexed, "not indexed"),
        ];

        Assert.Contains("One drive or indexed folder needs attention", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.Contains("excluded from overall health", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }

    [Fact]
    public void AllDriveHealth_UnindexedAndLeftoverRows_ExplainBothExclusions()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.NotIndexed, "not indexed"),
            new(@"D:\", IndexRootHealthKind.LeftoverIndex, "leftover"),
        ];

        string summary = ContentIndexUiStatus.AllDriveHealthSummary(roots);
        Assert.Contains("Unindexed unmaintained drives and leftover index data", summary);
    }

    [Fact]
    public void AllDriveHealth_UnknownFutureState_DegradesWithoutClaimingHealth()
    {
        var unknown = new IndexRootHealthEntry(@"X:\", (IndexRootHealthKind)int.MaxValue, "unknown");
        Assert.Equal("Index: no maintained indexes", ContentIndexUiStatus.AllDriveHealthLabel([unknown]));
        Assert.Contains("No ready local drive currently has a maintained content index",
            ContentIndexUiStatus.AllDriveHealthSummary([unknown]));

        var healthy = new IndexRootHealthEntry(@"C:\", IndexRootHealthKind.Healthy, "healthy");
        Assert.Equal("Index: 1/2 drives healthy", ContentIndexUiStatus.AllDriveHealthLabel([healthy, unknown]));
        Assert.Contains("1 of 2 ready drives", ContentIndexUiStatus.AllDriveHealthSummary([healthy, unknown]));

        var unindexed = new IndexRootHealthEntry(@"D:\", IndexRootHealthKind.NotIndexed, "not indexed");
        Assert.Contains("1 of 2 maintained indexes are healthy",
            ContentIndexUiStatus.AllDriveHealthSummary([healthy, unknown, unindexed]));
    }

    [Fact]
    public void UnregisteredRootHealth_WithoutStoredData_IsSimplyNotIndexed()
    {
        IndexRootHealthEntry root = ContentIndexUiStatus.UnregisteredRootHealth(@"F:\", hasStoredIndex: false);

        Assert.Equal(IndexRootHealthKind.NotIndexed, root.Kind);
        Assert.Equal("not indexed — not maintained; excluded from overall health", root.Status);
        Assert.False(root.NeedsAttention);
        Assert.False(root.HasStoredIndex);
        Assert.False(root.IsIncludedInOverallHealth);
        // An eligible-but-unindexed drive is the one dead end the hover flyout used to leave with no
        // action at all, so it must carry the quick "Add to index" affordance.
        Assert.True(root.CanAddToIndex);
        Assert.Equal(@"F:\", root.AddRoot);
        // Add and Maintain are mutually exclusive: there is no stored index here to adopt.
        Assert.False(root.CanMaintain);
        Assert.False(root.CanDeleteStoredIndex);
        Assert.False(root.CanBuildNow);
    }

    [Fact]
    public void AddToIndexAffordance_IsOffForEveryOtherHealthKind()
    {
        foreach (IndexRootHealthKind kind in Enum.GetValues<IndexRootHealthKind>())
        {
            var entry = new IndexRootHealthEntry(@"D:\", kind, "status");

            Assert.False(entry.CanAddToIndex);
            Assert.Null(entry.AddRoot);
        }
    }

    [Fact]
    public void BuildNowAffordance_IsOptInAndSeparateFromAddToIndex()
    {
        var buildRequired = new IndexRootHealthEntry(
            @"D:\", IndexRootHealthKind.BuildRequired, "registered but never built", BuildRoot: @"D:\");

        Assert.True(buildRequired.CanBuildNow);
        Assert.Equal(@"D:\", buildRequired.BuildRoot);
        // Already registered, so it must not also offer "Add to index".
        Assert.False(buildRequired.CanAddToIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAndBuildAffordances_TreatBlankRootsAsAbsent(string? blank)
    {
        var entry = new IndexRootHealthEntry(
            @"D:\", IndexRootHealthKind.NotIndexed, "status", AddRoot: blank, BuildRoot: blank);

        Assert.False(entry.CanAddToIndex);
        Assert.False(entry.CanBuildNow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnregisteredRootHealth_RejectsBlankRoot(string? root)
        => Assert.ThrowsAny<ArgumentException>(() =>
            ContentIndexUiStatus.UnregisteredRootHealth(root!, hasStoredIndex: false));

    [Theory]
    [InlineData(IndexRootHealthKind.RebuildRequired, "Index: rebuild required")]
    [InlineData(IndexRootHealthKind.BuildRequired, "Index: 1 drive needs build")]
    [InlineData(IndexRootHealthKind.FreshnessUnavailable, "Index: freshness unavailable")]
    public void AllDriveHealth_SingleAttentionState_UsesSpecificLabel(
        IndexRootHealthKind kind,
        string expected)
    {
        var root = new IndexRootHealthEntry(@"D:\", kind, "attention");

        Assert.Equal(expected, ContentIndexUiStatus.AllDriveHealthLabel([root]));
        Assert.True(root.NeedsAttention);
    }

    // ── Status-bar label clamp (the fixed-width slot showed a mid-word ellipsis) ──

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Index: ready", "Index: ready")]
    [InlineData("Index: freshness unavailable", "Index: freshness unavailable")]
    public void TrimStatusLabel_LeavesFittingLabelsUntouched(string? label, string expected)
        => Assert.Equal(expected, ContentIndexUiStatus.TrimStatusLabel(label));

    [Fact]
    public void TrimStatusLabel_ClampsOverlongLabelAtAWordBoundary()
    {
        string trimmed = ContentIndexUiStatus.TrimStatusLabel("Index: 3 freshness checks unavailable");

        Assert.Equal("Index: 3 freshness checks\u2026", trimmed);
        Assert.True(trimmed.Length <= ContentIndexUiStatus.StatusLabelMaxLength);
    }

    [Fact]
    public void TrimStatusLabel_ClampsSingleLongTokenWithoutOverflowing()
    {
        string trimmed = ContentIndexUiStatus.TrimStatusLabel(new string('x', 80));

        Assert.EndsWith("\u2026", trimmed, StringComparison.Ordinal);
        Assert.True(trimmed.Length <= ContentIndexUiStatus.StatusLabelMaxLength);
    }

    [Fact]
    public void StatusWarningGlyph_IsTheCautionTriangleUsedByAttentionStates()
    {
        var root = new IndexRootHealthEntry(@"D:\", IndexRootHealthKind.RebuildRequired, "attention");

        Assert.Equal("\uE7BA", ContentIndexUiStatus.StatusWarningGlyph);
        Assert.Equal(ContentIndexUiStatus.StatusWarningGlyph, ContentIndexUiStatus.AllDriveHealthGlyph([root]));
    }

    [Theory]
    [InlineData(IndexRootHealthKind.RebuildRequired, "Index: 2 rebuilds required")]
    [InlineData(IndexRootHealthKind.BuildRequired, "Index: 2 drives need build")]
    [InlineData(IndexRootHealthKind.FreshnessUnavailable, "Index: 2 freshness checks unavailable")]
    public void AllDriveHealth_AllRootsShareAttentionState_UsesSpecificPluralLabel(
        IndexRootHealthKind kind,
        string expected)
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", kind, "attention"),
            new(@"D:\", kind, "attention"),
        ];

        Assert.Equal(expected, ContentIndexUiStatus.AllDriveHealthLabel(roots));
    }

    [Fact]
    public void AllDriveHealth_JournalProvenChanges_AreHealthyPendingWork()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.ChangesPending, "3 recent changes pending"),
            new(@"D:\", IndexRootHealthKind.Healthy, "up to date"),
        ];

        Assert.Equal("Indexes: all healthy", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uE9F5", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.Contains("journal-proven changes", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.True(roots[0].IsHealthy);
        Assert.False(roots[0].NeedsAttention);
    }

    [Fact]
    public void AllDriveHealth_UnindexedDrives_AreInformationalNotWarnings()
    {
        IndexRootHealthEntry[] roots =
        [
            new(@"C:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"D:\", IndexRootHealthKind.Healthy, "healthy"),
            new(@"E:\", IndexRootHealthKind.ChangesPending, "recent changes pending"),
            new(@"F:\", IndexRootHealthKind.NotIndexed, "not indexed — not maintained"),
        ];

        Assert.Equal("Indexes: all healthy", ContentIndexUiStatus.AllDriveHealthLabel(roots));
        Assert.Equal("\uE9F5", ContentIndexUiStatus.AllDriveHealthGlyph(roots));
        Assert.False(roots[3].NeedsAttention);
        Assert.False(roots[3].HasStoredIndex);
        Assert.False(roots[3].IsIncludedInOverallHealth);
        Assert.Contains("Every maintained index included in overall health is healthy", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.Contains("Unindexed unmaintained drives are informational", ContentIndexUiStatus.AllDriveHealthSummary(roots));
        Assert.Contains("excluded from overall health totals and warnings", ContentIndexUiStatus.AllDriveHealthSummary(roots));
    }
}
