namespace Yagu.Services.Cli;

/// <summary>
/// A 1-based, inclusive line window for the CLI <c>--read</c> command (<c>--lines &lt;first&gt;-&lt;last&gt;</c>).
/// </summary>
/// <remarks>
/// Reads are always bounded: a machine caller asks for a window, never "the file". <see cref="Last"/>
/// may be <see cref="int.MaxValue"/> for an open-ended <c>10-</c> window, in which case the response
/// budget — not the range — decides where the read stops.
/// </remarks>
internal readonly record struct CliLineRange(int First, int Last)
{
    /// <summary>The whole file, bounded by the response budget.</summary>
    public static CliLineRange All { get; } = new(1, int.MaxValue);

    /// <summary>True when no explicit end line was given.</summary>
    public bool IsOpenEnded => Last == int.MaxValue;

    /// <summary>Number of lines requested, saturating for an open-ended window.</summary>
    public long Count => IsOpenEnded ? int.MaxValue : (long)Last - First + 1;

    public bool Contains(int lineNumber) => lineNumber >= First && lineNumber <= Last;

    /// <summary>
    /// Parses <c>first-last</c>, <c>first-</c> (open-ended) or <c>first</c> (single line). Rejects a
    /// zero/negative bound and an inverted range with a human-readable <paramref name="error"/>.
    /// </summary>
    public static bool TryParse(string? raw, out CliLineRange range, out string? error)
    {
        range = All;
        error = null;

        string value = raw?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "--lines requires a value, e.g. 1-200 or 500- for an open-ended window.";
            return false;
        }

        int dash = value.IndexOf('-');
        string firstText = dash < 0 ? value : value[..dash];
        string lastText = dash < 0 ? value : value[(dash + 1)..];

        if (!TryParseBound(firstText, out int first))
        {
            error = $"invalid --lines start '{firstText}'. Expected a positive line number.";
            return false;
        }

        int last;
        if (dash >= 0 && lastText.Trim().Length == 0)
        {
            last = int.MaxValue;
        }
        else if (!TryParseBound(lastText, out last))
        {
            error = $"invalid --lines end '{lastText}'. Expected a positive line number.";
            return false;
        }

        if (last < first)
        {
            error = $"invalid --lines range '{value}': end line {last} precedes start line {first}.";
            return false;
        }

        range = new CliLineRange(first, last);
        return true;
    }

    private static bool TryParseBound(string text, out int value) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
        && value >= 1;
}
