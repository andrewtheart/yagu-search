using System.Collections.Generic;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Round-trip and corruption tests for <see cref="ExtendedSourceNamespaceSerializer"/> (plan §7 Phase 4
/// on-disk format). A written namespace reads back byte-identically (fingerprint, postings, source keys,
/// negative proofs), and any missing / truncated / checksum-invalid / version-mismatched / count-mismatched
/// file reads back as <c>null</c> so the source kind live-extracts. No extracted text is ever persisted.
/// </summary>
public sealed class ExtendedSourceNamespaceSerializerTests : IDisposable
{
    private const string DistinctiveWord = "zephyrqux";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yagu-esns-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static ExtractorFingerprint Fp(SpecialSourceKind kind) => new(
        kind, "pdftotext", "24.02.0", "win-x64",
        [new ExtractorFileHash("exe", "AABB"), new ExtractorFileHash("dll", "CCDD")],
        new Dictionary<string, string> { ["enc"] = "UTF-8", ["layout"] = "raw" });

    private static TrigramExpression TriQuery(string word)
    {
        ContentRepresentation.Classify(Encoding.UTF8.GetBytes(word), out IReadOnlyList<Trigram> t);
        return TrigramExpression.OfTrigram(t[0]);
    }

    private ExtendedSourceNamespace BuildSample()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        builder.AddSource(@"C:\docs\a.pdf", new ExtractionOutcome.Success($"{DistinctiveWord} report"));
        builder.AddSource(@"C:\docs\b.pdf", new ExtractionOutcome.Success("ordinary unrelated content"));
        builder.AddSource(@"C:\docs\scan.pdf", new ExtractionOutcome.DeterministicUnsupported("image-only PDF"));
        return builder.Build();
    }

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void RoundTrip_PreservesFingerprintSourcesNegativesAndMembership()
    {
        ExtendedSourceNamespace original = BuildSample();
        ExtendedSourceNamespaceSerializer.Write(_dir, original);

        ExtendedSourceNamespace? loaded = ExtendedSourceNamespaceSerializer.TryRead(_dir);
        Assert.NotNull(loaded);

        Assert.Equal(SpecialSourceKind.PdfText, loaded!.Kind);
        Assert.True(loaded.Fingerprint.Matches(original.Fingerprint));
        Assert.Equal(original.SourceCount, loaded.SourceCount);
        Assert.Contains(@"C:\docs\scan.pdf", loaded.NegativeProofKeys);

        // Postings rebuilt: the member query still selects a.pdf and not b.pdf.
        IReadOnlySet<string> members = loaded.SelectMemberKeys(TriQuery(DistinctiveWord));
        Assert.Contains(@"C:\docs\a.pdf", members);
        Assert.DoesNotContain(@"C:\docs\b.pdf", members);
    }

    [Fact]
    public void TryRead_MissingDirectory_ReturnsNull()
        => Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(Path.Combine(_dir, "nope")));

    [Fact]
    public void TryRead_EmptyDirectory_ReturnsNull()
        => Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(string.Empty));

    [Fact]
    public void TryRead_TruncatedContent_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        string content = Path.Combine(_dir, ExtendedSourceNamespaceSerializer.ContentFile);
        File.WriteAllBytes(content, File.ReadAllBytes(content)[..3]); // shorter than the digest
        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void TryRead_CorruptHeaderDigest_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        string header = Path.Combine(_dir, ExtendedSourceNamespaceSerializer.HeaderFile);
        byte[] bytes = File.ReadAllBytes(header);
        bytes[^1] ^= 0xFF; // flip a digest byte
        File.WriteAllBytes(header, bytes);
        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void TryRead_UnsupportedVersion_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        // Overwrite the header with a checksum-valid payload carrying a future version number.
        ChecksummedFile.Write(
            Path.Combine(_dir, ExtendedSourceNamespaceSerializer.HeaderFile),
            BitConverter.GetBytes(ExtendedSourceNamespaceSerializer.FormatVersion + 99));
        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void TryRead_InvalidSourceKind_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        ChecksummedFile.Write(
            Path.Combine(_dir, ExtendedSourceNamespaceSerializer.HeaderFile),
            Payload(writer =>
            {
                writer.Write(ExtendedSourceNamespaceSerializer.FormatVersion);
                writer.Write(int.MaxValue);
            }));

        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void TryRead_InvalidIdentityPresenceByte_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        ChecksummedFile.Write(
            Path.Combine(_dir, ExtendedSourceNamespaceSerializer.IdentitiesFile),
            Payload(writer =>
            {
                writer.Write(1);
                byte[] key = Encoding.UTF8.GetBytes(@"C:\docs\a.pdf");
                writer.Write(key.Length);
                writer.Write(key);
                writer.Write((byte)2);
            }));

        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void TryRead_NegativeContentCount_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        ChecksummedFile.Write(
            Path.Combine(_dir, ExtendedSourceNamespaceSerializer.ContentFile),
            BitConverter.GetBytes(-1));

        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void TryRead_SourceContentCountMismatch_ReturnsNull()
    {
        ExtendedSourceNamespaceSerializer.Write(_dir, BuildSample());
        // Replace the sources file with a checksum-valid but empty list (count 0) — content still has 2 docs.
        ChecksummedFile.Write(
            Path.Combine(_dir, ExtendedSourceNamespaceSerializer.SourcesFile),
            BitConverter.GetBytes(0));
        Assert.Null(ExtendedSourceNamespaceSerializer.TryRead(_dir));
    }

    [Fact]
    public void RoundTrip_OcrNamespace_HasNoNegativeProofs()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.ImageOcr, Fp(SpecialSourceKind.ImageOcr));
        builder.AddSource(@"C:\img\a.png", new ExtractionOutcome.Success($"{DistinctiveWord} sign"));
        builder.AddSource(@"C:\img\b.png", new ExtractionOutcome.DeterministicUnsupported("no text")); // not persisted for OCR
        ExtendedSourceNamespaceSerializer.Write(_dir, builder.Build());

        ExtendedSourceNamespace? loaded = ExtendedSourceNamespaceSerializer.TryRead(_dir);
        Assert.NotNull(loaded);
        Assert.Equal(SpecialSourceKind.ImageOcr, loaded!.Kind);
        Assert.Empty(loaded.NegativeProofKeys);
        Assert.Equal(1, loaded.SourceCount);
    }

    [Fact]
    public void RoundTrip_PreservesFreshnessState_IdentitiesRootAndCheckpoint()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        builder.AddSource(@"C:\a.pdf", new ExtractionOutcome.Success($"{DistinctiveWord} one"), new UsnFileIdentity(11, 22));
        builder.AddSource(@"C:\b.pdf", new ExtractionOutcome.Success("other content")); // null identity
        builder.AddSource(@"C:\scan.pdf", new ExtractionOutcome.DeterministicUnsupported("image-only"), new UsnFileIdentity(33, 44));
        ExtendedSourceNamespaceSerializer.Write(_dir, builder.Build(@"C:\", new UsnCheckpoint(7, 900)));

        ExtendedSourceNamespace? loaded = ExtendedSourceNamespaceSerializer.TryRead(_dir);
        Assert.NotNull(loaded);
        Assert.Equal(@"C:\", loaded!.NormalizedRootPath);
        Assert.Equal(new UsnCheckpoint(7, 900), loaded.FreshnessCheckpoint);

        // Every source key (admitted + negative) round-trips its identity, null included.
        Assert.Equal(new UsnFileIdentity(11, 22), loaded.SourceIdentityByKey[@"C:\a.pdf"]);
        Assert.Null(loaded.SourceIdentityByKey[@"C:\b.pdf"]);
        Assert.Equal(new UsnFileIdentity(33, 44), loaded.SourceIdentityByKey[@"C:\scan.pdf"]);

        // A journal change to a.pdf's identity is resolved back to its source key after the round-trip.
        IReadOnlySet<string> dirty = loaded.ResolveDirtyKeys([new UsnChange(new UsnFileIdentity(11, 22), 0x1)]);
        Assert.Contains(@"C:\a.pdf", dirty);
        Assert.Contains(@"C:\b.pdf", dirty);          // null identity -> always dirty
        Assert.DoesNotContain(@"C:\scan.pdf", dirty); // unchanged
    }
}
