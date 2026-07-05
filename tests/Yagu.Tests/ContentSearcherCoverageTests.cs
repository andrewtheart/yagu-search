using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ContentSearcherExtendedCoverageTests
{
    [Fact]
    public void FindMatches_Regex_FindsMultiple()
    {
        var regex = new Regex(@"\d+", RegexOptions.Compiled);
        var hits = ContentSearcher.FindMatches("abc 123 def 456 ghi", regex, null, StringComparison.Ordinal);
        Assert.Equal(2, hits.Count);
        Assert.Equal((4, 3), hits[0]);
        Assert.Equal((12, 3), hits[1]);
    }

    [Fact]
    public void FindMatches_Regex_NoMatch()
    {
        var regex = new Regex(@"\d+", RegexOptions.Compiled);
        var hits = ContentSearcher.FindMatches("no digits here", regex, null, StringComparison.Ordinal);
        Assert.Empty(hits);
    }

    [Fact]
    public void FindMatches_Regex_SkipsZeroWidthMatches()
    {
        // A pattern that could match zero-width
        var regex = new Regex(@"x*", RegexOptions.Compiled);
        var hits = ContentSearcher.FindMatches("abc", regex, null, StringComparison.Ordinal);
        // All matches are zero-width, should be skipped
        Assert.Empty(hits);
    }

    [Fact]
    public void FindMatches_Literal_CaseSensitive()
    {
        var hits = ContentSearcher.FindMatches("Hello HELLO hello", null, "hello", StringComparison.Ordinal);
        Assert.Single(hits);
        Assert.Equal((12, 5), hits[0]);
    }

    [Fact]
    public void FindMatches_Literal_CaseInsensitive()
    {
        var hits = ContentSearcher.FindMatches("Hello HELLO hello", null, "hello", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, hits.Count);
        Assert.Equal((0, 5), hits[0]);
        Assert.Equal((6, 5), hits[1]);
        Assert.Equal((12, 5), hits[2]);
    }

    [Fact]
    public void FindMatches_Literal_NoMatch()
    {
        var hits = ContentSearcher.FindMatches("hello world", null, "xyz", StringComparison.Ordinal);
        Assert.Empty(hits);
    }

    [Fact]
    public void FindMatches_NullLiteral_NoHits()
    {
        var hits = ContentSearcher.FindMatches("hello world", null, null, StringComparison.Ordinal);
        Assert.Empty(hits);
    }

    [Fact]
    public void FindMatches_EmptyLiteral_NoHits()
    {
        var hits = ContentSearcher.FindMatches("hello world", null, "", StringComparison.Ordinal);
        Assert.Empty(hits);
    }

    [Fact]
    public void FindMatches_Literal_MultipleOccurrences()
    {
        var hits = ContentSearcher.FindMatches("ababab", null, "ab", StringComparison.Ordinal);
        Assert.Equal(3, hits.Count);
        Assert.Equal((0, 2), hits[0]);
        Assert.Equal((2, 2), hits[1]);
        Assert.Equal((4, 2), hits[2]);
    }

    [Fact]
    public void ConfigureGates_PositiveValues_SetsLimits()
    {
        // Should not throw
        ContentSearcher.ConfigureGates(8, 32);
        // Restore defaults
        ContentSearcher.ConfigureGates(0, 0);
    }

    [Fact]
    public void ConfigureGates_ZeroValues_ResetsDefaults()
    {
        ContentSearcher.ConfigureGates(0, 0);
        // Should not throw - just verifies no crash
    }

    [Fact]
    public async Task SearchFileAsync_NonExistentFile_ReturnsSkipNotFound()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var options = new SearchOptions
        {
            Directory = @"C:\nonexistent",
            Query = "test",
        };
        var searcher = new ContentSearcher();
        var result = await searcher.SearchFileAsync(
            @"C:\nonexistent\file_that_does_not_exist_12345.txt",
            null, "test", StringComparison.Ordinal,
            options, channel.Writer, CancellationToken.None);
        Assert.Equal(ContentSearcher.SkipNotFound, result);
    }

    [Fact]
    public async Task SearchFileAsync_FileTooSmall_ReturnsSkipTooSmall()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "hi");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "hi",
                MinFileSizeBytes = 1_000_000,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "hi", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(ContentSearcher.SkipTooSmall, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_FileTooLarge_ReturnsSkipTooLarge()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, new string('x', 1000));
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "x",
                MaxFileSizeBytes = 10,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "x", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(ContentSearcher.SkipTooLarge, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_BinaryFile_ReturnsSkipBinary()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Write binary content (PNG magic)
            File.WriteAllBytes(tmpFile, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[100]]);
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "test",
                SkipBinary = true,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "test", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(ContentSearcher.SkipBinary, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_TextFileWithMatches_ReturnsMatchCount()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "line one\nline two match\nline three\nline four match\n");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "match",
                ContextLines = 0,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "match", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(2, result);

            channel.Writer.Complete();
            var results = new List<SearchResult>();
            await foreach (var r in channel.Reader.ReadAllAsync())
                results.Add(r);
            Assert.Equal(2, results.Count);
            Assert.Equal(2, results[0].LineNumber);
            Assert.Equal(4, results[1].LineNumber);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_WithContextLines_IncludesContext()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "line 1\nline 2\nline 3 MATCH\nline 4\nline 5\n");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "MATCH",
                ContextLines = 2,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "MATCH", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(1, result);

            channel.Writer.Complete();
            var results = new List<SearchResult>();
            await foreach (var r in channel.Reader.ReadAllAsync())
                results.Add(r);

            Assert.Single(results);
            Assert.Equal(2, results[0].ContextBefore.Count);
            Assert.Equal(2, results[0].ContextAfter.Count);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_RegexSearch_FindsMatches()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "foo 123\nbar 456\nbaz abc\n");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var regex = new Regex(@"\d{3}");
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = @"\d{3}",
                UseRegex = true,
                ContextLines = 0,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, regex, null, StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(2, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_MaxMatchesPerFile_StopsEarly()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 100; i++)
                sb.AppendLine($"line {i} match");
            File.WriteAllText(tmpFile, sb.ToString());

            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "match",
                ContextLines = 0,
                MaxMatchesPerFile = 5,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "match", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(5, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_SkipByExtension()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.png");
        try
        {
            File.WriteAllText(tmpFile, "not really a png but has extension");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "test",
                SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png" },
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "test", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(ContentSearcher.SkipByExtension, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_CancellationRequested_Throws()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "line 1\nline 2\nline 3\n");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "line",
                ContextLines = 0,
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var searcher = new ContentSearcher();
            // May throw OperationCanceledException or TaskCanceledException (which derives from it)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                searcher.SearchFileAsync(
                    tmpFile, null, "line", StringComparison.Ordinal,
                    options, channel.Writer, cts.Token));
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_EmptyFile_ReturnsZeroMatches()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "");
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "test",
                ContextLines = 0,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "test", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(0, result);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public async Task SearchFileAsync_Utf8BomFile_ReadsCorrectly()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "Hello match world", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var channel = Channel.CreateUnbounded<SearchResult>();
            var options = new SearchOptions
            {
                Directory = Path.GetDirectoryName(tmpFile)!,
                Query = "match",
                ContextLines = 0,
            };
            var searcher = new ContentSearcher();
            var result = await searcher.SearchFileAsync(
                tmpFile, null, "match", StringComparison.Ordinal,
                options, channel.Writer, CancellationToken.None);
            Assert.Equal(1, result);
        }
        finally { File.Delete(tmpFile); }
    }
}

// ─── StreamingSink unit tests ───────────────────────────────────────────────
public sealed class StreamingSinkTests
{
    [Fact]
    public unsafe void OnMatch_WritesToChannel()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var metadata = new FileMetadata(100, DateTime.Now, DateTime.Now);
        var sink = new ContentSearcher.StreamingSink(
            @"C:\test\file.cs", channel.Writer, contextLines: 0, metadata, CancellationToken.None);

        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("hello world");
        fixed (byte* linePtr = lineBytes)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 42,
                MatchStart = 6,
                SourceMatchStart = 6,
                MatchLen = 5,
                LinePtr = linePtr,
                LineLen = (nuint)lineBytes.Length,
                CtxBeforePtr = null,
                CtxBeforeBytes = 0,
                CtxBeforeCount = 0,
                CtxAfterPtr = null,
                CtxAfterBytes = 0,
                CtxAfterCount = 0,
            };

            int ret = sink.OnMatch(&view);
            Assert.Equal(0, ret); // success
            Assert.Equal(1, sink.Emitted);
        }

        Assert.True(channel.Reader.TryRead(out var result));
        Assert.Equal("hello world", result.MatchLine);
        Assert.Equal(42, result.LineNumber);
        Assert.Equal(6, result.MatchStartColumn);
        Assert.Equal(5, result.MatchLength);
    }

    [Fact]
    public unsafe void OnMatch_Cancelled_ReturnsOne()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var channel = Channel.CreateUnbounded<SearchResult>();
        var metadata = new FileMetadata(100, DateTime.Now, DateTime.Now);
        var sink = new ContentSearcher.StreamingSink(
            @"C:\test\file.cs", channel.Writer, contextLines: 0, metadata, cts.Token);

        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("test");
        fixed (byte* linePtr = lineBytes)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchStart = 0,
                SourceMatchStart = 0,
                MatchLen = 4,
                LinePtr = linePtr,
                LineLen = (nuint)lineBytes.Length,
                CtxBeforePtr = null,
                CtxBeforeBytes = 0,
                CtxBeforeCount = 0,
                CtxAfterPtr = null,
                CtxAfterBytes = 0,
                CtxAfterCount = 0,
            };

            int ret = sink.OnMatch(&view);
            Assert.Equal(1, ret); // cancelled
            Assert.Equal(0, sink.Emitted);
        }
    }

    [Fact]
    public unsafe void OnMatch_ChannelCompleted_ReturnsOne()
    {
        var channel = Channel.CreateBounded<SearchResult>(1);
        var metadata = new FileMetadata(100, DateTime.Now, DateTime.Now);
        var sink = new ContentSearcher.StreamingSink(
            @"C:\test\file.cs", channel.Writer, contextLines: 0, metadata, CancellationToken.None);

        // Complete the channel writer so TryWrite returns false and WaitToWriteAsync returns false
        channel.Writer.Complete();

        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("match");
        fixed (byte* linePtr = lineBytes)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchStart = 0,
                SourceMatchStart = 0,
                MatchLen = 5,
                LinePtr = linePtr,
                LineLen = (nuint)lineBytes.Length,
                CtxBeforePtr = null,
                CtxBeforeBytes = 0,
                CtxBeforeCount = 0,
                CtxAfterPtr = null,
                CtxAfterBytes = 0,
                CtxAfterCount = 0,
            };

            int ret = sink.OnMatch(&view);
            Assert.Equal(1, ret); // channel closed
            Assert.Equal(0, sink.Emitted);
        }
    }

    [Fact]
    public unsafe void OnMatch_BoundedChannelFull_BackpressureWritesEventually()
    {
        var channel = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

        var metadata = new FileMetadata(100, DateTime.Now, DateTime.Now);
        var sink = new ContentSearcher.StreamingSink(
            @"C:\test\file.cs", channel.Writer, contextLines: 0, metadata, CancellationToken.None);

        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("line");
        fixed (byte* linePtr = lineBytes)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1,
                MatchStart = 0,
                SourceMatchStart = 0,
                MatchLen = 4,
                LinePtr = linePtr,
                LineLen = (nuint)lineBytes.Length,
                CtxBeforePtr = null,
                CtxBeforeBytes = 0,
                CtxBeforeCount = 0,
                CtxAfterPtr = null,
                CtxAfterBytes = 0,
                CtxAfterCount = 0,
            };

            // First write should succeed immediately (channel capacity=1)
            int ret1 = sink.OnMatch(&view);
            Assert.Equal(0, ret1);

            // Drain the channel so the second write can proceed
            Assert.True(channel.Reader.TryRead(out _));

            // Second write should succeed now that space is available
            view.LineNumber = 2;
            int ret2 = sink.OnMatch(&view);
            Assert.Equal(0, ret2);
            Assert.Equal(2, sink.Emitted);
        }
    }

    [Fact]
    public unsafe void OnMatch_WithContextLines_DecodesContext()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var metadata = new FileMetadata(100, DateTime.Now, DateTime.Now);
        var sink = new ContentSearcher.StreamingSink(
            @"C:\test\file.cs", channel.Writer, contextLines: 2, metadata, CancellationToken.None);

        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("match here");
        // Build context: length-prefixed format (4-byte LE length + UTF-8 bytes per line)
        byte[] beforeLine = System.Text.Encoding.UTF8.GetBytes("before");
        byte[] ctxBefore = new byte[4 + beforeLine.Length];
        BitConverter.GetBytes((uint)beforeLine.Length).CopyTo(ctxBefore, 0);
        Array.Copy(beforeLine, 0, ctxBefore, 4, beforeLine.Length);

        byte[] afterLine = System.Text.Encoding.UTF8.GetBytes("after");
        byte[] ctxAfter = new byte[4 + afterLine.Length];
        BitConverter.GetBytes((uint)afterLine.Length).CopyTo(ctxAfter, 0);
        Array.Copy(afterLine, 0, ctxAfter, 4, afterLine.Length);

        fixed (byte* linePtr = lineBytes)
        fixed (byte* beforePtr = ctxBefore)
        fixed (byte* afterPtr = ctxAfter)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 5,
                MatchStart = 0,
                SourceMatchStart = 0,
                MatchLen = 5,
                LinePtr = linePtr,
                LineLen = (nuint)lineBytes.Length,
                CtxBeforePtr = beforePtr,
                CtxBeforeBytes = (nuint)ctxBefore.Length,
                CtxBeforeCount = 1,
                CtxAfterPtr = afterPtr,
                CtxAfterBytes = (nuint)ctxAfter.Length,
                CtxAfterCount = 1,
            };

            int ret = sink.OnMatch(&view);
            Assert.Equal(0, ret);
        }

        Assert.True(channel.Reader.TryRead(out var result));
        Assert.Equal("match here", result.MatchLine);
        Assert.Single(result.ContextBefore);
        Assert.Equal("before", result.ContextBefore[0]);
        Assert.Single(result.ContextAfter);
        Assert.Equal("after", result.ContextAfter[0]);
    }

    [Fact]
    public unsafe void OnMatch_CachesFileMetadataOnlyOnce()
    {
        var channel = Channel.CreateUnbounded<SearchResult>();
        var metadata = new FileMetadata(12345, DateTime.Now, DateTime.Now);
        var sink = new ContentSearcher.StreamingSink(
            @"C:\test\cached.cs", channel.Writer, contextLines: 0, metadata, CancellationToken.None);

        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("x");
        fixed (byte* linePtr = lineBytes)
        {
            var view = new Native.NativeSearcher.QgMatchView
            {
                LineNumber = 1, MatchStart = 0, SourceMatchStart = 0, MatchLen = 1,
                LinePtr = linePtr, LineLen = 1,
                CtxBeforePtr = null, CtxBeforeBytes = 0, CtxBeforeCount = 0,
                CtxAfterPtr = null, CtxAfterBytes = 0, CtxAfterCount = 0,
            };

            sink.OnMatch(&view);
            sink.OnMatch(&view);
            Assert.Equal(2, sink.Emitted);
        }

        // Verify metadata was cached
        Assert.True(FileMetadataCache.TryGet(@"C:\test\cached.cs", out var cachedMeta));
        Assert.Equal(12345, cachedMeta.Length);
    }
}
