using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Yagu.Models;

/// <summary>
/// Per-section (per-file) match navigation state for the preview pane.
/// Tracks the matches inside a single <see cref="RichTextBlock"/>, the
/// containing <see cref="ScrollViewer"/>, and the currently-active match.
/// </summary>
internal sealed class SectionMatchNav
{
    public List<(Paragraph para, int matchInPara)> Matches { get; } = new();

    /// <summary>
    /// Lazy O(1) lookup from (paragraph, matchInPara) → index in <see cref="Matches"/>.
    /// Populated on first lookup; invalidated by setting to <c>null</c> when
    /// <see cref="Matches"/> mutates.
    /// </summary>
    public Dictionary<(Paragraph, int), int>? IndexByMatch { get; set; }

    public int CurrentIndex { get; set; } = -1;
    public ScrollViewer Scroller { get; set; } = null!;
    public RichTextBlock Block { get; set; } = null!;
}
