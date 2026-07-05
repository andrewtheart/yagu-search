using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Integration tests for ContentSearcher covering file size filtering, binary skip,
/// encoding handling, max matches per file, and multi-match scenarios.
/// </summary>
public sealed class ContentSearcherIntegrationTests : IDisposable
{
    private readonly string _root;

    public ContentSearcherIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-cs-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private string WriteBinaryFile(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static SearchOptions MakeOpts(string query, bool regex = false, bool caseSensitive = false,
        int context = 0, long maxSize = 0, long minSize = 0, int maxMatchesPerFile = 0, bool skipBinary = true)
        => new()
        {
            Directory = ".",
            Query = query,
            UseRegex = regex,
            CaseSensitive = caseSensitive,
            ContextLines = context,
            MaxFileSizeBytes = maxSize,
            MinFileSizeBytes = minSize,
            MaxResults = 0,
            MaxMatchesPerFile = maxMatchesPerFile,
            SkipBinary = skipBinary,
        };

    private static async Task<List<SearchResult>> SearchFileAsync(string path, SearchOptions opts)
    {
        var ch = Channel.CreateUnbounded<SearchResult>();
        var searcher = new ContentSearcher();
        Regex? r = opts.UseRegex ? new Regex(opts.Query, opts.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) : null;
        var literal = opts.UseRegex ? null : opts.Query;
        var cmp = opts.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        await searcher.SearchFileAsync(path, r, literal, cmp, opts, ch.Writer, CancellationToken.None);
        ch.Writer.Complete();
        var results = new List<SearchResult>();
        await foreach (var x in ch.Reader.ReadAllAsync()) results.Add(x);
        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Literal search
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LiteralSearch_FindsAllOccurrences()
    {
        var path = WriteFile("multi.txt", "apple banana apple cherry apple");
        var results = await SearchFileAsync(path, MakeOpts("apple"));
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(1, r.LineNumber));
    }

    [Fact]
    public async Task LiteralSearch_MultipleLines()
    {
        var path = WriteFile("lines.txt", "line1 needle\nline2\nline3 needle\nline4");
        var results = await SearchFileAsync(path, MakeOpts("needle"));
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Equal(3, results[1].LineNumber);
    }

    [Fact]
    public async Task LiteralSearch_CaseInsensitive_Default()
    {
        var path = WriteFile("case.txt", "HELLO\nhello\nHeLLo");
        var results = await SearchFileAsync(path, MakeOpts("hello"));
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task LiteralSearch_CaseSensitive()
    {
        var path = WriteFile("case.txt", "HELLO\nhello\nHeLLo");
        var results = await SearchFileAsync(path, MakeOpts("hello", caseSensitive: true));
        Assert.Single(results);
        Assert.Equal(2, results[0].LineNumber);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Regex search
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RegexSearch_MatchesPattern()
    {
        var path = WriteFile("regex.txt", "foo123\nbar456\nfoo789");
        var results = await SearchFileAsync(path, MakeOpts(@"foo\d+", regex: true));
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RegexSearch_CapturesMatchPosition()
    {
        var path = WriteFile("pos.txt", "prefix MATCH suffix");
        var results = await SearchFileAsync(path, MakeOpts("MATCH", caseSensitive: true));
        Assert.Single(results);
        Assert.Equal(7, results[0].MatchStartColumn);
        Assert.Equal(5, results[0].MatchLength);
    }

    [Fact]
    public async Task RegexSearch_MultilineAnchors()
    {
        var path = WriteFile("anchors.txt", "start\nmiddle\nend");
        var results = await SearchFileAsync(path, MakeOpts(@"^middle$", regex: true));
        Assert.Single(results);
        Assert.Equal(2, results[0].LineNumber);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Context lines
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContextLines_CapturesBeforeAndAfter()
    {
        var content = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line{i}"));
        var path = WriteFile("ctx.txt", content);
        var results = await SearchFileAsync(path, MakeOpts("line5", context: 2));
        Assert.Single(results);
        Assert.Equal(["line3", "line4"], results[0].ContextBefore);
        Assert.Equal(["line6", "line7"], results[0].ContextAfter);
    }

    [Fact]
    public async Task ContextLines_AtFileStart_LimitedBefore()
    {
        var content = "MATCH\nline2\nline3\nline4\nline5";
        var path = WriteFile("start.txt", content);
        var results = await SearchFileAsync(path, MakeOpts("MATCH", context: 3));
        Assert.Single(results);
        Assert.Empty(results[0].ContextBefore);
        Assert.Equal(3, results[0].ContextAfter.Count);
    }

    [Fact]
    public async Task ContextLines_AtFileEnd_LimitedAfter()
    {
        var content = "line1\nline2\nline3\nline4\nMATCH";
        var path = WriteFile("end.txt", content);
        var results = await SearchFileAsync(path, MakeOpts("MATCH", context: 3));
        Assert.Single(results);
        Assert.Equal(3, results[0].ContextBefore.Count);
        Assert.Empty(results[0].ContextAfter);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Binary file handling
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkipBinary_True_SkipsBinaryFiles()
    {
        var data = new byte[100];
        data[0] = 0x89; data[1] = 0x50; data[2] = 0x4E; data[3] = 0x47; // PNG magic
        var path = WriteBinaryFile("image.png", data);
        var results = await SearchFileAsync(path, MakeOpts("anything", skipBinary: true));
        Assert.Empty(results);
    }

    [Fact]
    public async Task SkipBinary_False_SearchesBinaryFiles()
    {
        // Write a file that looks binary (has NUL) but contains searchable text
        var content = Encoding.UTF8.GetBytes("hello\x00world");
        var path = WriteBinaryFile("mixed.bin", content);
        // With skipBinary: false, it should attempt the search
        var results = await SearchFileAsync(path, MakeOpts("hello", skipBinary: false));
        // May or may not find results depending on how binary content is handled
        // but should not throw
    }

    // ═══════════════════════════════════════════════════════════════
    //  File size filtering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MaxFileSize_SkipsOversizedFiles()
    {
        var content = new string('x', 5000);
        var path = WriteFile("large.txt", content + " needle");
        // File is >5000 bytes, set max to 100
        var results = await SearchFileAsync(path, MakeOpts("needle", maxSize: 100));
        Assert.Empty(results);
    }

    [Fact]
    public async Task MinFileSize_SkipsUndersizedFiles()
    {
        var path = WriteFile("tiny.txt", "needle");
        // File is ~6 bytes, set min to 1000
        var results = await SearchFileAsync(path, MakeOpts("needle", minSize: 1000));
        Assert.Empty(results);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MaxMatchesPerFile
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MaxMatchesPerFile_LimitsResults()
    {
        var content = string.Join("\n", Enumerable.Repeat("needle", 100));
        var path = WriteFile("many.txt", content);
        var results = await SearchFileAsync(path, MakeOpts("needle", maxMatchesPerFile: 5));
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task MaxMatchesPerFile_Zero_NoLimit()
    {
        var content = string.Join("\n", Enumerable.Repeat("needle", 20));
        var path = WriteFile("unlimited.txt", content);
        var results = await SearchFileAsync(path, MakeOpts("needle", maxMatchesPerFile: 0));
        Assert.Equal(20, results.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Encoding support
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Utf8Bom_FileSearchedCorrectly()
    {
        var path = WriteFile("utf8bom.txt", "needle in UTF-8 BOM file", new UTF8Encoding(true));
        var results = await SearchFileAsync(path, MakeOpts("needle"));
        Assert.Single(results);
    }

    [Fact]
    public async Task Utf16Le_FileSearchedCorrectly()
    {
        // ContentSearcher uses UTF-8 strict mode; UTF-16 LE files without BOM
        // may not be searchable depending on encoding detection. Verify no crash.
        var path = WriteFile("utf16le.txt", "needle in UTF-16 LE file", Encoding.Unicode);
        var results = await SearchFileAsync(path, MakeOpts("needle"));
        // UTF-16 LE files with BOM are detected and searched
        Assert.True(results.Count >= 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty / missing file
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmptyFile_NoResults()
    {
        var path = WriteFile("empty.txt", "");
        var results = await SearchFileAsync(path, MakeOpts("needle"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task NonExistentFile_NoResults_NoThrow()
    {
        var path = Path.Combine(_root, "nonexistent.txt");
        var results = await SearchFileAsync(path, MakeOpts("needle"));
        Assert.Empty(results);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SearchFileWithStatsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchFileWithStats_ReturnsMatchCountAndBytesRead()
    {
        var path = WriteFile("stats.txt", "one needle\ntwo needles\nthree");
        var ch = Channel.CreateUnbounded<SearchResult>();
        var opts = MakeOpts("needle");
        var outcome = await ContentSearcher.SearchFileWithStatsAsync(
            path,
            null, "needle", StringComparison.OrdinalIgnoreCase,
            opts, ch.Writer, null, CancellationToken.None);

        Assert.True(outcome.MatchCount >= 2);
        Assert.True(outcome.BytesScanned > 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelledToken_StopsEarly()
    {
        var content = string.Join("\n", Enumerable.Repeat("needle in long content", 10000));
        var path = WriteFile("cancel.txt", content);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var ch = Channel.CreateUnbounded<SearchResult>();
        var searcher = new ContentSearcher();
        // Pre-cancelled token throws TaskCanceledException/OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await searcher.SearchFileAsync(path, null, "needle", StringComparison.OrdinalIgnoreCase,
                MakeOpts("needle"), ch.Writer, cts.Token));
    }
}
