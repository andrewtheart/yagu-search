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
    public async Task ReadAsync_SeekableStream_ReportsProgressViaBoth()
    {
        // A seekable stream triggers ProgressStream (parse-phase: 0→0.5, enum-phase: 0.5→1.0)
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
        // Progress should include the parse phase (0→0.5 via ProgressStream) and end at 1.0
        Assert.Contains(progressValues, v => v >= 0.45 && v <= 0.55); // around 0.5 (end of parse)
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
}
