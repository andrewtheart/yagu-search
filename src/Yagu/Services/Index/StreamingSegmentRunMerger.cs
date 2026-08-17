using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>The layer a streaming merge prepared but has not published yet.</summary>
internal readonly record struct PreparedIndexLayer(
    string Directory,
    IndexManifest Manifest,
    long TombstoneCount);

/// <summary>
/// Merges a contiguous run of incremental delta segments into one equivalent segment — or every active
/// layer into one fresh base — using bounded memory.
/// <para>
/// The semantics are exactly the layered query's: inputs are considered newest-first, and the first layer
/// that decides a path — by alias or by tombstone — wins. Instead of deserializing every input layer, the
/// merge streams each layer's records once, orders path decisions with
/// <see cref="IndexExternalMergeSorter{TRecord}"/>, and writes the merged layer straight to a private
/// workspace. Retained aliases are ordered by <c>(layer, old content id, path)</c> so files that shared
/// content in a source layer — hard links — keep sharing one merged content id.
/// </para>
/// <para>
/// Nothing here touches the live index: the caller publishes the prepared directory through the store's
/// existing validate → promote → pointer-flip protocol, so any failure or cancellation leaves the current
/// pointer, checkpoint, and layers untouched.
/// </para>
/// </summary>
internal static class StreamingSegmentRunMerger
{
    private const byte KindTombstone = 0;
    private const byte KindAlias = 1;
    private const long ProgressRecordInterval = 65_536;

    /// <summary>
    /// Merges <paramref name="segmentDirectories"/> (pointer order, oldest first) into a prepared segment
    /// inside <paramref name="workspace"/>. Throws <see cref="InvalidDataException"/> when the inputs
    /// disagree on scope/root/volume/journal or when any record file fails its checksum or structure.
    /// </summary>
    public static PreparedIndexLayer Merge(
        IReadOnlyList<string> segmentDirectories,
        IndexCompactionWorkspace workspace,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        bool produceV3QueryStructures,
        CancellationToken cancellationToken)
        => MergeCore(
            segmentDirectories, workspace, memoryBudgetBytes, diskGuard, produceV3QueryStructures,
            asBase: false, compactionBuiltUtc: null, progress: null, cancellationToken);

    /// <summary>
    /// Folds <paramref name="layerDirectories"/> — the base followed by every active segment in pointer
    /// order — into a prepared <b>base</b> generation. Tombstoned paths simply do not appear, so the result
    /// carries no tombstones; it keeps the original creation time, the newest active checkpoint, and the
    /// latest incremental timestamp, and is stamped with <paramref name="compactionBuiltUtc"/>.
    /// </summary>
    public static PreparedIndexLayer MergeIntoBase(
        IReadOnlyList<string> layerDirectories,
        IndexCompactionWorkspace workspace,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        bool produceV3QueryStructures,
        DateTimeOffset compactionBuiltUtc,
        CancellationToken cancellationToken)
        => MergeIntoBase(
            layerDirectories, workspace, memoryBudgetBytes, diskGuard, produceV3QueryStructures,
            compactionBuiltUtc, progress: null, cancellationToken);

    public static PreparedIndexLayer MergeIntoBase(
        IReadOnlyList<string> layerDirectories,
        IndexCompactionWorkspace workspace,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        bool produceV3QueryStructures,
        DateTimeOffset compactionBuiltUtc,
        Action<int>? progress,
        CancellationToken cancellationToken)
        => MergeCore(
            layerDirectories, workspace, memoryBudgetBytes, diskGuard, produceV3QueryStructures,
            asBase: true, compactionBuiltUtc, progress, cancellationToken);

    private static PreparedIndexLayer MergeCore(
        IReadOnlyList<string> segmentDirectories,
        IndexCompactionWorkspace workspace,
        long memoryBudgetBytes,
        IndexCompactionDiskGuard? diskGuard,
        bool produceV3QueryStructures,
        bool asBase,
        DateTimeOffset? compactionBuiltUtc,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segmentDirectories);
        ArgumentNullException.ThrowIfNull(workspace);
        if (segmentDirectories.Count < 2)
            throw new ArgumentException("At least two segments are required to merge a run.", nameof(segmentDirectories));

        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        IndexManifest[] manifests = ReadAndValidateManifests(segmentDirectories);
        int layerCount = segmentDirectories.Count;
        long expectedDecisions = manifests.Sum(static manifest => Math.Max(0, manifest.AliasCount));
        long decisionsRead = 0;

        string assignmentSpool = Path.Combine(workspace.SpoolDirectory, "assignments.spool");
        string tombstoneSpool = Path.Combine(workspace.SpoolDirectory, "tombstones.spool");
        long documentCount;
        long aliasCount;
        long tombstoneCount;

        using (var decisions = new IndexExternalMergeSorter<PathDecision>(
                   PathDecisionCodec.Instance, Path.Combine(workspace.SpoolDirectory, "decisions"), memoryBudgetBytes, diskGuard))
        {
            for (int layer = 0; layer < layerCount; layer++)
            {
                int rank = layerCount - 1 - layer; // 0 = newest
                ReadLayerDecisions(
                    segmentDirectories[layer], rank, decisions,
                    () =>
                    {
                        decisionsRead++;
                        if (decisionsRead % ProgressRecordInterval == 0)
                                progressReporter.ReportFraction(decisionsRead, expectedDecisions, 0, 8);
                            },
                            cancellationToken);
            }
            progressReporter.Report(8);

            using var retained = new IndexExternalMergeSorter<AliasAssignment>(
                AliasAssignmentCodec.Instance, Path.Combine(workspace.SpoolDirectory, "retained"), memoryBudgetBytes, diskGuard);
            using (var tombstoneWriter = new IndexSpoolWriter<PathOnlyRecord>(tombstoneSpool, PathOnlyRecordCodec.Instance, diskGuard))
            {
                string? previousPath = null;
                long orderedDecisions = 0;
                foreach (PathDecision decision in decisions.SortedRecords(cancellationToken))
                {
                    orderedDecisions++;
                    if (orderedDecisions % ProgressRecordInterval == 0)
                        progressReporter.ReportFraction(orderedDecisions, decisions.RecordCount, 8, 17);
                    if (previousPath is not null && string.Equals(previousPath, decision.Path, StringComparison.Ordinal))
                        continue; // an older layer's decision for a path the newest layer already decided
                    previousPath = decision.Path;
                    if (decision.Kind == KindAlias)
                        retained.Add(new AliasAssignment(decision.Rank, decision.ContentId, decision.Path), cancellationToken);
                    else if (!asBase)
                        tombstoneWriter.Write(new PathOnlyRecord(decision.Path));
                }
                tombstoneCount = tombstoneWriter.Count;
            }
            progressReporter.Report(17);

            using var assignmentWriter = new IndexSpoolWriter<MergedAlias>(assignmentSpool, MergedAliasCodec.Instance, diskGuard);
            long nextContentId = 0;
            long nextAliasId = 0;
            int currentRank = -1;
            long currentOldContentId = -1;
            long currentNewContentId = -1;
            long retainedAssignments = 0;
            foreach (AliasAssignment assignment in retained.SortedRecords(cancellationToken))
            {
                retainedAssignments++;
                if (retainedAssignments % ProgressRecordInterval == 0)
                    progressReporter.ReportFraction(retainedAssignments, retained.RecordCount, 17, 24);
                bool sameContent = assignment.Rank == currentRank && assignment.OldContentId == currentOldContentId;
                if (!sameContent)
                {
                    currentRank = assignment.Rank;
                    currentOldContentId = assignment.OldContentId;
                    currentNewContentId = nextContentId++;
                }
                assignmentWriter.Write(new MergedAlias(
                    assignment.Rank, assignment.OldContentId, assignment.Path, currentNewContentId, nextAliasId++));
            }
            documentCount = nextContentId;
            aliasCount = assignmentWriter.Count;
        }
        progressReporter.Report(24);

        IndexManifest merged = BuildMergedManifest(
            manifests, checked((int)documentCount), checked((int)aliasCount), asBase, compactionBuiltUtc);
        WritePreparedSegment(
            workspace.PreparedDirectory, merged, segmentDirectories, assignmentSpool, tombstoneSpool,
            asBase ? -1 : tombstoneCount, diskGuard, progressReporter.Slice(24, 38),
            cancellationToken);

        if (produceV3QueryStructures)
        {
            ContentIndexV3StreamingWriter.Write(
                workspace.PreparedDirectory,
                Path.Combine(workspace.SpoolDirectory, "v3"),
                memoryBudgetBytes,
                diskGuard,
                progressReporter.Slice(38, 100),
                cancellationToken);
        }
        else
        {
            progressReporter.Report(100);
        }

        // Refuse to hand back a layer whose creation already pushed the volume past the user's limits.
        diskGuard?.EnsureHeadroomFor(0);

        IndexMutationFaults.Hit(IndexMutationFaults.CompactionPrepared);
        YaguLog.For("ContentIndex").LogDebug(
            "Streaming merge prepared {Documents} document(s), {Aliases} alias(es), {Tombstones} tombstone(s) from {Layers} layer(s) (base={AsBase}).",
            documentCount, aliasCount, asBase ? 0 : tombstoneCount, layerCount, asBase);
        return new PreparedIndexLayer(workspace.PreparedDirectory, merged, asBase ? 0 : tombstoneCount);
    }

    private static IndexManifest[] ReadAndValidateManifests(IReadOnlyList<string> segmentDirectories)
    {
        var manifests = new IndexManifest[segmentDirectories.Count];
        for (int i = 0; i < segmentDirectories.Count; i++)
        {
            manifests[i] = ContentIndexGenerationSerializer.TryReadManifest(segmentDirectories[i])
                ?? throw new InvalidDataException($"Segment '{segmentDirectories[i]}' has no trusted manifest.");
        }

        IndexManifest first = manifests[0];
        UsnCheckpoint prior = first.FreshnessCheckpoint;
        for (int i = 1; i < manifests.Length; i++)
        {
            IndexManifest manifest = manifests[i];
            if (!string.Equals(manifest.ScopeId, first.ScopeId, StringComparison.Ordinal)
                || !string.Equals(manifest.NormalizedRootPath, first.NormalizedRootPath, StringComparison.OrdinalIgnoreCase)
                || manifest.VolumeSerialNumber != first.VolumeSerialNumber
                || !string.Equals(manifest.VolumeGuidPath, first.VolumeGuidPath, StringComparison.OrdinalIgnoreCase)
                || manifest.FreshnessCheckpoint.JournalId != first.FreshnessCheckpoint.JournalId
                || manifest.FreshnessCheckpoint.NextUsn < prior.NextUsn)
            {
                throw new InvalidDataException("Streaming merge inputs disagree on scope, root, volume, journal, or checkpoint order.");
            }
            prior = manifest.FreshnessCheckpoint;
        }
        return manifests;
    }

    private static void ReadLayerDecisions(
        string segmentDirectory,
        int rank,
        IndexExternalMergeSorter<PathDecision> decisions,
        Action recordRead,
        CancellationToken cancellationToken)
    {
        string tombstonePath = Path.Combine(segmentDirectory, ContentIndexDeltaSegmentSerializer.TombstonesFile);
        if (File.Exists(tombstonePath))
        {
            using IndexTombstoneFileReader? tombstones = IndexTombstoneFileReader.Open(tombstonePath)
                ?? throw new InvalidDataException($"Segment '{segmentDirectory}' has an unreadable tombstones.bin.");
            while (tombstones.TryReadNext(out string path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                decisions.Add(new PathDecision(path, rank, KindTombstone, -1), cancellationToken);
                recordRead();
            }
            if (!tombstones.TryFinish())
                throw new InvalidDataException($"Segment '{segmentDirectory}' failed tombstone validation.");
        }

        using IndexAliasFileReader? aliases =
            IndexAliasFileReader.Open(Path.Combine(segmentDirectory, ContentIndexGenerationSerializer.AliasesFile))
            ?? throw new InvalidDataException($"Segment '{segmentDirectory}' has an unreadable aliases.bin.");
        while (aliases.TryReadNext(out IndexAliasRecord record))
        {
            cancellationToken.ThrowIfCancellationRequested();
            decisions.Add(new PathDecision(record.Path, rank, KindAlias, record.ContentId), cancellationToken);
            recordRead();
        }
        if (!aliases.TryFinish())
            throw new InvalidDataException($"Segment '{segmentDirectory}' failed alias validation.");
    }

    private static IndexManifest BuildMergedManifest(
        IndexManifest[] manifests,
        int documentCount,
        int aliasCount,
        bool asBase,
        DateTimeOffset? compactionBuiltUtc)
    {
        IndexManifest newest = manifests[^1];
        // A compacted base keeps the ORIGINAL index's identity and creation time; a merged run of segments
        // adopts the newest input's, because it replaces only that run.
        IndexManifest identity = asBase ? manifests[0] : newest;
        DateTimeOffset? lastIncrementalUpdateUtc = null;
        foreach (IndexManifest manifest in manifests)
        {
            if (manifest.LastIncrementalUpdateUtc is { } timestamp
                && (lastIncrementalUpdateUtc is null || timestamp > lastIncrementalUpdateUtc))
            {
                lastIncrementalUpdateUtc = timestamp;
            }
        }

        return new IndexManifest
        {
            ScopeId = identity.ScopeId,
            VolumeIdentity = identity.VolumeIdentity,
            VolumeSerialNumber = identity.VolumeSerialNumber,
            VolumeGuidPath = identity.VolumeGuidPath,
            FileSystemName = identity.FileSystemName,
            VolumeRelativeRootPath = identity.VolumeRelativeRootPath,
            NormalizedRootPath = identity.NormalizedRootPath,
            FreshnessCheckpoint = newest.FreshnessCheckpoint,
            ContentCount = documentCount,
            AliasCount = aliasCount,
            CreatedUtc = identity.CreatedUtc ?? identity.BuiltUtc,
            LastIncrementalUpdateUtc = lastIncrementalUpdateUtc,
            BuiltUtc = compactionBuiltUtc ?? newest.BuiltUtc,
        };
    }

    private static void WritePreparedSegment(
        string preparedDirectory,
        IndexManifest manifest,
        IReadOnlyList<string> segmentDirectories,
        string assignmentSpool,
        string tombstoneSpool,
        long tombstoneCount,
        IndexCompactionDiskGuard? diskGuard,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var progressReporter = new IndexProgressReporter(progress);
        progressReporter.Report(0);
        Directory.CreateDirectory(preparedDirectory);
        int layerCount = segmentDirectories.Count;

        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.Serialize());
        diskGuard?.EnsureHeadroomFor(manifestBytes.Length);
        ChecksummedFile.Write(
            Path.Combine(preparedDirectory, ContentIndexGenerationSerializer.ManifestFile),
            manifestBytes);
        diskGuard?.RecordCreated(manifestBytes.Length);
        progressReporter.Report(5);

        // aliases.bin — one pass over the assignment spool, which is already in alias-id order.
        ChecksummedFile.Write(
            Path.Combine(preparedDirectory, ContentIndexGenerationSerializer.AliasesFile),
            (stream, ct) =>
            {
                using var writer = new BinaryWriter(new DiskGuardedStream(stream, diskGuard), Encoding.UTF8, leaveOpen: true);
                writer.Write(checked((int)manifest.AliasCount));
                using var reader = new IndexSpoolReader<MergedAlias>(assignmentSpool, MergedAliasCodec.Instance);
                long written = 0;
                while (reader.TryReadNext(out MergedAlias alias))
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] pathBytes = Encoding.UTF8.GetBytes(alias.Path);
                    writer.Write(pathBytes.Length);
                    writer.Write(pathBytes);
                    writer.Write(alias.NewAliasId);
                    writer.Write(alias.NewContentId);
                    written++;
                    if (written % ProgressRecordInterval == 0)
                        progressReporter.ReportFraction(written, manifest.AliasCount, 5, 30);
                }
                writer.Flush();
            },
            cancellationToken);
        progressReporter.Report(30);

        // content.bin — one pass over the assignment spool, reading each source layer's content once.
        ChecksummedFile.Write(
            Path.Combine(preparedDirectory, ContentIndexGenerationSerializer.ContentFile),
            (stream, ct) =>
            {
                using var writer = new BinaryWriter(new DiskGuardedStream(stream, diskGuard), Encoding.UTF8, leaveOpen: true);
                writer.Write(checked((int)manifest.ContentCount));
                using var reader = new IndexSpoolReader<MergedAlias>(assignmentSpool, MergedAliasCodec.Instance);
                LayerContentCursor? cursor = null;
                try
                {
                    int cursorRank = -1;
                    long emitted = 0;
                    while (reader.TryReadNext(out MergedAlias alias))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (alias.NewContentId != emitted)
                            continue; // an additional alias (hard link) onto content already written
                        if (cursorRank != alias.Rank)
                        {
                            if (cursor is not null)
                            {
                                cursor.CompleteAndValidate(ct);
                                cursor.Dispose();
                                cursor = null;
                            }
                            cursor = LayerContentCursor.Open(segmentDirectories[layerCount - 1 - alias.Rank]);
                            cursorRank = alias.Rank;
                        }
                        IReadOnlyList<Trigram> trigrams = cursor!.Read(checked((int)alias.OldContentId));
                        writer.Write(trigrams.Count);
                        foreach (Trigram trigram in trigrams)
                            writer.Write(trigram.Value);
                        emitted++;
                        if (emitted % ProgressRecordInterval == 0)
                            progressReporter.ReportFraction(emitted, manifest.ContentCount, 30, 75);
                    }
                    if (emitted != manifest.ContentCount)
                        throw new InvalidDataException("Streaming merge produced fewer documents than its manifest declares.");
                    cursor?.CompleteAndValidate(ct);
                }
                finally
                {
                    cursor?.Dispose();
                }
                writer.Flush();
            },
            cancellationToken);
        progressReporter.Report(75);

        // fileids.bin — the same walk, reading each source layer's identity table once.
        ChecksummedFile.Write(
            Path.Combine(preparedDirectory, ContentIndexGenerationSerializer.FileIdsFile),
            (stream, ct) =>
            {
                using var writer = new BinaryWriter(new DiskGuardedStream(stream, diskGuard), Encoding.UTF8, leaveOpen: true);
                writer.Write(checked((int)manifest.ContentCount));
                using var reader = new IndexSpoolReader<MergedAlias>(assignmentSpool, MergedAliasCodec.Instance);
                LayerIdentityCursor? cursor = null;
                try
                {
                    int cursorRank = -1;
                    long emitted = 0;
                    while (reader.TryReadNext(out MergedAlias alias))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (alias.NewContentId != emitted)
                            continue;
                        if (cursorRank != alias.Rank)
                        {
                            if (cursor is not null)
                            {
                                cursor.CompleteAndValidate(ct);
                                cursor.Dispose();
                                cursor = null;
                            }
                            cursor = LayerIdentityCursor.Open(segmentDirectories[layerCount - 1 - alias.Rank]);
                            cursorRank = alias.Rank;
                        }
                        UsnFileIdentity? identity = cursor!.Read(checked((int)alias.OldContentId));
                        if (identity is { } value)
                        {
                            writer.Write((byte)1);
                            writer.Write(value.Low);
                            writer.Write(value.High);
                        }
                        else
                        {
                            writer.Write((byte)0);
                        }
                        emitted++;
                        if (emitted % ProgressRecordInterval == 0)
                            progressReporter.ReportFraction(emitted, manifest.ContentCount, 75, 95);
                    }
                    cursor?.CompleteAndValidate(ct);
                }
                finally
                {
                    cursor?.Dispose();
                }
                writer.Flush();
            },
            cancellationToken);
        progressReporter.Report(95);

        // tombstones.bin — the retained removals, already ordered by path. A compacted base has none.
        if (tombstoneCount < 0)
        {
            EnsureReadableWholeFiles(preparedDirectory);
            progressReporter.Report(100);
            return;
        }
        ChecksummedFile.Write(
            Path.Combine(preparedDirectory, ContentIndexDeltaSegmentSerializer.TombstonesFile),
            (stream, ct) =>
            {
                using var writer = new BinaryWriter(new DiskGuardedStream(stream, diskGuard), Encoding.UTF8, leaveOpen: true);
                writer.Write(checked((int)tombstoneCount));
                using var reader = new IndexSpoolReader<PathOnlyRecord>(tombstoneSpool, PathOnlyRecordCodec.Instance);
                while (reader.TryReadNext(out PathOnlyRecord record))
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] pathBytes = Encoding.UTF8.GetBytes(record.Path);
                    writer.Write(pathBytes.Length);
                    writer.Write(pathBytes);
                }
                writer.Flush();
            },
            cancellationToken);

        EnsureReadableWholeFiles(preparedDirectory);
        progressReporter.Report(100);
    }

    /// <summary>
    /// Refuses to hand back a prepared layer whose whole-file records exceed what a reader can load
    /// (<see cref="ChecksummedFile.MaxReadableBodyBytes"/>). Publishing one would produce an index that
    /// every reader silently rejects as corrupt while the pointer claims it is current — so the merge fails
    /// here instead, leaving the existing index untouched. <c>content.bin</c> is exempt: it is streamed.
    /// </summary>
    private static void EnsureReadableWholeFiles(string preparedDirectory)
    {
        foreach (string fileName in new[]
        {
            ContentIndexGenerationSerializer.ManifestFile,
            ContentIndexGenerationSerializer.AliasesFile,
            ContentIndexGenerationSerializer.FileIdsFile,
            ContentIndexDeltaSegmentSerializer.TombstonesFile,
        })
        {
            var info = new FileInfo(Path.Combine(preparedDirectory, fileName));
            if (!info.Exists)
                continue;
            long bodyBytes = info.Length - ChecksummedFile.DigestBytes;
            if (bodyBytes > ChecksummedFile.MaxReadableBodyBytes)
            {
                throw new InvalidDataException(
                    $"Merging these layers would produce a '{fileName}' of {bodyBytes:N0} bytes, more than the "
                    + $"{ChecksummedFile.MaxReadableBodyBytes:N0} bytes a single layer can hold. The existing index is unchanged; "
                    + "index a narrower folder, or split this scope into smaller indexed roots.");
            }
        }
    }

    /// <summary>Walks one source layer's <c>content.bin</c> forward to the requested content ids, which the
    /// merge always asks for in ascending order within a layer.</summary>
    private sealed class LayerContentCursor : IDisposable
    {
        private readonly IndexContentFileReader _reader;
        private readonly string _segmentDirectory;
        private readonly List<Trigram> _buffer = [];

        private LayerContentCursor(IndexContentFileReader reader, string segmentDirectory)
        {
            _reader = reader;
            _segmentDirectory = segmentDirectory;
        }

        public static LayerContentCursor Open(string segmentDirectory)
            => new(
                IndexContentFileReader.Open(Path.Combine(segmentDirectory, ContentIndexGenerationSerializer.ContentFile))
                    ?? throw new InvalidDataException($"Segment '{segmentDirectory}' has an unreadable content.bin."),
                segmentDirectory);

        public IReadOnlyList<Trigram> Read(int contentId)
        {
            while (_reader.TryReadNext(_buffer, out int id))
            {
                if (id == contentId)
                    return _buffer;
            }
            throw new InvalidDataException("A merged alias referenced a content id its source layer does not contain.");
        }

        /// <summary>
        /// Consumes the records after the last one the merge copied and verifies the layer's trailing
        /// digest. Reading forward only proves record framing, so without this a silently corrupt source
        /// layer would be copied into a freshly checksummed generation that every later reader trusts.
        /// </summary>
        public void CompleteAndValidate(CancellationToken cancellationToken)
        {
            while (_reader.TryReadNext(_buffer, out int id))
            {
                if ((id & 0xFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            if (!_reader.TryFinish())
            {
                throw new InvalidDataException(
                    $"Segment '{_segmentDirectory}' failed content.bin validation while merging; the existing index is unchanged.");
            }
        }

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>The <c>fileids.bin</c> counterpart of <see cref="LayerContentCursor"/>.</summary>
    private sealed class LayerIdentityCursor : IDisposable
    {
        private readonly IndexFileIdentityFileReader _reader;
        private readonly string _segmentDirectory;

        private LayerIdentityCursor(IndexFileIdentityFileReader reader, string segmentDirectory)
        {
            _reader = reader;
            _segmentDirectory = segmentDirectory;
        }

        public static LayerIdentityCursor Open(string segmentDirectory)
            => new(
                IndexFileIdentityFileReader.Open(Path.Combine(segmentDirectory, ContentIndexGenerationSerializer.FileIdsFile))
                    ?? throw new InvalidDataException($"Segment '{segmentDirectory}' has an unreadable fileids.bin."),
                segmentDirectory);

        public UsnFileIdentity? Read(int contentId)
        {
            while (_reader.TryReadNext(out UsnFileIdentity? identity, out int id))
            {
                if (id == contentId)
                    return identity;
            }
            throw new InvalidDataException("A merged alias referenced a content id its source identity table does not contain.");
        }

        /// <summary>The <c>fileids.bin</c> counterpart of <see cref="LayerContentCursor.CompleteAndValidate"/>.</summary>
        public void CompleteAndValidate(CancellationToken cancellationToken)
        {
            while (_reader.TryReadNext(out _, out int id))
            {
                if ((id & 0xFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            if (!_reader.TryFinish())
            {
                throw new InvalidDataException(
                    $"Segment '{_segmentDirectory}' failed fileids.bin validation while merging; the existing index is unchanged.");
            }
        }

        public void Dispose() => _reader.Dispose();
    }

    // ── spool record types ──

    internal readonly record struct PathDecision(string Path, int Rank, byte Kind, long ContentId);

    internal sealed class PathDecisionCodec : IIndexSpoolCodec<PathDecision>
    {
        public static readonly PathDecisionCodec Instance = new();

        public int MaxPayloadBytes => 4 + IndexCoreFileReaders.MaxPathBytes + 4 + 1 + 8;

        public int Compare(PathDecision x, PathDecision y)
        {
            int comparison = string.CompareOrdinal(x.Path, y.Path);
            if (comparison != 0)
                return comparison;
            comparison = x.Rank.CompareTo(y.Rank);
            return comparison != 0 ? comparison : x.Kind.CompareTo(y.Kind);
        }

        public int Encode(PathDecision record, Span<byte> destination)
        {
            int bytes = Encoding.UTF8.GetBytes(record.Path, destination[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(destination, bytes);
            int offset = 4 + bytes;
            BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], record.Rank);
            destination[offset + 4] = record.Kind;
            BinaryPrimitives.WriteInt64LittleEndian(destination[(offset + 5)..], record.ContentId);
            return offset + 13;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out PathDecision record)
        {
            record = default;
            if (payload.Length < 17)
                return false;
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (bytes < 0 || payload.Length != 4 + bytes + 13)
                return false;
            record = new PathDecision(
                Encoding.UTF8.GetString(payload.Slice(4, bytes)),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(4 + bytes)..]),
                payload[8 + bytes],
                BinaryPrimitives.ReadInt64LittleEndian(payload[(9 + bytes)..]));
            return true;
        }

        public long EstimateInMemoryBytes(PathDecision record) => 64 + (record.Path.Length * 2);
    }

    internal readonly record struct AliasAssignment(int Rank, long OldContentId, string Path);

    internal sealed class AliasAssignmentCodec : IIndexSpoolCodec<AliasAssignment>
    {
        public static readonly AliasAssignmentCodec Instance = new();

        public int MaxPayloadBytes => 4 + 8 + 4 + IndexCoreFileReaders.MaxPathBytes;

        public int Compare(AliasAssignment x, AliasAssignment y)
        {
            int comparison = x.Rank.CompareTo(y.Rank);
            if (comparison != 0)
                return comparison;
            comparison = x.OldContentId.CompareTo(y.OldContentId);
            return comparison != 0 ? comparison : string.CompareOrdinal(x.Path, y.Path);
        }

        public int Encode(AliasAssignment record, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, record.Rank);
            BinaryPrimitives.WriteInt64LittleEndian(destination[4..], record.OldContentId);
            int bytes = Encoding.UTF8.GetBytes(record.Path, destination[16..]);
            BinaryPrimitives.WriteInt32LittleEndian(destination[12..], bytes);
            return 16 + bytes;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out AliasAssignment record)
        {
            record = default;
            if (payload.Length < 16)
                return false;
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]);
            if (bytes < 0 || payload.Length != 16 + bytes)
                return false;
            record = new AliasAssignment(
                BinaryPrimitives.ReadInt32LittleEndian(payload),
                BinaryPrimitives.ReadInt64LittleEndian(payload[4..]),
                Encoding.UTF8.GetString(payload.Slice(16, bytes)));
            return true;
        }

        public long EstimateInMemoryBytes(AliasAssignment record) => 56 + (record.Path.Length * 2);
    }

    internal readonly record struct MergedAlias(int Rank, long OldContentId, string Path, long NewContentId, long NewAliasId);

    internal sealed class MergedAliasCodec : IIndexRecordCodec<MergedAlias>
    {
        public static readonly MergedAliasCodec Instance = new();

        public int MaxPayloadBytes => 4 + 8 + 8 + 8 + 4 + IndexCoreFileReaders.MaxPathBytes;

        public int Encode(MergedAlias record, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, record.Rank);
            BinaryPrimitives.WriteInt64LittleEndian(destination[4..], record.OldContentId);
            BinaryPrimitives.WriteInt64LittleEndian(destination[12..], record.NewContentId);
            BinaryPrimitives.WriteInt64LittleEndian(destination[20..], record.NewAliasId);
            int bytes = Encoding.UTF8.GetBytes(record.Path, destination[32..]);
            BinaryPrimitives.WriteInt32LittleEndian(destination[28..], bytes);
            return 32 + bytes;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out MergedAlias record)
        {
            record = default;
            if (payload.Length < 32)
                return false;
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
            if (bytes < 0 || payload.Length != 32 + bytes)
                return false;
            record = new MergedAlias(
                BinaryPrimitives.ReadInt32LittleEndian(payload),
                BinaryPrimitives.ReadInt64LittleEndian(payload[4..]),
                Encoding.UTF8.GetString(payload.Slice(32, bytes)),
                BinaryPrimitives.ReadInt64LittleEndian(payload[12..]),
                BinaryPrimitives.ReadInt64LittleEndian(payload[20..]));
            return true;
        }
    }

    internal readonly record struct PathOnlyRecord(string Path);

    internal sealed class PathOnlyRecordCodec : IIndexRecordCodec<PathOnlyRecord>
    {
        public static readonly PathOnlyRecordCodec Instance = new();

        public int MaxPayloadBytes => 4 + IndexCoreFileReaders.MaxPathBytes;

        public int Encode(PathOnlyRecord record, Span<byte> destination)
        {
            int bytes = Encoding.UTF8.GetBytes(record.Path, destination[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(destination, bytes);
            return 4 + bytes;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out PathOnlyRecord record)
        {
            record = default;
            if (payload.Length < 4)
                return false;
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (bytes < 0 || payload.Length != 4 + bytes)
                return false;
            record = new PathOnlyRecord(Encoding.UTF8.GetString(payload.Slice(4, bytes)));
            return true;
        }
    }
}
