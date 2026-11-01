using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>yagu_search</c>: how a client's arguments become a CLI command line, and how the CLI's
/// match records become the text a model reads.
/// </summary>
/// <remarks>
/// Two properties here are security properties rather than formatting ones. The root is required,
/// because Yagu's CLI treats an omitted directory as "search every drive"; and every returned path is
/// re-authorized, because the walker follows junctions and a link inside an allowed root produces
/// matches whose real location is outside it.
/// </remarks>
public sealed class SearchToolTests
{
    private const McpCapability Searchable = McpCapability.Search;

    private static SearchTool Tool(YaguCliBridge bridge, McpAuthorizationPolicy policy, int maxExportResults = 50_000) =>
        new(bridge, policy, McpToolHarness.Options(policy, maxExportResults));

    private static string[] OneMatch(string path) =>
    [
        McpToolHarness.Frame("match", new { path, line = 1, text = "hit" }, 1),
        McpToolHarness.Summary(new { returnedRecords = 1, truncated = false, exitCode = 0 }),
    ];

    private static string Value(List<string> arguments, string flag)
    {
        int index = arguments.IndexOf(flag);
        Assert.True(index >= 0, $"'{flag}' was not passed. Arguments: {string.Join(' ', arguments)}");
        return arguments[index + 1];
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyQuery_IsRefusedBeforeAnyChildRuns(string query)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).SearchAsync(query, harness.Root);

        Assert.Equal("error: a query is required.", answer);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task AnOmittedRoot_IsRefusedRatherThanSearchingEveryDrive()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).SearchAsync("needle", root: "");

        Assert.Contains(McpDenialCode.RootRequired, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ARootOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).SearchAsync("needle", Path.GetTempPath());

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    // ------------------------------------------------------- argument building

    [Theory]
    [InlineData("content", "content")]
    [InlineData("CONTENT", "content")]
    [InlineData("  content  ", "content")]
    [InlineData("filenames", "filenames")]
    [InlineData("filename", "filenames")]
    [InlineData("filename-then-content", "filename-then-content")]
    [InlineData("both", "both")]
    // Anything unrecognized falls back to the widest mode rather than failing the call: a model that
    // invents a mode should still get results, not an error it cannot act on.
    [InlineData("nonsense", "both")]
    [InlineData("", "both")]
    public async Task TheSearchMode_IsNormalized(string requested, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root, searchMode: requested);

        Assert.Equal(expected, Value(arguments, "--search-mode"));
    }

    [Theory]
    [InlineData(true, "--regex", "--no-regex")]
    [InlineData(false, "--no-regex", "--regex")]
    public async Task TheRegexFlag_IsAlwaysPassedExplicitly(bool regex, string expected, string forbidden)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root, regex: regex);

        // Both polarities are always sent, so a persisted user setting can never leak into an MCP call.
        Assert.Contains(expected, arguments);
        Assert.DoesNotContain(forbidden, arguments);
    }

    [Fact]
    public async Task EveryBooleanOption_IsSentInBothPolarities()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> on, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync(
            "needle", harness.Root,
            caseSensitive: true, wholeWord: true, multiline: true, includeHidden: true, searchArchives: true);

        Assert.Contains("--case-sensitive", on);
        Assert.Contains("--exact-match", on);
        Assert.Contains("--multiline", on);
        Assert.Contains("--hidden", on);
        Assert.Contains("--search-archives", on);

        using var second = new McpToolHarness();
        McpAuthorizationPolicy policy2 = second.Policy(Searchable);
        YaguCliBridge bridge2 = second.Recording(out List<string> off, OneMatch(second.Root + "\\a.cs"));

        await Tool(bridge2, policy2).SearchAsync(
            "needle", second.Root,
            caseSensitive: false, wholeWord: false, multiline: false, includeHidden: false, searchArchives: false);

        Assert.Contains("--ignore-case", off);
        Assert.Contains("--no-exact-match", off);
        Assert.Contains("--no-multiline", off);
        Assert.Contains("--no-hidden", off);
        Assert.Contains("--no-search-archives", off);
    }

    [Theory]
    [InlineData(false, "--no-online-only", "--online-only")]
    [InlineData(true, "--online-only", "--no-online-only")]
    public async Task CloudHydration_FollowsTheCapabilityNotTheUserSetting(
        bool granted, string expected, string forbidden)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(
            granted ? Searchable | McpCapability.CloudHydration : Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root);

        // Scanning a placeholder downloads it. A client without the capability must never trigger that,
        // whatever Yagu's own persisted setting says.
        Assert.Contains(expected, arguments);
        Assert.DoesNotContain(forbidden, arguments);
    }

    [Theory]
    // The producer's budget bounds the wire, but an unbounded ask still materializes a record per match
    // in this process first, so the ceiling is applied before the child ever starts.
    [InlineData(100, 1000, "100")]
    [InlineData(5000, 1000, "1000")]
    [InlineData(0, 1000, "1")]
    [InlineData(-7, 1000, "1")]
    [InlineData(int.MaxValue, 1000, "1000")]
    public async Task MaxResults_IsClampedToTheConfiguredCeiling(int requested, int ceiling, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy, ceiling).SearchAsync("needle", harness.Root, maxResults: requested);

        Assert.Equal(expected, Value(arguments, "--max-results"));
    }

    [Fact]
    public async Task ContextLines_AreClampedToTheProtocolCeiling()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> high, OneMatch(harness.Root + "\\a.cs"));

        // The scanner sizes a per-file context ring from this value, so int.MaxValue would exhaust the
        // child's memory long before any output budget applied.
        await Tool(bridge, policy).SearchAsync("needle", harness.Root, contextLines: int.MaxValue);
        Assert.Equal(
            MachineOutputBudget.MaxContextLinesCeiling.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value(high, "--context"));

        using var second = new McpToolHarness();
        McpAuthorizationPolicy policy2 = second.Policy(Searchable);
        YaguCliBridge bridge2 = second.Recording(out List<string> low, OneMatch(second.Root + "\\a.cs"));

        await Tool(bridge2, policy2).SearchAsync("needle", second.Root, contextLines: -5);
        Assert.Equal("0", Value(low, "--context"));
    }

    [Fact]
    public async Task MaxMatchesPerFile_IsOmittedUnlessAskedFor()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root, maxMatchesPerFile: null);

        // Omitted means "use the configured default", which is not the same as any number this tool
        // could invent — passing one would silently override the user's setting.
        Assert.DoesNotContain("--max-matches-per-file", arguments);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(0, "0")]
    // Negative is nonsense rather than "unlimited"; it is floored so it cannot reach the scanner.
    [InlineData(-3, "0")]
    public async Task MaxMatchesPerFile_IsPassedWhenAskedFor(int requested, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root, maxMatchesPerFile: requested);

        Assert.Equal(expected, Value(arguments, "--max-matches-per-file"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(3, true)]
    public async Task MaxDepth_IsOnlyPassedWhenItBounds(int depth, bool expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root, maxDepth: depth);

        Assert.Equal(expected, arguments.Contains("--max-depth"));
        if (expected)
            Assert.Equal(depth.ToString(System.Globalization.CultureInfo.InvariantCulture), Value(arguments, "--max-depth"));
    }

    [Fact]
    public async Task Globs_AreSplitOnBothSeparatorsAndTrimmed()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync(
            "needle", harness.Root,
            includeGlobs: " *.cs ; *.md ,, *.txt ",
            excludeGlobs: "*/bin/*;;*/obj/*");

        List<string> includes = [];
        List<string> excludes = [];
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "--glob") includes.Add(arguments[i + 1]);
            if (arguments[i] == "--exclude-glob") excludes.Add(arguments[i + 1]);
        }

        Assert.Equal(["*.cs", "*.md", "*.txt"], includes);
        Assert.Equal(["*/bin/*", "*/obj/*"], excludes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;,,")]
    public async Task BlankGlobs_AddNothing(string? globs)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, OneMatch(harness.Root + "\\a.cs"));

        await Tool(bridge, policy).SearchAsync("needle", harness.Root, includeGlobs: globs, excludeGlobs: globs);

        Assert.DoesNotContain("--glob", arguments);
        Assert.DoesNotContain("--exclude-glob", arguments);
    }

    [Fact]
    public void EveryAllowedRoot_IsPassedAsItsOwnConfinement()
    {
        string a = Path.Combine(Path.GetTempPath(), "yagu-confine-a");
        string b = Path.Combine(Path.GetTempPath(), "yagu-confine-b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        try
        {
            var policy = new McpAuthorizationPolicy([a, b], McpCapability.Search);
            var arguments = new List<string>();

            SearchTool.AddConfinement(arguments, policy);

            Assert.Equal(policy.AllowedRoots.Count * 2, arguments.Count);
            foreach (string root in policy.AllowedRoots)
            {
                int index = arguments.IndexOf(root);
                Assert.True(index > 0, $"'{root}' was not confined. Arguments: {string.Join(' ', arguments)}");
                Assert.Equal("--confine-to", arguments[index - 1]);
            }
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    // ------------------------------------------------------------- rendering

    private static McpAuthorizationPolicy RenderPolicy(string root) => new([root], McpCapability.Search);

    [Fact]
    public void AnErrorRecord_ReplacesTheWholeRendering()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record("match", new { path = harness.Root + "\\a.cs", line = 1, text = "hit" }),
            McpToolHarness.Record("error", new { code = "search-failed", message = "the scan failed" }, 2),
        ]);

        Assert.Equal("error: the scan failed", SearchTool.RenderMatches(result, RenderPolicy(harness.Root)));
    }

    [Fact]
    public void AnErrorRecordWithNoMessage_StillReadsAsAnError()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed(
            [McpToolHarness.Record("error", new { code = "search-failed" })]);

        Assert.Equal("error: the search failed.", SearchTool.RenderMatches(result, RenderPolicy(harness.Root)));
    }

    [Fact]
    public void NoMatchesAtAll_SaysSo()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed([]);

        Assert.Contains("(no matches)", SearchTool.RenderMatches(result, RenderPolicy(harness.Root)), StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchWithALineNumber_IsRenderedAsPathColonLine()
    {
        using var harness = new McpToolHarness();
        string path = harness.Root + "\\a.cs";
        YaguCliResult result = McpToolHarness.Completed(
            [McpToolHarness.Record("match", new { path, line = 42, text = "  var x = 1;  " })]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        // Trailing whitespace is trimmed, leading is kept: indentation is meaningful to a reader.
        Assert.Contains($"{path}:42:   var x = 1;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("var x = 1;  \r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchWithNoLineNumber_OmitsTheColon()
    {
        using var harness = new McpToolHarness();
        string path = harness.Root + "\\a.cs";
        YaguCliResult result = McpToolHarness.Completed(
            [McpToolHarness.Record("match", new { path, line = 0, text = "filename hit" })]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        // A filename match has no line, and "path:0:" would read as line zero rather than no line.
        Assert.Contains($"{path}: filename hit", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":0:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APathOutsideTheAllowedRoots_IsWithheldAndCounted()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record("match", new { path = harness.Root + "\\inside.cs", line = 1, text = "kept" }, 1),
            McpToolHarness.Record("match", new { path = @"C:\Windows\System32\secret.txt", line = 1, text = "leaked" }, 2),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        // A junction inside an allowed root yields matches whose real location is outside it; filtering
        // only the inputs would hand that path straight back to the client.
        Assert.Contains("kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", text, StringComparison.Ordinal);
        Assert.Contains("1 match(es) were withheld", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortenedPath_IsWithheldRatherThanAuthorizedOnAPrefix()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record(
                "match",
                new { path = harness.Root + "\\a", line = 1, text = "hit", pathTruncated = true }),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        // The surviving prefix can name a directory that IS contained while the real path is not, so
        // containment would be decided on a string that never existed on disk. Fail closed.
        Assert.DoesNotContain("hit", text, StringComparison.Ordinal);
        Assert.Contains("1 match(es) were withheld", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngineShortenedLine_IsMarked()
    {
        using var harness = new McpToolHarness();
        string path = harness.Root + "\\a.cs";
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record("match", new { path, line = 1, text = "first 500 chars", engineTruncatedText = true }, 1),
            McpToolHarness.Record(
                "match",
                new { path, line = 2, text = "also cut", truncated = new { text = 9000L } },
                2),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        // Both the engine's own trim and the protocol budget's trim have to be visible, or a consumer
        // cannot tell a complete line from the first 500 characters of one.
        Assert.Equal(2, text.Split("[line truncated]").Length - 1);
    }

    [Fact]
    public void ContextLines_AreNumberedRelativeToTheMatch()
    {
        using var harness = new McpToolHarness();
        string path = harness.Root + "\\a.cs";
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record(
                "match",
                new
                {
                    path,
                    line = 10,
                    text = "the match",
                    contextBefore = new[] { "eight", "nine" },
                    contextAfter = new[] { "eleven", "twelve" },
                }),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        Assert.Contains("8-eight", text, StringComparison.Ordinal);
        Assert.Contains("9-nine", text, StringComparison.Ordinal);
        Assert.Contains("11-eleven", text, StringComparison.Ordinal);
        Assert.Contains("12-twelve", text, StringComparison.Ordinal);

        // Dropping context made contextLines a parameter that silently did nothing, and a model needing
        // the surrounding code had to re-read the file it had just searched.
        Assert.True(
            text.IndexOf("8-eight", StringComparison.Ordinal) < text.IndexOf("the match", StringComparison.Ordinal),
            "context before the match must be rendered before it.");
        Assert.True(
            text.IndexOf("11-eleven", StringComparison.Ordinal) > text.IndexOf("the match", StringComparison.Ordinal),
            "context after the match must be rendered after it.");
    }

    [Fact]
    public void ContextForAMatchWithNoLineNumber_IsRenderedWithoutNumbers()
    {
        using var harness = new McpToolHarness();
        string path = harness.Root + "\\a.cs";
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record(
                "match",
                new { path, line = 0, text = "hit", contextAfter = new[] { "next" } }),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        Assert.Contains("next", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-next", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contextBefore")]
    [InlineData("contextAfter")]
    public void AnEmptyOrNonArrayContext_IsIgnored(string field)
    {
        using var harness = new McpToolHarness();
        string path = harness.Root + "\\a.cs";

        var empty = new Dictionary<string, object> { ["path"] = path, ["line"] = 1, ["text"] = "hit", [field] = Array.Empty<string>() };
        var wrongKind = new Dictionary<string, object> { ["path"] = path, ["line"] = 1, ["text"] = "hit", [field] = "not-an-array" };

        foreach (var data in new[] { empty, wrongKind })
        {
            YaguCliResult result = McpToolHarness.Completed([McpToolHarness.Record("match", data)]);
            string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));
            Assert.Contains("hit", text, StringComparison.Ordinal);
            Assert.DoesNotContain("not-an-array", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSummary_ReportsScanStatisticsAndEveryWayTheAnswerCanBeIncomplete()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record("match", new { path = harness.Root + "\\a.cs", line = 1, text = "hit" }, 1),
            McpToolHarness.Record(
                "summary",
                new
                {
                    filesScanned = 12345,
                    elapsedSeconds = 1.5,
                    truncated = true,
                    engineTruncated = true,
                    cancelled = true,
                },
                2),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        Assert.Contains("1 match(es) returned", text, StringComparison.Ordinal);
        Assert.Contains("12,345 file(s) scanned", text, StringComparison.Ordinal);
        Assert.Contains("1.50s", text, StringComparison.Ordinal);
        Assert.Contains("this page was truncated", text, StringComparison.Ordinal);
        // A separate ceiling from the response budget: without it an engine-shortened result set was
        // reported as a complete answer.
        Assert.Contains("engine's result ceiling", text, StringComparison.Ordinal);
        Assert.Contains("cancelled before completing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompleteSummary_ClaimsNoTruncation()
    {
        using var harness = new McpToolHarness();
        YaguCliResult result = McpToolHarness.Completed(
        [
            McpToolHarness.Record("match", new { path = harness.Root + "\\a.cs", line = 1, text = "hit" }, 1),
            McpToolHarness.Record(
                "summary",
                new { truncated = false, engineTruncated = false, cancelled = false },
                2),
        ]);

        string text = SearchTool.RenderMatches(result, RenderPolicy(harness.Root));

        Assert.Contains("1 match(es) returned.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ end to end

    [Fact]
    public async Task ACompleteRun_RendersTheMatchesTheChildEmitted()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        string path = harness.Root + "\\a.cs";
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("match", new { path, line = 7, text = "needle here" }, 1),
            McpToolHarness.Summary(new { returnedRecords = 1, filesScanned = 3, truncated = false, exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy).SearchAsync("needle", harness.Root);

        Assert.Contains($"{path}:7: needle here", answer, StringComparison.Ordinal);
        Assert.Contains("3 file(s) scanned", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChildThatNeverClosedItsStream_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(Searchable);
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(bridge, policy).SearchAsync("needle", harness.Root);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }
}
