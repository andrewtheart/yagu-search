using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;
using Xunit.Abstractions;

namespace Yagu.Tests.Index;

/// <summary>
/// EXPERIMENT (not a pass/fail regression) — a genuine end-to-end A/B that times the SAME selective content
/// search two ways over a real, large on-disk folder: (1) a plain live scan (index off), and (2) the Stage-4+
/// worker-pruning path (the IndexUseWorkerQuerySessions feature) served by the real bundled worker
/// over a freshly-built format-v3 index. It asserts the two result multisets are IDENTICAL (correctness) and
/// writes the build time + both search wall-clocks + match/skip counts to the test output so we can see whether
/// pruning is actually faster. Self-gates: skips if the target folder or the worker binary is absent, so it is
/// a no-op in CI / on other machines. [Slow] so the iterative suite never runs it. Run explicitly:
///   dotnet test --filter "FullyQualifiedName~LargeScopeSearchSpeedupExperiment"
/// </summary>
[Trait("Category", "Slow")]
public sealed class LargeScopeSearchSpeedupExperiment : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _sandbox;
    private readonly IContentIndexPathProvider _paths;
    private ContentIndexStore? _store;

    // The large-scope target and a deliberately SELECTIVE query (a token that appears in essentially no file),
    // so the index prunes ~everything and the measured delta isolates the content-read work pruning avoids.
    private const string TargetFolder = @"C:\Program Files\dotnet";
    private const string RareQuery = "ZzQqXx7414NeedleYagu";
    private const int MaxIndexedFiles = 30_000;
    private const long MaxIndexedBytes = 500L * 1024 * 1024; // cap the in-memory build for safety
    private const int Iterations = 3;

    private static readonly string[] TextExtensions =
    {
        ".cs", ".json", ".xml", ".txt", ".md", ".props", ".targets", ".nuspec",
        ".config", ".js", ".ts", ".html", ".css", ".csproj", ".sln", ".yml", ".yaml",
    };

    public LargeScopeSearchSpeedupExperiment(ITestOutputHelper output)
    {
        _output = output;
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-speedup-exp", Guid.NewGuid().ToString("N"));
        string storage = Path.Combine(_sandbox, "storage");
        Directory.CreateDirectory(storage);
        _paths = new DefaultContentIndexPathProvider(storage, storage);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private static ContentIndexFreshnessEvaluator.JournalReader OkReader()
        => (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());

    private SearchOptions MakeOptions(bool useContentIndex)
        => new()
        {
            Directory = TargetFolder,
            Query = RareQuery,
            ExactMatch = false,
            CaseSensitive = true,
            SearchMode = SearchMode.Content,
            MaxResults = 50_000, // must stay <= SearchOptions.MaxResultsCeiling (50k) — above it SearchService
                                 // copies options via CopyOptions, which drops the pruning-scan factory.
            MaxFileSizeBytes = 0,
            SkipBinary = true,
            UseContentIndex = useContentIndex,
        };

    // Runs a full SearchService search to completion; returns (elapsed ms, canonical result multiset).
    private static async Task<(double Ms, List<string> Rows)> TimeSearchAsync(SearchOptions options)
    {
        var rows = new List<string>();
        var service = new SearchService();
        var sw = Stopwatch.StartNew();
        await foreach (SearchEvent evt in service.SearchAsync(options, CancellationToken.None))
        {
            if (evt is SearchEvent.MatchBatch batch)
                foreach (SearchResult r in batch.Results) rows.Add($"{IndexScopeIdentity.NormalizePath(r.FilePath)}|{r.LineNumber}|{r.MatchStartColumn}");
            else if (evt is SearchEvent.Match m)
                rows.Add($"{IndexScopeIdentity.NormalizePath(m.Result.FilePath)}|{m.Result.LineNumber}|{m.Result.MatchStartColumn}");
        }
        sw.Stop();
        rows.Sort(StringComparer.Ordinal);
        return (sw.Elapsed.TotalMilliseconds, rows);
    }

    private static string? FindWorkerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return null;
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(dir.FullName, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [Fact]
    public async Task WorkerPruning_vs_LiveScan_OnRealLargeFolder()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null || !Directory.Exists(TargetFolder))
        {
            _output.WriteLine($"[skipped] worker built={workerExe is not null}, folder exists={Directory.Exists(TargetFolder)}");
            return;
        }

        // ── 1. Build a real format-v3 index over the folder's text files (this IS the indexing cost). ──
        var indexedFiles = new List<string>();
        long indexedBytes = 0;
        foreach (string path in Directory.EnumerateFiles(TargetFolder, "*", SearchOption.AllDirectories))
        {
            if (indexedFiles.Count >= MaxIndexedFiles || indexedBytes >= MaxIndexedBytes) break;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(TextExtensions, ext) < 0) continue;
            long len;
            try { len = new FileInfo(path).Length; } catch { continue; }
            if (len is <= 0 or > 5 * 1024 * 1024) continue; // skip empty + huge generated files
            indexedFiles.Add(path);
            indexedBytes += len;
        }

        _output.WriteLine($"Target: {TargetFolder}");
        _output.WriteLine($"Indexed text files: {indexedFiles.Count:N0}  ({indexedBytes / (1024 * 1024):N0} MB)");

        string scopeId = ContentIndexManager.ScopeIdForRoot(TargetFolder);
        ulong nextId = 1000;
        FileIdentity? Provider(string p) => new(0x7, new UsnFileIdentity(nextId++, 0));

        var buildSw = Stopwatch.StartNew();
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        int added = 0;
        foreach (string path in indexedFiles)
        {
            try { builder.AddDocument(path, File.ReadAllBytes(path)); added++; }
            catch { /* unreadable file — skip */ }
        }
        ContentIndexGeneration gen = builder.Build(scopeId, "vol", TargetFolder, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        _store = new ContentIndexStore(_paths, scopeId, retainedGenerations: 2) { ProduceV3QueryStructures = true };
        _store.Publish(gen);
        buildSw.Stop();

        long v3Bytes = 0;
        try
        {
            if (_store.TryGetCurrentLayerDirectories(out string? baseDir, out _) && baseDir is not null)
                v3Bytes = Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { /* best effort */ }

        _output.WriteLine($"Index build: {buildSw.Elapsed.TotalSeconds:N1} s for {added:N0} docs, v3 size {v3Bytes / (1024 * 1024):N0} MB");
        _output.WriteLine("");

        // ── 2. Live-scan baseline (index OFF): read + match every file's content. ──
        var liveTimes = new List<double>();
        List<string> liveRows = new();
        for (int i = 0; i < Iterations; i++)
        {
            (double ms, List<string> rows) = await TimeSearchAsync(MakeOptions(useContentIndex: false));
            liveTimes.Add(ms);
            liveRows = rows;
            _output.WriteLine($"[live-scan] run {i + 1}: {ms:N0} ms, matches={rows.Count}");
        }

        // ── 3. Worker-pruning path (index ON): classify+prune via the real worker; scan only survivors. ──
        // One long-lived worker client (as in production) — only the first search pays the cold worker launch.
        var pruneTimes = new List<double>();
        List<string> pruneRows = new();
        bool everEngaged = false;
        int session = 0;
        using (var client = new IndexWorkerClient(workerPathOverride: workerExe))
        {
            for (int i = 0; i < Iterations; i++)
            {
                bool engaged = false;
                SearchOptions options = MakeOptions(useContentIndex: true);
                string spoolDir = Path.Combine(_sandbox, "spool-" + Guid.NewGuid().ToString("N"));
                int s = ++session;
                int runNo = i + 1;
                options.ContentIndexPruningScanFactory = survivorSink =>
                {
                    IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
                        client, _store!, options, s, OkReader(), spoolDir, survivorSink);
                    engaged = scan is not null;
                    return scan;
                };
                (double ms, List<string> rows) = await TimeSearchAsync(options);
                pruneTimes.Add(ms);
                pruneRows = rows;
                everEngaged |= engaged;
                _output.WriteLine($"[worker-prune] run {runNo}: {ms:N0} ms, matches={rows.Count}, pruningEngaged={engaged}");
            }
        }

        // ── 4. Correctness + verdict. ──
        double liveMedian = Median(liveTimes);
        double pruneCold = pruneTimes[0];
        double pruneWarmMedian = Median(pruneTimes.Skip(1).ToList());

        _output.WriteLine("");
        _output.WriteLine("──────────── RESULT ────────────");
        _output.WriteLine($"Pruning engaged:              {everEngaged}");
        _output.WriteLine($"Result multisets identical:   {(liveRows.SequenceEqual(pruneRows, StringComparer.Ordinal) ? "YES" : "NO")}  (live={liveRows.Count}, prune={pruneRows.Count})");
        _output.WriteLine($"Live-scan   median:           {liveMedian:N0} ms");
        _output.WriteLine($"Worker-prune cold (1st):      {pruneCold:N0} ms  (pays the one-time worker launch)");
        _output.WriteLine($"Worker-prune warm median:     {pruneWarmMedian:N0} ms");
        _output.WriteLine($"Speedup (live / prune-warm):  {(pruneWarmMedian > 0 ? liveMedian / pruneWarmMedian : 0):N2}x");
        _output.WriteLine($"Speedup (live / prune-cold):  {(pruneCold > 0 ? liveMedian / pruneCold : 0):N2}x");
        _output.WriteLine("─────────────────────────────────");

        // The ONLY hard assertion is correctness — the whole point is that pruning never changes results.
        Assert.Equal(liveRows, pruneRows);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
