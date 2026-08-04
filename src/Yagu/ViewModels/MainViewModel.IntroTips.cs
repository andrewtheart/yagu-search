using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// First-time introductory teaching tips (file drawer, drawer line numbers, preview match) — the
/// shown/reset state, the persisted marks, and the terminal shell-kind preference.
/// </summary>
public sealed partial class MainViewModel
{
    public void ResetFirstTimeIntroductoryTooltips()
    {
        HasShownFileDrawerIntroTip = false;
        HasShownFileDrawerLineNumberIntroTip = false;
        HasShownPreviewMatchIntroTip = false;
    }

    public void RestoreFirstTimeIntroductoryTooltips(bool fileDrawer, bool fileDrawerLineNumber, bool previewMatch)
    {
        HasShownFileDrawerIntroTip = fileDrawer;
        HasShownFileDrawerLineNumberIntroTip = fileDrawerLineNumber;
        HasShownPreviewMatchIntroTip = previewMatch;
    }

    public Task MarkFileDrawerIntroTipShownAsync()
        => MarkIntroTipShownAsync(nameof(HasShownFileDrawerIntroTip));

    public Task MarkFileDrawerLineNumberIntroTipShownAsync()
        => MarkIntroTipShownAsync(nameof(HasShownFileDrawerLineNumberIntroTip));

    public Task MarkPreviewMatchIntroTipShownAsync()
        => MarkIntroTipShownAsync(nameof(HasShownPreviewMatchIntroTip));

    private async Task MarkIntroTipShownAsync(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(HasShownFileDrawerIntroTip):
                if (HasShownFileDrawerIntroTip) return;
                HasShownFileDrawerIntroTip = true;
                _settings.HasShownFileDrawerIntroTip = true;
                break;
            case nameof(HasShownFileDrawerLineNumberIntroTip):
                if (HasShownFileDrawerLineNumberIntroTip) return;
                HasShownFileDrawerLineNumberIntroTip = true;
                _settings.HasShownFileDrawerLineNumberIntroTip = true;
                break;
            case nameof(HasShownPreviewMatchIntroTip):
                if (HasShownPreviewMatchIntroTip) return;
                HasShownPreviewMatchIntroTip = true;
                _settings.HasShownPreviewMatchIntroTip = true;
                break;
            default:
                return;
        }

        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }

    /// <summary>Persists the embedded terminal's shell choice (0 = cmd, 1 = PowerShell) immediately,
    /// so switching shells via the terminal-pane dropdown survives a restart.</summary>
    public async Task SetTerminalShellKindIndexAsync(int index)
    {
        int normalized = TerminalShell.NormalizeSettingsIndex(index);
        TerminalShellKindIndex = normalized;
        _settings.TerminalShellKindIndex = normalized;
        await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
    }
}
