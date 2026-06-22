using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Yagu.Models;

namespace Yagu.Services.Ai;

/// <summary>
/// Pure, dependency-free extraction of a <see cref="SemanticSearchPlan"/> from raw model output.
/// Kept separate from <see cref="FoundryLocalSemanticQueryTranslator"/> (which pulls in Foundry/
/// WindowsAppSDK) so this logic can be unit-tested in isolation.
///
/// The small on-device instruct models we target are chatty: they wrap output in code fences,
/// append prose, repeat the object, and — when they ramble into the token limit — truncate the
/// object before its closing brace. This extractor makes a leading well-formed object always win
/// and repairs truncated output into a usable prefix.
/// </summary>
internal static class SemanticPlanJsonExtractor
{
    /// <summary>
    /// Extracts the first usable JSON object from <paramref name="raw"/> (tolerating code fences or
    /// surrounding prose) and deserializes it into a <see cref="SemanticSearchPlan"/>.
    /// </summary>
    internal static bool TryParsePlan(string raw, out SemanticSearchPlan? plan, out string? error)
    {
        plan = null;
        error = null;

        string? json = ExtractJsonObject(raw);
        if (json is null)
        {
            error = "The model did not return a JSON object.";
            return false;
        }

        try
        {
            // A successfully-extracted, brace-delimited object never deserializes to JSON null, so a
            // non-null plan is guaranteed here; genuinely malformed JSON throws and is handled below.
            plan = JsonSerializer.Deserialize(json, SemanticSearchPlanJsonContext.Default.SemanticSearchPlan);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"The model output was not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>Returns the first usable top-level <c>{...}</c> block in <paramref name="text"/>,
    /// honoring braces inside strings. If the first object is complete it is returned as-is (so a
    /// leading well-formed object always wins over any trailing repeats or prose a chatty model
    /// appends). If the model's output was truncated mid-object (no closing brace — e.g. it hit the
    /// token limit while rambling), a best-effort repair closes the open string/brackets, falling
    /// back to trimming the trailing partial field, so a usable prefix can still be parsed. Returns
    /// null when no parseable object can be recovered.</summary>
    internal static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int start = text.IndexOf('{');
        if (start < 0) return null;

        string? balanced = FindBalancedObject(text, start);
        if (balanced is not null) return balanced;

        return RepairTruncatedObject(text, start);
    }

    /// <summary>Scans from <paramref name="start"/> and returns the first brace-balanced object, or
    /// null if the text ends before the root object closes.</summary>
    private static string? FindBalancedObject(string text, int start)
    {
        bool inString = false;
        bool escaped = false;
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }

    /// <summary>Recovers a parseable object from output that was cut off before its closing brace.
    /// First tries closing the object at the truncation point (any open string + open brackets);
    /// if that does not parse, progressively trims back to the previous top-level comma (dropping
    /// the partial trailing field) until a valid object is found. Returns null if nothing parses.
    /// </summary>
    private static string? RepairTruncatedObject(string text, int start)
    {
        // Walk the partial object, tracking the open bracket stack, string state, and the indices of
        // top-level (depth-1) commas — each is a safe place to cut and close the object.
        var openStack = new List<char>();
        var topLevelCommas = new List<int>();
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                case '[': openStack.Add(c); break;
                case '}':
                case ']': if (openStack.Count > 0) openStack.RemoveAt(openStack.Count - 1); break;
                case ',': if (openStack.Count == 1) topLevelCommas.Add(i); break;
            }
        }
        bool endsInString = inString;

        if (openStack.Count == 0) return null; // not actually truncated; nothing to repair

        // Candidate 1: close the object exactly where it was cut.
        string head = text.Substring(start);
        string? closed = CloseAndValidate(head, endsInString, openStack);
        if (closed is not null) return closed;

        // Candidates 2..n: trim back to each top-level comma (rightmost first), dropping the partial
        // trailing field, then close the root object.
        for (int k = topLevelCommas.Count - 1; k >= 0; k--)
        {
            string trimmed = text.Substring(start, topLevelCommas[k] - start);
            string? candidate = CloseAndValidate(trimmed, endsInString: false, openStack: ['{']);
            if (candidate is not null) return candidate;
        }

        return null;
    }

    /// <summary>Appends the closers needed to balance <paramref name="head"/> (an open string, then
    /// the open brackets in reverse) and returns it if the result is valid JSON; otherwise null.</summary>
    internal static string? CloseAndValidate(string head, bool endsInString, List<char> openStack)
    {
        var sb = new StringBuilder(head.TrimEnd());
        // A trailing comma left by truncation (e.g. `..."x":1,`) would be invalid — drop it.
        while (sb.Length > 0 && sb[^1] == ',') sb.Length--;
        if (endsInString) sb.Append('"');
        for (int i = openStack.Count - 1; i >= 0; i--)
            sb.Append(openStack[i] == '[' ? ']' : '}');

        string candidate = sb.ToString();
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
