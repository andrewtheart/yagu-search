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
