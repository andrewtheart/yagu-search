using System.Runtime.CompilerServices;
using Yagu.Services;

namespace Yagu.Benchmarks;

/// <summary>
/// Keeps the benchmark host (which compiles the engine source, including <see cref="LogService"/>)
/// from writing to the real application log at <c>%APPDATA%\Yagu\yagu.log</c>. Mirrors
/// <c>Yagu.Tests.TestLogIsolation</c>: a module initializer points
/// <see cref="LogService.LogFileOverrideEnvVar"/> at an isolated per-process temp file before the
/// lazy <see cref="LogService.Instance"/> singleton is first created.
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void RedirectLogAwayFromRealAppLog()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LogService.LogFileOverrideEnvVar)))
            return;

        string dir = Path.Combine(Path.GetTempPath(), "Yagu-benchmarks");
        try { Directory.CreateDirectory(dir); } catch { /* best effort; LogService also creates the dir */ }

        string path = Path.Combine(dir, $"yagu-bench-{Environment.ProcessId}.log");
        Environment.SetEnvironmentVariable(LogService.LogFileOverrideEnvVar, path);
    }
}
