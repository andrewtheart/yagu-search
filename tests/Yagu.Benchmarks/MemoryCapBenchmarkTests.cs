using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Yagu.Models;
using Yagu.Services;
using Xunit;
using Xunit.Abstractions;

namespace Yagu.Benchmarks;

/// <summary>
/// Measures what exceeding the process memory cap actually costs. <c>AutoProcessMemoryCap</c> clamps to
/// 768 MB no matter how much RAM the machine has, so a large search on a workstation crosses it and stays
/// in memory-saving mode: native batches shrink from up to 4096 to 256, the working set is trimmed, and
/// compacting GCs run.
///
/// The cap is the only variable — system-wide pressure is disabled on both runs — so the delta is
/// attributable to the cap alone.
///
/// MEASURED (3 interleaved repeats, 4,000 files / ~156 MB, Debug, managed backend): median 305.1 MB/s
/// uncapped vs 306.1 MB/s over-cap — no measurable throughput cost. Single runs varied 257-310 MB/s in
/// BOTH arms, so any one-shot comparison here is noise. This does NOT cover the ResultStore disk-eviction
/// path, which is the documented cause of the large throughput collapse; this benchmark acknowledges
/// eviction with nothing freed.
/// </summary>
[Collection("PerformanceBenchmarks")]
[ExcludeFromCodeCoverage]
[Trait("Category", "Slow")]
public sealed class MemoryCapBenchmarkTests : IDisposable
{
    private const long Megabyte = 1024L * 1024L;

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public MemoryCapBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "yagu-memcap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        int fileCount = GetEnvInt("YAGU_MEMCAP_FILE_COUNT", 4_000);
        int linesPerFile = GetEnvInt("YAGU_MEMCAP_LINES_PER_FILE", 400);
        CreateCorpus(fileCount, linesPerFile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExceedingTheProcessCap_PreservesResultsAndRecordsThroughputDelta()
    {
        // Warm the page cache so the first measured run is not charged for cold I/O.
        await RunAsync("warmup", maxProcessMemoryBytes: long.MaxValue);

        int repeats = GetEnvInt("YAGU_MEMCAP_REPEATS", 3);
        var uncapped = new List<CapRun>();
        var overCap = new List<CapRun>();

        // Interleaved so background load on the machine drifts across both arms equally.
        for (int i = 0; i < repeats; i++)
        {
            uncapped.Add(await RunAsync("uncapped", maxProcessMemoryBytes: long.MaxValue));
            overCap.Add(await RunAsync("over-cap", maxProcessMemoryBytes: 1));
        }

        foreach (CapRun run in uncapped.Concat(overCap))
        {
            _output.WriteLine(
                $"{run.Label,-9}: {run.ElapsedSeconds:F2}s, {run.MBPerSecond:F1} MB/s, " +
                $"{run.FilesPerSecond:F0} files/s, matches={run.Matches:N0}, " +
                $"degraded={run.Degraded}, pressureEvents={run.PressureEvents}, peakWS={run.PeakWorkingSetMB:F0} MB");
        }

        double uncappedMedian = Median(uncapped.Select(r => r.MBPerSecond));
        double overCapMedian = Median(overCap.Select(r => r.MBPerSecond));
        _output.WriteLine(
            $"median MB/s: uncapped={uncappedMedian:F1}, over-cap={overCapMedian:F1} " +
            $"({overCapMedian / Math.Max(uncappedMedian, 0.001):P0} of uncapped throughput)");

        // A like-for-like comparison is only valid if both arms did identical work.
        int[] matchCounts = uncapped.Concat(overCap).Select(r => r.Matches).Distinct().ToArray();
        Assert.True(matchCounts.Length == 1,
            $"Runs did not do identical work; match counts differed: {string.Join(", ", matchCounts)}");

        Assert.All(overCap, run => Assert.True(run.Degraded, "Exceeding the cap must enter memory-saving mode."));
        Assert.All(uncapped, run => Assert.False(run.Degraded, "Staying under the cap must not enter memory-saving mode."));
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    private async Task<CapRun> RunAsync(string label, long maxProcessMemoryBytes)
    {
        var previousBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.Managed;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var options = new SearchOptions
            {
                Directory = _root,
                Query = "needle",
                SearchMode = SearchMode.Content,
                MaxFileSizeBytes = 0,
                MaxResults = 0,
                ContextLines = 0,
                MaxProcessMemoryBytes = maxProcessMemoryBytes,
                // Machine-wide pressure is disabled so the process cap is the only variable.
                MemoryPressurePercent = 0,
            };

            // Retain results like the UI does, so the run carries a realistic live heap.
            var retained = new List<SearchResult>(capacity: 1 << 16);
            int pressureEvents = 0;
            bool degraded = false;
            int filesScanned = 0;
            long bytesScanned = 0;
            int matches = 0;

            var sw = Stopwatch.StartNew();
            await foreach (SearchEvent evt in new SearchService().SearchAsync(options, default))
            {
                switch (evt)
                {
                    case SearchEvent.Match m:
                        retained.Add(m.Result);
                        matches++;
                        break;
                    case SearchEvent.MatchBatch mb:
                        retained.AddRange(mb.Results);
                        matches += mb.Results.Count;
                        break;
                    case SearchEvent.MemoryPressure mp:
                        pressureEvents++;
                        degraded = true;
                        mp.AcknowledgeEviction(0);
                        break;
                    case SearchEvent.Completed c:
                        filesScanned = c.Summary.FilesScanned;
                        bytesScanned = c.Summary.BytesScanned;
                        matches = Math.Max(matches, c.Summary.TotalMatches);
                        degraded |= c.Summary.Degraded;
                        break;
                }
            }
            sw.Stop();

            GC.KeepAlive(retained);
            using var process = Process.GetCurrentProcess();
            double peakWorkingSetMb = process.PeakWorkingSet64 / (double)Megabyte;
            double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            return new CapRun(
                label,
                seconds,
                bytesScanned / (double)Megabyte / seconds,
                filesScanned / seconds,
                matches,
                filesScanned,
                degraded,
                pressureEvents,
                peakWorkingSetMb);
        }
        finally
        {
            FileLister.Backend = previousBackend;
        }
    }

    private void CreateCorpus(int fileCount, int linesPerFile)
    {
        var line = new string('x', 100);
        for (int i = 0; i < fileCount; i++)
        {
            var sb = new StringBuilder(linesPerFile * 110);
            for (int l = 0; l < linesPerFile; l++)
            {
                // One match every 10 lines keeps the result set large without dwarfing scan time.
                sb.AppendLine(l % 10 == 0 ? $"{line} needle {l}" : line);
            }
            string dir = Path.Combine(_root, "d" + (i % 50));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    private static int GetEnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

    private sealed record CapRun(
        string Label,
        double ElapsedSeconds,
        double MBPerSecond,
        double FilesPerSecond,
        int Matches,
        int FilesScanned,
        bool Degraded,
        int PressureEvents,
        double PeakWorkingSetMB);
}
