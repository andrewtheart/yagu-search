using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ExcludedMethodTests_NativeMatchDecoder
{
    [Fact]
    public unsafe void DecodeMatchLine_NullPtr_ReturnsEmpty()
    {
        var (line, start, len) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(null, 0, 0, 0);
        Assert.Equal(string.Empty, line);
        Assert.Equal(0, start);
        Assert.Equal(0, len);
    }

    [Fact]
    public unsafe void DecodeMatchLine_ZeroLen_ReturnsEmpty()
    {
        byte dummy = 0x41;
        var (line, start, len) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(&dummy, 0, 0, 0);
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public unsafe void DecodeMatchLine_ValidUtf8_DecodesCorrectly()
    {
        // "hello" in UTF-8
        byte[] data = Encoding.UTF8.GetBytes("hello world");
        fixed (byte* ptr = data)
        {
            // match "world" starts at byte 6, length 5
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 6, 5);
            Assert.Contains("hello world", line);
            Assert.Equal(6, start);
            Assert.Equal(5, matchLen);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_MultibyteUtf8_DecodesCorrectly()
    {
        // "café" in UTF-8: 63 61 66 c3 a9 => match "é" at byte 3 (2 bytes)
        byte[] data = Encoding.UTF8.GetBytes("café");
        fixed (byte* ptr = data)
        {
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 3, 2);
            Assert.Contains("café", line);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_AsciiLine_UsesAsciFastPath()
    {
        // Pure ASCII: byte offsets == char offsets, so fast path is taken.
        // Verify result is correct (same as before but exercises the IsAsciiRegion branch).
        byte[] data = Encoding.UTF8.GetBytes("function hello() { return 42; }");
        fixed (byte* ptr = data)
        {
            // match "hello" at byte 9, length 5
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 9, 5);
            Assert.Contains("hello", line);
            Assert.Equal(9, start);
            Assert.Equal(5, matchLen);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_NonAsciiPrefix_FallsBackToGetCharCount()
    {
        // "日本語hello" — first 9 bytes are multi-byte (3 chars × 3 bytes each),
        // then "hello" at byte offset 9, length 5. Char offset for "hello" is 3, not 9.
        byte[] data = Encoding.UTF8.GetBytes("日本語hello");
        fixed (byte* ptr = data)
        {
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 9, 5);
            Assert.Contains("hello", line);
            Assert.Equal(3, start);   // char offset, not byte offset
            Assert.Equal(5, matchLen);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_EmptyMatchRegion_AsciiPathReturnsZeros()
    {
        byte[] data = Encoding.UTF8.GetBytes("abc");
        fixed (byte* ptr = data)
        {
            // matchStartBytes=0, matchLenBytes=0 → fast path (0 bytes is vacuously ASCII)
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 0, 0);
            Assert.Equal(0, start);
            Assert.Equal(0, matchLen);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_LongLine_KeepsDisplayAndSourceMatchStarts()
    {
        int oldLength = Yagu.Helpers.LineTruncator.TruncatedLength;
        Yagu.Helpers.LineTruncator.TruncatedLength = 500;
        try
        {
            string text = new string('a', 1500) + "NEEDLE" + new string('b', 1500);
            byte[] data = Encoding.UTF8.GetBytes(text);
            fixed (byte* ptr = data)
            {
                var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 1500, 6);

                Assert.Contains("NEEDLE", decoded.Line);
                Assert.Equal(decoded.Line.IndexOf("NEEDLE", StringComparison.Ordinal), decoded.MatchStart);
                Assert.Equal(1500, decoded.SourceMatchStart);
                Assert.NotEqual(decoded.MatchStart, decoded.SourceMatchStart);
            }
        }
        finally
        {
            Yagu.Helpers.LineTruncator.TruncatedLength = oldLength;
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_NativeTruncatedLine_KeepsAbsoluteSourceMatchStart()
    {
        int oldLength = Yagu.Helpers.LineTruncator.TruncatedLength;
        Yagu.Helpers.LineTruncator.TruncatedLength = 0;
        try
        {
            string text = Yagu.Helpers.LineTruncator.Ellipsis + new string('a', 2497) + "THE_NEEDLE" + new string('b', 1000);
            byte[] data = Encoding.UTF8.GetBytes(text);
            fixed (byte* ptr = data)
            {
                int matchStart = text.IndexOf("THE_NEEDLE", StringComparison.Ordinal);
                int matchStartBytes = Encoding.UTF8.GetByteCount(text.AsSpan(0, matchStart));
                var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                    ptr,
                    data.Length,
                    matchStartBytes,
                    "THE_NEEDLE".Length,
                    sourceMatchStartBytes: 2500);

                Assert.Contains("THE_NEEDLE", decoded.Line);
                Assert.Equal(matchStart, decoded.MatchStart);
                Assert.Equal(2500, decoded.SourceMatchStart);
            }
        }
        finally
        {
            Yagu.Helpers.LineTruncator.TruncatedLength = oldLength;
        }
    }

    [Fact]
    public unsafe void DecodeAndTruncate_NullPtr_ReturnsEmpty()
    {
        string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(null, 0);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public unsafe void DecodeAndTruncate_ZeroLen_ReturnsEmpty()
    {
        byte dummy = 0x41;
        string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(&dummy, 0);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public unsafe void DecodeAndTruncate_ShortString_ReturnsFullString()
    {
        byte[] data = Encoding.UTF8.GetBytes("short line");
        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, data.Length);
            Assert.Equal("short line", result);
        }
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_NullPtr_ReturnsEmpty()
    {
        var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(null, 0, 0);
        Assert.Empty(result);
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_ZeroCount_ReturnsEmpty()
    {
        byte dummy = 0x41;
        var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(&dummy, 1, 0);
        Assert.Empty(result);
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_ValidData_DecodesLines()
    {
        // Pack two lines: "abc" and "de"
        // Format: u32 len + bytes for each line
        var line1 = Encoding.UTF8.GetBytes("abc");
        var line2 = Encoding.UTF8.GetBytes("de");
        var buffer = new byte[4 + line1.Length + 4 + line2.Length];
        BitConverter.GetBytes((uint)line1.Length).CopyTo(buffer, 0);
        line1.CopyTo(buffer, 4);
        BitConverter.GetBytes((uint)line2.Length).CopyTo(buffer, 4 + line1.Length);
        line2.CopyTo(buffer, 4 + line1.Length + 4);

        fixed (byte* ptr = buffer)
        {
            var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(ptr, (nuint)buffer.Length, 2);
            Assert.Equal(2, result.Count);
            Assert.Equal("abc", result[0]);
            Assert.Equal("de", result[1]);
        }
    }
}
