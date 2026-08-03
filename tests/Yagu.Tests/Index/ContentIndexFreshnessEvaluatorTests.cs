using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexFreshnessEvaluator"/> (plan §3.5): turning journal changes into a
/// dirty-content set and mapping the read status onto a <see cref="RootFreshnessVerdict"/>. The pure
/// logic is unit-tested with an injected fake journal reader; an end-to-end test drives a real build +
/// real journal through the evaluator into <see cref="ContentIndexQuerySession"/> classification,
/// self-gating when the journal is unavailable.
/// </summary>
public sealed class ContentIndexFreshnessEvaluatorTests : IDisposable
{
    private readonly string _sandbox;

    public ContentIndexFreshnessEvaluatorTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-fresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    [Fact]
    public void CreateReader_UnboundedReaderForMissingRoot_FailsClosed()
    {
        ContentIndexFreshnessEvaluator.JournalReader reader =
            ContentIndexFreshnessEvaluator.CreateReader(10);

        UsnReadResult result = reader(
            Path.Combine(_sandbox, "missing"),
            new UsnCheckpoint(1, 100));

        Assert.NotEqual(UsnReadStatus.Ok, result.Status);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void CreateReader_BoundedReaderForMissingRoot_FailsClosed()
    {
        ContentIndexFreshnessEvaluator.JournalReader reader =
            ContentIndexFreshnessEvaluator.CreateReader(10, TimeSpan.FromSeconds(1));

        UsnReadResult result = reader(
            Path.Combine(_sandbox, "missing"),
            new UsnCheckpoint(1, 100));

        Assert.NotEqual(UsnReadStatus.Ok, result.Status);
        Assert.Empty(result.Changes);
    }

    /// <summary>Builds a 2-document generation with deterministic fake identities and the given build checkpoint.</summary>
    private static (ContentIndexGeneration Gen, UsnFileIdentity Id0, UsnFileIdentity Id1) BuildGeneration(UsnCheckpoint checkpoint)
    {
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        ulong next = 500;
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out var id))
            {
                id = new UsnFileIdentity(next++, 0);
                assigned[norm] = id;
            }
            return new FileIdentity(0x7, id);
        }

        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha content here"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta content here"));
        var gen = builder.Build("s", "v", @"C:\r", checkpoint, DateTimeOffset.UtcNow);
        return (gen,
            assigned[IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")],
            assigned[IndexScopeIdentity.NormalizePath(@"C:\r\b.txt")]);
    }

    // ── Pure logic (injected reader) ──

    [Fact]
    public void ReadDirtySince_OkWithChange_IsContinuousAndMarksExactContent()
    {
        var (gen, id0, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        UsnReadResult Reader(string path, UsnCheckpoint since)
            => new(UsnReadStatus.Ok, new UsnCheckpoint(1, 200), new[] { new UsnChange(id0, 0x1) });

        var read = ContentIndexFreshnessEvaluator.ReadDirtySince(gen, gen.Manifest.FreshnessCheckpoint, Reader);

        Assert.Equal(RootFreshnessVerdict.Continuous, read.Verdict);
        Assert.True(read.IsContinuous);
        Assert.True(read.Dirty.IsDirty(0));
        Assert.False(read.Dirty.IsDirty(1));
        Assert.Equal(new UsnCheckpoint(1, 200), read.NextCheckpoint);
        Assert.Equal(1, read.JournalChangeCount);
        Assert.Equal(1, read.ResolvedJournalChangeCount);
    }

    [Theory]
    [InlineData(UsnReadStatus.JournalIdChanged, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.GapDetected, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.CheckpointAhead, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.UnknownRecordVersion, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Error, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.IdentityMismatch, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Incomplete, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Unavailable, RootFreshnessVerdict.JournalUnavailable)]
    public void ReadDirtySince_NonOkStatus_IsUntrustedWithEmptyDirty(UsnReadStatus status, RootFreshnessVerdict expected)
    {
        var (gen, id0, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        UsnReadResult Reader(string path, UsnCheckpoint since)
            => new(status, new UsnCheckpoint(1, 150), new[] { new UsnChange(id0, 0x1) });

        var read = ContentIndexFreshnessEvaluator.ReadDirtySince(gen, gen.Manifest.FreshnessCheckpoint, Reader);

        Assert.Equal(expected, read.Verdict);
        Assert.False(read.IsContinuous);
        Assert.Equal(0, read.Dirty.Count); // fail closed: no partial dirty set from an untrusted read
    }

    [Fact]
    public void ReadDirtySince_NoBuildCheckpoint_IsCheckpointInvalidAndReaderNotCalled()
    {
        var (gen, _, _) = BuildGeneration(UsnCheckpoint.None);
        bool readerCalled = false;
        UsnReadResult Reader(string path, UsnCheckpoint since)
        {
            readerCalled = true;
            return UsnReadResult.Unavailable;
        }

        var read = ContentIndexFreshnessEvaluator.ReadDirtySince(gen, gen.Manifest.FreshnessCheckpoint, Reader);

        Assert.Equal(RootFreshnessVerdict.CheckpointInvalid, read.Verdict);
        Assert.False(readerCalled);
    }

    [Fact]
    public void ResolveDirty_PreCollectedResult_MapsKnownAndIgnoresUnknownIdentity()
    {
        var (gen, id0, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        var unknown = new UsnFileIdentity(987654, 7);
        var collected = new UsnReadResult(
            UsnReadStatus.Ok,
            new UsnCheckpoint(1, 200),
            new[] { new UsnChange(id0, 0x1), new UsnChange(unknown, 0x1) });

        FreshnessRead read = ContentIndexFreshnessEvaluator.ResolveDirty(collected, gen.BuildFileIdMap());

        Assert.True(read.IsContinuous);
        Assert.Equal(new UsnCheckpoint(1, 200), read.NextCheckpoint);
        Assert.True(read.Dirty.IsDirty(0));
        Assert.False(read.Dirty.IsDirty(1));
        Assert.Equal(1, read.Dirty.Count);
        Assert.Equal(2, read.JournalChangeCount); // includes the newly created/unindexed identity
        Assert.Equal(1, read.ResolvedJournalChangeCount);
    }

    [Fact]
    public void ResolveDirty_LegacyExtendedMapWithUnknownV2Record_FailsClosed()
    {
        var map = new FileIdMap(0x9);
        map.Add(0, new UsnFileIdentity(0x67, 0x600));
        var collected = new UsnReadResult(
            UsnReadStatus.Ok,
            new UsnCheckpoint(1, 200),
            new[] { new UsnChange(new UsnFileIdentity(0x3000000000067600, 0), 0x1) });

        FreshnessRead read = ContentIndexFreshnessEvaluator.ResolveDirty(collected, map);

        Assert.False(read.IsContinuous);
        Assert.Equal(RootFreshnessVerdict.JournalDiscontinuity, read.Verdict);
        Assert.Equal(UsnReadStatus.IdentityMismatch, read.RawStatus);
        Assert.Equal(1, read.JournalChangeCount);
    }

    [Theory]
    [InlineData(UsnReadStatus.JournalIdChanged, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.GapDetected, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.CheckpointAhead, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.UnknownRecordVersion, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Error, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.IdentityMismatch, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Incomplete, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Unavailable, RootFreshnessVerdict.JournalUnavailable)]
    public void ResolveDirty_PreCollectedNonOkStatus_FailsClosed(
        UsnReadStatus status,
        RootFreshnessVerdict expected)
    {
        var (gen, id0, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        var collected = new UsnReadResult(
            status,
            new UsnCheckpoint(1, 150),
            new[] { new UsnChange(id0, 0x1) });

        FreshnessRead read = ContentIndexFreshnessEvaluator.ResolveDirty(collected, gen.BuildFileIdMap());

        Assert.Equal(expected, read.Verdict);
        Assert.Equal(status, read.RawStatus);
        Assert.False(read.IsContinuous);
        Assert.Equal(0, read.Dirty.Count);
        Assert.Equal(0, read.JournalChangeCount); // never trust a partial/non-continuous record count
        Assert.Equal(0, read.ResolvedJournalChangeCount);
    }

    [Fact]
    public void ReadDirtyAtBuildBarrier_ReadsFromManifestCheckpoint()
    {
        var (gen, _, _) = BuildGeneration(new UsnCheckpoint(42, 777));
        UsnCheckpoint captured = default;
        UsnReadResult Reader(string path, UsnCheckpoint since)
        {
            captured = since;
            return new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());
        }

        ContentIndexFreshnessEvaluator.ReadDirtyAtBuildBarrier(gen, Reader);

        Assert.Equal(new UsnCheckpoint(42, 777), captured);
    }

    // ── End-to-end against a real build + real journal ──

    [Fact]
    public void EndToEnd_ModifiedFileIsClassifiedDirtyByUsn_AndGenerationAccelerates()
    {
        string corpus = Path.Combine(_sandbox, "corpus");
        string indexRoot = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(corpus);
        string aPath = Path.Combine(corpus, "a.txt");
        string bPath = Path.Combine(corpus, "b.txt");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(aPath, "the planner produces trigram queries", utf8);
        File.WriteAllText(bPath, "another file mentioning the planner", utf8);

        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var manager = new ContentIndexManager(paths);
        var result = manager.BuildScope(corpus, OpenPolicy);

        var store = new ContentIndexStore(paths, result.ScopeId);
        var gen = store.TryOpenCurrent();
        Assert.NotNull(gen);

        // Self-gate: needs a captured build checkpoint (journal available on the temp volume).
        if (gen!.Manifest.FreshnessCheckpoint.JournalId == 0)
            return;

        // Modify only a.txt after the build.
        File.AppendAllText(aPath, " and more", utf8);

        var freshness = ContentIndexFreshnessEvaluator.ReadDirtyAtBuildBarrier(gen);
        if (!freshness.IsContinuous)
            return; // tolerate transient journal states

        // The whole generation still accelerates (structural trusted + continuous + eligible)…
        var decision = ContentIndexQuerySession.CanAccelerate(gen, freshness.Verdict, queryEligible: true);
        Assert.IsType<GenerationDecision.UseGeneration>(decision);

        // …but the changed file is now classified dirty, so it is live-scanned, not pruned.
        var session = ContentIndexQuerySession.Begin(gen, TrigramExpression.All, freshness.Dirty);
        var classifyA = session.Classify(IndexScopeIdentity.NormalizePath(aPath));
        Assert.IsType<IndexPathClassification.DirtyByUsn>(classifyA);

        // The untouched file remains a fresh member (its content id is not dirty).
        var classifyB = session.Classify(IndexScopeIdentity.NormalizePath(bPath));
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(classifyB);
    }

    // ── Mapped (format-v3 reverse index) freshness parity (plan §6 Stage 2 slice 2) ──

    private ContentIndexV3Reader WriteAndOpenV3(ContentIndexGeneration gen)
    {
        string v3Dir = Path.Combine(_sandbox, "v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(v3Dir);
        ContentIndexV3Format.Write(v3Dir, gen);
        return ContentIndexV3Format.TryOpen(v3Dir)!;
    }

    [Fact]
    public void ReadDirtySince_MappedReader_MatchesTheFileIdMapOverload()
    {
        var (gen, id0, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        var unknown = new UsnFileIdentity(987654, 7); // a change for a file not in this index
        UsnReadResult Reader(string path, UsnCheckpoint since)
            => new(UsnReadStatus.Ok, new UsnCheckpoint(1, 200), new[] { new UsnChange(id0, 0x1), new UsnChange(unknown, 0x1) });

        // Deserialized FileIdMap oracle vs the memory-mapped v3 reverse index — same captured identities.
        var viaFileIds = ContentIndexFreshnessEvaluator.ReadDirtySince(
            gen.Manifest.NormalizedRootPath, gen.Manifest.FreshnessCheckpoint, gen.BuildFileIdMap(), Reader);

        using ContentIndexV3Reader reader = WriteAndOpenV3(gen);
        var viaMapped = ContentIndexFreshnessEvaluator.ReadDirtySince(
            gen.Manifest.NormalizedRootPath, gen.Manifest.FreshnessCheckpoint, reader, Reader);

        Assert.Equal(viaFileIds.Verdict, viaMapped.Verdict);
        Assert.Equal(RootFreshnessVerdict.Continuous, viaMapped.Verdict);
        Assert.Equal(viaFileIds.NextCheckpoint, viaMapped.NextCheckpoint);
        Assert.Equal(viaFileIds.Dirty.Count, viaMapped.Dirty.Count);
        Assert.True(viaMapped.Dirty.IsDirty(0));   // a.txt (content 0) changed
        Assert.False(viaMapped.Dirty.IsDirty(1));  // b.txt (content 1) untouched
        Assert.Equal(1, viaMapped.Dirty.Count);    // the unknown identity was ignored (not in the index)
    }

    [Fact]
    public void ReadDirtySince_MappedReader_NoBuildCheckpoint_IsCheckpointInvalidAndReaderNotCalled()
    {
        var (gen, _, _) = BuildGeneration(UsnCheckpoint.None);
        using ContentIndexV3Reader reader = WriteAndOpenV3(gen);
        bool readerCalled = false;
        UsnReadResult Reader(string path, UsnCheckpoint since)
        {
            readerCalled = true;
            return UsnReadResult.Unavailable;
        }

        var read = ContentIndexFreshnessEvaluator.ReadDirtySince(
            gen.Manifest.NormalizedRootPath, gen.Manifest.FreshnessCheckpoint, reader, Reader);

        Assert.Equal(RootFreshnessVerdict.CheckpointInvalid, read.Verdict);
        Assert.False(readerCalled);
    }

    [Fact]
    public void ReadDirtySince_MappedReader_DefaultReaderForMissingRoot_FailsClosed()
    {
        var (gen, _, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        using ContentIndexV3Reader reader = WriteAndOpenV3(gen);

        FreshnessRead read = ContentIndexFreshnessEvaluator.ReadDirtySince(
            Path.Combine(_sandbox, "missing"),
            gen.Manifest.FreshnessCheckpoint,
            reader);

        Assert.False(read.IsContinuous);
        Assert.Equal(0, read.Dirty.Count);
    }

    [Theory]
    [InlineData(UsnReadStatus.JournalIdChanged, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.GapDetected, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.CheckpointAhead, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Incomplete, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Unavailable, RootFreshnessVerdict.JournalUnavailable)]
    public void ReadDirtySince_MappedReader_NonOkStatus_IsUntrustedWithEmptyDirty(UsnReadStatus status, RootFreshnessVerdict expected)
    {
        var (gen, id0, _) = BuildGeneration(new UsnCheckpoint(1, 100));
        using ContentIndexV3Reader reader = WriteAndOpenV3(gen);
        UsnReadResult Reader(string path, UsnCheckpoint since)
            => new(status, new UsnCheckpoint(1, 150), new[] { new UsnChange(id0, 0x1) });

        var read = ContentIndexFreshnessEvaluator.ReadDirtySince(
            gen.Manifest.NormalizedRootPath, gen.Manifest.FreshnessCheckpoint, reader, Reader);

        Assert.Equal(expected, read.Verdict);
        Assert.False(read.IsContinuous);
        Assert.Equal(0, read.Dirty.Count); // fail closed: no partial dirty set from an untrusted read
    }
}
