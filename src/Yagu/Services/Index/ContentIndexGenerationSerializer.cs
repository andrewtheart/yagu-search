using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Reads and writes a <see cref="ContentIndexGeneration"/> to a directory (plan §3.4 on-disk format —
/// managed reference). Each file is self-checked: a trailing SHA-256 of the preceding bytes lets the
/// reader detect truncation or corruption and fall back to a live scan. The posting index is not
/// stored inverted; the per-content trigram sets are persisted and the index is rebuilt on load, so a
/// loaded generation is byte-identical to a freshly built one.
/// </summary>
public static class ContentIndexGenerationSerializer
{
    public const string ManifestFile = "manifest.json";
    public const string ContentFile = "content.bin";
    public const string AliasesFile = "aliases.bin";
    public const string FileIdsFile = "fileids.bin";

    /// <summary>
    /// A manifest-only diagnostic result. Unlike <see cref="TryReadManifest"/>, this preserves a
    /// checksum-valid manifest whose format or content-representation version is incompatible, so
    /// Settings can identify the original root and offer a rebuild instead of showing an opaque scope id.
    /// </summary>
    internal readonly record struct ManifestDiagnostic(IndexManifest? Manifest, IndexStructuralVerdict Verdict);

    /// <summary>Writes the generation's files (with trailing digests) into <paramref name="generationDir"/>.</summary>
    public static void Write(string generationDir, ContentIndexGeneration generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        WriteFiles(generationDir, generation.Manifest, generation.Documents, generation.Aliases, generation.ContentIdentities, cancellationToken);
    }

    /// <summary>
    /// Writes a <b>persistence-only</b> batch (plan §5.5) into <paramref name="generationDir"/>, producing
    /// byte-identical files to the <see cref="ContentIndexGeneration"/> overload — the batch just skips the
    /// posting index the serializer never persists anyway.
    /// </summary>
    internal static void Write(string generationDir, ContentIndexBuildBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        WriteFiles(generationDir, batch.Manifest, batch.Documents, batch.Aliases, batch.ContentIdentities, cancellationToken);
    }

    private static void WriteFiles(
        string generationDir,
        IndexManifest manifest,
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents,
        IReadOnlyDictionary<string, (long AliasId, long ContentId)> aliases,
        IReadOnlyList<UsnFileIdentity?> identities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationDir);
        Directory.CreateDirectory(generationDir);

        // The manifest is tiny JSON — keep the byte-array path. The large record files are streamed directly
        // to disk (plan §5.6): no MemoryStream/ToArray per file.
        WriteChecksummed(Path.Combine(generationDir, ManifestFile), Encoding.UTF8.GetBytes(manifest.Serialize()));
        ChecksummedFile.Write(Path.Combine(generationDir, ContentFile), (s, c) => WriteContentBody(s, documents, c), cancellationToken);
        ChecksummedFile.Write(Path.Combine(generationDir, AliasesFile), (s, c) => WriteAliasesBody(s, aliases, c), cancellationToken);
        ChecksummedFile.Write(Path.Combine(generationDir, FileIdsFile), (s, c) => WriteFileIdsBody(s, identities, c), cancellationToken);
    }

    /// <summary>
    /// Reads and validates a generation from <paramref name="generationDir"/>. Returns null when any file
    /// is missing, truncated, checksum-invalid, or the manifest is structurally incompatible.
    /// When <paramref name="retainDocuments"/> is false the per-document trigram sets are dropped after the
    /// posting index is built (a query-mode load) to halve the in-memory footprint.
    /// </summary>
    public static ContentIndexGeneration? TryRead(
        string generationDir,
        bool retainDocuments = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(generationDir) || !Directory.Exists(generationDir))
        {
            YaguLog.For("ContentIndex").LogDebug("TryRead: generation directory missing or empty path ('{GenerationDir}').", generationDir);
            return null;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // content.bin is read by STREAMING (never as one byte[]): a compacted whole-drive layer can hold
            // more than 2 GiB of trigram records, which no single .NET array can address. The other three
            // files stay on the whole-file path — they are orders of magnitude smaller (a path table and a
            // 17-bytes-per-document identity table).
            if (!TryReadChecksummed(Path.Combine(generationDir, ManifestFile), out byte[] manifestBytes, cancellationToken)
                || !TryReadChecksummed(Path.Combine(generationDir, AliasesFile), out byte[] aliasBytes, cancellationToken)
                || !TryReadChecksummed(Path.Combine(generationDir, FileIdsFile), out byte[] fileIdBytes, cancellationToken))
            {
                YaguLog.For("ContentIndex").LogWarning("TryRead: a generation file is missing or failed its checksum in '{GenerationDir}' (treated as corrupt).", generationDir);
                return null;
            }

            IndexManifest? manifest = IndexManifest.Deserialize(Encoding.UTF8.GetString(manifestBytes));
            if (manifest is null || manifest.EvaluateStructural() != IndexStructuralVerdict.Trusted)
            {
                YaguLog.For("ContentIndex").LogWarning("TryRead: manifest in '{GenerationDir}' is unparseable or structurally untrusted (verdict {Verdict}).", generationDir, manifest?.EvaluateStructural().ToString() ?? "null");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, (long, long)> aliases = DeserializeAliases(aliasBytes);
            List<UsnFileIdentity?> identities = DeserializeFileIds(fileIdBytes);

            // Query-mode load (retainDocuments == false): stream content.bin straight into the posting index
            // WITHOUT materializing the per-document trigram sets — opening a large layered index otherwise
            // churns GB of transient garbage building and discarding the whole corpus's documents. The retain
            // path (compaction/serialization) still materializes the documents.
            int documentCount;
            TrigramPostingIndex? streamedPostings = null;
            List<IReadOnlyCollection<Trigram>>? documents = null;
            string contentPath = Path.Combine(generationDir, ContentFile);
            if (retainDocuments)
            {
                documents = TryDeserializeContentFile(contentPath, cancellationToken);
                if (documents is null)
                {
                    YaguLog.For("ContentIndex").LogWarning("TryRead: content.bin is missing or failed its checksum in '{GenerationDir}' (treated as corrupt).", generationDir);
                    return null;
                }
                documentCount = documents.Count;
            }
            else
            {
                streamedPostings = TrigramPostingIndex.TryBuildFromContentFile(
                    contentPath,
                    out documentCount,
                    cancellationToken);
                if (streamedPostings is null)
                {
                    YaguLog.For("ContentIndex").LogWarning("TryRead: content.bin is missing or failed its checksum in '{GenerationDir}' (treated as corrupt).", generationDir);
                    return null;
                }
            }

            // The fileids table must be 1:1 with content ids.
            if (identities.Count != documentCount)
            {
                YaguLog.For("ContentIndex").LogWarning("TryRead: fileids/content count mismatch in '{GenerationDir}' ({IdentityCount} vs {DocumentCount}).", generationDir, identities.Count, documentCount);
                return null;
            }

            // Every alias must reference a valid content id.
            foreach (var (_, entry) in aliases)
            {
                if (entry.Item2 < 0 || entry.Item2 >= documentCount)
                {
                    YaguLog.For("ContentIndex").LogWarning("TryRead: alias references out-of-range content id {ContentId} (max {MaxContentId}) in '{GenerationDir}'.", entry.Item2, documentCount - 1, generationDir);
                    return null;
                }
            }

            return retainDocuments
                ? ContentIndexGeneration.FromPersisted(manifest, documents!, aliases, identities, retainDocuments: true)
                : ContentIndexGeneration.FromPersistedPostings(manifest, streamedPostings!, documentCount, aliases, identities);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "TryRead: exception while reading generation '{GenerationDir}' (treated as corrupt).", generationDir);
            return null;
        }
    }

    /// <summary>
    /// Reads and validates <b>only</b> a generation's <c>manifest.json</c> (skips content/aliases/fileids), or
    /// null when it is missing, checksum-invalid, or structurally incompatible. Lets storage-stats reporting
    /// count documents and read build metadata from a paged multi-GB index for a few KB of I/O instead of
    /// loading the whole generation into memory.
    /// </summary>
    public static IndexManifest? TryReadManifest(string generationDir)
    {
        ManifestDiagnostic diagnostic = ReadManifestDiagnostic(generationDir);
        return diagnostic.Verdict == IndexStructuralVerdict.Trusted ? diagnostic.Manifest : null;
    }

    /// <summary>
    /// Reads and checksum-validates only <c>manifest.json</c>, returning the exact structural verdict.
    /// This method never trusts incompatible metadata for searching; it only lets management UI recover
    /// a scope's identity and explain whether rebuilding is required after a format upgrade.
    /// </summary>
    internal static ManifestDiagnostic ReadManifestDiagnostic(string generationDir)
    {
        if (string.IsNullOrEmpty(generationDir) || !Directory.Exists(generationDir))
            return new ManifestDiagnostic(null, IndexStructuralVerdict.Missing);
        try
        {
            string manifestPath = Path.Combine(generationDir, ManifestFile);
            if (!File.Exists(manifestPath))
                return new ManifestDiagnostic(null, IndexStructuralVerdict.Missing);
            if (!TryReadChecksummed(manifestPath, out byte[] manifestBytes))
            {
                YaguLog.For("ContentIndex").LogDebug("TryReadManifest: manifest checksum is invalid in '{GenerationDir}'.", generationDir);
                return new ManifestDiagnostic(null, IndexStructuralVerdict.Corrupt);
            }

            IndexManifest? manifest = IndexManifest.Deserialize(Encoding.UTF8.GetString(manifestBytes));
            return manifest is null
                ? new ManifestDiagnostic(null, IndexStructuralVerdict.Corrupt)
                : new ManifestDiagnostic(manifest, manifest.EvaluateStructural());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "TryReadManifest: exception reading manifest in '{GenerationDir}'.", generationDir);
            return new ManifestDiagnostic(null, IndexStructuralVerdict.Corrupt);
        }
    }

    /// <summary>
    /// Advances ONLY a generation's freshness checkpoint (<see cref="IndexManifest.FreshnessCheckpoint"/>)
    /// in place, rewriting just <c>manifest.json</c> and leaving content/aliases/fileids untouched. Used to
    /// "re-anchor" an unchanging root whose checkpoint is nearing USN-journal wrap: a continuous, no-change
    /// journal replay proves the generation's content is still accurate as of <paramref name="newCheckpoint"/>,
    /// so persisting the newer checkpoint keeps future searches from replaying a purged (gapped) position and
    /// bypassing the index. The write is atomic (temp file → validated → <see cref="File.Move(string,string,bool)"/>),
    /// so a crash leaves either the old or the new manifest — both valid. Returns false (no change) when the
    /// manifest is missing/corrupt or the rewrite fails; never throws.
    /// </summary>
    public static bool TryReanchorManifestCheckpoint(string generationDir, UsnCheckpoint newCheckpoint)
        => TryReanchorManifestCheckpoint(generationDir, newCheckpoint, WriteChecksummed);

    internal static bool TryReanchorManifestCheckpoint(
        string generationDir,
        UsnCheckpoint newCheckpoint,
        Action<string, byte[]> writeChecksummed)
    {
        ArgumentNullException.ThrowIfNull(writeChecksummed);
        IndexManifest? manifest = TryReadManifest(generationDir);
        if (manifest is null)
            return false;

        // Nothing to do if the checkpoint is already at or past the target (idempotent / never regresses).
        if (manifest.FreshnessCheckpoint.JournalId == newCheckpoint.JournalId
            && manifest.FreshnessCheckpoint.NextUsn >= newCheckpoint.NextUsn)
            return false;

        string finalPath = Path.Combine(generationDir, ManifestFile);
        string tempPath = finalPath + ".reanchor.tmp";
        try
        {
            IndexManifest updated = manifest with { FreshnessCheckpoint = newCheckpoint };
            writeChecksummed(tempPath, Encoding.UTF8.GetBytes(updated.Serialize()));

            // Validate the freshly written manifest (checksum + parseable + still structurally trusted)
            // before it atomically replaces the live one.
            if (!TryReadChecksummed(tempPath, out byte[] tempBody)
                || IndexManifest.Deserialize(Encoding.UTF8.GetString(tempBody)) is not { } roundTripped
                || roundTripped.EvaluateStructural() != IndexStructuralVerdict.Trusted)
            {
                DeleteFileSafe(tempPath);
                return false;
            }

            File.Move(tempPath, finalPath, overwrite: true); // atomic same-directory replace on NTFS/ReFS
            IndexMutationFaults.Hit(IndexMutationFaults.ReanchorManifestReplaced);
            YaguLog.For("ContentIndex").LogDebug(
                "Re-anchored freshness checkpoint in '{GenerationDir}' to {JournalId}/{NextUsn}.",
                generationDir, newCheckpoint.JournalId, newCheckpoint.NextUsn);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "TryReanchorManifestCheckpoint: rewrite failed for '{GenerationDir}' (left unchanged).", generationDir);
            DeleteFileSafe(tempPath);
            return false;
        }
    }

    private static void DeleteFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp file; a leftover .reanchor.tmp is harmless (ignored by readers).
            YaguLog.For("ContentIndex").LogDebug(ex, "TryReanchorManifestCheckpoint: could not delete temp '{Path}'.", path);
        }
    }

    /// <summary>
    /// Reads ONLY the manifest + fileids of a generation and builds its <see cref="FileIdMap"/>, WITHOUT
    /// deserializing content.bin (the multi-GB posting data). Lets a freshness/staleness check replay the
    /// change journal for a paged index for a few MB of I/O instead of loading the whole generation and
    /// rebuilding its posting index. Returns null when the manifest or fileids is missing, checksum-invalid,
    /// or structurally incompatible.
    /// </summary>
    public static (IndexManifest Manifest, FileIdMap FileIds)? TryReadFreshnessInputs(string generationDir)
    {
        IndexManifest? manifest = TryReadManifest(generationDir);
        if (manifest is null)
            return null;
        try
        {
            if (!TryReadChecksummed(Path.Combine(generationDir, FileIdsFile), out byte[] fileIdBytes))
            {
                YaguLog.For("ContentIndex").LogDebug("TryReadFreshnessInputs: fileids missing or checksum-invalid in '{GenerationDir}'.", generationDir);
                return null;
            }
            List<UsnFileIdentity?> identities = DeserializeFileIds(fileIdBytes);
            var map = new FileIdMap(manifest.VolumeSerialNumber);
            for (int contentId = 0; contentId < identities.Count; contentId++)
            {
                if (identities[contentId] is { } identity)
                    map.Add(contentId, identity);
            }
            return (manifest, map);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "TryReadFreshnessInputs: exception reading fileids in '{GenerationDir}'.", generationDir);
            return null;
        }
    }

    /// <summary>
    /// The metadata an incremental USN refresh needs from one active layer: its trusted manifest plus
    /// durable identity-to-path mappings. Reads only <c>manifest.json</c>, <c>aliases.bin</c>, and
    /// <c>fileids.bin</c>, streaming the record files through their checksums. It deliberately never opens
    /// <c>content.bin</c>, builds trigram postings, or materializes document trigram sets.
    /// </summary>
    internal readonly record struct IncrementalLayerMetadata(
        IndexManifest Manifest,
        IReadOnlyDictionary<UsnFileIdentity, IReadOnlyList<string>> PathsByIdentity,
        IReadOnlyList<string> ShadowedPaths);

    internal static IncrementalLayerMetadata? TryReadIncrementalLayerMetadata(
        string generationDir,
        IReadOnlySet<UsnFileIdentity> targetIdentities,
        IReadOnlySet<string> shadowCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetIdentities);
        ArgumentNullException.ThrowIfNull(shadowCandidates);
        IndexManifest? manifest = TryReadManifest(generationDir);
        if (manifest is null)
            return null;

        try
        {
            using ChecksummedFile.ChecksummedReader? identityReader = ChecksummedFile.ChecksummedReader.Open(
                Path.Combine(generationDir, FileIdsFile));
            if (identityReader is null
                || !identityReader.TryReadInt32(out int identityCount)
                || identityCount < 0
                || identityCount != manifest.ContentCount)
                return null;

            var targetIdentityByContent = new Dictionary<long, UsnFileIdentity>(targetIdentities.Count);
            for (int i = 0; i < identityCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!identityReader.TryReadByte(out byte present))
                    return null;
                if (present == 0)
                    continue;
                if (present != 1
                    || !identityReader.TryReadUInt64(out ulong low)
                    || !identityReader.TryReadUInt64(out ulong high))
                    return null;
                var identity = new UsnFileIdentity(low, high);
                if (targetIdentities.Contains(identity))
                    targetIdentityByContent[i] = identity;
            }
            if (!identityReader.TryFinish())
                return null;

            using ChecksummedFile.ChecksummedReader? aliasReader = ChecksummedFile.ChecksummedReader.Open(
                Path.Combine(generationDir, AliasesFile));
            if (aliasReader is null
                || !aliasReader.TryReadInt32(out int aliasCount)
                || aliasCount < 0
                || aliasCount != manifest.AliasCount)
                return null;

            var mutablePaths = new Dictionary<UsnFileIdentity, List<string>>(targetIdentityByContent.Count);
            var shadowedPaths = new List<string>();
            byte[] pathBuffer = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                for (int i = 0; i < aliasCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!aliasReader.TryReadInt32(out int pathLength)
                        || pathLength < 0
                        || pathLength > 32 * 1024 * 1024)
                        return null;
                    if (pathBuffer.Length < pathLength)
                    {
                        ArrayPool<byte>.Shared.Return(pathBuffer);
                        pathBuffer = ArrayPool<byte>.Shared.Rent(pathLength);
                    }
                    if (!aliasReader.TryReadBytes(pathBuffer.AsSpan(0, pathLength))
                        || !aliasReader.TryReadInt64(out _)
                        || !aliasReader.TryReadInt64(out long contentId)
                        || contentId < 0
                        || contentId >= identityCount)
                        return null;

                    bool isTargetIdentity = targetIdentityByContent.TryGetValue(contentId, out UsnFileIdentity identity);
                    if (!isTargetIdentity && shadowCandidates.Count == 0)
                        continue;
                    string path = Encoding.UTF8.GetString(pathBuffer, 0, pathLength);
                    if (shadowCandidates.Contains(path))
                        shadowedPaths.Add(path);
                    if (isTargetIdentity)
                    {
                        if (!mutablePaths.TryGetValue(identity, out List<string>? paths))
                            mutablePaths[identity] = paths = new List<string>();
                        paths.Add(path);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pathBuffer);
            }
            if (!aliasReader.TryFinish())
                return null;

            var pathsByIdentity = new Dictionary<UsnFileIdentity, IReadOnlyList<string>>(mutablePaths.Count);
            foreach ((UsnFileIdentity identity, List<string> paths) in mutablePaths)
                pathsByIdentity[identity] = paths;
            return new IncrementalLayerMetadata(manifest, pathsByIdentity, shadowedPaths);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogDebug(
                ex,
                "TryReadIncrementalLayerMetadata: exception reading lightweight metadata in '{GenerationDir}'.",
                generationDir);
            return null;
        }
    }

    // ─────────────────────────── streaming structural validation (plan §5.7) ───────────────────────────

    /// <summary>
    /// The shape a structural validation confirmed: the document, alias, and identity record counts. Lets
    /// the caller cross-check the counts without loading the generation.
    /// </summary>
    internal readonly record struct SerializedGenerationShape(int DocumentCount, int AliasCount, int IdentityCount);

    /// <summary>
    /// Read-after-write validator for a <b>freshly written</b> generation (plan §5.7). It streams every
    /// file, verifies each SHA-256 trailer (constant-time) and every structural invariant — non-negative
    /// counts, exact record boundaries, exact EOF (no trailing garbage), fileid count equal to the document
    /// count, valid identity presence bytes, and every alias content id in <c>[0, documentCount)</c> — and
    /// checks the manifest counts against the persisted counts, <b>without ever building a posting index,
    /// posting arrays, or per-document trigram collections</b>. This is the publication gate;
    /// <see cref="TryRead"/> remains the trusted object loader for query, compaction, and explicit Validate.
    /// Returns false on any missing file, checksum failure, or structural violation; never throws for I/O.
    /// </summary>
    internal static bool TryValidateSerializedGeneration(string generationDir, out SerializedGenerationShape shape, CancellationToken cancellationToken = default)
    {
        shape = default;
        if (string.IsNullOrEmpty(generationDir) || !Directory.Exists(generationDir))
            return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Manifest (tiny): checksum + structural version trust.
            if (!TryReadChecksummed(Path.Combine(generationDir, ManifestFile), out byte[] manifestBytes, cancellationToken))
                return false;
            IndexManifest? manifest = IndexManifest.Deserialize(Encoding.UTF8.GetString(manifestBytes));
            if (manifest is null || manifest.EvaluateStructural() != IndexStructuralVerdict.Trusted)
                return false;

            if (!TryValidateContent(Path.Combine(generationDir, ContentFile), out int documentCount, cancellationToken))
                return false;
            if (!TryValidateAliases(Path.Combine(generationDir, AliasesFile), documentCount, out int aliasCount, cancellationToken))
                return false;
            if (!TryValidateFileIds(Path.Combine(generationDir, FileIdsFile), documentCount, out int identityCount, cancellationToken))
                return false;

            // The manifest's declared counts must match what was actually persisted.
            if (manifest.ContentCount != documentCount || manifest.AliasCount != aliasCount)
            {
                YaguLog.For("ContentIndex").LogWarning("Validate: manifest counts ({ManifestContent}/{ManifestAlias}) disagree with persisted ({Content}/{Alias}) in '{GenerationDir}'.", manifest.ContentCount, manifest.AliasCount, documentCount, aliasCount, generationDir);
                return false;
            }

            shape = new SerializedGenerationShape(documentCount, aliasCount, identityCount);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Validate: exception validating generation '{GenerationDir}' (treated as invalid).", generationDir);
            return false;
        }
    }

    /// <summary>Streams content.bin: <c>int32 docCount, per doc [int32 trigramCount, uint32×N]</c>, skipping
    /// (still hashing) the trigram values. No postings or trigram collections are built.</summary>
    private static bool TryValidateContent(string path, out int documentCount, CancellationToken cancellationToken)
    {
        documentCount = 0;
        using ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return false;
        if (!reader.TryReadInt32(out int docCount) || docCount < 0)
            return false;
        for (int i = 0; i < docCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadInt32(out int trigramCount) || trigramCount < 0)
                return false;
            if (!reader.Skip((long)trigramCount * 4)) // each trigram is a uint32
                return false;
        }
        if (!reader.TryFinish())
            return false;
        documentCount = docCount;
        return true;
    }

    /// <summary>Streams aliases.bin: <c>int32 count, per [int32 pathLen, utf8 path, int64 aliasId, int64 contentId]</c>,
    /// asserting every content id lands in <c>[0, documentCount)</c>. Path bytes are consumed, not decoded
    /// (matching the lenient <see cref="Encoding.UTF8.GetString(byte[])"/> the reader uses).</summary>
    private static bool TryValidateAliases(string path, int documentCount, out int aliasCount, CancellationToken cancellationToken)
    {
        aliasCount = 0;
        using ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return false;
        if (!reader.TryReadInt32(out int count) || count < 0)
            return false;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadInt32(out int pathLen) || pathLen < 0)
                return false;
            if (!reader.Skip(pathLen))
                return false;
            if (!reader.TryReadInt64(out _)) // aliasId (unconstrained)
                return false;
            if (!reader.TryReadInt64(out long contentId))
                return false;
            if (contentId < 0 || contentId >= documentCount)
                return false;
        }
        if (!reader.TryFinish())
            return false;
        aliasCount = count;
        return true;
    }

    /// <summary>Streams fileids.bin: <c>int32 count, per [byte present; if 1: uint64 low, uint64 high]</c>,
    /// requiring the count to equal the document count and every presence byte to be 0 or 1.</summary>
    private static bool TryValidateFileIds(string path, int documentCount, out int identityCount, CancellationToken cancellationToken)
    {
        identityCount = 0;
        using ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return false;
        if (!reader.TryReadInt32(out int count) || count < 0 || count != documentCount)
            return false;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadByte(out byte present))
                return false;
            if (present == 1)
            {
                if (!reader.Skip(16)) // uint64 low + uint64 high
                    return false;
            }
            else if (present != 0)
            {
                return false; // invalid presence byte
            }
        }
        if (!reader.TryFinish())
            return false;
        identityCount = count;
        return true;
    }

    /// <summary>
    /// Streams a checksummed tombstones file for a delta segment: <c>int32 count, per [int32 pathLen, utf8 path]</c>.
    /// Shared with <see cref="ContentIndexDeltaSegmentSerializer"/> so the segment validator never duplicates
    /// this record parser. Returns false on checksum failure, negative counts, or trailing garbage.
    /// </summary>
    internal static bool TryValidateTombstones(string path, CancellationToken cancellationToken)
    {
        using ChecksummedFile.ChecksummedReader? reader = ChecksummedFile.ChecksummedReader.Open(path);
        if (reader is null)
            return false;
        if (!reader.TryReadInt32(out int count) || count < 0)
            return false;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadInt32(out int pathLen) || pathLen < 0)
                return false;
            if (!reader.Skip(pathLen))
                return false;
        }
        return reader.TryFinish();
    }

    // ─────────────────────────── content.bin ───────────────────────────

    private static void WriteContentBody(Stream stream, IReadOnlyList<IReadOnlyCollection<Trigram>> documents, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(documents.Count);
        foreach (var doc in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(doc.Count);
            foreach (Trigram t in doc)
                writer.Write(t.Value);
        }
        writer.Flush();
    }

    /// <summary>
    /// Streams and checksum-validates a <c>content.bin</c> into its per-document trigram sets, or null when
    /// the file is missing, truncated, malformed, or fails its digest. Streaming (rather than reading the
    /// whole body into one array) is what lets a compacted layer exceed 2 GiB of content records.
    /// </summary>
    private static List<IReadOnlyCollection<Trigram>>? TryDeserializeContentFile(string contentPath, CancellationToken cancellationToken)
    {
        using IndexContentFileReader? reader = IndexContentFileReader.Open(contentPath);
        if (reader is null)
            return null;
        var documents = new List<IReadOnlyCollection<Trigram>>(reader.DocumentCount);
        var buffer = new List<Trigram>();
        while (reader.TryReadNext(buffer, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.Add(buffer.ToArray());
        }
        return reader.TryFinish() ? documents : null;
    }

    // ─────────────────────────── aliases.bin ───────────────────────────

    private static void WriteAliasesBody(Stream stream, IReadOnlyDictionary<string, (long AliasId, long ContentId)> aliases, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(aliases.Count);
        foreach (var (path, entry) in aliases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            writer.Write(pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(entry.AliasId);
            writer.Write(entry.ContentId);
        }
        writer.Flush();
    }

    private static Dictionary<string, (long, long)> DeserializeAliases(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative alias count.");
        var aliases = new Dictionary<string, (long, long)>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int pathLen = reader.ReadInt32();
            if (pathLen < 0)
                throw new InvalidDataException("Negative path length.");
            string path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
            long aliasId = reader.ReadInt64();
            long contentId = reader.ReadInt64();
            aliases[path] = (aliasId, contentId);
        }
        return aliases;
    }

    // ─────────────────────────── fileids.bin ───────────────────────────

    private static void WriteFileIdsBody(Stream stream, IReadOnlyList<UsnFileIdentity?> identities, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(identities.Count);
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity is { } id)
            {
                writer.Write((byte)1);
                writer.Write(id.Low);
                writer.Write(id.High);
            }
            else
            {
                writer.Write((byte)0);
            }
        }
        writer.Flush();
    }

    private static List<UsnFileIdentity?> DeserializeFileIds(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative fileid count.");
        var identities = new List<UsnFileIdentity?>(count);
        for (int i = 0; i < count; i++)
        {
            byte present = reader.ReadByte();
            if (present == 1)
                identities.Add(new UsnFileIdentity(reader.ReadUInt64(), reader.ReadUInt64()));
            else if (present == 0)
                identities.Add(null);
            else
                throw new InvalidDataException("Invalid fileid presence byte.");
        }
        return identities;
    }

    // ─────────────────────────── checksummed file I/O ───────────────────────────
    private static void WriteChecksummed(string path, byte[] body) => ChecksummedFile.Write(path, body);

    private static bool TryReadChecksummed(
        string path,
        out byte[] body,
        CancellationToken cancellationToken = default)
        => ChecksummedFile.TryRead(path, out body, cancellationToken);
}
