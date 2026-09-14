using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;

namespace Yagu;

/// <summary>
/// Syntax coloring for the preview pane. Classifies each rendered preview line with
/// <see cref="PreviewSyntaxHighlighter"/> (same language table as the built-in editor) and
/// splits the non-match text of a line into colored <see cref="Run"/>s.
/// <para>
/// Search-match runs are never recolored here — match highlighting always wins, so a match
/// stays bold and in the configured match color even when it lands inside a string or comment.
/// </para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Lines longer than this are rendered without syntax coloring (bounded per-line cost).</summary>
    private const int MaxPreviewSyntaxLineLength = 4000;

    /// <summary>
    /// Ceiling on extra <see cref="Run"/>s syntax coloring may add to one preview build. A preview can
    /// emit thousands of paragraphs, and every additional Run costs XAML text layout, so coloring
    /// degrades to plain runs once the budget is spent instead of slowing the pane down.
    /// </summary>
    private const int PreviewSyntaxRunBudget = 20_000;

    private readonly Dictionary<(PreviewTokenKind Kind, bool Dimmed), SolidColorBrush> _previewSyntaxBrushes = new();
    private bool _previewSyntaxBrushesAreLightTheme;
    private string? _previewSyntaxLanguagePath;
    private EditorSyntaxLanguage? _previewSyntaxLanguage;
    private int _previewSyntaxRunsRemaining = PreviewSyntaxRunBudget;

    /// <summary>Resets the per-build syntax-run budget. Called when a preview build starts.</summary>
    private void ResetPreviewSyntaxBudget() => _previewSyntaxRunsRemaining = PreviewSyntaxRunBudget;

    /// <summary>Drops cached language/brush state so the next preview build re-resolves both.</summary>
    private void InvalidatePreviewSyntaxState()
    {
        _previewSyntaxLanguagePath = null;
        _previewSyntaxLanguage = null;
        _previewSyntaxBrushes.Clear();
    }

    /// <summary>
    /// Resolves the syntax language for a previewed file, or <c>null</c> when preview syntax
    /// coloring is off or the file type has no classifier. Caches the last path because a preview
    /// renders many consecutive lines from the same file.
    /// </summary>
    private EditorSyntaxLanguage? ResolvePreviewSyntaxLanguage(string? filePath)
    {
        if (!ViewModel.PreviewSyntaxHighlightingEnabled || string.IsNullOrEmpty(filePath))
            return null;

        if (!string.Equals(filePath, _previewSyntaxLanguagePath, StringComparison.OrdinalIgnoreCase))
        {
            _previewSyntaxLanguagePath = filePath;
            var resolved = EditorSyntaxHighlightingResolver.ResolveFromFileName(filePath);
            _previewSyntaxLanguage = resolved is { } language && PreviewSyntaxHighlighter.IsSupported(language)
                ? language
                : null;
        }

        return _previewSyntaxLanguage;
    }

    /// <summary>
    /// Classifies one rendered preview line. Returns <c>null</c> when coloring is unavailable,
    /// the line is too long, the build's run budget is spent, or nothing was classified.
    /// </summary>
    private List<PreviewSyntaxHighlighter.Token>? BuildPreviewSyntaxTokens(string text, EditorSyntaxLanguage? language)
    {
        if (language is null || string.IsNullOrEmpty(text))
            return null;
        if (text.Length > MaxPreviewSyntaxLineLength || _previewSyntaxRunsRemaining <= 0)
            return null;

        var tokens = PreviewSyntaxHighlighter.Tokenize(text, language.Value);
        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>
    /// Emits <paramref name="text"/>[<paramref name="from"/>..<paramref name="to"/>) into
    /// <paramref name="para"/>, split into syntax-colored runs where tokens overlap the range.
    /// Falls back to a single <paramref name="baseBrush"/> run when there is nothing to color.
    /// </summary>
    private void AddPreviewSyntaxRuns(
        Paragraph para,
        string text,
        int from,
        int to,
        Brush baseBrush,
        bool isMatchLine,
        List<PreviewSyntaxHighlighter.Token>? tokens)
    {
        if (to <= from)
            return;

        if (tokens is null || _previewSyntaxRunsRemaining <= 0)
        {
            para.Inlines.Add(new Run { Text = text[from..to], Foreground = baseBrush });
            return;
        }

        int cursor = from;
        foreach (var token in tokens)
        {
            if (token.End <= cursor) continue;
            if (token.Start >= to) break;

            int tokenStart = Math.Max(token.Start, cursor);
            int tokenEnd = Math.Min(token.End, to);
            if (tokenEnd <= tokenStart) continue;

            if (tokenStart > cursor)
            {
                para.Inlines.Add(new Run { Text = text[cursor..tokenStart], Foreground = baseBrush });
                _previewSyntaxRunsRemaining--;
            }

            para.Inlines.Add(new Run
            {
                Text = text[tokenStart..tokenEnd],
                Foreground = ResolvePreviewSyntaxBrush(token.Kind, dimmed: !isMatchLine),
            });
            _previewSyntaxRunsRemaining--;
            cursor = tokenEnd;

            if (_previewSyntaxRunsRemaining <= 0)
                break;
        }

        if (cursor < to)
            para.Inlines.Add(new Run { Text = text[cursor..to], Foreground = baseBrush });
    }

    private SolidColorBrush ResolvePreviewSyntaxBrush(PreviewTokenKind kind, bool dimmed)
    {
        bool isLight = IsLightSurfaceTheme;
        if (isLight != _previewSyntaxBrushesAreLightTheme)
        {
            _previewSyntaxBrushes.Clear();
            _previewSyntaxBrushesAreLightTheme = isLight;
        }

        if (_previewSyntaxBrushes.TryGetValue((kind, dimmed), out var cached))
            return cached;

        uint argb = dimmed
            ? PreviewSyntaxPalette.GetContextColor(kind, isLight)
            : PreviewSyntaxPalette.GetColor(kind, isLight);

        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF)));

        _previewSyntaxBrushes[(kind, dimmed)] = brush;
        return brush;
    }
}
