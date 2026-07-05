using System.Text;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class NativeMatchDecoderTests
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

    [Fact]
    public unsafe void DecodeMatchLine_LongLine_TriggersWindowingPath()
    {
        // Line > MaxDisplayLength * 4 bytes triggers the windowed decode path
        int longLen = (Helpers.LineTruncator.MaxDisplayLength + 1) * 4 + 100;
        var data = new byte[longLen];
        Array.Fill(data, (byte)'x');
        // Place a match somewhere in the middle
        int matchStart = longLen / 2;
        int matchLen = 10;
        Array.Fill(data, (byte)'M', matchStart, matchLen);

        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, longLen, matchStart, matchLen);
            Assert.Contains("M", result.Line);
            Assert.True(result.Line.Length < longLen); // was truncated
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_LongLineWithUtf8_AlignsBoundaries()
    {
        // Build a line with multi-byte UTF-8 chars that forces AlignUtf8Start/End
        int longLen = (Helpers.LineTruncator.MaxDisplayLength + 1) * 4 + 200;
        var data = new byte[longLen];
        Array.Fill(data, (byte)'a');
        // Insert 3-byte UTF-8 chars (e.g. 'あ' = E3 81 82) near window boundaries
        int matchStart = longLen / 2;
        // Put continuation bytes where the window start might land
        int windowGuess = matchStart - 500;
        if (windowGuess > 0 && windowGuess + 2 < longLen)
        {
            data[windowGuess] = 0xE3;     // start of 3-byte sequence
            data[windowGuess + 1] = 0x81; // continuation
            data[windowGuess + 2] = 0x82; // continuation
        }

        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, longLen, matchStart, 5);
            // Should not throw and should produce a valid string
            Assert.NotNull(result.Line);
            Assert.True(result.MatchLength >= 0);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_SourceMatchStartBytes_PreservedInResult()
    {
        byte[] data = Encoding.UTF8.GetBytes("hello world match here");
        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 12, 5, sourceMatchStartBytes: 42);
            Assert.Equal(42, result.SourceMatchStart);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_NonAsciiRegion_ConvertsCharOffsets()
    {
        // "日本語needle日本語" — multi-byte chars before the match
        byte[] data = Encoding.UTF8.GetBytes("日本語needle日本語");
        int matchStartBytes = Encoding.UTF8.GetByteCount("日本語"); // 9 bytes for 3 chars
        int matchLenBytes = Encoding.UTF8.GetByteCount("needle");   // 6 bytes = 6 chars

        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, matchStartBytes, matchLenBytes);
            Assert.Contains("needle", result.Line);
            Assert.Equal(3, result.MatchStart); // 3 CJK chars before match
            Assert.Equal(6, result.MatchLength);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_MatchStartBeyondLen_ClampsGracefully()
    {
        byte[] data = Encoding.UTF8.GetBytes("short");
        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 999, 5);
            // matchStart clamped to len, matchLen clamped to 0
            Assert.Equal("short", result.Line);
            Assert.Equal(5, result.MatchStart); // clamped to end
            Assert.Equal(0, result.MatchLength);
        }
    }

    [Fact]
    public unsafe void DecodeAndTruncate_ShortString_ReturnsUntruncated()
    {
        byte[] data = Encoding.UTF8.GetBytes("short string");
        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, data.Length);
            Assert.Equal("short string", result);
        }
    }

    [Fact]
    public unsafe void DecodeAndTruncate_VeryLongAscii_Truncates()
    {
        int longLen = (Helpers.LineTruncator.MaxDisplayLength + 1) * 4 + 100;
        var data = new byte[longLen];
        Array.Fill(data, (byte)'z');

        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, longLen);
            // Should be truncated to TruncatedLength + ellipsis
            Assert.True(result.Length <= Helpers.LineTruncator.TruncatedLength + 2);
            Assert.Contains("…", result);
        }
    }

    [Fact]
    public unsafe void DecodeAndTruncate_VeryLongUtf8_AlignsToCharBoundary()
    {
        // Build a long buffer with multi-byte chars near the truncation point
        int longLen = (Helpers.LineTruncator.MaxDisplayLength + 1) * 4 + 100;
        var data = new byte[longLen];
        Array.Fill(data, (byte)'a');
        // Put a 3-byte char at the exact truncation boundary
        int maxBytes = (Helpers.LineTruncator.MaxDisplayLength + 1) * 4;
        if (maxBytes - 1 >= 0 && maxBytes + 1 < longLen)
        {
            data[maxBytes - 1] = 0xE3;
            data[maxBytes] = 0x81;
            data[maxBytes + 1] = 0x82;
        }

        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, longLen);
            // Should produce valid string without broken UTF-8
            Assert.NotNull(result);
            Assert.True(result.Length > 0);
        }
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_MalformedLength_Stops()
    {
        // Craft data where length field points beyond totalBytes
        var data = new byte[8];
        // First line: length = 9999 (way beyond buffer)
        BitConverter.GetBytes((uint)9999).CopyTo(data, 0);

        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(ptr, (nuint)data.Length, 2);
            // Should stop at malformed entry
            Assert.True(result.Count <= 1);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_LongLineWindowEnd_AtEndOfBuffer()
    {
        // Match near the end of a very long line — exercises windowEnd == len branch
        int longLen = (Helpers.LineTruncator.MaxDisplayLength + 1) * 4 + 100;
        var data = new byte[longLen];
        Array.Fill(data, (byte)'q');
        int matchStart = longLen - 20; // match near end

        fixed (byte* ptr = data)
        {
            var result = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, longLen, matchStart, 10);
            Assert.NotNull(result.Line);
            // suffix should NOT have ellipsis since window reaches end
            Assert.DoesNotContain("…", result.Line.AsSpan(result.Line.Length - 2).ToString());
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_LongNonAsciiLine_WindowFallsBackToGetCharCount()
    {
        // Line > MaxDisplayLength*4 bytes with non-ASCII in the match region
        // Forces the truncation code path with IsAsciiRegion returning false
        string prefix = new string('あ', 1500); // 1500 × 3 = 4500 bytes
        string needle = "NEEDLE";
        string suffix = new string('い', 500);
        string fullText = prefix + needle + suffix;
        byte[] data = Encoding.UTF8.GetBytes(fullText);

        int matchStartBytes = Encoding.UTF8.GetByteCount(prefix);
        int matchLenBytes = Encoding.UTF8.GetByteCount(needle);

        fixed (byte* ptr = data)
        {
            var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, matchStartBytes, matchLenBytes);

            Assert.Contains("NEEDLE", decoded.Line);
            Assert.Equal(decoded.Line.IndexOf("NEEDLE", StringComparison.Ordinal), decoded.MatchStart);
            Assert.Equal(needle.Length, decoded.MatchLength);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_LongNonAscii_WithSourceMatchStart()
    {
        // Long non-ASCII line with explicit sourceMatchStartBytes
        string prefix = new string('あ', 1500);
        string needle = "NEEDLE";
        string suffix = new string('い', 500);
        string fullText = prefix + needle + suffix;
        byte[] data = Encoding.UTF8.GetBytes(fullText);

        int matchStartBytes = Encoding.UTF8.GetByteCount(prefix);
        int matchLenBytes = Encoding.UTF8.GetByteCount(needle);

        fixed (byte* ptr = data)
        {
            var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                ptr, data.Length, matchStartBytes, matchLenBytes,
                sourceMatchStartBytes: prefix.Length);

            Assert.Contains("NEEDLE", decoded.Line);
            Assert.Equal(prefix.Length, decoded.SourceMatchStart);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_LongNonAscii_WithoutSourceMatchStart_ComputesFromBytes()
    {
        // Long non-ASCII without sourceMatchStartBytes — triggers
        // sourceMatchStart = GetCharCount(ptr, safeStartBytes) fallback
        string prefix = new string('日', 1500); // 3 bytes each = 4500 bytes
        string needle = "XY";
        string suffix = new string('本', 500);
        string fullText = prefix + needle + suffix;
        byte[] data = Encoding.UTF8.GetBytes(fullText);

        int matchStartBytes = Encoding.UTF8.GetByteCount(prefix);
        int matchLenBytes = Encoding.UTF8.GetByteCount(needle);

        fixed (byte* ptr = data)
        {
            var decoded = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, matchStartBytes, matchLenBytes);

            Assert.Contains("XY", decoded.Line);
            // SourceMatchStart should be char count of prefix, not byte count
            Assert.Equal(prefix.Length, decoded.SourceMatchStart);
        }
    }
}
