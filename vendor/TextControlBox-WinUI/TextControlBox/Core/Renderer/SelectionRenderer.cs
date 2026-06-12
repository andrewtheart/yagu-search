using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using TextControlBoxNS.Core.Selection;
using TextControlBoxNS.Core.Text;
using TextControlBoxNS.Helper;
using Windows.Foundation;
using Windows.UI;

namespace TextControlBoxNS.Core.Renderer
{
    internal class SelectionRenderer
    {
        public int renderedSelectionLength = 0;
        public int renderedSelectionStart = 0;

        private SelectionManager selectionManager;
        private TextRenderer textRenderer;
        private EventsManager eventsManager;
        private ScrollManager scrollManager;
        private ZoomManager zoomManager;
        private DesignHelper designHelper;
        private TextManager textManager;
        private const byte MaximumReadableOverlayAlpha = 110;
        private const byte ActiveSearchSelectionOverlayAlpha = 145;
        private const float ActiveSearchSelectionBorderWidth = 2f;
        private string _lastSelectionDiagnosticsKey = string.Empty;
        private long _lastSelectionDiagnosticsTick;

        internal static Color GetReadableOverlayColor(Color color)
        {
            return color.A <= MaximumReadableOverlayAlpha
                ? color
                : Color.FromArgb(MaximumReadableOverlayAlpha, color.R, color.G, color.B);
        }

        public void Init(
            SelectionManager selectionManager,
            TextRenderer textRenderer,
            EventsManager eventsManager,
            ScrollManager scrollManager,
            ZoomManager zoomManager,
            DesignHelper designHelper,
            TextManager textManager
            )
        {
            this.selectionManager = selectionManager;
            this.textRenderer = textRenderer;
            this.eventsManager = eventsManager;
            this.scrollManager = scrollManager;
            this.zoomManager = zoomManager;
            this.designHelper = designHelper;
            this.textManager = textManager;
        }

        public void DrawSelection(
            CanvasTextLayout textLayout,
            CanvasDrawEventArgs args,
            float marginLeft,
            float marginTop,
            int unrenderedLinesToRenderStart,
            int numberOfRenderedLines,
            float fontSize,
            Color selectionColor
            )
        {
            int selStartIndex = 0;
            int selEndIndex = 0;
            int characterPosStart = selectionManager.selectionStart.CharacterPosition;
            int characterPosEnd = selectionManager.selectionEnd.CharacterPosition;
            int startLine = selectionManager.selectionStart.LineNumber;
            int endLine = selectionManager.selectionEnd.LineNumber;

            int lineEndingLength = textManager.NewLineCharacter.Length;

            if (textManager.totalLines.Count == 0)
                return;

            if (startLine >= textManager.totalLines.Count)
                startLine = textManager.totalLines.Count - 1;
            if (startLine < 0)
                startLine = 0;

            if (endLine >= textManager.totalLines.Count)
                endLine = textManager.totalLines.Count - 1;
            if (endLine < 0)
                endLine = 0;

            if (characterPosStart > textManager.totalLines.Span[startLine].Length)
                characterPosStart = textManager.totalLines.Span[startLine].Length;

            if (characterPosEnd > textManager.totalLines.Span[endLine].Length)
                characterPosEnd = textManager.totalLines.Span[endLine].Length;

            //Render the selection on position 0 if the user scrolled the start away
            if (startLine < unrenderedLinesToRenderStart)
            {
                startLine = unrenderedLinesToRenderStart;
                characterPosStart = 0;
            }

            if (endLine < unrenderedLinesToRenderStart)
            {
                endLine = unrenderedLinesToRenderStart;
                characterPosEnd = 0;
            }

            // If start is beyond visible area, clamp it to the end of the visible region
            int lastRenderedLine = Math.Min(
                unrenderedLinesToRenderStart + numberOfRenderedLines - 1,
                textManager.totalLines.Count - 1);
            if (lastRenderedLine < 0)
                return;

            if (startLine > lastRenderedLine)
            {
                startLine = lastRenderedLine;
                characterPosStart = textManager.totalLines.Span[startLine].Length;
            }

            if (endLine > lastRenderedLine)
            {
                endLine = lastRenderedLine;
                characterPosEnd = textManager.totalLines.Span[endLine].Length;
            }

            if (textRenderer.IsVirtualizedWrappedLine)
            {
                if (startLine != textRenderer.NumberOfStartLine || endLine != textRenderer.NumberOfStartLine)
                {
                    selectionManager.currentTextSelection.renderedIndex = 0;
                    selectionManager.currentTextSelection.renderedLength = 0;
                    LogWordWrapSelectionDiagnostics("virtualized selection outside rendered line", startLine, characterPosStart, endLine, characterPosEnd, 0, 0, 0, marginLeft, marginTop);
                    return;
                }

                selStartIndex = textRenderer.GetRenderedCharacterIndexForDocumentCharacter(startLine, characterPosStart);
                selEndIndex = textRenderer.GetRenderedCharacterIndexForDocumentCharacter(endLine, characterPosEnd);
                if (selStartIndex < 0 || selEndIndex < 0)
                {
                    selectionManager.currentTextSelection.renderedIndex = 0;
                    selectionManager.currentTextSelection.renderedLength = 0;
                    LogWordWrapSelectionDiagnostics("virtualized selection outside rendered slice", startLine, characterPosStart, endLine, characterPosEnd, selStartIndex, selEndIndex, 0, marginLeft, marginTop);
                    return;
                }
            }
            else if (startLine == endLine)
            {
                int lenghtToLine = 0;
                for (int i = 0; i < startLine - unrenderedLinesToRenderStart; i++)
                {
                    if (i < numberOfRenderedLines)
                    {
                        lenghtToLine += textManager.totalLines.Span[textRenderer.NumberOfStartLine + i].Length + lineEndingLength;
                    }
                }

                selStartIndex = characterPosStart + lenghtToLine;
                selEndIndex = characterPosEnd + lenghtToLine;

                // Adjust for horizontal virtualization slice offset
                if (textRenderer.IsHorizontallyVirtualized)
                {
                    selStartIndex -= textRenderer.HorizontalSliceStart;
                    selEndIndex -= textRenderer.HorizontalSliceStart;
                }
            }
            else
            {
                for (int i = 0; i < startLine - unrenderedLinesToRenderStart; i++)
                {
                    if (i >= numberOfRenderedLines) //Out of range of the List (do nothing)
                        break;
                    selStartIndex += textManager.totalLines.Span[textRenderer.NumberOfStartLine + i].Length + lineEndingLength;
                }

                selStartIndex += characterPosStart;

                for (int i = 0; i < endLine - unrenderedLinesToRenderStart; i++)
                {
                    if (i >= numberOfRenderedLines) //Out of range of the List (do nothing)
                        break;

                    selEndIndex += textManager.totalLines.Span[textRenderer.NumberOfStartLine + i].Length + lineEndingLength;
                }

                selEndIndex += characterPosEnd;
            }

            renderedSelectionStart = Math.Max(0, Math.Min(selStartIndex, selEndIndex));

            renderedSelectionLength = selEndIndex > selStartIndex ?
                selEndIndex - selStartIndex :
                selStartIndex - selEndIndex;

            // Clamp to slice bounds when horizontally virtualized
            if (textRenderer.IsHorizontallyVirtualized && textLayout != null)
            {
                int layoutLen = textRenderer.RenderedText.Length;
                if (renderedSelectionStart >= layoutLen)
                {
                    selectionManager.currentTextSelection.renderedIndex = 0;
                    selectionManager.currentTextSelection.renderedLength = 0;
                    LogWordWrapSelectionDiagnostics("horizontal selection outside rendered slice", startLine, characterPosStart, endLine, characterPosEnd, renderedSelectionStart, renderedSelectionLength, 0, marginLeft, marginTop);
                    return;
                }
                if (renderedSelectionStart + renderedSelectionLength > layoutLen)
                    renderedSelectionLength = layoutLen - renderedSelectionStart;
            }

            //no selection can be rendered. 
            //GetCharacterRegions(0,0) still returns a "ghost" region, so stop rendering here
            if(renderedSelectionLength == 0)
            {
                selectionManager.currentTextSelection.renderedIndex = 0;
                selectionManager.currentTextSelection.renderedLength = 0;
                LogWordWrapSelectionDiagnostics("zero rendered selection", startLine, characterPosStart, endLine, characterPosEnd, renderedSelectionStart, renderedSelectionLength, 0, marginLeft, marginTop);
                return;
            }

            CanvasTextLayoutRegion[] regions = textLayout.GetCharacterRegions(renderedSelectionStart, renderedSelectionLength);
            LogWordWrapSelectionDiagnostics("render selection", startLine, characterPosStart, endLine, characterPosEnd, renderedSelectionStart, renderedSelectionLength, regions.Length, marginLeft, marginTop, regions.Length > 0 ? regions[0].LayoutBounds : null);

            using CanvasCommandList canvasCommandList = new CanvasCommandList(args.DrawingSession);
            using (var ccls = canvasCommandList.CreateDrawingSession())
            {
                bool isActiveSearchMatch = selectionManager.CurrentSelectionIsActiveSearchMatch;
                Color readableSelectionColor = isActiveSearchMatch
                    ? GetActiveSearchSelectionOverlayColor(selectionColor)
                    : GetReadableOverlayColor(selectionColor);
                Color activeSearchSelectionBorderColor = GetActiveSearchSelectionBorderColor(selectionColor);
                float width = fontSize / scrollManager.DefaultVerticalScrollSensitivity;
                for (int i = 0; i < regions.Length; i++)
                {
                    //Change the width if selection in an empty line or starts at a line end
                    if (regions[i].LayoutBounds.Width == 0)
                    {
                        var bounds = regions[i].LayoutBounds;
                        regions[i].LayoutBounds = new Rect
                        {
                            Width = width,
                            Height = bounds.Height,
                            X = bounds.X,
                            Y = bounds.Y
                        };
                    }

                    Rect selectionRect = Utils.CreateRect(regions[i].LayoutBounds, marginLeft, marginTop);
                    ccls.FillRectangle(selectionRect, readableSelectionColor);
                    if (isActiveSearchMatch)
                    {
                        ccls.DrawRectangle(selectionRect, activeSearchSelectionBorderColor, ActiveSearchSelectionBorderWidth);
                    }
                }
            }
            args.DrawingSession.DrawImage(canvasCommandList);

            selectionManager.currentTextSelection.renderedIndex = renderedSelectionStart;
            selectionManager.currentTextSelection.renderedLength = renderedSelectionLength;
        }

        private static Color GetActiveSearchSelectionOverlayColor(Color selectionColor)
        {
            return Color.FromArgb(ActiveSearchSelectionOverlayAlpha, selectionColor.R, selectionColor.G, selectionColor.B);
        }

        private static Color GetActiveSearchSelectionBorderColor(Color selectionColor)
        {
            byte green = selectionColor.G < 160 ? (byte)160 : selectionColor.G;
            return Color.FromArgb(255, selectionColor.R, green, selectionColor.B);
        }

        private void LogWordWrapSelectionDiagnostics(
            string reason,
            int startLine,
            int startChar,
            int endLine,
            int endChar,
            int renderedStart,
            int renderedLength,
            int regionCount,
            float marginLeft,
            float marginTop,
            Rect? firstRegion = null)
        {
            if (!textRenderer.IsWordWrapEnabled || !TextControlBoxDiagnostics.IsVerboseEnabled)
                return;

            long now = Environment.TickCount64;
            string key = $"{reason}|{startLine}:{startChar}-{endLine}:{endChar}|{renderedStart}+{renderedLength}|regions={regionCount}|line={textRenderer.NumberOfStartLine}|rows={textRenderer.StartVisualRow}";
            if (string.Equals(key, _lastSelectionDiagnosticsKey, StringComparison.Ordinal) && now - _lastSelectionDiagnosticsTick < 500)
                return;

            _lastSelectionDiagnosticsKey = key;
            _lastSelectionDiagnosticsTick = now;

            string first = firstRegion is { } rect
                ? $" first=({rect.X:F1},{rect.Y:F1},{rect.Width:F1},{rect.Height:F1})"
                : string.Empty;
            TextControlBoxDiagnostics.Verbose("TextControlBox.SelectionHighlight", $"{reason}: doc={startLine}:{startChar}-{endLine}:{endChar}, rendered={renderedStart}+{renderedLength}, regions={regionCount},{first} margin=({marginLeft:F1},{marginTop:F1}), startLine={textRenderer.NumberOfStartLine}, renderedLines={textRenderer.NumberOfRenderedLines}, startVisualRow={textRenderer.StartVisualRow}, wrappedStartRowOffset={textRenderer.WrappedStartRowOffset}, virtualWrapped={textRenderer.IsVirtualizedWrappedLine}, virtualSliceStart={textRenderer.VirtualizedLineSliceStart}, renderedTextLen={textRenderer.RenderedText?.Length ?? 0}");
        }


        public void Draw(CanvasControl canvasSelection, CanvasDrawEventArgs args)
        {
            if (!selectionManager.selectionStart.IsNull && !selectionManager.selectionEnd.IsNull)
                selectionManager.HasSelection = SelectionHelper.TextIsSelected(selectionManager.selectionStart, selectionManager.selectionEnd);
            else
                selectionManager.HasSelection = false;

            if (selectionManager.HasSelection)
            {
                DrawSelection(
                    textRenderer.DrawnTextLayout,
                    args,
                    textRenderer.IsWordWrapEnabled ? 0 : textRenderer.HorizontalOffset,
                    GetSelectionTopMargin(),
                    textRenderer.NumberOfStartLine,
                    textRenderer.NumberOfRenderedLines,
                    zoomManager.ZoomedFontSize,
                    designHelper._Design.SelectionColor
                );
            }

            if (selectionManager.HasSelection && !selectionManager.Equals(selectionManager.OldTextSelection, selectionManager.currentTextSelection))
            {
                //Update the variables
                selectionManager.OldTextSelection.EndPosition.SetChangeValues(selectionManager.currentTextSelection.EndPosition);
                selectionManager.OldTextSelection.StartPosition.SetChangeValues(selectionManager.currentTextSelection.StartPosition);
                eventsManager.CallSelectionChanged();
            }
        }

        private float GetSelectionTopMargin()
        {
            float topInset = textRenderer.SingleLineHeight / scrollManager.DefaultVerticalScrollSensitivity;
            if (!textRenderer.IsWordWrapEnabled)
                return topInset;

            return textRenderer.IsVirtualizedWrappedLine
                ? topInset
                : topInset - (textRenderer.WrappedStartRowOffset * textRenderer.SingleLineHeight);
        }
    }
}