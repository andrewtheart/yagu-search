using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for <see cref="SemanticPromptText"/>. The semantic system prompt is authored as a VS Code
/// ".prompt.md" with YAML front matter (editor-only metadata); <see cref="SemanticPromptText.StripFrontMatter"/>
/// must remove that block so the model receives only the live prompt body.
/// </summary>
public class SemanticPromptTextTests
{
    [Fact]
    public void StripFrontMatter_RemovesLeadingYamlBlock_KeepsBody()
    {
        const string content =
            "---\n" +
            "description: 'editor only'\n" +
            "mode: 'agent'\n" +
            "---\n" +
            "# Title\n" +
            "Body line with {{TODAY}}.\n";

        string result = SemanticPromptText.StripFrontMatter(content);

        Assert.StartsWith("# Title", result);
        Assert.Contains("{{TODAY}}", result);
        Assert.DoesNotContain("description:", result);
        Assert.DoesNotContain("mode:", result);
        Assert.DoesNotContain("---", result);
    }

    [Fact]
    public void StripFrontMatter_HandlesCrLfNewlines()
    {
        const string content =
            "---\r\n" +
            "description: 'x'\r\n" +
            "---\r\n" +
            "Body\r\n";

        string result = SemanticPromptText.StripFrontMatter(content);

        Assert.Equal("Body\r\n", result);
    }

    [Fact]
    public void StripFrontMatter_ToleratesLeadingBom()
    {
        const string content = "\uFEFF---\nmode: 'agent'\n---\nBody\n";

        string result = SemanticPromptText.StripFrontMatter(content);

        Assert.Equal("Body\n", result);
    }

    [Fact]
    public void StripFrontMatter_NoFrontMatter_ReturnsUnchanged()
    {
        const string content = "You convert a request into JSON.\n# Examples\n";

        string result = SemanticPromptText.StripFrontMatter(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void StripFrontMatter_DashedRuleNotAtStart_IsNotTreatedAsFrontMatter()
    {
        // A "---" that is not the very first line is body content, not a delimiter.
        const string content = "Intro line\n---\nmore\n";

        string result = SemanticPromptText.StripFrontMatter(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void StripFrontMatter_UnterminatedBlock_ReturnsUnchanged()
    {
        // Missing closing delimiter: keep the whole content rather than drop the prompt.
        const string content = "---\ndescription: 'x'\nBody without close\n";

        string result = SemanticPromptText.StripFrontMatter(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void StripFrontMatter_EmptyString_ReturnsInput()
        => Assert.Equal(string.Empty, SemanticPromptText.StripFrontMatter(string.Empty));

    [Fact]
    public void StripFrontMatter_DelimiterWithoutTrailingNewline_ReturnsUnchanged()
    {
        // A lone "---" with no newline exercises the no-newline paths (IndexOf('\n') == -1) in both
        // the delimiter check and the line-advance helper; front matter is unterminated so it is kept.
        Assert.Equal("---", SemanticPromptText.StripFrontMatter("---"));
    }

    [Fact]
    public void StripFrontMatter_BlankLineInsideFrontMatter_StripsToBody()
    {
        // A blank line inside the block makes the delimiter scan hit an empty line (lineEnd == index),
        // then the closing "---" terminates the block and only the body survives.
        Assert.Equal("body\n", SemanticPromptText.StripFrontMatter("---\n\n---\nbody\n"));
    }

    [Fact]
    public void CondenseForLowMemory_DropsExamplesSection_KeepsSchemaAndRules()
    {
        const string content =
            "# Yagu semantic search\n" +
            "## Output schema\n" +
            "fields...\n" +
            "## INTERPRETATION RULES\n" +
            "rules...\n" +
            "## Examples\n" +
            "### Example 1\n" +
            "User: ...\n" +
            "JSON: {...}\n";

        string result = SemanticPromptText.CondenseForLowMemory(content);

        Assert.Contains("## Output schema", result);
        Assert.Contains("## INTERPRETATION RULES", result);
        Assert.DoesNotContain("## Examples", result);
        Assert.DoesNotContain("### Example 1", result);
        Assert.EndsWith("rules...\n", result);
    }

    [Fact]
    public void CondenseForLowMemory_NoExamplesHeading_ReturnsUnchanged()
    {
        const string content = "# Prompt\n## Output schema\nfields...\n";

        string result = SemanticPromptText.CondenseForLowMemory(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void CondenseForLowMemory_MidLineExamplesMention_IsNotCut()
    {
        // "## Examples" must be a heading at the start of a line; a mid-text mention is body content.
        const string content = "Rules mention the ## Examples section inline.\nmore rules\n";

        string result = SemanticPromptText.CondenseForLowMemory(content);

        Assert.Equal(content, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void CondenseForLowMemory_EmptyOrNull_ReturnsInput(string? content)
    {
        Assert.Equal(content, SemanticPromptText.CondenseForLowMemory(content!));
    }

    private const string SampleTemplate =
        "# Prompt for {{TODAY}}\n" +
        "## Output schema\n" +
        "fields...\n" +
        "## Examples\n" +
        "### Example 1\n";

    [Fact]
    public void BuildSystemPrompt_NullBudget_UsesFullPromptAndSubstitutesDate()
    {
        // Accelerated hardware reports a null budget → the identical full prompt (examples kept).
        string result = SemanticPromptText.BuildSystemPrompt(SampleTemplate, "2026-07-02", availableMemoryBudgetMb: null, condenseThresholdMb: 2048);

        Assert.Contains("# Prompt for 2026-07-02", result);
        Assert.DoesNotContain("{{TODAY}}", result);
        Assert.Contains("## Examples", result);
    }

    [Fact]
    public void BuildSystemPrompt_BudgetAtThreshold_UsesFullPrompt()
    {
        // Boundary: a budget EQUAL to the threshold is not "below" it, so the full prompt is used.
        string result = SemanticPromptText.BuildSystemPrompt(SampleTemplate, "2026-07-02", availableMemoryBudgetMb: 2048, condenseThresholdMb: 2048);

        Assert.Contains("## Examples", result);
        Assert.Contains("2026-07-02", result);
    }

    [Fact]
    public void BuildSystemPrompt_BudgetBelowThreshold_CondensesPrompt()
    {
        string result = SemanticPromptText.BuildSystemPrompt(SampleTemplate, "2026-07-02", availableMemoryBudgetMb: 2047, condenseThresholdMb: 2048);

        Assert.DoesNotContain("## Examples", result);
        Assert.Contains("## Output schema", result);
        Assert.Contains("# Prompt for 2026-07-02", result);
    }

    [Fact]
    public void BuildSystemPrompt_NullTemplate_ReturnsEmpty()
        => Assert.Equal(string.Empty, SemanticPromptText.BuildSystemPrompt(null!, "2026-07-02", availableMemoryBudgetMb: null, condenseThresholdMb: 2048));

    [Fact]
    public void BuildSystemPrompt_NullToday_SubstitutesEmptyForPlaceholder()
    {
        string result = SemanticPromptText.BuildSystemPrompt("date={{TODAY}}", todayIso: null!, availableMemoryBudgetMb: null, condenseThresholdMb: 2048);

        Assert.Equal("date=", result);
    }
}
