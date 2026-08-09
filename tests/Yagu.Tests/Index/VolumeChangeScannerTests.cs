using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for journal-gap recovery: the rescan strategies that replace a lost change-journal interval so a
/// root does not have to be rebuilt from scratch (<see cref="IVolumeChangeScanner"/> and its two
/// implementations), plus the record parser they share.
/// </summary>
public sealed class VolumeChangeScannerTests
{
    private const long Checkpoint = 1000;

    private static IndexIngestionPolicy Policy() => new(0, null, null, true, false, 0);

    private static UsnCheckpoint Since(long nextUsn = Checkpoint, ulong journalId = 7)
        => new(journalId, nextUsn);

    private static IndexCrawlEntry FileEntry(string path)
        => new(path, 10, FileAttributes.Normal);

    private static UsnFileIdentity Id(ulong low) => UsnFileIdentity.FromFileReferenceNumber(low);

    // ---------- UsnFileRecordParser ----------

    private static byte[] BuildV2Record(ulong fileRef, long usn, FileAttributes attributes, int length = 64)
    {
        var record = new byte[length];
        BitConverter.GetBytes((uint)length).CopyTo(record, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(record, 4);   // MajorVersion
        BitConverter.GetBytes((ushort)0).CopyTo(record, 6);   // MinorVersion
        BitConverter.GetBytes(fileRef).CopyTo(record, 8);     // FileReferenceNumber
        BitConverter.GetBytes(usn).CopyTo(record, 24);        // Usn
        BitConverter.GetBytes((uint)attributes).CopyTo(record, 52);
        return record;
    }

    private static byte[] BuildV3Record(ulong low, ulong high, long usn, FileAttributes attributes, int length = 96)
    {
        var record = new byte[length];
        BitConverter.GetBytes((uint)length).CopyTo(record, 0);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 4);
        BitConverter.GetBytes(low).CopyTo(record, 8);         // FILE_ID_128 low half
        BitConverter.GetBytes(high).CopyTo(record, 16);       // FILE_ID_128 high half
        BitConverter.GetBytes(usn).CopyTo(record, 40);        // Usn
        BitConverter.GetBytes((uint)attributes).CopyTo(record, 68);
        return record;
    }

    [Fact]
    public void Parser_ReadsUsnAndAttributes_FromV2AndV3Records()
    {
        Assert.True(UsnFileRecordParser.TryParseOne(
            BuildV2Record(42, 5150, FileAttributes.Normal), out UsnFileRecord v2));
        Assert.Equal(Id(42), v2.Identity);
        Assert.Equal(5150, v2.Usn);
        Assert.False(v2.Attributes.HasFlag(FileAttributes.Directory));

        Assert.True(UsnFileRecordParser.TryParseOne(
            BuildV3Record(9, 0, 777, FileAttributes.Directory), out UsnFileRecord v3));
        Assert.Equal(777, v3.Usn);
        Assert.True(v3.Attributes.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void Parser_FailsClosed_OnUnknownVersionOrTruncatedRecord()
    {
        byte[] unknown = BuildV2Record(1, 1, FileAttributes.Normal);
        BitConverter.GetBytes((ushort)9).CopyTo(unknown, 4);
        var sink = new List<UsnFileRecord>();
        Assert.Equal(UsnParseStatus.UnknownVersion, UsnFileRecordParser.ParseRecords(unknown, sink));
        Assert.Empty(sink);

        // A record whose declared length runs past the buffer must never yield a partial record.
        byte[] truncated = BuildV2Record(1, 1, FileAttributes.Normal, length: 64);
        Assert.Equal(UsnParseStatus.Malformed, UsnFileRecordParser.ParseRecords(truncated.AsSpan(0, 40), sink));
        Assert.Empty(sink);
    }

    [Fact]
    public void Parser_WalksPackedRecords_AndStopsAtTheEndMarker()
    {
        byte[] first = BuildV2Record(1, 10, FileAttributes.Normal);
        byte[] second = BuildV2Record(2, 20, FileAttributes.Normal);
        var buffer = new byte[first.Length + second.Length + 4];
        first.CopyTo(buffer, 0);
        second.CopyTo(buffer, first.Length);   // trailing zero DWORD = end marker

        var sink = new List<UsnFileRecord>();
        Assert.Equal(UsnParseStatus.Ok, UsnFileRecordParser.ParseRecords(buffer, sink));
        Assert.Equal(new long[] { 10, 20 }, sink.Select(r => r.Usn).ToArray());
    }

    // ---------- PerFileUsnChangeScanner ----------

    private static IndexCrawlerFileSystem CrawlerWith(params string[] files)
    {
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        return new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory == root
                ? files.Select(FileEntry).ToArray()
                : Array.Empty<IndexCrawlEntry>(),
        };
    }

    [Fact]
    public void PerFile_ReportsOnlyFilesWhoseUsnReachedTheCheckpoint()
    {
        var usns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\root\unchanged.txt"] = Checkpoint - 1,
            [@"C:\root\changed.txt"] = Checkpoint,        // checkpoint is the next-unwritten USN
            [@"C:\root\later.txt"] = Checkpoint + 5000,
        };
        var identities = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\root\unchanged.txt"] = 1,
            [@"C:\root\changed.txt"] = 2,
            [@"C:\root\later.txt"] = 3,
        };

        using var scanner = new PerFileUsnChangeScanner(
            path => new UsnFileRecord(Id(identities[path]), usns[path], FileAttributes.Normal),
            CrawlerWith(@"C:\root\unchanged.txt", @"C:\root\changed.txt", @"C:\root\later.txt"));

        VolumeChangeScanResult result = scanner.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), Since(), Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 4, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.FilesExamined);
        Assert.Empty(result.UnprovablePaths);
        Assert.Equal(
            new[] { Id(2), Id(3) }.OrderBy(i => i.Low),
            result.ChangedIdentities.Select(c => c.Identity).OrderBy(i => i.Low));
    }

    [Fact]
    public void PerFile_TombstonesAFileWhoseUsnCannotBeRead_RatherThanTrustingIt()
    {
        using var scanner = new PerFileUsnChangeScanner(
            path => path.EndsWith("denied.txt", StringComparison.OrdinalIgnoreCase)
                ? null
                : new UsnFileRecord(Id(1), Checkpoint - 1, FileAttributes.Normal),
            CrawlerWith(@"C:\root\ok.txt", @"C:\root\denied.txt"));

        VolumeChangeScanResult result = scanner.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), Since(), Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.ChangedIdentities);
        Assert.Equal(
            new[] { IndexScopeIdentity.NormalizePath(@"C:\root\denied.txt") },
            result.UnprovablePaths);
    }

    [Fact]
    public void PerFile_FailsWhenTheRootWasNotCompletelyEnumerated()
    {
        // An unenumerated subtree could hide a changed file; advancing the checkpoint would strand it as
        // permanently stale, so the scan must refuse and let the caller rebuild.
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory == root
                ? throw new IOException("the device is not ready")
                : Array.Empty<IndexCrawlEntry>(),
        };

        using var scanner = new PerFileUsnChangeScanner(
            _ => new UsnFileRecord(Id(1), Checkpoint + 1, FileAttributes.Normal), fs);

        VolumeChangeScanResult result = scanner.Scan(
            root, Since(), Policy(), IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not completely enumerated", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PerFile_RefusesWithoutAUsableCheckpoint()
    {
        using var scanner = new PerFileUsnChangeScanner(
            _ => new UsnFileRecord(Id(1), 5, FileAttributes.Normal), CrawlerWith(@"C:\root\a.txt"));

        VolumeChangeScanResult result = scanner.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), UsnCheckpoint.None, Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("checkpoint", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PerFile_ReadsRealFileChangeUsns_AndSeesAFreshWriteAsChanged()
    {
        // Integration guard: proves FSCTL_READ_FILE_USN_DATA works from a normal, non-elevated session and
        // that a just-written file reports a USN at or beyond the journal cursor captured before the write.
        string root = Path.Combine(Path.GetTempPath(), "yagu-usn-rescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (UsnJournalReader.TryCaptureCheckpoint(root) is not { } barrier || barrier.JournalId == 0)
                return; // no usable change journal on this volume (e.g. exFAT) — nothing to prove here

            string changed = Path.Combine(root, "changed.txt");
            File.WriteAllText(changed, "written after the barrier");

            using var scanner = new PerFileUsnChangeScanner();
            VolumeChangeScanResult result = scanner.Scan(
                IndexScopeIdentity.NormalizePath(root), barrier, Policy(),
                IndexScopeIdentity.NormalizePath(Path.Combine(root, "index-data")), 4, null, CancellationToken.None);

            Assert.True(result.Succeeded, result.Failure);
            Assert.Equal(1, result.FilesExamined);
            Assert.Single(result.ChangedIdentities);

            // The same file is provably unchanged relative to a barrier taken after the write.
            if (UsnJournalReader.TryCaptureCheckpoint(root) is not { } after)
                return;
            VolumeChangeScanResult quiet = scanner.Scan(
                IndexScopeIdentity.NormalizePath(root), after, Policy(),
                IndexScopeIdentity.NormalizePath(Path.Combine(root, "index-data")), 4, null, CancellationToken.None);
            Assert.True(quiet.Succeeded, quiet.Failure);
            Assert.Empty(quiet.ChangedIdentities);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---------- MftUsnChangeScanner ----------

    private static byte[] EnumBuffer(params byte[][] records)
    {
        int size = sizeof(ulong) + records.Sum(r => r.Length);
        var buffer = new byte[size];
        BitConverter.GetBytes(0UL).CopyTo(buffer, 0); // continuation cursor
        int offset = sizeof(ulong);
        foreach (byte[] record in records)
        {
            record.CopyTo(buffer, offset);
            offset += record.Length;
        }
        return buffer;
    }

    [Fact]
    public void Mft_ReportsChangedFiles_AndIgnoresDirectories()
    {
        byte[] payload = EnumBuffer(
            BuildV2Record(11, Checkpoint + 1, FileAttributes.Normal),
            BuildV2Record(12, Checkpoint + 2, FileAttributes.Directory),
            BuildV2Record(13, Checkpoint + 3, FileAttributes.Archive));

        int calls = 0;
        using var scanner = new MftUsnChangeScanner(
            _ => new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)1, ownsHandle: false),
            (Microsoft.Win32.SafeHandles.SafeFileHandle volume, byte[] input, byte[] output, out int returned, out int error) =>
            {
                error = 0;
                if (calls++ > 0)
                {
                    returned = sizeof(ulong); // cursor only → end of enumeration
                    return true;
                }
                payload.CopyTo(output, 0);
                returned = payload.Length;
                return true;
            });

        VolumeChangeScanResult result = scanner.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), Since(), Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { Id(11), Id(13) }.OrderBy(i => i.Low),
            result.ChangedIdentities.Select(c => c.Identity).OrderBy(i => i.Low));
    }

    [Fact]
    public void Mft_ReportsAccessDenied_AsAnElevationRequirement_NotAnError()
    {
        using var scanner = new MftUsnChangeScanner(
            _ => new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)1, ownsHandle: false),
            (Microsoft.Win32.SafeHandles.SafeFileHandle volume, byte[] input, byte[] output, out int returned, out int error) =>
            {
                returned = 0;
                error = 5; // ERROR_ACCESS_DENIED
                return false;
            });

        VolumeChangeScanResult result = scanner.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), Since(), Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("elevation", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- FallbackVolumeChangeScanner ----------

    private sealed class StubScanner(string name, VolumeChangeScanResult result) : IVolumeChangeScanner
    {
        public string Name { get; } = name;
        public int Calls { get; private set; }

        public VolumeChangeScanResult Scan(
            string normalizedRoot, UsnCheckpoint since, IndexIngestionPolicy policy,
            string excludedStorageRoot, int parallelism, Action<long>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return result;
        }

        public void Dispose() { }
    }

    [Fact]
    public void Fallback_UsesTheFirstStrategyThatWorks()
    {
        var succeeded = new VolumeChangeScanResult(
            true, null, new[] { new UsnChange(Id(1), 0) }, Array.Empty<string>(), 1);
        var elevatedOnly = new StubScanner("MFT sweep", VolumeChangeScanResult.Failed("requires elevation"));
        var universal = new StubScanner("per-file USN", succeeded);

        using var chain = new FallbackVolumeChangeScanner(elevatedOnly, universal);
        VolumeChangeScanResult result = chain.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), Since(), Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, elevatedOnly.Calls);
        Assert.Equal(1, universal.Calls);
    }

    [Fact]
    public void Fallback_ReportsTheLastFailure_WhenNoStrategyWorks()
    {
        var first = new StubScanner("MFT sweep", VolumeChangeScanResult.Failed("requires elevation"));
        var second = new StubScanner("per-file USN", VolumeChangeScanResult.Failed("root was not completely enumerated"));

        using var chain = new FallbackVolumeChangeScanner(first, second);
        VolumeChangeScanResult result = chain.Scan(
            IndexScopeIdentity.NormalizePath(@"C:\root"), Since(), Policy(),
            IndexScopeIdentity.NormalizePath(@"C:\index"), 1, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not completely enumerated", result.Failure, StringComparison.OrdinalIgnoreCase);
    }
}
