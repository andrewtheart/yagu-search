using System;

namespace TextControlBoxNS;

/// <summary>
/// Provides optional diagnostic logging callbacks for host applications.
/// </summary>
public static class TextControlBoxDiagnostics
{
    /// <summary>
    /// Gets or sets a callback that receives verbose diagnostic messages.
    /// </summary>
    public static Action<string, string> VerboseLogger { get; set; }

    /// <summary>
    /// Gets or sets a callback that reports whether verbose diagnostics are enabled.
    /// </summary>
    public static Func<bool> IsVerboseEnabledProvider { get; set; }

    /// <summary>
    /// Gets or sets a callback that receives non-fatal error diagnostics (e.g. a rendering
    /// exception that was caught and swallowed to avoid a process-killing fail-fast). Unlike
    /// <see cref="VerboseLogger"/>, this is always invoked regardless of the verbose flag.
    /// </summary>
    public static Action<string, string, Exception> ErrorLogger { get; set; }

    internal static bool IsVerboseEnabled => VerboseLogger is not null
        && (IsVerboseEnabledProvider?.Invoke() ?? true);

    internal static void Verbose(string source, string message)
    {
        if (!IsVerboseEnabled) return;
        try { VerboseLogger?.Invoke(source, message); }
        catch { }
    }

    internal static void Error(string source, string message, Exception ex)
    {
        try { ErrorLogger?.Invoke(source, message, ex); }
        catch { }
    }

    internal static string DescribeText(string text, int maxChars = 80)
    {
        if (string.IsNullOrEmpty(text)) return "<empty>";
        var escaped = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        if (escaped.Length > maxChars)
            escaped = escaped[..maxChars] + "...";
        return $"'{escaped}' len={text.Length}";
    }
}