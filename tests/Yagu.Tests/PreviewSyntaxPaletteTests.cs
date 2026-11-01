using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Covers the preview token palette: the per-theme colors and the context dimming applied to
/// non-match lines.
/// </summary>
/// <remarks>
/// The load-bearing property is that every token kind resolves to a legible color in both themes.
/// A missing mapping does not fail loudly — it returns whatever the fallback arm decides — so the
/// fallback has to be the readable foreground rather than a default-initialized (transparent) value.
/// </remarks>
public sealed class PreviewSyntaxPaletteTests
{
    private static readonly PreviewTokenKind[] AllKinds = Enum.GetValues<PreviewTokenKind>();

    [Fact]
    public void EveryTokenKind_ResolvesToAnOpaqueColorInBothThemes()
    {
        foreach (PreviewTokenKind kind in AllKinds)
        {
            foreach (bool light in new[] { true, false })
            {
                uint color = PreviewSyntaxPalette.GetColor(kind, light);
                Assert.True((color >> 24) == 0xFF, $"{kind} ({(light ? "light" : "dark")}) is not opaque.");
            }
        }
    }

    [Fact]
    public void EveryTokenKind_IsDistinguishableFromThePlainForeground()
    {
        // A kind that resolves to the same color as unhighlighted text is indistinguishable from no
        // highlighting at all, which is how a forgotten palette entry presents to a user.
        foreach (PreviewTokenKind kind in AllKinds)
        {
            Assert.NotEqual(0xFF000000u, PreviewSyntaxPalette.GetColor(kind, isLightTheme: true));
            Assert.NotEqual(0xFFFFFFFFu, PreviewSyntaxPalette.GetColor(kind, isLightTheme: false));
        }
    }

    [Theory]
    [InlineData(true, 0xFF000000u)]
    [InlineData(false, 0xFFFFFFFFu)]
    public void AnUnmappedTokenKind_FallsBackToThePlainForeground(bool isLightTheme, uint expected)
    {
        // Adding an enum member without a palette entry must degrade to ordinary readable text rather
        // than to a transparent or black-on-black default.
        var unmapped = (PreviewTokenKind)9999;

        Assert.Equal(expected, PreviewSyntaxPalette.GetColor(unmapped, isLightTheme));
    }

    [Fact]
    public void ContextColors_AreBlendedTowardGraySoMatchesStillReadMoreStrongly()
    {
        foreach (PreviewTokenKind kind in AllKinds)
        {
            foreach (bool light in new[] { true, false })
            {
                uint token = PreviewSyntaxPalette.GetColor(kind, light);
                uint context = PreviewSyntaxPalette.GetContextColor(kind, light);

                Assert.Equal(
                    PreviewSyntaxPalette.Blend(token, PreviewSyntaxPalette.ContextGray, PreviewSyntaxPalette.ContextDimAmount),
                    context);
                Assert.True((context >> 24) == 0xFF, $"context color for {kind} is not opaque.");
            }
        }
    }

    [Fact]
    public void Blend_IsAnEndpointPreservingInterpolation()
    {
        const uint from = 0xFF102030;
        const uint to = 0xFF8090A0;

        Assert.Equal(from, PreviewSyntaxPalette.Blend(from, to, 0.0));
        Assert.Equal(to, PreviewSyntaxPalette.Blend(from, to, 1.0));

        uint middle = PreviewSyntaxPalette.Blend(from, to, 0.5);
        foreach (int shift in new[] { 16, 8, 0 })
        {
            byte a = (byte)(from >> shift), b = (byte)(to >> shift), m = (byte)(middle >> shift);
            Assert.InRange(m, Math.Min(a, b), Math.Max(a, b));
        }
    }
}
