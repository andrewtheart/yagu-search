using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage 2 differential tests for <see cref="StreamingRepresentationClassifier"/> (plan §5.3). Its verdict
/// and sorted trigram set MUST equal the in-memory reference
/// (<see cref="ContentRepresentation.ValidateAndNormalize"/> + <see cref="ContentRepresentation.ExtractTrigrams"/>)
/// for every input <b>at every chunk boundary</b> — so CRLF pairs, multibyte UTF-8 sequences, and the
/// rolling trigram window all reassemble correctly across arbitrary read splits.
/// </summary>
public sealed class StreamingRepresentationClassifierTests
{
    // The reference this streaming state machine must reproduce byte-for-byte (post BOM/binary, which the
    // reader — not the classifier — decides from the 8 KB prefix).
    private static (ContentRepresentationVerdict verdict, IReadOnlyList<Trigram> trigrams) Reference(byte[] input)
    {
        var normalized = new List<byte>(input.Length);
        if (!ContentRepresentation.ValidateAndNormalize(input, normalized))
            return (ContentRepresentationVerdict.NotBomlessUtf8, Array.Empty<Trigram>());
        return (ContentRepresentationVerdict.Indexed, ContentRepresentation.ExtractTrigrams(normalized));
    }

    private static (ContentRepresentationVerdict verdict, IReadOnlyList<Trigram> trigrams) StreamAt(byte[] input, int chunkSize)
    {
        var classifier = new StreamingRepresentationClassifier();
        for (int off = 0; off < input.Length; off += chunkSize)
        {
            int len = Math.Min(chunkSize, input.Length - off);
            classifier.Feed(input.AsSpan(off, len)); // return ignored: Finish reports invalid
        }
        ContentRepresentationVerdict verdict = classifier.Finish(out var trigrams);
        return (verdict, trigrams);
    }

    // ───────────────────── Differential corpus ─────────────────────

    public static IEnumerable<object[]> Corpus()
    {
        byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        yield return C("empty", Array.Empty<byte>());
        yield return C("one", Ascii("A"));
        yield return C("two", Ascii("AB"));
        yield return C("three", Ascii("ABC"));

        // Newlines and their normalization.
        yield return C("cr", B(0x0D));
        yield return C("lf", B(0x0A));
        yield return C("crlf", B(0x0D, 0x0A));
        yield return C("cr-cr", B(0x0D, 0x0D));
        yield return C("crlf-lf", B(0x0D, 0x0A, 0x0A));
        yield return C("lf-crlf", B(0x0A, 0x0D, 0x0A));
        yield return C("three-lf", B(0x0A, 0x0A, 0x0A));
        yield return C("mixed-newlines", Ascii("a\r\nb\rc\nd\r\n\r\ne"));
        yield return C("text-crlf", Ascii("the quick brown\r\nfox jumps\r\nover the dog\r\n"));

        // Multibyte UTF-8 (valid).
        yield return C("utf8-2byte", Utf8("café résumé naïve"));
        yield return C("utf8-3byte", Utf8("€ ☃ 中文 テスト"));
        yield return C("utf8-4byte", Utf8("𝟘𝟙𝟚 😀🎉 astral"));
        yield return C("utf8-mixed", Utf8("aé€𝟘b\r\ncafé—☃\n中x"));
        yield return C("utf8-adjacent", Utf8("é€𝟘☃ü中"));

        // Valid but boundary-sensitive: multibyte immediately after/around newlines.
        yield return C("multibyte-after-cr", Utf8("a\ré"));
        yield return C("multibyte-after-crlf", Utf8("a\r\n€"));

        // Invalid UTF-8 (all → NotBomlessUtf8).
        yield return C("overlong", B(0x41, 0xC0, 0xAF, 0x42));
        yield return C("surrogate", B(0x41, 0xED, 0xA0, 0x80, 0x42));
        yield return C("out-of-range", B(0x41, 0xF4, 0x90, 0x80, 0x80));
        yield return C("invalid-lead", B(0x41, 0x80, 0x42));
        yield return C("bad-continuation", B(0x41, 0xC3, 0x28));
        yield return C("truncated-2byte", B(0x41, 0xC3));           // EOF mid-sequence
        yield return C("truncated-3byte", B(0x41, 0xE2, 0x82));     // EOF mid-sequence
        yield return C("truncated-4byte", B(0x41, 0xF0, 0x9D, 0x9F)); // EOF mid-sequence
        yield return C("lead-0xF8", B(0x41, 0xF8, 0x80));           // 5-byte lead is invalid
        yield return C("lead-0xFF", B(0x41, 0xFF));

        // NUL and control bytes are valid canonical bytes (the reader — not this classifier — does the
        // binary sniff; a NUL past the 8 KB sniff is admitted by the reference, so the classifier admits it).
        yield return C("nul", B(0x61, 0x00, 0x62, 0x00, 0x63));
        yield return C("controls", B(0x01, 0x02, 0x03, 0x1F, 0x7F, 0x41));

        // Trigram distribution.
        yield return C("duplicate-heavy", Ascii(new string('a', 300)));
        yield return C("all-distinct", Ascii("abcdefghijklmnopqrstuvwxyz0123456789"));
        yield return C("longer-text", Utf8(BuildLongText()));
    }

    private static object[] C(string name, byte[] bytes) => new object[] { name, bytes };

    private static string BuildLongText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
            sb.Append("line ").Append(i).Append(" the café—brown fox ☃ jumps 𝟘\r\n");
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Stream_MatchesReference_AtEveryChunkSizeFrom1To20(string name, byte[] input)
    {
        var reference = Reference(input);

        for (int chunk = 1; chunk <= 20; chunk++)
        {
            var streamed = StreamAt(input, chunk);
            Assert.Equal(reference.verdict, streamed.verdict);
            Assert.True(reference.trigrams.SequenceEqual(streamed.trigrams),
                $"[{name}] trigram mismatch at chunk size {chunk}");
        }

        // And in a single feed.
        var whole = StreamAt(input, Math.Max(1, input.Length + 1));
        Assert.Equal(reference.verdict, whole.verdict);
        Assert.True(reference.trigrams.SequenceEqual(whole.trigrams), $"[{name}] whole-buffer mismatch");
    }

    [Fact]
    public void Stream_SeededRandomBytes_RandomChunkBoundaries_MatchReference()
    {
        var rng = new Random(20260723);
        for (int iter = 0; iter < 500; iter++)
        {
            int len = rng.Next(0, 300);
            var input = new byte[len];
            rng.NextBytes(input);

            var reference = Reference(input);
            var (verdict, trigrams) = StreamRandom(input, rng);

            Assert.Equal(reference.verdict, verdict);
            Assert.True(reference.trigrams.SequenceEqual(trigrams), $"iter={iter} len={len}");
        }
    }

    [Fact]
    public void Stream_SeededRandomValidText_RandomChunkBoundaries_MatchReference()
    {
        var rng = new Random(98765);
        const string ascii = "abcdefg 0123 \n\r\t.,";
        string[] multibyte = { "é", "€", "𝟘", "ü", "☃", "中" };

        for (int iter = 0; iter < 300; iter++)
        {
            var sb = new StringBuilder();
            int count = rng.Next(0, 220);
            for (int j = 0; j < count; j++)
            {
                if (rng.Next(6) == 0) sb.Append(multibyte[rng.Next(multibyte.Length)]);
                else sb.Append(ascii[rng.Next(ascii.Length)]);
            }
            byte[] input = Encoding.UTF8.GetBytes(sb.ToString());

            var reference = Reference(input);
            Assert.Equal(ContentRepresentationVerdict.Indexed, reference.verdict); // valid by construction

            var (verdict, trigrams) = StreamRandom(input, rng);
            Assert.Equal(ContentRepresentationVerdict.Indexed, verdict);
            Assert.True(reference.trigrams.SequenceEqual(trigrams), $"iter={iter}");
        }
    }

    private static (ContentRepresentationVerdict, IReadOnlyList<Trigram>) StreamRandom(byte[] input, Random rng)
    {
        var classifier = new StreamingRepresentationClassifier();
        int off = 0;
        while (off < input.Length)
        {
            int take = Math.Min(rng.Next(1, 8), input.Length - off);
            classifier.Feed(input.AsSpan(off, take));
            off += take;
        }
        ContentRepresentationVerdict verdict = classifier.Finish(out var trigrams);
        return (verdict, trigrams);
    }

    // ───────────────────── Focused behavior ─────────────────────

    [Fact]
    public void Finish_Empty_IsIndexedWithNoTrigrams()
    {
        var classifier = new StreamingRepresentationClassifier();
        ContentRepresentationVerdict verdict = classifier.Finish(out var trigrams);
        Assert.Equal(ContentRepresentationVerdict.Indexed, verdict);
        Assert.Empty(trigrams);
    }

    [Fact]
    public void Feed_AfterInvalid_IsNoOp_AndFinishRejects()
    {
        var classifier = new StreamingRepresentationClassifier();
        Assert.False(classifier.Feed(new byte[] { 0x41, 0x80 })); // 0x80 is an invalid lead
        Assert.True(classifier.IsInvalid);
        Assert.False(classifier.Feed(new byte[] { 0x42, 0x43 })); // no-op once invalid
        Assert.Equal(ContentRepresentationVerdict.NotBomlessUtf8, classifier.Finish(out var trigrams));
        Assert.Empty(trigrams);
    }

    [Fact]
    public void Crlf_And_BareCr_NormalizeToLf_IdenticallyToReference()
    {
        byte[] crlf = Encoding.ASCII.GetBytes("x\r\ny\r\nz");
        byte[] lf = Encoding.ASCII.GetBytes("x\ny\nz");

        var crlfStreamed = StreamAt(crlf, 1);
        var lfReference = Reference(lf);

        Assert.Equal(ContentRepresentationVerdict.Indexed, crlfStreamed.verdict);
        Assert.True(lfReference.trigrams.SequenceEqual(crlfStreamed.trigrams),
            "CRLF must produce the same trigrams as the LF-normalized text");
    }
}
