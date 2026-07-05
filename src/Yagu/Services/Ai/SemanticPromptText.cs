namespace Yagu.Services.Ai;

/// <summary>
/// Dependency-free text helpers for the semantic-search system prompt. Kept separate from
/// <see cref="FoundryLocalSemanticQueryTranslator"/> (which pulls in Foundry Local / WindowsAppSDK)
/// so the logic can be unit-tested without those dependencies.
/// </summary>
internal static class SemanticPromptText
{
    /// <summary>
    /// Removes a leading YAML front-matter block (an opening line of exactly "---", arbitrary lines,
    /// then a closing line of exactly "---") when present. The prompt is authored as a VS Code
    /// ".prompt.md" whose front matter is editor-only metadata (description/mode) that must NOT be
    /// sent to the model. Returns <paramref name="content"/> unchanged when there is no front matter
    /// or the block is unterminated, so a plain prompt file (no front matter) still loads verbatim.
    /// </summary>
    public static string StripFrontMatter(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Tolerate a leading BOM that the reader may not have stripped; front matter must be the
        // very first content (optionally after that BOM).
        int start = content[0] == '\uFEFF' ? 1 : 0;
        if (!IsDelimiterAt(content, start))
            return content;

        int cursor = NextLineStart(content, start); // skip the opening "---" line
        while (cursor < content.Length)
        {
            if (IsDelimiterAt(content, cursor))
                return content.Substring(NextLineStart(content, cursor)); // drop closing "---" + its newline
            cursor = NextLineStart(content, cursor);
        }

        // Unterminated front matter: keep the content rather than risk dropping the whole prompt.
        return content;
    }

    /// <summary>
    /// Produces a shorter variant of the system prompt for EXTREMELY memory-constrained CPU machines,
    /// where the full ~8.5K-token prompt's KV cache would risk an out-of-memory failure on a tiny model.
    /// Drops the few-shot "## Examples" section (the bulk of the tokens) while keeping the complete
    /// schema, interpretation rules, and decision tree. Matches the heading only at the start of a line
    /// so a mid-text mention is never cut, and returns <paramref name="prompt"/> UNCHANGED when the
    /// heading is absent — so nothing is lost if the prompt is restructured, and every non-constrained
    /// case still sends the identical full prompt.
    /// </summary>
    public static string CondenseForLowMemory(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        int idx = prompt.IndexOf("\n## Examples", StringComparison.Ordinal);
        if (idx < 0)
            return prompt;

        return prompt[..idx].TrimEnd() + "\n";
    }

    /// <summary>
    /// Builds the final system prompt to send to the model: substitutes the <c>{{TODAY}}</c> placeholder
    /// with <paramref name="todayIso"/>, and — ONLY when a CPU-only memory budget is supplied and falls
    /// below <paramref name="condenseThresholdMb"/> — first condenses the prompt via
    /// <see cref="CondenseForLowMemory"/> so a tiny model's KV cache fits. A null
    /// <paramref name="availableMemoryBudgetMb"/> (accelerated hardware) or a budget at/above the
    /// threshold yields the IDENTICAL full prompt. Kept pure/dependency-free so the condense-gating
    /// decision is unit-testable without the Foundry-coupled translator.
    /// </summary>
    public static string BuildSystemPrompt(string template, string todayIso, int? availableMemoryBudgetMb, int condenseThresholdMb)
    {
        string prompt = template ?? string.Empty;
        if (availableMemoryBudgetMb is { } budgetMb && budgetMb < condenseThresholdMb)
            prompt = CondenseForLowMemory(prompt);
        return prompt.Replace("{{TODAY}}", todayIso ?? string.Empty);
    }

    /// <summary>True when the line starting at <paramref name="index"/> is exactly "---" (the YAML
    /// front-matter delimiter), ignoring a trailing CR.</summary>
    private static bool IsDelimiterAt(string content, int index)
    {
        int lineEnd = content.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = content.Length;
        if (lineEnd > index && content[lineEnd - 1] == '\r') lineEnd--;
        return lineEnd - index == 3
            && content[index] == '-' && content[index + 1] == '-' && content[index + 2] == '-';
    }

    /// <summary>Index just past the next '\n' at/after <paramref name="index"/>, or the end of the
    /// string when no newline remains.</summary>
    private static int NextLineStart(string content, int index)
    {
        int nl = content.IndexOf('\n', index);
        return nl < 0 ? content.Length : nl + 1;
    }
}
