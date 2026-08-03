using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="FileIdentityReader"/> and <see cref="FileIdMap"/> (plan §3.5/§3.6): the bridge
/// that turns name-less USN change records into dirty content ids. The map is unit-tested; the identity
/// reader and the end-to-end freshness path are integration-tested against real files + the real journal,
/// self-gating when unavailable (non-NTFS / CI / journal off). The pivotal test proves that the identity
/// captured at build time equals the identity the USN journal reports for the same file.
/// </summary>
public sealed class FileIdentityAndMapTests
{
    // ── FileIdMap (pure) ──

    [Fact]
    public void ResolveDirty_MarksOnlyMatchingContentIds()
    {
        var map = new FileIdMap(volumeSerialNumber: 0x1234);
        var a = new UsnFileIdentity(10, 0);
        var b = new UsnFileIdentity(20, 0);
        var c = new UsnFileIdentity(30, 0);
        map.Add(100, a);
        map.Add(200, b);
        map.Add(300, c);

        var dirty = new DirtyContentSet();
        int resolved = map.ResolveDirty(new[] { new UsnChange(a, 1), new UsnChange(c, 2) }, dirty);

        Assert.Equal(2, resolved);
        Assert.True(dirty.IsDirty(100));
        Assert.False(dirty.IsDirty(200));
        Assert.True(dirty.IsDirty(300));
    }

    [Fact]
    public void ResolveDirty_IgnoresUnknownIdentities()
    {
        var map = new FileIdMap(0);
        map.Add(1, new UsnFileIdentity(5, 0));
        var dirty = new DirtyContentSet();

        int resolved = map.ResolveDirty(new[] { new UsnChange(new UsnFileIdentity(999, 0), 1) }, dirty);

        Assert.Equal(0, resolved);
        Assert.Equal(0, dirty.Count);
    }

    [Fact]
    public void Add_HardLinkAlias_SharesOneContentIdIdempotently()
    {
        var map = new FileIdMap(0);
        var id = new UsnFileIdentity(7, 0);
        map.Add(42, id);
        map.Add(42, id); // second alias of the same content object

        Assert.Equal(1, map.Count);
        Assert.True(map.TryGetContentId(id, out long contentId));
        Assert.Equal(42, contentId);
    }

    [Fact]
    public void TryGetContentId_UnknownIdentity_ReturnsFalse()
    {
        var map = new FileIdMap(0);
        Assert.False(map.TryGetContentId(new UsnFileIdentity(1, 2), out _));
    }

    [Fact]
    public void HasExtendedIdentities_TracksLegacyHighHalf_IncludingMergedLayers()
    {
        var current = new FileIdMap(0);
        current.Add(1, new UsnFileIdentity(100, 0));
        Assert.False(current.HasExtendedIdentities);

        var legacy = new FileIdMap(0);
        legacy.Add(2, new UsnFileIdentity(200, 0x600));
        long nextContentId = 10;
        current.MergeIdentitiesFrom(legacy, ref nextContentId);

        Assert.True(current.HasExtendedIdentities);
    }

    // ── Integration against real files + journal (self-gating) ──

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "YaguFileIdTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TryGetIdentity_RealFiles_AreNonZeroAndDistinct()
    {
        string dir = CreateTempDir();
        try
        {
            string fileA = Path.Combine(dir, "a.txt");
            string fileB = Path.Combine(dir, "b.txt");
            File.WriteAllText(fileA, "aaa");
            File.WriteAllText(fileB, "bbb");

            var idA = FileIdentityReader.TryGetIdentity(fileA);
            var idB = FileIdentityReader.TryGetIdentity(fileB);
            if (idA is null || idB is null)
                return; // self-gated (identity unavailable on this volume)

            Assert.NotEqual(default, idA.Value.FileId);
            Assert.NotEqual(idA.Value.FileId, idB.Value.FileId);
            Assert.Equal(idA.Value.VolumeSerialNumber, idB.Value.VolumeSerialNumber);

            // Identity is stable across re-reads.
            Assert.Equal(idA, FileIdentityReader.TryGetIdentity(fileA));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryGetIdentity_MissingFile_ReturnsNull()
    {
        string dir = CreateTempDir();
        try
        {
            Assert.Null(FileIdentityReader.TryGetIdentity(Path.Combine(dir, "does-not-exist.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryGetIdentity_PathCore_ValidatesFailsClosedAndDisposesHandles()
    {
        int openCalls = 0;
        Assert.Null(FileIdentityReader.TryGetIdentity(
            " ",
            _ => { openCalls++; return new SafeFileHandle(IntPtr.Zero, ownsHandle: false); },
            _ => throw new InvalidOperationException()));
        Assert.Equal(0, openCalls);

        var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Null(FileIdentityReader.TryGetIdentity("missing", _ => invalid, _ => throw new InvalidOperationException()));
        Assert.True(invalid.IsClosed);

        var expected = new FileIdentity(7, new UsnFileIdentity(11, 0));
        var valid = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Assert.Equal(expected, FileIdentityReader.TryGetIdentity("file", _ => valid, _ => expected));
        Assert.True(valid.IsClosed);

        Assert.Null(FileIdentityReader.TryGetIdentity(
            "file",
            _ => throw new IOException("open failed"),
            _ => expected));

        var readFailure = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Assert.Null(FileIdentityReader.TryGetIdentity(
            "file",
            _ => readFailure,
            _ => throw new IOException("read failed")));
        Assert.True(readFailure.IsClosed);
    }

    [Fact]
    public void TryGetIdentity_HandleCore_ValidatesAndFailsClosed()
    {
        static bool UnexpectedIdInfo(SafeFileHandle _, byte[] __) => throw new InvalidOperationException();
        static bool UnexpectedLegacy(
            SafeFileHandle _,
            out FileIdentityReader.BY_HANDLE_FILE_INFORMATION information)
        {
            information = default;
            throw new InvalidOperationException();
        }

        Assert.Null(FileIdentityReader.TryGetIdentity(null!, UnexpectedIdInfo, UnexpectedLegacy));
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Null(FileIdentityReader.TryGetIdentity(invalid, UnexpectedIdInfo, UnexpectedLegacy));
        var closed = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        closed.Dispose();
        Assert.Null(FileIdentityReader.TryGetIdentity(closed, UnexpectedIdInfo, UnexpectedLegacy));

        using var handle = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Assert.Null(FileIdentityReader.TryGetIdentity(handle, (_, _) => false, UnexpectedLegacy));
        Assert.Null(FileIdentityReader.TryGetIdentity(
            handle,
            (_, buffer) => { BinaryPrimitives.WriteUInt64LittleEndian(buffer, 17); return true; },
            (SafeFileHandle _, out FileIdentityReader.BY_HANDLE_FILE_INFORMATION information) =>
            {
                information = default;
                return false;
            }));
        Assert.Null(FileIdentityReader.TryGetIdentity(
            handle,
            (_, _) => throw new IOException("native failure"),
            UnexpectedLegacy));
    }

    [Fact]
    public void TryGetIdentity_HandleCore_AssemblesVolumeAndFileReference()
    {
        using var handle = new SafeFileHandle(new IntPtr(1), ownsHandle: false);

        FileIdentity? identity = FileIdentityReader.TryGetIdentity(
            handle,
            (_, buffer) => { BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0x1122334455667788); return true; },
            (SafeFileHandle _, out FileIdentityReader.BY_HANDLE_FILE_INFORMATION information) =>
            {
                information = new FileIdentityReader.BY_HANDLE_FILE_INFORMATION
                {
                    FileIndexHigh = 0xAABBCCDD,
                    FileIndexLow = 0x12345678,
                };
                return true;
            });

        Assert.Equal(new FileIdentity(
            0x1122334455667788,
            UsnFileIdentity.FromFileReferenceNumber(0xAABBCCDD12345678)), identity);
    }

    /// <summary>
    /// The pivotal correctness fact for freshness (plan §3.5/§3.6): the identity captured at build time
    /// via <see cref="FileIdentityReader"/> is exactly the identity the USN journal reports for the same
    /// file, so an index keyed by these identities can be dirtied precisely by the change journal.
    /// </summary>
    [Fact]
    public void CapturedFileIdentity_EqualsUsnRecordIdentity()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "probe.txt");
            File.WriteAllText(file, "seed");

            var identity = FileIdentityReader.TryGetIdentity(file);
            var start = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (identity is null || start is null)
                return; // self-gated

            File.AppendAllText(file, " changed");

            var result = UsnJournalReader.TryCollectChanges(dir, start.Value);
            if (result.Status != UsnReadStatus.Ok)
                return; // tolerate transient journal states in CI-like environments

            Assert.Contains(result.Changes, change => change.Identity == identity.Value.FileId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>End-to-end: build a fileid map from real identities, change one file, and confirm the
    /// journal + map dirty exactly that file's content id.</summary>
    [Fact]
    public void FileIdMap_ResolvesRealJournalChangesToContentIds()
    {
        string dir = CreateTempDir();
        try
        {
            string fileA = Path.Combine(dir, "a.txt");
            string fileB = Path.Combine(dir, "b.txt");
            File.WriteAllText(fileA, "aaa");
            File.WriteAllText(fileB, "bbb");

            var idA = FileIdentityReader.TryGetIdentity(fileA);
            var idB = FileIdentityReader.TryGetIdentity(fileB);
            var start = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (idA is null || idB is null || start is null)
                return; // self-gated

            var map = new FileIdMap(idA.Value.VolumeSerialNumber);
            map.Add(contentId: 1, idA.Value.FileId);
            map.Add(contentId: 2, idB.Value.FileId);

            // Modify only file A.
            File.AppendAllText(fileA, " changed");

            var result = UsnJournalReader.TryCollectChanges(dir, start.Value);
            if (result.Status != UsnReadStatus.Ok)
                return; // tolerate transient journal states

            var dirty = new DirtyContentSet();
            map.ResolveDirty(result.Changes, dirty);

            Assert.True(dirty.IsDirty(1));  // A changed
            Assert.False(dirty.IsDirty(2)); // B untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Handle-based identity (plan §5.4): same-handle == path == USN record ──

    [Fact]
    public void TryGetIdentity_FromOpenHandle_EqualsPathIdentity_AndUsnRecordIdentity()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "probe.txt");
            File.WriteAllText(file, "seed");

            FileIdentity? pathIdentity = FileIdentityReader.TryGetIdentity(file);
            if (pathIdentity is null)
                return; // self-gated

            // Reading identity from an already-open content handle (as the build reader does) yields the
            // same durable identity — no second CreateFileW is needed.
            FileIdentity? handleIdentity;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                handleIdentity = FileIdentityReader.TryGetIdentity(stream.SafeFileHandle);

            Assert.Equal(pathIdentity, handleIdentity);

            // And it is exactly the identity the USN journal reports for the same file.
            var start = UsnJournalReader.TryCaptureCheckpoint(dir);
            if (start is null)
                return;
            File.AppendAllText(file, " changed");
            var result = UsnJournalReader.TryCollectChanges(dir, start.Value);
            if (result.Status != UsnReadStatus.Ok)
                return;
            Assert.Contains(result.Changes, c => c.Identity == handleIdentity!.Value.FileId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryGetIdentity_InvalidOrNullHandle_ReturnsNull()
    {
        Assert.Null(FileIdentityReader.TryGetIdentity((SafeFileHandle)null!));
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Null(FileIdentityReader.TryGetIdentity(invalid));
    }
}
