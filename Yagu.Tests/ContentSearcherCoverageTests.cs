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
