namespace QuickGrep.Helpers;

/// <summary>Truncate long lines for display.</summary>
public static class LineTruncator
{
    private static int _truncatedLength = 500;

    public readonly record struct DisplayLine(string Text, int MatchStart);

    /// <summary>Lines longer than 2× this value are truncated to this length + ellipsis.</summary>
    public static int TruncatedLength
    {
        get => _truncatedLength;
        set => _truncatedLength = Math.Max(50, value);
    }

    public static int MaxDisplayLength => TruncatedLength * 2;
    public const string Ellipsis = "…";

    public static string Truncate(string line)
    {
        if (line is null) return string.Empty;
        if (line.Length <= MaxDisplayLength) return line;
        return string.Concat(line.AsSpan(0, TruncatedLength), Ellipsis);
    }

    public static DisplayLine TruncateAroundMatch(string? line, int matchStart, int matchLength)
    {
        line ??= string.Empty;
        if (line.Length <= MaxDisplayLength || matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
            return new DisplayLine(Truncate(line), matchStart);

        int safeMatchLength = Math.Min(matchLength, line.Length - matchStart);
        int visibleMatchLength = Math.Min(safeMatchLength, TruncatedLength);
        int contextChars = Math.Max(0, (TruncatedLength - visibleMatchLength) / 2);

        int start = Math.Max(0, matchStart - contextChars);
        int end = Math.Min(line.Length, start + TruncatedLength);
        if (end - start < TruncatedLength)
            start = Math.Max(0, end - TruncatedLength);

        bool hasPrefix = start > 0;
        bool hasSuffix = end < line.Length;
        var prefix = hasPrefix ? Ellipsis : string.Empty;
        var suffix = hasSuffix ? Ellipsis : string.Empty;
        var text = string.Concat(prefix, line.AsSpan(start, end - start), suffix);
        int displayMatchStart = matchStart - start + prefix.Length;

        return new DisplayLine(text, displayMatchStart);
    }
}
