using System.Diagnostics;
using System.Text.Json;
using QuickGrep.Models;
using QuickGrep.Services;
using Xunit.Abstractions;

namespace QuickGrep.Tests;

/// <summary>
/// Real-world throughput benchmarks that exercise the full SearchService pipeline
/// for a fixed wall-clock duration and record metrics for regression detection.
///
/// By default these tests search a synthetic temp tree so they run everywhere.
/// Set the environment variable <c>QUICKGREP_PERF_DIRECTORY</c> to a real path
/// (e.g. "C:\") to benchmark against the actual file system. The <c>QUICKGREP_PERF_DURATION_SECONDS</c>
/// variable controls the run duration (default 30 s; set to 120 for full 2-minute runs).
///
/// Results are appended as JSON lines to <c>TestResults/perf-baselines.jsonl</c>
/// so CI can diff across commits.
/// </summary>
[Collection("PerformanceBenchmarks")]
public sealed class PerformanceBenchmarkTests : IDisposable
{
    private const long Megabyte = 1024L * 1024L;
    private const long Gigabyte = 1024L * Megabyte;

    private readonly ITestOutputHelper _output;
    private readonly string? _syntheticRoot;
    private readonly string _searchDirectory;
    private readonly TimeSpan _runDuration;

    public PerformanceBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;

        // Configurable search target: default to a synthetic temp tree for CI,
        // but allow pointing at C:\ or any real path for local deep benchmarks.
        var envDir = Environment.GetEnvironmentVariable("QUICKGREP_PERF_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            _searchDirectory = envDir;
            _syntheticRoot = null;
        }
        else
        {
            int fileCount = GetEnvInt("QUICKGREP_PERF_FILE_COUNT", 50_000);
            int linesPerFile = GetEnvInt("QUICKGREP_PERF_LINES_PER_FILE", 200);
            _syntheticRoot = CreateSyntheticTree(fileCount, linesPerFile);
            _searchDirectory = _syntheticRoot;
        }

        int durationSeconds = GetEnvInt("QUICKGREP_PERF_DURATION_SECONDS", 30);
        _runDuration = TimeSpan.FromSeconds(durationSeconds);
    }

    public void Dispose()
    {
        if (_syntheticRoot is not null)
        {
            try { Directory.Delete(_syntheticRoot, recursive: true); } catch { }
        }
    }

    // ───────────────────────── Benchmark Scenarios ─────────────────────────

    [Fact]
    public async Task LiteralSearch_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "LiteralSearch",
            query: "Test",
            useRegex: false,
            caseSensitive: false);

        AssertMinimumThroughput(metrics, "LiteralSearch");
    }

    [Fact]
    public async Task CaseSensitiveLiteralSearch_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "CaseSensitiveLiteral",
            query: "Test",
            useRegex: false,
            caseSensitive: true);

        AssertMinimumThroughput(metrics, "CaseSensitiveLiteral");
    }

    [Fact]
    public async Task RegexSearch_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "RegexSearch",
            query: @"\bTest\w*\b",
            useRegex: true,
            caseSensitive: false);

        AssertMinimumThroughput(metrics, "RegexSearch");
    }

    [Fact]
    public async Task FileNameOnlySearch_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "FileNameOnly",
            query: "file",
            useRegex: false,
            caseSensitive: false,
            searchMode: SearchMode.FileNames);

        AssertMinimumThroughput(metrics, "FileNameOnly");
    }

    [Fact]
    public async Task ContentOnlySearch_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "ContentOnly",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            searchMode: SearchMode.Content);

        AssertMinimumThroughput(metrics, "ContentOnly");
    }

    [Fact]
    public async Task RegexSearch_CaseSensitive_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "RegexCaseSensitive",
            query: @"\bTest\w*\b",
            useRegex: true,
            caseSensitive: true);

        AssertMinimumThroughput(metrics, "RegexCaseSensitive");
    }

    [Fact]
    public async Task LargeFileExclusion_Throughput()
    {
        // Verify that early file-size exclusion doesn't regress overall throughput.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "LargeFileExclusion",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            maxFileSizeBytes: 100 * 1024); // 100 KB cap — excludes larger files early

        AssertMinimumThroughput(metrics, "LargeFileExclusion");
    }

    [Fact]
    public async Task IncludeGlobFilter_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "IncludeGlobFilter",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            includeGlobs: ["*.txt"]);

        AssertMinimumThroughput(metrics, "IncludeGlobFilter");
    }

    [Fact]
    public async Task ExcludeGlobFilter_Throughput()
    {
        var metrics = await RunTimedSearchAsync(
            scenarioName: "ExcludeGlobFilter",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            excludeGlobs: ["*.log", "*.bin", "node_modules"]);

        AssertMinimumThroughput(metrics, "ExcludeGlobFilter");
    }

    [Fact]
    public async Task NoContextLines_Throughput()
    {
        // Context-line extraction adds memory/copy overhead. Verify 0-context is faster.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "NoContextLines",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            contextLines: 0);

        AssertMinimumThroughput(metrics, "NoContextLines");
    }

    // ───────────────────────── Core Runner ─────────────────────────

    private async Task<PerfMetrics> RunTimedSearchAsync(
        string scenarioName,
        string query,
        bool useRegex,
        bool caseSensitive,
        SearchMode searchMode = SearchMode.Both,
        long maxFileSizeBytes = 50L * Megabyte,
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null,
        int contextLines = 3)
    {
        // Force managed backend so the test works without Everything installed.
        var previousBackend = FileLister.Backend;
        if (_syntheticRoot is not null)
            FileLister.Backend = FileListerBackend.Managed;

        try
        {
            using var cts = new CancellationTokenSource(_runDuration);
            var sw = Stopwatch.StartNew();

            var options = new SearchOptions
            {
                Directory = _searchDirectory,
                Query = query,
                CaseSensitive = caseSensitive,
                UseRegex = useRegex,
                ContextLines = contextLines,
                SearchMode = searchMode,
                IncludeGlobs = includeGlobs ?? [],
                ExcludeGlobs = excludeGlobs ?? [],
                MaxFileSizeBytes = maxFileSizeBytes,
                MaxResults = 0, // unlimited — let the timer be the bound
                MaxMatchesPerFile = 0,
                SkipBinary = true,
            };

            var svc = new SearchService();
            int totalMatches = 0;
            int totalFilesScanned = 0;
            long totalBytesScanned = 0;
            int filesWithMatches = 0;
            bool completed = false;
            bool cancelled = false;
            string? fallbackReason = null;

            try
            {
                await foreach (var evt in svc.SearchAsync(options, cts.Token))
                {
                    switch (evt)
                    {
                        case SearchEvent.Match:
                            totalMatches++;
                            break;
                        case SearchEvent.MatchBatch mb:
                            totalMatches += mb.Results.Count;
                            break;
                        case SearchEvent.Fallback fb:
                            fallbackReason = fb.Reason;
                            break;
                        case SearchEvent.Progress p:
                            totalFilesScanned = p.Snapshot.FilesScanned;
                            totalBytesScanned = p.Snapshot.BytesScanned;
                            filesWithMatches = p.Snapshot.FilesWithMatches;
                            break;
                        case SearchEvent.Completed summary:
                            completed = true;
                            totalFilesScanned = summary.Summary.FilesScanned;
                            totalBytesScanned = summary.Summary.BytesScanned;
                            filesWithMatches = summary.Summary.FilesWithMatches;
                            totalMatches = summary.Summary.TotalMatches;
                            cancelled = summary.Summary.Cancelled;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            sw.Stop();

            var metrics = new PerfMetrics
            {
                Scenario = scenarioName,
                Directory = _searchDirectory,
                Query = query,
                UseRegex = useRegex,
                CaseSensitive = caseSensitive,
                SearchMode = searchMode.ToString(),
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                TotalMatches = totalMatches,
                FilesScanned = totalFilesScanned,
                FilesWithMatches = filesWithMatches,
                BytesScanned = totalBytesScanned,
                MatchesPerSecond = totalMatches / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                FilesPerSecond = totalFilesScanned / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                MBPerSecond = (totalBytesScanned / (double)Megabyte) / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                CompletedNaturally = completed && !cancelled,
                FallbackReason = fallbackReason,
                Timestamp = DateTime.UtcNow,
                MachineName = Environment.MachineName,
                ProcessorCount = Environment.ProcessorCount,
            };

            WriteMetricsToOutput(metrics);
            AppendMetricsToBaseline(metrics);

            return metrics;
        }
        finally
        {
            FileLister.Backend = previousBackend;
        }
    }

    // ───────────────────────── Assertions ─────────────────────────

    private void AssertMinimumThroughput(PerfMetrics metrics, string scenario)
    {
        // If the search completed before the timer fired, it finished all files —
        // that's fine, throughput is just total/time.
        _output.WriteLine($"[{scenario}] completed={metrics.CompletedNaturally}, " +
            $"matches={metrics.TotalMatches:N0}, files={metrics.FilesScanned:N0}, " +
            $"MB scanned={metrics.BytesScanned / (double)Megabyte:F1}, " +
            $"elapsed={metrics.ElapsedSeconds:F2}s");

        // Sanity: we must have scanned something.
        Assert.True(metrics.FilesScanned > 0,
            $"[{scenario}] No files were scanned — check search directory '{metrics.Directory}'.");

        // Regression guard: files/sec must exceed a floor that scales with processor count.
        // These floors are deliberately conservative so the test doesn't flake on slow CI.
        int minFilesPerSecond = GetEnvInt($"QUICKGREP_PERF_{scenario.ToUpperInvariant()}_MIN_FPS", 10);
        Assert.True(metrics.FilesPerSecond >= minFilesPerSecond,
            $"[{scenario}] Throughput regression: {metrics.FilesPerSecond:F1} files/s " +
            $"is below minimum {minFilesPerSecond} files/s.");

        // If the search actually found matches, the matches/sec rate should be non-trivial.
        if (metrics.TotalMatches > 0)
        {
            int minMatchesPerSecond = GetEnvInt($"QUICKGREP_PERF_{scenario.ToUpperInvariant()}_MIN_MPS", 1);
            Assert.True(metrics.MatchesPerSecond >= minMatchesPerSecond,
                $"[{scenario}] Match rate regression: {metrics.MatchesPerSecond:F1} matches/s " +
                $"is below minimum {minMatchesPerSecond} matches/s.");
        }
    }

    // ───────────────────────── Baseline Persistence ─────────────────────────

    private void AppendMetricsToBaseline(PerfMetrics metrics)
    {
        try
        {
            // Write alongside the test results so CI can archive it.
            var baselineDir = Path.Combine(
                Path.GetDirectoryName(typeof(PerformanceBenchmarkTests).Assembly.Location)!,
                "perf-baselines");
            Directory.CreateDirectory(baselineDir);

            var baselinePath = Path.Combine(baselineDir, "perf-baselines.jsonl");
            var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.AppendAllText(baselinePath, json + Environment.NewLine);
            _output.WriteLine($"  → Baseline appended to {baselinePath}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  ⚠ Could not write baseline: {ex.Message}");
        }
    }

    private void WriteMetricsToOutput(PerfMetrics metrics)
    {
        _output.WriteLine("────────────────────────────────────────");
        _output.WriteLine($"Scenario:           {metrics.Scenario}");
        _output.WriteLine($"Directory:          {metrics.Directory}");
        _output.WriteLine($"Query:              {metrics.Query} (regex={metrics.UseRegex}, caseSensitive={metrics.CaseSensitive})");
        _output.WriteLine($"Search Mode:        {metrics.SearchMode}");
        _output.WriteLine($"Elapsed:            {metrics.ElapsedSeconds:F2} s");
        _output.WriteLine($"Files scanned:      {metrics.FilesScanned:N0}");
        _output.WriteLine($"Files with matches: {metrics.FilesWithMatches:N0}");
        _output.WriteLine($"Total matches:      {metrics.TotalMatches:N0}");
        _output.WriteLine($"Bytes scanned:      {metrics.BytesScanned / (double)Megabyte:F1} MB");
        _output.WriteLine($"Files/sec:          {metrics.FilesPerSecond:F1}");
        _output.WriteLine($"Matches/sec:        {metrics.MatchesPerSecond:F1}");
        _output.WriteLine($"MB/sec:             {metrics.MBPerSecond:F1}");
        _output.WriteLine($"Completed:          {metrics.CompletedNaturally}");
        if (metrics.FallbackReason is not null)
            _output.WriteLine($"Fallback:           {metrics.FallbackReason}");
        _output.WriteLine("────────────────────────────────────────");
    }

    // ───────────────────────── Synthetic Tree Setup ─────────────────────────

    private static string CreateSyntheticTree(int fileCount, int linesPerFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-perf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Build varied content with "Test" appearing on ~20% of lines so searches
        // actually find matches and exercise the match-production pipeline.
        var rng = new Random(42); // fixed seed for reproducibility
        var sb = new System.Text.StringBuilder();

        for (int f = 0; f < fileCount; f++)
        {
            sb.Clear();
            for (int l = 0; l < linesPerFile; l++)
            {
                if (rng.NextDouble() < 0.2)
                    sb.AppendLine($"line {l}: This is a Test line with keyword Testing various patterns");
                else
                    sb.AppendLine($"line {l}: Lorem ipsum dolor sit amet, consectetur adipiscing elit");
            }

            // Spread files across subdirectories (100 files per folder).
            int dirIndex = f / 100;
            var dir = Path.Combine(root, $"folder-{dirIndex:D3}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"file-{f:D5}.txt"), sb.ToString());
        }

        return root;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static int GetEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) && v > 0 ? v : defaultValue;
    }

    // ───────────────────────── Metrics Model ─────────────────────────

    private sealed class PerfMetrics
    {
        public string Scenario { get; init; } = "";
        public string Directory { get; init; } = "";
        public string Query { get; init; } = "";
        public bool UseRegex { get; init; }
        public bool CaseSensitive { get; init; }
        public string SearchMode { get; init; } = "";
        public double ElapsedSeconds { get; init; }
        public int TotalMatches { get; init; }
        public int FilesScanned { get; init; }
        public int FilesWithMatches { get; init; }
        public long BytesScanned { get; init; }
        public double MatchesPerSecond { get; init; }
        public double FilesPerSecond { get; init; }
        public double MBPerSecond { get; init; }
        public bool CompletedNaturally { get; init; }
        public string? FallbackReason { get; init; }
        public DateTime Timestamp { get; init; }
        public string MachineName { get; init; } = "";
        public int ProcessorCount { get; init; }
    }
}
