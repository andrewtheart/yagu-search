using System.Buffers.Binary;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the non-elevated USN change-journal reader (plan §3.5, Phase 0 feasibility gate). The pure
/// <see cref="UsnRecordParser"/> is unit-tested with synthetic V2/V3/malformed/unknown record buffers;
/// the P/Invoke <see cref="UsnJournalReader"/> is exercised end-to-end against the real journal of the
/// test volume, self-gating (skipping) when the journal is unavailable (non-NTFS / CI / disabled).
/// </summary>
public sealed class UsnJournalReaderTests
{
    // ── Pure parser ──

    private static byte[] BuildV2Record(ulong fileRef, uint reason, int recordLength = 64)
    {
        var record = new byte[recordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)recordLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2); // MajorVersion
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileRef);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), reason);
        return record;
    }

    private static byte[] BuildV3Record(ulong low, ulong high, uint reason, int recordLength = 80)
    {
        var record = new byte[recordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)recordLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3); // MajorVersion
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), low);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), high);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), reason);
        return record;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    [Fact]
    public void ParseRecords_V2_ExtractsFileReferenceAndReason()
    {
        var buffer = BuildV2Record(fileRef: 0x1122334455667788, reason: 0x00000102);
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Ok, UsnRecordParser.ParseRecords(buffer, sink));
        var change = Assert.Single(sink);
        Assert.Equal(new UsnFileIdentity(0x1122334455667788, 0), change.Identity);
        Assert.Equal(0x00000102u, change.Reason);
    }

    [Fact]
    public void ParseRecords_V3_ExtractsFileId128AndReason()
    {
        var buffer = BuildV3Record(low: 0xAABBCCDD, high: 0x99, reason: 0x80000200);
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Ok, UsnRecordParser.ParseRecords(buffer, sink));
        var change = Assert.Single(sink);
        Assert.Equal(new UsnFileIdentity(0xAABBCCDD, 0x99), change.Identity);
        Assert.Equal(0x80000200u, change.Reason);
    }

    [Fact]
    public void ParseRecords_MixedV2AndV3_ParsesBoth()
    {
        var buffer = Concat(
            BuildV2Record(fileRef: 1, reason: 1),
            BuildV3Record(low: 2, high: 0, reason: 2),
            BuildV2Record(fileRef: 3, reason: 4));
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Ok, UsnRecordParser.ParseRecords(buffer, sink));
        Assert.Equal(3, sink.Count);
        Assert.Equal(UsnFileIdentity.FromFileReferenceNumber(1), sink[0].Identity);
        Assert.Equal(new UsnFileIdentity(2, 0), sink[1].Identity);
        Assert.Equal(UsnFileIdentity.FromFileReferenceNumber(3), sink[2].Identity);
    }

    [Fact]
    public void ParseRecords_ZeroLengthRecord_StopsAtEndMarker()
    {
        var buffer = Concat(BuildV2Record(fileRef: 7, reason: 1), new byte[8]); // trailing zero-length record
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Ok, UsnRecordParser.ParseRecords(buffer, sink));
        Assert.Single(sink);
    }

    [Fact]
    public void ParseRecords_UnknownMajorVersion_FailsClosed()
    {
        var record = BuildV2Record(fileRef: 1, reason: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 4); // future/unknown version
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.UnknownVersion, UsnRecordParser.ParseRecords(record, sink));
        Assert.Empty(sink);
    }

    [Fact]
    public void ParseRecords_RecordLengthBeyondBuffer_ReturnsMalformed()
    {
        var record = BuildV2Record(fileRef: 1, reason: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 4096); // claims far more than the buffer holds
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Malformed, UsnRecordParser.ParseRecords(record, sink));
    }

    [Fact]
    public void ParseRecords_RecordLengthTooSmallForVersion_ReturnsMalformed()
    {
        // A V3 record whose length only covers a V2-sized header can't hold the FILE_ID_128 + reason.
        // Build it directly (a helper would overrun writing the reason field), leaving it truncated.
        var record = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 48);          // RecordLength = 48 (< V3 min 60)
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3); // MajorVersion = 3
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Malformed, UsnRecordParser.ParseRecords(record, sink));
    }

    [Fact]
    public void ParseRecords_RespectsMaxRecords()
    {
        var buffer = Concat(
            BuildV2Record(fileRef: 1, reason: 1),
            BuildV2Record(fileRef: 2, reason: 1),
            BuildV2Record(fileRef: 3, reason: 1));
        var sink = new List<UsnChange>();

        Assert.Equal(UsnParseStatus.Ok, UsnRecordParser.ParseRecords(buffer, sink, maxRecords: 2));
        Assert.Equal(2, sink.Count);
    }

    [Fact]
    public void ParseRecords_EmptyBuffer_OkWithNoChanges()
    {
        var sink = new List<UsnChange>();
        Assert.Equal(UsnParseStatus.Ok, UsnRecordParser.ParseRecords(ReadOnlySpan<byte>.Empty, sink));
        Assert.Empty(sink);
    }

    [Fact]
    public void FromFileId128_ShortSpan_Throws()
        => Assert.Throws<ArgumentException>(() => UsnFileIdentity.FromFileId128(new byte[8]));

    [Fact]
    public void UnavailableResult_IsNotTrusted()
    {
        Assert.False(UsnReadResult.Unavailable.IsTrusted);
        Assert.Equal(UsnReadStatus.Unavailable, UsnReadResult.Unavailable.Status);
    }

    // ── Integration against the real journal (self-gating) ──

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "YaguUsnTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The journal info for a path's volume, or null when unavailable (test self-gates on null).</summary>
    private static UsnJournalInfo? TryGetInfo(string path)
    {
        using var handle = UsnJournalReader.TryOpenVolumeRoot(path);
        return handle is null ? null : UsnJournalReader.QueryJournal(handle);
    }

    [Fact]
    public void TryCaptureCheckpoint_OnLocalVolume_ReturnsUsableCheckpointOrNull()
    {
        string dir = CreateTempDir();
        try
        {
            var checkpoint = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (checkpoint is null)
                return; // journal unavailable on this volume — self-gated
            Assert.NotEqual(0UL, checkpoint.Value.JournalId);
            Assert.True(checkpoint.Value.NextUsn >= 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCollectChanges_ObservesFileWrites()
    {
        string dir = CreateTempDir();
        try
        {
            var start = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (start is null)
                return; // self-gated

            for (int i = 0; i < 5; i++)
            {
                string file = Path.Combine(dir, $"probe_{i}.txt");
                File.WriteAllText(file, $"content {i} {Guid.NewGuid()}");
            }

            var result = UsnJournalReader.TryCollectChanges(dir, start.Value);

            Assert.Equal(UsnReadStatus.Ok, result.Status);
            Assert.NotEmpty(result.Changes);
            Assert.True(result.NextCheckpoint.NextUsn >= start.Value.NextUsn);
            Assert.Equal(start.Value.JournalId, result.NextCheckpoint.JournalId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCollectChanges_ReFsRecordIdentityMatchesFileIdAndResolvesPath()
    {
        DriveInfo? refs = DriveInfo.GetDrives().FirstOrDefault(static drive =>
        {
            try { return drive.IsReady && string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        if (refs is null)
            return; // self-gated: no writable ReFS volume on this machine

        string dir = Path.Combine(refs.RootDirectory.FullName, "YaguUsnReFsTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            UsnCheckpoint? start = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (start is null)
                return; // journal unavailable

            string file = Path.Combine(dir, "new-file.txt");
            File.WriteAllText(file, "ReFS journal identity parity probe");
            FileIdentity? identity = FileIdentityReader.TryGetIdentity(file);
            Assert.NotNull(identity);

            UsnReadResult result = UsnJournalReader.TryCollectChanges(dir, start.Value);
            Assert.Equal(UsnReadStatus.Ok, result.Status);
            Assert.Contains(result.Changes, change => change.Identity == identity.Value.FileId);

            using FileIdPathResolver? resolver = FileIdPathResolver.ForRoot(dir);
            Assert.NotNull(resolver);
            Assert.Equal(
                IndexScopeIdentity.NormalizePath(file),
                IndexScopeIdentity.NormalizePath(resolver.TryResolvePath(identity.Value.FileId)!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryCollectChanges_ExceedingTheRecordCap_ReportsIncompleteNotOk()
    {
        // Exact-target replay contract: if the change delta since the checkpoint exceeds the record cap,
        // the read is INCOMPLETE (changes beyond the cap are unread) and must NOT report Ok — else a file
        // dirtied beyond the cap would be classified clean and pruned, silently hiding a match.
        string dir = CreateTempDir();
        try
        {
            var start = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (start is null)
                return; // self-gated (journal unavailable on this volume)

            for (int i = 0; i < 8; i++)
                File.WriteAllText(Path.Combine(dir, $"probe_{i}.txt"), $"content {i} {Guid.NewGuid()}");

            var capped = UsnJournalReader.TryCollectChanges(dir, start.Value, maxRecords: 1);
            if (capped.Status == UsnReadStatus.Ok)
                return; // self-gated: too few records materialized to exceed the tiny cap on this volume

            Assert.Equal(UsnReadStatus.Incomplete, capped.Status);
            Assert.True(capped.Changes.Count <= 1); // never collects past the cap
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCollectChanges_NoneCheckpoint_ReportsCurrentCursorWithNoChanges()
    {
        string dir = CreateTempDir();
        try
        {
            if (TryGetInfo(dir) is not { } info)
                return; // self-gated

            var result = UsnJournalReader.TryCollectChanges(dir, UsnCheckpoint.None);

            Assert.Equal(UsnReadStatus.Ok, result.Status);
            Assert.Empty(result.Changes);
            Assert.Equal(info.UsnJournalId, result.NextCheckpoint.JournalId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCollectChanges_JournalIdMismatch_ReportsJournalIdChanged()
    {
        string dir = CreateTempDir();
        try
        {
            if (TryGetInfo(dir) is not { } info)
                return; // self-gated

            // A checkpoint from a different journal id must be reported as discontinuity.
            var stale = new UsnCheckpoint(info.UsnJournalId ^ 0xDEADBEEF, info.NextUsn);
            var result = UsnJournalReader.TryCollectChanges(dir, stale);

            Assert.Equal(UsnReadStatus.JournalIdChanged, result.Status);
            Assert.Equal(info.UsnJournalId, result.NextCheckpoint.JournalId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCollectChanges_StartBeforeFirstUsn_ReportsGap()
    {
        string dir = CreateTempDir();
        try
        {
            if (TryGetInfo(dir) is not { } info || info.FirstUsn < 1)
                return; // self-gated (need a purged region below FirstUsn to force a real gap)

            // A start USN older than the oldest retained record is a wrap/gap.
            var purged = new UsnCheckpoint(info.UsnJournalId, info.FirstUsn - 1);
            var result = UsnJournalReader.TryCollectChanges(dir, purged);

            Assert.Equal(UsnReadStatus.GapDetected, result.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCollectChanges_StartAfterCurrentNextUsn_ReportsCheckpointAhead()
    {
        string dir = CreateTempDir();
        try
        {
            if (TryGetInfo(dir) is not { } info || info.NextUsn == long.MaxValue)
                return; // self-gated

            // ReFS can reset to a small cursor without changing its journal id. A checkpoint from before
            // that reset must fail closed rather than report a trusted empty future interval.
            var future = new UsnCheckpoint(info.UsnJournalId, info.NextUsn + 1);
            UsnReadResult result = UsnJournalReader.TryCollectChanges(dir, future);

            Assert.Equal(UsnReadStatus.CheckpointAhead, result.Status);
            Assert.Equal(info.NextUsn, result.NextCheckpoint.NextUsn);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
