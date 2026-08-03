using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Stage-7 whole-drive sign-off — the §4.2 <b>canonical result-multiset</b> correctness gate, automated
/// end-to-end against a synthetic scope (the plan's §4.3 explicitly permits "a real <b>or synthetic</b> 1M+
/// path scope"). Unlike the pipeline / scope-builder tests, which drive the pruning scan directly, these tests
/// run the <b>whole production stack</b> through <see cref="SearchService"/>: real on-disk corpus → published
/// format-v3 store → the real bundled index worker maps + classifies → prune / survive / rescue →
/// <see cref="SearchService"/> result assembly. The linchpin invariant is that the accelerated (worker-pruning)
/// result multiset — <c>(path, line, column, length, match-line)</c> — is <b>identical</b> to a plain
/// index-disabled live scan across a query battery, that a file changed during the search is still found
/// (dirty rescue), and that a missing worker degrades to a live scan with no result loss. The real-worker tests
/// self-gate when the worker binary is not built (like every other real-worker test); the worker-missing
/// fallback test always runs.
/// </summary>
public sealed class ContentIndexWorkerPathParityTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;
    private readonly IContentIndexPathProvider _paths;
    private ContentIndexStore? _store;
    private int _session;

    public ContentIndexWorkerPathParityTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-wp-parity", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "corpus");
        Directory.CreateDirectory(_root);
        string storage = Path.Combine(_sandbox, "storage");
        Directory.CreateDirectory(storage);
        _paths = new DefaultContentIndexPathProvider(storage, storage);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // A mixed corpus: members (contain a searched term), genuine nonmembers (prunable), an all-nonmember filler,
    // a multi-match file, a case-differing file, and one file left OUT of the index (Unindexed → never pruned).
    private (string[] All, string[] Indexed) BuildCorpus()
    {
        var spec = new (string Rel, string Content, bool Index)[]
        {
            ("a.txt",            "the planner emits trigram queries",   true),
            ("sub/b.txt",        "another planner note here",           true),
            ("c.txt",            "lorem ipsum dolor sit amet",          true),
            ("d.txt",            "trigram indexing is useful",          true),
            ("e.txt",            "wholly unrelated stuff here",         true),
            ("sub/deep/f.txt",   "planner planner planner repeats",     true),
            ("g.txt",            "nothing to see in this file",         true),
            ("h.txt",            "TriGram case differs here",           true),
            ("i.txt",            "the planner and the trigram meet",    true),
            ("j.txt",            "just some filler content only",       true),
            ("unindexed.txt",    "planner appears here but not indexed", false),
        };
        var all = new List<string>();
        var indexed = new List<string>();
        foreach ((string rel, string content, bool index) in spec)
        {
            string p = WriteFile(rel, content);
            all.Add(p);
            if (index) indexed.Add(p);
        }
        return (all.ToArray(), indexed.ToArray());
    }

    // Publishes a format-v3 generation built from the real on-disk files, with a deterministic identity
    // provider and a fixed build checkpoint (so freshness is independent of the machine's USN journal). The
    // store keeps its v3 query structures so the worker can MAP (not deserialize) the scope.
    private void PublishGeneration(params string[] absolutePaths)
    {
        ulong next = 900;
        FileIdentity? Provider(string path) => new(0x7, new UsnFileIdentity(next++, 0));

        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        foreach (string path in absolutePaths)
            builder.AddDocument(path, File.ReadAllBytes(path));
        ContentIndexGeneration gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        _store = new ContentIndexStore(_paths, scopeId, retainedGenerations: 2) { ProduceV3QueryStructures = true };
        _store.Publish(gen);
    }

    // A deterministic journal reader that reports a continuous read with no changes → every prune is certain.
    private static ContentIndexFreshnessEvaluator.JournalReader OkReader()
        => (root, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());

    private SearchOptions MakeOptions(string query, bool caseSensitive, bool useContentIndex)
        => new()
        {
            Directory = _root,
            Query = query,
            ExactMatch = false,       // substring → Literals family (accelerable when case-sensitive)
            CaseSensitive = caseSensitive,
            SearchMode = SearchMode.Content,
            MaxResults = 50_000,
            MaxFileSizeBytes = 0,
            SkipBinary = true,
            UseContentIndex = useContentIndex,
        };

    // The canonical result multiset: every match row as (normalized path, line, column, length, match-line).
    // Sorted, duplicates kept — this is a MULTISET, not a set, so a file with N matches contributes N rows.
    private static async Task<List<string>> CollectMultisetAsync(SearchOptions options)
    {
        var rows = new List<string>();
        var service = new SearchService();
        await foreach (SearchEvent evt in service.SearchAsync(options, CancellationToken.None))
        {
            if (evt is SearchEvent.MatchBatch batch)
                foreach (SearchResult r in batch.Results) rows.Add(Canonical(r));
            else if (evt is SearchEvent.Match m)
                rows.Add(Canonical(m.Result));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static string Canonical(SearchResult r)
        => $"{IndexScopeIdentity.NormalizePath(r.FilePath)}|{r.LineNumber}|{r.MatchStartColumn}|{r.MatchLength}|{r.MatchLine}";

    // Runs the FULL SearchService with the worker pruning path engaged (real worker via the shared client),
    // returning the result multiset and whether the pruning scan actually opened (worker mapped the scope).
    private async Task<(List<string> Multiset, bool Engaged)> RunWorkerPathAsync(
        IndexWorkerClient client, string query, bool caseSensitive)
    {
        bool engaged = false;
        SearchOptions options = MakeOptions(query, caseSensitive, useContentIndex: true);
        string spoolDir = Path.Combine(_sandbox, "spool-" + Guid.NewGuid().ToString("N"));
        int session = Interlocked.Increment(ref _session);
        options.ContentIndexPruningScanFactory = survivorSink =>
        {
            IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
                client, _store!, options, session, OkReader(), spoolDir, survivorSink);
            engaged = scan is not null;
            return scan;
        };
        List<string> multiset = await CollectMultisetAsync(options);
        return (multiset, engaged);
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

    // ── §4.2 canonical-multiset gate: accelerated (worker pruning) == index-disabled live scan ──

    [Fact]
    public async Task WorkerPruningPath_ResultMultiset_MatchesLiveScan_AcrossQueryBattery()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return; // self-gate: the real worker isn't built on this machine

        (string[] _, string[] indexed) = BuildCorpus();
        PublishGeneration(indexed);

        // One long-lived worker client serves the whole battery (as in production), a fresh mapped session per query.
        using var client = new IndexWorkerClient(workerPathOverride: workerExe);

        var battery = new (string Query, bool CaseSensitive, bool ExpectAccelerated)[]
        {
            ("planner",       true,  true),   // multi-file member term
            ("trigram",       true,  true),   // another member term
            ("planner emits", true,  true),   // multi-token substring literal
            ("zzznomatch",    true,  true),   // eligible but empty → every file pruned, no matches
            ("TriGram",       false, false),  // case-insensitive → not accelerated → live-scan fallback
        };

        foreach ((string query, bool caseSensitive, bool expectAccelerated) in battery)
        {
            List<string> baseline = await CollectMultisetAsync(MakeOptions(query, caseSensitive, useContentIndex: false));
            (List<string> accelerated, bool engaged) = await RunWorkerPathAsync(client, query, caseSensitive);

            // The linchpin gate: byte-for-byte identical canonical multiset (path, line, column, length, match-line).
            Assert.Equal(baseline, accelerated);
            if (expectAccelerated)
                Assert.True(engaged, $"the worker pruning path did not engage for '{query}' (it silently live-scanned).");
        }
    }

    // ── Safety: a file changed DURING the search is rescued at B1 and its new match is never hidden ──

    [Fact]
    public async Task WorkerPruningPath_FileChangedDuringSearch_IsRescuedAndFound()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return; // self-gate

        string a = WriteFile("a.txt", "the planner emits trigram queries"); // member
        string b = WriteFile("b.txt", "lorem ipsum dolor sit amet");         // nonmember at build time
        PublishGeneration(a, b);

        // b changes after the build to contain the term (a "changed during search" file).
        File.WriteAllText(b, "now b mentions the planner too", new UTF8Encoding(false));

        // Staged reader: continuous at B0 (b is a fresh prunable nonmember) → a discontinuity at B1 forces an
        // uncertain reconcile → the whole spool is replayed, so every provisionally-pruned path (incl. b) is
        // rescued and content-scanned. Its new match must appear (plan §5.1 #3 across the process boundary).
        int calls = 0;
        ContentIndexFreshnessEvaluator.JournalReader staged = (root, since) =>
        {
            calls++;
            return calls <= 1
                ? new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>())
                : new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>());
        };

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        SearchOptions options = MakeOptions("planner", caseSensitive: true, useContentIndex: true);
        string spoolDir = Path.Combine(_sandbox, "spool-rescue");
        options.ContentIndexPruningScanFactory = survivorSink =>
            ContentIndexShadowScopeBuilder.TryCreatePruningScan(client, _store!, options, sessionId: 1, staged, spoolDir, survivorSink);

        List<string> accelerated = await CollectMultisetAsync(options);
        List<string> baseline = await CollectMultisetAsync(MakeOptions("planner", caseSensitive: true, useContentIndex: false));

        Assert.Equal(baseline, accelerated);
        Assert.Contains(accelerated, r => r.StartsWith(IndexScopeIdentity.NormalizePath(b) + "|", StringComparison.Ordinal));
        Assert.Contains(accelerated, r => r.StartsWith(IndexScopeIdentity.NormalizePath(a) + "|", StringComparison.Ordinal));
    }

    // ── Safety: a missing worker degrades to a live scan with the identical result multiset ──

    [Fact]
    public async Task WorkerPruningPath_WorkerMissing_FallsBackToLiveScan_NoResultLoss()
    {
        (string[] _, string[] indexed) = BuildCorpus();
        PublishGeneration(indexed);

        using var client = new IndexWorkerClient(workerPathOverride: Path.Combine(_sandbox, "no-such-worker.exe"));
        SearchOptions options = MakeOptions("planner", caseSensitive: true, useContentIndex: true);
        string spoolDir = Path.Combine(_sandbox, "spool-missing");
        bool engaged = false;
        options.ContentIndexPruningScanFactory = survivorSink =>
        {
            IContentIndexPruningScan? scan = ContentIndexShadowScopeBuilder.TryCreatePruningScan(
                client, _store!, options, sessionId: 1, OkReader(), spoolDir, survivorSink);
            engaged = scan is not null;
            return scan;
        };

        List<string> accelerated = await CollectMultisetAsync(options);
        List<string> baseline = await CollectMultisetAsync(MakeOptions("planner", caseSensitive: true, useContentIndex: false));

        Assert.False(engaged);              // the worker could not be launched → no pruning scan opened
        Assert.Equal(baseline, accelerated); // and the search still returned the exact live-scan multiset
    }
}
