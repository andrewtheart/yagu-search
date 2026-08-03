using System.Runtime.CompilerServices;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Guarantees the Yagu test suite never writes to the real application log at
/// <c>%APPDATA%\Yagu\yagu.log</c>. The engine source is compiled directly into this test
/// assembly, so any code a test exercises that logs through the process-wide
/// <see cref="LogService.Instance"/> singleton (e.g. via <c>YaguLog.For(...)</c>) would otherwise
/// interleave test noise into the running app's diagnostic log.
///
/// A <see cref="ModuleInitializerAttribute"/> runs at assembly load — before any test code and
/// before the lazy <see cref="LogService.Instance"/> is first created — so pointing
/// <see cref="LogService.LogFileOverrideEnvVar"/> at an isolated per-process temp file here means
/// the singleton is constructed against that path.
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void RedirectLogAwayFromRealAppLog()
    {
        // Respect an explicit override (e.g. CI wanting logs in a known location).
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LogService.LogFileOverrideEnvVar)))
            return;

        string dir = Path.Combine(Path.GetTempPath(), "Yagu-tests");
        try { Directory.CreateDirectory(dir); } catch { /* best effort; LogService also creates the dir */ }

        // Per-process file so parallel test-host processes don't contend on one log.
        string path = Path.Combine(dir, $"yagu-test-{Environment.ProcessId}.log");
        Environment.SetEnvironmentVariable(LogService.LogFileOverrideEnvVar, path);
    }
}
