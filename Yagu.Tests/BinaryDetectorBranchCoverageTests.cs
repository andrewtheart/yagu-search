using System.Text;
using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Comprehensive branch coverage for BinaryDetector: magic numbers, NUL bytes,
/// heuristic thresholds, Zip/7z detection, and stream-based detection.
/// </summary>
public sealed class BinaryDetectorBranchCoverageTests
{
    // ═══════════════════════════════════════════════════════════════
    //  IsBinary(ReadOnlySpan<byte>)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsBinary_EmptySpan_ReturnsFalse()
    {
        Assert.False(BinaryDetector.IsBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsBinary_PureAsciiText_ReturnsFalse()
    {
        var text = Encoding.ASCII.GetBytes("Hello world, this is normal text with numbers 12345 and symbols !@#$%");
        Assert.False(BinaryDetector.IsBinary(text));
    }

    [Fact]
    public void IsBinary_Utf8WithHighBytes_ReturnsFalse()
    {
        // UTF-8 multibyte chars have bytes >= 0x80 which should not trigger binary
        var text = Encoding.UTF8.GetBytes("Héllo wörld. This has ñ and ü characters scattered throughout the file content for testing.");
        Assert.False(BinaryDetector.IsBinary(text));
    }

    [Fact]
    public void IsBinary_ContainsNulByte_ReturnsTrue()
    {
        var data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_TabsAndNewlines_ReturnsFalse()
    {
        // Tab (0x09), LF (0x0A), CR (0x0D) are not suspicious
        var text = "Line1\tTabbed\r\nLine2\nLine3"u8.ToArray();
        Assert.False(BinaryDetector.IsBinary(text));
    }

    [Fact]
    public void IsBinary_HighControlByteRatio_ReturnsTrue()
    {
        // Create a 1024-byte buffer with >5% suspicious control bytes
        // Need n*100/1024 > 5 (integer division), so n >= 62 → 62*100/1024 = 6 > 5
        var data = new byte[1024];
        Array.Fill(data, (byte)'A');
        for (int i = 0; i < 62; i++)
            data[i * 16 % 1024] = 0x01;
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_LowControlByteRatio_ReturnsFalse()
    {
        // Create a 1024-byte buffer with <5% suspicious control bytes
        var data = new byte[1024];
        Array.Fill(data, (byte)'A');
        // Only 10 suspicious bytes (~1%)
        for (int i = 0; i < 10; i++)
            data[i * 100] = 0x02;
        Assert.False(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_SmallSample_SkipsHeuristic()
    {
        // Under 512 bytes, heuristic is skipped
        var data = new byte[100];
        Array.Fill(data, (byte)'X');
        data[50] = 0x01; // suspicious but below threshold check
        Assert.False(BinaryDetector.IsBinary(data));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Magic Number Detection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsBinary_GzipMagic_ReturnsTrue()
    {
        var data = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_ZipMagic_ReturnsTrue()
    {
        var data = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_PngMagic_ReturnsTrue()
    {
        var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_JpegMagic_ReturnsTrue()
    {
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_PdfMagic_ReturnsTrue()
    {
        var data = Encoding.ASCII.GetBytes("%PDF-1.4 some content here");
        // PDF starts with 0x25 0x50 0x44 0x46
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_PEExeMagic_ReturnsTrue()
    {
        var data = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_ElfMagic_ReturnsTrue()
    {
        var data = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };
        Assert.True(BinaryDetector.IsBinary(data));
    }

    // ═══════════════════════════════════════════════════════════════
    //  IsZipMagic / IsSevenZipMagic
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsZipMagic_ValidPK0304_ReturnsTrue()
    {
        Assert.True(BinaryDetector.IsZipMagic(new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
    }

    [Fact]
    public void IsZipMagic_ValidPK0506_ReturnsTrue()
    {
        Assert.True(BinaryDetector.IsZipMagic(new byte[] { 0x50, 0x4B, 0x05, 0x06 }));
    }

    [Fact]
    public void IsZipMagic_ValidPK0708_ReturnsTrue()
    {
        Assert.True(BinaryDetector.IsZipMagic(new byte[] { 0x50, 0x4B, 0x07, 0x08 }));
    }

    [Fact]
    public void IsZipMagic_TooShort_ReturnsFalse()
    {
        Assert.False(BinaryDetector.IsZipMagic(new byte[] { 0x50, 0x4B, 0x03 }));
    }

    [Fact]
    public void IsZipMagic_WrongBytes_ReturnsFalse()
    {
        Assert.False(BinaryDetector.IsZipMagic(new byte[] { 0x50, 0x4B, 0x01, 0x02 }));
    }

    [Fact]
    public void IsSevenZipMagic_Valid_ReturnsTrue()
    {
        Assert.True(BinaryDetector.IsSevenZipMagic(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x00 }));
    }

    [Fact]
    public void IsSevenZipMagic_TooShort_ReturnsFalse()
    {
        Assert.False(BinaryDetector.IsSevenZipMagic(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27 }));
    }

    [Fact]
    public void IsSevenZipMagic_WrongBytes_ReturnsFalse()
    {
        Assert.False(BinaryDetector.IsSevenZipMagic(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0xFF }));
    }

    // ═══════════════════════════════════════════════════════════════
    //  IsBinary(Stream)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsBinary_Stream_TextContent_ReturnsFalse()
    {
        var content = Encoding.UTF8.GetBytes("Just a normal text file with multiple lines\nLine two\nLine three\n");
        using var ms = new MemoryStream(content);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void IsBinary_Stream_BinaryContent_ReturnsTrue()
    {
        var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG
        using var ms = new MemoryStream(data);
        Assert.True(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void IsBinary_Stream_EmptyStream_ReturnsFalse()
    {
        using var ms = new MemoryStream([]);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void IsBinary_Stream_LargeTextFile_ReturnsFalse()
    {
        // Larger than SampleBytes (8192) to test partial read
        var content = Encoding.UTF8.GetBytes(new string('A', 10000));
        using var ms = new MemoryStream(content);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    // ═══════════════════════════════════════════════════════════════
    //  IsBinary(string filePath)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsBinary_FilePath_TextFile_ReturnsFalse()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "Normal text content here\nWith multiple lines");
            Assert.False(BinaryDetector.IsBinary(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IsBinary_FilePath_BinaryFile_ReturnsTrue()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Assert.True(BinaryDetector.IsBinary(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IsBinary_FilePath_NonExistent_ReturnsTrue()
    {
        // Non-existent file should be treated as binary (cannot read)
        Assert.True(BinaryDetector.IsBinary(@"C:\nonexistent\path\file.xyz"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Boundary: exactly 512 bytes threshold
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsBinary_Exactly512Bytes_HeuristicApplied()
    {
        var data = new byte[512];
        Array.Fill(data, (byte)'X');
        // Need n*100/512 > 5 (integer division): 31*100/512 = 6 > 5
        for (int i = 0; i < 31; i++)
            data[i * 16 % 512] = 0x01;
        Assert.True(BinaryDetector.IsBinary(data));
    }

    [Fact]
    public void IsBinary_511Bytes_HeuristicNotApplied()
    {
        var data = new byte[511];
        Array.Fill(data, (byte)'X');
        // Even with many suspicious bytes, heuristic should NOT apply under 512
        for (int i = 0; i < 50; i++)
            data[i * 10 % 511] = 0x01;
        // No NUL, no magic, heuristic skipped for <512 byte samples
        Assert.False(BinaryDetector.IsBinary(data));
    }
}
