using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="SemanticPlanJsonExtractor"/> — the pure extraction/repair of a
/// <see cref="SemanticSearchPlan"/> from raw model output. The local instruct models we target are
/// chatty: they wrap output in code fences, append prose, repeat the object, and — when they ramble
/// into the token limit — truncate the object before its closing brace. These tests lock in that the
/// first well-formed object always wins and that truncated output is repaired into a usable plan.
/// </summary>
public sealed class SemanticPlanJsonExtractorTests
{
    [Fact]
    public void ExtractJsonObject_CleanObject_ReturnedAsIs()
    {
        const string raw = "{\"directory\":\"C:\\\\\",\"pattern\":\"*.png\"}";
        Assert.Equal(raw, SemanticPlanJsonExtractor.ExtractJsonObject(raw));
    }

    [Fact]
    public void ExtractJsonObject_CodeFencedWithProse_ExtractsObject()
    {
        const string raw = "Here is the plan:\n```json\n{\"directory\":\"D:\\\\docs\"}\n```\nHope that helps!";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);
        Assert.Equal("{\"directory\":\"D:\\\\docs\"}", json);
    }

    [Fact]
    public void ExtractJsonObject_RepeatedObjects_FirstCompleteWins()
    {
        const string raw = "{\"pattern\":\"*.log\"}\n{\"pattern\":\"*.txt\"}\n{\"pattern\":\"*.md\"}";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);
        Assert.Equal("{\"pattern\":\"*.log\"}", json);
    }

    [Fact]
    public void ExtractJsonObject_BracesInsideStrings_AreIgnored()
    {
        const string raw = "{\"explanation\":\"matches {abc} not real braces\",\"pattern\":\"*.txt\"}";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);
        Assert.Equal(raw, json);
    }

    [Fact]
    public void ExtractJsonObject_NoObject_ReturnsNull()
    {
        Assert.Null(SemanticPlanJsonExtractor.ExtractJsonObject("I cannot help with that."));
        Assert.Null(SemanticPlanJsonExtractor.ExtractJsonObject(""));
        Assert.Null(SemanticPlanJsonExtractor.ExtractJsonObject(null));
    }

    [Fact]
    public void ExtractJsonObject_TruncatedBeforeClosingBrace_IsRepairedAndKeepsFields()
    {
        // phi cut the output one '}' short after a complete string value (the query-6 failure mode).
        const string raw = "{\"directory\":\"C:\\\\\",\"createdBefore\":\"2024-01-01\"," +
                           "\"excludeFileNames\":[\"thumbnails\"],\"explanation\":\"images before 2024\"";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.NotNull(plan);
        Assert.Equal("2024-01-01", plan!.CreatedBefore);
        Assert.Equal("images before 2024", plan.Explanation);
        Assert.Equal(new[] { "thumbnails" }, plan.ExcludeFileNames!);
    }

    [Fact]
    public void ExtractJsonObject_TruncatedInsideArray_IsRepairedByClosingBrackets()
    {
        const string raw = "{\"directory\":\"C:\\\\\",\"includeGlobs\":[\"*.png\",\"*.jpg\"";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.Equal(new[] { "*.png", "*.jpg" }, plan!.IncludeGlobs!);
    }

    [Fact]
    public void ExtractJsonObject_TruncatedMidPartialField_TrimsToLastCompleteField()
    {
        // Cut mid-key after a complete field: the partial trailing field must be dropped, keeping the
        // earlier valid fields rather than failing outright.
        const string raw = "{\"directory\":\"C:\\\\\",\"pattern\":\"*.png\",\"modifiedAf";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.Equal("C:\\", plan!.Directory);
        Assert.Equal("*.png", plan.Pattern);
    }

    [Fact]
    public void ExtractJsonObject_TruncatedMidStringValue_ClosesString()
    {
        const string raw = "{\"directory\":\"C:\\\\\",\"explanation\":\"searching for png files in the";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.Equal("C:\\", plan!.Directory);
    }

    [Fact]
    public void TryParsePlan_TrailingProseAfterObject_ParsesObject()
    {
        const string raw = "{\"pattern\":\"*.pdf\",\"minFileSizeBytes\":5242880}\n\nThis searches for PDFs.";
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(raw, out SemanticSearchPlan? plan, out _));
        Assert.Equal("*.pdf", plan!.Pattern);
        Assert.Equal(5242880, plan.MinFileSizeBytes);
    }

    [Fact]
    public void TryParsePlan_NoJson_Fails()
    {
        Assert.False(SemanticPlanJsonExtractor.TryParsePlan("sorry, no JSON here", out SemanticSearchPlan? plan, out string? error));
        Assert.Null(plan);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParsePlan_BalancedButInvalidJson_FailsWithError()
    {
        // The braces balance, so extraction returns the object, but the missing value makes
        // deserialization throw — the JsonException path must surface a friendly error.
        Assert.False(SemanticPlanJsonExtractor.TryParsePlan("{\"pattern\": }", out SemanticSearchPlan? plan, out string? error));
        Assert.Null(plan);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ExtractJsonObject_NestedObject_ReturnedWhole()
    {
        const string raw = "{\"a\":{\"b\":1}}";
        Assert.Equal(raw, SemanticPlanJsonExtractor.ExtractJsonObject(raw));
    }

    [Fact]
    public void ExtractJsonObject_UnbalancedClosingBeforeRootCloses_ReturnsNull()
    {
        // A stray ']' pops the open-bracket stack to empty without the root object ever closing, so
        // there is nothing to repair.
        Assert.Null(SemanticPlanJsonExtractor.ExtractJsonObject("{\"a\":1]"));
    }

    [Fact]
    public void ExtractJsonObject_ExtraCloserWithEmptyStack_IsToleratedAndReturnsNull()
    {
        // The second ']' is seen while the open-bracket stack is already empty; the guard must skip
        // it rather than throw, and the unrecoverable input yields null.
        Assert.Null(SemanticPlanJsonExtractor.ExtractJsonObject("{\"a\":]]"));
    }

    [Fact]
    public void ExtractJsonObject_UnrepairableTruncation_ReturnsNull()
    {
        // Neither closing at the cut point nor trimming to the top-level comma yields valid JSON.
        Assert.Null(SemanticPlanJsonExtractor.ExtractJsonObject("{\"a\":@,\"b\""));
    }

    [Fact]
    public void ExtractJsonObject_TruncatedWithTrailingComma_DropsCommaAndCloses()
    {
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject("{\"pattern\":\"*.png\",");

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.Equal("*.png", plan!.Pattern);
    }

    [Fact]
    public void ExtractJsonObject_TruncatedMidStringWithEscape_ClosesString()
    {
        // Cut inside a string that contains a backslash escape — the repair must respect the escape
        // when deciding the string is still open, then close it.
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject("{\"explanation\":\"line\\nbroken");

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.Equal("line\nbroken", plan!.Explanation);
    }

    [Fact]
    public void ExtractJsonObject_TruncatedAfterNestedObject_RepairsAndKeepsFields()
    {
        // A nested, complete sub-object means the repair scan must process a real '}' before the
        // outer object is truncated mid-string.
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(
            "{\"pattern\":\"*.png\",\"meta\":{\"k\":1},\"explanation\":\"searching");

        Assert.NotNull(json);
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(json!, out SemanticSearchPlan? plan, out _));
        Assert.Equal("*.png", plan!.Pattern);
    }

    [Fact]
    public void CloseAndValidate_HeadOfOnlyCommas_TrimsToEmptyAndFailsValidation()
    {
        // Defensive guard: when trimming trailing commas consumes the entire head, the loop must stop
        // at length 0 instead of indexing past the start, and the resulting "}" is not valid JSON.
        string? result = SemanticPlanJsonExtractor.CloseAndValidate(",", endsInString: false, openStack: ['{']);
        Assert.Null(result);
    }
}
