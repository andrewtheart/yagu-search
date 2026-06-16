using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Integration tests for SearchService: full end-to-end search pipeline including
/// file listing, content searching, and result collection with various configurations.
/// </summary>
public sealed class SearchServiceIntegrationTests : IDisposable
{
    private readonly string _root;

    public SearchServiceIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-ss-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private SearchOptions MakeOpts(string query, bool regex = false, bool caseSensitive = false,
        int context = 0, SearchMode mode = SearchMode.Content, string? includes = null, string? excludes = null)
        => new()
        {
            Directory = _root,
            Query = query,
            UseRegex = regex,
            CaseSensitive = caseSensitive,
            ContextLines = context,
            SearchMode = mode,
            MaxFileSizeBytes = 0,
            MaxResults = 50_000,
            MaxMatchesPerFile = 0,
            SkipBinary = true,
            IncludeGlobs = includes?.Split(',').Select(s => s.Trim()).ToArray() ?? [],
            ExcludeGlobs = excludes?.Split(',').Select(s => s.Trim()).ToArray() ?? [],
        };

    private async Task<SearchResultCollection> RunSearchAsync(SearchOptions opts, CancellationToken ct = default)
    {
        var collection = new SearchResultCollection();
        var service = new SearchService();

        await foreach (var evt in service.SearchAsync(opts, ct))
        {
            if (evt is SearchEvent.MatchBatch batch)
            {
                collection.AddRange(batch.Results);
            }
            else if (evt is SearchEvent.Match match)
            {
                collection.Add(match.Result);
            }
        }

        return collection;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Basic content search
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContentSearch_FindsMatchesAcrossFiles()
    {
        WriteFile("a.txt", "hello world");
        WriteFile("b.txt", "hello there");
        WriteFile("c.txt", "goodbye");

        var results = await RunSearchAsync(MakeOpts("hello"));
        Assert.Equal(2, results.AllGroups.Count);
    }

    [Fact]
    public async Task ContentSearch_CaseSensitive_FiltersCorrectly()
    {
        WriteFile("a.txt", "Hello");
        WriteFile("b.txt", "hello");
        WriteFile("c.txt", "HELLO");

        var results = await RunSearchAsync(MakeOpts("hello", caseSensitive: true));
        Assert.Single(results.AllGroups);
    }

    [Fact]
    public async Task ContentSearch_RegexPattern()
    {
        WriteFile("a.txt", "foo123");
        WriteFile("b.txt", "foo");
        WriteFile("c.txt", "bar456");

        var results = await RunSearchAsync(MakeOpts(@"foo\d+", regex: true));
        Assert.Single(results.AllGroups);
    }

    // ═══════════════════════════════════════════════════════════════
    //  File name search mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileNameSearch_MatchesByFileName()
    {
        WriteFile("needle-file.txt", "no match here");
        WriteFile("other.txt", "needle content");

        var results = await RunSearchAsync(MakeOpts("needle", mode: SearchMode.FileNames));
        // Should find needle-file.txt by name
        Assert.Contains(results.AllGroups, g => g.FileName.Contains("needle"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Include/exclude globs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task IncludeGlobs_FiltersToMatchingFiles()
    {
        WriteFile("code.cs", "needle");
        WriteFile("readme.txt", "needle");

        var results = await RunSearchAsync(MakeOpts("needle", includes: "*.cs"));
        Assert.Single(results.AllGroups);
        Assert.EndsWith(".cs", results.AllGroups[0].FilePath);
    }

    [Fact]
    public async Task ExcludeGlobs_RemovesMatchingFiles()
    {
        WriteFile("src/code.cs", "needle");
        WriteFile("node_modules/pkg/index.js", "needle");

        var results = await RunSearchAsync(MakeOpts("needle", excludes: "node_modules"));
        Assert.Single(results.AllGroups);
        Assert.Contains("src", results.AllGroups[0].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Context lines
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContextLines_CapturedInResults()
    {
        WriteFile("ctx.txt", "1\n2\n3\nNEEDLE\n5\n6\n7");
        var results = await RunSearchAsync(MakeOpts("NEEDLE", context: 2));
        var match = results.AllGroups.SelectMany(g => g).First();
        Assert.Equal(2, match.ContextBefore.Count);
        Assert.Equal(2, match.ContextAfter.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Subdirectory search
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubdirectorySearch_FindsNestedFiles()
    {
        WriteFile("a/b/c/deep.txt", "needle");
        WriteFile("shallow.txt", "needle");

        var results = await RunSearchAsync(MakeOpts("needle"));
        Assert.Equal(2, results.AllGroups.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty query
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmptyQuery_NoResults()
    {
        WriteFile("a.txt", "content");
        var results = await RunSearchAsync(MakeOpts(""));
        Assert.Empty(results.AllGroups);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelledSearch_StopsEarly()
    {
        // Create many files to make search take longer
        for (int i = 0; i < 100; i++)
            WriteFile($"file{i}.txt", $"needle content {i}");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pre-cancelled token throws OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await RunSearchAsync(MakeOpts("needle"), cts.Token));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Binary file skipping
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BinaryFiles_SkippedByDefault()
    {
        WriteFile("text.txt", "needle");
        var binaryPath = Path.Combine(_root, "binary.png");
        File.WriteAllBytes(binaryPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.UTF8.GetBytes("needle")]);

        var results = await RunSearchAsync(MakeOpts("needle"));
        Assert.Single(results.AllGroups);
        Assert.Contains("text.txt", results.AllGroups[0].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Both mode (file name + content)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BothMode_MatchesFileNameAndContent()
    {
        WriteFile("needle-named.txt", "nothing here");
        WriteFile("other.txt", "has needle inside");

        var results = await RunSearchAsync(MakeOpts("needle", mode: SearchMode.Both));
        Assert.Equal(2, results.AllGroups.Count);
    }
}
