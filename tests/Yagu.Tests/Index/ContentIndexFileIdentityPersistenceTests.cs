using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests that a generation captures and persists each content's durable file identity as
/// <c>fileids.bin</c> and reconstructs a <see cref="FileIdMap"/> from it (plan §3.4/§3.5), plus that a
/// real <see cref="ContentIndexManager"/> build captures identities + a freshness checkpoint. Runs under
/// a per-test temp sandbox (§9.2).
/// </summary>
public sealed class ContentIndexFileIdentityPersistenceTests : IDisposable
{
    private readonly string _sandbox;

    public ContentIndexFileIdentityPersistenceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-fileids", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    /// <summary>A deterministic fake identity provider keyed by normalized path.</summary>
    private static Func<string, FileIdentity?> FakeIdentities(ulong volumeSerial, out Dictionary<string, UsnFileIdentity> assigned)
    {
        var map = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        assigned = map;
        ulong next = 1000;
        return path =>
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!map.TryGetValue(norm, out var id))
            {
                id = new UsnFileIdentity(next++, 0);
                map[norm] = id;
            }
            return new FileIdentity(volumeSerial, id);
        };
    }

    [Fact]
    public void Generation_RoundTripsFileIdentitiesAndVolumeSerial()
    {
        var provider = FakeIdentities(0x99, out var assigned);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: provider);
        long a = builder.AddDocument(@"C:\root\a.txt", Encoding.UTF8.GetBytes("the planner trigram query"));
        long b = builder.AddDocument(@"C:\root\b.txt", Encoding.UTF8.GetBytes("another planner keyword file"));
        Assert.Equal(0, a);
        Assert.Equal(1, b);
        var generation = builder.Build("scope", "vol", @"C:\root", UsnCheckpoint.None, DateTimeOffset.UtcNow);

        string dir = Path.Combine(_sandbox, "gen");
        ContentIndexGenerationSerializer.Write(dir, generation);
        var read = ContentIndexGenerationSerializer.TryRead(dir);

        Assert.NotNull(read);
        Assert.Equal(0x99UL, read!.Manifest.VolumeSerialNumber);

        var map = read.BuildFileIdMap();
        Assert.Equal(0x99UL, map.VolumeSerialNumber);
        Assert.True(map.TryGetContentId(assigned[IndexScopeIdentity.NormalizePath(@"C:\root\a.txt")], out long ca));
        Assert.Equal(0, ca);
        Assert.True(map.TryGetContentId(assigned[IndexScopeIdentity.NormalizePath(@"C:\root\b.txt")], out long cb));
        Assert.Equal(1, cb);
    }

    [Fact]
    public void FileIdMap_ResolvesUsnChangesAfterRoundTrip()
    {
        var provider = FakeIdentities(0x1, out var assigned);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: provider);
        builder.AddDocument(@"C:\r\x.txt", Encoding.UTF8.GetBytes("hello world content"));
        builder.AddDocument(@"C:\r\y.txt", Encoding.UTF8.GetBytes("second document here"));
        var generation = builder.Build("s", "v", @"C:\r", UsnCheckpoint.None, DateTimeOffset.UtcNow);

        string dir = Path.Combine(_sandbox, "gen2");
        ContentIndexGenerationSerializer.Write(dir, generation);
        var map = ContentIndexGenerationSerializer.TryRead(dir)!.BuildFileIdMap();

        // Simulate a journal change to only x.txt.
        var changedId = assigned[IndexScopeIdentity.NormalizePath(@"C:\r\x.txt")];
        var dirty = new DirtyContentSet();
        map.ResolveDirty(new[] { new UsnChange(changedId, 0x100) }, dirty);

        Assert.True(dirty.IsDirty(0));   // x.txt (content 0) changed
        Assert.False(dirty.IsDirty(1));  // y.txt (content 1) untouched
    }

    [Fact]
    public void TryReadFreshnessInputs_MatchesFullGeneration_WithoutLoadingContent()
    {
        var provider = FakeIdentities(0x7, out var assigned);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: provider);
        builder.AddDocument(@"C:\r\x.txt", Encoding.UTF8.GetBytes("hello world content"));
        builder.AddDocument(@"C:\r\y.txt", Encoding.UTF8.GetBytes("second document here"));
        var generation = builder.Build("s", "v", @"C:\r", new UsnCheckpoint(3, 300), DateTimeOffset.UtcNow);

        string dir = Path.Combine(_sandbox, "gen-fresh");
        ContentIndexGenerationSerializer.Write(dir, generation);

        // The lightweight read (manifest + fileids only, no content.bin) must reproduce the manifest's
        // checkpoint/root + a FileIdMap identical to the full generation's BuildFileIdMap — the exact input
        // the staleness check consumes, so IsScopeStale stays behavior-identical while skipping content.bin.
        var inputs = ContentIndexGenerationSerializer.TryReadFreshnessInputs(dir);
        Assert.NotNull(inputs);
        Assert.Equal(new UsnCheckpoint(3, 300), inputs!.Value.Manifest.FreshnessCheckpoint);
        Assert.Equal(IndexScopeIdentity.NormalizePath(@"C:\r"), inputs.Value.Manifest.NormalizedRootPath);

        FileIdMap full = generation.BuildFileIdMap();
        FileIdMap light = inputs.Value.FileIds;
        Assert.Equal(full.Count, light.Count);
        Assert.Equal(full.VolumeSerialNumber, light.VolumeSerialNumber);

        var changedId = assigned[IndexScopeIdentity.NormalizePath(@"C:\r\x.txt")];
        var dirty = new DirtyContentSet();
        light.ResolveDirty(new[] { new UsnChange(changedId, 0x100) }, dirty);
        Assert.True(dirty.IsDirty(0));   // x.txt resolved via the lightweight FileIdMap
        Assert.False(dirty.IsDirty(1));
    }

    [Fact]
    public void QueryModeRead_StreamsPostings_IdenticalToRetainRead()
    {
        var provider = FakeIdentities(0x5, out _);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries for content"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("another document mentioning the planner and queries"));
        builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("wholly unrelated lorem ipsum dolor sit amet"));
        var generation = builder.Build("s", "v", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);

        string dir = Path.Combine(_sandbox, "gen-stream");
        ContentIndexGenerationSerializer.Write(dir, generation);

        // The query-mode read (retainDocuments:false) streams content.bin straight into the postings and
        // stores NO documents; its postings must evaluate byte-identically to the retain read's Build path.
        var retain = ContentIndexGenerationSerializer.TryRead(dir, retainDocuments: true)!;
        var streamed = ContentIndexGenerationSerializer.TryRead(dir, retainDocuments: false)!;

        Assert.NotEmpty(retain.Documents);
        Assert.Empty(streamed.Documents);
        Assert.Equal(retain.Postings.DocumentCount, streamed.Postings.DocumentCount);
        Assert.Equal(retain.Postings.TrigramCount, streamed.Postings.TrigramCount);

        foreach (string term in new[] { "planner", "queries", "content", "lorem", "zzqx" })
        {
            var options = new SearchOptions { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
            if (TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options)) is TrigramPlan.Eligible eligible)
                Assert.True(
                    retain.Postings.EvaluateSet(eligible.Query).SetEquals(streamed.Postings.EvaluateSet(eligible.Query)),
                    $"Streamed vs retain postings differ for '{term}'.");
        }
    }

    [Fact]
    public void QueryModeRead_CorruptContentFailsClosed()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: FakeIdentities(0x5, out _));
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        ContentIndexGeneration generation = builder.Build(
            "s", "v", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        string directory = Path.Combine(_sandbox, "gen-stream-corrupt");
        ContentIndexGenerationSerializer.Write(directory, generation);

        string contentPath = Path.Combine(directory, ContentIndexGenerationSerializer.ContentFile);
        byte[] bytes = File.ReadAllBytes(contentPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(contentPath, bytes);

        Assert.Null(ContentIndexGenerationSerializer.TryRead(directory, retainDocuments: false));
    }

    [Fact]
    public void NullIdentityProvider_ProducesEmptyFileIdMap()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy); // no identity provider
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("some indexed text"));
        var generation = builder.Build("s", "v", @"C:\r", UsnCheckpoint.None, DateTimeOffset.UtcNow);

        Assert.Equal(0UL, generation.Manifest.VolumeSerialNumber);
        Assert.Equal(0, generation.BuildFileIdMap().Count);

        // Still round-trips (fileids.bin is all-absent) and stays queryable.
        string dir = Path.Combine(_sandbox, "gen3");
        ContentIndexGenerationSerializer.Write(dir, generation);
        var read = ContentIndexGenerationSerializer.TryRead(dir);
        Assert.NotNull(read);
        Assert.Equal(0, read!.BuildFileIdMap().Count);
        Assert.Equal(1, read.AliasCount);
    }

    [Fact]
    public void RealBuild_CapturesFileIdentitiesForEveryIndexedFile()
    {
        string corpus = Path.Combine(_sandbox, "corpus");
        string indexRoot = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "a.txt"), "the planner produces trigram queries",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(corpus, "b.txt"), "another file mentioning the planner",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        var manager = new ContentIndexManager(paths);
        var result = manager.BuildScope(corpus, new IndexIngestionPolicy(0, null, null, true, false, 0));

        var store = new ContentIndexStore(paths, result.ScopeId);
        var generation = store.TryOpenCurrent();
        Assert.NotNull(generation);

        // Real files on the NTFS temp volume all resolve to durable identities, keyed by one volume.
        var map = generation!.BuildFileIdMap();
        Assert.Equal(2, map.Count);
        Assert.NotEqual(0UL, generation.Manifest.VolumeSerialNumber);
    }
}
