using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.McpServer.Tools;

/// <summary>
/// Shows what a find-and-replace WOULD do, and applies a reviewed preview.
/// </summary>
/// <remarks>
/// <para>
/// The preview and the commit are deliberately separate calls. A <c>confirm: true</c> parameter would
/// not be authorization — the same model that decides to call the tool would set the flag, so it defends
/// against an accidental default but not against prompt injection or a confidently wrong plan. The
/// commit instead takes a short-lived id bound to the content hashes the preview saw, so a file changed
/// since then is refused rather than silently rewritten by a re-run of the regex. The writes themselves
/// are off entirely unless the user has enabled them in Yagu's settings; that is the human gate.
/// </para>
/// <para>
/// The preview requires the <c>Replace</c> capability even though it writes nothing: seeing the proposed
/// rewrite of a file is itself a disclosure, and it is the same capability the commit needs.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class ReplacePreviewTool(
    YaguCliBridge bridge,
    McpAuthorizationPolicy policy,
    YaguMcpServerOptions options)
{
    [McpServerTool(Name = "yagu_replace_preview")]
    [Description("Preview a find-and-replace across a folder WITHOUT modifying any file. Returns per-file "
               + "counts and a sample of the lines that would change. This tool never writes.")]
    public async Task<string> PreviewAsync(
        [Description("Text to find. A literal string unless 'regex' is true.")] string query,
        [Description("Literal text to substitute for each match.")] string replacement,
        [Description("Absolute folder to search recursively. Required, and must be inside an allowed root.")] string root,
        [Description("Treat the query as a .NET regular expression.")] bool regex = false,
        [Description("Match case exactly.")] bool caseSensitive = false,
        [Description("Match whole words only.")] bool wholeWord = true,
        [Description("Semicolon-separated globs to include, for example '*.cs'.")] string? includeGlobs = null,
        [Description("Semicolon-separated globs to exclude, for example 'bin/*;obj/*'.")] string? excludeGlobs = null,
        [Description("Maximum files to consider.")] int maxFiles = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "error: a query is required.";

        if (!policy.HasCapability(McpCapability.Replace))
            return $"error [{McpDenialCode.CapabilityDenied}]: replacement preview is not enabled for this client.";

        McpAuthorizationDecision rootDecision = policy.AuthorizeSearchRoot(root);
        if (!rootDecision.Allowed)
            return $"error [{rootDecision.DenialCode}]: {rootDecision.Reason}";

        var arguments = new List<string>
        {
            "--directory", rootDecision.CanonicalPath!,
            "--pattern", query,
            "--replace", replacement,
            // Not negotiable from the tool surface: v1 never writes.
            "--dry-run",
            "--max-results", Math.Max(1, maxFiles).ToString(CultureInfo.InvariantCulture),
            regex ? "--regex" : "--no-regex",
            caseSensitive ? "--case-sensitive" : "--ignore-case",
            wholeWord ? "--exact-match" : "--no-exact-match",
            // Same gate as every other search surface. A dry run still SCANS, and scanning a cloud
            // placeholder downloads it, so "this tool never writes" does not make hydration free for a
            // client that was not granted it.
            policy.HasCapability(McpCapability.CloudHydration) ? "--online-only" : "--no-online-only",
        };

        if (!string.IsNullOrWhiteSpace(includeGlobs))
        {
            foreach (string glob in includeGlobs.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                arguments.Add("--glob");
                arguments.Add(glob);
            }
        }

        // yagu_search has always accepted excludes; without the same on the preview there was no way to
        // keep a replacement out of build output such as bin/ and obj/, which is exactly the kind of
        // sweep a caller most wants to narrow before committing it.
        if (!string.IsNullOrWhiteSpace(excludeGlobs))
        {
            foreach (string glob in excludeGlobs.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                arguments.Add("--exclude-glob");
                arguments.Add(glob);
            }
        }

        SearchTool.AddConfinement(arguments, policy);

        YaguCliResult result = await bridge.InvokeAsync(arguments, options.DefaultCallTimeout, cancellationToken, BridgeWorkload.Search)
            .ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        RememberCommitScope(result, policy);
        return Render(result, policy);    }

    /// <summary>
    /// Remembers which paths a commit id covers, so the commit can be re-authorized against the policy
    /// this server holds rather than trusting whatever the client passes back.
    /// </summary>
    private static void RememberCommitScope(YaguCliResult result, McpAuthorizationPolicy policy)
    {
        string? commitId = null;
        var paths = new List<string>();

        foreach (MachineRecord record in result.Records)
        {
            if (record.Type != MachineProtocol.Types.Result)
                continue;

            if (record.GetString("commitId") is { Length: > 0 } id)
                commitId = id;
            else if (record.GetString("path") is { Length: > 0 } path)
                paths.Add(path);
        }

        if (commitId is not null)
            CommitScopes[commitId] = new CommitScope(DateTimeOffset.UtcNow, paths);
    }

    /// <summary>A commit id issued by this server, the files it touches, and when it was issued.</summary>
    private sealed record CommitScope(DateTimeOffset IssuedUtc, List<string> Paths);

    /// <summary>
    /// How long an uncommitted scope is kept. Matches the CLI-side plan lifetime, which is what actually
    /// decides whether a commit is still accepted.
    /// </summary>
    private static readonly TimeSpan CommitScopeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Commit ids issued by this server, mapped to the files they touch.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CommitScope> CommitScopes = new();

    /// <summary>
    /// Drops scopes past their lifetime. Most previews are never committed, so without this the map
    /// grows for the life of a long-running server.
    /// </summary>
    private static void PurgeExpiredCommitScopes()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - CommitScopeLifetime;
        foreach (var entry in CommitScopes)
        {
            if (entry.Value.IssuedUtc < cutoff)
                CommitScopes.TryRemove(entry.Key, out _);
        }
    }

    [McpServerTool(Name = "yagu_replace_commit", Destructive = true)]
    [Description("Apply the edits from a previous yagu_replace_preview, identified by its commitId. "
               + "Files that changed since the preview are refused, not rewritten. Requires the user to "
               + "have enabled replacement writes in Yagu's settings.")]
    public async Task<string> CommitAsync(
        [Description("The commitId returned by yagu_replace_preview.")] string commitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commitId))
            return "error: a commitId is required.";

        if (!policy.HasCapability(McpCapability.Replace))
            return $"error [{McpDenialCode.CapabilityDenied}]: replacement is not enabled for this client.";

        PurgeExpiredCommitScopes();

        if (!CommitScopes.TryGetValue(commitId, out CommitScope? scope))
            return "error: that commitId was not issued by this server, or the server has restarted since.";

        // Re-authorize on the way IN, against the roots as they stand now. A preview does not buy a
        // permanent licence to write to those paths.
        foreach (string path in scope.Paths)
        {
            McpAuthorizationDecision decision = policy.AuthorizeReplaceTarget(path);
            if (!decision.Allowed)
                return $"error [{decision.DenialCode}]: {path}: {decision.Reason}";
        }

        // The CLI re-checks every planned path against these roots before it writes anything. This map
        // is the server's own accounting; --confine-to is what the writing process independently enforces.
        List<string> arguments = ["--replace-commit", commitId];
        SearchTool.AddConfinement(arguments, policy);

        YaguCliResult result = await bridge
            .InvokeAsync(arguments, options.DefaultCallTimeout, cancellationToken, cancellableOnceStarted: false)
            .ConfigureAwait(false);

        // Only a run that finished its protocol stream released the .inflight claim, so only then is this
        // id spent. Dropping the scope after a timeout closed off the writer's interrupted-commit report:
        // re-issuing the id answered "not issued by this server" instead of naming the torn batch.
        if (result.Outcome == YaguCliOutcome.Completed)
            CommitScopes.TryRemove(commitId, out _);

        if (result.Outcome != YaguCliOutcome.Completed)
        {
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()} "
                 + "Files are rewritten one at a time and the rollback could not run, so part of this batch "
                 + $"may already be on disk. Call yagu_replace_commit again with commitId \"{commitId}\" for "
                 + "the interrupted-commit report, and check the .yagubak backups beside the affected files.";
        }

        return RenderCommit(result);
    }

    private static string RenderCommit(YaguCliResult result)
    {
        var text = new StringBuilder();
        var written = new List<string>();
        string? rollbackError = null;

        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Warning)
                text.Append("refused/failed: ").AppendLine(record.GetString("message"));

            if (record.Type == MachineProtocol.Types.Result && record.GetBoolean("committed") == true)
            {
                written.Add($"{record.GetString("path")}"
                    + $"  ({record.GetInt64("replacements") ?? 0} replacement(s) written)");
            }

            if (record.Type == MachineProtocol.Types.Error)
            {
                // A per-file record says "this file was written"; it is emitted as the batch progresses
                // and cannot know a later file will fail. The rollback that follows undoes those writes
                // and deletes their backups, so replaying them as successes points the caller at
                // .yagubak files that no longer exist and at edits that are no longer on disk.
                if (record.GetString("code") == "commit-rolled-back")
                    rollbackError = record.GetString("message");
                else
                    text.Append("error: ").AppendLine(record.GetString("message"));
            }
        }

        if (rollbackError is not null)
        {
            text.AppendLine();
            text.Append("error: the batch was rolled back, so NOTHING it reported above was left on disk. ")
                .AppendLine(rollbackError);
            return text.ToString();
        }

        foreach (string line in written)
            text.AppendLine(line);

        text.AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"{written.Count} file(s) written. A .yagubak backup was made beside each one.");
        return text.ToString();
    }

    private static string Render(YaguCliResult result, McpAuthorizationPolicy policy)
    {
        var text = new StringBuilder();
        text.AppendLine("PREVIEW ONLY — no file was modified.");
        text.AppendLine();

        int files = 0;
        string? commitId = null;
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Warning)
                text.Append("warning: ").AppendLine(record.GetString("message"));

            if (record.Type != MachineProtocol.Types.Result)
                continue;

            if (record.GetString("commitId") is { Length: > 0 } id)
            {
                commitId = id;
                continue;
            }

            string path = record.GetString("path") ?? string.Empty;
            if (!policy.AuthorizeOutboundPath(path).Allowed)
                continue;

            files++;
            text.Append(path)
                .Append(CultureInfo.InvariantCulture, $"  ({record.GetInt64("replacements") ?? 0} replacement(s))")
                .AppendLine();

            if (record.HasData && record.Data.TryGetProperty("changes", out var changes))
            {
                foreach (var change in changes.EnumerateArray())
                {
                    long line = change.TryGetProperty("line", out var l) ? l.GetInt64() : 0;
                    text.Append(CultureInfo.InvariantCulture, $"    {line,6}  - ")
                        .AppendLine(change.TryGetProperty("before", out var b) ? b.GetString() : string.Empty);
                    text.Append(CultureInfo.InvariantCulture, $"    {line,6}  + ")
                        .AppendLine(change.TryGetProperty("after", out var a) ? a.GetString() : string.Empty);
                }
            }

            if (record.GetBoolean("changesTruncated") == true)
                text.AppendLine("           ... more changes in this file are not shown");
        }

        if (files == 0)
            text.AppendLine("(nothing would change)");

        if (commitId is not null)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture,
                $"To apply exactly these edits, call yagu_replace_commit with commitId \"{commitId}\". ");
            text.AppendLine("It expires shortly, is single-use, and refuses any file that changed in the meantime.");
        }

        if (result.Summary is { } summary
            && summary.HasData
            && summary.Data.TryGetProperty("replace", out var replace))
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture,
                $"attempted {Read(replace, "filesAttempted")}, would change {Read(replace, "filesChanged")}, ")
                .Append(CultureInfo.InvariantCulture,
                    $"unchanged {Read(replace, "filesUnchanged")}, skipped archives {Read(replace, "archivesSkipped")}, ")
                .Append(CultureInfo.InvariantCulture,
                    $"failed {Read(replace, "filesFailed")}; {Read(replace, "totalReplacements")} replacement(s) total.")
                .AppendLine();
        }

        return text.ToString();

        static long Read(System.Text.Json.JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
                ? value.GetInt64()
                : 0;
    }
}