using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using TextControlBoxNS.Core.Renderer;
using TextControlBoxNS.Core.Text;
using TextControlBoxNS.Helper;

namespace TextControlBoxNS.Core;

internal class ScrollManager
{

    public double _HorizontalScrollSensitivity = 1;
    public double _VerticalScrollSensitivity = 1;
    public int DefaultVerticalScrollSensitivity = 4;
    public float OldHorizontalScrollValue = 0;

    public double VerticalScroll { get => verticalScrollBar.Value; set { verticalScrollBar.Value = value < 0 ? 0 : value; canvasHelper.UpdateAll(); } }
    public double HorizontalScroll { get => horizontalScrollBar.Value; set { horizontalScrollBar.Value = coreTextbox?.WordWrap == true ? 0 : value < 0 ? 0 : value; canvasHelper.UpdateAll(); } }

    public ScrollBar verticalScrollBar;
    public ScrollBar horizontalScrollBar;
    private CanvasUpdateManager canvasHelper;
    private TextRenderer textRenderer;
    private CursorManager cursorManager;
    private TextManager textManager;
    private CoreTextControlBox coreTextbox;
    private Grid scrollGrid;
    private ZoomManager zoomManager;
    public void Init(CoreTextControlBox coreTextbox, CanvasUpdateManager canvasHelper, TextManager textManager, TextRenderer textRenderer, CursorManager cursorManager, ZoomManager zoomManager, ScrollBar verticalScrollBar, ScrollBar horizontalScrollBar)
    {
        this.verticalScrollBar = coreTextbox.verticalScrollBar;
        this.horizontalScrollBar = coreTextbox.horizontalScrollBar;
        scrollGrid = coreTextbox.scrollGrid;
        this.canvasHelper = canvasHelper;
        this.textRenderer = textRenderer;
        this.cursorManager = cursorManager;
        this.textManager = textManager;
        this.coreTextbox = coreTextbox;
        this.zoomManager = zoomManager;
        verticalScrollBar.Loaded += VerticalScrollbar_Loaded;
        verticalScrollBar.Scroll += VerticalScrollBar_Scroll;
        horizontalScrollBar.Scroll += HorizontalScrollBar_Scroll;
    }

    internal void VerticalScrollbar_Loaded(object sender, RoutedEventArgs e)
    {
        verticalScrollBar.Maximum = ((textManager.LinesCount + 1) * textRenderer.SingleLineHeight - scrollGrid.ActualHeight) / DefaultVerticalScrollSensitivity;
        verticalScrollBar.ViewportSize = coreTextbox.ActualHeight;
    }
    internal void VerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        //only update when a line was scrolled
        int currentScrollRow = (int)(verticalScrollBar.Value / Math.Max(1, textRenderer.SingleLineHeight) * DefaultVerticalScrollSensitivity);
        int renderedStartRow = coreTextbox.WordWrap ? textRenderer.StartVisualRow : textRenderer.NumberOfStartLine;
        if (currentScrollRow != renderedStartRow)
        {
            canvasHelper.UpdateAll();
        }
    }

    internal void HorizontalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        canvasHelper.UpdateText();
        canvasHelper.UpdateSelection();
    }

    public void UpdateWhenScrolled()
    {
        canvasHelper.UpdateAll();
    }

    public void ScrollLineIntoViewIfOutside(int line, bool update = true)
    {
        if (textRenderer.OutOfRenderedArea(line))
            ScrollLineIntoView(line, update);
    }

    public void ScrollOneLineUp(bool update = true)
    {
        verticalScrollBar.Value -= textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        if(update)
            canvasHelper.UpdateAll();
    }
    public void ScrollOneLineDown(bool update = true)
    {
        verticalScrollBar.Value += textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        if(update)
            canvasHelper.UpdateAll();
    }

    public void ScrollLineIntoView(int line, bool update = true)
    {
        if (coreTextbox.WordWrap)
        {
            textRenderer.EnsureWrapMetrics(coreTextbox.canvasText);
            int lineStartRow = textRenderer.GetLineVisualStartRow(line);
            int lineRows = textRenderer.GetWrappedRowCount(line);
            int visibleRows = textRenderer.GetVisibleVisualRowCount(coreTextbox.canvasText);
            int targetRow = Math.Max(0, lineStartRow - Math.Max(0, (visibleRows - lineRows) / 2));
            verticalScrollBar.Value = targetRow * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        else
        {
            verticalScrollBar.Value = (line - textRenderer.NumberOfRenderedLines / 2) * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        
        if(update)
            canvasHelper.UpdateAll();
    }

    public void ScrollTopIntoView(bool update = true)
    {
        if (coreTextbox.WordWrap)
        {
            textRenderer.EnsureWrapMetrics(coreTextbox.canvasText);
            int targetRow = Math.Max(0, textRenderer.GetLineVisualStartRow(cursorManager.LineNumber) - 1);
            verticalScrollBar.Value = targetRow * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        else
        {
            verticalScrollBar.Value = (cursorManager.LineNumber - 1) * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        if(update)
            canvasHelper.UpdateAll();
    }
    public void ScrollBottomIntoView(bool update = true)
    {
        if (coreTextbox.WordWrap)
        {
            textRenderer.EnsureWrapMetrics(coreTextbox.canvasText);
            int cursorRow = textRenderer.GetVisualRowForCursorPosition(coreTextbox.canvasText, cursorManager.currentCursorPosition);
            int visibleRows = textRenderer.GetVisibleVisualRowCount(coreTextbox.canvasText);
            int targetRow = Math.Max(0, cursorRow - visibleRows + 1);
            verticalScrollBar.Value = targetRow * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        else
        {
            verticalScrollBar.Value = (cursorManager.LineNumber - textRenderer.NumberOfRenderedLines + 1) * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        if(update)
            canvasHelper.UpdateAll();
    }

    public void ScrollPageUp()
    {
        if (coreTextbox.WordWrap)
        {
            int visibleRows = textRenderer.GetVisibleVisualRowCount(coreTextbox.canvasText);
            textRenderer.MoveCursorByVisualRows(coreTextbox.canvasText, cursorManager.currentCursorPosition, -visibleRows);
            verticalScrollBar.Value -= visibleRows * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        else
        {
            cursorManager.LineNumber -= textRenderer.NumberOfRenderedLines;
            if (cursorManager.LineNumber < 0)
                cursorManager.LineNumber = 0;

            verticalScrollBar.Value -= textRenderer.NumberOfRenderedLines * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        canvasHelper.UpdateAll();
    }


    public void ScrollPageDown()
    {
        if (coreTextbox.WordWrap)
        {
            int visibleRows = textRenderer.GetVisibleVisualRowCount(coreTextbox.canvasText);
            textRenderer.MoveCursorByVisualRows(coreTextbox.canvasText, cursorManager.currentCursorPosition, visibleRows);
            verticalScrollBar.Value += visibleRows * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        else
        {
            cursorManager.LineNumber += textRenderer.NumberOfRenderedLines;
            if (cursorManager.LineNumber > textManager.LinesCount - 1)
                cursorManager.LineNumber = textManager.LinesCount - 1;
            verticalScrollBar.Value += textRenderer.NumberOfRenderedLines * textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        canvasHelper.UpdateAll();
    }

    public void UpdateScrollToShowCursor(bool update = true)
    {
        if (coreTextbox.WordWrap)
        {
            textRenderer.EnsureWrapMetrics(coreTextbox.canvasText);
            int cursorVisualRow = textRenderer.GetVisualRowForCursorPosition(coreTextbox.canvasText, cursorManager.currentCursorPosition);
            int startVisualRow = textRenderer.GetStartVisualRowFromScroll();
            int visibleRows = textRenderer.GetVisibleVisualRowCount(coreTextbox.canvasText);

            if (cursorVisualRow >= startVisualRow + visibleRows)
            {
                verticalScrollBar.Value =
                    (cursorVisualRow - visibleRows + 2) *
                    textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
            }
            else if (cursorVisualRow < startVisualRow)
            {
                verticalScrollBar.Value =
                    cursorVisualRow *
                    textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
            }

            if (update)
                canvasHelper.UpdateAll();
            return;
        }

        if (textRenderer.NumberOfStartLine + textRenderer.NumberOfRenderedLines - 1 <= cursorManager.LineNumber)
        {
            verticalScrollBar.Value =
                (cursorManager.LineNumber - textRenderer.NumberOfRenderedLines + 2) *
                textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }
        else if (textRenderer.NumberOfStartLine > cursorManager.LineNumber)
        {
            verticalScrollBar.Value =
                cursorManager.LineNumber *
                textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        }

        if (update)
            canvasHelper.UpdateAll();
    }

    public bool ScrollIntoViewHorizontal(CanvasControl canvasText, bool update = true)
    {
        if (coreTextbox.WordWrap)
        {
            if (horizontalScrollBar.Value != 0)
                horizontalScrollBar.Value = 0;
            OldHorizontalScrollValue = 0;
            return false;
        }

        int charPosForLayout = cursorManager.currentCursorPosition.CharacterPosition;
        if (textRenderer.IsHorizontallyVirtualized)
            charPosForLayout -= textRenderer.HorizontalSliceStart;

        float curPosInLine;
        if (textRenderer.IsHorizontallyVirtualized && (charPosForLayout < 0 || textRenderer.CurrentLineTextLayout == null))
        {
            // Cursor is before the slice or layout unavailable; estimate absolute position
            curPosInLine = cursorManager.currentCursorPosition.CharacterPosition * textRenderer.CachedCharWidth;
        }
        else if (textRenderer.IsHorizontallyVirtualized && charPosForLayout > textRenderer.RenderedText.Length)
        {
            // Cursor is beyond the slice; estimate absolute position
            curPosInLine = cursorManager.currentCursorPosition.CharacterPosition * textRenderer.CachedCharWidth;
        }
        else
        {
            curPosInLine = CursorHelper.GetCursorPositionInLine(
                textRenderer.CurrentLineTextLayout,
                new CursorPosition(charPosForLayout, cursorManager.currentCursorPosition.LineNumber),
                textRenderer.HorizontalSlicePixelOffset
            );
        }

        if (curPosInLine == OldHorizontalScrollValue)
            return false;

        double visibleStart = horizontalScrollBar.Value;
        double visibleEnd = visibleStart + canvasText.ActualWidth;

        bool changed = false;
        if (curPosInLine < visibleStart + 3)
        {
            changed = true;
            horizontalScrollBar.Value = Math.Max(curPosInLine - 3, horizontalScrollBar.Minimum);
        }
        else if (curPosInLine > visibleEnd)
        {
            changed = true;
            horizontalScrollBar.Value = Math.Min(curPosInLine - canvasText.ActualWidth + 5, horizontalScrollBar.Maximum + 5);
        }

        OldHorizontalScrollValue = curPosInLine;

        if (update)
            canvasHelper.UpdateAll();

        return changed;
    }

    public void EnsureHorizontalScrollBounds(CanvasControl canvasText, LongestLineManager longestLineManager, bool triggeredByCursor, bool forceRecalculateLongestLine = false)
    {
        if (coreTextbox.WordWrap)
        {
            horizontalScrollBar.ViewportSize = canvasText.ActualWidth;
            horizontalScrollBar.Maximum = 0;
            if (horizontalScrollBar.Value != 0)
                horizontalScrollBar.Value = 0;
            return;
        }

        longestLineManager.CheckRecalculateLongestLine(forceRecalculateLongestLine);

        //Apply longest width to scrollbar
        horizontalScrollBar.ViewportSize = canvasText.ActualWidth;
        horizontalScrollBar.Maximum = (longestLineManager.longestLineWidth.Width <= canvasText.ActualWidth ? 0 : longestLineManager.longestLineWidth.Width - canvasText.ActualWidth + (zoomManager.ZoomedFontSize / 2));

        if(ScrollIntoViewHorizontal(canvasText, false))
        {
            if (triggeredByCursor)
                canvasHelper.UpdateText();
            else
                canvasHelper.UpdateCursor();
        }
    }
}
