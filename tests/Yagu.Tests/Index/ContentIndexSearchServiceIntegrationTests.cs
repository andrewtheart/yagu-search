using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// End-to-end integration of the content-index pruning gate (plan §5) with the real
/// <see cref="SearchService"/> streaming pipeline. Publishes a real generation over an on-disk corpus,
/// drives a live search with a deterministic (fake-journal) gate attached via
/// <see cref="SearchOptions.ContentIndexGateFactory"/>, and asserts the two invariants that matter:
/// (1) <b>results equivalence</b> — an accelerated search returns exactly the same file set as a plain
/// live scan (plan §5.1 #2); and (2) <b>the gate actually engaged</b> — non-member files were pruned
/// (<see cref="ContentIndexSearchGate.TotalPruned"/> &gt; 0), proving the pipeline consulted the gate
/// rather than silently full-scanning.
/// </summary>
public sealed class ContentIndexSearchServiceIntegrationTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;
    private readonly IContentIndexPathProvider _paths;

    public ContentIndexSearchServiceIntegrationTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-ss", Guid.NewGuid().ToString("N"));
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
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // Publishes a generation built from the real on-disk files, using a fake identity provider and a
    // fixed build checkpoint so freshness is fully deterministic (no dependency on the machine's USN
    // journal). The paths in the index therefore exactly match the paths SearchService will discover.
    private void PublishGeneration(params string[] absolutePaths)
    {
        ulong next = 900;
        FileIdentity? Provider(string path) => new(0x7, new UsnFileIdentity(next++, 0));

        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        foreach (string path in absolutePaths)
            builder.AddDocument(path, File.ReadAllBytes(path));
        var gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, scopeId).Publish(gen);
    }

    // A deterministic journal reader that reports no changes since the build checkpoint → Continuous.
    private static ContentIndexFreshnessEvaluator.JournalReader OkReader()
        => (path, since) => new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), Array.Empty<UsnChange>());

    private SearchOptions MakeOptions(string query, bool useContentIndex, string? directory = null)
        => new()
        {
            Directory = directory ?? _root,
            Query = query,
            ExactMatch = false,      // substring → Literals family (accelerated)
            CaseSensitive = true,    // v1 only accelerates case-sensitive queries (plan §4)
            SearchMode = SearchMode.Content,
            MaxResults = 50_000,
            MaxFileSizeBytes = 0,
            SkipBinary = true,
            UseContentIndex = useContentIndex,
        };

    private static async Task<List<string>> RunSearchAsync(SearchOptions options)
    {
        var files = new List<string>();
        var service = new SearchService();
        await foreach (var evt in service.SearchAsync(options, CancellationToken.None))
        {
            if (evt is SearchEvent.MatchBatch batch)
                foreach (var r in batch.Results) files.Add(r.FilePath);
            else if (evt is SearchEvent.Match m)
                files.Add(m.Result.FilePath);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public async Task AcceleratedSearch_MatchesLiveScan_AndPrunesNonmembers()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries");   // member
        string b = WriteFile("b.txt", "lorem ipsum dolor sit amet");           // nonmember
        string c = WriteFile(Path.Combine("sub", "c.txt"), "another planner note here"); // member
        string d = WriteFile("d.txt", "wholly unrelated content only");        // nonmember
        PublishGeneration(a, b, c, d);

        // Baseline: a plain live scan (no gate).
        List<string> baseline = await RunSearchAsync(MakeOptions("planner", useContentIndex: false));

        // Accelerated: attach a deterministic gate. Hold a reference so we can inspect engagement.
        var gate = ContentIndexSearchGate.TryCreate(_paths, _root, MakeOptions("planner", useContentIndex: true),
            new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 }, retainedGenerations: 2, journalReader: OkReader());
        Assert.NotNull(gate);

        var accelOptions = MakeOptions("planner", useContentIndex: true);
        accelOptions.ContentIndexGateFactory = () => gate;
        List<string> accelerated = await RunSearchAsync(accelOptions);

        // (1) Results equivalence — the accelerated search returns exactly the member files.
        Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), baseline);
        Assert.Equal(baseline, accelerated);

        // (2) The gate actually engaged: both non-members were pruned from the content scan.
        Assert.Equal(2, gate!.TotalPruned);
        Assert.False(gate.PruningDisabled);
    }

    [Fact]
    public async Task AcceleratedSearch_NonmemberChangedAfterBuild_IsRescuedAndFound()
    {
        string a = WriteFile("a.txt", "the planner emits trigram queries"); // member
        string b = WriteFile("b.txt", "lorem ipsum dolor sit amet");         // nonmember at build time
        PublishGeneration(a, b);

        // b changes after the build to contain the term. A B1 journal reader that reports b dirty must
        // cause it to be rescued and content-scanned, so the new match is never hidden (plan §5.1 #3).
        File.WriteAllText(b, "now b mentions the planner too", new UTF8Encoding(false));

        // Reader: no changes at B0 (build state), but reports b's identity dirty at B1. We don't know b's
        // synthetic identity here, so instead force the fail-safe path — an uncertain B1 rescans every
        // pruned path — by returning a discontinuity at B1. Either way b must be rescued.
        int call = 0;
        ContentIndexFreshnessEvaluator.JournalReader reader = (path, since) =>
        {
            call++;
            return call == 1
                ? new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), Array.Empty<UsnChange>())
                : new UsnReadResult(UsnReadStatus.GapDetected, since, Array.Empty<UsnChange>());
        };

        var gate = ContentIndexSearchGate.TryCreate(_paths, _root, MakeOptions("planner", useContentIndex: true),
            new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 }, retainedGenerations: 2, journalReader: reader);
        Assert.NotNull(gate);

        var options = MakeOptions("planner", useContentIndex: true);
        options.ContentIndexGateFactory = () => gate;
        List<string> results = await RunSearchAsync(options);

        // Both files contain "planner" now; b was pruned at B0 but rescued at B1 → both found.
        Assert.Contains(a, results);
        Assert.Contains(b, results);
    }

    [Fact]
    public async Task ParentIndex_AcceleratesChildSearch_WithoutOutsideOrDuplicateResults()
    {
        string child = Path.Combine(_root, "src");
        string insideMatch = WriteFile(Path.Combine("src", "inside.txt"), "planner inside child");
        string insideNonmember = WriteFile(Path.Combine("src", "other.txt"), "unrelated child content");
        string outsideMatch = WriteFile("outside.txt", "planner outside requested child");
        PublishGeneration(insideMatch, insideNonmember, outsideMatch);

        // Discovery remains child-scoped even though the index root is the parent.
        SearchOptions options = MakeOptions("planner", useContentIndex: true, directory: child);
        var gate = ContentIndexSearchGate.TryCreate(
            _paths,
            _root,
            options,
            new AppSettings { EnableContentIndex = true, IndexMaxCandidatePercent = 100 },
            retainedGenerations: 2,
            journalReader: OkReader());
        Assert.NotNull(gate);
        options.ContentIndexGateFactory = () => gate;

        List<string> results = await RunSearchAsync(options);

        Assert.Equal(new[] { insideMatch }, results);
        Assert.DoesNotContain(outsideMatch, results);
        Assert.Equal(1, gate!.TotalPruned); // Only the discovered child nonmember was pruned.
    }
}
