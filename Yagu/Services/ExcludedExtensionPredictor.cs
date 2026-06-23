using System;
using System.Collections.Generic;
using System.Linq;
using Yagu.Models;

namespace Yagu.Services;

/// <summary>The advanced-options source(s) that would hide a file with a given extension.</summary>
[Flags]
internal enum ExtensionExclusionReason
{
    None = 0,
    /// <summary>The extension is in the active Skip Extensions list.</summary>
    SkipExtensions = 1,
    /// <summary>The extension is in the active Binary Extensions list.</summary>
    BinaryExtensions = 2,
    /// <summary>An Exclude filter (glob) removes files with this extension.</summary>
    ExcludeFilter = 4,
    /// <summary>A restrictive Include filter omits this extension (only other extensions are included).</summary>
    IncludeFilter = 8,
}

/// <summary>The predicted extension a user searched for and why it would be excluded from results.</summary>
internal sealed record ExcludedExtensionWarning(string Extension, ExtensionExclusionReason Reasons);

/// <summary>
/// Pure, dependency-free prediction of the "you searched for a file whose extension is currently
/// excluded" case. It deliberately collapses prediction and the exclusion check into one rule: the
/// trailing extension token of the query is only flagged when it is actually present in (or omitted
/// by) one of the active advanced-option filters. That keeps false positives near zero — a query like
/// <c>report.2024</c> yields no warning because no filter excludes <c>2024</c>.
/// </summary>
internal static class ExcludedExtensionPredictor
{
    /// <summary>
    /// Returns the offending extension and the filter(s) excluding it, or <c>null</c> when the query
    /// does not name a file whose extension is currently excluded. Returns null in regex mode (where a
    /// literal extension cannot be detected) and in Content-only mode (where file names are not matched).
    /// </summary>
    /// <param name="query">The raw search query text.</param>
    /// <param name="useRegex">True when the query is a regular expression (extension detection is skipped).</param>
    /// <param name="exactMatch">True treats the whole query as one term; false splits on whitespace.</param>
    /// <param name="searchMode">The active search mode; Content-only is not checked.</param>
    /// <param name="skipExtensions">Active Skip Extensions (dot-less, e.g. "tmp").</param>
    /// <param name="binaryExtensions">Active Binary Extensions (dot-less, e.g. "exe").</param>
    /// <param name="includeGlobs">Raw Include filter text.</param>
    /// <param name="includeMode">Include filter interpretation mode.</param>
    /// <param name="excludeGlobs">Raw Exclude filter text.</param>
    /// <param name="excludeMode">Exclude filter interpretation mode.</param>
    internal static ExcludedExtensionWarning? Predict(
        string? query,
        bool useRegex,
        bool exactMatch,
        SearchMode searchMode,
        IReadOnlySet<string> skipExtensions,
        IReadOnlySet<string> binaryExtensions,
        string includeGlobs,
        FilterPatternMode includeMode,
        string excludeGlobs,
        FilterPatternMode excludeMode)
    {
        // Regex queries: "." is a wildcard, so a trailing ".exe" is not a reliable file extension.
        if (useRegex) return null;
        // Content-only mode never matches file names, so an excluded extension is not the surprise here.
        if (searchMode == SearchMode.Content) return null;

        // Glob filters only contribute extension semantics in GlobPath mode; regex filters are opaque.
        var excludeExts = excludeMode == FilterPatternMode.GlobPath
            ? ExtractFilterExtensions(excludeGlobs)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeExts = includeMode == FilterPatternMode.GlobPath
            ? ExtractFilterExtensions(includeGlobs)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = ExtractCandidateExtensions(query, exactMatch);
        // The Include filter's own target extensions are search targets too: restricting results to
        // *.exe while .exe is skip/binary-excluded silently finds nothing. This is the key signal for
        // Semantic searches (the model resolves "exe files" into the include glob *.exe) and for
        // self-contradictory manual filters.
        foreach (var inc in includeExts)
            if (!candidates.Contains(inc))
                candidates.Add(inc);

        if (candidates.Count == 0) return null;

        foreach (var ext in candidates)
        {
            var reasons = ExtensionExclusionReason.None;
            if (binaryExtensions.Contains(ext)) reasons |= ExtensionExclusionReason.BinaryExtensions;
            if (skipExtensions.Contains(ext)) reasons |= ExtensionExclusionReason.SkipExtensions;
            if (excludeExts.Contains(ext)) reasons |= ExtensionExclusionReason.ExcludeFilter;
            if (includeExts.Count > 0 && !includeExts.Contains(ext)) reasons |= ExtensionExclusionReason.IncludeFilter;

            if (reasons != ExtensionExclusionReason.None)
                return new ExcludedExtensionWarning(ext, reasons);
        }

        return null;
    }

    /// <summary>
    /// Extracts the dot-less, lower-cased trailing extension of each query term that looks like a file
    /// name. With <paramref name="exactMatch"/> the whole query is one term; otherwise it is split on
    /// whitespace (matching the search engine's any-term behavior). Surrounding quotes are stripped and
    /// the extension must be 1–16 alphanumeric characters (so "v1.2.3" yields "3", "notes.txt" yields "txt").
    /// </summary>
    internal static List<string> ExtractCandidateExtensions(string? query, bool exactMatch)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        string trimmed = query.Trim();
        IEnumerable<string> terms = exactMatch
            ? new[] { trimmed }
            : trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawTerm in terms)
        {
            string term = rawTerm.Trim().Trim('"', '\'');
            int dot = term.LastIndexOf('.');
            if (dot <= 0 || dot == term.Length - 1) continue; // no name before the dot, or trailing dot

            string ext = term[(dot + 1)..];
            if (ext.Length > 16) continue; // ext is always >= 1 char here (dot is not the last char)
            if (!ext.All(char.IsLetterOrDigit)) continue;

            string normalized = ext.ToLowerInvariant();
            if (seen.Add(normalized)) result.Add(normalized);
        }

        return result;
    }

    /// <summary>
    /// Extracts the dot-less, lower-cased extensions named by a glob filter string, mirroring
    /// <c>GlobMatcher</c>'s extension-pattern detection: a bare alphanumeric token of 1–5 chars
    /// (e.g. "ts", "json") and the <c>*.ext</c> form both denote an extension. Path-segment, wildcard,
    /// and regex tokens contribute nothing, so a folder include like "src" never triggers a warning.
    /// </summary>
    internal static HashSet<string> ExtractFilterExtensions(string? globText)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(globText)) return set;

        foreach (var token in globText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "*.ext" → extension (any length, no further wildcard/path chars after the dot)
            if (token.StartsWith("*.", StringComparison.Ordinal))
            {
                string rest = token[2..];
                if (rest.Length > 0 && rest.All(char.IsLetterOrDigit))
                    set.Add(rest.ToLowerInvariant());
                continue;
            }

            // bare alphanumeric token ≤5 chars → extension (matches GlobMatcher.BuildGlob)
            if (token.Length <= 5 && token.All(char.IsLetterOrDigit))
                set.Add(token.ToLowerInvariant());
        }

        return set;
    }
    /// <summary>Removes any token denoting <paramref name="ext"/> (e.g. "exe", ".exe", "*.exe") from a
    /// semicolon/comma-separated extension or glob string, preserving the remaining tokens' order.</summary>
    internal static string RemoveExtensionToken(string? list, string ext)
    {
        if (string.IsNullOrWhiteSpace(list)) return list ?? string.Empty;

        var kept = list
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.Equals(token.TrimStart('.', '*'), ext, StringComparison.OrdinalIgnoreCase));

        return string.Join(';', kept);
    }

    /// <summary>Appends a <c>*.ext</c> include token to a glob list unless a token for that extension is
    /// already present.</summary>
    internal static string AppendExtensionToken(string? list, string ext)
    {
        var existing = (list ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (existing.Any(t => string.Equals(t.TrimStart('.', '*'), ext, StringComparison.OrdinalIgnoreCase)))
            return string.Join(';', existing);

        existing.Add("*." + ext);
        return string.Join(';', existing);
    }
}
