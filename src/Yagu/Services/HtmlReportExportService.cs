using System.Net;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>
/// Generates HTML search reports. Pure I/O — no UI dependencies.
/// </summary>
public static class HtmlReportExportService
{
    public sealed record SearchStats(
        DateTime StartedUtc,
        TimeSpan Elapsed,
        long FilesScanned,
        long BytesScanned);

    public sealed record FileMatchGroup(string FilePath, string FileName, List<SearchResult> Results);

    public static async Task WriteMultiFileReportAsync(
        TextWriter writer,
        string query,
        IReadOnlyList<FileMatchGroup> groups,
        SearchStats stats)
    {
        var exportGroups = GetExportableGroups(groups);
        int totalMatches = 0;
        foreach (var g in exportGroups) totalMatches += g.Results.Count;

        var queryHtml = WebUtility.HtmlEncode(query);

        await WriteHeaderAsync(writer, $"Yagu Report — {queryHtml}", queryHtml,
            $"{exportGroups.Count:N0} file(s), {totalMatches:N0} match(es)").ConfigureAwait(false);

        // Search statistics table
        double seconds = Math.Max(stats.Elapsed.TotalSeconds, 0.001);
        double filesPerSec = stats.FilesScanned / seconds;
        double mbPerSec = stats.BytesScanned / (1024.0 * 1024.0) / seconds;
        var endUtc = stats.StartedUtc + stats.Elapsed;

        await writer.WriteLineAsync("<table class=\"stats\">").ConfigureAwait(false);
        await writer.WriteLineAsync($"<tr><td><b>Started:</b></td><td>{stats.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC</td></tr>").ConfigureAwait(false);
        await writer.WriteLineAsync($"<tr><td><b>Finished:</b></td><td>{endUtc:yyyy-MM-dd HH:mm:ss} UTC</td></tr>").ConfigureAwait(false);
        await writer.WriteLineAsync($"<tr><td><b>Duration:</b></td><td>{stats.Elapsed.Hours:D2}:{stats.Elapsed.Minutes:D2}:{stats.Elapsed.Seconds:D2}.{stats.Elapsed.Milliseconds:D3}</td></tr>").ConfigureAwait(false);
        await writer.WriteLineAsync($"<tr><td><b>Files scanned:</b></td><td>{stats.FilesScanned:N0}</td></tr>").ConfigureAwait(false);
        await writer.WriteLineAsync($"<tr><td><b>Throughput:</b></td><td>{filesPerSec:N1} files/sec, {mbPerSec:N1} MB/s</td></tr>").ConfigureAwait(false);
        await writer.WriteLineAsync("</table>").ConfigureAwait(false);

        foreach (var group in exportGroups)
            await WriteFileGroupAsync(writer, group).ConfigureAwait(false);

        await writer.WriteLineAsync("</body></html>").ConfigureAwait(false);
    }

    public static async Task WriteSingleFileReportAsync(
        TextWriter writer,
        string query,
        FileMatchGroup group)
    {
        var exportGroup = CreateExportableGroup(group);
        var queryHtml = WebUtility.HtmlEncode(query);
        var titleHtml = WebUtility.HtmlEncode(Path.GetFileName(group.FilePath));

        await WriteHeaderAsync(writer, $"Yagu Report — {titleHtml}",
            $"Report: <code>{queryHtml}</code>",
            $"{exportGroup.Results.Count:N0} match(es) in this file").ConfigureAwait(false);

        if (exportGroup.Results.Count > 0)
            await WriteFileGroupAsync(writer, exportGroup).ConfigureAwait(false);
        await writer.WriteLineAsync("</body></html>").ConfigureAwait(false);
    }

    public static string BuildHighlightedMatchHtml(string line, int matchStart, int matchLength)
    {
        if (matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
            return WebUtility.HtmlEncode(line);

        int safeLen = Math.Min(matchLength, line.Length - matchStart);
        var before = WebUtility.HtmlEncode(line[..matchStart]);
        var match = WebUtility.HtmlEncode(line.Substring(matchStart, safeLen));
        var after = WebUtility.HtmlEncode(line[(matchStart + safeLen)..]);
        return $"{before}<mark>{match}</mark>{after}";
    }

    private static async Task WriteHeaderAsync(TextWriter w, string title, string h1Content, string summary)
    {
        await w.WriteLineAsync("<!DOCTYPE html>").ConfigureAwait(false);
        await w.WriteLineAsync("<html><head><meta charset=\"utf-8\">").ConfigureAwait(false);
        await w.WriteLineAsync($"<title>{title}</title>").ConfigureAwait(false);
        await w.WriteLineAsync("<style>").ConfigureAwait(false);
        await w.WriteLineAsync("body { font-family: 'Segoe UI', Consolas, monospace; background: #1e1e1e; color: #d4d4d4; margin: 2em; }").ConfigureAwait(false);
        await w.WriteLineAsync("h1 { color: #569cd6; font-size: 1.4em; }").ConfigureAwait(false);
        await w.WriteLineAsync(".file-group { margin: 1.5em 0; border-top: 1px solid #333; }").ConfigureAwait(false);
        await w.WriteLineAsync(".file-summary { color: #9cdcfe; cursor: pointer; display: flex; align-items: center; gap: 0.55em; padding: 0.65em 0 0.45em; font-size: 1.1em; font-weight: 600; list-style: none; }").ConfigureAwait(false);
        await w.WriteLineAsync(".file-summary::-webkit-details-marker { display: none; }").ConfigureAwait(false);
        await w.WriteLineAsync(".file-summary:focus-visible { outline: 2px solid #569cd6; outline-offset: 3px; }").ConfigureAwait(false);
        await w.WriteLineAsync(".chevron { width: 0.55em; height: 0.55em; border-right: 2px solid #9cdcfe; border-bottom: 2px solid #9cdcfe; transform: rotate(-45deg); transition: transform 120ms ease; flex: 0 0 auto; }").ConfigureAwait(false);
        await w.WriteLineAsync("details[open] > .file-summary .chevron { transform: rotate(45deg); }").ConfigureAwait(false);
        await w.WriteLineAsync(".file-name { min-width: 0; overflow-wrap: anywhere; }").ConfigureAwait(false);
        await w.WriteLineAsync(".match-count { color: #6a9955; font-size: 0.8em; font-weight: 400; margin-left: auto; white-space: nowrap; }").ConfigureAwait(false);
        await w.WriteLineAsync(".file-path { color: #808080; font-size: 0.85em; }").ConfigureAwait(false);
        await w.WriteLineAsync("table { border-collapse: collapse; margin: 0.5em 0 1.5em 0; width: 100%; }").ConfigureAwait(false);
        await w.WriteLineAsync("td { padding: 1px 8px; vertical-align: top; white-space: pre-wrap; word-break: break-all; font-family: Consolas, 'Courier New', monospace; font-size: 0.9em; }").ConfigureAwait(false);
        await w.WriteLineAsync("td.ln { color: #858585; text-align: right; user-select: none; width: 1%; white-space: nowrap; padding-right: 12px; border-right: 1px solid #333; }").ConfigureAwait(false);
        await w.WriteLineAsync("tr.match { background: #2a2d2e; }").ConfigureAwait(false);
        await w.WriteLineAsync("tr.ctx { opacity: 0.7; }").ConfigureAwait(false);
        await w.WriteLineAsync("mark { background: #b5890066; color: #ffd700; font-weight: bold; }").ConfigureAwait(false);
        await w.WriteLineAsync(".summary { color: #6a9955; margin-bottom: 1em; }").ConfigureAwait(false);
        await w.WriteLineAsync(".stats { color: #9cdcfe; font-size: 0.9em; margin-bottom: 1.5em; }").ConfigureAwait(false);
        await w.WriteLineAsync(".stats td { padding: 2px 12px 2px 0; border: none; font-family: 'Segoe UI', sans-serif; }").ConfigureAwait(false);
        await w.WriteLineAsync("</style></head><body>").ConfigureAwait(false);
        await w.WriteLineAsync($"<h1>{h1Content}</h1>").ConfigureAwait(false);
        await w.WriteLineAsync($"<p class=\"summary\">{summary}</p>").ConfigureAwait(false);
    }

    private static async Task WriteFileGroupAsync(TextWriter w, FileMatchGroup group)
    {
        var escapedPath = WebUtility.HtmlEncode(group.FilePath);
        var escapedName = WebUtility.HtmlEncode(group.FileName);
        var matchLabel = group.Results.Count == 1 ? "1 match" : $"{group.Results.Count:N0} matches";

        await w.WriteLineAsync("<details class=\"file-group\" open>").ConfigureAwait(false);
        await w.WriteLineAsync($"<summary class=\"file-summary\"><span class=\"chevron\" aria-hidden=\"true\"></span><span class=\"file-name\">{escapedName}</span><span class=\"match-count\">{matchLabel}</span></summary>").ConfigureAwait(false);
        await w.WriteLineAsync($"<div class=\"file-path\">{escapedPath}</div>").ConfigureAwait(false);
        await w.WriteLineAsync("<table>").ConfigureAwait(false);

        foreach (var result in group.Results)
        {
            int ctxBeforeStart = result.LineNumber - result.ContextBefore.Count;
            for (int i = 0; i < result.ContextBefore.Count; i++)
            {
                if (ReportExportService.IsGeneratedPreviewNoticeLine(result.ContextBefore[i]))
                    continue;

                int ln = ctxBeforeStart + i;
                await w.WriteLineAsync($"<tr class=\"ctx\"><td class=\"ln\">{ln}</td><td>{WebUtility.HtmlEncode(result.ContextBefore[i])}</td></tr>").ConfigureAwait(false);
            }

            string matchHtml = BuildHighlightedMatchHtml(result.MatchLine, result.MatchStartColumn, result.MatchLength);
            await w.WriteLineAsync($"<tr class=\"match\"><td class=\"ln\">{result.LineNumber}</td><td>{matchHtml}</td></tr>").ConfigureAwait(false);

            // A cross-line (multiline) match spans start..end; show a marker row and number the
            // after-context from the END line (single-line results leave MatchEndLineNumber null).
            if (result.MatchEndLineNumber is int endLine && endLine > result.LineNumber)
            {
                int extra = endLine - result.LineNumber;
                string marker = $"\u2026 (+{extra} line{(extra == 1 ? "" : "s")})";
                await w.WriteLineAsync($"<tr class=\"ctx\"><td class=\"ln\"></td><td>{WebUtility.HtmlEncode(marker)}</td></tr>").ConfigureAwait(false);
            }

            int afterStart = (result.MatchEndLineNumber ?? result.LineNumber) + 1;
            for (int i = 0; i < result.ContextAfter.Count; i++)
            {
                if (ReportExportService.IsGeneratedPreviewNoticeLine(result.ContextAfter[i]))
                    continue;

                int ln = afterStart + i;
                await w.WriteLineAsync($"<tr class=\"ctx\"><td class=\"ln\">{ln}</td><td>{WebUtility.HtmlEncode(result.ContextAfter[i])}</td></tr>").ConfigureAwait(false);
            }
        }

        await w.WriteLineAsync("</table>").ConfigureAwait(false);
        await w.WriteLineAsync("</details>").ConfigureAwait(false);
    }

    private static List<FileMatchGroup> GetExportableGroups(IReadOnlyList<FileMatchGroup> groups)
    {
        var exportGroups = new List<FileMatchGroup>(groups.Count);
        foreach (var group in groups)
        {
            var exportGroup = CreateExportableGroup(group);
            if (exportGroup.Results.Count > 0)
                exportGroups.Add(exportGroup);
        }
        return exportGroups;
    }

    private static FileMatchGroup CreateExportableGroup(FileMatchGroup group)
    {
        var results = new List<SearchResult>(group.Results.Count);
        foreach (var result in group.Results)
        {
            if (!ReportExportService.IsGeneratedPreviewNoticeLine(result.MatchLine))
                results.Add(result);
        }
        return group with { Results = results };
    }
}
