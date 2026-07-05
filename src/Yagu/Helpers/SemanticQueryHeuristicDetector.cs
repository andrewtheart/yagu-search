namespace Yagu.Helpers;

/// <summary>
/// Conservative heuristic that flags a Traditional-mode query reading like a natural-language request
/// meant for AI (Semantic) search — e.g. <c>files on C containing the word "test"</c> — rather than a
/// literal text or filename match. Used to offer a one-time switch to Semantic mode. It only fires on
/// multi-word queries carrying clear natural-language search cues, so ordinary literal searches (code
/// snippets, file names, quoted-exact text) are not flagged.
/// </summary>
public static class SemanticQueryHeuristicDetector
{
    // A single strong, imperative phrase is enough to flag a query (when it has at least 3 words).
    private static readonly string[] StrongPhrases =
    {
        "show me", "find all", "find me", "search for", "look for", "list all", "list every",
        "containing the word", "containing the words", "containing the text", "files on", "files in",
        "files that", "files containing", "files named", "folders containing", "documents containing",
        "anything containing", "everything in", "everything on", "all files", "all the files",
    };

    // Weaker cues; two or more together flag a query (when it has at least 3 words).
    private static readonly string[] WeakCues =
    {
        "containing", "contains", "named", "called", "modified", "created", "edited", "folders",
        "documents", "with the word", "larger than", "smaller than", "bigger than", "older than",
        "newer than", "last week", "last month", "this week", "this month", "yesterday", "recently",
        "anything", "everything",
    };

    /// <summary>
    /// Returns true when <paramref name="query"/> reads like a natural-language semantic request.
    /// </summary>
    public static bool LooksLikeSemanticQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var trimmed = query.Trim();

        // A fully quote-wrapped query is an explicit exact-text search — never flagged.
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return false;

        var lower = trimmed.ToLowerInvariant();
        int wordCount = lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 3)
            return false; // too short to be a natural-language request

        foreach (var phrase in StrongPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        int weakHits = 0;
        foreach (var cue in WeakCues)
        {
            if (lower.Contains(cue, StringComparison.Ordinal) && ++weakHits >= 2)
                return true;
        }

        return false;
    }
}
