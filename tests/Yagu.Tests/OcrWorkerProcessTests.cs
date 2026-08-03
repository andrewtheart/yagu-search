using System.Diagnostics;
using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class OcrWorkerProcessTests
{
    [Fact]
    public async Task Adapter_StartsWithRedirectedStreams_AndWaitsForCleanExit()
    {
        using IOcrWorkerProcess process = CreateProcess();

        Assert.True(process.Start());
        Assert.False(process.HasExited);
        Assert.True(Assert.IsType<StreamWriter>(process.StandardInput).BaseStream.CanWrite);
        Assert.True(process.StandardOutput.BaseStream.CanRead);
        Assert.True(process.StandardError.BaseStream.CanRead);
        Assert.Contains("\"type\":\"ready\"", await process.StandardOutput.ReadLineAsync());

        await process.StandardInput.WriteLineAsync("{\"op\":\"shutdown\"}");
        await process.StandardInput.FlushAsync();
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Adapter_RemovesExitHandler_CancelsWait_AndKillsOwnedProcessTree()
    {
        string childPidPath = Path.Combine(Path.GetTempPath(), "yagu-ocr-process-child-" + Guid.NewGuid().ToString("N"));
        IOcrWorkerProcess process = CreateProcess("--ocr-process-tree-parent", childPidPath);
        Process? child = null;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int removedHandlerCalls = 0;
        EventHandler removedHandler = (_, _) => Interlocked.Increment(ref removedHandlerCalls);
        EventHandler retainedHandler = (_, _) => exited.TrySetResult();
        process.Exited += removedHandler;
        process.Exited += retainedHandler;
        process.Exited -= removedHandler;

        try
        {
            Assert.True(process.Start());
            Assert.Contains("\"type\":\"ready\"", await process.StandardOutput.ReadLineAsync());
            int childPid = int.Parse(await File.ReadAllTextAsync(childPidPath), System.Globalization.CultureInfo.InvariantCulture);
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => process.WaitForExitAsync(cancellation.Token));
            Assert.False(process.HasExited);

            process.Kill();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(process.HasExited);
            Assert.True(child.HasExited);
            Assert.Equal(0, Volatile.Read(ref removedHandlerCalls));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (child is not null && !child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            child?.Dispose();
            process.Exited -= retainedHandler;
            process.Dispose();
            File.Delete(childPidPath);
        }
    }

    [Fact]
    public void Adapter_DisposeBeforeStart_DoesNotThrow()
    {
        IOcrWorkerProcess process = CreateProcess();

        Exception? error = Record.Exception(process.Dispose);

        Assert.Null(error);
    }

    private static IOcrWorkerProcess CreateProcess(params string[] arguments)
    {
        string configuration =
    #if DEBUG
            "Debug";
    #else
            "Release";
    #endif
        string helperPath = Path.Combine(
            FindRepoRoot(), "tests", "Yagu.FakeIndexWorker", "bin", configuration, "net10.0", "Yagu.FakeIndexWorker.exe");
        Assert.True(File.Exists(helperPath), $"Repository-owned process helper was not built: {helperPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return OcrWorkerProcessFactory.Create(startInfo);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx).");
    }
}