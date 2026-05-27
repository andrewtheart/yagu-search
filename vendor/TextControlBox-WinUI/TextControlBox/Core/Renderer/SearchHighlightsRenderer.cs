using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System.Text.RegularExpressions;
using TextControlBoxNS.Helper;
using Windows.UI;

namespace TextControlBoxNS.Core.Renderer
{
    internal class SearchHighlightsRenderer
    {
        internal const int DefaultMaxSearchHighlightsPerRender = 1000;

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
            int maxHighlights = DefaultMaxSearchHighlightsPerRender)
        {
            if (!HasHighlightInput(renderedText, searchRegex, possibleLines) || drawnTextLayout == null)
                return;

            //draw the characters only to the drawingSession, which gets passed as a 
            //CanvasCommandList instance for efficient batched rendering 

            int renderedHighlights = 0;
            for (Match match = Regex.Match(renderedText, searchRegex); match.Success; match = match.NextMatch())
            {
                if (match.Length <= 0)
                    continue;

                var layoutRegion = drawnTextLayout.GetCharacterRegions(match.Index, match.Length);
                if (layoutRegion.Length > 0)
                {
                    drawingSession.FillRectangle(Utils.CreateRect(layoutRegion[0].LayoutBounds, scrollOffsetX, offsetTop), searchHighlightColor);
                    renderedHighlights++;
                    if (renderedHighlights >= maxHighlights)
                        break;
                }
            }
            return;
        }

        internal static bool HasHighlightInput(string renderedText, string searchRegex, int[] possibleLines)
        {
            return !string.IsNullOrEmpty(searchRegex) && !string.IsNullOrEmpty(renderedText);
        }
    }
}
