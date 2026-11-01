using System.Text;
using System.Text.Json;

namespace Yagu.Services.Mcp;

/// <summary>The outcome of editing one client's configuration file.</summary>
internal enum McpRegistrationOutcome
{
    /// <summary>Yagu's entry was added.</summary>
    Installed,

    /// <summary>Yagu's entry already existed and was updated in place.</summary>
    Updated,

    /// <summary>Yagu's entry was removed.</summary>
    Removed,

    /// <summary>There was no Yagu entry to remove.</summary>
    NotPresent,

    /// <summary>The file could not be parsed, so nothing was written.</summary>
    Malformed,

    /// <summary>The file changed underneath us, or could not be read or written.</summary>
    Failed,
}

internal sealed record McpRegistrationResult(McpRegistrationOutcome Outcome, string? Detail = null)
{
    public bool Succeeded => Outcome is McpRegistrationOutcome.Installed
        or McpRegistrationOutcome.Updated
        or McpRegistrationOutcome.Removed
        or McpRegistrationOutcome.NotPresent;
}

/// <summary>
/// Edits an MCP client's configuration file to add or remove Yagu's server entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>This merges; it never overwrites.</b> These files belong to the user and routinely list other
/// servers, carry comments, and hold settings Yagu knows nothing about. Reserializing a parsed document
/// would silently delete every comment and every property this version does not understand, and
/// restoring a backup over the file would revert unrelated edits the user made in between.
/// </para>
/// <para>
/// So the edit is performed as a <b>text splice</b>: the exact byte range of Yagu's own property (or of
/// the servers container) is located with <see cref="Utf8JsonReader"/>, and only that range is replaced.
/// Every other byte of the file — including comments and formatting — survives untouched.
/// </para>
/// <para>
/// A file that does not parse is left strictly alone. Rewriting a file we do not understand is how a
/// tool destroys a configuration a user spent an afternoon on.
/// </para>
/// </remarks>
internal static class McpClientRegistrar
{
    /// <summary>Shown when a spliced document would not parse, so the file is left untouched.</summary>
    private const string SpliceRefusal =
        "the edit would have produced invalid JSON, so the file was left untouched. Please report this.";

    /// <summary>Scratch file for the atomic write, beside the target so the swap is a same-volume rename.</summary>
    internal const string TempSuffix = ".yagu-tmp";

    /// <summary>The user's previous configuration, kept because the splice is validated but not verified.</summary>
    internal const string BackupSuffix = ".yagu-backup";

    internal delegate bool InputRemovalOperation(
        string text,
        string inputId,
        out string updated,
        out string? parseError);

    internal delegate void FileReplaceOperation(
        string sourceFileName,
        string destinationFileName,
        string? destinationBackupFileName,
        bool ignoreMetadataErrors);

    internal delegate void FileMoveOperation(string sourceFileName, string destinationFileName, bool overwrite);

    /// <summary>Adds or updates Yagu's entry. <paramref name="serverJson"/> is the value object for the entry.</summary>
    /// <param name="inputId">
    /// When set, an <c>inputs</c> definition with this id is merged too. An HTTP entry references the
    /// bearer token as <c>${input:id}</c>, and the reference is useless without its definition, so the
    /// two are written in one operation or not at all.
    /// </param>
    public static McpRegistrationResult Install(
        string path, string containerProperty, string serverName, string serverJson,
        string? inputId = null, string? inputJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string? original = ReadIfExists(path, out DateTime stamp, out long length);

            if (string.IsNullOrWhiteSpace(original))
            {
                string fresh = BuildFreshDocument(containerProperty, serverName, serverJson);
                if (inputId is not null && inputJson is not null
                    && !TryMergeInput(fresh, inputId, inputJson, out fresh, out string? freshError))
                {
                    return new McpRegistrationResult(McpRegistrationOutcome.Malformed, freshError);
                }

                if (!IsWellFormed(fresh))
                    return new McpRegistrationResult(McpRegistrationOutcome.Malformed, SpliceRefusal);

                return Write(path, fresh, stamp, length, McpRegistrationOutcome.Installed);
            }

            if (!TryLocate(original, containerProperty, serverName, out JsonSpan container, out JsonSpan entry, out string? parseError))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, parseError);

            string updated;
            McpRegistrationOutcome outcome;

            if (entry.Found)
            {
                // Replace only the value of our own property.
                updated = original[..entry.ValueStart] + Reindent(serverJson, entry.Indent) + original[entry.ValueEnd..];
                outcome = McpRegistrationOutcome.Updated;
            }
            else if (container.Found)
            {
                updated = InsertIntoContainer(original, container, serverName, serverJson);
                outcome = McpRegistrationOutcome.Installed;
            }
            else
            {
                updated = InsertContainer(original, containerProperty, serverName, serverJson);
                outcome = McpRegistrationOutcome.Installed;
            }

            if (inputId is not null && inputJson is not null)
            {
                // Re-located against the spliced text: the offsets above have already shifted.
                if (!TryMergeInput(updated, inputId, inputJson, out updated, out string? inputError))
                    return new McpRegistrationResult(McpRegistrationOutcome.Malformed, inputError);
            }

            if (!IsWellFormed(updated))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, SpliceRefusal);

            return Write(path, updated, stamp, length, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new McpRegistrationResult(McpRegistrationOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Removes Yagu's entry and nothing else. Other servers, comments, and unknown properties survive.
    /// </summary>
    public static McpRegistrationResult Remove(
        string path, string containerProperty, string serverName, string? inputId = null)
        => Remove(path, containerProperty, serverName, inputId, TryRemoveInput, IsWellFormed);

    /// <remarks>
    /// Takes the input-removal and final JSON validation seams so tests can force the rollback arms that
    /// protect users from future splice regressions; a well-formed document cannot naturally make the
    /// current remover produce malformed JSON without changing this production code.
    /// </remarks>
    internal static McpRegistrationResult Remove(
        string path,
        string containerProperty,
        string serverName,
        string? inputId,
        InputRemovalOperation removeInput,
        Func<string, bool> isWellFormed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string? original = ReadIfExists(path, out DateTime stamp, out long length);
            if (string.IsNullOrWhiteSpace(original))
                return new McpRegistrationResult(McpRegistrationOutcome.NotPresent);

            if (!TryLocate(original, containerProperty, serverName, out _, out JsonSpan entry, out string? parseError))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, parseError);

            if (!entry.Found)
                return new McpRegistrationResult(McpRegistrationOutcome.NotPresent);

            (int cutStart, int cutEnd) = ExpandToWholeProperty(original, entry);
            string updated = original[..cutStart] + original[cutEnd..];

            // The token input is ours too, so uninstalling must not leave it prompting for a server that
            // no longer exists.
            if (inputId is not null && !removeInput(updated, inputId, out updated, out string? inputError))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, inputError);

            if (!isWellFormed(updated))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, SpliceRefusal);

            return Write(path, updated, stamp, length, McpRegistrationOutcome.Removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new McpRegistrationResult(McpRegistrationOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Removes one input definition Yagu owns, leaving the rest of the file alone.
    /// </summary>
    /// <remarks>
    /// Switching a client from the HTTP entry back to stdio drops the <c>${input:}</c> reference but not
    /// its definition, which would otherwise keep prompting for a token nothing uses.
    /// </remarks>
    public static McpRegistrationResult RemoveInput(string path, string inputId)
        => RemoveInput(path, inputId, TryRemoveInput, IsWellFormed);

    /// <remarks>
    /// Takes the input-removal and final JSON validation seams so tests can reach the refusal arm that
    /// prevents a future bad splice from committing a malformed <c>inputs</c> array.
    /// </remarks>
    internal static McpRegistrationResult RemoveInput(
        string path,
        string inputId,
        InputRemovalOperation removeInput,
        Func<string, bool> isWellFormed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string? original = ReadIfExists(path, out DateTime stamp, out long length);
            if (string.IsNullOrWhiteSpace(original))
                return new McpRegistrationResult(McpRegistrationOutcome.NotPresent);

            if (!removeInput(original, inputId, out string updated, out string? parseError))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, parseError);

            if (ReferenceEquals(updated, original) || string.Equals(updated, original, StringComparison.Ordinal))
                return new McpRegistrationResult(McpRegistrationOutcome.NotPresent);

            if (!isWellFormed(updated))
                return new McpRegistrationResult(McpRegistrationOutcome.Malformed, SpliceRefusal);

            return Write(path, updated, stamp, length, McpRegistrationOutcome.Removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new McpRegistrationResult(McpRegistrationOutcome.Failed, ex.Message);
        }
    }

    /// <summary>True when the file already carries a Yagu entry.</summary>
    public static bool IsInstalled(string path, string containerProperty, string serverName)
    {
        try
        {
            string? original = ReadIfExists(path, out _, out _);
            if (string.IsNullOrWhiteSpace(original))
                return false;

            return TryLocate(original, containerProperty, serverName, out _, out JsonSpan entry, out _) && entry.Found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ── Locating ──

    /// <summary>Byte/character range of a JSON property found in the source text.</summary>
    private readonly record struct JsonSpan(bool Found, int NameStart, int ValueStart, int ValueEnd, string Indent);

    /// <summary>
    /// Finds the servers container and Yagu's entry inside it, as character ranges in the source text.
    /// </summary>
    /// <remarks>
    /// Comments are skipped rather than rejected: VS Code's <c>mcp.json</c> is JSONC and users comment
    /// their servers. Trailing commas are allowed for the same reason.
    /// </remarks>
    private static bool TryLocate(
        string text, string containerProperty, string serverName,
        out JsonSpan container, out JsonSpan entry, out string? parseError)
    {
        container = default;
        entry = default;
        parseError = null;

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var options = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        try
        {
            var reader = new Utf8JsonReader(utf8, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                parseError = "the file's root is not a JSON object.";
                return false;
            }

            int depth = 1;
            bool inContainer = false;
            int containerDepth = -1;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        string name = reader.GetString() ?? string.Empty;
                        int nameStart = CharIndex(utf8, (int)reader.TokenStartIndex);

                        if (depth == 1 && string.Equals(name, containerProperty, StringComparison.Ordinal))
                        {
                            reader.Read();
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                // The property exists but is not an object. Inserting our own would leave
                                // the file with two '<container>' properties, which is ambiguous at best
                                // and silently drops one of them at worst.
                                parseError =
                                    $"'{containerProperty}' is present but is not a JSON object, so Yagu's entry cannot be merged into it.";
                                return false;
                            }

                            int valueStart = CharIndex(utf8, (int)reader.TokenStartIndex);
                            inContainer = true;
                            containerDepth = depth + 1;
                            depth++;
                            container = new JsonSpan(true, nameStart, valueStart, -1, IndentOf(text, nameStart));
                            continue;
                        }

                        if (inContainer && depth == containerDepth
                            && string.Equals(name, serverName, StringComparison.Ordinal))
                        {
                            reader.Read();
                            int valueStart = CharIndex(utf8, (int)reader.TokenStartIndex);

                            // Arrays need skipping as much as objects: without it the recorded end sits
                            // just past '[', and replacing that range leaves the old array body behind.
                            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                                reader.Skip();

                            int valueEnd = CharIndex(utf8, (int)reader.BytesConsumed);
                            entry = new JsonSpan(true, nameStart, valueStart, valueEnd, IndentOf(text, nameStart));
                            continue;
                        }
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        if (inContainer && depth == containerDepth - 1 && container.ValueEnd < 0)
                        {
                            container = container with { ValueEnd = CharIndex(utf8, (int)reader.BytesConsumed) };
                            inContainer = false;
                        }
                        break;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Finds the top-level <c>inputs</c> array and, inside it, the element whose <c>id</c> is ours.
    /// </summary>
    /// <remarks>
    /// Returns true with an unfound <paramref name="array"/> when the file simply has no <c>inputs</c>
    /// yet — that is a merge, not a failure. It returns false only when the file cannot be understood.
    /// </remarks>
    private static bool TryLocateInputs(
        string text, string inputId,
        out JsonSpan array, out JsonSpan element, out string? parseError)
    {
        array = default;
        element = default;
        parseError = null;

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var options = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        try
        {
            var reader = new Utf8JsonReader(utf8, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                parseError = "the file's root is not a JSON object.";
                return false;
            }

            int depth = 1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && depth == 1
                    && string.Equals(reader.GetString(), McpConfigSnippetBuilder.InputsPropertyName, StringComparison.Ordinal))
                {
                    int nameStart = CharIndex(utf8, (int)reader.TokenStartIndex);
                    reader.Read();

                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        parseError =
                            $"'{McpConfigSnippetBuilder.InputsPropertyName}' is present but is not a JSON array, so Yagu's token input cannot be merged into it.";
                        return false;
                    }

                    int valueStart = CharIndex(utf8, (int)reader.TokenStartIndex);
                    ReadInputElements(utf8, text, ref reader, inputId, ref element);
                    int valueEnd = CharIndex(utf8, (int)reader.BytesConsumed);

                    array = new JsonSpan(true, nameStart, valueStart, valueEnd, IndentOf(text, nameStart));
                    return true;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            return false;
        }
    }

    /// <summary>Scans the elements of the open <c>inputs</c> array, stopping on its closing bracket.</summary>
    private static void ReadInputElements(
        byte[] utf8, string text, ref Utf8JsonReader reader, string inputId, ref JsonSpan element)
    {
        int elementStart = -1;
        string? currentId = null;
        int depth = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth == 0)
                    {
                        elementStart = CharIndex(utf8, (int)reader.TokenStartIndex);
                        currentId = null;
                    }
                    depth++;
                    break;

                case JsonTokenType.StartArray:
                    depth++;
                    break;

                case JsonTokenType.EndArray:
                    if (depth == 0)
                        return;
                    depth--;
                    break;

                case JsonTokenType.EndObject:
                    depth--;
                    if (depth == 0 && !element.Found
                        && string.Equals(currentId, inputId, StringComparison.Ordinal))
                    {
                        int end = CharIndex(utf8, (int)reader.BytesConsumed);
                        element = new JsonSpan(true, elementStart, elementStart, end, IndentOf(text, elementStart));
                    }
                    break;

                case JsonTokenType.PropertyName:
                    if (depth == 1 && string.Equals(reader.GetString(), "id", StringComparison.Ordinal))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String)
                            currentId = reader.GetString();
                        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                            depth++;
                    }
                    break;
            }
        }
    }

    /// <summary>Adds or refreshes our <c>inputs</c> element, creating the array when the file has none.</summary>
    private static bool TryMergeInput(
        string text, string inputId, string inputJson, out string updated, out string? parseError)
        => TryMergeInput(text, inputId, inputJson, RootBraceIndex, out updated, out parseError);

    /// <remarks>
    /// Takes the root-brace resolver so tests can exercise the fail-closed insertion arm. Public callers
    /// reach this only after the JSON reader has accepted the root object, so a natural filesystem input
    /// cannot make <see cref="RootBraceIndex"/> fail without a future parser regression.
    /// </remarks>
    internal static bool TryMergeInput(
        string text,
        string inputId,
        string inputJson,
        Func<string, int> rootBraceIndex,
        out string updated,
        out string? parseError)
    {
        updated = text;

        if (!TryLocateInputs(text, inputId, out JsonSpan array, out JsonSpan element, out parseError))
            return false;

        if (element.Found)
        {
            updated = text[..element.ValueStart] + Reindent(inputJson, element.Indent) + text[element.ValueEnd..];
            return true;
        }

        if (array.Found)
        {
            int insertAt = array.ValueStart + 1;
            string indent = array.Indent + "  ";
            bool empty = IsEmptyArray(text, array);

            var inserted = new StringBuilder();
            inserted.Append('\n').Append(indent).Append(Reindent(inputJson, indent));
            if (!empty)
                inserted.Append(',');

            updated = text[..insertAt] + inserted + text[insertAt..];
            return true;
        }

        int open = rootBraceIndex(text);
        if (open < 0)
        {
            parseError = "the file's root is not a JSON object.";
            return false;
        }

        bool rootEmpty = SkipTriviaForward(text, open + 1) is int probe && probe < text.Length && text[probe] == '}';
        const string rootIndent = "  ";

        var block = new StringBuilder();
        block.Append('\n').Append(rootIndent).Append('"').Append(McpConfigSnippetBuilder.InputsPropertyName).Append("\": [")
             .Append('\n').Append(rootIndent).Append("  ").Append(Reindent(inputJson, rootIndent + "  "))
             .Append('\n').Append(rootIndent).Append(']');
        if (!rootEmpty)
            block.Append(',');

        updated = text[..(open + 1)] + block + text[(open + 1)..];
        return true;
    }

    /// <summary>Removes our <c>inputs</c> element, leaving every other input definition untouched.</summary>
    private static bool TryRemoveInput(string text, string inputId, out string updated, out string? parseError)
    {
        updated = text;

        if (!TryLocateInputs(text, inputId, out _, out JsonSpan element, out parseError))
            return false;

        if (!element.Found)
            return true;

        (int start, int end) = ExpandToWholeProperty(text, element);
        updated = text[..start] + text[end..];
        return true;
    }

    private static bool IsEmptyArray(string text, JsonSpan array)
    {
        int i = SkipTriviaForward(text, array.ValueStart + 1);
        return i >= text.Length || text[i] == ']';
    }

    // ── Splicing ──

    private static string InsertIntoContainer(string text, JsonSpan container, string serverName, string serverJson)
    {
        // Insert immediately after the container's opening brace so the existing entries, their order,
        // and their formatting are untouched.
        int insertAt = container.ValueStart + 1;
        string indent = container.Indent + "  ";
        bool empty = IsEmptyObject(text, container);

        var inserted = new StringBuilder();
        inserted.Append('\n').Append(indent).Append('"').Append(serverName).Append("\": ")
                .Append(Reindent(serverJson, indent));
        if (!empty)
            inserted.Append(',');

        return text[..insertAt] + inserted + text[insertAt..];
    }

    private static string InsertContainer(string text, string containerProperty, string serverName, string serverJson)
    {
        int open = RootBraceIndex(text);
        if (open < 0)
            return BuildFreshDocument(containerProperty, serverName, serverJson);

        int firstToken = SkipTriviaForward(text, open + 1);
        bool empty = firstToken < text.Length && text[firstToken] == '}';
        string indent = "  ";

        var inserted = new StringBuilder();
        inserted.Append('\n').Append(indent).Append('"').Append(containerProperty).Append("\": {")
                .Append('\n').Append(indent).Append("  \"").Append(serverName).Append("\": ")
                .Append(Reindent(serverJson, indent + "  "))
                .Append('\n').Append(indent).Append('}');
        if (!empty)
            inserted.Append(',');

        return text[..(open + 1)] + inserted + text[(open + 1)..];
    }

    /// <summary>
    /// Grows a property's range to cover its name, its value, one adjacent comma, and the whitespace that
    /// would otherwise be left behind as a blank line.
    /// </summary>
    private static (int Start, int End) ExpandToWholeProperty(string text, JsonSpan entry)
    {
        List<(int Start, int End)> comments = CommentSpans(text);

        int start = entry.NameStart;
        int end = entry.ValueEnd;

        // Comments are trivia here. A comment between the value and its comma would otherwise hide the
        // comma, so the property would be cut out while its separator stayed behind as a leading comma.
        int after = SkipTriviaForward(text, end);
        if (after < text.Length && text[after] == ',')
        {
            end = after + 1;
        }
        else
        {
            int before = SkipTriviaBackward(text, start, comments);
            if (before > 0 && text[before - 1] == ',')
                start = before - 1;
        }

        // Absorb the line's leading indentation and the newline before it.
        int lineStart = start;
        while (lineStart > 0 && (text[lineStart - 1] == ' ' || text[lineStart - 1] == '\t')) lineStart--;
        if (lineStart > 0 && text[lineStart - 1] == '\n')
        {
            lineStart--;
            if (lineStart > 0 && text[lineStart - 1] == '\r') lineStart--;
            start = lineStart;
        }

        return (start, end);
    }

    private static bool IsEmptyObject(string text, JsonSpan container)
    {
        int i = SkipTriviaForward(text, container.ValueStart + 1);
        return i >= text.Length || text[i] == '}';
    }

    /// <summary>
    /// Character index of the ROOT object's opening brace, or -1.
    /// </summary>
    /// <remarks>
    /// A leading JSONC comment can contain a brace, so scanning the text for the first '{' can splice a
    /// new top-level property into comment text and destroy the file while reporting success.
    /// </remarks>
    private static int RootBraceIndex(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        try
        {
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                return CharIndex(utf8, (int)reader.TokenStartIndex);
        }
        catch (JsonException)
        {
        }

        return -1;
    }

    /// <summary>Character ranges of every JSONC comment, so trivia scans can step over them.</summary>
    private static List<(int Start, int End)> CommentSpans(string text)
    {
        var spans = new List<(int, int)>();
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                    spans.Add((CharIndex(utf8, (int)reader.TokenStartIndex), CharIndex(utf8, (int)reader.BytesConsumed)));
            }
        }
        catch (JsonException)
        {
        }

        return spans;
    }

    /// <summary>Advances past whitespace and JSONC comments.</summary>
    private static int SkipTriviaForward(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    int line = text.IndexOf('\n', index);
                    index = line < 0 ? text.Length : line + 1;
                    continue;
                }

                if (text[index + 1] == '*')
                {
                    int block = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = block < 0 ? text.Length : block + 2;
                    continue;
                }
            }

            break;
        }

        return index;
    }

    /// <summary>Steps back past whitespace and any comment ending at the current position.</summary>
    private static int SkipTriviaBackward(string text, int index, List<(int Start, int End)> comments)
    {
        while (index > 0)
        {
            if (char.IsWhiteSpace(text[index - 1]))
            {
                index--;
                continue;
            }

            int commentStart = -1;
            foreach ((int start, int end) in comments)
            {
                if (end == index)
                {
                    commentStart = start;
                    break;
                }
            }

            if (commentStart < 0)
                break;

            index = commentStart;
        }

        return index;
    }

    /// <summary>
    /// True when a spliced document still parses. This is the backstop for the whole text-splicing
    /// approach: any offset mistake becomes a refusal instead of a destroyed configuration file.
    /// </summary>
    private static bool IsWellFormed(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildFreshDocument(string containerProperty, string serverName, string serverJson)
        => "{\n  \"" + containerProperty + "\": {\n    \"" + serverName + "\": "
           + Reindent(serverJson, "    ") + "\n  }\n}\n";

    /// <summary>Re-indents a rendered JSON object so it sits correctly at its insertion depth.</summary>
    private static string Reindent(string json, string indent)
    {
        string[] lines = json.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                text.Append('\n').Append(indent);
            text.Append(lines[i]);
        }

        return text.ToString();
    }

    private static string IndentOf(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Min(index, text.Length - 1));
        if (lineStart < 0)
            return string.Empty;

        var indent = new StringBuilder();
        for (int i = lineStart + 1; i < index && (text[i] == ' ' || text[i] == '\t'); i++)
            indent.Append(text[i]);
        return indent.ToString();
    }

    /// <summary>UTF-8 byte offsets from the reader become character offsets in the source string.</summary>
    private static int CharIndex(byte[] utf8, int byteIndex)
        => Encoding.UTF8.GetCharCount(utf8, 0, Math.Clamp(byteIndex, 0, utf8.Length));

    // ── IO ──

    private static string? ReadIfExists(string path, out DateTime lastWriteUtc, out long length)
    {
        lastWriteUtc = default;
        length = -1;

        if (!File.Exists(path))
            return null;

        var info = new FileInfo(path);
        lastWriteUtc = info.LastWriteTimeUtc;
        length = info.Length;
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>
    /// Writes atomically, and only if the file still looks the way it did when we read it.
    /// </summary>
    /// <remarks>
    /// An editor with the file open can save between our read and our write. Checking the timestamp and
    /// length first turns "silently discard the user's edit" into "refuse and say so". A baseline of
    /// <c>-1</c> means the file did not exist when it was read, which is a stricter contract, not a
    /// weaker one: the write must CREATE the file, so a file that appeared in the meantime (another
    /// Yagu, or the client itself writing its first configuration) is refused rather than replaced.
    /// <para>
    /// Internal so a test can supply a baseline that no longer matches. Reproducing that by racing a real
    /// editor would be timing-dependent, and this is the guard that stands between a concurrent save and
    /// a silently discarded one.
    /// </para>
    /// </remarks>
    internal static McpRegistrationResult Write(
        string path, string content, DateTime expectedWriteUtc, long expectedLength, McpRegistrationOutcome outcome)
        => Write(
            path,
            content,
            expectedWriteUtc,
            expectedLength,
            outcome,
            afterTempWritten: null,
            File.Replace,
            File.Move,
            Thread.Sleep);

    /// <remarks>
    /// Takes the replace/move/sleep operations and an after-temp-write hook so tests can deterministically
    /// hit the second identity check, retry loop, and cleanup path. Reproducing those with real editors,
    /// antivirus scanners, or concurrent MCP registrations would be timing-dependent.
    /// </remarks>
    internal static McpRegistrationResult Write(
        string path,
        string content,
        DateTime expectedWriteUtc,
        long expectedLength,
        McpRegistrationOutcome outcome,
        Action? afterTempWritten,
        FileReplaceOperation replaceFile,
        FileMoveOperation moveFile,
        Action<int> sleep)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        bool expectedToExist = expectedLength >= 0;
        bool existsNow = File.Exists(path);

        if (expectedToExist && existsNow)
        {
            var current = new FileInfo(path);
            if (current.Length != expectedLength || current.LastWriteTimeUtc != expectedWriteUtc)
            {
                return new McpRegistrationResult(
                    McpRegistrationOutcome.Failed,
                    "the configuration file changed while it was being edited; nothing was written. Try again.");
            }
        }
        else if (expectedToExist != existsNow)
        {
            return new McpRegistrationResult(
                McpRegistrationOutcome.Failed,
                existsNow
                    ? "the configuration file was created while it was being edited; nothing was written. Try again."
                    : "the configuration file was removed while it was being edited; nothing was written. Try again.");
        }

        // Unique and created exclusively. A fixed "<config>.yagu-tmp" is a name both an attacker and a
        // SECOND CONCURRENT REGISTRATION can predict: two clients being registered at once would write
        // the same temp path, so one would swap the other's content into place and both would report
        // success for a configuration only one of them produced. CreateNew also refuses to follow a link
        // planted at that name.
        string temp = path + $".{Guid.NewGuid():n}{TempSuffix}";

        // A previous run killed mid-write leaves one of these behind next to the user's own
        // configuration. They are unique now, so sweep whatever earlier runs abandoned.
        RemoveAbandonedTempFiles(path);

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
            }
            afterTempWritten?.Invoke();

            if (expectedToExist)
            {
                // Re-checked immediately before the swap. The check above is separated from the replace
                // by a whole file write, which is ample time for an editor to save over the file, and
                // File.Replace is unconditional.
                //
                // The replace is retried because a scanner or indexer holding the destination open makes
                // it throw "Unable to remove the file to be replaced" — measured at roughly 1.5% of
                // replaces on an ordinary Windows box. Reporting that as "could not update your client"
                // would blame the user's configuration for someone else's file handle. Identity is
                // re-verified before every attempt, so retrying never widens the race it guards.
                const int replaceAttempts = 5;
                for (int attempt = 1; ; attempt++)
                {
                    var current = new FileInfo(path);
                    if (!current.Exists || current.Length != expectedLength || current.LastWriteTimeUtc != expectedWriteUtc)
                    {
                        try { File.Delete(temp); } catch (IOException) { }
                        return new McpRegistrationResult(
                            McpRegistrationOutcome.Failed,
                            "the configuration file changed while it was being edited; nothing was written. Try again.");
                    }

                    try
                    {
                        // A backup, not null: the splice is validated for well-formedness but not for
                        // meaning, and this is a file the user may have hand-tuned over a long time.
                        // File.Replace produces it for free and it is the only recovery path there is.
                        replaceFile(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);
                        break;
                    }
                    catch (IOException) when (attempt < replaceAttempts)
                    {
                        sleep(attempt * 20);
                    }
                }
            }
            else
            {
                moveFile(temp, path, overwrite: false);
            }
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }

        return new McpRegistrationResult(outcome);
    }

    /// <summary>
    /// Deletes temp files an earlier interrupted write left beside <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Unique temp names mean a killed run no longer leaves a single well-known file the next run
    /// overwrites, so they have to be swept explicitly or they accumulate next to the user's
    /// configuration. A file still held open by a live concurrent write cannot be deleted, which is
    /// exactly the one this must not touch.
    /// <para>
    /// The pattern has no dot before the wildcard so it also matches the pre-unique-name
    /// <c>&lt;config&gt;.yagu-tmp</c>: a leftover written by an older build would otherwise never be
    /// swept by any later one, and sit beside the user's configuration forever.
    /// </para>
    /// </remarks>
    private static void RemoveAbandonedTempFiles(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                return;

            foreach (string stale in Directory.EnumerateFiles(
                         directory, Path.GetFileName(path) + "*" + TempSuffix))
            {
                try { File.Delete(stale); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Leftovers are untidy, not harmful; never fail a registration over them.
        }
    }
}
