using System;
using System.Text.RegularExpressions;
using Yagu.Models;

namespace Yagu.Helpers;

/// <summary>
/// Pure, WinUI-free arithmetic and label formatting for preview match navigation.
/// Extracted from <see cref="MainWindow"/>'s <c>MainWindow.MatchNav.cs</c> so the branch-heavy
/// occurrence math (index clamping, wrap-around, stable file counting, and the
/// "Occurrence X/N" labels) can be exhaustively unit-tested. The WinUI method bodies keep the
/// side effects (field mutation, control updates) and delegate the math here.
/// </summary>
internal static class MatchNavMath
{
    internal static int CountHighlightOccurrences(IEnumerable<SearchResult> results, Regex? regex)
        => CountHighlightOccurrencesCore(results, regex is null ? null : regex.Count);

    internal static int CountHighlightOccurrencesCore(IEnumerable<SearchResult> results, Func<string, int>? countMatches)
    {
        int total = 0;
        foreach (var file in results.Where(result => result.LineNumber > 0)
                     .GroupBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var line in file.GroupBy(result => result.LineNumber))
            {
                int known = line.Select(result => (
                    result.SourceMatchStartColumn, result.MatchLength,
                    result.MatchEndLineNumber, result.MatchEndColumn)).Distinct().Count();
                int visible = 0;
                var windows = new HashSet<(int SourceStart, int Length)>();
                foreach (var result in line)
                {
                    if (countMatches is not null && !string.IsNullOrEmpty(result.MatchLine)
                        && windows.Add((result.SourceMatchStartColumn - result.MatchStartColumn, result.MatchLine.Length)))
                        visible = Math.Max(visible, countMatches(result.MatchLine));
                }
                total += Math.Max(known, visible);
            }
        }
        return total;
    }

    /// <summary>
    /// Formats the global match-navigation label
    /// <c>"Occurrence {index+1}/{totalMatches} ({fileCount} file[s])"</c>.
    /// The file word pluralizes unless exactly one file contributes matches.
    /// </summary>
    public static string FormatOccurrenceLabel(int index, int totalMatches, int fileCount)
        => $"Occurrence {index + 1:N0}/{totalMatches:N0} ({fileCount:N0} file{(fileCount != 1 ? "s" : "")})";

    /// <summary>
    /// Formats the per-section overlay label
    /// <c>"Occurrence {displayIndex}/{total} ({total} match[es] in file)"</c>. The display index is
    /// clamped into <c>[1, total]</c> and the match word pluralizes unless the section has one match.
    /// </summary>
    public static string FormatSectionOccurrenceLabel(int currentIndex, int total)
    {
        int displayIndex = Math.Clamp(currentIndex + 1, 1, total);
        string matchWord = total == 1 ? "match" : "matches";
        return $"Occurrence {displayIndex:N0}/{total:N0} ({total:N0} {matchWord} in file)";
    }

    /// <summary>
    /// File count used by the global nav label: the registered preview file total when it is known
    /// (&gt; 0), otherwise the count of files already rendered plus any deferred (not-yet-inserted) files.
    /// </summary>
    public static int StableFileCount(int previewTotalFileCount, int renderedFileCount, int deferredFiles)
        => previewTotalFileCount > 0 ? previewTotalFileCount : renderedFileCount + deferredFiles;

    /// <summary>
    /// Resolves the global current-match index after a preview refresh. An active (boxed) match wins;
    /// otherwise the previous index is clamped into the rendered range, defaulting to the first match.
    /// With no matches rendered, a negative index resets to the first slot and any other index is kept
    /// unchanged (matching the original fall-through behavior).
    /// </summary>
    public static int ResolveCurrentIndex(int activeIndex, int currentIndex, int matchCount)
    {
        if (activeIndex >= 0)
            return activeIndex;

        if (matchCount > 0)
        {
            if (currentIndex < 0)
                return 0;
            if (currentIndex >= matchCount)
                return matchCount - 1;
            return currentIndex;
        }

        return currentIndex < 0 ? 0 : currentIndex;
    }

    /// <summary>
    /// Computes the previous occurrence index with wrap-around to the end. <paramref name="count"/>
    /// must be positive (callers guard the empty case). Also reports whether the step wrapped past the
    /// start of the list (i.e. the current index was already at or before the first match).
    /// </summary>
    public static (int Index, bool WrappedToEnd) PrevIndexWithWrap(int currentIndex, int count)
    {
        bool wrappedToEnd = currentIndex <= 0;
        int index = (currentIndex - 1 + count) % count;
        return (index, wrappedToEnd);
    }
}
