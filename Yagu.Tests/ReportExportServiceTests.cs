using System.Text.Json;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ReportExportServiceTests : IDisposable
{
    private readonly string _root;

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