namespace Yagu.Services.Telemetry;

/// <summary>
/// Single decision point for whether Yagu may emit telemetry or offer a bug report. Mirrors
/// <see cref="Yagu.Services.Ocr.OcrDownloadGate"/>: pure managed, no UI dependency, so it links
/// cleanly into the test project. The two consents are independent — a user may enable telemetry
/// without bug reporting, or vice versa.
/// <para>
/// The flags are seeded from persisted settings at startup and updated when the user changes them
/// (first-run consent dialog or the Settings panel). Headless hosts (CLI, tests) set
/// <see cref="Headless"/> so nothing is ever sent and no modal is ever shown, even if the persisted
/// flags happen to be on.
/// </para>
/// </summary>
public static class TelemetryGate
{
    /// <summary>True when the user has consented to silent, anonymized performance + error
    /// telemetry. Seeded from <c>AppSettings.TelemetryEnabled</c>.</summary>
    public static volatile bool TelemetryEnabled;

    /// <summary>True when the user has consented to the bug-report flow (a reviewable dialog shown
    /// when a critical error occurs). Seeded from <c>AppSettings.BugReportingEnabled</c>.</summary>
    public static volatile bool BugReportingEnabled;

    /// <summary>Set by the CLI/headless host. When true, all telemetry and bug-report behavior is
    /// suppressed regardless of the consent flags (there is no UI to review a submission, and a
    /// scripted/headless run must never phone home).</summary>
    public static volatile bool Headless;

    /// <summary>True when anonymized telemetry should actually be sent: the user consented, the
    /// endpoint is configured, and we are not headless.</summary>
    public static bool ShouldSendTelemetry => TelemetryEnabled && !Headless && TelemetryConfig.IsConfigured;

    /// <summary>True when a critical error should offer the user a bug report: the user consented,
    /// the endpoint is configured, and we are not headless.</summary>
    public static bool ShouldOfferBugReport => BugReportingEnabled && !Headless && TelemetryConfig.IsConfigured;
}
