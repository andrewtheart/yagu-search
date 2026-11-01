using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>yagu_search_natural_language_start</c>.
/// </summary>
/// <remarks>
/// This is a job because of measured variance rather than mere slowness — the same on-device model
/// produced runs from about nine seconds to roughly sixteen minutes — so the tool must return an id
/// immediately and never block. The single invocation returns both the interpreted plan and the
/// results, so the plan a caller sees is always the one that produced those results.
/// </remarks>
public sealed class NaturalLanguageSearchToolTests
{
    private const McpCapability Searchable = McpCapability.Search;

    private static NaturalLanguageSearchTool Tool(
        YaguCliBridge bridge, McpAuthorizationPolicy policy, BridgeJobRegistry jobs) =>
        new(bridge, policy, McpToolHarness.Options(policy), jobs);

    private static BridgeJobRegistry Jobs(int maxRunning = 8) => new(maxRetained: 64, maxRunning: maxRunning);

    private static string Value(List<string> arguments, string flag)
    {
        int index = arguments.IndexOf(flag);
        Assert.True(index >= 0, $"'{flag}' was not passed. Arguments: {string.Join(' ', arguments)}");
        return arguments[index + 1];
    }

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyRequest_IsRefusedBeforeAnyJobStarts(string request)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();

        string answer = await Tool(harness.Recording(out _), policy, jobs).StartAsync(request, harness.Root);

        Assert.Equal("error: a request is required.", answer);
        Assert.Empty(jobs.All());
    }

    [Fact]
    public async Task AnOmittedRoot_IsRefusedRatherThanSearchingEveryDrive()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();

        string answer = await Tool(harness.Recording(out _), policy, jobs)
            .StartAsync("large pdf reports", root: "");

        Assert.Contains(McpDenialCode.RootRequired, answer, StringComparison.Ordinal);
        Assert.Empty(jobs.All());
    }

    [Fact]
    public async Task ARootOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();

        string answer = await Tool(harness.Recording(out _), policy, jobs)
            .StartAsync("large pdf reports", Path.GetTempPath());

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(jobs.All());
    }

    [Fact]
    public async Task TheStartMessage_WarnsHowVariableTheRunIs()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).StartAsync("large pdf reports since January", harness.Root);
        await AwaitJob(jobs, start);

        Assert.StartsWith("started job ", start, StringComparison.Ordinal);
        Assert.Contains("a few seconds to several minutes", start, StringComparison.Ordinal);
        Assert.Equal("large pdf reports since January", Value(arguments, "--semantic-pattern"));
        Assert.Contains("--confine-to", arguments);
    }

    [Fact]
    public async Task AVeryLongRequest_IsTrimmedInTheJobDescription()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        string request = new string('x', 200);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, McpToolHarness.Summary(new { exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).StartAsync(request, harness.Root);
        await AwaitJob(jobs, start);

        // The description is echoed back in job listings, so an unbounded request would flood them.
        Assert.Contains("...", start, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 61), start, StringComparison.Ordinal);

        // Only the description is trimmed. The model must still receive the whole request.
        Assert.Equal(request, Value(arguments, "--semantic-pattern"));
    }

    [Fact]
    public async Task AShortRequest_IsNotTrimmed()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(out _, McpToolHarness.Summary(new { exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).StartAsync("big pdfs", harness.Root);
        await AwaitJob(jobs, start);

        Assert.Contains("\"big pdfs\"", start, StringComparison.Ordinal);
        Assert.DoesNotContain("...", start, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(50, "50")]
    [InlineData(1, "1")]
    [InlineData(0, "1")]
    [InlineData(-3, "1")]
    public async Task MaxResults_IsFlooredAtOne(int requested, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, McpToolHarness.Summary(new { exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).StartAsync("big pdfs", harness.Root, requested);
        await AwaitJob(jobs, start);

        Assert.Equal(expected, Value(arguments, "--max-results"));
    }

    [Theory]
    [InlineData(false, "--no-online-only")]
    [InlineData(true, "--online-only")]
    public async Task CloudHydration_FollowsTheCapability(bool granted, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(
            granted ? Searchable | McpCapability.CloudHydration : Searchable);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, McpToolHarness.Summary(new { exitCode = 0 }));

        string start = await Tool(bridge, policy, jobs).StartAsync("big pdfs", harness.Root);
        await AwaitJob(jobs, start);

        Assert.Contains(expected, arguments);
    }

    [Fact]
    public async Task TheInterpretedPlan_IsReportedAlongsideTheMatchesItProduced()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        string match = harness.Root + "\\report.pdf";
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame(
                "plan",
                new
                {
                    summary = "PDFs larger than 1 MB changed this year",
                    model = "phi-4-mini",
                    pattern = "report",
                    directory = harness.Root,
                },
                1),
            McpToolHarness.Frame("match", new { path = match, line = 2, text = "quarterly report" }, 2),
            McpToolHarness.Summary(new { returnedRecords = 1, exitCode = 0 }),
        ]);

        string start = await Tool(bridge, policy, jobs).StartAsync("large pdf reports", harness.Root);
        string final = await AwaitJob(jobs, start);

        // Asking for the plan separately would load the model twice and could resample to a different
        // plan, so the plan and the results always travel together.
        Assert.Contains("interpreted as:", final, StringComparison.Ordinal);
        Assert.Contains("PDFs larger than 1 MB changed this year", final, StringComparison.Ordinal);
        Assert.Contains("model: phi-4-mini", final, StringComparison.Ordinal);
        Assert.Contains("pattern: report", final, StringComparison.Ordinal);
        Assert.Contains($"{match}:2: quarterly report", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlanWithOnlyASummary_OmitsTheFieldsItDoesNotHave()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("plan", new { summary = "everything", model = "", pattern = "" }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string start = await Tool(bridge, policy, jobs).StartAsync("everything", harness.Root);
        string final = await AwaitJob(jobs, start);

        Assert.Contains("interpreted as:", final, StringComparison.Ordinal);
        Assert.DoesNotContain("model:", final, StringComparison.Ordinal);
        Assert.DoesNotContain("pattern:", final, StringComparison.Ordinal);
        Assert.DoesNotContain("directory:", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslationWarning_IsReportedRatherThanTreatedAsABridgeFault()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { message = "no on-device model is installed" }, 1),
            McpToolHarness.Summary(new { exitCode = 2 }),
        ],
        exitCode: 2);

        string start = await Tool(bridge, policy, jobs).StartAsync("big pdfs", harness.Root);
        string final = await AwaitJob(jobs, start);

        // No model, no consent, or an unusable plan are legitimate outcomes, not crashes.
        Assert.Contains("warning: no on-device model is installed", final, StringComparison.Ordinal);
        Assert.Contains("(no matches)", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatCrashed_FailsTheJob()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs();

        string start = await Tool(harness.Scenario("no-summary"), policy, jobs)
            .StartAsync("big pdfs", harness.Root);
        string final = await AwaitJob(jobs, start);

        Assert.DoesNotContain("completed", final, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheInFlightLimit_RefusesANewSearch()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        BridgeJobRegistry jobs = Jobs(maxRunning: 1);

        string first = await Tool(harness.Scenario("hang"), policy, jobs).StartAsync("big pdfs", harness.Root);
        Assert.StartsWith("started job ", first, StringComparison.Ordinal);

        string refused = await Tool(harness.Scenario("hang"), policy, jobs).StartAsync("more pdfs", harness.Root);
        Assert.Contains("1 job(s) are already running", refused, StringComparison.Ordinal);

        Assert.True(jobs.Cancel(first.Split(' ')[2].TrimEnd(':')));
    }

    [Fact]
    public void ARunThatNeverFinished_IsNeverRenderedAsAPlan()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);

        // The model can take many minutes, so a timeout is an ordinary outcome here. Rendering it as
        // a plan would hand the caller an empty interpretation as though the model had produced one.
        var timedOut = new YaguCliResult(
            YaguCliOutcome.TimedOut, ExitCode: 0, Records: [],
            Failure: "the model did not answer within the time limit", StandardError: "");
        var crashed = new YaguCliResult(
            YaguCliOutcome.Crashed, ExitCode: 0, Records: [], Failure: null, StandardError: "");

        Assert.Equal(
            "error: the model did not answer within the time limit",
            NaturalLanguageSearchTool.Render(timedOut, policy));
        Assert.Equal("error: crashed", NaturalLanguageSearchTool.Render(crashed, policy));
    }
}
