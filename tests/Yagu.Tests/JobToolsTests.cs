using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Cli.MachineOutput;

namespace Yagu.Tests;

/// <summary>
/// Covers the job polling surface shared by every long-running tool.
/// </summary>
/// <remarks>
/// The outcome mapping is the part with teeth. Returning an "error: ..." string from a job's body
/// instead of throwing marks it <c>Completed</c>, so a cancelled or timed-out job reports success with
/// an error body — and cancelling after launch would look different from cancelling while still queued.
/// </remarks>
public sealed class JobToolsTests
{
    private static BridgeJobRegistry Jobs(int maxRunning = 8) => new(maxRetained: 64, maxRunning: maxRunning);

    private static YaguCliResult Result(
        YaguCliOutcome outcome, string? failure = null, params MachineRecord[] records) =>
        new(outcome, 0, records, failure, StandardError: "");

    [Fact]
    public void AnUnknownJobId_SaysWhyItMightBeUnknown()
    {
        string answer = new JobTools(Jobs()).Status("job-that-never-existed");

        Assert.Contains("no job named", answer, StringComparison.Ordinal);
        Assert.Contains("pruned", answer, StringComparison.Ordinal);
        Assert.Contains("restarted", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyRegistry_ListsNothing()
    {
        Assert.Equal("(no jobs)", new JobTools(Jobs()).List());
    }

    [Fact]
    public async Task AFinishedJob_IsListedWithItsStateElapsedTimeAndDescription()
    {
        BridgeJobRegistry jobs = Jobs();
        var tools = new JobTools(jobs);

        BridgeJob? job = jobs.Start("test", "doing the thing", _ => Task.FromResult("the answer"));
        Assert.NotNull(job);

        await WaitForTerminal(tools, job.Id);

        string list = tools.List();
        Assert.Contains(job.Id, list, StringComparison.Ordinal);
        Assert.Contains("doing the thing", list, StringComparison.Ordinal);
        Assert.Contains("s  ", list, StringComparison.Ordinal);
        Assert.Contains("the answer", tools.Status(job.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingAJobThatIsNotRunning_SaysSoRatherThanPretending()
    {
        string answer = new JobTools(Jobs()).Cancel("job-that-never-existed");

        Assert.Contains("is not running", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingARunningJob_IsAcknowledged()
    {
        BridgeJobRegistry jobs = Jobs();
        var tools = new JobTools(jobs);
        var started = new TaskCompletionSource();

        BridgeJob? job = jobs.Start("test", "waiting to be cancelled", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        });
        Assert.NotNull(job);
        await started.Task;

        Assert.Contains($"cancelling job {job.Id}", tools.Cancel(job.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void ACompletedRun_RaisesNothing()
    {
        JobTools.ThrowIfNotCompleted(Result(YaguCliOutcome.Completed));
    }

    [Fact]
    public void ACancelledRun_RaisesACancellationRatherThanAFailure()
    {
        // The registry maps these to different terminal states, so a cancelled job must not be mapped
        // onto the same exception a crash produces.
        var cancelled = Assert.Throws<OperationCanceledException>(
            () => JobTools.ThrowIfNotCompleted(Result(YaguCliOutcome.Cancelled, "the user cancelled it")));

        Assert.Contains("the user cancelled it", cancelled.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancelledRunWithNoFailureText_StillReadsAsACancellation()
    {
        var cancelled = Assert.Throws<OperationCanceledException>(
            () => JobTools.ThrowIfNotCompleted(Result(YaguCliOutcome.Cancelled)));

        Assert.Contains("cancelled", cancelled.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyOtherIncompleteRun_RaisesAFailure(bool timedOut)
    {
        var failed = Assert.Throws<InvalidOperationException>(
            () => JobTools.ThrowIfNotCompleted(
                Result(timedOut ? YaguCliOutcome.TimedOut : YaguCliOutcome.Crashed, "it went wrong")));

        Assert.Equal("it went wrong", failed.Message);
    }

    [Fact]
    public void AnIncompleteRunWithNoFailureText_NamesItsOutcome()
    {
        var failed = Assert.Throws<InvalidOperationException>(
            () => JobTools.ThrowIfNotCompleted(Result(YaguCliOutcome.TimedOut)));

        Assert.Equal("timedout", failed.Message);
    }

    [Fact]
    public void AnErrorRecord_FailsTheJobAndCarriesTheRenderedDescription()
    {
        YaguCliResult result = Result(
            YaguCliOutcome.Completed,
            failure: null,
            McpToolHarness.Record("error", new { message = "the index is corrupt" }));

        var failed = Assert.Throws<InvalidOperationException>(
            () => JobTools.ThrowIfCommandFailed(result, "rendered description"));

        // The rendered result is carried into the failure so no detail is lost to the caller.
        Assert.Equal("rendered description", failed.Message);
    }

    [Fact]
    public void ARunWithNoSummary_IsLeftAlone()
    {
        JobTools.ThrowIfCommandFailed(Result(YaguCliOutcome.Completed), "rendered description");
    }

    [Fact]
    public void ASuccessfulOutcome_IsLeftAlone()
    {
        YaguCliResult result = Result(
            YaguCliOutcome.Completed,
            failure: null,
            McpToolHarness.Record("summary", new { outcome = "success" }));

        JobTools.ThrowIfCommandFailed(result, "rendered description");
    }

    [Fact]
    public void ASummaryWithNoOutcome_IsLeftAlone()
    {
        YaguCliResult result = Result(
            YaguCliOutcome.Completed,
            failure: null,
            McpToolHarness.Record("summary", new { returnedRecords = 0 }));

        JobTools.ThrowIfCommandFailed(result, "rendered description");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("SUCCESS")]
    public void TheSuccessOutcome_IsMatchedWithoutRegardToCase(string outcome)
    {
        YaguCliResult result = Result(
            YaguCliOutcome.Completed, failure: null, McpToolHarness.Record("summary", new { outcome }));

        JobTools.ThrowIfCommandFailed(result, "rendered description");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("CANCELLED")]
    public void ACancelledOutcome_RaisesACancellation(string outcome)
    {
        YaguCliResult result = Result(
            YaguCliOutcome.Completed, failure: null, McpToolHarness.Record("summary", new { outcome }));

        Assert.Throws<OperationCanceledException>(
            () => JobTools.ThrowIfCommandFailed(result, "rendered description"));
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("failed")]
    [InlineData("partial-failure")]
    public void AnyNonSuccessOutcome_FailsTheJob(string outcome)
    {
        YaguCliResult result = Result(
            YaguCliOutcome.Completed, failure: null, McpToolHarness.Record("summary", new { outcome }));

        // A build refused because another process holds the index writer, or aborted on a full disk,
        // speaks the protocol perfectly. Only the outcome distinguishes it from a real build.
        var failed = Assert.Throws<InvalidOperationException>(
            () => JobTools.ThrowIfCommandFailed(result, "rendered description"));

        Assert.Equal("rendered description", failed.Message);
    }

    [Fact]
    public void TheStartRefusal_NamesTheLimitAndBothWaysOut()
    {
        string refusal = JobTools.DescribeStartRefusal(Jobs(maxRunning: 3));

        Assert.Contains("3 job(s) are already running", refusal, StringComparison.Ordinal);
        Assert.Contains("yagu_job_list", refusal, StringComparison.Ordinal);
        Assert.Contains("yagu_job_cancel", refusal, StringComparison.Ordinal);
    }

    private static async Task WaitForTerminal(JobTools tools, string id)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            string status = tools.Status(id);
            if (!status.Contains("running", StringComparison.OrdinalIgnoreCase)
                && !status.Contains("queued", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"job '{id}' never finished. Last status: {tools.Status(id)}");
    }
}
