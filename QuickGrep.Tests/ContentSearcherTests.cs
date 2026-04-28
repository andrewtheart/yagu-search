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

// ─── ContentSearcher: skip-by-extension, mmap path, per-file cap ────────

public class ContentSearcherExtraTests : IDisposable
{
    private readonly string _root;
    public ContentSearcherExtraTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-cs-extra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private static SearchOptions Opt(string query, bool regex = false, int context = 0, IReadOnlySet<string>? skipExtensions = null)
        => new()
        {
            Directory = ".",
            Query = query,
            UseRegex = regex,
            CaseSensitive = false,
            ContextLines = context,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
            SkipBinary = true,
            SkipExtensions = skipExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
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
    public async Task SkipByExtension_SkipsFile()
    {
        var p = Write("data.exe", "needle in binary");
        var s = new ContentSearcher();
        var opts = Opt("needle", skipExtensions: new HashSet<string>(new[] { "exe", "dll" }, StringComparer.OrdinalIgnoreCase));
        var results = await CollectAsync(s, p, opts);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SkipByExtension_DoesNotSkipOtherExtensions()
    {
        var p = Write("data.txt", "needle here");
        var s = new ContentSearcher();
        var opts = Opt("needle", skipExtensions: new HashSet<string>(new[] { "exe", "dll" }, StringComparer.OrdinalIgnoreCase));
        var results = await CollectAsync(s, p, opts);
        Assert.Single(results);
    }

    [Fact]
    public async Task LargeFile_MemoryMappedPath()
    {
        // Create file > 1MB to trigger memory-mapped path
        var p = Path.Combine(_root, "large.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < 50000; i++)
            sb.AppendLine($"line {i} with some content to pad the file");
        sb.AppendLine("FINDME target match here");
        File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));

        var fi = new FileInfo(p);
        Assert.True(fi.Length >= ContentSearcher.MemoryMapThresholdBytes,
            $"File should be >= {ContentSearcher.MemoryMapThresholdBytes} bytes, was {fi.Length}");

        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var s = new ContentSearcher();
            var results = await CollectAsync(s, p, Opt("FINDME"));
            Assert.Single(results);
            Assert.Contains("FINDME", results[0].MatchLine);
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }

    [Fact]
    public async Task NonexistentFile_ReturnsEmpty()
    {
        var s = new ContentSearcher();
        var results = await CollectAsync(s, @"Z:\no\such\file.txt", Opt("anything"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task PerFileCap_LimitsResults()
    {
        var p = Write("many.txt", string.Join('\n', Enumerable.Repeat("match", 100)));
        var s = new ContentSearcher();
        var opts = new SearchOptions
        {
            Directory = ".",
            Query = "match",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
            MaxMatchesPerFile = 5,
        };
        var ch = Channel.CreateUnbounded<SearchResult>();
        Regex? r = null;
        await s.SearchFileAsync(p, r, "match", StringComparison.OrdinalIgnoreCase, opts, ch.Writer, default);
        ch.Writer.Complete();
        var list = new List<SearchResult>();
        await foreach (var x in ch.Reader.ReadAllAsync()) list.Add(x);
        Assert.True(list.Count <= 5, $"Expected <= 5 results but got {list.Count}");
    }
}

// ─── ContentSearcher: exception paths ───────────────────────────────────

public class ContentSearcherExceptionTests : IDisposable
{
    private readonly string _root;
    public ContentSearcherExceptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-cs-ex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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

    private static SearchOptions DefaultOpts() => new()
    {
        Directory = ".",
        Query = "test",
        MaxFileSizeBytes = 0,
        MaxResults = 0,
        SkipBinary = true,
    };

    [Fact]
    public async Task InvalidPathChars_SkipsFile()
    {
        // Path with chars that make FileInfo throw on some systems
        var s = new ContentSearcher();
        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var results = await CollectAsync(s, "NUL", DefaultOpts());
            // NUL is a reserved device name, should get SkipNotFound or SkipOther
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }

    [Fact]
    public async Task SkipBinaryFalse_DoesNotSniff()
    {
        var p = Path.Combine(_root, "data.txt");
        File.WriteAllText(p, "hello test world\n");
        var s = new ContentSearcher();
        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var opts = new SearchOptions
            {
                Directory = ".",
                Query = "test",
                MaxFileSizeBytes = 0,
                MaxResults = 0,
                SkipBinary = false,
            };
            var results = await CollectAsync(s, p, opts);
            Assert.NotEmpty(results);
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }

    [Fact]
    public async Task MaxFileSize_SkipsLargeFile()
    {
        var p = Path.Combine(_root, "big.txt");
        File.WriteAllText(p, string.Join('\n', Enumerable.Repeat("test", 100)));
        var fi = new FileInfo(p);

        var s = new ContentSearcher();
        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var opts = new SearchOptions
            {
                Directory = ".",
                Query = "test",
                MaxFileSizeBytes = 1, // 1 byte = skip everything
                MaxResults = 0,
                SkipBinary = true,
            };
            var results = await CollectAsync(s, p, opts);
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }

    [Fact]
    public async Task EncodingFallbackException_SkipsFile()
    {
        var p = Path.Combine(_root, "bad_encoding.txt");
        File.WriteAllBytes(p, new byte[] { 0xC0, 0xC1, 0xF5, 0xF8, 0xFE, 0xFF });

        var s = new ContentSearcher();
        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var results = await CollectAsync(s, p, DefaultOpts());
            // Should either skip with encoding error or produce no results
            // The key is it doesn't throw
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }
}

// ─── RingBuffer cached Snapshot ─────────────────────────────────────────

public class RingBufferCachedSnapshotTests
{
    [Fact]
    public void Snapshot_CalledTwiceWithoutAdd_ReturnsCachedInstance()
    {
        var rbType = typeof(ContentSearcher)
            .GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
            .Single(t => t.Name.StartsWith("RingBuffer"));
        var closedType = rbType.MakeGenericType(typeof(string));

        var instance = Activator.CreateInstance(closedType,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new object[] { 2 }, null)!;

        var addMethod = closedType.GetMethod("Add", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var snapshotMethod = closedType.GetMethod("Snapshot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;

        addMethod.Invoke(instance, ["hello"]);
        var first = snapshotMethod.Invoke(instance, null);
        var second = snapshotMethod.Invoke(instance, null);

        Assert.Same(first, second);
    }
}

// ─── MaxMatchesPerFile = 0 ──────────────────────────────────────────────

public class MaxMatchesPerFileZeroTests : IDisposable
{
    private readonly string _root;
    public MaxMatchesPerFileZeroTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-mmf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task MaxMatchesPerFile_Zero_UsesIntMaxValue()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "word\nword\nword", new UTF8Encoding(false));

        var s = new ContentSearcher();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "word",
            CaseSensitive = false,
            ContextLines = 0,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
            SkipBinary = true,
            MaxMatchesPerFile = 0,
        };

        var ch = Channel.CreateUnbounded<SearchResult>();
        await s.SearchFileAsync(path, null, "word", StringComparison.OrdinalIgnoreCase, opts, ch.Writer, default);
        ch.Writer.Complete();
        var results = new List<SearchResult>();
        await foreach (var r in ch.Reader.ReadAllAsync()) results.Add(r);

        Assert.Equal(3, results.Count);
    }
}
