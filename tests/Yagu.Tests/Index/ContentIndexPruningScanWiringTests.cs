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
/// Verifies the Stage-4 <see cref="IContentIndexPruningScan"/> wiring into <see cref="SearchService"/> (plan
/// §5.3/§5.5, slice 3c). Unlike shadow mode, this pipeline actually skips files: it is offered every
/// content-scan candidate, forwards survivors to the pending-scan channel, prunes proven-nonmembers, and
/// rescues the dirty subset at B1. The linchpin invariant is that the result multiset is <b>identical to a
/// live scan</b> — pruning genuine nonmembers changes nothing, a rescued path is re-scanned so its match is
/// found, and any offer fault degrades to scanning that path live. A source-pin locks the wiring points.
/// </summary>
public sealed class ContentIndexPruningScanWiringTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;

    public ContentIndexPruningScanWiringTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-prune-wire", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "corpus");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A driveable in-memory pruning scan: it forwards every offered SCAN path to the survivor sink UNLESS its
    /// normalized (CLASSIFY) path is in the prune set, in which case it is provisionally pruned (recorded, not
    /// forwarded). At B1 it returns the configured rescue paths. This exercises the SearchService wiring
    /// without a worker — the real prune/rescue correctness lives in the pipeline + scope-builder tests.
    /// </summary>
    private sealed class FakePruningScan : IContentIndexPruningScan
    {
        private readonly Func<string, CancellationToken, ValueTask> _sink;
        private readonly HashSet<string> _pruneNormalized;
        private readonly List<string> _rescue;
        private readonly bool _throwOnOffer;
        private readonly object _gate = new();
        public readonly List<(string Scan, string Classify)> Offered = new();
        public int CompleteOfferingCount;
        public int ReconcileCount;
        private long _grossPruned;

        public FakePruningScan(
            Func<string, CancellationToken, ValueTask> sink,
            HashSet<string> pruneNormalized,
            IEnumerable<string>? rescue = null,
            bool throwOnOffer = false)
        {
            _sink = sink;
            _pruneNormalized = pruneNormalized;
            _rescue = rescue?.ToList() ?? new List<string>();
            _throwOnOffer = throwOnOffer;
        }

        public async ValueTask OfferAsync(string scanPath, string classifyPath, CancellationToken cancellationToken)
        {
            if (_throwOnOffer)
                throw new InvalidOperationException("prune offer boom");
            lock (_gate)
                Offered.Add((scanPath, classifyPath));
            if (_pruneNormalized.Contains(classifyPath))
                Interlocked.Increment(ref _grossPruned); // pruned → NOT forwarded
            else
                await _sink(scanPath, cancellationToken).ConfigureAwait(false); // survivor → forwarded as the original path
        }

        public Task CompleteOfferingAsync()
        {
            Interlocked.Increment(ref CompleteOfferingCount);
            return Task.CompletedTask;
        }

        public Task CleanupAsync() => Task.CompletedTask;

        public Task<PruningScanResult> ReconcileAtB1Async(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReconcileCount);
            long gross = Interlocked.Read(ref _grossPruned);
            return Task.FromResult(new PruningScanResult(true, _rescue, gross, _rescue.Count));
        }

        public bool WasIndexMember(string normalizedPath) => false; // provenance is exercised at the pipeline level
    }

    private string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private SearchOptions MakeOptions(string query)
        => new()
        {
            Directory = _root,
            Query = query,
            ExactMatch = false,
            CaseSensitive = true,
            SearchMode = SearchMode.Content,
            MaxResults = 50_000,
            MaxFileSizeBytes = 0,
            SkipBinary = true,
        };

    private static async Task<List<string>> RunSearchAsync(SearchOptions options)
    {
        var files = new List<string>();
        var service = new SearchService();
        await foreach (SearchEvent evt in service.SearchAsync(options, CancellationToken.None))
        {
            if (evt is SearchEvent.MatchBatch batch)
                foreach (SearchResult r in batch.Results) files.Add(r.FilePath);
            else if (evt is SearchEvent.Match m)
                files.Add(m.Result.FilePath);
        }
        return files;
    }

    private static List<string> NormalizedSet(IEnumerable<string> paths)
        => paths.Select(IndexScopeIdentity.NormalizePath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task PruningScan_ForwardsSurvivors_PrunesNonmembers_ResultMultisetUnchanged()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries"); // matches
        string b = WriteFile("b.txt", "lorem ipsum dolor sit amet");        // nonmember (prunable)
        string c = WriteFile("c.txt", "another planner note here");         // matches
        string d = WriteFile("d.txt", "wholly unrelated content only");     // nonmember (prunable)

        List<string> baseline = await RunSearchAsync(MakeOptions("planner"));
        Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), baseline.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        // Prune the two genuine nonmembers (b, d). Survivors (a, c) are forwarded as their ORIGINAL paths.
        var prune = new[] { b, d }.Select(IndexScopeIdentity.NormalizePath).ToHashSet(StringComparer.Ordinal);
        FakePruningScan? captured = null;
        SearchOptions options = MakeOptions("planner");
        options.ContentIndexPruningScanFactory = sink => captured = new FakePruningScan(sink, prune);

        List<string> withPruning = await RunSearchAsync(options);

        // The result multiset is identical to the live scan (pruning nonmembers changes nothing).
        Assert.Equal(NormalizedSet(baseline), NormalizedSet(withPruning));
        Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), withPruning.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        // Every candidate was offered; the two nonmembers were pruned (not forwarded / scanned).
        Assert.NotNull(captured);
        var offeredClassify = captured!.Offered.Select(o => o.Classify).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[] { a, b, c, d }.Select(IndexScopeIdentity.NormalizePath).ToHashSet(StringComparer.Ordinal), offeredClassify);
        Assert.Equal(1, captured.CompleteOfferingCount);
        Assert.Equal(1, captured.ReconcileCount);
    }

    [Fact]
    public async Task PruningScan_RescuedPath_IsReScanned_SoItsMatchIsStillFound()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries"); // matches, but we prune it at B0…
        string c = WriteFile("c.txt", "another planner note here");         // matches

        List<string> baseline = await RunSearchAsync(MakeOptions("planner"));

        // Prune a.txt at B0 (as if a fresh nonmember) but rescue it at B1 (it "changed during the search") →
        // the rescue re-scan must still surface a.txt's match, so the result set matches the live scan.
        string aNorm = IndexScopeIdentity.NormalizePath(a);
        var prune = new HashSet<string>(StringComparer.Ordinal) { aNorm };
        SearchOptions options = MakeOptions("planner");
        options.ContentIndexPruningScanFactory = sink => new FakePruningScan(sink, prune, rescue: new[] { aNorm });

        List<string> withRescue = await RunSearchAsync(options);

        // Compared in normalized form (a B1 rescue scans the normalized path, like the in-process gate).
        Assert.Equal(NormalizedSet(baseline), NormalizedSet(withRescue));
        Assert.Contains(aNorm, NormalizedSet(withRescue));
        Assert.Contains(IndexScopeIdentity.NormalizePath(c), NormalizedSet(withRescue));
    }

    [Fact]
    public async Task PruningScan_OfferFault_ScansEveryPathLive_NeverLosesAMatch()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries");
        WriteFile("b.txt", "lorem ipsum dolor sit amet");
        string c = WriteFile("c.txt", "another planner note here");

        List<string> baseline = await RunSearchAsync(MakeOptions("planner"));

        // Every offer throws → the search scans that path live (the SearchService catch forwards it) → the
        // result set is unchanged. The pruning scan is kept (its B1 reconcile still runs, harmlessly).
        var prune = new HashSet<string>(StringComparer.Ordinal);
        SearchOptions options = MakeOptions("planner");
        options.ContentIndexPruningScanFactory = sink => new FakePruningScan(sink, prune, throwOnOffer: true);

        List<string> withFault = await RunSearchAsync(options);

        Assert.Equal(NormalizedSet(baseline), NormalizedSet(withFault));
        Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), withFault.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchService_WiresThePruningScan_AtB0_Chokepoint_Drain_AndB1()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SearchService.cs"));

        // Created once at B0 with the survivor sink; supersedes the in-process gate.
        Assert.Contains("options.ContentIndexPruningScanFactory?.Invoke((p, _) => WritePendingFileAsync(p))", source);
        Assert.Contains("if (pruningScan is not null)", source);
        Assert.Contains("contentIndexGate = null;", source);
        // Offered (original + normalized) at the content-scan chokepoint.
        Assert.Contains("await pruningScan.OfferAsync(path, normalizedForIndex, cancellationToken)", source);
        // Drained before the pending-scan channel is completed.
        Assert.Contains("await pruningScan.CompleteOfferingAsync()", source);
        // Reconciled at B1 after the scan drains, feeding rescues into the shared rescue scan.
        Assert.Contains("await pruningScan!.ReconcileAtB1Async(cancellationToken)", source);
        Assert.Contains("Interlocked.Add(ref filesScanned, (int)result.GrossPruned)", source);
        // A guaranteed pipeline-level backstop runs with no search token after discovery/workers complete.
        Assert.Contains("await pruningScan.CleanupAsync().ConfigureAwait(false);", source);
    }

    [Fact]
    public void SearchOptions_ExposesContentIndexPruningScanFactory()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Models", "SearchOptions.cs"));
        Assert.Contains("ContentIndexPruningScanFactory", source);
        Assert.Contains("Services.Index.IContentIndexPruningScan?", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
