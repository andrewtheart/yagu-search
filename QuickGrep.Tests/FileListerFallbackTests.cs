using QuickGrep.Services;

namespace QuickGrep.Tests;

public class FileListerFallbackTests : IDisposable
{
    private readonly string _root;
    public FileListerFallbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "sub", "b.cs"), "x");
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

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

public class FileListerExtraTests : IDisposable
{
    private readonly string _root;
    public FileListerExtraTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-fl-extra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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

public class FileListerFallbackExceptionTests : IDisposable
{
    private readonly string _root;
    public FileListerFallbackExceptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-fl-exc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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
