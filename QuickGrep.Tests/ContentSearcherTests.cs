using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using QuickGrep.Helpers;
using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Tests;

public class ContentSearcherTests : IDisposable
{
    private readonly string _root;
    public ContentSearcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private static SearchOptions Opt(string query, bool regex = false, bool caseSensitive = false, int context = 0)
        => new()
        {
            Directory = ".",
            Query = query,
            UseRegex = regex,
            CaseSensitive = caseSensitive,
            ContextLines = context,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
            SkipBinary = true,
        };

    private static async Task<List<SearchResult>> CollectAsync(ContentSearcher s, string path, SearchOptions opts)
    {
        var ch = Channel.CreateUnbounded<SearchResult>();
        Regex? r = opts.UseRegex ? new Regex(opts.Query, opts.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) : null;
        var literal = opts.UseRegex ? null : opts.Query;
        var cmp = opts.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        await s.SearchFileAsync(path, r, literal, cmp, opts, ch.Writer, default);
        ch.Writer.Complete();
        var list = new List<SearchResult>();
        await foreach (var x in ch.Reader.ReadAllAsync()) list.Add(x);
        return list;
    }

    [Fact]
    public async Task LiteralCaseInsensitive_FindsAllMatches()
    {
        var p = Write("a.txt", "Hello World\nhello again\nHELLO");
        var s = new ContentSearcher();
        var results = await CollectAsync(s, p, Opt("hello"));
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task LiteralCaseSensitive_OnlyExactCase()
    {
        var p = Write("a.txt", "Hello\nhello\nHELLO");
        var s = new ContentSearcher();
        var results = await CollectAsync(s, p, Opt("hello", caseSensitive: true));
        Assert.Single(results);
        Assert.Equal(2, results[0].LineNumber);
    }

    [Fact]
    public async Task Regex_Matches()
    {
        var p = Write("a.txt", "foo\nfoobar\nbar");
        var s = new ContentSearcher();
        var results = await CollectAsync(s, p, Opt(@"^foo\w*$", regex: true));
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task ContextLines_Captured()
    {
        var p = Write("a.txt", "1\n2\n3\nMATCH\n5\n6\n7");
        var s = new ContentSearcher();
        var results = await CollectAsync(s, p, Opt("MATCH", context: 2));
        Assert.Single(results);
        Assert.Equal(new[] { "2", "3" }, results[0].ContextBefore);
        Assert.Equal(new[] { "5", "6" }, results[0].ContextAfter);
    }

    [Fact]
    public async Task MultipleMatchesPerLine_AllReturned()
    {
        var p = Write("a.txt", "ab ab ab");
        var s = new ContentSearcher();
        var results = await CollectAsync(s, p, Opt("ab", caseSensitive: true));
        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { 0, 3, 6 }, results.Select(r => r.MatchStartColumn));
    }

    [Fact]
    public async Task LongSingleLine_MatchLineWindowIncludesMatch()
    {
        var p = Write("long.txt", new string('a', 1500) + "NEEDLE" + new string('b', 1500));
        var oldPreferNative = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var s = new ContentSearcher();
            var results = await CollectAsync(s, p, Opt("NEEDLE", caseSensitive: true));

            var result = Assert.Single(results);
            Assert.Contains("NEEDLE", result.MatchLine);
            Assert.Equal(result.MatchLine.IndexOf("NEEDLE", StringComparison.Ordinal), result.MatchStartColumn);
            Assert.StartsWith(LineTruncator.Ellipsis, result.MatchLine);
            Assert.EndsWith(LineTruncator.Ellipsis, result.MatchLine);
        }
        finally
        {
            ContentSearcher.PreferNative = oldPreferNative;
        }
    }

    [Fact]
    public async Task MaxFileSize_SkipsLargeFiles()
    {
        var p = Write("big.txt", new string('x', 1000));
        var s = new ContentSearcher();
        var custom = new SearchOptions
        {
            Directory = ".",
            Query = "x",
            MaxFileSizeBytes = 10,
            MaxResults = 0,
        };
        var results = await CollectAsync(s, p, custom);
        Assert.Empty(results);
    }

    [Fact]
    public async Task BinaryFiles_Skipped()
    {
        var p = Path.Combine(_root, "bin.dat");
        File.WriteAllBytes(p, new byte[] { 0x68, 0x69, 0x00, 0x68, 0x69 });
        var s = new ContentSearcher();
        var results = await CollectAsync(s, p, Opt("hi"));
        Assert.Empty(results);
    }
}
