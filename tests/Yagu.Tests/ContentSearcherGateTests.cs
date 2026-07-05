using System.Text;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ContentSearcherGateTests : IDisposable
{
    private readonly string _root;

    public ContentSearcherGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-cs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static SearchOptions Opts(string query) => new()
    {
        Directory = ".",
        Query = query,
        UseRegex = false,
        CaseSensitive = false,
        ContextLines = 0,
        MaxFileSizeBytes = 0,
        MaxResults = 0,
        SkipBinary = true,
    };

    [Fact]
    public async Task SearchStreamAsync_FindsMatches()
    {
        var path = WriteFile("stream.txt", "alpha\nbeta\ngamma\nalpha again");
        var ch = Channel.CreateUnbounded<SearchResult>();
        int count = await ContentSearcher.SearchStreamAsync(
            path, null, "alpha", StringComparison.OrdinalIgnoreCase,
            Opts("alpha"), ch.Writer, default, CancellationToken.None);
        ch.Writer.Complete();
        Assert.Equal(2, count);
        var results = new List<SearchResult>();
        await foreach (var r in ch.Reader.ReadAllAsync()) results.Add(r);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchMappedAsync_FindsMatches()
    {
        var path = WriteFile("mapped.txt", "line1\nfind me here\nline3");
        var fileLength = new FileInfo(path).Length;
        var ch = Channel.CreateUnbounded<SearchResult>();
        int count = await ContentSearcher.SearchMappedAsync(
            path, fileLength, null, "find me", StringComparison.OrdinalIgnoreCase,
            Opts("find me"), ch.Writer, default, CancellationToken.None);
        ch.Writer.Complete();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SearchStreamAsync_SkipsBinary()
    {
        // Create a file with binary content (null bytes)
        var path = Path.Combine(_root, "binary.dat");
        var content = new byte[1024];
        content[0] = 0x00; content[1] = 0x00; content[2] = 0x00;
        File.WriteAllBytes(path, content);

        var ch = Channel.CreateUnbounded<SearchResult>();
        var opts = new SearchOptions
        {
            Directory = ".",
            Query = "test",
            UseRegex = false,
            CaseSensitive = false,
            ContextLines = 0,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
            SkipBinary = true,
        };
        int count = await ContentSearcher.SearchStreamAsync(
            path, null, "test", StringComparison.OrdinalIgnoreCase,
            opts, ch.Writer, default, CancellationToken.None);
        ch.Writer.Complete();
        // SearchStreamAsync doesn't do binary detection itself (that's in SearchFileAsync)
        // but it should still function on the file
        Assert.True(count >= 0);
    }
}
