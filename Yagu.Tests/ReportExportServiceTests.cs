using System.Text.Json;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ReportExportServiceTests : IDisposable
{
    private readonly string _root;
    private const string PreviewNotice = "\u26A0 Showing first 121 of 558 matches. Click \u2193 (Next match) to load more, or open in editor to browse all.";

    public ReportExportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-report-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteMultiFileReportAsync_RendersCollapsibleFileSections()
    {
        var filePath = Path.Combine(_root, "sample.txt");
        var result = new SearchResult(
            FilePath: filePath,
            LineNumber: 2,
            MatchLine: "needle here",
            MatchStartColumn: 0,
            MatchLength: 6,
            ContextBefore: ["before"],
            ContextAfter: ["after"]);
        var group = new HtmlReportExportService.FileMatchGroup(filePath, Path.GetFileName(filePath), [result]);

        using var writer = new StringWriter();
        await HtmlReportExportService.WriteMultiFileReportAsync(
            writer,
            "needle",
            [group],
            new HtmlReportExportService.SearchStats(DateTime.UtcNow, TimeSpan.FromSeconds(1), 1, 0));

        var html = writer.ToString();

        Assert.Contains("<details class=\"file-group\" open>", html);
        Assert.Contains("<summary class=\"file-summary\">", html);
        Assert.Contains("<span class=\"chevron\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("<span class=\"file-name\">sample.txt</span>", html);
        Assert.Contains("<span class=\"match-count\">1 match</span>", html);
        Assert.Contains("</details>", html);
    }

    [Fact]
    public async Task ReportWriters_OmitGeneratedPreviewPaginationNotices()
    {
        var filePath = Path.Combine(_root, "sample.txt");
        var normal = new SearchResult(
            FilePath: filePath,
            LineNumber: 4,
            MatchLine: "needle here",
            MatchStartColumn: 0,
            MatchLength: 6,
            ContextBefore: [PreviewNotice, "safe before"],
            ContextAfter: ["Click \u2193 (Next match) to load more, or open in editor to browse all.", "safe after"]);
        var generatedNotice = new SearchResult(
            FilePath: filePath,
            LineNumber: 8,
            MatchLine: PreviewNotice,
            MatchStartColumn: 2,
            MatchLength: 7,
            ContextBefore: [],
            ContextAfter: []);
        var group = new HtmlReportExportService.FileMatchGroup(filePath, Path.GetFileName(filePath), [normal, generatedNotice]);
        var stats = new HtmlReportExportService.SearchStats(DateTime.UtcNow, TimeSpan.FromSeconds(1), 1, 0);

        using var htmlWriter = new StringWriter();
        await HtmlReportExportService.WriteMultiFileReportAsync(htmlWriter, "needle", [group], stats);
        var html = htmlWriter.ToString();
        Assert.DoesNotContain("Showing first", html);
        Assert.DoesNotContain("Next match", html);
        Assert.Contains("<mark>needle</mark> here", html);
        Assert.Contains("safe before", html);
        Assert.Contains("safe after", html);
        Assert.Contains("1 match(es)", html);

        using var jsonWriter = new StringWriter();
        await ReportExportService.WriteJsonReportAsync(
            jsonWriter,
            "needle",
            [group],
            stats,
            new ReportExportOptions { Format = ReportFormat.Json, IncludeContextLines = true, ContextLineCount = 3, IncludeMatchMarkers = false });
        var json = jsonWriter.ToString();
        Assert.DoesNotContain("Showing first", json);
        Assert.DoesNotContain("Next match", json);
        using (var document = JsonDocument.Parse(json))
        {
            var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Single(results);
            Assert.Equal("needle here", results[0].GetProperty("matchLine").GetString());
        }

        using var csvWriter = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(
            csvWriter,
            "needle",
            [group],
            new ReportExportOptions { Format = ReportFormat.Csv, IncludeContextLines = true, ContextLineCount = 3, CsvUsePipeSeparator = true, IncludeMatchMarkers = false });
        var csv = csvWriter.ToString();
        Assert.DoesNotContain("Showing first", csv);
        Assert.DoesNotContain("Next match", csv);
        Assert.Contains("needle here", csv);
        Assert.Contains("safe before", csv);
        Assert.Contains("safe after", csv);
    }

    [Fact]
    public async Task WriteJsonReportAsync_FillsMissingContextFromSourceFile()
    {
        var filePath = Path.Combine(_root, "sample.txt");
        await File.WriteAllTextAsync(filePath, string.Join(Environment.NewLine,
        [
            "header",
            "before one",
            "before two",
            "needle here",
            "after one",
            "after two",
            "footer",
        ]));

        var result = new SearchResult(
            FilePath: filePath,
            LineNumber: 4,
            MatchLine: "needle here",
            MatchStartColumn: 0,
            MatchLength: 6,
            ContextBefore: Array.Empty<string>(),
            ContextAfter: Array.Empty<string>());
        var group = new HtmlReportExportService.FileMatchGroup(filePath, Path.GetFileName(filePath), [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteJsonReportAsync(
            writer,
            "needle",
            [group],
            new HtmlReportExportService.SearchStats(DateTime.UtcNow, TimeSpan.FromSeconds(1), 1, 0),
            new ReportExportOptions
            {
                Format = ReportFormat.Json,
                IncludeContextLines = true,
                ContextLineCount = 2,
                IncludeMatchMarkers = false,
            });

        using var document = JsonDocument.Parse(writer.ToString());
        var exported = document.RootElement.GetProperty("results")[0];

        Assert.Equal(["before one", "before two"], ReadStringArray(exported.GetProperty("contextBefore")));
        Assert.Equal(["after one", "after two"], ReadStringArray(exported.GetProperty("contextAfter")));
    }

    [Fact]
    public async Task WriteJsonReportAsync_TrimsBeforeContextToNearestLines()
    {
        var result = new SearchResult(
            FilePath: Path.Combine(_root, "missing.txt"),
            LineNumber: 10,
            MatchLine: "needle here",
            MatchStartColumn: 0,
            MatchLength: 6,
            ContextBefore: ["far before", "near before one", "near before two"],
            ContextAfter: ["near after one", "near after two", "far after"]);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, Path.GetFileName(result.FilePath), [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteJsonReportAsync(
            writer,
            "needle",
            [group],
            new HtmlReportExportService.SearchStats(DateTime.UtcNow, TimeSpan.FromSeconds(1), 1, 0),
            new ReportExportOptions
            {
                Format = ReportFormat.Json,
                IncludeContextLines = true,
                ContextLineCount = 2,
                IncludeMatchMarkers = false,
            });

        using var document = JsonDocument.Parse(writer.ToString());
        var exported = document.RootElement.GetProperty("results")[0];

        Assert.Equal(["near before one", "near before two"], ReadStringArray(exported.GetProperty("contextBefore")));
        Assert.Equal(["near after one", "near after two"], ReadStringArray(exported.GetProperty("contextAfter")));
    }

    private static string[] ReadStringArray(JsonElement element)
        => element.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
}