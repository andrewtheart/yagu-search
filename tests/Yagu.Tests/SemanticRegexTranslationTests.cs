using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the semantic layer's regex-generation capability. Two concerns are covered:
/// <list type="number">
///   <item>Applier passthrough — a model-produced plan whose <c>pattern</c> is a regular
///   expression (with <c>useRegex:true</c>) is carried faithfully through
///   <see cref="SemanticPlanApplier.Resolve"/>, <see cref="SemanticPlanApplier.ToOverlay"/>,
///   and <see cref="SemanticPlanApplier.ApplyToTarget(ResolvedSearchPlan, ISemanticPlanTarget)"/>
///   to both the UI target and the CLI overlay, without being mangled or dropped.</item>
///   <item>Prompt contract — the canonical regexes the system prompt teaches the model to emit
///   are (a) present in the prompt, (b) valid .NET regular expressions, (c) free of constructs
///   the Rust <c>regex</c> crate rejects (lookaround / backreferences), and (d) actually match
///   the sample inputs they are meant to match while rejecting clear non-matches.</item>
/// </list>
/// An end-to-end case (<see cref="DigitRangeFilenameQuery_TranslatesAndMatchesOnlyDigit1To5Files"/>)
/// drives the documented example "all files that have the numbers 1 through 5 in them" through the
/// full production pipeline — raw model JSON → <see cref="SemanticPlanJsonExtractor"/> →
/// <see cref="SemanticPlanApplier"/> → <see cref="SearchOptions"/> → <see cref="SearchService"/> —
/// against synthetic directories and files, asserting only files whose names contain a digit 1-5 match.
/// </summary>
public sealed class SemanticRegexTranslationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public SemanticRegexTranslationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-semantic-regex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private void WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static SemanticTranslationContext Context(string? defaultDir = null) =>
        new() { Now = Now, DefaultDirectory = defaultDir };

    private sealed class FakeTarget : ISemanticPlanTarget
    {
        public string Directory { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public int SearchModeIndex { get; set; }
        public bool CaseSensitive { get; set; }
        public bool UseRegex { get; set; }
        public bool ExactMatch { get; set; } = true;
        public string IncludeGlobs { get; set; } = string.Empty;
        public string ExcludeGlobs { get; set; } = string.Empty;
        public int IncludeFilterModeIndex { get; set; }
        public int ExcludeFilterModeIndex { get; set; }
        public long MinFileSizeBytes { get; set; }
        public long MaxFileSizeBytes { get; set; }
        public DateTimeOffset? CreatedAfterDate { get; set; }
        public DateTimeOffset? CreatedBeforeDate { get; set; }
        public DateTimeOffset? ModifiedAfterDate { get; set; }
        public DateTimeOffset? ModifiedBeforeDate { get; set; }
        public double MaxSearchDepth { get; set; }
        public bool ObeyGitignore { get; set; }
        public bool SearchInsideArchives { get; set; }
        public bool SearchHiddenFiles { get; set; } = true;
        public bool SearchImageText { get; set; }
        public int SortModeIndex { get; set; }
        public int SortDirectionIndex { get; set; }
        public int GroupModeIndex { get; set; }
        public int GroupSortDirectionIndex { get; set; }
    }

    // ---- Applier passthrough -------------------------------------------------

    [Theory]
    [InlineData(@"\d{3}-\d{4}")]
    [InlineData(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    [InlineData(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    [InlineData(@"^ERROR")]
    public void Resolve_RegexPattern_PreservedVerbatimWithUseRegexTrue(string regex)
    {
        var plan = new SemanticSearchPlan { Pattern = regex, UseRegex = true, SearchMode = "content" };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(regex, resolved.Pattern);
        Assert.True(resolved.UseRegex);
        Assert.Equal(SearchMode.Content, resolved.SearchMode);
    }

    [Fact]
    public void ToOverlay_RegexPattern_CarriesPatternAndUseRegex()
    {
        var plan = new SemanticSearchPlan { Pattern = @"\b\d{3}-\d{3}-\d{4}\b", UseRegex = true, SearchMode = "content" };

        var overlay = SemanticPlanApplier.ToOverlay(SemanticPlanApplier.Resolve(plan, Context()));

        Assert.Equal(@"\b\d{3}-\d{3}-\d{4}\b", overlay.Query);
        Assert.True(overlay.UseRegex);
    }

    [Fact]
    public void ApplyToTarget_RegexPattern_SetsQueryAndUseRegexOnTarget()
    {
        var plan = new SemanticSearchPlan
        {
            Pattern = @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}",
            UseRegex = true,
            SearchMode = "content",
        };
        var target = new FakeTarget();

        SemanticPlanApplier.ApplyToTarget(plan, Context(), target);

        Assert.Equal(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", target.Query);
        Assert.True(target.UseRegex);
    }

    [Fact]
    public void Resolve_RegexWithSpecialChars_NotTreatedAsRedundantGlob()
    {
        // A real regex must never be dropped as a "redundant glob" even alongside include filters.
        var plan = new SemanticSearchPlan
        {
            Pattern = @"\d{3}-\d{4}",
            UseRegex = true,
            SearchMode = "content",
            IncludeGlobs = new() { "*.txt" },
        };

        var resolved = SemanticPlanApplier.Resolve(plan, Context());

        Assert.Equal(@"\d{3}-\d{4}", resolved.Pattern);
        Assert.True(resolved.UseRegex);
    }

    // ---- End-to-end: "numbers 1 through 5" filename search -------------------

    /// <summary>
    /// The documented example "all files that have the numbers 1 through 5 in them (e.g. 1, 2, 3, 4
    /// or 5)" must translate into a filename regex <c>[1-5]</c> and, when actually executed, match
    /// only files whose names contain one of those digits. This drives the full production pipeline
    /// (raw model JSON → extractor → applier → overlay → <see cref="SearchOptions"/> →
    /// <see cref="SearchService"/>) against synthetic directories and files. Non-matching files are
    /// seeded with digits 1-5 in their CONTENTS to prove the search matched on names, not contents.
    /// </summary>
    [Fact]
    public async Task DigitRangeFilenameQuery_TranslatesAndMatchesOnlyDigit1To5Files()
    {
        // What the model is taught to emit for this request (see SemanticSearchSystemPrompt Example 15).
        const string modelJson =
            "{\"pattern\":\"[1-5]\",\"searchMode\":\"filenames\",\"useRegex\":true," +
            "\"explanation\":\"Listing files whose names contain any digit from 1 to 5 using a regex.\"}";

        // 1) Production parse + normalization path.
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(modelJson, out var plan, out var parseError),
            $"Plan should parse. Error: {parseError}");
        var overlay = SemanticPlanApplier.ToOverlay(SemanticPlanApplier.Resolve(plan!, Context(_root)));

        Assert.Equal("[1-5]", overlay.Query);
        Assert.True(overlay.UseRegex);
        Assert.Equal(SearchMode.FileNames, overlay.SearchMode);

        // 2) Synthetic corpus. Names WITH a digit 1-5 should match; names WITHOUT must not — even
        //    though several non-matching files contain 1-5 in their CONTENTS (filenames mode ignores it).
        var expectedMatches = new[]
        {
            "report1.txt", "data-2.log", "notes3.md", "page4.csv", "summary5.txt",
            "v15-draft.txt", Path.Combine("archive", "old-3.bak"),
        };
        var nonMatches = new[]
        {
            "report.txt", "data-6.log", "notes7.md", "page8.csv", "summary9.txt",
            "zero0.txt", Path.Combine("archive", "old.bak"),
        };
        foreach (var rel in expectedMatches) WriteFile(rel, "no relevant digits in body");
        foreach (var rel in nonMatches) WriteFile(rel, "body mentions 1 2 3 4 5 but the name does not");

        // 3) Fold the overlay into concrete SearchOptions exactly as a caller would, then run a real search.
        var opts = new SearchOptions
        {
            Directory = overlay.Directory ?? _root,
            Query = overlay.Query!,
            UseRegex = overlay.UseRegex ?? false,
            SearchMode = overlay.SearchMode ?? SearchMode.Both,
            CaseSensitive = overlay.CaseSensitive ?? false,
            ExactMatch = overlay.ExactMatch ?? true,
            MaxFileSizeBytes = 0,
            MaxResults = 50_000,
            MaxMatchesPerFile = 0,
            SkipBinary = true,
        };

        var matchedNames = await RunSearchAsync(opts);

        var expectedNames = expectedMatches.Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(expectedNames, matchedNames.OrderBy(n => n).ToArray());

        // Every matched name really does contain a 1-5; none of the seeded non-matches slipped through.
        Assert.All(matchedNames, n => Assert.Matches("[1-5]", n));
        foreach (var rel in nonMatches)
            Assert.DoesNotContain(Path.GetFileName(rel), matchedNames);
    }

    /// <summary>
    /// "files with the word andrew in them at least two times" must translate into a content regex
    /// <c>andrew.*andrew</c> and, when executed, match only files that contain the word at least twice
    /// on a single line. Drives the full production pipeline (raw model JSON → extractor → applier →
    /// overlay → <see cref="SearchOptions"/> → <see cref="SearchService"/>) against synthetic files.
    /// Because content matching is line-scoped, a file with the two occurrences split across separate
    /// lines does NOT match — this test documents that engine behavior.
    /// </summary>
    [Fact]
    public async Task RepeatedWordQuery_TranslatesAndMatchesFilesWithWordAtLeastTwice()
    {
        // What the model is taught to emit for this request (see SemanticSearchSystemPrompt Example 16).
        const string modelJson =
            "{\"pattern\":\"andrew.*andrew\",\"searchMode\":\"content\",\"useRegex\":true," +
            "\"explanation\":\"Searching file contents for a line where the word andrew appears at least twice.\"}";

        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(modelJson, out var plan, out var parseError),
            $"Plan should parse. Error: {parseError}");
        var overlay = SemanticPlanApplier.ToOverlay(SemanticPlanApplier.Resolve(plan!, Context(_root)));

        Assert.Equal("andrew.*andrew", overlay.Query);
        Assert.True(overlay.UseRegex);
        Assert.Equal(SearchMode.Content, overlay.SearchMode);

        // Matches: two+ occurrences on one line. Non-matches: a single occurrence, none at all, or
        // two occurrences split across separate lines (content matching is line-scoped).
        WriteFile("twice-same-line.txt", "here is andrew and again andrew on one line");
        WriteFile("adjacent.txt", "andrewandrew stuck together");
        WriteFile("thrice.txt", "andrew andrew andrew three times");
        WriteFile(Path.Combine("sub", "nested-twice.md"), "the andrew met the other andrew");

        WriteFile("once.txt", "only andrew appears here a single time");
        WriteFile("none.txt", "no relevant word in this file at all");
        WriteFile("split-lines.txt", "andrew on the first line\nandrew on the second line");

        var opts = new SearchOptions
        {
            Directory = overlay.Directory ?? _root,
            Query = overlay.Query!,
            UseRegex = overlay.UseRegex ?? false,
            SearchMode = overlay.SearchMode ?? SearchMode.Both,
            CaseSensitive = overlay.CaseSensitive ?? false,
            ExactMatch = overlay.ExactMatch ?? true,
            MaxFileSizeBytes = 0,
            MaxResults = 50_000,
            MaxMatchesPerFile = 0,
            SkipBinary = true,
        };

        var matchedNames = await RunSearchAsync(opts);

        var expected = new[] { "twice-same-line.txt", "adjacent.txt", "thrice.txt", "nested-twice.md" }
            .OrderBy(n => n).ToArray();
        Assert.Equal(expected, matchedNames.OrderBy(n => n).ToArray());

        Assert.DoesNotContain("once.txt", matchedNames);
        Assert.DoesNotContain("none.txt", matchedNames);
        Assert.DoesNotContain("split-lines.txt", matchedNames);
    }

    private async Task<List<string>> RunSearchAsync(SearchOptions opts, CancellationToken ct = default)
    {
        var service = new SearchService();
        var names = new List<string>();
        await foreach (var evt in service.SearchAsync(opts, ct))
        {
            if (evt is SearchEvent.MatchBatch batch)
                names.AddRange(batch.Results.Select(r => Path.GetFileName(r.FilePath)));
            else if (evt is SearchEvent.Match match)
                names.Add(Path.GetFileName(match.Result.FilePath));
        }
        return names.Distinct().ToList();
    }

    public static TheoryData<string, string[], string[]> CanonicalRegexCases() => new()
    {
        // regex, positive samples, negative samples
        { @"[\w.+-]+@[\w-]+\.[\w.-]+",
          new[] { "jane.doe@example.com", "a+b@mail.co.uk", "user_1@sub.domain.org" },
          new[] { "not-an-email", "@nope.com", "plainword" } },
        { @"^ERROR",
          new[] { "ERROR something failed", "ERROR: disk full" },
          new[] { "WARN ERROR later", " info ERROR" } },
        { @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
          new[] { "192.168.0.1", "10.0.0.255", "ip=8.8.8.8 end" },
          new[] { "1234", "abc.def.ghi.jkl", "999" } },
        { @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}",
          new[] { "550e8400-e29b-41d4-a716-446655440000", "id 123e4567-e89b-12d3-a456-426614174000 here" },
          new[] { "550e8400e29b41d4a716446655440000", "not-a-guid", "1234-5678" } },
        { @"\b\d{3}-\d{3}-\d{4}\b",
          new[] { "555-123-4567", "call 800-555-0199 now" },
          new[] { "5551234567", "12-345-6789", "phone" } },
        { @"[1-5]",
          new[] { "report1.txt", "data-2.log", "notes3", "page4.csv", "summary5", "v15-draft" },
          new[] { "report.txt", "data-6.log", "notes7", "page8", "summary9", "zero0" } },
        { @"andrew.*andrew",
          new[] { "andrew met andrew", "andrewandrew", "x andrew y andrew z" },
          new[] { "andrew once", "no match here", "andre w" } },
    };

    [Theory]
    [MemberData(nameof(CanonicalRegexCases))]
    public void CanonicalRegex_IsValidDotNetAndMatchesFixtures(string regex, string[] positives, string[] negatives)
    {
        var compiled = new Regex(regex);

        foreach (var p in positives)
            Assert.True(compiled.IsMatch(p), $"Expected /{regex}/ to match \"{p}\".");
        foreach (var n in negatives)
            Assert.False(compiled.IsMatch(n), $"Expected /{regex}/ NOT to match \"{n}\".");
    }

    [Theory]
    [MemberData(nameof(CanonicalRegexCases))]
    public void CanonicalRegex_UsesNoConstructsRustRegexRejects(string regex, string[] positives, string[] negatives)
    {
        _ = positives;
        _ = negatives;

        // The Rust `regex` crate (used by the native scanner) rejects lookaround and backreferences.
        Assert.DoesNotContain("(?=", regex);
        Assert.DoesNotContain("(?!", regex);
        Assert.DoesNotContain("(?<=", regex);
        Assert.DoesNotContain("(?<!", regex);
        Assert.DoesNotMatch(@"\\[1-9]", regex);
    }

    [Fact]
    public void SystemPrompt_TeachesRegexGenerationAndCanonicalExamples()
    {
        string prompt = ReadSystemPrompt();

        // The capability + guardrails must be documented.
        Assert.Contains("PATTERN GENERATION (regex)", prompt);
        Assert.Contains("lookahead", prompt);
        Assert.Contains("lookbehind", prompt);
        Assert.Contains("backreference", prompt);
        Assert.Contains("\"useRegex\":true", prompt);

        // Canonical example regexes appear (JSON-escaped, i.e. each backslash doubled).
        Assert.Contains(@"[\\w.+-]+@[\\w-]+\\.[\\w.-]+", prompt);   // email
        Assert.Contains(@"^ERROR", prompt);                          // line-start
        Assert.Contains(@"\\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b", prompt); // IPv4

        // The "numbers 1 through 5" filename example is taught (no backslashes, so it appears verbatim).
        Assert.Contains("numbers 1 through 5", prompt);
        Assert.Contains(@"""pattern"":""[1-5]""", prompt);
        Assert.Contains(@"""searchMode"":""filenames""", prompt);

        // The "repeated word at least N times" example + guidance is taught (line-scoped repetition regex).
        Assert.Contains("REPETITION / COUNT", prompt);
        Assert.Contains("at least two times", prompt);
        Assert.Contains(@"""pattern"":""andrew.*andrew""", prompt);
    }

    private static string ReadSystemPrompt()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Ai", "Prompts", "SemanticSearchSystemPrompt.prompt.md");
        Assert.True(File.Exists(path), $"System prompt not found at {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
