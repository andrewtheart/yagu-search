using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using YaguLevel = Yagu.Services.LogLevel;

namespace Yagu.Services.Logging;

/// <summary>
/// An <see cref="ILogger"/> that routes structured and source-generated (<c>[LoggerMessage]</c>) log
/// entries into the app's existing async <see cref="LogService"/> file/console sink. One instance per
/// category — the category becomes the log line's <c>[source]</c>. It performs no reflection and no I/O
/// (every entry is enqueued to the shared background-flushed queue), so it is Native-AOT safe and keeps
/// the current logging behavior unchanged apart from the richer, template-based message.
/// </summary>
internal sealed class LogServiceLogger : ILogger
{
    private readonly string _category;
    private readonly LogService _sink;

    public LogServiceLogger(string category, LogService sink)
    {
        _category = category ?? throw new ArgumentNullException(nameof(category));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(MsLogLevel logLevel)
        => logLevel != MsLogLevel.None && _sink.IsEnabled(MapLevel(logLevel));

    public void Log<TState>(
        MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (logLevel == MsLogLevel.None)
            return;

        YaguLevel level = MapLevel(logLevel);
        // Gate BEFORE rendering the message template — a dropped entry costs only this check.
        if (!_sink.IsEnabled(level))
            return;

        _sink.WriteStructured(level, _category, formatter(state, exception), exception);
    }

    /// <summary>
    /// Maps a Microsoft.Extensions.Logging level onto Yagu's four-tier sink. Yagu has no dedicated
    /// Trace/Debug/Error tier: Trace and Debug fold into <see cref="YaguLevel.Verbose"/>, and Error folds
    /// into <see cref="YaguLevel.Warning"/>. <see cref="LogLevel.Critical"/> stays Critical so it keeps
    /// firing the telemetry/bug-report event; Error is deliberately NOT mapped to Critical to avoid
    /// flooding that path with routine errors.
    /// </summary>
    private static YaguLevel MapLevel(MsLogLevel level) => level switch
    {
        MsLogLevel.Trace => YaguLevel.Verbose,
        MsLogLevel.Debug => YaguLevel.Verbose,
        MsLogLevel.Information => YaguLevel.Info,
        MsLogLevel.Warning => YaguLevel.Warning,
        MsLogLevel.Error => YaguLevel.Warning,
        MsLogLevel.Critical => YaguLevel.Critical,
        _ => YaguLevel.Verbose,
    };

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
