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
/// on Windows in an interactive session. It is tagged <c>Headed</c> (requires an
/// interactive desktop, so excluded from default headless/CI runs) and <c>Slow</c>.
/// Because it is also screenshot-fragile it keeps an extra opt-in: set the env var
/// <c>YAGU_RUN_UI_REGRESSION=1</c>. Without that, the test exits early with
/// a "skipped" message so CI runs that lack a desktop session don't fail.
/// </summary>
[Trait("Category", "Slow")]
[Trait("Category", "Headed")]
public sealed class MatchNavRegressionTests
{
    private const int MinRedPixelsPerMatch = 100;

    private readonly ITestOutputHelper _output;

    public MatchNavRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void MatchNav_AllScreenshotsContainHighlight()
    {
        if (!HeadedTestEnvironment.CanRun)
        {
            _output.WriteLine($"Skipped: {HeadedTestEnvironment.SkipReason}");
            return;
        }

        // Extra opt-in on top of the headed-capability gate: this test is screenshot-fragile (it fails
        // if any other Yagu instance is running, and depends on exact highlight pixels), so it is not
        // auto-run even on a capable desktop — a dev must set YAGU_RUN_UI_REGRESSION=1 deliberately.
        var optIn = Environment.GetEnvironmentVariable("YAGU_RUN_UI_REGRESSION");
        if (string.IsNullOrEmpty(optIn) || optIn == "0")
        {
            _output.WriteLine("Skipped: set YAGU_RUN_UI_REGRESSION=1 to run the screenshot-fragile match-nav UI test.");
            return;
        }

        var solutionRoot = FindSolutionRoot();
        var scriptsDir = Path.Combine(solutionRoot, "scripts");
        var navScript = Path.Combine(scriptsDir, "test-match-nav.ps1");
        var redCountScript = Path.Combine(scriptsDir, "count-red-pixels.ps1");
        Assert.True(File.Exists(navScript), $"Missing nav script: {navScript}");
        Assert.True(File.Exists(redCountScript), $"Missing red-count script: {redCountScript}");

        var yaguExe = Path.Combine(
            solutionRoot, "src", "Yagu", "bin", "Debug", "net10.0-windows10.0.19041.0", "Yagu.exe");
        Assert.True(File.Exists(yaguExe),
            $"Yagu Debug build not found at {yaguExe}. Run 'dotnet build src/Yagu/Yagu.csproj -c Debug' first.");

        // Random per-run screenshot directory under TestResults so concurrent runs
        // don't collide and old screenshots don't pollute the red-pixel scan.
        var runId = "MatchNavRun-" + Guid.NewGuid().ToString("N")[..8];
        var screenshotDir = Path.Combine(
            solutionRoot, "TestResults", "MatchNavScreenshots", runId);
        Directory.CreateDirectory(screenshotDir);
        _output.WriteLine($"Screenshot dir: {screenshotDir}");

        // Drive a SMALL, DETERMINISTIC corpus instead of the old default (all of C:\ for "a", which
        // produced tens of millions of matches over minutes, so the match-nav panel was never ready
        // when the script looked for the Next-match button). A handful of files with a known term
        // makes the search finish in well under a second and the match-nav flow reliable.
        // The term is deliberately long (14 chars) so the active-match highlight band is comfortably
        // above the MinRedPixelsPerMatch floor — a short term like "needle" renders only ~97 px.
        const string query = "yagumatchtoken";
        var corpusDir = Path.Combine(solutionRoot, "TestResults", "MatchNavCorpus", runId);
        Directory.CreateDirectory(corpusDir);
        CreateMatchNavCorpus(corpusDir, query);
        _output.WriteLine($"Corpus dir: {corpusDir}");

        try
        {
            // Step 1: drive the UI and capture screenshots against the deterministic corpus.
            RunPowerShellScript(
                navScript,
                $"-Directory \"{corpusDir}\" -Query \"{query}\" -ScreenshotDir \"{screenshotDir}\" " +
                "-SearchWaitSeconds 4 -MatchIterations 24",
                timeout: TimeSpan.FromMinutes(5));

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
        finally
        {
            // The corpus is disposable — always clean it up (screenshots are kept for diagnosis).
            try { Directory.Delete(corpusDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private string RunPowerShellScript(string scriptPath, string scriptArgs, TimeSpan timeout)
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
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"PowerShell script '{Path.GetFileName(scriptPath)}' did not complete within {timeout.TotalSeconds:F0}s.");
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

    // Builds a small, deterministic corpus so the match-nav UI flow is fast and reliable: four files
    // each with eight lines containing the query term (32 matches total), giving plenty to navigate
    // through and screenshot the active-match highlight for, without depending on a huge live search.
    private static void CreateMatchNavCorpus(string dir, string query)
    {
        for (int f = 1; f <= 4; f++)
        {
            var sb = new System.Text.StringBuilder();
            for (int ln = 1; ln <= 24; ln++)
            {
                sb.AppendLine(ln % 3 == 0
                    ? $"L{ln}: the {query} appears clearly here on line {ln} of file {f}."
                    : $"L{ln}: filler line {ln} of file {f} with no target term.");
            }
            File.WriteAllText(
                Path.Combine(dir, $"match-corpus-{f}.txt"),
                sb.ToString(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
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
