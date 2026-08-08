using System.ComponentModel;
using System.Text;

namespace Yagu.Services;

/// <summary>One parsed line from Yagu's structured text log.</summary>
public sealed class LogTailEntry : INotifyPropertyChanged
{
    internal LogTailEntry(DateTimeOffset timestamp, LogLevel level, string severity, string category, string message, string rawText)
    {
        Timestamp = timestamp;
        Level = level;
        Severity = severity;
        Category = category;
        Message = message;
        RawText = rawText;
    }

    public DateTimeOffset Timestamp { get; }
    public LogLevel Level { get; }
    public string Severity { get; }
    public string Category { get; }
    public string Message { get; private set; }
    public string RawText { get; private set; }
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void AppendContinuation(string line)
    {
        Message += Environment.NewLine + line;
        RawText += Environment.NewLine + line;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawText)));
    }
}

public readonly record struct LogTailReadBatch(bool Reset, IReadOnlyList<LogTailEntry> Entries);

/// <summary>
/// Incrementally reads Yagu's active text log without blocking its writer. No file handle survives a
/// call: every read uses read/write/delete sharing, consumes only appended bytes, and retains an
/// incomplete trailing line until the next pass.
/// </summary>
public sealed class LogTailReader
{
    private readonly string _logPath;
    private long _position;
    private byte[] _pendingBytes = [];
    private LogTailEntry? _lastEntry;

    public LogTailReader(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        _logPath = Path.GetFullPath(logPath);
    }

    public string LogPath => _logPath;

    public LogTailReadBatch ReadNew()
    {
        if (!File.Exists(_logPath))
        {
            bool resetMissing = _position != 0 || _pendingBytes.Length != 0 || _lastEntry is not null;
            if (resetMissing)
                ResetState();
            return new LogTailReadBatch(resetMissing, Array.Empty<LogTailEntry>());
        }

        using var stream = new FileStream(
            _logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);

        bool reset = stream.Length < _position;
        if (reset)
            ResetState();

        stream.Position = _position;
        using var appended = new MemoryStream();
        stream.CopyTo(appended);
        _position = stream.Position;

        if (appended.Length == 0)
            return new LogTailReadBatch(reset, Array.Empty<LogTailEntry>());

        byte[] newBytes = appended.ToArray();
        byte[] combined;
        if (_pendingBytes.Length == 0)
        {
            combined = newBytes;
        }
        else
        {
            combined = new byte[_pendingBytes.Length + newBytes.Length];
            _pendingBytes.CopyTo(combined, 0);
            newBytes.CopyTo(combined, _pendingBytes.Length);
        }

        int completeLength = Array.LastIndexOf(combined, (byte)'\n') + 1;
        if (completeLength == 0)
        {
            _pendingBytes = combined;
            return new LogTailReadBatch(reset, Array.Empty<LogTailEntry>());
        }

        _pendingBytes = completeLength == combined.Length ? [] : combined[completeLength..];
        string text = Encoding.UTF8.GetString(combined, 0, completeLength);
        var entries = new List<LogTailEntry>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (TryParse(line.TrimStart('\uFEFF'), out LogTailEntry? entry))
            {
                entries.Add(entry!);
                _lastEntry = entry;
            }
            else if (_lastEntry is not null)
            {
                _lastEntry.AppendContinuation(line);
            }
        }

        return new LogTailReadBatch(reset, entries);
    }

    public void Reset() => ResetState();

    private void ResetState()
    {
        _position = 0;
        _pendingBytes = [];
        _lastEntry = null;
    }

    internal static bool TryParse(string line, out LogTailEntry? entry)
    {
        entry = null;
        if (line.Length < 12 || line[0] != '[')
            return false;

        int timestampEnd = line.IndexOf("] [", StringComparison.Ordinal);
        if (timestampEnd <= 1)
            return false;
        int severityStart = timestampEnd + 3;
        int severityEnd = line.IndexOf("] [", severityStart, StringComparison.Ordinal);
        if (severityEnd <= severityStart)
            return false;
        int categoryStart = severityEnd + 3;
        int categoryEnd = line.IndexOf("] ", categoryStart, StringComparison.Ordinal);
        if (categoryEnd < categoryStart)
            return false;

        if (!DateTimeOffset.TryParse(
                line.AsSpan(1, timestampEnd - 1),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            return false;
        }

        string severityCode = line[severityStart..severityEnd];
        LogLevel level = severityCode switch
        {
            "CRT" => LogLevel.Critical,
            "WRN" => LogLevel.Warning,
            "INF" => LogLevel.Info,
            "VRB" => LogLevel.Verbose,
            _ => (LogLevel)int.MaxValue,
        };
        string severity = level switch
        {
            LogLevel.Critical => "Critical",
            LogLevel.Warning => "Warning",
            LogLevel.Info => "Info",
            LogLevel.Verbose => "Verbose",
            _ => severityCode,
        };
        string category = line[categoryStart..categoryEnd];
        string message = line[(categoryEnd + 2)..];
        entry = new LogTailEntry(timestamp, level, severity, category, message, line);
        return true;
    }
}

public static class LogTailFilter
{
    public static IReadOnlyList<LogTailEntry> Apply(
        IEnumerable<LogTailEntry> entries,
        string? category,
        LogLevel? severity,
        DateTimeOffset? since,
        string? text)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string? normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        string? normalizedText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        return entries.Where(entry =>
                (normalizedCategory is null || string.Equals(entry.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase))
                && (severity is null || entry.Level == severity)
                && (since is null || entry.Timestamp >= since.Value)
                && (normalizedText is null || entry.RawText.Contains(normalizedText, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}