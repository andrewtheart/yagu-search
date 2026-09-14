using System;

namespace Yagu.Helpers;

/// <summary>
/// Theme-aware colors for <see cref="PreviewTokenKind"/> tokens rendered in the preview pane.
/// <para>
/// Kept free of WinUI types (plain packed ARGB) so the palette — including the
/// context-line dimming rule — is unit-testable in the headless engine test project.
/// </para>
/// </summary>
public static class PreviewSyntaxPalette
{
    /// <summary>The preview pane's context-line gray; dimmed tokens are blended toward it.</summary>
    public const uint ContextGray = 0xFF6E6E6E;

    /// <summary>
    /// How far a context-line token is blended toward <see cref="ContextGray"/>. Context lines stay
    /// visually subordinate to match lines, which is the preview's existing hierarchy.
    /// </summary>
    public const double ContextDimAmount = 0.45;

    /// <summary>Returns the packed ARGB color for a token kind in the requested theme.</summary>
    public static uint GetColor(PreviewTokenKind kind, bool isLightTheme) => isLightTheme
        ? kind switch
        {
            PreviewTokenKind.Keyword => 0xFF0000FF,
            PreviewTokenKind.StringLiteral => 0xFFA31515,
            PreviewTokenKind.Comment => 0xFF008000,
            PreviewTokenKind.Number => 0xFF098658,
            PreviewTokenKind.Preprocessor => 0xFFAF00DB,
            PreviewTokenKind.Tag => 0xFF267F99,
            PreviewTokenKind.Attribute => 0xFFCC0000,
            _ => 0xFF000000,
        }
        : kind switch
        {
            PreviewTokenKind.Keyword => 0xFF569CD6,
            PreviewTokenKind.StringLiteral => 0xFFCE9178,
            PreviewTokenKind.Comment => 0xFF6A9955,
            PreviewTokenKind.Number => 0xFFB5CEA8,
            PreviewTokenKind.Preprocessor => 0xFFC586C0,
            PreviewTokenKind.Tag => 0xFF4EC9B0,
            PreviewTokenKind.Attribute => 0xFF9CDCFE,
            _ => 0xFFFFFFFF,
        };

    /// <summary>
    /// Returns the color used on context (non-match) lines: the token color blended toward
    /// <see cref="ContextGray"/> so syntax coloring never makes context read as strongly as matches.
    /// </summary>
    public static uint GetContextColor(PreviewTokenKind kind, bool isLightTheme) =>
        Blend(GetColor(kind, isLightTheme), ContextGray, ContextDimAmount);

    /// <summary>Linearly blends <paramref name="from"/> toward <paramref name="to"/> by <paramref name="amount"/> (0..1).</summary>
    public static uint Blend(uint from, uint to, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        byte a = BlendChannel(from >> 24, to >> 24, t);
        byte r = BlendChannel(from >> 16, to >> 16, t);
        byte g = BlendChannel(from >> 8, to >> 8, t);
        byte b = BlendChannel(from, to, t);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static byte BlendChannel(uint from, uint to, double t)
    {
        double value = ((from & 0xFF) * (1.0 - t)) + ((to & 0xFF) * t);
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
