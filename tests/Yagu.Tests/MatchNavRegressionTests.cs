using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Headed UI regressions for preview occurrence pagination across deterministic corpora: mixed terms,
/// large random-text files, many single-line files tens of thousands of characters long, and dense
/// same-line matches. Every Next click must advance the live <c>Occurrence x/y</c> label exactly once.
/// The screenshot analyzer then requires one term-sized OrangeRed component inside the preview, or up
/// to two independently validated components for multiline matches. Extra highlighted text, gutter
/// spill, preview-edge contact, and line-wide boxes are rejected.
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
    private readonly ITestOutputHelper _output;

    public MatchNavRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> ScenarioIds()
        => MatchNavTestCorpus.Scenarios.Select(scenario => new object[] { scenario.Id });

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void MatchNav_DiverseCorpus_PaginatesAndBoxesOnlyTheActiveTerm(string scenarioId)
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

        MatchNavScenario scenario = MatchNavTestCorpus.Get(scenarioId);
        var runId = $"{scenario.Id}-{Guid.NewGuid().ToString("N")[..8]}";
        var screenshotDir = Path.Combine(
            solutionRoot, "TestResults", "MatchNavScreenshots", runId);
        Directory.CreateDirectory(screenshotDir);
        _output.WriteLine($"Screenshot dir: {screenshotDir}");

        var corpusDir = Path.Combine(solutionRoot, "TestResults", "MatchNavCorpus", runId);
        Directory.CreateDirectory(corpusDir);
        MatchNavTestCorpus.Create(corpusDir, scenario);
        _output.WriteLine($"Corpus dir: {corpusDir}");
        _output.WriteLine(
            $"Scenario: {scenario.Id}; query='{scenario.Query}'; files={scenario.FileCount}; " +
            $"expected occurrences={scenario.ExpectedMatches}; regex={scenario.UseRegex}; " +
            $"exact={scenario.ExactMatch}; multiline={scenario.Multiline}");

        using var settingsScope = PreviewTestSettingsScope.Create();
        HashSet<int> processIdsBefore = CaptureDebugYaguProcessIds(yaguExe);

        try
        {
            RunPowerShellScript(
                navScript,
                $"-Directory \"{corpusDir}\" -Query \"{scenario.Query}\" " +
                $"-ScreenshotDir \"{screenshotDir}\" -SearchWaitSeconds {scenario.SearchWaitSeconds} " +
                $"-MatchIterations {scenario.MatchIterations} -MaxFiles {scenario.FileCount} " +
                $"-ExpectedFiles {scenario.FileCount} -UseRegex {Convert.ToInt32(scenario.UseRegex)} " +
                $"-ExactMatch {Convert.ToInt32(scenario.ExactMatch)} -Multiline {Convert.ToInt32(scenario.Multiline)}",
                timeout: TimeSpan.FromMinutes(8),
                settingsFilePath: settingsScope.SettingsPath);

            string manifestPath = Path.Combine(screenshotDir, "navigation.tsv");
            Assert.True(File.Exists(manifestPath), $"Navigation manifest was not created: {manifestPath}");
            NavigationRow[] rows = ReadNavigationManifest(manifestPath);
            string[] matchScreenshots = Directory.GetFiles(screenshotDir, "03-match-*.png");

            Assert.True(matchScreenshots.Length >= scenario.MinimumScreenshots,
                $"Scenario '{scenario.Id}' produced only {matchScreenshots.Length} match screenshots; " +
                $"expected at least {scenario.MinimumScreenshots}. Artifacts: {screenshotDir}");
            Assert.Equal(matchScreenshots.Length, rows.Length);
            Assert.All(rows, row => Assert.Equal(scenario.ExpectedMatches, row.Total));
            Assert.All(rows, row => Assert.Equal(scenario.FileCount, row.Files));
            Assert.All(rows, row => Assert.True(row.ContextCompared,
                $"Occurrence {row.Occurrence} did not compare left/right context."));
            Assert.All(rows, row => Assert.Equal(scenario.ExpectedHighlightLength, row.ContextMatch.Length));
            Assert.Equal(
                Enumerable.Range(2, rows.Length).ToArray(),
                rows.Select(row => row.Occurrence).ToArray());

            int maximumHighlightComponents = scenario.Multiline ? 2 : 1;
            string geometryOutput = RunPowerShellScript(
                redCountScript,
                $"-Directory \"{screenshotDir}\" -Pattern \"03-match-*.png\" " +
                $"-Manifest \"{manifestPath}\" -ExpectedTermLength {scenario.ExpectedHighlightLength} " +
                $"-MaximumHighlightComponents {maximumHighlightComponents} -StrictGeometry",
                timeout: TimeSpan.FromMinutes(5),
                settingsFilePath: settingsScope.SettingsPath);

            _output.WriteLine("Strict highlight geometry output:");
            _output.WriteLine(geometryOutput);
            Assert.Contains("GEOMETRY\tPASS", geometryOutput);
            Assert.DoesNotContain("GEOMETRY\tFAIL", geometryOutput);
        }
        catch
        {
            _output.WriteLine($"Test failed; screenshot dir preserved at {screenshotDir} for inspection.");
            throw;
        }
        finally
        {
            StopDebugYaguCreatedAfter(yaguExe, processIdsBefore);
            try { Directory.Delete(corpusDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private string RunPowerShellScript(
        string scriptPath, string scriptArgs, TimeSpan timeout, string? settingsFilePath = null)
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

        // The launched Yagu.exe inherits this, so the app under test reads the throwaway settings file
        // instead of the developer's own configuration.
        if (settingsFilePath is not null)
            psi.Environment[SettingsService.SettingsFileOverrideEnvVar] = settingsFilePath;

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

    private static NavigationRow[] ReadNavigationManifest(string path)
    {
        return File.ReadLines(path)
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
        {
            string[] columns = line.Split('\t');
            Assert.True(columns.Length >= 16, $"Malformed navigation manifest row: {line}");
            return new NavigationRow(
                Screenshot: columns[0],
                Occurrence: int.Parse(columns[1]),
                Total: int.Parse(columns[2]),
                Files: int.Parse(columns[3]),
                ContextCompared: string.Equals(columns[9], "PASS", StringComparison.Ordinal),
                ContextMatch: Encoding.UTF8.GetString(Convert.FromBase64String(columns[14])));
        }).ToArray();
    }

    private static HashSet<int> CaptureDebugYaguProcessIds(string executablePath)
    {
        var processIds = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName("Yagu"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                        processIds.Add(process.Id);
                }
                catch { }
            }
        }
        return processIds;
    }

    private static void StopDebugYaguCreatedAfter(string executablePath, IReadOnlySet<int> processIdsBefore)
    {
        foreach (Process process in Process.GetProcessesByName("Yagu"))
        {
            using (process)
            {
                try
                {
                    if (!processIdsBefore.Contains(process.Id)
                        && string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5_000);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Deterministic settings for the app under test, written to a throwaway file and handed to the child
    /// process via <see cref="SettingsService.SettingsFileOverrideEnvVar"/>. This must NEVER touch the real
    /// user settings: an earlier version wrote %APPDATA%\Yagu\settings.json directly and a running app
    /// re-persisted the test values, silently switching the developer's own searches to the slow managed
    /// enumeration backend.
    /// </summary>
    private sealed class PreviewTestSettingsScope : IDisposable
    {
        private readonly string _directory;

        private PreviewTestSettingsScope(string directory, string settingsPath)
        {
            _directory = directory;
            SettingsPath = settingsPath;
        }

        public string SettingsPath { get; }

        public static PreviewTestSettingsScope Create()
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "yagu-match-nav-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.json");

            var settings = new JsonObject
            {
                ["PreviewWordWrap"] = false,
                ["PreviewWrapModeIndex"] = 2,
                ["PreviewOverlayColor"] = "#FFFF4500",
                ["PreviewMatchTextColor"] = "#FFFFD700",
                ["PreviewGutterContextColor"] = "#FF9CDCFE",
                ["PreviewGutterMatchColor"] = "#FF9CDCFE",
                ["PreviewMatchLineColor"] = "#FFFFFFFF",
                ["PreviewTextFontFamily"] = "Consolas",
                ["PreviewTextFontSize"] = 14,
                ["HasChosenQueryMode"] = true,
                ["LastQueryModeIsSemantic"] = false,
                ["DefaultToTraditionalSearchMode"] = true,
                // The corpus is created moments before the run, so Everything has not indexed it yet.
                ["FileListerBackendIndex"] = 3,
                ["EnableContentIndex"] = false,
                ["UseContentIndexByDefault"] = false,
            };
            File.WriteAllText(
                path,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new PreviewTestSettingsScope(directory, path);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private sealed record NavigationRow(
        string Screenshot,
        int Occurrence,
        int Total,
        int Files,
        bool ContextCompared,
        string ContextMatch);

    private static string FindSolutionRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(MatchNavRegressionTests).Assembly.Location)!;
        // bin/Debug/<tfm>/  →  walk up to the solution root that contains Yagu.slnx
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate Yagu.slnx walking up from {assemblyDir}.");
    }
}
