namespace Yagu.Models;

/// <summary>
/// One entry in the Advanced Options tab column.
/// </summary>
/// <remarks>
/// The column is drag-reorderable, so tabs are bound as data items rather than declared as inline
/// <c>ListViewItem</c> containers: WinUI's built-in reorder only rewrites an <c>ItemsSource</c>
/// collection and silently does nothing for inline containers.
/// <para><see cref="Key"/> is the stable identity used to map a tab to its content pane and is what
/// gets persisted in <c>AppSettings.AdvancedOptionsTabOrder</c>. Never map a tab to its content by
/// list position, and never rename a key — that silently resets a user's saved order.</para>
/// </remarks>
public sealed class AdvancedOptionsTabItem(string key, string glyph, string label)
{
    /// <summary>Stable, persisted identifier (for example <c>"filters"</c>).</summary>
    public string Key { get; } = key;

    /// <summary>Segoe Fluent Icons glyph shown beside the label.</summary>
    public string Glyph { get; } = glyph;

    /// <summary>Display text for the tab.</summary>
    public string Label { get; } = label;

    /// <summary>Gives the list item a meaningful UI Automation name for accessibility and UI tests.</summary>
    public override string ToString() => Label;
}
