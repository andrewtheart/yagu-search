using System.Buffers;
using System.Text.Json;
using Yagu.Models;
using System.Globalization;

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

    /// <summary>
    /// Current schema version written by this build. v2 (Phase 1b) adds optional per-result
    /// cross-line span fields (<c>mel</c>/<c>mec</c>); a v1 file (no span fields) loads as
    /// single-line, and a v2 file with no multiline results is byte-compatible with v1 apart
    /// from this version string. Readers accept every version in <see cref="ReadableSchemaVersions"/>.
    /// </summary>
    public const string SchemaVersion = "yagu-session/v2";

    /// <summary>Schema versions this build knows how to read. Newer/unknown versions are refused.</summary>
    private static readonly string[] ReadableSchemaVersions = ["yagu-session/v1", "yagu-session/v2"];

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

        // Phase 1b: persist the cross-line span only for multiline matches, so single-line
        // sessions stay compact and byte-compatible with v1 apart from the schema string.
        if (r.MatchEndLineNumber is int matchEndLine)
        {
            writer.WriteNumber("mel", matchEndLine);
            writer.WriteNumber("mec", r.MatchEndColumn ?? 0);
        }

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
    /// rehydrated <see cref="SearchResult"/> instances.
    /// </summary>
    /// <remarks>
    /// Streams the document with a <see cref="Utf8JsonReader"/> over a refillable
    /// <see cref="ArrayPool{T}"/> buffer rather than <c>JsonDocument.ParseAsync</c>.
    /// <c>JsonDocument</c> materialises the entire file into a single contiguous
    /// allocation sized by an <see cref="int"/>: a session larger than 2 GB overflows
    /// that sizing (a <see cref="long"/> length wraps to a negative <see cref="int"/>)
    /// and throws <see cref="ArgumentOutOfRangeException"/> from <c>ArrayPool.Rent</c>.
    /// Streaming keeps memory bounded and removes the 2 GB ceiling entirely.
    /// </remarks>
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

        // Byte-based progress for seekable inputs (covers the whole file); non-seekable
        // streams fall back to parsed-vs-declared result count.
        long? totalBytes = input.CanSeek ? input.Length : (long?)null;

        var parser = new SessionStreamParser();
        var readerState = new JsonReaderState();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int bufferedBytes = 0;
            bool isFinalBlock = false;
            long totalConsumed = 0;
            double lastReported = 0.0;
            bool headerDelivered = false;
            SessionHeader? header = null;

            while (!isFinalBlock)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // If the buffer filled without the reader consuming a full token (a single
                // value larger than the buffer, e.g. a multi-MB match line), grow it.
                if (bufferedBytes == buffer.Length)
                {
                    byte[] grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, grown, 0, bufferedBytes);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                int read = await input.ReadAsync(buffer.AsMemory(bufferedBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    isFinalBlock = true;
                else
                    bufferedBytes += read;

                // Consume every complete token now in the buffer. Utf8JsonReader is a ref
                // struct, so this runs synchronously (no await inside the token loop).
                int consumed = PumpTokens(buffer.AsSpan(0, bufferedBytes), isFinalBlock, ref readerState, parser);
                totalConsumed += consumed;

                // Slide any unconsumed tail (a token straddling the buffer edge) to the front.
                int leftover = bufferedBytes - consumed;
                if (leftover > 0 && consumed > 0)
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
                bufferedBytes = leftover;

                if (!headerDelivered && parser.HeaderReady)
                {
                    header = parser.BuildHeader();
                    onHeader(header);
                    headerDelivered = true;
                }

                // Hand fully-parsed results to the caller so memory stays bounded.
                if (parser.Completed.Count >= ResultBatchSize)
                    await onBatch(parser.TakeAllCompleted()).ConfigureAwait(false);

                double frac =
                    totalBytes is long tb && tb > 0 ? Math.Min(1.0, (double)totalConsumed / tb)
                    : parser.ResultCount > 0 ? Math.Min(1.0, (double)parser.ParsedCount / parser.ResultCount)
                    : 0.0;
                if (frac < 1.0 && frac > lastReported + 0.005)
                {
                    lastReported = frac;
                    progress?.Report(frac);
                }
            }

            // Deliver the header even for header-only / empty / truncated sessions.
            if (!headerDelivered)
            {
                header = parser.BuildHeader();
                onHeader(header);
                headerDelivered = true;
            }

            // Flush any trailing partial batch.
            if (parser.Completed.Count > 0)
                await onBatch(parser.TakeAllCompleted()).ConfigureAwait(false);

            // Catches empty / schema-less files (a wrong or unsupported schema already
            // threw as soon as the schema value was read).
            parser.ThrowIfSchemaInvalid();

            progress?.Report(1.0);
            return header!;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int PumpTokens(ReadOnlySpan<byte> data, bool isFinalBlock, ref JsonReaderState state, SessionStreamParser parser)
    {
        var reader = new Utf8JsonReader(data, isFinalBlock, state);
        while (reader.Read())
            parser.Consume(ref reader);
        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    /// <summary>
    /// Incremental, allocation-light state machine that turns the token stream from a
    /// <see cref="Utf8JsonReader"/> into <see cref="SearchResult"/> instances without
    /// buffering the whole document. Fed one buffer at a time by <see cref="ReadAsync"/>,
    /// so it works on files of any size (removes the 2 GB <c>JsonDocument</c> ceiling).
    /// </summary>
    private sealed class SessionStreamParser
    {
        private enum Scope { None, Root, Stats, Results, Result, StringArray, Skip }
        private enum HeaderField { Unknown, Schema, SavedUtc, Query, SearchRoot, Stats, ResultCount, Results }
        private enum StatsField { Unknown, StartedUtc, ElapsedMs, FilesScanned, BytesScanned, MatchesFound }
        private enum ResultField { Unknown, F, Ln, Ml, Mc, Sc, Mlen, Mel, Mec, Before, After }

        private readonly Stack<Scope> _stack = new();
        private Scope _scope = Scope.None;

        private HeaderField _headerField;
        private StatsField _statsField;
        private ResultField _resultField;

        // Header + stats accumulators.
        private string _schema = string.Empty;
        private DateTime _savedUtc = DateTime.UtcNow;
        private string _query = string.Empty;
        private string _searchRoot = string.Empty;
        private int _resultCount;
        private DateTime _statsStarted = DateTime.UtcNow;
        private long _statsElapsedMs;
        private int _statsFiles;
        private long _statsBytes;
        private int _statsMatches;
        private bool _schemaChecked;

        // Current result being assembled.
        private string _rf = string.Empty;
        private int _rln, _rmc, _rmlen, _rsc;
        private bool _rscSet;
        private string _rml = string.Empty;
        private int? _rmel;
        private int _rmec;
        private List<string>? _rbefore;
        private List<string>? _rafter;
        private List<string>? _curArray;

        private List<SearchResult> _completed = new(ResultBatchSize * 2);

        public bool HeaderReady { get; private set; }
        public int ResultCount => _resultCount;
        public int ParsedCount { get; private set; }
        public List<SearchResult> Completed => _completed;

        /// <summary>Hands off the buffered results and installs a fresh list (O(1)).</summary>
        public List<SearchResult> TakeAllCompleted()
        {
            var taken = _completed;
            _completed = new List<SearchResult>(ResultBatchSize * 2);
            return taken;
        }

        public SessionHeader BuildHeader()
        {
            var stats = new SessionStats(
                _statsStarted, TimeSpan.FromMilliseconds(_statsElapsedMs),
                _statsFiles, _statsBytes, _statsMatches);
            return new SessionHeader(_schema, _savedUtc, _query, _searchRoot, stats, _resultCount);
        }

        /// <summary>
        /// Validates the schema string. Called both eagerly (as soon as the schema value is read,
        /// so a wrong/unsupported file fails fast) and at end-of-stream (to catch empty/schema-less
        /// files). Idempotent via <see cref="_schemaChecked"/>.
        /// </summary>
        public void ThrowIfSchemaInvalid()
        {
            if (_schemaChecked) return;
            if (string.IsNullOrEmpty(_schema) || !_schema.StartsWith("yagu-session/", StringComparison.Ordinal))
                throw new InvalidDataException($"Not a Yagu session file (schema='{_schema}').");
            if (Array.IndexOf(ReadableSchemaVersions, _schema) < 0)
                throw new InvalidDataException($"Unsupported session schema '{_schema}' (this build understands {string.Join(", ", ReadableSchemaVersions)}).");
            _schemaChecked = true;
        }

        public void Consume(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: OnStartObject(); break;
                case JsonTokenType.EndObject: OnEndObject(); break;
                case JsonTokenType.StartArray: OnStartArray(); break;
                case JsonTokenType.EndArray: OnEndArray(); break;
                case JsonTokenType.PropertyName: OnPropertyName(ref reader); break;
                default: OnValue(ref reader); break;
            }
        }

        private void OnStartObject()
        {
            _stack.Push(_scope);
            if (_scope == Scope.None) _scope = Scope.Root;
            else if (_scope == Scope.Root && _headerField == HeaderField.Stats) _scope = Scope.Stats;
            else if (_scope == Scope.Results) { _scope = Scope.Result; BeginResult(); }
            else _scope = Scope.Skip;
        }

        private void OnEndObject()
        {
            if (_scope == Scope.Result) EndResult();
            else if (_scope == Scope.Root) HeaderReady = true; // root closed (covers a missing "results" array)
            _scope = _stack.Count > 0 ? _stack.Pop() : Scope.None;
        }

        private void OnStartArray()
        {
            _stack.Push(_scope);
            if (_scope == Scope.Root && _headerField == HeaderField.Results)
            {
                _scope = Scope.Results;
                HeaderReady = true; // every header field is written before "results"
            }
            else if (_scope == Scope.Result && _resultField is ResultField.Before or ResultField.After)
            {
                _scope = Scope.StringArray;
                _curArray = _resultField == ResultField.Before ? (_rbefore ??= new()) : (_rafter ??= new());
            }
            else _scope = Scope.Skip;
        }

        private void OnEndArray()
        {
            if (_scope == Scope.StringArray) _curArray = null;
            _scope = _stack.Count > 0 ? _stack.Pop() : Scope.None;
        }

        private void OnPropertyName(ref Utf8JsonReader reader)
        {
            switch (_scope)
            {
                case Scope.Root:
                    _headerField =
                        reader.ValueTextEquals("schema"u8) ? HeaderField.Schema :
                        reader.ValueTextEquals("savedUtc"u8) ? HeaderField.SavedUtc :
                        reader.ValueTextEquals("query"u8) ? HeaderField.Query :
                        reader.ValueTextEquals("searchRoot"u8) ? HeaderField.SearchRoot :
                        reader.ValueTextEquals("stats"u8) ? HeaderField.Stats :
                        reader.ValueTextEquals("resultCount"u8) ? HeaderField.ResultCount :
                        reader.ValueTextEquals("results"u8) ? HeaderField.Results :
                        HeaderField.Unknown;
                    break;
                case Scope.Stats:
                    _statsField =
                        reader.ValueTextEquals("startedUtc"u8) ? StatsField.StartedUtc :
                        reader.ValueTextEquals("elapsedMs"u8) ? StatsField.ElapsedMs :
                        reader.ValueTextEquals("filesScanned"u8) ? StatsField.FilesScanned :
                        reader.ValueTextEquals("bytesScanned"u8) ? StatsField.BytesScanned :
                        reader.ValueTextEquals("matchesFound"u8) ? StatsField.MatchesFound :
                        StatsField.Unknown;
                    break;
                case Scope.Result:
                    _resultField =
                        reader.ValueTextEquals("f"u8) ? ResultField.F :
                        reader.ValueTextEquals("ln"u8) ? ResultField.Ln :
                        reader.ValueTextEquals("ml"u8) ? ResultField.Ml :
                        reader.ValueTextEquals("mc"u8) ? ResultField.Mc :
                        reader.ValueTextEquals("sc"u8) ? ResultField.Sc :
                        reader.ValueTextEquals("mlen"u8) ? ResultField.Mlen :
                        reader.ValueTextEquals("mel"u8) ? ResultField.Mel :
                        reader.ValueTextEquals("mec"u8) ? ResultField.Mec :
                        reader.ValueTextEquals("b"u8) ? ResultField.Before :
                        reader.ValueTextEquals("a"u8) ? ResultField.After :
                        ResultField.Unknown;
                    break;
            }
        }

        private void OnValue(ref Utf8JsonReader reader)
        {
            switch (_scope)
            {
                case Scope.Root: ApplyHeaderValue(ref reader); break;
                case Scope.Stats: ApplyStatsValue(ref reader); break;
                case Scope.Result: ApplyResultValue(ref reader); break;
                case Scope.StringArray:
                    if (reader.TokenType == JsonTokenType.String)
                        _curArray?.Add(reader.GetString() ?? string.Empty);
                    else if (reader.TokenType == JsonTokenType.Null)
                        _curArray?.Add(string.Empty);
                    break;
            }
        }

        private void ApplyHeaderValue(ref Utf8JsonReader reader)
        {
            switch (_headerField)
            {
                case HeaderField.Schema:
                    _schema = reader.GetString() ?? string.Empty;
                    ThrowIfSchemaInvalid(); // fail fast on a wrong / unsupported schema
                    break;
                case HeaderField.SavedUtc: _savedUtc = ParseDate(ref reader, DateTime.UtcNow); break;
                case HeaderField.Query: _query = reader.GetString() ?? string.Empty; break;
                case HeaderField.SearchRoot: _searchRoot = reader.GetString() ?? string.Empty; break;
                case HeaderField.ResultCount: _resultCount = ReadIntOrZero(ref reader); break;
            }
        }

        private void ApplyStatsValue(ref Utf8JsonReader reader)
        {
            switch (_statsField)
            {
                case StatsField.StartedUtc: _statsStarted = ParseDate(ref reader, DateTime.UtcNow); break;
                case StatsField.ElapsedMs: _statsElapsedMs = ReadLongOrZero(ref reader); break;
                case StatsField.FilesScanned: _statsFiles = ReadIntOrZero(ref reader); break;
                case StatsField.BytesScanned: _statsBytes = ReadLongOrZero(ref reader); break;
                case StatsField.MatchesFound: _statsMatches = ReadIntOrZero(ref reader); break;
            }
        }

        private void ApplyResultValue(ref Utf8JsonReader reader)
        {
            switch (_resultField)
            {
                case ResultField.F: _rf = reader.GetString() ?? string.Empty; break;
                case ResultField.Ml: _rml = reader.GetString() ?? string.Empty; break;
                case ResultField.Ln: _rln = ReadIntOrZero(ref reader); break;
                case ResultField.Mc: _rmc = ReadIntOrZero(ref reader); break;
                case ResultField.Sc: _rscSet = TryReadInt(ref reader, out _rsc); break;
                case ResultField.Mlen: _rmlen = ReadIntOrZero(ref reader); break;
                case ResultField.Mel: if (TryReadInt(ref reader, out int mel)) _rmel = mel; break;
                case ResultField.Mec: _rmec = ReadIntOrZero(ref reader); break;
            }
        }

        private void BeginResult()
        {
            _rf = string.Empty;
            _rml = string.Empty;
            _rln = _rmc = _rmlen = _rsc = 0;
            _rscSet = false;
            _rmel = null;
            _rmec = 0;
            _rbefore = null;
            _rafter = null;
            _resultField = ResultField.Unknown;
        }

        private void EndResult()
        {
            // A result with no file path is skipped (parity with the old ParseResult).
            if (!string.IsNullOrEmpty(_rf))
            {
                string[] before = _rbefore is { Count: > 0 } ? _rbefore.ToArray() : Array.Empty<string>();
                string[] after = _rafter is { Count: > 0 } ? _rafter.ToArray() : Array.Empty<string>();
                var result = new SearchResult(_rf, _rln, _rml, _rmc, _rmlen, before, after)
                {
                    // The writer always pairs mel+mec; absent mel (v1 or single-line v2) => single-line.
                    SourceMatchStartColumn = _rscSet ? _rsc : _rmc,
                    MatchEndLineNumber = _rmel,
                    MatchEndColumn = _rmel is null ? null : _rmec,
                };
                _completed.Add(result);
                ParsedCount++;
            }
        }

        private static DateTime ParseDate(ref Utf8JsonReader reader, DateTime fallback)
        {
            if (reader.TokenType == JsonTokenType.String
                && DateTime.TryParse(reader.GetString(), null, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return fallback;
        }

        // Number-token readers shared by every integer field. A missing or wrong-typed value
        // (a corrupt / hand-edited session) yields the fallback instead of throwing.
        private static int ReadIntOrZero(ref Utf8JsonReader reader)
            => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int v) ? v : 0;

        private static long ReadLongOrZero(ref Utf8JsonReader reader)
            => reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long v) ? v : 0;

        private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out value))
                return true;
            value = 0;
            return false;
        }
    }
}
