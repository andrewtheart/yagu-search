using System.Diagnostics;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class EverythingIndexConfiguratorTests
{
    [Fact]
    public async Task AddVolumesAndRescanAsync_DefaultOverload_WithInvalidInputs_ReturnsFalse()
    {
        bool blankExe = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            string.Empty,
            [@"C:\\work"],
            CancellationToken.None);
        bool noRoots = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\does-not-exist\\Everything.exe",
            [""],
            CancellationToken.None);

        Assert.False(blankExe);
        Assert.False(noRoots);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_DefaultOverload_UsesProcessStarterSeamWithoutLaunching()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "yagu-everything-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        string exe = Path.Combine(sandbox, "Everything.exe");
        File.WriteAllText(exe, "placeholder");

        Func<ProcessStartInfo, Process?> originalStarter = EverythingIndexConfigurator.ProcessStarter;
        try
        {
            int starterCalls = 0;
            EverythingIndexConfigurator.ProcessStarter = _ =>
            {
                starterCalls++;
                return null;
            };

            bool result = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
                exe,
                [@"C:\\work"],
                CancellationToken.None);

            Assert.False(result);
            Assert.Equal(1, starterCalls);
        }
        finally
        {
            EverythingIndexConfigurator.ProcessStarter = originalStarter;
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_DefaultOverload_StartedProcessExitCodeControlsResult()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "yagu-everything-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        string exe = Path.Combine(sandbox, "Everything.exe");
        File.WriteAllText(exe, "placeholder");

        Func<ProcessStartInfo, Process?> originalStarter = EverythingIndexConfigurator.ProcessStarter;
        Func<Process, CancellationToken, Task> originalWait = EverythingIndexConfigurator.WaitForExitAsyncCore;
        Func<Process, int> originalReadExitCode = EverythingIndexConfigurator.ReadExitCode;
        try
        {
            EverythingIndexConfigurator.ProcessStarter = _ => new Process();
            EverythingIndexConfigurator.WaitForExitAsyncCore = static (_, _) => Task.CompletedTask;

            EverythingIndexConfigurator.ReadExitCode = static _ => 0;
            bool success = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
                exe,
                [@"C:\\work"],
                CancellationToken.None);

            EverythingIndexConfigurator.ReadExitCode = static _ => 1;
            bool failure = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
                exe,
                [@"C:\\work"],
                CancellationToken.None);

            Assert.True(success);
            Assert.False(failure);
        }
        finally
        {
            EverythingIndexConfigurator.ProcessStarter = originalStarter;
            EverythingIndexConfigurator.WaitForExitAsyncCore = originalWait;
            EverythingIndexConfigurator.ReadExitCode = originalReadExitCode;
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_DefaultOverload_WaitCancellationPropagates()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "yagu-everything-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        string exe = Path.Combine(sandbox, "Everything.exe");
        File.WriteAllText(exe, "placeholder");

        Func<ProcessStartInfo, Process?> originalStarter = EverythingIndexConfigurator.ProcessStarter;
        Func<Process, CancellationToken, Task> originalWait = EverythingIndexConfigurator.WaitForExitAsyncCore;
        Func<Process, int> originalReadExitCode = EverythingIndexConfigurator.ReadExitCode;
        try
        {
            EverythingIndexConfigurator.ProcessStarter = _ => new Process();
            EverythingIndexConfigurator.WaitForExitAsyncCore = static (_, _) => throw new OperationCanceledException();
            EverythingIndexConfigurator.ReadExitCode = static _ => 0;

            await Assert.ThrowsAsync<OperationCanceledException>(() => EverythingIndexConfigurator.AddVolumesAndRescanAsync(
                exe,
                [@"C:\\work"],
                CancellationToken.None));
        }
        finally
        {
            EverythingIndexConfigurator.ProcessStarter = originalStarter;
            EverythingIndexConfigurator.WaitForExitAsyncCore = originalWait;
            EverythingIndexConfigurator.ReadExitCode = originalReadExitCode;
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NormalizeRootVolumes_SkipsMalformedAndDeduplicates()
    {
        IReadOnlyList<string> roots = EverythingIndexConfigurator.NormalizeRootVolumes([
            @"C:\\one\\two",
            @"c:\\three",
            @"D:\\",
            "bad\0path"
        ]);

        Assert.Equal(["C:", "D:"], roots);
    }

    [Fact]
    public void NormalizeRootVolumes_NullAndUncPaths_AreHandled()
    {
        IReadOnlyList<string> roots = EverythingIndexConfigurator.NormalizeRootVolumes([
            null!,
            @"\\server\share\dir"
        ]);

        Assert.Equal([@"\\server\share"], roots);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_ReturnsFalse_ForMissingExecutableOrNoRoots()
    {
        bool missingExe = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [@"C:\\work"],
            fileExists: _ => false,
            runAsync: static (_, _) => Task.FromResult(true),
            cancellationToken: CancellationToken.None);

        bool noRoots = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [""],
            fileExists: _ => true,
            runAsync: static (_, _) => Task.FromResult(true),
            cancellationToken: CancellationToken.None);

        Assert.False(missingExe);
        Assert.False(noRoots);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_ReturnsFalse_WhenAddVolumesFails()
    {
        bool result = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [@"C:\\work", @"D:\\docs"],
            fileExists: _ => true,
            runAsync: static (startInfo, _) =>
            {
                bool isAddVolumes = startInfo.ArgumentList.Count >= 1 && startInfo.ArgumentList[0] == "-add-volumes";
                return Task.FromResult(!isAddVolumes);
            },
            cancellationToken: CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_ReturnsFalse_WhenOneRescanFails()
    {
        int call = 0;
        bool result = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [@"C:\\work", @"D:\\docs"],
            fileExists: _ => true,
            runAsync: (_, _) => Task.FromResult(++call != 3),
            cancellationToken: CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_SendsExpectedArguments_InOrder()
    {
        List<string[]> calls = [];

        bool result = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [@"C:\\work", @"D:\\docs", @"d:\\more"],
            fileExists: _ => true,
            runAsync: (startInfo, _) =>
            {
                calls.Add(startInfo.ArgumentList.ToArray());
                return Task.FromResult(true);
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, calls.Count);
        Assert.Equal(["-add-volumes", "C:;D:"], calls[0]);
        Assert.Equal(["-rescan", "C:\\"], calls[1]);
        Assert.Equal(["-rescan", "D:\\"], calls[2]);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_RethrowsCancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [@"C:\\work"],
            fileExists: _ => true,
            runAsync: static (_, _) => throw new OperationCanceledException(),
            cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_UnexpectedException_ReturnsFalse()
    {
        bool result = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\\Everything.exe",
            [@"C:\\work"],
            fileExists: _ => true,
            runAsync: static (_, _) => throw new InvalidOperationException("boom"),
            cancellationToken: CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task AddVolumesAndRescanAsync_UsesProvidedExecutablePath()
    {
        string? exe = null;
        bool result = await EverythingIndexConfigurator.AddVolumesAndRescanAsync(
            @"C:\Tools\Everything.exe",
            [@"C:\\work"],
            fileExists: _ => true,
            runAsync: (startInfo, _) =>
            {
                exe = startInfo.FileName;
                return Task.FromResult(true);
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result);
        Assert.Equal(@"C:\Tools\Everything.exe", exe);
    }
}
