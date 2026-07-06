using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Phase 1 (managed MVP) tests for cross-line (multiline) search: the LF-shadow helper, the
/// centralized regex factory, span-aware <see cref="SearchResult"/> fields, the dedicated multiline
/// parallelism, the <see cref="ContentSearcher.SearchMultilineAsync"/> engine, and source-pins for
/// the UI/VM/CLI wiring that <c>Yagu.Tests</c> cannot compile in.
/// </summary>
[Collection("PreferNative")]
public sealed class MultilineSearchTests : IDisposable
{
    private readonly string _root;
    public MultilineSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-ml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // Default to the native engine so the parity/validation tests reliably exercise the Phase 2
        // path (the collection serializes PreferNative toggles; managed-specific tests opt back out).
        ContentSearcher.PreferNative = true;
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private string WriteBytes(string name, byte[] content)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, content);
        return p;
    }

    private static SearchOptions MultilineOpt(string pattern, bool dotAll = false, bool caseSensitive = false, int context = 0, long cap = SearchOptions.DefaultMaxMultilineBytes, bool skipBinary = true, int maxResults = 0, int maxMatchesPerFile = 0, MultilineEngineKind engine = MultilineEngineKind.Regex)
        => new()
        {
            Directory = ".",
            Query = pattern,
            UseRegex = true,
            Multiline = true,
            MultilineDotAll = dotAll,
            MultilineEngine = engine,
            CaseSensitive = caseSensitive,
            ContextLines = context,
            MaxResults = maxResults,
            SkipBinary = skipBinary,
            MaxMultilineBytes = cap,
            MaxMatchesPerFile = maxMatchesPerFile,
        };

    private async Task<(FileSearchOutcome outcome, List<SearchResult> results)> RunMultiline(string path, SearchOptions opts)
    {
        var regex = SearchRegexFactory.Build(opts.Query, opts);
        return await RunMultilineWithRegex(path, opts, regex);
    }

    private async Task<(FileSearchOutcome outcome, List<SearchResult> results)> RunMultilineWithRegex(string path, SearchOptions opts, Regex? regex)
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var outcome = await ContentSearcher.SearchFileWithStatsAsync(
            path, regex, null, StringComparison.Ordinal, opts, channel.Writer, session: null, CancellationToken.None);
        channel.Writer.Complete();
        var results = new List<SearchResult>();
        await foreach (var r in channel.Reader.ReadAllAsync())
            results.Add(r);
        return (outcome, results);
    }

    // ── Native parity (Phase 2) ─────────────────────────────────────

    private async Task<List<SearchResult>> RunWithPreferNative(string path, SearchOptions opts, bool preferNative)
    {
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = preferNative;
        try { return (await RunMultiline(path, opts)).results; }
        finally { ContentSearcher.PreferNative = prev; }
    }

    // Projection normalized for order-independent comparison (parallelism/engine can reorder).
    private static List<(int line, int startCol, int len, int? endLine, int? endCol, string matchLine, int srcCol)> Project(List<SearchResult> rs)
        => rs.Select(r => (r.LineNumber, r.MatchStartColumn, r.MatchLength, r.MatchEndLineNumber, r.MatchEndColumn, r.MatchLine, r.SourceMatchStartColumn))
             .OrderBy(t => t.LineNumber).ThenBy(t => t.MatchStartColumn).ToList();

    [Theory]
    [InlineData("xx foo\nbar yy\nzz\n", "foo.bar", true)]           // cross-line dotall
    [InlineData("foo\r\nbar\r\n", "foo$", false)]                   // CRLF, $ excludes '\r'
    [InlineData("foo\r\nbar\r\n", "foo\\nbar", false)]              // CRLF cross-line span
    [InlineData("end\nmiddle\nend\n", "end$", false)]              // multiline $ anchor, two hits
    [InlineData("café foo\nbar\n", "foo.bar", true)]               // non-ASCII start line (UTF-16 col)
    [InlineData("A1\nB A2\nB tail\n", "A[\\s\\S]*?B", false)]       // lazy cross-line, two occurrences
    [InlineData("no match here\n", "zzz", false)]                   // no match
    [InlineData("a💩b foo\nbar\n", "foo.bar", true)]               // astral char before match
    public async Task Multiline_NativeMatchesManaged_Parity(string content, string pattern, bool dotAll)
    {
        if (!Yagu.Native.NativeSearcher.IsAvailable) return; // native required for a real comparison
        var p = Write("parity.txt", content);
        var opts = MultilineOpt(pattern, dotAll: dotAll);
        var native = Project(await RunWithPreferNative(p, opts, preferNative: true));
        var managed = Project(await RunWithPreferNative(p, opts, preferNative: false));
        Assert.Equal(managed, native);
    }

    [Theory]
    [InlineData("xx foo\nbar yy\nzz\n", "foo.bar", true)]
    [InlineData("foo\r\nbar\r\n", "foo\\nbar", false)]
    [InlineData("A1\nB A2\nB tail\n", "A[\\s\\S]*?B", false)]
    [InlineData("café foo\nbar\n", "foo.bar", true)]
    public async Task Multiline_GrepEngineMatchesRegexEngine_Parity(string content, string pattern, bool dotAll)
    {
        // The two native backends (regex::bytes default vs grep-searcher) must be byte-identical.
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("engineparity.txt", content);
        var regexEngine = Project(await RunWithPreferNative(p, MultilineOpt(pattern, dotAll: dotAll, engine: MultilineEngineKind.Regex), preferNative: true));
        var grepEngine = Project(await RunWithPreferNative(p, MultilineOpt(pattern, dotAll: dotAll, engine: MultilineEngineKind.Grep), preferNative: true));
        Assert.Equal(regexEngine, grepEngine);
    }

    [Fact]
    public async Task Multiline_NativeMatchesManaged_WithContext_Parity()
    {
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("parityctx.txt", "before1\nbefore2\nSTARTa\naEND\nafter1\nafter2\n");
        var opts = MultilineOpt("START[\\s\\S]*?END", context: 2);
        var native = await RunWithPreferNative(p, opts, preferNative: true);
        var managed = await RunWithPreferNative(p, opts, preferNative: false);
        Assert.Equal(Project(managed), Project(native));
        // After-context is numbered from the END line on both engines.
        var nr = Assert.Single(native);
        Assert.Equal(3, nr.LineNumber);
        Assert.Equal(4, nr.MatchEndLineNumber);
        Assert.Equal(new[] { "after1", "after2" }, nr.NumberedAfter.Select(c => c.Text));
    }

    [Fact]
    public async Task Multiline_Native_CatastrophicPatternCompletesWithoutTimeout()
    {
        // The native linear engine cannot catastrophically backtrack, so a pattern that would time
        // out in .NET completes and simply finds no match (no DoS, no timeout skip).
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("nativeslow.txt", new string('a', 40) + "!");
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = true; // force native — do not rely on the ambient default
        try
        {
            var (outcome, results) = await RunMultiline(p, MultilineOpt("(a+)+$"));
            Assert.Equal(0, outcome.MatchCount);
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_Native_LookaroundFallsThroughToManaged()
    {
        // A lookaround pattern is rejected by the native regex engine (StatusInvalidRegex), so the
        // native path falls through and the managed whole-file engine (which supports .NET lookbehind)
        // runs it. Exercises the native-fallthrough branch + the invalid-regex error-message plumbing.
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("lookaround.txt", "xADYx\nfoo\n");
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = true;
        try
        {
            // "(?<=A)D" — a 'D' preceded by 'A'. Valid in .NET, rejected by the Rust regex crate.
            var (_, results) = await RunMultiline(p, MultilineOpt("(?<=A)D"));
            var r = Assert.Single(results);
            Assert.Equal(1, r.LineNumber);
            Assert.Equal("xADYx", r.MatchLine);
        }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_Native_LockedFile_ReturnsAccessDeniedSkip()
    {
        // The native read of a FileShare.None-locked file fails to open (STATUS_OPEN_FAILED), which
        // the native multiline path maps to an access-denied skip (distinct from the managed engine's
        // IO-error skip). Exercises the TryNativeMultilineAsync StatusOpenFailed arm.
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("nativelocked.txt", "foo\nbar");
        using var hold = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = true;
        try
        {
            var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
            Assert.Equal(ContentSearcher.SkipAccessDenied, outcome.MatchCount);
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_Native_WatchedFile_TakesNativeExitCheckpoint()
    {
        // A watched file that succeeds through the native engine exercises the watched-diagnostics
        // checkpoint on the native multiline exit path.
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("nativewatched.txt", "foo\nbar\n");
        FileWatchDiagnostics.Add("nativewatched.txt");
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = true;
        try
        {
            var (_, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
            Assert.True(Assert.Single(results).IsMultilineMatch);
        }
        finally { ContentSearcher.PreferNative = prev; FileWatchDiagnostics.Clear(); }
    }

    [Fact]
    public async Task Multiline_Native_WatchedFile_Lookaround_TakesFallthroughCheckpoint()
    {
        // A watched file whose pattern the native engine rejects (lookaround) exercises the watched
        // diagnostic on the native-fallthrough path (distinct from the success-exit checkpoint).
        if (!Yagu.Native.NativeSearcher.IsAvailable) return;
        var p = Write("watchedfallthrough.txt", "xADYx\n");
        FileWatchDiagnostics.Add("watchedfallthrough.txt");
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = true;
        try
        {
            var (_, results) = await RunMultiline(p, MultilineOpt("(?<=A)D"));
            Assert.Equal("xADYx", Assert.Single(results).MatchLine); // managed engine handled it
        }
        finally { ContentSearcher.PreferNative = prev; FileWatchDiagnostics.Clear(); }
    }

    // ── MultilineTextShadow ─────────────────────────────────────────

    [Fact]
    public void Shadow_NoCarriageReturn_IsIdentity()
    {
        var shadow = MultilineTextShadow.Build("abc\ndef\nghi");
        Assert.True(shadow.IsIdentity);
        Assert.Equal("abc\ndef\nghi", shadow.Lf);
        Assert.Equal(5, shadow.ToOriginalOffset(5));
    }

    [Fact]
    public void Shadow_Crlf_CollapsesAndMapsBack()
    {
        var shadow = MultilineTextShadow.Build("a\r\nb");
        Assert.False(shadow.IsIdentity);
        Assert.Equal("a\nb", shadow.Lf);
        // Original: a(0) \r(1) \n(2) b(3). Shadow: a(0) \n(1) b(2).
        Assert.Equal(0, shadow.ToOriginalOffset(0));
        Assert.Equal(1, shadow.ToOriginalOffset(1)); // the '\r'
        Assert.Equal(3, shadow.ToOriginalOffset(2)); // 'b'
        Assert.Equal(4, shadow.ToOriginalOffset(3)); // end
    }

    [Fact]
    public void Shadow_LoneCr_IsLengthPreservingSubstitution()
    {
        var shadow = MultilineTextShadow.Build("a\rb");
        Assert.False(shadow.IsIdentity); // content changed (has a '\r')
        Assert.Equal("a\nb", shadow.Lf);
        // Lone '\r' -> '\n' does not shift offsets.
        Assert.Equal(2, shadow.ToOriginalOffset(2));
    }

    // ── SearchRegexFactory ──────────────────────────────────────────

    [Fact]
    public void RegexFactory_LineMode_IgnoreCaseNoMultilineNoTimeout()
    {
        var opts = SearchRegexFactory.ResolveOptions(caseSensitive: false, multiline: false, multilineDotAll: false);
        Assert.True(opts.HasFlag(RegexOptions.IgnoreCase));
        Assert.False(opts.HasFlag(RegexOptions.Multiline));
        Assert.False(opts.HasFlag(RegexOptions.Singleline));

        var rx = SearchRegexFactory.Build("foo", caseSensitive: false, multiline: false, multilineDotAll: false);
        Assert.Equal(Regex.InfiniteMatchTimeout, rx.MatchTimeout);
    }

    [Fact]
    public void RegexFactory_MultilineDotAll_SetsFlagsAndTimeout()
    {
        var opts = SearchRegexFactory.ResolveOptions(caseSensitive: true, multiline: true, multilineDotAll: true);
        Assert.False(opts.HasFlag(RegexOptions.IgnoreCase));
        Assert.True(opts.HasFlag(RegexOptions.Multiline));
        Assert.True(opts.HasFlag(RegexOptions.Singleline));

        var rx = SearchRegexFactory.Build("foo", caseSensitive: true, multiline: true, multilineDotAll: true);
        Assert.Equal(SearchRegexFactory.MultilineMatchTimeout, rx.MatchTimeout);
    }

    [Fact]
    public void RegexFactory_MultilineWithoutDotAll_HasNoSingleline()
    {
        var opts = SearchRegexFactory.ResolveOptions(caseSensitive: false, multiline: true, multilineDotAll: false);
        Assert.True(opts.HasFlag(RegexOptions.Multiline));
        Assert.False(opts.HasFlag(RegexOptions.Singleline));
    }

    // ── SearchOptions multiline plumbing ────────────────────────────

    [Fact]
    public void SearchOptions_MultilineDefaults()
    {
        var o = new SearchOptions { Directory = ".", Query = "x" };
        Assert.False(o.Multiline);
        Assert.False(o.MultilineDotAll);
        Assert.Equal(SearchOptions.DefaultMaxMultilineBytes, o.MaxMultilineBytes);
        Assert.Equal(50L * 1024 * 1024, SearchOptions.DefaultMaxMultilineBytes);
    }

    [Theory]
    [InlineData(8, 100L * 1024 * 1024 * 1024, 4)] // abundant memory -> clamp to 4
    [InlineData(8, 1L, 2)]                        // starved -> clamp to 2 (min)
    [InlineData(1, 100L * 1024 * 1024 * 1024, 1)] // single core -> 1
    public void ResolveMultilineParallelism_IsMemoryDerivedAndClamped(int cores, long avail, int expected)
    {
        int degree = SearchOptions.ResolveMultilineParallelism(cores, avail, SearchOptions.DefaultMaxMultilineBytes);
        Assert.Equal(expected, degree);
        Assert.True(degree >= 1);
    }

    // ── SearchResult span fields ────────────────────────────────────

    [Fact]
    public void SearchResult_SingleLine_HasNullSpanAndFolds()
    {
        var r = new SearchResult("f", 5, "line", 0, 3, Array.Empty<string>(), new[] { "a1", "a2" });
        Assert.False(r.IsMultilineMatch);
        Assert.Null(r.MatchEndLineNumber);
        // After-context numbers from LineNumber + 1 = 6.
        Assert.Equal(6, r.NumberedAfter[0].LineNum);
    }

    [Fact]
    public void SearchResult_Multiline_NumbersAfterFromEndLine()
    {
        var r = new SearchResult("f", 5, "line", 0, 3, Array.Empty<string>(), new[] { "a1", "a2" })
        {
            MatchEndLineNumber = 7,
            MatchEndColumn = 4,
        };
        Assert.True(r.IsMultilineMatch);
        Assert.Equal(7, r.MatchEndLineNumber);
        Assert.Equal(4, r.MatchEndColumn);
        // After-context numbers from (end line 7) + 1 = 8.
        Assert.Equal(8, r.NumberedAfter[0].LineNum);
        Assert.Equal(9, r.NumberedAfter[1].LineNum);
    }

    [Fact]
    public void SearchResult_MultilineResult_EvictsAndPreservesSpan()
    {
        var single = new SearchResult("f", 1, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>());
        Assert.True(single.TryBeginEviction()); // single-line evicts normally

        // Phase 1b removed the 1a in-memory pin: multiline results evict like any other result,
        // and their cross-line span rides along on the retained SearchResult across eviction.
        var multi = new SearchResult("f", 1, "START", 0, 5, Array.Empty<string>(), new[] { "after" })
        {
            MatchEndLineNumber = 3,
            MatchEndColumn = 2,
        };
        Assert.True(multi.TryBeginEviction());
        multi.CancelEvictionReservation();

        // Full retained-object eviction round-trip: span survives the disk write + hydrate.
        Assert.True(multi.EvictWith((ml, cb, ca) => 0L));
        Assert.True(multi.IsEvicted);
        Assert.Equal(3, multi.MatchEndLineNumber);
        Assert.Equal(2, multi.MatchEndColumn);

        multi.HydrateFrom("START", Array.Empty<string>(), new[] { "after" });
        Assert.False(multi.IsEvicted);
        Assert.Equal(3, multi.MatchEndLineNumber);
        Assert.Equal(2, multi.MatchEndColumn);
        Assert.True(multi.IsMultilineMatch);
    }

    [Fact]
    public void SearchResult_MultilineSpanLabel_ReflectsExtraLines()
    {
        var single = new SearchResult("f", 2, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(string.Empty, single.MultilineSpanLabel);

        var one = new SearchResult("f", 2, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>())
        { MatchEndLineNumber = 3, MatchEndColumn = 1 };
        Assert.Equal("\u2026 (+1 line)", one.MultilineSpanLabel);

        var many = new SearchResult("f", 2, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>())
        { MatchEndLineNumber = 5, MatchEndColumn = 1 };
        Assert.Equal("\u2026 (+3 lines)", many.MultilineSpanLabel);
    }

    // ── SkipBreakdown ───────────────────────────────────────────────

    [Fact]
    public void SkipBreakdown_HasMultilineSkippedCounter()
    {
        var b = new SkipBreakdown(0, 0, 0, 0, 0, 0, 0, MultilineSkipped: 7);
        Assert.Equal(7, b.MultilineSkipped);
        Assert.Contains("multilineSkipped=7", b.ToString());
    }

    // ── ContentSearcher.SearchMultilineAsync (engine) ───────────────

    [Fact]
    public async Task Multiline_MatchesAcrossLines()
    {
        var p = Write("a.txt", "foo\nbar\nbaz");
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
        Assert.Equal(1, outcome.MatchCount);
        var r = Assert.Single(results);
        Assert.Equal(1, r.LineNumber);
        Assert.Equal(2, r.MatchEndLineNumber);
        Assert.Equal(3, r.MatchEndColumn); // end col on line 2 = after "bar"
        Assert.True(r.IsMultilineMatch);
        Assert.Equal(3, r.MatchLength); // first-line-visible length = "foo"
    }

    [Fact]
    public async Task Multiline_SingleLineHit_KeepsNullSpan()
    {
        var p = Write("a.txt", "foobar\nbaz");
        var (_, results) = await RunMultiline(p, MultilineOpt("foo"));
        var r = Assert.Single(results);
        Assert.False(r.IsMultilineMatch);
        Assert.Null(r.MatchEndLineNumber);
    }

    [Fact]
    public async Task Multiline_DotAll_ControlsWhetherDotCrossesNewline()
    {
        var p = Write("a.txt", "a\nb");

        var (_, without) = await RunMultiline(p, MultilineOpt("a.b", dotAll: false));
        Assert.Empty(without); // '.' does not cross '\n'

        var (_, with) = await RunMultiline(p, MultilineOpt("a.b", dotAll: true));
        var r = Assert.Single(with);
        Assert.Equal(1, r.LineNumber);
        Assert.Equal(2, r.MatchEndLineNumber);
    }

    [Fact]
    public async Task Multiline_Crlf_SpanExcludesCarriageReturn()
    {
        var p = Write("crlf.txt", "foo\r\nbar\r\nbaz");
        var (_, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
        var r = Assert.Single(results);
        Assert.Equal(1, r.LineNumber);
        Assert.Equal(2, r.MatchEndLineNumber);
        Assert.Equal("foo", r.MatchLine); // display line has no trailing '\r'
        Assert.DoesNotContain('\r', r.MatchLine);
    }

    [Fact]
    public async Task Multiline_ContextNumbersFromSpanEnds()
    {
        var p = Write("ctx.txt", "aaa\nSTARTxxx\nyyyEND\nzzz\nwww");
        var (_, results) = await RunMultiline(p, MultilineOpt("START[\\s\\S]*?END", context: 1));
        var r = Assert.Single(results);
        Assert.Equal(2, r.LineNumber);
        Assert.Equal(3, r.MatchEndLineNumber);
        Assert.StartsWith("START", r.MatchLine);
        // Before context is the line before the span start (line 1).
        Assert.Equal(1, r.NumberedBefore[0].LineNum);
        Assert.Equal("aaa", r.NumberedBefore[0].Text);
        // After context is numbered from the END line (line 3) + 1 = line 4.
        Assert.Equal(4, r.NumberedAfter[0].LineNum);
        Assert.Equal("zzz", r.NumberedAfter[0].Text);
    }

    [Fact]
    public async Task Multiline_OverSizeCap_SkipsAndReportsMultilineCode()
    {
        var p = Write("big.txt", new string('x', 4096) + "\nfoo\nbar");
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar", cap: 64));
        Assert.Equal(ContentSearcher.SkipMultilineTooLarge, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_BinaryFile_SkippedBeforeDecode()
    {
        var bytes = new byte[] { (byte)'f', (byte)'o', (byte)'o', 0x00, 0x01, (byte)'\n', (byte)'b', (byte)'a', (byte)'r' };
        var p = WriteBytes("bin.dat", bytes);
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
        Assert.Equal(ContentSearcher.SkipBinary, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_ZeroWidthMatches_AreDropped()
    {
        var p = Write("z.txt", "line1\nline2\nline3");
        // '^' is zero-width at every line start; must not emit or loop.
        var (outcome, results) = await RunMultiline(p, MultilineOpt("^"));
        Assert.Equal(0, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_MultipleCrossLineSpans_AllEmittedAsDistinctOccurrences()
    {
        // Two "A...B" lazy spans that each wrap from one line to the next — two occurrences.
        var p = Write("two.txt", "A1\nB A2\nB tail");
        var (_, results) = await RunMultiline(p, MultilineOpt("A[\\s\\S]*?B"));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsMultilineMatch));
    }

    // ── Additional branch coverage: shadow / factory / parallelism ──

    [Fact]
    public void Shadow_NullInput_TreatedAsEmptyIdentity()
    {
        var shadow = MultilineTextShadow.Build(null!);
        Assert.Equal(string.Empty, shadow.Lf);
        Assert.True(shadow.IsIdentity); // no '\r' -> identity mapping
    }

    [Fact]
    public void Shadow_TrailingLoneCr_CollapsesWithoutOffsetShift()
    {
        var shadow = MultilineTextShadow.Build("a\r"); // lone '\r' at end of buffer
        Assert.Equal("a\n", shadow.Lf);
        Assert.False(shadow.IsIdentity);
        Assert.Equal(2, shadow.ToOriginalOffset(2)); // length-preserving substitution
    }

    [Fact]
    public void RegexFactory_ResolveOptionsFromSearchOptions_MirrorsFlags()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x", CaseSensitive = true, Multiline = true, MultilineDotAll = true };
        var flags = SearchRegexFactory.ResolveOptions(opts);
        Assert.True(flags.HasFlag(RegexOptions.Multiline));
        Assert.True(flags.HasFlag(RegexOptions.Singleline));
        Assert.False(flags.HasFlag(RegexOptions.IgnoreCase)); // case-sensitive
    }

    [Fact]
    public void ResolveMultilineParallelism_CapDisabled_UsesDefaultBudget()
    {
        // maxMultilineBytes <= 0 falls back to the 50 MB budget (cap ternary false branch).
        int degree = SearchOptions.ResolveMultilineParallelism(8, 100L * 1024 * 1024 * 1024, 0);
        Assert.Equal(4, degree);
    }

    [Fact]
    public void ResolveMultilineParallelism_UnknownAvailableMemory_DefaultsToTwo()
    {
        // availableBytes <= 0 -> memoryDerived defaults to 2 (avail ternary false branch).
        int degree = SearchOptions.ResolveMultilineParallelism(8, 0, SearchOptions.DefaultMaxMultilineBytes);
        Assert.Equal(2, degree);
    }

    [Fact]
    public void GetAvailablePhysicalMemoryBytes_ReturnsPositive()
    {
        // Real Win32 query (or the conservative 2 GB fallback) — always positive.
        Assert.True(SearchService.GetAvailablePhysicalMemoryBytes() > 0);
    }

    // ── SearchMultilineAsync — additional branch coverage ───────────

    [Fact]
    public async Task Multiline_NullRegex_ReturnsZeroWithoutReading()
    {
        var p = Write("nullrx.txt", "foo\nbar");
        var (outcome, results) = await RunMultilineWithRegex(p, MultilineOpt("unused"), regex: null);
        Assert.Equal(0, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_CapDisabled_ScansWithoutSizeSkip()
    {
        // MaxMultilineBytes <= 0 disables the size cap; even a large file is scanned.
        var p = Write("nocap.txt", new string('a', 200_000) + "\nfoo\nbar");
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar", cap: 0));
        Assert.NotEqual(ContentSearcher.SkipMultilineTooLarge, outcome.MatchCount);
        Assert.True(Assert.Single(results).IsMultilineMatch);
    }

    [Fact]
    public async Task Multiline_SkipBinaryDisabled_ScansBinaryContent()
    {
        // SkipBinary=false bypasses the binary sniff; the file is decoded and scanned.
        var bytes = new byte[] { (byte)'f', (byte)'o', (byte)'o', (byte)'\n', (byte)'b', (byte)'a', (byte)'r', 0x00 };
        var p = WriteBytes("notbin.dat", bytes);
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar", skipBinary: false));
        Assert.NotEqual(ContentSearcher.SkipBinary, outcome.MatchCount);
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiline_EmptyFile_NoMatchesNoCrash()
    {
        var p = Write("empty.txt", "");
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
        Assert.Equal(0, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_FileLargerThanPeekSample_StillMatchesAcrossLines()
    {
        // Larger than the binary/encoding peek sample so the peek read loop fills completely.
        var p = Write("largepeek.txt", new string('a', 9000) + "\nfoo\nbar\n");
        var (_, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
        Assert.True(Assert.Single(results).IsMultilineMatch);
    }

    [Fact]
    public async Task Multiline_MaxResults_BoundsBufferedResults()
    {
        // Two "A...B" cross-line spans exist, but MaxResults=1 caps per-file buffering to one.
        var p = Write("maxres.txt", "A1\nB A2\nB tail");
        var (_, results) = await RunMultiline(p, MultilineOpt("A[\\s\\S]*?B", maxResults: 1));
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiline_MaxMatchesPerFile_BoundsBufferedResults()
    {
        var p = Write("maxmpf.txt", "A1\nB A2\nB tail");
        var (_, results) = await RunMultiline(p, MultilineOpt("A[\\s\\S]*?B", maxMatchesPerFile: 1));
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiline_RegexTimeout_SkipsFileAndDiscardsPartials()
    {
        // Catastrophic backtracking is a MANAGED-engine safety property; the native linear regex
        // engine cannot time out, so this pins the managed path explicitly.
        var p = Write("slow.txt", new string('a', 40) + "!");
        var catastrophic = new Regex("(a+)+$", RegexOptions.Multiline, TimeSpan.FromMilliseconds(1));
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var (outcome, results) = await RunMultilineWithRegex(p, MultilineOpt("(a+)+$"), catastrophic);
            Assert.Equal(ContentSearcher.SkipMultilineTimeout, outcome.MatchCount);
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_ContextAtFirstLine_HasNoBeforeContext()
    {
        var p = Write("firstline.txt", "STARTa\naEND\nafter1\nafter2");
        var (_, results) = await RunMultiline(p, MultilineOpt("START[\\s\\S]*?END", context: 2));
        var r = Assert.Single(results);
        Assert.Equal(1, r.LineNumber);
        Assert.Empty(r.NumberedBefore);   // match starts on line 1: no before-context
        Assert.NotEmpty(r.NumberedAfter);
    }

    [Fact]
    public async Task Multiline_SpanEndsOnLastLine_HasNoAfterContext()
    {
        var p = Write("lastline.txt", "before1\nbefore2\nSTARTa\naEND");
        var (_, results) = await RunMultiline(p, MultilineOpt("START[\\s\\S]*?END", context: 2));
        var r = Assert.Single(results);
        Assert.Equal(3, r.LineNumber);
        Assert.Equal(4, r.MatchEndLineNumber);
        Assert.NotEmpty(r.NumberedBefore);
        Assert.Empty(r.NumberedAfter);     // span ends on the last line: no after-context
    }

    [Fact]
    public async Task Multiline_MatchStartsOnLastLine_UsesBufferEndAsLineEnd()
    {
        // Match starting on the final (no-trailing-newline) line exercises the "start line is the
        // last line" branch of the start-line-content-end computation.
        var p = Write("lastmatch.txt", "aaa\nbbb\nCCCdd");
        var (_, results) = await RunMultiline(p, MultilineOpt("CCC"));
        var r = Assert.Single(results);
        Assert.Equal(3, r.LineNumber);
        Assert.False(r.IsMultilineMatch);
        Assert.Equal("CCCdd", r.MatchLine);
    }

    // ── SearchMultilineAsync (managed engine forced) — the native engine intercepts these on a
    //    native-capable box (PreferNative default true), so force the managed whole-file path to
    //    cover its size-cap / null-regex / binary-sniff / match-cap / last-line / no-match / context
    //    branches directly. On a box where native is unavailable these simply exercise the same path. ──

    private async Task<(FileSearchOutcome outcome, List<SearchResult> results)> RunMultilineManaged(string path, SearchOptions opts)
    {
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try { return await RunMultiline(path, opts); }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_Managed_OverSizeCap_SkipsWithMultilineCode()
    {
        var p = Write("mbig.txt", new string('x', 4096) + "\nfoo\nbar");
        var (outcome, results) = await RunMultilineManaged(p, MultilineOpt("foo\\nbar", cap: 64));
        Assert.Equal(ContentSearcher.SkipMultilineTooLarge, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_Managed_NullRegex_ReturnsZeroWithoutReading()
    {
        var p = Write("mnullrx.txt", "foo\nbar");
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var (outcome, results) = await RunMultilineWithRegex(p, MultilineOpt("unused"), regex: null);
            Assert.Equal(0, outcome.MatchCount);
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_Managed_BinaryFile_SkippedBeforeDecode()
    {
        var bytes = new byte[] { (byte)'f', (byte)'o', (byte)'o', 0x00, 0x01, (byte)'\n', (byte)'b', (byte)'a', (byte)'r' };
        var p = WriteBytes("mbin.dat", bytes);
        var (outcome, results) = await RunMultilineManaged(p, MultilineOpt("foo\\nbar"));
        Assert.Equal(ContentSearcher.SkipBinary, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_Managed_MaxResults_BoundsBufferedResultsAndBreaks()
    {
        // Two "A...B" cross-line spans exist; MaxResults=1 caps the per-file buffer to one and takes
        // the `buffered.Count >= matchCap` break.
        var p = Write("mmaxres.txt", "A1\nB A2\nB tail");
        var (_, results) = await RunMultilineManaged(p, MultilineOpt("A[\\s\\S]*?B", maxResults: 1));
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiline_Managed_MatchStartsOnLastLine_UsesBufferEndAsLineEnd()
    {
        // Match on the final (no-trailing-newline) line takes the "start line is the last line"
        // (`: lf.Length`) branch of the start-line-content-end computation.
        var p = Write("mlastmatch.txt", "aaa\nbbb\nCCCdd");
        var (_, results) = await RunMultilineManaged(p, MultilineOpt("CCC"));
        var r = Assert.Single(results);
        Assert.Equal(3, r.LineNumber);
        Assert.False(r.IsMultilineMatch);
        Assert.Equal("CCCdd", r.MatchLine);
    }

    [Fact]
    public async Task Multiline_Managed_NoMatch_ReturnsZeroWithoutWriting()
    {
        // No match → the buffer stays empty, so the `buffered.Count > 0` write block is skipped and
        // the method returns 0 without publishing anything.
        var p = Write("mnomatch.txt", "hello\nworld\n");
        var (outcome, results) = await RunMultilineManaged(p, MultilineOpt("zzznotpresent"));
        Assert.Equal(0, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_Managed_ContextAtFirstLine_HasNoBeforeContext()
    {
        // Match starts on line 1 → BuildContextBefore's `from >= startLineIdx` early-return; the
        // after-context loop runs.
        var p = Write("mfirst.txt", "STARTa\naEND\nafter1\nafter2");
        var (_, results) = await RunMultilineManaged(p, MultilineOpt("START[\\s\\S]*?END", context: 2));
        var r = Assert.Single(results);
        Assert.Equal(1, r.LineNumber);
        Assert.Empty(r.NumberedBefore);
        Assert.NotEmpty(r.NumberedAfter);
    }

    [Fact]
    public async Task Multiline_Managed_SpanEndsOnLastLine_HasNoAfterContext()
    {
        // Span ends on the last line → BuildContextAfter's `to <= endLineIdx` early-return; the
        // before-context loop runs.
        var p = Write("mlastctx.txt", "before1\nbefore2\nSTARTa\naEND");
        var (_, results) = await RunMultilineManaged(p, MultilineOpt("START[\\s\\S]*?END", context: 2));
        var r = Assert.Single(results);
        Assert.Equal(3, r.LineNumber);
        Assert.Equal(4, r.MatchEndLineNumber);
        Assert.NotEmpty(r.NumberedBefore);
        Assert.Empty(r.NumberedAfter);
    }

    [Fact]
    public async Task Multiline_Managed_UnauthorizedOpen_ReturnsAccessDeniedSkip()
    {
        // Opening a directory as a file throws UnauthorizedAccessException from the managed multiline
        // read; the multiline block maps it to an access-denied skip. Pre-seed the metadata cache so
        // the FileInfo.Exists check (false for a directory) is bypassed and the read is attempted.
        var dir = Path.Combine(_root, "asdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FileMetadataCache.Set(dir, new FileMetadata(100, DateTime.Now, DateTime.Now));
        var (outcome, results) = await RunMultilineManaged(dir, MultilineOpt("foo\\nbar"));
        Assert.Equal(ContentSearcher.SkipAccessDenied, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_Managed_CapDisabled_ScansWithoutSizeSkip()
    {
        // MaxMultilineBytes <= 0 short-circuits the size-cap guard (its `> 0` condition false) on the
        // managed path, so even a large file is scanned.
        var p = Write("mnocap.txt", new string('a', 200_000) + "\nfoo\nbar");
        var (outcome, results) = await RunMultilineManaged(p, MultilineOpt("foo\\nbar", cap: 0));
        Assert.NotEqual(ContentSearcher.SkipMultilineTooLarge, outcome.MatchCount);
        Assert.True(Assert.Single(results).IsMultilineMatch);
    }

    [Fact]
    public async Task Multiline_Managed_SkipBinaryDisabled_ScansBinaryContent()
    {
        // SkipBinary=false short-circuits the binary-sniff guard (its first condition false) on the
        // managed path, so a file with a NUL byte is still decoded and scanned.
        var bytes = new byte[] { (byte)'f', (byte)'o', (byte)'o', (byte)'\n', (byte)'b', (byte)'a', (byte)'r', 0x00 };
        var p = WriteBytes("mnotbin.dat", bytes);
        var (outcome, results) = await RunMultilineManaged(p, MultilineOpt("foo\\nbar", skipBinary: false));
        Assert.NotEqual(ContentSearcher.SkipBinary, outcome.MatchCount);
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiline_Managed_EmptyFile_NoMatchesNoCrash()
    {
        // An empty file makes the peek read return 0 bytes, so the binary-sniff guard's `peekRead > 0`
        // condition is false; the managed path decodes an empty string and finds nothing.
        var p = Write("mempty.txt", "");
        var (outcome, results) = await RunMultilineManaged(p, MultilineOpt("foo\\nbar"));
        Assert.Equal(0, outcome.MatchCount);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Multiline_Managed_MaxResultsAboveMatchCap_KeepsPerFileCap()
    {
        // MaxResults (5) is larger than the per-file cap (MaxMatchesPerFile=1), so the
        // `MaxResults < matchCap` condition is false and matchCap stays at the per-file cap.
        var p = Write("mcapkeep.txt", "A1\nB A2\nB tail");
        var (_, results) = await RunMultilineManaged(p, MultilineOpt("A[\\s\\S]*?B", maxResults: 5, maxMatchesPerFile: 1));
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiline_FileLockedForReading_ReturnsIoErrorSkip()
    {
        // A file held with FileShare.None makes the managed multiline read's open throw IOException,
        // which is caught and reported as an IO-error skip (not a crash). Pins the managed path;
        // the native engine maps a locked open to an access-denied skip instead.
        var p = Write("locked.txt", "foo\nbar");
        using var hold = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        bool prev = ContentSearcher.PreferNative;
        ContentSearcher.PreferNative = false;
        try
        {
            var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
            Assert.Equal(ContentSearcher.SkipIOError, outcome.MatchCount);
            Assert.Empty(results);
        }
        finally { ContentSearcher.PreferNative = prev; }
    }

    [Fact]
    public async Task Multiline_WatchedFile_EmitsDiagnosticsAndStillMatches()
    {
        // Registering the file with FileWatchDiagnostics exercises the `if (watched)` diagnostic
        // checkpoints on the multiline exit path without altering results.
        var p = Write("watched.txt", "foo\nbar");
        FileWatchDiagnostics.Add("watched.txt");
        try
        {
            var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
            var r = Assert.Single(results);
            Assert.True(r.IsMultilineMatch);
            Assert.Equal(2, r.MatchEndLineNumber);
            Assert.True(outcome.MatchCount >= 1);
        }
        finally
        {
            FileWatchDiagnostics.Clear();
        }
    }

    // ── Source-pins: UI / VM / CLI / SearchService wiring ───────────

    [Fact]
    public void SearchService_GatesNativeOffAndPromotesLiteralForMultiline()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SearchService.cs"));
        Assert.Contains("Native.NativeSearcher.IsAvailable && !options.Multiline", src);
        Assert.Contains("ResolveMultilineParallelism", src);
        // A bare literal is promoted to an escaped regex under multiline.
        Assert.Contains("else if (options.Multiline)", src);
        Assert.Contains("Regex.Escape(literalTerms[0])", src);
    }

    [Fact]
    public void CopyOptions_CarriesMultilineFlags()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SearchService.cs"));
        Assert.Contains("Multiline = options.Multiline,", src);
        Assert.Contains("MultilineDotAll = options.MultilineDotAll,", src);
        Assert.Contains("MaxMultilineBytes = options.MaxMultilineBytes,", src);
    }

    [Fact]
    public void ContentSearcher_RoutesMultilineBeforeNative()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "ContentSearcher.cs"));
        int multilineBranch = src.IndexOf("if (options.Multiline)", StringComparison.Ordinal);
        int nativePath = src.IndexOf("if (PreferNative && Native.NativeSearcher.IsAvailable)", StringComparison.Ordinal);
        Assert.True(multilineBranch > 0);
        Assert.True(nativePath > multilineBranch, "Multiline branch must precede the native fast path.");
    }

    [Fact]
    public void CliRunner_HasMultilineFlagsAndStreamingWriter()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));
        Assert.Contains("\"--multiline\", \"-U\"", src);
        Assert.Contains("\"--multiline-dotall\"", src);
        Assert.Contains("\"--max-multiline-bytes\"", src);
        // Phase 2 engine selector.
        Assert.Contains("\"--multiline-engine\"", src);
        Assert.Contains("MultilineEngine       = (MultilineEngineKind)(args.MultilineEngineIndex ?? 0)", src);
        // Hidden-gate: managed streaming writer bypasses the native DirectOutputSink for multiline.
        Assert.Contains("bool streamingMultiline = options.Multiline && !needsCollection;", src);
        Assert.Contains("multilineWriter", src);
        // Same-line de-dup fold is bypassed for true cross-line spans only.
        Assert.Contains("!result.IsMultilineMatch && sameFile && result.LineNumber > 0 && result.LineNumber == _lastLine", src);
        // Multiline replace maps spans back via the offset map, not regex.Replace(original).
        Assert.Contains("ReplaceMultiline(regex, original", src);
    }

    [Fact]
    public void MainViewModel_HasMultilinePropertyAndThreadsIntoOptions()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("public partial bool Multiline { get; set; }", src);
        Assert.Contains("public partial bool MultilineDotAll { get; set; }", src);
        Assert.Contains("Multiline = Multiline,", src);
        Assert.Contains("MultilineDotAll = MultilineDotAll,", src);
        Assert.Contains("MultilineEngine = (MultilineEngineKind)_settings.MultilineEngine,", src);
        Assert.Contains("Multiline = _settings.MultilineSearchDefault;", src);
        Assert.Contains("LastSearchMultiline = Multiline;", src);
    }

    [Fact]
    public void MainWindowXaml_HasMultilineToggleAfterRegexToggle()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        int regex = src.IndexOf("x:Name=\"RegexToggle\"", StringComparison.Ordinal);
        int multiline = src.IndexOf("x:Name=\"MultilineToggle\"", StringComparison.Ordinal);
        int exact = src.IndexOf("x:Name=\"ExactMatchToggle\"", StringComparison.Ordinal);
        Assert.True(regex > 0 && multiline > regex && exact > multiline, "MultilineToggle must sit between Regex and Exact toggles.");
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.Multiline, Mode=TwoWay}\"", src);
        Assert.Contains("Alt+M", src);
        // The regex toggle tooltip explains that it is driven by the multiline toggle.
        Assert.Contains("Use Regular Expression (Alt+R). This will be enabled if Multiline is enabled", src);
        // The exact-match toggle tooltip explains it is ignored while multiline is on.
        Assert.Contains("(Alt+E). Ignored while Multiline is on", src);
        // Reserved room for the fourth toggle (padding set via a Setter, not an inline attribute).
        Assert.Contains("<Setter Property=\"Padding\" Value=\"10,6,158,6\" />", src);
        // Advanced Options toggle + dot-all sub-toggle.
        Assert.Contains("x:Name=\"MultilineAdvancedToggle\"", src);
        Assert.Contains("x:Name=\"MultilineDotAllToggle\"", src);
        // Phase 1 cross-line span marker in the results row.
        Assert.Contains("Text=\"{x:Bind MultilineSpanLabel, Mode=OneWay}\"", src);
    }

    [Fact]
    public void MainViewModel_RegexFollowsMultilineToggle()
    {
        // Turning Multiline on/off drives UseRegex on/off, because a cross-line match is only
        // meaningful in regex mode (a plain literal is split on whitespace, newlines included).
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("partial void OnMultilineChanged(bool value)", src);
        Assert.Contains("UseRegex = value;", src);
        // Exact-match (whole-word) is unchecked while Multiline is on and restored when it turns off.
        Assert.Contains("ExactMatch = !value;", src);
    }

    [Fact]
    public void SettingsService_HasMultilineSearchDefault()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool MultilineSearchDefault { get; set; }", src);
        Assert.Contains("public int MultilineEngine { get; set; }", src);
    }

    [Fact]
    public void CliCommandGenerator_EmitsMultilineFlags()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.CliCommand.cs"));
        Assert.Contains("ViewModel.Multiline ? \"--multiline\" : \"--no-multiline\"", src);
        Assert.Contains("--multiline-dotall", src);
        Assert.Contains("\"--multiline-engine\", \"grep\"", src);
    }

    [Fact]
    public void PreviewHighlight_BuildsRegexThroughFactoryWithMultilineFlags()
    {
        // Preview highlighting must route through the centralized SearchRegexFactory so search,
        // replace, and highlight can never disagree on the multiline flags (§4 of the plan).
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewBuilder.cs"));
        Assert.Contains("bool multiline = false, bool multilineDotAll = false", src);
        Assert.Contains("SearchRegexFactory.Build(pattern, caseSensitive, multiline, multilineDotAll)", src);
        Assert.Contains("ViewModel.Multiline, ViewModel.MultilineDotAll)", src);
        Assert.Contains("ViewModel.LastSearchMultiline, ViewModel.LastSearchMultilineDotAll)", src);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// Direct unit tests for <c>ContentSearcher.MultilineStreamingSink.OnMultilineMatch</c> — the native
/// multiline callback sink. Mirrors <c>StreamingSinkBackpressureTests</c> for the single-line sink:
/// fabricates a <see cref="Yagu.Native.NativeSearcher.QgMultilineMatchView"/> and drives the sink
/// directly to deterministically cover the span-mapping, overflow-clamp, metadata-cache, and
/// channel-backpressure/cancellation branches (no native engine required).
///
/// Intentionally NOT covered (accepted defensive residuals, matching the single-line
/// <c>StreamingSink</c>/<c>SearchFileStream</c>/<c>OnMatchTrampoline</c> and per
/// <c>testing.instructions.md</c> — genuinely-unreachable defensive code):
///   • <c>LineLen &gt; int.MaxValue</c> clamp — cannot allocate/point at a 2 GB+ line safely.
///   • the in-loop backpressure retry-success and <c>OperationCanceledException</c> catch — the
///     top-of-callback cancel check short-circuits before the loop, so those arms need a racy
///     cross-thread test (the single-line sink leaves them uncovered for the same reason).
///   • the native FFI exception handlers (<c>sink.CapturedException</c> throw, the trampoline
///     <c>catch</c>) and the <c>!IsAvailable</c> guard — the sink never throws and native is loaded
///     in tests.
/// </summary>
public sealed class MultilineStreamingSinkTests
{
    private static FileMetadata Meta() => new(100, DateTime.Now, DateTime.Now);

    [Fact]
    public unsafe void OnMultilineMatch_CancelledToken_ReturnsOne()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), cts.Token);
        byte[] line = Encoding.UTF8.GetBytes("foo");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            { LineNumber = 1, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length, EndLine = 1, EndCol = 3 };
            Assert.Equal(1, sink.OnMultilineMatch(&v)); // cancelled -> stop
            Assert.Equal(0, sink.Emitted);
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_ChannelCompleted_ReturnsOne()
    {
        var channel = Channel.CreateBounded<SearchResult>(1);
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), CancellationToken.None);
        channel.Writer.Complete(); // TryWrite fails and WaitToWriteAsync returns false
        byte[] line = Encoding.UTF8.GetBytes("foo");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            { LineNumber = 1, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length, EndLine = 1 };
            Assert.Equal(1, sink.OnMultilineMatch(&v)); // channel closed -> stop
            Assert.Equal(0, sink.Emitted);
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_BoundedChannelFull_WritesEventually()
    {
        var channel = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), CancellationToken.None);
        byte[] line = Encoding.UTF8.GetBytes("foo");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            { LineNumber = 1, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length, EndLine = 1 };
            Assert.Equal(0, sink.OnMultilineMatch(&v)); // fills capacity 1
            Assert.True(channel.Reader.TryRead(out _));  // drain
            v.LineNumber = 2;
            Assert.Equal(0, sink.OnMultilineMatch(&v)); // succeeds after space frees
            Assert.Equal(2, sink.Emitted);
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_CancelledDuringBackpressure_ReturnsOne()
    {
        var channel = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var cts = new CancellationTokenSource();
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), cts.Token);
        byte[] line = Encoding.UTF8.GetBytes("foo");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            { LineNumber = 1, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length, EndLine = 1 };
            Assert.Equal(0, sink.OnMultilineMatch(&v)); // fills capacity 1
            cts.Cancel();                                // next write enters + bails the backpressure loop
            v.LineNumber = 2;
            Assert.Equal(1, sink.OnMultilineMatch(&v));
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_CrossLineSpan_SetsEndFieldsAndCachesMetadataOnce()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), CancellationToken.None);
        byte[] line = Encoding.UTF8.GetBytes("START");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            { LineNumber = 2, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p, LineLen = (nuint)line.Length, EndLine = 4, EndCol = 3 };
            Assert.Equal(0, sink.OnMultilineMatch(&v)); // caches metadata (first match)
            v.LineNumber = 6; v.EndLine = 8;
            Assert.Equal(0, sink.OnMultilineMatch(&v)); // metadata already cached -> skip
            Assert.Equal(2, sink.Emitted);

            Assert.True(channel.Reader.TryRead(out var r1));
            Assert.Equal(2, r1.LineNumber);
            Assert.Equal(4, r1.MatchEndLineNumber);
            Assert.Equal(3, r1.MatchEndColumn);
            Assert.True(r1.IsMultilineMatch);
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_SingleLineHit_LeavesSpanNull()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), CancellationToken.None);
        byte[] line = Encoding.UTF8.GetBytes("foobar");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            { LineNumber = 3, MatchStart = 0, SourceMatchStart = 0, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length, EndLine = 3, EndCol = 3 };
            Assert.Equal(0, sink.OnMultilineMatch(&v)); // endLine == lineNum -> no span
            Assert.True(channel.Reader.TryRead(out var r));
            Assert.Null(r.MatchEndLineNumber);
            Assert.False(r.IsMultilineMatch);
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_OverflowFields_ClampSafely()
    {
        // Out-of-int-range line/start/len fields clamp defensively (small line bytes kept safe).
        var channel = Channel.CreateUnbounded<SearchResult>();
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), CancellationToken.None);
        byte[] line = Encoding.UTF8.GetBytes("x");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            {
                LineNumber = ulong.MaxValue,      // -> int.MaxValue
                MatchStart = uint.MaxValue,       // -> clamped to line length
                SourceMatchStart = uint.MaxValue, // -> null (deferred sentinel)
                MatchLen = uint.MaxValue,         // -> 0
                LinePtr = p,
                LineLen = (nuint)line.Length,
                EndLine = 1,                       // < clamped lineNum -> single-line render
                EndCol = 0,
            };
            Assert.Equal(0, sink.OnMultilineMatch(&v));
            Assert.True(channel.Reader.TryRead(out var r));
            Assert.Equal(int.MaxValue, r.LineNumber);
            Assert.Null(r.MatchEndLineNumber);
        }
    }

    [Fact]
    public unsafe void OnMultilineMatch_CrossLineOverflowEndFields_ClampSpan()
    {
        // Cross-line span with out-of-range end line/col clamps both to int.MaxValue.
        var channel = Channel.CreateUnbounded<SearchResult>();
        var sink = new ContentSearcher.MultilineStreamingSink(@"C:\t.txt", channel.Writer, Meta(), CancellationToken.None);
        byte[] line = Encoding.UTF8.GetBytes("foo");
        fixed (byte* p = line)
        {
            var v = new Yagu.Native.NativeSearcher.QgMultilineMatchView
            {
                LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 3,
                LinePtr = p, LineLen = (nuint)line.Length,
                EndLine = ulong.MaxValue, // -> int.MaxValue, > lineNum 1 -> cross-line span
                EndCol = uint.MaxValue,   // -> int.MaxValue
            };
            Assert.Equal(0, sink.OnMultilineMatch(&v));
            Assert.True(channel.Reader.TryRead(out var r));
            Assert.Equal(int.MaxValue, r.MatchEndLineNumber);
            Assert.Equal(int.MaxValue, r.MatchEndColumn);
        }
    }
}

