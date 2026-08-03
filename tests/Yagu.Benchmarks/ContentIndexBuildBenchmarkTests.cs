using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Yagu.Services.Index;
using Xunit;
using Xunit.Abstractions;

namespace Yagu.Benchmarks;

/// <summary>
/// Stage 0 end-to-end index-<b>build</b> benchmark harness (plan §7 Stage 0). Unlike
/// <see cref="ContentIndexBenchmarkTests"/> (which measures in-memory query evaluation over a synthetic
/// generation), these build a real on-disk corpus through <see cref="ContentIndexManager.BuildScope"/>
/// and record wall-clock time plus the actual content bytes read (<see cref="IndexBuildIoStats"/>).
/// <list type="bullet">
///   <item><b>Binary-heavy</b> — proves the Stage 1 one-open prefix rejection reads at most the 8 KB
///     sniff per binary file instead of its whole body (a large bytes-read reduction).</item>
///   <item><b>Text-heavy</b> — records build throughput over valid BOM-less UTF-8 (a regression guard;
///     text still needs a full read, so this is not expected to shrink until later stages).</item>
/// </list>
/// Corpus sizes are env-overridable so a slow box can shrink them; every run appends a JSON line to
/// <c>Yagu.Benchmarks/results/content-index-build-baselines.jsonl</c> for cross-commit diff.
/// </summary>
[Collection("PerformanceBenchmarks")]
[ExcludeFromCodeCoverage]
[Trait("Category", "Slow")]
public sealed class ContentIndexBuildBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public ContentIndexBuildBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BinaryHeavyFullBuild_ReadsAtMostSniffPrefixPerBinary()
    {
        int binaryFiles = Math.Max(20, GetEnvInt("YAGU_INDEX_BENCH_BIN_FILES", 200));
        int binaryFileBytes = Math.Max(64 * 1024, GetEnvInt("YAGU_INDEX_BENCH_BIN_BYTES", 256 * 1024));
        int textFiles = Math.Max(1, GetEnvInt("YAGU_INDEX_BENCH_BIN_TEXTFILES", 10));

        using var corpus = new BuildCorpus();
        long onDiskBytes = 0;
        for (int i = 0; i < binaryFiles; i++)
            onDiskBytes += corpus.WriteBinary($"bin{i}.png", binaryFileBytes);
        for (int i = 0; i < textFiles; i++)
            onDiskBytes += corpus.WriteText($"doc{i}.txt", "the quick brown fox jumps over the lazy dog\n", 40);

        var manager = new ContentIndexManager(corpus.Paths);
        var sw = Stopwatch.StartNew();
        BuildScopeResult result = manager.BuildScope(corpus.Root, OpenPolicy());
        sw.Stop();

        long sniff = ContentRepresentation.BinarySniffBytes;
        // The binaries must contribute at most one sniff each; only the text files are read in full.
        long ceiling = (long)binaryFiles * sniff + corpus.TextBytes;

        Record("content-index-build-baselines.jsonl", "BinaryHeavyFullBuild", new()
        {
            ["binaryFiles"] = binaryFiles,
            ["textFiles"] = textFiles,
            ["onDiskBytes"] = onDiskBytes,
            ["contentBytesRead"] = result.IoStats.ContentBytesRead,
            ["prefixRejectedFiles"] = result.IoStats.PrefixRejectedFiles,
            ["fullyReadFiles"] = result.IoStats.FullyReadFiles,
            ["indexed"] = result.Report.IndexedCount,
            ["elapsedMs"] = sw.Elapsed.TotalMilliseconds,
            ["readReductionX"] = onDiskBytes == 0 ? 0 : Math.Round((double)onDiskBytes / Math.Max(1, result.IoStats.ContentBytesRead), 2),
        });

        Assert.Equal(binaryFiles, result.IoStats.PrefixRejectedFiles);
        Assert.Equal(textFiles, result.Report.IndexedCount);
        Assert.True(result.IoStats.ContentBytesRead <= ceiling,
            $"content bytes read {result.IoStats.ContentBytesRead:N0} exceeds the prefix-rejection ceiling {ceiling:N0} " +
            $"(binaries={binaryFiles}, sniff={sniff}, textBytes={corpus.TextBytes})");
        // Sanity: the binary corpus is far larger than the bytes actually read (the whole point).
        Assert.True(result.IoStats.ContentBytesRead < onDiskBytes,
            $"expected a bytes-read reduction: read {result.IoStats.ContentBytesRead:N0} of {onDiskBytes:N0} on-disk bytes");
    }

    [Fact]
    public void TextHeavyFullBuild_ThroughputRecorded()
    {
        int textFiles = Math.Max(50, GetEnvInt("YAGU_INDEX_BENCH_TEXT_FILES", 2000));

        using var corpus = new BuildCorpus();
        long onDiskBytes = 0;
        for (int i = 0; i < textFiles; i++)
            onDiskBytes += corpus.WriteText($"doc{i}.txt", $"line {i} the quick brown fox café über\n", 30);

        var manager = new ContentIndexManager(corpus.Paths);
        var sw = Stopwatch.StartNew();
        BuildScopeResult result = manager.BuildScope(corpus.Root, OpenPolicy());
        sw.Stop();

        double docsPerSec = sw.Elapsed.TotalSeconds > 0 ? result.Report.IndexedCount / sw.Elapsed.TotalSeconds : 0;

        Record("content-index-build-baselines.jsonl", "TextHeavyFullBuild", new()
        {
            ["textFiles"] = textFiles,
            ["onDiskBytes"] = onDiskBytes,
            ["contentBytesRead"] = result.IoStats.ContentBytesRead,
            ["fullyReadFiles"] = result.IoStats.FullyReadFiles,
            ["indexed"] = result.Report.IndexedCount,
            ["elapsedMs"] = sw.Elapsed.TotalMilliseconds,
            ["docsPerSec"] = Math.Round(docsPerSec, 1),
        });

        Assert.Equal(textFiles, result.Report.IndexedCount);
        // A valid-text file is read in full: bytes read tracks the on-disk size (no early rejection here).
        Assert.Equal(onDiskBytes, result.IoStats.ContentBytesRead);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static IndexIngestionPolicy OpenPolicy() =>
        new(0, null, null, includeHiddenFiles: true, followReparsePoints: false, maxDepth: 0);

    /// <summary>A throwaway on-disk corpus with its own index root, tracking indexable text bytes.</summary>
    private sealed class BuildCorpus : IDisposable
    {
        private readonly string _sandbox;
        public string Root { get; }
        public IContentIndexPathProvider Paths { get; }
        public long TextBytes { get; private set; }

        public BuildCorpus()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "yagu-build-bench-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_sandbox, "corpus");
            string indexRoot = Path.Combine(_sandbox, "index");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(indexRoot);
            Paths = new DefaultContentIndexPathProvider(indexRoot, indexRoot);
        }

        public long WriteBinary(string name, int bytes)
        {
            var blob = new byte[bytes];
            for (int i = 0; i < blob.Length; i++) blob[i] = 0xAB; // >= 0x80, no NUL/control noise in the tail
            blob[0] = 0x89; blob[1] = 0x50; blob[2] = 0x4E; blob[3] = 0x47; // PNG magic decides it after the sniff
            File.WriteAllBytes(Path.Combine(Root, name), blob);
            return blob.Length;
        }

        public long WriteText(string name, string line, int repeats)
        {
            var sb = new StringBuilder(line.Length * repeats);
            for (int i = 0; i < repeats; i++) sb.Append(line);
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
            File.WriteAllBytes(Path.Combine(Root, name), bytes);
            TextBytes += bytes.Length;
            return bytes.Length;
        }

        public void Dispose()
        {
            try { Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    private void Record(string file, string scenario, Dictionary<string, object> metrics)
    {
        metrics["scenario"] = scenario;
        metrics["timestampUtc"] = DateTime.UtcNow.ToString("O");
        metrics["machineName"] = Environment.MachineName;
        metrics["is64Bit"] = Environment.Is64BitProcess;

        foreach (var kv in metrics.OrderBy(k => k.Key))
            _output.WriteLine($"  {kv.Key}: {kv.Value}");

        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(ContentIndexBuildBenchmarkTests).Assembly.Location)!;
            var solutionRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
            var baselineDir = Path.Combine(solutionRoot, "Yagu.Benchmarks", "results");
            Directory.CreateDirectory(baselineDir);
            File.AppendAllText(Path.Combine(baselineDir, file), JsonSerializer.Serialize(metrics) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  ⚠ Could not write baseline: {ex.Message}");
        }
    }

    private static int GetEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
    }
}
