using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Text;
using System.Text.RegularExpressions;
using TextControlBoxNS.Helper;
using Windows.UI;

namespace TextControlBoxNS.Core.Renderer
{
    internal class SearchHighlightsRenderer
    {
        internal const int DefaultMaxSearchHighlightsPerRender = 1000;
        private static string s_lastDiagnosticsKey = string.Empty;
        private static long s_lastDiagnosticsTick;

        public static void RenderHighlights(
            CanvasDrawEventArgs args,
            CanvasDrawingSession drawingSession,
            CanvasTextLayout drawnTextLayout,
            string renderedText,
            int[] possibleLines,
            string searchRegex,
            float scrollOffsetX,
            float offsetTop,
            Color searchHighlightColor,
            int maxHighlights = DefaultMaxSearchHighlightsPerRender,
            bool logDiagnostics = false,
            string diagnosticsContext = "",
            bool virtualizedWrappedLine = false,
            int virtualizedWrappedLineCharsPerRow = 0,
            int virtualizedWrappedLineNewLineLength = 0)
        {
            if (!HasHighlightInput(renderedText, searchRegex, possibleLines) || drawnTextLayout == null)
                return;

            bool shouldLog = logDiagnostics && ShouldLogDiagnostics(diagnosticsContext, renderedText, searchRegex, scrollOffsetX, offsetTop);
            var sample = shouldLog ? new StringBuilder() : null;
            string searchText = virtualizedWrappedLine && virtualizedWrappedLineCharsPerRow > 0
                ? RemoveVirtualWrapBreaks(renderedText, virtualizedWrappedLineCharsPerRow, virtualizedWrappedLineNewLineLength)
                : renderedText;

            //draw the characters only to the drawingSession, which gets passed as a 
            //CanvasCommandList instance for efficient batched rendering 

            int renderedHighlights = 0;
            int totalMatches = 0;
            for (Match match = Regex.Match(searchText, searchRegex); match.Success; match = match.NextMatch())
            {
                if (match.Length <= 0)
                    continue;

                totalMatches++;
                int renderedIndex = match.Index;
                int renderedLength = match.Length;
                if (virtualizedWrappedLine && virtualizedWrappedLineCharsPerRow > 0)
                {
                    renderedIndex = GetVirtualizedRenderedStart(match.Index, virtualizedWrappedLineCharsPerRow, virtualizedWrappedLineNewLineLength);
                    int renderedEnd = GetVirtualizedRenderedEnd(match.Index + match.Length, virtualizedWrappedLineCharsPerRow, virtualizedWrappedLineNewLineLength);
                    renderedLength = Math.Max(0, renderedEnd - renderedIndex);
                }

                if (renderedIndex < 0 || renderedIndex >= renderedText.Length || renderedLength <= 0)
                    continue;

                if (renderedIndex + renderedLength > renderedText.Length)
                    renderedLength = renderedText.Length - renderedIndex;

                var layoutRegion = drawnTextLayout.GetCharacterRegions(renderedIndex, renderedLength);
                if (layoutRegion.Length > 0)
                {
                    if (sample is not null && renderedHighlights < 8)
                    {
                        var first = layoutRegion[0].LayoutBounds;
                        sample.Append($" match#{totalMatches} idx={renderedIndex} len={renderedLength} sourceIdx={match.Index} sourceLen={match.Length} regions={layoutRegion.Length} first=({first.X:F1},{first.Y:F1},{first.Width:F1},{first.Height:F1});");
                    }

                    for (int i = 0; i < layoutRegion.Length; i++)
                    {
                        drawingSession.FillRectangle(Utils.CreateRect(layoutRegion[i].LayoutBounds, scrollOffsetX, offsetTop), searchHighlightColor);
                    }
                    renderedHighlights++;
                    if (renderedHighlights >= maxHighlights)
                        break;
                }
            }

            if (shouldLog)
            {
                TextControlBoxDiagnostics.Verbose("TextControlBox.Highlight", $"RenderHighlights: renderedMatches={renderedHighlights}, totalScannedMatches={totalMatches}, renderedTextLen={renderedText.Length}, searchTextLen={searchText.Length}, possibleLines={possibleLines?.Length ?? 0}, regex={TextControlBoxDiagnostics.DescribeText(searchRegex)}, scrollOffsetX={scrollOffsetX:F1}, offsetTop={offsetTop:F1}, max={maxHighlights}, context={diagnosticsContext}, samples={sample}");
            }
            return;
        }

        private static string RemoveVirtualWrapBreaks(string renderedText, int charsPerRow, int newlineLength)
        {
            if (string.IsNullOrEmpty(renderedText) || charsPerRow <= 0 || newlineLength <= 0)
                return renderedText;

            var builder = new StringBuilder(renderedText.Length);
            int renderedIndex = 0;
            while (renderedIndex < renderedText.Length)
            {
                int rowLength = Math.Min(charsPerRow, renderedText.Length - renderedIndex);
                builder.Append(renderedText, renderedIndex, rowLength);
                renderedIndex += rowLength;
                if (renderedIndex < renderedText.Length)
                    renderedIndex = Math.Min(renderedText.Length, renderedIndex + newlineLength);
            }

            return builder.ToString();
        }

        private static int GetVirtualizedRenderedStart(int sourceIndex, int charsPerRow, int newlineLength)
        {
            int row = sourceIndex / charsPerRow;
            return sourceIndex + row * newlineLength;
        }

        private static int GetVirtualizedRenderedEnd(int sourceEndExclusive, int charsPerRow, int newlineLength)
        {
            if (sourceEndExclusive <= 0)
                return 0;

            int completedRowsBeforeEnd = (sourceEndExclusive - 1) / charsPerRow;
            return sourceEndExclusive + completedRowsBeforeEnd * newlineLength;
        }

        private static bool ShouldLogDiagnostics(string context, string renderedText, string searchRegex, float scrollOffsetX, float offsetTop)
        {
            if (!TextControlBoxDiagnostics.IsVerboseEnabled)
                return false;

            long now = Environment.TickCount64;
            string key = $"{context}|len={renderedText.Length}|regex={searchRegex}|x={scrollOffsetX:F1}|y={offsetTop:F1}";
            if (string.Equals(key, s_lastDiagnosticsKey, StringComparison.Ordinal) && now - s_lastDiagnosticsTick < 500)
                return false;

            s_lastDiagnosticsKey = key;
            s_lastDiagnosticsTick = now;
            return true;
        }

        internal static bool HasHighlightInput(string renderedText, string searchRegex, int[] possibleLines)
        {
            return !string.IsNullOrEmpty(searchRegex) && !string.IsNullOrEmpty(renderedText);
        }
    }
}
