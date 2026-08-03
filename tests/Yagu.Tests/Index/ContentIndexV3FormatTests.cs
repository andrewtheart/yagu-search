using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Parity + integrity tests for the <b>format-v3 query structures</b> (plan §5.1, Stage 1). The managed
/// reference reader must reproduce the in-memory generation's answers EXACTLY — trigram-query candidate
/// sets, path→(aliasId,contentId) resolution, and forward/reverse file-identity lookups — because the Rust
/// worker reads the identical bytes. Corruption of a header or a body block must be caught (→ live-scan).
/// </summary>
public sealed class ContentIndexV3FormatTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root = @"C:\v3";

    public ContentIndexV3FormatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-index-v3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration BuildGeneration(out Dictionary<string, UsnFileIdentity> identities, out IReadOnlyList<string> paths)
    {
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        ulong next = 5000;
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out var id)) { id = new UsnFileIdentity(next++, next); assigned[norm] = id; }
            return new FileIdentity(0x7, id);
        }

        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        var pathList = new List<string>();
        void Add(string rel, string text)
        {
            string full = _root + "\\" + rel;
            builder.AddDocument(full, Encoding.UTF8.GetBytes(text));
            pathList.Add(IndexScopeIdentity.NormalizePath(full));
        }

        Add("a.txt", "the planner produces trigram queries");
        Add("b.txt", "nothing whatsoever of interest here");
        Add("c.txt", "another planner mentions trigram indexing");
        Add("d.txt", "unrelated filler content and words");

        var gen = builder.Build(
            ContentIndexManager.ScopeIdForRoot(_root), "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        identities = assigned;
        paths = pathList;
        return gen;
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\v3", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    [Fact]
    public void Postings_ReproduceTheGenerationReference_ForEveryQuery()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        ContentIndexV3Format.Write(_dir, gen);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        Assert.NotNull(reader);
        Assert.Equal(gen.Postings.DocumentCount, reader.DocumentCount);

        foreach (string term in new[] { "planner", "trigram", "queries", "indexing", "whatsoever", "absentxyz", "the" })
        {
            TrigramExpression q = PlanQuery(term);
            IReadOnlySet<int> expected = gen.Postings.EvaluateSet(q);
            IReadOnlySet<int> actual = reader.EvaluateSet(q);
            Assert.True(expected.SetEquals(actual),
                $"posting parity mismatch for '{term}': expected [{string.Join(",", expected.Order())}] got [{string.Join(",", actual.Order())}]");
        }
    }

    [Fact]
    public void PathLookup_MatchesTryGetAlias_WithCollisionVerification()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out IReadOnlyList<string> paths);
        ContentIndexV3Format.Write(_dir, gen);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        Assert.Equal(gen.AliasCount, reader.PathCount);

        foreach (string norm in paths)
        {
            Assert.True(gen.TryGetAlias(norm, out long expAlias, out long expContent));
            Assert.True(reader.TryLookupPath(norm, out long gotAlias, out long gotContent), $"v3 path lookup missed '{norm}'");
            Assert.Equal(expAlias, gotAlias);
            Assert.Equal(expContent, gotContent);
        }

        // A path not in the index resolves to false (never a false positive from a hash collision).
        Assert.False(reader.TryLookupPath(IndexScopeIdentity.NormalizePath(_root + "\\missing.txt"), out _, out _));
    }

    [Fact]
    public void Identities_ForwardAndReverse_RoundTrip()
    {
        ContentIndexGeneration gen = BuildGeneration(out Dictionary<string, UsnFileIdentity> identities, out IReadOnlyList<string> paths);
        ContentIndexV3Format.Write(_dir, gen);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;

        foreach (string norm in paths)
        {
            Assert.True(gen.TryGetAlias(norm, out _, out long contentId));
            UsnFileIdentity expected = identities[norm];

            UsnFileIdentity? forward = reader.TryGetIdentity((int)contentId);
            Assert.Equal(expected, forward);

            Assert.True(reader.TryReverseIdentity(expected, out int reverseContentId));
            Assert.Equal((int)contentId, reverseContentId);
        }

        // A fabricated identity that no content has → not found.
        Assert.False(reader.TryReverseIdentity(new UsnFileIdentity(999_999_999UL, 1UL), out _));
        // An out-of-range content id → no identity.
        Assert.Null(reader.TryGetIdentity(int.MaxValue));
    }

    [Fact]
    public void WindowedViews_ReproduceEveryAnswer_LikeTheWholeFileMapping()
    {
        // Force the x86 bounded-window path even on this x64 box: every posting/path/identity answer must be
        // identical to the whole-file mapping, proving the windowed reader is a drop-in on a 32-bit build.
        ContentIndexGeneration gen = BuildGeneration(out Dictionary<string, UsnFileIdentity> identities, out IReadOnlyList<string> paths);
        ContentIndexV3Format.Write(_dir, gen);

        ContentIndexV3BlockFile.ForceWindowedViewsForTests = true;
        try
        {
            using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
            Assert.NotNull(reader);

            foreach (string term in new[] { "planner", "trigram", "queries", "indexing", "whatsoever", "absentxyz" })
            {
                TrigramExpression q = PlanQuery(term);
                Assert.True(gen.Postings.EvaluateSet(q).SetEquals(reader.EvaluateSet(q)), $"windowed posting parity mismatch for '{term}'");
            }

            foreach (string norm in paths)
            {
                Assert.True(gen.TryGetAlias(norm, out long expAlias, out long expContent));
                Assert.True(reader.TryLookupPath(norm, out long gotAlias, out long gotContent), $"windowed path lookup missed '{norm}'");
                Assert.Equal(expAlias, gotAlias);
                Assert.Equal(expContent, gotContent);

                UsnFileIdentity expected = identities[norm];
                Assert.Equal(expected, reader.TryGetIdentity((int)expContent));
                Assert.True(reader.TryReverseIdentity(expected, out int reverseContentId));
                Assert.Equal((int)expContent, reverseContentId);
            }
        }
        finally
        {
            ContentIndexV3BlockFile.ForceWindowedViewsForTests = false;
        }
    }

    [Fact]
    public void WindowedViews_CorruptBlock_ThrowsOnAccess()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        ContentIndexV3Format.Write(_dir, gen);

        string postingsPath = Path.Combine(_dir, ContentIndexV3Format.PostingsFile);
        byte[] bytes = File.ReadAllBytes(postingsPath);
        int bodyStart = 24 + /*blockCount*/ 1 * 8 + /*headerHash*/ 8;
        bytes[bodyStart] ^= 0xFF;
        File.WriteAllBytes(postingsPath, bytes);

        ContentIndexV3BlockFile.ForceWindowedViewsForTests = true;
        try
        {
            using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
            Assert.NotNull(reader); // header still verifies; the corruption is in a body block
            Assert.Throws<InvalidDataException>(() => reader.EvaluateSet(PlanQuery("planner")));
        }
        finally
        {
            ContentIndexV3BlockFile.ForceWindowedViewsForTests = false;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockFile_MultiBlockBody_StreamsAndReadsBackEveryByte(bool windowed)
    {
        // A body spanning several 64 KB blocks exercises the streamed write (no combined [header|body] array),
        // per-block hashing, and cross-block reads — the generation-based tests use tiny single-block bodies.
        string path = Path.Combine(_dir, "multiblock.v3");
        var body = new byte[ContentIndexV3BlockFile.BlockSize * 3 + 1234]; // 4 blocks (last partial)
        for (int i = 0; i < body.Length; i++) body[i] = (byte)((i * 31 + 7) & 0xFF);
        ContentIndexV3BlockFile.Write(path, 1, 1, body);

        ContentIndexV3BlockFile.ForceWindowedViewsForTests = windowed;
        try
        {
            using ContentIndexV3BlockFile bf = ContentIndexV3BlockFile.Open(path, 1, 1)!;
            Assert.NotNull(bf);
            Assert.Equal(body.Length, bf.BodyLength);
            Assert.True(bf.Body(0, body.Length).SequenceEqual(body));           // whole-body read (all blocks)
            int mid = ContentIndexV3BlockFile.BlockSize - 10;
            Assert.True(bf.Body(mid, 40).SequenceEqual(body.AsSpan(mid, 40)));  // spans the block-0/block-1 boundary
        }
        finally
        {
            ContentIndexV3BlockFile.ForceWindowedViewsForTests = false;
        }
    }

    [Fact]
    public void BlockFile_MultiBlockBody_CorruptMiddleBlock_ThrowsOnlyWhenThatBlockIsTouched()
    {
        string path = Path.Combine(_dir, "multiblock-corrupt.v3");
        var body = new byte[ContentIndexV3BlockFile.BlockSize * 3];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)((i * 17 + 3) & 0xFF);
        ContentIndexV3BlockFile.Write(path, 1, 1, body);

        byte[] raw = File.ReadAllBytes(path);
        int bodyStart = 24 + /*blockCount 3*/ 3 * 8 + /*headerHash*/ 8;
        raw[bodyStart + ContentIndexV3BlockFile.BlockSize * 2 + 5] ^= 0xFF; // corrupt block 2
        File.WriteAllBytes(path, raw);

        static byte FirstByte(ContentIndexV3BlockFile bf, long offset) => bf.Body(offset, 1)[0];

        using ContentIndexV3BlockFile file = ContentIndexV3BlockFile.Open(path, 1, 1)!;
        Assert.NotNull(file); // header still verifies
        _ = FirstByte(file, 0); // block 0 reads fine
        Assert.Throws<InvalidDataException>(() => FirstByte(file, ContentIndexV3BlockFile.BlockSize * 2));
    }

    [Fact]
    public void TryOpen_MissingFiles_ReturnsNull()
    {
        Assert.Null(ContentIndexV3Format.TryOpen(_dir)); // nothing written yet
        Assert.Null(ContentIndexV3Format.TryOpen(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void Open_CorruptHeader_ReturnsNull()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        ContentIndexV3Format.Write(_dir, gen);

        // Corrupt a byte inside the block-hash table (index 25), which the header hash covers → open must reject.
        string postingsPath = Path.Combine(_dir, ContentIndexV3Format.PostingsFile);
        byte[] bytes = File.ReadAllBytes(postingsPath);
        bytes[25] ^= 0xFF;
        File.WriteAllBytes(postingsPath, bytes);

        Assert.Null(ContentIndexV3Format.TryOpen(_dir));
    }

    [Fact]
    public void Body_CorruptBlock_ThrowsOnAccess_SoTheCallerLiveScans()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        ContentIndexV3Format.Write(_dir, gen);

        // Corrupt the FIRST body byte (past the header) — the header hash still verifies, so TryOpen succeeds,
        // but the body block's own hash now mismatches and the first lookup that touches it must throw.
        string postingsPath = Path.Combine(_dir, ContentIndexV3Format.PostingsFile);
        byte[] bytes = File.ReadAllBytes(postingsPath);
        int bodyStart = 24 + /*blockCount*/ 1 * 8 + /*headerHash*/ 8; // small body → exactly one block
        bytes[bodyStart] ^= 0xFF;
        File.WriteAllBytes(postingsPath, bytes);

        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        Assert.NotNull(reader); // header intact
        Assert.Throws<InvalidDataException>(() => reader.EvaluateSet(PlanQuery("planner")));
    }

    [Fact]
    public void Reader_IsMemoryMapped_DisposeReleasesTheFileAndRejectsFurtherUse()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        ContentIndexV3Format.Write(_dir, gen);
        string postings = Path.Combine(_dir, ContentIndexV3Format.PostingsFile);

        ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        _ = reader.EvaluateSet(PlanQuery("planner")); // maps + verifies a block on demand
        reader.Dispose();

        // Using a disposed reader fails fast rather than reading freed/unmapped memory.
        Assert.Throws<ObjectDisposedException>(() => reader.EvaluateSet(PlanQuery("planner")));
        // The mapping is released, so a rebuild/retention can delete the backing file.
        File.Delete(postings);
        Assert.False(File.Exists(postings));
    }

    // ── Tombstone index (plan §5.1 parallel tombstone index; Stage 2 slice 3a) ──

    private static IReadOnlySet<string> RemovedSet(params string[] normalizedPaths)
        => new HashSet<string>(normalizedPaths, StringComparer.Ordinal);

    [Fact]
    public void Tombstones_SegmentRoundTrip_ContainsRemovedButNotOthers()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        string gone1 = IndexScopeIdentity.NormalizePath(_root + "\\gone1.txt");
        string gone2 = IndexScopeIdentity.NormalizePath(_root + "\\sub\\gone2.log");
        ContentIndexV3Format.Write(_dir, gen, RemovedSet(gone1, gone2));

        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        Assert.True(reader.HasTombstoneIndex);
        Assert.True(reader.ContainsTombstone(gone1));
        Assert.True(reader.ContainsTombstone(gone2));
        // Collision-verify: a path that is not tombstoned is not reported even if it shares a prefix.
        Assert.False(reader.ContainsTombstone(IndexScopeIdentity.NormalizePath(_root + "\\gone1.txt.other")));
        // A live member is never tombstoned.
        Assert.False(reader.ContainsTombstone(IndexScopeIdentity.NormalizePath(_root + "\\a.txt")));
    }

    [Fact]
    public void Tombstones_BaseWrite_HasEmptyIndex_AndNothingIsTombstoned()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out IReadOnlyList<string> paths);
        ContentIndexV3Format.Write(_dir, gen); // base: no removed paths

        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        Assert.True(reader.HasTombstoneIndex); // an (empty) tombstone file is always written
        foreach (string p in paths)
            Assert.False(reader.ContainsTombstone(p));
    }

    [Fact]
    public void Tombstones_MissingFile_IsBackwardCompatible_WithNoTombstoneIndex()
    {
        // An older 3-file v3 (no tombstone sidecar) still opens; HasTombstoneIndex is false and the reader
        // reports nothing tombstoned (a layered mapped classifier requires the index before it may prune).
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        string gone = IndexScopeIdentity.NormalizePath(_root + "\\gone.txt");
        ContentIndexV3Format.Write(_dir, gen, RemovedSet(gone));
        File.Delete(Path.Combine(_dir, ContentIndexV3Format.TombstonesFile));

        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        Assert.NotNull(reader);
        Assert.False(reader.HasTombstoneIndex);
        Assert.False(reader.ContainsTombstone(gone));
    }

    [Fact]
    public void Tombstones_CorruptHeader_TryOpenReturnsNull()
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        ContentIndexV3Format.Write(_dir, gen, RemovedSet(IndexScopeIdentity.NormalizePath(_root + "\\gone.txt")));

        string tombPath = Path.Combine(_dir, ContentIndexV3Format.TombstonesFile);
        byte[] bytes = File.ReadAllBytes(tombPath);
        bytes[25] ^= 0xFF; // block-hash table byte, covered by the header hash
        File.WriteAllBytes(tombPath, bytes);

        // A present-but-corrupt tombstone index fails the whole open (safe: live-scan).
        Assert.Null(ContentIndexV3Format.TryOpen(_dir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tombstones_WindowedViews_MatchTheWholeFileMapping(bool windowed)
    {
        ContentIndexGeneration gen = BuildGeneration(out _, out _);
        string gone = IndexScopeIdentity.NormalizePath(_root + "\\gone.txt");
        ContentIndexV3Format.Write(_dir, gen, RemovedSet(gone));

        ContentIndexV3BlockFile.ForceWindowedViewsForTests = windowed;
        try
        {
            using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
            Assert.True(reader.ContainsTombstone(gone));
            Assert.False(reader.ContainsTombstone(IndexScopeIdentity.NormalizePath(_root + "\\a.txt")));
        }
        finally { ContentIndexV3BlockFile.ForceWindowedViewsForTests = false; }
    }
}
