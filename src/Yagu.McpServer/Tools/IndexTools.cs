using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.McpServer.Tools;

/// <summary>
/// Content-index tools. Status and root listing answer immediately; build, rebuild, compact and
/// validate are modelled as jobs because they can run for minutes and their outcome matters — killing
/// one mid-flight would strand recovery work or a busy writer the client could never observe.
/// </summary>
[McpServerToolType]
internal sealed class IndexTools(
    YaguCliBridge bridge,
    McpAuthorizationPolicy policy,
    YaguMcpServerOptions options,
    BridgeJobRegistry jobs)
{
    private TimeSpan MaintenanceTimeout => options.MaintenanceTimeout;

    [McpServerTool(Name = "yagu_index_status")]
    [Description("Report whether a folder has a usable content index, how large it is, and when it was built.")]
    public async Task<string> StatusAsync(
        [Description("Absolute folder to report on. Must be inside an allowed root.")] string root,
        CancellationToken cancellationToken)
    {
        McpAuthorizationDecision decision = policy.AuthorizeIndexRoot(root);
        if (!decision.Allowed)
            return $"error [{decision.DenialCode}]: {decision.Reason}";

        YaguCliResult result = await bridge
            .InvokeAsync(["--index-status", decision.CanonicalPath!], options.DefaultCallTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        foreach (MachineRecord record in result.Records)
        {
            if (record.Type != MachineProtocol.Types.Result)
                continue;

            var text = new StringBuilder();
            text.Append("root: ").AppendLine(record.GetString("root"));
            text.Append("indexed: ").AppendLine(record.GetBoolean("exists") == true ? "yes" : "no");
            text.Append("health: ").AppendLine(record.GetString("healthLabel") ?? record.GetString("health"));

            if (record.GetBoolean("coveredByBroaderRoot") == true)
            {
                text.Append("covered by: ").AppendLine(record.GetString("coveringRoot"));
                text.AppendLine("note: searches use the broader index; a child index would be redundant.");
            }

            if (record.GetString("problem") is { } problem)
                text.Append("problem: ").AppendLine(problem);
            if (record.GetInt64("documentCount") is { } documents)
                text.Append(CultureInfo.InvariantCulture, $"stored records: {documents:N0}").AppendLine();
            if (record.GetInt64("segmentCount") is { } segments)
                text.Append(CultureInfo.InvariantCulture, $"segments: {segments:N0}").AppendLine();
            if (record.GetString("builtUtc") is { } built)
                text.Append("built (UTC): ").AppendLine(built);
            if (record.GetString("lastIncrementalUpdateUtc") is { } updated)
                text.Append("last incremental update (UTC): ").AppendLine(updated);

            return text.ToString();
        }

        return "error: the CLI returned no index status.";
    }

    [McpServerTool(Name = "yagu_index_roots")]
    [Description("List the folders registered for content indexing, with their per-folder filters and size policy.")]
    public async Task<string> RootsAsync(CancellationToken cancellationToken)
    {
        // Gated like every other index tool. Outbound filtering hides roots outside the allowed folders,
        // but without this a client with no index rights could still enumerate which folders inside them
        // are indexed and what their include/exclude policy is.
        if (!policy.HasCapability(McpCapability.IndexMaintenance))
        {
            return $"error [{McpDenialCode.CapabilityDenied}]: "
                 + "index maintenance is not enabled for this client.";
        }

        YaguCliResult result = await bridge
            .InvokeAsync(["--index-list-roots"], options.DefaultCallTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        var text = new StringBuilder();
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type != MachineProtocol.Types.Result)
                continue;

            string root = record.GetString("root") ?? string.Empty;

            // A registered root the client may not touch is not its business.
            if (!policy.AuthorizeOutboundPath(root).Allowed)
                continue;

            text.Append(root);
            if (record.GetBoolean("exists") == false)
                text.Append("  [missing]");
            if (record.GetString("includeGlobs") is { Length: > 0 } include)
                text.Append(CultureInfo.InvariantCulture, $"  [include={include}]");
            if (record.GetString("excludeGlobs") is { Length: > 0 } exclude)
                text.Append(CultureInfo.InvariantCulture, $"  [exclude={exclude}]");
            text.AppendLine();
        }

        return text.Length == 0
            ? "(no indexed folders are registered inside the allowed roots)"
            : text.ToString();
    }

    [McpServerTool(Name = "yagu_index_build_start", Destructive = true)]
    [Description("Start building or rebuilding the content index for a folder. Returns a job id immediately; "
               + "poll yagu_job_status. Indexing can run for many minutes on a large tree. "
               + "rebuild=true discards the stored index first and needs the destructive-index capability.")]
    public Task<string> BuildStartAsync(
        [Description("Absolute folder to index. Must be inside an allowed root.")] string root,
        [Description("Rebuild from scratch instead of building incrementally.")] bool rebuild = false)
    {
        // A rebuild throws the stored index away, so it is gated separately from an incremental build.
        McpAuthorizationDecision decision = rebuild
            ? policy.AuthorizeDestructiveIndexRoot(root)
            : policy.AuthorizeIndexRoot(root);
        if (!decision.Allowed)
            return Task.FromResult($"error [{decision.DenialCode}]: {decision.Reason}");

        string canonical = decision.CanonicalPath!;
        string flag = rebuild ? "--rebuild-index" : "--build-index";

        BridgeJob? job = jobs.Start(
            rebuild ? "index-rebuild" : "index-build",
            $"{(rebuild ? "rebuilding" : "building")} the content index for {canonical}",
            async token =>
            {
                YaguCliResult result = await bridge
                    .InvokeAsync([flag, canonical], MaintenanceTimeout, token, BridgeWorkload.IndexMutation)
                    .ConfigureAwait(false);

                JobTools.ThrowIfNotCompleted(result);
                string description = DescribeMaintenanceResult(result);
                JobTools.ThrowIfCommandFailed(result, description);
                return description;
            });

        return Task.FromResult(job is null
            ? JobTools.DescribeStartRefusal(jobs)
            : $"started job {job.Id}: {job.Description}. Poll yagu_job_status with this id.");
    }

    [McpServerTool(Name = "yagu_index_validate_start")]
    [Description("Start a structural validation of a folder's content index. Returns a job id immediately; "
               + "poll yagu_job_status.")]
    public Task<string> ValidateStartAsync(
        [Description("Absolute folder whose index should be validated. Must be inside an allowed root.")] string root)
    {
        McpAuthorizationDecision decision = policy.AuthorizeIndexRoot(root);
        if (!decision.Allowed)
            return Task.FromResult($"error [{decision.DenialCode}]: {decision.Reason}");

        string canonical = decision.CanonicalPath!;

        BridgeJob? job = jobs.Start(
            "index-validate",
            $"validating the content index for {canonical}",
            async token =>
            {
                YaguCliResult result = await bridge
                    .InvokeAsync(["--validate-index", canonical], MaintenanceTimeout, token, BridgeWorkload.IndexMutation)
                    .ConfigureAwait(false);

                JobTools.ThrowIfNotCompleted(result);
                string description = DescribeMaintenanceResult(result);
                JobTools.ThrowIfCommandFailed(result, description);
                return description;
            });

        return Task.FromResult(job is null
            ? JobTools.DescribeStartRefusal(jobs)
            : $"started job {job.Id}: {job.Description}. Poll yagu_job_status with this id.");
    }

    [McpServerTool(Name = "yagu_index_add_root")]
    [Description("Register a folder for content indexing. This only enrolls it; nothing is built until "
               + "yagu_index_build_start runs. Must be inside an allowed root.")]
    public async Task<string> AddRootAsync(
        [Description("Absolute folder to register. Must be inside an allowed root.")] string root,
        CancellationToken cancellationToken)
    {
        McpAuthorizationDecision decision = policy.AuthorizeIndexRoot(root);
        if (!decision.Allowed)
            return $"error [{decision.DenialCode}]: {decision.Reason}";

        YaguCliResult result = await bridge
            .InvokeAsync(["--index-add-root", decision.CanonicalPath!], options.DefaultCallTimeout,
                cancellationToken, BridgeWorkload.IndexMutation)
            .ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        return CliExitCode.IsSuccess(result.ExitCode)
            ? $"registered {decision.CanonicalPath} for content indexing. Nothing is built yet \u2014 call yagu_index_build_start to build it."
            : DescribeMaintenanceResult(result);
    }

    [McpServerTool(Name = "yagu_index_remove_root", Destructive = true)]
    [Description("Unregister a folder from content indexing. Searches still work; they read that folder "
               + "live instead. This does not delete the stored index data. "
               + "Needs the destructive-index capability.")]
    public async Task<string> RemoveRootAsync(
        [Description("Absolute folder to unregister. Must be inside an allowed root.")] string root,
        CancellationToken cancellationToken)
    {
        McpAuthorizationDecision decision = policy.AuthorizeDestructiveIndexRoot(root);
        if (!decision.Allowed)
            return $"error [{decision.DenialCode}]: {decision.Reason}";

        YaguCliResult result = await bridge
            .InvokeAsync(["--index-remove-root", decision.CanonicalPath!], options.DefaultCallTimeout,
                cancellationToken, BridgeWorkload.IndexMutation)
            .ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        return CliExitCode.IsSuccess(result.ExitCode)
            ? $"unregistered {decision.CanonicalPath}. Searches over it now read files live; its stored index data was not deleted."
            : DescribeMaintenanceResult(result);
    }

    [McpServerTool(Name = "yagu_index_compact_start")]
    [Description("Start compacting a folder's content index, folding its accumulated update layers into "
               + "one. Returns a job id immediately; poll yagu_job_status. This can run for minutes.")]
    public Task<string> CompactStartAsync(
        [Description("Absolute folder whose index should be compacted. Must be inside an allowed root.")] string root)
    {
        McpAuthorizationDecision decision = policy.AuthorizeIndexRoot(root);
        if (!decision.Allowed)
            return Task.FromResult($"error [{decision.DenialCode}]: {decision.Reason}");

        string canonical = decision.CanonicalPath!;

        BridgeJob? job = jobs.Start(
            "index-compact",
            $"compacting the content index for {canonical}",
            async token =>
            {
                YaguCliResult result = await bridge
                    .InvokeAsync(["--compact-index", canonical], MaintenanceTimeout, token, BridgeWorkload.IndexMutation)
                    .ConfigureAwait(false);

                JobTools.ThrowIfNotCompleted(result);
                string description = DescribeMaintenanceResult(result);
                JobTools.ThrowIfCommandFailed(result, description);
                return description;
            });

        return Task.FromResult(job is null
            ? JobTools.DescribeStartRefusal(jobs)
            : $"started job {job.Id}: {job.Description}. Poll yagu_job_status with this id.");
    }

    /// <summary>
    /// Index commands keep their own published exit codes, so the raw code is reported alongside the
    /// protocol outcome rather than being flattened into a generic success or failure.
    /// </summary>
    private static string DescribeMaintenanceResult(YaguCliResult result)
    {
        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        var text = new StringBuilder();
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Warning)
                text.Append("warning: ").AppendLine(record.GetString("message"));
            else if (record.Type == MachineProtocol.Types.Error)
                text.Append("error: ").AppendLine(record.GetString("message"));
        }

        if (result.Summary is { } summary)
        {
            string outcome = summary.GetString("outcome") ?? "unknown";
            long indexExitCode = summary.GetInt64("indexExitCode") ?? result.ExitCode;
            text.Append(CultureInfo.InvariantCulture, $"outcome: {outcome} (index exit code {indexExitCode})");
            text.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            text.AppendLine();
            text.AppendLine("details:");
            text.AppendLine(result.StandardError.Trim());
        }

        return text.ToString();
    }
}

/// <summary>Polling surface shared by every long-running tool.</summary>
[McpServerToolType]
internal sealed class JobTools(BridgeJobRegistry jobs)
{
    /// <summary>The message a tool returns when the in-flight job limit refused a new job.</summary>
    internal static string DescribeStartRefusal(BridgeJobRegistry registry) =>
        $"error: {registry.MaxRunningJobs} job(s) are already running, which is this server's limit. "
        + "Wait for one to finish (yagu_job_list), or cancel one with yagu_job_cancel.";

    /// <summary>
    /// Turns a non-completed bridge outcome into the exception the registry maps to a terminal state.
    /// </summary>
    /// <remarks>
    /// Returning an "error: ..." string instead marks the job <c>Completed</c>, so a cancelled or
    /// timed-out job reports success with an error body — and cancelling after launch would look
    /// different from cancelling while still queued.
    /// </remarks>
    internal static void ThrowIfNotCompleted(YaguCliResult result)
    {
        if (result.Outcome == YaguCliOutcome.Completed)
            return;

        if (result.Outcome == YaguCliOutcome.Cancelled)
            throw new OperationCanceledException(result.Failure ?? "the job was cancelled.");

        throw new InvalidOperationException(result.Failure ?? result.Outcome.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Raises the index command's OWN terminal outcome, which the transport cannot see.
    /// </summary>
    /// <remarks>
    /// A completed protocol stream only means the child ran and spoke the protocol. A build refused
    /// because another holds the index writer, or aborted on a full disk, does exactly that — so
    /// without this a job whose maintenance never touched the index is listed as <c>Completed</c> and a
    /// caller polling <c>yagu_job_status</c> reads the failure body as a success.
    /// </remarks>
    /// <param name="description">The rendered result, carried into the failure so no detail is lost.</param>
    internal static void ThrowIfCommandFailed(YaguCliResult result, string description)
    {
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Error)
                throw new InvalidOperationException(description);
        }

        if (result.Summary is not { } summary)
            return;

        string outcome = summary.GetString("outcome") ?? string.Empty;
        if (outcome.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            throw new OperationCanceledException(description);

        // "success" is the only terminal state in which the index actually changed. Anything else —
        // busy, failed, partial-failure — is a job that did not do what it was asked to.
        if (outcome.Length > 0 && !outcome.Equals("success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(description);
    }
    [McpServerTool(Name = "yagu_job_status")]
    [Description("Check a long-running Yagu job started by another tool. Returns its state, and its full "
               + "result once it has finished.")]
    public string Status(
        [Description("The job id returned when the job was started.")] string jobId)
    {
        BridgeJob? job = jobs.Find(jobId);
        return job is null
            ? $"error: no job named '{jobId}'. It may have finished long ago and been pruned, or the server may have restarted."
            : BridgeJobRegistry.Describe(job);
    }

    [McpServerTool(Name = "yagu_job_list")]
    [Description("List the Yagu jobs this server session knows about.")]
    public string List()
    {
        IReadOnlyList<BridgeJob> all = jobs.All();
        if (all.Count == 0)
            return "(no jobs)";

        var text = new StringBuilder();
        foreach (BridgeJob job in all)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{job.Id}  {job.State.ToString().ToLowerInvariant(),-9}  {job.Elapsed.TotalSeconds:N1}s  {job.Description}");
            text.AppendLine();
        }

        return text.ToString();
    }

    [McpServerTool(Name = "yagu_job_cancel")]
    [Description("Cancel a running Yagu job. The child process and its whole worker tree are terminated.")]
    public string Cancel(
        [Description("The job id to cancel.")] string jobId)
        => jobs.Cancel(jobId)
            ? $"cancelling job {jobId}."
            : $"error: job '{jobId}' is not running (it may have already finished, or never existed).";
}
