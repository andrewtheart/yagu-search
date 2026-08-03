using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yagu.Services.Logging;

/// <summary>
/// Static accessor for category <see cref="ILogger"/>s backed by the app's async <see cref="LogService"/>.
/// This is the entry point for structured logging across Yagu — e.g.
/// <c>YaguLog.For("ContentIndex").LogWarning(ex, "Size-limit check failed for {Root}", root)</c> — and it
/// supplies the <c>ILogger</c> argument to source-generated <c>[LoggerMessage]</c> methods on hot paths.
/// Loggers are cached per category, so <see cref="For"/> is cheap to call repeatedly.
/// </summary>
public static class YaguLog
{
    private static readonly ConcurrentDictionary<string, ILogger> s_loggers = new(StringComparer.Ordinal);

    /// <summary>Returns the cached <see cref="ILogger"/> for <paramref name="category"/> (the log line's
    /// <c>[source]</c> tag). Categories are conventionally short subsystem names, e.g. "ContentIndex".</summary>
    public static ILogger For(string category)
        => s_loggers.GetOrAdd(category, static c => new LogServiceLogger(c, LogService.Instance));
}
