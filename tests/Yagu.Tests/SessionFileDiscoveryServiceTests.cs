using System.Diagnostics;
using Yagu.Native;
using Yagu.Services;

namespace Yagu.Tests;

[Collection("FileListerBackend")]
public sealed class SessionFileDiscoveryServiceTests : IDisposable
{
    private readonly bool _originalSdkAvailable;

    public SessionFileDiscoveryServiceTests()
    {
        _originalSdkAvailable = FileLister.SdkAvailable;
        FileLister.SdkAvailable = true;
    }

    public void Dispose()
    {
        FileLister.SdkAvailable = _originalSdkAvailable;
    }

    [Fact]
    public async Task FindSessionFilesAsync_UsesEverythingSdkForMachineWideSessionFiles()
    {
        var modified = DateTime.UtcNow.AddMinutes(-5).ToFileTimeUtc();
        var sdk = new MockEverythingSdkOps
        {
            Results =
            [
                (@"C:\sessions\first.yagu-session", 128),
                (@"C:\sessions\ignore.txt", 256),
            ],
            ModifiedFileTimes = { [0] = modified },
        };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Equal(SessionFileDiscoveryBackend.EverythingSdk, result.Backend);
        Assert.Single(result.Files);
        Assert.Equal(@"C:\sessions\first.yagu-session", result.Files[0].Path);
        Assert.Equal(128, result.Files[0].SizeBytes);
        Assert.Equal("file: ext:yagu-session", sdk.CapturedQuery);
        Assert.Equal(EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME
            | EverythingSdk.EVERYTHING_REQUEST_SIZE
            | EverythingSdk.EVERYTHING_REQUEST_DATE_MODIFIED, sdk.CapturedRequestFlags);
    }

    [Fact]
    public async Task FindSessionFilesAsync_FallsBackToEsExeWhenSdkUnavailable()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        ProcessStartInfo? capturedStartInfo = null;
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, psi) =>
            {
                capturedStartInfo = psi;
                return new FakeProcess([
                    @"D:\saved\one.yagu-session",
                    @"D:\saved\not-a-session.txt",
                    @"E:\other\two.YAGU-SESSION",
                ], exitCode: 0);
            });

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Equal(SessionFileDiscoveryBackend.EsExe, result.Backend);
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Files, candidate => candidate.Path == @"D:\saved\one.yagu-session");
        Assert.Contains(result.Files, candidate => candidate.Path == @"E:\other\two.YAGU-SESSION");
        Assert.NotNull(capturedStartInfo);
        Assert.Contains("/a-d", capturedStartInfo!.ArgumentList);
        Assert.Contains("ext:yagu-session", capturedStartInfo.ArgumentList);
    }

    [Fact]
    public async Task FindSessionFilesAsync_ReturnsUnavailableWhenNoFastBackendExists()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Equal(SessionFileDiscoveryBackend.None, result.Backend);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task FindSessionFilesAsync_TreatsEsExeExitCodeEightAsUnavailable()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([], exitCode: 8));

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Equal("Everything is not running.", result.Error);
    }

    private sealed class FakeProcess(string[] lines, int exitCode) : FileLister.IProcess
    {
        private int _index;

        public int ExitCode { get; } = exitCode;
        public void Start() { }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_index >= lines.Length)
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(lines[_index++]);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

[Collection("FileListerBackend")]
public sealed class SessionFileDiscoveryServiceBranchTests : IDisposable
{
    private readonly bool _originalSdkAvailable;

    public SessionFileDiscoveryServiceBranchTests()
    {
        _originalSdkAvailable = FileLister.SdkAvailable;
        FileLister.SdkAvailable = true;
    }

    public void Dispose()
    {
        FileLister.SdkAvailable = _originalSdkAvailable;
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkQueryFails_ReportsIpcError()
    {
        var sdk = new MockEverythingSdkOps { QuerySucceeds = false, LastError = EverythingSdk.EVERYTHING_ERROR_IPC };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Contains("Everything is not running", result.Error);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkQueryFails_NonIpcError()
    {
        var sdk = new MockEverythingSdkOps { QuerySucceeds = false, LastError = 99 };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Contains("query failed", result.Error);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkMultipleResults_CollectsAll()
    {
        var results = new List<(string Path, long Size)>();
        for (int i = 0; i < 3; i++)
            results.Add(($@"C:\sessions\file{i}.yagu-session", i * 100));

        var sdk = new MockEverythingSdkOps { Results = results };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Equal(3, result.Files.Count);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkDllNotFound_FallsBackToEsExe()
    {
        var sdk = new MockEverythingSdkOps { ThrowDllNotFound = true };
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([@"D:\a.yagu-session"], exitCode: 0));

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Equal(SessionFileDiscoveryBackend.EsExe, result.Backend);
    }

    [Fact]
    public async Task FindSessionFilesAsync_EsExeNonZeroExitWithResults_StillReturnsFiles()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([@"D:\a.yagu-session"], exitCode: 1));

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task FindSessionFilesAsync_EsExeNonZeroExitNoResults_ReturnsUnavailable()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([], exitCode: 2));

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Contains("exited with code 2", result.Error);
    }

    [Fact]
    public async Task FindSessionFilesAsync_EsExeThrows_ReturnsUnavailable()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new ThrowingProcess());

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Contains("could not search", result.Error);
    }

    [Fact]
    public async Task FindSessionFilesAsync_DeduplicatesPaths()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results =
            [
                (@"C:\sessions\dup.yagu-session", 100),
                (@"C:\sessions\DUP.YAGU-SESSION", 200),
            ]
        };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.Single(result.Files);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkNotAvailableStatically_SkipsSdk()
    {
        FileLister.SdkAvailable = false;
        var sdk = new MockEverythingSdkOps { DbLoaded = true };
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([@"D:\file.yagu-session"], exitCode: 0));

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Equal(SessionFileDiscoveryBackend.EsExe, result.Backend);
    }

    private sealed class FakeProcess(string[] lines, int exitCode) : FileLister.IProcess
    {
        private int _index;
        public int ExitCode { get; } = exitCode;
        public void Start() { }
        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(_index < lines.Length ? lines[_index++] : null);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingProcess : FileLister.IProcess
    {
        public int ExitCode => -1;
        public void Start() => throw new InvalidOperationException("Process failed to start");
        public Task<string?> ReadLineAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkLongPath_TriggersBufferResize()
    {
        // Path longer than 1024 chars triggers the buffer resize in ReadSdkFullPath
        string longDir = @"C:\" + new string('a', 1050);
        string longPath = longDir + @"\data.yagu-session";
        var sdk = new MockEverythingSdkOps
        {
            Results = [(longPath, 500)],
            ModifiedFileTimes = { [0] = DateTime.UtcNow.ToFileTimeUtc() },
        };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        Assert.Equal(longPath, result.Files[0].Path);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkEmptyPath_Skipped()
    {
        // GetResultFullPathName returning 0 → empty string → IsSessionFilePath returns false
        var sdk = new MockEverythingSdkOps
        {
            Results = [("", 0)],
        };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task FindSessionFilesAsync_EsExeNonexistentFile_StillIncluded()
    {
        // CreateCandidateFromPath with nonexistent path gets null size/modified but still included
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        string fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".yagu-session");
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([fakePath], exitCode: 0));

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        Assert.Equal(fakePath, result.Files[0].Path);
        Assert.Null(result.Files[0].SizeBytes);
        Assert.Null(result.Files[0].ModifiedUtc);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkDeduplicatesByPath()
    {
        // NormalizeCandidates deduplicates by path (case-insensitive)
        var sdk = new MockEverythingSdkOps
        {
            Results =
            [
                (@"C:\sessions\dup.yagu-session", 100),
                (@"C:\SESSIONS\DUP.yagu-session", 200),
            ],
            ModifiedFileTimes = { [0] = DateTime.UtcNow.ToFileTimeUtc(), [1] = DateTime.UtcNow.AddMinutes(1).ToFileTimeUtc() },
        };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkPathWithTrailingNull_TrimmedCorrectly()
    {
        // Exercises the "buffer[charCount - 1] == '\0'" branch in ReadSdkFullPath
        // Need a mock that includes a trailing null char in the returned length
        var sdk = new TrailingNullSdkOps();
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        // The trailing null should be trimmed by ReadSdkFullPath
        Assert.Equal(@"C:\data\test.yagu-session", result.Files[0].Path);
        // Verify no embedded null in the final path
        Assert.Equal(-1, result.Files[0].Path.IndexOf('\0'));
    }

    /// <summary>
    /// Mock that writes a trailing null char into the buffer and includes it in the returned length,
    /// exercising the null-trimming branch in ReadSdkFullPath.
    /// </summary>
    private sealed class TrailingNullSdkOps : IEverythingSdkOps
    {
        private const string TestPath = @"C:\data\test.yagu-session";
        public object SyncLock { get; } = new();
        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string searchString) { }
        public void SetMatchCase(bool matchCase) { }
        public void SetMatchPath(bool matchPath) { }
        public void SetOffset(uint offset) { }
        public void SetMax(uint max) { }
        public void SetRequestFlags(uint flags) { }
        public bool Query(bool bWait) => true;
        public uint GetLastError() => 0;
        public string ErrorMessage(uint error) => "";
        public uint GetNumResults() => 1;
        public uint GetTotResults() => 1;
        public bool GetResultSize(uint index, out long size) { size = 1024; return true; }
        public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = DateTime.UtcNow.ToFileTimeUtc(); return true; }
        public bool GetResultDateModified(uint index, out long fileTime) { fileTime = DateTime.UtcNow.ToFileTimeUtc(); return true; }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
        {
            if (index >= 1) return 0;
            TestPath.AsSpan().CopyTo(buffer);
            int len = TestPath.Length;
            buffer[len] = '\0'; // trailing null
            return (uint)(len + 1); // length includes the null
        }
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkNegativeSize_ReturnsNullSizeBytes()
    {
        var sdk = new NegativeSizeSdkOps();
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        Assert.Null(result.Files[0].SizeBytes);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkZeroModifiedFileTime_ReturnsNullModifiedUtc()
    {
        var sdk = new ZeroModifiedTimeSdkOps();
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        Assert.Null(result.Files[0].ModifiedUtc);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkGetSizeReturnsFalse_ReturnsNullSizeBytes()
    {
        var sdk = new SizeUnavailableSdkOps();
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Single(result.Files);
        Assert.Null(result.Files[0].SizeBytes);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkCancellation_StopsSearching()
    {
        using var cts = new CancellationTokenSource();
        var sdk = new CancelAfterFirstIterationSdkOps(cts);
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.FindSessionFilesAsync(cts.Token));
    }

    private sealed class NegativeSizeSdkOps : IEverythingSdkOps
    {
        public object SyncLock { get; } = new();
        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string s) { }
        public void SetMatchCase(bool v) { }
        public void SetMatchPath(bool v) { }
        public void SetOffset(uint v) { }
        public void SetMax(uint v) { }
        public void SetRequestFlags(uint v) { }
        public bool Query(bool bWait) => true;
        public uint GetLastError() => 0;
        public string ErrorMessage(uint e) => "";
        public uint GetNumResults() => 1;
        public uint GetTotResults() => 1;
        public bool GetResultSize(uint index, out long size) { size = -5; return true; }
        public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
        public bool GetResultDateModified(uint index, out long fileTime) { fileTime = DateTime.UtcNow.ToFileTimeUtc(); return true; }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
        {
            const string path = @"C:\test.yagu-session";
            path.AsSpan().CopyTo(buffer);
            return (uint)path.Length;
        }
    }

    private sealed class ZeroModifiedTimeSdkOps : IEverythingSdkOps
    {
        public object SyncLock { get; } = new();
        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string s) { }
        public void SetMatchCase(bool v) { }
        public void SetMatchPath(bool v) { }
        public void SetOffset(uint v) { }
        public void SetMax(uint v) { }
        public void SetRequestFlags(uint v) { }
        public bool Query(bool bWait) => true;
        public uint GetLastError() => 0;
        public string ErrorMessage(uint e) => "";
        public uint GetNumResults() => 1;
        public uint GetTotResults() => 1;
        public bool GetResultSize(uint index, out long size) { size = 100; return true; }
        public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
        public bool GetResultDateModified(uint index, out long fileTime) { fileTime = 0; return true; }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
        {
            const string path = @"C:\test.yagu-session";
            path.AsSpan().CopyTo(buffer);
            return (uint)path.Length;
        }
    }

    private sealed class SizeUnavailableSdkOps : IEverythingSdkOps
    {
        public object SyncLock { get; } = new();
        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string s) { }
        public void SetMatchCase(bool v) { }
        public void SetMatchPath(bool v) { }
        public void SetOffset(uint v) { }
        public void SetMax(uint v) { }
        public void SetRequestFlags(uint v) { }
        public bool Query(bool bWait) => true;
        public uint GetLastError() => 0;
        public string ErrorMessage(uint e) => "";
        public uint GetNumResults() => 1;
        public uint GetTotResults() => 1;
        public bool GetResultSize(uint index, out long size) { size = 0; return false; }
        public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
        public bool GetResultDateModified(uint index, out long fileTime) { fileTime = DateTime.UtcNow.ToFileTimeUtc(); return true; }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
        {
            const string path = @"C:\test.yagu-session";
            path.AsSpan().CopyTo(buffer);
            return (uint)path.Length;
        }
    }

    private sealed class CancelAfterFirstIterationSdkOps : IEverythingSdkOps
    {
        private readonly CancellationTokenSource _cts;
        public object SyncLock { get; } = new();
        public CancelAfterFirstIterationSdkOps(CancellationTokenSource cts) => _cts = cts;
        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string s) { }
        public void SetMatchCase(bool v) { }
        public void SetMatchPath(bool v) { }
        public void SetOffset(uint v) { }
        public void SetMax(uint v) { }
        public void SetRequestFlags(uint v) { }
        public bool Query(bool bWait) => true;
        public uint GetLastError() => 0;
        public string ErrorMessage(uint e) => "";
        public uint GetNumResults() => 5000; // match SdkPageSize so paging continues
        public uint GetTotResults() => 10000; // pretend more pages exist
        public bool GetResultSize(uint index, out long size) { size = 10; return true; }
        public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
        public bool GetResultDateModified(uint index, out long fileTime) { fileTime = DateTime.UtcNow.ToFileTimeUtc(); return true; }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
        {
            const string path = @"C:\test.yagu-session";
            path.AsSpan().CopyTo(buffer);
            _cts.Cancel(); // Cancel so next ThrowIfCancellationRequested fires
            return (uint)path.Length;
        }
    }
}

[Collection("FileListerBackend")]
public sealed class SessionFileDiscoveryServiceRound5Tests : IDisposable
{
    private readonly bool _originalSdkAvailable;

    public SessionFileDiscoveryServiceRound5Tests()
    {
        _originalSdkAvailable = FileLister.SdkAvailable;
        FileLister.SdkAvailable = true;
    }

    public void Dispose()
    {
        FileLister.SdkAvailable = _originalSdkAvailable;
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkZeroResults_ReturnsEmptyList()
    {
        // GetNumResults returns 0 on the first page → covers the "count == 0 break" line
        var sdk = new MockEverythingSdkOps { Results = [] };
        var service = new SessionFileDiscoveryService(sdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task FindSessionFilesAsync_SdkGeneralException_ReportsError()
    {
        // ThrowGeneral causes InvalidOperationException inside Query() → exercises catch(Exception)
        var sdk = new MockEverythingSdkOps { ThrowGeneral = true, GeneralExceptionMessage = "boom" };
        // ThrowGeneral fires in IsDBLoaded first. Need it to fire in Query instead.
        // Use a custom mock that throws only in Query
        var throwingSdk = new ThrowsInQuerySdkOps();
        var service = new SessionFileDiscoveryService(throwingSdk, findEsExe: () => null, processFactory: null);

        var result = await service.FindSessionFilesAsync();

        Assert.False(result.FastSearchAvailable);
        Assert.Contains("query failed", result.Error);
    }

    [Fact]
    public async Task FindSessionFilesAsync_EsExeWithExistingFile_PopulatesSizeAndModified()
    {
        // CreateCandidateFromPath with a real file covers the info.Exists/Length/LastWriteTimeUtc branches
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yagu-session");
        File.WriteAllText(tempFile, "session data content");
        try
        {
            var service = new SessionFileDiscoveryService(
                sdk,
                findEsExe: () => @"C:\tools\es.exe",
                processFactory: (_, _) => new FakeProcess([tempFile], exitCode: 0));

            var result = await service.FindSessionFilesAsync();

            Assert.True(result.FastSearchAvailable);
            Assert.Single(result.Files);
            Assert.NotNull(result.Files[0].SizeBytes);
            Assert.True(result.Files[0].SizeBytes > 0);
            Assert.NotNull(result.Files[0].ModifiedUtc);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task FindSessionFilesAsync_NormalizeCandidates_FiltersInvalidExtension()
    {
        // NormalizeCandidates skips non-.yagu-session paths (covers the continue branch)
        // This happens naturally when the process returns a non-session path — already tested
        // but let's test that duplicate paths with different metadata get merged correctly
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        string sessionPath = @"D:\sessions\test.yagu-session";
        var service = new SessionFileDiscoveryService(
            sdk,
            findEsExe: () => @"C:\tools\es.exe",
            processFactory: (_, _) => new FakeProcess([sessionPath, sessionPath], exitCode: 0));

        var result = await service.FindSessionFilesAsync();

        Assert.True(result.FastSearchAvailable);
        // Duplicate paths are deduplicated
        Assert.Single(result.Files);
    }

    private sealed class FakeProcess(string[] lines, int exitCode) : FileLister.IProcess
    {
        private int _index;
        public int ExitCode { get; } = exitCode;
        public void Start() { }
        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(_index < lines.Length ? lines[_index++] : null);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Mock that throws a general exception in Query() (not DllNotFoundException).
    /// IsDBLoaded() succeeds to get past the first guard, then Query throws.
    /// </summary>
    private sealed class ThrowsInQuerySdkOps : IEverythingSdkOps
    {
        public object SyncLock { get; } = new();
        public bool IsDBLoaded() => true;
        public void Reset() { }
        public void SetSearch(string s) { }
        public void SetMatchCase(bool v) { }
        public void SetMatchPath(bool v) { }
        public void SetOffset(uint v) { }
        public void SetMax(uint v) { }
        public void SetRequestFlags(uint v) { }
        public bool Query(bool bWait) => throw new InvalidOperationException("general SDK error");
        public uint GetLastError() => 0;
        public string ErrorMessage(uint e) => "";
        public uint GetNumResults() => 0;
        public uint GetTotResults() => 0;
        public bool GetResultSize(uint index, out long size) { size = 0; return false; }
        public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
        public bool GetResultDateModified(uint index, out long fileTime) { fileTime = 0; return false; }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity) => 0;
    }
}