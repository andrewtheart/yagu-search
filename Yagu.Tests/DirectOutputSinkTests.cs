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
        using var sink = new DirectOutputSink(output, color: false, paths, maxResults: 2, currentTotalMatches: 0, (IntPtr)(&cancel), &filesScanned);

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
}