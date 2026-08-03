using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the on-disk generation format, transactional publication, dual checksummed pointer slots,
/// corruption→fallback recovery, and retention (plan §3.4). Every test runs under a unique injected
/// sandbox via <see cref="IContentIndexPathProvider"/>, so no real Yagu index is ever touched (§9.2).
/// </summary>
public sealed class ContentIndexStoreTests : IDisposable
{
    [Fact]
    public void NextSequence_AdvancesOrRejectsInvalidAndExhaustedCounters()
    {
        Assert.Equal(1, ContentIndexStore.NextSequence(0, "generation"));
        Assert.Equal(long.MaxValue, ContentIndexStore.NextSequence(long.MaxValue - 1, "segment"));
        Assert.Throws<InvalidDataException>(() => ContentIndexStore.NextSequence(-1, "pointer"));
        Assert.Throws<InvalidDataException>(() => ContentIndexStore.NextSequence(long.MaxValue, "pointer"));
    }

    private readonly string _sandbox;
    private readonly IContentIndexPathProvider _paths;

    public ContentIndexStoreTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        // Custom-directory provider rooted at the sandbox (never the real LocalAppData index).
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static ContentIndexGeneration BuildGeneration(params (string Path, string Text)[] docs)
    {
        var builder = new ContentIndexGenerationBuilder(new IndexIngestionPolicy(0, null, null, true, false, 0));
        foreach (var (path, text) in docs)
            builder.AddDocument(path, Encoding.UTF8.GetBytes(text));
        return builder.Build("scope-id", "vol", @"C:\src", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanLiteral(string literal)
        => Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(
            new EffectiveSearchPattern(literal, isRegex: false, caseSensitive: true, multiline: false, dotAll: false))).Query;

    private ContentIndexStore NewStore(int retained = 2) => new(_paths, "scope-id", retained);

    // ─────────────────────────── serializer round-trip ───────────────────────────

    [Fact]
    public void Serializer_RoundTrips_GenerationData()
    {
        var gen = BuildGeneration((@"C:\src\a.txt", "hello planner"), (@"C:\src\b.txt", "world content"));
        string dir = Path.Combine(_sandbox, "roundtrip");
        ContentIndexGenerationSerializer.Write(dir, gen);

        var loaded = ContentIndexGenerationSerializer.TryRead(dir);
        Assert.NotNull(loaded);
        Assert.Equal(gen.AliasCount, loaded!.AliasCount);
        Assert.Equal(gen.Manifest, loaded.Manifest);
        // Query results are identical after reload.
        var query = PlanLiteral("planner");
        Assert.Equal(gen.Postings.EvaluateSet(query), loaded.Postings.EvaluateSet(query));
    }

    [Fact]
    public void Serializer_MissingDirectory_ReturnsNull()
        => Assert.Null(ContentIndexGenerationSerializer.TryRead(Path.Combine(_sandbox, "nope")));

    [Fact]
    public void Serializer_CorruptFile_ReturnsNull()
    {
        var gen = BuildGeneration((@"C:\src\a.txt", "hello planner"));
        string dir = Path.Combine(_sandbox, "corrupt");
        ContentIndexGenerationSerializer.Write(dir, gen);

        // Flip a byte in the content file → checksum mismatch → null.
        string content = Path.Combine(dir, ContentIndexGenerationSerializer.ContentFile);
        byte[] bytes = File.ReadAllBytes(content);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(content, bytes);

        Assert.Null(ContentIndexGenerationSerializer.TryRead(dir));
    }

    // ─────────────────────────── publish + open ───────────────────────────

    [Fact]
    public void Publish_ThenOpen_ReturnsQueryableGeneration()
    {
        var store = NewStore();
        var result = store.Publish(BuildGeneration((@"C:\src\a.txt", "the planner is here")));
        Assert.Equal("gen-000001", result.GenerationId);
        Assert.Equal(1, result.Sequence);

        var opened = store.TryOpenCurrent();
        Assert.NotNull(opened);
        var session = ContentIndexQuerySession.Begin(opened!, PlanLiteral("planner"), new DirtyContentSet());
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(IndexScopeIdentity.NormalizePath(@"C:\src\a.txt")));
    }

    // ─────────────────────── format-v3 query structures (plan §5.1, opt-in) ───────────────────────

    [Fact]
    public void Publish_WithV3Enabled_WritesQueryStructuresTransactionally_AndTheyRoundTrip()
    {
        var gen = BuildGeneration((@"C:\src\a.txt", "the planner is here"), (@"C:\src\b.txt", "nothing at all"));
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        var result = store.Publish(gen);

        string scopeDir = _paths.GetScopeDirectory("scope-id");
        string[] v3 = Directory.GetFiles(scopeDir, ContentIndexV3Format.PostingsFile, SearchOption.AllDirectories);
        Assert.Single(v3); // exactly one base generation carries the structures
        string genDir = Path.GetDirectoryName(v3[0])!;
        Assert.EndsWith(result.GenerationId, genDir); // they landed in the published generation dir (atomic move)

        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(genDir)!;
        Assert.NotNull(reader);
        // Round-trips against the same generation: postings + path lookup.
        TrigramExpression query = PlanLiteral("planner");
        Assert.True(gen.Postings.EvaluateSet(query).SetEquals(reader.EvaluateSet(query)));
        Assert.True(gen.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\src\a.txt"), out long expAlias, out long expContent));
        Assert.True(reader.TryLookupPath(IndexScopeIdentity.NormalizePath(@"C:\src\a.txt"), out long gotAlias, out long gotContent));
        Assert.Equal(expAlias, gotAlias);
        Assert.Equal(expContent, gotContent);
    }

    [Fact]
    public void Publish_WithV3Disabled_WritesNoQueryStructures_DefaultBehaviorUnchanged()
    {
        var store = NewStore(); // ProduceV3QueryStructures defaults false
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content")));

        string scopeDir = _paths.GetScopeDirectory("scope-id");
        Assert.Empty(Directory.GetFiles(scopeDir, ContentIndexV3Format.PostingsFile, SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(scopeDir, ContentIndexV3Format.PathIndexFile, SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(scopeDir, ContentIndexV3Format.IdentitiesFile, SearchOption.AllDirectories));
        // The generation still opens normally — v3 absence never affects the existing query path.
        Assert.NotNull(store.TryOpenCurrent());
    }

    [Fact]
    public void Publish_WhenV3WriteFails_CleansPartialStructuresAndPublishesTheBase()
    {
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.V3Published)
            {
                string tempGenerationDir = Path.Combine(
                    store.ScopeDirectory,
                    "generations",
                    ".gen-000001.tmp");
                Directory.CreateDirectory(Path.Combine(tempGenerationDir, ContentIndexV3Format.TombstonesFile));
                throw new IOException("injected v3 publish failure");
            }
        };
        try
        {
            PublishResult result = store.Publish(BuildGeneration((@"C:\src\a.txt", "content")));

            string generationDir = Path.Combine(store.ScopeDirectory, "generations", result.GenerationId);
            Assert.NotNull(store.TryOpenCurrent());
            Assert.Empty(Directory.GetFiles(generationDir, "query-*.v3*", SearchOption.TopDirectoryOnly));
            Assert.True(Directory.Exists(Path.Combine(generationDir, ContentIndexV3Format.TombstonesFile)));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }
    }

    [Fact]
    public void Publish_WhenStagedBaseFailsValidation_RemovesItAndLeavesPointerUnchanged()
    {
        var store = NewStore();
        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.BaseWritten)
            {
                string tempGenerationDir = Directory.GetDirectories(
                    Path.Combine(store.ScopeDirectory, "generations"),
                    ".gen-*.tmp").Single();
                File.WriteAllBytes(
                    Path.Combine(tempGenerationDir, ContentIndexGenerationSerializer.ContentFile),
                    new byte[] { 1, 2, 3 });
            }
        };
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                store.Publish(BuildGeneration((@"C:\src\a.txt", "content"))));

            Assert.Null(store.TryOpenCurrent());
            Assert.Empty(Directory.GetDirectories(
                Path.Combine(store.ScopeDirectory, "generations"),
                ".gen-*.tmp"));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }
    }

    [Fact]
    public void Publish_WhenV3WriteRunsOutOfMemory_RethrowsAndDoesNotPublish()
    {
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.V3Published)
                throw new OutOfMemoryException("injected v3 exhaustion");
        };
        try
        {
            Assert.Throws<OutOfMemoryException>(() =>
                store.Publish(BuildGeneration((@"C:\src\a.txt", "content"))));
            Assert.Null(store.TryOpenCurrent());
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }
    }

    [Fact]
    public void Open_NoGeneration_ReturnsNull()
        => Assert.Null(NewStore().TryOpenCurrent());

    [Fact]
    public void Publish_WritesBothPointerSlotsOverTime()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\a.txt", "first")));
        store.Publish(BuildGeneration((@"C:\src\b.txt", "second")));

        Assert.True(File.Exists(Path.Combine(store.ScopeDirectory, "current.a")));
        Assert.True(File.Exists(Path.Combine(store.ScopeDirectory, "current.b")));
        // Newest generation wins.
        var opened = store.TryOpenCurrent();
        Assert.NotNull(opened);
        Assert.True(opened!.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\src\b.txt"), out _, out _));
    }

    // ─────────────────────────── corruption → recover older generation ───────────────────────────

    [Fact]
    public void Open_NewestGenerationCorrupt_RecoversPriorGeneration()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\old.txt", "old planner content")));   // gen-000001
        store.Publish(BuildGeneration((@"C:\src\new.txt", "new planner content")));   // gen-000002

        // Corrupt the newest generation's content file.
        string newestContent = Path.Combine(store.ScopeDirectory, "generations", "gen-000002", ContentIndexGenerationSerializer.ContentFile);
        byte[] bytes = File.ReadAllBytes(newestContent);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(newestContent, bytes);

        var opened = store.TryOpenCurrent();
        Assert.NotNull(opened);
        // Falls back to the prior valid generation (which has old.txt, not new.txt).
        Assert.True(opened!.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\src\old.txt"), out _, out _));
        Assert.False(opened.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\src\new.txt"), out _, out _));
    }

    [Fact]
    public void Open_CorruptPointerSlot_IsIgnored()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content one")));  // → slot a
        store.Publish(BuildGeneration((@"C:\src\b.txt", "content two")));  // → slot b

        // Corrupt the newest pointer slot (b). The store must ignore it and recover via the other slot.
        string slotB = Path.Combine(store.ScopeDirectory, "current.b");
        File.WriteAllText(slotB, "corrupt garbage");

        var opened = store.TryOpenCurrent();
        Assert.NotNull(opened);
        Assert.True(opened!.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\src\a.txt"), out _, out _));
    }

    [Fact]
    public void Open_LegacyPointerWithBadDigestInvalidSequenceOrReadFailure_IsIgnored()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content")));
        string slot = Path.Combine(store.ScopeDirectory, "current.a");
        string generationId = "gen-000001";

        WriteLegacySlot(slot, "1", generationId, digestOverride: "bad");
        Assert.Null(store.TryOpenCurrent());

        WriteLegacySlot(slot, "not-a-sequence", generationId);
        Assert.Null(store.TryOpenCurrent());

        WriteLegacySlot(slot, "1", generationId);
        using (new FileStream(slot, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(store.TryOpenCurrent());
        Assert.NotNull(store.TryOpenCurrent());
    }

    [Fact]
    public void ReadStorageStat_IncompatibleRepresentation_RecoversRootAndExactReason()
    {
        var store = NewStore();
        ContentIndexGeneration generation = BuildGeneration((@"C:\src\a.txt", "content one"));
        PublishResult published = store.Publish(generation);
        string generationDir = Path.Combine(store.ScopeDirectory, "generations", published.GenerationId);
        IndexManifest legacy = generation.Manifest with
        {
            ContentRepresentationVersion = ContentRepresentation.Version - 1,
        };
        ChecksummedFile.Write(
            Path.Combine(generationDir, ContentIndexGenerationSerializer.ManifestFile),
            Encoding.UTF8.GetBytes(legacy.Serialize()));

        StoredIndexStat stat = store.ReadStorageStat();

        Assert.False(stat.Readable);
        Assert.Equal(IndexStorageHealth.IncompatibleRepresentation, stat.Health);
        Assert.Equal(@"C:\src", stat.RootPath);
        Assert.Contains($"v{legacy.ContentRepresentationVersion}", stat.Problem);
        Assert.Contains($"v{ContentRepresentation.Version}", stat.Problem);
    }

    [Fact]
    public void ReadStorageStat_IncompatibleBaseFormatAndWrongScope_AreDiagnosed()
    {
        var store = NewStore();
        ContentIndexGeneration generation = BuildGeneration((@"C:\src\a.txt", "content one"));
        PublishResult published = store.Publish(generation);
        string generationDir = Path.Combine(store.ScopeDirectory, "generations", published.GenerationId);

        IndexManifest incompatible = generation.Manifest with
        {
            IndexFormatVersion = IndexManifest.CurrentFormatVersion - 1,
            CreatedUtc = null,
        };
        ChecksummedFile.Write(
            Path.Combine(generationDir, ContentIndexGenerationSerializer.ManifestFile),
            Encoding.UTF8.GetBytes(incompatible.Serialize()));
        StoredIndexStat incompatibleStat = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.IncompatibleFormat, incompatibleStat.Health);
        Assert.Equal(@"C:\src", incompatibleStat.RootPath);

        IndexManifest wrongScope = generation.Manifest with { ScopeId = "another-scope" };
        ChecksummedFile.Write(
            Path.Combine(generationDir, ContentIndexGenerationSerializer.ManifestFile),
            Encoding.UTF8.GetBytes(wrongScope.Serialize()));
        StoredIndexStat wrongScopeStat = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, wrongScopeStat.Health);
        Assert.Null(wrongScopeStat.RootPath);
        Assert.Contains("another scope", wrongScopeStat.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadStorageStat_MissingBaseManifestWithValidPointer_IsDiagnosed()
    {
        var store = NewStore();
        PublishResult published = store.Publish(BuildGeneration((@"C:\src\a.txt", "content one")));
        File.Delete(Path.Combine(
            store.ScopeDirectory,
            "generations",
            published.GenerationId,
            ContentIndexGenerationSerializer.ManifestFile));

        StoredIndexStat stat = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, stat.Health);
        Assert.Null(stat.RootPath);
        Assert.Contains("manifest", stat.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadStorageStat_CorruptPointers_RecoversRootFromChecksumValidGeneration()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content one")));
        File.WriteAllText(Path.Combine(store.ScopeDirectory, "current.a"), "damaged pointer");

        StoredIndexStat stat = store.ReadStorageStat();

        Assert.False(stat.Readable);
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, stat.Health);
        Assert.Equal(@"C:\src", stat.RootPath);
        Assert.Contains("pointer", stat.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadStorageStat_RecoverySkipsInvalidNewerDirectoryAndPreservesIncompatibility()
    {
        var store = NewStore();
        ContentIndexGeneration generation = BuildGeneration((@"C:\src\a.txt", "content one"));
        PublishResult published = store.Publish(generation);
        string generations = Path.Combine(store.ScopeDirectory, "generations");
        string generationDir = Path.Combine(generations, published.GenerationId);
        string invalidNewer = Path.Combine(generations, "gen-999999");
        Directory.CreateDirectory(invalidNewer);
        File.WriteAllText(Path.Combine(store.ScopeDirectory, "current.a"), "damaged pointer");
        store.ExistingGenerationDirectoriesReader = () => [invalidNewer, generationDir];

        StoredIndexStat recovered = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, recovered.Health);
        Assert.Equal(@"C:\src", recovered.RootPath);

        IndexManifest incompatible = generation.Manifest with
        {
            IndexFormatVersion = IndexManifest.CurrentFormatVersion - 1,
            CreatedUtc = null,
        };
        ChecksummedFile.Write(
            Path.Combine(generationDir, ContentIndexGenerationSerializer.ManifestFile),
            Encoding.UTF8.GetBytes(incompatible.Serialize()));
        StoredIndexStat incompatibleRecovered = store.ReadStorageStat();
        Assert.Equal(IndexStorageHealth.IncompatibleFormat, incompatibleRecovered.Health);
        Assert.Equal(@"C:\src", incompatibleRecovered.RootPath);
    }

    [Fact]
    public void ReadStorageStat_RecoveryEnumerationIoAndAccessFailures_ReturnUnidentifiedResidue()
    {
        foreach (Exception failure in new Exception[]
                 {
                     new IOException("enumeration failed"),
                     new UnauthorizedAccessException("enumeration denied"),
                 })
        {
            var store = NewStore();
            store.Publish(BuildGeneration((@"C:\src\a.txt", "content one")));
            File.WriteAllText(Path.Combine(store.ScopeDirectory, "current.a"), "damaged pointer");
            store.ExistingGenerationDirectoriesReader = () => throw failure;

            StoredIndexStat stat = store.ReadStorageStat();
            Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, stat.Health);
            Assert.Null(stat.RootPath);
            Assert.Contains("identify", stat.Problem, StringComparison.OrdinalIgnoreCase);
            store.DeleteScope();
        }
    }

    [Fact]
    public void ReadStorageStat_NoRecoverableManifest_ReportsUnidentifiedResidue()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.ScopeDirectory);
        File.WriteAllText(Path.Combine(store.ScopeDirectory, "current.a"), "damaged pointer");

        StoredIndexStat stat = store.ReadStorageStat();

        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, stat.Health);
        Assert.Null(stat.RootPath);
        Assert.Contains("identify", stat.Problem, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── retention ───────────────────────────

    [Fact]
    public void Retention_DeletesOldestUnreferencedGeneration()
    {
        var store = NewStore(retained: 1);
        store.Publish(BuildGeneration((@"C:\src\1.txt", "one")));   // gen-000001
        store.Publish(BuildGeneration((@"C:\src\2.txt", "two")));   // gen-000002
        store.Publish(BuildGeneration((@"C:\src\3.txt", "three"))); // gen-000003

        string gensDir = Path.Combine(store.ScopeDirectory, "generations");
        var remaining = Directory.GetDirectories(gensDir).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("gen-000001", remaining); // oldest, unreferenced → deleted
        Assert.Contains("gen-000003", remaining);        // newest always kept
    }

    // ─────────────────────────── delete ───────────────────────────

    [Fact]
    public void DeleteScope_RemovesEverything()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content")));
        Assert.True(Directory.Exists(store.ScopeDirectory));

        store.DeleteScope();
        Assert.False(Directory.Exists(store.ScopeDirectory));
        Assert.Null(store.TryOpenCurrent());
    }

    [Fact]
    public void DeleteScope_LockedFileIsBestEffort_AndLaterRetrySucceeds()
    {
        var store = NewStore();
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content")));
        string lockedPath = Path.Combine(store.ScopeDirectory, "locked.bin");
        File.WriteAllText(lockedPath, "locked");

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            store.DeleteScope();
        Assert.True(Directory.Exists(store.ScopeDirectory));

        store.DeleteScope();
        Assert.False(Directory.Exists(store.ScopeDirectory));
    }

    [Fact]
    public void StorageDiagnosticHelpers_MapEveryVerdictAndRejectInvalidScopeMetadata()
    {
        IndexManifest manifest = BuildGeneration((@"C:\src\a.txt", "content")).Manifest;

        Assert.Equal(IndexStorageHealth.Healthy, ContentIndexStore.HealthFor(IndexStructuralVerdict.Trusted));
        Assert.Equal(IndexStorageHealth.IncompatibleFormat, ContentIndexStore.HealthFor(IndexStructuralVerdict.IncompatibleFormat));
        Assert.Equal(IndexStorageHealth.IncompatibleRepresentation, ContentIndexStore.HealthFor(IndexStructuralVerdict.IncompatibleRepresentation));
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, ContentIndexStore.HealthFor(IndexStructuralVerdict.Missing));
        Assert.Equal(IndexStorageHealth.CorruptOrIncomplete, ContentIndexStore.HealthFor(IndexStructuralVerdict.Corrupt));

        Assert.Contains("index format", ContentIndexStore.ProblemFor(IndexStructuralVerdict.IncompatibleFormat, manifest));
        Assert.Contains("content representation", ContentIndexStore.ProblemFor(IndexStructuralVerdict.IncompatibleRepresentation, manifest));
        Assert.Equal("The manifest is missing.", ContentIndexStore.ProblemFor(IndexStructuralVerdict.Missing, manifest));
        Assert.Equal("The manifest is corrupt or incomplete.", ContentIndexStore.ProblemFor(IndexStructuralVerdict.Trusted, manifest));

        var syntheticStore = NewStore();
        Assert.True(syntheticStore.IsManifestForThisScope(manifest));
        Assert.False(syntheticStore.IsManifestForThisScope(manifest with { ScopeId = " " }));
        Assert.False(syntheticStore.IsManifestForThisScope(manifest with { VolumeIdentity = " " }));
        Assert.False(syntheticStore.IsManifestForThisScope(manifest with { NormalizedRootPath = " " }));
        Assert.False(syntheticStore.IsManifestForThisScope(manifest with { ScopeId = "other" }));

        string normalizedRoot = IndexScopeIdentity.NormalizePath(_sandbox);
        string volume = Path.GetPathRoot(normalizedRoot)!;
        string realScopeId = IndexScopeIdentity.ComputeScopeId(volume, normalizedRoot);
        var realStore = new ContentIndexStore(_paths, realScopeId);
        IndexManifest realManifest = manifest with
        {
            ScopeId = realScopeId,
            VolumeIdentity = volume,
            NormalizedRootPath = normalizedRoot,
        };
        Assert.True(realStore.IsManifestForThisScope(realManifest));
        Assert.False(realStore.IsManifestForThisScope(realManifest with { NormalizedRootPath = @"C:\different" }));
        Assert.False(realStore.IsManifestForThisScope(realManifest with { NormalizedRootPath = "\0" }));

        realStore.ManifestPathRootReader = _ => null;
        Assert.True(realStore.IsManifestForThisScope(realManifest));
        realStore.ManifestRootNormalizer = _ => throw new NotSupportedException("unsupported root");
        Assert.False(realStore.IsManifestForThisScope(realManifest));
        realStore.ManifestRootNormalizer = _ => throw new InvalidOperationException("unexpected root failure");
        Assert.Throws<InvalidOperationException>(() => realStore.IsManifestForThisScope(realManifest));
    }

    [Fact]
    public void SequenceParsers_AcceptValidNamesAndRejectPrefixesAndInvalidNumbers()
    {
        Assert.True(ContentIndexStore.TryParseGenerationSequence("gen-42", out long generation));
        Assert.Equal(42, generation);
        Assert.False(ContentIndexStore.TryParseGenerationSequence("other-42", out _));
        Assert.False(ContentIndexStore.TryParseGenerationSequence("gen-nope", out _));
        Assert.Equal(42, ContentIndexStore.ParseGenerationSequence("gen-42"));
        Assert.Null(ContentIndexStore.ParseGenerationSequence("gen-nope"));

        Assert.True(ContentIndexStore.TryParseSegmentSequence("seg-17", out long segment));
        Assert.Equal(17, segment);
        Assert.False(ContentIndexStore.TryParseSegmentSequence("other-17", out _));
        Assert.False(ContentIndexStore.TryParseSegmentSequence("seg-nope", out _));

        Assert.Equal(0, ContentIndexStore.DirectorySizeBytes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void DirectorySizeBytes_ExpectedReadFailuresAreBestEffort_AndOomEscapes()
    {
        Assert.Equal(0, ContentIndexStore.DirectorySizeBytes(
            "root", _ => false, _ => throw new InvalidOperationException(), _ => 0));
        Assert.Equal(7, ContentIndexStore.DirectorySizeBytes(
            "root", _ => true, _ => ["a", "b"], file => file == "a" ? 3 : 4));

        Assert.Equal(0, ContentIndexStore.DirectorySizeBytes(
            "root",
            _ => true,
            _ => ["io", "access"],
            file => file == "io"
                ? throw new IOException("file read")
                : throw new UnauthorizedAccessException("file access")));
        Assert.Equal(0, ContentIndexStore.DirectorySizeBytes(
            "root", _ => true, _ => throw new IOException("enumeration"), _ => 0));
        Assert.Equal(0, ContentIndexStore.DirectorySizeBytes(
            "root", _ => true, _ => throw new UnauthorizedAccessException("enumeration"), _ => 0));

        Assert.Throws<OutOfMemoryException>(() => ContentIndexStore.DirectorySizeBytes(
            "root", _ => true, _ => ["oom"], _ => throw new OutOfMemoryException("file length")));
        Assert.Throws<OutOfMemoryException>(() => ContentIndexStore.DirectorySizeBytes(
            "root", _ => true, _ => throw new OutOfMemoryException("enumeration"), _ => 0));
    }

    [Fact]
    public void ValidateCurrent_TrueAfterPublish()
    {
        var store = NewStore();
        Assert.False(store.ValidateCurrent());
        store.Publish(BuildGeneration((@"C:\src\a.txt", "content")));
        Assert.True(store.ValidateCurrent());
    }

    private static void WriteLegacySlot(
        string path,
        string sequence,
        string generationId,
        string? digestOverride = null)
    {
        string payload = sequence + "\n" + generationId;
        string digest = digestOverride
            ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        File.WriteAllText(path, payload + "\n" + digest + "\n");
    }
}
