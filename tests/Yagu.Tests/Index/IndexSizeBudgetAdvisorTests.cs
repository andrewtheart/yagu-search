using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Covers detection of the "this index stopped updating because it hit its storage budget" state.
/// The condition is dangerous precisely because it looks healthy — the index is structurally fine and
/// searches stay complete — so the detection boundary and the offered remedies are what matter.
/// </summary>
public class IndexSizeBudgetAdvisorTests
{
    private const long Mb = 1024 * 1024;

    private static EffectiveIndexSizePolicy Policy(
        string mode = IndexSizeManagementModes.CoalesceThenCompact,
        int budgetMB = 4096,
        int compactionCapMB = 512)
        => EffectiveIndexSizePolicy.Default with
        {
            Mode = mode,
            SizeBudgetMB = budgetMB,
            MaxAutoCompactionSizeMB = compactionCapMB,
        };

    [Fact]
    public void Diagnose_UnderBudget_IsHealthy()
        => Assert.False(IndexSizeBudgetAdvisor.Diagnose(Policy(), 1000 * Mb).AtBudget);

    [Fact]
    public void Diagnose_WithNoBudget_IsHealthyAtAnySize()
        => Assert.False(IndexSizeBudgetAdvisor.Diagnose(Policy(budgetMB: 0), 33_000 * Mb).AtBudget);

    [Fact]
    public void Diagnose_OverBudgetButStillCompactable_IsHealthy()
    {
        // Compaction can still reclaim here, so maintenance has not stopped and there is nothing to report.
        Assert.False(IndexSizeBudgetAdvisor.Diagnose(Policy(budgetMB: 100, compactionCapMB: 0), 5_000 * Mb).AtBudget);
    }

    [Fact]
    public void Diagnose_OverBudgetAndTooLargeToCompact_ReportsTheStall()
    {
        IndexSizeBudgetDiagnosis d = IndexSizeBudgetAdvisor.Diagnose(Policy(), 33_178 * Mb);

        Assert.True(d.AtBudget);
        Assert.Equal(33_178, d.ActiveMB);
        Assert.Equal(4096, d.BudgetMB);
        Assert.True(d.CompactionBlockedByCap);
    }

    [Fact]
    public void Diagnose_WithCleanupOff_ReportsTheStallWithoutBlamingTheCompactionCap()
    {
        IndexSizeBudgetDiagnosis d = IndexSizeBudgetAdvisor.Diagnose(
            Policy(mode: IndexSizeManagementModes.Off), 33_178 * Mb);

        Assert.True(d.AtBudget);
        Assert.False(d.CompactionBlockedByCap);
        Assert.Contains("turned off", d.ExplainWhyAutomaticCleanupFailed());
    }

    [Fact]
    public void Diagnose_WithCoalescingOnly_ExplainsWhyMergingCannotShrinkIndexedContent()
    {
        IndexSizeBudgetDiagnosis diagnosis = IndexSizeBudgetAdvisor.Diagnose(
            Policy(mode: IndexSizeManagementModes.Coalesce), 33_178 * Mb);

        Assert.True(diagnosis.AtBudget);
        Assert.False(diagnosis.CompactionBlockedByCap);
        string explanation = diagnosis.ExplainWhyAutomaticCleanupFailed();
        Assert.Contains("only removes the overhead", explanation);
        Assert.DoesNotContain("only started automatically below", explanation);
    }

    [Fact]
    public void Explain_TellsTheUserSearchesAreStillComplete()
    {
        string text = IndexSizeBudgetAdvisor.Diagnose(Policy(), 33_178 * Mb).Explain();

        // The single most important reassurance: no match is ever lost by this condition.
        Assert.Contains("no match is missed", text);
        Assert.Contains("no longer being kept up to date", text);
    }

    [Fact]
    public void SuggestedBudget_ClearsTheCurrentSizeWithHeadroom()
    {
        IndexSizeBudgetDiagnosis d = IndexSizeBudgetAdvisor.Diagnose(Policy(), 33_178 * Mb);

        Assert.True(d.SuggestedBudgetMB > d.ActiveMB, "A raised limit must exceed the current size.");
        Assert.Equal(0, d.SuggestedBudgetMB % 1024);
    }

    [Fact]
    public void Remedies_LeadWithRebuild_AndOfferCompactionOnlyWhenTheCapIsWhatBlocks()
    {
        IReadOnlyList<IndexSizeBudgetRemedy> capped = IndexSizeBudgetAdvisor.Diagnose(Policy(), 33_178 * Mb).Remedies();
        Assert.Equal(IndexSizeBudgetRemedy.Rebuild, capped[0]);
        Assert.Contains(IndexSizeBudgetRemedy.AllowCompaction, capped);
        Assert.Contains(IndexSizeBudgetRemedy.Delete, capped);

        IReadOnlyList<IndexSizeBudgetRemedy> off = IndexSizeBudgetAdvisor
            .Diagnose(Policy(mode: IndexSizeManagementModes.Off), 33_178 * Mb).Remedies();
        Assert.DoesNotContain(IndexSizeBudgetRemedy.AllowCompaction, off);
    }

    [Fact]
    public void Remedies_AreEmptyWhenHealthy()
        => Assert.Empty(IndexSizeBudgetDiagnosis.Healthy.Remedies());

    [Fact]
    public void RemedyText_IsPlainLanguageForEveryRemedy()
    {
        IndexSizeBudgetDiagnosis d = IndexSizeBudgetAdvisor.Diagnose(Policy(), 33_178 * Mb);
        foreach (IndexSizeBudgetRemedy remedy in d.Remedies())
        {
            Assert.False(string.IsNullOrWhiteSpace(IndexSizeBudgetAdvisor.RemedyLabel(remedy)));
            Assert.False(string.IsNullOrWhiteSpace(IndexSizeBudgetAdvisor.RemedyDescription(remedy, d)));
        }
    }

    [Fact]
    public void HealthStatus_NamesBothTheLimitAndTheCurrentSize()
    {
        string status = IndexSizeBudgetAdvisor.HealthStatus(IndexSizeBudgetAdvisor.Diagnose(Policy(), 33_178 * Mb));
        Assert.Contains("4,096 MB", status);
        Assert.Contains("33,178 MB", status);
        Assert.Contains("updates paused", status);
    }

    [Fact]
    public void SizeBudgetReached_CountsAsNeedingAttention_AndIsNotReportedHealthy()
    {
        var entry = new IndexRootHealthEntry(
            @"C:\", IndexRootHealthKind.SizeBudgetReached, "updates paused", SizeBudgetRoot: @"C:\");

        Assert.True(entry.NeedsAttention);
        Assert.False(entry.IsHealthy);
        Assert.True(entry.HasStoredIndex);
    }

    // The window layer is not compiled into this assembly, so its wiring is source-pinned.
    private static readonly string MainWindowSource = ReadMainWindowSources();
    private static readonly string IndexSizeBudgetSource = File.ReadAllText(Path.Combine(
        RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.IndexSizeBudget.cs"));

    [Fact]
    public void Detection_RunsBeforeTheFreshnessVerdict_SoAPausedIndexNeverReportsHealthy()
    {
        string refresh = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.IndexStatusRefresh.cs"));
        // Scope to the per-root health method; freshness is also consulted elsewhere in this file.
        int method = refresh.IndexOf("private static IndexRootHealthEntry ReadAllDriveIndexHealth", StringComparison.Ordinal);
        Assert.True(method >= 0, "ReadAllDriveIndexHealth is missing.");
        string body = refresh[method..];

        int budget = body.IndexOf("IndexSizeBudgetAdvisor.Diagnose(", StringComparison.Ordinal);
        int freshness = body.IndexOf("GetScopeFreshnessStatus(", StringComparison.Ordinal);
        Assert.True(budget >= 0, "Per-root health does not check the size budget.");
        Assert.True(freshness >= 0, "Per-root health no longer checks freshness.");
        Assert.True(budget < freshness, "The budget check must precede the freshness verdict.");
        Assert.Contains("IndexRootHealthKind.SizeBudgetReached", refresh);
        Assert.Contains("IndexSizeBudgetDetected", refresh);
    }

    [Fact]
    public void Dialog_ClosesBeforeDispatch_AndReportsLongWorkInTheStatusIndicator()
    {
        Assert.Contains("ShowIndexSizeBudgetDialogAsync", IndexSizeBudgetSource);
        Assert.Contains("diagnosis.Explain()", IndexSizeBudgetSource);
        Assert.Contains("diagnosis.ExplainWhyAutomaticCleanupFailed()", IndexSizeBudgetSource);
        Assert.Contains("dialog?.AcceptClose();", IndexSizeBudgetSource);
        Assert.Contains("ApplyIndexSizeBudgetRemedyAsync", IndexSizeBudgetSource);
        Assert.Contains("ApplyIndexSizeAttentionRemedyAsync", IndexSizeBudgetSource);
        Assert.DoesNotContain("new ProgressBar", IndexSizeBudgetSource);
        Assert.DoesNotContain("status.Text", IndexSizeBudgetSource);
        Assert.DoesNotContain("RebuildCurrentIndexBlockingAsync", IndexSizeBudgetSource);

        Assert.Contains("IndexSizeBudgetRemedy.RaiseBudget", IndexSizeBudgetSource);
        Assert.Contains("IndexSizeBudgetRemedy.AllowCompaction", IndexSizeBudgetSource);
        Assert.Contains("IndexSizeBudgetRemedy.Delete", IndexSizeBudgetSource);
        Assert.Contains("ViewModel.RebuildRegisteredIndexNow(indexRoot);", IndexSizeBudgetSource);
        Assert.Contains("ViewModel.BeginIndexBuildActivity(indexRoot, isIncremental: true);", IndexSizeBudgetSource);
        Assert.Contains("ViewModel.ReportIndexBuildProgress(indexRoot, 2, IndexUpdateStages.CompactAnalyzing);", IndexSizeBudgetSource);
        Assert.Contains("ViewModel.ReportIndexBuildProgress(indexRoot, -1, IndexUpdateStages.Deleting);", IndexSizeBudgetSource);
        Assert.Contains("ViewModel.EndIndexBuildActivity();", IndexSizeBudgetSource);
        Assert.Contains("ShowTitleBar = false", IndexSizeBudgetSource);
    }

    [Fact]
    public void Dialog_PromptsAtMostOncePerRootPerSession_UnlessTheUserAsks()
    {
        Assert.Contains("_indexSizeBudgetPrompted.Add", MainWindowSource);
        Assert.Contains("fromUserAction", MainWindowSource);
    }

    [Fact]
    public void Dialog_SerializesMultipleAffectedRoots_SoTheyCannotStack()
    {
        // Detection is per-root and fire-and-forget: two roots over budget in the same refresh both
        // cleared the "a modal is already open" check and opened two stacked dialogs. Observed live.
        Assert.Contains("_indexSizeBudgetQueue.Enqueue", MainWindowSource);
        Assert.Contains("_indexSizeBudgetPumping", MainWindowSource);
        Assert.Contains("await ShowOneIndexSizeBudgetDialogAsync(_indexSizeBudgetQueue.Dequeue())", MainWindowSource);
    }

    private static string RepoRoot => FindRepoRoot(AppContext.BaseDirectory);

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadMainWindowSources()
    {
        string root = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow");
        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(root, "MainWindow*.cs")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }
}
