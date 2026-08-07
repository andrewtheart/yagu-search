using System.Runtime.CompilerServices;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Guarantees the Yagu test suite never reads or writes the real user settings at
/// <c>%APPDATA%\Yagu\settings.json</c>. The engine source is compiled directly into this test
/// assembly, so any code that constructs <see cref="SettingsService"/> without an explicit path —
/// and any child process a test launches, which inherits this environment — would otherwise operate
/// on the developer's own configuration.
///
/// This is a real incident guard, not a hypothetical one: a headed UI test wrote test values
/// (including the slow managed enumeration backend) straight into the user's settings file, and a
/// running Yagu instance then re-persisted them, silently degrading that user's searches.
/// </summary>
internal static class TestSettingsIsolation
{
    [ModuleInitializer]
    internal static void RedirectSettingsAwayFromRealUserSettings()
    {
        // Respect an explicit override (e.g. a test that points a child process at its own file).
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SettingsService.SettingsFileOverrideEnvVar)))
            return;

        string dir = Path.Combine(Path.GetTempPath(), "Yagu-tests");
        try { Directory.CreateDirectory(dir); } catch { /* best effort; SettingsService also creates the dir */ }

        // Per-process file so parallel test-host processes don't contend on one settings file.
        string path = Path.Combine(dir, $"yagu-test-settings-{Environment.ProcessId}.json");
        Environment.SetEnvironmentVariable(SettingsService.SettingsFileOverrideEnvVar, path);
    }
}
