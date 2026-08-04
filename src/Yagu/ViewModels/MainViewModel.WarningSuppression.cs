using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// "Don't show this again" state: suppression flags for the admin, font-contrast, excluded-extension
/// and Everything prompts, the admin-protected path list, and the first-run completion flags.
/// </summary>
public sealed partial class MainViewModel
{
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
}
