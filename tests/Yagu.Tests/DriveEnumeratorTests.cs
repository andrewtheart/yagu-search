using System.IO;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

public class DriveEnumeratorTests
{
    private static DriveDescriptor Drive(string root, DriveType type, bool ready = true, bool cloud = false)
        => new(root, type, ready, cloud);

    [Fact]
    public void SelectRoots_FixedDrivesAlwaysIncluded_OthersOffByDefault()
    {
        var drives = new[]
        {
            Drive(@"C:\", DriveType.Fixed),
            Drive(@"D:\", DriveType.Fixed),
            Drive(@"E:\", DriveType.Removable),
            Drive(@"Z:\", DriveType.Network),
        };

        var roots = DriveEnumerator.SelectRoots(drives, includeNetwork: false, includeRemovable: false, includeCloud: false);

        Assert.Equal(new[] { @"C:\", @"D:\" }, roots);
    }

    [Fact]
    public void SelectRoots_TogglesIncludeNetworkAndRemovable()
    {
        var drives = new[]
        {
            Drive(@"C:\", DriveType.Fixed),
            Drive(@"E:\", DriveType.Removable),
            Drive(@"Z:\", DriveType.Network),
        };

        var roots = DriveEnumerator.SelectRoots(drives, includeNetwork: true, includeRemovable: true, includeCloud: false);

        Assert.Equal(new[] { @"C:\", @"E:\", @"Z:\" }, roots);
    }

    [Fact]
    public void SelectRoots_SkipsNotReadyDrives()
    {
        var drives = new[]
        {
            Drive(@"C:\", DriveType.Fixed, ready: true),
            Drive(@"A:\", DriveType.Removable, ready: false),
            Drive(@"D:\", DriveType.Fixed, ready: false),
        };

        var roots = DriveEnumerator.SelectRoots(drives, includeNetwork: true, includeRemovable: true, includeCloud: true);

        Assert.Equal(new[] { @"C:\" }, roots);
    }

    [Fact]
    public void SelectRoots_CloudDriveExcludedByDefault_EvenWhenFixed()
    {
        // Google Drive for desktop reports as a Fixed drive but is flagged cloud.
        var drives = new[]
        {
            Drive(@"C:\", DriveType.Fixed),
            Drive(@"G:\", DriveType.Fixed, cloud: true),
        };

        var withoutCloud = DriveEnumerator.SelectRoots(drives, includeNetwork: false, includeRemovable: false, includeCloud: false);
        Assert.Equal(new[] { @"C:\" }, withoutCloud);

        var withCloud = DriveEnumerator.SelectRoots(drives, includeNetwork: false, includeRemovable: false, includeCloud: true);
        Assert.Equal(new[] { @"C:\", @"G:\" }, withCloud);
    }

    [Fact]
    public void SelectRoots_CloudFlagOverridesNetworkToggle()
    {
        // A network-typed drive that is also cloud-detected follows the cloud toggle, not the network one.
        var drives = new[] { Drive(@"Z:\", DriveType.Network, cloud: true) };

        Assert.Empty(DriveEnumerator.SelectRoots(drives, includeNetwork: true, includeRemovable: true, includeCloud: false));
        Assert.Equal(new[] { @"Z:\" }, DriveEnumerator.SelectRoots(drives, includeNetwork: false, includeRemovable: false, includeCloud: true));
    }

    [Fact]
    public void SelectRoots_DeduplicatesAndIgnoresBlankRoots()
    {
        var drives = new[]
        {
            Drive(@"C:\", DriveType.Fixed),
            Drive(@"c:\", DriveType.Fixed),
            Drive("   ", DriveType.Fixed),
        };

        var roots = DriveEnumerator.SelectRoots(drives, includeNetwork: false, includeRemovable: false, includeCloud: false);

        Assert.Equal(new[] { @"C:\" }, roots);
    }

    [Fact]
    public void SelectRoots_UnknownAndCdRomTypesAreSkipped()
    {
        var drives = new[]
        {
            Drive(@"C:\", DriveType.Fixed),
            Drive(@"X:\", DriveType.CDRom),
            Drive(@"R:\", DriveType.Ram),
            Drive(@"U:\", DriveType.Unknown),
        };

        var roots = DriveEnumerator.SelectRoots(drives, includeNetwork: true, includeRemovable: true, includeCloud: true);

        Assert.Equal(new[] { @"C:\" }, roots);
    }

    [Fact]
    public void SelectRoots_NullInput_ReturnsEmpty()
    {
        Assert.Empty(DriveEnumerator.SelectRoots(null!, true, true, true));
    }

    [Fact]
    public void SelectRoots_SkipsNullElements()
    {
        var drives = new DriveDescriptor?[]
        {
            null,
            Drive(@"C:\", DriveType.Fixed),
        };

        var roots = DriveEnumerator.SelectRoots(drives!, true, true, true);

        Assert.Equal(new[] { @"C:\" }, roots);
    }

    [Theory]
    [InlineData("Google Drive", "FAT32", true)]   // label marker
    [InlineData("GOOGLEDRIVE", "NTFS", true)]      // case-insensitive
    [InlineData("Backup", "OneDrive", true)]       // format marker
    [InlineData("Dropbox", "exFAT", true)]
    [InlineData("My Data", "NTFS", false)]         // no marker
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void MatchesCloudMarker_DetectsKnownProviders(string? label, string? format, bool expected)
    {
        Assert.Equal(expected, DriveEnumerator.MatchesCloudMarker(label, format));
    }

    [Fact]
    public void IsLikelyCloudDrive_NullDrive_ReturnsFalse()
    {
        Assert.False(DriveEnumerator.IsLikelyCloudDrive(null!));
    }

    [Fact]
    public void GetSearchRoots_DoesNotThrow_AndReturnsList()
    {
        // Exercises the live-hardware enumeration path; just assert it runs and returns a list.
        var roots = DriveEnumerator.GetSearchRoots(includeNetwork: false, includeRemovable: false, includeCloud: false);
        Assert.NotNull(roots);
    }

    [Fact]
    public void EnumerateDrives_DoesNotThrow_AndReturnsList()
    {
        var drives = DriveEnumerator.EnumerateDrives();
        Assert.NotNull(drives);
    }
}
