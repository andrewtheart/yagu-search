using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Unit tests for the pure content-index core types: <see cref="Trigram"/>,
/// <see cref="TrigramExpression"/>, <see cref="ContentRepresentation"/>, and
/// <see cref="IndexScopeIdentity"/> (plan §3.1/§3.2/§3.6).
/// </summary>
public sealed class ContentIndexCoreTests
{
    // ─────────────────────────── Trigram ───────────────────────────

    [Fact]
    public void Trigram_PacksBytesAndExposesThem()
    {
        var t = new Trigram((byte)'f', (byte)'o', (byte)'o');
        Assert.Equal((byte)'f', t.Byte0);
        Assert.Equal((byte)'o', t.Byte1);
        Assert.Equal((byte)'o', t.Byte2);
        Assert.Equal(0x666F6Fu, t.Value);
    }

    [Fact]
    public void Trigram_EqualityAndHashing()
    {
        var a = new Trigram(1, 2, 3);
        var b = new Trigram(1, 2, 3);
        var c = new Trigram(1, 2, 4);
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a == c);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals("not a trigram"));
    }

    [Fact]
    public void Trigram_Ordering()
    {
        var a = new Trigram(0, 0, 1);
        var aCopy = new Trigram(0, 0, 1);
        var b = new Trigram(0, 0, 2);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= aCopy);
        Assert.True(a >= aCopy);
        Assert.Equal(-1, Math.Sign(a.CompareTo(b)));
    }

    [Fact]
    public void Trigram_FromPacked_MasksHighBits()
    {
        var t = Trigram.FromPacked(0xFF_66_6F_6Fu);
        Assert.Equal(0x666F6Fu, t.Value);
        Assert.Equal("66 6F 6F", t.ToString());
    }

    // ─────────────────────── TrigramExpression ───────────────────────

    private static Trigram Tg(string s) => new((byte)s[0], (byte)s[1], (byte)s[2]);

    private static HashSet<Trigram> TrigramsOf(string ascii)
    {
        var set = new HashSet<Trigram>();
        for (int i = 0; i + 2 < ascii.Length; i++)
            set.Add(new Trigram((byte)ascii[i], (byte)ascii[i + 1], (byte)ascii[i + 2]));
        return set;
    }

    [Fact]
    public void Expression_AllAndNoneSimplification()
    {
        var foo = TrigramExpression.OfTrigram(Tg("foo"));
        Assert.Same(foo, TrigramExpression.And(TrigramExpression.All, foo));
        Assert.Same(foo, TrigramExpression.And(foo, TrigramExpression.All));
        Assert.Equal(TrigramExpression.NodeKind.None, TrigramExpression.And(TrigramExpression.None, foo).Kind);
        Assert.Equal(TrigramExpression.NodeKind.None, TrigramExpression.And(foo, TrigramExpression.None).Kind);

        Assert.Equal(TrigramExpression.NodeKind.All, TrigramExpression.Or(TrigramExpression.All, foo).Kind);
        Assert.Equal(TrigramExpression.NodeKind.All, TrigramExpression.Or(foo, TrigramExpression.All).Kind);
        Assert.Same(foo, TrigramExpression.Or(TrigramExpression.None, foo));
        Assert.Same(foo, TrigramExpression.Or(foo, TrigramExpression.None));
    }

    [Fact]
    public void Expression_AndDeduplicatesTrigramLeaves()
    {
        var foo = TrigramExpression.OfTrigram(Tg("foo"));
        var same = TrigramExpression.And(foo, TrigramExpression.OfTrigram(Tg("foo")));
        // De-dup collapses two identical leaves into one.
        Assert.Equal(TrigramExpression.NodeKind.Trigram, same.Kind);
    }

    [Fact]
    public void Expression_AndOrFlattenNestedSameKind()
    {
        var a = TrigramExpression.OfTrigram(Tg("aaa"));
        var b = TrigramExpression.OfTrigram(Tg("bbb"));
        var c = TrigramExpression.OfTrigram(Tg("ccc"));
        var and = TrigramExpression.And(TrigramExpression.And(a, b), c);
        Assert.Equal(TrigramExpression.NodeKind.And, and.Kind);
        Assert.Equal(3, and.Children.Count);
    }

    [Fact]
    public void Expression_Evaluate_And()
    {
        var query = TrigramExpression.And(
            TrigramExpression.OfTrigram(Tg("foo")),
            TrigramExpression.OfTrigram(Tg("bar")));
        Assert.True(query.Evaluate(TrigramsOf("foobar")));
        Assert.False(query.Evaluate(TrigramsOf("foo")));      // missing bar
        Assert.False(query.Evaluate(TrigramsOf("bar")));      // missing foo
    }

    [Fact]
    public void Expression_Evaluate_Or()
    {
        var query = TrigramExpression.Or(
            TrigramExpression.OfTrigram(Tg("foo")),
            TrigramExpression.OfTrigram(Tg("bar")));
        Assert.True(query.Evaluate(TrigramsOf("xxfooxx")));
        Assert.True(query.Evaluate(TrigramsOf("xxbarxx")));
        Assert.False(query.Evaluate(TrigramsOf("xxbazxx")));
    }

    [Fact]
    public void Expression_Evaluate_AllAndNoneLeaves()
    {
        Assert.True(TrigramExpression.All.Evaluate(new HashSet<Trigram>()));
        Assert.False(TrigramExpression.None.Evaluate(new HashSet<Trigram>()));
    }

    [Fact]
    public void Expression_CollectTrigrams()
    {
        var query = TrigramExpression.And(
            TrigramExpression.OfTrigram(Tg("foo")),
            TrigramExpression.Or(
                TrigramExpression.OfTrigram(Tg("bar")),
                TrigramExpression.OfTrigram(Tg("baz"))));
        var all = query.CollectTrigrams();
        Assert.Equal(3, all.Count);
        Assert.Contains(Tg("foo"), all);
        Assert.Contains(Tg("bar"), all);
        Assert.Contains(Tg("baz"), all);
    }

    // ─────────────────────── ContentRepresentation ───────────────────────

    private static HashSet<Trigram> Classify(byte[] bytes, out ContentRepresentationVerdict verdict)
    {
        verdict = ContentRepresentation.Classify(bytes, out var list);
        return new HashSet<Trigram>(list);
    }

    [Fact]
    public void Representation_AsciiText_IndexedWithTrigrams()
    {
        var v = ContentRepresentation.Classify(Encoding.ASCII.GetBytes("hello"), out var trigrams);
        Assert.Equal(ContentRepresentationVerdict.Indexed, v);
        var set = new HashSet<Trigram>(trigrams);
        Assert.Equal(new HashSet<Trigram> { Tg("hel"), Tg("ell"), Tg("llo") }, set);
        // Sorted, de-duplicated output.
        Assert.Equal(trigrams.OrderBy(t => t.Value).ToList(), trigrams.ToList());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void Representation_ShortInput_IndexedWithNoTrigrams(string text)
    {
        var v = ContentRepresentation.Classify(Encoding.ASCII.GetBytes(text), out var trigrams);
        Assert.Equal(ContentRepresentationVerdict.Indexed, v);
        Assert.Empty(trigrams);
    }

    [Fact]
    public void Representation_ThreeNewlines_HasLfTrigram()
    {
        // Plan §5.1 #7: three LF bytes DO contain trigram 0A 0A 0A.
        var v = ContentRepresentation.Classify(new byte[] { 0x0A, 0x0A, 0x0A }, out var trigrams);
        Assert.Equal(ContentRepresentationVerdict.Indexed, v);
        Assert.Single(trigrams);
        Assert.Equal(new Trigram(0x0A, 0x0A, 0x0A), trigrams[0]);
    }

    [Fact]
    public void Representation_NormalizesCrlfAndBareCrToLf()
    {
        var crlf = Classify(Encoding.ASCII.GetBytes("a\r\nb"), out var v1);
        Assert.Equal(ContentRepresentationVerdict.Indexed, v1);
        Assert.Equal(new HashSet<Trigram> { new(0x61, 0x0A, 0x62) }, crlf);
        Assert.DoesNotContain(crlf, t => t.Byte0 == 0x0D || t.Byte1 == 0x0D || t.Byte2 == 0x0D);

        var bareCr = Classify(Encoding.ASCII.GetBytes("a\rb"), out var v2);
        Assert.Equal(ContentRepresentationVerdict.Indexed, v2);
        Assert.Equal(new HashSet<Trigram> { new(0x61, 0x0A, 0x62) }, bareCr);
    }

    [Fact]
    public void Representation_ValidMultiByteUtf8_Indexed()
    {
        // "café" → 63 61 66 C3 A9
        var v = ContentRepresentation.Classify(Encoding.UTF8.GetBytes("café"), out var trigrams);
        Assert.Equal(ContentRepresentationVerdict.Indexed, v);
        var set = new HashSet<Trigram>(trigrams);
        Assert.Contains(new Trigram(0x63, 0x61, 0x66), set);
        Assert.Contains(new Trigram(0x66, 0xC3, 0xA9), set);
    }

    [Fact]
    public void Representation_Utf8Bom_StrippedAndIndexed()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'y' };
        Assert.Equal(ContentRepresentationVerdict.Indexed, ContentRepresentation.Classify(bytes, out var trigrams));
        Assert.Equal(new[] { new Trigram((byte)'h', (byte)'e', (byte)'y') }, trigrams);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })]           // UTF-16 LE BOM
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 })]           // UTF-16 BE BOM
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF })]           // UTF-32 BE BOM
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })]           // UTF-32 LE BOM
    public void Representation_Utf16And32Boms_Rejected(byte[] bytes)
    {
        Assert.Equal(ContentRepresentationVerdict.NotBomlessUtf8, ContentRepresentation.Classify(bytes, out _));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF }, true)]
    [InlineData(new byte[] { 0xEF, 0x00, 0xBF }, false)]
    [InlineData(new byte[] { 0xEF, 0xBB, 0x00 }, false)]
    public void Representation_GenericBomDetector_ChecksWholeUtf8Prefix(byte[] bytes, bool expected)
        => Assert.Equal(expected, ContentRepresentation.StartsWithBom(bytes));

    [Theory]
    [InlineData(new byte[] { 0x80 })]                    // invalid lead byte
    [InlineData(new byte[] { 0xC3, 0x28 })]              // bad continuation
    [InlineData(new byte[] { 0xC0, 0x80 })]              // overlong NUL
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]        // UTF-16 surrogate U+D800
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 })]  // > U+10FFFF
    [InlineData(new byte[] { 0xE2, 0x82 })]              // truncated 3-byte
    public void Representation_InvalidUtf8_Rejected(byte[] bytes)
    {
        Assert.Equal(ContentRepresentationVerdict.NotBomlessUtf8, ContentRepresentation.Classify(bytes, out _));
    }

    [Fact]
    public void Representation_NulByte_Binary()
    {
        var bytes = new byte[] { (byte)'a', 0x00, (byte)'b' };
        Assert.Equal(ContentRepresentationVerdict.Binary, ContentRepresentation.Classify(bytes, out _));
    }

    [Fact]
    public void Representation_LargeInput_SniffsOnlyLeadingBytes()
    {
        // Large valid-UTF8 body; binary sniff looks only at the first 8 KB.
        var bytes = Encoding.ASCII.GetBytes(new string('x', ContentRepresentation.BinarySniffBytes + 100));
        Assert.Equal(ContentRepresentationVerdict.Indexed, ContentRepresentation.Classify(bytes, out var trigrams));
        Assert.Single(trigrams); // only "xxx"
    }

    // ─────────────────────── IndexScopeIdentity ───────────────────────

    [Theory]
    [InlineData(@"C:\src\Yagu", @"C:\src\Yagu")]
    [InlineData("C:/src/Yagu", @"C:\src\Yagu")]
    [InlineData(@"C:\src\Yagu\", @"C:\src\Yagu")]
    [InlineData(@"C:\", @"C:\")]                              // bare drive root keeps its separator
    [InlineData("D:", @"D:\")]                                // bare drive letter canonicalized to the root
    [InlineData("d:/", @"d:\")]                               // forward-slash drive root canonicalized
    [InlineData(@"C:\src\\Yagu", @"C:\src\Yagu")]           // collapsed separators
    [InlineData(@"\\?\C:\src\Yagu", @"C:\src\Yagu")]        // long-path prefix stripped
    [InlineData(@"\\?\UNC\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData("  C:\\src\\Yagu  ", @"C:\src\Yagu")]
    public void ScopeIdentity_NormalizePath(string input, string expected)
    {
        Assert.Equal(expected, IndexScopeIdentity.NormalizePath(input));
    }

    [Fact]
    public void ScopeIdentity_BareDriveLetterAndRoot_ShareOneScopeId()
    {
        // "D:" (from the search box) and "D:\" (from the folder picker) must resolve to the SAME index
        // scope, so a drive indexed one way is found when searched the other way.
        Assert.Equal(@"D:\", IndexScopeIdentity.NormalizePath("D:"));
        Assert.Equal(
            ContentIndexManager.ScopeIdForRoot("D:"),
            ContentIndexManager.ScopeIdForRoot(@"D:\"));
    }

    [Fact]
    public void ScopeIdentity_NormalizePath_Empty()
    {
        Assert.Equal(string.Empty, IndexScopeIdentity.NormalizePath("   "));
    }

    [Fact]
    public void ScopeIdentity_NormalizePath_PreservesCase()
    {
        Assert.Equal(@"C:\Src\YaGu", IndexScopeIdentity.NormalizePath(@"C:\Src\YaGu"));
    }

    [Fact]
    public void ScopeIdentity_ComputeScopeId_StableAndHex()
    {
        string id = IndexScopeIdentity.ComputeScopeId("vol-guid", @"C:\src\Yagu");
        Assert.Equal(32, id.Length);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, IndexScopeIdentity.ComputeScopeId("vol-guid", @"C:\src\Yagu"));
    }

    [Fact]
    public void ScopeIdentity_ComputeScopeId_VolumeIsCaseInsensitive_PathIsCaseSensitive()
    {
        Assert.Equal(
            IndexScopeIdentity.ComputeScopeId("VOL-GUID", @"C:\src"),
            IndexScopeIdentity.ComputeScopeId("vol-guid", @"C:\src"));

        Assert.NotEqual(
            IndexScopeIdentity.ComputeScopeId("vol", @"C:\src"),
            IndexScopeIdentity.ComputeScopeId("vol", @"C:\Src"));
    }

    [Fact]
    public void ScopeIdentity_ComputeScopeId_DiffersByVolumeAndPath()
    {
        string a = IndexScopeIdentity.ComputeScopeId("vol-a", @"C:\src");
        string b = IndexScopeIdentity.ComputeScopeId("vol-b", @"C:\src");
        string c = IndexScopeIdentity.ComputeScopeId("vol-a", @"C:\other");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }
}
