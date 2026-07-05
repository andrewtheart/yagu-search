using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using TextControlBoxNS.Core.Selection;

namespace TextControlBoxNS.Core.Renderer;

internal class LineHighlighterRenderer
{
    private LineHighlighterManager lineHighlighterManager;
    private SelectionManager selectionManager;
    private TextRenderer textRenderer;
    public void Init(LineHighlighterManager lineHighlighterManager, SelectionManager selectionManager, TextRenderer textRenderer)
    {
        this.selectionManager = selectionManager;
        this.lineHighlighterManager = lineHighlighterManager;
        this.textRenderer = textRenderer;
    }

    public void Render(float canvasWidth, float y, float fontSize, CanvasDrawEventArgs args, CanvasSolidColorBrush backgroundBrush)
    {
        if (textRenderer.CurrentLineTextLayout == null)
            return;

        // Fill the full line slot (font size + inter-line spacing) so the current-line
        // highlight does not leave a gap below the text once line spacing is added.
        float lineHeight = textRenderer.SingleLineHeight;
        args.DrawingSession.FillRectangle(0, y, canvasWidth, lineHeight > fontSize ? lineHeight : fontSize, backgroundBrush);
    }

    public bool CanRender(FocusManager focusManager)
    {
        if(lineHighlighterManager._ShowLineHighlighter && !selectionManager.HasSelection)
        {
            return lineHighlighterManager._HighlightLineWhenNotFocused ? true : focusManager.HasFocus;
        }
        return false;
    }
}
