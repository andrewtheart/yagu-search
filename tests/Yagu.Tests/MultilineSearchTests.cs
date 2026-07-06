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

    private static SearchOptions MultilineOpt(string pattern, bool dotAll = false, bool caseSensitive = false, int context = 0, long cap = SearchOptions.DefaultMaxMultilineBytes, bool skipBinary = true, int maxResults = 0, int maxMatchesPerFile = 0)
        => new()
        {
            Directory = ".",
            Query = pattern,
            UseRegex = true,
            Multiline = true,
            MultilineDotAll = dotAll,
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
    public void SearchResult_MultilineResult_IsPinnedFromEviction()
    {
        var single = new SearchResult("f", 1, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>());
        Assert.True(single.TryBeginEviction()); // single-line evicts normally

        var multi = new SearchResult("f", 1, "x", 0, 1, Array.Empty<string>(), Array.Empty<string>())
        {
            MatchEndLineNumber = 3,
            MatchEndColumn = 2,
        };
        Assert.False(multi.TryBeginEviction()); // Phase 1a stopgap: multiline pinned in memory
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
        // Catastrophic backtracking + a 1 ms timeout -> RegexMatchTimeoutException -> multiline-timeout skip.
        var p = Write("slow.txt", new string('a', 40) + "!");
        var catastrophic = new Regex("(a+)+$", RegexOptions.Multiline, TimeSpan.FromMilliseconds(1));
        var (outcome, results) = await RunMultilineWithRegex(p, MultilineOpt("(a+)+$"), catastrophic);
        Assert.Equal(ContentSearcher.SkipMultilineTimeout, outcome.MatchCount);
        Assert.Empty(results);
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

    [Fact]
    public async Task Multiline_FileLockedForReading_ReturnsIoErrorSkip()
    {
        // A file held with FileShare.None makes the multiline read's open throw IOException,
        // which is caught and reported as an IO-error skip (not a crash).
        var p = Write("locked.txt", "foo\nbar");
        using var hold = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        var (outcome, results) = await RunMultiline(p, MultilineOpt("foo\\nbar"));
        Assert.Equal(ContentSearcher.SkipIOError, outcome.MatchCount);
        Assert.Empty(results);
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
        // Reserved room for the fourth toggle (padding set via a Setter, not an inline attribute).
        Assert.Contains("<Setter Property=\"Padding\" Value=\"10,6,158,6\" />", src);
        // Advanced Options toggle + dot-all sub-toggle.
        Assert.Contains("x:Name=\"MultilineAdvancedToggle\"", src);
        Assert.Contains("x:Name=\"MultilineDotAllToggle\"", src);
        // Phase 1 cross-line span marker in the results row.
        Assert.Contains("Text=\"{x:Bind MultilineSpanLabel, Mode=OneWay}\"", src);
    }

    [Fact]
    public void SettingsService_HasMultilineSearchDefault()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public bool MultilineSearchDefault { get; set; }", src);
    }

    [Fact]
    public void CliCommandGenerator_EmitsMultilineFlags()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.CliCommand.cs"));
        Assert.Contains("ViewModel.Multiline ? \"--multiline\" : \"--no-multiline\"", src);
        Assert.Contains("--multiline-dotall", src);
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
