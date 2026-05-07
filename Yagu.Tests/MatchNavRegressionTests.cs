using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace Yagu.Tests;

/// <summary>
/// UI regression test that exercises the full match-navigation flow:
///   1. Launches Yagu via <c>scripts\test-match-nav.ps1</c> against a test
///      directory and query, capturing screenshots after each "Next match"
///      click into a randomly-named output directory.
///   2. Invokes <c>scripts\count-red-pixels.ps1</c> against the screenshot
///      directory to count OrangeRed-ish pixels (the active-match highlight
///      colour) in every <c>03-match-*.png</c> screenshot.
///   3. Fails if ANY <c>03-match-*</c> screenshot contains fewer than
///      <see cref="MinRedPixelsPerMatch"/> red pixels — the highlight is
///      considered "missing" / off-screen at that point.
///
/// This test is heavy: it actually launches the WinUI 3 desktop app, drives
/// the UI via UIAutomation, and takes full-screen screenshots. It must run
/// on Windows in an interactive session. To enable it, set the env var
/// <c>YAGU_RUN_UI_REGRESSION=1</c>. Without that, the test exits early with
/// a "skipped" message so CI runs that lack a desktop session don't fail.
/// </summary>
public sealed class MatchNavRegressionTests
{
    private const int MinRedPixelsPerMatch = 50;

    private readonly ITestOutputHelper _output;

    public MatchNavRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void MatchNav_AllScreenshotsContainHighlight()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: UI regression test requires Windows.");
            return;
        }

        var optIn = Environment.GetEnvironmentVariable("YAGU_RUN_UI_REGRESSION");
        if (string.IsNullOrEmpty(optIn) || optIn == "0")
        {
            _output.WriteLine("Skipped: set YAGU_RUN_UI_REGRESSION=1 to run the UI regression test.");
            return;
        }

        var solutionRoot = FindSolutionRoot();
        var scriptsDir = Path.Combine(solutionRoot, "scripts");
        var navScript = Path.Combine(scriptsDir, "test-match-nav.ps1");
        var redCountScript = Path.Combine(scriptsDir, "count-red-pixels.ps1");
        Assert.True(File.Exists(navScript), $"Missing nav script: {navScript}");
        Assert.True(File.Exists(redCountScript), $"Missing red-count script: {redCountScript}");

        var yaguExe = Path.Combine(
            solutionRoot, "Yagu", "bin", "Debug", "net10.0-windows10.0.19041.0", "Yagu.exe");
        Assert.True(File.Exists(yaguExe),
            $"Yagu Debug build not found at {yaguExe}. Run 'dotnet build Yagu/Yagu.csproj -c Debug' first.");

        // Random per-run screenshot directory under TestResults so concurrent runs
        // don't collide and old screenshots don't pollute the red-pixel scan.
        var runId = "MatchNavRun-" + Guid.NewGuid().ToString("N")[..8];
        var screenshotDir = Path.Combine(
            solutionRoot, "TestResults", "MatchNavScreenshots", runId);
        Directory.CreateDirectory(screenshotDir);
        _output.WriteLine($"Screenshot dir: {screenshotDir}");

        try
        {
            // Step 1: drive the UI and capture screenshots.
            // The nav script can legitimately run for a long time depending on the
            // file corpus, so use an idle-output timeout instead of a wall-clock
            // one: as long as the script keeps emitting progress lines we keep
            // waiting; only abort if it stalls (no new stdout/stderr) for 3 min.
            RunPowerShellScript(
                navScript,
                $"-ScreenshotDir \"{screenshotDir}\"",
                timeout: Timeout.InfiniteTimeSpan,
                idleTimeout: TimeSpan.FromMinutes(5));

            // Step 2: red-pixel scan over 03-match-*.png. Use Threshold = MinRedPixelsPerMatch - 1
            // so the script only emits rows for screenshots whose count is BELOW the floor;
            // any output row therefore indicates a regression.
            var thresholdBelow = MinRedPixelsPerMatch - 1;
            var redCountOutput = RunPowerShellScript(
                redCountScript,
                $"-Directory \"{screenshotDir}\" -Pattern \"03-match-*.png\" -Threshold {thresholdBelow}",
                timeout: TimeSpan.FromMinutes(5));

            _output.WriteLine("count-red-pixels output:");
            _output.WriteLine(redCountOutput);

            // Parse failing screenshots out of the script output. The script emits
            // PSObject lines that, when echoed, take the form:
            //   RedPixels Path
            //   --------- ----
            //          12 D:\...\03-match-05.png
            // We grep for any line ending in "03-match-*.png" with a leading
            // RedPixels integer < MinRedPixelsPerMatch.
            var failures = ParseFailingScreenshots(redCountOutput, MinRedPixelsPerMatch);

            // Sanity check: we must have produced at least one 03-match-*.png. Otherwise
            // the UI script itself failed silently (e.g. preview never loaded) and the
            // red-pixel scan trivially passes.
            var matchScreenshots = Directory.GetFiles(screenshotDir, "03-match-*.png");
            Assert.True(matchScreenshots.Length > 0,
                $"No 03-match-*.png screenshots produced in {screenshotDir}. " +
                "The UI test script likely failed to navigate matches. Check screenshot dir " +
                "for '01-after-search.png' and '02-preview-loaded.png' for diagnosis.");
            _output.WriteLine($"Screenshots produced: {matchScreenshots.Length}");

            if (failures.Count > 0)
            {
                var lines = failures.Select(f =>
                    $"  • {Path.GetFileName(f.Path)}: {f.RedPixels} red pixels (< {MinRedPixelsPerMatch})");
                Assert.Fail(
                    $"{failures.Count} of {matchScreenshots.Length} match-nav screenshots had " +
                    $"fewer than {MinRedPixelsPerMatch} red highlight pixels — the active-match " +
                    $"highlight was likely not visible in those frames:\n" +
                    string.Join("\n", lines) +
                    $"\n\nReview the screenshots in {screenshotDir} to confirm.");
            }
        }
        catch
        {
            _output.WriteLine($"Test failed; screenshot dir preserved at {screenshotDir} for inspection.");
            throw;
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private string RunPowerShellScript(
        string scriptPath,
        string scriptArgs,
        TimeSpan timeout,
        TimeSpan? idleTimeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            // -NoProfile keeps things deterministic; -File runs the script directly.
            // Quoting: scriptArgs is appended raw — callers must quote paths.
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {scriptArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _output.WriteLine($"$ pwsh -File {Path.GetFileName(scriptPath)} {scriptArgs}");
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh.");

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var lastOutputTicks = Environment.TickCount64;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastOutputTicks, Environment.TickCount64);
            lock (stdout) stdout.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastOutputTicks, Environment.TickCount64);
            lock (stderr) stderr.AppendLine(e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited;
        if (idleTimeout is { } idle)
        {
            // Idle-based wait: keep polling until the process exits, the wall-clock
            // timeout elapses (if finite), or the time since the last output line
            // exceeds idleTimeout.
            var idleMs = (long)idle.TotalMilliseconds;
            var hasWallClock = timeout != Timeout.InfiniteTimeSpan && timeout.TotalMilliseconds > 0;
            var deadlineTicks = hasWallClock
                ? Environment.TickCount64 + (long)timeout.TotalMilliseconds
                : long.MaxValue;
            exited = false;
            while (true)
            {
                if (proc.WaitForExit(500))
                {
                    exited = true;
                    break;
                }
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastOutputTicks) >= idleMs)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException(
                        $"PowerShell script '{Path.GetFileName(scriptPath)}' produced no output for " +
                        $"{idle.TotalSeconds:F0}s; aborting as stalled.");
                }
                if (now >= deadlineTicks)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException(
                        $"PowerShell script '{Path.GetFileName(scriptPath)}' did not complete within " +
                        $"{timeout.TotalSeconds:F0}s.");
                }
            }
        }
        else
        {
            var timeoutMs = timeout == Timeout.InfiniteTimeSpan
                ? -1
                : (int)timeout.TotalMilliseconds;
            exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"PowerShell script '{Path.GetFileName(scriptPath)}' did not complete within {timeout.TotalSeconds:F0}s.");
            }
        }
        // Drain remaining async output buffers.
        proc.WaitForExit();

        string outStr, errStr;
        lock (stdout) outStr = stdout.ToString();
        lock (stderr) errStr = stderr.ToString();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"PowerShell script '{Path.GetFileName(scriptPath)}' exited with code {proc.ExitCode}.\n" +
                $"--- STDOUT ---\n{outStr}\n--- STDERR ---\n{errStr}");
        }

        if (errStr.Length > 0)
            _output.WriteLine($"[stderr] {errStr}");

        return outStr;
    }

    private static List<(int RedPixels, string Path)> ParseFailingScreenshots(string output, int floor)
    {
        var failures = new List<(int, string)>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            // Skip table headers/separators emitted by Format-Table.
            if (line.StartsWith("RedPixels", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;

            // Expected: "<integer><spaces><path-ending-in-03-match-*.png>"
            int firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0) continue;
            var numText = line[..firstSpace];
            if (!int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var redCount))
                continue;
            var pathPart = line[(firstSpace + 1)..].Trim();
            if (pathPart.Length == 0) continue;
            var fileName = Path.GetFileName(pathPart);
            if (!fileName.StartsWith("03-match-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

            if (redCount < floor)
                failures.Add((redCount, pathPart));
        }
        return failures;
    }

    private static string FindSolutionRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(MatchNavRegressionTests).Assembly.Location)!;
        // bin/Debug/<tfm>/  →  walk up to the solution root that contains Yagu.sln
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate Yagu.sln walking up from {assemblyDir}.");
    }
}
