using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="IndexWorkerQuerySource"/> — the <see cref="IIndexCandidateSource"/> backed by the
/// out-of-process worker. The failure-degradation contract is unit-tested; the full end-to-end path (launch
/// the real <c>Yagu.IndexWorker.exe</c> → verify + query <c>content.bin</c> natively → candidate ids identical
/// to the in-process posting evaluation) is an integration test that self-gates when the worker isn't built.
/// </summary>
public sealed class IndexWorkerQuerySourceTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;

    public IndexWorkerQuerySourceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-worker-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private (ContentIndexGeneration Generation, string GenerationDir) PublishGeneration()
    {
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("the lazy dog sleeps while the quick fox runs"));
        builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("brown foxes and quick rabbits everywhere"));
        builder.AddDocument(@"C:\r\d.txt", Encoding.UTF8.GetBytes("nothing whatsoever in common with the others here"));
        var gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        var store = new ContentIndexStore(_paths, scopeId);
        store.Publish(gen);
        ContentIndexGeneration reopened = store.TryOpenCurrent(out string? dir)!;
        return (reopened, dir!);
    }

    private static TrigramExpression PlanFor(string term)
    {
        var options = new SearchOptions { Directory = @"C:\r", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        var pattern = EffectiveSearchPattern.Resolve(options);
        return TrigramQueryPlanner.Plan(pattern) is TrigramPlan.Eligible eligible
            ? eligible.Query
            : throw new InvalidOperationException($"'{term}' is not index-eligible");
    }

    // ── Failure-degradation contract (no worker process) ──

    [Fact]
    public void TryEvaluate_MissingContentBin_ReturnsFalse()
    {
        // A generation dir with no content.bin → source returns false (caller falls back in-process).
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        var source = new IndexWorkerQuerySource(client);
        bool ok = source.TryEvaluate(_sandbox, PlanFor("quick"), out IReadOnlySet<int> candidates);
        Assert.False(ok);
        Assert.Empty(candidates);
    }

    [Fact]
    public void TryEvaluate_WorkerUnavailable_ReturnsFalse()
    {
        // content.bin exists but the worker exe path is bogus → the client never becomes ready → false.
        (_, string dir) = PublishGeneration();
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        var source = new IndexWorkerQuerySource(client, timeout: TimeSpan.FromSeconds(3));
        bool ok = source.TryEvaluate(dir, PlanFor("quick"), out IReadOnlySet<int> candidates);
        Assert.False(ok);
        Assert.Empty(candidates);
    }

    [Fact]
    public void TryEvaluate_InternalFailure_ReturnsFalse()
    {
        (_, string dir) = PublishGeneration();
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        var source = new IndexWorkerQuerySource(client, timeout: TimeSpan.MaxValue);

        bool ok = source.TryEvaluate(dir, PlanFor("quick"), out IReadOnlySet<int> candidates);

        Assert.False(ok);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IndexWorkerQuerySource(null!));
    }

    [Fact]
    public void TryEvaluate_EmptyGenerationDir_ReturnsFalse()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        var source = new IndexWorkerQuerySource(client);
        Assert.False(source.TryEvaluate(string.Empty, PlanFor("quick"), out IReadOnlySet<int> candidates));
        Assert.Empty(candidates);
    }

    [Fact]
    public void TryEvaluate_NullQuery_ReturnsFalse()
    {
        using var client = new IndexWorkerClient(workerPathOverride: NonexistentWorker());
        var source = new IndexWorkerQuerySource(client);
        Assert.False(source.TryEvaluate(_sandbox, query: null!, out IReadOnlySet<int> candidates));
        Assert.Empty(candidates);
    }

    // ── End-to-end with the REAL worker (self-gates when not built) ──

    [Fact]
    public void TryEvaluate_RealWorker_MatchesInProcessPostingEvaluation()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
        {
            return; // worker not built into an app bin on this machine → skip (validated on the dev box)
        }

        (ContentIndexGeneration gen, string dir) = PublishGeneration();

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        var source = new IndexWorkerQuerySource(client);

        foreach (string term in new[] { "quick", "lazy", "brown", "fox", "the", "zzzzz", "dog" })
        {
            TrigramExpression query = PlanFor(term);
            var managed = new HashSet<int>(gen.Postings.EvaluateSet(query));

            bool ok = source.TryEvaluate(dir, query, out IReadOnlySet<int> workerCandidates);

            Assert.True(ok, $"worker query for '{term}' should succeed");
            Assert.True(managed.SetEquals(workerCandidates),
                $"term '{term}': managed=[{string.Join(",", managed.OrderBy(x => x))}] worker=[{string.Join(",", workerCandidates.OrderBy(x => x))}]");
        }
    }

    private static string NonexistentWorker()
        => Path.Combine(Path.GetTempPath(), "yagu-no-such-index-worker-" + Guid.NewGuid().ToString("N") + ".exe");

    /// <summary>Locates the bundled <c>Yagu.IndexWorker.exe</c> beside a built app (Debug or Release), or null
    /// when it hasn't been built (CI test-only runs) so the integration test self-skips.</summary>
    private static string? FindWorkerExe()
    {
        string repoRoot = FindRepoRoot();
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(repoRoot, "src", "Yagu", "bin", cfg, tfm, "index-worker", "Yagu.IndexWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Yagu.slnx).");
    }
}
