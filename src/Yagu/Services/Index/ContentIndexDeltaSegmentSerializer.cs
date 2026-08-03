using System.Buffers;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// Reads and writes a <see cref="ContentIndexDeltaSegment"/> to a directory (plan §3.4/§11.4). The added
/// documents reuse the base <see cref="ContentIndexGenerationSerializer"/> format verbatim (manifest +
/// content + aliases + fileids), and a single extra checksummed <c>tombstones.bin</c> holds the removed
/// paths — so the segment and base formats never drift and every file is self-checked (a truncation or
/// corruption makes <see cref="TryRead"/> return null, and the caller live-scans).
/// </summary>
public static class ContentIndexDeltaSegmentSerializer
{
    public const string TombstonesFile = "tombstones.bin";

    /// <summary>Writes the segment's files into <paramref name="segmentDir"/>.</summary>
    public static void Write(string segmentDir, ContentIndexDeltaSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentDir);
        ArgumentNullException.ThrowIfNull(segment);

        ContentIndexGenerationSerializer.Write(segmentDir, segment.Added, cancellationToken);
        ChecksummedFile.Write(Path.Combine(segmentDir, TombstonesFile), (s, c) => WriteTombstonesBody(s, segment.RemovedPaths, c), cancellationToken);
    }

    /// <summary>
    /// Writes a <b>persistence-only</b> segment batch (plan §5.5) into <paramref name="segmentDir"/> —
    /// byte-identical to the <see cref="ContentIndexDeltaSegment"/> overload, but the added documents come
    /// from a <see cref="ContentIndexBuildBatch"/> that never built a posting index.
    /// </summary>
    internal static void Write(string segmentDir, ContentIndexDeltaSegmentBatch segment, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentDir);
        ArgumentNullException.ThrowIfNull(segment);

        ContentIndexGenerationSerializer.Write(segmentDir, segment.Added, cancellationToken);
        ChecksummedFile.Write(Path.Combine(segmentDir, TombstonesFile), (s, c) => WriteTombstonesBody(s, segment.RemovedPaths, c), cancellationToken);
    }

    /// <summary>
    /// Streaming read-after-write validator for a freshly written segment (plan §5.7): validates the added
    /// generation structurally (no postings) plus the tombstone record structure and checksum.
    /// </summary>
    internal static bool TryValidateSerializedSegment(string segmentDir, out ContentIndexGenerationSerializer.SerializedGenerationShape shape, CancellationToken cancellationToken = default)
    {
        if (!ContentIndexGenerationSerializer.TryValidateSerializedGeneration(segmentDir, out shape, cancellationToken))
            return false;
        return ContentIndexGenerationSerializer.TryValidateTombstones(Path.Combine(segmentDir, TombstonesFile), cancellationToken);
    }

    /// <summary>
    /// Reads and validates a segment from <paramref name="segmentDir"/>. Returns null when the added
    /// generation or the tombstones file is missing, truncated, checksum-invalid, or malformed.
    /// <paramref name="retainDocuments"/> is forwarded to the added generation read (false = query-mode).
    /// </summary>
    public static ContentIndexDeltaSegment? TryRead(
        string segmentDir,
        bool retainDocuments = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(segmentDir) || !Directory.Exists(segmentDir))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        ContentIndexGeneration? added = ContentIndexGenerationSerializer.TryRead(
            segmentDir,
            retainDocuments,
            cancellationToken);
        if (added is null)
            return null;

        if (!ChecksummedFile.TryRead(
            Path.Combine(segmentDir, TombstonesFile),
            out byte[] tombstoneBytes,
            cancellationToken))
            return null;

        try
        {
            IReadOnlyCollection<string> removed = DeserializeTombstones(tombstoneBytes);
            return new ContentIndexDeltaSegment(added, removed);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Reads only this segment's tombstones, streaming and checksum-validating the file without
    /// loading its added-document content or posting structures.</summary>
    internal static IReadOnlySet<string>? TryReadTombstones(
        string segmentDir,
        IReadOnlySet<string> candidates,
        CancellationToken cancellationToken = default)
        => TryReadTombstones(segmentDir, candidates, ChecksummedFile.ChecksummedReader.Open, cancellationToken);

    internal static IReadOnlySet<string>? TryReadTombstones(
        string segmentDir,
        IReadOnlySet<string> candidates,
        Func<string, ChecksummedFile.ChecksummedReader?> openReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        try
        {
            using ChecksummedFile.ChecksummedReader? reader = openReader(
                Path.Combine(segmentDir, TombstonesFile));
            if (reader is null
                || !reader.TryReadInt32(out int count)
                || count < 0)
                return null;

            var paths = new HashSet<string>(StringComparer.Ordinal);
            byte[] pathBuffer = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.TryReadInt32(out int pathLength)
                        || pathLength < 0
                        || pathLength > 32 * 1024 * 1024)
                        return null;
                    if (candidates.Count == 0)
                    {
                        if (!reader.Skip(pathLength))
                            return null;
                        continue;
                    }
                    if (pathBuffer.Length < pathLength)
                    {
                        ArrayPool<byte>.Shared.Return(pathBuffer);
                        pathBuffer = ArrayPool<byte>.Shared.Rent(pathLength);
                    }
                    if (!reader.TryReadBytes(pathBuffer.AsSpan(0, pathLength)))
                        return null;
                    string path = Encoding.UTF8.GetString(pathBuffer, 0, pathLength);
                    if (candidates.Contains(path))
                        paths.Add(path);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pathBuffer);
            }
            return reader.TryFinish() ? paths : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteTombstonesBody(Stream stream, IReadOnlySet<string> removedPaths, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(removedPaths.Count);
        foreach (string path in removedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            writer.Write(pathBytes.Length);
            writer.Write(pathBytes);
        }
        writer.Flush();
    }

    private static List<string> DeserializeTombstones(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative tombstone count.");
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int pathLen = reader.ReadInt32();
            if (pathLen < 0)
                throw new InvalidDataException("Negative tombstone path length.");
            paths.Add(Encoding.UTF8.GetString(reader.ReadBytes(pathLen)));
        }
        return paths;
    }
}
