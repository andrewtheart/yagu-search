using System.Diagnostics;
using Xunit.Abstractions;

namespace Yagu.Tests;

/// <summary>
/// UI regression test for the multiline (cross-line) search GUI. Launches the WinUI 3 app via
/// <c>scripts\test-multiline-gui.ps1</c> and drives it through UIAutomation to verify:
///   • TC-G01 — the inline <c>\n</c> MultilineToggle exists, has the <c>\n</c> glyph, sits between the
///     Regex and Exact toggles, and starts Off by default.
///   • TC-G02 — the Advanced Options "Match across lines" checkbox and its ". matches newlines"
///     sub-toggle exist, and the sub-toggle is disabled while multiline is off, enabled once it is on.
///   • TC-G06 — the inline toggle and the Advanced Options checkbox stay in sync (two-way binding).
///
/// Heavy: it actually launches the desktop app and drives UIAutomation, so it must run on Windows in
/// an interactive session with no other Yagu instance running (single-instance would hijack the
/// launch). It is in the <c>Headed</c> category and gated at runtime by
/// <see cref="HeadedTestEnvironment"/>: it **auto-runs** on an interactive Windows desktop and
/// **auto-skips** in CI / non-interactive / non-Windows environments (so a normal <c>dotnet test</c>
/// on a dev box exercises it, while CI quietly skips it). The script snapshots/restores
/// <c>settings.json</c> so the run never changes the machine's saved defaults.
///
/// Script exit codes: 0 = all checks pass, 1 = one or more failed, 2 = skipped (environment not clean).
/// </summary>
[Trait("Category", "Headed")]
public sealed class MultilineGuiRegressionTests
{
    private readonly ITestOutputHelper _output;

    public MultilineGuiRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MultilineToggles_BehaveCorrectlyInTheGui()
    {
        if (!HeadedTestEnvironment.CanRun)
        {
            _output.WriteLine($"Skipped: {HeadedTestEnvironment.SkipReason}");
            return;
        }

        var root = FindSolutionRoot();
        var script = Path.Combine(root, "scripts", "test-multiline-gui.ps1");
        Assert.True(File.Exists(script), $"Missing GUI test script: {script}");

        var yaguExe = Path.Combine(
            root, "src", "Yagu", "bin", "Debug", "net10.0-windows10.0.19041.0", "Yagu.exe");
        if (!File.Exists(yaguExe))
        {
            // Auto-run is opportunistic: a normal `dotnet test` on a dev box may not have a Debug build.
            // Skip (don't fail) — build with `dotnet build src/Yagu/Yagu.csproj -c Debug` to exercise it.
            _output.WriteLine($"Skipped: Yagu Debug build not found at {yaguExe} (build it to run the GUI test).");
            return;
        }

        var (exitCode, stdout, stderr) = RunPowerShellScript(
            script, $"-Exe \"{yaguExe}\"", TimeSpan.FromMinutes(3));

        _output.WriteLine(stdout);
        if (stderr.Length > 0) _output.WriteLine($"[stderr] {stderr}");

        switch (exitCode)
        {
            case 0:
                Assert.Contains("ALL PASS", stdout);
                break;
            case 2:
                // Environment wasn't clean (another Yagu running, or single-instance handoff). Treat
                // as skipped rather than failed — this is environmental, not a regression.
                _output.WriteLine("Skipped: GUI test environment not clean (see output).");
                break;
            default:
                Assert.Fail($"Multiline GUI regression checks failed (exit {exitCode}).\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}");
                break;
        }
    }

    private (int ExitCode, string Stdout, string Stderr) RunPowerShellScript(string scriptPath, string scriptArgs, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {scriptArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh.");

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"'{Path.GetFileName(scriptPath)}' did not complete within {timeout.TotalSeconds:F0}s.");
        }
        proc.WaitForExit();

        string outStr, errStr;
        lock (stdout) outStr = stdout.ToString();
        lock (stderr) errStr = stderr.ToString();
        return (proc.ExitCode, outStr, errStr);
    }

    private static string FindSolutionRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(MultilineGuiRegressionTests).Assembly.Location)!;
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not locate Yagu.sln walking up from {assemblyDir}.");
    }
}
