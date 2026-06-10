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