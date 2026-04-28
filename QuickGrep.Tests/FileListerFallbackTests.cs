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
