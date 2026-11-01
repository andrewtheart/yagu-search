using Yagu.McpServer;
using Yagu.McpServer.Tools;
using Yagu.Services.Mcp;

namespace Yagu.Tests;

/// <summary>
/// Covers <c>yagu_replace_preview</c> and <c>yagu_replace_commit</c>.
/// </summary>
/// <remarks>
/// The preview and the commit are deliberately separate calls, and the commit takes a short-lived id
/// bound to what the preview saw rather than a <c>confirm</c> flag the same model would set. The tests
/// that matter here are the ones proving the preview never writes, that a commit is re-authorized
/// against the roots as they stand now, and that a rolled-back batch is not replayed to the caller as a
/// list of successful writes.
/// </remarks>
public sealed class ReplacePreviewToolTests
{
    private const McpCapability CanReplace = McpCapability.Search | McpCapability.Replace;

    private static ReplacePreviewTool Tool(YaguCliBridge bridge, McpAuthorizationPolicy policy) =>
        new(bridge, policy, McpToolHarness.Options(policy));

    private static string Value(List<string> arguments, string flag)
    {
        int index = arguments.IndexOf(flag);
        Assert.True(index >= 0, $"'{flag}' was not passed. Arguments: {string.Join(' ', arguments)}");
        return arguments[index + 1];
    }

    /// <summary>A unique id per test: the commit scope map is static and shared across the class.</summary>
    private static string NewCommitId() => "commit-" + Guid.NewGuid().ToString("n");

    // ---------------------------------------------------------------- preview

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyQuery_IsRefusedBeforeAnyChildRuns(string query)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).PreviewAsync(query, "new", harness.Root);

        Assert.Equal("error: a query is required.", answer);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task WithoutTheReplaceCapability_EvenThePreviewIsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(McpCapability.Search);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        // Seeing the proposed rewrite of a file is itself a disclosure, so the preview needs the same
        // capability as the commit even though it writes nothing.
        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ARootOutsideTheAllowedRoots_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", Path.GetTempPath());

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ThePreview_AlwaysAsksForADryRun()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        // Not negotiable from the tool surface: v1 never writes.
        Assert.Contains("--dry-run", arguments);
        Assert.Equal("old", Value(arguments, "--pattern"));
        Assert.Equal("new", Value(arguments, "--replace"));
        Assert.Contains("--confine-to", arguments);
    }

    [Fact]
    public async Task ADryRunStillScans_SoHydrationStillFollowsTheCapability()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        // Scanning a cloud placeholder downloads it, so "this tool never writes" does not make hydration
        // free for a client that was not granted it.
        Assert.Contains("--no-online-only", arguments);

        using var granted = new McpToolHarness();
        McpAuthorizationPolicy policy2 = granted.Policy(CanReplace | McpCapability.CloudHydration);
        YaguCliBridge bridge2 = granted.Recording(
            out List<string> withHydration,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge2, policy2).PreviewAsync("old", "new", granted.Root);
        Assert.Contains("--online-only", withHydration);
    }

    [Theory]
    [InlineData(50, "50")]
    [InlineData(1, "1")]
    [InlineData(0, "1")]
    [InlineData(-9, "1")]
    public async Task MaxFiles_IsFlooredAtOne(int requested, string expected)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root, maxFiles: requested);

        Assert.Equal(expected, Value(arguments, "--max-results"));
    }

    [Fact]
    public async Task Globs_AreSplitOnBothSeparatorsAndTrimmed()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge, policy).PreviewAsync(
            "old", "new", harness.Root, includeGlobs: " *.cs , *.md ", excludeGlobs: "bin/*;obj/*");

        List<string> includes = [];
        List<string> excludes = [];
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "--glob") includes.Add(arguments[i + 1]);
            if (arguments[i] == "--exclude-glob") excludes.Add(arguments[i + 1]);
        }

        Assert.Equal(["*.cs", "*.md"], includes);
        // Without excludes there was no way to keep a replacement out of build output such as bin/ and
        // obj/, which is exactly the sweep a caller most wants to narrow before committing it.
        Assert.Equal(["bin/*", "obj/*"], excludes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BlankGlobs_AddNothing(string? globs)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root, includeGlobs: globs, excludeGlobs: globs);

        Assert.DoesNotContain("--glob", arguments);
        Assert.DoesNotContain("--exclude-glob", arguments);
    }

    [Theory]
    [InlineData(true, "--regex", "--case-sensitive", "--exact-match")]
    [InlineData(false, "--no-regex", "--ignore-case", "--no-exact-match")]
    public async Task TheMatchingFlags_AreAlwaysPassedExplicitly(
        bool on, string regexFlag, string caseFlag, string wordFlag)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Summary(new { exitCode = 0 }));

        await Tool(bridge, policy).PreviewAsync(
            "old", "new", harness.Root, regex: on, caseSensitive: on, wholeWord: on);

        Assert.Contains(regexFlag, arguments);
        Assert.Contains(caseFlag, arguments);
        Assert.Contains(wordFlag, arguments);
    }

    [Fact]
    public async Task APreviewWithNoChanges_SaysSoAndStillLeadsWithThePreviewBanner()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Emitting([McpToolHarness.Summary(new { exitCode = 0 })]);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        Assert.StartsWith("PREVIEW ONLY — no file was modified.", answer, StringComparison.Ordinal);
        Assert.Contains("(nothing would change)", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachChangedFile_IsRenderedWithItsBeforeAndAfterLines()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string path = harness.Root + "\\a.cs";
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { message = "one file was skipped" }, 1),
            McpToolHarness.Frame(
                "result",
                new
                {
                    path,
                    replacements = 3,
                    changes = new[] { new { line = 12, before = "old text", after = "new text" } },
                    changesTruncated = true,
                },
                2),
            McpToolHarness.Frame("result", new { commitId = NewCommitId() }, 3),
            McpToolHarness.Summary(
                new
                {
                    exitCode = 0,
                    replace = new
                    {
                        filesAttempted = 4,
                        filesChanged = 1,
                        filesUnchanged = 2,
                        archivesSkipped = 1,
                        filesFailed = 0,
                        totalReplacements = 3,
                    },
                }),
        ]);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        Assert.Contains("warning: one file was skipped", answer, StringComparison.Ordinal);
        Assert.Contains($"{path}  (3 replacement(s))", answer, StringComparison.Ordinal);
        Assert.Contains("- old text", answer, StringComparison.Ordinal);
        Assert.Contains("+ new text", answer, StringComparison.Ordinal);
        Assert.Contains("more changes in this file are not shown", answer, StringComparison.Ordinal);
        Assert.Contains("attempted 4, would change 1", answer, StringComparison.Ordinal);
        Assert.Contains("unchanged 2, skipped archives 1", answer, StringComparison.Ordinal);
        Assert.Contains("failed 0; 3 replacement(s) total", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASummaryMissingItsReplaceCounters_ReadsAsZeroRatherThanThrowing()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path = harness.Root + "\\a.cs" }, 1),
            McpToolHarness.Summary(new { exitCode = 0, replace = new { filesAttempted = "not-a-number" } }),
        ]);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        Assert.Contains("attempted 0, would change 0", answer, StringComparison.Ordinal);
        Assert.Contains("(0 replacement(s))", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreviewedPathOutsideTheAllowedRoots_IsWithheld()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path = @"C:\Windows\System32\secret.txt", replacements = 1 }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        Assert.DoesNotContain("secret.txt", answer, StringComparison.Ordinal);
        Assert.Contains("(nothing would change)", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommitId_IsOfferedWithItsExpiryAndSingleUseWarning()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string commitId = NewCommitId();
        YaguCliBridge bridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path = harness.Root + "\\a.cs", replacements = 1 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        Assert.Contains($"commitId \"{commitId}\"", answer, StringComparison.Ordinal);
        Assert.Contains("single-use", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreviewThatCrashed_IsReportedAsAFailure()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Scenario("no-summary");

        string answer = await Tool(bridge, policy).PreviewAsync("old", "new", harness.Root);

        Assert.StartsWith("error: ", answer, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- commit

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyCommitId_IsRefused(string commitId)
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        Assert.Equal("error: a commitId is required.", await Tool(bridge, policy).CommitAsync(commitId));
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task WithoutTheReplaceCapability_ACommitIsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(McpCapability.Search);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).CommitAsync(NewCommitId());

        Assert.Contains(McpDenialCode.CapabilityDenied, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ACommitIdThisServerNeverIssued_IsRefused()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        YaguCliBridge bridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(bridge, policy).CommitAsync(NewCommitId());

        Assert.Contains("was not issued by this server", answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task APreviewedFileNowOutsideTheAllowedRoots_BlocksTheCommit()
    {
        using var harness = new McpToolHarness();
        string commitId = NewCommitId();
        string outside = Path.Combine(Path.GetTempPath(), "moved-away.cs");

        // Issue the scope under a policy that allowed the file, then commit under one that does not.
        // A preview does not buy a permanent licence to write to those paths.
        var issuing = new McpAuthorizationPolicy([Path.GetTempPath()], CanReplace);
        YaguCliBridge previewBridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path = outside, replacements = 1 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(previewBridge, issuing).PreviewAsync("old", "new", Path.GetTempPath());

        McpAuthorizationPolicy narrowed = harness.Policy(CanReplace);
        YaguCliBridge commitBridge = harness.Recording(out List<string> arguments);

        string answer = await Tool(commitBridge, narrowed).CommitAsync(commitId);

        Assert.Contains(McpDenialCode.OutsideAllowedRoots, answer, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task AnAcceptedCommit_ReportsEachWrittenFileAndItsBackup()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string commitId = NewCommitId();
        string path = harness.MakeFile("a.cs", "old");

        YaguCliBridge previewBridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path, replacements = 2 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(previewBridge, policy).PreviewAsync("old", "new", harness.Root);

        YaguCliBridge commitBridge = harness.Recording(
            out List<string> arguments,
            McpToolHarness.Frame("result", new { path, committed = true, replacements = 2 }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }));

        string answer = await Tool(commitBridge, policy).CommitAsync(commitId);

        Assert.Equal(commitId, Value(arguments, "--replace-commit"));
        Assert.Contains("--confine-to", arguments);
        Assert.Contains($"{path}  (2 replacement(s) written)", answer, StringComparison.Ordinal);
        Assert.Contains("1 file(s) written", answer, StringComparison.Ordinal);
        Assert.Contains(".yagubak", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommittedIdIsSpent_AndCannotBeReplayed()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string commitId = NewCommitId();
        string path = harness.MakeFile("a.cs", "old");

        YaguCliBridge previewBridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path, replacements = 1 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(previewBridge, policy).PreviewAsync("old", "new", harness.Root);

        YaguCliBridge first = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path, committed = true, replacements = 1 }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(first, policy).CommitAsync(commitId);

        YaguCliBridge second = harness.Recording(out List<string> arguments);
        string replay = await Tool(second, policy).CommitAsync(commitId);

        Assert.Contains("was not issued by this server", replay, StringComparison.Ordinal);
        Assert.Empty(arguments);
    }

    [Fact]
    public async Task ARolledBackBatch_DoesNotReplayItsWritesAsSuccesses()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string commitId = NewCommitId();
        string path = harness.MakeFile("a.cs", "old");

        YaguCliBridge previewBridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path, replacements = 1 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(previewBridge, policy).PreviewAsync("old", "new", harness.Root);

        YaguCliBridge commitBridge = harness.Emitting(
        [
            // A per-file record says "this file was written"; it is emitted as the batch progresses and
            // cannot know a later file will fail.
            McpToolHarness.Frame("result", new { path, committed = true, replacements = 1 }, 1),
            McpToolHarness.Frame(
                "error",
                new { code = "commit-rolled-back", message = "a later file failed, so every write was undone." },
                2),
            McpToolHarness.Summary(new { exitCode = 1 }),
        ]);

        string answer = await Tool(commitBridge, policy).CommitAsync(commitId);

        // Replaying them points the caller at .yagubak files that no longer exist and at edits that are
        // no longer on disk.
        Assert.Contains("NOTHING it reported above was left on disk", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("file(s) written. A .yagubak", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusalsAndOrdinaryErrors_AreBothReported()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string commitId = NewCommitId();
        string path = harness.MakeFile("a.cs", "old");

        YaguCliBridge previewBridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path, replacements = 1 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(previewBridge, policy).PreviewAsync("old", "new", harness.Root);

        YaguCliBridge commitBridge = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { message = "a.cs changed since the preview" }, 1),
            McpToolHarness.Frame("error", new { code = "commit-failed", message = "one file is read-only" }, 2),
            McpToolHarness.Summary(new { exitCode = 1 }),
        ]);

        string answer = await Tool(commitBridge, policy).CommitAsync(commitId);

        Assert.Contains("refused/failed: a.cs changed since the preview", answer, StringComparison.Ordinal);
        Assert.Contains("error: one file is read-only", answer, StringComparison.Ordinal);
        Assert.Contains("0 file(s) written", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptedCommit_KeepsItsIdAndPointsAtTheRecoveryReport()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string commitId = NewCommitId();
        string path = harness.MakeFile("a.cs", "old");

        YaguCliBridge previewBridge = harness.Emitting(
        [
            McpToolHarness.Frame("result", new { path, replacements = 1 }, 1),
            McpToolHarness.Frame("result", new { commitId }, 2),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        await Tool(previewBridge, policy).PreviewAsync("old", "new", harness.Root);

        // no-summary never closes its protocol stream, which is exactly how a torn commit looks.
        YaguCliBridge commitBridge = harness.Scenario("no-summary");
        string answer = await Tool(commitBridge, policy).CommitAsync(commitId);

        Assert.Contains("may already be on disk", answer, StringComparison.Ordinal);
        Assert.Contains(commitId, answer, StringComparison.Ordinal);

        // Dropping the scope after a failure closed off the writer's interrupted-commit report:
        // re-issuing the id answered "not issued by this server" instead of naming the torn batch.
        YaguCliBridge retry = harness.Emitting(
        [
            McpToolHarness.Frame("warning", new { message = "the previous commit was interrupted" }, 1),
            McpToolHarness.Summary(new { exitCode = 0 }),
        ]);
        string report = await Tool(retry, policy).CommitAsync(commitId);

        Assert.DoesNotContain("was not issued by this server", report, StringComparison.Ordinal);
        Assert.Contains("the previous commit was interrupted", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScopePastItsLifetime_IsDroppedRatherThanLeftCommittable()
    {
        using var harness = new McpToolHarness();
        McpAuthorizationPolicy policy = harness.Policy(CanReplace);
        string path = harness.MakeFile("a.cs", "old");

        async Task IssueAsync(string id)
        {
            YaguCliBridge previewBridge = harness.Emitting(
            [
                McpToolHarness.Frame("result", new { path, replacements = 1 }, 1),
                McpToolHarness.Frame("result", new { commitId = id }, 2),
                McpToolHarness.Summary(new { exitCode = 0 }),
            ]);
            await Tool(previewBridge, policy).PreviewAsync("old", "new", harness.Root);
        }

        string stale = NewCommitId();
        await IssueAsync(stale);

        // A commit id authorizes a write against the files the preview saw. Honouring one indefinitely
        // would let a stale id apply edits to a tree that has since moved on, so expiry has to evict it.
        ReplacePreviewTool.PurgeExpiredCommitScopes(DateTimeOffset.UtcNow.AddHours(1));

        string expired = await Tool(harness.Scenario("normal"), policy).CommitAsync(stale);
        Assert.Contains("was not issued by this server", expired, StringComparison.Ordinal);

        // The sweep must only evict what is past its lifetime; a scope issued moments ago survives it.
        string live = NewCommitId();
        await IssueAsync(live);
        ReplacePreviewTool.PurgeExpiredCommitScopes(DateTimeOffset.UtcNow);

        string answer = await Tool(
            harness.Emitting([McpToolHarness.Summary(new { exitCode = 0 })]), policy).CommitAsync(live);
        Assert.DoesNotContain("was not issued by this server", answer, StringComparison.Ordinal);
    }
}
