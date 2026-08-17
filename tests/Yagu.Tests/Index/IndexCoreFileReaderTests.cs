using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Covers the streaming record readers over the checksummed core index files. They are the only way a
/// bounded-memory merge sees a layer's aliases, tombstones, content, and identities, so they must
/// reproduce the persisted records exactly and fail closed on any corruption or truncation.
/// </summary>
public sealed class IndexCoreFileReaderTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly string _scopeId;

    public IndexCoreFileReaderTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-core-readers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private string WriteGeneration(params (string Path, string Text)[] docs)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        foreach ((string path, string text) in docs)
            builder.AddDocument(path, Encoding.UTF8.GetBytes(text));
        ContentIndexGeneration generation = builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        string dir = Path.Combine(_sandbox, "gen-" + Guid.NewGuid().ToString("N"));
        ContentIndexGenerationSerializer.Write(dir, generation);
        return dir;
    }

    [Fact]
    public void AliasContentAndIdentityReaders_ReproduceEveryPersistedRecord()
    {
        string dir = WriteGeneration(
            (@"C:\r\alpha.txt", "alpha zephyrqux content"),
            (@"C:\r\beta.txt", "beta ordinary content"),
            (@"C:\r\gamma.txt", "gamma ordinary content"));

        ContentIndexGeneration? reloaded = ContentIndexGenerationSerializer.TryRead(dir);
        Assert.NotNull(reloaded);

        using (IndexAliasFileReader? aliases =
               IndexAliasFileReader.Open(Path.Combine(dir, ContentIndexGenerationSerializer.AliasesFile)))
        {
            Assert.NotNull(aliases);
            var read = new List<IndexAliasRecord>();
            while (aliases!.TryReadNext(out IndexAliasRecord record))
                read.Add(record);
            Assert.True(aliases.TryFinish());
            Assert.Equal(aliases.Count, read.Count);
            foreach (IndexAliasRecord record in read)
            {
                Assert.True(reloaded!.TryGetAlias(record.Path, out long aliasId, out long contentId));
                Assert.Equal(aliasId, record.AliasId);
                Assert.Equal(contentId, record.ContentId);
            }
        }

        using (IndexContentFileReader? content =
               IndexContentFileReader.Open(Path.Combine(dir, ContentIndexGenerationSerializer.ContentFile)))
        {
            Assert.NotNull(content);
            var buffer = new List<Trigram>();
            int seen = 0;
            while (content!.TryReadNext(buffer, out int contentId))
            {
                Assert.Equal(seen, contentId);
                Assert.Equal(reloaded!.Documents[contentId].ToArray(), buffer.ToArray());
                seen++;
            }
            Assert.True(content.TryFinish());
            Assert.Equal(content.DocumentCount, seen);
        }

        using (IndexFileIdentityFileReader? identities =
               IndexFileIdentityFileReader.Open(Path.Combine(dir, ContentIndexGenerationSerializer.FileIdsFile)))
        {
            Assert.NotNull(identities);
            int seen = 0;
            while (identities!.TryReadNext(out UsnFileIdentity? identity, out int contentId))
            {
                Assert.Equal(seen, contentId);
                Assert.Equal(reloaded!.ContentIdentities[contentId], identity);
                seen++;
            }
            Assert.True(identities.TryFinish());
            Assert.Equal(identities.Count, seen);
        }
    }

    [Fact]
    public void TombstoneReader_ReproducesEveryRemovedPath()
    {
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy, null, IndexTestIdentities.Provider);
        builder.AddChangedDocument(@"C:\r\kept.txt", Encoding.UTF8.GetBytes("kept"));
        builder.AddTombstone(@"C:\r\gone one.txt");
        builder.AddTombstone(@"C:\r\ушёл.txt");
        ContentIndexDeltaSegment segment = builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 200), DateTimeOffset.UtcNow);
        string dir = Path.Combine(_sandbox, "seg");
        ContentIndexDeltaSegmentSerializer.Write(dir, segment);

        using IndexTombstoneFileReader? reader =
            IndexTombstoneFileReader.Open(Path.Combine(dir, ContentIndexDeltaSegmentSerializer.TombstonesFile));
        Assert.NotNull(reader);
        var paths = new List<string>();
        while (reader!.TryReadNext(out string path))
            paths.Add(path);

        Assert.True(reader.TryFinish());
        Assert.Equal(segment.RemovedPaths.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            paths.OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void MissingFile_OpensAsNull()
    {
        string missing = Path.Combine(_sandbox, "nope.bin");
        Assert.Null(IndexAliasFileReader.Open(missing));
        Assert.Null(IndexTombstoneFileReader.Open(missing));
        Assert.Null(IndexContentFileReader.Open(missing));
        Assert.Null(IndexFileIdentityFileReader.Open(missing));

        string tooShort = Path.Combine(_sandbox, "too-short.bin");
        File.WriteAllBytes(tooShort, new byte[ChecksummedFile.DigestBytes - 1]);
        Assert.Null(IndexAliasFileReader.Open(tooShort));
        Assert.Null(IndexTombstoneFileReader.Open(tooShort));
        Assert.Null(IndexContentFileReader.Open(tooShort));
        Assert.Null(IndexFileIdentityFileReader.Open(tooShort));
    }

    [Fact]
    public void CorruptedChecksum_IsRejectedByFinish_NotSilentlyAccepted()
    {
        string dir = WriteGeneration((@"C:\r\alpha.txt", "alpha content"));
        string aliasesPath = Path.Combine(dir, ContentIndexGenerationSerializer.AliasesFile);
        byte[] bytes = File.ReadAllBytes(aliasesPath);
        bytes[^1] ^= 0xFF; // flip a digest byte: records still parse, the checksum must not
        File.WriteAllBytes(aliasesPath, bytes);

        using IndexAliasFileReader? reader = IndexAliasFileReader.Open(aliasesPath);
        Assert.NotNull(reader);
        while (reader!.TryReadNext(out _)) { }
        Assert.False(reader.TryFinish());
    }

    [Fact]
    public void TruncatedBody_StopsReadingAndFailsFinish()
    {
        string dir = WriteGeneration((@"C:\r\alpha.txt", "alpha content"), (@"C:\r\beta.txt", "beta content"));
        string contentPath = Path.Combine(dir, ContentIndexGenerationSerializer.ContentFile);
        byte[] bytes = File.ReadAllBytes(contentPath);
        File.WriteAllBytes(contentPath, bytes[..(bytes.Length / 2)]);

        using IndexContentFileReader? reader = IndexContentFileReader.Open(contentPath);
        if (reader is not null)
        {
            var buffer = new List<Trigram>();
            while (reader.TryReadNext(buffer, out _)) { }
            Assert.False(reader.TryFinish());
        }
    }

    [Fact]
    public void NegativeOrOversizedDeclaredCounts_AreRejected()
    {
        string negativeCount = Path.Combine(_sandbox, "negative.bin");
        WriteChecksummed(negativeCount, BitConverter.GetBytes(-1));
        Assert.Null(IndexAliasFileReader.Open(negativeCount));
        Assert.Null(IndexTombstoneFileReader.Open(negativeCount));
        Assert.Null(IndexContentFileReader.Open(negativeCount));
        Assert.Null(IndexFileIdentityFileReader.Open(negativeCount));

        // One declared record whose path length cannot possibly fit in the remaining body.
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(1));
        body.AddRange(BitConverter.GetBytes(int.MaxValue));
        string absurdPathLength = Path.Combine(_sandbox, "absurd.bin");
        WriteChecksummed(absurdPathLength, body.ToArray());

        using IndexTombstoneFileReader? reader = IndexTombstoneFileReader.Open(absurdPathLength);
        Assert.NotNull(reader);
        Assert.False(reader!.TryReadNext(out _));
        Assert.False(reader.TryFinish());
    }

    [Fact]
    public void InvalidIdentityPresenceByte_IsRejected()
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(1));
        body.Add(7); // neither 0 (absent) nor 1 (present)
        string path = Path.Combine(_sandbox, "identities.bin");
        WriteChecksummed(path, body.ToArray());

        using IndexFileIdentityFileReader? reader = IndexFileIdentityFileReader.Open(path);
        Assert.NotNull(reader);
        Assert.False(reader!.TryReadNext(out _, out _));
        Assert.False(reader.TryFinish());
    }

    [Fact]
    public void AliasAndTombstoneReaders_RejectMalformedRecordsAfterAValidHeader()
    {
        string missingAliasIds = Path.Combine(_sandbox, "alias-missing-ids.bin");
        WriteChecksummed(missingAliasIds, [1, 0, 0, 0, 0, 0, 0, 0]);
        using (IndexAliasFileReader? aliases = IndexAliasFileReader.Open(missingAliasIds))
        {
            Assert.NotNull(aliases);
            Assert.False(aliases!.TryReadNext(out _));
            Assert.False(aliases.TryFinish());
        }

        string invalidAliasPath = Path.Combine(_sandbox, "alias-invalid-path.bin");
        var invalidAliasBody = new List<byte>();
        invalidAliasBody.AddRange(BitConverter.GetBytes(1));
        invalidAliasBody.AddRange(BitConverter.GetBytes(int.MaxValue));
        WriteChecksummed(invalidAliasPath, invalidAliasBody.ToArray());
        using (IndexAliasFileReader? aliases = IndexAliasFileReader.Open(invalidAliasPath))
        {
            Assert.NotNull(aliases);
            Assert.False(aliases!.TryReadNext(out _));
        }

        string emptyTombstonePath = Path.Combine(_sandbox, "empty-tombstone.bin");
        WriteChecksummed(emptyTombstonePath, [1, 0, 0, 0, 0, 0, 0, 0]);
        using (IndexTombstoneFileReader? tombstones = IndexTombstoneFileReader.Open(emptyTombstonePath))
        {
            Assert.NotNull(tombstones);
            Assert.True(tombstones!.TryReadNext(out string emptyPath));
            Assert.Equal(string.Empty, emptyPath);
            Assert.False(tombstones.TryReadNext(out _));
            Assert.True(tombstones.TryFinish());
        }

        string negativeTombstonePath = Path.Combine(_sandbox, "negative-tombstone.bin");
        var negativeTombstoneBody = new List<byte>();
        negativeTombstoneBody.AddRange(BitConverter.GetBytes(1));
        negativeTombstoneBody.AddRange(BitConverter.GetBytes(-1));
        WriteChecksummed(negativeTombstonePath, negativeTombstoneBody.ToArray());
        using (IndexTombstoneFileReader? tombstones = IndexTombstoneFileReader.Open(negativeTombstonePath))
        {
            Assert.NotNull(tombstones);
            Assert.False(tombstones!.TryReadNext(out _));
            Assert.False(tombstones.TryFinish());
        }
    }

    [Fact]
    public void ContentReader_RejectsMalformedCounts_AndAcceptsAnEmptyDocument()
    {
        var malformedCounts = new Dictionary<string, int>
        {
            ["negative-trigrams.bin"] = -1,
            ["excessive-trigrams.bin"] = IndexCoreFileReaders.MaxTrigramsPerDocument + 1,
            ["missing-trigram-bytes.bin"] = 1,
        };
        foreach ((string name, int trigramCount) in malformedCounts)
        {
            string path = Path.Combine(_sandbox, name);
            var body = new List<byte>();
            body.AddRange(BitConverter.GetBytes(1));
            body.AddRange(BitConverter.GetBytes(trigramCount));
            WriteChecksummed(path, body.ToArray());

            using IndexContentFileReader? reader = IndexContentFileReader.Open(path);
            Assert.NotNull(reader);
            Assert.False(reader!.TryReadNext([], out _));
            Assert.False(reader.TryFinish());
        }

        string emptyDocumentPath = Path.Combine(_sandbox, "empty-document.bin");
        WriteChecksummed(emptyDocumentPath, [1, 0, 0, 0, 0, 0, 0, 0]);
        using (IndexContentFileReader? reader = IndexContentFileReader.Open(emptyDocumentPath))
        {
            Assert.NotNull(reader);
            var trigrams = new List<Trigram> { Trigram.FromPacked(1) };
            Assert.True(reader!.TryReadNext(trigrams, out int contentId));
            Assert.Equal(0, contentId);
            Assert.Empty(trigrams);
            Assert.False(reader.TryReadNext(trigrams, out _));
            Assert.True(reader.TryFinish());
            Assert.Throws<ArgumentNullException>(() => reader.TryReadNext(null!, out _));
        }
    }

    [Fact]
    public void IdentityReader_HandlesAbsentIdentity_AndRejectsTruncatedRecords()
    {
        string path = Path.Combine(_sandbox, "identity-truncated.bin");
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(2));
        body.Add(0);
        body.Add(1);
        body.AddRange(BitConverter.GetBytes(123UL));
        WriteChecksummed(path, body.ToArray());

        using (IndexFileIdentityFileReader? reader = IndexFileIdentityFileReader.Open(path))
        {
            Assert.NotNull(reader);
            Assert.True(reader!.TryReadNext(out UsnFileIdentity? absent, out int firstContentId));
            Assert.Null(absent);
            Assert.Equal(0, firstContentId);
            Assert.False(reader.TryReadNext(out _, out _));
            Assert.False(reader.TryFinish());
        }

        string missingPresence = Path.Combine(_sandbox, "identity-missing-presence.bin");
        WriteChecksummed(missingPresence, BitConverter.GetBytes(1));
        using IndexFileIdentityFileReader? missing = IndexFileIdentityFileReader.Open(missingPresence);
        Assert.NotNull(missing);
        Assert.False(missing!.TryReadNext(out _, out _));
    }

    [Fact]
    public void FinishBeforeAllDeclaredRecords_FailsClosed()
    {
        string directory = WriteGeneration(
            (@"C:\r\alpha.txt", "alpha content"),
            (@"C:\r\beta.txt", "beta content"));
        using IndexAliasFileReader? aliases =
            IndexAliasFileReader.Open(Path.Combine(directory, ContentIndexGenerationSerializer.AliasesFile));

        Assert.NotNull(aliases);
        Assert.False(aliases!.TryFinish());
    }

    [Fact]
    public void TrailingGarbageAfterTheDeclaredRecords_FailsFinish()
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(1));
        body.AddRange(BitConverter.GetBytes(3));
        body.AddRange(Encoding.UTF8.GetBytes("abc"));
        body.AddRange(new byte[] { 9, 9, 9, 9 }); // unaccounted trailing bytes
        string path = Path.Combine(_sandbox, "trailing.bin");
        WriteChecksummed(path, body.ToArray());

        using IndexTombstoneFileReader? reader = IndexTombstoneFileReader.Open(path);
        Assert.NotNull(reader);
        Assert.True(reader!.TryReadNext(out string read));
        Assert.Equal("abc", read);
        Assert.False(reader.TryFinish());
    }

    private static void WriteChecksummed(string path, byte[] body)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(body);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(body, 0, body.Length);
        fs.Write(digest, 0, digest.Length);
    }
}
