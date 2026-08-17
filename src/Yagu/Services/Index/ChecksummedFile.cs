using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Yagu.Services.Index;

/// <summary>
/// Shared self-checking file I/O for the on-disk index format (plan §3.4): each file is written as
/// <c>&lt;body&gt; || SHA-256(body)</c> so a reader can detect truncation or corruption and fall back to a
/// live scan. Used by both <see cref="ContentIndexGenerationSerializer"/> (base generations) and
/// <see cref="ContentIndexDeltaSegmentSerializer"/> (incremental delta segments) so the two never drift.
/// </summary>
internal static class ChecksummedFile
{
    /// <summary>Length of the trailing SHA-256 digest.</summary>
    public const int DigestBytes = 32;

    /// <summary>
    /// Largest body <see cref="TryRead(string, out byte[], CancellationToken)"/> can return: it materializes
    /// the body in a single array, and no .NET array can address more than <see cref="int.MaxValue"/> bytes.
    /// Writers of files read that way must refuse to exceed this rather than publish a layer that every
    /// reader would later report as corrupt. Files read through <see cref="ChecksummedReader"/> (notably
    /// <c>content.bin</c>) stream instead and are not bound by it.
    /// </summary>
    public const long MaxReadableBodyBytes = int.MaxValue;

    /// <summary>Writes <paramref name="body"/> followed by its SHA-256 digest, flushed to disk.</summary>
    public static void Write(string path, byte[] body)
    {
        byte[] digest = SHA256.HashData(body);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(body, 0, body.Length);
        IndexMutationFaults.Hit(IndexMutationFaults.ChecksummedBodyWritten);
        fs.Write(digest, 0, digest.Length);
        IndexMutationFaults.Hit(IndexMutationFaults.ChecksummedDigestWritten);
        fs.Flush(flushToDisk: true);
        IndexMutationFaults.Hit(IndexMutationFaults.ChecksummedFlushed);
    }

    /// <summary>
    /// Streaming write (plan §5.6): <paramref name="writeBody"/> writes the body directly to the destination
    /// file while every byte is fed into an incremental SHA-256; the 32-byte digest (of the body only) is
    /// appended and the file is flushed durably. This avoids materializing the whole serialized body in a
    /// <c>MemoryStream</c>/<c>ToArray()</c> for a large file. The digest itself is not hashed.
    /// </summary>
    public static void Write(string path, Action<Stream, CancellationToken> writeBody, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeBody);
        cancellationToken.ThrowIfCancellationRequested();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
        using (var hashing = new HashingWriteStream(fs))
        {
            writeBody(hashing, cancellationToken);
            hashing.Flush();
            IndexMutationFaults.Hit(IndexMutationFaults.ChecksummedBodyWritten);
            byte[] digest = hashing.GetHashAndReset();
            fs.Write(digest, 0, digest.Length); // written straight to fs so the digest is not itself hashed
            IndexMutationFaults.Hit(IndexMutationFaults.ChecksummedDigestWritten);
        }
        fs.Flush(flushToDisk: true);
        IndexMutationFaults.Hit(IndexMutationFaults.ChecksummedFlushed);
    }

    /// <summary>
    /// Reads a checksummed file and validates its trailing digest (constant-time compare). Returns false —
    /// with an empty <paramref name="body"/> — when the file is missing, shorter than the digest, or the
    /// digest does not match (i.e. truncated/corrupt).
    /// </summary>
    public static bool TryRead(string path, out byte[] body, CancellationToken cancellationToken = default)
    {
        body = Array.Empty<byte>();
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return false;

        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        return TryRead(fs, out body, cancellationToken);
    }

    internal static bool TryRead(Stream fs, out byte[] body, CancellationToken cancellationToken = default)
    {
        body = Array.Empty<byte>();
        if (fs.Length < DigestBytes || fs.Length - DigestBytes > MaxReadableBodyBytes)
            return false;

        int bodyLen = checked((int)(fs.Length - DigestBytes));
        byte[] candidateBody = GC.AllocateUninitializedArray<byte>(bodyLen);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int offset = 0;
        while (offset < bodyLen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(1024 * 1024, bodyLen - offset);
            int read = fs.Read(candidateBody, offset, count);
            if (read == 0)
                return false;
            hash.AppendData(candidateBody, offset, read);
            offset += read;
        }

        byte[] storedDigest = new byte[DigestBytes];
        offset = 0;
        while (offset < storedDigest.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = fs.Read(storedDigest, offset, storedDigest.Length - offset);
            if (read == 0)
                return false;
            offset += read;
        }

        byte[] computed = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(storedDigest, computed))
            return false;
        body = candidateBody;
        return true;
    }

    /// <summary>
    /// A write-only <see cref="Stream"/> that mirrors every byte written to it into both an underlying
    /// stream and an incremental SHA-256, so a body can be serialized directly to disk while its digest is
    /// computed in one pass (plan §5.6). Only the write path is supported.
    /// </summary>
    internal sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Span<byte> one = [value];
            Write(one);
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Streaming reader for a checksummed file that lets a caller parse the body <b>structurally</b> — one
    /// primitive at a time — while every consumed byte is fed into an incremental SHA-256, so a validator
    /// can verify record boundaries, counts, and the trailing digest without ever materializing the whole
    /// body (plan §5.7). Reads are bounded to the body region (file length minus the digest). Call
    /// <see cref="TryFinish"/> after parsing to assert exact body consumption and a matching digest.
    /// </summary>
    internal sealed class ChecksummedReader : IDisposable
    {
        private readonly FileStream _fs;
        private readonly IncrementalHash _hash;
        private readonly byte[] _skipBuffer;
        private readonly long _bodyLength;
        private long _bodyPos;
        private bool _failed;

        private ChecksummedReader(FileStream fs, long bodyLength)
        {
            _fs = fs;
            _bodyLength = bodyLength;
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _skipBuffer = new byte[64 * 1024];
        }

        /// <summary>Opens <paramref name="path"/> for streaming validation, or null when it is missing or
        /// too short to hold a digest.</summary>
        public static ChecksummedReader? Open(string path)
        {
            if (!File.Exists(path))
                return null;
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            if (fs.Length < DigestBytes)
            {
                fs.Dispose();
                return null;
            }
            return new ChecksummedReader(fs, fs.Length - DigestBytes);
        }

        /// <summary>Body bytes not yet consumed; 0 once a read has failed. Lets a record parser reject a
        /// declared length that cannot possibly fit before it allocates a buffer for it.</summary>
        public long RemainingBodyBytes => _failed ? 0 : _bodyLength - _bodyPos;

        private bool ReadExact(Span<byte> dest)
        {
            if (_failed || _bodyPos + dest.Length > _bodyLength)
            {
                _failed = true;
                return false;
            }
            int total = 0;
            while (total < dest.Length)
            {
                int n = _fs.Read(dest[total..]);
                if (n <= 0)
                {
                    _failed = true;
                    return false;
                }
                total += n;
            }
            _hash.AppendData(dest);
            _bodyPos += dest.Length;
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            Span<byte> b = stackalloc byte[4];
            if (!ReadExact(b)) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt32LittleEndian(b);
            return true;
        }

        public bool TryReadInt64(out long value)
        {
            Span<byte> b = stackalloc byte[8];
            if (!ReadExact(b)) { value = 0; return false; }
            value = BinaryPrimitives.ReadInt64LittleEndian(b);
            return true;
        }

        public bool TryReadUInt64(out ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            if (!ReadExact(b)) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt64LittleEndian(b);
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            Span<byte> b = stackalloc byte[1];
            if (!ReadExact(b)) { value = 0; return false; }
            value = b[0];
            return true;
        }

        public bool TryReadBytes(Span<byte> destination) => ReadExact(destination);

        /// <summary>Consumes <paramref name="count"/> body bytes (still hashed) without retaining them — used
        /// to advance past variable-length records (trigram blocks, path bytes) that need no value check.</summary>
        public bool Skip(long count)
        {
            if (_failed || count < 0 || _bodyPos + count > _bodyLength)
            {
                _failed = true;
                return false;
            }
            long remaining = count;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(_skipBuffer.Length, remaining);
                int n = _fs.Read(_skipBuffer, 0, chunk);
                if (n <= 0)
                {
                    _failed = true;
                    return false;
                }
                _hash.AppendData(_skipBuffer, 0, n);
                _bodyPos += n;
                remaining -= n;
            }
            return true;
        }

        /// <summary>Asserts the whole body was consumed (no trailing garbage) and the trailing digest matches
        /// the incremental hash (constant-time). Any prior failure makes this false.</summary>
        public bool TryFinish()
        {
            if (_failed || _bodyPos != _bodyLength)
                return false;
            Span<byte> stored = stackalloc byte[DigestBytes];
            int total = 0;
            while (total < DigestBytes)
            {
                int n = _fs.Read(stored[total..]);
                if (n <= 0)
                    return false;
                total += n;
            }
            byte[] computed = _hash.GetHashAndReset();
            return CryptographicOperations.FixedTimeEquals(stored, computed);
        }

        public void Dispose()
        {
            _hash.Dispose();
            _fs.Dispose();
        }
    }
}
