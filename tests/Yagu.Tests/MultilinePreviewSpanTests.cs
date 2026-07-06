using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="MultilinePreviewSpan.TryGetLineSpan"/>, the pure per-line column
/// geometry used for full-span (Phase 3) multiline highlighting in the preview/editor.
/// </summary>
public sealed class MultilinePreviewSpanTests
{
    [Fact]
    public void StartLine_HighlightsFromMatchColumnToEndOfLine()
    {
        // Span lines 3..5, start col 4. Line 3 is 10 chars long → highlight [4, 10).
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 3, lineLength: 10,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 2,
            out int start, out int end);

        Assert.True(ok);
        Assert.Equal(4, start);
        Assert.Equal(10, end);
    }

    [Fact]
    public void BodyLine_HighlightsWholeLine()
    {
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 4, lineLength: 7,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 2,
            out int start, out int end);

        Assert.True(ok);
        Assert.Equal(0, start);
        Assert.Equal(7, end);
    }

    [Fact]
    public void EndLine_HighlightsFromStartToEndColumn()
    {
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 5, lineLength: 9,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 2,
            out int start, out int end);

        Assert.True(ok);
        Assert.Equal(0, start);
        Assert.Equal(2, end);
    }

    [Theory]
    [InlineData(2)]  // before the span
    [InlineData(6)]  // after the span
    public void LineOutsideSpan_ReturnsFalse(int lineNumber)
    {
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber, lineLength: 10,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 2,
            out int start, out int end);

        Assert.False(ok);
        Assert.Equal(0, start);
        Assert.Equal(0, end);
    }

    [Fact]
    public void EmptyBodyLine_ReturnsFalse()
    {
        // A blank body line has nothing to color.
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 4, lineLength: 0,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 2,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void EndColumnZero_ReturnsFalse()
    {
        // Match ends exactly at the start of the end line (col 0) → nothing to color there.
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 5, lineLength: 9,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 0,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void StartColumnBeyondLineLength_ClampsToEmpty_ReturnsFalse()
    {
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 3, lineLength: 4,
            startLine: 3, startColumn: 10,
            endLine: 5, endColumn: 2,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void StartAndEndColumns_ClampToLineLength()
    {
        // End column beyond the end line's length clamps to the line length.
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 5, lineLength: 3,
            startLine: 3, startColumn: 4,
            endLine: 5, endColumn: 99,
            out int start, out int end);

        Assert.True(ok);
        Assert.Equal(0, start);
        Assert.Equal(3, end);
    }

    [Fact]
    public void DegenerateSingleLineSpan_HighlightsStartToEndColumn()
    {
        // startLine == endLine: highlight the inclusive column window.
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 3, lineLength: 20,
            startLine: 3, startColumn: 4,
            endLine: 3, endColumn: 12,
            out int start, out int end);

        Assert.True(ok);
        Assert.Equal(4, start);
        Assert.Equal(12, end);
    }

    [Fact]
    public void EndLineBeforeStartLine_ReturnsFalse()
    {
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 3, lineLength: 10,
            startLine: 5, startColumn: 0,
            endLine: 3, endColumn: 2,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void NegativeLineLength_TreatedAsEmpty()
    {
        bool ok = MultilinePreviewSpan.TryGetLineSpan(
            lineNumber: 4, lineLength: -5,
            startLine: 3, startColumn: 0,
            endLine: 5, endColumn: 2,
            out _, out _);

        Assert.False(ok);
    }
}
