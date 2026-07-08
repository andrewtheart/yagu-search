using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string LogSource = "Semantic.JsonExtractor";

    /// <summary>Matches a backslash together with the single character that follows it, so runs of
    /// backslashes are consumed as pairs and a valid <c>\\</c> escape is never mis-split.</summary>
    private static readonly Regex BackslashEscapePair = new(@"\\(.)", RegexOptions.Compiled | RegexOptions.Singleline);

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
            LogService.Instance.Verbose(LogSource,
                $"No JSON object could be extracted from model output ({(raw?.Length ?? 0)} chars).");
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
            LogService.Instance.Verbose(LogSource,
                $"Extracted candidate did not deserialize as a plan: {ex.Message}. Candidate JSON:\n{json}");
            return false;
        }
    }

    /// <summary>Doubles any backslash that does not begin a valid JSON escape sequence
    /// (<c>" \ / b f n r t u</c>), so a model-emitted regex metacharacter like <c>\w</c>/<c>\d</c>/
    /// <c>\.</c> becomes a literal backslash in the parsed string instead of failing the whole parse.
    /// Valid escapes (<c>\n</c>, <c>\"</c>, <c>\\</c>, <c>\uXXXX</c>) are left untouched; matching a
    /// backslash together with its following char keeps runs of backslashes correct.</summary>
    private static string FixInvalidJsonEscapes(string objectBody)
    {
        if (objectBody.IndexOf('\\') < 0) return objectBody;
        return BackslashEscapePair.Replace(objectBody, m =>
        {
            char c = m.Groups[1].Value[0];
            return c is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u'
                ? m.Value            // valid escape -> keep as-is
                : "\\\\" + c;        // invalid -> double the backslash so it parses to a literal '\'
        });
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

        // Reasoning / chain-of-thought models (e.g. phi-4-reasoning) emit a <think>…</think> trace
        // before the answer. That trace routinely contains braces (illustrative JSON, set notation),
        // so the first '{' the scanner finds would otherwise land inside the reasoning prose rather
        // than the real plan. Strip the reasoning preamble first so brace extraction sees only the
        // model's final answer. No-op for the chatty instruct models that never emit <think> tags.
        text = StripReasoningTrace(text);
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Strong instruct models (e.g. phi-4 14B) faithfully mimic the COMMENTED schema example in the
        // system prompt, emitting JSONC — a `// …` comment after every field (sometimes fenced as
        // ```jsonc). Comments are NOT valid JSON, so the gatekeeper JsonDocument.Parse calls below reject
        // the whole object and the trailing-field repair salvages only `{"pattern":""}` (an empty plan).
        // Strip line/block comments first — honoring string state so a `//` inside a value (a URL, a
        // regex, the explanation) is preserved — so the model's correct interpretation survives.
        text = StripJsonComments(text);
        if (string.IsNullOrWhiteSpace(text)) return null;

        int start = text.IndexOf('{');
        if (start < 0) return null;

        // Small on-device models sometimes emit arithmetic in numeric fields (e.g.
        // "minFileSizeBytes": 1048576*100 or 100*1024*1024) despite the prompt forbidding it. That is
        // not valid JSON, and trailing-field repair cannot fix it because the malformed value is not
        // the last field. Fold any integer multiplication chains in value positions into a single
        // literal up front so the object parses. Only the object body is touched; start is unchanged.
        text = string.Concat(text.AsSpan(0, start), FoldIntegerMultiplication(text.Substring(start)));

        // Small models routinely emit regex patterns with single-backslash metacharacters ("\w",
        // "\d", "\s", "\.", "\+") inside JSON string values. Those are INVALID JSON escapes and make
        // the ENTIRE object fail to parse (System.Text.Json: "'w' is an invalid escapable character").
        // Double any backslash that doesn't begin a valid JSON escape so the metacharacter survives as
        // a literal (\w -> \\w, which JSON reads back as "\w"), rescuing an otherwise-lost plan.
        text = string.Concat(text.AsSpan(0, start), FixInvalidJsonEscapes(text.Substring(start)));

        string? balanced = FindBalancedObject(text, start);
        if (balanced is not null)
        {
            if (IsParseableObject(balanced))
                return balanced;

            // The object is brace-balanced but does not parse. A common small-model failure is a
            // string value that closes early (e.g. an unescaped quote inside the explanation, like
            // ...for the term 'Andrew".}), which leaves stray characters before a delimiter. Drop the
            // trailing malformed field(s) and retry so the structured fields still survive.
            LogService.Instance.Verbose(LogSource,
                "Brace-balanced object did not parse; attempting trailing-field repair.");
            string? trimmed = RepairBalancedObject(balanced);
            LogService.Instance.Verbose(LogSource,
                trimmed is null
                    ? "Trailing-field repair failed; surfacing the original parse error."
                    : "Trailing-field repair succeeded.");
            return trimmed ?? balanced;
        }

        // No closing brace was found — the model was almost certainly truncated mid-object (e.g. it
        // hit the token limit). Attempt a best-effort repair so a usable prefix can still be parsed.
        LogService.Instance.Verbose(LogSource,
            "Model output has no brace-balanced object (likely truncated); attempting repair.");
        string? repaired = RepairTruncatedObject(text, start);
        LogService.Instance.Verbose(LogSource,
            repaired is null
                ? "Truncated-object repair failed; no parseable object recovered."
                : "Truncated-object repair succeeded.");
        return repaired;
    }

    /// <summary>Compiled matcher for a complete <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning block
    /// (case-insensitive, spanning newlines, non-greedy so multiple blocks are removed individually).</summary>
    private static readonly Regex ThinkBlockRegex =
        new(@"<think\b[^>]*>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Removes a reasoning model's chain-of-thought trace so only its final answer remains.
    /// Handles three shapes: (1) well-formed <c>&lt;think&gt;…&lt;/think&gt;</c> blocks are deleted;
    /// (2) a leftover lone closing <c>&lt;/think&gt;</c> (model that began thinking implicitly, or whose
    /// opening tag was suppressed) — everything up to and including the last one is dropped; (3) an
    /// unclosed <c>&lt;think&gt;</c> with no closing tag means the model was truncated mid-reasoning and
    /// never reached the answer, so the trace is dropped (no JSON follows anyway). Text with no think
    /// markers is returned unchanged.</summary>
    internal static string StripReasoningTrace(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        string result = ThinkBlockRegex.Replace(text, string.Empty);

        // Any remaining closing tag means the opening tag was malformed/absent; keep only what follows
        // the final close, which is where a reasoning model places its answer.
        int lastClose = result.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (lastClose >= 0)
            result = result[(lastClose + "</think>".Length)..];

        // A dangling opener with no close = truncated mid-thought; drop it (the answer never arrived).
        int openOnly = result.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (openOnly >= 0)
            result = result[..openOnly];

        return result;
    }

    /// <summary>Removes JSONC-style comments — <c>// line</c> comments (to end of line) and
    /// <c>/* block */</c> comments (spanning lines) — that a model appends when it mirrors the commented
    /// schema example in the system prompt. Scanning tracks JSON string state, so a <c>//</c> or
    /// <c>/*</c> that appears INSIDE a string value (a URL like <c>http://…</c>, a regex, the
    /// explanation) is left untouched. Newlines are preserved so line numbers/structure stay intact.
    /// Without this, comments make <see cref="System.Text.Json"/> reject the whole object and the plan
    /// is lost. A no-op when the text contains no comment markers.</summary>
    internal static string StripJsonComments(string text)
    {
        if (string.IsNullOrEmpty(text) ||
            (text.IndexOf("//", StringComparison.Ordinal) < 0 && text.IndexOf("/*", StringComparison.Ordinal) < 0))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        bool inString = false;
        bool escaped = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                i++;
                continue;
            }

            // Line comment: drop from `//` to just before the end of the line (the newline is kept).
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                continue;
            }

            // Block comment: drop `/* … */`. If it is never closed (truncated), drop the remainder.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = (i + 1 < text.Length) ? i + 2 : text.Length;
                continue;
            }

            if (c == '"') inString = true;
            sb.Append(c);
            i++;
        }

        return sb.ToString();
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

    /// <summary>Returns true when <paramref name="json"/> parses as a JSON document.</summary>
    private static bool IsParseableObject(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Repairs a brace-balanced object that fails to parse (most often a string value closed
    /// early by an unescaped quote, leaving stray characters before a delimiter) by trimming back to a
    /// top-level (depth-1) comma — dropping the trailing malformed field(s) — and re-closing the
    /// object with <c>}</c>. Tries the rightmost comma first so the fewest fields are dropped. Returns
    /// null when no trim yields valid JSON (e.g. the first field itself is malformed).</summary>
    internal static string? RepairBalancedObject(string obj)
    {
        var topLevelCommas = new List<int>();
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < obj.Length; i++)
        {
            char c = obj[i];
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
                case '[': depth++; break;
                case '}':
                case ']': depth--; break;
                case ',': if (depth == 1) topLevelCommas.Add(i); break;
            }
        }

        for (int k = topLevelCommas.Count - 1; k >= 0; k--)
        {
            string candidate = string.Concat(obj.AsSpan(0, topLevelCommas[k]), "}");
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                // The trailing field was not the only malformed one; keep trimming earlier fields.
            }
        }
        return null;
    }

    /// <summary>Folds integer multiplication chains that on-device models sometimes emit in numeric
    /// value positions (e.g. <c>1048576*100</c> or <c>100 * 1024 * 1024</c>) into a single literal,
    /// since arithmetic expressions are not valid JSON. Scanning tracks string state so text inside
    /// string values (such as the explanation) is never altered, and a chain that follows a decimal
    /// point is left alone. Products that overflow <see cref="long"/> saturate to <c>long.MaxValue</c>.
    /// </summary>
    internal static string FoldIntegerMultiplication(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('*')) return text;

        var sb = new StringBuilder(text.Length);
        bool inString = false;
        bool escaped = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (char.IsDigit(c))
            {
                // Consume a run of the form  digits ( * digits )*  with optional spaces/tabs around '*'.
                var factors = new List<long>();
                bool factorOverflow = false;
                int j = i;
                while (j < text.Length && char.IsDigit(text[j]))
                {
                    int numStart = j;
                    while (j < text.Length && char.IsDigit(text[j])) j++;
                    if (long.TryParse(text.AsSpan(numStart, j - numStart), NumberStyles.None, CultureInfo.InvariantCulture, out long factor))
                        factors.Add(factor);
                    else { factorOverflow = true; break; }

                    int k = j;
                    while (k < text.Length && (text[k] == ' ' || text[k] == '\t')) k++;
                    if (k < text.Length && text[k] == '*')
                    {
                        int m = k + 1;
                        while (m < text.Length && (text[m] == ' ' || text[m] == '\t')) m++;
                        if (m < text.Length && char.IsDigit(text[m]))
                        {
                            j = m; // continue the chain at the next factor
                            continue;
                        }
                    }
                    break; // no further '* digits'
                }

                bool precededByDot = i > 0 && text[i - 1] == '.';
                if (!factorOverflow && !precededByDot && factors.Count >= 2)
                {
                    long product = 1;
                    bool productOverflow = false;
                    foreach (long f in factors)
                    {
                        try { product = checked(product * f); }
                        catch (OverflowException) { productOverflow = true; break; }
                    }
                    sb.Append((productOverflow ? long.MaxValue : product).ToString(CultureInfo.InvariantCulture));
                    i = j;
                    continue;
                }

                // Not a multiplication chain (or unparseable) — copy the original digits verbatim.
                sb.Append(text, i, j - i);
                i = j;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
