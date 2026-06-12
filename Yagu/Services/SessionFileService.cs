using System.Text.Json;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Reads and writes <c>.yagu-session</c> files: a self-contained snapshot of a
/// search's results plus enough metadata to re-display them in the UI or CLI
/// without re-running the search. Format is a single streaming JSON document
/// (NOT the same as the report-export JSON), versioned via the <see cref="SchemaVersion"/>
/// constant so older readers can refuse newer files cleanly.
/// </summary>
public static class SessionFileService
{
    public const string FileExtension = ".yagu-session";
    public const string SchemaVersion = "yagu-session/v1";

    private const int ResultBatchSize = 1024;

    /// <summary>Stats captured at save time for display when a session is re-loaded.</summary>
    public sealed record SessionStats(
        DateTime StartedUtc,
        TimeSpan Elapsed,
        int FilesScanned,
        long BytesScanned,
        int MatchesFound);

    /// <summary>Header information returned to the caller before result streaming begins.</summary>
    public sealed record SessionHeader(
        string SchemaVersion,
        DateTime SavedUtc,
        string Query,
        string SearchRoot,
        SessionStats Stats,
        int ResultCount);

    /// <summary>Writes a session file containing the supplied results and metadata.</summary>
    /// <param name="progress">Optional progress reporter (0.0..1.0).</param>
    public static async Task WriteAsync(
        Stream output,
        string query,
        string searchRoot,
        SessionStats stats,
        IReadOnlyList<SearchResult> results,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(searchRoot);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(results);

        var jsonOpts = new JsonWriterOptions { Indented = false, SkipValidation = false };
        await using var writer = new Utf8JsonWriter(output, jsonOpts);

        writer.WriteStartObject();
        writer.WriteString("schema", SchemaVersion);
        writer.WriteString("savedUtc", DateTime.UtcNow.ToString("o"));
        writer.WriteString("query", query);
        writer.WriteString("searchRoot", searchRoot);

        writer.WriteStartObject("stats");
        writer.WriteString("startedUtc", stats.StartedUtc.ToString("o"));
        writer.WriteNumber("elapsedMs", (long)stats.Elapsed.TotalMilliseconds);
        writer.WriteNumber("filesScanned", stats.FilesScanned);
        writer.WriteNumber("bytesScanned", stats.BytesScanned);
        writer.WriteNumber("matchesFound", stats.MatchesFound);
        writer.WriteEndObject();

        writer.WriteNumber("resultCount", results.Count);

        progress?.Report(0.0);
        int total = results.Count;
        writer.WriteStartArray("results");
        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteResult(writer, results[i]);

            // Flush periodically so very large sessions don't buffer entirely in memory.
            if ((i + 1) % ResultBatchSize == 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (total > 0)
                    progress?.Report((double)(i + 1) / total);
            }
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
    }

    /// <summary>
    /// Streaming write overload that processes results group-by-group via a callback,
    /// so the caller can hydrate one group at a time and release its memory before
    /// proceeding to the next. Avoids holding all results in RAM simultaneously.
    /// </summary>
    /// <param name="prepareGroup">
    /// Called for each group index. The callback should hydrate/materialize the group's
    /// results and return the list to write. After writing, the caller may re-evict.
    /// </param>
    /// <param name="releaseGroup">
    /// Called after each group's results have been flushed to disk, so the caller
    /// can re-evict or release references.
    /// </param>
    public static async Task WriteStreamingAsync(
        Stream output,
        string query,
        string searchRoot,
        SessionStats stats,
        int totalResultCount,
        int groupCount,
        Func<int, IReadOnlyList<SearchResult>> prepareGroup,
        Action<int>? releaseGroup,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(searchRoot);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(prepareGroup);

        var jsonOpts = new JsonWriterOptions { Indented = false, SkipValidation = false };
        await using var writer = new Utf8JsonWriter(output, jsonOpts);

        writer.WriteStartObject();
        writer.WriteString("schema", SchemaVersion);
        writer.WriteString("savedUtc", DateTime.UtcNow.ToString("o"));
        writer.WriteString("query", query);
        writer.WriteString("searchRoot", searchRoot);

        writer.WriteStartObject("stats");
        writer.WriteString("startedUtc", stats.StartedUtc.ToString("o"));
        writer.WriteNumber("elapsedMs", (long)stats.Elapsed.TotalMilliseconds);
        writer.WriteNumber("filesScanned", stats.FilesScanned);
        writer.WriteNumber("bytesScanned", stats.BytesScanned);
        writer.WriteNumber("matchesFound", stats.MatchesFound);
        writer.WriteEndObject();

        writer.WriteNumber("resultCount", totalResultCount);

        progress?.Report(0.0);
        int written = 0;
        writer.WriteStartArray("results");

        for (int gi = 0; gi < groupCount; gi++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var results = prepareGroup(gi);
            for (int i = 0; i < results.Count; i++)
            {
                WriteResult(writer, results[i]);
                written++;

                if (written % ResultBatchSize == 0)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (totalResultCount > 0)
                        progress?.Report((double)written / totalResultCount);
                }
            }

            // Flush after each group so memory can be reclaimed immediately.
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            releaseGroup?.Invoke(gi);

            if (totalResultCount > 0)
                progress?.Report((double)written / totalResultCount);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
    }

    private static void WriteResult(Utf8JsonWriter writer, SearchResult r)
    {
        writer.WriteStartObject();
        writer.WriteString("f", r.FilePath);
        writer.WriteNumber("ln", r.LineNumber);
        writer.WriteString("ml", r.MatchLine ?? string.Empty);
        writer.WriteNumber("mc", r.MatchStartColumn);
        writer.WriteNumber("sc", r.SourceMatchStartColumn);
        writer.WriteNumber("mlen", r.MatchLength);

        WriteStringArray(writer, "b", r.ContextBefore);
        WriteStringArray(writer, "a", r.ContextAfter);

        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string>? lines)
    {
        writer.WriteStartArray(name);
        if (lines is not null)
        {
            for (int i = 0; i < lines.Count; i++)
                writer.WriteStringValue(lines[i] ?? string.Empty);
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Reads a session file, invoking <paramref name="onHeader"/> once with the
    /// metadata and <paramref name="onBatch"/> repeatedly with batches of
    /// rehydrated <see cref="SearchResult"/> instances. Streaming so very large
    /// sessions don't have to be fully materialised in memory before the UI
    /// starts displaying them.
    /// </summary>
    public static async Task<SessionHeader> ReadAsync(
        Stream input,
        Action<SessionHeader> onHeader,
        Func<IReadOnlyList<SearchResult>, Task> onBatch,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onHeader);
        ArgumentNullException.ThrowIfNull(onBatch);

        progress?.Report(0.0);

        // Parse phase reports 0..0.5 driven by bytes consumed; enumeration phase 0.5..1.0
        // driven by results parsed. Wrapping with a tracking stream lets us cover the
        // JsonDocument.ParseAsync buffer-fill, which dominates wall time for big files.
        long? totalBytes = input.CanSeek ? input.Length : null;
        Stream parseStream = totalBytes is long t && t > 0
            ? new ProgressStream(input, t, p => progress?.Report(p * 0.5))
            : input;

        using var doc = await JsonDocument.ParseAsync(parseStream, default, cancellationToken).ConfigureAwait(false);
        progress?.Report(0.5);
        var root = doc.RootElement;

        string schema = root.TryGetProperty("schema", out var s) ? (s.GetString() ?? string.Empty) : string.Empty;
        if (string.IsNullOrEmpty(schema) || !schema.StartsWith("yagu-session/", StringComparison.Ordinal))
            throw new InvalidDataException($"Not a Yagu session file (schema='{schema}').");

        // We only know how to read v1 today. Newer versions: fail loudly.
        if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported session schema '{schema}' (this build understands '{SchemaVersion}').");

        DateTime savedUtc = root.TryGetProperty("savedUtc", out var su) && DateTime.TryParse(su.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var savedParsed)
            ? savedParsed : DateTime.UtcNow;
        string query = root.TryGetProperty("query", out var q) ? (q.GetString() ?? string.Empty) : string.Empty;
        string searchRoot = root.TryGetProperty("searchRoot", out var sr) ? (sr.GetString() ?? string.Empty) : string.Empty;

        var stats = ReadStats(root);
        int resultCount = root.TryGetProperty("resultCount", out var rc) && rc.TryGetInt32(out var rcv) ? rcv : 0;

        var header = new SessionHeader(schema, savedUtc, query, searchRoot, stats, resultCount);
        onHeader(header);

        if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
        {
            progress?.Report(1.0);
            return header;
        }

        int reportEvery = Math.Max(1, resultCount / 100);
        int parsedCount = 0;
        var batch = new List<SearchResult>(ResultBatchSize);
        foreach (var item in resultsEl.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parsed = ParseResult(item);
            if (parsed is null) continue;

            batch.Add(parsed);
            parsedCount++;
            if (batch.Count >= ResultBatchSize)
            {
                await onBatch(batch).ConfigureAwait(false);
                batch = new List<SearchResult>(ResultBatchSize);
                if (resultCount > 0 && parsedCount % reportEvery < ResultBatchSize)
                    progress?.Report(0.5 + 0.5 * Math.Min(1.0, (double)parsedCount / resultCount));
            }
        }

        if (batch.Count > 0)
            await onBatch(batch).ConfigureAwait(false);

        progress?.Report(1.0);
        return header;
    }

    private static SessionStats ReadStats(JsonElement root)
    {
        if (!root.TryGetProperty("stats", out var statsEl) || statsEl.ValueKind != JsonValueKind.Object)
            return new SessionStats(DateTime.UtcNow, TimeSpan.Zero, 0, 0, 0);

        DateTime startedUtc = statsEl.TryGetProperty("startedUtc", out var su)
            && DateTime.TryParse(su.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var startedParsed)
            ? startedParsed : DateTime.UtcNow;

        long elapsedMs = statsEl.TryGetProperty("elapsedMs", out var em) && em.TryGetInt64(out var emv) ? emv : 0;
        int filesScanned = statsEl.TryGetProperty("filesScanned", out var fs) && fs.TryGetInt32(out var fsv) ? fsv : 0;
        long bytesScanned = statsEl.TryGetProperty("bytesScanned", out var bs) && bs.TryGetInt64(out var bsv) ? bsv : 0;
        int matchesFound = statsEl.TryGetProperty("matchesFound", out var mf) && mf.TryGetInt32(out var mfv) ? mfv : 0;

        return new SessionStats(startedUtc, TimeSpan.FromMilliseconds(elapsedMs), filesScanned, bytesScanned, matchesFound);
    }

    private static SearchResult? ParseResult(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string filePath = el.TryGetProperty("f", out var f) ? (f.GetString() ?? string.Empty) : string.Empty;
        if (string.IsNullOrEmpty(filePath)) return null;

        int lineNumber = el.TryGetProperty("ln", out var ln) && ln.TryGetInt32(out var lnv) ? lnv : 0;
        string matchLine = el.TryGetProperty("ml", out var ml) ? (ml.GetString() ?? string.Empty) : string.Empty;
        int matchStart = el.TryGetProperty("mc", out var mc) && mc.TryGetInt32(out var mcv) ? mcv : 0;
        int sourceMatchStart = el.TryGetProperty("sc", out var sc) && sc.TryGetInt32(out var scv) ? scv : matchStart;
        int matchLength = el.TryGetProperty("mlen", out var mlen) && mlen.TryGetInt32(out var mlenv) ? mlenv : 0;

        var contextBefore = ReadStringArray(el, "b");
        var contextAfter = ReadStringArray(el, "a");

        return new SearchResult(filePath, lineNumber, matchLine, matchStart, matchLength, contextBefore, contextAfter)
        { SourceMatchStartColumn = sourceMatchStart };
    }

    private static string[] ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        int len = arr.GetArrayLength();
        if (len == 0) return Array.Empty<string>();

        var items = new string[len];
        int i = 0;
        foreach (var v in arr.EnumerateArray())
            items[i++] = v.GetString() ?? string.Empty;
        return items;
    }

    /// <summary>
    /// Pass-through read stream that reports fractional progress (0..1) to a callback
    /// based on bytes consumed against a known total. Used to drive the parse-phase
    /// progress bar while <see cref="JsonDocument.ParseAsync"/> fills its buffer.
    /// </summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private readonly Action<double> _report;
        private long _read;
        private long _lastReportedTick;

        public ProgressStream(Stream inner, long total, Action<double> report)
        {
            _inner = inner;
            _total = total;
            _report = report;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            Advance(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Advance(n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Advance(n);
            return n;
        }

        private void Advance(int n)
        {
            if (n <= 0) return;
            _read += n;
            // Throttle to ~50 updates per second using Environment.TickCount.
            int tick = Environment.TickCount;
            if (tick - _lastReportedTick < 20 && _read < _total) return;
            _lastReportedTick = tick;
            _report(Math.Min(1.0, (double)_read / _total));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
