using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ChecksummedFileTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-checksummed-file", Guid.NewGuid().ToString("N"));

    public ChecksummedFileTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void TryRead_ForcedEofInBodyOrDigest_FailsWithEmptyBody()
    {
        using var bodyEof = new FixedLengthEofStream(ChecksummedFile.DigestBytes + 1);
        Assert.False(ChecksummedFile.TryRead(bodyEof, out byte[] body));
        Assert.Empty(body);

        using var digestEof = new FixedLengthEofStream(ChecksummedFile.DigestBytes);
        Assert.False(ChecksummedFile.TryRead(digestEof, out body));
        Assert.Empty(body);
    }

    [Fact]
    public void HashingWriteStream_ExposesOnlyWriteOperations()
    {
        using var inner = new MemoryStream();
        using var stream = new ChecksummedFile.HashingWriteStream(inner);

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void ChecksummedReader_FailedReadPoisonsLaterOperations()
    {
        string path = WriteFile("failed-read.bin", [1, 2, 3, 4]);
        using ChecksummedFile.ChecksummedReader reader = Assert.IsType<ChecksummedFile.ChecksummedReader>(
            ChecksummedFile.ChecksummedReader.Open(path));

        Assert.False(reader.TryReadInt64(out long value));
        Assert.Equal(0, value);
        Assert.False(reader.TryReadByte(out _));
        Assert.False(reader.TryFinish());
    }

    [Fact]
    public void ChecksummedReader_NegativeSkipPoisonsLaterSkip()
    {
        string path = WriteFile("negative-skip.bin", [1]);
        using ChecksummedFile.ChecksummedReader reader = Assert.IsType<ChecksummedFile.ChecksummedReader>(
            ChecksummedFile.ChecksummedReader.Open(path));

        Assert.False(reader.Skip(-1));
        Assert.False(reader.Skip(0));
    }

    [Fact]
    public void ChecksummedReader_TruncatedBodyFailsReadExact()
    {
        string path = WriteFile("read-eof.bin", [1, 2, 3, 4]);
        using ChecksummedFile.ChecksummedReader reader = Assert.IsType<ChecksummedFile.ChecksummedReader>(
            ChecksummedFile.ChecksummedReader.Open(path));
        Truncate(path);

        Assert.False(reader.TryReadInt32(out _));
    }

    [Fact]
    public void ChecksummedReader_TruncatedBodyFailsSkip()
    {
        string path = WriteFile("skip-eof.bin", [1]);
        using ChecksummedFile.ChecksummedReader reader = Assert.IsType<ChecksummedFile.ChecksummedReader>(
            ChecksummedFile.ChecksummedReader.Open(path));
        Truncate(path);

        Assert.False(reader.Skip(1));
    }

    [Fact]
    public void ChecksummedReader_TruncatedDigestFailsFinish()
    {
        string path = WriteFile("digest-eof.bin", Array.Empty<byte>());
        using ChecksummedFile.ChecksummedReader reader = Assert.IsType<ChecksummedFile.ChecksummedReader>(
            ChecksummedFile.ChecksummedReader.Open(path));
        Truncate(path);

        Assert.False(reader.TryFinish());
    }

    private string WriteFile(string name, byte[] body)
    {
        string path = Path.Combine(_sandbox, name);
        ChecksummedFile.Write(path, body);
        return path;
    }

    private static void Truncate(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.SetLength(0);
    }

    private sealed class FixedLengthEofStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}