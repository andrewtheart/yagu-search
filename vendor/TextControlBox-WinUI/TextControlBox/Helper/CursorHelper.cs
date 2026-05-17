using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using TextControlBoxNS.Core;
using TextControlBoxNS.Core.Renderer;
using TextControlBoxNS.Core.Text;
using TextControlBoxNS.Extensions;
using Windows.Foundation;

namespace TextControlBoxNS.Helper;

internal class CursorHelper
{
    public static int GetCursorLineFromPoint(TextRenderer textRenderer, Point point)
    {
        if (textRenderer.IsWordWrapEnabled)
            return textRenderer.GetDocumentLineFromVisualRow(textRenderer.GetVisualRowFromPointY(point.Y));

        //Calculate the relative linenumber, where the pointer was pressed at
        // Subtract the same top inset used by the word-wrap path so the
        // click-to-line mapping aligns with where text is actually rendered.
        const int DefaultVerticalScrollSensitivity = 4;
        double topInset = textRenderer.SingleLineHeight / DefaultVerticalScrollSensitivity;
        double adjustedY = Math.Max(0, point.Y - topInset);
        int relativeLine = (int)Math.Floor(adjustedY / textRenderer.SingleLineHeight);

        return Math.Max(0, relativeLine + textRenderer.NumberOfStartLine);
    }
    public static int GetCharacterPositionFromPoint(CurrentLineManager currentLineManager, CanvasTextLayout textLayout, Point cursorPosition, float marginLeft, float y = 0)
    {
        if (currentLineManager.GetCurrentLineText() == null || textLayout == null)
            return 0;

        textLayout.HitTest(
            (float)cursorPosition.X - marginLeft, y,
            out var textLayoutRegion);
        return textLayoutRegion.CharacterIndex;
    }

    //Return the position in pixels of the cursor in the current line
    public static float GetCursorPositionInLine(CanvasTextLayout currentLineTextLayout, CursorPosition cursorPosition, float xOffset)
    {
        if (currentLineTextLayout == null)
            return 0;

        return currentLineTextLayout.GetCaretPosition(cursorPosition.CharacterPosition < 0 ? 0 : cursorPosition.CharacterPosition, false).X + xOffset;
    }

    public static void UpdateCursorPosFromPoint(CanvasControl canvasText, CurrentLineManager currentLineManager, TextRenderer textRenderer, ScrollManager scrollManager, Point point, CursorPosition cursorPos)
    {
        //Apply an offset to the cursorposition to make selection easier
        point.X += textRenderer.SingleLineHeight / scrollManager.DefaultVerticalScrollSensitivity;
        textRenderer.UpdateRenderedLineRange(canvasText);

        cursorPos.LineNumber = GetCursorLineFromPoint(textRenderer, point);
        cursorPos.LineNumber = Math.Clamp(cursorPos.LineNumber, 0, textRenderer.NumberOfStartLine + textRenderer.NumberOfRenderedLines - 1); //Clamp to visible? or total? GetCursorLineFromPoint handles relative logic, but we need to clamp to document bounds.

        //GetCursorLineFromPoint returns absolute line index.    
        textRenderer.UpdateCurrentLineTextLayout(canvasText);
        float y = textRenderer.IsWordWrapEnabled
            ? textRenderer.GetWrappedLineHitTestYFromPointY(cursorPos.LineNumber, point.Y)
            : 0;
        float marginLeft = textRenderer.IsWordWrapEnabled ? 0 : (float)-scrollManager.HorizontalScroll;
        int renderedCharacterPosition = GetCharacterPositionFromPoint(currentLineManager, textRenderer.CurrentLineTextLayout, point, marginLeft, y);
        cursorPos.CharacterPosition = textRenderer.GetDocumentCharacterIndexFromRenderedIndex(cursorPos.LineNumber, renderedCharacterPosition);
    }
}

