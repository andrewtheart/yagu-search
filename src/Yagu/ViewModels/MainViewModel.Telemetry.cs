using CommunityToolkit.Mvvm.ComponentModel;

namespace Yagu.ViewModels;

/// <summary>
/// Opt-in telemetry and bug-report consent state. Telemetry ships offline by default: these
/// settings only seed from persisted values (guarded by <c>_telemetryInitialized</c>) and are
/// persisted when the user changes them.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Settings-panel toggle for the silent, anonymized telemetry channel. Two-way bound;
    /// applied live to <see cref="Yagu.Services.Telemetry.TelemetryGate"/> and persisted.</summary>
    [ObservableProperty]
    public partial bool TelemetryEnabledSetting { get; set; }

    /// <summary>Settings-panel toggle for the (reviewed) bug-report flow. Two-way bound; applied live
    /// and persisted. Independent of <see cref="TelemetryEnabledSetting"/>.</summary>
    [ObservableProperty]
    public partial bool BugReportingEnabledSetting { get; set; }

    /// <summary>Optional contact email used to pre-fill the bug-report dialog. Two-way bound in the
    /// Settings panel and updated when the user types an email in a report.</summary>
    [ObservableProperty]
    public partial string BugReportContactEmail { get; set; } = string.Empty;

    /// <summary>True once the first-run telemetry/bug-report consent dialog has been shown, so the app
    /// never asks again.</summary>
    public bool TelemetryConsentPromptShown => _settings.TelemetryConsentPromptShown;

    partial void OnTelemetryEnabledSettingChanged(bool value)
    {
        if (!_telemetryInitialized) return;
        Yagu.Services.Telemetry.TelemetryGate.TelemetryEnabled = value;
        _settings.TelemetryEnabled = value;
        if (value)
            Yagu.Services.Telemetry.TelemetryService.Instance.Initialize(EnsureTelemetryInstallId());
        _ = PersistSettingsAsync();
    }

    partial void OnBugReportingEnabledSettingChanged(bool value)
    {
        if (!_telemetryInitialized) return;
        Yagu.Services.Telemetry.TelemetryGate.BugReportingEnabled = value;
        _settings.BugReportingEnabled = value;
        if (value)
            Yagu.Services.Telemetry.BugReportService.Instance.Initialize(EnsureTelemetryInstallId());
        _ = PersistSettingsAsync();
    }

    partial void OnBugReportContactEmailChanged(string value)
    {
        if (!_telemetryInitialized) return;
        _settings.BugReportContactEmail = value ?? string.Empty;
        _ = PersistSettingsAsync();
    }

    /// <summary>Ensures a stable, non-PII install identifier exists (generating one on first need) and
    /// returns it. Used to tag telemetry and bug reports without identifying the user or machine.</summary>
    private string EnsureTelemetryInstallId()
    {
        if (string.IsNullOrEmpty(_settings.TelemetryInstallId))
            _settings.TelemetryInstallId = Guid.NewGuid().ToString("N");
        return _settings.TelemetryInstallId;
    }

    /// <summary>Records the user's first-run telemetry/bug-report choices (independently), applies them
    /// live to the gate and senders, reflects them in the Settings toggles, and persists. Marks the
    /// consent prompt as shown so it is never displayed again, regardless of the choices.</summary>
    public async Task MarkTelemetryConsentAsync(bool telemetryEnabled, bool bugReportingEnabled)
    {
        _settings.TelemetryConsentPromptShown = true;
        _settings.TelemetryEnabled = telemetryEnabled;
        _settings.BugReportingEnabled = bugReportingEnabled;
        OnPropertyChanged(nameof(TelemetryConsentPromptShown));

        string installId = EnsureTelemetryInstallId();
        Yagu.Services.Telemetry.TelemetryGate.TelemetryEnabled = telemetryEnabled;
        Yagu.Services.Telemetry.TelemetryGate.BugReportingEnabled = bugReportingEnabled;
        if (telemetryEnabled)
            Yagu.Services.Telemetry.TelemetryService.Instance.Initialize(installId);
        if (bugReportingEnabled)
            Yagu.Services.Telemetry.BugReportService.Instance.Initialize(installId);

        // Reflect into the Settings-panel toggles without re-triggering a persist per toggle.
        _telemetryInitialized = false;
        TelemetryEnabledSetting = telemetryEnabled;
        BugReportingEnabledSetting = bugReportingEnabled;
        _telemetryInitialized = true;

        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Shows the telemetry/bug-reporting consent prompt again on the next launch while
    /// preserving the currently selected telemetry and bug-reporting settings until then.</summary>
    public async Task ResetTelemetryConsentPromptAsync()
    {
        if (await PersistPromptResetAsync(settings => settings.TelemetryConsentPromptShown = false)
            .ConfigureAwait(true))
            OnPropertyChanged(nameof(TelemetryConsentPromptShown));
    }

    /// <summary>Persists the contact email the user supplied in a bug report so it pre-fills next time.</summary>
    public Task SetBugReportContactEmailAsync(string email)
    {
        BugReportContactEmail = (email ?? string.Empty).Trim();
        return Task.CompletedTask;
    }
}
