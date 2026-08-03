using System;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage 4 parity tests for the persistence-only build batch (plan §5.5). A paged full build now finalizes
/// each flush via <see cref="ContentIndexGenerationBuilder.BuildForPersistence"/> (a
/// <see cref="ContentIndexBuildBatch"/> with <b>no posting index</b>) instead of the queryable
/// <see cref="ContentIndexGenerationBuilder.Build"/>. These prove the two paths are indistinguishable on
/// disk and after reopen — same manifest, same bytes, same posting lists / candidates.
/// </summary>
public sealed class ContentIndexBuildBatchTests : IDisposable
{
    private readonly string _dir;

    public ContentIndexBuildBatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string ScopeId = "scope-id";
    private const string Volume = "vol";
    private const string Root = @"C:\r";
    private static readonly UsnCheckpoint Checkpoint = new(7, 4242);

    private static IndexIngestionPolicy Policy() => new(0, null, null, true, false, 0);

    // A deterministic non-null identity per path so volume serial + fileids exercise the parity paths.
    private static FileIdentity? FakeIdentity(string path)
        => new FileIdentity(0xABCDEF, new UsnFileIdentity((ulong)(path.GetHashCode() & 0x7FFFFFFF) + 1, 0));

    /// <summary>Builds a builder with a mixed corpus (indexable text, a skipped binary, a hard-link alias).</summary>
    private static ContentIndexGenerationBuilder NewPopulatedBuilder()
    {
        var builder = new ContentIndexGenerationBuilder(Policy(), new IndexBuildReport(), FakeIdentity);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the quick brown planner fox jumps"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("another planner file with words café"));
        builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("nothing relevant lives in this document"));
        builder.AddDocument(@"C:\r\bin.dat", new byte[] { (byte)'x', 0, (byte)'y' }); // binary → skipped, no alias
        builder.AddHardLink(@"C:\r\a-hardlink.txt", 0); // second alias to content id 0
        return builder;
    }

    private static TrigramExpression PlanLiteral(string literal)
        => Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(
            new EffectiveSearchPattern(literal, isRegex: false, caseSensitive: true, multiline: false, dotAll: false))).Query;

    // ───────────────────── manifest + collection parity ─────────────────────

    [Fact]
    public void Constructors_RejectNullDependencies()
    {
        ContentIndexBuildBatch valid = NewPopulatedBuilder().BuildForPersistence(
            ScopeId, Volume, Root, Checkpoint, DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentNullException>(() => new ContentIndexBuildBatch(
            null!, valid.Documents, valid.Aliases, valid.ContentIdentities, valid.Report));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexBuildBatch(
            valid.Manifest, null!, valid.Aliases, valid.ContentIdentities, valid.Report));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexBuildBatch(
            valid.Manifest, valid.Documents, null!, valid.ContentIdentities, valid.Report));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexBuildBatch(
            valid.Manifest, valid.Documents, valid.Aliases, null!, valid.Report));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexBuildBatch(
            valid.Manifest, valid.Documents, valid.Aliases, valid.ContentIdentities, null!));

        Assert.Throws<ArgumentNullException>(() => new ContentIndexDeltaSegmentBatch(null!, []));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexDeltaSegmentBatch(valid, null!));
    }

    [Fact]
    public void BuildForPersistence_ProducesSameManifestAndCollectionsAsBuild()
    {
        ContentIndexGenerationBuilder builder = NewPopulatedBuilder();
        DateTimeOffset builtUtc = DateTimeOffset.UtcNow;

        // Build() does not mutate builder state, so both finalizers see identical inputs.
        ContentIndexGeneration gen = builder.Build(ScopeId, Volume, Root, Checkpoint, builtUtc);
        ContentIndexBuildBatch batch = builder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, builtUtc);

        Assert.Equal(gen.Manifest.Serialize(), batch.Manifest.Serialize());
        Assert.Equal(gen.Manifest.ContentCount, batch.Manifest.ContentCount);
        Assert.Equal(gen.Manifest.AliasCount, batch.Manifest.AliasCount);
        Assert.Equal(gen.Manifest.VolumeSerialNumber, batch.Manifest.VolumeSerialNumber);
        Assert.Equal(0xABCDEFuL, batch.Manifest.VolumeSerialNumber); // captured from the fake identities
        Assert.Equal(gen.Manifest.FreshnessCheckpoint, batch.Manifest.FreshnessCheckpoint);

        Assert.Equal(gen.Documents.Count, batch.Documents.Count);
        Assert.Equal(gen.Aliases.Count, batch.Aliases.Count);
        Assert.Equal(gen.ContentIdentities.Count, batch.ContentIdentities.Count);
        // 3 admitted documents (the binary is skipped) and 4 aliases (3 + 1 hard link).
        Assert.Equal(3, batch.Documents.Count);
        Assert.Equal(4, batch.Aliases.Count);
    }

    // ───────────────────── serialized-bytes parity ─────────────────────

    [Fact]
    public void SerializedBatch_IsByteIdenticalToSerializedGeneration()
    {
        ContentIndexGenerationBuilder builder = NewPopulatedBuilder();
        DateTimeOffset builtUtc = DateTimeOffset.UtcNow;
        ContentIndexGeneration gen = builder.Build(ScopeId, Volume, Root, Checkpoint, builtUtc);
        ContentIndexBuildBatch batch = builder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, builtUtc);

        string dirGen = Path.Combine(_dir, "gen");
        string dirBatch = Path.Combine(_dir, "batch");
        ContentIndexGenerationSerializer.Write(dirGen, gen);
        ContentIndexGenerationSerializer.Write(dirBatch, batch);

        AssertFilesByteIdentical(dirGen, dirBatch,
            ContentIndexGenerationSerializer.ManifestFile,
            ContentIndexGenerationSerializer.ContentFile,
            ContentIndexGenerationSerializer.AliasesFile,
            ContentIndexGenerationSerializer.FileIdsFile);
    }

    [Fact]
    public void SerializedSegmentBatch_IsByteIdenticalToSerializedSegment()
    {
        ContentIndexGenerationBuilder builder = NewPopulatedBuilder();
        DateTimeOffset builtUtc = DateTimeOffset.UtcNow;
        ContentIndexGeneration gen = builder.Build(ScopeId, Volume, Root, Checkpoint, builtUtc);
        ContentIndexBuildBatch batch = builder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, builtUtc);

        string[] noRemovals = Array.Empty<string>();
        string dirSeg = Path.Combine(_dir, "seg");
        string dirSegBatch = Path.Combine(_dir, "seg-batch");
        ContentIndexDeltaSegmentSerializer.Write(dirSeg, new ContentIndexDeltaSegment(gen, noRemovals));
        ContentIndexDeltaSegmentSerializer.Write(dirSegBatch, new ContentIndexDeltaSegmentBatch(batch, noRemovals));

        AssertFilesByteIdentical(dirSeg, dirSegBatch,
            ContentIndexGenerationSerializer.ManifestFile,
            ContentIndexGenerationSerializer.ContentFile,
            ContentIndexGenerationSerializer.AliasesFile,
            ContentIndexGenerationSerializer.FileIdsFile,
            ContentIndexDeltaSegmentSerializer.TombstonesFile);
    }

    // ───────────────────── reopened-postings parity ─────────────────────

    [Fact]
    public void PersistedBatch_OpensToIdenticalPostingsAndCandidates()
    {
        ContentIndexGenerationBuilder builder = NewPopulatedBuilder();
        DateTimeOffset builtUtc = DateTimeOffset.UtcNow;
        ContentIndexGeneration gen = builder.Build(ScopeId, Volume, Root, Checkpoint, builtUtc);
        ContentIndexBuildBatch batch = builder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, builtUtc);

        string dirBatch = Path.Combine(_dir, "batch");
        ContentIndexGenerationSerializer.Write(dirBatch, batch);
        ContentIndexGeneration? reopened = ContentIndexGenerationSerializer.TryRead(dirBatch);
        Assert.NotNull(reopened);

        foreach (string term in new[] { "planner", "quick", "café", "zzzzz" })
        {
            TrigramExpression query = PlanLiteral(term);
            var fromGen = gen.Postings.EvaluateSet(query);
            var fromBatch = reopened!.Postings.EvaluateSet(query);
            Assert.True(fromGen.SetEquals(fromBatch), $"candidate mismatch for '{term}'");
        }

        // Identities + volume serial survive the batch round trip.
        FileIdMap map = reopened!.BuildFileIdMap();
        Assert.Equal(3, map.Count);
    }

    // ───────────────────── store publishes a batch ─────────────────────

    [Fact]
    public void Store_PublishesAndQueriesAPersistenceBatch()
    {
        var paths = new DefaultContentIndexPathProvider(_dir, _dir);
        var store = new ContentIndexStore(paths, ScopeId);

        ContentIndexGenerationBuilder builder = NewPopulatedBuilder();
        ContentIndexBuildBatch batch = builder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, DateTimeOffset.UtcNow);

        using (IndexMutationContext mutation = IndexMutationContext.Acquire(paths))
            store.PublishUnderLease(mutation, batch);

        ContentIndexGeneration? opened = store.TryOpenCurrent();
        Assert.NotNull(opened);
        Assert.Equal(3, opened!.Manifest.ContentCount);
        Assert.Equal(2, opened.Postings.EvaluateSet(PlanLiteral("planner")).Count); // a.txt + b.txt
    }

    [Fact]
    public void Store_PublishesBaseThenSegmentBatch_AndBothAreQueryable()
    {
        var paths = new DefaultContentIndexPathProvider(_dir, _dir);
        var store = new ContentIndexStore(paths, ScopeId);
        string[] noRemovals = Array.Empty<string>();

        // Base batch: only a.txt/hardlink.
        var baseBuilder = new ContentIndexGenerationBuilder(Policy(), new IndexBuildReport(), FakeIdentity);
        baseBuilder.AddDocument(@"C:\r\base.txt", Encoding.UTF8.GetBytes("the planner base document"));
        ContentIndexBuildBatch baseBatch = baseBuilder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, DateTimeOffset.UtcNow);

        // Segment batch: a distinct token so we can prove it is queryable over the base.
        var segBuilder = new ContentIndexGenerationBuilder(Policy(), new IndexBuildReport(), FakeIdentity);
        segBuilder.AddDocument(@"C:\r\seg.txt", Encoding.UTF8.GetBytes("segment zephyrqux extra token"));
        ContentIndexBuildBatch segBatch = segBuilder.BuildForPersistence(ScopeId, Volume, Root, Checkpoint, DateTimeOffset.UtcNow);

        using (IndexMutationContext mutation = IndexMutationContext.Acquire(paths))
        {
            store.PublishUnderLease(mutation, baseBatch);
            store.PublishSegmentFastUnderLease(mutation, new ContentIndexDeltaSegmentBatch(segBatch, noRemovals));
        }

        Assert.Equal(1, store.ActiveSegmentCount());

        var layered = store.TryOpenLayered();
        Assert.NotNull(layered);
        // Base token resolves in the base layer; segment token resolves in the segment layer.
        Assert.NotEmpty(layered!.Base.Postings.EvaluateSet(PlanLiteral("planner")));
        Assert.NotEmpty(layered.Segments.Single().Added.Postings.EvaluateSet(PlanLiteral("zephyrqux")));
    }

    private static void AssertFilesByteIdentical(string dirA, string dirB, params string[] files)
    {
        foreach (string f in files)
        {
            byte[] a = File.ReadAllBytes(Path.Combine(dirA, f));
            byte[] b = File.ReadAllBytes(Path.Combine(dirB, f));
            Assert.True(a.SequenceEqual(b), $"{f} differs between the generation and batch serializations");
        }
    }
}
