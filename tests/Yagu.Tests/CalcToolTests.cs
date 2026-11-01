using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>yagu_calc</c>, the one tool that touches no path at all.
/// </summary>
/// <remarks>
/// It deliberately bypasses the authorization policy — there is nothing to authorize — which makes it
/// the cheapest end-to-end proof that argument passing, the protocol reader and record selection all
/// work. Its failure paths matter for the same reason: a client must never be told an expression
/// evaluated when the child crashed or answered nothing.
/// </remarks>
public sealed class CalcToolTests
{
    private static CalcTool Tool(McpToolHarness harness, YaguCliBridge bridge) =>
        new(bridge, McpToolHarness.Options(harness.Policy(McpCapability.None)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task AnEmptyExpression_IsRefusedWithoutStartingAChild(string expression)
    {
        using var harness = new McpToolHarness();
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(harness, bridge).CalcAsync(expression, CancellationToken.None);

        Assert.Equal("error: an expression is required.", answer);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task TheExpression_IsPassedThroughAsASingleCalcArgument()
    {
        using var harness = new McpToolHarness();
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Frame("result", new { display = "4" }),
            McpToolHarness.Summary());

        string answer = await Tool(harness, bridge).CalcAsync("2 + 2", CancellationToken.None);

        Assert.Equal("4", answer);

        // One argument, not three: a shell-split expression would evaluate something else entirely.
        int flag = arguments.IndexOf("--calc");
        Assert.True(flag >= 0, $"--calc was not passed. Arguments: {string.Join(' ', arguments)}");
        Assert.Equal("2 + 2", arguments[flag + 1]);
    }

    [Fact]
    public async Task TheFirstResultCarryingADisplay_IsTheAnswer()
    {
        using var harness = new McpToolHarness();
        YaguCliBridge bridge = harness.Emitting(
        [
            // A result with no display must be skipped rather than ending the scan: answering with the
            // empty string here would report a successful evaluation that produced nothing.
            McpToolHarness.Frame("result", new { note = "warming up" }, 1),
            McpToolHarness.Frame("result", new { display = "5 km = 3.10686 miles" }, 2),
            McpToolHarness.Frame("result", new { display = "ignored" }, 3),
            McpToolHarness.Summary(),
        ]);

        string answer = await Tool(harness, bridge).CalcAsync("5 km to miles", CancellationToken.None);

        Assert.Equal("5 km = 3.10686 miles", answer);
    }

    [Fact]
    public async Task AnErrorRecord_IsReportedWithItsMessage()
    {
        using var harness = new McpToolHarness();
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("error", new { code = "invalid-input", message = "cannot parse '1 +'" }),
            McpToolHarness.Summary(),
        ]);

        string answer = await Tool(harness, bridge).CalcAsync("1 +", CancellationToken.None);

        Assert.Equal("error: cannot parse '1 +'", answer);
    }

    [Fact]
    public async Task AnErrorRecordWithNoMessage_StillReadsAsAnError()
    {
        using var harness = new McpToolHarness();
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("error", new { code = "invalid-input" }),
            McpToolHarness.Summary(),
        ]);

        string answer = await Tool(harness, bridge).CalcAsync("???", CancellationToken.None);

        Assert.Equal("error: the expression could not be evaluated.", answer);
    }

    [Fact]
    public async Task ARunThatProducedNoRecords_IsNotReportedAsAnAnswer()
    {
        using var harness = new McpToolHarness();
        YaguCliBridge bridge = harness.Emitting([McpToolHarness.Summary()]);

        string answer = await Tool(harness, bridge).CalcAsync("2+2", CancellationToken.None);

        Assert.Equal("error: the CLI returned no answer.", answer);
    }

    [Fact]
    public async Task AChildThatNeverClosedItsStream_IsReportedAsAFailureNotAnAnswer()
    {
        using var harness = new McpToolHarness();

        // no-summary emits a record and exits 0 without closing the protocol stream. Trusting the exit
        // code here would hand back a partial answer as a complete one.
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(harness, bridge).CalcAsync("2+2", CancellationToken.None);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", answer, StringComparison.OrdinalIgnoreCase);
    }
}
