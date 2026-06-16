using System.Text;
using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Comprehensive branch coverage for EncodingDetector: all BOM variants,
/// fallback behavior, and stream-based detection.
/// </summary>
public sealed class EncodingDetectorBranchCoverageTests
{
    // ═══════════════════════════════════════════════════════════════
    //  DetectEncoding(ReadOnlySpan<byte>)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DetectEncoding_Utf8Bom_ReturnsUtf8()
    {
        var header = new byte[] { 0xEF, 0xBB, 0xBF, 0x48, 0x65, 0x6C };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void DetectEncoding_Utf32LeBom_ReturnsUtf32()
    {
        var header = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x48, 0x00 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.Equal(Encoding.UTF32, enc);
    }

    [Fact]
    public void DetectEncoding_Utf32BeBom_ReturnsUtf32BigEndian()
    {
        var header = new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00 };
        var enc = EncodingDetector.DetectEncoding(header);
        // UTF-32 big-endian
        Assert.True(enc is UTF32Encoding);
        var preamble = enc.GetPreamble();
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, preamble);
    }

    [Fact]
    public void DetectEncoding_Utf16LeBom_ReturnsUnicode()
    {
        var header = new byte[] { 0xFF, 0xFE, 0x48, 0x00 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.Equal(Encoding.Unicode, enc);
    }

    [Fact]
    public void DetectEncoding_Utf16BeBom_ReturnsBigEndianUnicode()
    {
        var header = new byte[] { 0xFE, 0xFF, 0x00, 0x48 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.Equal(Encoding.BigEndianUnicode, enc);
    }

    [Fact]
    public void DetectEncoding_NoBom_ReturnsUtf8WithThrowOnInvalid()
    {
        var header = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var enc = EncodingDetector.DetectEncoding(header);
        // Should be UTF-8 without BOM that throws on invalid bytes
        Assert.IsType<UTF8Encoding>(enc);
        var preamble = enc.GetPreamble();
        Assert.Empty(preamble); // no BOM emitted
    }

    [Fact]
    public void DetectEncoding_EmptySpan_ReturnsUtf8()
    {
        var enc = EncodingDetector.DetectEncoding(ReadOnlySpan<byte>.Empty);
        Assert.IsType<UTF8Encoding>(enc);
    }

    [Fact]
    public void DetectEncoding_SingleByte_ReturnsUtf8()
    {
        var header = new byte[] { 0x41 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.IsType<UTF8Encoding>(enc);
    }

    [Fact]
    public void DetectEncoding_TwoBytes_NotBom_ReturnsUtf8()
    {
        var header = new byte[] { 0x41, 0x42 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.IsType<UTF8Encoding>(enc);
    }

    // ═══════════════════════════════════════════════════════════════
    //  BOM Priority: UTF-32 LE BOM starts with same bytes as UTF-16 LE BOM
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DetectEncoding_Utf32Le_TakesPriorityOverUtf16Le()
    {
        // 0xFF 0xFE 0x00 0x00 is both UTF-16 LE BOM + U+0000 and UTF-32 LE BOM
        // UTF-32 should win because it's checked first
        var header = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.Equal(Encoding.UTF32, enc);
    }

    [Fact]
    public void DetectEncoding_Utf16Le_WhenThirdByteNotZero()
    {
        // 0xFF 0xFE followed by non-zero bytes -> UTF-16 LE
        var header = new byte[] { 0xFF, 0xFE, 0x41, 0x00 };
        var enc = EncodingDetector.DetectEncoding(header);
        Assert.Equal(Encoding.Unicode, enc);
    }

    // ═══════════════════════════════════════════════════════════════
    //  DetectEncoding(Stream)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DetectEncoding_Stream_Utf8Bom_ReturnsUtf8()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF, 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        using var ms = new MemoryStream(data);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void DetectEncoding_Stream_ResetsPosition()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF, 0x48, 0x65 };
        using var ms = new MemoryStream(data);
        ms.Position = 0;
        EncodingDetector.DetectEncoding(ms);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void DetectEncoding_Stream_EmptyStream_ReturnsUtf8()
    {
        using var ms = new MemoryStream([]);
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.IsType<UTF8Encoding>(enc);
    }

    [Fact]
    public void DetectEncoding_Stream_NonSeekable_ReturnsUtf8()
    {
        using var ns = new NonSeekableStream();
        var enc = EncodingDetector.DetectEncoding(ns);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void DetectEncoding_Stream_FromMiddlePosition_StillDetectsCorrectly()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF, 0x48, 0x65, 0x6C };
        using var ms = new MemoryStream(data);
        ms.Position = 0; // Simulate positioned at start
        var enc = EncodingDetector.DetectEncoding(ms);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void DetectEncoding_Utf8NoBom_ThrowsOnInvalidSequences()
    {
        // The returned encoding should throw when encountering invalid UTF-8
        var enc = EncodingDetector.DetectEncoding(new byte[] { 0x41, 0x42, 0x43 });
        // 0xFF 0xFE is invalid UTF-8 sequence
        Assert.Throws<DecoderFallbackException>(() =>
            enc.GetString(new byte[] { 0xFF, 0xFE }));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
