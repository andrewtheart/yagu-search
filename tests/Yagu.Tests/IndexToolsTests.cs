using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers the content-index tools and the job surface they share.
/// </summary>
/// <remarks>
/// Status and root listing answer immediately; build, rebuild, compact and validate are modelled as
/// jobs because they run for minutes. The property that matters most is the one <see cref="JobTools"/>
/// enforces: a completed protocol stream only means the child ran and spoke the protocol, so a build
/// refused because another process holds the index writer must not be reported as a finished job.
/// </remarks>
public sealed class IndexToolsTests
{
    private const McpCapability CanIndex = McpCapability.Search | McpCapability.IndexMaintenance;
    private const McpCapability CanDestroy = CanIndex | McpCapability.DestructiveIndexOps;

    private static IndexTools Tool(
        YaguCliBridge bridge, McpAuthorizationPolicy policy, BridgeJobRegistry jobs) =>
        new(bridge, policy, McpToolHarness.Options(policy), jobs);

    private static BridgeJobRegistry Jobs(int maxRunning = 8) => new(maxRetained: 64, maxRunning: maxRunning);

    private static string Value(List<string> arguments, string flag)
    {
        int index = arguments.IndexOf(flag);
        Assert.True(index >= 0, $"'{flag}' was not passed. Arguments: {string.Join(' ', arguments)}");
        return arguments[index + 1];
    }

    /// <summary>Waits for a started job to reach a terminal state, then describes it.</summary>
    private static async Task<string> AwaitJob(BridgeJobRegistry jobs, string startMessage)
    {
        string id = startMessage.Split(' ')[2].TrimEnd(':');
        var jobTools = new JobTools(jobs);

        for (int attempt = 0; attempt < 600; attempt++)
        {
            string status = jobTools.Status(id);
            if (!status.Contains("running", StringComparison.OrdinalIgnoreCase)
                && !status.Contains("queued", StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"job '{id}' never finished. Last status: {jobTools.Status(id)}");
    }

    // ----------------------------------------------------------------- status

    [Fact]
    public async Task ARootOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy, Jobs()).StatusAsync(Path.GetTempPath(), CancellationToken.None);

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task AFullStatusRecord_IsRenderedFieldByField()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Frame(
                "result",
                new
                {
                    root = harness.Root,
                    exists = true,
                    healthLabel = "healthy",
                    documentCount = 12345,
                    segmentCount = 7,
                    builtUtc = "2026-01-02T03:04:05Z",
                    lastIncrementalUpdateUtc = "2026-01-03T04:05:06Z",
                },
                1),
            McpToolHarness.Summary(new { exitCode = 0 }));

        string answer = await Tool(bridge, policy, Jobs()).StatusAsync(harness.Root, CancellationToken.None);

        Assert.EndsWith(harness.Root, Value(arguments, "--index-status"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indexed: yes", answer, StringComparison.Ordinal);
        Assert.Contains("health: healthy", answer, StringComparison.Ordinal);
        Assert.Contains("stored records: 12,345", answer, StringComparison.Ordinal);
        Assert.Contains("segments: 7", answer, StringComparison.Ordinal);
        Assert.Contains("built (UTC): 2026-01-02T03:04:05Z", answer, StringComparison.Ordinal);
        Assert.Contains("last incremental update (UTC): 2026-01-03T04:05:06Z", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnindexedRoot_ReportsItsProblemAndFallsBackToTheRawHealthCode()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame(
                "result",
                new { root = harness.Root, exists = false, health = "missing", problem = "no index has been built" },
                1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy, Jobs()).StatusAsync(harness.Root, CancellationToken.None);

        Assert.Contains("indexed: no", answer, StringComparison.Ordinal);
        Assert.Contains("health: missing", answer, StringComparison.Ordinal);
        Assert.Contains("problem: no index has been built", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARootCoveredByABroaderIndex_SaysAChildIndexWouldBeRedundant()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame(
                "result",
                new
                {
                    root = harness.Root,
                    exists = false,
                    healthLabel = "covered",
                    coveredByBroaderRoot = true,
                    coveringRoot = @"C:\",
                },
                1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy, Jobs()).StatusAsync(harness.Root, CancellationToken.None);

        Assert.Contains(@"covered by: C:\", answer, StringComparison.Ordinal);
        Assert.Contains("a child index would be redundant", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatusRunThatProducedNoResult_IsReportedAsAnError()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting([McpToolHarness.Summary(new { exitCode = 0 })]);

        string answer = await Tool(bridge, policy, Jobs()).StatusAsync(harness.Root, CancellationToken.None);

        Assert.Equal("error: the CLI returned no index status.", answer);
    }

    [Fact]
    public async Task AStatusRunThatCrashed_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(bridge, policy, Jobs()).StatusAsync(harness.Root, CancellationToken.None);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ roots

    [Fact]
    public async Task WithoutIndexMaintenance_TheRootListIsNotEvenEnumerable()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(McpCapability.Search);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy, Jobs()).RootsAsync(CancellationToken.None);

        // Outbound filtering hides roots outside the allowed folders, but without this gate a client
        // with no index rights could still learn which folders inside them are indexed, and how.
        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task RegisteredRoots_AreListedWithTheirFiltersAndMissingMarker()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame(
                "result",
                new { root = harness.Root, exists = true, includeGlobs = "*.cs", excludeGlobs = "bin/*" },
                1),
            McpToolHarness.Frame("result", new { root = harness.Root + "\\gone", exists = false }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy, Jobs()).RootsAsync(CancellationToken.None);

        Assert.Contains("[include=*.cs]", answer, StringComparison.Ordinal);
        Assert.Contains("[exclude=bin/*]", answer, StringComparison.Ordinal);
        Assert.Contains("[missing]", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegisteredRootOutsideTheAllowedRoots_IsNotListed()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { root = @"C:\Users\someone-else\Documents", exists = true }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy, Jobs()).RootsAsync(CancellationToken.None);

        // A registered root the client may not touch is not its business.
        Assert.DoesNotContain("someone-else", answer, StringComparison.Ordinal);
        Assert.Equal("(no indexed folders are registered inside the allowed roots)", answer);
    }

    [Fact]
    public async Task ARootListThatCrashed_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(bridge, policy, Jobs()).RootsAsync(CancellationToken.None);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }

    // -------------------------------------------------------- add and remove

    [Fact]
    public async Task AddingARoot_SaysNothingIsBuiltYet()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { outcome = "success", exitCode = 0 }));

        string answer = await Tool(bridge, policy, Jobs()).AddRootAsync(harness.Root, CancellationToken.None);

        Assert.EndsWith(harness.Root, Value(arguments, "--index-add-root"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing is built yet", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingARootThatFailed_ReportsTheOutcomeRatherThanClaimingSuccess()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("error", new { message = "the registry is locked" }, 1),
            McpToolHarness.Summary(new { outcome = "failed", indexExitCode = 3, exitCode = 3 }),
        ],
        exitCode: 3);

        string answer = await Tool(bridge, policy, Jobs()).AddRootAsync(harness.Root, CancellationToken.None);

        Assert.DoesNotContain("registered", answer, StringComparison.Ordinal);
        Assert.Contains("error: the registry is locked", answer, StringComparison.Ordinal);
        Assert.Contains("outcome: failed (index exit code 3)", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingARoot_NeedsTheDestructiveCapability()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy, Jobs()).RemoveRootAsync(harness.Root, CancellationToken.None);

        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task RemovingARoot_SaysTheStoredDataSurvives()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanDestroy);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { outcome = "success", exitCode = 0 }));

        string answer = await Tool(bridge, policy, Jobs()).RemoveRootAsync(harness.Root, CancellationToken.None);

        Assert.EndsWith(harness.Root, Value(arguments, "--index-remove-root"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read files live", answer, StringComparison.Ordinal);
        Assert.Contains("was not deleted", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMaintenanceRunThatCrashed_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanDestroy);
        YaguCliBridge bridge = harness.Scenario("no-summary");

        Assert.StartsWith(
            "error: ",
            await Tool(bridge, policy, Jobs()).AddRootAsync(harness.Root, CancellationToken.None),
            StringComparison.Ordinal);

        using var second = new McpToolHarness();
        McpAuthorizationPolicy policy2 = second.Policy(CanDestroy);
        Assert.StartsWith(
            "error: ",
            await Tool(second.Scenario("no-summary"), policy2, Jobs())
                .RemoveRootAsync(second.Root, CancellationToken.None),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- the jobs

    [Theory]
    [InlineData(false, "--build-index")]
    [InlineData(true, "--rebuild-index")]
    public async Task ABuild_RunsAsAJobAndUsesTheMatchingFlag(bool rebuild, string expectedFlag)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanDestroy);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { outcome = "success", indexExitCode = 0, exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).BuildStartAsync(harness.Root, rebuild);
        Assert.StartsWith("started job ", start, StringComparison.Ordinal);

        string final = await AwaitJob(jobs, start);

        Assert.Contains(expectedFlag, arguments);
        Assert.Contains("outcome: success", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARebuild_NeedsTheDestructiveCapability()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        // A rebuild throws the stored index away, so it is gated separately from an incremental build.
        string answer = await Tool(bridge, policy, Jobs()).BuildStartAsync(harness.Root, rebuild: true);

        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);

        // The same root is fine incrementally.
        Assert.StartsWith(
            "started job ",
            await Tool(harness.Recording(out _, McpToolHarness.Summary(new { outcome = "success" }))
                , policy, Jobs()).BuildStartAsync(harness.Root, rebuild: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildRefusedByABusyWriter_IsAFailedJobNotACompletedOne()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { message = "another process holds the index writer" }, 1),
            McpToolHarness.Summary(new { outcome = "busy", indexExitCode = 5, exitCode = 5 }),
        ],
        exitCode: 5);

        string start = await Tool(bridge, policy, jobs).BuildStartAsync(harness.Root);
        string final = await AwaitJob(jobs, start);

        // "success" is the only terminal state in which the index actually changed. Without this a job
        // whose maintenance never touched the index is listed as Completed and a caller polling
        // yagu_job_status reads the failure body as a success.
        Assert.DoesNotContain("completed", final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outcome: busy", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidation_RunsAsAJob()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { outcome = "success", exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).ValidateStartAsync(harness.Root);
        await AwaitJob(jobs, start);

        Assert.Contains("--validate-index", arguments);
    }

    [Fact]
    public async Task ACompaction_RunsAsAJob()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { outcome = "success", exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).CompactStartAsync(harness.Root);
        await AwaitJob(jobs, start);

        Assert.Contains("--compact-index", arguments);
    }

    [Fact]
    public async Task EveryJobStartingTool_RefusesARootOutsideTheAllowedRoots()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanDestroy);
        IndexTools tool = Tool(harness.Recording(out List<string> arguments), policy, Jobs());
        string outside = Path.GetTempPath();

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, await tool.BuildStartAsync(outside), StringComparison.Ordinal);
        Assert.Contains(McpDenialCode.OutsideAllowedRoots, await tool.ValidateStartAsync(outside), StringComparison.Ordinal);
        Assert.Contains(McpDenialCode.OutsideAllowedRoots, await tool.CompactStartAsync(outside), StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task TheInFlightLimit_RefusesANewJobWithActionableAdvice()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        BridgeJobRegistry jobs = Jobs(maxRunning: 1);

        // hang never exits, so the first job occupies the only slot for the length of the test.
        YaguCliBridge blocking = harness.Scenario("hang");
        string first = await Tool(blocking, policy, jobs).CompactStartAsync(harness.Root);
        Assert.StartsWith("started job ", first, StringComparison.Ordinal);

        string refused = await Tool(harness.Scenario("hang"), policy, jobs).CompactStartAsync(harness.Root);

        Assert.Contains("1 job(s) are already running", refused, StringComparison.Ordinal);
        Assert.Contains("yagu_job_list", refused, StringComparison.Ordinal);
        Assert.Contains("yagu_job_cancel", refused, StringComparison.Ordinal);

        string id = first.Split(' ')[2].TrimEnd(':');
        Assert.True(jobs.Cancel(id), "the running job should have been cancellable.");
    }

    [Fact]
    public async Task RegisteringARootOutsideTheAllowedRoots_IsRefusedBeforeTheChildRuns()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { outcome = "success", exitCode = 0 }));

        // Enrolling a root is what later grants the indexer permission to read every file beneath it,
        // so the confinement check has to happen here rather than at build time.
        string answer = await Tool(bridge, policy, Jobs())
            .AddRootAsync(Path.GetTempPath(), CancellationToken.None);

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task AFailedMaintenanceRun_CarriesTheChildDiagnosticsThroughToTheCaller()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanIndex);
        YaguCliBridge bridge = harness.Emitting(
            [McpToolHarness.Summary(new { outcome = "failed", indexExitCode = 4, exitCode = 4 })],
            exitCode: 4,
            standardError: "  unable to open index.db: sharing violation  ");

        string answer = await Tool(bridge, policy, Jobs()).AddRootAsync(harness.Root, CancellationToken.None);

        // Index failures are usually environmental, and the actionable part lands on stderr rather
        // than in a protocol record, so dropping it leaves the caller with a bare exit code.
        Assert.Contains("details:", answer, StringComparison.Ordinal);
        Assert.Contains("unable to open index.db: sharing violation", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("  unable", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AMaintenanceRunThatNeverFinished_IsNeverRenderedAsAVerdict()
    {
        // A child killed mid-compaction can still exit 0, so rendering by exit code alone would
        // report an interrupted run as a clean one.
        var crashed = new YaguCliResult(
            YaguCliOutcome.Crashed, ExitCode: 0, Records: [], Failure: null, StandardError: "");
        var timedOut = new YaguCliResult(
            YaguCliOutcome.TimedOut, ExitCode: 0, Records: [],
            Failure: "the index build exceeded its time limit", StandardError: "");

        Assert.Equal("error: crashed", IndexTools.DescribeMaintenanceResult(crashed));
        Assert.Equal(
            "error: the index build exceeded its time limit",
            IndexTools.DescribeMaintenanceResult(timedOut));
        Assert.DoesNotContain("outcome:", IndexTools.DescribeMaintenanceResult(crashed), StringComparison.Ordinal);
    }
}
