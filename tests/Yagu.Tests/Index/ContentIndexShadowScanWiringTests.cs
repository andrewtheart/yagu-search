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
/// Verifies the Stage-3 <see cref="IContentIndexShadowScan"/> wiring into <see cref="SearchService"/> (plan
/// §5.3, slice 2c). The shadow scan must be offered EVERY content-scan candidate path and completed once
/// discovery drains, but — being shadow — it must never change the result set, and a shadow fault must never
/// break the search. A source-pin locks the three wiring points.
/// </summary>
public sealed class ContentIndexShadowScanWiringTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;

    public ContentIndexShadowScanWiringTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-shadow-wire", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "corpus");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingShadowScan : IContentIndexShadowScan
    {
        private readonly object _gate = new();
        public readonly List<string> Offered = new();
        public int CompletedCount;
        public bool ThrowOnOffer;

        public ValueTask OfferAsync(string normalizedPath, CancellationToken cancellationToken)
        {
            if (ThrowOnOffer)
                throw new InvalidOperationException("shadow offer boom");
            lock (_gate)
                Offered.Add(normalizedPath);
            return ValueTask.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CompletedCount);
            return Task.CompletedTask;
        }
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
        return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public async Task ShadowScan_IsOfferedEveryCandidate_AndCompleted_ButNeverChangesResults()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries");
        string b = WriteFile("b.txt", "lorem ipsum dolor sit amet");
        string c = WriteFile("c.txt", "another planner note here");
        string d = WriteFile("d.txt", "wholly unrelated content only");

        List<string> baseline = await RunSearchAsync(MakeOptions("planner"));

        var shadow = new RecordingShadowScan();
        SearchOptions options = MakeOptions("planner");
        options.ContentIndexShadowScanFactory = () => shadow;
        List<string> withShadow = await RunSearchAsync(options);

        // Shadow never changes the result set.
        Assert.Equal(baseline, withShadow);
        Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), baseline);

        // Every content-scan candidate was offered (as a normalized path), and the scan completed it once.
        var expected = new[] { a, b, c, d }.Select(IndexScopeIdentity.NormalizePath).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, shadow.Offered.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(1, shadow.CompletedCount);
    }

    [Fact]
    public async Task ShadowScan_ThatFaults_NeverBreaksTheSearch_NorChangesResults()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries");
        WriteFile("b.txt", "lorem ipsum dolor sit amet");
        string c = WriteFile("c.txt", "another planner note here");

        List<string> baseline = await RunSearchAsync(MakeOptions("planner"));

        var shadow = new RecordingShadowScan { ThrowOnOffer = true };
        SearchOptions options = MakeOptions("planner");
        options.ContentIndexShadowScanFactory = () => shadow;
        List<string> withFaultingShadow = await RunSearchAsync(options);

        // A shadow offer that throws disables shadow but never affects the search results.
        Assert.Equal(baseline, withFaultingShadow);
        Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), withFaultingShadow);
    }

    [Fact]
    public void SearchService_WiresTheShadowScan_AtB0_ChokepointAndFinally()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SearchService.cs"));

        // Created once at B0 alongside the gate.
        Assert.Contains("options.ContentIndexShadowScanFactory?.Invoke()", source);
        // Offered at the content-scan chokepoint (fail-safe: an offer fault disables shadow, not the search).
        Assert.Contains("shadowScan.OfferAsync(normalizedForIndex, cancellationToken)", source);
        Assert.Contains("shadowScan = null;", source);
        // Completed in the discovery finally (drains + closes even on cancel/error).
        Assert.Contains("shadowScan.CompleteAsync(cancellationToken)", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
