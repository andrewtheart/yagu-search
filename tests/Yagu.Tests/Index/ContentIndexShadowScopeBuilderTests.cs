using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexShadowScopeBuilder"/> (plan §6 Stage 3, slice 2d-3): assembling the
/// worker <see cref="IndexQueryOpenRequest"/> for a published v3 scope <b>without deserializing the index</b>.
/// The builder must report the scope's base + segment directories (from the cheap pointer-slot read), encode
/// the planned trigram query as RPN that round-trips to the same candidate set, resolve each layer's B0 dirty
/// content-ids from a fake journal, and fail closed (return null) for an ineligible query, an absent index,
/// or an unprovable freshness verdict. Uses a per-test sandbox.
/// </summary>
public sealed class ContentIndexShadowScopeBuilderTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexShadowScopeBuilderTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-shadow-scope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexStore NewV3Store()
    {
        var store = new ContentIndexStore(_paths, _scopeId, retainedGenerations: 2) { ProduceV3QueryStructures = true };
        return store;
    }

    private ContentIndexGeneration BuildBase()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        return builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private ContentIndexDeltaSegment BuildSegment(
        string root = @"C:\r",
        UsnCheckpoint? checkpoint = null,
        params (string Path, string Content)[] documents)
    {
        var b = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        if (documents.Length == 0)
            documents = new[] { (@"C:\r\d.txt", "the planner is over here now") };
        foreach ((string path, string content) in documents)
            b.AddChangedDocument(path, Encoding.UTF8.GetBytes(content));
        return b.Build(_scopeId, "vol", root, checkpoint ?? new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow);
    }

    private static SearchOptions QueryOptions(string term)
        => new() { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };

    /// <summary>A journal reader that reports a continuous read with no changes (a fresh, unchanged scope).</summary>
    private static ContentIndexFreshnessEvaluator.JournalReader NoChangeReader()
        => (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());

    private static TrigramExpression Plan(string term)
        => ((TrigramPlan.Eligible)TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(QueryOptions(term)))).Query;

    // ── Happy path ──

    [Fact]
    public void TryBuild_EligibleQuery_FreshBaseOnly_BuildsRequestWithDirsRpnAndEmptyDirty()
    {
        var store = NewV3Store();
        ContentIndexGeneration gen = BuildBase();
        store.Publish(gen);

        Assert.True(store.TryGetCurrentLayerDirectories(out string? expectedBaseDir, out _));

        IndexQueryOpenRequest? request =
            ContentIndexShadowScopeBuilder.TryBuild(
                store, QueryOptions("planner"), sessionId: 7, NoChangeReader(), out string reason,
                workerParallelism: 999);

        Assert.NotNull(request);
        Assert.Equal("", reason);
        Assert.Equal(7, request!.SessionId);
        Assert.Equal(IndexWorkerParallelism.Maximum, request.Parallelism);
        Assert.Equal(expectedBaseDir, request.BaseDir);
        Assert.Empty(request.SegmentDirs);

        // The RPN the worker will self-evaluate candidates from must round-trip to the SAME candidate set as
        // the planned query over the base postings (semantics preserved end-to-end, no host candidates sent).
        Assert.Empty(request.BaseCandidatesBase64);
        TrigramExpression decoded = TrigramQueryRpn.Decode(Convert.FromBase64String(request.QueryRpnBase64!));
        Assert.True(gen.Postings.EvaluateSet(Plan("planner")).SetEquals(gen.Postings.EvaluateSet(decoded)));

        // No journal changes → no dirty content ids for the base.
        Assert.Equal("", request.BaseDirtyBase64);
    }

    [Fact]
    public void TryBuild_JournalReportsChange_EncodesThatLayersDirtyContentId()
    {
        var store = NewV3Store();
        ContentIndexGeneration gen = BuildBase();
        store.Publish(gen);

        // Dirty exactly a.txt by reporting a change for its captured durable identity.
        UsnFileIdentity aId = IndexTestIdentities.Capture(@"C:\r\a.txt")!.Value.FileId;
        ContentIndexFreshnessEvaluator.JournalReader reader =
            (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, new[] { new UsnChange(aId, 0x1) });

        IndexQueryOpenRequest? request =
            ContentIndexShadowScopeBuilder.TryBuild(store, QueryOptions("planner"), sessionId: 1, reader, out _);

        Assert.NotNull(request);
        Assert.True(gen.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt"), out _, out long aContentId));
        int[] dirty = IndexWorkerProtocol.DecodeCandidates(request!.BaseDirtyBase64);
        Assert.Equal(new[] { (int)aContentId }, dirty);
    }

    [Fact]
    public void TryBuild_WithSegment_IncludesSegmentDirAndPerLayerDirty()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());
        store.PublishSegment(BuildSegment());

        Assert.True(store.TryGetCurrentLayerDirectories(out _, out IReadOnlyList<string> expectedSegmentDirs));

        IndexQueryOpenRequest? request =
            ContentIndexShadowScopeBuilder.TryBuild(store, QueryOptions("planner"), sessionId: 3, NoChangeReader(), out _);

        Assert.NotNull(request);
        Assert.Equal(expectedSegmentDirs, request!.SegmentDirs);
        Assert.Single(request.SegmentDirtiesBase64);          // one dirty set, 1:1 with the segment dir
        Assert.Equal("", request.SegmentDirtiesBase64[0]);    // no changes → empty
        Assert.Empty(request.SegmentCandidatesBase64);        // worker self-evaluates from the RPN
    }

    [Fact]
    public void TryBuild_WithSegment_ReplaysSharedIntervalOnceFromNewestCheckpoint()
    {
        var store = NewV3Store();
        store.Publish(BuildBase()); // checkpoint 1/100
        store.PublishSegment(BuildSegment()); // checkpoint 1/200
        var seen = new List<UsnCheckpoint>();
        ContentIndexFreshnessEvaluator.JournalReader reader = (root, since) =>
        {
            seen.Add(since);
            return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
        };

        Assert.NotNull(ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 4, reader, out _));

        Assert.Equal(new[] { new UsnCheckpoint(1, 200) }, seen);
    }

    [Fact]
    public void TryBuild_MultipleSegments_MapsOneSharedChangeIntoEachLayerInPointerOrder()
    {
        var store = NewV3Store();
        ContentIndexGeneration gen = BuildBase();
        store.Publish(gen);
        store.PublishSegment(BuildSegment(
            documents: new[]
            {
                (@"C:\r\d.txt", "the planner changed d"),
                (@"C:\r\a.txt", "the planner changed a"),
            }));
        store.PublishSegment(BuildSegment(
            checkpoint: new UsnCheckpoint(1, 300),
            documents: new[] { (@"C:\r\e.txt", "the planner changed e") }));

        UsnFileIdentity aId = IndexTestIdentities.Capture(@"C:\r\a.txt")!.Value.FileId;
        int calls = 0;
        ContentIndexFreshnessEvaluator.JournalReader reader = (root, since) =>
        {
            calls++;
            Assert.Equal(new UsnCheckpoint(1, 300), since);
            return new UsnReadResult(UsnReadStatus.Ok, since, new[] { new UsnChange(aId, 0x1) });
        };

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 5, reader, out _);

        Assert.NotNull(request);
        Assert.Equal(1, calls);
        Assert.Equal(2, request!.SegmentDirs.Length);
        Assert.Equal(2, request.SegmentDirtiesBase64.Length);
        Assert.Equal(new[] { 0 }, IndexWorkerProtocol.DecodeCandidates(request.BaseDirtyBase64));
        Assert.Equal(new[] { 1 }, IndexWorkerProtocol.DecodeCandidates(request.SegmentDirtiesBase64[0]));
        Assert.Empty(IndexWorkerProtocol.DecodeCandidates(request.SegmentDirtiesBase64[1]));
    }

    [Fact]
    public void TryBuild_BaseOnly_ReadsOnceFromBaseCheckpoint()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());
        var seen = new List<UsnCheckpoint>();

        Assert.NotNull(ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 6,
            (root, since) =>
            {
                seen.Add(since);
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            }, out _));

        Assert.Equal(new[] { new UsnCheckpoint(1, 100) }, seen);
    }

    [Fact]
    public void TryBuild_MultipleSegments_LargeSharedChangeList_IsCollectedOnce()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());
        store.PublishSegment(BuildSegment());
        store.PublishSegment(BuildSegment(
            checkpoint: new UsnCheckpoint(1, 300),
            documents: new[] { (@"C:\r\e.txt", "the planner changed e") }));
        var changes = Enumerable.Range(0, 50_000)
            .Select(i => new UsnChange(new UsnFileIdentity((ulong)(1_000_000 + i), 0), 0x1))
            .ToArray();
        int calls = 0;

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 11,
            (root, since) =>
            {
                calls++;
                return new UsnReadResult(UsnReadStatus.Ok, since, changes);
            }, out _);

        Assert.NotNull(request);
        Assert.Equal(1, calls);
        Assert.Equal(2, request!.SegmentDirtiesBase64.Length);
    }

    [Fact]
    public void TryBuild_UnreadableSegment_FailsBeforeJournalRead()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());
        store.PublishSegment(BuildSegment());
        Assert.True(store.TryGetCurrentLayerDirectories(out _, out IReadOnlyList<string> segmentDirs));
        File.WriteAllBytes(Path.Combine(segmentDirs[0], "fileids.bin"), new byte[] { 1, 2, 3 });
        int calls = 0;

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 7,
            (root, since) =>
            {
                calls++;
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            }, out string reason);

        Assert.Null(request);
        Assert.Equal(0, calls);
        Assert.Equal("layer freshness inputs unreadable", reason);
    }

    [Fact]
    public void TryBuild_LayerRootMismatch_FailsBeforeJournalRead()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());
        store.PublishSegment(BuildSegment(root: @"C:\other"));
        int calls = 0;

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 8,
            (root, since) =>
            {
                calls++;
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            }, out string reason);

        Assert.Null(request);
        Assert.Equal(0, calls);
        Assert.Contains("disagree", reason);
    }

    [Fact]
    public void TryBuild_IdentitylessSegmentWithUnknownVolume_RemainsSafelyQueryable()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy); // no identity provider
        builder.AddChangedDocument(@"C:\r\identity-unavailable.txt", Encoding.UTF8.GetBytes("planner text"));
        ContentIndexDeltaSegment segment = builder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 200),
            DateTimeOffset.UtcNow);
        Assert.Equal(0UL, segment.Added.Manifest.VolumeSerialNumber);
        Assert.Equal(0, segment.Added.BuildFileIdMap().Count);
        store.PublishSegment(segment);

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store,
            QueryOptions("planner"),
            sessionId: 9,
            NoChangeReader(),
            out string reason);

        Assert.NotNull(request);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryBuild_SegmentWithConflictingKnownVolume_FailsBeforeJournalRead()
    {
        var store = NewV3Store();
        ContentIndexGeneration baseGeneration = BuildBase();
        store.Publish(baseGeneration);
        ulong conflictingSerial = baseGeneration.Manifest.VolumeSerialNumber + 1;
        var builder = new ContentIndexDeltaSegmentBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(conflictingSerial, new UsnFileIdentity(999, 0)));
        builder.AddChangedDocument(@"C:\r\wrong-volume.txt", Encoding.UTF8.GetBytes("planner text"));
        store.PublishSegment(builder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 200),
            DateTimeOffset.UtcNow));
        int calls = 0;

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store,
            QueryOptions("planner"),
            sessionId: 10,
            (_, since) =>
            {
                calls++;
                return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
            },
            out string reason);

        Assert.Null(request);
        Assert.Equal(0, calls);
        Assert.Contains("known volume", reason, StringComparison.Ordinal);
    }

    // ── Fail-closed (return null → live-scan) ──

    [Fact]
    public void TryBuild_IneligibleQuery_ReturnsNullWithReason()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());

        IndexQueryOpenRequest? request =
            ContentIndexShadowScopeBuilder.TryBuild(store, QueryOptions(""), sessionId: 1, NoChangeReader(), out string reason);

        Assert.Null(request);
        Assert.Equal(TrigramQueryPlanner.ReasonEmptyQuery, reason);
    }

    [Fact]
    public void TryBuild_NoTrustedIndex_ReturnsNull()
    {
        var store = NewV3Store(); // nothing published

        IndexQueryOpenRequest? request =
            ContentIndexShadowScopeBuilder.TryBuild(store, QueryOptions("planner"), sessionId: 1, NoChangeReader(), out string reason);

        Assert.Null(request);
        Assert.Equal("no trusted index", reason);
    }

    [Theory]
    [InlineData(UsnReadStatus.JournalIdChanged)]
    [InlineData(UsnReadStatus.GapDetected)]
    [InlineData(UsnReadStatus.CheckpointAhead)]
    [InlineData(UsnReadStatus.Incomplete)]
    [InlineData(UsnReadStatus.Unavailable)]
    [InlineData(UsnReadStatus.UnknownRecordVersion)]
    [InlineData(UsnReadStatus.Error)]
    public void TryBuild_NonContinuousFreshness_ReturnsNullWithSpecificStatus(UsnReadStatus status)
    {
        var store = NewV3Store();
        store.Publish(BuildBase());

        // A journal reset, gap, or configured catch-up-cap stop means freshness cannot be proven.
        ContentIndexFreshnessEvaluator.JournalReader reader =
            (root, since) => new UsnReadResult(status, since, Array.Empty<UsnChange>());

        IndexQueryOpenRequest? request =
            ContentIndexShadowScopeBuilder.TryBuild(store, QueryOptions("planner"), sessionId: 1, reader, out string reason);

        Assert.Null(request);
        Assert.StartsWith("layer not fresh", reason);
        Assert.Contains($"({status})", reason);
    }

    [Fact]
    public void TryBuild_JournalReaderThrows_FailsClosed()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());

        IndexQueryOpenRequest? request = ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 9,
            (root, since) => throw new IOException("injected journal failure"), out string reason);

        Assert.Null(request);
        Assert.Contains("injected journal failure", reason);
    }

    [Fact]
    public void TryBuild_JournalReaderOutOfMemory_Propagates()
    {
        var store = NewV3Store();
        store.Publish(BuildBase());

        Assert.Throws<OutOfMemoryException>(() => ContentIndexShadowScopeBuilder.TryBuild(
            store, QueryOptions("planner"), sessionId: 10,
            (root, since) => throw new OutOfMemoryException("injected"), out _));
    }

    // ── TryCreateShadowScan (open + spool ownership) ──

    [Fact]
    public void TryCreateShadowScan_WhenScopeNotBuildable_ReturnsNull_AndLeavesNoSpool()
    {
        var store = NewV3Store(); // nothing published → TryBuild returns null before the worker is touched
        string spoolDir = Path.Combine(_sandbox, "spool");
        using var client = new IndexWorkerClient(workerPathOverride: Path.Combine(_sandbox, "no-worker.exe"));

        IContentIndexShadowScan? scan = ContentIndexShadowScopeBuilder.TryCreateShadowScan(
            client, store, QueryOptions("planner"), sessionId: 1, NoChangeReader(), spoolDir);

        Assert.Null(scan);
        Assert.False(Directory.Exists(spoolDir) && Directory.GetFiles(spoolDir, "prune-spool-*.spool").Length > 0);
    }

    [Fact]
    public async Task TryCreateShadowScan_RealWorker_OpensOffersCompletes_AndDeletesSpool()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return; // self-gate: the bundled worker isn't built

        var store = NewV3Store();
        store.Publish(BuildBase());
        string spoolDir = Path.Combine(_sandbox, "spool");
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);

        IContentIndexShadowScan? scan = ContentIndexShadowScopeBuilder.TryCreateShadowScan(
            client, store, QueryOptions("planner"), sessionId: 5, NoChangeReader(), spoolDir);

        Assert.NotNull(scan);
        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "z.txt" })
            await scan!.OfferAsync(IndexScopeIdentity.NormalizePath(@"C:\r\" + file), CancellationToken.None);
        await scan!.CompleteAsync(CancellationToken.None);

        // Shadow never replays the spool → the per-search spool file is deleted on completion (no leak).
        Assert.False(Directory.Exists(spoolDir) && Directory.GetFiles(spoolDir, "prune-spool-*.spool").Length > 0);
    }

    // ── TryCreatePruningScan (Stage-4: open + survivor forwarding + B1 rescue + spool ownership) ──

    [Fact]
    public void TryCreatePruningScan_WhenScopeNotBuildable_ReturnsNull_AndLeavesNoSpool()
    {
        var store = NewV3Store(); // nothing published → TryBuild returns null before the worker is touched
        string spoolDir = Path.Combine(_sandbox, "pspool");
        using var client = new IndexWorkerClient(workerPathOverride: Path.Combine(_sandbox, "no-worker.exe"));

        IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
            client, store, QueryOptions("planner"), sessionId: 1, NoChangeReader(), spoolDir,
            (_, _) => ValueTask.CompletedTask);

        Assert.Null(scan);
        Assert.False(Directory.Exists(spoolDir) && Directory.GetFiles(spoolDir, "prune-spool-*.spool").Length > 0);
    }

    [Fact]
    public void TryCreatePruningScan_IneligibleStructuredRegex_ReportsExactBypassReason()
    {
        var store = NewV3Store();
        string spoolDir = Path.Combine(_sandbox, "pspool-ineligible");
        using var client = new IndexWorkerClient(workerPathOverride: Path.Combine(_sandbox, "no-worker.exe"));
        var options = new SearchOptions
        {
            Directory = _root,
            Query = @"\d{3}-\d{3}-\d{4}",
            UseRegex = true,
            CaseSensitive = false,
            ExactMatch = false,
            UseContentIndex = true,
        };
        var attempts = new List<(bool Active, string Reason)>();

        IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
            client, store, options, sessionId: 2, NoChangeReader(), spoolDir,
            (_, _) => ValueTask.CompletedTask,
            onAttempt: (active, reason) => attempts.Add((active, reason)));

        Assert.Null(scan);
        var attempt = Assert.Single(attempts);
        Assert.False(attempt.Active);
        Assert.Contains("no required trigram", attempt.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(spoolDir) && Directory.GetFiles(spoolDir, "prune-spool-*.spool").Length > 0);
    }

    [Fact]
    public async Task TryCreatePruningScan_RealWorker_ForwardsSurvivors_PrunesNonmember_QuiescentB1KeepsPrune()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return; // self-gate

        var store = NewV3Store();
        store.Publish(BuildBase());
        string spoolDir = Path.Combine(_sandbox, "pspool");
        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);

        IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
            client, store, QueryOptions("planner"), sessionId: 5, NoChangeReader(), spoolDir,
            (path, _) => { survivors.Enqueue(path); return ValueTask.CompletedTask; },
            workerParallelism: 4);
        Assert.NotNull(scan);

        // For "planner": a.txt + c.txt are members, b.txt is a nonmember, z.txt is unindexed.
        string[] files = { "a.txt", "b.txt", "c.txt", "z.txt" };
        foreach (string file in files)
            await scan!.OfferAsync(@"C:\r\" + file, IndexScopeIdentity.NormalizePath(@"C:\r\" + file), CancellationToken.None);
        await scan!.CompleteOfferingAsync();

        PruningScanResult result = await scan.ReconcileAtB1Async(CancellationToken.None);

        // Survivors are forwarded as the ORIGINAL OS path (so result rows show the real path): the two
        // members + the unindexed path; b.txt (a nonmember) was pruned.
        var expectedSurvivors = new[] { @"C:\r\a.txt", @"C:\r\c.txt", @"C:\r\z.txt" };
        Assert.Equal(expectedSurvivors.OrderBy(p => p, StringComparer.Ordinal), survivors.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(1, result.GrossPruned);         // b.txt
        Assert.True(result.Accelerated);
        Assert.Empty(result.RescuePaths);            // nothing changed since build → b.txt stays pruned
        Assert.Equal(1, result.NetPruned);
        Assert.DoesNotContain(@"C:\r\b.txt", survivors);
        // The per-search spool was completed (deleted) — no leak.
        Assert.False(Directory.GetFiles(spoolDir, "prune-spool-*.spool").Length > 0);
    }

    [Fact]
    public async Task TryCreatePruningScan_RealWorker_RescuesAPrunedPathThatChangesDuringTheSearch()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return; // self-gate

        var store = NewV3Store();
        ContentIndexGeneration gen = BuildBase();
        store.Publish(gen);
        string spoolDir = Path.Combine(_sandbox, "pspool");
        var survivors = new ConcurrentQueue<string>();
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);

        // A staged reader: clean at barrier B0 (open) → b.txt is a fresh prunable nonmember; then reports b.txt
        // changed at barrier B1 (reconcile) → it must be rescued (scanned after all).
        UsnFileIdentity bId = IndexTestIdentities.Capture(@"C:\r\b.txt")!.Value.FileId;
        int calls = 0;
        ContentIndexFreshnessEvaluator.JournalReader stagedReader = (root, since) =>
        {
            calls++;
            return calls <= 1
                ? new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>())
                : new UsnReadResult(UsnReadStatus.Ok, since, new[] { new UsnChange(bId, 0x1) });
        };

        IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
            client, store, QueryOptions("planner"), sessionId: 9, stagedReader, spoolDir,
            (path, _) => { survivors.Enqueue(path); return ValueTask.CompletedTask; });
        Assert.NotNull(scan);

        foreach (string file in new[] { "a.txt", "b.txt", "c.txt" })
            await scan!.OfferAsync(@"C:\r\" + file, IndexScopeIdentity.NormalizePath(@"C:\r\" + file), CancellationToken.None);
        await scan!.CompleteOfferingAsync();

        PruningScanResult result = await scan.ReconcileAtB1Async(CancellationToken.None);

        // b.txt was provisionally pruned at B0 but changed during the search → rescued at B1 (net pruning 0).
        // The rescue path is the NORMALIZED form (the worker's provisional key), like the in-process B1 rescue.
        Assert.Equal(1, result.GrossPruned);
        Assert.Equal(new[] { IndexScopeIdentity.NormalizePath(@"C:\r\b.txt") }, result.RescuePaths);
        Assert.Equal(0, result.NetPruned);
        Assert.DoesNotContain(@"C:\r\b.txt", survivors); // it was pruned, not forwarded as a survivor
        Assert.Equal(2, calls); // one shared B0 read + one shared B1 read
    }

    [Fact]
    public async Task TryCreatePruningScan_MultipleSegments_ReadsJournalOncePerBarrier()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return; // self-gate

        var store = NewV3Store();
        store.Publish(BuildBase());
        store.PublishSegment(BuildSegment());
        store.PublishSegment(BuildSegment(
            checkpoint: new UsnCheckpoint(1, 300),
            documents: new[] { (@"C:\r\e.txt", "the planner changed e") }));
        int calls = 0;
        ContentIndexFreshnessEvaluator.JournalReader reader = (root, since) =>
        {
            calls++;
            Assert.Equal(new UsnCheckpoint(1, 300), since);
            return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
        };
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);

        IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
            client, store, QueryOptions("planner"), sessionId: 10, reader,
            Path.Combine(_sandbox, "pspool-shared"), (_, _) => ValueTask.CompletedTask);

        Assert.NotNull(scan);
        await scan!.CompleteOfferingAsync();
        PruningScanResult result = await scan.ReconcileAtB1Async(CancellationToken.None);
        Assert.True(result.Accelerated);
        Assert.Equal(2, calls);
    }

    private static string? FindWorkerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(dir.FullName, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
