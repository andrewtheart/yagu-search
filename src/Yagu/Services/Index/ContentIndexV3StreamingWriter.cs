using System.Buffers.Binary;

namespace Yagu.Services.Index;

/// <summary>
/// Regenerates the format-v3 query sidecars for a layer that was written by a streaming merge, reading the
/// layer's own core files back one record at a time.
/// <para>
/// Every section is produced with bounded memory: postings, path entries, tombstones, and the reverse
/// identity table are ordered by <see cref="IndexExternalMergeSorter{TRecord}"/>, each section body is
/// staged in a scratch file, and the block-framed file is assembled header-first. No section ever holds a
/// whole body, all postings, or an alias dictionary in memory. The bytes are the same layout the in-memory
/// writer produces, so the managed reader and the Rust engine read them unchanged.
/// </para>
/// </summary>
internal static class ContentIndexV3StreamingWriter
{
    private const long ProgressRecordInterval = 65_536;
    private const int PostingsHeaderBytes = 16;
    private const int PostingsDirectoryEntryBytes = 16;
    private const int PathIndexHeaderBytes = 16;
    private const int PathIndexEntryBytes = 32;
    private const int IdentitiesHeaderBytes = 16;
    private const int IdentitiesForwardBytes = 24;
    private const int IdentitiesReverseBytes = 20;
    private const int TombstonesHeaderBytes = 16;
    private const int TombstonesEntryBytes = 16;

    /// <summary>
    /// Writes <c>query-postings.v3</c>, <c>query-pathindex.v3</c>, <c>query-identities.v3</c>, and
    /// <c>query-tombstones.v3</c> into <paramref name="layerDirectory"/> from the core files already
    /// written there. Throws <see cref="InvalidDataException"/> if any core file fails its checksum or
    /// record structure, so a sidecar can never describe data that was not verified.
    /// </summary>
    public static void Write(
        string layerDirectory,
        string scratchDirectory,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        CancellationToken cancellationToken)
        => Write(
            layerDirectory, scratchDirectory, memoryBudgetBytes, diskGuard,
            progress: null, cancellationToken);

    public static void Write(
        string layerDirectory,
        string scratchDirectory,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        Directory.CreateDirectory(scratchDirectory);

        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        int documentCount = WritePostings(
            layerDirectory, scratchDirectory, memoryBudgetBytes, diskGuard,
            progressReporter.Slice(0, 90), cancellationToken);
        WritePathIndex(
            layerDirectory, scratchDirectory, memoryBudgetBytes, diskGuard,
            progressReporter.Slice(90, 94), cancellationToken);
        WriteIdentities(
            layerDirectory, scratchDirectory, documentCount, memoryBudgetBytes, diskGuard,
            progressReporter.Slice(94, 99), cancellationToken);
        WriteTombstones(
            layerDirectory, scratchDirectory, memoryBudgetBytes, diskGuard,
            progressReporter.Slice(99, 100), cancellationToken);
        progressReporter.Report(100);
    }

    private static int WritePostings(
        string layerDirectory,
        string scratchDirectory,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        int documentCount;
        string postingsScratch = Path.Combine(scratchDirectory, "postings-region.tmp");
        var directory = new List<(uint Trigram, uint Count, long RelativeOffset)>();

        using (var sorter = new IndexExternalMergeSorter<PostingPair>(
                   PostingPairCodec.Instance, Path.Combine(scratchDirectory, "postings"), memoryBudgetBytes, diskGuard))
        {
            using (IndexContentFileReader? content =
                   IndexContentFileReader.Open(Path.Combine(layerDirectory, ContentIndexGenerationSerializer.ContentFile)))
            {
                if (content is null)
                    throw new InvalidDataException("The merged layer's content.bin could not be opened for v3 generation.");
                documentCount = content.DocumentCount;
                var buffer = new List<Trigram>();
                long processedDocuments = 0;
                while (content.TryReadNext(buffer, out int contentId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (Trigram trigram in buffer)
                        sorter.Add(new PostingPair(trigram.Value, contentId), cancellationToken);
                    processedDocuments++;
                    if (processedDocuments % ProgressRecordInterval == 0)
                        progressReporter.ReportFraction(processedDocuments, documentCount, 0, 45);
                }
                if (!content.TryFinish())
                    throw new InvalidDataException("The merged layer's content.bin failed structural validation.");
            }
            progressReporter.Report(45);

            using var region = new FileStream(postingsScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            Span<byte> four = stackalloc byte[4];
            long cursor = 0;
            uint currentTrigram = 0;
            uint currentCount = 0;
            long currentStart = 0;
            bool any = false;
            long mergedPostings = 0;
            foreach (PostingPair pair in sorter.SortedRecords(cancellationToken))
            {
                if (!any || pair.Trigram != currentTrigram)
                {
                    if (any)
                        directory.Add((currentTrigram, currentCount, currentStart));
                    currentTrigram = pair.Trigram;
                    currentCount = 0;
                    currentStart = cursor;
                    any = true;
                }
                diskGuard?.EnsureHeadroomFor(4);
                BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)pair.ContentId);
                region.Write(four);
                diskGuard?.RecordCreated(4);
                cursor += 4;
                currentCount++;
                mergedPostings++;
                if (mergedPostings % ProgressRecordInterval == 0)
                    progressReporter.ReportFraction(mergedPostings, sorter.RecordCount, 45, 80);
            }
            if (any)
                directory.Add((currentTrigram, currentCount, currentStart));
            region.Flush();
        }
        progressReporter.Report(80);

        long regionStart = PostingsHeaderBytes + ((long)directory.Count * PostingsDirectoryEntryBytes);
        int capturedDocumentCount = documentCount;
        ContentIndexV3BlockFile.WriteStreamed(
            Path.Combine(layerDirectory, ContentIndexV3Format.PostingsFile),
            ContentIndexV3Format.SectionPostings,
            ContentIndexV3Format.FormatVersion,
            Path.Combine(scratchDirectory, "postings-body.tmp"),
            (stream, ct) =>
            {
                Span<byte> header = stackalloc byte[PostingsHeaderBytes];
                header.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)directory.Count);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)capturedDocumentCount);
                stream.Write(header);

                Span<byte> entry = stackalloc byte[PostingsDirectoryEntryBytes];
                foreach ((uint trigram, uint count, long relativeOffset) in directory)
                {
                    ct.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteUInt32LittleEndian(entry, trigram);
                    BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], count);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)(regionStart + relativeOffset));
                    stream.Write(entry);
                }
                CopyScratch(postingsScratch, stream, progressReporter.Slice(80, 100), ct);
            },
            cancellationToken,
            diskGuard);

        DeleteFileSafe(postingsScratch);
        progressReporter.Report(100);
        return documentCount;
    }

    private static void WritePathIndex(
        string layerDirectory,
        string scratchDirectory,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        string entriesScratch = Path.Combine(scratchDirectory, "pathindex-entries.tmp");
        string stringsScratch = Path.Combine(scratchDirectory, "pathindex-strings.tmp");
        long entryCount = 0;

        using (var sorter = new IndexExternalMergeSorter<HashedPathRecord>(
                   HashedPathRecordCodec.Instance, Path.Combine(scratchDirectory, "pathindex"), memoryBudgetBytes, diskGuard))
        {
            using (IndexAliasFileReader? aliases =
                   IndexAliasFileReader.Open(Path.Combine(layerDirectory, ContentIndexGenerationSerializer.AliasesFile)))
            {
                if (aliases is null)
                    throw new InvalidDataException("The merged layer's aliases.bin could not be opened for v3 generation.");
                long aliasesRead = 0;
                while (aliases.TryReadNext(out IndexAliasRecord record))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(record.Path);
                    sorter.Add(
                        new HashedPathRecord(V3Fnv.Hash(pathBytes), pathBytes, record.AliasId, record.ContentId),
                        cancellationToken);
                    aliasesRead++;
                    if (aliasesRead % ProgressRecordInterval == 0)
                        progressReporter.ReportFraction(aliasesRead, aliases.Count, 0, 45);
                }
                if (!aliases.TryFinish())
                    throw new InvalidDataException("The merged layer's aliases.bin failed structural validation.");
            }
            progressReporter.Report(45);

            using var entries = new FileStream(entriesScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            using var strings = new FileStream(stringsScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            Span<byte> entry = stackalloc byte[PathIndexEntryBytes];
            long stringCursor = 0;
            long orderedEntries = 0;
            foreach (HashedPathRecord record in sorter.SortedRecords(cancellationToken))
            {
                diskGuard?.EnsureHeadroomFor(PathIndexEntryBytes + record.Path.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, record.Hash);
                BinaryPrimitives.WriteInt64LittleEndian(entry[8..], record.AliasId);
                BinaryPrimitives.WriteInt64LittleEndian(entry[16..], record.ContentId);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)stringCursor);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], (uint)record.Path.Length);
                entries.Write(entry);
                strings.Write(record.Path, 0, record.Path.Length);
                diskGuard?.RecordCreated(PathIndexEntryBytes + record.Path.Length);
                stringCursor += record.Path.Length;
                entryCount++;
                orderedEntries++;
                if (orderedEntries % ProgressRecordInterval == 0)
                    progressReporter.ReportFraction(orderedEntries, sorter.RecordCount, 45, 80);
            }
            entries.Flush();
            strings.Flush();
        }
        progressReporter.Report(80);

        long stringsStart = PathIndexHeaderBytes + (entryCount * PathIndexEntryBytes);
        ContentIndexV3BlockFile.WriteStreamed(
            Path.Combine(layerDirectory, ContentIndexV3Format.PathIndexFile),
            ContentIndexV3Format.SectionPathIndex,
            ContentIndexV3Format.FormatVersion,
            Path.Combine(scratchDirectory, "pathindex-body.tmp"),
            (stream, ct) =>
            {
                Span<byte> header = stackalloc byte[PathIndexHeaderBytes];
                header.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)entryCount);
                BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)stringsStart);
                stream.Write(header);
                CopyScratch(entriesScratch, stream, progressReporter.Slice(80, 90), ct);
                CopyScratch(stringsScratch, stream, progressReporter.Slice(90, 100), ct);
            },
            cancellationToken,
            diskGuard);

        DeleteFileSafe(entriesScratch);
        DeleteFileSafe(stringsScratch);
        progressReporter.Report(100);
    }

    private static void WriteIdentities(
        string layerDirectory,
        string scratchDirectory,
        int documentCount,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        string forwardScratch = Path.Combine(scratchDirectory, "identities-forward.tmp");
        string reverseScratch = Path.Combine(scratchDirectory, "identities-reverse.tmp");
        long presentCount = 0;

        using (var sorter = new IndexExternalMergeSorter<ReverseIdentityRecord>(
                   ReverseIdentityRecordCodec.Instance, Path.Combine(scratchDirectory, "identities"), memoryBudgetBytes, diskGuard))
        {
            using (var forward = new FileStream(forwardScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024))
            using (IndexFileIdentityFileReader? identities =
                   IndexFileIdentityFileReader.Open(Path.Combine(layerDirectory, ContentIndexGenerationSerializer.FileIdsFile)))
            {
                if (identities is null || identities.Count != documentCount)
                    throw new InvalidDataException("The merged layer's fileids.bin is missing or disagrees with content.bin.");
                Span<byte> record = stackalloc byte[IdentitiesForwardBytes];
                long identitiesRead = 0;
                while (identities.TryReadNext(out UsnFileIdentity? identity, out int contentId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    record.Clear();
                    if (identity is { } value)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(record, value.Low);
                        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], value.High);
                        record[16] = 1;
                        sorter.Add(new ReverseIdentityRecord(value.Low, value.High, contentId), cancellationToken);
                        presentCount++;
                    }
                    diskGuard?.EnsureHeadroomFor(IdentitiesForwardBytes);
                    forward.Write(record);
                    diskGuard?.RecordCreated(IdentitiesForwardBytes);
                    identitiesRead++;
                    if (identitiesRead % ProgressRecordInterval == 0)
                        progressReporter.ReportFraction(identitiesRead, documentCount, 0, 45);
                }
                if (!identities.TryFinish())
                    throw new InvalidDataException("The merged layer's fileids.bin failed structural validation.");
                forward.Flush();
            }
            progressReporter.Report(45);

            using var reverse = new FileStream(reverseScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            Span<byte> entry = stackalloc byte[IdentitiesReverseBytes];
            long reverseEntries = 0;
            foreach (ReverseIdentityRecord record in sorter.SortedRecords(cancellationToken))
            {
                diskGuard?.EnsureHeadroomFor(IdentitiesReverseBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, record.Low);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], record.High);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)record.ContentId);
                reverse.Write(entry);
                diskGuard?.RecordCreated(IdentitiesReverseBytes);
                reverseEntries++;
                if (reverseEntries % ProgressRecordInterval == 0)
                    progressReporter.ReportFraction(reverseEntries, sorter.RecordCount, 45, 80);
            }
            reverse.Flush();
        }
        progressReporter.Report(80);

        long reverseStart = IdentitiesHeaderBytes + ((long)documentCount * IdentitiesForwardBytes);
        ContentIndexV3BlockFile.WriteStreamed(
            Path.Combine(layerDirectory, ContentIndexV3Format.IdentitiesFile),
            ContentIndexV3Format.SectionIdentities,
            ContentIndexV3Format.FormatVersion,
            Path.Combine(scratchDirectory, "identities-body.tmp"),
            (stream, ct) =>
            {
                Span<byte> header = stackalloc byte[IdentitiesHeaderBytes];
                header.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)documentCount);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)presentCount);
                BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)reverseStart);
                stream.Write(header);
                CopyScratch(forwardScratch, stream, progressReporter.Slice(80, 90), ct);
                CopyScratch(reverseScratch, stream, progressReporter.Slice(90, 100), ct);
            },
            cancellationToken,
            diskGuard);

        DeleteFileSafe(forwardScratch);
        DeleteFileSafe(reverseScratch);
        progressReporter.Report(100);
    }

    private static void WriteTombstones(
        string layerDirectory,
        string scratchDirectory,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        string entriesScratch = Path.Combine(scratchDirectory, "tombstones-entries.tmp");
        string stringsScratch = Path.Combine(scratchDirectory, "tombstones-strings.tmp");
        long entryCount = 0;

        using (var sorter = new IndexExternalMergeSorter<HashedPathRecord>(
                   HashedPathRecordCodec.Instance, Path.Combine(scratchDirectory, "tombstones"), memoryBudgetBytes, diskGuard))
        {
            string tombstonePath = Path.Combine(layerDirectory, ContentIndexDeltaSegmentSerializer.TombstonesFile);
            if (File.Exists(tombstonePath))
            {
                using IndexTombstoneFileReader? tombstones = IndexTombstoneFileReader.Open(tombstonePath);
                if (tombstones is null)
                    throw new InvalidDataException("The merged layer's tombstones.bin could not be opened for v3 generation.");
                while (tombstones.TryReadNext(out string path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
                    sorter.Add(new HashedPathRecord(V3Fnv.Hash(pathBytes), pathBytes, 0, 0), cancellationToken);
                }
                if (!tombstones.TryFinish())
                    throw new InvalidDataException("The merged layer's tombstones.bin failed structural validation.");
            }

            using var entries = new FileStream(entriesScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            using var strings = new FileStream(stringsScratch, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            Span<byte> entry = stackalloc byte[TombstonesEntryBytes];
            long stringCursor = 0;
            foreach (HashedPathRecord record in sorter.SortedRecords(cancellationToken))
            {
                diskGuard?.EnsureHeadroomFor(TombstonesEntryBytes + record.Path.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, record.Hash);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)stringCursor);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)record.Path.Length);
                entries.Write(entry);
                strings.Write(record.Path, 0, record.Path.Length);
                diskGuard?.RecordCreated(TombstonesEntryBytes + record.Path.Length);
                stringCursor += record.Path.Length;
                entryCount++;
            }
            entries.Flush();
            strings.Flush();
        }
        progressReporter.Report(80);

        long stringsStart = TombstonesHeaderBytes + (entryCount * TombstonesEntryBytes);
        ContentIndexV3BlockFile.WriteStreamed(
            Path.Combine(layerDirectory, ContentIndexV3Format.TombstonesFile),
            ContentIndexV3Format.SectionTombstones,
            ContentIndexV3Format.FormatVersion,
            Path.Combine(scratchDirectory, "tombstones-body.tmp"),
            (stream, ct) =>
            {
                Span<byte> header = stackalloc byte[TombstonesHeaderBytes];
                header.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)entryCount);
                BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)stringsStart);
                stream.Write(header);
                CopyScratch(entriesScratch, stream, progressReporter.Slice(80, 90), ct);
                CopyScratch(stringsScratch, stream, progressReporter.Slice(90, 100), ct);
            },
            cancellationToken,
            diskGuard);

        DeleteFileSafe(entriesScratch);
        DeleteFileSafe(stringsScratch);
        progressReporter.Report(100);
    }

    private static void CopyScratch(
        string path,
        Stream destination,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            copied += read;
            progressReporter.ReportFraction(copied, source.Length, 0, 100);
        }
        progressReporter.Report(100);
    }

    private static void DeleteFileSafe(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* scratch cleanup is best effort */ }
    }

    // ── spool record types ──

    internal readonly record struct PostingPair(uint Trigram, int ContentId);

    internal sealed class PostingPairCodec : IIndexSpoolCodec<PostingPair>
    {
        public static readonly PostingPairCodec Instance = new();

        public int MaxPayloadBytes => 8;

        public int Compare(PostingPair x, PostingPair y)
        {
            int comparison = x.Trigram.CompareTo(y.Trigram);
            return comparison != 0 ? comparison : x.ContentId.CompareTo(y.ContentId);
        }

        public int Encode(PostingPair record, Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, record.Trigram);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..], record.ContentId);
            return 8;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out PostingPair record)
        {
            record = default;
            if (payload.Length != 8)
                return false;
            record = new PostingPair(
                BinaryPrimitives.ReadUInt32LittleEndian(payload),
                BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
            return true;
        }

        public long EstimateInMemoryBytes(PostingPair record) => 8;
    }

    internal readonly record struct HashedPathRecord(ulong Hash, byte[] Path, long AliasId, long ContentId);

    internal sealed class HashedPathRecordCodec : IIndexSpoolCodec<HashedPathRecord>
    {
        public static readonly HashedPathRecordCodec Instance = new();

        public int MaxPayloadBytes => 8 + 4 + IndexCoreFileReaders.MaxPathBytes + 8 + 8;

        public int Compare(HashedPathRecord x, HashedPathRecord y)
        {
            int comparison = x.Hash.CompareTo(y.Hash);
            return comparison != 0 ? comparison : x.Path.AsSpan().SequenceCompareTo(y.Path);
        }

        public int Encode(HashedPathRecord record, Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, record.Hash);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], record.Path.Length);
            record.Path.CopyTo(destination[12..]);
            int offset = 12 + record.Path.Length;
            BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], record.AliasId);
            BinaryPrimitives.WriteInt64LittleEndian(destination[(offset + 8)..], record.ContentId);
            return offset + 16;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out HashedPathRecord record)
        {
            record = default;
            if (payload.Length < 28)
                return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
            if (length < 0 || payload.Length != 12 + length + 16)
                return false;
            record = new HashedPathRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(payload),
                payload.Slice(12, length).ToArray(),
                BinaryPrimitives.ReadInt64LittleEndian(payload[(12 + length)..]),
                BinaryPrimitives.ReadInt64LittleEndian(payload[(20 + length)..]));
            return true;
        }

        public long EstimateInMemoryBytes(HashedPathRecord record) => 64 + record.Path.Length;
    }

    internal readonly record struct ReverseIdentityRecord(ulong Low, ulong High, int ContentId);

    internal sealed class ReverseIdentityRecordCodec : IIndexSpoolCodec<ReverseIdentityRecord>
    {
        public static readonly ReverseIdentityRecordCodec Instance = new();

        public int MaxPayloadBytes => 20;

        public int Compare(ReverseIdentityRecord x, ReverseIdentityRecord y)
        {
            int comparison = x.Low.CompareTo(y.Low);
            if (comparison != 0)
                return comparison;
            comparison = x.High.CompareTo(y.High);
            return comparison != 0 ? comparison : x.ContentId.CompareTo(y.ContentId);
        }

        public int Encode(ReverseIdentityRecord record, Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, record.Low);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], record.High);
            BinaryPrimitives.WriteInt32LittleEndian(destination[16..], record.ContentId);
            return 20;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out ReverseIdentityRecord record)
        {
            record = default;
            if (payload.Length != 20)
                return false;
            record = new ReverseIdentityRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(payload),
                BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[16..]));
            return true;
        }

        public long EstimateInMemoryBytes(ReverseIdentityRecord record) => 24;
    }
}
