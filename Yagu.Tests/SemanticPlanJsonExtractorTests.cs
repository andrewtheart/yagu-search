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
    public void TryParsePlan_BalancedButExplanationQuotedEarlyClose_DropsExplanationKeepsFields()
    {
        // phi-4-mini's "all word documents with Andrew in them" failure: it closed the explanation
        // string early (...for the term 'Andrew") and left a stray '.' before '}'. The object is
        // brace-balanced but invalid; the trailing-field repair must drop the broken explanation and
        // keep the structured fields so the search still runs.
        const string raw = "{\"directory\":\"\",\"pattern\":\"Andrew\",\"searchMode\":\"content\"," +
                           "\"includeGlobs\":[\"*.doc\",\"*.docx\",\"*.pdf\",\"*.txt\",\"*.rtf\",\"*.odt\"]," +
                           "\"explanation\":\"Searching the contents of word documents for the term 'Andrew\".}";
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(raw, out SemanticSearchPlan? plan, out string? error));
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal("Andrew", plan!.Pattern);
        Assert.Equal("content", plan.SearchMode);
        Assert.Equal(new[] { "*.doc", "*.docx", "*.pdf", "*.txt", "*.rtf", "*.odt" }, plan.IncludeGlobs!);
        Assert.True(string.IsNullOrEmpty(plan.Explanation)); // the malformed trailing field was dropped
    }

    [Fact]
    public void RepairBalancedObject_FirstFieldMalformed_ReturnsNull()
    {
        // No top-level comma precedes the break, so there is no earlier field to trim back to.
        Assert.Null(SemanticPlanJsonExtractor.RepairBalancedObject("{\"pattern\":\"a\"b\"}"));
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

    [Fact]
    public void TryParsePlan_ArithmeticInSizeField_FoldedToLiteralAndParses()
    {
        // The "all files greater than 100Mb in size" failure: phi-4-mini emitted an arithmetic
        // expression (1048576*100) in minFileSizeBytes, which is not valid JSON. The fold must turn
        // it into the literal product so the plan parses.
        const string raw = "{\"minFileSizeBytes\": 1048576*100,\"explanation\":\"Listing files greater than 100 MB in size.\"}";
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(raw, out SemanticSearchPlan? plan, out string? error));
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal(104857600, plan!.MinFileSizeBytes);
        Assert.Equal("Listing files greater than 100 MB in size.", plan.Explanation);
    }

    [Fact]
    public void TryParsePlan_MultiFactorArithmeticWithSpaces_FoldedToProduct()
    {
        // A three-factor chain with spaces (100 * 1024 * 1024) must also fold to its product.
        const string raw = "{\"maxFileSizeBytes\": 100 * 1024 * 1024,\"pattern\":\"*.bin\"}";
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(raw, out SemanticSearchPlan? plan, out _));
        Assert.Equal(104857600, plan!.MaxFileSizeBytes);
        Assert.Equal("*.bin", plan.Pattern);
    }

    [Fact]
    public void FoldIntegerMultiplication_InsideStringValue_IsNotAltered()
    {
        // Arithmetic that appears inside a string value (e.g. the explanation) must be left verbatim.
        const string raw = "{\"explanation\":\"about 2*3 things\",\"pattern\":\"*.txt\"}";
        Assert.Equal(raw, SemanticPlanJsonExtractor.FoldIntegerMultiplication(raw));
    }

    [Fact]
    public void FoldIntegerMultiplication_PlainNumbersAndAsteriskGlobs_Unchanged()
    {
        // A lone integer and an unquoted-looking glob asterisk that is not a digit*digit chain must
        // be left exactly as-is.
        const string raw = "{\"minFileSizeBytes\":5242880,\"pattern\":\"*.png\"}";
        Assert.Equal(raw, SemanticPlanJsonExtractor.FoldIntegerMultiplication(raw));
    }

    [Fact]
    public void FoldIntegerMultiplication_DecimalFollowedByChain_LeftAlone()
    {
        // A chain that begins right after a decimal point must not be folded (it is part of a decimal,
        // not a standalone integer product).
        const string raw = "{\"x\":1.5*2}";
        Assert.Equal(raw, SemanticPlanJsonExtractor.FoldIntegerMultiplication(raw));
    }

    [Fact]
    public void ExtractJsonObject_ReasoningModelWithThinkBlock_StripsTraceAndExtractsAnswer()
    {
        // phi-4-reasoning emits a <think>…</think> trace before the answer. The trace contains braces
        // (illustrative JSON), so the extractor must strip the trace before scanning for the plan,
        // otherwise the first '{' lands inside the reasoning prose.
        const string raw = "<think>The user wants png files. A plan might look like {\"pattern\":\"*.foo\"} " +
                           "but that is just my reasoning.</think>\n{\"pattern\":\"*.png\"}";
        string? json = SemanticPlanJsonExtractor.ExtractJsonObject(raw);
        Assert.Equal("{\"pattern\":\"*.png\"}", json);
    }

    [Fact]
    public void TryParsePlan_ReasoningModelFencedAnswerAfterThink_ParsesPlan()
    {
        const string raw = "<think>\nLet me reason about file sizes: 100*1024*1024 bytes.\n</think>\n" +
                           "```json\n{\"pattern\":\"*.bin\",\"minFileSizeBytes\":104857600}\n```";
        Assert.True(SemanticPlanJsonExtractor.TryParsePlan(raw, out SemanticSearchPlan? plan, out string? error));
        Assert.Null(error);
        Assert.Equal("*.bin", plan!.Pattern);
        Assert.Equal(104857600, plan.MinFileSizeBytes);
    }

    [Fact]
    public void StripReasoningTrace_LoneClosingTag_KeepsOnlyAnswerAfterIt()
    {
        // Some reasoning models start thinking implicitly and only emit the closing </think>.
        string stripped = SemanticPlanJsonExtractor.StripReasoningTrace(
            "thinking about this... </think> {\"pattern\":\"*.md\"}");
        Assert.Equal(" {\"pattern\":\"*.md\"}", stripped);
    }

    [Fact]
    public void StripReasoningTrace_UnclosedThink_DropsTruncatedTrace()
    {
        // Truncated mid-reasoning: there is no answer, so the dangling trace is dropped entirely.
        string stripped = SemanticPlanJsonExtractor.StripReasoningTrace(
            "answer pending <think>still reasoning and never finished");
        Assert.Equal("answer pending ", stripped);
    }

    [Fact]
    public void StripReasoningTrace_NoThinkMarkers_ReturnedUnchanged()
    {
        const string raw = "{\"pattern\":\"*.txt\"}";
        Assert.Equal(raw, SemanticPlanJsonExtractor.StripReasoningTrace(raw));
    }
}
