using Microsoft.Extensions.Logging;

namespace Yagu.Services.Ai;

/// <summary>
/// Bridges <see cref="ILogger"/> output emitted by the Foundry Local SDK into Yagu's
/// <see cref="LogService"/>, so the on-device model runtime's internal diagnostics — execution-provider
/// downloads, catalog/model resolution, native load failures — land in the same log as the rest of the
/// semantic layer. Without this, that runtime detail is dropped (the SDK was previously handed a
/// <c>NullLogger</c>), leaving native/runtime failures invisible.
///
/// Level mapping (Microsoft.Extensions.Logging -&gt; <see cref="LogLevel"/>):
/// Trace/Debug -&gt; Verbose, Information -&gt; Info, Warning/Error -&gt; Warning, Critical -&gt; Critical.
/// Error folds to Warning to match the codebase convention where Critical is reserved for fatal,
/// app-ending conditions.
/// </summary>
internal sealed class FoundryLoggerAdapter : ILogger
{
    public static readonly FoundryLoggerAdapter Instance = new();

    private const string LogSource = "Semantic.Foundry";

    private FoundryLoggerAdapter() { }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.None) return false;

        var svc = LogService.Instance;
        return logLevel switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace or Microsoft.Extensions.Logging.LogLevel.Debug => svc.IsVerboseEnabled,
            Microsoft.Extensions.Logging.LogLevel.Information => svc.IsInfoEnabled,
            // Warning and above always pass the default file level (Warning), so let them through.
            _ => true,
        };
    }

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.None) return;

        string message;
        try { message = formatter(state, exception); }
        catch { message = state?.ToString() ?? string.Empty; }

        if (string.IsNullOrEmpty(message) && exception is null) return;

        var svc = LogService.Instance;
        switch (logLevel)
        {
            case Microsoft.Extensions.Logging.LogLevel.Critical:
                svc.Critical(LogSource, message, exception);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Error:
            case Microsoft.Extensions.Logging.LogLevel.Warning:
                svc.Warning(LogSource, message, exception);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Information:
                svc.Info(LogSource, message);
                break;
            default: // Trace, Debug
                svc.Verbose(LogSource, message, exception);
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
