using System.Collections.Generic;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ExtractorFingerprint"/> (plan §7 Phase 4): the canonical digest is
/// order-independent over the hash/option lists, folds case/whitespace, and any material change
/// (source kind, engine id/version, runtime, a binary hash, an option) produces a different digest so a
/// swapped or reconfigured extractor is never treated as the same. Equality is by digest only.
/// </summary>
public sealed class ExtractorFingerprintTests
{
    private static ExtractorFingerprint Pdf(
        string version = "24.02.0",
        string runtime = "win-x64",
        IEnumerable<ExtractorFileHash>? hashes = null,
        IEnumerable<KeyValuePair<string, string>>? options = null)
        => new(
            SpecialSourceKind.PdfText,
            "pdftotext",
            version,
            runtime,
            hashes ?? [new ExtractorFileHash("exe", "AABB")],
            options ?? new Dictionary<string, string> { ["enc"] = "UTF-8", ["layout"] = "raw" });

    [Fact]
    public void SameFacts_ProduceEqualFingerprints()
    {
        ExtractorFingerprint a = Pdf();
        ExtractorFingerprint b = Pdf();

        Assert.Equal(a.Digest, b.Digest);
        Assert.True(a.Matches(b));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void NullInputs_AreCanonicalizedToEmpty_AndDoNotThrow()
    {
        var fp = new ExtractorFingerprint(
            SpecialSourceKind.PdfText,
            engineId: null!,
            engineVersion: null!,
            runtime: null!,
            binaryHashes: new[] { new ExtractorFileHash(null!, null!) },
            options: new[] { new KeyValuePair<string, string>(null!, null!) });

        Assert.NotNull(fp.Digest);
        Assert.Equal(string.Empty, fp.EngineId);
        Assert.Equal(string.Empty, fp.EngineVersion);
        Assert.Equal(string.Empty, fp.Runtime);
        Assert.Equal("=", new ExtractorFileHash(null!, null!).Canonical());

        // Null hash/option collections default to empty.
        var fp2 = new ExtractorFingerprint(SpecialSourceKind.Archive, "e", "v", "r");
        Assert.Empty(fp2.BinaryHashes);
        Assert.Empty(fp2.Options);
    }

    [Fact]
    public void Matches_And_Equals_HandleNullAndForeignTypes()
    {
        ExtractorFingerprint a = Pdf();
        Assert.False(a.Matches(null));
        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not a fingerprint"));
        Assert.False(a.Matches(Pdf(version: "99.0")));
    }

    [Fact]
    public void HashAndOptionOrder_DoesNotChangeDigest()
    {
        ExtractorFingerprint a = Pdf(
            hashes: [new ExtractorFileHash("exe", "AABB"), new ExtractorFileHash("dll", "CCDD")],
            options: new Dictionary<string, string> { ["layout"] = "raw", ["enc"] = "UTF-8" });
        ExtractorFingerprint b = Pdf(
            hashes: [new ExtractorFileHash("dll", "CCDD"), new ExtractorFileHash("exe", "AABB")],
            options: new Dictionary<string, string> { ["enc"] = "UTF-8", ["layout"] = "raw" });

        Assert.Equal(a.Digest, b.Digest);
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void CaseAndWhitespace_AreFoldedInHashesAndTrimmedInScalars()
    {
        ExtractorFingerprint a = Pdf(version: "24.02.0", hashes: [new ExtractorFileHash("EXE", "aabb")]);
        ExtractorFingerprint b = Pdf(version: "  24.02.0 ", hashes: [new ExtractorFileHash(" exe ", "AABB")]);

        Assert.Equal(a.Digest, b.Digest);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("version")]
    [InlineData("runtime")]
    [InlineData("hash")]
    [InlineData("option")]
    [InlineData("source")]
    public void AnyMaterialChange_ChangesDigest(string what)
    {
        ExtractorFingerprint baseline = Pdf();
        ExtractorFingerprint changed = what switch
        {
            "engine" => new ExtractorFingerprint(SpecialSourceKind.PdfText, "otherengine", "24.02.0", "win-x64",
                [new ExtractorFileHash("exe", "AABB")],
                new Dictionary<string, string> { ["enc"] = "UTF-8", ["layout"] = "raw" }),
            "version" => Pdf(version: "24.03.0"),
            "runtime" => Pdf(runtime: "win-arm64"),
            "hash" => Pdf(hashes: [new ExtractorFileHash("exe", "FFFF")]),
            "option" => Pdf(options: new Dictionary<string, string> { ["enc"] = "Latin1", ["layout"] = "raw" }),
            "source" => new ExtractorFingerprint(SpecialSourceKind.Archive, "pdftotext", "24.02.0", "win-x64",
                [new ExtractorFileHash("exe", "AABB")],
                new Dictionary<string, string> { ["enc"] = "UTF-8", ["layout"] = "raw" }),
            _ => baseline,
        };

        Assert.NotEqual(baseline.Digest, changed.Digest);
        Assert.False(baseline.Matches(changed));
    }

    [Fact]
    public void Matches_Null_IsFalse()
        => Assert.False(Pdf().Matches(null));

    [Fact]
    public void NullCollections_AreTreatedAsEmpty_AndStillHashable()
    {
        ExtractorFingerprint a = new(SpecialSourceKind.ImageOcr, "eng", "1", "cpu", binaryHashes: null, options: null);
        ExtractorFingerprint b = new(SpecialSourceKind.ImageOcr, "eng", "1", "cpu", [], new Dictionary<string, string>());

        Assert.Equal(a.Digest, b.Digest);
        Assert.NotEmpty(a.Digest);
    }

    [Fact]
    public void UsableAsDictionaryKey_ByDigest()
    {
        var set = new HashSet<ExtractorFingerprint> { Pdf(), Pdf() };
        Assert.Single(set);
    }

    [Fact]
    public void FileHashCanonical_FoldsCaseAndTrims()
        => Assert.Equal("exe=aabb", new ExtractorFileHash("  EXE ", " AaBb ").Canonical());
}
