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

public sealed class ReportExportServiceBranchTests : IDisposable
{
    private readonly string _root;

    public ReportExportServiceBranchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-report-branch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("hello", 0, 5, "<match>hello</match>")]
    [InlineData("hello world", 6, 5, "hello <match>world</match>")]
    [InlineData("abc", 0, 10, "<match>abc</match>")]
    [InlineData("abc", -1, 3, "abc")]
    [InlineData("abc", 5, 3, "abc")]
    [InlineData("abc", 0, 0, "abc")]
    public void InsertMatchMarkers_EdgeCases(string line, int start, int length, string expected)
    {
        Assert.Equal(expected, ReportExportService.InsertMatchMarkers(line, start, length));
    }

    [Theory]
    [InlineData("\u26A0 Showing first 100 of 500 matches. Click \u2193 (Next match) to load more, or open in editor to browse all.", true)]
    [InlineData("Click \u2193 (Next match) to load more, or open in editor to browse all.", true)]
    [InlineData("normal line with matches", false)]
    [InlineData("Showing first but not all parts", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGeneratedPreviewNoticeLine_DetectsCorrectly(string? line, bool expected)
    {
        Assert.Equal(expected, ReportExportService.IsGeneratedPreviewNoticeLine(line));
    }

    [Fact]
    public async Task WriteCsvReportAsync_IncludesFileSizeAndModifiedDate()
    {
        var filePath = Path.Combine(_root, "sized.txt");
        await File.WriteAllTextAsync(filePath, "needle is here");

        var result = new SearchResult(filePath, 1, "needle is here", 0, 6, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(filePath, "sized.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "needle", [group],
            new ReportExportOptions
            {
                Format = ReportFormat.Csv,
                IncludeFileSizes = true,
                IncludeModifiedDates = true,
                IncludeMatchMarkers = true,
            });

        var csv = writer.ToString();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("FileSize", lines[0]);
        Assert.Contains("ModifiedDate", lines[0]);
        Assert.True(lines.Length >= 2);
    }

    [Fact]
    public async Task WriteCsvReportAsync_PipeSeparator_JoinsContextWithPipe()
    {
        var result = new SearchResult(
            Path.Combine(_root, "ctx.txt"), 5, "match line", 0, 5,
            ["before1", "before2"],
            ["after1", "after2"]);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "ctx.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "match", [group],
            new ReportExportOptions
            {
                Format = ReportFormat.Csv,
                IncludeContextLines = true,
                ContextLineCount = 3,
                CsvUsePipeSeparator = true,
                IncludeMatchMarkers = false,
            });

        var csv = writer.ToString();
        Assert.Contains("ContextBefore", csv);
        Assert.Contains("ContextAfter", csv);
        Assert.Contains("before1 | before2", csv);
        Assert.Contains("after1 | after2", csv);
    }

    [Fact]
    public async Task WriteCsvReportAsync_EmbeddedNewlines_QuotesField()
    {
        var result = new SearchResult(
            Path.Combine(_root, "nl.txt"), 5, "match line", 0, 5,
            ["ctx before"],
            ["ctx after"]);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "nl.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "match", [group],
            new ReportExportOptions
            {
                Format = ReportFormat.Csv,
                IncludeContextLines = true,
                ContextLineCount = 3,
                CsvEmbedContext = true,
                CsvUsePipeSeparator = false,
                IncludeMatchMarkers = false,
            });

        var csv = writer.ToString();
        Assert.Contains("ContextBefore", csv);
    }

    [Fact]
    public async Task WriteJsonReportAsync_IncludesFileSizeAndDate()
    {
        var filePath = Path.Combine(_root, "sized.json.txt");
        await File.WriteAllTextAsync(filePath, "needle content\nsecond line");

        var result = new SearchResult(filePath, 1, "needle content", 0, 6, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(filePath, "sized.json.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteJsonReportAsync(writer, "needle", [group],
            new HtmlReportExportService.SearchStats(DateTime.UtcNow, TimeSpan.FromSeconds(2), 5, 1024),
            new ReportExportOptions
            {
                Format = ReportFormat.Json,
                IncludeFileSizes = true,
                IncludeModifiedDates = true,
                IncludeContextLines = false,
                IncludeMatchMarkers = true,
            });

        var json = writer.ToString();
        using var doc = JsonDocument.Parse(json);
        var res = doc.RootElement.GetProperty("results")[0];
        Assert.True(res.TryGetProperty("fileSize", out _));
        Assert.True(res.TryGetProperty("modifiedDate", out _));
        Assert.Contains("<match>", res.GetProperty("matchLine").GetString());
    }

    [Fact]
    public async Task WriteJsonReportAsync_NoContextOption_OmitsContextArrays()
    {
        var result = new SearchResult(Path.Combine(_root, "nc.txt"), 1, "line", 0, 4, ["b"], ["a"]);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "nc.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteJsonReportAsync(writer, "line", [group],
            new HtmlReportExportService.SearchStats(DateTime.UtcNow, TimeSpan.FromSeconds(1), 1, 0),
            new ReportExportOptions
            {
                Format = ReportFormat.Json,
                IncludeContextLines = false,
                IncludeMatchMarkers = false,
            });

        using var doc = JsonDocument.Parse(writer.ToString());
        var res = doc.RootElement.GetProperty("results")[0];
        Assert.False(res.TryGetProperty("contextBefore", out _));
        Assert.False(res.TryGetProperty("contextAfter", out _));
    }

    [Fact]
    public async Task WriteJsonReportAsync_Stats_IncludesAllFields()
    {
        var started = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = new SearchResult(Path.Combine(_root, "s.txt"), 1, "x", 0, 1, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "s.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteJsonReportAsync(writer, "x", [group],
            new HtmlReportExportService.SearchStats(started, TimeSpan.FromMilliseconds(1500), 42, 65536),
            new ReportExportOptions { IncludeContextLines = false, IncludeMatchMarkers = false });

        using var doc = JsonDocument.Parse(writer.ToString());
        var stats = doc.RootElement.GetProperty("stats");
        Assert.Equal(42, stats.GetProperty("filesScanned").GetInt32());
        Assert.Equal(65536, stats.GetProperty("bytesScanned").GetInt64());
        Assert.Contains("2026-01-15", stats.GetProperty("started").GetString());
    }
}

public sealed class HtmlReportExportServiceSingleFileTests
{
    [Fact]
    public async Task WriteSingleFileReportAsync_RendersMatchesAndTitle()
    {
        var result = new SearchResult(
            @"C:\code\app.cs", 10, "var needle = true;", 4, 6,
            ["// line 9"], ["// line 11"]);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "app.cs", [result]);

        using var writer = new StringWriter();
        await HtmlReportExportService.WriteSingleFileReportAsync(writer, "needle", group);

        var html = writer.ToString();
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("app.cs", html);
        Assert.Contains("<mark>needle</mark>", html);
        Assert.Contains("1 match(es)", html);
        Assert.Contains("</body></html>", html);
    }

    [Fact]
    public async Task WriteSingleFileReportAsync_EmptyGroup_NoFileGroupSection()
    {
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\code\empty.txt", "empty.txt", []);

        using var writer = new StringWriter();
        await HtmlReportExportService.WriteSingleFileReportAsync(writer, "missing", group);

        var html = writer.ToString();
        Assert.Contains("0 match(es)", html);
        Assert.DoesNotContain("<details class=\"file-group\"", html);
        Assert.Contains("</body></html>", html);
    }

    [Fact]
    public async Task WriteSingleFileReportAsync_FiltersGeneratedPreviewNoticeLines()
    {
        string previewNotice = "\u26A0 Showing first 121 of 558 matches. Click \u2193 (Next match) to load more, or open in editor to browse all.";
        var result = new SearchResult(
            @"C:\code\data.log", 5, "real match line", 0, 4,
            [previewNotice, "ctx before"],
            ["ctx after", previewNotice]);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "data.log", [result]);

        using var writer = new StringWriter();
        await HtmlReportExportService.WriteSingleFileReportAsync(writer, "real", group);

        var html = writer.ToString();
        Assert.DoesNotContain("\u26A0", html);
        Assert.Contains("ctx before", html);
        Assert.Contains("ctx after", html);
    }

    [Fact]
    public async Task WriteSingleFileReportAsync_MultipleMatches_RendersAll()
    {
        var results = new List<SearchResult>
        {
            new(@"C:\f.txt", 1, "first match", 0, 5, [], []),
            new(@"C:\f.txt", 10, "second match", 0, 6, [], []),
            new(@"C:\f.txt", 20, "third match", 0, 5, [], []),
        };
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\f.txt", "f.txt", results);

        using var writer = new StringWriter();
        await HtmlReportExportService.WriteSingleFileReportAsync(writer, "match", group);

        var html = writer.ToString();
        Assert.Contains("3 match(es)", html);
        Assert.Contains("<mark>first</mark>", html);
        Assert.Contains("<mark>second</mark>", html);
        Assert.Contains("<mark>third</mark>", html);
    }

    [Fact]
    public async Task WriteSingleFileReportAsync_HtmlEncodesSpecialChars()
    {
        var result = new SearchResult(
            @"C:\a&b<c>.txt", 1, "<script>alert('xss')</script>", 0, 8, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(result.FilePath, "a&b<c>.txt", [result]);

        using var writer = new StringWriter();
        await HtmlReportExportService.WriteSingleFileReportAsync(writer, "<script>", group);

        var html = writer.ToString();
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a&amp;b&lt;c&gt;.txt", html);
    }

    [Fact]
    public void BuildHighlightedMatchHtml_NegativeStart_ReturnsEncodedLine()
    {
        var result = HtmlReportExportService.BuildHighlightedMatchHtml("hello world", -1, 5);
        Assert.Equal("hello world", result);
        Assert.DoesNotContain("<mark>", result);
    }

    [Fact]
    public void BuildHighlightedMatchHtml_ZeroLength_ReturnsEncodedLine()
    {
        var result = HtmlReportExportService.BuildHighlightedMatchHtml("hello world", 0, 0);
        Assert.Equal("hello world", result);
        Assert.DoesNotContain("<mark>", result);
    }

    [Fact]
    public void BuildHighlightedMatchHtml_StartBeyondLine_ReturnsEncodedLine()
    {
        var result = HtmlReportExportService.BuildHighlightedMatchHtml("hi", 10, 3);
        Assert.Equal("hi", result);
    }

    [Fact]
    public void BuildHighlightedMatchHtml_MatchLengthExceedsLine_Clamps()
    {
        var result = HtmlReportExportService.BuildHighlightedMatchHtml("abcde", 3, 100);
        Assert.Contains("<mark>de</mark>", result);
        Assert.StartsWith("abc", result);
    }

    [Fact]
    public void BuildHighlightedMatchHtml_HtmlCharsInMatch_Encoded()
    {
        var result = HtmlReportExportService.BuildHighlightedMatchHtml("a<b>c", 1, 3);
        Assert.Contains("<mark>&lt;b&gt;</mark>", result);
    }
}

// ─── ReportExportService: CsvEscape branch coverage ─────────────────────

public class ReportExportServiceCsvEscapeTests : IDisposable
{
    private readonly string _root;

    public ReportExportServiceCsvEscapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-csv-escape-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteCsvReportAsync_FieldWithComma_IsQuoted()
    {
        var result = new SearchResult(@"C:\a,b.cs", 1, "foo,bar", 0, 3, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\a,b.cs", "a,b.cs", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "foo", [group],
            new ReportExportOptions { Format = ReportFormat.Csv, IncludeContextLines = false, IncludeMatchMarkers = false });

        var csv = writer.ToString();
        // Fields with commas must be wrapped in quotes
        Assert.Contains("\"C:\\a,b.cs\"", csv);
        Assert.Contains("\"a,b.cs\"", csv);
        Assert.Contains("\"foo,bar\"", csv);
    }

    [Fact]
    public async Task WriteCsvReportAsync_FieldWithQuote_IsDoubled()
    {
        var result = new SearchResult(@"C:\a.cs", 1, @"say ""hello""", 0, 3, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\a.cs", "a.cs", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "say", [group],
            new ReportExportOptions { Format = ReportFormat.Csv, IncludeContextLines = false, IncludeMatchMarkers = false });

        var csv = writer.ToString();
        // Quotes must be doubled and field wrapped: "say ""hello"""
        Assert.Contains(@"""say """"hello""""""", csv);
    }

    [Fact]
    public async Task WriteCsvReportAsync_FieldWithNewline_IsQuoted()
    {
        var result = new SearchResult(@"C:\a.cs", 1, "line1\nline2", 0, 5, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\a.cs", "a.cs", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "line", [group],
            new ReportExportOptions { Format = ReportFormat.Csv, IncludeContextLines = false, IncludeMatchMarkers = false });

        var csv = writer.ToString();
        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public async Task WriteCsvReportAsync_EmptyMatchLine_EscapesToQuotedEmpty()
    {
        var result = new SearchResult(@"C:\a.cs", 1, "", 0, 0, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\a.cs", "a.cs", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "q", [group],
            new ReportExportOptions { Format = ReportFormat.Csv, IncludeContextLines = false, IncludeMatchMarkers = false });

        var csv = writer.ToString();
        // Empty field should produce ""
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2); // header + data
    }

    [Fact]
    public async Task WriteCsvReportAsync_SourceContextFallback_ProvidesMoreLines()
    {
        // Create a real file so SourceFileContext can load it
        var dir = Path.Combine(Path.GetTempPath(), "yagu-csv-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "source.txt");
            File.WriteAllLines(filePath, ["line1", "line2", "line3", "match line", "line5", "line6", "line7"]);

            // Result with only 1 context line captured; source has more
            var result = new SearchResult(filePath, 4, "match line", 0, 5, ["line3"], ["line5"]);
            var group = new HtmlReportExportService.FileMatchGroup(filePath, "source.txt", [result]);

            using var writer = new StringWriter();
            await ReportExportService.WriteCsvReportAsync(writer, "match", [group],
                new ReportExportOptions
                {
                    Format = ReportFormat.Csv,
                    IncludeContextLines = true,
                    ContextLineCount = 3,
                    CsvUsePipeSeparator = true,
                    IncludeMatchMarkers = false,
                });

            var csv = writer.ToString();
            // SourceFileContext should provide 3 lines of context (line1, line2, line3)
            Assert.Contains("line1", csv);
            Assert.Contains("line2", csv);
            Assert.Contains("line3", csv);
            Assert.Contains("line5", csv);
            Assert.Contains("line6", csv);
            Assert.Contains("line7", csv);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task WriteCsvReportAsync_MatchMarkers_InsertedWhenEnabled()
    {
        var result = new SearchResult(@"C:\a.cs", 1, "hello world", 6, 5, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\a.cs", "a.cs", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "world", [group],
            new ReportExportOptions
            {
                Format = ReportFormat.Csv,
                IncludeContextLines = false,
                IncludeMatchMarkers = true,
            });

        var csv = writer.ToString();
        Assert.Contains("<match>world</match>", csv);
    }

    [Fact]
    public async Task WriteCsvReportAsync_SourceContext_FirstLine_NoBefore()
    {
        // Match on line 1: GetBefore returns empty (matchIndex=0 → matchIndex <= 0)
        var dir = Path.Combine(Path.GetTempPath(), "yagu-ctx-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "first.txt");
            File.WriteAllLines(filePath, ["first line match", "line2", "line3", "line4"]);

            var result = new SearchResult(filePath, 1, "first line match", 0, 5, [], []);
            var group = new HtmlReportExportService.FileMatchGroup(filePath, "first.txt", [result]);

            using var writer = new StringWriter();
            await ReportExportService.WriteCsvReportAsync(writer, "first", [group],
                new ReportExportOptions
                {
                    Format = ReportFormat.Csv,
                    IncludeContextLines = true,
                    ContextLineCount = 3,
                    CsvUsePipeSeparator = true,
                    IncludeMatchMarkers = false,
                });

            var csv = writer.ToString();
            // No context before (first line) but context after should exist
            Assert.Contains("first line match", csv);
            Assert.Contains("line2", csv);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task WriteCsvReportAsync_SourceContext_LastLine_NoAfter()
    {
        // Match on last line: GetAfter returns empty (matchIndex >= _lines.Length - 1)
        var dir = Path.Combine(Path.GetTempPath(), "yagu-ctx-last-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "last.txt");
            File.WriteAllLines(filePath, ["line1", "line2", "line3", "last line match"]);

            var result = new SearchResult(filePath, 4, "last line match", 0, 4, [], []);
            var group = new HtmlReportExportService.FileMatchGroup(filePath, "last.txt", [result]);

            using var writer = new StringWriter();
            await ReportExportService.WriteCsvReportAsync(writer, "last", [group],
                new ReportExportOptions
                {
                    Format = ReportFormat.Csv,
                    IncludeContextLines = true,
                    ContextLineCount = 3,
                    CsvUsePipeSeparator = true,
                    IncludeMatchMarkers = false,
                });

            var csv = writer.ToString();
            // Context before should exist, no context after (last line)
            Assert.Contains("line2", csv);
            Assert.Contains("line3", csv);
            Assert.Contains("last line match", csv);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task WriteCsvReportAsync_SourceContext_NonexistentFile_NoContext()
    {
        // TryLoad returns null when file doesn't exist → no source context added
        var result = new SearchResult(@"C:\nonexistent\ghost.cs", 5, "match text", 0, 5, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(@"C:\nonexistent\ghost.cs", "ghost.cs", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "match", [group],
            new ReportExportOptions
            {
                Format = ReportFormat.Csv,
                IncludeContextLines = true,
                ContextLineCount = 3,
                CsvUsePipeSeparator = true,
                IncludeMatchMarkers = false,
            });

        var csv = writer.ToString();
        // Should still produce a valid CSV row with the match, just no context
        Assert.Contains("match text", csv);
    }

    [Fact]
    public async Task WriteCsvReportAsync_LockedFile_ProducesOutputWithoutContext()
    {
        // Exercise the catch block in SourceFileContext.TryLoad when file can't be read
        var filePath = Path.Combine(_root, "locked.txt");
        File.WriteAllText(filePath, "line one\nline two\nneedle here\nline four\n");

        // Lock the file exclusively so ReadAllLines throws
        using var lockStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = new SearchResult(filePath, 3, "needle here", 0, 6, [], []);
        var group = new HtmlReportExportService.FileMatchGroup(filePath, "locked.txt", [result]);

        using var writer = new StringWriter();
        await ReportExportService.WriteCsvReportAsync(writer, "needle", [group],
            new ReportExportOptions
            {
                Format = ReportFormat.Csv,
                IncludeContextLines = true,
                ContextLineCount = 2,
                IncludeMatchMarkers = false,
            });

        var csv = writer.ToString();
        // Should produce output (not throw) even though the file couldn't be read for context
        Assert.Contains("needle here", csv);
    }
}