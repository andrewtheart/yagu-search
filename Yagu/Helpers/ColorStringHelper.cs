using System.Globalization;

namespace Yagu.Helpers;

internal static class ColorStringHelper
{
    public static Windows.UI.Color Parse(string? value, Windows.UI.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Windows.UI.Color.FromArgb(
                0xFF,
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
        }

        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return Windows.UI.Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        return fallback;
    }

    public static string ToHex(Windows.UI.Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static string Normalize(string? value, Windows.UI.Color fallback)
        => ToHex(Parse(value, fallback));
}