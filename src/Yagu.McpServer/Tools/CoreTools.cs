using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Yagu.Services.Cli.MachineOutput;

namespace Yagu.McpServer.Tools;

/// <summary>
/// The path-independent tools. <c>yagu_calc</c> takes no path at all, so it deliberately bypasses the
/// filesystem authorization policy — there is nothing to authorize — which also makes it the cheapest
/// possible end-to-end check that the bridge, the protocol and the transport all work.
/// </summary>
[McpServerToolType]
internal sealed class CalcTool(YaguCliBridge bridge, YaguMcpServerOptions options)
{
    [McpServerTool(Name = "yagu_calc")]
    [Description("Evaluate a math expression (for example \"2+2\", \"sqrt(9)*4\", \"15% of 340\") or a unit "
               + "conversion (for example \"5 km to miles\", \"72 f to c\", \"2 gb to mb\"). Does not touch the "
               + "filesystem.")]
    public async Task<string> CalcAsync(
        [Description("The expression or unit conversion to evaluate.")] string expression,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "error: an expression is required.";

        YaguCliResult result = await bridge.InvokeAsync(
            ["--calc", expression],
            options.DefaultCallTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Result && record.GetString("display") is { } display)
                return display;

            if (record.Type == MachineProtocol.Types.Error)
                return $"error: {record.GetString("message") ?? "the expression could not be evaluated."}";
        }

        return "error: the CLI returned no answer.";
    }
}

/// <summary>
/// Reads a bounded window of one file. Every path goes through the authorization policy before the
/// bridge is invoked, and the canonical path the policy returns — never the caller's raw request — is
/// what reaches the CLI.
/// </summary>
[McpServerToolType]
internal sealed class ReadFileTool(
    YaguCliBridge bridge,
    Services.Mcp.McpAuthorizationPolicy policy,
    YaguMcpServerOptions options)
{
    [McpServerTool(Name = "yagu_read_file")]
    [Description("Read a bounded window of lines from one file inside an allowed root. Returns at most "
               + "the requested lines; long lines are truncated and marked.")]
    public async Task<string> ReadFileAsync(
        [Description("Absolute path of the file to read. Must be inside an allowed root.")] string path,
        [Description("First line to return, 1-based. Defaults to 1.")] int firstLine = 1,
        [Description("Last line to return, inclusive. Omit or use 0 for 'to the end of the file'.")] int lastLine = 0,
        CancellationToken cancellationToken = default)
    {
        var decision = policy.AuthorizeRead(path);
        if (!decision.Allowed)
            return $"error [{decision.DenialCode}]: {decision.Reason}";

        if (firstLine < 1)
            firstLine = 1;

        string range = lastLine >= firstLine
            ? $"{firstLine}-{lastLine}"
            : $"{firstLine}-";

        // Authorizing above resolved the path as it stood THEN. The call can queue behind the bridge's
        // concurrency gate for as long as a model load takes, and an allowed subdirectory replaced by an
        // outward-pointing junction in that window redirects the eventual open. --confine-to makes the
        // reading process re-decide containment at the read itself, which is the only boundary that
        // cannot be raced.
        List<string> arguments = ["--read", decision.CanonicalPath!, "--lines", range];
        SearchTool.AddConfinement(arguments, policy);

        YaguCliResult result = await bridge.InvokeAsync(
            arguments,
            options.DefaultCallTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        var text = new StringBuilder();
        int truncatedLines = 0;
        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Error)
                return $"error: {record.GetString("message") ?? "the file could not be read."}";

            if (record.Type != MachineProtocol.Types.Result)
                continue;

            long line = record.GetInt64("line") ?? 0;
            text.Append(line).Append(": ").Append(record.GetString("text") ?? string.Empty);

            // The producer bounds each line to the configured byte ceiling. Without repeating that here,
            // a 10 MB minified line arrives looking like a complete short one, and a caller reasoning
            // about the file's contents — or writing an edit against them — is reasoning about a prefix.
            if (record.HasData
                && record.Data.TryGetProperty("truncated", out var truncatedFields)
                && truncatedFields.TryGetProperty("text", out var originalBytes))
            {
                truncatedLines++;
                text.Append(CultureInfo.InvariantCulture,
                    $"  [line truncated; {originalBytes.GetInt64():N0} bytes in full]");
            }

            text.AppendLine();
        }

        if (text.Length == 0)
            return "(no lines in the requested range)";

        if (result.Summary is { } summary && summary.GetBoolean("reachedEndOfFile") == false)
            text.AppendLine("... (more lines follow; request a later range to continue)");

        if (truncatedLines > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"({truncatedLines} line(s) were shortened to the configured line ceiling.)");
            text.AppendLine();
        }

        return text.ToString();
    }
}
