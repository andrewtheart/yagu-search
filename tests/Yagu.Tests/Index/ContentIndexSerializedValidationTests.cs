using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage 5 tests for streaming checksummed writes and the streaming structural read-after-write validator
/// (plan §5.6/§5.7). The validator must accept a well-formed generation/segment and reject every corruption
/// — bad digest, truncation, trailing garbage, negative/mismatched counts, out-of-range alias, bad identity
/// marker, malformed tombstones — <b>without building a posting index</b>.
/// </summary>
public sealed class ContentIndexSerializedValidationTests : IDisposable
{
    private readonly string _dir;

    public ContentIndexSerializedValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy Policy() => new(0, null, null, true, false, 0);

    private static FileIdentity? FakeIdentity(string path)
        => new FileIdentity(0xFEED, new UsnFileIdentity((ulong)(path.GetHashCode() & 0x7FFFFFFF) + 1, 0));

    private ContentIndexGeneration BuildGen(int docCount)
    {
        var builder = new ContentIndexGenerationBuilder(Policy(), new IndexBuildReport(), FakeIdentity);
        for (int i = 0; i < docCount; i++)
            builder.AddDocument($@"C:\r\doc{i}.txt", Encoding.UTF8.GetBytes($"planner document number {i} café"));
        return builder.Build("scope", "vol", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private string WriteGen(int docCount)
    {
        string genDir = Path.Combine(_dir, "gen-" + Guid.NewGuid().ToString("N"));
        ContentIndexGenerationSerializer.Write(genDir, BuildGen(docCount));
        return genDir;
    }

    private static string File_(string genDir, string name) => Path.Combine(genDir, name);
    private const string ContentFile = ContentIndexGenerationSerializer.ContentFile;
    private const string AliasesFile = ContentIndexGenerationSerializer.AliasesFile;
    private const string FileIdsFile = ContentIndexGenerationSerializer.FileIdsFile;
    private const string ManifestFile = ContentIndexGenerationSerializer.ManifestFile;
    private const string TombstonesFile = ContentIndexDeltaSegmentSerializer.TombstonesFile;

    /// <summary>Overwrites a checksummed file with a crafted body (recomputing a valid trailing digest).</summary>
    private static void WriteChecksummed(string path, Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            write(w);
        ChecksummedFile.Write(path, ms.ToArray());
    }

    // ───────────────────── streaming write equivalence ─────────────────────

    [Fact]
    public void ChecksummedFile_StreamingAndByteArrayWrites_AreByteIdentical()
    {
        foreach (int len in new[] { 0, 1, 100, 70_000 })
        {
            byte[] body = new byte[len];
            for (int index = 0; index < body.Length; index++)
                body[index] = (byte)((index * 31 + len) % 251);
            string a = Path.Combine(_dir, $"a{len}.bin");
            string b = Path.Combine(_dir, $"b{len}.bin");

            ChecksummedFile.Write(a, body);
            ChecksummedFile.Write(b, (s, _) => s.Write(body, 0, body.Length), CancellationToken.None);

            Assert.True(File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)), $"len={len} bytes differ");
            Assert.True(ChecksummedFile.TryRead(a, out byte[] ra) && ra.SequenceEqual(body));
            Assert.True(ChecksummedFile.TryRead(b, out byte[] rb) && rb.SequenceEqual(body));
        }
    }

    [Fact]
    public void StreamedGeneration_RoundTripsThroughTryRead()
    {
        string genDir = WriteGen(3);
        ContentIndexGeneration? reopened = ContentIndexGenerationSerializer.TryRead(genDir);
        Assert.NotNull(reopened);
        Assert.Equal(3, reopened!.Manifest.ContentCount);
    }

    [Fact]
    public void TryRead_MissingUntrustedAndMalformedInputs_FailSafe()
    {
        Assert.Null(ContentIndexGenerationSerializer.TryRead(null!));
        Assert.Null(ContentIndexGenerationSerializer.TryRead(""));
        Assert.Null(ContentIndexGenerationSerializer.TryRead(Path.Combine(_dir, "missing")));

        ContentIndexGeneration generation = BuildGen(1);
        string genDir = Path.Combine(_dir, "read-malformed");
        ContentIndexGenerationSerializer.Write(genDir, generation);
        string manifestPath = File_(genDir, ManifestFile);

        using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(ContentIndexGenerationSerializer.TryRead(genDir));

        ChecksummedFile.Write(manifestPath, Encoding.UTF8.GetBytes("not json"));
        Assert.Null(ContentIndexGenerationSerializer.TryRead(genDir));
        ChecksummedFile.Write(
            manifestPath,
            Encoding.UTF8.GetBytes((generation.Manifest with
            {
                ContentRepresentationVersion = ContentRepresentation.Version + 1,
            }).Serialize()));
        Assert.Null(ContentIndexGenerationSerializer.TryRead(genDir));

        ContentIndexGenerationSerializer.Write(genDir, generation);
        foreach (Action<BinaryWriter> malformedBody in new Action<BinaryWriter>[]
                 {
                     w => w.Write(-1),
                     w => { w.Write(1); w.Write(-1); },
                     w => { w.Write(1); w.Write(1); },
                 })
        {
            WriteChecksummed(File_(genDir, ContentFile), malformedBody);
            Assert.Null(ContentIndexGenerationSerializer.TryRead(genDir));
        }

        ContentIndexGenerationSerializer.Write(genDir, generation);
        foreach (Action<BinaryWriter> malformedBody in new Action<BinaryWriter>[]
                 {
                     w => w.Write(-1),
                     w => { w.Write(1); w.Write(-1); },
                     w => { w.Write(1); w.Write(0); w.Write(0L); w.Write(-1L); },
                     w => { w.Write(1); w.Write(0); w.Write(0L); w.Write(1L); },
                 })
        {
            WriteChecksummed(File_(genDir, AliasesFile), malformedBody);
            Assert.Null(ContentIndexGenerationSerializer.TryRead(genDir));
        }

        ContentIndexGenerationSerializer.Write(genDir, generation);
        foreach (Action<BinaryWriter> malformedBody in new Action<BinaryWriter>[]
                 {
                     w => w.Write(-1),
                     w => { w.Write(1); w.Write((byte)2); },
                     w => { w.Write(1); w.Write((byte)1); },
                     w => w.Write(0),
                 })
        {
            WriteChecksummed(File_(genDir, FileIdsFile), malformedBody);
            Assert.Null(ContentIndexGenerationSerializer.TryRead(genDir));
        }
    }

    [Fact]
    public void TryRead_Cancellation_Throws()
    {
        string genDir = WriteGen(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ContentIndexGenerationSerializer.TryRead(genDir, cancellationToken: cts.Token));
    }

    // ───────────────────── manifest diagnostics and freshness ─────────────────────

    [Fact]
    public void ManifestDiagnostics_DistinguishMissingCorruptIncompatibleAndTrusted()
    {
        Assert.Equal(
            IndexStructuralVerdict.Missing,
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(null!).Verdict);
        Assert.Equal(
            IndexStructuralVerdict.Missing,
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(Path.Combine(_dir, "missing")).Verdict);

        string genDir = Path.Combine(_dir, "manifest-diagnostic");
        Directory.CreateDirectory(genDir);
        Assert.Equal(
            IndexStructuralVerdict.Missing,
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(genDir).Verdict);

        string manifestPath = File_(genDir, ManifestFile);
        File.WriteAllBytes(manifestPath, [1, 2, 3]);
        Assert.Equal(
            IndexStructuralVerdict.Corrupt,
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(genDir).Verdict);

        ChecksummedFile.Write(manifestPath, Encoding.UTF8.GetBytes("not json"));
        Assert.Equal(
            IndexStructuralVerdict.Corrupt,
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(genDir).Verdict);

        IndexManifest manifest = BuildGen(1).Manifest;
        IndexManifest incompatibleFormat = manifest with
        {
            IndexFormatVersion = IndexManifest.CurrentFormatVersion + 1,
        };
        ChecksummedFile.Write(manifestPath, Encoding.UTF8.GetBytes(incompatibleFormat.Serialize()));
        var formatDiagnostic = ContentIndexGenerationSerializer.ReadManifestDiagnostic(genDir);
        Assert.Equal(IndexStructuralVerdict.IncompatibleFormat, formatDiagnostic.Verdict);
        Assert.Equal(incompatibleFormat, formatDiagnostic.Manifest);
        Assert.Null(ContentIndexGenerationSerializer.TryReadManifest(genDir));

        IndexManifest incompatibleRepresentation = manifest with
        {
            ContentRepresentationVersion = ContentRepresentation.Version + 1,
        };
        ChecksummedFile.Write(manifestPath, Encoding.UTF8.GetBytes(incompatibleRepresentation.Serialize()));
        Assert.Equal(
            IndexStructuralVerdict.IncompatibleRepresentation,
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(genDir).Verdict);

        ChecksummedFile.Write(manifestPath, Encoding.UTF8.GetBytes(manifest.Serialize()));
        Assert.Equal(manifest, ContentIndexGenerationSerializer.TryReadManifest(genDir));
        using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(
                IndexStructuralVerdict.Corrupt,
                ContentIndexGenerationSerializer.ReadManifestDiagnostic(genDir).Verdict);
    }

    [Fact]
    public void Reanchor_IsMonotonicAtomicAndCleansFailedTempFiles()
    {
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            Path.Combine(_dir, "missing"), new UsnCheckpoint(1, 200)));

        string genDir = WriteGen(1);
        string manifestPath = File_(genDir, ManifestFile);
        string tempPath = manifestPath + ".reanchor.tmp";
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir, new UsnCheckpoint(1, 100)));
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir, new UsnCheckpoint(1, 99)));

        Assert.True(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir, new UsnCheckpoint(2, 1)));
        Assert.Equal(
            new UsnCheckpoint(2, 1),
            ContentIndexGenerationSerializer.TryReadManifest(genDir)!.FreshnessCheckpoint);

        using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
                genDir, new UsnCheckpoint(2, 2)));
        }
        Assert.False(File.Exists(tempPath));
        Assert.Equal(
            new UsnCheckpoint(2, 1),
            ContentIndexGenerationSerializer.TryReadManifest(genDir)!.FreshnessCheckpoint);

        using (new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
                genDir, new UsnCheckpoint(2, 3)));
            Assert.True(File.Exists(tempPath));
        }
        File.Delete(tempPath);

        Directory.CreateDirectory(tempPath);
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir, new UsnCheckpoint(2, 4)));
        Assert.True(Directory.Exists(tempPath));
    }

    [Fact]
    public void Reanchor_RejectsMissingCorruptAndIncompatibleStagedManifests()
    {
        string genDir = WriteGen(1);
        UsnCheckpoint original = ContentIndexGenerationSerializer.TryReadManifest(genDir)!.FreshnessCheckpoint;
        var target = new UsnCheckpoint(original.JournalId, original.NextUsn + 1);

        Assert.Throws<ArgumentNullException>(() =>
            ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(genDir, target, null!));
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir,
            target,
            static (_, _) => { }));
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir,
            target,
            static (path, _) => ChecksummedFile.Write(path, Encoding.UTF8.GetBytes("not json"))));
        Assert.False(ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(
            genDir,
            target,
            static (path, body) =>
            {
                IndexManifest incompatible = IndexManifest.Deserialize(Encoding.UTF8.GetString(body))! with
                {
                    IndexFormatVersion = IndexManifest.CurrentFormatVersion + 1,
                };
                ChecksummedFile.Write(path, Encoding.UTF8.GetBytes(incompatible.Serialize()));
            }));

        Assert.Equal(original, ContentIndexGenerationSerializer.TryReadManifest(genDir)!.FreshnessCheckpoint);
        Assert.False(File.Exists(File_(genDir, ManifestFile) + ".reanchor.tmp"));
    }

    [Fact]
    public void FreshnessInputs_FailSafeAndSkipMissingIdentities()
    {
        Assert.Null(ContentIndexGenerationSerializer.TryReadFreshnessInputs(
            Path.Combine(_dir, "missing")));

        var builder = new ContentIndexGenerationBuilder(
            Policy(),
            identityProvider: path => path.EndsWith("present.txt", StringComparison.Ordinal)
                ? new FileIdentity(0xBEEF, new UsnFileIdentity(42, 84))
                : null);
        builder.AddDocument(@"C:\r\present.txt", Encoding.UTF8.GetBytes("present planner content"));
        builder.AddDocument(@"C:\r\missing.txt", Encoding.UTF8.GetBytes("missing identity content"));
        ContentIndexGeneration generation = builder.Build(
            "scope", "vol", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        string genDir = Path.Combine(_dir, "freshness");
        ContentIndexGenerationSerializer.Write(genDir, generation);

        var inputs = ContentIndexGenerationSerializer.TryReadFreshnessInputs(genDir);
        Assert.NotNull(inputs);
        Assert.Equal(1, inputs!.Value.FileIds.Count);
        Assert.True(inputs.Value.FileIds.TryGetContentId(new UsnFileIdentity(42, 84), out long contentId));
        Assert.Equal(0, contentId);

        string fileIdsPath = File_(genDir, FileIdsFile);
        File.Delete(fileIdsPath);
        Assert.Null(ContentIndexGenerationSerializer.TryReadFreshnessInputs(genDir));

        WriteChecksummed(fileIdsPath, w => w.Write(-1));
        Assert.Null(ContentIndexGenerationSerializer.TryReadFreshnessInputs(genDir));

        ContentIndexGenerationSerializer.Write(genDir, generation);
        using (new FileStream(fileIdsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(ContentIndexGenerationSerializer.TryReadFreshnessInputs(genDir));
    }

    // ───────────────────── lightweight incremental metadata ─────────────────────

    [Fact]
    public void IncrementalMetadata_ReturnsTargetAliasesAndShadowedPaths()
    {
        var identities = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal)
        {
            [IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")] = new UsnFileIdentity(10, 1),
            [IndexScopeIdentity.NormalizePath(@"C:\r\c.txt")] = new UsnFileIdentity(30, 3),
        };
        var builder = new ContentIndexGenerationBuilder(
            Policy(),
            identityProvider: path => new FileIdentity(0xCAFE, identities[IndexScopeIdentity.NormalizePath(path)]));
        byte[] shared = Encoding.UTF8.GetBytes("deduplicated planner content");
        long sharedContentId = builder.AddDocument(@"C:\r\a.txt", shared);
        builder.AddHardLink(@"C:\r\b.txt", sharedContentId);
        builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("separate content"));
        ContentIndexGeneration generation = builder.Build(
            "scope", "vol", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        string genDir = Path.Combine(_dir, "metadata");
        ContentIndexGenerationSerializer.Write(genDir, generation);

        string a = IndexScopeIdentity.NormalizePath(@"C:\r\a.txt");
        string b = IndexScopeIdentity.NormalizePath(@"C:\r\b.txt");
        string c = IndexScopeIdentity.NormalizePath(@"C:\r\c.txt");
        UsnFileIdentity sharedIdentity = generation.ContentIdentities[0]!.Value;
        var metadata = ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            genDir,
            new HashSet<UsnFileIdentity> { sharedIdentity },
            new HashSet<string>(new[] { b, c }, StringComparer.Ordinal));

        Assert.NotNull(metadata);
        Assert.Equal(new[] { a, b }, metadata!.Value.PathsByIdentity[sharedIdentity].OrderBy(path => path));
        Assert.Equal(new[] { b, c }, metadata.Value.ShadowedPaths.OrderBy(path => path));

        var skipped = ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            genDir,
            new HashSet<UsnFileIdentity>(),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.NotNull(skipped);
        Assert.Empty(skipped!.Value.PathsByIdentity);
        Assert.Empty(skipped.Value.ShadowedPaths);
    }

    [Fact]
    public void IncrementalMetadata_LongPathGrowsBufferAndRoundTrips()
    {
        string path = @"C:\r\" + new string('x', 300) + ".txt";
        var identity = new UsnFileIdentity(100, 200);
        var builder = new ContentIndexGenerationBuilder(
            Policy(),
            identityProvider: _ => new FileIdentity(0xCAFE, identity));
        builder.AddDocument(path, Encoding.UTF8.GetBytes("long normalized path content"));
        ContentIndexGeneration generation = builder.Build(
            "scope", "vol", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        string genDir = Path.Combine(_dir, "metadata-long");
        ContentIndexGenerationSerializer.Write(genDir, generation);

        var metadata = ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            genDir,
            new HashSet<UsnFileIdentity> { identity },
            new HashSet<string> { IndexScopeIdentity.NormalizePath(path) });

        Assert.NotNull(metadata);
        Assert.Equal(IndexScopeIdentity.NormalizePath(path), Assert.Single(metadata!.Value.PathsByIdentity[identity]));
        Assert.Equal(IndexScopeIdentity.NormalizePath(path), Assert.Single(metadata.Value.ShadowedPaths));
    }

    [Fact]
    public void IncrementalMetadata_MalformedIdentityAndAliasFiles_FailSafe()
    {
        ContentIndexGeneration generation = BuildGen(1);
        UsnFileIdentity identity = generation.ContentIdentities[0]!.Value;
        var targets = new HashSet<UsnFileIdentity> { identity };
        var shadows = new HashSet<string>(StringComparer.Ordinal);
        string genDir = Path.Combine(_dir, "metadata-malformed");
        ContentIndexGenerationSerializer.Write(genDir, generation);
        string fileIdsPath = File_(genDir, FileIdsFile);

        foreach (Action<BinaryWriter> malformedBody in new Action<BinaryWriter>[]
                 {
                     _ => { },
                     w => w.Write(-1),
                     w => w.Write(2),
                     w => w.Write(1),
                     w => { w.Write(1); w.Write((byte)2); },
                     w => { w.Write(1); w.Write((byte)1); },
                     w => { w.Write(1); w.Write((byte)1); w.Write(1UL); },
                     w => { w.Write(1); w.Write((byte)0); w.Write((byte)0xFF); },
                 })
        {
            WriteChecksummed(fileIdsPath, malformedBody);
            Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                genDir, targets, shadows));
        }

        ContentIndexGenerationSerializer.Write(genDir, generation);
        string aliasesPath = File_(genDir, AliasesFile);
        foreach (Action<BinaryWriter> malformedBody in new Action<BinaryWriter>[]
                 {
                     _ => { },
                     w => w.Write(-1),
                     w => w.Write(2),
                     w => w.Write(1),
                     w => { w.Write(1); w.Write(-1); },
                     w => { w.Write(1); w.Write(32 * 1024 * 1024 + 1); },
                     w => { w.Write(1); w.Write(1); },
                     w => { w.Write(1); w.Write(0); },
                     w => { w.Write(1); w.Write(0); w.Write(0L); },
                     w => { w.Write(1); w.Write(0); w.Write(0L); w.Write(-1L); },
                     w => { w.Write(1); w.Write(0); w.Write(0L); w.Write(1L); },
                     w => { w.Write(1); w.Write(0); w.Write(0L); w.Write(0L); w.Write((byte)0xFF); },
                 })
        {
            WriteChecksummed(aliasesPath, malformedBody);
            Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                genDir, targets, shadows));
        }

        ContentIndexGenerationSerializer.Write(genDir, generation);
        File.Delete(fileIdsPath);
        Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            genDir, targets, shadows));
        ContentIndexGenerationSerializer.Write(genDir, generation);
        File.Delete(aliasesPath);
        Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            genDir, targets, shadows));
    }

    [Fact]
    public void IncrementalMetadata_ValidAbsentIdentityAndReadFailures_FailSafe()
    {
        ContentIndexGeneration generation = BuildGen(1);
        string genDir = Path.Combine(_dir, "metadata-read-failures");
        ContentIndexGenerationSerializer.Write(genDir, generation);
        var targets = new HashSet<UsnFileIdentity>();
        var shadows = new HashSet<string>(StringComparer.Ordinal);

        WriteChecksummed(File_(genDir, FileIdsFile), w => { w.Write(1); w.Write((byte)0); });
        var absent = ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            genDir, targets, shadows);
        Assert.NotNull(absent);
        Assert.Empty(absent!.Value.PathsByIdentity);

        ContentIndexGenerationSerializer.Write(genDir, generation);
        using (new FileStream(File_(genDir, FileIdsFile), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                genDir, targets, shadows));
        using (new FileStream(File_(genDir, AliasesFile), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                genDir, targets, shadows));
    }

    [Fact]
    public void IncrementalMetadata_NullArgumentsAndCancellation_Throw()
    {
        string genDir = WriteGen(1);
        var targets = new HashSet<UsnFileIdentity>();
        var shadows = new HashSet<string>(StringComparer.Ordinal);
        Assert.Null(ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
            Path.Combine(_dir, "missing-metadata"), targets, shadows));
        Assert.Throws<ArgumentNullException>(() =>
            ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(genDir, null!, shadows));
        Assert.Throws<ArgumentNullException>(() =>
            ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(genDir, targets, null!));

        using var identityCancellation = new CancellationTokenSource();
        identityCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                genDir, targets, shadows, identityCancellation.Token));

        string aliasCancellationDir = WriteGen(0);
        IndexManifest manifest = ContentIndexGenerationSerializer.TryReadManifest(aliasCancellationDir)! with
        {
            AliasCount = 1,
        };
        ChecksummedFile.Write(
            File_(aliasCancellationDir, ManifestFile),
            Encoding.UTF8.GetBytes(manifest.Serialize()));
        WriteChecksummed(File_(aliasCancellationDir, AliasesFile), w => w.Write(1));
        using var aliasCancellation = new CancellationTokenSource();
        aliasCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                aliasCancellationDir, targets, shadows, aliasCancellation.Token));
    }

    // ───────────────────── valid shapes ─────────────────────

    [Fact]
    public void Validate_ValidGeneration_ReturnsTrueWithShape()
    {
        string genDir = WriteGen(4);
        Assert.True(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out var shape));
        Assert.Equal(4, shape.DocumentCount);
        Assert.Equal(4, shape.AliasCount);
        Assert.Equal(4, shape.IdentityCount);
    }

    [Fact]
    public void Validate_EmptyGeneration_ReturnsTrue()
    {
        string genDir = WriteGen(0);
        Assert.True(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out var shape));
        Assert.Equal(0, shape.DocumentCount);
    }

    [Fact]
    public void Validate_ValidSegment_ReturnsTrue()
    {
        string segDir = Path.Combine(_dir, "seg");
        ContentIndexDeltaSegmentSerializer.Write(segDir, new ContentIndexDeltaSegment(BuildGen(2), Array.Empty<string>()));
        Assert.True(ContentIndexDeltaSegmentSerializer.TryValidateSerializedSegment(segDir, out var shape));
        Assert.Equal(2, shape.DocumentCount);
    }

    // ───────────────────── corruption cases ─────────────────────

    [Fact]
    public void Validate_CorruptDigest_ReturnsFalse()
    {
        string genDir = WriteGen(3);
        string content = File_(genDir, ContentFile);
        byte[] bytes = File.ReadAllBytes(content);
        bytes[^1] ^= 0xFF; // flip a digest byte
        File.WriteAllBytes(content, bytes);
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_TruncatedContent_ReturnsFalse()
    {
        string genDir = WriteGen(3);
        string content = File_(genDir, ContentFile);
        byte[] bytes = File.ReadAllBytes(content);
        File.WriteAllBytes(content, bytes[..^1]); // drop one byte
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_TrailingGarbageAfterRecords_ReturnsFalse()
    {
        string genDir = WriteGen(1);
        // A well-checksummed content.bin declaring 0 documents but with trailing bytes → exact-EOF check fails.
        WriteChecksummed(File_(genDir, ContentFile), w =>
        {
            w.Write(0);              // docCount = 0
            w.Write(0xDEADBEEF);     // trailing garbage
        });
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_NegativeDocumentCount_ReturnsFalse()
    {
        string genDir = WriteGen(1);
        WriteChecksummed(File_(genDir, ContentFile), w => w.Write(-1));
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_MissingOrUntrustedManifest_ReturnsFalse()
    {
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration("", out _));
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(
            Path.Combine(_dir, "missing"), out _));

        string genDir = WriteGen(1);
        File.Delete(File_(genDir, ManifestFile));
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));

        ChecksummedFile.Write(File_(genDir, ManifestFile), Encoding.UTF8.GetBytes("not json"));
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));

        IndexManifest incompatible = BuildGen(1).Manifest with
        {
            IndexFormatVersion = IndexManifest.CurrentFormatVersion + 1,
        };
        ChecksummedFile.Write(
            File_(genDir, ManifestFile),
            Encoding.UTF8.GetBytes(incompatible.Serialize()));
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_MalformedContentRecords_ReturnFalse()
    {
        string genDir = WriteGen(1);
        string contentPath = File_(genDir, ContentFile);
        Action<BinaryWriter>[] malformedBodies =
        [
            _ => { },
            w => w.Write(1),
            w => { w.Write(1); w.Write(-1); },
            w => { w.Write(1); w.Write(1); },
        ];

        foreach (Action<BinaryWriter> malformedBody in malformedBodies)
        {
            WriteChecksummed(contentPath, malformedBody);
            Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
        }

        File.Delete(contentPath);
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_AliasContentIdOutOfRange_ReturnsFalse()
    {
        string genDir = WriteGen(1); // one document → valid content ids are just {0}
        WriteChecksummed(File_(genDir, AliasesFile), w =>
        {
            w.Write(1);                 // alias count
            byte[] p = Encoding.UTF8.GetBytes(@"c:\r\x.txt");
            w.Write(p.Length);
            w.Write(p);
            w.Write(0L);                // aliasId
            w.Write(5L);                // contentId 5 is out of range for 1 document
        });
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_MalformedAliasRecords_ReturnFalse()
    {
        string genDir = WriteGen(1);
        string aliasesPath = File_(genDir, AliasesFile);
        Action<BinaryWriter>[] malformedBodies =
        [
            _ => { },
            w => w.Write(-1),
            w => w.Write(1),
            w => { w.Write(1); w.Write(-1); },
            w => { w.Write(1); w.Write(3); w.Write((byte)'a'); },
            w => { w.Write(1); w.Write(0); },
            w => { w.Write(1); w.Write(0); w.Write(0L); },
            w => { w.Write(1); w.Write(0); w.Write(0L); w.Write(-1L); },
            w => { w.Write(0); w.Write((byte)0xFF); },
        ];

        foreach (Action<BinaryWriter> malformedBody in malformedBodies)
        {
            WriteChecksummed(aliasesPath, malformedBody);
            Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
        }

        File.Delete(aliasesPath);
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_BadIdentityPresenceByte_ReturnsFalse()
    {
        string genDir = WriteGen(1);
        WriteChecksummed(File_(genDir, FileIdsFile), w =>
        {
            w.Write(1);          // count == documentCount
            w.Write((byte)2);    // invalid presence byte (only 0 or 1 are valid)
        });
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_FileIdCountMismatch_ReturnsFalse()
    {
        string genDir = WriteGen(1);
        WriteChecksummed(File_(genDir, FileIdsFile), w =>
        {
            w.Write(2);          // 2 identities but only 1 document
            w.Write((byte)0);
            w.Write((byte)0);
        });
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_MalformedFileIdRecords_ReturnFalse()
    {
        string genDir = WriteGen(1);
        string fileIdsPath = File_(genDir, FileIdsFile);
        Action<BinaryWriter>[] malformedBodies =
        [
            _ => { },
            w => w.Write(-1),
            w => w.Write(1),
            w => { w.Write(1); w.Write((byte)1); w.Write(1UL); },
            w => { w.Write(1); w.Write((byte)0); w.Write((byte)0xFF); },
        ];

        foreach (Action<BinaryWriter> malformedBody in malformedBodies)
        {
            WriteChecksummed(fileIdsPath, malformedBody);
            Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
        }
    }

    [Fact]
    public void Validate_LockedContentFile_ReturnsFalse()
    {
        string genDir = WriteGen(1);
        using var locked = new FileStream(
            File_(genDir, ContentFile),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_ManifestCountMismatch_ReturnsFalse()
    {
        ContentIndexGeneration gen = BuildGen(2);
        string genDir = Path.Combine(_dir, "gen-mm");
        ContentIndexGenerationSerializer.Write(genDir, gen);

        // Rewrite the manifest with a document count that no longer matches content.bin.
        IndexManifest wrong = gen.Manifest with { ContentCount = gen.Manifest.ContentCount + 1 };
        ChecksummedFile.Write(File_(genDir, ManifestFile), Encoding.UTF8.GetBytes(wrong.Serialize()));

        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void Validate_MissingFile_ReturnsFalse()
    {
        string genDir = WriteGen(2);
        File.Delete(File_(genDir, FileIdsFile));
        Assert.False(ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _));
    }

    [Fact]
    public void ValidateSegment_MalformedTombstones_ReturnsFalse()
    {
        string segDir = Path.Combine(_dir, "seg-bad");
        ContentIndexDeltaSegmentSerializer.Write(segDir, new ContentIndexDeltaSegment(BuildGen(1), Array.Empty<string>()));
        WriteChecksummed(File_(segDir, TombstonesFile), w => w.Write(-1)); // negative tombstone count
        Assert.False(ContentIndexDeltaSegmentSerializer.TryValidateSerializedSegment(segDir, out _));
    }

    [Fact]
    public void ValidateTombstones_MalformedRecordsAndCancellation_AreRejected()
    {
        string path = Path.Combine(_dir, "tombstones.bin");
        Assert.False(ContentIndexGenerationSerializer.TryValidateTombstones(path, CancellationToken.None));

        Action<BinaryWriter>[] malformedBodies =
        [
            _ => { },
            w => w.Write(-1),
            w => w.Write(1),
            w => { w.Write(1); w.Write(-1); },
            w => { w.Write(1); w.Write(3); w.Write((byte)'a'); },
            w => { w.Write(0); w.Write((byte)0xFF); },
        ];

        foreach (Action<BinaryWriter> malformedBody in malformedBodies)
        {
            WriteChecksummed(path, malformedBody);
            Assert.False(ContentIndexGenerationSerializer.TryValidateTombstones(path, CancellationToken.None));
        }

        WriteChecksummed(path, w => { w.Write(1); w.Write(0); });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ContentIndexGenerationSerializer.TryValidateTombstones(path, cts.Token));
    }

    [Fact]
    public void Validate_Cancellation_Throws()
    {
        string genDir = WriteGen(2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ContentIndexGenerationSerializer.TryValidateSerializedGeneration(genDir, out _, cts.Token));
    }
}
