using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>yagu_read_file</c>: the authorization gate, the line-range it asks the CLI for, and the
/// way a shortened line is reported back.
/// </summary>
/// <remarks>
/// The truncation notices are the part that matters most. The producer bounds each line to a byte
/// ceiling, so without repeating that here a ten-megabyte minified line arrives looking like a short
/// complete one — and a caller writing an edit against it is editing a prefix it believes is the whole
/// file.
/// </remarks>
public sealed class ReadFileToolTests
{
    private const McpCapability ReadOnly = McpCapability.Read;

    private static ReadFileTool Tool(McpToolHarness harness, YaguCliBridge bridge, McpAuthorizationPolicy policy) =>
        new(bridge, policy, McpToolHarness.Options(policy));

    [Fact]
    public async Task APathOutsideTheAllowedRoots_IsRefusedBeforeAnyChildRuns()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(harness, bridge, policy)
            .ReadFileAsync(Path.Combine(Path.GetTempPath(), "not-in-the-root.txt"));

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task WithoutTheReadCapability_ThePathIsRefusedEvenInsideTheRoot()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(McpCapability.Search);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file);

        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Theory]
    // A missing or zero end means "to the end of the file", which is an open-ended range.
    [InlineData(1, 0, "1-")]
    [InlineData(5, 0, "5-")]
    // An end before the start is not an empty range; it is the same open-ended request.
    [InlineData(9, 3, "9-")]
    [InlineData(2, 7, "2-7")]
    // A single line is a closed range, not an open one.
    [InlineData(4, 4, "4-4")]
    // Line numbers are 1-based, so anything below one is raised rather than refused.
    [InlineData(0, 0, "1-")]
    [InlineData(-40, 6, "1-6")]
    public async Task TheRequestedWindow_BecomesTheLinesArgument(int firstLine, int lastLine, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Frame("result", new { line = 1, text = "hello" }),
            McpToolHarness.Summary());

        await Tool(harness, bridge, policy).ReadFileAsync(file, firstLine, lastLine);

        int flag = arguments.IndexOf("--lines");
        Assert.True(flag >= 0, $"--lines was not passed. Arguments: {string.Join(' ', arguments)}");
        Assert.Equal(expected, arguments[flag + 1]);
    }

    [Fact]
    public async Task TheCanonicalPath_AndTheAllowedRoots_ReachTheChild()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("nested\\a.txt");
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Frame("result", new { line = 1, text = "hello" }),
            McpToolHarness.Summary());

        await Tool(harness, bridge, policy).ReadFileAsync(file);

        int read = arguments.IndexOf("--read");
        Assert.True(read >= 0);
        Assert.EndsWith("nested\\a.txt", arguments[read + 1], StringComparison.OrdinalIgnoreCase);

        // The open happens later than the authorization, so containment has to be re-decided by the
        // reading process itself. Without --confine-to, an allowed subdirectory swapped for an
        // outward-pointing junction in that window redirects the read.
        int confine = arguments.IndexOf("--confine-to");
        Assert.True(confine >= 0, $"--confine-to was not passed. Arguments: {string.Join(' ', arguments)}");
        Assert.Equal(policy.AllowedRoots.Single(), arguments[confine + 1]);
    }

    [Fact]
    public async Task EachResultRecord_IsRenderedAsANumberedLine()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { line = 10, text = "first" }, 1),
            McpToolHarness.Frame("result", new { line = 11, text = "second" }, 2),
            McpToolHarness.Summary(new { reachedEndOfFile = true, exitCode = 0 }),
        ]);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file, 10, 11);

        Assert.Contains("10: first", answer, StringComparison.Ordinal);
        Assert.Contains("11: second", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("more lines follow", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("shortened", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortenedLine_IsMarkedAndCounted()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { line = 1, text = "short" }, 1),
            McpToolHarness.Frame(
                "result",
                new { line = 2, text = "start of a huge line", truncated = new { text = 10_485_760L } },
                2),
            McpToolHarness.Summary(new { reachedEndOfFile = true, exitCode = 0 }),
        ]);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file);

        // The byte count is what tells a caller how much is missing, so it has to survive rendering.
        Assert.Contains("[line truncated; 10,485,760 bytes in full]", answer, StringComparison.Ordinal);
        Assert.Contains("(1 line(s) were shortened", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("1: short  [line truncated", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowThatStoppedShortOfTheEnd_SaysMoreLinesFollow()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { line = 1, text = "only line shown" }, 1),
            McpToolHarness.Summary(new { reachedEndOfFile = false, exitCode = 0 }),
        ]);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file, 1, 1);

        Assert.Contains("more lines follow", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyRange_SaysSoRatherThanReturningNothing()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Emitting([McpToolHarness.Summary(new { reachedEndOfFile = true })]);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file, 900, 999);

        Assert.Equal("(no lines in the requested range)", answer);
    }

    [Fact]
    public async Task AnErrorRecord_ReplacesTheWholeRendering()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { line = 1, text = "a line the caller must not keep" }, 1),
            McpToolHarness.Frame("error", new { code = "read-failed", message = "the file is locked" }, 2),
            McpToolHarness.Summary(),
        ]);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file);

        Assert.Equal("error: the file is locked", answer);
    }

    [Fact]
    public async Task AnErrorRecordWithNoMessage_StillReadsAsAnError()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("error", new { code = "read-failed" }, 1),
            McpToolHarness.Summary(),
        ]);

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file);

        Assert.Equal("error: the file could not be read.", answer);
    }

    [Fact]
    public async Task AChildThatNeverClosedItsStream_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(ReadOnly);
        string file = harness.MakeFile("a.txt");
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(harness, bridge, policy).ReadFileAsync(file);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }
}
