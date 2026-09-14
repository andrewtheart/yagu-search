using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Yagu.Models;

namespace Yagu.Services.Cli.MachineOutput;

/// <summary>
/// Writes the versioned CLI machine protocol: one JSON envelope per line (NDJSON) on stdout.
/// </summary>
/// <remarks>
/// <para>
/// Every record carries <c>schemaVersion</c>, <c>type</c>, <c>sequence</c> and the request correlation
/// id, so a consumer can validate compatibility and re-order/attribute records without guessing. The
/// record types are <c>match</c>, <c>result</c>, <c>plan</c>, <c>progress</c>, <c>warning</c>,
/// <c>summary</c>, <c>error</c> and <c>diagnostic</c>.
/// </para>
/// <para>
/// The writer owns the response budget: it stops emitting payload records once
/// <see cref="MachineOutputBudget.MaxRecords"/> or <see cref="MachineOutputBudget.MaxResponseBytes"/>
/// is reached, but control records (warning/error/summary/diagnostic) are always emitted so a client
/// can never be left unaware that the page was cut short.
/// </para>
/// </remarks>
internal sealed class MachineOutputWriter : IDisposable
{
    /// <summary>Bumped only for a breaking envelope change; consumers reject a version they don't know.</summary>
    public const int SchemaVersion = MachineProtocol.SchemaVersion;

    private static readonly JsonWriterOptions s_options = new()
    {
        Indented = false,
        // Escapes HTML-sensitive characters but leaves ordinary non-ASCII text unescaped, so a
        // byte budget measured on the source text stays a meaningful approximation of the wire size.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly Stream _stream;
    private readonly MemoryStream _scratch = new(64 * 1024);
    private readonly Utf8JsonWriter _writer;
    private readonly MachineOutputBudget _budget;
    private readonly string _requestId;

    /// <summary>
    /// The per-field ceilings actually in force. Starts at <see cref="_budget"/> and only ever shrinks,
    /// when a record that fits the configured limits is still refused by the response ceiling after
    /// JSON escaping. See <see cref="MachineOutputBudget.Shrink"/>.
    /// </summary>
    private MachineOutputBudget _effective;

    /// <summary>True once the shrink was reported, so it is explained exactly once per response.</summary>
    private bool _warnedFieldsShrunk;

    private long _sequence;
    private int _payloadRecords;
    private int _warningRecords;
    private int _suppressedWarnings;

    // Split so control records (progress, warning, diagnostic, error, summary) cannot starve the payload.
    private long _payloadBytesWritten;
    private long _controlBytesWritten;

    public MachineOutputWriter(Stream output, string requestId, MachineOutputBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _stream = output;
        _requestId = string.IsNullOrWhiteSpace(requestId) ? NewRequestId() : requestId;
        _budget = budget ?? MachineOutputBudget.Default;
        _effective = _budget;
        _writer = new Utf8JsonWriter(output, s_options);
    }

    /// <summary>A fresh correlation id for one CLI invocation.</summary>
    public static string NewRequestId() => Guid.NewGuid().ToString("n");

    public string RequestId => _requestId;

    /// <summary>Sequence number of the last emitted record (records are 1-based).</summary>
    public long Sequence => _sequence;

    /// <summary>Payload records emitted so far — the <c>returnedMatches</c> a summary reports.</summary>
    public int PayloadRecords => _payloadRecords;

    /// <summary>True once a payload record was suppressed because a budget was reached.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Warning records dropped because the warning ceiling was reached.</summary>
    public int SuppressedWarnings => _suppressedWarnings;

    /// <summary>Bytes emitted as payload records, which is what the response ceiling bounds.</summary>
    public long PayloadBytesWritten => _payloadBytesWritten;

    /// <summary>Bytes emitted as control records. Deliberately outside the payload ceiling.</summary>
    public long ControlBytesWritten => _controlBytesWritten;

    /// <summary>True when no further payload record can be emitted.</summary>
    public bool BudgetExhausted =>
        _payloadRecords >= _budget.MaxRecords || _payloadBytesWritten >= _budget.MaxResponseBytes;

    /// <summary>
    /// Records that the budget was already spent when a caller dropped a record of its own accord.
    /// Returns true when the page is full, so the caller can stop exactly as if it had tried to write.
    /// </summary>
    /// <remarks>
    /// A filled page is a COMPLETE bounded response, and the search self-cancels to stop scanning for
    /// results it can never return. Only <see cref="Truncated"/> distinguishes that self-cancel from a
    /// user cancellation, and <see cref="WriteMatch"/> is what sets it. A match a caller-side filter
    /// discards never reaches <c>WriteMatch</c>, so without this the same full page was reported as
    /// <c>cancelled</c> with <c>truncated:false</c>.
    /// </remarks>
    public bool NoteBudgetExhausted()
    {
        if (!BudgetExhausted)
            return false;

        Truncated = true;
        return true;
    }

    /// <summary>
    /// Emits a search match. Returns false when the budget stopped it, which is the caller's signal to
    /// stop pumping results and write the summary.
    /// </summary>
    public bool WriteMatch(SearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (BudgetExhausted)
        {
            Truncated = true;
            return false;
        }

        while (true)
        {
            if (TryComposeMatch(result))
                return true;

            // Once something has landed, a refusal is an honest end-of-page. Refusing the FIRST record
            // is not: the caller reports zero matches for a search that really did match, and no ceiling
            // the user can raise is to blame — JSON escaping inflated a record that fitted the limits.
            if (_payloadRecords > 0)
                return false;

            MachineOutputBudget? smaller = _effective.Shrink();
            if (smaller is null)
            {
                WriteWarning("record-too-large",
                    "a single match could not be encoded within the response limit even at the smallest "
                    + "field sizes. Raise Settings > MCP Server > Response Limits > maximum response size.");
                return false;
            }

            _effective = smaller;

            if (!_warnedFieldsShrunk)
            {
                _warnedFieldsShrunk = true;
                WriteWarning("fields-shrunk",
                    "match fields were shortened below the configured limits so results could fit the "
                    + "response size. Raise Settings > MCP Server > Response Limits to see more per match.");
            }
        }
    }

    /// <summary>Composes one match record at the per-field ceilings currently in force.</summary>
    private bool TryComposeMatch(SearchResult result)
    {

        return WriteRecord("match", w =>
        {
            var truncatedFields = new List<(string Field, int OriginalBytes)>();

            string path = MachineOutputBudget.TruncateUtf8(
                result.FilePath, _effective.MaxPathBytes, out bool pathTruncated, out int pathBytes);
            if (pathTruncated) truncatedFields.Add(("path", pathBytes));

            string text = MachineOutputBudget.TruncateUtf8(
                result.MatchLine, _effective.MaxLineBytes, out bool textTruncated, out int textBytes);
            if (textTruncated) truncatedFields.Add(("text", textBytes));

            w.WriteString("path", path);
            w.WriteNumber("line", result.LineNumber);
            w.WriteNumber("column", result.MatchStartColumn);
            w.WriteNumber("length", result.MatchLength);
            if (result.MatchEndLineNumber is int endLine)
            {
                w.WriteNumber("endLine", endLine);
                w.WriteNumber("endColumn", result.MatchEndColumn ?? 0);
            }
            // A truncated path is a SHORTENED path, and a consumer authorizes what it is given: a
            // cut path can name a directory that is contained when the real one is not. Flag it so the
            // consumer can fail closed rather than authorize a prefix.
            if (pathTruncated)
                w.WriteBoolean("pathTruncated", true);

            w.WriteString("text", text);

            // The search engine trims a long match line to LineTruncationLength BEFORE it reaches this
            // writer, marking the cut with an ellipsis. Without reporting that, a client cannot tell a
            // complete short line from a 10 MB line reduced to 500 characters — the byte budget below
            // never fires because the value already arrived small. The original length is genuinely
            // unknown here, so this is a flag rather than a size.
            if (WasTrimmedBySearchEngine(result.MatchLine))
                w.WriteBoolean("engineTruncatedText", true);

            WriteContext(w, "contextBefore", result.ContextBefore, truncatedFields);
            WriteContext(w, "contextAfter", result.ContextAfter, truncatedFields);

            if (truncatedFields.Count > 0)
            {
                w.WriteStartObject("truncated");
                foreach (var (field, originalBytes) in truncatedFields)
                    w.WriteNumber(field, originalBytes);
                w.WriteEndObject();
            }
        }, isPayload: true);
    }

    /// <summary>Emits a generic payload record (a read window, an index root, a session entry, …).</summary>
    public bool WriteResult(Action<Utf8JsonWriter> writeData)
    {
        ArgumentNullException.ThrowIfNull(writeData);

        if (BudgetExhausted)
        {
            Truncated = true;
            return false;
        }

        if (WriteRecord("result", writeData, isPayload: true))
            return true;

        // A caller-composed record cannot be shrunk from here, so the best that can be done is to make
        // the refusal audible: otherwise the response is an empty page with no explanation at all.
        if (_payloadRecords == 0)
        {
            WriteWarning("record-too-large",
                "the first result did not fit the response size limit, so nothing could be returned. "
                + "Raise Settings > MCP Server > Response Limits > maximum response size, or narrow the request.");
        }

        return false;
    }

    /// <summary>Emits the semantic translation plan. The search continues in the same invocation.</summary>
    public void WritePlan(Action<Utf8JsonWriter> writeData) => WriteRecord("plan", writeData, isPayload: false);

    /// <summary>Emits a scan-progress heartbeat. Never counted against the payload budget.</summary>
    public void WriteProgress(long filesScanned, long bytesScanned, long matches, double? percent)
        => WriteRecord("progress", w =>
        {
            w.WriteNumber("filesScanned", filesScanned);
            w.WriteNumber("bytesScanned", bytesScanned);
            w.WriteNumber("matches", matches);
            if (percent is double p)
                w.WriteNumber("percent", Math.Round(p, 2));
        }, isPayload: false);

    /// <summary>
    /// Emits a non-fatal condition (a fallback, a skipped root, memory pressure).
    /// </summary>
    /// <remarks>
    /// Capped at <see cref="MachineOutputBudget.MaxRecords"/>. A scan over a drive raises one of these per
    /// unreadable root, and an uncapped stream of them would dominate the response. The count of dropped
    /// warnings is reported in the summary so the consumer still knows they happened.
    /// </remarks>
    public void WriteWarning(string code, string message)
    {
        if (_warningRecords >= _budget.MaxRecords)
        {
            _suppressedWarnings++;
            return;
        }

        _warningRecords++;
        WriteRecord("warning", w =>
        {
            w.WriteString("code", code);
            w.WriteString("message", MachineOutputBudget.TruncateUtf8(message, _budget.MaxLineBytes, out _, out _));
        }, isPayload: false);
    }

    /// <summary>Emits an environment/configuration fact, e.g. which settings source was resolved.</summary>
    public void WriteDiagnostic(Action<Utf8JsonWriter> writeData) => WriteRecord("diagnostic", writeData, isPayload: false);

    /// <summary>Emits the terminal failure record. Always paired with a non-success exit code.</summary>
    public void WriteError(string code, string message, int exitCode)
        => WriteRecord("error", w =>
        {
            w.WriteString("code", code);
            w.WriteString("message", MachineOutputBudget.TruncateUtf8(message, _budget.MaxLineBytes, out _, out _));
            w.WriteNumber("exitCode", exitCode);
            w.WriteString("outcome", CliExitCode.Describe(exitCode));
        }, isPayload: false);

    /// <summary>
    /// Emits the closing summary. <c>totalKnown</c> is always false for a search: each invocation is a
    /// fresh bounded scan, so there is no stable global total to report.
    /// </summary>
    public void WriteSummary(int exitCode, Action<Utf8JsonWriter>? writeExtra = null)
        => WriteRecord("summary", w =>
        {
            w.WriteNumber("returnedRecords", _payloadRecords);
            w.WriteBoolean("truncated", Truncated);
            if (_suppressedWarnings > 0)
                w.WriteNumber("suppressedWarnings", _suppressedWarnings);
            w.WriteBoolean("totalKnown", false);
            w.WriteNumber("exitCode", exitCode);
            w.WriteString("outcome", CliExitCode.Describe(exitCode));
            writeExtra?.Invoke(w);
        }, isPayload: false);

    public void Flush()
    {
        _writer.Flush();
        _stream.Flush();
    }

    public void Dispose()
    {
        try { Flush(); } catch (IOException) { /* broken pipe: consumer closed early */ }
        _writer.Dispose();
        _scratch.Dispose();
    }

    /// <summary>
    /// True when the search engine already trimmed this line, which it signals with a leading and/or
    /// trailing <see cref="Helpers.LineTruncator.Ellipsis"/>.
    /// </summary>
    internal static bool WasTrimmedBySearchEngine(string? matchLine)
    {
        if (string.IsNullOrEmpty(matchLine))
            return false;

        string ellipsis = Helpers.LineTruncator.Ellipsis;
        return matchLine.StartsWith(ellipsis, StringComparison.Ordinal)
            || matchLine.EndsWith(ellipsis, StringComparison.Ordinal);
    }

    private void WriteContext(
        Utf8JsonWriter w,
        string name,
        IReadOnlyList<string> lines,
        List<(string Field, int OriginalBytes)> truncatedFields)
    {
        if (lines.Count == 0)
            return;

        int emit = Math.Min(lines.Count, _effective.MaxContextLines);
        w.WriteStartArray(name);
        for (int i = 0; i < emit; i++)
        {
            string line = MachineOutputBudget.TruncateUtf8(
                lines[i], _effective.MaxContextLineBytes, out bool lineTruncated, out int lineBytes);
            if (lineTruncated) truncatedFields.Add(($"{name}[{i}]", lineBytes));
            w.WriteStringValue(line);
        }
        w.WriteEndArray();

        if (emit < lines.Count)
            truncatedFields.Add(($"{name}.count", lines.Count));
    }

    /// <summary>
    /// Emits one record. Returns false only when a payload record was refused by the byte ceiling.
    /// </summary>
    /// <remarks>
    /// A payload record is serialized into a scratch buffer FIRST and only committed if it fits the
    /// remaining budget. Checking the running total before serializing — the obvious order — bounds
    /// nothing: one match carrying maximum-size context lines can exceed the whole configured ceiling on
    /// its own, and by then it has already been written. Control records bypass the ceiling on purpose,
    /// so a client is never left without the error or summary explaining the cut — and their bytes are
    /// tracked separately, because charging them to the payload ceiling let a few hundred access-denied
    /// warnings consume the whole response and return zero matches.
    /// </remarks>
    private bool WriteRecord(string type, Action<Utf8JsonWriter>? writeData, bool isPayload)
    {
        long nextSequence = _sequence + 1;

        if (!isPayload)
        {
            _writer.Reset(_stream);
            Compose(type, nextSequence, writeData);
            _stream.WriteByte((byte)'\n');
            _controlBytesWritten += _writer.BytesCommitted + 1;
            _sequence = nextSequence;
            return true;
        }

        _scratch.SetLength(0);
        _writer.Reset(_scratch);
        Compose(type, nextSequence, writeData);

        long recordBytes = _writer.BytesCommitted + 1;
        if (_payloadBytesWritten + recordBytes > _budget.MaxResponseBytes)
        {
            Truncated = true;
            return false;
        }

        _stream.Write(_scratch.GetBuffer(), 0, (int)_scratch.Length);
        _stream.WriteByte((byte)'\n');
        _payloadBytesWritten += recordBytes;
        _sequence = nextSequence;
        _payloadRecords++;
        return true;
    }

    private void Compose(string type, long sequence, Action<Utf8JsonWriter>? writeData)
    {
        _writer.WriteStartObject();
        _writer.WriteNumber("schemaVersion", SchemaVersion);
        _writer.WriteString("type", type);
        _writer.WriteNumber("sequence", sequence);
        _writer.WriteString("requestId", _requestId);
        if (writeData is not null)
        {
            _writer.WriteStartObject("data");
            writeData(_writer);
            _writer.WriteEndObject();
        }
        _writer.WriteEndObject();
        _writer.Flush();
    }
}
