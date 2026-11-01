using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>yagu_export_report</c>, <c>yagu_save_session</c> and <c>yagu_load_session</c>.
/// </summary>
/// <remarks>
/// These are the only tools that create files, so the capability gate and the write-destination
/// authorization are the load-bearing parts. The reporting is the other half: a search that matched
/// nothing writes no file, and a write can fail outright, so claiming "wrote the report" for a path
/// that does not exist is worse than any error message.
/// </remarks>
public sealed class ExportToolsTests
{
    private const McpCapability CanExport =
        McpCapability.Search | McpCapability.Read | McpCapability.ExportWrite;

    private static ExportTools Tool(YaguCliBridge bridge, McpAuthorizationPolicy policy, int maxExportResults = 50_000) =>
        new(bridge, policy, McpToolHarness.Options(policy, maxExportResults));

    private static string[] Wrote(int matches = 2, int files = 1) =>
    [
        McpToolHarness.Frame("result", new { written = true }, 1),
        McpToolHarness.Summary(new { totalMatches = matches, filesWithMatches = files, elapsedSeconds = 0.25, exitCode = 0 }),
    ];

    private static string Value(List<string> arguments, string flag)
    {
        int index = arguments.IndexOf(flag);
        Assert.True(index >= 0, $"'{flag}' was not passed. Arguments: {string.Join(' ', arguments)}");
        return arguments[index + 1];
    }

    // ----------------------------------------------------------------- gating

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyQuery_IsRefusedBeforeAnyChildRuns(string query)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy)
            .ExportReportAsync(query, harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.Equal("error: a query is required.", answer);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task WithoutTheExportWriteCapability_NothingIsWritten()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(McpCapability.Search | McpCapability.Read);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Contains("writing files is not enabled", answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ASearchRootOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", Path.GetTempPath(), Path.Combine(harness.Root, "out.html"));

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ADestinationOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        // The destination is checked separately from the search root: a contained search that writes its
        // report anywhere on disk is still an arbitrary file write.
        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(Path.GetTempPath(), "escape.html"));

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    // ------------------------------------------------------- argument building

    [Fact]
    public async Task AReportRun_PassesTheCanonicalRootDestinationAndConfinement()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string destination = Path.Combine(harness.Root, "out.html");
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, Wrote());

        await Tool(bridge, policy).ExportReportAsync("needle", harness.Root, destination, regex: true, caseSensitive: true);

        Assert.EndsWith("out.html", Value(arguments, "--export"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(policy.AllowedRoots.Single(), Value(arguments, "--directory"));
        Assert.Equal("needle", Value(arguments, "--pattern"));
        Assert.Contains("--regex", arguments);
        Assert.Contains("--case-sensitive", arguments);
        Assert.Contains("--no-online-only", arguments);
        Assert.Contains("--confine-to", arguments);
    }

    [Fact]
    public async Task ASessionRun_UsesSaveSessionRatherThanExport()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string destination = Path.Combine(harness.Root, "s.yagu-session");
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, Wrote());

        await Tool(bridge, policy).SaveSessionAsync("needle", harness.Root, destination, regex: false, caseSensitive: false);

        Assert.EndsWith("s.yagu-session", Value(arguments, "--save-session"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--export", arguments);
        Assert.Contains("--no-regex", arguments);
        Assert.Contains("--ignore-case", arguments);
    }

    [Theory]
    [InlineData(100, 1000, "100")]
    [InlineData(9999, 1000, "1000")]
    [InlineData(0, 1000, "1")]
    [InlineData(-5, 1000, "1")]
    public async Task TheCollectedCount_IsClampedToTheConfiguredCeiling(int requested, int ceiling, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments, Wrote());

        // Unlike a search, these commands hold every match in memory before writing, so the count a
        // client asks for decides how much memory the child uses.
        await Tool(bridge, policy, ceiling)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"), maxResults: requested);

        Assert.Equal(expected, Value(arguments, "--max-results"));
    }

    [Fact]
    public async Task AskingForMoreThanTheCeiling_WarnsThatTheFileMayBeIncomplete()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(Wrote());

        string answer = await Tool(bridge, policy, maxExportResults: 10)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"), maxResults: 5000);

        // A file that looks complete but is not is worse than one the caller knows is partial.
        Assert.Contains("5,000 matches were requested", answer, StringComparison.Ordinal);
        Assert.Contains("may be incomplete", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingForLessThanTheCeiling_AddsNoNote()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(Wrote());

        string answer = await Tool(bridge, policy, maxExportResults: 10_000)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"), maxResults: 10);

        Assert.DoesNotContain("matches were requested", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YagusOwnResultCeiling_IsReportedSeparatelyFromTheServerCap()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { written = true, maxResultsApplied = 25 }, 1),
            McpToolHarness.Summary(new { totalMatches = 25, exitCode = 0 }),
        ]);

        // The engine's limit is user-configurable and separate from this server's cap, so a request
        // under the server cap can still come back truncated.
        string answer = await Tool(bridge, policy, maxExportResults: 10_000)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"), maxResults: 500);

        Assert.Contains("Yagu's own result ceiling of 25", answer, StringComparison.Ordinal);
        Assert.Contains("Settings ▸ Performance", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineCeilingAboveTheCollectedCount_AddsNoNote()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { written = true, maxResultsApplied = 100_000 }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy, maxExportResults: 10_000)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"), maxResults: 500);

        Assert.DoesNotContain("own result ceiling", answer, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- reporting

    [Fact]
    public async Task AConfirmedWrite_NamesTheFileAndItsStatistics()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string destination = Path.Combine(harness.Root, "out.html");
        YaguCliBridge bridge = harness.Emitting(Wrote(matches: 1234, files: 56));

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, destination, maxResults: 10_000);

        Assert.Contains("wrote the report to", answer, StringComparison.Ordinal);
        Assert.Contains("1,234 match(es)", answer, StringComparison.Ordinal);
        Assert.Contains("in 56 file(s)", answer, StringComparison.Ordinal);
        Assert.Contains("0.25s", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteWithNoConfirmation_IsNotClaimedAsASuccess()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting([McpToolHarness.Summary(new { exitCode = 0 })]);

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.Contains("no write confirmation", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("wrote the report", answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export-empty")]
    [InlineData("export-failed")]
    [InlineData("session-write-failed")]
    public async Task AWriteWarning_ReportsThatNoFileWasWritten(string code)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { code, message = "nothing matched" }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.StartsWith("no report was written: ", answer, StringComparison.Ordinal);
        Assert.Contains("nothing matched", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrelatedWarning_DoesNotSuppressTheWriteReport()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { code = "slow-scan", message = "this took a while" }, 1),
            McpToolHarness.Frame("result", new { written = true }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.Contains("wrote the report to", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorRecord_ReplacesTheWholeReport()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("error", new { code = "export-failed", message = "disk is full" }, 1),
            McpToolHarness.Summary(new { exitCode = 1 }),
        ]);

        string answer = await Tool(bridge, policy)
            .SaveSessionAsync("needle", harness.Root, Path.Combine(harness.Root, "s.yagu-session"));

        Assert.StartsWith("error: disk is full", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorRecordWithNoMessage_NamesTheKindThatFailed()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("error", new { code = "export-failed" }, 1),
            McpToolHarness.Summary(new { exitCode = 1 }),
        ]);

        string answer = await Tool(bridge, policy)
            .SaveSessionAsync("needle", harness.Root, Path.Combine(harness.Root, "s.yagu-session"));

        Assert.Equal("error: the session could not be written.", answer);
    }

    [Fact]
    public async Task AWriteFailureOnStandardError_IsSurfaced()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(
            Wrote(),
            standardError: "failed to open the destination for writing");

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        // Without this the caller reads "wrote the report" for a file that was never created.
        Assert.Contains("failed to open the destination", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrelatedStandardError_IsNotSurfaced()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Emitting(Wrote(), standardError: "warming the cache");

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.DoesNotContain("warming the cache", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChildThatNeverClosedItsStream_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(bridge, policy)
            .ExportReportAsync("needle", harness.Root, Path.Combine(harness.Root, "out.html"));

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }

    // --------------------------------------------------------- load session

    [Fact]
    public async Task ASessionOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy)
            .LoadSessionAsync(Path.Combine(Path.GetTempPath(), "elsewhere.yagu-session"));

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ALoadedSession_RendersItsStoredMatches()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string session = harness.MakeFile("s.yagu-session");
        string match = harness.Root + "\\a.cs";
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Frame("match", new { path = match, line = 4, text = "stored hit" }, 1),
            McpToolHarness.Summary(new { returnedRecords = 1, exitCode = 0 }));

        string answer = await Tool(bridge, policy).LoadSessionAsync(session, maxResults: 7);

        Assert.EndsWith("s.yagu-session", Value(arguments, "--load-session"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("7", Value(arguments, "--max-results"));
        Assert.Contains($"{match}:4: stored hit", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoadedSessionsStoredPaths_AreReauthorizedOnTheWayOut()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string session = harness.MakeFile("s.yagu-session");
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("match", new { path = @"C:\Windows\System32\secret.txt", line = 1, text = "leaked" }, 1),
            McpToolHarness.Summary(new { returnedRecords = 1, exitCode = 0 }),
        ]);

        // A session stored inside an allowed root can still reference matches from anywhere on the
        // machine, and it is an ordinary file a client with write rights could have authored.
        string answer = await Tool(bridge, policy).LoadSessionAsync(session);

        Assert.DoesNotContain("leaked", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", answer, StringComparison.Ordinal);
        Assert.Contains("withheld", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoadThatCrashed_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string session = harness.MakeFile("s.yagu-session");
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(bridge, policy).LoadSessionAsync(session);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingASessionOutsideTheAllowedRoots_IsRefusedBeforeAnythingIsWritten()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanExport);
        string destination = Path.Combine(Path.GetTempPath(), $"escape-{Guid.NewGuid():N}.yagu-session");
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { returnedRecords = 0, exitCode = 0 }));

        // A saved session is a real file written by the child, so the destination has to clear the
        // same confinement check as an exported report rather than only the search root.
        string answer = await Tool(bridge, policy).SaveSessionAsync("needle", harness.Root, destination);

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
        Assert.False(File.Exists(destination), "the refusal must not have written the session file.");
    }
}
