using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Differential and safety tests for the bounded-memory merge of a run of incremental delta segments.
/// The in-memory <see cref="ContentIndexIncrementalUpdater.MergeSegmentRun"/> is the reference oracle: the
/// streaming merge must reproduce its newest-wins alias/tombstone semantics, its hard-link sharing, and its
/// manifest provenance exactly, and must leave the live pointer untouched when it cannot finish.
/// </summary>
public sealed class StreamingSegmentRunMergerTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public StreamingSegmentRunMergerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-stream-merge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexStore NewStore() => new(_paths, _scopeId, 2);

    private ContentIndexGeneration BuildBase()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        builder.AddDocument(@"C:\r\base.txt", Encoding.UTF8.GetBytes("base document content"));
        return builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    /// <summary>Builds a run of randomized incremental segments exercising replacement, deletion, rename,
    /// hard links, and uncapturable identities, then publishes them over a base.</summary>
    private ContentIndexStore PublishRandomizedRun(int seed, int layerCount, out List<string> segmentDirectories)
    {
        var random = new Random(seed);
        ContentIndexStore store = NewStore();
        store.Publish(BuildBase());

        var start = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        for (int layer = 0; layer < layerCount; layer++)
        {
            var added = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            var tombstones = new HashSet<string>(StringComparer.Ordinal);

            int documents = random.Next(1, 6);
            for (int d = 0; d < documents; d++)
            {
                // A small shared path space guarantees replacements across layers.
                string path = $@"C:\r\file-{random.Next(0, 12):D2}.txt";
                long contentId = added.AddDocument(
                    path, Encoding.UTF8.GetBytes($"layer {layer} doc {d} zephyrqux {random.Next()}"));
                if (contentId >= 0 && random.Next(4) == 0)
                    added.AddHardLink($@"C:\r\link-{layer}-{d}.txt", contentId); // hard link sharing content
            }

            // A document with no capturable identity at all.
            if (random.Next(3) == 0)
            {
                var noIdentity = new ContentIndexGenerationBuilder(OpenPolicy);
                _ = noIdentity; // documented intent: the builder below adds it without an identity provider
                added.AddClassifiedDocument(
                    $@"C:\r\anon-{layer}.txt",
                    IndexIngestionClassifier.ClassifyContent(Encoding.UTF8.GetBytes("anonymous content"), OpenPolicy).Trigrams,
                    identity: null);
            }

            int removals = random.Next(0, 3);
            for (int t = 0; t < removals; t++)
                tombstones.Add(IndexScopeIdentity.NormalizePath($@"C:\r\file-{random.Next(0, 12):D2}.txt"));

            // A rename: the old path is tombstoned while the new path is added.
            if (random.Next(3) == 0)
            {
                tombstones.Add(IndexScopeIdentity.NormalizePath($@"C:\r\renamed-from-{layer}.txt"));
                added.AddDocument($@"C:\r\renamed-to-{layer}.txt", Encoding.UTF8.GetBytes($"renamed {layer}"));
            }

            added.SeedVolumeSerialNumber(0x5);
            ContentIndexGeneration generation = added.Build(
                _scopeId, "vol", _root, new UsnCheckpoint(1, 200 + (layer * 10)), start.AddMinutes(layer),
                createdUtc: null, lastIncrementalUpdateUtc: start.AddMinutes(layer));
            store.PublishSegment(new ContentIndexDeltaSegment(generation, tombstones));
        }

        segmentDirectories = Enumerable.Range(1, layerCount)
            .Select(i => Path.Combine(store.ScopeDirectory, "segments", $"seg-{i:D6}"))
            .ToList();
        return store;
    }

    private ContentIndexDeltaSegment RunOracle(ContentIndexStore store, IReadOnlyList<string> directories)
    {
        var inputs = directories
            .Select(d => ContentIndexDeltaSegmentSerializer.TryRead(d, retainDocuments: true)!)
            .ToList();
        Assert.DoesNotContain(inputs, s => s is null);
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        return updater.MergeSegmentRun(inputs, CancellationToken.None);
    }

    private static Dictionary<string, (List<uint> Trigrams, UsnFileIdentity? Identity)> DescribeByPath(
        ContentIndexDeltaSegment segment)
    {
        var described = new Dictionary<string, (List<uint>, UsnFileIdentity?)>(StringComparer.Ordinal);
        foreach ((string path, (long _, long contentId)) in segment.Added.Aliases)
        {
            described[path] = (
                segment.Added.Documents[(int)contentId].Select(t => t.Value).OrderBy(v => v).ToList(),
                segment.Added.ContentIdentities[(int)contentId]);
        }
        return described;
    }

    private static List<HashSet<string>> ContentPartition(ContentIndexDeltaSegment segment)
    {
        var byContent = new Dictionary<long, HashSet<string>>();
        foreach ((string path, (long _, long contentId)) in segment.Added.Aliases)
        {
            if (!byContent.TryGetValue(contentId, out HashSet<string>? group))
                byContent[contentId] = group = new HashSet<string>(StringComparer.Ordinal);
            group.Add(path);
        }
        return byContent.Values
            .OrderBy(g => string.Join("|", g.OrderBy(p => p, StringComparer.Ordinal)), StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(11)]
    [InlineData(2027)]
    [InlineData(883311)]
    public void StreamingMerge_ReproducesTheInMemoryOracle_OnRandomizedIncrementalLayers(int seed)
    {
        ContentIndexStore store = PublishRandomizedRun(seed, layerCount: 5, out List<string> directories);
        ContentIndexDeltaSegment oracle = RunOracle(store, directories);

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        PreparedIndexLayer prepared = StreamingSegmentRunMerger.Merge(
            directories, workspace, memoryBudgetBytes: 1, diskGuard: null,
            produceV3QueryStructures: true, CancellationToken.None);

        ContentIndexDeltaSegment? streamed = ContentIndexDeltaSegmentSerializer.TryRead(prepared.Directory);
        Assert.NotNull(streamed);

        Assert.Equal(oracle.Added.Manifest.ContentCount, streamed!.Added.Manifest.ContentCount);
        Assert.Equal(oracle.Added.Manifest.AliasCount, streamed.Added.Manifest.AliasCount);
        Assert.Equal(oracle.Added.Manifest.FreshnessCheckpoint, streamed.Added.Manifest.FreshnessCheckpoint);
        Assert.Equal(oracle.Added.Manifest.NormalizedRootPath, streamed.Added.Manifest.NormalizedRootPath);
        Assert.Equal(oracle.Added.Manifest.ScopeId, streamed.Added.Manifest.ScopeId);
        Assert.Equal(oracle.Added.Manifest.BuiltUtc, streamed.Added.Manifest.BuiltUtc);
        Assert.Equal(oracle.Added.Manifest.CreatedUtc, streamed.Added.Manifest.CreatedUtc);
        Assert.Equal(oracle.Added.Manifest.LastIncrementalUpdateUtc, streamed.Added.Manifest.LastIncrementalUpdateUtc);
        Assert.Equal(oracle.Added.Manifest.VolumeSerialNumber, streamed.Added.Manifest.VolumeSerialNumber);

        Assert.Equal(
            oracle.RemovedPaths.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            streamed.RemovedPaths.OrderBy(p => p, StringComparer.Ordinal).ToList());

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

        // Hard links must still share one merged content id.
        Assert.Equal(
            ContentPartition(oracle).Select(g => g.OrderBy(p => p, StringComparer.Ordinal).ToList()).ToList(),
            ContentPartition(streamed).Select(g => g.OrderBy(p => p, StringComparer.Ordinal).ToList()).ToList());
    }

    [Fact]
    public void StreamingMerge_ProducesV3StructuresThatAnswerLikeTheInMemoryWriter()
    {
        ContentIndexStore store = PublishRandomizedRun(4242, layerCount: 4, out List<string> directories);
        ContentIndexDeltaSegment oracle = RunOracle(store, directories);

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        PreparedIndexLayer prepared = StreamingSegmentRunMerger.Merge(
            directories, workspace, memoryBudgetBytes: 1, diskGuard: null,
            produceV3QueryStructures: true, CancellationToken.None);

        string reference = Path.Combine(_sandbox, "reference-v3");
        Directory.CreateDirectory(reference);
        ContentIndexV3Format.Write(reference, oracle.Added, (IReadOnlySet<string>)oracle.RemovedPaths.ToHashSet(StringComparer.Ordinal));

        using ContentIndexV3Reader? streamedReader = ContentIndexV3Format.TryOpen(prepared.Directory);
        using ContentIndexV3Reader? referenceReader = ContentIndexV3Format.TryOpen(reference);
        Assert.NotNull(streamedReader);
        Assert.NotNull(referenceReader);

        ContentIndexDeltaSegment? streamed = ContentIndexDeltaSegmentSerializer.TryRead(prepared.Directory);
        Assert.NotNull(streamed);

        // Path lookups resolve to the same alias/content the segment itself records.
        foreach ((string path, (long aliasId, long contentId)) in streamed!.Added.Aliases)
        {
            Assert.True(streamedReader!.TryLookupPath(path, out long readAliasId, out long readContentId));
            Assert.Equal(aliasId, readAliasId);
            Assert.Equal(contentId, readContentId);
        }
        Assert.False(streamedReader!.TryLookupPath(@"c:\r\never-indexed.txt", out _, out _));

        foreach (string removed in streamed.RemovedPaths)
            Assert.True(streamedReader.ContainsTombstone(removed));

        // Postings answer identically to the segment's own posting index for every trigram in the corpus.
        var trigrams = streamed.Added.Documents
            .SelectMany(d => d)
            .Select(t => t.Value)
            .Distinct()
            .OrderBy(v => v)
            .Take(64)
            .ToList();
        foreach (uint value in trigrams)
        {
            TrigramExpression query = TrigramExpression.OfTrigram(Trigram.FromPacked(value));
            Assert.Equal(
                streamed.Added.Postings.EvaluateSet(query).OrderBy(id => id).ToList(),
                streamedReader.EvaluateSet(query).OrderBy(id => id).ToList());
        }

        // Identities round-trip both ways.
        for (int contentId = 0; contentId < streamed.Added.Manifest.ContentCount; contentId++)
        {
            UsnFileIdentity? expected = streamed.Added.ContentIdentities[contentId];
            Assert.Equal(expected, streamedReader.TryGetIdentity(contentId));
            if (expected is { } identity)
            {
                Assert.True(streamedReader.TryReverseIdentity(identity, out int reversedContentId));
                Assert.Equal(
                    streamed.Added.ContentIdentities[reversedContentId],
                    identity);
            }
        }

        Assert.Equal(referenceReader!.HasTombstoneIndex, streamedReader.HasTombstoneIndex);
    }

    [Fact]
    public void IncrementalRunSelection_NeverPicksFullBuildPagingLayers()
    {
        ContentIndexStore store = NewStore();
        var createdUtc = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);
        var baseBuilder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        baseBuilder.AddDocument(@"C:\r\base.txt", Encoding.UTF8.GetBytes("base"));
        store.Publish(baseBuilder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), createdUtc));

        for (int i = 0; i < 6; i++)
        {
            var page = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            page.AddDocument($@"C:\r\page-{i}.txt", Encoding.UTF8.GetBytes($"page {i}"));
            store.PublishSegment(new ContentIndexDeltaSegment(
                page.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), createdUtc.AddMinutes(i),
                    lastIncrementalUpdateUtc: null),
                Array.Empty<string>()));
        }

        store.DirectorySizeReader = _ => 1;
        Assert.False(store.TryFindIncrementalSegmentRun(4, 32, 1024, 4096, out _));
        // The provenance-agnostic selector would happily merge those disjoint pages.
        Assert.True(store.TryFindSmallSegmentRun(4, 32, 1024, 4096, out _));

        for (int i = 0; i < 4; i++)
        {
            var incremental = new ContentIndexDeltaSegmentBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            incremental.AddChangedDocument($@"C:\r\changed-{i}.txt", Encoding.UTF8.GetBytes($"changed {i}"));
            store.PublishSegment(incremental.Build(
                _scopeId, "vol", _root, new UsnCheckpoint(1, 200 + i), createdUtc.AddHours(1).AddMinutes(i)));
        }

        Assert.True(store.TryFindIncrementalSegmentRun(4, 32, 1024, 4096, out ContentIndexStore.SegmentCoalesceRun? run));
        Assert.Equal(6, run!.StartIndex);
        Assert.Equal(new[] { "seg-000007", "seg-000008", "seg-000009", "seg-000010" }, run.SegmentIds);
    }

    [Fact]
    public void PreparedSegmentPublication_ReplacesTheRun_AndKeepsEveryPathQueryable()
    {
        ContentIndexStore store = PublishRandomizedRun(7, layerCount: 4, out List<string> directories);
        ContentIndexDeltaSegment oracle = RunOracle(store, directories);
        int before = store.ActiveSegmentCount();

        store.DirectorySizeReader = _ => 1;
        Assert.True(store.TryFindIncrementalSegmentRun(4, 32, 1024, 4096, out ContentIndexStore.SegmentCoalesceRun? run));

        // The workspace must be created while the writer lease is already held: acquiring the lease runs
        // crash recovery, which removes any abandoned compaction workspace it finds.
        using (IndexMutationContext mutation = IndexMutationContext.Acquire(_paths))
        {
            using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
            StreamingSegmentRunMerger.Merge(
                run!.SegmentDirectories, workspace, memoryBudgetBytes: 4096, diskGuard: null,
                produceV3QueryStructures: false, CancellationToken.None);
            Assert.True(store.TryReplacePreparedSegmentRunUnderLease(mutation, run, workspace.PreparedDirectory));
        }

        Assert.Equal(before - 3, store.ActiveSegmentCount());
        ContentIndexStore.LayeredIndexHandle? handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.Single(handle!.Segments);
        foreach (string path in oracle.Added.Aliases.Keys)
            Assert.True(handle.Segments[0].Added.TryGetAlias(path, out _, out _));
    }

    [Fact]
    public void CancellationBeforePublication_LeavesTheActivePointerUntouched()
    {
        ContentIndexStore store = PublishRandomizedRun(99, layerCount: 4, out List<string> directories);
        int before = store.ActiveSegmentCount();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        Assert.Throws<OperationCanceledException>(() => StreamingSegmentRunMerger.Merge(
            directories, workspace, memoryBudgetBytes: 1, diskGuard: null,
            produceV3QueryStructures: false, cts.Token));

        Assert.Equal(before, store.ActiveSegmentCount());
        Assert.NotNull(store.TryOpenLayered());
    }

    [Fact]
    public void DiskGuardAbort_LeavesTheActivePointerUntouched_AndDiscardsTheWorkspace()
    {
        ContentIndexStore store = PublishRandomizedRun(1234, layerCount: 4, out List<string> directories);
        int before = store.ActiveSegmentCount();
        var guard = new IndexCompactionDiskGuard(
            _sandbox, minimumFreeSpaceMB: 1, maxDiskUsagePercent: 0,
            probe: _ => new IndexVolumeSpace("X:\\", 1000, 0));

        string workspaceRoot;
        using (IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox))
        {
            workspaceRoot = workspace.Root;
            Assert.Throws<IndexCompactionDiskGuardException>(() => StreamingSegmentRunMerger.Merge(
                directories, workspace, memoryBudgetBytes: 1, diskGuard: guard,
                produceV3QueryStructures: false, CancellationToken.None));
        }

        Assert.False(Directory.Exists(workspaceRoot));
        Assert.Equal(before, store.ActiveSegmentCount());
        Assert.NotNull(store.TryOpenLayered());
    }

    [Fact]
    public void InputsDisagreeingOnScopeOrCheckpointOrder_AreRejectedBeforeAnyWrite()
    {
        ContentIndexStore store = NewStore();
        store.Publish(BuildBase());
        var first = new ContentIndexDeltaSegmentBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        first.AddChangedDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("a"));
        store.PublishSegment(first.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 300), DateTimeOffset.UtcNow));
        var second = new ContentIndexDeltaSegmentBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        second.AddChangedDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("b"));
        store.PublishSegment(second.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow));

        var directories = new List<string>
        {
            Path.Combine(store.ScopeDirectory, "segments", "seg-000001"),
            Path.Combine(store.ScopeDirectory, "segments", "seg-000002"),
        };

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        Assert.Throws<InvalidDataException>(() => StreamingSegmentRunMerger.Merge(
            directories, workspace, memoryBudgetBytes: 4096, diskGuard: null,
            produceV3QueryStructures: false, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(workspace.PreparedDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Merge_RequiresAtLeastTwoLayers(int layerCount)
    {
        string[] directories = Enumerable.Range(0, layerCount)
            .Select(index => Path.Combine(_sandbox, $"unused-{index}"))
            .ToArray();
        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);

        Assert.Throws<ArgumentException>(() => StreamingSegmentRunMerger.Merge(
            directories,
            workspace,
            memoryBudgetBytes: 4096,
            diskGuard: null,
            produceV3QueryStructures: false,
            CancellationToken.None));
    }

    /// <summary>A run of layers whose every document carries a capturable identity, so a corrupted trailing
    /// byte stays structurally valid and only the layer's digest can detect it.</summary>
    private ContentIndexStore PublishIdentifiedRun(out List<string> segmentDirectories)
    {
        ContentIndexStore store = NewStore();
        store.Publish(BuildBase());
        var start = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
        for (int layer = 0; layer < 2; layer++)
        {
            var added = new ContentIndexDeltaSegmentBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
            for (int d = 0; d < 3; d++)
            {
                added.AddChangedDocument(
                    $@"C:\r\layer-{layer}-doc-{d}.txt",
                    Encoding.UTF8.GetBytes($"layer {layer} document {d} zephyrqux content"));
            }
            store.PublishSegment(added.Build(
                _scopeId, "vol", _root, new UsnCheckpoint(1, 200 + layer), start.AddMinutes(layer)));
        }

        segmentDirectories = Enumerable.Range(1, 2)
            .Select(i => Path.Combine(store.ScopeDirectory, "segments", $"seg-{i:D6}"))
            .ToList();
        return store;
    }

    /// <summary>Flips the last body byte, leaving every record boundary and count intact so only the
    /// trailing SHA-256 can prove the file is no longer what was published.</summary>
    private static void CorruptTrailingBodyByte(string checksummedFilePath)
    {
        byte[] bytes = File.ReadAllBytes(checksummedFilePath);
        int bodyEnd = bytes.Length - ChecksummedFile.DigestBytes;
        Assert.True(bodyEnd > 4, $"'{checksummedFilePath}' has no body to corrupt.");
        bytes[bodyEnd - 1] ^= 0xFF;
        File.WriteAllBytes(checksummedFilePath, bytes);
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("missing-aliases")]
    [InlineData("missing-content")]
    [InlineData("missing-fileids")]
    [InlineData("malformed-aliases")]
    [InlineData("malformed-tombstones")]
    [InlineData("dangling-content")]
    [InlineData("dangling-identity")]
    public void MissingMalformedOrDanglingLayerRecords_FailBeforePublication(string defect)
    {
        ContentIndexStore store = PublishIdentifiedRun(out List<string> directories);
        int before = store.ActiveSegmentCount();
        string newest = directories[^1];

        static void WriteChecksummed(string path, byte[] body)
        {
            byte[] digest = System.Security.Cryptography.SHA256.HashData(body);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(body);
            stream.Write(digest);
        }

        switch (defect)
        {
            case "missing-manifest":
                File.Delete(Path.Combine(newest, ContentIndexGenerationSerializer.ManifestFile));
                break;
            case "missing-aliases":
                File.Delete(Path.Combine(newest, ContentIndexGenerationSerializer.AliasesFile));
                break;
            case "missing-content":
                File.Delete(Path.Combine(newest, ContentIndexGenerationSerializer.ContentFile));
                break;
            case "missing-fileids":
                File.Delete(Path.Combine(newest, ContentIndexGenerationSerializer.FileIdsFile));
                break;
            case "malformed-aliases":
            {
                var body = new List<byte>();
                body.AddRange(BitConverter.GetBytes(1));
                body.AddRange(BitConverter.GetBytes(-1));
                WriteChecksummed(Path.Combine(newest, ContentIndexGenerationSerializer.AliasesFile), body.ToArray());
                break;
            }
            case "malformed-tombstones":
            {
                var body = new List<byte>();
                body.AddRange(BitConverter.GetBytes(1));
                body.AddRange(BitConverter.GetBytes(-1));
                WriteChecksummed(Path.Combine(newest, ContentIndexDeltaSegmentSerializer.TombstonesFile), body.ToArray());
                break;
            }
            case "dangling-content":
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(@"C:\r\dangling.txt");
                var body = new List<byte>();
                body.AddRange(BitConverter.GetBytes(1));
                body.AddRange(BitConverter.GetBytes(pathBytes.Length));
                body.AddRange(pathBytes);
                body.AddRange(BitConverter.GetBytes(0L));
                body.AddRange(BitConverter.GetBytes(999L));
                WriteChecksummed(Path.Combine(newest, ContentIndexGenerationSerializer.AliasesFile), body.ToArray());
                break;
            }
            case "dangling-identity":
                WriteChecksummed(
                    Path.Combine(newest, ContentIndexGenerationSerializer.FileIdsFile),
                    BitConverter.GetBytes(0));
                break;
            default:
                throw new InvalidOperationException($"Unknown test defect '{defect}'.");
        }

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        Assert.Throws<InvalidDataException>(() => StreamingSegmentRunMerger.Merge(
            directories,
            workspace,
            memoryBudgetBytes: 4096,
            diskGuard: null,
            produceV3QueryStructures: false,
            CancellationToken.None));

        Assert.Equal(before, store.ActiveSegmentCount());
        Assert.NotNull(store.TryOpenLayered());
    }

    [Fact]
    public void MergerSpoolCodecs_RejectMalformedPayloads_AndUseEveryTieBreak()
    {
        StreamingSegmentRunMerger.PathDecisionCodec decisions =
            StreamingSegmentRunMerger.PathDecisionCodec.Instance;
        var firstDecision = new StreamingSegmentRunMerger.PathDecision("a", 0, 0, 0);
        Assert.True(decisions.Compare(firstDecision,
            new StreamingSegmentRunMerger.PathDecision("b", 0, 0, 0)) < 0);
        Assert.True(decisions.Compare(firstDecision,
            new StreamingSegmentRunMerger.PathDecision("a", 1, 0, 0)) < 0);
        Assert.True(decisions.Compare(firstDecision,
            new StreamingSegmentRunMerger.PathDecision("a", 0, 1, 0)) < 0);
        Assert.False(decisions.TryDecode(new byte[16], out _));
        byte[] invalidDecision = new byte[17];
        BitConverter.GetBytes(-1).CopyTo(invalidDecision, 0);
        Assert.False(decisions.TryDecode(invalidDecision, out _));

        StreamingSegmentRunMerger.AliasAssignmentCodec assignments =
            StreamingSegmentRunMerger.AliasAssignmentCodec.Instance;
        var firstAssignment = new StreamingSegmentRunMerger.AliasAssignment(0, 1, "a");
        Assert.True(assignments.Compare(firstAssignment,
            new StreamingSegmentRunMerger.AliasAssignment(1, 0, "a")) < 0);
        Assert.True(assignments.Compare(firstAssignment,
            new StreamingSegmentRunMerger.AliasAssignment(0, 2, "a")) < 0);
        Assert.True(assignments.Compare(firstAssignment,
            new StreamingSegmentRunMerger.AliasAssignment(0, 1, "b")) < 0);
        Assert.False(assignments.TryDecode(new byte[15], out _));
        byte[] invalidAssignment = new byte[16];
        BitConverter.GetBytes(-1).CopyTo(invalidAssignment, 12);
        Assert.False(assignments.TryDecode(invalidAssignment, out _));

        StreamingSegmentRunMerger.MergedAliasCodec merged =
            StreamingSegmentRunMerger.MergedAliasCodec.Instance;
        Assert.False(merged.TryDecode(new byte[31], out _));
        byte[] invalidMerged = new byte[32];
        BitConverter.GetBytes(-1).CopyTo(invalidMerged, 28);
        Assert.False(merged.TryDecode(invalidMerged, out _));

        StreamingSegmentRunMerger.PathOnlyRecordCodec paths =
            StreamingSegmentRunMerger.PathOnlyRecordCodec.Instance;
        Assert.False(paths.TryDecode(new byte[3], out _));
        byte[] invalidPath = new byte[4];
        BitConverter.GetBytes(-1).CopyTo(invalidPath, 0);
        Assert.False(paths.TryDecode(invalidPath, out _));
    }

    [Theory]
    [InlineData(ContentIndexGenerationSerializer.ContentFile)]
    [InlineData(ContentIndexGenerationSerializer.FileIdsFile)]
    public void SilentlyCorruptSourceLayer_FailsTheMerge_InsteadOfBeingRepublishedAsTrustedData(string fileName)
    {
        ContentIndexStore store = PublishIdentifiedRun(out List<string> directories);
        int before = store.ActiveSegmentCount();
        CorruptTrailingBodyByte(Path.Combine(directories[^1], fileName));

        using IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => StreamingSegmentRunMerger.Merge(
            directories, workspace, memoryBudgetBytes: 4096, diskGuard: null,
            produceV3QueryStructures: false, CancellationToken.None));

        Assert.Contains(fileName, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, store.ActiveSegmentCount());
    }

    /// <summary>
    /// The bulk output files are the largest thing a merge creates, so they must consume the same
    /// configured headroom as its spool. Sized from a permissive run: the second guard has room for the
    /// spool plus only half the prepared layer, so it can only abort if those writes are charged.
    /// </summary>
    [Fact]
    public void PreparedLayerWrites_ConsumeTheConfiguredDiskHeadroom()
    {
        ContentIndexStore store = PublishIdentifiedRun(out List<string> directories);
        int before = store.ActiveSegmentCount();
        const long Total = 1_000_000_000_000;

        var permissive = new IndexCompactionDiskGuard(
            _sandbox, minimumFreeSpaceMB: 1, maxDiskUsagePercent: 0,
            probe: _ => new IndexVolumeSpace("X:\\", Total, Total));
        long charged;
        long preparedBytes;
        using (IndexCompactionWorkspace measuring = IndexCompactionWorkspace.Create(_sandbox))
        {
            PreparedIndexLayer prepared = StreamingSegmentRunMerger.Merge(
                directories, measuring, memoryBudgetBytes: 4096, diskGuard: permissive,
                produceV3QueryStructures: false, CancellationToken.None);
            preparedBytes = new DirectoryInfo(prepared.Directory).GetFiles().Sum(file => file.Length);
            charged = permissive.BytesCreated;
        }

        Assert.True(preparedBytes > 0);
        Assert.True(charged > preparedBytes, $"charged {charged} bytes should cover the {preparedBytes}-byte layer plus spool.");

        long floorBytes = 1024 * 1024;
        long available = floorBytes + charged - (preparedBytes / 2);
        IndexCompactionDiskGuard? shrinking = null;
        shrinking = new IndexCompactionDiskGuard(
            _sandbox, minimumFreeSpaceMB: 1, maxDiskUsagePercent: 0,
            probe: _ => new IndexVolumeSpace("X:\\", Total, Math.Max(0, available - shrinking!.BytesCreated)));

        string workspaceRoot;
        using (IndexCompactionWorkspace constrained = IndexCompactionWorkspace.Create(_sandbox))
        {
            workspaceRoot = constrained.Root;
            Assert.Throws<IndexCompactionDiskGuardException>(() => StreamingSegmentRunMerger.Merge(
                directories, constrained, memoryBudgetBytes: 4096, diskGuard: shrinking,
                produceV3QueryStructures: false, CancellationToken.None));
        }

        Assert.False(Directory.Exists(workspaceRoot));
        Assert.Equal(before, store.ActiveSegmentCount());
    }
}
