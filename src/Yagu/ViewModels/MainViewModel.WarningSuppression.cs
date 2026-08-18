using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// "Don't show this again" state: suppression flags for the admin, font-contrast, excluded-extension
/// and Everything prompts, the admin-protected path list, and the first-run completion flags.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Persists one prompt-reset field without saving unrelated Settings-window edits that
    /// have not been applied yet. The live settings object is updated too so the reset state is
    /// immediately reflected in Developer Options.</summary>
    private async Task<bool> PersistPromptResetAsync(Action<AppSettings> reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        return await _settingsService.UpdateAsync(reset, afterCommit: () => reset(_settings))
            .ConfigureAwait(true);
    }

    private bool _suppressAdminWarning;
    public bool SuppressAdminWarning
    {
        get => _suppressAdminWarning;
        set => SetProperty(ref _suppressAdminWarning, value);
    }

    [ObservableProperty] public partial bool SuppressFontContrastWarnings { get; set; }
    [ObservableProperty] public partial bool SuppressExcludedExtensionWarnings { get; set; }

    /// <summary>When the excluded-file-type warning is suppressed, whether to automatically INCLUDE the
    /// excluded type in the search (true) or search WITHOUT it (false). Set from the warning dialog's
    /// "Always do this" choice and from Settings. Only meaningful while <see cref="SuppressExcludedExtensionWarnings"/>
    /// is true.</summary>
    [ObservableProperty] public partial bool IncludeExcludedExtensionByDefault { get; set; }

    private bool _suppressEverythingNotRunningPrompt;
    public bool SuppressEverythingNotRunningPrompt
    {
        get => _suppressEverythingNotRunningPrompt;
        set => SetProperty(ref _suppressEverythingNotRunningPrompt, value);
    }
    [ObservableProperty] public partial bool SuppressEverythingIndexCoverageWarning { get; set; }
    [ObservableProperty] public partial DateTimeOffset? FontContrastReminderAfterUtc { get; set; }

    [ObservableProperty] public partial bool ExcludeAdminProtectedPaths { get; set; } = true;
    [ObservableProperty] public partial string AdminProtectedPathSegments { get; set; } = AppSettings.DefaultAdminProtectedPathSegments;

    [ObservableProperty] public partial bool HasCompletedFirstRun { get; set; }
    [ObservableProperty] public partial bool HasShownFileDrawerIntroTip { get; set; }
    [ObservableProperty] public partial bool HasShownFileDrawerLineNumberIntroTip { get; set; }
    [ObservableProperty] public partial bool HasShownPreviewMatchIntroTip { get; set; }

    /// <summary>Shows Explorer context-menu onboarding again on the next launch when the integration is
    /// not installed. An existing context-menu registration is left unchanged.</summary>
    public async Task ResetExplorerContextMenuPromptAsync()
    {
        if (await PersistPromptResetAsync(settings => settings.HasCompletedFirstRun = false)
            .ConfigureAwait(true))
            HasCompletedFirstRun = false;
    }

    /// <summary>Shows result-storage location onboarding again on the next launch while preserving the
    /// currently selected directory until the user accepts another choice.</summary>
    public async Task ResetResultStoreTempLocationPromptAsync()
    {
        if (await PersistPromptResetAsync(settings => settings.HasChosenSearchResultTempDirectory = false)
            .ConfigureAwait(true))
            HasChosenSearchResultTempDirectory = false;
    }

    /// <summary>Returns update checks to the undecided state so the startup choice is shown again.
    /// Prompt mode performs no network check before the user selects and saves a mode.</summary>
    public async Task ResetAppUpdateConsentPromptAsync()
    {
        await PersistPromptResetAsync(settings =>
        {
            settings.AppUpdateCheckMode = AppUpdateCheckMode.Prompt;
            settings.AppUpdateChecksEnabled = true;
        }).ConfigureAwait(true);
    }
}
