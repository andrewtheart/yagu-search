using Yagu.Services;

namespace Yagu.Tests;

[Collection("FileListerBackend")]
public sealed class FileListerEsExeGateTests : IDisposable
{
    private readonly FileListerBackend _originalBackend;

    public FileListerEsExeGateTests()
    {
        _originalBackend = FileLister.Backend;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
    }

    [Fact]
    public async Task RunEverythingAsync_ViaProcessFactory_ParsesOutput()
    {
        // Force es.exe backend and use a mock process
        FileLister.Backend = FileListerBackend.EsExe;
        var dir = Path.GetTempPath().TrimEnd('\\');

        var lister = new FileLister((path, psi) => new FakeProcess(
            lines: new[] { dir + @"\file1.txt", dir + @"\file2.cs", "" },
            exitCode: 0));

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(dir, Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Equal(2, files.Count);
        Assert.Contains(dir + @"\file1.txt", files);
        Assert.Contains(dir + @"\file2.cs", files);
    }

    [Fact]
    public async Task RunEverythingAsync_ExitCode8_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EsExe;
        var dir = Path.GetTempPath().TrimEnd('\\');

        var lister = new FileLister((path, psi) => new FakeProcess(
            lines: Array.Empty<string>(),
            exitCode: 8));

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(dir, Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Empty(files);
        Assert.Equal("Everything is not running", lister.FallbackReason);
    }

    [Fact]
    public async Task TryGetEverythingResultCount_SetsKnownTotalFiles()
    {
        FileLister.Backend = FileListerBackend.EsExe;
        var dir = Path.GetTempPath().TrimEnd('\\');

        // The count process returns "42", then the main process returns lines
        int callCount = 0;
        var lister = new FileLister((path, psi) =>
        {
            callCount++;
            if (psi.ArgumentList.Contains("-get-result-count"))
                return new FakeProcess(lines: new[] { "42" }, exitCode: 0);
            return new FakeProcess(
                lines: new[] { dir + @"\a.txt", dir + @"\b.txt" },
                exitCode: 0);
        });

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(dir, Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Equal(42, lister.KnownTotalFiles);
    }

    [Fact]
    public async Task RunEverythingAsync_ProcessStartFails_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EsExe;
        var dir = Path.GetTempPath().TrimEnd('\\');

        var lister = new FileLister((path, psi) => new ThrowingProcess());

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(dir, Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Empty(files);
        Assert.Contains("could not start", lister.FallbackReason);
    }

    [Fact]
    public void BuildEverythingFileNameFilter_QuotesLiteralTermsAsOrGroup()
    {
        Assert.Equal("\"target\"", FileLister.BuildEverythingFileNameFilter(["target"]));
        Assert.Equal("<\"alpha\"|\"beta\">", FileLister.BuildEverythingFileNameFilter(["alpha", "beta"]));
        Assert.Null(FileLister.BuildEverythingFileNameFilter(["bad\"term"]));
    }

    private sealed class FakeProcess : FileLister.IProcess
    {
        private readonly string[] _lines;
        private int _index;
        public int ExitCode { get; }

        public FakeProcess(string[] lines, int exitCode)
        {
            _lines = lines;
            ExitCode = exitCode;
        }

        public void Start() { }
        public Task<string?> ReadLineAsync(CancellationToken ct)
        {
            if (_index >= _lines.Length) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(_lines[_index++]);
        }
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingProcess : FileLister.IProcess
    {
        public int ExitCode => -1;
        public void Start() => throw new InvalidOperationException("Process start failed");
        public Task<string?> ReadLineAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
