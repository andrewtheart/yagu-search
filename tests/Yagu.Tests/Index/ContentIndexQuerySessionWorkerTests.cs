using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests the out-of-process worker query seam (plan §3.3): <see cref="ContentIndexQuerySession.BeginWithCandidates"/>
/// and <see cref="ContentIndexAccelerator.TryBegin"/>'s opt-in to an <see cref="IIndexCandidateSource"/>. A fake
/// source lets these run deterministically without launching the worker process; the real end-to-end launch is
/// covered by <c>IndexWorkerQuerySourceTests</c>.
/// </summary>
public sealed class ContentIndexQuerySessionWorkerTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;

    public ContentIndexQuerySessionWorkerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-worker-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration PublishGeneration()
    {
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest"));
        var gen = builder.Build(scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, scopeId).Publish(gen);
        return gen;
    }

    private static AppSettings EnabledSettings(bool useWorker) => new()
    {
        EnableContentIndex = true,
        IndexMaxCandidatePercent = 100,
        IndexUseNativeWorker = useWorker,
    };

    private SearchOptions LiteralQuery() => new()
    {
        Directory = _root,
        Query = "planner",
        CaseSensitive = true,
        ExactMatch = false,
        UseContentIndex = true,
    };

    private static ContentIndexFreshnessEvaluator.JournalReader OkReader()
        => (path, since) => new UsnReadResult(UsnReadStatus.Ok, new UsnCheckpoint(since.JournalId, since.NextUsn + 10), Array.Empty<UsnChange>());

    private static string Norm(string path) => IndexScopeIdentity.NormalizePath(path);

    // ── BeginWithCandidates (the injection seam) ──

    [Fact]
    public void BeginWithCandidates_UsesTheSuppliedSet_NotTheInProcessEvaluation()
    {
        var gen = PublishGeneration();
        var dirty = new DirtyContentSet();

        // The in-process evaluation of "planner" selects a.txt (content id 0). Supply a DIFFERENT set
        // (empty) and prove the session honors it: a.txt now classifies as a nonmember.
        var session = ContentIndexQuerySession.BeginWithCandidates(gen, new HashSet<int>(), dirty);

        Assert.Equal(0, session.CandidateCount);
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(session.Classify(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void BeginWithCandidates_MemberSet_ClassifiesMember()
    {
        var gen = PublishGeneration();
        var session = ContentIndexQuerySession.BeginWithCandidates(gen, new HashSet<int> { 0 }, new DirtyContentSet());
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(session.Classify(Norm(@"C:\r\a.txt")));
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(session.Classify(Norm(@"C:\r\b.txt")));
    }

    [Fact]
    public void BeginWithCandidates_NullArguments_Throw()
    {
        var gen = PublishGeneration();
        Assert.Throws<ArgumentNullException>(() => ContentIndexQuerySession.BeginWithCandidates(null!, new HashSet<int>(), new DirtyContentSet()));
        Assert.Throws<ArgumentNullException>(() => ContentIndexQuerySession.BeginWithCandidates(gen, null!, new DirtyContentSet()));
        Assert.Throws<ArgumentNullException>(() => ContentIndexQuerySession.BeginWithCandidates(gen, new HashSet<int>(), null!));
    }

    // ── Accelerator opt-in ──

    private sealed class FakeCandidateSource : IIndexCandidateSource
    {
        private readonly IReadOnlySet<int>? _result;
        public int Calls { get; private set; }
        public string? LastGenerationDir { get; private set; }

        public FakeCandidateSource(IReadOnlySet<int>? result) => _result = result;

        public bool TryEvaluate(string generationDir, TrigramExpression query, out IReadOnlySet<int> candidateContentIds)
        {
            Calls++;
            LastGenerationDir = generationDir;
            if (_result is null)
            {
                candidateContentIds = new HashSet<int>();
                return false;
            }
            candidateContentIds = _result;
            return true;
        }
    }

    [Fact]
    public void TryBegin_WorkerFlagOn_UsesCandidateSource()
    {
        PublishGeneration();
        var source = new FakeCandidateSource(new HashSet<int>()); // worker says "no candidates"
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings(useWorker: true), source);

        Assert.True(result.CanAccelerate);
        Assert.Equal(1, source.Calls);
        Assert.False(string.IsNullOrEmpty(source.LastGenerationDir));
        // The worker's (empty) set was used, so a.txt — which the in-process path would call a member — is a nonmember.
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(result.Query!.Classify(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void TryBegin_WorkerFlagOn_SourceFails_FallsBackInProcess()
    {
        PublishGeneration();
        var source = new FakeCandidateSource(null); // worker unavailable → TryEvaluate returns false
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings(useWorker: true), source);

        Assert.True(result.CanAccelerate);
        Assert.Equal(1, source.Calls);
        // Fallback to in-process evaluation: a.txt IS a member of "planner".
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(result.Query!.Classify(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void TryBegin_WorkerFlagOff_NeverConsultsSource()
    {
        PublishGeneration();
        var source = new FakeCandidateSource(new HashSet<int>());
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings(useWorker: false), source);

        Assert.True(result.CanAccelerate);
        Assert.Equal(0, source.Calls); // flag off → in-process evaluation only
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(result.Query!.Classify(Norm(@"C:\r\a.txt")));
    }

    [Fact]
    public void TryBegin_WorkerFlagOn_NoSource_UsesInProcess()
    {
        PublishGeneration();
        var accel = new ContentIndexAccelerator(_paths, journalReader: OkReader());

        var result = accel.TryBegin(_root, LiteralQuery(), EnabledSettings(useWorker: true), candidateSource: null);

        Assert.True(result.CanAccelerate);
        Assert.IsType<IndexPathClassification.FreshIndexedMember>(result.Query!.Classify(Norm(@"C:\r\a.txt")));
    }
}
