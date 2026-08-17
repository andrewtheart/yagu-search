using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Covers the bounded-memory external sort that makes streaming index merges possible: sorted runs
/// bounded by the memory budget, a deterministic stable k-way merge, cancellation, disk-guard aborts,
/// and spool cleanup.
/// </summary>
public sealed class IndexExternalMergeSorterTests : IDisposable
{
    private readonly string _sandbox;

    public IndexExternalMergeSorterTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-merge-sort", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A record whose key is a path and whose payload is an ordinal, so ties are observable.</summary>
    private readonly record struct KeyedRecord(string Key, long Ordinal);

    private sealed class KeyedRecordCodec : IIndexSpoolCodec<KeyedRecord>
    {
        public int MaxPayloadBytes => 4 + IndexCoreFileReaders.MaxPathBytes + 8;

        public int Compare(KeyedRecord x, KeyedRecord y) => string.CompareOrdinal(x.Key, y.Key);

        public int Encode(KeyedRecord record, Span<byte> destination)
        {
            int bytes = Encoding.UTF8.GetBytes(record.Key, destination[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, bytes);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(destination[(4 + bytes)..], record.Ordinal);
            return 4 + bytes + 8;
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out KeyedRecord record)
        {
            record = default;
            if (payload.Length < 12)
                return false;
            int bytes = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (bytes < 0 || payload.Length != 4 + bytes + 8)
                return false;
            record = new KeyedRecord(
                Encoding.UTF8.GetString(payload.Slice(4, bytes)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload[(4 + bytes)..]));
            return true;
        }

        public long EstimateInMemoryBytes(KeyedRecord record) => 40 + (record.Key.Length * 2);
    }

    private sealed class OversizedEncodeCodec : IIndexSpoolCodec<KeyedRecord>
    {
        public int MaxPayloadBytes => 8;
        public int Compare(KeyedRecord left, KeyedRecord right) => left.Ordinal.CompareTo(right.Ordinal);
        public int Encode(KeyedRecord record, Span<byte> destination) => destination.Length + 1;
        public bool TryDecode(ReadOnlySpan<byte> payload, out KeyedRecord record)
        {
            record = default;
            return false;
        }
        public long EstimateInMemoryBytes(KeyedRecord record) => 1;
    }

    private IndexExternalMergeSorter<KeyedRecord> NewSorter(long budget, IndexCompactionDiskGuard? guard = null)
        => new(new KeyedRecordCodec(), Path.Combine(_sandbox, "spool"), budget, guard);

    [Fact]
    public void TinyMemoryBudget_SpillsManyRuns_AndStillSortsEveryRecordExactlyOnce()
    {
        using var sorter = NewSorter(budget: 1);
        var expected = new List<string>();
        for (int i = 0; i < 500; i++)
        {
            string key = $"path-{(i * 37) % 500:D4}";
            expected.Add(key);
            sorter.Add(new KeyedRecord(key, i));
        }

        List<KeyedRecord> sorted = sorter.SortedRecords().ToList();

        Assert.True(sorter.SpilledRunCount > 1, "A one-byte budget must spill more than one run.");
        Assert.Equal(500, sorter.RecordCount);
        expected.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, sorted.Select(r => r.Key).ToList());
    }

    [Fact]
    public void DuplicateKeys_KeepInsertionOrder_AcrossRunBoundaries()
    {
        using var sorter = NewSorter(budget: 1);
        for (int i = 0; i < 20; i++)
            sorter.Add(new KeyedRecord(i % 2 == 0 ? "same" : "other", i));

        List<KeyedRecord> sorted = sorter.SortedRecords().ToList();

        Assert.Equal(
            Enumerable.Range(0, 20).Where(i => i % 2 == 1).Select(i => (long)i).ToList(),
            sorted.Where(r => r.Key == "other").Select(r => r.Ordinal).ToList());
        Assert.Equal(
            Enumerable.Range(0, 20).Where(i => i % 2 == 0).Select(i => (long)i).ToList(),
            sorted.Where(r => r.Key == "same").Select(r => r.Ordinal).ToList());
    }

    [Fact]
    public void LongAndUnicodePaths_RoundTripThroughTheSpool()
    {
        string longKey = @"C:\" + new string('л', 4000) + @"\файл.txt";
        string emoji = @"D:\emoji\🙂🙃\name.txt";
        using var sorter = NewSorter(budget: 1);
        sorter.Add(new KeyedRecord(longKey, 1));
        sorter.Add(new KeyedRecord(emoji, 2));
        sorter.Add(new KeyedRecord(string.Empty, 3));

        List<KeyedRecord> sorted = sorter.SortedRecords().ToList();

        Assert.Equal(3, sorted.Count);
        Assert.Contains(sorted, r => r.Key == longKey);
        Assert.Contains(sorted, r => r.Key == emoji);
        Assert.Contains(sorted, r => r.Key.Length == 0);
    }

    [Fact]
    public void EverythingFittingInMemory_NeverSpills_AndProducesTheSameOrder()
    {
        using var inMemory = NewSorter(budget: 64 * 1024 * 1024);
        using var spilled = NewSorter(budget: 1);
        foreach (int i in new[] { 5, 3, 9, 1, 7 })
        {
            inMemory.Add(new KeyedRecord($"k{i}", i));
            spilled.Add(new KeyedRecord($"k{i}", i));
        }

        List<KeyedRecord> a = inMemory.SortedRecords().ToList();
        List<KeyedRecord> b = spilled.SortedRecords().ToList();

        Assert.Equal(0, inMemory.SpilledRunCount);
        Assert.Equal(a, b);
    }

    [Fact]
    public void EmptyAndSingleRecordSorts_DoNotSpill()
    {
        using var empty = NewSorter(budget: 1024);
        Assert.Empty(empty.SortedRecords());
        Assert.Equal(0, empty.SpilledRunCount);

        using var single = NewSorter(budget: 1024);
        single.Add(new KeyedRecord("only", 1));
        Assert.Equal([new KeyedRecord("only", 1)], single.SortedRecords().ToList());
        Assert.Equal(0, single.SpilledRunCount);
    }

    [Fact]
    public void SpilledRuns_MergeWithABufferedTail()
    {
        using var sorter = NewSorter(budget: 80);
        sorter.Add(new KeyedRecord("c", 1));
        sorter.Add(new KeyedRecord("a", 2));
        Assert.Equal(1, sorter.SpilledRunCount);
        sorter.Add(new KeyedRecord("b", 3));

        Assert.Equal(new[] { "a", "b", "c" }, sorter.SortedRecords().Select(record => record.Key));
        Assert.Equal(2, sorter.SpilledRunCount);
    }

    [Fact]
    public void Cancellation_StopsAddAndEnumeration()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        using var sorter = NewSorter(budget: 1);
        sorter.Add(new KeyedRecord("a", 1));
        sorter.Add(new KeyedRecord("b", 2));
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => sorter.Add(new KeyedRecord("c", 3), cts.Token));
        Assert.Throws<OperationCanceledException>(() => sorter.SortedRecords(cts.Token).ToList());
    }

    [Fact]
    public void DiskGuard_AbortsTheSpill_AndDisposeLeavesNoSpoolFiles()
    {
        // A volume with no free space at all: the first spool write must be refused.
        var guard = new IndexCompactionDiskGuard(
            _sandbox,
            minimumFreeSpaceMB: 1,
            maxDiskUsagePercent: 0,
            probe: _ => new IndexVolumeSpace("X:\\", 1000, 0));

        var sorter = NewSorter(budget: 1, guard);
        Assert.Throws<IndexCompactionDiskGuardException>(() =>
        {
            for (int i = 0; i < 100; i++)
                sorter.Add(new KeyedRecord($"k{i}", i));
        });

        sorter.Dispose();
        string spool = Path.Combine(_sandbox, "spool");
        Assert.True(!Directory.Exists(spool) || Directory.GetFiles(spool, "*.spool").Length == 0);
    }

    [Fact]
    public void DiskGuard_WithinLimits_AllowsTheMergeAndCountsCreatedBytes()
    {
        var guard = new IndexCompactionDiskGuard(
            _sandbox,
            minimumFreeSpaceMB: 1,
            maxDiskUsagePercent: 99,
            probe: _ => new IndexVolumeSpace("X:\\", 100L * 1024 * 1024 * 1024, 50L * 1024 * 1024 * 1024));

        using var sorter = NewSorter(budget: 1, guard);
        for (int i = 0; i < 50; i++)
            sorter.Add(new KeyedRecord($"k{i:D3}", i));

        Assert.Equal(50, sorter.SortedRecords().Count());
        Assert.True(guard.BytesCreated > 0);
    }

    [Fact]
    public void UnreadableVolume_FailsOpen_SoMaintenanceIsNeverBlockedByAnUnknownDrive()
    {
        var guard = new IndexCompactionDiskGuard(_sandbox, 1024, 50, probe: _ => null);
        guard.EnsureHeadroomFor(long.MaxValue / 4);
        Assert.Equal(0, guard.BytesCreated);
    }

    [Fact]
    public void DisabledLimits_SkipProbingEntirely()
    {
        var guard = new IndexCompactionDiskGuard(_sandbox, 0, 0, probe: _ => throw new InvalidOperationException("probed"));
        Assert.True(guard.IsDisabled);
        guard.EnsureHeadroomFor(1024);
    }

    [Fact]
    public void DiskGuard_ReprobesBeforeAbort_AndFailsOpenWhenTheVolumeChangesOrBecomesUnreadable()
    {
        int recoveredProbeCalls = 0;
        var recovered = new IndexCompactionDiskGuard(
            _sandbox,
            minimumFreeSpaceMB: 1,
            maxDiskUsagePercent: 0,
            probe: _ => ++recoveredProbeCalls == 1
                ? new IndexVolumeSpace("X:\\", 1000, 0)
                : new IndexVolumeSpace("X:\\", 100L * 1024 * 1024, 100L * 1024 * 1024));

        recovered.EnsureHeadroomFor(1);
        Assert.Equal(2, recoveredProbeCalls);

        int unreadableProbeCalls = 0;
        var unreadable = new IndexCompactionDiskGuard(
            _sandbox,
            minimumFreeSpaceMB: 1,
            maxDiskUsagePercent: 0,
            probe: _ => ++unreadableProbeCalls == 1
                ? new IndexVolumeSpace("X:\\", 1000, 0)
                : null);

        unreadable.EnsureHeadroomFor(1);
        Assert.Equal(2, unreadableProbeCalls);
    }

    [Fact]
    public void DiskGuard_PercentLimitReportsTheVolumeAndVolumeSpaceCalculatesUsage()
    {
        var guard = new IndexCompactionDiskGuard(
            _sandbox,
            minimumFreeSpaceMB: 0,
            maxDiskUsagePercent: 50,
            probe: _ => new IndexVolumeSpace("X:\\", 1000, 600));

        IndexCompactionDiskGuardException error = Assert.Throws<IndexCompactionDiskGuardException>(
            () => guard.EnsureHeadroomFor(101));

        Assert.Equal("X:\\", error.DriveName);
        Assert.Contains("50% full limit", error.Message);
        Assert.Equal(40, new IndexVolumeSpace("X:\\", 1000, 600).UsedPercent);
        Assert.Equal(0, new IndexVolumeSpace("X:\\", 0, 0).UsedPercent);
        Assert.Null(IndexCompactionDiskGuard.ProbeVolume("\0"));
    }

    [Fact]
    public void DiskGuardedStream_ChargesWrites_AndRejectsUnsupportedOperations()
    {
        var guard = new IndexCompactionDiskGuard(
            _sandbox,
            minimumFreeSpaceMB: 0,
            maxDiskUsagePercent: 99,
            probe: _ => new IndexVolumeSpace("X:\\", 1024 * 1024, 1024 * 1024));
        using var inner = new MemoryStream();
        using var stream = new DiskGuardedStream(inner, guard);

        stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
        stream.WriteByte(4);
        stream.Flush();
        guard.RecordCreated(0);
        guard.RecordCreated(-1);
        guard.EnsureHeadroomFor(-1);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, inner.ToArray());
        Assert.Equal(4, guard.BytesCreated);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void SpoolWriterAndReader_RoundTripAndRejectInvalidFraming()
    {
        string validPath = Path.Combine(_sandbox, "valid.spool");
        var codec = new KeyedRecordCodec();
        using (var writer = new IndexSpoolWriter<KeyedRecord>(validPath, codec))
        {
            writer.Write(new KeyedRecord("a", 1));
            writer.Write(new KeyedRecord("b", 2));
            Assert.Equal(2, writer.Count);
        }

        using (var reader = new IndexSpoolReader<KeyedRecord>(validPath, codec))
        {
            Assert.True(reader.TryReadNext(out KeyedRecord first));
            Assert.True(reader.TryReadNext(out KeyedRecord second));
            Assert.False(reader.TryReadNext(out _));
            Assert.Equal(new KeyedRecord("a", 1), first);
            Assert.Equal(new KeyedRecord("b", 2), second);
        }

        string invalidWriterPath = Path.Combine(_sandbox, "invalid-writer.spool");
        using (var writer = new IndexSpoolWriter<KeyedRecord>(invalidWriterPath, new OversizedEncodeCodec()))
        {
            Assert.Throws<InvalidDataException>(() => writer.Write(new KeyedRecord("a", 1)));
        }

        var malformedFiles = new Dictionary<string, byte[]>
        {
            ["short-header.spool"] = [1, 2],
            ["oversized.spool"] = BitConverter.GetBytes(int.MaxValue),
            ["short-payload.spool"] = [12, 0, 0, 0, 1, 2],
            ["rejected-payload.spool"] = [12, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        };
        foreach ((string name, byte[] bytes) in malformedFiles)
        {
            string path = Path.Combine(_sandbox, name);
            File.WriteAllBytes(path, bytes);
            using var reader = new IndexSpoolReader<KeyedRecord>(path, codec);
            Assert.Throws<InvalidDataException>(() => reader.TryReadNext(out _));
        }
    }

    [Fact]
    public void Sorter_DeletesFailedSpills_AndRejectsCorruptRuns()
    {
        string invalidSpoolDirectory = Path.Combine(_sandbox, "invalid-spool");
        using (var invalid = new IndexExternalMergeSorter<KeyedRecord>(
            new OversizedEncodeCodec(), invalidSpoolDirectory, memoryBudgetBytes: 1))
        {
            Assert.Throws<InvalidDataException>(() => invalid.Add(new KeyedRecord("a", 1)));
            Assert.Empty(Directory.GetFiles(invalidSpoolDirectory, "*.spool"));
        }

        using var corrupt = NewSorter(budget: 1);
        corrupt.Add(new KeyedRecord("a", 1));
        string runPath = Assert.Single(Directory.GetFiles(Path.Combine(_sandbox, "spool"), "*.spool"));
        using (var stream = new FileStream(runPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Write(BitConverter.GetBytes(int.MaxValue));
        }
        Assert.Throws<InvalidDataException>(() => corrupt.SortedRecords().ToList());
    }

    [Theory]
    [InlineData("short-header")]
    [InlineData("short-payload")]
    [InlineData("rejected-payload")]
    public void Sorter_RunReaderRejectsEveryMalformedFrame(string defect)
    {
        string spoolDirectory = Path.Combine(_sandbox, "corrupt-" + defect);
        using var sorter = new IndexExternalMergeSorter<KeyedRecord>(
            new KeyedRecordCodec(), spoolDirectory, memoryBudgetBytes: 1);
        sorter.Add(new KeyedRecord("a", 1));
        string runPath = Assert.Single(Directory.GetFiles(spoolDirectory, "*.spool"));

        byte[] bytes = defect switch
        {
            "short-header" => [1, 2],
            "short-payload" => [12, 0, 0, 0, 1, 2],
            "rejected-payload" => [12, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            _ => throw new InvalidOperationException($"Unknown defect '{defect}'."),
        };
        File.WriteAllBytes(runPath, bytes);

        Assert.Throws<InvalidDataException>(() => sorter.SortedRecords().ToList());
    }

    [Fact]
    public void SortedRecords_CanOnlyBeRequestedOnce_AndAddIsRejectedAfterwards()
    {
        using var sorter = NewSorter(budget: 64 * 1024);
        sorter.Add(new KeyedRecord("a", 1));
        Assert.Single(sorter.SortedRecords().ToList());
        Assert.Throws<InvalidOperationException>(() => sorter.Add(new KeyedRecord("b", 2)));
        Assert.Throws<InvalidOperationException>(() => sorter.SortedRecords());
    }

    [Fact]
    public void DisposedSorter_RejectsFurtherUse()
    {
        var sorter = NewSorter(budget: 64 * 1024);
        sorter.Dispose();
        sorter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sorter.Add(new KeyedRecord("a", 1)));
        Assert.Throws<ObjectDisposedException>(() => sorter.SortedRecords());
    }

    [Fact]
    public void Dispose_BestEffortCleanupDoesNotThrowWhenARunIsLocked()
    {
        string spoolDirectory = Path.Combine(_sandbox, "locked-spool");
        var sorter = new IndexExternalMergeSorter<KeyedRecord>(
            new KeyedRecordCodec(), spoolDirectory, memoryBudgetBytes: 1);
        sorter.Add(new KeyedRecord("a", 1));
        string runPath = Assert.Single(Directory.GetFiles(spoolDirectory, "*.spool"));

        using (var held = new FileStream(runPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            sorter.Dispose();
            Assert.True(File.Exists(runPath));
        }

        File.Delete(runPath);
        Assert.False(File.Exists(runPath));
    }

    [Fact]
    public void Workspace_IsPrivateToTheIndexVolume_AndIsRemovedOnDispose()
    {
        string root;
        using (IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox))
        {
            root = workspace.Root;
            Assert.StartsWith(".compact-", Path.GetFileName(root), StringComparison.Ordinal);
            Assert.Equal(_sandbox, Path.GetDirectoryName(root));
            Assert.True(Directory.Exists(workspace.SpoolDirectory));
            Assert.True(Directory.Exists(workspace.PreparedDirectory));
            File.WriteAllText(Path.Combine(workspace.SpoolDirectory, "residue.tmp"), "x");
        }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Workspace_DisposeIsIdempotent_AndLockedResidueCanBeRetried()
    {
        IndexCompactionWorkspace workspace = IndexCompactionWorkspace.Create(_sandbox);
        string root = workspace.Root;
        string heldPath = Path.Combine(workspace.SpoolDirectory, "held.bin");
        using (var held = new FileStream(heldPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            workspace.Dispose();
            Assert.True(Directory.Exists(root));
            workspace.Dispose();
        }

        IndexCompactionWorkspace.TryDelete(root);
        Assert.False(Directory.Exists(root));
    }
}
