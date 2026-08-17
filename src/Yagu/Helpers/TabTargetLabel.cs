using System.Text;

namespace Yagu.Helpers;

/// <summary>
/// Builds the short, human-readable name a control is given in the one-time "where should Tab go?"
/// prompt. The prompt names whichever control currently sits first inside the directory/search-pattern
/// box, so the label has to be derived from that control rather than hard-coded — adding a new inline
/// control renames the prompt automatically.
/// </summary>
internal static class TabTargetLabel
{
    /// <summary>Tooltips carry accelerators and long explanations ("Match Case (Alt+C)",
    /// "Exact Match — on: treat the whole query as one term"); only the leading name is wanted.</summary>
    private static readonly string[] CutMarkers = [" (", " \u2014 ", " - ", ". ", ", ", "\u2014", ":"];

    private static readonly string[] NameSuffixes = ["ToggleButton", "Toggle", "Button"];

    /// <summary>The label for a control, preferring its automation name, then its tooltip, then a
    /// humanized form of its x:Name. Returns a generic fallback when all three are empty.</summary>
    internal static string For(string? automationName, string? toolTipText, string? elementName)
    {
        if (!string.IsNullOrWhiteSpace(automationName))
            return automationName.Trim();

        string fromTooltip = FromToolTip(toolTipText);
        if (fromTooltip.Length > 0)
            return fromTooltip;

        return FromElementName(elementName);
    }

    internal static string FromToolTip(string? toolTipText)
    {
        if (string.IsNullOrWhiteSpace(toolTipText))
            return string.Empty;

        int cut = toolTipText.Length;
        foreach (string marker in CutMarkers)
        {
            int index = toolTipText.IndexOf(marker, StringComparison.Ordinal);
            if (index > 0 && index < cut)
                cut = index;
        }

        return toolTipText[..cut].Trim().TrimEnd('.', ',', ':', ';');
    }

    internal static string FromElementName(string? elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName))
            return "the next control";

        string name = elementName.Trim();
        foreach (string suffix in NameSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                builder.Append(' ');
            builder.Append(c);
        }

        return builder.ToString();
    }
}
