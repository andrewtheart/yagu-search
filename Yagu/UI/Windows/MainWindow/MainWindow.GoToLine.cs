using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.System;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Ctrl+G "Go to line" support for the preview RichTextBlock surface and
/// the PreviewEditor TextControlBox.
/// </summary>
public sealed partial class MainWindow
{
    private bool _goToLineDialogOpen;

    private bool HasNavigablePreviewSurfaceForGoToLine()
    {
        if (PreviewPanelBorder.Visibility != Visibility.Visible) return false;
        if (PreviewScrollViewer.Visibility != Visibility.Visible) return false;
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
            return _sectionGutterBlocks.Count > 0;
        return PreviewBlock.Visibility == Visibility.Visible && PreviewBlock.Blocks.Count > 0;
    }

    private async Task ShowGoToLineDialogForEditorAsync()
    {
        if (_goToLineDialogOpen) return;
        if (PreviewEditor.Visibility != Visibility.Visible) return;

        int maxLine = Math.Max(1, PreviewEditor.NumberOfLines);
        int? entered = await ShowGoToLineDialogAsync(maxLine);
        if (entered is not int target) return;

        try
        {
            int zeroBased = Math.Clamp(target - 1, 0, Math.Max(0, PreviewEditor.NumberOfLines - 1));
            PreviewEditor.GoToLine(zeroBased);
            PreviewEditor.ScrollLineIntoView(zeroBased);
            PreviewEditor.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("GoToLine", "Editor GoToLine failed: " + ex.Message);
        }
    }

    private async Task ShowGoToLineDialogForPreviewAsync()
    {
        if (_goToLineDialogOpen) return;

        var block = ResolveGoToLinePreviewBlock();
        if (block is null)
        {
            LogService.Instance.Warning("GoToLine", "ResolveGoToLinePreviewBlock returned null");
            return;
        }

        int maxLine = ComputeMaxPreviewLineNumber(block);
        if (maxLine <= 0)
        {
            LogService.Instance.Warning("GoToLine", $"ComputeMaxPreviewLineNumber returned {maxLine}");
            return;
        }

        int? entered = await ShowGoToLineDialogAsync(maxLine);
        if (entered is not int target) return;

        var targetPara = FindParagraphForLine(block, target);
        if (targetPara is null)
        {
            LogService.Instance.Warning("GoToLine", $"FindParagraphForLine returned null for line {target} in block with {block.Blocks.Count} paragraphs");
            return;
        }

        LogService.Instance.Info("GoToLine", $"Scrolling to line {target}, block.Blocks.Count={block.Blocks.Count}, scrollableHeight={PreviewScrollViewer.ScrollableHeight:N1}, viewportHeight={PreviewScrollViewer.ViewportHeight:N1}, currentOffset={PreviewScrollViewer.VerticalOffset:N1}");

        try
        {
            ScrollPreviewToLine(block, targetPara, forceCenter: true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("GoToLine", "Preview ScrollPreviewToLine failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Chooses which RichTextBlock to operate on:
    /// - Single-file mode: <see cref="PreviewBlock"/>.
    /// - Multi-section mode: the section block containing the currently-active match,
    ///   else the first section block whose host expander is expanded,
    ///   else the first registered section block.
    /// </summary>
    private RichTextBlock? ResolveGoToLinePreviewBlock()
    {
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            if (_currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count)
            {
                var active = _matchParagraphs[_currentMatchIndex].block;
                if (_sectionGutterBlocks.ContainsKey(active))
                    return active;
            }

            foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
            {
                if (!expander.IsExpanded) continue;
                var rtb = FindFirstDescendantRichTextBlock(expander);
                if (rtb is not null && _sectionGutterBlocks.ContainsKey(rtb))
                    return rtb;
            }

            return _sectionGutterBlocks.Keys.FirstOrDefault();
        }

        if (PreviewBlock.Visibility == Visibility.Visible && PreviewBlock.Blocks.Count > 0)
            return PreviewBlock;

        return null;
    }

    private static RichTextBlock? FindFirstDescendantRichTextBlock(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RichTextBlock rtb) return rtb;
            var found = FindFirstDescendantRichTextBlock(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static int ComputeMaxPreviewLineNumber(RichTextBlock block)
    {
        int max = 0;
        foreach (var p in block.Blocks.OfType<Paragraph>())
        {
            if (TryGetPreviewParagraphLineNumber(p, out int n) && n > max)
                max = n;
        }
        return max;
    }

    /// <summary>
    /// Returns the first non-continuation paragraph whose line number equals <paramref name="line"/>.
    /// Falls back to the first paragraph with line &gt;= <paramref name="line"/> if exact match not found.
    /// </summary>
    private static Paragraph? FindParagraphForLine(RichTextBlock block, int line)
    {
        Paragraph? fallback = null;
        foreach (var p in block.Blocks.OfType<Paragraph>())
        {
            if (!TryGetPreviewParagraphLineNumber(p, out int n)) continue;
            if (s_paragraphIsContinuation.TryGetValue(p, out _)) continue;
            if (n == line) return p;
            if (n > line && fallback is null) fallback = p;
        }
        return fallback;
    }

    private async Task<int?> ShowGoToLineDialogAsync(int maxLine)
    {
        _goToLineDialogOpen = true;
        try
        {
            var numberBox = new NumberBox
            {
                Minimum = 1,
                Maximum = maxLine,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                SmallChange = 1,
                LargeChange = 10,
                Value = double.NaN,
                MinWidth = 160,
                AcceptsExpression = false,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            };

            var label = new TextBlock
            {
                Text = $"Go to line # (1 – {maxLine}):",
                Margin = new Thickness(0, 0, 0, 6),
            };

            var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
            panel.Children.Add(label);
            panel.Children.Add(numberBox);

            // Submit on Enter from inside the NumberBox.
            // We must NOT set args.Handled = true here because the NumberBox
            // needs to process the Enter key to commit its text to .Value.
            numberBox.Loaded += (_, _) =>
            {
                numberBox.Focus(FocusState.Programmatic);
            };

            _dialogEnterAccepted = false;
            var result = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Go to line",
                    Content = panel,
                    PrimaryButtonText = "Go",
                    CloseButtonText = "Cancel",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    Width = 440,
                    Height = 270,
                },
                dialog =>
                {
                    numberBox.KeyUp += (_, args) =>
                    {
                        if (args.Key == VirtualKey.Enter)
                        {
                            dialog.AcceptPrimary();
                            _dialogEnterAccepted = true;
                        }
                    };
                });
            bool accepted = result == YaguDialogResult.Primary || _dialogEnterAccepted;
            _dialogEnterAccepted = false;
            if (!accepted) return null;

            double value = numberBox.Value;
            if (double.IsNaN(value)) return null;
            int line = (int)Math.Round(value);
            if (line < 1) line = 1;
            if (line > maxLine) line = maxLine;
            return line;
        }
        finally
        {
            _goToLineDialogOpen = false;
        }
    }

    private bool _dialogEnterAccepted;
}
