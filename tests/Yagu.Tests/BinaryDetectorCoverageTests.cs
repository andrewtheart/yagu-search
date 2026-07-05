using System.Text;
using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class BinaryDetectorExtendedCoverageTests
{
    [Fact]
    public void IsBinary_EmptySpan_ReturnsFalse()
    {
        Assert.False(BinaryDetector.IsBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsBinary_EmptyStream_ReturnsFalse()
    {
        using var ms = new MemoryStream([]);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void IsBinary_ShortSample_NulByte_ReturnsTrue()
    {
        byte[] data = [(byte)'A', 0x00, (byte)'B'];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_JpegMagic()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_ElfMagic()
    {
        byte[] data = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_PeMzMagic()
    {
        byte[] data = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_ZstandardMagic()
    {
        byte[] data = [0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x00];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_MachO32Magic()
    {
        byte[] data = [0xCE, 0xFA, 0xED, 0xFE, 0x01, 0x00];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_MachO64Magic()
    {
        byte[] data = [0xCF, 0xFA, 0xED, 0xFE, 0x01, 0x00];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_MachOFatMagic()
    {
        byte[] data = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x02];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_Bzip2Magic()
    {
        byte[] data = [0x42, 0x5A, 0x68, 0x39, 0x31, 0x41];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_XzMagic()
    {
        byte[] data = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x01];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_RarMagic()
    {
        byte[] data = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_7zMagic()
    {
        byte[] data = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x01];
        Assert.True(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_LessThan4Bytes_NoMagicCheck_ReturnsFalse()
    {
        byte[] data = [(byte)'A', (byte)'B', (byte)'C'];
        Assert.False(BinaryDetector.IsBinary(data.AsSpan()));
    }

    [Fact]
    public void IsBinary_ControlBytesBelow5Percent_NotBinary()
    {
        // 600 bytes, only 2% control bytes (< 5% threshold)
        var bytes = new byte[600];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)'A'; // all printable
        // Add a few control bytes (12 out of 600 = 2%)
        for (int i = 0; i < 12; i++)
            bytes[i * 50] = 0x01;
        Assert.False(BinaryDetector.IsBinary(bytes.AsSpan()));
    }

    [Fact]
    public void IsBinary_PrintableAsciiBuffer_NotBinary()
    {
        // File of only printable ASCII should not be binary
        var bytes = new byte[600];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)('A' + (i % 26));
        Assert.False(BinaryDetector.IsBinary(bytes.AsSpan()));
    }

    [Fact]
    public void IsBinary_SampleBelow512_SkipsHeuristic()
    {
        // < 512 bytes, with some control bytes - should not trigger heuristic
        var bytes = new byte[400];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (i % 5 == 0) ? (byte)0x01 : (byte)'A';
        // No NUL bytes, no magic, but has control bytes
        // Below 512 threshold so heuristic doesn't apply
        Assert.False(BinaryDetector.IsBinary(bytes.AsSpan()));
    }

    [Fact]
    public void IsZipMagic_PK0304_True()
    {
        byte[] data = [0x50, 0x4B, 0x03, 0x04];
        Assert.True(BinaryDetector.IsZipMagic(data));
    }

    [Fact]
    public void IsZipMagic_PK0506_True()
    {
        byte[] data = [0x50, 0x4B, 0x05, 0x06];
        Assert.True(BinaryDetector.IsZipMagic(data));
    }

    [Fact]
    public void IsZipMagic_PK0708_True()
    {
        byte[] data = [0x50, 0x4B, 0x07, 0x08];
        Assert.True(BinaryDetector.IsZipMagic(data));
    }

    [Fact]
    public void IsZipMagic_TooShort_False()
    {
        byte[] data = [0x50, 0x4B, 0x03];
        Assert.False(BinaryDetector.IsZipMagic(data));
    }

    [Fact]
    public void IsZipMagic_NotPK_False()
    {
        byte[] data = [0x50, 0x4C, 0x03, 0x04];
        Assert.False(BinaryDetector.IsZipMagic(data));
    }

    [Fact]
    public void IsSevenZipMagic_Valid_True()
    {
        byte[] data = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        Assert.True(BinaryDetector.IsSevenZipMagic(data));
    }

    [Fact]
    public void IsSevenZipMagic_TooShort_False()
    {
        byte[] data = [0x37, 0x7A, 0xBC, 0xAF, 0x27];
        Assert.False(BinaryDetector.IsSevenZipMagic(data));
    }

    [Fact]
    public void IsSevenZipMagic_WrongBytes_False()
    {
        byte[] data = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1D];
        Assert.False(BinaryDetector.IsSevenZipMagic(data));
    }

    [Fact]
    public void IsBinary_StreamReadsInChunks()
    {
        // Simulate a stream that returns data in small chunks
        var data = Encoding.UTF8.GetBytes("Hello world this is plain text content");
        using var ms = new ChunkedStream(data, 5);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    [Fact]
    public void IsBinary_LargeTextFile_ReturnsFalse()
    {
        // Larger than SampleBytes - only first 8192 are checked
        var data = Encoding.UTF8.GetBytes(new string('A', 10000));
        using var ms = new MemoryStream(data);
        Assert.False(BinaryDetector.IsBinary(ms));
    }

    private sealed class ChunkedStream : MemoryStream
    {
        private readonly int _chunkSize;
        public ChunkedStream(byte[] data, int chunkSize) : base(data) => _chunkSize = chunkSize;
        public override int Read(Span<byte> buffer)
        {
            var limited = buffer.Length > _chunkSize ? buffer[.._chunkSize] : buffer;
            return base.Read(limited);
        }
    }
}
