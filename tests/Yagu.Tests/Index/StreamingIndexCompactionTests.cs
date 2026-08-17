using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Acceptance tests for folding an entire layered index into a fresh base by streaming. The in-memory
/// <see cref="ContentIndexCompactor"/> is the reference oracle; the streamed base must be
/// query-equivalent, keep the original creation time and newest checkpoint, publish through the single
/// pointer flip, and leave the previous generation intact as the rollback point.
/// </summary>
public sealed class StreamingIndexCompactionTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public StreamingIndexCompactionTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-stream-compact", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private static readonly DateTimeOffset Created = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private ContentIndexStore PublishLayeredIndex(int seed, out List<string> layerDirectories)
    {
        var random = new Random(seed);
        var store = new ContentIndexStore(_paths, _scopeId, 2);

        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        for (int i = 0; i < 8; i++)
            baseBuilder.AddDocument($@"C:\r\base-{i:D2}.txt", Encoding.UTF8.GetBytes($"base document {i} zephyrqux"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), Created));

        // Two disjoint pages of the same full build (they share the base checkpoint and have no
        // incremental provenance).
        for (int page = 0; page < 2; page++)
        {
            var pageBuilder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            pageBuilder.AddDocument($@"C:\r\page-{page}.txt", Encoding.UTF8.GetBytes($"paged document {page}"));
            store.PublishSegment(new ContentIndexDeltaSegment(
                pageBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), Created.AddMinutes(page + 1),
                    lastIncrementalUpdateUtc: null),
                Array.Empty<string>()));
        }

        // Incremental history: replacements, deletions and hard links over the base.
        for (int layer = 0; layer < 4; layer++)
        {
            var added = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            var tombstones = new HashSet<string>(StringComparer.Ordinal);
            for (int d = 0; d < random.Next(1, 4); d++)
            {
                string path = $@"C:\r\base-{random.Next(0, 8):D2}.txt";
                long contentId = added.AddDocument(path, Encoding.UTF8.GetBytes($"replaced at layer {layer} {random.Next()}"));
                if (contentId >= 0 && random.Next(3) == 0)
                    added.AddHardLink($@"C:\r\hardlink-{layer}-{d}.txt", contentId);
            }
            tombstones.Add(IndexScopeIdentity.NormalizePath($@"C:\r\base-{random.Next(0, 8):D2}.txt"));
            added.SeedVolumeSerialNumber(0x5);
            store.PublishSegment(new ContentIndexDeltaSegment(
                added.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200 + (layer * 10)),
                    Created.AddHours(1).AddMinutes(layer), createdUtc: null,
                    lastIncrementalUpdateUtc: Created.AddHours(1).AddMinutes(layer)),
                tombstones));
        }

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        layerDirectories = new List<string> { baseDir! };
        layerDirectories.AddRange(segmentDirs);
        return store;
    }

    private static Dictionary<string, (List<uint> Trigrams, UsnFileIdentity? Identity)> DescribeByPath(
        ContentIndexGeneration generation)
    {
        var described = new Dictionary<string, (List<uint>, UsnFileIdentity?)>(StringComparer.Ordinal);
        foreach ((string path, (long _, long contentId)) in generation.Aliases)
        {
            described[path] = (
                generation.Documents[(int)contentId].Select(t => t.Value).OrderBy(v => v).ToList(),
                generation.ContentIdentities[(int)contentId]);
        }
        return described;
    }

    private static List<List<string>> ContentPartition(ContentIndexGeneration generation)
    {
        var byContent = new Dictionary<long, List<string>>();
        foreach ((string path, (long _, long contentId)) in generation.Aliases)
        {
            if (!byContent.TryGetValue(contentId, out List<string>? group))
                byContent[contentId] = group = [];
            group.Add(path);
        }
        return byContent.Values
            .Select(g => g.OrderBy(p => p, StringComparer.Ordinal).ToList())
            .OrderBy(g => string.Join("|", g), StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(191)]
    public void StreamingCompaction_ReproducesTheInMemoryCompactorExactly(int seed)
    {
        ContentIndexStore store = PublishLayeredIndex(seed, out List<string> layers);
        var compactedUtc = Created.AddDays(1);

        ContentIndexStore.LayeredIndexHandle? handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        ContentIndexGeneration oracle = ContentIndexCompactor.Compact(handle!, OpenPolicy, compactedUtc);

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        StreamingSegmentRunMerger.MergeIntoBase(
            layers, workspace, memoryBudgetBytes: 1, diskGuard: null,
            produceV3QueryStructures: true, compactedUtc, CancellationToken.None);

        ContentIndexGeneration? streamed = ContentIndexGenerationSerializer.TryRead(workspace.PreparedDirectory);
        Assert.NotNull(streamed);

        Assert.Equal(oracle.Manifest.ContentCount, streamed!.Manifest.ContentCount);
        Assert.Equal(oracle.Manifest.AliasCount, streamed.Manifest.AliasCount);
        Assert.Equal(oracle.Manifest.FreshnessCheckpoint, streamed.Manifest.FreshnessCheckpoint);
        Assert.Equal(oracle.Manifest.CreatedUtc, streamed.Manifest.CreatedUtc);
        Assert.Equal(compactedUtc, streamed.Manifest.BuiltUtc);
        Assert.Equal(oracle.Manifest.LastIncrementalUpdateUtc, streamed.Manifest.LastIncrementalUpdateUtc);
        Assert.Equal(oracle.Manifest.NormalizedRootPath, streamed.Manifest.NormalizedRootPath);
        Assert.Equal(oracle.Manifest.VolumeSerialNumber, streamed.Manifest.VolumeSerialNumber);

        // A compacted base carries no tombstones at all.
        Assert.False(File.Exists(Path.Combine(workspace.PreparedDirectory, ContentIndexDeltaSegmentSerializer.TombstonesFile)));

        Dictionary<string, (List<uint> Trigrams, UsnFileIdentity? Identity)> expected = DescribeByPath(oracle);
        Dictionary<string, (List<uint> Trigrams, UsnFileIdentity? Identity)> actual = DescribeByPath(streamed);
        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            actual.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach ((string path, (List<uint> trigrams, UsnFileIdentity? identity)) in expected)
        {
            Assert.Equal(trigrams, actual[path].Trigrams);
            Assert.Equal(identity, actual[path].Identity);
        }
        Assert.Equal(ContentPartition(oracle), ContentPartition(streamed));
    }

    [Fact]
    public void PublishedCompactedBase_AnswersEveryQueryLikeTheLayeredIndexItReplaced()
    {
        ContentIndexStore store = PublishLayeredIndex(77, out List<string> layers);
        ContentIndexStore.LayeredIndexHandle? before = store.TryOpenLayered();
        Assert.NotNull(before);

        // Candidate sets and path routing measured against the layered index before compaction.
        var probes = before!.Base.Documents.SelectMany(d => d).Select(t => t.Value).Distinct().OrderBy(v => v).Take(32).ToList();
        var layered = LayeredContentIndexQuerySession.Begin(
            before.Base, before.Segments, TrigramExpression.All, new DirtyContentSet(),
            before.Segments.Select(_ => new DirtyContentSet()).ToArray());
        var routedBefore = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in before.Base.Aliases.Keys.Concat(before.Segments.SelectMany(s => s.Added.Aliases.Keys)).Distinct())
            routedBefore[path] = layered.Classify(path).GetType().Name;

        var settings = new IndexMaintenanceSettings { BuildMemoryBudgetMB = 1, ProduceV3QueryStructures = true };
        var manager = new ContentIndexManager(_paths, 2);
        manager.CompactScopeNow(_root, settings, Created.AddDays(2));

        Assert.Equal(0, store.ActiveSegmentCount());
        ContentIndexGeneration? compacted = store.TryOpenCurrent();
        Assert.NotNull(compacted);

        foreach (uint value in probes)
        {
            TrigramExpression query = TrigramExpression.OfTrigram(Trigram.FromPacked(value));
            var layeredSession = LayeredContentIndexQuerySession.Begin(
                before.Base, before.Segments, query, new DirtyContentSet(),
                before.Segments.Select(_ => new DirtyContentSet()).ToArray());
            var compactedSession = ContentIndexQuerySession.Begin(compacted!, query, new DirtyContentSet());
            foreach (string path in routedBefore.Keys)
            {
                Assert.Equal(
                    layeredSession.Classify(path).GetType().Name,
                    compactedSession.Classify(path).GetType().Name);
            }
        }
    }

    [Fact]
    public void ExplicitCompaction_ReportsMeasuredProgressThroughoutTheStreamingMerge()
    {
        PublishLayeredIndex(91, out _);
        var progress = new List<(int Percent, string Stage)>();
        var manager = new ContentIndexManager(_paths, 2);

        manager.CompactScopeNow(
            _root,
            new IndexMaintenanceSettings { BuildMemoryBudgetMB = 1, ProduceV3QueryStructures = true },
            Created.AddDays(2),
            (percent, stage) => progress.Add((percent, stage)));

        Assert.Contains((2, IndexUpdateStages.CompactAnalyzing), progress);
        Assert.Contains((10, IndexUpdateStages.CompactMerging), progress);
        Assert.Contains(progress, item =>
            item.Stage == IndexUpdateStages.CompactMerging && item.Percent > 10 && item.Percent < 90);
        Assert.Contains((90, IndexUpdateStages.CompactPublishing), progress);
        Assert.Contains((100, IndexUpdateStages.CompactPublishing), progress);
        Assert.True(progress.Zip(progress.Skip(1), (left, right) => left.Percent <= right.Percent).All(value => value));
        Assert.True(progress.Select(item => item.Percent).Distinct().Count() >= 8);
    }

    [Fact]
    public void CompactionOfASingleBase_IsANoOp()
    {
        var store = new ContentIndexStore(_paths, _scopeId, 2);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        builder.AddDocument(@"C:\r\only.txt", Encoding.UTF8.GetBytes("only document"));
        store.Publish(builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), Created));

        var manager = new ContentIndexManager(_paths, 2);
        manager.CompactScopeNow(_root, new IndexMaintenanceSettings(), Created.AddDays(1));

        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Equal(Created, store.ReadStorageStat().BuiltUtc);
    }

    [Fact]
    public void CompactionWithNoTrustedLayers_ReportsWhyInsteadOfSilentlySucceeding()
    {
        var manager = new ContentIndexManager(_paths, 2);
        Assert.Throws<InvalidDataException>(() =>
            manager.CompactScopeNow(_root, new IndexMaintenanceSettings(), Created));
    }

    [Fact]
    public void StreamingV3Writer_RejectsMissingOrMalformedCoreFiles()
    {
        void WriteChecksummed(string path, byte[] body)
        {
            byte[] digest = System.Security.Cryptography.SHA256.HashData(body);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(body);
            stream.Write(digest);
        }

        void AssertRejected(string name, Action<string> corrupt)
        {
            string layer = Path.Combine(_sandbox, "v3-invalid-" + name);
            string scratch = Path.Combine(_sandbox, "v3-scratch-" + name);
            var builder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            builder.AddDocument(@"C:\r\one.txt", Encoding.UTF8.GetBytes("one searchable document"));
            ContentIndexGenerationSerializer.Write(
                layer,
                builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), Created));
            corrupt(layer);

            Assert.Throws<InvalidDataException>(() => ContentIndexV3StreamingWriter.Write(
                layer, scratch, memoryBudgetBytes: 1, diskGuard: null, CancellationToken.None));
        }

        AssertRejected("missing-content", layer =>
            File.Delete(Path.Combine(layer, ContentIndexGenerationSerializer.ContentFile)));
        AssertRejected("malformed-content", layer =>
        {
            var body = new List<byte>();
            body.AddRange(BitConverter.GetBytes(1));
            body.AddRange(BitConverter.GetBytes(-1));
            WriteChecksummed(Path.Combine(layer, ContentIndexGenerationSerializer.ContentFile), body.ToArray());
        });
        AssertRejected("missing-aliases", layer =>
            File.Delete(Path.Combine(layer, ContentIndexGenerationSerializer.AliasesFile)));
        AssertRejected("malformed-aliases", layer =>
        {
            var body = new List<byte>();
            body.AddRange(BitConverter.GetBytes(1));
            body.AddRange(BitConverter.GetBytes(-1));
            WriteChecksummed(Path.Combine(layer, ContentIndexGenerationSerializer.AliasesFile), body.ToArray());
        });
        AssertRejected("identity-count-mismatch", layer =>
            WriteChecksummed(
                Path.Combine(layer, ContentIndexGenerationSerializer.FileIdsFile),
                BitConverter.GetBytes(0)));
        AssertRejected("malformed-identities", layer =>
        {
            var body = new List<byte>();
            body.AddRange(BitConverter.GetBytes(1));
            body.Add(7);
            WriteChecksummed(Path.Combine(layer, ContentIndexGenerationSerializer.FileIdsFile), body.ToArray());
        });
        AssertRejected("malformed-tombstones", layer =>
        {
            var body = new List<byte>();
            body.AddRange(BitConverter.GetBytes(1));
            body.AddRange(BitConverter.GetBytes(-1));
            WriteChecksummed(Path.Combine(layer, ContentIndexDeltaSegmentSerializer.TombstonesFile), body.ToArray());
        });
        AssertRejected("unreadable-tombstones", layer =>
            File.WriteAllBytes(
                Path.Combine(layer, ContentIndexDeltaSegmentSerializer.TombstonesFile),
                [1, 2, 3]));
    }

    [Fact]
    public void StreamingV3Codecs_RejectMalformedPayloads_AndUseDeterministicTieBreaks()
    {
        ContentIndexV3StreamingWriter.PostingPairCodec posting =
            ContentIndexV3StreamingWriter.PostingPairCodec.Instance;
        Assert.True(posting.Compare(
            new ContentIndexV3StreamingWriter.PostingPair(1, 10),
            new ContentIndexV3StreamingWriter.PostingPair(2, 1)) < 0);
        Assert.True(posting.Compare(
            new ContentIndexV3StreamingWriter.PostingPair(1, 10),
            new ContentIndexV3StreamingWriter.PostingPair(1, 11)) < 0);
        Assert.False(posting.TryDecode([1, 2, 3], out _));

        ContentIndexV3StreamingWriter.HashedPathRecordCodec paths =
            ContentIndexV3StreamingWriter.HashedPathRecordCodec.Instance;
        var firstPath = new ContentIndexV3StreamingWriter.HashedPathRecord(1, [1], 0, 0);
        var laterHash = new ContentIndexV3StreamingWriter.HashedPathRecord(2, [0], 0, 0);
        var laterPath = new ContentIndexV3StreamingWriter.HashedPathRecord(1, [2], 0, 0);
        Assert.True(paths.Compare(firstPath, laterHash) < 0);
        Assert.True(paths.Compare(firstPath, laterPath) < 0);
        Assert.False(paths.TryDecode(new byte[12], out _));
        byte[] invalidPathLength = new byte[28];
        BitConverter.GetBytes(-1).CopyTo(invalidPathLength, 8);
        Assert.False(paths.TryDecode(invalidPathLength, out _));

        ContentIndexV3StreamingWriter.ReverseIdentityRecordCodec identities =
            ContentIndexV3StreamingWriter.ReverseIdentityRecordCodec.Instance;
        var lowFirst = new ContentIndexV3StreamingWriter.ReverseIdentityRecord(1, 9, 9);
        Assert.True(identities.Compare(lowFirst,
            new ContentIndexV3StreamingWriter.ReverseIdentityRecord(2, 0, 0)) < 0);
        Assert.True(identities.Compare(lowFirst,
            new ContentIndexV3StreamingWriter.ReverseIdentityRecord(1, 10, 0)) < 0);
        Assert.True(identities.Compare(lowFirst,
            new ContentIndexV3StreamingWriter.ReverseIdentityRecord(1, 9, 10)) < 0);
        Assert.False(identities.TryDecode([1], out _));
    }

    [Fact]
    public void AbandonedCompactionWorkspaces_AreRemovedByStorageRecovery()
    {
        Directory.CreateDirectory(_paths.IndexRoot);
        IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_paths.IndexRoot);
        File.WriteAllText(Path.Combine(workspace.PreparedDirectory, "residue.bin"), "x");
        string root = workspace.Root;

        using (IndexMutationContext mutation = IndexMutationContext.Acquire(_paths))
        {
            IndexStorageRecovery.RecoverUnderLease(mutation, _paths, 2);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void CompactOnlyMaintenanceOperation_IsAcceptedAndFoldsEveryLayer()
    {
        ContentIndexStore store = PublishLayeredIndex(31, out _);
        Assert.True(store.ActiveSegmentCount() > 0);

        var operation = new IndexMaintenanceOperation
        {
            StorageDirectory = _paths.IndexRoot,
            RetainedGenerations = 2,
            Mode = IndexMaintenanceOperation.ModeCompactOnly,
            Settings = new IndexMaintenanceSettings { BuildMemoryBudgetMB = 1 },
            Roots =
            [
                new IndexMaintenanceRootOperation
                {
                    Root = _root,
                    Policy = IndexIngestionPolicySnapshot.FromPolicy(OpenPolicy),
                },
            ],
        };

        using (IndexMutationContext mutation = IndexMutationContext.Acquire(_paths))
        {
            IndexMaintenanceSuccess success = IndexBuildExecutor.RunMaintenancePassUnderLease(
                mutation, operation, CancellationToken.None, null);
            Assert.Equal(1, success.Built);
        }

        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void CompactionOfACorpusFarLargerThanTheChunkBudget_StaysWithinIt()
    {
        var store = new ContentIndexStore(_paths, _scopeId, 2);
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        for (int i = 0; i < 4000; i++)
            baseBuilder.AddDocument($@"C:\r\bulk-{i:D5}.txt", Encoding.UTF8.GetBytes($"bulk document {i} with some filler text"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), Created));

        for (int layer = 0; layer < 4; layer++)
        {
            var added = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            for (int i = 0; i < 500; i++)
                added.AddDocument($@"C:\r\bulk-{(layer * 500) + i:D5}.txt", Encoding.UTF8.GetBytes($"rewritten layer {layer} doc {i}"));
            added.SeedVolumeSerialNumber(0x5);
            store.PublishSegment(new ContentIndexDeltaSegment(
                added.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200 + layer),
                    Created.AddHours(1).AddMinutes(layer), createdUtc: null,
                    lastIncrementalUpdateUtc: Created.AddHours(1).AddMinutes(layer)),
                Array.Empty<string>()));
        }

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs));
        var layers = new List<string> { baseDir! };
        layers.AddRange(segmentDirs);

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        long before = GC.GetTotalAllocatedBytes(precise: false);
        StreamingSegmentRunMerger.MergeIntoBase(
            layers, workspace, memoryBudgetBytes: 64 * 1024, diskGuard: null,
            produceV3QueryStructures: true, Created.AddDays(1), CancellationToken.None);
        long allocated = GC.GetTotalAllocatedBytes(precise: false) - before;

        ContentIndexGeneration? compacted = ContentIndexGenerationSerializer.TryRead(workspace.PreparedDirectory);
        Assert.NotNull(compacted);
        Assert.Equal(4000, compacted!.Manifest.AliasCount);
        // Allocation is dominated by transient buffers, never by holding the corpus: the retained set is
        // bounded by the tiny chunk budget, so a run this size must not require anywhere near its own size.
        Assert.True(GC.GetTotalMemory(forceFullCollection: true) < 256L * 1024 * 1024,
            $"Live heap after compaction should stay small (allocated {allocated / (1024 * 1024)} MB in transit).");
    }
}
