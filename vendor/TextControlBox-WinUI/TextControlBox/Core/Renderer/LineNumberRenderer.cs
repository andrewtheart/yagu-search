using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Text;
using TextControlBoxNS.Core.Text;
using TextControlBoxNS.Helper;

namespace TextControlBoxNS.Core.Renderer
{
    internal class LineNumberRenderer
    {
        public CanvasTextLayout LineNumberTextLayout = null;
        public CanvasTextFormat LineNumberTextFormat = null;

        public string LineNumberTextToRender;
        public string OldLineNumberTextToRender;

        private readonly StringBuilder LineNumberContent = new StringBuilder();
        private bool needsUpdate = false;

        private TextManager textManager;
        private TextRenderer textRenderer;
        private DesignHelper designHelper;
        private LineNumberManager lineNumberManager;
        private TextLayoutManager textLayoutManager;

        public void Init(TextManager textManager, TextLayoutManager textLayoutManager, TextRenderer textRenderer, DesignHelper designHelper, LineNumberManager lineNumberManager)
        {
            this.textManager = textManager;
            this.textRenderer = textRenderer;
            this.designHelper = designHelper;
            this.lineNumberManager = lineNumberManager;
            this.textLayoutManager = textLayoutManager;
        }

        public void GenerateLineNumberText(int renderedLines, int startLine)
        {
            if (textRenderer.IsVirtualizedWrappedLine)
            {
                LineNumberContent.AppendLine(textRenderer.WrappedStartRowOffset == 0 ? (startLine + 1).ToString() : string.Empty);
                for (int row = 1; row < textRenderer.VirtualizedWrappedRowsToRender; row++)
                    LineNumberContent.AppendLine();

                LineNumberTextToRender = LineNumberContent.ToString();
                LineNumberContent.Clear();
                return;
            }

            //TODO! check performance:
            for (int i = 0; i < renderedLines; i++)
            {
                int lineIndex = i + startLine;
                LineNumberContent.AppendLine((lineIndex + 1).ToString());

                if (textRenderer.IsWordWrapEnabled)
                {
                    int continuationRows = textRenderer.GetWrappedRowCount(lineIndex) - 1;
                    for (int row = 0; row < continuationRows; row++)
                        LineNumberContent.AppendLine();
                }
            }
            LineNumberTextToRender = LineNumberContent.ToString();
            LineNumberContent.Clear();
        }

        public bool CanUpdateCanvas()
        {
            return needsUpdate || OldLineNumberTextToRender == null ||
                LineNumberTextToRender == null ||
                !OldLineNumberTextToRender.Equals(LineNumberTextToRender, StringComparison.OrdinalIgnoreCase);
        }

        public void NeedsUpdateLineNumbers()
        {
            this.needsUpdate = true;
        }

        public void HideLineNumbers(CanvasControl canvas, float spaceBetweenCanvasAndText)
        {
            canvas.Width = spaceBetweenCanvasAndText;
        }

        public void Draw(CanvasControl canvas, CanvasDrawEventArgs args, float spaceBetweenCanvasAndText)
        {
            if (LineNumberTextToRender == null || LineNumberTextToRender.Length == 0)
                return;

            float lineNumberWidth = (float)Utils.MeasureTextSize(args.DrawingSession.Device, (textManager.LinesCount).ToString(), LineNumberTextFormat).Width;
            canvas.Width = lineNumberWidth + 10 + spaceBetweenCanvasAndText;

            float posX = (float)canvas.Size.Width - spaceBetweenCanvasAndText;
            if (posX < 0) 
                posX = 0;

            OldLineNumberTextToRender = LineNumberTextToRender;

            LineNumberTextLayout?.Dispose();
            float textY = textRenderer.IsWordWrapEnabled
                ? (textRenderer.IsVirtualizedWrappedLine
                    ? textRenderer.SingleLineHeight
                    : textRenderer.SingleLineHeight - (textRenderer.WrappedStartRowOffset * textRenderer.SingleLineHeight))
                : textRenderer.SingleLineHeight;
            float layoutHeight = textRenderer.IsWordWrapEnabled
                ? (textRenderer.IsVirtualizedWrappedLine
                    ? (float)canvas.Size.Height + (2 * textRenderer.SingleLineHeight)
                    : (float)canvas.Size.Height + ((textRenderer.WrappedStartRowOffset + 2) * textRenderer.SingleLineHeight))
                : (float)canvas.Size.Height;
            LineNumberTextLayout = textLayoutManager.CreateTextLayout(canvas, LineNumberTextFormat, LineNumberTextToRender, posX, layoutHeight);

            args.DrawingSession.DrawTextLayout(
                LineNumberTextLayout,
                10,
                textY,
                designHelper.LineNumberColorBrush);
        }

        public void CreateLineNumberTextFormat()
        {
            if (lineNumberManager._ShowLineNumbers)
            {
                LineNumberTextFormat?.Dispose();
                LineNumberTextFormat = textLayoutManager.CreateLinenumberTextFormat();
            }
        }

        public void CheckDispose()
        {
            LineNumberTextLayout?.Dispose();
            LineNumberTextFormat?.Dispose();
        }

        public void CheckGenerateLineNumberText()
        {
            if (lineNumberManager._ShowLineNumbers)
            {
                GenerateLineNumberText(textRenderer.NumberOfRenderedLines, textRenderer.NumberOfStartLine);
            }
        }
    }
}
