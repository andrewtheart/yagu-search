using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Yagu.Models;
using Yagu.Services;
using Xunit.Abstractions;

namespace Yagu.Tests;

/// <summary>
/// Real-world throughput benchmarks that exercise the full SearchService pipeline
/// for a fixed wall-clock duration and record metrics for regression detection.
///
/// By default these tests search a synthetic temp tree so they run everywhere.
/// Set the environment variable <c>YAGU_PERF_DIRECTORY</c> to a real path
/// (e.g. "C:\") to benchmark against the actual file system. The <c>YAGU_PERF_DURATION_SECONDS</c>
/// variable controls the run duration (default 30 s; set to 120 for full 2-minute runs).
///
/// Results are appended as JSON lines to <c>Yagu.Benchmarks/results/perf-baselines.jsonl</c>
/// so CI can diff across commits.
/// </summary>
[Collection("PerformanceBenchmarks")]
[ExcludeFromCodeCoverage]
[Trait("Category", "Slow")]
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

        // Clean up leftover temp trees from previous runs that were terminated early.
        CleanupStaleTempTrees();

        // Configurable search target: default to a synthetic temp tree for CI,
        // but allow pointing at C:\ or any real path for local deep benchmarks.
        var envDir = Environment.GetEnvironmentVariable("YAGU_PERF_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            _searchDirectory = envDir;
            _syntheticRoot = null;
        }
        else
        {
            int fileCount = GetEnvInt("YAGU_PERF_FILE_COUNT", 50_000);
            int linesPerFile = GetEnvInt("YAGU_PERF_LINES_PER_FILE", 200);
            _syntheticRoot = CreateSyntheticTree(fileCount, linesPerFile);
            _searchDirectory = _syntheticRoot;
        }

        int durationSeconds = GetEnvInt("YAGU_PERF_DURATION_SECONDS", 30);
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

    [Fact]
    public async Task NoMatchSearch_Throughput()
    {
        // Worst-case: scans every byte of every file, produces zero results.
        // Catches overhead in the "no match" fast path.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "NoMatch",
            query: "ZZZZZ_DEFINITELY_NOT_IN_ANY_FILE_ZZZZZ",
            useRegex: false,
            caseSensitive: false);

        AssertMinimumThroughput(metrics, "NoMatch");
        // No matches expected
        Assert.Equal(0, metrics.TotalMatches);
    }

    [Fact]
    public async Task HighMatchDensity_Throughput()
    {
        // Every line matches — stress-tests match production, channel throughput,
        // and allocation rate. Uses a dedicated tree where every line contains "line".
        var highDensityRoot = CreateHighDensityTree(fileCount: 5_000, linesPerFile: 200);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "HighMatchDensity",
                query: "line",
                useRegex: false,
                caseSensitive: false,
                overrideDirectory: highDensityRoot);

            AssertMinimumThroughput(metrics, "HighMatchDensity");
            // Every line should match
            Assert.True(metrics.TotalMatches >= metrics.FilesScanned,
                $"[HighMatchDensity] Expected at least 1 match per file but got {metrics.TotalMatches:N0} matches for {metrics.FilesScanned:N0} files.");
        }
        finally
        {
            try { Directory.Delete(highDensityRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TinyFiles_Throughput()
    {
        // Tiny files (200 × ~50 KB). High file-count-to-byte ratio stresses
        // file-open overhead, metadata lookups, and per-file pipeline setup.
        var root = CreateSizedFileTree("tiny", fileCount: 200, targetSizeBytes: 50 * 1024);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "TinyFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "TinyFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SmallFiles_Throughput()
    {
        // Small files (20 × ~10 MB). Exercises buffered reading without hitting
        // mmap thresholds.
        var root = CreateSizedFileTree("small", fileCount: 20, targetSizeBytes: 10 * Megabyte);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "SmallFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                maxFileSizeBytes: 20 * Megabyte,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "SmallFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MediumFiles_Throughput()
    {
        // Medium files (10 × ~50 MB). Starts exercising mmap and streaming paths.
        var root = CreateSizedFileTree("medium", fileCount: 10, targetSizeBytes: 50 * Megabyte);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "MediumFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                maxFileSizeBytes: 100 * Megabyte,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "MediumFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LargeFiles_Throughput()
    {
        // Large files (5 × ~100 MB). Heavy per-file I/O, stresses mmap throughput.
        var root = CreateSizedFileTree("large", fileCount: 5, targetSizeBytes: 100 * Megabyte);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "LargeFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                maxFileSizeBytes: 200 * Megabyte,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "LargeFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task VeryLargeFiles_Throughput()
    {
        // Very large files (3 × ~200 MB). Tests sustained sequential I/O throughput.
        var root = CreateSizedFileTree("vlarge", fileCount: 3, targetSizeBytes: 200 * Megabyte);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "VeryLargeFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                maxFileSizeBytes: 300 * Megabyte,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "VeryLargeFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task VeryVeryLargeFiles_Throughput()
    {
        // Very very large files (2 × ~500 MB). Extreme per-file size, tests
        // memory pressure, streaming, and GC behaviour on huge buffers.
        var root = CreateSizedFileTree("vvlarge", fileCount: 2, targetSizeBytes: 500 * Megabyte);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "VeryVeryLargeFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                maxFileSizeBytes: 600 * Megabyte,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "VeryVeryLargeFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MixedFileSizes_Throughput()
    {
        // Hard-coded mix of all size categories so results are comparable across runs.
        // File sizes (in bytes) are deterministic — the same files are created every time.
        var root = CreateMixedSizeTree();
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "MixedFileSizes",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                maxFileSizeBytes: 600 * Megabyte,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "MixedFileSizes");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeepDirectoryTree_Throughput()
    {
        // Many nested folders, few files each. Isolates directory traversal overhead.
        var deepRoot = CreateDeepDirectoryTree(depth: 8, filesPerDir: 3, branchingFactor: 3);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "DeepDirectoryTree",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                overrideDirectory: deepRoot);

            AssertMinimumThroughput(metrics, "DeepDirectoryTree");
        }
        finally
        {
            try { Directory.Delete(deepRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MaxResultsCap_Throughput()
    {
        // Hits the result limit before fully scanning the tree. Tests early-termination
        // throughput and cleanup. The cap is sized so it fires partway through the tree
        // (after ~1,200 of 50,000 files at ~80 matches/file), giving the MB/sec metric
        // a meaningful denominator (~17 MB scanned over multiple hundred ms) instead of
        // being dominated by fixed per-search setup overhead.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "MaxResultsCap",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            maxResults: 100_000);

        AssertMinimumThroughput(metrics, "MaxResultsCap");
        Assert.True(metrics.TotalMatches <= 110_000,
            $"[MaxResultsCap] Expected ≤ ~100,000 matches but got {metrics.TotalMatches:N0} (cap should have kicked in).");
        Assert.True(metrics.FilesScanned < 50_000,
            $"[MaxResultsCap] Expected cap to terminate early but scanned all {metrics.FilesScanned:N0} files.");
    }

    [Fact]
    public async Task ParallelismLow_Throughput()
    {
        // Forces single-threaded content scanning. Catches regressions hidden by parallelism.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "ParallelismLow",
            query: "Test",
            useRegex: false,
            caseSensitive: false,
            maxDegreeOfParallelism: 1);

        AssertMinimumThroughput(metrics, "ParallelismLow");
    }

    [Fact]
    public async Task MixedBinaryText_Throughput()
    {
        // Tree with ~50% binary files. Tests binary detection skip overhead.
        var mixedRoot = CreateMixedBinaryTextTree(fileCount: 10_000);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "MixedBinaryText",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                skipBinary: true,
                overrideDirectory: mixedRoot);

            AssertMinimumThroughput(metrics, "MixedBinaryText");
        }
        finally
        {
            try { Directory.Delete(mixedRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ComplexRegex_Throughput()
    {
        // Multi-alternative regex with lookaheads. Catches regex engine regressions.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "ComplexRegex",
            query: @"(?i)test.*pattern|foo\d+bar|lorem\s+ipsum",
            useRegex: true,
            caseSensitive: false);

        AssertMinimumThroughput(metrics, "ComplexRegex");
    }

    [Fact]
    public async Task FileListerOnly_Throughput()
    {
        // Filename-only search with a no-match query — measures pure discovery/listing
        // throughput with zero content scanning and zero match production.
        var metrics = await RunTimedSearchAsync(
            scenarioName: "FileListerOnly",
            query: "ZZZZZ_NO_MATCH_ZZZZZ",
            useRegex: false,
            caseSensitive: false,
            searchMode: SearchMode.FileNames);

        AssertMinimumThroughput(metrics, "FileListerOnly");
        Assert.Equal(0, metrics.TotalMatches);
    }

    [Fact]
    public async Task HalfMillionFiles_Throughput()
    {
        // Fixed 500,000-file benchmark — always creates a 500K-file tree regardless
        // of YAGU_PERF_FILE_COUNT. This is the primary scale stress test.
        var root = CreateSyntheticTree(fileCount: 500_000, linesPerFile: 200);
        try
        {
            var metrics = await RunTimedSearchAsync(
                scenarioName: "HalfMillionFiles",
                query: "Test",
                useRegex: false,
                caseSensitive: false,
                overrideDirectory: root);

            AssertMinimumThroughput(metrics, "HalfMillionFiles");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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
        int contextLines = 3,
        int maxResults = 0,
        int maxDegreeOfParallelism = 0,
        bool skipBinary = true,
        string? overrideDirectory = null)
    {
        // Force managed backend so the test works without Everything installed.
        var previousBackend = FileLister.Backend;
        if (_syntheticRoot is not null)
            FileLister.Backend = FileListerBackend.Managed;

        var effectiveDir = overrideDirectory ?? _searchDirectory;

        try
        {
            // Snapshot GC state before the search.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gcGen0Before = GC.CollectionCount(0);
            int gcGen1Before = GC.CollectionCount(1);
            int gcGen2Before = GC.CollectionCount(2);
            long memoryBefore = GC.GetTotalMemory(forceFullCollection: false);

            using var cts = new CancellationTokenSource(_runDuration);
            var sw = Stopwatch.StartNew();
            Stopwatch? firstMatchSw = Stopwatch.StartNew();
            double? firstMatchMs = null;

            // CPU utilization sampling: record process CPU% every 100ms.
            var cpuSamples = new List<double>();
            var proc = Process.GetCurrentProcess();
            var cpuTimeAtStart = proc.TotalProcessorTime; // bookend for short searches
            var lastCpuTime = cpuTimeAtStart;
            var lastSampleTime = sw.Elapsed;
            int cpuCount = Environment.ProcessorCount;
            using var cpuTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var now = sw.Elapsed;
                    var currentCpu = proc.TotalProcessorTime;
                    var elapsed = now - lastSampleTime;
                    if (elapsed.TotalMilliseconds > 0)
                    {
                        double cpuPercent = (currentCpu - lastCpuTime).TotalMilliseconds
                            / (elapsed.TotalMilliseconds * cpuCount) * 100.0;
                        lock (cpuSamples)
                            cpuSamples.Add(Math.Clamp(cpuPercent, 0, 100));
                    }
                    lastCpuTime = currentCpu;
                    lastSampleTime = now;
                }
                catch { /* sampling is best-effort */ }
            }, null, dueTime: 50, period: 100);

            var options = new SearchOptions
            {
                Directory = effectiveDir,
                Query = query,
                CaseSensitive = caseSensitive,
                UseRegex = useRegex,
                ContextLines = contextLines,
                SearchMode = searchMode,
                IncludeGlobs = includeGlobs ?? [],
                ExcludeGlobs = excludeGlobs ?? [],
                MaxFileSizeBytes = maxFileSizeBytes,
                MaxResults = maxResults,
                MaxMatchesPerFile = 0,
                SkipBinary = skipBinary,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
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
                            if (firstMatchMs is null)
                            {
                                firstMatchMs = firstMatchSw!.Elapsed.TotalMilliseconds;
                                firstMatchSw = null;
                            }
                            break;
                        case SearchEvent.MatchBatch mb:
                            totalMatches += mb.Results.Count;
                            if (firstMatchMs is null && mb.Results.Count > 0)
                            {
                                firstMatchMs = firstMatchSw!.Elapsed.TotalMilliseconds;
                                firstMatchSw = null;
                            }
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
            cpuTimer.Change(Timeout.Infinite, Timeout.Infinite); // stop sampling

            // If the search was too fast for the timer to fire, compute a
            // single whole-search CPU% from the bookend measurements.
            proc.Refresh();
            lock (cpuSamples)
            {
                if (cpuSamples.Count == 0 && sw.Elapsed.TotalMilliseconds > 0)
                {
                    double wholeCpu = (proc.TotalProcessorTime - cpuTimeAtStart).TotalMilliseconds
                        / (sw.Elapsed.TotalMilliseconds * cpuCount) * 100.0;
                    cpuSamples.Add(Math.Clamp(wholeCpu, 0, 100));
                }
            }

            // Compute CPU utilization stats from samples.
            double cpuMin = 0, cpuMax = 0, cpuMean = 0, cpuMedian = 0;
            int cpuSampleCount;
            lock (cpuSamples)
            {
                cpuSampleCount = cpuSamples.Count;
                if (cpuSampleCount > 0)
                {
                    cpuSamples.Sort();
                    cpuMin = cpuSamples[0];
                    cpuMax = cpuSamples[^1];
                    cpuMean = cpuSamples.Average();
                    cpuMedian = cpuSampleCount % 2 == 1
                        ? cpuSamples[cpuSampleCount / 2]
                        : (cpuSamples[cpuSampleCount / 2 - 1] + cpuSamples[cpuSampleCount / 2]) / 2.0;
                }
            }

            // Snapshot GC/memory state after the search.
            int gcGen0After = GC.CollectionCount(0);
            int gcGen1After = GC.CollectionCount(1);
            int gcGen2After = GC.CollectionCount(2);
            long peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
            int peakThreadCount = Process.GetCurrentProcess().Threads.Count;
            long memoryAfter = GC.GetTotalMemory(forceFullCollection: false);

            var metrics = new PerfMetrics
            {
                Scenario = scenarioName,
                Directory = effectiveDir,
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
                // GC & memory metrics
                GcGen0Collections = gcGen0After - gcGen0Before,
                GcGen1Collections = gcGen1After - gcGen1Before,
                GcGen2Collections = gcGen2After - gcGen2Before,
                PeakWorkingSetMB = peakWorkingSet / (double)Megabyte,
                AllocatedDeltaMB = (memoryAfter - memoryBefore) / (double)Megabyte,
                PeakThreadCount = peakThreadCount,
                // Time-to-first-match
                FirstMatchMs = firstMatchMs,
                // Config params for cross-run comparison
                ConfigDurationSeconds = (int)_runDuration.TotalSeconds,
                ConfigContextLines = contextLines,
                ConfigMaxParallelism = maxDegreeOfParallelism,
                ConfigMaxFileSizeBytes = maxFileSizeBytes,
                // Runtime info
                DotNetVersion = Environment.Version.ToString(),
                OsVersion = Environment.OSVersion.ToString(),
                // CPU utilization
                CpuMinPercent = Math.Round(cpuMin, 1),
                CpuMaxPercent = Math.Round(cpuMax, 1),
                CpuMeanPercent = Math.Round(cpuMean, 1),
                CpuMedianPercent = Math.Round(cpuMedian, 1),
                CpuSampleCount = cpuSampleCount,
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

        double averageBytesPerFile = metrics.BytesScanned / (double)Math.Max(metrics.FilesScanned, 1);

        // For scenarios with few or very large files, files/sec is not meaningful —
        // a 500 MB file scanned quickly still produces a low fps value. Use MB/sec instead.
        if (metrics.FilesScanned < 20 || averageBytesPerFile >= 20 * Megabyte)
        {
            double minMBPerSecond = GetEnvInt($"YAGU_PERF_{scenario.ToUpperInvariant()}_MIN_MBPS", 10);
            Assert.True(metrics.MBPerSecond >= minMBPerSecond,
                $"[{scenario}] Throughput regression: {metrics.MBPerSecond:F1} MB/s " +
                $"is below minimum {minMBPerSecond} MB/s.");
        }
        else
        {
            // Regression guard: files/sec must exceed a floor.
            // These floors are deliberately conservative so the test doesn't flake on slow CI.
            int minFilesPerSecond = GetEnvInt($"YAGU_PERF_{scenario.ToUpperInvariant()}_MIN_FPS", 10);
            Assert.True(metrics.FilesPerSecond >= minFilesPerSecond,
                $"[{scenario}] Throughput regression: {metrics.FilesPerSecond:F1} files/s " +
                $"is below minimum {minFilesPerSecond} files/s.");
        }

        // If the search actually found matches, the matches/sec rate should be non-trivial.
        if (metrics.TotalMatches > 0)
        {
            int minMatchesPerSecond = GetEnvInt($"YAGU_PERF_{scenario.ToUpperInvariant()}_MIN_MPS", 1);
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
            // Write to the Yagu.Benchmarks/results folder (gitignored).
            // Walk up from the test assembly bin dir to the solution root,
            // then into Yagu.Benchmarks/results.
            var assemblyDir = Path.GetDirectoryName(typeof(PerformanceBenchmarkTests).Assembly.Location)!;
            var solutionRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
            var baselineDir = Path.Combine(solutionRoot, "Yagu.Benchmarks", "results");
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
        _output.WriteLine($"GC Gen0/1/2:        {metrics.GcGen0Collections}/{metrics.GcGen1Collections}/{metrics.GcGen2Collections}");
        _output.WriteLine($"Peak working set:   {metrics.PeakWorkingSetMB:F1} MB");
        _output.WriteLine($"Alloc delta:        {metrics.AllocatedDeltaMB:F1} MB");
        _output.WriteLine($"Peak threads:       {metrics.PeakThreadCount}");
        _output.WriteLine($"First match:        {(metrics.FirstMatchMs.HasValue ? $"{metrics.FirstMatchMs.Value:F2} ms" : "(no matches)")}");
        _output.WriteLine($".NET:               {metrics.DotNetVersion}");
        _output.WriteLine($"OS:                 {metrics.OsVersion}");
        _output.WriteLine($"CPU util (min/med/mean/max): {metrics.CpuMinPercent:F1}% / {metrics.CpuMedianPercent:F1}% / {metrics.CpuMeanPercent:F1}% / {metrics.CpuMaxPercent:F1}%  ({metrics.CpuSampleCount} samples)");
        _output.WriteLine("────────────────────────────────────────");
    }

    /// <summary>
    /// Delete any <c>qg-perf-*</c> directories left in temp from previous runs
    /// that were terminated before Dispose could run.
    /// </summary>
    private void CleanupStaleTempTrees()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var dir in Directory.EnumerateDirectories(tempDir, "qg-perf-*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _output.WriteLine($"Cleaned up stale temp tree: {dir}");
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort — don't fail the test if cleanup itself fails */ }
    }

    // ───────────────────────── Synthetic Tree Setup ─────────────────────────

    private static string CreateSyntheticTree(int fileCount, int linesPerFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-perf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Pre-create all subdirectories (100 files per folder).
        int dirCount = (fileCount + 99) / 100;
        for (int d = 0; d < dirCount; d++)
            Directory.CreateDirectory(Path.Combine(root, $"folder-{d:D3}"));

        // Build files in parallel — each thread gets its own RNG (seeded
        // deterministically per file) and StringBuilder.
        Parallel.For(0, fileCount, () => new System.Text.StringBuilder(linesPerFile * 80),
            (f, _, sb) =>
            {
                var rng = new Random(42 + f); // deterministic per file
                sb.Clear();
                for (int l = 0; l < linesPerFile; l++)
                {
                    if (rng.NextDouble() < 0.2)
                        sb.AppendLine($"line {l}: This is a Test line with keyword Testing various patterns");
                    else
                        sb.AppendLine($"line {l}: Lorem ipsum dolor sit amet, consectetur adipiscing elit");
                }

                int dirIndex = f / 100;
                var dir = Path.Combine(root, $"folder-{dirIndex:D3}");
                File.WriteAllText(Path.Combine(dir, $"file-{f:D5}.txt"), sb.ToString());
                return sb;
            },
            _ => { });

        return root;
    }

    private static string CreateHighDensityTree(int fileCount, int linesPerFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-perf-hd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        int dirCount = (fileCount + 99) / 100;
        for (int d = 0; d < dirCount; d++)
            Directory.CreateDirectory(Path.Combine(root, $"dense-{d:D3}"));

        Parallel.For(0, fileCount, () => new System.Text.StringBuilder(linesPerFile * 80),
            (f, _, sb) =>
            {
                sb.Clear();
                for (int l = 0; l < linesPerFile; l++)
                    sb.AppendLine($"line {l}: every line matches the query with variations line-{l}");

                int dirIndex = f / 100;
                var dir = Path.Combine(root, $"dense-{dirIndex:D3}");
                File.WriteAllText(Path.Combine(dir, $"dense-{f:D5}.txt"), sb.ToString());
                return sb;
            },
            _ => { });

        return root;
    }

    /// <summary>
    /// Creates a tree of files where each file is approximately <paramref name="targetSizeBytes"/>.
    /// Content is deterministic (fixed seed) with ~20% of lines containing "Test".
    /// </summary>
    private static string CreateSizedFileTree(string tag, int fileCount, long targetSizeBytes)
    {
        var root = Path.Combine(Path.GetTempPath(), $"qg-perf-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Average line length ≈ 80 chars → 80 bytes UTF-8
        const int avgLineBytes = 80;
        int linesPerFile = Math.Max(1, (int)(targetSizeBytes / avgLineBytes));

        int dirCount = (fileCount + 49) / 50;
        for (int d = 0; d < dirCount; d++)
            Directory.CreateDirectory(Path.Combine(root, $"{tag}-{d:D3}"));

        int sbCapacity = (int)Math.Min(targetSizeBytes + 4096, int.MaxValue);
        Parallel.For(0, fileCount, () => new System.Text.StringBuilder(sbCapacity),
            (f, _, sb) =>
            {
                var rng = new Random(42 + f);
                sb.Clear();
                for (int l = 0; l < linesPerFile; l++)
                {
                    if (rng.NextDouble() < 0.2)
                        sb.AppendLine($"line {l}: This is a Test line with keyword Testing various patterns in sized file");
                    else
                        sb.AppendLine($"line {l}: Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium");
                }

                int dirIndex = f / 50;
                var dir = Path.Combine(root, $"{tag}-{dirIndex:D3}");
                File.WriteAllText(Path.Combine(dir, $"{tag}-{f:D5}.txt"), sb.ToString());
                return sb;
            },
            _ => { });

        return root;
    }

    /// <summary>
    /// Creates a deterministic mix of files across all size categories.
    /// Hard-coded sizes ensure benchmarks are comparable across runs.
    ///
    /// Layout (30 files total):
    ///   10 × tiny   (~  30 KB,  ~  40 KB,  ~  50 KB … varying around 50 KB)
    ///    6 × small   (~   5 MB, ~   7 MB, ~  10 MB)
    ///    5 × medium  (~  30 MB, ~  40 MB, ~  50 MB)
    ///    4 × large   (~  80 MB, ~  90 MB, ~ 100 MB)
    ///    3 × vlarge  (~ 150 MB, ~ 180 MB, ~ 200 MB)
    ///    2 × vvlarge (~ 400 MB, ~ 500 MB)
    /// </summary>
    private static string CreateMixedSizeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-perf-mixed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Hard-coded target sizes in bytes — deterministic across runs.
        var fileSizes = new (string category, long sizeBytes)[]
        {
            // Tiny (≤50 KB)
            ("tiny", 30 * 1024L),
            ("tiny", 35 * 1024L),
            ("tiny", 40 * 1024L),
            ("tiny", 42 * 1024L),
            ("tiny", 45 * 1024L),
            ("tiny", 47 * 1024L),
            ("tiny", 48 * 1024L),
            ("tiny", 49 * 1024L),
            ("tiny", 50 * 1024L),
            ("tiny", 50 * 1024L),
            // Small (≤10 MB)
            ("small",  5L * 1024 * 1024),
            ("small",  6L * 1024 * 1024),
            ("small",  7L * 1024 * 1024),
            ("small",  8L * 1024 * 1024),
            ("small",  9L * 1024 * 1024),
            ("small", 10L * 1024 * 1024),
            // Medium (~50 MB)
            ("medium", 30L * 1024 * 1024),
            ("medium", 35L * 1024 * 1024),
            ("medium", 40L * 1024 * 1024),
            ("medium", 45L * 1024 * 1024),
            ("medium", 50L * 1024 * 1024),
            // Large (~100 MB)
            ("large",  80L * 1024 * 1024),
            ("large",  90L * 1024 * 1024),
            ("large",  95L * 1024 * 1024),
            ("large", 100L * 1024 * 1024),
            // Very Large (~200 MB)
            ("vlarge", 150L * 1024 * 1024),
            ("vlarge", 180L * 1024 * 1024),
            ("vlarge", 200L * 1024 * 1024),
            // Very Very Large (~500 MB)
            ("vvlarge", 400L * 1024 * 1024),
            ("vvlarge", 500L * 1024 * 1024),
        };

        const int avgLineBytes = 80;

        // Pre-create category directories
        foreach (var cat in fileSizes.Select(x => x.category).Distinct())
            Directory.CreateDirectory(Path.Combine(root, cat));

        Parallel.For(0, fileSizes.Length, f =>
        {
            var (category, sizeBytes) = fileSizes[f];
            int linesPerFile = Math.Max(1, (int)(sizeBytes / avgLineBytes));
            var rng = new Random(42 + f);

            var sb = new System.Text.StringBuilder((int)Math.Min(sizeBytes + 4096, int.MaxValue));
            for (int l = 0; l < linesPerFile; l++)
            {
                if (rng.NextDouble() < 0.2)
                    sb.AppendLine($"line {l}: This is a Test line with keyword Testing various patterns in mixed file");
                else
                    sb.AppendLine($"line {l}: Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium");
            }

            File.WriteAllText(Path.Combine(root, category, $"mixed-{f:D3}-{category}.txt"), sb.ToString());
        });

        return root;
    }

    private static string CreateDeepDirectoryTree(int depth, int filesPerDir, int branchingFactor)
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-perf-deep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Phase 1: create entire directory structure (fast, no file I/O).
        var allDirs = new List<(string dir, int remaining)>();
        CollectDirs(root, depth);

        void CollectDirs(string dir, int remaining)
        {
            allDirs.Add((dir, remaining));
            if (remaining <= 0) return;
            for (int b = 0; b < branchingFactor; b++)
            {
                var sub = Path.Combine(dir, $"sub-{b}");
                Directory.CreateDirectory(sub);
                CollectDirs(sub, remaining - 1);
            }
        }

        // Phase 2: write files in parallel across all directories.
        Parallel.ForEach(allDirs, entry =>
        {
            for (int f = 0; f < filesPerDir; f++)
            {
                var rng = new Random(42 + entry.remaining * 1000 + f);
                var content = rng.NextDouble() < 0.3
                    ? $"line 0: This is a Test file at depth {entry.remaining}\nline 1: Lorem ipsum\n"
                    : "line 0: Lorem ipsum dolor sit amet\nline 1: consectetur adipiscing elit\n";
                File.WriteAllText(Path.Combine(entry.dir, $"d{entry.remaining}-f{f}.txt"), content);
            }
        });

        return root;
    }

    private static string CreateMixedBinaryTextTree(int fileCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-perf-mix-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        int dirCount = (fileCount + 99) / 100;
        for (int d = 0; d < dirCount; d++)
            Directory.CreateDirectory(Path.Combine(root, $"mix-{d:D3}"));

        Parallel.For(0, fileCount, f =>
        {
            var rng = new Random(42 + f);
            int dirIndex = f / 100;
            var dir = Path.Combine(root, $"mix-{dirIndex:D3}");

            if (f % 2 == 0)
            {
                // Text file
                var content = rng.NextDouble() < 0.3
                    ? $"line 0: This is a Test line\nline 1: More text content\nline 2: End of file\n"
                    : "line 0: Lorem ipsum dolor sit amet\nline 1: consectetur adipiscing elit\n";
                File.WriteAllText(Path.Combine(dir, $"text-{f:D5}.txt"), content);
            }
            else
            {
                // Binary file — write bytes with NUL characters so BinaryDetector flags it
                var bytes = new byte[1024];
                rng.NextBytes(bytes);
                // Ensure a few NUL bytes so it looks binary
                bytes[10] = 0;
                bytes[50] = 0;
                bytes[200] = 0;
                File.WriteAllBytes(Path.Combine(dir, $"bin-{f:D5}.dat"), bytes);
            }
        });

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
        // ── Search config & identity ──
        public string Scenario { get; init; } = "";
        public string Directory { get; init; } = "";
        public string Query { get; init; } = "";
        public bool UseRegex { get; init; }
        public bool CaseSensitive { get; init; }
        public string SearchMode { get; init; } = "";

        // ── Throughput ──
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

        // ── GC & memory ──
        public int GcGen0Collections { get; init; }
        public int GcGen1Collections { get; init; }
        public int GcGen2Collections { get; init; }
        public double PeakWorkingSetMB { get; init; }
        public double AllocatedDeltaMB { get; init; }
        public int PeakThreadCount { get; init; }

        // ── Responsiveness ──
        public double? FirstMatchMs { get; init; }

        // ── Config (for cross-run comparison) ──
        public int ConfigDurationSeconds { get; init; }
        public int ConfigContextLines { get; init; }
        public int ConfigMaxParallelism { get; init; }
        public long ConfigMaxFileSizeBytes { get; init; }

        // ── Environment ──
        public DateTime Timestamp { get; init; }
        public string MachineName { get; init; } = "";
        public int ProcessorCount { get; init; }
        public string DotNetVersion { get; init; } = "";
        public string OsVersion { get; init; } = "";

        // ── CPU utilization ──
        public double CpuMinPercent { get; init; }
        public double CpuMaxPercent { get; init; }
        public double CpuMeanPercent { get; init; }
        public double CpuMedianPercent { get; init; }
        public int CpuSampleCount { get; init; }
    }
}
