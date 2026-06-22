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
}
