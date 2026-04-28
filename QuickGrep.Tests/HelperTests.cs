using System.Text;
using QuickGrep.Helpers;

namespace QuickGrep.Tests;

public class BinaryDetectorTests
{
    [Fact]
    public void TextContent_IsNotBinary()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello world\nthis is text"));
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void NullByte_IsBinary()
    {
        var bytes = new byte[] { (byte)'a', 0, (byte)'b' };
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void GzipMagic_IsBinary()
    {
        var bytes = new byte[] { 0x1F, 0x8B, 0x08, 0x00, (byte)'a', (byte)'b' };
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void ZipMagic_IsBinary()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, (byte)'x' };
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void PngMagic_IsBinary()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void PdfMagic_IsBinary()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void SqliteMagic_IsBinary()
    {
        var bytes = Encoding.ASCII.GetBytes("SQLite format 3\0");
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void HighControlByteRatio_IsBinary()
    {
        // 600 bytes, 90% are 0x01 (suspicious control), no NULs.
        var bytes = new byte[600];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (i % 10 == 0) ? (byte)'a' : (byte)0x01;
        using var ms = new MemoryStream(bytes);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void Utf8NonAsciiText_IsNotBinary()
    {
        // Bytes >= 0x80 should not trip the heuristic.
        var bytes = Encoding.UTF8.GetBytes(new string('é', 1000));
        using var ms = new MemoryStream(bytes);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void ShortNonBinaryStaysText()
    {
        // <512 bytes triggers neither magic nor ratio heuristic.
        var bytes = Encoding.UTF8.GetBytes("short text without nul");
        using var ms = new MemoryStream(bytes);
        Assert.False(BinaryDetector.IsBinary(ms));
    }
}

public class EncodingDetectorTests
{
    [Fact]
    public void Utf8Bom_Detected()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        using var ms = new MemoryStream(bom);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal(Encoding.UTF8.WebName, enc.WebName);
    }

    [Fact]
    public void Utf16Le_Detected()
    {
        var bom = new byte[] { 0xFF, 0xFE, (byte)'a', 0 };
        using var ms = new MemoryStream(bom);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal(Encoding.Unicode.WebName, enc.WebName);
    }

    [Fact]
    public void Bomless_FallsBackToUtf8Strict()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        using var ms = new MemoryStream(data);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal("utf-8", enc.WebName);
    }
}

public class LineTruncatorTests
{
    [Fact]
    public void Short_PassThrough()
    {
        Assert.Equal("hi", LineTruncator.Truncate("hi"));
    }

    [Fact]
    public void Long_Truncated()
    {
        var s = new string('x', 1500);
        var t = LineTruncator.Truncate(s);
        Assert.True(t.Length <= LineTruncator.TruncatedLength + 1);
        Assert.EndsWith(LineTruncator.Ellipsis, t);
    }

    [Fact]
    public void Long_TruncateAroundMatch_KeepsMatchVisible()
    {
        var s = new string('a', 1200) + "NEEDLE" + new string('b', 1200);
        var display = LineTruncator.TruncateAroundMatch(s, 1200, "NEEDLE".Length);

        Assert.Contains("NEEDLE", display.Text);
        Assert.Equal(display.Text.IndexOf("NEEDLE", StringComparison.Ordinal), display.MatchStart);
        Assert.StartsWith(LineTruncator.Ellipsis, display.Text);
        Assert.EndsWith(LineTruncator.Ellipsis, display.Text);
    }
}

public class GlobMatcherTests
{
    [Fact]
    public void IncludeBareExtension_Matches()
    {
        var m = new GlobMatcher(new[] { "ts" }, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\proj\src\file.ts"));
        Assert.False(m.Matches(@"C:\proj\src\file.js"));
    }

    [Fact]
    public void Exclude_Wins()
    {
        var m = new GlobMatcher(new[] { "**/*.ts" }, new[] { "node_modules" });
        Assert.False(m.Matches(@"C:\proj\node_modules\foo.ts"));
        Assert.True(m.Matches(@"C:\proj\src\foo.ts"));
    }

    [Fact]
    public void NoIncludes_AllowsEverythingNotExcluded()
    {
        var m = new GlobMatcher(Array.Empty<string>(), new[] { "*.tmp" });
        Assert.True(m.Matches(@"C:\a.cs"));
        Assert.False(m.Matches(@"C:\a.tmp"));
    }
}
