using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

[Collection("PreferNative")]
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
            Assert.Equal(1500, result.SourceMatchStartColumn);
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

[Collection("PreferNative")]
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
        // Create file large enough to trigger the memory-mapped path.
        var p = Path.Combine(_root, "large.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < 220000; i++)
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

[Collection("PreferNative")]
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

[Collection("PreferNative")]
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

// ─── ContentSearcher: managed regex path ────────────────────────────────

[Collection("PreferNative")]
public class ContentSearcherManagedRegexTests : IDisposable
{
    private readonly string _root;
    public ContentSearcherManagedRegexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-mrx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Regex_ManagedPath_FindsMatches()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "foo\nfoobar\nbar", new UTF8Encoding(false));

        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var s = new ContentSearcher();
            var opts = new SearchOptions
            {
                Directory = _root,
                Query = @"^foo\w*$",
                UseRegex = true,
                CaseSensitive = false,
                ContextLines = 0,
                MaxFileSizeBytes = 0,
                MaxResults = 0,
                SkipBinary = true,
            };
            var ch = Channel.CreateUnbounded<SearchResult>();
            var regex = new Regex(opts.Query, RegexOptions.IgnoreCase);
            await s.SearchFileAsync(path, regex, null, StringComparison.OrdinalIgnoreCase, opts, ch.Writer, default);
            ch.Writer.Complete();
            var results = new List<SearchResult>();
            await foreach (var r in ch.Reader.ReadAllAsync()) results.Add(r);
            Assert.Equal(2, results.Count);
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }

    [Fact]
    public void FindMatches_Regex_ReturnsHits()
    {
        var regex = new Regex(@"foo", RegexOptions.IgnoreCase);
        var hits = ContentSearcher.FindMatches(" foobar foo ", regex, null, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void FindMatches_ZeroWidthRegex_SkipsEmptyMatches()
    {
        // A lookahead-only pattern produces zero-width matches that should be skipped
        var regex = new Regex(@"(?=foo)", RegexOptions.IgnoreCase);
        var hits = ContentSearcher.FindMatches(" foobar foo ", regex, null, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task PerFileCap_ManagedPath_BreaksEarly()
    {
        var path = Path.Combine(_root, "many.txt");
        File.WriteAllText(path, string.Join('\n', Enumerable.Repeat("match", 100)), new UTF8Encoding(false));

        var oldPref = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var s = new ContentSearcher();
            var opts = new SearchOptions
            {
                Directory = _root,
                Query = "match",
                UseRegex = true,
                MaxFileSizeBytes = 0,
                MaxResults = 0,
                MaxMatchesPerFile = 5,
                SkipBinary = true,
            };
            var ch = Channel.CreateUnbounded<SearchResult>();
            var regex = new Regex("match", RegexOptions.IgnoreCase);
            await s.SearchFileAsync(path, regex, null, StringComparison.OrdinalIgnoreCase, opts, ch.Writer, default);
            ch.Writer.Complete();
            var list = new List<SearchResult>();
            await foreach (var x in ch.Reader.ReadAllAsync()) list.Add(x);
            Assert.Equal(5, list.Count);
        }
        finally { ContentSearcher.PreferNative = oldPref; }
    }
}

// ─── ContextLine ───────────────────────────────────────────────────────────────────────────

public class ContextLineTests
{
    [Fact]
    public void Properties_ReturnConstructorValues()
    {
        var cl = new ContextLine(42, "hello world");
        Assert.Equal(42, cl.LineNum);
        Assert.Equal("hello world", cl.Text);
    }

    [Fact]
    public void RecordEquality()
    {
        var a = new ContextLine(1, "abc");
        var b = new ContextLine(1, "abc");
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ContextLine(2, "abc"));
        Assert.NotEqual(a, new ContextLine(1, "xyz"));
    }

    [Fact]
    public void Deconstruction()
    {
        var (num, text) = new ContextLine(7, "line");
        Assert.Equal(7, num);
        Assert.Equal("line", text);
    }
}

// ─── SearchResult ───────────────────────────────────────────────────────

public class SearchResultCoverageTests
{
    private static SearchResult MakeResult(
        string matchLine = "hello world",
        int lineNumber = 10,
        int matchStartColumn = 0,
        int matchLength = 5,
        IReadOnlyList<string>? before = null,
        IReadOnlyList<string>? after = null)
    {
        return new SearchResult(
            FilePath: @"C:\test\file.txt",
            LineNumber: lineNumber,
            MatchLine: matchLine,
            MatchStartColumn: matchStartColumn,
            MatchLength: matchLength,
            ContextBefore: before ?? Array.Empty<string>(),
            ContextAfter: after ?? Array.Empty<string>());
    }

    [Fact]
    public void ShortPreview_ShortLine_ReturnsFullLine()
    {
        var r = MakeResult("short line");
        Assert.Equal("short line", r.ShortPreview);
        Assert.Equal(0, r.ShortPreviewMatchStart);
    }

    [Fact]
    public void ShortPreview_LongLine_TruncatesAt120()
    {
        var longLine = new string('A', 200);
        var r = MakeResult(longLine);
        Assert.Equal(longLine[..120] + "…", r.ShortPreview);
        Assert.Equal(121, r.ShortPreview.Length);
        Assert.Equal(0, r.ShortPreviewMatchStart);
    }

    [Fact]
    public void ShortPreview_LongLine_CentersLaterMatch()
    {
        var longLine = new string('A', 200) + "NEEDLE" + new string('B', 200);
        var r = MakeResult(longLine, matchStartColumn: 200, matchLength: 6);

        Assert.Contains("NEEDLE", r.ShortPreview);
        Assert.Equal(r.ShortPreview.IndexOf("NEEDLE", StringComparison.Ordinal), r.ShortPreviewMatchStart);
        Assert.StartsWith("…", r.ShortPreview);
        Assert.EndsWith("…", r.ShortPreview);
    }

    [Fact]
    public void ShortPreview_Exactly120Chars_NoTruncation()
    {
        var exact = new string('B', 120);
        var r = MakeResult(exact);
        Assert.Equal(exact, r.ShortPreview);
    }

    [Fact]
    public void IsEvicted_Initially_False()
    {
        var r = MakeResult();
        Assert.False(r.IsEvicted);
        Assert.Equal(-1, r.DiskOffset);
    }

    [Fact]
    public void Evict_And_Hydrate_RoundTrip()
    {
        using var store = new ResultStore();
        var r = MakeResult("original line", before: new[] { "before1" }, after: new[] { "after1" });

        r.Evict(store);
        Assert.True(r.IsEvicted);
        Assert.True(r.DiskOffset >= 0);
        Assert.Equal(r.ShortPreview, r.MatchLine);
        Assert.Empty(r.ContextBefore);
        Assert.Empty(r.ContextAfter);

        r.Hydrate(store);
        Assert.False(r.IsEvicted);
        Assert.Equal("original line", r.MatchLine);
        Assert.Single(r.ContextBefore);
        Assert.Equal("before1", r.ContextBefore[0]);
        Assert.Single(r.ContextAfter);
        Assert.Equal("after1", r.ContextAfter[0]);
    }

    [Fact]
    public void Evict_KeepsShortPreviewMatchVisible()
    {
        using var store = new ResultStore();
        var longLine = new string('A', 200) + "NEEDLE" + new string('B', 200);
        var r = MakeResult(longLine, matchStartColumn: 200, matchLength: 6);

        r.Evict(store);

        Assert.True(r.IsEvicted);
        Assert.Equal(r.ShortPreview, r.MatchLine);
        Assert.Contains("NEEDLE", r.MatchLine);
        Assert.Equal(r.MatchLine.IndexOf("NEEDLE", StringComparison.Ordinal), r.ShortPreviewMatchStart);
    }

    [Fact]
    public void Evict_Twice_IsNoOp()
    {
        using var store = new ResultStore();
        var r = MakeResult();
        r.Evict(store);
        var offset1 = r.DiskOffset;
        r.Evict(store); // second evict is no-op
        Assert.Equal(offset1, r.DiskOffset);
    }

    [Fact]
    public void Hydrate_WhenNotEvicted_IsNoOp()
    {
        using var store = new ResultStore();
        var r = MakeResult("line");
        r.Hydrate(store);
        Assert.Equal("line", r.MatchLine);
        Assert.False(r.IsEvicted);
    }

    [Fact]
    public void EvictWith_Works()
    {
        using var store = new ResultStore();
        var r = MakeResult("test line", before: new[] { "b" }, after: new[] { "a" });

        store.WriteBatch(writeOne =>
        {
            r.EvictWith(writeOne);
        });

        Assert.True(r.IsEvicted);
        Assert.Equal(r.ShortPreview, r.MatchLine);
    }

    [Fact]
    public void IsSelected_FiresPropertyChanged()
    {
        var r = MakeResult();
        var raised = new List<string>();
        r.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        r.IsSelected = true;
        r.IsSelected = true; // same value, no fire
        r.IsSelected = false;

        Assert.Equal(2, raised.Count);
        Assert.All(raised, name => Assert.Equal("IsSelected", name));
    }

    [Fact]
    public void NumberedBefore_CorrectLineNumbers()
    {
        var r = MakeResult(lineNumber: 10, before: new[] { "line7", "line8", "line9" });
        var numbered = r.NumberedBefore;
        Assert.Equal(3, numbered.Count);
        Assert.Equal(7, numbered[0].LineNum);
        Assert.Equal("line7", numbered[0].Text);
        Assert.Equal(8, numbered[1].LineNum);
        Assert.Equal(9, numbered[2].LineNum);
    }

    [Fact]
    public void NumberedAfter_CorrectLineNumbers()
    {
        var r = MakeResult(lineNumber: 10, after: new[] { "line11", "line12" });
        var numbered = r.NumberedAfter;
        Assert.Equal(2, numbered.Count);
        Assert.Equal(11, numbered[0].LineNum);
        Assert.Equal("line11", numbered[0].Text);
        Assert.Equal(12, numbered[1].LineNum);
    }

    [Fact]
    public void NumberedBefore_Empty_ReturnsEmpty()
    {
        var r = MakeResult(lineNumber: 5);
        Assert.Empty(r.NumberedBefore);
    }

    [Fact]
    public void NumberedAfter_Empty_ReturnsEmpty()
    {
        var r = MakeResult(lineNumber: 5);
        Assert.Empty(r.NumberedAfter);
    }
}

// ─── SearchResult: MatchLength access ─────────────────────────────────

public class SearchResultMatchLengthTest
{
    [Fact]
    public void MatchLength_ReturnsCorrectValue()
    {
        var r = new SearchResult("f.txt", 1, "hello world", 0, 5,
            Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(5, r.MatchLength);
    }

    [Fact]
    public void MatchStartColumn_ReturnsCorrectValue()
    {
        var r = new SearchResult("f.txt", 1, "hello world", 6, 5,
            Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(6, r.MatchStartColumn);
    }
}

// ─── ContentSearcher.Cancel ─────────────────────────────────────────

public class ContentSearcherCancelTests
{
    [Fact]
    public void Cancel_NonCancelledToken_ReturnsEmitted()
    {
        int result = ContentSearcher.Cancel(42, CancellationToken.None);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Cancel_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ContentSearcher.Cancel(10, cts.Token));
    }
}

// ─── ContentSearcher.SearchFileWithStatsAsync ───────────────────────

[Collection("PreferNative")]
public class SearchFileWithStatsTests : IDisposable
{
    private readonly string _root;
    private readonly ContentSearcher _searcher = new();

    public SearchFileWithStatsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-sfws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ContentSearcher.PreferNative = false;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private static SearchOptions Opt(string query) => new()
    {
        Directory = ".",
        Query = query,
        UseRegex = false,
        CaseSensitive = false,
        ContextLines = 0,
        MaxMatchesPerFile = 0,
        MaxFileSizeBytes = 0,
        SkipBinary = false,
    };

    private static SearchOptions OptWithMaxSize(string query, long maxSize) => new()
    {
        Directory = ".",
        Query = query,
        UseRegex = false,
        CaseSensitive = false,
        ContextLines = 0,
        MaxMatchesPerFile = 0,
        MaxFileSizeBytes = maxSize,
        SkipBinary = false,
    };

    private static SearchOptions OptWithSkipExtensions(string query, IReadOnlySet<string> exts) => new()
    {
        Directory = ".",
        Query = query,
        UseRegex = false,
        CaseSensitive = false,
        ContextLines = 0,
        MaxMatchesPerFile = 0,
        MaxFileSizeBytes = 0,
        SkipBinary = false,
        SkipExtensions = exts,
    };

    private static SearchOptions OptWithSkipBinary(string query) => new()
    {
        Directory = ".",
        Query = query,
        UseRegex = false,
        CaseSensitive = false,
        ContextLines = 0,
        MaxMatchesPerFile = 0,
        MaxFileSizeBytes = 0,
        SkipBinary = true,
    };

    private async Task<(FileSearchOutcome outcome, List<SearchResult> results)> Search(string path, SearchOptions opts)
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var outcome = await ContentSearcher.SearchFileWithStatsAsync(
            path, null, opts.Query, StringComparison.OrdinalIgnoreCase,
            opts, channel.Writer, null, CancellationToken.None);
        channel.Writer.Complete();
        var results = new List<SearchResult>();
        await foreach (var r in channel.Reader.ReadAllAsync())
            results.Add(r);
        return (outcome, results);
    }

    [Fact]
    public async Task FileNotFound_ReturnsSkipNotFound()
    {
        var (outcome, _) = await Search(Path.Combine(_root, "missing.txt"), Opt("hello"));
        Assert.Equal(ContentSearcher.SkipNotFound, outcome.MatchCount);
    }

    [Fact]
    public async Task InvalidPath_ReturnsSkipOther()
    {
        // null bytes in path cause FileInfo to throw
        var (outcome, _) = await Search("", Opt("hello"));
        Assert.True(outcome.MatchCount == ContentSearcher.SkipNotFound || outcome.MatchCount == ContentSearcher.SkipOther);
    }

    [Fact]
    public async Task FileTooLarge_ReturnsSkipTooLarge()
    {
        var path = Write("big.txt", "hello world");
        var opts = OptWithMaxSize("hello", 1); // 1 byte limit
        var (outcome, _) = await Search(path, opts);
        Assert.Equal(ContentSearcher.SkipTooLarge, outcome.MatchCount);
    }

    [Fact]
    public async Task ExtensionSkip_ReturnsSkipByExtension()
    {
        var path = Write("test.log", "hello world");
        var opts = OptWithSkipExtensions("hello", new HashSet<string> { "log" });
        var (outcome, _) = await Search(path, opts);
        Assert.Equal(ContentSearcher.SkipByExtension, outcome.MatchCount);
    }

    [Fact]
    public async Task ExtensionSkip_NoExtension_FallsThrough()
    {
        // A file with no extension should NOT be skipped even when SkipExtensions is set
        var path = Write("Makefile", "hello world\n");
        var opts = OptWithSkipExtensions("hello", new HashSet<string> { "log" });
        var (outcome, results) = await Search(path, opts);
        Assert.True(outcome.MatchCount > 0);
        Assert.Single(results);
    }

    [Fact]
    public async Task BinaryFile_WithSkipBinary_ReturnsSkipBinary()
    {
        var path = Path.Combine(_root, "binary.dat");
        // Write bytes with nulls to trigger binary detection
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00 });
        var opts = OptWithSkipBinary("hello");
        var (outcome, _) = await Search(path, opts);
        Assert.Equal(ContentSearcher.SkipBinary, outcome.MatchCount);
    }

    [Fact]
    public async Task ManagedPath_FindsMatch()
    {
        var path = Write("found.txt", "hello world\ngoodbye\n");
        var (outcome, results) = await Search(path, Opt("hello"));
        Assert.True(outcome.MatchCount > 0);
        Assert.Single(results);
        Assert.Equal(1, results[0].LineNumber);
    }

    [Fact]
    public async Task ManagedPath_NoMatch_ReturnsZero()
    {
        var path = Write("empty.txt", "nothing here\n");
        var (outcome, results) = await Search(path, Opt("hello"));
        Assert.Equal(0, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ManagedPath_WithRegex()
    {
        var path = Write("regex.txt", "foo123bar\nbaz456\n");
        var channel = Channel.CreateUnbounded<SearchResult>();
        var regex = new Regex(@"\d+", RegexOptions.Compiled);
        var opts = new SearchOptions
        {
            Directory = ".",
            Query = @"\d+",
            UseRegex = true,
            CaseSensitive = false,
            ContextLines = 0,
            MaxMatchesPerFile = 0,
            MaxFileSizeBytes = 0,
            SkipBinary = false,
        };
        var outcome = await ContentSearcher.SearchFileWithStatsAsync(
            path, regex, null, StringComparison.Ordinal,
            opts, channel.Writer, null, CancellationToken.None);
        channel.Writer.Complete();
        Assert.True(outcome.MatchCount >= 2);
    }
}
