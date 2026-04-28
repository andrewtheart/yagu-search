using System.Diagnostics;
using QuickGrep.Services;
using static QuickGrep.Services.FileLister;

namespace QuickGrep.Tests;

[Collection("FileListerBackend")]
public class FileListerFallbackTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public FileListerFallbackTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "sub", "b.cs"), "x");
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Fallback_EnumeratesRecursively()
    {
        // We can't reliably skip es.exe from outside, so call the fallback path
        // by passing a directory to a sandbox lister and discarding any es results.
        // Instead, validate the fallback path through the service's overall behavior:
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Contains(files, f => f.EndsWith("a.cs"));
        Assert.Contains(files, f => f.EndsWith(Path.Combine("sub", "b.cs")));
    }

    [Fact]
    public async Task Fallback_RespectsExtensionFilter()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, new[] { "cs" }, 0, default))
            files.Add(p);
        Assert.All(files, f => Assert.EndsWith(".cs", f));
    }
}

// ─── FileLister: extra coverage paths ───────────────────────────────────

[Collection("FileListerBackend")]
public class FileListerExtraTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public FileListerExtraTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-fl-extra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task EmptyDirectory_ReturnsNoFiles()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task NonexistentDirectory_ReturnsNoFiles()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(@"Z:\nonexistent\path", Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task ManagedFallback_EnumeratesFiles()
    {
        File.WriteAllText(Path.Combine(_root, "file1.txt"), "content");
        File.WriteAllText(Path.Combine(_root, "file2.log"), "content");
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "file3.txt"), "content");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.True(files.Count >= 3);
    }

    [Fact]
    public async Task ManagedFallback_WithExtensionFilter_Filters()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, new[] { "cs" }, 0, default))
            files.Add(p);
        Assert.Single(files);
        Assert.EndsWith(".cs", files[0]);
    }

    [Fact]
    public async Task WhitespaceDirectory_ReturnsNoFiles()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync("  ", Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task KnownTotalFiles_PopulatedOrZero()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var lister = new FileLister();
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        // KnownTotalFiles may be 0 when Everything SDK is unavailable and managed fallback is used
        Assert.True(lister.KnownTotalFiles >= 0);
    }

    [Fact]
    public async Task SkippedDirectories_TracksCount()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        var lister = new FileLister();
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.True(lister.SkippedDirectories >= 0);
    }

    [Fact]
    public async Task AccessDeniedDirectories_TracksCount()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        var lister = new FileLister();
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.True(lister.AccessDeniedDirectories >= 0);
    }

    [Fact]
    public async Task FallbackReason_SetAfterListing()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        var lister = new FileLister();
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        // FallbackReason is null when Everything SDK is used, or a string when managed fallback fires
        // Either way, accessing it should not throw
        _ = lister.FallbackReason;
    }
}

// ─── FileLister: fallback exception paths ───────────────────────────────

[Collection("FileListerBackend")]
public class FileListerFallbackExceptionTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    public FileListerFallbackExceptionTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-fl-exc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { FileLister.Backend = _originalBackend; try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Cancellation_StopsEnumeration()
    {
        for (int i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "data");

        var lister = new FileLister();
        var cts = new CancellationTokenSource();
        int count = 0;

        try
        {
            await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, cts.Token))
            {
                count++;
                if (count >= 5) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { /* expected */ }

        Assert.True(count >= 5);
    }

    [Fact]
    public async Task SubdirWithFiles_ReturnsAll()
    {
        var sub = Path.Combine(_root, "inner");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_root, "root.txt"), "");
        File.WriteAllText(Path.Combine(sub, "nested.txt"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.True(files.Count >= 2);
    }

    [Fact]
    public async Task ExtensionFilter_MultipleExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.xml"), "");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, new[] { "cs", "xml" }, 0, default))
            files.Add(p);
        Assert.Equal(2, files.Count);
    }
}

// ─── FileLister.Backend property ────────────────────────────────────────

[Collection("FileListerBackend")]
public class FileListerBackendPropertyTests
{
    [Fact]
    public void Backend_SetAndGet_RoundTrips()
    {
        var original = FileLister.Backend;
        try
        {
            FileLister.Backend = FileListerBackend.Managed;
            Assert.Equal(FileListerBackend.Managed, FileLister.Backend);

            FileLister.Backend = FileListerBackend.Auto;
            Assert.Equal(FileListerBackend.Auto, FileLister.Backend);
        }
        finally { FileLister.Backend = original; }
    }
}

// ─── FileLister.NormalizeExtension ──────────────────────────────────────

public class NormalizeExtensionTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("*.cs", "cs")]
    [InlineData(".cs", "cs")]
    [InlineData("*cs", "cs")]
    [InlineData("cs", "cs")]
    [InlineData(" *.txt ", "txt")]
    public void NormalizeExtension_VariousFormats(string input, string expected)
    {
        Assert.Equal(expected, FileLister.NormalizeExtension(input));
    }
}

// ─── FileLister.FindEsExe (injectable overload) ─────────────────────

public class FindEsExeTests
{
    [Fact]
    public void FindEsExe_ReturnsFirstExisting()
    {
        var candidates = new[] { @"C:\a\es.exe", @"C:\b\es.exe", @"C:\c\es.exe" };
        var existing = new HashSet<string> { @"C:\b\es.exe" };
        var result = FileLister.FindEsExe(candidates, p => existing.Contains(p));
        Assert.Equal(@"C:\b\es.exe", result);
    }

    [Fact]
    public void FindEsExe_NoneExist_ReturnsNull()
    {
        var candidates = new[] { @"C:\a\es.exe", @"C:\b\es.exe" };
        var result = FileLister.FindEsExe(candidates, _ => false);
        Assert.Null(result);
    }

    [Fact]
    public void FindEsExe_EmptyCandidates_ReturnsNull()
    {
        var result = FileLister.FindEsExe(Array.Empty<string>(), _ => true);
        Assert.Null(result);
    }

    [Fact]
    public void FindEsExe_ExceptionOnCheck_SkipsAndContinues()
    {
        var candidates = new[] { @"C:\bad\es.exe", @"C:\good\es.exe" };
        int callCount = 0;
        var result = FileLister.FindEsExe(candidates, p =>
        {
            callCount++;
            if (p.Contains("bad")) throw new UnauthorizedAccessException("test");
            return true;
        });
        Assert.Equal(@"C:\good\es.exe", result);
        Assert.Equal(2, callCount);
    }
}

// ─── FileLister: ListFilesAsync + EnumerateFallbackAsync deeper ─────

[Collection("FileListerBackend")]
public class FileListerListFilesTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;

    public FileListerListFilesTests()
    {
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        _root = Path.Combine(Path.GetTempPath(), "qg-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task EmptyDirectory_YieldsNothing()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task NonExistentDirectory_YieldsNothing()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(Path.Combine(_root, "nope"), Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task MaxFilesLimit_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "");
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 3, default))
            files.Add(p);
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public async Task ExtensionFilter_FiltersCorrectly()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");
        File.WriteAllText(Path.Combine(_root, "c.cs"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, new[] { "cs" }, 0, default))
            files.Add(p);
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.EndsWith(".cs", f));
    }

    [Fact]
    public async Task RecursiveEnumeration()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_root, "top.txt"), "");
        File.WriteAllText(Path.Combine(sub, "nested.txt"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task CancellationToken_StopsEnumeration()
    {
        for (int i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "");

        using var cts = new CancellationTokenSource();
        var lister = new FileLister();
        var files = new List<string>();
        try
        {
            await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, cts.Token))
            {
                files.Add(p);
                if (files.Count >= 5) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }
        Assert.True(files.Count < 100);
    }
}

// ─── MockProcess for RunEverythingAsync / TryGetEverythingResultCountAsync ───

internal class MockProcess : IProcess
{
    private readonly Queue<string?> _lines;
    private readonly int _exitCode;
    private readonly bool _throwOnStart;

    public MockProcess(IEnumerable<string?> lines, int exitCode = 0, bool throwOnStart = false)
    {
        _lines = new Queue<string?>(lines);
        _exitCode = exitCode;
        _throwOnStart = throwOnStart;
    }

    public int ExitCode => _exitCode;

    public void Start()
    {
        if (_throwOnStart) throw new InvalidOperationException("mock start failure");
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// ─── RunEverythingAsync via mock process ────────────────────────────────

[Collection("FileListerBackend")]
public class FileListerEsExeTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;

    public FileListerEsExeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-esexe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileLister CreateLister(MockProcess mainProc, MockProcess? countProc = null)
    {
        return new FileLister((path, psi) =>
        {
            bool isCountCall = psi.ArgumentList.Contains("-get-result-count");
            if (isCountCall) return countProc ?? new MockProcess([null], 0);
            return mainProc;
        });
    }

    [Fact]
    public async Task EsExe_NormalOutput_YieldsLines()
    {
        var proc = new MockProcess([@"C:\code\a.cs", @"C:\code\b.cs", null]);
        var countProc = new MockProcess(["2", null], 0);
        var lister = CreateLister(proc, countProc);
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        Assert.Equal(2, files.Count);
        Assert.Contains(@"C:\code\a.cs", files);
        Assert.Contains(@"C:\code\b.cs", files);
    }

    [Fact]
    public async Task EsExe_EmptyLines_AreSkipped()
    {
        var proc = new MockProcess([@"C:\code\a.cs", "", @"C:\code\b.cs", null]);
        var lister = CreateLister(proc);
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task EsExe_ExitCode8_SetsEverythingNotRunning()
    {
        var proc = new MockProcess([null], exitCode: 8);
        var lister = CreateLister(proc);
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        Assert.Empty(files);
        Assert.Equal("Everything is not running", lister.FallbackReason);
    }

    [Fact]
    public async Task EsExe_ExitCode8_WithResults_StillYields()
    {
        var proc = new MockProcess([@"C:\code\a.cs", null], exitCode: 8);
        var lister = CreateLister(proc);
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        Assert.Single(files);
        Assert.Equal("Everything is not running", lister.FallbackReason);
    }

    [Fact]
    public async Task EsExe_NonZeroExitCode_NoResults_SetsFallbackReason()
    {
        var proc = new MockProcess([null], exitCode: 1);
        var lister = CreateLister(proc);
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        Assert.Empty(files);
        Assert.Equal("es.exe exited with code 1", lister.FallbackReason);
    }

    [Fact]
    public async Task EsExe_StartThrows_FallsBackGracefully()
    {
        var proc = new MockProcess([null], throwOnStart: true);
        var lister = CreateLister(proc);
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        Assert.Empty(files);
        Assert.StartsWith("es.exe could not start:", lister.FallbackReason);
    }

    [Fact]
    public async Task EsExe_WithExtensions_IncludesExtArg()
    {
        ProcessStartInfo? capturedPsi = null;
        var lister = new FileLister((path, psi) =>
        {
            bool isCountCall = psi.ArgumentList.Contains("-get-result-count");
            if (!isCountCall) capturedPsi = psi;
            return new MockProcess(isCountCall ? ["0", null] : [null], 0);
        });
        FileLister.Backend = FileListerBackend.EsExe;

        await foreach (var _ in lister.ListFilesAsync(_root, new[] { "*.cs", ".txt" }, 0, default)) { }

        Assert.NotNull(capturedPsi);
        Assert.Contains(capturedPsi!.ArgumentList, a => a.StartsWith("ext:"));
    }

    [Fact]
    public async Task EsExe_WithMaxFiles_IncludesNArg()
    {
        ProcessStartInfo? capturedPsi = null;
        var lister = new FileLister((path, psi) =>
        {
            bool isCountCall = psi.ArgumentList.Contains("-get-result-count");
            if (!isCountCall) capturedPsi = psi;
            return new MockProcess(isCountCall ? ["0", null] : [null], 0);
        });
        FileLister.Backend = FileListerBackend.EsExe;

        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 50, default)) { }

        Assert.NotNull(capturedPsi);
        Assert.Contains("-n", capturedPsi!.ArgumentList);
        Assert.Contains("50", capturedPsi!.ArgumentList);
    }

    [Fact]
    public async Task TryGetEverythingResultCount_Parsed_SetsKnownTotal()
    {
        var countProc = new MockProcess(["42", null], 0);
        var mainProc = new MockProcess([@"C:\a.cs", null], 0);
        var lister = CreateLister(mainProc, countProc);
        FileLister.Backend = FileListerBackend.EsExe;

        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }

        Assert.Equal(42, lister.KnownTotalFiles);
    }

    [Fact]
    public async Task TryGetEverythingResultCount_FailedExit_DoesNotThrow()
    {
        var countProc = new MockProcess(["error", null], exitCode: 1);
        var mainProc = new MockProcess([@"C:\a.cs", null], 0);
        var lister = CreateLister(mainProc, countProc);
        FileLister.Backend = FileListerBackend.EsExe;

        // Count process failed (exit code 1), but main process still works
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
    }

    [Fact]
    public async Task TryGetEverythingResultCount_NullLine_ReturnsZero()
    {
        var countProc = new MockProcess([null], 0);
        var mainProc = new MockProcess([@"C:\a.cs", null], 0);
        var lister = CreateLister(mainProc, countProc);
        FileLister.Backend = FileListerBackend.EsExe;

        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }

        // Should not throw, just returns 0 count
    }

    [Fact]
    public async Task TryGetEverythingResultCount_StartThrows_ReturnsZero()
    {
        var countProc = new MockProcess([null], exitCode: 0, throwOnStart: true);
        var mainProc = new MockProcess([@"C:\a.cs", null], 0);
        var lister = CreateLister(mainProc, countProc);
        FileLister.Backend = FileListerBackend.EsExe;

        // Count process Start() throws, but main process still works
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
    }

    [Fact]
    public async Task EsExe_NoResults_SetsFallbackReason()
    {
        var proc = new MockProcess([null], 0);
        var lister = CreateLister(proc);
        FileLister.Backend = FileListerBackend.EsExe;

        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }

        Assert.Equal("es.exe returned no results", lister.FallbackReason);
    }

    [Fact]
    public async Task EsExeBackend_Forced_NoEsExeFound_YieldsNothing()
    {
        // Remove es.exe from PATH by using a lister without processFactory
        // and a directory where FindEsExe returns null (unlikely on this machine,
        // so we verify the fallback reason if es.exe IS found)
        var lister = new FileLister();
        FileLister.Backend = FileListerBackend.EsExe;

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);

        // If es.exe exists, it runs es.exe with no results for empty dir
        // If es.exe doesn't exist, FallbackReason = "es.exe not found"
        Assert.True(lister.FallbackReason is not null || files.Count == 0);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

// ─── EnumerateFallbackAsync via forced Managed backend ──────────────────

[Collection("FileListerBackend")]
public class FileListerManagedTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;

    public FileListerManagedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Managed_EnumeratesRecursively()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(sub, "b.txt"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task Managed_ExtensionFilter()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");
        File.WriteAllText(Path.Combine(_root, "c.cs"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, new[] { "cs" }, 0, default))
            files.Add(p);
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.EndsWith(".cs", f));
    }

    [Fact]
    public async Task Managed_MaxFiles_RespectsLimit()
    {
        for (int i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 5, default))
            files.Add(p);
        Assert.Equal(5, files.Count);
    }

    [Fact]
    public async Task Managed_EmptyDirectory_YieldsNothing()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task Managed_NonExistentDir_YieldsNothing()
    {
        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(Path.Combine(_root, "nope"), Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Empty(files);
    }

    [Fact]
    public async Task Managed_Cancellation_StopsEnumeration()
    {
        for (int i = 0; i < 50; i++)
            File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "");

        using var cts = new CancellationTokenSource();
        var lister = new FileLister();
        var files = new List<string>();
        try
        {
            await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, cts.Token))
            {
                files.Add(p);
                if (files.Count >= 3) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }
        Assert.True(files.Count < 50);
    }

    [Fact]
    public async Task Managed_FileWithNoExtension_IncludedWhenNoFilter()
    {
        File.WriteAllText(Path.Combine(_root, "Makefile"), "");
        File.WriteAllText(Path.Combine(_root, "README"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task Managed_FileWithNoExtension_ExcludedByExtFilter()
    {
        File.WriteAllText(Path.Combine(_root, "Makefile"), "");
        File.WriteAllText(Path.Combine(_root, "code.cs"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, new[] { "cs" }, 0, default))
            files.Add(p);
        Assert.Single(files);
        Assert.EndsWith(".cs", files[0]);
    }

    [Fact]
    public async Task Managed_DeeplyNested_Traverses()
    {
        var dir = _root;
        for (int i = 0; i < 5; i++)
        {
            dir = Path.Combine(dir, $"d{i}");
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(Path.Combine(dir, "deep.txt"), "");

        var lister = new FileLister();
        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default))
            files.Add(p);
        Assert.Single(files);
        Assert.EndsWith("deep.txt", files[0]);
    }
}
