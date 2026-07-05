using System.Text.RegularExpressions;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services.Ocr;

/// <summary>
/// Searches OCR-recognized text for query matches and projects them into <see cref="SearchResult"/>
/// rows that display under the image's path — making an OCR'd image behave like a text file whose
/// content is its recognized text. Reuses <see cref="ContentSearcher.FindMatches"/> so OCR matches
/// are found exactly the way file-content matches are.
/// </summary>
public static class OcrTextMatcher
{
    /// <summary>
    /// Returns one <see cref="SearchResult"/> per match found in <paramref name="text"/>, attributed to
    /// <paramref name="displayPath"/> (the image file). Line numbers are 1-based into the OCR text.
    /// </summary>
    public static List<SearchResult> Match(
        string displayPath,
        string? text,
        Regex? regex,
        string? literal,
        StringComparison comparison,
        int contextLines,
        int maxMatches)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(text)) return results;

        string[] lines = SplitLines(text);
        for (int i = 0; i < lines.Length; i++)
        {
            var matches = ContentSearcher.FindMatches(lines[i], regex, literal, comparison);
            if (matches.Count == 0) continue;

            IReadOnlyList<string> before = BuildContext(lines, i, contextLines, after: false);
            foreach (var (start, length) in matches)
            {
                LineTruncator.DisplayLine display = LineTruncator.TruncateAroundMatch(lines[i], start, length);
                IReadOnlyList<string> after = contextLines > 0
                    ? BuildContext(lines, i, contextLines, after: true)
                    : Array.Empty<string>();

                results.Add(new SearchResult(
                    FilePath: displayPath,
                    LineNumber: i + 1,
                    MatchLine: display.Text,
                    MatchStartColumn: display.MatchStart,
                    MatchLength: length,
                    ContextBefore: before,
                    ContextAfter: after)
                {
                    SourceMatchStartColumn = start,
                });

                if (maxMatches > 0 && results.Count >= maxMatches) return results;
            }
        }

        return results;
    }

    private static IReadOnlyList<string> BuildContext(string[] lines, int index, int contextLines, bool after)
    {
        if (contextLines <= 0) return Array.Empty<string>();
        var list = new List<string>(contextLines);
        if (after)
        {
            for (int j = index + 1; j <= index + contextLines && j < lines.Length; j++)
                list.Add(LineTruncator.Truncate(lines[j]));
        }
        else
        {
            int from = Math.Max(0, index - contextLines);
            for (int j = from; j < index; j++)
                list.Add(LineTruncator.Truncate(lines[j]));
        }
        return list.Count == 0 ? Array.Empty<string>() : list;
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
