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

    [Fact]
    public void WithNoCloudProviderInstalled_TheDiscoveredPathGateCostsNothingAndSkipsNothing()
    {
        // Everything yields bare path strings, so this gate runs for every discovered result. With no
        // sync root registered it must answer without stat-ing anything — a File.GetAttributes per
        // result would be a syscall on a path that cannot be a placeholder.
        Assert.False(CloudFileHelper.ShouldSkipDiscoveredPath(
            @"C:\does\not\exist\file.txt", searchOnlineOnlyFiles: false, prefixes: []));
    }

    [Fact]
    public void APathUnderASyncRootThatCannotBeStatted_IsKeptRatherThanSkipped()
    {
        string root = Path.Combine(Path.GetTempPath(), "yagu-cloud-" + Guid.NewGuid().ToString("n"));
        string[] prefixes = [root + "\\"];

        // A path that vanished between discovery and this check cannot be proven to be a placeholder.
        // Skipping on a failed stat would silently drop real results whenever a scan raced a delete.
        Assert.False(CloudFileHelper.ShouldSkipDiscoveredPath(
            Path.Combine(root, "gone.txt"), searchOnlineOnlyFiles: false, prefixes));
    }

    [Fact]
    public void AnOrdinaryFileUnderASyncRoot_IsNotTreatedAsAPlaceholder()
    {
        string root = Path.Combine(Path.GetTempPath(), "yagu-cloud-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "local.txt");
            File.WriteAllText(file, "hydrated content");
            string[] prefixes = [root + "\\"];

            // Being inside a sync root is not itself a reason to skip: most files under OneDrive are
            // fully local, and skipping them would silently exclude the user's own documents.
            Assert.False(CloudFileHelper.ShouldSkipDiscoveredPath(file, searchOnlineOnlyFiles: false, prefixes));
            Assert.False(CloudFileHelper.ShouldSkipDiscoveredPath(file, searchOnlineOnlyFiles: true, prefixes));

            // A path outside every prefix never reaches the stat at all.
            Assert.False(CloudFileHelper.ShouldSkipDiscoveredPath(
                @"C:\src\Yagu\README.md", searchOnlineOnlyFiles: false, prefixes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OnAHostWithoutTheCloudFilterApi_NothingIsHydratable()
    {
        // The API arrived in Windows 10 1709. Before it, every placeholder is unrecoverable, so the
        // only safe answer is "no live provider" rather than optimistically trying to read the file.
        Assert.False(CloudFileHelper.IsProviderLive(
            @"C:\x\y.txt", cloudFilterAvailable: false, static _ => @"C:\x"));
    }

    [Fact]
    public void APathThePlatformRefusesToParse_IsReportedAsNotHydratable()
    {
        // This runs per file during a scan. Letting the failure escape would abort the whole search
        // over one unparseable path.
        Assert.False(CloudFileHelper.IsProviderLive(
            "\0bad", cloudFilterAvailable: true, static _ => throw new ArgumentException("bad path")));
    }

    [Fact]
    public void WithNoSyncRootManagerKey_TheSyncRootListIsEmptyRatherThanFailing()
    {
        Assert.Empty(CloudFileHelper.LoadSyncRootPrefixes(isWindows: true, static () => null));
        Assert.Empty(CloudFileHelper.LoadSyncRootPrefixes(isWindows: false, static () => null));
    }

    [Fact]
    public void WhenTheRegistryCannotBeRead_TheSyncRootListIsEmptyRatherThanFailing()
    {
        // Warming this list is the first thing a search does. A throw here would turn a locked-down
        // registry into a total search failure instead of "no cloud roots known".
        Assert.Empty(CloudFileHelper.LoadSyncRootPrefixes(
            isWindows: true, static () => throw new System.Security.SecurityException("denied")));
    }

    [Fact]
    public void AProviderRecordLargerThanTheFirstBuffer_IsRetriedAtTheSizeTheApiReports()
    {
        // This is the defect the retry exists for: CF_SYNC_ROOT_PROVIDER_INFO is 1028 bytes, and
        // reading ERROR_MORE_DATA as "not a sync root" reported every provider as dead — which
        // silently disabled every cloud opt-in in both search and indexing.
        var sizes = new List<int>();
        bool Probe(string path, int bytes, out uint status, out int hr, out int required)
        {
            sizes.Add(bytes);
            if (bytes < 4096)
            {
                status = 0;
                hr = unchecked((int)0x800700EA); // ERROR_MORE_DATA
                required = 4096;
                return false;
            }
            status = 1;
            hr = 0;
            required = 4096;
            return true;
        }

        Assert.True(CloudFileHelper.TryQuerySyncRootProvider(@"C:\cloud\a.txt", out uint providerStatus, Probe));
        Assert.Equal(1u, providerStatus);
        Assert.Equal(2, sizes.Count);
        Assert.Equal(4096, sizes[1]);
    }

    [Fact]
    public void AFailureThatIsNotATooSmallBuffer_IsNotRetried()
    {
        int calls = 0;
        bool Probe(string path, int bytes, out uint status, out int hr, out int required)
        {
            calls++;
            status = 0;
            hr = unchecked((int)0x80070186); // ERROR_NOT_A_CLOUD_FILE
            required = 0;
            return false;
        }

        Assert.False(CloudFileHelper.TryQuerySyncRootProvider(@"C:\plain\a.txt", out uint status, Probe));
        Assert.Equal(0u, status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ARetryTheApiSizesNoLargerThanTheFirstAttempt_IsNotAttempted()
    {
        // Retrying at a size that is not actually bigger would loop on a provider that keeps saying
        // "more data" without ever asking for more room.
        int calls = 0;
        bool Probe(string path, int bytes, out uint status, out int hr, out int required)
        {
            calls++;
            status = 0;
            hr = unchecked((int)0x800700EA);
            required = 8; // smaller than the initial buffer
            return false;
        }

        Assert.False(CloudFileHelper.TryQuerySyncRootProvider(@"C:\cloud\a.txt", out _, Probe));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WhenTheCloudFilterLibraryIsMissing_TheQueryAnswersNotHydratable()
    {
        Assert.False(CloudFileHelper.TryQuerySyncRootProvider(
            @"C:\cloud\a.txt",
            out _,
            static (string p, int b, out uint s, out int h, out int r) => throw new DllNotFoundException("cldapi.dll")));
    }

    [Fact]
    public void AnUnexpectedQueryFailure_IsSwallowedRatherThanFailingTheScan()
    {
        Assert.False(CloudFileHelper.TryQuerySyncRootProvider(
            @"C:\cloud\a.txt",
            out _,
            static (string p, int b, out uint s, out int h, out int r) => throw new InvalidOperationException("boom")));
    }
}
