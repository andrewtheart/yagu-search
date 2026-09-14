using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu.McpServer.Tools;

/// <summary>
/// Text search over an allowed root.
/// </summary>
/// <remarks>
/// The root is <b>required</b>. Yagu's CLI treats an omitted directory as "search every drive", so a
/// tool that let the model leave it out would hand over the whole machine by omission. Results are one
/// bounded page: there is no cross-call offset paging, because each call is a fresh scan with no stable
/// ordering to page against.
/// </remarks>
[McpServerToolType]
internal sealed class SearchTool(
    YaguCliBridge bridge,
    McpAuthorizationPolicy policy,
    YaguMcpServerOptions options)
{
    [McpServerTool(Name = "yagu_search")]
    [Description("Use this instead of shell commands or workspace search for Windows filesystem searches "
               + "across an entire drive, all files under an absolute folder, or locations outside the "
               + "current workspace. This includes requests such as 'find all instances of the string X "
               + "in all files on C:\\', 'search every file under D:\\ for X', or 'which files contain X?'. "
               + "It also finds or lists files by name, path, or extension. "
               + "Use searchMode='content' for 'contains', 'in it', or 'inside' requests and 'filenames' "
               + "for name or extension requests. Keep all per-file matches for 'all instances' or 'all "
               + "occurrences'; for 'which files contain' or 'all files containing', set maxMatchesPerFile=1. "
               + "Returns one bounded page; narrow the query or root if truncated.")]
    public async Task<string> SearchAsync(
        [Description("Text to find. A literal string unless 'regex' is true.")] string query,
        [Description("Absolute folder to search recursively. Required, and must be inside an allowed root.")] string root,
        [Description("Treat the query as a .NET regular expression.")] bool regex = false,
        [Description("Match case exactly.")] bool caseSensitive = false,
        [Description("Match whole words only. Set false for 'string', 'substring', 'contains', or 'in it' requests. Defaults to true.")] bool wholeWord = true,
        [Description("Let one match span line breaks. Reads whole files; pair with a narrow scope.")] bool multiline = false,
        [Description("Lines of context to include around each match.")] int contextLines = 2,
        [Description("Semicolon-separated globs to include, for example '*.cs;*.md'.")] string? includeGlobs = null,
        [Description("Semicolon-separated globs to exclude, for example '*/bin/*;*/obj/*'.")] string? excludeGlobs = null,
        [Description("What to search: 'content' means text inside files; 'filenames' means names and paths; 'both' searches both surfaces; 'filename-then-content' requires both.")] string searchMode = "both",
        [Description("Include files and folders carrying the Hidden attribute.")] bool includeHidden = true,
        [Description("Search inside ZIP-like archives.")] bool searchArchives = false,
        [Description("Maximum directory depth. 0 means unlimited.")] int maxDepth = 0,
        [Description("Maximum matches returned per file. Use 1 when asking which or all files contain the query; omit to use the configured default; 0 means unlimited.")] int? maxMatchesPerFile = null,
        [Description("Maximum matches to return in this page.")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "error: a query is required.";

        McpAuthorizationDecision rootDecision = policy.AuthorizeSearchRoot(root);
        if (!rootDecision.Allowed)
            return $"error [{rootDecision.DenialCode}]: {rootDecision.Reason}";

        // Bounded the same way an export is. The producer-side budget stops the WIRE from growing, but
        // an unbounded ask still makes the bridge materialize a record per match in this process first.
        maxResults = Math.Clamp(maxResults, 1, options.MaxExportResults);

        // Clamped before it reaches the scanner, which sizes a per-file context ring from this value.
        // int.MaxValue would exhaust the child's memory long before any output budget applied.
        contextLines = Math.Clamp(contextLines, 0, MachineOutputBudget.MaxContextLinesCeiling);

        var arguments = new List<string>
        {
            "--directory", rootDecision.CanonicalPath!,
            "--pattern", query,
            "--max-results", maxResults.ToString(CultureInfo.InvariantCulture),
            "--context", contextLines.ToString(CultureInfo.InvariantCulture),
            "--search-mode", NormalizeSearchMode(searchMode),
            regex ? "--regex" : "--no-regex",
            caseSensitive ? "--case-sensitive" : "--ignore-case",
            wholeWord ? "--exact-match" : "--no-exact-match",
            multiline ? "--multiline" : "--no-multiline",
            includeHidden ? "--hidden" : "--no-hidden",
            searchArchives ? "--search-archives" : "--no-search-archives",
            // Cloud hydration is a separate capability: never force a placeholder download implicitly.
            policy.HasCapability(McpCapability.CloudHydration) ? "--online-only" : "--no-online-only",
        };

        if (maxMatchesPerFile is not null)
        {
            arguments.Add("--max-matches-per-file");
            arguments.Add(Math.Max(0, maxMatchesPerFile.Value).ToString(CultureInfo.InvariantCulture));
        }

        if (maxDepth > 0)
        {
            arguments.Add("--max-depth");
            arguments.Add(maxDepth.ToString(CultureInfo.InvariantCulture));
        }

        AddGlobs(arguments, "--glob", includeGlobs);
        AddGlobs(arguments, "--exclude-glob", excludeGlobs);
        AddConfinement(arguments, policy);

        YaguCliResult result = await bridge.InvokeAsync(arguments, options.DefaultCallTimeout, cancellationToken, BridgeWorkload.Search)
            .ConfigureAwait(false);

        if (result.Outcome != YaguCliOutcome.Completed)
            return $"error: {result.Failure ?? result.Outcome.ToString().ToLowerInvariant()}";

        return RenderMatches(result, policy);
    }

    /// <summary>
    /// Confines the CLI run to the allowed roots, resolved through links.
    /// </summary>
    /// <remarks>
    /// Authorizing the requested root is not enough: the walker follows directory junctions, so a link
    /// inside an allowed folder produces matches whose real location is outside it. Filtering outbound
    /// covers a plain search, but an export, session, or replacement is written by the CLI itself.
    /// </remarks>
    internal static void AddConfinement(List<string> arguments, McpAuthorizationPolicy policy)
    {
        foreach (string root in policy.AllowedRoots)
        {
            arguments.Add("--confine-to");
            arguments.Add(root);
        }
    }

    private static void AddGlobs(List<string> arguments, string flag, string? globs)    {
        if (string.IsNullOrWhiteSpace(globs))
            return;

        foreach (string glob in globs.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            arguments.Add(flag);
            arguments.Add(glob);
        }
    }

    private static string NormalizeSearchMode(string mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "content" => "content",
        "filenames" or "filename" => "filenames",
        "filename-then-content" => "filename-then-content",
        _ => "both",
    };

    /// <summary>
    /// Renders the bounded page. Every returned path is re-authorized: a junction inside an allowed
    /// root can yield a match whose real location is outside it, and filtering only the inputs would
    /// leak that path back to the client.
    /// </summary>
    internal static string RenderMatches(YaguCliResult result, McpAuthorizationPolicy policy)
    {
        var text = new StringBuilder();
        int shown = 0;
        int suppressed = 0;

        foreach (MachineRecord record in result.Records)
        {
            if (record.Type == MachineProtocol.Types.Error)
                return $"error: {record.GetString("message") ?? "the search failed."}";

            if (record.Type != MachineProtocol.Types.Match)
                continue;

            string path = record.GetString("path") ?? string.Empty;

            // A path the producer had to shorten cannot be authorized: the surviving prefix can name a
            // directory that IS contained while the real path is not, so containment would be decided
            // on a string that never existed on disk. Fail closed.
            if (record.GetBoolean("pathTruncated") == true || !policy.AuthorizeOutboundPath(path).Allowed)
            {
                suppressed++;
                continue;
            }

            long line = record.GetInt64("line") ?? 0;
            string matchText = (record.GetString("text") ?? string.Empty).TrimEnd();

            // The caller asked for context lines and the producer emitted them; dropping them here made
            // contextLines a parameter that silently did nothing, and a model that needs surrounding
            // code had to re-read the file it just searched.
            AppendContext(text, record, "contextBefore", line, before: true);

            text.Append(path);
            if (line > 0)
                text.Append(':').Append(line);
            text.Append(": ").Append(matchText);

            // The scanner trims a very long match line before it ever reaches the protocol, so a
            // consumer cannot otherwise tell a complete line from the first 500 characters of one.
            if (record.GetBoolean("engineTruncatedText") == true
                || (record.HasData
                    && record.Data.TryGetProperty("truncated", out var truncatedFields)
                    && truncatedFields.TryGetProperty("text", out _)))
            {
                text.Append("  [line truncated]");
            }

            text.AppendLine();
            AppendContext(text, record, "contextAfter", line, before: false);
            shown++;
        }

        if (shown == 0 && suppressed == 0)
            text.AppendLine("(no matches)");

        if (result.Summary is { } summary)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"{shown} match(es) returned");

            if (summary.GetInt64("filesScanned") is { } filesScanned)
                text.Append(CultureInfo.InvariantCulture, $"; {filesScanned:N0} file(s) scanned");
            if (summary.GetDouble("elapsedSeconds") is { } elapsed)
                text.Append(CultureInfo.InvariantCulture, $" in {elapsed:N2}s");

            if (summary.GetBoolean("truncated") == true)
                text.Append("; this page was truncated — narrow the query or the root");
            // A separate ceiling from the response budget: the engine itself stopped scanning. Without
            // it, an engine-shortened result set was reported as a complete answer.
            if (summary.GetBoolean("engineTruncated") == true)
                text.Append("; the scan stopped at the engine's result ceiling, so this is not every match");
            if (summary.GetBoolean("cancelled") == true)
                text.Append("; the scan was cancelled before completing");

            text.AppendLine(".");
        }

        if (suppressed > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{suppressed} match(es) were withheld because they resolve outside the allowed roots.");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Renders one context block, numbering the lines relative to the match.</summary>
    private static void AppendContext(
        StringBuilder text, MachineRecord record, string field, long matchLine, bool before)
    {
        if (!record.HasData
            || !record.Data.TryGetProperty(field, out var lines)
            || lines.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return;
        }

        int count = lines.GetArrayLength();
        if (count == 0)
            return;

        long first = before ? matchLine - count : matchLine + 1;
        long offset = 0;
        foreach (var element in lines.EnumerateArray())
        {
            long number = first + offset++;
            text.Append("  ");
            if (matchLine > 0 && number > 0)
                text.Append(number).Append('-');
            text.AppendLine((element.GetString() ?? string.Empty).TrimEnd());
        }
    }
}
