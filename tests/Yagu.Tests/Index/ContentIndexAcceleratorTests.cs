using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexAccelerator"/> (plan §5): the single facade that assembles plan →
/// open generation → freshness → session, and returns a classifier or a bypass. Uses a per-test sandbox
/// (§9.2) with a generation published from fake identities and an injected fake journal reader, so every
/// branch (disabled, ineligible, family-gated, no-generation, untrusted-root, member/nonmember/dirty
/// classification) is deterministic without touching the real journal.
/// </summary>
public sealed class ContentIndexAcceleratorTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private UsnFileIdentity _idA;
    private UsnFileIdentity _idB;

    public ContentIndexAcceleratorTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-accel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    /// <summary>Publishes a 2-document generation (a.txt has the query term, b.txt does not) and records identities.</summary>
    private void PublishGeneration(bool produceV3 = false)
    {
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        ulong next = 900;
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out var id)) { id = new UsnFileIdentity(next++, 0); assigned[norm] = id; }
            return new FileIdentity(0x5, id);
        }

        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest"));
        var gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);

        var store = new ContentIndexStore(_paths, scopeId) { ProduceV3QueryStructures = produceV3 };
        store.Publish(gen);

        _idA = assigned[IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")];
        _idB = assigned[IndexScopeIdentity.NormalizePath(@"C:\r\b.txt")];
    }

    // Acceleration-mechanics tests use a 100% candidate budget so the selectivity guard never trips on the
    // tiny 2-document fixtures; the guard itself is covered by dedicated selectivity tests.
    private static AppSettings EnabledSettings() => new() { EnableContentIndex = true, IndexMaxCandidatePercent = 100 };

    private SearchOptions LiteralQuery(bool useContentIndex = true, bool caseSensitive = true) => new()
    {
        Directory = _root,
        Query = "planner",
        CaseSensitive = caseSensitive,
        ExactMatch = false,
        UseContentIndex = useContentIndex,
    };

    private SearchOptions FamilyProbe(bool exactMatch = false, bool useRegex = false, bool multiline = false) => new()
    {
        Directory = _root,
        Query = "planner",
        ExactMatch = exactMatch,
        UseRegex = useRegex,
        Multiline = multiline,
    };

    private static ContentIndexFreshnessEvaluator.JournalReader OkReader(params UsnChange[] changes)
        => (path, since) => new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), changes);

    // ── Bypass branches ──

    [Fact]
    public void TryBegin_MasterDisabled_Bypasses()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var result = accel.TryBegin(_root, LiteralQuery(), new AppSettings { EnableContentIndex = false });
        Assert.False(result.CanAccelerate);
        Assert.Contains("disabled", result.BypassReason);
    }

    [Fact]
    public void TryBegin_PerSearchToggleOff_Bypasses()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var options = LiteralQuery(useContentIndex: false);
        var result = accel.TryBegin(_root, options, EnabledSettings());
        Assert.False(result.CanAccelerate);
    }

    [Fact]
    public void TryBegin_CaseInsensitiveAsciiLiteral_Accelerates()
    {
        // Case-insensitive acceleration is supported for a pure-ASCII literal (the planner folds each
        // trigram to its ASCII case variants). "planner" has no k/s so every trigram survives.
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var options = LiteralQuery(caseSensitive: false);
        var result = accel.TryBegin(_root, options, EnabledSettings());
        Assert.True(result.CanAccelerate);
    }

    [Fact]
    public void TryBegin_FamilyDisabled_Bypasses()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var settings = EnabledSettings();
        settings.IndexAccelerateLiterals = false; // literal family gated off
        var result = accel.TryBegin(_root, LiteralQuery(), settings);
        Assert.False(result.CanAccelerate);
        Assert.Contains("family", result.BypassReason);
    }

    [Fact]
    public void TryBegin_NoGeneration_Bypasses()
    {
        // Nothing published.
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings());
        Assert.False(result.CanAccelerate);
        Assert.Contains("no trusted index", result.BypassReason);
    }

    [Fact]
    public void TryBegin_FreshnessDiscontinuity_Bypasses()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths,
            journalReader: (path, since) => new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>()));
        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings());
        Assert.False(result.CanAccelerate);
        Assert.Contains("GapDetected", result.BypassReason);
    }

    // ── Selectivity guard (plan §6.1 IndexMaxCandidatePercent — performance-only bypass) ──

    [Fact]
    public void TryBegin_CandidatesExceedSelectivityBudget_BypassesToLiveScan()
    {
        PublishGeneration(); // 2 docs; "planner" selects 1 → 50% candidates.
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        // A 25% budget: 50% > 25% → not selective enough → bypass (results are identical to a live scan).
        var settings = new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 25 };
        var result = accel.TryBegin(_root, LiteralQuery(), settings);

        Assert.False(result.CanAccelerate);
        Assert.Contains("selective", result.BypassReason);
    }

    [Fact]
    public void TryBegin_GenerousSelectivityBudget_Accelerates()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        // A generous budget admits the same 50%-selective query.
        var settings = new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 };
        Assert.True(accel.TryBegin(_root, LiteralQuery(), settings).CanAccelerate);
    }

    // ── Happy path + classification ──

    [Fact]
    public void TryBegin_QuiescentIndex_ClassifiesMemberAndProvisionallyPrunesNonmember()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings());
        Assert.True(result.CanAccelerate);
        var query = result.Query!;

        // a.txt contains "planner" → fresh posting member (live-verified, never pruned).
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(
            query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));

        // b.txt has no required trigram → fresh nonmember → provisionally pruned.
        var decision = query.Route(IndexScopeIdentity.NormalizePath(@"C:\r\b.txt"));
        Assert.IsType<PathDecision.ProvisionalPrune>(decision);
        Assert.Single(query.ProvisionalAliases);

        // An unindexed path always live-scans.
        Assert.IsType<IndexPathClassification.Unindexed>(
            query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\missing.txt")));
    }

    [Fact]
    public void TryBegin_V3ReaderEnabled_AcceleratesFromV3Postings_IdenticalClassification()
    {
        // With IndexUseV3QueryReader on and the generation upgraded (v3 sidecars present), the candidate set
        // is produced by the memory-mapped ContentIndexV3Reader instead of the in-process posting index.
        // The result must be identical to the in-process happy path above.
        PublishGeneration(produceV3: true);
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var settings = EnabledSettings();
        settings.IndexUseV3QueryReader = true;

        var result = accel.TryBegin(_root, LiteralQuery(), settings);
        Assert.True(result.CanAccelerate);
        var query = result.Query!;

        Assert.IsType<IndexPathClassification.FreshIndexedMember>(
            query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
        Assert.IsType<PathDecision.ProvisionalPrune>(
            query.Route(IndexScopeIdentity.NormalizePath(@"C:\r\b.txt")));
    }

    [Fact]
    public void TryBegin_V3ReaderEnabledButGenerationNotUpgraded_FallsBackToInProcess()
    {
        // A generation WITHOUT the v3 sidecars (produceV3:false) must transparently fall back to the
        // in-process evaluation — acceleration still works and classifies identically.
        PublishGeneration(produceV3: false);
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var settings = EnabledSettings();
        settings.IndexUseV3QueryReader = true;

        var result = accel.TryBegin(_root, LiteralQuery(), settings);
        Assert.True(result.CanAccelerate);
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(
            result.Query!.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
    }

    [Fact]
    public void TryBegin_FileDirtiedSinceBuild_ClassifiesDirtyByUsn()
    {
        PublishGeneration();
        // The journal reports a change to a.txt's content since the build checkpoint.
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader(new UsnChange(_idA, 0x1)));

        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings());
        Assert.True(result.CanAccelerate);
        var query = result.Query!;

        // a.txt is dirty → live-scanned even though it is a posting member (dirty is checked first).
        Assert.IsType<IndexPathClassification.DirtyByUsn>(
            query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(
            query.Route(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
    }

    [Fact]
    public void IsFamilyAccelerationEnabled_HonorsEachFamilyGate()
    {
        var settings = new AppSettings
        {
            IndexAccelerateLiterals = false,
            IndexAccelerateWholeWord = false,
            IndexAccelerateRegex = false,
            IndexAccelerateMultiline = false,
        };
        Assert.False(ContentIndexAccelerator.IsFamilyAccelerationEnabled(FamilyProbe(exactMatch: false), settings));
        Assert.False(ContentIndexAccelerator.IsFamilyAccelerationEnabled(FamilyProbe(exactMatch: true), settings));
        Assert.False(ContentIndexAccelerator.IsFamilyAccelerationEnabled(FamilyProbe(useRegex: true), settings));
        Assert.False(ContentIndexAccelerator.IsFamilyAccelerationEnabled(FamilyProbe(multiline: true, exactMatch: false), settings));

        settings.IndexAccelerateLiterals = true;
        Assert.True(ContentIndexAccelerator.IsFamilyAccelerationEnabled(FamilyProbe(exactMatch: false), settings));
    }

    // ── Two-barrier lifecycle (B0 → B1 reconciliation, invariant §5.1 #3) ──

    [Fact]
    public void FinalizeAtB1_QuiescentBetweenBarriers_KeepsPrunes()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var query = accel.TryBegin(_root, LiteralQuery(), EnabledSettings()).Query!;

        // b.txt is provisionally pruned at B0.
        query.Route(IndexScopeIdentity.NormalizePath(@"C:\r\b.txt"));
        Assert.Single(query.ProvisionalAliases);

        // No changes between B0 and B1 → the prune stands; nothing must be live-scanned and b.txt
        // remains pruned (a final prune, not rescued).
        var mustLiveScan = query.FinalizeAtB1(OkReader());
        Assert.Empty(mustLiveScan);
        Assert.Single(query.ProvisionalAliases);
    }

    [Fact]
    public void FinalizeAtB1_NonmemberGainsMatchAfterB0_IsLiveScannedNotHidden()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader()); // B0: quiescent
        var query = accel.TryBegin(_root, LiteralQuery(), EnabledSettings()).Query!;

        // b.txt is provisionally pruned at B0…
        query.Route(IndexScopeIdentity.NormalizePath(@"C:\r\b.txt"));
        Assert.Single(query.ProvisionalAliases);

        // …but its content changes after B0 (it might now contain the term) → must be live-scanned (§5.1 #3).
        var mustLiveScan = query.FinalizeAtB1(OkReader(new UsnChange(_idB, 0x1)));
        Assert.Single(mustLiveScan);
        Assert.Empty(query.ProvisionalAliases);
    }

    [Fact]
    public void FinalizeAtB1_JournalDiscontinuityAfterB0_LiveScansEveryProvisional()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var query = accel.TryBegin(_root, LiteralQuery(), EnabledSettings()).Query!;

        query.Route(IndexScopeIdentity.NormalizePath(@"C:\r\b.txt"));
        Assert.Single(query.ProvisionalAliases);

        // A gap between B0 and B1 makes reconciliation uncertain → live-scan every provisional alias.
        var mustLiveScan = query.FinalizeAtB1((path, since) =>
            new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>()));
        Assert.Single(mustLiveScan);
        Assert.Empty(query.ProvisionalAliases);
    }

    // ── Phase 3: layered (base + delta segment) acceleration ──

    [Fact]
    public void TryBegin_WithDeltaSegment_UsesLayeredSession_NewestFirst()
    {
        PublishGeneration(); // base: a.txt has "planner", b.txt does not
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId);

        // Segment: replace a.txt with non-matching content, add c.txt that DOES contain "planner".
        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        seg.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest now"));
        seg.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("the planner is over here now"));
        store.PublishSegment(seg.Build(scopeId, "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow));

        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings());

        Assert.True(result.CanAccelerate);
        Assert.True(result.Query!.IsLayered);
        // Newest-first: the segment's replaced a.txt (no "planner") shadows the base → nonmember (prunable).
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(
            result.Query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
        // The new c.txt (only in the segment) is the member.
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(
            result.Query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\c.txt")));
    }

    [Fact]
    public void TryBegin_LayeredV3ReaderEnabled_AcceleratesFromPerLayerV3_IdenticalClassification()
    {
        // Base AND segment upgraded (v3 sidecars present) + IndexUseV3QueryReader on → the layered candidate
        // sets come from the memory-mapped format-v3 postings per layer; classification must be identical to
        // the in-process layered path above.
        PublishGeneration(produceV3: true); // base v3
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId) { ProduceV3QueryStructures = true };

        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        seg.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest now"));
        seg.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("the planner is over here now"));
        store.PublishSegment(seg.Build(scopeId, "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow));

        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var settings = EnabledSettings();
        settings.IndexUseV3QueryReader = true;
        var result = accel.TryBegin(_root, LiteralQuery(), settings);

        Assert.True(result.CanAccelerate);
        Assert.True(result.Query!.IsLayered);
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(
            result.Query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(
            result.Query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\c.txt")));
    }

    [Fact]
    public void TryBegin_LayeredV3ReaderEnabledButSegmentNotUpgraded_FallsBackToInProcessLayered()
    {
        // Base has v3 but the segment does NOT (produced without v3) → all-or-nothing fall-through to the
        // in-process layered evaluation; acceleration + classification are unchanged.
        PublishGeneration(produceV3: true);
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId); // ProduceV3QueryStructures = false for the segment

        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        seg.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest now"));
        seg.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("the planner is over here now"));
        store.PublishSegment(seg.Build(scopeId, "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow));

        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());
        var settings = EnabledSettings();
        settings.IndexUseV3QueryReader = true;
        var result = accel.TryBegin(_root, LiteralQuery(), settings);

        Assert.True(result.CanAccelerate);
        Assert.True(result.Query!.IsLayered);
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(
            result.Query.Classify(IndexScopeIdentity.NormalizePath(@"C:\r\c.txt")));
    }

    [Fact]
    public void TryBegin_SegmentFreshnessNotContinuous_FallsBackToBaseOnly()
    {
        PublishGeneration();
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var store = new ContentIndexStore(_paths, scopeId);
        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        seg.AddChangedDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("the planner is over here now"));
        store.PublishSegment(seg.Build(scopeId, "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow));

        // A reader that reports a gap for the segment's root → segment freshness is not continuous → base-only.
        var accel = new ContentIndexAccelerator(_paths, journalReader:
            (path, since) => new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>()));
        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings());

        // Base freshness also non-continuous here → the base itself bypasses (safe): no acceleration.
        Assert.False(result.CanAccelerate);
    }
}
