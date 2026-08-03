using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests the Phase 3 incremental machinery (plan §3.4/§11.4): the <see cref="ContentIndexDeltaSegment"/>
/// model + serializer, the newest-first <see cref="LayeredContentIndexQuerySession"/>, and the
/// <see cref="ContentIndexDeltaSegmentBuilder"/>. All pure — per-layer dirty sets are injected so every
/// branch is deterministic without USN.
/// </summary>
public sealed class ContentIndexDeltaSegmentTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";

    public ContentIndexDeltaSegmentTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-delta-seg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration BuildGeneration(params (string Path, string Text)[] docs)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        foreach (var (path, text) in docs)
            builder.AddDocument(path, Encoding.UTF8.GetBytes(text));
        return builder.Build("scope", "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression Plan(string term)
    {
        var options = new SearchOptions { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        var pattern = EffectiveSearchPattern.Resolve(options);
        return TrigramQueryPlanner.Plan(pattern) is TrigramPlan.Eligible e ? e.Query : throw new InvalidOperationException($"'{term}' ineligible");
    }

    private static string Norm(string path) => IndexScopeIdentity.NormalizePath(path);

    [Fact]
    public void Builders_ExposeReportAndTombstoneCount()
    {
        var gen = new ContentIndexGenerationBuilder(OpenPolicy);
        gen.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the quick brown fox jumps"));
        Assert.Equal(1, gen.Report.IndexedCount);

        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        seg.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("lazy dog over there"));
        seg.AddTombstone(@"C:\r\gone.txt");
        Assert.Equal(1, seg.Report.IndexedCount);
        Assert.Equal(1, seg.TombstoneCount);
    }

    [Fact]
    public void SegmentBuilder_PreclassifiedChangesReplaceOrTombstonePriorState()
    {
        const string admittedPath = @"C:\r\admitted.txt";
        const string rejectedPath = @"C:\r\rejected.txt";
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        builder.AddTombstone(admittedPath);

        long admittedId = builder.AddChangedClassified(
            admittedPath,
            new IndexContentClassification(IndexSkipReason.None, [new Trigram(1, 2, 3)]),
            identity: null);
        long rejectedId = builder.AddChangedClassified(
            rejectedPath,
            new IndexContentClassification(IndexSkipReason.Binary, []),
            identity: null);

        Assert.Equal(0, admittedId);
        Assert.Equal(-1, rejectedId);
        ContentIndexDeltaSegment segment = builder.Build(
            "scope", "vol", _root, new UsnCheckpoint(1, 10), DateTimeOffset.UtcNow);
        Assert.False(segment.IsRemoved(Norm(admittedPath)));
        Assert.True(segment.IsRemoved(Norm(rejectedPath)));
    }

    [Fact]
    public void SegmentBuilder_SeedsVolumeBindingIntoManifest()
    {
        var binding = new VolumeBinding(
            @"\\?\Volume{ABC}\", 0x1234, "NTFS", @"C:\", "r");
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        builder.SeedVolumeSerialNumber(binding.VolumeSerialNumber);
        builder.SeedVolumeBinding(binding);

        ContentIndexDeltaSegment segment = builder.Build(
            "scope", "vol", _root, new UsnCheckpoint(1, 10), DateTimeOffset.UtcNow);

        Assert.Equal(binding.VolumeGuidPath, segment.Added.Manifest.VolumeGuidPath);
        Assert.Equal(binding.VolumeSerialNumber, segment.Added.Manifest.VolumeSerialNumber);
        Assert.Equal(binding.FileSystemName, segment.Added.Manifest.FileSystemName);
        Assert.Equal(binding.RootRelativePath, segment.Added.Manifest.VolumeRelativeRootPath);
    }

    [Fact]
    public void GenerationBuilder_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentIndexGenerationBuilder(null!));

        var gen = new ContentIndexGenerationBuilder(OpenPolicy);
        long id = gen.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("hello world content here"));
        Assert.True(id >= 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.AddHardLink(@"C:\r\b.txt", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.AddHardLink(@"C:\r\b.txt", 999));
    }

    [Fact]
    public void DeltaSegment_Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentIndexDeltaSegment(null!, System.Array.Empty<string>()));

        ContentIndexGeneration gen = new ContentIndexGenerationBuilder(OpenPolicy)
            .Build("s", "v", _root, new UsnCheckpoint(1, 1), DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentNullException>(() => new ContentIndexDeltaSegment(gen, null!));
    }

    // ── Segment serialization ──

    [Fact]
    public void Segment_RoundTrips_AddedDocsAndTombstones()
    {
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        builder.AddChangedDocument(@"C:\r\new.txt", Encoding.UTF8.GetBytes("the quick brown fox"));
        builder.AddTombstone(@"C:\r\gone.txt");
        var segment = builder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);

        string dir = Path.Combine(_sandbox, "seg1");
        ContentIndexDeltaSegmentSerializer.Write(dir, segment);
        ContentIndexDeltaSegment? read = ContentIndexDeltaSegmentSerializer.TryRead(dir);

        Assert.NotNull(read);
        Assert.Equal(1, read!.Added.AliasCount);
        Assert.True(read.IsRemoved(Norm(@"C:\r\gone.txt")));
        Assert.False(read.IsRemoved(Norm(@"C:\r\new.txt")));
        Assert.Equal(new UsnCheckpoint(2, 200), read.FreshnessCheckpoint);
    }

    [Fact]
    public void Segment_EmptySegment_RoundTrips()
    {
        var segment = new ContentIndexDeltaSegmentBuilder(OpenPolicy)
            .Build("scope", "vol", _root, new UsnCheckpoint(3, 300), DateTimeOffset.UtcNow);
        string dir = Path.Combine(_sandbox, "empty");
        ContentIndexDeltaSegmentSerializer.Write(dir, segment);
        ContentIndexDeltaSegment? read = ContentIndexDeltaSegmentSerializer.TryRead(dir);
        Assert.NotNull(read);
        Assert.Equal(0, read!.Added.AliasCount);
        Assert.Empty(read.RemovedPaths);
    }

    [Fact]
    public void Segment_CorruptTombstones_ReturnsNull()
    {
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        builder.AddTombstone(@"C:\r\x.txt");
        var segment = builder.Build("scope", "vol", _root, new UsnCheckpoint(4, 400), DateTimeOffset.UtcNow);
        string dir = Path.Combine(_sandbox, "corrupt");
        ContentIndexDeltaSegmentSerializer.Write(dir, segment);

        // Flip a byte in tombstones.bin → checksum mismatch → null.
        string tombFile = Path.Combine(dir, ContentIndexDeltaSegmentSerializer.TombstonesFile);
        byte[] bytes = File.ReadAllBytes(tombFile);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(tombFile, bytes);

        Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(dir));
    }

    [Fact]
    public void Segment_MissingTombstones_ReturnsNull()
    {
        var segment = new ContentIndexDeltaSegmentBuilder(OpenPolicy)
            .Build("scope", "vol", _root, new UsnCheckpoint(5, 500), DateTimeOffset.UtcNow);
        string dir = Path.Combine(_sandbox, "notomb");
        ContentIndexDeltaSegmentSerializer.Write(dir, segment);
        File.Delete(Path.Combine(dir, ContentIndexDeltaSegmentSerializer.TombstonesFile));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(dir));
    }

    [Fact]
    public void Segment_TryRead_MissingDir_ReturnsNull()
        => Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(Path.Combine(_sandbox, "nope")));

    [Fact]
    public void Segment_TryRead_EmptyDirectoryArgumentReturnsNull()
        => Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(string.Empty));

    [Fact]
    public void Segment_MissingAddedGenerationFailsReadAndValidation()
    {
        string dir = Path.Combine(_sandbox, "missing-generation");
        Directory.CreateDirectory(dir);

        Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(dir));
        Assert.False(ContentIndexDeltaSegmentSerializer.TryValidateSerializedSegment(dir, out _));
    }

    [Fact]
    public void Segment_TryRead_MalformedTombstoneRecordsReturnNull()
    {
        string negativeCount = WriteEmptySegment("negative-count");
        WriteTombstones(negativeCount, writer => writer.Write(-1));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(negativeCount));

        string negativeLength = WriteEmptySegment("negative-length");
        WriteTombstones(negativeLength, writer =>
        {
            writer.Write(1);
            writer.Write(-1);
        });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(negativeLength));

        string truncated = WriteEmptySegment("truncated-record");
        WriteTombstones(truncated, writer => writer.Write(1));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryRead(truncated));
    }

    [Fact]
    public void TryReadTombstones_RejectsNullCandidatesAndMissingOrMalformedFiles()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ContentIndexDeltaSegmentSerializer.TryReadTombstones(_sandbox, null!));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(
            Path.Combine(_sandbox, "missing"), new HashSet<string>()));

        string shortBody = WriteTombstonesDirectory("short-body", _ => { });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(shortBody, new HashSet<string>()));

        string negativeCount = WriteTombstonesDirectory("stream-negative-count", writer => writer.Write(-1));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(negativeCount, new HashSet<string>()));
    }

    [Fact]
    public void TryReadTombstones_RejectsInvalidPathLengths()
    {
        string missingLength = WriteTombstonesDirectory("missing-length", writer => writer.Write(1));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(missingLength, new HashSet<string> { "candidate" }));

        string negativeLength = WriteTombstonesDirectory("stream-negative-length", writer =>
        {
            writer.Write(1);
            writer.Write(-1);
        });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(negativeLength, new HashSet<string> { "candidate" }));

        string oversizedLength = WriteTombstonesDirectory("oversized-length", writer =>
        {
            writer.Write(1);
            writer.Write(32 * 1024 * 1024 + 1);
        });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(oversizedLength, new HashSet<string> { "candidate" }));
    }

    [Fact]
    public void TryReadTombstones_EmptyCandidatesSkipsBodiesAndDetectsTruncation()
    {
        string valid = WriteTombstonesDirectory("skip-valid", writer => WritePath(writer, "ignored"));
        IReadOnlySet<string> result = Assert.IsAssignableFrom<IReadOnlySet<string>>(
            ContentIndexDeltaSegmentSerializer.TryReadTombstones(valid, new HashSet<string>()));
        Assert.Empty(result);

        string truncated = WriteTombstonesDirectory("skip-truncated", writer =>
        {
            writer.Write(1);
            writer.Write(5);
        });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(truncated, new HashSet<string>()));
    }

    [Fact]
    public void TryReadTombstones_GrowsBufferAndReturnsOnlyCandidates()
    {
        string longPath = new('x', 300);
        string dir = WriteTombstonesDirectory("candidate-filter", writer =>
        {
            writer.Write(2);
            WritePathBody(writer, longPath);
            WritePathBody(writer, "not-a-candidate");
        });

        IReadOnlySet<string> result = Assert.IsAssignableFrom<IReadOnlySet<string>>(
            ContentIndexDeltaSegmentSerializer.TryReadTombstones(dir, new HashSet<string> { longPath }));

        Assert.Equal([longPath], result);
    }

    [Fact]
    public void TryReadTombstones_DetectsTruncatedBodyAndTrailingGarbage()
    {
        string truncated = WriteTombstonesDirectory("read-truncated", writer =>
        {
            writer.Write(1);
            writer.Write(5);
        });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(
            truncated, new HashSet<string> { "candidate" }));

        string trailing = WriteTombstonesDirectory("trailing", writer =>
        {
            writer.Write(0);
            writer.Write((byte)1);
        });
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(trailing, new HashSet<string>()));
    }

    [Fact]
    public void TryReadTombstones_CancellationPropagates()
    {
        string dir = WriteTombstonesDirectory("cancelled", writer => WritePath(writer, "candidate"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ContentIndexDeltaSegmentSerializer.TryReadTombstones(
                dir, new HashSet<string> { "candidate" }, cancellation.Token));
    }

    [Fact]
    public void TryReadTombstones_ReaderOpenFailuresReturnNull()
    {
        var candidates = new HashSet<string>();
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(
            _sandbox,
            candidates,
            _ => throw new IOException("open failed")));
        Assert.Null(ContentIndexDeltaSegmentSerializer.TryReadTombstones(
            _sandbox,
            candidates,
            _ => throw new UnauthorizedAccessException("denied")));
    }

    private string WriteEmptySegment(string name)
    {
        string dir = Path.Combine(_sandbox, name);
        var segment = new ContentIndexDeltaSegmentBuilder(OpenPolicy)
            .Build("scope", "vol", _root, new UsnCheckpoint(1, 1), DateTimeOffset.UtcNow);
        ContentIndexDeltaSegmentSerializer.Write(dir, segment);
        return dir;
    }

    private string WriteTombstonesDirectory(string name, Action<BinaryWriter> writeBody)
    {
        string dir = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(dir);
        WriteTombstones(dir, writeBody);
        return dir;
    }

    private static void WriteTombstones(string dir, Action<BinaryWriter> writeBody)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
            writeBody(writer);
        ChecksummedFile.Write(
            Path.Combine(dir, ContentIndexDeltaSegmentSerializer.TombstonesFile),
            body.ToArray());
    }

    private static void WritePath(BinaryWriter writer, string path)
    {
        writer.Write(1);
        WritePathBody(writer, path);
    }

    private static void WritePathBody(BinaryWriter writer, string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    // ── Incremental builder semantics ──

    [Fact]
    public void Builder_ChangedToBinary_IsTombstonedNotAdded()
    {
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        // A NUL byte makes the content binary → not admitted → tombstoned.
        long id = builder.AddChangedDocument(@"C:\r\bin.dat", new byte[] { (byte)'a', 0x00, (byte)'b' });
        Assert.Equal(-1, id);
        var seg = builder.Build("scope", "vol", _root, new UsnCheckpoint(6, 600), DateTimeOffset.UtcNow);
        Assert.True(seg.IsRemoved(Norm(@"C:\r\bin.dat")));
        Assert.Equal(0, seg.Added.AliasCount);
    }

    [Fact]
    public void Builder_TombstoneThenReadd_DropsTombstone()
    {
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        builder.AddTombstone(@"C:\r\f.txt");
        builder.AddChangedDocument(@"C:\r\f.txt", Encoding.UTF8.GetBytes("the quick brown fox re-created"));
        var seg = builder.Build("scope", "vol", _root, new UsnCheckpoint(7, 700), DateTimeOffset.UtcNow);
        Assert.False(seg.IsRemoved(Norm(@"C:\r\f.txt"))); // live add wins
        Assert.Equal(1, seg.Added.AliasCount);
    }

    // ── Layered newest-first query ──

    [Fact]
    public void Layered_NewestSegmentShadowsBase()
    {
        // base: a.txt contains "planner"; segment replaces a.txt with content that does NOT contain it.
        var baseGen = BuildGeneration((@"C:\r\a.txt", "the planner produces trigram queries"));
        var segBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        segBuilder.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        var seg = segBuilder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);

        var session = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { seg }, Plan("planner"),
            new DirtyContentSet(), new[] { new DirtyContentSet() });

        // The segment's fresh (non-matching) content wins → a.txt is a nonmember (prunable), NOT a member.
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(session.Classify(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void Layered_TombstonedPath_IsUnindexed()
    {
        var baseGen = BuildGeneration((@"C:\r\a.txt", "the planner produces trigram queries"));
        var segBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        segBuilder.AddTombstone(@"C:\r\a.txt");
        var seg = segBuilder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);

        var session = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { seg }, Plan("planner"),
            new DirtyContentSet(), new[] { new DirtyContentSet() });

        Assert.IsType<IndexPathClassification.Unindexed>(session.Classify(Norm(@"C:\r\a.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void Layered_NewFileInSegment_IsClassified()
    {
        var baseGen = BuildGeneration((@"C:\r\a.txt", "unrelated base content"));
        var segBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        segBuilder.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        var seg = segBuilder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);

        var session = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { seg }, Plan("planner"),
            new DirtyContentSet(), new[] { new DirtyContentSet() });

        Assert.IsType<IndexPathClassification.FreshIndexedMember>(session.Classify(Norm(@"C:\r\b.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\r\b.txt")));
    }

    [Fact]
    public void Layered_DirtyInAuthoritativeLayer_LiveScans()
    {
        var baseGen = BuildGeneration((@"C:\r\a.txt", "the planner produces trigram queries"));
        var dirtyBase = new DirtyContentSet();
        dirtyBase.MarkDirty(0); // a.txt content id 0 is dirty

        var session = LayeredContentIndexQuerySession.Begin(
            baseGen, Array.Empty<ContentIndexDeltaSegment>(), Plan("zzzzz"),
            dirtyBase, Array.Empty<DirtyContentSet>());

        Assert.IsType<IndexPathClassification.DirtyByUsn>(session.Classify(Norm(@"C:\r\a.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void Layered_MissingIdentityAndAbsentPath_AlwaysLiveScan()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: _ => null);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("ordinary indexed content"));
        ContentIndexGeneration generation = builder.Build(
            "scope", "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        LayeredContentIndexQuerySession session = LayeredContentIndexQuerySession.Begin(
            generation, Array.Empty<ContentIndexDeltaSegment>(), Plan("zzzzz"),
            new DirtyContentSet(), Array.Empty<DirtyContentSet>());

        Assert.IsType<IndexPathClassification.DirtyByUsn>(session.Classify(Norm(@"C:\r\a.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\r\a.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\r\absent.txt")));
    }

    [Fact]
    public void Layered_BeginWithCandidates_MatchesBegin_ForTheSamePerLayerCandidates()
    {
        // Injecting per-layer candidate sets (as the format-v3 layered reader does) must yield a session
        // identical to the in-process Begin, because each injected set equals that layer's
        // Postings.EvaluateSet — only the candidate producer differs.
        var baseGen = BuildGeneration((@"C:\r\a.txt", "the planner produces trigram queries"));
        var segBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        segBuilder.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        segBuilder.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        var seg = segBuilder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);

        TrigramExpression query = Plan("planner");
        var segments = new[] { seg };
        var baseDirty = new DirtyContentSet();
        var segDirties = new[] { new DirtyContentSet() };

        var reference = LayeredContentIndexQuerySession.Begin(baseGen, segments, query, baseDirty, segDirties);
        var injected = LayeredContentIndexQuerySession.BeginWithCandidates(
            baseGen, segments,
            baseGen.Postings.EvaluateSet(query),
            new IReadOnlySet<int>[] { seg.Added.Postings.EvaluateSet(query) },
            baseDirty, segDirties);

        Assert.Equal(reference.CandidateCount, injected.CandidateCount);
        foreach (string p in new[] { @"C:\r\a.txt", @"C:\r\b.txt", @"C:\r\c.txt" })
        {
            string n = Norm(p);
            Assert.Equal(reference.Classify(n).GetType(), injected.Classify(n).GetType());
        }
    }

    [Fact]
    public void Layered_BeginWithCandidates_RejectsMismatchedSegmentCandidateCount()
    {
        var baseGen = BuildGeneration((@"C:\r\a.txt", "the planner produces trigram queries"));
        var segBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        segBuilder.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("another planner here"));
        var seg = segBuilder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);
        TrigramExpression query = Plan("planner");

        Assert.Throws<ArgumentException>(() => LayeredContentIndexQuerySession.BeginWithCandidates(
            baseGen, new[] { seg },
            baseGen.Postings.EvaluateSet(query),
            Array.Empty<IReadOnlySet<int>>(), // wrong: 0 candidate sets for 1 segment
            new DirtyContentSet(), new[] { new DirtyContentSet() }));
    }

    [Fact]
    public void Layered_Route_PrunesNonmember_AndB1RescuesWhenDirtied()
    {
        // base a.txt (member of "planner"), b.txt (nonmember). Query "zzzzz" → both nonmembers → both prunable.
        var baseGen = BuildGeneration(
            (@"C:\r\a.txt", "alpha content"),
            (@"C:\r\b.txt", "beta content"));

        var session = LayeredContentIndexQuerySession.Begin(
            baseGen, Array.Empty<ContentIndexDeltaSegment>(), Plan("zzzzz"),
            new DirtyContentSet(), Array.Empty<DirtyContentSet>());

        var da = session.Route(Norm(@"C:\r\a.txt"));
        var db = session.Route(Norm(@"C:\r\b.txt"));
        Assert.IsType<PathDecision.ProvisionalPrune>(da);
        Assert.IsType<PathDecision.ProvisionalPrune>(db);
        Assert.Equal(2, session.ProvisionalAliases.Count);

        // At B1, a.txt (content id 0) became dirty → its alias must be rescued; b.txt stays pruned.
        var dirtyB1 = new DirtyContentSet();
        dirtyB1.MarkDirty(0);
        IReadOnlyList<long> rescued = session.ReconcileAtB1(dirtyB1, Array.Empty<DirtyContentSet>());
        Assert.Single(rescued);
        Assert.Equal(((PathDecision.ProvisionalPrune)da).AliasId, rescued[0]);
        Assert.Single(session.ProvisionalAliases); // b.txt still pruned

        long remaining = ((PathDecision.ProvisionalPrune)db).AliasId;
        Assert.Empty(session.ResolveAliasPaths(Array.Empty<long>()));
        Assert.Empty(session.ResolveAliasPaths(new[] { long.MaxValue }));
        Assert.Equal(new[] { Norm(@"C:\r\a.txt"), Norm(@"C:\r\b.txt") },
            session.ResolveAliasPaths(new[] { rescued[0], remaining }));
        session.ClearProvisionalAliases();
        Assert.Empty(session.ProvisionalAliases);
        Assert.Empty(session.ResolveAliasPaths(new[] { rescued[0], remaining }));
    }

    [Fact]
    public void Layered_GlobalAliasIds_AreUniqueAcrossLayers()
    {
        // Both base and segment prune a path; their local alias ids would collide (both 0) but the layered
        // session assigns globally-unique ids, and B1 reconciliation routes each to the right layer.
        var baseGen = BuildGeneration((@"C:\r\a.txt", "alpha"));
        var segBuilder = new ContentIndexDeltaSegmentBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        segBuilder.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("beta"));
        var seg = segBuilder.Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);

        var session = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { seg }, Plan("zzzzz"),
            new DirtyContentSet(), new[] { new DirtyContentSet() });

        var pruneA = (PathDecision.ProvisionalPrune)session.Route(Norm(@"C:\r\a.txt")); // base, content 0
        var pruneB = (PathDecision.ProvisionalPrune)session.Route(Norm(@"C:\r\b.txt")); // segment, content 0
        Assert.NotEqual(pruneA.AliasId, pruneB.AliasId);

        // Dirty content 0 in the SEGMENT only → only b.txt is rescued (not a.txt, even though it is also content 0).
        var segDirtyB1 = new DirtyContentSet();
        segDirtyB1.MarkDirty(0);
        IReadOnlyList<long> rescued = session.ReconcileAtB1(new DirtyContentSet(), new[] { segDirtyB1 });
        Assert.Single(rescued);
        Assert.Equal(pruneB.AliasId, rescued[0]);
        Assert.Equal(new[] { Norm(@"C:\r\b.txt") }, session.ResolveAliasPaths(rescued));
    }

    [Fact]
    public void Layered_Begin_MismatchedDirtyCount_Throws()
    {
        var baseGen = BuildGeneration((@"C:\r\a.txt", "alpha"));
        var seg = new ContentIndexDeltaSegmentBuilder(OpenPolicy).Build("scope", "vol", _root, new UsnCheckpoint(2, 200), DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { seg }, Plan("alpha"), new DirtyContentSet(), Array.Empty<DirtyContentSet>()));

        LayeredContentIndexQuerySession session = LayeredContentIndexQuerySession.Begin(
            baseGen, new[] { seg }, Plan("alpha"), new DirtyContentSet(), new[] { new DirtyContentSet() });
        Assert.Throws<ArgumentException>(() =>
            session.ReconcileAtB1(new DirtyContentSet(), Array.Empty<DirtyContentSet>()));
    }
}
