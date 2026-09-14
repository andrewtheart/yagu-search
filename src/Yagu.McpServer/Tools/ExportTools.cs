using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.McpServer.Tools;

/// <summary>
/// Writes a search's results somewhere durable: a report file, or a reloadable session.
/// </summary>
/// <remarks>
/// These are the only tools that create files, so they all require the <c>ExportWrite</c> capability
/// and their destination is authorized as a write target — which checks that the directory the file
/// would land in resolves inside an allowed root, not merely that the path string looks contained.
/// </remarks>
[McpServerToolType]
internal sealed class ExportTools(
    YaguCliBridge bridge,
    McpAuthorizationPolicy policy,
    YaguMcpServerOptions options)
{
    [McpServerTool(Name = "yagu_export_report")]
    [Description("Run a search and write its results to a report file (html, json, or csv, inferred from "
               + "the extension). Returns a summary, not the report contents.")]
    public async Task<string> ExportReportAsync(
        [Description("Text to find. A literal string unless 'regex' is true.")] string query,
        [Description("Absolute folder to search recursively. Must be inside an allowed root.")] string root,
        [Description("Absolute path of the report to write. Must be inside an allowed root.")] string destination,
        [Description("Treat the query as a .NET regular expression.")] bool regex = false,
        [Description("Match case exactly.")] bool caseSensitive = false,
        [Description("Maximum matches to include.")] int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(query, root, destination);
        if (prepared.Error is not null)
            return prepared.Error;

        int bounded = BoundCollected(maxResults);

        var arguments = new List<string>
        {
            "--directory", prepared.Root!,
            "--pattern", query,
            "--export", prepared.Destination!,
            "--max-results", bounded.ToString(CultureInfo.InvariantCulture),
            regex ? "--regex" : "--no-regex",
            caseSensitive ? "--case-sensitive" : "--ignore-case",
            // Never hydrate cloud placeholders without the capability, whatever the global setting says.
            policy.HasCapability(McpCapability.CloudHydration) ? "--online-only" : "--no-online-only",
        };
        SearchTool.AddConfinement(arguments, policy);

        YaguCliResult result = await bridge.InvokeAsync(
            arguments,
            options.DefaultCallTimeout,
            cancellationToken,
            BridgeWorkload.Search).ConfigureAwait(false);

        return DescribeWrite(result, prepared.Destination!, "report")
             + DescribeCeiling(maxResults, bounded)
             + DescribeEngineCeiling(result, bounded);
    }

    [McpServerTool(Name = "yagu_save_session")]
    [Description("Run a search and save its full results as a Yagu session file that can be reloaded later "
               + "with yagu_load_session.")]
    public async Task<string> SaveSessionAsync(
        [Description("Text to find. A literal string unless 'regex' is true.")] string query,
        [Description("Absolute folder to search recursively. Must be inside an allowed root.")] string root,
        [Description("Absolute path of the .yagu-session file to write. Must be inside an allowed root.")] string destination,
        [Description("Treat the query as a .NET regular expression.")] bool regex = false,
        [Description("Match case exactly.")] bool caseSensitive = false,
        [Description("Maximum matches to store.")] int maxResults = 10000,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(query, root, destination);
        if (prepared.Error is not null)
            return prepared.Error;

        int bounded = BoundCollected(maxResults);

        var arguments = new List<string>
        {
            "--directory", prepared.Root!,
            "--pattern", query,
            "--save-session", prepared.Destination!,
            "--max-results", bounded.ToString(CultureInfo.InvariantCulture),
            regex ? "--regex" : "--no-regex",
            caseSensitive ? "--case-sensitive" : "--ignore-case",
            policy.HasCapability(McpCapability.CloudHydration) ? "--online-only" : "--no-online-only",
        };
        SearchTool.AddConfinement(arguments, policy);

        YaguCliResult result = await bridge.InvokeAsync(
            arguments,
            options.DefaultCallTimeout,
            cancellationToken,
            BridgeWorkload.Search).ConfigureAwait(false);

        return DescribeWrite(result, prepared.Destination!, "session")
             + DescribeCeiling(maxResults, bounded)
             + DescribeEngineCeiling(result, bounded);
    }

    [McpServerTool(Name = "yagu_load_session")]
    [Description("Re-emit the matches stored in a previously saved Yagu session file, without re-running "
               + "the search.")]
    public async Task<string> LoadSessionAsync(
        [Description("Absolute path of the .yagu-session file to load. Must be inside an allowed root.")] string sessionPath,
        [Description("Maximum matches to return in this page.")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        McpAuthorizationDecision decision = policy.AuthorizeRead(sessionPath);
        if (!decision.Allowed)
            return $"error [{decision.DenialCode}]: {decision.Reason}";

        YaguCliResult result = await bridge.InvokeAsync(
            [
                "--load-session", decision.CanonicalPath!,
                "--max-results", BoundCollected(maxResults).ToString(CultureInfo.InvariantCulture),
            ],
            options.DefaultCallTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        // A session stored inside an allowed root can still reference matches from anywhere on the
        // machine, so the stored paths are re-authorized on the way out.
        return SearchTool.RenderMatches(result, policy);
    }

    /// <summary>
    /// Clamps a requested match count to the configured ceiling.
    /// </summary>
    /// <remarks>
    /// Unlike a search, these commands hold every match in memory before writing, so the count a client
    /// asks for decides how much memory the child process uses. A client asking for a very large number
    /// would otherwise turn a bounded tool into an unbounded one.
    /// </remarks>
    private int BoundCollected(int requested) =>
        Math.Clamp(requested < 1 ? 1 : requested, 1, Math.Max(1, options.MaxExportResults));

    /// <summary>
    /// Says so when the ceiling shortened the result, because a file that looks complete but is not is
    /// worse than one the caller knows is partial.
    /// </summary>
    private string DescribeCeiling(int requested, int applied) =>
        requested > applied
            ? $"\nNote: {requested:N0} matches were requested but this server collects at most {applied:N0} "
              + "(Settings ▸ MCP Server ▸ Response Limits). The written file may be incomplete."
            : string.Empty;

    /// <summary>
    /// Says so when YAGU'S OWN result ceiling shortened the file. That limit is separate from this
    /// server's cap and user-configurable, so a request under the server cap can still be truncated.
    /// </summary>
    private static string DescribeEngineCeiling(YaguCliResult result, int bounded)
    {
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type != MachineProtocol.Types.Result)
                continue;

            if (record.GetInt64("maxResultsApplied") is { } applied && applied < bounded)
            {
                return $"\nNote: Yagu's own result ceiling of {applied:N0} is below the {bounded:N0} collected for "
                     + "this call, so the file may be incomplete. Raise it in Settings ▸ Performance.";
            }
        }

        return string.Empty;
    }

    private (string? Root, string? Destination, string? Error) Prepare(string query, string root, string destination)
    {        if (string.IsNullOrWhiteSpace(query))
            return (null, null, "error: a query is required.");

        if (!policy.HasCapability(McpCapability.ExportWrite))
            return (null, null, $"error [{McpDenialCode.CapabilityDenied}]: writing files is not enabled for this client.");

        McpAuthorizationDecision rootDecision = policy.AuthorizeSearchRoot(root);
        if (!rootDecision.Allowed)
            return (null, null, $"error [{rootDecision.DenialCode}]: {rootDecision.Reason}");

        McpAuthorizationDecision writeDecision = policy.AuthorizeWrite(destination);
        if (!writeDecision.Allowed)
            return (null, null, $"error [{writeDecision.DenialCode}]: {writeDecision.Reason}");

        return (rootDecision.CanonicalPath, writeDecision.CanonicalPath, null);
    }

    private static string DescribeWrite(YaguCliResult result, string destination, string kind)
    {
        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        bool written = false;
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Error)
                return $"error: {record.GetString("message") ?? $"the {kind} could not be written."}";

            // A search with no matches writes no file, and a write can fail outright. Both are reported
            // as warnings, and claiming "wrote the report" for a path that does not exist is worse than
            // any error message.
            if (record.Type == MachineProtocol.Types.Warning
                && record.GetString("code") is "export-empty" or "export-failed" or "session-write-failed")
            {
                return $"no {kind} was written: {record.GetString("message") ?? "the CLI did not write the file."}";
            }

            if (record.Type == MachineProtocol.Types.Result && record.GetBoolean("written") == true)
                written = true;
        }

        var text = new StringBuilder();
        text.AppendLine(written
            ? $"wrote the {kind} to {destination}"
            : $"the CLI reported no write confirmation for {destination}; verify the {kind} before relying on it");

        if (result.Summary is { } summary)
        {
            if (summary.GetInt64("totalMatches") is { } matches)
                text.Append(CultureInfo.InvariantCulture, $"{matches:N0} match(es)");
            if (summary.GetInt64("filesWithMatches") is { } files)
                text.Append(CultureInfo.InvariantCulture, $" in {files:N0} file(s)");
            if (summary.GetDouble("elapsedSeconds") is { } elapsed)
                text.Append(CultureInfo.InvariantCulture, $", {elapsed:N2}s");
            text.AppendLine(".");
        }

        // The CLI reports a write failure on stderr; surfacing it prevents a false "wrote the report".
        if (!string.IsNullOrWhiteSpace(result.StandardError)
            && result.StandardError.Contains("failed to", StringComparison.OrdinalIgnoreCase))
        {
            text.AppendLine();
            text.AppendLine(result.StandardError.Trim());
        }

        return text.ToString();
    }
}
