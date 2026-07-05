using System.Text;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Tests;

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

    [Fact]
    public void RegexInclude_MatchesNormalizedPath()
    {
        var m = new GlobMatcher([@"\.(cs|xaml)$"], [], FilterPatternMode.Regex);

        Assert.True(m.Matches(@"C:\proj\MainWindow.xaml"));
        Assert.True(m.Matches(@"C:\proj\Program.cs"));
        Assert.False(m.Matches(@"C:\proj\README.md"));
    }

    [Fact]
    public void RegexExclude_Wins()
    {
        var m = new GlobMatcher([], [@"(^|/)node_modules/|\.min\.js$"], excludeMode: FilterPatternMode.Regex);

        Assert.False(m.Matches(@"C:\proj\node_modules\pkg\index.js"));
        Assert.False(m.Matches(@"C:\proj\src\app.min.js"));
        Assert.True(m.Matches(@"C:\proj\src\app.js"));
    }

    [Fact]
    public void InvalidRegex_DoesNotThrow()
    {
        var include = new GlobMatcher(["["], [], FilterPatternMode.Regex);
        var exclude = new GlobMatcher([], ["["], excludeMode: FilterPatternMode.Regex);

        Assert.False(include.Matches(@"C:\proj\Program.cs"));
        Assert.True(exclude.Matches(@"C:\proj\Program.cs"));
    }
}

// ─── BinaryDetector: file path overload + more magic bytes ──────────────

public class BinaryDetectorPathTests
{
    [Fact]
    public void IsBinary_FilePath_TextFile_ReturnsFalse()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "plain text content");
            Assert.False(BinaryDetector.IsBinary(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void IsBinary_FilePath_BinaryFile_ReturnsTrue()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // PNG
            Assert.True(BinaryDetector.IsBinary(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void IsBinary_FilePath_NonExistent_ReturnsTrue()
    {
        Assert.True(BinaryDetector.IsBinary(@"Z:\nonexistent\path\file.dat"));
    }

    [Fact]
    public void JpegMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void ElfMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void MzMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void SevenZipMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void SevenZipMagic_IsDetected()
    {
        Assert.True(BinaryDetector.IsSevenZipMagic(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }));
        Assert.False(BinaryDetector.IsSevenZipMagic(new byte[] { 0x37, 0x7A, 0x00, 0x00 }));
    }

    [Fact]
    public void ZstdMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void MachO32Magic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0xCE, 0xFA, 0xED, 0xFE });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void MachO64Magic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void MachOFatMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void Bzip2Magic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x42, 0x5A, 0x68, 0x39 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void XzMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void RarMagic_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void EmptySpan_IsNotBinary()
    {
        Assert.False(BinaryDetector.IsBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ShortBytes_NoMagic_NotBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x61, 0x62 });
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void EmptyStream_NotBinary()
    {
        using var ms = new MemoryStream(Array.Empty<byte>());
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void ZipPk0506_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x50, 0x4B, 0x05, 0x06, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void ZipPk0708_IsBinary()
    {
        using var ms = new MemoryStream(new byte[] { 0x50, 0x4B, 0x07, 0x08, 0x00 });
        Assert.True(BinaryDetector.IsBinary(ms));
    }
}

// ─── EncodingDetector: remaining BOM patterns ───────────────────────────

public class EncodingDetectorExtraTests
{
    [Fact]
    public void Utf32Le_Detected()
    {
        var bom = new byte[] { 0xFF, 0xFE, 0x00, 0x00, (byte)'a', 0, 0, 0 };
        using var ms = new MemoryStream(bom);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal(Encoding.UTF32.WebName, enc.WebName);
    }

    [Fact]
    public void Utf32Be_Detected()
    {
        var bom = new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, (byte)'a' };
        using var ms = new MemoryStream(bom);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.True(enc is UTF32Encoding);
    }

    [Fact]
    public void BigEndianUnicode_Detected()
    {
        var bom = new byte[] { 0xFE, 0xFF, 0x00, (byte)'h' };
        using var ms = new MemoryStream(bom);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal(Encoding.BigEndianUnicode.WebName, enc.WebName);
    }

    [Fact]
    public void NonSeekableStream_FallsBackToUtf8()
    {
        using var ms = new NonSeekableStream(new byte[] { (byte)'h', (byte)'i' });
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal("utf-8", enc.WebName);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

// ─── LineTruncator: edge cases ──────────────────────────────────────────

public class LineTruncatorExtraTests
{
    [Fact]
    public void SetTruncatedLength_ClampsToMinimum50()
    {
        int original = LineTruncator.TruncatedLength;
        try
        {
            LineTruncator.TruncatedLength = 10;
            Assert.Equal(50, LineTruncator.TruncatedLength);
        }
        finally { LineTruncator.TruncatedLength = original; }
    }

    [Fact]
    public void SetTruncatedLength_ZeroDisablesTruncation()
    {
        int original = LineTruncator.TruncatedLength;
        try
        {
            LineTruncator.TruncatedLength = 0;
            Assert.Equal(0, LineTruncator.TruncatedLength);
            var longLine = new string('x', 5000);
            Assert.Equal(longLine, LineTruncator.Truncate(longLine));
            var result = LineTruncator.TruncateAroundMatch(longLine, 2500, 10);
            Assert.Equal(longLine, result.Text);
            Assert.Equal(2500, result.MatchStart);
        }
        finally { LineTruncator.TruncatedLength = original; }
    }

    [Fact]
    public void SetTruncatedLength_AcceptsValidValue()
    {
        int original = LineTruncator.TruncatedLength;
        try
        {
            LineTruncator.TruncatedLength = 200;
            Assert.Equal(200, LineTruncator.TruncatedLength);
        }
        finally { LineTruncator.TruncatedLength = original; }
    }

    [Fact]
    public void Truncate_NullLine_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LineTruncator.Truncate(null!));
    }

    [Fact]
    public void TruncateAroundMatch_NullLine_ReturnsEmpty()
    {
        var result = LineTruncator.TruncateAroundMatch(null, 0, 5);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_NegativeMatchStart_FallsBackToTruncate()
    {
        var longLine = new string('x', 2000);
        var result = LineTruncator.TruncateAroundMatch(longLine, -1, 5);
        Assert.EndsWith(LineTruncator.Ellipsis, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_MatchNearEnd_OnlyPrefixEllipsis()
    {
        var line = new string('a', 1200) + "NEEDLE";
        var result = LineTruncator.TruncateAroundMatch(line, 1200, "NEEDLE".Length);
        Assert.Contains("NEEDLE", result.Text);
        Assert.StartsWith(LineTruncator.Ellipsis, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_MatchNearStart_OnlySuffixEllipsis()
    {
        var line = "NEEDLE" + new string('b', 1200);
        var result = LineTruncator.TruncateAroundMatch(line, 0, "NEEDLE".Length);
        Assert.Contains("NEEDLE", result.Text);
        Assert.EndsWith(LineTruncator.Ellipsis, result.Text);
        Assert.False(result.Text.StartsWith(LineTruncator.Ellipsis));
    }

    [Fact]
    public void TruncateAroundMatch_ShortLine_NoTruncation()
    {
        var result = LineTruncator.TruncateAroundMatch("short", 0, 5);
        Assert.Equal("short", result.Text);
        Assert.Equal(0, result.MatchStart);
    }

    [Fact]
    public void TruncateAroundMatch_ZeroMatchLength_FallsToTruncate()
    {
        var longLine = new string('x', 2000);
        var result = LineTruncator.TruncateAroundMatch(longLine, 500, 0);
        Assert.EndsWith(LineTruncator.Ellipsis, result.Text);
    }

    [Fact]
    public void MaxDisplayLength_IsTwiceTruncatedLength()
    {
        Assert.Equal(LineTruncator.TruncatedLength * 2, LineTruncator.MaxDisplayLength);
    }
}

// ─── GlobMatcher: additional edge cases ─────────────────────────────────

public class GlobMatcherExtraTests
{
    [Fact]
    public void QuestionMark_MatchesSingleChar()
    {
        var m = new GlobMatcher(new[] { "file?.txt" }, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\dir\file1.txt"));
        Assert.False(m.Matches(@"C:\dir\file12.txt"));
    }

    [Fact]
    public void DoubleStarGlob_MatchesDeepPaths()
    {
        var m = new GlobMatcher(new[] { "**/test/**/*.cs" }, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\proj\test\sub\file.cs"));
        Assert.False(m.Matches(@"C:\proj\src\file.cs"));
    }

    [Fact]
    public void CommaSeparatedInclude_Both()
    {
        var m = new GlobMatcher(new[] { "cs,ts" }, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\a.cs"));
        Assert.True(m.Matches(@"C:\a.ts"));
        Assert.False(m.Matches(@"C:\a.py"));
    }

    [Fact]
    public void SemicolonSeparatedExclude()
    {
        var m = new GlobMatcher(Array.Empty<string>(), new[] { "bin;obj" });
        Assert.False(m.Matches(@"C:\proj\output.bin"));
        Assert.False(m.Matches(@"C:\proj\data.obj"));
        Assert.True(m.Matches(@"C:\proj\src\file.cs"));
    }

    [Fact]
    public void DotExtensionPattern()
    {
        var m = new GlobMatcher(new[] { "*.tsx" }, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\proj\app.tsx"));
        Assert.False(m.Matches(@"C:\proj\app.ts"));
    }

    [Fact]
    public void LongSegmentName_IsSegmentMatch()
    {
        var m = new GlobMatcher(Array.Empty<string>(), new[] { "node_modules" });
        Assert.False(m.Matches(@"C:\proj\node_modules\foo.js"));
    }

    [Fact]
    public void DotInPattern_TreatedAsSegment()
    {
        var m = new GlobMatcher(Array.Empty<string>(), new[] { ".git" });
        Assert.False(m.Matches(@"C:\proj\.git\config"));
        Assert.True(m.Matches(@"C:\proj\src\file.cs"));
    }

    [Fact]
    public void EmptyInclude_EmptyExclude_MatchesAll()
    {
        var m = new GlobMatcher(Array.Empty<string>(), Array.Empty<string>());
        Assert.True(m.Matches(@"C:\anything\at\all.txt"));
    }

    [Fact]
    public void WhitespacePattern_Skipped()
    {
        var m = new GlobMatcher(new[] { "  ", "" }, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\a.txt"));
    }

    [Fact]
    public void NullInclude_DoesNotThrow()
    {
        var m = new GlobMatcher(null!, Array.Empty<string>());
        Assert.True(m.Matches(@"C:\a.txt"));
    }
}

// ─── GlobMatcher ** edge cases ──────────────────────────────────────────

public class GlobMatcherDoubleStarEdgeTests
{
    [Fact]
    public void DoubleStarNotFollowedBySlash_Matches()
    {
        var matcher = new GlobMatcher(["**x"], []);
        Assert.True(matcher.Matches("test/foox"));
    }

    [Fact]
    public void DoubleStarAtEnd_Matches()
    {
        var matcher = new GlobMatcher(["src/**"], []);
        Assert.True(matcher.Matches("src/foo/bar"));
    }
}
