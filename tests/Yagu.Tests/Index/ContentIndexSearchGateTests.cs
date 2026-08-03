using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexSearchGate"/> (plan §5): the per-path pruning brain a live search
/// consults. Drives a real <see cref="AcceleratedQuery"/> (published generation + injected fake journal)
/// and asserts the safety invariants — only fresh nonmembers are pruned; members/dirty/unindexed always
/// scan; errors and B1 discontinuity fall back to scanning every pruned path; large scopes do not hit an
/// arbitrary path-count fallback; and a file that changes after B0 is rescued for a live scan.
/// </summary>
public sealed class ContentIndexSearchGateTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private UsnFileIdentity _idB;

    public ContentIndexSearchGateTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private void PublishGeneration()
    {
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        ulong next = 700;
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out var id)) { id = new UsnFileIdentity(next++, 0); assigned[norm] = id; }
            return new FileIdentity(0x3, id);
        }

        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries")); // member
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest"));        // nonmember
        var gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, scopeId).Publish(gen);
        _idB = assigned[IndexScopeIdentity.NormalizePath(@"C:\r\b.txt")];
    }

    /// <summary>Publishes a generation with one member (a.txt) and two prunable nonmembers (b.txt, c.txt)
    /// so the after-scan-drain re-drain loop (plan §5.4, option (b)) can be exercised across passes.</summary>
    private (UsnFileIdentity IdB, UsnFileIdentity IdC) PublishTwoNonmemberGeneration()
    {
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        ulong next = 900;
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out var id)) { id = new UsnFileIdentity(next++, 0); assigned[norm] = id; }
            return new FileIdentity(0x3, id);
        }

        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries")); // member
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest"));        // nonmember
        builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("also unrelated filler content here"));    // nonmember
        var gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, scopeId).Publish(gen);
        return (assigned[IndexScopeIdentity.NormalizePath(@"C:\r\b.txt")], assigned[IndexScopeIdentity.NormalizePath(@"C:\r\c.txt")]);
    }

    private static ContentIndexFreshnessEvaluator.JournalReader OkReader(params UsnChange[] changes)
        => (path, since) => new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), changes);

    private AcceleratedQuery BeginQuery(ContentIndexFreshnessEvaluator.JournalReader b0Reader)
    {
        var accel = new ContentIndexAccelerator(_paths, journalReader: b0Reader);
        var options = new SearchOptions { Directory = _root, Query = "planner", CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        var result = accel.TryBegin(_root, options, new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 });
        Assert.True(result.CanAccelerate);
        return result.Query!;
    }

    private static string Norm(string p) => IndexScopeIdentity.NormalizePath(p);

    [Fact]
    public void ShouldContentScan_Member_IsScanned_NonmemberIsPruned()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));

        Assert.True(gate.ShouldContentScan(@"C:\r\a.txt", Norm(@"C:\r\a.txt")));   // member → scan
        Assert.False(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")));  // nonmember → prune
        Assert.Equal(1, gate.PrunedCount);
        Assert.False(gate.PruningDisabled);
    }

    [Fact]
    public void ShouldContentScan_UnindexedPath_IsScanned()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        Assert.True(gate.ShouldContentScan(@"C:\r\missing.txt", Norm(@"C:\r\missing.txt")));
        Assert.Equal(0, gate.PrunedCount);
    }

    [Fact]
    public void ClassifyProvenance_MemberIsAccelerated_EverythingElseLiveScanned()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));

        // a.txt is a query member (posting-selected) → index-accelerated candidacy.
        Assert.Equal(IndexProvenanceKind.IndexAccelerated, gate.ClassifyProvenance(Norm(@"C:\r\a.txt")));
        // b.txt is a fresh nonmember (would be pruned) → live-scanned if ever asked.
        Assert.Equal(IndexProvenanceKind.LiveScanned, gate.ClassifyProvenance(Norm(@"C:\r\b.txt")));
        // A path absent from the index → live-scanned.
        Assert.Equal(IndexProvenanceKind.LiveScanned, gate.ClassifyProvenance(Norm(@"C:\r\missing.txt")));
    }

    [Fact]
    public void Constructor_NullQuery_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ContentIndexSearchGate(null!));

    [Fact]
    public void ShouldContentScan_RepeatedNonmember_DoesNotDisablePruning()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));

        Assert.False(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")));
        Assert.False(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")));
        Assert.False(gate.PruningDisabled);
        Assert.Single(gate.Query.ProvisionalAliases); // query session deduplicates the persisted alias id
    }

    [Fact]
    public void ShouldContentScan_ClassificationError_FailsSafeToScanAndDisablesPruning()
    {
        PublishGeneration();
        bool? accelerated = null;
        string? reason = null;
        var gate = new ContentIndexSearchGate(
            BeginQuery(OkReader()),
            (active, why) => (accelerated, reason) = (active, why));

        // A null normalized path makes the underlying classifier throw; the gate must swallow it, scan the
        // path, and disable pruning for the rest of the search (fail safe — a match can never be hidden).
        Assert.True(gate.ShouldContentScan(@"C:\r\b.txt", null!));
        Assert.True(gate.PruningDisabled);
        Assert.False(accelerated);
        Assert.Contains("classification", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPathsToRescan_JournalReaderThrows_RescuesEveryPrunedPath()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")); // prune b

        // An exception replaying the journal at B1 must fail safe → drain every pruned path for live scan.
        var rescan = gate.GetPathsToRescan((path, since) => throw new IOException("journal read blew up"));
        Assert.Contains(@"C:\r\b.txt", rescan);
    }

    [Fact]
    public void GateHelpers_DoNotHideOutOfMemory()
    {
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "planner",
            CaseSensitive = true,
            ExactMatch = false,
            UseContentIndex = true,
        };
        var settings = new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 };
        var throwingPaths = new OomPathProvider(_sandbox);
        Assert.Throws<OutOfMemoryException>(() => ContentIndexSearchGate.TryCreate(
            throwingPaths, _root, options, settings));
        Assert.Throws<OutOfMemoryException>(() => ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(
            throwingPaths, _root, 2, 100));
        Assert.Throws<OutOfMemoryException>(() => ContentIndexSearchGate.IsScopeWarm(
            throwingPaths, _root, 2));

        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt"));
        Assert.Throws<OutOfMemoryException>(() => gate.GetPathsToRescan(
            (_, _) => throw new OutOfMemoryException("journal oom")));
    }

    private sealed class OomPathProvider(string indexRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => throw new OutOfMemoryException("index open oom");
    }

    [Fact]
    public void GetPathsToRescan_NoReaderArg_UsesTheGatesOwnJournalReader()
    {
        PublishGeneration();
        // The gate keeps the B0 reader (OkReader → no changes) from TryBegin; a no-arg B1 call must reuse it.
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")); // prune b

        var rescan = gate.GetPathsToRescan(); // no reader arg → falls back to the gate's stored reader
        Assert.Empty(rescan);                  // quiescent journal → nothing to rescue
    }

    [Fact]
    public void ShouldContentScan_DirtyFile_IsScanned()
    {
        PublishGeneration();
        // b.txt reported dirty at B0 → it must be scanned, not pruned.
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader(new UsnChange(_idB, 0x1))));
        Assert.True(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")));
        Assert.Equal(0, gate.PrunedCount);
    }

    // ── After-scan-drain B1 re-drain loop (plan §5.4, option (b)) ──

    [Fact]
    public void ReconcileB1Pass_QuiescentJournal_RescuesNothingAndStops()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        Assert.False(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt"))); // prune b

        B1RescuePass pass = gate.ReconcileB1Pass(); // reuses the gate's quiescent B0 reader
        Assert.Empty(pass.PathsToScan);            // nothing changed since B0 → nothing to rescue
        Assert.False(pass.MorePassesUseful);        // and no further pass is useful
        Assert.False(gate.PruningDisabled);         // a clean pass does not disable pruning
    }

    [Fact]
    public void ReconcileB1Pass_DirtyPrunedPath_RescuesItThenStops()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader())); // quiescent B0 → b pruned as a nonmember
        Assert.False(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")));

        // A B1 override reader reports b changed since B0 → rescue it; b was the only pruned path so stop.
        B1RescuePass pass = gate.ReconcileB1Pass(OkReader(new UsnChange(_idB, 0x1)));
        Assert.Contains(@"C:\r\b.txt", pass.PathsToScan);
        Assert.False(pass.MorePassesUseful);
    }

    [Fact]
    public void ReconcileB1Pass_ReDrainLoop_CatchesAPrunedFileDirtiedOnALaterPass()
    {
        (UsnFileIdentity idB, UsnFileIdentity idC) = PublishTwoNonmemberGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader())); // quiescent B0 → b and c pruned as nonmembers
        Assert.False(gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")));
        Assert.False(gate.ShouldContentScan(@"C:\r\c.txt", Norm(@"C:\r\c.txt")));
        Assert.Equal(2, gate.PrunedCount);

        // A staged reader: the 1st B1 pass sees only b dirty; a later pass (simulating c being edited DURING
        // the rescue scan) additionally sees c dirty. The re-drain loop must rescue BOTH across passes — the
        // whole point of taking B1 after the scan drains rather than at end of discovery.
        int call = 0;
        ContentIndexFreshnessEvaluator.JournalReader staged = (path, since) =>
        {
            call++;
            UsnChange[] changes = call <= 1
                ? new[] { new UsnChange(idB, 0x1) }
                : new[] { new UsnChange(idB, 0x1), new UsnChange(idC, 0x1) };
            return new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), changes);
        };

        B1RescuePass pass1 = gate.ReconcileB1Pass(staged);
        Assert.Contains(@"C:\r\b.txt", pass1.PathsToScan);
        Assert.DoesNotContain(@"C:\r\c.txt", pass1.PathsToScan);
        Assert.True(pass1.MorePassesUseful); // c is still provisionally pruned → keep draining

        B1RescuePass pass2 = gate.ReconcileB1Pass(staged);
        Assert.Contains(@"C:\r\c.txt", pass2.PathsToScan);
        Assert.False(pass2.MorePassesUseful); // everything pruned has now been rescued

        B1RescuePass pass3 = gate.ReconcileB1Pass(staged);
        Assert.Empty(pass3.PathsToScan); // nothing left provisional
    }

    [Fact]
    public void ReconcileB1Pass_JournalReaderThrows_RescuesEveryPrunedPathAndStops()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")); // prune b

        // An exception replaying the journal at B1 must fail safe → drain every pruned path for a live scan.
        B1RescuePass pass = gate.ReconcileB1Pass((path, since) => throw new IOException("journal read blew up"));
        Assert.Contains(@"C:\r\b.txt", pass.PathsToScan);
        Assert.False(pass.MorePassesUseful);
        Assert.True(gate.PruningDisabled);
    }

    [Fact]
    public void ReconcileB1Pass_AfterPruningDisabled_ReturnsAllRemainingAndStops()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")); // prune b
        // Force pruning off via a classification error (null path), then B1 must drain everything still pruned.
        Assert.True(gate.ShouldContentScan(@"C:\r\z.txt", null!));
        Assert.True(gate.PruningDisabled);

        B1RescuePass pass = gate.ReconcileB1Pass();
        Assert.Contains(@"C:\r\b.txt", pass.PathsToScan);
        Assert.False(pass.MorePassesUseful);
    }

    [Fact]
    public void Gate_DoesNotRetainDuplicatePathDictionaryOrExposeLegacyPathBound()
    {
        System.Reflection.FieldInfo[] fields = typeof(ContentIndexSearchGate).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(Dictionary<long, string>));
        Assert.Null(typeof(ContentIndexSearchGate).GetField(
            "DefaultMaxPrunedTracked",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }

    [Fact]
    public void GetPathsToRescan_Quiescent_ReturnsNothing()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")); // prune b

        var rescan = gate.GetPathsToRescan(OkReader()); // no changes between B0 and B1
        Assert.Empty(rescan);
        Assert.Equal(0, gate.PrunedCount); // B1 releases all per-query alias tracking
    }

    [Fact]
    public void GetPathsToRescan_NonmemberChangedAfterB0_RescuesThatPath()
    {
        PublishGeneration();
        var gate = new ContentIndexSearchGate(BeginQuery(OkReader()));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt")); // prune b

        // b.txt changes after B0 → must be rescued for a live scan (§5.1 #3).
        var rescan = gate.GetPathsToRescan(OkReader(new UsnChange(_idB, 0x1)));
        Assert.Equal(new[] { @"C:\r\b.txt" }, rescan);
    }

    [Fact]
    public void GetPathsToRescan_JournalDiscontinuity_RescuesEveryPrunedPath()
    {
        PublishGeneration();
        bool? accelerated = null;
        string? reason = null;
        var gate = new ContentIndexSearchGate(
            BeginQuery(OkReader()),
            (active, why) => (accelerated, reason) = (active, why));
        gate.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt"));

        var rescan = gate.GetPathsToRescan((path, since) =>
            new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>()));
        Assert.Contains(@"C:\r\b.txt", rescan);
        Assert.True(gate.PruningDisabled);
        Assert.False(accelerated);
        Assert.Contains("live rescan", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreate_PublishedGenerationAndEligibleQuery_ReturnsGate()
    {
        PublishGeneration();
        var options = new SearchOptions { Directory = _root, Query = "planner", CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        bool? accelerated = null;
        string? reason = null;

        var gate = ContentIndexSearchGate.TryCreate(
            _paths,
            _root,
            options,
            new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 },
            retainedGenerations: 2,
            journalReader: OkReader(),
            onAttempt: (active, why) => (accelerated, reason) = (active, why));

        Assert.NotNull(gate);
        Assert.True(accelerated);
        Assert.Equal("index acceleration active", reason);
        Assert.False(gate!.ShouldContentScan(@"C:\r\b.txt", Norm(@"C:\r\b.txt"))); // nonmember pruned → gate is live
    }

    [Fact]
    public void TryCreate_MidSearchFallback_ReportsSecondStatusForSameRoot()
    {
        PublishGeneration();
        var options = new SearchOptions { Directory = _root, Query = "planner", CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        var attempts = new List<(bool Active, string Reason)>();

        var gate = ContentIndexSearchGate.TryCreate(
            _paths,
            _root,
            options,
            new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 },
            journalReader: OkReader(),
            onAttempt: (active, why) => attempts.Add((active, why)));

        Assert.NotNull(gate);
        Assert.True(gate!.ShouldContentScan(@"C:\r\b.txt", null!));
        Assert.Collection(
            attempts,
            first => Assert.True(first.Active),
            fallback => Assert.False(fallback.Active));
    }

    [Fact]
    public void TryCreate_IneligibleWholeWordQuery_ReportsNoRequiredTrigramImmediately()
    {
        PublishGeneration();
        var options = new SearchOptions
        {
            Directory = _root,
            Query = "test",
            CaseSensitive = false,
            ExactMatch = true,
            UseContentIndex = true,
        };
        bool? accelerated = null;
        string? reason = null;

        var gate = ContentIndexSearchGate.TryCreate(
            _paths,
            _root,
            options,
            new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 },
            journalReader: OkReader(),
            onAttempt: (active, why) => (accelerated, reason) = (active, why));

        Assert.Null(gate);
        Assert.False(accelerated);
        Assert.Contains("no required trigram", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_MasterFeatureOff_ReturnsNull()
    {
        PublishGeneration();
        var options = new SearchOptions { Directory = _root, Query = "planner", ExactMatch = false, UseContentIndex = true };

        var gate = ContentIndexSearchGate.TryCreate(_paths, _root, options, new AppSettings { EnableContentIndex = false }, journalReader: OkReader());

        Assert.Null(gate);
    }

    [Fact]
    public void TryCreate_OptInFlagOff_ReturnsNull()
    {
        PublishGeneration();
        var options = new SearchOptions { Directory = _root, Query = "planner", ExactMatch = false, UseContentIndex = false };

        var gate = ContentIndexSearchGate.TryCreate(_paths, _root, options, new AppSettings { EnableContentIndex = true }, journalReader: OkReader());

        Assert.Null(gate);
    }

    [Fact]
    public void TryCreate_NoPublishedGeneration_ReturnsNull()
    {
        // No PublishGeneration() call → nothing to accelerate against.
        var options = new SearchOptions { Directory = _root, Query = "planner", ExactMatch = false, UseContentIndex = true };

        var gate = ContentIndexSearchGate.TryCreate(_paths, _root, options, new AppSettings { EnableContentIndex = true }, journalReader: OkReader());

        Assert.Null(gate);
    }

    [Fact]
    public void TryCreate_BlankRoot_ReturnsNull()
    {
        PublishGeneration();
        var options = new SearchOptions { Directory = _root, Query = "planner", ExactMatch = false, UseContentIndex = true };

        var gate = ContentIndexSearchGate.TryCreate(_paths, "   ", options, new AppSettings { EnableContentIndex = true }, journalReader: OkReader());

        Assert.Null(gate);
    }
}
