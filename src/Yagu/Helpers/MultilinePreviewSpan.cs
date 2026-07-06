namespace Yagu.Helpers;

/// <summary>
/// Pure geometry for full-span (Phase 3) highlighting of a cross-line (multiline) match in the
/// preview/editor. A multiline <c>SearchResult</c> stores only its span endpoints — the start line +
/// <see cref="Models.SearchResult.SourceMatchStartColumn"/> and the
/// <see cref="Models.SearchResult.MatchEndLineNumber"/> + <see cref="Models.SearchResult.MatchEndColumn"/> —
/// so each rendered line inside the span needs its own column range computed:
/// <list type="bullet">
/// <item>the START line: from the match start column to end of line,</item>
/// <item>every BODY line: the whole line,</item>
/// <item>the END line: from column 0 to the match end column.</item>
/// </list>
/// Columns are 0-based UTF-16 source columns (exclusive end), the same space as the stored columns.
/// This is a pure function so it is unit-testable independently of the WinUI preview builder.
/// </summary>
public static class MultilinePreviewSpan
{
    /// <summary>
    /// Computes the half-open column range <c>[spanStart, spanEnd)</c> to highlight on
    /// <paramref name="lineNumber"/> when it lies within the cross-line span
    /// <paramref name="startLine"/>..<paramref name="endLine"/>. Returns <c>false</c> when the line is
    /// outside the span or the resulting range is empty (e.g. an empty body line, or a start/end
    /// column that clamps to a zero-width range), so callers can skip coloring that line entirely.
    /// </summary>
    /// <param name="lineNumber">1-based number of the line being rendered.</param>
    /// <param name="lineLength">Length (UTF-16 code units) of that line's text.</param>
    /// <param name="startLine">1-based start line of the match span.</param>
    /// <param name="startColumn">0-based start column on <paramref name="startLine"/>.</param>
    /// <param name="endLine">1-based end line of the match span.</param>
    /// <param name="endColumn">0-based exclusive end column on <paramref name="endLine"/>.</param>
    public static bool TryGetLineSpan(
        int lineNumber,
        int lineLength,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        out int spanStart,
        out int spanEnd)
    {
        spanStart = 0;
        spanEnd = 0;

        if (lineLength < 0)
            lineLength = 0;
        if (endLine < startLine)
            return false;
        if (lineNumber < startLine || lineNumber > endLine)
            return false;

        int rawStart;
        int rawEnd;
        if (startLine == endLine)
        {
            // Degenerate single-line span (not a true cross-line match): highlight [startCol, endCol).
            rawStart = startColumn;
            rawEnd = endColumn;
        }
        else if (lineNumber == startLine)
        {
            rawStart = startColumn;
            rawEnd = lineLength;
        }
        else if (lineNumber == endLine)
        {
            rawStart = 0;
            rawEnd = endColumn;
        }
        else
        {
            rawStart = 0;
            rawEnd = lineLength;
        }

        int s = Math.Clamp(rawStart, 0, lineLength);
        int e = Math.Clamp(rawEnd, 0, lineLength);
        if (e <= s)
            return false;

        spanStart = s;
        spanEnd = e;
        return true;
    }
}
