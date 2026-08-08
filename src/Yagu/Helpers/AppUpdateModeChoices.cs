using Yagu.Services;

namespace Yagu.Helpers;

/// <summary>One selectable update-check answer, worded for both the one-time consent prompt and the
/// Settings picker.</summary>
internal sealed record AppUpdateModeChoice(
    AppUpdateCheckMode Mode,
    string PromptTitle,
    string PromptDetail,
    string SettingsLabel);

/// <summary>
/// The single ordered list of update-check answers. The one-time consent prompt and Settings ▸ Updates
/// both render it, so a mode can never be offered by one surface and missing from the other, and the
/// Settings row index can never drift from the mode it stores.
/// </summary>
internal static class AppUpdateModeChoices
{
    /// <summary>Row preselected by both surfaces.</summary>
    internal const int DefaultIndex = 0;

    internal static IReadOnlyList<AppUpdateModeChoice> All { get; } =
    [
        new(AppUpdateCheckMode.AutomaticDaily,
            "Automatically \u2014 once a day (recommended)",
            "A quiet background check about once a day; you are notified only when a newer version exists.",
            "Automatically (a quiet check once a day)"),
        new(AppUpdateCheckMode.Automatic,
            "Automatically \u2014 about once a week",
            "The same quiet background check, just less often.",
            "Automatically (a quiet check about once a week)"),
        new(AppUpdateCheckMode.Manual,
            "Only when I ask",
            "Yagu never checks on its own; a Settings button checks on demand.",
            "Only when I ask"),
        new(AppUpdateCheckMode.Off,
            "Don't check for updates",
            "No update checks at all.",
            "Off (never check)"),
    ];

    internal static AppUpdateCheckMode DefaultMode => All[DefaultIndex].Mode;

    /// <summary>Row to select for <paramref name="mode"/>. The undecided <see cref="AppUpdateCheckMode.Prompt"/>
    /// state has no row of its own and shows as check-on-demand, which is how it behaves until the
    /// consent prompt is answered.</summary>
    internal static int IndexFor(AppUpdateCheckMode mode)
    {
        int index = IndexOf(mode);
        return index >= 0 ? index : IndexOf(AppUpdateCheckMode.Manual);
    }

    /// <summary>Mode for a selected row. An out-of-range row falls back to check-on-demand rather than
    /// silently enabling background network calls.</summary>
    internal static AppUpdateCheckMode ModeFor(int index)
        => index >= 0 && index < All.Count ? All[index].Mode : AppUpdateCheckMode.Manual;

    private static int IndexOf(AppUpdateCheckMode mode)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i].Mode == mode)
                return i;
        }

        return -1;
    }
}
