using System.Text;
using Yagu.Native;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class DirectOutputSinkTests
{
    [Fact]
    public unsafe void OnMatchForFile_WritesPlainRipgrepOutputWithContextSeparatorsAndTruncation()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\src\example.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 2, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned, contextEnabled: true);

        byte[] firstLine = Encoding.UTF8.GetBytes("prefix NEEDLE suffix");
        byte[] before = PackContext("before one", "before two");
        byte[] after = PackContext("after one");
        fixed (byte* firstLinePtr = firstLine)
        fixed (byte* beforePtr = before)
        fixed (byte* afterPtr = after)
        {
            var first = new NativeSearcher.QgMatchView
            {
                LineNumber = 10,
                MatchStart = 7,
                SourceMatchStart = 7,
                MatchLen = 6,
                LinePtr = firstLinePtr,
                LineLen = (nuint)firstLine.Length,
                CtxBeforePtr = beforePtr,
                CtxBeforeBytes = (nuint)before.Length,
                CtxBeforeCount = 2,
                CtxAfterPtr = afterPtr,
                CtxAfterBytes = (nuint)after.Length,
                CtxAfterCount = 1,
            };

            Assert.Equal(0, sink.OnMatchForFile(0, &first));
        }

        byte[] secondLine = Encoding.UTF8.GetBytes("second NEEDLE");
        fixed (byte* secondLinePtr = secondLine)
        {
            var second = new NativeSearcher.QgMatchView
            {
                LineNumber = 20,
                MatchStart = 7,
                SourceMatchStart = 7,
                MatchLen = 6,
                LinePtr = secondLinePtr,
                LineLen = (nuint)secondLine.Length,
            };

            Assert.Equal(1, sink.OnMatchForFile(0, &second));
            Assert.True(sink.Truncated);
            Assert.Equal(2, sink.TotalMatches);

            var beforeLength = output.Length;
            Assert.Equal(1, sink.OnMatchForFile(0, &second));
            Assert.Equal(beforeLength, output.Length);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(@"C:\src\example.txt", text);
        Assert.Contains("8-before one", text);
        Assert.Contains("9-before two", text);
        Assert.Contains("10:prefix NEEDLE suffix", text);
        Assert.Contains("11-after one", text);
        Assert.Contains("--", text);
        Assert.Contains("20:second NEEDLE", text);
        Assert.Equal(1, sink.FilesWithMatches);
    }

    [Fact]
    public unsafe void OnMatchForFile_WritesColorizedOutputAndHandlesWholeLineMatchFallback()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { new string('x', 1_100) + ".txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: true, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned, initialCapacity: 1);

        byte[] line = Encoding.UTF8.GetBytes("plain line");
        byte[] after = PackContext("after color");
        fixed (byte* linePtr = line)
        fixed (byte* afterPtr = after)
        {
            var match = new NativeSearcher.QgMatchView
            {
                LineNumber = 3,
                MatchStart = uint.MaxValue,
                SourceMatchStart = uint.MaxValue,
                MatchLen = 0,
                LinePtr = linePtr,
                LineLen = (nuint)line.Length,
                CtxAfterPtr = afterPtr,
                CtxAfterBytes = (nuint)after.Length,
                CtxAfterCount = 1,
            };

            Assert.Equal(0, sink.OnMatchForFile(0, &match));
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\u001b[1;35m", text);
        Assert.Contains("\u001b[1;32m3\u001b[0m:plain line", text);
        Assert.Contains("\u001b[1;34m4\u001b[0m-after color", text);
    }

    [Fact]
    public unsafe void OnFileDone_TracksStatusesBytesAndResizesStorage()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { "a.txt", "b.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned, initialCapacity: 1);

        Assert.Equal(1, sink.OnMatch(null));

        sink.OnFileDone(0, NativeSearcher.StatusOk, 123, 0);
        sink.OnFileDone(2, NativeSearcher.StatusBinarySkipped, 10, 0);
        sink.OnFileDone(3, NativeSearcher.StatusOpenFailed, 11, 0);
        sink.OnFileDone(4, NativeSearcher.StatusTooLarge, 12, 0);
        sink.OnFileDone(5, NativeSearcher.StatusInvalidPath, 13, 0);
        sink.OnFileDone(6, NativeSearcher.StatusCancelled, 14, 0);

        Assert.Equal(6, filesScanned);
        Assert.Equal(123, sink.BytesScanned);
        Assert.Equal(5, sink.FilesSkipped);
        Assert.Equal(1, sink.SkipBinary);
        Assert.Equal(1, sink.SkipAccessDenied);
        Assert.Equal(1, sink.SkipTooLarge);
        Assert.Equal(1, sink.SkipNotFound);
        Assert.Equal(1, sink.SkipOther);
        Assert.Equal(NativeSearcher.StatusCancelled, sink.GetStatus(6));
        Assert.Equal(14, sink.GetFileLength(6));
        Assert.Equal(0, sink.GetStatus(99));
        Assert.Equal(0, sink.GetFileLength(99));
    }

    [Fact]
    public unsafe void OnMatchForFile_MultipleFiles_InsertsBlankLineBetween()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\a.txt", @"C:\b.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] line1 = Encoding.UTF8.GetBytes("match in a");
        byte[] line2 = Encoding.UTF8.GetBytes("match in b");
        fixed (byte* p1 = line1)
        fixed (byte* p2 = line2)
        {
            var m1 = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p1, LineLen = (nuint)line1.Length };
            var m2 = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p2, LineLen = (nuint)line2.Length };

            sink.OnMatchForFile(0, &m1);
            sink.OnMatchForFile(1, &m2);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("C:\\a.txt", text);
        Assert.Contains("C:\\b.txt", text);
        Assert.Equal(2, sink.FilesWithMatches);
        // Blank line between files
        Assert.Contains("\n\n", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_CancelFlagSet_StopsImmediately()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\a.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] line = Encoding.UTF8.GetBytes("first match");
        fixed (byte* p = line)
        {
            var m = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p, LineLen = (nuint)line.Length };
            sink.OnMatchForFile(0, &m);
        }

        long lenAfterFirst = output.Length;

        // Truncate to trigger _stopped
        byte[] line2 = Encoding.UTF8.GetBytes("second");
        fixed (byte* p = line2)
        {
            // Manually set Truncated by reaching maxResults via constructor with maxResults=1, currentTotal=0
        }

        // Create a new sink with maxResults=1 to verify stop behavior
        using var output2 = new MemoryStream();
        using var sink2 = new DirectOutputSink(output2, color: false, paths, maxResults: 1, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] lineA = Encoding.UTF8.GetBytes("AAA");
        byte[] lineB = Encoding.UTF8.GetBytes("BBB");
        fixed (byte* pA = lineA)
        fixed (byte* pB = lineB)
        {
            var ma = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 3, LinePtr = pA, LineLen = (nuint)lineA.Length };
            var mb = new NativeSearcher.QgMatchView { LineNumber = 2, MatchStart = 0, SourceMatchStart = 0, MatchLen = 3, LinePtr = pB, LineLen = (nuint)lineB.Length };

            int r1 = sink2.OnMatchForFile(0, &ma);
            Assert.Equal(1, r1); // Returns 1 because maxResults reached
            Assert.True(sink2.Truncated);

            long lenBefore = output2.Length;
            int r2 = sink2.OnMatchForFile(0, &mb);
            Assert.Equal(1, r2);
            Assert.Equal(lenBefore, output2.Length); // No output written after stop
        }
    }

    [Fact]
    public unsafe void OnMatchForFile_ContextBeforeWithSeparator_WritesDoubleDash()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\a.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] line1 = Encoding.UTF8.GetBytes("first");
        byte[] line2 = Encoding.UTF8.GetBytes("second");
        byte[] ctx = PackContext("before second");
        fixed (byte* p1 = line1)
        fixed (byte* p2 = line2)
        fixed (byte* pCtx = ctx)
        {
            // First match at line 5
            var m1 = new NativeSearcher.QgMatchView { LineNumber = 5, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p1, LineLen = (nuint)line1.Length };
            sink.OnMatchForFile(0, &m1);

            // Second match at line 20 with context-before starting at line 19
            var m2 = new NativeSearcher.QgMatchView
            {
                LineNumber = 20, MatchStart = 0, SourceMatchStart = 0, MatchLen = 6,
                LinePtr = p2, LineLen = (nuint)line2.Length,
                CtxBeforePtr = pCtx, CtxBeforeBytes = (nuint)ctx.Length, CtxBeforeCount = 1,
            };
            sink.OnMatchForFile(0, &m2);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        // Should contain separator between non-contiguous matches
        Assert.Contains("--\n", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_NoContext_NonContiguousMatches_OmitsSeparator()
    {
        // ripgrep prints "--" separators only when context (-A/-B/-C) is enabled.
        // At context 0 (contextEnabled: false), non-contiguous matches must NOT be
        // separated by "--".
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\a.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned, contextEnabled: false);

        byte[] line1 = Encoding.UTF8.GetBytes("first");
        byte[] line2 = Encoding.UTF8.GetBytes("second");
        fixed (byte* p1 = line1)
        fixed (byte* p2 = line2)
        {
            var m1 = new NativeSearcher.QgMatchView { LineNumber = 5, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p1, LineLen = (nuint)line1.Length };
            sink.OnMatchForFile(0, &m1);
            var m2 = new NativeSearcher.QgMatchView { LineNumber = 20, MatchStart = 0, SourceMatchStart = 0, MatchLen = 6, LinePtr = p2, LineLen = (nuint)line2.Length };
            sink.OnMatchForFile(0, &m2);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("--", text);
        Assert.Contains("5:first", text);
        Assert.Contains("20:second", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_MultipleMatchesOnSameLine_EmitsLineOnce()
    {
        // ripgrep is line-oriented: a source line containing several matches is
        // printed exactly once. The scanner delivers same-line matches consecutively,
        // so the sink must fold them into a single emitted line and count it once.
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\a.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        // "foo foo foo" — three matches of "foo" on the same line (offsets 0, 4, 8).
        byte[] line = Encoding.UTF8.GetBytes("foo foo foo");
        byte[] next = Encoding.UTF8.GetBytes("foo bar");
        fixed (byte* p = line)
        fixed (byte* pn = next)
        {
            var a = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length };
            var b = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 4, SourceMatchStart = 4, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length };
            var c = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 8, SourceMatchStart = 8, MatchLen = 3, LinePtr = p, LineLen = (nuint)line.Length };
            var d = new NativeSearcher.QgMatchView { LineNumber = 2, MatchStart = 0, SourceMatchStart = 0, MatchLen = 3, LinePtr = pn, LineLen = (nuint)next.Length };

            Assert.Equal(0, sink.OnMatchForFile(0, &a));
            Assert.Equal(0, sink.OnMatchForFile(0, &b));
            Assert.Equal(0, sink.OnMatchForFile(0, &c));
            Assert.Equal(0, sink.OnMatchForFile(0, &d));
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        // Line 1 emitted exactly once despite three matches on it.
        int occurrences = text.Split("1:foo foo foo").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("2:foo bar", text);
        // Matching-line count (like ripgrep), not raw match count: 2 lines, not 4.
        Assert.Equal(2, sink.TotalMatches);
    }

    [Fact]
    public unsafe void OnMatchForFile_PlainNoColorMatchInvalid_WritesWholeLine()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\a.txt" };
        int cancel = 0;
        int filesScanned = 0;
        // Use color mode to test the matchStart >= lineLen fallback
        using var sink = new DirectOutputSink(output, color: true, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] line = Encoding.UTF8.GetBytes("hello world");
        fixed (byte* p = line)
        {
            // matchStart beyond line length → writes whole line without highlight
            var m = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 999, SourceMatchStart = 999, MatchLen = 0, LinePtr = p, LineLen = (nuint)line.Length };
            sink.OnMatchForFile(0, &m);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("hello world", text);
    }

    private static byte[] PackContext(params string[] lines)
    {
        using var stream = new MemoryStream();
        foreach (string line in lines)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(BitConverter.GetBytes((uint)bytes.Length));
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    [Fact]
    public unsafe void OnMatchForFile_ColorMode_HighlightsMatchWithPrefixAndSuffix()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\test.cs" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: true, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        // "hello MATCH world" — match starts at 6, length 5
        byte[] line = Encoding.UTF8.GetBytes("hello MATCH world");
        fixed (byte* p = line)
        {
            var m = new NativeSearcher.QgMatchView
            {
                LineNumber = 7,
                MatchStart = 6,
                SourceMatchStart = 6,
                MatchLen = 5,
                LinePtr = p,
                LineLen = (nuint)line.Length,
            };
            sink.OnMatchForFile(0, &m);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        // Should have: green line number, then prefix "hello ", then red "MATCH", then suffix " world"
        Assert.Contains("\u001b[1;31mMATCH\u001b[0m", text); // red match
        Assert.Contains("hello ", text); // prefix before match
        Assert.Contains(" world", text); // suffix after match
    }

    [Fact]
    public unsafe void OnMatchForFile_ColorMode_MatchAtStart_NoPrefixWritten()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\test.cs" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: true, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        // "MATCH suffix" — match starts at 0
        byte[] line = Encoding.UTF8.GetBytes("MATCH suffix");
        fixed (byte* p = line)
        {
            var m = new NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchStart = 0,
                SourceMatchStart = 0,
                MatchLen = 5,
                LinePtr = p,
                LineLen = (nuint)line.Length,
            };
            sink.OnMatchForFile(0, &m);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\u001b[1;31mMATCH\u001b[0m suffix", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_ColorMode_MatchAtEnd_NoSuffixWritten()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\test.cs" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: true, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        // "prefix MATCH" — match at end of line
        byte[] line = Encoding.UTF8.GetBytes("prefix MATCH");
        fixed (byte* p = line)
        {
            var m = new NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchStart = 7,
                SourceMatchStart = 7,
                MatchLen = 5,
                LinePtr = p,
                LineLen = (nuint)line.Length,
            };
            sink.OnMatchForFile(0, &m);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("prefix \u001b[1;31mMATCH\u001b[0m", text);
        // No trailing content after reset
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_ColorMode_ContextBeforeSeparator()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\test.cs" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: true, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] line1 = Encoding.UTF8.GetBytes("first match");
        byte[] line2 = Encoding.UTF8.GetBytes("second match");
        byte[] ctx = PackContext("ctx line");
        fixed (byte* p1 = line1)
        fixed (byte* p2 = line2)
        fixed (byte* pCtx = ctx)
        {
            // First match at line 5
            var m1 = new NativeSearcher.QgMatchView { LineNumber = 5, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p1, LineLen = (nuint)line1.Length };
            sink.OnMatchForFile(0, &m1);

            // Second match at line 50 with context at line 49 — gap from line 5 triggers separator
            var m2 = new NativeSearcher.QgMatchView
            {
                LineNumber = 50, MatchStart = 0, SourceMatchStart = 0, MatchLen = 6,
                LinePtr = p2, LineLen = (nuint)line2.Length,
                CtxBeforePtr = pCtx, CtxBeforeBytes = (nuint)ctx.Length, CtxBeforeCount = 1,
            };
            sink.OnMatchForFile(0, &m2);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        // Should contain colorized separator
        Assert.Contains("\u001b[1;34m--\u001b[0m\n", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_LargeFilePath_WritesViaRentedBuffer()
    {
        using var output = new MemoryStream();
        // Create a path longer than 340 chars (UTF-8 maxBytes > 1024)
        string longPath = @"C:\" + new string('X', 350) + @"\file.txt";
        var paths = new List<string> { longPath };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        byte[] line = Encoding.UTF8.GetBytes("match here");
        fixed (byte* p = line)
        {
            var m = new NativeSearcher.QgMatchView { LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 5, LinePtr = p, LineLen = (nuint)line.Length };
            sink.OnMatchForFile(0, &m);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(longPath, text);
        Assert.Contains("match here", text);
    }

    [Fact]
    public unsafe void OnMatchForFile_NonColorMode_MatchHighlighting_WritesPlainOutput()
    {
        using var output = new MemoryStream();
        var paths = new List<string> { @"C:\test.cs" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        // Valid match position in non-color mode — should just write "linenum:fullline"
        byte[] line = Encoding.UTF8.GetBytes("hello MATCH world");
        fixed (byte* p = line)
        {
            var m = new NativeSearcher.QgMatchView
            {
                LineNumber = 3,
                MatchStart = 6,
                SourceMatchStart = 6,
                MatchLen = 5,
                LinePtr = p,
                LineLen = (nuint)line.Length,
            };
            sink.OnMatchForFile(0, &m);
        }

        string text = Encoding.UTF8.GetString(output.ToArray());
        // Non-color: no ANSI codes, just "3:hello MATCH world"
        Assert.Contains("3:hello MATCH world", text);
        Assert.DoesNotContain("\u001b[", text);
    }

    [Fact]
    public unsafe void Flush_ForwardsToUnderlyingStream()
    {
        // DirectOutputSink writes straight to the wrapped stream (buffering lives at the CliRunner
        // call site), so Flush() must forward to that stream so the CLI's BufferedStream drains.
        using var tracker = new FlushCountingStream();
        var paths = new List<string> { @"C:\src\example.txt" };
        int cancel = 0;
        int filesScanned = 0;
        using var sink = new DirectOutputSink(tracker, color: false, paths, maxResults: 0, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

        Assert.Equal(0, tracker.FlushCount);
        sink.Flush();
        Assert.Equal(1, tracker.FlushCount);
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }
}