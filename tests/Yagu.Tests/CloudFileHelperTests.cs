using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests the cloud placeholder detection and skip-decision logic. The attribute
/// bitmask and opt-in/opt-out behavior are pure; the provider-liveness and
/// sync-root registry paths are exercised only for their safe-default behavior
/// (no live provider for a fabricated path must still skip).
/// </summary>
public sealed class CloudFileHelperTests
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    [Theory]
    [InlineData(FileAttributes.Normal)]
    [InlineData(FileAttributes.Archive)]
    [InlineData(FileAttributes.Archive | FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.Offline)] // legacy HSM flag is NOT a Cloud Files placeholder
    public void IsCloudOnlyPlaceholder_NonPlaceholder_ReturnsFalse(FileAttributes attrs)
        => Assert.False(CloudFileHelper.IsCloudOnlyPlaceholder(attrs));

    [Fact]
    public void IsCloudOnlyPlaceholder_RecallOnOpen_ReturnsTrue()
        => Assert.True(CloudFileHelper.IsCloudOnlyPlaceholder(RecallOnOpen | FileAttributes.Archive));

    [Fact]
    public void IsCloudOnlyPlaceholder_RecallOnDataAccess_ReturnsTrue()
        => Assert.True(CloudFileHelper.IsCloudOnlyPlaceholder(RecallOnDataAccess | FileAttributes.Archive));

    [Fact]
    public void IsCloudSyncDirectory_FabricatedPath_IsNotASyncRoot()
    {
        // A path that cannot be under any sync root must answer false without throwing, so the index
        // crawler falls back to the ordinary junction/symlink gate for it.
        Assert.False(CloudFileHelper.IsCloudSyncDirectory(@"C:\definitely\not\a\sync\root\zzz"));
        Assert.False(CloudFileHelper.IsCloudSyncDirectory(string.Empty));
    }

    [Fact]
    public void IsCloudSyncDirectory_RealSyncRoot_IsDetected_WhenOneIsPresent()
    {
        // Regression guard for the buffer-sizing defect: CF_SYNC_ROOT_PROVIDER_INFO is 1028 bytes, so a
        // hand-sized 1024-byte buffer made CfGetSyncRootInfoByPath always return ERROR_MORE_DATA. That was
        // read as "not a sync root", which silently reduced provider liveness to a constant false and made
        // every "search/index online-only files" opt-in a no-op. Only assert when this machine actually has
        // a cloud sync root, so the test stays meaningful without becoming environment-dependent.
        string? syncRoot = FindLocalSyncRoot();
        if (syncRoot is null)
            return; // No cloud provider configured on this machine; nothing to assert.

        Assert.True(
            CloudFileHelper.IsCloudSyncDirectory(syncRoot),
            $"'{syncRoot}' is a registered cloud sync root but was not detected as one.");
    }

    private static string? FindLocalSyncRoot()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var srm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (srm is null)
                return null;
            foreach (string rootName in srm.GetSubKeyNames())
            {
                using var userRoots = srm.OpenSubKey($@"{rootName}\UserSyncRoots");
                if (userRoots is null) continue;
                foreach (string valueName in userRoots.GetValueNames())
                {
                    if (userRoots.GetValue(valueName) is string dir
                        && dir.Length > 0
                        && Directory.Exists(dir))
                    {
                        return dir;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        return null;
    }

    [Fact]
    public void ShouldSkipPlaceholder_NonPlaceholder_NeverSkips()
    {
        Assert.False(CloudFileHelper.ShouldSkipPlaceholder(@"C:\x\file.txt", FileAttributes.Archive, searchOnlineOnlyFiles: false));
        Assert.False(CloudFileHelper.ShouldSkipPlaceholder(@"C:\x\file.txt", FileAttributes.Archive, searchOnlineOnlyFiles: true));
    }

    [Fact]
    public void ShouldSkipPlaceholder_OptOut_SkipsPlaceholder()
        => Assert.True(CloudFileHelper.ShouldSkipPlaceholder(@"C:\x\file.txt", RecallOnDataAccess, searchOnlineOnlyFiles: false));

    [Fact]
    public void ShouldSkipPlaceholder_OptIn_NoLiveProvider_StillSkips()
    {
        // A fabricated path is under no connected sync provider, so even with the
        // opt-in enabled it must be skipped — opening it could block forever.
        Assert.True(CloudFileHelper.ShouldSkipPlaceholder(
            @"C:\Yagu-nonexistent-sync-root-xyz\file.txt", RecallOnDataAccess, searchOnlineOnlyFiles: true));
    }

    [Fact]
    public void ShouldSkipDiscoveredPath_PathOutsideCloudRoots_ReturnsFalseWithoutStat()
    {
        // A normal source path is not under any cloud sync root, so the prefix gate
        // returns false without ever stat-ing or hydrating.
        Assert.False(CloudFileHelper.ShouldSkipDiscoveredPath(
            @"C:\src\Yagu\README.md", searchOnlineOnlyFiles: false));
    }

    [Fact]
    public void SkipBreakdown_CloudOnly_RoundTripsAndReports()
    {
        var b = new SkipBreakdown(
            Binary: 0, AccessDenied: 0, IOError: 0, TooLarge: 0, NotFound: 0,
            Encoding: 0, Other: 0, CloudOnly: 42);
        Assert.Equal(42, b.CloudOnly);
        Assert.Contains("cloudOnly=42", b.ToString());
    }

    [Fact]
    public void SearchOptions_SearchOnlineOnlyFiles_DefaultsOff()
        => Assert.False(new SearchOptions { Directory = @"C:\", Query = "x" }.SearchOnlineOnlyFiles);
}
