using System;
using System.IO;
using System.Text.RegularExpressions;
using Yagu.Models;
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
/// </summary>
public sealed class SemanticRegexTranslationTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

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

    // ---- Prompt contract -----------------------------------------------------

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
    }

    private static string ReadSystemPrompt()
    {
        string path = Path.Combine(FindRepoRoot(), "Yagu", "Services", "Ai", "Prompts", "SemanticSearchSystemPrompt.txt");
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
