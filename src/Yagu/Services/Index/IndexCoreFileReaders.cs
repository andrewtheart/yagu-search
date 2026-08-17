using System.Buffers;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>One persisted alias record: a normalized path bound to an alias id and a content id.</summary>
internal readonly record struct IndexAliasRecord(string Path, long AliasId, long ContentId);

/// <summary>
/// Streaming record readers over the checksummed core index files (<c>aliases.bin</c>,
/// <c>tombstones.bin</c>, <c>content.bin</c>, <c>fileids.bin</c>).
/// <para>
/// Every reader consumes the exact body through <see cref="ChecksummedFile.ChecksummedReader"/> and
/// requires <c>TryFinish</c> to succeed, so a checksum failure, a truncated record, or trailing garbage
/// is detected <b>before</b> any pointer can change. Nothing is materialized beyond one record at a
/// time, so a multi-GB layer costs a bounded buffer rather than its whole content.
/// </para>
/// </summary>
internal static class IndexCoreFileReaders
{
    /// <summary>Upper bound on one persisted path's UTF-8 byte length. Windows caps a fully-qualified
    /// path at 32767 UTF-16 units, so this is generous while still rejecting a corrupt length.</summary>
    internal const int MaxPathBytes = 256 * 1024;

    /// <summary>There are only 2^24 distinct trigrams, so no honest document can declare more.</summary>
    internal const int MaxTrigramsPerDocument = 1 << 24;
}

/// <summary>Streams <c>aliases.bin</c>: <c>int32 count, per [int32 pathLen, utf8 path, int64 aliasId, int64 contentId]</c>.</summary>
internal sealed class IndexAliasFileReader : IDisposable
{
    private readonly ChecksummedFile.ChecksummedReader _reader;
    private int _remaining;

    private IndexAliasFileReader(ChecksummedFile.ChecksummedReader reader, int count)
    {
        _reader = reader;
        Count = count;
        _remaining = count;
    }

    /// <summary>Number of alias records the file declares.</summary>
    public int Count { get; }

    public static IndexAliasFileReader? Open(string path)
    {
        ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return null;
        if (!reader.TryReadInt32(out int count) || count < 0)
        {
            reader.Dispose();
            return null;
        }
        return new IndexAliasFileReader(reader, count);
    }

    public bool TryReadNext(out IndexAliasRecord record)
    {
        record = default;
        if (_remaining <= 0)
            return false;
        if (!IndexRecordIo.TryReadPath(_reader, out string? path))
            return false;
        if (!_reader.TryReadInt64(out long aliasId) || !_reader.TryReadInt64(out long contentId))
            return false;
        _remaining--;
        record = new IndexAliasRecord(path!, aliasId, contentId);
        return true;
    }

    /// <summary>True only when every declared record was read and the trailing digest matches.</summary>
    public bool TryFinish() => _remaining == 0 && _reader.TryFinish();

    public void Dispose() => _reader.Dispose();
}

/// <summary>Streams <c>tombstones.bin</c>: <c>int32 count, per [int32 pathLen, utf8 path]</c>.</summary>
internal sealed class IndexTombstoneFileReader : IDisposable
{
    private readonly ChecksummedFile.ChecksummedReader _reader;
    private int _remaining;

    private IndexTombstoneFileReader(ChecksummedFile.ChecksummedReader reader, int count)
    {
        _reader = reader;
        Count = count;
        _remaining = count;
    }

    public int Count { get; }

    public static IndexTombstoneFileReader? Open(string path)
    {
        ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return null;
        if (!reader.TryReadInt32(out int count) || count < 0)
        {
            reader.Dispose();
            return null;
        }
        return new IndexTombstoneFileReader(reader, count);
    }

    public bool TryReadNext(out string path)
    {
        path = string.Empty;
        if (_remaining <= 0)
            return false;
        if (!IndexRecordIo.TryReadPath(_reader, out string? value))
            return false;
        _remaining--;
        path = value!;
        return true;
    }

    public bool TryFinish() => _remaining == 0 && _reader.TryFinish();

    public void Dispose() => _reader.Dispose();
}

/// <summary>Streams <c>content.bin</c>: <c>int32 docCount, per doc [int32 trigramCount, uint32×N]</c>.
/// Documents arrive in content-id order (id == ordinal).</summary>
internal sealed class IndexContentFileReader : IDisposable
{
    private readonly ChecksummedFile.ChecksummedReader _reader;
    private int _nextContentId;

    private IndexContentFileReader(ChecksummedFile.ChecksummedReader reader, int documentCount)
    {
        _reader = reader;
        DocumentCount = documentCount;
    }

    public int DocumentCount { get; }

    public static IndexContentFileReader? Open(string path)
    {
        ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return null;
        if (!reader.TryReadInt32(out int documentCount) || documentCount < 0)
        {
            reader.Dispose();
            return null;
        }
        return new IndexContentFileReader(reader, documentCount);
    }

    /// <summary>
    /// Reads the next document's sorted trigrams into <paramref name="trigrams"/> (cleared first) and
    /// reports its content id. Returns false at the end of the declared documents or on any malformed
    /// record.
    /// </summary>
    public bool TryReadNext(List<Trigram> trigrams, out int contentId)
    {
        ArgumentNullException.ThrowIfNull(trigrams);
        contentId = -1;
        trigrams.Clear();
        if (_nextContentId >= DocumentCount)
            return false;
        if (!_reader.TryReadInt32(out int trigramCount)
            || trigramCount < 0
            || trigramCount > IndexCoreFileReaders.MaxTrigramsPerDocument
            || (long)trigramCount * 4 > _reader.RemainingBodyBytes)
        {
            return false;
        }

        if (trigrams.Capacity < trigramCount)
            trigrams.Capacity = trigramCount;
        Span<byte> four = stackalloc byte[4];
        for (int i = 0; i < trigramCount; i++)
        {
            if (!_reader.TryReadBytes(four))
                return false;
            trigrams.Add(Trigram.FromPacked(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(four)));
        }

        contentId = _nextContentId++;
        return true;
    }

    public bool TryFinish() => _nextContentId == DocumentCount && _reader.TryFinish();

    public void Dispose() => _reader.Dispose();
}

/// <summary>Streams <c>fileids.bin</c>: <c>int32 count, per [byte present; if 1: uint64 low, uint64 high]</c>,
/// aligned one-to-one with content ids.</summary>
internal sealed class IndexFileIdentityFileReader : IDisposable
{
    private readonly ChecksummedFile.ChecksummedReader _reader;
    private int _nextContentId;

    private IndexFileIdentityFileReader(ChecksummedFile.ChecksummedReader reader, int count)
    {
        _reader = reader;
        Count = count;
    }

    public int Count { get; }

    public static IndexFileIdentityFileReader? Open(string path)
    {
        ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return null;
        if (!reader.TryReadInt32(out int count) || count < 0)
        {
            reader.Dispose();
            return null;
        }
        return new IndexFileIdentityFileReader(reader, count);
    }

    public bool TryReadNext(out UsnFileIdentity? identity, out int contentId)
    {
        identity = null;
        contentId = -1;
        if (_nextContentId >= Count)
            return false;
        if (!_reader.TryReadByte(out byte present))
            return false;
        if (present == 1)
        {
            if (!_reader.TryReadUInt64(out ulong low) || !_reader.TryReadUInt64(out ulong high))
                return false;
            identity = new UsnFileIdentity(low, high);
        }
        else if (present != 0)
        {
            return false;
        }

        contentId = _nextContentId++;
        return true;
    }

    public bool TryFinish() => _nextContentId == Count && _reader.TryFinish();

    public void Dispose() => _reader.Dispose();
}

/// <summary>Shared primitive parsing shared by the core record readers.</summary>
internal static class IndexRecordIo
{
    /// <summary>Reads a length-prefixed UTF-8 path, rejecting a length that cannot fit in the remaining
    /// body so a corrupt header can never drive a huge allocation.</summary>
    internal static bool TryReadPath(ChecksummedFile.ChecksummedReader reader, out string? path)
    {
        path = null;
        if (!reader.TryReadInt32(out int pathLen)
            || pathLen < 0
            || pathLen > IndexCoreFileReaders.MaxPathBytes
            || pathLen > reader.RemainingBodyBytes)
        {
            return false;
        }
        if (pathLen == 0)
        {
            path = string.Empty;
            return true;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(pathLen);
        try
        {
            if (!reader.TryReadBytes(buffer.AsSpan(0, pathLen)))
                return false;
            path = Encoding.UTF8.GetString(buffer, 0, pathLen);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
