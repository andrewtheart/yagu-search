using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Logging;

namespace Yagu;

public sealed partial class MainWindow
{
    private AppUpdateCheckResult? _pendingAppUpdateCheck;
    private AppReleaseInfo? _pendingAppUpdateRelease;

    /// <summary>
    /// One-time update-check consent. Only shows on a fresh install, or a legacy user who never made a
    /// durable choice (mode == Prompt); the selection is persisted so Yagu never asks again. Stays in the
    /// awaited startup-modal chain so it never races or stacks with the other first-run dialogs.
    /// </summary>
    private async Task MaybeShowAppUpdateConsentPromptAsync()
    {
        AppSettings settings = ViewModel.Settings;
        if (settings.AppUpdateCheckMode != AppUpdateCheckMode.Prompt || YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var panel = new StackPanel { Spacing = 12, MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = "How should Yagu check for new versions? It only ever contacts the official GitHub Releases page and never sends any of your data.",
            TextWrapping = TextWrapping.Wrap,
        });

        var choices = new StackPanel { Spacing = 6 };
        var options = AppUpdateModeChoices.All.Select(BuildAppUpdateModeOption).ToList();
        foreach (RadioButton option in options)
            choices.Children.Add(option);
        options[AppUpdateModeChoices.DefaultIndex].IsChecked = true;
        panel.Children.Add(choices);

        panel.Children.Add(new TextBlock
        {
            Text = "You can change this any time under Settings \u25b8 Updates.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.6,
        });

        YaguDialogResult result = await YaguDialog.ShowAsync(_hwnd, new YaguDialogOptions
        {
            Title = "How should Yagu check for updates?",
            TitleGlyph = "\uE895",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Ask me later",
            DefaultButton = YaguDialogDefaultButton.Primary,
            RequestedTheme = RootGrid.ActualTheme,
            ShowTitleBar = false,
            Width = 660,
            Height = 520,
        });

        // Anything other than Save (including Escape/close) leaves the mode at Prompt so Yagu asks again
        // next launch instead of silently picking a network behavior for the user.
        if (result != YaguDialogResult.Primary)
            return;
        if (options.FirstOrDefault(o => o.IsChecked == true) is not { Tag: AppUpdateCheckMode mode })
            return;

        settings.AppUpdateCheckMode = mode;
        settings.AppUpdateChecksEnabled = mode != AppUpdateCheckMode.Off; // keep the legacy flag consistent
        await ViewModel.PersistSettingsAsync().ConfigureAwait(true);
    }

    private static RadioButton BuildAppUpdateModeOption(AppUpdateModeChoice choice)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = choice.PromptTitle, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = choice.PromptDetail,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
        });

        return new RadioButton
        {
            GroupName = "AppUpdateCheckMode",
            Tag = choice.Mode,
            Content = content,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
        };
    }

    /// <summary>
    /// Automatic-mode background check. Runs at most once per the interval
    /// <see cref="AppUpdateChecker.GetAutoCheckInterval"/> maps the chosen mode to (daily or weekly)
    /// and never opens a modal: it silently ignores "up to date" and network errors and surfaces a genuinely
    /// newer, verifiable, not-already-skipped release only through the non-modal <c>AppUpdateInfoBar</c>.
    /// Fire-and-forget from startup so it never delays launch.
    /// </summary>
    private async Task MaybeRunAutomaticAppUpdateCheckAsync()
    {
        AppSettings settings = ViewModel.Settings;
        if (AppUpdateChecker.GetAutoCheckInterval(settings.AppUpdateCheckMode) is not { } interval)
            return;
        if (!settings.NotificationsEnabled || !settings.NotifyApplicationUpdates)
            return;
        if (!AppUpdateChecker.ShouldAutoCheck(settings.LastAppUpdateCheckUtc, DateTimeOffset.UtcNow, interval))
            return;

        AppUpdateCheckResult? check = await RunAppUpdateCheckCoreAsync().ConfigureAwait(true);
        if (check is null || !check.UpdateAvailable || check.Release is not { } release)
            return; // silent: no update, unverifiable installer, or a network error
        if (string.Equals(release.Version.ToString(), settings.LastAppUpdateAlertedVersion, StringComparison.Ordinal))
            return; // the user already skipped this exact version

        ShowAppUpdateAvailableInfoBar(check, release);
    }

    /// <summary>
    /// On-demand check (the Settings "Check for updates now" button). Verbose: shows the up-to-date and
    /// error results as modals owned by <paramref name="ownerHwnd"/>, and the full download/verify/install
    /// flow for a newer release.
    /// </summary>
    internal async Task RunManualAppUpdateCheckAsync(IntPtr ownerHwnd)
    {
        if (YaguDialog.HasOpenOwnedWindow(ownerHwnd))
            return;

        ViewModel.StatusText = "Checking the official Yagu GitHub release…";
        AppUpdateCheckResult? check = await RunAppUpdateCheckCoreAsync().ConfigureAwait(true);
        if (check is null)
        {
            ViewModel.StatusText = "The update check did not complete.";
            await ShowAppUpdateCheckResultAsync(ownerHwnd, "Update check did not complete",
                "Yagu could not read the official GitHub release information. Check your connection and try again.");
            return;
        }
        if (!check.UpdateAvailable)
        {
            ViewModel.StatusText = $"Yagu {check.CurrentVersion} is up to date.";
            await ShowAppUpdateCheckResultAsync(ownerHwnd, "Yagu is up to date",
                $"You are running Yagu {check.CurrentVersion}, the latest official release.");
            return;
        }
        if (check.Release is not { } release)
        {
            ViewModel.StatusText = $"Yagu {check.LatestVersion} is available, but its installer metadata could not be verified.";
            await ShowAppUpdateCheckResultAsync(ownerHwnd, "Update installer could not be verified",
                $"Yagu {check.LatestVersion} is available, but Yagu could not find exactly one architecture-matched installer with valid GitHub SHA-256 metadata. Nothing was downloaded.");
            return;
        }
        await PresentAppUpdateReleaseAsync(ownerHwnd, check, release).ConfigureAwait(true);
    }

    /// <summary>Runs the GitHub metadata check and records the attempt time (throttle input). Returns null
    /// on any network/parse error.</summary>
    private async Task<AppUpdateCheckResult?> RunAppUpdateCheckCoreAsync()
    {
        AppUpdateCheckResult? check;
        try { check = await AppUpdateChecker.CheckLatestAsync(AppInfo.Version).ConfigureAwait(true); }
        catch { check = null; }
        ViewModel.Settings.LastAppUpdateCheckUtc = DateTimeOffset.UtcNow;
        await ViewModel.PersistSettingsAsync().ConfigureAwait(true);
        return check;
    }

    /// <summary>Opens the non-modal update banner for a newer release and stashes it for the banner actions.</summary>
    private void ShowAppUpdateAvailableInfoBar(AppUpdateCheckResult check, AppReleaseInfo release)
    {
        _pendingAppUpdateCheck = check;
        _pendingAppUpdateRelease = release;
        AppUpdateInfoBar.Title = $"Yagu {release.Version} is available";
        AppUpdateInfoBar.Message = $"You are running Yagu {check.CurrentVersion}. View the release to download the verified installer.";
        AppUpdateInfoBar.IsOpen = true;
        ViewModel.StatusText = $"Yagu {release.Version} is available.";
    }

    // Close (X) on the banner = remind me later: leave LastAppUpdateAlertedVersion unset so the next
    // throttled automatic check surfaces this version again.
    private void OnAppUpdateInfoBarRemindLater(InfoBar sender, object args) { }

    private async void OnAppUpdateInfoBarViewRelease(object sender, RoutedEventArgs e)
    {
        AppUpdateInfoBar.IsOpen = false;
        if (_pendingAppUpdateCheck is { } check && _pendingAppUpdateRelease is { } release)
            await PresentAppUpdateReleaseAsync(_hwnd, check, release);
    }

    private async void OnAppUpdateInfoBarSkipVersion(object sender, RoutedEventArgs e)
    {
        if (_pendingAppUpdateRelease is { } release)
        {
            ViewModel.Settings.LastAppUpdateAlertedVersion = release.Version.ToString();
            await ViewModel.PersistSettingsAsync().ConfigureAwait(true);
            ViewModel.StatusText = $"Skipped Yagu {release.Version}.";
        }
        AppUpdateInfoBar.IsOpen = false;
    }

    private async Task ShowAppUpdateCheckResultAsync(IntPtr ownerHwnd, string title, string message)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var releaseLink = new HyperlinkButton { Content = "Open official Yagu releases on GitHub", Padding = new Thickness(0) };
        releaseLink.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(AppUpdateChecker.LatestReleasePage);
        panel.Children.Add(releaseLink);
        await YaguDialog.ShowAsync(ownerHwnd, new YaguDialogOptions
        {
            Title = title,
            TitleGlyph = "\uE895",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = YaguDialogDefaultButton.Close,
            RequestedTheme = RootGrid.ActualTheme,
            ShowTitleBar = false,
            ShowTopRightCloseButton = true,
            Width = 620,
            Height = 340,
        });
    }

    private async Task PresentAppUpdateReleaseAsync(IntPtr ownerHwnd, AppUpdateCheckResult check, AppReleaseInfo release)
    {

        var panel = new StackPanel { Spacing = 12, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Yagu {release.Version} is available. You are running {check.CurrentVersion}.",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = "Release notes", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            MaxHeight = 300,
            Text = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "No release notes were provided." : release.ReleaseNotes,
        });
        var releaseLink = new HyperlinkButton { Content = "View this release on GitHub", Padding = new Thickness(0) };
        releaseLink.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(release.ReleasePage);
        panel.Children.Add(releaseLink);
        panel.Children.Add(new TextBlock
        {
            Text = "If you continue, download progress opens in a dedicated MultiTerm terminal. Yagu verifies the GitHub SHA-256 and then requires a trusted Authenticode signature from the same publisher as the running Yagu before it will offer installation.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.75,
        });

        YaguDialogResult result = await YaguDialog.ShowAsync(
            ownerHwnd,
            new YaguDialogOptions
            {
                Title = "Yagu update available",
                TitleGlyph = "\uE896",
                Content = panel,
                PrimaryButtonText = "Download update",
                CloseButtonText = "Later",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                ShowTopRightCloseButton = true,
                Width = 700,
                Height = 570,
                MaxContentHeight = 470,
            });
        if (result != YaguDialogResult.Primary)
            return;

        await DownloadAppUpdateInMultiTermAsync(ownerHwnd, release).ConfigureAwait(true);
    }

    private async Task DownloadAppUpdateInMultiTermAsync(IntPtr ownerHwnd, AppReleaseInfo release)
    {
        string updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yagu", "Updates", release.Version.ToString());
        string destination = Path.Combine(updateDir, release.Installer.Name);
        ViewModel.StatusText = $"Downloading Yagu {release.Version} in MultiTerm…";
        MultiTermDownloadResult download = await MultiTermUpdateDownloader.DownloadAsync(
            release, destination, CancellationToken.None).ConfigureAwait(true);
        if (!download.Succeeded || download.FilePath is null)
        {
            await ShowUpdateDownloadFailureAsync(ownerHwnd, release, download.Error ?? "The download did not complete.");
            return;
        }
        if (!await AppUpdateChecker.VerifyDownloadedAssetAsync(download.FilePath, release.Installer).ConfigureAwait(true))
        {
            try { File.Delete(download.FilePath); } catch { }
            await ShowUpdateDownloadFailureAsync(ownerHwnd, release, "The downloaded installer failed its size or SHA-256 verification and was deleted.");
            return;
        }
        if (!AuthenticodeVerifier.IsInstallerTrustedForHostPublisher(download.FilePath, out string trustFailure))
        {
            try { File.Delete(download.FilePath); } catch { }
            await ShowUpdateDownloadFailureAsync(
                ownerHwnd,
                release,
                $"The downloaded installer failed Authenticode publisher verification ({trustFailure}) and was deleted. Nothing was executed.");
            return;
        }

        ViewModel.StatusText = $"Yagu {release.Version} passed SHA-256 and Authenticode verification.";
        await OfferVerifiedInstallerAsync(ownerHwnd, release, download.FilePath).ConfigureAwait(true);
    }

    private async Task ShowUpdateDownloadFailureAsync(IntPtr ownerHwnd, AppReleaseInfo release, string error)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
        var link = new HyperlinkButton { Content = "Open the release on GitHub", Padding = new Thickness(0) };
        link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(release.ReleasePage);
        panel.Children.Add(link);
        await YaguDialog.ShowAsync(ownerHwnd, new YaguDialogOptions
        {
            Title = "Update download did not complete",
            TitleGlyph = "\uEA39",
            Content = panel,
            CloseButtonText = "Close",
            RequestedTheme = RootGrid.ActualTheme,
            ShowTitleBar = false,
            ShowTopRightCloseButton = true,
            Width = 600,
            Height = 330,
        });
    }

    private async Task OfferVerifiedInstallerAsync(IntPtr ownerHwnd, AppReleaseInfo release, string installerPath)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Yagu {release.Version} passed GitHub SHA-256 and Authenticode publisher verification. Install now? Yagu will close only after the trusted installer starts.",
            TextWrapping = TextWrapping.Wrap,
        });

        YaguDialogResult result = await YaguDialog.ShowAsync(ownerHwnd, new YaguDialogOptions
        {
            Title = "Ready to install Yagu",
            TitleGlyph = "\uE930",
            Content = panel,
            PrimaryButtonText = "Install and exit Yagu",
            SecondaryButtonText = "Open download folder",
            CloseButtonText = "Later",
            DefaultButton = YaguDialogDefaultButton.Primary,
            RequestedTheme = RootGrid.ActualTheme,
            ShowTitleBar = false,
            ShowTopRightCloseButton = true,
            Width = 650,
            Height = 370,
        });
        if (result == YaguDialogResult.Secondary)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{installerPath}\"") { UseShellExecute = true });
            return;
        }
        if (result != YaguDialogResult.Primary)
            return;

        if (!await ConfirmExitWhileIndexingAsync(IndexingCloseTrigger.AppUpdate))
            return;

        // Re-check after every user-controlled wait, immediately before crossing the execution boundary.
        if (!await AppUpdateChecker.VerifyDownloadedAssetAsync(installerPath, release.Installer).ConfigureAwait(true)
            || !AuthenticodeVerifier.IsInstallerTrustedForHostPublisher(installerPath, out _))
        {
            try { File.Delete(installerPath); } catch { }
            ViewModel.StatusText = "The installer no longer passes update verification and was not launched.";
            return;
        }

        try
        {
            Process? installer = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"/YAGUWAITPID={Environment.ProcessId}",
            });
            if (installer is null) return;
            await CompleteApplicationExitAsync();
        }
        catch (Exception ex)
        {
            YaguLog.For("AppUpdate").LogWarning(ex, "Could not launch the verified Yagu installer.");
            ViewModel.StatusText = "The update installer was not launched; Yagu remains open.";
        }
    }
}
