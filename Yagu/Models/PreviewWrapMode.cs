namespace Yagu.Models;

/// <summary>
/// Controls how long lines are displayed in the preview pane.
/// </summary>
public enum PreviewWrapMode
{
    /// <summary>Full word wrap — lines wrap at the container width.</summary>
    Wrap = 0,

    /// <summary>Partial wrap — no XAML word wrap but long lines are segmented into 4096-char paragraphs for layout performance.</summary>
    PartialWrap = 1,

    /// <summary>No wrap — lines are never broken regardless of length. Horizontal scrolling is required for long lines.</summary>
    NoWrap = 2,
}
