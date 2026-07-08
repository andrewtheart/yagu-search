using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Text;
using TextControlBoxNS.Core.Text;
using TextControlBoxNS.Helper;
using TextControlBoxNS.Models;
using Windows.Foundation;

namespace TextControlBoxNS.Core.Renderer;

internal class TextRenderer
{
    public CanvasTextFormat TextFormat = null;
    public CanvasTextLayout DrawnTextLayout = null;
    public CanvasTextLayout CurrentLineTextLayout = null;


    public bool NeedsUpdateTextLayout = true;
    public bool NeedsTextFormatUpdate = true;
    public float SingleLineHeight { get => TextFormat == null ? 0 : TextFormat.LineSpacing; }
    public float HorizontalOffset => (float)-scrollManager.HorizontalScroll + HorizontalSlicePixelOffset;
    public int NumberOfStartLine = 0;
    public int NumberOfRenderedLines = 0;
    public string RenderedText = "";
    public string OldRenderedText = null;
    public int StartVisualRow { get; private set; }
    public int WrappedStartRowOffset { get; private set; }
    public bool IsWordWrapEnabled => textLayoutManager?.WordWrap == true;
    public bool IsVirtualizedWrappedLine { get; private set; }
    public int VirtualizedWrappedRowsToRender { get; private set; }
    public int VirtualizedLineSliceStart { get; private set; }
    public int VirtualizedLineCharsPerRow { get; private set; }

    private CursorManager cursorManager;
    private TextManager textManager;
    private ScrollManager scrollManager;
    private LineNumberRenderer lineNumberRenderer;
    private TextLayoutManager textLayoutManager;
    private DesignHelper designHelper;
    private Grid scrollGrid;
    private LongestLineManager longestLineManager;
    private SearchManager searchManager;
    private CoreTextControlBox coreTextbox;
    private CanvasUpdateManager canvasUpdateManager;
    private ZoomManager zoomManager;
    private WhitespaceCharactersRenderer invisibleCharactersRenderer;
    private LinkRenderer linkRenderer;
    private LinkHighlightManager linkHighlightManager;
    private readonly Dictionary<int, int> wrappedLineRowCountCache = new();
    private readonly List<int> wrappedLineStartRowsCache = new();
    // Lines whose text changed (without changing the line count) since the last wrap-metrics
    // update. They are re-measured incrementally instead of rebuilding metrics for every line.
    private readonly HashSet<int> dirtyWrapLines = new();
    private float cachedWrapWidth;
    private int cachedTotalVisualRows = 1;
    private bool wrapMetricsDirty = true;
    // Above this many dirty lines, a full rebuild is cheaper than many incremental patches.
    private const int IncrementalWrapRemeasureLimit = 64;
    private const int LongWrappedLineVirtualizationThreshold = 100_000;
    private const int VirtualizedWrappedLinePaddingRows = 2;

    // ── Horizontal virtualization (non-wrap, very long lines) ──
    private const int HorizontalVirtualizationThreshold = 50_000;
    /// <summary>Character index where the horizontal slice starts within the original rendered text.</summary>
    public int HorizontalSliceStart { get; private set; }
    /// <summary>True when horizontal virtualization is active (lines were sliced for perf).</summary>
    public bool IsHorizontallyVirtualized { get; private set; }
    /// <summary>Max chars kept per line by the horizontal slice (the slice window width). 0 when not sliced.
    /// Every rendered line is sliced to the SAME window [HorizontalSliceStart, HorizontalSliceStart+HorizontalSliceLength),
    /// so a uniform pixel offset re-aligns the sliced text to document coordinates and the caret/selection
    /// index mapping is a simple per-line clamp plus a prefix sum (see <see cref="_renderedLineSlicePrefix"/>).</summary>
    public int HorizontalSliceLength { get; private set; }
    /// <summary>Cumulative rendered-layout character offset of each rendered line when horizontally sliced:
    /// entry k is the offset of the k-th rendered line (ordinal from <see cref="NumberOfStartLine"/>) inside
    /// <see cref="DrawnTextLayout"/>, accounting for each prior line's SLICED length plus a newline. Lets
    /// selection map a document (line,char) to a multi-line rendered index in O(1). Rebuilt each slice.</summary>
    private readonly List<int> _renderedLineSlicePrefix = new();
    /// <summary>Cached measured width of a single character in the current font (monospace assumption).</summary>
    private float _cachedCharWidth;
    internal float CachedCharWidth => _cachedCharWidth > 0 ? _cachedCharWidth : Math.Max(1, zoomManager.ZoomedFontSize * 0.6f);
    /// <summary>The visible char range covered by the current slice [start, end). If scroll stays within, reuse layout.</summary>
    private int _hSliceVisibleStart;
    private int _hSliceVisibleEnd;
    /// <summary>Pixel offset of the horizontal slice start (for cursor hit-testing).</summary>
    public float HorizontalSlicePixelOffset => IsHorizontallyVirtualized
        ? HorizontalSliceStart * (_cachedCharWidth > 0 ? _cachedCharWidth : Math.Max(1, zoomManager.ZoomedFontSize * 0.6f))
        : 0;

    public float DrawTextOffsetX { get; private set; }
    public float DrawTextOffsetY { get; private set; }

    public void Init(
        CursorManager cursorManager,
        DesignHelper designHelper,
        TextLayoutManager textLayoutManager,
        TextManager textManager,
        ScrollManager scrollManager,
        LineNumberRenderer lineNumberRenderer,
        LongestLineManager longestLineManager,
        CoreTextControlBox textbox,
        SearchManager searchManager,
        CanvasUpdateManager canvasUpdateManager,
        ZoomManager zoomManager,
        WhitespaceCharactersRenderer invisibleCharactersRenderer,
        LinkRenderer linkRenderer,
        LinkHighlightManager linkHighlightManager)
    {
        this.cursorManager = cursorManager;
        this.textManager = textManager;
        this.designHelper = designHelper;
        this.textLayoutManager = textLayoutManager;
        this.scrollManager = scrollManager;
        this.lineNumberRenderer = lineNumberRenderer;
        this.longestLineManager = longestLineManager;
        this.searchManager = searchManager;
        this.coreTextbox = textbox;
        this.scrollGrid = textbox.scrollGrid;
        this.canvasUpdateManager = canvasUpdateManager;
        this.zoomManager = zoomManager;
        this.invisibleCharactersRenderer = invisibleCharactersRenderer;
        this.linkRenderer = linkRenderer;
        this.linkHighlightManager = linkHighlightManager;

        textManager.LinesChanged += InvalidateWrapMetrics;
        textManager.SingleLineTextChanged += OnSingleLineTextChanged;
    }

    public void CheckDispose()
    {
        TextFormat?.Dispose();
        DrawnTextLayout?.Dispose();
        CurrentLineTextLayout?.Dispose();
        invisibleCharactersRenderer.CheckDispose();
    }

    public void ClearWrapCache()
    {
        wrappedLineRowCountCache.Clear();
        wrappedLineStartRowsCache.Clear();
        dirtyWrapLines.Clear();
        cachedWrapWidth = 0;
        cachedTotalVisualRows = 1;
        wrapMetricsDirty = true;
        StartVisualRow = 0;
        WrappedStartRowOffset = 0;
        ResetVirtualizedWrappedLineState();
        NeedsUpdateTextLayout = true;
        lineNumberRenderer?.NeedsUpdateLineNumbers();
    }

    public void InvalidateWrapMetrics()
    {
        wrappedLineRowCountCache.Clear();
        wrappedLineStartRowsCache.Clear();
        dirtyWrapLines.Clear();
        cachedTotalVisualRows = Math.Max(1, textManager?.LinesCount ?? 1);
        wrapMetricsDirty = true;
        ResetVirtualizedWrappedLineState();
        NeedsUpdateTextLayout = true;
        lineNumberRenderer?.NeedsUpdateLineNumbers();
    }

    // A single line's text changed without changing the line count. Re-measuring every line on
    // each keystroke (the previous full InvalidateWrapMetrics behavior) created a CanvasTextLayout
    // per document line and caused multi-second "not responding" hangs while typing in word-wrap
    // mode on large files. Instead, mark just this line dirty and update wrap metrics incrementally.
    private void OnSingleLineTextChanged(int lineIndex)
    {
        // Only track dirty lines while word wrap is on; otherwise EnsureWrapMetrics early-returns
        // and the set would grow unbounded. A pending full rebuild (wrapMetricsDirty) already covers
        // every line, so there is no need to record individual lines in that case.
        if (IsWordWrapEnabled && !wrapMetricsDirty && lineIndex >= 0)
            dirtyWrapLines.Add(lineIndex);

        ResetVirtualizedWrappedLineState();
        NeedsUpdateTextLayout = true;
        lineNumberRenderer?.NeedsUpdateLineNumbers();
    }

    private void ResetVirtualizedWrappedLineState()
    {
        IsVirtualizedWrappedLine = false;
        VirtualizedWrappedRowsToRender = 0;
        VirtualizedLineSliceStart = 0;
        VirtualizedLineCharsPerRow = 0;
    }

    //Check whether the current line is outside the bounds of the visible area
    public bool OutOfRenderedArea(int line)
    {
        return line < NumberOfStartLine || line >= NumberOfStartLine + NumberOfRenderedLines;
    }

    public void UpdateCurrentLineTextLayout(CanvasControl canvasText)
    {
        CurrentLineTextLayout?.Dispose();
        if (cursorManager.LineNumber >= textManager.LinesCount)
        {
            CurrentLineTextLayout = null;
            return;
        }

        EnsureWrapMetrics(canvasText);
        if (ShouldVirtualizeWrappedLine(cursorManager.LineNumber))
        {
            var virtualizedText = IsVirtualizedWrappedLine && cursorManager.LineNumber == NumberOfStartLine
                ? new LineSliceResult(RenderedText, ReadOnlySpan<string>.Empty)
                : BuildVirtualizedWrappedLineRenderData(canvasText, cursorManager.LineNumber);
            CurrentLineTextLayout = textLayoutManager.CreateTextLayout(
                canvasText,
                TextFormat,
                virtualizedText.Text,
                GetWrapWidth(canvasText),
                (float)Math.Max(canvasText.Size.Height, (VirtualizedWrappedRowsToRender + 1) * Math.Max(1, SingleLineHeight)));
            return;
        }

        string lineText = textManager.GetLineText(cursorManager.LineNumber) + "|";

        // Slice the current line to the SAME horizontal window as the main text (see Draw). This layout
        // backs the caret and click hit-testing, so it MUST start at HorizontalSliceStart: the caret's
        // rendered index is (documentChar - HorizontalSliceStart) and the caret x adds HorizontalSlicePixelOffset
        // (= HorizontalSliceStart * charWidth). A window computed independently here would misplace the caret.
        if (IsHorizontallyVirtualized && !IsWordWrapEnabled && HorizontalSliceLength > 0)
        {
            int sliceStart = Math.Min(HorizontalSliceStart, lineText.Length);
            int len = Math.Min(HorizontalSliceLength, lineText.Length - sliceStart);
            lineText = len > 0 ? lineText.Substring(sliceStart, len) : string.Empty;
        }

        var layoutSize = IsWordWrapEnabled
            ? new Size(GetWrapWidth(canvasText), Math.Max(canvasText.Size.Height, (GetWrappedRowCount(cursorManager.LineNumber) + 1) * Math.Max(1, SingleLineHeight)))
            : canvasText.Size;

        CurrentLineTextLayout =
            textLayoutManager.CreateTextLayout(
            canvasText,
            TextFormat,
            lineText,
            layoutSize);
    }

    private float GetWrapWidth(CanvasControl canvasText)
        => Math.Max(1, (float)canvasText.ActualWidth);

    private bool ShouldVirtualizeWrappedLine(int lineIndex)
        => IsWordWrapEnabled
           && lineIndex >= 0
           && lineIndex < textManager.LinesCount
           && textManager.GetLineLength(lineIndex) >= LongWrappedLineVirtualizationThreshold;

    private int EstimateWrappedCharsPerRow(CanvasControl canvasText)
    {
        float wrapWidth = GetWrapWidth(canvasText);
        float averageCharWidth = Math.Max(1, zoomManager.ZoomedFontSize * 0.58f);
        return Math.Max(1, (int)Math.Floor(wrapWidth / averageCharWidth));
    }

    private int EstimateWrappedRowCount(CanvasControl canvasText, int textLength)
        => Math.Max(1, (int)Math.Ceiling(textLength / (double)EstimateWrappedCharsPerRow(canvasText)));

    private LineSliceResult BuildVirtualizedWrappedLineRenderData(CanvasControl canvasText, int lineIndex)
    {
        string lineText = textManager.GetLineText(lineIndex);
        int charsPerRow = EstimateWrappedCharsPerRow(canvasText);
        int totalRows = Math.Max(1, (int)Math.Ceiling(lineText.Length / (double)charsPerRow));
        int startRow = Math.Clamp(WrappedStartRowOffset, 0, totalRows - 1);
        int rowsToRender = Math.Min(totalRows - startRow, GetVisibleVisualRowCount(canvasText, VirtualizedWrappedLinePaddingRows));
        int sliceStart = Math.Min(lineText.Length, startRow * charsPerRow);
        int newlineLength = textManager.NewLineCharacter.Length;

        var builder = new StringBuilder(Math.Min(lineText.Length - sliceStart, rowsToRender * (charsPerRow + newlineLength)));
        int offset = sliceStart;
        int rowsRendered = 0;
        while (rowsRendered < rowsToRender && offset < lineText.Length)
        {
            if (rowsRendered > 0)
                builder.Append(textManager.NewLineCharacter);

            int length = Math.Min(charsPerRow, lineText.Length - offset);
            builder.Append(lineText, offset, length);
            offset += length;
            rowsRendered++;
        }

        if (rowsRendered == 0)
        {
            builder.Append(string.Empty);
            rowsRendered = 1;
        }

        IsVirtualizedWrappedLine = true;
        VirtualizedWrappedRowsToRender = rowsRendered;
        VirtualizedLineSliceStart = sliceStart;
        VirtualizedLineCharsPerRow = charsPerRow;
        return new LineSliceResult(builder.ToString(), ReadOnlySpan<string>.Empty);
    }

    public int GetRenderedCharacterIndexForDocumentCharacter(int lineIndex, int characterPosition)
    {
        if (!IsVirtualizedWrappedLine || lineIndex != NumberOfStartLine || VirtualizedLineCharsPerRow <= 0)
        {
            // Account for horizontal virtualization slice offset. Every rendered line is sliced to the same
            // window, so the per-line rendered index is the document position minus the slice start, clamped
            // to this line's sliced length (a line shorter than the window contributes fewer chars).
            if (IsHorizontallyVirtualized)
                return Math.Clamp(characterPosition - HorizontalSliceStart, 0, SlicedLineLength(lineIndex));
            return characterPosition;
        }

        int relative = characterPosition - VirtualizedLineSliceStart;
        if (relative < 0)
            return -1;

        int maxDocumentChars = VirtualizedWrappedRowsToRender * VirtualizedLineCharsPerRow;
        if (relative > maxDocumentChars)
            return -1;

        int row = relative / VirtualizedLineCharsPerRow;
        int column = relative % VirtualizedLineCharsPerRow;
        return relative + row * textManager.NewLineCharacter.Length;
    }

    public int GetDocumentCharacterIndexFromRenderedIndex(int lineIndex, int renderedIndex)
    {
        if (!IsVirtualizedWrappedLine || lineIndex != NumberOfStartLine || VirtualizedLineCharsPerRow <= 0)
        {
            // Account for horizontal virtualization slice offset (per-line: renderedIndex is within the
            // current line's sliced layout), clamped to the document line length.
            if (IsHorizontallyVirtualized)
                return Math.Clamp(renderedIndex + HorizontalSliceStart, 0, textManager.GetLineLength(lineIndex));
            return renderedIndex;
        }

        int rowStride = VirtualizedLineCharsPerRow + textManager.NewLineCharacter.Length;
        int row = Math.Max(0, renderedIndex / rowStride);
        int column = Math.Clamp(renderedIndex % rowStride, 0, VirtualizedLineCharsPerRow);
        int characterPosition = VirtualizedLineSliceStart + row * VirtualizedLineCharsPerRow + column;
        return Math.Clamp(characterPosition, 0, textManager.GetLineLength(lineIndex));
    }

    // ── Multi-line horizontal virtualization (non-wrap, many very long lines) ─────────────────────

    /// <summary>Number of characters line <paramref name="lineIndex"/> contributes to the current
    /// horizontal slice: its length past <see cref="HorizontalSliceStart"/>, capped at the slice window
    /// width. 0 when the line ends before the window (nothing of it is visible at this scroll position).</summary>
    private int SlicedLineLength(int lineIndex)
    {
        if (HorizontalSliceLength <= 0 || lineIndex < 0 || lineIndex >= textManager.LinesCount)
            return 0;
        int full = textManager.GetLineLength(lineIndex);
        return Math.Clamp(full - HorizontalSliceStart, 0, HorizontalSliceLength);
    }

    /// <summary>Maps a document position (line, char) to its character index inside the multi-line
    /// horizontally-sliced <see cref="DrawnTextLayout"/>, using the per-line prefix offsets built by
    /// <see cref="BuildHorizontallySlicedText"/>. Returns -1 when the line is outside the rendered range.
    /// Used by selection rendering, which spans multiple rendered lines.</summary>
    public int GetRenderedLayoutIndexForDocument(int lineIndex, int characterPosition)
    {
        int ordinal = lineIndex - NumberOfStartLine;
        if (ordinal < 0 || ordinal >= _renderedLineSlicePrefix.Count)
            return -1;
        int inLine = Math.Clamp(characterPosition - HorizontalSliceStart, 0, SlicedLineLength(lineIndex));
        return _renderedLineSlicePrefix[ordinal] + inLine;
    }

    /// <summary>Decides whether to horizontally virtualize the current (non-wrap) frame and, if so, the
    /// slice window [<paramref name="sliceStart"/>, sliceStart+<paramref name="sliceLen"/>). Only triggers
    /// when at least one visible line exceeds <see cref="HorizontalVirtualizationThreshold"/>, so files
    /// without a pathologically long line keep the normal (untouched) render path. Reuses the previous
    /// window while the viewport stays inside the buffered safe zone so small horizontal scrolls don't
    /// re-slice.</summary>
    private bool ShouldHorizontallySlice(CanvasControl canvasText, out int sliceStart, out int sliceLen)
    {
        sliceStart = 0;
        sliceLen = 0;
        if (IsWordWrapEnabled || NumberOfRenderedLines <= 0)
            return false;

        int end = Math.Min(NumberOfStartLine + NumberOfRenderedLines, textManager.LinesCount);
        int maxLen = 0;
        for (int i = NumberOfStartLine; i < end; i++)
        {
            int len = textManager.GetLineLength(i);
            if (len > maxLen)
                maxLen = len;
        }
        if (maxLen <= HorizontalVirtualizationThreshold)
            return false;

        float charWidth = CachedCharWidth;
        float viewportWidth = (float)canvasText.Size.Width;
        float hScroll = (float)scrollManager.HorizontalScroll;
        int visibleStartChar = Math.Max(0, (int)(hScroll / charWidth) - 50);
        int visibleEndChar = visibleStartChar + (int)(viewportWidth / charWidth) + 100;

        // Reuse the current window while the viewport is still inside its buffered safe zone.
        if (IsHorizontallyVirtualized && HorizontalSliceLength > 0
            && visibleStartChar >= _hSliceVisibleStart && visibleEndChar <= _hSliceVisibleEnd)
        {
            sliceStart = HorizontalSliceStart;
            sliceLen = HorizontalSliceLength;
            return true;
        }

        int visibleChars = Math.Max(1, visibleEndChar - visibleStartChar);
        int bufferChars = visibleChars * 3;
        sliceStart = Math.Max(0, visibleStartChar - bufferChars);
        sliceLen = visibleChars + (bufferChars * 2);
        _hSliceVisibleStart = visibleStartChar;
        _hSliceVisibleEnd = visibleEndChar + bufferChars; // inner safe zone
        return true;
    }

    /// <summary>Builds the visible text with every line sliced to the same horizontal window, joined with
    /// the newline character, and (re)builds <see cref="_renderedLineSlicePrefix"/> so document positions
    /// can be mapped back into the resulting layout. Never materializes the full (multi-megabyte) line text.</summary>
    private string BuildHorizontallySlicedText(int sliceStart, int sliceLen)
    {
        _renderedLineSlicePrefix.Clear();
        string newline = textManager.NewLineCharacter;
        int newlineLen = newline.Length;
        int end = Math.Min(NumberOfStartLine + NumberOfRenderedLines, textManager.LinesCount);

        int capacity = Math.Min(Math.Max(1, NumberOfRenderedLines) * (sliceLen + newlineLen), 1 << 21);
        var builder = new StringBuilder(capacity);
        int offset = 0;
        bool first = true;
        for (int i = NumberOfStartLine; i < end; i++)
        {
            if (!first)
            {
                builder.Append(newline);
                offset += newlineLen;
            }
            first = false;

            _renderedLineSlicePrefix.Add(offset); // rendered-layout offset of this line's text start

            string lineText = textManager.GetLineText(i);
            if (lineText.Length > sliceStart)
            {
                int len = Math.Min(sliceLen, lineText.Length - sliceStart);
                builder.Append(lineText, sliceStart, len);
                offset += len;
            }
        }
        return builder.ToString();
    }

    public void EnsureWrapMetrics(CanvasControl canvasText)
    {
        if (!IsWordWrapEnabled || canvasText == null || TextFormat == null)
            return;

        float wrapWidth = GetWrapWidth(canvasText);
        if (Math.Abs(cachedWrapWidth - wrapWidth) >= 0.5f)
        {
            cachedWrapWidth = wrapWidth;
            wrappedLineRowCountCache.Clear();
            wrappedLineStartRowsCache.Clear();
            dirtyWrapLines.Clear();
            wrapMetricsDirty = true;
            NeedsUpdateTextLayout = true;
            lineNumberRenderer.NeedsUpdateLineNumbers();
        }

        if (wrapMetricsDirty || wrappedLineStartRowsCache.Count != textManager.LinesCount + 1)
        {
            RebuildWrapMetrics(canvasText);
            return;
        }

        if (dirtyWrapLines.Count > 0)
            ApplyIncrementalWrapMetrics(canvasText);
    }

    private void RebuildWrapMetrics(CanvasControl canvasText)
    {
        wrappedLineRowCountCache.Clear();
        wrappedLineStartRowsCache.Clear();
        dirtyWrapLines.Clear();

        int visualRow = 0;
        wrappedLineStartRowsCache.Add(visualRow);
        for (int i = 0; i < textManager.LinesCount; i++)
        {
            int rows = MeasureWrappedRowCount(canvasText, i);
            wrappedLineRowCountCache[i] = rows;
            visualRow += rows;
            wrappedLineStartRowsCache.Add(visualRow);
        }

        cachedTotalVisualRows = Math.Max(1, visualRow);
        wrapMetricsDirty = false;
    }

    // Re-measures only the lines whose text changed and shifts the cumulative start-row cache by
    // the per-line row-count delta. This is cheap integer arithmetic plus at most a handful of
    // CanvasTextLayout measurements, versus a layout per document line in RebuildWrapMetrics.
    private void ApplyIncrementalWrapMetrics(CanvasControl canvasText)
    {
        if (dirtyWrapLines.Count > IncrementalWrapRemeasureLimit ||
            wrappedLineStartRowsCache.Count != textManager.LinesCount + 1)
        {
            RebuildWrapMetrics(canvasText);
            return;
        }

        bool changed = false;
        foreach (int line in dirtyWrapLines)
        {
            if (line < 0 || line >= textManager.LinesCount)
                continue;

            int oldRows = wrappedLineRowCountCache.TryGetValue(line, out int cached) ? cached : 1;
            int newRows = MeasureWrappedRowCount(canvasText, line);
            if (newRows == oldRows)
                continue;

            wrappedLineRowCountCache[line] = newRows;
            int delta = newRows - oldRows;
            for (int i = line + 1; i < wrappedLineStartRowsCache.Count; i++)
                wrappedLineStartRowsCache[i] += delta;
            changed = true;
        }

        if (changed && wrappedLineStartRowsCache.Count > 0)
            cachedTotalVisualRows = Math.Max(1, wrappedLineStartRowsCache[^1]);

        dirtyWrapLines.Clear();
    }

    private int MeasureWrappedRowCount(CanvasControl canvasText, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= textManager.LinesCount)
            return 1;

        string lineText = textManager.GetLineText(lineIndex);
        if (lineText.Length == 0)
            return 1;

        if (lineText.Length >= LongWrappedLineVirtualizationThreshold)
            return EstimateWrappedRowCount(canvasText, lineText.Length);

        float singleLineHeight = Math.Max(1, SingleLineHeight);
        float layoutHeight = Math.Max(singleLineHeight, (lineText.Length + 1) * singleLineHeight);
        using CanvasTextLayout lineLayout = textLayoutManager.CreateTextLayout(canvasText, TextFormat, lineText, cachedWrapWidth, layoutHeight);
        // Avoid lineLayout.LineMetrics — CanvasLineMetrics is non-blittable and throws
        // NotSupportedException on newer .NET. Use layout height / line height instead.
        int rowCount = (int)Math.Ceiling(lineLayout.LayoutBounds.Height / singleLineHeight);
        return Math.Max(1, rowCount);
    }

    private CanvasTextLayout CreateWrappedLineTextLayout(CanvasControl canvasText, int lineIndex, bool includeCaretMarker = false)
    {
        string lineText = textManager.GetLineText(lineIndex);
        if (includeCaretMarker)
            lineText += "|";

        float singleLineHeight = Math.Max(1, SingleLineHeight);
        int rowCount = GetWrappedRowCount(lineIndex);
        float layoutHeight = (float)Math.Max(canvasText.Size.Height, (rowCount + 1) * singleLineHeight);
        float wrapWidth = cachedWrapWidth > 1 ? cachedWrapWidth : GetWrapWidth(canvasText);

        return textLayoutManager.CreateTextLayout(canvasText, TextFormat, lineText, wrapWidth, layoutHeight);
    }

    public int GetWrappedRowCount(int lineIndex)
    {
        if (!IsWordWrapEnabled || lineIndex < 0 || lineIndex >= textManager.LinesCount)
            return 1;

        if (wrappedLineRowCountCache.TryGetValue(lineIndex, out int cachedRows))
            return cachedRows;

        return 1;
    }

    public int GetLineVisualStartRow(int lineIndex)
    {
        if (!IsWordWrapEnabled)
            return Math.Clamp(lineIndex, 0, Math.Max(0, textManager.LinesCount - 1));

        int cappedLine = Math.Clamp(lineIndex, 0, textManager.LinesCount);
        if (wrappedLineStartRowsCache.Count == textManager.LinesCount + 1)
            return wrappedLineStartRowsCache[cappedLine];

        int visualRow = 0;
        for (int i = 0; i < cappedLine; i++)
            visualRow += GetWrappedRowCount(i);
        return visualRow;
    }

    public int GetDocumentLineFromVisualRow(int visualRow)
    {
        if (!IsWordWrapEnabled)
            return Math.Clamp(visualRow, 0, Math.Max(0, textManager.LinesCount - 1));

        if (textManager.LinesCount == 0)
            return 0;

        visualRow = Math.Clamp(visualRow, 0, Math.Max(0, cachedTotalVisualRows - 1));

        if (wrappedLineStartRowsCache.Count == textManager.LinesCount + 1)
        {
            int low = 0;
            int high = textManager.LinesCount - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                int lineStart = wrappedLineStartRowsCache[mid];
                int nextLineStart = wrappedLineStartRowsCache[mid + 1];

                if (visualRow < lineStart)
                    high = mid - 1;
                else if (visualRow >= nextLineStart)
                    low = mid + 1;
                else
                    return mid;
            }

            return Math.Clamp(low, 0, textManager.LinesCount - 1);
        }

        int rowCursor = 0;
        for (int i = 0; i < textManager.LinesCount; i++)
        {
            int rows = GetWrappedRowCount(i);
            if (rowCursor + rows > visualRow)
                return i;
            rowCursor += rows;
        }
        return Math.Max(0, textManager.LinesCount - 1);
    }

    public int GetStartVisualRowFromScroll()
    {
        if (!IsWordWrapEnabled)
            return NumberOfStartLine;

        int visualRow = (int)Math.Floor((scrollManager.VerticalScroll * scrollManager.DefaultVerticalScrollSensitivity) / Math.Max(1, SingleLineHeight));
        return Math.Clamp(visualRow, 0, Math.Max(0, cachedTotalVisualRows - 1));
    }

    public int GetVisibleVisualRowCount(CanvasControl canvasText, int extraRows = 0)
    {
        return Math.Max(1, (int)Math.Ceiling(canvasText.ActualHeight / Math.Max(1, SingleLineHeight)) + extraRows);
    }

    public int GetRenderedVisualRowCount(int startLine, int lineCount)
    {
        if (!IsWordWrapEnabled)
            return lineCount;

        int endLine = Math.Clamp(startLine + lineCount, 0, textManager.LinesCount);
        return Math.Max(0, GetLineVisualStartRow(endLine) - GetLineVisualStartRow(startLine));
    }

    public int GetVisualRowFromPointY(double y)
    {
        return CalculateVisualRowFromPointY(y, StartVisualRow, SingleLineHeight, scrollManager.DefaultVerticalScrollSensitivity);
    }

    public int GetWrappedRowOffsetInLineFromPointY(int lineIndex, double y)
    {
        int lineStartRow = GetLineVisualStartRow(lineIndex);
        int rowOffset = GetVisualRowFromPointY(y) - lineStartRow;
        return Math.Clamp(rowOffset, 0, GetWrappedRowCount(lineIndex) - 1);
    }

    public float GetWrappedLineHitTestYFromPointY(int lineIndex, double y)
    {
        if (IsVirtualizedWrappedLine && lineIndex == NumberOfStartLine)
        {
            return CalculateWrappedLineHitTestYFromPointY(
                y,
                0,
                SingleLineHeight,
                scrollManager.DefaultVerticalScrollSensitivity,
                Math.Max(1, VirtualizedWrappedRowsToRender));
        }

        return CalculateWrappedLineHitTestYFromPointY(
            y,
            GetLineTopY(lineIndex),
            SingleLineHeight,
            scrollManager.DefaultVerticalScrollSensitivity,
            GetWrappedRowCount(lineIndex));
    }

    internal static int CalculateVisualRowFromPointY(double y, int startVisualRow, float singleLineHeight, int defaultVerticalScrollSensitivity)
    {
        double rowHeight = Math.Max(1, singleLineHeight);
        double topInset = rowHeight / Math.Max(1, defaultVerticalScrollSensitivity);
        int relativeRow = (int)Math.Floor(Math.Max(0, y - topInset) / rowHeight);
        return startVisualRow + relativeRow;
    }

    internal static float CalculateWrappedLineHitTestYFromPointY(double y, float lineTopY, float singleLineHeight, int defaultVerticalScrollSensitivity, int wrappedRowCount)
    {
        double rowHeight = Math.Max(1, singleLineHeight);
        double topInset = rowHeight / Math.Max(1, defaultVerticalScrollSensitivity);
        double maxHitTestY = Math.Max(0, wrappedRowCount * rowHeight - 0.001);
        return (float)Math.Clamp(y - lineTopY - topInset, 0, maxHitTestY);
    }

    public float GetLineTopY(int lineIndex)
    {
        if (!IsWordWrapEnabled)
            return (lineIndex - NumberOfStartLine) * SingleLineHeight;

        return (GetLineVisualStartRow(lineIndex) - StartVisualRow) * SingleLineHeight;
    }

    public int GetVisualRowForCursorPosition(CanvasControl canvasText, CursorPosition cursorPosition)
    {
        if (!IsWordWrapEnabled || cursorPosition == null || textManager.LinesCount == 0)
            return cursorPosition?.LineNumber ?? 0;

        EnsureWrapMetrics(canvasText);
        int lineIndex = Math.Clamp(cursorPosition.LineNumber, 0, textManager.LinesCount - 1);
        int characterPosition = Math.Clamp(cursorPosition.CharacterPosition, 0, textManager.GetLineLength(lineIndex));
        if (ShouldVirtualizeWrappedLine(lineIndex))
            return GetLineVisualStartRow(lineIndex) + characterPosition / EstimateWrappedCharsPerRow(canvasText);

        using CanvasTextLayout lineLayout = CreateWrappedLineTextLayout(canvasText, lineIndex, true);
        var caretPosition = lineLayout.GetCaretPosition(characterPosition, false);
        int rowOffset = (int)Math.Floor(Math.Max(0, caretPosition.Y) / Math.Max(1, SingleLineHeight));
        return GetLineVisualStartRow(lineIndex) + Math.Clamp(rowOffset, 0, GetWrappedRowCount(lineIndex) - 1);
    }

    public bool MoveCursorByVisualRows(CanvasControl canvasText, CursorPosition cursorPosition, int rowDelta)
    {
        if (!IsWordWrapEnabled || cursorPosition == null || textManager.LinesCount == 0)
            return false;

        EnsureWrapMetrics(canvasText);
        int lineIndex = Math.Clamp(cursorPosition.LineNumber, 0, textManager.LinesCount - 1);
        int characterPosition = Math.Clamp(cursorPosition.CharacterPosition, 0, textManager.GetLineLength(lineIndex));
        if (ShouldVirtualizeWrappedLine(lineIndex))
        {
            int charsPerRow = EstimateWrappedCharsPerRow(canvasText);
            int currentColumn = characterPosition % charsPerRow;
            int virtualCurrentVisualRow = GetLineVisualStartRow(lineIndex) + characterPosition / charsPerRow;
            int virtualTargetVisualRow = Math.Clamp(virtualCurrentVisualRow + rowDelta, 0, Math.Max(0, cachedTotalVisualRows - 1));
            int virtualTargetLine = GetDocumentLineFromVisualRow(virtualTargetVisualRow);
            int virtualTargetRowOffset = virtualTargetVisualRow - GetLineVisualStartRow(virtualTargetLine);
            int targetLength = textManager.GetLineLength(virtualTargetLine);
            int targetCharsPerRow = ShouldVirtualizeWrappedLine(virtualTargetLine)
                ? EstimateWrappedCharsPerRow(canvasText)
                : charsPerRow;

            cursorPosition.LineNumber = virtualTargetLine;
            cursorPosition.CharacterPosition = Math.Clamp(virtualTargetRowOffset * targetCharsPerRow + currentColumn, 0, targetLength);
            return true;
        }

        using CanvasTextLayout currentLayout = CreateWrappedLineTextLayout(canvasText, lineIndex, true);
        var currentCaret = currentLayout.GetCaretPosition(characterPosition, false);
        int currentVisualRow = GetLineVisualStartRow(lineIndex) + (int)Math.Floor(Math.Max(0, currentCaret.Y) / Math.Max(1, SingleLineHeight));
        int targetVisualRow = Math.Clamp(currentVisualRow + rowDelta, 0, Math.Max(0, cachedTotalVisualRows - 1));
        int targetLine = GetDocumentLineFromVisualRow(targetVisualRow);
        int targetRowOffset = targetVisualRow - GetLineVisualStartRow(targetLine);

        using CanvasTextLayout targetLayout = CreateWrappedLineTextLayout(canvasText, targetLine, true);
        targetLayout.HitTest(currentCaret.X, targetRowOffset * Math.Max(1, SingleLineHeight), out var targetRegion);

        cursorPosition.LineNumber = targetLine;
        cursorPosition.CharacterPosition = Math.Clamp(targetRegion.CharacterIndex, 0, textManager.GetLineLength(targetLine));
        return true;
    }

    public (int startLine, int linesToRender) CalculateLinesToRender(CanvasControl canvasText)
    {
        var singleLineHeight = SingleLineHeight;

        if (IsWordWrapEnabled)
            return CalculateWrappedLinesToRender(canvasText, singleLineHeight);

        //Measure text position and apply the value to the scrollbar
        scrollManager.verticalScrollBar.Maximum = ((textManager.LinesCount + 1) * singleLineHeight - scrollGrid.ActualHeight) / scrollManager.DefaultVerticalScrollSensitivity;
        scrollManager.verticalScrollBar.ViewportSize = coreTextbox.canvasText.ActualHeight;

        //Calculate number of lines that need to be rendered
        int linesToRenderCount = (int)(coreTextbox.canvasText.ActualHeight / singleLineHeight);
        linesToRenderCount = Math.Min(linesToRenderCount, textManager.LinesCount);

        int startLine = (int)((scrollManager.VerticalScroll * scrollManager.DefaultVerticalScrollSensitivity) / singleLineHeight);
        startLine = Math.Min(startLine, textManager.LinesCount);

        int linesToRender = Math.Min(linesToRenderCount, textManager.LinesCount - startLine);

        return (startLine, linesToRender);
    }

    public void UpdateRenderedLineRange(CanvasControl canvasText)
    {
        (NumberOfStartLine, NumberOfRenderedLines) = CalculateLinesToRender(canvasText);
    }

    private (int startLine, int linesToRender) CalculateWrappedLinesToRender(CanvasControl canvasText, float singleLineHeight)
    {
        EnsureWrapMetrics(canvasText);
        int totalVisualRows = cachedTotalVisualRows;

        scrollManager.verticalScrollBar.Maximum = Math.Max(0, (totalVisualRows * singleLineHeight - scrollGrid.ActualHeight) / scrollManager.DefaultVerticalScrollSensitivity);
        scrollManager.verticalScrollBar.ViewportSize = coreTextbox.canvasText.ActualHeight;

        StartVisualRow = GetStartVisualRowFromScroll();

        int startLine = GetDocumentLineFromVisualRow(StartVisualRow);
        int startLineVisualRow = GetLineVisualStartRow(startLine);
        WrappedStartRowOffset = Math.Max(0, StartVisualRow - startLineVisualRow);

        int visibleRows = GetVisibleVisualRowCount(canvasText, 2);
        int rowsToCover = visibleRows + WrappedStartRowOffset;
        int linesToRender = 0;
        for (int i = startLine; i < textManager.LinesCount && rowsToCover > 0; i++)
        {
            rowsToCover -= GetWrappedRowCount(i);
            linesToRender++;
        }

        return (startLine, Math.Max(0, linesToRender));
    }

    public void Draw(CanvasControl canvasText, CanvasDrawEventArgs args)
    {
        //Create resources and layouts:
        if (NeedsTextFormatUpdate || TextFormat == null || lineNumberRenderer.LineNumberTextFormat == null)
        {
            lineNumberRenderer.CreateLineNumberTextFormat();

            TextFormat?.Dispose();
            TextFormat = textLayoutManager.CreateCanvasTextFormat();

            invisibleCharactersRenderer.UpdateTextFormat(canvasText, TextFormat);

            designHelper.CreateColorResources(args.DrawingSession);
            NeedsTextFormatUpdate = false;
            InvalidateWrapMetrics();

            // Measure actual character width for horizontal virtualization offset calculation.
            using (var measureLayout = new CanvasTextLayout(args.DrawingSession, "M", TextFormat, 0, 0))
            {
                _cachedCharWidth = Math.Max(1, (float)measureLayout.DrawBounds.Width);
            }
        }

        UpdateRenderedLineRange(canvasText);
        ResetVirtualizedWrappedLineState();

        // Decide horizontal virtualization BEFORE materializing the visible text. Joining many very long
        // lines (megabytes) into one string every frame — then laying it out — is the actual scroll-stutter
        // cost on files like JSONL logs (hundreds of 50k+ char lines). When any visible line is very long we
        // build ONLY the sliced text (a few KB) instead of the full join, and record a per-line prefix so
        // the caret/selection can still map document positions into the sliced multi-line layout.
        int hSliceStart = 0, hSliceLen = 0;
        bool horizontallySlice = !IsWordWrapEnabled
            && ShouldHorizontallySlice(canvasText, out hSliceStart, out hSliceLen);
        LineSliceResult renderTextData;
        if (IsWordWrapEnabled && NumberOfRenderedLines == 1 && ShouldVirtualizeWrappedLine(NumberOfStartLine))
        {
            renderTextData = BuildVirtualizedWrappedLineRenderData(canvasText, NumberOfStartLine);
            RenderedText = renderTextData.Text;
            IsHorizontallyVirtualized = false;
            HorizontalSliceStart = 0;
            HorizontalSliceLength = 0;
            _hSliceVisibleStart = 0;
            _hSliceVisibleEnd = 0;
        }
        else if (horizontallySlice)
        {
            RenderedText = BuildHorizontallySlicedText(hSliceStart, hSliceLen);
            HorizontalSliceStart = hSliceStart;
            HorizontalSliceLength = hSliceLen;
            IsHorizontallyVirtualized = true;
            // Empty per-line span: syntax highlighting is skipped for a sliced layout (its char offsets
            // would shift), mirroring the wrapped-line virtualization path.
            renderTextData = new LineSliceResult(RenderedText, ReadOnlySpan<string>.Empty);
        }
        else
        {
            renderTextData = textManager.GetLinesForRendering(NumberOfStartLine, NumberOfRenderedLines);
            RenderedText = renderTextData.Text;
            IsHorizontallyVirtualized = false;
            HorizontalSliceStart = 0;
            HorizontalSliceLength = 0;
            _hSliceVisibleStart = 0;
            _hSliceVisibleEnd = 0;
        }

        float hSlicePixelOffset = IsHorizontallyVirtualized
            ? HorizontalSliceStart * (_cachedCharWidth > 0 ? _cachedCharWidth : Math.Max(1, zoomManager.ZoomedFontSize * 0.6f))
            : 0;
        DrawTextOffsetX = IsWordWrapEnabled ? 0 : (float)-scrollManager.HorizontalScroll + hSlicePixelOffset;
        DrawTextOffsetY = IsVirtualizedWrappedLine
            ? SingleLineHeight
            : (IsWordWrapEnabled ? SingleLineHeight - (WrappedStartRowOffset * SingleLineHeight) : SingleLineHeight);
        int renderedVisualRows = IsVirtualizedWrappedLine
            ? VirtualizedWrappedRowsToRender
            : GetRenderedVisualRowCount(NumberOfStartLine, NumberOfRenderedLines);
        var layoutSize = IsWordWrapEnabled
            ? new Size
            {
                Height = IsVirtualizedWrappedLine
                    ? Math.Max(canvasText.Size.Height + (VirtualizedWrappedLinePaddingRows * SingleLineHeight), (renderedVisualRows + 2) * SingleLineHeight)
                    : Math.Max(canvasText.Size.Height + (WrappedStartRowOffset + 2) * SingleLineHeight, (renderedVisualRows + 2) * SingleLineHeight),
                Width = GetWrapWidth(canvasText)
            }
            : new Size { Height = canvasText.Size.Height, Width = coreTextbox.ActualWidth };

        //check rendering and calculation updates
        lineNumberRenderer.CheckGenerateLineNumberText();

        using CanvasCommandList canvasCommandList = new CanvasCommandList(args.DrawingSession);
        if ((OldRenderedText != null && OldRenderedText.Length != RenderedText.Length)
            || !RenderedText.Equals(OldRenderedText, StringComparison.Ordinal)
            || NeedsUpdateTextLayout
        )
        {
            NeedsUpdateTextLayout = false;
            OldRenderedText = RenderedText;

            DrawnTextLayout = textLayoutManager.CreateTextResource(canvasText, DrawnTextLayout, TextFormat, RenderedText, layoutSize);
            SyntaxHighlightingRenderer.UpdateSyntaxHighlighting(renderTextData, textManager.NewLineCharacter, DrawnTextLayout, designHelper._AppTheme, textManager._SyntaxHighlighting, coreTextbox.EnableSyntaxHighlighting);
        }

        scrollManager.EnsureHorizontalScrollBounds(canvasText, longestLineManager, false, zoomManager.ZoomNeedsRecalculateLongestLine);
        if (zoomManager.ZoomNeedsRecalculateLongestLine)
            zoomManager.ZoomNeedsRecalculateLongestLine = false;

        if (linkHighlightManager.HighlightLinks)
        {
            linkHighlightManager.FindAndComputeLinkPositions();
            linkRenderer.HighlightLinks();
        }

        var clipRect = new Rect(0, 0, canvasText.Size.Width, canvasText.Size.Height);
        using (var ccls = canvasCommandList.CreateDrawingSession())
        {
            using (ccls.CreateLayer(1.0f, clipRect))
            {
                //Only update the textformat when the text changes:
                //render the search highlights
                if (searchManager.IsSearchOpen)
                {
                    string diagnosticsContext = TextControlBoxDiagnostics.IsVerboseEnabled && IsWordWrapEnabled
                        ? $"wordWrap={IsWordWrapEnabled}, startLine={NumberOfStartLine}, renderedLines={NumberOfRenderedLines}, startVisualRow={StartVisualRow}, wrappedStartRowOffset={WrappedStartRowOffset}, virtualWrapped={IsVirtualizedWrappedLine}, virtualSliceStart={VirtualizedLineSliceStart}, virtualRows={VirtualizedWrappedRowsToRender}, virtualCharsPerRow={VirtualizedLineCharsPerRow}, drawOffset=({DrawTextOffsetX:F1},{DrawTextOffsetY:F1}), layout=({layoutSize.Width:F1},{layoutSize.Height:F1}), canvas=({canvasText.Size.Width:F1},{canvasText.Size.Height:F1})"
                        : string.Empty;
                    SearchHighlightsRenderer.RenderHighlights(
                        args,
                        ccls,
                        DrawnTextLayout,
                        RenderedText,
                        searchManager.MatchingSearchLines,
                        searchManager.searchParameter.SearchExpression,
                        DrawTextOffsetX,
                        IsWordWrapEnabled ? DrawTextOffsetY - SingleLineHeight + (SingleLineHeight / scrollManager.DefaultVerticalScrollSensitivity) : SingleLineHeight / scrollManager.DefaultVerticalScrollSensitivity,
                        designHelper._Design.SearchHighlightColor,
                        coreTextbox.MaxSearchHighlightsPerRender,
                        logDiagnostics: IsWordWrapEnabled,
                        diagnosticsContext: diagnosticsContext,
                        virtualizedWrappedLine: IsVirtualizedWrappedLine,
                        virtualizedWrappedLineCharsPerRow: VirtualizedLineCharsPerRow,
                        virtualizedWrappedLineNewLineLength: textManager.NewLineCharacter.Length
                        );
                    }

                ccls.DrawTextLayout(DrawnTextLayout, DrawTextOffsetX, DrawTextOffsetY, designHelper.TextColorBrush);

                invisibleCharactersRenderer.DrawTabsAndSpaces(args, ccls, RenderedText, DrawnTextLayout, DrawTextOffsetY, HorizontalSlicePixelOffset);
            }
        }
        args.DrawingSession.DrawImage(canvasCommandList);

        //Only update if needed, to reduce updates when scrolling
        if (lineNumberRenderer.CanUpdateCanvas())
        {
            canvasUpdateManager.UpdateLineNumbers();
        }
    }
}
