using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Yagu.Helpers;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage 1 differential + behavioral tests for <see cref="IndexFileContentReader"/> (plan §5.2). The
/// reader replaces the build loop's <c>File.ReadAllBytes</c> + in-memory classify with a single-open
/// prefix-rejecting read. Its verdict/trigram output MUST equal the reference
/// <see cref="IndexIngestionClassifier.ClassifyContent"/> for every stable file, and it must physically
/// read at most the 8 KB sniff prefix for a binary/BOM file — the binary-heavy I/O win.
/// </summary>
public sealed class ContentIndexFileContentReaderTests : IDisposable
{
    private readonly string _dir;

    public ContentIndexFileContentReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy Policy(long cap = 0, bool indexBinary = false) =>
        new(cap, excludedGlobs: null, excludedExtensions: null, includeHiddenFiles: true,
            followReparsePoints: false, maxDepth: 0, indexBinaryAsciiContent: indexBinary);

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ───────────────────── Differential corpus ─────────────────────

    public static IEnumerable<object[]> Corpus()
    {
        byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        yield return Case("empty", Array.Empty<byte>());
        yield return Case("one-byte", Ascii("A"));
        yield return Case("two-byte", Ascii("AB"));
        yield return Case("three-byte", Ascii("ABC"));
        yield return Case("ascii-lf", Ascii("hello world\nsecond line\n"));
        yield return Case("crlf", Ascii("a\r\nb\r\nc\r\n"));
        yield return Case("bare-cr", Ascii("x\ry\rz"));
        yield return Case("three-lf", B(0x0A, 0x0A, 0x0A));
        yield return Case("utf8-multibyte", Utf8("héllo wörld café — ☃ 𝟘"));
        yield return Case("duplicate-trigrams", Ascii(new string('a', 400)));
        yield return Case("all-distinct", Ascii("abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOP"));

        // BOMs (all outside the v1 allowlist).
        yield return Case("bom-utf8", B(0xEF, 0xBB, 0xBF).Concat(Ascii("text")).ToArray());
        yield return Case("bom-utf16le", B(0xFF, 0xFE).Concat(Ascii("t\0e\0")).ToArray());
        yield return Case("bom-utf16be", B(0xFE, 0xFF).Concat(Ascii("\0t\0e")).ToArray());
        yield return Case("bom-utf32le", B(0xFF, 0xFE, 0x00, 0x00).Concat(Ascii("text")).ToArray());
        yield return Case("bom-utf32be", B(0x00, 0x00, 0xFE, 0xFF).Concat(Ascii("text")).ToArray());

        // Embedded NUL → binary.
        yield return Case("nul", Ascii("abc").Concat(B(0x00)).Concat(Ascii("def")).ToArray());

        // Every binary magic signature the shared detector knows (padded to a plausible length).
        yield return Case("magic-gzip", MagicBlob(0x1F, 0x8B, 0x08, 0x00));
        yield return Case("magic-zip", MagicBlob(0x50, 0x4B, 0x03, 0x04));
        yield return Case("magic-zip-empty", MagicBlob(0x50, 0x4B, 0x05, 0x06));
        yield return Case("magic-zip-spanned", MagicBlob(0x50, 0x4B, 0x07, 0x08));
        yield return Case("magic-png", MagicBlob(0x89, 0x50, 0x4E, 0x47));
        yield return Case("magic-jpeg", MagicBlob(0xFF, 0xD8, 0xFF, 0xE0));
        yield return Case("magic-pdf", MagicBlob(0x25, 0x50, 0x44, 0x46));
        yield return Case("magic-elf", MagicBlob(0x7F, 0x45, 0x4C, 0x46));
        yield return Case("magic-mz", MagicBlob(0x4D, 0x5A, 0x90, 0x00));
        yield return Case("magic-7z", MagicBlob(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C));
        yield return Case("magic-zstd", MagicBlob(0x28, 0xB5, 0x2F, 0xFD));
        yield return Case("magic-macho32", MagicBlob(0xCE, 0xFA, 0xED, 0xFE));
        yield return Case("magic-macho64", MagicBlob(0xCF, 0xFA, 0xED, 0xFE));
        yield return Case("magic-machofat", MagicBlob(0xCA, 0xFE, 0xBA, 0xBE));
        yield return Case("magic-sqlite", MagicBlob(0x53, 0x51, 0x4C, 0x69, 0x74, 0x65));
        yield return Case("magic-bzip2", MagicBlob(0x42, 0x5A, 0x68, 0x39));
        yield return Case("magic-xz", MagicBlob(0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00));
        yield return Case("magic-rar", MagicBlob(0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00));

        // Control-heavy blob (>512 bytes, >5% suspicious control bytes) → binary via the ratio heuristic.
        var control = new byte[1024];
        for (int i = 0; i < control.Length; i++)
            control[i] = (byte)((i % 5 == 0) ? 0x01 : 0x41); // 20% control bytes
        yield return Case("control-ratio", control);

        // Invalid UTF-8 sequences (all → NotBomlessUtf8/UnsupportedEncoding).
        yield return Case("utf8-overlong", B(0x41, 0xC0, 0xAF, 0x42));            // overlong '/'
        yield return Case("utf8-surrogate", B(0x41, 0xED, 0xA0, 0x80, 0x42));      // U+D800 surrogate
        yield return Case("utf8-out-of-range", B(0x41, 0xF4, 0x90, 0x80, 0x80));   // > U+10FFFF
        yield return Case("utf8-invalid-lead", B(0x41, 0x80, 0x42));               // lone continuation
        yield return Case("utf8-bad-continuation", B(0x41, 0xC3, 0x28));           // bad 2nd byte
        yield return Case("utf8-truncated", B(0x41, 0xE2, 0x82));                  // truncated 3-byte

        // Boundary sizes around the 8 KB sniff.
        yield return Case("text-8191", Ascii(new string('m', 8191)));
        yield return Case("text-8192-exact", Ascii(new string('n', 8192)));
        yield return Case("text-8193", Ascii(new string('o', 8193)));
        yield return Case("text-multi-chunk", Utf8(BuildLongText(50_000)));
    }

    private static object[] Case(string name, byte[] bytes) => new object[] { name, bytes };

    private static byte[] MagicBlob(params int[] magic)
    {
        var blob = new byte[4096];
        for (int i = 0; i < blob.Length; i++)
            blob[i] = 0x41; // filler after the magic; the magic decides binary regardless
        for (int i = 0; i < magic.Length; i++)
            blob[i] = (byte)magic[i];
        return blob;
    }

    private static string BuildLongText(int approxChars)
    {
        var sb = new StringBuilder(approxChars + 64);
        int line = 0;
        while (sb.Length < approxChars)
            sb.Append("line ").Append(line++).Append(" the quick brown fox jumps café\n");
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Read_MatchesReferenceClassifier_ForEveryCase(string name, byte[] bytes)
    {
        string path = Write(name + ".bin", bytes);
        var policy = Policy();

        IndexContentClassification expected = IndexIngestionClassifier.ClassifyContent(bytes, policy);
        IndexFileReadResult actual = new IndexFileContentReader().Read(path, bytes.Length, policy, CancellationToken.None);

        Assert.Equal(expected.Reason, actual.Reason);
        Assert.True(expected.Trigrams.SequenceEqual(actual.Trigrams),
            $"[{name}] trigram parity failed: expected {expected.Trigrams.Count}, got {actual.Trigrams.Count}");
    }

    [Fact]
    public void Read_SeededRandomInputs_MatchReferenceClassifier()
    {
        var rng = new Random(20260723);
        var policy = Policy();
        var reader = new IndexFileContentReader();

        for (int iter = 0; iter < 200; iter++)
        {
            int len = rng.Next(0, 20_000);
            var bytes = new byte[len];
            rng.NextBytes(bytes);
            string path = Write($"rand-{iter}.bin", bytes);

            IndexContentClassification expected = IndexIngestionClassifier.ClassifyContent(bytes, policy);
            IndexFileReadResult actual = reader.Read(path, len, policy, CancellationToken.None);

            Assert.Equal(expected.Reason, actual.Reason);
            Assert.True(expected.Trigrams.SequenceEqual(actual.Trigrams),
                $"[rand-{iter} len={len}] trigram parity failed");
        }
    }

    // ───────────────────── Byte-read accounting ─────────────────────

    [Fact]
    public void Read_LargeBinaryFile_ReadsAtMostTheSniffPrefix()
    {
        // A PNG magic then ~1 MB of tail: the reader must reject after the 8 KB sniff without reading on.
        var bytes = new byte[1024 * 1024];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = 0xAB; // >= 0x80, no NUL/control noise
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47; // PNG magic
        string path = Write("big.png", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.Binary, result.Reason);
        Assert.True(result.BytesRead <= ContentRepresentation.BinarySniffBytes,
            $"binary tail should not be read: BytesRead={result.BytesRead} > {ContentRepresentation.BinarySniffBytes}");
    }

    [Fact]
    public void Read_BinaryAsciiEnabled_IndexesPrintableRunsButNeverBridgesBinaryBytes()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("header andrew1stein@gmail.com tail")
            .Concat(new byte[] { 0, 1, 2 })
            .Concat(Encoding.ASCII.GetBytes("second printable run"))
            .ToArray();
        string path = Write("mail.db", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(
            path, bytes.Length, Policy(indexBinary: true), CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.Equal(bytes.LongLength, result.BytesRead);
        Assert.Contains(new Trigram((byte)'a', (byte)'n', (byte)'d'), result.Trigrams);
        Assert.DoesNotContain(new Trigram((byte)'l', 0, 1), result.Trigrams);
        Assert.NotNull(result.Identity);
    }

    [Fact]
    public void Read_BinaryAsciiEnabled_OverflowFailsSafeToUnindexed()
    {
        // Deterministic printable triples exceeding the per-file bound, separated by NULs so no extra
        // cross-record trigrams are created. Overflow means Binary/unindexed -> authoritative live scan.
        using var ms = new MemoryStream();
        int needed = BinaryAsciiContentRepresentation.MaxDistinctTrigramsPerFile + 1;
        for (int i = 0; i < needed; i++)
        {
            ms.WriteByte((byte)(0x20 + (i / (95 * 95)) % 95));
            ms.WriteByte((byte)(0x20 + (i / 95) % 95));
            ms.WriteByte((byte)(0x20 + i % 95));
            ms.WriteByte(0);
        }
        byte[] bytes = ms.ToArray();
        string path = Write("overflow.bin", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(
            path, bytes.Length, Policy(indexBinary: true), CancellationToken.None);

        Assert.Equal(IndexSkipReason.Binary, result.Reason);
        Assert.Empty(result.Trigrams);

        var representation = new BinaryAsciiContentRepresentation();
        representation.Feed(bytes);
        Assert.True(representation.Overflowed);
        representation.Feed(Encoding.ASCII.GetBytes("ignored after overflow"));
        Assert.False(representation.TryFinish(out _));
    }

    [Fact]
    public void BinaryAsciiQuerySafety_RequiresEveryRequiredTrigramToBePrintable()
    {
        TrigramExpression safe = TrigramExpression.And(
            TrigramExpression.OfTrigram(new Trigram((byte)'a', (byte)'n', (byte)'d')),
            TrigramExpression.OfTrigram(new Trigram((byte)'m', (byte)'a', (byte)'i')));
        TrigramExpression unsafeQuery = TrigramExpression.OfTrigram(new Trigram((byte)'a', 0, (byte)'b'));

        Assert.True(BinaryAsciiContentRepresentation.CanSafelyEvaluate(safe));
        Assert.False(BinaryAsciiContentRepresentation.CanSafelyEvaluate(unsafeQuery));
        Assert.False(BinaryAsciiContentRepresentation.CanSafelyEvaluate(TrigramExpression.All));
    }

    [Fact]
    public void BinaryAsciiRepresentation_NonPrintableByteBreaksTheRun()
    {
        var representation = new BinaryAsciiContentRepresentation();
        representation.Feed(Encoding.ASCII.GetBytes("ab"));
        representation.Feed([0]);
        representation.Feed(Encoding.ASCII.GetBytes("cde"));

        Assert.True(representation.TryFinish(out IReadOnlyList<Trigram> trigrams));
        Assert.Equal([new Trigram((byte)'c', (byte)'d', (byte)'e')], trigrams);
    }

    [Theory]
    [InlineData(0x1F, 0x20, 0x20)]
    [InlineData(0x7F, 0x20, 0x20)]
    [InlineData(0x20, 0x1F, 0x20)]
    [InlineData(0x20, 0x7F, 0x20)]
    [InlineData(0x20, 0x20, 0x1F)]
    [InlineData(0x20, 0x20, 0x7F)]
    public void BinaryAsciiQuerySafety_RejectsEachNonPrintableByte(int byte0, int byte1, int byte2)
    {
        var query = TrigramExpression.OfTrigram(
            new Trigram((byte)byte0, (byte)byte1, (byte)byte2));

        Assert.False(BinaryAsciiContentRepresentation.CanSafelyEvaluate(query));
    }

    [Fact]
    public void Read_PlausibleText_ReadsWholeBody()
    {
        var bytes = Encoding.UTF8.GetBytes(BuildLongText(40_000));
        string path = Write("text.txt", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.None, result.Reason);
        Assert.Equal(bytes.LongLength, result.BytesRead);
    }

    [Fact]
    public void Read_InvalidUtf8AfterSniffPrefix_IsUnsupportedEncoding()
    {
        byte[] bytes = Enumerable.Repeat((byte)'a', ContentRepresentation.BinarySniffBytes)
            .Append((byte)0xFF)
            .ToArray();
        string path = Write("invalid-tail.txt", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(
            path, bytes.Length, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.UnsupportedEncoding, result.Reason);
        Assert.Equal(bytes.LongLength, result.BytesRead);
    }

    [Fact]
    public void Read_LongTextAtCapBoundary_IsAdmitted()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(new string('a', ContentRepresentation.BinarySniffBytes + 1000));
        string path = Write("long-at-cap.txt", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(
            path, bytes.Length, Policy(cap: bytes.Length), CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.Equal(bytes.LongLength, result.BytesRead);
    }

    [Fact]
    public void Read_LongBinaryAsciiAtCapBoundary_ReachesEofAndIsAdmitted()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abc\0", 3000)));
        string path = Write("long-binary-at-cap.bin", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(
            path, bytes.Length, Policy(cap: bytes.Length, indexBinary: true), CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.Equal(bytes.LongLength, result.BytesRead);
    }

    [Fact]
    public void StreamingHelpers_FileGrowthPastCap_FailsClosed()
    {
        const int appendedBytes = 1000;
        int prefixLength = ContentRepresentation.BinarySniffBytes;
        long cap = prefixLength + appendedBytes - 1;

        string textPath = Write("growing-text.txt", Enumerable.Repeat((byte)'a', prefixLength).ToArray());
        using var textStream = new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] textPrefix = new byte[prefixLength];
        textStream.ReadExactly(textPrefix);
        using (var writer = new FileStream(textPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            writer.Write(new byte[appendedBytes]);

        IndexFileReadResult textResult = IndexFileContentReader.StreamClassify(
            textStream, textPrefix, prefixLength, cap, CancellationToken.None);

        string binaryPath = Write("growing-binary.bin", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abc\0", prefixLength / 4))));
        using var binaryStream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] binaryPrefix = new byte[prefixLength];
        binaryStream.ReadExactly(binaryPrefix);
        using (var writer = new FileStream(binaryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            writer.Write(new byte[appendedBytes]);

        IndexFileReadResult binaryResult = IndexFileContentReader.StreamClassifyBinary(
            binaryStream, binaryPrefix, prefixLength, cap, CancellationToken.None);

        Assert.Equal(IndexSkipReason.OverSizeCap, textResult.Reason);
        Assert.Equal(IndexSkipReason.OverSizeCap, binaryResult.Reason);
    }

    [Fact]
    public void Read_SmallText_BytesReadEqualsFileLength()
    {
        var bytes = Encoding.ASCII.GetBytes("hello\nworld\n");
        string path = Write("small.txt", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.None, result.Reason);
        Assert.Equal(bytes.LongLength, result.BytesRead);
    }

    // ───────────────────── Size cap ─────────────────────

    [Fact]
    public void Read_OverCapFile_ReturnsOverSizeCap_WithoutReadingBody()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('a', 5000));
        string path = Write("over.txt", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(cap: 1000), CancellationToken.None);

        Assert.Equal(IndexSkipReason.OverSizeCap, result.Reason);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public void Read_AtCapBoundary_IsAdmitted()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('a', 1000));
        string path = Write("atcap.txt", bytes);

        // content.Length == cap is NOT over cap (reference uses strict '>').
        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(cap: 1000), CancellationToken.None);

        Assert.Equal(IndexSkipReason.None, result.Reason);
        Assert.Equal(bytes.LongLength, result.BytesRead);
    }

    // ───────────────────── Failure / mutation semantics ─────────────────────

    [Fact]
    public void Read_MissingFile_ThrowsIoException()
    {
        string path = Path.Combine(_dir, "does-not-exist.txt");
        Assert.Throws<FileNotFoundException>(() =>
            new IndexFileContentReader().Read(path, 0, Policy(), CancellationToken.None));
    }

    [Fact]
    public void Read_DirectoryPath_ThrowsUnauthorizedAccess()
    {
        // Opening a directory as a file surfaces UnauthorizedAccessException — the build loop's when-filter
        // catches it and records AccessDenied (conservative skip), never aborting the build.
        Assert.Throws<UnauthorizedAccessException>(() =>
            new IndexFileContentReader().Read(_dir, 0, Policy(), CancellationToken.None));
    }

    [Fact]
    public void Read_AlreadyCancelled_ThrowsBeforeOpening()
    {
        var bytes = Encoding.ASCII.GetBytes("content");
        string path = Write("cancel.txt", bytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new IndexFileContentReader().Read(path, bytes.Length, Policy(), cts.Token));
    }

    [Fact]
    public void Read_EmptyFile_IsAdmittedWithNoTrigrams()
    {
        string path = Write("empty.txt", Array.Empty<byte>());

        IndexFileReadResult result = new IndexFileContentReader().Read(path, 0, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.None, result.Reason);
        Assert.Empty(result.Trigrams);
        Assert.Equal(0, result.BytesRead);
        Assert.Equal(result.Reason, result.Classification.Reason);
        Assert.Same(result.Trigrams, result.Classification.Trigrams);
    }

    // ───────────────────── Same-handle identity (Stage 3) ─────────────────────

    [Fact]
    public void Read_AdmittedText_CapturesIdentityFromTheSameHandle()
    {
        var bytes = Encoding.ASCII.GetBytes("hello world indexed content\n");
        string path = Write("id.txt", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.None, result.Reason);
        FileIdentity? pathIdentity = FileIdentityReader.TryGetIdentity(path);
        if (pathIdentity is null)
            return; // self-gated: identity unavailable on this volume
        Assert.Equal(pathIdentity, result.Identity);
    }

    [Fact]
    public void Read_NonAdmittedFile_HasNoIdentity()
    {
        var bytes = new byte[128];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47; // PNG → binary, not admitted
        string path = Write("bin.png", bytes);

        IndexFileReadResult result = new IndexFileContentReader().Read(path, bytes.Length, Policy(), CancellationToken.None);

        Assert.Equal(IndexSkipReason.Binary, result.Reason);
        Assert.Null(result.Identity);
    }
}
