using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;
using Xunit.Abstractions;

namespace Yagu.Benchmarks;

/// <summary>
/// Phase 0 content-index performance gates (plan §7 Phase 0 "Exit" + §11). These measure the index-specific
/// numbers the plan requires before the feature is considered signed off — separate from the whole-pipeline
/// throughput in <see cref="PerformanceBenchmarkTests"/>:
/// <list type="bullet">
/// <item><b>Warm posting evaluation</b> — a selective query's in-memory candidate evaluation p95 ≤ 75 ms.</item>
/// <item><b>Candidate reduction</b> — a selective query selects &lt; 5% of indexed content objects.</item>
/// <item><b>Cold-worker query</b> — a fresh <c>Yagu.IndexWorker</c> process verifies + queries
///   <c>content.bin</c> p95 ≤ 250 ms (self-gates when the worker isn't built).</item>
/// <item><b>Generation build</b> — build throughput + committed memory for a corpus (recorded; a generous
///   ceiling guards gross regressions, since the managed reference is single-process and not the shipping
///   external-memory builder).</item>
/// <item><b>Safe-lane query latency</b> — end-to-end plan → posting eval → classify every path, first-result
///   proxy p95 ≤ 250 ms (recorded).</item>
/// </list>
/// Every threshold is env-overridable (so a slow CI box can relax it without editing code) and every run
/// appends a JSON line to <c>Yagu.Benchmarks/results/content-index-baselines.jsonl</c> for cross-commit diff.
/// Corpus size is env-configurable via <c>YAGU_INDEX_DOC_COUNT</c> (default 20,000); the plan's headline 1M
/// scope is exercised by raising it on a dedicated run.
/// </summary>
[Collection("PerformanceBenchmarks")]
[ExcludeFromCodeCoverage]
[Trait("Category", "Slow")]
public sealed class ContentIndexBenchmarkTests
{
    private const long Megabyte = 1024L * 1024L;

    // A token whose trigrams do not occur in the common English filler below, so a query for it selects
    // exactly the docs that contain it (deterministic candidate reduction).
    private const string RareNeedle = "Zq9Vx7Kw";

    private readonly ITestOutputHelper _output;
    private readonly int _docCount;

    public ContentIndexBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        _docCount = Math.Max(500, GetEnvInt("YAGU_INDEX_DOC_COUNT", 20_000));
    }

    // ───────────────────────── Scenarios ─────────────────────────

    [Fact]
    public void WarmPostingEvaluation_P95_UnderThreshold()
    {
        ContentIndexGeneration gen = BuildGeneration(_docCount, out _);
        TrigramExpression query = PlanSelectiveQuery();
        var emptyDirty = new DirtyContentSet();

        int iterations = Math.Max(20, GetEnvInt("YAGU_INDEX_WARM_ITERATIONS", 200));

        // Warm up JIT + caches so the p95 reflects steady state, not first-call compilation.
        for (int i = 0; i < 20; i++)
            _ = ContentIndexQuerySession.Begin(gen, query, emptyDirty);

        var samples = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var session = ContentIndexQuerySession.Begin(gen, query, emptyDirty);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
            GC.KeepAlive(session);
        }

        (double p50, double p95, double max) = Percentiles(samples);
        double thresholdMs = GetEnvInt("YAGU_INDEX_WARM_P95_MS", 75);

        Record("WarmPostingEvaluation", new()
        {
            ["docCount"] = _docCount,
            ["iterations"] = iterations,
            ["p50Ms"] = Math.Round(p50, 4),
            ["p95Ms"] = Math.Round(p95, 4),
            ["maxMs"] = Math.Round(max, 4),
            ["thresholdMs"] = thresholdMs,
        });

        AssertPerformanceBudget(p95 <= thresholdMs,
            $"[WarmPostingEvaluation] p95 {p95:F3} ms exceeds warm posting-evaluation budget {thresholdMs} ms " +
            $"(docCount={_docCount}). Set YAGU_INDEX_WARM_P95_MS to override on slow hardware.");
    }

    [Fact]
    public void CandidateReduction_SelectiveQuery_UnderThreshold()
    {
        ContentIndexGeneration gen = BuildGeneration(_docCount, out int rareDocs);
        TrigramExpression query = PlanSelectiveQuery();

        var session = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
        int candidates = session.CandidateCount;
        long corpus = gen.Manifest.ContentCount;
        double candidatePct = corpus == 0 ? 0 : candidates * 100.0 / corpus;

        double thresholdPct = GetEnvInt("YAGU_INDEX_MAX_CANDIDATE_PCT", 5);

        Record("CandidateReduction", new()
        {
            ["docCount"] = _docCount,
            ["corpus"] = corpus,
            ["rareDocs"] = rareDocs,
            ["candidates"] = candidates,
            ["candidatePct"] = Math.Round(candidatePct, 4),
            ["thresholdPct"] = thresholdPct,
        });

        // The trigram index is a superset filter, so candidates must at least cover the true members …
        Assert.True(candidates >= rareDocs,
            $"[CandidateReduction] candidates {candidates} < true members {rareDocs} — superset invariant violated.");
        // … but a selective query must still prune the vast majority of the corpus.
        Assert.True(candidatePct < thresholdPct,
            $"[CandidateReduction] selective query selected {candidatePct:F2}% of {corpus:N0} objects " +
            $"(≥ {thresholdPct}% budget). Set YAGU_INDEX_MAX_CANDIDATE_PCT to override.");
    }

    [Fact]
    public void GenerationBuild_Throughput_AndCommit()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocBefore = GC.GetTotalAllocatedBytes(precise: false);

        var sw = Stopwatch.StartNew();
        ContentIndexGeneration gen = BuildGeneration(_docCount, out _);
        sw.Stop();

        long allocAfter = GC.GetTotalAllocatedBytes(precise: false);
        double allocatedMB = (allocAfter - allocBefore) / (double)Megabyte;
        double peakWorkingSetMB = Process.GetCurrentProcess().PeakWorkingSet64 / (double)Megabyte;
        double docsPerSec = _docCount / Math.Max(sw.Elapsed.TotalSeconds, 0.001);

        // The managed reference is single-process (not the shipping external-memory builder), so this is a
        // gross-regression guard, not the §11 worker-commit gate. Default 384 MB matches the 64-bit worker
        // build-commit budget; override for constrained x86 runs.
        double commitCeilingMB = GetEnvInt("YAGU_INDEX_BUILD_COMMIT_MB", 384);

        Record("GenerationBuild", new()
        {
            ["docCount"] = _docCount,
            ["elapsedSec"] = Math.Round(sw.Elapsed.TotalSeconds, 3),
            ["docsPerSec"] = Math.Round(docsPerSec, 1),
            ["allocatedMB"] = Math.Round(allocatedMB, 1),
            ["peakWorkingSetMB"] = Math.Round(peakWorkingSetMB, 1),
            ["commitCeilingMB"] = commitCeilingMB,
            ["contentCount"] = gen.Manifest.ContentCount,
        });

        Assert.True(gen.Manifest.ContentCount > 0, "[GenerationBuild] built an empty generation.");
        _output.WriteLine($"[GenerationBuild] {_docCount:N0} docs in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({docsPerSec:N0} docs/s), allocated {allocatedMB:F0} MB, peak WS {peakWorkingSetMB:F0} MB.");
    }

    [Fact]
    public void SafeLaneQuery_ClassifyEveryPath_FirstResultLatency()
    {
        ContentIndexGeneration gen = BuildGeneration(_docCount, out _, out IReadOnlyList<string> paths);
        TrigramExpression query = PlanSelectiveQuery();

        int iterations = Math.Max(10, GetEnvInt("YAGU_INDEX_SAFELANE_ITERATIONS", 30));
        var beginToFirstClassify = new double[iterations];
        var totalClassify = new double[iterations];

        for (int it = 0; it < iterations; it++)
        {
            var sw = Stopwatch.StartNew();
            var session = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
            // First classify = the "first verified content result" proxy (B0 → first routing decision).
            _ = session.Classify(paths[0]);
            beginToFirstClassify[it] = sw.Elapsed.TotalMilliseconds;

            for (int p = 1; p < paths.Count; p++)
                _ = session.Classify(paths[p]);
            sw.Stop();
            totalClassify[it] = sw.Elapsed.TotalMilliseconds;
        }

        (double firstP50, double firstP95, _) = Percentiles(beginToFirstClassify);
        (double totalP50, double totalP95, _) = Percentiles(totalClassify);
        double firstResultThresholdMs = GetEnvInt("YAGU_INDEX_FIRSTRESULT_P95_MS", 250);

        Record("SafeLaneQuery", new()
        {
            ["docCount"] = _docCount,
            ["iterations"] = iterations,
            ["firstResultP50Ms"] = Math.Round(firstP50, 4),
            ["firstResultP95Ms"] = Math.Round(firstP95, 4),
            ["classifyAllP50Ms"] = Math.Round(totalP50, 4),
            ["classifyAllP95Ms"] = Math.Round(totalP95, 4),
            ["firstResultThresholdMs"] = firstResultThresholdMs,
        });

        AssertPerformanceBudget(firstP95 <= firstResultThresholdMs,
            $"[SafeLaneQuery] first-result p95 {firstP95:F3} ms exceeds {firstResultThresholdMs} ms " +
            $"(docCount={_docCount}). Set YAGU_INDEX_FIRSTRESULT_P95_MS to override.");
    }

    // ───────────────────────── After-scan-drain B1 (plan §5.4 option (b)) ─────────────────────────

    [Fact]
    public void AfterScanDrainB1_QuiescentReconciliation_ScalesNegligibly()
    {
        // The after-scan-drain B1 barrier replays the journal over the still-pruned aliases. For a selective
        // query nothing changed, so this quiescent reconciliation over the WHOLE pruned set (≈99.5% of the
        // corpus) must cost almost nothing — validating the plan's "the added latency is negligible". Captured
        // identities make the nonmembers genuinely prunable (a missing identity correctly forces a live scan).
        ContentIndexGeneration gen = BuildGeneration(_docCount, out _, out IReadOnlyList<string> paths, BenchIdentityProvider());
        TrigramExpression query = PlanSelectiveQuery();

        int iterations = Math.Max(10, GetEnvInt("YAGU_INDEX_B1_ITERATIONS", 50));
        var samples = new double[iterations];
        int prunedSample = 0;

        for (int it = 0; it < iterations; it++)
        {
            var session = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
            foreach (string p in paths)
                _ = session.Route(p); // fresh nonmembers → provisional prune
            prunedSample = session.ProvisionalAliases.Count;

            var sw = Stopwatch.StartNew();
            IReadOnlyList<long> rescued = session.ReconcileAtB1(new DirtyContentSet(), reconciliationCertain: true);
            sw.Stop();
            samples[it] = sw.Elapsed.TotalMilliseconds;
            Assert.Empty(rescued); // quiescent journal → nothing rescued
        }

        (double p50, double p95, double max) = Percentiles(samples);
        double thresholdMs = GetEnvInt("YAGU_INDEX_B1_P95_MS", 50);

        Record("AfterScanDrainB1", new()
        {
            ["docCount"] = _docCount,
            ["iterations"] = iterations,
            ["prunedAliases"] = prunedSample,
            ["p50Ms"] = Math.Round(p50, 4),
            ["p95Ms"] = Math.Round(p95, 4),
            ["maxMs"] = Math.Round(max, 4),
            ["thresholdMs"] = thresholdMs,
        });

        Assert.True(prunedSample > 0, "[AfterScanDrainB1] expected a non-empty pruned set (captured identities).");
        AssertPerformanceBudget(p95 <= thresholdMs,
            $"[AfterScanDrainB1] quiescent B1 reconciliation p95 {p95:F3} ms over {prunedSample:N0} pruned aliases " +
            $"exceeds {thresholdMs} ms (docCount={_docCount}). Set YAGU_INDEX_B1_P95_MS to override.");
    }

    [Fact]
    public void ColdWorkerQuery_P95_UnderThreshold()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            _output.WriteLine("[ColdWorkerQuery] Yagu.IndexWorker.exe not built into an app bin — skipping " +
                "(validated on a dev box with the worker present).");
            return; // self-gate: the worker isn't built on this machine
        }

        // Persist a generation's content.bin so a fresh worker can verify + query it.
        ContentIndexGeneration gen = BuildGeneration(_docCount, out _);
        string genDir = Path.Combine(Path.GetTempPath(), "yagu-index-bench-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(genDir);
        try
        {
            ContentIndexGenerationSerializer.Write(genDir, gen);
            TrigramExpression query = PlanSelectiveQuery();

            int coldIterations = Math.Max(3, GetEnvInt("YAGU_INDEX_COLD_ITERATIONS", 5));
            var samples = new double[coldIterations];
            int okCount = 0;

            for (int i = 0; i < coldIterations; i++)
            {
                // A FRESH client each iteration = a fresh worker process = a genuine cold query.
                using var client = new IndexWorkerClient(workerPathOverride: workerExe);
                var source = new IndexWorkerQuerySource(client);
                var sw = Stopwatch.StartNew();
                bool ok = source.TryEvaluate(genDir, query, out _);
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
                if (ok) okCount++;
            }

            (double p50, double p95, double max) = Percentiles(samples);
            double thresholdMs = GetEnvInt("YAGU_INDEX_COLD_P95_MS", 250);

            Record("ColdWorkerQuery", new()
            {
                ["docCount"] = _docCount,
                ["coldIterations"] = coldIterations,
                ["okCount"] = okCount,
                ["p50Ms"] = Math.Round(p50, 2),
                ["p95Ms"] = Math.Round(p95, 2),
                ["maxMs"] = Math.Round(max, 2),
                ["thresholdMs"] = thresholdMs,
            });

            Assert.True(okCount > 0, "[ColdWorkerQuery] no cold worker query succeeded — worker/engine mismatch?");
            AssertPerformanceBudget(p95 <= thresholdMs,
                $"[ColdWorkerQuery] cold p95 {p95:F1} ms exceeds {thresholdMs} ms " +
                $"(docCount={_docCount}). Set YAGU_INDEX_COLD_P95_MS to override on slow hardware.");
        }
        finally
        {
            try { Directory.Delete(genDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void WorkerQueryScope_ClassifiesLikeTheOracle_WithPagedWorkerFootprint()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            _output.WriteLine("[WorkerQueryScope] Yagu.IndexWorker.exe not built into an app bin — skipping " +
                "(validated on a dev box with the worker present).");
            return; // self-gate: the worker isn't built on this machine
        }

        // Persist a generation's format-v3 structures so the worker can MAP (not deserialize) them.
        ContentIndexGeneration gen = BuildGeneration(_docCount, out _, out IReadOnlyList<string> paths, BenchIdentityProvider());
        string v3Dir = Path.Combine(Path.GetTempPath(), "yagu-index-bench-v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(v3Dir);
        try
        {
            ContentIndexV3Format.Write(v3Dir, gen);
            long v3Bytes = DirectoryBytes(v3Dir);
            TrigramExpression query = PlanSelectiveQuery();
            IReadOnlySet<int> candidates = gen.Postings.EvaluateSet(query);
            var oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());

            // Classify EVERY discovered path so the worker faults in most of its mapped path index — the page
            // set that would otherwise be an ~8x managed deserialize in the main process.
            string[] batch = paths.ToArray();

            using var client = new IndexWorkerClient(workerPathOverride: workerExe);
            var scope = new ContentIndexShadowClassifier.ShadowScope(
                1, v3Dir, System.Array.Empty<string>(),
                candidates, System.Array.Empty<IReadOnlySet<int>>(),
                new HashSet<long>(), System.Array.Empty<IReadOnlySet<long>>());
            var classifier = new ContentIndexShadowClassifier(client);

            ContentIndexShadowClassifier.ShadowMetrics metrics = classifier
                .RunAsync(scope, batch, p => IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(p)), System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();

            long workerWsBytes = client.WorkerPeakWorkingSetBytes;
            double workerWsMb = workerWsBytes / (double)Megabyte;
            IndexQueryOpenDiagnostics? diagnostics = metrics.OpenDiagnostics;

            Record("WorkerQueryScope", new()
            {
                ["docCount"] = _docCount,
                ["pathBatch"] = batch.Length,
                ["candidates"] = metrics.CandidateCount,
                ["accelerable"] = metrics.Accelerable,
                ["mismatches"] = metrics.MismatchCount,
                ["openMs"] = metrics.OpenMs,
                ["classifyMs"] = metrics.ClassifyMs,
                ["layerCount"] = diagnostics?.LayerCount ?? 0,
                ["pathRecords"] = diagnostics?.PathRecordCount ?? 0,
                ["tombstoneRecords"] = diagnostics?.TombstoneRecordCount ?? 0,
                ["distinctRouteHashes"] = diagnostics?.DistinctRouteHashCount ?? 0,
                ["supersededRouteRecords"] = diagnostics?.SupersededRouteRecordCount ?? 0,
                ["routeRecordAmplification"] = Math.Round(diagnostics?.RouteRecordAmplification ?? 0, 4),
                ["candidatesEvaluatedInWorker"] = diagnostics?.CandidatesEvaluatedInWorker ?? false,
                ["mapOpenMs"] = Math.Round(diagnostics?.MapOpenMs ?? 0, 2),
                ["candidateEvaluationMs"] = Math.Round(diagnostics?.CandidateEvaluationMs ?? 0, 2),
                ["routingIndexMs"] = Math.Round(diagnostics?.RoutingIndexMs ?? 0, 2),
                ["workerOpenMs"] = Math.Round(diagnostics?.WorkerOpenMs ?? 0, 2),
                ["hostRoundTripMs"] = Math.Round(diagnostics?.HostRoundTripMs ?? 0, 2),
                ["v3Bytes"] = v3Bytes,
                ["workerPeakWsBytes"] = workerWsBytes,
                ["workerWsMb"] = Math.Round(workerWsMb, 1),
                ["workerWsOverV3"] = v3Bytes > 0 ? Math.Round(workerWsBytes / (double)v3Bytes, 2) : 0,
            });

            Assert.True(metrics.Accelerable, metrics.BypassReason);
            Assert.NotNull(diagnostics);
            Assert.Equal(0, metrics.MismatchCount); // worker classification == in-process oracle (never prunes wrongly)

            // Paged, not an ~8x in-process deserialize: the worker's peak resident set stays under a generous
            // absolute bound (mapped-page working set + a fresh-.NET baseline), not proportional to an 8x
            // managed-object expansion of the index size.
            double wsBudgetMb = GetEnvInt("YAGU_INDEX_WORKER_WS_MB", 512);
            AssertPerformanceBudget(workerWsBytes == 0 || workerWsMb <= wsBudgetMb,
                $"[WorkerQueryScope] worker peak WS {workerWsMb:F0} MB exceeds {wsBudgetMb} MB " +
                $"(docCount={_docCount}, v3={v3Bytes / Megabyte} MB). Set YAGU_INDEX_WORKER_WS_MB to override.");

            // Stage-6 warm-path gate (§5.8): the worker's COLD mapped-open must stay cheap — this is the
            // precondition that justifies skipping the in-process warm on the worker path (MainViewModel.
            // StartContentIndexWarmup returns early when IndexUseWorkerQuerySessions is on). openMs is a genuine
            // cold open (fresh worker PROCESS launch + mmap + candidate evaluation), so a worker-served scope
            // accelerates on the FIRST search without any warm-and-defer. Measured ~114 ms on a 20k-doc / ~18 MB
            // v3 here; a generous, env-overridable bound guards against a regression that would make the mapped
            // open expensive (which would make the warm-skip hurt first-search latency).
            double openBudgetMs = GetEnvInt("YAGU_INDEX_MAPPED_OPEN_MS", 400);
            AssertPerformanceBudget(metrics.OpenMs <= openBudgetMs,
                $"[WorkerQueryScope] cold mapped-open {metrics.OpenMs} ms exceeds {openBudgetMs} ms " +
                $"(docCount={_docCount}, v3={v3Bytes / Megabyte} MB). Set YAGU_INDEX_MAPPED_OPEN_MS to override on slow hardware.");
        }
        finally
        {
            try { Directory.Delete(v3Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static long DirectoryBytes(string dir)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* best effort */ }
        }
        return total;
    }

    // ───────────────────────── Corpus + query helpers ─────────────────────────

    private ContentIndexGeneration BuildGeneration(int docCount, out int rareDocs)
        => BuildGeneration(docCount, out rareDocs, out _);

    private ContentIndexGeneration BuildGeneration(int docCount, out int rareDocs, out IReadOnlyList<string> paths)
        => BuildGeneration(docCount, out rareDocs, out paths, identityProvider: null);

    private ContentIndexGeneration BuildGeneration(int docCount, out int rareDocs, out IReadOnlyList<string> paths, Func<string, FileIdentity?>? identityProvider)
    {
        // Open policy: index everything, no size cap, include hidden/reparse irrelevant (synthetic bytes).
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        var builder = new ContentIndexGenerationBuilder(policy, identityProvider: identityProvider);

        string root = @"C:\bench";
        var pathList = new List<string>(docCount);
        // The rare needle lands in ~0.5% of docs so a query for it is highly selective (< 5% candidates).
        int stride = Math.Max(50, docCount / 200);
        int rare = 0;

        byte[] commonBytes = Encoding.UTF8.GetBytes(BuildCommonBody(lines: 80));
        for (int i = 0; i < docCount; i++)
        {
            string path = $@"C:\bench\dir{i % 64}\file{i}.txt";
            pathList.Add(IndexScopeIdentity.NormalizePath(path));

            if (i % stride == 0)
            {
                rare++;
                byte[] withNeedle = Encoding.UTF8.GetBytes(BuildCommonBody(lines: 80) + "\n" + RareNeedle + " token line\n");
                builder.AddDocument(path, withNeedle);
            }
            else
            {
                builder.AddDocument(path, commonBytes);
            }
        }

        rareDocs = rare;
        paths = pathList;
        return builder.Build(
            ContentIndexManager.ScopeIdForRoot(root),
            "bench-vol",
            root,
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow);
    }

    private static string BuildCommonBody(int lines)
    {
        var sb = new StringBuilder(lines * 48);
        for (int i = 0; i < lines; i++)
            sb.Append("the quick brown fox jumps over the lazy dog ").Append(i).Append('\n');
        return sb.ToString();
    }

    private static TrigramExpression PlanSelectiveQuery()
    {
        // Case-SENSITIVE (v1 acceleration is case-sensitive only) selective literal query.
        var options = new SearchOptions
        {
            Directory = @"C:\bench",
            Query = RareNeedle,
            CaseSensitive = true,
            UseContentIndex = true,
        };
        EffectiveSearchPattern pattern = EffectiveSearchPattern.Resolve(options);
        TrigramPlan plan = TrigramQueryPlanner.Plan(pattern);
        if (plan is TrigramPlan.Eligible eligible)
            return eligible.Query;
        throw new InvalidOperationException($"Benchmark query unexpectedly ineligible: {(plan as TrigramPlan.Ineligible)?.Reason}");
    }

    private static Func<string, FileIdentity?> BenchIdentityProvider()
    {
        // Deterministic distinct non-null identity per path (FNV-1a) so admitted content is USN-dirtyable and
        // therefore genuinely prunable — a null identity would (correctly) force a live scan for that content.
        return path =>
        {
            ulong hash = 1469598103934665603UL;
            string norm = IndexScopeIdentity.NormalizePath(path);
            foreach (char c in norm) { hash ^= c; hash *= 1099511628211UL; }
            return new FileIdentity(0x5UL, new UsnFileIdentity(hash, 0));
        };
    }

    // ───────────────────────── Metrics helpers ─────────────────────────

    private static (double p50, double p95, double max) Percentiles(double[] samples)
    {
        if (samples.Length == 0)
            return (0, 0, 0);
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        double At(double q)
        {
            int idx = (int)Math.Ceiling(q * sorted.Length) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }
        return (At(0.50), At(0.95), sorted[^1]);
    }

    private void Record(string scenario, Dictionary<string, object> metrics)
    {
        metrics["scenario"] = scenario;
        metrics["timestampUtc"] = DateTime.UtcNow.ToString("O");
        metrics["machineName"] = Environment.MachineName;
        metrics["processorCount"] = Environment.ProcessorCount;
        metrics["is64Bit"] = Environment.Is64BitProcess;

        foreach (var kv in metrics.OrderBy(k => k.Key))
            _output.WriteLine($"  {kv.Key}: {kv.Value}");

        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(ContentIndexBenchmarkTests).Assembly.Location)!;
            var solutionRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
            var baselineDir = Path.Combine(solutionRoot, "Yagu.Benchmarks", "results");
            Directory.CreateDirectory(baselineDir);
            var baselinePath = Path.Combine(baselineDir, "content-index-baselines.jsonl");
            string json = JsonSerializer.Serialize(metrics);
            File.AppendAllText(baselinePath, json + Environment.NewLine);
            _output.WriteLine($"  → Baseline appended to {baselinePath}");
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

    private void AssertPerformanceBudget(bool condition, string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("YAGU_BENCHMARK_COVERAGE_MODE"), "1", StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine("Performance budget assertion skipped for instrumented coverage collection.");
            return;
        }

        Assert.True(condition, message);
    }

    // ───────────────────────── Worker discovery (mirrors IndexWorkerQuerySourceTests) ─────────────────────────

    private static string? FindWorkerExe()
    {
        string repoRoot = FindRepoRoot();
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(repoRoot, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
