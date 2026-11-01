using System;
using System.IO;
using System.Linq;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Covers the volume probe that feeds <see cref="IndexCompactionDiskGuard"/>. Every arm here decides
/// whether a merge is allowed to keep writing, so a probe that invents a free-space figure instead of
/// declining is how an index merge fills the user's disk.
/// </summary>
public class IndexCompactionDiskGuardTests
{
    [Fact]
    public void APathWithNoVolumeRoot_CannotBeMeasured()
    {
        // No root means there is no volume whose free space we could compare against the limits.
        // Returning a snapshot anyway would hand the guard totals it never actually read.
        Assert.Null(IndexCompactionDiskGuard.ProbeVolume(@"C:\index", static _ => string.Empty));
        Assert.Null(IndexCompactionDiskGuard.ProbeVolume(@"C:\index", static _ => null));
    }

    [Fact]
    public void AVolumeThatIsNotReady_CannotBeMeasured()
    {
        // An unmounted or removed drive answers IsReady=false and throws on TotalSize. Probing it has
        // to decline rather than propagate, otherwise an index configured onto a detached drive turns
        // every maintenance run into a crash instead of a skipped compaction.
        string? unusedRoot = FindUnusedDriveRoot();
        if (unusedRoot is null)
            return; // every drive letter is in use on this host; nothing to assert against

        Assert.Null(IndexCompactionDiskGuard.ProbeVolume(unusedRoot, static p => p));
    }

    [Fact]
    public void AnUnparseableVolumeRoot_IsDeclinedRatherThanThrown()
    {
        Assert.Null(IndexCompactionDiskGuard.ProbeVolume(
            "??", static _ => throw new ArgumentException("bad root")));
        Assert.Null(IndexCompactionDiskGuard.ProbeVolume(
            @"\\server\share", static _ => throw new NotSupportedException("unsupported")));
    }

    [Fact]
    public void ARealVolume_ReportsItsOwnTotalsNotFabricatedOnes()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        IndexVolumeSpace? space = IndexCompactionDiskGuard.ProbeVolume(Path.GetTempPath());

        Assert.NotNull(space);
        var drive = new DriveInfo(root);
        Assert.Equal(drive.Name, space!.Value.DriveName);
        Assert.Equal(drive.TotalSize, space.Value.TotalBytes);
        Assert.True(space.Value.AvailableBytes > 0);
        Assert.True(space.Value.AvailableBytes <= space.Value.TotalBytes);
    }

    private static string? FindUnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
                return letter + @":\";
        }
        return null;
    }
}
