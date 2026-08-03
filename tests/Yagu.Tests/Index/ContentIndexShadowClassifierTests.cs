using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexShadowClassifier"/> — the Stage-2 shadow-mode consumer. It runs a
/// scope's per-path classification in the out-of-process mapped query worker and its verdicts must match the
/// in-process oracle for every path (proven against the real bundled worker, self-gated when it is not
/// built), while never pruning. Its fail-safe contract (worker unavailable → non-accelerable, never throws)
/// is covered without a worker.
/// </summary>
public sealed class ContentIndexShadowClassifierTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-shadow", Guid.NewGuid().ToString("N"));

    public ContentIndexShadowClassifierTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration BuildBaseGeneration(string root)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(root + "\\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        builder.AddDocument(root + "\\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        builder.AddDocument(root + "\\d.txt", Encoding.UTF8.GetBytes("unrelated filler content and words"));
        return builder.Build("scope", "vol", root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\sw", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    private static string Norm(string root, string file) => IndexScopeIdentity.NormalizePath(root + "\\" + file);

    private ContentIndexShadowClassifier.ShadowScope EmptyScope(int sessionId = 1) => new(
        sessionId, _sandbox, Array.Empty<string>(),
        new HashSet<int>(), Array.Empty<IReadOnlySet<int>>(),
        new HashSet<long>(), Array.Empty<IReadOnlySet<long>>());

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
    public async Task RunAsync_MissingWorker_IsNotAccelerable_AndNeverThrows()
    {
        using var client = new IndexWorkerClient(workerPathOverride: Path.Combine(_sandbox, "no-such-worker.exe"));
        var classifier = new ContentIndexShadowClassifier(client);
        var scope = new ContentIndexShadowClassifier.ShadowScope(
            1, _sandbox, Array.Empty<string>(),
            new HashSet<int>(), Array.Empty<IReadOnlySet<int>>(),
            new HashSet<long>(), Array.Empty<IReadOnlySet<long>>());

        ContentIndexShadowClassifier.ShadowMetrics metrics =
            await classifier.RunAsync(scope, new[] { @"c:\sw\a.txt" }, oracleVerdict: null, CancellationToken.None);

        Assert.False(metrics.Accelerable);
        Assert.Equal(1, metrics.PathCount);
        Assert.False(string.IsNullOrEmpty(metrics.BypassReason));
    }

    [Fact]
    public void Constructor_RejectsNullClientAndOperations()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentIndexShadowClassifier((IndexWorkerClient)null!));

        Func<IndexQueryOpenRequest, CancellationToken, Task<IndexQueryOpenResult?>> open =
            (_, _) => Task.FromResult<IndexQueryOpenResult?>(null);
        Func<int, IReadOnlyList<string>, CancellationToken, Task<byte[]?>> classify =
            (_, _, _) => Task.FromResult<byte[]?>(null);
        Func<int, CancellationToken, Task> close = (_, _) => Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new ContentIndexShadowClassifier(null!, classify, close));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexShadowClassifier(open, null!, close));
        Assert.Throws<ArgumentNullException>(() => new ContentIndexShadowClassifier(open, classify, null!));
    }

    [Fact]
    public async Task RunAsync_NonAccelerableOpen_PreservesWorkerReason()
    {
        var classifier = new ContentIndexShadowClassifier(
            (_, _) => Task.FromResult<IndexQueryOpenResult?>(new IndexQueryOpenResult
            {
                Accelerable = false,
                BypassReason = "format unavailable",
            }),
            (_, _, _) => throw new InvalidOperationException("classification must not run"),
            (_, _) => throw new InvalidOperationException("close must not run"));

        ContentIndexShadowClassifier.ShadowMetrics metrics = await classifier.RunAsync(
            EmptyScope(), [@"C:\sw\a.txt"], oracleVerdict: null, CancellationToken.None);

        Assert.False(metrics.Accelerable);
        Assert.Equal("format unavailable", metrics.BypassReason);
    }

    [Fact]
    public async Task RunAsync_NullClassification_ClosesScopeAndFallsBack()
    {
        int closedSession = 0;
        var classifier = new ContentIndexShadowClassifier(
            (_, _) => Task.FromResult<IndexQueryOpenResult?>(new IndexQueryOpenResult
            {
                Accelerable = true,
                CandidateCount = 3,
            }),
            (_, _, _) => Task.FromResult<byte[]?>(null),
            (sessionId, _) =>
            {
                closedSession = sessionId;
                return Task.CompletedTask;
            });

        ContentIndexShadowClassifier.ShadowMetrics metrics = await classifier.RunAsync(
            EmptyScope(17), [@"C:\sw\a.txt"], oracleVerdict: null, CancellationToken.None);

        Assert.Equal(17, closedSession);
        Assert.False(metrics.Accelerable);
        Assert.Equal("classify failed", metrics.BypassReason);
    }

    [Fact]
    public async Task RunAsync_SuccessWithoutOracle_DoesNotCompareVerdicts()
    {
        var diagnostics = new IndexQueryOpenDiagnostics();
        var classifier = new ContentIndexShadowClassifier(
            (_, _) => Task.FromResult<IndexQueryOpenResult?>(new IndexQueryOpenResult
            {
                Accelerable = true,
                CandidateCount = 2,
                Diagnostics = diagnostics,
            }),
            (_, _, _) => Task.FromResult<byte[]?>([1]),
            (_, _) => Task.CompletedTask);

        ContentIndexShadowClassifier.ShadowMetrics metrics = await classifier.RunAsync(
            EmptyScope(), [@"C:\sw\a.txt", @"C:\sw\b.txt"], oracleVerdict: null, CancellationToken.None);

        Assert.True(metrics.Accelerable);
        Assert.Equal(0, metrics.MismatchCount);
        Assert.Same(diagnostics, metrics.OpenDiagnostics);
    }

    [Fact]
    public async Task RunAsync_OracleMismatch_CountsOnlyReturnedVerdicts()
    {
        var classifier = new ContentIndexShadowClassifier(
            (_, _) => Task.FromResult<IndexQueryOpenResult?>(new IndexQueryOpenResult
            {
                Accelerable = true,
                CandidateCount = 1,
            }),
            (_, _, _) => Task.FromResult<byte[]?>([2]),
            (_, _) => Task.CompletedTask);

        ContentIndexShadowClassifier.ShadowMetrics metrics = await classifier.RunAsync(
            EmptyScope(), [@"C:\sw\a.txt", @"C:\sw\b.txt"], _ => 1, CancellationToken.None);

        Assert.True(metrics.Accelerable);
        Assert.Equal(1, metrics.MismatchCount);
    }

    [Fact]
    public async Task RunAsync_OrdinaryException_FallsBackButOutOfMemoryPropagates()
    {
        var ordinaryFailure = new ContentIndexShadowClassifier(
            (_, _) => throw new InvalidOperationException("worker broke"),
            (_, _, _) => Task.FromResult<byte[]?>(null),
            (_, _) => Task.CompletedTask);

        ContentIndexShadowClassifier.ShadowMetrics metrics = await ordinaryFailure.RunAsync(
            EmptyScope(), Array.Empty<string>(), oracleVerdict: null, CancellationToken.None);
        Assert.False(metrics.Accelerable);
        Assert.Contains("worker broke", metrics.BypassReason);

        var outOfMemory = new ContentIndexShadowClassifier(
            (_, _) => throw new OutOfMemoryException("pressure"),
            (_, _, _) => Task.FromResult<byte[]?>(null),
            (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<OutOfMemoryException>(() => outOfMemory.RunAsync(
            EmptyScope(), Array.Empty<string>(), oracleVerdict: null, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_RealWorker_MatchesTheInProcessOracle_WithZeroMismatches()
    {
        string? workerExe = FindWorkerExe();
        if (workerExe is null)
            return;

        const string root = @"C:\sw";
        ContentIndexGeneration gen = BuildBaseGeneration(root);
        string baseDir = Path.Combine(_sandbox, "base");
        Directory.CreateDirectory(baseDir);
        ContentIndexV3Format.Write(baseDir, gen);

        TrigramExpression query = PlanQuery("planner");
        IReadOnlySet<int> candidates = gen.Postings.EvaluateSet(query);
        var oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());

        var scope = new ContentIndexShadowClassifier.ShadowScope(
            42, baseDir, Array.Empty<string>(),
            candidates, Array.Empty<IReadOnlySet<int>>(),
            new HashSet<long>(), Array.Empty<IReadOnlySet<long>>());
        var paths = new[] { Norm(root, "a.txt"), Norm(root, "b.txt"), Norm(root, "c.txt"), Norm(root, "d.txt"), Norm(root, "absent.txt") };

        using var client = new IndexWorkerClient(workerPathOverride: workerExe);
        var classifier = new ContentIndexShadowClassifier(client);

        ContentIndexShadowClassifier.ShadowMetrics metrics = await classifier.RunAsync(
            scope, paths, p => IndexQueryWorkerProtocol.VerdictFor(oracle.Classify(p)), CancellationToken.None);

        Assert.True(metrics.Accelerable, metrics.BypassReason);
        Assert.Equal(0, metrics.MismatchCount); // shadow classification matches the in-process oracle exactly
        Assert.Equal(candidates.Count, metrics.CandidateCount);
        Assert.Equal(paths.Length, metrics.PathCount);
    }
}
