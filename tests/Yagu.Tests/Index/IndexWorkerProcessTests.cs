using System.Diagnostics;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

public sealed class IndexWorkerProcessTests
{
    [Fact]
    public async Task Adapter_ExposesOwnedProcessAndRedirectedStreams_UntilCleanExit()
    {
        using IIndexWorkerProcess process = CreateProcess();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int removedHandlerCalls = 0;
        EventHandler removed = (_, _) => Interlocked.Increment(ref removedHandlerCalls);
        EventHandler retained = (_, _) => exited.TrySetResult();
        process.Exited += removed;
        process.Exited += retained;
        process.Exited -= removed;

        Assert.True(process.Start());
        Assert.False(process.HasExited);
        Assert.True(process.Id > 0);
        Assert.NotEqual(nint.Zero, process.Handle);
        Assert.True(process.StandardInput is StreamWriter);
        Assert.True(process.StandardOutput.BaseStream.CanRead);
        Assert.True(process.StandardError.BaseStream.CanRead);
        Assert.Contains("\"type\":\"ready\"", await process.StandardOutput.ReadLineAsync());

        process.Refresh();
        Assert.True(process.PeakWorkingSetBytes > 0);
        await process.StandardInput.WriteLineAsync("{\"op\":\"shutdown\"}");
        await process.StandardInput.FlushAsync();
        await WaitForExitAsync(process);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
        Assert.Equal(0, Volatile.Read(ref removedHandlerCalls));
        process.Exited -= retained;
    }

    [Fact]
    public async Task Adapter_KillTerminatesOnlyItsOwnedProcessTree()
    {
        using IIndexWorkerProcess process = CreateProcess();
        Assert.True(process.Start());
        Assert.Contains("\"type\":\"ready\"", await process.StandardOutput.ReadLineAsync());

        process.Kill();
        await WaitForExitAsync(process);

        Assert.True(process.HasExited);
    }

    [Fact]
    public void Adapter_DisposeBeforeStart_DoesNotThrow()
    {
        IIndexWorkerProcess process = CreateProcess();

        Exception? error = Record.Exception(process.Dispose);

        Assert.Null(error);
    }

    private static IIndexWorkerProcess CreateProcess()
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
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        return IndexWorkerProcessFactory.Create(startInfo);
    }

    private static async Task WaitForExitAsync(IIndexWorkerProcess process)
    {
        for (int attempt = 0; attempt < 500 && !process.HasExited; attempt++)
            await Task.Delay(10);

        Assert.True(process.HasExited);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx).");
    }
}