using System.Text;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Reads and writes an <see cref="ExtendedSourceNamespace"/> to a directory (plan §7 Phase 4 on-disk
/// format). Mirrors <see cref="ContentIndexGenerationSerializer"/>: each file is self-checked with a
/// trailing SHA-256 (via <see cref="ChecksummedFile"/>) so a truncated or corrupt namespace reads back as
/// <c>null</c> and its source kind falls back to live extraction. It persists <b>only</b> the extractor
/// fingerprint, per-source trigram postings, source keys, and durable negative-exclusion proofs — never
/// any extracted source text (§6.4). The format is independent of the raw-text generation format.
/// </summary>
public static class ExtendedSourceNamespaceSerializer
{
    /// <summary>On-disk format version; a reader rejects any other value (→ live-scan).</summary>
    public const int FormatVersion = 2;

    public const string HeaderFile = "esns-header.bin";
    public const string ContentFile = "esns-content.bin";
    public const string SourcesFile = "esns-sources.bin";
    public const string IdentitiesFile = "esns-identities.bin";

    /// <summary>Writes the namespace's files (each with a trailing digest) into <paramref name="namespaceDir"/>.</summary>
    public static void Write(string namespaceDir, ExtendedSourceNamespace ns)
    {
        ArgumentException.ThrowIfNullOrEmpty(namespaceDir);
        ArgumentNullException.ThrowIfNull(ns);
        Directory.CreateDirectory(namespaceDir);
        ChecksummedFile.Write(Path.Combine(namespaceDir, HeaderFile), SerializeHeader(ns));
        ChecksummedFile.Write(Path.Combine(namespaceDir, ContentFile), SerializeContent(ns.Documents));
        ChecksummedFile.Write(Path.Combine(namespaceDir, SourcesFile), SerializeStrings(ns.SourceKeys));
        ChecksummedFile.Write(Path.Combine(namespaceDir, IdentitiesFile), SerializeIdentities(ns.SourceIdentityByKey));
    }

    /// <summary>
    /// Reads and validates a namespace from <paramref name="namespaceDir"/>. Returns <c>null</c> when any
    /// file is missing, truncated, checksum-invalid, the version is unsupported, or the source/content
    /// counts disagree — in every case the caller live-extracts.
    /// </summary>
    public static ExtendedSourceNamespace? TryRead(string namespaceDir)
    {
        if (string.IsNullOrEmpty(namespaceDir) || !Directory.Exists(namespaceDir))
            return null;
        try
        {
            if (!ChecksummedFile.TryRead(Path.Combine(namespaceDir, HeaderFile), out byte[] headerBytes)
                || !ChecksummedFile.TryRead(Path.Combine(namespaceDir, ContentFile), out byte[] contentBytes)
                || !ChecksummedFile.TryRead(Path.Combine(namespaceDir, SourcesFile), out byte[] sourceBytes)
                || !ChecksummedFile.TryRead(Path.Combine(namespaceDir, IdentitiesFile), out byte[] identityBytes))
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "ExtendedSourceNamespace TryRead: a file is missing or failed its checksum in '{NamespaceDir}' (treated as corrupt).", namespaceDir);
                return null;
            }

            (SpecialSourceKind kind, ExtractorFingerprint fingerprint, HashSet<string> negatives,
                string rootPath, UsnCheckpoint checkpoint) = DeserializeHeader(headerBytes);
            List<IReadOnlyCollection<Trigram>> documents = DeserializeContent(contentBytes);
            List<string> sourceKeys = DeserializeStrings(sourceBytes);
            Dictionary<string, UsnFileIdentity?> identities = DeserializeIdentities(identityBytes);

            if (sourceKeys.Count != documents.Count)
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "ExtendedSourceNamespace TryRead: source/content count mismatch ({SourceCount} vs {DocumentCount}) in '{NamespaceDir}'.", sourceKeys.Count, documents.Count, namespaceDir);
                return null;
            }

            return new ExtendedSourceNamespace(kind, fingerprint, documents, sourceKeys, negatives, identities, rootPath, checkpoint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "ExtendedSourceNamespace TryRead: exception while reading '{NamespaceDir}' (treated as corrupt).", namespaceDir);
            return null;
        }
    }

    // ─────────────────────────── esns-header.bin ───────────────────────────
    // version, kind, fingerprint (engine/version/runtime + binary hashes + options), negative-proof keys,
    // normalized root path, and the build-time USN checkpoint.

    private static byte[] SerializeHeader(ExtendedSourceNamespace ns)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(FormatVersion);
        w.Write((int)ns.Kind);

        ExtractorFingerprint fp = ns.Fingerprint;
        WriteString(w, fp.EngineId);
        WriteString(w, fp.EngineVersion);
        WriteString(w, fp.Runtime);
        w.Write(fp.BinaryHashes.Count);
        foreach (ExtractorFileHash h in fp.BinaryHashes)
        {
            WriteString(w, h.Role);
            WriteString(w, h.Sha256);
        }
        w.Write(fp.Options.Count);
        foreach (KeyValuePair<string, string> o in fp.Options)
        {
            WriteString(w, o.Key);
            WriteString(w, o.Value);
        }

        w.Write(ns.NegativeProofKeys.Count);
        foreach (string k in ns.NegativeProofKeys)
            WriteString(w, k);

        WriteString(w, ns.NormalizedRootPath);
        w.Write(ns.FreshnessCheckpoint.JournalId);
        w.Write(ns.FreshnessCheckpoint.NextUsn);

        w.Flush();
        return ms.ToArray();
    }

    private static (SpecialSourceKind Kind, ExtractorFingerprint Fingerprint, HashSet<string> Negatives,
        string RootPath, UsnCheckpoint Checkpoint) DeserializeHeader(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        int version = r.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported extended-source namespace version {version}.");

        var kind = (SpecialSourceKind)r.ReadInt32();
        if (!Enum.IsDefined(kind))
            throw new InvalidDataException($"Invalid source kind {(int)kind}.");

        string engineId = ReadString(r);
        string engineVersion = ReadString(r);
        string runtime = ReadString(r);

        int hashCount = ReadCount(r);
        var hashes = new List<ExtractorFileHash>(hashCount);
        for (int i = 0; i < hashCount; i++)
            hashes.Add(new ExtractorFileHash(ReadString(r), ReadString(r)));

        int optCount = ReadCount(r);
        var options = new List<KeyValuePair<string, string>>(optCount);
        for (int i = 0; i < optCount; i++)
            options.Add(new KeyValuePair<string, string>(ReadString(r), ReadString(r)));

        int negCount = ReadCount(r);
        var negatives = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < negCount; i++)
            negatives.Add(ReadString(r));

        string rootPath = ReadString(r);
        var checkpoint = new UsnCheckpoint(r.ReadUInt64(), r.ReadInt64());

        var fingerprint = new ExtractorFingerprint(kind, engineId, engineVersion, runtime, hashes, options);
        return (kind, fingerprint, negatives, rootPath, checkpoint);
    }

    // ─────────────────────────── esns-identities.bin ───────────────────────────
    // int32 count, per [string key, byte present, if present: ulong Low, ulong High] — the build-time file
    // identity for every source key (admitted members and negative proofs); a null identity forces live-extract.

    private static byte[] SerializeIdentities(IReadOnlyDictionary<string, UsnFileIdentity?> identities)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(identities.Count);
        foreach (KeyValuePair<string, UsnFileIdentity?> pair in identities)
        {
            WriteString(w, pair.Key);
            if (pair.Value is { } id)
            {
                w.Write((byte)1);
                w.Write(id.Low);
                w.Write(id.High);
            }
            else
            {
                w.Write((byte)0);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    private static Dictionary<string, UsnFileIdentity?> DeserializeIdentities(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int count = ReadCount(r);
        var identities = new Dictionary<string, UsnFileIdentity?>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = ReadString(r);
            byte present = r.ReadByte();
            identities[key] = present switch
            {
                1 => new UsnFileIdentity(r.ReadUInt64(), r.ReadUInt64()),
                0 => null,
                _ => throw new InvalidDataException("Invalid identity presence byte."),
            };
        }
        return identities;
    }

    // ─────────────────────────── esns-content.bin ───────────────────────────
    // int32 docCount, per doc [int32 trigramCount, uint32 x N] — same layout as the generation content.bin.

    private static byte[] SerializeContent(IReadOnlyList<IReadOnlyCollection<Trigram>> documents)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(documents.Count);
        foreach (IReadOnlyCollection<Trigram> doc in documents)
        {
            w.Write(doc.Count);
            foreach (Trigram t in doc)
                w.Write(t.Value);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static List<IReadOnlyCollection<Trigram>> DeserializeContent(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int docCount = ReadCount(r);
        var documents = new List<IReadOnlyCollection<Trigram>>(docCount);
        for (int i = 0; i < docCount; i++)
        {
            int trigramCount = ReadCount(r);
            var set = new List<Trigram>(trigramCount);
            for (int j = 0; j < trigramCount; j++)
                set.Add(Trigram.FromPacked(r.ReadUInt32()));
            documents.Add(set);
        }
        return documents;
    }

    // ─────────────────────────── esns-sources.bin ───────────────────────────
    // int32 count, per [int32 len, utf8 bytes] — the source key for each document id.

    private static byte[] SerializeStrings(IReadOnlyList<string> strings)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(strings.Count);
        foreach (string s in strings)
            WriteString(w, s);
        w.Flush();
        return ms.ToArray();
    }

    private static List<string> DeserializeStrings(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int count = ReadCount(r);
        var strings = new List<string>(count);
        for (int i = 0; i < count; i++)
            strings.Add(ReadString(r));
        return strings;
    }

    // ─────────────────────────── primitives ───────────────────────────

    private static void WriteString(BinaryWriter w, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
    }

    private static string ReadString(BinaryReader r)
    {
        int len = ReadCount(r);
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private static int ReadCount(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0)
            throw new InvalidDataException("Negative count in extended-source namespace.");
        return n;
    }
}
