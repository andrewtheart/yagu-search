using System.Text;
using Yagu.Models;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ContentIndexReadinessCheckerTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexReadinessCheckerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-readiness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static SearchOptions Options(string root, string query = "distinctive") => new()
    {
        Directory = root,
        Query = query,
        CaseSensitive = true,
        ExactMatch = false,
        UseContentIndex = true,
    };

    private void Publish()
    {
        var policy = new IndexIngestionPolicy(0, null, null, includeHiddenFiles: true, followReparsePoints: false, maxDepth: 0);
        var builder = new ContentIndexGenerationBuilder(policy, identityProvider: IndexTestIdentities.Provider);
        builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("a distinctive indexed document"));
        ContentIndexGeneration generation = builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        new ContentIndexStore(_paths, _scopeId, retainedGenerations: 2).Publish(generation);
    }

    [Fact]
    public void CheckRoot_EligibleQueryWithoutIndex_ReportsMissingAndAddable()
    {
        ContentIndexReadinessIssue? issue = ContentIndexReadinessChecker.CheckRoot(
            _paths, _root, Array.Empty<string>(), Options(_root), 2,
            (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>()));

        Assert.NotNull(issue);
        Assert.Equal(ContentIndexReadinessIssueKind.Missing, issue!.Kind);
        Assert.False(issue.Registered);
        Assert.True(issue.CanAdd);
        Assert.False(issue.CanRebuild);
        Assert.Contains("Missing", issue.WarningKey);
    }

    [Fact]
    public void CheckRoot_IneligibleQueryWithoutIndex_DoesNotRecommendARebuild()
    {
        ContentIndexReadinessIssue? issue = ContentIndexReadinessChecker.CheckRoot(
            _paths, _root, Array.Empty<string>(), Options(_root, "x"), 2,
            (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>()));

        Assert.Null(issue);
    }

    [Fact]
    public void CheckRoot_IncompleteJournal_ReportsRegisteredRefreshAction()
    {
        Publish();

        ContentIndexReadinessIssue? issue = ContentIndexReadinessChecker.CheckRoot(
            _paths, _root, [_root], Options(_root), 2,
            (_, since) => new UsnReadResult(UsnReadStatus.Incomplete, since, Array.Empty<UsnChange>()));

        Assert.NotNull(issue);
        Assert.Equal(ContentIndexReadinessIssueKind.RefreshRequired, issue!.Kind);
        Assert.True(issue.Registered);
        Assert.True(issue.CanRebuild);
        Assert.False(issue.CanAdd);
        Assert.Contains("Incomplete", issue.Reason);
    }

    [Fact]
    public void CheckRoot_ContinuousJournalAndUsableIndex_IsReady()
    {
        Publish();

        ContentIndexReadinessIssue? issue = ContentIndexReadinessChecker.CheckRoot(
            _paths, _root, [_root], Options(_root), 2,
            (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>()));

        Assert.Null(issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("query has no required trigram")]
    [InlineData("query is not selective enough")]
    [InlineData("")]
    public void ClassifyReason_QueryShapeOrUnknown_IsNotActionable(string? reason)
        => Assert.Null(ContentIndexReadinessChecker.ClassifyReason(reason));

    [Theory]
    [InlineData("layer freshness inputs unreadable")]
    [InlineData("layer not fresh: JournalUnavailable (Unavailable)")]
    [InlineData("layer not fresh: CheckpointInvalid (Ok)")]
    [InlineData("layer not fresh: JournalDiscontinuity (UnknownRecordVersion)")]
    [InlineData("layer not fresh: JournalDiscontinuity (Error)")]
    [InlineData("JournalDiscontinuity")]
    [InlineData("CheckpointInvalid")]
    [InlineData("JournalUnavailable")]
    public void ClassifyReason_EveryFreshnessFailure_IsActionable(string reason)
        => Assert.Equal(ContentIndexReadinessIssueKind.RefreshRequired,
            ContentIndexReadinessChecker.ClassifyReason(reason));

    [Fact]
    public void ResolveRepairability_OnlyProbesCheckpointRefreshFailures()
    {
        bool called = false;
        Func<bool> unexpectedProbe = () => { called = true; return false; };

        Assert.Equal(("CheckpointInvalid", true), ContentIndexReadinessChecker.ResolveRepairability(
            ContentIndexReadinessIssueKind.Missing, "CheckpointInvalid", unexpectedProbe));
        Assert.Equal(("other", true), ContentIndexReadinessChecker.ResolveRepairability(
            ContentIndexReadinessIssueKind.RefreshRequired, "other", unexpectedProbe));
        Assert.False(called);

        Assert.Equal(("CheckpointInvalid", true), ContentIndexReadinessChecker.ResolveRepairability(
            ContentIndexReadinessIssueKind.RefreshRequired, "CheckpointInvalid", () => true));
        (string reason, bool repairable) = ContentIndexReadinessChecker.ResolveRepairability(
            ContentIndexReadinessIssueKind.RefreshRequired, "CheckpointInvalid", () => false);
        Assert.False(repairable);
        Assert.Contains("UnsupportedChangeJournal", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRoot_DiagnosticFailure_FailsOpenExceptForOutOfMemory()
    {
        SearchOptions options = Options(_root);
        ContentIndexFreshnessEvaluator.JournalReader reader =
            (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, Array.Empty<UsnChange>());

        Assert.Null(ContentIndexReadinessChecker.CheckRoot(
            new ThrowingPaths(new InvalidOperationException("storage unavailable")),
            _root, Array.Empty<string>(), options, 2, reader));
        Assert.Throws<OutOfMemoryException>(() => ContentIndexReadinessChecker.CheckRoot(
            new ThrowingPaths(new OutOfMemoryException("allocation failed")),
            _root, Array.Empty<string>(), options, 2, reader));
    }

    private sealed class ThrowingPaths(Exception error) : IContentIndexPathProvider
    {
        public string IndexRoot => throw error;
        public string GetScopeDirectory(string scopeId) => throw error;
    }
}
