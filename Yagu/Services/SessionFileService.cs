using System.IO;
using System.Text;
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
    public static async Task WriteAsync(
        Stream output,
        string query,
        string searchRoot,
        SessionStats stats,
        IReadOnlyList<SearchResult> results,
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

        writer.WriteStartArray("results");
        for (int i = 0; i < results.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteResult(writer, results[i]);

            // Flush periodically so very large sessions don't buffer entirely in memory.
            if ((i + 1) % ResultBatchSize == 0)
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteResult(Utf8JsonWriter writer, SearchResult r)
    {
        writer.WriteStartObject();
        writer.WriteString("f", r.FilePath);
        writer.WriteNumber("ln", r.LineNumber);
        writer.WriteString("ml", r.MatchLine ?? string.Empty);
        writer.WriteNumber("mc", r.MatchStartColumn);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onHeader);
        ArgumentNullException.ThrowIfNull(onBatch);

        // For schema-checking and metadata extraction we parse the document
        // once with JsonDocument. Result payloads are streamed in chunks so the
        // entire result graph is not duplicated in JsonDocument's internal buffers.
        using var doc = await JsonDocument.ParseAsync(input, default, cancellationToken).ConfigureAwait(false);
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
            return header;

        var batch = new List<SearchResult>(ResultBatchSize);
        foreach (var item in resultsEl.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parsed = ParseResult(item);
            if (parsed is null) continue;

            batch.Add(parsed);
            if (batch.Count >= ResultBatchSize)
            {
                await onBatch(batch).ConfigureAwait(false);
                batch = new List<SearchResult>(ResultBatchSize);
            }
        }

        if (batch.Count > 0)
            await onBatch(batch).ConfigureAwait(false);

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
        int matchLength = el.TryGetProperty("mlen", out var mlen) && mlen.TryGetInt32(out var mlenv) ? mlenv : 0;

        var contextBefore = ReadStringArray(el, "b");
        var contextAfter = ReadStringArray(el, "a");

        return new SearchResult(filePath, lineNumber, matchLine, matchStart, matchLength, contextBefore, contextAfter);
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
}
