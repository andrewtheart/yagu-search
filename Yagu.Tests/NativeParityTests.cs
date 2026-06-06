using System.IO;
using System.Threading;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Verifies behavioural parity between the Rust native scan path and the pure
/// managed fallback. These tests run twice — once with the native path forced
/// on, once forced off — and assert identical match counts and line numbers.
/// </summary>
[Collection("PreferNative")]
public class NativeParityTests : IDisposable
{
    private readonly string _root;

    public NativeParityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static async Task<List<SearchResult>> RunAsync(string file, SearchOptions opts)
    {
        var ch = Channel.CreateUnbounded<SearchResult>();
        var s = new ContentSearcher();
        System.Text.RegularExpressions.Regex? rx = null;
        string? literal = opts.Query;
        if (opts.UseRegex)
        {
            var ro = System.Text.RegularExpressions.RegexOptions.Compiled |
                     System.Text.RegularExpressions.RegexOptions.CultureInvariant;
            if (!opts.CaseSensitive) ro |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            rx = new System.Text.RegularExpressions.Regex(opts.Query, ro);
            literal = null;
        }

        var produced = await s.SearchFileAsync(file, regex: rx, literal: literal,
            literalComparison: opts.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            options: opts, writer: ch.Writer, cancellationToken: CancellationToken.None);
        ch.Writer.Complete();
        var list = new List<SearchResult>();
        await foreach (var r in ch.Reader.ReadAllAsync()) list.Add(r);
        // produced == -1 means file was skipped (binary / too large) — list will be empty.
        if (produced >= 0) Assert.Equal(list.Count, produced);
        return list;
    }

    private async Task<(List<SearchResult> native, List<SearchResult> managed)> RunBothAsync(string file, SearchOptions opts)
    {
        ContentSearcher.PreferNative = true;
        var nativeResults = await RunAsync(file, opts);
        ContentSearcher.PreferNative = false;
        try
        {
            var managedResults = await RunAsync(file, opts);
            return (nativeResults, managedResults);
        }
        finally
        {
            ContentSearcher.PreferNative = true;
        }
    }

    [Fact]
    public async Task LiteralCaseInsensitive_Parity()
    {
        var f = Write("a.txt", "Foo bar\nbar foo\nNO MATCH HERE\nFOOFOO\n");
        var opts = new SearchOptions { Directory = _root, Query = "foo", CaseSensitive = false, ContextLines = 0 };
        var (native, managed) = await RunBothAsync(f, opts);
        Assert.Equal(managed.Count, native.Count);
        Assert.Equal(managed.Select(r => r.LineNumber), native.Select(r => r.LineNumber));
    }

    [Fact]
    public async Task LiteralCaseSensitive_Parity()
    {
        var f = Write("b.txt", "Foo\nfoo\nFOO\n");
        var opts = new SearchOptions { Directory = _root, Query = "foo", CaseSensitive = true, ContextLines = 0 };
        var (native, managed) = await RunBothAsync(f, opts);
        Assert.Single(native);
        Assert.Equal(managed.Count, native.Count);
    }

    [Fact]
    public async Task ContextLines_Parity()
    {
        var f = Write("c.txt", "1\n2\n3\nMATCH\n4\n5\n6\n");
        var opts = new SearchOptions { Directory = _root, Query = "MATCH", CaseSensitive = true, ContextLines = 2 };
        var (native, managed) = await RunBothAsync(f, opts);
        Assert.Single(native);
        Assert.Equal(managed[0].ContextBefore, native[0].ContextBefore);
        Assert.Equal(managed[0].ContextAfter, native[0].ContextAfter);
    }

    [Fact]
    public async Task Regex_Parity()
    {
        var f = Write("d.txt", "alpha 12\nbeta 345\ngamma\ndelta 0\n");
        var opts = new SearchOptions { Directory = _root, Query = @"\d+", UseRegex = true, ContextLines = 0 };
        var (native, managed) = await RunBothAsync(f, opts);
        Assert.Equal(3, native.Count);
        Assert.Equal(managed.Select(r => r.LineNumber), native.Select(r => r.LineNumber));
    }

    [Fact]
    public async Task BinaryFile_BothSkip()
    {
        var p = Path.Combine(_root, "bin.dat");
        File.WriteAllBytes(p, new byte[] { 0x41, 0x00, 0x42, 0x00, 0x43 });
        var opts = new SearchOptions { Directory = _root, Query = "A", SkipBinary = true, ContextLines = 0 };
        var (native, managed) = await RunBothAsync(p, opts);
        Assert.Empty(native);
        Assert.Empty(managed);
    }

    [Fact]
    public async Task LargeFile_NativeMatches()
    {
        // 2MB file with a needle near the end — exercises the mmap path.
        var p = Path.Combine(_root, "big.txt");
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 50_000; i++) sb.Append("padding line ").Append(i).Append('\n');
        sb.Append("THE_NEEDLE_IS_HERE\n");
        File.WriteAllText(p, sb.ToString());

        var opts = new SearchOptions { Directory = _root, Query = "THE_NEEDLE_IS_HERE", CaseSensitive = true, ContextLines = 1 };
        ContentSearcher.PreferNative = true;
        var hits = await RunAsync(p, opts);
        Assert.Single(hits);
        Assert.Equal("THE_NEEDLE_IS_HERE", hits[0].MatchLine);
    }

    [Fact]
    public async Task LongSingleLine_ParityKeepsMatchVisible()
    {
        var f = Write("long-one-line.txt", new string('a', 2500) + "THE_NEEDLE" + new string('b', 2500));
        var opts = new SearchOptions { Directory = _root, Query = "THE_NEEDLE", CaseSensitive = true, ContextLines = 0 };

        var (native, managed) = await RunBothAsync(f, opts);

        var nativeHit = Assert.Single(native);
        var managedHit = Assert.Single(managed);
        Assert.Contains("THE_NEEDLE", nativeHit.MatchLine);
        Assert.Equal(nativeHit.MatchLine.IndexOf("THE_NEEDLE", StringComparison.Ordinal), nativeHit.MatchStartColumn);
        Assert.Equal(2500, nativeHit.SourceMatchStartColumn);
        Assert.Equal(managedHit.SourceMatchStartColumn, nativeHit.SourceMatchStartColumn);
        Assert.Contains("THE_NEEDLE", managedHit.MatchLine);
        Assert.Equal(managedHit.MatchLine.IndexOf("THE_NEEDLE", StringComparison.Ordinal), managedHit.MatchStartColumn);
    }
}
