using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Options controlling what is included in an exported report.
/// </summary>
public sealed class ReportExportOptions
{
    public ReportFormat Format { get; set; } = ReportFormat.Html;
    public bool IncludeFileSizes { get; set; }
    public bool IncludeModifiedDates { get; set; }
    public bool IncludeContextLines { get; set; } = true;
    public int ContextLineCount { get; set; } = 3;
    public bool IncludeMatchMarkers { get; set; } = true;
    /// <summary>When true, CSV uses embedded newlines for context (RFC 4180). When false, context is omitted from CSV.</summary>
    public bool CsvEmbedContext { get; set; }
    /// <summary>When true, uses pipe (|) as line separator instead of embedded newlines for context and match lines in CSV.</summary>
    public bool CsvUsePipeSeparator { get; set; }
}

public enum ReportFormat
{
    Html,
    Json,
    Csv
}

/// <summary>
/// Generates JSON and CSV search reports. Pure I/O — no UI dependencies.
/// </summary>
public static class ReportExportService
{
    // ───────────────────────────────────────────────────────────────
    // JSON Export
    // ───────────────────────────────────────────────────────────────

    public static async Task WriteJsonReportAsync(
        TextWriter writer,
        string query,
        IReadOnlyList<HtmlReportExportService.FileMatchGroup> groups,
        HtmlReportExportService.SearchStats stats,
        ReportExportOptions options)
    {
        using var jsonWriter = new Utf8JsonWriter(
            new WriterStream(writer, Encoding.UTF8),
            new JsonWriterOptions { Indented = true });

        jsonWriter.WriteStartObject();

        jsonWriter.WriteString("query", query);

        // Stats
        jsonWriter.WriteStartObject("stats");
        jsonWriter.WriteString("started", stats.StartedUtc.ToString("o"));
        jsonWriter.WriteString("elapsed", stats.Elapsed.ToString());
        jsonWriter.WriteNumber("filesScanned", stats.FilesScanned);
        jsonWriter.WriteNumber("bytesScanned", stats.BytesScanned);
        jsonWriter.WriteEndObject();

        // Results
        jsonWriter.WriteStartArray("results");

        foreach (var group in groups)
        {
            var sourceContext = SourceFileContext.TryLoad(group.FilePath);
            long fileSize = 0;
            DateTime? modifiedDate = null;
            if (options.IncludeFileSizes || options.IncludeModifiedDates)
            {
                try
                {
                    var fi = new FileInfo(group.FilePath);
                    if (fi.Exists)
                    {
                        fileSize = fi.Length;
                        modifiedDate = fi.LastWriteTimeUtc;
                    }
                }
                catch { /* best effort */ }
            }

            foreach (var result in group.Results)
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("filePath", group.FilePath);
                jsonWriter.WriteString("fileName", group.FileName);
                if (options.IncludeFileSizes)
                    jsonWriter.WriteNumber("fileSize", fileSize);
                if (options.IncludeModifiedDates && modifiedDate.HasValue)
                    jsonWriter.WriteString("modifiedDate", modifiedDate.Value.ToString("o"));
                jsonWriter.WriteNumber("lineNumber", result.LineNumber);

                string matchLine = options.IncludeMatchMarkers
                    ? InsertMatchMarkers(result.MatchLine, result.MatchStartColumn, result.MatchLength)
                    : result.MatchLine;
                jsonWriter.WriteString("matchLine", matchLine);
                jsonWriter.WriteNumber("matchStart", result.MatchStartColumn);
                jsonWriter.WriteNumber("matchLength", result.MatchLength);

                if (options.IncludeContextLines && options.ContextLineCount > 0)
                {
                    var ctxBefore = ResolveContextBefore(result, options.ContextLineCount, sourceContext);
                    var ctxAfter = ResolveContextAfter(result, options.ContextLineCount, sourceContext);

                    jsonWriter.WriteStartArray("contextBefore");
                    foreach (var line in ctxBefore)
                        jsonWriter.WriteStringValue(line);
                    jsonWriter.WriteEndArray();

                    jsonWriter.WriteStartArray("contextAfter");
                    foreach (var line in ctxAfter)
                        jsonWriter.WriteStringValue(line);
                    jsonWriter.WriteEndArray();
                }

                jsonWriter.WriteEndObject();
            }
        }

        jsonWriter.WriteEndArray();
        jsonWriter.WriteEndObject();

        await jsonWriter.FlushAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    // ───────────────────────────────────────────────────────────────
    // CSV Export
    // ───────────────────────────────────────────────────────────────

    public static async Task WriteCsvReportAsync(
        TextWriter writer,
        string query,
        IReadOnlyList<HtmlReportExportService.FileMatchGroup> groups,
        ReportExportOptions options)
    {
        bool includeContext = options.IncludeContextLines && (options.CsvEmbedContext || options.CsvUsePipeSeparator) && options.ContextLineCount > 0;

        // Header row
        var headers = new List<string> { "FilePath", "FileName" };
        if (options.IncludeFileSizes) headers.Add("FileSize");
        if (options.IncludeModifiedDates) headers.Add("ModifiedDate");
        headers.Add("LineNumber");
        headers.Add("MatchLine");
        headers.Add("MatchStart");
        headers.Add("MatchLength");
        if (includeContext)
        {
            headers.Add("ContextBefore");
            headers.Add("ContextAfter");
        }

        await writer.WriteLineAsync(string.Join(',', headers)).ConfigureAwait(false);

        foreach (var group in groups)
        {
            var sourceContext = includeContext ? SourceFileContext.TryLoad(group.FilePath) : null;
            long fileSize = 0;
            DateTime? modifiedDate = null;
            if (options.IncludeFileSizes || options.IncludeModifiedDates)
            {
                try
                {
                    var fi = new FileInfo(group.FilePath);
                    if (fi.Exists)
                    {
                        fileSize = fi.Length;
                        modifiedDate = fi.LastWriteTimeUtc;
                    }
                }
                catch { /* best effort */ }
            }

            foreach (var result in group.Results)
            {
                var fields = new List<string>();
                fields.Add(CsvEscape(group.FilePath));
                fields.Add(CsvEscape(group.FileName));
                if (options.IncludeFileSizes)
                    fields.Add(fileSize.ToString(CultureInfo.InvariantCulture));
                if (options.IncludeModifiedDates)
                    fields.Add(modifiedDate.HasValue ? CsvEscape(modifiedDate.Value.ToString("o")) : "");
                fields.Add(result.LineNumber.ToString(CultureInfo.InvariantCulture));

                string matchLine = options.IncludeMatchMarkers
                    ? InsertMatchMarkers(result.MatchLine, result.MatchStartColumn, result.MatchLength)
                    : result.MatchLine;
                fields.Add(CsvEscape(matchLine));
                fields.Add(result.MatchStartColumn.ToString(CultureInfo.InvariantCulture));
                fields.Add(result.MatchLength.ToString(CultureInfo.InvariantCulture));

                if (includeContext)
                {
                    string lineSep = options.CsvUsePipeSeparator ? " | " : "\n";
                    var ctxBefore = ResolveContextBefore(result, options.ContextLineCount, sourceContext);
                    var ctxAfter = ResolveContextAfter(result, options.ContextLineCount, sourceContext);
                    fields.Add(CsvEscape(string.Join(lineSep, ctxBefore)));
                    fields.Add(CsvEscape(string.Join(lineSep, ctxAfter)));
                }

                await writer.WriteLineAsync(string.Join(',', fields)).ConfigureAwait(false);
            }
        }
    }

    // ───────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────

    /// <summary>Inserts &lt;match&gt;...&lt;/match&gt; markers around the matched text.</summary>
    public static string InsertMatchMarkers(string line, int matchStart, int matchLength)
    {
        if (matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
            return line;
        int safeLen = Math.Min(matchLength, line.Length - matchStart);
        return $"{line[..matchStart]}<match>{line.Substring(matchStart, safeLen)}</match>{line[(matchStart + safeLen)..]}";
    }

    private static IReadOnlyList<string> ResolveContextBefore(SearchResult result, int maxLines, SourceFileContext? sourceContext)
    {
        var captured = TrimContextBefore(result.ContextBefore, maxLines);
        if (captured.Count >= maxLines || sourceContext is null)
            return captured;

        var fromSource = sourceContext.GetBefore(result.LineNumber, maxLines);
        return fromSource.Count > captured.Count ? fromSource : captured;
    }

    private static IReadOnlyList<string> ResolveContextAfter(SearchResult result, int maxLines, SourceFileContext? sourceContext)
    {
        var captured = TrimContextAfter(result.ContextAfter, maxLines);
        if (captured.Count >= maxLines || sourceContext is null)
            return captured;

        var fromSource = sourceContext.GetAfter(result.LineNumber, maxLines);
        return fromSource.Count > captured.Count ? fromSource : captured;
    }

    private static IReadOnlyList<string> TrimContextBefore(IReadOnlyList<string> context, int maxLines)
    {
        if (context.Count <= maxLines) return context;

        var trimmed = new List<string>(maxLines);
        int start = context.Count - maxLines;
        for (int i = start; i < context.Count; i++)
            trimmed.Add(context[i]);
        return trimmed;
    }

    private static IReadOnlyList<string> TrimContextAfter(IReadOnlyList<string> context, int maxLines)
    {
        if (context.Count <= maxLines) return context;

        var trimmed = new List<string>(maxLines);
        for (int i = 0; i < maxLines && i < context.Count; i++)
            trimmed.Add(context[i]);
        return trimmed;
    }

    private sealed class SourceFileContext
    {
        private readonly string[] _lines;

        private SourceFileContext(string[] lines) => _lines = lines;

        public static SourceFileContext? TryLoad(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    return null;

                return new SourceFileContext(File.ReadAllLines(filePath));
            }
            catch
            {
                return null;
            }
        }

        public IReadOnlyList<string> GetBefore(int lineNumber, int maxLines)
        {
            int matchIndex = lineNumber - 1;
            if (maxLines <= 0 || matchIndex <= 0 || matchIndex > _lines.Length)
                return Array.Empty<string>();

            int start = Math.Max(0, matchIndex - maxLines);
            var context = new List<string>(matchIndex - start);
            for (int i = start; i < matchIndex; i++)
                context.Add(LineTruncator.Truncate(_lines[i]));
            return context;
        }

        public IReadOnlyList<string> GetAfter(int lineNumber, int maxLines)
        {
            int matchIndex = lineNumber - 1;
            if (maxLines <= 0 || matchIndex < 0 || matchIndex >= _lines.Length - 1)
                return Array.Empty<string>();

            int end = Math.Min(_lines.Length - 1, matchIndex + maxLines);
            var context = new List<string>(end - matchIndex);
            for (int i = matchIndex + 1; i <= end; i++)
                context.Add(LineTruncator.Truncate(_lines[i]));
            return context;
        }
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        // RFC 4180: if the field contains comma, quote, or newline, wrap in quotes and double any existing quotes
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    /// <summary>Adapter stream that writes to a TextWriter — used to bridge Utf8JsonWriter to TextWriter.</summary>
    private sealed class WriterStream : Stream
    {
        private readonly TextWriter _writer;
        private readonly Encoding _encoding;

        public WriterStream(TextWriter writer, Encoding encoding)
        {
            _writer = writer;
            _encoding = encoding;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var chars = _encoding.GetString(buffer, offset, count);
            _writer.Write(chars);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var chars = _encoding.GetString(buffer, offset, count);
            return _writer.WriteAsync(chars);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var chars = _encoding.GetString(buffer.Span);
            return new ValueTask(_writer.WriteAsync(chars));
        }

        public override void Flush() => _writer.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
