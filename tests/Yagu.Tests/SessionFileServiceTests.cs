using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public class SessionFileServiceTests
{
    private static SessionFileService.SessionStats MakeStats() =>
        new(new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromSeconds(5.5), 1200, 98_000_000, 42);

    private static SearchResult MakeResult(int i) =>
        new(FilePath: $@"C:\code\file{i}.cs",
            LineNumber: 100 + i,
            MatchLine: $"var x{i} = needle + {i};",
            MatchStartColumn: 10,
            MatchLength: 6,
            ContextBefore: [$"// before line {i}"],
            ContextAfter: [$"// after line {i}"]);

    [Fact]
    public async Task WriteAndRead_Roundtrip_PreservesHeaderAndResults()
    {
        var stats = MakeStats();
        var results = Enumerable.Range(0, 5).Select(MakeResult).ToList();

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "needle", @"C:\code", stats, results);

        ms.Position = 0;
        SessionFileService.SessionHeader? capturedHeader = null;
        var readResults = new List<SearchResult>();

        var header = await SessionFileService.ReadAsync(
            ms,
            h => capturedHeader = h,
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.NotNull(capturedHeader);
        Assert.Equal(SessionFileService.SchemaVersion, header.SchemaVersion);
        Assert.Equal("needle", header.Query);
        Assert.Equal(@"C:\code", header.SearchRoot);
        Assert.Equal(5, header.ResultCount);
        Assert.Equal(stats.FilesScanned, header.Stats.FilesScanned);
        Assert.Equal(stats.BytesScanned, header.Stats.BytesScanned);
        Assert.Equal(stats.MatchesFound, header.Stats.MatchesFound);
        Assert.Equal(5, readResults.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(results[i].FilePath, readResults[i].FilePath);
            Assert.Equal(results[i].LineNumber, readResults[i].LineNumber);
            Assert.Equal(results[i].MatchLine, readResults[i].MatchLine);
            Assert.Equal(results[i].MatchStartColumn, readResults[i].MatchStartColumn);
            Assert.Equal(results[i].MatchLength, readResults[i].MatchLength);
            Assert.Equal(results[i].ContextBefore, readResults[i].ContextBefore);
            Assert.Equal(results[i].ContextAfter, readResults[i].ContextAfter);
        }
    }

    [Fact]
    public async Task WriteAndRead_EmptyResults_RoundtripsCleanly()
    {
        var stats = MakeStats();
        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, Array.Empty<SearchResult>());

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(0, header.ResultCount);
        Assert.Empty(readResults);
    }

    [Fact]
    public async Task WriteAndRead_LargeSet_BatchesProgress()
    {
        var stats = MakeStats();
        var results = Enumerable.Range(0, 2048).Select(MakeResult).ToList();
        var progressValues = new System.Collections.Concurrent.ConcurrentBag<double>();

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, results,
            new Progress<double>(v => progressValues.Add(v)));

        // Allow background Progress<T> callbacks to complete
        await Task.Delay(50);
        Assert.Contains(progressValues, v => v > 0.0 && v < 1.0);
        Assert.Contains(progressValues, v => v == 1.0);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        var readProgress = new System.Collections.Concurrent.ConcurrentBag<double>();
        await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; },
            new Progress<double>(v => readProgress.Add(v)));

        Assert.Equal(2048, readResults.Count);
    }

    [Fact]
    public async Task WriteStreamingAsync_GroupByGroup_Roundtrips()
    {
        var stats = MakeStats();
        var groups = new[] {
            Enumerable.Range(0, 3).Select(MakeResult).ToList(),
            Enumerable.Range(3, 4).Select(MakeResult).ToList()
        };
        int totalResults = groups.Sum(g => g.Count);
        var releasedGroups = new List<int>();

        using var ms = new MemoryStream();
        await SessionFileService.WriteStreamingAsync(
            ms, "needle", @"C:\code", stats, totalResults, groups.Length,
            gi => groups[gi],
            gi => releasedGroups.Add(gi));

        Assert.Equal([0, 1], releasedGroups);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(7, header.ResultCount);
        Assert.Equal(7, readResults.Count);
    }

    [Fact]
    public async Task WriteAsync_Cancellation_ThrowsOperationCanceled()
    {
        var stats = MakeStats();
        var results = Enumerable.Range(0, 5000).Select(MakeResult).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, results, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ReadAsync_InvalidSchema_ThrowsInvalidDataException()
    {
        var json = """{"schema":"not-yagu","query":"x"}"""u8.ToArray();
        using var ms = new MemoryStream(json);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReadAsync_UnsupportedVersion_ThrowsInvalidDataException()
    {
        var json = """{"schema":"yagu-session/v99","query":"x"}"""u8.ToArray();
        using var ms = new MemoryStream(json);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReadAsync_MissingResultsArray_ReturnsHeaderOnly()
    {
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":100,"filesScanned":10,"bytesScanned":500,"matchesFound":0},"resultCount":0}"""u8.ToArray();
        using var ms = new MemoryStream(json);

        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal("q", header.Query);
        Assert.Empty(readResults);
    }

    [Fact]
    public async Task ReadAsync_ResultWithMissingFields_SkipsGracefully()
    {
        // Result with empty file path should be skipped by ParseResult
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":2,"results":[{"f":"","ln":1,"ml":"x","mc":0,"sc":0,"mlen":1,"b":[],"a":[]},{"f":"C:\\a.cs","ln":5,"ml":"hello","mc":0,"sc":0,"mlen":5,"b":["before"],"a":["after"]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);

        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(@"C:\a.cs", readResults[0].FilePath);
    }

    [Fact]
    public async Task WriteAsync_NullArguments_Throws()
    {
        var stats = MakeStats();
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteAsync(null!, "q", @"C:\x", stats, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteAsync(ms, null!, @"C:\x", stats, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteAsync(ms, "q", null!, stats, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteAsync(ms, "q", @"C:\x", null!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, null!));
    }

    [Fact]
    public async Task WriteStreamingAsync_NullArguments_Throws()
    {
        var stats = MakeStats();
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteStreamingAsync(null!, "q", @"C:\x", stats, 0, 0, _ => [], null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteStreamingAsync(ms, null!, @"C:\x", stats, 0, 0, _ => [], null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteStreamingAsync(ms, "q", null!, stats, 0, 0, _ => [], null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteStreamingAsync(ms, "q", @"C:\x", null!, 0, 0, _ => [], null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.WriteStreamingAsync(ms, "q", @"C:\x", stats, 0, 0, null!, null));
    }

    [Fact]
    public async Task ReadAsync_NullArguments_Throws()
    {
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.ReadAsync(null!, _ => { }, _ => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.ReadAsync(ms, null!, _ => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, null!));
    }

    [Fact]
    public async Task WriteAndRead_ContextWithEmptyStrings_Preserved()
    {
        var stats = MakeStats();
        var result = new SearchResult(@"C:\f.cs", 1, "match", 0, 5,
            ContextBefore: ["", "line2"],
            ContextAfter: ["a", ""]);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, [result]);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(["", "line2"], readResults[0].ContextBefore);
        Assert.Equal(["a", ""], readResults[0].ContextAfter);
    }

    [Fact]
    public async Task ReadAsync_SeekableStream_ReportsProgress()
    {
        // The streaming reader reports 0.0 at the start, byte-based fractions for a
        // seekable stream, and 1.0 at the end.
        var stats = MakeStats();
        var results = Enumerable.Range(0, 100).Select(MakeResult).ToList();

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, results);

        ms.Position = 0;
        var progressValues = new System.Collections.Concurrent.ConcurrentBag<double>();
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; },
            new Progress<double>(v => progressValues.Add(v)));

        Assert.Equal(100, readResults.Count);
        Assert.Contains(progressValues, v => v == 0.0);
        Assert.Contains(progressValues, v => v == 1.0);
    }

    [Fact]
    public async Task ReadAsync_LargeStream_ReportsIntermediateProgress()
    {
        // A stream large enough to span several buffer refills should report at least one
        // intermediate byte-based progress fraction strictly between 0 and 1.
        var stats = MakeStats();
        var results = Enumerable.Range(0, 20_000).Select(MakeResult).ToList();

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, results);

        ms.Position = 0;
        var progressValues = new System.Collections.Concurrent.ConcurrentBag<double>();
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(
            ms,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; },
            new Progress<double>(v => progressValues.Add(v)));

        Assert.Equal(20_000, readResults.Count);
        Assert.Contains(progressValues, v => v > 0.0 && v < 1.0);
        Assert.Contains(progressValues, v => v == 1.0);
    }

    [Fact]
    public async Task ReadAsync_NonSeekableStream_StillWorks()
    {
        // Non-seekable stream skips ProgressStream but still parses correctly
        var stats = MakeStats();
        var results = Enumerable.Range(0, 5).Select(MakeResult).ToList();

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, results);

        var data = ms.ToArray();
        using var nonSeekable = new NonSeekableStream(data);
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            nonSeekable,
            _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(5, header.ResultCount);
        Assert.Equal(5, readResults.Count);
    }

    [Fact]
    public async Task WriteStreamingAsync_ZeroGroups_ProducesValidEmptySession()
    {
        var stats = MakeStats();
        using var ms = new MemoryStream();
        await SessionFileService.WriteStreamingAsync(
            ms, "q", @"C:\x", stats, 0, 0,
            _ => Array.Empty<SearchResult>(), null);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(0, header.ResultCount);
        Assert.Empty(readResults);
    }

    [Fact]
    public async Task WriteStreamingAsync_Cancellation_ThrowsOperationCanceled()
    {
        var stats = MakeStats();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var ms = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SessionFileService.WriteStreamingAsync(
                ms, "q", @"C:\x", stats, 100, 5,
                _ => Enumerable.Range(0, 20).Select(MakeResult).ToList(),
                null, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task WriteStreamingAsync_ProgressAndRelease_Called()
    {
        var stats = MakeStats();
        int groupCount = 3;
        int perGroup = 50;
        int total = groupCount * perGroup;
        var releasedGroups = new List<int>();
        var progressValues = new System.Collections.Concurrent.ConcurrentBag<double>();

        using var ms = new MemoryStream();
        await SessionFileService.WriteStreamingAsync(
            ms, "q", @"C:\x", stats, total, groupCount,
            gi => Enumerable.Range(gi * perGroup, perGroup).Select(MakeResult).ToList(),
            gi => releasedGroups.Add(gi),
            new Progress<double>(v => progressValues.Add(v)));

        // Allow Progress<T> threadpool callbacks
        await Task.Delay(50);

        // All groups should have been released
        Assert.Equal([0, 1, 2], releasedGroups);
        // Progress should start at 0 and end at 1
        Assert.Contains(progressValues, v => v == 0.0);
        Assert.Contains(progressValues, v => v == 1.0);
        // Intermediate progress values
        Assert.Contains(progressValues, v => v > 0.0 && v < 1.0);
    }

    [Fact]
    public async Task WriteStreamingAsync_LargeGroup_FlushesAtBatchBoundary()
    {
        // Use enough results to trigger the batch flush (written % ResultBatchSize == 0)
        // ResultBatchSize is 1024, so we need at least 1025 results
        var stats = MakeStats();
        int total = 1100; // more than ResultBatchSize (1024)
        var progressValues = new System.Collections.Concurrent.ConcurrentBag<double>();

        using var ms = new MemoryStream();
        await SessionFileService.WriteStreamingAsync(
            ms, "q", @"C:\x", stats, total, 1,
            _ => Enumerable.Range(0, total).Select(MakeResult).ToList(),
            null,
            new Progress<double>(v => progressValues.Add(v)));

        await Task.Delay(50);

        // Should produce valid session that roundtrips
        ms.Position = 0;
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });
        Assert.Equal(total, header.ResultCount);
        Assert.Equal(total, readResults.Count);
    }

    /// <summary>A stream wrapper that reports CanSeek=false to avoid ProgressStream.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task ReadAsync_StreamLengthExceedsInt32_DoesNotOverflow()
    {
        // Regression (reported: a 3 GB .yagu-session failed to import with
        // "ArgumentOutOfRangeException: minimumLength ('-2147483648')"). The old reader used
        // JsonDocument.ParseAsync, which materialises the whole file into a single int-sized
        // allocation; a >2 GB stream overflowed that sizing (long Length wrapped to a negative
        // int passed to ArrayPool.Rent). The streaming reader must load a session regardless of
        // a reported Length beyond int.MaxValue.
        var stats = MakeStats();
        var results = Enumerable.Range(0, 10).Select(MakeResult).ToList();
        using var real = new MemoryStream();
        await SessionFileService.WriteAsync(real, "q", @"C:\x", stats, results);

        using var huge = new HugeLengthStream(real.ToArray(), reportedLength: 3L * 1024 * 1024 * 1024);
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            huge, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(10, header.ResultCount);
        Assert.Equal(10, readResults.Count);
        Assert.Equal(results[0].FilePath, readResults[0].FilePath);
    }

    /// <summary>
    /// A seekable stream whose <see cref="Length"/> reports a value larger than
    /// <see cref="int.MaxValue"/> (simulating a &gt;2 GB session file) while backing only a
    /// small buffer, to prove the reader never sizes an allocation from that Length.
    /// </summary>
    private sealed class HugeLengthStream(byte[] data, long reportedLength) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reportedLength;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _inner.Dispose(); base.Dispose(disposing); }
    }

    // ─── Branch coverage: ParseResult fallback branches ─────────────────────

    [Fact]
    public async Task ReadAsync_ResultMissingScProperty_FallsBackToMatchStart()
    {
        // "sc" absent → sourceMatchStart defaults to matchStart (mc=7)
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":1,"results":[{"f":"C:\\a.cs","ln":3,"ml":"hello","mc":7,"mlen":5,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(7, readResults[0].MatchStartColumn);
        // SourceMatchStartColumn falls back to mc when sc is absent
        Assert.Equal(7, readResults[0].SourceMatchStartColumn);
    }

    [Fact]
    public async Task ReadAsync_ResultMissingMlenProperty_DefaultsToZero()
    {
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":1,"results":[{"f":"C:\\a.cs","ln":1,"ml":"x","mc":0,"sc":0,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(0, readResults[0].MatchLength);
    }

    [Fact]
    public async Task ReadAsync_ResultMissingLnAndMcProperties_DefaultsToZero()
    {
        // Missing "ln" and "mc" → default to 0
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":1,"results":[{"f":"C:\\a.cs","ml":"match","mlen":3,"sc":0,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(0, readResults[0].LineNumber);
        Assert.Equal(0, readResults[0].MatchStartColumn);
    }

    [Fact]
    public async Task ReadAsync_ResultMissingMlProperty_DefaultsToEmptyString()
    {
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":1,"results":[{"f":"C:\\a.cs","ln":1,"mc":0,"sc":0,"mlen":1,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(string.Empty, readResults[0].MatchLine);
    }

    [Fact]
    public async Task ReadAsync_NonObjectItemInResultsArray_Skipped()
    {
        // A non-object (number) item in results should be skipped by ParseResult
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":2,"results":[42,{"f":"C:\\a.cs","ln":1,"ml":"x","mc":0,"sc":0,"mlen":1,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(@"C:\a.cs", readResults[0].FilePath);
    }

    [Fact]
    public async Task ReadAsync_MissingStatsObject_UsesDefaults()
    {
        // No "stats" property at all → ReadStats returns defaults
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","resultCount":0,"results":[]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        await SessionFileService.ReadAsync(ms, h => captured = h, _ => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal(0, captured.Stats.FilesScanned);
        Assert.Equal(0, captured.Stats.BytesScanned);
        Assert.Equal(0, captured.Stats.MatchesFound);
        Assert.Equal(TimeSpan.Zero, captured.Stats.Elapsed);
    }

    [Fact]
    public async Task ReadAsync_StatsWithMissingFields_UsesDefaults()
    {
        // "stats" is an object but individual fields are missing
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{},"resultCount":0,"results":[]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        await SessionFileService.ReadAsync(ms, h => captured = h, _ => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal(0, captured.Stats.FilesScanned);
        Assert.Equal(0, captured.Stats.BytesScanned);
        Assert.Equal(TimeSpan.Zero, captured.Stats.Elapsed);
    }

    [Fact]
    public async Task ReadAsync_ContextPropertyIsNotArray_TreatedAsEmpty()
    {
        // "b" and "a" are strings instead of arrays → ReadStringArray returns empty
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":1,"results":[{"f":"C:\\a.cs","ln":1,"ml":"x","mc":0,"sc":0,"mlen":1,"b":"notarray","a":"notarray"}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Empty(readResults[0].ContextBefore);
        Assert.Empty(readResults[0].ContextAfter);
    }

    [Fact]
    public async Task ReadAsync_MissingSavedUtcAndQuery_UsesDefaults()
    {
        // Missing savedUtc and query/searchRoot properties → defaults
        var json = """{"schema":"yagu-session/v1","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":100,"filesScanned":1,"bytesScanned":100,"matchesFound":1},"resultCount":0,"results":[]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        await SessionFileService.ReadAsync(ms, h => captured = h, _ => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal(string.Empty, captured.Query);
        Assert.Equal(string.Empty, captured.SearchRoot);
    }

    [Fact]
    public async Task ReadAsync_MissingResultCountProperty_DefaultsToZero()
    {
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"results":[]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        await SessionFileService.ReadAsync(ms, h => captured = h, _ => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal(0, captured.ResultCount);
    }

    [Fact]
    public async Task WriteAndRead_NullContextArrays_TreatedAsEmpty()
    {
        // SearchResult with null ContextBefore/ContextAfter — exercises WriteStringArray null branch
        var stats = MakeStats();
        var result = new SearchResult(@"C:\f.cs", 1, "match", 0, 5, null!, null!);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, [result]);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Empty(readResults[0].ContextBefore);
        Assert.Empty(readResults[0].ContextAfter);
    }

    [Fact]
    public async Task WriteAndRead_NullMatchLine_WrittenAsEmpty()
    {
        var stats = MakeStats();
        var result = new SearchResult(@"C:\f.cs", 1, null!, 0, 5, [], []);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, [result]);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(string.Empty, readResults[0].MatchLine);
    }

    [Fact]
    public async Task ReadAsync_EmptySchemaProperty_ThrowsInvalidDataException()
    {
        var json = """{"schema":"","query":"x"}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReadAsync_MissingSchemaProperty_ThrowsInvalidDataException()
    {
        var json = """{"query":"x"}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, _ => Task.CompletedTask));
    }

    [Fact]
    public void SessionHeader_SavedUtc_ReturnsExpectedValue()
    {
        var now = DateTime.UtcNow;
        var header = new SessionFileService.SessionHeader(
            SchemaVersion: "1.0",
            SavedUtc: now,
            Query: "test",
            SearchRoot: @"C:\",
            Stats: new SessionFileService.SessionStats(DateTime.UtcNow, TimeSpan.FromSeconds(1), 100, 50000, 5),
            ResultCount: 5);

        Assert.Equal(now, header.SavedUtc);
    }

    // ─── Phase 1b: cross-line (multiline) span persistence (schema v2) ──────

    [Fact]
    public async Task WriteAndRead_MultilineSpan_RoundtripsAsV2()
    {
        var multi = new SearchResult(@"C:\code\ml.cs", 10, "START", 0, 5,
            ContextBefore: [], ContextAfter: ["after"])
        { MatchEndLineNumber = 12, MatchEndColumn = 4 };
        var single = MakeResult(1);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "START", @"C:\code", MakeStats(), [multi, single]);

        ms.Position = 0;
        var read = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms, _ => { }, batch => { read.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal("yagu-session/v2", header.SchemaVersion);
        Assert.Equal(SessionFileService.SchemaVersion, header.SchemaVersion);

        var mlRead = read.Single(r => r.LineNumber == 10);
        Assert.True(mlRead.IsMultilineMatch);
        Assert.Equal(12, mlRead.MatchEndLineNumber);
        Assert.Equal(4, mlRead.MatchEndColumn);

        var slRead = read.Single(r => r.FilePath == single.FilePath);
        Assert.False(slRead.IsMultilineMatch);
        Assert.Null(slRead.MatchEndLineNumber);
        Assert.Null(slRead.MatchEndColumn);
    }

    [Fact]
    public async Task WriteAndRead_MultilineSpanWithZeroEndColumn_Roundtrips()
    {
        var multi = new SearchResult(@"C:\ml.cs", 3, "x", 0, 1, [], [])
        { MatchEndLineNumber = 5, MatchEndColumn = 0 };

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\", MakeStats(), [multi]);

        ms.Position = 0;
        var read = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, b => { read.AddRange(b); return Task.CompletedTask; });

        Assert.True(read[0].IsMultilineMatch);
        Assert.Equal(5, read[0].MatchEndLineNumber);
        Assert.Equal(0, read[0].MatchEndColumn);
    }

    [Fact]
    public async Task WriteAndRead_MultilineWithNullEndColumn_WritesZero()
    {
        // A multiline result (MatchEndLineNumber set) with a null MatchEndColumn exercises the
        // write-side `?? 0`: mec is persisted as 0 and reads back as a concrete 0.
        var multi = new SearchResult(@"C:\ml.cs", 3, "x", 0, 1, [], [])
        { MatchEndLineNumber = 6 };
        Assert.True(multi.IsMultilineMatch);
        Assert.Null(multi.MatchEndColumn);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\", MakeStats(), [multi]);

        ms.Position = 0;
        var read = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, b => { read.AddRange(b); return Task.CompletedTask; });

        Assert.True(read[0].IsMultilineMatch);
        Assert.Equal(6, read[0].MatchEndLineNumber);
        Assert.Equal(0, read[0].MatchEndColumn);
    }

    [Fact]
    public async Task ReadAsync_V1SchemaWithoutSpanFields_LoadsAsSingleLine()
    {
        // A v1 file (no mel/mec) is still readable and loads as a single-line result.
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":1},"resultCount":1,"results":[{"f":"C:\\a.txt","ln":3,"ml":"x","mc":0,"sc":0,"mlen":1,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        var read = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms, h => captured = h, b => { read.AddRange(b); return Task.CompletedTask; });

        Assert.Equal("yagu-session/v1", header.SchemaVersion);
        Assert.Single(read);
        Assert.False(read[0].IsMultilineMatch);
        Assert.Null(read[0].MatchEndLineNumber);
        Assert.Null(read[0].MatchEndColumn);
    }

    [Fact]
    public async Task ReadAsync_V2ResultWithMelButMissingMec_DefaultsEndColumnToZero()
    {
        // A malformed v2 result carrying "mel" without "mec" still yields a multiline match (mec => 0).
        var json = """{"schema":"yagu-session/v2","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":1},"resultCount":1,"results":[{"f":"C:\\a.txt","ln":2,"ml":"x","mc":0,"sc":0,"mlen":1,"mel":4,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var read = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, b => { read.AddRange(b); return Task.CompletedTask; });

        Assert.Single(read);
        Assert.True(read[0].IsMultilineMatch);
        Assert.Equal(4, read[0].MatchEndLineNumber);
        Assert.Equal(0, read[0].MatchEndColumn);
    }

    // ─── Streaming reader: buffer/scope/date-fallback branch coverage ───────

    [Fact]
    public async Task ReadAsync_MalformedSavedUtc_FallsBackToCurrentTime()
    {
        // "savedUtc" present but unparseable → ParseDate falls back to DateTime.UtcNow
        // (the reader must not throw on a bad date).
        var json = """{"schema":"yagu-session/v1","savedUtc":"not-a-real-date","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":0,"results":[]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        var before = DateTime.UtcNow.AddSeconds(-5);

        var header = await SessionFileService.ReadAsync(ms, h => captured = h, _ => Task.CompletedTask);

        Assert.NotNull(captured);
        Assert.Equal("q", header.Query);
        // Fallback is "now", not the JSON's bogus value, so it is a fresh timestamp.
        Assert.True(header.SavedUtc >= before, $"SavedUtc {header.SavedUtc:o} should fall back to ~now");
    }

    [Fact]
    public async Task ReadAsync_ContextArrayWithNullElement_TreatedAsEmptyString()
    {
        // A JSON null inside a context array must be materialised as "" (not dropped, not a
        // null entry), preserving positional alignment.
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":1},"resultCount":1,"results":[{"f":"C:\\a.cs","ln":1,"ml":"x","mc":0,"sc":0,"mlen":1,"b":[null,"line2"],"a":["a",null]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { }, batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(["", "line2"], readResults[0].ContextBefore);
        Assert.Equal(["a", ""], readResults[0].ContextAfter);
    }

    [Fact]
    public async Task ReadAsync_UnknownObjectArrayAndScalarFields_AreSkipped()
    {
        // Forward-compat: unknown scalar/array/object fields at the root, in stats, and in a
        // result must be skipped without disturbing the known data (the Scope.Skip path).
        var json = """{"schema":"yagu-session/v2","savedUtc":"2025-01-01T00:00:00Z","query":"q","searchRoot":"C:\\x","future":42,"tags":["a","b"],"extra":{"deep":{"x":1}},"stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":3,"bytesScanned":0,"matchesFound":1,"newStat":9},"resultCount":1,"results":[{"f":"C:\\a.cs","ln":7,"ml":"hit","mc":0,"sc":0,"mlen":3,"z":99,"tags2":[1,2],"nested":{"k":1},"b":["ctx"],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? captured = null;
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms, h => captured = h,
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.NotNull(captured);
        Assert.Equal("q", header.Query);
        Assert.Equal(3, header.Stats.FilesScanned);   // known stats field survived the unknown ones
        Assert.Single(readResults);
        Assert.Equal(@"C:\a.cs", readResults[0].FilePath);
        Assert.Equal(7, readResults[0].LineNumber);
        Assert.Equal(["ctx"], readResults[0].ContextBefore);
    }

    [Fact]
    public async Task ReadAsync_EmptyStream_Throws()
    {
        // No bytes at all → the Utf8JsonReader reports there are no JSON tokens. The reader must
        // surface a clean parse failure (this is exactly the empty ".yagu-session" case).
        using var ms = new MemoryStream(Array.Empty<byte>());
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReadAsync_NonObjectRootJson_ThrowsInvalidDataException()
    {
        // A syntactically valid JSON document that is not a Yagu session object (here a bare
        // number) parses cleanly but never sets a schema, so the header-delivery fallback runs
        // and then the schema check rejects it as InvalidDataException.
        using var ms = new MemoryStream("42"u8.ToArray());
        SessionFileService.SessionHeader? captured = null;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionFileService.ReadAsync(ms, h => captured = h, _ => Task.CompletedTask));

        // The fallback delivered a (schema-less) header before the schema validation threw.
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task ReadAsync_ResultLargerThanReadBuffer_GrowsBufferAndParses()
    {
        // A single result whose match line exceeds the 128 KB read buffer forces the reader to
        // grow its buffer (a token that can't be consumed in one buffer's worth of data).
        var stats = MakeStats();
        string hugeLine = new('x', 200_000);
        var result = new SearchResult(@"C:\big.txt", 1, hugeLine, 0, 5, ["ctx-before"], ["ctx-after"]);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, [result]);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        var header = await SessionFileService.ReadAsync(
            ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Equal(1, header.ResultCount);
        Assert.Single(readResults);
        Assert.Equal(hugeLine, readResults[0].MatchLine);
        Assert.Equal(["ctx-before"], readResults[0].ContextBefore);
    }

    [Fact]
    public async Task ReadAsync_Cancellation_ThrowsOperationCanceled()
    {
        var stats = MakeStats();
        var results = Enumerable.Range(0, 5000).Select(MakeResult).ToList();
        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, results);
        ms.Position = 0;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SessionFileService.ReadAsync(
                ms, _ => { }, _ => Task.CompletedTask, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ReadAsync_WrongTypedNumericAndDateFields_DefaultGracefully()
    {
        // A corrupt / hand-edited session with wrong-typed values (strings, floats, bools, a
        // numeric date) must load without throwing: every numeric field defaults to 0, dates fall
        // back to "now", and the one valid result still comes through.
        var before = DateTime.UtcNow.AddSeconds(-5);
        var json = """{"schema":"yagu-session/v2","savedUtc":123,"query":"q","searchRoot":"C:\\x","stats":{"startedUtc":true,"elapsedMs":"nope","filesScanned":1.5,"bytesScanned":1.5,"matchesFound":2.5},"resultCount":"lots","results":[{"f":"C:\\a.cs","ln":"L","mc":1.5,"sc":1.5,"mlen":true,"mel":"M","mec":2.5,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? header = null;
        var readResults = new List<SearchResult>();

        header = await SessionFileService.ReadAsync(
            ms, h => header = h,
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.NotNull(header);
        Assert.Equal("q", header.Query);          // valid string field survives
        Assert.Equal(0, header.ResultCount);        // "lots" (string) → 0
        Assert.Equal(0, header.Stats.FilesScanned); // 1.5 (float) → 0
        Assert.Equal(0, header.Stats.BytesScanned); // 1.5 (float long) → 0
        Assert.Equal(0, header.Stats.MatchesFound); // 2.5 (float) → 0
        Assert.Equal(TimeSpan.Zero, header.Stats.Elapsed); // "nope" (string) → 0
        Assert.True(header.SavedUtc >= before);      // 123 (number) → fallback "now"

        var r = Assert.Single(readResults);
        Assert.Equal(@"C:\a.cs", r.FilePath);
        Assert.Equal(0, r.LineNumber);              // "L" (string) → 0
        Assert.Equal(0, r.MatchStartColumn);        // 1.5 (float) → 0
        Assert.Equal(0, r.SourceMatchStartColumn);  // "sc" invalid → falls back to mc (0)
        Assert.Equal(0, r.MatchLength);             // true (bool) → 0
        Assert.Null(r.MatchEndLineNumber);          // "mel" invalid → not multiline
        Assert.False(r.IsMultilineMatch);
    }

    [Fact]
    public async Task ReadAsync_NonSeekableStreamWithNoResults_LoadsHeaderOnly()
    {
        // Non-seekable + zero declared results exercises the progress fraction's final fallback
        // (no byte total AND no result count → 0.0).
        var stats = MakeStats();
        using var seekable = new MemoryStream();
        await SessionFileService.WriteAsync(seekable, "q", @"C:\x", stats, Array.Empty<SearchResult>());

        using var ns = new NonSeekableStream(seekable.ToArray());
        SessionFileService.SessionHeader? header = null;
        var readResults = new List<SearchResult>();

        header = await SessionFileService.ReadAsync(
            ns, h => header = h,
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.NotNull(header);
        Assert.Equal(0, header.ResultCount);
        Assert.Empty(readResults);
    }

    [Fact]
    public async Task ReadAsync_JsonNullStringFields_TreatedAsEmpty()
    {
        // Explicit JSON null for string-typed fields (query/searchRoot/f/ml) must become "" and not
        // throw. A result whose "f" is null has no file path, so it is skipped.
        var json = """{"schema":"yagu-session/v1","savedUtc":"2025-01-01T00:00:00Z","query":null,"searchRoot":null,"stats":{"startedUtc":"2025-01-01T00:00:00Z","elapsedMs":0,"filesScanned":0,"bytesScanned":0,"matchesFound":0},"resultCount":2,"results":[{"f":null,"ln":1,"ml":"x","mc":0,"sc":0,"mlen":1,"b":[],"a":[]},{"f":"C:\\a.cs","ln":2,"ml":null,"mc":0,"sc":0,"mlen":1,"b":[],"a":[]}]}"""u8.ToArray();
        using var ms = new MemoryStream(json);
        SessionFileService.SessionHeader? header = null;
        var readResults = new List<SearchResult>();

        header = await SessionFileService.ReadAsync(
            ms, h => header = h,
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.NotNull(header);
        Assert.Equal(string.Empty, header.Query);
        Assert.Equal(string.Empty, header.SearchRoot);
        var r = Assert.Single(readResults);           // the null-"f" result was skipped
        Assert.Equal(@"C:\a.cs", r.FilePath);
        Assert.Equal(string.Empty, r.MatchLine);       // ml:null → ""
    }

    [Fact]
    public async Task ReadAsync_NullSchema_ThrowsInvalidDataException()
    {
        // A JSON null schema decodes to "" → rejected as not a Yagu session.
        using var ms = new MemoryStream("""{"schema":null,"query":"x"}"""u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionFileService.ReadAsync(ms, _ => { }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task WriteAndRead_NullContextElement_WrittenAsEmptyString()
    {
        // A null element inside a context list must be written as "" (WriteStringArray's null-coalesce).
        var stats = MakeStats();
        var result = new SearchResult(@"C:\f.cs", 1, "match", 0, 5,
            ContextBefore: ["a", null!], ContextAfter: []);

        using var ms = new MemoryStream();
        await SessionFileService.WriteAsync(ms, "q", @"C:\x", stats, [result]);

        ms.Position = 0;
        var readResults = new List<SearchResult>();
        await SessionFileService.ReadAsync(ms, _ => { },
            batch => { readResults.AddRange(batch); return Task.CompletedTask; });

        Assert.Single(readResults);
        Assert.Equal(["a", ""], readResults[0].ContextBefore);
    }
}