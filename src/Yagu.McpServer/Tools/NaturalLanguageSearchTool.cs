using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.McpServer.Tools;

/// <summary>
/// Natural-language search, exposed as a job rather than a synchronous call.
/// </summary>
/// <remarks>
/// <para>
/// This is a job because of measured <b>variance</b>, not merely slowness. Driving the same on-device
/// model through six queries produced 8.9 s, 20.9 s, 39.7 s and ~950 s runs; no synchronous tool call
/// survives that spread. Each run is a fresh CLI invocation that loads and releases its own model —
/// holding one resident was measured at roughly 25x worse, so there is deliberately no warm host.
/// </para>
/// <para>
/// The single invocation returns both the interpreted plan and the results, so the plan a caller sees
/// is always the one that produced those results. Asking for the plan separately would load the model
/// twice and could resample to a different plan.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class NaturalLanguageSearchTool(
    YaguCliBridge bridge,
    McpAuthorizationPolicy policy,
    YaguMcpServerOptions options,
    BridgeJobRegistry jobs)
{
    private TimeSpan TranslationTimeout => options.ModelTimeout;

    [McpServerTool(Name = "yagu_search_natural_language_start")]
    [Description("Start a natural-language search that Yagu's on-device model translates into concrete "
               + "search options. Returns a job id immediately; poll yagu_job_status. This is SLOW and "
               + "highly variable (seconds to many minutes) because the model loads per call — prefer "
               + "yagu_search when you can express the query as a pattern and filters yourself.")]
    public Task<string> StartAsync(
        [Description("The request in plain language, for example 'large pdf reports created since January'.")] string request,
        [Description("Absolute folder to search. Required, and must be inside an allowed root.")] string root,
        [Description("Maximum matches to return in this page.")] int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(request))
            return Task.FromResult("error: a request is required.");

        McpAuthorizationDecision decision = policy.AuthorizeSearchRoot(root);
        if (!decision.Allowed)
            return Task.FromResult($"error [{decision.DenialCode}]: {decision.Reason}");

        string canonical = decision.CanonicalPath!;
        int bounded = Math.Max(1, maxResults);

        BridgeJob? job = jobs.Start(
            "nl-search",
            $"interpreting \"{Trim(request)}\" and searching {canonical}",
            async token =>
            {
                var arguments = new List<string>
                {
                    "--semantic-pattern", request,
                    "--directory", canonical,
                    "--max-results", bounded.ToString(CultureInfo.InvariantCulture),
                    policy.HasCapability(McpCapability.CloudHydration) ? "--online-only" : "--no-online-only",
                };
                SearchTool.AddConfinement(arguments, policy);

                YaguCliResult result = await bridge.InvokeAsync(
                    arguments,
                    TranslationTimeout,
                    token,
                    // Loads an AI model: serialized across every Yagu MCP server on the machine, because
                    // each concurrent load costs gigabytes.
                    BridgeWorkload.Model).ConfigureAwait(false);

                JobTools.ThrowIfNotCompleted(result);
                return Render(result, policy);
            });

        if (job is null)
            return Task.FromResult(JobTools.DescribeStartRefusal(jobs));

        return Task.FromResult(
            $"started job {job.Id}: {job.Description}. Poll yagu_job_status with this id; this can take "
            + "anywhere from a few seconds to several minutes.");
    }

    private static string Trim(string request) =>
        request.Length <= 60 ? request : request[..57] + "...";

    private static string Render(YaguCliResult result, McpAuthorizationPolicy policy)
    {
        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        var text = new StringBuilder();

        foreach (MachineRecord record in result.Records)
        {
            if (record.Type != MachineProtocol.Types.Plan)
                continue;

            text.AppendLine("interpreted as:");
            if (record.GetString("summary") is { } summary)
                text.Append("  ").AppendLine(summary);
            if (record.GetString("model") is { Length: > 0 } model)
                text.Append("  model: ").AppendLine(model);
            if (record.GetString("pattern") is { Length: > 0 } pattern)
                text.Append("  pattern: ").AppendLine(pattern);
            if (record.GetString("directory") is { Length: > 0 } directory)
                text.Append("  directory: ").AppendLine(directory);
            text.AppendLine();
        }

        // Non-interactive translation failures are reported as warnings plus a non-success exit, which
        // is a legitimate outcome rather than a bridge fault: no model, no consent, or an unusable plan.
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Warning)
                text.Append("warning: ").AppendLine(record.GetString("message"));
        }

        text.Append(SearchTool.RenderMatches(result, policy));
        return text.ToString();
    }
}
