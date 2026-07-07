using System.Text.Json.Serialization;
using System.Text.Json;

namespace Yagu.Models;

/// <summary>
/// Structured translation of a natural-language search request into the subset of
/// <see cref="SearchOptions"/> fields that Yagu can map. Produced by an
/// <see cref="Services.Ai.ISemanticQueryTranslator"/> (the local model emits this as JSON)
/// and applied to the UI view-model or a CLI <see cref="SearchOptions"/> by
/// <see cref="Services.Ai.SemanticPlanApplier"/>.
///
/// Every field is nullable so that "unspecified" is distinct from "explicitly set". The
/// applier only overrides a baseline value when the corresponding field is non-null.
/// </summary>
public sealed class SemanticSearchPlan
{
    /// <summary>Directory to search, e.g. <c>C:\</c>. Drive shorthands like "C drive" are
    /// resolved to a rooted path by the applier when the model does not.</summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

    /// <summary>The literal/regex term to match. May be empty when the intent is purely a
    /// filename/metadata filter (e.g. "all png files").</summary>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    /// <summary>One of <see cref="SearchMode"/>: <c>both</c>, <c>content</c>,
    /// <c>filenames</c>, <c>filename-then-content</c>.</summary>
    [JsonPropertyName("searchMode")]
    public string? SearchMode { get; init; }

    [JsonPropertyName("caseSensitive")]
    public bool? CaseSensitive { get; init; }

    [JsonPropertyName("useRegex")]
    public bool? UseRegex { get; init; }

    /// <summary>Whole-word/whole-query exact match. When false, the query is split on
    /// whitespace and any term may match.</summary>
    [JsonPropertyName("exactMatch")]
    public bool? ExactMatch { get; init; }

    /// <summary>Cross-line ("multiline", ripgrep -U) matching: run the pattern over the WHOLE file so a
    /// single match can span line breaks. Set <c>true</c> when the request needs ONE match to cover text
    /// on DIFFERENT lines — e.g. "X on one line then Y on a later line", "a block from BEGIN to END",
    /// "spanning multiple lines". Requires a regex <see cref="Pattern"/> that crosses lines (e.g.
    /// <c>foo[\s\S]*?bar</c>); the applier turns <see cref="UseRegex"/> on. Null = single-line (default).
    /// Do NOT set for ordinary same-line searches.</summary>
    [JsonPropertyName("multiline")]
    public bool? Multiline { get; init; }

    /// <summary>Only meaningful with <see cref="Multiline"/>: make <c>.</c> also match newlines (dot-all /
    /// ripgrep --multiline-dotall / inline (?s)) so <c>.*</c> crosses lines. Null = the dot stops at a
    /// line break.</summary>
    [JsonPropertyName("multilineDotAll")]
    public bool? MultilineDotAll { get; init; }

    /// <summary>Include filters — extensions or globs, e.g. <c>["*.png"]</c> or
    /// <c>["png","jpg"]</c>.</summary>
    [JsonPropertyName("includeGlobs")]
    [JsonConverter(typeof(TolerantStringListConverter))]
    public List<string>? IncludeGlobs { get; init; }

    /// <summary>Exclude filters — extensions or globs, e.g. <c>["*.mov"]</c>.</summary>
    [JsonPropertyName("excludeGlobs")]
    [JsonConverter(typeof(TolerantStringListConverter))]
    public List<string>? ExcludeGlobs { get; init; }

    /// <summary>Bare file names (without extension) to exclude, e.g. <c>["abc"]</c>. The
    /// applier converts these into exclude globs like <c>abc.*</c> / <c>*abc*</c>.</summary>
    [JsonPropertyName("excludeFileNames")]
    [JsonConverter(typeof(TolerantStringListConverter))]
    public List<string>? ExcludeFileNames { get; init; }

    [JsonPropertyName("minFileSizeBytes")]
    public long? MinFileSizeBytes { get; init; }

    [JsonPropertyName("maxFileSizeBytes")]
    public long? MaxFileSizeBytes { get; init; }

    /// <summary>ISO-8601 date (yyyy-MM-dd) — only files created on/after are included.</summary>
    [JsonPropertyName("createdAfter")]
    public string? CreatedAfter { get; init; }

    [JsonPropertyName("createdBefore")]
    public string? CreatedBefore { get; init; }

    /// <summary>ISO-8601 date (yyyy-MM-dd) — only files modified on/after are included.</summary>
    [JsonPropertyName("modifiedAfter")]
    public string? ModifiedAfter { get; init; }

    [JsonPropertyName("modifiedBefore")]
    public string? ModifiedBefore { get; init; }

    /// <summary>Maximum recursion depth. 0 = unlimited.</summary>
    [JsonPropertyName("maxSearchDepth")]
    public int? MaxSearchDepth { get; init; }

    [JsonPropertyName("obeyGitignore")]
    public bool? ObeyGitignore { get; init; }

    [JsonPropertyName("searchInsideArchives")]
    public bool? SearchInsideArchives { get; init; }

    /// <summary>Whether to include files/folders carrying the Windows Hidden attribute. <c>true</c> =
    /// include hidden items ("hidden files"), <c>false</c> = exclude them ("not hidden" / "no hidden
    /// files"). Null = leave the user's current toggle. Maps to the Advanced Options "Search hidden
    /// files" toggle — NOT to an exclude glob.</summary>
    [JsonPropertyName("searchHidden")]
    public bool? SearchHidden { get; init; }

    /// <summary>Whether to read text inside image files (OCR). Set <c>true</c> when the request is to
    /// find text WITHIN images — e.g. "png files with the word CUDA in it", "screenshots mentioning
    /// invoice". Maps to the Advanced Options "Search image text (OCR)" toggle. Null = leave the
    /// current toggle; the applier also enables this automatically when a content search targets image
    /// extensions.</summary>
    [JsonPropertyName("searchImageText")]
    public bool? SearchImageText { get; init; }

    /// <summary>Field to sort the results by, e.g. <c>name</c>, <c>size</c>, <c>date</c>/<c>modified</c>,
    /// <c>relevance</c>/<c>matches</c>, or <c>directory</c>/<c>path</c>. Null = leave the current sort.</summary>
    [JsonPropertyName("sortBy")]
    public string? SortBy { get; init; }

    /// <summary>Sort direction, e.g. <c>asc</c>/<c>ascending</c> or <c>desc</c>/<c>descending</c>. When a
    /// <see cref="SortBy"/> field is given without a direction, the applier defaults to descending.</summary>
    [JsonPropertyName("sortDirection")]
    public string? SortDirection { get; init; }

    /// <summary>Field to group the results by, e.g. <c>directory</c>/<c>folder</c>, <c>extension</c>/<c>type</c>,
    /// <c>size</c>, <c>modified</c>/<c>date</c>, <c>created</c>, or <c>none</c>. Null = leave the current grouping.</summary>
    [JsonPropertyName("groupBy")]
    public string? GroupBy { get; init; }

    /// <summary>Optional direction for the group order, e.g. <c>asc</c>/<c>a-z</c>/<c>recent</c> or
    /// <c>desc</c>/<c>z-a</c>/<c>older</c>. Defaults to the natural order (A-Z / recent-first / small-first).</summary>
    [JsonPropertyName("groupDirection")]
    public string? GroupDirection { get; init; }

    /// <summary>Short human-readable summary of how the request was interpreted. Surfaced to
    /// the user so the mapping is transparent. Not applied to the search.</summary>
    [JsonPropertyName("explanation")]
    public string? Explanation { get; init; }
}

/// <summary>
/// Deserializes a JSON string-list field tolerantly, so one malformed field from a small on-device
/// model never fails the WHOLE plan parse. Accepts a proper array (<c>["a","b"]</c>), a SINGLE bare
/// string (<c>"a"</c> -&gt; <c>["a"]</c>) — a common small-model mistake for a list field — and returns
/// <c>null</c> for any other shape (bool/number/object/null) or an empty result. Non-string array
/// elements are skipped. Writing always emits a normal string array.
/// </summary>
internal sealed class TolerantStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
            {
                string? single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? null : [single];
            }

            case JsonTokenType.StartArray:
            {
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        string? s = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                    else
                    {
                        reader.Skip(); // ignore a non-string element instead of failing the parse
                    }
                }
                return list.Count > 0 ? list : null;
            }

            default:
                // A shape we cannot use for a string list (bool/number/object). Consume it and treat the
                // field as absent rather than throwing, so it does not nuke the whole plan.
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (string s in value)
            writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Source-generated <see cref="System.Text.Json"/> context for <see cref="SemanticSearchPlan"/>.
/// Tolerant of unknown members and case so partial/loosely-formatted model output still binds.
/// Keeping this separate from <c>AppSettingsJsonContext</c> avoids coupling persisted settings
/// to model-output deserialization rules.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SemanticSearchPlan))]
public sealed partial class SemanticSearchPlanJsonContext : JsonSerializerContext
{
}
